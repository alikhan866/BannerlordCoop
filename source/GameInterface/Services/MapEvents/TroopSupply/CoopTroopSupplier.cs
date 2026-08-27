using Common.Logging;
using GameInterface.Services.ObjectManager;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace GameInterface.Services.MapEvents.TroopSupply;

/// <summary>
/// A coop battle's troop supplier for one side: instead of pulling from the local <c>MapEventSide</c> pool
/// (as the native <c>PartyGroupTroopSupplier</c> does), it serves troops the SERVER committed — fed in over
/// the network as <see cref="PartyReserve"/>s. The native deployment/reinforcement/formation logic drives it
/// exactly as it drives the native supplier, so we only change where troops come from. Because the server
/// owns the descriptor seeds, every client agrees on troop identity, and on disconnect/migration a fresh
/// owner resumes from the server's supplied pointer.
/// <para>
/// State is per party (the side aggregates one or more), so the supplied pointer maps cleanly to the server's
/// per-party ledger and to migration. Substituted into the mission by <c>BattleTroopSupplierInjectionPatch</c>
/// and fed via <c>CoopTroopSupplierRegistry</c>. Supply runs on the game thread, <see cref="SetReserve"/> on
/// the network thread — hence the lock.
/// </para>
/// </summary>
public class CoopTroopSupplier : IMissionTroopSupplier
{
    private static readonly ILogger Logger = LogManager.GetLogger<CoopTroopSupplier>();

    public readonly struct AllocationSnapshot
    {
        private readonly int[] partyOffsets;
        private readonly int[] partyCounts;
        private readonly int playerOwnedPartyCount;
        private readonly bool ownsReceiverPlayerParty;
        private readonly int receiverPlayerRank;
        private readonly bool hasPlayerOwnedPartiesBefore;
        private readonly int[] supplyOrders;

        public long Revision { get; }
        public int BattleSize { get; }
        public int SideTotalTroops { get; }
        public int TotalTroops { get; }
        public int SuppliedTroops { get; }

        internal AllocationSnapshot(long revision, int battleSize, int sideTotalTroops, int totalTroops,
            int suppliedTroops,
            int playerOwnedPartyCount, bool ownsReceiverPlayerParty, int receiverPlayerRank,
            bool hasPlayerOwnedPartiesBefore,
            int[] partyOffsets, int[] partyCounts, int[] supplyOrders)
        {
            Revision = revision;
            BattleSize = battleSize;
            SideTotalTroops = sideTotalTroops;
            TotalTroops = totalTroops;
            SuppliedTroops = suppliedTroops;
            this.playerOwnedPartyCount = playerOwnedPartyCount;
            this.ownsReceiverPlayerParty = ownsReceiverPlayerParty;
            this.receiverPlayerRank = receiverPlayerRank;
            this.hasPlayerOwnedPartiesBefore = hasPlayerOwnedPartiesBefore;
            this.partyOffsets = partyOffsets;
            this.partyCounts = partyCounts;
            this.supplyOrders = supplyOrders;
        }

        public int OwnedShareOf(int sideAllocation)
        {
            if (sideAllocation <= 0 || TotalTroops <= 0) return 0;

            int total = SideTotalTroops;
            if (total <= 0) return 0;
            sideAllocation = Math.Min(sideAllocation, total);
            if (supplyOrders != null)
            {
                int rankedShare = 0;
                foreach (int supplyOrder in supplyOrders)
                    if (supplyOrder > 0 && supplyOrder <= sideAllocation)
                        rankedShare++;
                return rankedShare;
            }
            if (TotalTroops >= total) return sideAllocation;

            if (playerOwnedPartyCount > 0)
            {
                if (sideAllocation < playerOwnedPartyCount)
                    return receiverPlayerRank >= 0 && receiverPlayerRank < sideAllocation ? 1 : 0;

                int apportionmentTotal = hasPlayerOwnedPartiesBefore
                    ? Math.Max(0, total - playerOwnedPartyCount)
                    : total;
                int share = ApportionByInterval(sideAllocation - playerOwnedPartyCount, apportionmentTotal);
                if (ownsReceiverPlayerParty) share += 1;
                return hasPlayerOwnedPartiesBefore
                    ? Math.Min(share, Math.Min(sideAllocation, TotalTroops))
                    : Math.Min(share, sideAllocation);
            }

            return Math.Min(ApportionByInterval(sideAllocation, total), sideAllocation);
        }

        private int ApportionByInterval(int allocation, int total)
        {
            if (allocation <= 0 || partyOffsets == null || partyCounts == null) return 0;

            int share = 0;
            for (int i = 0; i < partyCounts.Length; i++)
            {
                int count = partyCounts[i];
                if (count <= 0) continue;

                int start = ScaleToAllocation(partyOffsets[i], total, allocation);
                int end = ScaleToAllocation(partyOffsets[i] + count, total, allocation);
                share += end - start;
            }
            return share;
        }

        private static int ScaleToAllocation(int position, int total, int allocation)
            => (int)((long)position * allocation / total);
    }

    private sealed class PartyState
    {
        public string PartyId;
        public TroopReserveEntry[] Entries = Array.Empty<TroopReserveEntry>();
        public int Supplied;
        /// <summary>Where this party starts within its side; see <see cref="PartyReserve.SideOffset"/>.</summary>
        public int SideOffset;
        /// <summary>Its position among the side's player-owned parties, or -1; see <see cref="PartyReserve.PlayerOwnedRank"/>.</summary>
        public int PlayerOwnedRank;
        /// <summary>Player-owned parties before this party in the complete side order.</summary>
        public int PlayerOwnedPartiesBefore;
        /// <summary>Whether the sender supplied the guaranteed-slot offset metadata.</summary>
        public bool HasPlayerOwnedPartiesBefore;
    }

    private readonly object gate = new object();
    private readonly List<PartyState> parties = new List<PartyState>();
    // seed -> partyId, rebuilt alongside `parties` in SetReserve, so GetParty/FindPartyId is O(1) instead of
    // scanning every party's entries per agent. Entry seeds are server-unique, so one seed maps to one party.
    private readonly Dictionary<int, string> seedToPartyId = new Dictionary<int, string>();
    private string playerPartyId;
    private bool populated;
    private int sideTotalTroops;
    private int playerOwnedPartyCount;
    private long allocationRevision;
    private int battleSize;
    private int reserveRevision;
    private bool usesSupplyOrder;
    private int numWounded, numKilled, numRouted;
    private bool sizingSourceReported;
    // Injected at construction (a stable per-session singleton) so the per-agent supply path resolves troop/party
    // objects without hitting the service locator each call. Null only in tests that don't exercise that path.
    private readonly IObjectManager objectManager;
    // BR-110: the engine agent budget clamps wave/initial allocation to the mission's render capacity.
    private readonly IBattleAgentBudget agentBudget;
    public string MapEventId { get; }
    public BattleSideEnum Side { get; }

    public CoopTroopSupplier(string mapEventId, BattleSideEnum side, IObjectManager objectManager,
        IBattleAgentBudget agentBudget)
    {
        MapEventId = mapEventId;
        Side = side;
        this.objectManager = objectManager;
        this.agentBudget = agentBudget;
    }

    /// <summary>
    /// [Network thread] Replace this side's reserve with the server's authoritative set (each party with its
    /// current supplied pointer — 0 at battle start, the server's pointer on migration). Marks us populated,
    /// so a side this client owns nothing on (empty set) reports "done" instead of blocking deployment.
    /// A party's pointer never rewinds: if we have already supplied further than a (possibly stale) resend
    /// carries, we keep our local pointer — see the monotonic resume below.
    /// <para>
    /// Returns the FINAL local supplied pointers of the parties this REPLACE dropped (held before, absent
    /// from the new set) — the BR-033 flush payload. Captured under the same lock as the replace itself, so
    /// no supply can advance a dropped party between the capture and the removal: the returned pointers are
    /// definitively this supplier's last word on those parties.
    /// </para>
    /// </summary>
    public IReadOnlyList<(string PartyId, int Supplied)> SetReserve(IReadOnlyList<PartyReserve> reserve,
        int sideTotal, int playerOwnedParties, int authoritativeBattleSize, long snapshotRevision = 0)
    {
        var dropped = new List<(string PartyId, int Supplied)>();
        lock (gate)
        {
            sideTotalTroops = Math.Max(0, sideTotal);
            playerOwnedPartyCount = Math.Max(0, playerOwnedParties);
            allocationRevision = snapshotRevision;
            battleSize = Math.Max(0, authoritativeBattleSize);

            // Capture the current per-party pointers before rebuilding. A resend can carry a STALE pointer: the
            // server's ledger lags our local supply by up to one report interval, and on migration it re-sends
            // our OWN party at that lagging value. Resuming from the server value alone would rewind a party we
            // have already supplied further and re-spawn troops already on the field (with duplicate seeds). So
            // resume from max(local, server), mirroring the server ledger's own monotonic ReportSupplied.
            var priorSupplied = new Dictionary<string, int>(parties.Count);
            foreach (var existing in parties)
                priorSupplied[existing.PartyId] = existing.Supplied;

            parties.Clear();
            seedToPartyId.Clear();
            playerPartyId = null;
            if (reserve != null)
            {
                foreach (var party in reserve)
                {
                    var entries = party.Entries ?? Array.Empty<TroopReserveEntry>();
                    int supplied = Math.Min(Math.Max(0, party.SuppliedCount), entries.Length);
                    if (priorSupplied.TryGetValue(party.PartyId, out var local) && local > supplied)
                        supplied = Math.Min(local, entries.Length);
                    priorSupplied.Remove(party.PartyId); // kept — not part of the dropped set
                    var state = new PartyState
                    {
                        PartyId = party.PartyId,
                        Entries = entries,
                        Supplied = supplied,
                        SideOffset = party.SideOffset,
                        PlayerOwnedRank = party.PlayerOwnedRank,
                        PlayerOwnedPartiesBefore = party.PlayerOwnedPartiesBefore,
                        HasPlayerOwnedPartiesBefore = party.HasPlayerOwnedPartiesBefore,
                    };
                    // Allocate this client's own party first. Otherwise an army's AI parties can fill the
                    // render cap before the local hero is reserved, leaving deployment without a player agent.
                    if (party.IsReceiverPlayerParty)
                        parties.Insert(0, state);
                    else
                        parties.Add(state);
                    if (party.IsReceiverPlayerParty)
                        playerPartyId = party.PartyId;
                    foreach (var entry in entries)
                        seedToPartyId[entry.Seed] = party.PartyId;
                }
            }
            usesSupplyOrder = false;
            bool foundEntry = false;
            bool allEntriesRanked = true;
            foreach (var party in parties)
            {
                foreach (var entry in party.Entries)
                {
                    foundEntry = true;
                    if (entry.SupplyOrder <= 0)
                        allEntriesRanked = false;
                }
            }
            usesSupplyOrder = foundEntry && allEntriesRanked;
            populated = true;
            reserveRevision++;

            // Whatever the new set did not re-claim was DROPPED by this replace.
            foreach (var prior in priorSupplied)
                dropped.Add((prior.Key, prior.Value));
        }

        Logger.Information("[TroopSupply] Supplier {MapEvent} side {Side}: SetReserve {Parties} parties / {Entries} troops ({Dropped} parties dropped), receiver party {PlayerParty}",
            MapEventId, Side, parties.Count, NumTroopsNotSupplied, dropped.Count, PlayerPartyId);
        return dropped;
    }

    /// <summary>How many troops have been supplied per party — reported back to the server for the ledger.</summary>
    public IReadOnlyList<(string partyId, int supplied)> GetSuppliedByParty()
    {
        lock (gate)
        {
            var result = new List<(string, int)>(parties.Count);
            foreach (var party in parties)
                result.Add((party.PartyId, party.Supplied));
            return result;
        }
    }

    /// <summary>
    /// Winds every party's supplied pointer back to the start of its reserve, for a round restart.
    /// </summary>
    /// <remarks>
    /// A restart clears the field and spawns the battle again from scratch, so the reserve has to be drawable
    /// from the top - otherwise the second round can only field whatever the first round had not reached, and a
    /// side that had already spawned most of its men would come back nearly empty.
    ///
    /// Deliberately separate from <see cref="SetReserve"/>, which refuses to rewind: an authoritative resend that
    /// moved a pointer backwards would re-supply troops that are already on the field, so that guard has to stay.
    /// A restart is the one case where rewinding is the whole point, and saying so explicitly keeps the two
    /// intentions from being confused for one another.
    ///
    /// Casualty counters are left alone. They describe men who died in the battle, and a restart re-forms the
    /// lines - it does not resurrect anyone the campaign has already been told about.
    /// </remarks>
    public void RewindForRoundRestart()
    {
        lock (gate)
        {
            foreach (var party in parties) party.Supplied = 0;
            reserveRevision++;
        }

        Logger.Information("[TroopSupply] Supplier {MapEvent} side {Side}: rewound for a round restart", MapEventId, Side);
    }

    /// <summary>
    /// Hands one troop of <paramref name="partyId"/> back to the reserve, so it can be fielded again later.
    /// </summary>
    /// <remarks>
    /// Used when a troop is stood down to keep a side within the battle size. The man has not died and has not
    /// left the battle - he is waiting again, exactly like one who was never fielded - so the pointer moves
    /// back one and he returns through the ordinary reinforcement path as casualties make room. Without this he
    /// would be gone from the fight for good, which is not what standing down means.
    /// </remarks>
    public void ReturnOneToReserve(string partyId)
    {
        if (string.IsNullOrEmpty(partyId)) return;

        lock (gate)
        {
            foreach (var party in parties)
            {
                if (party.PartyId != partyId) continue;
                if (party.Supplied > 0) party.Supplied--;
                return;
            }
        }
    }

    public int NumRemovedTroops { get { lock (gate) { return numWounded + numKilled + numRouted; } } }

    /// <summary>Whether the server's reserve has arrived (counts/identity known and final).</summary>
    public bool IsPopulated { get { lock (gate) { return populated; } } }

    /// <summary>The server-authored reserve id of this client's own party, when it belongs to this side.</summary>
    public string PlayerPartyId { get { lock (gate) { return playerPartyId; } } }

    /// <summary>Monotonic count of authoritative reserve snapshots applied to this supplier.</summary>
    public int ReserveRevision { get { lock (gate) { return reserveRevision; } } }

    /// <summary>The server-authored complete two-side snapshot generation.</summary>
    public long AllocationRevision { get { lock (gate) { return allocationRevision; } } }

    public AllocationSnapshot CaptureAllocationSnapshot()
    {
        lock (gate)
        {
            int total = 0;
            int supplied = 0;
            int receiverPlayerRank = -1;
            var partyOffsets = new int[parties.Count];
            var partyCounts = new int[parties.Count];
            bool hasGuaranteedSlotOffsets = true;
            foreach (var party in parties)
                hasGuaranteedSlotOffsets &= party.HasPlayerOwnedPartiesBefore;
            for (int i = 0; i < parties.Count; i++)
            {
                var party = parties[i];
                int count = party.Entries.Length;
                partyOffsets[i] = hasGuaranteedSlotOffsets
                    ? Math.Max(0, party.SideOffset - party.PlayerOwnedPartiesBefore)
                    : party.SideOffset;
                partyCounts[i] = hasGuaranteedSlotOffsets
                    ? Math.Max(0, count - (party.PlayerOwnedRank >= 0 ? 1 : 0))
                    : count;
                total += count;
                supplied += party.Supplied;
                if (party.PartyId != playerPartyId) continue;

                receiverPlayerRank = party.PlayerOwnedRank;
            }

            int[] ownedSupplyOrders = null;
            if (usesSupplyOrder)
            {
                ownedSupplyOrders = new int[total];
                int supplyOrderIndex = 0;
                foreach (var party in parties)
                    foreach (var entry in party.Entries)
                        ownedSupplyOrders[supplyOrderIndex++] = entry.SupplyOrder;
            }

            return new AllocationSnapshot(
                allocationRevision,
                battleSize,
                sideTotalTroops,
                total,
                supplied,
                playerOwnedPartyCount,
                playerPartyId != null,
                receiverPlayerRank,
                hasGuaranteedSlotOffsets,
                partyOffsets,
                partyCounts,
                ownedSupplyOrders);
        }
    }

    /// <summary>Remaining troop count for each party in the current authoritative reserve.</summary>
    public IReadOnlyList<(string partyId, int remaining)> GetRemainingByParty()
    {
        lock (gate)
        {
            var result = new List<(string, int)>(parties.Count);
            foreach (var party in parties)
                result.Add((party.PartyId, party.Entries.Length - party.Supplied));
            return result;
        }
    }

    /// <summary>Remaining troop count for one party, or zero when it is absent or exhausted.</summary>
    public int GetRemainingForParty(string partyId)
    {
        lock (gate)
        {
            foreach (var party in parties)
                if (party.PartyId == partyId)
                    return party.Entries.Length - party.Supplied;
            return 0;
        }
    }

#if DEBUG
    /// <summary>Runs a debug allocation decision while reserve refreshes are blocked.</summary>
    internal TResult WithSupplyPreview<TResult>(
        int numberToAllocate,
        Func<List<IAgentOriginBase>, TResult> action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));

        var origins = new List<IAgentOriginBase>();
        lock (gate)
        {
            if (numberToAllocate > 0)
            {
                int slotBudget = agentBudget != null
                    ? agentBudget.RemainingCapacity(agentBudget.CountLiveAgents(Mission.Current))
                    : int.MaxValue;
                int allocated = 0;
                var previewPointers = new int[parties.Count];
                for (int i = 0; i < parties.Count; i++)
                    previewPointers[i] = parties[i].Supplied;

                while (allocated < numberToAllocate)
                {
                    int partyIndex = GetNextPartyIndex(previewPointers);
                    if (partyIndex < 0) break;

                    var party = parties[partyIndex];
                    var origin = CreateOrigin(party.Entries[previewPointers[partyIndex]], party.PartyId);
                    int slots = SlotsForOrigin(origin);
                    if (slots > slotBudget) return action(origins);

                    previewPointers[partyIndex]++;
                    allocated++;
                    slotBudget -= slots;
                    if (origin != null) origins.Add(origin);
                }
            }
            return action(origins);
        }
    }
#endif

    /// <summary>Whether this authoritative reserve snapshot still contains a party.</summary>
    public bool ContainsParty(string partyId)
    {
        lock (gate)
        {
            foreach (var party in parties)
                if (party.PartyId == partyId)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Claim the missing live troops of a newly-owned migration party for explicit recovery. The whole party
    /// is marked supplied so the native wave path cannot also spawn entries now owned by the recovery queue.
    /// </summary>
    public IReadOnlyList<CoopAgentOrigin> ClaimRecoveryTroops(
        string partyId,
        IReadOnlyDictionary<string, int> neededByCharacter,
        ISet<int> recoverableSeeds)
    {
        var origins = new List<CoopAgentOrigin>();
        lock (gate)
        {
            foreach (var party in parties)
            {
                if (party.PartyId != partyId) continue;

                var remainingNeeded = new Dictionary<string, int>();
                foreach (var pair in neededByCharacter)
                    remainingNeeded[pair.Key] = pair.Value;
                foreach (var entry in party.Entries)
                {
                    if (!recoverableSeeds.Contains(entry.Seed)) continue;
                    if (!remainingNeeded.TryGetValue(entry.CharacterId, out var needed) || needed <= 0) continue;

                    if (CreateOrigin(entry, partyId) is CoopAgentOrigin origin)
                    {
                        origins.Add(origin);
                        remainingNeeded[entry.CharacterId] = needed - 1;
                    }
                }

                party.Supplied = party.Entries.Length;
                break;
            }
        }
        return origins;
    }

    /// <summary>Total troops this side's supplier owns (across its parties), regardless of supplied state —
    /// the per-side count the coop spawn handler sizes the engine's deployment to.</summary>
    public int TotalTroops
    {
        get
        {
            lock (gate)
            {
                int total = 0;
                foreach (var party in parties)
                    total += party.Entries.Length;
                return total;
            }
        }
    }

    /// <summary>
    /// Every troop on this side across ALL owners.
    /// The spawn handler sizes the engine from this so each client computes the same split; the supplier then
    /// contributes only its <see cref="OwnedShareOf"/> that allocation.
    /// </summary>
    public int SideTotalTroops { get { lock (gate) { return sideTotalTroops; } } }

    public int PlayerOwnedPartyCount { get { lock (gate) { return playerOwnedPartyCount; } } }

    public int BattleSize { get { lock (gate) { return battleSize; } } }

    /// <summary>
    /// This client's slice of a side-wide allocation, in proportion to the troops it owns. Every owner runs
    /// the same sum, so the slices add up to the allocation instead of each owner serving all of it.
    /// </summary>
    public int OwnedShareOf(int sideAllocation)
        => CaptureAllocationSnapshot().OwnedShareOf(sideAllocation);

    public int NumTroopsNotSupplied
    {
        get
        {
            lock (gate)
            {
                int notSupplied = 0;
                foreach (var party in parties)
                    notSupplied += party.Entries.Length - party.Supplied;
                return notSupplied;
            }
        }
    }

    // True while the reserve hasn't arrived (so deployment waits rather than concluding "no troops") and
    // while any party still has troops to supply.
    public bool AnyTroopRemainsToBeSupplied
    {
        get
        {
            lock (gate)
            {
                if (!populated) return true;
                foreach (var party in parties)
                    if (party.Supplied < party.Entries.Length) return true;
                return false;
            }
        }
    }

    public IEnumerable<IAgentOriginBase> SupplyTroops(int numberToAllocate)
    {
        // No apportionment here: the number arriving is ALREADY this client's share. Init is given the side
        // totals so every client computes the same battle-size split, and CoopBattleMissionSpawnHandler then
        // rewrites the phase numbers through OwnedShareOf once - so every request the engine derives from a
        // phase is this client's slice, and the other owners' agents arrive replicated as before
        // (OwnedAgentReplicator/PuppetSpawner).
        //
        // Taking the share again here would apply it twice, and the engine cannot tolerate being short-changed:
        // CheckDeployment reserves InitialSpawnNumber - ReservedTroopsCount and SKIPS THE WHOLE SIDE (its
        // plan-making included) while the count falls short. Returning a fraction of each request makes the gap
        // close geometrically and never reach zero - live, a 65-troop target stalled at 64 with the side never
        // planned, so the player's team never spawned and the player had no agent on the field.
        if (numberToAllocate <= 0) return Array.Empty<IAgentOriginBase>();

        // ...but the number arriving is still the engine's idea of the shortfall, and in a coop battle that
        // idea is wrong. The engine counts a side as `spawned - removed` PER SUPPLIER, so it cannot see the
        // agents a peer replicated, nor the ones ReinforcementFielder spawns directly for a party that joined
        // mid-battle. It measures a side of 190 as a side of 90 and asks for the difference. Live, on a battle
        // sized for 400: three separate requests for 101 men against room for about 13, each landing in under
        // two seconds and each cut straight back down by the field balancer - spending 300 men out of a reserve
        // of 534 who never fought, until the reserve read zero and the side could not reinforce at all.
        //
        // So the wave is capped by what the FIELD has room for rather than by what the engine believes.
        // Deliberately this client's OWNED share of that room (the same apportionment that splits every other
        // side-wide figure), so several owners filling one side cannot each supply all of it.
        //
        // Only reinforcement is capped. BattleFieldRoom answers Unlimited while the opening wave is still
        // landing, because CheckDeployment skips a whole side - plan-making included - if its supplier
        // under-delivers, and a skipped side never spawns the player an agent.
        int myQuota = RemainingFieldQuota();
        int capped = CapWaveToQuota(numberToAllocate, myQuota);
        if (capped != numberToAllocate)
        {
            Logger.Information("[TroopSupply] {MapEvent} side {Side}: engine asked for {Req}, my remaining quota is {Quota}; supplying {Capped}",
                MapEventId, Side, numberToAllocate, myQuota, capped);
            numberToAllocate = capped;
        }
        if (numberToAllocate <= 0) return Array.Empty<IAgentOriginBase>();

        // BR-110: allocate no more troops than the engine has RENDER-SLOT capacity for — a mounted troop needs
        // two slots (rider + horse). The unallocated remainder stays UNSUPPLIED (wave-eligible), so the native
        // wave logic re-requests it as casualties free slots; the supplied pointer stays aligned with what can
        // actually field. A null budget (the service-locator fallback path could not resolve one) means no
        // clamp, matching the no-mission behaviour. The native drip is additionally re-checked at spawn time by
        // MissionSpawnCapacityPatch, so this clamp is a pre-filter, not the sole guard.
        int slotBudget = agentBudget != null
            ? agentBudget.RemainingCapacity(agentBudget.CountLiveAgents(Mission.Current))
            : int.MaxValue;

        var origins = new List<IAgentOriginBase>();
        int supplied = 0;
        lock (gate)
        {
            // A siege-ambush wave is ranked by SupplyOrder, and that ranking is only meaningful troop by
            // troop, so honour it exactly as upstream does.
            if (usesSupplyOrder)
            {
                while (supplied < numberToAllocate)
                {
                    var party = GetNextPartyWithRemaining();
                    if (party == null) break;

                    var origin = CreateOrigin(party.Entries[party.Supplied], party.PartyId);
                    int slots = SlotsForOrigin(origin);
                    // Stop rather than skip: the supplied pointer advances sequentially, so a troop that does
                    // not fit now must remain unsupplied (wave-eligible) instead of being jumped over.
                    if (slots > slotBudget) break;

                    party.Supplied++;
                    supplied++;
                    slotBudget -= slots;
                    if (origin != null) origins.Add(origin);
                }
            }
            // Unranked, each party gives up its own share of the wave instead. GetNextPartyWithRemaining falls
            // back to the first party still holding troops, which drains them in order: that emptied the first
            // party before the second contributed anything, and the receiver's own party is deliberately first
            // (below), so a player in an army fielded their whole party against the enemy's mixed wave and
            // fought it alone - the allied lords trickled in one at a time as those men died.
            else
            {
                var quota = BuildWaveQuota(numberToAllocate);

                bool stop = false;
                for (int i = 0; i < parties.Count && !stop; i++)
                {
                    var party = parties[i];
                    int take = quota[i];
                    while (take > 0 && party.Supplied < party.Entries.Length)
                    {
                        var origin = CreateOrigin(party.Entries[party.Supplied], party.PartyId);
                        int slots = SlotsForOrigin(origin);
                        // Stop rather than skip: the supplied pointer advances sequentially within a party, so
                        // a troop that does not fit now must remain unsupplied (wave-eligible), not jumped over.
                        if (slots > slotBudget) { stop = true; break; }

                        party.Supplied++;
                        supplied++;
                        take--;
                        slotBudget -= slots;
                        if (origin != null) origins.Add(origin);
                    }
                }
            }
        }
        Logger.Information("[TroopSupply] {MapEvent} side {Side}: SupplyTroops({Req}) -> {Ret} origins ({Withheld} withheld at the engine agent limit), {Remaining} remaining",
            MapEventId, Side, numberToAllocate, origins.Count, numberToAllocate - supplied, NumTroopsNotSupplied);
        return origins;
    }

    /// <summary>
    /// How many troops each owned party contributes to one wave, in proportion to what it has LEFT.
    /// </summary>
    /// <remarks>
    /// This is the per-party counterpart of <see cref="OwnedShareOf"/> (which splits a wave between CLIENTS),
    /// and it mirrors what vanilla does for a whole side: <c>MapEventSide.MakeReady</c> builds one priority
    /// list spanning every party, weighted by party size, sorts it, and <c>AllocateTroops</c> takes the first
    /// N - so a vanilla wave is a proportional cross-section of the side rather than one party at a time.
    ///
    /// Same cumulative-flooring trick as <see cref="ApportionByInterval"/>: each party takes the difference
    /// between the wave scaled to the END of its range and to its START, over contiguous non-overlapping
    /// ranges, so the shares sum to EXACTLY the wave with nothing lost to rounding. That exactness matters -
    /// CheckDeployment reserves <c>InitialSpawnNumber - ReservedTroopsCount</c> and skips the whole side while
    /// the count falls short, so a wave that quietly under-delivers stops the side being planned at all.
    ///
    /// Weighting by REMAINING rather than by original size keeps later waves representative as parties run
    /// dry, and guarantees a party is never handed more than it holds (for a wave no larger than the total
    /// remaining, its share cannot exceed its own remainder), so no party can under-deliver its quota.
    ///
    /// Callers hold <see cref="gate"/>.
    /// </remarks>
    private int[] BuildWaveQuota(int numberToAllocate)
    {
        var quota = new int[parties.Count];

        long totalRemaining = 0;
        for (int i = 0; i < parties.Count; i++)
            totalRemaining += parties[i].Entries.Length - parties[i].Supplied;
        if (totalRemaining <= 0) return quota;

        // Asking for more than exists is normal (the engine asks for a side's whole deficit); apportioning the
        // capped figure is what keeps each party's share inside its own remainder.
        long target = Math.Min(numberToAllocate, totalRemaining);

        long cumulative = 0;
        long allocatedSoFar = 0;
        for (int i = 0; i < parties.Count; i++)
        {
            long remaining = parties[i].Entries.Length - parties[i].Supplied;
            cumulative += remaining;
            // long throughout: cumulative * target overflows int for a large army and a large wave, and an
            // overflow here would hand out a negative or wrapped share.
            long end = cumulative * target / totalRemaining;
            quota[i] = (int)Math.Min(end - allocatedSoFar, remaining);
            allocatedSoFar += quota[i];
        }

        GuaranteeReceiverPlayerATroop(quota, target);
        return quota;
    }

    /// <summary>
    /// Makes sure the receiver's own party contributes to a non-empty wave, taking the troop from the largest
    /// share so the wave still totals exactly what was asked for.
    /// </summary>
    /// <remarks>
    /// A player whose party is tiny next to the army it fights with rounds to nothing - "one player with only
    /// himself" alongside a 999-strong lord is the extreme case. That player would field no agent at all,
    /// which is not a cosmetic loss: they have nothing to control, and the spawn handler reads a missing
    /// origin on the side holding the local player as a reason to abort the battle.
    /// </remarks>
    private void GuaranteeReceiverPlayerATroop(int[] quota, long target)
    {
        if (playerPartyId == null || target <= 0) return;

        int playerIndex = -1;
        for (int i = 0; i < parties.Count; i++)
        {
            if (parties[i].PartyId != playerPartyId) continue;
            playerIndex = i;
            break;
        }

        if (playerIndex < 0 || quota[playerIndex] > 0) return;
        if (parties[playerIndex].Supplied >= parties[playerIndex].Entries.Length) return; // nothing left to give

        int largest = -1;
        for (int i = 0; i < quota.Length; i++)
            if (quota[i] > 0 && (largest < 0 || quota[i] > quota[largest])) largest = i;
        if (largest < 0) return;

        quota[largest]--;
        quota[playerIndex]++;
    }

    // BR-110: render slots one supplied origin will consume when spawned — a mounted troop spawns a rider and a
    // horse (2), an unmounted troop one (1), a null/unresolvable origin none (0, so it advances the supplied
    // pointer without charging the budget). Falls back to 1 when no budget is available (the null-budget path).
    private int SlotsForOrigin(IAgentOriginBase origin)
    {
        if (origin == null) return 0;
        return agentBudget == null ? 1 : agentBudget.SlotsForOrigin(origin);
    }

    public IAgentOriginBase SupplyOneTroop()
    {
        lock (gate)
        {
            var party = GetNextPartyWithRemaining();
            if (party == null) return null;

            var origin = CreateOrigin(party.Entries[party.Supplied], party.PartyId);
            party.Supplied++;
            return origin;
        }
    }

    private PartyState GetNextPartyWithRemaining()
    {
        PartyState next = null;
        int nextOrder = int.MaxValue;
        foreach (var party in parties)
        {
            if (party.Supplied >= party.Entries.Length) continue;
            if (!usesSupplyOrder) return party;

            int supplyOrder = party.Entries[party.Supplied].SupplyOrder;
            if (supplyOrder >= nextOrder) continue;
            next = party;
            nextOrder = supplyOrder;
        }
        return next;
    }

    private int GetNextPartyIndex(int[] pointers)
    {
        int nextIndex = -1;
        int nextOrder = int.MaxValue;
        for (int i = 0; i < parties.Count; i++)
        {
            var party = parties[i];
            if (pointers[i] >= party.Entries.Length) continue;
            if (!usesSupplyOrder) return i;

            int supplyOrder = party.Entries[pointers[i]].SupplyOrder;
            if (supplyOrder >= nextOrder) continue;
            nextIndex = i;
            nextOrder = supplyOrder;
        }
        return nextIndex;
    }

    /// <summary>Supply the next remaining troop from one party without consuming any other party.</summary>
    public IAgentOriginBase SupplyOneTroopFromParty(string partyId)
    {
        lock (gate)
        {
            foreach (var party in parties)
            {
                if (party.PartyId != partyId) continue;
                if (party.Supplied >= party.Entries.Length) return null;

                var origin = CreateOrigin(party.Entries[party.Supplied], party.PartyId);
                party.Supplied++;
                return origin;
            }
            return null;
        }
    }

    public IEnumerable<IAgentOriginBase> GetAllTroops()
    {
        var origins = new List<IAgentOriginBase>();
        lock (gate)
        {
            var pointers = new int[parties.Count];
            while (true)
            {
                int partyIndex = GetNextPartyIndex(pointers);
                if (partyIndex < 0) break;

                var party = parties[partyIndex];
                var origin = CreateOrigin(party.Entries[pointers[partyIndex]++], party.PartyId);
                if (origin != null) origins.Add(origin);
            }
        }
        return origins;
    }

    public BasicCharacterObject GetGeneralCharacter()
    {
        lock (gate)
        {
            foreach (var party in parties)
                foreach (var entry in party.Entries)
                    if (TryResolveCharacter(entry, out var character) && character.IsHero)
                        return character;
        }
        return null;
    }

    // The local player commands the troops it owns, so the whole owned reserve is player-controllable.
    public int GetNumberOfPlayerControllableTroops()
    {
        lock (gate)
        {
            int count = 0;
            foreach (var party in parties)
                count += party.Entries.Length;
            return count;
        }
    }

    public PartyBase GetParty(UniqueTroopDescriptor troopDescriptor)
    {
        string partyId;
        lock (gate)
            seedToPartyId.TryGetValue(troopDescriptor.UniqueSeed, out partyId);

        return ResolveParty(partyId);
    }

    /// <summary>
    /// How large a wave may actually be supplied: what was asked for, or this client's share of the room the
    /// field has left, whichever is smaller.
    /// </summary>
    /// <param name="requested">What the engine asked for - already this client's share of the phase.</param>
    /// <param name="sideRoom">Room left on the side, or <see cref="BattleFieldRoom.Unlimited"/>.</param>
    /// <param name="ownedShareOf">This client's slice of a side-wide figure.</param>
    /// <remarks>
    /// Unlimited must pass the request through UNTOUCHED rather than take a share of it. It is the answer both
    /// while the opening wave is still landing and when the mission has no battle sizing at all, and in the
    /// first of those the engine cannot tolerate being short-changed: <c>CheckDeployment</c> skips a whole side
    /// - its plan-making included - while its supplier under-delivers, so a side quietly given a fraction of
    /// its deployment is never planned and never spawns the player an agent.
    ///
    /// The share is taken of the ROOM, not of the request, because room is a side-wide measurement. Several
    /// clients filling one side each see the same room, and without the share each would supply all of it.
    /// </remarks>
    internal static int CapWaveToQuota(int requested, int remainingQuota)
    {
        if (remainingQuota == BattleFieldRoom.Unlimited) return requested;
        if (remainingQuota <= 0) return 0;

        return Math.Min(requested, remainingQuota);
    }

    /// <summary>
    /// How many more men THIS client may field on its side: its share of the side's allocation, less the men
    /// it already has standing there.
    /// </summary>
    /// <remarks>
    /// A private quota rather than a share of the contended leftovers. The previous rule - this client's share
    /// of the room still free on the side - reads zero for everyone the moment the side is full, and every
    /// share of zero is zero. That made fielding a race: as casualties opened room, whoever asked first took
    /// it, and the battle host asks constantly while a joining client asks every few seconds.
    ///
    /// A joining player lost that race for an entire battle: one troop fielded, his own hero, then "asked for
    /// 64, room for 0" every three seconds while holding 950 men in reserve. He had nothing of his own on the
    /// field, which is also why he had nothing to command.
    ///
    /// Counted from THIS supplier's own agents, not the side's, so another owner filling its quota can never
    /// consume ours. Unlimited passes straight through: it means no sizing exists yet, and deployment must not
    /// be short-changed.
    /// </remarks>
    public int RemainingFieldQuota()
    {
        var target = BattleFieldRoom.SideTarget(objectManager, MapEventId, Side);
        if (target == BattleFieldRoom.Unlimited) return BattleFieldRoom.Unlimited;

        ReportSizingSourceOnce(target);

        return Math.Max(0, OwnedShareOf(target) - CountMyTroopsOnField(Mission.Current));
    }

    /// <summary>
    /// Says once per battle which of the two strength figures the cap is being computed from.
    /// </summary>
    /// <remarks>
    /// The campaign's live count is used while it exists and the committed reserve only when it does not, and
    /// the two do not have to agree: the reserve is the opening headcount and is never extended, so a side
    /// reinforced mid-battle has moved on from it. Printing both, once, is what makes a battle that reinforced
    /// oddly readable afterwards without having to reproduce it - and it is the line that says out loud when a
    /// battle has lost its campaign event and is being sized from the reserve instead.
    /// </remarks>
    private void ReportSizingSourceOnce(int target)
    {
        if (sizingSourceReported) return;
        sizingSourceReported = true;

        var mapEvent = ResolveMapEvent();
        int campaignTotal = mapEvent?.GetMapEventSide(Side)?.TroopCount ?? -1;

        Logger.Information(
            "[TroopSupply] {MapEvent} side {Side}: target {Target}, sized from {Source} (campaign says {Campaign}, reserve holds {Reserve})",
            MapEventId, Side, target,
            campaignTotal > 0 ? "the campaign" : "the RESERVE - this battle has no live map event",
            campaignTotal, SideTotalTroops);
    }

    /// <summary>Live men on the field that came out of THIS supplier's reserve.</summary>
    /// <remarks>
    /// Matched by the party the troop was supplied from, which is the only link back to a supplier that
    /// survives replication and ownership changes. Guarded because it walks the agent list on the game thread
    /// and an agent mid-removal is not guaranteed to answer.
    /// </remarks>
    private int CountMyTroopsOnField(Mission mission)
    {
        var agents = mission?.Agents;
        if (agents == null) return 0;

        int count = 0;
        foreach (var agent in agents)
        {
            try
            {
                if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;
                if ((agent.Team?.Side ?? BattleSideEnum.None) != Side) continue;
                if (agent.Origin is not CoopAgentOrigin origin) continue;
                if (!ContainsParty(origin.MapEventPartyId)) continue;

                count++;
            }
            catch
            {
                // An agent that cannot answer is one we should not count; it is on its way off the field.
            }
        }
        return count;
    }

    // The battle this supplier serves, or null when it cannot be resolved this instant — which the caller must
    // read as "cannot verify the sizing", not as "there is no sizing".
    private MapEvent ResolveMapEvent()
        => objectManager != null && objectManager.TryGetObject<MapEvent>(MapEventId, out var mapEvent)
            ? mapEvent
            : null;

    // partyId is a MapEventParty object id (what the builder stored), not a MobileParty id. MapEventParty.Party
    // is the PartyBase the engine needs for the agent's team/combatant and player-command checks.
    private PartyBase ResolveParty(string partyId)
    {
        if (partyId != null
            && objectManager != null
            && objectManager.TryGetObject<MapEventParty>(partyId, out var mapEventParty))
            return mapEventParty?.Party;

        return null;
    }

    // [BR-073] Origin→supplier casualty feedback, called by this supplier's own CoopAgentOrigins (one-shot
    // per origin) when the removal prefix reports a wound/kill/rout. NumRemovedTroops is the engine's only
    // casualty input for reinforcements (NumberOfActiveTroops = spawned − removed), so without these the
    // wave gate never opens. ENGINE BOOKKEEPING ONLY — roster casualties remain single-sourced on the
    // network death path (MapEventParty.OnTroop*). Seed-scoped so a descriptor this supplier doesn't own
    // (a foreign or puppet seed) can never perturb this side's count — a side that locally spawned 0 must
    // never go negative and corrupt IsSideDepleted / the wave math. Locked: supply runs on the game thread
    // while replicated removals can arrive off it.
    public void OnTroopWounded(UniqueTroopDescriptor troopDescriptor)
    {
        lock (gate) { if (seedToPartyId.ContainsKey(troopDescriptor.UniqueSeed)) numWounded++; }
    }

    public void OnTroopKilled(UniqueTroopDescriptor troopDescriptor)
    {
        lock (gate) { if (seedToPartyId.ContainsKey(troopDescriptor.UniqueSeed)) numKilled++; }
    }

    public void OnTroopRouted(UniqueTroopDescriptor troopDescriptor, bool isOrderRetreat)
    {
        lock (gate) { if (seedToPartyId.ContainsKey(troopDescriptor.UniqueSeed)) numRouted++; }
    }

    public void OnTroopScoreHit(UniqueTroopDescriptor descriptor, BasicCharacterObject attackedCharacter, int damage, bool isFatal, bool isTeamKill, WeaponComponentData attackerWeapon) { }

    private IAgentOriginBase CreateOrigin(TroopReserveEntry entry, string partyId)
    {
        if (!TryResolveCharacter(entry, out var character))
        {
            Logger.Warning("[TroopSupply] {Side} could not resolve character {CharId} (seed={Seed}) — not spawning",
                Side, entry.CharacterId, entry.Seed);
            return null;
        }
        // CoopAgentOrigin carries the troop's party for ALL troops (SimpleAgentOrigin gives non-heroes a null
        // party → no team → no spawn) and the server's descriptor, so every client agrees on troop identity.
        // It also carries this supplier, so removals feed back into NumRemovedTroops (the engine's
        // reinforcement quota) — see OnTroopWounded/Killed/Routed above.
        var party = ResolveParty(partyId);
        var origin = new CoopAgentOrigin(character, party, -1, null, new UniqueTroopDescriptor(entry.Seed), partyId, this);
        if (party == null)
            Logger.Warning("[TroopSupply] {Side} origin char={Char} (isHero={Hero}) got NULL party — partyId {PartyId} unresolvable → no team / not player-commanded",
                Side, entry.CharacterId, character.IsHero, partyId);
        else if (character.IsHero)
            Logger.Information("[TroopSupply] {Side} HERO origin char={Char} party={Party} isMainParty={Main} underPlayersCmd={Cmd}",
                Side, entry.CharacterId, party.Name, party == PartyBase.MainParty, origin.IsUnderPlayersCommand);
        return origin;
    }

    // Heroes and regular troops alike are keyed by their CharacterObject id (hero CharacterObjects are
    // registered — CharacterObjectRegistry), so resolve uniformly; hero-ness is read from character.IsHero.
    private bool TryResolveCharacter(TroopReserveEntry entry, out CharacterObject character)
    {
        character = null;
        return objectManager != null && objectManager.TryGetObject<CharacterObject>(entry.CharacterId, out character);
    }
}
