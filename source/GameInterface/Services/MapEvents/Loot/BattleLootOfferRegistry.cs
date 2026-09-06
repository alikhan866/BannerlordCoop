using System.Collections.Generic;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>Why an incoming loot result was refused before it was even validated.</summary>
public enum BattleLootClaimRefusal
{
    None = 0,
    UnknownOffer,
    AlreadyResolved,
    Expired,
    WrongParty,
}

/// <summary>
/// The server's record of which spoils it has offered and to whom, and whether the offer is still open.
/// </summary>
/// <remarks>
/// The offer is the server's memory of what it was willing to give. Without it there is nothing to validate a
/// client's answer against - the client's own claim would be the only description of the loot, which is the
/// forgery hole this whole design exists to close.
///
/// Everything time-related is passed in rather than read from CampaignTime. That keeps expiry testable
/// without a running campaign, and it means the caller decides which clock matters - campaign time stops when
/// the world is paused, and an offer sitting on a player's loot screen must expire on wall clock, not on a
/// world that is frozen precisely because that player is staring at a menu.
///
/// Resolution is one-way and atomic: <see cref="TryClaim"/> hands the offer over and marks it resolved in the
/// same step, so a duplicated or replayed result finds it already gone. That matters because the transport is
/// reliable-ordered but the CLIENT is not - a retry after a hiccup would otherwise pay the same loot twice.
/// </remarks>
public sealed class BattleLootOfferRegistry
{
    private sealed class Entry
    {
        public BattleLootOffer Offer;
        public double ExpiresAtSeconds;
        public bool Resolved;
    }

    private readonly Dictionary<string, Entry> byOfferId = new Dictionary<string, Entry>();

    /// <summary>
    /// The server's one registry.
    /// </summary>
    /// <remarks>
    /// Static because the two ends of the transaction live in different handlers - the results handler builds
    /// an offer, the transaction handler answers it - and both are constructed independently by the container's
    /// IHandler scan, so neither can hold the other's instance. <c>PendingBattleLoot</c> next door solves the
    /// same problem the same way. It is server-only state; a client never registers anything here.
    /// </remarks>
    public static readonly BattleLootOfferRegistry Shared = new BattleLootOfferRegistry();

    /// <summary>How long an unanswered offer stays open, in seconds of wall clock.</summary>
    /// <remarks>
    /// Generous on purpose. This is a human reading a loot screen and deciding which prisoners to ransom, not
    /// a machine round trip; expiring under them would take the loot away mid-decision. What it protects
    /// against is the offer living forever when nobody is ever going to answer it.
    /// </remarks>
    public const double DefaultLifetimeSeconds = 600;

    public int OpenOfferCount
    {
        get
        {
            int open = 0;
            foreach (var entry in byOfferId.Values)
            {
                if (!entry.Resolved) open++;
            }

            return open;
        }
    }

    /// <summary>Records an offer as outstanding. Replaces any earlier offer with the same id.</summary>
    public void Register(BattleLootOffer offer, double nowSeconds, double lifetimeSeconds = DefaultLifetimeSeconds)
    {
        if (string.IsNullOrEmpty(offer.OfferId)) return;

        byOfferId[offer.OfferId] = new Entry
        {
            Offer = offer,
            ExpiresAtSeconds = nowSeconds + lifetimeSeconds,
            Resolved = false,
        };
    }

    /// <summary>
    /// Takes ownership of an outstanding offer so its result can be applied, exactly once.
    /// </summary>
    /// <param name="partyId">
    /// The party the answering player controls. Checked against the offer so one player cannot answer another's.
    /// </param>
    public bool TryClaim(
        string offerId,
        string partyId,
        double nowSeconds,
        out BattleLootOffer offer,
        out BattleLootClaimRefusal refusal)
    {
        offer = default;

        if (string.IsNullOrEmpty(offerId) || !byOfferId.TryGetValue(offerId, out var entry))
        {
            refusal = BattleLootClaimRefusal.UnknownOffer;
            return false;
        }

        // Checked before expiry so a replay is always reported as a replay. Reporting "expired" for something
        // that was in fact already paid out would send whoever reads the log looking for a timing bug.
        if (entry.Resolved)
        {
            refusal = BattleLootClaimRefusal.AlreadyResolved;
            return false;
        }

        if (!string.Equals(entry.Offer.PartyId, partyId, System.StringComparison.Ordinal))
        {
            refusal = BattleLootClaimRefusal.WrongParty;
            return false;
        }

        if (nowSeconds >= entry.ExpiresAtSeconds)
        {
            refusal = BattleLootClaimRefusal.Expired;
            return false;
        }

        entry.Resolved = true;
        offer = entry.Offer;
        refusal = BattleLootClaimRefusal.None;
        return true;
    }

    /// <summary>Whether an offer is still open and unexpired.</summary>
    public bool IsOutstanding(string offerId, double nowSeconds)
        => !string.IsNullOrEmpty(offerId)
           && byOfferId.TryGetValue(offerId, out var entry)
           && !entry.Resolved
           && nowSeconds < entry.ExpiresAtSeconds;

    /// <summary>Every open, unexpired offer for a party - used to answer "does this player still owe a screen".</summary>
    public IReadOnlyList<BattleLootOffer> OutstandingFor(string partyId, double nowSeconds)
    {
        var open = new List<BattleLootOffer>();
        foreach (var entry in byOfferId.Values)
        {
            if (entry.Resolved) continue;
            if (nowSeconds >= entry.ExpiresAtSeconds) continue;
            if (!string.Equals(entry.Offer.PartyId, partyId, System.StringComparison.Ordinal)) continue;

            open.Add(entry.Offer);
        }

        return open;
    }

    /// <summary>
    /// Every unexpired offer made to a party, answered or not.
    /// </summary>
    /// <remarks>
    /// "What was this party offered recently", which is what bounds the troop XP it may claim for donating loot
    /// (<c>TradeHandler.CapDonationXp</c>). Answering an offer must not shrink that bound: the loot answer and the
    /// inventory screen's Done are two messages and either can arrive first.
    /// </remarks>
    public IReadOnlyList<BattleLootOffer> RecentFor(string partyId, double nowSeconds)
    {
        var recent = new List<BattleLootOffer>();
        foreach (var entry in byOfferId.Values)
        {
            if (nowSeconds >= entry.ExpiresAtSeconds) continue;
            if (!string.Equals(entry.Offer.PartyId, partyId, System.StringComparison.Ordinal)) continue;
            recent.Add(entry.Offer);
        }
        return recent;
    }

    /// <summary>
    /// Whether anything is still waiting on an answer.
    /// </summary>
    /// <remarks>
    /// The hook an autosave interlock needs: spoils that exist in neither the save nor a roster are lost on
    /// reload, so a save taken mid-offer can quietly cost a player a battle. The interlock itself is NOT
    /// implemented here, because the dedicated server's autosave timer lives in code kept out of source
    /// control - this is the predicate that code can consult in one line.
    /// </remarks>
    public bool AnyOutstanding(double nowSeconds)
    {
        foreach (var entry in byOfferId.Values)
        {
            if (!entry.Resolved && nowSeconds < entry.ExpiresAtSeconds) return true;
        }

        return false;
    }

    /// <summary>Forgets every offer. Used when the world itself is replaced by a load.</summary>
    public void Clear() => byOfferId.Clear();

    /// <summary>Drops an offer entirely, answered or not.</summary>
    public void Forget(string offerId)
    {
        if (string.IsNullOrEmpty(offerId)) return;
        byOfferId.Remove(offerId);
    }

    /// <summary>
    /// Drops every offer belonging to a map event.
    /// </summary>
    /// <remarks>
    /// A siege runs assault after assault against the same settlement. Without this, an unanswered offer from
    /// one wave stays outstanding while the next wave's offer arrives, and a late answer to the old one would
    /// still be honoured.
    /// </remarks>
    /// <summary>
    /// Drops the offers one party still holds for a battle, so a new wave's offer to that party cannot be
    /// answered against the old one. The other parties' offers for the same battle stay answerable: a
    /// two-player battle offers each player its own spoils, and the second offer must not wipe the first.
    /// </summary>
    public int ForgetPartyOffers(string mapEventId, string partyId)
    {
        if (string.IsNullOrEmpty(mapEventId) || string.IsNullOrEmpty(partyId)) return 0;
        var doomed = new List<string>();
        foreach (var pair in byOfferId)
        {
            if (string.Equals(pair.Value.Offer.MapEventId, mapEventId, System.StringComparison.Ordinal) &&
                string.Equals(pair.Value.Offer.PartyId, partyId, System.StringComparison.Ordinal))
                doomed.Add(pair.Key);
        }
        foreach (var id in doomed) byOfferId.Remove(id);
        return doomed.Count;
    }

    public int ForgetMapEvent(string mapEventId)
    {
        if (string.IsNullOrEmpty(mapEventId)) return 0;

        var doomed = new List<string>();
        foreach (var pair in byOfferId)
        {
            if (string.Equals(pair.Value.Offer.MapEventId, mapEventId, System.StringComparison.Ordinal))
                doomed.Add(pair.Key);
        }

        foreach (var id in doomed) byOfferId.Remove(id);
        return doomed.Count;
    }

    /// <summary>Removes offers that are past their lifetime, answered or not. Returns how many went.</summary>
    /// <remarks>
    /// Resolved entries are kept until this runs so replays keep being reported as replays rather than
    /// silently becoming "unknown offer". Purging is what stops that memory growing without bound.
    /// </remarks>
    public int PurgeExpired(double nowSeconds)
    {
        var doomed = new List<string>();
        foreach (var pair in byOfferId)
        {
            if (nowSeconds >= pair.Value.ExpiresAtSeconds) doomed.Add(pair.Key);
        }

        foreach (var id in doomed) byOfferId.Remove(id);
        return doomed.Count;
    }
}
