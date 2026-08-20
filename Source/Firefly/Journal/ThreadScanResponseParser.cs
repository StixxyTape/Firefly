using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Verse;

namespace Firefly
{
    public sealed class NewThreadResult
    {
        public string Name = "";
        public string Summary = "";
        public List<string> Facts = new List<string>();
    }

    public sealed class ThreadFactUpdateResult
    {
        public string Id = "";
        public List<string> Facts = new List<string>();
    }

    public sealed class ThreadScanResponse
    {
        public List<NewThreadResult> NewThreads = new List<NewThreadResult>();
        public List<ThreadFactUpdateResult> Updates = new List<ThreadFactUpdateResult>();
    }

    // Atomic parser shared by colony Story Threads and World Threads.
    public static class ThreadScanResponseParser
    {
        public static ThreadScanResponse Parse(string rawJson, string label,
            ISet<string> existingIds)
        {
            JObject root = JsonResponseReader.ParseObject(rawJson, label);
            if (root == null || !(root["new_threads"] is JArray newThreads) ||
                !(root["updates"] is JArray updates))
                return null;

            var result = new ThreadScanResponse();
            foreach (var token in newThreads)
            {
                if (!(token is JObject entry) || !NonEmpty(entry["name"]) ||
                    !NonEmpty(entry["summary"]) || !FactArray(entry["facts"], out List<string> facts))
                    return Invalid(label, "each new thread requires name, summary, and non-empty facts");
                result.NewThreads.Add(new NewThreadResult
                {
                    Name = entry["name"].Value<string>().Trim(),
                    Summary = entry["summary"].Value<string>().Trim(),
                    Facts = facts,
                });
            }
            foreach (var token in updates)
            {
                if (!(token is JObject entry) || !NonEmpty(entry["id"]) ||
                    !FactArray(entry["facts"], out List<string> facts))
                    return Invalid(label, "each update requires a known id and non-empty facts");
                string id = entry["id"].Value<string>().Trim();
                if (!existingIds.Contains(id)) return Invalid(label, $"unknown thread id {id}");
                result.Updates.Add(new ThreadFactUpdateResult { Id = id, Facts = facts });
            }
            return result;
        }

        private static ThreadScanResponse Invalid(string label, string reason)
        {
            Log.Warning($"[Firefly] {label} response was incomplete or invalid: {reason}");
            return null;
        }

        private static bool NonEmpty(JToken token) => token?.Type == JTokenType.String &&
            !token.Value<string>().Trim().NullOrEmpty();

        private static bool FactArray(JToken token, out List<string> facts)
        {
            facts = token is JArray array
                ? array.Where(NonEmpty).Select(t => t.Value<string>().Trim()).ToList()
                : new List<string>();
            return token is JArray source && source.Count > 0 && facts.Count == source.Count;
        }

    }
}
