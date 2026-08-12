using ProtoBuf;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>What the player chose to do with a hero on a prisoner line.</summary>
/// <remarks>
/// Only meaningful for a hero prisoner. Releasing one is not "declining to take it" - it is an action with
/// consequences the server owns (captivity ends, relation changes, the lord reappears somewhere), which is
/// why it travels as an explicit choice rather than as an absence.
/// </remarks>
public enum BattleLootDisposition
{
    Keep = 0,
    Release = 1,
}

/// <summary>A claim against one line of the offer, by index.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct BattleLootClaim
{
    [ProtoMember(1)]
    public readonly int LineIndex;

    [ProtoMember(2)]
    public readonly int Count;

    [ProtoMember(3)]
    public readonly BattleLootDisposition Disposition;

    public BattleLootClaim(int lineIndex, int count, BattleLootDisposition disposition = BattleLootDisposition.Keep)
    {
        LineIndex = lineIndex;
        Count = count;
        Disposition = disposition;
    }
}

/// <summary>
/// A player's answer to an offer: which lines they took, how much of each, and what happens to hero prisoners.
/// </summary>
/// <remarks>
/// A line the player left alone is simply absent. That matches vanilla, where loot left on the screen is
/// discarded rather than followed up - so silence means "not taken", and no client can express "give me the
/// rest later".
/// </remarks>
[ProtoContract(SkipConstructor = true)]
public readonly struct BattleLootResult
{
    [ProtoMember(1)]
    public readonly string OfferId;

    [ProtoMember(2)]
    public readonly BattleLootClaim[] Claims;

    public BattleLootResult(string offerId, IEnumerable<BattleLootClaim> claims)
    {
        OfferId = offerId;
        Claims = claims?.ToArray() ?? new BattleLootClaim[0];
    }
}

/// <summary>Why a result was refused. <see cref="None"/> means it was accepted.</summary>
public enum BattleLootRejection
{
    None = 0,
    OfferIdMismatch,
    UnknownLineIndex,
    DuplicateLineIndex,
    NonPositiveCount,
    ExceedsOfferedCount,
    HeroCountMustBeOne,
    DispositionOnNonPrisonerLine,
}

/// <summary>A single validated claim, resolved against the line it referenced.</summary>
public readonly struct BattleLootResolvedClaim
{
    public readonly BattleLootOfferLine Line;
    public readonly int Count;
    public readonly BattleLootDisposition Disposition;

    public BattleLootResolvedClaim(BattleLootOfferLine line, int count, BattleLootDisposition disposition)
    {
        Line = line;
        Count = count;
        Disposition = disposition;
    }
}
