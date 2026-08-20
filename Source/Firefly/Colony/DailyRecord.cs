using Verse;

namespace Firefly
{
    public class DailyRecord : IExposable
    {
        public int    Day;
        public string Timeline = "";
        public string Summary = "";       // empty until LLM response arrives
        public string QuestSnapshot = ""; // empty if no quests were mentioned this day

        public void ExposeData()
        {
            Scribe_Values.Look(ref Day,           "day",           0);
            Scribe_Values.Look(ref Timeline,      "timeline",      "");
            Scribe_Values.Look(ref Summary,       "summary",       "");
            Scribe_Values.Look(ref QuestSnapshot, "questSnapshot", "");
        }
    }
}
