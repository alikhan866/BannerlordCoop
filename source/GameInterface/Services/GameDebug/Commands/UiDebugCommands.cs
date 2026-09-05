using Common.Commands;
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
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using static TaleWorlds.Library.CommandLineFunctionality;
using System.Linq;

namespace GameInterface.Services.GameDebug.Commands;

/// <summary>
/// [Debug] UI / screen commands. <c>coop.debug.ui.close_screen</c> forces the current game menu to exit
/// (<see cref="GameMenu.ExitToLast"/>) — a manual escape for when a post-battle encounter screen is left open.
/// </summary>
internal class UiDebugCommands
{
    private static CoopCommandResult Succeeded(string output) =>
        new CoopCommandResult(true, output);

    private static CoopCommandResult Failed(string output) =>
        new CoopCommandResult(false, output, "command_failed");

    public static readonly ILogger Logger = LogManager.GetLogger<UiDebugCommands>();

    public sealed class UiCloseScreenCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "close_screen";

        public string Description => "Runs the close screen debug operation.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (Campaign.Current == null)
                return Failed("Failed: no active campaign.");

            try
            {
                GameMenu.ExitToLast();
            }
            catch (Exception ex)
            {
                return Failed(CommandHelpers.FormatException("Close screen", ex));
            }

            return Succeeded("Called GameMenu.ExitToLast().");
        }
    }

    public sealed class UiPrepareEvidenceMapCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "prepare_evidence_map";

        public string Description => "Runs the prepare evidence map debug operation.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (ModInformation.IsServer)
                return Failed("Run this command on a client.");


            MapScreen mapScreen = MapScreen.Instance;
            if (mapScreen == null)
                return Failed("Campaign map screen is unavailable.");

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
                return Failed(CommandHelpers.FormatException("Prepare evidence map", ex));
            }

            return Succeeded(GetEvidenceMapState(mapScreen));
        }
    }

    public sealed class UiEvidenceMapStateCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "evidence_map_state";

        public string Description => "Reports evidence map state.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (ModInformation.IsServer)
                return Failed("Run this command on a client.");


            MapScreen mapScreen = MapScreen.Instance;
            if (mapScreen == null)
                return Failed("Campaign map screen is unavailable.");

            return Succeeded(GetEvidenceMapState(mapScreen));
        }
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

    public sealed class UiLeaveSettlementEncounterCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "leave_settlement_encounter";

        public string Description => "Runs the leave settlement encounter debug operation.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (ModInformation.IsServer)
                return Failed("Run this command on a client.");


            if (Campaign.Current == null)
                return Failed("Failed: no active campaign.");

            var mainParty = MobileParty.MainParty;
            if (mainParty == null)
                return Failed("Failed: no main party.");

            if (PlayerEncounter.Battle != null || mainParty.MapEvent != null)
                return Failed("Cannot leave the settlement encounter after a battle has started.");

            if (PlayerEncounter.Current == null || PlayerEncounter.EncounterSettlement == null)
                return Failed("No active settlement encounter to leave.");

            if (!ContainerProvider.TryResolve<ISettlementInterface>(out var settlementInterface))
                return Failed("Unable to resolve the settlement interface.");

            try
            {
                using (new AllowedThread())
                    settlementInterface.EndSettlementEncounter();
            }
            catch (Exception ex)
            {
                return Failed(CommandHelpers.FormatException("Leave settlement encounter", ex));
            }

            return Succeeded("Cleared the local settlement encounter and returned to the campaign map.");
        }
    }

#if DEBUG
    public sealed class UiMapClickOffsetCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "map_click_offset";

        public string Description => "Runs the map click offset debug operation.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("offset_x", "The horizontal map offset.", isRequired: true),
            new ExpectedArgs("offset_y", "The vertical map offset.", isRequired: true),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (ModInformation.IsServer)
                return Failed("Run this command on a client.");
            if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetX) ||
                !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetY))
                return Failed("Offsets must be valid numbers.");

            var mapScreen = MapScreen.Instance;
            var mainParty = MobileParty.MainParty;
            if (mapScreen == null || mainParty == null)
                return Failed("Failed: campaign map or main party is unavailable.");
            if (PlayerEncounter.Current != null || mainParty.CurrentSettlement != null)
                return Failed("Leave the active settlement encounter before clicking the campaign map.");
            if (mainParty.MapEvent != null)
                return Failed("Cannot click-to-move while the main party is in a map event.");

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
                return Failed("No nearby navigable map-click target was found.");

            mapScreen.HandleLeftMouseButtonClick(null, target, target.Face, false);

            return Succeeded($"Issued a real campaign-map click from {current.X:R},{current.Y:R} " +
                $"to {target.X:R},{target.Y:R}; time={Campaign.Current.TimeControlMode}; " +
                $"behavior={mainParty.DefaultBehavior}; target={mainParty.TargetPosition.X:R},{mainParty.TargetPosition.Y:R}.");
        }
    }

    public sealed class UiMapMovementStateCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "map_movement_state";

        public string Description => "Reports map movement state.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (ModInformation.IsServer)
                return Failed("Run this command on a client.");

            var mainParty = MobileParty.MainParty;
            if (mainParty == null || Campaign.Current == null)
                return Failed("Failed: no active campaign or main party.");

            return Succeeded($"position={mainParty.Position.X:R},{mainParty.Position.Y:R}|" +
                $"target={mainParty.TargetPosition.X:R},{mainParty.TargetPosition.Y:R}|" +
                $"behavior={mainParty.DefaultBehavior}|" +
                $"settlement={mainParty.CurrentSettlement?.StringId ?? "none"}|" +
                $"encounter={PlayerEncounter.EncounterSettlement?.StringId ?? "none"}|" +
                $"time={Campaign.Current.TimeControlMode}");
        }
    }
#endif

    public sealed class UiSwitchMenuCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "switch_menu";

        public string Description => "Runs the switch menu debug operation.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("menu_id", "The game menu id.", isRequired: true),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (ModInformation.IsServer)
                return Failed("Run this command on a client.");


            if (Campaign.Current == null)
                return Failed("Failed: no active campaign.");

            try
            {
                GameMenu.SwitchToMenu(args[0]);
            }
            catch (Exception ex)
            {
                return Failed(CommandHelpers.FormatException("Switch menu", ex));
            }

            return Succeeded($"Switched to game menu {args[0]}.");
        }
    }

    public sealed class UiPopStateCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "pop_state";

        public string Description => "Reports pop state.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {

            TaleWorlds.Core.GameState activeState = Game.Current?.GameStateManager?.ActiveState;
            if (activeState == null)
                return Failed("Failed: no active game state.");

            if (activeState is MapState)
                return Failed("Active state is already MapState.");

            Game.Current.GameStateManager.PopState();
            return Succeeded($"Queued pop for {activeState.GetType().Name}.");
        }
    }

    public sealed class UiActiveStateCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "active_state";

        public string Description => "Reports active state.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {

            return Succeeded(Game.Current?.GameStateManager?.ActiveState?.GetType().Name ?? "none");
        }
    }

    public sealed class UiLoadingWindowStateCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "loading_window_state";

        public string Description => "Reports loading window state.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {

            return Succeeded($"Loading window: {(LoadingWindow.IsLoadingWindowActive ? "ACTIVE" : "INACTIVE")}.");
        }
    }

    public sealed class UiSavingOverlayStateCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "saving_overlay_state";

        public string Description => "Reports saving overlay state.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {

            var dataSource = MapScreen.Instance?
                .GetMapView<GauntletMapSaveView>()?
                ._dataSource;
            if (dataSource == null)
                return Failed("Saving overlay: UNAVAILABLE.");

            return Succeeded($"Saving overlay: {(dataSource.IsActive ? "ACTIVE" : "INACTIVE")}.");
        }
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
    public sealed class RaiseTestPopupCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "raise_test_popup";

        public string Description => "Raises a real popup through the engine's own entry point, to prove the capture intercepts it.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("kind", "inquiry (default), quick or message.", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            string kind = (args.ElementAtOrDefault(0) ?? "inquiry").ToLowerInvariant();

            switch (kind)
            {
                case "inquiry":
                    InformationManager.ShowInquiry(new InquiryData(
                        "Rig self-test",
                        "Raised by coop.debug.ui.raise_test_popup to prove popup capture.",
                        true, true,
                        "Accept", "Decline",
                        null, null));
                    return Succeeded("Raised an inquiry. Check coop.debug.ui.popup_log.");

                case "quick":
                    MBInformationManager.AddQuickInformation(
                        new TaleWorlds.Localization.TextObject("Rig self-test quick information."));
                    return Succeeded("Raised a quick information message. Check coop.debug.ui.popup_log.");

                case "message":
                    InformationManager.DisplayMessage(
                        new InformationMessage("Rig self-test campaign message."));
                    return Succeeded("Raised a campaign message. Check coop.debug.ui.message_log.");

                default:
                    return Failed("Usage: coop.debug.ui.raise_test_popup [inquiry|quick|message]");
            }
        }
    }

    /// <summary>C24 - arm or inspect the server's refusal of irreversible actions.</summary>
    /// <remarks>
    /// Deliberately reachable while armed: the guard denies irreversible ACTS, not the ability to see what it
    /// has refused. A guard whose own status command was blocked would be indistinguishable from a broken one.
    /// </remarks>
    public sealed class TestClientGuardCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.testclient";

        public string Name => "guard";

        public string Description => "Arms, disarms, clears or reports the server's refusal of irreversible actions.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("action", "arm, disarm or clear; omit to print the guard state and its refusals.", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            string action = args.ElementAtOrDefault(0);
            if (string.Equals(action, "arm", StringComparison.OrdinalIgnoreCase))
            {
                Services.Headless.IrreversibleActionGuard.Arm(true);
                return Succeeded("Irreversible-action refusal ARMED.");
            }
            if (string.Equals(action, "disarm", StringComparison.OrdinalIgnoreCase))
            {
                Services.Headless.IrreversibleActionGuard.Arm(false);
                return Succeeded("Irreversible-action refusal disarmed.");
            }
            if (string.Equals(action, "clear", StringComparison.OrdinalIgnoreCase))
                return Succeeded($"Cleared {Services.Headless.IrreversibleActionGuard.ClearRefusals()} refusal(s).");
            if (action != null)
                return Failed("Usage: coop.debug.testclient.guard [arm|disarm|clear]");

            return Succeeded(GuardReport());
        }
    }

    /// <summary>The guard's state and every refusal it has recorded, one per line.</summary>
    private static string GuardReport()
    {
        var refusals = Services.Headless.IrreversibleActionGuard.RefusalLog;
        var report = new System.Text.StringBuilder();
        report.AppendLine($"GUARD armed={Services.Headless.IrreversibleActionGuard.IsArmed} " +
                          $"denied={Services.Headless.IrreversibleActionGuard.DeniedPatterns.Count} " +
                          $"refusals={refusals.Count}");
        foreach (var refusal in refusals) report.AppendLine($"  REFUSED {refusal}");
        return report.ToString().TrimEnd();
    }

    /// <summary>C23 - declare how a named popup should be answered.</summary>
    public sealed class PopupDeclareCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "popup_declare";

        public string Description => "Declares how a popup matching a pattern should be answered.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("answer", "affirmative or negative, or clear to drop every declaration."),
            new ExpectedArgs("pattern", "The popup pattern to answer. Quote multi-word patterns.", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (string.Equals(args[0], "clear", StringComparison.OrdinalIgnoreCase))
                return Succeeded($"Cleared {Services.Headless.PopupPolicy.ClearDeclarations()} declaration(s).");

            string pattern = args.ElementAtOrDefault(1);
            if (string.IsNullOrEmpty(pattern))
                return Failed("Usage: coop.debug.ui.popup_declare <affirmative|negative> <pattern>  |  popup_declare clear");

            Services.Headless.PopupPolicy.Answer answer;
            switch (args[0].ToLowerInvariant())
            {
                case "affirmative": answer = Services.Headless.PopupPolicy.Answer.Affirmative; break;
                case "negative": answer = Services.Headless.PopupPolicy.Answer.Negative; break;
                default: return Failed("The answer must be 'affirmative' or 'negative'. There is no accept-all.");
            }

            Services.Headless.PopupPolicy.Declare(pattern, answer);
            return Succeeded($"Declared '{pattern}' -> {answer}.");
        }
    }

    /// <summary>C23 - the declarations in force, and whether the run has already failed.</summary>
    public sealed class PopupPolicyCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "popup_policy";

        public string Description => "Reports the popup declarations in force and whether the run has failed.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("action", "reset_failures to clear recorded failures; omit to print the policy state.", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            string action = args.ElementAtOrDefault(0);
            if (string.Equals(action, "reset_failures", StringComparison.OrdinalIgnoreCase))
                return Succeeded($"Cleared {Services.Headless.PopupPolicy.ClearFailures()} failure(s).");
            if (action != null)
                return Failed("Usage: coop.debug.ui.popup_policy [reset_failures]");

            return Succeeded(PopupPolicyReport());
        }
    }

    /// <summary>The popup declarations in force, whether the run has failed, and why.</summary>
    private static string PopupPolicyReport()
    {
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
    public sealed class MessageLogCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "message_log";

        public string Description => "Prints, clears or exports the campaign messages this process captured.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("mode", "clear, json, or the number of messages to print (default 30).", false),
            new ExpectedArgs("count", "With json: how many messages to include (default 200).", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            string mode = args.ElementAtOrDefault(0);
            string countArgument = args.ElementAtOrDefault(1);

            if (string.Equals(mode, "clear", StringComparison.OrdinalIgnoreCase))
                return Succeeded($"Cleared {Services.Headless.PopupCapture.ClearMessages()} message(s).");

            var captured = Services.Headless.PopupCapture.MessageSnapshot();

            if (string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase))
            {
                int jsonWanted = countArgument != null && int.TryParse(countArgument, out var parsed) ? parsed : 200;
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
                return Succeeded("LIVE_TEST_JSON=" + json.Append("]}"));
            }

            int wanted = 30;
            if (countArgument != null || (mode != null && !int.TryParse(mode, out wanted)))
                return Failed("Usage: coop.debug.ui.message_log [clear|json [count]|<count>]");

            if (captured.Count == 0) return Succeeded("MESSAGE_LOG count=0");
            var report = new System.Text.StringBuilder();
            report.AppendLine($"MESSAGE_LOG count={captured.Count}");
            foreach (var message in captured.Skip(Math.Max(0, captured.Count - wanted)))
                report.AppendLine($"#{message.Sequence} utc={message.RealTimeUtc:HH:mm:ss} " +
                                  $"campaign='{message.CampaignTime}' text='{message.Text}'");
            return Succeeded(report.ToString().TrimEnd());
        }
    }

    /// <summary>C22 - the popups this process could not show.</summary>
    public sealed class PopupLogCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "popup_log";

        public string Description => "Prints, clears or exports the popups this process captured.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("mode", "clear, json, or the number of popups to print (default 25).", false),
            new ExpectedArgs("count", "With json: how many popups to include (default 200).", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            string mode = args.ElementAtOrDefault(0);
            string countArgument = args.ElementAtOrDefault(1);

            if (string.Equals(mode, "clear", StringComparison.OrdinalIgnoreCase))
                return Succeeded($"Cleared {Services.Headless.PopupCapture.Clear()} captured popup(s).");

            // A machine-readable form, because C25 asserts on these and parsing the prose below would break on
            // any popup whose own text contained a quote or a bracket - which player-facing text eventually does.
            if (string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase))
                return Succeeded(PopupLogJson(countArgument != null && int.TryParse(countArgument, out var jsonCount) ? jsonCount : 200));

            int wanted = 25;
            if (countArgument != null || (mode != null && !int.TryParse(mode, out wanted)))
                return Failed("Usage: coop.debug.ui.popup_log [clear|json [count]|<count>]");

            var captured = Services.Headless.PopupCapture.Snapshot();
            if (captured.Count == 0) return Succeeded("POPUP_LOG count=0");

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
            return Succeeded(report.ToString().TrimEnd());
        }
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
    public sealed class MenuOptionsCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.ui";

        public string Name => "menu_options";

        public string Description => "Lists the current game menu's options with the result of each one's condition.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("format", "json for a LIVE_TEST_JSON line; omit for the readable report.", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            string format = args.ElementAtOrDefault(0);
            bool asJson = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase);
            if (format != null && !asJson)
                return Failed("Usage: coop.debug.ui.menu_options [json]");

            var menuContext = Campaign.Current?.CurrentMenuContext;
            if (menuContext == null)
                return Succeeded(asJson ? "LIVE_TEST_JSON={\"menuOpen\":false}" : "No game menu is open.");

            string menuId = menuContext.GameMenu?.StringId ?? "unknown";
            var options = FindMenuOptions(menuContext);

            // C26 - the reachability report, which must describe an EMPTY menu as clearly as a full one. On a
            // render-free client the context is never activated, so a real menu legitimately offers nothing; a
            // command that only printed options would show the same blank as a command that had crashed.
            if (asJson) return Succeeded(MenuJson(menuContext, menuId, options));

            if (options == null)
            {
                // Self-describing on failure. Reporting only "not found" would cost a build and a campaign load
                // per guess at the field name, which is the loop this command was written to avoid.
                return Failed($"menu={menuId} contextState={ReadMember(menuContext, "_currentState") ?? "?"}: " +
                       $"no option list found. Candidates:\n" + DescribeCandidates(menuContext));
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
            return Succeeded(report.ToString().TrimEnd());
        }
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
}
