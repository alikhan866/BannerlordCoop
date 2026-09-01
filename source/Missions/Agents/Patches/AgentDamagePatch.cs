using Common;
using Common.Messaging;
using Common.Util;
using HarmonyLib;
using Missions.Agents.Extensions;
using Missions.Agents.Messages;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents.Patches
{
    /// <summary>
    /// Intercept agent damage and determine if a network call is needed
    /// </summary>
    [HarmonyPatch(typeof(Mission), "RegisterBlow")]
    public class AgentDamagePatch
    {
        private static bool Prefix(Agent attacker, Agent victim, Blow b, ref AttackCollisionData collisionData)
        {
            if (!attacker.IsLocallyControlled()) return false;

            // construct a agent damage data
            AgentDamaged agentDamageData = new AgentDamaged(attacker, victim, b, collisionData);

            // publish the event
            MessageBroker.Instance.Publish(attacker, agentDamageData);

            if (victim.IsLocallyControlled()) return true;
             
            return false;
        }
    }

    /// <summary>
    /// Stops this node applying a blow to an agent it does not own, so the hit can be routed to the owner
    /// instead. BattleDamageRouter is written around this being live - it publishes the suppressed hit as a
    /// BattlePuppetHit, and calls RunOriginalRegisterBlow with an AllowedThread purely to bypass this
    /// prefix - but the class carried no patch category, and nothing calls PatchAllUncategorized on this
    /// assembly, so it was never installed. Every node applied each hit locally AND received the routed
    /// copy: damage landed twice, which reads in a battle as one side killing about twice as fast.
    /// </summary>
    [HarmonyPatch(typeof(Agent), "RegisterBlow")]
    [HarmonyPatchCategory(MissionModule.DamageSuppressionPatchCategory)]
    public class RegisterBlowPatch
    {
        private static bool Prefix(ref Agent __instance)
        {
            bool onAllowedThread = AllowedThread.IsThisThreadAllowed();
            bool victimIsLocal = !onAllowedThread && __instance.IsLocallyControlled();
#if DEBUG
            Missions.Diagnostics.DamageAttributionDiagnostics.RecordRegisterBlow(
                onAllowedThread,
                victimIsLocal);
#endif

            if (onAllowedThread) return true;

            return victimIsLocal;
        }

        public static void RunOriginalRegisterBlow(Agent agent, Blow blow, AttackCollisionData collisionData)
        {
            GameThread.Run(() =>
            {
                using(new AllowedThread())
                {
                    agent.RegisterBlow(blow, collisionData);
                }
            });
        }
    }

    #region SkipPatches
    [HarmonyPatch(typeof(Mission), "ChargeDamageCallback")]
    public class ChargeDamageCallbackPatch
    {
        private static bool Prefix(ref Agent attacker)
        {
            return attacker.IsLocallyControlled();
        }
    }

    [HarmonyPatch(typeof(Mission), "FallDamageCallback")]
    public class FallDamageCallbackPatch
    {
        private static bool Prefix(ref Agent attacker)
        {
            return attacker.IsLocallyControlled();
        }
    }

    [HarmonyPatch(typeof(Mission), "MeleeHitCallback")]
    public class MeleeHitCallbackPatch
    {
        private static bool Prefix(ref Agent attacker)
        {
            return attacker.IsLocallyControlled();
        }
    }

    [HarmonyPatch(typeof(Mission), "MissileAreaDamageCallback")]
    public class MissileAreaDamageCallbackPatch
    {
        private static bool Prefix(ref Agent shooterAgent)
        {
            return shooterAgent.IsLocallyControlled();
        }
    }

    [HarmonyPatch(typeof(Mission), "MissileHitCallback")]
    public class MissileHitCallbackPatch
    {
        private static bool Prefix(ref Agent attacker)
        {
            return attacker.IsLocallyControlled();
        }
    }
    #endregion
}