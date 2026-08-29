using GameInterface.Services.ObjectManager;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.GameDebug.Commands;

/// <summary>
/// A comparable digest of world state, so that client and server can be diffed against each other.
///
/// WHY THIS EXISTS
/// Every other check in this codebase is an assertion somebody wrote by hand: it finds the one bug it was
/// written to find and is silent about everything else. Finding bugs at any scale needs an oracle that does
/// not know in advance what is wrong - and for a synchronised game the general oracle is disagreement. If the
/// server says a party holds 41 troops and the client says 38, that is a bug without anyone having predicted
/// it. One digest over 1500 parties therefore covers ground that 1500 hand-written assertions would.
///
/// THE FALSE-POSITIVE PROBLEM, AND THE HEADER LINE
/// A live campaign changes while it is being read, so two snapshots taken seconds apart disagree about things
/// that were never broken. That would drown a real finding in noise. Hence the campaign day in the header:
/// the comparator refuses to compare snapshots taken at different campaign times and reports INCONCLUSIVE
/// rather than a divergence it cannot attribute. Stop the clock first (coop.debug.request_time_mode Stop) and
/// the two sides can be read at the same instant. A divergence that survives that is worth looking at.
///
/// Entities are keyed by the coop object id rather than StringId, because that is the id both processes agree
/// on, and emitted sorted so the diff does not depend on enumeration order.
/// </summary>
internal class WorldDigestCommands
{
    /// <summary>Positions are quantised: floats that differ in the last bits are not a synchronisation bug.</summary>
    private const int PositionDecimals = 2;

    // coop.debug.world.digest <parties|heroes|settlements|clans|kingdoms> [max]
    [CommandLineArgumentFunction("digest", "coop.debug.world")]
    public static string Digest(List<string> args)
    {
        if (args.Count < 1)
            return "Usage: coop.debug.world.digest <parties|heroes|settlements|clans|kingdoms> [max]";

        if (Campaign.Current == null) return "WORLD_DIGEST error=no campaign is loaded";

        if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) == false)
            return "WORLD_DIGEST error=unable to resolve the object manager";

        string scope = args[0].ToLowerInvariant();
        int max = int.MaxValue;
        if (args.Count > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            max = parsed;

        List<string> lines;
        try
        {
            switch (scope)
            {
                case "parties": lines = Collect(Campaign.Current.MobileParties, objectManager, DescribeParty); break;
                case "heroes": lines = Collect(Campaign.Current.AliveHeroes, objectManager, DescribeHero); break;
                case "settlements": lines = Collect(Campaign.Current.Settlements, objectManager, DescribeSettlement); break;
                case "clans": lines = Collect(Campaign.Current.Clans, objectManager, DescribeClan); break;
                case "kingdoms": lines = Collect(Campaign.Current.Kingdoms, objectManager, DescribeKingdom); break;
                default: return $"WORLD_DIGEST error=unknown scope '{scope}'; use parties|heroes|settlements|clans|kingdoms";
            }
        }
        catch (Exception ex)
        {
            return $"WORLD_DIGEST error={ex.GetType().Name}: {ex.Message}";
        }

        var builder = new StringBuilder();
        builder.AppendLine(string.Format(CultureInfo.InvariantCulture,
            "WORLD_DIGEST scope={0} day={1:F5} count={2} emitted={3}",
            scope, CampaignTime.Now.ToDays, lines.Count, Math.Min(lines.Count, max)));

        foreach (string line in lines.Take(max)) builder.AppendLine(line);
        return builder.ToString();
    }

    /// <summary>
    /// One entity that cannot be described must not cost the other 1500. A thrown entity is emitted as an
    /// error line of its own - which is itself a finding worth having, and on exactly the same footing as a
    /// divergence because the two sides will disagree about it.
    /// </summary>
    private static List<string> Collect<T>(IEnumerable<T> source, IObjectManager objectManager, Func<T, string> describe)
        where T : class
    {
        var lines = new List<string>();
        if (source == null) return lines;

        foreach (T item in source.ToList())
        {
            if (item == null) continue;
            if (objectManager.TryGetId(item, out string id) == false || string.IsNullOrEmpty(id)) continue;

            string fields;
            try { fields = describe(item); }
            catch (Exception ex) { fields = $"ERROR={ex.GetType().Name}"; }

            lines.Add($"{id}|{fields}");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static string DescribeParty(MobileParty party)
    {
        PartyBase partyBase = party.Party;
        return string.Join(",", new[]
        {
            Num("x", party.Position.X, PositionDecimals),
            Num("y", party.Position.Y, PositionDecimals),
            $"men={partyBase?.MemberRoster?.TotalManCount ?? -1}",
            $"wounded={partyBase?.MemberRoster?.TotalWounded ?? -1}",
            $"prisoners={partyBase?.PrisonRoster?.TotalManCount ?? -1}",
            Num("morale", party.Morale, 1),
            Num("food", party.Food, 1),
            $"active={party.IsActive}",
            $"disorganized={party.IsDisorganized}",
            $"mapEvent={party.MapEvent?.EventType.ToString() ?? "none"}",
            $"army={Id(party.Army?.LeaderParty)}",
            $"leader={Id(party.LeaderHero)}",
            $"settlement={Id(party.CurrentSettlement)}",

            // These are the fields PartyBehaviorUpdateData replicates - the mod's own sync contract for party
            // movement. A disagreement here is unambiguously a synchronisation bug rather than a difference of
            // opinion, and it is the shape a party that stops moving on one side takes.
            $"defaultBehavior={party.DefaultBehavior}",
            $"shortTermBehavior={party.ShortTermBehavior}",
            $"targetParty={Id(party.TargetParty)}",
            $"targetSettlement={Id(party.TargetSettlement)}",
            Num("moveX", party.MoveTargetPoint.X, PositionDecimals),
            Num("moveY", party.MoveTargetPoint.Y, PositionDecimals),
            $"aiDisabled={party.Ai?.IsDisabled}",
        });
    }

    private static string DescribeHero(Hero hero)
    {
        return string.Join(",", new[]
        {
            $"alive={hero.IsAlive}",
            $"prisoner={hero.IsPrisoner}",
            $"gold={hero.Gold}",
            $"clan={Id(hero.Clan)}",
            $"party={Id(hero.PartyBelongedTo)}",

            // Who HOLDS a prisoner, which PartyBelongedTo does not say - it is null for anyone captive, so
            // "prisoner=True,party=none" is ambiguous between "held by someone" and "held by nobody at all"
            // after their captor was destroyed. Those are different bugs and the digest has to separate them.
            $"captor={Id(hero.PartyBelongedToAsPrisoner)}",

            // The hero's CharacterObject id, which is what roster rows and party-screen commands are keyed by.
            // It cannot be derived from the hero id: Hero_CharacterObject_1610 carries it, Hero_Created_3966
            // does not, and guessing produced "Character with id CharacterObject_Created_3966 not found" -
            // a release that never staged, reported as a prisoner who would not release.
            $"character={Id(hero.CharacterObject)}",
            $"settlement={Id(hero.CurrentSettlement)}",
        });
    }

    private static string DescribeSettlement(Settlement settlement)
    {
        Town town = settlement.Town;
        return string.Join(",", new[]
        {
            $"owner={Id(settlement.OwnerClan)}",
            $"siege={settlement.IsUnderSiege}",
            $"garrison={settlement.Party?.MemberRoster?.TotalManCount ?? -1}",
            town == null ? "prosperity=n/a" : Num("prosperity", town.Prosperity, 1),
            town == null ? "militia=n/a" : Num("militia", settlement.Militia, 1),
        });
    }

    private static string DescribeClan(Clan clan)
    {
        return string.Join(",", new[]
        {
            $"leader={Id(clan.Leader)}",
            $"kingdom={Id(clan.Kingdom)}",
            $"tier={clan.Tier}",
            Num("influence", clan.Influence, 1),
            $"settlements={clan.Settlements?.Count ?? -1}",
            $"parties={clan.WarPartyComponents?.Count ?? -1}",
            $"eliminated={clan.IsEliminated}",
        });
    }

    private static string DescribeKingdom(Kingdom kingdom)
    {
        return string.Join(",", new[]
        {
            $"leader={Id(kingdom.Leader)}",
            $"clans={kingdom.Clans?.Count ?? -1}",
            $"settlements={kingdom.Settlements?.Count ?? -1}",
            $"eliminated={kingdom.IsEliminated}",
        });
    }

    /// <summary>Invariant formatting throughout: a machine reads this, and a comma decimal separator would
    /// collide with the field separator on a differently-configured machine.</summary>
    private static string Num(string name, float value, int decimals) =>
        name + "=" + Math.Round(value, decimals).ToString("F" + decimals, CultureInfo.InvariantCulture);

    private static string Id(object obj)
    {
        if (obj == null) return "none";
        if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) &&
            objectManager.TryGetId(obj, out string id) && string.IsNullOrEmpty(id) == false)
        {
            return id;
        }
        return "unresolved";
    }
}
