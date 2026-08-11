using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Kingdoms.Patches;

/// <summary>
/// Records who declared each war and why. Diagnostic only - it changes nothing.
/// </summary>
/// <remarks>
/// <c>PlayerLedKingdomDiplomacyPatch</c> refuses AI war PROPOSALS for a player-led kingdom, but that only
/// covers <c>KingdomDecisionProposalBehavior</c>. A war can still start from a call to war by an ally,
/// hostility in the field, rebellion, a crime rating change, a claim on the throne or kingdom creation -
/// all deliberately left alone, because they are consequences of things that actually happened rather than
/// an AI setting policy for a kingdom that has a ruler.
///
/// So when a player-led kingdom declares a war nobody asked for, the useful question is which of those
/// fired, and nothing logged it. Every public entry point funnels into <c>ApplyInternal</c>, which carries
/// a <c>DeclareWarDetail</c> naming the cause - so one patch here answers it for all of them.
///
/// Grep the server log for <c>[WarDiag]</c>.
/// </remarks>
[HarmonyPatch(typeof(DeclareWarAction), "ApplyInternal")]
internal class DeclareWarDiagnosticPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<DeclareWarDiagnosticPatch>();

    [HarmonyPrefix]
    private static void Prefix(IFaction faction1, IFaction faction2, DeclareWarAction.DeclareWarDetail declareWarDetail)
    {
        try
        {
            Logger.Warning(
                "[WarDiag] {Faction1} declares war on {Faction2}. Detail={Detail} PlayerLed1={PlayerLed1} PlayerLed2={PlayerLed2}\nCaller:\n{Stack}",
                SafeName(faction1),
                SafeName(faction2),
                declareWarDetail,
                IsPlayerLed(faction1),
                IsPlayerLed(faction2),
                Environment.StackTrace);
        }
        catch (Exception e)
        {
            // A diagnostic must never be the reason a war fails to be declared.
            Logger.Warning(e, "[WarDiag] Could not record a war declaration");
        }
    }

    private static string SafeName(IFaction faction)
    {
        try { return faction?.Name?.ToString() ?? "<none>"; } catch { return "?"; }
    }

    private static bool IsPlayerLed(IFaction faction)
    {
        try { return PlayerLedKingdomDiplomacyPatch.IsLedByAPlayer(faction as Kingdom); } catch { return false; }
    }
}
