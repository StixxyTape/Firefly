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
        private readonly JournalSummaryService _journalSummaries;

        private bool _enabled;
        private int  _tickCounter;
        private int  _lastHourBucket  = -1;
        private int  _lastDrainHour   = -1;
        private int  _lastArchivedDay = -1;
        private bool _arcInFlight;

        private readonly Queue<DailyRecord> _backfillQueue = new Queue<DailyRecord>();
        private bool _backfillActive;

        // These are logical work queues, not network state. Entries remain persisted until the
        // corresponding scan or final thread summary has been applied successfully.
        private List<PendingThreadScan> _pendingThreadScans = new List<PendingThreadScan>();
        private List<string> _pendingThreadSummaries = new List<string>();
        private readonly HashSet<int> _attemptedThreadScanDays = new HashSet<int>();
        private int? _activeThreadScanDay;

        public JournalRecorder(ColonyLedger ledger)
        {
            _ledger = ledger;
            _journalSummaries = new JournalSummaryService(OnJournalWorkSettled);
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
            Scribe_Collections.Look(ref _pendingThreadScans, "pendingThreadScans", LookMode.Deep);
            Scribe_Collections.Look(ref _pendingThreadSummaries, "pendingThreadSummaries", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (_pendingThreadScans == null) _pendingThreadScans = new List<PendingThreadScan>();
                if (_pendingThreadSummaries == null) _pendingThreadSummaries = new List<string>();
                _pendingThreadScans.RemoveAll(s => s == null || s.Timeline.NullOrEmpty());
                _pendingThreadScans = _pendingThreadScans
                    .GroupBy(s => s.Day)
                    .Select(g => g.Last())
                    .OrderBy(s => s.Day)
                    .ToList();
                _pendingThreadSummaries = _pendingThreadSummaries
                    .Where(id => !id.NullOrEmpty())
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public void RecoverInterruptedThreadWork()
        {
            if (!_enabled || !IsStillActive) return;

            foreach (var threadId in _pendingThreadSummaries.ToList())
                EnqueueThreadWork(threadId);

            FireflyWorldComponent.Current?.TryResumeWorldWork();

            StartNextPendingThreadScan();
        }

        public void Tick()
        {
            if (!_enabled || _ledger == null) return;

            _tickCounter++;
            if (_tickCounter % CheckIntervalTicks != 0) return;

            try
            {
                Map map = ColonyLedger.ResolveMap();
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

        // Colony-specific — World History now has its own dedicated arc prompt
        // (WorldThreadScanIngest.WorldArcHistorySystemPrompt) rather than sharing this one.
        public static readonly string ArcSystemPrompt =
            "You are Fillion, narrator of a colony on a distant earth-like rimworld. You will be " +
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
            "Never include meta figures — relationship points, mood percentages, or similar. Describe " +
            "the situational equivalent instead.\n\n" +
            "Keep it to a single flowing account, plain past-tense prose. No lists. 10 lines maximum.";

        // Reads today's raw colony record directly (not pre-extracted facts) and the full
        // existing-thread index (not just names), deciding new-vs-update. Not fed the daily
        // narrative summary — this call is independent of that one, firing right alongside it,
        // never chained after it. Only writes a summary itself for a brand-new thread (short,
        // so it's never left blank) — an existing thread's full summary is written separately by
        // a per-thread pass derived from its complete fact record, not patched here, to avoid
        // compounding drift from repeatedly rewriting a rewrite.
        private static readonly string ThreadScanSystemPrompt =
            "You are Fillion, observer of a colony on a distant earth-like rimworld. You will be " +
            "given story threads the colony is currently dealing with, and a detailed log of what " +
            "happened today. Your job is to scour through today's log and decide two things from " +
            "today's events: are any story threads being updated, or are any new story threads " +
            "being created.\n\n" +
            "The criteria for threads being updated: anything in today's log which would have an " +
            "impact on the story thread. This includes notable and impactful changes to relevant " +
            "characters (death, permanent injury, other notable and impactful changes), factions, " +
            "quests, and anything else that is involved with the story thread (items, buildings, " +
            "relationships, events, etc.). This also includes anything that could have future " +
            "developments or consequences for the story thread.\n\n" +
            "The criteria for threads being created: anything in today's log which is not " +
            "relevant/connected to any current story threads, but could lead to larger implications " +
            "about or expand the world, could have any consequences or lasting impact on the colony " +
            "or any factions, have a narrative built in (e.g from quests or events), or could lead " +
            "to narrative arcs about characters or factions.\n\n" +
            "Never dismiss founding events merely because they occur on Day 0. Forced, violent, " +
            "mysterious, or consequential arrival circumstances should create an origin thread.\n\n" +
            "You can mainly ignore routine events/activities and inconsequential occurrences. Only " +
            "focus on things that actually have a narrative weight.\n\n" +
            "It is okay to respond with an empty list of new threads/updates if nothing consequential " +
            "happens to update or create new threads. If a day seems normal with nothing really out " +
            "of the ordinary happening, then it is safe to assume no story threads are being " +
            "progressed or created.\n\n" +
            "Facts must be short, self-contained statements of what actually happened. When writing " +
            "a fact, make sure to include the full names of any Characters/Factions/Items/Entities " +
            "involved.\n\n" +
            "For a brand-new thread, also write a short initial summary - 3 lines maximum - capturing " +
            "what's known so far, in the same curious voice as facts below. For an update to an " +
            "existing thread, do not write a summary at all; only report the relevant facts. That " +
            "thread's full summary is written separately afterward, from its complete fact record.\n\n" +
            "When writing a new thread's summary and facts, never include meta figures - relationship " +
            "points, mood percentages, infection percentages, or similar. If you need to describe " +
            "them, describe their situational equivalent instead.\n\n" +
            "Example new-thread summary:\n" +
            "\"The colony seems to have gotten involved with something shady. An ominous stranger " +
            "showed up and dropped off a mysterious gift. Who were they, and what secrets does this " +
            "item hold?\"\n\n" +
            "When writing facts, keep them short, focused, and curious, sticking to the same question " +
            "pattern as summaries. You should also feel free to guess intentions behind any actions " +
            "the colony or colonists take - but don't assume them as certain. Also feel free to pose " +
            "multiple motives. Sometimes you can also stick to the facts straight up - vary it.\n\n" +
            "Example facts:\n" +
            "\"An ominous figure visited the colony and left a mysterious item. What could it be, and " +
            "where does this stranger come from?\n\n" +
            "The Royal Empire visited the colony asking if they had seen the ominous stranger. The " +
            "colony answered truthfully - perhaps wanting to earn the trust of the Empire, or maybe " +
            "just wanting to resolve this situation as peacefully as possible.\n\n" +
            "The colony showed The Royal Empire the mysterious item the stranger left - and was " +
            "offered 1000 silver pieces for it. They took the deal, handing off the mysterious item " +
            "in exchange for the money.\"\n\n" +
            "Return exactly one JSON object and nothing else, with this shape:\n" +
            "{\"new_threads\":[{\"name\":\"string\",\"summary\":\"string\",\"facts\":[\"string\"]}]," +
            "\"updates\":[{\"id\":\"string\",\"facts\":[\"string\"]}]}\n" +
            "Both arrays must always be present, using empty arrays when there is nothing to report. " +
            "Every update id must exactly match an id from the existing-threads block; never use a name as an id.";

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
                _ledger.RefreshTrackedPawnCategories(map);
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
                RememberPendingThreadScan(day, fullContent);
                StartNextPendingThreadScan();
                BackfillMissingSummaries(excludeDay: day);
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Failed to process daily timeline: {e.Message}");
            }
        }

        // Public: MainTabWindow_Journal reads these to know which nav tab to highlight while a
        // given call is in flight — Colony for anything that writes daily/colony-level prose,
        // Threads for the single day-end thread scan.
        public const string DailyColonySummaryLabel    = "DailyColonySummary";
        public const string DailyColonyThreadScanLabel      = "DailyColonyThreadScan";
        public const string MonthlyColonyArcHistoryLabel      = "MonthlyColonyArcHistory";

        // Backfill (catching up a day that never got summarised) reuses SendSummaryRequest
        // directly rather than its own label — it's the same call for the same purpose, just
        // triggered for a past day instead of the one that just closed.
        public static readonly string[] ColonyPendingLabels  = { DailyColonySummaryLabel, MonthlyColonyArcHistoryLabel };
        public static readonly string[] ThreadsPendingLabels =
            { DailyColonyThreadScanLabel, JournalSummaryService.ThreadFactChunkerLabel, JournalSummaryService.ThreadSummariserLabel };

        // onDone fires after the request settles either way (success or failure) — used by the
        // backfill queue below to chain to the next missing day without its own LLMClient.Send
        // call. Ordinary same-day calls just leave it null.
        private void SendSummaryRequest(int day, string content, Action onDone = null)
        {
            Log.Message($"[Firefly:{DailyColonySummaryLabel}] Sending Day {day}...");

            string prevSummary = _ledger?.PastDays
                .LastOrDefault(d => d.Day == day - 1 && !d.Summary.NullOrEmpty())
                ?.Summary;

            string prompt = prevSummary.NullOrEmpty()
                ? content
                : $"=== PREVIOUS DAY SUMMARY (context only — do not summarise this) ===\n{prevSummary.Trim()}\n\n{content}";

            LLMClient.Send(
                DailyColonySummaryLabel,
                DailySystemPrompt(),
                prompt,
                onSuccess: summary =>
                {
                    Log.Message($"[Firefly:{DailyColonySummaryLabel}] Responded for Day {day}: {summary}");
                    if (!IsStillActive) { onDone?.Invoke(); return true; }
                    if (summary.NullOrEmpty()) return false; // empty reply — worth another attempt
                    // Write through the ledger instance this request was made for, not
                    // ColonyLedger.Current — the player may have switched saves while this
                    // callback was in flight, and Current would then point at a different colony.
                    _ledger?.SetDailySummary(day, summary);
                    FireflyWorldComponent.Current?.QueueDailyWorldWork(day, summary.Trim());
                    MaybeSendArcSummary();
                    onDone?.Invoke();
                    return true;
                },
                onError: err =>
                {
                    Log.Warning($"[Firefly:{DailyColonySummaryLabel}] Failed for Day {day}: {err}");
                    onDone?.Invoke();
                });
        }

        private void RememberPendingThreadScan(int day, string content)
        {
            var existing = _pendingThreadScans.FirstOrDefault(s => s.Day == day);
            if (existing != null) existing.Timeline = content;
            else _pendingThreadScans.Add(new PendingThreadScan { Day = day, Timeline = content });
        }

        private void CompletePendingThreadScan(int day)
        {
            _pendingThreadScans.RemoveAll(s => s.Day == day);
            _activeThreadScanDay = null;
            StartNextPendingThreadScan();
        }

        // Scan days in order and never concurrently. Each response is classified against the
        // thread index produced by all earlier days, avoiding duplicate or unknown-thread results
        // when several interrupted days are recovered together.
        private void StartNextPendingThreadScan()
        {
            // A later day waits only for work that can still change a summary this session.
            // Persisted failed items remain queued for load-time recovery, but must not stall all
            // future scans after their in-session attempt has already settled.
            if (_activeThreadScanDay.HasValue || _journalSummaries.AnyWorking || !IsStillActive) return;
            var next = _pendingThreadScans
                .Where(s => !_attemptedThreadScanDays.Contains(s.Day))
                .OrderBy(s => s.Day)
                .FirstOrDefault();
            if (next == null) return;

            _activeThreadScanDay = next.Day;
            _attemptedThreadScanDays.Add(next.Day);
            SendThreadScanRequest(next.Timeline, next.Day);
        }

        // Restored to the original single-call design: one call reads today's raw record plus
        // the full existing-thread index and decides new-vs-update AND writes each thread's
        // summary in the same response — no separate extraction, relevance, or write pass. Fires
        // independently of the daily summary call, not chained after it.
        //
        // Invalid JSON here used to retry via its own bespoke attempt counter (RetryOrGiveUp) —
        // removed now that LLMClient.Send retries a validation failure the same way it retries a
        // network failure, driven by the shared Max retries setting instead of a hardcoded 4.
        private void SendThreadScanRequest(string content, int day)
        {
            if (_ledger == null) return;

            string threadContext = StoryThreadScanIngest.BuildThreadContextBlock(_ledger);

            string prompt =
                "=== EXISTING STORY THREADS ===\n" +
                (threadContext.NullOrEmpty() ? "(none)" : threadContext.Trim()) +
                "\n\n=== TODAY'S COLONY RECORD ===\n" + content;

            Log.Message($"[Firefly:{DailyColonyThreadScanLabel}] Sending...");
            LLMClient.Send(
                DailyColonyThreadScanLabel,
                ThreadScanSystemPrompt,
                prompt,
                onSuccess: rawJson =>
                {
                    Log.Message($"[Firefly:{DailyColonyThreadScanLabel}] Responded: {rawJson}");
                    if (!IsStillActive) return true;

                    List<string> touchedExisting;
                    try
                    {
                        touchedExisting = StoryThreadScanIngest.ApplyScanResult(_ledger, rawJson, day);
                    }
                    catch (Exception e)
                    {
                        // A code-level failure applying otherwise-valid JSON — retrying sends the
                        // same broken input into the same code path, so don't.
                        Log.Warning($"[Firefly:{DailyColonyThreadScanLabel}] Ingest failed: {e.Message}");
                        return true;
                    }

                    if (touchedExisting == null) return false; // invalid JSON — worth another attempt

                    foreach (var id in touchedExisting) EnqueueThreadWork(id);
                    CompletePendingThreadScan(day);
                    return true;
                },
                onError: err =>
                {
                    _activeThreadScanDay = null;
                    Log.Warning($"[Firefly:{DailyColonyThreadScanLabel}] Failed: {err}. It will be retried after reload.");
                    StartNextPendingThreadScan();
                });
        }

        public static bool IsThreadWorking(string threadId)
        {
            if (threadId.NullOrEmpty()) return false;
            var recorder = Current.Game?.GetComponent<FireflyGameComponent>()?.Recorder;
            return recorder != null && recorder.IsStillActive &&
                   recorder._journalSummaries.IsWorking("story:" + threadId);
        }

        private void EnqueueThreadWork(string threadId)
        {
            var thread = _ledger?.StoryThreads.FirstOrDefault(t =>
                string.Equals(t.Id, threadId, StringComparison.OrdinalIgnoreCase));
            if (thread == null || !thread.Journal.SummaryStale) return;
            if (!_pendingThreadSummaries.Contains(thread.Id, StringComparer.OrdinalIgnoreCase))
                _pendingThreadSummaries.Add(thread.Id);
            _journalSummaries.Enqueue("story:" + thread.Id, thread.Name, thread.Journal,
                () => IsStillActive && _ledger.StoryThreads.Contains(thread));
        }

        public void RegenerateWorldThreads(IEnumerable<string> ids, Action<bool> completed)
        {
            var world = FireflyWorldComponent.Current;
            var wanted = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var targets = world?.WorldThreads.Where(t => t != null && wanted.Contains(t.Id))
                .Select(t => new JournalSummaryService.Target
                {
                    Key = "world:" + t.Id,
                    Title = t.Title,
                    Record = t.Journal,
                    ChunkRequestLabel = FireflyWorldComponent.WorldThreadFactChunkerLabel,
                    SummaryRequestLabel = FireflyWorldComponent.WorldThreadSummariserLabel,
                    // World Threads get their own one-line summary prompt, mirroring how World
                    // Seed writes its initial thread summaries — distinct from Story Threads (plain
                    // default), Faction Narrative (NarrativeSummaryPrompt), and Description. The
                    // chunker itself stays shared across all three, no override here.
                    SummaryPromptOverride = JournalSummaryService.WorldThreadSummaryPrompt,
                    IsActive = () => IsStillActive && ReferenceEquals(FireflyWorldComponent.Current, world) &&
                        world.WorldThreads.Contains(t),
                }) ?? Enumerable.Empty<JournalSummaryService.Target>();
            _journalSummaries.EnqueueBatch(targets, completed);
        }

        // Narrative pair (event-driven story) — populated by Faction Update.
        public void RegenerateFactionNarratives(IEnumerable<string> keys, Action<bool> completed)
        {
            var world = FireflyWorldComponent.Current;
            var wanted = new HashSet<string>(keys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var targets = world?.FactionSnapshots.Where(f => f != null && wanted.Contains(f.Key))
                .Select(f => new JournalSummaryService.Target
                {
                    Key = "faction-narrative:" + f.Key,
                    Title = f.FactionName,
                    Record = f.NarrativeJournal,
                    ChunkRequestLabel = FireflyWorldComponent.FactionNarrativeChunkerLabel,
                    SummaryRequestLabel = FireflyWorldComponent.FactionNarrativeSummariserLabel,
                    SummaryPromptOverride = JournalSummaryService.NarrativeSummaryPrompt,
                    IsActive = () => IsStillActive && ReferenceEquals(FireflyWorldComponent.Current, world) &&
                        world.FactionSnapshots.Contains(f),
                }) ?? Enumerable.Empty<JournalSummaryService.Target>();
            _journalSummaries.EnqueueBatch(targets, completed);
        }

        // Faction pair (stable characterization) — populated directly by Faction Update.
        public void RegenerateFactionDescriptions(IEnumerable<string> keys, Action<bool> completed)
        {
            var world = FireflyWorldComponent.Current;
            var wanted = new HashSet<string>(keys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var targets = world?.FactionSnapshots.Where(f => f != null && wanted.Contains(f.Key))
                .Select(f => new JournalSummaryService.Target
                {
                    Key = "faction-description:" + f.Key,
                    Title = f.FactionName,
                    Record = f.FactionJournal,
                    ChunkRequestLabel = FireflyWorldComponent.FactionIdentityChunkerLabel,
                    SummaryRequestLabel = FireflyWorldComponent.FactionIdentitySummariserLabel,
                    // Description is a settled identity portrait, not an unfolding plot — the
                    // shared story-shaped prompt's "developments in order"/"unresolved stakes"
                    // framing doesn't fit, so this uses its own dedicated prompt pair.
                    SummaryPromptOverride = JournalSummaryService.DescriptionSummaryPrompt,
                    ChunkPromptOverride = JournalSummaryService.DescriptionChunkPrompt,
                    IsActive = () => IsStillActive && ReferenceEquals(FireflyWorldComponent.Current, world) &&
                        world.FactionSnapshots.Contains(f),
                }) ?? Enumerable.Empty<JournalSummaryService.Target>();
            _journalSummaries.EnqueueBatch(targets, completed);
        }

        private void OnJournalWorkSettled()
        {
            _pendingThreadSummaries.RemoveAll(id =>
            {
                var thread = _ledger?.StoryThreads.FirstOrDefault(t =>
                    string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
                return thread == null || !thread.Journal.SummaryStale;
            });
            StartNextPendingThreadScan();
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

                Log.Message($"[Firefly:{MonthlyColonyArcHistoryLabel}] Sending... (through Day {day})");
                _arcInFlight = true;
                LLMClient.Send(
                    MonthlyColonyArcHistoryLabel,
                    ArcSystemPrompt,
                    combined,
                    onSuccess: arcText =>
                    {
                        if (!IsStillActive) { _arcInFlight = false; return true; }
                        if (arcText.NullOrEmpty()) return false; // empty reply — worth another attempt
                        Log.Message($"[Firefly:{MonthlyColonyArcHistoryLabel}] Responded (through Day {day}): {arcText}");
                        _arcInFlight = false;
                        _ledger?.SetColonyHistory(arcText, day);
                        return true;
                    },
                    onError: err =>
                    {
                        _arcInFlight = false;
                        Log.Warning($"[Firefly:{MonthlyColonyArcHistoryLabel}] Failed (through Day {day}): {err}");
                    });
            }
            catch (Exception e)
            {
                _arcInFlight = false;
                Log.Warning($"[Firefly:{MonthlyColonyArcHistoryLabel}] SendArcSummaryRequest failed: {e.Message}");
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

            Log.Message($"[Firefly:{DailyColonySummaryLabel}] Backfilling {_backfillQueue.Count} missing summaries, one at a time...");
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
            Log.Message($"[Firefly:{DailyColonySummaryLabel}] Backfilling Day {record.Day} ({_backfillQueue.Count} remaining)...");
            // Same call, same label, as an ordinary same-day summary — just for a day that got
            // missed. onDone chains to the next queued day once this one settles, one at a time,
            // so a stale in-flight request can't chain further calls after the entry guard above
            // has already stopped the queue.
            SendSummaryRequest(record.Day, record.Timeline, onDone: ProcessBackfillQueue);
        }
    }
}
