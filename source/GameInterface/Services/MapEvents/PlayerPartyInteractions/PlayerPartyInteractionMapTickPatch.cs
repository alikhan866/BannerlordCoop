using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem.GameState;

namespace GameInterface.Services.MapEvents.PlayerPartyInteractions;

/// <summary>
/// A map tick that keeps running while the campaign clock is stopped.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS. Opening a player-to-player map conversation cannot always happen the moment the server
/// says so: <c>CanOpenMapConversation</c> refuses while the player is at a game menu, and the player who
/// STARTS the conversation is always at one - the encounter menu they just clicked "Talk to army leader" in.
/// So the open is retried until the menu closes.
/// </para>
/// <para>
/// That retry used to be driven by <c>CampaignEvents.TickEvent</c>, which does not fire while the game is
/// paused. From the engine, <c>Campaign.Tick</c> reads:
/// </para>
/// <code>
/// if (_dt > 0f || CurrentTickCount &lt; 3)
///     CampaignEventDispatcher.Instance.Tick(_dt);   // raises TickEvent
/// </code>
/// <para>
/// and <c>TickMapTime</c> leaves <c>_dt</c> at zero for <c>CampaignTimeControlMode.Stop</c>. So a player who
/// started a conversation with the game paused waited forever: their own screen never advanced, while the
/// other player - who is not at a menu, so opened on the first try - sat on "Awaiting proposal from ...".
/// Unpausing released it, which is what made this look like "I have to start time for barter to work".
/// </para>
/// <para>
/// <c>MapState.OnMapModeTick</c> is driven by the game state machine rather than campaign time, so it ticks
/// whether or not the clock is running. It is also a strictly BETTER source than the campaign tick for this
/// job, not merely an additional one: it only fires while the map state is active, which is precisely the
/// condition <c>CanOpenMapConversation</c> already requires.
/// </para>
/// </remarks>
[HarmonyPatch(typeof(MapState), "OnMapModeTick")]
internal static class PlayerPartyInteractionMapTickPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<PlayerPartyInteractionHandler>();

    /// <summary>Raised once per map frame, paused or not.</summary>
    public static event Action MapTicked;

    /// <remarks>
    /// Swallows exceptions on purpose. This runs inside a native-driven state tick, so letting one escape
    /// would take the map tick down with it - and the subscriber here is a best-effort retry whose whole
    /// job is to do nothing until conditions are right.
    /// </remarks>
    [HarmonyPostfix]
    private static void Postfix()
    {
        var handlers = MapTicked;
        if (handlers == null) return;

        try
        {
            handlers();
        }
        catch (Exception e)
        {
            Logger.Warning(e, "A player-party map tick subscriber threw");
        }
    }
}
