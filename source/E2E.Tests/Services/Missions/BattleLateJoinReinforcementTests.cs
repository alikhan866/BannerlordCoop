using Missions.Battles;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// What a client that joined a battle already in progress may field, now and later.
/// </summary>
/// <remarks>
/// Two rules that pull in opposite directions and were, until now, the same rule.
///
/// The OPENING wave is clamped to one man. An initial spawn assumes an empty field, and a client arriving
/// mid-battle computed one anyway: a player joining a live fight holding 392 men took that side to 532 in a
/// single step. One man, not zero, because that one man is the player's own hero.
///
/// The CEILING is a different question. The engine sizes every subsequent wave as
/// <c>Min(batch, phase.InitialSpawnedNumber - NumberOfActiveTroops)</c>, and <c>InitialSpawnedNumber</c> is how
/// many this client actually put out at the opening. Clamping the opening therefore also clamped the ceiling to
/// one - so the instant that hero was standing, the ceiling read zero and stayed there for the rest of the
/// battle.
///
/// That is not a rounding error, it is a client that can never field another man. Observed exactly: a player
/// joined holding eight parties and 952 men, his supplier was asked for a single troop and never asked again,
/// and the seven allied lords in his reserve finished the battle with no kills and no losses. On his screen he
/// had an army he could see and could not command.
/// </remarks>
public class BattleLateJoinReinforcementTests
{
    [Fact]
    public void AJoinerOpensWithOneMan_HisOwnHero()
    {
        Assert.Equal(1, CoopBattleMissionSpawnHandler.OpeningSpawnForLateJoiner(392));
        Assert.Equal(1, CoopBattleMissionSpawnHandler.OpeningSpawnForLateJoiner(952));
    }

    [Fact]
    public void AJoinerWithNothingToField_StaysAtNothing()
    {
        // Min, not a flat 1: a supplier that can reach nobody must not be told it opened with a man it does
        // not have. The spawn handler reads a missing origin as a battle it cannot start.
        Assert.Equal(0, CoopBattleMissionSpawnHandler.OpeningSpawnForLateJoiner(0));
    }

    [Fact]
    public void AJoinersCeilingIsHisRealShare_NotTheOneManHeOpenedWith()
    {
        // The defect, in one line. The joiner opened with 1; his share of the field is 128. A ceiling of 1
        // means he can never field another man however many casualties open in front of him.
        Assert.Equal(128, CoopBattleMissionSpawnHandler.ReinforcementCeiling(
            lateJoin: true, spawnedInOpeningWave: 1, openingWaveHeldBack: 128));
    }

    [Fact]
    public void ACeilingIsNeverLoweredByRestoringIt()
    {
        // Max, not assignment. If the opening wave somehow landed MORE men than the share held back, taking
        // the held-back figure would cut the ceiling below what is already standing - and a ceiling below the
        // live count is exactly the state that fields nobody ever again.
        Assert.Equal(200, CoopBattleMissionSpawnHandler.ReinforcementCeiling(
            lateJoin: true, spawnedInOpeningWave: 200, openingWaveHeldBack: 128));
    }

    [Fact]
    public void AClientThatStartedTheBattle_IsLeftAlone()
    {
        // Only a late joiner had its opening wave clamped, so only a late joiner needs it restored. Touching
        // anyone else's InitialSpawnedNumber would be rewriting a number the engine set correctly - and that
        // number is the ceiling for the whole battle.
        Assert.Equal(203, CoopBattleMissionSpawnHandler.ReinforcementCeiling(
            lateJoin: false, spawnedInOpeningWave: 203, openingWaveHeldBack: 999));
    }

    [Fact]
    public void TheHeldBackWaveIsTheShareBeforeTheClamp_NotAfterIt()
    {
        // The bug this exists to prevent is invisible by construction. The held-back wave and the clamped
        // opening wave are read from the SAME field, so capturing it after the clamp yields 1 - and restoring
        // a ceiling of 1 restores exactly the ceiling that was broken. It reads as a working fix and changes
        // nothing: a joining player's supplier was still asked for one troop and never asked again, with 950
        // of his men waiting behind it.
        //
        // Stated as the property that has to hold: whatever the joiner was NOT allowed to field is what his
        // ceiling must be restored to, and that is always the full share, never the one man.
        var (opening, heldBack) = CoopBattleMissionSpawnHandler.OpeningAndHeldBack(
            lateJoin: true, reachableInitial: 128);

        Assert.Equal(1, opening);
        Assert.Equal(128, heldBack);

        // And the two must not be the same number, which is the whole failure: if held-back is read back off
        // the clamped phase it becomes 1, and the restored ceiling is the broken one.
        Assert.NotEqual(opening, heldBack);

        Assert.Equal(128, CoopBattleMissionSpawnHandler.ReinforcementCeiling(
            lateJoin: true, spawnedInOpeningWave: opening, openingWaveHeldBack: heldBack));
    }

    [Fact]
    public void AClientThatStartedTheBattleHoldsNothingBack()
    {
        // Nothing was withheld from it, so there is nothing to restore later - and restoring anything would
        // rewrite a ceiling the engine set correctly.
        var (opening, heldBack) = CoopBattleMissionSpawnHandler.OpeningAndHeldBack(
            lateJoin: false, reachableInitial: 203);

        Assert.Equal(203, opening);
        Assert.Equal(0, heldBack);
    }

    [Fact]
    public void ARoundRestartIsNotALateJoin()
    {
        // Both look identical from outside - the battle is under way and deployment is long since activated -
        // but they want opposite things. A restart is precisely when every side puts its full re-derived
        // opening wave back out; clamping it to one man would empty the battle it was meant to re-balance.
        Assert.False(CoopBattleMissionSpawnHandler.IsLateJoin(restartingRound: true, deploymentActivated: true));
        Assert.True(CoopBattleMissionSpawnHandler.IsLateJoin(restartingRound: false, deploymentActivated: true));
        Assert.False(CoopBattleMissionSpawnHandler.IsLateJoin(restartingRound: false, deploymentActivated: false));
    }
}
