using Common;
using Common.Logging;
using GameInterface.Services.MapEvents.Handlers;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.Encounters;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Reads what the player declined on the troop and prisoner screen, in the one instant it can be read.
/// </summary>
/// <remarks>
/// The answer to a loot offer is "what is still on the staged rosters". For items that holds to the end. For
/// troops it does not: vanilla's own screen-closed callback, the method patched here, begins by clearing BOTH
/// staged rosters - so a frame later they are empty whatever the player chose, and empty means "took
/// everything". Prisoners deliberately left on the screen were therefore still delivered, which is the
/// opposite of the decision the player had just made.
///
/// A prefix, so it runs while the rosters still say something. It only reads.
///
/// This is the same defect as #48 in a different place: a player's decision being inferred from evidence that
/// has already been destroyed. Where the evidence is about to go, capture it rather than infer it.
/// </remarks>
[HarmonyPatch(typeof(PlayerEncounter), "OnPlayerLootMembersAndPrisonerEnd")]
internal class BattleLootDeclinedTroopsPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleLootDeclinedTroopsPatch>();

    [HarmonyPrefix]
    private static void Prefix(PlayerEncounter __instance)
    {
        if (ModInformation.IsServer) return;
        if (!Loot.ClientBattleLootOffer.HasPending) return;

        try
        {
            if (ContainerProvider.TryResolve<BattleLootClientHandler>(out var handler))
            {
                handler.CaptureDeclinedTroops(__instance);
                return;
            }

            Logger.Warning("[Loot] No client loot handler; what the player declined on the loot screen cannot be recorded");
        }
        catch (Exception e)
        {
            // Never let this throw: the screen is closing, and an exception here would strand the player on a
            // half-finished encounter to save a bookkeeping detail.
            Logger.Warning(e, "[Loot] Could not record what the player declined on the loot screen");
        }
    }
}
