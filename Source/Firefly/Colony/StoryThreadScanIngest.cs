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
    // which threads are new vs. updated and records their facts. It only ever writes a summary
    // itself for a brand-new thread (a short one, to avoid leaving it blank); an existing
    // thread's full summary is written by a separate per-thread pass in JournalRecorder, derived
    // from that thread's complete fact record rather than patched here. Kept out of ColonyLedger
    // — this is response parsing and id generation, not ledger state.
    public static class StoryThreadScanIngest
    {
        // Context block of every existing thread's id, name, and current summary — the single
        // scan call always gets the full set, never filtered, so it has enough to judge relevance
        // and avoid duplicate threads on its own.
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
        // their initial short summary and facts — written directly here, since a brand-new
        // thread needs SOME summary right away and doesn't yet have a fact history worth a full
        // per-thread summarize pass) and applies updates (matched by id — never by name) — an
        // update entry only ever carries facts now, never a summary; the full summary for a
        // touched existing thread is written separately, derived from its complete fact record
        // (chunks + unchunked tail) rather than patched onto whatever this call last wrote.
        // Returns null only when rawJson itself couldn't be parsed — that's the caller's signal
        // to retry the request; a malformed individual entry inside otherwise-valid JSON is
        // skipped and logged, not a retry trigger. A non-null (possibly empty) list of touched
        // EXISTING thread ids is always the valid-JSON case, same convention as ParseRelevantIds —
        // the caller uses it to dispatch each one's summarize pass.
        public static List<string> ApplyScanResult(ColonyLedger ledger, string rawJson, int day)
        {
            JObject root = TryParse(rawJson, "thread scan");
            if (root == null) return null;

            var existingIds = new HashSet<string>(ledger.StoryThreads.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
            if (!HasCompleteSchema(root, existingIds, out string validationError))
            {
                Log.Warning($"[Firefly] Thread scan response was incomplete or invalid: {validationError}");
                return null;
            }

            var touchedExisting = new List<string>();

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
                    long now = Find.TickManager.TicksAbs;
                    ledger.AddStoryThread(id, name, summary);
                    ledger.TouchStoryThread(id, now);

                    foreach (var factTok in AsArray(entry["facts"]))
                    {
                        string fact = factTok?.Value<string>();
                        if (!fact.NullOrEmpty())
                            ledger.AddFactToThread(id, now, day, fact);
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

                    long now      = Find.TickManager.TicksAbs;
                    bool touched  = false;

                    foreach (var factTok in AsArray(entry["facts"]))
                    {
                        string fact = factTok?.Value<string>();
                        if (!fact.NullOrEmpty())
                        {
                            ledger.AddFactToThread(id, now, day, fact);
                            touched = true;
                        }
                    }

                    if (touched)
                    {
                        ledger.TouchStoryThread(id, now);
                        touchedExisting.Add(id);
                    }
                }
                catch (Exception e)
                {
                    Log.Warning($"[Firefly] Thread scan: failed to apply an updates entry: {e.Message}");
                }
            }

            return touchedExisting;
        }

        // Parsing alone is not success: a response truncated cleanly after one property can still
        // be valid JSON. Validate the complete contract before applying any entry, making retries
        // atomic and preventing a partial response from silently completing a day.
        internal static bool HasCompleteSchema(JObject root, HashSet<string> existingIds, out string error)
        {
            error = null;
            if (!(root?["new_threads"] is JArray newThreads))
            {
                error = "new_threads must be present and be an array";
                return false;
            }
            if (!(root["updates"] is JArray updates))
            {
                error = "updates must be present and be an array";
                return false;
            }

            foreach (var token in newThreads)
            {
                if (!(token is JObject entry) || !IsNonEmptyString(entry["name"]) ||
                    !IsNonEmptyString(entry["summary"]) || !IsNonEmptyStringArray(entry["facts"]))
                {
                    error = "each new_threads entry requires a non-empty name, summary, and facts array";
                    return false;
                }
            }

            foreach (var token in updates)
            {
                if (!(token is JObject entry) || !IsNonEmptyString(entry["id"]) ||
                    !IsNonEmptyStringArray(entry["facts"]))
                {
                    error = "each updates entry requires an id and at least one non-empty fact";
                    return false;
                }

                string id = entry["id"].Value<string>().Trim();
                if (!existingIds.Contains(id))
                {
                    error = $"update references unknown thread id \"{id}\"";
                    return false;
                }
            }

            return true;
        }

        private static bool IsNonEmptyString(JToken token) =>
            token?.Type == JTokenType.String && !token.Value<string>().Trim().NullOrEmpty();
        private static bool IsNonEmptyStringArray(JToken token) =>
            token is JArray array && array.Count > 0 && array.All(IsNonEmptyString);

        private static JObject TryParse(string rawJson, string label)
        {
            if (rawJson.NullOrEmpty()) return null;
            string extracted = ExtractJson(rawJson);
            try { return JObject.Parse(extracted); }
            catch (Exception e)
            {
                string repaired = TryAutoCloseBrackets(extracted);
                if (repaired != null)
                {
                    try
                    {
                        var result = JObject.Parse(repaired);
                        Log.Message($"[Firefly] {label} response was missing a closing bracket — auto-repaired.");
                        return result;
                    }
                    catch { /* still broken — fall through to the warning below */ }
                }

                Log.Warning($"[Firefly] {label} response was not valid JSON: {e.Message}");
                return null;
            }
        }

        // Cheap, deterministic structural fix for a common small-model failure mode: dropping a
        // single closing bracket somewhere in a long generated JSON blob — observed in practice
        // to survive even an LLM repair pass, since reproducing the whole blob verbatim doesn't
        // reduce the bracket-tracking burden that caused the original slip. Tried before ever
        // reaching for the LLM repair call. Deliberately conservative: never touches text inside
        // an unterminated string, and bails (returns null) if more than a handful of closers are
        // missing or mismatched — that's more likely real truncation than a one-bracket slip, and
        // better left to the LLM repair call or a full retry than silently patched over.
        private const int MaxAutoCloseInserts = 3;

        private static string TryAutoCloseBrackets(string json)
        {
            var stack = new List<char>();
            var sb = new StringBuilder(json.Length + MaxAutoCloseInserts);
            bool inString = false;
            int inserted = 0;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length)
                    {
                        sb.Append(json[++i]);
                        continue;
                    }
                    if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') { inString = true; sb.Append(c); continue; }

                if (c == '{' || c == '[')
                {
                    stack.Add(c == '{' ? '}' : ']');
                    sb.Append(c);
                    continue;
                }

                if (c == '}' || c == ']')
                {
                    if (stack.Count > 0 && stack[stack.Count - 1] == c)
                    {
                        stack.RemoveAt(stack.Count - 1);
                        sb.Append(c);
                        continue;
                    }

                    // Mismatched closer — look back for the level it actually closes and insert
                    // whatever's missing in between, rather than treating it as unrecoverable.
                    int matchDepth = stack.LastIndexOf(c);
                    if (matchDepth < 0) return null;

                    int missing = stack.Count - 1 - matchDepth;
                    if (inserted + missing > MaxAutoCloseInserts) return null;

                    for (int k = stack.Count - 1; k > matchDepth; k--)
                    {
                        sb.Append(stack[k]);
                        inserted++;
                    }
                    stack.RemoveRange(matchDepth, stack.Count - matchDepth);
                    sb.Append(c);
                    continue;
                }

                sb.Append(c);
            }

            if (inString) return null; // Unterminated string — real truncation, not a bracket slip.
            if (stack.Count == 0) return inserted > 0 ? sb.ToString() : null;
            if (stack.Count + inserted > MaxAutoCloseInserts) return null;

            for (int k = stack.Count - 1; k >= 0; k--)
            {
                sb.Append(stack[k]);
                inserted++;
            }
            return sb.ToString();
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
