using Common;
using System;
using Xunit;

namespace Coop.Tests;

/// <summary>
/// The two render-free roles must stay distinguishable.
/// </summary>
/// <remarks>
/// A driven client and a dedicated server both run without a renderer, so they share every render-free path -
/// the map scene, the view guards, party visuals. What they must not share is the server half: opening a
/// console, polling the command file, booting a Steam GAME SERVER, or signalling that a campaign is up and
/// ready for peers.
///
/// This cannot be expressed with <see cref="ModInformation.IsServer"/>, which is only set once a session
/// starts. The render-free gates run at boot, from launch arguments, when IsServer is still its default on a
/// dedicated server too - so gating them on it would disable the server rather than exclude the client.
/// </remarks>
public class HeadlessRoleTests : IDisposable
{
    private readonly bool wasHeadless = ModInformation.IsHeadless;
    private readonly bool wasHeadlessClient = ModInformation.IsHeadlessClient;

    public void Dispose()
    {
        ModInformation.IsHeadless = wasHeadless;
        ModInformation.IsHeadlessClient = wasHeadlessClient;
    }

    [Fact]
    public void ARenderedProcessIsNeitherHeadlessRole()
    {
        ModInformation.IsHeadless = false;
        ModInformation.IsHeadlessClient = false;

        Assert.False(ModInformation.IsHeadlessServer);
    }

    [Fact]
    public void TheDedicatedServerIsAHeadlessServer()
    {
        ModInformation.IsHeadless = true;
        ModInformation.IsHeadlessClient = false;

        Assert.True(ModInformation.IsHeadlessServer);
    }

    [Fact]
    public void ADrivenClientIsHeadlessButNotAServer()
    {
        // The whole point: render-free paths apply, server paths do not.
        ModInformation.IsHeadless = true;
        ModInformation.IsHeadlessClient = true;

        Assert.True(ModInformation.IsHeadless);
        Assert.False(ModInformation.IsHeadlessServer);
    }

    [Fact]
    public void AMalformedStateNeverClaimsToBeAServer()
    {
        // IsHeadlessClient without IsHeadless is nonsense - nothing sets it - but if a future change ever
        // produced it, the failure must be "not a server" rather than "a server". Booting a Steam game server
        // by accident is the expensive direction to be wrong in.
        ModInformation.IsHeadless = false;
        ModInformation.IsHeadlessClient = true;

        Assert.False(ModInformation.IsHeadlessServer);
    }
}
