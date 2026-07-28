using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Firefly
{
    internal struct CombatEvent
    {
        public string Initiator;
        public string InitiatorId;         // ThingID — for identity-based grouping
        public string Target;
        public string TargetId;            // ThingID — distinguishes same-named animals
        public bool   ReachedTarget;       // bullet/blow reached the intended target
        public bool   Deflected;           // resolved at capture time from Entry
        public bool   DidDamage;           // resolved at capture time from Entry
        public string CoverHit;            // thing physically struck when missing (null = clean miss)
        public string Weapon;              // weapon label (null = unarmed)
        public bool   InitiatorIsColonist; // used to normalise fight pair (colonist first)
        public string BattleId;            // Battle.GetUniqueLoadID() — for new-fight detection
        public long   Tick;                // TicksAbs at capture time — used to timestamp the combat-started event
    }

    internal struct HazardEvent
    {
        public string Victim;
        public string HazardLabel;         // human-readable label derived from ruleDef.defName
        public long   Tick;                // TicksAbs at capture time — used to timestamp the hazard-started event
        public bool   DidDamage;           // resolved at capture time from Entry
    }

    public class ColonyLedger
    {
        // The ledger belongs to one game. Null when no game is loaded, which makes every
        // capture call from a Harmony patch a safe no-op via `ColonyLedger.Current?.`.
        // Game.GetComponent<T>() is a linear scan, and the capture patches hit this on every
        // battle-log, play-log, message and archive entry — so it is cached per Game instance.
        private static Game _cachedGame;
        private static ColonyLedger _cachedLedger;

        public static ColonyLedger Current
        {
            get
            {
                Game game = Verse.Current.Game;
                if (game == null)
                {
                    _cachedGame = null;
                    _cachedLedger = null;
                    return null;
                }
                if (!ReferenceEquals(game, _cachedGame))
                {
                    _cachedGame = game;
                    _cachedLedger = game.GetComponent<FireflyGameComponent>()?.Ledger;
                }
                return _cachedLedger;
            }
        }

        private int _recordingDay;
        private bool _initialized = false;
        public int RecordingDay => _recordingDay;

        // Battle IDs we've already emitted a [Combat started] event for. Bounded — one entry per
        // colonist/enemy pair would otherwise grow all playthrough and ride along in every save.
        private const int MaxAnnouncedBattles = 4000;
        private HashSet<string> _announcedBattles = new HashSet<string>();
        private Queue<string> _announcedBattleOrder = new Queue<string>();
        // (victim, hazardLabel) pairs we've announced this day — cleared at midnight so new fires re-announce
        private HashSet<(string, string)> _announcedHazards = new HashSet<(string, string)>();

        // Yesterday's baselines — saved inside the .rws so rewinding a save rewinds these too
        private Dictionary<string, PawnHealthSnapshot> _prevDayHealth = new Dictionary<string, PawnHealthSnapshot>();
        private Dictionary<string, string> _prevDayRelations  = new Dictionary<string, string>();
        private Dictionary<string, string> _prevDaySkills     = new Dictionary<string, string>();

        // Combat events buffered until next drain (damage fields filled synchronously within the same tick)
        private readonly List<CombatEvent> _capturedBattleEvents = new List<CombatEvent>();
        private readonly List<HazardEvent> _capturedHazardEvents = new List<HazardEvent>();
        // Structured downed/killed outcomes — drained alongside combat events for simple summaries.
        // SubjectId and Tick exist so an outcome attaches to the fight it actually belongs to
        // rather than to any later fight by the same colonist name.
        private readonly List<(string Subject, string SubjectId, string Outcome, string Initiator, string Cause, long Tick)> _capturedOutcomes
            = new List<(string, string, string, string, string, long)>();

        public void CaptureBattleEvent(string initiator, string initiatorId, string target, string targetId, bool reachedTarget, string weapon, string coverHit, bool initiatorIsColonist, string battleId, LogEntry_DamageResult entry, Pawn initiatorPawn = null, Pawn targetPawn = null)
        {
            if (!_initialized) return;
            bool didDamage = false;
            bool deflected = false;
            if (entry != null)
            {
                try
                {
                    if (_deflectedField != null) deflected = (bool)_deflectedField.GetValue(entry);
                    didDamage = HasDamagedParts(entry);
                }
                catch { }
            }
            long tick = Find.TickManager.TicksAbs;
            var ev = new CombatEvent
            {
                Initiator            = initiator   ?? "?",
                InitiatorId          = initiatorId ?? initiator ?? "?",
                Target               = target      ?? "?",
                TargetId             = targetId    ?? target    ?? "?",
                ReachedTarget        = reachedTarget,
                Deflected            = deflected,
                DidDamage            = didDamage,
                Weapon               = weapon,
                CoverHit             = coverHit,
                InitiatorIsColonist  = initiatorIsColonist,
                BattleId             = battleId    ?? "",
                Tick                 = tick,
            };
            lock (_capturedBattleEvents) _capturedBattleEvents.Add(ev);

            // Emit one [Combat started] per unique colonist–enemy pair
            string colonistId = initiatorIsColonist ? initiatorId : targetId;
            string enemyId    = initiatorIsColonist ? targetId    : initiatorId;
            string pairKey    = !colonistId.NullOrEmpty() && !enemyId.NullOrEmpty() ? $"{colonistId}:{enemyId}" : null;
            if (!pairKey.NullOrEmpty() && TryAnnounceBattle(pairKey))
            {
                string initiatorTag = IntroduceTag(initiatorPawn);
                string targetTag    = IntroduceTag(targetPawn);
                string col      = initiatorIsColonist ? (initiator ?? "?") : (target   ?? "?");
                string other    = initiatorIsColonist ? (target    ?? "?") : (initiator ?? "?");
                string colTag   = initiatorIsColonist ? initiatorTag : targetTag;
                string otherTag = initiatorIsColonist ? targetTag    : initiatorTag;
                AppendEvent(tick, $"[Combat started] {col}{colTag} vs {other}{otherTag}");
            }
        }

        public void CaptureStateChange(string targetName, string text)
        {
            if (!_initialized || targetName.NullOrEmpty()) return;
            AppendEvent(Find.TickManager.TicksAbs, text + ".");
        }

        public void CaptureOutcome(string subject, string subjectId, string outcome, string initiator, string cause)
        {
            if (!_initialized || subject.NullOrEmpty()) return;
            long tick = 0L;
            try { tick = Find.TickManager.TicksAbs; } catch { }
            lock (_capturedOutcomes)
                _capturedOutcomes.Add((subject, subjectId ?? "", outcome ?? "", initiator ?? "", cause ?? "", tick));
        }

        private bool TryAnnounceBattle(string pairKey)
        {
            if (!_announcedBattles.Add(pairKey)) return false;
            _announcedBattleOrder.Enqueue(pairKey);
            while (_announcedBattleOrder.Count > MaxAnnouncedBattles)
                _announcedBattles.Remove(_announcedBattleOrder.Dequeue());
            return true;
        }

        private static readonly FieldInfo _deflectedField =
            AccessTools.Field(typeof(LogEntry_DamageResult), "deflected");
        private static readonly FieldInfo _damagedPartsField =
            AccessTools.Field(typeof(LogEntry_DamageResult), "damagedParts");

        private static bool HasDamagedParts(LogEntry_DamageResult entry)
        {
            var parts = _damagedPartsField?.GetValue(entry) as List<BodyPartRecord>;
            return parts != null && parts.Count > 0;
        }

        public void CaptureMessage(string text)
        {
            if (!_initialized || text.NullOrEmpty()) return;
            AppendEvent(Find.TickManager.TicksAbs, StripTags(text));
        }

        public void CaptureHazardEvent(string victim, string hazardLabel, LogEntry_DamageResult entry, Pawn victimPawn = null)
        {
            if (!_initialized) return;
            bool didDamage = false;
            if (entry != null)
            {
                try { didDamage = HasDamagedParts(entry); }
                catch { }
            }
            long tick = Find.TickManager.TicksAbs;
            var ev = new HazardEvent { Victim = victim ?? "?", HazardLabel = hazardLabel ?? "unknown hazard", Tick = tick, DidDamage = didDamage };
            lock (_capturedHazardEvents) _capturedHazardEvents.Add(ev);

            // Emit start event immediately the first time this victim takes this hazard
            string victimTag = IntroduceTag(victimPawn);
            var key = (ev.Victim, ev.HazardLabel);
            if (_announcedHazards.Add(key))
                AppendEvent(tick, $"[{ev.Victim}{victimTag} started taking damage from {ev.HazardLabel}]");
        }

        // Caller provides the fully-formatted string including prefix
        public void CaptureDecision(string formattedText)
        {
            if (!_initialized || formattedText.NullOrEmpty()) return;
            AppendEvent(Find.TickManager.TicksAbs, StripTags(formattedText));
        }

        private static readonly FieldInfo _initiatorField =
            AccessTools.Field(typeof(PlayLogEntry_Interaction), "initiator");
        private static readonly FieldInfo _recipientField =
            AccessTools.Field(typeof(PlayLogEntry_Interaction), "recipient");

        public void Record(Map map, int hourOfDay)
        {
            if (!_initialized)
            {
                _initialized = true;
                lock (_capturedBattleEvents) _capturedBattleEvents.Clear();
                lock (_capturedHazardEvents) _capturedHazardEvents.Clear();
                lock (_capturedOutcomes)     _capturedOutcomes.Clear();
                Log.Message("[Firefly] Ledger initialized — skipping historical events.");
            }

            _recordingDay = GenDate.DaysPassed;

            long snapshotTick = Find.TickManager.TicksAbs;
            float lon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;

            EnsureFileHeaders(map, lon, snapshotTick);

            // Weather and colonist activity snapshots — written every 3 hours
            string weather = map.weatherManager?.curWeather?.LabelCap ?? "Unknown";
            AppendEvent(snapshotTick, $"Weather — {weather}");

            var colonists = map.mapPawns?.FreeColonists?.ToList();
            if (colonists != null)
            {
                foreach (var p in colonists)
                {
                    if (p == null) continue;
                    string activity = p.jobs?.curDriver?.GetReport()?.CapitalizeFirst() ?? "idle";
                    var carried = p.carryTracker?.CarriedThing;
                    if (carried != null)
                        activity += $" (carrying {StripTags(carried.Label)})";
                    string location = GetPawnLocation(p);
                    int mood = Mathf.RoundToInt((p.needs?.mood?.CurLevel ?? 0.5f) * 100f);
                    string introTag = IntroduceTag(p);
                    AppendEvent(snapshotTick, $"{p.LabelShort ?? "?"}{introTag} — {activity} ({location}, mood {mood}%)");
                }
            }

            Log.Message($"[Firefly] Ledger recorded: Hour {hourOfDay:D2}:00, Day {_recordingDay}");
        }

        // Called at midnight before sending to LLM — captures health + relations once per day
        public string BuildComparisonSection(Map map)
        {
            var colonists = map.mapPawns?.FreeColonists?.ToList();
            if (colonists == null || colonists.Count == 0) return "";

            var sb = new StringBuilder();

            // --- Health ---
            var currentHealth = new Dictionary<string, PawnHealthSnapshot>();
            sb.AppendLine("=== COLONIST HEALTH ===");
            foreach (var p in colonists)
            {
                if (p == null) continue;
                string name = p.LabelShort ?? "?";
                string id   = p.ThingID ?? name;
                var snap = TakePawnHealthSnapshot(p);
                currentHealth[id] = snap;
                _prevDayHealth.TryGetValue(id, out PawnHealthSnapshot prev);

                // Overall line: health % + optional delta + optional bleeding
                string overallLine = $"Overall: {snap.HealthPct}%";
                if (prev != null && prev.HealthPct != snap.HealthPct)
                {
                    int hDelta = snap.HealthPct - prev.HealthPct;
                    overallLine += $" ({(hDelta < 0 ? "decreased" : "increased")} by {Math.Abs(hDelta)}% today)";
                }
                if (snap.BleedRatePct > 0 && snap.HoursUntilDeath > 0f)
                    overallLine += $" — {snap.HoursUntilDeath:F1}h until death";

                if (p.Dead)
                    overallLine += " — Dead";
                else if (p.Downed)
                    overallLine += " — Downed";
                else if (p.InMentalState)
                    overallLine += $" — Mental break ({p.MentalStateDef?.label ?? "unknown"})";

                string conditions = RenderHealthConditions(snap, prev);
                if (conditions.NullOrEmpty() && snap.BleedRatePct == 0 && snap.HoursUntilDeath == 0f)
                    overallLine += " — Healthy";

                sb.AppendLine($"  {name} health overview:");
                sb.AppendLine($"    - {overallLine}");
                if (!conditions.NullOrEmpty())
                    sb.AppendLine($"    - {conditions}");
            }
            _prevDayHealth = currentHealth;

            // --- Relations ---
            var currentRelations = GetRelationSnapshot(map, colonists);
            string relationChanges = BuildRelationChanges(currentRelations);
            if (!relationChanges.NullOrEmpty())
            {
                sb.AppendLine();
                sb.Append(relationChanges);
            }
            _prevDayRelations = currentRelations;

            // --- Skills ---
            var currentSkills = GetSkillSnapshot(colonists);
            string skillChanges = BuildSkillChanges(currentSkills);
            if (!skillChanges.NullOrEmpty())
            {
                sb.AppendLine();
                sb.Append(skillChanges);
            }
            _prevDaySkills = currentSkills;

            return sb.ToString();
        }

        // Snapshot format: "NameA->NameB|DirectRel1,DirectRel2|TotalOpinion|ThoughtLabel1:val1;ThoughtLabel2:val2"
        // Key is "ThingIDA->ThingIDB" to survive duplicate display names.
        private static Dictionary<string, string> GetRelationSnapshot(Map map, List<Pawn> colonists)
        {
            var snapshot = new Dictionary<string, string>();
            foreach (var pawnA in colonists)
            {
                if (pawnA == null) continue;
                string idA   = pawnA.ThingID   ?? pawnA.LabelShort ?? "?";
                string nameA = pawnA.LabelShort ?? "?";

                // Collect every pawn this colonist has any tracked link with
                var related = new HashSet<Pawn>();
                if (pawnA.relations?.DirectRelations != null)
                    foreach (var rel in pawnA.relations.DirectRelations)
                        if (rel.otherPawn != null) related.Add(rel.otherPawn);

                var socialMems = pawnA.needs?.mood?.thoughts?.memories?.Memories
                    ?.OfType<Thought_MemorySocial>()
                    .Where(t => t.otherPawn != null)
                    .ToList();
                if (socialMems != null)
                    foreach (var t in socialMems) related.Add(t.otherPawn);

                foreach (var pawnB in related)
                {
                    if (pawnB == null) continue;
                    string idB   = pawnB.ThingID   ?? pawnB.LabelShort ?? "?";
                    string nameB = pawnB.LabelShort ?? "?";
                    try
                    {
                        var directRels = pawnA.relations?.DirectRelations
                            ?.Where(r => r.otherPawn == pawnB)
                            .Select(r => r.def?.label ?? "?")
                            .ToList() ?? new List<string>();

                        int opinion = pawnA.relations?.OpinionOf(pawnB) ?? 0;

                        var thoughts = pawnA.needs?.mood?.thoughts?.memories?.Memories
                            ?.OfType<Thought_MemorySocial>()
                            .Where(t => t.otherPawn == pawnB && t.OpinionOffset() != 0f)
                            .Select(t => $"{t.LabelCap}:{Mathf.RoundToInt(t.OpinionOffset()):+#;-#;0}")
                            .ToList() ?? new List<string>();

                        string relStr     = string.Join(",", directRels);
                        string thoughtStr = string.Join(";", thoughts);
                        snapshot[$"{idA}->{idB}"] = $"{nameA}->{nameB}|{relStr}|{opinion}|{thoughtStr}";
                    }
                    catch { }
                }
            }
            return snapshot;
        }

        private string BuildRelationChanges(Dictionary<string, string> current)
        {
            if (_prevDayRelations.Count == 0) return "";

            var byPawn = new Dictionary<string, List<string>>();

            // Check all current pairs for changes vs yesterday
            foreach (var kvp in current)
            {
                _prevDayRelations.TryGetValue(kvp.Key, out string prevValue);

                ParseRelEntry(kvp.Value,   out string nameA, out string nameB, out string curRels,  out int curOpinion,  out string curThoughts);
                ParseRelEntry(prevValue ?? "->||", out _,    out _,            out string prevRels, out int prevOpinion, out string prevThoughts);

                var changes = new List<string>();

                // Named relation changes
                var curRelList  = curRels.Split(',').Where(r => !r.NullOrEmpty()).ToList();
                var prevRelList = prevRels.Split(',').Where(r => !r.NullOrEmpty()).ToList();
                foreach (var r in curRelList.Except(prevRelList))  changes.Add($"new relation with {nameB}: {r}");
                foreach (var r in prevRelList.Except(curRelList))  changes.Add($"lost relation with {nameB}: {r}");

                // Opinion total change (threshold 10)
                int delta = curOpinion - prevOpinion;
                if (Math.Abs(delta) >= 10)
                {
                    string dir = delta > 0 ? "improved" : "worsened";
                    changes.Add($"opinion of {nameB} {dir}: {prevOpinion:+#;-#;0} → {curOpinion:+#;-#;0}");
                }

                // Individual thought changes
                var curThoughtSet  = new HashSet<string>(curThoughts.Split(';').Where(t => !t.NullOrEmpty()));
                var prevThoughtSet = new HashSet<string>(prevThoughts.Split(';').Where(t => !t.NullOrEmpty()));
                foreach (var t in curThoughtSet.Except(prevThoughtSet))  changes.Add($"new memory about {nameB}: {t}");
                foreach (var t in prevThoughtSet.Except(curThoughtSet))  changes.Add($"memory about {nameB} expired: {t}");

                if (changes.Any())
                {
                    if (!byPawn.ContainsKey(nameA)) byPawn[nameA] = new List<string>();
                    byPawn[nameA].AddRange(changes);
                }
            }

            // Relationships that existed yesterday but are entirely gone today
            foreach (var kvp in _prevDayRelations)
            {
                if (current.ContainsKey(kvp.Key)) continue;
                ParseRelEntry(kvp.Value, out string nameA, out string nameB, out string prevRels, out _, out _);
                var prevRelList = prevRels.Split(',').Where(r => !r.NullOrEmpty()).ToList();
                foreach (var r in prevRelList)
                {
                    if (!byPawn.ContainsKey(nameA)) byPawn[nameA] = new List<string>();
                    byPawn[nameA].Add($"lost relation with {nameB}: {r}");
                }
            }

            if (!byPawn.Any()) return "";

            var sb = new StringBuilder();
            sb.AppendLine("=== RELATIONSHIP CHANGES ===");
            foreach (var kvp in byPawn)
            {
                sb.AppendLine($"  {kvp.Key}:");
                foreach (var change in kvp.Value)
                    sb.AppendLine($"    {change}");
            }
            return sb.ToString();
        }

        private static Dictionary<string, string> GetSkillSnapshot(List<Pawn> colonists)
        {
            var snapshot = new Dictionary<string, string>();
            foreach (var p in colonists)
            {
                if (p?.skills == null) continue;
                string id   = p.ThingID ?? p.LabelShort ?? "?";
                string name = p.LabelShort ?? "?";
                var parts = p.skills.skills
                    .Where(s => !s.TotallyDisabled)
                    .Select(s => $"{s.def.LabelCap}:{s.Level}:{s.passion}");
                snapshot[id] = $"{name}\t{string.Join(";", parts)}";
            }
            return snapshot;
        }

        private string BuildSkillChanges(Dictionary<string, string> current)
        {
            if (_prevDaySkills.Count == 0) return "";

            var lines = new List<string>();
            foreach (var kvp in current)
            {
                if (!_prevDaySkills.TryGetValue(kvp.Key, out string prevValue)) continue;

                int tab = kvp.Value.IndexOf('\t');
                string displayName = tab >= 0 ? kvp.Value.Substring(0, tab) : kvp.Key;
                string curRaw      = tab >= 0 ? kvp.Value.Substring(tab + 1) : kvp.Value;
                int ptab = prevValue.IndexOf('\t');
                string prevRaw = ptab >= 0 ? prevValue.Substring(ptab + 1) : prevValue;

                var curSkills  = ParseSkillSnapshot(curRaw);
                var prevSkills = ParseSkillSnapshot(prevRaw);

                var pawnChanges = new List<string>();
                foreach (var skill in curSkills)
                {
                    if (!prevSkills.TryGetValue(skill.Key, out var prev)) continue;
                    int    curLevel   = skill.Value.Level;
                    int    prevLevel  = prev.Level;
                    string curPassion = skill.Value.Passion;
                    string prevPassion = prev.Passion;

                    if (curLevel != prevLevel)
                        pawnChanges.Add($"{skill.Key} {(curLevel > prevLevel ? "levelled up" : "decreased")} {prevLevel} → {curLevel}. Went from '{SkillLevelLabel(prevLevel)}' to '{SkillLevelLabel(curLevel)}'.");
                    if (curPassion != prevPassion)
                        pawnChanges.Add($"{skill.Key} passion changed: {prevPassion} → {curPassion}");
                }

                if (pawnChanges.Any())
                    lines.Add($"  {displayName}: {string.Join(", ", pawnChanges)}");
            }

            if (!lines.Any()) return "";

            var sb = new StringBuilder();
            sb.AppendLine("=== SKILL CHANGES ===");
            foreach (var line in lines) sb.AppendLine(line);
            return sb.ToString();
        }

        private static readonly string[] _skillLabels = {
            "Barely heard of it", "Utter beginner", "Beginner", "Basic familiarity", "Some familiarity",
            "Significant familiarity", "Capable amateur", "Weak professional", "Employable professional",
            "Solid professional", "Skilled professional", "Very skilled professional", "Expert",
            "Strong expert", "Master", "Strong master", "Region-known master", "Region-leading master",
            "Planet-known master", "Planet-leading master", "Legendary master"
        };

        private static string SkillLevelLabel(int level) =>
            level >= 0 && level < _skillLabels.Length ? _skillLabels[level] : level.ToString();

        private static Dictionary<string, (int Level, string Passion)> ParseSkillSnapshot(string value)
        {
            var result = new Dictionary<string, (int, string)>();
            if (value.NullOrEmpty()) return result;
            foreach (var entry in value.Split(';'))
            {
                var parts = entry.Split(':');
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[1], out int level)) continue;
                result[parts[0]] = (level, parts[2]);
            }
            return result;
        }

        private static void ParseRelEntry(string value, out string nameA, out string nameB, out string relations, out int opinion, out string thoughts)
        {
            // Format: "NameA->NameB|rels|opinion|thoughts"
            var parts = value?.Split('|') ?? new string[0];
            string names = parts.Length > 0 ? parts[0] : "->";
            int arrow = names.IndexOf("->");
            nameA     = arrow >= 0 ? names.Substring(0, arrow) : names;
            nameB     = arrow >= 0 ? names.Substring(arrow + 2) : "";
            relations = parts.Length > 1 ? parts[1] : "";
            opinion   = parts.Length > 2 && int.TryParse(parts[2], out int o) ? o : 0;
            thoughts  = parts.Length > 3 ? parts[3] : "";
        }

        // One-time migration for colonies saved before comparisons moved into the .rws file.
        private void TryLoadLegacyComparisons(string filePath)
        {
            if (_prevDayHealth.Count > 0 || _prevDayRelations.Count > 0 || _prevDaySkills.Count > 0) return;
            try
            {
                if (!File.Exists(filePath)) return;
                string section = "";
                foreach (var line in File.ReadAllLines(filePath, Encoding.UTF8))
                {
                    if (line == "[health]")    { section = "health";    continue; }
                    if (line == "[relations]") { section = "relations"; continue; }
                    if (line == "[skills]")    { section = "skills";    continue; }
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string key = line.Substring(0, tab);
                    string val = line.Substring(tab + 1);
                    switch (section)
                    {
                        case "health":    _prevDayHealth[key]    = PawnHealthSnapshot.Deserialize(val); break;
                        case "relations": _prevDayRelations[key] = val; break;
                        case "skills":    _prevDaySkills[key]    = val; break;
                    }
                }
                Log.Message($"[Firefly] Migrated legacy comparisons ({_prevDayHealth.Count} health, {_prevDayRelations.Count} relation, {_prevDaySkills.Count} skill entries).");
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Failed to migrate legacy comparisons: {e.Message}");
            }
        }

        // ── Save/load ───────────────────────────────────────────────────────────
        // Everything here is in-flight working state. The narrative itself lives in text
        // files on disk; only the buffers that would otherwise be lost on quit ride along
        // inside the save, which is what makes the old pending_events.txt unnecessary.
        private const char FieldSep = '\u0001';

        public void ExposeData()
        {
            bool saving = Scribe.mode == LoadSaveMode.Saving;

            var pending   = saving ? EncodePendingEvents() : null;
            var health    = saving ? _prevDayHealth.ToDictionary(kv => kv.Key, kv => kv.Value.Serialize()) : null;
            var battles   = saving ? _announcedBattleOrder.ToList() : null;
            var hazards   = saving ? _announcedHazards.Select(h => $"{h.Item1}{FieldSep}{h.Item2}").ToList() : null;
            var pawnIds   = saving ? _trackedPawnIds.ToList() : null;
            var pawnLines = saving ? _trackedPawnLines.Select(p => $"{p.Name}{FieldSep}{p.Descriptor}").ToList() : null;

            Scribe_Values.Look(ref _recordingDay, "recordingDay", 0);
            Scribe_Values.Look(ref _initialized, "initialized", false);
            Scribe_Values.Look(ref _timelineHeaderWritten, "timelineHeaderWritten", false);
            Scribe_Collections.Look(ref pending,            "pendingEvents",     LookMode.Value);
            Scribe_Collections.Look(ref health,             "prevDayHealth",     LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _prevDayRelations,  "prevDayRelations",  LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _prevDaySkills,     "prevDaySkills",     LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref battles,            "announcedBattles",  LookMode.Value);
            Scribe_Collections.Look(ref hazards,            "announcedHazards",  LookMode.Value);
            Scribe_Collections.Look(ref pawnIds,            "trackedPawnIds",    LookMode.Value);
            Scribe_Collections.Look(ref pawnLines,          "trackedPawnLines",  LookMode.Value);

            if (Scribe.mode != LoadSaveMode.LoadingVars) return;

            if (_prevDayRelations == null) _prevDayRelations = new Dictionary<string, string>();
            if (_prevDaySkills    == null) _prevDaySkills    = new Dictionary<string, string>();

            _prevDayHealth = new Dictionary<string, PawnHealthSnapshot>();
            if (health != null)
                foreach (var kv in health)
                    _prevDayHealth[kv.Key] = PawnHealthSnapshot.Deserialize(kv.Value);

            _announcedBattles = new HashSet<string>();
            _announcedBattleOrder = new Queue<string>();
            if (battles != null)
                foreach (var b in battles)
                    if (!b.NullOrEmpty()) TryAnnounceBattle(b);

            _announcedHazards = new HashSet<(string, string)>();
            if (hazards != null)
                foreach (var h in hazards)
                {
                    var p = h.Split(FieldSep);
                    if (p.Length == 2) _announcedHazards.Add((p[0], p[1]));
                }

            _trackedPawnIds.Clear();
            if (pawnIds != null)
                foreach (var id in pawnIds) _trackedPawnIds.Add(id);

            _trackedPawnLines.Clear();
            if (pawnLines != null)
                foreach (var l in pawnLines)
                {
                    var p = l.Split(FieldSep);
                    if (p.Length == 2) _trackedPawnLines.Add((p[0], p[1]));
                }

            DecodePendingEvents(pending);
        }

        // FieldSep rather than '|' — a pawn nickname or weapon label containing a pipe would
        // otherwise shift every field after it and corrupt the record on load.
        private List<string> EncodePendingEvents()
        {
            var lines = new List<string>();
            lock (_capturedBattleEvents)
                foreach (var e in _capturedBattleEvents)
                    lines.Add(string.Join(FieldSep.ToString(), "C", e.Initiator, e.InitiatorId, e.Target, e.TargetId,
                        e.ReachedTarget.ToString(), e.Deflected.ToString(), e.DidDamage.ToString(),
                        e.CoverHit ?? "", e.Weapon ?? "", e.InitiatorIsColonist.ToString(), e.BattleId, e.Tick.ToString()));
            lock (_capturedHazardEvents)
                foreach (var e in _capturedHazardEvents)
                    lines.Add(string.Join(FieldSep.ToString(), "H", e.Victim, e.HazardLabel, e.Tick.ToString(), e.DidDamage.ToString()));
            lock (_capturedOutcomes)
                foreach (var (subject, subjectId, outcome, initiator, cause, tick) in _capturedOutcomes)
                    lines.Add(string.Join(FieldSep.ToString(), "O", subject, subjectId, outcome, initiator, cause, tick.ToString()));
            return lines;
        }

        private void DecodePendingEvents(List<string> lines)
        {
            _capturedBattleEvents.Clear();
            _capturedHazardEvents.Clear();
            _capturedOutcomes.Clear();
            if (lines == null) return;

            int skipped = 0;
            foreach (var line in lines)
            {
                if (line.NullOrEmpty()) continue;
                // Saves written before the separator change used '|'.
                var p = line.Split(FieldSep);
                if (p.Length == 1) p = line.Split('|');
                try
                {
                    if (p[0] == "C" && p.Length >= 13 && long.TryParse(p[12], out long cTick))
                    {
                        _capturedBattleEvents.Add(new CombatEvent
                        {
                            Initiator           = p[1],
                            InitiatorId         = p[2],
                            Target              = p[3],
                            TargetId            = p[4],
                            ReachedTarget       = p[5] == "True",
                            Deflected           = p[6] == "True",
                            DidDamage           = p[7] == "True",
                            CoverHit            = p[8].NullOrEmpty() ? null : p[8],
                            Weapon              = p[9].NullOrEmpty() ? null : p[9],
                            InitiatorIsColonist = p[10] == "True",
                            BattleId            = p[11],
                            Tick                = cTick,
                        });
                    }
                    else if (p[0] == "H" && p.Length >= 5 && long.TryParse(p[3], out long hTick))
                    {
                        _capturedHazardEvents.Add(new HazardEvent
                        {
                            Victim      = p[1],
                            HazardLabel = p[2],
                            Tick        = hTick,
                            DidDamage   = p[4] == "True",
                        });
                    }
                    else if (p[0] == "O" && p.Length >= 7 && long.TryParse(p[6], out long oTick))
                    {
                        _capturedOutcomes.Add((p[1], p[2], p[3], p[4], p[5], oTick));
                    }
                    else if (p[0] == "O" && p.Length == 5)
                    {
                        // Legacy outcome record: no subject id, no tick.
                        _capturedOutcomes.Add((p[1], "", p[2], p[3], p[4], 0L));
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch { skipped++; }
            }

            if (skipped > 0) Log.Warning($"[Firefly] Skipped {skipped} unreadable in-flight event record(s) on load.");

            int total = _capturedBattleEvents.Count + _capturedHazardEvents.Count + _capturedOutcomes.Count;
            if (total > 0) Log.Message($"[Firefly] Restored {total} in-flight events from save.");
        }

        private const  string CurrentTimelineFile = "current_timeline.txt";
        private const  string CombatEventsFile    = "current_combat_events.txt";
        private const  string HazardEventsFile    = "current_hazard_events.txt";
        private string _outputDir = null;
        private bool _timelineHeaderWritten = false;
        private readonly HashSet<string> _trackedPawnIds = new HashSet<string>();
        private readonly List<(string Name, string Descriptor)> _trackedPawnLines = new List<(string, string)>();
        private readonly StringBuilder _timelineBuffer = new StringBuilder();

        public string OutputDir => _outputDir;

        public void SetOutputDir(string dir)
        {
            _outputDir = dir;
            if (dir != null) TryLoadLegacyComparisons(Path.Combine(dir, "prev_day_comparisons.txt"));
        }

        private void EnsureFileHeaders(Map map, float lon, long tick)
        {
            if (_outputDir == null) return;
            string colony = map.info?.parent?.Label ?? "Unnamed Colony";
            string season = GenLocalDate.Season(map).ToString();
            int year = GenDate.Year(tick, lon);
            if (!_timelineHeaderWritten)
            {
                string header = $"=== DAY {_recordingDay} CHRONICLE — {colony} ===\n{season}, Year {year}\n\n=== EVENTS ===\n";
                WriteFileHeader(CurrentTimelineFile, header);
                _timelineHeaderWritten = true;

                if (GenDate.DaysPassed == 0)
                {
                    try
                    {
                        var scenario = Find.Scenario;
                        string sName = scenario?.name;
                        string sDesc = StripTags(scenario?.description);
                        if (!sName.NullOrEmpty())
                        {
                            string flat = sDesc.NullOrEmpty() ? "" : sDesc.Replace("\r\n", " ").Replace('\n', ' ').Trim();
                            string line = flat.NullOrEmpty() ? sName : $"{sName} — {flat}";
                            float hf = GenDate.HourFloat(tick, lon);
                            int h = (int)hf, m = (int)((hf % 1f) * 60f);
                            AppendRawToTimeline($"  - [{h:D2}:{m:D2}] {line}\n");
                        }
                    }
                    catch { }
                }
            }
        }

        private void WriteFileHeader(string fileName, string header)
        {
            FlushTimelineBuffer();
            try
            {
                // Prepend, never overwrite — events are appended from midnight onward but the header
                // is only written on the next 3-hourly Record, which would erase them.
                string path = Path.Combine(_outputDir, fileName);
                string existing = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "";
                File.WriteAllText(path, header + existing, Encoding.UTF8);
            }
            catch { }
        }

        public void AppendRawToTimeline(string content)
        {
            if (_outputDir == null || content.NullOrEmpty()) return;
            lock (_timelineBuffer) _timelineBuffer.Append(content);
        }

        private static string GetPawnDescriptor(Pawn pawn)
        {
            try
            {
                if (pawn.IsFreeColonist)      return "Colonist";
                if (pawn.IsSlaveOfColony)     return "Colony Slave";
                if (pawn.IsPrisonerOfColony)  return "Colony Prisoner";
                if (pawn.RaceProps?.Animal == true)
                    return pawn.Faction == Faction.OfPlayer ? "Colony Animal" : "Wild Animal";
                string factionName = pawn.Faction?.Name;
                if (factionName.NullOrEmpty()) return "No Faction";
                bool hostile = pawn.Faction.HostileTo(Faction.OfPlayer);
                return $"{factionName}, {(hostile ? "Hostile" : "Friendly")}";
            }
            catch { return "Unknown"; }
        }

        // Returns " (Descriptor)" on first appearance, "" on subsequent ones.
        public string IntroduceTag(Pawn pawn)
        {
            if (!_initialized || pawn == null) return "";
            string id = pawn.ThingID;
            if (id.NullOrEmpty()) return "";
            bool isNew;
            lock (_trackedPawnIds) { isNew = _trackedPawnIds.Add(id); }
            if (!isNew) return "";
            string name       = pawn.LabelShort ?? "?";
            string descriptor = GetPawnDescriptor(pawn);
            lock (_trackedPawnLines) { _trackedPawnLines.Add((name, descriptor)); }
            return $" ({descriptor})";
        }


        public string BuildPawnRosterSection()
        {
            lock (_trackedPawnLines)
            {
                if (_trackedPawnLines.Count == 0) return "";
                var order = new[] { "Colonist", "Colony Slave", "Colony Prisoner", "Colony Animal", "Wild Animal" };
                var groups = _trackedPawnLines
                    .GroupBy(p => p.Descriptor)
                    .OrderBy(g => { int i = System.Array.IndexOf(order, g.Key); return i >= 0 ? i : order.Length; })
                    .ThenBy(g => g.Key);
                var sb = new StringBuilder("=== CHARACTER ROSTER ===\n");
                foreach (var group in groups)
                {
                    sb.AppendLine(RosterCategoryHeader(group.Key) + ":");
                    foreach (var (name, _) in group)
                        sb.AppendLine($"  - {name}");
                }
                return sb.ToString();
            }
        }

        private static string RosterCategoryHeader(string descriptor)
        {
            switch (descriptor)
            {
                case "Colonist":        return "Colonists";
                case "Colony Slave":    return "Colony Slaves";
                case "Colony Prisoner": return "Colony Prisoners";
                case "Colony Animal":   return "Colony Animals";
                case "Wild Animal":     return "Wild Animals";
                case "No Faction":      return "No Faction";
                case "Unknown":         return "Unknown";
                default:
                    int comma = descriptor.IndexOf(", ", StringComparison.Ordinal);
                    return comma >= 0
                        ? $"{descriptor.Substring(0, comma)} ({descriptor.Substring(comma + 2)})"
                        : descriptor;
            }
        }

        private static string InjectAfterFirst(string text, string name, string tag)
        {
            if (tag.NullOrEmpty() || name.NullOrEmpty()) return text;
            int idx = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return text;
            return text.Substring(0, idx + name.Length) + tag + text.Substring(idx + name.Length);
        }

        public void DrainAndWriteSections(float lon)
        {
            var (combatSimple, _) = DrainCombatEvents();
            var hazardSummaries   = DrainHazardEvents();
            AppendSectionToFile(CombatEventsFile, "COMBAT",  combatSimple,    lon);
            AppendSectionToFile(HazardEventsFile, "HAZARDS", hazardSummaries, lon);
        }

        private void AppendEvent(long tick, string text)
        {
            if (_outputDir == null) return;
            try
            {
                float lon = Find.WorldGrid?.LongLatOf(Find.CurrentMap?.Tile ?? 0).x ?? 0f;
                int hr  = GenDate.HourInteger(tick, lon);
                int min = (int)((GenDate.HourFloat(tick, lon) % 1f) * 60f);
                string flat = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
                lock (_timelineBuffer) _timelineBuffer.Append($"  - [{hr:D2}:{min:D2}] {flat}\n");
            }
            catch { }
        }

        // Events are buffered and written in batches. A raid fires hundreds of log entries a
        // second, and an open/write/close per entry on the main thread costs real TPS.
        public void FlushTimelineBuffer()
        {
            if (_outputDir == null) return;
            string chunk;
            lock (_timelineBuffer)
            {
                if (_timelineBuffer.Length == 0) return;
                chunk = _timelineBuffer.ToString();
                _timelineBuffer.Length = 0;
            }
            try { File.AppendAllText(Path.Combine(_outputDir, CurrentTimelineFile), chunk, Encoding.UTF8); }
            catch { }
        }

        private void AppendSectionToFile(string fileName, string header, List<(long Tick, string Text)> entries, float lon)
        {
            if (_outputDir == null || entries.Count == 0) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"=== {header} ===");
                foreach (var (tick, text) in entries.OrderBy(e => e.Tick))
                {
                    int hr  = GenDate.HourInteger(tick, lon);
                    int min = (int)((GenDate.HourFloat(tick, lon) % 1f) * 60f);
                    sb.AppendLine($"  - [{hr:D2}:{min:D2}] {text}");
                }
                File.AppendAllText(Path.Combine(_outputDir, fileName), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public void Clear()
        {
            // Fire can start again next day, so hazard announcements reset.
            _announcedHazards.Clear();
            lock (_capturedOutcomes) _capturedOutcomes.Clear();
            // _announcedBattles intentionally kept — battle IDs are unique per game, so a
            // genuine new fight always gets a new ID; no need to re-announce across day boundaries

            _timelineHeaderWritten = false;
            lock (_trackedPawnIds)  _trackedPawnIds.Clear();
            lock (_trackedPawnLines) _trackedPawnLines.Clear();
            lock (_timelineBuffer)  _timelineBuffer.Length = 0;

            if (_outputDir != null)
            {
                foreach (var f in new[] { CurrentTimelineFile, CombatEventsFile, HazardEventsFile })
                    try { File.Delete(Path.Combine(_outputDir, f)); } catch { }
            }
        }

        // Cached "colony history + recent days" header — rebuilt only when a new summary or arc lands.
        private string _contextStaticCache = null;

        public void InvalidateContextCache() => _contextStaticCache = null;

        private string BuildContextStaticSection()
        {
            try
            {
                string dailyDir = Path.Combine(_outputDir, "daily records");
                if (!Directory.Exists(dailyDir)) return "";

                var sb = new StringBuilder();

                string historyPath = Path.Combine(dailyDir, "colony_history.txt");
                if (File.Exists(historyPath))
                {
                    string history = File.ReadAllText(historyPath, Encoding.UTF8);
                    if (!history.NullOrEmpty())
                    {
                        sb.AppendLine("=== COLONY HISTORY ===");
                        sb.AppendLine(history.Trim());
                        sb.AppendLine();
                    }
                }

                int lastArcDay = 0;
                string lastDayPath = Path.Combine(dailyDir, "colony_history_last_day.txt");
                if (File.Exists(lastDayPath))
                    int.TryParse(File.ReadAllText(lastDayPath, Encoding.UTF8).Trim(), out lastArcDay);

                var recentSummaries = Directory.GetFiles(dailyDir, "daily_summary_day*.txt")
                    .Select(f =>
                    {
                        string stem   = Path.GetFileNameWithoutExtension(f);
                        string dayStr = stem.Substring("daily_summary_day".Length);
                        return int.TryParse(dayStr, out int d) ? (d, f) : (-1, f);
                    })
                    .Where(x => x.Item1 > lastArcDay)
                    .OrderBy(x => x.Item1)
                    .ToList();

                if (recentSummaries.Count > 0)
                {
                    sb.AppendLine("=== RECENT DAYS ===");
                    foreach (var (d, path) in recentSummaries)
                    {
                        string text;
                        try { text = File.ReadAllText(path, Encoding.UTF8); }
                        catch { continue; }
                        if (text.NullOrEmpty()) continue;
                        sb.AppendLine($"Day {d}:");
                        sb.AppendLine(text.Trim());
                        sb.AppendLine();
                    }
                }

                return sb.ToString();
            }
            catch { return ""; }
        }

        public void WriteContextFile()
        {
            if (_outputDir == null) return;
            FlushTimelineBuffer();
            try
            {
                if (_contextStaticCache == null)
                    _contextStaticCache = BuildContextStaticSection();

                var sb = new StringBuilder(_contextStaticCache);

                string timelinePath = Path.Combine(_outputDir, CurrentTimelineFile);
                if (File.Exists(timelinePath))
                {
                    string timeline = File.ReadAllText(timelinePath, Encoding.UTF8);
                    if (!timeline.NullOrEmpty())
                    {
                        sb.AppendLine("=== TODAY ===");
                        sb.Append(timeline.TrimEnd());
                    }
                }

                File.WriteAllText(Path.Combine(_outputDir, "llm_colony_context.txt"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private static readonly Regex _richTextTag = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static string StripTags(string s) => s == null ? null : _richTextTag.Replace(s, "");

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

        private List<(long Tick, string Text)> DrainHazardEvents()
        {
            List<HazardEvent> events;
            lock (_capturedHazardEvents)
            {
                events = new List<HazardEvent>(_capturedHazardEvents);
                _capturedHazardEvents.Clear();
            }
            if (events.Count == 0) return new List<(long, string)>();

            var summaries = new List<(long Tick, string Text)>();

            foreach (var group in events.GroupBy(e => (e.Victim, e.HazardLabel)).OrderBy(g => g.Key.Victim))
            {
                string victim    = group.Key.Victim;
                string label     = group.Key.HazardLabel;
                long   firstTick = group.Min(e => e.Tick);

                int total       = group.Count();
                int damageCount = group.Count(e => e.DidDamage);
                string verb     = HazardVerb(label);
                string line     = $"{victim} was {verb} {total} time{(total == 1 ? "" : "s")}";
                if (damageCount > 0 && damageCount < total)
                    line += $" ({damageCount} did damage)";
                summaries.Add((firstTick, line + "."));
            }
            return summaries;
        }

        private static string HazardVerb(string label)
        {
            switch (label)
            {
                case "fire":               return "burned by fire";
                case "ceiling":            return "crushed by collapsing roof";
                case "trap spike":         return "hit by a spike trap";
                case "tornado":            return "struck by a tornado";
                case "power beam":         return "hit by a power beam";
                case "unnatural darkness": return "harmed by unnatural darkness";
                default:                   return $"damaged by {label}";
            }
        }

        // includeDetailed guards the per-fight prose. Only the simple summaries are consumed today,
        // and building the prose costs a full regroup plus name disambiguation on every drain.
        private (List<(long Tick, string Text)> Simple, List<string> Detailed) DrainCombatEvents(bool includeDetailed = false)
        {
            List<CombatEvent> shots;
            lock (_capturedBattleEvents)
            {
                shots = new List<CombatEvent>(_capturedBattleEvents);
                _capturedBattleEvents.Clear();
            }

            List<(string Subject, string SubjectId, string Outcome, string Initiator, string Cause, long Tick)> outcomes;
            lock (_capturedOutcomes)
            {
                outcomes = new List<(string, string, string, string, string, long)>(_capturedOutcomes);
                _capturedOutcomes.Clear();
            }

            long nowTick = 0L;
            try { nowTick = Find.TickManager.TicksAbs; } catch { }
            long keepAfter = nowTick - GenDate.TicksPerHour * 2;

            if (shots.Count == 0)
            {
                // A downed/killed record can arrive with no shots behind it (fire, a fall). Hold it
                // briefly in case its fight lands in the next drain, then drop it — the timeline
                // already carries it via CaptureStateChange.
                RebufferOutcomes(outcomes, null, keepAfter);
                return (new List<(long, string)>(), new List<string>());
            }

            var summaries = new List<string>();

            // Canonical info: always (colonistName, colonistId, otherId, otherDisplayName)
            (string ColonistName, string ColonistId, string OtherId, string OtherName) Info(CombatEvent e) =>
                e.InitiatorIsColonist
                    ? (e.Initiator, e.InitiatorId, e.TargetId,    e.Target)
                    : (e.Target,    e.TargetId,    e.InitiatorId, e.Initiator);

            if (includeDetailed)
            {
                // Group by identity: same colonist + same other ThingID = same fight
                var groups = shots.GroupBy(e => { var i = Info(e); return (i.ColonistName, i.OtherId); }).ToList();

                // Count how many distinct IDs share each (colonistName, otherName) display pair
                var nameCount = groups
                    .GroupBy(g => { var i = Info(g.First()); return (i.ColonistName, i.OtherName); })
                    .ToDictionary(g => g.Key, g => g.Count());
                var nameCounter = new Dictionary<(string, string), int>();

                foreach (var group in groups)
                {
                    var info     = Info(group.First());
                    string col   = info.ColonistName;
                    string other = info.OtherName;
                    var nameKey  = (col, other);

                    // Assign display name: no number if only one, first stays bare, rest get 2/3/...
                    string displayOther;
                    if (nameCount[nameKey] == 1)
                    {
                        displayOther = other;
                    }
                    else
                    {
                        if (!nameCounter.ContainsKey(nameKey)) nameCounter[nameKey] = 0;
                        nameCounter[nameKey]++;
                        int n = nameCounter[nameKey];
                        displayOther = n == 1 ? other : $"{other} {n}";
                    }

                    var lines = new List<string>();
                    foreach (var wGroup in group.GroupBy(e => (e.Initiator, e.Weapon.NullOrEmpty() ? "(unarmed)" : e.Weapon)))
                        lines.Add(BuildCombatProse(wGroup.Key.Initiator, wGroup.Key.Initiator == col ? displayOther : col, wGroup.Key.Item2, wGroup.ToList()));

                    summaries.Add($"{col} vs {displayOther}:\n" + string.Join("\n", lines.Select(l => "  " + l)));
                }
            }

            // Simple per-colonist summaries for LLM output
            var simple              = new List<(long Tick, string Text)>();
            var opponentsByColonist = new Dictionary<string, Dictionary<string, string>>(); // col -> {otherId -> otherName}
            var hitsReceived        = new Dictionary<string, int>();
            var firstTickByColonist = new Dictionary<string, long>();
            var lastTickByColonist  = new Dictionary<string, long>();
            var idByColonist        = new Dictionary<string, string>();

            foreach (var e in shots)
            {
                var info = Info(e);
                if (!opponentsByColonist.ContainsKey(info.ColonistName))
                    opponentsByColonist[info.ColonistName] = new Dictionary<string, string>();
                opponentsByColonist[info.ColonistName][info.OtherId] = info.OtherName;

                if (!info.ColonistId.NullOrEmpty()) idByColonist[info.ColonistName] = info.ColonistId;

                if (!firstTickByColonist.TryGetValue(info.ColonistName, out long earliest) || e.Tick < earliest)
                    firstTickByColonist[info.ColonistName] = e.Tick;
                if (!lastTickByColonist.TryGetValue(info.ColonistName, out long latest) || e.Tick > latest)
                    lastTickByColonist[info.ColonistName] = e.Tick;

                if (!e.InitiatorIsColonist && e.ReachedTarget && e.DidDamage)
                    hitsReceived[info.ColonistName] = (hitsReceived.TryGetValue(info.ColonistName, out int n) ? n : 0) + 1;
            }

            var used = new bool[outcomes.Count];

            foreach (var kvp in opponentsByColonist)
            {
                string col      = kvp.Key;
                var    oppNames = kvp.Value.Values.Distinct().ToList();
                var    sb2      = new StringBuilder();
                sb2.Append($"{col} — fought {JoinList(oppNames)}.");

                hitsReceived.TryGetValue(col, out int hits);
                long tick      = firstTickByColonist.TryGetValue(col, out long ft) ? ft : 0L;
                long lastTick  = lastTickByColonist.TryGetValue(col, out long lt) ? lt : tick;
                idByColonist.TryGetValue(col, out string colId);

                int fateIdx = FindOutcomeForFight(outcomes, used, col, colId, tick, lastTick);

                var personalParts = new List<string>();
                if (hits > 0) personalParts.Add($"took {hits} hit{(hits == 1 ? "" : "s")}");
                if (fateIdx >= 0)
                {
                    used[fateIdx] = true;
                    var fate = outcomes[fateIdx];
                    var fateParts = new List<string>();
                    if (!fate.Cause.NullOrEmpty())     fateParts.Add(fate.Cause);
                    if (!fate.Initiator.NullOrEmpty()) fateParts.Add(fate.Initiator);
                    string fateStr = fate.Outcome + (fateParts.Any() ? $" ({string.Join(" — ", fateParts)})" : "");
                    personalParts.Add(fateStr);
                }
                if (personalParts.Any()) sb2.Append($" {string.Join(", ", personalParts)}.");

                simple.Add((tick, sb2.ToString()));
            }

            RebufferOutcomes(outcomes, used, keepAfter);

            return (simple, summaries);
        }

        // An outcome belongs to a fight only if it concerns the same colonist and landed inside the
        // fight's window. Legacy records carry no tick (0) and fall back to name matching.
        private static int FindOutcomeForFight(
            List<(string Subject, string SubjectId, string Outcome, string Initiator, string Cause, long Tick)> outcomes,
            bool[] used, string colonistName, string colonistId, long firstTick, long lastTick)
        {
            long grace = GenDate.TicksPerHour;
            for (int i = 0; i < outcomes.Count; i++)
            {
                if (used[i]) continue;
                var o = outcomes[i];

                bool sameSubject = !o.SubjectId.NullOrEmpty() && !colonistId.NullOrEmpty()
                    ? o.SubjectId == colonistId
                    : o.Subject == colonistName;
                if (!sameSubject) continue;

                if (o.Tick != 0L && (o.Tick < firstTick - grace || o.Tick > lastTick + grace)) continue;
                return i;
            }
            return -1;
        }

        private void RebufferOutcomes(
            List<(string Subject, string SubjectId, string Outcome, string Initiator, string Cause, long Tick)> outcomes,
            bool[] used, long keepAfter)
        {
            var keep = new List<(string, string, string, string, string, long)>();
            for (int i = 0; i < outcomes.Count; i++)
            {
                if (used != null && used[i]) continue;
                if (outcomes[i].Tick < keepAfter) continue;
                keep.Add(outcomes[i]);
            }
            if (keep.Count == 0) return;
            lock (_capturedOutcomes) _capturedOutcomes.InsertRange(0, keep);
        }

        private static string BuildCombatProse(string initiator, string target, string weapon, List<CombatEvent> attacks)
        {
            int total       = attacks.Count;
            int hitCount    = attacks.Count(e => e.ReachedTarget);
            int missCount   = total - hitCount;
            int deflCount   = attacks.Count(e => e.ReachedTarget && e.Deflected);
            int damageCount = attacks.Count(e => e.ReachedTarget && e.DidDamage);
            int blockedCount = hitCount - deflCount - damageCount; // hit but no damage and not deflected

            string wpn = weapon == "(unarmed)" ? "unarmed" : weapon;
            var sb = new StringBuilder();
            sb.Append($"{initiator} made {total} attack{(total == 1 ? "" : "s")} with {wpn}.");

            if (missCount > 0)
            {
                var missByCover = attacks
                    .Where(e => !e.ReachedTarget)
                    .GroupBy(e => e.CoverHit.NullOrEmpty() ? "nothing" : e.CoverHit)
                    .OrderByDescending(g => g.Count())
                    .ToList();

                // Entries ending in " dodging" are rendered without "hitting" prefix
                var coverHits = missByCover.Where(g => !g.Key.EndsWith(" dodging")).ToList();
                var dodges    = missByCover.Where(g =>  g.Key.EndsWith(" dodging")).ToList();

                sb.Append($" Missed {missCount}");
                var missParts = new List<string>();
                missParts.AddRange(coverHits.Select(g => { int c = g.Count(); return $"hitting {g.Key} {c} time{(c == 1 ? "" : "s")}"; }));
                missParts.AddRange(dodges.Select(g    => { int c = g.Count(); return $"{g.Key} {c} time{(c == 1 ? "" : "s")}"; }));
                sb.Append(missParts.Count > 0 ? $", {JoinList(missParts)}." : ".");
            }

            if (hitCount > 0)
            {
                sb.Append($" {hitCount} attack{(hitCount == 1 ? "" : "s")} reached {target}");
                var stops = new List<string>();
                if (deflCount   > 0) stops.Add($"{deflCount} deflected by armor");
                if (blockedCount > 0) stops.Add($"{blockedCount} blocked");
                if (stops.Count > 0) sb.Append($", but {JoinList(stops)}");
                sb.Append(".");
                if (damageCount > 0)
                    sb.Append($" {damageCount} did damage.");
            }

            return sb.ToString();
        }

        private static string JoinList(List<string> parts)
        {
            if (parts.Count == 0) return "";
            if (parts.Count == 1) return parts[0];
            return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts.Last();
        }

        private class PawnHealthSnapshot
        {
            public int HealthPct;
            public int BleedRatePct;      // 0 if not bleeding
            public float HoursUntilDeath; // 0 if not applicable
            public List<(string Source, int Pct)> Injuries
                = new List<(string, int)>();
            public List<(string Label, int InfPct, int ImmPct, string SevLabel, bool Lethal)> Diseases
                = new List<(string, int, int, string, bool)>();
            public List<(string Label, int SevPct, string SevLabel)> Other
                = new List<(string, int, string)>();
            public List<string> Addictions = new List<string>();

            // Pipe-separated records; colon-separated fields within each record; special chars percent-encoded
            public string Serialize()
            {
                var sb = new StringBuilder();
                sb.Append(HealthPct);
                sb.Append($"|B:{BleedRatePct}:{HoursUntilDeath:F1}");
                foreach (var i in Injuries)
                    sb.Append($"|I:{Esc(i.Source)}:{i.Pct}");
                foreach (var d in Diseases)
                    sb.Append($"|D:{Esc(d.Label)}:{d.InfPct}:{d.ImmPct}:{Esc(d.SevLabel)}:{(d.Lethal ? 1 : 0)}");
                foreach (var o in Other)
                    sb.Append($"|O:{Esc(o.Label)}:{o.SevPct}:{Esc(o.SevLabel)}");
                foreach (var a in Addictions)
                    sb.Append($"|A:{Esc(a)}");
                return sb.ToString();
            }

            public static PawnHealthSnapshot Deserialize(string s)
            {
                var snap = new PawnHealthSnapshot();
                if (s.NullOrEmpty()) return snap;
                var records = s.Split('|');
                if (records.Length > 0 && int.TryParse(records[0], out int hp))
                    snap.HealthPct = hp;
                for (int idx = 1; idx < records.Length; idx++)
                {
                    var r = records[idx];
                    if (r.Length < 2) continue;
                    var f = r.Substring(2).Split(':');
                    switch (r[0])
                    {
                        case 'B':
                            if (f.Length >= 2 && int.TryParse(f[0], out int br))
                            {
                                snap.BleedRatePct = br;
                                if (float.TryParse(f[1], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float hud))
                                    snap.HoursUntilDeath = hud;
                            }
                            break;
                        case 'I':
                            if (f.Length >= 2 && int.TryParse(f[1], out int ipct))
                                snap.Injuries.Add((Unesc(f[0]), ipct));
                            break;
                        case 'D':
                            if (f.Length >= 5 && int.TryParse(f[1], out int inf) && int.TryParse(f[2], out int imm))
                                snap.Diseases.Add((Unesc(f[0]), inf, imm, Unesc(f[3]), f[4] == "1"));
                            break;
                        case 'O':
                            if (f.Length >= 3 && int.TryParse(f[1], out int spct))
                                snap.Other.Add((Unesc(f[0]), spct, Unesc(f[2])));
                            break;
                        case 'A':
                            if (f.Length >= 1) snap.Addictions.Add(Unesc(f[0]));
                            break;
                    }
                }
                return snap;
            }

            private static string Esc(string s)   => s?.Replace("%", "%25").Replace("|", "%7C").Replace(":", "%3A") ?? "";
            private static string Unesc(string s)  => s?.Replace("%3A", ":").Replace("%7C", "|").Replace("%25", "%") ?? "";
        }

        private static PawnHealthSnapshot TakePawnHealthSnapshot(Pawn p)
        {
            var snap = new PawnHealthSnapshot();
            try
            {
                var hediffSet = p.health?.hediffSet;
                if (hediffSet == null) return snap;

                snap.HealthPct = Mathf.RoundToInt((p.health?.summaryHealth?.SummaryHealthPercent ?? 1f) * 100f);

                float bleedRate = hediffSet.BleedRateTotal;
                if (bleedRate > 0.001f)
                {
                    snap.BleedRatePct = Mathf.RoundToInt(bleedRate * 100f);
                    try
                    {
                        float ticks = HealthUtility.TicksUntilDeathDueToBloodLoss(p);
                        if (ticks < float.MaxValue / 2f)
                            snap.HoursUntilDeath = ticks / GenDate.TicksPerHour;
                    }
                    catch { }
                }

                var bad = hediffSet.hediffs
                    .Where(h => h.Visible && h.def.isBad && !(h is Hediff_MissingPart))
                    .ToList();

                if (!bad.Any()) return snap;

                var injuryList   = bad.OfType<Hediff_Injury>().ToList();
                float totalSev   = injuryList.Sum(h => h.Severity);
                float healthLoss = 1f - snap.HealthPct / 100f;
                foreach (var group in injuryList.GroupBy(h => InjurySourceKey(h)).OrderBy(g => g.Key))
                {
                    int pct = totalSev > 0f
                        ? Mathf.RoundToInt(group.Sum(h => h.Severity) / totalSev * healthLoss * 100f)
                        : 0;
                    snap.Injuries.Add((group.Key, pct));
                }

                var rest = bad.Where(h => !(h is Hediff_Injury)).ToList();

                foreach (var h in rest.OfType<Hediff_Addiction>())
                    snap.Addictions.Add(h.Chemical?.LabelCap ?? h.def.LabelCap);

                foreach (var h in rest.Where(h => !(h is Hediff_Addiction)
                    && h.def.HasComp(typeof(HediffComp_Immunizable))))
                {
                    var immunComp = h.TryGetComp<HediffComp_Immunizable>();
                    snap.Diseases.Add((
                        h.def.LabelCap,
                        Mathf.RoundToInt(h.Severity * 100f),
                        Mathf.RoundToInt((immunComp?.Immunity ?? 0f) * 100f),
                        h.CurStage?.label ?? "",
                        h.def.lethalSeverity >= 0f
                    ));
                }

                foreach (var h in rest.Where(h => !(h is Hediff_Addiction)
                    && !h.def.HasComp(typeof(HediffComp_Immunizable))))
                {
                    snap.Other.Add((h.def.LabelCap, Mathf.RoundToInt(h.Severity * 100f), h.CurStage?.label ?? ""));
                }
            }
            catch { }
            return snap;
        }

        private static string RenderHealthConditions(PawnHealthSnapshot snap, PawnHealthSnapshot prev)
        {
            if (!snap.Injuries.Any() && !snap.Diseases.Any() && !snap.Other.Any() && !snap.Addictions.Any())
                return "Healthy";

            var parts = new List<string>();

            // "Injuries, timber wolf - (54%)" or "Burns, fire - (54%, increased by 10% today)"
            var prevInj = prev?.Injuries.GroupBy(i => i.Source).ToDictionary(g => g.Key, g => g.First().Pct);
            foreach (var inj in snap.Injuries)
            {
                string type   = string.Equals(inj.Source, "fire", StringComparison.OrdinalIgnoreCase) ? "Burns" : "Injuries";
                string header = inj.Source == "scar"      ? "Old scars"
                              : inj.Source.NullOrEmpty()  ? $"{type} (unknown cause)"
                              : $"{type} from {inj.Source}";
                string detail = prevInj != null && prevInj.TryGetValue(inj.Source, out int pp) && pp != inj.Pct
                    ? $"{inj.Pct}%, {(inj.Pct > pp ? "increased" : "decreased")} by {Math.Abs(inj.Pct - pp)}% today"
                    : $"{inj.Pct}%";
                parts.Add($"{header} - ({detail})");
            }

            // "Infection, minor - (infection 5%, immunity 7%)"
            var prevDis = prev?.Diseases.GroupBy(d => d.Label).ToDictionary(g => g.Key, g => g.First());
            foreach (var d in snap.Diseases)
            {
                string header = d.SevLabel.NullOrEmpty() ? d.Label : $"{d.Label}, {d.SevLabel}";
                string infStr, immStr;
                if (prevDis != null && prevDis.TryGetValue(d.Label, out var pd))
                {
                    infStr = FieldDelta("affliction", d.InfPct, d.InfPct - pd.InfPct);
                    immStr = FieldDelta("immunity",  d.ImmPct, d.ImmPct - pd.ImmPct);
                }
                else
                {
                    infStr = $"affliction {d.InfPct}%";
                    immStr = $"immunity {d.ImmPct}%";
                }
                parts.Add($"{header} - ({infStr}, {immStr})");
            }

            // "Blood loss, moderate - (37%)" or "Blood loss - (37%, decreased by 5% today)"
            var prevOth = prev?.Other.GroupBy(o => o.Label).ToDictionary(g => g.Key, g => g.First());
            foreach (var o in snap.Other)
            {
                string header = o.SevLabel.NullOrEmpty() ? o.Label : $"{o.Label}, {o.SevLabel}";
                string detail = prevOth != null && prevOth.TryGetValue(o.Label, out var po) && po.SevPct != o.SevPct
                    ? $"{o.SevPct}%, {(o.SevPct > po.SevPct ? "increased" : "decreased")} by {Math.Abs(o.SevPct - po.SevPct)}% today"
                    : $"{o.SevPct}%";
                parts.Add($"{header} - ({detail})");
            }

            foreach (var a in snap.Addictions)
                parts.Add($"Addicted to {a}");

            return string.Join("; ", parts);
        }

        private static string FieldDelta(string name, int pct, int delta) =>
            delta != 0
                ? $"{name} {pct}%, {(delta > 0 ? "increased" : "decreased")} by {Math.Abs(delta)}% today"
                : $"{name} {pct}%";

        private static string InjurySourceKey(Hediff_Injury h)
        {
            try
            {
                var def = Traverse.Create(h).Field("source").GetValue<ThingDef>();
                if (def?.label != null) return def.label;
            }
            catch { }
            if (!h.sourceLabel.NullOrEmpty()) return h.sourceLabel;
            // Fire has no weapon ThingDef — identify by hediff def instead
            if (h.def?.defName == "Burn") return "fire";
            // Permanent injuries (healed scars) lose their source info
            if (h.TryGetComp<HediffComp_GetsPermanent>()?.IsPermanent == true) return "scar";
            return "";
        }

        private static string GetPawnLocation(Pawn p)
        {
            if (!p.Spawned)
            {
                if (p.ParentHolder is Pawn_CarryTracker carrierTracker)
                    return GetPawnLocation(carrierTracker.pawn);
                return "unspawned";
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

        public void CaptureArchiveEntry(IArchivable item)
        {
            if (!_initialized || item == null) return;
            try
            {
                string label = null;
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
                            if (tv.FieldExists())
                                tooltip = StripTags(tv.GetValue()?.ToString());
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

                // Skip administrative noise
                if (label != null && (
                    label.IndexOf("role deactivated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    label.IndexOf("role activated",   StringComparison.OrdinalIgnoreCase) >= 0)) return;

                // Ritual opportunity letters: include but truncate to first sentence
                bool isOpportunity = item.GetType().Name == "RitualOpportunityLetter"
                    || (item is Letter lo && lo.def?.defName == "NeutralEvent"
                        && label != null && label.IndexOf("opportunity", StringComparison.OrdinalIgnoreCase) >= 0);
                if (isOpportunity)
                    text = $"{label}: {FirstSentence(tooltip)}";

                AppendEvent(Find.TickManager.TicksAbs, prefix.NullOrEmpty() ? text : $"{prefix} {text}");
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Skipped archive item ({item?.GetType().Name}): {e.Message}");
            }
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
                    logInitiator = _initiatorField?.GetValue(entry) as Pawn;
                    logRecipient = _recipientField?.GetValue(entry) as Pawn;
                    bool colonistInvolved = (logInitiator?.IsFreeColonist ?? false)
                                        || (logRecipient?.IsFreeColonist ?? false);
                    if (!colonistInvolved) return;
                    initiatorTag = IntroduceTag(logInitiator);
                    recipientTag = IntroduceTag(logRecipient);
                }

                string text = StripTags(FormatLogEntry(entry));
                if (text.NullOrEmpty()) return;

                if (logInitiator?.LabelShort != null)
                    text = InjectAfterFirst(text, logInitiator.LabelShort, initiatorTag);
                if (logRecipient?.LabelShort != null)
                    text = InjectAfterFirst(text, logRecipient.LabelShort, recipientTag);

                long absTick = Find.TickManager.TicksAbs;
                try { absTick = (long)Traverse.Create(entry).Field("ticksAbs").GetValue<int>(); } catch { }

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
    }
}
