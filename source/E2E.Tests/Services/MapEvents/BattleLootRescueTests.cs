using GameInterface.Services.MapEvents.Patches;
using TaleWorlds.CampaignSystem.Encounters;
using Xunit;

namespace E2E.Tests.Services.MapEvents;

/// <summary>
/// Not losing a player's loot when the encounter it was staged into never gets to spend it.
/// </summary>
/// <remarks>
/// Staged loot is granted when the encounter reaches <c>LootInventory</c>. Concluding a coop battle finalizes
/// the map event and closes every involved player's encounter, which can happen first - and the loot dies with
/// it, indistinguishably from a battle that awarded nothing.
///
/// Measured on one battle: one player had 73 item stacks, 19 recovered members and 43 prisoners staged and
/// received none of it, while the other received all of his - purely because his exit happened to run through a
/// retreat prompt that re-entered the encounter flow. The loot was correct on both machines; only one of them
/// ever spent it. Every earlier fix looked upstream of this and found nothing wrong, because nothing upstream
/// WAS wrong.
/// </remarks>
public class BattleLootRescueTests
{
    [Fact]
    public void AnEncounterThatNeverOfferedTheScreen_HasItsLootRescued()
    {
        // The reported case: closed at CaptureHeroes, which is why prisoners arrived and items did not.
        Assert.False(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.CaptureHeroes));
        Assert.False(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.FreeHeroes));
        Assert.False(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.LootParty));
    }

    [Fact]
    public void AnEncounterThatOfferedTheScreen_IsLeftAlone()
    {
        // Loot left behind at the screen is a CHOICE - vanilla discards it. Re-awarding it would hand the
        // player things they explicitly declined, which is a different bug rather than a fix for this one.
        Assert.True(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.LootInventory));
        Assert.True(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.LootShips));
        Assert.True(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.End));
    }

    [Fact]
    public void AnEarlyStateIsNeverMistakenForHavingOffered()
    {
        // The states before the loot sequence must all count as "not offered", or a battle torn down early
        // would silently keep losing its loot exactly as before.
        Assert.False(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.Begin));
        Assert.False(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.Wait));
        Assert.False(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.PrepareResults));
        Assert.False(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.ApplyResults));
        Assert.False(BattleLootRescuePatch.ReachedTheLootScreen(PlayerEncounterState.PlayerVictory));
    }

    [Fact]
    public void NoEncounterAtAll_CountsAsNeverOffered()
    {
        // The encounter can already be gone by the time we look. That is the strongest case for rescuing, not
        // a reason to skip it.
        Assert.False(BattleLootRescuePatch.ReachedTheLootScreen(null));
    }
}
