using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Verse;

namespace Firefly
{
    public class ChatMessage
    {
        public string Role;
        public string Content;

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    public static class LLMClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // Counts real narrative requests only (Send, not TestConnection) — drives the "Fillion is
        // writing" UI indicator and the quit-warning patches. Incremented synchronously on the main
        // thread right before the background task starts, decremented on the main thread right as
        // the queued callback fires — both sides run on the main thread, so no cross-thread race,
        // Interlocked here is just defensive.
        private static int _pendingCount;
        public static bool IsPending => System.Threading.Volatile.Read(ref _pendingCount) > 0;

        // Per-label pending counts — drives the nav-tab "which section is Fillion writing"
        // highlight, which needs to know not just "something's pending" but "is it one of
        // these specific labels". Same main-thread-only increment/decrement guarantee as
        // _pendingCount above; the lock is just defensive, not load-bearing.
        private static readonly Dictionary<string, int> _pendingByLabel = new Dictionary<string, int>();
        private static readonly object _pendingByLabelLock = new object();

        // Named distinctly from the IsPending property above — C# won't allow a property and a
        // method to share one name on the same type.
        public static bool IsPendingForAny(params string[] labels)
        {
            lock (_pendingByLabelLock)
            {
                foreach (var label in labels)
                    if (_pendingByLabel.TryGetValue(label, out int count) && count > 0)
                        return true;
            }
            return false;
        }

        private static void AdjustLabelPending(string label, int delta)
        {
            lock (_pendingByLabelLock)
            {
                _pendingByLabel.TryGetValue(label, out int count);
                _pendingByLabel[label] = count + delta;
            }
        }

        static LLMClient()
        {
            // RimWorld's Mono runtime negotiates an older TLS version by default on some platforms,
            // which most providers reject with an opaque handshake error.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception e) { Log.Warning($"[Firefly] Could not enable TLS 1.2: {e.Message}"); }
        }

        // label identifies the call in every log line it produces (both here and inside
        // SendAsync's own retry-attempt warnings) — e.g. "ThreadScan", "DailySummary".
        // Required, not optional: with several distinct call sites now sharing this one choke
        // point, a generic unlabelled log line is no longer usable for debugging.
        public static void Send(
            string label,
            string systemPrompt,
            string userPrompt,
            Action<string> onSuccess,
            Action<string> onError)
        {
            // Single choke point for every narrative request — max_tokens caps the reply, not the
            // prompt, and a raid-heavy day produces a log far larger than most context windows.
            var settings = FireflyMod.Settings;
            userPrompt = TruncateForPrompt(label, userPrompt, settings.MaxPromptChars);

            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt)
            };
            // Snapshot mutable settings before the background thread starts so mid-edit changes
            // don't produce a mixed-config request across retry attempts.
            string apiKey  = settings.ApiKey  ?? "";
            string baseUrl = settings.BaseUrl ?? "";
            string model   = settings.Model   ?? "";

            System.Threading.Interlocked.Increment(ref _pendingCount);
            AdjustLabelPending(label, 1);
            Task.Run(async () => await SendAsync(
                label, messages, apiKey, baseUrl, model,
                onSuccess: content => { System.Threading.Interlocked.Decrement(ref _pendingCount); AdjustLabelPending(label, -1); onSuccess(content); },
                onError:   err     => { System.Threading.Interlocked.Decrement(ref _pendingCount); AdjustLabelPending(label, -1); onError(err); }));
        }

        public static void TestConnection(Action<bool, string> onResult)
        {
            var settings = FireflyMod.Settings;
            string apiKey  = settings.ApiKey  ?? "";
            string baseUrl = settings.BaseUrl ?? "";
            string model   = settings.Model   ?? "";
            var messages = new List<ChatMessage>
            {
                new ChatMessage("user", "Reply with only the word: ok")
            };
            Task.Run(async () => await SendAsync(
                "TestConnection", messages,
                apiKey, baseUrl, model,
                response => onResult(true, response.Trim()),
                error => onResult(false, error)
            ));
        }

        // Keeps the head (day header, character roster) and the tail (latest events, health
        // section) — the two ends that carry the most narrative weight.
        public static string TruncateForPrompt(string label, string text, int maxChars)
        {
            if (text == null || maxChars <= 0 || text.Length <= maxChars) return text;

            const string marker = "\n\n[... middle of the day's log omitted to fit the context window ...]\n\n";
            int budget = maxChars - marker.Length;
            if (budget <= 0) return text.Substring(0, maxChars);

            int head = (int)(budget * 0.4f);
            int tail = budget - head;
            string result = text.Substring(0, head) + marker + text.Substring(text.Length - tail);
            Log.Message($"[Firefly:{label}] Prompt truncated {text.Length} → {result.Length} chars (limit {maxChars}).");
            return result;
        }

        private static async Task SendAsync(
            string label,
            List<ChatMessage> messages,
            string apiKey,
            string baseUrl,
            string model,
            Action<string> onSuccess,
            Action<string> onError)
        {
            if (apiKey.NullOrEmpty())
            {
                MainThreadQueue.Enqueue(() => onError("No API key set — configure one in Mod Settings."));
                return;
            }

            string lastError = null;
            const int maxAttempts = 4;
            const int maxErrorChars = 500;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (attempt > 1)
                    await Task.Delay(TimeSpan.FromSeconds(3 * (attempt - 1)));

                try
                {
                    var url = baseUrl.TrimEnd('/') + "/chat/completions";

                    var messagePayloads = new List<object>();
                    foreach (var m in messages)
                        messagePayloads.Add(new { role = m.Role, content = m.Content });

                    var payload = JsonConvert.SerializeObject(new
                    {
                        model      = model,
                        messages   = messagePayloads,
                        max_tokens = 2048
                    });

                    using (var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    })
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                        request.Headers.Add("HTTP-Referer", "https://github.com/StixxyTape/Firefly");
                        request.Headers.Add("X-Title", "Firefly");

                        using (var response = await Http.SendAsync(request))
                        {
                            var body = await response.Content.ReadAsStringAsync();

                            if (!response.IsSuccessStatusCode)
                            {
                                int code = (int)response.StatusCode;
                                lastError = $"HTTP {code}: {Truncate(body, maxErrorChars)}";
                                LogWarningOnMainThread($"[Firefly:{label}] Attempt {attempt}/{maxAttempts} failed: {lastError}");
                                // Bad key, bad model, bad request — retrying changes nothing. Only 429 is worth another try.
                                if (code >= 400 && code < 500 && code != 429) break;
                                continue;
                            }

                            var content = JObject.Parse(body)?["choices"]?[0]?["message"]?["content"]?.Value<string>();
                            if (content == null)
                            {
                                lastError = "Malformed response — no content field in reply.";
                                LogWarningOnMainThread($"[Firefly:{label}] Attempt {attempt}/{maxAttempts} failed: {lastError}");
                                continue;
                            }

                            MainThreadQueue.Enqueue(() => onSuccess(content));
                            return;
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    lastError = "Timed out after 60s.";
                    LogWarningOnMainThread($"[Firefly:{label}] Attempt {attempt}/{maxAttempts} failed: {lastError}");
                }
                catch (Exception e)
                {
                    lastError = Truncate(e.Message, maxErrorChars);
                    LogWarningOnMainThread($"[Firefly:{label}] Attempt {attempt}/{maxAttempts} failed: {lastError}");
                }
            }

            MainThreadQueue.Enqueue(() => onError($"Request failed. Last error: {lastError}"));
        }

        // SendAsync runs on a background Task.Run thread — RimWorld's Log.Warning/Message write
        // into a plain Queue<T> that the dev log window enumerates on the main thread whenever
        // it's open, so calling Log.Warning directly from here can race with that enumeration
        // and throw "Collection was modified" out of EditWindow_Log. Route through the same
        // MainThreadQueue everything else in this method already uses for callbacks.
        private static void LogWarningOnMainThread(string message) =>
            MainThreadQueue.Enqueue(() => Log.Warning(message));

        private static string Truncate(string text, int maxChars)
        {
            if (text == null || text.Length <= maxChars) return text;
            return text.Substring(0, maxChars) + "… (truncated)";
        }
    }
}
