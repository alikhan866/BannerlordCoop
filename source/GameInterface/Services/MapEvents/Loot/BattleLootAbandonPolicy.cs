using System.Collections.Generic;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>Why an offer stopped being answerable.</summary>
public enum BattleLootAbandonReason
{
    /// <summary>The player's connection dropped, or their game died, while the offer was open.</summary>
    Disconnected = 0,

    /// <summary>The player closed the encounter without answering - they chose to walk away.</summary>
    LeftDeliberately = 1,

    /// <summary>Nobody answered before the offer expired.</summary>
    TimedOut = 2,

    /// <summary>
    /// The encounter closed before the player was ever shown the spoils.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="LeftDeliberately"/> on purpose, and the distinction is the whole point: both
    /// end with an unanswered offer and full staged rosters, and treating them alike is what silently cost a
    /// won siege its entire spoils - 191 item stacks, 47 members and 52 prisoners, reported to the server as
    /// "claimed none of it".
    /// </remarks>
    NeverShown = 3,
}

/// <summary>What to do with spoils nobody answered for.</summary>
public enum BattleLootAbandonOutcome
{
    /// <summary>Hand over everything that was offered.</summary>
    TakeAll = 0,

    /// <summary>Hand over nothing, as vanilla does with loot left on the screen.</summary>
    Discard = 1,
}

/// <summary>
/// Decides the fate of an offer that was never answered.
/// </summary>
/// <remarks>
/// This is a gameplay judgement, not a technical one, so it lives in one place with the reasoning attached
/// rather than being spread across the handlers that hit each case.
///
/// A DISCONNECT pays out in full. A crash, a dropped connection or a closed laptop is not a decision the
/// player made, and taking a whole battle's spoils away for it is a punishment for an accident. It also
/// matches what the mod did before this feature existed, so nobody loses anything they used to get.
///
/// A DELIBERATE LEAVE discards, because that is exactly what vanilla does: loot left on the screen when you
/// close it is gone. Paying it out anyway would make the loot screen pointless - there would be no way to
/// decline anything.
///
/// A TIMEOUT also discards, and this is the uncomfortable one. Paying out on timeout would make "walk away
/// and wait" strictly better than answering, and a player could farm the difference. Discarding risks
/// costing someone who genuinely idled. The expiry is long (ten minutes) precisely so that reaching it means
/// something other than reading a screen.
/// </remarks>
public static class BattleLootAbandonPolicy
{
    public static BattleLootAbandonOutcome Decide(BattleLootAbandonReason reason)
    {
        switch (reason)
        {
            case BattleLootAbandonReason.Disconnected:

            // Same reasoning as a disconnect: a player who was never asked has not declined anything, and
            // charging them a whole battle's spoils for a screen that failed to open is a punishment for our
            // bug. It pays out through the ordinary transaction, so there is still exactly one authority for
            // moving loot - and it is loud in the log, so it can never be mistaken for the screens working.
            case BattleLootAbandonReason.NeverShown:
                return BattleLootAbandonOutcome.TakeAll;

            case BattleLootAbandonReason.LeftDeliberately:
            case BattleLootAbandonReason.TimedOut:
            default:
                return BattleLootAbandonOutcome.Discard;
        }
    }

    /// <summary>
    /// The answer to apply on the player's behalf when an offer is abandoned.
    /// </summary>
    /// <remarks>
    /// Built as a normal result so the abandonment path goes through exactly the same validation, planning
    /// and application as a real answer. A separate "just give them everything" route would be a second way
    /// to move loot, and the second way is always the one that skips the hero handling and corrupts a save.
    /// </remarks>
    public static BattleLootResult AnswerFor(BattleLootOffer offer, BattleLootAbandonReason reason)
    {
        if (Decide(reason) == BattleLootAbandonOutcome.Discard)
            return new BattleLootResult(offer.OfferId, new List<BattleLootClaim>());

        // Take-all is "nothing remains on the staged rosters", which is the same shape a player produces by
        // clicking take-all themselves - so it reuses the same differ rather than inventing a second one.
        var lines = offer.Lines ?? new BattleLootOfferLine[0];
        var nothingLeft = new int[lines.Length];

        return BattleLootSelection.FromRemaining(offer, nothingLeft);
    }
}
