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
        private int lastHourBucket = -1;

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            if (!(target is Map)) yield break;

            Map map = Find.CurrentMap;
            if (map == null) yield break;

            int hourOfDay  = GenLocalDate.HourOfDay(map);
            int hourBucket = hourOfDay / 3;

            // Prime on first tick — don't fire, just record where we are
            if (lastHourBucket == -1)
            {
                lastHourBucket = hourBucket;
                yield break;
            }

            if (hourBucket == lastHourBucket) yield break;

            // Bucket went backwards → local midnight crossed → compile the day first
            if (hourBucket < lastHourBucket)
                WriteTimeline(map);

            lastHourBucket = hourBucket;
            ColonyLedger.Record(map, hourOfDay);
            FlushLedger(map);
        }

        private static void FlushLedger(Map map)
        {
            try
            {
                string content = ColonyLedger.Compile(map);
                if (content.NullOrEmpty()) return;
                string dir = Path.Combine(GenFilePaths.ConfigFolderPath, "Firefly");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "timeline_current.txt"), content, Encoding.UTF8);
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
                int day = ColonyLedger.RecordingDay;
                ColonyLedger.Clear();

                string dir = Path.Combine(GenFilePaths.ConfigFolderPath, "Firefly");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "timeline_current.txt"), "", Encoding.UTF8);

                if (timeline.NullOrEmpty()) return;

                // Always save the raw timeline
                File.WriteAllText(Path.Combine(dir, $"daily_timeline_day{day}.txt"), timeline, Encoding.UTF8);

                string summaryPath = Path.Combine(dir, $"daily_summary_day{day}.txt");

                string systemPrompt =
                    "You are Fillion, a storyteller observing a distant colony. " +
                    "Summarize the colony's day in exactly 5 short, focused, narrative sentences. " +
                    "Write in third person. Be concise, evocative, and story-driven. " +
                    "No bullet points, no headers, no lists.";

                Log.Message($"[Firefly] Sending Day {day} to LLM for summary...");

                LLMClient.Send(
                    systemPrompt,
                    timeline,
                    onSuccess: summary =>
                    {
                        try { File.WriteAllText(summaryPath, summary, Encoding.UTF8); }
                        catch (Exception e) { Log.Warning($"[Firefly] Failed to write summary: {e.Message}"); }
                        Log.Message($"[Firefly] Daily summary written: Day {day}");
                    },
                    onError: err =>
                    {
                        Log.Warning($"[Firefly] LLM summary failed: {err}");
                    });
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Failed to process daily timeline: {e.Message}");
            }
        }
    }
}
