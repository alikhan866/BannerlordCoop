#if DEBUG
using System;
using System.Globalization;
using System.Text;
using Common.Commands;
using GameInterface.Services.ObjectManager;
using Helpers;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.GameDebug.Commands;

/// <summary>
/// [Debug] Troop XP as the party actually holds it, and the vanilla shared-XP distribution on demand. Written for
/// M12 ("discarding items after a battle loses the troops' XP"): the coop path can hand the party its donated XP
/// correctly and the roster still not move, because vanilla only gives XP to troops that can still use it. This
/// separates "the XP never arrived" from "the troops could not take it".
/// </summary>
internal static class TroopXpDebugCommands
{
    private static CoopCommandResult Succeeded(string output) => new CoopCommandResult(true, output);

    private static CoopCommandResult Failed(string output) => new CoopCommandResult(false, output, "command_failed");

    private static string Report(MobileParty party, string prefix)
    {
        var roster = party.MemberRoster;
        var text = new StringBuilder(prefix);
        int total = 0;
        text.Append(" party=").Append(party.StringId).Append(" men=").Append(roster.TotalManCount.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < roster.Count; i++)
        {
            var element = roster.GetElementCopyAtIndex(i);
            if (element.Character == null) continue;
            total += element.Xp;
            bool canGain = false;
            int upgradeXpCost = 0;
            try { canGain = MobilePartyHelper.CanTroopGainXp(party.Party, element.Character, out upgradeXpCost); } catch (Exception) { }
            text.Append(" | ").Append(element.Character.StringId)
                .Append(" n=").Append(element.Number.ToString(CultureInfo.InvariantCulture))
                .Append(" xp=").Append(element.Xp.ToString(CultureInfo.InvariantCulture))
                .Append(" canGain=").Append(canGain ? "1" : "0")
                .Append(" upgradeCost=").Append(upgradeXpCost.ToString(CultureInfo.InvariantCulture));
        }
        text.Append(" || totalXp=").Append(total.ToString(CultureInfo.InvariantCulture));
        try
        {
            text.Append(" partyCanAbsorb=").Append(MobilePartyHelper.GetMaximumXpAmountPartyCanGet(party).ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception) { }
        return text.ToString();
    }

    // coop.debug.mobile_party.troop_xp
    /// <summary>Per-troop XP of a party, whether each troop can still gain XP, and what the party can absorb in total.</summary>
    public sealed class TroopXpCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.mobile_party";

        public string Name => "troop_xp";

        public string Description => "Per-troop XP of a party, each troop's can-gain-XP flag and upgrade cost, and the party's remaining XP capacity.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("party_id", "The registered mobile party id.", isRequired: true),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!ContainerProvider.TryResolve<IObjectManager>(out var objectManager)) return Failed("No object manager.");
            if (!objectManager.TryGetObject(args[0], out MobileParty party) || party == null)
                return Failed("Party " + args[0] + " not found.");
            return Succeeded(Report(party, "TROOP_XP"));
        }
    }

    // coop.debug.mobile_party.add_shared_xp
    /// <summary>
    /// Runs vanilla's <c>MobilePartyHelper.PartyAddSharedXp</c> on a party and reports the roster before and after,
    /// which is exactly what the donated-loot XP does once the server accepts it.
    /// </summary>
    public sealed class AddSharedXpCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.mobile_party";

        public string Name => "add_shared_xp";

        public string Description => "Gives a party shared troop XP through the vanilla helper and reports the roster before and after.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("party_id", "The registered mobile party id.", isRequired: true),
            new ExpectedArgs("xp", "Shared XP to add.", isRequired: true),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (Common.ModInformation.IsClient) return Failed("Run this command on the server.");
            if (!ContainerProvider.TryResolve<IObjectManager>(out var objectManager)) return Failed("No object manager.");
            if (!objectManager.TryGetObject(args[0], out MobileParty party) || party == null)
                return Failed("Party " + args[0] + " not found.");
            if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float xp))
                return Failed("XP must be a number.");
            string before = Report(party, "BEFORE");
            MobilePartyHelper.PartyAddSharedXp(party, xp);
            return Succeeded(before + "\n" + Report(party, "AFTER") + "\nadded=" + xp.ToString("0.#", CultureInfo.InvariantCulture));
        }
    }
}
#endif
