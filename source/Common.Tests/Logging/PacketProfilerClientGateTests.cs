using Common.Logging;
using Common.PacketHandlers;
using Common.Util;
using LiteNetLib;
using System;
using Xunit;

namespace Common.Tests.Logging;

/// <summary>
/// Covers the rule deciding whether a <see cref="PacketProfiler"/> records on a client.
/// </summary>
/// <remarks>
/// <para>
/// Worth pinning because the default protects players: the server-pipe profiler dumps a packet breakdown
/// every ten seconds, and if it ever started recording on clients too, every player's log would fill with
/// traffic dumps they never asked for. The mission-mesh profiler is the one deliberate exception, and it
/// has to work — with the guard applied to it, per-agent battle traffic is invisible on both sides, which
/// is precisely the blind spot that made a stuttering battle impossible to diagnose.
/// </para>
/// <para>
/// Serialized into its own collection: <see cref="ModInformation.IsServer"/> is process-global mutable
/// state, so these must not run beside anything else reading it.
/// </para>
/// </remarks>
[Collection(nameof(PacketProfilerClientGateTests))]
public class PacketProfilerClientGateTests
{
    /// <summary>A packet that is not a MessagePacket, so naming needs no serializer.</summary>
    private readonly struct ProbePacket : IPacket
    {
        public DeliveryMethod DeliveryMethod => DeliveryMethod.Unreliable;
        public PacketType PacketType => PacketType.Message;
    }

    /// <summary>Long enough that the poller cannot drain the window mid-test.</summary>
    private static PacketProfiler NewProfiler(bool profileOnClient) =>
        new PacketProfiler(TimeSpan.FromHours(1), scope: "Test", profileOnClient: profileOnClient);

    private static void AsRole(bool isServer, Action body)
    {
        bool original = ModInformation.IsServer;
        try
        {
            ModInformation.IsServer = isServer;
            body();
        }
        finally
        {
            ModInformation.IsServer = original;
        }
    }

    [Fact]
    public void The_server_pipe_profiler_records_on_the_server()
    {
        AsRole(isServer: true, () =>
        {
            using var profiler = NewProfiler(profileOnClient: false);

            profiler.Record(new ProbePacket(), 128);

            Assert.Equal(1, profiler.RecordedTypeCountForTest);
        });
    }

    [Fact]
    public void The_server_pipe_profiler_stays_silent_on_a_client()
    {
        AsRole(isServer: false, () =>
        {
            using var profiler = NewProfiler(profileOnClient: false);

            profiler.Record(new ProbePacket(), 128);

            // A player's log must not fill with server-pipe traffic dumps.
            Assert.Equal(0, profiler.RecordedTypeCountForTest);
        });
    }

    [Fact]
    public void The_mesh_profiler_records_on_a_client_which_is_the_only_place_it_can()
    {
        AsRole(isServer: false, () =>
        {
            using var profiler = NewProfiler(profileOnClient: true);

            profiler.Record(new ProbePacket(), 128);

            // The mission mesh runs client-to-client; if the guard applied here, per-agent battle
            // traffic would have no profiler anywhere.
            Assert.Equal(1, profiler.RecordedTypeCountForTest);
        });
    }

    [Fact]
    public void The_mesh_profiler_also_records_on_a_listen_server_host()
    {
        // A player hosting from their own game is IsServer AND runs missions, so the opt-in must not
        // accidentally become client-only.
        AsRole(isServer: true, () =>
        {
            using var profiler = NewProfiler(profileOnClient: true);

            profiler.Record(new ProbePacket(), 128);

            Assert.Equal(1, profiler.RecordedTypeCountForTest);
        });
    }
}
