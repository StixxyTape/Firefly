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

    public class StoryThread : IExposable
    {
        public string Id = "";
        public string Name = "";
        public JournalRecord Journal = new JournalRecord();

        public string ActiveSummary => Journal.ActiveSummary;
        public List<JournalFact> Facts => Journal.Facts;
        public List<JournalFactChunk> Chunks => Journal.Chunks;
        public int ChunkedThroughFactIndex => Journal.ChunkedThroughFactIndex;
        public long LastTouchedTick => Journal.LastTouchedTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "id", "");
            Scribe_Values.Look(ref Name, "name", "");

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // Read the pre-shared-record layout directly, then move it into Journal.
                string oldSummary = "";
                List<JournalFact>? oldFacts = null;
                List<JournalFactChunk>? oldChunks = null;
                int oldCursor = 0;
                long oldTouched = 0L;
                Scribe_Values.Look(ref oldSummary, "description", "");
                Scribe_Collections.Look(ref oldFacts, "facts", LookMode.Deep);
                Scribe_Collections.Look(ref oldChunks, "chunks", LookMode.Deep);
                Scribe_Values.Look(ref oldCursor, "chunkedThroughFactIndex", 0);
                Scribe_Values.Look(ref oldTouched, "lastTouchedTick", 0L);
                JournalRecord? loadedJournal = null;
                Scribe_Deep.Look(ref loadedJournal, "journal");
                if (loadedJournal == null)
                {
                    Journal = new JournalRecord
                    {
                        ActiveSummary = oldSummary ?? "",
                        Facts = oldFacts ?? new List<JournalFact>(),
                        Chunks = oldChunks ?? new List<JournalFactChunk>(),
                        ChunkedThroughFactIndex = oldCursor,
                        LastTouchedTick = oldTouched,
                    };
                }
                else Journal = loadedJournal;
            }
            else
                Scribe_Deep.Look(ref Journal, "journal");

            if (Scribe.mode == LoadSaveMode.PostLoadInit && Journal == null)
                Journal = new JournalRecord();
        }
    }
}
