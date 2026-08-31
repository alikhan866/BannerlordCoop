using Common;
using GameInterface.Policies;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.Clans.Patches;

/// <summary>
/// Lets a player who is NOT their kingdom's ruler offer a captured lord the "join us, and be set free" line.
/// </summary>
/// <remarks>
/// WHAT VANILLA DOES, AND WHY IT DOES NOT FIT CO-OP.
///
/// The prisoner recruitment line is a dialogue of its own -
/// <c>player_prisoner_talk</c>, "I have an offer for you: join us, and be set free." - gated by
/// <c>conversation_player_start_defection_with_prisoner_on_condition</c>. That condition carries a clause the
/// free-lord recruitment lines do NOT:
///
///     Hero.MainHero.IsKingdomLeader
///
/// In singleplayer that is simply true whenever it matters: you are the only player, and a kingdom you belong
/// to is one you rule. In co-op a kingdom routinely has several players in it and only one throne, so every
/// vassal player is silently missing an option the ruler has. Reported exactly that way: the same lord could
/// be persuaded in the field, then could not be persuaded once captured, while the kingdom's ruler could
/// recruit his own prisoners freely.
///
/// WHY DROPPING IT IS SAFE, AND NOT A CHEAT.
///
/// The server never agreed with the restriction in the first place.
/// <c>LordBarterHandler.CanAuthorizeKind</c> authorises a <c>JoinKingdomAsClan</c> barter on:
/// the target kingdom being the requester's OWN kingdom, the requester leading their OWN clan, the target
/// leading theirs, the target not already being in that kingdom, and the target clan not being minor, rebel
/// or mercenary. There is no ruler check anywhere in it. So the barter this line leads to was always going to
/// be accepted for a vassal - the dialogue simply never offered it, and the request was never sent.
///
/// Every other clause of the vanilla condition is reproduced below unchanged, including the two
/// <see cref="ConversationContext"/> exclusions and the "held by me, or in a settlement my clan owns" branch.
/// Only the leader clause is gone.
///
/// The reimplementation is pinned to the shipped v1.4.8 body, decompiled. If a game update changes the
/// condition this patch will silently enforce the OLD rules, so the clause list is written out in full above
/// the code rather than summarised, and a mismatch is meant to be obvious on inspection.
/// </remarks>
[HarmonyPatch(typeof(LordDefectionCampaignBehavior), "conversation_player_start_defection_with_prisoner_on_condition")]
internal class PrisonerDefectionVassalPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ref bool __result)
    {
        if (CallOriginalPolicy.IsOriginalAllowed()) return true;

        __result = CanOfferFreedomForDefection(
            Hero.OneToOneConversationHero,
            Clan.PlayerClan,
            Campaign.Current?.CurrentConversationContext ?? ConversationContext.Default);
        return false;
    }

    /// <summary>
    /// Vanilla's condition with the <c>IsKingdomLeader</c> clause removed; everything else is as shipped.
    /// </summary>
    /// <remarks>
    /// Taken as arguments rather than read from statics so the rule can be driven from a test -
    /// <see cref="Hero.OneToOneConversationHero"/> and <see cref="Clan.PlayerClan"/> are getter-only.
    /// </remarks>
    internal static bool CanOfferFreedomForDefection(
        Hero conversationHero, Clan playerClan, ConversationContext context)
    {
        if (conversationHero == null || playerClan?.Kingdom == null) return false;

        // The captive must lead his own clan: a clan changes allegiance through its leader, so anyone else
        // has nothing to bring across.
        if (conversationHero.Clan?.Leader != conversationHero) return false;
        if (conversationHero.HeroState != Hero.CharacterStates.Prisoner) return false;

        // Vanilla excludes both of these outright: they are the post-battle captive screen and the
        // free-or-capture prompt, which run their own flows and must not open a defection barter.
        if (context == ConversationContext.CapturedLord) return false;
        if (context == ConversationContext.FreeOrCapturePrisonerHero) return false;

        // A kingdom's own ruling clan cannot defect away from itself.
        if (conversationHero.MapFaction?.Leader == null) return false;
        if (conversationHero.Clan == conversationHero.MapFaction.Leader.Clan) return false;

        // Held by me, or held in a settlement my clan owns. Unchanged from vanilla, and it is what keeps a
        // player from recruiting a captive who is somebody else's problem.
        var captor = conversationHero.PartyBelongedToAsPrisoner;
        if (captor != null && captor == PartyBase.MainParty) return true;

        var settlement = conversationHero.CurrentSettlement;
        return settlement != null && settlement.OwnerClan == playerClan;
    }
}
