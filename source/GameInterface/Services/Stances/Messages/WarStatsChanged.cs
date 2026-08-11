using Common.Messaging;

namespace GameInterface.Services.Stances.Messages
{
    /// <summary>
    /// Command handled on the client when the server sends NetworkWarStats: the authoritative war totals for
    /// one pair of factions.
    /// </summary>
    public class WarStatsChanged : ICommand
    {
        public string Faction1Id { get; }
        public string Faction2Id { get; }
        public int TroopCasualties1 { get; }
        public int TroopCasualties2 { get; }
        public int SuccessfulSieges1 { get; }
        public int SuccessfulSieges2 { get; }
        public int SuccessfulTownSieges1 { get; }
        public int SuccessfulTownSieges2 { get; }
        public int SuccessfulRaids1 { get; }
        public int SuccessfulRaids2 { get; }

        public WarStatsChanged(string faction1Id, string faction2Id,
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
