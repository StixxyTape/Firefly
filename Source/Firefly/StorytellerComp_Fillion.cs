using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RimWorld;
using Verse;

namespace Firefly
{
    public class StorytellerCompProperties_Fillion : StorytellerCompProperties
    {
        public StorytellerCompProperties_Fillion()
        {
            compClass = typeof(StorytellerComp_Fillion);
        }
    }

    public class StorytellerComp_Fillion : StorytellerComp
    {
        private int lastSnapshotHour = -1;

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            if (!(target is Map)) yield break;

            int currentHour = (int)(Find.TickManager.TicksGame / GenDate.TicksPerHour);
            if (currentHour <= lastSnapshotHour) yield break;
            lastSnapshotHour = currentHour;

            Map map = Find.CurrentMap;
            if (map == null) yield break;

            WriteSnapshot(map);
        }

        private static void WriteSnapshot(Map map)
        {
            try
            {
                string snapshot = ColonyStateCollector.GetSnapshot(map);
                if (snapshot == null) return;

                string dir = Path.Combine(GenFilePaths.ConfigFolderPath, "Firefly");
                Directory.CreateDirectory(dir);

                File.WriteAllText(Path.Combine(dir, "snapshot_latest.txt"), snapshot, Encoding.UTF8);
                Log.Message($"[Firefly] Snapshot written: Day {GenDate.DaysPassed}");
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Failed to write snapshot: {e.Message}");
            }
        }
    }
}
