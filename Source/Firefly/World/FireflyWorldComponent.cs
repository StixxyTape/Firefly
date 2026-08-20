using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace Firefly
{
    // World-scoped counterpart to FireflyGameComponent — owns simulation that happens outside the
    // player's colony: per-faction "ironclad facts" snapshots and the World Threads they seed and
    // progress. A WorldComponent (not a GameComponent) deliberately: this is conceptually tied to
    // the world/save, not to any one colony, and RimWorld auto-discovers and constructs it via
    // World.FillComponents (Activator.CreateInstance(type, world) over every
    // RimWorld.Planet.WorldComponent subclass) — no manual registration needed, same as
    // FireflyGameComponent needs none for GameComponent.
    //
    // Purely narrative for now, per Josh's explicit v1 scope: nothing here ever mutates real
    // faction relations, settlements, or any other live game state. The LLM only ever reads
    // FactionSnapshots; it can never write back into them.
    public class FireflyWorldComponent : WorldComponent
    {
        private static World _cachedWorld;
        private static FireflyWorldComponent _cachedComponent;

        public static FireflyWorldComponent Current
        {
            get
            {
                World world = Find.World;
                if (world == null)
                {
                    _cachedWorld = null;
                    _cachedComponent = null;
                    return null;
                }
                if (!ReferenceEquals(world, _cachedWorld))
                {
                    _cachedWorld = world;
                    _cachedComponent = world.GetComponent<FireflyWorldComponent>();
                }
                return _cachedComponent;
            }
        }

        private List<FactionSnapshot> _factionSnapshots = new List<FactionSnapshot>();
        private List<WorldThread> _worldThreads = new List<WorldThread>();
        private List<PendingWorldWork> _pendingWorldWork = new List<PendingWorldWork>();
        private List<DailyWorldRecord> _dailyWorldRecords = new List<DailyWorldRecord>();
        private string _worldHistory = "";
        private int _lastWorldArcDay;

        public IReadOnlyList<FactionSnapshot> FactionSnapshots => _factionSnapshots;
        public IReadOnlyList<WorldThread> WorldThreads => _worldThreads;
        public IReadOnlyList<DailyWorldRecord> DailyWorldRecords => _dailyWorldRecords;
        public string WorldHistory => _worldHistory;
        public int LastWorldArcDay => _lastWorldArcDay;

        // Whether the initial seed call has ever completed successfully (valid JSON applied) —
        // false keeps retrying bootstrap on every load (e.g. no API key was set yet at world-gen
        // time, the request failed outright, or it came back as unparseable JSON); only a
        // confirmed successful apply sets this true for good. Deliberately NOT set merely because
        // a request was sent — see RequestSeedThreads for why that was a real bug.
        private bool _seedRequested;

        // Last fully completed daily chain. Work is only advanced after the colony summary and
        // remains persisted at its current stage across failures and reloads.
        private int _lastProgressedDay = -1;

        private bool _factionsCaptured;

        private bool _requestInFlight;
        public static bool IsWorldThreadWorking => LLMClient.IsPendingForAny(WorldPendingLabels);

        public const string InitialWorldThreadSeederLabel = "InitialWorldThreadSeeder";
        public const string DailyWorldProgressionLabel = "DailyWorldProgression";
        public const string DailyWorldThreadScanLabel = "DailyWorldThreadScan";
        public const string DailyWorldThreadFactionScanLabel = "DailyWorldThreadFactionScan";
        public const string WorldThreadFactChunkerLabel = "WorldThreadFactChunker";
        public const string WorldThreadSummariserLabel = "WorldThreadSummariser";
        public const string MonthlyWorldArcHistoryLabel = "MonthlyWorldArcHistory";
        public const string FactionNarrativeChunkerLabel = "FactionNarrativeChunker";
        public const string FactionNarrativeSummariserLabel = "FactionNarrativeSummariser";
        public const string InitialFactionFactSeederLabel = "InitialFactionFactSeeder";
        public const string FactionIdentityChunkerLabel = "FactionIdentityChunker";
        public const string FactionIdentitySummariserLabel = "FactionIdentitySummariser";
        public const string FactionTaglineUpdaterLabel = "FactionTaglineUpdater";
        public static readonly string[] WorldPendingLabels =
            { InitialWorldThreadSeederLabel, DailyWorldProgressionLabel, DailyWorldThreadScanLabel, WorldThreadFactChunkerLabel, WorldThreadSummariserLabel,
                MonthlyWorldArcHistoryLabel };
        public static readonly string[] FactionPendingLabels =
            { InitialFactionFactSeederLabel, DailyWorldThreadFactionScanLabel, FactionNarrativeChunkerLabel, FactionNarrativeSummariserLabel,
                FactionIdentityChunkerLabel, FactionIdentitySummariserLabel, FactionTaglineUpdaterLabel };

        private readonly HashSet<string> _factionFactsInFlight = new HashSet<string>();
        private readonly HashSet<string> _factionTaglinesInFlight = new HashSet<string>();
        private readonly HashSet<string> _factionDescriptionsInFlight = new HashSet<string>();

        public static bool IsFactionFactsWorking(string key) =>
            !key.NullOrEmpty() && Current?._factionFactsInFlight.Contains(key) == true;
        public static bool IsFactionTaglineWorking(string key) =>
            !key.NullOrEmpty() && Current?._factionTaglinesInFlight.Contains(key) == true;
        public static bool IsFactionDescriptionWorking(string key) =>
            !key.NullOrEmpty() && Current?._factionDescriptionsInFlight.Contains(key) == true;

        public FireflyWorldComponent(World world) : base(world) { }

        // ── Lifecycle ───────────────────────────────────────────────────────

        public override void FinalizeInit(bool fromLoad)
        {
            try
            {
                CaptureFactionsIfNeeded();
                RequestMissingFactionFacts();
                RequestMissingFactionDescriptions();
                RequestMissingFactionTaglines();
                BootstrapIfNeeded();
                TryResumeWorldWork();
            }
            catch (Exception e) { Log.Warning($"[Firefly] World FinalizeInit failed: {e.Message}"); }
        }

        // Called by JournalRecorder.WriteTimeline right alongside the colony's own daily summary
        // and thread-scan requests — Josh wants faction snapshots, world-thread progression, and
        // the colony's own daily calls all firing together on one trigger, and to reuse the exact
        // same day number WriteTimeline already computed (a plain incrementing int — the same
        // approach JournalFact.Day uses, not tick arithmetic; see the day-numbering rework
        // this replaced for why tick math was the wrong tool here). No colony loaded at all means
        // this is never called, so neither the snapshot refresh nor progression happen without an
        // active colony — a deliberate tradeoff for staying in sync, not an oversight. Safe to
        // call more than once for the same colonyDay — both day-boundary checks below dedupe.
        public void QueueDailyWorldWork(int colonyDay, string colonySummary)
        {
            if (colonySummary.NullOrEmpty() || colonyDay <= _lastProgressedDay ||
                _pendingWorldWork.Any(w => w.Day == colonyDay && !w.WorldGeneration)) return;
            _pendingWorldWork.Add(new PendingWorldWork
            {
                Day = colonyDay,
                ColonySummary = colonySummary,
                Stage = WorldWorkStage.WorldOutcome,
            });
            TryResumeWorldWork();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref _factionSnapshots, "factionSnapshots", LookMode.Deep);
            Scribe_Collections.Look(ref _worldThreads, "worldThreads", LookMode.Deep);
            Scribe_Collections.Look(ref _pendingWorldWork, "pendingWorldWork", LookMode.Deep);
            Scribe_Collections.Look(ref _dailyWorldRecords, "dailyWorldRecords", LookMode.Deep);
            Scribe_Values.Look(ref _worldHistory, "worldHistory", "");
            Scribe_Values.Look(ref _lastWorldArcDay, "lastWorldArcDay", 0);
            Scribe_Values.Look(ref _seedRequested, "worldSeedRequested", false);
            Scribe_Values.Look(ref _lastProgressedDay, "worldLastProgressedDay", -1);
            Scribe_Values.Look(ref _factionsCaptured, "factionsCaptured", false);

            if (Scribe.mode == LoadSaveMode.LoadingVars && _factionSnapshots == null)
                _factionSnapshots = new List<FactionSnapshot>();
            if (Scribe.mode == LoadSaveMode.LoadingVars && _worldThreads == null)
                _worldThreads = new List<WorldThread>();
            if (Scribe.mode == LoadSaveMode.LoadingVars && _pendingWorldWork == null)
                _pendingWorldWork = new List<PendingWorldWork>();
            if (Scribe.mode == LoadSaveMode.LoadingVars && _dailyWorldRecords == null)
                _dailyWorldRecords = new List<DailyWorldRecord>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                _worldHistory = _worldHistory ?? "";
                _lastWorldArcDay = Math.Max(0, _lastWorldArcDay);
                _worldThreads.RemoveAll(t => t == null || t.Id.NullOrEmpty());
                _pendingWorldWork.RemoveAll(w => w == null);
                _dailyWorldRecords.RemoveAll(r => r == null || r.Day < 1 || r.Simulation.NullOrEmpty());
                _dailyWorldRecords = _dailyWorldRecords.GroupBy(r => r.Day)
                    .Select(g => g.Last()).OrderBy(r => r.Day).ToList();

                // Preserve an outcome already persisted by an older build while its remaining
                // daily stages were still pending, then establish a baseline for saves that
                // predate world-history records so their first new record is not blocked forever
                // by days that can no longer be reconstructed.
                foreach (var work in _pendingWorldWork.Where(w => !w.WorldGeneration &&
                             w.Day > 0 && !w.WorldOutcome.NullOrEmpty()))
                    SetDailyWorldSimulation(work.Day, work.WorldOutcome);
                if (_lastWorldArcDay == 0 && _worldHistory.NullOrEmpty())
                {
                    int firstRecordedDay = _dailyWorldRecords.Count > 0 ? _dailyWorldRecords[0].Day : 0;
                    if (firstRecordedDay > 1)
                        _lastWorldArcDay = firstRecordedDay - 1;
                    else if (firstRecordedDay == 0 && _lastProgressedDay > 0)
                        _lastWorldArcDay = _lastProgressedDay;
                }
                if (_factionSnapshots.Count > 0) _factionsCaptured = true;
            }
        }

        // Only the API key gates this — World Threads are explicitly independent of any one
        // colony's Firefly enabled/disabled toggle (Patch_StorytellerPage.FireflyEnabled is
        // per-Game and conceptually the wrong scope for something world-level).
        private static bool ShouldRun() => !FireflyMod.Settings.ApiKey.NullOrEmpty();

        // World generation has no colony (and so no colony day) to anchor to — bootstrap and the
        // seed call it may fire always use day 0, the only sensible choice before any colony exists.
        private const int WorldGenDay = 0;

        private void BootstrapIfNeeded()
        {
            if (_seedRequested || _requestInFlight) return;
            if (!ShouldRun()) return; // try again next load
            CaptureFactionsIfNeeded();
            if (_factionSnapshots.Count == 0) return; // nothing to seed from yet, try again next load
            if (_factionSnapshots.Any(f => f?.FactionJournal?.Facts == null || f.FactionJournal.Facts.Count == 0))
                return; // each parallel faction bootstrap re-checks this gate when it succeeds
            if (_factionSnapshots.Any(f => f == null || f.Description.NullOrEmpty()))
                return; // first Description uses the normal summary pipeline before World Seed
            if (_factionSnapshots.Any(f => f == null || f.TaglineStale))
                return; // World Seed must see a real identity-only tagline for every faction
            RequestSeedThreads();
        }

        // ── One-time faction snapshot capture ────────────────────────────────
        // Status is frozen here and never refreshed. Initial facts and Tagline are generated in
        // parallel afterward; Description then uses the same summary workflow as later updates.

        private void CaptureFactionsIfNeeded()
        {
            if (_factionsCaptured) return;

            var allFactions = Find.FactionManager?.AllFactionsListForReading?
                .Where(f => f != null && f.def != null && !f.temporary).ToList();
            var factions = allFactions?.Where(f => !f.IsPlayer).ToList();
            if (factions.NullOrEmpty()) return;

            var settlements = Find.WorldObjects?.Settlements ?? new List<Settlement>();
            var byFaction = settlements.Where(s => s?.Faction != null)
                .ToLookup(s => s.Faction.loadID);
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var captured = new List<FactionSnapshot>();

            foreach (var faction in factions)
            {
                string key = UniqueSlug.Create(faction.Name, "faction", keys);
                keys.Add(key);
                var status = new FactionStatusSnapshot
                {
                    TechLevel = faction.def.techLevel.ToString(),
                    Species = DeriveSpecies(faction),
                    LeaderName = faction.leader?.Name?.ToStringFull ?? "",
                    LeaderTitle = faction.LeaderTitle ?? "",
                    ReligionName = ModsConfig.IdeologyActive
                        ? faction.ideos?.PrimaryIdeo?.name ?? ""
                        : "",
                    // Excludes the four "origin/style" structure memes (Structure_OriginChristian/
                    // Islamic/Hindu/Buddhist — real-world religion flavor reskins) — Josh wants
                    // just the mechanically meaningful memes here, not the cosmetic origin flavor.
                    ReligionMemes = ModsConfig.IdeologyActive
                        ? faction.ideos?.PrimaryIdeo?.memes?
                            .Where(m => !m.defName.StartsWith("Structure_Origin"))
                            .Select(m => new FactionMemeSnapshot { Label = m.label, Description = m.description })
                            .ToList() ?? new List<FactionMemeSnapshot>()
                        : new List<FactionMemeSnapshot>(),
                };

                foreach (var settlement in byFaction[faction.loadID])
                {
                    var coordinates = Find.WorldGrid?.LongLatOf(settlement.Tile) ?? UnityEngine.Vector2.zero;
                    status.ActiveSettlements.Add(new FactionSettlementSnapshot
                    {
                        WorldObjectId = settlement.ID,
                        Name = settlement.LabelCap ?? settlement.Label ?? "Unnamed settlement",
                        Tile = settlement.Tile,
                        Location = $"tile {settlement.Tile} ({coordinates.y:0.0} latitude, {coordinates.x:0.0} longitude)",
                    });
                }

                foreach (var other in allFactions)
                {
                    if (other == faction) continue;
                    status.Relationships.Add(new FactionRelationSnapshot
                    {
                        OtherFactionLoadId = other.loadID,
                        OtherFactionName = other.Name ?? other.def.label ?? "Unknown faction",
                        Kind = faction.RelationKindWith(other).ToString(),
                        Goodwill = faction.GoodwillWith(other),
                    });
                }
                status.Relationships = status.Relationships.OrderByDescending(r => r.Goodwill).ToList();

                captured.Add(new FactionSnapshot
                {
                    FactionLoadId = faction.loadID,
                    Key = key,
                    FactionName = faction.Name ?? faction.def.label ?? "Unknown faction",
                    Status = status,
                    NarrativeJournal = new JournalRecord(),
                    FactionJournal = new JournalRecord(),
                });
            }

            _factionSnapshots = captured;
            _factionsCaptured = true;
        }

        // A leader's own race covers the vast majority of factions (vanilla humans and any
        // modded humanlike alien race) — most reliable single source when it exists. Leaderless
        // factions (mechanoids, insect hives, and similar) fall back to whichever race appears
        // most heavily weighted across the faction's own raid/pawn-group composition.
        private static string DeriveSpecies(Faction faction)
        {
            string leaderSpecies = faction.leader?.def?.LabelCap;
            if (!leaderSpecies.NullOrEmpty()) return leaderSpecies;

            var raceWeights = new Dictionary<ThingDef, float>();
            foreach (var maker in faction.def?.pawnGroupMakers ?? Enumerable.Empty<PawnGroupMaker>())
                foreach (var option in maker.options ?? Enumerable.Empty<PawnGenOption>())
                {
                    ThingDef race = option.kind?.race;
                    if (race == null) continue;
                    raceWeights.TryGetValue(race, out float weight);
                    raceWeights[race] = weight + option.selectionWeight;
                }
            return raceWeights.OrderByDescending(kv => kv.Value).FirstOrDefault().Key?.LabelCap ?? "";
        }

        private void RequestMissingFactionFacts()
        {
            if (!ShouldRun()) return;
            var missing = _factionSnapshots.Where(f => f?.FactionJournal?.Facts != null &&
                f.FactionJournal.Facts.Count == 0 && !_factionFactsInFlight.Contains(f.Key)).ToList();
            if (missing.Count == 0) return;
            const int batchSize = 2;
            int batchCount = (missing.Count + batchSize - 1) / batchSize;
            Log.Message($"[Firefly:{InitialFactionFactSeederLabel}] Dispatching {batchCount} batch(es) for {missing.Count} faction(s): " +
                string.Join(", ", missing.Select(f => f.Key)));
            for (int i = 0; i < missing.Count; i += batchSize)
                RequestFactionFacts(missing.Skip(i).Take(batchSize).ToList());
        }

        private void RequestMissingFactionTaglines()
        {
            if (!ShouldRun()) return;
            var missing = _factionSnapshots.Where(f => f?.FactionJournal?.Facts?.Count > 0 &&
                f.TaglineStale && !_factionTaglinesInFlight.Contains(f.Key)).ToList();
            foreach (var faction in missing)
                RequestBootstrapFactionTaglines(faction);
        }

        private void RequestMissingFactionDescriptions()
        {
            if (!ShouldRun()) return;
            var missing = _factionSnapshots.Where(f => f?.FactionJournal?.Facts?.Count > 0 &&
                f.Description.NullOrEmpty() && !_factionDescriptionsInFlight.Contains(f.Key)).ToList();
            if (missing.Count == 0) return;
            var recorder = Verse.Current.Game?.GetComponent<FireflyGameComponent>()?.Recorder;
            if (recorder == null) return;
            var keys = missing.Select(f => f.Key).ToList();
            foreach (string key in keys) _factionDescriptionsInFlight.Add(key);
            recorder.RegenerateFactionDescriptions(keys, success =>
            {
                foreach (string key in keys) _factionDescriptionsInFlight.Remove(key);
                if (!IsStillActive()) return;
                if (!success)
                {
                    Log.Warning($"[Firefly:{FactionIdentitySummariserLabel}] Bootstrap failed for " +
                        $"[{string.Join(", ", keys)}]. Will retry next load.");
                    return;
                }
                BootstrapIfNeeded();
                TryResumeWorldWork();
            });
        }

        // Public fact entry point for the Faction Update stage — writes to the event-driven
        // Narrative pair. Facts are append-only and share the exact revision/summary lifecycle
        // used by Story Threads.
        public bool AddFactionFact(string key, int day, string text)
        {
            var faction = _factionSnapshots.FirstOrDefault(f =>
                f != null && string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
            return faction?.NarrativeJournal.AddFact(GenTicks.TicksAbs, day, text) ?? false;
        }

        public bool AddFactionIdentityFact(string key, int day, string text)
        {
            var faction = _factionSnapshots.FirstOrDefault(f =>
                f != null && string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
            return faction?.FactionJournal.AddFact(GenTicks.TicksAbs, day, text) ?? false;
        }

        // ── LLM requests ────────────────────────────────────────────────────

        private void RequestSeedThreads()
        {
            _requestInFlight = true;
            Log.Message($"[Firefly:{InitialWorldThreadSeederLabel}] Sending...");
            LLMClient.Send(InitialWorldThreadSeederLabel, WorldThreadScanIngest.SeedSystemPrompt,
                WorldThreadScanIngest.BuildFactionContextBlock(this),
                onSuccess: rawJson =>
                {
                    if (!IsStillActive()) { _requestInFlight = false; return true; }
                    var touched = WorldThreadScanIngest.ApplyThreadResult(this, rawJson, WorldGenDay, seed: true);
                    if (touched == null) return false; // invalid JSON — worth another attempt
                    _requestInFlight = false;
                    _seedRequested = true;
                    // Deliberately NOT _lastProgressedDay = WorldGenDay here. WorldGenDay is 0,
                    // and a real colony's first day is also day 0 — setting this at bootstrap
                    // made QueueDailyWorldWork's "colonyDay <= _lastProgressedDay" guard treat
                    // the colony's genuine Day 0 as already processed, silently dropping the
                    // entire world pipeline for that day. _lastProgressedDay should only ever
                    // reflect a real (non-bootstrap) colony day; CompleteWorldWork already
                    // advances it correctly once actual daily work finishes.
                    _pendingWorldWork.Add(new PendingWorldWork
                    {
                        Day = WorldGenDay,
                        WorldGeneration = true,
                        Stage = WorldWorkStage.FactionUpdate,
                        TouchedWorldThreadIds = touched,
                    });
                    TryResumeWorldWork();
                    return true;
                },
                onError: err =>
                {
                    _requestInFlight = false;
                    Log.Warning($"[Firefly:{InitialWorldThreadSeederLabel}] Failed: {err}. Will retry next load.");
                });
        }

        public void TryResumeWorldWork()
        {
            if (_requestInFlight || _factionTaglinesInFlight.Count > 0 ||
                _factionDescriptionsInFlight.Count > 0 || !ShouldRun() ||
                !_seedRequested || !IsStillActive()) return;
            var work = _pendingWorldWork.OrderBy(w => w.WorldGeneration ? -1 : w.Day).FirstOrDefault();
            if (work == null) return;
            var recorder = Verse.Current.Game?.GetComponent<FireflyGameComponent>()?.Recorder;
            if (recorder == null) return;

            switch (work.Stage)
            {
                case WorldWorkStage.WorldOutcome:
                    RequestWorldOutcome(work);
                    break;
                case WorldWorkStage.WorldThreadUpdates:
                    RequestWorldThreadUpdates(work);
                    break;
                case WorldWorkStage.WorldThreadSummaries:
                    recorder.RegenerateWorldThreads(work.TouchedWorldThreadIds,
                        success => OnSummaryBatchCompleted(work, success, WorldWorkStage.FactionUpdate));
                    break;
                case WorldWorkStage.FactionUpdate:
                    RequestFactionUpdate(work);
                    break;
                case WorldWorkStage.NarrativeSummaries:
                    recorder.RegenerateFactionNarratives(work.TouchedFactionKeys,
                        success => OnSummaryBatchCompleted(work, success, WorldWorkStage.DescriptionSummaries));
                    break;
                case WorldWorkStage.DescriptionSummaries:
                    recorder.RegenerateFactionDescriptions(work.TouchedFactionFactKeys,
                        success => OnSummaryBatchCompleted(work, success, WorldWorkStage.FactionTaglines));
                    break;
                case WorldWorkStage.FactionTaglines:
                    RequestFactionTaglinesForWork(work);
                    break;
                case WorldWorkStage.WorldArcHistory:
                    RequestWorldArcHistory(work);
                    break;
            }
        }

        private void RequestWorldOutcome(PendingWorldWork work)
        {
            _requestInFlight = true;
            Log.Message($"[Firefly:{DailyWorldProgressionLabel}] Sending for Day {work.Day}...");
            LLMClient.Send(DailyWorldProgressionLabel, WorldThreadScanIngest.WorldOutcomeSystemPrompt,
                WorldThreadScanIngest.BuildWorldOutcomePrompt(this, work.ColonySummary),
                onSuccess: rawJson =>
                {
                    if (!IsStillActive() || !_pendingWorldWork.Contains(work)) { _requestInFlight = false; return true; }
                    string outcome = WorldThreadScanIngest.ValidateWorldOutcome(rawJson);
                    if (outcome == null) return false; // invalid — worth another attempt
                    _requestInFlight = false;
                    work.WorldOutcome = outcome;
                    SetDailyWorldSimulation(work.Day, outcome);
                    work.Stage = WorldWorkStage.WorldThreadUpdates;
                    TryResumeWorldWork();
                    return true;
                },
                onError: err =>
                {
                    _requestInFlight = false;
                    Log.Warning($"[Firefly:{DailyWorldProgressionLabel}] Failed for Day {work.Day}: {err}");
                });
        }

        private void RequestWorldThreadUpdates(PendingWorldWork work)
        {
            if (work.WorldOutcome.NullOrEmpty())
            {
                Log.Warning($"[Firefly:{DailyWorldThreadScanLabel}] Day {work.Day} has no persisted world outcome; restarting that stage.");
                work.Stage = WorldWorkStage.WorldOutcome;
                TryResumeWorldWork();
                return;
            }

            _requestInFlight = true;
            Log.Message($"[Firefly:{DailyWorldThreadScanLabel}] Sending for Day {work.Day}...");
            LLMClient.Send(DailyWorldThreadScanLabel, WorldThreadScanIngest.WorldThreadUpdatesSystemPrompt,
                WorldThreadScanIngest.BuildWorldThreadUpdatesPrompt(this, work.WorldOutcome),
                onSuccess: rawJson =>
                {
                    if (!IsStillActive() || !_pendingWorldWork.Contains(work)) { _requestInFlight = false; return true; }
                    var touched = WorldThreadScanIngest.ApplyThreadResult(this, rawJson, work.Day, seed: false);
                    if (touched == null) return false; // invalid JSON — worth another attempt
                    _requestInFlight = false;
                    work.TouchedWorldThreadIds = touched;
                    work.Stage = WorldWorkStage.WorldThreadSummaries;
                    TryResumeWorldWork();
                    return true;
                },
                onError: err =>
                {
                    _requestInFlight = false;
                    Log.Warning($"[Firefly:{DailyWorldThreadScanLabel}] Failed for Day {work.Day}: {err}");
                });
        }

        private void RequestFactionUpdate(PendingWorldWork work)
        {
            _requestInFlight = true;
            Log.Message($"[Firefly:{DailyWorldThreadFactionScanLabel}] Sending for Day {work.Day}...");
            LLMClient.Send(DailyWorldThreadFactionScanLabel, WorldThreadScanIngest.FactionUpdateSystemPrompt,
                WorldThreadScanIngest.BuildFactionUpdatePrompt(this, work.TouchedWorldThreadIds),
                onSuccess: rawJson =>
                {
                    if (!IsStillActive() || !_pendingWorldWork.Contains(work)) { _requestInFlight = false; return true; }
                    var applied = WorldThreadScanIngest.ApplyFactionUpdate(this, rawJson, work.Day);
                    if (applied == null) return false; // invalid JSON — worth another attempt
                    _requestInFlight = false;
                    work.TouchedFactionKeys = applied.NarrativeFactionKeys;
                    work.TouchedFactionFactKeys = applied.IdentityFactionKeys;
                    work.Stage = WorldWorkStage.NarrativeSummaries;
                    TryResumeWorldWork();
                    return true;
                },
                onError: err =>
                {
                    _requestInFlight = false;
                    Log.Warning($"[Firefly:{DailyWorldThreadFactionScanLabel}] Failed for Day {work.Day}: {err}");
                });
        }

        private void OnSummaryBatchCompleted(PendingWorldWork work, bool success, WorldWorkStage next)
        {
            if (!success || !_pendingWorldWork.Contains(work)) return;
            work.Stage = next;
            TryResumeWorldWork();
        }

        private void CompleteWorldWork(PendingWorldWork work)
        {
            if (!_pendingWorldWork.Contains(work)) return;
            _pendingWorldWork.Remove(work);
            if (!work.WorldGeneration) _lastProgressedDay = Math.Max(_lastProgressedDay, work.Day);
            TryResumeWorldWork();
        }

        private void AdvanceToWorldArcHistory(PendingWorldWork work)
        {
            if (!_pendingWorldWork.Contains(work)) return;
            work.Stage = WorldWorkStage.WorldArcHistory;
            TryResumeWorldWork();
        }

        private void SetDailyWorldSimulation(int day, string simulation)
        {
            if (day < 1 || simulation.NullOrEmpty()) return;
            if (_dailyWorldRecords.Count == 0 && _lastWorldArcDay == 0 &&
                _worldHistory.NullOrEmpty() && day > 1)
                _lastWorldArcDay = day - 1;
            var existing = _dailyWorldRecords.FirstOrDefault(r => r != null && r.Day == day);
            if (existing != null)
                existing.Simulation = simulation;
            else
                _dailyWorldRecords.Add(new DailyWorldRecord { Day = day, Simulation = simulation });
            _dailyWorldRecords.Sort((a, b) => a.Day.CompareTo(b.Day));
        }

        private const int WorldArcIntervalDays = 15;

        private int ComputeContiguousWorldArcThroughDay()
        {
            int throughDay = _lastWorldArcDay;
            foreach (var record in _dailyWorldRecords
                         .Where(r => r != null && r.Day > _lastWorldArcDay)
                         .OrderBy(r => r.Day))
            {
                if (record.Day != throughDay + 1 || record.Simulation.NullOrEmpty()) break;
                throughDay = record.Day;
            }
            return throughDay;
        }

        private void RequestWorldArcHistory(PendingWorldWork work)
        {
            if (work.WorldGeneration)
            {
                CompleteWorldWork(work);
                return;
            }

            int throughDay = ComputeContiguousWorldArcThroughDay();
            int lastArcDay = _lastWorldArcDay;
            if (throughDay - lastArcDay < WorldArcIntervalDays)
            {
                CompleteWorldWork(work);
                return;
            }

            var simulations = _dailyWorldRecords
                .Where(r => r != null && r.Day > lastArcDay && r.Day <= throughDay &&
                    !r.Simulation.NullOrEmpty())
                .OrderBy(r => r.Day).ToList();
            if (simulations.Count == 0) return;

            var sb = new StringBuilder();
            if (!_worldHistory.NullOrEmpty())
            {
                sb.AppendLine("=== EXISTING WORLD HISTORY ===");
                sb.AppendLine(_worldHistory);
                sb.AppendLine();
            }
            sb.AppendLine("=== RECENT DAILY WORLD SIMULATIONS ===");
            foreach (var record in simulations)
            {
                sb.AppendLine($"=== Day {record.Day} ===");
                sb.AppendLine(record.Simulation);
                sb.AppendLine();
            }

            _requestInFlight = true;
            Log.Message($"[Firefly:{MonthlyWorldArcHistoryLabel}] Sending... (through Day {throughDay})");
            LLMClient.Send(MonthlyWorldArcHistoryLabel, WorldThreadScanIngest.WorldArcHistorySystemPrompt, sb.ToString(),
                onSuccess: arcText =>
                {
                    if (!IsStillActive() || !_pendingWorldWork.Contains(work)) { _requestInFlight = false; return true; }
                    string history = arcText?.Trim() ?? "";
                    if (history.NullOrEmpty()) return false; // empty reply — worth another attempt
                    _requestInFlight = false;
                    _worldHistory = history;
                    _lastWorldArcDay = throughDay;
                    CompleteWorldWork(work);
                    return true;
                },
                onError: err =>
                {
                    _requestInFlight = false;
                    Log.Warning($"[Firefly:{MonthlyWorldArcHistoryLabel}] Failed through Day {throughDay}: {err}");
                });
        }

        private bool IsStillActive() => ReferenceEquals(Current, this);

        private void RequestFactionFacts(List<FactionSnapshot> batch)
        {
            var keys = batch.Where(f => f != null).Select(f => f.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (keys.Count == 0 || keys.Any(key => _factionFactsInFlight.Contains(key))) return;
            foreach (string key in keys) _factionFactsInFlight.Add(key);
            Log.Message($"[Firefly:{InitialFactionFactSeederLabel}] Sending for [{string.Join(", ", keys)}]...");
            LLMClient.Send(InitialFactionFactSeederLabel, WorldThreadScanIngest.FactionFactsSystemPrompt,
                WorldThreadScanIngest.BuildFactionFactsPrompt(batch),
                onSuccess: rawJson =>
                {
                    if (!IsStillActive())
                    {
                        foreach (string key in keys) _factionFactsInFlight.Remove(key);
                        return true;
                    }
                    var current = _factionSnapshots.Where(f => f != null &&
                        keys.Contains(f.Key, StringComparer.OrdinalIgnoreCase)).ToList();
                    var applied = WorldThreadScanIngest.ApplyInitialFactionFacts(
                        current, rawJson, WorldGenDay);
                    // Invalid (e.g. an unknown/duplicate faction_key) — worth another attempt.
                    // Keep the factions marked in-flight so the UI still shows them working
                    // rather than clearing the spinner mid-retry.
                    if (applied == null) return false;

                    foreach (string key in keys) _factionFactsInFlight.Remove(key);
                    var missing = keys.Where(key => !applied.Contains(key, StringComparer.OrdinalIgnoreCase) &&
                        !_factionSnapshots.Any(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase) &&
                            f.FactionJournal.Facts.Count > 0)).ToList();
                    if (missing.Count > 0)
                        Log.Warning($"[Firefly:{InitialFactionFactSeederLabel}] Response omitted {string.Join(", ", missing)}. Will retry next load.");
                    RequestMissingFactionDescriptions();
                    RequestMissingFactionTaglines();
                    BootstrapIfNeeded();
                    return true;
                },
                onError: err =>
                {
                    foreach (string key in keys) _factionFactsInFlight.Remove(key);
                    Log.Warning($"[Firefly:{InitialFactionFactSeederLabel}] Failed for {string.Join(", ", keys)}: {err}. Will retry next load.");
                });
        }

        // Fallback only — the very first tagline for most factions is already produced inline by
        // the Faction Facts response itself. This exists for factions that already have facts but
        // somehow still lack one (an old save from before taglines existed, or an interrupted
        // bootstrap). One independent call per faction, same as the daily refresh — no batching.
        private void RequestBootstrapFactionTaglines(FactionSnapshot faction)
        {
            if (faction == null || !(faction.FactionJournal?.Facts?.Count > 0) || !faction.TaglineStale ||
                _factionTaglinesInFlight.Contains(faction.Key))
                return;
            _factionTaglinesInFlight.Add(faction.Key);
            var target = new List<FactionSnapshot> { faction };
            var narrativeRevisions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [faction.Key] = faction.NarrativeJournal.FactRevision,
            };
            var factionRevisions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [faction.Key] = faction.FactionJournal.FactRevision,
            };
            Log.Message($"[Firefly:{FactionTaglineUpdaterLabel}] Sending bootstrap for {faction.Key}...");
            LLMClient.Send(FactionTaglineUpdaterLabel, WorldThreadScanIngest.FactionTaglineSystemPrompt,
                WorldThreadScanIngest.BuildFactionTaglinePrompt(target),
                onSuccess: rawJson =>
                {
                    if (!IsStillActive())
                    {
                        _factionTaglinesInFlight.Remove(faction.Key);
                        return true;
                    }
                    var applied = WorldThreadScanIngest.ApplyFactionTaglines(target, narrativeRevisions, factionRevisions, rawJson);
                    if (applied == null) return false; // invalid — worth another attempt
                    _factionTaglinesInFlight.Remove(faction.Key);
                    BootstrapIfNeeded();
                    TryResumeWorldWork();
                    return true;
                },
                onError: err =>
                {
                    _factionTaglinesInFlight.Remove(faction.Key);
                    Log.Warning($"[Firefly:{FactionTaglineUpdaterLabel}] Bootstrap failed for " +
                        $"{faction.Key}: {err}. Will retry next load.");
                });
        }

        // One independent LLM call per faction whose Narrative Facts OR Faction (identity) Facts
        // actually changed today (and whose tagline is consequently stale) — no cross-faction
        // batching. Each call gets only that faction's own narrative facts, faction facts, and
        // previous tagline.
        private void RequestFactionTaglinesForWork(PendingWorldWork work)
        {
            var touched = new HashSet<string>(
                (work.TouchedFactionKeys ?? Enumerable.Empty<string>())
                    .Concat(work.TouchedFactionFactKeys ?? Enumerable.Empty<string>()),
                StringComparer.OrdinalIgnoreCase);
            var factions = _factionSnapshots.Where(f => f != null && touched.Contains(f.Key) &&
                f.FactionJournal.Facts.Count > 0 && f.TaglineStale).ToList();
            if (factions.Count == 0)
            {
                AdvanceToWorldArcHistory(work);
                return;
            }

            _requestInFlight = true;
            int remaining = factions.Count;
            bool allSucceeded = true;
            void SettleOne()
            {
                remaining--;
                if (remaining > 0) return;
                _requestInFlight = false;
                if (!IsStillActive() || !_pendingWorldWork.Contains(work)) return;
                if (!allSucceeded)
                {
                    Log.Warning($"[Firefly:{FactionTaglineUpdaterLabel}] One or more factions failed for Day {work.Day}; retrying this stage next resume.");
                    return;
                }
                AdvanceToWorldArcHistory(work);
            }

            foreach (var faction in factions)
            {
                var target = new List<FactionSnapshot> { faction };
                var narrativeRevisions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [faction.Key] = faction.NarrativeJournal.FactRevision,
                };
                var factionRevisions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [faction.Key] = faction.FactionJournal.FactRevision,
                };
                Log.Message($"[Firefly:{FactionTaglineUpdaterLabel}] Sending for Day {work.Day}, {faction.Key}...");
                LLMClient.Send(FactionTaglineUpdaterLabel, WorldThreadScanIngest.FactionTaglineSystemPrompt,
                    WorldThreadScanIngest.BuildFactionTaglinePrompt(target),
                    onSuccess: rawJson =>
                    {
                        if (!IsStillActive() || !_pendingWorldWork.Contains(work)) { SettleOne(); return true; }
                        if (WorldThreadScanIngest.ApplyFactionTaglines(target, narrativeRevisions, factionRevisions, rawJson) == null)
                            return false; // invalid — worth another attempt
                        SettleOne();
                        return true;
                    },
                    onError: err =>
                    {
                        allSucceeded = false;
                        Log.Warning($"[Firefly:{FactionTaglineUpdaterLabel}] Failed for Day {work.Day} " +
                            $"faction {faction.Key}: {err}");
                        SettleOne();
                    });
            }
        }

        // ── World Thread journal mutation ───────────────────────────────────

        public string AddWorldThread(string title, string initialSummary,
            IEnumerable<string> facts, int day)
        {
            var ids = new HashSet<string>(_worldThreads.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            string id = UniqueSlug.Create(title, "world-thread", ids);
            var thread = new WorldThread
            {
                Id = id,
                Title = title,
                CreatedDay = day,
                LastUpdatedDay = day,
                Journal = new JournalRecord(),
            };
            foreach (string fact in facts ?? Enumerable.Empty<string>())
                thread.Journal.AddFact(GenTicks.TicksAbs, day, fact);
            if (!initialSummary.NullOrEmpty())
                thread.Journal.SetActiveSummary(initialSummary, thread.Journal.FactRevision);
            _worldThreads.Add(thread);
            return id;
        }

        public bool AddWorldThreadFact(string threadId, int day, string text)
        {
            var thread = _worldThreads.FirstOrDefault(t =>
                string.Equals(t.Id, threadId, StringComparison.OrdinalIgnoreCase));
            if (thread == null) return false;
            bool added = thread.Journal.AddFact(GenTicks.TicksAbs, day, text);
            if (added) thread.LastUpdatedDay = day;
            return added;
        }

    }
}
