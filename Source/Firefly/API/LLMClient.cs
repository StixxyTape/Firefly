using System;
using System.Collections.Generic;
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
        public static void Send(
            string systemPrompt,
            string userPrompt,
            Action<string> onSuccess,
            Action<string> onError)
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt)
            };
            Task.Run(async () => await SendAsync(messages, onSuccess, onError));
        }

        public static void TestConnection(Action<bool, string> onResult)
        {
            var messages = new List<ChatMessage>
            {
                new ChatMessage("user", "Reply with only the word: ok")
            };
            Task.Run(async () => await SendAsync(
                messages,
                response => onResult(true, response.Trim()),
                error => onResult(false, error)
            ));
        }

        private static async Task SendAsync(
            List<ChatMessage> messages,
            Action<string> onSuccess,
            Action<string> onError)
        {
            var settings = FireflyMod.Settings;

            if (settings.ApiKey.NullOrEmpty())
            {
                LongEventHandler.ExecuteWhenFinished(() => onError("No API key set — configure one in Mod Settings."));
                return;
            }

            string lastError = null;
            const int maxAttempts = 3;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (attempt > 1)
                    await Task.Delay(TimeSpan.FromSeconds(3));

                try
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                    {
                        var url = settings.BaseUrl.TrimEnd('/') + "/chat/completions";

                        var messagePayloads = new List<object>();
                        foreach (var m in messages)
                            messagePayloads.Add(new { role = m.Role, content = m.Content });

                        var payload = JsonConvert.SerializeObject(new
                        {
                            model = settings.Model,
                            messages = messagePayloads,
                            max_tokens = 2048
                        });

                        var request = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(payload, Encoding.UTF8, "application/json")
                        };
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                        request.Headers.Add("HTTP-Referer", "https://github.com/StixxyTape/Firefly");
                        request.Headers.Add("X-Title", "Firefly");

                        var response = await http.SendAsync(request);
                        var body = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            lastError = $"HTTP {(int)response.StatusCode}: {body}";
                            Log.Warning($"[Firefly] LLM attempt {attempt}/{maxAttempts} failed: {lastError}");
                            continue;
                        }

                        var content = JObject.Parse(body)?["choices"]?[0]?["message"]?["content"]?.Value<string>();
                        if (content == null)
                        {
                            lastError = "Malformed response — no content field in reply.";
                            Log.Warning($"[Firefly] LLM attempt {attempt}/{maxAttempts} failed: {lastError}");
                            continue;
                        }

                        LongEventHandler.ExecuteWhenFinished(() => onSuccess(content));
                        return;
                    }
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

            LongEventHandler.ExecuteWhenFinished(() => onError($"Failed after {maxAttempts} attempts. Last error: {lastError}"));
        }
    }
}
