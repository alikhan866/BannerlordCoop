using GameInterface.Configuration;
using HarmonyLib;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Stops troops breaking and fleeing when their morale gives out, while
/// <see cref="ModOptions.DisableBattleMorale"/> is set.
/// </summary>
/// <remarks>
/// <c>MissionAgentPanicHandler.OnAgentPanicked</c> is the whole of the morale-rout path, which is why the patch
/// is here and nowhere else. Disassembling the engine gives exactly one route from a lost nerve to a man
/// running: <c>CommonAIComponent.Panic</c> raises <c>Mission.OnAgentPanicked</c>, which dispatches to this
/// handler; the handler queues the agent, and on the next pre-tick calls <c>CommonAIComponent.Retreat</c> and
/// <c>Mission.OnAgentFleeing</c> for everything queued. Refusing the queue therefore ends the behaviour
/// completely, without touching morale values, formation cohesion, or the scoreboard.
///
/// The other caller of <c>Retreat</c> is <c>AgentComponentExtensions.Retreat</c> - a deliberate, explicit order
/// - and it is deliberately left working. This suppresses panic, not retreating.
///
/// It is worth being plain about why this exists at all, since it turns off a real part of the game. A side's
/// morale responds to casualties, to the men standing around each agent and to whether its formation is still
/// intact. In a coop battle all three are distorted: every client sees a different slice of the field, the
/// battle-size cap fields reinforcements and stands them down again, and agents change owner on migration. The
/// result is troops breaking for reasons unrelated to how the battle is actually going.
/// </remarks>
[HarmonyPatch(typeof(MissionAgentPanicHandler), "OnAgentPanicked")]
internal class BattleMoralePanicPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !ModConfigProvider.ModOptions.DisableBattleMorale;
}

/// <summary>
/// Stops an agent fleeing at all while <see cref="ModOptions.DisableBattleMorale"/> is set - including when
/// its whole formation is ordered to.
/// </summary>
/// <remarks>
/// Suppressing panic alone was not enough, and the reason is that a man can be made to run in two unrelated
/// ways. <see cref="BattleMoralePanicPatch"/> covers the first: his own nerve going, which queues him in
/// <c>MissionAgentPanicHandler</c>. The second is the TEAM AI deciding the battle is lost and putting a
/// formation under a retreat movement order - <c>MovementOrder.RetreatAux</c> walks every unit in the formation
/// and retreats each one directly, never touching the panic handler. That is a whole side leaving at once, and
/// it is what was still being seen: an attacking side of 1,500 broke and ended the battle with 900 of its men
/// never fielded.
///
/// Both paths converge on exactly one method - <c>CommonAIComponent.Retreat(bool)</c> - which is why the block
/// belongs here rather than at either source. Verified by disassembly: its only callers are
/// <c>MissionAgentPanicHandler.OnPreMissionTick</c> and <c>AgentComponentExtensions.Retreat</c>, and the latter
/// is reached only from <c>MovementOrder.OnUnitJoinOrLeave</c> and <c>MovementOrder.RetreatAux</c>.
///
/// Deliberate retreats by this mod are untouched, and not by luck: <c>Agent.Retreat(WorldPosition)</c> is a
/// different method that does not route through here, and that is the one
/// <c>BattleAuthorityMigrator</c> uses to send a departed player's troops off the field.
///
/// With nobody able to flee, a battle ends when a side is actually destroyed - which is what the option asks
/// for. Side depletion still ends it, so a battle cannot become unendable.
/// </remarks>
[HarmonyPatch(typeof(CommonAIComponent), nameof(CommonAIComponent.Retreat))]
internal class BattleRetreatSuppressionPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !ModConfigProvider.ModOptions.DisableBattleMorale;
}
