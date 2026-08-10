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

        // Writes a touched existing thread's complete summary from its immutable chunks and
        // unchunked facts. The previous summary is deliberately excluded so drift cannot compound.
        private static readonly string ThreadSummarizeSystemPrompt =
            "You are Fillion, narrator of one ongoing story thread in a colony on a distant " +
            "earth-like rimworld. You will receive the thread's name, summaries of older facts, and " +
            "its newer facts. Your job is to summarize the thread's facts into a short piece of " +
            "focused writing, detailing what happened.\n\n" +
            "Write exactly three compact sentences. The first establishes the thread's origin and " +
            "its essential named participants. The second covers major developments and where " +
            "things currently stand. The third states what remains unresolved, framed with " +
            "Fillion's curious, invested voice. Omit texture, repetition, minor incidents, and " +
            "questions later facts have already answered. Aim for roughly 500-700 characters " +
            "total.\n\n" +
            "Preserve guesses, possibilities, and open questions as uncertainty rather than " +
            "treating them as settled — but only where they're still genuinely unresolved. When " +
            "later material explicitly answers an earlier question, use that resolution instead " +
            "of restating the question.\n\n" +
            "Never include meta figures - relationship points, mood percentages, infection " +
            "percentages, or similar. If you need to describe them, describe their situational " +
            "equivalent instead.\n\n" +
            "Example summary:\n" +
            "\"The colony's dealings with an ominous stranger began when they dropped off a " +
            "mysterious gift one day. The Royal Empire later inquired about the stranger and bought " +
            "the gift off the colony for a moderate sum, and no word has come since. Who was the " +
            "stranger, and what does the Empire really want with that item?\"\n\n" +
            "Return only the summary itself as plain prose — no JSON, no headers, no quotation marks, " +
            "no introduction or conclusion.";

        // Condenses one immutable fact batch into hidden source material for the summarizer.
        private static readonly string ThreadChunkSystemPrompt =
            "You are Fillion, narrator of one ongoing story thread in a colony on a distant " +
            "earth-like rimworld. You will receive one chronological batch of facts about this " +
            "story thread, and your job is to condense them into a short piece of focused writing, " +
            "including all the important, consequential, and notable details.\n\n" +
            "Preserve all narratively important people, factions, items, events, changes, causes, " +
            "outcomes, and unresolved stakes. Keep their order clear and state causality only when " +
            "the facts support it. Always use full names, titles, faction names, and similar in " +
            "order to keep it clear which entities are involved.\n\n" +
            "Do not invent, embellish, or resolve anything. Preserve guesses, possibilities, and " +
            "open questions as uncertainty, woven as underlying texture within the paragraph. " +
            "Remove repetition and minor connective wording, but do not discard distinct " +
            "consequential information. Use plain, compact language rather than Fillion's " +
            "player-facing voice.\n\n" +
            "Return only a single condensed paragraph. Return only plain prose — no JSON, no " +
            "headers, no quotation marks, no introduction or conclusion, no day labels.";

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
                SendThreadScanRequest(fullContent, day);
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
        public const string DailySummaryLabel    = "DailySummary";
        public const string ThreadScanLabel      = "ThreadScan";
        public const string ThreadRepairLabel    = "ThreadRepair";
        public const string ThreadChunkLabel     = "ThreadChunk";
        public const string ThreadSummarizeLabel = "ThreadSummarize";
        public const string ArcHistoryLabel      = "ArcHistory";
        public const string BackfillLabel        = "Backfill";

        public static readonly string[] ColonyPendingLabels  = { DailySummaryLabel, ArcHistoryLabel, BackfillLabel };
        public static readonly string[] ThreadsPendingLabels =
            { ThreadScanLabel, ThreadRepairLabel, ThreadChunkLabel, ThreadSummarizeLabel };

        // Cheap, mechanical fix-up call — given only a response that failed to parse, fixes JSON
        // syntax/escaping/key formatting without touching narrative content. Tried once per failed
        // scan response, before paying for a full retry of the expensive scan call itself.
        private static readonly string ThreadRepairSystemPrompt =
            "You repair malformed JSON. Return only the corrected JSON, with no markdown or " +
            "commentary.\n\n" +
            "Required shape:\n" +
            "{\"new_threads\":[{\"name\":\"string\",\"summary\":\"string\",\"facts\":[\"string\"]}]," +
            "\"updates\":[{\"id\":\"string\",\"facts\":[\"string\"]}]}\n\n" +
            "Fix only JSON syntax, escaping, and schema-key formatting. Preserve all supplied " +
            "narrative content exactly. Never add, infer, rewrite, summarize, or remove narrative " +
            "content. Both arrays must be present. If the response is too incomplete to repair " +
            "without inventing content, return it unchanged.";

        private void SendSummaryRequest(int day, string content)
        {
            Log.Message($"[Firefly:{DailySummaryLabel}] Sending Day {day}...");

            string prevSummary = _ledger?.PastDays
                .LastOrDefault(d => d.Day == day - 1 && !d.Summary.NullOrEmpty())
                ?.Summary;

            string prompt = prevSummary.NullOrEmpty()
                ? content
                : $"=== PREVIOUS DAY SUMMARY (context only — do not summarise this) ===\n{prevSummary.Trim()}\n\n{content}";

            LLMClient.Send(
                DailySummaryLabel,
                DailySystemPrompt(),
                prompt,
                onSuccess: summary =>
                {
                    Log.Message($"[Firefly:{DailySummaryLabel}] Responded for Day {day}: {summary}");
                    // Write through the ledger instance this request was made for, not
                    // ColonyLedger.Current — the player may have switched saves while this
                    // callback was in flight, and Current would then point at a different colony.
                    _ledger?.SetDailySummary(day, summary);
                    if (!IsStillActive) return;
                    MaybeSendArcSummary();
                },
                onError: err => Log.Warning($"[Firefly:{DailySummaryLabel}] Failed for Day {day}: {err}"));
        }

        private const int ThreadJsonMaxAttempts = 4;

        // Restored to the original single-call design: one call reads today's raw record plus
        // the full existing-thread index and decides new-vs-update AND writes each thread's
        // summary in the same response — no separate extraction, relevance, or write pass. Fires
        // independently of the daily summary call, not chained after it.
        private void SendThreadScanRequest(string content, int day, int attempt = 1)
        {
            if (_ledger == null) return;

            string threadContext = StoryThreadScanIngest.BuildThreadContextBlock(_ledger);

            string prompt =
                "=== EXISTING STORY THREADS ===\n" +
                (threadContext.NullOrEmpty() ? "(none)" : threadContext.Trim()) +
                "\n\n=== TODAY'S COLONY RECORD ===\n" + content;

            Log.Message($"[Firefly:{ThreadScanLabel}] Sending... (attempt {attempt}/{ThreadJsonMaxAttempts})");
            LLMClient.Send(
                ThreadScanLabel,
                ThreadScanSystemPrompt,
                prompt,
                onSuccess: rawJson =>
                {
                    Log.Message($"[Firefly:{ThreadScanLabel}] Responded: {rawJson}");
                    if (!IsStillActive) return;

                    List<string> touchedExisting;
                    try
                    {
                        touchedExisting = StoryThreadScanIngest.ApplyScanResult(_ledger, rawJson, day);
                    }
                    catch (Exception e)
                    {
                        // A code-level failure applying otherwise-valid JSON — retrying sends the
                        // same broken input into the same code path, so don't.
                        Log.Warning($"[Firefly:{ThreadScanLabel}] Ingest failed: {e.Message}");
                        return;
                    }

                    if (touchedExisting != null)
                    {
                        foreach (var id in touchedExisting) EnqueueThreadWork(id);
                        return;
                    }

                    // A cheap repair attempt before paying for a full retry of the expensive scan
                    // call — skipped for an empty response, since there's nothing to repair.
                    if (!rawJson.NullOrEmpty())
                    {
                        SendThreadScanRepairRequest(content, day, rawJson, attempt);
                        return;
                    }

                    RetryOrGiveUp(content, day, attempt);
                },
                onError: err => Log.Warning($"[Firefly:{ThreadScanLabel}] Failed: {err}"));
        }

        private void RetryOrGiveUp(string content, int day, int attempt)
        {
            if (attempt < ThreadJsonMaxAttempts)
            {
                Log.Warning($"[Firefly:{ThreadScanLabel}] Invalid JSON — retrying ({attempt + 1}/{ThreadJsonMaxAttempts}).");
                SendThreadScanRequest(content, day, attempt + 1);
            }
            else
            {
                Log.Warning($"[Firefly:{ThreadScanLabel}] Gave up after {ThreadJsonMaxAttempts} attempts — invalid JSON each time.");
            }
        }

        // One repair shot per failed scan response — not itself retried. If the repair call
        // errors out, or its own output still fails to parse, falls through to the existing
        // full-retry-from-scratch path, consuming one of ThreadJsonMaxAttempts same as before.
        // A repair success costs nothing against that budget.
        private void SendThreadScanRepairRequest(string content, int day, string brokenJson, int attempt)
        {
            if (_ledger == null) return;

            Log.Message($"[Firefly:{ThreadRepairLabel}] Attempting repair of malformed scan response...");
            LLMClient.Send(
                ThreadRepairLabel,
                ThreadRepairSystemPrompt,
                brokenJson,
                onSuccess: repairedJson =>
                {
                    Log.Message($"[Firefly:{ThreadRepairLabel}] Repair responded: {repairedJson}");
                    if (!IsStillActive) return;

                    List<string> touchedExisting;
                    try
                    {
                        touchedExisting = StoryThreadScanIngest.ApplyScanResult(_ledger, repairedJson, day);
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"[Firefly:{ThreadRepairLabel}] Ingest failed: {e.Message}");
                        return;
                    }

                    if (touchedExisting != null)
                    {
                        Log.Message($"[Firefly:{ThreadRepairLabel}] Repair succeeded — full retry avoided.");
                        foreach (var id in touchedExisting) EnqueueThreadWork(id);
                        return;
                    }

                    Log.Warning($"[Firefly:{ThreadRepairLabel}] Repaired response still invalid — falling back to full retry.");
                    RetryOrGiveUp(content, day, attempt);
                },
                onError: err =>
                {
                    Log.Warning($"[Firefly:{ThreadRepairLabel}] Failed: {err} — falling back to full retry.");
                    if (IsStillActive) RetryOrGiveUp(content, day, attempt);
                });
        }

        // Trigger for folding a thread's oldest unchunked facts into a permanent chunk — whichever
        // comes first, since fact length varies enough that count alone isn't a reliable proxy for
        // how large the resulting prompt would be.
        private const int ChunkFactThreshold = 30;
        private const int ChunkCharThreshold = 3000;

        // Per-thread work: chunk maintenance (folding old facts into permanent condensed history)
        // and the full summarize pass, serialized so at most one of either is ever in flight per
        // thread — two overlapping days both wanting to touch the same thread could otherwise let
        // a slower response clobber a fresher one, or two chunk calls claim overlapping fact
        // ranges. A touch that lands while work is already in flight just flags NeedsAnotherPass
        // rather than firing a second concurrent call; re-evaluated fresh (not replayed) once the
        // in-flight call finishes, since by then the ledger may have moved on further still.
        private sealed class ThreadWorkState
        {
            public bool InFlight;
            public bool NeedsAnotherPass;
        }

        private readonly Dictionary<string, ThreadWorkState> _threadWork = new Dictionary<string, ThreadWorkState>();

        // UI-facing view of the active save's per-thread worker. Covers the full serialized
        // chunk-then-summarize sequence, not just whichever individual LLM call is active now.
        public static bool IsThreadWorking(string threadId)
        {
            if (threadId.NullOrEmpty()) return false;
            var recorder = Current.Game?.GetComponent<FireflyGameComponent>()?.Recorder;
            if (recorder == null || !recorder.IsStillActive) return false;
            return recorder._threadWork.TryGetValue(threadId, out var state) && state.InFlight;
        }

        private void EnqueueThreadWork(string threadId)
        {
            if (!_threadWork.TryGetValue(threadId, out var state))
            {
                state = new ThreadWorkState();
                _threadWork[threadId] = state;
            }

            if (state.InFlight)
            {
                state.NeedsAnotherPass = true;
                return;
            }

            state.InFlight = true;
            RunThreadWorkStep(threadId);
        }

        // Re-evaluates a thread's state fresh — chunk if there's enough unchunked material,
        // otherwise summarize. A completed chunk call loops back through here (see
        // SendThreadChunkRequest) so a thread that's accumulated several chunks' worth of facts
        // gets folded down fully before ever summarizing, rather than summarizing against a
        // still-oversized unchunked tail.
        private void RunThreadWorkStep(string threadId)
        {
            if (!_threadWork.TryGetValue(threadId, out var state)) return;
            if (!IsStillActive) { state.InFlight = false; return; }

            var thread = _ledger?.StoryThreads.FirstOrDefault(t => t.Id == threadId);
            if (thread == null) { state.InFlight = false; return; }

            int unchunkedCount = thread.Facts.Count - thread.ChunkedThroughFactIndex;
            int unchunkedChars = 0;
            for (int i = thread.ChunkedThroughFactIndex; i < thread.Facts.Count; i++)
                unchunkedChars += thread.Facts[i]?.Text?.Length ?? 0;

            if (unchunkedCount >= ChunkFactThreshold || unchunkedChars >= ChunkCharThreshold)
            {
                SendThreadChunkRequest(threadId);
                return;
            }

            SendThreadSummarizeRequest(threadId);
        }

        // Called once a thread's work settles — either the summarize pass completed, or chunking
        // failed and there's nothing further to do this pass. If more facts landed while this was
        // in flight, loops back through RunThreadWorkStep rather than just clearing the flag, so
        // nothing touched mid-flight gets silently left un-summarized.
        private void FinishThreadWork(string threadId)
        {
            if (!_threadWork.TryGetValue(threadId, out var state)) return;

            if (IsStillActive && state.NeedsAnotherPass)
            {
                state.NeedsAnotherPass = false;
                RunThreadWorkStep(threadId);
                return;
            }

            state.InFlight = false;
        }

        // Writes a touched thread's complete summary from scratch — condensed history (permanent
        // chunks) plus whatever facts haven't been folded into one yet. Never reads or references
        // the previous summary, so it can't compound drift from repeatedly rewriting a rewrite.
        private void SendThreadSummarizeRequest(string threadId)
        {
            var thread = _ledger?.StoryThreads.FirstOrDefault(t => t.Id == threadId);
            if (thread == null) { FinishThreadWork(threadId); return; }

            var sb = new StringBuilder();
            sb.AppendLine("=== THREAD NAME ===");
            sb.AppendLine(thread.Name);
            sb.AppendLine();

            if (thread.Chunks.Count > 0)
            {
                sb.AppendLine("=== CONDENSED HISTORY ===");
                foreach (var chunk in thread.Chunks)
                    sb.AppendLine($"[Day {chunk.StartDay}-{chunk.EndDay}] {chunk.Summary}");
                sb.AppendLine();
            }

            sb.AppendLine("=== RECENT FACTS ===");
            bool anyFacts = false;
            for (int i = thread.ChunkedThroughFactIndex; i < thread.Facts.Count; i++)
            {
                var fact = thread.Facts[i];
                if (fact == null) continue;
                sb.AppendLine($"[Day {fact.Day}] {fact.Text}");
                anyFacts = true;
            }
            if (!anyFacts) sb.AppendLine("(none)");

            string prompt = sb.ToString().TrimEnd();

            Log.Message($"[Firefly:{ThreadSummarizeLabel}] Sending for thread \"{threadId}\"...");
            LLMClient.Send(
                ThreadSummarizeLabel,
                ThreadSummarizeSystemPrompt,
                prompt,
                onSuccess: prose =>
                {
                    Log.Message($"[Firefly:{ThreadSummarizeLabel}] Responded for thread \"{threadId}\": {prose}");
                    if (IsStillActive && !prose.NullOrEmpty())
                        _ledger?.UpdateStoryThreadSummary(threadId, prose.Trim());
                    FinishThreadWork(threadId);
                },
                onError: err =>
                {
                    Log.Warning($"[Firefly:{ThreadSummarizeLabel}] Failed for thread \"{threadId}\": {err}");
                    FinishThreadWork(threadId);
                });
        }

        // Condenses one exact, immutable range beginning at the thread's persisted cursor. Facts
        // may be appended while the request is in flight, but this snapshot's boundary never
        // changes; the cursor advances only after the resulting chunk is successfully persisted.
        private void SendThreadChunkRequest(string threadId, int attempt = 1)
        {
            var thread = _ledger?.StoryThreads.FirstOrDefault(t => t.Id == threadId);
            if (thread == null) { FinishThreadWork(threadId); return; }

            int startIndex = Math.Max(0, thread.ChunkedThroughFactIndex);
            int count = Math.Min(ChunkFactThreshold, thread.Facts.Count - startIndex);
            if (count <= 0) { FinishThreadWork(threadId); return; }

            var batch = thread.Facts.Skip(startIndex).Take(count).ToList();
            var firstFact = batch.FirstOrDefault(f => f != null);
            var lastFact = batch.LastOrDefault(f => f != null);
            if (firstFact == null || lastFact == null)
            {
                Log.Warning($"[Firefly:{ThreadChunkLabel}] Thread \"{threadId}\" has no usable facts in the claimed range.");
                FinishThreadWork(threadId);
                return;
            }

            int newCursorValue = startIndex + count;
            int startDay = firstFact.Day;
            int endDay = lastFact.Day;

            var sb = new StringBuilder();
            sb.AppendLine("=== THREAD NAME ===");
            sb.AppendLine(thread.Name);
            sb.AppendLine();
            sb.AppendLine("=== FACTS TO CONDENSE ===");
            foreach (var fact in batch)
            {
                if (fact != null)
                    sb.AppendLine($"[Day {fact.Day}] {fact.Text}");
            }

            string prompt = sb.ToString().TrimEnd();

            Log.Message($"[Firefly:{ThreadChunkLabel}] Sending for thread \"{threadId}\" " +
                        $"(facts {startIndex}-{newCursorValue - 1}, attempt {attempt})...");
            LLMClient.Send(
                ThreadChunkLabel,
                ThreadChunkSystemPrompt,
                prompt,
                onSuccess: prose =>
                {
                    Log.Message($"[Firefly:{ThreadChunkLabel}] Responded for thread \"{threadId}\": {prose}");
                    if (!IsStillActive) { FinishThreadWork(threadId); return; }
                    if (prose.NullOrEmpty())
                    {
                        Log.Warning($"[Firefly:{ThreadChunkLabel}] Empty response for thread \"{threadId}\".");
                        FinishThreadWork(threadId);
                        return;
                    }

                    var current = _ledger?.StoryThreads.FirstOrDefault(t => t.Id == threadId);
                    if (current == null || current.ChunkedThroughFactIndex != startIndex)
                    {
                        Log.Warning($"[Firefly:{ThreadChunkLabel}] Stale chunk response for thread \"{threadId}\" — dropped.");
                        FinishThreadWork(threadId);
                        return;
                    }

                    _ledger.AddThreadChunk(threadId, startDay, endDay, prose.Trim(), newCursorValue);
                    RunThreadWorkStep(threadId);
                },
                onError: err =>
                {
                    Log.Warning($"[Firefly:{ThreadChunkLabel}] Failed for thread \"{threadId}\": {err}");
                    FinishThreadWork(threadId);
                });
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

                Log.Message($"[Firefly:{ArcHistoryLabel}] Sending... (through Day {day})");
                _arcInFlight = true;
                LLMClient.Send(
                    ArcHistoryLabel,
                    ArcSystemPrompt,
                    combined,
                    onSuccess: arcText =>
                    {
                        Log.Message($"[Firefly:{ArcHistoryLabel}] Responded (through Day {day}): {arcText}");
                        _arcInFlight = false;
                        if (!IsStillActive) return;
                        _ledger?.SetColonyHistory(arcText, day);
                    },
                    onError: err =>
                    {
                        _arcInFlight = false;
                        Log.Warning($"[Firefly:{ArcHistoryLabel}] Failed (through Day {day}): {err}");
                    });
            }
            catch (Exception e)
            {
                _arcInFlight = false;
                Log.Warning($"[Firefly:{ArcHistoryLabel}] SendArcSummaryRequest failed: {e.Message}");
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

            Log.Message($"[Firefly:{BackfillLabel}] Backfilling {_backfillQueue.Count} missing summaries, one at a time...");
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
            Log.Message($"[Firefly:{BackfillLabel}] Sending Day {record.Day} ({_backfillQueue.Count} remaining)...");
            LLMClient.Send(
                BackfillLabel,
                DailySystemPrompt(),
                record.Timeline,
                onSuccess: summary =>
                {
                    Log.Message($"[Firefly:{BackfillLabel}] Responded for Day {record.Day}: {summary}");
                    // Don't gate the write itself — mutating a detached ledger is harmless, it's
                    // discarded once out of scope. ProcessBackfillQueue's own entry guard above
                    // is what stops the queue from chaining further paid requests once stale.
                    _ledger?.SetDailySummary(record.Day, summary);
                    ProcessBackfillQueue();
                },
                onError: err =>
                {
                    Log.Warning($"[Firefly:{BackfillLabel}] Failed for Day {record.Day}: {err}");
                    ProcessBackfillQueue();
                });
        }
    }
}
