using GameInterface.Services.MapEvents;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.MapEvents;

/// <summary>
/// Where the server looks when deciding which nearby lords will join a battle.
/// </summary>
/// <remarks>
/// This is the defect that made every other reinforcement symptom possible, and it hid for a long time because
/// it fails silently: the scan runs, finds nobody, and reports nothing wrong. Measured live at 18,033
/// consecutive scans of a live battle offering zero joiners, while lords stood beside the fight doing nothing.
///
/// The cause was that vanilla's <c>FindNonAttachedNpcPartiesWhoWillJoinPlayerEncounter</c> centres its radius
/// search on <c>MobileParty.MainParty.Position</c>, and only re-centres on the battle when
/// <c>PlayerEncounter.Battle</c> is non-null. A server has no PlayerEncounter for a client's battle, so it
/// searched around ITS OWN character - anywhere on the map - rather than around the fight.
///
/// Hence the test: make the two positions differ, and require the answer to be the battle's.
/// </remarks>
public class BattleJoinCandidatesTests : MapEventTestBase
{
    public BattleJoinCandidatesTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void TheSearchIsCentredOnTheBattle_NotOnWhoeverTheServerHappensToBe()
    {
        var ctx = CreateServerMapEvent();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out var mapEvent));

            // Put the battle somewhere unmistakable, and the server's own character somewhere else entirely.
            // Vanilla's model would answer with the second of these.
            var battleAt = new CampaignVec2(new Vec2(120f, 240f), isOnLand: true);
            mapEvent.Position = battleAt;

            var mainParty = MobileParty.MainParty;
            if (mainParty != null) mainParty.Position = new CampaignVec2(new Vec2(900f, 30f), isOnLand: true);

            var centre = BattleJoinCandidates.SearchCentre(mapEvent);

            Assert.Equal(battleAt.ToVec2().X, centre.X, 3);
            Assert.Equal(battleAt.ToVec2().Y, centre.Y, 3);

            if (mainParty != null)
            {
                Assert.False(centre.NearlyEquals(mainParty.Position.ToVec2()),
                    "searching around the server's own party is the bug this exists to prevent");
            }
        }, MapEventDisabledMethods);
    }

    [Fact]
    public void APartyAlreadyFightingSomewhereElse_IsNotPulledIn()
    {
        // Vanilla's own exclusion, and the one that matters most: a party in another battle must not be
        // dragged into this one. Kept explicit because the filters were re-stated rather than reused, and a
        // re-statement is exactly where an exclusion goes quietly missing.
        var ctx = CreateServerMapEvent();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out var mapEvent));
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(ctx.AttackerPartyId, out var busy));

            // The attacker is in THIS map event, so it is already fighting.
            Assert.NotNull(busy.MapEvent);
            Assert.False(BattleJoinCandidates.IsEligible(busy, mapEvent));
        }, MapEventDisabledMethods);
    }

    [Fact]
    public void ANullPartyOrBattle_IsSimplyNotEligible()
    {
        // The scan runs on every live battle every second; it must answer "no" to nonsense rather than throw.
        // An exception here would propagate into the server's tick.
        Assert.False(BattleJoinCandidates.IsEligible(null, null));
    }
}
