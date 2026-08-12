using ProtoBuf;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>What a single offer line is made of.</summary>
public enum BattleLootLineKind
{
    Item = 0,
    Member = 1,
    Prisoner = 2,
}

/// <summary>
/// One indexed line of spoils the server is willing to hand a player.
/// </summary>
/// <remarks>
/// The index of this line inside <see cref="BattleLootOffer.Lines"/> is the only thing a client ever sends
/// back. That is deliberate. If a result named what it was taking - "five swords" - the server could not tell
/// a plain sword from a lordly one without re-deriving the whole offer and comparing item modifiers, troop xp
/// and wounded counts; get that comparison slightly wrong and a client can upgrade its own loot. Answering
/// with a line index instead reduces the entire trust question to "is this index in range, and is the amount
/// no more than the line holds".
/// </remarks>
[ProtoContract(SkipConstructor = true)]
public readonly struct BattleLootOfferLine
{
    [ProtoMember(1)]
    public readonly BattleLootLineKind Kind;

    /// <summary>Item id for <see cref="BattleLootLineKind.Item"/>, character id otherwise.</summary>
    [ProtoMember(2)]
    public readonly string ObjectId;

    /// <summary>Item modifier id, or null. Part of the line's identity - a lordly sword is not a plain one.</summary>
    [ProtoMember(3)]
    public readonly string ModifierId;

    [ProtoMember(4)]
    public readonly int Count;

    [ProtoMember(5)]
    public readonly int WoundedNumber;

    [ProtoMember(6)]
    public readonly int Xp;

    /// <summary>
    /// Whether this line is a hero rather than ordinary troops.
    /// </summary>
    /// <remarks>
    /// Carried on the line so the validator can enforce the one rule that makes heroes different without
    /// needing the campaign: a hero is one person, so a claim on a hero line can only ever be for one.
    /// </remarks>
    [ProtoMember(7)]
    public readonly bool IsHero;

    public BattleLootOfferLine(
        BattleLootLineKind kind,
        string objectId,
        string modifierId,
        int count,
        int woundedNumber = 0,
        int xp = 0,
        bool isHero = false)
    {
        Kind = kind;
        ObjectId = objectId;
        ModifierId = modifierId;
        Count = count;
        WoundedNumber = woundedNumber;
        Xp = xp;
        IsHero = isHero;
    }
}

/// <summary>
/// The complete set of spoils offered to one player for one battle, held by the server until it is answered.
/// </summary>
/// <remarks>
/// Keyed by <see cref="OfferId"/> rather than by map event alone: a player can be offered spoils from
/// consecutive battles - a siege runs assault after assault - and an answer to the previous one must never be
/// applied to the current one.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
public readonly struct BattleLootOffer
{
    [ProtoMember(1)]
    public readonly string OfferId;

    [ProtoMember(2)]
    public readonly string MapEventId;

    /// <summary>The party the spoils belong to, so a result from the wrong party can be refused.</summary>
    [ProtoMember(3)]
    public readonly string PartyId;

    [ProtoMember(4)]
    public readonly BattleLootOfferLine[] Lines;

    public BattleLootOffer(string offerId, string mapEventId, string partyId, IEnumerable<BattleLootOfferLine> lines)
    {
        OfferId = offerId;
        MapEventId = mapEventId;
        PartyId = partyId;
        Lines = lines?.ToArray() ?? new BattleLootOfferLine[0];
    }

    /// <summary>An offer with nothing in it. A battle can genuinely produce no loot and no prisoners.</summary>
    public bool IsEmpty => Lines == null || Lines.Length == 0;
}
