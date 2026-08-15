using System.Collections.Generic;
using Verse;

namespace Firefly
{
    // Durable input for a day whose Story Thread scan has not completed. Keeping the original
    // record makes an interrupted or exhausted request recoverable after save/load without
    // reconstructing a prompt from mutable colony state.
    public class PendingThreadScan : IExposable
    {
        public int Day;
        public string Timeline = "";

        public void ExposeData()
        {
            Scribe_Values.Look(ref Day, "day", 0);
            Scribe_Values.Look(ref Timeline, "timeline", "");
        }
    }

    public class StoryThreadFact : IExposable
    {
        public long   Tick;
        // Colony-relative day (matches DailyRecord.Day / the daily timeline's numbering) —
        // the day this fact was recorded, set directly from the pipeline rather than
        // reconstructed from Tick, since Tick is an absolute-calendar tick (RimWorld.GenDate's
        // year-5500 epoch) and deriving a "day" from it drifts from the colony's own day count
        // as soon as a colony starts anywhere but day 1 of the in-game year.
        public int    Day;
        public string Text;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Tick, "tick", 0L);
            Scribe_Values.Look(ref Day,  "day",  0);
            Scribe_Values.Look(ref Text, "text", "");
        }
    }

    // One immutable, permanent condensation of a run of a thread's facts — written once by the
    // chunk call and never revised afterward, so re-condensing never risks the same rewrite-of-a-
    // rewrite drift the per-thread summarizer is designed to avoid. Purely a hidden input to that
    // summarizer; the full Facts list it was drawn from is never touched and still backs the UI.
    public class StoryThreadChunk : IExposable
    {
        public int    StartDay;
        public int    EndDay;
        public string Summary;

        public void ExposeData()
        {
            Scribe_Values.Look(ref StartDay, "startDay", 0);
            Scribe_Values.Look(ref EndDay,   "endDay",   0);
            Scribe_Values.Look(ref Summary,  "summary",  "");
        }
    }

    public class StoryThread : IExposable
    {
        public string              Id;
        public string              Name;
        public string              Description;
        public List<StoryThreadFact> Facts = new List<StoryThreadFact>();

        // Growing, append-only list of chunks (see StoryThreadChunk) plus a cursor marking how
        // many of Facts have already been folded into one — the boundary a new chunk claims
        // before it starts writing, so a slow chunk call in flight can never overlap with the
        // next one.
        public List<StoryThreadChunk> Chunks = new List<StoryThreadChunk>();
        public int                    ChunkedThroughFactIndex;

        // Tick this thread was last created or given a fact/summary update — lets the UI show
        // an "updated today" badge without re-deriving it from the Facts list each frame.
        public long                LastTouchedTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id,          "id",          "");
            Scribe_Values.Look(ref Name,        "name",        "");
            Scribe_Values.Look(ref Description, "description", "");
            Scribe_Collections.Look(ref Facts,  "facts",       LookMode.Deep);
            Scribe_Collections.Look(ref Chunks, "chunks",      LookMode.Deep);
            Scribe_Values.Look(ref ChunkedThroughFactIndex, "chunkedThroughFactIndex", 0);
            Scribe_Values.Look(ref LastTouchedTick, "lastTouchedTick", 0L);
            if (Scribe.mode == LoadSaveMode.LoadingVars && Facts == null)
                Facts = new List<StoryThreadFact>();
            if (Scribe.mode == LoadSaveMode.LoadingVars && Chunks == null)
                Chunks = new List<StoryThreadChunk>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Facts.RemoveAll(f => f == null || f.Text.NullOrEmpty());
                Chunks.RemoveAll(c => c == null || c.Summary.NullOrEmpty());
                ChunkedThroughFactIndex = System.Math.Max(0,
                    System.Math.Min(ChunkedThroughFactIndex, Facts.Count));
            }
        }
    }
}
