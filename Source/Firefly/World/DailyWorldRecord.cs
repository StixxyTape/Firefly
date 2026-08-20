using Verse;

namespace Firefly
{
    public class DailyWorldRecord : IExposable
    {
        public int Day;
        public string Simulation = "";

        public void ExposeData()
        {
            Scribe_Values.Look(ref Day, "day", 0);
            Scribe_Values.Look(ref Simulation, "simulation", "");
        }
    }
}
