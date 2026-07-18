using System.Collections.Generic;
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

    // Milestone 1: stub — vanilla fallback handles all incidents until the director is wired up.
    public class StorytellerComp_Fillion : StorytellerComp
    {
        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            yield break;
        }
    }
}
