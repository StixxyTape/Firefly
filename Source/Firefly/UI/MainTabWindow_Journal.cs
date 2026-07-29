using System.Collections.Generic;
using System.Linq;
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

        private const float NavW  = 148f;
        private const float Pad   = 5f;
        private const float TabH  = 22f;

        // Section accent colours
        private static readonly Color AcSummary = new Color(0.35f, 0.78f, 0.48f);
        private static readonly Color AcEvents  = new Color(0.58f, 0.73f, 0.86f);
        private static readonly Color AcStatus  = new Color(0.52f, 0.62f, 0.84f);
        private static readonly Color AcCombat  = new Color(0.84f, 0.38f, 0.28f);
        private static readonly Color AcHazards = new Color(0.84f, 0.62f, 0.22f);
        private static readonly Color AcHistory = new Color(0.92f, 0.78f, 0.38f);
        private static readonly Color AcToday   = new Color(1.00f, 0.88f, 0.28f);

        private int    _nav     = NavToday;
        private string _section = "EVENTS";

        private readonly Dictionary<int, string>    _navSectionMemory = new Dictionary<int, string>();
        private readonly Dictionary<string, Vector2> _scrolls         = new Dictionary<string, Vector2>();
        private Vector2 _navScroll = Vector2.zero;

        public override Vector2 RequestedTabSize => new Vector2(1000f, 430f);

        // ── Entry point ───────────────────────────────────────────────────────

        public override void DoWindowContents(Rect inRect)
        {
            var ledger = ColonyLedger.Current;
            if (ledger == null) { Widgets.Label(inRect, "No active colony."); return; }

            var  past     = ledger.PastDays;
            int  today    = ledger.RecordingDay;
            bool hasToday = !past.Any(d => d.Day == today);

            if (!hasToday && past.Count == 0 && ledger.ColonyHistory.NullOrEmpty())
            {
                Widgets.Label(inRect.ContractedBy(Pad),
                    "No journal entries yet.\n\nThe journal starts recording once Fillion is your active storyteller.");
                return;
            }

            // Clamp nav to valid target
            if (_nav == NavToday && !hasToday)
                _nav = past.Count > 0 ? past.Max(d => d.Day) : NavColony;

            // Layout
            var navRect  = new Rect(inRect.x,        inRect.y, NavW,                       inRect.height);
            var divLine  = new Rect(inRect.x + NavW, inRect.y, 1f,                         inRect.height);
            var mainRect = new Rect(inRect.x + NavW + 1f + Pad, inRect.y,
                                    inRect.width - NavW - 1f - Pad, inRect.height);

            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            GUI.DrawTexture(divLine, BaseContent.WhiteTex);
            GUI.color = Color.white;

            DrawNav(navRect, hasToday, today, past);
            DrawMain(mainRect, ledger, hasToday, today, past);
        }

        // ── Navigation panel ──────────────────────────────────────────────────

        private void DrawNav(Rect rect, bool hasToday, int today, IReadOnlyList<DailyRecord> past)
        {
            const float rowH = 26f;
            int rows = (hasToday ? 1 : 0) + 1 + (past.Count > 0 ? past.Count + 1 : 0);
            var view = new Rect(0f, 0f, rect.width - 16f, Mathf.Max(rows * rowH, rect.height));
            Widgets.BeginScrollView(rect, ref _navScroll, view);

            float y = 0f;
            y = NavRow(y, view.width, rowH, NavColony, "Colony", "◆", AcHistory);

            if (hasToday || past.Count > 0)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.09f);
                GUI.DrawTexture(new Rect(6f, y + rowH * 0.45f, view.width - 12f, 1f), BaseContent.WhiteTex);
                GUI.color = Color.white;
                y += rowH * 0.8f;
            }

            if (hasToday)
                y = NavRow(y, view.width, rowH, NavToday, $"Day {today}", "●", AcToday);

            if (past.Count > 0)
            {
                foreach (var rec in past.OrderByDescending(d => d.Day))
                {
                    bool hasSummary = !rec.Summary.NullOrEmpty();
                    y = NavRow(y, view.width, rowH, rec.Day,
                        $"Day {rec.Day}",
                        hasSummary ? "✓" : "·",
                        hasSummary ? new Color(0.4f, 0.92f, 0.4f) : Color.gray);
                }
            }

            Widgets.EndScrollView();
        }

        private float NavRow(float y, float w, float rowH, int id, string label, string badge, Color accent)
        {
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

            var rec = past.FirstOrDefault(d => d.Day == _nav);
            if (rec != null) DrawDay(rect, rec);
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

            var secs = new List<(string Name, Color Ac, string Text)>
            {
                ("EVENTS", AcEvents, eventsContent),
            };
            if (!combatContent.NullOrEmpty())  secs.Add(("COMBAT",  AcCombat,  combatContent));
            if (!hazardContent.NullOrEmpty())  secs.Add(("HAZARDS", AcHazards, hazardContent));

            DrawSectioned(rect, secs, $"today{day}", AcToday, $"DAY {day}  —  IN PROGRESS");
        }

        // ── Colony history view ───────────────────────────────────────────────

        private void DrawColony(Rect rect, ColonyLedger ledger)
        {
            string history = ledger.ColonyHistory;
            var secs = new List<(string, Color, string)>
            {
                ("COLONY HISTORY", AcHistory, history.NullOrEmpty()
                    ? "(Colony history will appear here after the first arc summary, every 15 days.)"
                    : history),
            };
            DrawSectioned(rect, secs, "colony", AcHistory, "COLONY HISTORY");
        }

        // ── Past day view ─────────────────────────────────────────────────────

        private void DrawDay(Rect rect, DailyRecord record)
        {
            var parsed = ParseSections(record.Timeline);

            // Events = roster + events merged
            parsed.TryGetValue("EVENTS", out string evRaw);
            string eventsContent = "";
            if (parsed.TryGetValue("CHARACTER ROSTER", out string rRaw) && !rRaw.NullOrEmpty())
                eventsContent = "Character Roster:\n" + rRaw.Trim() + "\n\n";
            eventsContent += evRaw ?? "";

            // Status = health + relations + skills
            var statusParts = new System.Collections.Generic.List<string>();
            if (parsed.TryGetValue("COLONIST HEALTH",      out string h)  && !h.NullOrEmpty())  statusParts.Add(h.Trim());
            if (parsed.TryGetValue("RELATIONSHIP CHANGES",  out string r)  && !r.NullOrEmpty())  statusParts.Add(r.Trim());
            if (parsed.TryGetValue("SKILL CHANGES",         out string sk) && !sk.NullOrEmpty()) statusParts.Add(sk.Trim());
            string statusContent = string.Join("\n\n", statusParts);

            parsed.TryGetValue("COMBAT",  out string combatContent);
            parsed.TryGetValue("HAZARDS", out string hazardContent);

            bool hasSummary = !record.Summary.NullOrEmpty();
            string summaryText = hasSummary
                ? record.Summary
                : "(Summary pending — LLM request in progress)";

            var secs = new List<(string Name, Color Ac, string Text)>
            {
                ("SUMMARY", hasSummary ? AcSummary : Color.gray, summaryText),
                ("EVENTS",  AcEvents,  eventsContent),
            };
            if (!statusContent.NullOrEmpty())  secs.Add(("STATUS",  AcStatus,  statusContent));
            if (!combatContent.NullOrEmpty())  secs.Add(("COMBAT",  AcCombat,  combatContent));
            if (!hazardContent.NullOrEmpty())  secs.Add(("HAZARDS", AcHazards, hazardContent));
            secs.Add(("LLM IN",  new Color(0.65f, 0.55f, 0.80f), record.Timeline ?? ""));
            secs.Add(("LLM OUT", new Color(0.45f, 0.75f, 0.65f), summaryText));

            DrawSectioned(rect, secs, $"day{record.Day}", AcEvents, $"DAY {record.Day}");
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

            // Block typed characters so the area is read-only but still selectable/copyable
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && ev.character != '\0')
                ev.Use();

            var   style  = SelectableStyle;
            float textH  = style.CalcHeight(text, pad.width - 16f);
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
        }
    }
}
