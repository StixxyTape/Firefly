using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Firefly
{
    // Pure parser core. The public wrapper adds RimWorld logging; this class owns only validation
    // and plain result construction so it can run in the standalone regression-test executable.
    internal static class EventDecisionResponseParserCore
    {
        internal static EventIntentResult ParseIntent(string rawJson, ISet<string> activeThreadIds,
            out string error)
        {
            error = null;
            JObject root = JsonResponseCore.ParseObject(rawJson);
            if (root == null || !TryString(root["intent"], out string intentText) ||
                !TryString(root["reason"], out string reason))
                return Invalid("intent and reason are required strings", out error);

            EventIntent intent;
            switch (intentText.ToUpperInvariant())
            {
                case "A": intent = EventIntent.ExistingThread; break;
                case "B": intent = EventIntent.NewThreadMaterial; break;
                case "C": intent = EventIntent.Background; break;
                default: return Invalid("intent must be A, B, or C", out error);
            }

            string threadId = root["thread_id"]?.Type == JTokenType.String
                ? root["thread_id"].Value<string>().Trim()
                : "";
            if (intent == EventIntent.ExistingThread)
            {
                if (string.IsNullOrEmpty(threadId) || activeThreadIds == null ||
                    !activeThreadIds.Contains(threadId))
                    return Invalid("intent A requires a supplied active thread id", out error);
            }
            else if (!string.IsNullOrEmpty(threadId))
                return Invalid("intent B or C must not identify a thread", out error);

            return new EventIntentResult { Intent = intent, ThreadId = threadId, Reason = reason };
        }

        internal static EventDecisionResult ParseParameters(string rawJson, EventIntentResult intent,
            ISet<string> allowedFields, out string error)
        {
            error = null;
            JObject root = JsonResponseCore.ParseObject(rawJson);
            if (root == null || !TryString(root["custom_letter_text"], out string letter) ||
                !(root["fields"] is JObject fields))
                return InvalidParameters("custom_letter_text and fields are required", out error);

            if (CountOccurrences(letter, "{BASETEXT}") != 1)
                return InvalidParameters("custom_letter_text must contain {BASETEXT} exactly once", out error);

            var result = EventDecisionResult.Untouched(intent);
            result.CustomLetterText = letter;
            foreach (JProperty property in fields.Properties())
            {
                string canonicalName = allowedFields?.FirstOrDefault(name =>
                    string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (canonicalName == null)
                    return InvalidParameters($"field {property.Name} is not allowed", out error);
                if (property.Value.Type != JTokenType.String ||
                    string.IsNullOrEmpty(property.Value.Value<string>()?.Trim()))
                    return InvalidParameters($"field {property.Name} must be a non-empty string", out error);
                result.ProposedValues[canonicalName] = property.Value.Value<string>().Trim();
            }
            return result;
        }

        private static bool TryString(JToken token, out string value)
        {
            value = token?.Type == JTokenType.String ? token.Value<string>()?.Trim() ?? "" : "";
            return !string.IsNullOrEmpty(value);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            for (int index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
                index += value.Length)
                count++;
            return count;
        }

        private static EventIntentResult Invalid(string reason, out string error)
        {
            error = reason;
            return null;
        }

        private static EventDecisionResult InvalidParameters(string reason, out string error)
        {
            error = reason;
            return null;
        }
    }
}
