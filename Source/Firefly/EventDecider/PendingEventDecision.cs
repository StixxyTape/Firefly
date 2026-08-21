using RimWorld;
using Verse;

namespace Firefly
{
    // Holds an incident that has been intercepted at Storyteller.TryFire, before CanFireNow/
    // TryExecute have run — nothing about this incident has happened yet (no pawns, no letter, no
    // map changes), so there is nothing to undo if this ends up just resuming untouched. Lives
    // only from the moment Patch_EventDeciderIntercept defers it until
    // Patch_EventDeciderIntercept.Complete runs, both on the main thread. Never touched from a
    // background thread — same rule as PendingRaidNarrative.
    //
    // Holds the whole FiringIncident rather than splitting it into separate Worker/Def/Parms
    // fields: TryFire's own tail (Notify_IncidentFired) takes a FiringIncident directly, and
    // IncidentParms is a reference type, so mutating Fi.parms's fields through this holder is the
    // same object the eventual CanFireNow/TryExecute call sees — no separate re-assembly step
    // needed at commit time.
    public class PendingEventDecision
    {
        public FiringIncident Fi;
        public IIncidentAdapter Adapter;

        // Identity used to detect a stale callback (save switched, game unloaded) before ever
        // touching Verse/RimWorld objects from a queued LLM callback — same pattern as
        // PendingRaidNarrative.OwningGame / JournalRecorder's IsStillActive guard.
        public Game OwningGame;

        public int RequestId;
        public int CreatedTick;
        public float CreatedRealtime;
    }
}
