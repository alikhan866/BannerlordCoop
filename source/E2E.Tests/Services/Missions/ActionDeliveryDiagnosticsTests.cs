using Missions.Diagnostics;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// The counter that finds where a remote attack's opening packet goes.
/// </summary>
/// <remarks>
/// Action packets are <c>ReliableOrdered</c> and the sender emits one on the tick an action index changes, so the
/// packet carrying the start of a swing must arrive, and arrive first. Remote attacks were nevertheless measured
/// beginning up to 88% in, at zero latency. Something on the receiving side discards the start, and the outcome
/// each packet already gets - applied / stale / agentNotReady / wrongAuthority - names which guard did it. Each
/// implies a different bug and a different fix, which is why this counts rather than guesses.
/// </remarks>
[Collection("ActionDeliveryDiagnostics")]
public class ActionDeliveryDiagnosticsTests
{
    // Ordinals mirror RemoteAgentActionProcessor.RemoteActionApplyResult, which is private to that class.
    private const int Applied = 0;
    private const int AgentNotReady = 1;
    private const int Stale = 2;
    private const int WrongAuthority = 3;

    /// <summary>Melee attacks ride the upper-body channel, so that is the one under test.</summary>
    private const int Ch = 1;

    private static string Snapshot() => ActionDeliveryDiagnostics.Snapshot(stop: true);

    public ActionDeliveryDiagnosticsTests() => ActionDeliveryDiagnostics.Start();

    [Fact]
    public void EveryOutcomeIsCountedSeparately()
    {
        ActionDeliveryDiagnostics.Record(Applied, Ch, 0.02f, "ali");
        ActionDeliveryDiagnostics.Record(Stale, Ch, 0.01f, "ali");
        ActionDeliveryDiagnostics.Record(Stale, Ch, 0.01f, "ali");
        ActionDeliveryDiagnostics.Record(WrongAuthority, Ch, 0.9f, "omar");

        string text = Snapshot();
        Assert.Contains("packets=4", text);
        Assert.Contains("applied.ch1=1", text);
        Assert.Contains("stale.ch1=2", text);
        Assert.Contains("wrongAuthority.ch1=1", text);
        Assert.Contains("agentNotReady.ch0=0", text);
    }

    /// <summary>
    /// The shape that would prove the diagnosis: discarded packets carrying the START of a swing while the ones
    /// that survive carry a swing already well advanced. Each outcome keeps its own mean so that asymmetry cannot
    /// average itself away.
    /// </summary>
    [Fact]
    public void DroppedAndAppliedPacketsKeepSeparateProgressDistributions()
    {
        ActionDeliveryDiagnostics.Record(Stale, Ch, 0.01f, "ali");
        ActionDeliveryDiagnostics.Record(Stale, Ch, 0.03f, "ali");
        ActionDeliveryDiagnostics.Record(Applied, Ch, 0.80f, "ali");
        ActionDeliveryDiagnostics.Record(Applied, Ch, 0.90f, "ali");

        string text = Snapshot();
        int appliedAt = text.IndexOf("| applied.ch1=", System.StringComparison.Ordinal);
        int staleAt = text.IndexOf("| stale.ch1=", System.StringComparison.Ordinal);
        Assert.True(appliedAt >= 0 && staleAt > appliedAt, text);

        Assert.Contains("meanProgress=85%", text.Substring(appliedAt, staleAt - appliedAt));
        Assert.Contains("meanProgress=2%", text.Substring(staleAt));
    }

    /// <summary>
    /// More than one controller behind a drop is the fingerprint of agents changing hands mid-swing - the
    /// strongest untested candidate, since the defect followed ownership when the players swapped roles.
    /// </summary>
    [Fact]
    public void DistinctControllersPerOutcomeAreTracked()
    {
        ActionDeliveryDiagnostics.Record(WrongAuthority, Ch, 0.5f, "ali");
        ActionDeliveryDiagnostics.Record(WrongAuthority, Ch, 0.5f, "omar");
        ActionDeliveryDiagnostics.Record(WrongAuthority, Ch, 0.5f, "omar");
        ActionDeliveryDiagnostics.Record(Applied, Ch, 0.5f, "ali");

        string text = Snapshot();
        int wrongAt = text.IndexOf("| wrongAuthority.ch1=", System.StringComparison.Ordinal);
        Assert.Contains("controllers=2", text.Substring(wrongAt));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-2f)]
    public void UntrustedProgressIsClampedRatherThanPoisoningTheMean(float progress)
    {
        ActionDeliveryDiagnostics.Record(Applied, Ch, progress, "ali");

        Assert.Contains("meanProgress=0%", Snapshot());
    }

    [Fact]
    public void ProgressAboveOneIsClampedToTheWholeAnimation()
    {
        ActionDeliveryDiagnostics.Record(Applied, Ch, 4f, "ali");

        Assert.Contains("meanProgress=100%", Snapshot());
    }

    [Fact]
    public void AnUnrecognisedOutcomeIsCountedRatherThanSilentlyDropped()
    {
        // Guards against the apply-result enum gaining a member, or a third channel appearing, without this
        // counter being updated - either would otherwise be recorded into the wrong slot or silently lost.
        ActionDeliveryDiagnostics.Record(99, Ch, 0.5f, "ali");
        ActionDeliveryDiagnostics.Record(-1, Ch, 0.5f, "ali");
        ActionDeliveryDiagnostics.Record(Applied, 2, 0.5f, "ali");
        ActionDeliveryDiagnostics.Record(Applied, -1, 0.5f, "ali");
        ActionDeliveryDiagnostics.Record(Applied, Ch, 0.5f, "ali");

        Assert.Contains("unknownOutcome=4", Snapshot());
    }

    [Fact]
    public void NoPacketsReportsThatPlainlyRatherThanZeroes()
    {
        Assert.Contains("no remote action packets observed", Snapshot());
    }

    [Fact]
    public void SnapshotWithoutStopLeavesMeasurementRunning()
    {
        ActionDeliveryDiagnostics.Record(Applied, Ch, 0.5f, "ali");
        Assert.Contains("packets=1", ActionDeliveryDiagnostics.Snapshot(stop: false));
        Assert.True(ActionDeliveryDiagnostics.Enabled);

        ActionDeliveryDiagnostics.Record(Applied, Ch, 0.5f, "ali");
        Assert.Contains("packets=2", ActionDeliveryDiagnostics.Snapshot(stop: true));
        Assert.False(ActionDeliveryDiagnostics.Enabled);
    }

    [Fact]
    public void RecordingWhileStoppedIsIgnored()
    {
        ActionDeliveryDiagnostics.Snapshot(stop: true);
        ActionDeliveryDiagnostics.Record(Applied, Ch, 0.5f, "ali");

        Assert.Contains("no remote action packets observed", ActionDeliveryDiagnostics.Snapshot(stop: true));
    }

    /// <summary>A restart must not carry the previous battle's numbers into the next one.</summary>
    [Fact]
    public void StartClearsEverythingFromThePreviousRun()
    {
        ActionDeliveryDiagnostics.Record(Stale, Ch, 0.9f, "ali");
        ActionDeliveryDiagnostics.Start();

        Assert.Contains("no remote action packets observed", Snapshot());
    }
}
