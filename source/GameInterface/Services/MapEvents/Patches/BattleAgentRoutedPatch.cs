using Common.Messaging;
using GameInterface.Services.MapEvents.Messages;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Captures agents routing out of a coop battle (postfix on the <see cref="Mission.OnAgentRemoved"/>
/// removal funnel, filtered to the routed state) and publishes <see cref="BattleAgentRouted"/> so the
/// Missions battle controller can replicate the despawn to peers. Deaths take the separate
/// <see cref="BattleAgentDiedPatch"/> path; a rout is not a casualty, so no roster message is sent.
/// </summary>
[HarmonyPatch(typeof(Mission), nameof(Mission.OnAgentRemoved))]
internal class BattleAgentRoutedPatch
{
    [HarmonyPostfix]
    private static void Postfix(Agent affectedAgent, AgentState agentState)
    {
        if (!BattleSpawnConfig.Enabled) return;
        if (!BattleSpawnGate.IsCoopBattleActive) return;
        if (agentState != AgentState.Routed || affectedAgent == null) return;

        // NOT suppressed during a withdrawal, deliberately. This looks like a "the man fled" report, but it is
        // the channel that tells every peer to DROP ITS PUPPET - AgentRoutReporter answers it by broadcasting
        // NetworkBattleAgentRouted, forgetting the casualty and de-registering the agent. Silencing it left
        // peers holding puppets for troops that had been taken off the field here: nothing owned them so they
        // never moved, and damage routed to an owner that no longer had them, so they could not be killed.
        // That is the "stuck and immortal troops" a second client saw.
        //
        // The scoreboard attribution - the actual retreat/rout counter - is handled separately by
        // BattleObserverRoundRestartPatch, which issues a plain decrement with no cause. Two different jobs
        // that happen to be triggered by the same agent state.

        MessageBroker.Instance.Publish(affectedAgent, new BattleAgentRouted(affectedAgent));
    }
}
