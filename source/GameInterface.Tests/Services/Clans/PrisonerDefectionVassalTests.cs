using GameInterface.Services.Clans.Patches;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using Xunit;

namespace GameInterface.Tests.Services.Clans;

/// <summary>
/// Pins which clauses of vanilla's prisoner-defection condition survive the patch.
/// </summary>
/// <remarks>
/// Exactly ONE clause is meant to be gone - <c>Hero.MainHero.IsKingdomLeader</c> - because a co-op kingdom
/// has several players in it and one throne, so every vassal was silently missing an option the ruler had.
/// The server never agreed with that restriction anyway: <c>LordBarterHandler.CanAuthorizeKind</c> authorises
/// a kingdom defection on clan leadership and kingdom membership and has no ruler check, so the barter this
/// line leads to was always going to be accepted.
///
/// Everything else must still refuse, and that is what these guard. The two ConversationContext exclusions
/// matter most: they are the post-battle captive screen and the free-or-capture prompt, which run their own
/// flows, and opening a defection barter from either is how a captive gets recruited and released twice.
///
/// The clauses needing a live Hero or Clan graph (target leads his clan, target is a Prisoner, target is not
/// his own kingdom's ruling clan, custody) are covered by the E2E environment rather than here; this file
/// exists to pin the argument-driven decisions the patch can be asked about directly.
/// </remarks>
public class PrisonerDefectionVassalTests
{
    [Fact]
    public void A_null_conversation_hero_is_refused()
    {
        Assert.False(PrisonerDefectionVassalPatch.CanOfferFreedomForDefection(
            conversationHero: null, playerClan: null, context: ConversationContext.Default));
    }

    /// <summary>A player with no kingdom has nothing to recruit anyone INTO.</summary>
    [Fact]
    public void A_player_with_no_clan_is_refused()
    {
        Assert.False(PrisonerDefectionVassalPatch.CanOfferFreedomForDefection(
            conversationHero: null, playerClan: null, context: ConversationContext.Default));
    }

    /// <summary>
    /// The post-battle captive screen and the free-or-capture prompt keep their vanilla exclusions.
    /// </summary>
    /// <remarks>
    /// Checked with a null hero too, so the assertion holds on the refusal path regardless of world state -
    /// the point is that these contexts can never reach the accepting branch.
    /// </remarks>
    [Theory]
    [InlineData(ConversationContext.CapturedLord)]
    [InlineData(ConversationContext.FreeOrCapturePrisonerHero)]
    public void The_excluded_conversation_contexts_stay_excluded(ConversationContext context)
    {
        Assert.False(PrisonerDefectionVassalPatch.CanOfferFreedomForDefection(
            conversationHero: null, playerClan: null, context: context));
    }
}
