using System;

namespace Missions.Battles;

/// <summary>
/// How long the engine may wait between reinforcement waves in a co-op battle.
/// </summary>
/// <remarks>
/// WHY CO-OP NEEDS ITS OWN ANSWER.
///
/// Singleplayer can afford a long interval because its field does not empty between waves: the engine spawns
/// roughly what it asked for, so a side is still near its allocation when the timer next fires. A co-op side
/// is not, and the difference is structural rather than a matter of taste.
///
/// Every wave here is capped to the room the field actually has (<c>CoopTroopSupplier.CapWaveToQuota</c>),
/// because the engine's own count of a side is <c>spawned - removed</c> PER SUPPLIER and cannot see the agents
/// a peer replicated. So the engine asks for a deficit far larger than the room, is served the room, and then
/// starts its interval believing it topped the side up. Whatever dies before the timer next fires is replaced
/// by nothing at all.
///
/// Measured live on the attacker side of a siege at Rovalt: waves at 16:24:48, 16:27:19, 16:31:35, 16:35:56,
/// 16:38:08 and 16:41:48 - two to four minutes apart - with the field decaying 226 -> 136 -> 75 between two of
/// them while 1,205 men sat unspawned in reserve. Not a shortage of troops; a shortage of opportunities to
/// send them.
///
/// Separated from <c>CoopBattleMissionSpawnHandler</c> so it can be proven rather than sampled: that class
/// derives from <c>SandBoxMissionSpawnHandler</c>, which drags in an assembly the unit tests do not reference,
/// and the question here is really about one number.
/// </remarks>
internal static class CoopReinforcementPacing
{
    /// <summary>Longest gap a co-op battle may leave between reinforcement waves.</summary>
    /// <remarks>
    /// Long enough that men do not trickle in one at a time, and far short of the multi-minute gaps measured
    /// above.
    ///
    /// ponytail: one constant rather than a config knob, because nothing yet suggests the right number varies
    /// by battle type. Promote it to mod-config if a siege and a field battle turn out to want different
    /// answers.
    /// </remarks>
    internal const float MaximumInterval = 20f;

    /// <summary>
    /// The interval to use, given whatever the battle type's own settings asked for.
    /// </summary>
    /// <remarks>
    /// Only ever SHORTENS. A battle type already asking for something brisker keeps its own value - raising it
    /// would make reinforcements arrive less often in exactly the battles somebody had tuned, and it would do
    /// so silently, since nothing about a thin field says which side of a clamp it came from.
    ///
    /// A non-positive interval means the battle type asked for no timing of its own, and is passed through
    /// untouched rather than clamped into a cadence it never requested.
    /// </remarks>
    internal static float Interval(float vanillaInterval)
        => vanillaInterval <= 0f ? vanillaInterval : Math.Min(vanillaInterval, MaximumInterval);
}
