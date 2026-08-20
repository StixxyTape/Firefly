using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Firefly
{
    public enum JournalCategory
    {
        Colony,
        Threads,
        World,
        Factions,
    }

    public static class JournalCategoryVisuals
    {
        public static readonly Color Colony = new Color(0.28f, 0.62f, 0.96f);
        public static readonly Color Threads = new Color(0.88f, 0.38f, 0.82f);
        public static readonly Color World = new Color(0.96f, 0.62f, 0.18f);
        public static readonly Color Factions = new Color(0.20f, 0.82f, 0.48f);

        public static Color Accent(JournalCategory category) => category switch
        {
            JournalCategory.Colony => Colony,
            JournalCategory.Threads => Threads,
            JournalCategory.World => World,
            _ => Factions,
        };

        public static string Name(JournalCategory category) => category switch
        {
            JournalCategory.Colony => "Colony",
            JournalCategory.Threads => "Threads",
            JournalCategory.World => "World",
            _ => "Factions",
        };

        public static bool IsPending(JournalCategory category) => category switch
        {
            JournalCategory.Colony => LLMClient.IsPendingForAny(JournalRecorder.ColonyPendingLabels),
            JournalCategory.Threads => LLMClient.IsPendingForAny(JournalRecorder.ThreadsPendingLabels),
            JournalCategory.World => LLMClient.IsPendingForAny(FireflyWorldComponent.WorldPendingLabels),
            _ => LLMClient.IsPendingForAny(FireflyWorldComponent.FactionPendingLabels),
        };

        public static List<JournalCategory> PendingCategories()
        {
            var categories = new List<JournalCategory>();
            foreach (JournalCategory category in System.Enum.GetValues(typeof(JournalCategory)))
                if (IsPending(category)) categories.Add(category);
            return categories;
        }

        public static Color Blend(IReadOnlyList<JournalCategory> categories)
        {
            if (categories.Count == 0) return Color.white;
            return new Color(
                categories.Average(c => Accent(c).r),
                categories.Average(c => Accent(c).g),
                categories.Average(c => Accent(c).b));
        }
    }
}
