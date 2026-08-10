using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;

namespace Firefly
{
    public class MainTabWindow_Journal : MainTabWindow
    {
        // Special nav IDs
        private const int NavToday  = -1;
        private const int NavColony = -2;
        private const int NavThreads  = -3;

        private const float NavW  = 148f;
        private const float Pad   = 5f;
        private const float TabH  = 22f;

        private enum NavRoot { Colony, Threads }

        // Semantic accents: journal, resolved prose, danger, and ongoing threads.
        private static readonly Color AcJournal = new Color(0.58f, 0.73f, 0.86f);
        private static readonly Color AcSummary = new Color(0.35f, 0.78f, 0.48f);
        private static readonly Color AcWarning = new Color(0.88f, 0.48f, 0.28f);
        private static readonly Color AcThreads = new Color(0.88f, 0.52f, 0.72f);
        private static readonly string[] PendingGlyphs = { "◐", "◓", "◑", "◒" };

        private int    _nav     = NavToday;
        private string _section = "EVENTS";
        private NavRoot _activeRoot = NavRoot.Colony;

        private readonly Dictionary<int, string>    _navSectionMemory = new Dictionary<int, string>();
        private readonly Dictionary<string, Vector2> _scrolls         = new Dictionary<string, Vector2>();
        private Vector2 _navScroll      = Vector2.zero;
        private string  _selectedThreadId = null;

        public override Vector2 RequestedTabSize => new Vector2(1000f, 430f);

        // ── Entry point ───────────────────────────────────────────────────────

        public override void DoWindowContents(Rect inRect)
        {
            var ledger = ColonyLedger.Current;
            if (ledger == null) { Widgets.Label(inRect, "No active colony."); return; }

            var  past     = ledger.PastDays;
            int  today    = ledger.RecordingDay;
            bool hasToday = !past.Any(d => d.Day == today);

            if (!hasToday && past.Count == 0 && ledger.ColonyHistory.NullOrEmpty() && ledger.StoryThreads.Count == 0)
            {
                Widgets.Label(inRect.ContractedBy(Pad),
                    "I have not begun this colony's chronicle yet.\n\nGive the day time to unfold, and I will remember it. — Fillion");
                return;
            }

            // Keep content selection valid within the active top-level section.
            if (_activeRoot == NavRoot.Colony)
            {
                bool validDay = _nav >= 0 && past.Any(d => d.Day == _nav);
                if (_nav == NavThreads || (_nav == NavToday && !hasToday) || (_nav >= 0 && !validDay))
                    SelectColonyDefault(hasToday, today, past);
            }
            else
            {
                _nav = NavThreads;
                if (ledger.StoryThreads.All(t => t.Id != _selectedThreadId))
                    _selectedThreadId = ledger.StoryThreads.FirstOrDefault()?.Id;
            }

            // Layout
            var navRect  = new Rect(inRect.x,        inRect.y, NavW,                       inRect.height);
            var divLine  = new Rect(inRect.x + NavW, inRect.y, 1f,                         inRect.height);
            var mainRect = new Rect(inRect.x + NavW + 1f + Pad, inRect.y,
                                    inRect.width - NavW - 1f - Pad, inRect.height);

            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            GUI.DrawTexture(divLine, BaseContent.WhiteTex);
            GUI.color = Color.white;

            DrawNav(navRect, hasToday, today, past, ledger.StoryThreads);
            DrawMain(mainRect, ledger, hasToday, today, past);
            DrawPendingIndicator(mainRect);
        }

        private static void DrawPendingIndicator(Rect mainRect)
        {
            if (!LLMClient.IsPending) return;

            bool colonyPending = LLMClient.IsPendingForAny(JournalRecorder.ColonyPendingLabels);
            bool threadsPending = LLMClient.IsPendingForAny(JournalRecorder.ThreadsPendingLabels);
            Color accent = colonyPending && threadsPending
                ? Color.Lerp(AcJournal, AcThreads, 0.5f)
                : colonyPending ? AcJournal : threadsPending ? AcThreads : AcSummary;
            string category = colonyPending && threadsPending
                ? "Colony + Threads"
                : colonyPending ? "Colony" : threadsPending ? "Threads" : "Journal";
            string bannerText = $"Fillion is writing  ·  {category}";

            int frame = Mathf.FloorToInt(Time.realtimeSinceStartup * 4f) % PendingGlyphs.Length;
            float wave = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 5f);
            Text.Font = GameFont.Tiny;
            float bannerWidth = Mathf.Ceil(Text.CurFontStyle.CalcSize(new GUIContent(bannerText)).x) + 46f;
            bannerWidth = Mathf.Min(bannerWidth, mainRect.width);
            var rect = new Rect(mainRect.xMax - bannerWidth, mainRect.y, bannerWidth, 26f);

            GUI.color = Color.white;
            Widgets.DrawMenuSection(rect);
            GUI.color = new Color(accent.r, accent.g, accent.b, 0.07f);
            GUI.DrawTexture(rect.ContractedBy(1f), BaseContent.WhiteTex);
            GUI.color = new Color(accent.r, accent.g, accent.b, 0.62f + wave * 0.3f);
            GUI.DrawTexture(new Rect(rect.x + 1f, rect.y + 2f, 3f, rect.height - 4f), BaseContent.WhiteTex);

            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = new Color(accent.r, accent.g, accent.b, 0.82f + wave * 0.18f);
            Widgets.Label(new Rect(rect.x + 11f, rect.y, 22f, rect.height), PendingGlyphs[frame]);
            GUI.color = new Color(0.88f, 0.88f, 0.88f);
            Widgets.Label(new Rect(rect.x + 32f, rect.y, rect.width - 38f, rect.height), bannerText);
            TooltipHandler.TipRegion(rect, "An LLM response for Firefly is being generated.");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }

        // ── Navigation panel ──────────────────────────────────────────────────

        private void DrawNav(Rect rect, bool hasToday, int today, IReadOnlyList<DailyRecord> past,
                             IReadOnlyList<StoryThread> threads)
        {
            const float rowH = 26f;
            float viewWidth = rect.width - 16f;

            // Computed once and reused for both the scroll content size below and the actual
            // per-row draw/hit-test in the loop further down — calling Text.CalcHeight separately
            // in two places let the two disagree (rows drawn at one height, hitboxes at another,
            // drifting further out of sync each row down the list).
            // Not gated on _activeRoot: a root-row click below can flip _activeRoot to Threads
            // mid-frame, and the render loop further down re-checks _activeRoot fresh — gating this
            // list on the pre-click value left it null on exactly the frame that needed it.
            List<float> threadHeights = threads.Count > 0
                ? threads.Select(t => ThreadRowHeight(viewWidth, t, rowH)).ToList()
                : null;

            float childH = _activeRoot == NavRoot.Colony
                ? (1 + (hasToday ? 1 : 0) + past.Count) * rowH
                : threadHeights != null
                    ? threadHeights.Sum()
                    : rowH;
            float headerH = 3 * rowH;
            var view = new Rect(0f, 0f, viewWidth, Mathf.Max(headerH + childH, rect.height));
            Widgets.BeginScrollView(rect, ref _navScroll, view);

            float y = 0f;
            y = RootNavRow(y, view.width, rowH, NavRoot.Colony, "Colony", AcJournal,
                () =>
                {
                    _activeRoot = NavRoot.Colony;
                    SelectColonyDefault(hasToday, today, past);
                });
            y = RootNavRow(y, view.width, rowH, NavRoot.Threads, "Threads", AcThreads,
                () =>
                {
                    _activeRoot = NavRoot.Threads;
                    _nav = NavThreads;
                    _selectedThreadId = threads.FirstOrDefault()?.Id;
                    _section = _navSectionMemory.TryGetValue(NavThreads, out string memory) ? memory : "SUMMARY";
                });
            y = NavDivider(y, view.width, rowH);

            if (_activeRoot == NavRoot.Colony)
            {
                y = NavRow(y, view.width, rowH, NavColony, "History", "◆", AcJournal);

                if (hasToday)
                    y = NavRow(y, view.width, rowH, NavToday, $"Day {today}", "●", AcJournal);

                foreach (var rec in past.OrderByDescending(d => d.Day))
                {
                    bool hasSummary = !rec.Summary.NullOrEmpty();
                    y = NavRow(y, view.width, rowH, rec.Day,
                        $"Day {rec.Day}",
                        hasSummary ? "✓" : "·",
                        hasSummary ? AcSummary : Color.gray);
                }
            }
            else if (threads.Count > 0)
            {
                for (int i = 0; i < threads.Count; i++)
                    y = ThreadNavRow(y, view.width, threads[i], threadHeights[i]);
            }
            else
            {
                y = NavRow(y, view.width, rowH, NavThreads, "No threads yet", "·", AcThreads);
            }

            Widgets.EndScrollView();
        }

        private void SelectColonyDefault(bool hasToday, int today, IReadOnlyList<DailyRecord> past)
        {
            _nav = hasToday ? NavToday : past.Count > 0 ? past.Max(d => d.Day) : NavColony;
            _section = _navSectionMemory.TryGetValue(_nav, out string memory)
                ? memory
                : DefaultSection(_nav);
        }

        private float RootNavRow(float y, float w, float rowH, NavRoot root, string label,
                                 Color accent, System.Action onClick)
        {
            Text.Font = GameFont.Small;
            var row = new Rect(0f, y, w, rowH);
            bool selected = _activeRoot == root;
            bool pending = root == NavRoot.Colony
                ? LLMClient.IsPendingForAny(JournalRecorder.ColonyPendingLabels)
                : LLMClient.IsPendingForAny(JournalRecorder.ThreadsPendingLabels);

            if (selected)
            {
                GUI.color = new Color(accent.r, accent.g, accent.b, 0.14f);
                GUI.DrawTexture(row, BaseContent.WhiteTex);
                GUI.color = accent;
                GUI.DrawTexture(new Rect(0f, y + 2f, 3f, rowH - 4f), BaseContent.WhiteTex);
            }
            else if (Mouse.IsOver(row))
                Widgets.DrawHighlight(row);

            if (pending)
            {
                float wave = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 5f);
                GUI.color = new Color(accent.r, accent.g, accent.b, 0.42f + wave * 0.5f);
                const float edge = 1.5f;
                GUI.DrawTexture(new Rect(row.x, row.y, row.width, edge), BaseContent.WhiteTex);
                GUI.DrawTexture(new Rect(row.x, row.yMax - edge, row.width, edge), BaseContent.WhiteTex);
                GUI.DrawTexture(new Rect(row.x, row.y, edge, row.height), BaseContent.WhiteTex);
                GUI.DrawTexture(new Rect(row.xMax - edge, row.y, edge, row.height), BaseContent.WhiteTex);
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = selected ? Color.white : new Color(0.65f, 0.65f, 0.65f);
            Widgets.Label(new Rect(9f, y, w - 40f, rowH), label);
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = accent;
            string badge = pending
                ? PendingGlyphs[Mathf.FloorToInt(Time.realtimeSinceStartup * 4f) % PendingGlyphs.Length]
                : selected ? "▼" : "▶";
            var badgeRect = new Rect(w - 28f, y, 24f, rowH);
            Widgets.Label(badgeRect, badge);
            if (pending)
                TooltipHandler.TipRegion(badgeRect, $"Fillion is writing in {label}.");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonInvisible(row)) onClick();
            return y + rowH;
        }

        private static float NavDivider(float y, float width, float rowH)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.09f);
            GUI.DrawTexture(new Rect(6f, y + rowH * 0.45f, width - 12f, 1f), BaseContent.WhiteTex);
            GUI.color = Color.white;
            return y + rowH * 0.8f;
        }

        // Long thread names wrap rather than clip — the row grows to fit instead of squashing
        // wrapped lines into a fixed single-line height.
        private static float ThreadRowHeight(float w, StoryThread thread, float rowH)
        {
            float labelW = w - 54f;
            // Do not measure through Text.CurFontStyle: it is global IMGUI state and may still
            // carry a font selected by a different control on the previous frame. Pinning both
            // measurement and rendering to Small keeps wrapping and row geometry identical.
            var style = Text.fontStyles[(int)GameFont.Small];
            float textH = style.CalcHeight(new GUIContent(thread.Name ?? ""), labelW);
            return Mathf.Max(rowH, textH + 6f);
        }

        private float ThreadNavRow(float y, float w, StoryThread thread, float h)
        {
            float labelW = w - 54f;

            var r = new Rect(0f, y, w, h);
            bool selected = _nav == NavThreads && _selectedThreadId == thread.Id;
            bool working = JournalRecorder.IsThreadWorking(thread.Id);
            long now = Find.TickManager?.TicksAbs ?? 0L;
            bool updatedToday = thread.LastTouchedTick > 0L && now >= thread.LastTouchedTick
                && now - thread.LastTouchedTick < GenDate.TicksPerDay;

            if (selected)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.09f);
                GUI.DrawTexture(r, BaseContent.WhiteTex);
                GUI.color = AcThreads;
                GUI.DrawTexture(new Rect(0f, y + 3f, 3f, h - 6f), BaseContent.WhiteTex);
            }
            else if (Mouse.IsOver(r))
                Widgets.DrawHighlight(r);
            else
            {
                GUI.color = new Color(AcThreads.r, AcThreads.g, AcThreads.b, 0.035f);
                GUI.DrawTexture(r, BaseContent.WhiteTex);
            }

            if (working)
            {
                float wave = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 5f);
                GUI.color = new Color(AcThreads.r, AcThreads.g, AcThreads.b, 0.42f + wave * 0.5f);
                const float edge = 1.5f;
                GUI.DrawTexture(new Rect(r.x, r.y, r.width, edge), BaseContent.WhiteTex);
                GUI.DrawTexture(new Rect(r.x, r.yMax - edge, r.width, edge), BaseContent.WhiteTex);
                GUI.DrawTexture(new Rect(r.x, r.y, edge, r.height), BaseContent.WhiteTex);
                GUI.DrawTexture(new Rect(r.xMax - edge, r.y, edge, r.height), BaseContent.WhiteTex);
            }

            GUI.color = Color.white;

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = selected ? Color.white : new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(new Rect(14f, y, labelW, h), thread.Name);

            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = AcThreads;
            var badgeRect = new Rect(w - 39f, y, 36f, h);
            string badge = working
                ? PendingGlyphs[Mathf.FloorToInt(Time.realtimeSinceStartup * 4f) % PendingGlyphs.Length]
                : updatedToday ? "NEW" : "◇";
            Widgets.Label(badgeRect, badge);
            if (working)
                TooltipHandler.TipRegion(badgeRect, "Fillion is writing this story thread.");
            else if (updatedToday)
                TooltipHandler.TipRegion(badgeRect, "This story thread changed today.");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonInvisible(r))
            {
                _nav = NavThreads;
                _selectedThreadId = thread.Id;
                _section = _navSectionMemory.TryGetValue(NavThreads, out string memory) ? memory : "SUMMARY";
            }
            return y + h;
        }

        private float NavRow(float y, float w, float rowH, int id, string label, string badge, Color accent)
        {
            Text.Font = GameFont.Small;
            var r   = new Rect(0f, y, w, rowH);
            bool sel = _nav == id;

            // Background
            if (sel)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.09f);
                GUI.DrawTexture(r, BaseContent.WhiteTex);
                GUI.color = accent;
                GUI.DrawTexture(new Rect(0f, y + 3f, 3f, rowH - 6f), BaseContent.WhiteTex);
            }
            else if (Mouse.IsOver(r))
                Widgets.DrawHighlight(r);
            GUI.color = Color.white;

            // Label
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color   = sel ? Color.white : new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(new Rect(9f, y, w - 26f, rowH), label);

            // Badge
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color   = accent;
            Widgets.Label(new Rect(0f, y, w - 3f, rowH), badge);

            GUI.color   = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonInvisible(r))
            {
                _nav     = id;
                _section = _navSectionMemory.TryGetValue(id, out string mem) ? mem : DefaultSection(id);
            }
            return y + rowH;
        }

        private static string DefaultSection(int navId) => navId >= 0 ? "SUMMARY" : "EVENTS";

        // ── Main content area ─────────────────────────────────────────────────

        private void DrawMain(Rect rect, ColonyLedger ledger, bool hasToday, int today, IReadOnlyList<DailyRecord> past)
        {
            if (_nav == NavToday && hasToday) { DrawToday(rect, ledger, today); return; }
            if (_nav == NavColony)            { DrawColony(rect, ledger);        return; }
            if (_nav == NavThreads)             { DrawThreads(rect, ledger);         return; }

            var rec = past.FirstOrDefault(d => d.Day == _nav);
            if (rec != null) DrawDay(rect, rec, past);
        }

        // ── Today view ────────────────────────────────────────────────────────

        private void DrawToday(Rect rect, ColonyLedger ledger, int day)
        {
            float lon = Find.WorldGrid?.LongLatOf(Find.CurrentMap?.Tile ?? 0).x ?? 0f;

            var   liveSec       = ParseSections(ledger.GetCurrentDayContent());
            liveSec.TryGetValue("EVENTS", out string evRaw);

            string dateInfo = "";
            foreach (var kv in liveSec)
                if (kv.Key.StartsWith("DAY ")) { dateInfo = kv.Value.Trim(); break; }

            string eventsContent = dateInfo.NullOrEmpty() ? (evRaw ?? "") : dateInfo + "\n\n" + (evRaw ?? "");
            string combatContent = ledger.GetCurrentCombatContent(lon);
            string hazardContent = ledger.GetCurrentHazardContent(lon);

            string todayQuestContent = ledger.BuildMentionedQuestsSnapshot();

            var secs = new List<(string Name, Color Ac, string Text)>
            {
                ("EVENTS", AcJournal, eventsContent),
            };
            if (!combatContent.NullOrEmpty())       secs.Add(("COMBAT",  AcWarning, combatContent));
            if (!hazardContent.NullOrEmpty())       secs.Add(("HAZARDS", AcWarning, hazardContent));
            if (!todayQuestContent.NullOrEmpty())   secs.Add(("QUESTS",  AcJournal, todayQuestContent));

            DrawSectioned(rect, secs, $"today{day}", AcJournal, $"DAY {day}  —  IN PROGRESS");
        }

        // ── Colony history view ───────────────────────────────────────────────

        private void DrawColony(Rect rect, ColonyLedger ledger)
        {
            string history = ledger.ColonyHistory;
            var secs = new List<(string, Color, string)>
            {
                ("COLONY HISTORY", AcJournal, history.NullOrEmpty()
                    ? "I am still gathering the shape of this colony's story. In time, its history will find its voice. — Fillion"
                    : history),
            };
            DrawSectioned(rect, secs, "colony", AcJournal, "COLONY HISTORY");
        }

        // ── Story thread view ─────────────────────────────────────────────────

        private void DrawThreads(Rect rect, ColonyLedger ledger)
        {
            var selected = ledger.StoryThreads.FirstOrDefault(t => t.Id == _selectedThreadId);
            if (selected == null)
            {
                var secs = new List<(string, Color, string)>
                {
                    ("THREADS", AcThreads,
                        "No threads have taken root yet. When something in this colony's story feels " +
                        "larger than a single day, I will begin one here. — Fillion"),
                };
                DrawSectioned(rect, secs, "threads_empty", AcThreads, "STORY THREADS");
                return;
            }

            // Day comes straight from the colony's own day counter (same numbering as
            // DailyRecord.Day / the daily timeline), set on the fact when it was recorded — not
            // reconstructed from Tick. Tick is an absolute-calendar tick (RimWorld.GenDate's
            // year-5500 epoch), so deriving "day" from it drifted from the colony's actual day
            // count for any colony that didn't start on day 1 of the in-game year.
            var facts = new StringBuilder();
            foreach (var fact in selected.Facts.Where(f => f != null).OrderBy(f => f.Tick))
                facts.AppendLine($"  - [Day {fact.Day}] {fact.Text}");

            var sections = new List<(string Name, Color Ac, string Text)>
            {
                ("SUMMARY", AcSummary, selected.Description),
                ("FACTS", AcThreads, facts.ToString().TrimEnd()),
            };

            // Chunks are deliberately hidden from normal play — they're internal source material
            // for the summarizer, not part of the player-facing record the FACTS tab shows. Dev
            // mode only, same gating as the LLM IN/OUT debug sections on a day's own view.
            if (Prefs.DevMode)
            {
                var chunks = new StringBuilder();
                if (selected.Chunks.Count == 0)
                {
                    chunks.AppendLine("(none yet)");
                }
                else
                {
                    foreach (var chunk in selected.Chunks)
                    {
                        chunks.AppendLine($"[Day {chunk.StartDay}-{chunk.EndDay}] {chunk.Summary}");
                        chunks.AppendLine();
                    }
                }
                chunks.AppendLine($"Unchunked facts: {selected.Facts.Count - selected.ChunkedThroughFactIndex} " +
                                   $"(of {selected.Facts.Count} total, chunked through index {selected.ChunkedThroughFactIndex})");
                sections.Add(("CHUNKS", AcThreads, chunks.ToString().TrimEnd()));
            }

            DrawSectioned(rect, sections, $"thread_{selected.Id}", AcThreads,
                selected.Name.ToUpperInvariant());
        }

        // ── Past day view ─────────────────────────────────────────────────────

        private void DrawDay(Rect rect, DailyRecord record, IReadOnlyList<DailyRecord> past)
        {
            var parsed = ParseSections(record.Timeline);

            // Events = roster + events merged
            parsed.TryGetValue("EVENTS", out string evRaw);
            string eventsContent = "";
            if (parsed.TryGetValue("CHARACTER ROSTER", out string rRaw) && !rRaw.NullOrEmpty())
                eventsContent = "Character Roster:\n" + rRaw.Trim() + "\n\n";
            eventsContent += evRaw ?? "";

            // Status = colony status + health + relations + skills
            var statusParts = new System.Collections.Generic.List<string>();
            if (parsed.TryGetValue("COLONY STATUS",          out string cs) && !cs.NullOrEmpty())  statusParts.Add(cs.Trim());
            if (parsed.TryGetValue("COLONIST HEALTH",        out string h)  && !h.NullOrEmpty())   statusParts.Add(h.Trim());
            if (parsed.TryGetValue("PRISONER/SLAVE HEALTH",  out string ph) && !ph.NullOrEmpty())  statusParts.Add(ph.Trim());
            if (parsed.TryGetValue("RELATIONSHIP CHANGES",   out string r)  && !r.NullOrEmpty())   statusParts.Add(r.Trim());
            if (parsed.TryGetValue("FACTION RELATIONS",      out string fr) && !fr.NullOrEmpty())  statusParts.Add(fr.Trim());
            if (parsed.TryGetValue("SKILL CHANGES",          out string sk) && !sk.NullOrEmpty())  statusParts.Add(sk.Trim());
            string statusContent = string.Join("\n\n", statusParts);

            parsed.TryGetValue("COMBAT",  out string combatContent);
            parsed.TryGetValue("HAZARDS", out string hazardContent);

            bool hasSummary = !record.Summary.NullOrEmpty();
            string summaryText = hasSummary
                ? record.Summary
                : PendingSummaryText();

            var secs = new List<(string Name, Color Ac, string Text)>
            {
                ("SUMMARY", hasSummary ? AcSummary : Color.gray, summaryText),
                ("EVENTS",  AcJournal, eventsContent),
            };
            if (!statusContent.NullOrEmpty())            secs.Add(("STATUS",  AcJournal, statusContent));
            if (!combatContent.NullOrEmpty())            secs.Add(("COMBAT",  AcWarning, combatContent));
            if (!hazardContent.NullOrEmpty())            secs.Add(("HAZARDS", AcWarning, hazardContent));
            if (!record.QuestSnapshot.NullOrEmpty())     secs.Add(("QUESTS",  AcJournal, record.QuestSnapshot));
            if (Prefs.DevMode)
            {
                string prevSummary = past.FirstOrDefault(d => d.Day == record.Day - 1 && !d.Summary.NullOrEmpty())?.Summary;
                string llmIn = prevSummary.NullOrEmpty()
                    ? (record.Timeline ?? "")
                    : $"=== PREVIOUS DAY SUMMARY (context only — do not summarise this) ===\n{prevSummary.Trim()}\n\n{record.Timeline}";
                secs.Add(("LLM IN", AcThreads, llmIn));
                secs.Add(("LLM OUT", AcSummary, summaryText));
            }

            DrawSectioned(rect, secs, $"day{record.Day}", AcJournal, $"DAY {record.Day}");
        }

        private static string PendingSummaryText()
        {
            int dots = 1 + Mathf.FloorToInt(Time.realtimeSinceStartup * 2f) % 3;
            return "I am still considering how this day should be remembered" + new string('.', dots) + " — Fillion";
        }

        // ── Quests view ───────────────────────────────────────────────────────

        internal static void AppendQuestBlock(StringBuilder sb, Quest quest, List<Quest> all, int depth)
        {
            string pad  = depth > 0 ? new string(' ', depth * 4) : "";
            string name = ColonyLedger.StripTags(quest.name.ToString()).Trim();

            string status = QuestStatusLabel(quest);
            string timing = QuestTimingLabel(quest);
            string header = timing.NullOrEmpty()
                ? $"{name} ({status}):"
                : $"{name} ({status} — {timing}):";

            sb.AppendLine($"{pad}{header}");

            // Description
            try
            {
                string desc = ColonyLedger.StripTags(quest.description.ToString()).Trim();
                if (!desc.NullOrEmpty())
                    foreach (var line in desc.Split('\n'))
                    {
                        string l = line.Trim();
                        if (!l.NullOrEmpty()) sb.AppendLine($"{pad}  {l}");
                    }
            }
            catch { }

            // Reward choices — use DescriptionPart from each choice's quest parts
            try
            {
                foreach (var cp in quest.PartsListForReading.OfType<QuestPart_Choice>())
                {
                    for (int ci = 0; ci < cp.choices.Count; ci++)
                    {
                        var choice = cp.choices[ci];
                        var descs = new List<string>();
                        foreach (var qp in choice.questParts)
                        {
                            try
                            {
                                string d = ColonyLedger.StripTags(qp.DescriptionPart ?? "").Trim();
                                if (!d.NullOrEmpty()) descs.Add(d);
                            }
                            catch { }
                        }
                        string choiceLabel = descs.Count > 0
                            ? string.Join(", ", descs)
                            : cp.choices.Count > 1 ? $"Option {ci + 1}" : null;
                        if (!choiceLabel.NullOrEmpty())
                            sb.AppendLine($"{pad}  Accept for: {choiceLabel}");
                    }
                }
            }
            catch { }

            sb.AppendLine();

            // Child quests indented
            foreach (var child in all.Where(q => q.parent == quest && !q.hidden))
                AppendQuestBlock(sb, child, all, depth + 1);
        }

        internal static string QuestStatusLabel(Quest quest)
        {
            switch (quest.State)
            {
                case QuestState.NotYetAccepted:   return "Available";
                case QuestState.Ongoing:           return "Active";
                case QuestState.EndedSuccess:      return "Completed";
                case QuestState.EndedFailed:       return "Failed";
                default:                           return "Historical";
            }
        }

        internal static string QuestTimingLabel(Quest quest)
        {
            try
            {
                if (quest.State == QuestState.NotYetAccepted)
                {
                    int ticks = quest.TicksUntilExpiry;
                    if (ticks > 0)
                    {
                        float days = ticks / (float)GenDate.TicksPerDay;
                        return days < 1f
                            ? $"Expires in {Mathf.RoundToInt(days * 24f)}h"
                            : $"Expires in {days:F1} days";
                    }
                }
                else if (quest.State == QuestState.Ongoing)
                {
                    int ticks = quest.TicksSinceAccepted;
                    if (ticks > 0)
                    {
                        int days = ticks / GenDate.TicksPerDay;
                        return days == 0 ? "Accepted today" : $"Accepted {days} day{(days == 1 ? "" : "s")} ago";
                    }
                }
            }
            catch { }
            return "";
        }

        // ── Sectioned content renderer ────────────────────────────────────────

        private void DrawSectioned(Rect rect, List<(string Name, Color Ac, string Text)> secs,
                                    string prefix, Color headerColor, string headerLabel)
        {
            if (secs.Count == 0) return;

            // Clamp section
            if (!secs.Any(s => s.Name == _section))
            {
                _section = secs[0].Name;
                _navSectionMemory[_nav] = _section;
            }

            float y = rect.y;

            // ── Page header ──────────────────────────────────────────────────
            var headerRect = new Rect(rect.x, y, rect.width, 20f);
            GUI.color = headerColor;
            Text.Font  = GameFont.Tiny;
            Widgets.Label(headerRect, headerLabel);
            Text.Font  = GameFont.Small;
            GUI.color  = Color.white;
            y += 20f + 2f;

            // Thin header underline
            GUI.color = new Color(headerColor.r, headerColor.g, headerColor.b, 0.35f);
            GUI.DrawTexture(new Rect(rect.x, y, rect.width, 1f), BaseContent.WhiteTex);
            GUI.color = Color.white;
            y += 1f + Pad;

            // ── Section tab bar ───────────────────────────────────────────────
            float tabW     = Mathf.Min(110f, (rect.width - (secs.Count - 1) * 2f) / secs.Count);
            var   tabBarY  = y;

            for (int i = 0; i < secs.Count; i++)
            {
                var (name, ac, _) = secs[i];
                bool sel     = _section == name;
                var  tabRect = new Rect(rect.x + i * (tabW + 2f), tabBarY, tabW, TabH);

                // Background
                if (sel)
                {
                    GUI.color = new Color(ac.r, ac.g, ac.b, 0.22f);
                    GUI.DrawTexture(tabRect, BaseContent.WhiteTex);
                }
                else if (Mouse.IsOver(tabRect))
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.06f);
                    GUI.DrawTexture(tabRect, BaseContent.WhiteTex);
                }
                GUI.color = Color.white;

                // Label
                Text.Anchor = TextAnchor.MiddleCenter;
                Text.Font   = GameFont.Tiny;
                GUI.color   = sel ? ac : new Color(ac.r, ac.g, ac.b, 0.60f);
                Widgets.Label(tabRect, name);
                Text.Font   = GameFont.Small;
                GUI.color   = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;

                // Bottom accent for selected
                if (sel)
                {
                    GUI.color = ac;
                    GUI.DrawTexture(new Rect(tabRect.x, tabRect.yMax - 2f, tabRect.width, 2f), BaseContent.WhiteTex);
                    GUI.color = Color.white;
                }

                if (Widgets.ButtonInvisible(tabRect))
                {
                    _section = name;
                    _navSectionMemory[_nav] = name;
                }
            }

            y += TabH + Pad;

            // ── Content box ───────────────────────────────────────────────────
            var (_, selAc, selText) = secs.First(s => s.Name == _section);
            string scrollKey = $"{prefix}_{_section}";
            _scrolls.TryGetValue(scrollKey, out Vector2 scroll);

            var contentBox = new Rect(rect.x, y, rect.width, rect.yMax - y);
            DrawTextBox(contentBox, selText, selAc, ref scroll, scrollKey);
            _scrolls[scrollKey] = scroll;
        }

        private static GUIStyle _selectableStyle;
        private static GUIStyle SelectableStyle
        {
            get
            {
                if (_selectableStyle != null) return _selectableStyle;
                _selectableStyle = new GUIStyle(GUI.skin.textArea)
                {
                    wordWrap = true,
                    richText = false,
                    padding  = new RectOffset(0, 0, 0, 0),
                    margin   = new RectOffset(0, 0, 0, 0),
                    font     = Text.fontStyles[(int)GameFont.Small].font,
                    fontSize = Text.fontStyles[(int)GameFont.Small].fontSize,
                    normal   = { background = null, textColor = new Color(0.85f, 0.85f, 0.85f) },
                    focused  = { background = null, textColor = new Color(0.85f, 0.85f, 0.85f) },
                    hover    = { background = null, textColor = new Color(0.85f, 0.85f, 0.85f) },
                    active   = { background = null, textColor = new Color(0.85f, 0.85f, 0.85f) },
                };
                return _selectableStyle;
            }
        }

        private static void DrawTextBox(Rect box, string text, Color accent, ref Vector2 scroll, string controlKey)
        {
            // Border
            GUI.color = new Color(accent.r, accent.g, accent.b, 0.18f);
            GUI.DrawTexture(box, BaseContent.WhiteTex);
            GUI.color = Color.white;

            var inner = box.ContractedBy(1f);
            Widgets.DrawMenuSection(inner);
            var pad = inner.ContractedBy(4f);

            if (text.NullOrEmpty())
            {
                GUI.color = new Color(0.55f, 0.55f, 0.55f);
                Widgets.Label(pad, "(none)");
                GUI.color = Color.white;
                return;
            }

            // Block typed characters so the area is read-only but still selectable/copyable —
            // only while this specific text box holds focus, so typing elsewhere (e.g. game
            // hotkeys) isn't swallowed just because a journal text box exists on screen.
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && ev.character != '\0' && GUI.GetNameOfFocusedControl() == controlKey)
                ev.Use();

            var   style  = SelectableStyle;
            float textH  = style.CalcHeight(new GUIContent(text), pad.width - 16f);
            var   view   = new Rect(0f, 0f, pad.width - 16f, Mathf.Max(textH, pad.height));
            Widgets.BeginScrollView(pad, ref scroll, view);
            GUI.SetNextControlName(controlKey);
            GUI.TextArea(new Rect(0f, 0f, view.width, Mathf.Max(textH, pad.height)), text, style);
            Widgets.EndScrollView();
        }

        // ── Section parsing ────────────────────────────────────────────────────

        private static readonly Regex SectionRx =
            new Regex(@"^=== (.+?) ===$", RegexOptions.Multiline | RegexOptions.Compiled);

        private static Dictionary<string, string> ParseSections(string text)
        {
            var result = new Dictionary<string, string>();
            if (text.NullOrEmpty()) return result;
            var ms = SectionRx.Matches(text);
            for (int i = 0; i < ms.Count; i++)
            {
                string name  = ms[i].Groups[1].Value;
                int    start = ms[i].Index + ms[i].Length;
                int    end   = i + 1 < ms.Count ? ms[i + 1].Index : text.Length;
                if (!result.ContainsKey(name))
                    result[name] = text.Substring(start, end - start).Trim();
            }
            return result;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            _scrolls.Clear();
            _selectedThreadId = null;
        }
    }
}
