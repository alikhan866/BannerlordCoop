using Missions.Diagnostics;
using Xunit;
using Kind = Missions.Diagnostics.WindupDiagnostics.EntryKind;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// The arithmetic that decides which wind-up fix is the right one.
/// </summary>
/// <remarks>
/// <para>
/// Live measurement showed remote attacks starting up to 87.5% into the swing at ZERO latency, so the cause is not
/// the network. Two candidates remain and they want opposite fixes: a late first apply (clamping
/// <c>startProgress</c> fixes it) versus the puppet repeatedly falling out and re-entering the same swing
/// (clamping makes it visibly worse - the swing would stutter and repeat). Keeping the two populations apart is
/// the whole point of this class, so these tests guard that separation.
/// </para>
/// <para>
/// Everything is a fraction of the animation. The millisecond version called
/// <c>MBActionSet.GetActionAnimationDuration(agent.ActionSet, action)</c> per applied action and killed a live
/// client with an <c>AccessViolationException</c> its try/catch could not intercept. No test here may reintroduce
/// a duration lookup to "improve" the units.
/// </para>
/// </remarks>
[Collection("WindupDiagnostics")]
public class WindupDiagnosticsTests
{
    private static string Snapshot() => WindupDiagnostics.Snapshot(stop: true);

    public WindupDiagnosticsTests() => WindupDiagnostics.Start();

    [Fact]
    public void EveryPopulationIsCountedSeparately()
    {
        WindupDiagnostics.RecordMeasuredAttack(0.90f, Kind.Spawn);
        WindupDiagnostics.RecordMeasuredAttack(0.10f, Kind.FirstEntry);
        WindupDiagnostics.RecordMeasuredAttack(0.80f, Kind.ReEntry);
        WindupDiagnostics.RecordMeasuredAttack(0.90f, Kind.ReEntry);

        string text = Snapshot();
        Assert.Contains("attacks=4", text);
        Assert.Contains("spawn.ch0=1", text);
        Assert.Contains("first.ch0=1", text);
        Assert.Contains("re-entry.ch0=2", text);
    }

    /// <summary>
    /// The distinction the spawn split exists for: a puppet created mid-swing SHOULD adopt a deep progress, so
    /// that population must never be averaged in with genuine lateness. Clamping the spawn case would rewind a
    /// swing the agent never had.
    /// </summary>
    [Fact]
    public void SpawnCatchUpIsKeptOutOfTheEstablishedPuppetAverage()
    {
        WindupDiagnostics.RecordMeasuredAttack(0.90f, Kind.Spawn);
        WindupDiagnostics.RecordMeasuredAttack(0.90f, Kind.Spawn);
        WindupDiagnostics.RecordMeasuredAttack(0.02f, Kind.FirstEntry);

        string text = Snapshot();
        int spawnAt = text.IndexOf("| spawn.ch0=", System.StringComparison.Ordinal);
        int firstAt = text.IndexOf("| first.ch0=", System.StringComparison.Ordinal);
        Assert.True(spawnAt >= 0 && firstAt > spawnAt, text);

        Assert.Contains("mean=90%", text.Substring(spawnAt, firstAt - spawnAt));
        Assert.Contains("mean=2%", text.Substring(firstAt));
    }

    /// <summary>
    /// The decisive question: which population owns the deep skips. Each keeps its own mean, so a re-entry
    /// cluster cannot hide inside a healthy first-entry average.
    /// </summary>
    [Fact]
    public void EachPopulationKeepsItsOwnDistribution()
    {
        WindupDiagnostics.RecordMeasuredAttack(0.02f, Kind.FirstEntry);
        WindupDiagnostics.RecordMeasuredAttack(0.02f, Kind.FirstEntry);
        WindupDiagnostics.RecordMeasuredAttack(0.875f, Kind.ReEntry);

        string text = Snapshot();
        int firstAt = text.IndexOf("| first.ch0=", System.StringComparison.Ordinal);
        int reAt = text.IndexOf("| re-entry.ch0=", System.StringComparison.Ordinal);
        Assert.True(firstAt >= 0 && reAt > firstAt, text);

        string first = text.Substring(firstAt, reAt - firstAt);
        string reentry = text.Substring(reAt);

        Assert.Contains("mean=2%", first);
        Assert.Contains("mean=87.5%", reentry);
        Assert.Contains("<2.5%:2", first);
        Assert.Contains(">=50%:1", reentry);
    }

    [Fact]
    public void AttacksThatStartAtTheBeginningAreNotCountedAsSkips()
    {
        WindupDiagnostics.RecordMeasuredAttack(0f, Kind.FirstEntry);
        WindupDiagnostics.RecordMeasuredAttack(0.005f, Kind.FirstEntry);

        string text = Snapshot();
        Assert.Contains("first.ch0=2 withSkip=0", text);
    }

    /// <summary>Progress comes off the wire, so a corrupt value must not poison the aggregate.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteProgressIsRejectedAndCounted(float progress)
    {
        WindupDiagnostics.RecordMeasuredAttack(progress, Kind.FirstEntry);

        string text = Snapshot();
        Assert.Contains("attacks=1", text);
        Assert.Contains("rejected=1", text);
        Assert.Contains("first.ch0=1 withSkip=0", text);
    }

    [Fact]
    public void NegativeProgressIsClampedToNoSkip()
    {
        WindupDiagnostics.RecordMeasuredAttack(-0.5f, Kind.FirstEntry);

        string text = Snapshot();
        Assert.Contains("first.ch0=1 withSkip=0", text);
        Assert.Contains("rejected=0", text);
    }

    [Fact]
    public void ProgressAboveOneIsClampedToTheWholeAnimation()
    {
        WindupDiagnostics.RecordMeasuredAttack(3f, Kind.ReEntry);

        Assert.Contains("max=100%", Snapshot());
    }

    [Fact]
    public void CameFromNoneIsCountedIndependentlyOfClassification()
    {
        WindupDiagnostics.RecordMeasuredAttack(0.5f, Kind.FirstEntry, channel: 0, cameFromNone: true);
        WindupDiagnostics.RecordMeasuredAttack(0.5f, Kind.ReEntry, channel: 0, cameFromNone: true);
        WindupDiagnostics.RecordMeasuredAttack(0.5f, Kind.FirstEntry, channel: 0, cameFromNone: false);

        Assert.Contains("cameFromNone=2", Snapshot());
    }

    [Fact]
    public void BucketsSeparateTrivialSkipsFromSevereOnes()
    {
        WindupDiagnostics.RecordMeasuredAttack(0.02f, Kind.FirstEntry);
        WindupDiagnostics.RecordMeasuredAttack(0.12f, Kind.FirstEntry);
        WindupDiagnostics.RecordMeasuredAttack(0.60f, Kind.FirstEntry);

        string text = Snapshot();
        Assert.Contains("<2.5%:1", text);
        Assert.Contains("<20%:1", text);
        Assert.Contains(">=50%:1", text);
    }

    [Fact]
    public void NoAttacksReportsThatPlainlyRatherThanZeroes()
    {
        Assert.Contains("no remote attack transitions observed", Snapshot());
    }

    [Fact]
    public void SnapshotWithoutStopLeavesMeasurementRunning()
    {
        WindupDiagnostics.RecordMeasuredAttack(0.5f, Kind.FirstEntry);
        Assert.Contains("attacks=1", WindupDiagnostics.Snapshot(stop: false));
        Assert.True(WindupDiagnostics.Enabled);

        WindupDiagnostics.RecordMeasuredAttack(0.5f, Kind.FirstEntry);
        Assert.Contains("attacks=2", WindupDiagnostics.Snapshot(stop: true));
        Assert.False(WindupDiagnostics.Enabled);
    }

    [Fact]
    public void RecordingWhileStoppedIsIgnored()
    {
        WindupDiagnostics.Snapshot(stop: true);
        WindupDiagnostics.RecordMeasuredAttack(0.5f, Kind.FirstEntry);

        Assert.Contains("no remote attack transitions observed", WindupDiagnostics.Snapshot(stop: true));
    }

    /// <summary>A restart must not carry the previous battle's numbers into the next one.</summary>
    [Fact]
    public void StartClearsEverythingFromThePreviousRun()
    {
        WindupDiagnostics.RecordMeasuredAttack(0.9f, Kind.ReEntry, channel: 0, cameFromNone: true);
        WindupDiagnostics.Start();

        string text = Snapshot();
        Assert.Contains("no remote attack transitions observed", text);
    }
}
