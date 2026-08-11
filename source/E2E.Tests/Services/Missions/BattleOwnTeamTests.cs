using Missions.Battles;
using TaleWorlds.Core;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Which team a client's OWN troops go on, so it can actually command them.
/// </summary>
/// <remarks>
/// "The side's main team" and "the team I command" are the same object on the client that STARTED the battle,
/// and different on a client that JOINED one - a joiner is not the side's leader, so vanilla gives it a
/// subordinate team while the main team belongs to whoever leads. Own troops were routed to the main team,
/// which is therefore correct on the host and silently wrong for everyone else.
///
/// Reported as: the joining player could walk and fight, but his order menu was empty. Measured at one instant
/// across both clients - the host's player team held 131 agents with none on its ally team, while the joiner's
/// player team held a single agent, his own hero, against 216 on the ally team. His army was on the field the
/// whole battle and none of it was his to order.
/// </remarks>
public class BattleOwnTeamTests
{
    [Fact]
    public void AJoinersOwnTroopsGoToTheTeamHeCommands()
    {
        // The reported case: he has a player team on this side, so his own men belong on it - whatever the
        // side's main team happens to be.
        Assert.True(BattleTeams.ShouldUsePlayerTeam(
            hasPlayerTeam: true, playerTeamSide: BattleSideEnum.Defender, troopSide: BattleSideEnum.Defender));
    }

    [Fact]
    public void TroopsOnTheOtherSideAreNeverPulledOntoTheTeamWeCommand()
    {
        // Two players can be on opposite sides of the same battle. A troop must not be dragged onto the team
        // this client commands merely because that is the team it commands - it would be fighting for the
        // wrong army.
        Assert.False(BattleTeams.ShouldUsePlayerTeam(
            hasPlayerTeam: true, playerTeamSide: BattleSideEnum.Defender, troopSide: BattleSideEnum.Attacker));
        Assert.False(BattleTeams.ShouldUsePlayerTeam(
            hasPlayerTeam: true, playerTeamSide: BattleSideEnum.Attacker, troopSide: BattleSideEnum.Defender));
    }

    [Fact]
    public void WithNoPlayerTeamYet_TheSidesMainTeamIsUsed()
    {
        // Teams are created during mission setup and a spawn can land before that. Falling back to the main
        // team keeps the old behaviour rather than dropping the troop.
        Assert.False(BattleTeams.ShouldUsePlayerTeam(
            hasPlayerTeam: false, playerTeamSide: BattleSideEnum.None, troopSide: BattleSideEnum.Defender));
    }

    [Fact]
    public void AnUnknownTroopSideNeverClaimsThePlayerTeam()
    {
        // BattleSideEnum.None reaches Resolve as "the player's enemy team". It must not match a player team
        // that also happens to read None during setup, or a stray record would land under our command.
        Assert.False(BattleTeams.ShouldUsePlayerTeam(
            hasPlayerTeam: true, playerTeamSide: BattleSideEnum.Defender, troopSide: BattleSideEnum.None));
    }

    [Fact]
    public void TheHostIsUnaffected()
    {
        // On the client that started the battle the two teams are the same object, so this decision changes
        // nothing there - which is exactly why the bug survived so long. Asserted so a future change cannot
        // quietly alter the host's behaviour while "fixing" the joiner's.
        Assert.True(BattleTeams.ShouldUsePlayerTeam(
            hasPlayerTeam: true, playerTeamSide: BattleSideEnum.Attacker, troopSide: BattleSideEnum.Attacker));
    }
}
