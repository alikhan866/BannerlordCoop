using System;
using System.Linq;
using E2E.Tests.Util;
using GameInterface.Services.Heroes.Patches;
using HarmonyLib;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Heroes;

/// <summary>
/// Keeping a broken campaign log entry from freezing every lord conversation.
/// </summary>
/// <remarks>
/// <c>conversation_lord_makes_comment_on_condition</c> asks the log history for something the lord could remark
/// on, and that walk calls <c>GetConversationScoreAndComment</c> on every entry - seventeen <c>LogEntry</c>
/// subclasses implement it. A <c>DeclareWarLogEntry</c> naming a destroyed rebel faction dereferenced its
/// Faction1, that faction's Leader and the Leader's Clan with no null guard, and threw every time the condition
/// ran. Swallowed further up by the screen-manager guard, the conversation simply never advanced: 36 clicks on
/// "Continue" produced 36 identical exceptions and no progress, with nothing shown to the player.
/// </remarks>
public class LordConversationCommentGuardTests : SyncTestBase
{
    public LordConversationCommentGuardTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void TheGuardedConditionStillExistsUnderThatName()
    {
        // The condition is private, so it can only be patched by STRING. A game update that renames it would
        // make the patch silently target nothing - no error, no guard, and the freeze returns. This is the
        // assertion that fails loudly instead.
        var condition = AccessTools.Method(
            typeof(LordConversationsCampaignBehavior),
            LordConversationsCampaignBehaviorPatches.LordMakesCommentCondition);

        Assert.NotNull(condition);
        Assert.Equal(typeof(bool), condition.ReturnType);
        Assert.Empty(condition.GetParameters());
    }

    [Fact]
    public void TheGuardIsActuallyAppliedByPatchAll()
    {
        // Declaring a patch is not the same as applying one. A mistyped target or a signature Harmony refuses
        // leaves the attribute in place and the method unguarded - and worse, a refusal throws out of PatchAll
        // and takes every other patch in GameInterface with it. Assert the finalizer is really attached to the
        // live method after the environment has run PatchAll.
        var condition = AccessTools.Method(
            typeof(LordConversationsCampaignBehavior),
            LordConversationsCampaignBehaviorPatches.LordMakesCommentCondition);

        var patches = Harmony.GetPatchInfo(condition);

        Assert.NotNull(patches);
        Assert.Contains(
            patches.Finalizers,
            finalizer => finalizer.PatchMethod.DeclaringType == typeof(LordConversationsCampaignBehaviorPatches));
    }

    [Fact]
    public void AThrowIsSuppressedAndTheLordSimplyHasNoRemark()
    {
        LordConversationsCampaignBehaviorPatches.ResetCommentFailureCount();

        bool result = true;
        var rethrown = LordConversationsCampaignBehaviorPatches.SuppressCommentFailure(
            new NullReferenceException("log entry points at a destroyed faction"), ref result);

        // Returning null is what suppresses the exception; returning it back would rethrow and change nothing.
        Assert.Null(rethrown);
        Assert.False(result);
        Assert.Equal(1, LordConversationsCampaignBehaviorPatches.CommentFailuresSuppressed);
    }

    [Fact]
    public void ACleanRunIsLeftCompletelyAlone()
    {
        LordConversationsCampaignBehaviorPatches.ResetCommentFailureCount();

        // A finalizer runs on EVERY call, not just failing ones. Overwriting the result here would silence every
        // lord remark in the game rather than only the broken ones.
        bool result = true;
        var rethrown = LordConversationsCampaignBehaviorPatches.SuppressCommentFailure(null, ref result);

        Assert.Null(rethrown);
        Assert.True(result);
        Assert.Equal(0, LordConversationsCampaignBehaviorPatches.CommentFailuresSuppressed);
    }

    [Fact]
    public void RepeatedFailuresAreCountedButOnlyReportedOnce()
    {
        LordConversationsCampaignBehaviorPatches.ResetCommentFailureCount();

        // The condition is evaluated per dialogue line, so a player who keeps talking hits this thousands of
        // times. The count must keep rising even though the log stays quiet, or the diagnostic is worthless.
        bool result = true;
        foreach (var _ in Enumerable.Range(0, 50))
            LordConversationsCampaignBehaviorPatches.SuppressCommentFailure(new InvalidOperationException(), ref result);

        Assert.Equal(50, LordConversationsCampaignBehaviorPatches.CommentFailuresSuppressed);
    }
}
