using HarmonyLib;
using Verse;

namespace Firefly
{
    public class FireflyMod : Mod
    {
        public static Harmony Harmony { get; private set; } = null!;

        public FireflyMod(ModContentPack content) : base(content)
        {
            Harmony = new Harmony("Stixxy.Firefly");
            Harmony.PatchAll();
            Log.Message("[Firefly] Initialized.");
        }
    }
}
