using Verse;

namespace Firefly
{
    public class FireflySettings : ModSettings
    {
        public const int DefaultMaxPromptChars = 24000;
        public const int DefaultMaxCompletionTokens = 2048;

        public string ApiKey = "";
        public string BaseUrl = "https://openrouter.ai/api/v1";
        public string Model = "xiaomi/mimo-v2.5";
        public int MaxPromptChars = DefaultMaxPromptChars;
        public int MaxCompletionTokens = DefaultMaxCompletionTokens;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref ApiKey, "apiKey", "");
            Scribe_Values.Look(ref BaseUrl, "baseUrl", "https://openrouter.ai/api/v1");
            Scribe_Values.Look(ref Model, "model", "xiaomi/mimo-v2.5");
            Scribe_Values.Look(ref MaxPromptChars, "maxPromptChars", DefaultMaxPromptChars);
            Scribe_Values.Look(ref MaxCompletionTokens, "maxCompletionTokens", DefaultMaxCompletionTokens);

            if (Scribe.mode == LoadSaveMode.LoadingVars && MaxPromptChars < 2000)
                MaxPromptChars = DefaultMaxPromptChars;
            if (Scribe.mode == LoadSaveMode.LoadingVars && MaxCompletionTokens < 256)
                MaxCompletionTokens = DefaultMaxCompletionTokens;
        }
    }
}
