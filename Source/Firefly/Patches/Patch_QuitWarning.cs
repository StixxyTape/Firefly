using System;
using HarmonyLib;
using Verse;

namespace Firefly
{
    // Shared bypass + dialog logic for both quit-intercept patches below. Warns before letting
    // the player leave while an LLM response is still in flight — that response won't make it
    // into whatever's on disk if they leave now.
    //
    // Two targets, covering all four quit menu options:
    //   - Non-permadeath "Quit to Main Menu" -> GenScene.GoToMainMenu()
    //   - Non-permadeath "Quit to OS"         -> Root.Shutdown()
    //   - Permadeath "Save and quit to OS"    -> Root.Shutdown() (same as above)
    //   - Permadeath "Save and quit to Main Menu" -> LongEventHandler.QueueLongEvent(...) with
    //     levelToLoad "Entry". NOT GenScene.GoToMainMenu, and NOT MemoryUtility.ClearAllMapsAndWorld
    //     either — that was the previous (wrong) fix. Confirmed via decompile: this path queues a
    //     long-running job whose preLoadLevelAction happens to be ClearAllMapsAndWorld, but the
    //     scene transition to "Entry" is driven by QueueLongEvent's own levelToLoad field once the
    //     job finishes — it doesn't go through GoToMainMenu, and it doesn't care whether the
    //     preLoadLevelAction actually ran or was skipped by a Harmony prefix. Patching
    //     ClearAllMapsAndWorld could skip its own body but had zero effect on whether the player
    //     actually left, since QueueLongEvent proceeds to the scene load regardless. The only way
    //     to actually stop this path is to intercept before the job is queued at all.
    internal static class QuitWarningGate
    {
        // Set right before re-invoking the just-patched method after the player confirms — lets
        // that one call through without re-showing the dialog, then resets itself.
        private static bool _bypassOnce;

        public static bool TryProceed(Action original)
        {
            if (_bypassOnce) { _bypassOnce = false; return true; }
            if (!LLMClient.IsPending)
            {
                Log.Message("[Firefly:QuitWarning] Quit attempted, nothing pending — letting it through.");
                return true;
            }

            Log.Message("[Firefly:QuitWarning] Quit attempted while a response is pending — showing warning.");
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "An LLM response for Firefly is being generated. Leaving now could cause " +
                "precious story beats, progress, and consistencies to be missing from your story - " +
                "which, worst case scenario, can lead to the mod kind of imploding. Continue at your " +
                "own risk.",
                confirmedAct: () =>
                {
                    Log.Message("[Firefly:QuitWarning] Player confirmed — proceeding with quit.");
                    _bypassOnce = true;
                    original();
                },
                destructive: true,
                title: "Warning [Firefly]"));

            return false;
        }
    }

    [HarmonyPatch(typeof(GenScene), nameof(GenScene.GoToMainMenu))]
    public static class Patch_QuitWarning_GoToMainMenu
    {
        static bool Prefix() => QuitWarningGate.TryProceed(GenScene.GoToMainMenu);
    }

    [HarmonyPatch(typeof(Root), nameof(Root.Shutdown))]
    public static class Patch_QuitWarning_Shutdown
    {
        static bool Prefix() => QuitWarningGate.TryProceed(Root.Shutdown);
    }

    // The permadeath "Save and quit to Main Menu" path — see the class comment above for why this
    // has to be QueueLongEvent itself rather than anything queued inside it. Targets the specific
    // overload with a levelToLoad parameter (the other two QueueLongEvent overloads don't have
    // one) and only intervenes when levelToLoad is "Entry" — i.e. this specific job is headed back
    // to the main menu — so it doesn't fire for every other unrelated use of QueueLongEvent
    // throughout the game (loading a map, generating a world, etc).
    [HarmonyPatch(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent),
        new[] { typeof(Action), typeof(string), typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool) })]
    public static class Patch_QuitWarning_QueueLongEvent
    {
        static bool Prefix(Action preLoadLevelAction, string levelToLoad, string textKey,
            bool doAsynchronously, Action<Exception> exceptionHandler, bool showExtraUIInfo, bool forceHideUI)
        {
            if (levelToLoad != "Entry") return true;

            return QuitWarningGate.TryProceed(() => LongEventHandler.QueueLongEvent(
                preLoadLevelAction, levelToLoad, textKey, doAsynchronously, exceptionHandler,
                showExtraUIInfo, forceHideUI));
        }
    }
}
