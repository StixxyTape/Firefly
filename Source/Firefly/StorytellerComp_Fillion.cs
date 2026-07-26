using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
        private int lastDrainHour  = -1;

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
                lastDrainHour  = hourOfDay;
                ColonyLedger.SetOutputDir(GetOutputDir());
                ColonyLedger.LoadPendingEvents();
                ColonyLedger.Record(map, hourOfDay);
                FlushLedger(map);
                yield break;
            }

            // Hourly drain of combat/hazard buffers into the live section files
            if (hourOfDay != lastDrainHour)
            {
                lastDrainHour = hourOfDay;
                float drainLon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
                ColonyLedger.DrainAndWriteSections(drainLon);
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
                    ColonyLedger.AppendRawToTimeline("\n" + healthSection);

                // Drain any remaining combat/hazard events before assembling
                float lon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
                ColonyLedger.DrainAndWriteSections(lon);

                // Assemble daily file from the live files, merging repeated sections
                string timelineContent = ReadFileOrEmpty(Path.Combine(dir, "current_timeline.txt"));
                string combatContent   = MergeCombatSections(ReadFileOrEmpty(Path.Combine(dir, "current_combat_events.txt")));
                string hazardContent   = MergeHazardSections(ReadFileOrEmpty(Path.Combine(dir, "current_hazard_events.txt")));
                ColonyLedger.Clear();

                string fullContent = string.Concat(
                    timelineContent,
                    combatContent.NullOrEmpty() ? "" : "\n" + combatContent,
                    hazardContent.NullOrEmpty() ? "" : "\n" + hazardContent);

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

        // [HH:MM] {desc} {N} time(s)[ ({X} did damage)].
        private static readonly Regex HazardEntryRx = new Regex(
            @"^\s*-\s+\[(\d+):(\d+)\]\s+(.+?)\s+(\d+)\s+times?(?:\s+\((\d+)\s+did\s+damage\))?\.?\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        // [HH:MM] {col} — fought {opponents}.[rest]
        private static readonly Regex CombatEntryRx = new Regex(
            @"^\s*-\s+\[(\d+):(\d+)\]\s+(.+?)\s+—\s+fought\s+(.+?)\.(.*)$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex HitsRx = new Regex(
            @"took\s+(\d+)\s+hits?", RegexOptions.Compiled);

        private static string MergeHazardSections(string raw)
        {
            if (raw.NullOrEmpty()) return raw;
            var entries = new List<(int HM, int H, int M, string Desc, int Count, int Dmg)>();
            foreach (Match m in HazardEntryRx.Matches(raw))
            {
                int h = int.Parse(m.Groups[1].Value), mn = int.Parse(m.Groups[2].Value);
                entries.Add((h * 60 + mn, h, mn,
                    m.Groups[3].Value.Trim(),
                    int.Parse(m.Groups[4].Value),
                    m.Groups[5].Success ? int.Parse(m.Groups[5].Value) : 0));
            }
            if (entries.Count == 0) return "";
            var sb = new StringBuilder("=== HAZARDS ===\n");
            foreach (var g in entries.GroupBy(e => e.Desc).OrderBy(g => g.Min(e => e.HM)))
            {
                var   first  = g.OrderBy(e => e.HM).First();
                int   total  = g.Sum(e => e.Count);
                int   dmg    = g.Sum(e => e.Dmg);
                string line  = $"{g.Key} {total} time{(total == 1 ? "" : "s")}";
                if (dmg > 0 && dmg < total) line += $" ({dmg} did damage)";
                sb.AppendLine($"  - [{first.H:D2}:{first.M:D2}] {line}.");
            }
            return sb.ToString();
        }

        private static string MergeCombatSections(string raw)
        {
            if (raw.NullOrEmpty()) return raw;
            var entries = new List<(int HM, int H, int M, string Col, List<string> Opps, int Hits, string Fate)>();
            foreach (Match m in CombatEntryRx.Matches(raw))
            {
                int h = int.Parse(m.Groups[1].Value), mn = int.Parse(m.Groups[2].Value);
                // Split on ", " then also on " and " to handle "A, B and C" → ["A","B","C"]
                var opps = m.Groups[4].Value
                    .Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)
                    .SelectMany(s => s.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries))
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                string rest = m.Groups[5].Value.Trim().TrimStart('.');
                int hits = 0;
                var hm = HitsRx.Match(rest);
                if (hm.Success)
                {
                    hits = int.Parse(hm.Groups[1].Value);
                    rest = (rest.Substring(0, hm.Index) + rest.Substring(hm.Index + hm.Length))
                           .Trim().TrimStart(',').TrimEnd('.').Trim();
                }
                else
                {
                    rest = rest.TrimEnd('.').Trim();
                }
                entries.Add((h * 60 + mn, h, mn, m.Groups[3].Value.Trim(), opps, hits, rest));
            }
            if (entries.Count == 0) return "";
            var sb = new StringBuilder("=== COMBAT ===\n");
            foreach (var g in entries.GroupBy(e => e.Col).OrderBy(g => g.Min(e => e.HM)))
            {
                var   first    = g.OrderBy(e => e.HM).First();
                var   allOpps  = g.SelectMany(e => e.Opps).Distinct().ToList();
                int   hits     = g.Sum(e => e.Hits);
                string fate    = g.Select(e => e.Fate).FirstOrDefault(f => !f.NullOrEmpty()) ?? "";
                string line    = $"{g.Key} — fought {string.Join(", ", allOpps)}.";
                var   parts    = new List<string>();
                if (hits > 0)         parts.Add($"took {hits} hit{(hits == 1 ? "" : "s")}");
                if (!fate.NullOrEmpty()) parts.Add(fate.TrimEnd('.'));
                if (parts.Any())      line += $" {string.Join(", ", parts)}.";
                sb.AppendLine($"  - [{first.H:D2}:{first.M:D2}] {line}");
            }
            return sb.ToString();
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
