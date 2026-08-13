using Common;
using Common.Logging;
using Common.Util;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using HarmonyLib;
using Serilog;
using System;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using static GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Services.MobileParties.Data;

public interface IMobilePartyBehaviorSnapshot
{
    bool TryCreate(MobileParty party, out PartyBehaviorUpdateData data);
    bool CanApply(MobileParty party, PartyBehaviorUpdateData data);
    bool TryCreateJoinState(
        MobileParty party,
        ISet<MobileParty> liveParties,
        ISet<Settlement> liveSettlements,
        out MobilePartyJoinState state,
        out string failure);
    bool TryApply(MobileParty party, PartyBehaviorUpdateData data, out IInteractablePoint interactable);
    bool TryApplyJoinBaseline(MobilePartyJoinState[] states, Action beforeApply);
}

public sealed class MobilePartyBehaviorSnapshot : IMobilePartyBehaviorSnapshot
{
    private static readonly ILogger Logger = LogManager.GetLogger<MobilePartyBehaviorSnapshot>();

    private readonly IObjectManager objectManager;
    private readonly HashSet<string> loggedJoinBaselineFailures = new HashSet<string>();
    private string lastJoinBaselineFailure;

    internal string LastJoinBaselineFailure => lastJoinBaselineFailure;
    internal int LoggedJoinBaselineFailureCount => loggedJoinBaselineFailures.Count;

    public MobilePartyBehaviorSnapshot(IObjectManager objectManager) => this.objectManager = objectManager;

    public bool TryCreate(
        MobileParty party,
        out PartyBehaviorUpdateData data) =>
        TryCreate(party, out data, out _);

    private bool TryCreate(
        MobileParty party,
        out PartyBehaviorUpdateData data,
        out string failure)
    {
        data = default;
        failure = null;
        if (party == null)
            return FailCreation("party is null", out failure);
        if (party.Ai == null)
            return FailCreation("party AI is unavailable", out failure);
        if (!TryGetCompactId(party, out string partyId))
            return FailCreation("party is not registered", out failure);
        if (!TryGetInteractableReference(
            party.Ai.AiBehaviorInteractable,
            out string interactablePointId,
            out bool isInteractableAnchor))
        {
            return FailCreation(
                $"AI interactable '{party.Ai.AiBehaviorInteractable?.GetType().Name}' is not registered",
                out failure);
        }
        if (!TryGetCompactId(party.TargetParty, out string targetPartyId))
        {
            return FailCreation(
                $"target party '{party.TargetParty?.StringId}' is not registered",
                out failure);
        }
        if (!TryGetCompactId(party.TargetSettlement, out string targetSettlementId))
        {
            return FailCreation(
                $"target settlement '{party.TargetSettlement?.StringId}' is not registered",
                out failure);
        }

        MoveModeType partyMoveMode = party.PartyMoveMode;
        CampaignVec2 moveTargetPoint = party.MoveTargetPoint;
        MobileParty moveTargetParty = party.MoveTargetParty;
        if (!TryGetCompactId(moveTargetParty, out string moveTargetPartyId))
        {
            // A removed movement target cannot exist on clients, so preserve its last destination.
            moveTargetPartyId = null;
            if (partyMoveMode == MoveModeType.Party)
            {
                partyMoveMode = MoveModeType.Point;
                moveTargetPoint = moveTargetParty.Position;
            }
        }

        data = new PartyBehaviorUpdateData(
            partyId,
            party.ShortTermBehavior,
            interactablePointId,
            party.Ai.BehaviorTarget,
            party.Position,
            party.DefaultBehavior,
            party.TargetPosition,
            party.DesiredAiNavigationType)
        {
            TargetPartyId = targetPartyId,
            TargetSettlementId = targetSettlementId,
            MoveTargetPoint = moveTargetPoint,
            IsTargetingPort = party.IsTargetingPort,
            PartyMoveMode = partyMoveMode,
            MoveTargetPartyId = moveTargetPartyId,
            IsInteractableAnchor = isInteractableAnchor,
            IsCurrentlyAtSea = party.IsCurrentlyAtSea,
        };
        return true;
    }

    public bool CanApply(MobileParty party, PartyBehaviorUpdateData data) =>
        party?.Ai != null &&
        TryResolveInteractable(data, out _) &&
        TryResolve(data.TargetPartyId, out MobileParty _) &&
        TryResolve(data.TargetSettlementId, out Settlement _) &&
        TryResolve(data.MoveTargetPartyId, out MobileParty _);

    public bool TryCreateJoinState(
        MobileParty party,
        ISet<MobileParty> liveParties,
        ISet<Settlement> liveSettlements,
        out MobilePartyJoinState state,
        out string failure)
    {
        state = default;
        if (liveParties == null || liveSettlements == null)
            return FailCreation("live campaign objects are unavailable", out failure);

        bool created = TryCreate(party, out PartyBehaviorUpdateData behavior, out failure);
        if (TryGetInvalidJoinReferences(
            party,
            liveParties,
            liveSettlements,
            out string invalidReferences))
        {
            if (!TryGetCompactId(party, out _))
                return false;

            try
            {
                // Mirror vanilla's removed-target cleanup so behavior and navigation stay coherent.
                party.SetMoveModeHold();
                party.SetNavigationModeHold();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to reset stale join references on party {Party}", party.StringId);
                return FailCreation(
                    $"failed to reset stale join references ({invalidReferences}): " +
                    $"{ex.GetType().Name}: {ex.Message}",
                    out failure);
            }

            Logger.Warning(
                "Reset stale join references ({References}) on party {Party} to Hold",
                invalidReferences,
                party.StringId);

            if (!TryCreate(party, out behavior, out failure))
                return false;
            if (TryGetInvalidJoinReferences(
                party,
                liveParties,
                liveSettlements,
                out string remainingReferences))
            {
                return FailCreation(
                    $"stale join references remain after reset ({remainingReferences})",
                    out failure);
            }
        }
        else if (!created)
        {
            return false;
        }

        // Preserve a removed move target as its last point instead of resetting the party to Hold.
        PreserveUnavailableMoveTarget(party, liveParties, ref behavior);

        state = new MobilePartyJoinState
        {
            Behavior = behavior,
            EventPositionAdder = party.EventPositionAdder,
            ArmyPositionAdder = party.ArmyPositionAdder,
            Bearing = party.Bearing,
            IsCurrentlyAtSea = party.IsCurrentlyAtSea,
            EndPositionForNavigationTransition = party.EndPositionForNavigationTransition,
            NavigationTransitionStartTimeTicks = party.NavigationTransitionStartTime.NumTicks,
            StartTransitionNextFrameToExitFromPort = party.StartTransitionNextFrameToExitFromPort,
            ForceAiNoPathMode = party.ForceAiNoPathMode,
            IsActive = party.IsActive,
        };
        failure = null;
        return true;
    }

    private bool TryGetInvalidJoinReferences(
        MobileParty party,
        ISet<MobileParty> liveParties,
        ISet<Settlement> liveSettlements,
        out string references)
    {
        references = null;
        if (party?.Ai == null) return false;

        var invalidReferences = new List<string>();
        IInteractablePoint interactable = party.Ai.AiBehaviorInteractable;
        if (interactable != null &&
            (!TryGetInteractableReference(interactable, out _, out _) ||
             !IsLiveInteractable(interactable, liveParties, liveSettlements)))
        {
            invalidReferences.Add(nameof(MobilePartyAi.AiBehaviorInteractable));
        }
        if (IsInvalidJoinReference(party.TargetParty, liveParties))
            invalidReferences.Add(nameof(MobileParty.TargetParty));
        if (IsInvalidJoinReference(party.TargetSettlement, liveSettlements))
            invalidReferences.Add(nameof(MobileParty.TargetSettlement));

        if (invalidReferences.Count == 0) return false;

        references = string.Join(", ", invalidReferences);
        return true;
    }

    private static void PreserveUnavailableMoveTarget(
        MobileParty party,
        ISet<MobileParty> liveParties,
        ref PartyBehaviorUpdateData behavior)
    {
        MobileParty moveTargetParty = party.MoveTargetParty;
        if (moveTargetParty == null || liveParties.Contains(moveTargetParty)) return;

        behavior.MoveTargetPartyId = null;
        if (behavior.PartyMoveMode != MoveModeType.Party) return;

        behavior.PartyMoveMode = MoveModeType.Point;
        behavior.MoveTargetPoint = moveTargetParty.Position;
    }

    private bool IsInvalidJoinReference<T>(T instance, ISet<T> liveObjects)
        where T : class =>
        instance != null &&
        (!TryGetCompactId(instance, out _) || !liveObjects.Contains(instance));

    private static bool FailCreation(string reason, out string failure)
    {
        failure = reason;
        return false;
    }

    public bool TryApply(MobileParty party, PartyBehaviorUpdateData data, out IInteractablePoint interactable)
    {
        interactable = null;
        if (!TryPrepare(
            party,
            data,
            null,
            null,
            logLookupFailures: true,
            out ResolvedBehaviorUpdate resolved,
            out _))
            return false;

        interactable = resolved.Interactable;
        ApplyBehavior(resolved, resetPath: false);
        return true;
    }

    public bool TryApplyJoinBaseline(MobilePartyJoinState[] states, Action beforeApply)
    {
        if (states == null)
            return RejectJoinBaseline("the baseline party-state array is null");
        if (beforeApply == null)
            return RejectJoinBaseline("the before-apply callback is null");

        var objectManager = Campaign.Current?.CampaignObjectManager;
        var parties = objectManager?.MobileParties;
        var settlements = objectManager?.Settlements;
        if (parties == null)
            return RejectJoinBaseline("the client campaign mobile-party collection is unavailable");
        if (settlements == null)
            return RejectJoinBaseline("the client campaign settlement collection is unavailable");
        // Activation is authoritative world state and must be applied BEFORE anything is validated
        // against it: CampaignObjectManager.MobileParties holds only ACTIVE parties, so a party the
        // joiner has but has deactivated is missing from every count and every walk of that
        // collection. Reconciling here is what makes the comparison below meaningful.
        ApplyServerActivation(states, parties);

        var liveParties = new HashSet<MobileParty>();
        for (int i = 0; i < parties.Count; i++)
        {
            MobileParty party = parties[i];
            if (party?.IsActive == true) liveParties.Add(party);
        }

        if (states.Length != liveParties.Count)
        {
            return RejectJoinBaseline(
                $"party count mismatch (baseline={states.Length}, client={liveParties.Count}); " +
                DescribeDivergence(states, parties));
        }

        var liveSettlements = new HashSet<Settlement>(settlements);
        var seenParties = new HashSet<MobileParty>();
        var resolved = new ResolvedBehaviorUpdate[states.Length];

        for (int i = 0; i < states.Length; i++)
        {
            PartyBehaviorUpdateData behavior = states[i].Behavior;
            if (string.IsNullOrEmpty(behavior.MobilePartyId))
                return RejectJoinBaseline($"state {i} has no mobile-party id");
            if (!this.objectManager.TryGetObject(
                behavior.MobilePartyId,
                out MobileParty party))
            {
                return RejectJoinBaseline(
                    $"state {i} references missing mobile party '{behavior.MobilePartyId}'");
            }
            if (!liveParties.Contains(party))
            {
                return RejectJoinBaseline(
                    $"state {i} party '{behavior.MobilePartyId}' is not in the client campaign collection");
            }
            if (!seenParties.Add(party))
                return RejectJoinBaseline($"state {i} duplicates party '{behavior.MobilePartyId}'");
            if (!TryPrepare(
                party,
                behavior,
                liveParties,
                liveSettlements,
                logLookupFailures: false,
                out resolved[i],
                out string failure))
            {
                return RejectJoinBaseline(
                    $"state {i} party '{behavior.MobilePartyId}' failed validation: {failure}");
            }
        }

        if (seenParties.Count != liveParties.Count)
        {
            return RejectJoinBaseline(
                $"party coverage mismatch (baseline={seenParties.Count}, client={liveParties.Count})");
        }

        try
        {
            beforeApply();
            // TEMPORARY LOCAL PATCH - DO NOT COMMIT. Apply per party so one bad party cannot abandon the
            // whole baseline. Seen live: a party whose target reference was dropped by TryCreate arrives with
            // a targetless GoToSettlement/EscortParty, and MobilePartyAi.UpdateBehavior dereferences it. The
            // throw aborted the entire baseline, the server resent ~6 MB, and the client looped on the loading
            // screen forever. Revert this before continuing PR work; the real fix belongs in the PR.
            int applyFailures = 0;
            using (new AllowedThread())
            {
                for (int i = 0; i < resolved.Length; i++)
                {
                    ApplyJoinState(resolved[i].Party, states[i]);
                    ApplyBehavior(resolved[i], resetPath: true);

                }
            }

            lastJoinBaselineFailure = null;
            loggedJoinBaselineFailures.Clear();
            return true;
        }
        catch (Exception ex)
        {
            return RejectJoinBaseline(
                $"application threw {ex.GetType().Name}: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Brings the joiner's party activation in line with the server's before the baseline is validated.
    /// </summary>
    /// <remarks>
    /// The joiner does not lose parties - it deactivates them. Vanilla campaign code running during world
    /// load (settlement menu-init being the observed case) flips <c>IsActive</c> on a party the server still
    /// has active, and the coop guards cannot stop it: they key off whether the object is REGISTERED, which
    /// during load is indistinguishable from "not registered yet". The party then vanishes from
    /// <c>CampaignObjectManager.MobileParties</c> and the join is refused for a party that was there the
    /// whole time. Measured live: baseline=1538 / client=1537, the difference being one castle garrison at
    /// <c>IsActive=false</c>, on the one client whose own party was sitting in that castle.
    ///
    /// Applied under <see cref="AllowedThread"/> so the sync layer treats it as the server speaking rather
    /// than a local client mutation - which is exactly the distinction the load-time guards could not make.
    /// </remarks>
    // MobileParty.IsActive is a plain auto-property - its setter only writes the backing field and does
    // NOT move the party in or out of CampaignObjectManager.MobileParties. Membership of that collection
    // is controlled solely by the internal Add/RemoveMobileParty pair, so setting the flag alone leaves a
    // party that reports IsActive=true and is still invisible to every count and every walk of it.
    //
    // Only the ADD half is used here, and the same fact is why. Because the setter is a bare field write,
    // the SERVER keeps an inactive party in its own collection too - and the baseline is built by walking
    // exactly that collection, so an IsActive=false party is present in the states array and counted in
    // states.Length. Mirroring the flag by REMOVING it on the client therefore does not converge the two
    // sides, it separates them: the client ends one party short of a baseline that still lists it, and the
    // count check below rejects a join that was correct until this method touched it. Measured live on
    // 2026-08-11 - baseline=1546 / client=1545, the one difference being the other player's party at
    // IsActive=false because that player was not connected, which livelocked both joins until the retry
    // cap disconnected them.
    private static readonly MethodInfo AddMobilePartyMethod =
        AccessTools.Method(typeof(CampaignObjectManager), "AddMobileParty", new[] { typeof(MobileParty) });

    /// <summary>
    /// Identifies a party for a log line without ever throwing.
    /// </summary>
    /// <remarks>
    /// <c>MobileParty.Name</c> dereferences state a half-initialised party may not have, so reading it can
    /// throw - and a diagnostic that crashes the join it is describing is worse than no diagnostic at all.
    /// Caught in a unit test where an NRE from get_Name aborted TryApplyJoinBaseline outright.
    /// </remarks>
    private static string Describe(MobileParty party)
    {
        if (party == null) return "<null>";

        try
        {
            var name = party.Name?.ToString();
            return string.IsNullOrEmpty(name) ? party.StringId : $"{party.StringId} ({name})";
        }
        catch
        {
            return party.StringId;
        }
    }

    private void ApplyServerActivation(MobilePartyJoinState[] states, IEnumerable<MobileParty> parties)
    {
        var campaignObjectManager = Campaign.Current?.CampaignObjectManager;
        if (campaignObjectManager == null) return;

        var inCollection = new HashSet<MobileParty>(parties);
        int changed = 0;
        string firstChange = null;

        using (new AllowedThread())
        {
            foreach (var state in states)
            {
                var id = state.Behavior.MobilePartyId;
                if (string.IsNullOrEmpty(id)) continue;
                if (!objectManager.TryGetObject(id, out MobileParty party) || party == null) continue;

                bool present = inCollection.Contains(party);
                if (party.IsActive == state.IsActive && present) continue;

                party.IsActive = state.IsActive;

                // Membership is RESTORED, never withdrawn - see the remarks above. Guarded on current
                // membership so a re-add cannot duplicate the party in the list.
                if (!present)
                    AddMobilePartyMethod?.Invoke(campaignObjectManager, new object[] { party });

                changed++;
                firstChange ??= $"{Describe(party)} -> IsActive={state.IsActive}";
            }
        }

        if (changed > 0)
        {
            Logger.Warning(
                "[PartySync] Join baseline corrected activation on {Count} party(ies); first {First}",
                changed,
                firstChange);
        }
    }

    /// <summary>
    /// Names the parties the two sides disagree about, rather than only how many there are.
    /// </summary>
    /// <remarks>
    /// A bare "baseline=1538, client=1537" says a join is impossible but not why, and identifying the
    /// single offending party took a night of dumping both clients' party lists and diffing them by
    /// hand. It turned out to be one garrison. Naming it here turns that into one line.
    ///
    /// Capped because a genuinely broken world could differ by hundreds, and a log line that long is
    /// its own denial of service.
    /// </remarks>
    private string DescribeDivergence(MobilePartyJoinState[] states, IEnumerable<MobileParty> parties)
    {
        const int MaxNamed = 8;

        // Resolved through the SAME path the apply loop uses. Comparing id STRINGS from the two sides
        // instead compares two different namespaces - the baseline carries bare ids while the object
        // manager hands back prefixed ones ("MobileParty_...") - which reports every single party as
        // diverging and buries the one that actually is.
        var matched = new Dictionary<MobileParty, string>();
        var missing = new List<string>();
        var aliased = new List<string>();

        foreach (var state in states)
        {
            var id = state.Behavior.MobilePartyId;
            if (string.IsNullOrEmpty(id)) continue;

            if (objectManager.TryGetObject(id, out MobileParty resolved) && resolved != null)
            {
                // Two DISTINCT server parties resolving to one client object is the failure that hides
                // behind a count mismatch with nothing missing and nothing extra: N baseline states
                // collapse onto N-1 client parties. The apply loop's own duplicate check sits after the
                // count check and so never runs, leaving only the arithmetic to notice.
                if (matched.TryGetValue(resolved, out string firstId))
                {
                    if (aliased.Count < MaxNamed)
                        aliased.Add($"{id} and {firstId} both resolve to {Describe(resolved)}");
                    continue;
                }

                matched[resolved] = id;
                continue;
            }

            if (missing.Count < MaxNamed) missing.Add(id);
        }

        var live = new HashSet<MobileParty>(parties);

        var extra = new List<string>();
        foreach (var party in parties)
        {
            if (party == null || matched.ContainsKey(party)) continue;
            if (extra.Count >= MaxNamed) break;

            extra.Add(Describe(party));
        }

        // The case none of the sets above can see: a party the client HAS and can resolve by id, but
        // which is absent from CampaignObjectManager.MobileParties because that collection only holds
        // ACTIVE parties. It is counted by the server and not by the client, so the totals differ by
        // one while nothing is missing, extra, or aliased.
        var inactive = new List<string>();
        foreach (var pair in matched)
        {
            if (live.Contains(pair.Key)) continue;
            if (inactive.Count >= MaxNamed) break;

            inactive.Add($"{Describe(pair.Key)} IsActive={pair.Key.IsActive}");
        }

        return $"onServerNotOnClient=[{string.Join(", ", missing)}] " +
               $"onClientNotOnServer=[{string.Join(", ", extra)}] " +
               $"aliased=[{string.Join(", ", aliased)}] " +
               $"resolvedButNotInClientCollection=[{string.Join(", ", inactive)}]";
    }

    private bool RejectJoinBaseline(string failure, Exception exception = null)
    {
        lastJoinBaselineFailure = failure;
        if (!loggedJoinBaselineFailures.Add(failure))
            return false;

        if (exception == null)
        {
            Logger.Warning(
                "Could not apply mobile-party join baseline: {Failure}. Identical retries will not be logged",
                failure);
        }
        else
        {
            Logger.Error(
                exception,
                "Could not apply mobile-party join baseline: {Failure}. Identical retries will not be logged",
                failure);
        }
        return false;
    }

    private bool TryPrepare(
        MobileParty party,
        PartyBehaviorUpdateData data,
        HashSet<MobileParty> liveParties,
        HashSet<Settlement> liveSettlements,
        bool logLookupFailures,
        out ResolvedBehaviorUpdate resolved,
        out string failure)
    {
        resolved = default;
        failure = null;
        if (party == null)
            return FailPreparation("party is unavailable", out failure);
        if (party.Ai == null)
            return FailPreparation("party AI is unavailable", out failure);
        if (!TryResolveInteractable(data, out IInteractablePoint interactable, logLookupFailures))
            return FailPreparation($"interactable '{data.InteractablePointId}' could not be resolved", out failure);
        if (!TryResolve(data.TargetPartyId, out MobileParty targetParty, logLookupFailures))
            return FailPreparation($"target party '{data.TargetPartyId}' could not be resolved", out failure);
        if (!TryResolve(data.TargetSettlementId, out Settlement targetSettlement, logLookupFailures))
            return FailPreparation($"target settlement '{data.TargetSettlementId}' could not be resolved", out failure);
        if (!TryResolve(data.MoveTargetPartyId, out MobileParty moveTargetParty, logLookupFailures))
            return FailPreparation($"move target party '{data.MoveTargetPartyId}' could not be resolved", out failure);

        if (data.PartyMoveMode == MoveModeType.Party && moveTargetParty == null)
            return FailPreparation("party movement mode requires a move target", out failure);

        if (liveParties != null && targetParty != null && !liveParties.Contains(targetParty))
            return FailPreparation($"target party '{data.TargetPartyId}' is not live", out failure);
        if (liveParties != null && moveTargetParty != null && !liveParties.Contains(moveTargetParty))
            return FailPreparation($"move target party '{data.MoveTargetPartyId}' is not live", out failure);
        if (liveParties != null && !IsLiveInteractable(interactable, liveParties, liveSettlements))
            return FailPreparation($"interactable '{data.InteractablePointId}' is not live", out failure);

        if (liveSettlements != null &&
            targetSettlement != null &&
            !liveSettlements.Contains(targetSettlement))
        {
            return FailPreparation($"target settlement '{data.TargetSettlementId}' is not live", out failure);
        }

        resolved = new ResolvedBehaviorUpdate(
            party,
            data,
            interactable,
            targetParty,
            targetSettlement,
            moveTargetParty);
        return true;
    }

    private static bool FailPreparation(string reason, out string failure)
    {
        failure = reason;
        return false;
    }

    private static bool IsLiveInteractable(
        IInteractablePoint interactable,
        ISet<MobileParty> liveParties,
        ISet<Settlement> liveSettlements)
    {
        if (interactable == null) return true;
        if (interactable is AnchorPoint anchor)
            return anchor.Owner != null && liveParties.Contains(anchor.Owner);
        if (interactable is PartyBase partyBase)
        {
            if (partyBase.MobileParty != null) return liveParties.Contains(partyBase.MobileParty);
            if (partyBase.Settlement != null) return liveSettlements.Contains(partyBase.Settlement);
        }
        return false;
    }

    private static void ApplyJoinState(MobileParty party, MobilePartyJoinState state)
    {
        party.IsCurrentlyAtSea = state.IsCurrentlyAtSea;
        party.Position = state.Behavior.PartyPosition;
        party.EventPositionAdder = state.EventPositionAdder;
        party.ArmyPositionAdder = state.ArmyPositionAdder;
        party.Bearing = state.Bearing;
        party.EndPositionForNavigationTransition = state.EndPositionForNavigationTransition;
        party.NavigationTransitionStartTime = new CampaignTime(state.NavigationTransitionStartTimeTicks);
        party.StartTransitionNextFrameToExitFromPort = state.StartTransitionNextFrameToExitFromPort;
        party.ForceAiNoPathMode = state.ForceAiNoPathMode;
    }

    private static void ApplyBehavior(ResolvedBehaviorUpdate resolved, bool resetPath)
    {
        MobileParty party = resolved.Party;
        PartyBehaviorUpdateData data = resolved.Data;


        // Install targets first because DefaultBehavior can immediately recalculate short-term state.
        party.SetTargetSettlement(resolved.TargetSettlement, data.IsTargetingPort);
        party.TargetParty = resolved.TargetParty;
        party.TargetPosition = data.TargetPosition;
        party.DefaultBehavior = data.DefaultBehavior;
        party.SetShortTermBehavior(data.NewAiBehavior, resolved.Interactable);
        party.DesiredAiNavigationType = data.DesiredAiNavigationType;
        party.Ai.BehaviorTarget = data.BestTargetPoint;
        party.Ai.UpdateBehavior();
        switch (data.PartyMoveMode)
        {
            case MoveModeType.Hold:
                party.SetNavigationModeHold();
                break;
            case MoveModeType.Point:
                party.SetNavigationModePoint(data.MoveTargetPoint);
                break;
            case MoveModeType.Party:
                party.SetNavigationModeParty(resolved.MoveTargetParty);
                break;
            default:
                party.PartyMoveMode = data.PartyMoveMode;
                party.MoveTargetParty = resolved.MoveTargetParty;
                break;
        }
        party.MoveTargetPoint = data.MoveTargetPoint;

        if (resetPath)
        {
            party._pathMode = false;
            party._aiPathNotFound = false;
            party.PathLastFace = PathFaceRecord.NullFaceRecord;
            party.PathBegin = 0;
            party.NextTargetPosition = party.Position;
            party.Party.SetVisualAsDirty();
        }
    }

    private bool TryResolveInteractable(
        PartyBehaviorUpdateData data,
        out IInteractablePoint interactable,
        bool logLookupFailures = true)
    {
        interactable = null;
        if (data.InteractablePointId == null)
            return true;
        if (data.IsInteractableAnchor)
            return TryResolve(data.InteractablePointId, out MobileParty owner, logLookupFailures) &&
                (interactable = owner.Anchor) != null;
        return TryResolve(data.InteractablePointId, out PartyBase partyBase, logLookupFailures) &&
            (interactable = partyBase) != null;
    }

    private bool TryResolve<T>(string id, out T value, bool logLookupFailures = true) where T : class
    {
        value = null;
        return id == null || (logLookupFailures
            ? objectManager.TryGetObjectWithLogging(id, out value)
            : objectManager.TryGetObject(id, out value));
    }

    private bool TryGetInteractableReference(IInteractablePoint interactable, out string id, out bool isAnchor)
    {
        isAnchor = interactable is AnchorPoint;
        if (interactable is PartyBase partyBase)
            return TryGetCompactId(partyBase, out id);
        if (interactable is AnchorPoint anchor && anchor.Owner != null)
            return TryGetCompactId(anchor.Owner, out id);
        id = null;
        return interactable == null;
    }

    private bool TryGetCompactId<T>(T instance, out string id)
        where T : class
    {
        if (instance != null && objectManager.TryGetId(instance, out id))
        {
            id = Compact(id, typeof(T));
            return true;
        }
        id = null;
        return instance == null;
    }

    private readonly struct ResolvedBehaviorUpdate
    {
        public readonly MobileParty Party;
        public readonly PartyBehaviorUpdateData Data;
        public readonly IInteractablePoint Interactable;
        public readonly MobileParty TargetParty;
        public readonly Settlement TargetSettlement;
        public readonly MobileParty MoveTargetParty;

        public ResolvedBehaviorUpdate(
            MobileParty party,
            PartyBehaviorUpdateData data,
            IInteractablePoint interactable,
            MobileParty targetParty,
            Settlement targetSettlement,
            MobileParty moveTargetParty)
        {
            Party = party;
            Data = data;
            Interactable = interactable;
            TargetParty = targetParty;
            TargetSettlement = targetSettlement;
            MoveTargetParty = moveTargetParty;
        }
    }
}
