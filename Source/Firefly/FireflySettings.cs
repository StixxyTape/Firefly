using Verse;

namespace Firefly
{
    public class FireflySettings : ModSettings
    {
        public string ApiKey = "";
        public string BaseUrl = "https://openrouter.ai/api/v1";
        public string Model = "google/gemini-2.0-flash";

        public override void ExposeData()
        {
            Scribe_Values.Look(ref ApiKey, "apiKey", "");
            Scribe_Values.Look(ref BaseUrl, "baseUrl", "https://openrouter.ai/api/v1");
            Scribe_Values.Look(ref Model, "model", "google/gemini-2.0-flash");
        }
    }
}
