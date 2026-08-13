using System;
using System.Reflection;

namespace Common;

public static class ModInformation
{
    public static bool IsServer { get; set; } = false;
    public static bool IsClient => !IsServer;

    /// <summary>
    /// True when this process runs the game with no renderer, whether it is a dedicated server
    /// (/coopheadless) or a driven client (/coopheadlessclient). Anything that reaches for a window, a
    /// loading screen or a rendered map scene has to take a different path here, because the engine has none
    /// of them to give.
    /// </summary>
    public static bool IsHeadless { get; set; } = false;

    /// <summary>
    /// True when this process is a headless CLIENT - render-free, but joining a server rather than being one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IsHeadless"/> because that flag answers "is there a renderer", and the
    /// render-free paths - the map scene, the view guards, party visuals - are wanted by both roles. What a
    /// client must NOT do is the server half: opening a server console, polling the command file, booting a
    /// Steam GAME SERVER, or signalling that a campaign is up and ready for peers to join.
    ///
    /// It is also not expressible as <see cref="IsServer"/>. That flag is set when a session starts - when
    /// this process hosts or joins - whereas the headless flags are set during boot, from the launch
    /// arguments, long before either happens. At the moment the boot-time gates run, IsServer is still its
    /// default on a dedicated server too, so gating them on it would break the server rather than exclude
    /// the client.
    /// </remarks>
    public static bool IsHeadlessClient { get; set; } = false;

    /// <summary>A render-free process that is NOT a driven client - the dedicated server.</summary>
    public static bool IsHeadlessServer => IsHeadless && !IsHeadlessClient;

    /// <summary>
    /// The mod build stamped on this assembly. Its semantic version comes from the same build
    /// property as the deployed module manifest.
    /// </summary>
    public static string BuildVersion { get; } = typeof(ModInformation).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "0.0.0";

    /// <summary>The semantic portion of <see cref="BuildVersion"/>, used by Steam server metadata.</summary>
    public static Version Version { get; } = ParseVersion(BuildVersion);

    /// <summary>Whether an advertised lobby was built with this exact mod build.</summary>
    public static bool MatchesBuildVersion(string version)
    {
        return string.Equals(BuildVersion, version, StringComparison.Ordinal);
    }

    private static Version ParseVersion(string buildVersion)
    {
        var semanticVersion = buildVersion.Split('-', '+')[0];
        return System.Version.TryParse(semanticVersion, out var version) ? version : new Version(0, 0, 0);
    }
}
