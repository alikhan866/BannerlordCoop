using Common;
using GameInterface.Services.ObjectManager;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Headless.Commands;

/// <summary>
/// Whether a party can be sent into a fight, and if not, which fight it can still be sent into.
/// </summary>
/// <remarks>
/// WHY THIS IS A COMMAND AND NOT A CHECKLIST
/// A driven client kept being told to start a battle that the campaign then refused, and the refusal surfaces
/// as nothing at all - the encounter opens, the attack option is not clickable, and the rig sits in a menu
/// wondering which of its steps failed. Every one of those refusals came from party state that was knowable
/// BEFORE the attempt: a leader on 1% health, a party that starved down to no troops, a party already inside
/// another map event. So this reports the decision rather than leaving each scenario to re-derive it, and it
/// reports it in the same shape for both roles - the server can ask about any party, a client about its own.
///
/// THE 20% RULE IS THE CALLER'S RULE, AND IT IS NAMED
/// Below a fifth of the leader's health the only thing that still works is committing troops to a simulated
/// resolution; above it the party can be walked into the fight itself. That threshold is a judgement, not a
/// campaign constant, so it lives here as one named number instead of being spread across scenarios as a
/// literal - and the health figures it was derived from are reported alongside the verdict, so a caller that
/// disagrees can apply its own cut without re-reading the party.
///
/// WARNINGS ARE SEPARATE FROM THE VERDICT
/// Starvation, low morale and disorganisation change the OUTCOME of a battle without preventing it. Folding
/// them into the verdict would refuse fights the campaign allows, which manufactures a finding; leaving them
/// out entirely would hide the reason a battle that started went badly. They are therefore listed, and the
/// verdict ignores them.
/// </remarks>
public static class PartyBattleReadinessCommand
{
    /// <summary>Below this share of the leader's health, only a simulated commitment is offered.</summary>
    private const int LeaderFightHitPointsPercent = 20;

    /// <summary>Morale below this deserts troops mid-battle; it does not stop the battle starting.</summary>
    private const float LowMoraleWarning = 25f;

    [CommandLineArgumentFunction("battle_readiness", "coop.debug.party")]
    public static string BattleReadiness(List<string> args)
    {
        bool asJsonOnly = args.Count > 0 &&
            string.Equals(args[args.Count - 1], "json", StringComparison.OrdinalIgnoreCase);
        var positional = asJsonOnly ? args.Take(args.Count - 1).ToList() : args;
        if (positional.Count > 1)
            return "Usage: coop.debug.party.battle_readiness [partyId] [json]";

        if (Campaign.Current == null) return "BATTLE_READINESS campaign=false";

        if (!TryResolveParty(positional.Count == 1 ? positional[0] : null, out var party, out var error))
            return error;

        var view = Describe(party);
        string json = "LIVE_TEST_JSON=" + JsonConvert.SerializeObject(view);
        if (asJsonOnly) return json;

        var report = new StringBuilder();
        report.AppendLine(
            $"BATTLE_READINESS party={view.partyId} name=\"{view.name}\" verdict={view.verdict} " +
            $"allows={view.allows}");
        report.AppendLine(
            $"  leader={view.leader} hp={view.leaderHitPoints}/{view.leaderMaxHitPoints} " +
            $"({view.leaderHitPointsPercent}%) wounded={Lower(view.leaderWounded)} " +
            $"threshold={LeaderFightHitPointsPercent}%");
        report.AppendLine(
            $"  troops healthy={view.healthyTroops} wounded={view.woundedTroops} total={view.totalTroops} " +
            $"strength={view.strength:F1}");
        report.AppendLine(
            $"  supply morale={view.morale:F1} food={view.food:F1} foodPercent={view.foodPercent} " +
            $"foodChange={view.foodChange:F2} starving={Lower(view.starving)} daysStarving={view.daysStarving:F2}");
        report.AppendLine(
            $"  state active={Lower(view.active)} disorganized={Lower(view.disorganized)} " +
            $"mapEvent={view.mapEvent} settlement={view.settlement}");
        if (view.warnings.Length > 0)
            foreach (string warning in view.warnings) report.AppendLine("  WARN " + warning);
        if (view.blockers.Length > 0)
            foreach (string blocker in view.blockers) report.AppendLine("  BLOCKS " + blocker);
        report.Append(json);
        return report.ToString();
    }

    /// <summary>
    /// One token of readiness for the local party, for reports whose subject is something else.
    /// </summary>
    /// <remarks>
    /// Carried on the blocker report because the two questions are always asked together: a caller that has
    /// just been told what it is blocked on immediately needs to know whether clearing it would even lead to a
    /// fight. Two round trips to learn "you are at an encounter menu, and your leader is on 1% health" is two
    /// chances to act on half the picture.
    /// </remarks>
    internal static string LocalVerdict()
    {
        var party = MobileParty.MainParty;
        if (party == null) return "n/a";

        try
        {
            var view = Describe(party);
            // No spaces: this is appended to a key=value report line, and a space would make every reader
            // that splits on whitespace silently truncate the verdict to its first half.
            return view.verdict == "READY"
                ? "READY"
                : $"{view.verdict}(hp={view.leaderHitPointsPercent}%,healthy={view.healthyTroops})";
        }
        catch
        {
            return "unreadable";
        }
    }

    private static ReadinessView Describe(MobileParty party)
    {
        var leader = party.LeaderHero;
        int maxHitPoints = leader == null ? 0 : Math.Max(1, leader.MaxHitPoints);
        int hitPoints = leader?.HitPoints ?? 0;
        int hitPointsPercent = leader == null ? 0 : (int)Math.Round(100.0 * hitPoints / maxHitPoints);

        var partyBase = party.Party;
        int healthy = partyBase?.NumberOfHealthyMembers ?? 0;

        // Blockers stop a fight from being possible at all; warnings only make it go worse. Keeping them in
        // two lists is what lets the verdict stay honest about which one it is reacting to.
        var blockers = new List<string>();
        var warnings = new List<string>();

        if (!party.IsActive) blockers.Add("party is not active");
        if (healthy == 0) blockers.Add("no healthy troops to field");

        // Already fighting is reported as its own verdict rather than as a blocker. A party inside its map
        // event cannot START a fight, but calling that NOT_READY reads as a fault on every sample taken during
        // the battle the caller just started - which is most of them.
        bool inBattle = party.MapEvent != null;
        if (party.CurrentSettlement != null)
            warnings.Add($"inside settlement {party.CurrentSettlement.StringId}; leave before engaging");
        if (party.IsDisorganized) warnings.Add("disorganized after a recent battle; engaging will be refused until it clears");
        if (partyBase?.IsStarving == true && party.Food > 0f)
            warnings.Add(
                $"starving flag is stale: {party.Food:F1} food is carried but the campaign has not " +
                $"re-evaluated it yet (needs an hourly tick with time running)");
        else if (partyBase?.IsStarving == true)
            warnings.Add($"starving for {partyBase.DaysStarving:F2} days; troops will desert");
        if (party.Food <= 0f) warnings.Add("no food carried");
        if (party.Morale < LowMoraleWarning) warnings.Add($"morale {party.Morale:F1} is below {LowMoraleWarning:F0}");
        if (leader == null) warnings.Add("party has no leader hero, so leader health cannot gate the decision");

        // Wounded is checked as well as the percentage. A hero can be flagged wounded above the threshold - the
        // campaign uses its own WoundedHealthLimit - and a wounded leader cannot lead a fight either way.
        bool leaderCanFight = leader == null ||
            (!leader.IsWounded && hitPointsPercent >= LeaderFightHitPointsPercent);

        string verdict = blockers.Count > 0
            ? "NOT_READY"
            : inBattle ? "IN_BATTLE"
            : leaderCanFight ? "READY" : "SEND_TROOPS_ONLY";

        string allows = blockers.Count > 0
            ? "nothing"
            : inBattle
                ? $"already in {party.MapEvent.EventType}; coop.debug.battle.snapshot | spawn_progress"
                : leaderCanFight
                    ? "coop.debug.encounter.attack | coop.debug.encounter.send_troops | mission entry"
                    : "coop.debug.encounter.send_troops";

        return new ReadinessView
        {
            partyId = Identify(party),
            name = party.Name?.ToString() ?? "<unnamed>",
            verdict = verdict,
            allows = allows,
            leader = leader?.StringId ?? "none",
            leaderHitPoints = hitPoints,
            leaderMaxHitPoints = leader == null ? 0 : maxHitPoints,
            leaderHitPointsPercent = hitPointsPercent,
            leaderWounded = leader?.IsWounded ?? false,
            healthyTroops = healthy,
            woundedTroops = partyBase?.NumberOfWoundedTotalMembers ?? 0,
            totalTroops = party.MemberRoster?.TotalManCount ?? 0,
            strength = partyBase?.EstimatedStrength ?? 0f,
            morale = party.Morale,
            food = party.Food,
            foodPercent = partyBase?.RemainingFoodPercentage ?? 0,
            foodChange = party.FoodChange,
            starving = partyBase?.IsStarving ?? false,
            daysStarving = partyBase?.DaysStarving ?? 0f,
            active = party.IsActive,
            disorganized = party.IsDisorganized,
            mapEvent = party.MapEvent == null ? "none" : party.MapEvent.EventType.ToString(),
            settlement = party.CurrentSettlement?.StringId ?? "none",
            warnings = warnings.ToArray(),
            blockers = blockers.ToArray(),
        };
    }

    private static bool TryResolveParty(string partyId, out MobileParty party, out string error)
    {
        error = null;

        if (partyId == null)
        {
            // No id means "the party this process plays", which only a client has. Saying so beats returning
            // the server's absent main party as if it were an answer.
            party = MobileParty.MainParty;
            if (party == null)
                error = "BATTLE_READINESS party=none reason=no-main-party " +
                        "hint=pass a party id; a dedicated server has no party of its own";
            return party != null;
        }

        party = null;
        if (!ContainerProvider.TryResolve<IObjectManager>(out var objectManager))
        {
            error = $"Unable to get {nameof(IObjectManager)}";
            return false;
        }

        if (objectManager.TryGetObject(partyId, out party) && party != null) return true;

        // Party ids come in two flavours in this codebase - the MobileParty and its PartyBase - and a caller
        // holding the wrong one would otherwise be told the party does not exist.
        if (objectManager.TryGetObject(partyId, out PartyBase partyBase) && partyBase?.MobileParty != null)
        {
            party = partyBase.MobileParty;
            return true;
        }

        error = $"BATTLE_READINESS party={partyId} reason=not-found";
        return false;
    }

    private static string Identify(MobileParty party)
    {
        if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) &&
            objectManager.TryGetId(party, out string id))
            return id;
        return party.StringId ?? "unregistered";
    }

    private static string Lower(bool value) => value.ToString().ToLowerInvariant();

    private sealed class ReadinessView
    {
        public string partyId { get; set; }
        public string name { get; set; }
        public string verdict { get; set; }
        public string allows { get; set; }
        public string leader { get; set; }
        public int leaderHitPoints { get; set; }
        public int leaderMaxHitPoints { get; set; }
        public int leaderHitPointsPercent { get; set; }
        public bool leaderWounded { get; set; }
        public int healthyTroops { get; set; }
        public int woundedTroops { get; set; }
        public int totalTroops { get; set; }
        public float strength { get; set; }
        public float morale { get; set; }
        public float food { get; set; }
        public int foodPercent { get; set; }
        public float foodChange { get; set; }
        public bool starving { get; set; }
        public float daysStarving { get; set; }
        public bool active { get; set; }
        public bool disorganized { get; set; }
        public string mapEvent { get; set; }
        public string settlement { get; set; }
        public string[] warnings { get; set; }
        public string[] blockers { get; set; }
    }
}
