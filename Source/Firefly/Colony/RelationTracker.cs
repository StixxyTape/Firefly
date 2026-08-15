using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace Firefly
{
    // Day-over-day diffing for pawn relations, faction goodwill, and skills — the RELATIONSHIP
    // CHANGES / FACTION RELATIONS / SKILL CHANGES journal sections. Extracted from ColonyLedger.
    internal class RelationTracker
    {
        private Dictionary<string, string> _prevDayRelations     = new Dictionary<string, string>();
        private Dictionary<string, string> _prevDaySkills        = new Dictionary<string, string>();
        private Dictionary<string, string> _prevFactionRelations = new Dictionary<string, string>();

        // Snapshots relations for allTracked (filtered to trackedPawnIds — pawns already
        // introduced in the journal), diffs against yesterday, and commits today as the new
        // "yesterday". Returns the rendered RELATIONSHIP CHANGES section (or "").
        public string BuildRelationSection(Map map, List<Pawn> allTracked, HashSet<string> trackedPawnIds)
        {
            var current = GetRelationSnapshot(allTracked, trackedPawnIds);
            string changes = BuildRelationChanges(current);
            _prevDayRelations = current;
            return changes;
        }

        // Same pattern for faction goodwill/relation-kind — returns the FACTION RELATIONS section.
        public string BuildFactionSection()
        {
            var current = GetFactionRelationSnapshot();
            string changes = BuildFactionRelationChanges(current);
            _prevFactionRelations = current;
            return changes;
        }

        // Same pattern for colonist skill levels/passions — returns the SKILL CHANGES section.
        public string BuildSkillSection(List<Pawn> colonists)
        {
            var current = GetSkillSnapshot(colonists);
            string changes = BuildSkillChanges(current);
            _prevDaySkills = current;
            return changes;
        }

        private static Dictionary<string, string> GetRelationSnapshot(List<Pawn> colonists, HashSet<string> trackedPawnIds)
        {
            var snapshot = new Dictionary<string, string>();
            foreach (var pawnA in colonists)
            {
                if (pawnA == null) continue;
                string idA   = pawnA.ThingID ?? pawnA.LabelShort ?? "?";
                string nameA = ColonyLedger.PawnFullName(pawnA);

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

                related.RemoveWhere(p => p == null || !trackedPawnIds.Contains(p.ThingID ?? ""));

                foreach (var pawnB in related)
                {
                    if (pawnB == null) continue;
                    string idB   = pawnB.ThingID ?? pawnB.LabelShort ?? "?";
                    string nameB = ColonyLedger.PawnFullName(pawnB);
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

            foreach (var kvp in current)
            {
                _prevDayRelations.TryGetValue(kvp.Key, out string prevValue);

                ParseRelEntry(kvp.Value,   out string nameA, out string nameB, out string curRels,  out int curOpinion,  out string curThoughts);
                ParseRelEntry(prevValue ?? "->||", out _, out _, out string prevRels, out int prevOpinion, out string prevThoughts);

                var changes = new List<string>();

                var curRelList  = curRels.Split(',').Where(r => !r.NullOrEmpty()).ToList();
                var prevRelList = prevRels.Split(',').Where(r => !r.NullOrEmpty()).ToList();
                foreach (var r in curRelList.Except(prevRelList))  changes.Add($"new relation with {nameB}: {r}");
                foreach (var r in prevRelList.Except(curRelList))  changes.Add($"lost relation with {nameB}: {r}");

                int delta = curOpinion - prevOpinion;
                if (Math.Abs(delta) >= 10)
                {
                    string dir = delta > 0 ? "improved" : "worsened";
                    changes.Add($"opinion of {nameB} {dir}: {prevOpinion:+#;-#;0} → {curOpinion:+#;-#;0}");
                }

                var curThoughtSet  = new HashSet<string>(curThoughts.Split(';').Where(t => !t.NullOrEmpty()));
                var prevThoughtSet = new HashSet<string>(prevThoughts.Split(';').Where(t => !t.NullOrEmpty()));
                foreach (var t in curThoughtSet.Except(prevThoughtSet))  changes.Add($"new memory about {nameB}: {t}");

                if (changes.Any())
                {
                    if (!byPawn.ContainsKey(nameA)) byPawn[nameA] = new List<string>();
                    byPawn[nameA].AddRange(changes);
                }
            }

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

        private static Dictionary<string, string> GetFactionRelationSnapshot()
        {
            var snapshot = new Dictionary<string, string>();
            var factions = Find.FactionManager?.AllFactionsVisible;
            if (factions == null) return snapshot;

            foreach (var faction in factions)
            {
                if (faction == null || faction.IsPlayer || faction.defeated || !faction.HasGoodwill) continue;
                snapshot[faction.GetUniqueLoadID()] = $"{faction.PlayerGoodwill}|{faction.PlayerRelationKind}";
            }
            return snapshot;
        }

        private string BuildFactionRelationChanges(Dictionary<string, string> current)
        {
            if (_prevFactionRelations.Count == 0) return "";

            var factionsById = (Find.FactionManager?.AllFactionsVisible ?? Enumerable.Empty<Faction>())
                .Where(f => f != null)
                .ToDictionary(f => f.GetUniqueLoadID(), f => f);

            var lines = new List<string>();
            foreach (var kvp in current)
            {
                if (!_prevFactionRelations.TryGetValue(kvp.Key, out string prevValue)) continue;
                if (!factionsById.TryGetValue(kvp.Key, out Faction faction)) continue;

                ParseFactionEntry(kvp.Value,  out int curGoodwill,  out FactionRelationKind curKind);
                ParseFactionEntry(prevValue,  out int prevGoodwill, out FactionRelationKind prevKind);

                var changes = new List<string>();
                if (curKind != prevKind)
                    changes.Add($"relations with the colony shifted from {prevKind} to {curKind}");

                int delta = curGoodwill - prevGoodwill;
                if (Math.Abs(delta) >= 10)
                {
                    string dir = delta > 0 ? "improved" : "worsened";
                    changes.Add($"goodwill {dir}: {prevGoodwill:+#;-#;0} → {curGoodwill:+#;-#;0}");
                }

                if (changes.Any())
                    lines.Add($"  {faction.Name ?? "Unknown Faction"}: {string.Join(", ", changes)}");
            }

            if (!lines.Any()) return "";

            var sb = new StringBuilder();
            sb.AppendLine("=== FACTION RELATIONS ===");
            foreach (var line in lines) sb.AppendLine(line);
            return sb.ToString();
        }

        private static void ParseFactionEntry(string value, out int goodwill, out FactionRelationKind kind)
        {
            var parts = value?.Split('|') ?? new string[0];
            goodwill = parts.Length > 0 && int.TryParse(parts[0], out int g) ? g : 0;
            kind     = parts.Length > 1 && Enum.TryParse(parts[1], out FactionRelationKind k) ? k : FactionRelationKind.Neutral;
        }

        private static Dictionary<string, string> GetSkillSnapshot(List<Pawn> colonists)
        {
            var snapshot = new Dictionary<string, string>();
            foreach (var p in colonists)
            {
                if (p?.skills == null) continue;
                string id   = p.ThingID ?? p.LabelShort ?? "?";
                string name = ColonyLedger.PawnFullName(p);
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
                    int curLevel   = skill.Value.Level;
                    int prevLevel  = prev.Level;
                    string curPassion  = skill.Value.Passion;
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
            var parts = value?.Split('|') ?? new string[0];
            string names = parts.Length > 0 ? parts[0] : "->";
            int arrow = names.IndexOf("->");
            nameA     = arrow >= 0 ? names.Substring(0, arrow) : names;
            nameB     = arrow >= 0 ? names.Substring(arrow + 2) : "";
            relations = parts.Length > 1 ? parts[1] : "";
            opinion   = parts.Length > 2 && int.TryParse(parts[2], out int o) ? o : 0;
            thoughts  = parts.Length > 3 ? parts[3] : "";
        }

        // ── Save / load ───────────────────────────────────────────────────────
        // Field name strings must stay exactly as-is for existing-save compatibility.

        public void ExposeData()
        {
            Scribe_Collections.Look(ref _prevDayRelations,     "prevDayRelations",     LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _prevDaySkills,        "prevDaySkills",        LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _prevFactionRelations, "prevFactionRelations", LookMode.Value, LookMode.Value);

            if (Scribe.mode != LoadSaveMode.LoadingVars) return;

            if (_prevDayRelations     == null) _prevDayRelations     = new Dictionary<string, string>();
            if (_prevDaySkills        == null) _prevDaySkills        = new Dictionary<string, string>();
            if (_prevFactionRelations == null) _prevFactionRelations = new Dictionary<string, string>();
        }
    }
}
