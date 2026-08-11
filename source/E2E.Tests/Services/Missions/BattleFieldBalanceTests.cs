using GameInterface.Services.MapEvents;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Keeping each side's fielded troops within what the battle is sized for, as reinforcements change the fight.
/// </summary>
/// <remarks>
/// Replaces the round restart, which got the same proportions right by clearing the field and re-forming - and
/// paid for it with four defects, all of them consequences of the clearing rather than of the re-sizing:
/// every cleared agent filed as RETREATED, the local player dropped to the spectator view, the engine's opening
/// wave racing the reinforcement fielder over an emptied field, and every troop returning at full health on a
/// fresh horse. Trimming the over-strength side instead leaves the men who stay exactly as they were.
/// </remarks>
public class BattleFieldBalanceTests
{
    [Fact]
    public void TheOverStrengthSideGivesGround()
    {
        // The reported case, in numbers: a battle sized for 400 opens 350 against 50. Five minutes later 1000
        // men reinforce the smaller side, so the proportional split becomes 100 against 300. Feeding the
        // smaller side alone would put 650 on a field sized for 400; the larger side has to come down to 100.
        Assert.Equal(250, BattleFieldBalance.Surplus(troopsOnField: 350, sideTarget: 100));
    }

    [Fact]
    public void ASideWithinItsShare_IsLeftAlone()
    {
        Assert.Equal(0, BattleFieldBalance.Surplus(troopsOnField: 100, sideTarget: 100));
        Assert.Equal(0, BattleFieldBalance.Surplus(troopsOnField: 40, sideTarget: 100));
    }

    [Fact]
    public void ANegativeTargetNeverAsksForMoreThanTheFieldHolds()
    {
        // A target can read below zero transiently while the sides are being re-derived. That must not turn
        // into "withdraw more men than are standing".
        Assert.Equal(30, BattleFieldBalance.Surplus(troopsOnField: 30, sideTarget: -50));
    }

    // ---- players are never stood down, and never displace a troop ------------------------------------

    [Fact]
    public void AJoiningPlayerAddsToTheFieldRatherThanPushingOutTheirOwnMen()
    {
        // 200 v 200, and a client joins with 35 troops. The player's hero is excluded from BOTH the count and
        // the target, so the side reads 200 troops against a target of 200 - no surplus, nothing withdrawn.
        // The hero spawns on top, making it 200 v 201. Their 35 troops are still bound by the limit and wait
        // their turn like everyone else's.
        Assert.Equal(0, BattleFieldBalance.Surplus(troopsOnField: 200, sideTarget: 200));
    }

    [Fact]
    public void AFullSideStillCannotStandDownAPlayer()
    {
        // Even a genuinely over-strength side only ever withdraws from its TROOPS. Players are filtered out
        // before this arithmetic sees them, so an owner holding nothing but its player contributes nothing to
        // withdraw however large the surplus.
        Assert.Equal(0, BattleFieldBalance.OwnedShareOfSurplus(surplus: 250, ownedTroopsOnField: 0, sideTroopsOnField: 350));
    }

    // ---- dividing a side-wide withdrawal between owners ----------------------------------------------

    [Fact]
    public void EachOwnerStandsDownItsOwnShare()
    {
        // Only the client that spawned an agent can take it off the field, so a side-wide surplus is divided
        // by ownership - the same rule that divides a side-wide allocation.
        var a = BattleFieldBalance.OwnedShareOfSurplus(250, ownedTroopsOnField: 200, sideTroopsOnField: 350);
        var b = BattleFieldBalance.OwnedShareOfSurplus(250, ownedTroopsOnField: 150, sideTroopsOnField: 350);

        Assert.Equal(142, a);
        Assert.Equal(107, b);
        Assert.True(a + b <= 250, "between them the owners must never withdraw more than the surplus");
    }

    [Fact]
    public void RoundingDownLeavesTheSideSlightlyOverRatherThanShort()
    {
        // Deliberate: every owner rounding UP would withdraw more than the surplus between them and leave the
        // side under-strength, which is the harder error to notice. A man or two over is corrected on the next
        // pass as casualties move the numbers anyway.
        //
        // Three owners holding 5 each of a 15-strong side, asked for 10: 10 * 5 / 15 = 3.33 -> 3 apiece.
        var each = BattleFieldBalance.OwnedShareOfSurplus(10, ownedTroopsOnField: 5, sideTroopsOnField: 15);

        Assert.Equal(3, each);
        Assert.True(each * 3 < 10, "rounding down must leave the side slightly over, never short");
    }

    [Fact]
    public void ASoleOwnerTakesTheWholeSurplus()
    {
        Assert.Equal(250, BattleFieldBalance.OwnedShareOfSurplus(250, ownedTroopsOnField: 350, sideTroopsOnField: 350));
    }

    [Fact]
    public void NobodyWithdrawsMoreThanTheyHave()
    {
        // The share is capped by what this owner actually has standing, so a large surplus on a side it barely
        // contributes to cannot ask it for men it does not have.
        Assert.Equal(5, BattleFieldBalance.OwnedShareOfSurplus(surplus: 400, ownedTroopsOnField: 5, sideTroopsOnField: 400));
    }
}
