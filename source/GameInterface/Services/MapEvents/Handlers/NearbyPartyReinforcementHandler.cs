using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Configuration;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Patches;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.MapEvents.Messages.Start;
using GameInterface.Services.PlayerCaptivityService.Messages;
using Serilog;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.Handlers;

/// <summary>
/// Server-side replacement for vanilla's nearby-party reinforcement, so AI parties standing next to a
/// player's battle actually join it.
/// </summary>
/// <remarks>
/// Vanilla does this from <c>PlayerEncounter.CheckNearbyPartiesToJoinPlayerMapEvent</c>, driven off
/// PlayerEncounter.Update. Co-op suppresses that method outright, because on a client it would mutate the
/// shared MapEventSide locally and desync - so nothing ever pulled nearby parties in and a friendly army
/// could sit beside your battle doing nothing.
///
/// The selection itself is vanilla's and needs no porting: PlayerEncounter's method is a one-line delegate to
/// <c>EncounterModel.FindNonAttachedNpcPartiesWhoWillJoinPlayerEncounter(list, list)</c>, which takes only the
/// two side lists - no MainParty, no encounter state - so the headless host can call it directly.
///
/// Replication is already in place: <see cref="MapEventPatches"/>' AddInvolvedPartyInternal postfix
/// broadcasts an AI join while the battle is inside its
/// <see cref="ModConfigProvider.ModOptions.PlayerBattleAiJoinWindowHours"/> window. That window existed with
/// nothing to populate it; this is what populates it.
/// </remarks>
internal class NearbyPartyReinforcementHandler : IHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<NearbyPartyReinforcementHandler>();

    private readonly IMessageBroker messageBroker;

    public NearbyPartyReinforcementHandler(IMessageBroker messageBroker)
    {
        this.messageBroker = messageBroker;
        messageBroker.Subscribe<PlayerJoinedBattle>(Handle_PlayerJoinedBattle);
        messageBroker.Subscribe<CampaignTick>(Handle_CampaignTick);
        messageBroker.Subscribe<LiveBattleReinforcementTick>(Handle_LiveBattleReinforcementTick);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<PlayerJoinedBattle>(Handle_PlayerJoinedBattle);
        messageBroker.Unsubscribe<CampaignTick>(Handle_CampaignTick);
        messageBroker.Unsubscribe<LiveBattleReinforcementTick>(Handle_LiveBattleReinforcementTick);
    }

    /// <summary>
    /// The same sweep as <see cref="Handle_CampaignTick"/>, driven by real time instead of campaign time.
    /// </summary>
    /// <remarks>
    /// Campaign time stops for the duration of a co-op battle (see <see cref="LiveBattleReinforcementTick"/>),
    /// so without this the battle-start scan is the only one that ever runs and a lord who was slightly too far
    /// away at the opening bell can never join.
    /// </remarks>
    private void Handle_LiveBattleReinforcementTick(MessagePayload<LiveBattleReinforcementTick> payload)
        => SweepBattlesForReinforcements();

    /// <summary>
    /// The moment a player's battle opens its AI-join window is the one moment reinforcement is guaranteed to
    /// get a look in. CampaignTick alone is not enough: map time stops while a player sits in an encounter, so
    /// a tick-driven scan can go the entire battle without running - which is why nearby lords stood and
    /// watched. This fires from MapEvent.Initialize's postfix, where the window is opened.
    /// </summary>
    private void Handle_PlayerJoinedBattle(MessagePayload<PlayerJoinedBattle> payload)
    {
        if (!ModInformation.IsServer) return;

        // Published with the MapEvent as its source; E2E publishes the same event with a test object, so type-check.
        if (payload.Who is not MapEvent mapEvent) return;

        var skip = WhyNotReinforce(mapEvent);
        if (skip != null)
        {
            Logger.Debug("[Reinforce] battle {MapEventId} opened its join window but will not reinforce: {Reason}",
                mapEvent.StringId ?? "<no id>", skip);
            return;
        }

        // Never let a reinforcement failure take a battle down with it - the battle is playable without it.
        try
        {
            Reinforce(mapEvent);
        }
        catch (System.Exception e)
        {
            Logger.Error(e, "Reinforcing player battle {MapEventId} at start failed", mapEvent.StringId ?? "<no id>");
        }
    }

    private void Handle_CampaignTick(MessagePayload<CampaignTick> payload) => SweepBattlesForReinforcements();

    /// <summary>
    /// How often the sweep may actually scan, in real seconds.
    /// </summary>
    /// <remarks>
    /// The campaign tick fires far faster than it is worth re-answering this question - measured at ~95 sweeps
    /// a second live. That was harmless while every sweep bailed at the join-window check, but each one now
    /// walks the map's locatable index for every live battle, so it has to be paced. Real time rather than
    /// campaign time, because the campaign clock stops for the duration of a co-op battle, which is precisely
    /// when the sweep needs to keep running.
    /// </remarks>
    internal const double SweepIntervalSeconds = 1.0;

    private readonly Stopwatch sinceLastSweep = Stopwatch.StartNew();

    private void SweepBattlesForReinforcements()
    {
        if (!ModInformation.IsServer) return;
        if (!IsSweepDue(sinceLastSweep.Elapsed.TotalSeconds)) return;
        sinceLastSweep.Restart();

        var events = Campaign.Current?.MapEventManager?.MapEvents;
        if (events == null) return;

        // ToArray: adding a party mutates the event graph while we walk it.
        foreach (var mapEvent in events.ToArray())
        {
            EnsureJoinWindowForPlayerBattle(mapEvent);

            var skip = WhyNotReinforce(mapEvent);
            if (skip != null)
            {
                // Only trace player battles - an ordinary AI skirmish skipping this is not interesting.
                if (mapEvent != null && mapEvent.InvolvedParties.Any(p => p.IsMobile && p.MobileParty?.IsPlayerParty() == true))
                    Logger.Debug("[Reinforce] skipping player battle {MapEventId}: {Reason}",
                        mapEvent.StringId ?? "<no id>", skip);
                continue;
            }

            Reinforce(mapEvent);
        }
    }

    /// <summary>
    /// Gives a player's battle an AI-join window if it has never had one.
    /// </summary>
    /// <remarks>
    /// The window is normally opened by <c>MapEvent.Initialize</c>'s postfix, which is fine for a battle that
    /// starts while the server is running. A battle that arrives with a LOADED SAVE never runs Initialize: the
    /// event is deserialised with its parties already attached, so no window is ever opened and the battle is
    /// skipped for its entire life. Measured on a save taken mid-siege: 18,033 consecutive scans, every one
    /// refused with "none opened", so no lord ever joined however many were stood around the walls.
    ///
    /// The same silence also stopped the mid-battle round restart, which keys off parties being ADDED - a
    /// battle nothing can join never publishes that either.
    ///
    /// Opening one here is safe because <see cref="InteractionPatches.OpenAiJoinWindowIfNeeded"/> is a no-op
    /// once a window exists, including an EXPIRED one: a battle whose window ran out stays closed, which is
    /// the intended behaviour. Only a battle that never had one at all is given one.
    /// </remarks>
    /// <summary>Pure so the pacing rule is testable without a clock.</summary>
    internal static bool IsSweepDue(double elapsedSeconds) => elapsedSeconds >= SweepIntervalSeconds;

    private static void EnsureJoinWindowForPlayerBattle(MapEvent mapEvent)
    {
        if (mapEvent == null || mapEvent.IsFinalized) return;
        if (!mapEvent.InvolvedParties.Any(p => p.IsMobile && p.MobileParty?.IsPlayerParty() == true)) return;

        InteractionPatches.OpenAiJoinWindowIfNeeded(mapEvent);
    }

    /// <summary>
    /// Mirrors vanilla's own guards, plus the co-op join window. Returns null when the event SHOULD be
    /// reinforced, otherwise the reason it was skipped - so "no reinforcement" is diagnosable instead of
    /// silent. A bare "nothing happened" is indistinguishable from "never ran", which cost real time.
    /// </summary>
    private static string WhyNotReinforce(MapEvent mapEvent)
    {
        if (mapEvent == null) return "null";
        if (mapEvent.IsFinalized) return "finalized";

        // Vanilla refuses these outright - a raid, a wall assault, and the forced supply/volunteer shakedowns
        // are not battles nearby parties may wander into.
        if (mapEvent.IsRaid) return "raid";
        if (mapEvent.IsSiegeAssault) return "siege assault";
        if (mapEvent.IsForcingSupplies) return "forcing supplies";
        if (mapEvent.IsForcingVolunteers) return "forcing volunteers";

        if (mapEvent.MapEventSettlement?.IsHideout == true) return "hideout";

        // Only player battles reinforce, and only while the window the broadcast path checks is still open -
        // otherwise the join would apply on the server and never reach the clients.
        if (!InteractionPatches.IsWithinAiJoinWindow(mapEvent))
            return "outside the AI join window (none opened, or it expired)";

        if (!mapEvent.InvolvedParties.Any(p => p.IsMobile && p.MobileParty?.IsPlayerParty() == true))
            return "no player party involved";

        return null;
    }

    /// <summary>
    /// Pulls in every nearby party that would join, using <see cref="BattleJoinCandidates"/> rather than
    /// vanilla's encounter model.
    /// </summary>
    /// <remarks>
    /// The model was asked directly until it was found to be unusable from a server: it searches around
    /// <c>MobileParty.MainParty</c>, decides sides from <c>PlayerEncounter</c>, and takes its two lists as
    /// (player side, enemy side) rather than (attacker, defender). See <see cref="BattleJoinCandidates"/>.
    /// </remarks>
    private static void Reinforce(MapEvent mapEvent)
    {
        var unresolvedBefore = BattleJoinCandidates.UnresolvedSideDecisions;
        var candidates = BattleJoinCandidates.Find(mapEvent);
        var unresolved = BattleJoinCandidates.UnresolvedSideDecisions - unresolvedBefore;

        Logger.Debug("[Reinforce] {MapEventId}: {Count} nearby parties would join",
            mapEvent.StringId ?? "<no id>", candidates.Count);

        // "Nobody was nearby" and "everybody nearby was refused because the battle could not be evaluated"
        // are the same empty list, and only one of them is a problem. Say which at Warning, because the
        // second means a battle is reinforcing with nobody for a reason unrelated to who is around it.
        if (unresolved > 0)
        {
            Logger.Warning(
                "[Reinforce] {MapEventId}: {Unresolved} nearby part(ies) could not be assigned a side because the battle's parties are not all resolved; they were skipped",
                mapEvent.StringId ?? "<no id>", unresolved);
        }

        if (candidates.Count == 0) return;

        foreach (var candidate in candidates)
        {
            var mapEventSide = mapEvent.GetMapEventSide(candidate.Side);
            if (mapEventSide == null) continue;

            Logger.Debug("Nearby party {PartyId} joins battle {MapEventId} on the {Side} side",
                candidate.Party.StringId, mapEvent.StringId ?? "<no id>", candidate.Side);

            mapEventSide.AddNearbyPartyToPlayerMapEvent(candidate.Party);
        }
    }
}
