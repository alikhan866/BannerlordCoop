using Missions.Battles;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// How many men the reinforcement fielder may put on the field at once.
/// </summary>
/// <remarks>
/// Measured live: <c>battleSize=400</c> with <c>onField(def=310, atk=282, total=592)</c>. Six parties were
/// fielded inside one second - 78, 56, 110, 91, 84 and 88 men, 507 in total - each granted the full allowance.
///
/// The cause was a meter that could not see what it spent. <c>SideFieldingAllowance</c> returned the supplier's
/// quota, which subtracts <c>CountMyTroopsOnField</c>; that counts only agents whose origin party sits in the
/// supplier's own reserve. But the fielder, by explicit guard, only ever fields parties that are NOT the
/// supplier's - <c>IsSupplierParty</c> skips those at both the queueing and the fielding site. So every troop it
/// spawned was invisible to the meter it was charged against, the quota returned the same number for all six
/// parties, and the batch spent one allowance six times.
///
/// Pairing the quota with the side-wide room fixes it: that room counts every live human on the side, so it
/// falls as the batch spawns and the next party in the same batch reads a smaller number. No running budget has
/// to be threaded through the loop - the meter simply tells the truth each time it is read.
/// </remarks>
public class ReinforcementFieldingAllowanceTests
{
    [Fact]
    public void TheSideRoomCapsAQuotaThatCannotSeeItsOwnSpending()
    {
        // The regression, in one line: quota says 110 is fine, but the side has only 12 places left.
        Assert.Equal(12, ReinforcementFielder.EffectiveAllowance(sideRoom: 12, ownQuota: 110));
    }

    [Fact]
    public void TheQuotaStillCapsAClientFillingASideWithPlentyOfRoom()
    {
        // The quota is not merely defensive padding - it is what stops one client filling a shared side and
        // crowding out another that is trying to field its own men.
        Assert.Equal(40, ReinforcementFielder.EffectiveAllowance(sideRoom: 300, ownQuota: 40));
    }

    [Fact]
    public void ASideAlreadyAtItsTargetFieldsNobodyHoweverMuchQuotaRemains()
    {
        // The intended outcome, not a degradation: the men stay queued and arrive as casualties make room,
        // which is what vanilla does and what the battle was sized for.
        Assert.Equal(0, ReinforcementFielder.EffectiveAllowance(sideRoom: 0, ownQuota: 250));
    }

    [Fact]
    public void ASideOverItsTargetNeverReturnsANegativeAllowance()
    {
        // Room goes negative whenever a side is already over its target - which is precisely the state this bug
        // produced. A negative allowance would flow into `spawned < allowance` and, worse, into any arithmetic
        // downstream; clamping at zero keeps "no room" meaning no room.
        Assert.Equal(0, ReinforcementFielder.EffectiveAllowance(sideRoom: -192, ownQuota: 110));
    }

    [Fact]
    public void AnUnboundedQuotaIsStillBoundedByTheSide()
    {
        // No spawn logic reports int.MaxValue as "no cap in existence". That must not become a licence to
        // ignore a side that IS sized.
        Assert.Equal(25, ReinforcementFielder.EffectiveAllowance(sideRoom: 25, ownQuota: int.MaxValue));
    }

    [Fact]
    public void AnUnboundedSideIsStillBoundedByTheQuota()
    {
        Assert.Equal(25, ReinforcementFielder.EffectiveAllowance(sideRoom: int.MaxValue, ownQuota: 25));
    }

    [Fact]
    public void RemainingAllowanceIsTargetMinusWhatIsAlreadyStanding()
    {
        Assert.Equal(88, ReinforcementFielder.RemainingFieldingAllowance(sideTarget: 200, activeOnSide: 112));
    }

    [Fact]
    public void RemainingAllowanceClampsWhenASideHasOvershot()
    {
        // The exact live numbers: a defender side of 310 on a target of 200.
        Assert.Equal(0, ReinforcementFielder.RemainingFieldingAllowance(sideTarget: 200, activeOnSide: 310));
    }

    [Fact]
    public void ABatchCannotSpendTheSameAllowanceTwice()
    {
        // The batch behaviour the fix depends on, expressed as the sequence the six parties would now see:
        // each party's spawns reduce the side-wide count, so the next party reads a smaller room. Previously
        // every party read the same quota and the side ran to 592.
        int sideTarget = 200;
        int active = 100;
        int quota = 110;

        int firstParty = ReinforcementFielder.EffectiveAllowance(
            ReinforcementFielder.RemainingFieldingAllowance(sideTarget, active), quota);
        Assert.Equal(100, firstParty);

        active += firstParty;   // those men are now standing on the field

        int secondParty = ReinforcementFielder.EffectiveAllowance(
            ReinforcementFielder.RemainingFieldingAllowance(sideTarget, active), quota);
        Assert.Equal(0, secondParty);
    }
}
