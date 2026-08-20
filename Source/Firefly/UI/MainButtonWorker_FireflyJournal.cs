using RimWorld;
using UnityEngine;
using Verse;

namespace Firefly
{
    public class MainButtonWorker_FireflyJournal : MainButtonWorker_ToggleTab
    {
        private static readonly string[] PendingGlyphs = { "◐", "◓", "◑", "◒" };

        public override bool Visible
        {
            get
            {
                var comp = Current.Game?.GetComponent<FireflyGameComponent>();
                return comp?.FireflyEnabled ?? false;
            }
        }

        public override void DoButton(Rect rect)
        {
            base.DoButton(rect);
            if (!LLMClient.IsPending) return;

            var pending = JournalCategoryVisuals.PendingCategories();
            Color activityColor = pending.Count > 0
                ? JournalCategoryVisuals.Blend(pending)
                : Color.white;
            float wave = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 5f);
            Color pulse = new Color(activityColor.r, activityColor.g, activityColor.b, 0.55f + wave * 0.4f);

            // Pulsing outline keeps the activity visible even when the small badge is overlooked.
            GUI.color = pulse;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 2f), BaseContent.WhiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), BaseContent.WhiteTex);
            GUI.DrawTexture(new Rect(rect.x, rect.y, 2f, rect.height), BaseContent.WhiteTex);
            GUI.DrawTexture(new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), BaseContent.WhiteTex);

            float size = Mathf.Min(26f, rect.height - 4f);
            var badge = new Rect(rect.xMax - size - 2f, rect.y + 2f, size, size);
            GUI.color = new Color(0.08f, 0.06f, 0.09f, 0.94f);
            GUI.DrawTexture(badge, BaseContent.WhiteTex);
            GUI.color = pulse;
            GUI.DrawTexture(badge.ContractedBy(2f), BaseContent.WhiteTex);

            int frame = Mathf.FloorToInt(Time.realtimeSinceStartup * 4f) % PendingGlyphs.Length;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = Color.white;
            Widgets.Label(badge, PendingGlyphs[frame]);
            string categories = pending.Count > 0
                ? string.Join(" + ", pending.ConvertAll(JournalCategoryVisuals.Name))
                : "Journal";
            TooltipHandler.TipRegion(rect, $"Fillion is writing · {categories}.");

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }
    }
}
