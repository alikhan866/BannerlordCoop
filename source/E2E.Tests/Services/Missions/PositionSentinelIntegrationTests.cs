using E2E.Tests.Environment.Mock;
using E2E.Tests.Environment.MockEngine;
using Missions.Agents;
using Missions.Agents.Packets;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Xunit;
using Xunit.Abstractions;
using AgentData = Missions.Agents.Packets.AgentData;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Proves that a packet carrying no usable position never moves an agent.
/// </summary>
/// <remarks>
/// <para>
/// The codec tests next door prove the encoding is faithful. These prove the other half: that the ONE case
/// the encoding cannot represent is handled safely all the way through to the agent.
/// </para>
/// <para>
/// The danger is specific and easy to get wrong. <c>AgentData.Position</c> reads <c>Vec3.Zero</c> when the
/// packet carries no position, and zero is a real place - the scene origin. A receiver that trusts it walks
/// the agent to the middle of the map, or, if the agent is more than six metres away, trips
/// <c>AgentPositionInterpolator</c>'s snap distance and teleports it there outright. That is the exact
/// failure this whole design exists to prevent, so it is worth proving rather than assuming.
/// </para>
/// </remarks>
public class PositionSentinelIntegrationTests : MissionTestEnvironment
{
    public PositionSentinelIntegrationTests(ITestOutputHelper output) : base(output) { }

    /// <summary>A position the encoder cannot represent, so the packet carries the unavailable marker.</summary>
    private static AgentData UnusablePosition(Vec2 direction) => new AgentData(
        position: new Vec3(float.NaN, 0f, 0f),
        movementDirection: direction,
        lookDirection: new Vec3(direction.X, direction.Y, 0f),
        inputVector: direction,
        speed: 1f);

    private static AgentData At(Vec3 position, Vec2 direction) => new AgentData(
        position: position,
        movementDirection: direction,
        lookDirection: new Vec3(direction.X, direction.Y, 0f),
        inputVector: direction,
        speed: 1f);

    [Fact]
    public void A_packet_without_a_position_reports_that_it_has_none()
    {
        Assert.False(UnusablePosition(Vec2.Forward).HasPosition);
        Assert.True(At(new Vec3(10f, 20f, 0f), Vec2.Forward).HasPosition);
    }

    /// <summary>
    /// The agent keeps walking to the last position we had reason to trust.
    /// </summary>
    /// <remarks>
    /// This is the assertion that matters most in the file. If the target became the origin, an agent
    /// standing anywhere else in the scene would be dragged across the map - and beyond six metres, teleported.
    /// </remarks>
    [Fact]
    public void An_unusable_position_leaves_the_previous_target_standing()
    {
        using var fixture = new MissionEngineFixture();
        var peer = Clients.First();

        peer.Call(() =>
        {
            var mock = CreateMovementMission(fixture, peer);
            Agent puppet = mock.SpawnAgent(
                new AgentBuildData(Game.Current.PlayerTroop).Controller(AgentControllerType.None));
            Assert.True(AgentMirror.TryGet(puppet, out var puppetMirror));

            var interpolator = new AgentPositionInterpolator();
            var trusted = new Vec3(25f, 40f, 0f);

            interpolator.SetRiderTarget(puppet, At(trusted, Vec2.Forward));
            interpolator.Tick(1f / 60f);
            Vec2 afterTrusted = puppetMirror.LastTargetPosition;

            // Now a packet arrives that cannot describe where the agent is.
            interpolator.SetRiderTarget(puppet, UnusablePosition(Vec2.Forward));
            interpolator.Tick(1f / 60f);

            Assert.Equal(afterTrusted, puppetMirror.LastTargetPosition);
            Assert.Equal(1, interpolator.UnusablePositionReports);

            // And specifically NOT the origin, which is what a naive read of Position would have produced.
            Assert.NotEqual(Vec2.Zero, puppetMirror.LastTargetPosition);
        });
    }

    /// <summary>
    /// With nothing trustworthy ever received, the agent is left exactly where the engine has it.
    /// </summary>
    /// <remarks>
    /// There is no previous target to fall back on here, so the only safe answer is to do nothing at all -
    /// not to invent one. A target of zero would be a teleport to the scene origin on the agent's very first
    /// update.
    /// </remarks>
    [Fact]
    public void An_unusable_position_with_no_history_moves_nothing()
    {
        using var fixture = new MissionEngineFixture();
        var peer = Clients.First();

        peer.Call(() =>
        {
            var mock = CreateMovementMission(fixture, peer);
            Agent puppet = mock.SpawnAgent(
                new AgentBuildData(Game.Current.PlayerTroop).Controller(AgentControllerType.None));
            Assert.True(AgentMirror.TryGet(puppet, out var puppetMirror));

            var interpolator = new AgentPositionInterpolator();

            interpolator.SetRiderTarget(puppet, UnusablePosition(Vec2.Forward));
            interpolator.Tick(1f / 60f);

            Assert.Equal(0, puppetMirror.SetTargetPositionAndDirectionCalls);
            Assert.Equal(1, interpolator.UnusablePositionReports);
        });
    }

    /// <summary>A usable position still drives the agent exactly as before.</summary>
    /// <remarks>
    /// The guard must not be so cautious that it stops ordinary movement working. Without this, a bug that
    /// refused every position would pass every other test in this file.
    /// </remarks>
    [Fact]
    public void A_usable_position_still_drives_the_agent()
    {
        using var fixture = new MissionEngineFixture();
        var peer = Clients.First();

        peer.Call(() =>
        {
            var mock = CreateMovementMission(fixture, peer);
            Agent puppet = mock.SpawnAgent(
                new AgentBuildData(Game.Current.PlayerTroop).Controller(AgentControllerType.None));
            Assert.True(AgentMirror.TryGet(puppet, out var puppetMirror));

            var interpolator = new AgentPositionInterpolator();
            var target = new Vec3(12.34f, -56.78f, 0f);

            interpolator.SetRiderTarget(puppet, At(target, Vec2.Forward));
            interpolator.Tick(1f / 60f);

            Assert.Equal(1, puppetMirror.SetTargetPositionAndDirectionCalls);
            Assert.Equal(0, interpolator.UnusablePositionReports);

            // Within the wire's precision of where the owner said it was.
            Assert.True(
                (puppetMirror.LastTargetPosition - target.AsVec2).Length
                    <= MovementQuantizer.PositionTolerance * 2f,
                $"target {puppetMirror.LastTargetPosition} strayed from {target.AsVec2}");
        });
    }

    /// <summary>
    /// A position far beyond the representable range is refused rather than clamped to the boundary.
    /// </summary>
    /// <remarks>
    /// Clamping is the intuitive choice and the dangerous one: reporting a distant agent as standing at the
    /// boundary is a difference of kilometres, which is well past the snap distance, so clamping would cause
    /// the teleport rather than prevent it.
    /// </remarks>
    [Fact]
    public void A_position_beyond_the_range_does_not_clamp_the_agent_to_the_boundary()
    {
        using var fixture = new MissionEngineFixture();
        var peer = Clients.First();

        peer.Call(() =>
        {
            var mock = CreateMovementMission(fixture, peer);
            Agent puppet = mock.SpawnAgent(
                new AgentBuildData(Game.Current.PlayerTroop).Controller(AgentControllerType.None));
            Assert.True(AgentMirror.TryGet(puppet, out var puppetMirror));

            var interpolator = new AgentPositionInterpolator();
            var trusted = new Vec3(30f, 30f, 0f);

            interpolator.SetRiderTarget(puppet, At(trusted, Vec2.Forward));
            interpolator.Tick(1f / 60f);
            Vec2 afterTrusted = puppetMirror.LastTargetPosition;

            interpolator.SetRiderTarget(
                puppet,
                At(new Vec3(500_000f, 0f, 0f), Vec2.Forward));
            interpolator.Tick(1f / 60f);

            Assert.Equal(afterTrusted, puppetMirror.LastTargetPosition);
            Assert.Equal(1, interpolator.UnusablePositionReports);
        });
    }
}
