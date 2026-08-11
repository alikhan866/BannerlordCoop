using System.Collections.Generic;
using Common;
using Common.Logging;
using GameInterface.Services.ObjectManager;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.Core;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Heroes.Commands;

/// <summary>
/// Repairs a hero left half-way through the companion-to-lord conversion.
/// </summary>
/// <remarks>
/// Turning a companion into a lord is several campaign steps: the companion is released from the clan's
/// companion list, given clan membership, given the Lord occupation, and given a party. If that sequence
/// aborts partway - which it did, because a notification handler threw on a headless host - the hero is left
/// describing two mutually exclusive things at once.
///
/// Measured on a live save: "Oragur the Knowing" held <c>Occupation = Wanderer</c> and
/// <c>CompanionOf = Wang</c> with no clan of his own, while leading a party whose component was a
/// <c>LordPartyComponent</c> owned by that same clan. The party screen therefore listed him as a companion,
/// the map showed him fielding a party, and the army could not summon him at all - army membership resolves
/// through the clan, and his was null.
///
/// The abort itself is fixed (see <c>HeadlessNotificationGuardPatch</c>), so nothing should reach this state
/// again. This exists only to repair a save that already did, which is why it is a command and not automatic.
/// </remarks>
public static class HeroLordConversionRepairCommand
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(HeroLordConversionRepairCommand));

    /// <summary>What a half-converted hero still needs, decided over plain values so it can be asserted.</summary>
    internal readonly struct Repair
    {
        public readonly bool ReleaseFromCompanions;
        public readonly bool GrantClan;
        public readonly bool GrantLordOccupation;

        public Repair(bool releaseFromCompanions, bool grantClan, bool grantLordOccupation)
        {
            ReleaseFromCompanions = releaseFromCompanions;
            GrantClan = grantClan;
            GrantLordOccupation = grantLordOccupation;
        }

        public bool IsNeeded => ReleaseFromCompanions || GrantClan || GrantLordOccupation;
    }

    /// <summary>
    /// Decides what is missing. Repairs ONLY a hero who already leads a lord's party.
    /// </summary>
    /// <remarks>
    /// That guard is the whole safety of this command. A companion riding in someone's party is *supposed* to
    /// be a Wanderer with no clan - repairing one would promote an ordinary companion into a lord behind the
    /// player's back. It is the lord's PARTY that proves a conversion was started and never finished.
    /// </remarks>
    /// <summary>
    /// Whether a hero LEADS a lord's party, as opposed to merely riding in one.
    /// </summary>
    /// <remarks>
    /// The distinction is the entire safety of this command, and getting it wrong is not a small error.
    /// <c>Hero.PartyBelongedTo</c> is the party a hero BELONGS to, and a player's own party is itself a
    /// <c>LordPartyComponent</c> - so testing the component alone matches every companion riding in every
    /// lord's party in the game.
    ///
    /// The first dry run showed exactly that: seven "half-finished conversions", one of which was
    /// "Natu the Grey Falcon leading Jian's Party". He was not leading it, he was a passenger in it. Applying
    /// the repair on that finding would have promoted the player's entire retinue to lords.
    ///
    /// Leadership is the party's own answer to who commands it, so that is what is asked.
    /// </remarks>
    internal static bool LeadsOwnLordParty(bool partyIsALordParty, bool heroIsThePartyLeader)
        => partyIsALordParty && heroIsThePartyLeader;

    internal static Repair Diagnose(bool leadsLordParty, bool isCompanionOfAClan, bool hasOwnClan, bool isLord)
    {
        if (!leadsLordParty) return new Repair(false, false, false);

        return new Repair(
            releaseFromCompanions: isCompanionOfAClan,
            grantClan: !hasOwnClan,
            grantLordOccupation: !isLord);
    }

    [CommandLineArgumentFunction("repair_lord_conversion", "coop.debug.hero")]
    public static string RepairLordConversion(List<string> args)
    {
        if (ModInformation.IsClient) return "Command can only be run on the server.";
        if (args.Count != 1)
            return "Usage: coop.debug.hero.repair_lord_conversion <heroId> | scan | all";

        if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) == false)
            return "Unable to resolve the ObjectManager.";

        // "scan" reports without changing anything, so the damage can be reviewed before it is touched; "all"
        // then applies the same finding. One save can hold several of these, and finding them one at a time
        // means discovering the next only after the last has been repaired.
        var mode = args[0].Trim();
        if (mode.Equals("scan", System.StringComparison.OrdinalIgnoreCase)) return SweepAll(apply: false);
        if (mode.Equals("all", System.StringComparison.OrdinalIgnoreCase)) return SweepAll(apply: true);

        if (objectManager.TryGetObject<Hero>(args[0], out var hero) == false || hero == null)
            return $"No hero with id {args[0]}.";

        return RepairOne(hero);
    }

    /// <summary>Walks every living hero, reporting - and optionally repairing - each half-finished conversion.</summary>
    private static string SweepAll(bool apply)
    {
        var report = new System.Text.StringBuilder();
        int found = 0, repaired = 0, refused = 0;

        foreach (var candidate in Hero.AllAliveHeroes)
        {
            if (candidate == null) continue;

            var party = candidate.PartyBelongedTo;
            if (party == null) continue;
            if (!LeadsOwnLordParty(party.PartyComponent is LordPartyComponent, party.LeaderHero == candidate)) continue;

            var repair = Diagnose(
                leadsLordParty: true,
                isCompanionOfAClan: candidate.CompanionOf != null,
                hasOwnClan: candidate.Clan != null,
                isLord: candidate.Occupation == Occupation.Lord);

            if (!repair.IsNeeded) continue;

            found++;
            var needs = new List<string>();
            if (repair.ReleaseFromCompanions) needs.Add("still a companion");
            if (repair.GrantClan) needs.Add("no clan");
            if (repair.GrantLordOccupation) needs.Add($"occupation={candidate.Occupation}");

            report.AppendLine($"  {candidate.Name} ({candidate.StringId}) leading {party.Name}: {string.Join(", ", needs)}");

            if (!apply) continue;

            var result = RepairOne(candidate);
            if (result.StartsWith("Repaired")) repaired++; else { refused++; report.AppendLine($"     -> {result}"); }
        }

        if (found == 0) return "No half-finished companion-to-lord conversions found.";

        var header = apply
            ? $"Found {found} half-finished conversion(s); repaired {repaired}, refused {refused}:"
            : $"Found {found} half-finished conversion(s) (nothing changed - run with 'all' to repair):";
        return header + "\n" + report.ToString();
    }

    private static string RepairOne(Hero hero)
    {
        var party = hero.PartyBelongedTo;
        bool leadsLordParty = party != null
            && LeadsOwnLordParty(party.PartyComponent is LordPartyComponent, party.LeaderHero == hero);

        // The clan the party already belongs to is the answer to "which clan should he be in" - it is what the
        // half-finished conversion had already decided, so this restores that intent rather than inventing one.
        var targetClan = party?.ActualClan ?? hero.CompanionOf;

        var repair = Diagnose(
            leadsLordParty,
            isCompanionOfAClan: hero.CompanionOf != null,
            hasOwnClan: hero.Clan != null,
            isLord: hero.Occupation == Occupation.Lord);

        if (!repair.IsNeeded)
        {
            return leadsLordParty
                ? $"{hero.Name} is already a consistent lord (clan {hero.Clan?.Name}); nothing to repair."
                : $"{hero.Name} does not lead a lord's party, so there is no half-finished conversion to repair.";
        }

        if (targetClan == null)
            return $"{hero.Name} is half-converted but no clan can be resolved for him; refusing to guess.";

        var before = $"occupation={hero.Occupation}, clan={hero.Clan?.Name?.ToString() ?? "<none>"}, companionOf={hero.CompanionOf?.Name?.ToString() ?? "<none>"}";

        if (repair.ReleaseFromCompanions)
            RemoveCompanionAction.ApplyByByTurningToLord(hero.CompanionOf, hero);

        if (repair.GrantClan && hero.Clan == null)
            hero.Clan = targetClan;

        if (repair.GrantLordOccupation)
            hero.SetNewOccupation(Occupation.Lord);

        var after = $"occupation={hero.Occupation}, clan={hero.Clan?.Name?.ToString() ?? "<none>"}, companionOf={hero.CompanionOf?.Name?.ToString() ?? "<none>"}";

        Logger.Information("[Repair] Completed the lord conversion for {Hero}: {Before} -> {After}", hero.Name, before, after);

        return $"Repaired {hero.Name}.\n  before: {before}\n  after:  {after}";
    }
}
