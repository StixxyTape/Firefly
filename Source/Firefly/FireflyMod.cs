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
        // Settings has grown past a fixed dialog's height, so content needs to scroll. The view
        // rect must be taller than what's actually drawn yet, which we don't know until after a
        // pass — start generous and refine from the real height each frame so the scrollbar's
        // range stays accurate instead of a guessed constant slowly drifting wrong as the page
        // grows further.
        private Vector2 settingsScrollPosition = Vector2.zero;
        private float settingsContentHeight = 900f;

        // Small built-in starting-point list — anything else the player wants, they type in and
        // save via the ★ button next to it, which persists to Settings.CustomModelPresets.
        private static readonly List<(string Label, string Id)> PresetModels = new List<(string, string)>
        {
            ("Google Gemini 2.5 Flash",             "google/gemini-2.5-flash"),
            ("Claude Haiku 4.5",                    "anthropic/claude-haiku-4-5"),
            ("DeepSeek Chat",                       "deepseek/deepseek-chat"),
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
            // Test Connection can be used with no game loaded, where GameComponentTick never runs,
            // so the queue is also drained here while this window is open.
            MainThreadQueue.Drain();

            const float pagePadding = 12f;
            const float cardPadding = 18f;
            const float cardGap = 14f;

            // Scrollbar width worth of slack so the vertical scrollbar doesn't overlap the last
            // few pixels of content.
            var viewRect = new Rect(0f, 0f, inRect.width - 20f, settingsContentHeight);
            Widgets.BeginScrollView(inRect, ref settingsScrollPosition, viewRect);

            float y = pagePadding;
            float contentWidth = viewRect.width - pagePadding * 2f;

            // Page identity: a compact banner gives the settings a clear top-level hierarchy
            // without spending much of the fixed mod-settings window's vertical space.
            var banner = new Rect(pagePadding, y, contentWidth, 68f);
            DrawPanel(banner, new Color(0.11f, 0.16f, 0.20f, 0.96f), new Color(0.35f, 0.67f, 0.78f, 0.55f));
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(banner.x + 18f, banner.y + 10f, banner.width - 36f, 30f), "Firefly");
            Text.Font = GameFont.Small;
            GUI.color = new Color(0.76f, 0.84f, 0.88f);
            Widgets.Label(new Rect(banner.x + 19f, banner.y + 39f, banner.width - 38f, 22f),
                "Choose how Fillion connects, writes, and reasons about your colony.");
            GUI.color = Color.white;
            y = banner.yMax + cardGap;

            // ── Connection ──────────────────────────────────────────────────────
            var connectionCard = new Rect(pagePadding, y, contentWidth, 320f);
            DrawPanel(connectionCard, new Color(0.095f, 0.105f, 0.115f, 0.94f), new Color(1f, 1f, 1f, 0.14f));
            float cx = connectionCard.x + cardPadding;
            float cw = connectionCard.width - cardPadding * 2f;
            float cy = connectionCard.y + cardPadding;
            DrawSectionHeading(ref cy, cx, cw, "Connection",
                "Your provider, credentials, and the model Fillion uses.");

            // API key row with show/hide toggle.
            DrawFieldLabel(ref cy, cx, cw, "API key", "Stored with RimWorld's mod settings on this computer.");
            var keyRow = new Rect(cx, cy, cw, 30f);
            var keyField = new Rect(keyRow.x, keyRow.y, keyRow.width - 72f, keyRow.height);
            var toggleBtn = new Rect(keyRow.xMax - 66f, keyRow.y, 66f, keyRow.height);

            if (showApiKey)
                Settings.ApiKey = Widgets.TextField(keyField, Settings.ApiKey);
            else
            {
                Widgets.DrawBoxSolid(keyField, new Color(0.055f, 0.06f, 0.065f, 0.9f));
                GUI.color = Settings.ApiKey.NullOrEmpty() ? new Color(1f, 1f, 1f, 0.45f) : new Color(1f, 1f, 1f, 0.78f);
                Widgets.Label(keyField, Settings.ApiKey.NullOrEmpty()
                    ? "(not set)"
                    : new string('•', Mathf.Min(Settings.ApiKey.Length, 40)));
                GUI.color = Color.white;
            }

            if (Widgets.ButtonText(toggleBtn, showApiKey ? "Hide" : "Show"))
                showApiKey = !showApiKey;
            cy = keyRow.yMax + 12f;

            DrawFieldLabel(ref cy, cx, cw, "Base URL", "OpenRouter, OpenAI, DeepSeek, Ollama, or LM Studio endpoint.");
            var urlRect = new Rect(cx, cy, cw, 30f);
            Settings.BaseUrl = Widgets.TextField(urlRect, Settings.BaseUrl);
            cy = urlRect.yMax + 12f;

            // Model — text field + save-preset star + preset picker.
            DrawFieldLabel(ref cy, cx, cw, "Model", "Type an ID directly, save it with the star, or choose a preset.");
            var modelRow = new Rect(cx, cy, cw, 30f);
            var modelField  = modelRow.LeftPartPixels(modelRow.width - 124f);
            var saveBtnRect = new Rect(modelRow.xMax - 108f, modelRow.y, 28f, modelRow.height);
            var modelPickBtn = new Rect(modelRow.xMax - 76f, modelRow.y, 76f, modelRow.height);

            Settings.Model = Widgets.TextField(modelField, Settings.Model);

            bool currentIsSaved = Settings.CustomModelPresets.Exists(p => p.ModelId == Settings.Model);
            TooltipHandler.TipRegion(saveBtnRect, currentIsSaved
                ? "Remove this model from your saved presets"
                : "Save the current model as a preset");
            if (Widgets.ButtonText(saveBtnRect, currentIsSaved ? "★" : "☆"))
            {
                if (currentIsSaved)
                {
                    Settings.CustomModelPresets.RemoveAll(p => p.ModelId == Settings.Model);
                }
                else if (!Settings.Model.NullOrEmpty())
                {
                    Settings.CustomModelPresets.Add(new ModelPreset(Settings.Model, Settings.Model));
                }
            }

            if (Widgets.ButtonText(modelPickBtn, "Presets ▼"))
            {
                var options = new List<FloatMenuOption>();
                foreach (var (label, id) in PresetModels)
                {
                    var modelId = id;
                    options.Add(new FloatMenuOption($"{label}  —  {modelId}", () => Settings.Model = modelId));
                }
                foreach (var preset in Settings.CustomModelPresets)
                {
                    var modelId = preset.ModelId;
                    options.Add(new FloatMenuOption($"★ {modelId}", () => Settings.Model = modelId));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }

            cy = modelRow.yMax + 16f;

            // Test connection button + status.
            var testRow = new Rect(cx, cy, cw, 30f);
            var testBtnRect = testRow.LeftPartPixels(156f);
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
            {
                Widgets.DrawBoxSolid(testStatusRect, testStatus.StartsWith("Connected")
                    ? new Color(0.16f, 0.34f, 0.22f, 0.72f)
                    : testStatus == "Testing..."
                        ? new Color(0.20f, 0.27f, 0.34f, 0.72f)
                        : new Color(0.38f, 0.17f, 0.17f, 0.72f));
                var oldAnchor = Text.Anchor;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(testStatusRect, testStatus);
                Text.Anchor = oldAnchor;
            }

            y = connectionCard.yMax + 10f;

            var tipRect = new Rect(pagePadding, y, contentWidth, 48f);
            DrawPanel(tipRect, new Color(0.16f, 0.145f, 0.085f, 0.78f), new Color(0.72f, 0.58f, 0.24f, 0.48f));
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.94f, 0.86f, 0.62f);
            Widgets.Label(new Rect(tipRect.x + 15f, tipRect.y + 8f, tipRect.width - 30f, tipRect.height - 12f),
                "TIP  OpenRouter gives one key access to many models. Browse the catalogue at openrouter.ai/models.");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y = tipRect.yMax + cardGap;

            // ── Generation ──────────────────────────────────────────────────────
            var generationCard = new Rect(pagePadding, y, contentWidth, 340f);
            DrawPanel(generationCard, new Color(0.095f, 0.105f, 0.115f, 0.94f), new Color(1f, 1f, 1f, 0.14f));
            float gx = generationCard.x + cardPadding;
            float gw = generationCard.width - cardPadding * 2f;
            float gy = generationCard.y + cardPadding;
            DrawSectionHeading(ref gy, gx, gw, "Generation",
                "Balance context, response length, and thinking time.");

            DrawSettingLabel(ref gy, gx, gw, "Prompt size limit", $"{Settings.MaxPromptChars:N0} characters",
                "Busy logs are trimmed to this size before sending. Lower it to reduce cost or avoid provider limits.");
            var promptSlider = new Rect(gx, gy, gw, 24f);
            Settings.MaxPromptChars = Mathf.RoundToInt(
                Widgets.HorizontalSlider(promptSlider, Settings.MaxPromptChars, 4000f, 120000f) / 1000f) * 1000;
            gy = promptSlider.yMax + 18f;

            DrawSettingLabel(ref gy, gx, gw, "Completion size limit", $"{Settings.MaxCompletionTokens:N0} tokens",
                "Caps each reply, including summaries, thread scans, and raid narratives.");
            var completionSlider = new Rect(gx, gy, gw, 24f);
            Settings.MaxCompletionTokens = Mathf.RoundToInt(
                Widgets.HorizontalSlider(completionSlider, Settings.MaxCompletionTokens, 256f, 8192f) / 64f) * 64;
            gy = completionSlider.yMax + 18f;

            DrawSettingLabel(ref gy, gx, gw, "Reasoning effort", ReasoningDisplayName(Settings.ReasoningEffort),
                "Controls deliberation for supported reasoning models; other models ignore it.");
            var reasoningRow = new Rect(gx, gy, gw, 30f);
            var reasoningOptions = new (string Label, string Value)[]
            {
                ("Model default", ""),
                ("None",          "none"),
                ("Minimal",       "minimal"),
                ("Low",           "low"),
                ("Medium",        "medium"),
                ("High",          "high"),
            };
            float reasoningBtnWidth = reasoningRow.width / reasoningOptions.Length;
            for (int i = 0; i < reasoningOptions.Length; i++)
            {
                var (optLabel, optValue) = reasoningOptions[i];
                var btnRect = new Rect(reasoningRow.x + i * reasoningBtnWidth, reasoningRow.y, reasoningBtnWidth - 4f, reasoningRow.height);
                bool selected = Settings.ReasoningEffort == optValue;
                GUI.color = selected ? Color.white : new Color(1f, 1f, 1f, 0.6f);
                if (Widgets.ButtonText(btnRect, selected ? $"[{optLabel}]" : optLabel))
                    Settings.ReasoningEffort = optValue;
                GUI.color = Color.white;
            }

            y = generationCard.yMax + pagePadding;
            settingsContentHeight = y;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Widgets.EndScrollView();
        }

        private static void DrawPanel(Rect rect, Color fill, Color border)
        {
            Widgets.DrawBoxSolid(rect, fill);
            Color old = GUI.color;
            GUI.color = border;
            Widgets.DrawBox(rect, 1);
            GUI.color = old;
        }

        private static void DrawSectionHeading(ref float y, float x, float width, string title, string description)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(x, y, width, 28f), title);
            y += 29f;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.56f);
            Widgets.Label(new Rect(x, y, width, 20f), description);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 29f;
        }

        private static void DrawFieldLabel(ref float y, float x, float width, string label, string hint)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(x, y, width * 0.35f, 24f), label);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.48f);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(x + width * 0.32f, y, width * 0.68f, 22f), hint);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 24f;
        }

        private static void DrawSettingLabel(ref float y, float x, float width, string label, string value, string hint)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(x, y, width * 0.7f, 24f), label);
            GUI.color = new Color(0.50f, 0.78f, 0.88f);
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(new Rect(x + width * 0.55f, y, width * 0.45f, 24f), value);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            y += 24f;
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 1f, 1f, 0.52f);
            Widgets.Label(new Rect(x, y, width, 22f), hint);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 22f;
        }

        private static string ReasoningDisplayName(string value)
        {
            if (value.NullOrEmpty()) return "Model default";
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }
    }
}
