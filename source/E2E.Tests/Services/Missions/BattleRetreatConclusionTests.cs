using System;
using System.Collections.Generic;
using System.Linq;
using Common.Messaging;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.Messages.Leave;
using GameInterface.Services.MapEvents.TroopSupply;
using GameInterface.Services.Players;
using Missions.Battles;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using Xunit;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Missions;

/// <summary>
/// A retreat is not a defeat. A player who retreats from the mission (BR-051/052) stays a member of the map
/// event on the server; when the other side's mission then ends in its favour, the native conclusion used to
/// count the retreated party as defeated: every man taken prisoner and the hero captured.
/// </summary>
/// <remarks>
/// Measured live on 5 Sep 2026 in both roles (`Scripts/Rig/runs/2026-09-05-1859-army-100-retreat-host`,
/// `1902-army-100-retreat-client`): a retreat at 47 s with 96 of 101 men standing ended three seconds later
/// with the other player's victory, the retreater's roster at 0 / 0 / 0 on all three machines and its hero a
/// prisoner (the winner's prisoner count rose by 98). Vanilla treats a party that left the encounter as
/// escaped, so the server takes each retreated party out of the event through the authoritative leave
/// before it commits the winner's state.
/// </remarks>
public class BattleRetreatConclusionTests : MissionTestEnvironment
{
    public BattleRetreatConclusionTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    [Trait("Requirement", "BR-052")]
    public void RetreatedPartyLeavesTheBattleBeforeTheOtherSideIsDeclaredWinner()
    {
        // host-ctrl attacks and wins; retreat-ctrl defends and retreats with its men still standing.
        var (mapEventId, partyIds) = SetupCoopBattle("host-ctrl", "retreat-ctrl");
        var clients = Clients.ToArray();
        string retreatPartyId = partyIds[1];

        var troop = Server.CreateRegisteredObject<CharacterObject>("retreat_conclusion_troop");
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(retreatPartyId, out var retreatParty));
            retreatParty.MemberRoster.AddToCounts(troop, 40);
        });

        EnterBattle(clients[0], mapEventId);
        EnterBattle(clients[1], mapEventId);

        // The real graceful departure the server sees when a player confirms the retreat.
        DepartBattle("retreat-ctrl", mapEventId, wasRetreat: true);

        string retreatPartyBaseId = null;
        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(mapEventId, out var mapEvent));
            var retreated = Server.Resolve<IBattleTroopReserveBuilder>().GetRetreatedMobilePartyIds(mapEvent);
            Assert.Contains(retreatPartyId, retreated);
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(retreatPartyId, out var retreatParty));
            Assert.True(Server.ObjectManager.TryGetId(retreatParty.Party, out retreatPartyBaseId));
        });

        // The authoritative leave is broadcast to every client; the winner's copy records it here.
        var leftParties = new List<string>();
        Action<MessagePayload<NetworkPartyLeftBattle>> onPartyLeft = payload => leftParties.Add(payload.What.PartyId);
        clients[0].Resolve<IMessageBroker>().Subscribe(onPartyLeft);

        // The remaining player's mission ends in its favour (its enemies were despawned), and the completion
        // handler asks for the authoritative conclusion. Native loot / result arithmetic needs a full campaign,
        // so those methods are disabled exactly as the other conclusion tests disable them.
        var disabled = MapEventDisabledMethods
            .Append(HarmonyLib.AccessTools.Method(typeof(MapEvent), "LootDefeatedPartyCasualties"))
            .Append(HarmonyLib.AccessTools.Method(typeof(MapEvent), "LootDefeatedPartyItems"))
            .Append(HarmonyLib.AccessTools.Method(typeof(MapEvent), "LootDefeatedPartyPrisoners"))
            .Append(HarmonyLib.AccessTools.Method(typeof(MapEvent), "LootDefeatedPartyShips"))
            .Append(HarmonyLib.AccessTools.Method(typeof(MapEvent), "CalculateMapEventResults"))
            .Append(HarmonyLib.AccessTools.Method(typeof(MapEvent), "CommitCalculatedMapEventResults"))
            .Where(method => method != null)
            .ToList();
        Server.Call(() =>
        {
            Assert.True(Server.Resolve<IBattleHostRegistry>().TryGet(mapEventId, out var assignment));
            Server.Resolve<IMessageBroker>().Publish(this,
                new AuthoritativeBattleConclusionRequested(mapEventId, BattleState.AttackerVictory, assignment.Epoch));
        }, disabled);

        // The party escaped before the state was committed: the winner's clients saw it leave the battle, so
        // the conclusion could not count it among the defeated (the finalized event itself is gone by now).
        Assert.Contains(retreatPartyBaseId, leftParties);
        GC.KeepAlive(onPartyLeft);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MobileParty>(retreatPartyId, out var retreatParty));
            Assert.Null(retreatParty.Party.MapEvent);

            // Its men are still its own: the survivors were never converted into the winner's prisoners.
            Assert.Equal(40, retreatParty.MemberRoster.GetTroopCount(troop));

            // And the retreating player is not a captive.
            Assert.True(Server.Resolve<IPlayerManager>().TryGetPlayer("retreat-ctrl", out var player));
            Assert.True(Server.ObjectManager.TryGetObject<Hero>(player.HeroId, out var hero));
            Assert.Null(hero.PartyBelongedToAsPrisoner);
        });
    }

    [Fact]
    [Trait("Requirement", "BR-052")]
    public void ReEngagingClearsTheRetreat_SoTheConclusionFinalizesThePartyNormally()
    {
        var (mapEventId, partyIds) = SetupCoopBattle("host-ctrl", "retreat-ctrl");
        var clients = Clients.ToArray();
        string retreatPartyId = partyIds[1];

        EnterBattle(clients[0], mapEventId);
        EnterBattle(clients[1], mapEventId);
        DepartBattle("retreat-ctrl", mapEventId, wasRetreat: true);

        // Coming back into the battle asks for the player's reserves again (BR-052 re-flatten), which ends the
        // retreat: the party is a full member again and must not be pulled out at the conclusion.
        EnterBattle(clients[1], mapEventId);

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<MapEvent>(mapEventId, out var mapEvent));
            Assert.Empty(Server.Resolve<IBattleTroopReserveBuilder>().GetRetreatedMobilePartyIds(mapEvent));
        });
    }
}
