using Missions.Battles;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Letting a side that grew after the battle started actually field the men it gained.
/// </summary>
/// <remarks>
/// <c>DefaultBattleMissionAgentSpawnLogic.AddPhase</c> stores <c>TotalSpawnNumber</c> and derives
/// <c>RemainingSpawnNumber = TotalSpawnNumber - InitialSpawnNumber</c>. The total is a hard ceiling on how many
/// men a client will EVER spawn, and it is computed once, from the reserve as it stood when the battle was
/// sized.
///
/// A coop battle does not hold still. Lords ride in for minutes after the first clash and their troops land in
/// the supplier - but the ceiling does not move, so those men are unreachable. Measured live: a phase total of
/// 534 with 331 left to spawn, while the supplier behind it still held 1,108. The side stopped reinforcing at
/// roughly a third of its strength, read as depleted, and ended the battle. From the player's seat that is
/// "1,500 against 500, and it was over after I killed about 500".
/// </remarks>
public class BattlePhaseBudgetTests
{
    [Fact]
    public void AReserveThatOutgrewItsBudget_RaisesTheCeiling()
    {
        // The reported numbers.
        Assert.True(CoopBattleMissionSpawnHandler.ShouldGrowPhaseBudget(phaseTotal: 534, ownedReserveTotal: 1642));
    }

    [Fact]
    public void ABudgetThatAlreadyCoversTheReserve_IsLeftAlone()
    {
        Assert.False(CoopBattleMissionSpawnHandler.ShouldGrowPhaseBudget(phaseTotal: 1500, ownedReserveTotal: 1500));
        Assert.False(CoopBattleMissionSpawnHandler.ShouldGrowPhaseBudget(phaseTotal: 1500, ownedReserveTotal: 900));
    }

    [Fact]
    public void TheBudgetOnlyEverRises()
    {
        // A reserve can read smaller than the budget - troops already supplied are still counted by the phase
        // but no longer waiting. Shrinking on that reading would cut off a side mid-draw, stranding men the
        // engine was about to spawn. Strictly-greater is what makes the operation one-way.
        Assert.False(CoopBattleMissionSpawnHandler.ShouldGrowPhaseBudget(phaseTotal: 1000, ownedReserveTotal: 1));
        Assert.False(CoopBattleMissionSpawnHandler.ShouldGrowPhaseBudget(phaseTotal: 1000, ownedReserveTotal: 0));
    }

    [Fact]
    public void AnEmptyReserveNeverRaisesAnything()
    {
        // A supplier that has not been populated yet reads zero, and must not be mistaken for a side that
        // needs its budget adjusted at all.
        Assert.False(CoopBattleMissionSpawnHandler.ShouldGrowPhaseBudget(phaseTotal: 0, ownedReserveTotal: 0));
    }

    [Fact]
    public void AFreshBattleWhoseReserveArrivedLate_StillGetsItsBudget()
    {
        // The held path sizes both sides at zero until the reserves land. If they land after the opening wave
        // the phase budget is zero, and without this the side would field nobody at all for the whole battle.
        Assert.True(CoopBattleMissionSpawnHandler.ShouldGrowPhaseBudget(phaseTotal: 0, ownedReserveTotal: 516));
    }
}
