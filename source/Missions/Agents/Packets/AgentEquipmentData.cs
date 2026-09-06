using Missions.Agents;
using ProtoBuf;
using System;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents.Packets
{
    [ProtoContract(SkipConstructor = true)]
    public readonly struct AgentEquipmentData : IEquatable<AgentEquipmentData>
    {
        public AgentEquipmentData(Agent agent)
        {
            if (!TryRead(agent, out EquipmentIndex mainHandIndex,
                    out EquipmentIndex offHandIndex, out int mainHandUsageIndex))
            {
                mainHandIndex = EquipmentIndex.None;
                offHandIndex = EquipmentIndex.None;
                mainHandUsageIndex = 0;
            }

            MainHandIndex = (int)mainHandIndex;
            OffHandIndex = (int)offHandIndex;
            MainHandUsageIndex = mainHandUsageIndex;
        }

        internal static bool TryCapture(Agent agent, out AgentEquipmentData data)
        {
            data = default;
            if (!TryRead(agent, out EquipmentIndex mainHandIndex,
                    out EquipmentIndex offHandIndex, out int mainHandUsageIndex))
            {
                return false;
            }

            data = new AgentEquipmentData(mainHandIndex, offHandIndex, mainHandUsageIndex);
            return true;
        }

        private static bool TryRead(
            Agent agent,
            out EquipmentIndex mainHandIndex,
            out EquipmentIndex offHandIndex,
            out int mainHandUsageIndex)
        {
            mainHandIndex = EquipmentIndex.None;
            offHandIndex = EquipmentIndex.None;
            mainHandUsageIndex = 0;
            if (agent == null || !agent.IsHuman)
                return false;

            mainHandIndex = agent.GetPrimaryWieldedItemIndex();
            offHandIndex = agent.GetOffhandWieldedItemIndex();
            mainHandUsageIndex = GetUsageIndex(agent.Equipment, mainHandIndex);
            return true;
        }

        internal AgentEquipmentData(
            EquipmentIndex mainHandIndex,
            EquipmentIndex offHandIndex,
            int mainHandUsageIndex)
        {
            MainHandIndex = (int)mainHandIndex;
            OffHandIndex = (int)offHandIndex;
            MainHandUsageIndex = mainHandUsageIndex;
        }

        public void Apply(Agent agent)
        {
            // Bannerlord's wield-change callback always reads every weapon slot. During mission teardown and the
            // next tournament match's spawn, an agent can still be active while its MissionEquipment backing array
            // is temporarily incomplete. Do not invoke the native wield path until all weapon slots are available.
            if (agent?.IsHuman != true || !HasSafeWeaponSlots(agent.Equipment)) return;

            // Only wield an index this agent actually has a weapon in RIGHT NOW. The sender's wielded index can point
            // to a slot that is EMPTY on this puppet — its loadout differs, its weapon depleted/broke, or this is a
            // stale packet landing as the mission tears down (the wielded weapon has already been put away). Wielding
            // an empty slot leaves a wielded index whose equipment[index].Item is null, and
            // SandboxAgentStatCalculateModel.UpdateHumanStats then dereferences item.WeaponComponent → NRE on every
            // following Formation.Tick (notably right after a battle / on host-migration adopt). Validating the slot
            // here covers both the wrong-index case and the end-of-battle race, since it reads the live equipment.
            var mainHand = (EquipmentIndex)MainHandIndex;
            int mainHandUsageIndex = GetSafeUsageIndex(agent.Equipment, mainHand, MainHandUsageIndex);
            if (CanWield(agent, mainHand))
            {
                bool slotDiffers = mainHand != agent.GetPrimaryWieldedItemIndex();
                bool usageDiffers = mainHandUsageIndex != GetUsageIndex(agent.Equipment, mainHand);
                if (slotDiffers)
                {
                    agent.SetWieldedItemIndexAsClient(
                        Agent.HandIndex.MainHand,
                        mainHand,
                        false,
                        false,
                        mainHandUsageIndex);
                }
                else if (usageDiffers && mainHand != EquipmentIndex.None)
                {
                    // Same weapon, different usage (a couched lance, a javelin flipped to melee). This is the call the
                    // vanilla multiplayer client makes for WeaponUsageIndexChangeMessage; re-wielding the slot for a
                    // usage change restarted the wield and the couch usage never stuck (run m-lance-after: the
                    // puppet's lance read couched for one sample).
                    agent.SetUsageIndexOfWeaponInSlotAsClient(mainHand, mainHandUsageIndex);
                }

                // The engine may not keep a usage it re-evaluates itself (the couch, which needs a galloping horse
                // and a couch request the puppet never made): while the owner reports a non-default usage, the
                // per-tick re-assert watches this puppet. Watched even when the read-back matched right here: on the
                // host the managed value read 2 for the rest of the frame and reverted next frame, so a watch that
                // only started on a mismatch never started (run v2-lance-after A->B: 0 couched samples of 4227).
                if (mainHand != EquipmentIndex.None && mainHandUsageIndex != 0)
                    PuppetUsageWatch.Add(agent);
#if DEBUG
                if (Missions.Diagnostics.DuelEvents.Enabled && usageDiffers)
                    Missions.Diagnostics.DuelEvents.Record("usage",
                        "agent=" + Missions.Diagnostics.DuelEvents.Id8(agent) +
                        " apply slot=" + ((int)mainHand).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        " want=" + mainHandUsageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        " read=" + GetUsageIndex(agent.Equipment, mainHand).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        " slotDiffers=" + (slotDiffers ? "1" : "0"));
#endif
            }

            var offHand = (EquipmentIndex)OffHandIndex;
            // The native API's final argument is the main-hand usage index for both hand changes.
            if (offHand != agent.GetOffhandWieldedItemIndex() && CanWield(agent, offHand))
                agent.SetWieldedItemIndexAsClient(Agent.HandIndex.OffHand, offHand, false, false, mainHandUsageIndex);
        }

        /// <summary>
        /// Re-applies this record's main-hand usage to a puppet whose engine shows another one. Returns whether the
        /// puppet still needs watching: as long as the owner reports a non-default usage on the wielded slot, the
        /// engine may drop it again next frame, so the watch stays; it ends when the usage is the default (0) or the
        /// record no longer applies (a different slot is wielded; the packet path owns wield changes).
        /// <paramref name="reasserted"/> says whether a write happened this tick.
        /// </summary>
        internal bool TryReassertUsage(Agent agent, out bool reasserted)
        {
            reasserted = false;
            if (agent?.IsHuman != true || !HasSafeWeaponSlots(agent.Equipment)) return false;
            var mainHand = (EquipmentIndex)MainHandIndex;
            if (mainHand == EquipmentIndex.None || !CanWield(agent, mainHand)) return false;
            if (mainHand != agent.GetPrimaryWieldedItemIndex()) return false;
            int usage = GetSafeUsageIndex(agent.Equipment, mainHand, MainHandUsageIndex);
            if (usage == 0) return false;
            if (usage != GetUsageIndex(agent.Equipment, mainHand))
            {
                agent.SetUsageIndexOfWeaponInSlotAsClient(mainHand, usage);
                reasserted = true;
            }
            return true;
        }

        // True when it is safe to wield this index on this agent: -1 (None) unwields (UpdateHumanStats guards the -1
        // case), and a weapon slot is only safe when it actually holds a weapon on this agent right now.
        private static bool CanWield(Agent agent, EquipmentIndex index)
        {
            if (index == EquipmentIndex.None) return true;
            return index >= EquipmentIndex.WeaponItemBeginSlot
                && index < EquipmentIndex.NumAllWeaponSlots
                && agent.Equipment[index].Item != null;
        }

        internal static bool HasSafeWeaponSlots(MissionEquipment equipment)
        {
            if (equipment?._weaponSlots == null ||
                equipment._weaponSlots.Length < (int)EquipmentIndex.NumAllWeaponSlots)
            {
                return false;
            }

            for (var index = EquipmentIndex.WeaponItemBeginSlot;
                 index < EquipmentIndex.NumAllWeaponSlots;
                 index++)
            {
                MissionWeapon weapon = equipment[index];
                if (weapon.Item != null &&
                    (weapon.CurrentUsageIndex < 0 || weapon.CurrentUsageIndex >= weapon.WeaponsCount))
                {
                    return false;
                }
            }

            return true;
        }

        private static int GetUsageIndex(MissionEquipment equipment, EquipmentIndex index)
        {
            if (index < EquipmentIndex.WeaponItemBeginSlot ||
                index >= EquipmentIndex.NumAllWeaponSlots ||
                equipment?._weaponSlots == null ||
                equipment._weaponSlots.Length <= (int)index)
            {
                return 0;
            }

            MissionWeapon weapon = equipment[index];
            return weapon.Item != null &&
                   weapon.CurrentUsageIndex >= 0 &&
                   weapon.CurrentUsageIndex < weapon.WeaponsCount
                ? weapon.CurrentUsageIndex
                : 0;
        }

        internal static int GetSafeUsageIndex(MissionEquipment equipment, EquipmentIndex index, int usageIndex)
        {
            if (index < EquipmentIndex.WeaponItemBeginSlot ||
                index >= EquipmentIndex.NumAllWeaponSlots ||
                equipment?._weaponSlots == null ||
                equipment._weaponSlots.Length <= (int)index)
            {
                return 0;
            }

            MissionWeapon weapon = equipment[index];
            return weapon.Item != null && usageIndex >= 0 && usageIndex < weapon.WeaponsCount
                ? usageIndex
                : 0;
        }

        public bool Equals(AgentEquipmentData other)
        {
            return MainHandIndex == other.MainHandIndex &&
                   OffHandIndex == other.OffHandIndex &&
                   MainHandUsageIndex == other.MainHandUsageIndex;
        }

        public override bool Equals(object obj)
        {
            return obj is AgentEquipmentData other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = MainHandIndex;
                hashCode = (hashCode * 397) ^ OffHandIndex;
                return (hashCode * 397) ^ MainHandUsageIndex;
            }
        }

        [ProtoMember(1)]
        public int MainHandIndex { get; }
        [ProtoMember(2)]
        public int OffHandIndex { get; }
        [ProtoMember(3)]
        public int MainHandUsageIndex { get; }


    }
}
