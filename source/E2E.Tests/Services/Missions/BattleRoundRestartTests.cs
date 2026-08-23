using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Patches;
using GameInterface.Services.MapEvents.TroopSupply;
using Missions.Battles;
using System;
using System.Linq;
using TaleWorlds.Core;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Restarting a battle's round once reinforcements have changed what the fight actually is.
/// </summary>
/// <remarks>
/// The engine fixes a battle's shape at its first Init: the battle size is split between the sides in proportion
/// to the totals handed over then, and every later wave is drawn against that split. A side that opens 100
/// against 300 therefore keeps feeding in at its opening rate however many lords ride in behind it - the relief
/// is on the map and on the scoreboard but never reaches the field. Restarting re-derives the split from the
/// totals as they now stand.
///
/// These cover the decisions around the restart. The re-sizing itself is
/// <see cref="CoopBattleMissionSpawnHandler"/>'s existing joint-init path, whose proportional split and
/// per-player guarantee are covered by <see cref="CoopTroopSupplierTests"/>.
/// </remarks>
public class BattleRoundRestartTests
{
    private static TroopReserveEntry[] Entries(int count, int seedBase = 500)
        => Enumerable.Range(0, count)
            .Select(i => new TroopReserveEntry(seedBase + i, $"Char_{i}", formationClass: 0))
            .ToArray();

    private static PartyReserve Party(string id, int count, int supplied = 0, int seedBase = 500)
        => new PartyReserve(id, supplied, Entries(count, seedBase), false, 0, -1);

    // ---- when a restart is worth it -------------------------------------------------------------------

    [Fact]
    public void RelievingASideThatDoubles_IsWorthRestarting()
    {
        // The reported shape: a side holding at 100 is joined by 2000 men. Everything about the battle's sizing
        // was derived from the 100.
        Assert.True(BattleRoundRestartPolicy.IsMaterialGrowth(sizedAtTotal: 100, currentTotal: 2100));
    }

    [Fact]
    public void ASingleScoutJoiningAHugeBattle_IsNotWorthRestarting()
    {
        // A restart sends everyone back to their spawn. That price has to buy something: one man joining 900 is
        // not a different battle, and paying it for him would mean paying it constantly.
        Assert.False(BattleRoundRestartPolicy.IsMaterialGrowth(sizedAtTotal: 900, currentTotal: 901));
    }

    [Fact]
    public void ASideLosingMen_NeverTriggersARestart()
    {
        // Casualties are the normal course of a battle, not a reason to re-form the lines. Only growth counts.
        Assert.False(BattleRoundRestartPolicy.IsMaterialGrowth(sizedAtTotal: 300, currentTotal: 120));
        Assert.False(BattleRoundRestartPolicy.IsMaterialGrowth(sizedAtTotal: 300, currentTotal: 300));
    }

    [Fact]
    public void ASideThatWasEmptyWhenSized_IsAlwaysMaterial()
    {
        // There is no proportion to take of nothing, and a side that had no one is entirely changed by anyone.
        Assert.True(BattleRoundRestartPolicy.IsMaterialGrowth(sizedAtTotal: 0, currentTotal: 1));
    }

    [Fact]
    public void EitherSideGrowing_TriggersTheRestart()
    {
        // A restart re-sizes the WHOLE battle, so it must answer to growth on either side: reinforcing the
        // defenders changes the attackers' share of the field just as much as it changes the defenders'.
        Assert.True(BattleRoundRestartPolicy.ShouldRestart(
            defenderSizedAt: 100, defenderNow: 900, attackerSizedAt: 300, attackerNow: 300));

        Assert.True(BattleRoundRestartPolicy.ShouldRestart(
            defenderSizedAt: 100, defenderNow: 100, attackerSizedAt: 300, attackerNow: 2300));

        Assert.False(BattleRoundRestartPolicy.ShouldRestart(
            defenderSizedAt: 100, defenderNow: 100, attackerSizedAt: 300, attackerNow: 300));
    }

    // ---- the countdown --------------------------------------------------------------------------------

    [Fact]
    public void TheRoundRestartsOnlyAfterTheCountdown()
    {
        var restarts = 0;
        var restarter = new BattleRoundRestarter(() => null, () => restarts++);

        restarter.Schedule(countdownSeconds: 5f, restartSequence: 1);
        Assert.True(restarter.IsPending);

        restarter.Tick(4.9f);
        Assert.Equal(0, restarts); // players are still being told what is about to happen

        restarter.Tick(0.2f);
        Assert.Equal(1, restarts);
        Assert.False(restarter.IsPending);
    }

    [Fact]
    public void ARepeatedInstruction_DoesNotRestartTwice()
    {
        // The schedule is broadcast; a duplicate or retransmitted delivery must not restart the round again,
        // nor extend a countdown that is already running.
        var restarts = 0;
        var restarter = new BattleRoundRestarter(() => null, () => restarts++);

        // The dangerous shape is a resend that lands AFTER the restart it duplicates has already run: accepting
        // it would send everyone back to their spawn a second time for reinforcements already accounted for.
        restarter.Schedule(5f, restartSequence: 7);
        restarter.Tick(6f);
        Assert.Equal(1, restarts);

        restarter.Schedule(5f, restartSequence: 7);
        Assert.False(restarter.IsPending, "a restart already carried out must not be scheduled again");

        restarter.Tick(6f);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public void AStaleInstructionArrivingLate_IsIgnored()
    {
        Assert.True(BattleRoundRestarter.ShouldAccept(incomingSequence: 3, lastAcceptedSequence: 2));
        Assert.False(BattleRoundRestarter.ShouldAccept(incomingSequence: 2, lastAcceptedSequence: 2));
        Assert.False(BattleRoundRestarter.ShouldAccept(incomingSequence: 1, lastAcceptedSequence: 2));
    }

    [Fact]
    public void AFailedRestart_LeavesTheBattlePlayable()
    {
        // Restarting is an improvement, not a prerequisite. If it throws, the fight continues at its previous
        // sizing - which is strictly better than dropping everyone out of the mission.
        var restarter = new BattleRoundRestarter(() => null, () => throw new InvalidOperationException("boom"));

        restarter.Schedule(1f, restartSequence: 1);
        restarter.Tick(2f);

        Assert.False(restarter.IsPending);
    }

    // ---- the reserve after a restart ------------------------------------------------------------------

    [Fact]
    public void ARestartLetsTheWholeReserveBeFieldedAgain()
    {
        // A restart clears the field and spawns the battle afresh, so the reserve has to be drawable from the
        // top. Without the rewind the second round could only field what the first had not yet reached, and a
        // side that had already committed most of its men would re-form nearly empty.
        var supplier = new CoopTroopSupplier("M1", BattleSideEnum.Attacker, null, new BattleAgentBudget());
        supplier.SetReserve(new[] { Party("A", 10) }, sideTotal: 10, playerOwnedParties: 0, authoritativeBattleSize: 0);
        supplier.SupplyTroops(8);

        Assert.Equal(2, supplier.NumTroopsNotSupplied);

        supplier.RewindForRoundRestart();

        Assert.Equal(10, supplier.NumTroopsNotSupplied);
        Assert.True(supplier.AnyTroopRemainsToBeSupplied);
    }

    [Fact]
    public void TheRewindIsVisibleToTheServerAsANewRevision()
    {
        // The ledger tracks reserves by revision; a rewind that kept the old one would read as "nothing
        // changed" and the server's view of who is on the field would drift from the clients'.
        var supplier = new CoopTroopSupplier("M1", BattleSideEnum.Attacker, null, new BattleAgentBudget());
        supplier.SetReserve(new[] { Party("A", 4) }, sideTotal: 4, playerOwnedParties: 0, authoritativeBattleSize: 0);
        var before = supplier.ReserveRevision;

        supplier.RewindForRoundRestart();

        Assert.True(supplier.ReserveRevision > before);
    }

    [Fact]
    public void ARewindDoesNotResurrectTheDead()
    {
        // Casualties describe men the campaign has already been told about. Re-forming the lines must not
        // un-report them, or the post-battle roster would credit back troops that died.
        var supplier = new CoopTroopSupplier("M1", BattleSideEnum.Attacker, null, new BattleAgentBudget());
        supplier.SetReserve(new[] { Party("A", 5) }, sideTotal: 5, playerOwnedParties: 0, authoritativeBattleSize: 0);
        var removedBefore = supplier.NumRemovedTroops;

        supplier.RewindForRoundRestart();

        Assert.Equal(removedBefore, supplier.NumRemovedTroops);
    }

    // ---- staying in step with the other clients -------------------------------------------------------

    [Fact]
    public void ARestartWaitsForTheRebuiltReserve()
    {
        // The server drops the battle's reserves and each client asks for its own back. Re-forming before that
        // reply lands would field the OLD reserve while every other client fields the new one - the two views
        // of who is on the field would disagree for the rest of the battle.
        var restarts = 0;
        var reservesReady = false;
        var restarter = new BattleRoundRestarter(() => null, () => restarts++, () => reservesReady);

        restarter.Schedule(5f, restartSequence: 1);
        restarter.Tick(6f);
        Assert.Equal(0, restarts);

        reservesReady = true;
        restarter.Tick(0.1f);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public void ALostReserveReplyCannotStrandThisClient()
    {
        // Waiting forever would be the worse desync: everyone else has re-formed and this client is still
        // fighting the previous round. Past the grace it goes anyway, stale reserve and all.
        var restarts = 0;
        var restarter = new BattleRoundRestarter(() => null, () => restarts++, () => false);

        restarter.Schedule(5f, restartSequence: 1);
        restarter.Tick(6f);
        Assert.Equal(0, restarts);

        restarter.Tick(BattleRoundRestarter.ReserveGraceSeconds + 0.1f);
        Assert.Equal(1, restarts);
        Assert.False(restarter.IsPending);
    }

    [Fact]
    public void WithNothingToWaitFor_TheCountdownAloneDecides()
    {
        // No readiness check supplied (as in a mission with no coop spawn handler) must not mean "wait forever".
        var restarts = 0;
        var restarter = new BattleRoundRestarter(() => null, () => restarts++);

        restarter.Schedule(5f, restartSequence: 1);
        restarter.Tick(6f);

        Assert.Equal(1, restarts);
    }

    // ---- a restart is not a late join -----------------------------------------------------------------

    [Fact]
    public void ARestartIsNotTreatedAsALateJoin()
    {
        // Both happen with deployment long since activated, so they are indistinguishable from the engine's
        // point of view - but they want opposite things. A late joiner fields only its hero so its arrival is
        // not a spike; a restart is precisely the moment every side must put its full re-derived opening wave
        // back on the field. Conflating them would clamp every side to one man and empty the battle the
        // restart exists to re-balance.
        Assert.False(CoopBattleMissionSpawnHandler.IsLateJoin(restartingRound: true, deploymentActivated: true));
    }

    [Fact]
    public void AGenuineLateJoinerIsStillLimited()
    {
        // The late-join clamp must survive the distinction above: someone walking into a live fight still
        // fields their hero only.
        Assert.True(CoopBattleMissionSpawnHandler.IsLateJoin(restartingRound: false, deploymentActivated: true));
    }

    [Fact]
    public void StartingABattleIsNeitherOfThose()
    {
        // Before deployment activates nobody is arriving late, restart or not.
        Assert.False(CoopBattleMissionSpawnHandler.IsLateJoin(restartingRound: false, deploymentActivated: false));
        Assert.False(CoopBattleMissionSpawnHandler.IsLateJoin(restartingRound: true, deploymentActivated: false));
    }

    // ---- sizing must follow the reserve until the opening wave ---------------------------------------

    [Fact]
    public void ABattleThatGrowsBeforeItStarts_IsSizedFromWhatItBecame()
    {
        // Lords joining while players sit on the deployment screen are the ordinary case, and sizing used to
        // latch permanently the moment both reserves first landed. The engine's opening wave was then split
        // from stale totals and the battle simply started over its own size - measured at 445 agents on a
        // battle sized for 400, coming down only as men died, with nothing left to correct it.
        //
        // Expressed as the rule the fix restores: a reserve that differs from the one the current sizing was
        // derived from is a reason to size again.
        Assert.True(CoopBattleMissionSpawnHandler.ShouldReSize(initialSpawnOver: false, sizedAtReserveRevision: 4, currentReserveRevision: 6));
        Assert.False(CoopBattleMissionSpawnHandler.ShouldReSize(initialSpawnOver: false, sizedAtReserveRevision: 6, currentReserveRevision: 6));

        // And never once the opening wave has gone out: re-running Init over a populated field is the exact
        // class of change that caused the round-restart's failures.
        Assert.False(CoopBattleMissionSpawnHandler.ShouldReSize(initialSpawnOver: true, sizedAtReserveRevision: 4, currentReserveRevision: 6));
    }

    [Fact]
    public void TwoClientsEnteringAtDifferentMoments_ConvergeOnTheSameSizing()
    {
        // The desync half of the same defect. Each client latched at whatever the reserve happened to be when
        // ITS pair arrived, so two clients entering seconds apart sized the same battle differently and spawned
        // different numbers. Following the reserve means both end at the latest revision, whatever they started
        // from.
        const int latest = 9;

        Assert.True(CoopBattleMissionSpawnHandler.ShouldReSize(false, sizedAtReserveRevision: 4, currentReserveRevision: latest));
        Assert.True(CoopBattleMissionSpawnHandler.ShouldReSize(false, sizedAtReserveRevision: 7, currentReserveRevision: latest));
        Assert.False(CoopBattleMissionSpawnHandler.ShouldReSize(false, sizedAtReserveRevision: latest, currentReserveRevision: latest));
    }

    // ---- the live-battle heartbeat --------------------------------------------------------------------

    [Fact]
    public void TheLiveBattleScanIsPacedOnRealTime()
    {
        // Campaign time stops for the whole battle, so the rescan is driven by a real-time cadence. It must
        // actually be a cadence: RealTick follows the frame rate, and scanning the map's locatable index every
        // frame is not something to do on a host that happens to be fast.
        Assert.False(LiveBattleReinforcementTickPatch.IsDue(0.5));
        Assert.False(LiveBattleReinforcementTickPatch.IsDue(LiveBattleReinforcementTickPatch.IntervalSeconds - 0.01));
        Assert.True(LiveBattleReinforcementTickPatch.IsDue(LiveBattleReinforcementTickPatch.IntervalSeconds));
        Assert.True(LiveBattleReinforcementTickPatch.IsDue(30));
    }
}
