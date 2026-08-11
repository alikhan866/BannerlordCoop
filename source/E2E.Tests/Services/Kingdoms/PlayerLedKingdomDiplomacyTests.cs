using GameInterface.Services.Kingdoms.Patches;
using Xunit;

namespace E2E.Tests.Services.Kingdoms;

/// <summary>
/// Leaving a player-led kingdom's wars and peaces to the player who leads it.
/// </summary>
/// <remarks>
/// In single player a ruler makes these calls from the diplomacy screen and the kingdom does not go around
/// them. The co-op server runs every kingdom's AI and cannot tell which thrones have people on them, so
/// <c>KingdomDecisionProposalBehavior</c> kept proposing wars and peaces for a player's own kingdom, and they
/// resolved without the ruler ever being asked.
///
/// Only the AI's PROPOSALS are refused. The ruler's own diplomacy screen creates its decisions from different
/// call sites and is untouched, and war can still arrive by rebellion, crime rating, a call to war, or plain
/// hostility in the field - those follow from things that actually happened.
/// </remarks>
public class PlayerLedKingdomDiplomacyTests
{
    [Fact]
    public void AKingdomWithNoLeaderIsNotTreatedAsPlayerLed()
    {
        // A kingdom mid-collapse can have no leader at all. That must read as "not a player's", so the AI
        // keeps running it rather than the kingdom silently losing all diplomacy.
        Assert.False(PlayerLedKingdomDiplomacyPatch.IsLedByAPlayer(null));
    }

    [Fact]
    public void ClientsNeverSuppress()
    {
        // The kingdom AI runs on the server, and the server is the only machine that knows which heroes are
        // players. A client suppressing here would diverge from the authoritative campaign for no benefit.
        //
        // Under test there is no server context, so this also pins the safe default: when we cannot tell,
        // vanilla stays in charge.
        Assert.False(PlayerLedKingdomDiplomacyPatch.Suppress(null));
    }
}
