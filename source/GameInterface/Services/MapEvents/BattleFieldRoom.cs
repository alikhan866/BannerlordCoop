using System;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// How many more men a side may put on the field before it reaches its share of the battle size.
/// </summary>
/// <remarks>
/// This exists because the engine cannot answer the question for itself in a coop battle. Its own reinforcement
/// arithmetic is <c>NumberOfActiveTroops = spawned - removed</c>, counted PER SUPPLIER, so it sees only the
/// agents it spawned through that supplier: not the ones a peer replicated, and not the ones
/// <c>ReinforcementFielder</c> spawns directly for a party that joined mid-battle. Its picture of the side is
/// therefore an undercount, and it sizes waves against the shortfall it imagines rather than the field that
/// actually exists.
///
/// Measured live, on a battle sized for 400: the attacker side stood at 190 with room for about 13, and the
/// engine asked its supplier for 101. It did that three times. Each wave landed in under two seconds, took the
/// side past 290, and was then cut back down by the field balancer - and because the phase's
/// <c>RemainingSpawnNumber</c> is consumed by what actually SPAWNS, those ~300 men were spent out of a reserve
/// of 534 without ever fighting. By the time the reserve read zero the attacker could no longer reinforce at
/// all, which is what "not everyone spawned in and then most of them ran away" looked like from the scoreboard.
///
/// So the room is measured from the mission itself - every live human on the side, whoever owns them - and the
/// supplier refuses to hand over more than fits. That makes the cap PREVENTIVE. The balancer stays as the
/// corrective backstop for whatever still slips past it.
/// </remarks>
public static class BattleFieldRoom
{
    /// <summary>No sizing exists to respect, so the caller should impose no limit of its own.</summary>
    public const int Unlimited = int.MaxValue;

    /// <summary>
    /// Room left on <paramref name="side"/>, or <see cref="Unlimited"/> when this mission has no battle sizing.
    /// </summary>
    /// <remarks>
    /// Two of the three answers here are deliberate and were each got wrong once.
    ///
    /// No spawn logic means no sizing exists - headless and mock missions run that way - so imposing a limit
    /// would invent a rule the battle never had: <see cref="Unlimited"/>.
    ///
    /// Before the opening wave has finished landing the answer is also <see cref="Unlimited"/>, and that is not
    /// laxity. The engine's <c>CheckDeployment</c> reserves <c>InitialSpawnNumber - ReservedTroopsCount</c> and
    /// SKIPS THE WHOLE SIDE - its plan-making included - while the count falls short, so a supplier that
    /// under-delivers during deployment stops the side being planned at all and the player never gets an agent.
    /// Deployment must never be short-changed; the cap only governs reinforcement.
    ///
    /// An unresolvable map event is the opposite case: the sizing exists, we simply cannot read it this instant.
    /// That fails CLOSED, because the one time it failed open the attacker side reached 1,072 on a battle sized
    /// for 400.
    /// </remarks>
    public static int Remaining(MapEvent mapEvent, BattleSideEnum side)
    {
        var target = SideTarget(mapEvent, side);
        if (target == Unlimited) return Unlimited;

        return RoomLeft(target, CountActiveHumans(Mission.Current, side));
    }

    /// <summary>
    /// How many men this side may have standing at once, or <see cref="Unlimited"/> when nothing sizes it.
    /// </summary>
    /// <remarks>
    /// Exposed separately from <see cref="Remaining"/> because "room left on the side" turns out to be the
    /// wrong question for deciding what any ONE client may field.
    ///
    /// Room left is a shared, contended number: once the side is full it is zero for everybody, and every
    /// owner's share of zero is zero. A client that joins a battle already at capacity therefore never fields
    /// anything at all - and as casualties open room, whoever asks first takes it. The host asks constantly,
    /// from its engine and its reinforcement fielder both; a joining client asks every few seconds and always
    /// finds it gone. Measured live: a joining player spawned exactly ONE troop, his own hero, and was refused
    /// 64 men every three seconds for an entire battle while holding a reserve of 950.
    ///
    /// Dividing the TARGET instead gives every owner a private quota it fills at its own pace, and nobody can
    /// be starved by being slower to ask.
    /// </remarks>
    public static int SideTarget(MapEvent mapEvent, BattleSideEnum side)
    {
        var mission = Mission.Current;
        var spawnLogic = mission?.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
        if (spawnLogic == null) return Unlimited;
        if (!spawnLogic.IsInitialSpawnOver) return Unlimited;
        if (mapEvent == null) return 0;

        var settings = spawnLogic.SpawnSettings;
        var targets = BattleSizeTargets.Calculate(
            mapEvent.GetMapEventSide(BattleSideEnum.Defender)?.TroopCount ?? 0,
            mapEvent.GetMapEventSide(BattleSideEnum.Attacker)?.TroopCount ?? 0,
            spawnLogic.BattleSize,
            settings.MaximumBattleSideRatio,
            settings.DefenderAdvantageFactor);

        return targets.For(side);
    }

    /// <summary>Room left on a side: its target less what is already standing, never negative.</summary>
    public static int RoomLeft(int sideTarget, int activeOnSide) => Math.Max(0, sideTarget - activeOnSide);

    /// <summary>
    /// Every live human on a side, whoever owns them - replicated puppets included.
    /// </summary>
    /// <remarks>
    /// Counting only the agents this client owns is the mistake the engine itself makes: it treats every other
    /// client's troops as missing and refills the space they are already standing in.
    /// </remarks>
    public static int CountActiveHumans(Mission mission, BattleSideEnum side)
    {
        var agents = mission?.Agents;
        if (agents == null) return 0;

        int count = 0;
        foreach (var agent in agents)
        {
            if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;
            if ((agent.Team?.Side ?? BattleSideEnum.None) != side) continue;
            count++;
        }
        return count;
    }
}
