using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Start;

/// <summary>
/// [Server -&gt; Client] Asks this player how their troops should deploy, before the battle starts.
/// </summary>
/// <remarks>
/// Sent at the point the server would otherwise broadcast the mission start, so every recipient is still on the
/// map with no mission open. That is what makes holding the battle here safe - nothing has begun, so nobody can
/// run ahead of the players still choosing.
///
/// <see cref="ParticipantHeroIds"/> carries the whole battle's roster so a client can name who it is waiting
/// for. Ids rather than names: every client already has the world and resolves its own, so no display text
/// crosses the wire.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkTroopPreferenceRequest : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventId;

    [ProtoMember(2)]
    public readonly string[] ParticipantHeroIds;

    public NetworkTroopPreferenceRequest(string mapEventId, string[] participantHeroIds)
    {
        MapEventId = mapEventId;
        ParticipantHeroIds = participantHeroIds;
    }
}
