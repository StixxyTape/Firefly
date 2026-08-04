using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Firefly
{
    // Injects a "Play with Firefly Chronicle" toggle onto the storyteller selection page.
    // The choice is read by FireflyGameComponent.FinalizeInit() on new game start and
    // persisted to the save file from that point on.
    [HarmonyPatch(typeof(Page_SelectStoryteller), nameof(Page_SelectStoryteller.DoWindowContents))]
    public static class Patch_StorytellerPage
    {
        public static bool FireflyEnabled = true;

        static void Postfix(Rect rect)
        {
            // 38f = standard RimWorld page button height; 36f gap above it
            var checkRect = new Rect(rect.x, rect.yMax - 38f - 36f, 280f, 30f);
            Widgets.CheckboxLabeled(checkRect, "Play with Firefly Chronicle", ref FireflyEnabled);
        }
    }
}
