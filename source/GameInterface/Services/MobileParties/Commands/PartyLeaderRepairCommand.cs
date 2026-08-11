using System;
using System.Collections.Generic;
using System.Text;
using Common;
using Common.Logging;
using GameInterface.Services.ObjectManager;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.MobileParties.Commands;

/// <summary>
/// Gives a lord party back the leader it lost, and with it the size limit, army eligibility and movement
/// that vanilla derives from having one.
/// </summary>
/// <remarks>
/// <c>LordPartyComponent</c> keeps two references to the same hero: <c>Owner</c>, set once in the constructor,
/// and <c>_leader</c>, which vanilla clears through <c>MobileParty.RemovePartyLeader</c> whenever the lord stops
/// leading - captured, killed, disbanding. Vanilla always follows that with something that ends the party or
/// hands it a new leader. In coop it can stall in between, and the party then survives indefinitely with an
/// owner but no leader.
///
/// Nothing about that state announces itself. <c>MobileParty.LeaderHero</c> reads <c>_leader</c> directly, so the
/// party keeps its owner's name in every list while:
///   - the party size model loses every leader-derived bonus and collapses to the base limit (Oragur the
///     Knowing's party showed "90 / 20" with three sibling parties at ~106),
///   - Army Management has no leader to build a row from, so the party is absent from the list entirely -
///     not greyed out, not refused, simply not there, which is why he could not be summoned,
///   - <c>ChangePartyLeader(null)</c> parked it on Hold on the way out.
///
/// The repair is the vanilla call, made on the server and deliberately NOT inside an <c>AllowedThread</c> so the
/// interception in <c>PartyComponentTranspilers</c> publishes it and every client applies the same change.
/// </remarks>
public static class PartyLeaderRepairCommand
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(PartyLeaderRepairCommand));

    /// <summary>
    /// Whether a lord party is missing the leader it should have, decided over plain values so it can be asserted.
    /// </summary>
    /// <remarks>
    /// A party with a leader is working. A party with no owner has nobody to promote. A dead or captive owner is
    /// exactly the case where vanilla removed the leader ON PURPOSE - reinstating one there would resurrect a
    /// lord who is sitting in somebody's dungeon.
    /// </remarks>
    internal static bool NeedsLeaderRestore(bool hasLeader, bool hasOwner, bool ownerIsAlive, bool ownerIsPrisoner)
        => !hasLeader && hasOwner && ownerIsAlive && !ownerIsPrisoner;

    /// <summary>Whether the owner has to join the roster before he can be made leader.</summary>
    /// <remarks>
    /// <c>PartyComponent.ChangePartyLeader</c> asserts and returns without changing anything when the new leader
    /// is not in that party's member roster. It fails silently in a release build, so the roster has to be
    /// correct first or the repair reports success while nothing happened.
    /// </remarks>
    internal static bool MustAddOwnerToRoster(bool ownerIsInPartyRoster) => !ownerIsInPartyRoster;

    [CommandLineArgumentFunction("repair_party_leader", "coop.debug.mobileparty")]
    public static string RepairPartyLeader(List<string> args)
    {
        if (ModInformation.IsClient) return "Command can only be run on the server.";
        if (args.Count < 1 || args.Count > 2)
            return "Usage: coop.debug.mobileparty.repair_party_leader <partyId|all> [scan]";

        bool applyChanges = !(args.Count == 2 && args[1].Trim().Equals("scan", StringComparison.OrdinalIgnoreCase));

        IEnumerable<MobileParty> targets;
        if (args[0].Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targets = MobileParty.All;
        }
        else
        {
            if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) == false)
                return "Unable to resolve the ObjectManager.";
            if (!objectManager.TryGetObject<MobileParty>(args[0], out var party) || party == null)
                return $"No party with id {args[0]}.";
            targets = new[] { party };
        }

        var report = new StringBuilder();
        int found = 0, repaired = 0;

        foreach (var party in targets)
        {
            if (party?.PartyComponent is not LordPartyComponent lordParty) continue;

            var owner = lordParty.Owner;
            if (!NeedsLeaderRestore(
                    hasLeader: party.LeaderHero != null,
                    hasOwner: owner != null,
                    ownerIsAlive: owner?.IsAlive == true,
                    ownerIsPrisoner: owner?.IsPrisoner == true))
                continue;

            found++;
            report.AppendLine($"  {party.Name} ({party.StringId}): owner {owner.Name}, no leader, " +
                              $"{party.MemberRoster.TotalManCount} men, size limit {party.Party.PartySizeLimit}");

            if (!applyChanges) continue;

            // Deliberately outside AllowedThread: the interception has to see this and replicate it, otherwise
            // the server gets its leader back and every client keeps the broken party.
            if (MustAddOwnerToRoster(party.MemberRoster.GetTroopCount(owner.CharacterObject) > 0))
                party.AddElementToMemberRoster(owner.CharacterObject, 1, false);

            party.ChangePartyLeader(owner);

            if (party.LeaderHero == null)
            {
                report.AppendLine($"    FAILED - leader is still null; vanilla refused the change.");
                continue;
            }

            repaired++;
            report.AppendLine($"    restored leader {party.LeaderHero.Name}, size limit now {party.Party.PartySizeLimit}");
        }

        if (found == 0)
            return applyChanges
                ? "No leaderless lord parties found; nothing to repair."
                : "No leaderless lord parties found.";

        Logger.Information("[Repair] {Found} leaderless lord party(ies); restored {Repaired}", found, repaired);

        var header = applyChanges
            ? $"{found} leaderless lord party(ies); restored {repaired}:"
            : $"{found} leaderless lord party(ies) (nothing changed - omit 'scan' to repair):";

        return header + "\n" + report;
    }
}
