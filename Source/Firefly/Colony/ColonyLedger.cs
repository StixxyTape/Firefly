using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Firefly
{
    public class ColonyLedger
    {
        private static Game _cachedGame;
        private static ColonyLedger _cachedLedger;

        public static ColonyLedger Current
        {
            get
            {
                Game game = Verse.Current.Game;
                if (game == null)
                {
                    _cachedGame   = null;
                    _cachedLedger = null;
                    return null;
                }
                if (!ReferenceEquals(game, _cachedGame))
                {
                    _cachedGame   = game;
                    _cachedLedger = game.GetComponent<FireflyGameComponent>()?.Ledger;
                }
                return _cachedLedger;
            }
        }

        private int  _recordingDay;
        private bool _initialized = false;
        private bool _enabled     = false;
        public  int  RecordingDay => _recordingDay;

        // Whether Firefly is enabled for this save. Set once at game start from the player's choice
        // on the storyteller page and persisted to the save file. Gates all capture entry points so
        // the mod is fully inert when the player opted out.
        public void SetEnabled(bool enabled) => _enabled = enabled;

        private readonly CombatEventBuffer  _combatBuffer    = new CombatEventBuffer();
        private readonly PawnHealthTracker  _healthTracker   = new PawnHealthTracker();
        private readonly RelationTracker    _relationTracker = new RelationTracker();
        private readonly RosterTracker      _rosterTracker   = new RosterTracker();

        private float _prevColonyFoodDays = -1f;
        private int   _prevColonyMedicine = -1;
        private float _prevColonyWealth   = -1f;

        // ── In-memory journal storage (persisted via ExposeData) ──────────────
        private List<DailyRecord> _pastDays      = new List<DailyRecord>();
        private string            _colonyHistory  = "";
        private int               _lastArcDay     = 0;
        private List<StoryThread>   _storyThreads     = new List<StoryThread>();

        public IReadOnlyList<DailyRecord> PastDays      => _pastDays;
        public string                     ColonyHistory  => _colonyHistory;
        public int                        LastArcDay     => _lastArcDay;
        public IReadOnlyList<StoryThread>   StoryThreads     => _storyThreads;

        // ── Live timeline buffer ──────────────────────────────────────────────
        private bool                             _dayHeaderWritten        = false;
        private readonly StringBuilder           _timelineBuffer          = new StringBuilder();
        private readonly HashSet<int>            _mentionedQuestIds       = new HashSet<int>();


        // ── Capture methods ───────────────────────────────────────────────────

        public void CaptureBattleEvent(string initiator, string initiatorId, string target, string targetId, bool reachedTarget, string weapon, string coverHit, bool initiatorIsColonist, string battleId, LogEntry_DamageResult entry, Pawn initiatorPawn = null, Pawn targetPawn = null)
        {
            if (!_initialized || !_enabled) return;
            bool shouldAnnounce = _combatBuffer.RecordBattleEvent(initiator, initiatorId, target, targetId,
                reachedTarget, weapon, coverHit, initiatorIsColonist, battleId, entry, out CombatEvent ev);
            if (shouldAnnounce)
            {
                string initiatorTag = IntroduceTag(initiatorPawn);
                string targetTag    = IntroduceTag(targetPawn);
                string col      = initiatorIsColonist ? (initiator ?? "?") : (target   ?? "?");
                string other    = initiatorIsColonist ? (target    ?? "?") : (initiator ?? "?");
                string colTag   = initiatorIsColonist ? initiatorTag : targetTag;
                string otherTag = initiatorIsColonist ? targetTag    : initiatorTag;
                AppendEvent(ev.Tick, $"[Combat started] {col}{colTag} vs {other}{otherTag}");
            }
        }

        public void CaptureStateChange(string targetName, string text)
        {
            if (!_initialized || !_enabled || targetName.NullOrEmpty()) return;
            AppendEvent(Find.TickManager.TicksAbs, text + ".");
        }

        public void CaptureOutcome(string subject, string subjectId, string outcome, string initiator, string cause)
        {
            if (!_initialized || !_enabled || subject.NullOrEmpty()) return;
            long tick = 0L;
            try { tick = Find.TickManager.TicksAbs; } catch { }
            _combatBuffer.RecordOutcome(subject, subjectId, outcome, initiator, cause, tick);
        }

        public void CaptureFactionCombat(Faction factionA, Faction factionB, long tick)
        {
            if (!_initialized) return;
            if (factionA == null || factionB == null) return;
            if (factionA == Faction.OfPlayer || factionB == Faction.OfPlayer) return;
            if (factionA == factionB) return;

            string nameA = factionA.Name ?? "Unknown";
            string nameB = factionB.Name ?? "Unknown";
            if (!_combatBuffer.ShouldEmitFactionCombat(nameA, nameB, tick)) return;

            AppendEvent(tick, $"{nameA} vs {nameB}");
        }

        public void CaptureMessage(Message msg)
        {
            if (!_initialized || !_enabled || msg?.text.NullOrEmpty() != false) return;
            IntroduceFromTargets(msg.lookTargets);
            AppendEvent(Find.TickManager.TicksAbs, StripTags(msg.text));
        }

        private void IntroduceFromTargets(LookTargets targets)
        {
            if (targets == null) return;
            try
            {
                foreach (var t in targets.targets)
                    if (t.Thing is Pawn p) IntroduceTag(p);
            }
            catch { }
        }

        public void CaptureHazardEvent(string victim, string hazardLabel, LogEntry_DamageResult entry, Pawn victimPawn = null)
        {
            if (!_initialized || !_enabled) return;
            bool isNewHazard = _combatBuffer.RecordHazardEvent(victim, hazardLabel, entry, out HazardEvent ev);
            string victimTag = IntroduceTag(victimPawn);
            if (isNewHazard)
                AppendEvent(ev.Tick, $"[{ev.Victim}{victimTag} started taking damage from {ev.HazardLabel}]");
        }

        public void CaptureDecision(string formattedText)
        {
            if (!_initialized || !_enabled || formattedText.NullOrEmpty()) return;
            AppendEvent(Find.TickManager.TicksAbs, StripTags(formattedText));
        }

        private static readonly FieldInfo _initiatorField =
            AccessTools.Field(typeof(PlayLogEntry_Interaction), "initiator");
        private static readonly FieldInfo _recipientField =
            AccessTools.Field(typeof(PlayLogEntry_Interaction), "recipient");
        private static readonly FieldInfo _interactionDefField =
            AccessTools.Field(typeof(PlayLogEntry_Interaction), "intDef");
        private static readonly HashSet<string> _blockedInteractions = new HashSet<string>
            { "Chitchat", "DeepTalk", "Nuzzle", "AnimalChat" };
        private static readonly FieldInfo _logEntryTicksAbsField =
            AccessTools.Field(typeof(LogEntry), "ticksAbs");

        public void Record(Map map, int hourOfDay)
        {
            if (!_initialized)
            {
                _initialized = true;
                _combatBuffer.ClearPending();
                Log.Message("[Firefly] Ledger initialized — skipping historical events.");
            }

            // _recordingDay is explicit persisted state (advanced by AddDailyRecord when a day
            // archives), not derived from _pastDays.Count — that would break under any future
            // retention/compaction that prunes old records.

            long  snapshotTick = Find.TickManager.TicksAbs;
            float lon          = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;

            EnsureDayHeader(map, lon, snapshotTick);

            string weather = map.weatherManager?.curWeather?.LabelCap ?? "Unknown";
            float  tempC   = map.mapTemperature?.OutdoorTemp ?? 0f;
            AppendEvent(snapshotTick, $"Weather — {weather}, {Mathf.RoundToInt(tempC)}°C");

            var colonists = map.mapPawns?.FreeColonists?.ToList();
            if (colonists != null)
            {
                foreach (var p in colonists)
                {
                    if (p == null) continue;
                    string activity = p.jobs?.curDriver?.GetReport()?.CapitalizeFirst() ?? "idle";
                    if (p.jobs?.curJob?.def == JobDefOf.Research)
                    {
                        string proj = Find.ResearchManager.GetProject()?.LabelCap;
                        if (!proj.NullOrEmpty()) activity = $"Researching \"{proj}\"";
                    }
                    var carried = p.carryTracker?.CarriedThing;
                    if (carried != null)
                        activity += $" (carrying {StripTags(carried.Label)})";
                    string location  = GetPawnLocation(p);
                    int    mood      = Mathf.RoundToInt((p.needs?.mood?.CurLevel ?? 0.5f) * 100f);
                    string introTag  = IntroduceTag(p);
                    AppendEvent(snapshotTick, $"{PawnFullName(p)}{introTag} — {activity} ({location}, mood {mood}%)");
                }
            }

            Log.Message($"[Firefly] Ledger recorded: Hour {hourOfDay:D2}:00, Day {_recordingDay}");
        }

        private static string BuildTileDescription(Map map)
        {
            try
            {
                var tile = Find.WorldGrid?[map.Tile];
                if (tile == null) return "";

                string biome      = tile.PrimaryBiome?.LabelCap ?? "";
                var    hilliness  = tile.HillinessLabel;
                var    mutators   = tile.Mutators;

                var features = new List<string>();
                if (mutators != null)
                    foreach (var m in mutators)
                        if (!m.label.NullOrEmpty()) features.Add(m.LabelCap);

                bool isFlat       = hilliness == Hilliness.Flat;
                bool isMountainous = hilliness == Hilliness.Mountainous || hilliness == Hilliness.Impassable;

                if (!isFlat && !isMountainous)
                {
                    string hillLabel = hilliness switch
                    {
                        Hilliness.SmallHills => "Small Hills",
                        Hilliness.LargeHills => "Large Hills",
                        _                    => null,
                    };
                    if (hillLabel != null) features.Insert(0, hillLabel);
                }

                string prefix = isFlat ? "Flat " : isMountainous ? "Mountainous " : "";
                string loc    = features.Count > 0
                    ? $"{prefix}{biome} with {string.Join(", ", features)}"
                    : $"{prefix}{biome}";

                return $"Deep in a {loc}.";
            }
            catch { return ""; }
        }

        private void EnsureDayHeader(Map map, float lon, long tick)
        {
            if (_dayHeaderWritten) return;
            _dayHeaderWritten = true;
            string colony      = map.info?.parent?.Label ?? "Unnamed Colony";
            string fullDate    = GenDate.DateFullStringAt(tick, new UnityEngine.Vector2(lon, 0f));
            string tileDesc    = BuildTileDescription(map);
            string locationLine = tileDesc.NullOrEmpty() ? "" : tileDesc + "\n";
            string header      = $"=== DAY {_recordingDay} CHRONICLE — {colony} ===\n{fullDate}\n{locationLine}\n=== EVENTS ===\n";
            lock (_timelineBuffer) _timelineBuffer.Append(header);

            if (_pastDays.Count == 0)
            {
                try
                {
                    var    scenario = Find.Scenario;
                    string sName    = scenario?.name;
                    string sDesc    = StripTags(scenario?.description);
                    if (!sName.NullOrEmpty())
                    {
                        string flat = sDesc.NullOrEmpty() ? "" : sDesc.Replace("\r\n", " ").Replace('\n', ' ').Trim();
                        string line = flat.NullOrEmpty() ? sName : $"{sName} — {flat}";
                        float  hf   = GenDate.HourFloat(tick, lon);
                        int    h    = (int)hf, m = (int)((hf % 1f) * 60f);
                        AppendRawToTimeline($"  - [{h:D2}:{m:D2}] {line}\n");
                    }
                }
                catch { }
            }
        }

        public string GetCurrentDayContent()
        {
            lock (_timelineBuffer) return _timelineBuffer.ToString();
        }

        // Hourly drain — keeps combat outcome matching working without file writes.
        public void DrainToBuffers(float lon) => _combatBuffer.DrainToBuffers(lon);

        // Called at midnight — does one final drain then returns the accumulated content.
        public (string CombatContent, string HazardContent) FlushDrainedSections(float lon) =>
            _combatBuffer.FlushDrainedSections(lon);

        // Read-only peek at today's accumulated combat/hazard for the journal UI.
        public string GetCurrentCombatContent(float lon) => _combatBuffer.GetCurrentCombatContent(lon);

        public string GetCurrentHazardContent(float lon) => _combatBuffer.GetCurrentHazardContent(lon);

        // ── Past-day management ───────────────────────────────────────────────

        public void AddDailyRecord(DailyRecord record)
        {
            _pastDays.Add(record);
            _recordingDay = record.Day + 1;
        }

        public void SetDailySummary(int day, string summary)
        {
            var record = _pastDays.FirstOrDefault(d => d.Day == day);
            if (record != null) record.Summary = summary?.Trim() ?? "";
        }

        public void SetColonyHistory(string history, int throughDay)
        {
            _colonyHistory = history;
            _lastArcDay    = throughDay;
        }

        // Main-thread only. Queue via MainThreadQueue from async callbacks.
        public void AddStoryThread(string id, string name, string activeSummary)
        {
            id   = id?.Trim();
            name = name?.Trim();
            if (id.NullOrEmpty() || name.NullOrEmpty()) return;
            if (_storyThreads.Any(s => s.Id == id)) return;
            _storyThreads.Add(new StoryThread
            {
                Id = id,
                Name = name,
                Journal = new JournalRecord { ActiveSummary = activeSummary?.Trim() ?? "" },
            });
        }

        // Main-thread only. Queue via MainThreadQueue from async callbacks.
        public void AddFactToThread(string threadId, long tick, int day, string text)
        {
            threadId = threadId?.Trim();
            if (threadId.NullOrEmpty() || text.NullOrEmpty()) return;
            string flat = StripTags(text).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (flat.NullOrEmpty()) return;
            var thread = _storyThreads.FirstOrDefault(s => s.Id == threadId);
            if (thread == null)
            {
                // Was silent — a create/fact-add ordering mistake would otherwise lose the fact
                // with no trace. Now at least visible in the log for whoever's calling this.
                Log.Warning($"[Firefly] AddFactToThread: no thread with id \"{threadId}\" — fact dropped: {flat}");
                return;
            }
            thread.Journal.AddFact(tick, day, flat);
        }

        // Main-thread only. Queue via MainThreadQueue from async callbacks.
        // Marks a thread as touched at this tick — drives the UI's "updated today" badge.
        public void TouchStoryThread(string id, long tick)
        {
            id = id?.Trim();
            if (id.NullOrEmpty()) return;
            var thread = _storyThreads.FirstOrDefault(s => s.Id == id);
            if (thread != null) thread.Journal.LastTouchedTick = tick;
        }

        // ── Colony status ─────────────────────────────────────────────────────

        public string BuildColonyStatusSection(Map map)
        {
            try
            {
                var colonists = map.mapPawns?.FreeColonists;
                int count = colonists?.Count ?? 0;

                float totalNutrition = map.resourceCounter.TotalHumanEdibleNutrition;
                float dailyRate = count > 0
                    ? colonists.Where(p => p != null).Sum(p => (p.needs?.food?.FoodFallPerTick ?? 0f) * 60000f)
                    : 1.6f;
                float foodDays = dailyRate > 0f ? totalNutrition / dailyRate : 0f;

                int medicine = 0;
                if (ThingDefOf.MedicineHerbal     != null) medicine += map.resourceCounter.GetCount(ThingDefOf.MedicineHerbal);
                if (ThingDefOf.MedicineIndustrial != null) medicine += map.resourceCounter.GetCount(ThingDefOf.MedicineIndustrial);
                if (ThingDefOf.MedicineUltratech  != null) medicine += map.resourceCounter.GetCount(ThingDefOf.MedicineUltratech);

                float wealth = map.resourceCounter.GetCount(ThingDefOf.Silver);

                string DeltaStr(float cur, float prev) =>
                    prev < 0f ? "" : cur > prev ? $" (▲ from {prev:N0})" : cur < prev ? $" (▼ from {prev:N0})" : "";

                string foodDelta    = _prevColonyFoodDays < 0f ? "" :
                    foodDays > _prevColonyFoodDays ? $" (▲ from {_prevColonyFoodDays:F1})" :
                    foodDays < _prevColonyFoodDays ? $" (▼ from {_prevColonyFoodDays:F1})" : "";
                string foodWarning  = foodDays < 4f ? " — low" : "";

                var sb = new StringBuilder();
                sb.AppendLine("=== COLONY STATUS ===");
                sb.AppendLine($"Food: {foodDays:F1} days{foodDelta}{foodWarning}");
                sb.AppendLine($"Medicine: {medicine}{DeltaStr(medicine, _prevColonyMedicine)}");
                sb.AppendLine($"Silver: {wealth:N0}{DeltaStr(wealth, _prevColonyWealth)}");

                _prevColonyFoodDays = foodDays;
                _prevColonyMedicine = medicine;
                _prevColonyWealth   = wealth;

                return sb.ToString();
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] BuildColonyStatusSection failed: {e.Message}");
                return "";
            }
        }

        // ── Health / relations / skills ───────────────────────────────────────

        public string BuildComparisonSection(Map map)
        {
            var colonists = map.mapPawns?.FreeColonists?.ToList();
            if (colonists == null || colonists.Count == 0) return "";

            var sb = new StringBuilder();

            var currentHealth = new Dictionary<string, PawnHealthSnapshot>();
            sb.AppendLine("=== COLONIST HEALTH ===");
            foreach (var p in colonists)
            {
                if (p == null) continue;
                string name = PawnFullName(p);
                var (overallLine, conditions) = _healthTracker.DescribeAndSnapshot(p, currentHealth);

                sb.AppendLine($"  {name} health overview:");
                sb.AppendLine($"    - {overallLine}");
                if (!conditions.NullOrEmpty())
                    sb.AppendLine($"    - {conditions}");
            }

            // Prisoners and slaves that appeared in today's roster
            var captives = new List<Pawn>();
            if (map.mapPawns?.PrisonersOfColonySpawned != null)
                captives.AddRange(map.mapPawns.PrisonersOfColonySpawned.Where(p => p != null && _rosterTracker.TrackedPawnIds.Contains(p.ThingID ?? "")));
            if (map.mapPawns?.SlavesOfColonySpawned != null)
                captives.AddRange(map.mapPawns.SlavesOfColonySpawned.Where(p => p != null && _rosterTracker.TrackedPawnIds.Contains(p.ThingID ?? "")));

            if (captives.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("=== PRISONER/SLAVE HEALTH ===");
                foreach (var p in captives)
                {
                    string name = PawnFullName(p);
                    var (overallLine, conditions) = _healthTracker.DescribeAndSnapshot(p, currentHealth);

                    string role = p.IsPrisonerOfColony ? "Prisoner" : "Slave";
                    sb.AppendLine($"  {name} ({role}) health overview:");
                    sb.AppendLine($"    - {overallLine}");
                    if (!conditions.NullOrEmpty())
                        sb.AppendLine($"    - {conditions}");
                }
            }
            // Committed once, after both colonist and captive passes, so a captive's "yesterday"
            // lookup above never sees a colonist (or its own) just-written entry from this batch.
            _healthTracker.CommitDay(currentHealth);

            var allTracked = new List<Pawn>(colonists);
            if (map.mapPawns?.PrisonersOfColonySpawned != null) allTracked.AddRange(map.mapPawns.PrisonersOfColonySpawned.Where(p => p != null));
            if (map.mapPawns?.SlavesOfColonySpawned    != null) allTracked.AddRange(map.mapPawns.SlavesOfColonySpawned.Where(p => p != null));

            string relationChanges = _relationTracker.BuildRelationSection(map, allTracked, _rosterTracker.TrackedPawnIds);
            if (!relationChanges.NullOrEmpty())
            {
                sb.AppendLine();
                sb.Append(relationChanges);
            }

            string factionChanges = _relationTracker.BuildFactionSection();
            if (!factionChanges.NullOrEmpty())
            {
                sb.AppendLine();
                sb.Append(factionChanges);
            }

            string skillChanges = _relationTracker.BuildSkillSection(colonists);
            if (!skillChanges.NullOrEmpty())
            {
                sb.AppendLine();
                sb.Append(skillChanges);
            }

            return sb.ToString();
        }

        // ── Save / load ───────────────────────────────────────────────────────

        public void ExposeData()
        {
            bool saving = Scribe.mode == LoadSaveMode.Saving;

            var questIds     = saving ? _mentionedQuestIds.ToList() : null;
            string timelineSnapshot = saving ? _timelineBuffer.ToString() : null;

            Scribe_Values.Look(ref _recordingDay,      "recordingDay",      0);
            Scribe_Values.Look(ref _initialized,       "initialized",       false);
            Scribe_Values.Look(ref _dayHeaderWritten,  "dayHeaderWritten",  false);
            Scribe_Values.Look(ref _colonyHistory,     "colonyHistory",     "");
            Scribe_Values.Look(ref _lastArcDay,        "lastArcDay",        0);
            Scribe_Values.Look(ref timelineSnapshot,   "timelineBuffer",    "");
            Scribe_Collections.Look(ref _pastDays,          "pastDays",         LookMode.Deep);
            Scribe_Collections.Look(ref _storyThreads,        "storyThreads",      LookMode.Deep);
            Scribe_Values.Look(ref _prevColonyFoodDays, "prevColonyFoodDays", -1f);
            Scribe_Values.Look(ref _prevColonyMedicine, "prevColonyMedicine", -1);
            Scribe_Values.Look(ref _prevColonyWealth,   "prevColonyWealth",   -1f);
            Scribe_Collections.Look(ref questIds,           "mentionedQuestIds", LookMode.Value);
            _combatBuffer.ExposeData();
            _healthTracker.ExposeData();
            _relationTracker.ExposeData();
            _rosterTracker.ExposeData();

            if (Scribe.mode != LoadSaveMode.LoadingVars) return;

            lock (_timelineBuffer)
            {
                _timelineBuffer.Clear();
                if (!timelineSnapshot.NullOrEmpty())
                    _timelineBuffer.Append(timelineSnapshot);
            }

            if (_pastDays == null) _pastDays = new List<DailyRecord>();
            if (_storyThreads == null)
                _storyThreads = new List<StoryThread>();
            // Normalize IDs, remove nulls/blanks/duplicates, repair Facts lists.
            var seenThreadIds = new HashSet<string>();
            _storyThreads.RemoveAll(s =>
            {
                if (s == null) return true;
                s.Id = s.Id?.Trim() ?? "";
                return s.Id.NullOrEmpty() || !seenThreadIds.Add(s.Id);
            });
            foreach (var s in _storyThreads)
            {
                // Id is validated above (required — it's the lookup key); Name is cosmetic but
                // the journal UI calls .ToUpperInvariant() on it unguarded, so a null/blank Name
                // from a corrupted or hand-edited save would throw when the Threads tab is opened.
                if (s.Name.NullOrEmpty()) s.Name = "(unnamed)";
                if (s.Journal == null) s.Journal = new JournalRecord();
                s.Journal.Facts.RemoveAll(f => f == null || f.Text.NullOrEmpty() || f.Text.Trim().Length == 0);
            }
            if (_colonyHistory    == null) _colonyHistory    = "";

            _mentionedQuestIds.Clear();
            if (questIds != null)
                foreach (var id in questIds) _mentionedQuestIds.Add(id);
        }

        // ── Pawn name formatting ──────────────────────────────────────────────

        public static string PawnFullName(Pawn pawn)
        {
            if (pawn == null) return "?";
            if (pawn.Name is NameTriple nt)
            {
                bool hasNick = !nt.Nick.NullOrEmpty() && nt.Nick != nt.First && nt.Nick != nt.Last;
                return hasNick
                    ? $"{nt.First} '{nt.Nick}' {nt.Last}"
                    : $"{nt.First} {nt.Last}".Trim();
            }
            return pawn.LabelShort ?? "?";
        }

        // ── Timeline buffer ───────────────────────────────────────────────────

        private static Map ResolveMap()
        {
            var maps = Find.Maps;
            if (maps != null)
                for (int i = 0; i < maps.Count; i++)
                    if (maps[i] != null && maps[i].IsPlayerHome) return maps[i];
            return Find.CurrentMap;
        }

        public void AppendRawToTimeline(string content)
        {
            if (!_initialized || !_enabled || content.NullOrEmpty()) return;
            lock (_timelineBuffer) _timelineBuffer.Append(content);
        }

        internal void AppendEvent(long tick, string text)
        {
            if (!_initialized || !_enabled) return;
            try
            {
                Map   map = ResolveMap();
                float lon = Find.WorldGrid?.LongLatOf(map?.Tile ?? 0).x ?? 0f;
                int hr  = GenDate.HourInteger(tick, lon);
                int min = (int)((GenDate.HourFloat(tick, lon) % 1f) * 60f);
                string flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
                lock (_timelineBuffer) _timelineBuffer.Append($"  - [{hr:D2}:{min:D2}] {flat}\n");
            }
            catch { }
        }

        // ── Clear ─────────────────────────────────────────────────────────────

        public void Clear()
        {
            _combatBuffer.Clear();
            _rosterTracker.Clear();

            _dayHeaderWritten = false;
            _mentionedQuestIds.Clear();
            lock (_timelineBuffer)   _timelineBuffer.Length = 0;
        }

        // ── Pawn roster / tagging ─────────────────────────────────────────────
        // (implementation in RosterTracker; these wrappers own the _initialized/_enabled gating
        // that RosterTracker itself doesn't know about)

        public string IntroduceTag(Pawn pawn)
        {
            if (!_initialized || !_enabled || pawn == null) return "";
            return _rosterTracker.IntroduceTag(pawn);
        }

        public void IntroduceEventLeader(Pawn pawn, string eventLabel, long tick)
        {
            if (!_initialized || pawn == null) return;
            _rosterTracker.IntroduceEventLeader(pawn, eventLabel, tick);
        }

        public void EnsureCaptivesIntroduced(Map map)
        {
            // Also gated on _enabled: the original per-pawn IntroduceTag calls this used to make
            // were themselves gated on _enabled, so the net effect when disabled was always a
            // no-op — made explicit here now that RosterTracker.IntroduceTag has no gate of its own.
            if (!_initialized || !_enabled || map == null) return;
            try { _rosterTracker.EnsureCaptivesIntroduced(map); }
            catch { }
        }

        public void RefreshTrackedPawnCategories(Map map)
        {
            if (!_initialized || !_enabled || map?.mapPawns == null) return;
            try { _rosterTracker.RefreshTrackedPawnCategories(map); }
            catch { }
        }

        public string BuildPawnRosterSection() => _rosterTracker.BuildPawnRosterSection();

        private static string InjectAfterFirst(string text, string name, string tag)
        {
            if (tag.NullOrEmpty() || name.NullOrEmpty()) return text;
            int idx = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return text;
            return text.Substring(0, idx + name.Length) + tag + text.Substring(idx + name.Length);
        }

        private static string GetPawnLocation(Pawn p)
        {
            if (!p.Spawned)
            {
                if (p.ParentHolder is Pawn_CarryTracker carrierTracker)
                    return GetPawnLocation(carrierTracker.pawn);
                return "in transit";
            }
            var room = p.Position.GetRoom(p.Map);
            if (room == null || room.IsHuge)
            {
                var roof = p.Map.roofGrid.RoofAt(p.Position);
                if (roof != null && roof.isNatural) return "mountain tunnel";
                return "outside";
            }
            string role = room.Role?.label;
            if (role.NullOrEmpty() || role == "none") return "indoors";
            return role;
        }

        // ── Quest tracking ────────────────────────────────────────────────────

        private void RecordMentionedQuest(Quest quest)
        {
            if (quest == null) return;
            _mentionedQuestIds.Add(quest.id);
            if (quest.parent != null) _mentionedQuestIds.Add(quest.parent.id);
        }

        public string BuildMentionedQuestsSnapshot()
        {
            if (_mentionedQuestIds.Count == 0) return null;
            var mgr = Find.QuestManager;
            if (mgr == null) return null;

            var all      = mgr.QuestsListForReading;
            var mentioned = new HashSet<Quest>(
                all.Where(q => _mentionedQuestIds.Contains(q.id)));

            // Pull in children of mentioned quests even if they weren't directly logged
            foreach (var q in all)
                if (q.parent != null && mentioned.Contains(q.parent))
                    mentioned.Add(q);

            var topLevel = mentioned
                .Where(q => q.parent == null || !mentioned.Contains(q.parent))
                .OrderBy(q => q.State == QuestState.Ongoing        ? 0 :
                              q.State == QuestState.NotYetAccepted ? 1 : 2)
                .ToList();

            if (topLevel.Count == 0) return null;

            var mentionedList = mentioned.ToList();
            var sb = new StringBuilder();
            foreach (var q in topLevel)
                MainTabWindow_Journal.AppendQuestBlock(sb, q, mentionedList, 0);
            return sb.ToString().TrimEnd();
        }

        // ── Archive / log entry capture ───────────────────────────────────────

        public void CaptureArchiveEntry(IArchivable item)
        {
            if (!_initialized || item == null) return;
            try
            {
                string label   = null;
                string tooltip = null;
                try { label   = StripTags(item.ArchivedLabel); }   catch { }
                try { tooltip = StripTags(item.ArchivedTooltip); } catch { }

                string text;
                string prefix;

                if (item is Letter)
                {
                    if (tooltip.NullOrEmpty())
                    {
                        try
                        {
                            var tv = Traverse.Create(item).Field("text");
                            if (tv.FieldExists()) tooltip = StripTags(tv.GetValue()?.ToString());
                        }
                        catch { }
                    }

                    if (!label.NullOrEmpty() && !tooltip.NullOrEmpty() && label != tooltip)
                        text = $"{label}: {tooltip}";
                    else
                        text = !label.NullOrEmpty() ? label : tooltip;
                    prefix = "";
                }
                else
                {
                    text   = !label.NullOrEmpty() ? label : tooltip;
                    prefix = "[Notification]";
                }

                if (text.NullOrEmpty()) return;

                // Track which quests were referenced today via their letters
                if (item is Letter trackedLetter)
                {
                    try
                    {
                        var q = Traverse.Create(trackedLetter).Field("quest").GetValue<Quest>();
                        RecordMentionedQuest(q);
                    }
                    catch { }
                }

                if (label != null && (
                    label.IndexOf("role deactivated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("role activated",   StringComparison.OrdinalIgnoreCase) >= 0)) return;

                bool isOpportunity = item.GetType().Name == "RitualOpportunityLetter"
                    || (item is Letter lo && lo.def?.defName == "NeutralEvent"
                        && label != null && label.IndexOf("opportunity", StringComparison.OrdinalIgnoreCase) >= 0);
                if (isOpportunity) text = $"{label}: {FirstSentence(tooltip)}";

                AppendEvent(Find.TickManager.TicksAbs, prefix.NullOrEmpty() ? text : $"{prefix} {text}");

                try
                {
                    if (item is Letter letter)         IntroduceFromTargets(letter.lookTargets);
                    else if (item is Message archMsg)  IntroduceFromTargets(archMsg.lookTargets);
                }
                catch { }
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Skipped archive item ({item?.GetType().Name}): {e.Message}");
            }
        }

        internal static bool IsColonyMember(Pawn p)
        {
            if (p == null) return false;
            return p.IsFreeColonist
                || p.IsPrisonerOfColony
                || p.IsSlaveOfColony
                || (p.Faction == Faction.OfPlayer && (p.RaceProps?.Animal ?? false));
        }

        public void CaptureLogEntry(LogEntry entry)
        {
            if (!_initialized || entry == null) return;
            try
            {
                string initiatorTag = "";
                string recipientTag = "";
                Pawn   logInitiator = null;
                Pawn   logRecipient = null;

                if (entry is PlayLogEntry_Interaction)
                {
                    var interactionDef = _interactionDefField?.GetValue(entry) as Def;
                    if (interactionDef != null && _blockedInteractions.Contains(interactionDef.defName)) return;

                    logInitiator = _initiatorField?.GetValue(entry) as Pawn;
                    logRecipient = _recipientField?.GetValue(entry) as Pawn;
                    bool colonistInvolved = IsColonyMember(logInitiator) || IsColonyMember(logRecipient);
                    if (!colonistInvolved) return;
                    initiatorTag = IntroduceTag(logInitiator);
                    recipientTag = IntroduceTag(logRecipient);
                }

                string text = StripTags(FormatLogEntry(entry));
                if (text.NullOrEmpty()) return;

                if (logInitiator?.LabelShort != null) text = InjectAfterFirst(text, logInitiator.LabelShort, initiatorTag);
                if (logRecipient?.LabelShort != null) text = InjectAfterFirst(text, logRecipient.LabelShort, recipientTag);

                long absTick = Find.TickManager.TicksAbs;
                try { if (_logEntryTicksAbsField != null) absTick = (long)(int)_logEntryTicksAbsField.GetValue(entry); } catch { }

                AppendEvent(absTick, text.CapitalizeFirst());
            }
            catch { }
        }

        private static string FormatLogEntry(LogEntry entry)
        {
            if (entry == null) return null;
            if (entry is PlayLogEntry_Interaction)
            {
                var initiator = _initiatorField?.GetValue(entry) as Thing;
                return initiator != null ? entry.ToGameStringFromPOV(initiator) : null;
            }
            return entry.ToGameStringFromPOV(null);
        }

        // ── Utility ───────────────────────────────────────────────────────────

        private static readonly Regex _richTextTag = new Regex(@"<[^>]+>",                 RegexOptions.Compiled);
        private static readonly Regex _grammarTag  = new Regex(@"\(\*[^)]+\)|\(\/[^)]+\)", RegexOptions.Compiled);
        internal static string StripTags(string s)
        {
            if (s == null) return null;
            s = _richTextTag.Replace(s, "");
            s = _grammarTag.Replace(s, "");
            return s;
        }

        private static string FirstSentence(string s)
        {
            if (s.NullOrEmpty()) return s;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '.' || c == '!' || c == '?')
                    if (i + 1 >= s.Length || s[i + 1] == ' ' || s[i + 1] == '\n')
                        return s.Substring(0, i + 1);
            }
            return s;
        }
    }
}
