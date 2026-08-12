using GameInterface.Services.MapEvents.Loot;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// Turning a validated answer into what the server will actually do, given the room the party really has.
/// </summary>
public class BattleLootPlannerTests
{
    private static BattleLootResolvedClaim Item(string id, int count, string modifier = null)
        => new BattleLootResolvedClaim(
            new BattleLootOfferLine(BattleLootLineKind.Item, id, modifier, count), count, BattleLootDisposition.Keep);

    private static BattleLootResolvedClaim Troops(
        BattleLootLineKind kind, string id, int offered, int taken, int wounded = 0, int xp = 0)
        => new BattleLootResolvedClaim(
            new BattleLootOfferLine(kind, id, null, offered, wounded, xp), taken, BattleLootDisposition.Keep);

    private static BattleLootResolvedClaim Hero(
        string id,
        BattleLootDisposition disposition = BattleLootDisposition.Keep,
        BattleLootLineKind kind = BattleLootLineKind.Prisoner)
        => new BattleLootResolvedClaim(
            new BattleLootOfferLine(kind, id, null, 1, isHero: true), 1, disposition);

    [Fact]
    public void WithRoomToSpare_EverythingIsGranted()
    {
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim> { Item("grain", 5), Troops(BattleLootLineKind.Member, "recruit", 10, 10) },
            BattleLootCapacity.Unlimited);

        Assert.Equal(5, Assert.Single(plan.Items).Count);
        Assert.Equal(10, Assert.Single(plan.Members).Count);
        Assert.Equal(0, plan.ClampedMembers);
    }

    [Fact]
    public void MembersAreClampedToTheRoomAvailable_AndTheShortfallIsReported()
    {
        // The player must be told the truth: their screen promised 10, the party could take 3.
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim> { Troops(BattleLootLineKind.Member, "recruit", 10, 10) },
            new BattleLootCapacity(memberSlotsFree: 3, prisonerSlotsFree: 0));

        Assert.Equal(3, Assert.Single(plan.Members).Count);
        Assert.Equal(7, plan.ClampedMembers);
    }

    [Fact]
    public void ItemsAreNeverClampedBySlots()
    {
        // Items cost weight, which the party carries as a speed penalty rather than a refusal.
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim> { Item("grain", 600) },
            new BattleLootCapacity(0, 0));

        Assert.Equal(600, Assert.Single(plan.Items).Count);
    }

    [Fact]
    public void AHeroTakesPrisonerRoomBeforeOrdinaryPrisonersDo()
    {
        // One slot, a lord and a cart of looters. The lord is what the player came for.
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim>
            {
                Troops(BattleLootLineKind.Prisoner, "looter", 5, 5),
                Hero("lord_1_68"),
            },
            new BattleLootCapacity(memberSlotsFree: 0, prisonerSlotsFree: 1));

        Assert.Equal("lord_1_68", Assert.Single(plan.Heroes).CharacterId);
        Assert.Empty(plan.Prisoners);
        Assert.Equal(5, plan.ClampedPrisoners);
    }

    [Fact]
    public void AHeroWithNoRoom_IsLeftBehindRatherThanReleased()
    {
        // Releasing costs relations and lets the lord walk. Not handing him over is recoverable; that is not.
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim> { Hero("lord_1_68") },
            new BattleLootCapacity(0, 0));

        Assert.Empty(plan.Heroes);
        Assert.Equal("lord_1_68", Assert.Single(plan.HeroesWithoutRoom));
    }

    [Fact]
    public void ReleasingAHero_NeedsNoRoomAtAll()
    {
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim> { Hero("lord_1_68", BattleLootDisposition.Release) },
            new BattleLootCapacity(0, 0));

        var action = Assert.Single(plan.Heroes);
        Assert.Equal(BattleLootDisposition.Release, action.Disposition);
        Assert.Empty(plan.HeroesWithoutRoom);
    }

    [Fact]
    public void ARescuedCompanionCostsMemberRoomNotPrisonerRoom()
    {
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim> { Hero("companion_1", BattleLootDisposition.Keep, BattleLootLineKind.Member) },
            new BattleLootCapacity(memberSlotsFree: 1, prisonerSlotsFree: 0));

        Assert.Single(plan.Heroes);
        Assert.Empty(plan.HeroesWithoutRoom);
    }

    [Fact]
    public void TakingPartOfALine_ScalesItsWoundedDown()
    {
        // Otherwise half a line arrives with all of its casualties, and a roster holding more wounded than
        // present reports negative healthy troops.
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim> { Troops(BattleLootLineKind.Member, "recruit", offered: 10, taken: 5, wounded: 10) },
            BattleLootCapacity.Unlimited);

        var granted = Assert.Single(plan.Members);
        Assert.Equal(5, granted.Count);
        Assert.Equal(5, granted.WoundedNumber);
        Assert.True(granted.WoundedNumber <= granted.Count);
    }

    [Fact]
    public void WoundedNeverExceedsTheCountGranted()
    {
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim> { Troops(BattleLootLineKind.Member, "recruit", offered: 4, taken: 1, wounded: 4) },
            BattleLootCapacity.Unlimited);

        var granted = Assert.Single(plan.Members);
        Assert.True(granted.WoundedNumber <= granted.Count);
    }

    [Fact]
    public void NegativeCapacity_IsTreatedAsNoRoom()
    {
        // An over-full party can report a negative free count; it must clamp to zero rather than to a
        // negative allowance that would then "grant" nothing but still report a bizarre shortfall.
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim> { Troops(BattleLootLineKind.Member, "recruit", 5, 5) },
            new BattleLootCapacity(memberSlotsFree: -3, prisonerSlotsFree: -3));

        Assert.Empty(plan.Members);
        Assert.Equal(5, plan.ClampedMembers);
    }

    [Fact]
    public void RoomIsSharedAcrossSeveralLines()
    {
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim>
            {
                Troops(BattleLootLineKind.Member, "recruit", 4, 4),
                Troops(BattleLootLineKind.Member, "veteran", 4, 4),
            },
            new BattleLootCapacity(memberSlotsFree: 5, prisonerSlotsFree: 0));

        Assert.Equal(5, plan.Members.Sum(g => g.Count));
        Assert.Equal(3, plan.ClampedMembers);
    }

    [Fact]
    public void NoClaims_ProducesAnEmptyPlan()
    {
        // A battle really can yield nothing - killing every enemy outright leaves no prisoners.
        Assert.True(BattleLootPlanner.Plan(new List<BattleLootResolvedClaim>(), BattleLootCapacity.Unlimited).IsEmpty);
        Assert.True(BattleLootPlanner.Plan(null, BattleLootCapacity.Unlimited).IsEmpty);
    }

    [Fact]
    public void ItemModifierSurvivesIntoThePlan()
    {
        var plan = BattleLootPlanner.Plan(
            new List<BattleLootResolvedClaim> { Item("sword", 1, "lordly") },
            BattleLootCapacity.Unlimited);

        Assert.Equal("lordly", Assert.Single(plan.Items).ModifierId);
    }
}
