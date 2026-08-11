using System.Collections.Generic;
using GameInterface.Services.MapEvents.Patches;
using GameInterface.Services.MobileParties.Extensions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace GameInterface.Services.MapEvents;

/// <summary>
/// Which nearby AI parties would ride into a given battle, answered for a MAP EVENT rather than for
/// "the player's encounter".
/// </summary>
/// <remarks>
/// Vanilla's <c>DefaultEncounterModel.FindNonAttachedNpcPartiesWhoWillJoinPlayerEncounter</c> cannot be used by
/// a co-op server, for three separate reasons - all of which were live:
///
/// 1. It searches a radius around <c>MobileParty.MainParty.Position</c>, and only re-centres on the battle when
///    <c>PlayerEncounter.Battle</c> is non-null. The server has no PlayerEncounter for a client's battle, so it
///    searched around the SERVER's own character. Lords standing beside the actual fight were never considered.
///    Observed as 36,593 consecutive scans of a live battle offering zero joiners.
///
/// 2. With <c>PlayerEncounter.Battle</c> null it also takes the wrong eligibility branch, deciding sides from
///    <c>MainParty.MapFaction</c> and <c>PlayerEncounter.EncounteredParty</c> instead of from the battle.
///
/// 3. Its two parameters are (PLAYER-side parties, ENEMY-side parties) - see
///    <c>PlayerEncounter.CheckNearbyPartiesToJoinPlayerMapEvent</c>, which fills them from
///    <c>PartiesOnSide(PlayerSide)</c> and <c>PartiesOnSide(PlayerSide.GetOppositeSide())</c>. Calling it with
///    (attackers, defenders) is only correct while the player happens to be attacking; a defending player got
///    its allies added to the enemy side.
///
/// So the selection is restated here in the battle's own terms. The filters and the radius are vanilla's; what
/// changes is that the centre is the battle and the side comes from <see cref="MapEvent.CanPartyJoinBattle"/>,
/// which needs no notion of a single local player - the right shape for co-op, where players sit on both sides.
/// </remarks>
internal static class BattleJoinCandidates
{
    internal readonly struct Candidate
    {
        public Candidate(MobileParty party, BattleSideEnum side)
        {
            Party = party;
            Side = side;
        }

        public MobileParty Party { get; }
        public BattleSideEnum Side { get; }
    }

    /// <summary>
    /// Every nearby party that would join <paramref name="mapEvent"/>, paired with the side it would join.
    /// Player parties are never returned: a player chooses through its own encounter menu.
    /// </summary>
    public static List<Candidate> Find(MapEvent mapEvent)
    {
        var found = new List<Candidate>();
        if (mapEvent == null) return found;

        var centre = SearchCentre(mapEvent);
        var radius = SearchRadius(mapEvent);
        if (radius <= 0f) return found;

        var search = MobileParty.StartFindingLocatablesAroundPosition(centre, radius);
        for (var party = MobileParty.FindNextLocatable(ref search);
             party != null;
             party = MobileParty.FindNextLocatable(ref search))
        {
            if (!IsEligible(party, mapEvent)) continue;
            if (!TryChooseSide(mapEvent, party, out var side)) continue;

            found.Add(new Candidate(party, side));
        }

        return found;
    }

    /// <summary>
    /// Vanilla's exclusions, minus the ones that only make sense for a single local player.
    /// </summary>
    /// <remarks>
    /// <c>party == MainParty</c> becomes "any party a player controls": in co-op every player party is somebody's
    /// MainParty, and none of them may be dragged into a battle by proximity - they each decide at their own
    /// encounter menu. The at-sea comparison is against the BATTLE rather than against MainParty, since the
    /// battle is what the party has to be able to reach.
    /// </remarks>
    internal static bool IsEligible(MobileParty party, MapEvent mapEvent)
    {
        if (party == null || mapEvent == null) return false;

        if (party.IsPlayerParty()) return false;
        if (party.MapEvent != null) return false;
        if (party.IsInRaftState) return false;
        if (party.SiegeEvent != null) return false;
        if (party.CurrentSettlement != null) return false;
        if (party.AttachedTo != null) return false;

        if (party.IsCurrentlyAtSea != mapEvent.IsNavalMapEvent && !IsVillageBattle(mapEvent))
            return false;

        // Vanilla's "would this kind of party turn up at all" test.
        return party.IsLordParty
            || party.IsBandit
            || party.IsPatrolParty
            || party.ShouldJoinPlayerBattles;
    }

    /// <summary>
    /// The side this party would take, decided by vanilla's own <see cref="MapEvent.CanPartyJoinBattle"/>.
    /// </summary>
    /// <remarks>
    /// A party that could join BOTH sides is not a party with an opinion - it is one whose diplomacy leaves the
    /// answer undefined - and joining it to whichever side happens to be tested first would make the outcome
    /// depend on evaluation order. Left out instead, so the choice is never arbitrary.
    ///
    /// This is a DELIBERATE divergence from vanilla, which puts such a party in both of its output lists and
    /// lets <c>PlayerEncounter</c> resolve it against the local player's side. There is no such resolution here
    /// - the caller adds each candidate to the one side it was given - so matching vanilla would mean handing
    /// the same party to two opposing sides of the same battle. The visible cost is that a faction at war with
    /// everyone, bandits above all, never joins a co-op player's battle, even though vanilla lists
    /// <c>IsBandit</c> as an eligible type. Fewer joiners is the safe direction; a party fighting itself is not.
    /// </remarks>
    internal static bool TryChooseSide(MapEvent mapEvent, MobileParty party, out BattleSideEnum side)
    {
        side = BattleSideEnum.None;
        if (mapEvent == null || party?.Party == null) return false;

        // Ask whether the answer means anything before believing it. MapEventPatches guards vanilla's check
        // against half-synced state by forcing it to return TRUE when the battle's parties cannot all be
        // resolved - which comes back true for BOTH sides, and is indistinguishable below from a party whose
        // diplomacy genuinely allows either. Treated as a tie it rejects every candidate, so an event with one
        // unresolved party quietly reinforces with nobody at all, looking exactly like "no one was nearby".
        if (!InteractionPatches.CanEvaluateJoinBattle(mapEvent, party.Party))
        {
            UnresolvedSideDecisions++;
            return false;
        }

        var canAttack = mapEvent.CanPartyJoinBattle(party.Party, BattleSideEnum.Attacker);
        var canDefend = mapEvent.CanPartyJoinBattle(party.Party, BattleSideEnum.Defender);

        if (canAttack == canDefend) return false;

        side = canAttack ? BattleSideEnum.Attacker : BattleSideEnum.Defender;
        return true;
    }

    /// <summary>
    /// How many candidates have been turned away because the battle's own state could not be evaluated.
    /// </summary>
    /// <remarks>
    /// Counted rather than logged per candidate: the sweep runs about once a second per live battle, so a
    /// line each would bury everything else. The count is what distinguishes "nobody was near this battle"
    /// from "everybody near it was refused for a reason that has nothing to do with them", and those two look
    /// identical in the logs today.
    /// </remarks>
    internal static int UnresolvedSideDecisions { get; private set; }

    internal static void ResetUnresolvedSideDecisions() => UnresolvedSideDecisions = 0;

    /// <summary>
    /// Where to look. The battle's own position, except for the siege shapes vanilla re-centres: a sally-out is
    /// fought at the besieger's camp, and a blockade out at the settlement's port.
    /// </summary>
    internal static Vec2 SearchCentre(MapEvent mapEvent)
    {
        if (mapEvent.IsSallyOut)
        {
            var campLeader = mapEvent.MapEventSettlement?.SiegeEvent?.BesiegerCamp?.LeaderParty;
            if (campLeader != null) return campLeader.Position.ToVec2();
        }
        else if (mapEvent.IsBlockade || mapEvent.IsBlockadeSallyOut)
        {
            var settlement = mapEvent.MapEventSettlement;
            if (settlement != null) return settlement.PortPosition.ToVec2();
        }

        return mapEvent.Position.ToVec2();
    }

    /// <summary>How far to look, using the same model values vanilla does for each battle shape.</summary>
    internal static float SearchRadius(MapEvent mapEvent)
    {
        var models = Campaign.Current?.Models;
        if (models?.EncounterModel == null) return 0f;

        if (mapEvent.IsBlockade || mapEvent.IsBlockadeSallyOut)
            return models.EncounterModel.NeededMaximumDistanceForEncounteringBlockade * 3f;

        // A battle fought under a siege reaches as far as the parties waiting around that siege.
        if (mapEvent.MapEventSettlement?.SiegeEvent != null && models.MobilePartyAIModel != null)
            return models.MobilePartyAIModel.SettlementDefendingWaitingPositionRadius * 1.25f;

        return models.EncounterModel.GetEncounterJoiningRadius;
    }

    private static bool IsVillageBattle(MapEvent mapEvent)
        => mapEvent.MapEventSettlement?.IsVillage == true;
}
