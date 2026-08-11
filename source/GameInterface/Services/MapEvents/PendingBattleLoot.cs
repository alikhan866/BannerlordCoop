using Common.Logging;
using Common.Util;
using Serilog;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// Loot that has been staged into the local encounter but not yet handed to the player.
/// </summary>
/// <remarks>
/// Staging loot into <c>PlayerEncounter</c> is how single player does it: the rosters sit on the encounter and
/// are granted when it walks its states, reaching <c>LootInventory</c> to offer the loot screen. It assumes the
/// encounter survives long enough to get there.
///
/// In co-op it does not. Concluding a battle finalizes the map event and closes every involved player's
/// encounter, and that can happen before the states have run. The loot is then destroyed along with the
/// encounter, silently and with nothing to distinguish it from a battle that awarded nothing: measured live,
/// one player had 73 item stacks, 19 recovered members and 43 prisoners staged and received none of it, while
/// the other - who happened to leave through a retreat prompt that re-entered the encounter flow - collected
/// all of his.
///
/// So a copy is kept here. If the encounter ends without ever offering the loot screen, it is awarded directly
/// instead of being lost. If the screen WAS offered, this is discarded untouched: declining loot at the screen
/// is a real choice and must not be overridden.
/// </remarks>
public static class PendingBattleLoot
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(PendingBattleLoot));

    private static readonly object Gate = new object();
    private static string mapEventId;
    private static ItemRoster items;
    private static TroopRoster members;
    private static TroopRoster prisoners;

    /// <summary>Remember what was staged, so it can be rescued if the encounter never spends it.</summary>
    public static void Remember(string battleId, ItemRoster stagedItems, TroopRoster stagedMembers, TroopRoster stagedPrisoners)
    {
        lock (Gate)
        {
            mapEventId = battleId;
            items = stagedItems;
            members = stagedMembers;
            prisoners = stagedPrisoners;
        }
    }

    /// <summary>Whether anything is being held for rescue.</summary>
    public static bool HasPending
    {
        get { lock (Gate) return mapEventId != null; }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            mapEventId = null;
            items = null;
            members = null;
            prisoners = null;
        }
    }

    /// <summary>
    /// Hands whatever is held straight to the player's party, and forgets it.
    /// </summary>
    /// <remarks>
    /// Skips the loot screen, which is a real loss of presentation - but the alternative on this path is the
    /// player receiving nothing at all, and the server has already decided they are entitled to it.
    /// </remarks>
    public static void AwardToMainParty(string reason)
    {
        string battleId;
        ItemRoster awardItems;
        TroopRoster awardMembers;
        TroopRoster awardPrisoners;

        lock (Gate)
        {
            if (mapEventId == null) return;

            battleId = mapEventId;
            awardItems = items;
            awardMembers = members;
            awardPrisoners = prisoners;
            mapEventId = null;
            items = null;
            members = null;
            prisoners = null;
        }

        var party = MobileParty.MainParty;
        if (party == null)
        {
            Logger.Warning("[Loot] No local party to rescue the results of {MapEvent} into ({Reason})", battleId, reason);
            return;
        }

        int itemCount = awardItems?.Count ?? 0;
        int memberCount = awardMembers?.Count ?? 0;
        int prisonerCount = awardPrisoners?.Count ?? 0;

        // Applied the way every other authoritative change is applied: inside an AllowedThread, so the roster
        // patches let it through without treating it as this machine proposing a change of its own. Without it
        // the client's own rescue tripped the managed-roster guard three times per award ("Client attempted to
        // AddToCountsAtIndex on a managed TroopRoster"), which is noise here - the server decided this loot and
        // mirrors it onto its own copy of the party.
        using (new AllowedThread())
        {
            if (awardItems != null) party.ItemRoster.Add(awardItems);
            if (awardMembers != null) party.MemberRoster.Add(awardMembers);
            if (awardPrisoners != null) party.PrisonRoster.Add(awardPrisoners);
        }

        Logger.Information(
            "[Loot] Rescued {MapEvent} into {Party} ({Reason}): {Items} item stack(s), {Members} member(s), {Prisoners} prisoner(s)",
            battleId, party.Name, reason, itemCount, memberCount, prisonerCount);
    }
}
