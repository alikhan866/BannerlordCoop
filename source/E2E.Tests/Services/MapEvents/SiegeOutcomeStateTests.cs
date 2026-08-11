using GameInterface.Services.MapEvents.Handlers;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.MapEvents;

/// <summary>
/// What state a siege is left in once the battle fought under it ends.
/// </summary>
/// <remarks>
/// The server runs vanilla's <c>MapEvent.FinalizeEventAux</c> authoritatively and clients receive the outcome
/// through replication, so the outcomes themselves are vanilla's. What coop adds is the decision to hold a
/// siege open when a battle ended by retreat rather than by a result — and that decision is expressed by
/// setting <c>_keepSiegeEvent</c>, which is NOT a passive flag.
///
/// FinalizeEventAux branches on it:
///
///   false -> the teardown branch, reached only by SiegeAssault / SiegeOutside.
///   true  -> a separate branch, reached only by SallyOut / BlockadeSallyOut / Blockade.
///
/// So the same flag suppresses an unwanted lift on one battle type and opens an unwanted settlement handover
/// on another. Vanilla only ever sets it from <c>PlayerEncounter.ContinueBattle</c>, for a siege assault whose
/// attackers retreated, so it never reaches the second branch at all. Coop can, which is what these pin.
/// </remarks>
public class SiegeOutcomeStateTests : MapEventTestBase
{
    public SiegeOutcomeStateTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    // Holding the siege open is what the flag does here: FinalizeEventAux's teardown branch is the one that
    // would otherwise lift the camp, and the flag is what skips it.
    [InlineData(MapEvent.BattleTypes.Siege, true)]
    [InlineData(MapEvent.BattleTypes.SiegeOutside, true)]
    // Setting it here does the opposite. These types reach FinalizeEventAux's OTHER branch, which calls
    // SiegeCompleted -> ChangeOwnerOfSettlementAction.ApplyBySiege. Leaving it clear is already "the siege
    // stands", because neither branch matches.
    [InlineData(MapEvent.BattleTypes.SallyOut, false)]
    [InlineData(MapEvent.BattleTypes.BlockadeSallyOutBattle, false)]
    [InlineData(MapEvent.BattleTypes.BlockadeBattle, false)]
    public void TheKeepSiegeFlagIsOnlySafeOnTheTypesWhoseTeardownItSuppresses(
        MapEvent.BattleTypes battleType, bool expected)
    {
        Assert.Equal(expected, BattleFinalizeHandler.KeepSiegeEventSuppressesTeardown(battleType));
    }

    [Fact]
    public void ASallyOutWhoseGarrisonRuns_DoesNotHandTheTownToTheBesieger()
    {
        // The scenario, in vanilla's terms: a sally-out is fought with the sallying garrison as the ATTACKER
        // and the besieger as the DEFENDER (PlayerEncounter.StartBattleInternal picks the sally-out branch off
        // the attacker's CurrentSettlement having a SiegeEvent). So a garrison that turns and runs is a
        // retreating attacker, and MapEvent.EndByRunAway scores that as a DefenderVictory - a win for the
        // besieger.
        //
        // With _keepSiegeEvent set, FinalizeEventAux answers that with
        // SiegeCompleted(settlement, DefenderSide.Leader, isWin: true, SallyOut), and KingdomManager hands the
        // settlement over. Resuming the siege must therefore leave the flag alone on this type.
        var ctx = CreateServerMapEvent();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out var mapEvent));

            mapEvent._mapEventType = MapEvent.BattleTypes.SallyOut;
            mapEvent.RetreatingSide = BattleSideEnum.Attacker;
            Assert.Equal(BattleState.DefenderVictory, VanillaRunAwayResult(mapEvent.RetreatingSide));

            BattleFinalizeHandler.ResumeSiegeAfterEnemyRetreat(mapEvent);

            Assert.False(mapEvent._keepSiegeEvent,
                "a sally-out must resume WITHOUT the flag; setting it is what hands over the settlement");
        }, MapEventDisabledMethods);
    }

    [Fact]
    public void ASiegeOutsideWhoseReliefForceRuns_KeepsTheCampStanding()
    {
        // The other half of the same rule. Here the flag is the only thing that stops FinalizeEventAux from
        // lifting the camp, so resuming the siege means setting it.
        var ctx = CreateServerMapEvent();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(ctx.MapEventId, out var mapEvent));

            mapEvent._mapEventType = MapEvent.BattleTypes.SiegeOutside;
            mapEvent.RetreatingSide = BattleSideEnum.Attacker;

            BattleFinalizeHandler.ResumeSiegeAfterEnemyRetreat(mapEvent);

            Assert.True(mapEvent._keepSiegeEvent,
                "a siege-outside battle needs the flag, or finalize lifts the siege the relief force failed to break");
        }, MapEventDisabledMethods);
    }

    /// <summary>
    /// Vanilla's <c>MapEvent.EndByRunAway</c>, restated: the side that runs is the side that loses. Kept here
    /// so the sally-out test states the outcome it is guarding against rather than asserting a bare bool.
    /// </summary>
    private static BattleState VanillaRunAwayResult(BattleSideEnum retreatingSide)
        => retreatingSide == BattleSideEnum.Attacker
            ? BattleState.DefenderVictory
            : BattleState.AttackerVictory;
}
