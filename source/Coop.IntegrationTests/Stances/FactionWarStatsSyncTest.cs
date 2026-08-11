using Coop.Core.Server.Services.Stances.Messages;
using Coop.IntegrationTests.Environment;
using Coop.IntegrationTests.Environment.Instance;
using Coop.IntegrationTests.Kingdoms;
using GameInterface.Services.Stances.Messages;
using TaleWorlds.CampaignSystem;

namespace Coop.IntegrationTests.Stances
{
    /// <summary>
    /// Replication of the war statistics the kingdom diplomacy screen displays.
    /// </summary>
    /// <remarks>
    /// <c>KingdomWarItemVM</c> and <c>MakePeaceDecisionItemVM</c> read casualties, sieges and raids straight off
    /// the <c>StanceLink</c>, and the only thing in the game that writes them is
    /// <c>CampaignWarManagerBehavior</c> - which this mod used to disable on every machine, server included. The
    /// counters were therefore never written anywhere and every war read zero for the whole campaign. Now the
    /// server keeps them with vanilla's own arithmetic and sends the totals on.
    /// </remarks>
    [Collection(KingdomSyncGameThreadCollection.Name)]
    public class FactionWarStatsSyncTest
    {
        internal TestEnvironment TestEnvironment { get; } = new TestEnvironment();

        [Fact]
        public void ServerWarStats_Replicate_ToAllClients()
        {
            var server = TestEnvironment.Server;
            var kingdom1 = server.CreateRegisteredObject<Kingdom>("kingdom1");
            var kingdom2 = server.CreateRegisteredObject<Kingdom>("kingdom2");

            server.SimulateMessage(this, new WarStatsRecorded(kingdom1, kingdom2,
                troopCasualties1: 187, troopCasualties2: 87,
                successfulSieges1: 2, successfulSieges2: 0,
                successfulTownSieges1: 1, successfulTownSieges2: 0,
                successfulRaids1: 4, successfulRaids2: 3));

            Assert.Single(server.NetworkSentMessages.GetMessages<NetworkWarStats>(),
                message => message.Faction1Id == "kingdom1"
                           && message.Faction2Id == "kingdom2"
                           && message.TroopCasualties1 == 187
                           && message.TroopCasualties2 == 87
                           && message.SuccessfulSieges1 == 2
                           && message.SuccessfulTownSieges1 == 1
                           && message.SuccessfulRaids1 == 4
                           && message.SuccessfulRaids2 == 3);

            foreach (EnvironmentInstance client in TestEnvironment.Clients)
            {
                Assert.Single(client.InternalMessages.GetMessages<WarStatsChanged>(),
                    message => message.Faction1Id == "kingdom1"
                               && message.Faction2Id == "kingdom2"
                               && message.TroopCasualties1 == 187
                               && message.TroopCasualties2 == 87
                               && message.SuccessfulSieges1 == 2
                               && message.SuccessfulTownSieges1 == 1
                               && message.SuccessfulRaids1 == 4
                               && message.SuccessfulRaids2 == 3);
            }
        }

        [Fact]
        public void EverySideOfEveryStatisticSurvivesTheWire()
        {
            // Each statistic is a PAIR - what faction 1 suffered and what faction 2 suffered - and the two are
            // adjacent fields of the same type. A transposed pair would show each kingdom the other's losses,
            // which reads as plausible on screen and is wrong in exactly the way nobody notices. So the
            // asymmetric values are asserted individually rather than as a total.
            var server = TestEnvironment.Server;
            var kingdom1 = server.CreateRegisteredObject<Kingdom>("kingdom1");
            var kingdom2 = server.CreateRegisteredObject<Kingdom>("kingdom2");

            server.SimulateMessage(this, new WarStatsRecorded(kingdom1, kingdom2,
                troopCasualties1: 11, troopCasualties2: 22,
                successfulSieges1: 33, successfulSieges2: 44,
                successfulTownSieges1: 55, successfulTownSieges2: 66,
                successfulRaids1: 77, successfulRaids2: 88));

            var sent = Assert.Single(server.NetworkSentMessages.GetMessages<NetworkWarStats>());

            Assert.Equal(11, sent.TroopCasualties1);
            Assert.Equal(22, sent.TroopCasualties2);
            Assert.Equal(33, sent.SuccessfulSieges1);
            Assert.Equal(44, sent.SuccessfulSieges2);
            Assert.Equal(55, sent.SuccessfulTownSieges1);
            Assert.Equal(66, sent.SuccessfulTownSieges2);
            Assert.Equal(77, sent.SuccessfulRaids1);
            Assert.Equal(88, sent.SuccessfulRaids2);
        }

        [Fact]
        public void AnUnregisteredFaction_SendsNothing_RatherThanASilentlyWrongPairing()
        {
            // The payload identifies both factions by id. If one cannot be resolved, sending the message anyway
            // would apply this pair's numbers to whatever the receiver made of the missing id.
            var server = TestEnvironment.Server;
            var kingdom1 = server.CreateRegisteredObject<Kingdom>("kingdom1");

            server.SimulateMessage(this, new WarStatsRecorded(kingdom1, null,
                troopCasualties1: 1, troopCasualties2: 2,
                successfulSieges1: 0, successfulSieges2: 0,
                successfulTownSieges1: 0, successfulTownSieges2: 0,
                successfulRaids1: 0, successfulRaids2: 0));

            Assert.Equal(0, server.NetworkSentMessages.GetMessageCount<NetworkWarStats>());
        }
    }
}
