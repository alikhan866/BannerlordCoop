using Common;
using Common.Logging;
using Common.Messaging;
using Common.Util;
using Missions.Agents.Messages;
using Missions.Agents.Packets;
using Serilog;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.ObjectSystem;

namespace Missions.Agents.Handlers
{
    /// <summary>
    /// Handler for weapon pickups within a battle
    /// </summary>
    public interface IWeaponPickupHandler : IHandler
    {

    }
    /// <inheritdoc/>
    public class WeaponPickupHandler : IWeaponPickupHandler
    {
        readonly INetworkAgentRegistry networkAgentRegistry;
        readonly IBattleNetwork network;
        readonly IMessageBroker messageBroker;
        readonly static ILogger Logger = LogManager.GetLogger<WeaponPickupHandler>();
        public WeaponPickupHandler(
            INetworkAgentRegistry networkAgentRegistry,
            IBattleNetwork network,
            IMessageBroker messageBroker)
        {
            this.networkAgentRegistry = networkAgentRegistry;
            this.network = network;
            this.messageBroker = messageBroker;

            messageBroker.Subscribe<WeaponPickedup>(WeaponPickupSend);
            messageBroker.Subscribe<NetworkWeaponPickedup>(WeaponPickupReceive);

        }
        ~WeaponPickupHandler()
        {
            Dispose();
        }

        public void Dispose()
        {
            messageBroker.Unsubscribe<WeaponPickedup>(WeaponPickupSend);
            messageBroker.Unsubscribe<NetworkWeaponPickedup>(WeaponPickupReceive);
        }

        private void WeaponPickupSend(MessagePayload<WeaponPickedup> obj)
        {
            var payload = obj.What;

            if (!networkAgentRegistry.IsLocallyControlled(payload.Agent))
                return;

            if (!networkAgentRegistry.TryGetAgentInfo(payload.Agent, out var agentInfo))
            {
                Logger.Warning("No agentID was found for the Agent: {agent}", payload.Agent);
                return;
            }

            NetworkWeaponPickedup message = new NetworkWeaponPickedup(
                agentInfo.AgentId,
                payload.EquipmentIndex,
                ItemIdOf(payload.WeaponObject),
                payload.WeaponModifier,
                payload.Banner,
                payload.CurrentEquipment);

            network.SendAll(message);
        }
        private void WeaponPickupReceive(MessagePayload<NetworkWeaponPickedup> obj)
        {
            GameThread.RunSafe(() =>
            {
                if (networkAgentRegistry.TryGetAgentInfo(obj.What.AgentId, out var agentInfo) == false)
                {
                    Logger.Warning("No agent found at {guid} in {class}", obj.What.AgentId, typeof(WeaponPickupHandler));
                    return;
                }

                Agent agent = agentInfo.Agent;
                if (agent == null || agent.Mission != Mission.Current || !agent.IsActive()) return;

                MissionWeapon missionWeapon = new MissionWeapon(
                    ResolveItem(obj.What.ItemObjectId),
                    obj.What.ItemModifier,
                    obj.What.Banner);
                ApplyWeaponPickup(
                    agentInfo,
                    obj.What.EquipmentIndex,
                    ref missionWeapon,
                    obj.What.CurrentEquipment);
            });
        }

        /// <summary>
        /// The id a picked-up item travels under, or null when the slot is empty.
        /// </summary>
        /// <remarks>
        /// An item with no <c>StringId</c> cannot be resolved on the far side, so it is sent as "no item" rather
        /// than as an id that will not match: the receiver then equips an empty weapon, which is what the
        /// pickup of an unresolvable item amounts to, instead of holding a weapon nobody else can see.
        /// </remarks>
        internal static string ItemIdOf(ItemObject item)
            => string.IsNullOrEmpty(item?.StringId) ? null : item.StringId;

        /// <summary>Resolves an item id back to the item, tolerating the empty-slot case.</summary>
        /// <remarks>
        /// Resolved through <see cref="MBObjectManager"/> rather than the coop object manager, matching the
        /// missile and puppet paths: items are static game data present identically on every machine, not
        /// replicated instances that need a coop id.
        /// </remarks>
        internal static ItemObject ResolveItem(string itemObjectId)
        {
            if (string.IsNullOrEmpty(itemObjectId)) return null;

            var item = MBObjectManager.Instance?.GetObject<ItemObject>(itemObjectId);
            if (item == null)
                Logger.Warning("Weapon pickup names item {ItemId}, which does not exist here; equipping an empty slot", itemObjectId);

            return item;
        }

        internal static void ApplyWeaponPickup(
            CoopAgentInfo agentInfo,
            EquipmentIndex equipmentIndex,
            ref MissionWeapon missionWeapon,
            AgentEquipmentData currentEquipment)
        {
            Agent agent = agentInfo.Agent;
            agentInfo.RecordAuthoritativeEquipment(currentEquipment);
            using (new AllowedThread())
            {
                if (equipmentIndex == EquipmentIndex.ExtraWeaponSlot)
                    agent.EquipWeaponToExtraSlotAndWield(ref missionWeapon);
                else
                    agent.EquipWeaponWithNewEntity(equipmentIndex, ref missionWeapon);

                // Vanilla chooses the hand after equipping a pickup. Replay that exact post-pickup state so a
                // shield does not remain holstered while the remote agent is already blocking with it.
                currentEquipment.Apply(agent);
            }
        }
    }
}
