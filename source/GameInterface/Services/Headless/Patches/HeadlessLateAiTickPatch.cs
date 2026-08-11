using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Headless.Patches
{
    /// <summary>
    /// Runs the campaign's late AI pass on a headless server, where nothing else will.
    /// </summary>
    /// <remarks>
    /// <c>Campaign.LateAITick</c> is what lets a party decide what to do NEXT. Without it a party keeps
    /// whatever order it already has and strands the moment that order completes - so a headless world starts
    /// out looking healthy and grinds down as parties arrive at their destinations one by one. Measured on the
    /// same save, same build, same client, with only the host swapped:
    ///
    ///     windowed    moved 668-710   stalled 28-41    (steady)
    ///     headless    moved 665 -> 422 -> 312 -> 298 -> 241
    ///                 stalled 45 -> 95 -> 119 -> 123 -> 121   (decaying)
    ///
    /// The reason is the shape of the engine's own plumbing. <c>Campaign.WaitAsyncTasks</c> - the only thing
    /// that touches <c>CampaignLateAITickTask</c> - merely WAITS on it:
    ///
    ///     if (CampaignLateAITickTask != null) CampaignLateAITickTask.Wait();
    ///
    /// Starting it is the engine's native task pool's job, and on a windowless process that never happens. So
    /// an earlier attempt here, which created the task and assumed the engine would run it, produced a task
    /// that sat untouched forever - all of the waiting, none of the ticking.
    ///
    /// Calling it directly each tick is the honest fix for a host with no task pool. It costs the parallelism
    /// the async task exists for, which a dedicated server can afford far more easily than a frozen map. It
    /// runs AFTER the tick rather than alongside it, which is also why <see cref="HeadlessCampaignReadyPatch"/>
    /// no longer creates the task: with nothing to wait for, WaitAsyncTasks becomes the no-op it already is.
    /// </remarks>
    [HarmonyPatch(typeof(Campaign), "RealTick")]
    internal class HeadlessLateAiTickPatch
    {
        private static readonly ILogger Logger = LogManager.GetLogger<HeadlessLateAiTickPatch>();

        // One report only: this runs every tick, and a failing AI pass would otherwise bury the log.
        private static bool reportedFailure;

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!ModInformation.IsHeadless) return;
            if (Campaign.Current == null) return;

            try
            {
                Campaign.LateAITick();
            }
            catch (Exception e)
            {
                if (reportedFailure) return;

                reportedFailure = true;
                Logger.Error(e, "[Headless] the late AI tick threw; parties will stop deciding what to do next");
            }
        }
    }
}
