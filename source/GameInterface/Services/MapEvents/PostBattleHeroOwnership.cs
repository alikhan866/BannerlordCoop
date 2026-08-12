using GameInterface.Services.Clans.Extensions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// Who a hero answers to, for the purpose of dividing up the spoils of a battle.
/// </summary>
/// <remarks>
/// Single-player has one player, so "the winner takes the losers" needs no qualification. Co-op does: a
/// defeated army routinely carries heroes that belong to somebody who is not the winner - another player's
/// companions riding with an ally, or lords that another player had taken prisoner and was still carrying.
/// Handed to the winner those heroes join THEIR party, and the two clients then disagree about who commands
/// them, which is what locks the game up.
///
/// The rule is decided here, once, and applied on the SERVER while it divides the spoils - so the hero never
/// appears in anyone's loot in the first place. Deciding it on the client instead cannot work: the loot
/// transaction reads a client's staged rosters as its answer, so a hero quietly dropped there is indistinguishable
/// from one the player took, and the server would imprison them anyway.
/// </remarks>
internal static class PostBattleHeroOwnership
{
    /// <summary>
    /// The player clan a hero belongs to, or null when no player has a claim on them.
    /// </summary>
    /// <remarks>
    /// Both halves matter. A hero's own clan covers a player's family and the lords sworn to them; CompanionOf
    /// covers companions, whose Clan is null but who are unmistakably somebody's.
    /// </remarks>
    internal static Clan OwningPlayerClan(Hero hero)
    {
        if (hero == null) return null;

        if (hero.Clan != null && hero.Clan.IsPlayerClan()) return hero.Clan;

        if (hero.CompanionOf != null && hero.CompanionOf.IsPlayerClan()) return hero.CompanionOf;

        return null;
    }

    /// <summary>
    /// Whether the captor must let this hero go rather than take them.
    /// </summary>
    /// <remarks>
    /// Three conditions, each of them narrowing:
    ///
    /// The hero is owned by a player at all - an AI's companion is nobody's to disagree about.
    /// The CAPTOR is a player too - a companion taken by a lord is ordinary campaign life, and the server owns
    /// both ends of it; only a hero passing from one client's hands to another's causes the two to disagree.
    /// The hero is not a lord - lords are captured on purpose, and the winner speaks to them face to face
    /// before deciding, which is a choice the player keeps however that lord is owned.
    ///
    /// Written against the captor's clan rather than "is this my instance", because this runs on the SERVER,
    /// where nothing is controlled by this instance and every player would otherwise look foreign - including
    /// the winner, whose own recovered companions would then be given away.
    /// </remarks>
    internal static bool MustGoFree(Hero hero, PartyBase captorParty)
    {
        if (hero == null) return false;

        var captor = CaptorClan(captorParty);

        return MustGoFree(hero.IsLord, OwningPlayerClan(hero), captor, captor.IsPlayerClan());
    }

    /// <summary>
    /// The decision itself, with the world already looked up. Separated so it can be read - and tested - as
    /// the three narrowing conditions it is, rather than as a walk through campaign objects.
    /// </summary>
    internal static bool MustGoFree(bool heroIsLord, Clan owningPlayerClan, Clan captorClan, bool captorIsPlayerClan)
    {
        if (heroIsLord) return false;
        if (owningPlayerClan == null) return false;
        if (captorClan == null || !captorIsPlayerClan) return false;

        return captorClan != owningPlayerClan;
    }

    private static Clan CaptorClan(PartyBase captorParty)
    {
        if (captorParty == null) return null;

        return captorParty.MobileParty?.ActualClan ?? captorParty.LeaderHero?.Clan ?? captorParty.Owner?.Clan;
    }
}
