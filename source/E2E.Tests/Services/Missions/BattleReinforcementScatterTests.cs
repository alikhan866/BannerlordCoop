using System.Collections.Generic;
using Missions.Battles;
using TaleWorlds.Library;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// Spreading reinforcements over ground instead of stacking them on one point.
/// </summary>
/// <remarks>
/// The fielder asks where the friendly line is and gets back ONE position - the centroid of the side - and
/// every troop it spawned was given exactly that position. Men then materialised inside one another at a single
/// point, and agents wedged into each other cannot path out: a packed, motionless crowd in the middle of the
/// line. Reported as troops "blobbed, stuck, and not moving", and it compounds with every reinforcement that
/// arrives.
/// </remarks>
public class BattleReinforcementScatterTests
{
    [Fact]
    public void TheFirstManStandsExactlyWhereTheLineIs()
    {
        // Index 0 has to be the centroid itself, or a lone reinforcement would arrive offset from the line it
        // was measured against.
        var first = ReinforcementFielder.ScatterOffset(0);

        Assert.Equal(0f, first.x, 4);
        Assert.Equal(0f, first.y, 4);
    }

    [Fact]
    public void NoTwoMenAreGivenTheSameGround()
    {
        // The whole defect in one assertion: a hundred reinforcements must be a hundred positions, not one
        // position a hundred times.
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < 100; i++)
        {
            var offset = ReinforcementFielder.ScatterOffset(i);
            // Rounded to 10cm — two men closer together than that are standing in each other for this purpose.
            seen.Add(((int)(offset.x * 10f), (int)(offset.y * 10f)));
        }

        Assert.Equal(100, seen.Count);
    }

    [Fact]
    public void SuccessiveMenAreNotPlacedNextToEachOther()
    {
        // Filling a spiral outward in order would land each man beside the last, which re-creates the crush at
        // the leading edge even though the disc as a whole is spread. The golden angle is what avoids it, so
        // the property is asserted rather than assumed.
        for (int i = 1; i < 60; i++)
        {
            var previous = ReinforcementFielder.ScatterOffset(i - 1);
            var current = ReinforcementFielder.ScatterOffset(i);

            Assert.True((current - previous).Length > 0.9f,
                $"reinforcements {i - 1} and {i} were placed {(current - previous).Length:F2}m apart");
        }
    }

    [Fact]
    public void TheCrowdGrowsOutward_SoDensityStaysConstant()
    {
        // Radius as sqrt(n) is what keeps men per square metre flat as the batch grows: area scales with the
        // count. A linear radius would leave a big batch strung out in a thin ring, a constant one would put
        // them all back on top of each other.
        var tenth = ReinforcementFielder.ScatterOffset(10).Length;
        var fortieth = ReinforcementFielder.ScatterOffset(40).Length;
        var ninetieth = ReinforcementFielder.ScatterOffset(90).Length;

        Assert.True(fortieth > tenth && ninetieth > fortieth, "the patch must grow with the batch");

        // Four times the men, twice the radius.
        Assert.Equal(2.0, fortieth / tenth, 1);
        Assert.Equal(3.0, ninetieth / tenth, 1);
    }

    [Fact]
    public void EvenALargeBatchStaysWithinReachOfItsOwnLine()
    {
        // The spread must not turn into a scattering: a reinforcement that lands a hundred metres from the line
        // is no longer joining it. Two hundred men fit inside about sixteen metres.
        var far = ReinforcementFielder.ScatterOffset(200);

        Assert.True(far.Length < 20f, $"a batch of 200 reached {far.Length:F1}m from the line");
    }

    [Fact]
    public void ThePlacementIsReproducible()
    {
        // Deliberately not random. The same index gives the same ground every time, so a spawn batch cannot
        // clump by luck and a failure can be reproduced from the index alone.
        Assert.Equal(ReinforcementFielder.ScatterOffset(37).x, ReinforcementFielder.ScatterOffset(37).x, 5);
        Assert.Equal(ReinforcementFielder.ScatterOffset(37).y, ReinforcementFielder.ScatterOffset(37).y, 5);
    }
}
