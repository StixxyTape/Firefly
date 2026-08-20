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
        // TEMPORARY kill switch — flip back to true to disable again. While true, Send() never makes
        // a request (onError fires immediately instead); TestConnection is left working since
        // that's an explicit, deliberate user action, not part of the narrative pipeline.
        public const bool CallsDisabled = false;

        // The client-level Timeout is a hard ceiling every request is bound by no matter what —
        // .NET links it with any per-request CancellationToken and whichever fires first wins, so
        // it must be at least as long as the longest per-call timeoutSeconds anyone passes to
        // Send() (currently the world-seed call's 180s). Each individual call still gets its own
        // shorter effective timeout via the CancellationTokenSource created per attempt in
        // SendAsync — this ceiling is not itself the default.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(240) };

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
                int next = count + delta;
                if (next > 0) _pendingByLabel[label] = next;
                else _pendingByLabel.Remove(label);
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
        // timeoutSeconds: 0 (the default) means "use the player's Request timeout setting". Every
        // call site defers to it — none of them pass an explicit override anymore.
        //
        // onSuccess returns whether the content was actually valid and accepted (true) or should
        // be treated as a failed attempt and retried (false) — see the retry-on-invalid-content
        // comment in SendAsync. Every call site's success handler must return this honestly rather
        // than always returning true, or a malformed reply silently gets treated as accepted.
        public static void Send(
            string label,
            string systemPrompt,
            string userPrompt,
            Func<string, bool> onSuccess,
            Action<string> onError,
            int timeoutSeconds = 0)
        {
            if (CallsDisabled)
            {
                Log.Message($"[Firefly:{label}] LLM calls are temporarily disabled — request skipped.");
                onError("LLM calls are temporarily disabled.");
                return;
            }

            // Single choke point for every narrative request — max_tokens caps the reply, not the
            // prompt, and a raid-heavy day produces a log far larger than most context windows.
            var settings = FireflyMod.Settings;
            if (timeoutSeconds <= 0) timeoutSeconds = settings.RequestTimeoutSeconds;
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
            int maxTokens  = settings.MaxCompletionTokens > 0
                ? settings.MaxCompletionTokens
                : FireflySettings.DefaultMaxCompletionTokens;
            // A per-label override always wins over the global default when present — even an
            // explicit "" (Model default) override, which is why this checks TryGetValue rather
            // than falling through on an empty string.
            string reasoningEffort = settings.PerLabelReasoningEffort != null
                    && settings.PerLabelReasoningEffort.TryGetValue(label, out var labelReasoningEffort)
                ? labelReasoningEffort ?? ""
                : settings.ReasoningEffort ?? "";
            int maxAttempts = Math.Max(1, settings.MaxRetries + 1);

            // Pending-count bookkeeping lives inside SendAsync itself now, not a wrapper here —
            // onSuccess can fire more than once per Send() call (once per retried attempt whose
            // content failed validation), and only the truly terminal attempt should decrement.
            System.Threading.Interlocked.Increment(ref _pendingCount);
            AdjustLabelPending(label, 1);
            Task.Run(async () => await SendAsync(
                label, messages, apiKey, baseUrl, model, maxTokens, reasoningEffort, timeoutSeconds, maxAttempts,
                onSuccess, onError));
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
                apiKey, baseUrl, model, FireflySettings.DefaultMaxCompletionTokens, "", 60, 1,
                response => { onResult(true, response.Trim()); return true; },
                error => onResult(false, error),
                trackPending: false
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
            int maxTokens,
            string reasoningEffort,
            int timeoutSeconds,
            int maxAttempts,
            Func<string, bool> onSuccess,
            Action<string> onError,
            bool trackPending = true)
        {
            if (apiKey.NullOrEmpty())
            {
                if (trackPending) { System.Threading.Interlocked.Decrement(ref _pendingCount); AdjustLabelPending(label, -1); }
                MainThreadQueue.Enqueue(() => onError("No API key set — configure one in Mod Settings."));
                return;
            }

            string lastError = null;
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

                    // OpenRouter's unified "reasoning" field — only sent when configured, so
                    // endpoints/models that don't recognize it (non-reasoning models, some
                    // OpenAI-compatible local servers) just never see the field at all.
                    var payloadObj = new JObject
                    {
                        ["model"]      = model,
                        ["messages"]   = JArray.FromObject(messagePayloads),
                        ["max_tokens"] = maxTokens
                    };
                    if (!reasoningEffort.NullOrEmpty())
                        payloadObj["reasoning"] = new JObject { ["effort"] = reasoningEffort };
                    var payload = payloadObj.ToString(Newtonsoft.Json.Formatting.None);

                    using (var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    })
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                        request.Headers.Add("HTTP-Referer", "https://github.com/StixxyTape/Firefly");
                        request.Headers.Add("X-Title", "Firefly");

                        // Per-attempt timeout, not the shared HttpClient's own (much larger)
                        // ceiling — lets each call site ask for a different effective timeout
                        // (e.g. the world-seed call's longer 180s) without affecting every other
                        // request that goes through this same static client.
                        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                        using (var response = await Http.SendAsync(request, cts.Token))
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

                            // onSuccess runs on the main thread (it touches game state) and reports
                            // back whether the content actually parsed/validated — a response that
                            // arrived fine over the network but failed the caller's own validation
                            // (bad JSON shape, an unknown key, whatever) is a failed attempt just
                            // like a timeout, and gets retried the same way instead of silently
                            // giving up on the first bad reply.
                            var accepted = new TaskCompletionSource<bool>();
                            MainThreadQueue.Enqueue(() =>
                            {
                                bool ok;
                                try { ok = onSuccess(content); }
                                catch (Exception e)
                                {
                                    Log.Warning($"[Firefly:{label}] onSuccess handler threw: {e.Message}");
                                    ok = false;
                                }
                                accepted.SetResult(ok);
                            });
                            if (await accepted.Task)
                            {
                                if (trackPending)
                                {
                                    System.Threading.Interlocked.Decrement(ref _pendingCount);
                                    AdjustLabelPending(label, -1);
                                }
                                return;
                            }

                            lastError = "Response received but failed validation.";
                            LogWarningOnMainThread($"[Firefly:{label}] Attempt {attempt}/{maxAttempts} response was invalid; retrying.");
                            continue;
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    lastError = $"Timed out after {timeoutSeconds}s.";
                    LogWarningOnMainThread($"[Firefly:{label}] Attempt {attempt}/{maxAttempts} failed: {lastError}");
                }
                catch (Exception e)
                {
                    lastError = Truncate(e.Message, maxErrorChars);
                    LogWarningOnMainThread($"[Firefly:{label}] Attempt {attempt}/{maxAttempts} failed: {lastError}");
                }
            }

            if (trackPending) { System.Threading.Interlocked.Decrement(ref _pendingCount); AdjustLabelPending(label, -1); }
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
