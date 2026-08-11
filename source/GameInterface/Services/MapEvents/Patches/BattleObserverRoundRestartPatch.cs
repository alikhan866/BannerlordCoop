using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Books a withdrawn troop as having left the field, without booking it as having fled.
/// </summary>
/// <remarks>
/// <c>BattleObserverMissionLogic.OnAgentRemoved</c> does two jobs at once: it DECREMENTS the side's troop count
/// and it ATTRIBUTES the removal to a cause. The cause comes from the state the engine reports, and the only
/// removal call available for "take this man off the field" is <c>Agent.FadeOut</c>, which reports
/// <see cref="AgentState.Routed"/>. So standing troops down filed each of them as a retreat.
///
/// Suppressing the method outright was the first attempt, and it swapped one wrong number for another: no
/// retreats, but the scoreboard never learned those men had left either. Measured live as a scoreboard showing
/// 197 defenders while the mission actually held 106 - a gap of 91, exactly the number that had been stood
/// down.
///
/// Relabelling the state does not work either: the observer's switch throws
/// <c>ArgumentOutOfRangeException</c> for anything but Routed, Unconscious or Killed.
///
/// So the decrement is issued directly and the original skipped. <c>TroopNumberChanged</c>'s first count is the
/// troop delta and the rest are the cause buckets; passing -1 with every bucket at zero says "one fewer man
/// here, for no reason worth recording", which is precisely what standing down is.
///
/// <c>_removedAgentCountForSides</c> is deliberately NOT incremented. The engine counts a removal there as
/// permanent, and these men return to the reserve and come back as casualties make room - counting them as
/// removed would edge the side towards looking spent when it is not.
/// </remarks>
[HarmonyPatch(typeof(BattleObserverMissionLogic), nameof(BattleObserverMissionLogic.OnAgentRemoved))]
internal class BattleObserverRoundRestartPatch
{
    [HarmonyPrefix]
    private static bool Prefix(BattleObserverMissionLogic __instance, Agent affectedAgent)
    {
        if (!BattleRoundRestartScope.IsClearingField) return true;

        RecordWithdrawalWithoutCause(__instance, affectedAgent);
        return false;
    }

    private static void RecordWithdrawalWithoutCause(BattleObserverMissionLogic logic, Agent agent)
    {
        var observer = logic?.BattleObserver;
        if (observer == null || agent == null || !agent.IsHuman) return;

        var team = agent.Team;
        if (team == null || team == Team.Invalid) return;

        var combatant = agent.Origin?.BattleCombatant;
        if (combatant == null) return;

        // Positional, because the interface's parameter names are not exposed here. The first int is the troop
        // delta and the remaining five are the cause buckets - the Routed branch passes (-1, 0, 0, 1, 0, 0),
        // so (-1, 0, 0, 0, 0, 0) is the same removal with no cause attributed to it.
        observer.TroopNumberChanged(team.Side, combatant, agent.Character, -1, 0, 0, 0, 0, 0);
    }
}
