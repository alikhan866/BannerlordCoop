using GameInterface.Services.MapEvents.PlayerPartyInteractions;
using System;
using TaleWorlds.CampaignSystem.Inventory;
using Xunit;

namespace GameInterface.Tests.Services.MapEvents;

public class PlayerPartyTradeContextTests : IDisposable
{
    public void Dispose()
    {
        PlayerPartyTradeContext.End();
    }

    [Fact]
    public void CanTransfer_AllowsBothSidesWhenInactive()
    {
        Assert.True(PlayerPartyTradeContext.CanTransfer(new TransferCommand
        {
            FromSide = InventoryLogic.InventorySide.PlayerInventory
        }));
        Assert.True(PlayerPartyTradeContext.CanTransfer(new TransferCommand
        {
            FromSide = InventoryLogic.InventorySide.OtherInventory
        }));
    }

    [Fact]
    public void CanTransfer_BlocksOpposingSideWhenActive()
    {
        PlayerPartyTradeContext.Begin("session-1");

        Assert.True(PlayerPartyTradeContext.CanTransfer(new TransferCommand
        {
            FromSide = InventoryLogic.InventorySide.PlayerInventory
        }));
        Assert.False(PlayerPartyTradeContext.CanTransfer(new TransferCommand
        {
            FromSide = InventoryLogic.InventorySide.OtherInventory
        }));
    }

    [Fact]
    public void CanTransfer_AllowsLocalTransfersAfterEitherPlayerAccepted()
    {
        PlayerPartyTradeContext.Begin("session-1");
        PlayerPartyTradeContext.UpdateAcceptance(localAccepted: false, remoteAccepted: true);

        Assert.True(PlayerPartyTradeContext.CanTransfer(new TransferCommand
        {
            FromSide = InventoryLogic.InventorySide.PlayerInventory
        }));

        PlayerPartyTradeContext.UpdateAcceptance(localAccepted: true, remoteAccepted: false);

        Assert.True(PlayerPartyTradeContext.CanTransfer(new TransferCommand
        {
            FromSide = InventoryLogic.InventorySide.PlayerInventory
        }));
    }

    /// <summary>
    /// Accepting twice is refused; LEAVING is not.
    /// </summary>
    /// <remarks>
    /// This test previously also asserted <c>CanCancel()</c> was false here, which made the deadlock
    /// deliberate: Accept and Cancel shared the single condition <c>!LocalAccepted</c>, so pressing Accept
    /// closed both doors out of a modal screen at once. That assertion is reversed on purpose - see
    /// <see cref="Cancelling_is_still_allowed_after_this_player_has_accepted"/> for why leaving has to stay
    /// possible. The half that was right is kept: re-sending an acceptance the server already holds does
    /// nothing, so the Accept button stays closed.
    /// </remarks>
    [Fact]
    public void CanAccept_BlocksRepeatAcceptAfterLocalAccept()
    {
        PlayerPartyTradeContext.Begin("session-1");

        Assert.True(PlayerPartyTradeContext.CanAccept());

        PlayerPartyTradeContext.UpdateAcceptance(localAccepted: true, remoteAccepted: false);

        Assert.False(PlayerPartyTradeContext.CanAccept());
        Assert.True(PlayerPartyTradeContext.CanCancel());
    }

    [Fact]
    public void CanReset_BlocksResetWhileActive()
    {
        Assert.True(PlayerPartyTradeContext.CanReset());

        PlayerPartyTradeContext.Begin("session-1");

        Assert.False(PlayerPartyTradeContext.CanReset());
    }

    [Fact]
    public void CanOffer_BlocksUnknownBarterableWhenActive()
    {
        Assert.True(PlayerPartyTradeContext.CanOffer(null));

        PlayerPartyTradeContext.Begin("session-1");

        Assert.False(PlayerPartyTradeContext.CanOffer(null));
    }

    // ---- leaving a trade is always possible ------------------------------------------------

    [Fact]
    public void Cancelling_is_allowed_before_accepting()
    {
        PlayerPartyTradeContext.Begin("session-1");

        Assert.True(PlayerPartyTradeContext.CanCancel());
    }

    /// <summary>
    /// The reported bug: accepting must not take away the only remaining exit.
    /// </summary>
    /// <remarks>
    /// With Accept and Cancel sharing a condition, recovery depended entirely on a later server state
    /// message clearing the acceptance. When that did not arrive - the other player sat on the screen, the
    /// session ended in a way this client never saw, or the message queued behind a flood of offer updates -
    /// the player was stuck in a modal barter screen with no working control, and the only way out was
    /// killing the game. That was reported from live play as "I can't click Done or Cancel, and both players
    /// have to restart their clients".
    ///
    /// A late leave is safe on the server: it either ends a session that is still open, or names one already
    /// removed, which ProcessSubmittedOption ignores and logs. So refusing it protects nothing.
    /// </remarks>
    [Fact]
    public void Cancelling_is_still_allowed_after_this_player_has_accepted()
    {
        PlayerPartyTradeContext.Begin("session-2");
        PlayerPartyTradeContext.UpdateAcceptance(localAccepted: true, remoteAccepted: false);

        Assert.True(PlayerPartyTradeContext.LocalAccepted);
        Assert.True(
            PlayerPartyTradeContext.CanCancel(),
            "a player who has accepted must still be able to leave the trade");
    }

    [Fact]
    public void Cancelling_is_allowed_when_both_sides_have_accepted()
    {
        PlayerPartyTradeContext.Begin("session-3");
        PlayerPartyTradeContext.UpdateAcceptance(localAccepted: true, remoteAccepted: true);

        Assert.True(PlayerPartyTradeContext.CanCancel());
    }

    [Fact]
    public void Accepting_reopens_once_the_server_clears_the_acceptance()
    {
        PlayerPartyTradeContext.Begin("session-5");
        PlayerPartyTradeContext.UpdateAcceptance(localAccepted: true, remoteAccepted: false);
        PlayerPartyTradeContext.UpdateAcceptance(localAccepted: false, remoteAccepted: false);

        Assert.True(PlayerPartyTradeContext.CanAccept());
        Assert.True(PlayerPartyTradeContext.CanCancel());
    }

    [Fact]
    public void Ending_a_session_clears_acceptance()
    {
        PlayerPartyTradeContext.Begin("session-6");
        PlayerPartyTradeContext.UpdateAcceptance(localAccepted: true, remoteAccepted: true);

        PlayerPartyTradeContext.End("session-6");

        Assert.False(PlayerPartyTradeContext.IsActive);
        Assert.False(PlayerPartyTradeContext.LocalAccepted);
        Assert.True(PlayerPartyTradeContext.CanAccept());
        Assert.True(PlayerPartyTradeContext.CanCancel());
    }

    /// <summary>
    /// An End naming a different session must not tear down the live one.
    /// </summary>
    /// <remarks>
    /// Two sessions can overlap around a hand-off, and a late teardown message for the previous one would
    /// otherwise cancel the trade the player is currently looking at.
    /// </remarks>
    [Fact]
    public void Ending_a_different_session_leaves_the_active_one_alone()
    {
        PlayerPartyTradeContext.Begin("session-7");

        PlayerPartyTradeContext.End("some-other-session");

        Assert.True(PlayerPartyTradeContext.IsActive);
    }
}
