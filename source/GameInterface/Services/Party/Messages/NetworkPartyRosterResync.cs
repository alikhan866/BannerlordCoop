using Common.Messaging;
using GameInterface.Services.TroopRosters.Data;
using ProtoBuf;

namespace GameInterface.Services.Party.Messages;

/// <summary>
/// The server's whole truth for one roster, sent to a client whose party edits it just refused.
/// </summary>
/// <remarks>
/// Replication is otherwise entirely delta-based, and a delta can only ever describe a CHANGE - it has no way
/// to correct a roster that is already wrong. That left one failure with no exit: the party screen sends its
/// edits as a signed delta against the roster as the client saw it, the server refuses the batch if its own
/// roster no longer matches, and the client is told to "reopen the party screen and try again". Reopening reads
/// the same diverged roster and produces the same delta, so it is refused again, forever - a player with one
/// stale troop stack cannot discard, ransom, or transfer anything at all, and nothing in the game tells them
/// why.
///
/// Sending the authority's version closes that loop: whatever the divergence was, and whatever caused it, the
/// next attempt is made against the truth. Absolute rather than incremental for the same reason a resync has to
/// be - if deltas could have fixed it, it would not have needed fixing.
/// </remarks>
[ProtoContract(SkipConstructor = true)]
internal readonly struct NetworkPartyRosterResync : ICommand
{
    [ProtoMember(1)]
    public readonly string RosterId;
    [ProtoMember(2)]
    public readonly TroopRosterData RosterData;

    public NetworkPartyRosterResync(string rosterId, TroopRosterData rosterData)
    {
        RosterId = rosterId;
        RosterData = rosterData;
    }
}
