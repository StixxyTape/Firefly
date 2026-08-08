using System.Collections.Generic;
using Verse;

namespace Firefly
{
    public class StorySeedFact : IExposable
    {
        public long   Tick;
        public string Text;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Tick, "tick", 0L);
            Scribe_Values.Look(ref Text, "text", "");
        }
    }

    public class StorySeed : IExposable
    {
        public string              Id;
        public string              Name;
        public string              Description;
        public List<StorySeedFact> Facts = new List<StorySeedFact>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id,          "id",          "");
            Scribe_Values.Look(ref Name,        "name",        "");
            Scribe_Values.Look(ref Description, "description", "");
            Scribe_Collections.Look(ref Facts,  "facts",       LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && Facts == null)
                Facts = new List<StorySeedFact>();
        }
    }
}
