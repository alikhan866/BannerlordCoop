using GameInterface.Services.MapEvents;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// Who a defeated army's heroes are allowed to end up with.
/// </summary>
/// <remarks>
/// The rule only has to fire in one shape - one player's non-lord hero passing into another player's hands -
/// and every neighbouring shape has to be left exactly as vanilla plays it. So the cases here are mostly the
/// ones that must NOT trigger: over-reach is how a fix like this quietly changes the campaign for everyone.
///
/// Clans are bare instances used as identities; nothing here reads a field off them.
/// </remarks>
public class PostBattleHeroOwnershipTests
{
    private static Clan Someone() => (Clan)RuntimeHelpers.GetUninitializedObject(typeof(Clan));

    [Fact]
    public void AnotherPlayersCompanionIsLetGoRatherThanTaken()
    {
        var kan = Someone();
        var jian = Someone();

        Assert.True(PostBattleHeroOwnership.MustGoFree(
            heroIsLord: false, owningPlayerClan: kan, captorClan: jian, captorIsPlayerClan: true));
    }

    [Fact]
    public void ALordIsStillCapturedHoweverHeIsOwned()
    {
        // Capturing lords is the point of winning, and the winner speaks to them face to face first - that
        // conversation is the player's decision to make, so this rule keeps out of it.
        var kan = Someone();
        var jian = Someone();

        Assert.False(PostBattleHeroOwnership.MustGoFree(
            heroIsLord: true, owningPlayerClan: kan, captorClan: jian, captorIsPlayerClan: true));
    }

    [Fact]
    public void YourOwnCompanionComesBackToYou()
    {
        // Recovering your own people is the whole reason the winner gets the loser's prisoners.
        var jian = Someone();

        Assert.False(PostBattleHeroOwnership.MustGoFree(
            heroIsLord: false, owningPlayerClan: jian, captorClan: jian, captorIsPlayerClan: true));
    }

    [Fact]
    public void AnAiCompanionIsCapturedNormally()
    {
        // Nobody disagrees about a hero no player commands, so vanilla is left alone.
        Assert.False(PostBattleHeroOwnership.MustGoFree(
            heroIsLord: false, owningPlayerClan: null, captorClan: Someone(), captorIsPlayerClan: true));
    }

    [Fact]
    public void AnAiCaptorMayTakeAPlayersCompanion()
    {
        // A companion carried off by a lord is ordinary campaign life, and the server owns both ends of it.
        // Only a hero passing from one CLIENT to another leaves the two disagreeing over who commands them.
        Assert.False(PostBattleHeroOwnership.MustGoFree(
            heroIsLord: false, owningPlayerClan: Someone(), captorClan: Someone(), captorIsPlayerClan: false));
    }

    [Fact]
    public void NoCaptorMeansNoDecisionToMake()
    {
        Assert.False(PostBattleHeroOwnership.MustGoFree(
            heroIsLord: false, owningPlayerClan: Someone(), captorClan: null, captorIsPlayerClan: true));
    }
}
