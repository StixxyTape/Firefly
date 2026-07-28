using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(Messages), "Message", new[] { typeof(Message), typeof(bool) })]
    public static class Patch_Messages_Message
    {
        static void Prefix(Message msg, bool historical)
        {
            try
            {
                if (historical) return;
                if (msg?.text == null) return;
                var def = msg.def;
                if (def == MessageTypeDefOf.RejectInput ||
                    def == MessageTypeDefOf.CautionInput ||
                    def == MessageTypeDefOf.SilentInput) return;
                ColonyLedger.Current?.CaptureMessage(msg.text);
            }
            catch { }
        }
    }
}
