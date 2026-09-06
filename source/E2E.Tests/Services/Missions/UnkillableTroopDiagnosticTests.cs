using Common.Util;
using Missions.Battles;
using TaleWorlds.MountAndBlade;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Counting the damage an agent absorbs, so an unkillable troop can be named.
/// </summary>
/// <remarks>
/// The unkillable-troop reports have never had evidence attached, and the reason is structural: from the
/// attacking client the failure is invisible. Hits are routed to whoever owns the victim, and an owner that
/// receives them and does not apply them looks exactly like success - no warning, no dropped-hit line, because
/// the send itself worked. Every routing failure path logged zero occurrences while a man stood in a melee
/// taking no damage at all.
///
/// The log also identified victims only by troop type, so dozens of agents shared one name and no health
/// sequence could be followed. This tally closes both gaps: damage is attributed per agent index, and an agent
/// that absorbs more than twice its health limit says so once, by name and index, on whichever machine it
/// happens.
///
/// It is a diagnostic, not a fix - the root cause is still unknown, and inventing one would be worse than
/// admitting that.
/// </remarks>
public class UnkillableTroopDiagnosticTests
{
    [Fact]
    public void DamageAccumulatesPerAgentRatherThanPerHit()
    {
        BattleDamageRouter.ResetDamageTally();

        BattleDamageRouter.NoteDamageTaken(agentIndex: 7, 10f);
        BattleDamageRouter.NoteDamageTaken(agentIndex: 7, 15f);
        float total = BattleDamageRouter.NoteDamageTaken(agentIndex: 7, 5f);

        // The whole point: one number that keeps rising across hits, which is what "he would not die" looks
        // like in data. A per-hit log could never show it.
        Assert.Equal(30f, total);
    }

    [Fact]
    public void AReusedIndexStartsAFreshTallyForTheNextAgent()
    {
        // The engine hands a dead man's index to the next spawn. In an 800 v 800 battle capped at 400 (5 Sep 2026)
        // the tally of several successive men added up under one index and 195-259 agents a side were reported
        // unkillable, against 0-1 in battles without reinforcement waves. The identity is the agent object itself.
        BattleDamageRouter.ResetDamageTally();
        var firstMan = new object();
        var secondMan = new object();
        BattleDamageRouter.NoteDamageTaken(agentIndex: 7, firstMan, 150f);
        BattleDamageRouter.NoteDamageTaken(agentIndex: 7, firstMan, 150f);
        float fresh = BattleDamageRouter.NoteDamageTaken(agentIndex: 7, secondMan, 10f);
        Assert.Equal(10f, fresh);
        // And the same man keeps accumulating.
        Assert.Equal(25f, BattleDamageRouter.NoteDamageTaken(agentIndex: 7, secondMan, 15f));
    }

    [Fact]
    public void AgentsAreTalliedSeparately()
    {
        // Sharing a tally across agents would recreate the exact ambiguity this replaces, where dozens of
        // "Imperial Bucellarii" lines could not be told apart.
        BattleDamageRouter.ResetDamageTally();

        BattleDamageRouter.NoteDamageTaken(agentIndex: 1, 50f);
        float other = BattleDamageRouter.NoteDamageTaken(agentIndex: 2, 3f);

        Assert.Equal(3f, other);
    }

    [Fact]
    public void NegativeDamageDoesNotUnwindTheTally()
    {
        // Healing and zeroed blows both reach this path. Letting them subtract would mask the pattern.
        BattleDamageRouter.ResetDamageTally();

        BattleDamageRouter.NoteDamageTaken(agentIndex: 3, 40f);
        float total = BattleDamageRouter.NoteDamageTaken(agentIndex: 3, -100f);

        Assert.Equal(40f, total);
    }

    [Fact]
    public void TheTallyIsClearedBetweenMissions()
    {
        // Agent indices are reused by the next mission, so a carried-over tally would blame a fresh agent for
        // damage taken by whoever stood at that index last battle.
        BattleDamageRouter.NoteDamageTaken(agentIndex: 4, 500f);

        BattleDamageRouter.ResetDamageTally();

        Assert.Equal(12f, BattleDamageRouter.NoteDamageTaken(agentIndex: 4, 12f));
    }

    [Fact]
    public void ANullVictimIsIgnoredRatherThanThrowing()
    {
        // This runs inside the damage path on every routed blow. It must never be the reason a blow fails.
        BattleDamageRouter.ResetDamageTally();

        Assert.Equal(0f, BattleDamageRouter.NoteDamageTaken((Agent)null, 25f));
    }

    [Fact]
    public void AnAgentWhoseEngineStateCannotBeReadIsIgnoredRatherThanThrowing()
    {
        // An agent mid-construction or mid-removal cannot answer for its index or health. The original version
        // of this method read them unguarded and threw - which, sitting on the routed-damage path, would have
        // stopped blows being applied and CAUSED unkillable troops rather than diagnosing them. Caught here
        // rather than in a battle.
        BattleDamageRouter.ResetDamageTally();
        var halfBuilt = ObjectHelper.SkipConstructor<Agent>();

        var exception = Record.Exception(() => BattleDamageRouter.NoteDamageTaken(halfBuilt, 30f));

        Assert.Null(exception);
    }
}
