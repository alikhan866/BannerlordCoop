using E2E.Tests.Util;
using GameInterface.Services.Headless.Patches;
using HarmonyLib;
using SandBox.CampaignBehaviors;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Headless;

/// <summary>
/// Player-facing notifications must not run on a headless server.
/// </summary>
/// <remarks>
/// <c>DefaultNotificationsCampaignBehavior</c> builds quick-information popups, and its handlers read
/// <c>Clan.PlayerClan</c> - null in a process with no main hero. Because those handlers run inside campaign
/// ACTIONS rather than beside them, the throw does not merely lose a popup; it aborts the action half-done.
///
/// Promoting a companion to a lord is where that surfaced. <c>RemoveCompanionAction.ApplyByByTurningToLord</c>
/// removes the companion first and raises <c>OnCompanionRemoved</c> second, so the NRE landed after the removal
/// and before the new clan was created and its fief granted: the companion vanished from the player's list, no
/// clan appeared, no fief changed hands, and nothing explained why.
/// </remarks>
public class HeadlessNotificationGuardTests : SyncTestBase
{
    public HeadlessNotificationGuardTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void AHeadlessHostDoesNotRegisterNotificationHandlers()
    {
        // The whole fix: with no player clan to read, the safe thing is not to listen at all.
        Assert.False(HeadlessNotificationGuardPatch.ShouldRegisterNotifications(isHeadless: true));
    }

    [Fact]
    public void APlayerHostedGameStillGetsItsNotifications()
    {
        // Deliberately gated on headless rather than on "is server". A player hosting from their own game has a
        // player clan and wants these popups; gating on IsServer would silently take them away.
        Assert.True(HeadlessNotificationGuardPatch.ShouldRegisterNotifications(isHeadless: false));
    }

    [Fact]
    public void TheGuardIsActuallyAppliedByPatchAll()
    {
        // Declaring a patch is not applying one. A target that no longer resolves leaves the attribute in place
        // and the behaviour unguarded - and the failure it guards against is silent and half-applied, which is
        // the worst kind to rediscover in play.
        var registerEvents = AccessTools.Method(
            typeof(DefaultNotificationsCampaignBehavior),
            nameof(DefaultNotificationsCampaignBehavior.RegisterEvents));

        Assert.NotNull(registerEvents);

        var patches = Harmony.GetPatchInfo(registerEvents);

        Assert.NotNull(patches);
        Assert.Contains(
            patches.Prefixes,
            prefix => prefix.PatchMethod.DeclaringType == typeof(HeadlessNotificationGuardPatch));
    }
}
