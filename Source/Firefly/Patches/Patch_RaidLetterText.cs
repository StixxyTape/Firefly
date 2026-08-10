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
            try
            {
                if (pawns?.Count != 1 || __result.NullOrEmpty()) return;

                Pawn pawn = pawns[0];
                if (pawn == null) return;
                string raider = ColonyLedger.StripTags(pawn.kindDef?.label ?? "");
                string faction = ColonyLedger.StripTags(parms?.faction?.Name ?? pawn.Faction?.Name ?? "");
                if (raider.NullOrEmpty() || faction.NullOrEmpty()) return;

                int sentenceEnd = __result.IndexOf('.');
                if (sentenceEnd < 0) return;

                string opening = $"A single {raider} from {faction} has arrived nearby.";
                __result = opening + __result.Substring(sentenceEnd + 1);
            }
            catch { }
        }
    }
}
