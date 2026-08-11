using Common;
using Common.Messaging;
using GameInterface.Services.MapEvents.Messages;
using HarmonyLib;
using System.Diagnostics;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// [Server] Emits <see cref="LiveBattleReinforcementTick"/> on a real-time cadence, so battles in progress keep
/// being offered reinforcements even though campaign time has stopped.
/// </summary>
/// <remarks>
/// <c>Campaign.RealTick</c> is the hook because it keeps running while the campaign is paused, which is exactly
/// the state a co-op battle creates: <c>PlayerOccupancyPauseHandler</c> pauses as soon as every player is
/// occupied. A campaign-time driver is silent for the whole battle.
///
/// Throttled on a real-time stopwatch rather than a tick count: RealTick's rate follows the frame rate, so a
/// count would scan far more often on a fast host than a slow one, and the scan walks the map's locatable index.
/// </remarks>
[HarmonyPatch(typeof(Campaign))]
internal static class LiveBattleReinforcementTickPatch
{
    /// <summary>
    /// How often a live battle reconsiders reinforcements. Short enough that a lord riding past joins the fight
    /// he is standing next to, long enough that the radius search is not run every frame.
    /// </summary>
    internal const double IntervalSeconds = 2.0;

    private static readonly Stopwatch SinceLastTick = Stopwatch.StartNew();

    [HarmonyPatch(nameof(Campaign.RealTick))]
    [HarmonyPostfix]
    private static void Postfix_RealTick()
    {
        if (!ModInformation.IsServer) return;
        if (!IsDue(SinceLastTick.Elapsed.TotalSeconds)) return;

        SinceLastTick.Restart();
        MessageBroker.Instance.Publish(null, new LiveBattleReinforcementTick());
    }

    internal static bool IsDue(double elapsedSeconds) => elapsedSeconds >= IntervalSeconds;
}
