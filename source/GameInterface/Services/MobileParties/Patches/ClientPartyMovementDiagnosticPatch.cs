using Common;
using Common.Logging;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace GameInterface.Services.MobileParties.Patches;

/// <summary>
/// TODO(remove): temporary client-side diagnostic for "every party on the map is frozen".
/// </summary>
/// <remarks>
/// The server side of this is already measured and healthy - 95 to 159 behaviour updates and 9 to 12 hourly
/// ticks per ten seconds, with the campaign clock running. So the parties that look frozen are the CLIENT's
/// copies, and the state that would explain it lives only on that machine: whether its parties tick at all,
/// whether their AI is disabled, and whether they have anywhere to go.
///
/// Written as a self-reporting sample rather than a console command because a console command has to be typed
/// mid-test, which is not something to ask of someone trying to reproduce a bug.
///
/// Every ten seconds it reports how many parties actually MOVED since the last sample, alongside how many are
/// AI-disabled and how many hold a behaviour. Movement is the number that matters: behaviours arriving while
/// nothing moves means the client is being told where to go and not going.
/// </remarks>
[HarmonyPatch(typeof(Campaign), nameof(Campaign.Tick))]
internal class ClientPartyMovementDiagnosticPatch
{
    private static readonly ILogger Logger = LogManager.GetLogger<ClientPartyMovementDiagnosticPatch>();

    private static readonly Dictionary<MobileParty, Vec2> lastSeen = new Dictionary<MobileParty, Vec2>();
    private static DateTime nextReport = DateTime.MinValue;

    [HarmonyPostfix]
    private static void Postfix()
    {
        // Runs on BOTH sides now. The whole night was spent assuming the server's parties were moving and the
        // client was failing to show it; that was never measured, because this only ever ran on a client.
        if (Campaign.Current == null) return;

        var now = DateTime.UtcNow;
        if (now < nextReport) return;
        nextReport = now.AddSeconds(10);

        try
        {
            int total = 0, moved = 0, disabled = 0, withBehaviour = 0, firstSample = 0;

            // The population that matters: told to go somewhere, not going. Their speed says whether they are
            // crawling (a speed problem) or genuinely parked (a target/decision problem).
            int stalled = 0, stalledZeroSpeed = 0;
            float stalledSpeedSum = 0f;
            var examples = new List<string>();

            // The whole question is what separates the parties that move from the ones that do not. Counting
            // each candidate flag across BOTH groups answers it in one sample: a flag that is near-universal
            // among the stalled and absent among the movers is the cause, and anything present in both is not.
            // Behaviour histograms, because "why is it not moving" is answered by what it was told to do.
            // AiBehavior.Hold means stand still; None means no orders at all. Measured with the public
            // DefaultBehavior/ShortTermBehavior/IsMoving members - an earlier attempt keyed off _fleeingData,
            // which is allocated in the constructor and therefore never null, so it measured nothing.
            var stallDefault = new Dictionary<AiBehavior, int>();
            var moveDefault = new Dictionary<AiBehavior, int>();
            var stallShort = new Dictionary<AiBehavior, int>();
            int stallIsMoving = 0, moveIsMoving = 0, stallCount = 0, moveCount = 0;

            foreach (var party in MobileParty.All)
            {
                if (party?.Ai == null) continue;
                total++;

                if (party.Ai.IsDisabled) disabled++;
                if (party.Ai.AiBehaviorPartyBase != null || party.Ai.AiBehaviorInteractable != null) withBehaviour++;

                var position = party.GetPosition2D;
                if (!lastSeen.TryGetValue(party, out var previous))
                {
                    lastSeen[party] = position;
                    firstSample++;
                    continue;
                }

                var distance = previous.Distance(position);
                var isMoving = distance > 0.001f;
                if (isMoving) moved++;
                lastSeen[party] = position;

                if (party.CurrentSettlement == null)
                {
                    if (isMoving)
                    {
                        moveCount++;
                        if (party.IsMoving) moveIsMoving++;
                        Bump(moveDefault, party.DefaultBehavior);
                    }
                    else
                    {
                        stallCount++;
                        if (party.IsMoving) stallIsMoving++;
                        Bump(stallDefault, party.DefaultBehavior);
                        Bump(stallShort, party.ShortTermBehavior);
                    }
                }

                // Only parties OUT on the map can be judged: one sitting in a settlement is meant to be still,
                // and counting those made the first sample read "689 stalled" when nothing was wrong with them.
                var hasTarget = party.Ai.AiBehaviorPartyBase != null || party.Ai.AiBehaviorInteractable != null;
                if (hasTarget && distance <= 0.001f && party.CurrentSettlement == null)
                {
                    stalled++;
                    var speed = party.Speed;
                    stalledSpeedSum += speed;
                    if (speed <= 0.0001f) stalledZeroSpeed++;

                    if (examples.Count < 5)
                    {
                        examples.Add($"{party.StringId}({party.Name}) speed={speed:0.###} " +
                            $"disorganized={party.IsDisorganized} " +
                            $"target={party.Ai.AiBehaviorPartyBase?.Name?.ToString() ?? party.Ai.AiBehaviorInteractable?.ToString() ?? "-"}");
                    }
                }
            }

            Logger.Warning(
                "[PartyDiag] side={Side} parties={Total} moved={Moved} aiDisabled={Disabled} withBehaviour={Behaviour} " +
                "stalled={Stalled} stalledAtZeroSpeed={Zero} avgStalledSpeed={AvgSpeed:0.###} timeMode={Mode}",
                ModInformation.IsServer ? "SERVER" : "client",
                total, moved, disabled, withBehaviour, stalled, stalledZeroSpeed,
                stalled > 0 ? stalledSpeedSum / stalled : 0f, Campaign.Current.TimeControlMode);

            if (examples.Count > 0)
                Logger.Warning("[PartyDiag] stalled examples: {Examples}", string.Join(" | ", examples));

            Logger.Warning(
                "[PartyDiag] side={Side2} STALLED n={SC} IsMoving={SM} default=[{SD}] shortTerm=[{SS}] || MOVING n={MC} IsMoving={MM} default=[{MD}]",
                ModInformation.IsServer ? "SERVER" : "client",
                stallCount, stallIsMoving, Describe(stallDefault), Describe(stallShort),
                moveCount, moveIsMoving, Describe(moveDefault));
        }
        catch (Exception e)
        {
            Logger.Warning(e, "[PartyDiag] sample failed");
        }
    }

    private static void Bump(Dictionary<AiBehavior, int> counts, AiBehavior behavior)
    {
        counts.TryGetValue(behavior, out var current);
        counts[behavior] = current + 1;
    }

    /// <summary>The three commonest behaviours, which is enough to see what a group was told to do.</summary>
    private static string Describe(Dictionary<AiBehavior, int> counts)
    {
        var parts = new List<string>();
        foreach (var pair in counts) parts.Add($"{pair.Key}={pair.Value}");
        parts.Sort((a, b) => string.CompareOrdinal(b.Substring(b.IndexOf('=') + 1).PadLeft(6),
                                                   a.Substring(a.IndexOf('=') + 1).PadLeft(6)));
        return string.Join(",", parts.GetRange(0, Math.Min(3, parts.Count)));
    }
}
