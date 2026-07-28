using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(DiaOption), "Activate")]
    public static class Patch_DiaOption_Activate
    {
        private static readonly HashSet<string> _blacklist = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "Jump to location",
            "Close",
        };

        static void Prefix(string ___text)
        {
            try
            {
                if (___text.NullOrEmpty()) return;
                if (_blacklist.Contains(___text.Trim())) return;

                string context = TryGetDialogContext();
                string formatted = context.NullOrEmpty()
                    ? $"[Response] \"{___text}\""
                    : $"[Response to '{context}'] \"{___text}\"";

                ColonyLedger.Current?.CaptureDecision(formatted);
            }
            catch { }
        }

        private static string TryGetDialogContext()
        {
            try
            {
                var dialog = Find.WindowStack?.Windows?.OfType<Dialog_NodeTree>().FirstOrDefault();
                if (dialog == null) return null;

                // Dialog_NodeTree stores its constructor 'title' arg as a field — letters pass their label here
                var titleField = Traverse.Create(dialog).Field("title");
                if (titleField.FieldExists())
                {
                    var title = titleField.GetValue()?.ToString();
                    if (!title.NullOrEmpty())
                        return ColonyLedger.StripTags(title);
                }

                // Fall back to the first sentence of the current node's text
                var nodeField = Traverse.Create(dialog).Field("curNode");
                if (nodeField.FieldExists())
                {
                    var node = nodeField.GetValue() as DiaNode;
                    string text = node?.text.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        int dot = text.IndexOf('.');
                        if (dot > 0 && dot < 80) text = text.Substring(0, dot + 1);
                        else if (text.Length > 80) text = text.Substring(0, 77) + "...";
                        return ColonyLedger.StripTags(text.Trim());
                    }
                }
            }
            catch { }
            return null;
        }

    }
}
