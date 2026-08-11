using Missions.Battles;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// What a formation reinforcements just joined is told to do.
/// </summary>
/// <remarks>
/// This path used to issue a flat <c>MovementOrderCharge</c> to every formation reinforcements landed in,
/// unconditionally and permanently. A player joining a defensive battle had his men ordered to attack the
/// instant they arrived, while the defender who started the battle kept his hold order from deployment. With
/// both players down and nobody issuing orders, those stale orders were the entirety of the AI's intent - the
/// joiner's troops charged alone and died piecemeal while the host's stood at the back. It looks exactly like
/// the joiner being put on the wrong side; he is not, he is just the only one who was told to attack.
///
/// The charge is now the last resort. The ordering below is the whole fix, so each rung is pinned separately.
/// </remarks>
public class ReinforcementPostureTests
{
    [Fact]
    public void AFormationAPlayerCommandsIsNeverTouched()
    {
        // Seizing a formation from a living player is how a player loses command of his own troops, which has
        // its own history in this codebase. It outranks every other consideration, so it is checked first even
        // when the formation has no order at all.
        Assert.Equal(
            ReinforcementFielder.ReinforcementPosture.LeaveToPlayer,
            ReinforcementFielder.DecidePosture(commandedByPlayer: true, OrderType.None, OrderType.Charge));
    }

    [Fact]
    public void AFormationThatAlreadyHasAPostureKeepsIt()
    {
        // The regression itself: a defending formation holding position must not be re-ordered to charge just
        // because reinforcements arrived in it. Arriving troops inherit the order by joining the formation.
        Assert.Equal(
            ReinforcementFielder.ReinforcementPosture.KeepExisting,
            ReinforcementFielder.DecidePosture(commandedByPlayer: false, OrderType.StandYourGround, OrderType.Charge));
    }

    [Theory]
    [InlineData(OrderType.StandYourGround)]
    [InlineData(OrderType.Advance)]
    [InlineData(OrderType.FallBack)]
    [InlineData(OrderType.Charge)]
    public void AnOrderlessFormationAdoptsWhateverItsSideIsDoing(OrderType sideOrder)
    {
        // A brand-new formation has no order of its own. Copying the side's posture is what stops a late
        // joiner's men from doing something the rest of the army is not.
        Assert.Equal(
            ReinforcementFielder.ReinforcementPosture.Inherit,
            ReinforcementFielder.DecidePosture(commandedByPlayer: false, OrderType.None, sideOrder));
    }

    [Fact]
    public void OnlyASideWithNoPostureAtAllCharges()
    {
        // The original invariant, kept: reinforcements must never be left standing idle. When nothing on the
        // side has an order there is nothing to copy, so engaging is better than doing nothing.
        Assert.Equal(
            ReinforcementFielder.ReinforcementPosture.Charge,
            ReinforcementFielder.DecidePosture(commandedByPlayer: false, OrderType.None, OrderType.None));
    }

    [Fact]
    public void APlayerCommandedFormationIsSparedEvenWhenTheSideIsIdle()
    {
        // The one combination where the old code would have charged a player's own formation: nothing else on
        // the side has a posture. The player still outranks it.
        Assert.Equal(
            ReinforcementFielder.ReinforcementPosture.LeaveToPlayer,
            ReinforcementFielder.DecidePosture(commandedByPlayer: true, OrderType.None, OrderType.None));
    }

    [Fact]
    public void AnExistingPostureOutranksTheSideEvenWhenTheSideIsIdle()
    {
        // Ordering check: KeepExisting must be decided before the side is consulted, or a formation with a
        // perfectly good order would be re-ordered to charge whenever the rest of the side happened to be idle.
        Assert.Equal(
            ReinforcementFielder.ReinforcementPosture.KeepExisting,
            ReinforcementFielder.DecidePosture(commandedByPlayer: false, OrderType.StandYourGround, OrderType.None));
    }
}
