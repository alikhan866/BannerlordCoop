using GameInterface.Surrogates;
using Missions.Agents.Packets;
using ProtoBuf;
using ProtoBuf.Meta;
using System;
using System.IO;
using TaleWorlds.Library;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Holds the movement packet's size down, because size is the entire reason this encoding exists.
/// </summary>
/// <remarks>
/// <para>
/// Movement is 97% of the mission mesh by bytes - measured live at 7.3 MB in ten seconds against 58 KB for
/// everything else combined - and it is sent per agent, per update, to every peer. A player who owns 638
/// agents transmits all of them, so a byte added here is a byte multiplied by tens of thousands per second.
/// </para>
/// <para>
/// The ceiling below is a REGRESSION GUARD, not a target. It is set above what the current encoding produces
/// so ordinary changes do not trip it, and low enough that quietly restoring a full-precision vector would.
/// Before quantisation an agent cost about 57 bytes: a Vec3 surrogate is 15 and a Vec2 is 11, so four
/// vectors and a float accounted for 52 of them.
/// With directions, input, speed AND position all packed it is 31, and a stationary agent 18 - so a
/// ceiling of 34 leaves room for ordinary change while a single restored full-precision Vec3, six bytes
/// more, would trip it.
/// </para>
/// </remarks>
public class AgentDataWireSizeTests
{
    private readonly ITestOutputHelper output;

    public AgentDataWireSizeTests(ITestOutputHelper output)
    {
        this.output = output;

        // Vec3/Vec2 have no built-in protobuf serializer; the mod supplies surrogates.
        _ = new SurrogateCollection();
    }

    private static int SerializedSize(AgentData data)
    {
        using var buffer = new MemoryStream();
        Serializer.Serialize(buffer, data);
        return (int)buffer.Length;
    }

    private static AgentData Typical() => new AgentData(
        position: new Vec3(742.31f, 533.87f, 41.2f),
        movementDirection: new Vec2(0.7071f, -0.7071f),
        lookDirection: new Vec3(0.577f, 0.577f, 0.577f),
        inputVector: new Vec2(0.35f, 0.9f),
        speed: 4.75f);

    /// <summary>
    /// Every movement contract can actually build a serializer.
    /// </summary>
    /// <remarks>
    /// This exists because of a real failure it would have caught immediately. Quantising
    /// <c>AgentMountData</c> reused field numbers 16-18, which were already taken further down the type,
    /// and protobuf-net only reports a duplicate when it BUILDS the serializer - not at compile time. The
    /// size tests above missed it entirely: they serialise an AgentData whose MountData is null, so the
    /// mount serializer was never built, and 25 tests elsewhere failed instead.
    ///
    /// GetSchema forces the model to be built for a type without needing an instance of it, which is what
    /// makes this cheap enough to cover every packet type rather than only the ones easy to construct.
    /// </remarks>
    [Theory]
    [InlineData(typeof(AgentData))]
    [InlineData(typeof(AgentMountData))]
    [InlineData(typeof(MovementPacket))]
    [InlineData(typeof(MountMovementPacket))]
    [InlineData(typeof(AgentEquipmentPacket))]
    public void The_contract_builds_without_duplicate_or_missing_field_numbers(Type contract)
    {
        // Throws on a duplicate field number, an unserializable member, or a missing surrogate.
        string schema = RuntimeTypeModel.Default.GetSchema(contract);

        Assert.False(string.IsNullOrWhiteSpace(schema));
    }

    [Fact]
    public void A_foot_agent_update_stays_small()
    {
        int size = SerializedSize(Typical());
        output.WriteLine($"foot agent update = {size} bytes");

        Assert.True(size <= 34, $"a foot agent update grew to {size} bytes");
    }

    /// <summary>
    /// The worst case - every component at its extreme - must not cost more than the typical one.
    /// </summary>
    /// <remarks>
    /// This is what <c>DataFormat.FixedSize</c> buys. Packed directions with negative components set their
    /// high bits, so under a varint the wire would cost more for an agent facing away from the origin than
    /// towards it, and a battle's bandwidth would depend on which way its soldiers happened to be looking.
    /// </remarks>
    [Fact]
    public void A_worst_case_direction_costs_no_more_than_a_typical_one()
    {
        int typical = SerializedSize(Typical());
        int worstCase = SerializedSize(new AgentData(
            position: new Vec3(-1000f, -1000f, -1000f),
            movementDirection: new Vec2(-1f, -1f),
            lookDirection: new Vec3(-1f, -1f, -1f),
            inputVector: new Vec2(-1f, -1f),
            speed: MovementQuantizer.MaximumSpeed));

        output.WriteLine($"typical = {typical} bytes, worst case = {worstCase} bytes");

        Assert.True(
            worstCase <= typical + 2,
            $"worst case {worstCase} exceeded typical {typical} by more than framing");
    }

    /// <summary>A still agent costs no more than a moving one.</summary>
    [Fact]
    public void A_stationary_agent_is_not_more_expensive()
    {
        int size = SerializedSize(new AgentData(
            position: new Vec3(742.31f, 533.87f, 41.2f),
            movementDirection: Vec2.Zero,
            lookDirection: new Vec3(0f, 1f, 0f),
            inputVector: Vec2.Zero,
            speed: 0f));

        output.WriteLine($"stationary agent update = {size} bytes");

        Assert.True(size <= 34, $"a stationary agent update grew to {size} bytes");
    }

    /// <summary>
    /// What the packet actually carries survives the trip, at the precision the encoding promises.
    /// </summary>
    /// <remarks>
    /// Serialisation is where a packing mistake shows up as a component landing in the wrong lane, so the
    /// round trip is checked through protobuf rather than only through the quantiser's own unit tests.
    /// </remarks>
    [Fact]
    public void The_values_survive_a_real_serialization_round_trip()
    {
        AgentData original = Typical();

        using var buffer = new MemoryStream();
        Serializer.Serialize(buffer, original);
        buffer.Position = 0;
        AgentData restored = Serializer.Deserialize<AgentData>(buffer);

        Assert.Equal(original.Position.x, restored.Position.x);
        Assert.Equal(original.Position.y, restored.Position.y);
        Assert.Equal(original.Position.z, restored.Position.z);

        Assert.Equal(original.LookDirection.x, restored.LookDirection.x, 4);
        Assert.Equal(original.LookDirection.y, restored.LookDirection.y, 4);
        Assert.Equal(original.LookDirection.z, restored.LookDirection.z, 4);
        Assert.Equal(original.MovementDirection.X, restored.MovementDirection.X, 4);
        Assert.Equal(original.MovementDirection.Y, restored.MovementDirection.Y, 4);
        Assert.Equal(original.InputVector.X, restored.InputVector.X, 4);
        Assert.Equal(original.InputVector.Y, restored.InputVector.Y, 4);
        Assert.Equal(original.Speed, restored.Speed, 3);
    }
}
