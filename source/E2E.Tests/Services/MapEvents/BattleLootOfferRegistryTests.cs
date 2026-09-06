using System;
using System.Collections.Generic;
using GameInterface.Services.MapEvents.Loot;
using Xunit;

namespace E2E.Tests.Services.MapEvents;

/// <summary>
/// The spoils registry must keep one answerable offer per party per battle. The results handler used to forget the
/// WHOLE battle before registering each player's offer; in a two-player battle the second player's offer erased the
/// first's, and the first player's answer came back "UnknownOffer" (live, 5 Sep 2026, `2134-army-100-lootwalk2`:
/// the winner's 208-line offer was refused 54 s after it was made, lifetime 600 s).
/// </summary>
public class BattleLootOfferRegistryTests
{
    private static double Now() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    private static BattleLootOffer Offer(string id, string mapEventId, string partyId) =>
        new BattleLootOffer(id, mapEventId, partyId, new[] { new BattleLootOfferLine(BattleLootLineKind.Item, "item_" + id, null, 1) });

    [Fact]
    public void ForgettingOnePartysOffers_LeavesTheOtherPlayersOfferAnswerable()
    {
        var registry = new BattleLootOfferRegistry();
        double now = Now();
        registry.Register(Offer("winner-old", "battle_1", "party_winner"), now);
        // The winner's real offer for this battle replaces its own earlier one...
        registry.ForgetPartyOffers("battle_1", "party_winner");
        registry.Register(Offer("winner", "battle_1", "party_winner"), now);
        // ...and the loser's offer for the same battle must not touch it.
        registry.ForgetPartyOffers("battle_1", "party_loser");
        registry.Register(Offer("loser", "battle_1", "party_loser"), now);

        Assert.False(registry.IsOutstanding("winner-old", now));
        Assert.True(registry.TryClaim("winner", "party_winner", now, out var claimed, out var refusal));
        Assert.Equal(BattleLootClaimRefusal.None, refusal);
        Assert.Equal("party_winner", claimed.PartyId);
        Assert.True(registry.TryClaim("loser", "party_loser", now, out _, out refusal));
        Assert.Equal(BattleLootClaimRefusal.None, refusal);
    }

    [Fact]
    public void AnAnsweredOfferStillCountsAsRecentlyOffered_SoTheDonationCapDoesNotShrink()
    {
        // The loot answer and the inventory screen's Done are two messages; either can arrive first, and the XP a
        // player may claim for donating is bounded by what it was offered, not by what is still unanswered.
        var registry = new BattleLootOfferRegistry();
        double now = Now();
        registry.Register(Offer("answered", "battle_4", "party_a"), now);
        Assert.True(registry.TryClaim("answered", "party_a", now, out _, out _));
        Assert.Empty(registry.OutstandingFor("party_a", now));
        Assert.Single(registry.RecentFor("party_a", now));
        // Expiry still applies, so the bound cannot be reused a campaign later.
        Assert.Empty(registry.RecentFor("party_a", now + BattleLootOfferRegistry.DefaultLifetimeSeconds + 1));
    }

    [Fact]
    public void ForgettingTheWholeBattle_IsStillTheEndOfBattleSweep()
    {
        var registry = new BattleLootOfferRegistry();
        double now = Now();
        registry.Register(Offer("a", "battle_2", "party_a"), now);
        registry.Register(Offer("b", "battle_2", "party_b"), now);
        registry.Register(Offer("c", "battle_3", "party_a"), now);
        Assert.Equal(2, registry.ForgetMapEvent("battle_2"));
        Assert.False(registry.TryClaim("a", "party_a", now, out _, out var refusal));
        Assert.Equal(BattleLootClaimRefusal.UnknownOffer, refusal);
        Assert.True(registry.TryClaim("c", "party_a", now, out _, out _));
    }
}
