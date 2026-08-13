using Common;
using HarmonyLib;
using SandBox.CampaignBehaviors;

namespace GameInterface.Services.Headless.Patches;

/// <summary>
/// Keeps the campaign's player-facing notifications from running on a headless server.
/// </summary>
/// <remarks>
/// <c>DefaultNotificationsCampaignBehavior</c> exists to put quick-information popups on a player's screen. Its
/// handlers therefore read the things a player has and a dedicated server does not - most of them go through
/// <c>Clan.PlayerClan</c>, which is null in a process with no main hero.
///
/// That is not merely wasted work, because these handlers run inside campaign ACTIONS rather than beside them.
/// Promoting a companion to a lord showed exactly what that costs:
///
///     NullReferenceException
///       at DefaultNotificationsCampaignBehavior.OnCompanionRemoved(Hero, RemoveCompanionDetail)
///       at CampaignEventDispatcher.OnCompanionRemoved(...)
///       at RemoveCompanionAction.ApplyByByTurningToLord(clan, companion)
///       at CompanionRolesHandler.Handle_DoClanNameSelection
///
/// <c>ApplyByByTurningToLord</c> removes the companion first and raises the event second, so the throw landed
/// AFTER the removal and BEFORE the new clan was created and its fief granted. The player was left with the
/// companion gone from their list, no clan, and no explanation - the action half-applied because a popup
/// nobody could see failed to build.
///
/// Refusing <c>RegisterEvents</c> on a headless host is the same shape every other disabled campaign behaviour
/// here uses, and it covers every handler in the class rather than the one that happened to be hit first. Only
/// headless is excluded, not every server: a player hosting from their own game HAS a player clan and wants
/// these notifications, and gating on <see cref="ModInformation.IsServer"/> would take them away.
/// </remarks>
[HarmonyPatch(typeof(DefaultNotificationsCampaignBehavior))]
internal class HeadlessNotificationGuardPatch
{
    /// <summary>Whether this process should run player-facing campaign notifications at all.</summary>
    /// <remarks>
    /// The argument is "is this a process with no player behind it", which the call site now answers with
    /// IsHeadlessServer rather than IsHeadless. The reasoning in this class is entirely about a host that has
    /// no main hero and therefore no <c>Clan.PlayerClan</c> to read - that is a dedicated SERVER. A driven
    /// client is render-free but is a player in every other respect: it owns a hero, a party and a clan, and
    /// `coop.debug.mobileparty.whoami` on one answers "You are Kan".
    ///
    /// Suppressing it there was not merely conservative, it was blinding: these handlers are where most of
    /// what the campaign tells a player comes from, so a driven client raised no notifications at all and the
    /// capture built to read them had nothing to read.
    /// </remarks>
    internal static bool ShouldRegisterNotifications(bool isHeadless) => !isHeadless;

    [HarmonyPatch(nameof(DefaultNotificationsCampaignBehavior.RegisterEvents))]
    [HarmonyPrefix]
    private static bool RegisterEventsPrefix() =>
        ShouldRegisterNotifications(ModInformation.IsHeadlessServer);
}
