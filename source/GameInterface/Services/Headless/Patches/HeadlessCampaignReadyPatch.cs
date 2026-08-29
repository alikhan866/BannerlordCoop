using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.GameState.Messages;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.DotNet;
using TaleWorlds.Engine;
using TaleworldGameState = TaleWorlds.Core.GameState;

namespace GameInterface.Services.Headless.Patches
{
    /// <summary>
    /// Tells the server its campaign is ready, on a process that has no screen to tell it.
    /// </summary>
    /// <remarks>
    /// The server's state machine leaves InitialServerState - and only then binds UDP 4200 - when it
    /// receives CampaignReady. That message is published from a postfix on MapScreen.OnInitialize, which is
    /// a UI screen: on a headless server it never initialises, so the campaign loads completely, the world
    /// starts ticking, and the server still never listens. Nothing looks wrong in the log; there is simply
    /// no line saying the port opened.
    ///
    /// MapState becoming the active game state is the headless equivalent - it is what the client reaches
    /// just before its map screen initialises, and it means the campaign is loaded and running. The coop
    /// team's own dedicated server does the same thing, through a driver method named SignalCampaignReady.
    /// </remarks>
    [HarmonyPatch(typeof(TaleworldGameState))]
    internal class HeadlessCampaignReadyPatch
    {
        private static readonly ILogger Logger = LogManager.GetLogger<HeadlessCampaignReadyPatch>();

        /// <summary>
        /// The campaign this process has already announced, so the announcement is once PER CAMPAIGN.
        /// </summary>
        /// <remarks>
        /// A plain bool was wrong for a joining client, and silently so. Such a client reaches MapState TWICE:
        /// once with the throwaway campaign it creates to have something to join with, and again after the
        /// server's world has been received and loaded. A one-shot latch burns on the first, so the second -
        /// the only one that means anything - never publishes, LoadingState never completes, and the client
        /// sits in a fully loaded world that its own state machine does not believe is ready. Measured
        /// exactly: CampaignReady at 21:44:02 on the throwaway campaign, the real MapState at 21:44:11,
        /// nothing after it.
        ///
        /// Keying on the campaign instance tells the two apart without needing to know which is which. A
        /// server loads one campaign and still announces once. Weak, so a discarded campaign is not pinned
        /// alive by a diagnostic.
        /// </remarks>
        private static readonly WeakReference SignalledCampaign = new WeakReference(null);

        [HarmonyPostfix]
        [HarmonyPatch("OnActivate")]
        private static void OnActivate(ref TaleworldGameState __instance)
        {
            // Both render-free roles, not just the server. CampaignReady has only two publishers: this one,
            // and GameLoadedPatch hanging off MapScreen.OnInitialize - a UI screen a headless process never
            // builds. Gated on the server, a driven CLIENT fell between them: it reached MapState with the
            // campaign fully loaded, nothing published CampaignReady, its LoadingState never completed, and
            // the server sat on WaitingForCampaignEntry indefinitely.
            if (!ModInformation.IsHeadless) return;
            if (!(__instance is MapState)) return;

            Campaign campaign = Campaign.Current;
            if (campaign == null || ReferenceEquals(SignalledCampaign.Target, campaign)) return;

            bool again = SignalledCampaign.Target != null;
            SignalledCampaign.Target = campaign;
            Logger.Information(
                "[Headless] campaign is up; publishing CampaignReady (secondCampaign={Again})", again);


            MessageBroker.Instance.Publish(null, new CampaignReady());
        }

        // The late AI pass is NOT scheduled here any more. Creating an AsyncTask for it looked right and did
        // nothing: Campaign.WaitAsyncTasks only WAITS on CampaignLateAITickTask, and starting it belongs to the
        // engine's native task pool, which a windowless process does not run. The task sat untouched while the
        // world quietly stopped deciding anything. HeadlessLateAiTickPatch now calls Campaign.LateAITick
        // directly on the tick instead, which is the one way to be sure it happens.
    }
}
