using GameInterface.Services.Headless;
using System;
using Xunit;

namespace GameInterface.Tests.Services.Headless;

/// <summary>
/// The default-deny rule for modals a driven client cannot show.
/// </summary>
/// <remarks>
/// Tested because the failure mode is invisible. Answering everything affirmatively does not throw, does not
/// log, and does not stop the run - it just quietly makes campaign decisions nobody asked for, and every
/// assertion after that point is measuring a world the scenario never intended. A regression here would look
/// like a green run.
/// </remarks>
public class PopupPolicyTests : IDisposable
{
    public PopupPolicyTests()
    {
        PopupPolicy.ClearDeclarations();
        PopupPolicy.ClearFailures();
    }

    public void Dispose()
    {
        PopupPolicy.ClearDeclarations();
        PopupPolicy.ClearFailures();
    }

    [Fact]
    public void AnUndeclaredPopupIsDeclined()
    {
        var answer = PopupPolicy.Decide("Marry this person?", "It is a good match.", out bool wasDeclared);

        Assert.False(wasDeclared);
        Assert.Equal(PopupPolicy.Answer.Negative, answer);
    }

    [Fact]
    public void ADeclaredPopupGetsItsDeclaredAnswer()
    {
        PopupPolicy.Declare("good match", PopupPolicy.Answer.Affirmative);

        var answer = PopupPolicy.Decide("Marry this person?", "It is a good match.", out bool wasDeclared);

        Assert.True(wasDeclared);
        Assert.Equal(PopupPolicy.Answer.Affirmative, answer);
    }

    [Fact]
    public void ADeclarationCanAlsoDeclineExplicitly()
    {
        // Declaring a decline is not the same as leaving it undeclared: the scenario expected this popup, so
        // it must NOT fail the run, even though the answer is identical.
        PopupPolicy.Declare("good match", PopupPolicy.Answer.Negative);

        var answer = PopupPolicy.Decide("Marry this person?", "It is a good match.", out bool wasDeclared);

        Assert.True(wasDeclared);
        Assert.Equal(PopupPolicy.Answer.Negative, answer);
    }

    [Fact]
    public void MatchingIsCaseInsensitiveAndCoversTitleAndText()
    {
        PopupPolicy.Declare("SIEGE", PopupPolicy.Answer.Affirmative);

        Assert.Equal(PopupPolicy.Answer.Affirmative, PopupPolicy.Decide("Begin siege?", null, out _));
        Assert.Equal(PopupPolicy.Answer.Affirmative, PopupPolicy.Decide(null, "Lay siege to the town?", out _));
    }

    [Fact]
    public void AnUndeclaredPopupFailsTheRun()
    {
        Assert.False(PopupPolicy.RunFailed);

        PopupPolicy.Decide("Execute the prisoner?", "This cannot be undone.", out bool wasDeclared);
        Assert.False(wasDeclared);

        // Decide itself stays pure; failing the run is the caller's act, mirroring the patch.
        typeof(PopupPolicy)
            .GetMethod("FailRun", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            .Invoke(null, new object[] { "Execute the prisoner?", "This cannot be undone." });

        Assert.True(PopupPolicy.RunFailed);
        Assert.Contains(PopupPolicy.FailureReasons, reason => reason.Contains("Execute the prisoner?"));
    }

    [Fact]
    public void ThereIsNoAcceptAll()
    {
        // The rule this whole class exists to hold. An accept-everything switch would be reached for at the
        // exact moment the rule is protecting against, so the guarantee is that no such member exists at all
        // - not that it defaults to off.
        var members = typeof(PopupPolicy).GetMembers(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);

        Assert.DoesNotContain(members, member =>
            member.Name.IndexOf("AcceptAll", StringComparison.OrdinalIgnoreCase) >= 0 ||
            member.Name.IndexOf("AnswerAll", StringComparison.OrdinalIgnoreCase) >= 0 ||
            member.Name.IndexOf("AutoAccept", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
