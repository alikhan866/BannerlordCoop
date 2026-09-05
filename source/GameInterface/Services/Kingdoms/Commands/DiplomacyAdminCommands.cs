using System;
using System.Collections.Generic;
using System.Text;
using Common;
using Common.Logging;
using GameInterface.Services.ObjectManager;
using Serilog;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Kingdoms.Commands;

/// <summary>
/// Server-side diplomacy operations that the game only exposes through UI a coop client cannot drive.
/// </summary>
/// <remarks>
/// Alliances are NOT a stance. <c>StanceType</c> in this build is only <c>Neutral</c> and <c>War</c>; an
/// alliance lives in <c>AllianceCampaignBehavior</c>, with <c>Kingdom._alliedKingdoms</c> as a derived cache.
/// So there is no "set them to neutral" flag to write - the alliance has to be ended through the behaviour
/// that owns it.
///
/// The tempting shortcut is to declare war and immediately make peace, since war ends an alliance on the way
/// past. It is a trap: <c>AllianceCampaignBehavior.OnWarDeclared</c> routes through
/// <c>ApplyBrokenAlliancePenalty</c>, which applies <b>-100 relation</b> between the two kingdom leaders and a
/// dishonourable trait hit. Between two players who simply want to stop being allies that is far worse than
/// the alliance. <c>EndAlliance</c> is the clean call and costs nothing.
/// </remarks>
public static class DiplomacyAdminCommands
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(DiplomacyAdminCommands));

    /// <summary>A clan already in a kingdom is DEFECTING, which is a different vanilla call.</summary>
    /// <remarks>
    /// <c>ApplyByJoinToKingdom</c> assumes the clan is unaligned. Using it on a clan that already belongs
    /// somewhere leaves the old kingdom still listing it. <c>ApplyByJoinToKingdomByDefection</c> takes the old
    /// kingdom explicitly and unwinds that membership.
    /// </remarks>
    internal static bool JoinIsDefection(bool clanAlreadyInAKingdom) => clanAlreadyInAKingdom;

    /// <summary>Whether the payer can actually cover the fee.</summary>
    /// <remarks>
    /// <c>GiveGoldAction</c> does not stop a hero going into the red, and a kingdom ruler dragged to negative
    /// gold starts failing wage payments across every party they own. Checked before anything is applied.
    /// </remarks>
    internal static bool PaymentIsAffordable(int payerGold, int amount) => amount <= 0 || payerGold >= amount;

    [CommandLineArgumentFunction("end_alliance", "coop.debug.kingdom")]
    public static string EndAlliance(List<string> args)
    {
        if (ModInformation.IsClient) return "Command can only be run on the server.";
        if (args.Count < 2 || args.Count > 3)
            return "Usage: coop.debug.kingdom.end_alliance <kingdomId1> <kingdomId2> [scan]";

        if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) == false)
            return "Unable to resolve the ObjectManager.";
        if (!objectManager.TryGetObject<Kingdom>(args[0], out var first) || first == null)
            return $"No kingdom with id {args[0]}.";
        if (!objectManager.TryGetObject<Kingdom>(args[1], out var second) || second == null)
            return $"No kingdom with id {args[1]}.";

        bool applyChanges = !(args.Count == 3 && args[2].Trim().Equals("scan", StringComparison.OrdinalIgnoreCase));

        var behavior = Campaign.Current?.GetCampaignBehavior<IAllianceCampaignBehavior>();
        if (behavior == null) return "AllianceCampaignBehavior is not registered in this campaign.";

        bool allied = first.IsAllyWith(second);
        var before = $"{first.Name} <-> {second.Name}: allied={allied}, atWar={first.IsAtWarWith(second)}";

        if (!allied)
            return $"{before}\nThey are not allied; nothing to end.";

        if (!applyChanges)
            return $"{before}\nWould call EndAlliance (nothing changed - omit 'scan' to apply).";

        behavior.EndAlliance(first, second);

        // The per-kingdom list is a cache; refresh both sides so nothing keeps reading a stale alliance.
        first.UpdateAlliedKingdoms();
        second.UpdateAlliedKingdoms();

        var after = $"{first.Name} <-> {second.Name}: allied={first.IsAllyWith(second)}, atWar={first.IsAtWarWith(second)}";

        Logger.Information("[Diplomacy] Ended alliance between {First} and {Second}", first.Name, second.Name);

        return $"Ended alliance.\n  before: {before}\n  after:  {after}";
    }

    [CommandLineArgumentFunction("join_kingdom", "coop.debug.kingdom")]
    public static string JoinKingdom(List<string> args)
    {
        if (ModInformation.IsClient) return "Command can only be run on the server.";
        if (args.Count < 2 || args.Count > 5)
            return "Usage: coop.debug.kingdom.join_kingdom <clanId> <kingdomId> [gold] [relation] [scan]";

        if (ContainerProvider.TryResolve<IObjectManager>(out var objectManager) == false)
            return "Unable to resolve the ObjectManager.";
        if (!objectManager.TryGetObject<Clan>(args[0], out var clan) || clan == null)
            return $"No clan with id {args[0]}.";
        if (!objectManager.TryGetObject<Kingdom>(args[1], out var kingdom) || kingdom == null)
            return $"No kingdom with id {args[1]}.";

        int gold = 0;
        if (args.Count >= 3 && !int.TryParse(args[2].Trim(), out gold))
            return $"'{args[2]}' is not a whole number of denars.";

        int relation = 0;
        if (args.Count >= 4 && !int.TryParse(args[3].Trim(), out relation))
            return $"'{args[3]}' is not a whole number for relation.";

        bool applyChanges = !(args.Count == 5 && args[4].Trim().Equals("scan", StringComparison.OrdinalIgnoreCase));

        var payer = kingdom.Leader;
        var payee = clan.Leader;
        if (payer == null) return $"{kingdom.Name} has no leader to pay from.";
        if (payee == null) return $"{clan.Name} has no leader to pay to.";

        var oldKingdom = clan.Kingdom;
        var report = new StringBuilder();
        report.AppendLine($"  clan:     {clan.Name} (leader {payee.Name}, gold {payee.Gold:n0})");
        report.AppendLine($"  kingdom:  {kingdom.Name} (leader {payer.Name}, gold {payer.Gold:n0})");
        report.AppendLine($"  current:  {(oldKingdom == null ? "<independent>" : oldKingdom.Name.ToString())}");
        report.AppendLine($"  payment:  {gold:n0} denars, relation {relation:+#;-#;0}");
        report.AppendLine($"  method:   {(JoinIsDefection(oldKingdom != null) ? "defection (clan already in a kingdom)" : "plain join")}");

        if (!PaymentIsAffordable(payer.Gold, gold))
            return $"{payer.Name} has {payer.Gold:n0} denars and cannot pay {gold:n0}. Nothing applied.\n{report}";

        if (ReferenceEquals(oldKingdom, kingdom))
            return $"{clan.Name} is already in {kingdom.Name}. Nothing to do.\n{report}";

        if (!applyChanges)
            return $"Would apply (nothing changed - omit 'scan' to apply):\n{report}";

        // Deadline in the past: the clan carries no obligation to stay, which is what a negotiated join means.
        var noStayObligation = CampaignTime.Now;

        if (JoinIsDefection(oldKingdom != null))
            ChangeKingdomAction.ApplyByJoinToKingdomByDefection(clan, oldKingdom, kingdom, noStayObligation, false);
        else
            ChangeKingdomAction.ApplyByJoinToKingdom(clan, kingdom, noStayObligation, false);

        if (gold > 0)
            GiveGoldAction.ApplyBetweenCharacters(payer, payee, gold, true);

        if (relation != 0)
            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(payer, payee, relation, false);

        Logger.Information(
            "[Diplomacy] {Clan} joined {Kingdom} (from {Old}); paid {Gold} and {Relation} relation",
            clan.Name, kingdom.Name, oldKingdom?.Name?.ToString() ?? "<independent>", gold, relation);

        report.AppendLine($"  AFTER:    kingdom={clan.Kingdom?.Name?.ToString() ?? "<independent>"}, " +
                          $"{payer.Name} {payer.Gold:n0}, {payee.Name} {payee.Gold:n0}, " +
                          $"relation {CharacterRelationManager.GetHeroRelation(payer, payee)}");

        return $"Applied.\n{report}";
    }
}
