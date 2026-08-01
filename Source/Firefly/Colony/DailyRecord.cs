using Verse;

namespace Firefly
{
    public class DailyRecord : IExposable
    {
        public int    Day;
        public string Timeline;
        public string Summary;       // null until LLM response arrives
        public string QuestSnapshot; // null if no quests were mentioned this day

        public void ExposeData()
        {
            Scribe_Values.Look(ref Day,           "day",           0);
            Scribe_Values.Look(ref Timeline,      "timeline",      "");
            Scribe_Values.Look(ref Summary,       "summary",       null);
            Scribe_Values.Look(ref QuestSnapshot, "questSnapshot", null);
        }
    }
}
