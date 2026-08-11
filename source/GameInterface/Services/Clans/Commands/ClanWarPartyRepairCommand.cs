using System.Collections.Generic;
using System.Text;
using Common;
using Common.Logging;
using Common.Util;
using GameInterface.Services.ObjectManager;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Clans.Commands;

/// <summary>
/// Puts a clan's war parties back into the cache Army Management reads.
/// </summary>
/// <remarks>
/// <c>Clan.WarPartyComponents</c> is what the army screen lists, and it is a CACHE - maintained by
/// <c>OnWarPartyAdded</c> as parties are created, not derived from the parties themselves. So a party can exist,
/// belong to the clan, field troops and move around the map while being absent from that list, and the only
/// visible symptom is that it cannot be summoned to an army: no row, no greyed-out entry, no explanation.
///
/// That is what happened to "Oragur the Knowing's Party". The registration message is sent once, when the war
/// party is added, and <c>ClanCachesHandler</c> used to DROP it if the component had not been registered on the
/// receiving machine yet - a race it loses regularly. Nothing retried, so the cache stayed short by one entry
/// for the rest of the save's life.
///
/// The drop is fixed at the source now, so new registrations are held and retried. This repairs the saves that
/// already lost one, by rebuilding the cache from the parties that actually exist - which is the authority the
/// cache was supposed to be tracking all along.
/// </remarks>
public static class ClanWarPartyRepairCommand
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(ClanWarPartyRepairCommand));

    /// <summary>
    /// Whether a party should be in its clan's war-party cache but is not.
    /// </summary>
    /// <remarks>
    /// All three conditions matter. A party belonging to another clan is not this clan's to register; a
    /// non-war party (a caravan, a garrison) never belongs in the list; and one already cached must not be
    /// added twice, which would show it in the army screen as a duplicate row.
    /// </remarks>
    internal static bool IsMissingFromCache(bool belongsToClan, bool isWarParty, bool alreadyCached)
        => belongsToClan && isWarParty && !alreadyCached;

    [CommandLineArgumentFunction("repair_war_parties", "coop.debug.clan")]
    public static string RepairWarParties(List<string> args)
    {
        if (ModInformation.IsClient) return "Command can only be run on the server.";
        if (args.Count < 1 || args.Count > 2)
            return "Usage: coop.debug.clan.repair_war_parties <clanId> [scan]";

        if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) == false)
            return "Unable to resolve the ObjectManager.";
        if (!objectManager.TryGetObject<Clan>(args[0], out var clan) || clan == null)
            return $"No clan with id {args[0]}.";

        bool applyChanges = !(args.Count == 2 && args[1].Trim().Equals("scan", System.StringComparison.OrdinalIgnoreCase));

        var cached = new HashSet<WarPartyComponent>();
        foreach (var component in clan.WarPartyComponents) cached.Add(component);

        var report = new StringBuilder();
        int missing = 0, restored = 0;

        foreach (var party in MobileParty.All)
        {
            if (party?.PartyComponent is not WarPartyComponent warParty) continue;

            if (!IsMissingFromCache(
                    belongsToClan: ReferenceEquals(party.ActualClan, clan),
                    isWarParty: true,
                    alreadyCached: cached.Contains(warParty)))
                continue;

            missing++;
            report.AppendLine($"  {party.Name} ({party.StringId}) led by {party.LeaderHero?.Name?.ToString() ?? "<none>"}");

            if (!applyChanges) continue;

            using (new AllowedThread())
            {
                clan.OnWarPartyAdded(warParty);
            }
            restored++;
        }

        if (missing == 0)
            return $"{clan.Name}'s war-party cache is complete ({cached.Count} parties); nothing missing.";

        Logger.Information("[Repair] {Clan} war-party cache was missing {Missing} party(ies); restored {Restored}",
            clan.Name, missing, restored);

        var header = applyChanges
            ? $"{clan.Name}: {missing} party(ies) were missing from the army list; restored {restored}. Now {clan.WarPartyComponents.Count} cached:"
            : $"{clan.Name}: {missing} party(ies) missing from the army list (nothing changed - omit 'scan' to repair):";

        return header + "\n" + report;
    }
}
