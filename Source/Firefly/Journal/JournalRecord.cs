using System;
using System.Collections.Generic;
using Verse;

namespace Firefly
{
    public class JournalFact : IExposable
    {
        public long Tick;
        public int Day;
        public string Text = "";

        public void ExposeData()
        {
            Scribe_Values.Look(ref Tick, "tick", 0L);
            Scribe_Values.Look(ref Day, "day", 0);
            Scribe_Values.Look(ref Text, "text", "");
        }
    }

    public class JournalFactChunk : IExposable
    {
        public int StartDay;
        public int EndDay;
        public string Summary = "";

        public void ExposeData()
        {
            Scribe_Values.Look(ref StartDay, "startDay", 0);
            Scribe_Values.Look(ref EndDay, "endDay", 0);
            Scribe_Values.Look(ref Summary, "summary", "");
        }
    }

    public class JournalRecord : IExposable
    {
        public string ActiveSummary = "";
        public List<JournalFact> Facts = new List<JournalFact>();
        public List<JournalFactChunk> Chunks = new List<JournalFactChunk>();
        public int ChunkedThroughFactIndex;
        public int FactRevision;
        public int SummarizedRevision;
        public long LastTouchedTick;

        public bool SummaryStale => FactRevision > SummarizedRevision;

        public bool AddFact(long tick, int day, string text)
        {
            string? flat = ColonyLedger.StripTags(text ?? "")
                .Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (string.IsNullOrEmpty(flat)) return false;
            Facts.Add(new JournalFact { Tick = tick, Day = day, Text = flat });
            FactRevision++;
            LastTouchedTick = tick;
            return true;
        }

        public void SetActiveSummary(string summary, int coveredRevision)
        {
            if (summary.NullOrEmpty()) return;
            ActiveSummary = summary.Trim();
            SummarizedRevision = Math.Max(SummarizedRevision, coveredRevision);
            LastTouchedTick = GenTicks.TicksAbs;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref ActiveSummary, "activeSummary", "");
            Scribe_Collections.Look(ref Facts, "facts", LookMode.Deep);
            Scribe_Collections.Look(ref Chunks, "chunks", LookMode.Deep);
            Scribe_Values.Look(ref ChunkedThroughFactIndex, "chunkedThroughFactIndex", 0);
            Scribe_Values.Look(ref FactRevision, "factRevision", 0);
            Scribe_Values.Look(ref SummarizedRevision, "summarizedRevision", 0);
            Scribe_Values.Look(ref LastTouchedTick, "lastTouchedTick", 0L);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (Facts == null) Facts = new List<JournalFact>();
                if (Chunks == null) Chunks = new List<JournalFactChunk>();
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Facts.RemoveAll(f => f == null || f.Text.NullOrEmpty());
                Chunks.RemoveAll(c => c == null || c.Summary.NullOrEmpty());
                ChunkedThroughFactIndex = Math.Max(0, Math.Min(ChunkedThroughFactIndex, Facts.Count));
                FactRevision = Math.Max(FactRevision, Facts.Count);
                SummarizedRevision = Math.Max(0, Math.Min(SummarizedRevision, FactRevision));
                if (SummarizedRevision == 0 && !ActiveSummary.NullOrEmpty())
                    SummarizedRevision = FactRevision;
            }
        }
    }
}
