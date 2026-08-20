using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Firefly
{
    public static class UniqueSlug
    {
        private static readonly Regex NonSlugCharacters =
            new Regex("[^a-z0-9]+", RegexOptions.Compiled);

        public static string Create(string label, string fallback, ISet<string> existing)
        {
            string root = NonSlugCharacters.Replace((label ?? "").ToLowerInvariant(), "-").Trim('-');
            if (root.Length == 0) root = fallback;
            if (!existing.Contains(root)) return root;

            int suffix = 2;
            while (existing.Contains($"{root}-{suffix}")) suffix++;
            return $"{root}-{suffix}";
        }
    }
}
