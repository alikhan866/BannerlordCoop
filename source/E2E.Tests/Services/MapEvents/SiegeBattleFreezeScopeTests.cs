using Missions.Battles;
using Xunit;

namespace E2E.Tests.Services.MapEvents;

/// <summary>
/// Which settlement's siege is held while a battle over it is being fought.
/// </summary>
/// <remarks>
/// The freeze used to be found from where the PLAYER'S PARTY was standing: besieging something, or inside
/// something. Sallying out makes both false at the same moment - you leave the walls, so CurrentSettlement is
/// null, and you are the defender, so BesiegedSettlement is null.
///
/// So nothing was frozen, and the siege advanced underneath the very battle meant to decide it. A player
/// defending Phycaon sallied out against the besiegers, and while he was fighting them those same besiegers
/// took the town - an army cannot both be losing an open battle and storming the walls behind it. Zero
/// "Sieges held" lines were logged for the whole session.
///
/// The battle still knows what it is about after the parties have moved, so the settlement is taken from the
/// map event instead.
/// </remarks>
public class SiegeBattleFreezeScopeTests
{
    [Fact]
    public void ABattleWithNoSettlementFreezesNothing()
    {
        // An ordinary field battle must not hold anybody's siege.
        Assert.Null(BattleHostHandler.BesiegedSettlementOf(null));
    }

    // The settlement-bearing cases need a live MapEvent with a Settlement carrying a SiegeEvent, which cannot
    // be constructed outside the engine. What IS asserted here is the rule that made the bug possible: the
    // lookup must not depend on party position at all. BesiegedSettlementOf takes only the map event, so a
    // party that has moved - sallied out, ridden from its camp - cannot change the answer. That is the whole
    // of the fix, and the signature enforces it.
    [Fact]
    public void TheLookupDependsOnlyOnTheBattle()
    {
        var parameters = typeof(BattleHostHandler)
            .GetMethod("BesiegedSettlementOf", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.GetParameters();

        Assert.NotNull(parameters);
        Assert.Single(parameters);
        Assert.Equal(typeof(TaleWorlds.CampaignSystem.MapEvents.MapEvent), parameters[0].ParameterType);
    }
}
