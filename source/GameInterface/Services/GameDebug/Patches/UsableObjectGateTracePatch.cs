using Common.Logging;
using HarmonyLib;
using Serilog;
using System.Collections.Generic;
using static TaleWorlds.Library.CommandLineFunctionality;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.GameDebug.Patches;

/// <summary>
/// Names whoever switches a standing point off, by logging the first few deactivations with their stack.
/// </summary>
/// <remarks>
/// Diagnostic only. Written after five separate explanations for "one client gets no interaction prompts"
/// were each argued from state and each turned out to be wrong: the two clients agree on side, team, machine
/// count, standing-point count and main-agent control, and differ ONLY in that 1457 of 1484 points are
/// deactivated on one of them and 140 on the other - across every kind of usable object, ambient chairs
/// included. Nothing in this codebase deactivates a standing point, so the caller is vanilla's.
///
/// Reading the state cannot say who wrote it, which is why every previous attempt turned into a guess. This
/// records the writer instead.
///
/// A postfix reading <c>IsDeactivated</c> rather than a prefix reading the argument, deliberately: the two
/// candidate entry points name their parameter differently, and Harmony injects by NAME - one shared prefix
/// would silently fail to bind on one of them. Reading the resulting state afterwards works for both.
///
/// Hard-capped: a siege holds ~1484 points and this must not turn a diagnostic into the reason the log is
/// unreadable, or the reason the frame budget is gone.
///
/// Lives in GameInterface, NOT in Missions, and that placement is load-bearing: PatchAll only scans
/// <c>typeof(GameInterface).Assembly</c> for uncategorised patches, so the same class sitting in Missions is
/// compiled, shipped, and never applied - producing exactly the silence that reads as "the event never
/// happened". Console commands are found by a different scan that covers every assembly, which is why a
/// diagnostic COMMAND works from Missions while a patch does not.
/// </remarks>
[HarmonyPatch]
internal static class UsableObjectGateTracePatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(UsableObjectGateTracePatch));

    /// <summary>
    /// Stop aggregating after this many calls, so a long battle cannot make the probe the bottleneck.
    /// </summary>
    private const int MaxAggregated = 4000;

    private static int aggregated;

    /// <summary>caller signature + resulting flags -> how many times it happened</summary>
    private static readonly ConcurrentDictionary<string, int> Callers = new ConcurrentDictionary<string, int>();

    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Declared on UsableMissionObject, NOT StandingPoint - confirmed by reflecting the shipped assembly
        // rather than assumed. Patching via the derived type is what made the first attempt's silence
        // uninterpretable: nothing bound, and nothing logged, and those look identical from the outside.
        //
        // IsDisabledForPlayers is included because it is a SEPARATE gate - an object can be live for AI and
        // closed to players - and it is named for precisely the symptom being chased.
        var targets = new List<MethodBase>();
        var owner = typeof(UsableMissionObject);

        foreach (string name in new[] { "IsDeactivated", "IsDisabledForPlayers" })
        {
            PropertyInfo property = AccessTools.Property(owner, name);
            if (property?.SetMethod != null) targets.Add(property.SetMethod);
        }
        foreach (string name in new[] { "SetIsDeactivatedSynched", "SetIsDisabledForPlayersSynched" })
        {
            MethodInfo method = AccessTools.Method(owner, name);
            if (method != null) targets.Add(method);
        }

        // Say what was bound. Silence from a diagnostic must never be ambiguous between "it did not happen"
        // and "the probe was never attached" - that ambiguity cost a whole capture cycle.
        Logger.Warning("[PointDeac] tracer bound to {Count} method(s): {Names}",
            targets.Count,
            string.Join(",", targets.ConvertAll(t => t.DeclaringType?.Name + "." + t.Name)));
        return targets;
    }

    private static void Postfix(UsableMissionObject __instance)
    {
        if (__instance == null || Mission.Current == null) return;

        // Scene initialisation, before any agent exists, is not the event being chased - and there is a lot
        // of it. A fixed log budget was spent entirely on PatrolArea.OnInit noise before the battle began,
        // which is why the previous capture looked identical on both clients and said nothing.
        if (Mission.Current.Agents.Count == 0) return;
        if (Volatile.Read(ref aggregated) >= MaxAggregated) return;
        Interlocked.Increment(ref aggregated);

        // Aggregate by CALLER rather than logging each event. Counts survive any volume, and the difference
        // between two clients shows up as a different caller or a wildly different count - both of which a
        // capped list of individual stacks can hide.
        string caller = "unknown";
        try
        {
            var stack = new StackTrace(1, false);
            var frames = new List<string>();
            for (int i = 0; i < stack.FrameCount && frames.Count < 3; i++)
            {
                MethodBase method = stack.GetFrame(i)?.GetMethod();
                if (method?.DeclaringType == null) continue;
                if (method.DeclaringType == typeof(UsableObjectGateTracePatch)) continue;
                frames.Add(method.DeclaringType.Name + "." + method.Name);
            }
            if (frames.Count > 0) caller = string.Join(" <- ", frames);
        }
        catch { caller = "<stack threw>"; }

        string key = $"{caller} || type={__instance.GetType().Name} deac={__instance.IsDeactivated} disPlayers={__instance.IsDisabledForPlayers}";
        Callers.AddOrUpdate(key, 1, (_, count) => count + 1);
    }

    /// <summary>
    /// Dumps who has been opening and closing usable objects, busiest first.
    /// </summary>
    [CommandLineArgumentFunction("gate_trace", "coop.debug.mission")]
    public static string GateTrace(List<string> args)
    {
        bool reset = args != null && args.Count == 1 &&
                     string.Equals(args[0], "reset", System.StringComparison.OrdinalIgnoreCase);
        if (args != null && args.Count > 1) return "Usage: coop.debug.mission.gate_trace [reset]";

        if (reset)
        {
            Callers.Clear();
            Volatile.Write(ref aggregated, 0);
            return "GATE_TRACE reset";
        }

        var rows = new List<KeyValuePair<string, int>>(Callers);
        rows.Sort((left, right) => right.Value.CompareTo(left.Value));

        var report = new System.Text.StringBuilder();
        report.AppendLine($"GATE_TRACE events={aggregated} distinct={rows.Count} (cap {MaxAggregated})");
        for (int i = 0; i < rows.Count && i < 12; i++)
            report.AppendLine($"  {rows[i].Value,6}x  {rows[i].Key}");
        return report.ToString();
    }
}
