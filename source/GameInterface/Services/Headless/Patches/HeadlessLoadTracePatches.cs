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
            // Game-manager startup, which runs BEFORE anything below and is where a render-free CLIENT dies -
            // earlier than any Campaign step, so the original list was silent for it and the log tail was the
            // only evidence. The tail is not a locator here: this phase runs with a parallelism of ten, and
            // the last line printed wandered between runs while the actual failure did not.
            //
            // One-shot methods only. DoLoadingForGameManager is deliberately absent: it is called once per
            // frame, and burying the answer in thousands of lines is its own kind of blindness.
            (typeof(TaleWorlds.MountAndBlade.MBGameManager), "OnLoadFinished"),
            (typeof(SandBox.SandBoxGameManager), "OnLoadFinished"),
            (typeof(SandBox.SandBoxGameManager), "OnGameStart"),
            (typeof(SandBox.SandBoxGameManager), "InitializeGameStarter"),
            (typeof(SandBox.SandBoxGameManager), "OnNewCampaignStart"),
            (typeof(Campaign), "DoLoadingForGameType"),

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

        /// <summary>
        /// Logs the exception that ends a load step, then lets it carry on.
        /// </summary>
        /// <remarks>
        /// An exception thrown out of one of these steps kills the process before any of our own handlers see
        /// it: the engine has no top-level handler on the load path, so the only trace left is the exit code
        /// (0xE0434352, "a managed exception happened"), which names neither the type nor the frame. The
        /// missing "done" line above tells us which step died; this tells us why.
        ///
        /// Not headless-gated, unlike the trace lines - the same crash takes the game-window server down too,
        /// and there the log is the only thing anyone can read afterwards. Returning the exception rethrows
        /// it, so behaviour is unchanged; swallowing it here would trade a hard crash for a half-built world.
        /// </remarks>
        [HarmonyFinalizer]
        private static Exception Finalizer(MethodBase __originalMethod, Exception __exception)
        {
            if (__exception != null)
            {
                Logger.Error("[Headless] load step: {Type}.{Step} THREW {Exception}",
                    __originalMethod.DeclaringType?.Name, __originalMethod.Name, __exception.ToString());
            }

            return __exception;
        }
    }
}
