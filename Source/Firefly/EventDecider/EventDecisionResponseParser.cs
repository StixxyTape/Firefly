using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Verse;

namespace Firefly
{
    public static class EventDecisionResponseParser
    {
        public static EventIntentResult ParseIntent(string rawJson, ISet<string> activeThreadIds)
        {
            JObject root = JsonResponseReader.ParseObject(rawJson, "event intent classification");
            if (root == null || !TryString(root["intent"], out string intentText) ||
                !TryString(root["reason"], out string reason))
                return InvalidIntent("intent and reason are required strings");

            EventIntent intent;
            switch (intentText.ToUpperInvariant())
            {
                case "A": intent = EventIntent.ExistingThread; break;
                case "B": intent = EventIntent.NewThreadMaterial; break;
                case "C": intent = EventIntent.Background; break;
                default: return InvalidIntent("intent must be A, B, or C");
            }

            string threadId = root["thread_id"]?.Type == JTokenType.String
                ? root["thread_id"].Value<string>().Trim()
                : "";
            if (intent == EventIntent.ExistingThread)
            {
                if (threadId.NullOrEmpty() || activeThreadIds == null || !activeThreadIds.Contains(threadId))
                    return InvalidIntent("intent A requires a supplied active thread id");
            }
            else if (!threadId.NullOrEmpty())
                return InvalidIntent("intent B or C must not identify a thread");

            return new EventIntentResult { Intent = intent, ThreadId = threadId, Reason = reason };
        }

        public static EventDecisionResult ParseParameters(string rawJson, EventIntentResult intent,
            ISet<string> allowedFields)
        {
            JObject root = JsonResponseReader.ParseObject(rawJson, "event parameter selection");
            if (root == null || !TryString(root["custom_letter_text"], out string letter) ||
                !(root["fields"] is JObject fields))
                return InvalidParameters("custom_letter_text and fields are required");

            if (CountOccurrences(letter, "{BASETEXT}") != 1)
                return InvalidParameters("custom_letter_text must contain {BASETEXT} exactly once");

            var result = EventDecisionResult.Untouched(intent);
            result.CustomLetterText = letter;
            foreach (JProperty property in fields.Properties())
            {
                string canonicalName = allowedFields?.FirstOrDefault(name =>
                    string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (canonicalName == null)
                    return InvalidParameters($"field {property.Name} is not allowed");
                if (property.Value.Type != JTokenType.String || property.Value.Value<string>().Trim().NullOrEmpty())
                    return InvalidParameters($"field {property.Name} must be a non-empty string");
                result.ProposedValues[canonicalName] = property.Value.Value<string>().Trim();
            }
            return result;
        }

        private static bool TryString(JToken token, out string value)
        {
            value = token?.Type == JTokenType.String ? token.Value<string>()?.Trim() ?? "" : "";
            return !value.NullOrEmpty();
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            for (int index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
                index += value.Length)
                count++;
            return count;
        }

        private static EventIntentResult InvalidIntent(string reason)
        {
            Log.Warning($"[Firefly:{EventDeciderPrompts.IntentLabel}] Invalid response: {reason}.");
            return null;
        }

        private static EventDecisionResult InvalidParameters(string reason)
        {
            Log.Warning($"[Firefly:{EventDeciderPrompts.ParametersLabel}] Invalid response: {reason}.");
            return null;
        }
    }
}
