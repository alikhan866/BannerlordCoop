using GameInterface.Services.MapEventParties;
using System;
using TaleWorlds.CampaignSystem.Roster;
using Xunit;

namespace GameInterface.Tests.Services.MapEventParties;

/// <summary>
/// Proves the roster gate never hides a change and never goes silent forever.
/// </summary>
/// <remarks>
/// Two properties carry the whole design, and they pull in opposite directions:
///
///   NOTHING OBSERVABLE IS SUPPRESSED. Every field that reaches the wire must change the hash, or a peer
///   keeps a roster that disagrees with the server's and nothing ever corrects it. That is worse than the
///   traffic problem this exists to solve, because it is silent - troop spawning reads these rosters.
///
///   AN UNCHANGED ROSTER STILL RE-SENDS EVENTUALLY. Dedup alone lets a party that never changes go quiet
///   forever, so a peer that joined afterwards, or missed the last snapshot, would stay wrong indefinitely.
///   The keyframe bounds that at a known number rather than trusting delivery.
///
/// The gate exists because vanilla re-broadcasts every party's FULL roster once per simulation round whether
/// or not it changed - measured at 600 packets / 3.2 MB in ten seconds, which queued 5.3 MB into an 8 MB
/// send buffer and took a 1 ms peer to 80 ms.
/// </remarks>
public class RosterBroadcastGateTests
{
    private static readonly DateTime T0 = new DateTime(2026, 8, 30, 19, 0, 0, DateTimeKind.Utc);

    private static FlattenedTroop Troop(
        string id = "CharacterObject_1", bool isHero = false, int seed = 7,
        RosterTroopState state = RosterTroopState.Active, int xp = 0, int xpGained = 0)
        => new FlattenedTroop(id, isHero, seed, state, xp, xpGained);

    // ---- the decision ------------------------------------------------------

    [Fact]
    public void A_roster_never_sent_before_is_always_sent()
    {
        Assert.True(RosterBroadcastGate.ShouldSend(
            hasPrevious: false, previousHash: 0, previousSentUtc: default, hash: 123, nowUtc: T0));
    }

    [Fact]
    public void A_changed_roster_is_sent_immediately()
    {
        Assert.True(RosterBroadcastGate.ShouldSend(
            hasPrevious: true, previousHash: 111, previousSentUtc: T0, hash: 222, nowUtc: T0));
    }

    [Fact]
    public void An_unchanged_roster_is_suppressed_within_the_keyframe_window()
    {
        Assert.False(RosterBroadcastGate.ShouldSend(
            hasPrevious: true, previousHash: 111, previousSentUtc: T0,
            hash: 111, nowUtc: T0.AddSeconds(RosterBroadcastGate.KeyframeSeconds - 0.01)));
    }

    /// <summary>The property that stops a silent party from staying wrong forever.</summary>
    [Fact]
    public void An_unchanged_roster_is_re_sent_once_the_keyframe_elapses()
    {
        Assert.True(RosterBroadcastGate.ShouldSend(
            hasPrevious: true, previousHash: 111, previousSentUtc: T0,
            hash: 111, nowUtc: T0.AddSeconds(RosterBroadcastGate.KeyframeSeconds)));
    }

    [Fact]
    public void A_clock_that_steps_backwards_cannot_hold_a_roster_off_the_wire()
    {
        Assert.True(RosterBroadcastGate.ShouldSend(
            hasPrevious: true, previousHash: 111, previousSentUtc: T0,
            hash: 111, nowUtc: T0.AddSeconds(-30)));
    }

    // ---- the hash ---------------------------------------------------------

    [Fact]
    public void An_identical_roster_hashes_identically()
    {
        var a = new[] { Troop(), Troop("CharacterObject_2", seed: 9) };
        var b = new[] { Troop(), Troop("CharacterObject_2", seed: 9) };
        Assert.Equal(RosterBroadcastGate.HashRoster(a), RosterBroadcastGate.HashRoster(b));
    }

    /// <summary>Every serialised member must move the hash, or that change would be suppressed.</summary>
    [Theory]
    [InlineData("id")]
    [InlineData("hero")]
    [InlineData("seed")]
    [InlineData("state")]
    [InlineData("xp")]
    [InlineData("xpGained")]
    public void Changing_any_wire_field_changes_the_hash(string field)
    {
        var baseline = new[] { Troop() };
        var changed = field switch
        {
            "id"       => new[] { Troop(id: "CharacterObject_OTHER") },
            "hero"     => new[] { Troop(isHero: true) },
            "seed"     => new[] { Troop(seed: 8) },
            "state"    => new[] { Troop(state: RosterTroopState.WoundedInThisBattle) },
            "xp"       => new[] { Troop(xp: 1) },
            _          => new[] { Troop(xpGained: 1) },
        };

        Assert.NotEqual(RosterBroadcastGate.HashRoster(baseline), RosterBroadcastGate.HashRoster(changed));
    }

    /// <summary>A reorder IS a different snapshot to the receiver, which replaces the roster wholesale.</summary>
    [Fact]
    public void Reordering_the_roster_changes_the_hash()
    {
        var a = new[] { Troop("A"), Troop("B") };
        var b = new[] { Troop("B"), Troop("A") };
        Assert.NotEqual(RosterBroadcastGate.HashRoster(a), RosterBroadcastGate.HashRoster(b));
    }

    [Fact]
    public void A_roster_losing_a_troop_changes_the_hash()
    {
        var before = new[] { Troop("A"), Troop("B") };
        var after = new[] { Troop("A") };
        Assert.NotEqual(RosterBroadcastGate.HashRoster(before), RosterBroadcastGate.HashRoster(after));
    }

    [Fact]
    public void A_null_or_empty_roster_does_not_throw()
    {
        Assert.Equal(RosterBroadcastGate.HashRoster(null), RosterBroadcastGate.HashRoster(null));
        RosterBroadcastGate.HashRoster(new FlattenedTroop[0]);
    }

    // ---- end to end through the gate --------------------------------------

    [Fact]
    public void The_gate_sends_first_suppresses_repeats_and_sends_again_on_change()
    {
        var gate = new RosterBroadcastGate();
        var roster = new[] { Troop() };

        Assert.True(gate.ShouldBroadcast("party_1", roster, T0));                        // first sighting
        Assert.False(gate.ShouldBroadcast("party_1", roster, T0.AddSeconds(1)));         // identical
        Assert.False(gate.ShouldBroadcast("party_1", roster, T0.AddSeconds(2)));         // still identical

        var casualty = new[] { Troop(state: RosterTroopState.Killed) };
        Assert.True(gate.ShouldBroadcast("party_1", casualty, T0.AddSeconds(3)));        // changed

        Assert.Equal(2, gate.Sent);
        Assert.Equal(2, gate.Suppressed);
    }

    /// <summary>Parties are tracked independently; one party's traffic must not mask another's.</summary>
    [Fact]
    public void Two_parties_are_gated_independently()
    {
        var gate = new RosterBroadcastGate();
        var roster = new[] { Troop() };

        Assert.True(gate.ShouldBroadcast("party_1", roster, T0));
        Assert.True(gate.ShouldBroadcast("party_2", roster, T0));
        Assert.False(gate.ShouldBroadcast("party_1", roster, T0.AddSeconds(1)));
        Assert.False(gate.ShouldBroadcast("party_2", roster, T0.AddSeconds(1)));
    }

    // ---- the forced push (a player joining a battle) ----------------------

    /// <summary>
    /// The property that stops a joining player being starved of rosters.
    /// </summary>
    /// <remarks>
    /// The gate records what was last SENT, not what each peer RECEIVED. A roster broadcast to everyone else
    /// a moment ago is byte-identical to the one a newcomer needs, so plain gating would skip it and the
    /// joiner would have nothing to spawn. Forcing is how BattleHandler expresses "this one has to go out".
    /// </remarks>
    [Fact]
    public void A_forced_broadcast_is_sent_even_when_identical_and_recent()
    {
        var gate = new RosterBroadcastGate();
        var roster = new[] { Troop() };

        Assert.True(gate.ShouldBroadcast("party_1", roster, T0));
        Assert.False(gate.ShouldBroadcast("party_1", roster, T0.AddSeconds(1)));
        Assert.True(gate.ShouldBroadcast("party_1", roster, T0.AddSeconds(2), force: true));
    }

    /// <summary>
    /// Forcing must RECORD, not bypass - otherwise the AI joins that follow a player join have nothing to
    /// suppress against and the O(n^2) storm returns in full.
    /// </summary>
    [Fact]
    public void A_forced_broadcast_still_primes_the_gate_for_the_joins_that_follow()
    {
        var gate = new RosterBroadcastGate();
        var roster = new[] { Troop() };

        Assert.True(gate.ShouldBroadcast("party_1", roster, T0, force: true));
        Assert.False(gate.ShouldBroadcast("party_1", roster, T0.AddSeconds(1)));
        Assert.False(gate.ShouldBroadcast("party_1", roster, T0.AddSeconds(2)));
    }

    /// <summary>Forcing does not blind the gate to a later genuine change.</summary>
    [Fact]
    public void A_change_after_a_forced_broadcast_is_still_sent()
    {
        var gate = new RosterBroadcastGate();
        Assert.True(gate.ShouldBroadcast("party_1", new[] { Troop() }, T0, force: true));
        Assert.True(gate.ShouldBroadcast(
            "party_1", new[] { Troop(state: RosterTroopState.Killed) }, T0.AddSeconds(1)));
    }

    /// <summary>
    /// The shape of the burst this exists to remove: one battle assembling out of many parties.
    /// </summary>
    /// <remarks>
    /// The producer hands the handler the FULL involved-party list every time any party joins, so party n
    /// triggers n roster sends - 1+2+...+n. Measured live at 43 parties: 910 packets, 4.7 MB in ten seconds.
    /// Gated, each party sends once and every repeat is suppressed.
    /// </remarks>
    [Fact]
    public void An_assembling_battle_sends_each_party_once_rather_than_quadratically()
    {
        var gate = new RosterBroadcastGate();
        var roster = new[] { Troop() };
        const int parties = 43;

        var now = T0;
        for (int joining = 1; joining <= parties; joining++)
            for (int present = 1; present <= joining; present++)
                gate.ShouldBroadcast($"party_{present}", roster, now);

        int ungated = parties * (parties + 1) / 2;   // 946
        Assert.Equal(ungated, gate.Sent + gate.Suppressed);
        Assert.Equal(parties, gate.Sent);
        Assert.Equal(ungated - parties, gate.Suppressed);
    }

    [Fact]
    public void An_unidentifiable_party_is_always_sent_rather_than_dropped()
    {
        var gate = new RosterBroadcastGate();
        Assert.True(gate.ShouldBroadcast(null, new[] { Troop() }, T0));
        Assert.True(gate.ShouldBroadcast("", new[] { Troop() }, T0));
    }
}
