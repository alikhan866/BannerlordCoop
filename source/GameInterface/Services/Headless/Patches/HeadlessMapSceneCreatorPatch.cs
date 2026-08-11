using Common;
using Common.Logging;
using HarmonyLib;
using SandBox;
using Serilog;
using TaleWorlds.CampaignSystem.Map;

namespace GameInterface.Services.Headless.Patches
{
    /// <summary>
    /// Hands a console dedicated server a map scene it can actually load.
    /// </summary>
    /// <remarks>
    /// Every map scene in the campaign comes through this one factory method, so replacing it here covers
    /// the whole game rather than guarding each caller. Client processes are untouched: they have a
    /// renderer and want the real thing.
    /// </remarks>
    [HarmonyPatch(typeof(MapSceneCreator))]
    internal class HeadlessMapSceneCreatorPatch
    {
        private static readonly ILogger Logger = LogManager.GetLogger<HeadlessMapSceneCreatorPatch>();

        // MapSceneCreator implements IMapSceneCreator explicitly, so the method carries its interface-
        // qualified name rather than a plain one.
        [HarmonyPatch("TaleWorlds.CampaignSystem.Map.IMapSceneCreator.CreateMapScene")]
        [HarmonyPrefix]
        private static bool CreateMapScenePrefix(ref IMapScene __result)
        {
            if (!ModInformation.IsHeadless) return true;

            Logger.Information("[Headless] substituting the render-free map scene");
            __result = new HeadlessMapScene();
            return false;
        }
    }
}
