using System;
using Common.Logging;
using Common.Util;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem.Encounters;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Awards battle loot directly when the encounter it was staged into ends without ever offering the loot screen.
/// </summary>
/// <remarks>
/// The staged rosters are granted at <c>PlayerEncounterState.LootInventory</c>, which the encounter reaches by
/// walking its states. Concluding a co-op battle finalizes the map event and closes every involved player's
/// encounter, which can cut that walk short - and the staged loot dies with the encounter.
///
/// Measured live, on the same battle: one player had 73 item stacks, 19 members and 43 prisoners staged and
/// received none of it; the other received all of his, because his exit happened to go through a retreat prompt
/// that re-entered the encounter flow. The loot was present and correct on both machines. Only one of them ever
/// got as far as spending it.
///
/// <see cref="PlayerEncounterState.LootInventory"/> is the dividing line, and it is chosen deliberately over
/// "were the rosters emptied". Untaken loot left at the screen is a CHOICE - vanilla discards it - so a partly
/// consumed roster must not be force-fed back to the player. Never reaching the screen at all is the failure.
///
/// A prefix, because by the time <c>Finish</c> returns the encounter and its state are gone.
/// </remarks>
[HarmonyPatch(typeof(PlayerEncounter), nameof(PlayerEncounter.Finish))]
internal class BattleLootRescuePatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleLootRescuePatch>();

    [HarmonyPrefix]
    private static void Prefix()
    {
        if (!PendingBattleLoot.HasPending) return;

        try
        {
            var encounter = PlayerEncounter.Current;
            var state = encounter?.EncounterState;

            if (ReachedTheLootScreen(state))
            {
                // The player was offered their loot. Whatever they left behind, they left behind.
                Logger.Information("[Loot] Encounter reached {State}; the loot screen was offered, so nothing is rescued", state);
                PendingBattleLoot.Clear();
                return;
            }

            PendingBattleLoot.AwardToMainParty(
                $"the encounter ended at {state?.ToString() ?? "no encounter"} without offering the loot screen");

            // The encounter is still holding its own copy of this same loot, because staging added it there and
            // the rescue only ever read a remembered reference to it. Anything that grants that copy after this
            // point - the rest of Finish, a screen that opens late - hands the player a SECOND set of the troops
            // they were just given, and only on this machine, so the client ends up holding twice what the server
            // credited. Take the copy away now that the loot has been paid out.
            ClearStagedLoot(encounter);
        }
        catch (Exception e)
        {
            // Never let a rescue attempt stop an encounter from finishing - that would strand the player on a
            // dead menu, which is worse than losing loot.
            Logger.Warning(e, "[Loot] Could not rescue staged loot while the encounter was finishing");
            PendingBattleLoot.Clear();
        }
    }

    /// <summary>Whether the encounter got far enough to put the loot in front of the player.</summary>
    internal static bool ReachedTheLootScreen(PlayerEncounterState? state)
        => state.HasValue && state.Value >= PlayerEncounterState.LootInventory;

    /// <summary>
    /// Empties the loot an encounter is still holding, once that loot has been paid out another way.
    /// </summary>
    /// <remarks>
    /// Loot is granted to the player exactly once, and the staged rosters are how the encounter decides it still
    /// owes them. Leaving them full after paying directly is what lets the same troops be credited twice.
    /// Applied inside an <see cref="AllowedThread"/> like every other authoritative roster change, so the roster
    /// patches do not read it as this machine proposing a change of its own.
    /// </remarks>
    private static void ClearStagedLoot(PlayerEncounter encounter)
    {
        if (encounter == null) return;

        using (new AllowedThread())
        {
            encounter.RosterToReceiveLootItems?.Clear();
            encounter.RosterToReceiveLootMembers?.Clear();
            encounter.RosterToReceiveLootPrisoners?.Clear();
        }
    }
}
