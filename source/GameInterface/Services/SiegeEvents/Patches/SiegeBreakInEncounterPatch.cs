using Common;
using Common.Logging;
using GameInterface.Policies;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.SiegeEvents.Patches;

/// <summary>
/// Makes "Break in to help the defenders" open its menu instead of quietly putting the player back where
/// they started.
/// </summary>
/// <remarks>
/// The option's consequence is one line - <c>GameMenu.SwitchToMenu("break_in_menu")</c> - so everything
/// depends on that menu's init, and vanilla's begins with a bail-out:
///
///     if (PlayerEncounter.Current != null &amp;&amp; PlayerEncounter.EncounterSettlement.Party.SiegeEvent == null)
///     {
///         PlayerEncounter.Finish(true);
///         return;
///     }
///
/// It finishes the encounter and returns without building the menu. Nothing throws, nothing is logged, and
/// the player is dropped back to the map where the siege is immediately re-encountered - which is precisely
/// the reported "it does nothing and brings me back to the same popup". Silence is why this went unexplained:
/// the swallowed-exception logging that caught the capture-the-enemy bug has nothing to catch here.
///
/// Single-player never trips it because the player encounters the SETTLEMENT. A co-op relief force encounters
/// the besieger camp, so the encounter's settlement is not the besieged one - the same mismatch that made
/// <see cref="SiegeReliefJoinSidePatch.ResolveBesiegedSettlement"/> need four sources to find it at all.
///
/// So the encounter is re-pointed at the besieged settlement and vanilla's own menu then builds normally,
/// with vanilla's troop-loss arithmetic and vanilla's actions. Nothing here enters a settlement or spends
/// troops: the sacrifice still goes through <c>BreakInCasualtiesPatch</c> and the entry through
/// <c>SiegeEntryFlowPatches.BreakInContinuationPrefix</c>, both of which already round-trip the server.
///
/// It acts only when the guard WOULD have bailed out - when the option is already broken - so the worst case
/// is the log line and today's behaviour.
/// </remarks>
[HarmonyPatch(typeof(EncounterGameMenuBehavior), "game_menu_join_siege_event_on_defender_side_on_consequence")]
internal class SiegeBreakInEncounterPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<SiegeBreakInEncounterPatch>();

    [HarmonyPrefix]
    private static bool Prefix()
    {
        if (CallOriginalPolicy.IsOriginalAllowed()) return true;
        if (ModInformation.IsServer) return true;

        var encounter = PlayerEncounter.Current;
        var encounterSettlement = PlayerEncounter.EncounterSettlement;
        var currentSettlement = Settlement.CurrentSettlement;

        Logger.Information(
            "[BreakIn] taken: encounter={Encounter} encounterSettlement={EncounterSettlement} (siege={EncounterSiege}) " +
            "currentSettlement={CurrentSettlement} (siege={CurrentSiege})",
            encounter != null,
            encounterSettlement?.StringId ?? "<none>", encounterSettlement?.SiegeEvent != null,
            currentSettlement?.StringId ?? "<none>", currentSettlement?.SiegeEvent != null);

        // No encounter means the guard cannot trip, and vanilla builds the menu from CurrentSettlement.
        if (encounter == null) return true;

        // The guard passes: the encounter already knows about the siege. Leave vanilla alone.
        if (encounterSettlement?.SiegeEvent != null) return true;

        var besieged = SiegeReliefJoinSidePatch.ResolveBesiegedSettlement(out var source);
        if (besieged?.SiegeEvent == null)
        {
            Logger.Warning(
                "[BreakIn] the encounter has no besieged settlement and none could be resolved (via {Source}); " +
                "leaving this to vanilla, which will close the encounter",
                source);
            return true;
        }

        var mainParty = MobileParty.MainParty?.Party;
        var besiegedParty = besieged.Party;
        if (mainParty == null || besiegedParty == null)
        {
            Logger.Warning("[BreakIn] cannot re-point the encounter at {Settlement}: no main party or settlement party",
                besieged.StringId);
            return true;
        }

        Logger.Information(
            "[BreakIn] re-pointing the encounter at {Settlement} (found via {Source}) so the break-in menu can build",
            besieged.StringId, source);

        encounter.Init(mainParty, besiegedParty, besieged);

        Logger.Information("[BreakIn] encounter settlement is now {Settlement} (siege={Siege})",
            PlayerEncounter.EncounterSettlement?.StringId ?? "<none>",
            PlayerEncounter.EncounterSettlement?.SiegeEvent != null);

        return true;
    }

    /// <summary>
    /// Reports where the consequence actually left the player, and any exception it died on.
    /// </summary>
    /// <remarks>
    /// A finalizer rather than a postfix, because a menu consequence that throws is swallowed upstream: the
    /// click then does nothing, silently, which is the whole difficulty with this option. Naming the menu the
    /// player ends on separates "the switch never happened" from "the menu opened and something put it back".
    /// </remarks>
    [HarmonyFinalizer]
    private static void Finalizer(System.Exception __exception)
    {
        if (ModInformation.IsServer) return;

        Logger.Information("[BreakIn] consequence finished: menu is now '{Menu}', exception={Exception}",
            Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId ?? "<none>",
            __exception?.ToString() ?? "none");
    }
}

/// <summary>
/// Watches the break-in menu's own init, which is where the flow either builds or quietly gives up.
/// </summary>
/// <remarks>
/// Pure observation - it changes nothing. It exists because the failure produces no exception and no log of
/// its own, so the only way to tell "the init never ran" from "the init ran and bailed" is to say so here.
/// </remarks>
[HarmonyPatch(typeof(EncounterGameMenuBehavior), "break_in_out_menu_on_init")]
internal class SiegeBreakInMenuInitDiagnosticPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<SiegeBreakInMenuInitDiagnosticPatch>();

    [HarmonyPrefix]
    private static void Prefix(bool isBreakIn)
    {
        if (ModInformation.IsServer) return;

        Logger.Information(
            "[BreakIn] break_in_out_menu_on_init entered (isBreakIn={IsBreakIn}) with encounter={Encounter} " +
            "encounterSettlement={EncounterSettlement} (siege={EncounterSiege}) currentSettlement={CurrentSettlement}",
            isBreakIn, PlayerEncounter.Current != null,
            PlayerEncounter.EncounterSettlement?.StringId ?? "<none>",
            PlayerEncounter.EncounterSettlement?.SiegeEvent != null,
            Settlement.CurrentSettlement?.StringId ?? "<none>");
    }

    [HarmonyFinalizer]
    private static void Finalizer(System.Exception __exception)
    {
        if (ModInformation.IsServer) return;

        Logger.Information("[BreakIn] break_in_out_menu_on_init left: encounter={Encounter} menu='{Menu}' exception={Exception}",
            PlayerEncounter.Current != null,
            Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId ?? "<none>",
            __exception?.ToString() ?? "none");
    }
}

/// <summary>
/// Names whatever takes the break-in menu away again.
/// </summary>
/// <remarks>
/// The menu opens correctly - measured, with its troop-loss estimate and both options - and is then replaced
/// a moment later, which is what the player experiences as the option doing nothing. Every candidate ruled out
/// so far was ruled out by reading code, and each cost a round trip, so this asks the machine instead: the one
/// thing that can say WHO is a stack trace taken at the moment of the theft.
///
/// The existing SiegeEncounterMenuTrace does exactly this, and better, but the whole file is <c>#if DEBUG</c>
/// and so is absent from the build being played. This is the same idea narrowed to one menu, cheap enough to
/// leave in Release: it can only fire while break_in_menu is the current menu, which is a handful of frames
/// in a whole session.
/// </remarks>
[HarmonyPatch]
internal class SiegeBreakInMenuStealTracePatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<SiegeBreakInMenuStealTracePatch>();

    private static bool BreakInMenuIsOpen =>
        Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId == "break_in_menu";

    private static void Report(string what)
    {
        if (ModInformation.IsServer || !BreakInMenuIsOpen) return;

        Logger.Warning("[BreakIn] the break-in menu is being taken away by {What}:{NewLine}{Stack}",
            what, System.Environment.NewLine, System.Environment.StackTrace);
    }

    [HarmonyPatch(typeof(GameMenu), nameof(GameMenu.ActivateGameMenu), typeof(string))]
    [HarmonyPrefix]
    private static void ActivatePrefix(string menuId) => Report($"ActivateGameMenu(\"{menuId}\")");

    [HarmonyPatch(typeof(GameMenu), nameof(GameMenu.SwitchToMenu), typeof(string))]
    [HarmonyPrefix]
    private static void SwitchPrefix(string menuId) => Report($"SwitchToMenu(\"{menuId}\")");

    [HarmonyPatch(typeof(GameMenu), nameof(GameMenu.ExitToLast))]
    [HarmonyPrefix]
    private static void ExitPrefix() => Report("ExitToLast()");

    // Deliberately NOT PlayerEncounter.Init: it is overloaded, so naming it without an argument list is an
    // ambiguous match, and Harmony throws for the whole class - which aborts the mod's patching at startup and
    // takes the server down with it. Nothing is lost by leaving it out: Init ends by activating the encounter
    // menu, so the ActivateGameMenu trace above already catches it, and with a stack trace naming Init.
    [HarmonyPatch(typeof(PlayerEncounter), nameof(PlayerEncounter.Finish), typeof(bool))]
    [HarmonyPrefix]
    private static void EncounterFinishPrefix() => Report("PlayerEncounter.Finish()");
}
