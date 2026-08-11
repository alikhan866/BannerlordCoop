using Common.Messaging;

namespace GameInterface.Services.MapEvents.Messages;

/// <summary>
/// [Server, local] A real-time heartbeat for battles that are being fought right now, so reinforcement can be
/// reconsidered while a battle is in progress.
/// </summary>
/// <remarks>
/// <c>CampaignTick</c> cannot do this job. The moment every connected player is in a battle,
/// <c>PlayerOccupancyPauseHandler</c> pauses the campaign - so campaign time stops, campaign ticks stop, and the
/// only reinforcement scan a battle ever gets is the single one fired when it started. A lord who was a moment
/// too far away when the fight began therefore never joined it, and was seen piling in the instant the battle
/// ended and the map resumed.
///
/// Published from a <c>Campaign.RealTick</c> postfix, which keeps running while the campaign is paused.
/// </remarks>
internal readonly struct LiveBattleReinforcementTick : IEvent
{
}
