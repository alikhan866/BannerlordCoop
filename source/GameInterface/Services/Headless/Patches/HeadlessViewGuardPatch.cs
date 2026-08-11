using Common;
using HarmonyLib;
using SandBox.View.Map.Managers;

namespace GameInterface.Services.Headless.Patches
{
    /// <summary>
    /// Makes the map's visual manager report absent instead of throwing, on a server that has no visuals.
    /// </summary>
    /// <remarks>
    /// Eight places in this codebase guard their use of MobilePartyVisualManager with
    /// <c>Current == null</c> or <c>Current?.</c>, which reads as correct and is how the API is meant to
    /// behave. It is not what happens: with no view submodule loaded, the getter dereferences
    /// SandBoxViewSubModule and throws a NullReferenceException, so the guard throws before it can guard
    /// anything.
    ///
    /// That is not a cosmetic loss. One of those call sites is reached from DestroyPartyAction via
    /// GameThread, so the exception killed the whole queued action and the party was never fully removed -
    /// observed live, twice, in a three-minute session.
    ///
    /// Patching the getter fixes all eight at once, and leaves every existing null check reading exactly as
    /// its author intended. Clients are untouched: they have a visual manager and want the real one.
    /// </remarks>
    [HarmonyPatch(typeof(MobilePartyVisualManager))]
    internal class HeadlessViewGuardPatch
    {
        [HarmonyPatch(nameof(MobilePartyVisualManager.Current), MethodType.Getter)]
        [HarmonyPrefix]
        private static bool CurrentPrefix(ref MobilePartyVisualManager __result)
        {
            if (!ModInformation.IsHeadless) return true;

            __result = null;
            return false;
        }
    }
}
