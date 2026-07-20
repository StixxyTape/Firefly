using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Firefly
{
    internal class LedgerEntry
    {
        public int HourOfDay;
        public List<(string Name, string Activity, int MoodPct)> Pawns = new List<(string, string, int)>();
        public List<string> Events = new List<string>();
    }

    internal static class ColonyLedger
    {
        private static readonly List<LedgerEntry> _entries = new List<LedgerEntry>();
        private static LogEntry _lastSeenEntry;
        private static int _lastArchiveTick = 0;
        private static int _recordingDay;

        private static readonly FieldInfo _initiatorField =
            AccessTools.Field(typeof(PlayLogEntry_Interaction), "initiator");

        public static void Record(Map map, int hourOfDay)
        {
            _recordingDay = GenDate.DaysPassed;

            var entry = new LedgerEntry { HourOfDay = hourOfDay };

            var colonists = map.mapPawns?.FreeColonists?.ToList();
            if (colonists != null)
            {
                foreach (var p in colonists)
                {
                    if (p == null) continue;
                    string activity = p.jobs?.curDriver?.GetReport()?.CapitalizeFirst() ?? "idle";
                    int mood = Mathf.RoundToInt((p.needs?.mood?.CurLevel ?? 0.5f) * 100f);
                    entry.Pawns.Add((p.LabelShort ?? "?", activity, mood));
                }
            }

            var archiveEvents = GetNewArchiveEntries();
            var logEvents     = GetNewLogEntries();
            entry.Events.AddRange(archiveEvents);
            entry.Events.AddRange(logEvents);
            _entries.Add(entry);
        }

        public static string Compile(Map map)
        {
            var sb = new StringBuilder();

            string colony = map.info?.parent?.Label ?? "Unnamed Colony";
            long absTick = Find.TickManager.TicksAbs;
            float lon = Find.WorldGrid?.LongLatOf(map.Tile).x ?? 0f;
            int year = GenDate.Year(absTick, lon);
            string season = GenLocalDate.Season(map).ToString();

            sb.AppendLine($"=== DAY {_recordingDay} CHRONICLE — {colony} ===");
            sb.AppendLine($"{season}, Year {year}");
            sb.AppendLine();

            if (_entries.Count == 0)
            {
                sb.AppendLine("(no data recorded this day)");
                return sb.ToString();
            }

            foreach (var entry in _entries)
            {
                sb.AppendLine($"[Hour {entry.HourOfDay:D2}:00]");

                if (entry.Pawns.Count > 0)
                {
                    var parts = entry.Pawns.Select(p => $"{p.Name} — {p.Activity} (mood {p.MoodPct}%)");
                    sb.AppendLine($"  Colonists: {string.Join(", ", parts)}");
                }

                if (entry.Events.Count > 0)
                {
                    sb.AppendLine("  Events:");
                    foreach (var evt in entry.Events)
                        sb.AppendLine($"    - {evt}");
                }
                else
                {
                    sb.AppendLine("  Events: (none)");
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        public static void Clear()
        {
            _entries.Clear();
            // _lastSeenEntry and _lastArchiveTick intentionally kept — continue tracking from current position
        }

        private static List<string> GetNewArchiveEntries()
        {
            var archive = Find.Archive;
            if (archive == null) return new List<string>();

            int currentTick = Find.TickManager.TicksGame;
            var result = new List<string>();

            foreach (var item in archive.ArchivablesListForReading
                .Where(i => i.CreatedTicksGame > _lastArchiveTick)
                .OrderBy(i => i.CreatedTicksGame))
            {
                string label = item.ArchivedLabel;
                if (!label.NullOrEmpty()) result.Add($"[Event] {label}");
            }

            _lastArchiveTick = currentTick;
            return result;
        }

        private static List<string> GetNewLogEntries()
        {
            var all = Find.PlayLog?.AllEntries;
            if (all == null || all.Count == 0) return new List<string>();

            var result = new List<string>();
            foreach (var entry in all)  // newest first
            {
                if (entry == _lastSeenEntry) break;
                try
                {
                    string text = EntryToString(entry);
                    if (!text.NullOrEmpty()) result.Add(text.CapitalizeFirst());
                }
                catch { }
            }

            _lastSeenEntry = all[0];
            result.Reverse();  // oldest first
            return result;
        }

        private static string EntryToString(LogEntry entry)
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
