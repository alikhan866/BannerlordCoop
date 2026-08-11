using Common;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Siege;

namespace GameInterface.Services.SiegeEvents.Patches;

/// <summary>
/// The siege tick (strategy, construction, bombardment, removal) is server-authoritative; its RNG and
/// object creation diverge if a client runs it over the replicated siege events in its manager list.
/// Clients receive every tick outcome as sync messages and remove ended sieges via the registry destroy.
/// </summary>
[HarmonyPatch(typeof(SiegeEventManager))]
internal class SiegeEventManagerTickPatch
{
    [HarmonyPatch(nameof(SiegeEventManager.Tick))]
    [HarmonyPrefix]
    private static bool TickPrefix()
    {
        return ModInformation.IsServer;
    }
}

/// <summary>
/// [Server] Holds an individual siege still while a player is away fighting a battle for it.
/// </summary>
/// <remarks>
/// This is vanilla's own rule, applied through a signal vanilla does not have. <c>SiegeEvent.Tick</c> already
/// returns early when the besieger or the besieged settlement is in a MapEvent - but a co-op client can be in a
/// battle mission the server holds no map event for (a battle restored from a save is the reproducible case),
/// and then vanilla's gate sees nothing and the siege bombards straight through the assault.
///
/// Deliberately a separate patch from the manager-level one above rather than an edit to it: that one answers
/// "may this instance tick sieges at all", which is a different question from "should this particular siege be
/// advancing right now", and folding the two together would make each harder to reason about.
/// </remarks>
[HarmonyPatch(typeof(SiegeEvent))]
internal class SiegeEventBattleFreezePatch
{
    [HarmonyPatch(nameof(SiegeEvent.Tick))]
    [HarmonyPrefix]
    private static bool Prefix_Tick(SiegeEvent __instance)
    {
        if (!ModInformation.IsServer) return true;

        return !SiegeBattleFreeze.IsFrozen(__instance?.BesiegedSettlement);
    }
}
