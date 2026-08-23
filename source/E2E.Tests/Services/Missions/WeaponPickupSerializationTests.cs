using System;
using System.IO;
using System.Reflection;
using Common.Serialization;
using Common.Util;
using GameInterface.Surrogates;
using Missions.Agents.Handlers;
using Missions.Agents.Messages;
using Missions.Agents.Packets;
using ProtoBuf;
using ProtoBuf.Meta;
using TaleWorlds.Core;
using Xunit;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// A weapon pickup has to survive the wire, which it could not.
/// </summary>
/// <remarks>
/// <c>NetworkWeaponPickedup</c> carried the <see cref="ItemObject"/> itself. Engine types are made wire-safe in
/// this codebase by registering a surrogate in <c>SurrogateCollection</c> - and there is one for every engine
/// type on this message except <see cref="ItemObject"/>, which has none.
///
/// The consequence is not a dropped field. The WHOLE message fails with "No serializer defined for type:
/// TaleWorlds.Core.ItemObject", so nothing reaches the wire and nothing is logged at the far end, because there
/// is no far end. That is why troops fought with their fists: a man who picked a weapon off the ground held it
/// on his owner's machine and empty hands everywhere else, 959 failed sends in one session. The item now travels
/// as its <c>StringId</c> and is resolved through <c>MBObjectManager</c>, matching the missile and puppet paths.
/// </remarks>
public class WeaponPickupSerializationTests
{
    /// <summary>
    /// The model the game actually serializes with.
    /// </summary>
    /// <remarks>
    /// Registering the surrogates is what makes this a test of the real configuration rather than of a bare
    /// protobuf model. Without it every engine type looks broken and the one that IS broken cannot be told
    /// apart from the ones that are fine - which is exactly the false trail this test first produced.
    /// </remarks>
    private static void UseTheGamesSerializationModel()
    {
        ProtoBufSerializer.ConfigureRuntimeModel();
        _ = new SurrogateCollection();
    }

    [Fact]
    public void TheMessageSurvivesARoundTrip()
    {
        // The regression test proper. Against the old message shape this THROWS rather than failing an assert -
        // which is what happened in play, 959 times, silently.
        UseTheGamesSerializationModel();

        var sent = new NetworkWeaponPickedup(
            Guid.NewGuid(),
            EquipmentIndex.Weapon1,
            Guid.NewGuid(),
            "sturgia_axe_2_t3",
            null,
            null,
            new AgentEquipmentData(EquipmentIndex.Weapon1, EquipmentIndex.Weapon2, 0),
            previousSlotAmount: 0,
            previousWorldItemAmount: 1,
            resultingSlotAmount: 1,
            resultingWorldItemAmount: 0,
            worldItemConsumed: true);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, sent);
        stream.Position = 0;

        var received = Serializer.Deserialize<NetworkWeaponPickedup>(stream);

        Assert.Equal(sent.AgentId, received.AgentId);
        Assert.Equal(EquipmentIndex.Weapon1, received.EquipmentIndex);
        Assert.Equal("sturgia_axe_2_t3", received.ItemObjectId);
        Assert.Equal((int)EquipmentIndex.Weapon1, received.CurrentEquipment.MainHandIndex);
        Assert.Equal((int)EquipmentIndex.Weapon2, received.CurrentEquipment.OffHandIndex);
    }

    [Fact]
    public void AnEmptySlotSurvivesARoundTrip()
    {
        // Picking up nothing is a real case - a slot can be cleared - and must not be confused with a failure.
        UseTheGamesSerializationModel();

        var sent = new NetworkWeaponPickedup(
            Guid.NewGuid(),
            EquipmentIndex.Weapon0,
            Guid.NewGuid(),
            null,
            null,
            null,
            new AgentEquipmentData(EquipmentIndex.Weapon0, EquipmentIndex.None, 0),
            previousSlotAmount: 0,
            previousWorldItemAmount: 0,
            resultingSlotAmount: 0,
            resultingWorldItemAmount: 0,
            worldItemConsumed: false);

        using var stream = new MemoryStream();
        Serializer.Serialize(stream, sent);
        stream.Position = 0;

        var received = Serializer.Deserialize<NetworkWeaponPickedup>(stream);

        Assert.True(string.IsNullOrEmpty(received.ItemObjectId));
    }

    [Fact]
    public void EveryMemberOfTheMessageIsSomethingTheModelCanActuallySerialize()
    {
        // The general guard, and the one that would have caught this before it shipped. It asks the configured
        // model directly rather than guessing from type names: an engine type WITH a surrogate is fine, an
        // engine type without one takes the whole message off the wire. Only the model knows which is which.
        UseTheGamesSerializationModel();

        foreach (var property in typeof(NetworkWeaponPickedup).GetProperties())
        {
            Assert.True(
                RuntimeTypeModel.Default.CanSerialize(property.PropertyType),
                $"{property.Name} is typed {property.PropertyType.FullName}, which the model cannot serialize. " +
                "Send an id, or register a surrogate in SurrogateCollection.");
        }
    }

    [Fact]
    public void ItemObjectRemainsTheTypeWithNoSurrogate()
    {
        // Pins WHY the item travels as an id. Should someone add an ItemObject surrogate later, this fails and
        // says so, rather than leaving the id conversion looking like cargo cult.
        UseTheGamesSerializationModel();

        Assert.False(
            RuntimeTypeModel.Default.CanSerialize(typeof(ItemObject)),
            "ItemObject is now serializable; the id round-trip on NetworkWeaponPickedup can be reconsidered.");
    }

    [Fact]
    public void TheItemTravelsAsAStringNotAnEngineObject()
    {
        // Re-typing this member back to ItemObject would compile, pass every behavioural test that does not
        // serialize, and silently take the whole message off the wire again.
        PropertyInfo? item = typeof(NetworkWeaponPickedup).GetProperty(nameof(NetworkWeaponPickedup.ItemObjectId));

        Assert.NotNull(item);
        Assert.Equal(typeof(string), item!.PropertyType);
    }

    // The two tests that stood here covered WeaponPickupHandler.ItemIdOf/ResolveItem, which resolved items
    // through MBObjectManager. Development's rework routes the id through the coop object manager instead and
    // adds world-item tracking those helpers had no equivalent for, so they went with the merge rather than
    // being kept as an unused second way of doing the same thing.
    //
    // What they were guarding has not gone anywhere: the test above still fails if ItemObjectId is ever
    // re-typed back to ItemObject, which is the mistake that took the whole message off the wire.
}
