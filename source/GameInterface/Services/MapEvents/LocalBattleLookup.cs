using System;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// The battle the local player is in, whichever way their encounter holds it.
/// </summary>
/// <remarks>
/// <c>PlayerEncounter.Battle</c> is only <c>_mapEvent</c>, which is not set for every player in a battle:
/// someone fighting from inside a besieged settlement has a SETTLEMENT encounter, and someone helping an ally
/// has an encounter with that ally's party. Both reach the battle through <c>EncounteredBattle</c> instead -
/// via <c>_encounteredParty.MapEvent</c>, or for a settlement via its siege event's besieger camp.
///
/// Checking <c>Battle</c> alone therefore silently misses real participants, which has cost us the same bug
/// twice: a siege defender staged for no loot at all, and a "send troops" that ran ungated because it could
/// not find the battle it was about to resolve. The encounter-close path
/// (<c>PvPInteractionClientHandler.TryGetLocalPartyFromMapEvent</c>) already learned to check both; this puts
/// that knowledge in one place so the next caller inherits it.
///
/// Both properties dereference state that may already be torn down, so both are read defensively.
/// </remarks>
internal static class LocalBattleLookup
{
    /// <summary>
    /// The battle the local player is in, for deciding what to ACT on. Falls back to the party's own map event
    /// so a player whose encounter has not caught up is still acting on the battle they are standing in.
    /// </summary>
    public static MapEvent Resolve()
        => Battle() ?? EncounteredBattle() ?? MobileParty.MainParty?.MapEvent;

    /// <summary>
    /// Whether the OPEN ENCOUNTER is about <paramref name="mapEvent"/> — for guarding what may be staged onto it.
    /// </summary>
    /// <remarks>
    /// Deliberately narrower than <see cref="Resolve"/>: it asks about the encounter, not about the player.
    /// Being in a battle is not enough, because the encounter on screen may be for something else entirely, and
    /// staging one battle's loot onto another battle's encounter puts it on the wrong screen (BR-081). Only the
    /// two fields an encounter itself holds a battle in count here.
    /// </remarks>
    public static bool MatchesEncounter(MapEvent mapEvent)
    {
        if (mapEvent == null) return false;

        return Battle() == mapEvent || EncounteredBattle() == mapEvent;
    }

    private static MapEvent Battle()
    {
        try
        {
            return PlayerEncounter.Battle;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }

    private static MapEvent EncounteredBattle()
    {
        try
        {
            return PlayerEncounter.EncounteredBattle;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }
}
