using Verse;

namespace Firefly
{
    // A user-saved model preset, distinct from the small built-in list in FireflyMod — these are
    // whatever the player has actually typed into the Model field and chosen to keep around.
    public class ModelPreset : IExposable
    {
        public string Label = "";
        public string ModelId = "";

        public ModelPreset() { }
        public ModelPreset(string label, string modelId)
        {
            Label = label;
            ModelId = modelId;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref Label,   "label",   "");
            Scribe_Values.Look(ref ModelId, "modelId", "");
        }
    }
}
