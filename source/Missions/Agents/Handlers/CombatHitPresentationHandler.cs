using Common;
using Common.Messaging;
using Missions.Agents.Extensions;
using Missions.Agents.Messages;
using System;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents.Handlers;

public interface ICombatHitPresentationHandler : IHandler
{
    void BroadcastAcceptedMeleeBlood(
        Agent victim,
        Agent attacker,
        in Blow blow,
        in AttackCollisionData collisionData);

    void PresentRoutedMeleeBlood(
        Agent victim,
        Agent attacker,
        in Blow blow,
        in AttackCollisionData collisionData,
        string attackerControllerId);
}

/// <summary>Replicates blood and shield-impact presentation without changing authoritative combat state.</summary>
public class CombatHitPresentationHandler : ICombatHitPresentationHandler
{
    private readonly INetworkAgentRegistry agentRegistry;
    private readonly IBattleNetwork network;
    private readonly IMessageBroker messageBroker;

    public CombatHitPresentationHandler(
        INetworkAgentRegistry agentRegistry,
        IBattleNetwork network,
        IMessageBroker messageBroker)
    {
        this.agentRegistry = agentRegistry;
        this.network = network;
        this.messageBroker = messageBroker;

        messageBroker.Subscribe<MeleeHitPresentation>(Handle_LocalPresentation);
        messageBroker.Subscribe<NetworkMeleeHitPresentation>(Handle_NetworkPresentation);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<MeleeHitPresentation>(Handle_LocalPresentation);
        messageBroker.Unsubscribe<NetworkMeleeHitPresentation>(Handle_NetworkPresentation);
    }

    public void BroadcastAcceptedMeleeBlood(
        Agent victim,
        Agent attacker,
        in Blow blow,
        in AttackCollisionData collisionData)
    {
        if (!TryCreateBloodPresentation(
                victim,
                attacker,
                in blow,
                in collisionData,
                out MeleeHitPresentation presentation) ||
            !attacker.IsLocallyControlled())
        {
            return;
        }

        SendPresentation(presentation);
    }

    public void PresentRoutedMeleeBlood(
        Agent victim,
        Agent attacker,
        in Blow blow,
        in AttackCollisionData collisionData,
        string attackerControllerId)
    {
        if (!TryCreateBloodPresentation(
                victim,
                attacker,
                in blow,
                in collisionData,
                out MeleeHitPresentation presentation))
        {
            return;
        }

        PlayBlood(victim, presentation.CollisionBoneIndex, presentation.Strength);
        if (!TryCreateNetworkPresentation(presentation, out NetworkMeleeHitPresentation message))
            return;

        network.SendAllBut(attackerControllerId, message);
    }

    private static bool TryCreateBloodPresentation(
        Agent victim,
        Agent attacker,
        in Blow blow,
        in AttackCollisionData collisionData,
        out MeleeHitPresentation presentation)
    {
        presentation = default;
        if (victim == null ||
            attacker == null ||
            blow.IsMissile ||
            blow.InflictedDamage <= 0 ||
            !blow.WeaponRecord.HasWeapon() ||
            blow.WeaponRecord.WeaponFlags.HasAnyFlag(WeaponFlags.NoBlood) ||
            collisionData.IsAlternativeAttack)
        {
            return false;
        }

        presentation = new MeleeHitPresentation(
            victim,
            MeleeHitPresentationKind.Blood,
            collisionData.CollisionBoneIndex,
            collisionData.CollisionGlobalPosition,
            blow.WeaponRecord.WeaponClass,
            collisionData.PhysicsMaterialIndex,
            Clamp(blow.InflictedDamage / 50f, 0.25f, 1f));
        return true;
    }

    private void Handle_LocalPresentation(MessagePayload<MeleeHitPresentation> payload)
    {
        SendPresentation(payload.What);
    }

    private void SendPresentation(MeleeHitPresentation presentation)
    {
        if (!TryCreateNetworkPresentation(presentation, out NetworkMeleeHitPresentation message))
        {
#if DEBUG
            if (presentation.Kind == MeleeHitPresentationKind.ShieldImpact)
                Diagnostics.ShieldImpactDiagnostics.SendFailedNoIdentity();
#endif
            return;
        }

#if DEBUG
        if (presentation.Kind == MeleeHitPresentationKind.ShieldImpact)
            Diagnostics.ShieldImpactDiagnostics.Sent();
#endif
        network.SendAll(message);
    }

    private bool TryCreateNetworkPresentation(
        MeleeHitPresentation presentation,
        out NetworkMeleeHitPresentation message)
    {
        message = default;
        if (!TryResolveIdentity(presentation.Victim, out Guid victimId, out bool isMount))
            return false;

        message = new NetworkMeleeHitPresentation(
            victimId,
            isMount,
            presentation.Kind,
            presentation.CollisionBoneIndex,
            presentation.CollisionPosition,
            presentation.AttackerWeaponClass,
            presentation.PhysicsMaterialIndex,
            presentation.Strength);
        return true;
    }

    private bool TryResolveIdentity(Agent victim, out Guid victimId, out bool isMount)
    {
        victimId = Guid.Empty;
        isMount = false;
        if (agentRegistry.TryGetAgentInfo(victim, out CoopAgentInfo info))
        {
            victimId = info.AgentId;
            return true;
        }

        Agent rider = victim?.RiderAgent;
        if (victim?.IsMount != true ||
            rider == null ||
            !agentRegistry.TryGetAgentInfo(rider, out info))
        {
            return false;
        }

        victimId = info.AgentId;
        isMount = true;
        return true;
    }

    private void Handle_NetworkPresentation(MessagePayload<NetworkMeleeHitPresentation> payload)
    {
        NetworkMeleeHitPresentation presentation = payload.What;
        GameThread.RunSafe(
            () => Apply(presentation),
            context: nameof(Handle_NetworkPresentation));
    }

    private void Apply(NetworkMeleeHitPresentation presentation)
    {
#if DEBUG
        bool isShield = presentation.Kind == MeleeHitPresentationKind.ShieldImpact;
        if (isShield) Diagnostics.ShieldImpactDiagnostics.Received();
#endif
        if (!agentRegistry.TryGetAgentInfo(presentation.VictimAgentId, out CoopAgentInfo info))
        {
#if DEBUG
            if (isShield) Diagnostics.ShieldImpactDiagnostics.DropVictimUnknown();
#endif
            return;
        }
        Agent victim = presentation.IsMount ? info.Agent?.MountAgent : info.Agent;
        Mission mission = Mission.Current;
        if (mission == null || victim == null || victim.Mission != mission || !victim.IsActive())
        {
#if DEBUG
            if (isShield) Diagnostics.ShieldImpactDiagnostics.DropVictimInactive();
#endif
            return;
        }

        switch (presentation.Kind)
        {
            case MeleeHitPresentationKind.Blood:
                PlayBlood(victim, presentation.CollisionBoneIndex, presentation.Strength);
                break;
            case MeleeHitPresentationKind.ShieldImpact:
                PlayShieldImpact(mission, victim, presentation);
#if DEBUG
                Diagnostics.ShieldImpactDiagnostics.PlayedOk();
#endif
                break;
        }
    }

    internal static void PlayBlood(Agent victim, int collisionBoneIndex, float strength)
    {
        if (!victim.IsActive()) return;

        sbyte boneIndex = collisionBoneIndex >= sbyte.MinValue && collisionBoneIndex <= sbyte.MaxValue
            ? (sbyte)collisionBoneIndex
            : (sbyte)-1;
        if (boneIndex < 0)
            boneIndex = victim.GetRandomPairOfRealBloodBurstBoneIndices().Item1;
        if (boneIndex >= 0)
            victim.CreateBloodBurstAtLimb(boneIndex, strength);
    }

    private static void PlayShieldImpact(
        Mission mission,
        Agent victim,
        NetworkMeleeHitPresentation presentation)
    {
        // Prefer the real weapon-on-shield event; fall back to the item-physics set only if it fails to
        // resolve, so a bad lookup degrades to the old sound rather than to silence.
        int soundIndex = SelectShieldBlockSound(
            presentation.AttackerWeaponClass,
            presentation.PhysicsMaterialIndex);
        bool usedBlockEvent = soundIndex >= 0;
        if (!usedBlockEvent)
        {
            soundIndex = SelectShieldImpactSound(
                presentation.AttackerWeaponClass,
                presentation.PhysicsMaterialIndex);
        }
        Vec3 position = IsFinite(presentation.CollisionPosition)
            ? presentation.CollisionPosition
            : victim.Position;
#if DEBUG
        Diagnostics.ShieldImpactDiagnostics.SoundChosen(
            soundIndex,
            (int)presentation.AttackerWeaponClass,
            position.Distance(victim.Position),
            usedBlockEvent);
#endif
        var parameter = new SoundEventParameter("Force", Clamp(presentation.Strength, 0.2f, 1f));
        mission.MakeSound(
            soundIndex,
            position,
            soundCanBePredicted: true,
            isReliable: false,
            -1,
            -1,
            ref parameter);
    }

    // The real weapon-on-shield impact events, as shipped in Native/ModuleData. The engine plays these
    // natively on the machine that owns the attacker; a client never gets that, because native does not
    // resolve a puppet's melee collision at all, so it has to play them itself.
    //
    // What used to be used here was ItemPhysicsSoundContainer - the noise an item makes when DROPPED ON
    // THE GROUND. It played reliably (measured: 1,426 published, sent, received and played, none dropped,
    // a valid index every time) and simply sounded wrong, which is why a block against a puppet seemed to
    // make no sound while a block against a locally owned attacker sounded right.
    private const string EventMetalWeaponWoodShield = "event:/mission/combat/impact/metal_weapon/wood_shield";
    private const string EventMetalWeaponMetalShield = "event:/mission/combat/impact/metal_weapon/metal_shield";
    private const string EventWoodWeaponWoodShield = "event:/mission/combat/impact/wood_weapon/wood_shield";
    private const string EventWoodWeaponMetalShield = "event:/mission/combat/impact/wood_weapon/metal_shield";
    private const string EventPunchWoodShield = "event:/mission/combat/impact/punch_weapon/wood_shield";
    private const string EventPunchMetalShield = "event:/mission/combat/impact/punch_weapon/metal_shield";

    private static bool shieldSoundsResolved;
    private static int soundMetalWood = -1;
    private static int soundMetalMetal = -1;
    private static int soundWoodWood = -1;
    private static int soundWoodMetal = -1;
    private static int soundPunchWood = -1;
    private static int soundPunchMetal = -1;

    /// <summary>Resolved once; the ids are stable for the process and the lookup is not free.</summary>
    private static void EnsureShieldSounds()
    {
        if (shieldSoundsResolved) return;
        shieldSoundsResolved = true;
        try
        {
            soundMetalWood = SoundEvent.GetEventIdFromString(EventMetalWeaponWoodShield);
            soundMetalMetal = SoundEvent.GetEventIdFromString(EventMetalWeaponMetalShield);
            soundWoodWood = SoundEvent.GetEventIdFromString(EventWoodWeaponWoodShield);
            soundWoodMetal = SoundEvent.GetEventIdFromString(EventWoodWeaponMetalShield);
            soundPunchWood = SoundEvent.GetEventIdFromString(EventPunchWoodShield);
            soundPunchMetal = SoundEvent.GetEventIdFromString(EventPunchMetalShield);
        }
        catch (Exception)
        {
            // Leave them at -1; the caller falls back to the item-physics set rather than going silent.
        }
    }

    /// <summary>True when the struck surface is wooden, from the physics material the attacker reported.</summary>
    private static bool IsWoodenShield(int physicsMaterialIndex)
    {
        if (physicsMaterialIndex < 0) return true;
        try
        {
            PhysicsMaterial material = PhysicsMaterial.GetFromIndex(physicsMaterialIndex);
            if (!material.IsValid) return true;
            string name = material.Name;
            if (name == null) return true;
            if (name.Contains("metal") || name.Contains("iron") || name.Contains("steel")) return false;
            return true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The weapon-on-shield event for this blow, or -1 if none resolved.</summary>
    internal static int SelectShieldBlockSound(WeaponClass weaponClass, int physicsMaterialIndex)
    {
        EnsureShieldSounds();
        bool wood = IsWoodenShield(physicsMaterialIndex);
        switch (weaponClass)
        {
            case WeaponClass.Undefined:
                return wood ? soundPunchWood : soundPunchMetal;
            case WeaponClass.OneHandedPolearm:
            case WeaponClass.TwoHandedPolearm:
            case WeaponClass.LowGripPolearm:
                return wood ? soundWoodWood : soundWoodMetal;
            default:
                return wood ? soundMetalWood : soundMetalMetal;
        }
    }
    internal static int SelectShieldImpactSound(
        WeaponClass weaponClass,
        int physicsMaterialIndex)
    {
        GetSoundSet(weaponClass, out int defaultSound, out int woodSound, out int stoneSound);
        if (physicsMaterialIndex < 0)
            return defaultSound;

        PhysicsMaterial material = PhysicsMaterial.GetFromIndex(physicsMaterialIndex);
        if (!material.IsValid)
            return defaultSound;

        string materialName = material.Name;
        if (materialName?.Contains("wood") == true)
            return woodSound;
        if (materialName?.Contains("stone") == true)
            return stoneSound;
        return defaultSound;
    }

    private static void GetSoundSet(
        WeaponClass weaponClass,
        out int defaultSound,
        out int woodSound,
        out int stoneSound)
    {
        switch (weaponClass)
        {
            case WeaponClass.Arrow:
            case WeaponClass.Bolt:
                defaultSound = ItemPhysicsSoundContainer.SoundCodePhysicsArrowlikeDefault;
                woodSound = ItemPhysicsSoundContainer.SoundCodePhysicsArrowlikeWood;
                stoneSound = ItemPhysicsSoundContainer.SoundCodePhysicsArrowlikeStone;
                return;
            case WeaponClass.Bow:
            case WeaponClass.Crossbow:
            case WeaponClass.Javelin:
                defaultSound = ItemPhysicsSoundContainer.SoundCodePhysicsBowlikeDefault;
                woodSound = ItemPhysicsSoundContainer.SoundCodePhysicsBowlikeWood;
                stoneSound = ItemPhysicsSoundContainer.SoundCodePhysicsBowlikeStone;
                return;
            case WeaponClass.Dagger:
            case WeaponClass.ThrowingAxe:
            case WeaponClass.ThrowingKnife:
                defaultSound = ItemPhysicsSoundContainer.SoundCodePhysicsDaggerlikeDefault;
                woodSound = ItemPhysicsSoundContainer.SoundCodePhysicsDaggerlikeWood;
                stoneSound = ItemPhysicsSoundContainer.SoundCodePhysicsDaggerlikeStone;
                return;
            case WeaponClass.OneHandedSword:
            case WeaponClass.OneHandedAxe:
            case WeaponClass.Mace:
                defaultSound = ItemPhysicsSoundContainer.SoundCodePhysicsSwordlikeDefault;
                woodSound = ItemPhysicsSoundContainer.SoundCodePhysicsSwordlikeWood;
                stoneSound = ItemPhysicsSoundContainer.SoundCodePhysicsSwordlikeStone;
                return;
            case WeaponClass.TwoHandedSword:
            case WeaponClass.TwoHandedAxe:
            case WeaponClass.TwoHandedMace:
                defaultSound = ItemPhysicsSoundContainer.SoundCodePhysicsGreatswordlikeDefault;
                woodSound = ItemPhysicsSoundContainer.SoundCodePhysicsGreatswordlikeWood;
                stoneSound = ItemPhysicsSoundContainer.SoundCodePhysicsGreatswordlikeStone;
                return;
            case WeaponClass.SmallShield:
            case WeaponClass.LargeShield:
                defaultSound = ItemPhysicsSoundContainer.SoundCodePhysicsShieldlikeDefault;
                woodSound = ItemPhysicsSoundContainer.SoundCodePhysicsShieldlikeWood;
                stoneSound = ItemPhysicsSoundContainer.SoundCodePhysicsShieldlikeStone;
                return;
            default:
                defaultSound = ItemPhysicsSoundContainer.SoundCodePhysicsSpearlikeDefault;
                woodSound = ItemPhysicsSoundContainer.SoundCodePhysicsSpearlikeWood;
                stoneSound = ItemPhysicsSoundContainer.SoundCodePhysicsSpearlikeStone;
                return;
        }
    }

    private static bool IsFinite(Vec3 value) =>
        IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

    private static bool IsFinite(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);

    private static float Clamp(float value, float minimum, float maximum)
    {
        if (!IsFinite(value))
            return minimum;
        return value < minimum ? minimum : value > maximum ? maximum : value;
    }
}
