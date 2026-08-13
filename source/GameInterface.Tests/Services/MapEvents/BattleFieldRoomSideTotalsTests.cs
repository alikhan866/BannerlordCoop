using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.TroopSupply;
using GameInterface.Services.ObjectManager;
using System;
using TaleWorlds.Core;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// Where the battle-size cap gets the two sides' strengths from.
/// </summary>
/// <remarks>
/// It used to read them off the map event, and a battle whose map event was finalized underneath its own live
/// mission then sized every side at zero - which does not mean "hold back", it means the side may never field
/// another man. Measured live: both sides frozen for three and a half minutes holding 1,123 and 1,293 troops
/// in reserve.
///
/// The reserves are the right source for a second reason too: CoopBattleMissionSpawnHandler already sizes the
/// ENGINE from exactly these totals, so anything else was capping a different battle from the one being
/// fought.
/// </remarks>
public class BattleFieldRoomSideTotalsTests : IDisposable
{
    private const string BattleId = "MapEvent_Created_116871";

    public BattleFieldRoomSideTotalsTests() => CoopTroopSupplierRegistry.ClearAll();

    public void Dispose() => CoopTroopSupplierRegistry.ClearAll();

    [Fact]
    public void ReadsBothSidesFromTheReservesTheServerCommitted()
    {
        Register(BattleSideEnum.Defender, 1507);
        Register(BattleSideEnum.Attacker, 1309);

        Assert.True(BattleFieldRoom.TryReadSideTotals(NoCampaign, BattleId, out var defenders, out var attackers));
        Assert.Equal(1507, defenders);
        Assert.Equal(1309, attackers);
    }

    [Fact]
    public void SurvivesTheMapEventBeingGone()
    {
        // The whole point: nothing here consults the campaign, so a finalized map event cannot zero the cap.
        Register(BattleSideEnum.Defender, 1507);
        Register(BattleSideEnum.Attacker, 1309);

        Assert.True(BattleFieldRoom.TryReadSideTotals(NoCampaign, BattleId, out var defenders, out var attackers));
        Assert.True(defenders > 0 && attackers > 0);
    }

    [Fact]
    public void FailsClosedWhenNoReserveHasLanded()
    {
        // The guard the original code existed for. The one time this failed open, the attacker side reached
        // 1,072 on a battle sized for 400, so "no information" must stay "no allowance".
        Assert.False(BattleFieldRoom.TryReadSideTotals(NoCampaign, BattleId, out var defenders, out var attackers));
        Assert.Equal(0, defenders);
        Assert.Equal(0, attackers);
    }

    [Fact]
    public void AnUnpopulatedSupplierIsNotReadAsAnEmptySide()
    {
        // A supplier that has not been told its side's strength yet would otherwise size that side at zero,
        // and the opponent would be capped against a battle it is not fighting.
        var pending = new CoopTroopSupplier(BattleId, BattleSideEnum.Defender, null, null);
        CoopTroopSupplierRegistry.Register(pending);
        Register(BattleSideEnum.Attacker, 1309);

        Assert.True(BattleFieldRoom.TryReadSideTotals(NoCampaign, BattleId, out var defenders, out var attackers));
        Assert.Equal(0, defenders);
        Assert.Equal(1309, attackers);
    }

    [Fact]
    public void AnUnknownBattleFailsClosed()
    {
        Register(BattleSideEnum.Defender, 1507);

        Assert.False(BattleFieldRoom.TryReadSideTotals(NoCampaign, "MapEvent_Created_999999", out var defenders, out var attackers));
        Assert.Equal(0, defenders);
        Assert.Equal(0, attackers);
    }

    // Null object manager stands for "the campaign cannot answer" - the case every test here is about. The
    // live path ahead of it is the original code unchanged, so what needed covering is the fallback.
    private static readonly IObjectManager NoCampaign = null;

    private static void Register(BattleSideEnum side, int sideTotal)
    {
        var supplier = new CoopTroopSupplier(BattleId, side, null, null);
        // An empty reserve with a side total is the shape a client that owns nothing on this side receives:
        // it still has to know how big the side is, or it sizes the battle from its own share of it.
        supplier.SetReserve(Array.Empty<PartyReserve>(), sideTotal);
        CoopTroopSupplierRegistry.Register(supplier);
    }
}
