using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.CapturedBy))]
    public static class Patch_PrisonerCapture
    {
        static void Postfix(Pawn_GuestTracker __instance, Faction by, Pawn byPawn)
        {
            try
            {
                if (by != Faction.OfPlayer) return;
                if (!__instance.IsPrisoner) return;

                Pawn prisoner = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
                if (prisoner == null) return;

                string prisonerName = prisoner.LabelShort;
                string text = byPawn != null
                    ? $"{byPawn.LabelShort} captured {prisonerName}, who became a colony prisoner"
                    : $"{prisonerName} became a colony prisoner";

                ColonyLedger.Current?.AppendEvent(Find.TickManager.TicksAbs, text);
            }
            catch { }
        }
    }
}
