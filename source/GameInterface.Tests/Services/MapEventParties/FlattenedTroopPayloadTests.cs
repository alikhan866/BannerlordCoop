using GameInterface.Services.MapEventParties;
using ProtoBuf;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.MapEventParties;

/// <summary>
/// Proves the roster encoding is lossless, and that it actually buys what it exists to buy.
/// </summary>
/// <remarks>
/// <para>
/// Two properties matter and they are not the same property.
/// </para>
/// <para>
/// IT LOSES NOTHING. Troop spawning reads these rosters, so a field that does not survive the round trip is
/// a silent divergence between what each player sees fighting - far worse than the traffic this removes.
/// </para>
/// <para>
/// IT GETS UNDER 1200 BYTES. The saving is not really the bytes; it is that <c>ReliableMessageBatcher</c>
/// refuses to batch any message at or above its 1200-byte budget, so a 5 KB roster was sent alone and then
/// fragmented into several reliable-channel packets. Compressing it only helps if the result lands back
/// under that threshold, so that is asserted directly rather than inferred from a ratio.
/// </para>
/// <para>
/// A corrupt payload must THROW rather than decode to an empty roster: "this party is wiped out" is a
/// legitimate roster, so substituting it for something unreadable would apply a wrong-but-plausible state
/// to a live battle instead of leaving the previous one alone.
/// </para>
/// </remarks>
public class FlattenedTroopPayloadTests
{
    /// <summary>The batcher's budget - a message at or above this is sent alone and fragments.</summary>
    private const int BatchingBudgetBytes = 1200;

    /// <summary>
    /// What the payload has to leave room for, because the budget applies to the whole message.
    /// </summary>
    /// <remarks>
    /// <c>ReliableMessageBatcher</c> measures the SERIALISED MESSAGE, not this blob: the wire form also
    /// carries the map-event party id, protobuf framing for both fields, and the type-id envelope
    /// <c>ProtoBufSerializer</c> wraps every message in. A payload that only just squeaks under 1200 would
    /// therefore still be sent alone, so the assertions leave a deliberately generous allowance rather than
    /// testing a threshold the real message never sees.
    /// </remarks>
    private const int MessageFramingAllowanceBytes = 200;

    private static FlattenedTroop Troop(
        string id = "CharacterObject_1", bool isHero = false, int seed = 7,
        RosterTroopState state = RosterTroopState.Active, int xp = 0, int xpGained = 0)
        => new FlattenedTroop(id, isHero, seed, state, xp, xpGained);

    /// <summary>
    /// A roster shaped like a real one: many individual soldiers drawn from a few troop types.
    /// </summary>
    /// <remarks>
    /// The shape is the whole reason compression works here. A flattened roster carries one entry per
    /// SOLDIER, not per stack, so the same <c>ObjectId</c> string repeats for every man of a type - which is
    /// exactly what deflate collapses. A test built from unique ids would compress far worse than reality
    /// and would prove nothing about it.
    /// </remarks>
    private static FlattenedTroop[] RealisticRoster(int soldiers = 200)
    {
        string[] types =
        {
            "CharacterObject_imperial_recruit",
            "CharacterObject_imperial_infantryman",
            "CharacterObject_imperial_veteran_infantryman",
            "CharacterObject_imperial_trained_infantryman",
            "CharacterObject_imperial_archer",
        };

        return Enumerable.Range(0, soldiers)
            .Select(i => Troop(
                id: types[i % types.Length],
                seed: i,
                state: RosterTroopState.Active,
                xp: i * 3,
                xpGained: i % 17))
            .ToArray();
    }

    private static int RawSize(FlattenedTroop[] troops)
    {
        using var buffer = new MemoryStream();
        Serializer.Serialize(buffer, new FlattenedTroopBatch { Troops = troops });
        return (int)buffer.Length;
    }

    // ---- losing nothing ----------------------------------------------------

    [Fact]
    public void Every_field_of_every_troop_survives_the_round_trip()
    {
        var troops = new[]
        {
            Troop("CharacterObject_recruit", isHero: false, seed: 11,
                state: RosterTroopState.Active, xp: 250, xpGained: 40),
            Troop("Hero_1", isHero: true, seed: -3,
                state: RosterTroopState.WoundedInThisBattle, xp: 0, xpGained: 0),
            Troop("CharacterObject_archer", isHero: false, seed: int.MaxValue,
                state: RosterTroopState.Killed, xp: int.MaxValue, xpGained: int.MinValue),
        };

        FlattenedTroop[] restored = FlattenedTroopPayload.Decompress(
            FlattenedTroopPayload.Compress(troops));

        Assert.Equal(troops.Length, restored.Length);
        for (int i = 0; i < troops.Length; i++)
        {
            Assert.Equal(troops[i].ObjectId, restored[i].ObjectId);
            Assert.Equal(troops[i].IsHero, restored[i].IsHero);
            Assert.Equal(troops[i].UniqueSeed, restored[i].UniqueSeed);
            Assert.Equal(troops[i].State, restored[i].State);
            Assert.Equal(troops[i].Xp, restored[i].Xp);
            Assert.Equal(troops[i].XpGained, restored[i].XpGained);
        }
    }

    /// <summary>
    /// Order is part of the payload, not an incidental detail.
    /// </summary>
    /// <remarks>
    /// <c>RosterBroadcastGate</c> hashes the array in order and treats a reordering as a different snapshot,
    /// so an encoding that reordered would make the gate suppress against a hash the wire no longer carries.
    /// </remarks>
    [Fact]
    public void Troop_order_is_preserved()
    {
        var troops = Enumerable.Range(0, 50)
            .Select(i => Troop(id: "CharacterObject_" + i, seed: i))
            .ToArray();

        FlattenedTroop[] restored = FlattenedTroopPayload.Decompress(
            FlattenedTroopPayload.Compress(troops));

        Assert.Equal(
            troops.Select(t => t.ObjectId).ToArray(),
            restored.Select(t => t.ObjectId).ToArray());
    }

    [Fact]
    public void An_empty_roster_round_trips_as_empty()
    {
        FlattenedTroop[] restored = FlattenedTroopPayload.Decompress(
            FlattenedTroopPayload.Compress(Array.Empty<FlattenedTroop>()));

        Assert.Empty(restored);
    }

    /// <summary>
    /// A null roster is encoded as an empty one rather than rejected.
    /// </summary>
    /// <remarks>
    /// Both mean "this party has nobody", the receiver rebuilds an empty roster from either, and the
    /// producers already skip parties whose roster is genuinely absent. Throwing here would turn a harmless
    /// case into a lost update.
    /// </remarks>
    [Fact]
    public void A_null_roster_is_encoded_as_an_empty_one()
    {
        Assert.Empty(FlattenedTroopPayload.Decompress(FlattenedTroopPayload.Compress(null)));
    }

    // ---- getting under the batching budget ---------------------------------

    /// <summary>
    /// A roster the size of the ones actually measured on the wire lands under the batching budget.
    /// </summary>
    /// <remarks>
    /// The size is taken from the measurement, not chosen: live map-event rosters averaged 5,115 bytes,
    /// which at ~48 bytes a soldier is a party of about a hundred. That is the case the change has to win,
    /// and winning it means landing under 1200 - not merely getting smaller.
    /// </remarks>
    [Fact]
    public void A_typical_roster_compresses_below_the_batching_budget()
    {
        FlattenedTroop[] troops = RealisticRoster(100);

        int raw = RawSize(troops);
        int compressed = FlattenedTroopPayload.Compress(troops).Length;

        Assert.True(
            raw > BatchingBudgetBytes * 3,
            $"this must model a roster that was far over the budget to begin with, was {raw} bytes");
        Assert.True(
            compressed + MessageFramingAllowanceBytes < BatchingBudgetBytes,
            $"expected the payload to land under the {BatchingBudgetBytes}-byte batching budget with " +
            $"{MessageFramingAllowanceBytes} bytes to spare for message framing, " +
            $"was {compressed} bytes (from {raw})");
    }

    /// <summary>
    /// The saving has to hold for a big party, not just an average one.
    /// </summary>
    /// <remarks>
    /// A full 1000-strong roster will NOT fit under the budget and is not expected to - what does not
    /// compress is the part that is genuinely unique per soldier, its seed and its XP. What matters is that
    /// it still shrinks several fold, turning a message that spanned about forty reliable-channel fragments
    /// into one that spans about seven. Measured at 49,107 -> 7,660 bytes; the bar is set below that so a
    /// change in protobuf or deflate output does not fail the build for a rounding difference.
    /// </remarks>
    [Fact]
    public void A_very_large_roster_still_shrinks_several_fold()
    {
        FlattenedTroop[] troops = RealisticRoster(1000);

        int raw = RawSize(troops);
        int compressed = FlattenedTroopPayload.Compress(troops).Length;

        Assert.True(
            compressed * 5 < raw,
            $"expected at least a fivefold reduction, was {raw} -> {compressed} bytes");
    }

    // ---- refusing to guess -------------------------------------------------

    [Fact]
    public void A_null_payload_is_rejected()
    {
        Assert.Throws<SerializationException>(() => FlattenedTroopPayload.Decompress(null));
    }

    [Fact]
    public void A_payload_shorter_than_its_header_is_rejected()
    {
        Assert.Throws<SerializationException>(() => FlattenedTroopPayload.Decompress(new byte[7]));
    }

    [Fact]
    public void A_payload_without_the_magic_is_rejected()
    {
        byte[] payload = FlattenedTroopPayload.Compress(RealisticRoster(10));
        payload[0] ^= 0xFF;

        Assert.Throws<SerializationException>(() => FlattenedTroopPayload.Decompress(payload));
    }

    [Fact]
    public void A_corrupt_body_is_rejected_rather_than_decoded()
    {
        byte[] payload = FlattenedTroopPayload.Compress(RealisticRoster(50));
        for (int i = 8; i < payload.Length; i++) payload[i] ^= 0x5A;

        Assert.Throws<SerializationException>(() => FlattenedTroopPayload.Decompress(payload));
    }

    [Fact]
    public void A_truncated_payload_is_rejected()
    {
        byte[] payload = FlattenedTroopPayload.Compress(RealisticRoster(50));
        byte[] truncated = payload.Take(payload.Length / 2).ToArray();

        Assert.Throws<SerializationException>(() => FlattenedTroopPayload.Decompress(truncated));
    }

    /// <summary>
    /// An oversized declared length is refused BEFORE anything is decompressed.
    /// </summary>
    /// <remarks>
    /// This is the decompression-bomb guard: a few hundred bytes of deflate can declare and expand to
    /// gigabytes, so the ceiling has to be enforced from the header, not from the result.
    /// </remarks>
    [Fact]
    public void A_payload_declaring_more_than_the_ceiling_is_rejected()
    {
        byte[] payload = Handcrafted(
            declaredLength: FlattenedTroopPayload.MaxUncompressedBytes + 1,
            body: new byte[] { 1, 2, 3, 4 });

        var error = Assert.Throws<SerializationException>(
            () => FlattenedTroopPayload.Decompress(payload));
        Assert.Contains("outside the allowed range", error.Message);
    }

    [Fact]
    public void A_payload_declaring_a_negative_length_is_rejected()
    {
        byte[] payload = Handcrafted(declaredLength: -1, body: new byte[] { 1, 2, 3, 4 });

        Assert.Throws<SerializationException>(() => FlattenedTroopPayload.Decompress(payload));
    }

    /// <summary>
    /// A body that keeps expanding past the length it declared is refused.
    /// </summary>
    /// <remarks>
    /// Without this the ceiling could be walked straight past: declare one byte, expand to gigabytes, and
    /// only the first byte is ever checked. Treating the declared length as exact closes that.
    /// </remarks>
    [Fact]
    public void A_payload_expanding_past_its_declared_length_is_rejected()
    {
        byte[] real = new byte[4096];
        byte[] payload = Handcrafted(declaredLength: 8, body: Deflate(real));

        var error = Assert.Throws<SerializationException>(
            () => FlattenedTroopPayload.Decompress(payload));
        Assert.Contains("expanded past its declared length", error.Message);
    }

    private static byte[] Handcrafted(int declaredLength, byte[] body)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { (byte)'F', (byte)'T', (byte)'R', 1 }, 0, 4);
        output.WriteByte((byte)declaredLength);
        output.WriteByte((byte)(declaredLength >> 8));
        output.WriteByte((byte)(declaredLength >> 16));
        output.WriteByte((byte)(declaredLength >> 24));
        output.Write(body, 0, body.Length);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var compressor = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }
}
