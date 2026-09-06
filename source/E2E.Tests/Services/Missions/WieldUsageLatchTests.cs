using System.Diagnostics;
using Missions.Agents.Handlers;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// A couched lance flips the owner's usage index every frame (0, 2, 0, 2 ...) while the owner sees a steady
/// couch; the wire must carry "couched" once, not the flicker.
/// </summary>
public class WieldUsageLatchTests
{
    private static long Ms(double milliseconds) => (long)(milliseconds / 1000.0 * Stopwatch.Frequency);

    [Fact]
    public void PlainPress_IsReportedAtOnce()
    {
        var latch = new WieldUsageLatch();
        Assert.Equal(0, latch.Filter(0, Ms(0)));
        Assert.Equal(1, latch.Filter(1, Ms(16)));
        Assert.Equal(1, latch.Filter(1, Ms(32)));
    }

    [Fact]
    public void PerFrameFlicker_HoldsTheCouchedValue()
    {
        var latch = new WieldUsageLatch();
        latch.Filter(0, Ms(0));
        latch.Filter(0, Ms(500));
        int changes = 0, last = 0;
        // 60 Hz polls of a value that alternates every 5 ms frame: the poll sees a pseudo-random 0 / 2.
        int[] raw = { 2, 0, 0, 2, 2, 0, 2, 0, 0, 0, 2, 2, 0, 2, 0, 2, 2, 0, 2, 0 };
        for (int i = 0; i < raw.Length; i++)
        {
            int reported = latch.Filter(raw[i], Ms(516 + i * 16.7));
            if (reported != last) changes++;
            last = reported;
            Assert.Equal(2, reported);
        }
        Assert.Equal(1, changes);
    }

    [Fact]
    public void FlickerEnding_SettlesToTheStillValueAfterOneWindow()
    {
        var latch = new WieldUsageLatch();
        latch.Filter(0, Ms(0));
        latch.Filter(0, Ms(500));
        double t = 516;
        foreach (int raw in new[] { 2, 0, 2, 0, 2, 0 })
        {
            latch.Filter(raw, Ms(t));
            t += 16.7;
        }
        Assert.Equal(2, latch.Filter(0, Ms(t)));                    // still inside the window: held
        Assert.Equal(0, latch.Filter(0, Ms(t + 160)));              // still for a window: uncouched
    }

    [Fact]
    public void TwoSlowPresses_AreBothReported()
    {
        var latch = new WieldUsageLatch();
        latch.Filter(0, Ms(0));
        Assert.Equal(1, latch.Filter(1, Ms(400)));
        Assert.Equal(1, latch.Filter(1, Ms(600)));
        Assert.Equal(0, latch.Filter(0, Ms(800)));
    }
}
