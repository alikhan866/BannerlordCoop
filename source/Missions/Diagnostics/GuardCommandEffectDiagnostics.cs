using System.Globalization;
using System.Text;
using System.Threading;
using TaleWorlds.MountAndBlade;

namespace Missions.Diagnostics;

/// <summary>
/// Measures whether a guard command issued to a remote agent actually changes that agent's guard state.
/// </summary>
/// <remarks>
/// UpdateRemoteGuardState re-commands the guard whenever nativeGuardStateMissing holds, and that condition
/// is `agent.CurrentGuardMode != guardMode`. If the command never makes CurrentGuardMode equal guardMode,
/// the condition stays true and the command is re-issued every tick, for the life of the agent - measured
/// live at 590/sec across ~32 agents, 85-88% of all guard re-commands. Each one applies a defend pose, which
/// is what a puppet is showing instead of the wind-up it was sent: 62% defend against 5% wind-up in the
/// owner/puppet timeline comparison.
///
/// This answers the one question three failed fixes were all guessing at: does the order take effect?
///   reached  - CurrentGuardMode equals the commanded mode after the call (the command works; something
///              else reverts it, and the hunt moves to what)
///   noChange - CurrentGuardMode is identical before and after (the order is inert and the loop is the bug)
/// </remarks>
internal static class GuardCommandEffectDiagnostics
{
    private static bool enabled;

    private static long commands;
    private static long reached;
    private static long noChange;
    private static long changedButWrong;
    private static long viaDirectionTransition;
    private static long viaGuardState;
    private static long fromNativeMissing;
    private static long readFailed;

    // What the guard command lands ON. If it fires while the agent is mid-swing, it overwrites the swing:
    // this counts how many wind-ups and releases the guard destroys, which is the thing three earlier
    // fixes all assumed without measuring.
    private static long onWindup;
    private static long onRelease;
    private static long onDefending;
    private static long onNothing;
    private static long onOther;
    private static long DESTROYED_WINDUP;
    private static long DESTROYED_RELEASE;

    public static bool Enabled => enabled;

    public static void Start()
    {
        Interlocked.Exchange(ref commands, 0);
        Interlocked.Exchange(ref reached, 0);
        Interlocked.Exchange(ref noChange, 0);
        Interlocked.Exchange(ref changedButWrong, 0);
        Interlocked.Exchange(ref viaDirectionTransition, 0);
        Interlocked.Exchange(ref viaGuardState, 0);
        Interlocked.Exchange(ref fromNativeMissing, 0);
        Interlocked.Exchange(ref readFailed, 0);
        Interlocked.Exchange(ref onWindup, 0);
        Interlocked.Exchange(ref onRelease, 0);
        Interlocked.Exchange(ref onDefending, 0);
        Interlocked.Exchange(ref onNothing, 0);
        Interlocked.Exchange(ref onOther, 0);
        Interlocked.Exchange(ref DESTROYED_WINDUP, 0);
        Interlocked.Exchange(ref DESTROYED_RELEASE, 0);
        enabled = true;
    }

    public static void Record(
        Agent.GuardMode before,
        Agent.GuardMode after,
        Agent.GuardMode commanded,
        bool nativeGuardStateMissing,
        bool usedDirectionTransition,
        Agent.ActionCodeType actionBefore,
        Agent.ActionCodeType actionAfter)
    {
        if (!enabled) return;

        bool wasWindup = actionBefore == Agent.ActionCodeType.ReadyMelee;
        bool wasRelease = actionBefore == Agent.ActionCodeType.ReleaseMelee;
        if (wasWindup)
        {
            Interlocked.Increment(ref onWindup);
            if (actionAfter != actionBefore) Interlocked.Increment(ref DESTROYED_WINDUP);
        }
        else if (wasRelease)
        {
            Interlocked.Increment(ref onRelease);
            if (actionAfter != actionBefore) Interlocked.Increment(ref DESTROYED_RELEASE);
        }
        else if (actionBefore >= Agent.ActionCodeType.DefendFist
                 && actionBefore <= Agent.ActionCodeType.DefendLeftStaff)
        {
            Interlocked.Increment(ref onDefending);
        }
        else if (actionBefore == Agent.ActionCodeType.Other)
        {
            Interlocked.Increment(ref onNothing);
        }
        else Interlocked.Increment(ref onOther);
        Interlocked.Increment(ref commands);
        if (nativeGuardStateMissing) Interlocked.Increment(ref fromNativeMissing);
        if (usedDirectionTransition) Interlocked.Increment(ref viaDirectionTransition);
        else Interlocked.Increment(ref viaGuardState);

        if (after == commanded) Interlocked.Increment(ref reached);
        else if (after == before) Interlocked.Increment(ref noChange);
        else Interlocked.Increment(ref changedButWrong);
    }

    public static void RecordReadFailure()
    {
        if (!enabled) return;
        Interlocked.Increment(ref readFailed);
    }

    public static string Snapshot(bool stop)
    {
        long n = Interlocked.Read(ref commands);
        var text = new StringBuilder();
        text.Append("guardCommandEffect: commands=").Append(n.ToString(CultureInfo.InvariantCulture));
        text.Append(" REACHED=").Append(Interlocked.Read(ref reached).ToString(CultureInfo.InvariantCulture));
        if (n > 0)
            text.Append('(').Append((100.0 * Interlocked.Read(ref reached) / n).ToString("F1", CultureInfo.InvariantCulture)).Append("%)");
        text.Append(" NO_CHANGE=").Append(Interlocked.Read(ref noChange).ToString(CultureInfo.InvariantCulture));
        if (n > 0)
            text.Append('(').Append((100.0 * Interlocked.Read(ref noChange) / n).ToString("F1", CultureInfo.InvariantCulture)).Append("%)");
        text.Append(" changedButWrong=").Append(Interlocked.Read(ref changedButWrong).ToString(CultureInfo.InvariantCulture));
        text.Append(" fromNativeMissing=").Append(Interlocked.Read(ref fromNativeMissing).ToString(CultureInfo.InvariantCulture));
        text.Append(" [viaGuardState:").Append(Interlocked.Read(ref viaGuardState).ToString(CultureInfo.InvariantCulture));
        text.Append(" viaDirTransition:").Append(Interlocked.Read(ref viaDirectionTransition).ToString(CultureInfo.InvariantCulture)).Append(']');
        text.Append(" readFailed=").Append(Interlocked.Read(ref readFailed).ToString(CultureInfo.InvariantCulture));
        text.Append(" | landedOn=[windup:").Append(Interlocked.Read(ref onWindup).ToString(CultureInfo.InvariantCulture));
        text.Append(" release:").Append(Interlocked.Read(ref onRelease).ToString(CultureInfo.InvariantCulture));
        text.Append(" defending:").Append(Interlocked.Read(ref onDefending).ToString(CultureInfo.InvariantCulture));
        text.Append(" nothing:").Append(Interlocked.Read(ref onNothing).ToString(CultureInfo.InvariantCulture));
        text.Append(" other:").Append(Interlocked.Read(ref onOther).ToString(CultureInfo.InvariantCulture)).Append(']');
        text.Append(" DESTROYED_WINDUP=").Append(Interlocked.Read(ref DESTROYED_WINDUP).ToString(CultureInfo.InvariantCulture));
        text.Append(" DESTROYED_RELEASE=").Append(Interlocked.Read(ref DESTROYED_RELEASE).ToString(CultureInfo.InvariantCulture));
        if (stop) enabled = false;
        return text.ToString();
    }
}
