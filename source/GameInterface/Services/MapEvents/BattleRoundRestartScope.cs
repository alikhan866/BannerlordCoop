namespace GameInterface.Services.MapEvents;

/// <summary>
/// Marks the instant in which a round restart clears the field, so removing those agents is not mistaken for
/// them leaving the battle.
/// </summary>
/// <remarks>
/// A restart takes everyone off the field and spawns them again. The engine has no "remove this agent, it did
/// not go anywhere" call - the closest is <c>Agent.FadeOut</c>, which is a WITHDRAWAL and reports
/// <see cref="TaleWorlds.Core.AgentState.Routed"/>. That is exactly right for the one caller it was written for
/// (a player withdrawing their party mid-battle) and exactly wrong here: every man on the field is recorded as
/// having fled. Seen live as a scoreboard listing 288 attackers and 197 defenders retreated - about half of
/// each party - immediately after a restart cleared 1,338 agents.
///
/// Two consumers have to be held off for that instant:
///   <c>BattleObserverMissionLogic.OnAgentRemoved</c>, which files the rout on the scoreboard and in the
///   campaign's battle result, and <c>BattleAgentRoutedPatch</c>, which would broadcast a rout for every agent
///   to every peer.
///
/// A flag rather than a parameter because the removal is several frames of engine code away from the code that
/// knows why it is happening, and because both consumers are patches on engine methods we do not call.
/// </remarks>
internal static class BattleRoundRestartScope
{
    [System.ThreadStatic]
    private static int depth;

    /// <summary>True while a round restart is clearing the field on this thread.</summary>
    public static bool IsClearingField => depth > 0;

    /// <summary>
    /// Thread-static and counted rather than a plain bool: agent removal runs on the mission thread, and a
    /// count means a nested or re-entered clear cannot leave the flag stuck on - which would silently stop
    /// recording real routs for the rest of the battle.
    /// </summary>
    public static Scope Enter() => new Scope();

    public readonly struct Scope : System.IDisposable
    {
        public Scope() => depth++;

        public void Dispose() => depth--;
    }
}
