using Common.Messaging;
using Missions.Agents.Packets;
using ProtoBuf;
using System;
using TaleWorlds.Core;

namespace Missions.Agents.Messages
{
    /// <summary>
    /// External event for agent weapon pickups
    /// </summary>
    /// <remarks>
    /// The picked-up weapon travels as an item ID, never as the <see cref="ItemObject"/> itself. That type has
    /// no protobuf contract, so a message carrying one does not fail at the field - the whole message fails to
    /// serialize, with "No serializer defined for type: TaleWorlds.Core.ItemObject". Nothing reaches the wire
    /// and nothing is logged at the receiving end, because there is no receiving end.
    ///
    /// That is why troops fought with their fists: a man who picked a weapon off the ground held it on his
    /// owner's machine and empty hands on every other, 959 failed sends in one session. The same mistake is
    /// called out on <c>NetworkSiegeWeaponFired</c>, and the missile and puppet paths already send
    /// <c>StringId</c> and resolve through the object manager - this one had simply been missed.
    ///
    /// The modifier and banner beside it stay as engine types on purpose: both have surrogates registered in
    /// <c>SurrogateCollection</c>, which is this codebase's way of making an engine type wire-safe, and they
    /// serialize correctly through it. <see cref="ItemObject"/> is the one with no surrogate - that is the
    /// whole of the defect, and converting the other two as well would only add conversions the model already
    /// performs.
    /// </remarks>
    [ProtoContract(SkipConstructor = true)]
    public class NetworkWeaponPickedup : IEvent
    {
        [ProtoMember(1)]
        public Guid AgentId { get; }

        [ProtoMember(2)]
        public EquipmentIndex EquipmentIndex { get; }

        /// <summary>The picked-up item's <c>StringId</c>; null or empty means an empty weapon slot.</summary>
        [ProtoMember(3)]
        public string ItemObjectId { get; }

        /// <summary>Carried as the engine type: <c>ItemModifierSurrogate</c> makes it wire-safe.</summary>
        [ProtoMember(4)]
        public ItemModifier ItemModifier { get; }

        /// <summary>Carried as the engine type: <c>BannerSurrogate</c> makes it wire-safe.</summary>
        [ProtoMember(5)]
        public Banner Banner { get; }

        [ProtoMember(6)]
        public AgentEquipmentData CurrentEquipment { get; }

        public NetworkWeaponPickedup(
            Guid agentId,
            EquipmentIndex equipmentIndex,
            string itemObjectId,
            ItemModifier itemModifier,
            Banner banner,
            AgentEquipmentData currentEquipment)
        {
            AgentId = agentId;
            EquipmentIndex = equipmentIndex;
            ItemObjectId = itemObjectId;
            ItemModifier = itemModifier;
            Banner = banner;
            CurrentEquipment = currentEquipment;
        }
    }
}
