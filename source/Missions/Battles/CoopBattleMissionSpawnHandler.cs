using System;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.GameDebug.Messages;
using GameInterface.Services.MapEvents;
using GameInterface.Services.MapEvents.TroopSupply;
using SandBox.Missions.MissionLogics;
using Serilog;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace Missions.Battles;

/// <summary>
/// Coop replacement for <see cref="SandBoxBattleMissionSpawnHandler"/>: sizes each side to what THIS client's
/// supplier owns (its party, plus the AI/enemy side for the host), not the full side the native handler waits on
/// and never fills. The engine's battle-size cap and wave split are joint across both sides, so both are sized in
/// one pass once both reserves land: at <see cref="AfterStart"/> if already present, else held at zero until
/// <see cref="OnMissionTick"/> sees them, so a late side ends up identical to an on-time one.
/// </summary>
public class CoopBattleMissionSpawnHandler : SandBoxMissionSpawnHandler
{
    private static readonly ILogger Logger = LogManager.GetLogger<CoopBattleMissionSpawnHandler>();
    internal const string InvalidPlayerReserveMessage = "Unable to start the battle because your party's troop reserve was not received. Returning to the campaign map.";

    // Hold this long for a still-in-flight reserve before sizing with whatever landed. A dropped or server-rejected
    // reserve request would otherwise never populate a supplier, and the deployment controller (which gates on
    // IsSized) would wedge on the loading screen forever. A partial response degrades to a one-sided battle; a
    // zero-troop response cannot produce a valid battle and is terminated through the normal mission lifecycle.
    private const float ReserveHoldDeadlineSeconds = 15f;

    private readonly CoopTroopSupplier _defenderSupplier;
    private readonly CoopTroopSupplier _attackerSupplier;
    private readonly IMessageBroker _messageBroker;
    private readonly BattleSideEnum _playerSide;

    // Latched once the sides are sized jointly; both are held at zero until then.
    private bool _sized;
    // True only for the duration of a round-restart init; see IsLateJoin.
    private bool _restartingRound;

    // The reserve revision the current sizing was derived from, so a later reserve can be noticed.
    private int _sizedAtReserveRevision = -1;

    // Per side: the opening wave a late joiner was NOT allowed to put out, kept so its reinforcement ceiling can
    // be restored once the single man it was allowed has landed. Zero on any client that started the battle.
    private readonly int[] _openingWaveHeldBack = new int[2];
    private bool _lateJoinCeilingRestored;

    // Time spent holding both sides while a reserve is in flight (only accrues on the held path).
    private float _heldSeconds;
    private bool _invalidBattleAbortRequested;

    // Gated on by CoopBattleDeploymentMissionController: a game-thread latch, not the suppliers' network-thread
    // IsPopulated (which could read true mid-frame before Init has actually sized).
    public bool IsSized => _sized;

    /// <summary>
    /// Advances whenever either side's reserve is replaced by the server, so a caller can tell a rebuilt
    /// reserve from the one it already had.
    /// </summary>
    /// <remarks>
    /// Summed rather than exposed per side because callers only ever ask "has the server sent me anything new",
    /// and a restart replaces both sides together.
    /// </remarks>
    internal int ReserveRevision => _defenderSupplier.ReserveRevision + _attackerSupplier.ReserveRevision;

    public CoopBattleMissionSpawnHandler(CoopTroopSupplier defenderSupplier, CoopTroopSupplier attackerSupplier,
        IMessageBroker messageBroker, BattleSideEnum playerSide)
    {
        _defenderSupplier = defenderSupplier;
        _attackerSupplier = attackerSupplier;
        _messageBroker = messageBroker;
        _playerSide = playerSide;
    }

    public override void AfterStart()
    {
        _missionAgentSpawnLogic.SetSpawnHorses(BattleSideEnum.Defender, !_mapEvent.IsSiegeAssault);
        _missionAgentSpawnLogic.SetSpawnHorses(BattleSideEnum.Attacker, !_mapEvent.IsSiegeAssault);

        var sizing = ReadSizing();

        if (sizing.Ready)
        {
            if (sizing.SizeNow && HasLocalPlayerOrigin())
            {
                // On-time (common): both reserves present, so size before the first tick.
                RunJointInit(sizing);
                _sizedAtReserveRevision = ReserveRevision;
                _sized = true;
                Logger.Information("[BattleSync] Coop spawn sized on start: Defender={Def}, Attacker={Atk}", sizing.DefenderOwned, sizing.AttackerOwned);
                return;
            }

            // Keep deployment held when the authoritative response cannot produce the local player agent.
            // OnMissionTick ends the invalid mission without allowing native SetupTeams to run.
            AddHeldPhases();
            Logger.Error("[BattleSync] Battle reserves cannot produce the local player origin; holding deployment before aborting the invalid mission");
            return;
        }

        // A reserve is still in flight — hold both sides at zero until OnMissionTick sizes them (or the deadline).
        AddHeldPhases();
        Logger.Warning("[BattleSync] Coop spawn handler started before reserves arrived (Def populated={Def}, Atk populated={Atk}) — sizing on tick once both land",
            sizing.DefenderPopulated, sizing.AttackerPopulated);
    }

    // Size once both suppliers populate, then latch. If a reserve never lands, size a usable partial response
    // after ReserveHoldDeadlineSeconds; if no combatant exists, end the invalid mission instead. A mid-battle
    // migration re-feed re-populates an already-sized supplier and is left to ReinforcementFielder, which can
    // distinguish newly-owned parties with no adopted live agents without disturbing the initial phase sizing.
    public override void OnMissionTick(float dt)
    {
        base.OnMissionTick(dt);

        if (_sized)
        {
            ReSizeIfReservesChangedBeforeAnythingSpawned();
            RestoreLateJoinReinforcementCeiling();
            GrowPhaseBudgetToReserve();
            return;
        }

        if (_invalidBattleAbortRequested) return;

        _heldSeconds += dt;
        var sizing = ReadSizing();
        if (ShouldContinueHolding(sizing)) return;

        if (!HasLocalPlayerOrigin())
        {
            AbortInvalidBattle(sizing);
            return;
        }

        AcceptMissingReserveSides(sizing);

        // Ready, or the deadline expired with a partial/missing reserve. At least one combatant exists here,
        // so the joint Init cannot hit its invalid 0/0 split.
        RunJointInit(sizing);
        LogSizingCompleted(sizing);
        _sizedAtReserveRevision = ReserveRevision;
        _sized = true;
    }

    /// <summary>
    /// Whether the battle should be sized again: only before the opening wave, and only if the reserve the
    /// current sizing came from has been superseded.
    /// </summary>
    /// <remarks>
    /// Separated so the rule can be asserted directly. Both halves matter and for different reasons - the
    /// revision check is what makes a battle that grew during deployment get re-sized at all, and the
    /// initial-spawn check is what stops that ever happening over a populated field.
    /// </remarks>
    internal static bool ShouldReSize(bool initialSpawnOver, int sizedAtReserveRevision, int currentReserveRevision)
        => !initialSpawnOver && currentReserveRevision != sizedAtReserveRevision;

    /// <summary>
    /// Re-derives the battle's sizing when the server sends a changed reserve BEFORE anything has spawned.
    /// </summary>
    /// <remarks>
    /// Sizing used to latch permanently the moment both reserves landed, which is wrong whenever the battle
    /// grows between entering it and starting it. Lords joining while a player sits on the deployment screen
    /// are the ordinary case: the engine's opening wave is then split from totals that are already stale, and
    /// since nothing corrects the field afterwards the battle simply starts over its size. Measured at 445
    /// agents on a battle sized for 400, decaying only as men died.
    ///
    /// It also desynchronises clients. Each latches at whatever the reserve happened to be when ITS pair
    /// arrived, so two clients entering the same battle a few seconds apart size it differently and spawn
    /// different numbers. Re-sizing to the latest reserve makes them converge on the same figures.
    ///
    /// Strictly gated on the engine's own <c>IsInitialSpawnOver</c>. Before the opening wave there are no
    /// agents to disturb and this is the same call the initial sizing makes; after it, re-running Init over a
    /// populated field is precisely the class of change that caused the round-restart's failures, so it simply
    /// does not run. Safe by construction rather than by care.
    /// </remarks>
    private void ReSizeIfReservesChangedBeforeAnythingSpawned()
    {
        var revision = ReserveRevision;
        if (!ShouldReSize(_missionAgentSpawnLogic.IsInitialSpawnOver, _sizedAtReserveRevision, revision)) return;

        var sizing = ReadSizing();
        if (!sizing.SizeNow) return;

        RunJointInit(sizing);
        _sizedAtReserveRevision = revision;

        Logger.Information(
            "[BattleSync] Reserves changed before the opening wave; re-sized to Defender={Def}, Attacker={Atk}",
            sizing.DefenderOwned, sizing.AttackerOwned);
    }

    private bool ShouldContinueHolding(SideSizing sizing)
    {
        return _heldSeconds < ReserveHoldDeadlineSeconds
            && (!sizing.Ready || !sizing.HasAnyOwnedTroops);
    }

    // The native deployment controller dereferences InitialPlayerAgent after spawning. Without the local
    // player's authoritative origin, keep IsSized false and end through the attached mission lifecycle.
    private void AbortInvalidBattle(SideSizing sizing)
    {
        _invalidBattleAbortRequested = true;
        var playerPartyId = GetLocalPlayerPartyId();
        Logger.Error("[BattleSync] Local player origin missing from battle reserves (side={Side}, party={PartyId}, Def populated={DefP}, Atk populated={AtkP}); ending invalid mission",
            _playerSide, playerPartyId, sizing.DefenderPopulated, sizing.AttackerPopulated);
        _messageBroker.Publish(this, new SendInformationMessage(InvalidPlayerReserveMessage));
        base.Mission.EndMission();
    }

    private bool HasLocalPlayerOrigin()
    {
        return HasLocalPlayerOrigin(_playerSide, GetLocalPlayerPartyId(), _defenderSupplier, _attackerSupplier);
    }

    private string GetLocalPlayerPartyId()
    {
        var playerSupplier = _playerSide == BattleSideEnum.Attacker ? _attackerSupplier : _defenderSupplier;
        return playerSupplier.PlayerPartyId;
    }

    internal static bool HasLocalPlayerOrigin(BattleSideEnum playerSide, string playerPartyId,
        CoopTroopSupplier defenderSupplier, CoopTroopSupplier attackerSupplier)
    {
        var playerSupplier = playerSide == BattleSideEnum.Attacker ? attackerSupplier : defenderSupplier;
        return playerSupplier.GetRemainingForParty(playerPartyId) > 0;
    }

    // This is the one point where an empty side becomes intentional rather than merely late. Record exactly
    // which reserve timed out so the controller can eventually release BattleEndLogic and the depletion patch
    // can call only that side depleted; the populated side must still field an agent.
    private static void AcceptMissingReserveSides(SideSizing sizing)
    {
        if (sizing.Ready) return;
        if (!sizing.DefenderPopulated)
            BattleSpawnGate.AcceptMissingReserveSide(BattleSideEnum.Defender);
        if (!sizing.AttackerPopulated)
            BattleSpawnGate.AcceptMissingReserveSide(BattleSideEnum.Attacker);
    }

    private static void LogSizingCompleted(SideSizing sizing)
    {
        if (sizing.Ready)
            Logger.Information("[BattleSync] Reserves landed after start; sized sides jointly: Defender={Def}, Attacker={Atk}", sizing.DefenderOwned, sizing.AttackerOwned);
        else
            Logger.Warning("[BattleSync] Reserves incomplete after {Sec}s hold (Def populated={DefP}, Atk populated={AtkP}) — sizing with what landed: Defender={Def}, Attacker={Atk}",
                ReserveHoldDeadlineSeconds, sizing.DefenderPopulated, sizing.AttackerPopulated, sizing.DefenderOwned, sizing.AttackerOwned);
    }

    // Snapshot both suppliers into a SideSizing. Read populated before owned so the pair can't tear: SetReserve
    // commits the entries then flips populated under one lock. Shared by AfterStart and OnMissionTick.
    private SideSizing ReadSizing()
    {
        bool defenderPopulated = _defenderSupplier.IsPopulated;
        bool attackerPopulated = _attackerSupplier.IsPopulated;
        // The SIDE's totals, not this client's share of them. The engine splits a fixed battle size in
        // proportion to the two numbers it is given, so a client sizing from what it happens to own measures
        // a side that is divided between players at a fraction of its strength: its opponent gets capped
        // against that fraction, and the divided side ends up fielding more men than the larger one.
        // Falls back to owned totals when the server sent none.
        int defenderOwned = _defenderSupplier.SideTotalTroops;
        int attackerOwned = _attackerSupplier.SideTotalTroops;
        return new SideSizing(defenderPopulated, attackerPopulated, defenderOwned, attackerOwned);
    }

    /// <summary>
    /// Re-forms the battle around the totals as they now stand: rewinds the reserves, clears the engine's
    /// per-side spawn bookkeeping, and re-runs the joint init so the battle-size split reflects the
    /// reinforcements rather than whoever happened to be present at the opening bell.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT despawn anyone - that is the caller's job, and it has to happen first. This method
    /// only resets the accounting, so calling it with agents still on the field would leave the engine believing
    /// it has spawned nobody while the mission is full of troops, and it would spawn a second army on top.
    ///
    /// <c>_numSpawnedTroops</c> and the reserved/queued origin lists are what <c>CheckDeployment</c> and
    /// <c>CheckReinforcementSpawn</c> read to decide whether a side still owes the field anyone. Left at their
    /// end-of-round values, the restarted round would believe every side was already full and spawn nothing at
    /// all - which is the failure this reset exists to prevent.
    /// </remarks>
    internal void ResetForRoundRestart()
    {
        _defenderSupplier.RewindForRoundRestart();
        _attackerSupplier.RewindForRoundRestart();

        _missionAgentSpawnLogic._sidesWhereSpawnOccured?.Clear();

        foreach (var context in _missionAgentSpawnLogic._battleSideSpawnContexts)
        {
            if (context == null) continue;

            context._numSpawnedTroops = 0;
            context._reinforcementsSpawnedInLastBatch = 0;
            context._reservedTroops?.Clear();
            context._spawnedFormations?.Clear();
            context._reinforcementTroopFormationAssignments?.Clear();

            // Per-team queues of origins waiting to be spawned. The team entries themselves are kept - the teams
            // still exist across a restart - and only the queued origins are dropped, since they will be drawn
            // again from the rewound reserve.
            foreach (var (_, origins) in context._troopOriginsToSpawnPerTeam)
                origins?.Clear();
        }

        // Marks this init as a restart rather than a late join, so the sides put their full re-derived opening
        // wave back on the field instead of the single hero a genuine late joiner is limited to.
        _restartingRound = true;
        try
        {
            _sized = false;
            var sizing = ReadSizing();
            RunJointInit(sizing);
            _sized = true;

            // RunJointInit deliberately leaves both sides spawn-DISABLED, because at battle start the native
            // SetupTeams re-enables them one side at a time. SetupTeams does not run again for a restart, so
            // without this the re-formed round would be correctly sized and then spawn absolutely nobody.
            _missionAgentSpawnLogic.StartSpawner(BattleSideEnum.Defender);
            _missionAgentSpawnLogic.StartSpawner(BattleSideEnum.Attacker);

            Logger.Information(
                "[BattleSync] Round restarted; re-sized from defender {Defender} / attacker {Attacker}",
                sizing.DefenderOwned, sizing.AttackerOwned);
        }
        finally
        {
            _restartingRound = false;
        }
    }

    // Re-run the engine's Init with the real totals (initial == total; Init applies the joint cap, wave split and
    // agent counts). Clear the placeholder phases first — InitWithSinglePhase appends, so a leftover held phase
    // would leave two active phases. Nothing spawned while held, so no double-spawn.
    private void RunJointInit(SideSizing sizing)
    {
        _missionAgentSpawnLogic._phases[(int)BattleSideEnum.Defender].Clear();
        _missionAgentSpawnLogic._phases[(int)BattleSideEnum.Attacker].Clear();

        var settings = CreateSandBoxBattleWaveSpawnSettings();
        _missionAgentSpawnLogic.InitWithSinglePhase(sizing.DefenderOwned, sizing.AttackerOwned,
            sizing.DefenderOwned, sizing.AttackerOwned, spawnDefenders: true, spawnAttackers: true, in settings);

        ClampPhasesToOwnedShare(BattleSideEnum.Defender, _defenderSupplier);
        ClampPhasesToOwnedShare(BattleSideEnum.Attacker, _attackerSupplier);

        // Init leaves both sides spawn-active; the native path clears them after Init but nothing does here, so
        // restore it — else SetupTeams's first side spawns both at once and the per-side freeze misses one.
        _missionAgentSpawnLogic.SetSpawnTroops(BattleSideEnum.Defender, spawnTroops: false);
        _missionAgentSpawnLogic.SetSpawnTroops(BattleSideEnum.Attacker, spawnTroops: false);
    }

    /// <summary>
    /// Rewrites a side's spawn numbers from what the SIDE fields to what THIS client will actually be handed.
    /// </summary>
    /// <remarks>
    /// Init is deliberately given the side totals so the engine's battle-size split stays in proportion to the
    /// two sides' real strength (see <see cref="ReadSizing"/>). The resulting InitialSpawnNumber is therefore the
    /// whole side's opening wave - but the engine then treats it as a target THIS client must fill before the
    /// side may deploy. <c>CheckDeployment</c> is explicit about it:
    ///
    ///     deficit = phase.InitialSpawnNumber - ctx.ReservedTroopsCount;
    ///     ctx.ReserveTroops(deficit);
    ///     if (ctx.ReservedTroopsCount &lt; phase.InitialSpawnNumber) continue;   // skips the ENTIRE side
    ///
    /// and "continue" skips the side's plan-making as well as its spawn. So a side split between players cannot
    /// converge: the engine asks for the side's full deficit, <see cref="CoopTroopSupplier.OwnedShareOf"/> hands
    /// back only this client's fraction of it, and the gap closes geometrically without ever reaching zero.
    /// Observed live on a client owning 382 of a 955-strong side, against a 163-troop wave: reservations of
    /// 65, 39, 23, 14, 8, 5, 3, 2, 1, 1, 1 ... stalling at 162 of 163 - each one 40% of the remaining gap, which
    /// is exactly this client's ownership share. The side is skipped every tick, so its teams are never planned,
    /// so <c>IsPlanMade(PlayerTeam)</c> stays false, so nothing spawns and the player has no agent.
    ///
    /// Asking the supplier what it would return for the number makes the target reachable in one pass, and keeps
    /// every owner's slice adding up to the side's wave rather than each owner trying to field all of it. A side
    /// filled by replicated puppets owns nothing, resolves to zero, and correctly spawns nothing locally.
    /// </remarks>
    private void ClampPhasesToOwnedShare(BattleSideEnum side, CoopTroopSupplier supplier)
    {
        bool joiningInProgress = IsLateJoin();

        // Accumulated INSIDE the loop, from the value before the clamp. Reading it back afterwards is a bug
        // that hides perfectly: the loop has already overwritten InitialSpawnNumber with 1, so the "wave held
        // back" reads as 1, and restoring a ceiling of 1 restores exactly the ceiling that was broken. Live,
        // that left a joining player's supplier asked for one troop and never asked again, with 950 of his men
        // waiting - the very symptom the restore exists to cure, unchanged.
        int openingWaveHeldBack = 0;

        foreach (var phase in _missionAgentSpawnLogic._phases[(int)side])
        {
            phase.TotalSpawnNumber = ReachableSpawnNumber(phase.TotalSpawnNumber, supplier);
            phase.RemainingSpawnNumber = ReachableSpawnNumber(phase.RemainingSpawnNumber, supplier);

            var reachable = ReachableSpawnNumber(phase.InitialSpawnNumber, supplier);
            var (opening, heldBack) = OpeningAndHeldBack(joiningInProgress, reachable);

            phase.InitialSpawnNumber = opening;
            openingWaveHeldBack += heldBack;
        }

        // A late joiner is held to one man now, but that one man must not also become its reinforcement
        // ceiling for the rest of the battle - see RestoreLateJoinReinforcementCeiling.
        _openingWaveHeldBack[(int)side] = joiningInProgress ? openingWaveHeldBack : 0;
        _lateJoinCeilingRestored = false;
    }

    /// <summary>
    /// Gives a client that joined mid-battle back a reinforcement budget, once its opening man is standing.
    /// </summary>
    /// <remarks>
    /// The engine sizes every reinforcement wave as
    /// <c>Min(batch, phase.InitialSpawnedNumber - NumberOfActiveTroops)</c> - see
    /// <c>MissionBattleSideSpawnContext.ComputeBalancedBatch</c>. <c>InitialSpawnedNumber</c> is how many this
    /// client actually put out in the opening wave, and for a late joiner
    /// <see cref="OpeningSpawnForLateJoiner"/> deliberately makes that ONE. So the moment their hero is alive
    /// the ceiling reads 1 - 1 = 0, and it never rises again: that client cannot field another man for the
    /// whole battle, however many casualties open up in front of it.
    ///
    /// Observed exactly so. A player joined a live battle holding eight parties and 952 men; his supplier was
    /// asked for a single troop at 11:20:45 and never asked again. He fought alone, with an army he could see
    /// on the scoreboard and could not command, and the seven allied lords in his reserve finished the battle
    /// with no kills and no losses.
    ///
    /// The one-man opening wave is still right - dropping 952 men onto a field sized for 400 is what it was
    /// written to prevent. What was wrong is reusing it as the ceiling. So the opening stays clamped and the
    /// ceiling is restored afterwards to the share this client would ordinarily hold, and the men arrive through
    /// the ordinary reinforcement path as room appears - which is the behaviour the clamp intended all along.
    /// </remarks>
    internal static int ReinforcementCeiling(bool lateJoin, int spawnedInOpeningWave, int openingWaveHeldBack)
        => lateJoin ? Math.Max(spawnedInOpeningWave, openingWaveHeldBack) : spawnedInOpeningWave;

    /// <summary>
    /// Keeps a phase's spawn budget in step with a reserve that grew after the battle started.
    /// </summary>
    /// <remarks>
    /// <c>AddPhase</c> stores <c>TotalSpawnNumber</c> and derives
    /// <c>RemainingSpawnNumber = TotalSpawnNumber - InitialSpawnNumber</c>, and the total is a hard ceiling on
    /// how many men this client will EVER put on the field. It is computed once, from the reserve as it stood
    /// when the battle was sized - and a coop battle grows: lords ride in for minutes afterwards and their
    /// troops go into the supplier, but the phase budget does not move.
    ///
    /// The result is a side that stops reinforcing long before it is spent. Measured live: a phase total of
    /// 534 with 331 left to spawn, while the supplier behind it still held 1,108 men. Those 1,108 could never
    /// be drawn. The side ran dry at roughly a third of its strength, read as depleted, and ended the battle -
    /// which is exactly "1,500 against 500, and it finished after I killed about 500".
    ///
    /// Raising the ceiling spawns nobody by itself; it only stops the engine believing it has run out. How many
    /// men stand on the field at once is still governed by the battle size, which is enforced separately at the
    /// supplier. And it only ever raises: lowering a budget mid-battle would cut off a side that is currently
    /// drawing on it.
    ///
    /// <c>InitialSpawnNumber</c> is deliberately untouched. That is the opening wave and the reinforcement
    /// ceiling, not the total, and rewriting it here would re-open the late-joiner problem this class already
    /// solves elsewhere.
    /// </remarks>
    internal static bool ShouldGrowPhaseBudget(int phaseTotal, int ownedReserveTotal)
        => ownedReserveTotal > phaseTotal;

    private void GrowPhaseBudgetToReserve()
    {
        GrowPhaseBudgetToReserve(BattleSideEnum.Defender, _defenderSupplier);
        GrowPhaseBudgetToReserve(BattleSideEnum.Attacker, _attackerSupplier);
    }

    private void GrowPhaseBudgetToReserve(BattleSideEnum side, CoopTroopSupplier supplier)
    {
        var phases = _missionAgentSpawnLogic._phases[(int)side];
        if (phases.Count != 1) return; // sized with InitWithSinglePhase; anything else is not ours to re-budget

        var phase = phases[0];
        var owned = supplier.TotalTroops;
        if (!ShouldGrowPhaseBudget(phase.TotalSpawnNumber, owned)) return;

        var grewBy = owned - phase.TotalSpawnNumber;
        phase.TotalSpawnNumber = owned;
        phase.RemainingSpawnNumber += grewBy;

        Logger.Information(
            "[BattleSync] {Side} reserve grew to {Owned}; phase budget raised by {GrewBy} so the new arrivals can still be fielded",
            side, owned, grewBy);
    }

    private void RestoreLateJoinReinforcementCeiling()
    {
        if (_lateJoinCeilingRestored) return;
        if (!_missionAgentSpawnLogic.IsInitialSpawnOver) return;

        _lateJoinCeilingRestored = true;

        int restored = 0;
        foreach (BattleSideEnum side in new[] { BattleSideEnum.Defender, BattleSideEnum.Attacker })
        {
            var heldBack = _openingWaveHeldBack[(int)side];
            if (heldBack <= 0) continue;

            foreach (var phase in _missionAgentSpawnLogic._phases[(int)side])
            {
                phase.InitialSpawnedNumber = ReinforcementCeiling(true, phase.InitialSpawnedNumber, heldBack);
                restored += heldBack;
            }
        }

        if (restored > 0)
            Logger.Information("[BattleSync] Joined a battle in progress; reinforcement ceiling restored so the rest of this client's force can arrive as room appears");
    }

    /// <summary>
    /// What a client joining a battle ALREADY UNDERWAY may put on the field immediately: just enough to give
    /// the player an agent. The rest of its force stays in <c>RemainingSpawnNumber</c> and arrives through the
    /// engine's ordinary reinforcement waves, as casualties make room.
    /// </summary>
    /// <remarks>
    /// The initial spawn number is an OPENING wave - it assumes an empty field. A client arriving mid-battle
    /// computed one anyway and dumped its whole owned force in at once: a player joining a fight already
    /// holding 392 men took that side to 532 in one step, eight allied parties appearing together, far past
    /// what the battle was sized for.
    ///
    /// One troop, not zero: the receiver's own party is supplied first and its hero first within it (the
    /// reserve is built heroes-first, and <c>CoopTroopSupplier.GuaranteeReceiverPlayerATroop</c> reserves it a
    /// place), so one is exactly the player's hero. Zero would leave the joining player with no agent to
    /// control, which the spawn handler treats as a battle it cannot start.
    /// </remarks>
    internal static int OpeningSpawnForLateJoiner(int reachableInitial) => Math.Min(1, reachableInitial);

    /// <summary>
    /// Both halves of the decision at once: what this phase opens with, and what was held back from it.
    /// </summary>
    /// <remarks>
    /// Returned together, from one reading, on purpose. Both are derived from the SAME pre-clamp number, and
    /// the obvious way to write it - clamp the phase, then read the phase back to see what was held back - is
    /// wrong in a way nothing reveals: the clamp has already replaced the number with 1, so the held-back wave
    /// reads as 1, and restoring a ceiling of 1 restores precisely the ceiling that was broken. Live, that left
    /// a joining player's supplier asked for a single troop and never asked again, with 950 men waiting, while
    /// the code that was supposed to have fixed it ran every tick.
    ///
    /// Taking one input and returning both outputs makes that mistake unavailable rather than merely tested
    /// for: there is no clamped value in scope to read by accident.
    /// </remarks>
    internal static (int Opening, int HeldBack) OpeningAndHeldBack(bool lateJoin, int reachableInitial)
        => lateJoin
            ? (OpeningSpawnForLateJoiner(reachableInitial), reachableInitial)
            : (reachableInitial, 0);

    /// <summary>
    /// Whether this client is entering a battle that has already gone live, rather than starting one.
    /// </summary>
    /// <remarks>
    /// Activation is the "the fighting has begun" signal: the host raises it on the first deployment finish
    /// from any client and broadcasts it, and <c>BattleDeploymentCoordinator.CatchUpJoiner</c> re-sends it to
    /// anyone who arrives afterwards - which is exactly the case being detected. Read off the controller rather
    /// than injected, because this behaviour is built by the launcher before the controller is attached.
    ///
    /// A joiner whose activation catch-up has not landed by the time it sizes reads false and takes the
    /// ordinary opening wave. Sizing is normally reached well after the mesh connect (a scene load, or the
    /// tick path's hold for a reserve still in flight), so that is the uncommon case rather than the rule.
    /// </remarks>
    private static bool IsJoiningBattleAlreadyUnderway()
        => Mission.Current?.GetMissionBehavior<CoopBattleController>()?.Deployment?.IsActivated == true;

    /// <summary>
    /// Whether THIS client is arriving at a fight already in progress - as opposed to a round restart, where
    /// the battle is under way but everyone is re-forming together.
    /// </summary>
    /// <remarks>
    /// Both look identical to <see cref="IsJoiningBattleAlreadyUnderway"/>, because a restart happens while
    /// deployment is long since activated. But they want opposite things: a late joiner may field only its
    /// hero, so its arrival is not a spike, whereas a restart is precisely the moment every side is supposed
    /// to put its full re-derived opening wave back on the field. Without this distinction a restart would
    /// clamp every side to a single man and empty the battle it was meant to re-balance.
    /// </remarks>
    internal static bool IsLateJoin(bool restartingRound, bool deploymentActivated)
        => !restartingRound && deploymentActivated;

    private bool IsLateJoin() => IsLateJoin(_restartingRound, IsJoiningBattleAlreadyUnderway());

    private static int ReachableSpawnNumber(int sideNumber, CoopTroopSupplier supplier)
        => ReachableSpawnNumber(sideNumber, supplier.OwnedShareOf(sideNumber));

    /// <summary>
    /// The largest spawn target this client can actually reach: never more than the side needs, and never more
    /// than the supplier will hand over when asked for that many.
    /// </summary>
    internal static int ReachableSpawnNumber(int sideNumber, int ownedShareOfSideNumber)
        => Math.Min(sideNumber, ownedShareOfSideNumber);

    // Zero phases so the first tick has active phases to read (else DefenderActivePhase NREs), without feeding Init
    // a 0/0 total: its float battle-size split yields NaN, which Mono casts to int.MinValue (desktop .NET gives 0).
    private void AddHeldPhases()
    {
        _missionAgentSpawnLogic._phases[(int)BattleSideEnum.Defender].Add(new MissionSpawnPhase());
        _missionAgentSpawnLogic._phases[(int)BattleSideEnum.Attacker].Add(new MissionSpawnPhase());
    }

    /// <summary>
    /// Snapshot of both suppliers plus the joint sizing derived from it (unit-testable — pure over its readings).
    /// Ready = both reserves landed; SizeNow additionally requires a positive combined total, so Init is never
    /// handed a 0/0 battle-size split.
    /// </summary>
    public readonly struct SideSizing
    {
        public readonly bool DefenderPopulated;
        public readonly bool AttackerPopulated;
        public readonly int DefenderOwned;
        public readonly int AttackerOwned;

        public SideSizing(bool defenderPopulated, bool attackerPopulated, int defenderOwned, int attackerOwned)
        {
            DefenderPopulated = defenderPopulated;
            AttackerPopulated = attackerPopulated;
            DefenderOwned = defenderOwned;
            AttackerOwned = attackerOwned;
        }

        // Both reserves landed: commit the joint sizing now (else keep holding both sides at zero).
        public bool Ready => DefenderPopulated && AttackerPopulated;

        // Ready and at least one side owns troops: run the real Init (a positive sum avoids Init's 0/0 NaN).
        public bool SizeNow => Ready && DefenderOwned + AttackerOwned > 0;

        /// <summary>Whether a timeout can safely degrade to a one-sided sizing instead of empty/empty.</summary>
        public bool HasAnyOwnedTroops => DefenderOwned + AttackerOwned > 0;
    }
}
