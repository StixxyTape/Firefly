using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Firefly
{
    public class FireflyMod : Mod
    {
        public static FireflySettings Settings { get; private set; } = null!;
        public static Harmony Harmony { get; private set; } = null!;

        private bool showApiKey = false;
        private string testStatus = "";

        private static readonly List<(string Label, string Id)> PresetModels = new List<(string, string)>
        {
            ("Xiaomi MiMo v2.5",                   "xiaomi/mimo-v2.5"),
            ("Google Gemini 2.0 Flash",             "google/gemini-2.0-flash"),
            ("Google Gemini 2.5 Flash",             "google/gemini-2.5-flash"),
            ("Claude Haiku 4.5",                    "anthropic/claude-haiku-4-5"),
            ("GPT-4o Mini",                         "openai/gpt-4o-mini"),
            ("DeepSeek Chat",                       "deepseek/deepseek-chat"),
            ("Llama 3.1 8B (free)",                 "meta-llama/llama-3.1-8b-instruct:free"),
            ("Gemini 2.0 Flash Exp (free)",         "google/gemini-2.0-flash-exp:free"),
        };

        public FireflyMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<FireflySettings>();
            Harmony = new Harmony("Stixxy.Firefly");
            Harmony.PatchAll();
            Log.Message("[Firefly] Initialized.");
        }

        public override string SettingsCategory() => "Firefly";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("Connection");
            listing.GapLine();

            // API key row with show/hide toggle
            listing.Label("API Key");
            var keyRow = listing.GetRect(Text.LineHeight);
            var keyField = keyRow.LeftPartPixels(keyRow.width - 64f);
            var toggleBtn = new Rect(keyRow.xMax - 60f, keyRow.y, 60f, keyRow.height);

            if (showApiKey)
                Settings.ApiKey = Widgets.TextField(keyField, Settings.ApiKey);
            else
                Widgets.Label(keyField, Settings.ApiKey.NullOrEmpty()
                    ? "(not set)"
                    : new string('•', Mathf.Min(Settings.ApiKey.Length, 40)));

            if (Widgets.ButtonText(toggleBtn, showApiKey ? "Hide" : "Show"))
                showApiKey = !showApiKey;

            listing.Gap();

            // Base URL
            listing.Label("Base URL  (change to use OpenAI, DeepSeek, or a local Ollama/LM Studio endpoint)");
            Settings.BaseUrl = listing.TextEntry(Settings.BaseUrl);

            listing.Gap();

            // Model — text field + preset picker
            listing.Label("Model");
            var modelRow = listing.GetRect(Text.LineHeight);
            var modelField = modelRow.LeftPartPixels(modelRow.width - 80f);
            var modelPickBtn = new Rect(modelRow.xMax - 76f, modelRow.y, 76f, modelRow.height);

            Settings.Model = Widgets.TextField(modelField, Settings.Model);

            if (Widgets.ButtonText(modelPickBtn, "Presets ▼"))
            {
                var options = new List<FloatMenuOption>();
                foreach (var (label, id) in PresetModels)
                {
                    var modelId = id;
                    options.Add(new FloatMenuOption($"{label}  —  {modelId}", () => Settings.Model = modelId));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            listing.Gap(18f);

            // Test connection button + status
            var testRow = listing.GetRect(Text.LineHeight);
            var testBtnRect = testRow.LeftPartPixels(160f);
            var testStatusRect = new Rect(testRow.x + 168f, testRow.y, testRow.width - 168f, testRow.height);

            if (Widgets.ButtonText(testBtnRect, "Test Connection"))
            {
                testStatus = "Testing...";
                LLMClient.TestConnection((success, msg) =>
                {
                    testStatus = success ? $"Connected  ({msg})" : $"Failed: {msg}";
                });
            }

            if (!testStatus.NullOrEmpty())
                Widgets.Label(testStatusRect, testStatus);

            listing.Gap(18f);
            listing.Label("Tip: OpenRouter (openrouter.ai) gives you one key with access to many models. Browse at openrouter.ai/models.");

            listing.End();
        }
    }
}
