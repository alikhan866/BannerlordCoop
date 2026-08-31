using Common.Messaging;
using ProtoBuf;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Messages;

/// <summary>One party/troop-type scoreboard entry inside a
/// <see cref="NetworkUpdateScoreboardAfterUpgradesBatch"/>.</summary>
[ProtoContract(SkipConstructor = true)]
public readonly struct ScoreboardUpgradeEntry
{
    [ProtoMember(1)]
    public readonly string AffectorCharacterId;
    [ProtoMember(2)]
    public readonly string AffectorPartyId;
    [ProtoMember(3)]
    public readonly BattleSideEnum AffectorAgentSide;
    [ProtoMember(4)]
    public readonly int UpgradedCount;

    public ScoreboardUpgradeEntry(
        string affectorCharacterId,
        string affectorPartyId,
        BattleSideEnum affectorAgentSide,
        int upgradedCount)
    {
        AffectorCharacterId = affectorCharacterId;
        AffectorPartyId = affectorPartyId;
        AffectorAgentSide = affectorAgentSide;
        UpgradedCount = upgradedCount;
    }
}

/// <summary>
/// Every scoreboard upgrade change for one map event in a single message.
/// </summary>
/// <remarks>
/// Replaces a per-(party, troop-type) message. Measured over one live siege, the unbatched form was the
/// single largest recurring consumer on the server pipe — 21,004 messages totalling 2.03 MB, averaging 97
/// bytes each — because every message repeated <see cref="MapEventId"/> and its own party id as full
/// strings, and paid message framing per troop type. Batching writes the map event id once per flush and
/// leaves the entries carrying only what actually differs between them.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
public readonly struct NetworkUpdateScoreboardAfterUpgradesBatch : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventId;
    [ProtoMember(2)]
    public readonly ScoreboardUpgradeEntry[] Entries;

    public NetworkUpdateScoreboardAfterUpgradesBatch(string mapEventId, ScoreboardUpgradeEntry[] entries)
    {
        MapEventId = mapEventId;
        Entries = entries;
    }
}
