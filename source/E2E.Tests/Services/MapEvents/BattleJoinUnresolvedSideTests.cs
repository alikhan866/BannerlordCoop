using E2E.Tests.Util;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Patches;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.MapEvents;

/// <summary>
/// A battle whose own parties cannot be evaluated must not look like a battle nobody wanted to join.
/// </summary>
/// <remarks>
/// <c>InteractionPatches</c> guards vanilla's <c>CanPartyJoinBattle</c> against half-synced state by forcing it
/// to return TRUE when the battle's parties cannot all be resolved. That guard is right, but it answers TRUE for
/// BOTH sides - and <see cref="BattleJoinCandidates.TryChooseSide"/> reads "eligible for both" as a party whose
/// diplomacy leaves the side undefined, so it declines to guess and skips it.
///
/// Put together, one unresolved party in a battle silently rejects EVERY nearby lord, and the sweep reports the
/// same empty list it would report if the battle were alone in the wilderness. Those two outcomes are not the
/// same problem and must not look alike, which is what this pins.
/// </remarks>
public class BattleJoinUnresolvedSideTests : MapEventTestBase
{
    public BattleJoinUnresolvedSideTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void ACandidateTheBattleCannotEvaluate_IsCountedRatherThanSilentlyDropped()
    {
        var ctx = CreateServerMapEvent();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out var mapEvent));

            var nearbyParty = GameObjectCreator.CreateInitializedObject<MobileParty>();

            BattleJoinCandidates.ResetUnresolvedSideDecisions();
            bool evaluable = InteractionPatches.CanEvaluateJoinBattle(mapEvent, nearbyParty.Party);

            var chose = BattleJoinCandidates.TryChooseSide(mapEvent, nearbyParty, out _);

            if (evaluable)
            {
                // Nothing was unevaluable, so the counter must stay still - otherwise ordinary traffic would
                // raise the alarm and the signal would be worthless.
                Assert.Equal(0, BattleJoinCandidates.UnresolvedSideDecisions);
            }
            else
            {
                // The whole point: refused, and SAID so, instead of vanishing into an empty candidate list.
                Assert.False(chose);
                Assert.Equal(1, BattleJoinCandidates.UnresolvedSideDecisions);
            }
        });
    }

    [Fact]
    public void TheCounterOnlyMovesForUnevaluableCandidates()
    {
        var ctx = CreateServerMapEvent();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out var mapEvent));

            BattleJoinCandidates.ResetUnresolvedSideDecisions();

            // A null party is malformed input, not unevaluable battle state, and must not be counted as the
            // latter - the count exists to accuse the BATTLE, and a false accusation is worse than none.
            Assert.False(BattleJoinCandidates.TryChooseSide(mapEvent, null, out _));
            Assert.Equal(0, BattleJoinCandidates.UnresolvedSideDecisions);
        });
    }
}
