using System.Collections.Generic;
using Verse;

namespace Firefly
{
    // Holds raids that have been fully decided (faction/strategy/arrival mode/pawns all
    // resolved) but not yet committed to the map, while Fillion's rewrite is in flight. Not
    // persisted across save/load — a pending raid represents at most a few seconds of real time
    // with nothing spawned, so on load it's simplest and safest to just let it go rather than
    // carry unspawned-pawn ownership through the save system. Mirrors ColonyLedger.Current's
    // per-game caching pattern, but keyed explicitly since a raid's own PendingRaidNarrative
    // already carries a reference to the game it belongs to.
    public class RaidNarrativeTracker
    {
        private static Game _cachedGame;
        private static RaidNarrativeTracker _cachedTracker;

        public static RaidNarrativeTracker For(Game game)
        {
            if (game == null) return null;
            if (!ReferenceEquals(game, _cachedGame))
            {
                _cachedGame = game;
                _cachedTracker = new RaidNarrativeTracker();
            }
            return _cachedTracker;
        }

        private readonly List<PendingRaidNarrative> _pending = new List<PendingRaidNarrative>();

        public void AddPending(PendingRaidNarrative pending) => _pending.Add(pending);

        // Idempotent first-wins claim: whichever of "Fillion's callback landed" or "a save forced
        // early completion" gets here first removes and owns the record; the other is a no-op.
        public PendingRaidNarrative Claim(int requestId)
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

        // Called right before a save writes — see FireflyGameComponent.ExposeData. Commits every
        // still-pending raid immediately with vanilla fallback text (Fillion's answer, if it
        // lands after this, arrives at a requestId Claim() no longer recognizes and is a no-op).
        public void CompleteAllPending()
        {
            if (_pending.Count == 0) return;
            var ids = new List<int>(_pending.Count);
            foreach (var p in _pending) ids.Add(p.RequestId);
            foreach (var id in ids)
                Patch_RaidNarrativeIntercept.Complete(id, null);
        }
    }
}
