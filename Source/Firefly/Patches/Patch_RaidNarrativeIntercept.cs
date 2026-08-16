using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    // Defers a standard enemy raid's map-commit (spawning, letter, Lord) until Fillion has had a
    // chance to rewrite its letter to match the colony's active story threads, or a short
    // deadline expires. Any raid this patch doesn't recognize as a safe, plain vanilla path
    // (custom pawnKind, an overridden strategy/arrival worker, non-map target, Firefly disabled,
    // no API key configured) falls through to vanilla completely untouched.
    //
    // Boundary: this prefixes IncidentWorker_RaidEnemy.TryExecuteWorker itself, not
    // TryGenerateRaidInfo. Skipping the original here and reporting success lets the outer
    // IncidentWorker.TryExecute / Storyteller.TryFire finish immediately and normally — tale
    // recording, RecordIncidentFired, codex discovery, story-state notification, and the
    // storyteller's own pacing/cooldown bookkeeping all happen on their real synchronous timing.
    // Only the raid's own completion (spawning pawns, sending the letter, creating the Lord) is
    // deferred — nothing has spawned or touched the map at the point we defer, so there is
    // nothing to hide and nothing for another mod to see as a spawn-then-despawn cycle.
    //
    // Ground truth for every call below was read from RimWorld 1.6's real decompiled
    // IncidentWorker_Raid.cs / IncidentWorker_RaidEnemy.cs — see project memory for the verified
    // method bodies this patch is deliberately kept in lockstep with.
    [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "TryExecuteWorker")]
    public static class Patch_RaidNarrativeIntercept
    {
        private static int _nextRequestId = 1;

        static bool Prefix(IncidentWorker_RaidEnemy __instance, IncidentParms parms, ref bool __result)
        {
            if (!ShouldIntercept(__instance, parms)) return true;

            PendingRaidNarrative pending = null;
            PhaseOneOutcome outcome;
            try
            {
                outcome = TryRunPhaseOne(__instance, parms, out pending);
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly:RaidNarrative] Phase one threw: {e.Message}");
                // pending is only non-null once pawns have actually been generated — a real,
                // hard-to-undo side effect. If we never got that far, nothing happened yet and
                // it's safe to let vanilla run the whole thing itself. If we DID get that far,
                // vanilla must never also run — that would double-generate the raid.
                outcome = pending == null ? PhaseOneOutcome.NotEligible : PhaseOneOutcome.Deferred;
            }

            switch (outcome)
            {
                case PhaseOneOutcome.NotEligible:
                    // Nothing was mutated in a way vanilla can't redo identically — let the
                    // original method run exactly as if this patch didn't exist.
                    return true;

                case PhaseOneOutcome.GenuineFailure:
                    // We already ran vanilla's own resolution logic once and it failed the same
                    // way vanilla's would have (no faction, no arrival mode, no pawns available).
                    // Report that failure instead of letting vanilla run it all a second time.
                    __result = false;
                    return false;

                default: // Deferred
                    var tracker = RaidNarrativeTracker.For(Verse.Current.Game);
                    tracker?.AddPending(pending);
                    try
                    {
                        RequestNarrative(pending);
                    }
                    catch (Exception e)
                    {
                        // Pawns already exist. From here on the only safe move is to commit with
                        // vanilla fallback text ourselves — never return true, vanilla must not
                        // run TryExecuteWorker a second time for pawns we've already generated.
                        Log.Warning($"[Firefly:RaidNarrative] Request failed to start, committing vanilla fallback: {e.Message}");
                        Complete(pending.RequestId, null);
                    }

                    // Nothing has spawned yet either way. Report success so the outer wrappers
                    // run their normal bookkeeping now, exactly as if this raid had already
                    // fully fired — the actual spawn/letter/Lord happens in Complete().
                    __result = true;
                    return false;
            }
        }

        private enum PhaseOneOutcome { NotEligible, GenuineFailure, Deferred }

        // ------------------------------------------------------------------
        // Eligibility
        // ------------------------------------------------------------------

        private static bool ShouldIntercept(IncidentWorker_RaidEnemy worker, IncidentParms parms)
        {
            if (!RaidMethodFingerprint.IsSafe) return false;
            if (worker.GetType() != typeof(IncidentWorker_RaidEnemy)) return false;
            if (!(parms.target is Map map)) return false;
            if (map.mapPawns.FreeColonistsSpawnedCount == 0) return false;

            var gameComp = Verse.Current.Game?.GetComponent<FireflyGameComponent>();
            if (gameComp == null || !gameComp.FireflyEnabled) return false;
            if (FireflyMod.Settings?.ApiKey.NullOrEmpty() != false) return false;

            // A supplied pawnKind lets the strategy's SpawnThreats generate+arrive pawns itself,
            // bypassing the exact seam we depend on. Only the ordinary path is safe.
            if (parms.pawnKind != null) return false;

            return true;
        }

        // A strategy worker is safe only if it inherits both threat-generation methods unchanged
        // from the abstract base — any override means SpawnThreats/TryGenerateThreats could spawn
        // something before we ever reach our pause point.
        private static bool StrategyWorkerIsPlainBase(RaidStrategyWorker worker)
        {
            var type = worker.GetType();
            var spawnThreats = AccessTools.Method(type, "SpawnThreats");
            var tryGenerateThreats = AccessTools.Method(type, "TryGenerateThreats");
            return spawnThreats?.DeclaringType == typeof(RaidStrategyWorker)
                && tryGenerateThreats?.DeclaringType == typeof(RaidStrategyWorker);
        }

        private static readonly HashSet<Type> SafeArrivalWorkerTypes = new HashSet<Type>
        {
            typeof(PawnsArrivalModeWorker_EdgeWalkIn),
            typeof(PawnsArrivalModeWorker_EdgeWalkInGroups),
            typeof(PawnsArrivalModeWorker_EdgeDrop),
            typeof(PawnsArrivalModeWorker_CenterDrop),
            typeof(PawnsArrivalModeWorker_RandomDrop),
        };

        // ------------------------------------------------------------------
        // Phase 1 — replicates IncidentWorker_Raid.TryGenerateRaidInfo up through pawn
        // generation, calling the real vanilla resolver/generator methods throughout so any
        // other mod's Harmony patches on those methods still fire normally. Stops before
        // parms.raidArrivalMode.Worker.Arrive(pawns, parms) — nothing spawns here.
        // ------------------------------------------------------------------

        private static PhaseOneOutcome TryRunPhaseOne(IncidentWorker_RaidEnemy worker, IncidentParms parms, out PendingRaidNarrative pending)
        {
            pending = null;

            // Everything up to and including GeneratePawns below is cheap parms-resolution with
            // no hard-to-undo side effects — a failure or ineligibility here is safe to hand
            // straight back to vanilla to redo identically. Once GeneratePawns succeeds, real
            // Pawn objects exist and this patch owns the rest of the outcome unconditionally.

            CallResolveRaidPoints(worker, parms);
            if (!CallTryResolveRaidFaction(worker, parms)) return PhaseOneOutcome.GenuineFailure;

            PawnGroupKindDef groupKind = parms.pawnGroupKind ?? PawnGroupKindDefOf.Combat;
            worker.ResolveRaidStrategy(parms, groupKind);
            if (parms.raidStrategy == null || !StrategyWorkerIsPlainBase(parms.raidStrategy.Worker))
                return PhaseOneOutcome.NotEligible;

            if (parms.raidArrivalMode == null && !worker.TryResolveRaidArriveMode(parms))
                return PhaseOneOutcome.GenuineFailure;
            // Check the arrival worker's safety BEFORE calling anything on it — an unrecognized
            // (custom/modded) worker's own methods could have side effects we can't predict.
            if (!SafeArrivalWorkerTypes.Contains(parms.raidArrivalMode.Worker.GetType()))
                return PhaseOneOutcome.NotEligible;

            worker.ResolveRaidAgeRestriction(parms);

            // Real vanilla no-op for every strategy we've allowlisted above (verified: base
            // TryGenerateThreats does nothing), called anyway to stay faithful to the real order
            // and let any mod patch on it still fire.
            parms.raidStrategy.Worker.TryGenerateThreats(parms);

            if (!parms.raidArrivalMode.Worker.TryResolveRaidSpawnCenter(parms))
                return PhaseOneOutcome.GenuineFailure;
            // CenterDrop can rewrite raidArrivalMode to EdgeDrop if it can't find a valid center
            // — re-check the worker actually resolved now, not the one checked before this call.
            if (!SafeArrivalWorkerTypes.Contains(parms.raidArrivalMode.Worker.GetType()))
                return PhaseOneOutcome.NotEligible;

            float originalPoints = parms.points;
            parms.points = IncidentWorker_Raid.AdjustedRaidPoints(
                parms.points, parms.raidArrivalMode, parms.raidStrategy, parms.faction, groupKind, parms.target, parms.raidAgeRestriction);

            // pawnKind == null was already required in ShouldIntercept, so the real
            // SpawnThreats(parms) call here is guaranteed to return null for every strategy this
            // patch allowlists — skip calling it at all rather than risk a future allowlist
            // addition invoking it for a side effect we didn't anticipate.
            var groupMakerParms = IncidentParmsUtility.GetDefaultPawnGroupMakerParms(groupKind, parms);
            List<Pawn> pawns = PawnGroupMakerUtility.GeneratePawns(groupMakerParms).ToList();
            if (pawns.Count == 0) return PhaseOneOutcome.GenuineFailure;

            // Past this point real Pawn objects exist — committed. Any further failure (baseline
            // text generation throwing) must still produce a pending record so Complete() can
            // fall back to vanilla text rather than letting vanilla fire the raid a second time.
            pending = new PendingRaidNarrative
            {
                Worker = worker,
                Parms = parms,
                Pawns = pawns,
                OriginalPoints = originalPoints,
                OwningGame = Verse.Current.Game,
                RequestId = _nextRequestId++,
                CreatedTick = Find.TickManager.TicksGame,
            };

            try
            {
                pending.BaselineLabel = CallGetLetterLabel(worker, parms);
                pending.BaselineText = ApplySingularRaiderFix(parms, pawns, CallGetLetterText(worker, parms, pawns));
            }
            catch (Exception e)
            {
                // Baseline is only used for the LLM prompt — Complete() recomputes the real
                // letter fresh regardless. Missing baseline just means no narrative request gets
                // sent; the raid still fires normally with vanilla text.
                Log.Warning($"[Firefly:RaidNarrative] Baseline letter text failed, skipping narrative: {e.Message}");
                pending.BaselineText = null;
            }

            return PhaseOneOutcome.Deferred;
        }

        // ------------------------------------------------------------------
        // Narrative request
        // ------------------------------------------------------------------

        private const string RaidNarrativeLabel = "RaidNarrative";

        private static void RequestNarrative(PendingRaidNarrative pending)
        {
            if (pending.BaselineText.NullOrEmpty())
            {
                // Baseline generation failed earlier — nothing to send Fillion, commit with
                // vanilla text immediately rather than firing a request with no real content.
                Complete(pending.RequestId, null);
                return;
            }

            string prompt = BuildPrompt(pending);
            Log.Message($"[Firefly:{RaidNarrativeLabel}] Sending request {pending.RequestId} ({pending.BaselineLabel})...");

            LLMClient.Send(
                RaidNarrativeLabel,
                SystemPrompt,
                prompt,
                onSuccess: text =>
                {
                    Log.Message($"[Firefly:{RaidNarrativeLabel}] Responded for request {pending.RequestId}: {text}");
                    Complete(pending.RequestId, text);
                },
                onError: err =>
                {
                    Log.Warning($"[Firefly:{RaidNarrativeLabel}] Request {pending.RequestId} failed, falling back to vanilla text: {err}");
                    Complete(pending.RequestId, null);
                });
        }

        // Fillion's reply becomes the entire letter body directly — same pattern as every other
        // LLM call in this codebase (daily summary, thread scans, etc.): take the response, use
        // it as-is, no template/placeholder splicing.
        private static readonly string SystemPrompt =
            "You are Fillion, narrator of one event that is happening to a colony on a distant " +
            "earth-like rimworld. You will receive the details of the event, and story threads " +
            "the colony is currently dealing with. Your job is to write a short, focused piece, " +
            "detailing what is happening, how it relates to the colony's story so far, and how " +
            "it could tie into the colony's future.\n\n" +
            "Write the piece with an opening that draws the reader in and relates to the " +
            "colony's story if applicable. Keep your writing focused, but also curious. " +
            "Occasionally pose questions about events that have vague circumstances, potential " +
            "consequences, or could lead to greater threads throughout the world. You are " +
            "invested in the future of this story, along with how it's played out so far.\n\n" +
            "Also make sure to vary openings/endings and only ask rhetorical questions when " +
            "there's genuine uncertainty. Do not invent new threads. Preserve every concrete " +
            "fact from the event details (faction name, numbers, strategy). You're restyling " +
            "it, not replacing its content.\n\n" +
            "Try to make connections to the ongoing story threads the colony is dealing with. " +
            "Also generally end the piece with something that persists the mood of the " +
            "event/colony, or a different type of ending if you feel the event calls for it.\n\n" +
            "Here are some example pieces:\n\n" +
            "Raid\n" +
            "\"The world has taken notice of you.\n\n" +
            "Blight Wasters have found your colony, and their intentions are not friendly. " +
            "Strangely enough, however, there appears to be only a single raider behind the " +
            "attack.\n\n" +
            "Perhaps a scout, or a warrior with something to prove?\n\n" +
            "But how did they find your colony? Do the Wasters know about you yet?\n\n" +
            "Perhaps they have a connection to the disaster which wiped out your home-town.\n\n" +
            "Either way, you only have at most a few hours to prepare before they strike. Act " +
            "fast.\"\n\n" +
            "Ship chunks crashing nearby\n" +
            "\"Something's coming down. You hear it first - a low tearing sound overhead - " +
            "before glimpses of things big and heavy start to flash.\n\n" +
            "Chunks of a starship. Probably salvageable. But loud - the type of thing that " +
            "attracts scavengers.\n\n" +
            "This also poses the question - where did these pieces come from? A ferocious " +
            "battle above between two space-faring factions, or stray hunks of metal that have " +
            "been meaning to crash down for some time?\n\n" +
            "The origins are a mystery. Claim them quickly, and keep an eye out for any trouble " +
            "this might attract...\"\n\n" +
            "Ambroisa sprouts\n" +
            "\"A sweet odor has begun to permeate near the colony. It's... strange. Almost " +
            "addictive.\n\n" +
            "Ambrosia. Sweet enough that people have started wars over less, and worth good " +
            "silver to the right buyer.\n\n" +
            "But... why has it sprouted so suddenly? Could the planet's ecosystem hold " +
            "mysteries unknown, or is there something more devious afoot here...\n\n" +
            "This stuff takes a hold on whoever eats it. If your enemies - or allies - get word " +
            "about this, you might start seeing some unexpected traffic.\n\n" +
            "Pick it, sell it, or leave it be. It won't last forever.\"";

        private static string BuildPrompt(PendingRaidNarrative pending)
        {
            var sb = new StringBuilder();
            var threads = ColonyLedger.Current?.StoryThreads;
            if (threads != null && threads.Count > 0)
            {
                sb.AppendLine("=== ACTIVE STORY THREADS ===");
                foreach (var t in threads)
                {
                    sb.AppendLine($"--- {t.Name} ---");
                    sb.AppendLine(t.Description);

                    // Same shape as JournalRecorder's own thread-summary prompt: permanent
                    // condensed history first, then whatever facts haven't been folded into a
                    // chunk yet — full context, not just the one-line description.
                    if (t.Chunks.Count > 0)
                    {
                        foreach (var chunk in t.Chunks)
                            sb.AppendLine($"[Day {chunk.StartDay}-{chunk.EndDay}] {chunk.Summary}");
                    }
                    for (int i = t.ChunkedThroughFactIndex; i < t.Facts.Count; i++)
                    {
                        var fact = t.Facts[i];
                        if (fact == null) continue;
                        sb.AppendLine($"[Day {fact.Day}] {fact.Text}");
                    }
                    sb.AppendLine();
                }
            }
            sb.AppendLine("=== EVENT DETAILS ===");
            sb.AppendLine($"Label: {pending.BaselineLabel}");
            sb.AppendLine(pending.BaselineText);
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Phase 2 — commit. Runs on the main thread, either from Fillion's callback (via
        // MainThreadQueue, same as every other LLMClient call in this codebase) or from the
        // deadline check in RaidNarrativeTracker.Tick.
        // ------------------------------------------------------------------

        public static void Complete(int requestId, string narrativeTextOrNull)
        {
            var tracker = RaidNarrativeTracker.For(Verse.Current.Game);
            var pending = tracker?.Claim(requestId);
            if (pending == null) return; // already committed (deadline beat the callback), or game changed under us

            if (!ReferenceEquals(Verse.Current.Game, pending.OwningGame))
            {
                // Save switched mid-request. The pawns were never spawned, never entered any
                // save, and belong to no map — just let them go.
                return;
            }

            IncidentParms parms = pending.Parms;
            List<Pawn> pawns = pending.Pawns;

            if (!(parms.target is Map map) || !Find.Maps.Contains(map))
            {
                DiscardPawns(pawns);
                return;
            }
            if (parms.faction == null || parms.faction.HostileTo(Faction.OfPlayer) == false)
            {
                DiscardPawns(pawns);
                return;
            }

            if (!narrativeTextOrNull.NullOrEmpty())
                parms.customLetterText = narrativeTextOrNull.Trim();

            try
            {
                parms.raidArrivalMode.Worker.Arrive(pawns, parms);
                parms.pawnCount = pawns.Count;
                CallPostProcessSpawnedPawns(pending.Worker, parms, pawns);
                CallGenerateRaidLoot(pending.Worker, parms, pending.OriginalPoints, pawns);

                TaggedString letterLabel = CallGetLetterLabel(pending.Worker, parms);
                TaggedString letterText = ApplySingularRaiderFix(parms, pawns, CallGetLetterText(pending.Worker, parms, pawns));
                PawnRelationUtility.Notify_PawnsSeenByPlayer_Letter(
                    pawns, ref letterLabel, ref letterText,
                    CallGetRelatedPawnsInfoLetterText(pending.Worker, parms),
                    informEvenIfSeenBefore: true);

                List<TargetInfo> lookTargets = BuildLookTargets(parms, pawns);
                LetterDef letterDef = CallGetLetterDef(pending.Worker);
                CallSendStandardLetter(pending.Worker, letterLabel, letterText, letterDef, parms, lookTargets);

                if (parms.controllerPawn == null || parms.controllerPawn.Faction != Faction.OfPlayer)
                    parms.raidStrategy.Worker.MakeLords(parms, pawns);

                LessonAutoActivator.TeachOpportunity(ConceptDefOf.EquippingWeapons, OpportunityType.Critical);
                if (!PlayerKnowledgeDatabase.IsComplete(ConceptDefOf.ShieldBelts))
                {
                    foreach (var pawn in pawns)
                    {
                        if (pawn.apparel != null && pawn.apparel.WornApparel.Any(ap => ap.def == ThingDefOf.Apparel_ShieldBelt))
                        {
                            LessonAutoActivator.TeachOpportunity(ConceptDefOf.ShieldBelts, OpportunityType.Critical);
                            break;
                        }
                    }
                }

                // IncidentWorker_RaidEnemy's own tail (verified body: base call, speed signal,
                // stats, last-raid-faction) — reproduced here since the base call already ran.
                if (!parms.silent) Find.TickManager.slower.SignalForceNormalSpeedShort();
                Find.StoryWatcher.statsRecord.numRaidsEnemy++;
                parms.target.StoryState.lastRaidFaction = parms.faction;
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly:RaidNarrative] Deferred commit failed, discarding held pawns: {e.Message}");
                DiscardPawns(pawns);
            }
        }

        private static List<TargetInfo> BuildLookTargets(IncidentParms parms, List<Pawn> pawns)
        {
            var list = new List<TargetInfo>();
            if (parms.pawnGroups != null)
            {
                var groups = IncidentParmsUtility.SplitIntoGroups(pawns, parms.pawnGroups);
                var biggest = groups.MaxBy(g => g.Count);
                if (biggest.Any()) list.Add(biggest[0]);
                foreach (var g in groups)
                    if (g != biggest && g.Any()) list.Add(g[0]);
            }
            else
            {
                foreach (var p in pawns) list.Add(p);
            }
            return list;
        }

        // Reverse patches copy the real method's original IL only — they do NOT run other
        // Harmony patches on that method, including Firefly's own Patch_RaidLetterText postfix
        // (fixes vanilla always using the plural raider noun for a single-pawn raid). Reapply the
        // same fix manually everywhere this file calls the reverse-patched GetLetterText, so a
        // single-pawn raid's text stays correct whether or not Fillion's rewrite is in play.
        private static string ApplySingularRaiderFix(IncidentParms parms, List<Pawn> pawns, string text)
        {
            try
            {
                if (pawns?.Count != 1 || text.NullOrEmpty()) return text;
                Pawn pawn = pawns[0];
                if (pawn == null) return text;
                string raider = ColonyLedger.StripTags(pawn.kindDef?.label ?? "");
                string faction = ColonyLedger.StripTags(parms?.faction?.Name ?? pawn.Faction?.Name ?? "");
                if (raider.NullOrEmpty() || faction.NullOrEmpty()) return text;
                int sentenceEnd = text.IndexOf('.');
                if (sentenceEnd < 0) return text;
                string opening = $"A single {raider} from {faction} has arrived nearby.";
                return opening + text.Substring(sentenceEnd + 1);
            }
            catch { return text; }
        }

        private static void DiscardPawns(List<Pawn> pawns)
        {
            if (pawns == null) return;
            foreach (var p in pawns)
            {
                try { if (p != null && !p.Destroyed) p.Destroy(DestroyMode.Vanish); }
                catch { }
            }
        }

        // ------------------------------------------------------------------
        // Reverse patches — every method below is protected/abstract on IncidentWorker_Raid or
        // IncidentWorker, implemented on IncidentWorker_RaidEnemy, and inaccessible directly from
        // this separate assembly. Each stub's body is replaced by Harmony at patch time with the
        // real method's own IL, so calling it runs the actual vanilla code (and any other mod's
        // Harmony patches on it) exactly as if TryExecuteWorker had called it itself.
        // ------------------------------------------------------------------

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "ResolveRaidPoints")]
        private static void CallResolveRaidPoints(object instance, IncidentParms parms) =>
            throw new NotImplementedException("Reverse patch stub");

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "TryResolveRaidFaction")]
        private static bool CallTryResolveRaidFaction(object instance, IncidentParms parms) =>
            throw new NotImplementedException("Reverse patch stub");

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "GetLetterLabel")]
        private static string CallGetLetterLabel(object instance, IncidentParms parms) =>
            throw new NotImplementedException("Reverse patch stub");

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "GetLetterText")]
        private static string CallGetLetterText(object instance, IncidentParms parms, List<Pawn> pawns) =>
            throw new NotImplementedException("Reverse patch stub");

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "GetRelatedPawnsInfoLetterText")]
        private static string CallGetRelatedPawnsInfoLetterText(object instance, IncidentParms parms) =>
            throw new NotImplementedException("Reverse patch stub");

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "GetLetterDef")]
        private static LetterDef CallGetLetterDef(object instance) =>
            throw new NotImplementedException("Reverse patch stub");

        // RaidEnemy does not override this (only IncidentWorker_ShamblerAssault does) — target
        // the real declaring type directly rather than relying on inherited-method lookup.
        [HarmonyReversePatch]
        [HarmonyPatch(typeof(IncidentWorker_Raid), "PostProcessSpawnedPawns")]
        private static void CallPostProcessSpawnedPawns(object instance, IncidentParms parms, List<Pawn> pawns) =>
            throw new NotImplementedException("Reverse patch stub");

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(IncidentWorker_RaidEnemy), "GenerateRaidLoot")]
        private static void CallGenerateRaidLoot(object instance, IncidentParms parms, float points, List<Pawn> pawns) =>
            throw new NotImplementedException("Reverse patch stub");

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(IncidentWorker), "SendStandardLetter",
            new[] { typeof(TaggedString), typeof(TaggedString), typeof(LetterDef), typeof(IncidentParms), typeof(LookTargets), typeof(NamedArgument[]) })]
        private static void CallSendStandardLetter(object instance, TaggedString baseLabel, TaggedString baseText, LetterDef baseDef, IncidentParms parms, LookTargets lookTargets, params NamedArgument[] textArgs) =>
            throw new NotImplementedException("Reverse patch stub");
    }
}
