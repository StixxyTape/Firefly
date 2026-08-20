using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace Firefly
{
    public static class WorldThreadScanIngest
    {
        public static readonly string FactionFactsSystemPrompt =
            "You are Fillion, storyteller of a distant earth-like rimworld. You will be fed details of the factions currently existing on this world — their description, technology, leadership, religion, ideology, settlements, and relationships. Your job is to break these details into concise facts about what each faction is like, along with a one-sentence tagline.\n\n" +
            "Facts must be short, self-contained statements. Each fact should take something you were given and state what it means in practice — how it shapes their behaviour, what they value, how they'd treat an outsider. Read carefully and draw out implications: a title like \"First Chair\" suggests a council rather than a throne; a collectivist belief suggests individual ambition reads to them as failure; a long hostility list made up of scattered bands rather than states tells you they fight raiders, not rivals.\n\n" +
            "You may also add a little lore — origins, old grudges, how things came to be how they are — but always frame invented history as rumour, reputation, or common story, never as certain fact: \"it's said they descend from a mining fleet's mutineers\"; \"the story goes that their first Chair was elected at gunpoint.\" What you were given is fact; what you add is hearsay.\n\n" +
            "State relationships plainly without inventing their causes — \"they are at war with Mugpog\" needs no explanation yet. The reasons behind wars and friendships will reveal themselves in time.\n\n" +
            "Where two factions share a similar template or description, differentiate them — give them different concerns, different temperaments, different reputations. No two factions should read as copies.\n\n" +
            "Write plainly — focused and concise.\n\n" +
            "Example facts:\n\n" +
            "\"The Northwestern Confederation lost most of the technology that brought them here generations ago, and have rebuilt to simple machinery and gunpowder weapons.\"\n" +
            "\"They believe each person is part of a greater whole, and that a life is best spent playing your part for the group.\"\n" +
            "\"First Chair Isla Walter leads them — a title suggesting a council, not a throne.\"\n" +
            "\"It's said the Confederation's founders were the ship's passenger manifest, not its crew — and that they've distrusted self-appointed leaders ever since.\"\n" +
            "\"They keep civil terms with the other large powers and reserve hostility for raiders and outcasts.\"\n\n" +
            "The tagline is one concise sentence capturing what this faction is and what currently occupies them — a working description for other narrators, not a motto.\n\n" +
            "Return only JSON in this exact shape: {\"factions\":[{\"faction_key\":\"exact-key\",\"facts\":[\"string\"],\"tagline\":\"string\"}]}";

        public static readonly string SeedSystemPrompt =
            "You are Fillion, storyteller of a colony on a distant earth-like Rimworld. Your job is to create exactly three to six World Threads about ongoing plots happening on the world. " +
            "You will receive information about every faction currently existing on the world to be used as fuel for the world threads — whatever you write must be consistent with the data you are given. Every thread needs a concise name, some establishing facts, and an initial summary of what has happened or is happening. " +
            "Facts must be short, self-contained statements of what actually happened. When writing a fact, make sure to include the full names of any pre-existing factions or entities involved, but if something does not exist in the data you have been given, keep it generic — do not refer to things specifically, only with general descriptions. " +
            "Keep facts short and focused, giving context to the thread as well as its current state. Feel free to make things interesting — rumors spreading, spicy twists, and even intersections with other world threads. World threads can involve one faction, many factions, or even no factions at all, and can be something completely separate, such as powerful beings, ancient artifacts, or world-ending threats. " +
            "Feel free to worldbuild and make things up, but with one caveat: anything you write must be consistent and coherent with the data you are given about the world's present state. " +
            "Example facts: " +
            "\"The Frostmark alliance, a legendary peace treaty spearheaded 300 years ago by the leaders of Ixia and Lance-Cor, has recently fallen into turmoil.\" " +
            "\"The king of Ixia has been assassinated, and fingers are being pointed at Lance-Cor for being the prime suspects, but the diplomats of Lance-Cor claim they were framed.\" " +
            "\"Tensions are high as Ixia has recently called meetings with Lance-Cor, with rumors spreading that the prince of Ixia will attend in the King's absence.\" " +
            "\"The Northwestern Confederation has publicly stated they will serve as mediators to ensure things don't get out of hand.\" " +
            "The initial summary — three lines maximum — should capture what's known so far about the thread, summing up all the important details of the facts in a focused and concise manner. " +
            "Example new-thread summary: " +
            "\"The king of Ixia has fallen, jeopardising the legendary Frostmark alliance founded 300 years ago between them and Lance-Cor. Ixia believes Lance-Cor had something to do with the King's assassination and relations have fallen — but talks have been arranged between the two factions that will soon take place, with the prince of Ixia attending in the King's stead. The Northwestern Confederation has announced they will serve as mediators for the talks to keep things in check.\" " +
            "Remember to stay grounded in the existing factions, relations, leaders, and statuses you will be given. " +
            "Return only JSON in this exact shape: {\"new_threads\":[{\"name\":\"string\",\"summary\":\"string\",\"facts\":[\"string\"]}],\"updates\":[]}. The Frostmark alliance thread above, for instance, would be returned as: " +
            "{\"new_threads\":[{\"name\":\"The Fall of the Frostmark Alliance\",\"summary\":\"The king of Ixia has fallen, jeopardising the legendary Frostmark alliance founded 300 years ago between them and Lance-Cor. Ixia believes Lance-Cor had something to do with the King's assassination and relations have fallen — but talks have been arranged between the two factions that will soon take place, with the prince of Ixia attending in the King's stead. The Northwestern Confederation has announced they will serve as mediators for the talks to keep things in check.\",\"facts\":[\"The Frostmark alliance, a legendary peace treaty spearheaded 300 years ago by the leaders of Ixia and Lance-Cor, has recently fallen into turmoil.\",\"The king of Ixia has been assassinated, and fingers are being pointed at Lance-Cor for being the prime suspects, but the diplomats of Lance-Cor claim they were framed.\",\"Tensions are high as Ixia has recently called meetings with Lance-Cor, with rumors spreading that the prince of Ixia will attend in the King's absence.\",\"The Northwestern Confederation has publicly stated they will serve as mediators to ensure things don't get out of hand.\"]}],\"updates\":[]}. " +
            "Create three to six threads total this way, each its own object in new_threads, and spread them across a genuinely varied range of scale and character rather than repeating the same shape twice.";

        public static readonly string WorldOutcomeSystemPrompt =
            "You are Fillion, storyteller of a distant earth-like rimworld. Each day you chronicle what happened in the wider world - the factions, powers, forces, and narratives beyond one small colony's walls.\n\n" +
            "You will be given: the world's ongoing narrative threads, the history of the world so far, the factions and their standing, and a summary of the colony's activities over the past day.\n\n" +
            "Write what happened in the world since yesterday. The ongoing threads are your main material - most days, advance one of them a step: a position hardens, an envoy arrives, a deadline draws closer, an expedition delves deeper. Steps should be small and credible; big developments - a battle joined, a death, a pact signed or broken - must be earned by the pressure that came before. Around the threads, the rest of the world keeps its slow rhythm: caravans, rumours, seasons. New threads should be rare, born only when something genuinely new enters the world or you decide the world needs some more spice. The world does not perform; it simply continues\n\n" +
            "The colony's day is context, not material. Ignore its routine entirely. Only let it touch the world when something happened that the outer world would genuinely notice or care about - a faction's warriors killed, a title granted, a prisoner taken, or something else that could start to worry/concern outsiders. Even then, the world's reaction should be proportionate: word travels slowly, and a small colony is a small concern.\n\n" +
            "Ground everything in the factions and threads you're given. Do not invent new factions or contradict established facts.\n\n" +
            "Output a single short, focused paragraph of prose.\n\n" +
            "Example outputs:\n\n" +
            "**A normal day (one thread stepped):**\n\n" +
            "\"At the wreck site in the low hills, the Venom Team pulled their salvage crew back half a kilometre and replaced them with armed sentries — a quieter presence, but a more serious one. Baroydur observers noted the change and matched it, and the neutral settlements between them began quietly moving their livestock east. At the Concord talks, nothing moved: Sonia Mann has still given no answer on the mutual-defense clause, and the harvest draws closer. Elsewhere, the trade roads ran their usual routes, and the season turned another day toward autumn.\"\n\n" +
            "**A rarer, bigger day (earned escalation):**\n\n" +
            "\"The standoff at the derelict platform broke — not with the expected gunfire, but with weather. A dust storm drove both crews off the hills, and when it cleared, a third party had been through the wreck: tracks, cut plating, and a stripped navigation core. Baroydur and the Venom Team each accuse the other of using the storm as cover, but among the neutral settlements a different rumour moves — that someone smaller and faster beat both giants to the prize. The Concord talks paused for a day of mourning in Camiño lands, one season since the burned hamlet.\"";

        public static readonly string WorldThreadUpdatesSystemPrompt =
            "You are Fillion, chronicler of a distant earth-like rimworld. You will be given the world's ongoing threads, and a paragraph describing what happened in the wider world today. Your job is to decide two things from that account: are any world threads being updated, or are any new world threads being created.\n\n" +
            "The criteria for threads being updated: anything in today's account which advances, changes, or complicates that thread - a faction's move, a leader's decision, a shift in a standoff, a consequence landing, or anything that sets up future developments for it.\n\n" +
            "The criteria for threads being created: anything in today's account which is not connected to any current thread, but introduces a new ongoing situation in the world - a new conflict or alliance forming, a discovery, a power shifting, or events that will clearly continue beyond a single day. A passing occurrence with no future is not a thread.\n\n" +
            "Most days will update one thread at most, and new threads should be rare. It is entirely normal to return empty lists when the day's account only describes the world's routine rhythm - caravans, seasons, rumours without substance.\n\n" +
            "Facts must be short, self-contained, matter-of-fact statements of what happened. Record them plainly, as history - no wondering, no rhetorical questions, no speculation about motives unless the account itself states them as uncertain. When writing a fact, include the full names of any Characters, Factions, Items, or Entities involved.\n\n" +
            "For a brand-new thread, also write a short initial summary - 3 lines maximum - capturing the situation plainly: who is involved, what is happening, and what remains unsettled. For an update to an existing thread, do not write a summary; only report the relevant facts. That thread's full summary is written separately afterward, from its complete fact record.\n\n" +
            "Never include meta figures - goodwill values, relationship points, or similar. Describe the situational equivalent instead.\n\n" +
            "Example new-thread summary:\n" +
            "\"Baroydur and the Venom Team both claim a derelict orbital platform in the equatorial hills. Forces are gathering on both sides, and the neutral settlements nearby are preparing for a flashpoint. No shots have been fired yet.\"\n\n" +
            "Example facts:\n" +
            "\"A Baroydur survey team located a derelict orbital platform in the low hills near the equator.\"\n" +
            "\"The Venom Team withdrew their salvage crew from the wreck site and replaced them with armed sentries.\"\n" +
            "\"The Concord talks paused for a day of mourning in Camiño lands, one season after the burned hamlet.\"\n\n" +
            "Return exactly one JSON object and nothing else, with this shape:\n" +
            "{\"new_threads\":[{\"name\":\"string\",\"summary\":\"string\",\"facts\":[\"string\"]}],\"updates\":[{\"id\":\"string\",\"facts\":[\"string\"]}]}\n\n" +
            "Both arrays must always be present, using empty arrays when there is nothing to report. Every update id must exactly match an id from the existing-threads block; never use a name as an id.";

        public static readonly string FactionUpdateSystemPrompt =
            "You are Fillion, storyteller of a distant earth-like rimworld. You will be given the world threads that were newly created or updated today, along with every faction and their current state. Your job is to decide which factions are directly affected by what happened in those threads, and record it in their ledgers.\n\n" +
            "The criteria for a faction being updated: something in the threads happened _to_ them or _because of_ them — their standing shifted, they lost or gained something, their leaders or members acted, or a consequence now hangs over them. A faction is only affected when the threads give it clear, specific reason to be. Never add facts as a blanket reaction to world events, and never add the same fact to multiple factions unless each is genuinely implicated.\n\n" +
            "Most thread changes should produce zero or one faction update. It is entirely normal to return an empty list.\n\n" +
            "Facts must be short, self-contained, matter-of-fact statements — these are a faction's history, recorded plainly. Include the full names of any Characters, Factions, Items, or Entities involved. State motives only when the threads themselves state them; where they're uncertain, record the uncertainty as fact (\"their patrols have not returned to that border since\").\n\n" +
            "Separately, watch for the rare fact that permanently changes what a faction _is_ — a religion adopted or abandoned, a government overthrown or reformed, leadership changed, their purpose or way of life altered. Record these as identity facts. Most updates will have none; identity changes are rare by nature. An identity fact describes the change to who they are (\"The Cervexa League now answers to a council of three, the office of First Chair abolished\"), not the event that caused it — the event belongs in the regular facts.\n\n" +
            "Never include meta figures — goodwill values, relationship points, or similar. Describe the situational equivalent instead. Do not invent details beyond what the threads support, and do not alter a faction's fixed status.\n\n" +
            "Example regular fact:\n" +
            "\"The Cervexa League lost two scouts to the mechanoid cluster in the eastern hills. Their patrols have since avoided that stretch of border.\"\n\n" +
            "Return exactly one JSON object and nothing else, with this shape:\n" +
            "{\"updates\":[{\"faction_key\":\"string\",\"facts\":[\"string\"],\"identity_facts\":[\"string\"]}]}\n\n" +
            "The array must always be present, using an empty array when there is nothing to report. Both fact arrays must always be present per update, with identity_facts almost always empty. Every faction_key must exactly match a key from the supplied faction block.";

        public static readonly string FactionTaglineSystemPrompt =
            "You are Fillion, writing one short current-identity tagline for each faction on a distant earth-like rimworld. You will be given each faction's identity facts, their narrative history, and their previous tagline.\n\n" +
            "A tagline exists so other narrators can know who they're dealing with at a glance — what this faction is, and what currently occupies them. It is a working description, not a motto or slogan. Ground it entirely in the facts you're given; do not invent events or treat old identity facts as new developments.\n\n" +
            "Start from the previous tagline and change it only as much as the new facts require. If nothing meaningful has changed, return it unchanged.\n\n" +
            "Keep each tagline to one concise sentence. Return only JSON in this exact shape: {\"factions\":[{\"faction_key\":\"exact-key\",\"tagline\":\"string\"}]}. " +
            "Include exactly one entry for every submitted faction.";

        public static readonly string WorldArcHistorySystemPrompt =
            "You are Fillion, chronicler of a distant earth-like rimworld. You will be given the world's history so far (if one exists), followed by recent daily accounts of the wider world not yet folded in. Your job is to rewrite the history into a single updated account of the world's story.\n\n" +
            "Write it as the world's story — the powers, conflicts, and forces that shaped where things now stand. Carry forward what still matters, weave in what's new, and let quiet stretches fade as consequential events take their place. Wars begun or ended, alliances formed or broken, leaders risen or fallen, discoveries made, and the threads running between factions are the substance; caravans, seasons, and passing rumour are not.\n\n" +
            "Follow each situation to where it stands. When something began and later changed — a standoff broken, a summit concluded, a territory lost — say what became of it. Where a situation remains unsettled, leave it unsettled, so its weight is felt.\n\n" +
            "Write plainly, as history being recorded — no rhetorical questions, no wondering. Where something is unknown, state that it is unknown. The colony is one small settlement in this world: mention it only where it genuinely figured in events larger than itself.\n\n" +
            "Never include meta figures — goodwill values, relationship points, or similar. Describe the situational equivalent instead.\n\n" +
            "Keep it to a single flowing account, plain past-tense prose. No lists. 10 lines maximum.";

        public static string BuildFactionTaglinePrompt(IEnumerable<FactionSnapshot> batch)
        {
            var sb = new StringBuilder("=== FACTIONS ===\n");
            foreach (var faction in batch.Where(f => f != null))
            {
                sb.AppendLine($"=== FACTION {faction.Key} ===");
                sb.AppendLine($"faction_key: {faction.Key}");
                sb.AppendLine("Identity Facts:");
                if (faction.FactionJournal.Chunks.Count > 0)
                    foreach (var chunk in faction.FactionJournal.Chunks)
                        sb.AppendLine($"[Day {chunk.StartDay}-{chunk.EndDay}] {chunk.Summary}");
                var recentIdentity = faction.FactionFacts
                    .Skip(faction.FactionJournal.ChunkedThroughFactIndex).ToList();
                if (faction.FactionJournal.Chunks.Count == 0 && recentIdentity.Count == 0)
                    sb.AppendLine("(none yet)");
                else foreach (var fact in recentIdentity)
                    sb.AppendLine($"[Day {fact.Day}] {fact.Text}");
                sb.AppendLine("Narrative History:");
                sb.AppendLine(faction.NarrativeSummary.NullOrEmpty() ? "(none yet)" : faction.NarrativeSummary);
                var recentNarrative = faction.NarrativeFacts
                    .Skip(faction.NarrativeJournal.ChunkedThroughFactIndex).ToList();
                if (recentNarrative.Count == 0) sb.AppendLine("Recent Narrative Facts: (none yet)");
                else
                {
                    sb.AppendLine("Recent Narrative Facts:");
                    foreach (var fact in recentNarrative)
                        sb.AppendLine($"[Day {fact.Day}] {fact.Text}");
                }
                sb.AppendLine("Previous Tagline:");
                sb.AppendLine(faction.Tagline.NullOrEmpty() ? "(none yet)" : faction.Tagline);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        // Two revision snapshots, not one — a tagline can go stale from either journal now that
        // Faction Update can write identity facts too. Both are captured at request time (not
        // read live off the faction here), so a fact that lands while this call is still in
        // flight correctly leaves the tagline stale rather than getting marked as covered.
        public static List<string> ApplyFactionTaglines(IEnumerable<FactionSnapshot> batch,
            IReadOnlyDictionary<string, int> coveredNarrativeRevisions,
            IReadOnlyDictionary<string, int> coveredFactionRevisions, string rawJson)
        {
            var targets = batch.Where(f => f != null)
                .ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
            JObject root = JsonResponseReader.ParseObject(rawJson, "faction taglines");
            if (root == null || !(root["factions"] is JArray entries)) return null;

            var parsed = new List<(FactionSnapshot Faction, string Tagline, int NarrativeRevision, int FactionRevision)>();
            var returnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in entries)
            {
                if (!(token is JObject entry) || entry["faction_key"]?.Type != JTokenType.String ||
                    entry["tagline"]?.Type != JTokenType.String ||
                    entry["tagline"].Value<string>().Trim().NullOrEmpty())
                {
                    Log.Warning("[Firefly] Faction tagline response was incomplete or invalid.");
                    return null;
                }
                string key = entry["faction_key"].Value<string>().Trim();
                if (!targets.TryGetValue(key, out FactionSnapshot faction) || !returnedKeys.Add(key) ||
                    !coveredNarrativeRevisions.TryGetValue(key, out int narrativeRevision) ||
                    !coveredFactionRevisions.TryGetValue(key, out int factionRevision))
                {
                    Log.Warning($"[Firefly] Faction tagline response used an unknown or duplicate faction_key: {key}");
                    return null;
                }
                parsed.Add((faction, entry["tagline"].Value<string>().Trim(), narrativeRevision, factionRevision));
            }
            if (!returnedKeys.SetEquals(targets.Keys))
            {
                Log.Warning("[Firefly] Faction tagline response omitted a submitted faction.");
                return null;
            }

            foreach (var result in parsed)
            {
                result.Faction.Tagline = result.Tagline;
                result.Faction.TaglineCoveredNarrativeRevision = result.NarrativeRevision;
                result.Faction.TaglineCoveredFactionRevision = result.FactionRevision;
            }
            return returnedKeys.ToList();
        }

        // World Seed only needs enough to ground new threads in the world's actual factions — the
        // same lightweight key/name/tagline shape every other later-stage call already uses, not
        // the full status block (tech, leader, religion, relationships). Description isn't part
        // of this either; the tagline already distills it.
        public static string BuildFactionContextBlock(FireflyWorldComponent world)
        {
            var sb = new StringBuilder("=== FACTIONS ===\n");
            foreach (var faction in world.FactionSnapshots.Where(f => f != null))
            {
                sb.AppendLine($"faction_key: {faction.Key}");
                sb.AppendLine($"name: {faction.FactionName}");
                sb.AppendLine($"tagline: {(faction.Tagline.NullOrEmpty() ? "(none yet)" : faction.Tagline)}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static string BuildFactionFactsPrompt(IEnumerable<FactionSnapshot> factions)
        {
            var sb = new StringBuilder("=== FACTIONS TO INITIALIZE ===\n");
            foreach (var faction in factions.Where(f => f != null))
            {
                sb.AppendLine($"=== FACTION {faction.Key} ===");
                sb.AppendLine($"faction_key: {faction.Key}");
                // RimWorld's own built-in FactionDef flavor text — pulled fresh here rather than
                // stored on FactionStatusSnapshot, since Josh only wants it grounding this one
                // initial-seeding call, not living on permanently as part of Status (UI/World Seed
                // never see it).
                string lore = faction.ResolveFaction()?.def?.description ?? "";
                sb.AppendLine($"Known lore: {(lore.NullOrEmpty() ? "(none provided)" : lore)}");
                sb.AppendLine(faction.ToStatusPromptBlock());
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        public static List<string> ApplyInitialFactionFacts(
            IEnumerable<FactionSnapshot> batch, string rawJson, int day)
        {
            var targets = batch.Where(f => f != null)
                .ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
            JObject root = JsonResponseReader.ParseObject(rawJson, "faction facts");
            if (root == null || !(root["factions"] is JArray entries)) return null;

            var parsed = new List<(FactionSnapshot Faction, List<string> Facts, string Tagline)>();
            var returnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in entries)
            {
                if (!(token is JObject entry) || entry["faction_key"]?.Type != JTokenType.String ||
                    !(entry["facts"] is JArray facts) || facts.Count == 0 ||
                    facts.Any(f => f?.Type != JTokenType.String || f.Value<string>().Trim().NullOrEmpty()) ||
                    entry["tagline"]?.Type != JTokenType.String ||
                    entry["tagline"].Value<string>().Trim().NullOrEmpty())
                {
                    Log.Warning("[Firefly] Faction facts response was incomplete or invalid: each entry requires faction_key, non-empty facts, and tagline.");
                    return null;
                }

                string key = entry["faction_key"].Value<string>().Trim();
                if (!targets.TryGetValue(key, out FactionSnapshot faction) || !returnedKeys.Add(key))
                {
                    Log.Warning($"[Firefly] Faction facts response used an unknown or duplicate faction_key: {key}");
                    return null;
                }
                parsed.Add((faction, facts.Select(f => f.Value<string>().Trim()).ToList(),
                    entry["tagline"].Value<string>().Trim()));
            }

            foreach (var result in parsed)
            {
                // Bootstrap seeds identity facts and the first Tagline. Description is generated
                // immediately afterward through the same summary service used for later updates.
                if (result.Faction.FactionJournal.Facts.Count > 0) continue;
                foreach (string fact in result.Facts)
                    result.Faction.FactionJournal.AddFact(GenTicks.TicksAbs, day, fact);
                result.Faction.Tagline = result.Tagline;
                result.Faction.TaglineCoveredNarrativeRevision =
                    result.Faction.NarrativeJournal.FactRevision;
            }
            return returnedKeys.ToList();
        }

        // Shared "id / name / summary" block used by every prompt that lists World Threads —
        // keeps the World Progression and Faction Update prompts from drifting apart on format.
        private static void AppendThreadSummaryBlock(StringBuilder sb, IEnumerable<WorldThread> threads)
        {
            foreach (var thread in threads)
            {
                sb.AppendLine($"id: {thread.Id}");
                sb.AppendLine($"name: {thread.Title}");
                sb.AppendLine($"summary: {(thread.ActiveSummary.NullOrEmpty() ? "(none yet)" : thread.ActiveSummary)}");
                sb.AppendLine();
            }
        }

        public static string BuildWorldOutcomePrompt(FireflyWorldComponent world, string colonySummary)
        {
            var sb = new StringBuilder("=== CURRENT WORLD THREADS ===\n");
            AppendThreadSummaryBlock(sb, world.WorldThreads.Where(t => t != null));

            if (!world.WorldHistory.NullOrEmpty())
            {
                sb.AppendLine("=== WORLD HISTORY ===");
                sb.AppendLine(world.WorldHistory);
                sb.AppendLine();
            }

            var recentSimulations = world.DailyWorldRecords
                .Where(r => r != null && r.Day > world.LastWorldArcDay && !r.Simulation.NullOrEmpty())
                .OrderBy(r => r.Day).ToList();
            if (recentSimulations.Count > 0)
            {
                sb.AppendLine("=== RECENT WORLD SIMULATIONS ===");
                foreach (var record in recentSimulations)
                {
                    sb.AppendLine($"=== Day {record.Day} ===");
                    sb.AppendLine(record.Simulation);
                    sb.AppendLine();
                }
            }

            sb.AppendLine("=== FACTIONS ===");
            foreach (var faction in world.FactionSnapshots.Where(f => f != null))
            {
                sb.AppendLine($"faction_key: {faction.Key}");
                sb.AppendLine($"name: {faction.FactionName}");
                sb.AppendLine($"tagline: {(faction.Tagline.NullOrEmpty() ? "(none yet)" : faction.Tagline)}");
                sb.AppendLine();
            }

            // World Outcome now writes plain narrative prose (not structured JSON), and its
            // example output deliberately references the season — give it the real calendar
            // context to draw on rather than guessing. Map-derived, so only available once a
            // colony exists, same as everything else that reaches this call.
            Map map = Find.AnyPlayerHomeMap ?? Find.CurrentMap;
            if (map != null)
            {
                Season season = GenLocalDate.Season(map);
                int daysUntilNextSeason = GenDate.DaysPerSeason - GenLocalDate.DayOfSeason(map);
                sb.AppendLine("=== DATE ===");
                sb.AppendLine($"Season: {season.LabelCap()} ({daysUntilNextSeason} " +
                    $"day{(daysUntilNextSeason == 1 ? "" : "s")} until the next season)");
                sb.AppendLine($"Year: {GenLocalDate.Year(map)}");
            }

            sb.AppendLine("=== TODAY'S COLONY NARRATIVE SUMMARY ===");
            sb.AppendLine(colonySummary);
            return sb.ToString();
        }

        // World Outcome now returns plain prose, not JSON — validate it's real, plausible-length
        // narrative rather than parsing a schema. A model that ignores the prompt and returns
        // JSON/a code fence anyway is rejected outright rather than persisted as "the day's story".
        private const int MaxWorldOutcomeChars = 2000;

        public static string ValidateWorldOutcome(string rawText)
        {
            string text = rawText?.Trim() ?? "";
            if (text.NullOrEmpty())
            {
                Log.Warning("[Firefly] World outcome response was empty.");
                return null;
            }
            if (text.StartsWith("```") || text.StartsWith("{") || text.StartsWith("["))
            {
                Log.Warning("[Firefly] World outcome response looked like code/JSON instead of prose — rejected.");
                return null;
            }
            if (text.Length > MaxWorldOutcomeChars)
            {
                Log.Warning($"[Firefly] World outcome response was implausibly long ({text.Length} chars) — rejected.");
                return null;
            }
            return text;
        }

        public static string BuildWorldThreadUpdatesPrompt(FireflyWorldComponent world, string outcomeText)
        {
            var sb = new StringBuilder("=== CURRENT WORLD THREADS ===\n");
            AppendThreadSummaryBlock(sb, world.WorldThreads.Where(t => t != null));
            sb.AppendLine("=== DECIDED DAILY WORLD OUTCOME ===");
            sb.AppendLine(outcomeText);
            return sb.ToString();
        }

        public static string BuildFactionUpdatePrompt(FireflyWorldComponent world,
            IEnumerable<string> touchedIds)
        {
            var ids = new HashSet<string>(touchedIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder("=== CREATED OR UPDATED WORLD THREADS ===\n");
            AppendThreadSummaryBlock(sb, world.WorldThreads.Where(t => t != null && ids.Contains(t.Id)));
            sb.AppendLine("=== FACTIONS ===");
            foreach (var faction in world.FactionSnapshots.Where(f => f != null))
            {
                sb.AppendLine($"faction_key: {faction.Key}");
                sb.AppendLine($"tagline: {(faction.Tagline.NullOrEmpty() ? "(none yet)" : faction.Tagline)}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static List<string> ApplyThreadResult(FireflyWorldComponent world, string rawJson, int day, bool seed)
        {
            var ids = new HashSet<string>(world.WorldThreads.Where(t => t != null).Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            ThreadScanResponse response = ThreadScanResponseParser.Parse(rawJson, "world progression", ids);
            if (response == null) return null;
            if (seed && (response.NewThreads.Count < 3 || response.NewThreads.Count > 6 || response.Updates.Count != 0))
            {
                Log.Warning("[Firefly] World seed must create exactly three to six threads and no updates.");
                return null;
            }
            var touched = new List<string>();
            foreach (var created in response.NewThreads)
            {
                string id = world.AddWorldThread(created.Name, created.Summary, created.Facts, day);
                if (!id.NullOrEmpty()) touched.Add(id);
            }
            foreach (var update in response.Updates)
            {
                bool changed = false;
                foreach (string fact in update.Facts) changed |= world.AddWorldThreadFact(update.Id, day, fact);
                if (changed && !touched.Contains(update.Id, StringComparer.OrdinalIgnoreCase)) touched.Add(update.Id);
            }
            return touched;
        }

        public sealed class FactionUpdateApplyResult
        {
            public List<string> NarrativeFactionKeys = new List<string>();
            public List<string> IdentityFactionKeys = new List<string>();
        }

        public static FactionUpdateApplyResult ApplyFactionUpdate(
            FireflyWorldComponent world, string rawJson, int day)
        {
            JObject root = JsonResponseReader.ParseObject(rawJson, "faction update");
            if (root == null) return null;
            if (!(root["updates"] is JArray updates)) return null;
            var known = new HashSet<string>(world.FactionSnapshots.Where(f => f != null).Select(f => f.Key),
                StringComparer.OrdinalIgnoreCase);
            var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in updates)
            {
                if (!(token is JObject entry) || entry["faction_key"]?.Type != JTokenType.String ||
                    !(entry["facts"] is JArray facts) || facts.Count == 0 ||
                    facts.Any(f => f?.Type != JTokenType.String || f.Value<string>().Trim().NullOrEmpty()) ||
                    !(entry["identity_facts"] is JArray identityFacts) ||
                    identityFacts.Any(f => f?.Type != JTokenType.String || f.Value<string>().Trim().NullOrEmpty()) ||
                    !known.Contains(entry["faction_key"].Value<string>().Trim()) ||
                    !returned.Add(entry["faction_key"].Value<string>().Trim()))
                {
                    Log.Warning("[Firefly] Faction update response was incomplete or used an unknown or duplicate faction_key.");
                    return null;
                }
            }
            var result = new FactionUpdateApplyResult();
            foreach (JObject entry in updates)
            {
                string key = entry["faction_key"].Value<string>().Trim();
                bool narrativeChanged = false;
                foreach (var fact in (JArray)entry["facts"])
                    narrativeChanged |= world.AddFactionFact(key, day, fact.Value<string>());
                bool identityChanged = false;
                foreach (var fact in (JArray)entry["identity_facts"])
                    identityChanged |= world.AddFactionIdentityFact(key, day, fact.Value<string>());
                if (narrativeChanged) result.NarrativeFactionKeys.Add(key);
                if (identityChanged) result.IdentityFactionKeys.Add(key);
            }
            return result;
        }
    }
}
