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

        // Writing through _ledger directly (not ColonyLedger.Current) already stops a stale
        // callback from corrupting whichever save is now loaded — but without this check it
        // would still silently keep working (and spending API calls) for a save that's no
        // longer active. Gates every callback that would otherwise chain into more LLM work.
        private bool IsStillActive => ReferenceEquals(ColonyLedger.Current, _ledger);

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
            "You are Fillion, narrator of a colony on a distant earth-like rimworld. You will " +
            "be given yesterday's events solely for context, and then a detailed log of what " +
            "happened today. Your job is to summarise today's log into a short piece of focused " +
            "writing, detailing what happened.\n\n" +
            "Write the summary as the colony's story. The colonists try to be united for the " +
            "benefit of the colony, though they aren't always aligned with each-other and the " +
            "wellbeing of the colony. Routine labour, chitchat, minor social changes, weather, " +
            "and other small details should serve as underlying texture.\n\n" +
            "The opening should set the day's scene - an image or feeling, drawn from the day, " +
            "where the colony has been, where it stands now, or where it's heading. Events " +
            "outside of the routine - combat, quests, allies and enemies, life, death, any " +
            "changes to the overall state of the colony, etc. - should get more focus.\n\n" +
            "You should always explain things with causality - if something out of the ordinary " +
            "happens, say when it happened and, if the reason is given - why.\n\n" +
            "Try to vary how you open and close today's summary compared to yesterday's.\n\n" +
            "The log gives you figures - opinion scores, mood percentages, medical percentages. " +
            "Use them to understand what's happening, but never state them directly. Translate " +
            "them into plain description: a soured mood, a warming bond, an illness becoming terminal.\n\n" +
            "You should also be curious about things. Occasionally pose questions about events " +
            "that have vague circumstances, potential consequences, or could lead to greater " +
            "threads throughout the world. You are invested in the future of this story, along " +
            "with how it's played out so far.\n\n" +
            "Here's an example output summary. It's just a general outline of the type of " +
            "writing you should aim for - don't follow it strictly.\n\n" +
            "\"_It rained all day. Dickson and Yuriy set out to scavenge steel and machinery " +
            "from a ship wreckage that crashed nearby in the morning. Perhaps a battle recently " +
            "took place? The colony also agreed to help some passing vagabonds by donating them " +
            "a chunk of the colony's silver - a favour that the vagabonds hopefully soon won't " +
            "forget as they continue across the land. Anfisa, a friendly inventor, stopped by " +
            "as dusk fell. Perhaps to scout, perhaps just to introduce herself as a friend " +
            "rather than foe. Either way, it seems like the colony has started to attract " +
            "attention. \"\n\n" +
            "8 Lines maximum.";

        private static readonly string ArcSystemPrompt =
            "You are Fillion, chronicler of a colony on a distant earth-like rimworld. You will be " +
            "given the colony's history so far (if one exists), followed by recent daily summaries " +
            "not yet folded in. Your job is to rewrite the history into a single updated account of " +
            "the colony's story.\n\n" +
            "Write it as the colony's story. The history is the throughline — the events that shaped " +
            "where the colony stands now. Carry forward what still matters, weave in what's new, and " +
            "let old routine fade as fresh events take its place. Deaths, arrivals, quests, titles, " +
            "allies and enemies, and the threads running between them are the substance; daily labour, " +
            "weather, and passing chatter are not.\n\n" +
            "Follow each thread to where it stands. When something began and later ended — a creature " +
            "tamed then lost, a prisoner taken, a title earned — say what became of it. Where a thread " +
            "is still open, leave it open, so its future is felt.\n\n" +
            "You should be curious and invested in this story. Note how the wider world relates to the " +
            "colony — who has visited, who has noticed them, who they've traded with or fought — so " +
            "the world beyond feels present and alive.\n\n" +
            "Keep it to a single flowing account, plain past-tense prose. No lists. 10 lines maximum.";

        private static string DailySystemPrompt() => SummarySystemPrompt;

        public void WriteTimeline(Map map, int day = -1)
        {
            try
            {
                if (_ledger == null) return;
                if (day < 0) day = _ledger.RecordingDay;
                if (day == _lastArchivedDay) return;

                // Colony status snapshot (prepended at top of fullContent later; skip day 0)
                string colonyStatus = day > 0 ? _ledger.BuildColonyStatusSection(map) : "";

                // Append health/relations/skills snapshot
                string healthSection = _ledger.BuildComparisonSection(map);
                if (!healthSection.NullOrEmpty())
                    _ledger.AppendRawToTimeline("\n" + healthSection);

                // Final drain of combat/hazard buffers
                float lon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
                var (combatContent, hazardContent) = _ledger.FlushDrainedSections(lon);

                _ledger.EnsureCaptivesIntroduced(map);
                string rosterSection   = _ledger.BuildPawnRosterSection();
                string timelineContent = _ledger.GetCurrentDayContent();

                string questSnapshot = _ledger.BuildMentionedQuestsSnapshot();

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
                    colonyStatus.NullOrEmpty() ? "" : colonyStatus + "\n",
                    timelineContent,
                    combatContent.NullOrEmpty() ? "" : "\n" + combatContent,
                    hazardContent.NullOrEmpty() ? "" : "\n" + hazardContent,
                    questContent.NullOrEmpty() ? "" : "\n" + questContent);

                if (fullContent.NullOrEmpty())
                {
                    _ledger.Clear();
                    return;
                }

                var record = new DailyRecord { Day = day, Timeline = fullContent, QuestSnapshot = questSnapshot };
                _ledger.AddDailyRecord(record);
                _lastArchivedDay = day;
                _ledger.Clear();

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
                    // Write through the ledger instance this request was made for, not
                    // ColonyLedger.Current — the player may have switched saves while this
                    // callback was in flight, and Current would then point at a different colony.
                    _ledger?.SetDailySummary(day, summary);
                    Log.Message($"[Firefly] Daily summary written: Day {day}");
                    if (!IsStillActive) return;
                    MaybeSendArcSummary();
                },
                onError: err => Log.Warning($"[Firefly] LLM summary failed for Day {day}: {err}"));
        }

        // Only folds in a contiguous run of summarised days starting right after LastArcDay —
        // if an earlier day's summary is still in flight (e.g. backfill racing a fresh daily
        // request), it stays un-arced rather than getting silently skipped forever once a later
        // day's completion advances the watermark past it.
        private int ComputeContiguousArcThroughDay()
        {
            int throughDay = _ledger.LastArcDay;
            foreach (var record in _ledger.PastDays
                         .Where(d => d.Day > _ledger.LastArcDay)
                         .OrderBy(d => d.Day))
            {
                if (record.Day != throughDay + 1 || record.Summary.NullOrEmpty()) break;
                throughDay = record.Day;
            }
            return throughDay;
        }

        private void MaybeSendArcSummary()
        {
            if (_arcInFlight || _ledger == null) return;
            int throughDay = ComputeContiguousArcThroughDay();
            if (throughDay - _ledger.LastArcDay < ArcIntervalDays) return;
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
                        if (!IsStillActive) return;
                        _ledger?.SetColonyHistory(arcText, day);
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

            if (pending.Count == 0)
            {
                // No missing summaries — check whether an overdue arc should fire.
                if (_lastArchivedDay > 0) MaybeSendArcSummary();
                return;
            }

            _backfillQueue.Clear();
            foreach (var record in pending) _backfillQueue.Enqueue(record);

            Log.Message($"[Firefly] Backfilling {_backfillQueue.Count} missing summaries, one at a time...");
            _backfillActive = true;
            ProcessBackfillQueue();
        }

        public void TriggerBackfillOnLoad()
        {
            BackfillMissingSummaries(excludeDay: -1);
        }

        private void ProcessBackfillQueue()
        {
            // Stop processing if the game has changed — avoid spending API calls for a
            // colony that's no longer loaded.
            if (!IsStillActive)
            {
                _backfillQueue.Clear();
                _backfillActive = false;
                return;
            }

            if (_backfillQueue.Count == 0)
            {
                _backfillActive = false;
                if (_lastArchivedDay > 0) MaybeSendArcSummary();
                return;
            }

            var record = _backfillQueue.Dequeue();
            Log.Message($"[Firefly] Backfilling Day {record.Day} ({_backfillQueue.Count} remaining)...");
            LLMClient.Send(
                DailySystemPrompt(),
                record.Timeline,
                onSuccess: summary =>
                {
                    // Don't gate the write itself — mutating a detached ledger is harmless, it's
                    // discarded once out of scope. ProcessBackfillQueue's own entry guard above
                    // is what stops the queue from chaining further paid requests once stale.
                    _ledger?.SetDailySummary(record.Day, summary);
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
