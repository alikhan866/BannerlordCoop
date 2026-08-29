using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Headless.Patches
{
    /// <summary>
    /// Names the hero whose <c>AfterLoad</c> throws, instead of letting the whole campaign load fail anonymously.
    /// </summary>
    /// <remarks>
    /// <c>CampaignObjectManager.AfterLoad</c> runs <c>Hero.AfterLoad</c> over every hero in the save, and one
    /// bad hero takes the load down with a bare NullReferenceException whose stack names only
    /// <c>ClearChangedPerks</c>. With ~1700 heroes that is not a locator, and narrowing it by rebuilding the
    /// world a piece at a time costs a build, a save and a load per guess.
    ///
    /// This reports the victim and the fields most likely to be the null, then rethrows: the load still fails,
    /// exactly as before, but the log now says which hero to go and look at. Every read is guarded, because a
    /// hero far enough gone to break AfterLoad can equally break its own accessors, and a diagnostic that
    /// throws while reporting a throw explains nothing.
    /// </remarks>
    [HarmonyPatch(typeof(Hero), "AfterLoad")]
    internal static class HeroAfterLoadTracePatch
    {
        private static readonly ILogger Logger = LogManager.GetLogger(typeof(HeroAfterLoadTracePatch));

        /// <summary>One bad hero is usually a whole bad class of them; a cap keeps the log readable.</summary>
        private const int MaxReported = 15;

        private static int reported;

        private static Exception Finalizer(Hero __instance, Exception __exception)
        {
            if (__exception == null || reported >= MaxReported) return __exception;
            reported++;

            Logger.Error(
                "[LoadTrace] Hero.AfterLoad threw for id={Id} name={Name} alive={Alive} clan={Clan} " +
                "culture={Culture} character={Character} developer={Developer} " +
                "occupation={Occupation} party={Party} :: {Exception}",
                Safe(() => __instance?.StringId),
                Safe(() => __instance?.Name?.ToString()),
                Safe(() => __instance?.IsAlive.ToString()),
                Safe(() => __instance?.Clan?.StringId),
                Safe(() => __instance?.Culture?.StringId),
                Safe(() => __instance?.CharacterObject?.StringId),
                Safe(() => __instance?.HeroDeveloper == null ? "NULL" : "ok"),
                Safe(() => __instance?.Occupation.ToString()),
                Safe(() => __instance?.PartyBelongedTo?.StringId),
                __exception.ToString());

            return __exception;
        }

        private static string Safe(Func<string> read)
        {
            try { return read() ?? "none"; }
            catch (Exception e) { return "<threw:" + e.GetType().Name + ">"; }
        }
    }
}
