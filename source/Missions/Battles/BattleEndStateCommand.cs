using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace Missions.Battles;

/// <summary>
/// Why this battle has not ended - every input to the decision, in one answer.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS
/// A field battle whose losing side was wiped out sat unfinished for over three minutes while the SERVER had
/// already destroyed its map event. Working out why meant reading engine IL to find that the whole chain is
/// <c>Mission.CheckMissionEnd</c> -> <c>CheckMissionEnded</c> -> each <c>MissionLogic.MissionEnded</c>, that
/// only <c>BattleEndLogic</c> answers for a field battle, and that its answer is gated behind a private
/// <c>_canCheckForEndCondition</c> flag which - in this codebase - is set by NOBODY in the engine and only by
/// <c>CoopBattleController</c>. None of that is visible from outside, so the failure looks like "the battle
/// just hangs".
///
/// Every one of those inputs is a field or a call this command can read, so it reads them.
///
/// PRIVATE STATE, READ DELIBERATELY
/// The gate and the depletion latches are private to BattleEndLogic and there is no public accessor. A
/// diagnostic that could not see them would be able to say "the battle has not ended" and nothing more, which
/// is what made this cost a day. Each read is individually guarded so a renamed field costs one line of the
/// report instead of the whole answer.
///
/// IT ONLY REPORTS
/// Nothing here concludes a battle. A command that forced a conclusion would paper over the very state a
/// scenario is trying to observe, and the campaign result would then be one this rig invented.
/// </remarks>
public static class BattleEndStateCommand
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [CommandLineArgumentFunction("end_state", "coop.debug.battle")]
    public static string EndState(List<string> args)
    {
        bool asJsonOnly = args.Count == 1 && string.Equals(args[0], "json", StringComparison.OrdinalIgnoreCase);
        if (args.Count > 1 || (args.Count == 1 && !asJsonOnly))
            return "Usage: coop.debug.battle.end_state [json]";

        Mission mission = Mission.Current;
        if (mission == null) return "BATTLE_END_STATE active=false reason=no-mission";

        var endLogic = mission.GetMissionBehavior<BattleEndLogic>();
        var spawnLogic = mission.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
        var controller = mission.GetMissionBehavior<CoopBattleController>();

        var payload = new EndStateView
        {
            missionEnded = Read(() => mission.MissionEnded, false),
            isMissionEnding = Read(() => mission.IsMissionEnding, false),
            hasMissionResult = Read(() => mission.MissionResult != null, false),
            missionResult = Read(() => mission.MissionResult == null
                ? "none"
                : $"victory={mission.MissionResult.PlayerVictory} defeated={mission.MissionResult.PlayerDefeated}"),

            battleEndLogicPresent = endLogic != null,

            // THE gate. False here means BattleEndLogic never even looks, so no amount of depletion ends it.
            canCheckForEndCondition = Flag(endLogic, "_canCheckForEndCondition"),
            canCheckForEndConditionSiege = Flag(endLogic, "_canCheckForEndConditionSiege"),
            isEnemySideDepleted = Flag(endLogic, "_isEnemySideDepleted"),
            isPlayerSideDepleted = Flag(endLogic, "_isPlayerSideDepleted"),
            isEnemySideRetreating = Flag(endLogic, "_isEnemySideRetreating"),
            isPlayerSideRetreating = Flag(endLogic, "_isPlayerSideRetreating"),
            isEnemyDefenderPulledBack = Flag(endLogic, "_isEnemyDefenderPulledBack"),

            // Coop's own one-shot hold, which is what drives the gate above.
            endConditionHoldReleased = Flag(controller, "endConditionHoldReleased"),
            deploymentActivated = Read(() => controller?.Deployment?.IsActivated ?? false, false),
            deploymentCommitted = Read(() => controller?.Deployment?.IsCommitted ?? false, false),

            // What the gate would see if it did look.
            attackerDepleted = Depleted(spawnLogic, BattleSideEnum.Attacker),
            defenderDepleted = Depleted(spawnLogic, BattleSideEnum.Defender),
            attackerLiveAgents = LiveAgents(mission, BattleSideEnum.Attacker),
            defenderLiveAgents = LiveAgents(mission, BattleSideEnum.Defender),

            playerTeamSide = Read(() => mission.PlayerTeam?.Side.ToString()) ?? "none",
            mainAgentActive = Read(() => mission.MainAgent?.IsActive() ?? false, false),
        };

        payload.verdict = Explain(payload);

        string json = "LIVE_TEST_JSON=" + JsonConvert.SerializeObject(payload);
        if (asJsonOnly) return json;

        var report = new StringBuilder();
        report.AppendLine($"BATTLE_END_STATE verdict={payload.verdict}");
        report.AppendLine(
            $"  mission ended={payload.missionEnded} ending={payload.isMissionEnding} " +
            $"result={payload.missionResult}");
        report.AppendLine(
            $"  gate canCheckForEndCondition={payload.canCheckForEndCondition} " +
            $"siege={payload.canCheckForEndConditionSiege} " +
            $"coopHoldReleased={payload.endConditionHoldReleased} " +
            $"deployment(activated={payload.deploymentActivated},committed={payload.deploymentCommitted})");
        report.AppendLine(
            $"  latches enemyDepleted={payload.isEnemySideDepleted} playerDepleted={payload.isPlayerSideDepleted} " +
            $"enemyRetreating={payload.isEnemySideRetreating} playerRetreating={payload.isPlayerSideRetreating} " +
            $"defenderPulledBack={payload.isEnemyDefenderPulledBack}");
        report.AppendLine(
            $"  field attacker(live={payload.attackerLiveAgents},depleted={payload.attackerDepleted}) " +
            $"defender(live={payload.defenderLiveAgents},depleted={payload.defenderDepleted}) " +
            $"playerSide={payload.playerTeamSide} mainAgentActive={payload.mainAgentActive}");
        report.Append(json);
        return report.ToString();
    }

    /// <summary>
    /// Names the first reason the battle is not concluding, in the order the engine would hit them.
    /// </summary>
    /// <remarks>
    /// Ordered deliberately. A battle can be both un-gated AND not depleted, and reporting the second when the
    /// first is what stops it would send a reader after the wrong thing - the gate is checked before anything
    /// else is even evaluated.
    /// </remarks>
    private static string Explain(EndStateView view)
    {
        if (view.missionEnded) return "ENDED";
        if (!view.battleEndLogicPresent) return "NO_BATTLE_END_LOGIC";
        if (view.canCheckForEndCondition == "false")
            return view.endConditionHoldReleased == "false"
                ? "GATED-coop hold has not released"
                : "GATED-something re-disabled the check after the hold released";
        if (view.attackerDepleted == "true" || view.defenderDepleted == "true") return "DEPLETED-but-not-concluded";
        return "STILL-FIGHTING";
    }

    private static string Flag(object target, string field)
    {
        if (target == null) return "n/a";

        try
        {
            var info = target.GetType().GetField(field, Hidden);
            if (info == null) return "<no such field>";
            object value = info.GetValue(target);
            return value == null ? "null" : value.ToString().ToLowerInvariant();
        }
        catch
        {
            return "<unreadable>";
        }
    }

    private static string Depleted(DefaultBattleMissionAgentSpawnLogic spawnLogic, BattleSideEnum side)
    {
        if (spawnLogic == null) return "n/a";

        // Calls the real thing, patch included, so this reports what BattleEndLogic would actually be told
        // rather than a second opinion that could disagree with it.
        try { return spawnLogic.IsSideDepleted(side).ToString().ToLowerInvariant(); }
        catch { return "<threw>"; }
    }

    private static int LiveAgents(Mission mission, BattleSideEnum side)
    {
        try
        {
            return mission.Agents.Count(agent =>
                agent != null && agent.IsActive() && agent.IsHuman &&
                (agent.Team?.Side ?? BattleSideEnum.None) == side);
        }
        catch
        {
            return -1;
        }
    }

    private static T Read<T>(Func<T> read, T fallback = default)
    {
        try { return read(); }
        catch { return fallback; }
    }

    private sealed class EndStateView
    {
        public string verdict { get; set; }
        public bool missionEnded { get; set; }
        public bool isMissionEnding { get; set; }
        public bool hasMissionResult { get; set; }
        public string missionResult { get; set; }
        public bool battleEndLogicPresent { get; set; }
        public string canCheckForEndCondition { get; set; }
        public string canCheckForEndConditionSiege { get; set; }
        public string isEnemySideDepleted { get; set; }
        public string isPlayerSideDepleted { get; set; }
        public string isEnemySideRetreating { get; set; }
        public string isPlayerSideRetreating { get; set; }
        public string isEnemyDefenderPulledBack { get; set; }
        public string endConditionHoldReleased { get; set; }
        public bool deploymentActivated { get; set; }
        public bool deploymentCommitted { get; set; }
        public string attackerDepleted { get; set; }
        public string defenderDepleted { get; set; }
        public int attackerLiveAgents { get; set; }
        public int defenderLiveAgents { get; set; }
        public string playerTeamSide { get; set; }
        public bool mainAgentActive { get; set; }
    }
}
