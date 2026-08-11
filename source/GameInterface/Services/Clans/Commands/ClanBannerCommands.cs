using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Clans.Commands;

/// <summary>
/// Read and write the local player clan's banner as a banner code.
/// </summary>
/// <remarks>
/// A banner is a code, not an asset: <see cref="Banner.Serialize"/> produces a dot-separated list of layers
/// (mesh id, two colour ids, size, position, mirror, rotation) and <c>new Banner(code)</c> reads it back. Every
/// icon it can name already ships with the game, so a banner built this way needs no new texture, no atlas and
/// no module - which also means it costs nothing for the other players in a session, who would otherwise need
/// the same custom icon installed to see anything but a hole.
///
/// <c>show</c> exists so the current code can be written down before <c>set</c> replaces it. That printed string
/// IS the undo.
///
/// Named under <c>coop.debug.</c> because the live-test harness refuses to dispatch anything outside that
/// prefix, and these are worth driving from a script rather than by hand.
/// </remarks>
public static class ClanBannerCommands
{
    [CommandLineArgumentFunction("show", "coop.debug.banner")]
    public static string Show(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.banner.show";

        var clan = Hero.MainHero?.Clan;
        if (clan == null) return "No local player clan.";
        if (clan.Banner == null) return "The clan has no banner.";

        return clan.Banner.Serialize();
    }

    [CommandLineArgumentFunction("set", "coop.debug.banner")]
    public static string Set(List<string> args)
    {
        if (args.Count != 1) return "Usage: coop.debug.banner.set <bannerCode>";

        var code = args[0];
        if (!Banner.IsValidBannerCode(code)) return "Not a valid banner code.";

        var clan = Hero.MainHero?.Clan;
        if (clan == null) return "No local player clan.";

        var previous = clan.Banner?.Serialize() ?? "<none>";
        clan.Banner = new Banner(code);

        return $"Banner set. Previous code (keep this to revert): {previous}";
    }
}
