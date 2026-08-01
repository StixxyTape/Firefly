using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace Firefly
{
    // Drives the colony journal: 3-hourly snapshots, hourly buffer drains, and the midnight
    // archive-and-summarise pass. All narrative data now lives in-memory via the save file —
    // no text files on disk.
    public class JournalRecorder
    {
        private const int CheckIntervalTicks = 250;
        private const int ArcIntervalDays    = 15;

        private readonly ColonyLedger _ledger;

        private bool _enabled;
        private int  _tickCounter;
        private int  _lastHourBucket  = -1;
        private int  _lastDrainHour   = -1;
        private int  _lastArchivedDay = -1;
        private bool _arcInFlight;

        private readonly Queue<DailyRecord> _backfillQueue = new Queue<DailyRecord>();
        private bool _backfillActive;

        public JournalRecorder(ColonyLedger ledger)
        {
            _ledger = ledger;
        }

        public void SetEnabled(bool enabled) => _enabled = enabled;

        public void ExposeData()
        {
            Scribe_Values.Look(ref _lastHourBucket,  "journalLastHourBucket",  -1);
            Scribe_Values.Look(ref _lastDrainHour,   "journalLastDrainHour",   -1);
            Scribe_Values.Look(ref _lastArchivedDay, "journalLastArchivedDay", -1);
        }

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

                int hourOfDay  = GenLocalDate.HourOfDay(map);
                int hourBucket = hourOfDay / 3;

                if (_lastHourBucket == -1)
                {
                    _lastHourBucket = hourBucket;
                    _lastDrainHour  = hourOfDay;
                    _ledger.Record(map, hourOfDay);
                    return;
                }

                if (hourOfDay != _lastDrainHour)
                {
                    _lastDrainHour = hourOfDay;
                    float drainLon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
                    _ledger.DrainToBuffers(drainLon);
                }

                if (hourBucket == _lastHourBucket) return;

                if (hourBucket < _lastHourBucket)
                {
                    int closingDay = _ledger.RecordingDay;
                    _lastHourBucket = hourBucket;
                    WriteTimeline(map, closingDay);  // archive + clear first
                    _ledger.Record(map, hourOfDay);  // then write new day header to fresh buffer
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
            "each colonist's activities, health, mood, conversations, and events.\n\n" +
            "Tell the day as a short story — find what it was really about and open with that, " +
            "then let the rest follow. Lead with anything that changed the colony for good (a " +
            "death, a birth, someone joining or leaving, a title earned, a faction turned, a " +
            "quest won or lost); these carry the day. Routine — labour, chitchat, weather, " +
            "skill ticks — is texture: choose a few telling details and let them evoke the day's " +
            "feel, don't list them.\n\n" +
            "Write warm and literary, but never invent. Every feeling, motive, or meaning must " +
            "be earned by something in the log — if a bond deepened, the log shows the talk that " +
            "did it. Name a colonist's age or species only when it bears on what happened.\n\n" +
            "Plain past tense, flowing prose. No lists. Five lines max.";

        private static readonly string ArcSystemPrompt =
            "You are Fillion, the keeper of a colony's journal. You receive the current colony " +
            "history (if one exists) followed by recent daily summaries not yet folded in.\n\n" +
            "Rewrite the colony history as a single updated document: 10 lines capturing the " +
            "colony's story so far. Carry forward what still matters and weave in what's new.\n\n" +
            "Give the words to events that changed something for good — deaths, births, quests " +
            "won or lost, titles and status earned, factions turned friend or enemy, major " +
            "discoveries, and the arcs between colonists. Routine — daily labour, wildlife " +
            "scraps, chitchat, weather — is the rhythm underneath; keep a little for texture, " +
            "but never let it crowd out a real event. A rare event outweighs a frequent one, " +
            "even when the routine filled more of the log.\n\n" +
            "Where a story thread is still unresolved, leave it open on the page so its future " +
            "is felt — a quest not yet answered, a signal not yet followed, a rift not yet healed.\n\n" +
            "No lists, no timestamps, no section headers — just flowing narrative, plain and " +
            "warm, as if telling someone who's been away everything they need to know.";

        private static string DailySystemPrompt() => SummarySystemPrompt;

        public void WriteTimeline(Map map, int day = -1)
        {
            try
            {
                if (_ledger == null) return;
                if (day < 0) day = _ledger.RecordingDay;
                if (day == _lastArchivedDay) return;

                // Append health/relations/skills snapshot
                string healthSection = _ledger.BuildComparisonSection(map);
                if (!healthSection.NullOrEmpty())
                    _ledger.AppendRawToTimeline("\n" + healthSection);

                // Final drain of combat/hazard buffers
                float lon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
                var (combatContent, hazardContent) = _ledger.FlushDrainedSections(lon);

                string rosterSection   = _ledger.BuildPawnRosterSection();
                string timelineContent = _ledger.GetCurrentDayContent();

                string questSnapshot = _ledger.BuildMentionedQuestsSnapshot();
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

                string questContent = questSnapshot.NullOrEmpty() ? "" : "=== QUESTS ===\n" + questSnapshot;

                string fullContent = string.Concat(
                    timelineContent,
                    combatContent.NullOrEmpty() ? "" : "\n" + combatContent,
                    hazardContent.NullOrEmpty() ? "" : "\n" + hazardContent,
                    questContent.NullOrEmpty() ? "" : "\n" + questContent);

                if (fullContent.NullOrEmpty()) return;

                _lastArchivedDay = day;

                var record = new DailyRecord { Day = day, Timeline = fullContent, QuestSnapshot = questSnapshot };
                _ledger.AddDailyRecord(record);

                SendSummaryRequest(day, fullContent);
                BackfillMissingSummaries(excludeDay: day);
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Failed to process daily timeline: {e.Message}");
            }
        }

        private void SendSummaryRequest(int day, string content)
        {
            Log.Message($"[Firefly] Sending Day {day} to LLM for summary...");

            string prevSummary = _ledger?.PastDays
                .LastOrDefault(d => d.Day == day - 1 && !d.Summary.NullOrEmpty())
                ?.Summary;

            string prompt = prevSummary.NullOrEmpty()
                ? content
                : $"=== PREVIOUS DAY SUMMARY (context only — do not summarise this) ===\n{prevSummary.Trim()}\n\n{content}";

            LLMClient.Send(
                DailySystemPrompt(),
                prompt,
                onSuccess: summary =>
                {
                    ColonyLedger.Current?.SetDailySummary(day, summary);
                    Log.Message($"[Firefly] Daily summary written: Day {day}");
                    MaybeSendArcSummary(day);
                },
                onError: err => Log.Warning($"[Firefly] LLM summary failed for Day {day}: {err}"));
        }

        private void MaybeSendArcSummary(int throughDay)
        {
            if (_arcInFlight || throughDay < ArcIntervalDays) return;
            if (throughDay - (_ledger?.LastArcDay ?? 0) < ArcIntervalDays) return;
            SendArcSummaryRequest(throughDay);
        }

        private void SendArcSummaryRequest(int day)
        {
            try
            {
                if (_ledger == null) return;

                int lastArcDay = _ledger.LastArcDay;
                var newSummaries = _ledger.PastDays
                    .Where(d => d.Day > lastArcDay && d.Day <= day && !d.Summary.NullOrEmpty())
                    .OrderBy(d => d.Day)
                    .ToList();

                if (newSummaries.Count == 0) return;

                var sb = new StringBuilder();

                string existingHistory = _ledger.ColonyHistory;
                if (!existingHistory.NullOrEmpty())
                {
                    sb.AppendLine("=== EXISTING COLONY HISTORY ===");
                    sb.AppendLine(existingHistory);
                    sb.AppendLine();
                }

                sb.AppendLine("=== RECENT DAILY SUMMARIES ===");
                foreach (var record in newSummaries)
                {
                    sb.AppendLine($"=== Day {record.Day} ===");
                    sb.AppendLine(record.Summary);
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
                        ColonyLedger.Current?.SetColonyHistory(arcText, day);
                        Log.Message($"[Firefly] Colony history updated through Day {day}");
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

        private void BackfillMissingSummaries(int excludeDay)
        {
            if (_backfillActive || _ledger == null) return;

            var pending = _ledger.PastDays
                .Where(d => d.Day != excludeDay && d.Summary.NullOrEmpty() && !d.Timeline.NullOrEmpty())
                .OrderBy(d => d.Day)
                .ToList();

            if (pending.Count == 0) return;

            _backfillQueue.Clear();
            foreach (var record in pending) _backfillQueue.Enqueue(record);

            Log.Message($"[Firefly] Backfilling {_backfillQueue.Count} missing summaries, one at a time...");
            _backfillActive = true;
            ProcessBackfillQueue();
        }

        private void ProcessBackfillQueue()
        {
            if (_backfillQueue.Count == 0)
            {
                _backfillActive = false;
                if (_lastArchivedDay > 0) MaybeSendArcSummary(_lastArchivedDay);
                return;
            }

            var record = _backfillQueue.Dequeue();
            Log.Message($"[Firefly] Backfilling Day {record.Day} ({_backfillQueue.Count} remaining)...");
            LLMClient.Send(
                DailySystemPrompt(),
                record.Timeline,
                onSuccess: summary =>
                {
                    ColonyLedger.Current?.SetDailySummary(record.Day, summary);
                    ProcessBackfillQueue();
                },
                onError: err =>
                {
                    Log.Warning($"[Firefly] Backfill failed for Day {record.Day}: {err}");
                    ProcessBackfillQueue();
                });
        }
    }
}
