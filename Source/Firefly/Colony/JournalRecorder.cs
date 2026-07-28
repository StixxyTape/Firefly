using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using Verse;

namespace Firefly
{
    // Drives the colony journal: 3-hourly snapshots, hourly buffer drains, and the midnight
    // archive-and-summarise pass.
    //
    // This used to live on StorytellerComp_Fillion, driven from MakeIntervalIncidents. That
    // coupled two unrelated concerns: RimWorld calls MakeIntervalIncidents once per incident
    // target, so shared cadence state there meant only the first map of each 3-hour bucket ever
    // got incidents, and gating the call to once per 3 hours starved the delegated storyteller
    // curve of the ~1000 calls a day it expects. Journal cadence is global, so it belongs on the
    // GameComponent; the storyteller comp now only delegates incidents.
    public class JournalRecorder
    {
        private const int CheckIntervalTicks = 250;
        private const int ArcIntervalDays = 15;

        private readonly ColonyLedger _ledger;

        private bool _enabled;
        private int _tickCounter;
        private int _lastHourBucket = -1;
        private int _lastDrainHour = -1;
        private int _lastArchivedDay = -1;
        private bool _arcInFlight;

        private readonly Queue<(int Day, string Content)> _backfillQueue = new Queue<(int, string)>();
        private string _backfillDir;
        private bool _backfillActive;

        public JournalRecorder(ColonyLedger ledger)
        {
            _ledger = ledger;
        }

        public void SetEnabled(bool enabled) => _enabled = enabled;

        public void ExposeData()
        {
            Scribe_Values.Look(ref _lastHourBucket, "journalLastHourBucket", -1);
            Scribe_Values.Look(ref _lastDrainHour, "journalLastDrainHour", -1);
            Scribe_Values.Look(ref _lastArchivedDay, "journalLastArchivedDay", -1);
        }

        // The journal covers the colony as a whole, not one incident target, so it records the
        // player's home map rather than whichever map the camera happens to be on.
        private static Map ResolveJournalMap()
        {
            var maps = Find.Maps;
            if (maps != null)
                for (int i = 0; i < maps.Count; i++)
                    if (maps[i] != null && maps[i].IsPlayerHome) return maps[i];
            return Find.CurrentMap;
        }

        public void Tick()
        {
            if (!_enabled || _ledger == null) return;

            _tickCounter++;
            if (_tickCounter % CheckIntervalTicks != 0) return;

            try
            {
                Map map = ResolveJournalMap();
                if (map == null) return;

                int hourOfDay = GenLocalDate.HourOfDay(map);
                int hourBucket = hourOfDay / 3;

                // First run — initialize immediately so capture methods start working right away
                if (_lastHourBucket == -1)
                {
                    _lastHourBucket = hourBucket;
                    _lastDrainHour = hourOfDay;
                    _ledger.Record(map, hourOfDay);
                    return;
                }

                // Hourly drain of combat/hazard buffers into the live section files
                if (hourOfDay != _lastDrainHour)
                {
                    _lastDrainHour = hourOfDay;
                    float drainLon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
                    _ledger.DrainAndWriteSections(drainLon);
                }

                if (hourBucket == _lastHourBucket) return;

                if (hourBucket < _lastHourBucket)
                {
                    // Midnight crossed: add the 21→00 snapshot to the closing day, then archive it.
                    int closingDay = _ledger.RecordingDay;
                    _ledger.Record(map, hourOfDay);
                    _lastHourBucket = hourBucket;
                    WriteTimeline(map, closingDay);
                    return;
                }

                _lastHourBucket = hourBucket;
                _ledger.Record(map, hourOfDay);
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Journal tick failed: {e.Message}");
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

        private static string DailySystemPrompt()
        {
            string custom = FireflyMod.Settings.CustomPrompt;
            return !custom.NullOrEmpty() ? custom : SummarySystemPrompt;
        }

        public void WriteTimeline(Map map, int day = -1)
        {
            try
            {
                if (_ledger == null) return;

                if (day < 0) day = _ledger.RecordingDay;
                if (day == _lastArchivedDay) return;

                string dir = _ledger.OutputDir;
                if (dir == null) return;

                // Append health/relations/skills to timeline file before archiving
                string healthSection = _ledger.BuildComparisonSection(map);
                if (!healthSection.NullOrEmpty())
                    _ledger.AppendRawToTimeline("\n" + healthSection);

                // Drain any remaining combat/hazard events before assembling
                float lon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
                _ledger.DrainAndWriteSections(lon);
                _ledger.FlushTimelineBuffer();

                // Assemble daily file from the live files, merging repeated sections
                string timelineContent = ReadFileOrEmpty(Path.Combine(dir, "current_timeline.txt"));
                string combatContent   = MergeCombatSections(ReadFileOrEmpty(Path.Combine(dir, "current_combat_events.txt")));
                string hazardContent   = MergeHazardSections(ReadFileOrEmpty(Path.Combine(dir, "current_hazard_events.txt")));
                string rosterSection   = _ledger.BuildPawnRosterSection();

                _ledger.Clear();

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
                _lastArchivedDay = day;

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
                string fate    = string.Join(", ", g.Select(e => e.Fate).Where(f => !f.NullOrEmpty()).Distinct());
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

        private void SendSummaryRequest(string dir, int day, string content)
        {
            string summaryPath = Path.Combine(dir, $"daily_summary_day{day}.txt");
            Log.Message($"[Firefly] Sending Day {day} to LLM for summary...");
            LLMClient.Send(
                DailySystemPrompt(),
                content,
                onSuccess: summary =>
                {
                    try { File.WriteAllText(summaryPath, summary, Encoding.UTF8); }
                    catch (Exception e) { Log.Warning($"[Firefly] Failed to write summary day {day}: {e.Message}"); }
                    Log.Message($"[Firefly] Daily summary written: Day {day}");
                    ColonyLedger.Current?.InvalidateContextCache();
                    ColonyLedger.Current?.WriteContextFile();
                    MaybeSendArcSummary(dir, day);
                },
                onError: err => Log.Warning($"[Firefly] LLM summary failed for Day {day}: {err}"));
        }

        private static int ReadLastArcDay(string dir)
        {
            int lastArcDay = 0;
            try
            {
                string lastDayPath = Path.Combine(dir, "colony_history_last_day.txt");
                if (File.Exists(lastDayPath))
                    int.TryParse(File.ReadAllText(lastDayPath, Encoding.UTF8).Trim(), out lastArcDay);
            }
            catch { }
            return lastArcDay;
        }

        // Checked on every daily write rather than fired from the day-15 callback. A single failed
        // request used to mean no history update until day 30.
        private void MaybeSendArcSummary(string dir, int throughDay)
        {
            if (_arcInFlight || throughDay < ArcIntervalDays) return;
            if (throughDay - ReadLastArcDay(dir) < ArcIntervalDays) return;
            SendArcSummaryRequest(dir, throughDay);
        }

        private void SendArcSummaryRequest(string dir, int day)
        {
            try
            {
                string arcPath     = Path.Combine(dir, "colony_history.txt");
                string lastDayPath = Path.Combine(dir, "colony_history_last_day.txt");

                int lastArcDay = ReadLastArcDay(dir);

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
                string existingHistory = ReadFileOrEmpty(arcPath);
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
                _arcInFlight = true;
                LLMClient.Send(
                    ArcSystemPrompt,
                    combined,
                    onSuccess: arcText =>
                    {
                        _arcInFlight = false;
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
                    onError: err =>
                    {
                        _arcInFlight = false;
                        Log.Warning($"[Firefly] Colony history LLM failed for Day {day}: {err}");
                    });
            }
            catch (Exception e)
            {
                _arcInFlight = false;
                Log.Warning($"[Firefly] SendArcSummaryRequest failed: {e.Message}");
            }
        }

        // Backfill runs strictly one request at a time — a colony with many gaps would otherwise
        // fire a request per missing day simultaneously and hit rate limits.
        private void BackfillMissingSummaries(string dir, int excludeDay)
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

        private void ProcessBackfillQueue()
        {
            if (_backfillQueue.Count == 0)
            {
                _backfillActive = false;
                // Gaps are now filled, so an overdue history update has the summaries it needs.
                if (!_backfillDir.NullOrEmpty() && _lastArchivedDay > 0)
                    MaybeSendArcSummary(_backfillDir, _lastArchivedDay);
                return;
            }

            var (day, content) = _backfillQueue.Dequeue();
            string summaryPath = Path.Combine(_backfillDir, $"daily_summary_day{day}.txt");

            Log.Message($"[Firefly] Backfilling Day {day} ({_backfillQueue.Count} remaining)...");
            LLMClient.Send(
                DailySystemPrompt(),
                content,
                onSuccess: summary =>
                {
                    try { File.WriteAllText(summaryPath, summary, Encoding.UTF8); }
                    catch (Exception e) { Log.Warning($"[Firefly] Failed to write summary day {day}: {e.Message}"); }
                    ColonyLedger.Current?.InvalidateContextCache();
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
