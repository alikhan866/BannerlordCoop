using Common.Logging;
using GameInterface.Services.MobileParties.Extensions;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace GameInterface.Services.MobileParties.Patches;

/// <summary>
/// A player's explicit move order releases the Hold left behind by leaving a settlement.
/// </summary>
/// <remarks>
/// <c>SettlementInterface.EndSettlementEncounter</c> calls <c>SetMoveModeHold</c> twice on purpose, so a
/// party that has just left a settlement cannot immediately walk back into it. That Hold is correct at the
/// time and never released, and vanilla's <c>SetMoveGoToPoint</c> installs the target and the behaviour
/// without touching <c>PartyMoveMode</c>.
///
/// The result for a player who rejoined while inside a settlement: every click landed as
/// <c>target=(valid) shortTerm=GoToPoint default=GoToPoint moveMode=Hold</c> and the party did not move -
/// measured 18 times in a row - until some later sync happened to reset the move mode. From the player's
/// side it looked like the game ignoring input for a fixed period after joining.
///
/// Scoped to a party the LOCAL player controls, and applied only where a destination was just installed, so
/// an AI party parked after its own encounter keeps the Hold that protects it.
/// </remarks>
[HarmonyPatch(typeof(MobileParty))]
internal class PlayerMoveReleasesHoldPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerMoveReleasesHoldPatch>();

    [HarmonyPatch(nameof(MobileParty.SetMoveGoToPoint))]
    [HarmonyPostfix]
    private static void ReleaseHoldOnPlayerOrder(MobileParty __instance)
    {
        if (__instance == null) return;
        if (__instance.PartyMoveMode != MoveModeType.Hold) return;
        if (!__instance.IsPlayerParty() || !__instance.IsControlledByThisInstance()) return;

        __instance.PartyMoveMode = MoveModeType.Point;

        Logger.Debug(
            "Released a settlement-leave Hold on {Party} because its player issued a move order",
            __instance.StringId);
    }
}
