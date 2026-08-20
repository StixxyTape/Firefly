using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace Firefly
{
    public class FactionRelationSnapshot : IExposable
    {
        public int OtherFactionLoadId;
        public string OtherFactionName = "";
        public string Kind = "";
        public int Goodwill;

        public void ExposeData()
        {
            Scribe_Values.Look(ref OtherFactionLoadId, "otherFactionLoadId", 0);
            Scribe_Values.Look(ref OtherFactionName, "otherFactionName", "");
            Scribe_Values.Look(ref Kind, "kind", "");
            Scribe_Values.Look(ref Goodwill, "goodwill", 0);
        }
    }

    public class FactionMemeSnapshot : IExposable
    {
        public string Label = "";
        public string Description = "";

        public void ExposeData()
        {
            Scribe_Values.Look(ref Label, "label", "");
            Scribe_Values.Look(ref Description, "description", "");
        }
    }

    public class FactionSettlementSnapshot : IExposable
    {
        public int WorldObjectId;
        public string Name = "";
        public int Tile = -1;
        public string Location = "";

        public void ExposeData()
        {
            Scribe_Values.Look(ref WorldObjectId, "worldObjectId", 0);
            Scribe_Values.Look(ref Name, "name", "");
            Scribe_Values.Look(ref Tile, "tile", -1);
            Scribe_Values.Look(ref Location, "location", "");
        }
    }

    // Ironclad status captured once when the faction first exists in the generated world.
    public class FactionStatusSnapshot : IExposable
    {
        public string TechLevel = "";
        public string Species = "";
        public string LeaderName = "";
        public string LeaderTitle = "";
        public string ReligionName = "";
        // An Ideo has 1-4 memes (exactly one "structure" meme plus up to three "normal" ones) —
        // empty whenever ReligionName is, i.e. Ideology isn't installed or the faction has none.
        public List<FactionMemeSnapshot> ReligionMemes = new List<FactionMemeSnapshot>();
        public List<FactionSettlementSnapshot> ActiveSettlements = new List<FactionSettlementSnapshot>();
        public List<FactionRelationSnapshot> Relationships = new List<FactionRelationSnapshot>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref TechLevel, "techLevel", "");
            Scribe_Values.Look(ref Species, "species", "");
            Scribe_Values.Look(ref LeaderName, "leaderName", "");
            Scribe_Values.Look(ref LeaderTitle, "leaderTitle", "");
            Scribe_Values.Look(ref ReligionName, "religionName", "");
            Scribe_Collections.Look(ref ReligionMemes, "religionMemes", LookMode.Deep);
            Scribe_Collections.Look(ref ActiveSettlements, "activeSettlements", LookMode.Deep);
            Scribe_Collections.Look(ref Relationships, "relationships", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (ReligionMemes == null) ReligionMemes = new List<FactionMemeSnapshot>();
                if (ActiveSettlements == null) ActiveSettlements = new List<FactionSettlementSnapshot>();
                if (Relationships == null) Relationships = new List<FactionRelationSnapshot>();
            }
        }
    }

    public class FactionSnapshot : IExposable
    {
        public int FactionLoadId;
        public string Key = "";
        public string FactionName = "";
        public FactionStatusSnapshot Status = new FactionStatusSnapshot();

        // Event-driven story — what's happened to this faction, added by Faction Update from
        // World Thread activity. Starts genuinely empty; there's nothing to narrate until
        // something in the world actually touches this faction.
        public JournalRecord NarrativeJournal = new JournalRecord();

        // Stable characterization — seeded at bootstrap, then extended directly by identity facts
        // selected by Faction Update.
        public JournalRecord FactionJournal = new JournalRecord();

        public string Tagline = "";
        public int TaglineCoveredNarrativeRevision;
        // Faction Update can also write identity facts (rare, permanent changes to who a faction
        // is) straight into FactionJournal — the tagline needs to react to those too, not just
        // narrative facts.
        public int TaglineCoveredFactionRevision;

        public string NarrativeSummary => NarrativeJournal.ActiveSummary;
        public List<JournalFact> NarrativeFacts => NarrativeJournal.Facts;
        public string Description => FactionJournal.ActiveSummary;
        public List<JournalFact> FactionFacts => FactionJournal.Facts;
        public long LastTouchedTick => NarrativeJournal.LastTouchedTick;
        public bool TaglineStale => Tagline.NullOrEmpty() ||
            NarrativeJournal.FactRevision > TaglineCoveredNarrativeRevision ||
            FactionJournal.FactRevision > TaglineCoveredFactionRevision;

        public void ExposeData()
        {
            Scribe_Values.Look(ref FactionLoadId, "factionLoadId", 0);
            Scribe_Values.Look(ref Key, "key", "");
            Scribe_Values.Look(ref FactionName, "factionName", "");
            Scribe_Values.Look(ref Tagline, "tagline", "");
            Scribe_Values.Look(ref TaglineCoveredNarrativeRevision, "taglineCoveredNarrativeRevision", 0);
            Scribe_Values.Look(ref TaglineCoveredFactionRevision, "taglineCoveredFactionRevision", 0);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // Migrate the earlier live-snapshot layout without discarding its journal prose.
                string tech = "", leader = "", leaderTitle = "", background = "", summary = "";
                List<FactionRelationSnapshot>? relations = null;
                List<JournalFact>? facts = null;
                long touched = 0L;
                bool stale = false;
                Scribe_Values.Look(ref tech, "techLevel", "");
                Scribe_Values.Look(ref leader, "leaderName", "");
                Scribe_Values.Look(ref leaderTitle, "leaderTitle", "");
                Scribe_Collections.Look(ref relations, "relations", LookMode.Deep);
                Scribe_Collections.Look(ref facts, "facts", LookMode.Deep);
                Scribe_Values.Look(ref background, "background", "");
                Scribe_Values.Look(ref summary, "summary", "");
                Scribe_Values.Look(ref touched, "lastTouchedTick", 0L);
                Scribe_Values.Look(ref stale, "summaryStale", false);
                FactionStatusSnapshot? loadedStatus = null;
                // Pre-split saves only ever had one journal — that always represented the
                // event-driven story (the old combined Facts/Active Summary that Faction Update
                // wrote to), so it migrates into NarrativeJournal. FactionJournal starts empty for
                // existing factions; it gets populated going forward by direct identity facts,
                // while brand-new factions get it from the bootstrap call.
                JournalRecord? loadedNarrative = null;
                Scribe_Deep.Look(ref loadedStatus, "status");
                Scribe_Deep.Look(ref loadedNarrative, "journal");
                if (loadedStatus == null)
                    Status = new FactionStatusSnapshot
                    {
                        TechLevel = tech ?? "",
                        LeaderName = leader ?? "",
                        LeaderTitle = leaderTitle ?? "",
                        Relationships = relations ?? new List<FactionRelationSnapshot>(),
                    };
                else Status = loadedStatus;
                if (loadedNarrative == null)
                {
                    int revision = facts?.Count ?? 0;
                    NarrativeJournal = new JournalRecord
                    {
                        ActiveSummary = summary ?? "",
                        Facts = facts ?? new List<JournalFact>(),
                        FactRevision = revision,
                        SummarizedRevision = stale ? 0 : revision,
                        LastTouchedTick = touched,
                    };
                }
                else NarrativeJournal = loadedNarrative;
                JournalRecord? loadedFaction = null;
                Scribe_Deep.Look(ref loadedFaction, "factionJournal");
                if (loadedFaction == null)
                {
                    // First time this save sees the split: there was no separate identity journal.
                    FactionJournal = new JournalRecord();
                }
                else FactionJournal = loadedFaction;
            }
            else
            {
                Scribe_Deep.Look(ref Status, "status");
                Scribe_Deep.Look(ref NarrativeJournal, "journal");
                Scribe_Deep.Look(ref FactionJournal, "factionJournal");
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (Status == null) Status = new FactionStatusSnapshot();
                if (NarrativeJournal == null) NarrativeJournal = new JournalRecord();
                if (FactionJournal == null) FactionJournal = new JournalRecord();
                TaglineCoveredNarrativeRevision = Math.Max(0,
                    Math.Min(TaglineCoveredNarrativeRevision, NarrativeJournal.FactRevision));
                TaglineCoveredFactionRevision = Math.Max(0,
                    Math.Min(TaglineCoveredFactionRevision, FactionJournal.FactRevision));
            }
        }

        public Faction? ResolveFaction() => Find.FactionManager?.AllFactionsListForReading?
            .FirstOrDefault(f => f != null && f.loadID == FactionLoadId);

        private string BuildPromptBlock(bool includeDescription)
        {
            var lines = new List<string>
            {
                $"Faction: {FactionName} (key: {Key})",
                $"Species: {(Status.Species.NullOrEmpty() ? "Unknown" : Status.Species)}",
                $"Tech level: {Status.TechLevel}",
                $"Leader: {(Status.LeaderName.NullOrEmpty() ? "(no known leader)" : $"{Status.LeaderTitle} {Status.LeaderName}".Trim())}",
                $"Religion: {(Status.ReligionName.NullOrEmpty() ? "(none known)" : Status.ReligionName)}",
                $"Active settlements: {Status.ActiveSettlements.Count}",
            };
            lines.Add("Ideological beliefs:");
            lines.AddRange(Status.ReligionMemes.Count == 0
                ? new[] { "  - (none)" }
                : Status.ReligionMemes.Select(m => $"  - {m.Label}: {m.Description}"));
            // Kind only, no numeric goodwill — same "no meta figures" rule the LLM prompts
            // already state elsewhere (goodwill is a mechanic; Kind is its situational
            // equivalent). Feeds both Faction Facts bootstrap and every later call that shares
            // this block (World Seed, Faction Update), not just bootstrap.
            lines.Add("Relationships:");
            lines.AddRange(Status.Relationships.Count == 0
                ? new[] { "  - (none)" }
                : Status.Relationships.Select(r => $"  - {r.OtherFactionName}: {r.Kind}"));
            if (includeDescription)
                lines.Add($"Description: {(Description.NullOrEmpty() ? "(none yet)" : Description)}");
            return string.Join("\n", lines);
        }

        // No description yet at bootstrap time (that's what this call is generating) — status
        // fields only.
        public string ToStatusPromptBlock() => BuildPromptBlock(includeDescription: false);

        // Used post-bootstrap (World Seed, Faction Update) once Description exists.
        public string ToPromptBlock() => BuildPromptBlock(includeDescription: true);

        // Core status only — Religion and Relationships are split into their own sections (see
        // ToReligionLines/ToRelationshipLines) for cleaner parsing/display, not folded in here.
        public List<string> ToStatusLines()
        {
            var lines = new List<string>
            {
                $"Species: {(Status.Species.NullOrEmpty() ? "Unknown" : Status.Species)}.",
                $"Technology level: {(Status.TechLevel.NullOrEmpty() ? "Unknown" : Status.TechLevel)}.",
                Status.LeaderName.NullOrEmpty() ? "No known leader." : $"Led by {$"{Status.LeaderTitle} {Status.LeaderName}".Trim()}.",
            };
            lines.Add(Status.ActiveSettlements.Count == 1
                ? "1 active settlement."
                : $"{Status.ActiveSettlements.Count} active settlements.");
            return lines;
        }

        public List<string> ToReligionLines()
        {
            if (Status.ReligionName.NullOrEmpty())
                return new List<string> { "No active religion recorded." };
            var lines = new List<string> { $"Active religion: {Status.ReligionName}." };
            if (Status.ReligionMemes.Count > 0)
            {
                lines.Add("Ideological beliefs:");
                lines.AddRange(Status.ReligionMemes.Select(m => $"{m.Label}: {m.Description}"));
            }
            return lines;
        }

        public List<string> ToRelationshipLines()
        {
            var lines = new List<string>
            {
                Status.Relationships.Count == 0 ? "No recorded relationships." : "Relationships:",
            };
            lines.AddRange(Status.Relationships.Select(r => $"{r.OtherFactionName}: {r.Kind}."));
            return lines;
        }
    }
}
