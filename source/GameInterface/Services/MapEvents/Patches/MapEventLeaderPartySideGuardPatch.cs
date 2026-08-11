using Common.Logging;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Patches;

/// <summary>
/// Reports "no leader party" for a side this map event does not have yet, instead of throwing.
/// </summary>
/// <remarks>
/// <see cref="MapEvent.GetLeaderParty"/> indexes a fixed <c>MapEventSide[2]</c> with whatever side it is
/// handed, so <see cref="BattleSideEnum.None"/> (-1) is an IndexOutOfRangeException rather than an answer.
/// Vanilla never asks: a party receives its MapEvent and its MapEventSide in the same call, so
/// <c>PartyBase.Side</c> is never None while <c>MapEvent</c> is set. In coop the two arrive as separate
/// replicated messages, and in the window between them the local party holds a MapEvent whose side is
/// still null - Side returns None, and the first vanilla caller to ask throws.
///
/// Measured 2026-08-11 while helping a besieged own castle (castle_ES3). The throw came out of
/// <c>game_menu_town_besiege_continue_siege_on_condition</c>, which runs inside <c>GameMenuVM.Refresh</c>
/// while it evaluates each option's condition. An exception there aborts the refresh part-way, so the menu
/// is built from only the options evaluated before the throw: the player is left on a siege menu offering
/// "Capture the enemy" and "Leave...", with no option to join the assault. The side attached about a second
/// later, but the menu had already been built and was never refreshed again - hence stuck, not slow.
///
/// Guarding the accessor rather than that one condition is deliberate. Every caller routes through here -
/// vanilla's menu conditions, the siege-end patches, the battle launchers - so one guard covers all of
/// them, and null is what those callers already handle for a side with no leader.
/// </remarks>
[HarmonyPatch(typeof(MapEvent), nameof(MapEvent.GetLeaderParty))]
internal class MapEventLeaderPartySideGuardPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<MapEventLeaderPartySideGuardPatch>();

    [HarmonyPrefix]
    private static bool GuardMissingSide(MapEvent __instance, BattleSideEnum side, ref PartyBase __result)
    {
        var sides = __instance?._sides;
        var index = (int)side;

        // Both failure modes in one check: an index off the end of the array (side = None), and a side
        // slot that exists but has not been filled in yet, which would throw a step later on LeaderParty.
        if (sides != null && index >= 0 && index < sides.Length && sides[index] != null) return true;

        Logger.Debug(
            "{MapEvent} has no {Side} side yet; reporting no leader party rather than throwing",
            __instance?.StringId, side);

        __result = null;
        return false;
    }
}
