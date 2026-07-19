using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Firefly
{
    public static class ColonyStateCollector
    {
        private static readonly HashSet<string> KeyRelations = new HashSet<string>
            { "Spouse", "Lover", "Fiance", "Parent", "Sibling", "HalfSibling" };

        public static string GetSnapshot(Map map)
        {
            if (map == null) return null;

            var sb = new StringBuilder();
            TrySection(sb, "Header",       () => AppendHeader(sb, map));
            TrySection(sb, "Colonists",    () => AppendColonists(sb, map));
            TrySection(sb, "Prisoners",    () => AppendPrisoners(sb, map));
            TrySection(sb, "Threats",      () => AppendThreats(sb, map));
            TrySection(sb, "Resources",    () => AppendResources(sb, map));
            TrySection(sb, "Infra",        () => AppendInfrastructure(sb, map));
            TrySection(sb, "Factions",     () => AppendFactions(sb));
            TrySection(sb, "Research",     () => AppendResearch(sb));
            TrySection(sb, "Events",       () => AppendRecentEvents(sb));
            TrySection(sb, "Designations", () => AppendDesignations(sb, map));
            return sb.ToString();
        }

        private static void TrySection(StringBuilder sb, string name, Action body)
        {
            try { body(); }
            catch (Exception e)
            {
                sb.AppendLine($"[{name} error: {e.Message}]");
                Log.Warning($"[Firefly] ColonyStateCollector section '{name}' failed: {e}");
            }
        }

        // ── Header ──────────────────────────────────────────────────────────────

        private static void AppendHeader(StringBuilder sb, Map map)
        {
            string colony = map.info?.parent?.Label ?? "Unnamed Colony";
            int day = GenDate.DaysPassed;
            long absTick = Find.TickManager.TicksAbs;
            float lon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
            int year = GenDate.Year(absTick, lon);
            string season = SeasonString(GenLocalDate.Season(map));
            string biome = map.Biome?.label?.CapitalizeFirst() ?? "Unknown";
            string weather = map.weatherManager?.curWeather?.label?.CapitalizeFirst() ?? "Unknown";
            float temp = map.mapTemperature?.OutdoorTemp ?? 0f;

            sb.AppendLine("=== FIREFLY COLONY SNAPSHOT ===");
            sb.AppendLine($"Colony: {colony}  |  Day {day}, {season}, Year {year}");
            sb.AppendLine($"Biome: {biome}  |  Weather: {weather}, {temp:F0}°C");
            sb.AppendLine();
        }

        private static string SeasonString(Season s)
        {
            switch (s)
            {
                case Season.Spring:          return "Spring";
                case Season.Summer:          return "Summer";
                case Season.Fall:            return "Fall";
                case Season.Winter:          return "Winter";
                case Season.PermanentSummer: return "Permanent Summer";
                case Season.PermanentWinter: return "Permanent Winter";
                default:                     return s.ToString();
            }
        }

        // ── Colonists ───────────────────────────────────────────────────────────

        private static void AppendColonists(StringBuilder sb, Map map)
        {
            var colonists = map.mapPawns?.FreeColonists?.ToList();
            sb.AppendLine($"=== COLONISTS ({colonists?.Count ?? 0}) ===");
            if (colonists == null || colonists.Count == 0) { sb.AppendLine(); return; }

            foreach (var pawn in colonists)
                TrySection(sb, $"colonist:{pawn?.LabelShort ?? "?"}", () => AppendColonist(sb, pawn));

            sb.AppendLine();
        }

        private static void AppendColonist(StringBuilder sb, Pawn pawn)
        {
            if (pawn == null) return;

            string name      = pawn.Name?.ToStringFull ?? "Unknown";
            int    age       = pawn.ageTracker?.AgeBiologicalYears ?? 0;
            string gender    = pawn.gender == Gender.Male ? "M" : pawn.gender == Gender.Female ? "F" : "?";
            string childhood = pawn.story?.Childhood?.title ?? "—";
            string adulthood = pawn.story?.Adulthood?.title ?? "—";
            sb.AppendLine($"{name}, {age}{gender} — {childhood} / {adulthood}");

            // Traits
            var traits = pawn.story?.traits?.allTraits;
            if (traits != null && traits.Count > 0)
                sb.AppendLine($"  Traits: {string.Join(", ", traits.Select(t => t.LabelCap))}");

            // Mood + top thoughts
            var mood = pawn.needs?.mood;
            if (mood != null)
            {
                int moodPct = Mathf.RoundToInt(mood.CurLevel * 100f);
                string moodLabel = MoodLabel(mood.CurLevel);
                string thoughtStr = "";
                var mems = mood.thoughts?.memories?.Memories;
                if (mems != null && mems.Count > 0)
                {
                    var top = mems
                        .Where(t => t != null && t.MoodOffset() != 0f)
                        .OrderByDescending(t => Math.Abs(t.MoodOffset()))
                        .Take(3)
                        .Select(t => $"{t.LabelCap} ({(t.MoodOffset() >= 0 ? "+" : "")}{Mathf.RoundToInt(t.MoodOffset())})")
                        .ToList();
                    if (top.Count > 0)
                        thoughtStr = ". Recent thoughts: " + string.Join(", ", top);
                }
                sb.AppendLine($"  Mood: {moodPct}% — {moodLabel}{thoughtStr}");
            }

            // Health
            float hp = pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
            int hpPct = Mathf.RoundToInt(hp * 100f);
            var hediffs = GetNotableHediffs(pawn);
            string healthSuffix = hediffs.Count > 0 ? " — " + string.Join(", ", hediffs) : "";
            sb.AppendLine($"  Health: {hpPct}%{healthSuffix}");

            // Current activity
            string activity = pawn.jobs?.curDriver?.GetReport()?.CapitalizeFirst() ?? "Idle";
            sb.AppendLine($"  Doing: {activity}");

            // Top 3 skills
            var skills = pawn.skills?.skills;
            if (skills != null && skills.Count > 0)
            {
                var top3 = skills.OrderByDescending(s => s.Level).Take(3)
                    .Select(s => $"{s.def.LabelCap} {s.Level}");
                sb.AppendLine($"  Top skills: {string.Join(", ", top3)}");
            }

            // Key relations
            var rels = pawn.relations?.DirectRelations;
            if (rels != null)
            {
                var key = rels
                    .Where(r => r?.otherPawn != null && !r.otherPawn.Dead && KeyRelations.Contains(r.def?.defName))
                    .Select(r => $"{r.otherPawn.LabelShort} ({r.def.label})")
                    .ToList();
                if (key.Count > 0)
                    sb.AppendLine($"  Relations: {string.Join(", ", key)}");
            }
        }

        private static List<string> GetNotableHediffs(Pawn pawn)
        {
            var result = new List<string>();
            var hediffs = pawn.health?.hediffSet?.hediffs;
            if (hediffs == null) return result;

            foreach (var h in hediffs)
            {
                if (h == null || h.def == null) continue;
                if (!h.Visible) continue;
                if (!h.def.isBad) continue;
                if (h is Hediff_Injury && h.Severity < 0.15f) continue;
                string label = h.LabelBaseCap;
                if (h.Part != null) label += $" ({h.Part.Label})";
                result.Add(label);
            }
            return result;
        }

        private static string MoodLabel(float level)
        {
            if (level >= 0.9f) return "euphoric";
            if (level >= 0.75f) return "happy";
            if (level >= 0.6f) return "content";
            if (level >= 0.4f) return "okay";
            if (level >= 0.25f) return "stressed";
            if (level >= 0.1f) return "miserable";
            return "on the edge";
        }

        // ── Prisoners ───────────────────────────────────────────────────────────

        private static void AppendPrisoners(StringBuilder sb, Map map)
        {
            sb.AppendLine("=== PRISONERS ===");
            var prisoners = map.mapPawns?.PrisonersOfColony?.ToList();
            if (prisoners == null || prisoners.Count == 0) { sb.AppendLine("None."); sb.AppendLine(); return; }

            foreach (var p in prisoners)
            {
                if (p == null) continue;
                string name     = p.Name?.ToStringFull ?? "Unknown";
                string faction  = p.Faction?.Name ?? "No Faction";
                float resistance = p.guest?.resistance ?? 0f;
                sb.AppendLine($"{name} ({faction}) — Resistance: {resistance:F0}");
            }
            sb.AppendLine();
        }

        // ── Threats ─────────────────────────────────────────────────────────────

        private static void AppendThreats(StringBuilder sb, Map map)
        {
            sb.AppendLine("=== THREATS ON MAP ===");
            var playerFaction = Faction.OfPlayer;
            if (playerFaction == null) { sb.AppendLine("Unable to determine."); sb.AppendLine(); return; }

            var hostiles = map.mapPawns?.AllPawnsSpawned
                ?.Where(p => p != null && !p.Dead && !p.Downed && p.HostileTo(playerFaction))
                .ToList();

            if (hostiles == null || hostiles.Count == 0) { sb.AppendLine("None currently."); sb.AppendLine(); return; }

            var grouped = new Dictionary<string, int>();
            foreach (var p in hostiles)
            {
                string key = p.Faction?.Name ?? "Unknown";
                grouped[key] = grouped.TryGetValue(key, out int c) ? c + 1 : 1;
            }

            foreach (var kvp in grouped.OrderByDescending(x => x.Value))
                sb.AppendLine($"{kvp.Value} from {kvp.Key}");

            sb.AppendLine();
        }

        // ── Resources ───────────────────────────────────────────────────────────

        private static void AppendResources(StringBuilder sb, Map map)
        {
            sb.AppendLine("=== RESOURCES ===");

            float nutrition = map.resourceCounter?.TotalHumanEdibleNutrition ?? 0f;
            float wealth    = map.wealthWatcher?.WealthTotal ?? 0f;
            int silver      = map.resourceCounter?.GetCount(ThingDefOf.Silver) ?? 0;
            int steel       = map.resourceCounter?.GetCount(ThingDefOf.Steel) ?? 0;
            int components  = map.resourceCounter?.GetCount(ThingDefOf.ComponentIndustrial) ?? 0;
            int medicine    = map.resourceCounter?.GetCount(ThingDefOf.MedicineIndustrial) ?? 0;

            float threatPts = 0f;
            try { threatPts = StorytellerUtility.DefaultThreatPointsNow(map); } catch { }

            sb.AppendLine($"Food: {nutrition:F0} nutrition  |  Medicine: {medicine}  |  Silver: {silver:N0}");
            sb.AppendLine($"Steel: {steel:N0}  |  Components: {components}  |  Wealth: {wealth:N0}");
            sb.AppendLine($"Threat power: {threatPts:F0}");
            sb.AppendLine();
        }

        // ── Infrastructure ──────────────────────────────────────────────────────

        private static void AppendInfrastructure(StringBuilder sb, Map map)
        {
            sb.AppendLine("=== INFRASTRUCTURE ===");

            sb.AppendLine($"Base type: {DetermineBaseType(map)}");

            var beds = map.listerBuildings?.AllBuildingsColonistOfClass<Building_Bed>()?.ToList()
                       ?? new List<Building_Bed>();
            int colonistCount = map.mapPawns?.FreeColonists?.Count ?? 0;
            int medBeds = beds.Count(b => b != null && b.Medical);
            sb.AppendLine($"Beds: {beds.Count} / {colonistCount} colonists  |  Medical beds: {medBeds}");

            AppendPowerSummary(sb, map);
            AppendRoomTypes(sb, map);
            sb.AppendLine();
        }

        private static string DetermineBaseType(Map map)
        {
            int mountainCells = 0;
            int checked_      = 0;
            foreach (var cell in map.AllCells)
            {
                var roof = map.roofGrid?.RoofAt(cell);
                if (roof == RoofDefOf.RoofRockThick || roof == RoofDefOf.RoofRockThin)
                {
                    mountainCells++;
                    if (mountainCells >= 100) return "Mountain fortress";
                }
                if (++checked_ >= 5000) break;
            }
            return mountainCells >= 20 ? "Mountain base" : "Surface settlement";
        }

        private static void AppendPowerSummary(StringBuilder sb, Map map)
        {
            float generated = 0f, consumed = 0f;
            var buildings = map.listerBuildings?.allBuildingsColonist;
            if (buildings == null) return;

            foreach (var b in buildings)
            {
                var power = b?.TryGetComp<CompPowerTrader>();
                if (power == null) continue;
                float output = power.PowerOutput;
                if (output > 0f) generated += output;
                else consumed += -output;
            }

            if (generated == 0f && consumed == 0f) return;
            string status = generated >= consumed ? "stable" : "SHORTAGE";
            sb.AppendLine($"Power: {generated:F0}W generated / {consumed:F0}W consumed — {status}");
        }

        private static void AppendRoomTypes(StringBuilder sb, Map map)
        {
            var allRooms = map.regionGrid?.AllRooms;
            if (allRooms == null) return;

            var counts = new Dictionary<string, int>();
            foreach (var room in allRooms)
            {
                string role = room?.Role?.label;
                if (role.NullOrEmpty() || role == "none" || role == "outdoors") continue;
                counts[role] = counts.TryGetValue(role, out int c) ? c + 1 : 1;
            }

            if (counts.Count == 0) return;
            string rooms = string.Join(", ", counts.Select(kvp => $"{kvp.Value} {kvp.Key}"));
            sb.AppendLine($"Rooms: {rooms}");
        }

        // ── Factions ────────────────────────────────────────────────────────────

        private static void AppendFactions(StringBuilder sb)
        {
            sb.AppendLine("=== FACTION RELATIONS ===");
            var playerFaction = Faction.OfPlayer;
            if (playerFaction == null) { sb.AppendLine("Player faction not initialized."); sb.AppendLine(); return; }

            var factions = Find.FactionManager?.AllFactionsVisible?.ToList();
            if (factions == null) { sb.AppendLine(); return; }

            bool any = false;
            foreach (var f in factions)
            {
                if (f == null || f == playerFaction) continue;
                string name    = f.Name ?? "Unknown";
                string kind    = f.def?.label?.CapitalizeFirst() ?? "";
                int goodwill   = f.GoodwillWith(playerFaction);
                bool hostile   = f.HostileTo(playerFaction);
                string status  = hostile ? "Hostile" : goodwill >= 75 ? "Allied" : "Neutral";
                string sign    = goodwill >= 0 ? "+" : "";
                sb.AppendLine($"{name} ({kind}): {status} ({sign}{goodwill})");
                any = true;
            }

            if (!any) sb.AppendLine("No visible factions.");
            sb.AppendLine();
        }

        // ── Research ────────────────────────────────────────────────────────────

        private static readonly AccessTools.FieldRef<ResearchManager, ResearchProjectDef> _currentProjRef =
            AccessTools.FieldRefAccess<ResearchManager, ResearchProjectDef>("currentProj");

        private static void AppendResearch(StringBuilder sb)
        {
            sb.AppendLine("=== RESEARCH ===");
            ResearchManager rm = Find.ResearchManager;
            ResearchProjectDef proj = null;
            try { proj = rm != null ? _currentProjRef(rm) : null; } catch { }

            if (proj == null)
                sb.AppendLine("No research in progress.");
            else
            {
                int pct = Mathf.RoundToInt(proj.ProgressPercent * 100f);
                sb.AppendLine($"Current: {proj.label?.CapitalizeFirst() ?? proj.defName} ({pct}%)");
            }
            sb.AppendLine();
        }

        // ── Recent Events ───────────────────────────────────────────────────────

        private static void AppendRecentEvents(StringBuilder sb)
        {
            sb.AppendLine("=== RECENT EVENTS ===");
            var entries = Find.PlayLog?.AllEntries;
            if (entries == null || entries.Count == 0) { sb.AppendLine("No recent events."); sb.AppendLine(); return; }

            int written = 0;
            foreach (var entry in entries)
            {
                if (written >= 10) break;
                try
                {
                    string text = entry?.ToGameStringFromPOV(null);
                    if (text.NullOrEmpty()) continue;
                    sb.AppendLine(text.CapitalizeFirst());
                    written++;
                }
                catch { }
            }

            if (written == 0) sb.AppendLine("No recent events.");
            sb.AppendLine();
        }

        // ── Designations ────────────────────────────────────────────────────────

        private static readonly AccessTools.FieldRef<DesignationManager, List<Designation>> _desListRef =
            AccessTools.FieldRefAccess<DesignationManager, List<Designation>>("desList");

        private static void AppendDesignations(StringBuilder sb, Map map)
        {
            sb.AppendLine("=== PLAYER DESIGNATIONS ===");
            List<Designation> designations = null;
            try { designations = map.designationManager != null ? _desListRef(map.designationManager) : null; } catch { }

            if (designations == null || designations.Count == 0) { sb.AppendLine("None."); sb.AppendLine(); return; }

            var counts = new Dictionary<string, int>();
            foreach (var d in designations)
            {
                string key = d?.def?.label ?? d?.def?.defName ?? "unknown";
                if (key.NullOrEmpty()) continue;
                counts[key] = counts.TryGetValue(key, out int c) ? c + 1 : 1;
            }

            if (counts.Count == 0) { sb.AppendLine("None."); sb.AppendLine(); return; }
            string parts = string.Join(", ", counts.OrderByDescending(x => x.Value).Select(x => $"{x.Value} {x.Key}"));
            sb.AppendLine(parts);
            sb.AppendLine();
        }
    }
}
