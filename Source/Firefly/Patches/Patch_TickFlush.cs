using HarmonyLib;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    public static class Patch_TickManager_Flush
    {
        private static int _counter = 0;

        static void Postfix()
        {
            if (++_counter % 500 == 0)
                ColonyLedger.FlushPendingEvents();
        }
    }
}
