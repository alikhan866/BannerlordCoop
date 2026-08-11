using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.ObjectSystem;

namespace GameInterface.Services.Headless.Patches
{
    /// <summary>
    /// Logs each step of campaign loading on a headless server.
    /// </summary>
    /// <remarks>
    /// A windowless engine that hits something it cannot do without a renderer dies natively - no managed
    /// exception, no stack, and the last line in the log is whatever happened to be printed before it. That
    /// makes "which step failed" unanswerable by reading logs, and answering it by rebuilding around one
    /// probe at a time costs a full build and load per guess.
    ///
    /// So every step of Campaign.DoLoadingForGameType announces itself. The step that never logs its "done"
    /// is the one that killed the process. Server-only and cheap: a handful of log lines, once, during load.
    /// </remarks>
    [HarmonyPatch]
    internal class HeadlessLoadTracePatches
    {
        private static readonly ILogger Logger = LogManager.GetLogger<HeadlessLoadTracePatches>();

        /// <summary>
        /// The load sequence, in the order it runs: Campaign.DoLoadingForGameType first, then the steps
        /// inside OnGameLoaded, which is where a headless load actually stops.
        /// </summary>
        private static readonly (Type Type, string Method)[] TracedMethods =
        {
            (typeof(Campaign), "LoadMapScene"),
            (typeof(Campaign), "CheckMapUpdate"),
            (typeof(Campaign), "OnDataLoadFinished"),
            (typeof(Campaign), "CalculateCachedValues"),
            (typeof(Campaign), "CalculateCachedStatsOnLoad"),
            (typeof(Campaign), "OnGameLoaded"),

            (typeof(MBObjectManager), "PreAfterLoad"),
            (typeof(CampaignObjectManager), "PreAfterLoad"),
            (typeof(MBObjectManager), "AfterLoad"),
            (typeof(CampaignObjectManager), "AfterLoad"),
            (typeof(CharacterRelationManager), "AfterLoad"),
            (typeof(FactionManager), "AfterLoad"),
            (typeof(CampaignEventDispatcher), "OnGameEarlyLoaded"),
            (typeof(CampaignEventDispatcher), "OnGameLoaded"),
            (typeof(Campaign), "InitializeForSavedGame"),

            (typeof(Campaign), "OnSessionStart"),
            (typeof(Campaign), "InitializeMainParty"),
            (typeof(Campaign), "OnNewGameCreated"),
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var traced in TracedMethods)
            {
                var method = AccessTools.Method(traced.Type, traced.Method);
                if (method != null) yield return method;
            }
        }

        [HarmonyPrefix]
        private static void Prefix(MethodBase __originalMethod)
        {
            if (!ModInformation.IsHeadless) return;

            Logger.Information("[Headless] load step: {Type}.{Step} ...", __originalMethod.DeclaringType?.Name, __originalMethod.Name);
        }

        [HarmonyPostfix]
        private static void Postfix(MethodBase __originalMethod)
        {
            if (!ModInformation.IsHeadless) return;

            Logger.Information("[Headless] load step: {Type}.{Step} done", __originalMethod.DeclaringType?.Name, __originalMethod.Name);
        }
    }
}
