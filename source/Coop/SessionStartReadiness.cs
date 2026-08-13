using Common;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Coop
{
    /// <summary>
    /// Whether the engine is far enough along for a co-op session to be started.
    /// </summary>
    /// <remarks>
    /// A rendered process starts a session from the main menu, so "is the active state InitialState" is the
    /// natural test - and it is the test every start path here originally used.
    ///
    /// A render-free process NEVER reaches InitialState. There is no UI to show a main menu with, so the state
    /// is never pushed, and anything waiting for it waits forever. The dedicated server hit this first and
    /// worked around it inline; a headless client hits exactly the same wall on the join path, which is what
    /// made it worth having one answer instead of three.
    ///
    /// What a start actually needs is not a menu but a state manager to push the loading state onto, so that
    /// is what the render-free answer tests for.
    /// </remarks>
    internal static class SessionStartReadiness
    {
        internal static bool CanStartSession()
        {
            var stateManager = GameStateManager.Current;
            if (stateManager == null) return false;

            // Deliberately not "headless OR manager exists": a rendered client that is still on the splash
            // screen has a state manager too, and starting a session from there would race the menu it is
            // about to build.
            return stateManager.ActiveState is InitialState || ModInformation.IsHeadless;
        }

        /// <summary>Why a start was refused, for a caller that reports rather than waits.</summary>
        internal static string DescribeState()
        {
            var stateManager = GameStateManager.Current;
            if (stateManager == null) return "no game state manager";

            return stateManager.ActiveState?.GetType().Name ?? "no active state";
        }
    }
}
