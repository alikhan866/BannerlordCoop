using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEventParties.Messages;

/// <summary>
/// A map-event party's complete roster, deflated.
/// </summary>
/// <remarks>
/// The payload is produced and read by <see cref="FlattenedTroopPayload"/>; see that type for why this one
/// message is compressed when nothing else is. It stays a whole snapshot rather than a delta, so applying it
/// is still an idempotent wholesale replace.
/// </remarks>
[ProtoContract]
internal readonly struct NetworkUpdateMapEventParty : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventPartyId;

    /// <summary>Deflated <see cref="FlattenedTroop"/> array - never the raw array.</summary>
    /// <remarks>
    /// Tag 3, not the tag the uncompressed array used. Client and server are pinned to the same build hash
    /// so the two encodings can never meet, but retiring the tag means a mismatched pair would fail to find
    /// the field rather than silently decode a length-delimited blob as a repeated message.
    /// </remarks>
    [ProtoMember(3)]
    public readonly byte[] CompressedTroops;

    public NetworkUpdateMapEventParty(string mapEventPartyId, byte[] compressedTroops)
    {
        MapEventPartyId = mapEventPartyId;
        CompressedTroops = compressedTroops;
    }
}
