using System.Linq;
using Missions.Battles;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// What a client joining a battle ALREADY UNDERWAY is allowed to put on the field straight away.
/// </summary>
/// <remarks>
/// The engine's initial spawn number is an OPENING wave — it assumes an empty field. A client arriving
/// mid-battle computed one anyway and fielded its whole owned force in a single step: a player joining a
/// fight that already had 392 men on the defender side took it to 532, eight allied parties appearing at
/// once, far past what the battle was sized for.
///
/// A late joiner may field exactly enough to have someone to control — its hero — and the rest arrives
/// through the engine's ordinary reinforcement waves as casualties make room. That keeps the joiner's arrival
/// from being a spike, while never leaving the joining player without an agent.
/// </remarks>
public class BattleLateJoinSpawnTests
{
    [Fact]
    public void JoiningAFightInProgress_FieldsTheHeroOnly()
    {
        // The reported case: the joiner owned ~140 men across its own party and seven allied ones.
        Assert.Equal(1, CoopBattleMissionSpawnHandler.OpeningSpawnForLateJoiner(140));
    }

    [Fact]
    public void TheRestIsNotDiscarded_OnlyDeferred()
    {
        // The opening wave shrinks; TotalSpawnNumber/RemainingSpawnNumber are deliberately left alone so the
        // engine still knows those men exist and feeds them in as reinforcements. Guarding the intent: the
        // opening figure must be strictly smaller than the force, not equal to it.
        const int ownedForce = 140;

        var opening = CoopBattleMissionSpawnHandler.OpeningSpawnForLateJoiner(ownedForce);

        Assert.True(opening < ownedForce, "a late joiner must not field its whole force at once");
        Assert.True(opening > 0, "but it must still field its hero");
    }

    [Fact]
    public void AJoinerWithASingleMan_StillFieldsHim()
    {
        // A player arriving alone is the case that must not round down to nothing: zero would leave them with
        // no agent, which the spawn handler treats as a battle it cannot start.
        Assert.Equal(1, CoopBattleMissionSpawnHandler.OpeningSpawnForLateJoiner(1));
    }

    [Fact]
    public void AJoinerOwningNobodyOnThisSide_FieldsNobody()
    {
        // The enemy side of a joiner's mission: it owns nothing there, so there is no hero to guarantee and
        // the floor must not invent one. Everything on that side arrives replicated from its owners.
        Assert.Equal(0, CoopBattleMissionSpawnHandler.OpeningSpawnForLateJoiner(0));
    }

    [Fact]
    public void SeveralClientsJoiningAtOnce_AddAtMostOneManEach()
    {
        // The rule has to hold per joiner, not just for one, because nothing orders arrivals: three
        // players can walk into the same fight in the same second. Each computes its own opening wave from
        // its own owned share, so the only thing bounding the total is that each one is individually clamped.
        int[] ownedForces = { 140, 60, 3 };

        var opening = ownedForces.Select(CoopBattleMissionSpawnHandler.OpeningSpawnForLateJoiner).ToArray();

        Assert.All(opening, o => Assert.Equal(1, o));
        Assert.Equal(ownedForces.Length, opening.Sum());
    }

    [Fact]
    public void StartingABattleNormally_IsUnaffected()
    {
        // The rule applies only when joining a battle already underway. ReachableSpawnNumber is what an
        // ordinary start uses, and it must still hand over the full opening wave.
        Assert.Equal(140, CoopBattleMissionSpawnHandler.ReachableSpawnNumber(sideNumber: 140, ownedShareOfSideNumber: 140));
        Assert.Equal(60, CoopBattleMissionSpawnHandler.ReachableSpawnNumber(sideNumber: 200, ownedShareOfSideNumber: 60));
    }
}
