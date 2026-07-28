using System;
using System.Collections.Generic;
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

    // Incident pacing only. The colony journal is driven by JournalRecorder from
    // FireflyGameComponent — see the note there for why the two were separated.
    public class StorytellerComp_Fillion : StorytellerComp
    {
        private List<StorytellerComp> _delegateComps;
        private string _lastCurve;

        private List<StorytellerComp> GetDelegateComps()
        {
            string curve = FireflyMod.Settings.IncidentCurve ?? "Cassandra";
            if (_delegateComps != null && _lastCurve == curve) return _delegateComps;

            _lastCurve = curve;
            _delegateComps = new List<StorytellerComp>();
            if (curve == "None") return _delegateComps;

            var def = DefDatabase<StorytellerDef>.GetNamedSilentFail(curve);
            if (def?.comps == null) return _delegateComps;

            foreach (var p in def.comps)
            {
                try
                {
                    var comp = (StorytellerComp)Activator.CreateInstance(p.compClass);
                    comp.props = p;
                    comp.Initialize();
                    _delegateComps.Add(comp);
                }
                catch (Exception e)
                {
                    Log.Warning($"[Firefly] Failed to init delegate comp {p.compClass?.Name}: {e.Message}");
                }
            }

            Log.Message($"[Firefly] Incident curve: {curve} ({_delegateComps.Count} comps loaded)");
            return _delegateComps;
        }

        // Called once per incident target per storyteller tick. Delegates unconditionally: the
        // vanilla comps accumulate their own MTB/interval state and expect every call.
        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            foreach (var comp in GetDelegateComps())
            {
                IEnumerable<FiringIncident> incidents = null;
                try { incidents = comp.MakeIntervalIncidents(target); } catch { }
                if (incidents == null) continue;
                foreach (var fi in incidents)
                    if (fi != null) yield return fi;
            }
        }
    }
}
