# PvP army sync plan

Runs after `PVP-SYNC-PLAN.md` and depends on what it builds: the rig scripts in `Scripts/Rig/`, the
player lane, `Capture-Load.ps1`, and the before/after protocol. Where that plan asks "do the two
players see the same duel", this one asks "do the two players see the same battle when their ARMIES
fight each other": every soldier spawned, fighting, dying, routing and arriving as a reinforcement, and
the same result, loot, prisoners and rosters when it is over.

Method, unchanged: **check first, fix only what the numbers show, re-verify, and call nothing fixed until
both players have seen it in game.** Every fix carries a before/after row with its network and CPU cost
(`PVP-SYNC-PLAN.md` section 5).

Companion documents: `PVP-SYNC-PLAN.md` (rig, lane, protocol), `COMBAT-BUGS-MILESTONES.md` (M1-M12),
`COMBAT-REMAINING-WORK.md` (costs and open items, M6 and M8), `PLAN-2-PROGRESS.md` (the battle-size
and reserve work this plan re-measures).

---

## 0. Rules specific to army-scale work

All fourteen rules of `PVP-SYNC-PLAN.md` section 0 apply. These are added because they bit before:

1. **Prove a roster disagreement from the paired logs before touching a roster.** Memory
   `coop-roster-divergence-diagnosis`: diff the client and server `[Roster]` lines for the same party at
   the same instant and say which side is ahead. Never "repair" a roster on a hunch.
2. **Restart both client and server between scenarios**, never the client alone (memory
   `coop-restart-both-client-and-server`): a client-only restart leaves the world past the scenario.
3. **Never weaken the join-baseline count check** to make a late join pass; the party is `IsActive=false`
   on the client, not missing (memory `coop-join-baseline-count-mismatch-root-cause`).
4. **TaskStop on a rig-owning task kills the whole rig** (memory `coop-taskstop-kills-the-whole-rig`).
   Warm restart is about 2.5 minutes; budget for it.
5. **The battle-size mismatch is silent and asymmetric.** The server's `modOptions.battleSize` sizes the
   opening wave; each client's own engine battle size caps reinforcement (`coop.debug.battle.engine_size`
   documents a 300-man side landing whole while a 786-man side stalled at 250). Every army run records
   both numbers on both machines before anything else is read.
6. **Ownership decides what a number means.** In PvE the host owns most agents; in a PvP army battle
   each player owns exactly one side, so every enemy is a puppet and EVERY blow crosses the mesh. Numbers
   from the PvE runs (kill gaps, routed-damage counts, message rates) do not carry over and must be
   re-measured here.
7. **A fix that changes who owns, spawns or counts a troop must be re-run through the PvE harness too.**
   `Auto-AnimTest.ps1` and the 1,000-agent battle are the regression gate for anything in the spawn,
   reserve or casualty path.

---

## 1. Scope

Two players, each leading an army (own party plus attached AI lord parties), on opposite sides of a
field battle. Sieges between players are the last milestone, not the first.

| Phase of the battle | What "synced" means |
|---|---|
| Map encounter | One player attacks the other on the map; both get the encounter menu; every combination of attack / leave / surrender / talk resolves the same way on both clients and the server (`AsymmetricEncounterActionTests`, BR-072, is the existing guard) |
| Battle start and deployment | Both clients enter the mission for the same map event; each deploys its own side; the troop-preference prompt never blocks; the mission starts within seconds of each other |
| Sizing and opening wave | Each side is fielded to its allocation; the two allocations follow the server's battle size, not each client's engine setting; the sum of spawned plus reserve equals the roster |
| Spawn mirror | Every agent one player owns exists on the other machine with the same character, equipment, team, formation and mount, within one update of spawning |
| Fighting | Units on both sides engage the enemy puppets exactly as they engage local enemies; damage, kills, routs and deaths are mirrored; neither side is advantaged by ownership |
| Reinforcements | Waves arrive at the right count, time and place on both machines, for both sides, including AI lord parties attached to an army and nearby AI parties that join |
| Commanding | Each player's orders move their own formations on both machines and never move the enemy's |
| Late join, rejoin, host migration | A player who joins late, disconnects and rejoins, or takes over as battle host sees the same battle |
| End of battle | Rout, retreat, surrender, leave, or annihilation ends the battle the same way for both; result, loot, prisoners, casualties, XP and scoreboard agree on both machines and the server; both players are back on the map with their armies intact |
| Siege between players | One player besieges the other's fief: engines, walls, ladders, defenders, sally-out, capture menu |

**Out of scope:** duel-level animation fidelity (that is `PVP-SYNC-PLAN.md`), Realistic Battle Mod,
M8 horse locomotion except where the army run shows it regressing.

---

## 2. What exists today (the parts this plan measures)

| Area | Components | Existing guards |
|---|---|---|
| Map encounter between players | `PvPInteractionClientHandler`, `EncounterAttackConditionPatch`, `EncounterAttackConsequencePatch`, `BattleStartCoordinator`, `MapEventCreationCoordinator`, `StartBattleActionPatches`; commands `request_player_field_battle`, `player_interaction_state`, `submit_player_interaction`, `start_player_field_battle`, `restore_player_field_battle` | `AsymmetricEncounterActionTests`, `PlayerPartyInteractionFlowTests` |
| Entering the mission | `BattleMissionStartHandler`, `CoopFieldBattleLauncher`, `CoopBattleController`, `CoopBattlesController`, `BattleInstanceLifecycle`, `BattleSession`, `TroopPreferenceBarrier`, `TroopDeploymentPreference`; commands `start_attack_mission`, `finish_deployment`, `troop_preference` | `BattleInstanceLifecycleTests`, `BattleActivationJoinTests` |
| Sizing and spawning | `CoopBattleMissionSpawnHandler` (sizes each side to this client's supplier, joint sizing once both reserves land, 15 s hold), `CoopTroopSupplier`, `CoopTroopSupplierRegistry`, `BattleTroopReserveBuilder`, `PartyReserve`, `WaveQuota`, `BattleFieldBalancer`, `BattleSizeProvider`, `MissionSpawnCapacityPatch`, `BattleSpawnGate`, `PuppetSpawner`, `OwnedAgentReplicator`, `BattleAgentSpawnBatchCodec`, `AgentEquipmentApplier`, `AgentFormationAssigner`, `BattleTeams`, `CoopPlayerAllyTeamPatch`; commands `size_state`, `engine_size`, `replication_fixture`, `column_reinforcement_fixture` | `CoopBattleMissionSpawnHandlerSizingTests`, `BattleFieldBalanceTests`, `BattleSpawnMirrorTests`, `BattleWaveCapTests`, `BattleAgentBudgetTests`, `BattlePuppetTeamOwnershipTests`, `BattleTroopAssignmentTests` |
| Fighting at scale | `BattleDamageRouter`, `BattleBlowInterceptPatch`, `BattleDamageDataMapper`, `BattleCasualtyHandler`, `CasualtyAttributionMap`, `AgentDeathReporter`, `PuppetDeathApplier`, `AgentRoutReporter`, `PuppetRoutApplier`, `BattleMoralePanicPatch`, `BattleAgentRoutedPatch`, `MeleeReactionPolicy`, `AgentAiWaker`, `BattleTroopLedger`, `BattleObservationLedger`; commands `coop.debug.battle.timeline`, `mark`, `state`, `kill_random_troop`, `kill_all_but_one`, `kill_enemy` and the rest of `BattleTeamKillCommands`, `BattleOwnershipCensusCommand`, `BattleTeamDiagnostics`, `BattleSnapshotDebugCommand`, `BattleStrengthExpectationCommand` | `BattleDamageMirrorTests`, `BattleDeathMirrorTests`, `BattleRoutMirrorTests`, `BattleCasualtyVerticalTests`, `BattleTroopLedgerTests`, `BattleMeshRoutingTests`, `BattleMessageBatchingTests` |
| Reinforcements | `ReinforcementFielder`, `CoopReinforcementPacing`, `LiveBattleReinforcementTickPatch`, `SupplyProgressReporter`, `NetworkBattleTroopReserve` (reserve REPLACE with the receiver's supplied pointers), `NearbyPartyReinforcementHandler` (AI parties pulled into a player's battle), `BattleDeploymentCoordinator`; command `coop.debug.battle.size_state`, `BattleSpawnProgressCommand` | `BattleReinforcementSpawnTests`, `BattleReinforcementFieldingCapTests`, `BattleReinforcementScatterTests`, `BattleReinforcementCasualtyQuotaTests`, `CoopReinforcementIntervalTests`, `ReinforcementFieldingAllowanceTests`, `ReinforcementPostureTests`, `BattleLateJoinReinforcementTests` |
| Late join, rejoin, host | `BattleJoinLeaveHandler`, `BattleHostHandler`, `BattleAuthorityMigrator`, `HostEpochPolicy`, `BattleRoundRestarter`; commands `late_join_mode_*`, `join_existing`, `enter_current_battle` | `BattleLateJoinSpawnTests`, `HostElectionTests`, `HostEpochTests`, `HostMigrationTests`, `BattleMigrationContinuityTests`, `BattleMigrationMirrorTests`, `BattleRoundRestartTests` |
| End of battle | `BattleResultReadyLogic`, `BattleResultCommitter`, `BattleFinalizeHandler`, `MapEventResultsHandler`, `BattleLootTransactionHandler`, `BattleLootClientHandler`, `BattleLootAnswerPatch`, `BattleLootDeclinedTroopsPatch`, `TakePrisonerActionPatches`, `HitRewardHandler`, `ScoreboardUpgradeTotals`, `MapEventPartyPatches.CommitXpGain` (with the M12 `[CommitXp]` diagnostic), `BattleRetreatInterface`, `TryToGetAwayPatches`, development's authoritative field-battle leave, `ClientBattleSimulationGuardPatch`, `PostBattleWalkAutoAdvancePatch`; commands `encounter_state`, `get_events`, `retreat_confirmation`, `battle_reward_fixture_*` | `BattleResultReadyTests`, `CoopBattleResultCommitTests`, `CoopBattleFinalizeTests`, `BattleRetreatCasualtyTests`, `BattleNonHostRetreatDespawnTests`, `BattleAbandonmentTests`, `BattleSurrenderTests` |
| Sieges | `CoopSiegeBattleLauncher`, `CoopSiegeDeploymentMissionController`, `SiegeEngineDeploymentReplicator`, `SiegeMachineStateReplicator`, `SiegeWeaponFireReplicator`, `SiegeLadderAuthorityPatches`, `SiegeMachineAuthorityPatches`, `SiegeCaptureMenuHoldPatch`, `coop.debug.siege.*` including `sally_state` | `SiegeInteractionBattleTests`, `SiegeBattleFreezeTests` |

Open items inherited from earlier work that this plan will meet head on: M6 (ownership imbalance,
irrelevant here because each player owns one side, but the load it describes lands on the client),
M12 (troop XP lost when loot is discarded; the `[CommitXp]` diagnostic is in the tree and has not yet
been read against a real battle), the "damage feedback round trip" (unmeasured), and the battle-size
mismatch recorded in `engine_size`.

---

## 3. The army rig

Extends the duel rig. New pieces are marked.

### 3.1 Fixture (new)

`coop.debug.map_event.pvp_army_fixture <controllerA> <controllerB> <troopsA> <troopsB> [lordsA] [lordsB] [reserveA] [reserveB]`
on the server:

- Sets each player's party to the requested troops of its culture (`set_troops`), attaches the requested
  number of AI lord parties to each player's army (`coop.debug.army.attach_all` after adding them, the
  M11 lesson: a member is not attached), optionally oversizes the rosters past the battle size so
  reinforcement waves must happen, splits the factions and declares war, places the armies 150 m apart,
  pre-answers `troop_preference all` for both, and records everything for `restore_player_field_battle`.
- Presets: `even_small` 100 v 100, `even_medium` 400 v 400, `cap` 800 v 800 with battle size 400 so
  half of each side is reserve, `asymmetric` 200 v 700, `lords` 200 + 2 lords v 200 + 2 lords,
  `nearby` with a hostile AI party 300 m away that `NearbyPartyReinforcementHandler` should pull in.
- A siege variant `pvp_siege_fixture` for A8: player B holds a castle with a garrison, player A besieges
  with engines pre-built.

The `pvp_duel` save from the first plan is the base; the army fixture only changes rosters and
attachments, so `restore_player_field_battle` returns it to the duel state.

### 3.2 Driver

`Army-Test.ps1` (new) runs a scenario end to end without clicks: fixture, both players attack or one
attacks and the other accepts, `start_attack_mission`, `finish_deployment` on both, then one of:

| Scenario script | Content |
|---|---|
| `charge` | both sides `charge_owned_formations`; fight to the end |
| `hold_then_charge` | A holds 60 s while B charges, then A charges; tests orders and one-sided engagement |
| `waves` | `cap` preset; measure every reinforcement wave until reserves are empty |
| `lords` | `lords` preset; verify attached lords' troops are fielded on both sides and both machines |
| `nearby` | `nearby` preset; verify the AI party joins the right side on both machines |
| `late_join` | B disconnects at 30 s and rejoins; `late_join_mode_*` commands drive the rejoin |
| `host_migration` | the battle host leaves at 45 s; the other player must become host and the battle continue |
| `retreat` / `surrender` / `leave` | each end path, each player as the one who triggers it |
| `siege` | A8 only |

Each scenario runs twice with the players' roles swapped, so ownership of each side is covered both ways.

### 3.3 Recording (new pieces)

- **Per-second census on both machines and the server**, written to `ArmyEvents`: agents alive / spawned
  / killed / wounded / routed per side per machine (from `BattleOwnershipCensusCommand` and
  `BattleTeamDiagnostics` sampled by the script), reserve remaining and waves fielded per side
  (`size_state`, `SupplyProgressReporter`), formation centroid per formation, orders issued, damage
  routed versus applied (`BattleDamageRouter` counters), mesh messages per second and `worldQueue`
  (`Capture-Load.ps1`), fps, and the two battle sizes (`engine_size`) at start.
- **Roster snapshots** before the battle, at every wave, and after finalization: a new
  `coop.debug.mobile_party.roster_hash <partyId>` that prints member and prisoner rosters as sorted
  `characterId:healthy/wounded/xp` lines plus a hash, run on both clients and the server for every party
  in both armies. The hash makes divergence a one-line diff; the lines say where.
- **Outcome record**: `encounter_state`, `get_events`, scoreboard totals, loot and prisoner rosters,
  `[CommitXp]` lines, hero health, army cohesion and attached parties after the battle, on all three.
- The existing `BattleObservationLedger` timeline (`coop.debug.battle.timeline`, `mark`) brackets each
  scenario step so the census can be aligned to it.

### 3.4 Analyzer (new)

`analyze_army.py <hostEvents> <clientEvents> <serverEvents> [--baseline <dir>]` aligns the three
census streams on the shared clock and prints, per scenario and role swap:

| Metric | Definition | Target |
|---|---|---|
| Start skew | time between the two clients entering the mission, and between deployment finishes | <= 3 s |
| Battle-size agreement | server `battleSize` versus each client's `engine_size` | equal, or the mismatch is applied identically to both sides |
| Allocation | agents fielded per side in the opening wave versus the allocation `size_state` reports | exact |
| Spawn mirror | for every owned agent, the puppet exists on the other machine with the same character, equipment hash, team, formation, mount | 100%, within one update |
| Reserve accounting | spawned + reserve + casualties == roster, per side, every second | exact |
| Kill / wounded / rout mirror | per-side counts identical on both machines and the server at every sample | exact, within one update |
| Fairness | kills per 100 troops for side A versus side B in `charge`, averaged over both role swaps, with identical troop types | within 10% |
| Routed damage latency | blow on the attacker's machine to health change on the owner's, p50 / p99, at 400 v 400 | <= one hop / <= two hops |
| Puppet swing-through | `MeleeReactionPolicy` rewrites and suppressed-blow counters per side | 0 rewrites of a locally owned attacker |
| Wave arrival | for each reinforcement wave: count, time and spawn position on both machines | same count, within 1 s, positions within 5 m |
| Attached lords fielded | troops from attached AI parties present on both machines, both sides | 100% of the allocation |
| Nearby joiner | the AI party's side, arrival time and troops on both machines | same side, within 1 s |
| Late join / rejoin | census on the rejoining client matches the host within one update after the baseline load; no `party count mismatch` | exact |
| Host migration | battle continues; census continuous across the epoch change; no duplicate or missing agents | exact |
| Orders | own formation centroids respond on both machines; enemy centroids unaffected by the player's orders | 100% / 0% |
| Mesh load | messages per second per peer and `worldQueue` at 400 v 400 and 800 v 800 | within the section 4 budget |
| Client fps | Omar's laptop at 400 v 400 | not below the PvE 400-a-side run by more than 2 fps median |
| End state | rosters, prisoners, loot, XP, scoreboard, hero health identical on both machines and the server; both players on the map; armies intact | exact; `roster_hash` equal on all three |

---

## 4. Load budget for PvP army battles

In PvE the host owns most agents, so most blows are local. In a PvP army battle every blow is routed,
every death and rout is mirrored, and both peers send a full side of movement. The load expectation
must be written down before the first run so a surprise is recognised as one:

| Quantity | PvE 1,000-agent reference (`COMBAT-REMAINING-WORK.md`) | PvP 400 v 400 expectation | Budget |
|---|---|---|---|
| Movement wire per peer | 644 KB/s for ~1,000 owned at 20 Hz | ~250 KB/s each way for 400 owned, plus the player lane | <= 300 KB/s |
| Routed blows | a fraction of all blows | all blows: roughly 30-60 per second at peak, each with a routed message | measured; the reliable mesh queue must stay under 100 messages |
| Death / rout messages | mirrored for the puppet half | mirrored for every agent | included above |
| `worldQueue` on the server | overloads seen at 1,800+ | battle traffic is mesh-only; the server sees scoreboard and autosync scalars | flat; scoreboard clamp (M5) must hold |
| Receiver apply on the client | 31 ms/s mean, 634 ms/s peak in Omar's 28 Aug log | at most the PvE figure | `receiverQueueMs` p99 <= 50 ms |

If a scenario exceeds the budget, the fix is on the message-count side first (coarser keys, batching,
`SendCoalescer`), never on precision of the player lane.

### 4.1 CPU and network are recorded in every run, before and after every fix

`Capture-Load.ps1` runs for the whole of every scenario, on all three processes (server, host client,
Omar's or the second client), sampling once per second. The samples are stored, not summarised away:

| Stored file (per run folder `Scripts/Rig/runs/<date>-<label>/`) | Content |
|---|---|
| `load-server.csv`, `load-host.csv`, `load-client.csv` | one row per second: `wireBytesPerSecond`, `bulkHz`, `priorityHz`, `localAgents`, `agents`, `senderMsPerSecond`, `receiverApplyMsPerSecond`, `receiverQueueMs`, `fps`, process CPU %, working set MB, thread count, mesh RTT |
| `mesh-profile-*.txt` | the `[Mesh] Packet profile` dumps: messages per second and bytes per message type, DIRECT vs RELAY per controller |
| `server-profile.txt` | the server `Packet profile` lines: `worldQueue` per window and the top message types by count |
| `hotpath.txt` | `HotPathCostDiagnostics` snapshot at the end: calls/s, microseconds per call, ms per second for every instrumented path, including any path a fix adds |
| `census.csv` | the per-second battle census (section 3.3), so load can be read against agent count and battle phase |
| `report.md` | `analyze_army.py` output; with `--baseline` it adds a delta column for every load metric as well as every sync metric |

BEFORE is the run on the build without the fix, AFTER the run on the build with it, three cycles each,
same scenario, same preset, same role assignment. The load deltas are reported the way
`COMBAT-REMAINING-WORK.md` section 1b does: per-path CPU from `hotpath.txt` is the number that counts;
whole-process CPU %, wire and message rates are reported as median and spread over the three cycles,
against the same battle phase (aligned on `census.csv`), and a delta inside the spread is "no change".

Every scenario in the baseline table (section 8.1) therefore carries CPU and network columns, every
ledger row (section 8.2) carries the added cost of the fix, and the release summary (section 8.3)
carries the whole plan's start-to-end cost on both machines. No fix is merged without those columns
filled.

---

## 5. Hypotheses to confirm or kill

| # | Hypothesis | Why it is suspected | Scenario |
|---|---|---|---|
| A1 | All-routed damage roughly doubles mesh message rate versus PvE and pushes the client into the overload behaviour seen in the 28 Aug log | Rule 6; the reliable send window is the bottleneck | `charge` at 400 v 400 and 800 v 800, `Capture-Load.ps1` |
| A2 | The client's side is under-fielded or reinforced later than the host's | `CoopBattleMissionSpawnHandler` sizes to what THIS client's supplier owns; PLAN-2 found a 786-man side stalled at 250 | `waves`, both role swaps |
| A3 | Battle size disagreement between the server setting and each client's engine setting caps reinforcement differently per side | `engine_size` command exists because it happened | every scenario records both; `waves` shows the effect |
| A4 | Puppet routs are not mirrored promptly, leaving ghosts that fight on one machine and flee on the other | rout mirror exists but was measured in PvE only | `charge`, rout mirror metric |
| A5 | Rosters diverge at the end: killed versus wounded accounting, XP lost when loot is declined (M12) | roster divergence memory; `[CommitXp]` never read against a real battle | end state, `roster_hash` on all three |
| A6 | Attached AI lords' troops are fielded on one machine but not the other, or on the wrong side | M11 showed attached-party handling has gaps on the server | `lords` |
| A7 | A nearby AI party joins the wrong side or only on one machine | `NearbyPartyReinforcementHandler` was built for PvE ("pull nearby AI parties into a player's battle") | `nearby` |
| A8 | Late join into a PvP battle puts the joiner on the wrong team or double-spawns their side | team assignment assumes the AI is one side | `late_join` |
| A9 | Host migration mid-battle drops or duplicates reinforcements | `HostEpochStaleConclusionTests` exist because stale conclusions happened | `host_migration` |
| A10 | Fairness: units owned by the client kill fewer per capita than units owned by the host, because routed damage lands late and puppets read as tougher | "damage against puppet cavalry 133 vs 56" and the unmeasured round trip | `charge`, fairness metric, both role swaps |
| A11 | Retreat, surrender and leave leave one player stuck in a menu or with a different casualty result | `d0ac6da38`, dev's leave fix and the retreat interface all touched this recently | `retreat` / `surrender` / `leave` |
| A12 | Orders leak: charging my formations also moves the enemy's puppets or resets their formation | `AgentFormationAssigner` and `BattleDeploymentCoordinator` set `formation.AI.Side` on puppets | `hold_then_charge`, orders metric |

---

## 6. Milestones

Each: **run the scenario with load capture on, read the census -> fix only what disagrees -> unit or E2E
guard -> re-run the same scenario with `--baseline` -> both players watch it in game -> ledger row** in
section 8.2 with the fix's before/after sync numbers AND its added wire, message, CPU and fps cost.

| # | Milestone | Depends on | Exit criteria | Cost rule |
|---|---|---|---|---|
| A0 | Army rig: fixture and presets, `Army-Test.ps1`, census recording, `roster_hash`, `analyze_army.py`, baseline for every scenario in both role swaps, load budget checked | PVP-SYNC P0-P1 | Section 8 baseline filled; every hypothesis A1-A12 has a number | DEBUG only |
| A1 | Start, sizing and spawn mirror | A0, A2 A3 | start skew <= 3 s; allocation exact; spawn mirror 100%; battle size handled identically per side | No change to the wire format; sizing is server data already sent |
| A2 | Fighting at scale: mirrors, fairness, load | A0, A1, A4 A10 | kill / rout mirror exact; fairness within 10%; routed damage p99 <= two hops; mesh load within section 4 | Message count first; batching and coalescing over new messages |
| A3 | Reinforcements: waves, attached lords, nearby joiners | A1, A2 A6 A7 | wave arrival, lords fielded and nearby joiner targets met, both role swaps | Reuse the reserve REPLACE path; no per-wave chatter added |
| A4 | Commanding | A1, A12 | orders 100% own / 0% enemy on both machines | Local only |
| A5 | Late join, rejoin, host migration | A1-A3, A8 A9 | census matches within one update after rejoin; migration continuous; no duplicates | Reuse the join baseline; never weaken the count check (rule 3) |
| A6 | End of battle: results, loot, prisoners, XP (M12), scoreboard, armies intact | A2, A5, A11 | `roster_hash` equal on all three after finalization in every end path; M12 closed with its `[CommitXp]` evidence; both players on the map | Server-authoritative spoils path only |
| A7 | Two-machine and robustness | A1-A6 | all targets hold on Omar's laptop at 400 v 400 and under 80 / 150 ms simulated delay; fps target met | Section 4 budget |
| A8 | Siege between players | A1-A7 | engines, ladders, walls, defenders, sally-out and capture menu mirrored; `sally_state` and the capture hold verified live | As A2 |
| A9 | Hardening and release | A1-A8 | Release build audited for diagnostics; all guards green; CoopFixes packaged; section 8 complete; a full 400 v 400 army battle played end to end by both players with matching results | Package via `package-share.ps1` |

Where a milestone's hypotheses all come back clean, it is closed as "checked, not a bug" with its numbers,
and nothing is changed.

---

### 6.1 Status after the A0 pass (5 Sep 2026, one machine)

| Milestone | Status | Evidence |
|---|---|---|
| A0 | done for every scenario the rig can drive; `lords`, `nearby`, surrender / leave and siege are deferred with the reason in 8.1 | 8.1 (29 rows), 8.1.1 |
| A1 | met by the baseline, no change needed: start skew 0.1-0.5 s, allocation exact at 100 / 400 / 800-cap, identity hashes equal on both machines, coop battle size applied identically | 8.1 |
| A2 | mirrors exact and fairness within 1 percent with equal heroes; routed damage p50 24-33 ms, p99 42-91 ms on loopback (processing only); load at 400 v 400 on ONE machine is at or just over the section 4 budget (wire p99 300-470 KB/s, receiver queue p99 36-79 ms) with movement, not damage, dominating; judged on two machines at A7 before any load work; the unkillable-troop diagnostic false positives fixed (ledger) | 8.1, 8.1.1, 8.2 |
| A3 | open: `lords` / `nearby` presets need a Qin lord party located and the vanilla joiner distance respected | 8.1 notes |
| A4 | met by the baseline: hold_then_charge moved the holder 0 m and the charger 200 m on both machines | 8.1 |
| A5 | partial: host migration works (16-61 s hole, adopted side leaderless); rejoin blocker 1 fixed (start re-sent), blocker 2 open (returner's hero origin missing from the returned reserve) | 8.1.1, 8.2 |
| A6 | partial: rosters agree on all three in every run; retreat defect found and fixed (ledger); surrender / leave still covered by E2E only; `[CommitXp]` lines confirm XP commits on the server | 8.1.1, 8.2 |
| A7 | not started (needs Omar's machine) | |
| A8 | not started | |
| A9 | not started; nothing committed yet, all work is in the working tree | |

## 7. Test strategy

| Layer | What | Runs |
|---|---|---|
| Unit | Sizing arithmetic per side, wave quota, reserve accounting, fairness of routing (no owner-dependent branch in damage math), roster hash determinism | every build |
| E2E mock engine | The existing battle suites above plus new cases for two player-owned sides: spawn mirror with both sides player-owned, rout mirror under full routing, late join into a PvP map event, host migration with reserves outstanding, end-of-battle roster commit for both sides | every build |
| Live rig, one machine | `Army-Test.ps1` all scenarios both role swaps, `analyze_army.py --baseline` | every milestone, before and after each fix |
| Live rig, two machines | Same, Ali's PC and Omar's laptop | A2, A5, A7, A8, A9 |
| Human check | Both players fight a full army battle and compare scoreboard, loot and rosters on screen | A1, A3, A6, A9 |

---

## 8. Before/after record

### 8.1 Baseline (filled by A0)

Sync columns first, then the load columns from `load-*.csv`, `mesh-profile-*.txt`, `server-profile.txt`
and `hotpath.txt` (section 4.1). "h/c/s" = host client / second client / server. Load figures are the
median over the three cycles at peak agent count, with the spread in the run's `report.md`.

| Scenario | Roles | Start skew s | Size agree | Allocation | Spawn mirror % | Reserve acct | Kill mirror | Rout mirror | Fairness A/B | Routed dmg p50/p99 ms | Waves ok | Lords ok | Nearby ok | Orders own/enemy | End state | Wire KB/s (h/c) | Mesh msgs/s (h/c) | Server msgs/s | worldQueue | Sender ms/s (h/c) | Receiver apply ms/s (h/c) | Recv queue p99 ms (h/c) | Process CPU % (h/c/s) | Working set MB (h/c/s) | fps median (p5) (h/c) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| charge 100v100 | Omar host, Ali client; Ali attacks (`1712-army-100`) | 5.2 / 0.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | count diff median 0 max 1 (21 samples) | peak agents 204 / 204 | per-side alive counts: max diff 2 / 2 | fleeing 0 / 0 at the end | Ali's side lost 70 of 103, Omar's lost 101 of 103; puppet hits 566 @ 21.7 dmg vs 515 @ 20.2 dmg; enter skew is the rig's 5 s poll; side start counts include the two riders' horses in this first run | - | n/a | n/a | n/a | n/a | AttackerVictory on both at +103 s (skew 0.0 s); rosters agree on counts on all three | 73.6 / 81.3 | 168 / 209 | 31 med, 166 peak | 0 (max 0) | 33.6 / 29.0 | 5.3 / 5.5 | 49 / 54 | 26.2 / 26.6 / 7.3 | 4621 / 5220 / 941 | 192 (p5 144) / 189 (p5 131) |
| charge 100v100 | Omar host, Ali client; Omar attacks (`1720-army-100-swap`) | 0.2 / 0.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | count diff median 0 max 2 (28 samples) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 2 | fleeing 0 / 0 at the end | Ali's side lost 52 of 101, Omar's lost 101 of 101; puppet hits 534 @ 23.4 dmg vs 407 @ 20.0 dmg | - | n/a | n/a | n/a | n/a | DefenderVictory on both at +138 s (skew 0.0 s); rosters agree on counts on all three | 11.5 / 88.0 | 71 / 151 | 12 med, 150 peak | 0 (max 1) | 16.3 / 31.8 | 5.7 / 1.1 | 44 / 47 | 26.4 / 25.2 / 8.2 | 4571 / 5337 / 950 | 191 (p5 162) / 190 (p5 162) |
| charge 100v100 (Omar's start delayed 25 s) | Omar host, Ali client; Ali attacks (`1746-army-100-alihost`) | 25.3 / 5.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 10 / 10 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 1 / 1 | fleeing 0 / 0 at the end | Ali's side lost 68 of 101, Omar's lost 101 of 101; puppet hits 560 @ 21.9 dmg vs 480 @ 22.1 dmg; Omar still elected host: election is by mission-ready time, Ali's scene load took longer | - | n/a | n/a | n/a | n/a | AttackerVictory on both at +145 s (skew 0.0 s); rosters agree on counts on all three | 24.8 / 63.2 | 97 / 148 | 10 med, 185 peak | 0 (max 1) | 18.5 / 28.6 | 4.3 / 2.1 | 55 / 38 | 25.2 / 25.6 / 8.5 | 4593 / 4963 / 921 | 194 (p5 163) / 193 (p5 163) |
| charge 400v400 | Omar host, Ali client; Ali attacks (`1726-army-400`) | 0.2 / 0.5 | 800 coop, engine agrees on both | def 401/400 atk 401/400 (fielded/target) | count diff median 0 max 2 (55 samples) | peak agents 802 / 802 | per-side alive counts: max diff 0 / 2 | fleeing 0 / 0 at the end | Ali's side lost 336 of 400, Omar's lost 400 of 400; puppet hits 2458 @ 20.0 dmg vs 2332 @ 20.3 dmg | - | n/a | n/a | n/a | n/a | AttackerVictory on both at +278 s (skew 0.0 s); rosters agree on counts on all three | 135.5 / 163.5 | 291 / 332 | 43 med, 225 peak | 0 (max 1) | 49.1 / 59.0 | 12.5 / 10.7 | 62 / 79 | 28.8 / 28.7 / 8.5 | 5243 / 4597 / 928 | 117 (p5 64) / 114 (p5 62) |
| charge 400v400 | Omar host, Ali client; Omar attacks (`1733-army-400-swap`) | 0.2 / 0.5 | 800 coop, engine agrees on both | def 401/400 atk 401/400 (fielded/target) | identity hash agrees 16 / 19 samples (misses are 1-2 deaths in flight) | peak agents 802 / 802 | per-side alive counts: max diff 1 / 3 | fleeing 0 / 0 at the end | Ali's side lost 332 of 400, Omar's lost 400 of 400; puppet hits 2394 @ 20.6 dmg vs 2400 @ 20.1 dmg | - | n/a | n/a | n/a | n/a | DefenderVictory on both at +270 s (skew 0.0 s); rosters agree on counts on all three | 189.3 / 171.5 | 332 / 344 | 21 med, 222 peak | 0 (max 0) | 58.5 / 63.7 | 12.9 / 12.4 | 39 / 36 | 29.1 / 28.5 / 8.6 | 4373 / 4944 / 944 | 104 (p5 65) / 103 (p5 64) |
| charge 800v800 (cap 400, waves) | Ali host, Omar client; Ali attacks (`1751-army-800-cap400`) | 0.1 / 0.3 | 400 coop, engine DIFFERS | def 801/200 atk 801/200 (fielded/target) | identity hash agrees 29 / 38 samples (misses are 1-2 deaths in flight) | peak agents 402 / 402 | per-side alive counts: max diff 11 / 2 | fleeing 0 / 0 at the end | Ali's side lost 96 of 200, Omar's lost 200 of 200; puppet hits 4594 @ 21.0 dmg vs 4631 @ 21.0 dmg; casualties over the whole battle: Ali's side 696 of 800, Omar's 800 of 800 (server WarStats); Ali was HOST here and still won | - | n/a | n/a | n/a | n/a | AttackerVictory on both at +627 s (skew 0.0 s); ROSTERS DIFFER | 172.0 / 155.8 | 325 / 315 | 103 med, 176 peak | 0 (max 1) | 54.3 / 47.3 | 11.4 / 13.2 | 52 / 47 | 28.9 / 29.2 / 8.4 | 4848 / 4646 / 977 | 117 (p5 100) / 117 (p5 98) |
| hold_then_charge 100v100 (Ali holds 60 s) | Omar host, Ali client; Ali attacks (`1740-army-100-hold`) | 0.2 / 0.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 14 / 14 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 1 | fleeing 0 / 0 at the end | Ali's side lost 62 of 101, Omar's lost 101 of 101; puppet hits 567 @ 21.9 dmg vs 474 @ 20.9 dmg | - | n/a | n/a | n/a | holder moved 0 m during hold, charger 202 m; same on both machines | AttackerVictory on both at +196 s (skew 0.0 s); rosters agree on counts on all three | 33.9 / 60.7 | 54 / 116 | 9 med, 163 peak | 0 (max 1) | 19.2 / 28.7 | 4.2 / 2.9 | 37 / 23 | 25.8 / 27.8 / 8.5 | 4564 / 4878 / 946 | 195 (p5 168) / 195 (p5 160) |
| charge 100v100 (traced) | Ali host, Omar client; Ali attacks (`1810-army-100-t1`) | 0.2 / 0.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 9 / 10 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 1 / 1 | fleeing 0 / 0 at the end | Ali's side lost 75 of 101, Omar's lost 101 of 101; puppet hits 558 @ 21.9 dmg vs 523 @ 21.3 dmg; troop HealthLimit Ali's 123 vs Omar's 110; census: captains none, weapon mix equal | - | n/a | n/a | n/a | n/a | AttackerVictory on both at +145 s (skew 0.0 s); rosters agree on counts on all three | 46.5 / 24.2 | 115 / 82 | 10 med, 158 peak | 0 (max 0) | 25.3 / 17.5 | 1.9 / 3.9 | 47 / 44 | 25.6 / 25.3 / 8.5 | 5372 / 4779 / 938 | 193 (p5 166) / 194 (p5 171) |
| charge 100v100 (traced) | Omar host, Ali client; Omar attacks (`1815-army-100-t2-swap`) | 0.2 / 0.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 11 / 12 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 1 | fleeing 0 / 0 at the end | Ali's side lost 82 of 101, Omar's lost 101 of 101; puppet hits 562 @ 22.0 dmg vs 563 @ 21.3 dmg; troop HealthLimit Ali's 123 vs Omar's 110; census: captains none, weapon mix equal | - | n/a | n/a | n/a | n/a | DefenderVictory on both at +170 s (skew 0.0 s); rosters agree on counts on all three | 40.7 / 50.2 | 118 / 145 | 9 med, 147 peak | 0 (max 1) | 17.6 / 23.1 | 3.3 / 2.8 | 90 / 46 | 23.8 / 23.7 / 8.5 | 4356 / 5194 / 939 | 195 (p5 160) / 194 (p5 161) |
| charge 400v400 (traced) | Omar host, Ali client; Ali attacks (`1821-army-400-t1`) | 0.2 / 0.5 | 800 coop, engine agrees on both | def 401/400 atk 401/400 (fielded/target) | identity hash agrees 13 / 18 samples (misses are 1-2 deaths in flight) | peak agents 802 / 802 | per-side alive counts: max diff 2 / 1 | fleeing 0 / 0 at the end | Ali's side lost 353 of 400, Omar's lost 400 of 400; puppet hits 2448 @ 20.3 dmg vs 2461 @ 20.3 dmg; troop HealthLimit Ali's 123 vs Omar's 110; trace capped at 60k lines (first 102 s), so the funnel counts are partial; latency valid | ali->omar 33/91 (105% applied); omar->ali 32/88 (105% applied) | n/a | n/a | n/a | n/a | AttackerVictory on both at +268 s (skew 0.0 s); rosters agree on counts on all three | 144.8 / 142.8 | 303 / 306 | 21 med, 219 peak | 0 (max 1) | 52.4 / 49.5 | 11.3 / 11.9 | 71 / 70 | 27.6 / 27.1 / 8.4 | 4903 / 4907 / 946 | 100 (p5 60) / 102 (p5 59) |
| charge 800v800 (cap 400, waves) | Omar host, Ali client; Omar attacks (`1828-army-800-cap400-swap`) | 0.2 / 0.3 | 400 coop, engine DIFFERS | def 801/200 atk 801/200 (fielded/target) | identity hash agrees 31 / 40 samples (misses are 1-2 deaths in flight) | peak agents 402 / 402 | per-side alive counts: max diff 1 / 10 | fleeing 0 / 0 at the end | Ali's side lost 114 of 200, Omar's lost 200 of 200; puppet hits 4655 @ 20.8 dmg vs 4633 @ 21.0 dmg; troop HealthLimit Ali's 123 vs Omar's 110; casualties over the whole battle: Omar's side 801 of 800, Ali's 715 (server WarStats); 800-troop roster replace reached both clients in 1.9 s; trace capped at 60k lines | ali->omar 29/72 (99% applied); omar->ali 29/83 (105% applied) | n/a | n/a | n/a | n/a | DefenderVictory on both at +615 s (skew 0.0 s); rosters agree on counts on all three | 154.2 / 173.2 | 304 / 325 | 117 med, 174 peak | 0 (max 1) | 45.8 / 53.6 | 12.9 / 11.4 | 51 / 41 | 28.6 / 28.6 / 8.3 | 4840 / 4930 / 955 | 115 (p5 95) / 116 (p5 96) |
| charge 100v100 (equal heroes attempted) | Ali host, Omar client; Ali attacks (`1841-army-100-equal`) | 0.2 / 0.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 12 / 12 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 1 / 1 | fleeing 0 / 0 at the end | Ali's side lost 101 of 101, Omar's lost 66 of 101; puppet hits 492 @ 19.8 dmg vs 601 @ 22.5 dmg; troop HealthLimit Ali's 123 vs Omar's 110; reset_skills was given the registry id and failed, heroes unchanged; Omar WON this one despite the 110-vs-123 health handicap | ali->omar 27/74 (99% applied); omar->ali 25/58 (99% applied) | n/a | n/a | n/a | n/a | DefenderVictory on both at +162 s (skew 0.0 s); rosters agree on counts on all three | 32.7 / 63.0 | 86 / 134 | 10 med, 151 peak | 0 (max 1) | 18.4 / 27.5 | 4.8 / 2.8 | 64 / 140 | 24.8 / 24.1 / 8.4 | 4567 / 4987 / 932 | 191 (p5 151) / 191 (p5 153) |
| charge 100v100 (equal heroes) | Ali host, Omar client; Omar attacks (`1847-army-100-equal-swap`) | 0.2 / 0.9 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 11 / 12 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 1 / 1 | fleeing 0 / 0 at the end | Ali's side lost 101 of 101, Omar's lost 85 of 101; puppet hits 479 @ 21.1 dmg vs 498 @ 22.7 dmg; troop HealthLimit Ali's 100 vs Omar's 100; both heroes reset: every troop at 100 health on both machines; Omar won | ali->omar 24/42 (99% applied); omar->ali 24/81 (98% applied) | n/a | n/a | n/a | n/a | AttackerVictory on both at +159 s (skew 0.0 s); rosters agree on counts on all three | 27.6 / 36.0 | 100 / 138 | 10 med, 149 peak | 0 (max 0) | 17.1 / 20.3 | 3.1 / 3.0 | 68 / 48 | 24.0 / 23.1 / 8.5 | 4885 / 5047 / 936 | 197 (p5 178) / 197 (p5 176) |
| charge 400v400 (equal heroes) | Ali host, Omar client; Ali attacks (`1852-army-400-equal`) | 0.2 / 0.5 | 800 coop, engine agrees on both | def 401/400 atk 401/400 (fielded/target) | identity hash agrees 16 / 17 samples (misses are 1-2 deaths in flight) | peak agents 802 / 802 | per-side alive counts: max diff 1 / 1 | fleeing 0 / 0 at the end | Ali's side lost 339 of 400, Omar's lost 400 of 400; puppet hits 2232 @ 20.0 dmg vs 1981 @ 20.0 dmg; troop HealthLimit Ali's 100 vs Omar's 100; both at 100 health; Ali won; over the two equal-hero runs kills per fielded man are 0.93 for Ali and 0.93 for Omar | ali->omar 28/68 (99% applied); omar->ali 28/64 (99% applied) | n/a | n/a | n/a | n/a | AttackerVictory on both at +244 s (skew 0.0 s); rosters agree on counts on all three | 171.5 / 181.0 | 333 / 332 | 15 med, 178 peak | 0 (max 1) | 60.3 / 59.2 | 12.1 / 11.6 | 56 / 36 | 28.6 / 28.6 / 8.5 | 4843 / 4745 / 950 | 109 (p5 65) / 114 (p5 65) |
| hold_then_charge | Omar holds | | | | | | | | | | | | | | | | | | | | | | | | |
| waves | both | covered by the two cap-400 rows above (reinforcements flow until the 800-man rosters are spent) | | | | | | | | | | | | | | | | | | | | | | | |
| lords | both | not run in A0: the fixture pieces exist (`coop.debug.army.create <kingdom> <settlement> <leaderHero>`, `mobile_party_add`, `attach_all`) but no server command lists a kingdom's lord parties, so a Qin lord to attach to Alifreeze's army has to be found by hand first; A3 | | | | | | | | | | | | | | | | | | | | | | | |
| nearby | both | not run in A0: `NearbyPartyReinforcer` hands the choice to vanilla's joiner selection scoped to the client party, so the preset needs an AI party of a faction at war with one side parked inside the vanilla join distance and left to decide; A3 | | | | | | | | | | | | | | | | | | | | | | | |
| host_migration 100v100 (host process killed at 46 s) | Ali host, Omar client; Ali attacks (`1906-army-100-crash-host`) | 0.2 / 0.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 4 / 4 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 0 | fleeing 0 / 0 at the end | Ali's side lost 0 of 101, Omar's lost 0 of 101; puppet hits - @ - dmg vs 1090 @ 26.6 dmg; troop HealthLimit Ali's 100 vs Omar's 100; Omar became host 61 s after the kill (peer timeout); until then Ali's 100 puppets took no damage; then they fought on leaderless and lost 101 to 17; Ali's party destroyed | - | n/a | n/a | n/a | n/a | None on both at +- s (skew - s); rosters agree on counts on all three | 145.9 / 0.0 | 195 / 256 | 13 med, 149 peak | 0 (max 92) | 45.4 / 17.4 | 9.2 / 0.0 | 96 / 31 | 27.8 / 25.0 / 8.6 | 4882 / 4398 / 944 | 190 (p5 176) / 200 (p5 185) |
| late_join 100v100 (client killed at 31 s, relaunched 15 s later) | Omar host, Ali client; Ali attacks (`1912-army-100-crash-client-rejoin`) | 0.2 / 0.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 2 / 2 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 0 | fleeing  / 0 at the end | Ali's side lost 0 of 101, Omar's lost 0 of 101; puppet hits - @ - dmg vs 686 @ 23.6 dmg; troop HealthLimit Ali's 100 vs Omar's 100; the relaunch reconnected in 12 s but the rig re-entered too early (NRE from enter_current_battle before the campaign had loaded) and gave up after one try; the battle ran on without Ali: 101 casualties, party destroyed | - | n/a | n/a | n/a | n/a |  on both at +- s (skew - s); rosters agree on counts on all three | 0.0 / 148.4 | 244 / 162 | 8 med, 151 peak | 0 (max 0) | 9.0 / 41.9 | 0.0 / 11.2 | 7 / 21 | 21.7 / 27.3 / 8.5 | 4625 / 5184 / 986 | 196 (p5 172) / 191 (p5 162) |
| host_migration + late_join 100v100 (host killed at 32 s, relaunched 15 s later, before the rejoin fix) | Ali host, Omar client; Ali attacks (`1930-army-100-crash-host-rejoin`) | 0.1 / 0.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 2 / 2 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 0 | fleeing  / 0 at the end | Ali's side lost 0 of 101, Omar's lost 0 of 101; puppet hits - @ - dmg vs 677 @ 23.8 dmg; troop HealthLimit Ali's 100 vs Omar's 100; Omar became host 16 s after the kill; the relaunched Ali reconnected in 13 s and asked to enter after 57 s, but the server answered "already in this mission; not re-sending its start": no mission opened, Ali watched his army die from the map (101 casualties, party destroyed) | - | n/a | n/a | n/a | n/a |  on both at +- s (skew - s); rosters agree on counts on all three | 161.9 / 0.0 | 145 / 271 | 8 med, 148 peak | 0 (max 0) | 48.0 / 9.3 | 9.4 / 0.0 | 20 / 8 | 27.4 / 21.8 / 8.5 | 4842 / 4565 / 962 | 188 (p5 158) / 197 (p5 177) |
| late_join 100v100 (client killed at 32 s, relaunched 15 s later, AFTER the rejoin fix) | Ali host, Omar client; Ali attacks (`2000-army-100-crash-client-rejoin2`) | 0.2 / 0.8 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 2 / 2 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 0 | fleeing 0 /  at the end | Ali's side lost 0 of 101, Omar's lost 0 of 101; puppet hits 737 @ 23.4 dmg vs - @ - dmg; troop HealthLimit Ali's 100 vs Omar's 100; the server re-sent the start ("asked to start the mission again"), Omar's mission opened at +116 s, then his spawn handler aborted it: "Local player origin missing from battle reserves" (open, A5); the battle ended at +188 s; because his mission had ended, the retreat rule let his party leave before the commit: 69 men kept (68 wounded), no capture | - | n/a | n/a | n/a | n/a | AttackerVictory on both at +188 s (skew - s); rosters agree on counts on all three | 0.0 / 113.7 | 232 / 127 | 8 med, 137 peak | 0 (max 0) | 9.9 / 34.7 | 0.0 / 9.2 | 18 / 71 | 22.5 / 27.0 / 8.5 | 4664 / 5265 / 959 | 196 (p5 179) / 175 (p5 159) |
| retreat 100v100 (host retreats at 45 s, before the fix) | Omar host, Ali client; Ali attacks (`1859-army-100-retreat-host`) | 0.1 / 5.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 4 / 4 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 1 | fleeing 0 /  at the end | Ali's side lost 2 of 101, Omar's lost 5 of 101; puppet hits 121 @ 21.6 dmg vs 122 @ 20.5 dmg; troop HealthLimit Ali's 100 vs Omar's 100; BUG: Omar retreated with 96 men standing; 1 s later the server concluded AttackerVictory for Ali, took Omar prisoner and his whole party (roster 0/0/0, Ali +98 prisoners) | ali->omar 26/135 (98% applied); omar->ali 27/152 (100% applied) | n/a | n/a | n/a | n/a | AttackerVictory on both at +54 s (skew - s); rosters agree on counts on all three | 108.9 / 80.5 | 161 / 163 | 8 med, 152 peak | 0 (max 0) | 39.7 / 30.0 | 9.3 / 5.3 | 83 / 54 | 29.7 / 26.4 / 8.5 | 4948 / 5125 / 946 | 186 (p5 165) / 191 (p5 167) |
| retreat 100v100 (client retreats at 45 s, before the fix) | Omar host, Ali client; Ali attacks (`1902-army-100-retreat-client`) | 0.2 / 0.2 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 4 / 4 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 0 | fleeing  / 0 at the end | Ali's side lost 0 of 101, Omar's lost 0 of 101; puppet hits 26 @ 23.4 dmg vs 22 @ 25.8 dmg; troop HealthLimit Ali's 100 vs Omar's 100; same BUG in the other role: Ali retreated with ~100 men, DefenderVictory 3 s later, Ali captured, party 0/0/0, Omar +100 prisoners | ali->omar 30/79 (100% applied); omar->ali 31/76 (96% applied) | n/a | n/a | n/a | n/a |  on both at +- s (skew - s); rosters agree on counts on all three | 81.5 / 127.3 | 214 / 188 | 8 med, 149 peak | 0 (max 0) | 27.3 / 42.4 | 5.4 / 9.6 | 99 / 42 | 25.3 / 28.7 / 8.5 | 4890 / 5109 / 942 | 192 (p5 167) / 189 (p5 165) |
| retreat 100v100 (host retreats at 45 s, AFTER the fix) | Omar host, Ali client; Ali attacks (`1953-army-100-retreat-host-fixed`) | 0.2 / 0.3 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 4 / 4 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 0 | fleeing 0 /  at the end | Ali's side lost 0 of 101, Omar's lost 0 of 101; puppet hits 21 @ 21.2 dmg vs 33 @ 20.2 dmg; troop HealthLimit Ali's 100 vs Omar's 100; Omar left the event before the conclusion; his roster 101 -> 100 men (2 wounded), prisoners unchanged on both parties, back on the map (encounter closed); Ali AttackerVictory with 1 casualty | ali->omar 36/95 (100% applied); omar->ali 32/82 (100% applied) | n/a | n/a | n/a | n/a | AttackerVictory on both at +54 s (skew - s); rosters agree on counts on all three | 164.5 / 55.4 | 193 / 184 | 8 med, 148 peak | 0 (max 0) | 47.8 / 26.0 | 10.3 / 4.0 | 120 / 72 | 28.5 / 24.3 / 8.4 | 4947 / 5500 / 941 | 181 (p5 152) / 185 (p5 152) |
| retreat 100v100 (client retreats at 45 s, AFTER the fix) | Omar host, Ali client; Ali attacks (`1957-army-100-retreat-client-fixed`) | 0.2 / 0.3 | 800 coop, engine agrees on both | def 101/101 atk 101/101 (fielded/target) | identity hash agrees 4 / 4 samples (misses are 1-2 deaths in flight) | peak agents 204 / 204 | per-side alive counts: max diff 0 / 0 | fleeing  / 0 at the end | Ali's side lost 0 of 101, Omar's lost 0 of 101; puppet hits 34 @ 20.1 dmg vs 22 @ 19.6 dmg; troop HealthLimit Ali's 100 vs Omar's 100; Ali left the event before the conclusion; roster identical before and after (102/1/168), Omar 100/2/127; nobody captured | ali->omar 31/107 (100% applied); omar->ali 29/187 (100% applied) | n/a | n/a | n/a | n/a |  on both at +- s (skew - s); rosters agree on counts on all three | 86.6 / 163.8 | 193 / 186 | 8 med, 148 peak | 0 (max 0) | 33.4 / 48.5 | 5.9 / 10.8 | 46 / 42 | 24.8 / 28.3 / 8.3 | 4921 / 4782 / 949 | 183 (p5 158) / 174 (p5 154) |
| surrender / leave | each x2 | pre-mission encounter-menu choices; the fixture enters the mission directly (`start_player_field_battle`), so these stay with `AsymmetricEncounterActionTests` (BR-072) until A6 scripts the menu | | | | | | | | | | | | | | | | | | | | | | | |
| siege | A besieges | A8 | | | | | | | | | | | | | | | | | | | | | | | |
| PvE regression (`Auto-AnimTest.ps1`, 5 Sep 19:48, module with the census diagnostics and the retreat fix) | Ali and Omar allied vs AI (Full-Setup default bandit battle) | - | - | - | position error between the machines, same agent same instant: mean 0.16 m, p50 0.10, p90 0.50, p99 1.34, max 3.23 (18,058 samples, 0.6 percent over 2 m) | - | - | - | - | - | n/a | n/a | n/a | n/a | melee wind-up mirrored 85.4 / 84.2 percent and release 90.0 / 90.8 percent (client / host view); ranged ready 90.7 / 90.9 percent; no moving mounts sampled | - | - | - | - | - | - | - | - | - | - |

Rows are generated by `python Scripts/Rig/army_row.py <runDir> "<label>" ["<note>"]` from the run's `metrics.json`,
so every number traces to a file in `Scripts/Rig/runs/`. Fairness column: losses per side over the fielded
count, then the melee hits each side landed on the other's puppets and the mean damage per hit (from the
`DAMAGE_BY_VICTIM` snapshot). Routed dmg column: from the blow trace (`blowtrace-*.txt`, runs from chain 3 on).

#### 8.1.1 What the A0 baseline says (5 Sep 2026, one machine: both clients and the server on Ali's PC, loopback mesh)

| Question | Answer from the runs above |
|---|---|
| Start and deployment | Enter and deployment skew 0.1-0.5 s in every run without a deliberate delay. The battle host is the first MISSION-READY client (BR-010), which is scene-load time, not entry order: with Omar's `start_attack_mission` delayed 25 s Omar was still elected (Ali's load took longer); Omar held the seat in 6 of 7 runs, Ali in the cap-400 run. |
| Sizing (A3) | The coop `battleSize` governs; both clients read the same allocation (`def 401/400 atk 401/400`, `def 801/200 atk 801/200`). With the engine slider at 800 and coop at 400 the `engine_size` command flags the mismatch on both clients and both sides were capped to 200 identically. No per-side effect: A3 killed for the loopback case. |
| Spawn mirror and fielding (A1 spawn, A2) | Per-side identity hashes agree at every 30 s sample where the counts agree; the count misses are 1-2 agents (once 11, during an 800 v 800 wave) between two samples taken about a second apart on the two machines, i.e. deaths or spawns in flight. Both sides were fielded exactly to target at 100, 400 and 800-cap, and reinforcement waves reached both sides until the 800-man rosters were spent (peak 402 / 402, casualties 696 and 800). A2 killed on one machine; the two-machine check stays in A7. |
| Mesh load (A1 load) | At 400 v 400 each peer sends 300-345 msgs/s median, 390-530 peak: MovementPacket 190-210 median (270-415 peak), AgentActionPacket 70-92, AgentEquipment 2-7, routed damage (`NetworkApplyBattleDamage`) 3-6 median and 15 peak, hit presentation / guard reaction / shield 1-3 each, deaths 1. Damage-related traffic is under 5 percent of the mesh; movement dominates exactly as in PvE, so "all-routed damage doubles the mesh rate" is killed. Wire 135-190 KB/s median but 300-470 KB/s at p99 (over the section 4 budget at peaks); receiver queue p99 36-79 ms (over the 50 ms budget in two of four 400-scale runs); the rate controller sawtooths bulkHz 10 to 30 as fps swings 60-95 with 802 agents on a machine that is also running the other client and the server. To be judged on two machines (A7) before any fix. |
| Server | `worldQueue` median 0, max 1 in every run. 43 msgs/s median and 225 peak at 400 v 400, almost all hero skill-XP upserts and `contributionToBattle` sets from `HitRewardHandler` (49/s each of two message types at peak). No server-side pressure. |
| Kill, wounded, rout mirror (A4) | Per-side alive counts identical on both machines within 1-3 at every 5 s sample; both clients resolved at the same second in all 7 runs (skew 0.0 s), same result, and the after-battle rosters agree on counts on both clients and the server (XP excluded by design, see PVP-SYNC-PLAN). Nobody routed in any run (fleeing 0 on both sides throughout even at annihilation), so the rout mirror is unmeasured; the asymmetric preset or a morale check is needed before A4 can be answered. |
| Fairness (A10) | Ali's side won the first 10 runs: as attacker and defender, at 100, 400 and 800-cap, as client and as host. Host/client role and attack direction were cleared by those runs alone. The census with troop health explained it: Ali's `imperial_veteran_infantryman` spawned with `HealthLimit` 123, Omar's with 110, identical on both machines (a puppet carries its owner's value), no formation captains (`cap=-`), same weapon mix. The extra health is the party leader's perks (vanilla `GetEffectiveMaxHealth` adds the leader's Medicine / Athletics perk bonuses); 12 percent more health per man is a 25 percent effective edge under the square law, which is the 400-to-332 loss ratio seen. With both heroes reset (`-EqualHeroes`, server `reset_skills` by game StringId) every troop spawns at 100 on both machines, and the results split: Omar won the 100 v 100 (Ali lost 101, Omar 85), Ali won the 400 v 400 (Ali lost 339, Omar 400); damage per puppet hit 21.1 vs 22.7 and 20.0 vs 20.0; hits landed track survivorship. Kills per fielded man averaged over the two equal-hero runs: Ali 0.93, Omar 0.93, well inside the 10 percent target. Omar also won one run WITH the health handicap. Verdict: no ownership advantage in the damage path; the fairness metric must always be read with equal heroes. Observation from the same census: an enemy PUPPET's `Health` is set at spawn and never mirrored afterwards (Omar's puppets read 110 / 110 on Ali's machine while dying); kills, deaths and results are the owner's and unaffected, and vanilla shows no enemy health readout, so this costs nothing to leave. |
| Routing funnel | Only blows with `InflictedDamage > 0` on a puppet are routed (`BattleBlowInterceptPatch`). The blow trace (`blowtrace-*.txt`, from `1821-army-400-t1`) shows that 46-47 percent of the melee hits scored on puppets carry ZERO damage on both sides (armour absorbed the hit, or a shield block that still registered), so they are never routed; of the damaging hits, 99 percent were applied by the owner (617 routed, 610 applied, 7 unmatched one way; 560 / 556 / 6 the other), the remainder being hits on a man the owner already has dead. The earlier estimate of 70 percent from the counters was wrong because it counted the zero-damage hits. Routed-blow latency on loopback, attacker's blow to the owner's apply: p50 33 ms, p90 53 ms, p99 88-91 ms, max 143-200 ms, i.e. two to six frames of processing (guard-window epoch, drain on tick, presentation deferral) with no wire delay; the wire adds one hop to that. Each side also registers 3,000-3,600 own-side contacts at zero damage per 400 v 400 battle, more than its puppet hits; a `Mission.RegisterBlow` cost, not a sync issue. |
| Orders (A12) | hold_then_charge: the holder's centroid moved 0.0 m during the 60 s hold on BOTH machines while the charger's moved 200 m; after the hold both sides' centroids moved identically on both machines. Orders do not leak: A12 killed. |
| Retreat (A11) | DEFECT FOUND AND FIXED, both roles. A player who retreats from the mission stays a member of the map event on the server; the other player's mission ends within 1-3 s because the retreater's troops are despawned (BR-051), its engine reports a victory, the completion handler concludes it, and the native conclusion (`BattleState` setter, `CaptureDefeatedPartyMembers`) treated the retreated party as DEFEATED: hero captured, every man handed to the winner as a prisoner, party destroyed (`1859-army-100-retreat-host`: Omar retreated at 47 s with 96 men, roster 0 / 0 / 0 on all three, Ali's prisoners 168 to 266; `1902-army-100-retreat-client`: the mirror). Fix (ledger 8.2): the reserve builder already forgets a retreater's parties (`ForgetController`) and rebuilds them on re-entry (BR-052); it now remembers the retreated mobile parties per battle, and the server's authoritative conclusion (`MapEventHandler.ApplyBattleStateChange`) lets each still-present retreated party leave through the existing authoritative leave (`PlayerLeaveBattleAttempted`, the encounter-menu path) before committing the winner's state; when that leave empties a side the event finalizes natively and the commit is not repeated. Verified live in both roles (`1953-army-100-retreat-host-fixed`, `1957-army-100-retreat-client-fixed`): the retreater keeps its men (100 of 101, 2 wounded; 102 of 102), no prisoners change hands, the retreater is back on the map with its encounter closed, the other player gets its victory. Guards: `BattleRetreatConclusionTests` (E2E: the leave is broadcast for the retreated party, its men stay, its hero is free; re-engaging clears the mark); the neighbouring finalize, abandonment, surrender, retreat-casualty, despawn and migration suites (75 tests) still pass. |
| Crash, host migration, late join (A8, A9) | Host killed mid-battle (`1906-army-100-crash-host`, `1930-army-100-crash-host-rejoin`): the remaining client became host 61 s and 16 s after the kill (the peer timeout); until then the dead player's troops stood on the survivor's machine taking no damage (routed blows to an absent owner are dropped); then they were adopted and fought on leaderless, losing 101 for 17-25; the battle resolved for the survivor and the dead player's party was destroyed. Migration works; continuity has a 16-61 s hole and the adopted side gets no orders. Rejoin after a crash: blocker 1 FOUND AND FIXED. `BattleMissionStartHandler` remembers who was sent a mission start per event (so a late joiner's request does not tear the others out); the relaunched player was still in that set and its own entry request was answered "already in this mission; not re-sending its start" (`1930-army-100-crash-host-rejoin`). The requester is now forgotten from the set before its request is served (only the requester; the others keep the guard). Guard: `BattleInstanceCreationTests.RequesterRecordedAsStarted_IsSentTheStartAgain_OtherParticipantIsNot`; live (`2000-army-100-crash-client-rejoin2`) the server logged "asked to start the mission again" and the returner's mission opened. Blocker 2 OPEN (A5): the returner's `CoopBattleMissionSpawnHandler` aborted two seconds later with "Local player origin missing from battle reserves (side=Defender, Def populated=true, Atk populated=true)": the party had been adopted by the host (absent, not withdrawn) and the reserve handed back on return did not carry the hero's own origin (BR-033 return path). Rig notes: the relaunched client now answers the troop-preference prompt (the barrier held its start 30 s) and readiness is `ENCOUNTER_STATE`; a 100 v 100 with adopted troops is over in about three minutes, so the rejoin test should use 400 v 400. |
| Roster replication (A5) | The fixture's 800-troop roster replace was still not visible on Ali's client 4 s later (it showed the save's old 377 / 59 roster) while the 400-troop replaces were; the after-battle rosters agreed on all three in every run. The rig now waits for both clients to report the server's member count before the `before` snapshot and records the wait as `rosterSyncSeconds`. |
| Costs noticed for A2 | One Information log line per routed blow (`[BattleSync] Applying routed blow`), 20-40 lines/s per client at 400 v 400. `deadReckoning` 3.3-4.4 ms/s, `damageAttribution` 0.4 ms/s (DEBUG only) at 400 v 400. |

The same table is produced again on Omar's laptop as the second client at A7, and kept as a second copy
of 8.1 with the mesh RTT column added.

### 8.2 Fix ledger (one row per fix, appended as it lands)

| Date | Milestone | What changed (files) | Scenario used | Metric targeted | Before | After | Delta | Added wire KB/s (h/c) | Added msgs/s (mesh / server) | Added CPU ms/s per path (`hotpath.txt`) | Process CPU % delta (h/c/s, spread) | Receiver apply / queue delta | fps delta (h/c, spread) | PvE regression (M1 / M4 / 1,000-agent load) | Guard test | Seen in game (date, both players) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| | | | | | | | | | | | | | | | | |
| 2026-09-05 | A6 / A11 (found during A0) | `source/GameInterface/Services/MapEvents/TroopSupply/BattleTroopReserveBuilder.cs` (remembers retreated mobile parties per battle, cleared on re-engage and at battle end; `GetRetreatedMobilePartyIds`), `source/GameInterface/Services/MapEvents/Handlers/MapEventHandler.cs` (`LeaveRetreatedParties` before the authoritative commit; no second commit when the leave finalizes the event natively) | retreat 100v100, host and client (`-Scenario leave -LeaveMode retreat`) | end state after a mid-battle retreat | retreater's party 0 / 0 / 0 on all three, hero captured, winner +98 / +100 prisoners | retreater keeps its men (100 of 101; 102 of 102), no prisoner change, back on the map; winner's victory with the real casualties (1 / 3) | a retreat is no longer a total defeat | 0 / 0 (one `NetworkPartyLeftBattle` per retreated party at the end) | 0 / 0 | none (conclusion path only) | 0 | 0 | 0 | not affected (PvE run 19:48 green: wind-up 85 percent, position p50 0.10 m) | `BattleRetreatConclusionTests` (2), plus 75 neighbouring E2E tests green | one machine, both roles, 5 Sep 19:53 and 19:57; two-machine check at A7 |
| 2026-09-05 | A5 / A8 (found during A0) | `source/GameInterface/Services/MapEvents/Handlers/BattleMissionStartHandler.cs` (`ForgetStartedController`: the requester of a mission start is dropped from the already-started set before its request is served) | late_join 100v100 (`-Scenario leave -LeaveMode crash -RejoinAfterSeconds 15`) | a relaunched player receives its mission start | "already in this mission; not re-sending its start", no mission opened | start re-sent, mission opened on the returner (then aborted by the open reserve-origin blocker) | the relaunched player gets back into the mission-start path | 0 (one start message to the requester) | 0 | none | 0 | 0 | 0 | n/a | `BattleInstanceCreationTests.RequesterRecordedAsStarted_IsSentTheStartAgain_OtherParticipantIsNot` (26 tests in the join / start / late-join suites green) | one machine, 5 Sep 20:05; full rejoin blocked by the A5 reserve-origin issue |
| 2026-09-05 | A2 diagnostics (found during A0) | `source/Missions/Battles/BattleDamageRouter.cs` (unkillable-troop tally keyed by agent identity as well as index, reset when a reused index gets a new agent) | 800v800 capped at 400 (reinforcement waves) | false `unkillable-troop signature` warnings | 195 / 259 warnings per client in a wave battle, 0-1 without waves | tally starts fresh when the engine reuses an index (unit-verified; next wave run confirms live) | no misleading warnings in players' logs | 0 | 0 | none | 0 | 0 | 0 | n/a | `UnkillableTroopDiagnosticTests.AReusedIndexStartsAFreshTallyForTheNextAgent` (7 in the class green) | pending the next 800-cap run |

### 8.3 Release summary (filled at A9)

| Metric | Before the plan | After | Cost on one machine (wire, msgs/s, CPU ms/s, CPU %, fps) | Cost on Omar's laptop |
|---|---|---|---|---|
| Spawn mirror | | | | |
| Kill / rout mirror | | | | |
| Fairness | | | | |
| Routed damage latency p99 | | | | |
| Wave arrival skew | | | | |
| Late join / migration continuity | | | | |
| End-state roster agreement | | | | |
| Wire KB/s at 400 v 400 (h/c) | | | | |
| Mesh messages/s at 400 v 400 (h/c) | | | | |
| Server messages/s and `worldQueue` peak | | | | |
| Sender ms/s and receiver apply ms/s at 400 v 400 (h/c) | | | | |
| Process CPU % at 400 v 400 (h/c/s) | | | | |
| fps median / p99 at 400 v 400 (h/c) | | | | |
| PvE 1,000-agent run: wire, receiver apply, fps | | | | |

---

## 9. Order of work and rough effort

| Step | Effort | Output |
|---|---|---|
| Army fixture, presets, `roster_hash` | 1 day | Both clients enter a chosen army battle with no clicks |
| `Army-Test.ps1`, census recording, `analyze_army.py` | 2 days | Section 8.1 baseline, A1-A12 answered |
| A1 start, sizing, spawn mirror | 1-2 days | |
| A2 fighting at scale and load | 2-4 days, the message-count work is the unknown | |
| A3 reinforcements, lords, nearby joiners | 2-3 days | |
| A4 commanding | 0.5-1 day | |
| A5 late join, rejoin, host migration | 2-3 days | |
| A6 end of battle including M12 | 2-3 days | |
| A7 two-machine and robustness | 1-2 days plus scheduling with Omar | |
| A8 siege between players | 3-5 days | |
| A9 hardening, package, full play test | 1 day | Release zip, section 8 complete |

Roughly four to five weeks of sessions after the duel plan finishes. A0 cannot be shortened. Any
milestone whose hypotheses the baseline kills is closed with its numbers and nothing changed.
