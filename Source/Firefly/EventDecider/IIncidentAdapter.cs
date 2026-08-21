using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Firefly
{
    // One adapter per incident family the Event Decider is allowed to steer. Only a small
    // hand-verified set is safe here — arbitrary/modded incidents have no shared parameter
    // contract and must never be nudged blind (see project design notes, 2026-08-21: most of an
    // incident's real specifics are resolved inside its own execution code, not in the shared
    // IncidentParms bag, and pre-set fields aren't guaranteed to be re-validated by the incident
    // itself). Each adapter owns exactly the fields it has actually verified are honored by that
    // incident's own code — Validate is not optional decoration, it's the thing standing between
    // a stale/unsafe LLM suggestion and a mutated live IncidentParms.
    public interface IIncidentAdapter
    {
        // Which IncidentDef(s) this adapter covers — Patch_EventDeciderIntercept's registry keys
        // off these. Keyed by IncidentDef rather than IncidentWorker's C# Type because RimWorld
        // sometimes shares one worker class across several defs of different narrative weight
        // (e.g. multiple disease defs on one generic worker) — an adapter's honored-field
        // verification was done against specific defs' actual behavior, not a worker class in
        // the abstract, so registration has to be that precise too.
        IEnumerable<IncidentDef> CoveredDefs { get; }

        // Field keys this adapter can steer, exposed so the parameter-selection prompt can tell
        // the LLM what's actually allowed — the LLM never touches raw C# field names beyond these
        // string keys, and EventDecisionResult.ProposedValues is itself string-keyed/string-valued
        // (see EventDecisionResult.cs) for the same reason: keep the LLM boundary narrow and
        // legible, let each adapter's own Validate/Apply do the real typed work.
        IReadOnlyList<string> HonoredFields { get; }

        // Exact live choices offered to the LLM for each field. A bare field name is not enough
        // for execution-time values such as disease victims.
        IReadOnlyDictionary<string, string> DescribeAllowedFields(IncidentWorker worker,
            IncidentParms current);

        // Re-validates a single proposed field value against the incident's own real, current
        // requirements (still-hostile faction, still-eligible strategy/arrival mode, etc.) before
        // it's ever applied. Never trust that a value merely existing or being well-formed makes
        // it safe — the incident's own CanFireNow-style checks don't necessarily re-examine every
        // preset field, and by the time this runs the game state may have moved on since the LLM
        // call started.
        bool Validate(IncidentWorker worker, IncidentParms current, string fieldName,
            string proposedValue);

        // Mutates parms with values that already passed Validate — called once, with every field
        // that survived validation. Implementations should still be defensive about field names
        // they don't recognize (skip, don't throw) since HonoredFields is the source of truth for
        // what's offered to the LLM, not a hard guarantee about what callers will pass here.
        void Apply(IncidentWorker worker, IncidentParms parms,
            IReadOnlyDictionary<string, string> validatedValues);
    }
}
