using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Verse;

namespace Firefly
{
    // Parses and applies the single day-end scan response that drives Story Threads: it decides
    // which threads are new vs. updated, and writes each one's full summary directly (using the
    // existing-threads context block it was given) — no separate rewrite pass. Kept out of
    // ColonyLedger — this is response parsing and id generation, not ledger state.
    public static class StoryThreadScanIngest
    {
        // Context block for the scan prompt: every existing thread's id, name, and current
        // summary (never its Facts ledger — that stays internal bookkeeping).
        public static string BuildThreadContextBlock(ColonyLedger ledger)
        {
            var threads = ledger.StoryThreads;
            if (threads.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("=== EXISTING STORY THREADS ===");
            foreach (var s in threads)
            {
                sb.AppendLine($"id: {s.Id}");
                sb.AppendLine($"name: {s.Name}");
                sb.AppendLine($"summary: {(s.Description.NullOrEmpty() ? "(none yet)" : s.Description.Trim())}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // Applies the scan response: creates new_threads (locally-generated slugified ids, with
        // their initial summary and facts) and applies updates (matched by id — never by name) —
        // each update's facts are appended to the ledger and its summary is the LLM's full
        // rewrite, written straight onto Description.
        public static void ApplyScanResult(ColonyLedger ledger, string rawJson)
        {
            JObject root = TryParse(rawJson, "thread scan");
            if (root == null) return;

            var existingIds = new HashSet<string>(ledger.StoryThreads.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in AsArray(root["new_threads"]))
            {
                try
                {
                    string name = entry["name"]?.Value<string>();
                    if (name.NullOrEmpty())
                    {
                        Log.Warning("[Firefly] Thread scan: new_threads entry missing name, skipped.");
                        continue;
                    }
                    string summary = entry["summary"]?.Value<string>() ?? "";

                    string id = GenerateThreadId(name, existingIds);
                    existingIds.Add(id);
                    ledger.AddStoryThread(id, name, summary);

                    foreach (var factTok in AsArray(entry["facts"]))
                    {
                        string fact = factTok?.Value<string>();
                        if (!fact.NullOrEmpty())
                            ledger.AddFactToThread(id, Find.TickManager.TicksAbs, fact);
                    }
                }
                catch (Exception e)
                {
                    Log.Warning($"[Firefly] Thread scan: failed to apply a new_threads entry: {e.Message}");
                }
            }

            foreach (var entry in AsArray(root["updates"]))
            {
                try
                {
                    string id = entry["id"]?.Value<string>();
                    if (id.NullOrEmpty() || !existingIds.Contains(id))
                    {
                        Log.Warning($"[Firefly] Thread scan: updates entry referenced unknown thread id \"{id}\", skipped.");
                        continue;
                    }

                    foreach (var factTok in AsArray(entry["facts"]))
                    {
                        string fact = factTok?.Value<string>();
                        if (!fact.NullOrEmpty())
                            ledger.AddFactToThread(id, Find.TickManager.TicksAbs, fact);
                    }

                    string summary = entry["summary"]?.Value<string>();
                    if (!summary.NullOrEmpty())
                        ledger.UpdateStoryThreadSummary(id, summary);
                }
                catch (Exception e)
                {
                    Log.Warning($"[Firefly] Thread scan: failed to apply an updates entry: {e.Message}");
                }
            }
        }

        private static JObject TryParse(string rawJson, string label)
        {
            if (rawJson.NullOrEmpty()) return null;
            try { return JObject.Parse(ExtractJson(rawJson)); }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] {label} response was not valid JSON: {e.Message}");
                return null;
            }
        }

        private static IEnumerable<JToken> AsArray(JToken token) =>
            token as JArray ?? Enumerable.Empty<JToken>();

        // LLMs sometimes wrap JSON in ```json fences despite instructions not to — strip if present.
        private static string ExtractJson(string raw)
        {
            string text = raw.Trim();
            if (!text.StartsWith("```")) return text;

            int firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text.Substring(firstNewline + 1);
            int fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) text = text.Substring(0, fence);
            return text.Trim();
        }

        private static readonly Regex _nonSlugChars = new Regex("[^a-z0-9]+", RegexOptions.Compiled);

        private static string GenerateThreadId(string name, HashSet<string> existingIds)
        {
            string slug = _nonSlugChars.Replace(name.ToLowerInvariant(), "-").Trim('-');
            if (slug.NullOrEmpty()) slug = "thread";
            if (!existingIds.Contains(slug)) return slug;

            int n = 2;
            while (existingIds.Contains($"{slug}-{n}")) n++;
            return $"{slug}-{n}";
        }
    }
}
