using System.Collections.Generic;
using Verse;

namespace Firefly
{
    public static class EventDecisionResponseParser
    {
        public static EventIntentResult ParseIntent(string rawJson, ISet<string> activeThreadIds)
        {
            EventIntentResult result = EventDecisionResponseParserCore.ParseIntent(
                rawJson, activeThreadIds, out string error);
            if (result == null) InvalidIntent(error);
            return result;
        }

        public static EventDecisionResult ParseParameters(string rawJson, EventIntentResult intent,
            ISet<string> allowedFields)
        {
            EventDecisionResult result = EventDecisionResponseParserCore.ParseParameters(
                rawJson, intent, allowedFields, out string error);
            if (result == null) InvalidParameters(error);
            return result;
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
