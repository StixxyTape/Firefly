using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;

namespace Firefly
{
    // One worker for every Facts -> Active Summary journal flow, regardless of subject type. Most
    // subjects (Story Threads, World Threads, Narrative) are all the same shape — an ongoing plot
    // with stakes — and share the default SummaryPrompt/ChunkPrompt below. A subject whose summary
    // means something genuinely different (e.g. Description, a settled identity portrait rather
    // than an unfolding story) can override PromptOverride/ChunkPromptOverride on its Target
    // instead of forking the whole worker.
    public sealed class JournalSummaryService
    {
        public sealed class Target
        {
            public string Key = "";
            public string Title = "";
            public JournalRecord Record = null!;
            public Func<bool> IsActive = null!;
            public string ChunkRequestLabel = ThreadFactChunkerLabel;
            public string SummaryRequestLabel = ThreadSummariserLabel;
            public string SummaryPromptOverride;
            public string ChunkPromptOverride;
        }

        public const string ThreadFactChunkerLabel = "ThreadFactChunker";
        public const string ThreadSummariserLabel = "ThreadSummariser";
        private const int ChunkFactThreshold = 30;
        private const int ChunkCharThreshold = 3000;

        private sealed class Work
        {
            public string Title = "";
            public JournalRecord Record = null!;
            public Func<bool> IsActive = null!;
            public bool InFlight;
            public bool NeedsAnotherPass;
            public string ChunkRequestLabel = ThreadFactChunkerLabel;
            public string SummaryRequestLabel = ThreadSummariserLabel;
            public string SummaryPromptOverride;
            public string ChunkPromptOverride;
        }

        private readonly Dictionary<string, Work> _work = new Dictionary<string, Work>();
        private readonly List<(List<(string Key, JournalRecord Record, int Revision)> Targets,
            Action<bool> Complete)> _batches =
            new List<(List<(string, JournalRecord, int)>, Action<bool>)>();
        private readonly Action _onSettled;

        public JournalSummaryService(Action onSettled) => _onSettled = onSettled;

        public bool IsWorking(string key) => !key.NullOrEmpty() &&
            _work.TryGetValue(key, out Work work) && work.InFlight;
        public bool AnyWorking => _work.Values.Any(w => w.InFlight);

        public void Enqueue(string key, string title, JournalRecord record, Func<bool> isActive,
            string chunkLabel = ThreadFactChunkerLabel, string summaryLabel = ThreadSummariserLabel,
            string summaryPromptOverride = null, string chunkPromptOverride = null)
        {
            if (key.NullOrEmpty() || record == null || !record.SummaryStale) return;
            if (!_work.TryGetValue(key, out Work work))
                _work[key] = work = new Work();
            work.Title = title ?? "Journal entry";
            work.Record = record;
            work.IsActive = isActive;
            work.ChunkRequestLabel = chunkLabel;
            work.SummaryRequestLabel = summaryLabel;
            work.SummaryPromptOverride = summaryPromptOverride;
            work.ChunkPromptOverride = chunkPromptOverride;
            if (work.InFlight) { work.NeedsAnotherPass = true; return; }
            work.InFlight = true;
            Run(key, work);
        }

        public void EnqueueBatch(IEnumerable<Target> targets, Action<bool> onComplete)
        {
            var requested = targets?.Where(t => t != null && t.Record != null && t.Record.SummaryStale)
                .ToList() ?? new List<Target>();
            if (requested.Count == 0) { onComplete?.Invoke(true); return; }

            _batches.Add((requested.Select(t => (t.Key, t.Record, t.Record.FactRevision)).ToList(),
                onComplete));
            foreach (var target in requested)
                Enqueue(target.Key, target.Title, target.Record, target.IsActive,
                    target.ChunkRequestLabel, target.SummaryRequestLabel,
                    target.SummaryPromptOverride, target.ChunkPromptOverride);
            CheckBatches();
        }

        private void Run(string key, Work work)
        {
            if (work.IsActive?.Invoke() != true || work.Record == null)
            {
                Finish(key, work);
                return;
            }
            int count = work.Record.Facts.Count - work.Record.ChunkedThroughFactIndex;
            int chars = work.Record.Facts.Skip(work.Record.ChunkedThroughFactIndex)
                .Sum(f => f?.Text?.Length ?? 0);
            if (count >= ChunkFactThreshold || chars >= ChunkCharThreshold)
                SendChunk(key, work);
            else
                SendSummary(key, work);
        }

        private void SendSummary(string key, Work work)
        {
            int coveredRevision = work.Record.FactRevision;
            var prompt = new StringBuilder();
            prompt.AppendLine("=== SUBJECT ===");
            prompt.AppendLine(work.Title);
            if (work.Record.Chunks.Count > 0)
            {
                prompt.AppendLine("=== CONDENSED HISTORY ===");
                foreach (var chunk in work.Record.Chunks)
                    prompt.AppendLine($"[Day {chunk.StartDay}-{chunk.EndDay}] {chunk.Summary}");
            }
            prompt.AppendLine("=== RECENT FACTS ===");
            foreach (var fact in work.Record.Facts.Skip(work.Record.ChunkedThroughFactIndex))
                prompt.AppendLine($"[Day {fact.Day}] {fact.Text}");

            Log.Message($"[Firefly:{work.SummaryRequestLabel}] Sending for {key}...");
            LLMClient.Send(work.SummaryRequestLabel, work.SummaryPromptOverride ?? SummaryPrompt, prompt.ToString().TrimEnd(),
                onSuccess: prose =>
                {
                    if (work.IsActive?.Invoke() != true) { Finish(key, work); return true; }
                    if (prose.NullOrEmpty()) return false; // empty reply — worth another attempt
                    work.Record.SetActiveSummary(prose, coveredRevision);
                    Finish(key, work, work.Record.FactRevision > coveredRevision);
                    return true;
                },
                onError: err =>
                {
                    Log.Warning($"[Firefly:{work.SummaryRequestLabel}] Failed for {key}: {err}");
                    Finish(key, work);
                });
        }

        private void SendChunk(string key, Work work)
        {
            int start = work.Record.ChunkedThroughFactIndex;
            var batch = work.Record.Facts.Skip(start).Take(ChunkFactThreshold).ToList();
            if (batch.Count == 0) { SendSummary(key, work); return; }
            int end = start + batch.Count;
            string prompt = string.Join("\n", batch.Select(f => $"[Day {f.Day}] {f.Text}"));
            Log.Message($"[Firefly:{work.ChunkRequestLabel}] Sending for {key}...");
            LLMClient.Send(work.ChunkRequestLabel, work.ChunkPromptOverride ?? ChunkPrompt, prompt,
                onSuccess: prose =>
                {
                    if (work.IsActive?.Invoke() != true || work.Record.ChunkedThroughFactIndex != start)
                    {
                        Finish(key, work);
                        return true;
                    }
                    if (prose.NullOrEmpty()) return false; // empty reply — worth another attempt
                    work.Record.Chunks.Add(new JournalFactChunk
                    {
                        StartDay = batch.First().Day,
                        EndDay = batch.Last().Day,
                        Summary = prose.Trim(),
                    });
                    work.Record.ChunkedThroughFactIndex = end;
                    Run(key, work);
                    return true;
                },
                onError: err =>
                {
                    Log.Warning($"[Firefly:{work.ChunkRequestLabel}] Failed for {key}: {err}");
                    Finish(key, work);
                });
        }

        private void Finish(string key, Work work, bool forceAnotherPass = false)
        {
            if ((work.NeedsAnotherPass || forceAnotherPass) && work.Record.SummaryStale &&
                work.IsActive?.Invoke() == true)
            {
                work.NeedsAnotherPass = false;
                Run(key, work);
                return;
            }
            work.InFlight = false;
            _onSettled?.Invoke();
            CheckBatches();
        }

        private void CheckBatches()
        {
            var settled = new List<(Action<bool> Callback, bool Success)>();
            for (int i = _batches.Count - 1; i >= 0; i--)
            {
                var batch = _batches[i];
                bool complete = batch.Targets.All(t => t.Record.SummarizedRevision >= t.Revision);
                bool stillWorking = batch.Targets.Any(t => IsWorking(t.Key));
                if (!complete && stillWorking) continue;
                _batches.RemoveAt(i);
                settled.Add((batch.Complete, complete));
            }
            settled.Reverse();
            foreach (var result in settled) result.Callback?.Invoke(result.Success);
        }

        // Default fallback when a Target/Enqueue call supplies no override — Story Threads are
        // the only caller left on the plain default (EnqueueThreadWork passes no override), so
        // this is effectively the Story Thread-specific summary prompt now, not a generic shared
        // one. The chunk prompt below it, though, stays genuinely shared across all three
        // (Story Threads, World Threads, Faction Narrative) — Josh's call: the chunker's job
        // (condense old facts losslessly-as-possible) doesn't need to differ by subject the way
        // the summary's voice does.
        private static readonly string SummaryPrompt =
            "You are Fillion, narrator of one ongoing story thread in a colony on a distant earth-like rimworld. You will receive the thread's name, summaries of older facts, and its newer facts. Your job is to summarize the thread's facts into a short piece of focused writing, detailing what happened.\n\n" +
            "It must read as a consistent, short story including all the notable facts, the beginning, and all impactful/consequential details in-between. The summary should also read as curious about things. Occasionally pose questions about events that have vague circumstances, potential consequences, or could lead to greater threads throughout the world. You are invested in the future of this story, along with how it's played out so far.\n\n" +
            "Preserve the thread's beginning, major turning points, current state, and unresolved stakes. Preserve guesses, possibilities, and open questions as uncertainty, woven as underlying texture within the paragraph. Keep the result concise and no longer than 10 lines.\n\n" +
            "Never include meta figures - relationship points, mood percentages, infection percentages, or similar. If you need to describe them, describe their situational equivalent instead.\n\n" +
            "Example summary:\n" +
            "\"The colony seems to have gotten involved with something shady. It started when an ominous stranger showed up one day and dropped off a mysterious gift - which attracted the attention of The Royal Empire a few days later. They bought the gift off the colony for a moderate sum after inquiring about the mysterious stranger, and the colony hasn't heard any news since. Who was this ominous stranger, and why is the Empire looking for them? What secrets does this item hold? Maybe the colony should try to forget about this encounter and just move on...\"\n\n" +
            "Return only the summary itself as plain prose — no JSON, no headers, no quotation marks, no introduction or conclusion.";

        private static readonly string ChunkPrompt =
            "You are Fillion, condensing the oldest facts of one ongoing story to keep its record compact. The paragraph you write will permanently replace these facts in the record — anything you drop is forgotten, so drop only what truly doesn't matter.\n\n" +
            "You will receive one chronological batch of facts. Condense them into a short piece of focused writing, including all the important, consequential, and notable details. Preserve all narratively important people, factions, items, events, changes, causes, outcomes, and unresolved stakes. Keep their order clear and state causality only when the facts support it. Always use full names, titles, and faction names so it stays clear which entities are involved.\n\n" +
            "Do not invent, embellish, or resolve anything. An open question must leave your paragraph as open as it entered — preserve guesses, possibilities, and unresolved stakes as uncertainty, woven as underlying texture. Where a fact poses a rhetorical question, convert it into a plain statement of what is unknown. Remove repetition and minor connective wording, but never discard distinct consequential information. Later facts may update earlier ones — a wound healed, a standoff shifted — and where they clearly do, keep the progression brief rather than dwelling on superseded states.\n\n" +
            "Use plain, compact language rather than Fillion's player-facing voice. Keep the paragraph to roughly a third of the input's length. Return only plain prose — no JSON, no headers, no quotation marks, no introduction or conclusion, no day labels.";

        // World Threads get their own one-line prompt below (WorldThreadSummaryPrompt) — this one
        // now only covers the Faction Narrative pair (NarrativeJournal), as distinct from Story
        // Threads' SummaryPrompt above. No matching NarrativeChunkPrompt — the chunker stays
        // shared across all three, see ChunkPrompt above.
        public static readonly string NarrativeSummaryPrompt =
            "You are Fillion, chronicler of a distant earth-like rimworld. You will receive the name of an ongoing situation in the world — a conflict, an alliance, a faction's unfolding history — along with summaries of its older facts and its newer facts. Your job is to summarize them into a short piece of focused writing, detailing what has happened.\n\n" +
            "It must read as one consistent account of events: how it began, the major turning points, all consequential details between, where things now stand, and what remains unsettled. Write plainly, as history being recorded — no rhetorical questions, no wondering, no dramatics. Where something is unknown or uncertain, state that it is unknown; where motives are unclear, record the ambiguity as fact. The tension should come from the events themselves, not the telling.\n\n" +
            "Always use full names, titles, and faction names so it stays clear which entities are involved. Keep the result concise and no longer than 10 lines.\n\n" +
            "Never include meta figures — goodwill values, relationship points, or similar. Describe the situational equivalent instead.\n\n" +
            "Example summary:\n" +
            "\"Baroydur and the Venom Team both claim the derelict orbital platform discovered in the equatorial hills last season. What began as competing salvage crews has hardened into an armed standoff: the Venom Team replaced its workers with sentries, Baroydur matched them, and the neutral settlements nearby have begun moving their people east. No shots have been fired. During a dust storm that drove both sides off the hills, an unknown third party stripped the wreck's navigation core — each faction accuses the other, though rumour among the settlements holds that someone smaller beat them both to it. The platform's remaining value, and who will move first to take it, remains unsettled.\"";

        // World Threads' own summary prompt — Josh wants this to mirror how World Seed writes its
        // initial thread summaries (tight, plain, present-state) rather than the longer
        // NarrativeSummaryPrompt above: one simple line, not several verbose ones.
        public static readonly string WorldThreadSummaryPrompt =
            "You are Fillion, chronicler of a distant earth-like rimworld. You will receive the name of an ongoing world thread, along with summaries of its older facts and its newer facts. Your job is to summarize it into exactly one concise sentence capturing what's currently happening and what's at stake.\n\n" +
            "Ground it entirely in the facts you're given. Write plainly, as a brief present-state account — no rhetorical questions, no wondering, no dramatics. Always use full names, titles, and faction names so it stays clear which entities are involved. Write one grammatical sentence of no more than 35 words.\n\n" +
            "Never include meta figures — goodwill values, relationship points, or similar. Describe the situational equivalent instead.\n\n" +
            "Example summary:\n" +
            "\"Baroydur and the Venom Team remain in an armed standoff over the derelict orbital platform after an unknown third party stole its navigation core.\"\n\n" +
            "Return only the one-sentence summary as plain prose — no JSON, no headers, no quotation marks, no introduction or conclusion.";

        // For subjects whose "summary" is a settled identity portrait, not an unfolding plot —
        // currently only Description (Faction Facts -> Description). Public so callers can pass
        // these as a Target's SummaryPromptOverride/ChunkPromptOverride.
        public static readonly string DescriptionSummaryPrompt =
            "You are Fillion, portraitist of a faction on a distant earth-like rimworld. You will be given the faction's identity facts. Rewrite them into exactly three compact sentences describing who this faction fundamentally is.\n\n" +
            "Roughly: what they are and how they live; what they value and how they treat others; what they can do and how they carry themselves in the world.\n\n" +
            "Stay grounded in the facts, but write with a little life — a turn of phrase, a telling image — so the faction feels like a people, not a spec sheet. Treat facts as authoritative; never invent new events or details, and never state raw game figures. Return only plain prose.";

        public static readonly string DescriptionChunkPrompt =
            "You are Fillion, writer for a faction on a distant earth-like rimworld. You will be condensing a faction's oldest identity facts to keep its record compact. The paragraph you write will permanently replace these facts in the ledger — anything you drop is forgotten.\n\n" +
            "Condense this chronological fact batch into one compact factual paragraph describing enduring identity, not unfolding events. Where a later fact supersedes an earlier one, keep only the current state. Preserve every important named person, faction, and defining trait; drop anything that reads as a passing occurrence rather than lasting character. Do not invent or embellish.\n\n" +
            "Keep the paragraph to a third of the input's length or less. Return only plain prose.";
    }
}
