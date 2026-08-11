using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.ObjectSystem;

namespace GameInterface.Services.Headless.Patches
{
    /// <summary>
    /// Names the individual campaign object being restored, for when the load dies inside one of them.
    /// </summary>
    /// <remarks>
    /// CampaignObjectManager.PreAfterLoad walks every hero, clan and party in the save. When the process
    /// dies somewhere in that walk there is no stack and no exception, so the only way to know which object
    /// and which step did it is for each to say so first: the last line written is the one that failed.
    ///
    /// Prefix only, deliberately - a matching "done" for every object would double an already large log for
    /// no extra information, since it is the absence of the NEXT line that identifies the culprit.
    ///
    /// Off unless <see cref="HeadlessLoadTracePatches"/>-style tracing is wanted; see TraceObjects.
    /// </remarks>
    [HarmonyPatch]
    public class HeadlessObjectLoadTracePatches
    {
        private static readonly ILogger Logger = LogManager.GetLogger<HeadlessObjectLoadTracePatches>();

        /// <summary>
        /// Per-object tracing is noisy - thousands of lines for one load - so it is opt-in, via
        /// /cooptraceload. Left in because a headless load that dies inside one party cannot be diagnosed
        /// any other way, and the next such failure should not need this file written again.
        /// </summary>
        public static bool TraceObjects { get; set; }

        private static readonly (Type Type, string Method)[] TracedMethods =
        {
            (typeof(MobileParty), "PreAfterLoad"),
            (typeof(MobileParty), "ComputePathAfterLoad"),
            (typeof(MobilePartyAi), "PreAfterLoad"),
            (typeof(Hero), "PreAfterLoad"),
            (typeof(Clan), "PreAfterLoad"),
            (typeof(Kingdom), "PreAfterLoad"),
            (typeof(Settlement), "PreAfterLoad"),
            (typeof(Town), "PreAfterLoad"),
            (typeof(Clan), "UpdateBannerColorsAccordingToKingdom"),
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var traced in TracedMethods)
            {
                var method = AccessTools.Method(traced.Type, traced.Method);
                if (method != null) yield return method;
            }
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance, MethodBase __originalMethod)
        {
            if (!ModInformation.IsHeadless || !TraceObjects) return;

            Logger.Information("[Headless] load object: {Type}.{Step} {Id}{Detail}",
                __originalMethod.DeclaringType?.Name,
                __originalMethod.Name,
                (__instance as MBObjectBase)?.StringId ?? "<n/a>",
                Describe(__instance));
        }

        /// <summary>
        /// A "done" line only for clans. Clan.PreAfterLoad is where a headless load stops, and knowing
        /// whether it returned separates "this clan killed it" from "the next object did".
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix(object __instance, MethodBase __originalMethod)
        {
            if (!ModInformation.IsHeadless || !TraceObjects) return;
            if (!(__instance is Clan clan)) return;

            Logger.Information("[Headless] load object: Clan.{Step} {Id} done", __originalMethod.Name, clan.StringId);
        }

        /// <summary>
        /// Reports a managed exception escaping any traced step, which a native crash never produces. It
        /// answers the one question the logs otherwise cannot: whether the process is being killed by the
        /// engine, or by an ordinary exception nobody is printing. The exception is passed through
        /// unchanged - this only observes.
        /// </summary>
        [HarmonyFinalizer]
        private static void Finalizer(object __instance, MethodBase __originalMethod, Exception __exception)
        {
            if (!ModInformation.IsHeadless || __exception == null) return;

            Logger.Error(__exception, "[Headless] {Type}.{Step} threw on {Id}",
                __originalMethod.DeclaringType?.Name,
                __originalMethod.Name,
                (__instance as MBObjectBase)?.StringId ?? "<n/a>");
        }

        /// <summary>
        /// The clan state the surviving branches of Clan.PreAfterLoad actually key on - above all whether
        /// it has heroes, because an empty clan is destroyed outright.
        /// </summary>
        private static string Describe(object instance)
        {
            if (!(instance is Clan clan)) return string.Empty;

            return $" heroes={clan.Heroes?.Count ?? -1} leader={clan.Leader?.StringId ?? "<none>"}" +
                   $" eliminated={clan.IsEliminated} bandit={clan.IsBanditFaction}" +
                   $" kingdom={clan.Kingdom?.StringId ?? "<none>"}";
        }
    }
}
