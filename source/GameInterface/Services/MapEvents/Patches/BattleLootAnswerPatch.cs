using Common;
using Common.Logging;
using GameInterface.Services.MapEvents.Handlers;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.Encounters;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Sends the player's answer to a loot offer at the last moment it can still be read.
/// </summary>
/// <remarks>
/// Placed here because once <c>PlayerEncounter.Finish</c> returns, the encounter and its staged rosters are
/// gone. Those rosters are the evidence: vanilla's loot screen empties them as the player takes things, so
/// what is left at this instant is precisely what was declined.
///
/// This is where the old auto-award patch used to sit, awarding the staged loot locally when the encounter
/// never reached the loot screen. That patch is deleted - it credited spoils the server was crediting too,
/// and it moved heroes by roster copy.
///
/// Failures are swallowed. A missed answer costs the player the difference between what they took and what the
/// server hands over, which the offer's expiry then resolves; an exception escaping here would strand them on
/// a dead encounter, which is worse.
/// </remarks>
[HarmonyPatch(typeof(PlayerEncounter), nameof(PlayerEncounter.Finish))]
internal class BattleLootAnswerPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleLootAnswerPatch>();

    [HarmonyPrefix]
    private static void Prefix()
    {
        if (ModInformation.IsServer) return;
        if (!Loot.ClientBattleLootOffer.HasPending) return;

        try
        {
            if (ContainerProvider.TryResolve<BattleLootClientHandler>(out var handler))
            {
                handler.AnswerOutstandingOffer(PlayerEncounter.Current);
                return;
            }

            Logger.Warning("[Loot] No client loot handler is available; the outstanding offer goes unanswered");
        }
        catch (Exception e)
        {
            Logger.Warning(e, "[Loot] Could not answer the outstanding loot offer while the encounter was finishing");
        }
    }
}
