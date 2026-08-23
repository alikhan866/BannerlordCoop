using Common;
using Common.Logging;
using Common.Messaging;
using Common.Network;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.MapEventParties;
using GameInterface.Services.MapEvents.Patches;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Party.Data;
using GameInterface.Services.Party.Messages;
using GameInterface.Services.PlayerCaptivityService.Messages;
using GameInterface.Services.TroopRosters.Data;
using GameInterface.Services.TroopRosters.Interfaces;
using GameInterface.Services.TroopRosters.Messages;
using GameInterface.Services.UI.Notifications.Messages;
using LiteNetLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace GameInterface.Services.Party.Handlers;

internal class PartyDoneLogicHandler : IHandler
{
    private const string PartyChangedMessage =
        "The party changed before these edits were applied. Reopen the party screen and try again.";

    private const string PartyPartiallyAppliedMessage =
        "The party changed while you were editing it, so some troops were no longer there. Your changes were applied to what remained.";

    private static readonly ILogger logger = LogManager.GetLogger<PartyDoneLogicHandler>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly INetwork network;
    private readonly ITroopRosterInterface troopRosterInterface;

    public PartyDoneLogicHandler(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        INetwork network,
        ITroopRosterInterface troopRosterInterface)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.network = network;
        this.troopRosterInterface = troopRosterInterface;

        messageBroker.Subscribe<PartyDoneLogicAttempted>(Handle_PartyDoneLogicAttempted);
        messageBroker.Subscribe<NetworkCompleteDoneLogic>(Handle_CompletePartyDoneLogic);
        messageBroker.Subscribe<NetworkPartyRosterResync>(Handle_PartyRosterResync);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PartyDoneLogicAttempted>(Handle_PartyDoneLogicAttempted);
        messageBroker.Unsubscribe<NetworkCompleteDoneLogic>(Handle_CompletePartyDoneLogic);
        messageBroker.Unsubscribe<NetworkPartyRosterResync>(Handle_PartyRosterResync);
    }

    // Client
    private void Handle_PartyDoneLogicAttempted(MessagePayload<PartyDoneLogicAttempted> obj)
    {
        if (!objectManager.TryGetIdWithLogging(obj.What.MainHero, out var mainHeroId)) return;

        string leftPartyId = null;
        if (obj.What.LeftParty != null && 
            !objectManager.TryGetIdWithLogging(obj.What.LeftParty, out leftPartyId))
            return;

        // Not registered when donating
        objectManager.TryGetId(obj.What.LeftPrisonerRoster, out var leftPrisonerRosterId);

        var upgradedTroopHistory = new UpgradedTroopHistoryData(new());
        foreach (Tuple<CharacterObject, CharacterObject, int> tuple in obj.What.UpgradedTroopHistory)
        {
            if (!objectManager.TryGetIdWithLogging(tuple.Item1, out var character1Id)) continue;
            if (!objectManager.TryGetIdWithLogging(tuple.Item2, out var character2Id)) continue;

            upgradedTroopHistory.Data.Add(new(character1Id, character2Id, tuple.Item3));
        }

        // Send only the per-troop change the player made (current minus the screen-open snapshot). Heroes and
        // companions that did not change net to zero and are omitted, so the server needs no special handling
        // for them when re-applying the delta.
        var leftMemberRosterData = troopRosterInterface.PackTroopRosterDelta(obj.What.LeftMemberRoster, obj.What.InitialLeftMemberRoster);
        var leftPrisonerRosterData = troopRosterInterface.PackTroopRosterDelta(obj.What.LeftPrisonerRoster, obj.What.InitialLeftPrisonerRoster);
        var rightMemberRosterData = troopRosterInterface.PackTroopRosterDelta(obj.What.RightMemberRoster, obj.What.InitialRightMemberRoster);
        var rightPrisonerRosterData = troopRosterInterface.PackTroopRosterDelta(obj.What.RightPrisonerRoster, obj.What.InitialRightPrisonerRoster);

        var rightMemberOrderData = troopRosterInterface.PackTroopRosterOrderData(obj.What.RightMemberRoster);

        var releaserPartyPosition = GetReleaserPartyPosition(obj.What.MainHero);

        string donationSettlementId = null;
        FlattenedTroop[] donatedPrisonersRoster = null;
        if (obj.What.DonationSettlement != null)
        {
            if (!objectManager.TryGetIdWithLogging(obj.What.DonationSettlement, out donationSettlementId)) return;
            donatedPrisonersRoster = FlattenedTroopSerializer.Serialize(
                obj.What.DonatedPrisonersRoster,
                objectManager);
        }

        var message = new NetworkCompleteDoneLogic(
            mainHeroId,
            FlattenedTroopSerializer.Serialize(obj.What.ReleasedPrisonersRoster, objectManager),
            FlattenedTroopSerializer.Serialize(obj.What.TakenPrisonersRoster, objectManager),
            FlattenedTroopSerializer.Serialize(obj.What.RecruitedPrisonersRoster, objectManager),
            leftMemberRosterData,
            leftPrisonerRosterData,
            rightMemberRosterData,
            rightPrisonerRosterData,
            obj.What.RightOwnerPartyItemRoster._data,
            upgradedTroopHistory,
            leftPartyId,
            leftPrisonerRosterId,
            obj.What.PartyGoldChangeAmount,
            obj.What.PartyInfluenceChangeAmount,
            obj.What.PartyMoraleChangeAmount,
            obj.What.DoNotApplyGoldTransactions,
            releaserPartyPosition,
            obj.What.PartyScreenMode,
            rightMemberOrderData,
            obj.What.ApplyReleasedAndTakenPrisonerActions,
            donationSettlementId,
            donatedPrisonersRoster
        );

        network.SendAll(message);
    }

    private static CampaignVec2 GetReleaserPartyPosition(Hero mainHero)
    {
        var releaserParty = mainHero.PartyBelongedTo;
        if (releaserParty?.CurrentSettlement != null)
            return releaserParty.CurrentSettlement.GatePosition;

        if (releaserParty != null)
            return releaserParty.Position;

        return MobileParty.MainParty.Position;
    }

    // Server
    private void Handle_CompletePartyDoneLogic(MessagePayload<NetworkCompleteDoneLogic> obj)
    {
        var message = obj.What;
        var requester = obj.Who as NetPeer;
        
        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<Hero>(message.MainHeroId, out var mainHero)) return;

            if (!TryResolveCompleteDoneLogic(
                message,
                out var leftParty,
                out var leftPrisonerRoster,
                out var donationSettlement,
                out var upgradedTroopHistory)) return;

            var releasedPrisonersRoster = FlattenedTroopSerializer.Deserialize(message.ReleasedPrisonersRoster, objectManager);
            var takenPrisonersRoster = FlattenedTroopSerializer.Deserialize(message.TakenPrisonersRoster, objectManager);
            var recruitedPrisonersRoster = FlattenedTroopSerializer.Deserialize(message.RecruitedPrisonersRoster, objectManager);
            var donatedPrisonersRoster = FlattenedTroopSerializer.Deserialize(message.DonatedPrisonersRoster, objectManager);
            var releasedPlayerCaptivityEvents = new List<PlayerCaptivityEndedByServer>();
            var leftPrisonerRosterData = message.LeftPrisonerRosterData;
            var rightPrisonerRosterData = message.RightPrisonerRosterData;
            // Validate the client-reported release/take history against the signed delta before
            // removing player-prisoner releases from the apply delta. Player releases are handled
            // by PlayerCaptivityServerHandler rather than by a roster mutation, so validating the
            // filtered delta would always reject a legitimate dismissal (the -1 entry is gone).
            var signedRightPrisonerRosterData = rightPrisonerRosterData;
            // SellPrisonersHandler owns ransom releases so the same player is not released twice.
            if (message.PartyScreenMode != Helpers.PartyScreenHelper.PartyScreenMode.Ransom)
            {
                releasedPlayerCaptivityEvents = CreatePlayerCaptivityReleaseEvents(
                    message.LeftPrisonerRosterData,
                    message.RightPrisonerRosterData,
                    HasLeftPrisonerTransferDestination(
                        message.ApplyReleasedAndTakenPrisonerActions,
                        leftParty != null,
                        leftPrisonerRoster != null,
                        donationSettlement != null),
                    message.ReleaserPartyPosition,
                    out leftPrisonerRosterData,
                    out rightPrisonerRosterData);
            }
            var takenHeroCharacterIds = new HashSet<string>();
            var actionRostersAreValid =
                !message.ApplyReleasedAndTakenPrisonerActions ||
                TryValidatePrisonerActionRosters(
                    releasedPrisonersRoster,
                    takenPrisonersRoster,
                    signedRightPrisonerRosterData,
                    out takenHeroCharacterIds);
            if (!actionRostersAreValid)
            {
                logger.Error("Rejected Party screen prisoner actions because transfer history did not match the signed right-prisoner delta");
                return;
            }

            if (donationSettlement != null &&
                mainHero.PartyBelongedTo.CurrentSettlement != donationSettlement)
            {
                logger.Warning(
                    "Rejected Party screen prisoner donation because {MainHeroId} is no longer at {SettlementId}",
                    message.MainHeroId,
                    message.DonationSettlementId);
                return;
            }

            if (donationSettlement != null &&
                !TryValidatePrisonerDonationRosters(
                    donatedPrisonersRoster,
                    message.LeftPrisonerRosterData,
                    signedRightPrisonerRosterData))
            {
                logger.Error("Rejected Party screen prisoner donation because the donated roster did not match both signed prisoner deltas");
                return;
            }

            if (donationSettlement != null &&
                !HasPrisonerDonationCapacity(
                    donationSettlement,
                    message.LeftPrisonerRosterData))
            {
                logger.Warning(
                    "Rejected Party screen prisoner donation because {SettlementId} no longer has enough prisoner capacity",
                    message.DonationSettlementId);
                return;
            }

            var applicableTakenPrisonersRoster = FilterIneligibleTakenHeroes(takenPrisonersRoster);
            if (message.ApplyReleasedAndTakenPrisonerActions)
            {
                rightPrisonerRosterData = FilterTakenHeroAdditions(
                    rightPrisonerRosterData,
                    takenHeroCharacterIds);
            }

            bool clampedToActualContents = false;
            var rosterDeltas = CreateRosterDeltas(
                mainHero,
                leftParty,
                leftPrisonerRoster,
                donationSettlement,
                message,
                leftPrisonerRosterData,
                rightPrisonerRosterData);

            // Only apply deltas if not ransoming. SellPrisonersAction already changes troop rosters
            if (message.PartyScreenMode != Helpers.PartyScreenHelper.PartyScreenMode.Ransom)
            {
                if (!troopRosterInterface.TryApplyTroopRosterDeltas(rosterDeltas, out var rosterDeltasAdjusted))
                {
                    logger.Warning(
                        "Rejected party changes for {MainHeroId}: {Reason}",
                        message.MainHeroId,
                        PartyChangedMessage);
                    if (requester != null)
                    {
                        network.Send(requester, new SendInformationMessage(PartyChangedMessage));
                        ResyncRostersWithRequester(requester, rosterDeltas);
                    }
                    return;
                }

                clampedToActualContents = rosterDeltasAdjusted;
            }
            PublishPlayerCaptivityReleaseEvents(releasedPlayerCaptivityEvents);
            ApplyRightOwnerPartyItemRoster(mainHero, message);
            if (message.ApplyReleasedAndTakenPrisonerActions)
                ApplyReleasedAndTakenPrisonerActions(mainHero, releasedPrisonersRoster, applicableTakenPrisonersRoster);
            // The PAYOUT is gated on the batch having applied as asked. A clamped batch moved less than the
            // client requested - a stale or replayed donation moves nothing at all - and the gold and
            // influence in the message were computed by the client for the transfer it THOUGHT it was
            // making. Paying them out anyway is how one donation message awards its influence twice: resend
            // it after the prisoner has gone and the deltas clamp to nothing while the reward still lands.
            //
            // Before the clamp existed this was covered by accident, because an unsatisfiable delta refused
            // the whole batch and returned. Clamping deliberately kept the rest of the batch alive, so the
            // reward needs the guard the early return used to provide. Only the payout and the donation
            // side effects are held back; the roster work above still stands, which is the point of clamping.
            if (!clampedToActualContents)
            {
                if (donationSettlement != null)
                    ApplyPrisonerDonationEffects(mainHero, donationSettlement, donatedPrisonersRoster);
                ApplyPartyRewardChanges(mainHero, message);
            }
            NotifyTakenPrisonersChanged(applicableTakenPrisonersRoster);
            ApplyUpgradedTroopHistory(mainHero, upgradedTroopHistory);
            ApplyPrisonerRecruitmentEffects(mainHero, message, recruitedPrisonersRoster);

            ApplyRosterOrder(mainHero.PartyBelongedTo.MemberRoster, message.RightMemberOrderData);

            // The commit went through, but not as the client drew it - their screen is showing troops that were
            // never moved. Put them back in step rather than leave them editing a fiction. Sent last, because
            // the steps above move troops between these same rosters.
            if (clampedToActualContents && requester != null)
            {
                logger.Warning(
                    "Party changes for {MainHeroId} were clamped to the rosters' actual contents",
                    message.MainHeroId);
                network.Send(requester, new SendInformationMessage(PartyPartiallyAppliedMessage));
                ResyncRostersWithRequester(requester, rosterDeltas);
            }
        });
    }

    /// <summary>
    /// Sends the server's version of every roster this batch touched back to the client it just refused.
    /// </summary>
    /// <remarks>
    /// Without this a refusal is a dead end. The advice the client is given - reopen the party screen and try
    /// again - cannot work on its own: the screen rebuilds its delta from the same client-side roster that
    /// disagreed, so the same batch is sent and refused again. One stale troop stack is enough to block every
    /// discard, ransom and transfer permanently, which is exactly what a player hit.
    ///
    /// Every roster in the batch is sent, not only the one that failed to apply.
    /// <c>TryApplyTroopRosterDeltas</c> is all-or-nothing and stops at the first mismatch, so it does not know
    /// which of the others would also have been wrong - and a resync that fixed only the reported roster could
    /// be refused again on the next one.
    ///
    /// This corrects the SYMPTOM, deliberately. A divergence should not happen, and where one is understood it
    /// is fixed at the source; but the party screen must not become unusable when one does, and a client that
    /// can be put back in step with the server is a far smaller failure than a player locked out of their own
    /// party with no way back.
    /// </remarks>
    private void ResyncRostersWithRequester(
        NetPeer requester,
        List<(TroopRoster roster, TroopRosterData delta)> rosterDeltas)
    {
        foreach (var (roster, _) in rosterDeltas)
        {
            if (roster == null) continue;
            if (!objectManager.TryGetId(roster, out var rosterId))
            {
                // Nothing can be sent for a roster with no id, so the client keeps whatever it had. Say so:
                // silence here looks identical to a successful resync from the client's side.
                logger.Warning(
                    "No id for a roster in this batch, so it cannot be resynced; the client stays out of step with it");
                continue;
            }

            network.Send(requester, new NetworkPartyRosterResync(rosterId, troopRosterInterface.PackTroopRosterData(roster)));
        }
    }

    // Client
    private void Handle_PartyRosterResync(MessagePayload<NetworkPartyRosterResync> obj)
    {
        var message = obj.What;

        GameThread.RunSafe(() =>
        {
            if (!objectManager.TryGetObjectWithLogging<TroopRoster>(message.RosterId, out var roster)) return;

            troopRosterInterface.UpdateWithData(roster, message.RosterData, Hero.MainHero);

            logger.Information(
                "Resynced roster {RosterId} from the server after a refused party edit; it now holds {Count} entries",
                message.RosterId, roster.Count);
        });
    }

    private bool TryResolveCompleteDoneLogic(
        NetworkCompleteDoneLogic message,
        out PartyBase leftParty,
        out TroopRoster leftPrisonerRoster,
        out Settlement donationSettlement,
        out List<Tuple<CharacterObject, CharacterObject, int>> upgradedTroopHistory)
    {
        leftParty = null;
        leftPrisonerRoster = null;
        donationSettlement = null;
        upgradedTroopHistory = null;

        if (message.LeftPartyId != null && !objectManager.TryGetObjectWithLogging<PartyBase>(message.LeftPartyId, out leftParty)) return false;
        if (message.LeftPrisonerRosterId != null && !objectManager.TryGetObjectWithLogging<TroopRoster>(message.LeftPrisonerRosterId, out leftPrisonerRoster)) return false;
        if (message.DonationSettlementId != null && !objectManager.TryGetObjectWithLogging<Settlement>(message.DonationSettlementId, out donationSettlement)) return false;

        upgradedTroopHistory = ResolveUpgradedTroopHistory(message.UpgradedTroopHistoryIds);
        return true;
    }

    private List<Tuple<CharacterObject, CharacterObject, int>> ResolveUpgradedTroopHistory(UpgradedTroopHistoryData upgradedTroopHistoryIds)
    {
        List<Tuple<CharacterObject, CharacterObject, int>> upgradedTroopHistory = new();
        if (upgradedTroopHistoryIds.Data == null) return upgradedTroopHistory;

        foreach (var elementData in upgradedTroopHistoryIds.Data)
        {
            if (!objectManager.TryGetObjectWithLogging<CharacterObject>(elementData.Character1Id, out var character1)) continue;
            if (!objectManager.TryGetObjectWithLogging<CharacterObject>(elementData.Character2Id, out var character2)) continue;

            upgradedTroopHistory.Add(new(character1, character2, elementData.Number));
        }

        return upgradedTroopHistory;
    }

    private static List<(TroopRoster roster, TroopRosterData delta)> CreateRosterDeltas(
        Hero mainHero,
        PartyBase leftParty,
        TroopRoster leftPrisonerRoster,
        Settlement donationSettlement,
        NetworkCompleteDoneLogic message,
        TroopRosterData leftPrisonerRosterData,
        TroopRosterData rightPrisonerRosterData)
    {
        // Collect every roster delta and apply them together: TryApplyTroopRosterDeltas removes before it
        // adds across all rosters, so a hero/prisoner moved between parties keeps its party linkage
        // (the destination addition is the last AddToCounts on that hero).
        var rosterDeltas = new List<(TroopRoster roster, TroopRosterData delta)>();
        if (donationSettlement != null)
        {
            rosterDeltas.Add((donationSettlement.Party.PrisonRoster, leftPrisonerRosterData));
        }
        else if (leftParty != null)
        {
            rosterDeltas.Add((leftParty.MemberRoster, message.LeftMemberRosterData));
            rosterDeltas.Add((leftParty.PrisonRoster, leftPrisonerRosterData));
        }
        else if (leftPrisonerRoster != null) // Prisoner management doesn't have a set party
        {
            rosterDeltas.Add((leftPrisonerRoster, leftPrisonerRosterData));
        }

        rosterDeltas.Add((mainHero.PartyBelongedTo.MemberRoster, message.RightMemberRosterData));
        rosterDeltas.Add((mainHero.PartyBelongedTo.PrisonRoster, rightPrisonerRosterData));
        return rosterDeltas;
    }

    private void PublishPlayerCaptivityReleaseEvents(List<PlayerCaptivityEndedByServer> releasedPlayerCaptivityEvents)
    {
        foreach (var releaseEvent in releasedPlayerCaptivityEvents)
        {
            messageBroker.Publish(this, releaseEvent);
        }
    }

    private static void ApplyRightOwnerPartyItemRoster(Hero mainHero, NetworkCompleteDoneLogic message)
    {
        mainHero.PartyBelongedTo.ItemRoster.Clear();
        foreach (var itemRosterElement in message.RightOwnerPartyItemRosterData ?? Enumerable.Empty<ItemRosterElement>())
        {
            mainHero.PartyBelongedTo.ItemRoster.Add(itemRosterElement);
        }
    }

    private HashSet<string> GetTakenHeroCharacterIds(FlattenedTroopRoster takenPrisonersRoster)
    {
        var characterIds = new HashSet<string>();
        foreach (var element in takenPrisonersRoster)
        {
            if (element.Troop?.IsHero == true &&
                objectManager.TryGetIdWithLogging(element.Troop, out var characterId))
                characterIds.Add(characterId);
        }

        return characterIds;
    }

    private bool TryValidatePrisonerActionRosters(
        FlattenedTroopRoster releasedPrisonersRoster,
        FlattenedTroopRoster takenPrisonersRoster,
        TroopRosterData rightPrisonerRosterData,
        out HashSet<string> takenHeroCharacterIds)
    {
        takenHeroCharacterIds = GetTakenHeroCharacterIds(takenPrisonersRoster);
        var signedDeltas = (rightPrisonerRosterData.Data ?? Array.Empty<TroopRosterElementData>())
            .GroupBy(element => element.CharacterId)
            .ToDictionary(group => group.Key, group => group.Sum(element => element.Number));

        return ActionsMatchDelta(releasedPrisonersRoster, signedDeltas, expectedSign: -1) &&
               ActionsMatchDelta(takenPrisonersRoster, signedDeltas, expectedSign: 1);
    }

    private bool ActionsMatchDelta(
        FlattenedTroopRoster actionRoster,
        IReadOnlyDictionary<string, int> signedDeltas,
        int expectedSign)
    {
        var actionCounts = new Dictionary<string, int>();
        foreach (var element in actionRoster)
        {
            if (element.Troop == null ||
                !objectManager.TryGetIdWithLogging(element.Troop, out var characterId))
                return false;

            actionCounts.TryGetValue(characterId, out var count);
            actionCounts[characterId] = count + 1;
        }

        return actionCounts.All(action =>
            signedDeltas.TryGetValue(action.Key, out var delta) &&
            delta == expectedSign * action.Value);
    }

    internal static TroopRosterData FilterTakenHeroAdditions(
        TroopRosterData delta,
        HashSet<string> takenHeroCharacterIds)
    {
        if (delta.Data == null || takenHeroCharacterIds.Count == 0)
            return delta;

        var filtered = delta.Data
            .Where(element => element.Number <= 0 || !takenHeroCharacterIds.Contains(element.CharacterId))
            .ToArray();
        return filtered.Length == delta.Data.Length
            ? delta
            : new TroopRosterData(filtered);
    }

    internal static FlattenedTroopRoster FilterIneligibleTakenHeroes(
        FlattenedTroopRoster takenPrisonersRoster)
    {
        var filteredRoster = new FlattenedTroopRoster(4);
        foreach (var element in takenPrisonersRoster)
        {
            var hero = element.Troop?.HeroObject;
            if (hero != null && !TakePrisonerActionPatches.CanCaptureHero(hero))
            {
                logger.Warning(
                    "Skipped stale Party screen prisoner capture for hero {HeroId} in state {HeroState} with death mark {DeathMark}",
                    hero.StringId,
                    hero.HeroState,
                    hero.DeathMark);
                continue;
            }

            filteredRoster[element.Descriptor] = element;
        }

        return filteredRoster;
    }

    internal static bool HasLeftPrisonerTransferDestination(
        bool applyReleasedAndTakenPrisonerActions,
        bool hasLeftParty,
        bool hasLeftPrisonerRoster,
        bool hasDonationSettlement = false)
        => !applyReleasedAndTakenPrisonerActions &&
           (hasLeftParty || hasLeftPrisonerRoster || hasDonationSettlement);

    private bool TryValidatePrisonerDonationRosters(
        FlattenedTroopRoster donatedPrisonersRoster,
        TroopRosterData settlementPrisonerDelta,
        TroopRosterData playerPrisonerDelta)
    {
        if (donatedPrisonersRoster == null ||
            donatedPrisonersRoster.IsEmpty<FlattenedTroopRosterElement>())
            return false;

        return ActionRosterExactlyMatchesDelta(
                   donatedPrisonersRoster,
                   settlementPrisonerDelta,
                   expectedSign: 1) &&
               ActionRosterExactlyMatchesDelta(
                   donatedPrisonersRoster,
                   playerPrisonerDelta,
                   expectedSign: -1);
    }

    private bool ActionRosterExactlyMatchesDelta(
        FlattenedTroopRoster actionRoster,
        TroopRosterData delta,
        int expectedSign)
    {
        var actionCounts = new Dictionary<string, int>();
        foreach (var element in actionRoster)
        {
            if (element.Troop == null ||
                !objectManager.TryGetIdWithLogging(element.Troop, out var characterId))
                return false;

            actionCounts.TryGetValue(characterId, out var count);
            actionCounts[characterId] = count + 1;
        }

        var deltaCounts = (delta.Data ?? Array.Empty<TroopRosterElementData>())
            .GroupBy(element => element.CharacterId)
            .ToDictionary(group => group.Key, group => group.Sum(element => element.Number));
        return actionCounts.Count == deltaCounts.Count &&
               actionCounts.All(action =>
                   deltaCounts.TryGetValue(action.Key, out var count) &&
                   count == expectedSign * action.Value);
    }

    private static bool HasPrisonerDonationCapacity(
        Settlement settlement,
        TroopRosterData settlementPrisonerDelta)
    {
        long donatedCount = (settlementPrisonerDelta.Data ?? Array.Empty<TroopRosterElementData>())
            .Sum(element => (long)element.Number);
        long finalPrisonerCount = settlement.Party.PrisonRoster.TotalManCount + donatedCount;
        return donatedCount > 0 && finalPrisonerCount <= settlement.Party.PrisonerSizeLimit;
    }

    private static void ApplyPrisonerDonationEffects(
        Hero mainHero,
        Settlement settlement,
        FlattenedTroopRoster donatedPrisonersRoster)
    {
        foreach (var character in donatedPrisonersRoster.Troops)
        {
            if (character.IsHero)
                EnterSettlementAction.ApplyForPrisoner(character.HeroObject, settlement);
        }

        CampaignEventDispatcher.Instance.OnPrisonerDonatedToSettlement(
            mainHero.PartyBelongedTo,
            donatedPrisonersRoster,
            settlement);
    }

    private static void ApplyReleasedAndTakenPrisonerActions(
        Hero mainHero,
        FlattenedTroopRoster releasedPrisonersRoster,
        FlattenedTroopRoster takenPrisonersRoster)
    {
        var nonPlayerReleases = new FlattenedTroopRoster(4);
        foreach (var element in releasedPrisonersRoster)
        {
            if (element.Troop?.HeroObject?.IsPlayerHero() != true)
                nonPlayerReleases[element.Descriptor] = element;
        }

        if (!nonPlayerReleases.IsEmpty<FlattenedTroopRosterElement>())
            EndCaptivityAction.ApplyByReleasedByChoice(nonPlayerReleases);

        if (takenPrisonersRoster.IsEmpty<FlattenedTroopRosterElement>())
            return;

        var captorParty = mainHero.PartyBelongedTo?.Party;
        if (captorParty == null)
        {
            logger.Error("Cannot apply Party screen prisoner captures because main hero {Hero} has no party", mainHero);
            return;
        }

        foreach (var element in takenPrisonersRoster)
        {
            if (element.Troop?.HeroObject is Hero hero)
                TakePrisonerAction.Apply(captorParty, hero);
        }
        CampaignEventDispatcher.Instance.OnPrisonerTaken(takenPrisonersRoster);
    }

    private static void NotifyTakenPrisonersChanged(FlattenedTroopRoster takenPrisonersRoster)
    {
        if (Settlement.CurrentSettlement == null) return;
        if (takenPrisonersRoster.IsEmpty<FlattenedTroopRosterElement>()) return;

        CampaignEventDispatcher.Instance.OnPrisonersChangeInSettlement(Settlement.CurrentSettlement, takenPrisonersRoster, null, true);
    }

    private static void ApplyPartyRewardChanges(Hero mainHero, NetworkCompleteDoneLogic message)
    {
        if (!message.DoNotApplyGoldTransactions)
        {
            GiveGoldAction.ApplyBetweenCharacters(null, mainHero, message.PartyGoldChangeAmount, false);
        }
        if (message.PartyInfluenceChangeAmount != 0)
        {
            // Influence goes to the requesting player's clan (mainHero), not the local machine's
            // Hero.MainHero - which is null on a dedicated server (NRE) and the wrong clan otherwise.
            GainKingdomInfluenceAction.ApplyForLeavingTroopToGarrison(mainHero, (float)message.PartyInfluenceChangeAmount);
        }
    }

    private static void ApplyUpgradedTroopHistory(Hero mainHero, List<Tuple<CharacterObject, CharacterObject, int>> upgradedTroopHistory)
    {
        //Replacement for CampaignEventDispatcher.Instance.OnPlayerUpgradedTroops(tuple.Item1, tuple.Item2, tuple.Item3) without MainParty
        foreach (Tuple<CharacterObject, CharacterObject, int> tuple in upgradedTroopHistory)
        {
            SkillLevelingManager.OnUpgradeTroops(mainHero.PartyBelongedTo.Party, tuple.Item1, tuple.Item2, tuple.Item3);
        }
    }

    private static void ApplyPrisonerRecruitmentEffects(
        Hero mainHero,
        NetworkCompleteDoneLogic message,
        FlattenedTroopRoster recruitedPrisonersRoster)
    {
        if (message.RecruitedPrisonersRoster == null) return;
        if (recruitedPrisonersRoster.IsEmpty<FlattenedTroopRosterElement>()) return;

        // Replacement for CampaignEventDispatcher.Instance.OnMainPartyPrisonerRecruited(obj.What.RecruitedPrisonersRoster);
        foreach (CharacterObject characterObject in recruitedPrisonersRoster.Troops)
        {
            ApplyPrisonerRecruitmentEffect(mainHero, characterObject);
        }
    }

    private static void ApplyPrisonerRecruitmentEffect(Hero mainHero, CharacterObject characterObject)
    {
        // Replace CampaignEventDispatcher.Instance.OnUnitRecruited(characterObject, 1);
        if (mainHero.GetPerkValue(DefaultPerks.Leadership.FamousCommander))
        {
            mainHero.PartyBelongedTo.MemberRoster.AddXpToTroop(characterObject, (int)DefaultPerks.Leadership.FamousCommander.SecondaryBonus * 1);
        }
        SkillLevelingManager.OnTroopRecruited(mainHero, 1, characterObject.Tier);
        if (characterObject.Occupation == Occupation.Bandit)
        {
            SkillLevelingManager.OnBanditsRecruited(mainHero.PartyBelongedTo, characterObject, 1);
        }

        // Replace ApplyPrisonerRecruitmentEffects
        int prisonerRecruitmentMoraleEffect = Campaign.Current.Models.PrisonerRecruitmentCalculationModel.GetPrisonerRecruitmentMoraleEffect(mainHero.PartyBelongedTo.Party, characterObject, 1);
        mainHero.PartyBelongedTo.RecentEventsMorale += (float)prisonerRecruitmentMoraleEffect;
    }

    private void ApplyRosterOrder(TroopRoster roster, TroopRosterOrderData orderData)
    {
        messageBroker.Publish(this, new ApplyTroopRosterOrder(roster, orderData));
    }

    internal List<PlayerCaptivityEndedByServer> CreatePlayerCaptivityReleaseEvents(
        TroopRosterData leftPrisonerRosterData,
        TroopRosterData rightPrisonerRosterData,
        bool hasLeftPrisonerDestination,
        CampaignVec2 releaserPartyPosition,
        out TroopRosterData filteredLeftPrisonerRosterData,
        out TroopRosterData filteredRightPrisonerRosterData)
    {
        var releasedPlayerPrisoners = new List<Hero>();
        // The normal party screen's left prisoner roster is a dummy discard target, not a transfer destination.
        var transferredPlayerPrisoners = hasLeftPrisonerDestination
            ? GetTransferredPlayerPrisoners(leftPrisonerRosterData, rightPrisonerRosterData)
            : GetTransferredPlayerPrisoners(rightPrisonerRosterData);
        filteredLeftPrisonerRosterData = FilterPlayerPrisonerReleaseDelta(leftPrisonerRosterData, transferredPlayerPrisoners, releasedPlayerPrisoners);
        filteredRightPrisonerRosterData = FilterPlayerPrisonerReleaseDelta(rightPrisonerRosterData, transferredPlayerPrisoners, releasedPlayerPrisoners);

        return releasedPlayerPrisoners
            .Select(playerHero => new PlayerCaptivityEndedByServer(playerHero, EndCaptivityDetail.ReleasedByChoice, null, releaserPartyPosition))
            .ToList();
    }

    private HashSet<string> GetTransferredPlayerPrisoners(params TroopRosterData[] prisonerRosterDeltas)
    {
        var transferredPlayerPrisoners = new HashSet<string>();
        foreach (var delta in prisonerRosterDeltas)
        {
            foreach (var elementData in delta.Data ?? Array.Empty<TroopRosterElementData>())
            {
                if (elementData.Number > 0 && TryGetPlayerPrisonerHero(elementData, out _))
                    transferredPlayerPrisoners.Add(elementData.CharacterId);
            }
        }

        return transferredPlayerPrisoners;
    }

    private TroopRosterData FilterPlayerPrisonerReleaseDelta(
        TroopRosterData delta,
        HashSet<string> transferredPlayerPrisoners,
        List<Hero> releasedPlayerPrisoners)
    {
        if (delta.Data == null) return delta;

        var filtered = new List<TroopRosterElementData>();
        foreach (var elementData in delta.Data)
        {
            if (elementData.Number < 0 &&
                TryGetPlayerPrisonerHero(elementData, out var playerHero) &&
                !transferredPlayerPrisoners.Contains(elementData.CharacterId))
            {
                releasedPlayerPrisoners.Add(playerHero);
                continue;
            }

            filtered.Add(elementData);
        }

        return filtered.Count == delta.Data.Length
            ? delta
            : new TroopRosterData(filtered);
    }

    private bool TryGetPlayerPrisonerHero(TroopRosterElementData elementData, out Hero playerHero)
    {
        playerHero = null;
        return objectManager.TryGetObjectWithLogging<CharacterObject>(elementData.CharacterId, out var character) &&
               (playerHero = character.HeroObject)?.IsPlayerHero() == true;
    }
}
