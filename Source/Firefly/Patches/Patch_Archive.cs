using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(Archive), "Add")]
    public static class Patch_Archive_Add
    {
        static void Postfix(IArchivable archivable)
        {
            try { ColonyLedger.CaptureArchiveEntry(archivable); }
            catch { }
        }
    }
}
