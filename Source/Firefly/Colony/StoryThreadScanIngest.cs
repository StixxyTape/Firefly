using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;

namespace Firefly
{
    public static class StoryThreadScanIngest
    {
        public static string BuildThreadContextBlock(ColonyLedger ledger)
        {
            if (ledger.StoryThreads.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var thread in ledger.StoryThreads)
            {
                sb.AppendLine($"id: {thread.Id}");
                sb.AppendLine($"name: {thread.Name}");
                sb.AppendLine($"summary: {(thread.ActiveSummary.NullOrEmpty() ? "(none yet)" : thread.ActiveSummary)}");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static List<string> ApplyScanResult(ColonyLedger ledger, string rawJson, int day)
        {
            var ids = new HashSet<string>(ledger.StoryThreads.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
            ThreadScanResponse response = ThreadScanResponseParser.Parse(rawJson, "story thread scan", ids);
            if (response == null) return null;
            var touched = new List<string>();
            foreach (var created in response.NewThreads)
            {
                string id = UniqueSlug.Create(created.Name, "thread", ids);
                ids.Add(id);
                long now = GenTicks.TicksAbs;
                ledger.AddStoryThread(id, created.Name, created.Summary);
                foreach (string fact in created.Facts) ledger.AddFactToThread(id, now, day, fact);
                ledger.TouchStoryThread(id, now);
                touched.Add(id);
            }
            foreach (var update in response.Updates)
            {
                bool changed = false;
                long now = GenTicks.TicksAbs;
                foreach (string fact in update.Facts)
                {
                    ledger.AddFactToThread(update.Id, now, day, fact);
                    changed = true;
                }
                if (changed)
                {
                    ledger.TouchStoryThread(update.Id, now);
                    if (!touched.Contains(update.Id, StringComparer.OrdinalIgnoreCase)) touched.Add(update.Id);
                }
            }
            return touched;
        }
    }
}
