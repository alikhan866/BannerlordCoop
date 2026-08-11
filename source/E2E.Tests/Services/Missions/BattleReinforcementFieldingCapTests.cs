using Missions.Battles;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// How many men a party joining a live battle may put on the field at once.
/// </summary>
/// <remarks>
/// The initial troops reach the field through the engine's spawn logic, which honours the battle size. A party
/// that joins AFTER the battle started does not: <c>ReinforcementFielder.SpawnReinforcementParty</c> enqueues
/// every able troop in its roster and spawns them directly, so the only limit that applied was the engine's
/// 2000-agent RENDER ceiling. A 1400-man party joining a siege therefore arrived all at once, far past what
/// the battle was sized for.
///
/// Vanilla never does this: a party joining a live battle goes into its side's pool and
/// MissionAgentSpawnLogic feeds it in as casualties make room. The cap restores that shape - the side's
/// battle-size target less whoever is already standing on it - and the fielder's existing queue lets Tick
/// spawn the remainder as the field clears.
///
/// These cover the sizing rule itself. The spawn plumbing around it is covered by
/// <see cref="BattleReinforcementSpawnTests"/>, whose mock mission carries no spawn logic and so is
/// deliberately left uncapped there.
/// </remarks>
public class BattleReinforcementFieldingCapTests
{
    // The values SandBoxMissionSpawnHandler.CreateSandBoxBattleWaveSpawnSettings actually passes, which is
    // what a coop battle is initialised with (CoopBattleMissionSpawnHandler.RunJointInit).
    private const float MaximumSideRatio = 0.75f;
    private const float DefenderAdvantage = 1f;

    private static int AllowanceFor(int defenderTotal, int attackerTotal, int battleSize, int activeOnSide,
        bool defenderSide = true)
    {
        var targets = ReinforcementFielder.RecoveryTargets.Calculate(
            defenderTotal, attackerTotal, battleSize, MaximumSideRatio, DefenderAdvantage);

        return ReinforcementFielder.RemainingFieldingAllowance(
            defenderSide ? targets.Defenders : targets.Attackers, activeOnSide);
    }

    [Fact]
    public void AHugeReinforcement_IsCappedToTheBattleSize_NotItsRosterSize()
    {
        // The reported siege: a 1400-strong party joins a battle sized for 400 across both sides, with the
        // defender side already holding its share.
        var allowance = AllowanceFor(defenderTotal: 1000, attackerTotal: 1000, battleSize: 400, activeOnSide: 150);

        Assert.True(allowance < 1400, "the whole roster must not be fielded at once");
        Assert.Equal(50, allowance); // 400 split evenly -> 200 a side, less the 150 already fighting
    }

    [Fact]
    public void AFullSide_FieldsNobody_AndWaitsForCasualties()
    {
        // At or over its allocation the side is full; the remainder stays queued for Tick rather than being
        // dropped, which is how the engine's own reinforcement waves behave.
        Assert.Equal(0, AllowanceFor(1000, 1000, 400, activeOnSide: 200));
        Assert.Equal(0, AllowanceFor(1000, 1000, 400, activeOnSide: 999));
    }

    [Fact]
    public void CasualtiesReopenTheAllowance()
    {
        var full = AllowanceFor(1000, 1000, 400, activeOnSide: 200);
        var afterLosses = AllowanceFor(1000, 1000, 400, activeOnSide: 120);

        Assert.Equal(0, full);
        Assert.Equal(80, afterLosses);
    }

    [Fact]
    public void EachSideIsCappedAgainstItsOwnAllocation()
    {
        // Both sides are sized from the same battle size, so a reinforcement on either is bounded by that
        // side's own share rather than by the whole battle.
        var defenders = AllowanceFor(1000, 1000, 400, activeOnSide: 0, defenderSide: true);
        var attackers = AllowanceFor(1000, 1000, 400, activeOnSide: 0, defenderSide: false);

        Assert.Equal(200, defenders);
        Assert.Equal(200, attackers);
    }

    [Fact]
    public void AReinforcedSide_IsSizedFromItsNewStrength_NotItsStartingStrength()
    {
        // The reason the allowance reads LIVE map-event strength rather than the reserve totals captured at
        // battle start. A side that began with 100 against 200 and has since been joined by 1200 men is now
        // the stronger side, and its share of the field has to grow to match — otherwise it stays pinned at
        // the hundred it happened to start with and twelve lords' worth of relief can never reach the fight.
        var atStart = AllowanceFor(defenderTotal: 100, attackerTotal: 200, battleSize: 500, activeOnSide: 0);
        var afterReinforcement = AllowanceFor(defenderTotal: 1300, attackerTotal: 200, battleSize: 500, activeOnSide: 0);

        Assert.Equal(100, atStart); // capped by its own headcount while it really is 100 strong
        Assert.True(afterReinforcement > atStart,
            "a side that has been reinforced must be allowed more of the field than it started with");
        Assert.Equal(375, afterReinforcement); // 0.75 side cap of a 500 battle
    }

    [Fact]
    public void TheSideCapStillBoundsAHugeReinforcement()
    {
        // Growing with strength must not mean the whole field: 1300 against 200 is clamped to the 0.75 ratio,
        // so a relief force widens the front without dumping itself in.
        var allowance = AllowanceFor(defenderTotal: 1300, attackerTotal: 200, battleSize: 500, activeOnSide: 0);

        Assert.True(allowance < 1300, "the side cap must still apply");
        Assert.Equal(375, allowance);
    }

    [Fact]
    public void AnUnsizedBattle_ImposesNoAllocation()
    {
        // Battle size 0 means nothing has sized this battle; there is no allocation to respect and the render
        // budget stays the only limit, which is the behaviour before the cap existed.
        Assert.Equal(0, AllowanceFor(1000, 1000, battleSize: 0, activeOnSide: 0));
    }

    [Fact]
    public void AnUnknownAllocation_FieldsNobody_RatherThanEverybody()
    {
        // The allowance is derived from the battle's own sizing, and that sizing can briefly be unavailable -
        // the map event failed to resolve mid-battle in a live session. The question is what "I cannot work
        // out the limit" should mean.
        //
        // It used to mean int.MaxValue: no limit at all. In the window where the map event would not resolve,
        // the fielder spawned 840 troops in one minute and the attacker side reached 1,072 on a battle sized
        // for 400, which is the whole fight ruined. Refusing to field until the limit is known again costs a
        // second of reinforcement.
        //
        // RemainingFieldingAllowance is the arithmetic; a zero target must yield zero, never "unbounded".
        Assert.Equal(0, ReinforcementFielder.RemainingFieldingAllowance(sideTarget: 0, activeOnSide: 0));
        Assert.Equal(0, ReinforcementFielder.RemainingFieldingAllowance(sideTarget: 0, activeOnSide: 150));
    }

    [Fact]
    public void AllowanceNeverGoesNegative_WhenASideIsOverItsAllocation()
    {
        // A side can exceed its target transiently (puppets landing, a wave already in flight). That must read
        // as "no room", never as a negative that would wrap into a huge allowance.
        Assert.Equal(0, ReinforcementFielder.RemainingFieldingAllowance(sideTarget: 200, activeOnSide: 350));
    }
}
