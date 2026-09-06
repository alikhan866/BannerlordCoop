#if DEBUG
using System;
using Common.Commands;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TaleWorlds.MountAndBlade;

namespace Missions.Agents.Handlers;

internal static class MovementDebugCommands
{
    private static CoopCommandResult Succeeded(string output) =>
        new CoopCommandResult(true, output);

    private static CoopCommandResult Failed(string output) =>
        new CoopCommandResult(false, output, "command_failed");

    public sealed class StateCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.movement";

        public string Name => "state";

        public string Description => "Reports state.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!TryGetHandler(out IAgentMovementHandler handler) ||
                !TryGetDebugControl(handler, out IAgentMovementDebugControl debugControl))
                return Failed("No active co-op mission movement handler.");

            MovementRateSnapshot state = handler.MovementRate;
            int activeHumans = Mission.Current?.Agents.Count(agent =>
                agent != null && agent.IsActive() && agent.IsHuman) ?? 0;
            int movingAgents = Mission.Current?.Agents.Count(agent =>
                agent != null &&
                agent.IsActive() &&
                agent.GetRealGlobalVelocity().AsVec2.LengthSquared > 0.01f) ?? 0;
            return Succeeded(string.Join("|", new[]
            {
                $"profile={state.Profile}",
                $"bulkHz={state.BulkHz}",
                $"priorityHz={state.PriorityHz}",
                $"frameLimitHz={state.FrameLimitHz}",
                $"performanceCeilingHz={state.PerformanceCeilingHz}",
                $"localAdaptiveHz={state.LocalAdaptiveHz}",
                $"receiverCapHz={state.AdvertisedReceiverCapHz}",
                $"peerCapHz={FormatNullable(state.PeerReceiverCapHz)}",
                $"peerCapSource={state.PeerReceiverCapSource ?? "none"}",
                $"forcedBulkHz={FormatNullable(state.ForcedBulkHz)}",
                $"forcedReceiverCapHz={FormatNullable(state.ForcedReceiverCapHz)}",
                $"activeAgents={state.ActiveAgents}",
                $"activeHumans={activeHumans}",
                $"movingAgents={movingAgents}",
                $"localAgents={state.LocallyControlledAgents}",
                $"controllers={state.Controllers}",
                $"fps={state.FramesPerSecond.ToString("0.0", CultureInfo.InvariantCulture)}",
                $"senderMsPerSecond={state.SenderMillisecondsPerSecond.ToString("0.00", CultureInfo.InvariantCulture)}",
                $"receiverApplyMsPerSecond={state.ReceiverApplyMillisecondsPerSecond.ToString("0.00", CultureInfo.InvariantCulture)}",
                $"receiverQueueMs={state.MaximumReceiverQueueMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)}",
                $"wireBytesPerSecond={state.WireBytesPerSecond}",
                $"configuredOutgoingBytesPerSecond={state.ConfiguredOutgoingBytesPerSecond}",
                $"availableOutgoingBytes={handler.AvailableOutgoingMovementBytes}",
                $"configuredIncomingBytesPerSecond={state.ConfiguredIncomingBytesPerSecond}",
                $"incomingBytesPerSender={state.AdvertisedIncomingBytesPerSender}",
                $"focusAgentId={state.FocusAgentId}",
                $"deferred={state.MaximumDeferredSnapshots}",
                $"deferredAge={state.MaximumDeferredAgeSeconds.ToString("0.000", CultureInfo.InvariantCulture)}",
                $"bulkPolls={state.BulkPollsPerSecond}",
                $"priorityOnlyPolls={state.PriorityOnlyPollsPerSecond}",
                $"initialConfiguredBulkHz={debugControl.InitialConfiguredBulkHz}",
                $"syntheticReceivePressureActive={debugControl.SyntheticReceivePressureActive.ToString().ToLowerInvariant()}",
                $"syntheticReceivePressureRemainingSeconds={debugControl.SyntheticReceivePressureRemainingSeconds.ToString("0.00", CultureInfo.InvariantCulture)}",
                $"reason={state.Reason}",
            }));
        }
    }

    public sealed class ForceRateCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.movement";

        public string Name => "force_rate";

        public string Description => "Runs the force rate debug operation.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("rate", "The rate.", true),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!TryParseRate(
                    args,
                    "rate",
                    out int? rate,
                    out string error))
                return Failed(error);
            if (!TryGetHandler(out IAgentMovementHandler handler))
                return Failed("No active co-op mission movement handler.");
            if (!handler.TrySetForcedBulkHz(rate, out error))
                return Failed(error);

            return Succeeded(rate.HasValue
                ? $"MOVEMENT_RATE_FORCED hz={rate.Value}"
                : "MOVEMENT_RATE_AUTOMATIC");
        }
    }

    public sealed class ForceReceiverCapCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.movement";

        public string Name => "force_receiver_cap";

        public string Description => "Runs the force receiver cap debug operation.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("receiver_cap", "The receiver cap.", true),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!TryParseRate(
                    args,
                    "receiver cap",
                    out int? rate,
                    out string error))
            {
                return Failed(error);
            }
            if (!TryGetHandler(out IAgentMovementHandler handler))
                return Failed("No active co-op mission movement handler.");
            if (!handler.TrySetForcedReceiverCapHz(rate, out error))
                return Failed(error);

            return Succeeded(rate.HasValue
                ? $"MOVEMENT_RECEIVER_CAP_FORCED hz={rate.Value}"
                : "MOVEMENT_RECEIVER_CAP_AUTOMATIC");
        }
    }

    public sealed class SimulateReceivePressureCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.movement";

        public string Name => "simulate_receive_pressure";

        public string Description => "Runs the simulate receive pressure debug operation.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("duration_seconds", "The duration seconds.", true),
            new ExpectedArgs("queue_ms", "The queue ms.", true),
            new ExpectedArgs("apply_ms", "The apply ms.", true),
            new ExpectedArgs("snapshots", "The snapshots.", true),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float durationSeconds) ||
                !double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double queueMilliseconds) ||
                !double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double applyMilliseconds) ||
                !int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int snapshots))
            {
                return Failed("Invalid command argument value.");
            }
            if (!TryGetHandler(out IAgentMovementHandler handler) ||
                !TryGetDebugControl(handler, out IAgentMovementDebugControl debugControl))
                return Failed("No active co-op mission movement handler.");
            if (!debugControl.TrySetSyntheticReceivePressure(
                    durationSeconds,
                    queueMilliseconds,
                    applyMilliseconds,
                    snapshots,
                    out string error))
            {
                return Failed(error);
            }

            return Succeeded(string.Join(" ", new[]
            {
                "MOVEMENT_RECEIVE_PRESSURE_ACTIVE",
                $"durationSeconds={durationSeconds.ToString("0.00", CultureInfo.InvariantCulture)}",
                $"queueMilliseconds={queueMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)}",
                $"applyMilliseconds={applyMilliseconds.ToString("0.00", CultureInfo.InvariantCulture)}",
                $"snapshots={snapshots}",
            }));
        }
    }

    public sealed class ClearReceivePressureCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.movement";

        public string Name => "clear_receive_pressure";

        public string Description => "Restores or clears clear receive pressure.";

        public IExpectedArgs[] ExpectedArgs { get; } = Array.Empty<IExpectedArgs>();

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!TryGetHandler(out IAgentMovementHandler handler) ||
                !TryGetDebugControl(handler, out IAgentMovementDebugControl debugControl))
                return Failed("No active co-op mission movement handler.");

            debugControl.ClearSyntheticReceivePressure();
            return Succeeded("MOVEMENT_RECEIVE_PRESSURE_CLEARED");
        }
    }

#if DEBUG
    // coop.debug.movement.simulate_latency <min_ms|off> [max_ms]
    /// <summary>Holds every mesh packet this client receives for min..max ms (LiteNetLib's simulation), or turns it off.</summary>
    public sealed class SimulateLatencyCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.movement";

        public string Name => "simulate_latency";

        public string Description => "Delays every received mesh packet by a random min..max ms on this client (PVP-SYNC-PLAN P8); 'off' restores normal delivery.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("min_ms", "Minimum one-way delay in ms, or 'off'.", true),
            new ExpectedArgs("max_ms", "Maximum one-way delay in ms (default min_ms + 20).", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!GameInterface.ContainerProvider.TryResolve<IBattleNetwork>(out IBattleNetwork network)
                || !(network is Missions.Services.Network.LiteNetP2PClient mesh))
                return Failed("No mesh client (join a battle first).");

            string first = args[0];
            if (string.Equals(first, "off", StringComparison.OrdinalIgnoreCase))
                return Succeeded(mesh.SetSimulatedLatency(false, 0, 0));

            if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minMs) || minMs < 0)
                return Failed("min_ms must be a non-negative integer or 'off'.");
            int maxMs = minMs + 20;
            string second = args.ElementAtOrDefault(1);
            if (!string.IsNullOrEmpty(second)
                && (!int.TryParse(second, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxMs) || maxMs < minMs))
                return Failed("max_ms must be an integer not below min_ms.");
            return Succeeded(mesh.SetSimulatedLatency(true, minMs, maxMs));
        }
    }

    // coop.debug.movement.frame_limit <fps|0>
    /// <summary>
    /// Caps this client's frame rate through the engine's own limiter (the video option), so a rig on one machine
    /// can fight a 60 fps client against a 40 fps one (PVP-SYNC-PLAN frame-rate question). 0 removes the cap.
    /// </summary>
    // coop.debug.movement.mounted_fixes <on|off> [lead|latch]
    /// <summary>Turns the mounted-sync fixes (mounted lead, usage latch) on or off at runtime for before/after rig runs.</summary>
    public sealed class MountedFixesCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.movement";

        public string Name => "mounted_fixes";

        public string Description => "Turns the mounted-sync fixes on or off on this client (both, or 'lead' / 'latch' alone) for before/after rig runs.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("mode", "on or off.", true),
            new ExpectedArgs("which", "lead (puppet horse lead), latch (usage flicker latch) or reassert (usage re-assert); default all.", false),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            bool on;
            if (string.Equals(args[0], "on", StringComparison.OrdinalIgnoreCase)) on = true;
            else if (string.Equals(args[0], "off", StringComparison.OrdinalIgnoreCase)) on = false;
            else return Failed("mode must be on or off.");
            string which = (args.ElementAtOrDefault(1) ?? "all").ToLowerInvariant();
            bool all = which == "all" || which == "both";
            if (which == "lead" || all) Missions.Agents.MountedSyncSwitches.LeadEnabled = on;
            if (which == "latch" || all) Missions.Agents.MountedSyncSwitches.UsageLatchEnabled = on;
            if (which == "reassert" || all) Missions.Agents.MountedSyncSwitches.UsageReassertEnabled = on;
            if (which != "lead" && which != "latch" && which != "reassert" && !all) return Failed("which must be lead, latch, reassert or omitted.");
            return Succeeded("MOUNTED_FIXES lead=" + (Missions.Agents.MountedSyncSwitches.LeadEnabled ? "on" : "off") +
                             " latch=" + (Missions.Agents.MountedSyncSwitches.UsageLatchEnabled ? "on" : "off") +
                             " reassert=" + (Missions.Agents.MountedSyncSwitches.UsageReassertEnabled ? "on" : "off"));
        }
    }

    public sealed class FrameLimitCoopCommand : ICoopCommand
    {
        public string Prefix => "coop.debug.movement";

        public string Name => "frame_limit";

        public string Description => "Sets the engine frame limiter of this client to the given fps (0 = uncapped); the movement rate controller reads the same setting.";

        public IExpectedArgs[] ExpectedArgs { get; } = new IExpectedArgs[]
        {
            new ExpectedArgs("fps", "Frame cap in frames per second, or 0 for none.", true),
        };

        public CoopCommandResult ProcessCommand(ICoopCommandArgs args)
        {
            if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps) || fps < 0 || fps > 1000)
                return Failed("fps must be an integer between 0 and 1000.");
            if (TaleWorlds.Engine.EngineApplicationInterface.IConfig == null)
                return Failed("No graphics config on this process (headless).");
            float before = TaleWorlds.Engine.Options.NativeOptions.GetConfig(TaleWorlds.Engine.Options.NativeOptions.NativeOptionsType.FrameLimiter);
            TaleWorlds.Engine.Options.NativeOptions.SetConfig(TaleWorlds.Engine.Options.NativeOptions.NativeOptionsType.FrameLimiter, fps);
            TaleWorlds.Engine.Options.NativeOptions.ApplyConfigChanges(false);
            float after = TaleWorlds.Engine.Options.NativeOptions.GetConfig(TaleWorlds.Engine.Options.NativeOptions.NativeOptionsType.FrameLimiter);
            return Succeeded("FRAME_LIMIT was=" + before.ToString("0", CultureInfo.InvariantCulture) +
                             " now=" + after.ToString("0", CultureInfo.InvariantCulture) +
                             " fps=" + TaleWorlds.Engine.Utilities.GetFps().ToString("0", CultureInfo.InvariantCulture));
        }
    }
#endif

    private static bool TryGetHandler(out IAgentMovementHandler handler)
    {
        handler = Mission.Current?
            .GetMissionBehavior<CoopMissionController>()?
            .AgentMovementHandler;
        return handler != null;
    }

    private static bool TryGetDebugControl(
        IAgentMovementHandler handler,
        out IAgentMovementDebugControl debugControl)
    {
        debugControl = handler as IAgentMovementDebugControl;
        return debugControl != null;
    }

    private static bool TryParseRate(
        IReadOnlyList<string> args,
        string valueName,
        out int? rate,
        out string error)
    {
        rate = null;
        if (args[0].ToLowerInvariant() == "auto")
        {
            error = null;
            return true;
        }
        if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            error = $"Invalid {valueName}. Use auto, 10, 15, 20, 30, 40, or 60.";
            return false;
        }

        rate = parsed;
        error = null;
        return true;
    }

    private static string FormatNullable(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "none";
}
#endif
