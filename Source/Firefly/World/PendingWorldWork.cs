using System.Collections.Generic;
using Verse;

namespace Firefly
{
    // WorldOutcome/WorldThreadUpdates replace the old single WorldProgression stage (split so the
    // creative "what happens" call and the mechanical "write the JSON" call are separate — see
    // PendingWorldWork.WorldOutcomeResult for the persisted hand-off between them).
    // Faction Update writes Narrative and identity facts directly, then the two independent
    // journals regenerate before Taglines close the daily chain.
    public enum WorldWorkStage
    {
        WorldOutcome = 0,
        WorldThreadUpdates = 1,
        WorldThreadSummaries = 2,
        FactionUpdate = 3,
        // Value 4 is reserved for compatibility with saves made before the pipeline simplification.
        NarrativeSummaries = 5,
        DescriptionSummaries = 6,
        FactionTaglines = 7,
        WorldArcHistory = 8,
    }

    public class PendingWorldWork : IExposable
    {
        public int Day;
        public string ColonySummary = "";
        public WorldWorkStage Stage;
        public List<string> TouchedWorldThreadIds = new List<string>();
        public List<string> TouchedFactionKeys = new List<string>();
        // Subset of factions that received identity facts directly from Faction Update.
        public List<string> TouchedFactionFactKeys = new List<string>();
        public bool WorldGeneration;

        // Persisted output of the WorldOutcome call — set once that call succeeds, consumed (not
        // mutated) by WorldThreadUpdates. Keeping this durable means a reload mid-chain only ever
        // has to retry the mechanical JSON-conversion call, never re-roll what happened that day.
        public string WorldOutcome = "";

        public void ExposeData()
        {
            Scribe_Values.Look(ref Day, "day", 0);
            Scribe_Values.Look(ref ColonySummary, "colonySummary", "");
            Scribe_Values.Look(ref Stage, "stage", WorldWorkStage.WorldOutcome);
            Scribe_Collections.Look(ref TouchedWorldThreadIds, "touchedWorldThreadIds", LookMode.Value);
            Scribe_Collections.Look(ref TouchedFactionKeys, "touchedFactionKeys", LookMode.Value);
            Scribe_Collections.Look(ref TouchedFactionFactKeys, "touchedFactionFactKeys", LookMode.Value);
            Scribe_Values.Look(ref WorldGeneration, "worldGeneration", false);
            Scribe_Values.Look(ref WorldOutcome, "worldOutcome", "");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if ((int)Stage == 4) Stage = WorldWorkStage.NarrativeSummaries;
                if (TouchedWorldThreadIds == null) TouchedWorldThreadIds = new List<string>();
                if (TouchedFactionKeys == null) TouchedFactionKeys = new List<string>();
                if (TouchedFactionFactKeys == null) TouchedFactionFactKeys = new List<string>();
            }
        }
    }
}
