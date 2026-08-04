using System;
using RimWorld;
using Verse;

namespace Firefly
{
    // Owns the ledger and the journal recorder for exactly one game. RimWorld builds one of these
    // per loaded save and discards it on unload, so no state can leak from one colony into the next.
    public class FireflyGameComponent : GameComponent
    {
        private const int StorytellerCheckInterval = 2000;

        public ColonyLedger    Ledger   = new ColonyLedger();
        public JournalRecorder Recorder;

        private int _tickCounter;

        public FireflyGameComponent(Game game)
        {
            Recorder = new JournalRecorder(Ledger);
        }

        public override void FinalizeInit()
        {
            try { RefreshEnabled(); }
            catch (Exception e) { Log.Warning($"[Firefly] FinalizeInit failed: {e.Message}"); }
        }

        // Re-checked periodically because the storyteller can be swapped mid-game.
        private void RefreshEnabled()
        {
            bool isFillion = Find.Storyteller?.def?.defName == "Fillion";
            Ledger.SetEnabled(isFillion);
            Recorder.SetEnabled(isFillion);
        }

        public override void GameComponentTick()
        {
            MainThreadQueue.Drain();

            _tickCounter++;
            if (_tickCounter % StorytellerCheckInterval == 0) RefreshEnabled();

            Recorder.Tick();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Ledger.ExposeData();
            Recorder.ExposeData();
        }
    }
}
