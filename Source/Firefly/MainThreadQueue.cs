using System;
using System.Collections.Generic;
using Verse;

namespace Firefly
{
    // LongEventHandler.ExecuteWhenFinished runs its action inline on the calling thread when no
    // long event is active, so an HTTP callback would touch game state from a background thread.
    // Callbacks land here instead and are drained on the main thread.
    public static class MainThreadQueue
    {
        private static readonly List<Action> Pending = new List<Action>();

        public static void Enqueue(Action action)
        {
            if (action == null) return;
            lock (Pending) Pending.Add(action);
        }

        public static void Drain()
        {
            Action[] batch;
            lock (Pending)
            {
                if (Pending.Count == 0) return;
                batch = Pending.ToArray();
                Pending.Clear();
            }

            foreach (var action in batch)
            {
                try { action(); }
                catch (Exception e) { Log.Warning($"[Firefly] Queued callback failed: {e.Message}"); }
            }
        }
    }
}
