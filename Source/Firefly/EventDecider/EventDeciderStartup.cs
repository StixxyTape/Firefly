using Verse;

namespace Firefly
{
    // Adapter registration needs DefDatabase populated (DiseaseAdapter.CoveredDefs enumerates
    // DefDatabase<IncidentDef>.AllDefsListForReading) — mod constructors run during
    // LoadedModManager.CreateModClasses(), which is BEFORE XML parsing/ResolveAllReferences
    // populate the def databases (verified against the installed 1.6 assembly, 2026-08-22).
    // Registering from FireflyMod's constructor therefore always sees an empty DefDatabase and
    // silently registers nothing — no error, no warning, the Event Decider just never holds a
    // single incident. [StaticConstructorOnStartup] is RimWorld's standard fix for exactly this
    // ordering problem: it runs once defs are loaded and resolved, shortly before the main menu
    // appears.
    [StaticConstructorOnStartup]
    internal static class EventDeciderStartup
    {
        static EventDeciderStartup()
        {
            Patch_EventDeciderIntercept.Register(new DiseaseAdapter());
        }
    }
}
