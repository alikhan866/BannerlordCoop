using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using System.Reflection;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Headless.Patches;

/// <summary>
/// Keeps a headless client's campaign creation alive through vanilla's player-setup step.
/// </summary>
/// <remarks>
/// A joining client builds an empty campaign and then receives the world from the server, so it reaches
/// <c>Campaign.OnAfterNewGameCreatedInternal</c> - vanilla's "give the new player their starting gold and
/// influence" step - possibly without a player yet. Both of the things that step touches are reached by
/// <c>callvirt</c>:
///
///   Hero.MainHero.Gold = ...
///   ChangeClanInfluenceAction.Apply(Clan.PlayerClan, ...)
///
/// which is why the null reference surfaces with <c>OnAfterNewGameCreatedInternal</c> as its top frame and no
/// deeper one to point at: callvirt does the null check at the CALL SITE. It took an IL read to see that the
/// frame naming the method was not the method's own fault.
///
/// WHAT THIS DOES, AND DELIBERATELY DOES NOT DO
/// It only skips when the player really is absent, and it says which half was missing. A rendered client
/// reaches the same step with both present and is untouched, so the two roles keep taking the same path - the
/// point of a headless client is to behave like a client, and a guard that always skipped would quietly make
/// it a different thing. If this ever logs on a role that should have a player, the log names the missing
/// half instead of leaving a bare NullReferenceException in a property setter.
///
/// Skipping is safe for a joiner: starting gold and influence belong to a fresh single-player character, and
/// this process is about to have its hero, clan and purse replaced wholesale by the join baseline.
/// </remarks>
[HarmonyPatch]
internal static class HeadlessNewGameGuardPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(HeadlessNewGameGuardPatch));

    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(Campaign), "OnAfterNewGameCreatedInternal");

    [HarmonyPrefix]
    private static bool SkipPlayerSetupWithoutAPlayer()
    {
        if (!ModInformation.IsHeadlessClient) return true;

        bool hasHero = Read(() => Hero.MainHero != null);
        bool hasClan = Read(() => Clan.PlayerClan != null);
        if (hasHero && hasClan) return true;

        Logger.Information(
            "[Headless] skipping vanilla new-game player setup: mainHero={HasHero} playerClan={HasClan}. " +
            "A joining client is given its hero, clan and gold by the join baseline",
            hasHero,
            hasClan);
        return false;
    }

    private static bool Read(Func<bool> read)
    {
        try { return read(); }
        catch { return false; }
    }
}
