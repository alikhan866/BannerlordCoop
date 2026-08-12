using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.MapEvents.Loot;
using GameInterface.Services.MapEvents.Messages.Loot;
using GameInterface.Services.ObjectManager;
using LiteNetLib;
using Serilog;
using TaleWorlds.CampaignSystem.Party;
using System;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>
/// Owns the server side of the battle-spoils transaction: what was offered, and whether an answer may be paid.
/// </summary>
/// <remarks>
/// The shape is deliberate. A client cannot be allowed to describe its own winnings - client-side roster
/// writes are not even published, and a description-based answer would let one swap a plain sword for a lordly
/// one. So the server keeps the offer, the client answers with line indices, and this handler is the only
/// place the two meet.
///
/// Refusals are answered rather than dropped. A client that never hears back cannot tell "refused" from "lost
/// in flight", and its loot screen would sit open forever; a reply lets it close and, at M4, reconcile.
///
/// Expiry runs on wall clock, not campaign time: the world is paused precisely while a player reads a loot
/// screen, so a campaign clock would never advance and the offer would never age out.
/// </remarks>
internal class BattleLootTransactionHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleLootTransactionHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;

    /// <summary>
    /// The offers this server currently owes an answer on.
    /// </summary>
    /// <remarks>
    /// The shared instance, because the other end of the transaction lives in a different handler:
    /// MapEventResultsHandler builds and registers an offer, this one answers it, and the container
    /// constructs both independently so neither can hold the other's object.
    /// </remarks>
    internal BattleLootOfferRegistry Offers => BattleLootOfferRegistry.Shared;

    public BattleLootTransactionHandler(IMessageBroker messageBroker, INetwork network, IObjectManager objectManager)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;

        messageBroker.Subscribe<NetworkBattleLootResult>(Handle_NetworkBattleLootResult);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkBattleLootResult>(Handle_NetworkBattleLootResult);
    }

    /// <summary>Seconds on a monotonic-enough wall clock. See the remarks on why this is not campaign time.</summary>
    internal static double NowSeconds() => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    /// <summary>
    /// Whether an accepted result is actually paid out yet.
    /// </summary>
    /// <remarks>
    /// ON. It was false while the old award paths still existed - the server's mirror and the client's
    /// rescue - because paying a result on top of those would have handed the same spoils over twice. Both
    /// were deleted in the same change that set this true, which is the only ordering that never pays twice
    /// and never pays zero times.
    ///
    /// Kept as a named constant rather than deleted: it is the single place to turn payouts off if the
    /// transaction ever has to be disabled in a hurry, and it names the invariant - exactly one path may
    /// credit a player's spoils.
    /// </remarks>
    internal const bool AppliesResults = true;

    private void Handle_NetworkBattleLootResult(MessagePayload<NetworkBattleLootResult> payload)
    {
        if (ModInformation.IsClient) return;

        var data = payload.What;
        var offerId = data.Result.OfferId;
        var now = NowSeconds();

        Offers.PurgeExpired(now);

        if (!Offers.TryClaim(offerId, data.PartyId, now, out var offer, out var refusal))
        {
            Logger.Warning(
                "[Loot] Refused a loot result for offer {Offer} from party {Party}: {Refusal}",
                offerId, data.PartyId, refusal);
            Reply(payload.Who, offerId, data.PartyId, false, refusal.ToString(), default);
            return;
        }

        if (!BattleLootValidator.TryValidate(offer, data.Result, out var resolved, out var rejection))
        {
            // The offer is already marked resolved by TryClaim and is NOT reopened. A client that sends a
            // malformed answer does not get to try again with a better one - reopening would turn validation
            // into something to probe against until it passes.
            Logger.Warning(
                "[Loot] Rejected the loot result for offer {Offer} from party {Party}: {Rejection}",
                offerId, data.PartyId, rejection);
            Reply(payload.Who, offerId, data.PartyId, false, rejection.ToString(), default);
            return;
        }

        if (!objectManager.TryGetObject<MobileParty>(data.PartyId, out var party) || party == null)
        {
            // The party can genuinely be gone by now - destroyed in the very battle that produced the loot,
            // or disbanded while the screen sat open. There is nothing to pay it into.
            Logger.Warning(
                "[Loot] Party {Party} no longer exists; offer {Offer} cannot be paid out", data.PartyId, offerId);
            Reply(payload.Who, offerId, data.PartyId, false, "PartyMissing", default);
            return;
        }

        // Capacity is measured HERE, on the server, immediately before applying. The player's screen showed
        // them a party that may since have changed, and a limit asserted by a client is a limit a client can lie about.
        var plan = BattleLootPlanner.Plan(resolved, BattleLootApplier.MeasureCapacity(party));

        if (!AppliesResults)
        {
            Logger.Information(
                "[Loot] Validated {Claims} claim(s) for offer {Offer} from party {Party} but did not pay out; " +
                "the old award paths are still live (see AppliesResults)",
                resolved.Count, offerId, data.PartyId);
            Reply(payload.Who, offerId, data.PartyId, false, "NotYetApplied", default);
            return;
        }

        var snapshot = new BattleLootApplier(objectManager).Apply(party, plan);

        if (plan.ClampedMembers > 0 || plan.ClampedPrisoners > 0 || plan.HeroesWithoutRoom.Count > 0)
        {
            Logger.Information(
                "[Loot] Offer {Offer} clamped to what {Party} could hold: {Members} member(s) and " +
                "{Prisoners} prisoner(s) left behind, {Heroes} hero(es) with no room",
                offerId, data.PartyId, plan.ClampedMembers, plan.ClampedPrisoners, plan.HeroesWithoutRoom.Count);
        }

        Logger.Information(
            "[Loot] Applied {Claims} claim(s) for offer {Offer} to party {Party}",
            resolved.Count, offerId, data.PartyId);

        Reply(payload.Who, offerId, data.PartyId, true, string.Empty, snapshot);
    }

    private void Reply(
        object who,
        string offerId,
        string partyId,
        bool applied,
        string refusal,
        BattleLootRosterSnapshot snapshot)
    {
        var message = new NetworkBattleLootApplied(offerId, partyId, applied, refusal, snapshot);

        // Answer the peer that asked when we know which one it was; otherwise tell everyone, since a client
        // waiting on a reply it never receives is stuck on its loot screen.
        if (who is NetPeer peer)
        {
            network.Send(peer, message);
            return;
        }

        network.SendAll(message);
    }
}
