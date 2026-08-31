using Autofac;
using Common;
using GameInterface.Services.ObjectManager;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Clans.Commands;

/// <summary>
/// Reads and rewrites the colours a clan's troops and banner are drawn in.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS IS NOT JUST TWO ASSIGNMENTS. A clan's visible colours come from four places that override each
/// other, and setting the wrong one silently does nothing:
/// </para>
/// <list type="number">
/// <item><c>Clan.Color</c> / <c>Color2</c> - the party marker on the campaign map.</item>
/// <item><c>Clan.BannerBackgroundColorPrimary</c> / <c>Secondary</c> - private, and what
/// <c>Clan.UpdateBannerColorsAccordingToKingdom</c> applies to the banner when the clan has no kingdom.</item>
/// <item>The banner's own layer 0, which is what actually gets drawn.</item>
/// <item><c>Kingdom.PrimaryBannerColor</c> / <c>SecondaryBannerColor</c> - which OVERRIDE the clan's whenever
/// the clan belongs to a kingdom. A clan in a kingdom that only sets its own colours keeps rendering in the
/// kingdom's, which is the trap this command exists to avoid.</item>
/// </list>
/// <para>
/// COLOURS ARE PALETTE IDS, NOT ARBITRARY RGB. <c>Banner.ChangeBackgroundColor</c> maps the uint back through
/// <c>BannerManager.GetColorId</c> and silently does nothing when it is not an exact palette entry, so a
/// hand-mixed colour would look like the command had failed. Ids are taken instead, and turned into uints
/// through the same palette.
/// </para>
/// <para>
/// RUN THIS ON THE SERVER. Every field it writes is autosynced (<c>Clan_Color</c>, <c>Clan_Color2</c>,
/// <c>Clan_BannerBackgroundColorPrimary</c>/<c>Secondary</c>, <c>Clan__banner</c>,
/// <c>Kingdom_PrimaryBannerColor</c>/<c>Secondary</c>), so the server is the one place a change reaches both
/// players; a client-side change would be local and would be overwritten.
/// </para>
/// </remarks>
public static class ClanBannerColorCommands
{
    private static readonly PropertyInfo BannerBackgroundPrimaryProperty =
        typeof(Clan).GetProperty(
            "BannerBackgroundColorPrimary", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly PropertyInfo BannerBackgroundSecondaryProperty =
        typeof(Clan).GetProperty(
            "BannerBackgroundColorSecondary", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly PropertyInfo KingdomPrimaryBannerColorProperty =
        typeof(Kingdom).GetProperty("PrimaryBannerColor");

    private static readonly PropertyInfo KingdomSecondaryBannerColorProperty =
        typeof(Kingdom).GetProperty("SecondaryBannerColor");

    private static readonly PropertyInfo KingdomColorProperty = typeof(Kingdom).GetProperty("Color");
    private static readonly PropertyInfo KingdomColor2Property = typeof(Kingdom).GetProperty("Color2");

    [CommandLineArgumentFunction("colors", "coop.debug.banner")]
    public static string Colors(List<string> args)
    {
        if (args.Count != 1) return "Usage: coop.debug.banner.colors <clanId|heroId>";
        if (!TryResolveClan(args[0], out var clan, out var error)) return error;

        var report = new StringBuilder();
        report.AppendLine($"Clan {clan.Name} ({clan.StringId})");
        report.AppendLine($"  Color            {Describe(clan.Color)}");
        report.AppendLine($"  Color2           {Describe(clan.Color2)}");
        report.AppendLine($"  BannerBackground {Describe(ReadUInt(BannerBackgroundPrimaryProperty, clan))} / " +
                          $"{Describe(ReadUInt(BannerBackgroundSecondaryProperty, clan))}");

        if (clan.Banner != null)
        {
            report.AppendLine($"  Banner primary   {Describe(clan.Banner.GetPrimaryColor())}");
            report.AppendLine($"  Banner secondary {Describe(clan.Banner.GetSecondaryColor())}");
            report.AppendLine($"  Banner code      {clan.Banner.Serialize()}");
        }

        var kingdom = clan.Kingdom;
        if (kingdom == null)
        {
            report.Append("  Kingdom          none (clan colours are what render)");
            return report.ToString();
        }

        report.AppendLine($"  Kingdom          {kingdom.Name} ({kingdom.StringId}) - OVERRIDES the clan banner");
        report.AppendLine($"    Color          {Describe(kingdom.Color)} / {Describe(kingdom.Color2)}");
        report.Append($"    BannerColor    {Describe(kingdom.PrimaryBannerColor)} / " +
                      $"{Describe(kingdom.SecondaryBannerColor)}");

        return report.ToString();
    }

    /// <summary>
    /// Sets a clan's primary and secondary colours everywhere they are read from.
    /// </summary>
    /// <remarks>
    /// The kingdom is only touched when this clan RULES it. Rewriting the colours of a kingdom the clan
    /// merely belongs to would repaint every other clan in it, including AI lords who never asked - so in
    /// that case the command reports the override instead of quietly working around it.
    /// </remarks>
    [CommandLineArgumentFunction("set_colors", "coop.debug.banner")]
    public static string SetColors(List<string> args)
    {
        if (args.Count != 3)
            return "Usage: coop.debug.banner.set_colors <clanId|heroId> <primaryColorId> <secondaryColorId>";

        if (!TryResolveClan(args[0], out var clan, out var error)) return error;
        if (!int.TryParse(args[1], out int primaryId) || !int.TryParse(args[2], out int secondaryId))
            return "Colour ids must be integers. Use coop.debug.banner.palette to list them.";

        if (!TryGetPaletteColor(primaryId, out uint primary))
            return $"Colour id {primaryId} is not in the banner palette.";
        if (!TryGetPaletteColor(secondaryId, out uint secondary))
            return $"Colour id {secondaryId} is not in the banner palette.";

        // Printed before anything changes, so this line is the undo.
        var previous = new StringBuilder();
        previous.Append($"previous: clan {Describe(clan.Color)}/{Describe(clan.Color2)}");
        if (clan.Banner != null) previous.Append($" banner \"{clan.Banner.Serialize()}\"");

        var applied = new StringBuilder();

        clan.Color = primary;
        clan.Color2 = secondary;
        applied.Append("clan Color/Color2");

        if (BannerBackgroundPrimaryProperty != null && BannerBackgroundSecondaryProperty != null)
        {
            BannerBackgroundPrimaryProperty.SetValue(clan, primary);
            BannerBackgroundSecondaryProperty.SetValue(clan, secondary);
            applied.Append(", clan BannerBackground");
        }

        if (clan.Banner != null)
        {
            clan.Banner.ChangeBackgroundColor(primary, secondary);
            applied.Append(", banner layer 0");
        }

        var kingdom = clan.Kingdom;
        if (kingdom != null)
        {
            if (kingdom.RulingClan == clan)
            {
                SetIfPresent(KingdomColorProperty, kingdom, primary);
                SetIfPresent(KingdomColor2Property, kingdom, secondary);
                SetIfPresent(KingdomPrimaryBannerColorProperty, kingdom, primary);
                SetIfPresent(KingdomSecondaryBannerColorProperty, kingdom, secondary);
                applied.Append($", kingdom {kingdom.Name} (this clan rules it)");
            }
            else
            {
                applied.Append(
                    $" -- WARNING: {kingdom.Name} is not ruled by this clan and its banner colours " +
                    "override the clan's, so troops may still render in kingdom colours");
            }
        }

        RefreshPartyVisuals(clan);

        return $"Set {clan.Name} to {Describe(primary)} / {Describe(secondary)}. Applied to: {applied}. " +
               previous;
    }

    /// <summary>Lists palette ids so a colour can be named rather than guessed.</summary>
    [CommandLineArgumentFunction("palette", "coop.debug.banner")]
    public static string Palette(List<string> args)
    {
        if (args.Count > 1) return "Usage: coop.debug.banner.palette [hexFilter]";

        var palette = BannerManager.Instance?.ReadOnlyColorPalette;
        if (palette == null) return "The banner palette is unavailable.";

        string filter = args.Count == 1 ? args[0].ToUpperInvariant() : null;
        var report = new StringBuilder();
        int shown = 0;

        foreach (var entry in palette)
        {
            string hex = entry.Value.Color.ToString("X8");
            if (filter != null && !hex.Contains(filter)) continue;

            report.AppendLine($"  {entry.Key,4} = #{hex}");
            if (++shown >= 260) break;
        }

        return shown == 0 ? "No palette entries matched." : report.ToString().TrimEnd();
    }

    /// <remarks>
    /// A party caches its visual, so without this the change only appears after something else happens to
    /// dirty it - which reads exactly like the command not having worked.
    /// </remarks>
    private static void RefreshPartyVisuals(Clan clan)
    {
        foreach (var warPartyComponent in clan.WarPartyComponents)
        {
            try
            {
                warPartyComponent?.Party?.SetVisualAsDirty();
            }
            catch (NullReferenceException)
            {
            }
        }
    }

    private static void SetIfPresent(PropertyInfo property, object target, uint value)
        => property?.SetValue(target, value);

    private static uint ReadUInt(PropertyInfo property, object target)
        => property?.GetValue(target) is uint value ? value : 0u;

    private static bool TryGetPaletteColor(int id, out uint color)
    {
        color = BannerManager.GetColor(id);

        // GetColor hands back opaque white for an id it does not know, so the lookup is confirmed against
        // the palette itself rather than trusted - otherwise a typo silently paints the clan white.
        var palette = BannerManager.Instance?.ReadOnlyColorPalette;
        return palette != null && palette.ContainsKey(id);
    }

    private static string Describe(uint color)
    {
        int id = BannerManager.GetColorId(color);
        return id >= 0 ? $"#{color:X8} (id {id})" : $"#{color:X8}";
    }

    private static bool TryResolveClan(string id, out Clan clan, out string error)
    {
        clan = null;
        error = null;

        if (!ContainerProvider.TryGetContainer(out var container) ||
            !container.TryResolve(out IObjectManager objectManager))
        {
            error = "Unable to resolve ObjectManager.";
            return false;
        }

        if (objectManager.TryGetObject(id, out clan) && clan != null) return true;

        if (objectManager.TryGetObject(id, out Hero hero) && hero?.Clan != null)
        {
            clan = hero.Clan;
            return true;
        }

        error = $"Unable to resolve {id} as a clan or a hero with a clan.";
        return false;
    }
}
