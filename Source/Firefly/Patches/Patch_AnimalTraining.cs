using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(Pawn_TrainingTracker), nameof(Pawn_TrainingTracker.Train))]
    public static class Patch_AnimalTraining_Train
    {
        [ThreadStatic]
        private static bool _wasAlreadyLearned;

        static void Prefix(Pawn_TrainingTracker __instance, TrainableDef td)
        {
            _wasAlreadyLearned = __instance.HasLearned(td);
        }

        static void Postfix(Pawn_TrainingTracker __instance, TrainableDef td, Pawn trainer)
        {
            try
            {
                if (_wasAlreadyLearned) return;
                if (!__instance.HasLearned(td)) return;
                if (td == TrainableDefOf.Tameness) return;

                string animal  = __instance.pawn?.LabelShort ?? "Animal";
                string skill   = td.LabelCap;
                string trainerName = trainer?.LabelShort;

                string text = trainerName.NullOrEmpty()
                    ? $"{animal} learned {skill}"
                    : $"{trainerName} trained {animal} how to {skill}";

                ColonyLedger.Current?.AppendEvent(Find.TickManager.TicksAbs, text);
            }
            catch { }
        }
    }
}
