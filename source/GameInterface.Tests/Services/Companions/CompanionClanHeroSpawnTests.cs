using Common;
using Common.Util;
using GameInterface.Services.Companions.Patches;
using System;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using Xunit;

namespace GameInterface.Tests.Services.Companions;

/// <summary>
/// Who spawns the two lords a new companion-to-lord clan starts with.
/// </summary>
/// <remarks>
/// Promoting a companion to a lord is replayed on EVERY machine (<c>DoClanNameSelection</c> is a
/// server-to-all command), and the spawn invents its heroes from unsynchronised randomness — a random lord
/// template, then random skills and relations. Running it on a client therefore minted that client its OWN
/// pair of heroes, on top of the pair the server minted and replicated to it: four members on the client,
/// two on the server, two of them known to nobody else. The clan screen enumerates exactly those members,
/// which is where it came apart.
///
/// Heroes replicate on construction (HeroRegistry hooks the Hero constructor), so a client has no need to
/// create them — it receives the server's.
/// </remarks>
public class CompanionClanHeroSpawnTests : IDisposable
{
    private readonly bool wasServer = ModInformation.IsServer;

    public void Dispose() => ModInformation.IsServer = wasServer;

    [Fact]
    public void AClientSpawnsNothing_AndDoesNotEvenLookAtTheCompanion()
    {
        ModInformation.IsServer = false;

        var behavior = ObjectHelper.SkipConstructor<CompanionRolesCampaignBehavior>();

        // Every argument is null: reaching ANY of the spawn body would dereference one of them. Returning
        // cleanly is therefore proof the client path bailed out before doing any work, not merely that it
        // happened to create nothing.
        var runOriginal = CompanionRolesPatches.SpawnNewHeroesForNewCompanionClanPrefix(
            behavior, companionHero: null, clan: null, settlement: null);

        Assert.False(runOriginal, "the native spawn must stay skipped on a client too");
    }

    [Fact]
    public void TheServerStillSpawnsThem()
    {
        // The guard must be CLIENT-only. If it ever became unconditional, nobody would create the clan's
        // lords at all and the new clan would be empty everywhere. With null arguments the server path
        // cannot get far, but that it reaches into them at all is what distinguishes it from the client's
        // early return above.
        ModInformation.IsServer = true;

        var behavior = ObjectHelper.SkipConstructor<CompanionRolesCampaignBehavior>();

        Assert.Throws<NullReferenceException>(() =>
            CompanionRolesPatches.SpawnNewHeroesForNewCompanionClanPrefix(
                behavior, companionHero: null, clan: null, settlement: null));
    }
}
