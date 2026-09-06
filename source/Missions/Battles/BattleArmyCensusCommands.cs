#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Common.Commands;
using GameInterface;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Missions.Battles;

/// <summary>
/// Census commands for the PvP army rig (PVP-ARMY-SYNC-PLAN A0): what each machine holds per side and per formation,
/// printed so two machines' outputs can be diffed on a shared clock. Both read the mission on the game thread and
/// change nothing.
/// </summary>
internal static class BattleArmyCensusCommands
{
    private static CoopCommandResult Succeeded(string output) => new CoopCommandResult(true, output);

    private static CoopCommandResult Failed(string output) => new CoopCommandResult(false, output, "command_failed");

    private static string F(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    // coop.debug.battle.formations
    /// <summary>
    /// Per side and formation: live humans, their centroid and mean speed, from the agents themselves (a puppet's
    /// Formation object exists on every machine, so the same grouping works for owned and remote troops). The
    /// hold-then-charge scenario reads the holding side's centroids on both machines: they must stand still there
    /// while the charging side's move (orders leak, hypothesis A12).
    /// </summary>
    public sealed class BattleFormationsCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.battle";

        public string Name => "formations";

        public string Description => "Per side and formation index: live humans, centroid and mean speed on this machine.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            Mission mission = Mission.Current;
            if (mission == null || mission.GetMissionBehavior<CoopMissionController>() == null)
                return Failed("No active coop mission.");

            var groups = new SortedDictionary<string, (int n, double x, double y, double v)>(StringComparer.Ordinal);
            var captains = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Agent agent in mission.Agents)
            {
                if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;
                string side = agent.Team?.Side.ToString() ?? "None";
                int formationIndex = agent.Formation?.Index ?? -1;
                string key = side + "/f" + formationIndex.ToString(CultureInfo.InvariantCulture);
                if (!captains.ContainsKey(key))
                {
                    Agent captain = null;
                    try { captain = agent.Formation?.Captain; } catch (Exception) { }
                    captains[key] = captain == null ? "-" : (captain.Character?.StringId ?? captain.Name ?? "?").Replace(' ', '_');
                }
                groups.TryGetValue(key, out var g);
                Vec3 p = agent.Position;
                float speed = 0f;
                try { speed = agent.Velocity.Length; } catch (Exception) { }
                groups[key] = (g.n + 1, g.x + p.x, g.y + p.y, g.v + speed);
            }

            var text = new StringBuilder("FORMATIONS ");
            foreach (var pair in groups)
            {
                var g = pair.Value;
                text.Append(pair.Key).Append(" n=").Append(g.n.ToString(CultureInfo.InvariantCulture))
                    .Append(" x=").Append(F((float)(g.x / g.n)))
                    .Append(" y=").Append(F((float)(g.y / g.n)))
                    .Append(" v=").Append(F((float)(g.v / g.n)))
                    .Append(" cap=").Append(captains.TryGetValue(pair.Key, out var cap) ? cap : "-").Append("; ");
            }
            return Succeeded(text.ToString().TrimEnd());
        }
    }

    // coop.debug.battle.agent_census
    /// <summary>
    /// Per side: live humans, how many carry a network identity, the troop composition (counts per character id)
    /// and a hash over every registered agent's (network id, character, side, mounted). Two machines that hold the
    /// same battle print the same hashes; a differing hash with equal counts is a wrong character or side on one
    /// agent, a differing count is a missing or extra one (spawn mirror, hypothesis A1 / milestone A1).
    /// </summary>
    public sealed class BattleAgentCensusCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.battle";

        public string Name => "agent_census";

        public string Description => "Per side: live humans, registered count, composition by character and an identity hash for cross-machine comparison.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            Mission mission = Mission.Current;
            if (mission == null || mission.GetMissionBehavior<CoopMissionController>() == null)
                return Failed("No active coop mission.");
            if (!ContainerProvider.TryResolve<INetworkAgentRegistry>(out var registry))
                return Failed("No agent registry.");

            var bySide = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            var composition = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
            var weapons = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
            var health = new Dictionary<string, (double limit, double current, int n)>(StringComparer.Ordinal);
            int unregistered = 0, humans = 0;
            foreach (Agent agent in mission.Agents)
            {
                if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;
                humans++;
                string side = agent.Team?.Side.ToString() ?? "None";
                string character = agent.Character?.StringId ?? "-";
                if (!composition.TryGetValue(side, out var counts))
                    composition[side] = counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                counts.TryGetValue(character, out int c);
                counts[character] = c + 1;
                if (!agent.IsHero)
                {
                    health.TryGetValue(side, out var h);
                    float limit = 0f, current = 0f;
                    try { limit = agent.HealthLimit; current = agent.Health; } catch (Exception) { }
                    health[side] = (h.limit + limit, h.current + current, h.n + 1);
                }
                if (!weapons.TryGetValue(side, out var wcounts))
                    weapons[side] = wcounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                for (EquipmentIndex slot = EquipmentIndex.WeaponItemBeginSlot; slot < EquipmentIndex.NumAllWeaponSlots; slot++)
                {
                    string item = null;
                    try { item = agent.Equipment[slot].Item?.StringId; } catch (Exception) { }
                    if (item == null) continue;
                    wcounts.TryGetValue(item, out int wc);
                    wcounts[item] = wc + 1;
                }
                if (!registry.TryGetAgentInfo(agent, out CoopAgentInfo info) || info == null)
                {
                    unregistered++;
                    continue;
                }
                if (!bySide.TryGetValue(side, out var ids))
                    bySide[side] = ids = new List<string>();
                ids.Add(info.AgentId.ToString("N") + ":" + character + ":" + (agent.HasMount ? "m" : "f"));
            }

            var text = new StringBuilder("AGENT_CENSUS humans=").Append(humans.ToString(CultureInfo.InvariantCulture))
                .Append(" unregistered=").Append(unregistered.ToString(CultureInfo.InvariantCulture));
            using (var sha = SHA1.Create())
            {
                foreach (var side in composition.Keys)
                {
                    bySide.TryGetValue(side, out var ids);
                    ids = ids ?? new List<string>();
                    ids.Sort(StringComparer.Ordinal);
                    byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", ids)));
                    string hash = BitConverter.ToString(digest, 0, 8).Replace("-", "").ToLowerInvariant();
                    int n = composition[side].Values.Sum();
                    text.Append(" | ").Append(side).Append(" n=").Append(n.ToString(CultureInfo.InvariantCulture))
                        .Append(" registered=").Append(ids.Count.ToString(CultureInfo.InvariantCulture))
                        .Append(" hash=").Append(hash)
                        .Append(" troops=").Append(string.Join(",", composition[side].Select(kv => kv.Key + "x" + kv.Value.ToString(CultureInfo.InvariantCulture))));
                    if (health.TryGetValue(side, out var hs) && hs.n > 0)
                        text.Append(" troopHpLimitMean=").Append(F((float)(hs.limit / hs.n)))
                            .Append(" troopHpMean=").Append(F((float)(hs.current / hs.n)));
                    if (weapons.TryGetValue(side, out var w))
                        text.Append(" weapons=").Append(string.Join(",", w.Select(kv => kv.Key + "x" + kv.Value.ToString(CultureInfo.InvariantCulture))));
                }
            }
            return Succeeded(text.ToString());
        }
    }

    // coop.debug.battle.blow_trace
    /// <summary>
    /// Arms or dumps the duel rig's shared-clock blow events for an army battle: every blow as the attacker's
    /// machine scored it (with victim ownership), every blow routed to its owner, every routed blow applied.
    /// Paired across the two machines by (victim, attacker, damage), they give the routing funnel per side
    /// (scored on puppets, routed, applied) and the routed-damage latency the plan's fairness metric needs.
    /// </summary>
    public sealed class BattleBlowTraceCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.battle";

        public string Name => "blow_trace";

        public string Description => "start | stop [file]: record every blow / routed / applied event with a UTC ms clock; stop writes them to the file (or prints them when no file is given).";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("mode", "start or stop", isRequired: true),
            new ExpectedArgs("file", "stop only: path to write the trace to; an army trace is several MB, more than the live-test reply allows", isRequired: false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            string mode = args.Count > 0 ? args[0] : "";
            switch (mode)
            {
                case "start":
                    // Damage path only: the per-action apply / engine events of the duel rig are ~90 percent of an
                    // army battle's events and would push the blows past the recorder's cap.
                    Missions.Diagnostics.DuelEvents.Start(new[] { "blow", "routed", "applied", "damage_rx" });
                    return Succeeded("blow trace armed (blow, routed, applied, damage_rx)");
                case "stop":
                    string text = Missions.Diagnostics.DuelEvents.Snapshot(stop: true);
                    if (args.Count < 2 || string.IsNullOrWhiteSpace(args[1]))
                        return Succeeded(text);
                    try
                    {
                        System.IO.File.WriteAllText(args[1], text, new UTF8Encoding(false));
                    }
                    catch (Exception ex)
                    {
                        return Failed("could not write " + args[1] + ": " + ex.Message);
                    }
                    int newline = text.IndexOf('\n');
                    return Succeeded((newline < 0 ? text : text.Substring(0, newline)) + " written to " + args[1]);
                default:
                    return Failed("usage: blow_trace start|stop [file]");
            }
        }
    }
}
#endif
