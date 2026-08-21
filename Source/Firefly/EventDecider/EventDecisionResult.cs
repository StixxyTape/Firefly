using System.Collections.Generic;

namespace Firefly
{
    public enum EventIntent
    {
        ExistingThread,
        NewThreadMaterial,
        Background,
    }

    public sealed class EventIntentResult
    {
        public EventIntent Intent;
        public string ThreadId = "";
        public string Reason = "";
    }

    public sealed class EventDecisionResult
    {
        public EventIntent Intent;
        public string ThreadId = "";
        public string Reason = "";
        public string CustomLetterText = "";
        public Dictionary<string, string> ProposedValues = new Dictionary<string, string>();

        public bool HasIntervention => Intent == EventIntent.ExistingThread &&
            (!string.IsNullOrEmpty(CustomLetterText) || ProposedValues.Count > 0);

        public static EventDecisionResult Untouched(EventIntentResult intent) => new EventDecisionResult
        {
            Intent = intent.Intent,
            ThreadId = intent.ThreadId,
            Reason = intent.Reason,
        };
    }
}
