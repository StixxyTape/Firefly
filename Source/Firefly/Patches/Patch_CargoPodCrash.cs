using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    // Two-patch bridge: TryExecuteWorker sets a flag so DropThingsNear knows to capture
    // the items, then the Postfix reads them and writes the log entry.

    [HarmonyPatch(typeof(IncidentWorker_ResourcePodCrash), "TryExecuteWorker")]
    public static class Patch_CargoPodCrash
    {
        [ThreadStatic] internal static bool _active;
        [ThreadStatic] internal static List<Thing> _capturedThings;

        static void Prefix()
        {
            _active = true;
            _capturedThings = null;
        }

        static void Postfix()
        {
            try
            {
                var things = _capturedThings;
                if (things == null || things.Count == 0) return;

                // Plain g.Key.label was always the def's singular form regardless of how many
                // actually dropped — 12 gravlite panels crashing down got recorded as "some
                // gravlite panel", which read like a single stray item to the LLM instead of a
                // real haul. Pluralize against the real summed stack count instead.
                var labels = things
                    .GroupBy(t => t.def)
                    .Select(g => Find.ActiveLanguageWorker.Pluralize(g.Key.label, g.Sum(t => t.stackCount)))
                    .ToList();

                string contents = labels.Count == 1
                    ? labels[0]
                    : string.Join(", ", labels.Take(labels.Count - 1)) + " and " + labels.Last();

                ColonyLedger.Current?.AppendEvent(Find.TickManager.TicksAbs,
                    $"Cargo Pods containing some {contents} have crashed nearby");
            }
            catch { }
            finally
            {
                _active = false;
                _capturedThings = null;
            }
        }
    }

    [HarmonyPatch(typeof(DropPodUtility), nameof(DropPodUtility.DropThingsNear))]
    public static class Patch_DropThingsNear_Capture
    {
        static void Prefix(IEnumerable<Thing> things)
        {
            if (!Patch_CargoPodCrash._active) return;
            Patch_CargoPodCrash._capturedThings = things?.ToList();
        }
    }
}
