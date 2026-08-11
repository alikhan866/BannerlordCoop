using Common;
using System.Collections.Generic;
using System.Text;
using TaleWorlds.CampaignSystem;
using GameInterface.Services.MobileParties.Extensions;
using TaleWorlds.CampaignSystem.Party;
using static TaleWorlds.Library.CommandLineFunctionality;

namespace GameInterface.Services.MobileParties.Commands;

/// <summary>
/// Finds and releases parties whose AI was disabled and never re-enabled.
/// </summary>
/// <remarks>
/// <c>MobilePartyAi.DisableAi()</c> sets <c>_enableAgainAtHour = CampaignTime.Never</c>, so a held party's
/// tick returns early forever; only an explicit <c>EnableAi()</c> frees it, and the sole production release is
/// the engager's <c>PlayerEncounter.Finish</c>. When an encounter wedges - as one did when a client crashed
/// inside the leave consequence - that Finish never runs, and every party held at that moment is frozen for
/// good. <c>_isDisabled</c> is a SAVEABLE field, so the freeze is then written into the save and survives
/// reloading: the campaign looks alive but nothing moves.
///
/// Clearing <c>_isDisabled</c> alone is NOT enough, which cost a round trip to learn. The tick's real gate is
/// <c>_nextAiCheckTime</c>:
///
///     if (DefaultBehaviorNeedsUpdate) { _nextAiCheckTime = CampaignTime.Now; DefaultBehaviorNeedsUpdate = false; }
///     if (_nextAiCheckTime.IsFuture) return;
///     TickInternal();
///
/// and <c>EnableAi()</c> never touches it - it only clears <c>_isDisabled</c> and sets <c>_enableAgainAtHour</c>.
/// <c>_nextAiCheckTime</c> is saveable too, so a party released this way stays parked on a check time that
/// never arrives: AI "enabled", still motionless. Setting <c>DefaultBehaviorNeedsUpdate</c> is the engine's own
/// way of saying "re-decide now" - the tick then resets the check time itself, which is why this pokes the flag
/// rather than writing a time directly.
///
/// This is the manual release for a save already in that state. It is deliberately a report-first tool:
/// <c>list</c> shows what would be freed and changes nothing, <c>release</c> frees them.
///
/// A party still inside a live map event is left alone - its AI is disabled for a reason that is still true.
/// </remarks>
public static class StuckPartyAiCommands
{
    /// <summary>Reports every party whose AI is disabled, and whether this tool considers it releasable.</summary>
    [CommandLineArgumentFunction("list_stuck_ai", "coop.debug.mobileparty")]
    public static string ListStuckAi(List<string> arguments)
    {
        // Runs on either side ON PURPOSE. The disabled flag lives on each machine's own copy of a party, and
        // sync only carries CHANGES - so a server healed before a client connected leaves that client's copies
        // exactly as its save left them. Being unable to ask the client that question cost a whole diagnostic
        // round trip; this reads nothing but local state, so there is no reason to refuse it.
        var report = new StringBuilder();
        int disabled = 0, releasable = 0;

        foreach (var party in CollectDisabled())
        {
            disabled++;
            var held = IsLegitimatelyHeld(party);
            if (!held) releasable++;

            if (report.Length < 4000)
            {
                report.AppendLine($"{(held ? "HELD    " : "STUCK   ")} {party.StringId} ({party.Name}) " +
                    $"disabled={party.Ai.IsDisabled} nextCheckInFuture={party.Ai._nextAiCheckTime.IsFuture} " +
                    $"mapEvent={(party.MapEvent != null ? "yes" : "no")}");
            }
        }

        if (disabled == 0) return "No parties have their AI disabled.";

        return $"{disabled} party(ies) with AI disabled, {releasable} releasable:\n{report}";
    }

    /// <summary>Re-enables the AI of every party that is disabled with nothing left holding it.</summary>
    [CommandLineArgumentFunction("release_stuck_ai", "coop.debug.mobileparty")]
    public static string ReleaseStuckAi(List<string> arguments)
    {
        // Also allowed on a client, for the same reason: what it clears is that machine's own copy. On a
        // client this is a local nudge and nothing more - it publishes nothing, and the server stays the
        // authority on where parties actually go.
        int released = 0, skipped = 0;

        foreach (var party in CollectDisabled())
        {
            if (IsLegitimatelyHeld(party)) { skipped++; continue; }

            if (party.Ai.IsDisabled) party.Ai.EnableAi();

            // The part that actually unfreezes it: the tick resets _nextAiCheckTime to now when this is set.
            party.Ai.DefaultBehaviorNeedsUpdate = true;
            released++;
        }

        if (released == 0 && skipped == 0) return "No parties have their AI disabled; nothing to do.";

        var where = ModInformation.IsClient ? "locally on this client" : "on the server";
        return $"Released {released} party(ies) {where}; left {skipped} still inside a live map event." +
               (ModInformation.IsClient ? "" : " Save the world to keep this.");
    }

    /// <summary>
    /// One-time repair for a save whose ON-MAP parties kept their orders but stopped acting on them.
    /// </summary>
    /// <remarks>
    /// NOT a fix, and deliberately not part of normal behaviour - the bug that produced this state (a wedged
    /// encounter leaving a battle half-resolved) is fixed, so nothing should reach it again. This only rescues
    /// a campaign that already did.
    ///
    /// What the state actually is, measured on the live save rather than guessed: of the parties standing
    /// still out on the map, 75% were on <c>PatrolAroundPoint</c> (478 of 639) against 12% of the parties that
    /// were moving normally - and about half of the stalled ones reported <c>IsMoving == true</c> while their
    /// position did not change by so much as a thousandth over ten seconds. They are patrolling around a point
    /// they are already standing on, forever, which is why hovering one shows an order it never carries out.
    /// Parties that were inside a settlement when the battle wedged never took that order and are unaffected,
    /// which is exactly the split observed in play.
    ///
    /// So the repair clears the order rather than re-asking for a decision. An earlier version only set
    /// <c>DefaultBehaviorNeedsUpdate</c>, which let the AI re-decide and pick the same stale patrol anchor
    /// straight back - it changed nothing. <c>SetMoveModeHold</c> drops the behaviour and its target, and the
    /// flag then makes the party choose afresh on its next tick.
    ///
    /// Parties in a live map event are left alone: whatever holds them is still true.
    /// </remarks>
    [CommandLineArgumentFunction("repair_map_parties", "coop.debug.mobileparty")]
    public static string RepairMapParties(List<string> arguments)
    {
        int cleared = 0, skipped = 0, disorganizedCleared = 0, aiEnabled = 0;

        foreach (var party in MobileParty.All)
        {
            if (party?.Ai == null) continue;
            if (party.CurrentSettlement != null) continue;   // settlement parties are not the affected set
            if (party.MapEvent != null) { skipped++; continue; }
            if (party == MobileParty.MainParty) continue;    // never seize the player's own orders

            if (party.IsDisorganized)
            {
                party.SetDisorganized(false);
                disorganizedCleared++;
            }

            if (party.Ai.IsDisabled)
            {
                party.Ai.EnableAi();
                aiEnabled++;
            }

            // Drop the stale order AND its target, then make the party decide again from scratch.
            party.SetMoveModeHold();
            party.Ai.DefaultBehaviorNeedsUpdate = true;
            cleared++;
        }

        var where = ModInformation.IsClient ? "locally on this client" : "on the server";
        return $"Cleared stale orders on {cleared} on-map party(ies) {where} " +
               $"(disorganized cleared {disorganizedCleared}, AI re-enabled {aiEnabled}); " +
               $"left {skipped} in a live map event." +
               (ModInformation.IsClient ? "" : " Save the world to keep this.");
    }

    /// <summary>
    /// Reports how many on-map parties the server refuses to command because a player still claims them.
    /// </summary>
    /// <remarks>
    /// On the server, <c>IsControlledByThisInstance</c> is "nobody else claims it": a party registered to ANY
    /// player - including one who has since disconnected - is one the server will not set behaviour for, so
    /// PartyBehaviorPatch blocks its SetAiBehavior and its ShortTermBehavior stays None. Such a party keeps the
    /// DefaultBehavior it was last given and never acts on it, which is exactly what a stranded claim looks
    /// like from the map.
    /// </remarks>
    [CommandLineArgumentFunction("claim_report", "coop.debug.mobileparty")]
    public static string ClaimReport(List<string> arguments)
    {
        int onMap = 0, claimed = 0, claimedAndStill = 0;
        var examples = new List<string>();

        foreach (var party in MobileParty.All)
        {
            if (party?.Ai == null) continue;
            if (party.CurrentSettlement != null) continue;
            onMap++;

            if (party.IsControlledByThisInstance()) continue;

            claimed++;
            if (party.ShortTermBehavior == AiBehavior.None) claimedAndStill++;
            if (examples.Count < 5)
                examples.Add($"{party.StringId}({party.Name}) default={party.DefaultBehavior} short={party.ShortTermBehavior}");
        }

        return $"on-map parties {onMap}; claimed by a player (server will NOT command) {claimed}; " +
               $"of those with ShortTermBehavior=None {claimedAndStill}. " +
               (examples.Count > 0 ? "Examples: " + string.Join(" | ", examples) : "");
    }

    /// <summary>Every party whose AI cannot tick: disabled outright, or parked on a check time that never comes.</summary>
    private static List<MobileParty> CollectDisabled()
    {
        var frozen = new List<MobileParty>();
        foreach (var party in MobileParty.All)
        {
            if (party?.Ai == null) continue;
            if (!IsFrozen(party.Ai)) continue;

            frozen.Add(party);
        }

        return frozen;
    }

    private static bool IsFrozen(MobilePartyAi ai)
    {
        if (ai.IsDisabled) return true;

        // Enabled but unreachable: the next check is in the future and nothing has asked for a re-decision,
        // so the tick returns early every time.
        return ai._nextAiCheckTime.IsFuture && !ai.DefaultBehaviorNeedsUpdate;
    }

    /// <summary>Whether something still legitimately holds this party's AI down.</summary>
    private static bool IsLegitimatelyHeld(MobileParty party) => party.MapEvent != null;
}
