using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(Quest), nameof(Quest.End))]
    public static class Patch_Quest_End
    {
        static void Postfix(Quest __instance, QuestEndOutcome outcome)
        {
            try
            {
                if (__instance.hidden) return;
                string name = __instance.name ?? __instance.root?.defName ?? "unknown quest";
                ColonyLedger.Current?.CaptureDecision($"[Quest Resolved] {name} — {outcome}");
            }
            catch { }
        }
    }
}
