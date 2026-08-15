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
    internal class PawnHealthSnapshot
    {
        public int   HealthPct;
        public int   BleedRatePct;
        public float HoursUntilDeath;
        public List<(string Source, int Pct)>                             Injuries   = new List<(string, int)>();
        public List<(string Label, int InfPct, int ImmPct, string SevLabel, bool Lethal)> Diseases = new List<(string, int, int, string, bool)>();
        public List<(string Label, int SevPct, string SevLabel)>          Other      = new List<(string, int, string)>();
        public List<string>                                               Addictions = new List<string>();

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append(HealthPct);
            sb.Append($"|B:{BleedRatePct}:{HoursUntilDeath:F1}");
            foreach (var i in Injuries)    sb.Append($"|I:{Esc(i.Source)}:{i.Pct}");
            foreach (var d in Diseases)    sb.Append($"|D:{Esc(d.Label)}:{d.InfPct}:{d.ImmPct}:{Esc(d.SevLabel)}:{(d.Lethal ? 1 : 0)}");
            foreach (var o in Other)       sb.Append($"|O:{Esc(o.Label)}:{o.SevPct}:{Esc(o.SevLabel)}");
            foreach (var a in Addictions)  sb.Append($"|A:{Esc(a)}");
            return sb.ToString();
        }

        public static PawnHealthSnapshot Deserialize(string s)
        {
            var snap = new PawnHealthSnapshot();
            if (s.NullOrEmpty()) return snap;
            var records = s.Split('|');
            if (records.Length > 0 && int.TryParse(records[0], out int hp)) snap.HealthPct = hp;
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
                    case 'I': if (f.Length >= 2 && int.TryParse(f[1], out int ipct)) snap.Injuries.Add((Unesc(f[0]), ipct)); break;
                    case 'D': if (f.Length >= 5 && int.TryParse(f[1], out int inf) && int.TryParse(f[2], out int imm)) snap.Diseases.Add((Unesc(f[0]), inf, imm, Unesc(f[3]), f[4] == "1")); break;
                    case 'O': if (f.Length >= 3 && int.TryParse(f[1], out int spct)) snap.Other.Add((Unesc(f[0]), spct, Unesc(f[2]))); break;
                    case 'A': if (f.Length >= 1) snap.Addictions.Add(Unesc(f[0])); break;
                }
            }
            return snap;
        }

        private static string Esc(string s)   => s?.Replace("%", "%25").Replace("|", "%7C").Replace(":", "%3A") ?? "";
        private static string Unesc(string s)  => s?.Replace("%3A", ":").Replace("%7C", "|").Replace("%25", "%") ?? "";
    }

    // Takes daily colonist/captive health snapshots and renders the day-over-day diff prose used
    // in the COLONIST HEALTH / PRISONER-SLAVE HEALTH sections. Extracted from ColonyLedger.
    internal class PawnHealthTracker
    {
        private Dictionary<string, PawnHealthSnapshot> _prevDayHealth = new Dictionary<string, PawnHealthSnapshot>();

        // Snapshots p's health, diffs it against yesterday's stored snapshot, records it into
        // currentHealth (today's running batch — not committed to _prevDayHealth until CommitDay),
        // and returns the two rendered lines for the journal section.
        //
        // Looks up "yesterday" from _prevDayHealth (not currentHealth) BEFORE writing today's
        // snapshot into currentHealth — calling this for multiple pawns in the same batch before
        // CommitDay is safe and each call still compares against the real prior day, never against
        // another pawn's (or its own) just-written entry from this same batch.
        public (string OverallLine, string Conditions) DescribeAndSnapshot(Pawn p, Dictionary<string, PawnHealthSnapshot> currentHealth)
        {
            string id   = p.ThingID ?? p.LabelShort ?? "?";
            var    snap = TakePawnHealthSnapshot(p);
            _prevDayHealth.TryGetValue(id, out PawnHealthSnapshot prev);
            currentHealth[id] = snap;

            string overallLine = $"Overall: {snap.HealthPct}%";
            if (prev != null && prev.HealthPct != snap.HealthPct)
            {
                int hDelta = snap.HealthPct - prev.HealthPct;
                overallLine += $" ({(hDelta < 0 ? "decreased" : "increased")} by {Math.Abs(hDelta)}% today)";
            }
            if (snap.BleedRatePct > 0 && snap.HoursUntilDeath > 0f)
                overallLine += $" — {snap.HoursUntilDeath:F1}h until death";

            if (p.Dead)               overallLine += " — Dead";
            else if (p.Downed)        overallLine += " — Downed";
            else if (p.InMentalState) overallLine += $" — Mental break ({p.MentalStateDef?.label ?? "unknown"})";

            string conditions = RenderHealthConditions(snap, prev);
            if (conditions.NullOrEmpty() && snap.BleedRatePct == 0 && snap.HoursUntilDeath == 0f)
                overallLine += " — Healthy";

            return (overallLine, conditions);
        }

        // Call once per day, after every DescribeAndSnapshot for that day has run (colonists AND
        // captives) — commits the whole batch as tomorrow's "yesterday".
        public void CommitDay(Dictionary<string, PawnHealthSnapshot> currentHealth) => _prevDayHealth = currentHealth;

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
                        if (ticks < float.MaxValue / 2f) snap.HoursUntilDeath = ticks / GenDate.TicksPerHour;
                    }
                    catch { }
                }

                var bad = hediffSet.hediffs.Where(h => h.Visible && h.def.isBad && !(h is Hediff_MissingPart)).ToList();
                if (!bad.Any()) return snap;

                var injuryList = bad.OfType<Hediff_Injury>().ToList();
                float totalSev = injuryList.Sum(h => h.Severity);
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

                foreach (var h in rest.Where(h => !(h is Hediff_Addiction) && h.def.HasComp(typeof(HediffComp_Immunizable))))
                {
                    var immunComp = h.TryGetComp<HediffComp_Immunizable>();
                    snap.Diseases.Add((h.def.LabelCap, Mathf.RoundToInt(h.Severity * 100f),
                        Mathf.RoundToInt((immunComp?.Immunity ?? 0f) * 100f), h.CurStage?.label ?? "", h.def.lethalSeverity >= 0f));
                }

                foreach (var h in rest.Where(h => !(h is Hediff_Addiction) && !h.def.HasComp(typeof(HediffComp_Immunizable))))
                    snap.Other.Add((h.def.LabelCap, Mathf.RoundToInt(h.Severity * 100f), h.CurStage?.label ?? ""));
            }
            catch { }
            return snap;
        }

        private static string RenderHealthConditions(PawnHealthSnapshot snap, PawnHealthSnapshot prev)
        {
            if (!snap.Injuries.Any() && !snap.Diseases.Any() && !snap.Other.Any() && !snap.Addictions.Any())
                return "Healthy";

            var parts = new List<string>();

            var prevInj = prev?.Injuries.GroupBy(i => i.Source).ToDictionary(g => g.Key, g => g.First().Pct);
            foreach (var inj in snap.Injuries)
            {
                string type   = string.Equals(inj.Source, "fire", StringComparison.OrdinalIgnoreCase) ? "Burns" : "Injuries";
                string header = inj.Source == "scar"     ? "Old scars"
                              : inj.Source.NullOrEmpty() ? $"{type} (unknown cause)"
                              : $"{type} from {inj.Source}";
                string detail = prevInj != null && prevInj.TryGetValue(inj.Source, out int pp) && pp != inj.Pct
                    ? $"{inj.Pct}%, {(inj.Pct > pp ? "increased" : "decreased")} by {Math.Abs(inj.Pct - pp)}% today"
                    : $"{inj.Pct}%";
                parts.Add($"{header} - ({detail})");
            }

            var prevDis = prev?.Diseases.GroupBy(d => d.Label).ToDictionary(g => g.Key, g => g.First());
            foreach (var d in snap.Diseases)
            {
                string header = d.SevLabel.NullOrEmpty() ? d.Label : $"{d.Label}, {d.SevLabel}";
                string infStr, immStr;
                if (prevDis != null && prevDis.TryGetValue(d.Label, out var pd))
                {
                    infStr = FieldDelta("affliction", d.InfPct, d.InfPct - pd.InfPct);
                    immStr = FieldDelta("immunity",   d.ImmPct, d.ImmPct - pd.ImmPct);
                }
                else
                {
                    infStr = $"affliction {d.InfPct}%";
                    immStr = $"immunity {d.ImmPct}%";
                }
                parts.Add($"{header} - ({infStr}, {immStr})");
            }

            var prevOth = prev?.Other.GroupBy(o => o.Label).ToDictionary(g => g.Key, g => g.First());
            foreach (var o in snap.Other)
            {
                string header = o.SevLabel.NullOrEmpty() ? o.Label : $"{o.Label}, {o.SevLabel}";
                string detail = prevOth != null && prevOth.TryGetValue(o.Label, out var po) && po.SevPct != o.SevPct
                    ? $"{o.SevPct}%, {(o.SevPct > po.SevPct ? "increased" : "decreased")} by {Math.Abs(o.SevPct - po.SevPct)}% today"
                    : $"{o.SevPct}%";
                parts.Add($"{header} - ({detail})");
            }

            foreach (var a in snap.Addictions) parts.Add($"Addicted to {a}");

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
            if (h.def?.defName == "Burn") return "fire";
            if (h.TryGetComp<HediffComp_GetsPermanent>()?.IsPermanent == true) return "scar";
            return "";
        }

        // ── Save / load ───────────────────────────────────────────────────────
        // "prevDayHealth" key must stay exactly as-is for existing-save compatibility.

        public void ExposeData()
        {
            bool saving = Scribe.mode == LoadSaveMode.Saving;
            var health = saving ? _prevDayHealth.ToDictionary(kv => kv.Key, kv => kv.Value.Serialize()) : null;

            Scribe_Collections.Look(ref health, "prevDayHealth", LookMode.Value, LookMode.Value);

            if (Scribe.mode != LoadSaveMode.LoadingVars) return;

            _prevDayHealth = new Dictionary<string, PawnHealthSnapshot>();
            if (health != null)
                foreach (var kv in health)
                    _prevDayHealth[kv.Key] = PawnHealthSnapshot.Deserialize(kv.Value);
        }
    }
}
