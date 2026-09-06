// DIAGNOSTIC ONLY - compiled out of a shipping build.
//
// This is a Postfix on EVERY Mission.RegisterBlow, and it calls IsLocallyControlled, which takes a
// LOCK on the agent registry. Measured at 14-19 microseconds per blow: harmless at the ~30 blows a
// second of a test battle (0.05% of a second) but it scales with the blow rate, and at 500 blows a
// second it would be about 1% of a second spent contending on a lock for a counter nobody reads in a
// release build.
#if DEBUG
using System;
using HarmonyLib;
using Missions.Agents.Extensions;
using Missions.Diagnostics;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents.Patches;

/// <summary>
/// Counting only. Records who landed each blow so a doubled hit can be told apart from a doubled swing rate.
/// </summary>
/// <remarks>
/// A Postfix that reads and returns; it changes no state and skips nothing. Mission.RegisterBlow carries
/// both attacker and victim, which Agent.RegisterBlow does not, so attacker ownership can only be attributed
/// here.
/// </remarks>
[HarmonyPatch(typeof(Mission), "RegisterBlow")]
[HarmonyPatchCategory(MissionModule.DamageSuppressionPatchCategory)]
public static class DamageAttributionPatch
{
    /// <summary>Speed the engine believes the agent is moving at, or -1 when it cannot be read.</summary>
    private static float VelocityOf(Agent agent)
    {
        try
        {
            Agent moving = agent.HasMount ? agent.MountAgent : agent;
            if (moving == null) return -1f;
            float speed = moving.Velocity.Length;
            return float.IsNaN(speed) || float.IsInfinity(speed) ? -1f : speed;
        }
        catch (Exception) { return -1f; }
    }

    private static void Postfix(Agent attacker, Agent victim, Blow b, ref AttackCollisionData collisionData)
    {
        long costStart = Missions.Diagnostics.HotPathCostDiagnostics.Now();
        try
        {
            Measure(attacker, victim, b, ref collisionData);
        }
        finally
        {
            Missions.Diagnostics.HotPathCostDiagnostics.AddDamageAttribution(costStart);
        }
    }

    private static void Measure(Agent attacker, Agent victim, Blow b, ref AttackCollisionData collisionData)
    {
        if (attacker == null) return;
        // The duel rig's per-blow event, before the aggregate counters' gate: the two are armed separately.
        DuelEvents.RecordBlow(attacker, victim, in b, in collisionData);
        if (!DamageAttributionDiagnostics.Enabled) return;
        if (Mission.Current?.GetMissionBehavior<CoopMissionController>() == null) return;

        bool attackerIsLocal = attacker.IsLocallyControlled();
        DamageAttributionDiagnostics.RecordAttacker(attacker.Index, attackerIsLocal);

        // How far apart the two were when the blow registered. A blow landing from well beyond weapon
        // reach is the "hit from across the field" complaint; splitting by attacker ownership says
        // whether it is specific to agents this node does not simulate.
        if (victim == null) return;
        try
        {
            // Two distances, because they answer different questions. attacker-to-victim can be inflated
            // simply because a remote attacker walked on between the blow being routed and applied here.
            // The COLLISION POINT is where the engine says the weapon actually struck: if that is far from
            // the victim too, the hit really did land away from them and it is not a timing artifact.
            Vec3 impact = collisionData.CollisionGlobalPosition;
            float impactToVictim = float.NaN;
            if (!float.IsNaN(impact.x) && !float.IsInfinity(impact.x))
                impactToVictim = impact.Distance(victim.Position);

            // MISSILES MUST BE EXCLUDED. An archer legitimately hits from tens of metres, and counting
            // those as "melee landed from far away" would invent a position bug that is not there.
            // Damage carries a RELATIVE SPEED bonus. If a puppet victim's velocity reads low on this
            // machine - and it does, mounts report below their actual movement - the closing speed reads
            // low with it and the blow lands soft. Recorded per blow so a soft hit can be traced to the
            // modifier rather than guessed at.
            // The PLAYER's own hits, kept apart from the AI's. A driven test player stands still and
            // never swings, so every damage figure measured unattended is an AI hit in a scrum - which
            // is a different encounter from a player charging a horse archer. Mixing them hid the case
            // that was actually reported.
            if (attacker == Mission.Current.MainAgent)
            {
                DamageAttributionDiagnostics.RecordPlayerBlow(
                    b.InflictedDamage,
                    collisionData.BaseMagnitude,
                    collisionData.MovementSpeedDamageModifier,
                    victim.IsLocallyControlled(),
                    victim.HasMount || victim.IsMount,
                    VelocityOf(victim),
                    VelocityOf(attacker),
                    b.IsMissile);
            }

            DamageAttributionDiagnostics.RecordBlowDamage(
                b.InflictedDamage,
                collisionData.BaseMagnitude,
                collisionData.MovementSpeedDamageModifier,
                victim.IsLocallyControlled(),
                victim.HasMount || victim.IsMount,
                VelocityOf(victim),
                b.IsMissile);

            DamageAttributionDiagnostics.RecordBlowDistance(
                attacker.Position.Distance(victim.Position),
                impactToVictim,
                attackerIsLocal,
                victim.IsLocallyControlled(),
                b.IsMissile);
        }
        catch (Exception)
        {
            // A diagnostic must never take a battle down.
        }
    }
}
#endif
