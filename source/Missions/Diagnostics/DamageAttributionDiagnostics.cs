using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Missions.Diagnostics;

/// <summary>
/// Separates "the same blow is applied twice" from "one side simply swings more often".
/// </summary>
/// <remarks>
/// One player's troops were seen killing at roughly twice the rate of the other's. Two explanations fit,
/// and they need different fixes:
///
///   double application - a hit is applied locally AND again when the routed copy arrives. Shows up as
///                        suppressed=0 while routed applications are high: nothing was held back, so every
///                        hit landed on both paths.
///   higher swing rate  - remote-owned attackers land more blows per attacker than locally owned ones,
///                        because their swings are being restarted. Shows up in blowsPerAttacker.
///
/// Also proves the suppression is installed at all: RegisterBlowPatch sat dormant for a long time because
/// it carried no patch category, and nothing calls PatchAllUncategorized on this assembly. If SUPPRESSED
/// stays 0 while remote attackers are landing blows, it is still not running.
/// </remarks>
internal static class DamageAttributionDiagnostics
{
    private static readonly object Gate = new();

    private static bool enabled;

    private static long registerBlowCalls;
    private static long routedReapplications;
    private static long appliedBecauseLocal;
    private static long suppressedBecauseRemote;

    private static long blowsByLocalAttacker;
    private static long blowsByRemoteAttacker;
    private static readonly HashSet<int> LocalAttackers = new();

    // Distance between attacker and victim when the blow registered, bucketed. Melee reach is about 2m;
    // anything past ~4m did not connect where this machine thinks the two are standing.
    private static readonly long[] DistLocalAttacker = new long[5];
    private static readonly long[] DistRemoteAttacker = new long[5];
    private static long farBlowOnLocalVictim;
    private static double distSumLocal;
    private static double distSumRemote;
    private static long impactLocalN;
    private static long impactRemoteN;
    private static long impactRemoteFar;
    private static double impactSumLocal;
    private static double impactSumRemote;
    private static readonly long[] ApplyDelayBuckets = new long[6];
    private static double applyDelaySum;
    private static long applyDelayN;
    private static double applyDelayMax;
    private static long missileLocal;
    private static long missileRemote;

    // Damage split by whether the VICTIM is simulated here. Same attacker population, so a gap between
    // the two columns is about the victim, not about who is swinging.
    private sealed class DamageBucket
    {
        public long Hits;
        public double Damage;
        public double BaseMagnitude;
        public double SpeedModifier;
        public double VictimSpeed;
        public long ZeroSpeedModifier;
    }

    private static readonly DamageBucket LocalVictimFoot = new();
    private static readonly DamageBucket LocalVictimMounted = new();
    private static readonly DamageBucket PuppetVictimFoot = new();
    private static readonly DamageBucket PuppetVictimMounted = new();

    // The player's own blows. Small counts by nature, so they are listed rather than averaged away.
    private sealed class PlayerBlow
    {
        public int Damage;
        public float BaseMagnitude;
        public float SpeedModifier;
        public float VictimSpeed;
        public float AttackerSpeed;
        public bool VictimIsPuppet;
        public bool VictimMounted;
        public bool Missile;
    }

    private static readonly List<PlayerBlow> PlayerBlows = new();
    private static readonly HashSet<int> RemoteAttackers = new();

    public static bool Enabled => enabled;

    public static void Start()
    {
        lock (Gate)
        {
            registerBlowCalls = 0;
            routedReapplications = 0;
            appliedBecauseLocal = 0;
            suppressedBecauseRemote = 0;
            blowsByLocalAttacker = 0;
            blowsByRemoteAttacker = 0;
            LocalAttackers.Clear();
            RemoteAttackers.Clear();
            for (int i = 0; i < 5; i++) { DistLocalAttacker[i] = 0; DistRemoteAttacker[i] = 0; }
            farBlowOnLocalVictim = 0;
            distSumLocal = 0;
            distSumRemote = 0;
            impactLocalN = 0;
            impactRemoteN = 0;
            impactRemoteFar = 0;
            impactSumLocal = 0;
            impactSumRemote = 0;
            for (int i = 0; i < 6; i++) ApplyDelayBuckets[i] = 0;
            applyDelaySum = 0;
            applyDelayN = 0;
            applyDelayMax = 0;
            missileLocal = 0;
            missileRemote = 0;
            PlayerBlows.Clear();
            foreach (DamageBucket bucket in new[]
                     { LocalVictimFoot, LocalVictimMounted, PuppetVictimFoot, PuppetVictimMounted })
            {
                bucket.Hits = 0;
                bucket.Damage = 0;
                bucket.BaseMagnitude = 0;
                bucket.SpeedModifier = 0;
                bucket.VictimSpeed = 0;
                bucket.ZeroSpeedModifier = 0;
            }
            enabled = true;
        }
    }

    /// <summary>From RegisterBlowPatch: what the suppression decided for one Agent.RegisterBlow call.</summary>
    public static void RecordRegisterBlow(bool onAllowedThread, bool victimIsLocallyControlled)
    {
        if (!enabled) return;
        lock (Gate)
        {
            registerBlowCalls++;
            if (onAllowedThread) routedReapplications++;
            else if (victimIsLocallyControlled) appliedBecauseLocal++;
            else suppressedBecauseRemote++;
        }
    }

    /// <summary>From the counting patch on Mission.RegisterBlow: who is landing blows, and how many.</summary>
    /// <summary>Bucket: 0 = &lt;2m, 1 = &lt;4m, 2 = &lt;6m, 3 = &lt;10m, 4 = 10m+.</summary>
    private static int Bucket(float d) =>
        d < 2f ? 0 : d < 4f ? 1 : d < 6f ? 2 : d < 10f ? 3 : 4;

    /// <summary>Buckets: &lt;50ms, &lt;100, &lt;250, &lt;500, &lt;1000, 1s+.</summary>
    public static void RecordPlayerBlow(
        int inflicted,
        float baseMagnitude,
        float speedModifier,
        bool victimIsLocal,
        bool victimIsMounted,
        float victimSpeed,
        float attackerSpeed,
        bool isMissile)
    {
        if (!enabled) return;
        lock (Gate)
        {
            if (PlayerBlows.Count >= 400) return;
            PlayerBlows.Add(new PlayerBlow
            {
                Damage = inflicted,
                BaseMagnitude = baseMagnitude,
                SpeedModifier = speedModifier,
                VictimSpeed = victimSpeed,
                AttackerSpeed = attackerSpeed,
                VictimIsPuppet = !victimIsLocal,
                VictimMounted = victimIsMounted,
                Missile = isMissile,
            });
        }
    }

    public static void RecordBlowDamage(
        int inflicted,
        float baseMagnitude,
        float speedModifier,
        bool victimIsLocal,
        bool victimIsMounted,
        float victimSpeed,
        bool isMissile)
    {
        if (!enabled || isMissile) return;
        if (float.IsNaN(baseMagnitude) || float.IsNaN(speedModifier)) return;

        DamageBucket bucket = victimIsLocal
            ? (victimIsMounted ? LocalVictimMounted : LocalVictimFoot)
            : (victimIsMounted ? PuppetVictimMounted : PuppetVictimFoot);

        lock (Gate)
        {
            bucket.Hits++;
            bucket.Damage += inflicted;
            bucket.BaseMagnitude += baseMagnitude;
            bucket.SpeedModifier += speedModifier;
            if (victimSpeed >= 0f) bucket.VictimSpeed += victimSpeed;
            if (speedModifier <= 0.001f) bucket.ZeroSpeedModifier++;
        }
    }

    private static void AppendBucket(StringBuilder text, string name, DamageBucket bucket)
    {
        text.Append(' ').Append(name).Append('=');
        if (bucket.Hits == 0) { text.Append("none"); return; }
        text.Append("n:").Append(bucket.Hits.ToString(CultureInfo.InvariantCulture));
        text.Append(" dmg:").Append((bucket.Damage / bucket.Hits).ToString("F1", CultureInfo.InvariantCulture));
        text.Append(" base:").Append((bucket.BaseMagnitude / bucket.Hits).ToString("F1", CultureInfo.InvariantCulture));
        text.Append(" SPEEDMOD:").Append((bucket.SpeedModifier / bucket.Hits).ToString("F3", CultureInfo.InvariantCulture));
        text.Append(" victimSpeed:").Append((bucket.VictimSpeed / bucket.Hits).ToString("F2", CultureInfo.InvariantCulture));
        text.Append(" zeroSpeedMod:").Append(bucket.ZeroSpeedModifier.ToString(CultureInfo.InvariantCulture));
    }

    public static void RecordApplyDelay(double seconds)
    {
        if (!enabled || double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0d) return;
        int b = seconds < 0.05d ? 0 : seconds < 0.10d ? 1 : seconds < 0.25d ? 2
              : seconds < 0.50d ? 3 : seconds < 1.0d ? 4 : 5;
        lock (Gate)
        {
            ApplyDelayBuckets[b]++;
            applyDelaySum += seconds;
            applyDelayN++;
            if (seconds > applyDelayMax) applyDelayMax = seconds;
        }
    }

    public static void RecordBlowDistance(
        float distance,
        float impactToVictim,
        bool attackerIsLocal,
        bool victimIsLocal,
        bool isMissile)
    {
        if (!enabled || float.IsNaN(distance) || float.IsInfinity(distance)) return;
        lock (Gate)
        {
            if (isMissile)
            {
                if (attackerIsLocal) missileLocal++; else missileRemote++;
                return;
            }
            if (!float.IsNaN(impactToVictim))
            {
                if (attackerIsLocal) { impactLocalN++; impactSumLocal += impactToVictim; }
                else
                {
                    impactRemoteN++;
                    impactSumRemote += impactToVictim;
                    if (impactToVictim >= 4f) impactRemoteFar++;
                }
            }
            if (attackerIsLocal) { DistLocalAttacker[Bucket(distance)]++; distSumLocal += distance; }
            else { DistRemoteAttacker[Bucket(distance)]++; distSumRemote += distance; }
            if (victimIsLocal && distance >= 4f) farBlowOnLocalVictim++;
        }
    }

    public static void RecordAttacker(int attackerIndex, bool attackerIsLocallyControlled)
    {
        if (!enabled) return;
        lock (Gate)
        {
            if (attackerIsLocallyControlled)
            {
                blowsByLocalAttacker++;
                if (attackerIndex >= 0) LocalAttackers.Add(attackerIndex);
            }
            else
            {
                blowsByRemoteAttacker++;
                if (attackerIndex >= 0) RemoteAttackers.Add(attackerIndex);
            }
        }
    }

    public static string Snapshot(bool stop)
    {
        var text = new StringBuilder();
        lock (Gate)
        {
            text.Append("damageAttribution: registerBlow=")
                .Append(registerBlowCalls.ToString(CultureInfo.InvariantCulture));
            text.Append(" routedReapply=").Append(routedReapplications.ToString(CultureInfo.InvariantCulture));
            text.Append(" appliedLocal=").Append(appliedBecauseLocal.ToString(CultureInfo.InvariantCulture));
            text.Append(" SUPPRESSED=").Append(suppressedBecauseRemote.ToString(CultureInfo.InvariantCulture));

            double localPer = LocalAttackers.Count == 0
                ? 0 : (double)blowsByLocalAttacker / LocalAttackers.Count;
            double remotePer = RemoteAttackers.Count == 0
                ? 0 : (double)blowsByRemoteAttacker / RemoteAttackers.Count;

            text.Append(" | blows local=").Append(blowsByLocalAttacker.ToString(CultureInfo.InvariantCulture));
            text.Append(" over ").Append(LocalAttackers.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" attackers");
            text.Append(" remote=").Append(blowsByRemoteAttacker.ToString(CultureInfo.InvariantCulture));
            text.Append(" over ").Append(RemoteAttackers.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" attackers");
            text.Append(" | BLOWS_PER_ATTACKER local=").Append(localPer.ToString("F2", CultureInfo.InvariantCulture));
            text.Append(" remote=").Append(remotePer.ToString("F2", CultureInfo.InvariantCulture));
            if (localPer > 0)
                text.Append(" ratio=").Append((remotePer / localPer).ToString("F2", CultureInfo.InvariantCulture));

            long nL = 0, nR = 0;
            for (int i = 0; i < 5; i++) { nL += DistLocalAttacker[i]; nR += DistRemoteAttacker[i]; }
            text.Append(" || BLOW_DISTANCE localAttacker=[<2m:").Append(DistLocalAttacker[0]);
            text.Append(" <4m:").Append(DistLocalAttacker[1]).Append(" <6m:").Append(DistLocalAttacker[2]);
            text.Append(" <10m:").Append(DistLocalAttacker[3]).Append(" 10m+:").Append(DistLocalAttacker[4]).Append(']');
            if (nL > 0) text.Append(" mean=").Append((distSumLocal / nL).ToString("F2", CultureInfo.InvariantCulture));
            text.Append(" remoteAttacker=[<2m:").Append(DistRemoteAttacker[0]);
            text.Append(" <4m:").Append(DistRemoteAttacker[1]).Append(" <6m:").Append(DistRemoteAttacker[2]);
            text.Append(" <10m:").Append(DistRemoteAttacker[3]).Append(" 10m+:").Append(DistRemoteAttacker[4]).Append(']');
            if (nR > 0) text.Append(" mean=").Append((distSumRemote / nR).ToString("F2", CultureInfo.InvariantCulture));
            text.Append(" FAR_BLOW_ON_LOCAL_VICTIM=").Append(farBlowOnLocalVictim.ToString(CultureInfo.InvariantCulture));
            text.Append(" || IMPACT_POINT_TO_VICTIM local=");
            text.Append(impactLocalN > 0 ? (impactSumLocal / impactLocalN).ToString("F2", CultureInfo.InvariantCulture) : "-");
            text.Append(" remote=");
            text.Append(impactRemoteN > 0 ? (impactSumRemote / impactRemoteN).ToString("F2", CultureInfo.InvariantCulture) : "-");
            text.Append(" remoteFar4m=").Append(impactRemoteFar.ToString(CultureInfo.InvariantCulture));
            text.Append("/").Append(impactRemoteN.ToString(CultureInfo.InvariantCulture));
            text.Append(" || PLAYER_BLOWS n=").Append(PlayerBlows.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < PlayerBlows.Count && i < 40; i++)
            {
                PlayerBlow blow = PlayerBlows[i];
                text.Append(" [").Append(blow.VictimIsPuppet ? "puppet" : "local");
                text.Append(blow.VictimMounted ? "/mounted" : "/foot");
                if (blow.Missile) text.Append("/missile");
                text.Append(" dmg:").Append(blow.Damage.ToString(CultureInfo.InvariantCulture));
                text.Append(" base:").Append(blow.BaseMagnitude.ToString("F1", CultureInfo.InvariantCulture));
                text.Append(" spd:").Append(blow.SpeedModifier.ToString("F2", CultureInfo.InvariantCulture));
                text.Append(" vSpd:").Append(blow.VictimSpeed.ToString("F1", CultureInfo.InvariantCulture));
                text.Append(" aSpd:").Append(blow.AttackerSpeed.ToString("F1", CultureInfo.InvariantCulture));
                text.Append(']');
            }
            text.Append(" || DAMAGE_BY_VICTIM");
            AppendBucket(text, "localFoot", LocalVictimFoot);
            AppendBucket(text, "localMounted", LocalVictimMounted);
            AppendBucket(text, "puppetFoot", PuppetVictimFoot);
            AppendBucket(text, "puppetMounted", PuppetVictimMounted);
            text.Append(" || MISSILES_EXCLUDED local=").Append(missileLocal.ToString(CultureInfo.InvariantCulture));
            text.Append(" remote=").Append(missileRemote.ToString(CultureInfo.InvariantCulture));
            text.Append(" || APPLY_DELAY n=").Append(applyDelayN.ToString(CultureInfo.InvariantCulture));
            if (applyDelayN > 0)
            {
                text.Append(" mean=").Append((1000.0 * applyDelaySum / applyDelayN).ToString("F0", CultureInfo.InvariantCulture)).Append("ms");
                text.Append(" max=").Append((1000.0 * applyDelayMax).ToString("F0", CultureInfo.InvariantCulture)).Append("ms");
            }
            text.Append(" [<50ms:").Append(ApplyDelayBuckets[0]).Append(" <100:").Append(ApplyDelayBuckets[1]);
            text.Append(" <250:").Append(ApplyDelayBuckets[2]).Append(" <500:").Append(ApplyDelayBuckets[3]);
            text.Append(" <1s:").Append(ApplyDelayBuckets[4]).Append(" 1s+:").Append(ApplyDelayBuckets[5]).Append(']');
            if (stop) enabled = false;
        }
        return text.ToString();
    }
}
