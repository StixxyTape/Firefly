using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    // Disease victim choice is not an IncidentParms field. The adapter stages the requested pawn
    // against the exact parms object; ActualVictims consumes it once, after vanilla has selected
    // victims but before ApplyToPawns applies any hediff.
    public sealed class DiseaseAdapter : IIncidentAdapter
    {
        public const string VictimField = "victim_id";

        private sealed class StagedVictim
        {
            public Pawn Pawn;
        }

        private static readonly ConditionalWeakTable<IncidentParms, StagedVictim> StagedVictims =
            new ConditionalWeakTable<IncidentParms, StagedVictim>();

        private static readonly MethodInfo PotentialVictimsMethod = AccessTools.Method(
            typeof(IncidentWorker_Disease), "PotentialVictims");

        public IEnumerable<IncidentDef> CoveredDefs => DefDatabase<IncidentDef>.AllDefsListForReading
            .Where(incident => incident?.Worker is IncidentWorker_DiseaseHuman);

        public IReadOnlyList<string> HonoredFields { get; } = new[] { VictimField };

        public IReadOnlyDictionary<string, string> DescribeAllowedFields(IncidentWorker worker,
            IncidentParms parms)
        {
            List<Pawn> victims = GetPotentialVictims(worker, parms);
            string choices = string.Join(", ", victims.Select(p =>
                $"{p.ThingID} ({p.LabelShortCap})"));
            return new Dictionary<string, string>
            {
                [VictimField] = victims.Count == 0
                    ? "No eligible values; omit this field."
                    : "Choose at most one exact id: " + choices,
            };
        }

        public bool Validate(IncidentWorker worker, IncidentParms parms, string fieldName,
            string proposedValue)
        {
            return string.Equals(fieldName, VictimField, StringComparison.OrdinalIgnoreCase) &&
                GetPotentialVictims(worker, parms).Any(p =>
                    string.Equals(p.ThingID, proposedValue, StringComparison.Ordinal));
        }

        public void Apply(IncidentWorker worker, IncidentParms parms,
            IReadOnlyDictionary<string, string> validatedValues)
        {
            if (validatedValues == null ||
                !validatedValues.TryGetValue(VictimField, out string pawnId)) return;
            Pawn pawn = GetPotentialVictims(worker, parms).FirstOrDefault(p =>
                string.Equals(p.ThingID, pawnId, StringComparison.Ordinal));
            if (pawn == null) return;
            StagedVictims.Remove(parms);
            StagedVictims.Add(parms, new StagedVictim { Pawn = pawn });
        }

        internal static void OverrideVictims(IncidentWorker worker, IncidentParms parms,
            ref IEnumerable<Pawn> result)
        {
            if (parms == null || !StagedVictims.TryGetValue(parms, out StagedVictim staged)) return;
            StagedVictims.Remove(parms);

            List<Pawn> potential = GetPotentialVictims(worker, parms);
            if (!potential.Contains(staged.Pawn))
            {
                Log.Warning($"[Firefly] Staged disease victim {staged.Pawn?.ThingID ?? "<missing>"} " +
                    "became ineligible; using vanilla victim selection.");
                return;
            }

            // ActualVictims returns a lazy randomized sequence. Materialize it exactly once;
            // enumerating it for Count and then again would perform vanilla's random selection
            // twice and could produce two different victim sets.
            List<Pawn> original = result?.ToList() ?? new List<Pawn>();
            if (original.Count == 0) return;

            // Preserve vanilla's victim count: replace the first choice, retain the remaining
            // distinct choices, and never broaden the set beyond PotentialVictims.
            int count = original.Count;
            var replacement = new List<Pawn> { staged.Pawn };
            foreach (Pawn pawn in original)
            {
                if (pawn != null && pawn != staged.Pawn && potential.Contains(pawn))
                    replacement.Add(pawn);
                if (replacement.Count >= count) break;
            }
            result = replacement;
        }

        private static List<Pawn> GetPotentialVictims(IncidentWorker worker, IncidentParms parms)
        {
            if (!(worker is IncidentWorker_Disease) || parms?.target == null || PotentialVictimsMethod == null)
                return new List<Pawn>();
            try
            {
                return ((IEnumerable<Pawn>)PotentialVictimsMethod.Invoke(worker,
                    new object[] { parms.target }))?.Where(p => p != null).Distinct().ToList()
                    ?? new List<Pawn>();
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Could not enumerate eligible disease victims: {e.Message}");
                return new List<Pawn>();
            }
        }
    }

    [HarmonyPatch]
    internal static class Patch_EventDeciderDiseaseVictims
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo human = AccessTools.DeclaredMethod(typeof(IncidentWorker_DiseaseHuman), "ActualVictims");
            if (human != null) yield return human;
        }

        private static void Postfix(IncidentWorker __instance, IncidentParms parms,
            ref IEnumerable<Pawn> __result) =>
            DiseaseAdapter.OverrideVictims(__instance, parms, ref __result);
    }
}
