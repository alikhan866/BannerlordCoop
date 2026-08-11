using GameInterface.Services.Kingdoms.Commands;
using Xunit;

namespace E2E.Tests.Services.Kingdoms;

/// <summary>
/// Server-side diplomacy admin: ending an alliance, and moving a clan into a kingdom for a fee.
/// </summary>
/// <remarks>
/// An alliance is not a stance - <c>StanceType</c> here is only Neutral and War - so it can only be ended
/// through <c>AllianceCampaignBehavior</c>. The shortcut of declaring war and immediately making peace does
/// end it, but <c>OnWarDeclared</c> applies <b>-100 relation</b> between the two kingdom leaders plus a
/// dishonourable trait hit, which is not what "we just want to be neutral" means.
/// </remarks>
public class DiplomacyAdminTests
{
    [Fact]
    public void AClanAlreadyInAKingdomDefects()
    {
        // ApplyByJoinToKingdom assumes an unaligned clan; using it on an aligned one leaves the old kingdom
        // still listing the clan. The defection call takes the old kingdom and unwinds that membership.
        Assert.True(DiplomacyAdminCommands.JoinIsDefection(clanAlreadyInAKingdom: true));
    }

    [Fact]
    public void AnIndependentClanPlainJoins()
    {
        Assert.False(DiplomacyAdminCommands.JoinIsDefection(clanAlreadyInAKingdom: false));
    }

    [Fact]
    public void APayerWhoCannotCoverTheFeeIsRefused()
    {
        // THE safety property. GiveGoldAction will happily take a hero negative, and a ruler in the red starts
        // failing wages across every party they own - a far bigger mess than a refused command.
        Assert.False(DiplomacyAdminCommands.PaymentIsAffordable(payerGold: 900_000, amount: 1_100_000));
    }

    [Fact]
    public void ExactlyEnoughGoldIsAllowed()
    {
        Assert.True(DiplomacyAdminCommands.PaymentIsAffordable(payerGold: 1_100_000, amount: 1_100_000));
        Assert.True(DiplomacyAdminCommands.PaymentIsAffordable(payerGold: 1_100_001, amount: 1_100_000));
    }

    [Fact]
    public void AFreeTransferNeedsNoGoldAtAll()
    {
        // Joining with no fee must not be blocked by a broke ruler.
        Assert.True(DiplomacyAdminCommands.PaymentIsAffordable(payerGold: 0, amount: 0));
    }
}
