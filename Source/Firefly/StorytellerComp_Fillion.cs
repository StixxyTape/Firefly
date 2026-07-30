using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace Firefly
{
    public class StorytellerCompProperties_Fillion : StorytellerCompProperties
    {
        public StorytellerCompProperties_Fillion()
        {
            compClass = typeof(StorytellerComp_Fillion);
        }
    }

    public class StorytellerComp_Fillion : StorytellerComp
    {
        private string _activeCurve;
        private List<StorytellerComp> _comps;

        private void EnsureComps()
        {
            string curve = FireflyMod.Settings.IncidentCurve ?? "None";
            if (_comps != null && _activeCurve == curve) return;
            BuildComps(curve);
        }

        private void BuildComps(string curve)
        {
            _activeCurve = curve;
            _comps = new List<StorytellerComp>();
            if (curve == "None") return;

            var curveDef = DefDatabase<StorytellerDef>.GetNamedSilentFail(curve);
            if (curveDef?.comps == null) return;

            // Fillion inherits DLC comps from BaseStoryteller via XML inheritance. Any
            // comp type already on Fillion's own def would double-fire if we also delegate
            // it, so skip types we're already running natively.
            var fillionDef = DefDatabase<StorytellerDef>.GetNamed("Fillion");
            var ownTypes = fillionDef?.comps?
                .Select(p => p.compClass)
                .Where(t => t != null)
                .ToHashSet()
                ?? new HashSet<Type>();

            foreach (var p in curveDef.comps)
            {
                if (p.compClass == null) continue;
                if (ownTypes.Contains(p.compClass)) continue;

                try
                {
                    var comp = (StorytellerComp)Activator.CreateInstance(p.compClass);
                    comp.props = p;
                    comp.Initialize();
                    _comps.Add(comp);
                }
                catch (Exception e)
                {
                    Log.Warning($"[Firefly] Failed to init delegate comp {p.compClass.Name}: {e.Message}");
                }
            }

            Log.Message($"[Firefly] Incident curve: {curve} ({_comps.Count} delegate comps)");
        }

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            EnsureComps();
            foreach (var comp in _comps)
            {
                List<FiringIncident> incidents = null;
                try { incidents = comp.MakeIntervalIncidents(target)?.ToList(); } catch { }
                if (incidents == null) continue;
                foreach (var fi in incidents)
                    if (fi != null) yield return fi;
            }
        }
    }
}
