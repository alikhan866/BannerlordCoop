using GameInterface.Services.MapEvents.TroopSupply;
using GameInterface.Services.ObjectManager;
using TaleWorlds.CampaignSystem.MapEvents;
using System;
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
    public static int Remaining(IObjectManager objectManager, string mapEventId, BattleSideEnum side)
    {
        var target = SideTarget(objectManager, mapEventId, side);
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
    public static int SideTarget(IObjectManager objectManager, string mapEventId, BattleSideEnum side)
    {
        var mission = Mission.Current;
        var spawnLogic = mission?.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
        if (spawnLogic == null) return Unlimited;
        if (!spawnLogic.IsInitialSpawnOver) return Unlimited;
        if (!TryReadSideTotals(objectManager, mapEventId, out var defenderTotal, out var attackerTotal)) return 0;

        var settings = spawnLogic.SpawnSettings;
        var targets = BattleSizeTargets.Calculate(
            defenderTotal,
            attackerTotal,
            spawnLogic.BattleSize,
            settings.MaximumBattleSideRatio,
            settings.DefenderAdvantageFactor);

        return targets.For(side);
    }

    /// <summary>
    /// The two sides' strengths: the battle's live campaign strength, or the committed reserves when the
    /// campaign can no longer answer.
    /// </summary>
    /// <remarks>
    /// Both sources are wrong in a different way, so the order matters and neither may be dropped.
    ///
    /// The campaign is the better number while it exists, because it is LIVE. Reserves are built once, at
    /// entry or host election, and are not extended when a side is reinforced afterwards - so sizing from them
    /// bounds a side by the men it happened to start with, and a relief force of twelve lords arriving
    /// mid-battle ends up unable to field anyone at all. That is why this reads the map event first.
    ///
    /// But the map event can stop existing while its mission is still being fought: finalized underneath a
    /// live mission, it leaves every lookup failing while the fight carries on. All three sizing consumers
    /// then read zero, and zero here does not mean "hold back" - it means the side may never field another
    /// man. Measured live: both sides frozen for three and a half minutes holding 1,123 and 1,293 troops in
    /// reserve, and an enemy army that survived because its men could never reach the field.
    ///
    /// The opening headcount is the wrong number in exactly the way described above, and still far better than
    /// none: the battle stays fightable instead of stalling at whoever happened to be standing when the
    /// campaign object went away.
    ///
    /// With neither available it fails CLOSED, which is what the original guard existed for: the one time this
    /// failed open, the attacker side reached 1,072 on a battle sized for 400.
    /// </remarks>
    internal static bool TryReadSideTotals(IObjectManager objectManager, string mapEventId,
        out int defenderTotal, out int attackerTotal)
    {
        defenderTotal = 0;
        attackerTotal = 0;

        // The campaign first, and deliberately so. The reserves are built once, at entry or host election, and
        // are NOT extended when a side is reinforced afterwards - so a relief force that arrives mid-battle is
        // invisible to them, and sizing from them bounds a side by the men it happened to start with. That was
        // measured: twelve lords joining a battle, and the side unable to field anyone at all.
        if (objectManager != null && !string.IsNullOrEmpty(mapEventId) &&
            objectManager.TryGetObject<MapEvent>(mapEventId, out var mapEvent) && mapEvent != null)
        {
            defenderTotal = mapEvent.GetMapEventSide(BattleSideEnum.Defender)?.TroopCount ?? 0;
            attackerTotal = mapEvent.GetMapEventSide(BattleSideEnum.Attacker)?.TroopCount ?? 0;
            if (defenderTotal > 0 || attackerTotal > 0) return true;
        }

        // Only once the campaign cannot answer. A map event finalized underneath its own live mission stops
        // resolving while the fight carries on, and every sizing consumer then read zero - which does not mean
        // "hold back", it means the side may never field another man. Measured live: both sides frozen for
        // three and a half minutes holding 1,123 and 1,293 troops in reserve.
        //
        // The opening headcount is the wrong number in the ways described above, and it is still enormously
        // better than none: the battle stays fightable instead of stalling at whoever happened to be standing.
        if (string.IsNullOrEmpty(mapEventId)) return false;

        foreach (var supplier in CoopTroopSupplierRegistry.GetSuppliers(mapEventId))
        {
            // An unpopulated supplier has not been told its side's strength yet, and its zero would size the
            // battle as if that side were empty.
            if (supplier == null || !supplier.IsPopulated) continue;

            if (supplier.Side == BattleSideEnum.Defender) defenderTotal = supplier.SideTotalTroops;
            else if (supplier.Side == BattleSideEnum.Attacker) attackerTotal = supplier.SideTotalTroops;
        }

        return defenderTotal > 0 || attackerTotal > 0;
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
