using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace Missions.Battles;

/// <summary>
/// Reports everything that decides whether this client can interact with a usable object.
/// </summary>
/// <remarks>
/// Exists because "it works for me but not for him" was diagnosed three times by reasoning from code that
/// merely FIT the symptom, and all three were wrong. The asymmetry between two clients is a fact to be
/// measured, not deduced: run this on both, diff the two lines, and the difference names itself.
///
/// Deliberately read-only and deliberately NOT <c>#if DEBUG</c>. The clients that actually exhibit these
/// faults are other people running the shared Release build, and a diagnostic that only exists in a build
/// nobody plays cannot answer anything. It touches no state, so it is safe to run mid-battle.
///
/// The fields are chosen to separate the three candidate explanations for "no interaction prompt":
///   - the mission never got its main-agent controller  -> controller=none
///   - the agent is not the player's to drive           -> mainIsMine=false, or agentController != Player
///   - the usable objects are not there to be used      -> machines=0, or usablePoints=0
/// </remarks>
internal static class MissionInteractionDiagnostics
{
    [CommandLineArgumentFunction("interaction", "coop.debug.mission")]
    public static string Interaction(List<string> args)
    {
        if (args != null && args.Count != 0) return "Usage: coop.debug.mission.interaction";

        Mission mission = Mission.Current;
        if (mission == null) return "INTERACTION mission=none";

        var fields = new List<string> { "INTERACTION" };
        fields.Add($"mode={mission.Mode}");

        // Which side this client believes it is on. Team INDEX is local bookkeeping and can differ between
        // clients for the same team, so the side is what matters: a machine only offers its points to the side
        // that owns it, and two clients disagreeing about sides would switch the whole siege off for one of them.
        fields.Add($"playerSide={mission.PlayerTeam?.Side.ToString() ?? "none"}");
        fields.Add($"playerTeamIdx={mission.PlayerTeam?.TeamIndex.ToString() ?? "none"}");
        fields.Add($"attackerTeamIdx={mission.AttackerTeam?.TeamIndex.ToString() ?? "none"}");
        fields.Add($"defenderTeamIdx={mission.DefenderTeam?.TeamIndex.ToString() ?? "none"}");

        // Whether the mission has the behaviour that computes the focused usable object at all. If this is
        // absent no prompt can ever appear, whatever the agent is doing.
        //
        // Looked up by NAME rather than by type: MissionMainAgentController lives in the View assembly, and a
        // diagnostic is not worth binding this project to the renderer's assembly - which a headless build
        // does not even load.
        bool hasMainAgentController = mission.MissionBehaviors
            .Any(behavior => behavior != null && behavior.GetType().Name == "MissionMainAgentController");
        fields.Add($"mainAgentController={(hasMainAgentController ? "present" : "MISSING")}");

        Agent main = mission.MainAgent;
        if (main == null)
        {
            fields.Add("mainAgent=none");
        }
        else
        {
            // mainIsMine separates "the mission has a main agent" from "the main agent is the hero this
            // player is supposed to be driving" - a puppet adopted as main looks identical until asked.
            bool mainIsMine =
                main.Character != null &&
                TaleWorlds.CampaignSystem.Hero.MainHero != null &&
                main.Character.StringId == TaleWorlds.CampaignSystem.Hero.MainHero.CharacterObject?.StringId;

            fields.Add($"mainAgent={main.Index}");
            fields.Add($"mainIsMine={mainIsMine}");
            fields.Add($"agentController={main.Controller}");
            fields.Add($"active={main.IsActive()}");
            fields.Add($"health={main.Health:F0}");
            fields.Add($"mounted={main.MountAgent != null}");
            fields.Add($"detachment={(main.Detachment == null ? "none" : main.Detachment.GetType().Name)}");
            fields.Add($"formation={(main.Formation == null ? "none" : main.Formation.Index.ToString())}");
            fields.Add($"usingObject={(main.CurrentlyUsedGameObject == null ? "none" : main.CurrentlyUsedGameObject.GetType().Name)}");
            fields.Add($"team={(main.Team == null ? "none" : main.Team.TeamIndex.ToString())}");
        }

        // The usable objects themselves, with the three reasons a point can be unavailable counted SEPARATELY.
        //
        // The first version of this collapsed them into one "usable" number, which proved a difference existed
        // and then could not say which of the three it was - so it invited a guess, which is the failure this
        // command was written to stop. The conditions deliberately overlap (a point can be both deactivated and
        // occupied); each count answers its own question rather than partitioning.
        //
        // staleUser is the important one. A standing point can only be held by one agent, so occupied points
        // can never exceed the agent count in a healthy mission. If they do, points are holding users that are
        // gone - released on the owning client and never released here.
        var machines = mission.MissionObjects.OfType<UsableMachine>().ToList();
        int standingPoints = 0;
        int usablePoints = 0;
        int deactivated = 0;
        int disabled = 0;
        int occupied = 0;
        int staleUser = 0;
        int disabledMachines = 0;
        int deactivatedAttacker = 0;
        int deactivatedDefender = 0;
        int deactivatedNoSide = 0;
        int disabledForPlayers = 0;
        var deactivatedByType = new Dictionary<string, int>();
        foreach (UsableMachine machine in machines)
        {
            if (machine.IsDisabled) disabledMachines++;
            IEnumerable<StandingPoint> points = machine.StandingPoints;
            if (points == null) continue;
            foreach (StandingPoint point in points)
            {
                if (point == null) continue;
                standingPoints++;

                if (point.IsDisabledForPlayers) disabledForPlayers++;
                if (point.IsDeactivated)
                {
                    deactivated++;
                    // Attribute each switched-off point to the side that owns its machine, and to the machine
                    // type. If they all belong to the side this client is NOT on, the cause is side
                    // attribution rather than the usable objects themselves.
                    // Side lives on SiegeWeapon, not on UsableMachine - ladders, ramps and stone piles that
                    // are not siege weapons simply have no side to report, and are counted separately rather
                    // than being forced into one.
                    var sided = machine as SiegeWeapon;
                    if (sided == null) deactivatedNoSide++;
                    else if (sided.Side == BattleSideEnum.Attacker) deactivatedAttacker++;
                    else if (sided.Side == BattleSideEnum.Defender) deactivatedDefender++;
                    else deactivatedNoSide++;

                    string typeName = machine.GetType().Name;
                    deactivatedByType.TryGetValue(typeName, out int seen);
                    deactivatedByType[typeName] = seen + 1;
                }
                if (point.IsDisabled) disabled++;
                if (point.HasUser)
                {
                    occupied++;
                    Agent user = point.UserAgent;
                    if (user == null || !user.IsActive()) staleUser++;
                }

                if (!point.IsDeactivated && !point.IsDisabled && !point.HasUser) usablePoints++;
            }
        }

        fields.Add($"machines={machines.Count}");
        fields.Add($"machinesDisabled={disabledMachines}");
        fields.Add($"standingPoints={standingPoints}");
        fields.Add($"usablePoints={usablePoints}");
        fields.Add($"ptDeactivated={deactivated}");
        fields.Add($"ptDisabled={disabled}");
        fields.Add($"ptOccupied={occupied}");
        fields.Add($"ptStaleUser={staleUser}");
        // Separate from IsDeactivated: an object can be live for AI and closed to players. It is named for
        // exactly this symptom and was never measured, which is why four captures could not see it.
        fields.Add($"ptDisabledForPlayers={disabledForPlayers}");
        fields.Add($"deacAttacker={deactivatedAttacker}");
        fields.Add($"deacDefender={deactivatedDefender}");
        fields.Add($"deacNoSide={deactivatedNoSide}");
        fields.Add("deacByType=" + (deactivatedByType.Count == 0
            ? "none"
            : string.Join(",", deactivatedByType
                .OrderByDescending(entry => entry.Value)
                .Take(6)
                .Select(entry => entry.Key + ":" + entry.Value))));
        fields.Add($"agents={mission.Agents.Count}");

        return string.Join(" ", fields);
    }
}
