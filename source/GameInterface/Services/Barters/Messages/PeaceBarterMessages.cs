using Common.Messaging;
using ProtoBuf;
using System;

namespace GameInterface.Services.Barters.Messages;

internal enum PeaceBarterTermType
{
    Gold,
    Item,
    Fief,
    TransferPrisoner,
    ReleasePrisoner,
}

internal enum PeaceConversationContext
{
    MapParty,
    Location,

    /// <summary>
    /// A conversation held inside a settlement without a location mission - the settlement menu.
    /// </summary>
    /// <remarks>
    /// Talking to a lord from a castle or town menu creates no agent interaction and no
    /// CampaignMission.Current.Location, so neither the map-party hold nor the location lock is ever
    /// acquired and every barter from such a conversation was refused. There is nothing to lock here;
    /// the server instead verifies both parties are in the settlement it was told about.
    /// Appended, never reordered: the value travels as an int on the wire.
    /// </remarks>
    Settlement,

    /// <summary>
    /// A conversation with a lord the requesting player is holding PRISONER.
    /// </summary>
    /// <remarks>
    /// None of the other three describe this, and the fallback order made that a silent rejection. A
    /// prisoner's <c>OtherParty</c> is his CAPTOR's party - the requesting player's own - and that party is
    /// active, so the request went out as <see cref="MapParty"/>. The server then looked for a map engagement
    /// between two parties, which talking to your own captive never creates, and refused with "The lord
    /// conversation is no longer active." Measured live: five consecutive rejections against Hero_lord_1_54
    /// while he sat in the requester's own party.
    ///
    /// Authority here is CUSTODY rather than a hold or co-location. If the target is your prisoner you are
    /// necessarily the one who can speak to him, and no other player can - which is a stronger claim than the
    /// settlement case gets from co-location, where several players may stand in the same town.
    ///
    /// Appended, never reordered: the value travels as an int on the wire.
    /// </remarks>
    Prisoner,
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct PeaceBarterTerm
{
    [ProtoMember(1)]
    public readonly int Type;
    [ProtoMember(2)]
    public readonly string OwnerHeroId;
    [ProtoMember(3)]
    public readonly string ObjectId;
    [ProtoMember(4)]
    public readonly string ItemModifierId;
    [ProtoMember(5)]
    public readonly bool ItemModifierNull;
    [ProtoMember(6)]
    public readonly int Amount;

    public PeaceBarterTerm(
        PeaceBarterTermType type,
        string ownerHeroId,
        string objectId,
        string itemModifierId,
        bool itemModifierNull,
        int amount)
    {
        Type = (int)type;
        OwnerHeroId = ownerHeroId;
        ObjectId = objectId;
        ItemModifierId = itemModifierId;
        ItemModifierNull = itemModifierNull;
        Amount = amount;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkRequestPeaceBarter : ICommand
{
    [ProtoMember(1)]
    public readonly string TargetHeroId;
    [ProtoMember(2)]
    public readonly string ContextId;
    [ProtoMember(3)]
    public readonly PeaceBarterTerm[] Terms;
    [ProtoMember(4)]
    public readonly int Context;
    [ProtoMember(5)]
    public readonly string RequestId;

    public NetworkRequestPeaceBarter(
        string targetHeroId,
        PeaceConversationContext context,
        string contextId,
        PeaceBarterTerm[] terms,
        string requestId = null)
    {
        TargetHeroId = targetHeroId;
        ContextId = contextId;
        Terms = terms ?? Array.Empty<PeaceBarterTerm>();
        Context = (int)context;
        RequestId = requestId;
    }
}

[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkPeaceBarterResult : ICommand
{
    public const string InactiveEncounterReason = "The peace encounter is no longer active.";

    [ProtoMember(1)]
    public readonly string ContextId;
    [ProtoMember(2)]
    public readonly bool Accepted;
    [ProtoMember(3)]
    public readonly int PlayerGold;
    [ProtoMember(4)]
    public readonly string Reason;
    [ProtoMember(5)]
    public readonly string RequestId;

    public NetworkPeaceBarterResult(
        string contextId,
        bool accepted,
        int playerGold,
        string reason = null,
        string requestId = null)
    {
        ContextId = contextId;
        Accepted = accepted;
        PlayerGold = playerGold;
        Reason = reason;
        RequestId = requestId;
    }
}
