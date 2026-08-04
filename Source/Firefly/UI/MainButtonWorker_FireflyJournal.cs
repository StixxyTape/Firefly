using RimWorld;
using Verse;

namespace Firefly
{
    public class MainButtonWorker_FireflyJournal : MainButtonWorker_ToggleTab
    {
        public override bool Visible
        {
            get
            {
                var comp = Current.Game?.GetComponent<FireflyGameComponent>();
                return comp?.FireflyEnabled ?? false;
            }
        }
    }
}
