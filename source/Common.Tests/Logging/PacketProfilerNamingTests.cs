using Common.Logging;
using Common.Messaging;
using Common.PacketHandlers;
using Common.Serialization;
using LiteNetLib;
using ProtoBuf;
using System;
using Xunit;

namespace Common.Tests.Logging;

/// <summary>
/// Covers <see cref="PacketProfiler"/>'s packet naming, which the mission-mesh profiler calls on the
/// per-recipient send funnel — the highest-frequency path in a battle.
/// </summary>
/// <remarks>
/// The naming was moved behind a cache so a battle's packet storm stops allocating a fresh string per
/// packet. These tests pin the two things that could silently break: that the cached name is byte-for-byte
/// what the uncached formatter produced (otherwise every existing log reader and every before/after traffic
/// comparison quietly stops matching), and that the cache keys on the wrapped message type rather than
/// collapsing distinct messages into one bucket.
/// </remarks>
public class PacketProfilerNamingTests
{
    [ProtoContract(SkipConstructor = true)]
    private class ProbeMessageOne : IMessage
    {
        [ProtoMember(1)] public int Value { get; set; }
    }

    [ProtoContract(SkipConstructor = true)]
    private class ProbeMessageTwo : IMessage
    {
        [ProtoMember(1)] public int Value { get; set; }
    }

    /// <summary>A packet that is not a <see cref="MessagePacket"/>, like the mesh's movement packets.</summary>
    private readonly struct PlainProbePacket : IPacket
    {
        public DeliveryMethod DeliveryMethod => DeliveryMethod.Unreliable;
        public PacketType PacketType => PacketType.Message;
    }

    private static MessagePacket Wrap(IMessage message) =>
        MessagePacket.Create(message, new ProtoBufSerializer(new SerializableTypeMapper()));

    [Fact]
    public void A_message_packet_is_named_by_the_message_it_wraps_not_one_opaque_bucket()
    {
        var name = PacketProfiler.GetPacketNameForTest(Wrap(new ProbeMessageOne()));

        Assert.Equal($"{nameof(MessagePacket)}:{nameof(ProbeMessageOne)}", name);
    }

    [Fact]
    public void Two_message_types_never_collapse_into_the_same_bucket()
    {
        var first = PacketProfiler.GetPacketNameForTest(Wrap(new ProbeMessageOne()));
        var second = PacketProfiler.GetPacketNameForTest(Wrap(new ProbeMessageTwo()));

        // The cache keys on (packet type, message type). Keying on packet type alone would make every
        // message on the mesh report as one line, which is exactly the blindness the profiler exists to end.
        Assert.NotEqual(first, second);
        Assert.EndsWith(nameof(ProbeMessageOne), first);
        Assert.EndsWith(nameof(ProbeMessageTwo), second);
    }

    [Fact]
    public void Repeated_naming_of_one_message_type_returns_the_identical_cached_instance()
    {
        var first = PacketProfiler.GetPacketNameForTest(Wrap(new ProbeMessageOne()));
        var second = PacketProfiler.GetPacketNameForTest(Wrap(new ProbeMessageOne()));

        // Reference equality, not just value equality: this is what proves the per-packet string
        // allocation is actually gone from the hot path rather than merely producing equal text.
        Assert.Same(first, second);
    }

    [Fact]
    public void A_non_message_packet_is_named_by_its_own_type()
    {
        var name = PacketProfiler.GetPacketNameForTest(new PlainProbePacket());

        Assert.Equal(nameof(PlainProbePacket), name);
    }

    [Fact]
    public void A_message_packet_without_a_captured_message_type_falls_back_to_the_packet_name()
    {
        // MessageType is deliberately not serialized, so a packet that arrived over the wire has none.
        // Naming must not throw on it — a received packet reaching a profiler would otherwise crash the
        // send path it is measuring.
        var received = default(MessagePacket);

        var name = PacketProfiler.GetPacketNameForTest(received);

        Assert.Equal(nameof(MessagePacket), name);
    }
}
