#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Common;
using Common.Commands;
using Common.Util;
using GameInterface;
using GameInterface.Services.Entity;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using Missions.Diagnostics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Battles.DuelRig;

/// <summary>
/// Console surface of the PvP duel rig: <c>coop.debug.duel.*</c>. Drives the local player agent through scripted
/// input (<see cref="DuelDriver"/>) and records both players' animation, health and blows on a shared clock
/// (<see cref="AnimationTimeline"/> in duel mode, <see cref="DuelEvents"/>), so <c>analyze_duel.py</c> can diff the
/// two machines.
/// </summary>
internal static class DuelDebugCommands
{
    private static CoopCommandResult Succeeded(string output) =>
        new CoopCommandResult(true, output);

    private static CoopCommandResult Failed(string output) =>
        new CoopCommandResult(false, output, "command_failed");

    private static bool TryGetMission(out Mission mission, out CoopCommandResult failure)
    {
        mission = Mission.Current;
        failure = null;
        if (mission == null || mission.GetMissionBehavior<CoopMissionController>() == null)
        {
            failure = Failed("No active coop mission.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// The hero THIS client plays, from the player registry (controller id -> Player.HeroId), falling back to
    /// Hero.MainHero. Run 14: Hero.MainHero read as the companion "Jian" for a moment after the battle fixture
    /// started, so unhorse stripped the wrong hero and the player spawned mounted.
    /// </summary>
    private static Hero ResolveLocalHero(out string how)
    {
        try
        {
            if (ContainerProvider.TryResolve<IControllerIdProvider>(out var controllerIds)
                && ContainerProvider.TryResolve<IPlayerManager>(out var players)
                && players.TryGetPlayer(controllerIds.ControllerId, out Player player)
                && !string.IsNullOrEmpty(player.HeroId)
                && ContainerProvider.TryResolve<IObjectManager>(out var objects)
                && objects.TryGetObject(player.HeroId, out Hero hero)
                && hero != null)
            {
                how = "player registry";
                return hero;
            }
        }
        catch (Exception)
        {
            // treated as "not bound yet" below
        }
        // No fallback to Hero.MainHero: right after a client joins it is still the save's own player (Jian on
        // pvp_duel) until the coop binding switches it, and a fixture that acts on that hero strips the wrong
        // one (runs 14 and p7-kill2). The caller retries until the registry knows this client's hero.
        how = "unbound";
        return null;
    }

    // coop.debug.duel.face <controller_id>
    /// <summary>Turns the local player toward the named player's hero and walks up to melee reach.</summary>
    public sealed class DuelFaceCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.duel";

        public string Name => "face";

        public string Description => "Turns the local player agent toward another player's hero and walks up to melee reach.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("controller_id", "The opponent's controller id."),
            new ExpectedArgs("stand_off_m", "Stop this far from the opponent (default 1.4 m on foot, 3 m mounted; javelins want ~12).", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!TryGetMission(out Mission mission, out CoopCommandResult failure)) return failure;
            if (mission.MainAgent == null) return Failed("No local player agent in the mission yet.");
            float? standOff = null;
            string raw = args.ElementAtOrDefault(1);
            if (!string.IsNullOrEmpty(raw))
            {
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float metres) || metres <= 0f)
                    return Failed("stand_off_m must be a positive number.");
                standOff = metres;
            }
            return Succeeded(DuelDriver.GetOrAttach(mission).Face(args[0], standOff));
        }
    }

    // coop.debug.duel.script <name> [seed]
    /// <summary>Runs a named input script (swings, thrusts, blocks, kick_bash, mixed, hold) on the local player agent.</summary>
    public sealed class DuelScriptCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.duel";

        public string Name => "script";

        public string Description => "Runs a named input script on the local player agent: swings, thrusts, blocks, kick_bash, mixed, footwork, ride, ride_swing, couch, throw or hold.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("script", "swings, thrusts, blocks, kick_bash, mixed, footwork, ride, ride_swing, couch, throw or hold."),
            new ExpectedArgs("seed", "Seed for the mixed script (default 1).", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!TryGetMission(out Mission mission, out CoopCommandResult failure)) return failure;
            if (mission.MainAgent == null) return Failed("No local player agent in the mission yet.");

            int seed = 1;
            string seedArgument = args.ElementAtOrDefault(1);
            if (seedArgument != null && !int.TryParse(seedArgument, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                return Failed("seed must be a whole number, got '" + seedArgument + "'.");

            string result = DuelDriver.GetOrAttach(mission).RunScript(args[0], seed);
            return result.StartsWith("Unknown", StringComparison.Ordinal) || result.StartsWith("No local", StringComparison.Ordinal)
                ? Failed(result)
                : Succeeded(result);
        }
    }

    // coop.debug.duel.state
    public sealed class DuelStateCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.duel";

        public string Name => "state";

        public string Description => "Reports the duel driver's phase, script step, opponent, distance and health on this machine.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!TryGetMission(out Mission mission, out CoopCommandResult failure)) return failure;
            var driver = mission.GetMissionBehavior<DuelDriver>();
            return Succeeded(driver == null ? "DUEL phase=Idle (driver not attached)" : driver.State());
        }
    }

    // coop.debug.duel.stop
    public sealed class DuelStopCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.duel";

        public string Name => "stop";

        public string Description => "Releases all scripted input and gives the player agent back to the mouse.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!TryGetMission(out Mission mission, out CoopCommandResult failure)) return failure;
            var driver = mission.GetMissionBehavior<DuelDriver>();
            return Succeeded(driver == null ? "Nothing was running." : driver.Stop());
        }
    }

    // coop.debug.duel.record <start|snapshot|stop>
    /// <summary>
    /// Records both players' agents every tick (timeline duel mode) plus the blow event log. <c>snapshot</c> prints
    /// both; <c>stop</c> prints both and stops recording.
    /// </summary>
    public sealed class DuelRecordCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.duel";

        public string Name => "record";

        public string Description => "Starts, snapshots or stops the per-tick duel recording of both player agents and the blow event log.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("action", "start, snapshot or stop."),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!TryGetMission(out Mission mission, out CoopCommandResult failure)) return failure;

            switch (args[0].ToLowerInvariant())
            {
                case "start":
                {
                    if (!ContainerProvider.TryResolve<INetworkAgentRegistry>(out var registry))
                        return Failed("No agent registry.");

                    var ids = new List<Guid>();
                    var described = new List<string>();
                    Agent me = mission.MainAgent;
                    CoopAgentInfo myInfo = null;
                    if (me != null && registry.TryGetAgentInfo(me, out myInfo) && myInfo != null)
                    {
                        ids.Add(myInfo.AgentId);
                        described.Add("me=" + DuelEvents.Id8(myInfo.AgentId));
                    }

                    var driver = mission.GetMissionBehavior<DuelDriver>();
                    string opponentController = driver?.OpponentControllerId;
                    if (!string.IsNullOrEmpty(opponentController)
                        && DuelDriver.TryResolvePlayerHeroAgent(opponentController, mission, out _, out Guid opponentId))
                    {
                        ids.Add(opponentId);
                        described.Add("opponent=" + DuelEvents.Id8(opponentId));
                    }

                    if (ids.Count == 0)
                        return Failed("Nothing to record: no local player agent, and no opponent set with coop.debug.duel.face.");

                    AnimationTimeline.StartDuel(ids);
                    DuelEvents.Start();
                    return Succeeded("Duel recording STARTED for " + string.Join(" ", described) +
                                     (ids.Count == 1 ? " (opponent not resolved yet - run face first for both agents)" : ""));
                }
                case "snapshot":
                    return Succeeded(AnimationTimeline.Snapshot(stop: false) + "\n" + DuelEvents.Snapshot(stop: false));
                case "stop":
                    return Succeeded(AnimationTimeline.Snapshot(stop: true) + "\n" + DuelEvents.Snapshot(stop: true));
                default:
                    return Failed("Usage: coop.debug.duel.record <start|snapshot|stop>");
            }
        }
    }

    // coop.debug.duel.unhorse
    /// <summary>
    /// Removes the horse and harness from the local hero's battle equipment so the NEXT battle spawn is on foot.
    /// Run before start_attack_mission: the owning client builds its own hero from this equipment and the spawn
    /// record carries the result to every other machine, so nobody has to dismount mid-duel (and no riderless
    /// mount is left standing in the fight).
    /// </summary>
    public sealed class DuelUnhorseCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.duel";

        public string Name => "unhorse";

        public string Description => "Strips the horse and harness from the local player's battle equipment so the next battle spawn is on foot.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            Hero hero = ResolveLocalHero(out string how);
            if (hero == null) return Failed("Local hero not bound yet (player registry has no hero for this controller); retry.");
            Equipment equipment = hero.BattleEquipment;
            string horse = equipment[EquipmentIndex.Horse].Item?.StringId ?? "[EMPTY]";
            string harness = equipment[EquipmentIndex.HorseHarness].Item?.StringId ?? "[EMPTY]";
            // Every Equipment slot write is transpiled into a sync message; a client-side write outside
            // AllowedThread is the kind the sync layer treats as foreign, so mark this one as ours.
            using (new AllowedThread())
            {
                equipment[EquipmentIndex.Horse] = EquipmentElement.Invalid;
                equipment[EquipmentIndex.HorseHarness] = EquipmentElement.Invalid;
            }
            return Succeeded("Unhorsed " + hero.Name + " (" + how + "): removed " + horse + " + " + harness +
                             "; horse slot now " + (equipment[EquipmentIndex.Horse].Item?.StringId ?? "[EMPTY]"));
        }
    }

    // coop.debug.duel.arm <weapon0_item_id> [weapon1] [weapon2] [weapon3]   ('-' = empty slot)
    /// <summary>
    /// Sets the local hero's four battle weapon slots (unnamed slots are emptied), so the NEXT spawn carries exactly
    /// the kit under test: sword and shield for the foot duel, a lance or glaive for the mounted one, javelin stacks
    /// for the throw exchange. Run 7 fought with the heroes' own polearms (a lance and a spear): thrust-only
    /// weapons, so the left/right swing flags did nothing and half the scripted attacks never happened.
    /// </summary>
    public sealed class DuelArmCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.duel";

        public string Name => "arm";

        public string Description => "Sets the local player's four battle weapon slots for the next spawn ('-' empties a slot; unnamed slots are emptied).";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("weapon0_item_id", "ItemObject string id for slot 0, e.g. vlandia_sword_4_t4 or vlandia_lance_2_t4."),
            new ExpectedArgs("weapon1_item_id", "Slot 1, e.g. reinforced_kite_shield ('-' = empty).", false),
            new ExpectedArgs("weapon2_item_id", "Slot 2 ('-' = empty).", false),
            new ExpectedArgs("weapon3_item_id", "Slot 3 ('-' = empty).", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            Hero hero = ResolveLocalHero(out string how);
            if (hero == null) return Failed("Local hero not bound yet (player registry has no hero for this controller); retry.");
            var items = new ItemObject[4];
            for (int i = 0; i < 4; i++)
            {
                string id = args.ElementAtOrDefault(i);
                if (string.IsNullOrEmpty(id) || id == "-" || string.Equals(id, "none", StringComparison.OrdinalIgnoreCase)) continue;
                items[i] = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<ItemObject>(id);
                if (items[i] == null) return Failed("No item '" + id + "'.");
            }
            if (items[0] == null) return Failed("Slot 0 must name a weapon.");
            Equipment equipment = hero.BattleEquipment;
            var before = new System.Text.StringBuilder();
            for (EquipmentIndex i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumAllWeaponSlots; i++)
                before.Append(equipment[i].Item?.StringId ?? "[EMPTY]").Append(' ');
            using (new AllowedThread())
            {
                for (int i = 0; i < 4; i++)
                    equipment[EquipmentIndex.Weapon0 + i] = items[i] == null ? EquipmentElement.Invalid : new EquipmentElement(items[i]);
            }
            return Succeeded("Armed " + hero.Name + " (" + how + ") with " +
                             string.Join(" + ", items.Select(it => it?.StringId ?? "-")) + " (was: " + before.ToString().TrimEnd() + ")");
        }
    }

    // coop.debug.duel.boundary <off|on>
    /// <summary>
    /// Turns the battlefield boundary punishment off (or back on) on this machine. A scripted horse that overshoots
    /// the field is otherwise retreated after the countdown, and with one duellist gone the battle resolves for
    /// everybody (runs p6-ride and m-lance-before ended that way).
    /// </summary>
    public sealed class DuelBoundaryCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.duel";

        public string Name => "boundary";

        public string Description => "Disables ('off') or restores ('on') the battlefield boundary punishment on this machine for the test rig.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("mode", "off = nobody is retreated for leaving the field; on = vanilla behaviour."),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            string mode = args[0];
            if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase))
            {
                GameInterface.Services.MapEvents.Patches.BattleBoundaryOverride.SuppressPunishment = true;
                return Succeeded("BOUNDARY_PUNISHMENT off (test rig)");
            }
            if (string.Equals(mode, "on", StringComparison.OrdinalIgnoreCase))
            {
                GameInterface.Services.MapEvents.Patches.BattleBoundaryOverride.SuppressPunishment = false;
                return Succeeded("BOUNDARY_PUNISHMENT on");
            }
            return Failed("Usage: coop.debug.duel.boundary <off|on>");
        }
    }

    // coop.debug.duel.heal [limit]
    /// <summary>
    /// Raises the health limit of the local player agent AND of the opponent's puppet on this machine, and fills
    /// both. Run 9: a defender standing bare took 142 of 167 hit points in one swings exchange and was dead two
    /// exchanges later, which ends the duel (Mission.MainAgent turns null). Applied on both machines before every
    /// exchange, so health starts equal everywhere and the end-of-exchange owner/puppet comparison stays a mirror test.
    /// </summary>
    public sealed class DuelHealCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.duel";

        public string Name => "heal";

        public string Description => "Sets the health limit of the local player agent and the opponent puppet on this machine and fills both (default 2000).";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("limit", "New health limit and health (default 2000).", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!TryGetMission(out Mission mission, out CoopCommandResult failure)) return failure;
            float limit = 2000f;
            string raw = args.ElementAtOrDefault(0);
            if (!string.IsNullOrEmpty(raw) && !float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out limit))
                return Failed("Invalid limit '" + raw + "'.");
            var driver = DuelDriver.GetOrAttach(mission);
            var text = new System.Text.StringBuilder();
            foreach (Agent rider in new[] { mission.MainAgent, driver.ResolveOpponentForCommands() })
            {
                // The horses too: a mounted exchange lands most blows on the horse, and a dead horse ends the ride.
                foreach (Agent agent in new[] { rider, rider?.MountAgent })
                {
                    if (agent == null || !agent.IsActive()) continue;
                    float before = agent.Health;
                    agent.HealthLimit = limit;
                    agent.Health = limit;
                    text.Append(DuelEvents.Id8(agent)).Append(agent.IsMount ? "(horse) " : " ").Append(before.ToString("0", CultureInfo.InvariantCulture))
                        .Append("->").Append(agent.Health.ToString("0", CultureInfo.InvariantCulture)).Append(' ');
                }
            }
            if (text.Length == 0) return Failed("No live player agent to heal.");
            return Succeeded("Healed: " + text.ToString().TrimEnd());
        }
    }
}
#endif
