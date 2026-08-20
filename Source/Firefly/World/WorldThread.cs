using System.Collections.Generic;
using Verse;

namespace Firefly
{
    // A simulation thread happening at the world/faction level, independent of the player's
    // colony — wars, treaties, competitions between factions, or general world happenings (big
    // threats, quests). Purely narrative for now: nothing here ever mutates real game state.
    // Deliberately its own type rather than reusing StoryThread — even though its shape is now
    // identical (Id/Title/Journal), keeping them separate types avoids conflating a colony-scoped
    // concept with a world-scoped one, and leaves room for World Thread-specific fields again
    // later without disturbing Story Threads.
    public class WorldThread : IExposable
    {
        public string Id      = "";
        public string Title   = "";

        public int CreatedDay;
        public int LastUpdatedDay;

        public JournalRecord Journal = new JournalRecord();

        public string ActiveSummary => Journal.ActiveSummary;
        public List<JournalFact> Facts => Journal.Facts;
        public List<JournalFactChunk> Chunks => Journal.Chunks;
        public int ChunkedThroughFactIndex => Journal.ChunkedThroughFactIndex;
        public long LastTouchedTick => Journal.LastTouchedTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id,      "id",      "");
            Scribe_Values.Look(ref Title,   "title",   "");
            Scribe_Values.Look(ref CreatedDay,     "createdDay",     0);
            Scribe_Values.Look(ref LastUpdatedDay, "lastUpdatedDay", 0);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // Read the pre-shared-record layout directly, then move it into Journal — same
                // migration shape as StoryThread/FactionSnapshot.
                string oldPremise = "";
                string oldSummary = "";
                List<JournalFact>? oldDevelopments = null;
                long oldTouched = 0L;
                Scribe_Values.Look(ref oldPremise, "premise", "");
                Scribe_Values.Look(ref oldSummary, "summary", "");
                Scribe_Collections.Look(ref oldDevelopments, "developments", LookMode.Deep);
                Scribe_Values.Look(ref oldTouched, "lastTouchedTick", 0L);
                JournalRecord? loadedJournal = null;
                Scribe_Deep.Look(ref loadedJournal, "journal");
                if (loadedJournal == null)
                {
                    Journal = new JournalRecord
                    {
                        ActiveSummary = oldSummary ?? "",
                        Facts = oldDevelopments ?? new List<JournalFact>(),
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
