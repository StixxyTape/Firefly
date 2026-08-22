using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Firefly
{
    // Dependency-free from RimWorld/Verse/Unity so the LLM response cleanup can be regression-tested
    // without loading the game. JsonResponseReader owns the game-facing warning on final failure.
    internal static class JsonResponseCore
    {
        private const int MaxAutoCloseInserts = 3;

        internal static JObject ParseObject(string rawJson)
        {
            if (string.IsNullOrEmpty(rawJson)) return null;
            string json = ExtractJson(rawJson);
            try { return JObject.Parse(json); }
            catch
            {
                string repaired = AutoClose(json);
                if (repaired == null) return null;
                try { return JObject.Parse(repaired); }
                catch { return null; }
            }
        }

        internal static string ExtractJson(string raw)
        {
            string text = raw.Trim();
            if (!text.StartsWith("```")) return text;
            int firstLine = text.IndexOf('\n');
            if (firstLine >= 0) text = text.Substring(firstLine + 1);
            int fence = text.LastIndexOf("```", StringComparison.Ordinal);
            return (fence >= 0 ? text.Substring(0, fence) : text).Trim();
        }

        internal static string AutoClose(string json)
        {
            var stack = new List<char>();
            var output = new StringBuilder(json.Length + MaxAutoCloseInserts);
            bool inString = false;
            int inserted = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    output.Append(c);
                    if (c == '\\' && i + 1 < json.Length) output.Append(json[++i]);
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; output.Append(c); continue; }
                if (c == '{' || c == '[') { stack.Add(c == '{' ? '}' : ']'); output.Append(c); continue; }
                if (c == '}' || c == ']')
                {
                    int match = stack.LastIndexOf(c);
                    if (match < 0) return null;
                    int missing = stack.Count - 1 - match;
                    if (inserted + missing > MaxAutoCloseInserts) return null;
                    for (int k = stack.Count - 1; k > match; k--) { output.Append(stack[k]); inserted++; }
                    stack.RemoveRange(match, stack.Count - match);
                    output.Append(c);
                    continue;
                }
                output.Append(c);
            }
            if (inString || stack.Count + inserted > MaxAutoCloseInserts) return null;
            for (int i = stack.Count - 1; i >= 0; i--) output.Append(stack[i]);
            return stack.Count > 0 || inserted > 0 ? output.ToString() : null;
        }
    }
}
