using Common.Messaging;
using ProtoBuf;

namespace GameInterface.Services.MapEvents.Messages.Start;

/// <summary>
/// [Client -&gt; Server] This player has chosen; the battle may stop waiting on them.
/// </summary>
/// <remarks>
/// Deliberately carries no preference. What each client picked stays on that client and is read when it
/// allocates its own troops, so there is nothing here that could drift from what actually happens - the server
/// only needs to know the question was answered.
///
/// It carries no identity either. The server resolves who sent it from the peer, so a client cannot answer on
/// somebody else's behalf and start a battle the others are still choosing for.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkTroopPreferenceChosen : ICommand
{
    [ProtoMember(1)]
    public readonly string MapEventId;

    public NetworkTroopPreferenceChosen(string mapEventId)
    {
        MapEventId = mapEventId;
    }
}
