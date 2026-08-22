using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    // Universal Event Decider hold point. Unlike Patch_RaidNarrativeIntercept (which targets one
    // specific incident worker and replicates its internal decide/execute split up through pawn
    // generation), this patches Storyteller.TryFire itself — the single seam every storyteller
    // path funnels through regardless of which component or incident type made the pick.
    // TryFire's real body (verified against RimWorld 1.6.4871 rev598's decompiled source) is just:
    //
    //   if (fi.def.Worker.CanFireNow(fi.parms) && fi.def.Worker.TryExecute(fi.parms))
    //   {
    //       fi.parms.target.StoryState.Notify_IncidentFired(fi);
    //       lastIncidentTick = GenTicks.TicksGame;
    //       return true;
    //   }
    //   return false;
    //
    // Nothing incident-specific has run yet at the point this prefix executes, for ANY incident,
    // vanilla or modded — that's what makes one hook here sufficient instead of needing the kind
    // of per-family restructuring raids originally seemed to require (see project design notes,
    // 2026-08-21 session).
    //
    // Scope: this only ever HOLDS incidents whose IncidentDef has a registered IIncidentAdapter —
    // everything else (any incident type we haven't built and tested an adapter for, modded or
    // vanilla) returns true immediately and runs completely normally, at effectively zero cost.
    // Firing the ORIGINAL incident untouched is always safe regardless of adapter coverage,
    // because nothing about that incident's own code is ever touched or second-guessed — only a
    // held incident that an LLM decides to steer gets its parms mutated, and only through that
    // incident's own hand-verified adapter's Validate/Apply.
    [HarmonyPatch(typeof(Storyteller), nameof(Storyteller.TryFire))]
    public static class Patch_EventDeciderIntercept
    {
        private static int _nextRequestId = 1;

        // Populated by each adapter at mod init (e.g. FireflyMod's constructor) — Codex's
        // DiseaseAdapter needs to call Register(new DiseaseAdapter()) somewhere during startup.
        // Keyed by IncidentDef rather than IncidentWorker's C# Type — see IIncidentAdapter's own
        // doc comment for why.
        private static readonly Dictionary<IncidentDef, IIncidentAdapter> _adapters =
            new Dictionary<IncidentDef, IIncidentAdapter>();

        public static void Register(IIncidentAdapter adapter)
        {
            if (adapter?.CoveredDefs == null) return;
            foreach (var def in adapter.CoveredDefs)
                if (def != null) _adapters[def] = adapter;
        }

        static bool Prefix(FiringIncident fi, bool queued, ref bool __result)
        {
            // A false return has special retry meaning to IncidentQueue. Do not remove a queued
            // incident before its eventual deferred CanFireNow result is known.
            if (queued || !ShouldConsider(fi, out var adapter)) return true;

            var pending = new PendingEventDecision
            {
                Fi = fi,
                Adapter = adapter,
                OwningGame = Verse.Current.Game,
                RequestId = _nextRequestId++,
                CreatedTick = Find.TickManager.TicksGame,
            };

            var tracker = EventDecisionTracker.For(Verse.Current.Game);
            tracker?.AddPending(pending);

            try
            {
                // EventDeciderOrchestrator deliberately takes a plain-data EventDecisionRequest
                // rather than the live PendingEventDecision — it runs LLM calls on a background
                // thread and must never touch Verse/RimWorld objects directly. Building that
                // snapshot here (main thread, before the request starts) is this file's job;
                // interpreting it and running the two-call LLM conversation is the orchestrator's.
                var request = BuildDecisionRequest(pending);
                EventDeciderOrchestrator.RequestDecision(request,
                    result => Complete(pending.RequestId, result));
            }
            catch (Exception e)
            {
                // Nothing has fired yet — safe to just resume this incident untouched rather than
                // leave it stuck pending forever with no request in flight to ever resolve it.
                Log.Warning($"[Firefly:EventDecider] Request failed to start, resuming untouched: {e.Message}");
                Complete(pending.RequestId, null);
            }

            // Nothing has fired yet either way, but report success so any code checking TryFire's
            // return value sees the same outcome it would have — the actual CanFireNow/TryExecute
            // call happens later, inside Complete().
            __result = true;
            return false;
        }

        // ------------------------------------------------------------------
        // Eligibility — the cheap local prefilter. Must stay fast: this runs on every single
        // incident TryFire call in the game, the overwhelming majority of which will never be
        // held. No LLM call happens here, only local lookups.
        // ------------------------------------------------------------------

        private static bool ShouldConsider(FiringIncident fi, out IIncidentAdapter adapter)
        {
            adapter = null;
            if (!EventDeciderMethodFingerprint.IsSafe) return false;
            if (fi.def == null || fi.parms == null) return false;
            if (!_adapters.TryGetValue(fi.def, out adapter)) return false;

            var gameComp = Verse.Current.Game?.GetComponent<FireflyGameComponent>();
            if (gameComp == null || !gameComp.FireflyEnabled) return false;
            if (FireflyMod.Settings?.ApiKey.NullOrEmpty() != false) return false;

            // No point holding an incident if there's no active narrative state to reason about
            // at all yet (e.g. day-zero games with no Story Threads recorded). This does NOT
            // filter based on whether this specific incident looks relevant — that judgment call
            // (including the deliberate "not relevant, but let it happen anyway for texture"
            // branch) belongs to LLM #1, not to this cheap local filter.
            var threads = ColonyLedger.Current?.StoryThreads;
            var worldThreads = FireflyWorldComponent.Current?.WorldThreads;
            if ((threads == null || threads.Count == 0) &&
                (worldThreads == null || worldThreads.Count == 0)) return false;

            return true;
        }

        // Snapshots everything the LLM conversation needs into plain strings, main-thread-only,
        // before handing off to EventDeciderOrchestrator's background request work. Keep this
        // cheap-ish (it only runs for incidents that already passed ShouldConsider, not every
        // fire) but don't reach for anything expensive — Story Thread summaries and faction
        // taglines are already the compact forms this codebase builds for cost reasons elsewhere
        // (see JournalSummaryService/FactionSnapshot.Tagline), reused here rather than pulling
        // full detail.
        private static EventDecisionRequest BuildDecisionRequest(PendingEventDecision pending)
        {
            var fi = pending.Fi;
            var request = new EventDecisionRequest
            {
                IncidentDefName = fi.def?.defName ?? "",
                IncidentLabel = fi.def?.label ?? "",
                BaseParameters = $"Target: {fi.parms?.target}\nThreat points: {fi.parms?.points}",
                RecentEvents = ColonyLedger.Current?.GetCurrentDayContent() ?? "",
            };

            var threads = ColonyLedger.Current?.StoryThreads;
            if (threads != null)
            {
                foreach (var t in threads)
                {
                    if (t == null) continue;
                    request.ActiveThreads.Add(new EventThreadContext
                    {
                        Id = "story:" + t.Id,
                        Name = t.Name,
                        Summary = t.ActiveSummary,
                    });
                }
            }

            var worldThreads = FireflyWorldComponent.Current?.WorldThreads;
            if (worldThreads != null)
            {
                foreach (var t in worldThreads)
                {
                    if (t == null) continue;
                    request.ActiveThreads.Add(new EventThreadContext
                    {
                        Id = "world:" + t.Id,
                        Name = t.Title,
                        Summary = t.ActiveSummary,
                    });
                }
            }

            var factions = FireflyWorldComponent.Current?.FactionSnapshots;
            if (factions != null && factions.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var f in factions)
                {
                    if (f == null || f.Tagline.NullOrEmpty()) continue;
                    sb.AppendLine($"{f.FactionName}: {f.Tagline}");
                }
                request.FactionContext = sb.ToString();
            }

            if (pending.Adapter != null)
            {
                var descriptions = pending.Adapter.DescribeAllowedFields(fi.def.Worker, fi.parms);
                if (descriptions != null)
                    foreach (var field in descriptions)
                        request.AllowedFields[field.Key] = field.Value;
            }

            return request;
        }

        // ------------------------------------------------------------------
        // Commit. Runs on the main thread, either from the Event Decider's LLM callbacks (via
        // MainThreadQueue, same as every other LLMClient call in this codebase) or from
        // EventDecisionTracker.CompleteAllPending on save.
        // ------------------------------------------------------------------

        // resultOrNull == null, or a result with HasIntervention == false and no letter text,
        // means: resume the original incident completely untouched, exactly as if this patch had
        // returned true from Prefix in the first place.
        public static void Complete(int requestId, EventDecisionResult resultOrNull)
        {
            var tracker = EventDecisionTracker.For(Verse.Current.Game);
            var pending = tracker?.Claim(requestId);
            if (pending == null) return; // already committed (save beat the callback), or unknown id

            if (!ReferenceEquals(Verse.Current.Game, pending.OwningGame))
            {
                // Save switched mid-request. Nothing was ever fired or committed for this
                // incident — it just silently never happens. Nothing to clean up, since
                // CanFireNow/TryExecute never ran for it.
                return;
            }

            FiringIncident fi = pending.Fi;
            IncidentParms parms = fi.parms;

            if (resultOrNull != null && resultOrNull.HasIntervention && pending.Adapter != null)
                ApplyValidatedNudges(pending.Adapter, fi.def.Worker, parms, resultOrNull, requestId);

            if (resultOrNull != null && !resultOrNull.CustomLetterText.NullOrEmpty())
                parms.customLetterText = resultOrNull.CustomLetterText.Trim();

            try
            {
                if (fi.def.Worker.CanFireNow(parms) && fi.def.Worker.TryExecute(parms))
                {
                    // Reproduces Storyteller.TryFire's own tail exactly — this runs instead of,
                    // not in addition to, the original method, since Prefix returned false and
                    // skipped it entirely.
                    parms.target.StoryState.Notify_IncidentFired(fi);
                    SetLastIncidentTick(Find.Storyteller, Find.TickManager.TicksGame);
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly:EventDecider] Deferred fire failed for request {requestId}: {e.Message}");
            }
        }

        private static void ApplyValidatedNudges(IIncidentAdapter adapter, IncidentWorker worker,
            IncidentParms parms, EventDecisionResult result, int requestId)
        {
            if (result.ProposedValues == null || result.ProposedValues.Count == 0) return;

            var validated = new Dictionary<string, string>();
            foreach (var kvp in result.ProposedValues)
            {
                try
                {
                    if (adapter.Validate(worker, parms, kvp.Key, kvp.Value))
                        validated[kvp.Key] = kvp.Value;
                    else
                        Log.Warning($"[Firefly:EventDecider] Proposed value for '{kvp.Key}' failed adapter validation, skipping it (request {requestId}).");
                }
                catch (Exception e)
                {
                    Log.Warning($"[Firefly:EventDecider] Validating '{kvp.Key}' threw, skipping it (request {requestId}): {e.Message}");
                }
            }
            if (validated.Count == 0) return;

            try { adapter.Apply(worker, parms, validated); }
            catch (Exception e)
            {
                Log.Warning($"[Firefly:EventDecider] Adapter Apply threw, resuming with un-nudged parms (request {requestId}): {e.Message}");
            }
        }

        // Storyteller.lastIncidentTick is private with no setter — Traverse is the same
        // established technique this codebase already uses (see StrategyWorkerIsPlainBase's use
        // of AccessTools) for reaching a private vanilla member from outside its assembly.
        private static void SetLastIncidentTick(Storyteller storyteller, int tick)
        {
            if (storyteller == null) return;
            try { Traverse.Create(storyteller).Field("lastIncidentTick").SetValue(tick); }
            catch (Exception e)
            {
                // Non-fatal — only affects the storyteller's own incident-pacing cooldown math,
                // not whether this incident actually fired. Log and move on rather than fail the
                // whole commit over a bookkeeping field.
                Log.Warning($"[Firefly:EventDecider] Could not set lastIncidentTick: {e.Message}");
            }
        }
    }
}
