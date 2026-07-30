using RimWorld;

namespace Firefly
{
    public class StorytellerCompProperties_Fillion : StorytellerCompProperties
    {
        public StorytellerCompProperties_Fillion()
        {
            compClass = typeof(StorytellerComp_Fillion);
        }
    }

    // Placeholder for future LLM-driven incident logic.
    // Pacing is handled by the native comps declared in Storyteller_Fillion.xml.
    public class StorytellerComp_Fillion : StorytellerComp { }
}
