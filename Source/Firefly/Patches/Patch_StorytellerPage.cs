using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Firefly
{
    // Injects a "Play with Firefly Chronicle" toggle into the button strip at the bottom of
    // the storyteller selection page, sitting between the Back button and the vanilla hint label.
    // The choice is read by FireflyGameComponent.FinalizeInit() on new game start and
    // persisted to the save file from that point on.
    [HarmonyPatch(typeof(Page_SelectStoryteller), nameof(Page_SelectStoryteller.DoWindowContents))]
    public static class Patch_StorytellerPage
    {
        public static bool FireflyEnabled = true;

        // BottomButSize = (150, 38); hint label is 200px wide, 6px gap before Next button.
        // Available center strip: rect.x+150 → rect.xMax-150-200-6
        private const float BtnW  = 150f;
        private const float BtnH  = 38f;
        private const float HintW = 200f + 6f;

        static void Postfix(Rect rect)
        {
            float y         = rect.yMax - BtnH;
            float x         = rect.x + BtnW + 17f;
            float maxW      = rect.xMax - BtnW - HintW - x - 10f;
            if (maxW < 120f) return;

            float w = Mathf.Min(260f, maxW);

            Text.Font   = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.CheckboxLabeled(new Rect(x, y, w, BtnH), "Play with Firefly Chronicle", ref FireflyEnabled);
            TooltipHandler.TipRegion(new Rect(x, y, w, BtnH),
                "Enable the Firefly journal for this colony. Events are recorded and summarised daily by an AI narrator.");
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
