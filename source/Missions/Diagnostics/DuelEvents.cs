#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Common;
using GameInterface;
using TaleWorlds.MountAndBlade;

namespace Missions.Diagnostics;

/// <summary>
/// A shared-clock event log for the PvP duel rig: every blow as the attacker's machine scored it, every routed
/// blow as the victim's owner applied it, and every scripted input step the duel driver took.
/// </summary>
/// <remarks>
/// <para>
/// The per-tick <see cref="AnimationTimeline"/> says what each machine DREW. It cannot say what the engine
/// DECIDED: whether the attacker's collision was a hit or a block, when the routed damage left, and when the
/// owner applied it. Those are single instants, not states, so they are logged as events with the same UTC
/// millisecond clock the timeline uses, and <c>analyze_duel.py</c> lines them up.
/// </para>
/// <para>
/// Diagnostic only, compiled out of a release build. Bounded so a runaway battle cannot grow it without limit,
/// and every recorder swallows its own exceptions: a measurement must never take the blow it measures down.
/// </para>
/// </remarks>
internal static class DuelEvents
{
    // 400 v 400 scores ~6,000 blows a side plus their routed and applied halves; 800 v 800 doubles it.
    private const int MaxLines = 60000;

    private static readonly object Gate = new object();
    private static readonly List<string> Lines = new List<string>(4096);
    private static bool enabled;
    private static int dropped;
    private static HashSet<string> allowedKinds;

    public static bool Enabled => enabled;

    public static void Start()
    {
        Start(null);
    }

    /// <summary>Arms the recorder; with <paramref name="kinds"/> only those event kinds are kept (null keeps all).</summary>
    public static void Start(IEnumerable<string> kinds)
    {
        lock (Gate)
        {
            Lines.Clear();
            dropped = 0;
            allowedKinds = kinds == null ? null : new HashSet<string>(kinds, StringComparer.Ordinal);
            enabled = true;
        }
    }

    private static long NowMs => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>Appends one event line: <c>&lt;utcMs&gt; &lt;kind&gt; &lt;detail&gt;</c>.</summary>
    public static void Record(string kind, string detail)
    {
        if (!enabled) return;
        lock (Gate)
        {
            if (allowedKinds != null && !allowedKinds.Contains(kind)) return;
            if (Lines.Count >= MaxLines) { dropped++; return; }
            Lines.Add(NowMs.ToString(CultureInfo.InvariantCulture) + " " + kind + " " + detail);
        }
    }

    /// <summary>
    /// The attacker's view of a blow, from the <c>Mission.RegisterBlow</c> postfix: who hit whom, whether the
    /// engine scored it as blocked, and how much it would inflict. For a puppet victim this is the blow that gets
    /// ROUTED; the owner's <see cref="RecordApplied"/> is its other half.
    /// </summary>
    public static void RecordBlow(Agent attacker, Agent victim, in Blow blow, in AttackCollisionData collision)
    {
        if (!enabled || attacker == null) return;
        try
        {
            Record("blow",
                "attacker=" + Id8(attacker) +
                " victim=" + Id8(victim) +
                " attackerLocal=" + Flag(IsLocal(attacker)) +
                " victimLocal=" + Flag(IsLocal(victim)) +
                " dmg=" + blow.InflictedDamage.ToString(CultureInfo.InvariantCulture) +
                " shieldBlock=" + Flag(collision.AttackBlockedWithShield) +
                " result=" + ((int)collision.CollisionResult).ToString(CultureInfo.InvariantCulture) +
                " missile=" + Flag(blow.IsMissile) +
                " attackType=" + ((int)blow.AttackType).ToString(CultureInfo.InvariantCulture) +
                " attackerDir=" + ((int)attacker.AttackDirection).ToString(CultureInfo.InvariantCulture) +
                " victimActionDir=" + (victim == null ? "-" : ((int)victim.GetCurrentActionDirection(1)).ToString(CultureInfo.InvariantCulture)) +
                " victimHealth=" + Health(victim));
        }
        catch (Exception)
        {
            // Never let a diagnostic take the blow down.
        }
    }

    /// <summary>
    /// A melee collision the engine resolved as blocked, parried or chambered. Those never reach Mission.RegisterBlow
    /// (run 7: a defender holding a shield block produced zero blow lines), so they are taken from the attacker's
    /// MeleeHitCallback and written as a <c>blow</c> line with <c>dmg=0</c>, which is how the analyser tells a block.
    /// </summary>
    public static void RecordBlocked(Agent attacker, Agent victim, in AttackCollisionData collision)
    {
        if (!enabled || attacker == null) return;
        try
        {
            Record("blow",
                "attacker=" + Id8(attacker) +
                " victim=" + Id8(victim) +
                " attackerLocal=" + Flag(IsLocal(attacker)) +
                " victimLocal=" + Flag(IsLocal(victim)) +
                " dmg=0" +
                " shieldBlock=" + Flag(collision.AttackBlockedWithShield) +
                " result=" + ((int)collision.CollisionResult).ToString(CultureInfo.InvariantCulture) +
                " missile=0 attackType=-1" +
                " attackerDir=" + ((int)attacker.AttackDirection).ToString(CultureInfo.InvariantCulture) +
                " victimActionDir=" + (victim == null ? "-" : ((int)victim.GetCurrentActionDirection(1)).ToString(CultureInfo.InvariantCulture)) +
                " victimHealth=" + Health(victim));
        }
        catch (Exception)
        {
            // Never let a diagnostic take the hit down.
        }
    }

    /// <summary>The victim owner's view: a routed blow reached the machine that owns the victim and is being applied.</summary>
    public static void RecordApplied(Guid victimId, Guid attackerId, int damage, bool shieldBlock, int collisionResult, float victimHealthBefore)
    {
        if (!enabled) return;
        try
        {
            Record("applied",
                "victim=" + Id8(victimId) +
                " attacker=" + Id8(attackerId) +
                " dmg=" + damage.ToString(CultureInfo.InvariantCulture) +
                " shieldBlock=" + Flag(shieldBlock) +
                " result=" + collisionResult.ToString(CultureInfo.InvariantCulture) +
                " victimHealthBefore=" + victimHealthBefore.ToString("0", CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
        }
    }

    public static string Snapshot(bool stop)
    {
        lock (Gate)
        {
            if (stop) enabled = false;
            var text = new StringBuilder();
            text.Append("DUEL_EVENTS n=").Append(Lines.Count.ToString(CultureInfo.InvariantCulture));
            text.Append(" dropped=").Append(dropped.ToString(CultureInfo.InvariantCulture));
            foreach (string line in Lines)
            {
                text.Append('\n').Append(line);
            }
            return text.ToString();
        }
    }

    /// <summary>First 8 hex characters of the agent's NETWORK id, the same key the timeline uses; "-" when unknown.</summary>
    public static string Id8(Agent agent)
    {
        if (agent == null) return "-";
        if (!ContainerProvider.TryResolve<INetworkAgentRegistry>(out var registry)) return "?";
        if (!registry.TryGetAgentInfo(agent, out CoopAgentInfo info)) return "?";
        return Id8(info.AgentId);
    }

    public static string Id8(Guid id) => id == Guid.Empty ? "-" : id.ToString("N").Substring(0, 8);

    private static bool IsLocal(Agent agent)
    {
        if (agent == null) return false;
        return ContainerProvider.TryResolve<INetworkAgentRegistry>(out var registry) && registry.IsLocallyControlled(agent);
    }

    private static string Health(Agent agent)
    {
        try { return agent == null ? "-" : agent.Health.ToString("0", CultureInfo.InvariantCulture); }
        catch (Exception) { return "?"; }
    }

    private static string Flag(bool value) => value ? "1" : "0";
}
#endif
