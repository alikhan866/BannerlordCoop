using Common.Messaging;
using GameInterface.Services.MapEvents.Loot;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Loot;

/// <summary>
/// Server to client: here is what you won, choose what to take.
/// </summary>
/// <remarks>
/// The offer travels whole so the client's screens have something real to show, but the client is never
/// trusted to describe it back - <see cref="NetworkBattleLootResult"/> answers with line indices only.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
public record NetworkBattleLootOffer : IEvent
{
    [ProtoMember(1)]
    public BattleLootOffer Offer { get; }

    public NetworkBattleLootOffer(BattleLootOffer offer)
    {
        Offer = offer;
    }
}

/// <summary>
/// Client to server: this is what I took, and what happens to the prisoners.
/// </summary>
/// <remarks>
/// Carries the party id as well as the offer id. The offer already records which party it belongs to, so the
/// server can refuse a result that answers someone else's offer rather than assuming the sender is entitled
/// to it.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
public record NetworkBattleLootResult : IEvent
{
    [ProtoMember(1)]
    public string PartyId { get; }

    [ProtoMember(2)]
    public BattleLootResult Result { get; }

    public NetworkBattleLootResult(string partyId, BattleLootResult result)
    {
        PartyId = partyId;
        Result = result;
    }
}

/// <summary>
/// Server to client: the authoritative outcome, as ABSOLUTE state rather than a delta.
/// </summary>
/// <remarks>
/// Absolute on purpose. The client's own loot screen has already moved its local rosters, so echoing "add
/// these" would credit everything twice - the same trap the relation sync avoids by setting a value instead
/// of applying a change. Sending what the rosters should now BE makes the echo idempotent, and it is also the
/// only way to report an outcome that differs from what the player asked for, which happens whenever the
/// server clamps to party or prisoner capacity.
///
/// The roster payloads are filled in by M3, which is what actually applies a result.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
public record NetworkBattleLootApplied : IEvent
{
    [ProtoMember(1)]
    public string OfferId { get; }

    [ProtoMember(2)]
    public string PartyId { get; }

    /// <summary>True when the server applied something; false when the result was refused.</summary>
    [ProtoMember(3)]
    public bool Applied { get; }

    /// <summary>Why it was refused, for the log on the receiving side. Empty when applied.</summary>
    [ProtoMember(4)]
    public string Refusal { get; }

    /// <summary>
    /// What the party's rosters ARE now. The client replaces rather than adds.
    /// </summary>
    /// <remarks>
    /// Sent on a refusal too. A client that was told "no" has still moved its own rosters on its loot screen,
    /// and the only way to put it back is to state the truth rather than describe a change that never happened.
    /// </remarks>
    [ProtoMember(5)]
    public BattleLootRosterSnapshot Snapshot { get; }

    public NetworkBattleLootApplied(
        string offerId,
        string partyId,
        bool applied,
        string refusal,
        BattleLootRosterSnapshot snapshot)
    {
        OfferId = offerId;
        PartyId = partyId;
        Applied = applied;
        Refusal = refusal;
        Snapshot = snapshot;
    }
}
