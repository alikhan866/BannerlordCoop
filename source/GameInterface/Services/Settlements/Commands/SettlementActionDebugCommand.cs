using Autofac;
using Common;
using GameInterface.Services.ObjectManager;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Settlements.Commands;

/// <summary>
/// C17 - settlement actions a render-free client can actually perform.
/// </summary>
/// <remarks>
/// THE DIRECT PATH, NOT THE MENU PATH
/// The existing enter_random_castle goes through <c>EncounterManager.StartSettlementEncounter</c>, which is the
/// PLAYER route: it raises an encounter and hands control to a game menu. On a headless client the menu never
/// activates - MenuContext stays at None and GameMenu._menuItems stays empty - so that route dead-ends and takes
/// the capability with it.
///
/// <c>EnterSettlementAction.ApplyForParty</c> is the route every AI lord already uses. No encounter, no menu, no
/// renderer. That is why these commands exist beside the older ones rather than replacing them: the menu path is
/// still the right one to drive on a rendered client, and comparing the two is how a menu-shaped bug gets found.
///
/// A party placed inside a settlement this way has no menu context, so it must be taken out the same way -
/// leave, not a menu option. Mixing the two leaves a party the UI thinks is somewhere it is not.
///
/// WHAT IS NOT HERE, AND WHY
/// Trade needs the inventory screen and talking to a notable needs the conversation system; both are UI-led and
/// belong with C12/C13/C15 behind the menu-activation decision. They are named here rather than quietly omitted,
/// because a capability list that hides its gaps is how a rig gets trusted for things it never did.
/// </remarks>
public class SettlementActionDebugCommand
{
    // Reflected once. The vanilla routine is private, and calling it is still better than reimplementing it:
    // a hand-rolled recruit would skip the relation gain, the notable's slot bookkeeping and the gold path, and
    // would then be testing the rig's idea of recruiting rather than the game's.
    private static readonly MethodInfo RecruitVolunteerMethod =
        typeof(RecruitmentCampaignBehavior).GetMethod(
            "GetRecruitVolunteerFromIndividual",
            BindingFlags.Instance | BindingFlags.NonPublic);

    [CommandLineArgumentFunction("enter", "coop.debug.settlements")]
    public static string Enter(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 2) return "Usage: coop.debug.settlements.enter <partyId> <settlementId>";
        if (!TryResolve(args[0], out MobileParty party, out var error)) return error;

        var settlement = Settlement.Find(args[1]);
        if (settlement == null) return $"Settlement with id {args[1]} not found.";
        if (party.CurrentSettlement == settlement)
            return $"SETTLEMENT_ENTER party={party.StringId} settlement={settlement.StringId} alreadyInside=true";
        if (party.CurrentSettlement != null)
            return $"Party {party.StringId} is already inside {party.CurrentSettlement.StringId}. Leave first.";
        if (party.MapEvent != null) return $"Party {party.StringId} is in a map event.";

        EnterSettlementAction.ApplyForParty(party, settlement);

        return $"SETTLEMENT_ENTER party={party.StringId} settlement={settlement.StringId} " +
               $"inside={party.CurrentSettlement?.StringId ?? "none"}";
    }

    [CommandLineArgumentFunction("leave", "coop.debug.settlements")]
    public static string Leave(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 1) return "Usage: coop.debug.settlements.leave <partyId>";
        if (!TryResolve(args[0], out MobileParty party, out var error)) return error;
        if (party.CurrentSettlement == null)
            return $"SETTLEMENT_LEAVE party={party.StringId} wasInside=none";

        var was = party.CurrentSettlement.StringId;
        LeaveSettlementAction.ApplyForParty(party);

        return $"SETTLEMENT_LEAVE party={party.StringId} wasInside={was} " +
               $"nowInside={party.CurrentSettlement?.StringId ?? "none"}";
    }

    /// <summary>
    /// Every volunteer a settlement is offering, with its price and whether this party may actually take it.
    /// </summary>
    /// <remarks>
    /// coop.debug.hero.volunteers already lists a single notable's slots, but not the two things that decide
    /// whether recruiting will work: the cost, and the vanilla eligibility limit. A recruit that fails for
    /// either reason looks identical to one that failed because the capability is broken.
    /// </remarks>
    [CommandLineArgumentFunction("volunteers_in", "coop.debug.settlements")]
    public static string VolunteersIn(List<string> args)
    {
        if (args.Count < 1 || args.Count > 2)
            return "Usage: coop.debug.settlements.volunteers_in <settlementId> [buyerHeroId]";

        var settlement = Settlement.Find(args[0]);
        if (settlement == null) return $"Settlement with id {args[0]} not found.";

        Hero buyer = null;
        if (args.Count == 2 && !TryResolve(args[1], out buyer, out var buyerError)) return buyerError;

        var result = new StringBuilder();
        result.AppendLine($"SETTLEMENT_VOLUNTEERS settlement={settlement.StringId} " +
                          $"buyer={buyer?.StringId ?? "none"} notables={settlement.Notables.Count}");

        foreach (var notable in settlement.Notables)
        {
            var maximumIndex = buyer == null
                ? -1
                : Campaign.Current.Models.VolunteerModel.MaximumIndexHeroCanRecruitFromHero(
                    buyer, notable, (int)buyer.GetRelation(notable));

            for (var slot = 0; slot < notable.VolunteerTypes.Length; slot++)
            {
                var troop = notable.VolunteerTypes[slot];
                if (troop == null)
                {
                    result.AppendLine($"{notable.StringId}|slot={slot}|troop=none");
                    continue;
                }

                var cost = buyer == null
                    ? -1
                    : (int)Campaign.Current.Models.PartyWageModel.GetTroopRecruitmentCost(troop, buyer, false).ResultNumber;

                result.AppendLine(
                    $"{notable.StringId}|slot={slot}|troop={troop.StringId}|cost={cost}|" +
                    $"eligible={(buyer != null && slot < maximumIndex).ToString().ToLowerInvariant()}");
            }
        }

        return result.ToString();
    }

    [CommandLineArgumentFunction("recruit", "coop.debug.settlements")]
    public static string Recruit(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 3 ||
            !int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) || slot < 0)
            return "Usage: coop.debug.settlements.recruit <partyId> <notableHeroId> <volunteerSlot 0-based>";
        if (!TryResolve(args[0], out MobileParty party, out var partyError)) return partyError;
        if (!TryResolve(args[1], out Hero notable, out var notableError)) return notableError;

        var buyer = party.LeaderHero;
        if (buyer == null) return $"Party {party.StringId} has no leader to recruit with.";
        if (slot >= notable.VolunteerTypes.Length)
            return $"Volunteer slot {slot} is out of range - {notable.StringId} has {notable.VolunteerTypes.Length}.";

        var troop = notable.VolunteerTypes[slot];
        if (troop == null) return $"Volunteer slot {slot} of {notable.StringId} is empty.";

        // useValueAsRelation gets the real relation rather than 0, which would understate what a
        // well-liked buyer may take and refuse recruits vanilla would have allowed.
        var maximumIndex = Campaign.Current.Models.VolunteerModel.MaximumIndexHeroCanRecruitFromHero(
            buyer, notable, (int)buyer.GetRelation(notable));
        if (slot >= maximumIndex)
            return $"{buyer.StringId} may only recruit slots below {maximumIndex} from {notable.StringId}.";

        // ExplainedNumber, not an int - the model reports the price with its modifiers attached.
        var cost = (int)Campaign.Current.Models.PartyWageModel.GetTroopRecruitmentCost(troop, buyer, false).ResultNumber;
        if (buyer.Gold < cost) return $"{buyer.StringId} has {buyer.Gold} gold, recruiting costs {cost}.";

        // Loud rather than falling back to a homemade recruit. A reimplementation that quietly skipped the
        // relation change would still report success, and every test built on it would be testing the wrong
        // thing - which is far more expensive than an unavailable capability.
        if (RecruitVolunteerMethod == null)
            return "The vanilla recruit routine (RecruitmentCampaignBehavior.GetRecruitVolunteerFromIndividual) " +
                   "was not found in this game version.";

        var behavior = Campaign.Current.GetCampaignBehavior<RecruitmentCampaignBehavior>();
        if (behavior == null) return "RecruitmentCampaignBehavior is not registered in this campaign.";

        var goldBefore = buyer.Gold;
        var countBefore = party.MemberRoster.GetTroopCount(troop);
        // The fourth parameter is named bitCode, NOT an index - ApplyInternal takes (number, bitCode) as a
        // pair, so it is a bitmask of which volunteer slots are being taken. Passing the slot number straight
        // through would have been silently wrong in a way that still looked like it worked: slot 0 sets no bits
        // and recruits nobody, and slot 3 recruits slots 0 AND 1.
        var bitCode = 1 << slot;
        RecruitVolunteerMethod.Invoke(behavior, new object[] { party, troop, notable, bitCode });

        return $"SETTLEMENT_RECRUIT party={party.StringId} notable={notable.StringId} slot={slot} " +
               $"troop={troop.StringId} cost={cost} goldBefore={goldBefore} goldAfter={buyer.Gold} " +
               $"troopsBefore={countBefore} troopsAfter={party.MemberRoster.GetTroopCount(troop)} " +
               $"slotNowEmpty={(notable.VolunteerTypes[slot] == null).ToString().ToLowerInvariant()}";
    }

    /// <summary>Names the parts of C17 that need a renderer, so their absence is a stated limit not a silence.</summary>
    [CommandLineArgumentFunction("action_support", "coop.debug.settlements")]
    public static string ActionSupport(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.settlements.action_support";

        return "SETTLEMENT_ACTION_SUPPORT " +
               "enter=direct leave=direct recruit=direct " +
               "trade=needs-inventory-screen talk_to_notables=needs-conversation " +
               "note=trade and conversation are UI-led and share the menu-activation blocker with C12/C13/C15";
    }

    private static bool TryResolve<T>(string id, out T resolved, out string error) where T : class
    {
        resolved = null;
        error = null;

        if (!ContainerProvider.TryGetContainer(out var container) || !container.TryResolve(out IObjectManager objectManager))
        {
            error = "Unable to resolve ObjectManager.";
            return false;
        }
        if (!objectManager.TryGetObject(id, out resolved))
        {
            error = $"{typeof(T).Name} with id {id} not found.";
            return false;
        }

        return true;
    }
}
