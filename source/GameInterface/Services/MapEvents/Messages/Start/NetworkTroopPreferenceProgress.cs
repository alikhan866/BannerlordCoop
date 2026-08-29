using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Start;

/// <summary>
/// [Server -&gt; Client] Who the battle is still waiting on, so a player who has already chosen can be told
/// whose answer is holding it.
/// </summary>
/// <remarks>
/// An empty <see cref="OutstandingHeroIds"/> means the wait is over and the mission start is on its way.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkTroopPreferenceProgress : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventId;

    [ProtoMember(2)]
    public readonly string[] OutstandingHeroIds;

    public NetworkTroopPreferenceProgress(string mapEventId, string[] outstandingHeroIds)
    {
        MapEventId = mapEventId;
        OutstandingHeroIds = outstandingHeroIds;
    }
}
