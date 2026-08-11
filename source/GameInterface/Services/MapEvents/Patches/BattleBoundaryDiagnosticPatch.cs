using System;
using Common.Logging;
using HarmonyLib;
using Serilog;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Records what the boundary handler believed when it decided to eject a player from a coop battle.
/// </summary>
/// <remarks>
/// <c>MissionBoundaryCrossingHandler.DecideOrHandleAgentPunishment</c> was caught calling
/// <c>Mission.RetreatMission()</c> on a player who says they never left the field - and it happened while the
/// last surviving enemies were themselves standing somewhere unreachable. Both symptoms fit a battle whose
/// BOUNDARY is in the wrong place rather than a player who wandered out of a correct one, but "fits" is not
/// evidence.
///
/// So this records the three things that separate the two explanations: where the agent actually was, whether
/// the mission agrees that position is inside its soft and hard boundaries, and how many boundary sets the
/// mission has at all. A player standing well inside a sane boundary being punished says the handler is wrong;
/// a player genuinely outside says the boundary is misplaced or too small.
///
/// Diagnostic only - reads state, changes nothing, never suppresses the original. Guarded because it runs on
/// the mission tick during a teardown path, and a diagnostic must never be the reason a battle behaves
/// differently.
/// </remarks>
[HarmonyPatch(typeof(MissionBoundaryCrossingHandler), nameof(MissionBoundaryCrossingHandler.DecideOrHandleAgentPunishment))]
internal class BattleBoundaryDiagnosticPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleBoundaryDiagnosticPatch>();

    [HarmonyPrefix]
    private static void Prefix(Agent agent)
    {
        if (!BattleSpawnConfig.Enabled || !BattleSpawnGate.IsCoopBattleActive) return;

        try
        {
            var mission = Mission.Current;
            if (mission == null || agent == null) return;

            var position = agent.Position.AsVec2;

            Logger.Warning(
                "[Boundary] About to punish {Who} at ({X:F1}, {Y:F1}) — insideSoft={Soft} insideHard={Hard} " +
                "boundarySets={Sets} closestBoundary=({BX:F1}, {BY:F1}) distanceToBoundary={Dist:F1}",
                agent == mission.MainAgent ? "THE LOCAL PLAYER" : "an agent",
                position.x, position.y,
                Inside(mission, position, hard: false),
                Inside(mission, position, hard: true),
                mission.Boundaries?.Count,
                ClosestBoundary(mission, position).x,
                ClosestBoundary(mission, position).y,
                position.Distance(ClosestBoundary(mission, position)));
        }
        catch (Exception e)
        {
            Logger.Warning(e, "[Boundary] Could not record why the boundary handler is punishing an agent");
        }
    }

    private static bool? Inside(Mission mission, Vec2 position, bool hard)
    {
        try
        {
            return hard
                ? mission.IsPositionInsideHardBoundaries(position)
                : mission.IsPositionInsideBoundaries(position);
        }
        catch
        {
            return null;
        }
    }

    private static Vec2 ClosestBoundary(Mission mission, Vec2 position)
    {
        try
        {
            return mission.GetClosestBoundaryPosition(position);
        }
        catch
        {
            return Vec2.Invalid;
        }
    }
}
