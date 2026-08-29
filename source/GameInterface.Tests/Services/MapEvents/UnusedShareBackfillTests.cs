using GameInterface.Services.MapEvents.TroopSupply;
using System;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

/// <summary>
/// Proves the back-fill only unlocks after the side has stood short CONTINUOUSLY.
/// </summary>
/// <remarks>
/// The private field quota exists so two owners cannot race for the same slots, and the back-fill deliberately
/// weakens it. What keeps that safe is the grace being a measure of "the other owner has stopped" rather than
/// of "this battle has lasted" - so the interesting case is not the timer expiring, it is the timer RESETTING
/// when the side recovers. A version that accumulated scattered short moments would unlock while the co-owner
/// was supplying perfectly well, and both owners would then fill the same gap.
///
/// Written against the live fault it fixes (siege of Rovalt, 2026-08-29): the defender side stood short for
/// twenty minutes with 572 men in reserve because the share belonging to a co-owner that fielded nothing was
/// unreachable by anyone.
/// </remarks>
public class UnusedShareBackfillTests
{
    private static readonly DateTime T0 = new DateTime(2026, 8, 29, 14, 34, 0, DateTimeKind.Utc);

    private static double Grace => CoopTroopSupplier.UnusedShareGraceSeconds;

    [Fact]
    public void A_side_at_strength_never_backfills_and_holds_no_mark()
    {
        DateTime? mark = T0;

        Assert.Equal(0, CoopTroopSupplier.BackfillGrant(0, T0, ref mark));
        Assert.Null(mark);
    }

    [Fact]
    public void The_first_short_reading_only_starts_the_clock()
    {
        DateTime? mark = null;

        Assert.Equal(0, CoopTroopSupplier.BackfillGrant(105, T0, ref mark));
        Assert.Equal(T0, mark);
    }

    [Fact]
    public void Waiting_less_than_the_grace_grants_nothing()
    {
        DateTime? mark = null;
        CoopTroopSupplier.BackfillGrant(105, T0, ref mark);

        Assert.Equal(0, CoopTroopSupplier.BackfillGrant(105, T0.AddSeconds(Grace - 0.01), ref mark));
    }

    [Fact]
    public void The_whole_unused_room_is_granted_once_the_grace_has_passed()
    {
        DateTime? mark = null;
        CoopTroopSupplier.BackfillGrant(105, T0, ref mark);

        Assert.Equal(105, CoopTroopSupplier.BackfillGrant(105, T0.AddSeconds(Grace), ref mark));
    }

    /// <summary>The property the safety of this whole change rests on.</summary>
    [Fact]
    public void A_side_that_recovers_restarts_the_wait_from_scratch()
    {
        DateTime? mark = null;

        // Short for almost the full grace...
        CoopTroopSupplier.BackfillGrant(105, T0, ref mark);
        Assert.Equal(0, CoopTroopSupplier.BackfillGrant(105, T0.AddSeconds(Grace - 1), ref mark));

        // ...then the co-owner supplies and the side comes back to strength.
        Assert.Equal(0, CoopTroopSupplier.BackfillGrant(0, T0.AddSeconds(Grace), ref mark));
        Assert.Null(mark);

        // Going short again must wait the FULL grace, not the one second it had left.
        Assert.Equal(0, CoopTroopSupplier.BackfillGrant(105, T0.AddSeconds(Grace + 1), ref mark));
        Assert.Equal(0, CoopTroopSupplier.BackfillGrant(105, T0.AddSeconds(2 * Grace), ref mark));
        Assert.Equal(105, CoopTroopSupplier.BackfillGrant(105, T0.AddSeconds(2 * Grace + 1), ref mark));
    }

    [Fact]
    public void A_clock_that_steps_backwards_re_marks_instead_of_granting()
    {
        DateTime? mark = null;
        CoopTroopSupplier.BackfillGrant(105, T0, ref mark);

        var stepped = T0.AddSeconds(-5);
        Assert.Equal(0, CoopTroopSupplier.BackfillGrant(105, stepped, ref mark));
        Assert.Equal(stepped, mark);
    }

    [Fact]
    public void The_grant_never_exceeds_the_room_the_side_actually_has()
    {
        foreach (int room in new[] { 1, 7, 105, 157, 625 })
        {
            DateTime? mark = null;
            CoopTroopSupplier.BackfillGrant(room, T0, ref mark);

            Assert.Equal(room, CoopTroopSupplier.BackfillGrant(room, T0.AddSeconds(Grace), ref mark));
        }
    }
}
