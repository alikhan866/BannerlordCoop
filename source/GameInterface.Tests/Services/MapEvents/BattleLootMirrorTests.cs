using GameInterface.Services.MapEvents.Handlers;
using TaleWorlds.Core;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// The one rule that decides whether a player is owed a battle's spoils.
/// </summary>
/// <remarks>
/// The server computes each player's loot, sends it, and mirrors it onto its own copy of that player's party.
/// The client applies the copy it was sent. Both gate on this single predicate, and they must never disagree -
/// a side that awards while the other does not is precisely the divergence the mirror exists to close.
///
/// That divergence was live: the server never applied the loot at all, so a player held items, recovered troops
/// and prisoners the server had no record of. Prisoners were where it showed, because <c>PrisonerSaleProcessor</c>
/// validates a ransom against the SERVER's prison roster - so those prisoners could not be ransomed, could not be
/// discarded, and gave no reason why.
/// </remarks>
public class BattleLootMirrorTests
{
    [Theory]
    [InlineData(BattleSideEnum.Attacker)]
    [InlineData(BattleSideEnum.Defender)]
    public void TheWinningSideIsOwedTheSpoils(BattleSideEnum side)
    {
        Assert.True(MapEventResultsHandler.WonTheBattle(side, side));
    }

    [Theory]
    [InlineData(BattleSideEnum.Attacker, BattleSideEnum.Defender)]
    [InlineData(BattleSideEnum.Defender, BattleSideEnum.Attacker)]
    public void TheLosingSideIsNot(BattleSideEnum winningSide, BattleSideEnum playerSide)
    {
        Assert.False(MapEventResultsHandler.WonTheBattle(winningSide, playerSide));
    }

    [Fact]
    public void AResultThatNamesNoSideIsNotAWin()
    {
        // BattleSideEnum has a None, and results can carry it when the player's party cannot be placed on
        // either side. A plain equality check would read None == None as a victory and hand that player the
        // spoils of a battle they were never in - on the server's copy of their party, where nothing would
        // later contradict it.
        Assert.False(MapEventResultsHandler.WonTheBattle(BattleSideEnum.None, BattleSideEnum.None));
    }

    [Fact]
    public void AnUndecidedBattleAwardsNobody()
    {
        Assert.False(MapEventResultsHandler.WonTheBattle(BattleSideEnum.None, BattleSideEnum.Attacker));
        Assert.False(MapEventResultsHandler.WonTheBattle(BattleSideEnum.None, BattleSideEnum.Defender));
    }
}
