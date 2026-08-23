using Common;
using Common.Logging;
using Common.Util;
using GameInterface.Services.Settlements.Interfaces;
using GameInterface.Utils.Commands;
using SandBox.GauntletUI.Map;
using SandBox.View.Map;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.GameDebug.Commands;

/// <summary>
/// [Debug] UI / screen commands. <c>coop.debug.ui.close_screen</c> forces the current game menu to exit
/// (<see cref="GameMenu.ExitToLast"/>) — a manual escape for when a post-battle encounter screen is left open.
/// </summary>
internal class UiDebugCommands
{
    public static readonly ILogger Logger = LogManager.GetLogger<UiDebugCommands>();

    private const string CloseScreenUsage =
@"Usage:
  coop.debug.ui.close_screen

Exits the current game menu (GameMenu.ExitToLast). Use to dismiss a post-battle encounter screen left open.";

    [CommandLineArgumentFunction("close_screen", "coop.debug.ui")]
    public static string CloseScreen(List<string> args)
    {
        var ctx = new CommandContext("close_screen", CloseScreenUsage, args);
        if (!ctx.RequireArgCount(0, out var error))
            return error;

        if (Campaign.Current == null)
            return "Failed: no active campaign.";

        try
        {
            GameMenu.ExitToLast();
        }
        catch (Exception ex)
        {
            return CommandHelpers.FormatException("Close screen", ex);
        }

        return "Called GameMenu.ExitToLast().";
    }

    /// <summary>
    /// Raises a real popup through the engine's own entry point, to prove the capture intercepts it.
    /// </summary>
    /// <remarks>
    /// The capture is only worth having if something actually lands in it, and waiting for the campaign to
    /// volunteer one leaves the path untested for as long as the world happens to be quiet. This calls the
    /// same <c>InformationManager.ShowInquiry</c> / <c>AddQuickInformation</c> that every caller in the game
    /// uses, so intercepting this intercepts all of them.
    ///
    /// Not a mock: routing through the real API is the entire point. A test that called PopupCapture.Record
    /// directly would prove only that a list can hold an item.
    /// </remarks>
    [CommandLineArgumentFunction("raise_test_popup", "coop.debug.ui")]
    public static string RaiseTestPopup(List<string> args)
    {
        string kind = args.Count == 0 ? "inquiry" : args[0].ToLowerInvariant();

        switch (kind)
        {
            case "inquiry":
                InformationManager.ShowInquiry(new InquiryData(
                    "Rig self-test",
                    "Raised by coop.debug.ui.raise_test_popup to prove popup capture.",
                    true, true,
                    "Accept", "Decline",
                    null, null));
                return "Raised an inquiry. Check coop.debug.ui.popup_log.";

            case "quick":
                MBInformationManager.AddQuickInformation(
                    new TaleWorlds.Localization.TextObject("Rig self-test quick information."));
                return "Raised a quick information message. Check coop.debug.ui.popup_log.";

            case "message":
                InformationManager.DisplayMessage(
                    new InformationMessage("Rig self-test campaign message."));
                return "Raised a campaign message. Check coop.debug.ui.message_log.";

            default:
                return "Usage: coop.debug.ui.raise_test_popup [inquiry|quick|message]";
        }
    }

    /// <summary>C24 - arm or inspect the server's refusal of irreversible actions.</summary>
    /// <remarks>
    /// Deliberately reachable while armed: the guard denies irreversible ACTS, not the ability to see what it
    /// has refused. A guard whose own status command was blocked would be indistinguishable from a broken one.
    /// </remarks>
    [CommandLineArgumentFunction("guard", "coop.debug.testclient")]
    public static string Guard(List<string> args)
    {
        if (args.Count == 1 && string.Equals(args[0], "arm", StringComparison.OrdinalIgnoreCase))
        {
            Services.Headless.IrreversibleActionGuard.Arm(true);
            return "Irreversible-action refusal ARMED.";
        }
        if (args.Count == 1 && string.Equals(args[0], "disarm", StringComparison.OrdinalIgnoreCase))
        {
            Services.Headless.IrreversibleActionGuard.Arm(false);
            return "Irreversible-action refusal disarmed.";
        }
        if (args.Count == 1 && string.Equals(args[0], "clear", StringComparison.OrdinalIgnoreCase))
            return $"Cleared {Services.Headless.IrreversibleActionGuard.ClearRefusals()} refusal(s).";
        if (args.Count != 0)
            return "Usage: coop.debug.testclient.guard [arm|disarm|clear]";

        var refusals = Services.Headless.IrreversibleActionGuard.RefusalLog;
        var report = new System.Text.StringBuilder();
        report.AppendLine($"GUARD armed={Services.Headless.IrreversibleActionGuard.IsArmed} " +
                          $"denied={Services.Headless.IrreversibleActionGuard.DeniedPatterns.Count} " +
                          $"refusals={refusals.Count}");
        foreach (var refusal in refusals) report.AppendLine($"  REFUSED {refusal}");
        return report.ToString().TrimEnd();
    }

    /// <summary>C23 - declare how a named popup should be answered.</summary>
    [CommandLineArgumentFunction("popup_declare", "coop.debug.ui")]
    public static string PopupDeclare(List<string> args)
    {
        if (args.Count == 1 && string.Equals(args[0], "clear", StringComparison.OrdinalIgnoreCase))
            return $"Cleared {Services.Headless.PopupPolicy.ClearDeclarations()} declaration(s).";

        if (args.Count < 2)
            return "Usage: coop.debug.ui.popup_declare <affirmative|negative> <pattern...>  |  popup_declare clear";

        Services.Headless.PopupPolicy.Answer answer;
        switch (args[0].ToLowerInvariant())
        {
            case "affirmative": answer = Services.Headless.PopupPolicy.Answer.Affirmative; break;
            case "negative": answer = Services.Headless.PopupPolicy.Answer.Negative; break;
            default: return "The answer must be 'affirmative' or 'negative'. There is no accept-all.";
        }

        string pattern = string.Join(" ", args.Skip(1));
        Services.Headless.PopupPolicy.Declare(pattern, answer);
        return $"Declared '{pattern}' -> {answer}.";
    }

    /// <summary>C23 - the declarations in force, and whether the run has already failed.</summary>
    [CommandLineArgumentFunction("popup_policy", "coop.debug.ui")]
    public static string PopupPolicyState(List<string> args)
    {
        if (args.Count == 1 && string.Equals(args[0], "reset_failures", StringComparison.OrdinalIgnoreCase))
            return $"Cleared {Services.Headless.PopupPolicy.ClearFailures()} failure(s).";
        if (args.Count != 0)
            return "Usage: coop.debug.ui.popup_policy [reset_failures]";

        var declared = Services.Headless.PopupPolicy.Declared;
        var failures = Services.Headless.PopupPolicy.FailureReasons;

        var report = new System.Text.StringBuilder();
        report.AppendLine($"POPUP_POLICY runFailed={Services.Headless.PopupPolicy.RunFailed} " +
                          $"declarations={declared.Count} failures={failures.Count}");
        foreach (var declaration in declared)
            report.AppendLine($"  declared '{declaration.Pattern}' -> {declaration.Answer} matched={declaration.Matched}");
        foreach (var failure in failures)
            report.AppendLine($"  FAILURE {failure}");
        return report.ToString().TrimEnd();
    }

    /// <summary>C27 - the campaign messages this process could not show.</summary>
    [CommandLineArgumentFunction("message_log", "coop.debug.ui")]
    public static string MessageLog(List<string> args)
    {
        if (args.Count == 1 && string.Equals(args[0], "clear", StringComparison.OrdinalIgnoreCase))
            return $"Cleared {Services.Headless.PopupCapture.ClearMessages()} message(s).";

        var captured = Services.Headless.PopupCapture.MessageSnapshot();

        if (args.Count >= 1 && string.Equals(args[0], "json", StringComparison.OrdinalIgnoreCase))
        {
            int jsonWanted = args.Count > 1 && int.TryParse(args[1], out var parsed) ? parsed : 200;
            var chosen = captured.Skip(Math.Max(0, captured.Count - Math.Max(1, jsonWanted))).ToList();
            var json = new System.Text.StringBuilder();
            json.Append("{\"count\":").Append(captured.Count).Append(",\"messages\":[");
            for (int index = 0; index < chosen.Count; index++)
            {
                if (index > 0) json.Append(',');
                var message = chosen[index];
                json.Append("{\"sequence\":").Append(message.Sequence)
                    .Append(",\"text\":").Append(JsonString(message.Text))
                    .Append(",\"campaignTime\":").Append(JsonString(message.CampaignTime))
                    .Append(",\"campaignDays\":")
                    .Append(message.CampaignDays.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(",\"realTimeUtc\":")
                    .Append(JsonString(message.RealTimeUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)))
                    .Append('}');
            }
            return "LIVE_TEST_JSON=" + json.Append("]}");
        }

        int wanted = 30;
        if (args.Count == 1 && !int.TryParse(args[0], out wanted))
            return "Usage: coop.debug.ui.message_log [clear|json [count]|<count>]";

        if (captured.Count == 0) return "MESSAGE_LOG count=0";
        var report = new System.Text.StringBuilder();
        report.AppendLine($"MESSAGE_LOG count={captured.Count}");
        foreach (var message in captured.Skip(Math.Max(0, captured.Count - wanted)))
            report.AppendLine($"#{message.Sequence} utc={message.RealTimeUtc:HH:mm:ss} " +
                              $"campaign='{message.CampaignTime}' text='{message.Text}'");
        return report.ToString().TrimEnd();
    }

    /// <summary>C22 - the popups this process could not show.</summary>
    [CommandLineArgumentFunction("popup_log", "coop.debug.ui")]
    public static string PopupLog(List<string> args)
    {
        if (args.Count > 1)
            return "Usage: coop.debug.ui.popup_log [clear|<count>]";

        if (args.Count == 1 && string.Equals(args[0], "clear", StringComparison.OrdinalIgnoreCase))
            return $"Cleared {Services.Headless.PopupCapture.Clear()} captured popup(s).";

        // A machine-readable form, because C25 asserts on these and parsing the prose below would break on
        // any popup whose own text contained a quote or a bracket - which player-facing text eventually does.
        if (args.Count >= 1 && string.Equals(args[0], "json", StringComparison.OrdinalIgnoreCase))
            return PopupLogJson(args.Count > 1 && int.TryParse(args[1], out var jsonCount) ? jsonCount : 200);

        int wanted = 25;
        if (args.Count == 1 && !int.TryParse(args[0], out wanted))
            return "Usage: coop.debug.ui.popup_log [clear|json [count]|<count>]";

        var captured = Services.Headless.PopupCapture.Snapshot();
        if (captured.Count == 0) return "POPUP_LOG count=0";

        var report = new System.Text.StringBuilder();
        report.AppendLine($"POPUP_LOG count={captured.Count}");
        foreach (var popup in captured.Skip(Math.Max(0, captured.Count - wanted)))
        {
            report.AppendLine(
                $"#{popup.Sequence} {popup.Kind} utc={popup.RealTimeUtc:HH:mm:ss} " +
                $"campaign='{popup.CampaignTime}' days={popup.CampaignDays:F4} " +
                $"answered={popup.Answered} answer='{popup.Answer}' title='{popup.Title}' " +
                $"options=[{string.Join(" | ", popup.Options.Where(o => !string.IsNullOrEmpty(o)))}] " +
                $"text='{popup.Text}'");
        }
        return report.ToString().TrimEnd();
    }

    /// <summary>The popup log as one LIVE_TEST_JSON line, for assertions rather than for reading.</summary>
    private static string PopupLogJson(int wanted)
    {
        var captured = Services.Headless.PopupCapture.Snapshot();
        var chosen = captured.Skip(Math.Max(0, captured.Count - Math.Max(1, wanted))).ToList();

        var json = new System.Text.StringBuilder();
        json.Append("{\"count\":").Append(captured.Count).Append(",\"popups\":[");
        for (int index = 0; index < chosen.Count; index++)
        {
            var popup = chosen[index];
            if (index > 0) json.Append(',');
            json.Append("{\"sequence\":").Append(popup.Sequence)
                .Append(",\"kind\":").Append(JsonString(popup.Kind))
                .Append(",\"title\":").Append(JsonString(popup.Title))
                .Append(",\"text\":").Append(JsonString(popup.Text))
                .Append(",\"answered\":").Append(popup.Answered ? "true" : "false")
                .Append(",\"answer\":").Append(JsonString(popup.Answer))
                .Append(",\"campaignTime\":").Append(JsonString(popup.CampaignTime))
                .Append(",\"campaignDays\":")
                .Append(popup.CampaignDays.ToString("F6", System.Globalization.CultureInfo.InvariantCulture))
                .Append(",\"realTimeUtc\":")
                .Append(JsonString(popup.RealTimeUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture)))
                .Append(",\"options\":[");
            for (int optionIndex = 0; optionIndex < popup.Options.Length; optionIndex++)
            {
                if (optionIndex > 0) json.Append(',');
                json.Append(JsonString(popup.Options[optionIndex]));
            }
            json.Append("]}");
        }
        json.Append("]}");

        return "LIVE_TEST_JSON=" + json;
    }

    /// <summary>Escapes a string for JSON by hand - popup text is arbitrary and quotes appear in it.</summary>
    private static string JsonString(string value)
    {
        if (value == null) return "null";

        var escaped = new System.Text.StringBuilder("\"");
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': escaped.Append("\\\""); break;
                case '\\': escaped.Append("\\\\"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                case '\t': escaped.Append("\\t"); break;
                default:
                    if (character < ' ') escaped.Append("\\u").Append(((int)character).ToString("x4"));
                    else escaped.Append(character);
                    break;
            }
        }
        return escaped.Append('"').ToString();
    }

    /// <summary>
    /// Lists the current menu's options with the result of each one's condition.
    /// </summary>
    /// <remarks>
    /// The enabled flag is the whole point. A driven client that invoked a consequence directly would be
    /// bypassing the menu rather than clicking it, and would happily "succeed" at something a player cannot
    /// do - which is exactly the class of bug this rig exists to find, so it must be able to SEE a wrongly
    /// disabled option rather than step over it.
    ///
    /// Found by reflection instead of a compile-time member: the option list hangs off MenuContext under a
    /// name that is not part of the public surface, and guessing it wrong costs a build and a campaign load
    /// per attempt. Reflection also keeps this working if the field is renamed by a game update - it reports
    /// that it could not find the list rather than failing to compile.
    /// </remarks>
    [CommandLineArgumentFunction("menu_options", "coop.debug.ui")]
    public static string MenuOptions(List<string> args)
    {
        bool asJson = args.Count == 1 && string.Equals(args[0], "json", StringComparison.OrdinalIgnoreCase);
        if (args.Count > 1 || (args.Count == 1 && !asJson))
            return "Usage: coop.debug.ui.menu_options [json]";

        var menuContext = Campaign.Current?.CurrentMenuContext;
        if (menuContext == null)
            return asJson ? "LIVE_TEST_JSON={\"menuOpen\":false}" : "No game menu is open.";

        string menuId = menuContext.GameMenu?.StringId ?? "unknown";
        var options = FindMenuOptions(menuContext);

        // C26 - the reachability report, which must describe an EMPTY menu as clearly as a full one. On a
        // render-free client the context is never activated, so a real menu legitimately offers nothing; a
        // command that only printed options would show the same blank as a command that had crashed.
        if (asJson) return MenuJson(menuContext, menuId, options);

        if (options == null)
        {
            // Self-describing on failure. Reporting only "not found" would cost a build and a campaign load
            // per guess at the field name, which is the loop this command was written to avoid.
            return $"menu={menuId} contextState={ReadMember(menuContext, "_currentState") ?? "?"}: " +
                   $"no option list found. Candidates:\n" + DescribeCandidates(menuContext);
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine($"menu={menuId} options={options.Count}");
        for (int index = 0; index < options.Count; index++)
        {
            object option = options[index];
            report.AppendLine(
                $"[{index}] id={ReadMember(option, "IdString") ?? "?"} " +
                $"enabled={ReadMember(option, "IsEnabled") ?? "?"} " +
                $"disabled={ReadMember(option, "IsDisabled") ?? "?"} " +
                $"text='{ReadMember(option, "Text")}'");
        }
        return report.ToString().TrimEnd();
    }

    /// <summary>C26 - menu reachability as one LIVE_TEST_JSON line.</summary>
    /// <remarks>
    /// Carries contextState and activated alongside the options, because "no options" has two very different
    /// causes: a menu that genuinely offers nothing right now, and a menu whose context was never brought to
    /// life. Reporting only the count would collapse those into the same answer, and the second is a defect
    /// while the first is ordinary.
    /// </remarks>
    private static string MenuJson(MenuContext menuContext, string menuId, System.Collections.IList options)
    {
        string contextState = ReadMember(menuContext, "_currentState");
        var json = new System.Text.StringBuilder();
        json.Append("{\"menuOpen\":true,\"menuId\":").Append(JsonString(menuId))
            .Append(",\"contextState\":").Append(JsonString(contextState))
            .Append(",\"initialized\":").Append(string.Equals(ReadMember(menuContext, "IsInitialized"), "True",
                StringComparison.OrdinalIgnoreCase) ? "true" : "false")
            .Append(",\"optionListFound\":").Append(options == null ? "false" : "true")
            .Append(",\"optionCount\":").Append(options?.Count ?? 0)
            .Append(",\"options\":[");

        if (options != null)
        {
            for (int index = 0; index < options.Count; index++)
            {
                if (index > 0) json.Append(',');
                object option = options[index];
                json.Append("{\"index\":").Append(index)
                    .Append(",\"id\":").Append(JsonString(ReadMember(option, "IdString")))
                    .Append(",\"enabled\":").Append(
                        string.Equals(ReadMember(option, "IsEnabled"), "True", StringComparison.OrdinalIgnoreCase)
                            ? "true" : "false")
                    .Append(",\"text\":").Append(JsonString(ReadMember(option, "Text")))
                    .Append('}');
            }
        }
        return "LIVE_TEST_JSON=" + json.Append("]}");
    }

    /// <summary>The option collection, wherever the engine keeps it on this build.</summary>
    private static System.Collections.IList FindMenuOptions(object menuContext)
    {
        foreach (object holder in new[] { menuContext, (object)((MenuContext)menuContext).GameMenu })
        {
            if (holder == null) continue;

            foreach (var field in holder.GetType().GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic))
            {
                if (!(field.GetValue(holder) is System.Collections.IList list) || list.Count == 0) continue;
                if (list[0] is GameMenuOption) return list;
            }
        }
        return null;
    }

    /// <summary>Every field and property on the menu objects, with collection element types and counts.</summary>
    private static string DescribeCandidates(object menuContext)
    {
        var report = new System.Text.StringBuilder();
        var holders = new (string Label, object Value)[]
        {
            ("MenuContext", menuContext),
            ("GameMenu", ((MenuContext)menuContext).GameMenu),
        };

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        foreach (var holder in holders)
        {
            if (holder.Value == null) { report.AppendLine($"{holder.Label}: <null>"); continue; }

            foreach (var field in holder.Value.GetType().GetFields(flags))
            {
                object value = null;
                try { value = field.GetValue(holder.Value); } catch { }
                report.AppendLine($"  {holder.Label}.{field.Name} : {field.FieldType.Name} = {Describe(value)}");
            }
            foreach (var property in holder.Value.GetType().GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0) continue;
                object value = null;
                try { value = property.GetValue(holder.Value); } catch { }
                report.AppendLine($"  {holder.Label}.{property.Name} : {property.PropertyType.Name} = {Describe(value)}");
            }
        }
        return report.ToString().TrimEnd();
    }

    private static string Describe(object value)
    {
        if (value == null) return "<null>";
        if (value is string text) return $"'{text}'";
        if (value is System.Collections.IEnumerable sequence && !(value is string))
        {
            int count = 0;
            string elementType = "?";
            foreach (object item in sequence)
            {
                if (count == 0 && item != null) elementType = item.GetType().Name;
                count++;
                if (count > 200) break;
            }
            return $"[{count} x {elementType}]";
        }
        return value.ToString();
    }

    private static string ReadMember(object instance, string name)
    {
        if (instance == null) return null;

        var property = instance.GetType().GetProperty(name);
        if (property != null) return property.GetValue(instance)?.ToString();

        var field = instance.GetType().GetField(name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(instance)?.ToString();
    }

    [CommandLineArgumentFunction("prepare_evidence_map", "coop.debug.ui")]
    public static string PrepareEvidenceMap(List<string> args)
    {
        if (ModInformation.IsServer)
            return "Run this command on a client.";

        if (args.Count != 0)
            return "Usage: coop.debug.ui.prepare_evidence_map";

        MapScreen mapScreen = MapScreen.Instance;
        if (mapScreen == null)
            return "Campaign map screen is unavailable.";

        try
        {
            // Hide only the client presentation; keep the saved encounter and map event unchanged.
            if (mapScreen.IsInMenu)
            {
                mapScreen._latestMenuContext = null;
                mapScreen.ExitMenuContext();
            }
            mapScreen.RemoveEncounterOverlay();
        }
        catch (Exception ex)
        {
            return CommandHelpers.FormatException("Prepare evidence map", ex);
        }

        return GetEvidenceMapState(mapScreen);
    }

    [CommandLineArgumentFunction("evidence_map_state", "coop.debug.ui")]
    public static string EvidenceMapState(List<string> args)
    {
        if (ModInformation.IsServer)
            return "Run this command on a client.";

        if (args.Count != 0)
            return "Usage: coop.debug.ui.evidence_map_state";

        MapScreen mapScreen = MapScreen.Instance;
        return mapScreen == null
            ? "Campaign map screen is unavailable."
            : GetEvidenceMapState(mapScreen);
    }

    private static string GetEvidenceMapState(MapScreen mapScreen)
    {
        var cameraView = mapScreen.MapCameraView;
        PartyBase cameraFollowParty = Campaign.Current?.CameraFollowParty;
        string cameraFollowPartyId = cameraFollowParty?.MobileParty?.StringId ?? "null";
        string cameraMode = cameraView?.CurrentCameraFollowMode.ToString() ?? "null";
        bool followTargetReached = false;
        if (cameraView != null && cameraFollowParty != null)
        {
            var followPosition = cameraFollowParty.MapEvent?.Position ?? cameraFollowParty.Position;
            var targetDelta = followPosition.ToVec2() - cameraView._cameraTarget.AsVec2;
            followTargetReached = targetDelta.LengthSquared < 0.0001f;
        }

        return $"menuView={mapScreen.IsInMenu} " +
               $"pendingMenuView={mapScreen._latestMenuContext != null} " +
               $"encounterOverlay={mapScreen._encounterOverlay != null} " +
               $"cameraFollowParty={cameraFollowPartyId} " +
               $"cameraMode={cameraMode} " +
               $"followTargetReached={followTargetReached} " +
               $"animation={cameraView?.CameraAnimationInProgress} " +
               $"fastMove={cameraView?._doFastCameraMovementToTarget} " +
               $"loading={LoadingWindow.IsLoadingWindowActive}";
    }

    [CommandLineArgumentFunction("leave_settlement_encounter", "coop.debug.ui")]
    public static string LeaveSettlementEncounter(List<string> args)
    {
        if (ModInformation.IsServer)
            return "Run this command on a client.";

        if (args.Count != 0)
            return "Usage: coop.debug.ui.leave_settlement_encounter";

        if (Campaign.Current == null)
            return "Failed: no active campaign.";

        var mainParty = MobileParty.MainParty;
        if (mainParty == null)
            return "Failed: no main party.";

        if (PlayerEncounter.Battle != null || mainParty.MapEvent != null)
            return "Cannot leave the settlement encounter after a battle has started.";

        if (PlayerEncounter.Current == null || PlayerEncounter.EncounterSettlement == null)
            return "No active settlement encounter to leave.";

        if (!ContainerProvider.TryResolve<ISettlementInterface>(out var settlementInterface))
            return "Unable to resolve the settlement interface.";

        try
        {
            using (new AllowedThread())
                settlementInterface.EndSettlementEncounter();
        }
        catch (Exception ex)
        {
            return CommandHelpers.FormatException("Leave settlement encounter", ex);
        }

        return "Cleared the local settlement encounter and returned to the campaign map.";
    }

#if DEBUG
    [CommandLineArgumentFunction("map_click_offset", "coop.debug.ui")]
    public static string MapClickOffset(List<string> args)
    {
        if (ModInformation.IsServer)
            return "Run this command on a client.";
        if (args.Count != 2 ||
            !float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetX) ||
            !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetY))
            return "Usage: coop.debug.ui.map_click_offset <offsetX> <offsetY>";

        var mapScreen = MapScreen.Instance;
        var mainParty = MobileParty.MainParty;
        if (mapScreen == null || mainParty == null)
            return "Failed: campaign map or main party is unavailable.";
        if (PlayerEncounter.Current != null || mainParty.CurrentSettlement != null)
            return "Leave the active settlement encounter before clicking the campaign map.";
        if (mainParty.MapEvent != null)
            return "Cannot click-to-move while the main party is in a map event.";

        var current = mainParty.Position;
        var offsets = new[]
        {
            new Vec2(offsetX, offsetY),
            new Vec2(-offsetY, offsetX),
            new Vec2(-offsetX, -offsetY),
            new Vec2(offsetY, -offsetX),
        };
        CampaignVec2 target = default;
        bool targetFound = false;
        foreach (var offset in offsets)
        {
            var candidate = new CampaignVec2(
                new Vec2(current.X + offset.x, current.Y + offset.y),
                current.IsOnLand);
            if (!candidate.Face.IsValid() ||
                !mapScreen.MapScene.DoesPathExistBetweenFaces(
                    candidate.Face.FaceIndex,
                    mainParty.CurrentNavigationFace.FaceIndex,
                    false))
                continue;

            target = candidate;
            targetFound = true;
            break;
        }
        if (!targetFound)
            return "No nearby navigable map-click target was found.";

        mapScreen.HandleLeftMouseButtonClick(null, target, target.Face, false);

        return
            $"Issued a real campaign-map click from {current.X:R},{current.Y:R} " +
            $"to {target.X:R},{target.Y:R}; time={Campaign.Current.TimeControlMode}; " +
            $"behavior={mainParty.DefaultBehavior}; target={mainParty.TargetPosition.X:R},{mainParty.TargetPosition.Y:R}.";
    }

    [CommandLineArgumentFunction("map_movement_state", "coop.debug.ui")]
    public static string MapMovementState(List<string> args)
    {
        if (ModInformation.IsServer)
            return "Run this command on a client.";
        if (args.Count != 0)
            return "Usage: coop.debug.ui.map_movement_state";

        var mainParty = MobileParty.MainParty;
        if (mainParty == null || Campaign.Current == null)
            return "Failed: no active campaign or main party.";

        return
            $"position={mainParty.Position.X:R},{mainParty.Position.Y:R}|" +
            $"target={mainParty.TargetPosition.X:R},{mainParty.TargetPosition.Y:R}|" +
            $"behavior={mainParty.DefaultBehavior}|" +
            $"settlement={mainParty.CurrentSettlement?.StringId ?? "none"}|" +
            $"encounter={PlayerEncounter.EncounterSettlement?.StringId ?? "none"}|" +
            $"time={Campaign.Current.TimeControlMode}";
    }
#endif

    [CommandLineArgumentFunction("switch_menu", "coop.debug.ui")]
    public static string SwitchMenu(List<string> args)
    {
        if (ModInformation.IsServer)
            return "Run this command on a client.";

        if (args.Count != 1)
            return "Usage: coop.debug.ui.switch_menu <menuId>";

        if (Campaign.Current == null)
            return "Failed: no active campaign.";

        try
        {
            GameMenu.SwitchToMenu(args[0]);
        }
        catch (Exception ex)
        {
            return CommandHelpers.FormatException("Switch menu", ex);
        }

        return $"Switched to game menu {args[0]}.";
    }

    [CommandLineArgumentFunction("pop_state", "coop.debug.ui")]
    public static string PopState(List<string> args)
    {
        if (args.Count != 0)
            return "Usage: coop.debug.ui.pop_state";

        TaleWorlds.Core.GameState activeState = Game.Current?.GameStateManager?.ActiveState;
        if (activeState == null)
            return "Failed: no active game state.";

        if (activeState is MapState)
            return "Active state is already MapState.";

        Game.Current.GameStateManager.PopState();
        return $"Queued pop for {activeState.GetType().Name}.";
    }

    [CommandLineArgumentFunction("active_state", "coop.debug.ui")]
    public static string ActiveState(List<string> args)
    {
        if (args.Count != 0)
            return "Usage: coop.debug.ui.active_state";

        return Game.Current?.GameStateManager?.ActiveState?.GetType().Name ?? "none";
    }

    [CommandLineArgumentFunction("loading_window_state", "coop.debug.ui")]
    public static string LoadingWindowState(List<string> args)
    {
        if (args.Count != 0)
            return "Usage: coop.debug.ui.loading_window_state";

        return $"Loading window: {(LoadingWindow.IsLoadingWindowActive ? "ACTIVE" : "INACTIVE")}.";
    }

    [CommandLineArgumentFunction("saving_overlay_state", "coop.debug.ui")]
    public static string SavingOverlayState(List<string> args)
    {
        if (args.Count != 0)
            return "Usage: coop.debug.ui.saving_overlay_state";

        var dataSource = MapScreen.Instance?
            .GetMapView<GauntletMapSaveView>()?
            ._dataSource;
        if (dataSource == null)
            return "Saving overlay: UNAVAILABLE.";

        return $"Saving overlay: {(dataSource.IsActive ? "ACTIVE" : "INACTIVE")}.";
    }
}
