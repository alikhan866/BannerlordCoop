using Common;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.Heroes.Commands;

/// <summary>
/// C13 - select a conversation line, by index or by token.
/// </summary>
/// <remarks>
/// Selection is not blocked. ConversationManager exposes DoOption in both forms the plan asks for, and
/// AlleyRecruitDebugCommand already drives the token form for its own regression - this generalises it so any
/// conversation can be driven rather than one scripted alley exchange.
///
/// INDEX SELECTION GOES THROUGH THE TOKEN
/// DoOption has an int overload, but its parameter is named optionIndex while ConversationSentenceOption also
/// carries a SentenceNo - two plausible meanings for one int, and no way to tell them apart without a live
/// conversation. So index selection looks the option up in CurOptions and calls the STRING overload, which is
/// unambiguous and is the form already proven in this codebase. The capability is identical and rests on one
/// less assumption.
///
/// WHAT SELECTION CANNOT DO
/// Getting a headless client INTO a conversation is a separate question, and the flag to watch is
/// NeedsToActivateForMapConversation - the conversation analogue of the menu-activation problem that C12 and
/// C15 are parked behind. state reports it, so a run that cannot converse says why instead of looking like a
/// broken selector. Starting a conversation is C17's "talk to notables", which is already recorded as UI-gated.
/// </remarks>
public class ConversationLineDebugCommand
{
    [CommandLineArgumentFunction("state", "coop.debug.conversation")]
    public static string State(List<string> args)
    {
        if (args.Count != 0) return "Usage: coop.debug.conversation.state";

        var manager = Campaign.Current?.ConversationManager;
        if (manager == null) return "CONVERSATION_STATE campaign=false";
        if (!manager.IsConversationInProgress)
            return $"CONVERSATION_STATE inProgress=false " +
                   $"needsActivationForMapConversation={Lower(manager.NeedsToActivateForMapConversation)}";

        var options = manager.CurOptions ?? new List<ConversationSentenceOption>();
        var result = new StringBuilder();
        result.AppendLine(
            $"CONVERSATION_STATE inProgress=true flowActive={Lower(manager.IsConversationFlowActive)} " +
            $"needsActivationForMapConversation={Lower(manager.NeedsToActivateForMapConversation)} " +
            $"with={manager.OneToOneConversationHero?.StringId ?? "none"} options={options.Count}");
        result.AppendLine($"sentence={manager.CurrentSentenceText}");

        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];

            // IsClickable and HasPersuasion are reported because both change what a selection MEANS. Choosing
            // an unclickable option is a silent no-op, and a persuasion option's outcome is a roll - which is
            // only repeatable once C21's seed is set, so a scenario needs to know it is standing on one.
            result.AppendLine(
                $"index={index}|id={option.Id}|clickable={Lower(option.IsClickable)}|" +
                $"persuasion={Lower(option.HasPersuasion)}|skill={option.SkillName ?? "none"}|" +
                $"text={option.Text?.ToString() ?? string.Empty}");
        }

        return result.ToString();
    }

    [CommandLineArgumentFunction("select_index", "coop.debug.conversation")]
    public static string SelectIndex(List<string> args)
    {
        if (ModInformation.IsServer) return "Run this command on the conversing client.";
        if (args.Count != 1 ||
            !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            return "Usage: coop.debug.conversation.select_index <0-based index>";
        if (!TryGetOptions(out var manager, out var options, out var error)) return error;

        if (index >= options.Count)
            return $"Index {index} is out of range - the conversation offers {options.Count} option(s).";

        return Choose(manager, options[index], $"index={index}");
    }

    [CommandLineArgumentFunction("select_id", "coop.debug.conversation")]
    public static string SelectId(List<string> args)
    {
        if (ModInformation.IsServer) return "Run this command on the conversing client.";
        if (args.Count != 1) return "Usage: coop.debug.conversation.select_id <optionId>";
        if (!TryGetOptions(out var manager, out var options, out var error)) return error;

        // FindIndex, not FirstOrDefault: ConversationSentenceOption is a STRUCT, so a miss returns a default
        // value rather than null - the absent option would have been "found" as a blank one and selected.
        var position = options.FindIndex(candidate => candidate.Id == args[0]);
        if (position < 0)
            return $"Option '{args[0]}' is not on offer. Available: " +
                   $"{string.Join(",", options.Select(candidate => candidate.Id))}";

        return Choose(manager, options[position], $"id={args[0]}");
    }

    [CommandLineArgumentFunction("continue", "coop.debug.conversation")]
    public static string Continue(List<string> args)
    {
        if (ModInformation.IsServer) return "Run this command on the conversing client.";
        if (args.Count != 0) return "Usage: coop.debug.conversation.continue";

        var manager = Campaign.Current?.ConversationManager;
        if (manager?.IsConversationInProgress != true) return "No conversation is in progress.";

        manager.DoOptionContinue();

        return $"CONVERSATION_CONTINUED inProgress={Lower(manager.IsConversationInProgress)} " +
               $"sentence={manager.CurrentSentenceText}";
    }

    [CommandLineArgumentFunction("end", "coop.debug.conversation")]
    public static string End(List<string> args)
    {
        if (ModInformation.IsServer) return "Run this command on the conversing client.";
        if (args.Count != 0) return "Usage: coop.debug.conversation.end";

        var manager = Campaign.Current?.ConversationManager;
        if (manager?.IsConversationInProgress != true) return "CONVERSATION_ENDED wasInProgress=false";

        manager.EndConversation();

        return $"CONVERSATION_ENDED wasInProgress=true inProgress={Lower(manager.IsConversationInProgress)}";
    }

    private static string Choose(ConversationManager manager, ConversationSentenceOption option, string how)
    {
        // Refused rather than attempted. DoOption on an unclickable line does nothing at all, and a command
        // that reported success for it would have a scenario asserting against a conversation that never moved.
        if (!option.IsClickable)
            return $"Option '{option.Id}' is not clickable, so selecting it would do nothing.";

        var before = manager.CurrentSentenceText;

        // Always the string overload, including for index selection - see the class remarks.
        manager.DoOption(option.Id);

        return $"CONVERSATION_SELECTED {how} id={option.Id} persuasion={Lower(option.HasPersuasion)} " +
               $"inProgress={Lower(manager.IsConversationInProgress)} " +
               $"sentenceBefore={before} sentenceAfter={manager.CurrentSentenceText} " +
               $"options={manager.CurOptions?.Count ?? 0}";
    }

    private static bool TryGetOptions(
        out ConversationManager manager,
        out List<ConversationSentenceOption> options,
        out string error)
    {
        options = null;
        error = null;
        manager = Campaign.Current?.ConversationManager;

        if (manager?.IsConversationInProgress != true)
        {
            error = "No conversation is in progress.";
            return false;
        }

        options = manager.CurOptions;
        if (options == null || options.Count == 0)
        {
            error = "The conversation is in progress but is offering no options - try coop.debug.conversation.continue.";
            return false;
        }

        return true;
    }

    private static string Lower(bool value) => value.ToString().ToLowerInvariant();
}
