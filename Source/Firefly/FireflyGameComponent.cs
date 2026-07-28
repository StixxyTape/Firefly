using System;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;

namespace Firefly
{
    // Owns the ledger for exactly one game. RimWorld builds one of these per loaded save and
    // discards it on unload, so ledger state can no longer leak from one colony into the next.
    public class FireflyGameComponent : GameComponent
    {
        private const int TimelineFlushInterval = 300;
        private const int ContextWriteInterval  = 2000;

        public ColonyLedger Ledger = new ColonyLedger();

        private int _tickCounter;

        public FireflyGameComponent(Game game) { }

        public override void FinalizeInit()
        {
            try
            {
                if (Find.Storyteller?.def?.defName != "Fillion") return;
                Ledger.SetOutputDir(GetOutputDir());
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] FinalizeInit failed: {e.Message}");
            }
        }

        public override void GameComponentTick()
        {
            _tickCounter++;
            if (_tickCounter % TimelineFlushInterval == 0) Ledger.FlushTimelineBuffer();
            if (_tickCounter % ContextWriteInterval  == 0) Ledger.WriteContextFile();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) Ledger.FlushTimelineBuffer();
            Ledger.ExposeData();
        }

        public static string GetOutputDir()
        {
            // permadeathModeUniqueName is only set in Commitment mode; every other save reports empty,
            // so fall back to the world name plus its persistent seed to keep colonies apart.
            string saveName = Current.Game?.Info?.permadeathModeUniqueName;

            if (saveName.NullOrEmpty())
            {
                var info = Find.World?.info;
                saveName = info != null
                    ? $"{(info.name.NullOrEmpty() ? "colony" : info.name)}_{info.persistentRandomValue}"
                    : "unknown";
            }

            string safeName = string.Concat(
                saveName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

            string dir = Path.Combine(GenFilePaths.ConfigFolderPath, "Firefly", safeName);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
