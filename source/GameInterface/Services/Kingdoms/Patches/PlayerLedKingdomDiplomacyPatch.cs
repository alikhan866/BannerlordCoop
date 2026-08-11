using System;
using Common;
using Common.Logging;
using GameInterface.Configuration;
using GameInterface.Services.Heroes.Extensions;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Election;

namespace GameInterface.Services.Kingdoms.Patches;

/// <summary>
/// Stops the AI proposing war and peace on behalf of a kingdom a PLAYER leads, while
/// <see cref="ModOptions.PlayerLedKingdomsControlTheirOwnDiplomacy"/> is set.
/// </summary>
/// <remarks>
/// A player who rules a kingdom found it declaring war and making peace on its own, with no say in it. In
/// single player that cannot happen: as ruler you make those calls from the diplomacy screen, and the kingdom
/// does not go around you. In co-op the server runs every kingdom's AI, and it does not know that one of those
/// thrones has a person sitting on it - so <c>KingdomDecisionProposalBehavior</c> keeps proposing wars and
/// peaces for it, and they resolve without the ruler ever being asked.
///
/// Only the AI's PROPOSALS are suppressed, and only for a kingdom whose leader is a player. The player's own
/// diplomacy screen creates its decisions through <c>KingdomDiplomacyVM.OnDeclareWar</c> and
/// <c>OnDeclarePeace</c>, which are different call sites and are deliberately untouched - the point is to give
/// the ruler control, not to freeze their kingdom out of diplomacy altogether.
///
/// Everything else that can start a war still works: rebellion, crime rating, a call to war from an ally,
/// hostility in the field, kingdom creation. Those are consequences of things that actually happened rather
/// than an AI deciding policy for a kingdom that has a ruler.
/// </remarks>
[HarmonyPatch(typeof(KingdomDecisionProposalBehavior))]
internal class PlayerLedKingdomDiplomacyPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerLedKingdomDiplomacyPatch>();

    // Every __result below is typed from the game's own metadata, not from what the method looks like it
    // returns. Harmony validates that assignment when it builds the replacement, and a mismatch does not
    // fail this one patch - it throws out of PatchAll and takes EVERY patch in GameInterface with it, which
    // is a server that loads and then hosts nothing. GetRandomWarDecision returns the KingdomDecision base,
    // and both Consider* methods return bool.

    [HarmonyPatch("ConsiderWar")]
    [HarmonyPrefix]
    private static bool ConsiderWarPrefix(Clan clan, Kingdom kingdom, IFaction otherFaction, ref bool __result)
    {
        if (!Suppress(kingdom)) return true;

        __result = false;

        Logger.Debug("[Diplomacy] Refusing an AI war proposal against {Other} for player-led {Kingdom}",
            SafeName(otherFaction), SafeName(kingdom));
        return false;
    }

    [HarmonyPatch("GetRandomWarDecision")]
    [HarmonyPrefix]
    private static bool GetRandomWarDecisionPrefix(Clan clan, ref KingdomDecision __result)
    {
        if (!Suppress(clan?.Kingdom)) return true;

        __result = null;
        return false;
    }

    [HarmonyPatch("ConsiderPeace")]
    [HarmonyPrefix]
    private static bool ConsiderPeacePrefix(Clan clan, Clan otherClan, IFaction otherFaction, ref MakePeaceKingdomDecision decision, ref bool __result)
    {
        if (!Suppress(clan?.Kingdom)) return true;

        decision = null;
        __result = false;

        Logger.Debug("[Diplomacy] Refusing an AI peace proposal with {Other} for player-led {Kingdom}",
            SafeName(otherFaction), SafeName(clan?.Kingdom));
        return false;
    }

    /// <summary>Whether this kingdom's diplomacy belongs to a player rather than to the AI.</summary>
    /// <remarks>
    /// Server-side only: the server is where the kingdom AI runs, and it is the only machine that knows which
    /// heroes are players. Guarded because it is consulted from a campaign tick, and refusing to answer must
    /// mean "let vanilla proceed" rather than throwing into the tick.
    /// </remarks>
    internal static bool Suppress(Kingdom kingdom)
    {
        if (ModInformation.IsClient) return false;
        if (!ModConfigProvider.ModOptions.PlayerLedKingdomsControlTheirOwnDiplomacy) return false;

        try
        {
            return IsLedByAPlayer(kingdom);
        }
        catch (Exception e)
        {
            Logger.Warning(e, "[Diplomacy] Could not tell whether {Kingdom} is player-led; leaving vanilla in charge",
                SafeName(kingdom));
            return false;
        }
    }

    /// <summary>Whether a player hero sits on this kingdom's throne.</summary>
    internal static bool IsLedByAPlayer(Kingdom kingdom) => kingdom?.Leader?.IsPlayerHero() == true;

    private static string SafeName(IFaction faction)
    {
        try { return faction?.Name?.ToString() ?? "<none>"; } catch { return "?"; }
    }
}
