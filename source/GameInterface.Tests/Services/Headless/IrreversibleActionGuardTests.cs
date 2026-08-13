using GameInterface.Services.Headless;
using System;
using Xunit;

namespace GameInterface.Tests.Services.Headless;

/// <summary>
/// The server-side refusal of acts whose damage outlives the run.
/// </summary>
public class IrreversibleActionGuardTests : IDisposable
{
    public IrreversibleActionGuardTests()
    {
        IrreversibleActionGuard.Arm(false);
        IrreversibleActionGuard.ClearRefusals();
    }

    public void Dispose()
    {
        IrreversibleActionGuard.Arm(false);
        IrreversibleActionGuard.ClearRefusals();
    }

    [Fact]
    public void DisarmedByDefaultSoAHumanDrivenServerIsUnaffected()
    {
        // The guard is for unattended runs. A person hosting their own game must not find diplomacy refused
        // because a rig feature defaulted to on.
        Assert.False(IrreversibleActionGuard.IsArmed);
        Assert.False(IrreversibleActionGuard.TryRefuse("coop.debug.kingdom.declare_war Wang Ki", out _));
    }

    [Fact]
    public void ArmedRefusesADeniedCommandAndSaysWhy()
    {
        IrreversibleActionGuard.Arm(true);

        Assert.True(IrreversibleActionGuard.TryRefuse("coop.debug.kingdom.declare_war Wang Ki", out var reason));
        Assert.Contains("denylist", reason);
    }

    [Fact]
    public void ArmedStillAllowsEverythingElse()
    {
        IrreversibleActionGuard.Arm(true);

        // The point is not a read-only world - observation and ordinary play must keep working, or the rig
        // cannot do anything at all while protected.
        Assert.False(IrreversibleActionGuard.TryRefuse("coop.debug.mobileparty.position Player425", out _));
        Assert.False(IrreversibleActionGuard.TryRefuse("coop.debug.mobileparty.move_offset 3 3", out _));
        Assert.False(IrreversibleActionGuard.TryRefuse("coop.debug.ui.popup_log", out _));
    }

    [Theory]
    [InlineData("coop.debug.kingdom.make_peace Wang Ki")]
    [InlineData("coop.debug.clan.destroy_clan Clan_Player12")]
    [InlineData("coop.debug.romance.marry Kan Zena")]
    [InlineData("coop.debug.settlements.set_ownerclan town_ES1 Clan_Player")]
    [InlineData("coop.debug.town.start_rebellion town_ES1")]
    [InlineData("coop.debug.mobileparty.destroyParty Player425")]
    public void TheDenylistCoversEachIrreversibleFamily(string command)
    {
        Assert.True(IrreversibleActionGuard.IsDenied(command));
    }

    [Fact]
    public void RefusalsAreRecordedRatherThanSilent()
    {
        IrreversibleActionGuard.Arm(true);
        IrreversibleActionGuard.TryRefuse("coop.debug.clan.destroy_clan Clan_Player12", out _);

        // A refusal that leaves no trace becomes "why did the scenario not work", which costs more than the
        // action would have.
        Assert.Contains(IrreversibleActionGuard.RefusalLog, entry => entry.Contains("destroy_clan"));
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.True(IrreversibleActionGuard.IsDenied("COOP.DEBUG.KINGDOM.DECLARE_WAR a b"));
    }
}
