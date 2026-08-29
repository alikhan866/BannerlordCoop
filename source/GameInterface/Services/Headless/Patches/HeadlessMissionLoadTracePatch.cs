using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.Engine;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.Headless.Patches;

/// <summary>
/// Brackets a mission load, so a render-free process that dies inside one says how far it got.
/// </summary>
/// <remarks>
/// WHY "IT DIES NATIVELY" WAS NOT GOOD ENOUGH
/// A headless client dies during the mission load with no managed frame and no crash dump, and the last thing
/// it wrote was the mission being OPENED - which happens a whole tick earlier. Every hypothesis built on that
/// line assumed the next thing to run was the thing that failed. Two confident answers came out of that and
/// both were wrong; bracketing the calls replaced them with measurements.
///
/// WHAT IT ESTABLISHED
///   - Mission.Initialize RETURNS. A render-free process starts a campaign battle scene load without
///     complaint, and all 36 mission behaviours pre-load, including two ...View classes.
///   - LoadMission returns too, and FinishMissionLoading is never entered - so nothing inside it, including
///     the plausible-looking Scene.ResumeLoadingRenderings, was ever a suspect.
///   - The trace ends on Mission.IsLoadingFinished -> false. Scene loading is ASYNCHRONOUS: Initialize starts
///     it, TickLoading polls, and the work happens on engine loader threads. That is why the fault never
///     produces a managed frame - it is not on this thread.
///   - Mission.Current.Scene is NULL immediately after Initialize; the scene object appears partway through
///     the load, so nothing can be configured on it at that point.
///
/// AND WHY THAT IS THE END OF THE MANAGED ROAD
/// The dedicated-server engine loads no graphics modules at all - no d3d11, no dxgi - where a rendered client
/// loads four. The native scene load needs a device this process does not have. Clearing the loading screen,
/// and applying both of the engine's own no-renderer scene switches, were each confirmed applied and each
/// still faulted with 0xC0000005.
///
/// Kept as instrumentation rather than as a fix attempt: it is the first thing anybody retrying headless
/// missions will want, and it costs a rendered client nothing - every method here returns immediately unless
/// the process is render-free.
/// </remarks>
[HarmonyPatch]
internal static class HeadlessMissionLoadTracePatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(HeadlessMissionLoadTracePatch));

    private static bool reportedLoadingScreen;
    private static bool? lastLoadingFinished;

    [HarmonyPatch(typeof(MissionState), "LoadMission")]
    [HarmonyPrefix]
    private static void BeforeLoadMission()
    {
        if (!ModInformation.IsHeadless) return;

        reportedLoadingScreen = false;
        lastLoadingFinished = null;
        Logger.Information("[MissionLoad] 1/6 LoadMission entered");
    }

    [HarmonyPatch(typeof(MissionState), "LoadMission")]
    [HarmonyPostfix]
    private static void AfterLoadMission()
    {
        if (!ModInformation.IsHeadless) return;
        Logger.Information("[MissionLoad] 3/6 LoadMission returned");
    }

    /// <summary>Names each behaviour as its pre-load runs, so a fatal one is identified rather than guessed.</summary>
    /// <remarks>
    /// Patched on the base declaration, so an override that does not call base would be missed. A behaviour
    /// that appears here is confirmed to have started; a missing one proves nothing.
    /// </remarks>
    [HarmonyPatch(typeof(MissionBehavior), nameof(MissionBehavior.OnMissionScreenPreLoad))]
    [HarmonyPrefix]
    private static void BeforeBehaviorPreLoad(MissionBehavior __instance)
    {
        if (!ModInformation.IsHeadless) return;
        Logger.Information("[MissionLoad] 1/6 preload {Behavior}", __instance?.GetType().Name ?? "?");
    }

    [HarmonyPatch(typeof(Mission), "Initialize")]
    [HarmonyPrefix]
    private static void BeforeInitialize()
    {
        if (!ModInformation.IsHeadless) return;

        // Mission.Current is not set yet - Initialize sets it first - so this falls back to the mission name.
        string scene = Read(() => Mission.Current?.SceneName) ?? Read(() => MissionState.Current?.MissionName) ?? "?";
        Logger.Information("[MissionLoad] 2/6 Mission.Initialize entering (native) scene={Scene}", scene);
    }

    [HarmonyPatch(typeof(Mission), "Initialize")]
    [HarmonyPostfix]
    private static void AfterInitialize()
    {
        if (!ModInformation.IsHeadless) return;

        // "Started", not "finished": the load is asynchronous from here, and Mission.Current.Scene is still
        // null at this point - the scene object appears partway through.
        Logger.Information(
            "[MissionLoad] 2/6 Mission.Initialize returned - the scene load has STARTED, scenePresent={Scene}",
            Read(() => Mission.Current?.Scene != null));
    }

    /// <summary>The loading screen percentage, reported once per load rather than once per frame.</summary>
    [HarmonyPatch(typeof(Utilities), nameof(Utilities.SetLoadingScreenPercentage))]
    [HarmonyPrefix]
    private static void BeforeSetLoadingScreenPercentage()
    {
        if (!ModInformation.IsHeadless || reportedLoadingScreen) return;

        reportedLoadingScreen = true;
        Logger.Information("[MissionLoad] 4/6 SetLoadingScreenPercentage entering (native)");
    }

    /// <summary>Whether the engine considers the scene loaded, reported when the answer changes.</summary>
    [HarmonyPatch(typeof(Mission), nameof(Mission.IsLoadingFinished), MethodType.Getter)]
    [HarmonyPostfix]
    private static void AfterIsLoadingFinished(bool __result)
    {
        if (!ModInformation.IsHeadless || lastLoadingFinished == __result) return;

        lastLoadingFinished = __result;
        Logger.Information("[MissionLoad] 5/6 Mission.IsLoadingFinished -> {Finished}", __result);
    }

    [HarmonyPatch(typeof(MissionState), "FinishMissionLoading")]
    [HarmonyPrefix]
    private static void BeforeFinishMissionLoading()
    {
        if (!ModInformation.IsHeadless) return;
        Logger.Information("[MissionLoad] 6/6 FinishMissionLoading entered");
    }

    [HarmonyPatch(typeof(MissionState), "FinishMissionLoading")]
    [HarmonyPostfix]
    private static void AfterFinishMissionLoading()
    {
        if (!ModInformation.IsHeadless) return;
        Logger.Information("[MissionLoad] 6/6 FinishMissionLoading returned - the mission is live");
    }

    private static T Read<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
