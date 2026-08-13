using Common.Logging;
using Common.Util;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.TroopRosters.Data;
using Serilog;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Loot;

/// <summary>
/// Carries out a <see cref="BattleLootApplicationPlan"/> against a real party, on the server.
/// </summary>
/// <remarks>
/// Deliberately dull. Every decision worth arguing about - what may be claimed, how much room there is, who
/// gets it first - was already settled by the validator and the planner, both of which are pure and tested.
/// What is left here is resolving ids and calling the game, which cannot be tested without a campaign, so
/// there should be as little of it as possible.
///
/// The one rule this file exists to enforce: a HERO never moves by a roster copy. TroopRoster.Add and
/// AddToCounts put the element in without running PartyBase.OnHeroAdded, so Hero.PartyBelongedTo stays null
/// and the hero ends up owned by nobody - and, because the server still has him where he really was, present
/// in two rosters at once. That is not theoretical: it is what put "Ikren the Swift is in 2 rosters ...
/// PartyBelongedTo=&lt;null&gt;" into a live save, and a doubled hero made the battle reserve builder log
/// DUPLICATE HERO and never finish, leaving a player standing in an empty battle. Heroes move by
/// TakePrisonerAction and EndCaptivityAction, which do the bookkeeping.
/// </remarks>
internal class BattleLootApplier
{
    private static readonly ILogger Logger = LogManager.GetLogger<BattleLootApplier>();

    private readonly IObjectManager objectManager;

    public BattleLootApplier(IObjectManager objectManager)
    {
        this.objectManager = objectManager;
    }

    /// <summary>Reads how much room a party has right now, on the server, for the planner to clamp against.</summary>
    public static BattleLootCapacity MeasureCapacity(MobileParty party)
    {
        if (party?.Party == null) return new BattleLootCapacity(0, 0);

        return new BattleLootCapacity(
            party.Party.PartySizeLimit - party.MemberRoster.TotalManCount,
            party.Party.PrisonerSizeLimit - party.PrisonRoster.TotalManCount);
    }

    /// <summary>Applies the plan and returns what the party's rosters became.</summary>
    public BattleLootRosterSnapshot Apply(MobileParty party, BattleLootApplicationPlan plan)
    {
        if (party?.Party == null || plan == null) return Snapshot(party);

        // AllowedThread, like every other authoritative roster change: without it the roster patches read
        // this as the local machine proposing a change of its own and refuse or re-broadcast it.
        using (new AllowedThread())
        {
            foreach (var grant in plan.Items) GiveItem(party, grant);
            foreach (var grant in plan.Members) GiveTroops(party.MemberRoster, grant, party);
            foreach (var grant in plan.Prisoners) GiveTroops(party.PrisonRoster, grant, party);

            foreach (var action in plan.Heroes) MoveHero(party, action);
        }

        return Snapshot(party);
    }

    private void GiveItem(MobileParty party, BattleLootGrant grant)
    {
        if (!objectManager.TryGetObject<ItemObject>(grant.ObjectId, out var item) || item == null)
        {
            Logger.Warning("[Loot] Could not resolve item {Item}; skipping {Count}", grant.ObjectId, grant.Count);
            return;
        }

        ItemModifier modifier = null;
        if (!string.IsNullOrEmpty(grant.ModifierId) &&
            !objectManager.TryGetObject(grant.ModifierId, out modifier))
        {
            // Better to hand over the plain item than nothing: the modifier only changes its quality, and
            // refusing the whole stack over a missing modifier would cost the player loot they won.
            Logger.Warning("[Loot] Could not resolve item modifier {Modifier}; granting {Item} unmodified",
                grant.ModifierId, grant.ObjectId);
            modifier = null;
        }

        party.ItemRoster.AddToCounts(new EquipmentElement(item, modifier), grant.Count);
    }

    private void GiveTroops(TroopRoster roster, BattleLootGrant grant, MobileParty party)
    {
        if (!objectManager.TryGetObject<CharacterObject>(grant.ObjectId, out var character) || character == null)
        {
            Logger.Warning("[Loot] Could not resolve troop {Troop}; skipping {Count}", grant.ObjectId, grant.Count);
            return;
        }

        // The planner never puts a hero in these lists, but the cost of being wrong here is save corruption,
        // so the invariant is enforced where the damage would happen rather than only where it is decided.
        if (character.IsHero)
        {
            Logger.Error(
                "[Loot] Refused to roster-copy hero {Hero} into {Party}; heroes move by action only",
                grant.ObjectId, party.StringId);
            return;
        }

        roster.AddToCounts(character, grant.Count, false, grant.WoundedNumber, grant.Xp);
    }

    /// <summary>
    /// Imprisons or frees a hero, re-checking that it is still a sensible thing to do.
    /// </summary>
    /// <remarks>
    /// Time passes between the offer and the answer - a player can sit on a loot screen for as long as they
    /// like - and in that time the server keeps running. A hero can be ransomed, freed by a peace treaty, or
    /// have their captor destroyed. Acting on the stale answer anyway is how a hero ends up in two places.
    /// </remarks>
    private void MoveHero(MobileParty party, BattleLootHeroAction action)
    {
        if (!TryResolveHero(objectManager, action.CharacterId, out var hero))
        {
            Logger.Warning("[Loot] Could not resolve hero {Hero}; skipping", action.CharacterId);
            return;
        }

        if (!hero.IsAlive)
        {
            Logger.Information("[Loot] {Hero} died before the loot was answered; skipping", action.CharacterId);
            return;
        }

        if (action.Disposition == BattleLootDisposition.Release)
        {
            if (!hero.IsPrisoner)
            {
                Logger.Information("[Loot] {Hero} was already free; nothing to release", action.CharacterId);
                return;
            }

            EndCaptivityAction.ApplyByReleasedByChoice(hero, party.LeaderHero);
            return;
        }

        if (hero.PartyBelongedToAsPrisoner == party.Party)
        {
            // Already ours - a replayed apply, or the server got there first. Doing it again would be the
            // double-credit this whole design exists to prevent.
            Logger.Information("[Loot] {Hero} is already a prisoner of {Party}; nothing to do",
                action.CharacterId, party.StringId);
            return;
        }

        TakePrisonerAction.Apply(party.Party, hero);
        Logger.Information("[Loot] {Hero} taken prisoner by {Party}", action.CharacterId, party.StringId);
    }

    /// <summary>
    /// Resolves a hero loot line to its <see cref="Hero"/>.
    /// </summary>
    /// <remarks>
    /// Every line of an offer - items, troops and heroes alike - is keyed by the id of the OBJECT the roster
    /// element holds, and for a hero that object is a CharacterObject, not a Hero: the reserve builder keys
    /// heroes by their CharacterObject id (hero CharacterObjects are registered in their own right), and
    /// <see cref="GiveTroops"/> resolves the same field that way. Heroes are separately registered under Hero
    /// ids, so asking for one of those with a CharacterObject id can never match.
    ///
    /// It did exactly that, silently: 13 offers carried heroes, all 14 claims logged "Could not resolve hero
    /// CharacterObject_lord_..." and not one lord was ever imprisoned. The player claimed a captured lord in
    /// the loot screen and simply did not get him.
    ///
    /// Resolved in one place, through the one id space the whole offer uses - deliberately NOT by trying the
    /// Hero space as well. A reader that probes both spaces leaves the field's meaning ambiguous, which is the
    /// defect itself rather than a fix for it.
    /// </remarks>
    internal static bool TryResolveHero(IObjectManager objectManager, string characterId, out Hero hero)
    {
        hero = null;
        if (objectManager == null) return false;
        if (!objectManager.TryGetObject<CharacterObject>(characterId, out var character) || character == null)
            return false;

        hero = character.HeroObject;
        return hero != null;
    }

    /// <summary>Packs the party's rosters as they now stand.</summary>
    private BattleLootRosterSnapshot Snapshot(MobileParty party)
    {
        if (party?.Party == null)
            return new BattleLootRosterSnapshot(null, null, null);

        var items = new List<BattleLootItemStack>();
        foreach (var element in party.ItemRoster)
        {
            var item = element.EquipmentElement.Item;
            if (item == null) continue;
            if (!objectManager.TryGetId(item, out var itemId)) continue;

            string modifierId = null;
            var modifier = element.EquipmentElement.ItemModifier;
            if (modifier != null) objectManager.TryGetId(modifier, out modifierId);

            items.Add(new BattleLootItemStack(itemId, modifierId, element.Amount));
        }

        return new BattleLootRosterSnapshot(items, Pack(party.MemberRoster), Pack(party.PrisonRoster));
    }

    private List<TroopRosterElementData> Pack(TroopRoster roster)
    {
        var packed = new List<TroopRosterElementData>();
        if (roster == null) return packed;

        for (int i = 0; i < roster.Count; i++)
        {
            var element = roster.GetElementCopyAtIndex(i);
            if (element.Character == null) continue;
            if (!objectManager.TryGetId(element.Character, out var characterId)) continue;

            packed.Add(new TroopRosterElementData(
                characterId, element.Number, element.WoundedNumber, element.Xp));
        }

        return packed;
    }
}
