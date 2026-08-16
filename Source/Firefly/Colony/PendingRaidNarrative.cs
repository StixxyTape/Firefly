using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Firefly
{
    // Holds a raid that has fully decided its details (faction, strategy, arrival mode, the
    // complete generated pawn list) but has not yet been committed to the map — Arrive() hasn't
    // run, so nothing here is spawned, visible, or ticking. Lives only from the moment
    // Patch_RaidNarrativeIntercept defers a raid until CommitRaid or Abandon runs, both on the
    // main thread. Never touched from a background thread.
    public class PendingRaidNarrative
    {
        public IncidentWorker_RaidEnemy Worker;
        public IncidentParms Parms;
        public List<Pawn> Pawns;
        public float OriginalPoints;
        public string BaselineLabel;
        public string BaselineText;

        // Identity used to detect a stale callback (save switched, game unloaded) before ever
        // touching Verse/RimWorld objects from a queued LLM callback — same pattern as
        // JournalRecorder's IsStillActive guard.
        public Game OwningGame;

        public int RequestId;
        public int CreatedTick;

        public bool Claimed;
    }
}
