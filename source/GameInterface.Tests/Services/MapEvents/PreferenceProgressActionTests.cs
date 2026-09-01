using GameInterface.Services.MapEvents.Handlers;
using System;
using System.Collections.Generic;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

using Decision = BattleMissionStartHandler.PreferenceProgressAction;

/// <summary>
/// What a progress broadcast is allowed to do to the local preference prompt.
/// </summary>
/// <remarks>
/// <para>
/// These exist because of a live failure that no existing test could have caught. Two players were asked for a
/// deployment preference. Omar answered two seconds in. The server broadcast the progress that announced it, with
/// outstanding=[Hero_Player] - the other player. That other client filtered itself out of the list, saw nothing
/// left, concluded everyone had answered, and closed its own prompt. The barrier then waited out its full thirty
/// seconds for an answer that could no longer be given, and Omar's attack button did nothing the whole time
/// because the battle was held.
/// </para>
/// <para>
/// The trap is that "everyone has answered" and "only I have not answered" both reduce to an empty list once the
/// local hero is filtered out. Anything here that stops distinguishing those two brings the thirty-second hang
/// straight back.
/// </para>
/// </remarks>
public class PreferenceProgressActionTests
{
    private const string Local = "Hero_Player";
    private const string Omar = "Hero_Player2863";

    private static readonly Func<string, bool> IsLocal =
        id => string.Equals(id, Local, StringComparison.Ordinal);

    private static Decision Decide(string[] outstanding, bool answeredThisBattle = false) =>
        BattleMissionStartHandler.DecidePreferenceProgressAction(outstanding, IsLocal, answeredThisBattle);

    /// <summary>The exact live failure: the server is waiting on me and nobody else.</summary>
    [Fact]
    public void OnlyLocalHeroOutstanding_KeepsThePromptOpen()
    {
        Assert.Equal(Decision.KeepPrompt, Decide(new[] { Local }));
    }

    [Fact]
    public void LocalHeroOutstandingAlongsideOthers_KeepsThePromptOpen()
    {
        Assert.Equal(Decision.KeepPrompt, Decide(new[] { Local, Omar }));
        Assert.Equal(Decision.KeepPrompt, Decide(new[] { Omar, Local }));
    }

    /// <summary>
    /// The server's list is the authority. If it still names this client, the local "I answered" belief is wrong -
    /// the choice was lost in flight - and the prompt has to stay up so it can be sent again.
    /// </summary>
    [Fact]
    public void LocalHeroOutstanding_KeepsPromptEvenWhenThisClientThinksItAnswered()
    {
        Assert.Equal(Decision.KeepPrompt, Decide(new[] { Local }, answeredThisBattle: true));
        Assert.Equal(Decision.KeepPrompt, Decide(new[] { Local, Omar }, answeredThisBattle: true));
    }

    [Fact]
    public void NobodyOutstanding_DismissesThePrompt()
    {
        Assert.Equal(Decision.Dismiss, Decide(Array.Empty<string>()));
        Assert.Equal(Decision.Dismiss, Decide(Array.Empty<string>(), answeredThisBattle: true));
    }

    [Fact]
    public void NullOutstandingList_DismissesRatherThanThrows()
    {
        Assert.Equal(Decision.Dismiss, BattleMissionStartHandler.DecidePreferenceProgressAction(null, IsLocal, false));
    }

    [Fact]
    public void OthersOutstandingAfterThisClientAnswered_ShowsWhoIsHoldingThingsUp()
    {
        Assert.Equal(Decision.ShowWaiting, Decide(new[] { Omar }, answeredThisBattle: true));
    }

    /// <summary>
    /// Progress for a battle this client never answered and is not named in - a stale or unrelated broadcast.
    /// Nothing to keep on screen.
    /// </summary>
    [Fact]
    public void OthersOutstandingForAnUnrelatedBattle_Dismisses()
    {
        Assert.Equal(Decision.Dismiss, Decide(new[] { Omar }, answeredThisBattle: false));
    }

    [Fact]
    public void HeroIdMatchingIsExact_ASimilarIdIsNotThisClient()
    {
        // Hero_Player2863 starts with Hero_Player. A prefix or case-insensitive match here would make Omar look
        // like the local hero and hold a prompt that was never shown.
        Assert.Equal(Decision.ShowWaiting, Decide(new[] { Omar }, answeredThisBattle: true));
        Assert.Equal(Decision.Dismiss, Decide(new[] { "hero_player" }, answeredThisBattle: false));
    }

    [Fact]
    public void WorksThroughAnyReadOnlyList_NotJustArrays()
    {
        IReadOnlyList<string> outstanding = new List<string> { Local };
        Assert.Equal(
            Decision.KeepPrompt,
            BattleMissionStartHandler.DecidePreferenceProgressAction(outstanding, IsLocal, false));
    }

    /// <summary>
    /// Replays the live sequence end to end from the client that was stranded, and then the same sequence with the
    /// answer actually landing. Before the fix the second broadcast returned Dismiss and this client lost its
    /// prompt while the server was still waiting on it.
    /// </summary>
    [Fact]
    public void LiveSequence_LocalClientKeepsItsPromptUntilItsOwnAnswerLands()
    {
        // 21:21:39 - the hold opens, both players are outstanding.
        Assert.Equal(Decision.KeepPrompt, Decide(new[] { Local, Omar }));

        // 21:21:41 - Omar answers. This is the broadcast that used to close the local prompt.
        Assert.Equal(Decision.KeepPrompt, Decide(new[] { Local }));

        // The local player finally chooses; the server drops them from the list.
        Assert.Equal(Decision.Dismiss, Decide(Array.Empty<string>(), answeredThisBattle: true));
    }

    /// <summary>The mirror case: this client answers first and waits on the other one.</summary>
    [Fact]
    public void LiveSequence_ClientThatAnswersFirstWaitsRatherThanDismissing()
    {
        Assert.Equal(Decision.KeepPrompt, Decide(new[] { Local, Omar }));
        Assert.Equal(Decision.ShowWaiting, Decide(new[] { Omar }, answeredThisBattle: true));
        Assert.Equal(Decision.Dismiss, Decide(Array.Empty<string>(), answeredThisBattle: true));
    }

    /// <summary>
    /// A timeout ends the hold with names still outstanding, and the server sends a final empty broadcast when it
    /// gives up. Whatever this client's state, the prompt must come down - the mission is already starting.
    /// </summary>
    [Fact]
    public void TimeoutBroadcast_TakesThePromptDownForEveryone()
    {
        Assert.Equal(Decision.Dismiss, Decide(Array.Empty<string>(), answeredThisBattle: false));
        Assert.Equal(Decision.Dismiss, Decide(Array.Empty<string>(), answeredThisBattle: true));
    }
}
