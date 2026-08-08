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

        static LLMClient()
        {
            // RimWorld's Mono runtime negotiates an older TLS version by default on some platforms,
            // which most providers reject with an opaque handshake error.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (Exception e) { Log.Warning($"[Firefly] Could not enable TLS 1.2: {e.Message}"); }
        }

        public static void Send(
            string systemPrompt,
            string userPrompt,
            Action<string> onSuccess,
            Action<string> onError)
        {
            // Single choke point for every narrative request — max_tokens caps the reply, not the
            // prompt, and a raid-heavy day produces a log far larger than most context windows.
            var settings = FireflyMod.Settings;
            userPrompt = TruncateForPrompt(userPrompt, settings.MaxPromptChars);

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
            Task.Run(async () => await SendAsync(messages, apiKey, baseUrl, model, onSuccess, onError));
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
                messages,
                apiKey, baseUrl, model,
                response => onResult(true, response.Trim()),
                error => onResult(false, error)
            ));
        }

        // Keeps the head (day header, character roster) and the tail (latest events, health
        // section) — the two ends that carry the most narrative weight.
        public static string TruncateForPrompt(string text, int maxChars)
        {
            if (text == null || maxChars <= 0 || text.Length <= maxChars) return text;

            const string marker = "\n\n[... middle of the day's log omitted to fit the context window ...]\n\n";
            int budget = maxChars - marker.Length;
            if (budget <= 0) return text.Substring(0, maxChars);

            int head = (int)(budget * 0.4f);
            int tail = budget - head;
            string result = text.Substring(0, head) + marker + text.Substring(text.Length - tail);
            Log.Message($"[Firefly] Prompt truncated {text.Length} → {result.Length} chars (limit {maxChars}).");
            return result;
        }

        private static async Task SendAsync(
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
            const int maxAttempts = 3;

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

                    var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json")
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    request.Headers.Add("HTTP-Referer", "https://github.com/StixxyTape/Firefly");
                    request.Headers.Add("X-Title", "Firefly");

                    var response = await Http.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        int code = (int)response.StatusCode;
                        lastError = $"HTTP {code}: {body}";
                        Log.Warning($"[Firefly] LLM attempt {attempt}/{maxAttempts} failed: {lastError}");
                        // Bad key, bad model, bad request — retrying changes nothing. Only 429 is worth another try.
                        if (code >= 400 && code < 500 && code != 429) break;
                        continue;
                    }

                    var content = JObject.Parse(body)?["choices"]?[0]?["message"]?["content"]?.Value<string>();
                    if (content == null)
                    {
                        lastError = "Malformed response — no content field in reply.";
                        Log.Warning($"[Firefly] LLM attempt {attempt}/{maxAttempts} failed: {lastError}");
                        continue;
                    }

                    MainThreadQueue.Enqueue(() => onSuccess(content));
                    return;
                }
                catch (TaskCanceledException)
                {
                    lastError = "Timed out after 60s.";
                    Log.Warning($"[Firefly] LLM attempt {attempt}/{maxAttempts} failed: {lastError}");
                }
                catch (Exception e)
                {
                    lastError = e.Message;
                    Log.Warning($"[Firefly] LLM attempt {attempt}/{maxAttempts} failed: {lastError}");
                }
            }

            MainThreadQueue.Enqueue(() => onError($"Request failed. Last error: {lastError}"));
        }
    }
}
