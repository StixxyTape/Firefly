using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.SetGuestStatus))]
    public static class Patch_PrisonerCapture
    {
        [ThreadStatic]
        private static bool _wasAlreadyPlayerPrisoner;

        static void Prefix(Pawn_GuestTracker __instance)
        {
            _wasAlreadyPlayerPrisoner = __instance.IsPrisoner && __instance.HostFaction == Faction.OfPlayer;
        }

        static void Postfix(Pawn_GuestTracker __instance, Faction newHost, GuestStatus guestStatus)
        {
            try
            {
                if (guestStatus != GuestStatus.Prisoner) return;
                if (newHost != Faction.OfPlayer) return;
                if (_wasAlreadyPlayerPrisoner) return;

                Pawn prisoner = Traverse.Create(__instance).Field("pawn").GetValue<Pawn>();
                if (prisoner == null) return;

                ColonyLedger.Current?.AppendEvent(Find.TickManager.TicksAbs,
                    $"{prisoner.LabelShort} became a colony prisoner");
            }
            catch { }
        }
    }
}
