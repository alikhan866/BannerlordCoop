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

        // An encounter closing on top of unanswered spoils is worth a name and a stack trace. It is rare - it
        // needs an open offer - and it is the difference between "the player declined" and "something closed
        // the encounter before the player was asked", which no other line in the log distinguishes.
        var wasShown = Loot.ClientBattleLootOffer.WasShown;
        Logger.Information(
            "[Loot] PlayerEncounter.Finish with an offer still open (shownToPlayer={Shown}):{NewLine}{Stack}",
            wasShown, Environment.NewLine, Environment.StackTrace);

        try
        {
            if (ContainerProvider.TryResolve<BattleLootClientHandler>(out var handler))
            {
                // Nothing was ever put in front of the player, so the staged rosters say "never asked" and
                // reading them as a decline would forfeit the lot.
                if (!wasShown)
                {
                    handler.SettleUnshownOffer();
                    return;
                }

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
