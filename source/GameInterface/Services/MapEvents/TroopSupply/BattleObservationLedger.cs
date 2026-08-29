using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.Core;

namespace GameInterface.Services.MapEvents.TroopSupply;

/// <summary>
/// Plan 2 C4 and C5 - keeps the supply refusals and reserve-integrity findings that were previously only
/// written to a log, so a scenario can ASK for them instead of scraping a file afterwards.
/// </summary>
/// <remarks>
/// NOTHING HERE DETECTS ANYTHING
/// Both conditions were already found and already logged - the wave cap in CoopTroopSupplier, the duplicate
/// hero in BattleTroopReserveBuilder. Re-deriving them here would be a second implementation to keep in step
/// with the first, and the two would eventually disagree. The detection sites simply record what they already
/// decided, and this holds it.
///
/// WHY A LOG LINE IS NOT ENOUGH
/// A refusal is the answer to "why did those men never arrive", and it is needed at the moment a test asserts
/// the field is short - not in a 30 MB log after the run. It is also per-process: the client that refused is
/// the only one that knows, so this must be readable on whichever process a scenario is driving.
///
/// IT ALSO HOLDS THE TIMELINE AND THE OUTCOMES
/// C9 wants one ordered record of a battle and C8 wants its result after the map event is gone. Both are the
/// same shape as the two rings already here - something the code that knew it happened wrote down - so they
/// live here rather than in two more stores with their own locks and their own capacity rules. Refusals and
/// findings append to the timeline as they are recorded, so those two need no second call site and can never
/// appear in one view and not the other.
///
/// BOUNDED ON PURPOSE
/// A wedged battle produced 44,000 log events in two minutes earlier in this work. An unbounded ledger would
/// turn that into memory pressure inside the very process being diagnosed. Each ring keeps the most recent
/// entries and reports how many it dropped, so a reader can tell "nothing else happened" from "I stopped
/// looking".
/// </remarks>
public static class BattleObservationLedger
{
    private const int Capacity = 256;

    // The timeline receives every refusal and finding plus the lifecycle entries, so it fills fastest and is
    // the one a reader most wants intact. Outcomes are one per battle, so a handful covers any session.
    private const int TimelineCapacity = 1024;
    private const int OutcomeCapacity = 16;

    private static readonly object Gate = new object();
    private static readonly Queue<Refusal> Refusals = new Queue<Refusal>();
    private static readonly Queue<Finding> Findings = new Queue<Finding>();
    private static readonly Queue<TimelineEntry> Timeline = new Queue<TimelineEntry>();
    private static readonly Queue<BattleOutcome> Outcomes = new Queue<BattleOutcome>();
    private static int refusalsDropped;
    private static int findingsDropped;
    private static int timelineDropped;
    private static int outcomesDropped;

    public readonly struct Refusal
    {
        public readonly string MapEventId;
        public readonly BattleSideEnum Side;
        public readonly int Requested;
        public readonly int Quota;
        public readonly int Supplied;
        public readonly DateTime AtUtc;

        public Refusal(string mapEventId, BattleSideEnum side, int requested, int quota, int supplied)
        {
            MapEventId = mapEventId;
            Side = side;
            Requested = requested;
            Quota = quota;
            Supplied = supplied;
            AtUtc = DateTime.UtcNow;
        }

        public override string ToString() =>
            $"battle={MapEventId} side={Side} requested={Requested} quota={Quota} supplied={Supplied} " +
            $"withheld={Requested - Supplied} atUtc={AtUtc:HH:mm:ss.fff}";
    }

    public readonly struct Finding
    {
        public readonly string Kind;
        public readonly string MapEventId;
        public readonly string PartyId;
        public readonly string Detail;
        public readonly DateTime AtUtc;

        public Finding(string kind, string mapEventId, string partyId, string detail)
        {
            Kind = kind;
            MapEventId = mapEventId;
            PartyId = partyId;
            Detail = detail;
            AtUtc = DateTime.UtcNow;
        }

        public override string ToString() =>
            $"kind={Kind} battle={MapEventId} party={PartyId} {Detail} atUtc={AtUtc:HH:mm:ss.fff}";
    }

    /// <summary>One thing that happened, in the order it happened.</summary>
    public readonly struct TimelineEntry
    {
        public readonly DateTime AtUtc;
        public readonly string MapEventId;
        public readonly string Kind;
        public readonly string Detail;

        public TimelineEntry(string mapEventId, string kind, string detail)
        {
            AtUtc = DateTime.UtcNow;
            MapEventId = mapEventId ?? "<unknown>";
            Kind = kind;
            Detail = detail ?? string.Empty;
        }

        public override string ToString() =>
            $"{AtUtc:HH:mm:ss.fff} {Kind,-18} battle={MapEventId} {Detail}";
    }

    /// <summary>What one party walked away from the battle with.</summary>
    public sealed class OutcomeParty
    {
        public string PartyId { get; set; }
        public string Name { get; set; }
        public string Side { get; set; }
        public int HealthyAtStart { get; set; }
        public int Died { get; set; }
        public int Wounded { get; set; }
        public int Routed { get; set; }
        public int PrisonersToReceive { get; set; }
        public int LootMembersToReceive { get; set; }
        public int LootItemStacksToReceive { get; set; }
        public float GainedRenown { get; set; }
        public float GainedInfluence { get; set; }
        public int PlunderedGold { get; set; }
        public int GoldLost { get; set; }
        public int Contribution { get; set; }

        public override string ToString() =>
            $"party={PartyId} name={Name} side={Side} start={HealthyAtStart} died={Died} " +
            $"wounded={Wounded} routed={Routed} prisoners={PrisonersToReceive} " +
            $"lootMembers={LootMembersToReceive} lootStacks={LootItemStacksToReceive} " +
            $"renown={GainedRenown:F1} influence={GainedInfluence:F1} plundered={PlunderedGold} " +
            $"goldLost={GoldLost} contribution={Contribution}";
    }

    /// <summary>
    /// A battle's result, captured as it finalizes.
    /// </summary>
    /// <remarks>
    /// Captured on the way IN to FinalizeEvent, because finalize is what tears the rosters down - reading them
    /// afterwards gives zeros and reports every battle as bloodless. Whether finalize then RAN is recorded
    /// separately by the postfix, so "the battle ended 40-0" and "the battle ended and nothing was applied to
    /// the campaign" cannot be confused, which is the exact distinction C8 asks for.
    /// </remarks>
    public sealed class BattleOutcome
    {
        public string MapEventId { get; set; }
        public string EventType { get; set; }
        public string WinningSide { get; set; }
        public string BattleState { get; set; }
        public bool EndedByRetreat { get; set; }
        public string RetreatingSide { get; set; }
        public bool WasEverInLootingPhase { get; set; }
        public bool HasWinner { get; set; }
        public string Settlement { get; set; }
        public double DurationHours { get; set; }
        public DateTime CapturedUtc { get; set; }
        public bool CommittedToCampaign { get; set; }
        public List<OutcomeParty> Parties { get; set; } = new List<OutcomeParty>();

        public string Headline => $"battle={MapEventId} {Summary}";

        /// <summary>The headline without the battle id, for contexts that already name the battle.</summary>
        public string Summary =>
            $"type={EventType} winner={WinningSide} hasWinner={Lower(HasWinner)} " +
            $"state={BattleState} endedByRetreat={Lower(EndedByRetreat)} retreating={RetreatingSide} " +
            $"looted={Lower(WasEverInLootingPhase)} settlement={Settlement} " +
            $"durationHours={DurationHours:F2} committedToCampaign={Lower(CommittedToCampaign)} " +
            $"parties={Parties.Count} capturedUtc={CapturedUtc:HH:mm:ss.fff}";

        private static string Lower(bool value) => value.ToString().ToLowerInvariant();
    }

    public static void RecordEvent(string mapEventId, string kind, string detail)
    {
        lock (Gate)
        {
            AppendTimeline(new TimelineEntry(mapEventId, kind, detail));
        }
    }

    public static void RecordOutcome(BattleOutcome outcome)
    {
        if (outcome == null) return;
        lock (Gate)
        {
            Outcomes.Enqueue(outcome);
            while (Outcomes.Count > OutcomeCapacity) { Outcomes.Dequeue(); outcomesDropped++; }
            AppendTimeline(new TimelineEntry(outcome.MapEventId, "OUTCOME_CAPTURED", outcome.Summary));
        }
    }

    /// <summary>Marks the most recent capture for a battle as having actually been applied.</summary>
    public static void MarkOutcomeCommitted(string mapEventId)
    {
        lock (Gate)
        {
            var outcome = Outcomes.LastOrDefault(entry => entry.MapEventId == mapEventId);
            if (outcome == null) return;
            outcome.CommittedToCampaign = true;
            AppendTimeline(new TimelineEntry(mapEventId, "OUTCOME_COMMITTED", "finalize returned"));
        }
    }

    public static (IReadOnlyList<TimelineEntry> Entries, int Dropped) GetTimeline(string mapEventId = null)
    {
        lock (Gate)
        {
            var entries = Timeline
                .Where(entry => mapEventId == null || entry.MapEventId == mapEventId)
                .ToArray();
            return (entries, timelineDropped);
        }
    }

    public static (IReadOnlyList<BattleOutcome> Entries, int Dropped) GetOutcomes(string mapEventId = null)
    {
        lock (Gate)
        {
            var entries = Outcomes
                .Where(entry => mapEventId == null || entry.MapEventId == mapEventId)
                .ToArray();
            return (entries, outcomesDropped);
        }
    }

    // Callers already hold the gate - every recorder does - so this must never take it again.
    private static void AppendTimeline(TimelineEntry entry)
    {
        Timeline.Enqueue(entry);
        while (Timeline.Count > TimelineCapacity) { Timeline.Dequeue(); timelineDropped++; }
    }

    public static void RecordRefusal(string mapEventId, BattleSideEnum side, int requested, int quota, int supplied)
    {
        lock (Gate)
        {
            var refusal = new Refusal(mapEventId, side, requested, quota, supplied);
            Refusals.Enqueue(refusal);
            while (Refusals.Count > Capacity) { Refusals.Dequeue(); refusalsDropped++; }
            AppendTimeline(new TimelineEntry(mapEventId, "SUPPLY_REFUSED", refusal.ToString()));
        }
    }

    public static void RecordFinding(string kind, string mapEventId, string partyId, string detail)
    {
        lock (Gate)
        {
            var finding = new Finding(kind, mapEventId, partyId, detail);
            Findings.Enqueue(finding);
            while (Findings.Count > Capacity) { Findings.Dequeue(); findingsDropped++; }
            AppendTimeline(new TimelineEntry(mapEventId, "RESERVE_FINDING", finding.ToString()));
        }
    }

    public static (IReadOnlyList<Refusal> Entries, int Dropped) GetRefusals(string mapEventId = null)
    {
        lock (Gate)
        {
            var entries = Refusals
                .Where(entry => mapEventId == null || entry.MapEventId == mapEventId)
                .ToArray();
            return (entries, refusalsDropped);
        }
    }

    public static (IReadOnlyList<Finding> Entries, int Dropped) GetFindings(string mapEventId = null)
    {
        lock (Gate)
        {
            var entries = Findings
                .Where(entry => mapEventId == null || entry.MapEventId == mapEventId)
                .ToArray();
            return (entries, findingsDropped);
        }
    }

    /// <summary>Clears every ring, so a scenario can assert on what happened after a chosen moment.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Refusals.Clear();
            Findings.Clear();
            Timeline.Clear();
            Outcomes.Clear();
            refusalsDropped = 0;
            findingsDropped = 0;
            timelineDropped = 0;
            outcomesDropped = 0;
        }
    }
}
