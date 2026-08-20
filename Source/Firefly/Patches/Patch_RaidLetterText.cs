using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    // Vanilla always uses the faction's plural pawn noun in raid arrival text, even when the
    // generated raid contains only one pawn. Correct the opening sentence at the source so both
    // the player-facing letter and Firefly's archived copy receive accurate information.
    [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "GetLetterText")]
    public static class Patch_RaidLetterText
    {
        static void Postfix(IncidentParms parms, List<Pawn> pawns, ref string __result)
        {
            __result = Apply(parms, pawns, __result);
        }

        // Shared with Patch_RaidNarrativeIntercept, which calls this directly — Harmony reverse
        // patches copy only the target method's original IL, so this postfix never fires for text
        // obtained that way. One implementation, two call sites.
        public static string Apply(IncidentParms parms, List<Pawn> pawns, string text)
        {
            try
            {
                if (pawns?.Count != 1 || text.NullOrEmpty()) return text;

                Pawn pawn = pawns[0];
                if (pawn == null) return text;
                string raider = ColonyLedger.StripTags(pawn.kindDef?.label ?? "");
                string faction = ColonyLedger.StripTags(parms?.faction?.Name ?? pawn.Faction?.Name ?? "");
                if (raider.NullOrEmpty() || faction.NullOrEmpty()) return text;

                int sentenceEnd = text.IndexOf('.');
                if (sentenceEnd < 0) return text;

                string opening = $"A single {raider} from {faction} has arrived nearby.";
                return opening + text.Substring(sentenceEnd + 1);
            }
            catch { return text; }
        }
    }
}
