using Common.Messaging;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.MapEvents;

namespace GameInterface.Services.MapEvents.Messages;

public readonly struct MapEventInvolvedPartiesAdded : IEvent
{
    public readonly MapEvent MapEvent;
    public readonly IEnumerable<MapEventParty> AddedParties;

    /// <summary>
    /// Whether a PLAYER party is what joined, as opposed to an AI reinforcement.
    /// </summary>
    /// <remarks>
    /// The two need different handling and only this producer can tell them apart. A player join is the one
    /// moment a client genuinely needs every involved party's flattened roster - it is how a joiner learns
    /// what to spawn - so that push must go out in full. An AI reinforcement join needs no such push: the
    /// clients already hold those rosters, and re-sending them is what turned a battle assembling out of 43
    /// parties into ~946 full-roster broadcasts (measured: 910 packets, 4.7 MB in ten seconds).
    ///
    /// Defaults to true, which is the SAFE direction: an unflagged caller behaves exactly as before and
    /// pushes everything. Under-sending would cost a joiner their troops; over-sending only costs bandwidth.
    /// </remarks>
    public readonly bool IsPlayerJoin;

    public MapEventInvolvedPartiesAdded(
        MapEvent mapEvent, IEnumerable<MapEventParty> addedParties, bool isPlayerJoin = true)
    {
        MapEvent = mapEvent;
        AddedParties = addedParties;
        IsPlayerJoin = isPlayerJoin;
    }
}
