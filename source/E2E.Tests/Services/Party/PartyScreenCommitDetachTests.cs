using GameInterface.Services.Party.Patches;
using Xunit;

namespace E2E.Tests.Services.Party;

/// <summary>
/// The party screen must not detach heroes while it commits.
/// </summary>
/// <remarks>
/// Closing the party screen calls <c>Reset(true)</c>, which clears rosters that are still bound to the live
/// party. Vanilla treats removing a HERO from a roster as detaching that hero, so the clear cascaded into the
/// real objects: <c>Hero.PartyBelongedTo</c> was nulled and <c>ChangePartyLeader(null)</c> followed, wiping
/// <c>LordPartyComponent._leader</c>.
///
/// It ran inside an <c>AllowedThread</c>, so the leader change applied locally and published nothing - the
/// client lost the leader of the party it had just been looking at while the server kept its own, silently.
/// That is a lord party with no leader on the client only: base size limit, missing from Army Management,
/// and a companion belonging to no party. Rejoining appeared to fix it only because the client re-synced.
/// </remarks>
public class PartyScreenCommitDetachTests
{
    [Fact]
    public void ClearingRostersDuringACommitDoesNotDetachHeroes()
    {
        // The bug: clicking Done wiped the leader of the party being viewed.
        Assert.False(PartyScreenCommitDetachPatches.ShouldApplyHeroDetach(inPartyScreenCommit: true));
    }

    [Fact]
    public void ARealRemovalStillDetachesTheHero()
    {
        // THE safety property. A hero genuinely leaving, captured, or killed must still be detached -
        // suppressing that everywhere would leave heroes attached to parties they are no longer in, which
        // is the opposite corruption.
        Assert.True(PartyScreenCommitDetachPatches.ShouldApplyHeroDetach(inPartyScreenCommit: false));
    }
}
