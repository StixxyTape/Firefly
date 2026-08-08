using System;
using RimWorld;
using Verse;

namespace Firefly
{
    // Owns the ledger and the journal recorder for exactly one game. RimWorld builds one of these
    // per loaded save and discards it on unload, so no state can leak from one colony into the next.
    public class FireflyGameComponent : GameComponent
    {
        public ColonyLedger    Ledger   = new ColonyLedger();
        public JournalRecorder Recorder;

        private bool _fireflyEnabled = true;
        private bool _loadedFromSave = false;

        public bool FireflyEnabled => _fireflyEnabled;

        public FireflyGameComponent(Game game)
        {
            Recorder = new JournalRecorder(Ledger);
        }

        public override void FinalizeInit()
        {
            try
            {
                // New game: read the choice made on the storyteller selection page.
                // Loaded save: _loadedFromSave is true, keep the persisted value.
                if (!_loadedFromSave)
                    _fireflyEnabled = Patch_StorytellerPage.FireflyEnabled;
                RefreshEnabled();
                // On load, kick off backfill for any days that have a timeline but no summary
                // (e.g. days whose LLM request failed last session).
                if (_loadedFromSave && _fireflyEnabled)
                    Recorder.TriggerBackfillOnLoad();
            }
            catch (Exception e) { Log.Warning($"[Firefly] FinalizeInit failed: {e.Message}"); }
        }

        private void RefreshEnabled()
        {
            Ledger.SetEnabled(_fireflyEnabled);
            Recorder.SetEnabled(_fireflyEnabled);
        }

        public override void GameComponentTick()
        {
            MainThreadQueue.Drain();
            Recorder.Tick();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref _fireflyEnabled, "fireflyEnabled", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                _loadedFromSave = true;
            Ledger.ExposeData();
            Recorder.ExposeData();
        }
    }
}
