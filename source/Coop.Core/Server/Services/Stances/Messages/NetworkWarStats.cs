using Common.Messaging;
using ProtoBuf;

namespace Coop.Core.Server.Services.Stances.Messages
{
    /// <summary>
    /// Network message replicating the war statistics between two factions - the casualty, siege and raid
    /// counts the diplomacy screen displays - to clients.
    /// </summary>
    /// <remarks>
    /// Absolute totals rather than deltas, so the message is idempotent: a client that missed a battle, or
    /// received one twice, still ends up with the server's number.
    /// </remarks>
    [ProtoContract(SkipConstructor = true)]
    public class NetworkWarStats : ICommand
    {
        [ProtoMember(1)]
        public string Faction1Id { get; }
        [ProtoMember(2)]
        public string Faction2Id { get; }
        [ProtoMember(3, IsRequired = true)]
        public int TroopCasualties1 { get; }
        [ProtoMember(4, IsRequired = true)]
        public int TroopCasualties2 { get; }
        [ProtoMember(5, IsRequired = true)]
        public int SuccessfulSieges1 { get; }
        [ProtoMember(6, IsRequired = true)]
        public int SuccessfulSieges2 { get; }
        [ProtoMember(7, IsRequired = true)]
        public int SuccessfulTownSieges1 { get; }
        [ProtoMember(8, IsRequired = true)]
        public int SuccessfulTownSieges2 { get; }
        [ProtoMember(9, IsRequired = true)]
        public int SuccessfulRaids1 { get; }
        [ProtoMember(10, IsRequired = true)]
        public int SuccessfulRaids2 { get; }

        public NetworkWarStats(string faction1Id, string faction2Id,
            int troopCasualties1, int troopCasualties2,
            int successfulSieges1, int successfulSieges2,
            int successfulTownSieges1, int successfulTownSieges2,
            int successfulRaids1, int successfulRaids2)
        {
            Faction1Id = faction1Id;
            Faction2Id = faction2Id;
            TroopCasualties1 = troopCasualties1;
            TroopCasualties2 = troopCasualties2;
            SuccessfulSieges1 = successfulSieges1;
            SuccessfulSieges2 = successfulSieges2;
            SuccessfulTownSieges1 = successfulTownSieges1;
            SuccessfulTownSieges2 = successfulTownSieges2;
            SuccessfulRaids1 = successfulRaids1;
            SuccessfulRaids2 = successfulRaids2;
        }
    }
}
