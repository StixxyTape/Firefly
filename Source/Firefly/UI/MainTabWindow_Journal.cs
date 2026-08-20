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
        private const int NavWorld = -4;
        private const int NavFactions = -5;
        private const int NavWorldHistory = -6;

        private const float NavW  = 148f;
        private const float Pad   = 5f;
        private const float TabH  = 22f;

        private enum NavRoot { Colony, Threads, World, Factions, WorldHistory }

        // Distinct category hues remain readable against RimWorld's dark interface.
        private static readonly Color AcJournal = JournalCategoryVisuals.Colony;
        private static readonly Color AcSummary = new Color(0.35f, 0.78f, 0.48f);
        private static readonly Color AcThreads = JournalCategoryVisuals.Threads;
        private static readonly Color AcWorld = JournalCategoryVisuals.World;
        private static readonly Color AcFactions = JournalCategoryVisuals.Factions;
        // World Threads (AcWorld, orange) and the World history tab share the same underlying
        // pipeline for pending-indicator purposes, but need visually distinct nav accents so the
        // two rows don't read as one tab.
        private static readonly Color AcWorldHistory = new Color(0.82f, 0.74f, 0.22f);
        private static readonly string[] PendingGlyphs = { "◐", "◓", "◑", "◒" };

        private int    _nav     = NavToday;
        private string _section = "EVENTS";
        private NavRoot _activeRoot = NavRoot.Colony;

        private readonly Dictionary<int, string>    _navSectionMemory = new Dictionary<int, string>();
        private readonly Dictionary<string, Vector2> _scrolls         = new Dictionary<string, Vector2>();
        private Vector2 _navScroll      = Vector2.zero;
        private string  _selectedThreadId = null;
        private string  _selectedFactionKey = null;

        public override Vector2 RequestedTabSize => new Vector2(1320f, 430f);

        // ── Entry point ───────────────────────────────────────────────────────

        public override void DoWindowContents(Rect inRect)
        {
            var ledger = ColonyLedger.Current;
            var world = FireflyWorldComponent.Current;
            if (ledger == null && (world == null ||
                (world.WorldThreads.Count == 0 && world.FactionSnapshots.Count == 0)))
            { Widgets.Label(inRect, "No active colony or world chronicle."); return; }

            var  past     = ledger?.PastDays ?? new List<DailyRecord>();
            int  today    = ledger?.RecordingDay ?? 0;
            bool hasToday = !past.Any(d => d.Day == today);

            if (ledger != null && !hasToday && past.Count == 0 && ledger.ColonyHistory.NullOrEmpty()
                && ledger.StoryThreads.Count == 0 && (world == null ||
                    (world.WorldThreads.Count == 0 && world.FactionSnapshots.Count == 0)))
            {
                Widgets.Label(inRect.ContractedBy(Pad),
                    "I have not begun this colony's chronicle yet.\n\nGive the day time to unfold, and I will remember it. — Fillion");
                return;
            }

            // Keep content selection valid within the active top-level section.
            if (ledger == null && (_activeRoot == NavRoot.Colony || _activeRoot == NavRoot.Threads))
                _activeRoot = NavRoot.Factions;

            if (_activeRoot == NavRoot.Colony)
            {
                bool validDay = _nav >= 0 && past.Any(d => d.Day == _nav);
                if (_nav == NavThreads || (_nav == NavToday && !hasToday) || (_nav >= 0 && !validDay))
                    SelectColonyDefault(hasToday, today, past);
            }
            else if (_activeRoot == NavRoot.Threads)
            {
                _nav = NavThreads;
                if (ledger.StoryThreads.All(t => t.Id != _selectedThreadId))
                    _selectedThreadId = ledger.StoryThreads.FirstOrDefault()?.Id;
            }
            else if (_activeRoot == NavRoot.World)
            {
                _nav = NavWorld;
                if (world == null || world.WorldThreads.All(t => t.Id != _selectedThreadId))
                    _selectedThreadId = world?.WorldThreads.FirstOrDefault()?.Id;
            }
            else if (_activeRoot == NavRoot.WorldHistory)
            {
                var worldDays = world?.DailyWorldRecords ?? new List<DailyWorldRecord>();
                bool validDay = _nav >= 0 && worldDays.Any(d => d.Day == _nav);
                if (_nav != NavWorldHistory && !validDay)
                    SelectWorldHistoryDefault(worldDays);
            }
            else
            {
                _nav = NavFactions;
                if (world == null || world.FactionSnapshots.All(f => f.Key != _selectedFactionKey))
                    _selectedFactionKey = world?.FactionSnapshots.FirstOrDefault()?.Key;
            }

            // Layout
            var navRect  = new Rect(inRect.x,        inRect.y, NavW,                       inRect.height);
            var divLine  = new Rect(inRect.x + NavW, inRect.y, 1f,                         inRect.height);
            var mainRect = new Rect(inRect.x + NavW + 1f + Pad, inRect.y,
                                    inRect.width - NavW - 1f - Pad, inRect.height);

            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            GUI.DrawTexture(divLine, BaseContent.WhiteTex);
            GUI.color = Color.white;

            DrawNav(navRect, hasToday, today, past, ledger?.StoryThreads ?? new List<StoryThread>(),
                world?.WorldThreads ?? new List<WorldThread>(),
                world?.FactionSnapshots ?? new List<FactionSnapshot>(),
                world?.DailyWorldRecords ?? new List<DailyWorldRecord>());
            DrawMain(mainRect, ledger, world, hasToday, today, past);
            DrawPendingIndicator(mainRect);
        }

        private static void DrawPendingIndicator(Rect mainRect)
        {
            if (!LLMClient.IsPending) return;

            var pending = JournalCategoryVisuals.PendingCategories();
            Color accent = pending.Count > 0 ? JournalCategoryVisuals.Blend(pending) : AcSummary;
            string category = pending.Count > 0
                ? string.Join(" + ", pending.Select(JournalCategoryVisuals.Name))
                : "Journal";
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
                             IReadOnlyList<StoryThread> threads, IReadOnlyList<WorldThread> worldThreads,
                             IReadOnlyList<FactionSnapshot> factions, IReadOnlyList<DailyWorldRecord> worldDays)
        {
            const float rowH = 26f;
            float viewWidth = rect.width - 16f;

            List<float> threadHeights = threads.Count > 0
                ? threads.Select(t => ThreadRowHeight(viewWidth, t, rowH)).ToList()
                : null;
            List<float> worldThreadHeights = worldThreads.Count > 0
                ? worldThreads.Select(t => WorldThreadRowHeight(viewWidth, t, rowH)).ToList()
                : null;
            List<float> factionHeights = factions.Count > 0
                ? factions.Select(f => FactionRowHeight(viewWidth, f, rowH)).ToList()
                : null;

            // Category rows are fixed above the independently scrolling child list, so a long
            // journal never pushes or compresses the four primary navigation choices.
            float y = rect.y;
            y = RootNavRow(y, rect.width, rowH, NavRoot.Colony, "Colony", AcJournal, JournalCategory.Colony,
                () =>
                {
                    _activeRoot = NavRoot.Colony;
                    SelectColonyDefault(hasToday, today, past);
                });
            y = RootNavRow(y, rect.width, rowH, NavRoot.Threads, "Colony Threads", AcThreads, JournalCategory.Threads,
                () =>
                {
                    _activeRoot = NavRoot.Threads;
                    _nav = NavThreads;
                    _selectedThreadId = threads.FirstOrDefault()?.Id;
                    _section = _navSectionMemory.TryGetValue(NavThreads, out string memory)
                        ? memory : "ACTIVE SUMMARY";
                });
            y = RootNavRow(y, rect.width, rowH, NavRoot.Factions, "Factions", AcFactions, JournalCategory.Factions,
                () =>
                {
                    _activeRoot = NavRoot.Factions;
                    _nav = NavFactions;
                    _selectedFactionKey = factions.FirstOrDefault()?.Key;
                    _section = "FACTS";
                });
            y = RootNavRow(y, rect.width, rowH, NavRoot.WorldHistory, "World", AcWorldHistory, JournalCategory.World,
                () =>
                {
                    _activeRoot = NavRoot.WorldHistory;
                    SelectWorldHistoryDefault(worldDays);
                });
            y = RootNavRow(y, rect.width, rowH, NavRoot.World, "World Threads", AcWorld, JournalCategory.World,
                () =>
                {
                    _activeRoot = NavRoot.World;
                    _nav = NavWorld;
                    _selectedThreadId = worldThreads.FirstOrDefault()?.Id;
                    _section = _navSectionMemory.TryGetValue(NavWorld, out string memory)
                        ? memory : "SUMMARY";
                });
            y = NavDivider(y, rect.width, rowH);

            var childRect = new Rect(rect.x, y, rect.width, Mathf.Max(0f, rect.yMax - y));
            float childH = _activeRoot == NavRoot.Colony
                ? (1 + (hasToday ? 1 : 0) + past.Count) * rowH
                : _activeRoot == NavRoot.Threads && threadHeights != null
                    ? threadHeights.Sum()
                    : _activeRoot == NavRoot.World && worldThreadHeights != null
                        ? worldThreadHeights.Sum()
                        : _activeRoot == NavRoot.Factions && factionHeights != null
                            ? factionHeights.Sum()
                            : _activeRoot == NavRoot.WorldHistory
                                ? (1 + worldDays.Count) * rowH
                                : rowH;
            var view = new Rect(0f, 0f, viewWidth, Mathf.Max(childH, childRect.height));
            Widgets.BeginScrollView(childRect, ref _navScroll, view);

            float childY = 0f;
            if (_activeRoot == NavRoot.Colony)
            {
                childY = NavRow(childY, view.width, rowH, NavColony, "History", "◆", AcJournal);
                if (hasToday)
                    childY = NavRow(childY, view.width, rowH, NavToday, $"Day {today}", "●", AcJournal);
                foreach (var record in past.OrderByDescending(d => d.Day))
                {
                    bool hasSummary = !record.Summary.NullOrEmpty();
                    childY = NavRow(childY, view.width, rowH, record.Day, $"Day {record.Day}",
                        hasSummary ? "✓" : "·", hasSummary ? AcSummary : Color.gray);
                }
            }
            else if (_activeRoot == NavRoot.Threads && threads.Count > 0)
            {
                for (int i = 0; i < threads.Count; i++)
                    childY = ThreadNavRow(childY, view.width, threads[i], threadHeights[i]);
            }
            else if (_activeRoot == NavRoot.Threads)
                childY = NavRow(childY, view.width, rowH, NavThreads, "No threads yet", "·", AcThreads);
            else if (_activeRoot == NavRoot.World && worldThreads.Count > 0)
            {
                for (int i = 0; i < worldThreads.Count; i++)
                    childY = WorldThreadNavRow(childY, view.width, worldThreads[i], worldThreadHeights[i]);
            }
            else if (_activeRoot == NavRoot.World)
                childY = NavRow(childY, view.width, rowH, NavWorld, "No world threads yet", "·", AcWorld);
            else if (_activeRoot == NavRoot.WorldHistory)
            {
                childY = NavRow(childY, view.width, rowH, NavWorldHistory, "History", "◆", AcWorldHistory);
                foreach (var record in worldDays.OrderByDescending(d => d.Day))
                {
                    bool hasSim = !record.Simulation.NullOrEmpty();
                    childY = NavRow(childY, view.width, rowH, record.Day, $"Day {record.Day}",
                        hasSim ? "✓" : "·", hasSim ? AcSummary : Color.gray);
                }
            }
            else if (factions.Count > 0)
            {
                for (int i = 0; i < factions.Count; i++)
                    childY = FactionNavRow(childY, view.width, factions[i], factionHeights[i]);
            }
            else
                NavRow(childY, view.width, rowH, NavFactions, "No factions tracked", "·", AcFactions);

            Widgets.EndScrollView();
        }

        private void SelectColonyDefault(bool hasToday, int today, IReadOnlyList<DailyRecord> past)
        {
            _nav = hasToday ? NavToday : past.Count > 0 ? past.Max(d => d.Day) : NavColony;
            _section = _navSectionMemory.TryGetValue(_nav, out string memory)
                ? memory
                : DefaultSection(_nav);
        }

        private void SelectWorldHistoryDefault(IReadOnlyList<DailyWorldRecord> worldDays)
        {
            _nav = worldDays.Count > 0 ? worldDays.Max(d => d.Day) : NavWorldHistory;
            _section = _navSectionMemory.TryGetValue(_nav, out string memory)
                ? memory
                : DefaultSection(_nav);
        }

        private float RootNavRow(float y, float w, float rowH, NavRoot root, string label,
                                 Color accent, JournalCategory pendingCategory, System.Action onClick)
        {
            Text.Font = GameFont.Small;
            var row = new Rect(0f, y, w, rowH);
            bool selected = _activeRoot == root;
            bool pending = JournalCategoryVisuals.IsPending(pendingCategory);

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
            // Text.fontStyles itself does not have wrapping enabled; Widgets.Label renders via
            // Text.CurFontStyle, which does. Measuring the raw style therefore always returned a
            // single-line height even when the visible label wrapped across several lines.
            Text.Font = GameFont.Small;
            bool priorWordWrap = Text.WordWrap;
            Text.WordWrap = true;
            float textH = Text.CalcHeight(thread.Name ?? "", labelW);
            Text.WordWrap = priorWordWrap;
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
            bool priorWordWrap = Text.WordWrap;
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = selected ? Color.white : new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(new Rect(14f, y, labelW, h), thread.Name);
            Text.WordWrap = priorWordWrap;

            // Centered, not right-anchored — a right-anchored hollow glyph like "◇" presses its
            // outline against the rect edge, where RimWorld clips part of it. Barely visible for
            // most badge glyphs, but noticeable on the low-contrast idle diamond.
            Text.Anchor = TextAnchor.MiddleCenter;
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
                _section = _navSectionMemory.TryGetValue(NavThreads, out string memory) ? memory : "ACTIVE SUMMARY";
            }
            return y + h;
        }

        private static float WorldThreadRowHeight(float w, WorldThread thread, float rowH)
        {
            Text.Font = GameFont.Small;
            bool priorWordWrap = Text.WordWrap;
            Text.WordWrap = true;
            float h = Mathf.Max(rowH, Text.CalcHeight(thread.Title ?? "", w - 54f) + 6f);
            Text.WordWrap = priorWordWrap;
            return h;
        }

        private float WorldThreadNavRow(float y, float w, WorldThread thread, float h)
        {
            var row = new Rect(0f, y, w, h);
            bool selected = _nav == NavWorld && _selectedThreadId == thread.Id;
            bool working = FireflyWorldComponent.IsWorldThreadWorking;
            long now = GenTicks.TicksAbs;
            bool updatedToday = thread.LastTouchedTick > 0L && now >= thread.LastTouchedTick
                && now - thread.LastTouchedTick < GenDate.TicksPerDay;

            if (selected)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.09f);
                GUI.DrawTexture(row, BaseContent.WhiteTex);
                GUI.color = AcWorld;
                GUI.DrawTexture(new Rect(0f, y + 3f, 3f, h - 6f), BaseContent.WhiteTex);
            }
            else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            else
            {
                GUI.color = new Color(AcWorld.r, AcWorld.g, AcWorld.b, 0.035f);
                GUI.DrawTexture(row, BaseContent.WhiteTex);
            }

            Text.Font = GameFont.Small;
            bool priorWordWrap = Text.WordWrap;
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = selected ? Color.white : new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(new Rect(14f, y, w - 54f, h), thread.Title);
            Text.WordWrap = priorWordWrap;

            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = AcWorld;
            var badgeRect = new Rect(w - 39f, y, 36f, h);
            string badge = working
                ? PendingGlyphs[Mathf.FloorToInt(Time.realtimeSinceStartup * 4f) % PendingGlyphs.Length]
                : updatedToday ? "NEW" : "◇";
            Widgets.Label(badgeRect, badge);
            if (working) TooltipHandler.TipRegion(badgeRect, "Fillion is advancing the world chronicle.");
            else if (updatedToday) TooltipHandler.TipRegion(badgeRect, "This world thread changed today.");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonInvisible(row))
            {
                _nav = NavWorld;
                _selectedThreadId = thread.Id;
                _section = _navSectionMemory.TryGetValue(NavWorld, out string memory) ? memory : "SUMMARY";
            }
            return y + h;
        }

        private static float FactionRowHeight(float w, FactionSnapshot faction, float rowH)
        {
            Text.Font = GameFont.Small;
            bool priorWordWrap = Text.WordWrap;
            Text.WordWrap = true;
            float h = Mathf.Max(rowH, Text.CalcHeight(faction.FactionName ?? "", w - 40f) + 6f);
            Text.WordWrap = priorWordWrap;
            return h;
        }

        private float FactionNavRow(float y, float w, FactionSnapshot faction, float h)
        {
            var row = new Rect(0f, y, w, h);
            bool selected = _nav == NavFactions && _selectedFactionKey == faction.Key;
            // A faction sitting between bootstrap stages (facts done, description or tagline not
            // yet started/finished) was showing as idle — the badge only ever checked Facts, not
            // the two calls that follow it in the same bootstrap sequence.
            bool working = FireflyWorldComponent.IsFactionFactsWorking(faction.Key) ||
                FireflyWorldComponent.IsFactionDescriptionWorking(faction.Key) ||
                FireflyWorldComponent.IsFactionTaglineWorking(faction.Key);

            if (selected)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.09f);
                GUI.DrawTexture(row, BaseContent.WhiteTex);
                GUI.color = AcFactions;
                GUI.DrawTexture(new Rect(0f, y + 3f, 3f, h - 6f), BaseContent.WhiteTex);
            }
            else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            else
            {
                GUI.color = new Color(AcFactions.r, AcFactions.g, AcFactions.b, 0.035f);
                GUI.DrawTexture(row, BaseContent.WhiteTex);
            }

            if (working)
            {
                float wave = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 5f);
                GUI.color = new Color(AcFactions.r, AcFactions.g, AcFactions.b, 0.42f + wave * 0.5f);
                const float edge = 1.5f;
                GUI.DrawTexture(new Rect(row.x, row.y, row.width, edge), BaseContent.WhiteTex);
                GUI.DrawTexture(new Rect(row.x, row.yMax - edge, row.width, edge), BaseContent.WhiteTex);
                GUI.DrawTexture(new Rect(row.x, row.y, edge, row.height), BaseContent.WhiteTex);
                GUI.DrawTexture(new Rect(row.xMax - edge, row.y, edge, row.height), BaseContent.WhiteTex);
            }

            Text.Font = GameFont.Small;
            bool priorWordWrap = Text.WordWrap;
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = selected ? Color.white : new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(new Rect(14f, y, w - 40f, h), faction.FactionName);
            Text.WordWrap = priorWordWrap;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = AcFactions;
            var badgeRect = new Rect(w - 28f, y, 24f, h);
            Widgets.Label(badgeRect, working
                ? PendingGlyphs[Mathf.FloorToInt(Time.realtimeSinceStartup * 4f) % PendingGlyphs.Length]
                : "◇");
            if (working)
                TooltipHandler.TipRegion(badgeRect, "Fillion is writing this faction's opening chronicle.");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonInvisible(row))
            {
                _nav = NavFactions;
                _selectedFactionKey = faction.Key;
                _section = "FACTS";
            }
            return y + h;
        }

        // Most NavRow labels ("History", "Day 12") are always short and fit one line at rowH, but
        // the empty-state messages ("No world threads yet", "No factions tracked") genuinely don't
        // fit the sidebar's width on one line — same measure-and-grow treatment ThreadRowHeight/
        // WorldThreadRowHeight/FactionRowHeight already give populated rows, just applied here too
        // instead of assuming every NavRow is single-line.
        private static float NavRowHeight(float w, string label, float rowH)
        {
            Text.Font = GameFont.Small;
            bool priorWordWrap = Text.WordWrap;
            Text.WordWrap = true;
            float textH = Text.CalcHeight(label ?? "", w - 26f);
            Text.WordWrap = priorWordWrap;
            return Mathf.Max(rowH, textH + 6f);
        }

        private float NavRow(float y, float w, float rowH, int id, string label, string badge, Color accent)
        {
            Text.Font = GameFont.Small;
            float h = NavRowHeight(w, label, rowH);
            bool priorWordWrap = Text.WordWrap;
            Text.WordWrap = true;
            var r   = new Rect(0f, y, w, h);
            bool sel = _nav == id;

            // Background
            if (sel)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.09f);
                GUI.DrawTexture(r, BaseContent.WhiteTex);
                GUI.color = accent;
                GUI.DrawTexture(new Rect(0f, y + 3f, 3f, h - 6f), BaseContent.WhiteTex);
            }
            else if (Mouse.IsOver(r))
                Widgets.DrawHighlight(r);
            GUI.color = Color.white;

            // Label
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color   = sel ? Color.white : new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(new Rect(9f, y, w - 26f, h), label);
            Text.WordWrap = priorWordWrap;

            // Badge
            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color   = accent;
            Widgets.Label(new Rect(0f, y, w - 3f, h), badge);

            GUI.color   = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            if (Widgets.ButtonInvisible(r))
            {
                _nav     = id;
                _section = _navSectionMemory.TryGetValue(id, out string mem) ? mem : DefaultSection(id);
            }
            return y + h;
        }

        private static string DefaultSection(int navId) => navId >= 0 ? "SUMMARY" : "EVENTS";

        // ── Main content area ─────────────────────────────────────────────────

        private void DrawMain(Rect rect, ColonyLedger ledger, FireflyWorldComponent world,
                              bool hasToday, int today, IReadOnlyList<DailyRecord> past)
        {
            if (_nav == NavWorld)             { DrawWorldThreads(rect, world); return; }
            if (_nav == NavFactions)          { DrawFaction(rect, world); return; }
            if (_activeRoot == NavRoot.WorldHistory) { DrawWorldHistoryMain(rect, world); return; }
            if (ledger == null) return;
            if (_nav == NavToday && hasToday) { DrawToday(rect, ledger, today); return; }
            if (_nav == NavColony)            { DrawColony(rect, ledger); return; }
            if (_nav == NavThreads)           { DrawThreads(rect, ledger); return; }

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
            if (!combatContent.NullOrEmpty())       secs.Add(("COMBAT",  AcJournal, combatContent));
            if (!hazardContent.NullOrEmpty())       secs.Add(("HAZARDS", AcJournal, hazardContent));
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

        // ── World history view ────────────────────────────────────────────────
        // Mirrors the Colony history view above: a rolling arc summary plus one row per day the
        // World Outcome LLM has actually produced a simulation for. Unlike a colony day, a world
        // day has no separately-recorded raw timeline to summarise — the simulation text already
        // is Fillion's account of that day, so DrawWorldDay only ever shows the one section.

        private void DrawWorldHistoryMain(Rect rect, FireflyWorldComponent world)
        {
            var worldDays = world?.DailyWorldRecords ?? new List<DailyWorldRecord>();
            if (_nav == NavWorldHistory) { DrawWorldHistory(rect, world); return; }

            var rec = worldDays.FirstOrDefault(d => d.Day == _nav);
            if (rec != null) DrawWorldDay(rect, rec);
            else DrawWorldHistory(rect, world);
        }

        private void DrawWorldHistory(Rect rect, FireflyWorldComponent world)
        {
            string history = world?.WorldHistory ?? "";
            var secs = new List<(string, Color, string)>
            {
                ("WORLD HISTORY", AcWorldHistory, history.NullOrEmpty()
                    ? "I am still gathering the shape of this world's story. In time, its history will find its voice. — Fillion"
                    : history),
            };
            DrawSectioned(rect, secs, "world_history", AcWorldHistory, "WORLD HISTORY");
        }

        private void DrawWorldDay(Rect rect, DailyWorldRecord record)
        {
            bool hasSim = !record.Simulation.NullOrEmpty();
            string simText = hasSim ? record.Simulation
                : "I have not yet set down how this day unfolded across the world. — Fillion";

            var secs = new List<(string Name, Color Ac, string Text)>
            {
                ("OUTCOME", hasSim ? AcWorldHistory : Color.gray, simText),
            };

            DrawSectioned(rect, secs, $"worldday{record.Day}", AcWorldHistory, $"DAY {record.Day}");
        }

        // ── Shared journal sections (Story Threads / Factions / World Threads) ─
        // All three subject types share one JournalRecord and run through the same
        // Facts -> chunk -> Active Summary pipeline (JournalSummaryService), so their journal-tab
        // sections are built from one place rather than three hand-copied blocks that can silently
        // drift (as they had — Factions were missing the dev-mode CHUNKS section Story Threads and
        // World Threads both had).

        // ACTIVE SUMMARY + FACTS — the two sections every JournalRecord-backed subject always has.
        private static List<(string Name, Color Ac, string Text)> JournalSummaryAndFactsSections(
            JournalRecord journal, Color accent, string emptySummaryText, string emptyFactsText,
            string summaryLabel = "ACTIVE SUMMARY", string factsLabel = "FACTS", Color? summaryAccent = null)
        {
            var facts = new StringBuilder();
            foreach (var fact in journal.Facts.Where(f => f != null).OrderBy(f => f.Day))
                facts.AppendLine($"  - [Day {fact.Day}] {fact.Text}");

            return new List<(string Name, Color Ac, string Text)>
            {
                (summaryLabel, summaryAccent ?? AcSummary, journal.ActiveSummary.NullOrEmpty() ? emptySummaryText : journal.ActiveSummary),
                (factsLabel, accent, facts.Length == 0 ? emptyFactsText : facts.ToString().TrimEnd()),
            };
        }

        // Chunks are deliberately hidden from normal play — they're internal source material for
        // the summarizer, not part of the player-facing record the FACTS section shows. Dev mode
        // only, same gating as the LLM IN/OUT debug sections on a day's own view. Always appended
        // last, after any subject-specific sections the caller adds.
        private static void AppendChunksSectionIfDevMode(
            List<(string Name, Color Ac, string Text)> sections, JournalRecord journal, Color accent,
            string label = "CHUNKS")
        {
            if (!Prefs.DevMode) return;
            var chunks = new StringBuilder();
            if (journal.Chunks.Count == 0)
            {
                chunks.AppendLine("(none yet)");
            }
            else
            {
                foreach (var chunk in journal.Chunks)
                {
                    chunks.AppendLine($"[Day {chunk.StartDay}-{chunk.EndDay}] {chunk.Summary}");
                    chunks.AppendLine();
                }
            }
            chunks.AppendLine($"Unchunked facts: {journal.Facts.Count - journal.ChunkedThroughFactIndex} " +
                               $"(of {journal.Facts.Count} total, chunked through index {journal.ChunkedThroughFactIndex})");
            sections.Add((label, accent, chunks.ToString().TrimEnd()));
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

            var sections = JournalSummaryAndFactsSections(selected.Journal, AcThreads,
                "Fillion has not yet gathered this thread's changing story into a summary.",
                "No facts have entered this thread's chronicle yet.", summaryAccent: AcThreads);
            AppendChunksSectionIfDevMode(sections, selected.Journal, AcThreads);

            DrawSectioned(rect, sections, $"thread_{selected.Id}", AcThreads,
                selected.Name.ToUpperInvariant());
        }

        // ── World thread view ─────────────────────────────────────────────────

        private void DrawWorldThreads(Rect rect, FireflyWorldComponent world)
        {
            var selected = world?.WorldThreads.FirstOrDefault(t => t.Id == _selectedThreadId);
            if (selected == null)
            {
                var empty = new List<(string, Color, string)>
                {
                    ("WORLD", AcWorld,
                        "The wider world has not offered up a thread yet. When its powers begin to move, I will keep their story here. — Fillion"),
                };
                DrawSectioned(rect, empty, "world_empty", AcWorld, "WORLD THREADS");
                return;
            }

            // World Threads' Active Summary tab uses AcWorld like everything else in this tab,
            // not the shared green AcSummary — Josh wants World Threads visually all-orange.
            var sections = JournalSummaryAndFactsSections(selected.Journal, AcWorld,
                "Fillion has not yet gathered this thread's changing story into a summary.",
                "No developments yet.", summaryAccent: AcWorld);
            AppendChunksSectionIfDevMode(sections, selected.Journal, AcWorld);

            DrawSectioned(rect, sections, $"world_{selected.Id}", AcWorld, selected.Title.ToUpperInvariant());
        }

        // ── Faction view ──────────────────────────────────────────────────────

        private void DrawFaction(Rect rect, FireflyWorldComponent world)
        {
            var selected = world?.FactionSnapshots.FirstOrDefault(f => f.Key == _selectedFactionKey);
            if (selected == null)
            {
                var empty = new List<(string, Color, string)>
                {
                    ("FACTS", AcFactions, "No factions are currently tracked in the wider world."),
                };
                DrawSectioned(rect, empty, "factions_empty", AcFactions, "FACTIONS");
                return;
            }

            string currentStatus = string.Join("\n",
                selected.ToStatusLines().Select(line => "  - " + line));
            string religion = string.Join("\n",
                selected.ToReligionLines().Select(line => "  - " + line));
            string relationships = string.Join("\n",
                selected.ToRelationshipLines().Select(line => "  - " + line));

            // Two independent Facts->Summary pairs — Narrative (event-driven story, from Faction
            // Update) and Faction (stable characterization, from the bootstrap + shape filter).
            // Same shared section-builder used everywhere else, just called twice with distinct
            // labels rather than once.
            var narrativeSections = JournalSummaryAndFactsSections(selected.NarrativeJournal, AcFactions,
                "Fillion has not yet gathered their changing story into a summary.",
                "No events have entered this faction's story yet.",
                summaryLabel: "THREAD SUMMARY", factsLabel: "THREAD FACTS", summaryAccent: AcFactions);
            var factionSections = JournalSummaryAndFactsSections(selected.FactionJournal, AcFactions,
                "Fillion has not yet described who they are.",
                "No characterization facts recorded yet.",
                summaryLabel: "DESCRIPTION", factsLabel: "FACTION FACTS", summaryAccent: AcFactions);

            var sections = new List<(string Name, Color Ac, string Text)>
            {
                ("TAGLINE", AcFactions, selected.Tagline.NullOrEmpty()
                    ? "Fillion has not yet formed a working read on this faction."
                    : selected.Tagline),
                narrativeSections[0], // THREAD SUMMARY
                factionSections[0],   // DESCRIPTION
                ("STATUS", AcFactions, currentStatus),
                ("RELIGION", AcFactions, religion),
                ("RELATIONSHIPS", AcFactions, relationships),
                narrativeSections[1], // THREAD FACTS
                factionSections[1],   // FACTION FACTS
            };
            AppendChunksSectionIfDevMode(sections, selected.NarrativeJournal, AcFactions, "THREAD CHUNKS");
            AppendChunksSectionIfDevMode(sections, selected.FactionJournal, AcFactions, "FACTION CHUNKS");

            DrawSectioned(rect, sections, $"faction_{selected.Key}", AcFactions,
                selected.FactionName.ToUpperInvariant());
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
                ("SUMMARY", hasSummary ? AcJournal : Color.gray, summaryText),
                ("EVENTS",  AcJournal, eventsContent),
            };
            if (!statusContent.NullOrEmpty())            secs.Add(("STATUS",  AcJournal, statusContent));
            if (!combatContent.NullOrEmpty())            secs.Add(("COMBAT",  AcJournal, combatContent));
            if (!hazardContent.NullOrEmpty())            secs.Add(("HAZARDS", AcJournal, hazardContent));
            if (!record.QuestSnapshot.NullOrEmpty())     secs.Add(("QUESTS",  AcJournal, record.QuestSnapshot));
            if (Prefs.DevMode)
            {
                string prevSummary = past.FirstOrDefault(d => d.Day == record.Day - 1 && !d.Summary.NullOrEmpty())?.Summary;
                string llmIn = prevSummary.NullOrEmpty()
                    ? (record.Timeline ?? "")
                    : $"=== PREVIOUS DAY SUMMARY (context only — do not summarise this) ===\n{prevSummary.Trim()}\n\n{record.Timeline}";
                secs.Add(("LLM IN", AcJournal, llmIn));
                secs.Add(("LLM OUT", AcJournal, summaryText));
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

            // These tabs are deliberately single-line even when the surrounding UI has enabled
            // wrapping. Restore the caller's setting afterward because Unity text state is global.
            bool priorWordWrap = Text.WordWrap;
            Text.WordWrap = false;

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

            Text.WordWrap = priorWordWrap;
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
            _selectedFactionKey = null;
        }
    }
}
