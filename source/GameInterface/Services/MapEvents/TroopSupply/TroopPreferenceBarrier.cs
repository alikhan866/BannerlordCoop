using System;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.MapEvents.TroopSupply;

/// <summary>
/// Holds one battle at the starting line until every player in it has chosen how their troops deploy.
/// </summary>
/// <remarks>
/// The barrier sits on the SERVER, at the point where it would otherwise broadcast the mission start - after the
/// reserves are prepared and before <c>SendMissionStart</c>. That position is the whole reason this is safe: at
/// that moment no mission exists on any client and no battle traffic is flowing, so holding everyone costs
/// nothing and lets nobody run ahead.
///
/// An earlier attempt asked the question on ONE client while the battle had already begun for the others. That
/// client's mission stayed closed for as long as the player took to read the dialog, the battle ran on without
/// it, and it came back to hundreds of queued deaths and a mission that could no longer initialise -
/// "Number of teams is not 0", thrown every tick forever. Holding everyone before anything starts is a
/// different thing entirely from holding one player after everything has.
///
/// Only ACKNOWLEDGEMENTS are collected, never the choices themselves. Each player's preference is read from
/// their own <see cref="LocalTroopDeploymentPreference"/> when their client allocates troops, so there is no
/// preference state to replicate and no way for the server's idea of a choice to drift from the client's.
/// </remarks>
internal sealed class TroopPreferenceBarrier
{
    private readonly HashSet<string> expected;
    private readonly HashSet<string> answered = new HashSet<string>(StringComparer.Ordinal);

    public TroopPreferenceBarrier(string mapEventId, IEnumerable<string> participantHeroIds, DateTime startedAtUtc)
    {
        MapEventId = mapEventId;
        StartedAtUtc = startedAtUtc;
        expected = new HashSet<string>(
            (participantHeroIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)),
            StringComparer.Ordinal);
    }

    public string MapEventId { get; }

    public DateTime StartedAtUtc { get; }

    /// <summary>Who the battle is still waiting on, in a stable order so the waiting list does not jitter.</summary>
    public IReadOnlyList<string> Outstanding =>
        expected.Where(id => !answered.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();

    public IReadOnlyList<string> Answered =>
        answered.OrderBy(id => id, StringComparer.Ordinal).ToList();

    /// <summary>True once nobody is left to answer - including a battle nobody had to be asked about.</summary>
    public bool IsComplete => expected.Count == 0 || answered.Count >= expected.Count;

    /// <summary>
    /// Records one player's answer.
    /// </summary>
    /// <returns>True if this answer completed the barrier.</returns>
    /// <remarks>
    /// An answer from someone not in this battle is ignored rather than counted: counting it would let a
    /// stranger's message start a battle the actual participants had not answered for.
    /// </remarks>
    public bool Record(string heroId)
    {
        if (string.IsNullOrEmpty(heroId)) return false;
        if (!expected.Contains(heroId)) return false;

        answered.Add(heroId);
        return IsComplete;
    }

    /// <summary>
    /// Drops a player who is no longer waitable - disconnected, or left the battle.
    /// </summary>
    /// <returns>True if their departure completed the barrier.</returns>
    /// <remarks>
    /// Without this a player who closes their game holds everyone else at the starting line until the timeout,
    /// every time.
    /// </remarks>
    public bool Forget(string heroId)
    {
        if (string.IsNullOrEmpty(heroId)) return false;
        if (!expected.Remove(heroId)) return false;

        answered.Remove(heroId);
        return IsComplete;
    }

    /// <summary>
    /// Whether the battle should start regardless, because waiting longer is worse than a stale preference.
    /// </summary>
    /// <remarks>
    /// A battle that never starts is a far worse failure than one that starts on whatever each player last
    /// chose - and the preference is client-local and always has a value, so a timeout is never invalid, only
    /// unasked. Headless clients cannot show a dialog at all and acknowledge immediately, so this is for real
    /// players who alt-tabbed, not for the test rig.
    /// </remarks>
    public bool HasExpired(DateTime nowUtc, TimeSpan timeout) => nowUtc - StartedAtUtc >= timeout;
}
