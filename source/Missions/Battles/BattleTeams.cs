using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Battles;

/// <summary>Side-to-team resolution against the current mission, shared by the puppet and reinforcement spawn paths.</summary>
public static class BattleTeams
{
    /// <summary>The current mission's main team for a battle side (the player's enemy team when the side is unknown).</summary>
    public static Team Resolve(BattleSideEnum side)
    {
        return side switch
        {
            BattleSideEnum.Attacker => Mission.Current.AttackerTeam,
            BattleSideEnum.Defender => Mission.Current.DefenderTeam,
            _ => Mission.Current.PlayerEnemyTeam
        };
    }

    /// <summary>The team on <paramref name="side"/> that THIS client commands, for troops that are its own.</summary>
    public static Team ResolveOwn(BattleSideEnum side)
        => OwnTeam(side, Mission.Current?.PlayerTeam, Resolve(side));

    /// <summary>
    /// Which team an own troop belongs on: this client's player team when it has one on that side.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Resolve"/> because "the side's main team" and "the team I command" are the same
    /// object on the client that STARTED the battle and different on a client that JOINED one - the joiner is
    /// not the side's leader, so vanilla gives it a subordinate team while the main team belongs to whoever
    /// leads. Using the main team for own troops therefore works perfectly on the host and silently puts every
    /// one of a joiner's men on a team it cannot command.
    ///
    /// That is what a joining player saw: he could walk and fight, but his order menu was empty. At one instant
    /// the host's player team held 131 agents with none on its ally team, while the joiner's player team held a
    /// single agent - his own hero - against 216 on the ally team.
    ///
    /// The side check matters: a player fighting on the OTHER side of this battle must not have their troops
    /// pulled onto a team of the wrong side just because it is the one they command.
    /// </remarks>
    internal static Team OwnTeam(BattleSideEnum side, Team playerTeam, Team mainTeam)
        => ShouldUsePlayerTeam(playerTeam != null, playerTeam?.Side ?? BattleSideEnum.None, side)
            ? playerTeam
            : mainTeam;

    /// <summary>The decision itself, over plain values so it can be asserted without an engine mission.</summary>
    internal static bool ShouldUsePlayerTeam(bool hasPlayerTeam, BattleSideEnum playerTeamSide, BattleSideEnum troopSide)
        => hasPlayerTeam && playerTeamSide == troopSide;
}
