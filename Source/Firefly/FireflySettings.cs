using System.Collections.Generic;
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
        // OpenRouter's unified "reasoning" request field — "" omits the field entirely (model's
        // own default), otherwise "low"/"medium"/"high" caps how much a reasoning-capable model
        // (DeepSeek, o-series, Gemini thinking, etc.) deliberates before answering. Ignored by
        // non-reasoning models and by endpoints that don't recognize the field.
        public string ReasoningEffort = "";
        // Player-saved models, separate from the small built-in preset list — whatever the
        // player has actually typed in and chosen to keep.
        public List<ModelPreset> CustomModelPresets = new List<ModelPreset>();

        public override void ExposeData()
        {
            Scribe_Values.Look(ref ApiKey, "apiKey", "");
            Scribe_Values.Look(ref BaseUrl, "baseUrl", "https://openrouter.ai/api/v1");
            Scribe_Values.Look(ref Model, "model", "xiaomi/mimo-v2.5");
            Scribe_Values.Look(ref MaxPromptChars, "maxPromptChars", DefaultMaxPromptChars);
            Scribe_Values.Look(ref MaxCompletionTokens, "maxCompletionTokens", DefaultMaxCompletionTokens);
            Scribe_Values.Look(ref ReasoningEffort, "reasoningEffort", "");
            Scribe_Collections.Look(ref CustomModelPresets, "customModelPresets", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.LoadingVars && MaxPromptChars < 2000)
                MaxPromptChars = DefaultMaxPromptChars;
            if (Scribe.mode == LoadSaveMode.LoadingVars && MaxCompletionTokens < 256)
                MaxCompletionTokens = DefaultMaxCompletionTokens;
            if (Scribe.mode == LoadSaveMode.LoadingVars && CustomModelPresets == null)
                CustomModelPresets = new List<ModelPreset>();
        }
    }
}
