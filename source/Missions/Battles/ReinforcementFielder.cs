using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.Heroes.Extensions;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.Messages;
using GameInterface.Services.MapEvents.TroopSupply;
using GameInterface.Services.MapEvents.TroopSupply.Messages;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using Missions.Messages;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace Missions.Battles;

/// <summary>
/// Fields new AI parties that join a live battle and recovers newly-owned reserve parties after host migration
/// when no old-host agents arrived to adopt. Both paths use the local spawn pipeline, so troops are registered,
/// broadcast as puppets, casualty-attributed, assigned to formations, and ordered to charge.
/// </summary>
public interface IReinforcementFielder : IDisposable
{
    /// <summary>[Network thread] Snapshot the current reserves before this host receives newly-owned parties.</summary>
    void PrepareForReserveOwnershipExpansion();

    /// <summary>[Game thread] Field queued migration reserves as battle capacity becomes available.</summary>
    void Tick();
}

/// <inheritdoc cref="IReinforcementFielder"/>
public class ReinforcementFielder : IReinforcementFielder
{
    private static readonly ILogger Logger = LogManager.GetLogger<ReinforcementFielder>();

    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;
    private readonly ICoopMissionComponent coopMissionComponent;
    private readonly IBattleSession session;
    private readonly IBattleDeploymentCoordinator deployment;
    private readonly IAgentFormationAssigner formationAssigner;
    private readonly ICasualtyAttributionMap casualties;
    private readonly IBattleAgentBudget agentBudget;

    // [Host] Map-event party ids we have already fielded as mid-battle reinforcements, so a repeated involved-
    // parties broadcast for the same party doesn't double-spawn it.
    private readonly HashSet<string> reinforcedParties = new HashSet<string>();
    private readonly HashSet<string> pendingReinforcementParties = new HashSet<string>();

    /// <summary>A reinforcement party's troops still waiting for engine agent capacity (BR-110): fielding
    /// stops at the render limit and <see cref="Tick"/> spawns the remainder as removals free slots.</summary>
    private sealed class PendingReinforcementParty
    {
        public readonly BattleSideEnum Side;
        public readonly string PartyId;
        public readonly Queue<CoopAgentOrigin> Origins;

        public PendingReinforcementParty(BattleSideEnum side, string partyId, Queue<CoopAgentOrigin> origins)
        {
            Side = side;
            PartyId = partyId;
            Origins = origins;
        }
    }

    // [Host] Reinforcement troops withheld at the engine agent limit, fielded by Tick as capacity frees.
    private readonly List<PendingReinforcementParty> pendingReinforcements = new List<PendingReinforcementParty>();

    /// <summary>Reserve state captured before the promoted host requests its expanded ownership.</summary>
    private sealed class MigrationReserveSnapshot
    {
        public readonly int DefenderRevision;
        public readonly int AttackerRevision;
        public readonly HashSet<string> KnownPartyIds;

        public MigrationReserveSnapshot(int defenderRevision, int attackerRevision, HashSet<string> knownPartyIds)
        {
            DefenderRevision = defenderRevision;
            AttackerRevision = attackerRevision;
            KnownPartyIds = knownPartyIds;
        }
    }

    /// <summary>A newly-owned party whose authoritative reserve needs local fielding.</summary>
    private sealed class RecoveryParty
    {
        public readonly CoopTroopSupplier Supplier;
        public readonly string PartyId;
        public readonly Queue<CoopAgentOrigin> Origins;

        public RecoveryParty(CoopTroopSupplier supplier, string partyId, Queue<CoopAgentOrigin> origins)
        {
            Supplier = supplier;
            PartyId = partyId;
            Origins = origins;
        }
    }

    private readonly object migrationGate = new object();
    private MigrationReserveSnapshot pendingMigration;
    private readonly List<RecoveryParty>[] recoveryParties =
    {
        new List<RecoveryParty>(),
        new List<RecoveryParty>(),
    };
    private readonly int[] recoveryCursors = new int[2];
    private readonly HashSet<string> recoveryPartyIds = new HashSet<string>();

    public ReinforcementFielder(
        IMessageBroker messageBroker,
        IObjectManager objectManager,
        ICoopMissionComponent coopMissionComponent,
        IBattleSession session,
        IBattleDeploymentCoordinator deployment,
        IAgentFormationAssigner formationAssigner,
        ICasualtyAttributionMap casualties,
        IBattleAgentBudget agentBudget)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
        this.coopMissionComponent = coopMissionComponent;
        this.session = session;
        this.deployment = deployment;
        this.formationAssigner = formationAssigner;
        this.casualties = casualties;
        this.agentBudget = agentBudget;

        // [Host] A new AI party joining the live battle is fielded through our own spawn path (reinforcements).
        messageBroker.Subscribe<NetworkAddInvolvedParties>(Handle_ReinforcementPartiesAdded);
        messageBroker.Subscribe<BattleHostMigrated>(Handle_BattleHostMigrated);
        messageBroker.Subscribe<NetworkBattleReserveOwnershipExpanded>(Handle_ReserveOwnershipExpanded);
    }

    public void Dispose()
    {
        messageBroker.Unsubscribe<NetworkAddInvolvedParties>(Handle_ReinforcementPartiesAdded);
        messageBroker.Unsubscribe<BattleHostMigrated>(Handle_BattleHostMigrated);
        messageBroker.Unsubscribe<NetworkBattleReserveOwnershipExpanded>(Handle_ReserveOwnershipExpanded);
    }

    public void Tick()
    {
        if (!session.IsLocalHost || Mission.Current == null) return;

        try
        {
            if (deployment.IsActivated)
                FieldPendingReinforcementParties();

            if (!deployment.IsCommitted) return;

            TryQueueMigrationReserves();
            FieldMigrationReserves();
            FieldPendingReinforcements();
        }
        catch (Exception e)
        {
            Logger.Error(e, "[BattleSync] Failed to field battle reserves");
        }
    }

    // The promotion message precedes the request for the successor's expanded reserve. Snapshot the two
    // supplier revisions and known parties so Tick can wait for both replies and identify only newly-owned
    // parties; ordinary reserve resends and initial reserves never enter the recovery path.
    private void Handle_BattleHostMigrated(MessagePayload<BattleHostMigrated> payload)
    {
        if (payload.What.MapEventId != session.InstanceId) return;
        PrepareForReserveOwnershipExpansion();
    }

    private void Handle_ReserveOwnershipExpanded(MessagePayload<NetworkBattleReserveOwnershipExpanded> payload)
    {
        if (payload.What.MapEventId != session.InstanceId) return;
        PrepareForReserveOwnershipExpansion();
    }

    public void PrepareForReserveOwnershipExpansion()
    {
        if (!TryGetSuppliers(out var defenderSupplier, out var attackerSupplier)) return;

        var knownPartyIds = new HashSet<string>();
        AddPartyIds(defenderSupplier, knownPartyIds);
        AddPartyIds(attackerSupplier, knownPartyIds);

        var snapshot = new MigrationReserveSnapshot(
            defenderSupplier.ReserveRevision,
            attackerSupplier.ReserveRevision,
            knownPartyIds);

        lock (migrationGate)
        {
            // A host-migration signal and the server's ownership-expansion signal can describe the same
            // refresh. Keep the earlier snapshot until both side replies have arrived.
            if (pendingMigration == null)
                pendingMigration = snapshot;
        }
    }

    private static void AddPartyIds(CoopTroopSupplier supplier, HashSet<string> partyIds)
    {
        foreach (var (partyId, _) in supplier.GetRemainingByParty())
            partyIds.Add(partyId);
    }

    // Wait until both reliable-ordered reserve replies have replaced their suppliers. Adoption was queued before
    // the request, so all replayed old-host agents are registered under us before this game-thread scan runs.
    private void TryQueueMigrationReserves()
    {
        MigrationReserveSnapshot snapshot;
        lock (migrationGate)
            snapshot = pendingMigration;

        if (snapshot == null) return;
        if (!TryGetSuppliers(out var defenderSupplier, out var attackerSupplier)) return;
        if (defenderSupplier.ReserveRevision <= snapshot.DefenderRevision) return;
        if (attackerSupplier.ReserveRevision <= snapshot.AttackerRevision) return;

        lock (migrationGate)
        {
            if (!ReferenceEquals(pendingMigration, snapshot)) return;
            pendingMigration = null;
        }

        int queuedParties = QueueMissingParties(defenderSupplier, snapshot.KnownPartyIds);
        queuedParties += QueueMissingParties(attackerSupplier, snapshot.KnownPartyIds);

        Logger.Information("[BattleSync] Migration reserve refresh queued {Count} party/parties with no live adopted agents", queuedParties);
    }

    private int QueueMissingParties(CoopTroopSupplier supplier, HashSet<string> knownPartyIds)
    {
        int queued = 0;
        foreach (var (partyId, serverRemaining) in supplier.GetRemainingByParty())
        {
            if (knownPartyIds.Contains(partyId)) continue;
            if (!recoveryPartyIds.Add(partyId)) continue;

            if (!TryBuildRecoveryParty(supplier, partyId, out var recovery, out var activeRoster, out var liveAgents))
            {
                recoveryPartyIds.Remove(partyId);
                continue;
            }

            recoveryParties[(int)supplier.Side].Add(recovery);
            queued++;
            Logger.Information("[BattleSync] Migration reserve party {Party}: roster active={Roster}, locally present={Live}, queued={Queued}, server remaining={Remaining}",
                partyId, activeRoster, liveAgents, recovery.Origins.Count, serverRemaining);
        }
        return queued;
    }

    private bool TryBuildRecoveryParty(
        CoopTroopSupplier supplier,
        string partyId,
        out RecoveryParty recovery,
        out int activeRoster,
        out int liveAgents)
    {
        recovery = null;
        activeRoster = 0;
        liveAgents = 0;
        if (!objectManager.TryGetObjectWithLogging<MapEventParty>(partyId, out var mapEventParty)) return false;
        if (mapEventParty._roster == null)
        {
            Logger.Warning("[BattleSync] Migration reserve party {Party} has no flattened roster; recovery deferred", partyId);
            return false;
        }

        var activeByCharacter = new Dictionary<string, int>();
        var recoverableSeeds = new HashSet<int>();
        foreach (var element in mapEventParty._roster)
        {
            if (element.IsKilled || element.IsWounded || element.IsRouted || element.Troop == null) continue;
            if (!objectManager.TryGetId(element.Troop, out var characterId)) continue;

            Increment(activeByCharacter, characterId);
            recoverableSeeds.Add(element.Descriptor.UniqueSeed);
            activeRoster++;
        }

        var liveByCharacter = new Dictionary<string, int>();
        foreach (var controllerId in coopMissionComponent.AgentRegistry.GetControllerIds())
        {
            foreach (var info in coopMissionComponent.AgentRegistry.GetAgents(controllerId))
            {
                var agent = info.Agent;
                if (agent == null || agent.IsMount || !agent.IsActive()) continue;

                var attribution = casualties.GetOrDefault(info.AgentId);
                if (attribution.MapEventPartyId != partyId) continue;
                if (attribution.TroopCharacterId != null)
                    Increment(liveByCharacter, attribution.TroopCharacterId);
                recoverableSeeds.Remove(attribution.TroopSeed);
                liveAgents++;
            }
        }

        var neededByCharacter = CalculateMissingByCharacter(activeByCharacter, liveByCharacter);
        var origins = supplier.ClaimRecoveryTroops(partyId, neededByCharacter, recoverableSeeds);
        if (origins.Count == 0) return false;

        recovery = new RecoveryParty(supplier, partyId, new Queue<CoopAgentOrigin>(origins));
        return true;
    }

    private static void Increment(Dictionary<string, int> counts, string key)
        => counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;

    /// <summary>How many living roster troops are absent locally, grouped by character.</summary>
    public static Dictionary<string, int> CalculateMissingByCharacter(
        IReadOnlyDictionary<string, int> activeRoster,
        IReadOnlyDictionary<string, int> liveAgents)
    {
        var missing = new Dictionary<string, int>();
        foreach (var pair in activeRoster)
        {
            liveAgents.TryGetValue(pair.Key, out var live);
            int count = Math.Max(0, pair.Value - live);
            if (count > 0)
                missing[pair.Key] = count;
        }
        return missing;
    }

    private bool TryGetSuppliers(out CoopTroopSupplier defenderSupplier, out CoopTroopSupplier attackerSupplier)
    {
        defenderSupplier = null;
        attackerSupplier = null;
        foreach (var supplier in CoopTroopSupplierRegistry.GetSuppliers(session.InstanceId))
        {
            if (supplier.Side == BattleSideEnum.Defender) defenderSupplier = supplier;
            else if (supplier.Side == BattleSideEnum.Attacker) attackerSupplier = supplier;
        }
        return defenderSupplier != null && attackerSupplier != null;
    }

    private void FieldMigrationReserves()
    {
        if (recoveryParties[(int)BattleSideEnum.Defender].Count == 0
            && recoveryParties[(int)BattleSideEnum.Attacker].Count == 0)
            return;

        var mission = Mission.Current;
        var spawnLogic = mission.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();
        if (spawnLogic == null || !TryGetSuppliers(out var defenderSupplier, out var attackerSupplier)) return;

        var settings = spawnLogic.SpawnSettings;
        var targets = RecoveryTargets.Calculate(
            defenderSupplier.TotalTroops,
            attackerSupplier.TotalTroops,
            spawnLogic.BattleSize,
            settings.MaximumBattleSideRatio,
            settings.DefenderAdvantageFactor);

        // Side-wide target MUST be compared against a side-wide count. Subtracting only the agents THIS client
        // owns treats every other client's troops as missing and re-fields them, so a side already at its
        // target keeps growing by however much of it belongs to someone else. Measured live as a field of 485
        // on a battle sized for 400.
        CountActiveHumansPerSide(mission, out var activeDefenders, out var activeAttackers);
        var formations = new HashSet<Formation>();
        int spawned = FieldRecoverySide(BattleSideEnum.Defender, targets.Defenders - activeDefenders, formations);
        spawned += FieldRecoverySide(BattleSideEnum.Attacker, targets.Attackers - activeAttackers, formations);

        ChargeFormations(formations);

        if (spawned > 0)
            Logger.Information("[BattleSync] Fielded {Count} migration reserve troop(s) toward active targets Defender={Def}, Attacker={Atk}",
                spawned, targets.Defenders, targets.Attackers);
    }

    private void CountActiveOwnedHumans(out int defenders, out int attackers)
    {
        defenders = 0;
        attackers = 0;
        foreach (var info in coopMissionComponent.AgentRegistry.GetAgents(session.OwnControllerId))
        {
            var agent = info.Agent;
            if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;

            var side = agent.Team?.Side ?? BattleSideEnum.None;
            if (side == BattleSideEnum.Defender) defenders++;
            else if (side == BattleSideEnum.Attacker) attackers++;
        }
    }

    // Round-robin by party so every missing army party gets represented before one large reserve consumes the
    // whole side's active allocation. Exhausted parties leave the queue; the rest refill future casualty slots.
    private int FieldRecoverySide(BattleSideEnum side, int available, HashSet<Formation> formations)
    {
        if (available <= 0) return 0;

        var team = BattleTeams.Resolve(side);
        if (team == null) return 0;

        int sideIndex = (int)side;
        var parties = recoveryParties[sideIndex];
        int spawned = 0;
        while (available > 0 && parties.Count > 0)
        {
            if (recoveryCursors[sideIndex] >= parties.Count)
                recoveryCursors[sideIndex] = 0;

            int index = recoveryCursors[sideIndex];
            var recovery = parties[index];
            if (!recovery.Supplier.ContainsParty(recovery.PartyId))
            {
                recovery.Origins.Clear();
                Logger.Information("[BattleSync] Cancelled migration recovery for party {Party}; it left this host's reserve scope", recovery.PartyId);
            }

            // BR-110: stop at the engine agent limit, sized to the next origin's slots (mounted = 2); the
            // recovery queues persist, so the next Tick resumes fielding as removals free capacity. An empty
            // queue (null next) skips the check so the exhausted party is still cleaned up below.
            var nextOrigin = recovery.Origins.Count > 0 ? recovery.Origins.Peek() : null;
            if (nextOrigin != null && !agentBudget.HasCapacityFor(Mission.Current, SlotsForOrigin(nextOrigin))) break;

            var origin = recovery.Origins.Count > 0 ? recovery.Origins.Dequeue() : null;
            bool exhausted = recovery.Origins.Count == 0;

            if (origin != null && HasLivePartySeed(recovery.PartyId, origin.UniqueSeed))
            {
                Logger.Information("[BattleSync] Skipped migration recovery seed {Seed} for party {Party}; a late replay already registered it",
                    origin.UniqueSeed, recovery.PartyId);
            }
            else if (origin != null)
            {
                var agent = SpawnReinforcementTroop(Mission.Current, team, origin);
                if (agent?.Formation != null) formations.Add(agent.Formation);
                spawned++;
                available--;
            }

            if (exhausted)
            {
                parties.RemoveAt(index);
                recoveryPartyIds.Remove(recovery.PartyId);
                if (recoveryCursors[sideIndex] >= parties.Count)
                    recoveryCursors[sideIndex] = 0;
            }
            else
            {
                recoveryCursors[sideIndex]++;
            }
        }
        return spawned;
    }

    private bool HasLivePartySeed(string partyId, int troopSeed)
    {
        foreach (var controllerId in coopMissionComponent.AgentRegistry.GetControllerIds())
        {
            foreach (var info in coopMissionComponent.AgentRegistry.GetAgents(controllerId))
            {
                if (info.Agent == null || !info.Agent.IsActive()) continue;
                var attribution = casualties.GetOrDefault(info.AgentId);
                if (attribution.MapEventPartyId == partyId && attribution.TroopSeed == troopSeed)
                    return true;
            }
        }
        return false;
    }

    /// <summary>Joint active troop targets using the same battle-size allocation as native Init.</summary>
    public readonly struct RecoveryTargets
    {
        public readonly int Defenders;
        public readonly int Attackers;

        public RecoveryTargets(int defenders, int attackers)
        {
            Defenders = defenders;
            Attackers = attackers;
        }

        // Forwards to the shared rule. The split used to be spelled out here, but CoopTroopSupplier now has to
        // reach the same answer to cap a reinforcement wave, and it lives in an assembly that cannot see this
        // one. Two copies of a battle-size split that disagreed by a man would be a very quiet bug.
        public static RecoveryTargets Calculate(int defenderTotal, int attackerTotal, int battleSize,
            float maximumSideRatio, float defenderAdvantageFactor)
        {
            var targets = BattleSizeTargets.Calculate(defenderTotal, attackerTotal, battleSize,
                maximumSideRatio, defenderAdvantageFactor);
            return new RecoveryTargets(targets.Defenders, targets.Attackers);
        }
    }

    // [Host] Preserve involved-party messages until the mission is activated. World and battle messages use
    // different channels, so a party can arrive first and must not be lost while deployment is still frozen.
    private void Handle_ReinforcementPartiesAdded(MessagePayload<NetworkAddInvolvedParties> payload)
    {
        if (!session.IsLocalHost) return;
        if (payload.What.MapEventId != session.InstanceId) return;

        var partyIds = payload.What.MapEventPartyIds;
        if (partyIds == null || partyIds.Length == 0) return;

        GameThread.RunSafe(() =>
        {
            foreach (var partyId in partyIds)
                if (!string.IsNullOrEmpty(partyId) && !reinforcedParties.Contains(partyId))
                    pendingReinforcementParties.Add(partyId);

            if (deployment.IsActivated && Mission.Current != null)
                FieldPendingReinforcementParties();
        });
    }

    /// <summary>
    /// [Host, game thread] Queues any party sitting in the battle that nothing is fielding.
    /// </summary>
    /// <remarks>
    /// The involved-parties broadcast is not a complete account of who is in the battle: it is only sent while
    /// the AI-join window is open (<c>MapEventPatches.Postfix_AddInvolvedPartyInternal</c>), and reserves are
    /// built once at entry and never extended. A party that joins a live battle after that window is therefore
    /// in the map event - counted in the odds, listed on the scoreboard's side total - while no reserve holds
    /// it and no broadcast ever named it, so nobody spawns a single one of its men.
    ///
    /// Observed live: a sally-out with twenty attacker parties fielding eight troops between them, the reserve
    /// having been built from one party.
    ///
    /// Sweeping the map event itself closes that gap without relying on the broadcast: anything on either side
    /// that no supplier holds and that has not already been reinforced gets queued here, and the ordinary
    /// allowance then drips it in. Player parties are skipped - their own client fields them.
    /// </remarks>
    private void QueueUnreservedMapEventParties()
    {
        if (!objectManager.TryGetObject<MapEvent>(session.InstanceId, out var mapEvent)) return;
        if (mapEvent.AttackerSide == null || mapEvent.DefenderSide == null) return;

        // Iterated per SIDE because the reserves are keyed by MapEventParty, which is what a side's list holds;
        // MapEvent.InvolvedParties yields the underlying PartyBase and would not match those ids.
        QueueUnreservedPartiesOnSide(mapEvent.AttackerSide);
        QueueUnreservedPartiesOnSide(mapEvent.DefenderSide);
    }

    private void QueueUnreservedPartiesOnSide(MapEventSide side)
    {
        foreach (var mapEventParty in side.Parties)
        {
            var party = mapEventParty?.Party;
            if (party == null) continue;
            // A player's own client fields its party, so never sweep one up here. Tested against the player
            // REGISTRY rather than the hero flag: a registered player party whose leader hero is not wired up
            // would otherwise slip through and be fielded twice - once by its owner, once by this sweep.
            if (party.LeaderHero?.IsPlayerHero() == true) continue;
            if (party.MobileParty != null && party.MobileParty.IsPlayerParty()) continue;

            if (!objectManager.TryGetId(mapEventParty, out var partyId)) continue;
            if (reinforcedParties.Contains(partyId) || pendingReinforcementParties.Contains(partyId)) continue;
            if (IsSupplierParty(partyId)) continue;

            Logger.Information("[BattleSync] Party {Party} is in battle {MapEvent} but held by no reserve; queueing it to be fielded",
                partyId, session.InstanceId);
            pendingReinforcementParties.Add(partyId);
        }
    }

    private void FieldPendingReinforcementParties()
    {
        QueueUnreservedMapEventParties();

        if (pendingReinforcementParties.Count == 0) return;

        foreach (var partyId in new List<string>(pendingReinforcementParties))
        {
            if (reinforcedParties.Contains(partyId) || IsSupplierParty(partyId))
            {
                pendingReinforcementParties.Remove(partyId);
                continue;
            }

            // Registration is applied on the game thread too. Retain an unresolved id for a later tick.
            if (!objectManager.TryGetObject<MapEventParty>(partyId, out var mapEventParty)) continue;

            var party = mapEventParty?.Party;
            var mapEvent = party?.MapEventSide?.MapEvent;
            if (party == null || mapEvent == null || !objectManager.TryGetId(mapEvent, out var mapEventId)
                || mapEventId != session.InstanceId || party.LeaderHero?.IsPlayerHero() == true)
            {
                pendingReinforcementParties.Remove(partyId);
                continue;
            }

            pendingReinforcementParties.Remove(partyId);
            reinforcedParties.Add(partyId);
            SpawnReinforcementParty(party, partyId);
        }
    }

    // Whether a party is one of the initial reserves the troop supplier already provides, so the native spawn
    // logic spawns it and we must not also spawn it here.
    private bool IsSupplierParty(string mapEventPartyId)
    {
        foreach (var supplier in CoopTroopSupplierRegistry.GetSuppliers(session.InstanceId))
            foreach (var (partyId, _) in supplier.GetSuppliedByParty())
                if (partyId == mapEventPartyId) return true;
        return false;
    }

    // [Host, game thread] Field a newly-joined AI party: spawn each of its able troops AI-controlled at the
    // side's default reinforcement frame, then put the formations they land in on a charge. Capture is NOT
    // suppressed, so each spawn flows through the owner-side capture pipeline (registered under us, broadcast
    // to peers as puppets, casualty attributed from the origin) — the same pipeline the initial troops use.
    // Fielding stops at the engine agent limit (BR-110); the remainder is queued and Tick spawns it as
    // removals free capacity.
    private void SpawnReinforcementParty(PartyBase party, string mapEventPartyId)
    {
        var team = BattleTeams.Resolve(party.Side);
        if (team == null)
        {
            Logger.Warning("[BattleSync] No team for side {Side}; cannot field reinforcement party {Party}", party.Side, mapEventPartyId);
            return;
        }

        var origins = new Queue<CoopAgentOrigin>();
        foreach (var element in party.MemberRoster.GetTroopRoster())
        {
            var character = element.Character;
            if (character == null) continue;

            int able = element.Number - element.WoundedNumber;
            for (int i = 0; i < able; i++)
                origins.Enqueue(new CoopAgentOrigin(character, party, -1, null, new UniqueTroopDescriptor(MBRandom.RandomInt(int.MaxValue))));
        }

        var pending = new PendingReinforcementParty(party.Side, mapEventPartyId, origins);
        int spawned = FieldPendingParty(pending);

        Logger.Information("[BattleSync] Fielded reinforcement party {Party}: spawned {Count} troop(s)", mapEventPartyId, spawned);

        if (pending.Origins.Count > 0)
        {
            pendingReinforcements.Add(pending);
            Logger.Information("[BattleSync] Withheld {Count} reinforcement troop(s) for party {Party} at the engine agent limit (BR-110); they spawn as capacity frees",
                pending.Origins.Count, mapEventPartyId);
        }

        if (spawned > 0)
        {
            var troopText = spawned > 1 ? $"{spawned} troops" : $"{spawned} troop";
            InformationManager.DisplayMessage(new InformationMessage($"Reinforcements have arrived: {party.Name} ({troopText})"));
        }
    }

    // [Host, game thread] Field a pending party's troops while the engine has agent capacity (BR-110). The
    // capacity is re-read per spawn, so implicitly spawned cavalry mounts count against it as they appear.
    private int FieldPendingParty(PendingReinforcementParty pending)
    {
        var mission = Mission.Current;
        var team = BattleTeams.Resolve(pending.Side);
        if (team == null) return 0;

        // The battle size caps how many men a side may have on the field at once, and this path spawns
        // DIRECTLY rather than through the engine's spawn logic, so nothing else applies that cap here. A
        // party joining a live siege therefore put its whole roster on the field in one go - 1400 men at once
        // in the reported case - which is neither what the battle was sized for nor what vanilla does: there a
        // joining party goes into the side's pool and MissionAgentSpawnLogic feeds it in as casualties make
        // room. Whatever does not fit stays queued and Tick fields it exactly that way.
        int allowance = SideFieldingAllowance(pending.Side);

        var formations = new HashSet<Formation>();
        int spawned = 0;
        // BR-110: size the capacity check to the NEXT origin's slots (mounted = rider + horse = 2), so a cavalry
        // reinforcement with a single slot free is deferred rather than pushing the mission to 2001.
        while (pending.Origins.Count > 0
               && spawned < allowance
               && agentBudget.HasCapacityFor(mission, SlotsForOrigin(pending.Origins.Peek())))
        {
            var origin = pending.Origins.Dequeue();
            var agent = SpawnReinforcementTroop(mission, team, origin);
            if (agent?.Formation != null) formations.Add(agent.Formation);
            spawned++;
        }

        ChargeFormations(formations);
        return spawned;
    }

    /// <summary>
    /// [Host, game thread] How many more men this side may put on the field before it reaches the battle-size
    /// allocation the engine sized the battle to.
    /// </summary>
    /// <remarks>
    /// Sized from the battle's LIVE strength - what each side actually has in the map event now - not from the
    /// reserve totals captured when the battle started. Those totals are frozen: reserves are built once, at
    /// entry/election, and never extended, so a side that has since been reinforced still measures at its
    /// opening headcount. Sizing from them bounds a side by the men it happened to start with, which is how a
    /// relief force of twelve lords ends up unable to field anyone at all.
    ///
    /// Counts EVERY live human on the side, not just the ones this client owns: the cap is about how crowded
    /// the field is, and another player's troops occupy it just the same.
    ///
    /// No spawn logic or no map event means nothing has sized this battle, so there is no allocation to
    /// respect and the render budget remains the only limit - the behaviour before this cap existed.
    /// </remarks>
    private int SideFieldingAllowance(BattleSideEnum side)
    {
        var mission = Mission.Current;
        var spawnLogic = mission?.GetMissionBehavior<DefaultBattleMissionAgentSpawnLogic>();

        // Two different unknowns, which must NOT be answered the same way.
        //
        // No spawn logic at all means this mission has no battle sizing to respect - there is no limit in
        // existence, so imposing one would be inventing a rule the battle never had. Headless and mock
        // missions run this way deliberately.
        if (spawnLogic == null) return int.MaxValue;

        // An unresolvable map event is the opposite: the sizing exists, we simply cannot read the numbers that
        // define it this instant. That must FAIL CLOSED. It used to return int.MaxValue too, and in the window
        // where the map event would not resolve the fielder had no cap at all - 840 troops in a single minute,
        // the attacker side reaching 1,072 on a battle sized for 400. Refusing to field for a moment costs a
        // second of reinforcement; guessing cost the battle.
        if (!objectManager.TryGetObject<MapEvent>(session.InstanceId, out var mapEvent))
        {
            Logger.Warning("[BattleSync] Cannot resolve map event {MapEventId}; refusing to field reinforcements until it resolves", session.InstanceId);
            return 0;
        }

        // The engine's own opening wave has not finished landing, so the field is not yet what it is about to
        // be. Measuring "who is standing here" now and topping up the difference means both fillers rush the
        // same empty field and the side ends up with the engine's wave PLUS ours. Most visible right after a
        // round restart, which empties the field deliberately: measured 611 agents on a battle sized for ~400.
        // Waiting costs nothing - the engine is already putting that opening wave out.
        if (!spawnLogic.IsInitialSpawnOver) return 0;

        var settings = spawnLogic.SpawnSettings;
        var targets = RecoveryTargets.Calculate(
            LiveSideStrength(mapEvent, BattleSideEnum.Defender),
            LiveSideStrength(mapEvent, BattleSideEnum.Attacker),
            spawnLogic.BattleSize,
            settings.MaximumBattleSideRatio,
            settings.DefenderAdvantageFactor);

        // Room left on the side, counting EVERY live human on it whoever owns them. This is the meter that
        // matches what this method authorises: the fielder spawns agents for any party on the side, so the only
        // count that falls as it spends is a side-wide one.
        CountActiveHumansPerSide(mission, out var activeDefenders, out var activeAttackers);
        int sideRoom = side == BattleSideEnum.Defender
            ? RemainingFieldingAllowance(targets.Defenders, activeDefenders)
            : RemainingFieldingAllowance(targets.Attackers, activeAttackers);

        // This client's own quota as well, so both spawners on this machine - the engine's wave path through
        // CoopTroopSupplier and this fielder - cannot between them exceed the share this client is entitled to,
        // and cannot crowd out another client filling its own.
        foreach (var supplier in CoopTroopSupplierRegistry.GetSuppliers(session.InstanceId))
            if (supplier.Side == side)
                return EffectiveAllowance(sideRoom, supplier.RemainingFieldQuota(mapEvent));

        // No supplier for this side means nobody else is drawing against it here; the side-wide room is the
        // only bound, which is the behaviour before quotas existed.
        return sideRoom;
    }

    /// <summary>
    /// How many men may be fielded now: the tighter of the side's remaining room and this client's own quota.
    /// </summary>
    /// <remarks>
    /// The quota alone was not a usable meter for THIS path, and the mismatch put 592 men on a field sized for
    /// 400. <c>RemainingFieldQuota</c> subtracts <c>CountMyTroopsOnField</c>, which counts only agents whose
    /// origin party is in the supplier's own reserve - but the fielder, by explicit guard, only ever fields
    /// parties that are NOT the supplier's (<c>IsSupplierParty</c> skips those at both the queueing and the
    /// fielding site). Every troop it spawned was therefore invisible to the meter it was charged against, so
    /// the quota read the same number for every party in a batch and each one was granted the full allowance:
    /// six parties fielded inside one second, 507 men, six times the same allowance.
    ///
    /// Pairing it with the side-wide room fixes that without giving up what the quota is for. Side-wide room
    /// counts every live human on the side, so it falls as this batch spawns and the NEXT party in the same
    /// batch sees a smaller number - which is why no running budget has to be threaded through the loop; the
    /// meter simply tells the truth each time it is read. The quota still bounds this client's share so two
    /// clients filling one side cannot crowd each other out.
    ///
    /// Taking the tighter of the two means a side already at its target fields nobody, however much personal
    /// quota is left. That is the intended outcome: the men stay queued and arrive as casualties make room,
    /// which is what vanilla does and what the battle was sized for.
    /// </remarks>
    internal static int EffectiveAllowance(int sideRoom, int ownQuota)
        => Math.Max(0, Math.Min(sideRoom, ownQuota));

    /// <summary>Men a side currently has in the battle, including parties that joined after it began.</summary>
    private static int LiveSideStrength(MapEvent mapEvent, BattleSideEnum side)
        => mapEvent?.GetMapEventSide(side)?.TroopCount ?? 0;

    /// <summary>Room left on a side: its battle-size target less what is already standing on the field.</summary>
    internal static int RemainingFieldingAllowance(int sideTarget, int activeOnSide)
        => Math.Max(0, sideTarget - activeOnSide);

    /// <summary>Live human agents per side, whoever owns them — replicated puppets included.</summary>
    private static void CountActiveHumansPerSide(Mission mission, out int defenders, out int attackers)
    {
        defenders = 0;
        attackers = 0;

        var agents = mission?.Agents;
        if (agents == null) return;

        foreach (var agent in agents)
        {
            if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;

            var side = agent.Team?.Side ?? BattleSideEnum.None;
            if (side == BattleSideEnum.Defender) defenders++;
            else if (side == BattleSideEnum.Attacker) attackers++;
        }
    }

    // BR-110: render slots a reinforcement origin consumes when spawned — a mounted troop spawns a rider and a
    // horse (2), everyone else 1. The shared budget reads the same equipment SpawnReinforcementTroop spawns
    // from; a null origin keeps its historical rider-only cost (call sites never pass one).
    private int SlotsForOrigin(CoopAgentOrigin origin)
        => origin == null ? 1 : agentBudget.SlotsForOrigin(origin);

    // [Host, game thread] Field reinforcement troops withheld at the engine agent limit (BR-110) as removals
    // free capacity.
    private void FieldPendingReinforcements()
    {
        // EVERY queued party is offered a turn, and the queue rotates. Both matter, and the loop used to do
        // neither: it stopped at the first party that still had men waiting, on the reasoning that a global
        // render limit which blocks one party blocks them all.
        //
        // That reasoning stopped holding once fielding became capped PER SIDE. An attacker-side party sitting
        // at its cap then blocked a defender-side party with room, and - worse - the party at the head of the
        // queue took every slot that ever opened, forever. Measured live: the head party fielded 192 men one at
        // a time over a whole battle while five parties behind it fielded ZERO, 497 men who never left the
        // queue. On the scoreboard they are the lords listed with no kills, no losses and a full roster, and it
        // is the same starvation that left a player who joined mid-battle with an army he could not command.
        DrainQueue(pendingReinforcements, pending =>
        {
            int spawned = FieldPendingParty(pending);
            if (spawned > 0)
                Logger.Information("[BattleSync] Fielded {Count} deferred reinforcement troop(s) for party {Party}", spawned, pending.PartyId);
            return pending.Origins.Count == 0;
        });
    }

    /// <summary>
    /// Offers every queued item a turn, drops the ones that finish, and rotates whoever went first to the back.
    /// </summary>
    /// <param name="queue">The queue, modified in place.</param>
    /// <param name="serve">Serves one item; returns true when that item is finished and should be dropped.</param>
    /// <remarks>
    /// Rotation is not cosmetic. Each turn re-reads how much room the field has, so whoever is served first
    /// takes whatever room exists and everyone behind them sees none - visiting every item is necessary but not
    /// sufficient. Moving the head to the back makes "first" a different item each pass, which is the whole
    /// difference between a queue and a pecking order.
    /// </remarks>
    internal static void DrainQueue<T>(List<T> queue, Func<T, bool> serve)
    {
        if (queue.Count == 0) return;

        for (int i = 0; i < queue.Count;)
        {
            if (serve(queue[i])) queue.RemoveAt(i);
            else i++;
        }

        if (queue.Count < 2) return;

        var head = queue[0];
        queue.RemoveAt(0);
        queue.Add(head);
    }

    /// <summary>
    /// What a formation reinforcements just joined should be doing.
    /// </summary>
    /// <remarks>
    /// A coop battle has no general commanding formations, so a formation left merely AI-controlled stands
    /// idle - that is why this path used to issue a flat charge. The charge was worse than the idling it fixed:
    /// it was applied to EVERY formation reinforcements landed in, unconditionally, and it never expired.
    ///
    /// A player who joined a defensive battle therefore had his men ordered to charge the moment they arrived,
    /// while the defender who started the battle kept the hold order from deployment. Once both players were
    /// down and nobody was issuing orders, those stale orders were the whole of the AI's intent: the joiner's
    /// troops ran at the enemy alone and were cut down piecemeal while the host's stood at the back. It reads
    /// like the joiner is on the wrong side; he is not, he is simply the only one who was told to attack.
    ///
    /// So a charge is now the LAST resort rather than the first: a formation that already has a posture keeps
    /// it, a formation with none copies whatever the rest of its side is doing, and only a side with no posture
    /// at all charges. That keeps the original invariant - reinforcements never stand idle - without inventing
    /// an intent nobody expressed.
    /// </remarks>
    internal enum ReinforcementPosture
    {
        /// <summary>A living player commands this formation; it is not ours to touch.</summary>
        LeaveToPlayer,

        /// <summary>It already has an order. Arriving troops inherit it by joining.</summary>
        KeepExisting,

        /// <summary>No order of its own, so it adopts what the rest of the side is doing.</summary>
        Inherit,

        /// <summary>Nothing on the side has a posture; engage so they are not left standing.</summary>
        Charge,
    }

    /// <summary>The decision alone, over plain values, so every branch can be asserted without a mission.</summary>
    internal static ReinforcementPosture DecidePosture(bool commandedByPlayer, OrderType currentOrder, OrderType sideOrder)
    {
        if (commandedByPlayer) return ReinforcementPosture.LeaveToPlayer;
        if (currentOrder != OrderType.None) return ReinforcementPosture.KeepExisting;
        if (sideOrder != OrderType.None) return ReinforcementPosture.Inherit;
        return ReinforcementPosture.Charge;
    }

    private static void ChargeFormations(HashSet<Formation> formations)
    {
        foreach (var formation in formations)
        {
            if (formation == null) continue;

            // PlayerOwner is the agent commanding this formation. Seizing one out from under a living player
            // is how a player loses the ability to order his own men, which has its own history here.
            bool commandedByPlayer = formation.PlayerOwner != null;
            var currentOrder = SafeOrderType(formation);

            // Read the side's posture ONCE and keep the order itself, not just its type: the Inherit branch
            // needs the order to copy, and scanning the team a second time could see a different answer after
            // an earlier formation in this same batch was given one.
            var sideOrder = SidePostureOrder(formation.Team);

            switch (DecidePosture(commandedByPlayer, currentOrder, sideOrder?.OrderType ?? OrderType.None))
            {
                case ReinforcementPosture.LeaveToPlayer:
                    continue;

                case ReinforcementPosture.KeepExisting:
                    formation.SetControlledByAI(true);
                    continue;

                case ReinforcementPosture.Inherit:
                    formation.SetControlledByAI(true);
                    formation.SetMovementOrder(sideOrder.Value);
                    continue;

                case ReinforcementPosture.Charge:
                    formation.SetControlledByAI(true);
                    formation.SetMovementOrder(MovementOrder.MovementOrderCharge);
                    continue;
            }
        }
    }

    /// <summary>This formation's current order type, or None when it cannot be read.</summary>
    private static OrderType SafeOrderType(Formation formation)
    {
        try { return formation.GetReadonlyMovementOrderReference().OrderType; }
        catch { return OrderType.None; }
    }

    /// <summary>
    /// The first real movement order held by any populated formation on the team.
    /// </summary>
    /// <remarks>
    /// Empty formations are skipped: they keep whatever order they were last given and would otherwise let a
    /// long-dead formation dictate the posture of the living ones.
    /// </remarks>
    private static MovementOrder? SidePostureOrder(Team team)
    {
        if (team == null) return null;

        try
        {
            foreach (var formation in team.FormationsIncludingSpecialAndEmpty)
            {
                if (formation == null || formation.CountOfUnits <= 0) continue;

                var order = formation.GetReadonlyMovementOrderReference();
                if (order.OrderType != OrderType.None) return order;
            }
        }
        catch
        {
            // Reading formations mid-spawn can trip over a half-built one; no posture is a safe answer.
        }

        return null;
    }

    // [Host, game thread] Spawn one reinforcement troop AI-controlled. With no InitialPosition set, the engine
    // positions it at the side's reinforcement spawn frame; we then drop it into its troop-class formation.
    private Agent SpawnReinforcementTroop(Mission mission, Team team, CoopAgentOrigin origin)
    {
        var character = (CharacterObject)origin.Troop;
        var equipment = character.IsHero ? character.HeroObject.BattleEquipment : character.Equipment;

        var buildData = new AgentBuildData(character);
        buildData.Team(team);
        buildData.TroopOrigin(origin);
        buildData.Banner(origin.Banner);
        buildData.Equipment(equipment);
        buildData.BodyProperties(character.GetBodyPropertiesMax());
        buildData.Controller(AgentControllerType.AI);
        buildData.IsReinforcement(true);
        buildData.ClothingColor1(origin.FactionColor);
        buildData.ClothingColor2(origin.FactionColor2);

        // Put the man in with the troops he is joining rather than wherever the engine would drop him.
        // Without an InitialPosition the engine uses the side's reinforcement frame, and in a co-op battle -
        // which has no properly built deployment plan - that lands them out in the middle of the field,
        // separated from the line they belong to and often in front of it.
        if (TryGetFriendlyLinePosition(mission, team, out var spawnPosition, out var spawnDirection))
        {
            buildData.InitialPosition(ScatterAround(mission, spawnPosition));
            buildData.InitialDirection(spawnDirection);
        }

        var agent = mission.SpawnAgent(buildData);
        agent.FadeIn();

        formationAssigner.Assign(agent);

        // Wake the AI exactly as the adopt and NPC-release paths do. Without this the reinforcement is
        // AI-controlled but NOT alarmed and holds stale enemy caches, so it ignores its formation's Charge order
        // (set in SpawnReinforcementParty) and stands idle — the "reinforcements spawn but don't move" bug. In a
        // coop battle no general drives the formation, so nothing else alarms them.
        AgentAiWaker.Wake(agent);

        return agent;
    }

    /// <summary>
    /// Where this side's troops currently are, so a reinforcement joins the line instead of appearing apart
    /// from it.
    /// </summary>
    /// <remarks>
    /// Averaged over the team's own live agents, which is both the answer to "where are my units" and a
    /// position that is by construction behind whatever the line is facing. Every read is guarded: this runs on
    /// the game tick, and agent state is not guaranteed valid for an agent mid-removal - an exception here does
    /// not skip a spawn, it takes Game.OnTick down with it and freezes the client.
    ///
    /// Returns false when the side has nobody standing, in which case the engine's own frame is used and the
    /// behaviour is exactly what it was before.
    /// </remarks>
    // Cached per side. Recomputing per TROOP walked every agent in the mission on the game thread: a
    // 100-strong reinforcement into a 400-agent battle is 40,000 iterations in one tick, which is a frame
    // hitch at best. The line does not move meaningfully within a spawn batch, so one reading serves it.
    private readonly Dictionary<BattleSideEnum, (float AtTime, bool Found, Vec3 Position, Vec2 Direction)> lineCache
        = new Dictionary<BattleSideEnum, (float, bool, Vec3, Vec2)>();

    private const float LineCacheSeconds = 1f;

    // Counts every scattered spawn, so consecutive reinforcements land on different points of the spiral
    // rather than all on the first one.
    private int scatterIndex;

    /// <summary>
    /// Spreads reinforcements over a patch of ground instead of stacking them on a single point.
    /// </summary>
    /// <remarks>
    /// <see cref="TryGetFriendlyLinePosition"/> answers with ONE position - the centroid of the side - and
    /// every troop fielded through this path was given exactly that position. Dozens of men then materialised
    /// inside each other at a single point: a packed, motionless blob, because agents wedged into one another
    /// cannot path out. It is the crowd in the middle of the line that looked like troops "stuck and not
    /// moving", and it gets worse the more reinforcements arrive.
    ///
    /// A phyllotactic spiral rather than a random offset: it fills a disc evenly at any count, gives each
    /// successive man a very different angle from the last, and needs no randomness - so a spawn batch is
    /// reproducible and cannot clump by luck. The radius grows as sqrt(n), which is what keeps the DENSITY
    /// constant as the batch gets larger.
    /// </remarks>
    internal static Vec2 ScatterOffset(int index)
    {
        // ~137.5 degrees, the golden angle: successive points never line up into spokes.
        const float GoldenAngleRadians = 2.399963f;
        // Roughly one man per square metre once the sqrt spacing is applied - dense enough to still read as a
        // formation, loose enough that nobody spawns inside anyone.
        const float SpacingMetres = 1.1f;

        var radius = SpacingMetres * (float)Math.Sqrt(index);
        var angle = index * GoldenAngleRadians;
        return new Vec2(radius * (float)Math.Cos(angle), radius * (float)Math.Sin(angle));
    }

    private Vec3 ScatterAround(Mission mission, Vec3 centre)
    {
        var offset = ScatterOffset(scatterIndex++);
        var scattered = new Vec3(centre.x + offset.x, centre.y + offset.y, centre.z);

        // Re-seat on the terrain: the centroid's height belongs to the middle of the line, and a man placed a
        // few metres away at that height would spawn buried or in mid-air on any slope. Guarded because this
        // runs on the game tick and a scene query is not worth a frozen client.
        try
        {
            scattered.z = mission.Scene.GetGroundHeightAtPosition(scattered);
        }
        catch
        {
            scattered.z = centre.z;
        }

        return scattered;
    }

    private bool TryGetFriendlyLinePosition(Mission mission, Team team, out Vec3 position, out Vec2 direction)
    {
        var side = team?.Side ?? BattleSideEnum.None;
        var now = mission.CurrentTime;

        if (lineCache.TryGetValue(side, out var cached) && now - cached.AtTime < LineCacheSeconds)
        {
            position = cached.Position;
            direction = cached.Direction;
            return cached.Found;
        }

        var found = ComputeFriendlyLinePosition(mission, team, out position, out direction);
        lineCache[side] = (now, found, position, direction);
        return found;
    }

    private static bool ComputeFriendlyLinePosition(Mission mission, Team team, out Vec3 position, out Vec2 direction)
    {
        position = Vec3.Zero;
        direction = Vec2.Forward;

        try
        {
            var sum = Vec3.Zero;
            var count = 0;
            var facing = Vec2.Zero;

            foreach (var agent in mission.Agents)
            {
                if (agent == null || !agent.IsActive() || !agent.IsHuman) continue;
                if (agent.Team != team) continue;

                sum += agent.Position;
                facing += agent.GetMovementDirection();
                count++;
            }

            if (count == 0) return false;

            position = sum / count;
            if (!facing.IsNonZero()) return true;

            direction = facing.Normalized();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
