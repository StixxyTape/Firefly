using HarmonyLib;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(PlayLog), nameof(PlayLog.Add))]
    public static class Patch_PlayLog_Add
    {
        static void Postfix(LogEntry entry)
        {
            try { ColonyLedger.Current?.CaptureLogEntry(entry); }
            catch { }
        }
    }
}
