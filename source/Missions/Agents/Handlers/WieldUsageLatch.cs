using System.Diagnostics;

namespace Missions.Agents.Handlers;

/// <summary>
/// Steadies a wielded weapon's usage index before it goes on the wire.
/// </summary>
/// <remarks>
/// <para>
/// A couched lance is a weapon USAGE (the lance's third usage), and while it is couched the owner's managed
/// <c>MissionWeapon.CurrentUsageIndex</c> reads 0, 2, 0, 2 on consecutive frames - the engine flips it every frame
/// even though the owner's own screen shows a steadily couched lance. Measured on the duel rig (run m-lance-before,
/// `couch` script): 235 equipment packets in ten seconds instead of one, and the other player's copy of the lance
/// snapping between couched and upright in 15-90 ms bursts.
/// </para>
/// <para>
/// The latch reports a plain change at once (a player pressing X must not wait), but once the raw value has flipped
/// twice inside a short window it is a flicker, and the latch holds the value the flicker toggled TO (couched) until
/// the raw value has been still for the window. A genuine double press within the window settles the same way, one
/// window late.
/// </para>
/// </remarks>
internal sealed class WieldUsageLatch
{
    /// <summary>Two raw changes inside this window are a flicker, not two presses.</summary>
    internal const float FlickerWindowSeconds = 0.15f;

    private int stable;
    private int reported;
    private int lastRaw;
    private long lastRawChangeTicks;
    private int flipsInWindow;
    private bool initialized;

    /// <summary>The usage index to report for <paramref name="raw"/> observed at <paramref name="nowTicks"/> (Stopwatch ticks).</summary>
    public int Filter(int raw, long nowTicks)
    {
        if (!initialized)
        {
            initialized = true;
            stable = reported = lastRaw = raw;
            lastRawChangeTicks = nowTicks;
            return reported;
        }

        float sinceLastChange = (float)(nowTicks - lastRawChangeTicks) / Stopwatch.Frequency;
        if (raw != lastRaw)
        {
            flipsInWindow = sinceLastChange <= FlickerWindowSeconds ? flipsInWindow + 1 : 1;
            lastRaw = raw;
            lastRawChangeTicks = nowTicks;
            if (flipsInWindow >= 2)
            {
                // Flicker: hold whichever value is NOT the pre-flicker one; the engine is toggling into it.
                reported = raw != stable ? raw : reported;
                return reported;
            }
            reported = raw;   // a plain press: report it now
            return reported;
        }

        if (sinceLastChange >= FlickerWindowSeconds)
        {
            // Still for a whole window: this is the new steady state.
            stable = raw;
            reported = raw;
            flipsInWindow = 0;
        }
        return reported;
    }
}
