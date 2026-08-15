using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;

namespace Firefly
{
    // Tracks which pawns have been "introduced" to the journal (so the LLM only gets a pawn's
    // full descriptor line once) and renders the CHARACTER ROSTER section. Extracted from
    // ColonyLedger. Callers are expected to have already checked the ledger's _initialized /
    // _enabled gates before calling in — these methods have no gating of their own.
    internal class RosterTracker
    {
        private readonly HashSet<string> _trackedPawnIds = new HashSet<string>();
        // Keyed by the pawn's stable ThingID (never by displayed name/line — pawns can share
        // names or be renamed, which made the old name-matching update logic fragile) so a
        // pawn's category can be found and refreshed later regardless of what changed about them.
        private readonly List<(string Id, string Line, string Descriptor)> _trackedPawnLines =
            new List<(string, string, string)>();

        public HashSet<string> TrackedPawnIds => _trackedPawnIds;

        private static string GetPawnDescriptor(Pawn pawn)
        {
            try
            {
                if (pawn.IsFreeColonist)     return "Colonist";
                if (pawn.IsSlaveOfColony)    return "Colony Slave";
                if (pawn.IsPrisonerOfColony) return "Colony Prisoner";
                if (pawn.RaceProps?.Animal == true)
                    return pawn.Faction == Faction.OfPlayer ? "Colony Animal" : "Wild Animal";
                string factionName = pawn.Faction?.Name;
                if (factionName.NullOrEmpty()) return "No Faction";
                bool hostile = pawn.Faction.HostileTo(Faction.OfPlayer);
                return $"{factionName}, {(hostile ? "Hostile" : "Friendly")}";
            }
            catch { return "Unknown"; }
        }

        public string IntroduceTag(Pawn pawn)
        {
            if (pawn == null) return "";
            string id = pawn.ThingID;
            if (id.NullOrEmpty()) return "";

            string category = GetPawnDescriptor(pawn);
            bool isNew;
            lock (_trackedPawnIds) { isNew = _trackedPawnIds.Add(id); }

            if (!isNew)
            {
                // Already introduced before — a pawn's category can change since then (recruited,
                // manumitted, captured, freed, etc.), so refresh the stored entry silently rather
                // than never touching it again. No repeated inline annotation for an old pawn.
                lock (_trackedPawnLines)
                {
                    int idx = _trackedPawnLines.FindIndex(p => p.Id == id);
                    if (idx >= 0 && _trackedPawnLines[idx].Descriptor != category)
                        _trackedPawnLines[idx] = (id, _trackedPawnLines[idx].Line, category);
                }
                return "";
            }

            string line = BuildRosterLine(pawn);
            lock (_trackedPawnLines) { _trackedPawnLines.Add((id, line, category)); }
            return $" ({category})";
        }

        public void IntroduceEventLeader(Pawn pawn, string eventLabel, long tick)
        {
            if (pawn == null) return;
            string id = pawn.ThingID;
            if (id.NullOrEmpty()) return;

            float lon = Find.WorldGrid?.LongLatOf(Find.CurrentMap?.Tile ?? 0).x ?? 0f;
            int hr  = GenDate.HourInteger(tick, lon);
            int min = (int)((GenDate.HourFloat(tick, lon) % 1f) * 60f);
            string leaderLabel = $"Leader of the {eventLabel} [{hr:D2}:{min:D2}]";
            string baseLine    = BuildRosterLine(pawn);
            string newLine     = baseLine + $" — {leaderLabel}";
            string category    = GetPawnDescriptor(pawn);

            lock (_trackedPawnIds) { _trackedPawnIds.Add(id); }

            lock (_trackedPawnLines)
            {
                // If the pawn was already introduced (e.g. via battle events before the letter),
                // replace their existing entry with the leader-labelled version. Matched by their
                // stable id, not by the old line's text — two pawns can share a displayed name.
                int idx = _trackedPawnLines.FindIndex(p => p.Id == id);
                if (idx >= 0)
                    _trackedPawnLines[idx] = (id, newLine, category);
                else
                    _trackedPawnLines.Add((id, newLine, category));
            }
        }

        private static string BuildRosterLine(Pawn pawn)
        {
            string fullName = ColonyLedger.PawnFullName(pawn);

            var attrs = new List<string>();

            if (pawn.gender != Gender.None)
                attrs.Add(pawn.gender.ToString().ToLower());

            int age = pawn.ageTracker?.AgeBiologicalYears ?? 0;
            if (age > 0) attrs.Add(age.ToString());

            string species = pawn.def?.label;
            if (!species.NullOrEmpty()) attrs.Add(species);

            try
            {
                var role = pawn.ideo?.Ideo?.GetRole(pawn);
                if (role != null)
                {
                    string ideoName = pawn.ideo.Ideo.name;
                    attrs.Add(ideoName.NullOrEmpty() ? role.LabelCap : $"{role.LabelCap} of {ideoName}");
                }
            }
            catch { }

            try
            {
                var titles = pawn.royalty?.AllTitlesForReading;
                if (titles != null)
                    foreach (var t in titles)
                        if (t?.def != null)
                        {
                            string factionName = t.faction?.Name;
                            attrs.Add(factionName.NullOrEmpty() ? t.def.LabelCap : $"{t.def.LabelCap} of {factionName}");
                        }
            }
            catch { }

            string callName = pawn.LabelShort;

            string line = fullName;
            if (attrs.Count > 0) line += $" — {string.Join(", ", attrs)}";
            if (!callName.NullOrEmpty()) line += $" — refer to as \"{callName}\"";
            return line;
        }

        public void EnsureCaptivesIntroduced(Map map)
        {
            var prisoners = map.mapPawns.PrisonersOfColonySpawned;
            if (prisoners != null)
                foreach (var p in prisoners) IntroduceTag(p);

            var slaves = map.mapPawns.SlavesOfColonySpawned;
            if (slaves != null)
                foreach (var p in slaves) IntroduceTag(p);
        }

        // Daily roster maintenance for common pawn-status transitions such as prisoner
        // recruitment and slave manumission. IntroduceTag is idempotent for known pawns: it
        // silently refreshes their stored category without emitting another inline introduction.
        public void RefreshTrackedPawnCategories(Map map)
        {
            var colonists = map.mapPawns.FreeColonistsSpawned;
            if (colonists != null)
                foreach (var p in colonists) IntroduceTag(p);

            var prisoners = map.mapPawns.PrisonersOfColonySpawned;
            if (prisoners != null)
                foreach (var p in prisoners) IntroduceTag(p);

            var slaves = map.mapPawns.SlavesOfColonySpawned;
            if (slaves != null)
                foreach (var p in slaves) IntroduceTag(p);
        }

        public string BuildPawnRosterSection()
        {
            lock (_trackedPawnLines)
            {
                if (_trackedPawnLines.Count == 0) return "";
                var order = new[] { "Colonist", "Colony Slave", "Colony Prisoner", "Colony Animal", "Wild Animal" };
                var groups = _trackedPawnLines
                    .GroupBy(p => p.Descriptor)
                    .OrderBy(g => { int i = Array.IndexOf(order, g.Key); return i >= 0 ? i : order.Length; })
                    .ThenBy(g => g.Key);
                var sb = new StringBuilder("=== CHARACTER ROSTER ===\n");
                foreach (var group in groups)
                {
                    sb.AppendLine(RosterCategoryHeader(group.Key) + ":");
                    foreach (var (_, line, _) in group)
                        sb.AppendLine($"  - {line}");
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

        public void Clear()
        {
            lock (_trackedPawnIds)   _trackedPawnIds.Clear();
            lock (_trackedPawnLines) _trackedPawnLines.Clear();
        }

        // ── Save / load ───────────────────────────────────────────────────────
        // "trackedPawnIds" / "trackedPawnLines" keys must stay exactly as-is for existing-save
        // compatibility.

        public void ExposeData()
        {
            const char FieldSep = '\t';
            bool saving = Scribe.mode == LoadSaveMode.Saving;

            var pawnIds   = saving ? _trackedPawnIds.ToList() : null;
            var pawnLines = saving ? _trackedPawnLines.Select(p => $"{p.Id}{FieldSep}{p.Line}{FieldSep}{p.Descriptor}").ToList() : null;

            Scribe_Collections.Look(ref pawnIds,   "trackedPawnIds",   LookMode.Value);
            Scribe_Collections.Look(ref pawnLines, "trackedPawnLines", LookMode.Value);

            if (Scribe.mode != LoadSaveMode.LoadingVars) return;

            _trackedPawnIds.Clear();
            if (pawnIds != null)
                foreach (var id in pawnIds) _trackedPawnIds.Add(id);

            _trackedPawnLines.Clear();
            if (pawnLines != null)
                foreach (var l in pawnLines)
                {
                    var p = l.Split(FieldSep);
                    if (p.Length == 3) _trackedPawnLines.Add((p[0], p[1], p[2]));
                    // Pre-fix save format (no stable id stored yet) — keep visible with an empty
                    // id rather than dropping it; it just won't benefit from id-based refreshing
                    // until this specific pawn is naturally re-encountered and re-added.
                    else if (p.Length == 2) _trackedPawnLines.Add(("", p[0], p[1]));
                }
        }
    }
}
