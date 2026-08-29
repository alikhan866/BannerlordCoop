using System.Collections.Generic;
using TaleWorlds.MountAndBlade;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace Missions.Battles;

/// <summary>
/// Finishes a driven client's deployment on demand, instead of waiting out the BR-025 time limit.
/// </summary>
/// <remarks>
/// WHY A COMMAND, WHEN A TIMER ALREADY DOES IT
/// The timer is the safety net for a player who walked away; it is not a way to drive a test. A headless
/// client has no Start Battle button, so every battle it enters spends about two minutes withholding its own
/// party's troops from every peer before the limit expires - measured directly: a two-owner attacker side sat
/// at 251 of 502 on both clients, with every other observable reporting ok, until the timer fired and both
/// jumped to 502 in lockstep.
///
/// That cost is not just wall-clock. It made the withhold window impossible to test deliberately: a scenario
/// could not choose to commit, so it could never distinguish "the peer's troops never arrive" from "the peer's
/// troops arrive when deployment commits". Being able to commit on command is what turned that from a
/// suspected defect into a known behaviour.
///
/// WHAT IT REPORTS
/// The native finish is QUEUED rather than run inline - it removes the deployment behaviors, which cannot
/// happen inside the behavior tick - so Finished means "accepted and queued", not "already committed". The
/// caller confirms with coop.debug.battle.snapshot's deploymentCommitted. Retry means the teams are still
/// being set up and the call is worth repeating; Unavailable means there is no deployment phase to finish.
/// </remarks>
public static class BattleDeploymentDebugCommand
{
    [CommandLineArgumentFunction("finish_deployment", "coop.debug.battle")]
    public static string FinishDeployment(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.battle.finish_deployment";

        var mission = Mission.Current;
        var controller = mission?.GetMissionBehavior<CoopBattleController>();
        if (controller == null) return "FINISH_DEPLOYMENT active=false reason=no-coop-battle-mission";

        var deployment = controller.Deployment;
        if (deployment == null) return "FINISH_DEPLOYMENT active=false reason=no-deployment-coordinator";

        if (deployment.IsCommitted)
            return $"FINISH_DEPLOYMENT result=AlreadyCommitted activated={Lower(deployment.IsActivated)} " +
                   $"note=own-party troops are already released to peers";

        var result = deployment.FinishNow();
        var deploymentController = mission.GetMissionBehavior<DeploymentMissionController>();

        return $"FINISH_DEPLOYMENT result={result} committed={Lower(deployment.IsCommitted)} " +
               $"activated={Lower(deployment.IsActivated)} " +
               $"teamSetupOver={Lower(deploymentController?.TeamSetupOver ?? false)} " +
               $"note=Finished means queued; confirm with coop.debug.battle.snapshot deploymentCommitted";
    }

    private static string Lower(bool value) => value.ToString().ToLowerInvariant();
}
