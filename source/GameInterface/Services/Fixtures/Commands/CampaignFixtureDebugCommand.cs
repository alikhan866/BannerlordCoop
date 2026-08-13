using Autofac;
using Common;
using Common.Messaging;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MobileParties.Messages.Behavior;
using GameInterface.Services.ObjectManager;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Fixtures.Commands;

/// <summary>
/// C20 - arrange campaign state before a run, and put it back afterwards.
/// </summary>
/// <remarks>
/// Follows the <see cref="Alleys.Commands.AlleyRecruitDebugCommand"/> pattern: capture the original, apply the
/// change, and keep enough to undo it. Generalised, because the rig needs the same guarantee for gold,
/// influence, position, ownership and captivity rather than for one alley regression.
///
/// WHY A FIXTURE AND NOT JUST THE EXISTING SETTERS
/// Every mutation here already exists as a debug command - set_gold_state, restore_position, set_ownerclan,
/// add_influence. What does not exist is putting the world BACK. Without that, a scenario library permanently
/// alters the save it runs on, so the second run starts somewhere the first one left it, and a soak repeating
/// one scenario a hundred times is running a hundred different tests. Restore is the capability; the setters
/// are just what it wraps.
///
/// THE INVARIANT
/// Nothing here can be changed without an active fixture. Every setter refuses when none is open, so a change
/// made through these commands always has an undo recorded. A mutation with no undo is exactly the thing that
/// silently poisons every later run.
///
/// FIRST WRITE WINS
/// The undo for a given target is recorded only the first time it changes. A scenario that sets gold twice must
/// restore to the value from before the fixture, not to the intermediate one it happened to pass through.
///
/// REVERSE ORDER
/// Undos run last-to-first. Ownership and captivity interact - a hero taken prisoner after their settlement
/// changed hands has to be released before the settlement goes back - and reverse order is the only ordering
/// that is right in general rather than by luck.
/// </remarks>
public class CampaignFixtureDebugCommand
{
    private static FixtureUndoLog fixture;

    [CommandLineArgumentFunction("begin", "coop.debug.fixture")]
    public static string Begin(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 0) return "Usage: coop.debug.fixture.begin";
        if (fixture != null)
            return $"A fixture is already active with {fixture.Count} change(s). Restore it first.";

        fixture = new FixtureUndoLog();
        return "FIXTURE_BEGIN changes=0";
    }

    [CommandLineArgumentFunction("set_gold", "coop.debug.fixture")]
    public static string SetGold(List<string> args)
    {
        if (!TryBeginChange(out var error)) return error;
        if (args.Count != 2 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gold) || gold < 0)
            return "Usage: coop.debug.fixture.set_gold <heroId> <non-negative gold>";
        if (!TryGetObjectManager(out var objectManager)) return "Unable to resolve ObjectManager.";
        if (!objectManager.TryGetObject(args[0], out Hero hero)) return $"Hero with id {args[0]} not found.";

        var original = hero.Gold;
        fixture.Record($"gold:{args[0]}", $"hero {hero.StringId} gold {original}", exact: true, undo: () => hero.Gold = original);
        hero.Gold = gold;

        return $"FIXTURE_SET_GOLD hero={hero.StringId} was={original} now={hero.Gold} changes={fixture.Count}";
    }

    [CommandLineArgumentFunction("set_influence", "coop.debug.fixture")]
    public static string SetInfluence(List<string> args)
    {
        if (!TryBeginChange(out var error)) return error;
        if (args.Count != 2 || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var influence))
            return "Usage: coop.debug.fixture.set_influence <clanId> <influence>";
        if (!TryGetObjectManager(out var objectManager)) return "Unable to resolve ObjectManager.";
        if (!objectManager.TryGetObject(args[0], out Clan clan)) return $"Clan with id {args[0]} not found.";

        // Applied as a delta through the action rather than by assigning the field, because that is what makes
        // the _influence scalar store replicate - the same reason coop.debug.clan.add_influence uses it.
        var original = clan.Influence;
        fixture.Record($"influence:{args[0]}", $"clan {clan.StringId} influence {original}", exact: true,
            undo: () => ChangeClanInfluenceAction.Apply(clan, original - clan.Influence));
        ChangeClanInfluenceAction.Apply(clan, influence - clan.Influence);

        return $"FIXTURE_SET_INFLUENCE clan={clan.StringId} was={original} now={clan.Influence} changes={fixture.Count}";
    }

    [CommandLineArgumentFunction("set_position", "coop.debug.fixture")]
    public static string SetPosition(List<string> args)
    {
        if (!TryBeginChange(out var error)) return error;
        if (args.Count < 3 || args.Count > 4 ||
            !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return "Usage: coop.debug.fixture.set_position <partyId> <x> <y> [isOnLand]";
        if (!TryGetObjectManager(out var objectManager)) return "Unable to resolve ObjectManager.";
        if (!objectManager.TryGetObject(args[0], out MobileParty party)) return $"Party with id {args[0]} not found.";

        var original = party.Position;

        // isOnLand is a separate fact from the coordinates, and carrying the ORIGINAL party's value across to a
        // new point is a guess: place a land party on water while still claiming it is on land and it becomes
        // unable to path anywhere - which is exactly the frozen-party fault this rig exists to catch, manufactured
        // by the rig itself. Defaulted to the original because a fixture usually moves a party within its own
        // medium, but stated explicitly so a caller crossing the coastline can say so.
        var isOnLand = original.IsOnLand;
        if (args.Count == 4 && !bool.TryParse(args[3], out isOnLand))
            return "Usage: coop.debug.fixture.set_position <partyId> <x> <y> [isOnLand]";

        fixture.Record($"position:{args[0]}", $"party {party.StringId} at {Format(original.X)},{Format(original.Y)}",
            exact: true, undo: () => Place(party, original.X, original.Y, original.IsOnLand));
        Place(party, x, y, isOnLand);

        return $"FIXTURE_SET_POSITION party={party.StringId} was={Format(original.X)},{Format(original.Y)} " +
               $"now={Format(party.Position.X)},{Format(party.Position.Y)} isOnLand={isOnLand} changes={fixture.Count}";
    }

    [CommandLineArgumentFunction("set_owner", "coop.debug.fixture")]
    public static string SetOwner(List<string> args)
    {
        if (!TryBeginChange(out var error)) return error;
        if (args.Count != 2) return "Usage: coop.debug.fixture.set_owner <settlementId> <heroId>";
        if (!TryGetObjectManager(out var objectManager)) return "Unable to resolve ObjectManager.";

        var settlement = Settlement.Find(args[0]);
        if (settlement == null) return $"Settlement with id {args[0]} not found.";
        if (!objectManager.TryGetObject(args[1], out Hero hero)) return $"Hero with id {args[1]} not found.";

        var originalOwner = settlement.Owner;
        if (originalOwner == null) return $"Settlement {settlement.StringId} has no current owner to restore to.";

        fixture.Record($"owner:{args[0]}", $"settlement {settlement.StringId} owned by {originalOwner.StringId}",
            exact: true, undo: () => ChangeOwnerOfSettlementAction.ApplyByGift(settlement, originalOwner));
        ChangeOwnerOfSettlementAction.ApplyByGift(settlement, hero);

        return $"FIXTURE_SET_OWNER settlement={settlement.StringId} was={originalOwner.StringId} " +
               $"now={settlement.Owner?.StringId ?? "none"} changes={fixture.Count}";
    }

    [CommandLineArgumentFunction("set_captive", "coop.debug.fixture")]
    public static string SetCaptive(List<string> args)
    {
        if (!TryBeginChange(out var error)) return error;
        if (args.Count != 2) return "Usage: coop.debug.fixture.set_captive <heroId> <captorPartyId>";
        if (!TryGetObjectManager(out var objectManager)) return "Unable to resolve ObjectManager.";
        if (!objectManager.TryGetObject(args[0], out Hero hero)) return $"Hero with id {args[0]} not found.";
        if (!objectManager.TryGetObject(args[1], out MobileParty captor)) return $"Party with id {args[1]} not found.";
        if (hero.IsPrisoner) return $"Hero {hero.StringId} is already a prisoner.";

        // Marked INEXACT deliberately. EndCaptivityAction is not the clean inverse of TakePrisonerAction - it
        // runs release consequences (relation and ransom effects) that being taken prisoner never applied. The
        // restore is the best available, and the fixture reports that it was approximate rather than claiming
        // the world came back untouched. A silent imperfect restore is how a soak drifts without anyone seeing.
        fixture.Record($"captive:{args[0]}", $"hero {hero.StringId} was free", exact: false,
            undo: () => { if (hero.IsPrisoner) EndCaptivityAction.ApplyByReleasedAfterBattle(hero); });
        TakePrisonerAction.Apply(captor.Party, hero);

        return $"FIXTURE_SET_CAPTIVE hero={hero.StringId} captor={captor.StringId} " +
               $"isPrisoner={hero.IsPrisoner} exact=false changes={fixture.Count}";
    }

    // C21. Seeding is a mutation like any other here - it changes where the campaign's roll sequence is - so it
    // goes through the same undo machinery instead of beside it. A scenario that seeded and never restored
    // would leave the next one drawing from a sequence it did not choose, which is the exact flakiness the
    // capability exists to remove.
    [CommandLineArgumentFunction("set_random_seed", "coop.debug.fixture")]
    public static string SetRandomSeed(List<string> args)
    {
        if (!TryBeginChange(out var error)) return error;
        if (args.Count != 1 || !uint.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed))
            return "Usage: coop.debug.fixture.set_random_seed <seed>";

        var original = CampaignRandomControl.CaptureState();
        if (original == null)
            return "Could not read the campaign random state - the engine's internals have moved.";

        fixture.Record("random:campaign", $"generator state {CampaignRandomControl.Format(original)}",
            exact: true, undo: () => CampaignRandomControl.TryRestoreState(original));
        CampaignRandomControl.Seed(seed);

        return $"FIXTURE_SET_RANDOM_SEED seed={seed} was={CampaignRandomControl.Format(original)} " +
               $"now={CampaignRandomControl.Format(CampaignRandomControl.CaptureState())} changes={fixture.Count}";
    }

    /// <summary>Reports both generators, so a run that stays flaky under a fixed seed can be diagnosed.</summary>
    [CommandLineArgumentFunction("random_state", "coop.debug.fixture")]
    public static string RandomState(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.fixture.random_state";

        return $"FIXTURE_RANDOM_STATE {CampaignRandomControl.Describe()}";
    }

    [CommandLineArgumentFunction("state", "coop.debug.fixture")]
    public static string State(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.fixture.state";
        if (fixture == null) return "FIXTURE_STATE active=false";

        var result = new StringBuilder();
        var approximate = fixture.Entries.Count(entry => !entry.Exact);
        result.AppendLine($"FIXTURE_STATE active=true changes={fixture.Count} approximate={approximate}");
        foreach (var entry in fixture.Entries)
            result.AppendLine($"{entry.Key}: restores to {entry.Description} exact={entry.Exact}");

        return result.ToString();
    }

    [CommandLineArgumentFunction("restore", "coop.debug.fixture")]
    public static string Restore(List<string> args)
    {
        if (ModInformation.IsClient) return "Run this command on the server.";
        if (args.Count != 0) return "Usage: coop.debug.fixture.restore";
        if (fixture == null) return "FIXTURE_RESTORE active=false - nothing to restore.";

        var outcome = fixture.Restore();

        // Cleared even when some undos failed. Keeping it would block the next begin, and one unrestorable
        // change would then stop an entire soak; the FAILED token below is how the caller learns the world is
        // dirty, and the scenario runner already knows what to do with that.
        fixture = null;

        var summary = $"FIXTURE_RESTORE restored={outcome.Restored} of={outcome.Attempted} " +
                      $"exact={outcome.Exact.ToString().ToLowerInvariant()}";
        if (outcome.Approximate.Count > 0) summary += $" approximate={string.Join(",", outcome.Approximate)}";
        if (outcome.Failures.Count > 0) summary += $" FAILED={string.Join(" | ", outcome.Failures)}";
        return summary;
    }

    private static bool TryBeginChange(out string error)
    {
        if (ModInformation.IsClient) { error = "Run this command on the server."; return false; }
        if (fixture == null)
        {
            error = "No fixture is active. Run coop.debug.fixture.begin first so the change can be undone.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Places a party and holds it there, the same way coop.debug.mobileparty.restore_position does.</summary>
    private static void Place(MobileParty party, float x, float y, bool isOnLand)
    {
        party.Position = new CampaignVec2(new TaleWorlds.Library.Vec2(x, y), isOnLand);
        party.SetMoveModeHold();
        party.ResetNavigationToHold();
        MessageBroker.Instance.Publish(
            typeof(CampaignFixtureDebugCommand),
            new PartyBehaviorChangeAttempted(
                party,
                forcePosition: true,
                isCurrentlyAtSea: party.IsCurrentlyAtSea,
                resetMovementToHold: true));
    }

    private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryGetObjectManager(out IObjectManager objectManager)
    {
        objectManager = null;
        if (!ContainerProvider.TryGetContainer(out var container)) return false;

        return container.TryResolve(out objectManager);
    }
}
