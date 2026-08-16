using System;
using System.Reflection;
using System.Security.Cryptography;
using RimWorld;
using Verse;

namespace Firefly
{
    // Patch_RaidNarrativeIntercept doesn't just call vanilla's raid methods — it replicates the
    // exact ORDER their internals run in (see TryRunPhaseOne, Complete). If a future RimWorld
    // update changes that internal structure, Firefly wouldn't know; it would keep running its
    // now-outdated copy, which could misbehave in ways nobody notices until a save is already
    // affected. This checks the real installed methods' IL against a hash captured from the
    // exact version this patch was built and tested against (1.6.4871 rev598) — on any mismatch,
    // the raid narrative feature disables itself and every raid falls through to plain vanilla,
    // rather than silently running a stale assumption.
    internal static class RaidMethodFingerprint
    {
        // Captured once via reflection against the real installed Assembly-CSharp.dll for
        // RimWorld 1.6.4871 rev598 — see project memory for how these were generated if they
        // ever need recapturing against a new version.
        private const string KnownGood_TryGenerateRaidInfo         = "3D8E50D10DCEAFA2C116EE4156AC6533";
        private const string KnownGood_Raid_TryExecuteWorker       = "CD3563AA592A0972BEE26A5894B5123F";
        private const string KnownGood_RaidEnemy_TryExecuteWorker  = "E5531787328081C756C84617F3D5D5FC";

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
                bool ok = true;
                ok &= Check(typeof(IncidentWorker_Raid), "TryGenerateRaidInfo", KnownGood_TryGenerateRaidInfo);
                ok &= Check(typeof(IncidentWorker_Raid), "TryExecuteWorker", KnownGood_Raid_TryExecuteWorker);
                ok &= Check(typeof(IncidentWorker_RaidEnemy), "TryExecuteWorker", KnownGood_RaidEnemy_TryExecuteWorker);

                if (ok)
                    Log.Message("[Firefly:RaidNarrative] Method fingerprint check passed — raid narrative intercept enabled.");
                else
                    Log.Warning("[Firefly:RaidNarrative] Raid method fingerprint mismatch — this RimWorld version's raid code no " +
                        "longer matches what this feature was built against. Disabling raid narrative rewriting; every raid will " +
                        "fire as plain vanilla until Firefly is updated for this game version.");

                return ok;
            }
            catch (Exception e)
            {
                Log.Warning($"[Firefly:RaidNarrative] Fingerprint check itself failed ({e.Message}) — disabling raid narrative intercept to be safe.");
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
