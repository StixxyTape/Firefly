using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(BattleLog), nameof(BattleLog.Add))]
    public static class Patch_BattleLog_Add
    {
        // BattleLog.Add fires hundreds of times a second during a raid, so every field read here is
        // a cached FieldInfo rather than a fresh Traverse. AccessTools walks base types, so looking
        // a shared field up from the most-derived type is safe.
        private static readonly FieldInfo RangedInitiatorPawn      = AccessTools.Field(typeof(BattleLogEntry_RangedImpact), "initiatorPawn");
        private static readonly FieldInfo RangedOriginalTargetPawn = AccessTools.Field(typeof(BattleLogEntry_RangedImpact), "originalTargetPawn");
        private static readonly FieldInfo RangedOriginalTargetThing = AccessTools.Field(typeof(BattleLogEntry_RangedImpact), "originalTargetThing");
        private static readonly FieldInfo RangedRecipientPawn      = AccessTools.Field(typeof(BattleLogEntry_RangedImpact), "recipientPawn");
        private static readonly FieldInfo RangedRecipientThing     = AccessTools.Field(typeof(BattleLogEntry_RangedImpact), "recipientThing");
        private static readonly FieldInfo RangedWeaponDef          = AccessTools.Field(typeof(BattleLogEntry_RangedImpact), "weaponDef");
        private static readonly FieldInfo RangedBattle             = AccessTools.Field(typeof(BattleLogEntry_RangedImpact), "battle");

        private static readonly FieldInfo MeleeInitiator      = AccessTools.Field(typeof(BattleLogEntry_MeleeCombat), "initiator");
        private static readonly FieldInfo MeleeRecipientPawn  = AccessTools.Field(typeof(BattleLogEntry_MeleeCombat), "recipientPawn");
        private static readonly FieldInfo MeleeOwnerEquipment = AccessTools.Field(typeof(BattleLogEntry_MeleeCombat), "ownerEquipmentDef");
        private static readonly FieldInfo MeleeToolLabel      = AccessTools.Field(typeof(BattleLogEntry_MeleeCombat), "toolLabel");
        private static readonly FieldInfo MeleeRuleDef        = AccessTools.Field(typeof(BattleLogEntry_MeleeCombat), "ruleDef");
        private static readonly FieldInfo MeleeBattle         = AccessTools.Field(typeof(BattleLogEntry_MeleeCombat), "battle");

        private static readonly FieldInfo DamageTakenRecipientPawn = AccessTools.Field(typeof(BattleLogEntry_DamageTaken), "recipientPawn");
        private static readonly FieldInfo DamageTakenRuleDef       = AccessTools.Field(typeof(BattleLogEntry_DamageTaken), "ruleDef");

        private static readonly FieldInfo TransitionDef        = AccessTools.Field(typeof(BattleLogEntry_StateTransition), "transitionDef");
        private static readonly FieldInfo TransitionInitiator  = AccessTools.Field(typeof(BattleLogEntry_StateTransition), "initiator");
        private static readonly FieldInfo TransitionCulpritHediff = AccessTools.Field(typeof(BattleLogEntry_StateTransition), "culpritHediffDef");
        private static readonly FieldInfo TransitionCulpritPart   = AccessTools.Field(typeof(BattleLogEntry_StateTransition), "culpritHediffTargetPart");
        private static readonly FieldInfo TransitionCulpritPartAlt = AccessTools.Field(typeof(BattleLogEntry_StateTransition), "culpritTargetPart");

        private static T Read<T>(FieldInfo field, object target) where T : class
        {
            if (field == null || target == null) return null;
            try { return field.GetValue(target) as T; }
            catch { return null; }
        }

        static void Postfix(LogEntry entry)
        {
            try
            {
                // RangedFire is just the firing event — RangedImpact has the actual outcome
                if (entry is BattleLogEntry_RangedFire) return;

                if (entry is BattleLogEntry_StateTransition)
                {
                    var concerns = entry.GetConcerns().ToList();
                    if (!concerns.OfType<Pawn>().Any(p => p.IsColonist)) return;
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
                    var concerns = entry.GetConcerns().ToList();
                    if (!concerns.OfType<Pawn>().Any(p => p.IsColonist)) return;
                    HandleDamageTaken(entry);
                    return;
                }
            }
            catch { }
        }

        private static void HandleRangedImpact(LogEntry entry)
        {
            Pawn     initiatorPawn       = Read<Pawn>(RangedInitiatorPawn, entry);
            Pawn     originalTargetPawn  = Read<Pawn>(RangedOriginalTargetPawn, entry);
            Thing    originalTargetThing = Read<Thing>(RangedOriginalTargetThing, entry);
            Pawn     recipientPawn       = Read<Pawn>(RangedRecipientPawn, entry);
            Thing    recipientThing      = Read<Thing>(RangedRecipientThing, entry);
            ThingDef weaponDef           = Read<ThingDef>(RangedWeaponDef, entry);

            string initiator   = ColonyLedger.PawnFullName(initiatorPawn);
            string initiatorId = initiatorPawn?.ThingID ?? initiatorPawn?.LabelShort ?? "?";
            string target      = originalTargetPawn != null
                ? ColonyLedger.PawnFullName(originalTargetPawn)
                : originalTargetThing?.LabelShort ?? "?";
            string targetId    = originalTargetPawn?.ThingID ?? originalTargetThing?.ThingID ?? target;
            string weapon      = weaponDef?.label;

            bool reachedTarget       = recipientPawn != null && recipientPawn == originalTargetPawn;
            bool initiatorIsColonist = initiatorPawn?.IsColonist == true;
            if (!initiatorIsColonist && !(originalTargetPawn?.IsColonist == true)) return;

            string coverHit = null;
            if (!reachedTarget)
            {
                if (recipientPawn != null) coverHit = ColonyLedger.PawnFullName(recipientPawn);
                else if (recipientThing != null) coverHit = recipientThing.LabelShort;
            }

            var colonistPawn = initiatorIsColonist ? initiatorPawn : originalTargetPawn;
            string battleId  = Read<Battle>(RangedBattle, entry)?.GetUniqueLoadID()
                               ?? colonistPawn?.records?.BattleActive?.GetUniqueLoadID();

            ColonyLedger.Current?.CaptureBattleEvent(initiator, initiatorId, target, targetId, reachedTarget, weapon, coverHit, initiatorIsColonist, battleId, entry as LogEntry_DamageResult, initiatorPawn, originalTargetPawn);
        }

        private static void HandleMelee(LogEntry entry)
        {
            Pawn        initiator      = Read<Pawn>(MeleeInitiator, entry);
            Pawn        recipientPawn  = Read<Pawn>(MeleeRecipientPawn, entry);
            ThingDef    ownerEquipment = Read<ThingDef>(MeleeOwnerEquipment, entry);
            string      toolLabel      = Read<string>(MeleeToolLabel, entry);
            RulePackDef ruleDef        = Read<RulePackDef>(MeleeRuleDef, entry);

            string initiatorName = ColonyLedger.PawnFullName(initiator);
            string initiatorId   = initiator?.ThingID ?? initiator?.LabelShort ?? "?";
            string targetName    = ColonyLedger.PawnFullName(recipientPawn);
            string targetId      = recipientPawn?.ThingID ?? recipientPawn?.LabelShort ?? "?";
            string weapon        = ownerEquipment?.label ?? toolLabel;

            string ruleDefName       = ruleDef?.defName ?? "";
            bool reachedTarget       = !ruleDefName.Contains("Dodge") && !ruleDefName.Contains("Miss");
            bool initiatorIsColonist = initiator?.IsColonist == true;
            if (!initiatorIsColonist && !(recipientPawn?.IsColonist == true)) return;
            string coverHit          = ruleDefName.Contains("Dodge") ? $"{targetName} dodging" : null;

            var colonistPawn = initiatorIsColonist ? initiator : recipientPawn;
            string battleId  = Read<Battle>(MeleeBattle, entry)?.GetUniqueLoadID()
                               ?? colonistPawn?.records?.BattleActive?.GetUniqueLoadID();

            ColonyLedger.Current?.CaptureBattleEvent(initiatorName, initiatorId, targetName, targetId, reachedTarget, weapon, coverHit, initiatorIsColonist, battleId, entry as LogEntry_DamageResult, initiator, recipientPawn);
        }

        private static void HandleDamageTaken(LogEntry entry)
        {
            Pawn        recipientPawn = Read<Pawn>(DamageTakenRecipientPawn, entry);
            RulePackDef ruleDef       = Read<RulePackDef>(DamageTakenRuleDef, entry);
            if (recipientPawn == null || !recipientPawn.IsColonist) return;
            string victim      = ColonyLedger.PawnFullName(recipientPawn);
            string hazardLabel = GetHazardLabel(ruleDef);
            ColonyLedger.Current?.CaptureHazardEvent(victim, hazardLabel, entry as LogEntry_DamageResult, recipientPawn);
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

            // GetConcerns() returns [initiator, subject] for state transitions — subject is last.
            Pawn subject = pawns.Last();

            var transitionDef = Read<RulePackDef>(TransitionDef, entry);
            string stateChange;
            if (transitionDef == RulePackDefOf.Transition_Downed)
                stateChange = "downed";
            else if (transitionDef?.defName != null)
                stateChange = transitionDef.defName.Replace("Transition_", "").ToLower();
            else
                stateChange = "killed";

            Pawn initiator = Read<Pawn>(TransitionInitiator, entry);

            HediffDef culpritHediff = Read<HediffDef>(TransitionCulpritHediff, entry);
            BodyPartRecord culpritPart = Read<BodyPartRecord>(TransitionCulpritPart, entry)
                                      ?? Read<BodyPartRecord>(TransitionCulpritPartAlt, entry);

            var ledger = ColonyLedger.Current;
            if (ledger == null) return;

            string causeStr     = null;
            string subjectTag   = ledger.IntroduceTag(subject);
            string initiatorTag = initiator != null ? ledger.IntroduceTag(initiator) : "";
            string subjectName  = ColonyLedger.PawnFullName(subject);
            string initiatorName = initiator != null ? ColonyLedger.PawnFullName(initiator) : null;
            var sb = new System.Text.StringBuilder();
            sb.Append(subjectName);
            sb.Append(subjectTag);
            sb.Append($" {stateChange}");
            if (initiatorName != null) sb.Append($" by {initiatorName}{initiatorTag}");
            if (culpritHediff != null)
            {
                causeStr = culpritHediff.LabelCap;
                if (culpritPart != null) causeStr += $", {culpritPart.LabelShort}";
                sb.Append($" ({causeStr})");
            }

            ledger.CaptureStateChange(subjectName, sb.ToString());
            ledger.CaptureOutcome(subjectName, subject.ThingID, stateChange, initiatorName, causeStr);
        }
    }
}
