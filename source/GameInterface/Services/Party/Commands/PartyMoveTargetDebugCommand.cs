using Autofac;
using Common;
using Common.Messaging;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Party.Commands;

/// <summary>
/// C14's two missing move targets, issued from the CLIENT.
/// </summary>
/// <remarks>
/// C14 asks for movement orders to a position, a settlement or a party. Only the position was reachable from a
/// driven client: move_offset is client-side, but move_to_settlement answers "Command can only be run on the
/// server" and no move-to-party command existed at all.
///
/// WHY A SERVER-SIDE ORDER IS NOT THE SAME CAPABILITY
/// The rig exists to test what a CLIENT can do. Ordering a party to a settlement on the server and watching it
/// arrive proves the server can move a party - it says nothing about whether a client's order survives the
/// round trip, which is the thing that actually breaks. Reaching a settlement by moving to its coordinates
/// instead is a different act again: it never sets TargetSettlement, so none of the arrival behaviour a player
/// gets is exercised.
///
/// These mirror move_offset exactly - act on the local player's own party, then publish
/// PartyBehaviorChangeAttempted so the change goes to the server the same way a player's would. A client
/// driving someone else's party is not a capability, it is a desync, so neither command takes a party id for
/// the mover.
/// </remarks>
public class PartyMoveTargetDebugCommand
{
    [CommandLineArgumentFunction("order_to_settlement", "coop.debug.mobileparty")]
    public static string OrderToSettlement(List<string> args)
    {
        if (!ModInformation.IsClient) return "Command can only be run on a client.";
        if (args.Count != 1) return "Usage: coop.debug.mobileparty.order_to_settlement <settlementId>";
        if (!TryGetDrivenParty(out var party, out var error)) return error;

        var settlement = Settlement.Find(args[0]);
        if (settlement == null) return $"Settlement with id {args[0]} not found.";

        party.SetMoveGoToSettlement(settlement, NavigationFor(party), isTargetingThePort: false);
        Publish(party);

        // TargetSettlement is reported because it is what proves the ORDER took, as distinct from the party
        // merely drifting toward the right coordinates - which is what moving by offset would have looked like.
        return $"ORDER_TO_SETTLEMENT party={party.StringId} settlement={settlement.StringId} " +
               $"target={party.TargetSettlement?.StringId ?? "none"} " +
               $"defaultBehavior={party.DefaultBehavior} " +
               $"from={party.Position.X:R},{party.Position.Y:R}";
    }

    [CommandLineArgumentFunction("order_to_party", "coop.debug.mobileparty")]
    public static string OrderToParty(List<string> args)
    {
        if (!ModInformation.IsClient) return "Command can only be run on a client.";
        if (args.Count != 1) return "Usage: coop.debug.mobileparty.order_to_party <targetPartyId>";
        if (!TryGetDrivenParty(out var party, out var error)) return error;

        if (!ContainerProvider.TryGetContainer(out var container) ||
            !container.TryResolve(out IObjectManager objectManager))
            return "Unable to resolve ObjectManager.";
        if (!objectManager.TryGetObject(args[0], out MobileParty target))
            return $"Party with id {args[0]} not found.";
        if (target == party) return "A party cannot be ordered to follow itself.";
        if (!target.IsActive) return $"Party {target.StringId} is not active.";

        // Escort, not engage. C14 is a MOVEMENT order - SetMoveEngageParty starts a fight, which would make
        // every move-to-party test also a battle test and destroy the fixture it was run on.
        party.SetMoveEscortParty(target, NavigationFor(party), isTargetingPort: false);
        Publish(party);

        return $"ORDER_TO_PARTY party={party.StringId} target={target.StringId} " +
               $"targetParty={party.TargetParty?.StringId ?? "none"} " +
               $"defaultBehavior={party.DefaultBehavior} " +
               $"from={party.Position.X:R},{party.Position.Y:R} " +
               $"targetAt={target.Position.X:R},{target.Position.Y:R}";
    }

    private static bool TryGetDrivenParty(out MobileParty party, out string error)
    {
        error = null;
        party = Hero.MainHero?.PartyBelongedTo;

        if (party == null) { error = "The local player hero has no party."; return false; }
        if (!party.IsActive) { error = $"Party {party.StringId} is not active."; return false; }
        if (party.CurrentSettlement != null)
        {
            error = $"Party {party.StringId} is inside {party.CurrentSettlement.StringId} - leave before ordering a move.";
            return false;
        }

        return true;
    }

    private static MobileParty.NavigationType NavigationFor(MobileParty party) =>
        party.IsCurrentlyAtSea ? MobileParty.NavigationType.Naval : MobileParty.NavigationType.Default;

    private static void Publish(MobileParty party) =>
        MessageBroker.Instance.Publish(typeof(PartyMoveTargetDebugCommand), new PartyBehaviorChangeAttempted(party));
}
