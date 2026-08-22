using System.Collections.Generic;
using Verse;

namespace Firefly
{
    // Holds incidents that have been intercepted at TryFire and are waiting on the Event
    // Decider's LLM calls, mirroring RaidNarrativeTracker's per-game caching pattern. Not
    // persisted across save/load — a pending decision represents at most a few seconds of real
    // time with nothing fired or committed yet, so on load it's simplest and safest to just let
    // it go rather than carry an unresolved decision through the save system.
    //
    // Deliberately no independent hold-expiry here either (Josh's call, 2026-08-22, matching
    // RaidNarrativeTracker): a pending incident waits exactly as long as LLMClient's own
    // configured retry/timeout settings take, whatever that is for that player's config — same
    // global settings every other call in the mod defers to. Worst case under default settings
    // is several minutes per LLM call (up to two calls for an "existing thread" decision);
    // accepted as the trade for consistency over a special-cased short timeout.
    public class EventDecisionTracker
    {
        private static Game _cachedGame;
        private static EventDecisionTracker _cachedTracker;

        public static EventDecisionTracker For(Game game)
        {
            if (game == null) return null;
            if (!ReferenceEquals(game, _cachedGame))
            {
                _cachedGame = game;
                _cachedTracker = new EventDecisionTracker();
            }
            return _cachedTracker;
        }

        private readonly List<PendingEventDecision> _pending = new List<PendingEventDecision>();

        public void AddPending(PendingEventDecision pending) => _pending.Add(pending);

        // Idempotent first-wins claim: whichever of "the LLM callback landed" or "a save forced
        // early completion" gets here first removes and owns the record; the other is a no-op.
        public PendingEventDecision Claim(int requestId)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].RequestId == requestId)
                {
                    var found = _pending[i];
                    _pending.RemoveAt(i);
                    return found;
                }
            }
            return null;
        }

        // Called right before a save writes — see FireflyGameComponent.ExposeData. Resumes every
        // still-pending incident immediately, untouched (no LLM framing/nudges applied), the same
        // safe fallback used everywhere else in this system. The storyteller already "decided" to
        // fire these — silently dropping them on save would mean that bookkeeping happened but
        // the incident itself never did. Any LLM answer that lands after this arrives at a
        // requestId Claim() no longer recognizes and is a no-op.
        public void CompleteAllPending()
        {
            if (_pending.Count == 0) return;
            var ids = new List<int>(_pending.Count);
            foreach (var p in _pending) ids.Add(p.RequestId);
            foreach (var id in ids)
                Patch_EventDeciderIntercept.Complete(id, null);
        }
    }
}
