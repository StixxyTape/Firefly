using System.Collections.Generic;

namespace Firefly
{
    // Immutable prompt material captured on the main thread before any request starts. Keeping
    // Verse objects out of this object prevents background request work from touching game state.
    public sealed class EventDecisionRequest
    {
        public string IncidentDefName = "";
        public string IncidentLabel = "";
        public string BaseParameters = "";
        public string RecentEvents = "";
        public string FactionContext = "";
        public List<EventThreadContext> ActiveThreads = new List<EventThreadContext>();
        public Dictionary<string, string> AllowedFields = new Dictionary<string, string>();
    }

    public sealed class EventThreadContext
    {
        public string Id = "";
        public string Name = "";
        public string Summary = "";
    }
}
