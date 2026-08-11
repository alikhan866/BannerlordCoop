using System;
using System.Collections.Generic;
using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.Stances.Messages;
using HarmonyLib;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;

namespace GameInterface.Services.Battles.Patches.Disable;

/// <summary>
/// Lets the SERVER keep the war statistics the diplomacy screen displays, and keeps clients from inventing
/// their own.
/// </summary>
/// <remarks>
/// <c>CampaignWarManagerBehavior</c> is the only thing in the game that writes casualties, sieges and raids into
/// a <c>StanceLink</c> - <c>KingdomWarItemVM</c> and <c>MakePeaceDecisionItemVM</c> read them straight back out
/// of it. This used to refuse <c>RegisterEvents</c> outright, on every machine, so those counters were never
/// written ANYWHERE and every war in the diplomacy screen read zero for the whole campaign. Not a replication
/// gap: the feature was simply switched off.
///
/// Registering on the server only, which is the same shape every other disabled campaign behaviour here uses.
/// The behaviour listens to exactly two events and does nothing else, so this is the whole of it. Clients must
/// still not run it - they do not see every map event, so they would accumulate a different and quietly wrong
/// set of numbers - and instead receive the server's totals through <see cref="WarStatsRecorded"/>.
/// </remarks>
[HarmonyPatch(typeof(CampaignWarManagerBehavior))]
internal class DisableCampaignWarManagerBehavior
{
    [HarmonyPatch(nameof(CampaignWarManagerBehavior.RegisterEvents))]
    static bool Prefix() => ModInformation.IsServer;
}

/// <summary>
/// Publishes the server's updated war statistics after vanilla has written them, so clients can be told.
/// </summary>
/// <remarks>
/// Postfixes rather than replacements: vanilla's arithmetic decides what a "successful siege" is and how
/// casualties are apportioned between the two sides, and re-deriving that here would be a second implementation
/// to keep in step with the first. This only reads what it produced.
///
/// The message carries ABSOLUTE totals, not deltas. A total is idempotent - applying it twice, or out of order,
/// or to a client that missed the previous battle entirely, still lands on the right number - whereas a dropped
/// or duplicated delta would leave that client permanently wrong with nothing to correct it.
/// </remarks>
[HarmonyPatch(typeof(CampaignWarManagerBehavior))]
internal class CampaignWarManagerBehaviorPatches
{
    private static readonly ILogger Logger = LogManager.GetLogger<CampaignWarManagerBehaviorPatches>();

    [HarmonyPatch(nameof(CampaignWarManagerBehavior.MapEventEnded))]
    [HarmonyPrefix]
    private static void MapEventEndedPrefix(MapEvent mapEvent) => FillSideCasualties(mapEvent);

    [HarmonyPatch(nameof(CampaignWarManagerBehavior.MapEventEnded))]
    [HarmonyPostfix]
    private static void MapEventEndedPostfix(MapEvent mapEvent) => PublishStatsFor(mapEvent);

    /// <summary>
    /// Supplies the per-side casualty total that a coop battle never accumulates.
    /// </summary>
    /// <remarks>
    /// Vanilla's war statistics read ONE field - <c>MapEventSide.TroopCasualties</c> - and it is incremented
    /// only by <c>MapEventSide.OnTroopKilled/Wounded/Routed</c>. In a coop battle casualties do not travel that
    /// way: they are reported by whoever owned the agent, applied on the server per PARTY, and the side-level
    /// counter is never touched. So it stays at zero, and every war a player fights records zero casualties
    /// while AI-only wars accumulate normally.
    ///
    /// Measured exactly so: a battle with hundreds of dead published "Southern Empire vs Wang: casualties 0/0",
    /// while "Western Empire vs Ki: casualties 8/65" - a war between two AI kingdoms - counted correctly. From
    /// the diplomacy screen that is a war that apparently never cost anybody anything.
    ///
    /// Rebuilt from the per-party rosters rather than by un-suppressing the side accounting. Those rosters are
    /// the authoritative record the server already maintains - the loot calculation reads the same ones, and it
    /// produces correct results - whereas letting the side counter increment locally would double-count against
    /// the replicated path that deliberately owns casualties.
    ///
    /// ASSIGNED rather than added to, so running twice cannot inflate the number.
    /// </remarks>
    private static void FillSideCasualties(MapEvent mapEvent)
    {
        if (ModInformation.IsClient || mapEvent == null) return;

        try
        {
            foreach (var side in new[] { BattleSideEnum.Attacker, BattleSideEnum.Defender })
            {
                var mapEventSide = mapEvent.GetMapEventSide(side);
                if (mapEventSide?.Parties == null) continue;

                var counted = CountCasualties(mapEventSide);
                if (counted <= mapEventSide.TroopCasualties) continue; // already accounted for; never lower it

                Logger.Information("[WarStats] {Side} of {MapEvent} recorded {Counted} casualties from its rosters (side counter read {Had})",
                    side, mapEvent.StringId, counted, mapEventSide.TroopCasualties);

                mapEventSide.TroopCasualties = counted;
            }
        }
        catch (Exception e)
        {
            Logger.Warning(e, "[WarStats] Could not rebuild the side casualty totals of {MapEvent}", mapEvent.StringId);
        }
    }

    /// <summary>Everyone a side lost: killed, wounded and routed, across every party on it.</summary>
    /// <remarks>
    /// The same three buckets <c>MapEventSide.OnTroopKilled</c>, <c>OnTroopWounded</c> and <c>OnTroopRouted</c>
    /// would each have counted, so the total matches what vanilla's incremental count would have produced.
    /// </remarks>
    private static int CountCasualties(MapEventSide mapEventSide)
    {
        int total = 0;
        foreach (var party in mapEventSide.Parties)
        {
            if (party == null) continue;
            total += party.DiedInBattle?.TotalManCount ?? 0;
            total += party.WoundedInBattle?.TotalManCount ?? 0;
            total += party.RoutedInBattle?.TotalManCount ?? 0;
        }
        return total;
    }

    [HarmonyPatch(nameof(CampaignWarManagerBehavior.OnRaidCompleted))]
    [HarmonyPostfix]
    private static void OnRaidCompletedPostfix(RaidEventComponent raidEvent) => PublishStatsFor(raidEvent?.MapEvent);

    /// <summary>
    /// Publishes the current totals for every warring pair of factions that met in this battle.
    /// </summary>
    /// <remarks>
    /// Deliberately every at-war pair present, rather than only the pair vanilla happened to touch. Whatever it
    /// updated is a subset of these, and because the payload is an absolute total, publishing a pair whose
    /// numbers did not move is a no-op on the receiving side. Guessing the exact pair and getting it wrong would
    /// not be.
    /// </remarks>
    private static void PublishStatsFor(MapEvent mapEvent)
    {
        if (mapEvent == null) return;

        var attackers = FactionsOn(mapEvent, BattleSideEnum.Attacker);
        var defenders = FactionsOn(mapEvent, BattleSideEnum.Defender);

        foreach (var attacker in attackers)
        {
            foreach (var defender in defenders)
            {
                if (attacker == defender) continue;

                var stance = attacker.GetStanceWith(defender);
                if (stance == null || !stance.IsAtWar) continue;

                Logger.Information(
                    "[WarStats] {F1} vs {F2}: casualties {C1}/{C2}, sieges {S1}/{S2}, townSieges {T1}/{T2}, raids {R1}/{R2}",
                    stance.Faction1?.Name, stance.Faction2?.Name,
                    stance.TroopCasualties1, stance.TroopCasualties2,
                    stance.SuccessfulSieges1, stance.SuccessfulSieges2,
                    stance.SuccessfulTownSieges1, stance.SuccessfulTownSieges2,
                    stance.SuccessfulRaids1, stance.SuccessfulRaids2);

                MessageBroker.Instance.Publish(mapEvent, new WarStatsRecorded(
                    stance.Faction1, stance.Faction2,
                    stance.TroopCasualties1, stance.TroopCasualties2,
                    stance.SuccessfulSieges1, stance.SuccessfulSieges2,
                    stance.SuccessfulTownSieges1, stance.SuccessfulTownSieges2,
                    stance.SuccessfulRaids1, stance.SuccessfulRaids2));
            }
        }
    }

    private static HashSet<IFaction> FactionsOn(MapEvent mapEvent, BattleSideEnum side)
    {
        var factions = new HashSet<IFaction>();
        var parties = mapEvent.GetMapEventSide(side)?.Parties;
        if (parties == null) return factions;

        foreach (var party in parties)
        {
            var faction = party?.Party?.MapFaction;
            if (faction != null) factions.Add(faction);
        }
        return factions;
    }
}
