using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Firefly
{
    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.TryExecute))]
    public static class Patch_TradeDeal_TryExecute
    {
        [ThreadStatic]
        private static string _pendingLog;

        static void Prefix(TradeDeal __instance)
        {
            _pendingLog = null;
            try
            {
                var trader = TradeSession.trader;
                if (trader == null) return;

                var lost   = new List<string>();
                var gained = new List<string>();

                foreach (var t in __instance.AllTradeables)
                {
                    if (t.ActionToDo == TradeAction.None || t.CountToTransfer == 0) continue;

                    string label = t.CountToTransfer == 1
                        ? t.Label
                        : $"{t.CountToTransfer} {t.Label}";

                    if (t.ActionToDo == TradeAction.PlayerSells)
                        lost.Add(label);
                    else
                        gained.Add(label);
                }

                if (lost.Count == 0 && gained.Count == 0) return;

                var sb = new StringBuilder($"[Trade with {trader.TraderName}]");
                if (lost.Count   > 0) sb.Append($"\n  Lost:   {string.Join(", ", lost)}");
                if (gained.Count > 0) sb.Append($"\n  Gained: {string.Join(", ", gained)}");
                _pendingLog = sb.ToString();
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly] Trade capture failed: {e.Message}");
            }
        }

        static void Postfix(bool actuallyTraded)
        {
            if (actuallyTraded && !_pendingLog.NullOrEmpty())
                try { ColonyLedger.Current?.AppendRawToTimeline("\n" + _pendingLog); }
                catch { }
            _pendingLog = null;
        }
    }
}
