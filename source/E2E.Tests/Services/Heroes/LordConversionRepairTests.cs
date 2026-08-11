using GameInterface.Services.Heroes.Commands;
using Xunit;

namespace E2E.Tests.Services.Heroes;

/// <summary>
/// Repairing a hero stranded half-way between companion and lord.
/// </summary>
/// <remarks>
/// Turning a companion into a lord is several campaign steps - release from the clan's companion list, clan
/// membership, the Lord occupation, a party. When that sequence aborts partway the hero ends up describing two
/// mutually exclusive things at once.
///
/// Measured live: "Oragur the Knowing" held <c>Occupation = Wanderer</c> and <c>CompanionOf = Wang</c> with no
/// clan, while leading a party whose component was a <c>LordPartyComponent</c> owned by that same clan. The
/// party screen listed him as a companion, the map showed him fielding a party, and the army could not summon
/// him - army membership resolves through the clan, and his was null.
/// </remarks>
public class LordConversionRepairTests
{
    [Fact]
    public void TheStrandedHeroIsRepairedOnEveryAxis()
    {
        // Oragur's exact state: leads a lord's party, still listed as a companion, no clan, not a lord.
        var repair = HeroLordConversionRepairCommand.Diagnose(
            leadsLordParty: true, isCompanionOfAClan: true, hasOwnClan: false, isLord: false);

        Assert.True(repair.IsNeeded);
        Assert.True(repair.ReleaseFromCompanions);
        Assert.True(repair.GrantClan);
        Assert.True(repair.GrantLordOccupation);
    }

    [Fact]
    public void AnOrdinaryCompanionIsNeverPromoted()
    {
        // The safety property, and the reason the party is the gate. A companion riding in someone's party is
        // SUPPOSED to be a wanderer with no clan; "repairing" one would hand the player's companion a lordship
        // they never asked for.
        var repair = HeroLordConversionRepairCommand.Diagnose(
            leadsLordParty: false, isCompanionOfAClan: true, hasOwnClan: false, isLord: false);

        Assert.False(repair.IsNeeded);
        Assert.False(repair.ReleaseFromCompanions);
        Assert.False(repair.GrantClan);
        Assert.False(repair.GrantLordOccupation);
    }

    [Fact]
    public void AHealthyLordIsLeftAlone()
    {
        // Running the command twice, or on a lord who is already fine, must change nothing.
        var repair = HeroLordConversionRepairCommand.Diagnose(
            leadsLordParty: true, isCompanionOfAClan: false, hasOwnClan: true, isLord: true);

        Assert.False(repair.IsNeeded);
    }

    [Fact]
    public void OnlyTheMissingPieceIsRepaired()
    {
        // A conversion can abort at different points - Natu's and Oragur's left different halves undone - so
        // each axis is decided independently rather than as one all-or-nothing switch.
        var repair = HeroLordConversionRepairCommand.Diagnose(
            leadsLordParty: true, isCompanionOfAClan: false, hasOwnClan: true, isLord: false);

        Assert.True(repair.IsNeeded);
        Assert.False(repair.ReleaseFromCompanions);
        Assert.False(repair.GrantClan);
        Assert.True(repair.GrantLordOccupation);
    }

    [Fact]
    public void RidingInSomeoneElsesLordPartyIsNotLeadingOne()
    {
        // The bug the first dry run caught. Hero.PartyBelongedTo is the party a hero BELONGS to, and a
        // player's own party is itself a LordPartyComponent - so testing the component alone matched every
        // companion riding in every lord's party. The scan duly reported "Natu the Grey Falcon leading Jian's
        // Party", which he was not. Applying that finding would have promoted the player's whole retinue.
        Assert.False(HeroLordConversionRepairCommand.LeadsOwnLordParty(
            partyIsALordParty: true, heroIsThePartyLeader: false));
    }

    [Fact]
    public void LeadingYourOwnLordPartyCounts()
    {
        Assert.True(HeroLordConversionRepairCommand.LeadsOwnLordParty(
            partyIsALordParty: true, heroIsThePartyLeader: true));
    }

    [Fact]
    public void LeadingSomethingThatIsNotALordPartyDoesNotCount()
    {
        // Caravans and garrisons have leaders too; only a lord's party evidences an attempted lord conversion.
        Assert.False(HeroLordConversionRepairCommand.LeadsOwnLordParty(
            partyIsALordParty: false, heroIsThePartyLeader: true));
    }

    [Fact]
    public void APartylessHeroIsNeverTouchedWhateverElseIsWrong()
    {
        // Without a lord's party there is no evidence a conversion was ever started, so there is nothing to
        // finish - however inconsistent the rest of the record looks.
        var repair = HeroLordConversionRepairCommand.Diagnose(
            leadsLordParty: false, isCompanionOfAClan: false, hasOwnClan: false, isLord: false);

        Assert.False(repair.IsNeeded);
    }
}
