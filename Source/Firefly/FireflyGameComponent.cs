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
                {
                    Recorder.TriggerBackfillOnLoad();
                    Recorder.RecoverInterruptedThreadWork();
                }
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

        // Ticks stop advancing while the game is paused, but a background LLM request can still
        // be sitting mid-attempt with its queued main-thread callback waiting to run (in
        // particular, LLMClient's retry-on-invalid-content bridge genuinely awaits that callback
        // before deciding whether to retry) — without this, that wait would hang for however long
        // the player leaves the game paused. GameComponentUpdate runs every frame regardless of
        // pause state, so drain here too; Drain() is a cheap no-op when nothing is queued, so
        // calling it from both hooks isn't wasteful.
        public override void GameComponentUpdate()
        {
            MainThreadQueue.Drain();
        }

        public override void ExposeData()
        {
            // A pending raid's pawns aren't spawned or saved anywhere — the storyteller already
            // recorded the incident as fired, though, so if we let a save happen mid-wait and
            // just drop them on load, that raid is gone forever with no letter ever shown. Force
            // every pending raid to commit (with vanilla text if Fillion hasn't answered yet)
            // before the save actually writes, so nothing is ever silently lost to a save/reload.
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                RaidNarrativeTracker.For(Verse.Current.Game)?.CompleteAllPending();
                EventDecisionTracker.For(Verse.Current.Game)?.CompleteAllPending();
            }

            base.ExposeData();
            Scribe_Values.Look(ref _fireflyEnabled, "fireflyEnabled", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                _loadedFromSave = true;
            Ledger.ExposeData();
            Recorder.ExposeData();
        }
    }
}
