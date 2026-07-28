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

            var ledger = ColonyLedger.Current;
            if (ledger == null) yield break;

            // First tick — initialize immediately so capture methods start working right away
            if (lastHourBucket == -1)
            {
                lastHourBucket = hourBucket;
                lastDrainHour  = hourOfDay;
                ledger.SetOutputDir(FireflyGameComponent.GetOutputDir());
                ledger.Record(map, hourOfDay);
                yield break;
            }

            // Hourly drain of combat/hazard buffers into the live section files
            if (hourOfDay != lastDrainHour)
            {
                lastDrainHour = hourOfDay;
                float drainLon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
                ledger.DrainAndWriteSections(drainLon);
            }

            if (hourBucket == lastHourBucket) yield break;

            if (hourBucket < lastHourBucket)
            {
                // Midnight crossed: add the 21→00 snapshot to the closing day, then archive it.
                int closingDay = ledger.RecordingDay;
                ledger.Record(map, hourOfDay);
                WriteTimeline(map, closingDay);
                lastHourBucket = hourBucket;
                yield break;
            }

            lastHourBucket = hourBucket;
            ledger.Record(map, hourOfDay);

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

        private static readonly string SummarySystemPrompt =
            "You are Fillion, the keeper of a colony's journal. You receive the day's log: " +
            "each colonist's activities, health, mood, and any conversations or events.\n\n" +
            "Write a short summary of the day, a sentence or two per colonist, saying what " +
            "each one mainly got up to — the overall shape of their day, not a timeline of " +
            "every action. Use specifics from the log (\"researching drug production\", not " +
            "\"did research\"). Mention health or mood only when it actually mattered. Then " +
            "note anything important that happened between colonists and how it landed.\n\n" +
            "Keep it plain and warm, like telling a friend what everyone did today. No drama, " +
            "no metaphors, no lists. Keep your summary to 5 lines max.";

        private static void WriteTimeline(Map map, int day = -1)
        {
            try
            {
                var ledger = ColonyLedger.Current;
                if (ledger == null) return;

                if (day < 0) day = ledger.RecordingDay;
                string dir = FireflyGameComponent.GetOutputDir();

                // Append health/relations/skills to timeline file before archiving
                string healthSection = ledger.BuildComparisonSection(map);
                if (!healthSection.NullOrEmpty())
                    ledger.AppendRawToTimeline("\n" + healthSection);

                // Drain any remaining combat/hazard events before assembling
                float lon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
                ledger.DrainAndWriteSections(lon);
                ledger.FlushTimelineBuffer();

                // Assemble daily file from the live files, merging repeated sections
                string timelineContent = ReadFileOrEmpty(Path.Combine(dir, "current_timeline.txt"));
                string combatContent   = MergeCombatSections(ReadFileOrEmpty(Path.Combine(dir, "current_combat_events.txt")));
                string hazardContent   = MergeHazardSections(ReadFileOrEmpty(Path.Combine(dir, "current_hazard_events.txt")));
                string rosterSection   = ledger.BuildPawnRosterSection();

                ledger.Clear();

                // Insert pawn roster between the day header and === EVENTS ===
                if (!rosterSection.NullOrEmpty())
                {
                    const string eventsMarker = "=== EVENTS ===";
                    int idx = timelineContent.IndexOf(eventsMarker, StringComparison.Ordinal);
                    if (idx >= 0)
                        timelineContent = timelineContent.Substring(0, idx) + rosterSection + "\n" + timelineContent.Substring(idx);
                    else
                        timelineContent = rosterSection + "\n" + timelineContent;
                }

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
                sb.AppendLine($"  - {line}");
            }
            return sb.ToString();
        }

        private static string ReadFileOrEmpty(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : ""; }
            catch { return ""; }
        }

        private static readonly string ArcSystemPrompt =
            "You are Fillion, the keeper of a colony's journal. You receive the current colony " +
            "history (if one exists) followed by recent daily summaries that have not yet been " +
            "folded in.\n\n" +
            "Rewrite the colony history as a single updated document: 10 lines capturing the " +
            "most important narrative moments, character arcs, ongoing story threads, and the " +
            "overall shape of the colony's life so far. Carry forward what still matters from " +
            "the existing history and weave in what's new.\n\n" +
            "No lists, no timestamps, no section headers — just a flowing narrative, plain and " +
            "warm, as if telling someone who's been away everything they need to know about " +
            "this colony's story.";

        private static void SendSummaryRequest(string dir, int day, string content)
        {
            string summaryPath = Path.Combine(dir, $"daily_summary_day{day}.txt");
            bool triggerArc = day >= 15 && day % 15 == 0;
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
                    ColonyLedger.Current?.InvalidateContextCache();
                    ColonyLedger.Current?.WriteContextFile();
                    if (triggerArc) SendArcSummaryRequest(dir, day);
                },
                onError: err => Log.Warning($"[Firefly] LLM summary failed for Day {day}: {err}"));
        }

        private static void SendArcSummaryRequest(string dir, int day)
        {
            try
            {
                string arcPath      = Path.Combine(dir, "colony_history.txt");
                string lastDayPath  = Path.Combine(dir, "colony_history_last_day.txt");

                // Determine which daily summaries are new since the last arc update
                int lastArcDay = 0;
                if (File.Exists(lastDayPath))
                    int.TryParse(File.ReadAllText(lastDayPath, Encoding.UTF8).Trim(), out lastArcDay);

                var newSummaries = Directory.GetFiles(dir, "daily_summary_day*.txt")
                    .Select(f =>
                    {
                        string stem   = Path.GetFileNameWithoutExtension(f);
                        string dayStr = stem.Substring("daily_summary_day".Length);
                        return int.TryParse(dayStr, out int d) ? (d, f) : (-1, f);
                    })
                    .Where(x => x.Item1 > lastArcDay && x.Item1 <= day)
                    .OrderBy(x => x.Item1)
                    .ToList();

                if (newSummaries.Count == 0) return;

                var sb = new StringBuilder();

                // Prepend existing history so the LLM can carry it forward
                string existingHistory = File.Exists(arcPath)
                    ? ReadFileOrEmpty(arcPath)
                    : null;
                if (!existingHistory.NullOrEmpty())
                {
                    sb.AppendLine("=== EXISTING COLONY HISTORY ===");
                    sb.AppendLine(existingHistory);
                    sb.AppendLine();
                }

                sb.AppendLine("=== RECENT DAILY SUMMARIES ===");
                foreach (var (d, path) in newSummaries)
                {
                    string text;
                    try { text = File.ReadAllText(path, Encoding.UTF8); }
                    catch { continue; }
                    if (text.NullOrEmpty()) continue;
                    sb.AppendLine($"=== Day {d} ===");
                    sb.AppendLine(text);
                    sb.AppendLine();
                }

                string combined = sb.ToString();
                if (combined.NullOrEmpty()) return;

                Log.Message($"[Firefly] Updating colony history through Day {day}...");
                LLMClient.Send(
                    ArcSystemPrompt,
                    combined,
                    onSuccess: arcText =>
                    {
                        try
                        {
                            File.WriteAllText(arcPath, arcText, Encoding.UTF8);
                            File.WriteAllText(lastDayPath, day.ToString(), Encoding.UTF8);
                        }
                        catch (Exception e) { Log.Warning($"[Firefly] Failed to write colony history: {e.Message}"); }
                        Log.Message($"[Firefly] Colony history updated through Day {day}");
                        ColonyLedger.Current?.InvalidateContextCache();
                        ColonyLedger.Current?.WriteContextFile();
                    },
                    onError: err => Log.Warning($"[Firefly] Colony history LLM failed for Day {day}: {err}"));
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] SendArcSummaryRequest failed: {e.Message}");
            }
        }

        // Backfill runs strictly one request at a time — a colony with many gaps would otherwise
        // fire a request per missing day simultaneously and hit rate limits.
        private static readonly Queue<(int Day, string Content)> _backfillQueue = new Queue<(int, string)>();
        private static string _backfillDir;
        private static bool _backfillActive;

        private static void BackfillMissingSummaries(string dir, int excludeDay)
        {
            if (_backfillActive) return;
            try
            {
                _backfillQueue.Clear();
                _backfillDir = dir;

                var pending = new List<(int Day, string Content)>();
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
                    pending.Add((day, content));
                }

                if (pending.Count == 0) return;

                foreach (var item in pending.OrderBy(p => p.Day))
                    _backfillQueue.Enqueue(item);

                Log.Message($"[Firefly] Backfilling {_backfillQueue.Count} missing summaries, one at a time...");
                _backfillActive = true;
                ProcessBackfillQueue();
            }
            catch (Exception e)
            {
                _backfillActive = false;
                Log.Warning($"[Firefly] BackfillMissingSummaries failed: {e.Message}");
            }
        }

        private static void ProcessBackfillQueue()
        {
            if (_backfillQueue.Count == 0) { _backfillActive = false; return; }

            var (day, content) = _backfillQueue.Dequeue();
            string summaryPath = Path.Combine(_backfillDir, $"daily_summary_day{day}.txt");
            string custom = FireflyMod.Settings.CustomPrompt;
            string systemPrompt = !custom.NullOrEmpty() ? custom : SummarySystemPrompt;

            Log.Message($"[Firefly] Backfilling Day {day} ({_backfillQueue.Count} remaining)...");
            LLMClient.Send(
                systemPrompt,
                content,
                onSuccess: summary =>
                {
                    try { File.WriteAllText(summaryPath, summary, Encoding.UTF8); }
                    catch (Exception e) { Log.Warning($"[Firefly] Failed to write summary day {day}: {e.Message}"); }
                    ColonyLedger.Current?.WriteContextFile();
                    ProcessBackfillQueue();
                },
                onError: err =>
                {
                    Log.Warning($"[Firefly] Backfill failed for Day {day}: {err}");
                    ProcessBackfillQueue();
                });
        }
    }
}
