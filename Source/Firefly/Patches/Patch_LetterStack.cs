using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class Patch_LetterStack_ReceiveLetter
    {
        static void Postfix(Letter let)
        {
            try
            {
                var ledger = ColonyLedger.Current;
                if (ledger == null || let == null) return;

                // Only process letters tied to an external faction (visiting groups, raids, caravans).
                // Disease/injury/internal event letters have no relatedFaction.
                if (let.relatedFaction == null || let.relatedFaction == Faction.OfPlayer) return;

                string archiveLabel = (let as IArchivable)?.ArchivedLabel;

                Pawn pawn = let.lookTargets?.PrimaryTarget.Thing as Pawn;
                if (pawn == null || pawn.IsFreeColonist || pawn.IsPrisonerOfColony || pawn.IsSlaveOfColony) return;
                if (archiveLabel.NullOrEmpty()) return;

                ledger.IntroduceEventLeader(pawn, archiveLabel, Find.TickManager.TicksAbs);
            }
            catch { }
        }
    }
}
