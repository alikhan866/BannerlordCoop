using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using Common.Util;
using GameInterface.Services.MapEvents.Loot;
using GameInterface.Services.MapEvents.Messages.Loot;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.PlayerCaptivityService.Messages;
using Serilog;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>
/// The client half of the spoils transaction: hold the offer, answer it with what the player took.
/// </summary>
/// <remarks>
/// The answer is derived from what is LEFT on the encounter's staged rosters, never from the party. Vanilla's
/// loot screen removes from those staged rosters as the player takes things, and nothing else writes to them -
/// whereas the party is live, so a replicated update landing while the screen is open would be read as loot
/// the player chose.
/// </remarks>
internal class BattleLootClientHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleLootClientHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly INetwork network;
    private readonly IObjectManager objectManager;

    private List<TroopRosters.Data.TroopRosterElementData> declinedMembers;
    private List<TroopRosters.Data.TroopRosterElementData> declinedPrisoners;

    // Heroes the player FREED while this offer was open, keyed by CharacterObject id to match the offer lines.
    // A release is an action, not an absence: the staged roster looks identical whether a lord was freed or
    // ignored, so unless the choice is remembered here it cannot be told apart later and the lord is imprisoned.
    private readonly HashSet<string> releasedHeroes = new HashSet<string>();

    public BattleLootClientHandler(IMessageBroker messageBroker, INetwork network, IObjectManager objectManager)
    {
        this.messageBroker = messageBroker;
        this.network = network;
        this.objectManager = objectManager;

        messageBroker.Subscribe<NetworkBattleLootOffer>(Handle_Offer);
        messageBroker.Subscribe<NetworkBattleLootApplied>(Handle_Applied);
        messageBroker.Subscribe<EndCaptivityAttempted>(Handle_ReleaseAttempted);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkBattleLootOffer>(Handle_Offer);
        messageBroker.Unsubscribe<NetworkBattleLootApplied>(Handle_Applied);
        messageBroker.Unsubscribe<EndCaptivityAttempted>(Handle_ReleaseAttempted);
    }

    /// <summary>
    /// Remembers a hero the player freed while an offer is open.
    /// </summary>
    /// <remarks>
    /// The client already intercepts every local release here (EndCaptivityActionPatches turns it into this
    /// message rather than applying it), so this is the one place the choice is knowable before the encounter
    /// throws its staged rosters away.
    ///
    /// Only while an offer is PENDING. A release at any other time - a ransom, a peace treaty, a prisoner let
    /// go from the party screen - has its own path and must not be folded into an unrelated battle's spoils.
    /// </remarks>
    private void Handle_ReleaseAttempted(MessagePayload<EndCaptivityAttempted> payload)
    {
        if (ModInformation.IsServer) return;
        if (!ClientBattleLootOffer.HasPending) return;

        var character = payload.What.Prisoner?.CharacterObject;
        if (character == null) return;
        if (!objectManager.TryGetId(character, out var characterId) || string.IsNullOrEmpty(characterId)) return;

        releasedHeroes.Add(characterId);

        Logger.Information("[Loot] The player freed {Hero} while an offer was open", characterId);
    }

    private void Handle_Offer(MessagePayload<NetworkBattleLootOffer> payload)
    {
        if (ModInformation.IsServer) return;

        // A new battle's spoils start from nothing. Carrying a release across offers would free a lord in a
        // battle the player never fought him in.
        releasedHeroes.Clear();

        var offer = payload.What.Offer;

        // An offer with nothing in it is answered here and now, and never becomes pending. Otherwise it
        // deadlocks: the finalize handler holds the teardown open while an offer is outstanding, the answer
        // is sent when the encounter finishes, and an empty offer opens no loot screen for the player to
        // click through - so nothing would ever finish the encounter that the answer was waiting on. This is
        // not hypothetical; a battle won by killing every enemy outright yields zero prisoners.
        if (offer.IsEmpty)
        {
            var partyId = string.Empty;
            var mainParty = MobileParty.MainParty;
            if (mainParty != null) objectManager.TryGetId(mainParty, out partyId);

            network.SendAll(new NetworkBattleLootResult(
                partyId, BattleLootSelection.FromRemaining(offer, null)));

            Logger.Information(
                "[Loot] Offer {Offer} for {MapEvent} was empty; answered immediately so the encounter can close",
                offer.OfferId, offer.MapEventId);
            return;
        }

        ClientBattleLootOffer.Remember(offer);

        Logger.Information(
            "[Loot] Offered {Lines} line(s) for {MapEvent} (offer {Offer})",
            offer.Lines?.Length ?? 0, offer.MapEventId, offer.OfferId);
    }

    private void Handle_Applied(MessagePayload<NetworkBattleLootApplied> payload)
    {
        if (ModInformation.IsServer) return;

        var data = payload.What;

        if (!data.Applied)
        {
            // Nothing was paid out. While the changeover is in progress the server answers "NotYetApplied"
            // and the old award paths are still what actually credit the player, so the local rosters are
            // already correct - overwriting them from an empty snapshot would take the loot away.
            Logger.Information(
                "[Loot] Offer {Offer} was not applied ({Refusal}); leaving the local rosters as they are",
                data.OfferId, data.Refusal);
            return;
        }

        // TODO (M6): apply data.Snapshot as absolute state once the server actually pays out. It is deliberately
        // NOT applied while AppliesResults is false, because the snapshot then describes a party the server has
        // credited by the old mirror rather than by this transaction, and the two are not the same thing yet.
        Logger.Information("[Loot] Offer {Offer} applied by the server", data.OfferId);
    }

    /// <summary>
    /// Answers the outstanding offer using whatever the player left on the staged rosters.
    /// </summary>
    /// <remarks>
    /// Called from the PlayerEncounter.Finish prefix, which is the moment those rosters hold exactly what was
    /// declined - the screen has run, and the encounter has not yet thrown them away.
    /// </remarks>
    public void AnswerOutstandingOffer(PlayerEncounter encounter)
    {
        if (ModInformation.IsServer) return;
        if (!ClientBattleLootOffer.TryTake(out var offer)) return;

        var partyId = string.Empty;
        var mainParty = MobileParty.MainParty;
        if (mainParty != null) objectManager.TryGetId(mainParty, out partyId);

        // Troops and prisoners come from the snapshot taken as the party screen closed, because the rosters
        // themselves are cleared at that moment and would otherwise read as "all taken". Items come from the
        // live roster, which nothing clears, so it still holds exactly what was left behind.
        var members = declinedMembers ?? PackTroops(encounter?.RosterToReceiveLootMembers);
        var prisoners = declinedPrisoners ?? PackTroops(encounter?.RosterToReceiveLootPrisoners);
        declinedMembers = null;
        declinedPrisoners = null;

        var remaining = BattleLootLineMatcher.CountRemaining(
            offer,
            PackItems(encounter?.RosterToReceiveLootItems),
            members,
            prisoners);

        // The releases are what turn "left on the roster" into "deliberately freed" - without them every hero
        // line answers as Keep and a lord the player let go is imprisoned by the server anyway.
        var dispositions = BattleLootSelection.DispositionsFor(offer, releasedHeroes);
        var result = BattleLootSelection.FromRemaining(offer, remaining, dispositions);

        int releaseCount = releasedHeroes.Count;
        releasedHeroes.Clear();

        network.SendAll(new NetworkBattleLootResult(partyId, result));

        Logger.Information(
            "[Loot] Answered offer {Offer} for {MapEvent} with {Claims} claim(s), {Released} release(s)",
            offer.OfferId, offer.MapEventId, result.Claims?.Length ?? 0, releaseCount);
    }

    /// <summary>
    /// Records which troops and prisoners the player left behind, at the one instant that is knowable.
    /// </summary>
    /// <remarks>
    /// Called just before <c>PlayerEncounter.OnPlayerLootMembersAndPrisonerEnd</c>, which CLEARS both staged
    /// rosters as the party screen closes - whatever the player did or did not take. Read any later, the
    /// rosters are empty, and empty is what the answer reads as "the player took all of it". That is why
    /// prisoners the player deliberately left were still turning up in their dungeon: the choice was made and
    /// then erased a frame afterwards, and nothing downstream could tell.
    ///
    /// Items need no equivalent. Nothing clears <c>RosterToReceiveLootItems</c>, so what is left on it at the
    /// end really is what was declined.
    /// </remarks>
    public void CaptureDeclinedTroops(PlayerEncounter encounter)
    {
        if (ModInformation.IsServer) return;
        if (!ClientBattleLootOffer.HasPending) return;

        declinedMembers = PackTroops(encounter?.RosterToReceiveLootMembers);
        declinedPrisoners = PackTroops(encounter?.RosterToReceiveLootPrisoners);

        Logger.Information(
            "[Loot] The player left {Members} member(s) and {Prisoners} prisoner(s) on the loot screen",
            declinedMembers.Count, declinedPrisoners.Count);
    }

    /// <summary>
    /// Settles an offer the player was never shown, paying it out rather than forfeiting it.
    /// </summary>
    /// <remarks>
    /// Reached when the encounter closes before any step of the walk opened a screen or a conversation. The
    /// ordinary answer cannot be used: it reads what is left on the staged rosters, and nothing having been
    /// taken from them means "never asked", not "declined".
    ///
    /// This still goes through the offer as a normal answer, so the server validates, plans and applies it the
    /// same way as a real one - there is no second route by which loot can move. It is deliberately loud: a
    /// payout nobody chose is a symptom, and the line below is what says which battle it happened in.
    /// </remarks>
    public void SettleUnshownOffer()
    {
        if (ModInformation.IsServer) return;
        if (!ClientBattleLootOffer.TryTake(out var offer)) return;

        var partyId = string.Empty;
        var mainParty = MobileParty.MainParty;
        if (mainParty != null) objectManager.TryGetId(mainParty, out partyId);

        // Never shown means the player chose nothing at all, so any release recorded against this offer is
        // not theirs to have made.
        releasedHeroes.Clear();

        var result = BattleLootAbandonPolicy.AnswerFor(offer, BattleLootAbandonReason.NeverShown);

        network.SendAll(new NetworkBattleLootResult(partyId, result));

        Logger.Warning(
            "[Loot] The encounter for {MapEvent} closed before the player was shown anything, so offer {Offer} " +
            "({Lines} line(s)) is being paid out in full rather than forfeited. The loot screens did not run - " +
            "that is the bug; this only stops it costing the battle's spoils",
            offer.MapEventId, offer.OfferId, offer.Lines?.Length ?? 0);
    }

    private List<BattleLootItemStack> PackItems(ItemRoster roster)
    {
        var stacks = new List<BattleLootItemStack>();
        if (roster == null) return stacks;

        foreach (var element in roster)
        {
            var item = element.EquipmentElement.Item;
            if (item == null || element.Amount <= 0) continue;
            if (!objectManager.TryGetId(item, out var itemId)) continue;

            string modifierId = null;
            var modifier = element.EquipmentElement.ItemModifier;
            if (modifier != null) objectManager.TryGetId(modifier, out modifierId);

            stacks.Add(new BattleLootItemStack(itemId, modifierId, element.Amount));
        }

        return stacks;
    }

    private List<TroopRosters.Data.TroopRosterElementData> PackTroops(TroopRoster roster)
    {
        var packed = new List<TroopRosters.Data.TroopRosterElementData>();
        if (roster == null) return packed;

        for (int i = 0; i < roster.Count; i++)
        {
            var element = roster.GetElementCopyAtIndex(i);
            if (element.Character == null || element.Number <= 0) continue;
            if (!objectManager.TryGetId(element.Character, out var characterId)) continue;

            packed.Add(new TroopRosters.Data.TroopRosterElementData(
                characterId, element.Number, element.WoundedNumber, element.Xp));
        }

        return packed;
    }
}
