using Common.Logging;
using Common.Messaging;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.Heroes.Messages.Collections;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.TroopRosters.Data;
using GameInterface.Services.TroopRosters.Logging;
using GameInterface.Services.TroopRosters.Messages;
using Serilog;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;

namespace GameInterface.Services.TroopRosters.Interfaces;

public interface ITroopRosterInterface : IGameAbstraction
{
    /// <summary>
    /// Pack troop roster elements to allow for sending over the network.
    /// The string Id can either represent a Hero Id or a CharacterObject Id.
    /// </summary>
    TroopRosterData PackTroopRosterData(TroopRoster troopRoster);

    /// <summary>
    /// Unpack troop roster data into usable TroopRosterElements.
    /// Optional mainHero parameter for avoiding retrieving a duplicate of a player hero already in a roster.
    /// </summary>
    IEnumerable<TroopRosterElement> UnpackTroopRosterData(TroopRosterData troopRosterData);

    /// <summary>
    /// Updates target roster with incoming data from the client.
    /// </summary>
    void UpdateWithData(TroopRoster targetTroopRoster, TroopRosterData packedTroopRosterElements, Hero mainHero);

    /// <summary>
    /// Packs the per-character difference between <paramref name="current"/> and <paramref name="initial"/>
    /// (current minus initial). Only changed characters are included; an unchanged troop - including a hero -
    /// nets to zero and is omitted, so the change can be re-applied as a delta on the server with no special
    /// handling for heroes or companions.
    /// </summary>
    TroopRosterData PackTroopRosterDelta(TroopRoster current, TroopRoster initial);

    /// <summary>
    /// Applies a set of packed deltas (produced by <see cref="PackTroopRosterDelta"/>). A delta that asks for
    /// more than the roster holds is clamped to what is actually there rather than refused, with any matching
    /// addition reduced by the same amount so no troops are created. Only a malformed request - an unresolvable
    /// character or a roster element listed twice - fails. All count reductions are applied before any
    /// additions across every roster so transferred heroes retain their party linkage.
    /// </summary>
    bool TryApplyTroopRosterDeltas(
        IReadOnlyList<(TroopRoster roster, TroopRosterData delta)> deltas);

    /// <summary>
    /// As <see cref="TryApplyTroopRosterDeltas(IReadOnlyList{ValueTuple{TroopRoster, TroopRosterData}})"/>,
    /// additionally reporting through <paramref name="adjusted"/> whether anything had to be clamped. A caller
    /// serving a client request uses that to push the authoritative rosters back, since the requester's screen
    /// is now showing a result it did not get.
    /// </summary>
    bool TryApplyTroopRosterDeltas(
        IReadOnlyList<(TroopRoster roster, TroopRosterData delta)> deltas,
        out bool adjusted);

    /// <summary>
    /// Runs troop recruitment logic for client requests.
    /// </summary>
    void HandleOnRecruitmentDone(string mobilePartyId, TroopInfo[] troopsInCart);

    /// <summary>
    /// Players are able to change the order of their party roster.
    /// Used to pack the order of elements in a TroopRoster to reshuffle after apply deltas.
    /// </summary>
    TroopRosterOrderData PackTroopRosterOrderData(TroopRoster roster);
}

internal class TroopRosterInterface : ITroopRosterInterface
{
    private static readonly ILogger Logger = LogManager.GetLogger<TroopRosterInterface>();
    private readonly IObjectManager objectManager;
    private readonly ITroopRosterLogger troopRosterLogger;

    public TroopRosterInterface(
        IObjectManager objectManager,
        ITroopRosterLogger troopRosterLogger)
    {
        this.objectManager = objectManager;
        this.troopRosterLogger = troopRosterLogger;
    }

    public TroopRosterData PackTroopRosterData(TroopRoster troopRoster)
    {
        var elements = new List<TroopRosterElementData>();
        foreach (TroopRosterElement troopRosterElement in troopRoster.data)
        {
            if (troopRosterElement.Character == null)
                continue;

            if (!objectManager.TryGetIdWithLogging(troopRosterElement.Character, out var characterId))
                continue;

            elements.Add(new TroopRosterElementData(characterId, troopRosterElement.Number, troopRosterElement.WoundedNumber, troopRosterElement.Xp));
        }

        return new TroopRosterData(elements);
    }

    public IEnumerable<TroopRosterElement> UnpackTroopRosterData(TroopRosterData troopRosterData)
    {
        if (troopRosterData.Data == null)
            yield break;

        foreach (var elementData in troopRosterData.Data)
        {
            if (!objectManager.TryGetObjectWithLogging<CharacterObject>(elementData.CharacterId, out var character))
                continue;

            yield return new TroopRosterElement(character)
            {
                _number = elementData.Number,
                _woundedNumber = elementData.WoundedNumber,
                _xp = elementData.Xp
            };
        }
    }

    public void UpdateWithData(TroopRoster targetTroopRoster, TroopRosterData packedTroopRosterElements, Hero mainHero)
    {
        // Only preserve heroes in a player's troopRoster
        bool preserveHeroes = mainHero != null && mainHero.IsPlayerHero() && targetTroopRoster.OwnerParty?.MemberRoster == targetTroopRoster;

        // If preserving heroes, clear without removing mainHero and heroes in the same clan (companions & family members)
        // Causes issues if mainHero, player companions or family members are removed from a player's party
        for (int i = targetTroopRoster._count - 1; i >= 0; i--)
        {
            var character = targetTroopRoster.data[i].Character;
            if (preserveHeroes && (character?.HeroObject == mainHero || character?.HeroObject?.Clan == mainHero.Clan)) continue;
            targetTroopRoster.AddToCounts(character, -targetTroopRoster.data[i].Number, false, -targetTroopRoster.data[i].WoundedNumber, 0, true);
        }

        if (packedTroopRosterElements.Data == null) return;

        // Rebuild roster with new data
        foreach (var element in UnpackTroopRosterData(packedTroopRosterElements))
        {
            // If preserving heroes, clear doesn't remove mainHero and companions
            // Avoid adding duplicates of any existing heroes to the roster when rebuilding
            if (preserveHeroes && targetTroopRoster.Contains(element.Character))
                continue;

            targetTroopRoster.Add(element);
        }
    }

    public TroopRosterData PackTroopRosterDelta(TroopRoster current, TroopRoster initial)
    {
        // Diffed via per-character totals (not raw slots), so any quirk present in both snapshots cancels.
        var currentCounts = SumByCharacter(current);
        var initialCounts = SumByCharacter(initial);

        var elements = new List<TroopRosterElementData>();
        foreach (var character in currentCounts.Keys.Union(initialCounts.Keys))
        {
            currentCounts.TryGetValue(character, out var cur);
            initialCounts.TryGetValue(character, out var init);

            int numberDelta = cur.number - init.number;
            int woundedDelta = cur.wounded - init.wounded;
            int currentXp = cur.xp;
            if (cur.number == 0)
            {
                currentXp = 0;
            }

            int initialXp = init.xp;
            if (init.number == 0)
            {
                initialXp = 0;
            }

            int xpDelta = currentXp - initialXp;
            if (numberDelta == 0 && woundedDelta == 0 && xpDelta == 0)
                continue;

            if (!objectManager.TryGetIdWithLogging(character, out var characterId))
                continue;

            elements.Add(new TroopRosterElementData(characterId, numberDelta, woundedDelta, xpDelta));
        }

        return new TroopRosterData(elements);
    }

    public bool TryApplyTroopRosterDeltas(
        IReadOnlyList<(TroopRoster roster, TroopRosterData delta)> deltas)
        => TryApplyTroopRosterDeltas(deltas, out _);

    public bool TryApplyTroopRosterDeltas(
        IReadOnlyList<(TroopRoster roster, TroopRosterData delta)> deltas,
        out bool adjusted)
    {
        adjusted = false;
        if (deltas == null) return false;

        var elements = new List<(
            TroopRoster roster,
            CharacterObject character,
            TroopRosterElementData delta)>();
        var currentStates = new List<(int number, int wounded, int xp)>();
        var uniqueElements = new HashSet<(TroopRoster roster, CharacterObject character)>();
        var shortfallByCharacter = new Dictionary<CharacterObject, int>();

        foreach (var (roster, delta) in deltas)
        {
            if (roster == null) return false;

            if (delta.Data == null) continue;
            var currentByCharacter = SumByCharacter(roster);

            foreach (var elementData in delta.Data)
            {
                // A character the server cannot resolve, or the same roster element listed twice, is a
                // malformed request rather than a stale one - there is no sane state to clamp it towards.
                if (!objectManager.TryGetObjectWithLogging<CharacterObject>(elementData.CharacterId, out var character))
                    return false;
                if (!uniqueElements.Add((roster, character))) return false;

                currentByCharacter.TryGetValue(character, out var current);
                // Two rules meet here, because the two shapes of bad delta are not the same thing.
                //
                // A HERO is one indivisible person: there is no "move what is there" for half of one, so a
                // delta that does not fit is a STALE request - the hero has already gone somewhere else - and
                // the whole batch is refused rather than quietly clamped to nothing. Clamping one would let a
                // stale transfer half-apply, which is the case upstream added these guards for.
                //
                // A TROOP STACK is divisible, so an overdrawn one is clamped to what is actually there and the
                // shortfall is withheld from the destination below. That is the shape seen in the wild: a
                // client moving 30 of a militia stack the server holds 15 of.
                //
                // Overflow is refused whichever it is: there is no sane state to clamp towards.
                long finalNumber = current.number + elementData.Number;
                long finalWounded = current.wounded + elementData.WoundedNumber;
                long finalXp = current.xp + elementData.Xp;

                bool doesNotFit =
                    finalNumber < 0 ||
                    finalWounded < 0 ||
                    finalWounded > finalNumber ||
                    finalXp < 0 ||
                    (elementData.Xp != 0 && finalNumber == 0 && finalXp != 0);
                bool overflows = finalNumber > int.MaxValue || finalXp > int.MaxValue;

                if (overflows || (doesNotFit && character.IsHero)) return false;

                var clamped = ClampToHoldableState(elementData, current);
                if (clamped.Number != elementData.Number ||
                    clamped.WoundedNumber != elementData.WoundedNumber ||
                    clamped.Xp != elementData.Xp)
                {
                    // Only a count that could not be honoured is worth telling the player about: that is the
                    // one they can see, as troops still sitting where they tried to move them from. Experience
                    // settling differently is invisible on the screen, and reporting it would fire a resync and
                    // an alarming message during ordinary play.
                    if (clamped.Number != elementData.Number) adjusted = true;

                    Logger.Warning(
                        "Clamped troop roster delta for {CharacterId}: current=({CurrentNumber},{CurrentWounded},{CurrentXp}) requested=({NumberDelta},{WoundedDelta},{XpDelta}) applied=({ClampedNumber},{ClampedWounded},{ClampedXp})",
                        elementData.CharacterId,
                        current.number,
                        current.wounded,
                        current.xp,
                        elementData.Number,
                        elementData.WoundedNumber,
                        elementData.Xp,
                        clamped.Number,
                        clamped.WoundedNumber,
                        clamped.Xp);
                }

                // Removing fewer troops than asked leaves a debt: whatever the source could not supply must
                // not be handed to the destination, or the shortfall is minted as new troops.
                int shortfall = clamped.Number - elementData.Number;
                if (shortfall > 0)
                {
                    shortfallByCharacter.TryGetValue(character, out var pending);
                    shortfallByCharacter[character] = pending + shortfall;
                }

                elements.Add((roster, character, clamped));
                currentStates.Add(current);
            }
        }

        if (shortfallByCharacter.Count > 0)
        {
            for (int i = 0; i < elements.Count; i++)
            {
                var (roster, character, delta) = elements[i];
                if (delta.Number <= 0) continue;
                if (!shortfallByCharacter.TryGetValue(character, out var shortfall)) continue;

                int consumed = ConsumeShortfall(delta.Number, shortfall);
                if (consumed == 0) continue;

                shortfallByCharacter[character] = shortfall - consumed;
                var reduced = new TroopRosterElementData(
                    delta.CharacterId,
                    delta.Number - consumed,
                    delta.WoundedNumber,
                    delta.Xp);

                // The smaller count can strand wounded or experience, so re-settle the element against it.
                elements[i] = (roster, character, ClampToHoldableState(reduced, currentStates[i]));
                adjusted = true;
            }
        }

        // AddToCounts(hero, -n) nulls the hero's party linkage, so additions must be the last operation.
        ApplyDeltaElements(elements, applyAdditions: false);
        ApplyDeltaElements(elements, applyAdditions: true);
        return true;
    }

    /// <summary>
    /// Bends one requested delta onto the nearest state the roster can actually hold: no negative counts, no
    /// more wounded than troops, and no experience stranded on an empty stack.
    /// </summary>
    /// <remarks>
    /// These deltas are the difference between a party screen's live rosters and the snapshot it opened with,
    /// so they are stale by construction - the world keeps moving while the screen is up, and the screen is
    /// only modal for the player holding it. Refusing the whole commit on a stale element made the player's
    /// action silently do nothing, which is what a discard that "does not work" looks like from the outside.
    /// Observed repeatedly against garrisons, where the client asked to remove 30 of a militia stack the
    /// server held 15 of.
    ///
    /// Clamping grants no capability a well-formed request lacked: it only ever shrinks a change towards what
    /// the roster already contains, and the caller pairs any shortfall against matching additions so troops
    /// are never conjured. The player's intent - empty this stack - still lands, and both sides converge.
    /// </remarks>
    internal static TroopRosterElementData ClampToHoldableState(
        TroopRosterElementData delta,
        (int number, int wounded, int xp) current)
    {
        long finalNumber = current.number + (long)delta.Number;
        if (finalNumber < 0) finalNumber = 0;
        if (finalNumber > int.MaxValue) finalNumber = int.MaxValue;

        long finalWounded = current.wounded + (long)delta.WoundedNumber;
        if (finalWounded < 0) finalWounded = 0;
        if (finalWounded > finalNumber) finalWounded = finalNumber;

        long finalXp = current.xp + (long)delta.Xp;
        if (finalXp < 0) finalXp = 0;
        if (finalXp > int.MaxValue) finalXp = int.MaxValue;
        // An emptied stack is dropped from the roster, so any experience left on it would simply vanish.
        if (finalNumber == 0) finalXp = 0;

        return new TroopRosterElementData(
            delta.CharacterId,
            (int)(finalNumber - current.number),
            (int)(finalWounded - current.wounded),
            (int)(finalXp - current.xp));
    }

    /// <summary>
    /// How much of an outstanding removal shortfall a paired addition has to give back.
    /// </summary>
    internal static int ConsumeShortfall(int additionNumber, int shortfall)
    {
        if (additionNumber <= 0 || shortfall <= 0) return 0;
        return shortfall < additionNumber ? shortfall : additionNumber;
    }

    private void ApplyDeltaElements(
        IReadOnlyList<(TroopRoster roster, CharacterObject character, TroopRosterElementData delta)> elements,
        bool applyAdditions)
    {
        foreach (var element in elements)
        {
            bool isAddition = element.delta.Number >= 0;
            if (isAddition != applyAdditions) continue;

            troopRosterLogger.Debug(
                element.roster,
                "APPLY-DELTA pass={Pass} character={CharacterId} numberDelta={Number} woundedDelta={Wounded} xpDelta={Xp}",
                applyAdditions ? "add" : "remove",
                element.delta.CharacterId,
                element.delta.Number,
                element.delta.WoundedNumber,
                element.delta.Xp);

            element.roster.AddToCounts(
                element.character,
                element.delta.Number,
                false,
                element.delta.WoundedNumber,
                element.delta.Xp,
                true);
        }
    }

    private static Dictionary<CharacterObject, (int number, int wounded, int xp)> SumByCharacter(TroopRoster roster)
    {
        var counts = new Dictionary<CharacterObject, (int number, int wounded, int xp)>();
        if (roster == null) return counts;

        foreach (TroopRosterElement element in roster.data)
        {
            if (element.Character == null) continue;
            counts.TryGetValue(element.Character, out var existing);
            counts[element.Character] = (existing.number + element.Number, existing.wounded + element.WoundedNumber, existing.xp + element.Xp);
        }
        return counts;
    }

    public void HandleOnRecruitmentDone(string mobilePartyId, TroopInfo[] troopsInCart)
    {
        if (!objectManager.TryGetObjectWithLogging(mobilePartyId, out MobileParty mobileParty)) return;

        List<(Hero, CharacterObject, int)> herosValidated = new();

        // Validate troops before committing to recruiting
        foreach (var troop in troopsInCart)
        {
            if (!objectManager.TryGetObjectWithLogging(troop.RecruiterHeroId, out Hero hero)) continue;
            if (!objectManager.TryGetObjectWithLogging(troop.CharacterObjectId, out CharacterObject characterObject)) continue;

            var volunteerTroopAtIndex = hero.VolunteerTypes[troop.TroopIndex];

            if (volunteerTroopAtIndex is null) continue;

            herosValidated.Add((hero, characterObject, troop.TroopIndex));
        }

        // Calculate cost before changing any data
        var cost = 0;
        foreach ((Hero hero, CharacterObject characterObject, int index) in herosValidated)
        {
            cost += Campaign.Current.Models.PartyWageModel.GetTroopRecruitmentCost(characterObject, mobileParty.LeaderHero).RoundedResultNumber;
        }

        // Do not apply recruitment if the player does not have enough gold
        if (cost > mobileParty.LeaderHero.Gold)
        {
            Logger.Warning("Attempted to recruit troops that cost more than the player had");
            return;
        }

        // Commit recruitment
        foreach ((Hero hero, CharacterObject characterObject, int index) in herosValidated)
        {
            hero.VolunteerTypes[index] = null;
            MessageBroker.Instance.Publish(this, new VolunteerTypesArrayUpdated(hero, null, index));

            mobileParty.MemberRoster.AddToCounts(characterObject, 1, false, 0, 0, true, -1);
            CampaignEventDispatcher.Instance.OnUnitRecruited(characterObject, 1);
        }

        GiveGoldAction.ApplyBetweenCharacters(mobileParty.LeaderHero, null, cost, false);
    }

    public TroopRosterOrderData PackTroopRosterOrderData(TroopRoster roster)
    {
        var troopRosterOrderData = new TroopRosterOrderData(new());
        if (roster == null || roster.data == null) return null;

        for (int i = 0; i < roster.Count; i++)
        {
            var character = roster.data[i].Character;

            if (!objectManager.TryGetIdWithLogging(character, out var characterId)) continue;

            troopRosterOrderData.IndexCharacterIds[i] = characterId;
        }
        return troopRosterOrderData;
    }
}
