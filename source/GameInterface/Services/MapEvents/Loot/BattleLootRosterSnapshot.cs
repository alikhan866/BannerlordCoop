using GameInterface.Services.TroopRosters.Data;
using ProtoBuf;
using System.Collections.Generic;
using System.Linq;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>One kind of item, with its modifier, and how many of it a roster holds.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct BattleLootItemStack
{
    [ProtoMember(1)]
    public readonly string ItemId;

    [ProtoMember(2)]
    public readonly string ModifierId;

    [ProtoMember(3)]
    public readonly int Count;

    public BattleLootItemStack(string itemId, string modifierId, int count)
    {
        ItemId = itemId;
        ModifierId = modifierId;
        Count = count;
    }
}

/// <summary>
/// What a party's rosters ARE after the server applied a loot result - not what changed.
/// </summary>
/// <remarks>
/// Absolute rather than incremental, and that is the whole point of the type. By the time this arrives the
/// client's own loot screen has already moved its local rosters, so a message saying "add these" would credit
/// everything a second time. It is the same trap the relation sync avoids by setting a value instead of
/// applying a delta.
///
/// Being absolute is also the only way to report an outcome that DIFFERS from what the player asked for,
/// which happens whenever the server clamps to party or prisoner capacity, or declines a hero who stopped
/// being capturable while the screen was open. A delta could only ever describe the request.
///
/// Troops reuse <see cref="TroopRosterElementData"/> so there is one packed troop shape in the codebase
/// rather than two that can drift apart.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
public readonly struct BattleLootRosterSnapshot
{
    [ProtoMember(1)]
    public readonly BattleLootItemStack[] Items;

    [ProtoMember(2)]
    public readonly TroopRosterElementData[] Members;

    [ProtoMember(3)]
    public readonly TroopRosterElementData[] Prisoners;

    public BattleLootRosterSnapshot(
        IEnumerable<BattleLootItemStack> items,
        IEnumerable<TroopRosterElementData> members,
        IEnumerable<TroopRosterElementData> prisoners)
    {
        Items = items?.ToArray() ?? new BattleLootItemStack[0];
        Members = members?.ToArray() ?? new TroopRosterElementData[0];
        Prisoners = prisoners?.ToArray() ?? new TroopRosterElementData[0];
    }
}
