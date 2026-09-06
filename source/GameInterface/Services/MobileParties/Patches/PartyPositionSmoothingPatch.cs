using System;
using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem.GameState;

namespace GameInterface.Services.MobileParties.Patches;

/// <summary>
/// Drives <see cref="PartyPositionSmoothing"/> from the campaign's per-frame tick, which is the only place a
/// client-side correction can be spread over frames.
/// </summary>
/// <remarks>
/// The map's own frame tick, because the campaign's ticks are not frames: <c>Campaign.Tick</c> runs about once a
/// second and <c>Campaign.RealTick</c> about twice, and draining on either stretched a third-of-a-second
/// correction over tens of seconds - measured both times by residuals outliving their corrections (6 Sep 2026:
/// 444 outstanding at once, average life 47 s). The drain reports its own rate now, so a hook that is not per
/// frame says so instead of quietly not smoothing.
///
/// A throw here would take the campaign tick down with it, so the drain is guarded and reports once rather than
/// per frame: a correction that fails to land is a party in the wrong place, not a reason to end the session.
/// </remarks>
[HarmonyPatch(typeof(MapState), "OnMapModeTick")]
internal static class PartyPositionSmoothingPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(PartyPositionSmoothingPatch));

    // Reported at most this often. Reporting ONCE hid a drain that threw every single frame for a whole run.
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(30);

    private static DateTime nextReport = DateTime.MinValue;

    private static long failures;

    [HarmonyPostfix]
    private static void Postfix()
    {
        if (ModInformation.IsServer) return;
        try
        {
            PartyPositionSmoothing.Drain();
        }
        catch (Exception e)
        {
            failures++;
            var now = DateTime.UtcNow;
            if (now < nextReport) return;
            nextReport = now + ReportInterval;
            Logger.Error(e, "Party position smoothing has failed {Failures} time(s); corrections are arriving in one frame instead", failures);
        }
    }
}
