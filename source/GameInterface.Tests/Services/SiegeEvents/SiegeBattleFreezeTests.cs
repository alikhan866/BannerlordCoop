using Common.Util;
using GameInterface.Services.SiegeEvents;
using TaleWorlds.CampaignSystem.Settlements;
using Xunit;

namespace GameInterface.Tests.Services.SiegeEvents;

/// <summary>
/// Holding a siege still while the player who owns it is away fighting for it.
/// </summary>
/// <remarks>
/// Vanilla stops a siege while its besieger is in a fight, and decides that by looking for a MapEvent. In co-op
/// a client can be inside a battle mission the server holds no map event for - a battle restored from a save is
/// the reproducible case - so vanilla's gate sees nothing and the siege bombards straight through the assault.
/// Observed at Phycaon with the server reporting the besieger's MapEvent as null while the client sat in a live
/// mission. This carries the server's mission-membership knowledge to the tick gate instead.
/// </remarks>
public class SiegeBattleFreezeTests
{
    private static Settlement SettlementWithId(string stringId)
    {
        var settlement = ObjectHelper.SkipConstructor<Settlement>();
        settlement.StringId = stringId;
        return settlement;
    }

    public SiegeBattleFreezeTests() => SiegeBattleFreeze.Clear();

    [Fact]
    public void ASiegeWhosePlayerIsFighting_IsHeld()
    {
        var phycaon = SettlementWithId("town_ES6");

        SiegeBattleFreeze.Freeze("town_ES6");

        Assert.True(SiegeBattleFreeze.IsFrozen(phycaon));
    }

    [Fact]
    public void OtherSieges_KeepRunning()
    {
        // The freeze is per siege, not global: one player's assault must not stop every other siege on the map.
        SiegeBattleFreeze.Freeze("town_ES6");

        Assert.False(SiegeBattleFreeze.IsFrozen(SettlementWithId("castle_ES1")));
    }

    [Fact]
    public void OnceTheFightIsOver_TheSiegeResumes()
    {
        SiegeBattleFreeze.Freeze("town_ES6");
        SiegeBattleFreeze.Clear();

        Assert.False(SiegeBattleFreeze.IsFrozen(SettlementWithId("town_ES6")));
    }

    [Fact]
    public void FreezingTheSameSiegeTwice_StillThawsCleanly()
    {
        // Two players besieging the same settlement both freeze it. The set is rebuilt wholesale rather than
        // reference counted precisely so this cannot leave a residue - a siege that never advances again is a
        // far worse failure than a redundant freeze.
        SiegeBattleFreeze.Freeze("town_ES6");
        SiegeBattleFreeze.Freeze("town_ES6");
        SiegeBattleFreeze.Clear();

        Assert.False(SiegeBattleFreeze.IsFrozen(SettlementWithId("town_ES6")));
    }

    [Fact]
    public void AnUnknownSettlement_IsNeverFrozen()
    {
        // The gate runs for every siege on every tick, including ones with no id yet during load. It must
        // answer "not frozen" rather than throw.
        Assert.False(SiegeBattleFreeze.IsFrozen(null));
        Assert.False(SiegeBattleFreeze.IsFrozen(SettlementWithId(null)));
    }
}
