using E2E.Tests.Environment.Mock;
using E2E.Tests.Environment.MockEngine;
using GameInterface.Services.Entity;
using Missions;
using Missions.Agents.Handlers;
using Missions.Agents.Packets;
using Missions.Services.Network;
using System;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Covers what makes a masterless horse worth a packet.
/// </summary>
/// <remarks>
/// <para>
/// Masterless horses were about half of all mission-mesh traffic — 18,459 of ~37,000 packets in a
/// ten-second window — and their share grew through a battle, because every cavalry death adds one and
/// nothing removes it until it bolts off the map. They also defeat the existing suppression completely: a
/// bolting horse clears the 0.57-degree direction threshold on every tick, so "only send on change" sent on
/// every change of a heading nobody can perceive.
/// </para>
/// <para>
/// Position keeps its normal one-centimetre threshold on purpose. Earlier attempts at this traded
/// positional accuracy for bandwidth, which shows up as horses hopping between points — the visible
/// artefact this must not introduce. What changed is only WHICH differences justify a packet, never how
/// precisely a horse's position is tracked.
/// </para>
/// </remarks>
public class MasterlessHorseSendTests : MissionTestEnvironment
{
    public MasterlessHorseSendTests(ITestOutputHelper output) : base(output) { }

    private static Agent SpawnLooseHorse(MockMission mock)
    {
        Agent horse = mock.SpawnAgent(
            new AgentBuildData(Game.Current.PlayerTroop).Controller(AgentControllerType.AI));
        Assert.True(AgentMirror.TryGet(horse, out var mirror));
        mirror.IsMount = true;
        mirror.RiderAgent = null;
        return horse;
    }

    private static int MountPacketCount(MockBattleNetwork network) =>
        network.NetworkSentPackets.GetPackets<MountMovementPacket>().Count();

    [Fact]
    public void A_horse_that_only_changes_heading_is_no_longer_worth_a_packet()
    {
        using var fixture = new MissionEngineFixture();
        var peer = Clients.First();
        SetControllerId(peer, "peer");

        peer.Call(() =>
        {
            var mock = CreateMovementMission(fixture, peer);
            var registry = peer.Resolve<INetworkAgentRegistry>();
            var component = peer.Resolve<ICoopMissionComponent>();
            var network = Assert.IsType<MockBattleNetwork>(peer.Resolve<IBattleNetwork>());

            Agent horse = SpawnLooseHorse(mock);
            Assert.True(AgentMirror.TryGet(horse, out var mirror));
            Assert.True(registry.TryRegisterAgent("peer", Guid.NewGuid(), 1, horse));

            component.AgentMovementHandler.PollMovement(0f);
            network.NetworkSentPackets.Packets.Clear();

            // Turning on the spot: the heading moves, the horse does not. Nobody is fighting this animal and
            // its heading rides along inside the next position packet anyway.
            mirror.LookDirection = new Vec3(1f, 0f, 0f);
            mirror.MovementDirection = new Vec2(1f, 0f);
            component.AgentMovementHandler.PollMovement(0.025f);

            Assert.Equal(0, MountPacketCount(network));
        });
    }

    /// <summary>
    /// A horse that has moved is reported with its real position - spaced out in time, never approximated.
    /// </summary>
    /// <remarks>
    /// The distinction this pins is the whole design. Masterless horses are rate limited, so an update can
    /// wait up to <c>MasterlessMinSendIntervalSeconds</c>; what is never done is coarsening the position
    /// threshold, because that is what makes a horse appear to hop between points instead of moving. A ten
    /// centimetre move still qualifies once the interval has passed.
    /// </remarks>
    [Fact]
    public void A_horse_that_moves_is_reported_with_its_real_position_after_the_interval()
    {
        using var fixture = new MissionEngineFixture();
        var peer = Clients.First();
        SetControllerId(peer, "peer");

        peer.Call(() =>
        {
            var mock = CreateMovementMission(fixture, peer);
            var registry = peer.Resolve<INetworkAgentRegistry>();
            var component = peer.Resolve<ICoopMissionComponent>();
            var network = Assert.IsType<MockBattleNetwork>(peer.Resolve<IBattleNetwork>());

            Agent horse = SpawnLooseHorse(mock);
            Assert.True(AgentMirror.TryGet(horse, out var mirror));
            Assert.True(registry.TryRegisterAgent("peer", Guid.NewGuid(), 1, horse));

            component.AgentMovementHandler.PollMovement(0f);
            network.NetworkSentPackets.Packets.Clear();

            // A tenth of a metre - far below anything a player could notice, and still qualifies. Position
            // keeps its original one-centimetre threshold precisely so loose horses never hop between
            // points; only the RATE is limited.
            mirror.Position += new Vec3(0.1f, 0f, 0f);

            // Inside the interval it waits...
            component.AgentMovementHandler.PollMovement(0.025f);
            Assert.Equal(0, MountPacketCount(network));

            // ...and past it, the real position goes out.
            for (int i = 0; i < 10; i++)
                component.AgentMovementHandler.PollMovement(0.025f);

            Assert.True(MountPacketCount(network) >= 1);
        });
    }

    [Fact]
    public void A_bolting_horse_is_rate_limited_instead_of_sent_every_tick()
    {
        using var fixture = new MissionEngineFixture();
        var peer = Clients.First();
        SetControllerId(peer, "peer");

        peer.Call(() =>
        {
            var mock = CreateMovementMission(fixture, peer);
            var registry = peer.Resolve<INetworkAgentRegistry>();
            var component = peer.Resolve<ICoopMissionComponent>();
            var network = Assert.IsType<MockBattleNetwork>(peer.Resolve<IBattleNetwork>());

            Agent horse = SpawnLooseHorse(mock);
            Assert.True(AgentMirror.TryGet(horse, out var mirror));
            Assert.True(registry.TryRegisterAgent("peer", Guid.NewGuid(), 1, horse));

            component.AgentMovementHandler.PollMovement(0f);
            network.NetworkSentPackets.Packets.Clear();

            // Half a second of a horse bolting: it moves every tick, which is exactly the case the
            // per-field counter proved was defeating every other form of suppression.
            for (int i = 0; i < 20; i++)
            {
                mirror.Position += new Vec3(0.5f, 0f, 0f);
                component.AgentMovementHandler.PollMovement(0.025f);
            }

            // Twenty opportunities, held to roughly one per 0.2s rather than one per tick.
            int sent = MountPacketCount(network);
            Assert.True(sent > 0, "a bolting horse must still be reported");
            Assert.True(sent <= 6, $"expected the rate limit to hold sends near 0.2s apart, saw {sent}");
        });
    }

    [Fact]
    public void The_rate_limit_never_silences_a_horse_completely()
    {
        using var fixture = new MissionEngineFixture();
        var peer = Clients.First();
        SetControllerId(peer, "peer");

        peer.Call(() =>
        {
            var mock = CreateMovementMission(fixture, peer);
            var registry = peer.Resolve<INetworkAgentRegistry>();
            var component = peer.Resolve<ICoopMissionComponent>();
            var network = Assert.IsType<MockBattleNetwork>(peer.Resolve<IBattleNetwork>());

            Agent horse = SpawnLooseHorse(mock);
            Assert.True(AgentMirror.TryGet(horse, out var mirror));
            Assert.True(registry.TryRegisterAgent("peer", Guid.NewGuid(), 1, horse));

            component.AgentMovementHandler.PollMovement(0f);
            network.NetworkSentPackets.Packets.Clear();

            // Past the interval, a horse that has moved is reported - the limit spaces sends out, it does
            // not drop them, so a peer can never be left holding a stale position indefinitely.
            mirror.Position += new Vec3(2f, 0f, 0f);
            for (int i = 0; i < 12; i++)
                component.AgentMovementHandler.PollMovement(0.025f);

            Assert.True(MountPacketCount(network) >= 1);
        });
    }

    [Fact]
    public void A_stationary_horse_still_reports_on_the_heartbeat()
    {
        using var fixture = new MissionEngineFixture();
        var peer = Clients.First();
        SetControllerId(peer, "peer");

        peer.Call(() =>
        {
            var mock = CreateMovementMission(fixture, peer);
            var registry = peer.Resolve<INetworkAgentRegistry>();
            var component = peer.Resolve<ICoopMissionComponent>();
            var network = Assert.IsType<MockBattleNetwork>(peer.Resolve<IBattleNetwork>());

            Agent horse = SpawnLooseHorse(mock);
            Assert.True(registry.TryRegisterAgent("peer", Guid.NewGuid(), 1, horse));

            component.AgentMovementHandler.PollMovement(0f);
            network.NetworkSentPackets.Packets.Clear();

            // Suppression must never mean silence: the forced-sync interval still reconciles a horse that
            // has done nothing at all, so a peer cannot drift away from it indefinitely.
            for (int i = 0; i < 40; i++)
                component.AgentMovementHandler.PollMovement(0.025f);

            Assert.True(MountPacketCount(network) >= 1);
        });
    }

    [Fact]
    public void A_ridden_horse_is_untouched_by_this_rule()
    {
        using var fixture = new MissionEngineFixture();
        var peer = Clients.First();
        SetControllerId(peer, "peer");

        peer.Call(() =>
        {
            var mock = CreateMovementMission(fixture, peer);

            Agent rider = mock.SpawnAgent(
                new AgentBuildData(Game.Current.PlayerTroop).Controller(AgentControllerType.AI));
            Agent horse = mock.SpawnMount(rider);
            Assert.True(AgentMirror.TryGet(horse, out var horseMirror));
            horseMirror.RiderAgent = rider;

            // The whole relaxation rests on this: a mount carrying an active rider is never broadcast on the
            // masterless path, so its fidelity cannot be affected. Its movement travels inside the rider's
            // own AgentData instead.
            Assert.False(AgentMovementHandler.ShouldBroadcastMovement(horse));
        });
    }
}
