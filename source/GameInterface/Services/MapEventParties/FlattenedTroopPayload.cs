using ProtoBuf;
using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Threading;

namespace GameInterface.Services.MapEventParties;

/// <summary>
/// Carries a map-event party's flattened roster on the wire as a single compressed blob.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE ROSTER IS COMPRESSED AND OTHER MESSAGES ARE NOT.
/// </para>
/// <para>
/// <c>ReliableMessageBatcher</c> packs small messages into shared 1200-byte datagrams, but it explicitly
/// refuses anything that size or larger: such a message is sent on its own and is then fragmented by the
/// transport. A map-event roster serialised to about 5.1 KB, so every one of them bypassed batching and
/// became roughly five reliable-channel packets. Measured on a live siege that was 284 roster messages per
/// ten seconds - some 1,400 datagrams, against roughly 26 for the ~900 small autosync messages in the same
/// window, which batched normally. The reliable queue is bounded in PACKETS, so the rosters were producing
/// about fifty times the queue pressure of traffic that outnumbered them three to one.
/// </para>
/// <para>
/// A roster is an unusually compressible payload: it holds one entry per individual soldier, and every
/// soldier of a type repeats the same <c>ObjectId</c> string - which is most of its bulk, at roughly 48
/// bytes a soldier before compression and about 8 after. Measured on realistic data that is an 84%
/// reduction, so the average 5.1 KB roster lands near 800 bytes: back under the batching budget, sharing
/// datagrams with other rosters instead of claiming five of its own.
/// </para>
/// <para>
/// It is not a fixed win for every party. What does not compress is the part that is genuinely unique per
/// soldier - the roster seed and the accumulated XP - so a very large party stays over the budget: a
/// thousand-strong roster goes from 49 KB to about 7.7 KB, still six times smaller and six times fewer
/// fragments, but not batchable. Deflate runs at Fastest for a related reason: measured against Optimal it
/// gives up 6-8% of the ratio for several times less CPU on the server, and 8% would not move a single
/// roster across the threshold that actually matters.
/// </para>
/// <para>
/// WHAT IT COSTS. Measured in Release on synthetic worst-case data (every soldier a unique seed and XP;
/// live rosters compress better - a real 5000-man party measured 94.8%):
///
///     100 troops    4,781 ->    848 B   compress 0.157 ms   inflate+parse 0.121 ms  (parse alone was 0.041)
///   1,000 troops   49,107 ->  7,660 B   compress 0.825 ms   inflate+parse 0.806 ms  (parse alone was 0.601)
///   5,000 troops  246,235 -> 38,647 B   compress 3.759 ms   inflate+parse 4.679 ms  (parse alone was 3.063)
///
/// The client pays only the difference against a parse it was already doing, and pays it on the network
/// thread - the caller inflates BEFORE entering GameThread.Run, so the frame thread's share is unchanged.
/// The server pays compression on its game thread, because the roster broadcast is driven from campaign
/// simulation. At a typical party that is ~0.16 ms against a message that was costing several datagrams;
/// at a five-thousand-man party it is a few milliseconds, which is free on a headless dedicated server and
/// would be worth revisiting for a listen-server host running battles that size.
/// </para>
/// <para>
/// WHAT THIS DELIBERATELY DOES NOT CHANGE. The receiver still replaces the roster wholesale from a complete
/// snapshot, so this is purely an encoding: no delta, no base state to reconcile against, and a re-sent
/// snapshot stays idempotent. <see cref="RosterBroadcastGate"/> also still hashes the UNCOMPRESSED
/// <see cref="FlattenedTroop"/> array, so its dedup never depends on deflate being byte-for-byte
/// deterministic across runtimes - the server is .NET 6 and a client may be running under Wine-Mono.
/// </para>
/// </remarks>
internal static class FlattenedTroopPayload
{
    /// <summary>
    /// Ceiling on what a payload may claim to expand to, checked BEFORE any decompression happens.
    /// </summary>
    /// <remarks>
    /// A deflate stream can declare a tiny compressed size and expand without bound, so the declared length
    /// is validated against this first and the actual result is required to match it exactly afterwards. The
    /// largest plausible roster - a thousand-strong party at roughly forty bytes a soldier - is about 40 KB,
    /// so this leaves a hundredfold headroom that still cannot be used to exhaust memory.
    /// </remarks>
    public const int MaxUncompressedBytes = 4 * 1024 * 1024;

    private static readonly byte[] PayloadMagic = { (byte)'F', (byte)'T', (byte)'R', 1 };
    private const int HeaderLength = 8;

    private static long totalRawBytes;
    private static long totalCompressedBytes;
    private static long totalPayloads;

    /// <summary>Cumulative uncompressed bytes handed to <see cref="Compress"/>, for the traffic report.</summary>
    public static long TotalRawBytes => Interlocked.Read(ref totalRawBytes);

    /// <summary>Cumulative bytes actually handed to the network, for the traffic report.</summary>
    public static long TotalCompressedBytes => Interlocked.Read(ref totalCompressedBytes);

    /// <summary>How many rosters those totals cover.</summary>
    public static long TotalPayloads => Interlocked.Read(ref totalPayloads);

    /// <summary>
    /// Encodes a roster for the wire. A null roster is encoded as an empty one.
    /// </summary>
    /// <remarks>
    /// Null and empty are folded together deliberately: both mean "this party has nobody left", the receiver
    /// rebuilds an empty roster from either, and keeping them distinct would only create a state the
    /// encoding has no way to express.
    /// </remarks>
    public static byte[] Compress(FlattenedTroop[] troops)
    {
        byte[] serialized = SerializeBatch(troops);

        using var output = new MemoryStream();
        output.Write(PayloadMagic, 0, PayloadMagic.Length);
        WriteInt32(output, serialized.Length);

        using (var compressor = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(serialized, 0, serialized.Length);
        }

        byte[] compressed = output.ToArray();

        Interlocked.Add(ref totalRawBytes, serialized.Length);
        Interlocked.Add(ref totalCompressedBytes, compressed.Length);
        Interlocked.Increment(ref totalPayloads);

        return compressed;
    }

    /// <summary>
    /// Decodes a roster produced by <see cref="Compress"/>.
    /// </summary>
    /// <remarks>
    /// Every rejection throws rather than returning an empty roster. An empty roster is a legitimate value
    /// meaning "this party is wiped out", so quietly substituting it for a payload that could not be read
    /// would apply a wrong-but-plausible state to a live battle. The caller wraps this in the try/catch that
    /// already guards applying the message, so a bad payload leaves the previous roster untouched and says
    /// so in the log.
    /// </remarks>
    public static FlattenedTroop[] Decompress(byte[] payload)
    {
        if (payload == null || payload.Length < HeaderLength || !HasPayloadMagic(payload))
            throw new SerializationException("Flattened troop payload header was missing or invalid");

        int declaredLength = ReadInt32(payload, PayloadMagic.Length);
        if (declaredLength < 0 || declaredLength > MaxUncompressedBytes)
            throw new SerializationException(
                $"Flattened troop payload declared {declaredLength} bytes, outside the allowed range");

        byte[] serialized;
        try
        {
            using var input = new MemoryStream(
                payload, HeaderLength, payload.Length - HeaderLength, writable: false);
            using var decompressor = new DeflateStream(input, CompressionMode.Decompress);

            serialized = ReadExactly(decompressor, declaredLength);

            // Treating the declared length as exact rather than as a hint is what makes the ceiling above
            // meaningful: a stream that keeps expanding past what it declared is the shape a decompression
            // bomb takes, and one more byte is enough to catch it in the same pass.
            if (decompressor.ReadByte() >= 0)
                throw new SerializationException(
                    "Flattened troop payload expanded past its declared length");
        }
        catch (InvalidDataException e)
        {
            throw new SerializationException("Flattened troop payload could not be decompressed", e);
        }

        return DeserializeBatch(serialized);
    }

    private static byte[] SerializeBatch(FlattenedTroop[] troops)
    {
        using var buffer = new MemoryStream();
        Serializer.Serialize(buffer, new FlattenedTroopBatch
        {
            Troops = troops ?? Array.Empty<FlattenedTroop>(),
        });
        return buffer.ToArray();
    }

    private static FlattenedTroop[] DeserializeBatch(byte[] serialized)
    {
        using var buffer = new MemoryStream(serialized, writable: false);
        FlattenedTroopBatch batch = Serializer.Deserialize<FlattenedTroopBatch>(buffer);

        // protobuf-net writes nothing at all for an empty repeated field, so an empty roster round-trips as
        // a zero-length body and comes back with the member left null.
        return batch?.Troops ?? Array.Empty<FlattenedTroop>();
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var result = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(result, offset, count - offset);
            if (read <= 0)
                throw new SerializationException(
                    "Flattened troop payload ended before its declared length");
            offset += read;
        }

        return result;
    }

    private static bool HasPayloadMagic(byte[] data)
    {
        for (int i = 0; i < PayloadMagic.Length; i++)
            if (data[i] != PayloadMagic[i]) return false;

        return true;
    }

    private static void WriteInt32(Stream stream, int value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 24));
    }

    private static int ReadInt32(byte[] data, int offset) =>
        data[offset]
        | (data[offset + 1] << 8)
        | (data[offset + 2] << 16)
        | (data[offset + 3] << 24);
}

/// <summary>
/// Protobuf root for a roster blob.
/// </summary>
/// <remarks>
/// A class rather than a struct, and a named contract rather than serialising the array at the root: the
/// runtime model applies a value-type construction workaround under Wine-Mono
/// (<c>ProtoBufSerializer.ConfigureRuntimeModel</c>), and a plain reference type with a parameterless
/// constructor stays clear of it on every runtime this assembly is loaded into.
/// </remarks>
[ProtoContract]
internal class FlattenedTroopBatch
{
    [ProtoMember(1)]
    public FlattenedTroop[] Troops { get; set; }
}
