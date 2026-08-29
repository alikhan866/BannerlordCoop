using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;

namespace GameInterface.Services.Headless.Patches;

/// <summary>
/// Stops a headless client from dying while creating its campaign, in vanilla's patrol-party spawn.
/// </summary>
/// <remarks>
/// WHAT KILLED IT
/// A headless client has no save to load, so it CREATES a campaign and then receives the authoritative world
/// from the server. Vanilla's new-game creation spawns a patrol party per eligible settlement, and that path
/// threw before the campaign finished loading:
///
///   DefaultSettlementPatrolModel.GetPartyTemplateForPatrolParty
///     &lt;- PatrolPartiesCampaignBehavior.SpawnPatrolParty
///     &lt;- PatrolPartiesCampaignBehavior.OnNewGameCreated
///
/// The process exited with the campaign half-built, which is why every Group C capability was unreachable: the
/// client never got as far as having a campaign, let alone a mission.
///
/// WHY SKIPPING IS THE RIGHT ANSWER, NOT A WORKAROUND
/// DisablePatrolPartiesCampaignBehavior already turns off nine of this behaviour's entry points off the
/// server, because patrol parties are server-authoritative in coop - a client that spawns its own is
/// generating world state nobody asked for, which the join baseline then has to overwrite.
/// <c>OnNewGameCreated</c> was simply not in that list. It is the same rule, applied to the one entry point
/// that was missed.
///
/// The guard is deliberately narrower than the existing one. That patch keys off <c>IsServer</c>, which is set
/// when a SESSION starts and is therefore still false for everyone during new-game creation; widening it would
/// also stop a server that starts a fresh campaign from ever spawning patrols. Keying off the headless-client
/// flag, which is set at boot from the launch arguments, changes behaviour for exactly the process that was
/// broken and for nothing else.
///
/// THE FINALIZER STAYS AFTER THE FIX
/// It reports which link of the chain was null rather than swallowing the throw silently, so if this reappears
/// on a role that SHOULD be spawning patrols, the next person gets the answer instead of a stack trace ending
/// in a property getter. It only converts the exception into a skipped settlement - it never invents a
/// template, because a made-up patrol roster would be worse than an absent one.
/// </remarks>
[HarmonyPatch]
internal static class HeadlessPatrolSpawnGuardPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger(typeof(HeadlessPatrolSpawnGuardPatch));

    private static bool reportedSkip;

    [HarmonyPatch(typeof(PatrolPartiesCampaignBehavior), nameof(PatrolPartiesCampaignBehavior.OnNewGameCreated))]
    [HarmonyPrefix]
    private static bool SkipNewGamePatrolSpawn()
    {
        if (!ModInformation.IsHeadlessClient) return true;

        // Once, not once per settlement. Vanilla calls this per eligible settlement, so the unguarded version
        // wrote several hundred identical lines into the boot log and buried the failure after it.
        if (!reportedSkip)
        {
            reportedSkip = true;
            Logger.Information(
                "[Headless] skipping vanilla patrol-party creation for a joining client - patrols are " +
                "server-authoritative and arrive with the join baseline");
        }

        return false;
    }

    [HarmonyPatch(typeof(DefaultSettlementPatrolModel),
        nameof(DefaultSettlementPatrolModel.GetPartyTemplateForPatrolParty))]
    [HarmonyFinalizer]
    private static Exception ReportMissingPatrolTemplate(Exception __exception, Settlement settlement, bool naval)
    {
        if (__exception == null) return null;

        var culture = Read(() => settlement?.Culture);
        Logger.Warning(
            __exception,
            "[Patrol] no patrol template for {Settlement}: culture={Culture} isTown={IsTown} " +
            "isCastle={IsCastle} isVillage={IsVillage} hasTown={HasTown} naval={Naval} " +
            "weak={Weak} moderate={Moderate} strong={Strong} navalTemplate={NavalTemplate}",
            Read(() => settlement?.StringId) ?? "<null settlement>",
            Read(() => culture?.StringId) ?? "<null culture>",
            Read(() => settlement?.IsTown ?? false),
            Read(() => settlement?.IsCastle ?? false),
            Read(() => settlement?.IsVillage ?? false),
            Read(() => settlement?.Town != null),
            naval,
            Read(() => culture?.SettlementPatrolPartyTemplateWeak?.StringId) ?? "<null>",
            Read(() => culture?.SettlementPatrolPartyTemplateModerate?.StringId) ?? "<null>",
            Read(() => culture?.SettlementPatrolPartyTemplateStrong?.StringId) ?? "<null>",
            Read(() => culture?.SettlementPatrolPartyTemplateNaval?.StringId) ?? "<null>");

        // Swallowed so one settlement without a template cannot take the whole campaign load with it. The
        // caller handles a null template as "no patrol here", which is the honest outcome.
        return null;
    }

    private static T Read<T>(Func<T> read)
    {
        try { return read(); }
        catch { return default; }
    }
}
