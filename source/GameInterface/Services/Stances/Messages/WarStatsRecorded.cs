using Common.Messaging;
using TaleWorlds.CampaignSystem;

namespace GameInterface.Services.Stances.Messages
{
    /// <summary>
    /// Raised on the server after a battle or raid updates the war statistics the diplomacy screen shows.
    /// Carries absolute totals, so a client that missed earlier battles still lands on the right numbers.
    /// </summary>
    public readonly struct WarStatsRecorded : IEvent
    {
        public readonly IFaction Faction1;
        public readonly IFaction Faction2;
        public readonly int TroopCasualties1;
        public readonly int TroopCasualties2;
        public readonly int SuccessfulSieges1;
        public readonly int SuccessfulSieges2;
        public readonly int SuccessfulTownSieges1;
        public readonly int SuccessfulTownSieges2;
        public readonly int SuccessfulRaids1;
        public readonly int SuccessfulRaids2;

        public WarStatsRecorded(IFaction faction1, IFaction faction2,
            int troopCasualties1, int troopCasualties2,
            int successfulSieges1, int successfulSieges2,
            int successfulTownSieges1, int successfulTownSieges2,
            int successfulRaids1, int successfulRaids2)
        {
            Faction1 = faction1;
            Faction2 = faction2;
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
