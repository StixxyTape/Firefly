using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Firefly
{
    public class MainTabWindow_Journal : MainTabWindow
    {
        private const float DayListWidth    = 180f;
        private const float SummaryHeight   = 160f;
        private const float HistoryHeight   = 120f;
        private const float Padding         = 8f;

        private int     _selectedDay    = -1;
        private Vector2 _dayListScroll  = Vector2.zero;
        private Vector2 _contentScroll  = Vector2.zero;
        private Vector2 _historyScroll  = Vector2.zero;

        public override Vector2 RequestedTabSize => new Vector2(900f, 620f);

        public override void DoWindowContents(Rect inRect)
        {
            var ledger = ColonyLedger.Current;

            if (ledger == null)
            {
                Widgets.Label(inRect, "No active colony.");
                return;
            }

            var past = ledger.PastDays;

            if (past.Count == 0 && ledger.ColonyHistory.NullOrEmpty())
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(inRect, "No journal entries yet.\n\nThe journal starts recording once Fillion is your active storyteller.");
                Text.Font = GameFont.Small;
                return;
            }

            // ── Layout ────────────────────────────────────────────────────────
            var dayListRect  = new Rect(inRect.x,                     inRect.y, DayListWidth,               inRect.height);
            var dividerRect  = new Rect(inRect.x + DayListWidth,      inRect.y, 1f,                         inRect.height);
            var contentRect  = new Rect(inRect.x + DayListWidth + 1f, inRect.y, inRect.width - DayListWidth - 1f, inRect.height);

            Widgets.DrawLineVertical(dividerRect.x, dividerRect.y, dividerRect.height);

            DrawDayList(dayListRect, past);
            DrawContent(contentRect, ledger, past);
        }

        // ── Left panel: scrollable day list ───────────────────────────────────

        private void DrawDayList(Rect rect, IReadOnlyList<DailyRecord> past)
        {
            float rowH   = 28f;
            float totalH = past.Count * rowH;

            var viewRect = new Rect(0f, 0f, rect.width - 16f, Mathf.Max(totalH, rect.height));
            Widgets.BeginScrollView(rect, ref _dayListScroll, viewRect);

            var sorted = past.OrderByDescending(d => d.Day).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                var   record  = sorted[i];
                var   rowRect = new Rect(0f, i * rowH, viewRect.width, rowH);
                bool  selected = _selectedDay == record.Day;

                if (selected)
                    Widgets.DrawHighlightSelected(rowRect);
                else if (Mouse.IsOver(rowRect))
                    Widgets.DrawHighlight(rowRect);

                // Day number + summary status indicator
                string label    = $"Day {record.Day}";
                string status   = record.Summary.NullOrEmpty() ? " ·" : " ✓";
                Color  statusColor = record.Summary.NullOrEmpty() ? Color.gray : new Color(0.4f, 0.9f, 0.4f);

                var labelRect  = rowRect.LeftPartPixels(rowRect.width - 18f).ContractedBy(2f);
                var statusRect = new Rect(rowRect.xMax - 18f, rowRect.y, 16f, rowRect.height);

                Widgets.Label(labelRect, label);
                GUI.color = statusColor;
                Widgets.Label(statusRect, status);
                GUI.color = Color.white;

                if (Widgets.ButtonInvisible(rowRect))
                    _selectedDay = record.Day;
            }

            Widgets.EndScrollView();
        }

        // ── Right panel: content ──────────────────────────────────────────────

        private void DrawContent(Rect rect, ColonyLedger ledger, IReadOnlyList<DailyRecord> past)
        {
            rect = rect.ContractedBy(Padding);

            // Decide what to show: colony history banner + selected day content
            bool showHistory = !ledger.ColonyHistory.NullOrEmpty();
            float historyBlockH = showHistory ? HistoryHeight + Padding : 0f;

            // Colony history at the top
            if (showHistory)
            {
                var historyLabelRect = new Rect(rect.x, rect.y, rect.width, Text.LineHeight);
                Text.Font = GameFont.Medium;
                Widgets.Label(historyLabelRect, "Colony History");
                Text.Font = GameFont.Small;

                var historyBoxRect = new Rect(rect.x, rect.y + Text.LineHeight + 2f, rect.width, HistoryHeight - Text.LineHeight - 2f);
                DrawScrollableText(historyBoxRect, ledger.ColonyHistory, ref _historyScroll);
            }

            // Divider
            float afterHistory = rect.y + historyBlockH;
            if (showHistory)
            {
                Widgets.DrawLineHorizontal(rect.x, afterHistory, rect.width);
                afterHistory += Padding;
            }

            var remaining = new Rect(rect.x, afterHistory, rect.width, rect.yMax - afterHistory);

            // Selected day
            DailyRecord selected = _selectedDay >= 0
                ? past.FirstOrDefault(d => d.Day == _selectedDay)
                : null;

            // Auto-select most recent if nothing chosen
            if (selected == null && past.Count > 0)
            {
                selected = past.OrderByDescending(d => d.Day).First();
                _selectedDay = selected.Day;
            }

            if (selected == null)
            {
                Widgets.Label(remaining, "Select a day from the list.");
                return;
            }

            DrawSelectedDay(remaining, selected);
        }

        private void DrawSelectedDay(Rect rect, DailyRecord record)
        {
            // Header
            Text.Font = GameFont.Medium;
            var headerRect = new Rect(rect.x, rect.y, rect.width, Text.LineHeight + 4f);
            Widgets.Label(headerRect, $"Day {record.Day}");
            Text.Font = GameFont.Small;
            float y = headerRect.yMax + Padding;

            bool hasSummary  = !record.Summary.NullOrEmpty();
            bool hasTimeline = !record.Timeline.NullOrEmpty();

            // Summary block
            if (hasSummary)
            {
                var summaryLabelRect = new Rect(rect.x, y, rect.width, Text.LineHeight);
                GUI.color = new Color(0.7f, 0.9f, 0.7f);
                Widgets.Label(summaryLabelRect, "Summary");
                GUI.color = Color.white;
                y += Text.LineHeight + 2f;

                float summaryH = Mathf.Min(SummaryHeight, rect.yMax - y - 40f);
                if (summaryH > 20f)
                {
                    var summaryBox = new Rect(rect.x, y, rect.width, summaryH);
                    Vector2 dummy = Vector2.zero;
                    DrawScrollableText(summaryBox, record.Summary, ref dummy);
                    y += summaryH + Padding;
                }
            }
            else
            {
                var pendingRect = new Rect(rect.x, y, rect.width, Text.LineHeight);
                GUI.color = Color.gray;
                Widgets.Label(pendingRect, "(summary pending — LLM request in progress)");
                GUI.color = Color.white;
                y += Text.LineHeight + Padding;
            }

            // Timeline block
            if (hasTimeline)
            {
                Widgets.DrawLineHorizontal(rect.x, y, rect.width);
                y += Padding;

                var timelineLabelRect = new Rect(rect.x, y, rect.width, Text.LineHeight);
                Widgets.Label(timelineLabelRect, "Day Log");
                y += Text.LineHeight + 2f;

                float timelineH = rect.yMax - y;
                if (timelineH > 20f)
                {
                    var timelineBox = new Rect(rect.x, y, rect.width, timelineH);
                    DrawScrollableText(timelineBox, record.Timeline, ref _contentScroll);
                }
            }
            else if (!hasSummary)
            {
                Widgets.Label(new Rect(rect.x, y, rect.width, Text.LineHeight), "No data recorded for this day.");
            }
        }

        private static void DrawScrollableText(Rect box, string text, ref Vector2 scroll)
        {
            Widgets.DrawMenuSection(box);
            var inner = box.ContractedBy(4f);

            float textH  = Text.CalcHeight(text, inner.width - 16f);
            var viewRect = new Rect(0f, 0f, inner.width - 16f, Mathf.Max(textH, inner.height));

            Widgets.BeginScrollView(inner, ref scroll, viewRect);
            Widgets.Label(new Rect(0f, 0f, viewRect.width, viewRect.height), text);
            Widgets.EndScrollView();
        }

        public override void PreOpen()
        {
            base.PreOpen();
            _contentScroll = Vector2.zero;
        }
    }
}
