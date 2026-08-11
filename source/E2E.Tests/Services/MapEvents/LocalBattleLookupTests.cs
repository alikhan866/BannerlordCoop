using GameInterface.Services.MapEvents;
using E2E.Tests.Environment.Instance;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.MapEvents;

/// <summary>
/// Finding the battle the local player is in, and — separately — whether the OPEN ENCOUNTER is about it.
/// </summary>
/// <remarks>
/// <c>PlayerEncounter.Battle</c> is only <c>_mapEvent</c>, and several real participants do not have it set:
/// a player fighting from inside a besieged settlement has a settlement encounter, and a player helping an ally
/// has an encounter with that ally's party. Both reach the battle only through <c>EncounteredBattle</c>.
/// Testing <c>Battle</c> alone cost the same bug twice — a siege defender staged for no loot at all, and a
/// "send troops" that could not identify its battle and so ran the native auto-resolve ungated, opening a
/// scoreboard nothing paced and nothing finished.
///
/// The two questions are deliberately NOT the same predicate, which is the point of these tests:
/// <list type="bullet">
/// <item><c>Resolve</c> — "which battle am I in", for deciding what to act on. Falls back to the party's own
/// map event.</item>
/// <item><c>MatchesEncounter</c> — "is the encounter on screen about this battle", for deciding what may be
/// staged onto it. Being in a battle is not enough (BR-081): the encounter may be for something else, and one
/// battle's loot must not land on another battle's screen.</item>
/// </list>
/// </remarks>
public class LocalBattleLookupTests : MapEventTestBase
{
    public LocalBattleLookupTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void AnOrdinaryBattleEncounter_ResolvesAndMatches()
    {
        var ctx = CreateServerMapEvent();
        var client = Clients.First();
        SetMockPlayerEncounter(client, mapEventId: ctx.MapEventId);

        AssertBattle(client, ctx.MapEventId, resolves: true, matchesEncounter: true);
    }

    [Fact]
    public void AnEncounterHoldingTheBattleThroughItsEncounteredParty_ResolvesAndMatches()
    {
        // The shape a besieged settlement's defender and an ally-helper both have: _mapEvent is never set and
        // the battle is reachable only through the party the encounter is with. This is the case that used to
        // be missed, leaving that player with no loot staged at all.
        var ctx = CreateServerMapEvent();
        var client = Clients.First();
        SetMockPlayerEncounter(client, encounteredPartyId: ctx.DefenderPartyId);

        AssertBattle(client, ctx.MapEventId, resolves: true, matchesEncounter: true);
    }

    [Fact]
    [Trait("Requirement", "BR-081")]
    public void APartyInABattleWhoseEncounterIsElsewhere_Resolves_ButIsNotAnEncounterMatch()
    {
        // The distinction BR-081 rests on. The player's party is in `mine`, but the encounter on screen is for
        // `elsewhere` — so `mine`'s results must NOT be staged onto it, even though the player is in `mine`.
        var mine = CreateServerMapEvent();
        var elsewhere = CreateServerMapEvent();
        var client = Clients.First();
        SetMainParty(client, mine.AttackerPartyId);
        SetMockPlayerEncounter(client, mapEventId: elsewhere.MapEventId);

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(mine.MapEventId, out var myBattle));

            // The encounter wins for Resolve too — it is the thing the player is looking at.
            Assert.False(LocalBattleLookup.MatchesEncounter(myBattle));
        }, MapEventDisabledMethods);
    }

    [Fact]
    public void APartyInABattleWithNoEncounterAtAll_StillResolves()
    {
        // Nothing on screen, but the player is standing in a battle: act on it. There is no encounter for it
        // to be "about", so it is not an encounter match.
        var ctx = CreateServerMapEvent();
        var client = Clients.First();
        ClearPlayerEncounter(client);
        SetMainParty(client, ctx.AttackerPartyId);

        AssertBattle(client, ctx.MapEventId, resolves: true, matchesEncounter: false);
    }

    [Fact]
    public void ABattleTheLocalPlayerIsNotIn_IsNotAnEncounterMatch()
    {
        var mine = CreateServerMapEvent();
        var theirs = CreateServerMapEvent();
        var client = Clients.First();
        SetMockPlayerEncounter(client, mapEventId: mine.MapEventId);

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(theirs.MapEventId, out var otherBattle));
            Assert.False(LocalBattleLookup.MatchesEncounter(otherBattle));
        }, MapEventDisabledMethods);
    }

    [Fact]
    public void NoEncounterAndNoBattle_ResolvesToNothing()
    {
        var client = Clients.First();
        ClearPlayerEncounter(client);

        client.Call(() =>
        {
            if (MobileParty.MainParty != null) Campaign.Current.MainParty = null;

            Assert.Null(LocalBattleLookup.Resolve());
            Assert.False(LocalBattleLookup.MatchesEncounter(null));
        }, MapEventDisabledMethods);
    }

    private void AssertBattle(EnvironmentInstance client, string mapEventId, bool resolves, bool matchesEncounter)
    {
        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MapEvent>(mapEventId, out var mapEvent));

            if (resolves) Assert.Same(mapEvent, LocalBattleLookup.Resolve());
            Assert.Equal(matchesEncounter, LocalBattleLookup.MatchesEncounter(mapEvent));
        }, MapEventDisabledMethods);
    }

    private void SetMainParty(EnvironmentInstance client, string partyId)
    {
        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(partyId, out var party));
            Campaign.Current.MainParty = party;
            Assert.NotNull(MobileParty.MainParty.Party.MapEventSide);
        }, MapEventDisabledMethods);
    }
}
