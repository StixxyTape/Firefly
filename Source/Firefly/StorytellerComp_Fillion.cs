using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RimWorld;
using Verse;
using System.Linq;

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

        private List<StorytellerComp> _delegateComps;
        private string _lastCurve;

        private List<StorytellerComp> GetDelegateComps()
        {
            string curve = FireflyMod.Settings.IncidentCurve ?? "Cassandra";
            if (_delegateComps != null && _lastCurve == curve) return _delegateComps;

            _lastCurve = curve;
            _delegateComps = new List<StorytellerComp>();
            if (curve == "None") return _delegateComps;

            var def = DefDatabase<StorytellerDef>.GetNamedSilentFail(curve);
            if (def?.comps == null) return _delegateComps;

            foreach (var p in def.comps)
            {
                try
                {
                    var comp = (StorytellerComp)Activator.CreateInstance(p.compClass);
                    comp.props = p;
                    comp.Initialize();
                    _delegateComps.Add(comp);
                }
                catch (Exception e)
                {
                    Log.Warning($"[Firefly] Failed to init delegate comp {p.compClass?.Name}: {e.Message}");
                }
            }

            Log.Message($"[Firefly] Incident curve: {curve} ({_delegateComps.Count} comps loaded)");
            return _delegateComps;
        }

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            if (!(target is Map)) yield break;

            Map map = Find.CurrentMap;
            if (map == null) yield break;

            int hourOfDay  = GenLocalDate.HourOfDay(map);
            int hourBucket = hourOfDay / 3;

            // First tick — initialize immediately so capture methods start working right away
            if (lastHourBucket == -1)
            {
                lastHourBucket = hourBucket;
                ColonyLedger.SetOutputDir(GetOutputDir());
                ColonyLedger.LoadPendingEvents();
                ColonyLedger.Record(map, hourOfDay);
                FlushLedger(map);
                yield break;
            }

            if (hourBucket == lastHourBucket) yield break;

            if (hourBucket < lastHourBucket)
            {
                // Midnight crossed: add the 21→00 snapshot to the closing day, then archive it.
                int closingDay = ColonyLedger.RecordingDay;
                ColonyLedger.Record(map, hourOfDay);
                WriteTimeline(map, closingDay);
                lastHourBucket = hourBucket;
                yield break;
            }

            lastHourBucket = hourBucket;
            ColonyLedger.Record(map, hourOfDay);
            FlushLedger(map);

            // Delegate incident firing to the selected storyteller curve
            foreach (var comp in GetDelegateComps())
            {
                IEnumerable<FiringIncident> incidents = null;
                try { incidents = comp.MakeIntervalIncidents(target); } catch { }
                if (incidents == null) continue;
                foreach (var fi in incidents)
                    if (fi != null) yield return fi;
            }
        }

        private static string GetOutputDir()
        {
            // permadeathModeUniqueName is the save file's base name — unique per save slot, not per world
            string saveName = Current.Game?.Info?.permadeathModeUniqueName;

            if (saveName.NullOrEmpty()) saveName = "unknown";

            string safeName = string.Concat(
                saveName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

            string dir = Path.Combine(GenFilePaths.ConfigFolderPath, "Firefly", safeName);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void FlushLedger(Map map)
        {
            ColonyLedger.TryLoadPrevDayComparisons(Path.Combine(GetOutputDir(), "prev_day_comparisons.txt"));
        }

        private static readonly string SummarySystemPrompt =
            "You are Fillion, the keeper of a colony's journal. You receive the day's log: " +
            "each colonist's activities, health, mood, and any conversations or events.\n\n" +
            "Write a short summary of the day, a sentence or two per colonist, saying what " +
            "each one mainly got up to — the overall shape of their day, not a timeline of " +
            "every action. Use specifics from the log (\"researching drug production\", not " +
            "\"did research\"). Mention health or mood only when it actually mattered. Then " +
            "note anything important that happened between colonists and how it landed.\n\n" +
            "Keep it plain and warm, like telling a friend what everyone did today. No drama, " +
            "no metaphors, no lists.";

        private static void WriteTimeline(Map map, int day = -1)
        {
            try
            {
                if (day < 0) day = ColonyLedger.RecordingDay;
                string dir = GetOutputDir();

                // Append health/relations/skills to timeline file before archiving
                string healthSection = ColonyLedger.BuildComparisonSection(map);
                ColonyLedger.SavePrevDayComparisons(Path.Combine(dir, "prev_day_comparisons.txt"));
                if (!healthSection.NullOrEmpty())
                    ColonyLedger.AppendRawToTimeline(healthSection);

                // Assemble daily file from the three live files
                string timelineContent = ReadFileOrEmpty(Path.Combine(dir, "current_timeline.txt"));
                string combatContent   = ReadFileOrEmpty(Path.Combine(dir, "current_combat_events.txt"));
                string hazardContent   = ReadFileOrEmpty(Path.Combine(dir, "current_hazard_events.txt"));

                ColonyLedger.Clear();

                string fullContent = string.Concat(
                    timelineContent,
                    combatContent.NullOrEmpty()  ? "" : "\n" + combatContent,
                    hazardContent.NullOrEmpty()  ? "" : "\n" + hazardContent);

                if (fullContent.NullOrEmpty()) return;

                string dailyDir = Path.Combine(dir, "daily records");
                Directory.CreateDirectory(dailyDir);
                File.WriteAllText(Path.Combine(dailyDir, $"daily_timeline_day{day}.txt"), fullContent, Encoding.UTF8);

                SendSummaryRequest(dailyDir, day, fullContent);
                BackfillMissingSummaries(dailyDir, excludeDay: day);
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Failed to process daily timeline: {e.Message}");
            }
        }

        private static string ReadFileOrEmpty(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : ""; }
            catch { return ""; }
        }

        private static void SendSummaryRequest(string dir, int day, string content)
        {
            string summaryPath = Path.Combine(dir, $"daily_summary_day{day}.txt");
            string custom = FireflyMod.Settings.CustomPrompt;
            string systemPrompt = !custom.NullOrEmpty() ? custom : SummarySystemPrompt;
            Log.Message($"[Firefly] Sending Day {day} to LLM for summary...");
            LLMClient.Send(
                systemPrompt,
                content,
                onSuccess: summary =>
                {
                    try { File.WriteAllText(summaryPath, summary, Encoding.UTF8); }
                    catch (Exception e) { Log.Warning($"[Firefly] Failed to write summary day {day}: {e.Message}"); }
                    Log.Message($"[Firefly] Daily summary written: Day {day}");
                },
                onError: err => Log.Warning($"[Firefly] LLM summary failed for Day {day}: {err}"));
        }

        private static void BackfillMissingSummaries(string dir, int excludeDay)
        {
            try
            {
                foreach (var timelinePath in Directory.GetFiles(dir, "daily_timeline_day*.txt"))
                {
                    string stem = Path.GetFileNameWithoutExtension(timelinePath);
                    string dayStr = stem.Substring("daily_timeline_day".Length);
                    if (!int.TryParse(dayStr, out int day) || day == excludeDay) continue;

                    string summaryPath = Path.Combine(dir, $"daily_summary_day{day}.txt");
                    if (File.Exists(summaryPath)) continue;

                    string content;
                    try { content = File.ReadAllText(timelinePath, Encoding.UTF8); }
                    catch { continue; }

                    if (content.NullOrEmpty()) continue;

                    Log.Message($"[Firefly] Backfilling missing summary for Day {day}...");
                    SendSummaryRequest(dir, day, content);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] BackfillMissingSummaries failed: {e.Message}");
            }
        }
    }
}
