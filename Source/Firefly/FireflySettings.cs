using System.Collections.Generic;
using Verse;

namespace Firefly
{
    public class FireflySettings : ModSettings
    {
        public const int DefaultMaxPromptChars = 24000;
        public const int DefaultMaxCompletionTokens = 2048;
        public const int DefaultRequestTimeoutSeconds = 60;
        public const int DefaultMaxRetries = 3;

        public string ApiKey = "";
        public string BaseUrl = "https://openrouter.ai/api/v1";
        public string Model = "xiaomi/mimo-v2.5";
        public int MaxPromptChars = DefaultMaxPromptChars;
        public int MaxCompletionTokens = DefaultMaxCompletionTokens;
        // Applies uniformly to every LLM call in the mod — no call site gets its own exception.
        public int RequestTimeoutSeconds = DefaultRequestTimeoutSeconds;
        // Retries after the first attempt — total attempts sent is this plus one. Every LLM call
        // in the mod goes through this one retry loop (LLMClient.SendAsync); there's no per-call
        // opt-out.
        public int MaxRetries = DefaultMaxRetries;
        // OpenRouter's unified "reasoning" request field — "" omits the field entirely (model's
        // own default), otherwise "low"/"medium"/"high" caps how much a reasoning-capable model
        // (DeepSeek, o-series, Gemini thinking, etc.) deliberates before answering. Ignored by
        // non-reasoning models and by endpoints that don't recognize the field.
        public string ReasoningEffort = "";
        // Per-call override of the reasoning effort above, keyed by LLMClient.Send's "label"
        // (e.g. "DailyColonyThreadScan", "DailyColonySummary", "RaidNarrative"). A missing key means "inherit
        // ReasoningEffort"; a present key — including "" for an explicit "Model default" —
        // always wins over the global setting for that one call site.
        public Dictionary<string, string> PerLabelReasoningEffort = new Dictionary<string, string>();
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
            Scribe_Values.Look(ref RequestTimeoutSeconds, "requestTimeoutSeconds", DefaultRequestTimeoutSeconds);
            Scribe_Values.Look(ref MaxRetries, "maxRetries", DefaultMaxRetries);
            Scribe_Values.Look(ref ReasoningEffort, "reasoningEffort", "");
            Scribe_Collections.Look(ref PerLabelReasoningEffort, "perLabelReasoningEffort", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref CustomModelPresets, "customModelPresets", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.LoadingVars && MaxPromptChars < 2000)
                MaxPromptChars = DefaultMaxPromptChars;
            if (Scribe.mode == LoadSaveMode.LoadingVars && MaxCompletionTokens < 256)
                MaxCompletionTokens = DefaultMaxCompletionTokens;
            if (Scribe.mode == LoadSaveMode.LoadingVars && RequestTimeoutSeconds < 15)
                RequestTimeoutSeconds = DefaultRequestTimeoutSeconds;
            if (Scribe.mode == LoadSaveMode.LoadingVars && (MaxRetries < 0 || MaxRetries > 10))
                MaxRetries = DefaultMaxRetries;
            if (Scribe.mode == LoadSaveMode.LoadingVars && CustomModelPresets == null)
                CustomModelPresets = new List<ModelPreset>();
            if (Scribe.mode == LoadSaveMode.LoadingVars && PerLabelReasoningEffort == null)
                PerLabelReasoningEffort = new Dictionary<string, string>();
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                MigrateRenamedLabelKeys();
        }

        // One-time migration for the 2026-08-20 label rename — old saved per-call reasoning
        // overrides were keyed by the pre-rename label strings (e.g. "DailySummary") and would
        // otherwise silently stop applying (orphaned key, falls back to the global default) once
        // the call sites started sending their new label strings instead. A value already saved
        // under the new key wins if both happen to be present; old keys are removed either way so
        // this doesn't need to run more than once per save.
        private static readonly Dictionary<string, string> RenamedLabelKeys = new Dictionary<string, string>
        {
            { "DailySummary", "DailyColonySummary" },
            { "ThreadScan", "DailyColonyThreadScan" },
            { "ArcHistory", "MonthlyColonyArcHistory" },
            { "JournalChunk", "ThreadFactChunker" },
            { "JournalSummary", "ThreadSummariser" },
            { "WorldSeed", "InitialWorldThreadSeeder" },
            { "WorldOutcome", "DailyWorldProgression" },
            { "WorldThreadUpdates", "DailyWorldThreadScan" },
            { "WorldJournalChunk", "WorldThreadFactChunker" },
            { "WorldJournalSummary", "WorldThreadSummariser" },
            { "WorldArcHistory", "MonthlyWorldArcHistory" },
            { "FactionUpdate", "DailyWorldThreadFactionScan" },
            { "FactionJournalChunk", "FactionNarrativeChunker" },
            { "FactionJournalSummary", "FactionNarrativeSummariser" },
            { "FactionFacts", "InitialFactionFactSeeder" },
            { "DescriptionJournalChunk", "FactionIdentityChunker" },
            { "DescriptionJournalSummary", "FactionIdentitySummariser" },
            { "FactionTagline", "FactionTaglineUpdater" },
        };

        private void MigrateRenamedLabelKeys()
        {
            foreach (var kvp in RenamedLabelKeys)
            {
                if (!PerLabelReasoningEffort.TryGetValue(kvp.Key, out string oldValue)) continue;
                if (!PerLabelReasoningEffort.ContainsKey(kvp.Value))
                    PerLabelReasoningEffort[kvp.Value] = oldValue;
                PerLabelReasoningEffort.Remove(kvp.Key);
            }
        }
    }
}
