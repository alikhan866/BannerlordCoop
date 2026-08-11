using System.Collections.Generic;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.TroopSupply;
using Missions.Battles;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Holding the engine's own reinforcement waves to what the field has room for.
/// </summary>
/// <remarks>
/// The engine sizes a wave from <c>NumberOfActiveTroops = spawned - removed</c>, counted per supplier, so it
/// sees only the agents it spawned through that supplier - not a peer's replicated puppets, and not the ones
/// the reinforcement fielder spawns directly for a party that joined mid-battle. It therefore measures a full
/// side as a half-empty one and asks for the difference.
///
/// Measured live on a battle sized for 400: the attacker side stood at 190, had room for about 13, and the
/// engine asked its supplier for 101. Three times. Each wave landed inside two seconds, took the side past 290
/// and was cut back down by the balancer - and since a phase's RemainingSpawnNumber is consumed by what
/// actually spawns, roughly 300 men were spent out of a 534-man reserve without ever fighting. When that
/// reserve reached zero the side could not reinforce at all, which is what "not everyone spawned in, and then
/// most of them ran away" looked like from the scoreboard.
/// </remarks>
public class BattleWaveCapTests
{
    [Fact]
    public void AWaveIsBoundedByTheFieldRatherThanByWhatTheEngineBelieves()
    {
        // The reported case in numbers: a side sized for 203 with 190 already standing has room for 13, however
        // large a wave the engine asks for.
        Assert.Equal(13, BattleFieldRoom.RoomLeft(sideTarget: 203, activeOnSide: 190));
    }

    [Fact]
    public void AFullSideHasNoRoom_AndAnOverFullOneNeverReportsNegativeRoom()
    {
        // A side goes transiently over its target - puppets landing, a wave already in flight. That has to read
        // as "no room". A negative would be worse than useless here: it is compared against a request, so it
        // would let every request through.
        Assert.Equal(0, BattleFieldRoom.RoomLeft(sideTarget: 200, activeOnSide: 200));
        Assert.Equal(0, BattleFieldRoom.RoomLeft(sideTarget: 200, activeOnSide: 350));
    }

    [Fact]
    public void CasualtiesReopenTheRoom()
    {
        Assert.Equal(0, BattleFieldRoom.RoomLeft(200, 200));
        Assert.Equal(80, BattleFieldRoom.RoomLeft(200, 120));
    }

    [Fact]
    public void TheCapAgreesWithTheTargetTheBalancerTrimsTo()
    {
        // Both now derive from BattleSizeTargets. If they ever disagreed, one would spawn men the other
        // immediately stood down - which is precisely the treadmill this was written to end, so the agreement
        // is asserted rather than assumed.
        var shared = BattleSizeTargets.Calculate(
            defenderTotal: 1000, attackerTotal: 1000, battleSize: 400,
            maximumSideRatio: 0.75f, defenderAdvantageFactor: 1f);

        var fielder = ReinforcementFielder.RecoveryTargets.Calculate(
            1000, 1000, 400, 0.75f, 1f);

        Assert.Equal(fielder.Defenders, shared.Defenders);
        Assert.Equal(fielder.Attackers, shared.Attackers);
        Assert.Equal(shared.Defenders, shared.For(TaleWorlds.Core.BattleSideEnum.Defender));
        Assert.Equal(shared.Attackers, shared.For(TaleWorlds.Core.BattleSideEnum.Attacker));
    }

    // ---- the decision the supplier actually makes ----------------------------------------------------

    [Fact]
    public void AnOverLargeRequest_IsCutToWhatMyQuotaStillAllows()
    {
        // The live case: the engine asked for 101 while this client had 13 places left of its own share.
        Assert.Equal(13, CoopTroopSupplier.CapWaveToQuota(requested: 101, remainingQuota: 13));
    }

    [Fact]
    public void ARequestThatAlreadyFits_IsNotInflatedToFillTheQuota()
    {
        // Min, not assignment. The quota is a ceiling, not a target: handing over more than the engine asked
        // for would spawn men it has no deployment plan for.
        Assert.Equal(5, CoopTroopSupplier.CapWaveToQuota(requested: 5, remainingQuota: 100));
    }

    [Fact]
    public void AFilledQuotaSuppliesNobody()
    {
        Assert.Equal(0, CoopTroopSupplier.CapWaveToQuota(requested: 101, remainingQuota: 0));
    }

    [Fact]
    public void AQuotaIsPrivate_SoASlowerClientIsNeverStarved()
    {
        // The whole point of quotas over shared room. Two clients each holding places on the same side draw
        // against their OWN remaining allowance, so one filling its share cannot leave the other with nothing.
        //
        // Under the old rule both read the side's leftover room, which is zero once the side is full - and
        // every share of zero is zero. A joining player then fielded exactly one troop, his own hero, and was
        // refused 64 men every three seconds for a whole battle while holding 950 in reserve.
        Assert.Equal(35, CoopTroopSupplier.CapWaveToQuota(requested: 100, remainingQuota: 35));
        Assert.Equal(65, CoopTroopSupplier.CapWaveToQuota(requested: 100, remainingQuota: 65));
    }

    [Fact]
    public void AnUnlimitedQuota_PassesTheRequestThroughUNTOUCHED()
    {
        // This is the deployment case, and it must not merely be "a very large cap" - the request has to
        // arrive whole. CheckDeployment reserves InitialSpawnNumber and SKIPS THE ENTIRE SIDE, plan-making
        // included, while the reservation falls short; a side handed a fraction of its deployment is therefore
        // never planned, never spawns, and leaves the player with no agent to control.
        Assert.Equal(163, CoopTroopSupplier.CapWaveToQuota(
            requested: 163, remainingQuota: BattleFieldRoom.Unlimited));
    }

    [Fact]
    public void ANegativeQuotaNeverBecomesAnAllowance()
    {
        // A quota can read below zero transiently - the side's allocation shrinks as its strength falls while
        // this client's men are still standing. That must mean "field nobody", never wrap into a large wave.
        Assert.Equal(0, CoopTroopSupplier.CapWaveToQuota(requested: 50, remainingQuota: -20));
    }

    // ---- the queue that feeds parties which joined mid-battle ----------------------------------------

    [Fact]
    public void EveryQueuedPartyIsOfferedATurn()
    {
        // The loop used to stop at the first party that still had men waiting, on the reasoning that a GLOBAL
        // render limit blocking one party blocks them all. Fielding is now capped PER SIDE, so that stopped
        // being true: an attacker-side party at its cap blocked a defender-side party with room.
        //
        // Live, this left the party at the head of the queue fielding 192 men one at a time across a whole
        // battle while five parties behind it fielded none - 497 men who never left the queue. They are the
        // lords the after-battle screen lists with a full roster, no kills and no losses.
        var queue = new List<string> { "a", "b", "c" };
        var served = new List<string>();

        ReinforcementFielder.DrainQueue(queue, item => { served.Add(item); return false; });

        Assert.Equal(new[] { "a", "b", "c" }, served);
    }

    [Fact]
    public void TheHeadRotates_SoNoPartyMonopolisesTheRoom()
    {
        // Visiting everyone is necessary but not sufficient: each turn re-reads the remaining room, so whoever
        // goes first takes it and the rest still get nothing. Rotating is what makes "first" someone else next
        // time.
        var queue = new List<string> { "a", "b", "c" };

        ReinforcementFielder.DrainQueue(queue, _ => false);
        Assert.Equal(new[] { "b", "c", "a" }, queue);

        ReinforcementFielder.DrainQueue(queue, _ => false);
        Assert.Equal(new[] { "c", "a", "b" }, queue);
    }

    [Fact]
    public void AFinishedPartyLeavesTheQueue_AndDoesNotDisturbTheOthersTurns()
    {
        // Removing an item mid-iteration must not skip the one that slides into its place - a classic
        // index-walking mistake, and here it would silently starve whichever party followed a finished one.
        var queue = new List<string> { "a", "done", "b" };
        var served = new List<string>();

        ReinforcementFielder.DrainQueue(queue, item =>
        {
            served.Add(item);
            return item == "done";
        });

        Assert.Equal(new[] { "a", "done", "b" }, served);
        Assert.Equal(new[] { "b", "a" }, queue);
    }

    [Fact]
    public void ASingleQueuedParty_IsServedRepeatedly_WithNothingToRotate()
    {
        var queue = new List<string> { "only" };

        ReinforcementFielder.DrainQueue(queue, _ => false);
        ReinforcementFielder.DrainQueue(queue, _ => false);

        Assert.Equal(new[] { "only" }, queue);
    }

    [Fact]
    public void AnEmptyQueue_IsNotAnError()
    {
        var queue = new List<string>();
        ReinforcementFielder.DrainQueue(queue, _ => throw new System.InvalidOperationException("nothing to serve"));
        Assert.Empty(queue);
    }
}
