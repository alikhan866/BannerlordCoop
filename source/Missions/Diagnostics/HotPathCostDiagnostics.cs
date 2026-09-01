using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Missions.Diagnostics;

/// <summary>
/// Measures what the code added this session actually costs, as time rather than as an adjective.
/// </summary>
/// <remarks>
/// "One dictionary lookup and ten float operations per moving puppet per tick" is not something anyone
/// can weigh a decision against. An A/B against a build with the work disabled was tried and was
/// useless: run-to-run variance moved `wireBytesPerSecond` by 31-36% for a change that touches no wire
/// format at all, so the noise was larger than the effect.
///
/// Timing the paths directly avoids that entirely. The figures to compare against, from
/// `coop.debug.movement.state` on the same battle, are `senderMsPerSecond` (about 136 ms of every
/// second on the busier peer) and `receiverApplyMsPerSecond` (about 11-37 ms), against a frame budget
/// of roughly 23 ms at 43 fps.
/// </remarks>
internal static class HotPathCostDiagnostics
{
    private static readonly double TicksToMilliseconds = 1000.0 / Stopwatch.Frequency;

    private static bool enabled;
    private static long startedAt;

    private static long deadReckonCalls;
    private static long deadReckonTicks;
    private static long damageAttributionCalls;
    private static long damageAttributionTicks;

    public static bool Enabled => enabled;

    public static void Start()
    {
        Interlocked.Exchange(ref deadReckonCalls, 0);
        Interlocked.Exchange(ref deadReckonTicks, 0);
        Interlocked.Exchange(ref damageAttributionCalls, 0);
        Interlocked.Exchange(ref damageAttributionTicks, 0);
        startedAt = Stopwatch.GetTimestamp();
        enabled = true;
    }

    public static long Now() => enabled ? Stopwatch.GetTimestamp() : 0L;

    public static void AddDeadReckon(long startTimestamp)
    {
        if (!enabled || startTimestamp == 0L) return;
        Interlocked.Add(ref deadReckonTicks, Stopwatch.GetTimestamp() - startTimestamp);
        Interlocked.Increment(ref deadReckonCalls);
    }

    public static void AddDamageAttribution(long startTimestamp)
    {
        if (!enabled || startTimestamp == 0L) return;
        Interlocked.Add(ref damageAttributionTicks, Stopwatch.GetTimestamp() - startTimestamp);
        Interlocked.Increment(ref damageAttributionCalls);
    }

    private static void AppendPath(StringBuilder text, string name, long calls, long ticks, double seconds)
    {
        double totalMs = ticks * TicksToMilliseconds;
        text.Append(' ').Append(name).Append("=[calls:").Append(calls.ToString(CultureInfo.InvariantCulture));
        if (seconds > 0)
        {
            double msPerSecond = totalMs / seconds;
            text.Append(" msPerSecond:").Append(msPerSecond.ToString("F3", CultureInfo.InvariantCulture));
            // Share of wall-clock time on this thread: 1000ms of every second is the whole budget.
            text.Append(" PERCENT_OF_A_SECOND:").Append((msPerSecond / 10.0).ToString("F3", CultureInfo.InvariantCulture));
            text.Append(" callsPerSecond:").Append((calls / seconds).ToString("F0", CultureInfo.InvariantCulture));
        }
        if (calls > 0)
            text.Append(" usPerCall:").Append((totalMs * 1000.0 / calls).ToString("F2", CultureInfo.InvariantCulture));
        text.Append(']');
    }

    public static string Snapshot(bool stop)
    {
        double seconds = startedAt == 0
            ? 0
            : (Stopwatch.GetTimestamp() - startedAt) * TicksToMilliseconds / 1000.0;

        var text = new StringBuilder();
        text.Append("hotPathCost: over=").Append(seconds.ToString("F0", CultureInfo.InvariantCulture)).Append('s');
        AppendPath(text, "deadReckoning",
            Interlocked.Read(ref deadReckonCalls), Interlocked.Read(ref deadReckonTicks), seconds);
        AppendPath(text, "damageAttribution",
            Interlocked.Read(ref damageAttributionCalls), Interlocked.Read(ref damageAttributionTicks), seconds);

        if (stop) enabled = false;
        return text.ToString();
    }
}
