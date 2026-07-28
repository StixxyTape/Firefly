using RimWorld;
using UnityEngine;
using Verse;

namespace Firefly
{
    public class MainButtonWorker_Journal : MainButtonWorker
    {
        public override void Activate() => Find.MainTabsRoot.ToggleTab(def);

        public override void DoButton(Rect rect)
        {
            bool isOpen = Find.MainTabsRoot?.OpenTab is MainTabWindow_Journal;

            if (isOpen) Widgets.DrawHighlightSelected(rect);

            if (Widgets.ButtonText(rect, def.LabelCap, drawBackground: !isOpen))
                Activate();
        }
    }
}
