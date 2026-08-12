using System;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>
/// The offer this client has been sent and not yet answered.
/// </summary>
/// <remarks>
/// Static for the same reason <c>PendingBattleLoot</c> next door is: the pieces that need it are a network
/// handler, a Harmony prefix on PlayerEncounter.Finish and the finalize handler, and none of them can hold a
/// reference to the others. There is exactly one local player, so exactly one outstanding offer.
///
/// Keyed by map event id as well as offer id so the finalize handler can ask "is THIS battle still waiting on
/// me" rather than "is anything waiting". A siege starts its next assault immediately, and an answer owed for
/// the last wave must never hold up being seated in the next one.
/// </remarks>
public static class ClientBattleLootOffer
{
    private static readonly object Gate = new object();

    private static BattleLootOffer offer;
    private static bool present;
    private static bool shown;

    public static bool HasPending
    {
        get { lock (Gate) return present; }
    }

    /// <summary>
    /// Whether the player has actually been shown any of these spoils yet.
    /// </summary>
    /// <remarks>
    /// The answer to an offer is read from what is LEFT on the staged rosters, which says "declined" and
    /// "never asked" in exactly the same words. Without this flag the two are indistinguishable, and a battle
    /// whose screens never opened is reported to the server as a player who wanted none of it.
    /// </remarks>
    public static bool WasShown
    {
        get { lock (Gate) return shown; }
    }

    /// <summary>Called when a step of the post-battle walk actually puts something in front of the player.</summary>
    public static void MarkShown()
    {
        lock (Gate) shown = true;
    }

    /// <summary>Records the offer the server just sent. Replaces any earlier unanswered one.</summary>
    public static void Remember(BattleLootOffer value)
    {
        lock (Gate)
        {
            offer = value;
            present = true;
            shown = false;
        }
    }

    /// <summary>Whether the outstanding offer belongs to this battle.</summary>
    public static bool IsFor(string mapEventId)
    {
        if (string.IsNullOrEmpty(mapEventId)) return false;

        lock (Gate)
        {
            return present && string.Equals(offer.MapEventId, mapEventId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Takes the offer so it can be answered, leaving nothing behind.
    /// </summary>
    /// <remarks>
    /// One-way, like the server's registry: the answer is sent once, and a second attempt - a second
    /// PlayerEncounter.Finish, a retry - must find nothing rather than send a duplicate the server would then
    /// have to refuse.
    /// </remarks>
    public static bool TryTake(out BattleLootOffer value)
    {
        lock (Gate)
        {
            value = offer;
            bool had = present;
            offer = default;
            present = false;
            shown = false;
            return had;
        }
    }

    /// <summary>Forgets the offer without answering it.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            offer = default;
            present = false;
        }
    }
}
