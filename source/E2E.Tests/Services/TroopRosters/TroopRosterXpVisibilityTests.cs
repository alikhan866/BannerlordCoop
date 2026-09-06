using Common.Util;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using Xunit;

namespace E2E.Tests.Services.TroopRosters;

/// <summary>
/// A troop roster hands out its rows from a list it rebuilds only when its version changes, and writing XP does
/// not change that version. Anything reading the rows therefore keeps showing the XP the troops had before.
/// </summary>
/// <remarks>
/// Measured on 6 Sep 2026 in a live battle: donated loot put 40,170 XP into the winner's party, the party read
/// back 40,170 through <c>GetElementCopyAtIndex</c> on the server and on the owning client, and the row view of
/// the same roster still read 0 on all three. Between two battles nothing else touches those rows, so a player
/// who donates loot and opens their party screen sees the XP they were given not appear at all - which is what
/// "even if i discard all of them i dont see my units gain any experience" describes.
/// </remarks>
public class TroopRosterXpVisibilityTests
{
    private static (TroopRoster roster, CharacterObject troop) Roster()
    {
        var troop = ObjectHelper.SkipConstructor<CharacterObject>();
        var roster = TroopRoster.CreateDummyTroopRoster();
        roster.AddToCounts(troop, 10);
        return (roster, troop);
    }

    [Fact]
    public void WritingXp_DoesNotRefreshTheRowsByItself()
    {
        var (roster, troop) = Roster();
        // Reading once is what fills the cached rows; in the game the party screen or a tooltip has already done it.
        Assert.Equal(0, roster.GetTroopRoster()[0].Xp);

        roster.SetElementXp(0, 500);

        // The roster itself knows: this is the read every coop path uses to check its own work.
        Assert.Equal(500, roster.GetElementCopyAtIndex(0).Xp);
        // The rows do not, until something bumps the version.
        Assert.Equal(0, roster.GetTroopRoster()[0].Xp);
    }

    [Fact]
    public void BumpingTheVersion_MakesTheGivenXpVisible()
    {
        var (roster, troop) = Roster();
        Assert.Equal(0, roster.GetTroopRoster()[0].Xp);

        roster.SetElementXp(0, 500);
        roster.UpdateVersion();

        Assert.Equal(500, roster.GetTroopRoster()[0].Xp);
        Assert.Equal(500, roster.GetElementCopyAtIndex(0).Xp);
    }

    [Fact]
    public void AddingXpThroughTheTroopHelper_HasTheSameGap()
    {
        var (roster, troop) = Roster();
        Assert.Equal(0, roster.GetTroopRoster()[0].Xp);

        roster.AddXpToTroop(troop, 250);

        Assert.Equal(250, roster.GetElementCopyAtIndex(0).Xp);
        Assert.Equal(0, roster.GetTroopRoster()[0].Xp);
        roster.UpdateVersion();
        Assert.Equal(250, roster.GetTroopRoster()[0].Xp);
    }

    [Fact]
    public void ChangingTheCount_RefreshesTheRowsOnItsOwn()
    {
        // Why this never showed up during a battle: casualties change counts every few seconds, and a count
        // change does bump the version, so the XP appears with the next change. After a battle nothing changes.
        var (roster, troop) = Roster();
        Assert.Equal(0, roster.GetTroopRoster()[0].Xp);

        roster.SetElementXp(0, 700);
        roster.AddToCounts(troop, 1);

        Assert.Equal(700, roster.GetTroopRoster()[0].Xp);
    }
}
