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
    internal struct CombatEvent
    {
        public string Initiator;
        public string InitiatorId;
        public string Target;
        public string TargetId;
        public bool   ReachedTarget;
        public bool   Deflected;
        public bool   DidDamage;
        public string CoverHit;
        public string Weapon;
        public bool   InitiatorIsColonist;
        public string BattleId;
        public long   Tick;
    }

    internal struct HazardEvent
    {
        public string Victim;
        public string HazardLabel;
        public long   Tick;
        public bool   DidDamage;
    }

    // Captures battle/hazard/outcome events as they happen and turns them into the
    // COMBAT/HAZARDS timeline sections. Extracted from ColonyLedger — see that class for the
    // capture entry points (CaptureBattleEvent etc.) that call into this buffer.
    internal class CombatEventBuffer
    {
        private const char FieldSep = '\t';
        private const int MaxAnnouncedBattles = 4000;

        private HashSet<string>           _announcedBattles     = new HashSet<string>();
        private Queue<string>             _announcedBattleOrder = new Queue<string>();
        private HashSet<(string, string)> _announcedHazards     = new HashSet<(string, string)>();

        private readonly List<CombatEvent> _capturedBattleEvents = new List<CombatEvent>();
        private readonly List<HazardEvent> _capturedHazardEvents = new List<HazardEvent>();
        private readonly List<(string Subject, string SubjectId, string Outcome, string Initiator, string Cause, long Tick)> _capturedOutcomes
            = new List<(string, string, string, string, string, long)>();

        private readonly List<(long Tick, string Text)> _drainedCombatLines     = new List<(long, string)>();
        private readonly Dictionary<string, long>       _drainedCombatLastTick  = new Dictionary<string, long>();
        private readonly List<(long Tick, string Text)> _drainedHazardLines     = new List<(long, string)>();
        private readonly Dictionary<string, long>       _drainedHazardLastTick  = new Dictionary<string, long>();

        private readonly Dictionary<string, long> _lastFactionCombatTick = new Dictionary<string, long>();

        private static readonly FieldInfo _deflectedField =
            AccessTools.Field(typeof(LogEntry_DamageResult), "deflected");
        private static readonly FieldInfo _damagedPartsField =
            AccessTools.Field(typeof(LogEntry_DamageResult), "damagedParts");

        private static bool HasDamagedParts(LogEntry_DamageResult entry)
        {
            var parts = _damagedPartsField?.GetValue(entry) as List<BodyPartRecord>;
            return parts != null && parts.Count > 0;
        }

        // ── Capture ────────────────────────────────────────────────────────────

        // Records the event and reports whether it starts a newly-announced colonist/enemy
        // pairing (caller uses that to decide whether to emit a "[Combat started]" line).
        public bool RecordBattleEvent(string initiator, string initiatorId, string target, string targetId,
            bool reachedTarget, string weapon, string coverHit, bool initiatorIsColonist, string battleId,
            LogEntry_DamageResult entry, out CombatEvent recorded)
        {
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
            recorded = new CombatEvent
            {
                Initiator           = initiator   ?? "?",
                InitiatorId         = initiatorId ?? initiator ?? "?",
                Target              = target      ?? "?",
                TargetId            = targetId    ?? target    ?? "?",
                ReachedTarget       = reachedTarget,
                Deflected           = deflected,
                DidDamage           = didDamage,
                Weapon              = weapon,
                CoverHit            = coverHit,
                InitiatorIsColonist = initiatorIsColonist,
                BattleId            = battleId ?? "",
                Tick                = tick,
            };
            lock (_capturedBattleEvents) _capturedBattleEvents.Add(recorded);

            string colonistId = initiatorIsColonist ? initiatorId : targetId;
            string enemyId    = initiatorIsColonist ? targetId    : initiatorId;
            string pairKey    = !colonistId.NullOrEmpty() && !enemyId.NullOrEmpty() ? $"{colonistId}:{enemyId}" : null;
            return !pairKey.NullOrEmpty() && TryAnnounceBattle(pairKey);
        }

        // Records the event and reports whether this victim/hazard-label pairing is newly seen.
        public bool RecordHazardEvent(string victim, string hazardLabel, LogEntry_DamageResult entry, out HazardEvent recorded)
        {
            bool didDamage = false;
            if (entry != null)
            {
                try { didDamage = HasDamagedParts(entry); }
                catch { }
            }
            long tick = Find.TickManager.TicksAbs;
            recorded = new HazardEvent { Victim = victim ?? "?", HazardLabel = hazardLabel ?? "unknown hazard", Tick = tick, DidDamage = didDamage };
            lock (_capturedHazardEvents) _capturedHazardEvents.Add(recorded);

            var key = (recorded.Victim, recorded.HazardLabel);
            return _announcedHazards.Add(key);
        }

        public void RecordOutcome(string subject, string subjectId, string outcome, string initiator, string cause, long tick)
        {
            lock (_capturedOutcomes)
                _capturedOutcomes.Add((subject, subjectId ?? "", outcome ?? "", initiator ?? "", cause ?? "", tick));
        }

        // Faction-vs-faction combat is deduplicated by a 3h cooldown per unordered faction pair.
        // Returns true if the caller should emit a line for this tick.
        public bool ShouldEmitFactionCombat(string nameA, string nameB, long tick)
        {
            string key = string.Compare(nameA, nameB, StringComparison.Ordinal) <= 0
                ? $"{nameA}:{nameB}" : $"{nameB}:{nameA}";

            long cooldown = GenDate.TicksPerHour * 3;
            lock (_lastFactionCombatTick)
            {
                if (_lastFactionCombatTick.TryGetValue(key, out long last) && tick - last < cooldown) return false;
                _lastFactionCombatTick[key] = tick;
            }
            return true;
        }

        private bool TryAnnounceBattle(string pairKey)
        {
            if (!_announcedBattles.Add(pairKey)) return false;
            _announcedBattleOrder.Enqueue(pairKey);
            while (_announcedBattleOrder.Count > MaxAnnouncedBattles)
                _announcedBattles.Remove(_announcedBattleOrder.Dequeue());
            return true;
        }

        // ── Hourly drain — keeps combat outcome matching working without file writes ─────────

        public void DrainToBuffers(float lon)
        {
            var (combatSimple, _) = DrainCombatEvents();
            var hazardSummaries   = DrainHazardEvents();
            long cooldown = GenDate.TicksPerHour * 3;
            if (combatSimple.Count > 0)
                lock (_drainedCombatLines)
                    foreach (var line in combatSimple)
                        if (!_drainedCombatLastTick.TryGetValue(line.Text, out long last) || line.Tick - last >= cooldown)
                        {
                            _drainedCombatLines.Add(line);
                            _drainedCombatLastTick[line.Text] = line.Tick;
                        }
            if (hazardSummaries.Count > 0)
                lock (_drainedHazardLines)
                    foreach (var line in hazardSummaries)
                        if (!_drainedHazardLastTick.TryGetValue(line.Text, out long last) || line.Tick - last >= cooldown)
                        {
                            _drainedHazardLines.Add(line);
                            _drainedHazardLastTick[line.Text] = line.Tick;
                        }
        }

        // Called at midnight — does one final drain then returns the accumulated content.
        public (string CombatContent, string HazardContent) FlushDrainedSections(float lon)
        {
            DrainToBuffers(lon);

            List<(long, string)> combat, hazard;
            lock (_drainedCombatLines)
            {
                combat = new List<(long, string)>(_drainedCombatLines);
                _drainedCombatLines.Clear();
                _drainedCombatLastTick.Clear();
            }
            lock (_drainedHazardLines)
            {
                hazard = new List<(long, string)>(_drainedHazardLines);
                _drainedHazardLines.Clear();
                _drainedHazardLastTick.Clear();
            }
            return (BuildSectionString("COMBAT", combat, lon), BuildSectionString("HAZARDS", hazard, lon));
        }

        private static string BuildSectionString(string header, List<(long Tick, string Text)> entries, float lon)
        {
            if (entries.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine($"=== {header} ===");
            foreach (var (tick, text) in entries.OrderBy(e => e.Tick))
            {
                int hr  = GenDate.HourInteger(tick, lon);
                int min = (int)((GenDate.HourFloat(tick, lon) % 1f) * 60f);
                sb.AppendLine($"  - [{hr:D2}:{min:D2}] {text}");
            }
            return sb.ToString();
        }

        // Read-only peek at today's accumulated combat/hazard for the journal UI.
        public string GetCurrentCombatContent(float lon)
        {
            lock (_drainedCombatLines) return PeekDrainedLines(_drainedCombatLines, lon);
        }

        public string GetCurrentHazardContent(float lon)
        {
            lock (_drainedHazardLines) return PeekDrainedLines(_drainedHazardLines, lon);
        }

        private static string PeekDrainedLines(List<(long Tick, string Text)> lines, float lon)
        {
            if (lines.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var (tick, text) in lines.OrderBy(e => e.Tick))
            {
                int hr  = GenDate.HourInteger(tick, lon);
                int min = (int)((GenDate.HourFloat(tick, lon) % 1f) * 60f);
                sb.AppendLine($"  - [{hr:D2}:{min:D2}] {text}");
            }
            return sb.ToString();
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
                int    total     = group.Count();
                int    dmgCount  = group.Count(e => e.DidDamage);
                string verb      = HazardVerb(label);
                string line      = $"{victim} was {verb} {total} time{(total == 1 ? "" : "s")}";
                if (dmgCount > 0 && dmgCount < total)
                    line += $" ({dmgCount} did damage)";
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
                case "unnatural darkness":
                case "unnat dark":
                case "unnatural dark":     return "harmed by unnatural darkness";
                default:                   return $"damaged by {label}";
            }
        }

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

            long nowTick   = 0L;
            try { nowTick = Find.TickManager.TicksAbs; } catch { }
            long keepAfter = nowTick - GenDate.TicksPerHour * 2;

            if (shots.Count == 0)
            {
                RebufferOutcomes(outcomes, null, keepAfter);
                return (new List<(long, string)>(), new List<string>());
            }

            var summaries = new List<string>();

            (string ColonistName, string ColonistId, string OtherId, string OtherName) Info(CombatEvent e) =>
                e.InitiatorIsColonist
                    ? (e.Initiator, e.InitiatorId, e.TargetId,    e.Target)
                    : (e.Target,    e.TargetId,    e.InitiatorId, e.Initiator);

            if (includeDetailed)
            {
                var groups = shots.GroupBy(e => { var i = Info(e); return (i.ColonistName, i.OtherId); }).ToList();
                var nameCount = groups
                    .GroupBy(g => { var i = Info(g.First()); return (i.ColonistName, i.OtherName); })
                    .ToDictionary(g => g.Key, g => g.Count());
                var nameCounter = new Dictionary<(string, string), int>();

                foreach (var group in groups)
                {
                    var info    = Info(group.First());
                    string col  = info.ColonistName;
                    string other = info.OtherName;
                    var nameKey = (col, other);

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

            var simple              = new List<(long Tick, string Text)>();
            var opponentsByColonist = new Dictionary<string, Dictionary<string, string>>();
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
                long tick     = firstTickByColonist.TryGetValue(col, out long ft) ? ft : 0L;
                long lastTick = lastTickByColonist.TryGetValue(col, out long lt)  ? lt : tick;
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

        private static int FindOutcomeForFight(
            List<(string Subject, string SubjectId, string Outcome, string Initiator, string Cause, long Tick)> outcomes,
            bool[] used, string colonistName, string colonistId, long firstTick, long lastTick)
        {
            long grace = GenDate.TicksPerHour;
            int closestIndex = -1;
            long closestDistance = long.MaxValue;
            for (int i = 0; i < outcomes.Count; i++)
            {
                if (used[i]) continue;
                var o = outcomes[i];
                bool sameSubject = !o.SubjectId.NullOrEmpty() && !colonistId.NullOrEmpty()
                    ? o.SubjectId == colonistId
                    : o.Subject == colonistName;
                if (!sameSubject) continue;
                if (o.Tick != 0L && (o.Tick < firstTick - grace || o.Tick > lastTick + grace)) continue;
                long distance = o.Tick == 0L ? long.MaxValue : Math.Abs(o.Tick - lastTick);
                if (closestIndex < 0 || distance < closestDistance)
                {
                    closestIndex = i;
                    closestDistance = distance;
                }
            }
            return closestIndex;
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
            int blockedCount = hitCount - deflCount - damageCount;

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
                if (deflCount    > 0) stops.Add($"{deflCount} deflected by armor");
                if (blockedCount > 0) stops.Add($"{blockedCount} blocked");
                if (stops.Count  > 0) sb.Append($", but {JoinList(stops)}");
                sb.Append(".");
                if (damageCount > 0) sb.Append($" {damageCount} did damage.");
            }

            return sb.ToString();
        }

        private static string JoinList(List<string> parts)
        {
            if (parts.Count == 0) return "";
            if (parts.Count == 1) return parts[0];
            return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts.Last();
        }

        // ── Clear (new-storyteller / opt-out reset) ───────────────────────────

        public void Clear()
        {
            _announcedHazards.Clear();
            lock (_capturedBattleEvents) _capturedBattleEvents.Clear();
            lock (_capturedHazardEvents) _capturedHazardEvents.Clear();
            lock (_capturedOutcomes)     _capturedOutcomes.Clear();
            // _announcedBattles intentionally kept — battle IDs are unique per game
            lock (_drainedCombatLines) { _drainedCombatLines.Clear(); _drainedCombatLastTick.Clear(); }
            lock (_drainedHazardLines)   { _drainedHazardLines.Clear();  _drainedHazardLastTick.Clear(); }
            lock (_lastFactionCombatTick)  _lastFactionCombatTick.Clear();
        }

        // On init (first Record() call ever) — discard anything captured before the ledger
        // was ready, same as ColonyLedger's own _initialized guard used to do inline.
        public void ClearPending()
        {
            lock (_capturedBattleEvents) _capturedBattleEvents.Clear();
            lock (_capturedHazardEvents) _capturedHazardEvents.Clear();
            lock (_capturedOutcomes)     _capturedOutcomes.Clear();
        }

        // ── Save / load ───────────────────────────────────────────────────────
        // Field name strings below must stay exactly as-is — changing them would silently drop
        // this data from existing saves (Scribe looks values up by key, not position).

        public void ExposeData()
        {
            bool saving = Scribe.mode == LoadSaveMode.Saving;

            var pending = saving ? EncodePendingEvents() : null;
            var battles = saving ? _announcedBattleOrder.ToList() : null;
            var hazards = saving ? _announcedHazards.Select(h => $"{h.Item1}{FieldSep}{h.Item2}").ToList() : null;
            List<string> drainedCombat = saving ? _drainedCombatLines.Select(e => $"{e.Tick}{FieldSep}{e.Text}").ToList() : null;
            List<string> drainedHazard = saving ? _drainedHazardLines.Select(e => $"{e.Tick}{FieldSep}{e.Text}").ToList() : null;

            Scribe_Collections.Look(ref pending,       "pendingEvents",    LookMode.Value);
            Scribe_Collections.Look(ref battles,       "announcedBattles", LookMode.Value);
            Scribe_Collections.Look(ref hazards,       "announcedHazards", LookMode.Value);
            Scribe_Collections.Look(ref drainedCombat, "drainedCombat",    LookMode.Value);
            Scribe_Collections.Look(ref drainedHazard, "drainedHazard",    LookMode.Value);

            if (Scribe.mode != LoadSaveMode.LoadingVars) return;

            _announcedBattles     = new HashSet<string>();
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

            DecodePendingEvents(pending);

            lock (_drainedCombatLines)
            {
                _drainedCombatLines.Clear();
                _drainedCombatLastTick.Clear();
                if (drainedCombat != null)
                    foreach (var s in drainedCombat)
                    {
                        int sep = s.IndexOf(FieldSep);
                        if (sep > 0 && long.TryParse(s.Substring(0, sep), out long tick))
                        {
                            string text = s.Substring(sep + 1);
                            _drainedCombatLines.Add((tick, text));
                            _drainedCombatLastTick[text] = tick;
                        }
                    }
            }

            lock (_drainedHazardLines)
            {
                _drainedHazardLines.Clear();
                _drainedHazardLastTick.Clear();
                if (drainedHazard != null)
                    foreach (var s in drainedHazard)
                    {
                        int sep = s.IndexOf(FieldSep);
                        if (sep > 0 && long.TryParse(s.Substring(0, sep), out long tick))
                        {
                            string text = s.Substring(sep + 1);
                            _drainedHazardLines.Add((tick, text));
                            _drainedHazardLastTick[text] = tick;
                        }
                    }
            }
        }

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
                            ReachedTarget       = p[5]  == "True",
                            Deflected           = p[6]  == "True",
                            DidDamage           = p[7]  == "True",
                            CoverHit            = p[8].NullOrEmpty()  ? null : p[8],
                            Weapon              = p[9].NullOrEmpty()  ? null : p[9],
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
    }
}
