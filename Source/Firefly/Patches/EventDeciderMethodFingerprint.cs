using System;
using System.Reflection;
using System.Security.Cryptography;
using RimWorld;
using Verse;

namespace Firefly
{
    // Patch_EventDeciderIntercept doesn't just hook Storyteller.TryFire — it reproduces its
    // exact tail (CanFireNow/TryExecute, Notify_IncidentFired, lastIncidentTick) itself once a
    // held incident is ready to resume (see Patch_EventDeciderIntercept.Complete). If a future
    // RimWorld update changes TryFire's internals, Firefly wouldn't know; it would keep running
    // its now-outdated copy, which could misbehave in ways nobody notices until a save is already
    // affected. This checks the real installed TryFire method's IL against a hash captured from
    // the exact version this patch was built and tested against (1.6.4871 rev598) — on any
    // mismatch, the Event Decider disables itself and every incident falls through to plain
    // vanilla, rather than silently running a stale assumption. Same technique and same reasoning
    // as RaidMethodFingerprint — kept as a separate class since it checks a different method on a
    // different type and the two features can fail independently of each other.
    internal static class EventDeciderMethodFingerprint
    {
        // Captured via reflection against the real installed Assembly-CSharp.dll for RimWorld
        // 1.6.4871 rev598 — see RaidMethodFingerprint for how these are generated if they ever
        // need recapturing against a new version (same MD5-of-IL-bytes technique).
        private const string KnownGood_Storyteller_TryFire = "444A934927188F2AC9423F138A18E1C1";

        private static bool? _isSafe;

        public static bool IsSafe
        {
            get
            {
                if (_isSafe.HasValue) return _isSafe.Value;
                _isSafe = ComputeIsSafe();
                return _isSafe.Value;
            }
        }

        private static bool ComputeIsSafe()
        {
            try
            {
                bool ok = Check(typeof(Storyteller), "TryFire", KnownGood_Storyteller_TryFire);

                if (ok)
                    Log.Message("[Firefly:EventDecider] Method fingerprint check passed — Event Decider intercept enabled.");
                else
                    Log.Warning("[Firefly:EventDecider] Storyteller.TryFire fingerprint mismatch — this RimWorld version's " +
                        "storyteller code no longer matches what this feature was built against. Disabling the Event Decider " +
                        "intercept; every incident will fire as plain vanilla until Firefly is updated for this game version.");

                return ok;
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly:EventDecider] Fingerprint check itself failed ({e.Message}) — disabling Event Decider intercept to be safe.");
                return false;
            }
        }

        private static bool Check(Type type, string methodName, string knownGoodHash)
        {
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var method = type.GetMethod(methodName, flags);
            var body = method?.GetMethodBody();
            if (body == null) return false;

            var il = body.GetILAsByteArray();
            using var md5 = MD5.Create();
            string hash = BitConverter.ToString(md5.ComputeHash(il)).Replace("-", "");
            return hash == knownGoodHash;
        }
    }
}
