using Common.Util;
using GameInterface.Services.MapEvents;
using HarmonyLib;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using TaleWorlds.Library;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// Holding a map event that was finalized while its mission was still being fought, so the result the players
/// are about to produce has something to commit into.
/// </summary>
/// <remarks>
/// Measured live: a DefenderVictory reconciled by every mission member, then refused three times with "the map
/// event is no longer active". No casualties were applied and the beaten army survived intact.
/// </remarks>
public class FinalizedBattleRetentionTests
{
    private const string BattleId = "MapEvent_Created_116871";

    [Fact]
    public void AFinalizedBattleIsStillReachableWhenItsResultArrives()
    {
        var retention = new FinalizedBattleRetention();
        retention.Retain(BattleId, IntactMapEvent());

        Assert.True(retention.TryGet(BattleId, out var recovered));
        Assert.NotNull(recovered);
    }

    [Fact]
    public void AnEmptiedGraphIsRefusedRatherThanHalfCommitted()
    {
        // The one outcome worse than losing a result is applying half of it. Committing walks both sides and
        // is not atomic, so an event the finalize has already emptied must not be handed back.
        var retention = new FinalizedBattleRetention();
        retention.Retain(BattleId, TornDownMapEvent());

        Assert.False(retention.TryGet(BattleId, out var recovered));
        Assert.Null(recovered);
    }

    [Fact]
    public void ReleasingStopsItBeingHeld()
    {
        var retention = new FinalizedBattleRetention();
        retention.Retain(BattleId, IntactMapEvent());
        retention.Release(BattleId);

        Assert.False(retention.TryGet(BattleId, out _));
    }

    [Fact]
    public void AnUnknownBattleIsNotClaimed()
    {
        var retention = new FinalizedBattleRetention();
        retention.Retain(BattleId, IntactMapEvent());

        Assert.False(retention.TryGet("MapEvent_Created_999999", out _));
    }

    [Fact]
    public void OldestIsEvictedSoAResultThatNeverArrivesCannotPinTheGraph()
    {
        // Each entry holds a whole map event. A battle nobody ever concludes - a player who alt-F4s mid-fight -
        // must not keep one alive for the rest of the session.
        var retention = new FinalizedBattleRetention();
        for (int i = 0; i < 12; i++)
            retention.Retain($"MapEvent_Created_{i}", IntactMapEvent());

        Assert.False(retention.TryGet("MapEvent_Created_0", out _));
        Assert.True(retention.TryGet("MapEvent_Created_11", out _));
    }

    private static MapEvent IntactMapEvent()
    {
        var mapEvent = ObjectHelper.SkipConstructor<MapEvent>();
        mapEvent._sides = new[]
        {
            SideWithAParty(BattleSideEnum.Defender),
            SideWithAParty(BattleSideEnum.Attacker),
        };
        return mapEvent;
    }

    private static MapEvent TornDownMapEvent()
    {
        var mapEvent = ObjectHelper.SkipConstructor<MapEvent>();
        mapEvent._sides = new[]
        {
            EmptySide(BattleSideEnum.Defender),
            EmptySide(BattleSideEnum.Attacker),
        };
        return mapEvent;
    }

    private static MapEventSide SideWithAParty(BattleSideEnum side)
    {
        var mapEventSide = EmptySide(side);
        mapEventSide._battleParties.Add(ObjectHelper.SkipConstructor<MapEventParty>());
        return mapEventSide;
    }

    private static MapEventSide EmptySide(BattleSideEnum side)
    {
        var mapEventSide = ObjectHelper.SkipConstructor<MapEventSide>();
        // _battleParties is readonly, and vanilla fills it through the constructor this test deliberately
        // skips. MapEventSideRegistry.OnClientCreated seeds it the same way.
        AccessTools.Field(typeof(MapEventSide), nameof(MapEventSide._battleParties))
            .SetValue(mapEventSide, new MBList<MapEventParty>());
        mapEventSide.MissionSide = side;
        return mapEventSide;
    }
}
