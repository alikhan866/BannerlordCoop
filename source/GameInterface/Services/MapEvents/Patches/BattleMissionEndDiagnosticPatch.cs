using System;
using Common.Logging;
using HarmonyLib;
using Serilog;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Records WHY a coop battle mission ended, because the logs currently cannot say.
/// </summary>
/// <remarks>
/// A player was returned to the campaign map mid-battle with no result: the mission simply stopped, the server
/// read the departure as a retreat, and his army carried on without him. Every path that produces that ending -
/// the scoreboard's Leave Battle, the retreat confirmation widget, a boundary crossing, a side being judged
/// depleted - looks identical from outside, so the logs cannot distinguish "the player left" from "the player
/// was thrown out". That distinction is the whole question.
///
/// So the two funnels are stamped with a stack trace. Diagnostic only: both patches read state, change nothing,
/// and never suppress the original. Guarded regardless, because this runs on the game thread during teardown
/// and a diagnostic must never be the reason a battle fails to end.
/// </remarks>
[HarmonyPatch(typeof(Mission))]
internal class BattleMissionEndDiagnosticPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleMissionEndDiagnosticPatch>();

    [HarmonyPatch(nameof(Mission.EndMission))]
    [HarmonyPrefix]
    private static void EndMissionPrefix(Mission __instance) => Report(__instance, "EndMission");

    [HarmonyPatch(nameof(Mission.RetreatMission))]
    [HarmonyPrefix]
    private static void RetreatMissionPrefix(Mission __instance) => Report(__instance, "RetreatMission");

    private static void Report(Mission mission, string via)
    {
        if (!BattleSpawnConfig.Enabled || !BattleSpawnGate.IsCoopBattleActive) return;

        try
        {
            var result = mission?.MissionResult;
            Logger.Warning(
                "[MissionEnd] {Via}: missionEnded={Ended} result={HasResult} playerVictory={Victory} playerDefeated={Defeated} " +
                "mainAgent={MainAgent} mainAgentActive={MainAgentActive}\n{Stack}",
                via,
                mission?.MissionEnded,
                result != null,
                result?.PlayerVictory,
                result?.PlayerDefeated,
                mission?.MainAgent != null,
                mission?.MainAgent?.IsActive(),
                Environment.StackTrace);
        }
        catch (Exception e)
        {
            Logger.Warning(e, "[MissionEnd] {Via}: could not record why the mission ended", via);
        }
    }
}
