using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(BattleLog), nameof(BattleLog.Add))]
    public static class Patch_BattleLog_Add
    {
        static void Postfix(LogEntry entry)
        {
            try
            {
                // RangedFire is just the firing event — RangedImpact has the actual outcome
                if (entry is BattleLogEntry_RangedFire) return;

                var concerns = entry.GetConcerns().ToList();
                if (!concerns.OfType<Pawn>().Any(p => p.IsColonist)) return;

                if (entry is BattleLogEntry_StateTransition)
                {
                    HandleStateTransition(concerns, entry);
                    return;
                }

                if (entry is BattleLogEntry_RangedImpact)
                {
                    HandleRangedImpact(entry);
                    return;
                }

                if (entry is BattleLogEntry_MeleeCombat)
                {
                    HandleMelee(entry);
                    return;
                }

                if (entry is BattleLogEntry_DamageTaken)
                {
                    HandleDamageTaken(entry);
                    return;
                }
            }
            catch { }
        }

        private static void HandleRangedImpact(LogEntry entry)
        {
            var t = Traverse.Create(entry);

            Pawn     initiatorPawn      = t.Field("initiatorPawn").GetValue<Pawn>();
            Pawn     originalTargetPawn = t.Field("originalTargetPawn").GetValue<Pawn>();
            Thing    originalTargetThing = t.Field("originalTargetThing").GetValue<Thing>();
            Pawn     recipientPawn      = t.Field("recipientPawn").GetValue<Pawn>();
            Thing    recipientThing     = t.Field("recipientThing").GetValue<Thing>();
            ThingDef weaponDef          = t.Field("weaponDef").GetValue<ThingDef>();

            string initiator   = initiatorPawn?.LabelShort ?? "?";
            string initiatorId = initiatorPawn?.ThingID    ?? initiator;
            string target      = originalTargetPawn?.LabelShort ?? originalTargetThing?.LabelShort ?? "?";
            string targetId    = originalTargetPawn?.ThingID    ?? originalTargetThing?.ThingID    ?? target;
            string weapon      = weaponDef?.label;

            bool reachedTarget       = recipientPawn != null && recipientPawn == originalTargetPawn;
            bool initiatorIsColonist = initiatorPawn?.IsColonist == true;

            string coverHit = null;
            if (!reachedTarget)
            {
                if (recipientPawn != null) coverHit = recipientPawn.LabelShort;
                else if (recipientThing != null) coverHit = recipientThing.LabelShort;
            }

            var colonistPawn = initiatorIsColonist ? initiatorPawn : originalTargetPawn;
            string battleId  = colonistPawn?.records?.BattleActive?.GetUniqueLoadID();

            ColonyLedger.CaptureBattleEvent(initiator, initiatorId, target, targetId, reachedTarget, weapon, coverHit, initiatorIsColonist, battleId, entry as LogEntry_DamageResult, initiatorPawn, originalTargetPawn);
        }

        private static void HandleMelee(LogEntry entry)
        {
            var t = Traverse.Create(entry);

            Pawn          initiator      = t.Field("initiator").GetValue<Pawn>();
            Pawn          recipientPawn  = t.Field("recipientPawn").GetValue<Pawn>();
            ThingDef      ownerEquipment = t.Field("ownerEquipmentDef").GetValue<ThingDef>();
            string        toolLabel      = t.Field("toolLabel").GetValue<string>();
            RulePackDef   ruleDef        = t.Field("ruleDef").GetValue<RulePackDef>();

            string initiatorName = initiator?.LabelShort ?? "?";
            string initiatorId   = initiator?.ThingID    ?? initiatorName;
            string targetName    = recipientPawn?.LabelShort ?? "?";
            string targetId      = recipientPawn?.ThingID    ?? targetName;
            string weapon        = ownerEquipment?.label ?? toolLabel;

            string ruleDefName       = ruleDef?.defName ?? "";
            bool reachedTarget       = !ruleDefName.Contains("Dodge") && !ruleDefName.Contains("Miss");
            bool initiatorIsColonist = initiator?.IsColonist == true;
            string coverHit          = ruleDefName.Contains("Dodge") ? $"{targetName} dodging" : null;

            var colonistPawn = initiatorIsColonist ? initiator : recipientPawn;
            string battleId  = colonistPawn?.records?.BattleActive?.GetUniqueLoadID();

            ColonyLedger.CaptureBattleEvent(initiatorName, initiatorId, targetName, targetId, reachedTarget, weapon, coverHit, initiatorIsColonist, battleId, entry as LogEntry_DamageResult, initiator, recipientPawn);
        }

        private static void HandleDamageTaken(LogEntry entry)
        {
            var t = Traverse.Create(entry);
            Pawn        recipientPawn = t.Field("recipientPawn").GetValue<Pawn>();
            RulePackDef ruleDef       = t.Field("ruleDef").GetValue<RulePackDef>();
            if (recipientPawn == null || !recipientPawn.IsColonist) return;
            string victim      = recipientPawn.LabelShort ?? "?";
            string hazardLabel = GetHazardLabel(ruleDef);
            ColonyLedger.CaptureHazardEvent(victim, hazardLabel, entry as LogEntry_DamageResult, recipientPawn);
        }

        private static readonly Regex _pascalCase = new Regex("([A-Z])", RegexOptions.Compiled);
        private static string GetHazardLabel(RulePackDef def)
        {
            if (def == null) return "unknown hazard";
            string name = def.defName;
            if (name.StartsWith("DamageEvent_")) name = name.Substring("DamageEvent_".Length);
            return _pascalCase.Replace(name, " $1").Trim().ToLower();
        }

        private static void HandleStateTransition(List<Thing> concerns, LogEntry entry)
        {
            var pawns = concerns.OfType<Pawn>().ToList();
            if (pawns.Count == 0) return;

            Pawn subject = pawns.Last();
            var t = Traverse.Create(entry);

            var transitionDef = t.Field("transitionDef").GetValue<RulePackDef>();
            string stateChange = transitionDef == RulePackDefOf.Transition_Downed ? "downed" : "killed";

            Pawn initiator = null;
            try { initiator = t.Field("initiator").GetValue<Pawn>(); } catch { }

            HediffDef culpritHediff = null;
            BodyPartRecord culpritPart = null;
            try { culpritHediff = t.Field("culpritHediffDef").GetValue<HediffDef>(); } catch { }
            try { culpritPart   = t.Field("culpritHediffTargetPart").GetValue<BodyPartRecord>()
                               ?? t.Field("culpritTargetPart").GetValue<BodyPartRecord>(); } catch { }

            string causeStr    = null;
            string subjectTag  = ColonyLedger.IntroduceTag(subject);
            string initiatorTag = initiator != null ? ColonyLedger.IntroduceTag(initiator) : "";
            var sb = new System.Text.StringBuilder();
            sb.Append(subject.LabelShort);
            sb.Append(subjectTag);
            sb.Append($" {stateChange}");
            if (initiator != null) sb.Append($" by {initiator.LabelShort}{initiatorTag}");
            if (culpritHediff != null)
            {
                causeStr = culpritHediff.LabelCap;
                if (culpritPart != null) causeStr += $", {culpritPart.LabelShort}";
                sb.Append($" ({causeStr})");
            }

            ColonyLedger.CaptureStateChange(subject.LabelShort, sb.ToString());
            ColonyLedger.CaptureOutcome(subject.LabelShort, stateChange, initiator?.LabelShort, causeStr);
        }
    }
}
