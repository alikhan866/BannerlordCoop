using System.Collections.Generic;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Heroes.Commands;

/// <summary>
/// Finds heroes that exist in more than one place at once, or more than once in the same place.
/// </summary>
/// <remarks>
/// A hero is unique, so any roster entry above one is corruption. Two ways it shows up:
/// the same hero in two different rosters, and the same hero listed twice in ONE roster - a dungeon
/// showing "Belithor  2" with a transfer slider that goes to 2.
///
/// The first version of this command could only see the first kind, and only in one place. It walked
/// <c>MobileParty.All</c> and read <c>MemberRoster</c>, which misses:
///   - PRISON rosters, where captured lords actually live;
///   - SETTLEMENT parties, which are not in <c>MobileParty.All</c> at all, so no town dungeon was ever read;
///   - a count above 1 on a single entry, because it only asked which rosters held the hero, never how many
///     of him each one held.
/// All three are covered now.
/// </remarks>
public static class HeroDuplicateDebugCommands
{
    private readonly struct Holding
    {
        public Holding(string partyId, string rosterName, int count, TroopRoster roster, int index)
        {
            PartyId = partyId;
            RosterName = rosterName;
            Count = count;
            Roster = roster;
            Index = index;
        }

        public string PartyId { get; }
        public string RosterName { get; }
        public int Count { get; }
        public TroopRoster Roster { get; }
        public int Index { get; }

        public override string ToString() => $"{PartyId}.{RosterName}x{Count}";
    }

    /// <summary>A hero entry is corrupt whenever a roster holds more than one of them.</summary>
    internal static bool IsDoubledEntry(int countInOneRoster) => countInOneRoster > 1;

    /// <summary>A hero held by more than one roster at once is in two places.</summary>
    internal static bool IsSplitAcrossRosters(int rosterCount) => rosterCount > 1;

    /// <summary>What a hero entry's count must be reduced to. A hero is one person.</summary>
    internal static int ClampedHeroCount(int count) => count > 1 ? 1 : count;

    /// <summary>Wounded can never exceed the number present, or the roster reports negative healthy troops.</summary>
    internal static int ClampedWoundedCount(int wounded, int number) => wounded > number ? number : wounded;

    [CommandLineArgumentFunction("duplicates", "coop.debug.hero")]
    public static string Duplicates(List<string> arguments)
    {
        if (Campaign.Current == null) return "No campaign is loaded.";

        var repair = arguments != null && arguments.Count > 0 &&
                     arguments[0].Trim().Equals("repair", System.StringComparison.OrdinalIgnoreCase);
        var repaired = 0;

        var holdings = new Dictionary<Hero, List<Holding>>();

        foreach (var party in AllParties())
        {
            Collect(party, party?.MemberRoster, "members", holdings);
            Collect(party, party?.PrisonRoster, "prisoners", holdings);
        }

        var report = new StringBuilder();
        var problems = 0;

        foreach (var pair in holdings)
        {
            var hero = pair.Key;
            var held = pair.Value;
            var belongsTo = hero.PartyBelongedTo?.StringId;

            foreach (var holding in held)
            {
                if (!IsDoubledEntry(holding.Count)) continue;

                problems++;
                report.AppendLine(
                    $"DOUBLED    {hero.StringId} ({hero.Name}) appears {holding.Count}x in {holding.PartyId}.{holding.RosterName}");

                if (!repair) continue;

                // SetElementNumber, deliberately, NOT AddToCounts. AddToCountsAtIndex calls
                // PartyBase.OnHeroRemoved for ANY negative change regardless of the resulting count, so
                // trimming 2 -> 1 that way would release the prisoner rather than de-duplicate him - the
                // same side effect that once detached a companion from the party he was leading.
                // SetElementNumber writes the count and bumps the roster version, nothing else.
                var number = ClampedHeroCount(holding.Count);
                holding.Roster.SetElementNumber(holding.Index, number);
                holding.Roster.SetElementWoundedNumber(
                    holding.Index,
                    ClampedWoundedCount(holding.Roster.GetElementWoundedNumber(holding.Index), number));

                repaired++;
                report.AppendLine($"           -> set to {number}");
            }

            if (IsSplitAcrossRosters(held.Count))
            {
                problems++;
                report.AppendLine(
                    $"DUPLICATE  {hero.StringId} ({hero.Name}) is in {held.Count} rosters: {string.Join(", ", held)}; PartyBelongedTo={belongsTo ?? "<null>"}");
                continue;
            }

            // Held by a roster that is not the one it thinks it belongs to: the map shows it in one place
            // while the campaign believes it is in another. Only meaningful for members - a prisoner's
            // PartyBelongedTo is legitimately null.
            if (belongsTo != null && held[0].RosterName == "members" && held[0].PartyId != belongsTo)
            {
                problems++;
                report.AppendLine(
                    $"MISMATCH   {hero.StringId} ({hero.Name}) sits in roster {held[0].PartyId} but PartyBelongedTo={belongsTo}");
            }
        }

        // Informational, NOT a problem: a companion leading a clan party of their own is vanilla. Giving a
        // companion a party through Clan > Parties never changes their occupation or clan, so Wanderer +
        // CompanionOf + leading a LordPartyComponent is the normal, healthy shape. Listed only because it
        // looks alarming when hunting duplicates, and repairing it breaks a working party.
        var informational = new StringBuilder();
        foreach (var hero in Hero.AllAliveHeroes)
        {
            if (hero?.CompanionOf == null) continue;
            if (hero.PartyBelongedTo == null) continue;
            if (hero.PartyBelongedTo.LeaderHero != hero) continue;

            informational.AppendLine(
                $"INFO       companion {hero.StringId} ({hero.Name}) of {hero.CompanionOf.Name} leads its own party {hero.PartyBelongedTo.StringId} (normal)");
        }

        var suffix = informational.Length > 0 ? "\n" + informational : "";

        if (problems == 0)
            return $"No duplicated heroes found ({holdings.Count} heroes checked across members and prisoners).{suffix}";

        var header = repair
            ? $"{problems} problem(s) found; repaired {repaired} doubled entry(ies):"
            : $"{problems} problem(s) found (add 'repair' to fix doubled entries):";

        return $"{header}\n{report}{suffix}";
    }

    /// <summary>
    /// Every party that can hold a hero, including the settlement parties that own town and castle dungeons.
    /// </summary>
    /// <remarks>
    /// <c>MobileParty.All</c> does not contain settlement parties, so walking only that list never reads a
    /// single dungeon - which is exactly where captured lords sit.
    /// </remarks>
    private static IEnumerable<PartyBase> AllParties()
    {
        foreach (var party in MobileParty.All)
        {
            if (party?.Party != null) yield return party.Party;
        }

        foreach (var settlement in Settlement.All)
        {
            if (settlement?.Party != null) yield return settlement.Party;
        }
    }

    private static void Collect(PartyBase party, TroopRoster roster, string rosterName, Dictionary<Hero, List<Holding>> holdings)
    {
        if (party == null || roster == null) return;

        for (int i = 0; i < roster.Count; i++)
        {
            var hero = roster.GetCharacterAtIndex(i)?.HeroObject;
            if (hero == null) continue;

            if (!holdings.TryGetValue(hero, out var list))
            {
                list = new List<Holding>();
                holdings[hero] = list;
            }

            list.Add(new Holding(party.Id, rosterName, roster.GetElementNumber(i), roster, i));
        }
    }
}
