using Common.Messaging;

namespace GameInterface.Services.MapEvents.Messages;

/// <summary>
/// [Server, local] Reinforcements have changed a battle's strength enough that the round should be re-formed
/// around the new totals.
/// </summary>
/// <remarks>
/// Raised by <c>BattleRoundRestartHandler</c>, which watches the totals; acted on by <c>BattleHostHandler</c>,
/// which owns the reserve ledger and knows every participant's peer. Split that way on purpose: deciding
/// whether a restart is warranted is a rule about troop counts and belongs with the other map-event rules,
/// while rebuilding and re-sending the reserves needs the battle's runtime state.
/// </remarks>
internal readonly struct BattleRoundRestartRequested : IEvent
{
    public readonly string MapEventId;

    /// <summary>The side totals the round is being re-sized to, for logging and for the ledger's benefit.</summary>
    public readonly int DefenderTotal;
    public readonly int AttackerTotal;

    public BattleRoundRestartRequested(string mapEventId, int defenderTotal, int attackerTotal)
    {
        MapEventId = mapEventId;
        DefenderTotal = defenderTotal;
        AttackerTotal = attackerTotal;
    }
}
