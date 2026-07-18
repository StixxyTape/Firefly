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

            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
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
                        LongEventHandler.ExecuteWhenFinished(() =>
                            onError($"HTTP {(int)response.StatusCode}: {body}"));
                        return;
                    }

                    var content = JObject.Parse(body)?["choices"]?[0]?["message"]?["content"]?.Value<string>();
                    if (content == null)
                    {
                        LongEventHandler.ExecuteWhenFinished(() =>
                            onError("Malformed response — no content field in reply."));
                        return;
                    }

                    LongEventHandler.ExecuteWhenFinished(() => onSuccess(content));
                }
            }
            catch (TaskCanceledException)
            {
                LongEventHandler.ExecuteWhenFinished(() => onError("Timed out after 15s."));
            }
            catch (Exception e)
            {
                LongEventHandler.ExecuteWhenFinished(() => onError(e.Message));
            }
        }
    }
}
