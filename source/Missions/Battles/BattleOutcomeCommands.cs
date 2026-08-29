using GameInterface.Services.MapEvents.TroopSupply;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace Missions.Battles;

/// <summary>
/// Plan 2 C8 (battle outcome) and C9 (battle timeline), read from the ledger that recorded them.
/// </summary>
/// <remarks>
/// Neither command measures anything. The outcome is captured by MapEventOutcomeCapturePatch at the only
/// moment the numbers exist, and the timeline is written by the code that already knew each thing happened.
/// These read what was recorded, which is what keeps one set of numbers rather than two that drift apart.
///
/// BOTH ARE PER PROCESS
/// A client and the server each record what THEY saw finalize. That is the point rather than a limitation:
/// a battle that committed on the server and not on a client is a real and previously observed failure, and
/// it is only visible by asking both and comparing. Neither command reconciles.
/// </remarks>
public static class BattleOutcomeCommands
{
    [CommandLineArgumentFunction("outcome", "coop.debug.battle")]
    public static string Outcome(List<string> args)
    {
        var (filter, asJsonOnly, error) = ParseArguments(args, "outcome");
        if (error != null) return error;

        var (outcomes, dropped) = BattleObservationLedger.GetOutcomes(filter);
        if (outcomes.Count == 0)
            return $"BATTLE_OUTCOME count=0 dropped={dropped} " +
                   $"note=no battle has finalized in this process{(filter == null ? "" : $" for {filter}")}";

        // Newest first: a scenario almost always wants the battle it just ran, and making it count from the
        // end of a list whose length it cannot predict is how the wrong battle gets asserted on.
        var ordered = outcomes.Reverse().ToArray();

        if (asJsonOnly)
            return "LIVE_TEST_JSON=" + JsonConvert.SerializeObject(new { count = ordered.Length, dropped, outcomes = ordered });

        var report = new StringBuilder();
        report.AppendLine($"BATTLE_OUTCOME count={ordered.Length} dropped={dropped} order=newest-first");
        foreach (var outcome in ordered)
        {
            report.AppendLine("  " + outcome.Headline);
            foreach (var party in outcome.Parties) report.AppendLine("    " + party);
        }
        report.Append("LIVE_TEST_JSON=" + JsonConvert.SerializeObject(new { count = ordered.Length, dropped, outcomes = ordered }));
        return report.ToString();
    }

    [CommandLineArgumentFunction("timeline", "coop.debug.battle")]
    public static string Timeline(List<string> args)
    {
        var (filter, asJsonOnly, error) = ParseArguments(args, "timeline");
        if (error != null) return error;

        var (entries, dropped) = BattleObservationLedger.GetTimeline(filter);
        if (asJsonOnly)
            return "LIVE_TEST_JSON=" + JsonConvert.SerializeObject(new
            {
                count = entries.Count,
                dropped,
                entries = entries.Select(entry => new
                {
                    atUtc = entry.AtUtc.ToString("O", CultureInfo.InvariantCulture),
                    mapEventId = entry.MapEventId,
                    kind = entry.Kind,
                    detail = entry.Detail,
                }).ToArray(),
            });

        var report = new StringBuilder();
        report.AppendLine(
            $"BATTLE_TIMELINE count={entries.Count} dropped={dropped} " +
            $"filter={filter ?? "none"} order=oldest-first");
        foreach (var entry in entries) report.AppendLine("  " + entry);
        if (entries.Count == 0) report.AppendLine("  (nothing recorded yet)");
        return report.ToString().TrimEnd();
    }

    /// <summary>Adds a marker of the caller's own, so a scenario can bracket the part it cares about.</summary>
    /// <remarks>
    /// Worth having because the interesting question is usually "what happened between the moment I started
    /// this and the moment it went wrong", and a timeline with no caller-supplied landmarks makes that a
    /// matter of reading timestamps and hoping.
    /// </remarks>
    [CommandLineArgumentFunction("mark", "coop.debug.battle")]
    public static string Mark(List<string> args)
    {
        if (args.Count == 0) return "Usage: coop.debug.battle.mark <text...>";
        string text = string.Join(" ", args);
        BattleObservationLedger.RecordEvent(null, "SCENARIO_MARK", text);
        return $"BATTLE_TIMELINE_MARK recorded=\"{text}\"";
    }

    private static (string Filter, bool AsJsonOnly, string Error) ParseArguments(List<string> args, string verb)
    {
        bool asJsonOnly = args.Count > 0 &&
            string.Equals(args[args.Count - 1], "json", StringComparison.OrdinalIgnoreCase);
        var positional = asJsonOnly ? args.Take(args.Count - 1).ToList() : args;

        if (positional.Count > 1)
            return (null, false, $"Usage: coop.debug.battle.{verb} [mapEventId] [json]");

        return (positional.Count == 1 ? positional[0] : null, asJsonOnly, null);
    }
}
