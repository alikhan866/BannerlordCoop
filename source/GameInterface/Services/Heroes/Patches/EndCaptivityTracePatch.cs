using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace GameInterface.Services.Heroes.Patches;

/// <summary>
/// Records every captivity that ends on the server, with the detail that ended it.
/// </summary>
/// <remarks>
/// Added because a released lord granted no relation and the logs could not say why. A release reaches the
/// server through four different handlers - LiberateLordPrisoner, LordDefeatToRelease, LordFreedToRelease and
/// LordHelpedInBattle - and only three of them apply a relation change; the fourth has the change commented
/// out with a TODO. On top of that the server can end a captivity on its own (peace, ransom, escape), which
/// correctly grants the player nothing. All five leave the same evidence behind - the hero simply ends up at
/// <c>_heroState = Released</c> - so after the fact there is no way to tell which one ran, and the question
/// "should this have given relation?" cannot be answered.
///
/// One postfix on the shared action covers all five paths, including any added later, which is why it goes
/// here rather than a log line in each handler.
///
/// Server only: on a client this method is not the authority and the same release would be logged twice.
/// </remarks>
[HarmonyPatch(typeof(EndCaptivityAction), "ApplyInternal")]
internal class EndCaptivityTracePatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<EndCaptivityTracePatch>();

    // "facilitatior" is vanilla's own spelling of the parameter. Harmony binds injected arguments BY NAME,
    // so correcting the typo here would silently stop this argument being filled.
    [HarmonyPostfix]
    private static void Trace(Hero prisoner, EndCaptivityDetail detail, Hero facilitatior)
    {
        if (!ModInformation.IsServer) return;

        Logger.Information(
            "[Captivity] {Prisoner} released (detail={Detail}, by={Facilitator}); any relation reward is granted " +
            "by the calling handler, not by this action",
            prisoner?.StringId,
            detail,
            facilitatior?.StringId ?? "<none>");
    }
}
