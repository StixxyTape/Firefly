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
        private int lastLedgerSlot = -1;
        private int lastTimelineDay = -1;

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            if (!(target is Map)) yield break;

            Map map = Find.CurrentMap;
            if (map == null) yield break;

            int totalHours = (int)(Find.TickManager.TicksGame / GenDate.TicksPerHour);
            int ledgerSlot = totalHours / 3;
            int totalDays  = totalHours / 24;

            // Compile and clear at day boundary, before recording the new slot
            if (totalDays > lastTimelineDay)
            {
                lastTimelineDay = totalDays;
                WriteTimeline(map);
            }

            // Record every 3 hours and flush to disk
            if (ledgerSlot > lastLedgerSlot)
            {
                lastLedgerSlot = ledgerSlot;
                ColonyLedger.Record(map, totalHours % 24);
                FlushLedger(map);
            }
        }

        private static void FlushLedger(Map map)
        {
            try
            {
                string content = ColonyLedger.Compile(map);
                if (content.NullOrEmpty()) return;
                string dir = Path.Combine(GenFilePaths.ConfigFolderPath, "Firefly");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "timeline_latest.txt"), content, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Failed to flush ledger: {e.Message}");
            }
        }

        private static void WriteTimeline(Map map)
        {
            try
            {
                string timeline = ColonyLedger.Compile(map);
                ColonyLedger.Clear();
                if (timeline.NullOrEmpty()) return;

                string dir = Path.Combine(GenFilePaths.ConfigFolderPath, "Firefly");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "timeline_latest.txt"), timeline, Encoding.UTF8);
                Log.Message($"[Firefly] Timeline written: Day {GenDate.DaysPassed}");
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Failed to write timeline: {e.Message}");
            }
        }
    }
}
