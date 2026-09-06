# PvP combat sync plan

Goal: two human players fighting each other in a coop field battle see the same fight. A swing, thrust,
kick, bash, block, parry, hit, stagger, dismount and death rendered on the attacker's screen must be
rendered on the victim's screen, in the same direction, at the same moment, with the same outcome.
"Production grade" here means: every claim measured on a shared clock, a BEFORE and AFTER number for
every fix including its network and CPU cost, every fix guarded by a test, and every number reproduced
on the real two-machine setup (Ali's PC and Omar's laptop) before it is called done.

Design principle: **player-to-player sync is held to a higher standard than unit-to-unit sync, and may
spend more to get there.** There are two players and a thousand units. A player agent may be sent more
often, ranked first, and quantised finer than a soldier, within the explicit budget in section 3. What
is not allowed is spending that budget without measuring it.

Companion documents: `COMBAT-BUGS-MILESTONES.md` (root causes and wrong turns, M1-M12) and
`COMBAT-REMAINING-WORK.md` (what the PvE fixes cost, and the measurement method reused here).

---

## 0. Rules carried over from the combat milestones

These are not preferences. Each one is written down because ignoring it cost real time.

1. **Confirm it is real before fixing it.** Instrument first, read the numbers, then decide. M4 was
   "fixed" twice for the wrong cause before position error was measured (`COMBAT-BUGS-MILESTONES.md`,
   M4 and M4a). Four guard-side fixes for M1 failed before `guardCommandEffect` proved the guard was
   never involved.
2. **Only call it fixed when it has been seen in game.** Unit tests guard against regression; they do
   not prove the fix. M5 is still "never reproduced live" for exactly this reason.
3. **Before and after, or it did not happen.** Every fix is preceded by a baseline run and followed by
   an identical run, same script, same seed, same rig, three runs each, and the delta table goes into
   section 10 of this file. A fix without its before/after row is not merged.
4. **Add all instrumentation in one go**, one build, one run. Never one counter per cycle.
5. **Never debug a two-agent problem inside a thousand-agent battle with a human in the loop.** That
   mistake cost 30-40 manual rounds on M1. PvP is a two-agent problem by definition: build the duel rig
   first (section 4) and do everything through it.
6. **Two clients on one machine: windowed, side by side, matched by PID.** Stacked windows trap the
   mouse (M0b). The scripts already do this; keep it.
7. **A Missions patch needs both the attribute and the category registration** in
   `MissionModule.CreatePatchCategoryRegistrations()`, or it silently never installs.
   `MissionsPatchInstallationTests` fails the build otherwise; keep that test green.
8. **A replicated melee action must be applied with `ignorePriority: true`**
   (`AgentActionData.ShouldIgnorePriority`). `SetActionChannel` reports success even when the engine
   discards the write on the next frame, so delivery counters cannot see this class of bug. Only the
   timeline diff can.
9. **Dead-reckoning clamps are load-bearing.** Do not touch `AgentPositionInterpolator` without running
   `PuppetDeadReckoningTests` and the position-error report; the unclamped version wrecked the tail.
10. **Lag is message count, not bytes.** Any new per-hit or per-tick message is judged against
    `worldQueue` and the mesh profiler, not against KB/s.
11. **No wire-format growth for units without a measured need.** Every PvE fix so far added 0 bytes.
    The player lane (section 3) is the one deliberate exception, and it has a budget. Diagnostics stay
    behind `#if DEBUG`; confirm with a Release build before packaging.
12. **Whole-process CPU and network A/B runs are directional only.** Run-to-run variance in a battle
    (31-36% on wire bytes for a change that touched no wire format) swamps small effects, so per-path
    timing (`HotPathCostDiagnostics`) is the number that counts, and whole-process figures are reported
    with their variance. This is the method `COMBAT-REMAINING-WORK.md` section 1b already uses.
13. **Never restart a live rig unasked**, and never `rm -rf` / `git worktree remove` anything that can
    hold a junction to the game (memory `coop-never-recursive-delete-through-junctions`).
14. **No subagents; think it through here.**

---

## 1. Scope

**In scope** - a coop field battle where the two players are on opposite sides and fight each other:

| Interaction | What "synced" means |
|---|---|
| Melee swings (left, right, overhead), one- and two-handed | Puppet shows the wind-up and release in the same direction, starting within one network hop of the owner |
| Thrusts, including spear and polearm in one- and two-handed usage modes | Same as swings; the usage mode switch is rendered before the thrust that uses it |
| Kick, shield bash | Rendered on the puppet at all (today unmeasured, hypothesis H1) |
| Blocks and parries, all four directions, shield and weapon | The attacker's client sees the guard the victim is actually holding, early enough that a swing into it bounces on both screens |
| Chamber blocks | Same as parries; rare, measured but not a gate |
| Hit reactions: flinch, stagger, knockback, knockdown | Visible on both screens; the victim's health drops within one hop of the attacker's swing landing |
| Hit and block sounds | Right sound, both screens (M3 established the chain works) |
| Mounted: lance couch, horseback swings, horse charge, dismount by blow | Rendered and damaging on both screens; horse legs animate (M8 is open) |
| Ranged and thrown at a player | Projectile and hit visible on both screens, damage routed |
| Death, kill credit, scoreboard, post-battle result | Same winner, same killer, same numbers on both machines and on the server |

**Out of scope** - PvP inside locations (tavern, town centre, arena). `LocationPvpBlockPatch` suppresses
it by design because locations have no damage routing. Realistic Battle Mod compatibility. Fixing M6
(agent ownership imbalance), which is a PvE load question.

**Why PvP is harder than the PvE work so far.** In PvE the fighting agents on one machine are mostly
local AI hitting puppets; the human is rarely the victim of a puppet. In PvP each player's agent is a
puppet on the other machine at the same time, so every problem exists in both directions at once, the
victim is always a human who can see it, and block-versus-swing timing is a two-way race decided by the
mesh round trip. The M1 numbers (85% wind-up rendered, 9-12% still lost to the defend pose) are fine
for a crowd and not fine for a duel. Hence the higher targets in section 4.5 and the player lane.

---

## 2. What exists today (the parts the plan builds on)

| Component | Role | State |
|---|---|---|
| `Missions/Agents/Packets/AgentActionData.cs` | Replicates the two action channels per agent: index, progress, guard mode derived from the defend direction; `ShouldIgnorePriority` forces `ReadyMelee`/`ReleaseMelee` through the engine's priority arbitration | M1 fixed; only those two action types are forced |
| `AgentActionHandler` | Event-driven: an action CHANGE is sent `ReliableOrdered`, up to 8 agents per packet, polled per frame | Live; not rate-limited, so already "as fast as the frame" |
| `AgentEquipmentData.cs` | Wielded items and `MainHandUsageIndex` (spear one/two-handed, couch) | Replicated; ordering against the action that uses it unmeasured |
| `AgentMovementHandler`, `MovementBatchSender`, `MovementQuantizer` | Position, direction, speed on the wire; 31 bytes moving, 18 stationary | M4 fixed (mean 0.28 m, max 2.48 m) |
| `MovementPriorityScheduler` | Ranks each recipient's snapshots by **local-main-agent tier**, distance to the recipient's focus, and time since successful delivery (`MaximumPriorityAgingSeconds = 0.225`) | A player-first tier already exists; it is not yet the guaranteed high-rate lane section 3 describes |
| `MovementRateController`, `MovementNetworkSettings`, `MovementTrafficBudget` | `bulkHz` (20 typical, 10-60 observed) and `priorityHz`, an adaptive ladder tied to frame rate with a 40 Hz floor, per-peer traffic budget | Live; `coop.debug.movement.state` reports every knob |
| `AgentPositionInterpolator` | Dead reckoning with clamps (one update period, 1 m) | M4; M8 horse sliding open |
| `GameInterface/.../BattleBlowInterceptPatch.cs`, `Missions/Agents/Patches/AgentDamagePatch.cs` | A blow on a puppet is suppressed on the attacker's client and routed to the victim's owner, which applies it under `AllowedThread` | Live; `PlaySuppressedHitSound` gives the attacker immediate audio |
| `GuardReactionHandler`, `NetworkAgentGuardReaction`, `LocalAgentGuardedHit`, `GuardedHitWindow`, `MeleeReactionPolicy` | Block and parry reactions and the rule that a node never rewrites its own attacker's collision reaction | Live; guard state age at collision time unmeasured |
| `CombatHitPresentationHandler`, `NetworkMeleeHitPresentation` | Hit presentation and the real block sound (M3) | Live |
| `ReplicatedDeathAgentStatePatch`, `ReplicatedDeathKillFeedPatch`, `HitRewardHandler`, `DamageAttributionPatch` | Death mirror, kill feed, scoreboard, kill credit | Live; `DamageAttributionPatch` is `#if DEBUG` |
| `Missions/Diagnostics/*` (12 classes: `AnimationTimeline`, `WindupDiagnostics`, `GuardSyncDiagnostics`, `HitTimingDiagnostics`, `MeleeSwingDiagnostics`, `ActionApplyTrace`, `ActionSendLog`, `ActionDeliveryDiagnostics`, `GuardCommandEffectDiagnostics`, `DamageAttributionDiagnostics`, `ShieldImpactDiagnostics`, `HotPathCostDiagnostics`) | The measurement layer; `coop.debug.battle.windup` arms them all, `coop.debug.battle.animation_timeline` records per-tick action state for tracked agents | DEBUG-only; `ActionApplyTrace` classifies by a global index map that is wrong across weapons (documented trap) |
| `coop.debug.movement.state`, mesh packet profiler (`profileOnClient: true`), server `Packet profile` / `worldQueue` | The network and CPU measurement layer: `wireBytesPerSecond`, `bulkHz`, `priorityHz`, `senderMsPerSecond`, `receiverApplyMsPerSecond`, `receiverQueueMs`, `fps`, message counts per type, DIRECT versus RELAY per controller | Live |
| `Auto-AnimTest.ps1`, `analyze_timeline.py`, `Full-Setup.ps1` | The unattended PvE harness: launch rig windowed side by side, start a battle, charge, record both clients, print owner-versus-puppet rendering and position error | **Live only in the session scratchpad, not in the repository.** First deliverable is to bring them in |
| `coop.debug.map_event.start_player_field_battle <attacker> <defender>` | Server-side fixture that starts a field battle between two player parties; requires different factions and records `WasAtWar` for `restore_player_field_battle` | Exists; never used for a 1v1 duel |
| `coop.debug.mobile_party.set_troops`, `coop.debug.battle.troop_preference`, `start_attack_mission`, `finish_deployment`, `charge_owned_formations`, `state` | The click-free path into a battle | Exist |
| E2E mock-engine tests: `BattleBlockingSyncTests`, `BattleDamageMirrorTests`, `BattleDeathMirrorTests`, `BattleHeroDamageSyncTests`, `AgentActionPacketSerializationTests`, `ReplicatedSwingPriorityTests`, `PuppetDeadReckoningTests` | Regression guards for the routing and priority logic | Exist; nothing drives a scripted attack sequence |

Nothing drives the **player's own agent** by script today. The PvE harness lets the AI fight; a duel
needs the two player agents to walk up to each other and perform a known sequence of attacks and
blocks. That driver (section 4.2) is the one genuinely new piece of tooling.

---

## 3. The player lane

Units are replicated for a crowd: 20 Hz bulk movement, quantised to 31 bytes, priority by distance to
whoever is looking. A player fighting another player needs the opposite trade: rate and precision for
two agents, paid for out of a budget that is a rounding error against the crowd.

### 3.1 What the lane changes

| Aspect | Units today | Players (and their mounts) in the lane |
|---|---|---|
| Movement send rate | `bulkHz`, adaptive 10-60, typically 20 | Fixed 60 Hz to the opposing player, independent of the adaptive ladder and of frame-rate throttling; never dropped by the traffic budget |
| Priority | Local-main-agent tier already ranks first per recipient | Player agents are pinned to the top of every recipient's queue and exempt from `MaximumPriorityAgingSeconds`; the opposing player's agent is always inside the recipient's focus |
| Quantisation | 16-bit direction fraction, quantised speed, positions in decimetres | Full-precision position and direction for the two player agents (about 57 bytes instead of 31); everyone else unchanged |
| Action packets | Sent on change, `ReliableOrdered`, batched up to 8 agents per packet per frame | Player action changes flushed in their own packet the same frame, ahead of the unit batch; guard/defend direction included on every change, not only on transitions |
| Guard state | Sent on transition | For players, re-asserted at the movement rate while a guard is held, so a lost or late packet cannot leave a stale guard on the attacker's screen for longer than one update |
| Mount | Follows the rider's frame | Player mounts ride in the lane with their rider |

### 3.2 Budget

Two players at 60 Hz with 57-byte payloads is about 6.8 KB/s each way plus framing; the busiest PvE peer
measured 644 KB/s. Message count rises by at most 120 messages per second on the **mesh** (direct
client-to-client), which never touches the server pipe where `worldQueue` overloads were observed. The
hard limits for the lane, measured in P1 and re-checked in every later milestone:

| Limit | Value |
|---|---|
| Added wire, per peer, duel | <= 15 KB/s |
| Added messages, per peer | <= 120/s on the mesh, 0 on the server pipe |
| Added CPU, per peer | <= 0.5 ms per second of wall clock (the M4 dead reckoning cost 1.2 ms/s and was accepted) |
| Effect on a 1,000-agent battle | `Auto-AnimTest.ps1` PvE run: position error, wind-up and release within noise of the pre-lane numbers; `wireBytesPerSecond` within variance |
| Omar's laptop | fps in the duel not below the pre-lane run by more than 1 fps median |

If any limit is exceeded the lane setting is reduced, not the measurement.

**P1 outcome (2026-09-05):** most of this table was already true. Units and players are both sent from the same
poll, but the local player is tier 0 in every recipient's queue and has its own priority poll with a 40 Hz floor
(`priorityHz = max(40, bulkHz)`), and positions already travel at 1 cm in 63 bits. The one knob left, the floor,
was raised to 60 Hz and measured with the bulk rate forced to 20 Hz: no change in onset, wind-up or position
error, +1.2 ms/s sender time. It stays at 40 (`MovementRateController.PlayerLaneHz`), with the measurement in
section 10.2; the guard re-assertion row moves to P4, where the block data decides it.

### 3.3 Where it is implemented

`MovementPriorityScheduler` (pin the tier), `MovementRateController` and `MovementNetworkSettings` (a
player rate that bypasses the ladder), `MovementQuantizer` / `MovementBatchSender` (a full-precision
record type for lane agents, selected per agent, so the unit path does not change), `AgentActionHandler`
(separate flush for local main agent), `AgentActionData` (guard re-assertion). Each of these already has
unit tests around the numbers it produces (`AgentDataWireSizeTests`, `MovementRateControllerTests`); the
lane adds cases rather than altering the unit expectations.

---

## 4. P0 - the duel rig

Everything else in this plan is measured through this rig, so it comes first and is not optional.

### 4.1 Bring the harness into the repository

Move from the session scratchpad into `Scripts/Rig/` and commit them:

- `Full-Setup.ps1` (rig launch: server + two clients, `display_mode = 0`, windows at x=0 and x=1280 by PID)
- `Auto-AnimTest.ps1` (PvE animation run, kept as the regression run for M1/M4 and for the lane's crowd budget)
- `analyze_timeline.py`
- new `Duel-Test.ps1`, `analyze_duel.py` and `Capture-Load.ps1` (below)

A harness that lives in a temp folder is not production tooling; it was one crash away from being lost.

### 4.2 Scripted player-agent driver (new, DEBUG only)

A `MissionBehavior` installed on both clients in debug builds, `DuelDriver`, commanded over the live-test
channel:

| Command | Effect |
|---|---|
| `coop.debug.duel.face <controllerId>` | Turn the local player agent toward the other player's agent and walk to 1.5 m |
| `coop.debug.duel.script <name> [seed]` | Run a named input sequence on the local player agent |
| `coop.debug.duel.state` | Position, facing, distance to opponent, current action, health, script step |
| `coop.debug.duel.stop` | Release all input |

The driver sets `Agent.MovementFlags` and the attack/defend direction flags exactly as mouse input does,
frame by frame, so the owner's engine produces real attacks with real collisions. It must not call
`SetActionChannel` on the owner: that would test a path a human never takes.

Named scripts (each is a fixed timeline of flag changes, so two machines can be compared step for step):

| Script | Content |
|---|---|
| `swings` | attack left, right, overhead, thrust; hold 700 ms each; 500 ms gap |
| `thrusts` | thrust x5 with a one-handed spear, switch usage mode, thrust x5 two-handed |
| `blocks` | hold block up/down/left/right 1 s each, then parry timing at 100 ms before impact |
| `kick_bash` | kick x3, shield bash x3 |
| `mixed` | seeded random sequence of the above, 60 s, same seed on both machines |
| `mounted` | couch, canter past, three horseback swings; needs the mounted fixture |

Two runs per script: A attacks while B blocks the scripted directions, then swapped. That covers both
directions of ownership in one cycle.

### 4.3 Duel fixture

- A dedicated save `pvp_duel` built once by a server command
  `coop.debug.map_event.pvp_duel_fixture <controllerA> <controllerB> [weaponSet]`: two player parties,
  no troops (`set_troops ... 0`), heroes equipped from a named set (sword+shield, two-handed, spear+shield,
  two-handed polearm, lance+horse), placed 50 m apart, factions split and at war, both players'
  `troop_preference all` pre-answered. It wraps `start_player_field_battle` and records what it changed
  so `restore_player_field_battle` undoes it.
- The Qin save cannot be used directly: both players are in kingdom `Created_14`, and the fixture requires
  different factions.
- A second fixture variant, `pvp_duel_in_crowd`, puts the same two players on opposite sides of a
  400-a-side AI battle. It is used only for the load measurements in section 5, where the question is
  what the lane costs when there is a crowd to compete with.

### 4.4 Recording

Extend `AnimationTimeline` with a **duel mode** that tracks exactly the two player agents (and their
mounts) on both machines from mission start, bypassing the PvE arming rule (which waits for eight
swinging agents per side and would never arm in a 1v1). Per tick, per agent, add to the existing
sample: attack direction, defend direction / guard mode, main-hand usage index, health, hit-reaction
action type. The buffer is two agents, so the 1 MB live-test cap is not a concern.

Add one event log alongside the timeline, `DuelEvents`, written on both machines with shared-clock
timestamps: attack started (owner), attack landed / bounced (attacker's collision result), blow routed
(sent), blow applied (victim owner), guard changed (owner), health changed, reaction played, death.

Shared clock: both clients on one machine share it already. For the real two-machine run, stamp events
with server time from the existing session clock and record the measured offset in the report; report
latencies as ranges when the offset uncertainty is above one frame.

### 4.5 Analyzer and the numbers it prints

`analyze_duel.py <hostTimeline> <clientTimeline> <hostEvents> <clientEvents> [--baseline <dir>]` prints,
per script and per direction, and with `--baseline` prints the delta against a saved run:

| Metric | Definition | Production target (players) | For comparison: units today |
|---|---|---|---|
| Attack onset latency | owner attack-start to first puppet tick showing `ReadyMelee` for the same attack | median <= one mesh hop + 1 frame; p99 <= 2 hops | unmeasured |
| Wind-up rendered | puppet ticks in `ReadyMelee` while the owner is in `ReadyMelee`, same attack | >= 98% | 85% (M1) |
| Release rendered | same for `ReleaseMelee` | >= 98% | 91% (M1) |
| Direction match | puppet attack direction equals owner's for every attack | 100% | unmeasured |
| Usage-mode match | puppet usage index equals owner's on every tick of a thrust | 100% | unmeasured |
| Kick / bash rendered | as wind-up, for `Kick` and `WeaponBash` | >= 98% | unmeasured |
| Guard age at impact | when the attacker's collision resolves against the puppet, how old the puppet's guard state is | <= one hop, and <= 17 ms on one machine | unmeasured |
| Hit-through-block | attacks the attacker's client scored as a hit while the victim's own machine was already blocking that direction at impact time minus one hop | 0 when the block began >= 1 hop before impact; every remaining case listed | unmeasured |
| Block rendered on attacker's screen | puppet shows the guard the victim holds, per direction | >= 98% | unmeasured |
| Damage latency | attacker's collision to victim's health change on the victim's machine, and to the attacker's screen | <= one hop; <= two hops | suspected one round trip |
| Reaction rendered | victim flinch/stagger visible on the attacker's screen | >= 98% | unmeasured |
| Position error at impact | distance between the two machines' positions of the victim at the collision instant | p90 <= 0.25 m, max <= 0.5 m | battle-wide p90 0.72 m, max 2.48 m (M4) |
| Sound | block and hit sounds played on both machines per event | 100%, right event id | M3 |
| Mount sliding | as M8 metric, duel population | not worse than the local horse | 3x worse (M8) |
| Outcome mirror | health, death, killer id, scoreboard identical on both machines and the server | exact | M5 clamp, never seen live |

The first full run of the rig produces the **baseline table** (section 10). Nothing in sections 6-9
starts until that table exists, because it decides which milestones are real.

### 4.6 What building the rig taught (P0, 2026-09-05, runs 1-9)

Nine runs were needed before the first clean 1v1. Each stop is recorded because the same trap will bite the
army plan and any future fixture.

| Run | Symptom | Cause | Fix (all in the rig, none in shipped code) |
|---|---|---|---|
| 1-2 | owner stood still, its puppet on the other machine performed the scripted blocks | driver wrote `MovementFlags` in `OnMissionTick`, after the native tick had consumed input; replication read them, the engine never did | driver moved to `OnPreMissionTick` |
| 3 | still frozen; probe read `ctrl=enabled` one tick after the driver disabled the vanilla controller | something re-enables `MissionMainAgentController.IsDisabled` every frame, and `Mission.OnPreTick` iterates behaviours last-added-first, so the controller ran after the driver and overwrote its flags with real (empty) input | driver re-asserts `IsDisabled = true` on every driven tick; 1 Hz engine probe (`probe` events) kept |
| 3 | 955 agents on a "duel" field | Ali led an army; every attached lord joined | `army.destroy <id> ObjectiveFinished` before the battle |
| 3 | distance stuck at 305 m; opponent id differed between machines | opponent resolved as "first hero this controller owns" (a lord) | resolve via `IPlayerManager` -> `Player.HeroId` -> agent registry |
| 4 | `mount=1`, scripted swings became `MountStrike` | both heroes carry a warhorse in `BattleEquipment` | `coop.debug.duel.unhorse` on the owning client before it enters; the spawn record carries the result |
| 5 | a lord with 47 troops joined Omar's side | `leave_settlement` drops Ali on Ormanfard's gate where a lord at war with Qin camped; vanilla pulls every nearby co-belligerent into a new map event | see run 6 |
| 6 | 19 lord parties joined; fixture cannot be restored while its event lives; destroying parties does not detach them | Omar's own spot sat inside an army stack | `coop.debug.mobile_party.find_clear_spot` (server; rings of 1.5 units, land only, no party within 6) + `restore_position` for both players; any extra party now aborts the run |
| 7 | swings script produced 4 attacks of 8, direction 0%, zero blows in 10 exchanges | the heroes' first weapons are a lance and a spear (thrust-only: left/right flags are ignored); shield block stops every direction; `Agent.AttackDirection` is -1 on puppets | `coop.debug.duel.arm <sword> <shield>` (same weapons both sides); defender stance per script (`hold` for swings/thrusts/kick_bash, `blocks` for mixed); direction compared by action index |
| 7 | blocked swings invisible to the analyser | blocked/parried collisions never reach `Mission.RegisterBlow` | DEBUG record from `MeleeHitCallback` (`DuelEvents.RecordBlocked`) |
| 7 | kicks never happened | vanilla raises `Kick` only when `Agent.KickClear()`; the driver raised it blind | clearance recorded per kick step (`event` lines); scripts keep the pair within reach |
| 9 | a bare defender died in two exchanges | 142 of 167 hit points gone in one `swings` exchange; `Mission.MainAgent` turns null on death | `coop.debug.duel.heal 2000` on both machines for both duellists before every exchange |
| 14 | the player spawned mounted, galloped through the boundary and was retreated; a companion joined the fight | `Hero.MainHero` on the client read as the companion for a moment after the fixture started, so `unhorse` stripped the wrong hero; a companion hero in the party spawns as AI once deployment ends early | equipment steps run before the battle and resolve the hero through the player registry (`IControllerIdProvider` -> `IPlayerManager` -> `Player.HeroId`), with the hero name checked; companions are wounded to 1 hit point (`hero.set_hitpoints`); the run aborts unless exactly two agents stand on the field |

Two engine facts worth keeping: the player's own hero has `AIStateFlag.Paused` set after a coop deployment
(harmless for a `Player`-controlled agent, it moves and swings), and `Mission.MainAgent` becomes null the moment
the hero dies, so a fixture that lets AI troops on the field loses its subject within a minute.

---

## 5. Before/after protocol, including network and CPU

This is how every row in section 10 is produced. It is the same for the lane, for every fix, and for the
final release build.

### 5.1 One measurement cycle

1. Build the candidate. Record commit or working-tree hash in the run folder name.
2. `Full-Setup.ps1` launches server and both clients windowed side by side; `Capture-Load.ps1` starts
   sampling (5.2) on all three processes before the mission begins.
3. `Duel-Test.ps1` runs the six scripts in both directions on the `pvp_duel` fixture, then the `mixed`
   script on `pvp_duel_in_crowd`, then `Auto-AnimTest.ps1` once (the PvE regression and crowd budget).
4. `analyze_duel.py` and `analyze_timeline.py` write `report.md` for the run; with `--baseline` they add
   the delta columns.
5. Three cycles per candidate. Report median and the spread; a delta smaller than the spread is "no
   change", not a win.

BEFORE is the cycle run on the build without the fix; AFTER is the same cycle on the build with it and
nothing else changed. The two run folders are kept side by side under `Scripts/Rig/runs/<date>-<label>/`.

### 5.2 What `Capture-Load.ps1` records, per machine, every second

| Source | Fields | Why |
|---|---|---|
| `coop.debug.movement.state` over the live-test channel | `bulkHz`, `priorityHz`, `wireBytesPerSecond`, `senderMsPerSecond`, `receiverApplyMsPerSecond`, `receiverQueueMs`, `localAgents`, `agents`, `fps` | The mod's own view of its network and CPU cost; the same fields `COMBAT-REMAINING-WORK.md` section 1c used |
| Mesh packet profiler (`profileOnClient: true`), `[Mesh] Packet profile` lines in `Coop_client.log` | message count and bytes per message type, DIRECT vs RELAY per controller | Message COUNT is what causes lag (rule 10); this is the only place battle traffic is visible |
| Server `Packet profile` lines | `worldQueue`, top message types by count | Proves the lane adds nothing to the server pipe |
| `HotPathCostDiagnostics` snapshot at the end of the run | per new code path: calls/s, us per call, ms per second | The reliable CPU number (rule 12) |
| `Get-Process` sampled at 1 Hz for the three PIDs | process CPU %, working set, thread count | Whole-process view, reported with variance |
| Frame time from the timeline sample cadence and `fps` | median, p99 frame time during scripts | Omar's laptop is the constraint that matters |
| Ping / RTT to the peer (mesh) | median, p99 | Converts "one hop" into milliseconds for the latency targets |

### 5.3 How it is reported

Every fix gets one row in the ledger (section 10.2) with these columns: what changed, the metric it was
meant to move, BEFORE, AFTER, delta, added wire bytes/s, added messages/s (mesh and server), added CPU
ms/s from `HotPathCostDiagnostics`, whole-process CPU % and fps deltas with their spread, the guard test
that covers it, and the date it was seen working on both screens. The PvE regression numbers (M1, M4)
are appended to the same row so a PvP fix that costs the crowd is visible immediately.

Two machines: the same cycle runs with Omar's laptop as the client at P1, P4, P5, P8 and P9. Those rows
carry the RTT and are the ones that decide whether a target is met.

---

## 6. Hypotheses the baseline must confirm or kill

Written down now so they are tested, not assumed.

| # | Hypothesis | Evidence for it | How the rig settles it |
|---|---|---|---|
| H1 | Kicks, shield bashes and other non-`ReadyMelee`/`ReleaseMelee` attack actions are invisible on puppets | `ShouldIgnorePriority` only forces those two types; ranged wins arbitration on its own (M2), melee lost it (M1); `Kick` and `WeaponBash` have not been measured | `kick_bash` script: rendered % per type |
| H2 | Thrusts render like swings | Thrusts are `ReadyMelee`/`ReleaseMelee` with a different action index; M9 found lancers fine | `thrusts` script: rendered % and direction match per usage mode |
| H3 | The usage-mode switch (spear one/two-handed, couch) can arrive after the action that needs it, so the puppet plays the wrong animation set for the first attack | Equipment and actions travel as separate packets; ordering never measured | usage-mode match per tick; count mismatched first ticks |
| H4 | Hit-through-block is real and proportional to round trip | Attacker resolves collision against the puppet's guard; guard age never measured; players report "I blocked and still got hit" | guard age at impact, hit-through-block count, on one machine (near-zero RTT) and on Omar's laptop |
| H5 | The victim's health drop is a round trip late and the flinch is missing or late on the attacker's screen | Damage feedback round trip "suspected, never measured" in `COMBAT-REMAINING-WORK.md`; only the sound is played immediately | damage latency and reaction rendered |
| H6 | The remaining 15% of lost wind-up is the defend pose (9-12%) and block recoil (4-5%) winning the channel | Measured in M1 follow-up | `mixed` script: what the puppet shows during a missed wind-up |
| H7 | Mounted duels expose M8 sliding and lance couch not rendering | M8 open; couch not measured | `mounted` script |
| H8 | Kill credit and death are mirrored but the scoreboard can differ (M5 class of bug) | M5 fixed by clamp, never seen live | outcome mirror after each duel |
| H9 | Position error at the instant of impact is larger than the battle average | Dead reckoning is tuned on running crowds; duel footwork is stop-start, which is where the clamps bind | position error at impact |
| H10 | The 20 Hz bulk rate and priority aging, not the animation path, are the dominant source of onset latency and position error between players | The scheduler already has a local-player tier but it is aged and rate-limited like everything else | P1: the lane alone, with no other change, measured before/after |

### 6.1 Baseline verdicts (run 10, one machine; Omar's laptop still to come)

| # | Verdict | Evidence (section 10.1) | Consequence |
|---|---|---|---|
| H1 | **refuted for kicks** | run 11 (leg channel sampled): 3 kicks each way, 99.1% / 98.9% of the owner's kick ticks rendered as a kick on the puppet, kick damage applied in 18-19 ms; `WeaponBash` still unmeasured (no script produces one) | kicks need no work; P2 covers feints only |
| H2 | **confirmed, thrusts are fine** | wind-up 92% raw / 98% lag-compensated, release 97-98%, direction 100%, 10 of 10 blows on both directions | no thrust-specific work |
| H3 | not exercised, deferred | run p3-spear (`-Sword eastern_spear_2_t3`, shield kept): usage index 0 on both machines for the whole run; the scripted `ToggleAlternativeWeapon` never switched the grip, so no usage change happened to mirror; spear thrusts rendered 96% raw / 99% lag-compensated, direction 100%, release 85% (sword: 97%) | no fix; re-test on the two-machine run with a real grip switch (X key), and in the PvE run where spearmen switch on their own |
| H4 | **refuted as latency, confirmed as a 250 ms release-tail disagreement, fixed (P4 ledger row)** | of the 'hits through block' 90% were kicks (attackType 1: through a shield by design); the rest landed in the ~250 ms after the defender RELEASED the block key, while the owner's animation was still lowering the shield (type `DefendShield`, stage `Defend`) and the puppet had already dropped it (the sender replicates the player's guard from INPUT, `supPlayerGuard` suppresses the lowering action on purpose). Block onset on the puppet 16-60 ms, direction agreement 99.8%, no puppet-idle gap longer than 14 ms while a block was held (runs 10-11, p2-after, p3-spear) | P4 keeps only the two-machine latency test; the one cosmetic gap (puppet snaps to idle instead of lowering; `Guard` return pose never mirrored) is recorded, not fixed |
| H5 | **half confirmed** | victim-side damage applied 24-31 ms after the attacker's collision (one hop, good); the victim's health on the ATTACKER's machine never changes (puppet health is not mirrored); flinch rendered 100/87/100/60% for single swings and thrusts, 18% in `mixed` | P5: health mirror and the missing flinch under pressure |
| H6 | **refuted as stated; the real mechanism found** | per-apply events (`apply`/`engine` lines, runs p2diag-p2diag3): every lost wind-up was WRITTEN to the puppet (SetActionChannel returned true, read back as ReadyMelee) and was gone on the next tick with no further packet; each followed another upper-body action ending on the puppet (a bash, a block release, a stale swing). Separately, the owner's cancel of a wind-up (feint -> `none`) was refused by engine priority (`set=-1 ok=0`), leaving stale swings on puppets. Defend poses played no part | P2 fix: owner exits from a held ReadyMelee override priority (`ShouldIgnorePriority`, 3-arg), and a receiver watchdog re-asserts a dropped ReadyMelee once per tick, at most twice, while the owner still holds it (`RemoteAgentActionProcessor.ReassertDroppedReadies`) |
| H7 | **half refuted** | run p6-mounted (both on horses, lances, horses standing 2.8 m apart): wind-up 95-96% raw / 98.4-99.6% lag-compensated, direction 100%, release 85-98%, no lost wind-ups, mount action mirrored 100%; the driver rode both horses in from 199 m at a trot without an overshoot | couch and M8 sliding need a MOVING mounted exchange (a `charge` script); not built in this pass |
| H8 | **death mirrors, health does not** | kill run (`-HealTo 100`): the fatal blow was applied on the victim's machine 12 ms after the attacker's collision, the victim's own agent stopped 0 ms later and the attacker's puppet of it 63 ms later; puppet health read 100 the whole way down (100 -> 14 on the owner). Kill credit / scoreboard: run p7-kill2 records `scoreboard_state` on both machines | P7: outcome mirrored within one hop; puppet health mirroring stays unimplemented (no vanilla UI reads it) |
| H9 | untested | standing duel: position error at impact 0.00 m | P8 adds footwork to `mixed` before measuring |
| H10 | **refuted on one machine** | bulk forced to 20 Hz, player priority poll 40 vs 60 Hz: onset, wind-up and position error identical within variance (runs 12/13); the player agent was already tier 0 with a 40 Hz floor and positions travel at 1 cm | P1 closes with the floor named (`PlayerLaneHz = 40`) and the 60 Hz result recorded; revisit only on the mounted duel (P6) |

Also learnt: attack onset is one hop plus one poll (median 20-28 ms, p99 31-61 ms) and direction and usage mirror
100% for sword work, so the M1 animation path itself is sound between players; the losses are in guards, feints,
health and reactions.

---

## 7. Milestones

Each milestone: **measure -> fix only what the numbers show -> unit or E2E guard -> re-run the rig with
`--baseline` -> verify with a human on both screens -> add the ledger row**. The cost column is a hard
rule, not an estimate.

| # | Milestone | Depends on | Exit criteria | Cost rule |
|---|---|---|---|---|
| P0 | Duel rig: scripts in repo, `DuelDriver`, duel fixtures and save, timeline duel mode, `DuelEvents`, `analyze_duel.py`, `Capture-Load.ps1`, baseline table for all six scripts in both directions with network and CPU | - | Unattended cycle under 8 minutes; section 10.1 filled | DEBUG only; 0 bytes on the wire in Release |
| P1 | Player lane (section 3): pinned priority, 60 Hz player movement, full-precision player frames, own action flush, guard re-assertion | P0, H10 | Onset latency, guard age and position error at impact move toward target with no other change; all section 3.2 limits met on one machine and on Omar's laptop; PvE run unchanged | Within the section 3.2 budget, each knob measured separately |
| P2 | Attack visibility: swings, thrusts, kicks, bashes, couch | P0, P1, H1 H2 H6 | wind-up and release >= 98%, direction 100%, kick/bash >= 98%, both directions | Local flags only, as M1; any new wire field needs a written reason |
| P3 | Usage-mode and wield ordering | P0, H3 | usage-mode match 100%; no first-tick mismatch | Reorder or piggyback on existing packets; no new message |
| P4 | Block and parry fidelity | P1, H4 | guard age <= one hop; hit-through-block 0 for blocks begun >= one hop before impact; block rendered >= 98% | Design note below; victim-confirmation reuses `BattlePuppetHit` and `NetworkAgentGuardReaction`, adding at most one small field |
| P5 | Hit feedback: damage latency, reactions, sounds | P1, H5 | damage latency <= one hop on the victim, reaction rendered >= 98% on the attacker | Predicted local reaction on the attacker uses the existing suppressed-hit path; reconcile on the routed result |
| P6 | Mounted PvP | P1, P2, H7 | couch and horseback swings >= 98%; sliding no worse than local; charge damage routed and mirrored | M8 change (locomotion-driven mount) only with the position-error report green |
| P7 | Outcome mirror: death, kill credit, scoreboard, result screen | P0, H8 | exact match on both machines and server across 20 duels | Reuse existing death/kill paths |
| P8 | Latency robustness | P1-P7 | all targets hold with 80 ms and 150 ms simulated one-way delay (`simulate_receive_pressure` plus a mesh delay knob), and on Omar's laptop over the internet; `worldQueue` flat; lane budget still met | No additional traffic under load |
| P9 | Hardening and release | P1-P8 | Release build has no diagnostics detour (`#if DEBUG` audit), `MissionsPatchInstallationTests` green, all new tests green, CoopFixes package built and played by both players, section 10 complete with final before/after | Package via `package-share.ps1`; verify zip by content as before |

### Design note for P4 (block fidelity), to be decided by H4's numbers

The attacker's client owns the collision. Its view of the victim's guard is one hop old, and the lane
(P1) shrinks that hop but cannot remove it. Two options:

- **A. Victim confirms.** `BattlePuppetHit` carries the attacker's collision timestamp and the guard the
  attacker saw. The victim's owner checks its own guard history (`GuardedHitWindow` already keeps one)
  at that timestamp; if it was blocking that direction, it applies a block instead of damage, plays the
  block reaction and sound, and sends `NetworkAgentGuardReaction` back so the attacker's screen shows
  the bounce. One small field, victim-authoritative for defence, attacker-authoritative for offence.
  Cost: the attacker sees "hit" for one hop before it turns into "blocked".
- **B. Attacker predicts.** Keep everything attacker-side and rely on the lane's guard re-assertion to
  make hit-through-block rare. Cheaper, but cannot reach zero on a real internet link.

Pick A unless H4 after P1 shows hit-through-block is already near zero on Omar's link. Do not build
either before the baseline.

### P4 after the rig: the one remaining disagreement, and the fix to try next

Measured (runs 10, 11, p2-after, p3-spear, p8-lat50): while a block is HELD, the puppet's guard is up 16-60 ms
after the owner's and in the same direction 99.8% of the time; no puppet-idle gap over 14 ms was seen during a
held block; kicks pass a shield on both machines. The only melee hits scored against a "blocking" owner landed in
the ~250 ms after the owner RELEASED the key: the owner's engine still reports the defend action at stage
`Defend` while the shield lowers, and vanilla resolves a hit against the victim's stage, so on the victim's own
machine that swing would have bounced. The puppet, however, is dropped to `act_none` the moment the release packet
arrives (the sender derives a player's guard from INPUT and `ShouldSuppressReleasedPlayerGuardAction` refuses the
lowering action on purpose). Two to five such hits per 60 s `mixed` exchange on one machine.

Fix, done and measured (ledger 10.2, run p4-tail): the retained-guard release now clears the flags and guard mode
at once but no longer forces `act_none`; the puppet's own engine lowers the shield over the same ~250 ms at stage
`Defend` (59 of 59 releases ended naturally, 250-256 ms; the forced release stays as a 0.45 s fallback and never
fired). The puppet renders a held `DefendShield` 97-98% of the time (was 80%) and no melee hit landed on a
blocking owner in `mixed` (was 2-3 per exchange). Verify with `mixed`: melee hits with `ownerStage=5`
(the `hit_through_block_cases` list) must go to zero and the puppet must never be left holding a guard the owner
released (`blocks` exchange: puppet `DefendShield` ticks while the owner is idle for more than one hop = 0).

### Design note for P5 (hit feedback)

`PlaySuppressedHitSound` already gives the attacker immediate audio for a suppressed puppet hit. The
same hook can play a predicted hit reaction on the puppet immediately and let the routed result confirm
or correct it (a block confirmation from P4 replaces it with the bounce). Measure first whether the
reaction is actually missing (H5) - it may already arrive through action replication.

---

## 8. Test strategy

| Layer | What | Runs |
|---|---|---|
| Unit | Priority rules (`ShouldIgnorePriority` per action type), lane selection and rate (`MovementRateController`, `MovementPriorityScheduler`), full-precision player record size (`AgentDataWireSizeTests` gains a lane case with its own ceiling), usage-mode ordering, guard-age arithmetic, block-confirmation decision, position clamp | every build |
| E2E mock engine (`E2E.Tests/Services/Missions`) | Routing paths: blow to puppet with block confirmation, reaction message round trip, death mirror for player agents, packet serialisation of any new field, lane packets interleaved with unit batches | every build (~15 min for the suite) |
| Live rig, one machine | The full cycle of section 5.1, `--baseline` against the previous run | every milestone, before and after each fix |
| Live rig, two machines | Same, Ali's PC and Omar's laptop, RTT recorded | P1, P4, P5, P8, P9 |
| Human check | Both players watch the same exchange and call what they see; recorded in section 10 | P2, P4, P5, P9 |

Every fix lands with its unit or E2E guard in the same commit. Diagnostics, the driver and the load
capture stay behind `#if DEBUG`; the Release build is checked for the absence of their detours before
packaging.

---

## 9. Risks

| Risk | Mitigation |
|---|---|
| The driver's synthetic input differs from real mouse input (attack direction from camera, attack-hold timing) | Drive through `MovementFlags` and direction flags, the same path the input layer writes; validate once by recording a human doing the `swings` script and diffing owner timelines |
| The lane starves units near the players in a big battle | `pvp_duel_in_crowd` cycle and the PvE run are part of every measurement; the section 3.2 crowd limit is a hard stop |
| The lane's 60 Hz is more than Omar's laptop can apply | `receiverApplyMsPerSecond` and fps on his machine are in every P1 row; the rate is a setting, not a constant |
| Fixing block fidelity (P4) changes who decides a hit, which touches loot contribution and kill credit | Keep offence attacker-authoritative; only the block verdict moves; P7 outcome mirror is the regression gate |
| Any change to `AgentPositionInterpolator` for H9 or M8 wrecks the tail again | `PuppetDeadReckoningTests` plus the position-error report are mandatory before and after |
| New per-hit messages raise message count in big battles | Everything piggybacks on existing messages; mesh profiler and `worldQueue` in every cycle, not only in the duel |
| Timeline arming logic for PvE (eight swingers per side) silently prevents duel recording | Explicit duel mode flag; the rig fails loudly if no samples were captured |
| Shared-clock assumption breaks on two machines | Server-time stamps plus measured offset; report latency as a range when the offset is uncertain |
| Whole-process CPU/network A/B shows a "win" or "loss" that is really variance | Rule 12: three cycles, spread reported, per-path timing is the number that counts |

---

## 10. Before/after record

### 10.1 Baseline (filled by P0; one row per script and direction, one-machine rig, then Omar's laptop)

| Script | Direction | RTT ms | Onset ms | Wind-up % | Release % | Direction % | Usage % | Guard age ms | Hit-through-block | Damage latency ms | Reaction % | Pos err at impact p90/max | Outcome | Wire KB/s (host/client) | Msgs/s mesh (host/client) | worldQueue | Sender ms/s | Receiver ms/s | fps (host/client) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| swings | A->B | 0 (loopback) | 21.0 / p99 61 | 95.3 (98.4 lag-comp.) | 96.8 | 100.0 | 100.0 | match 100.0%, mismatch age med - | 0 | applied 26.0; attacker screen never | 100.0 | 0.00/0.00 | def owner/puppet 1858/2000 | 0.34/0.34 | 7.8/8.3 | 0 | 3.4/3.3 | 0.04/0.04 | 200/200 |
| swings | B->A | 0 (loopback) | 24.5 / p99 31 | 95.5 (97.8 lag-comp.) | 98.0 | 100.0 | 100.0 | match 100.0%, mismatch age med - | 0 | applied 28.0; attacker screen never | 87.5 | 0.00/0.00 | def owner/puppet 1915/2000 | 0.34/0.34 | 7.8/8.3 | 0 | 3.4/3.3 | 0.04/0.04 | 200/200 |
| thrusts | A->B | 0 (loopback) | 28.5 / p99 34 | 92.2 (98.3 lag-comp.) | 97.7 | 100.0 | 100.0 | match 100.0%, mismatch age med - | 0 | applied 31.5; attacker screen never | 100.0 | 0.00/0.00 | def owner/puppet 1932/2000 | 0.34/0.34 | 7.8/8.3 | 0 | 3.4/3.3 | 0.04/0.04 | 200/200 |
| thrusts | B->A | 0 (loopback) | 26.5 / p99 41 | 92.8 (97.7 lag-comp.) | 97.0 | 100.0 | 100.0 | match 100.0%, mismatch age med - | 0 | applied 29.5; attacker screen never | 60.0 | 0.00/0.00 | def owner/puppet 1944/2000 | 0.34/0.34 | 7.8/8.3 | 0 | 3.4/3.3 | 0.04/0.04 | 200/200 |
| blocks | A->B | 0 (loopback) | - | - | - | - | - | - | - | - | - | no impacts | def owner/puppet 2000/2000 | 0.34/0.34 | 7.8/8.3 | 0 | 3.4/3.3 | 0.04/0.04 | 200/200 |
| blocks | B->A | 0 (loopback) | - | - | - | - | - | - | - | - | - | no impacts | def owner/puppet 2000/2000 | 0.34/0.34 | 7.8/8.3 | 0 | 3.4/3.3 | 0.04/0.04 | 200/200 |
| kick_bash | A->B | 0 (loopback) | kicks 3, rendered on puppet 99.1% (run 11) | - | - | - | - | match 100.0%, mismatch age med - | 0 | 1 of 3 landed; 19 ms | 0.0 | 0.00/0.00 | def owner/puppet 1998/2000 | 0.34/0.34 | 7.8/8.3 | 0 | 3.4/3.3 | 0.04/0.04 | 200/200 |
| kick_bash | B->A | 0 (loopback) | kicks 3, rendered on puppet 98.9% (run 11) | - | - | - | - | match 100.0%, mismatch age med - | 0 | 3 of 3 landed; 18 ms | 0.0 | 0.00/0.00 | def owner/puppet 1998/2000 | 0.34/0.34 | 7.8/8.3 | 0 | 3.4/3.3 | 0.04/0.04 | 200/200 |
| mixed | A->B | 0 (loopback) | 25.0 / p99 849 | 76.2 (78.5 lag-comp.) | 95.4 | 99.3 | 100.0 | match 71.4%, mismatch age med 1235.0 | 3 | applied 25; attacker screen never | 18.2 | 0.00/0.00 | def owner/puppet 1960/2000 | 0.34/0.34 | 7.8/8.3 | 0 | 3.4/3.3 | 0.04/0.04 | 200/200 |
| mixed | B->A | 0 (loopback) | 20 / p99 33 | 76.3 (79.1 lag-comp.) | 95.0 | 100.0 | 100.0 | match 83.3%, mismatch age med 1171.5 | 1 | applied 24; attacker screen never | 18.2 | 0.00/0.00 | def owner/puppet 1971/2000 | 0.34/0.34 | 7.8/8.3 | 0 | 3.4/3.3 | 0.04/0.04 | 200/200 |
| mixed (bulk forced 20 Hz, pre-P2 build) | A->B | 0 (loopback) | 28 / p99 40 | 82.4 (85.9 lag-comp.) | 93.3 | 99.6 | 100.0 | match 100.0% | 0 | applied 27.0; attacker screen never | 100.0 | 0.10/0.10 (all-tick p90 0.00, max 0.28) | def owner/puppet 1949/2000 | 0.34/0.34 | 15.4/15.9 | 0 | 2.4/2.5 | 0.05/0.05 | 200/200 |
| mixed (bulk forced 20 Hz, pre-P2 build) | B->A | 0 (loopback) | 24.5 / p99 41 | 85.5 (88.6 lag-comp.) | 95.5 | 99.3 | 100.0 | match 94.7% | 0 | applied 25.5; attacker screen never | 100.0 | 0.00/0.10 (all-tick p90 0.10, max 0.22) | def owner/puppet 1946/2000 | 0.34/0.34 | 15.4/15.9 | 0 | 2.4/2.5 | 0.05/0.05 | 200/200 |
| footwork (bulk forced 20 Hz, pre-P2 build) | A->B | 0 (loopback) | 17.5 / p99 28 | 96.9 (99.4 lag-comp.) | 96.1 | 100.0 | 100.0 | match 100.0% | 0 | applied 20.5; attacker screen never | 100.0 | 0.00/0.00 (all-tick p90 0.00, max 0.10) | def owner/puppet 1958/2000 | 0.34/0.34 | 15.4/15.9 | 0 | 2.4/2.5 | 0.05/0.05 | 200/200 |
| footwork (bulk forced 20 Hz, pre-P2 build) | B->A | 0 (loopback) | 23.5 / p99 33 | 95.7 (99.2 lag-comp.) | 94.6 | 100.0 | 100.0 | match 100.0% | 0 | applied 27.5; attacker screen never | 100.0 | 0.00/0.00 (all-tick p90 0.00, max 0.20) | def owner/puppet 1974/2000 | 0.34/0.34 | 15.4/15.9 | 0 | 2.4/2.5 | 0.05/0.05 | 200/200 |
| mounted (lance thrusts, horses standing; run p6-mounted, post-P2 build) | A->B | 0 (loopback) | 21.5 / p99 64 | 96.3 (99.0 lag-comp.) | 84.7 | 100.0 | 100.0 | - | 0 | no blows landed (A: lance short of 2.8 m) | - | 0.00/0.00 (standing) | mount action agreement 100% | 0.59/0.59 | 8.1/8.5 | 0 | 4.2/4.2 | 0.06/0.07 | 200/200 |
| mounted (lance thrusts, horses standing; run p6-mounted, post-P2 build) | B->A | 0 (loopback) | 23 / p99 46 | 94.7 (98.4 lag-comp.) | 98.1 | 100.0 | 100.0 | - | 0 | applied 32 | 100.0 | 0.00/0.00 (standing) | mount action agreement 100% | 0.59/0.59 | 8.1/8.5 | 0 | 4.2/4.2 | 0.06/0.07 | 200/200 |
| PvE regression (`Auto-AnimTest.ps1`) | both | | | M1 wind-up | M1 release | | | | | | | M4 pos err mean/p90/max | | | | | | | |

Baseline source: `Scripts/Rig/runs/2026-09-05-1137-baseline` (run 10, one machine, both clients at 200 fps,
mesh ping 0 ms, server worldQueue 0 throughout). Both duellists carry `vlandia_sword_4_t4` + `reinforced_kite_shield`,
on foot, healed to 2000 before every exchange. "attacker screen never": the victim's health never changes on the
attacker's machine (puppet health is not mirrored), so that latency is unmeasurable today. Kicks and the crowd/mounted
rows are filled by the next run (leg-channel sampling was added after this one; the crowd scenario arrives with P1).

### 10.2 Fix ledger (one row per fix, appended as it lands)

| Date | Milestone | What changed (files) | Metric targeted | Before | After | Delta | Added wire KB/s | Added msgs/s (mesh / server) | Added CPU ms/s (`HotPathCostDiagnostics`) | Process CPU % / fps delta (spread) | PvE regression (M1 / M4) | Guard test | Seen on both screens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2026-09-05 | P1 player lane | `MovementRateController.PlayerLaneHz` (named; the priority poll floor), tests in `MovementRateControllerTests` | onset, wind-up, position error with bulk forced to 20 Hz (`Duel-Test.ps1 -ForceRate 20`, new `footwork` script) | prio 40 Hz: onset 15-28 / p99 25-62 ms; wind-up lag-comp. 98-99%; pos err p90 0.00 m, max 0.10-0.20 m (footwork) | prio 60 Hz: onset 22-30 / p99 32-67 ms; 98-99%; 0.00 m / 0.10-0.20 m | none measurable | 0.00 (348 B/s both; idle agents send nothing) | +5.7 / +1.2 mesh, 0 server | sender +1.2 ms/s (2.4 -> 3.6) | 14.3 -> 14.5 / 14.4 -> 14.1 %; 200 fps both | not run (no behaviour change kept) | E2E `MovementRateControllerTests` 35 pass | n/a |
| 2026-09-05 | P2 attack visibility | `AgentActionData.ShouldIgnorePriority` (3-arg: owner exit from a held ReadyMelee overrides priority), `RemoteAgentActionProcessor.TrackPendingReady` / `ReassertDroppedReadies` (a dropped ReadyMelee is put back once per tick, at most twice, while the owner holds it); DEBUG `apply`/`engine`/`reassert` duel events; test `ReplicatedSwingPriorityTests.OwnerLeavingHeldWindup_Overrides` | wind-up rendered in `mixed` (feints, quick attacks after a bash/block/stale swing) | run 11: 94.7% / 80.9% lag-compensated, 5 lost wind-ups of 33, onset p99 34 / 48 ms (runs 12-13: 762 and 856 ms outliers) | run p2-after: 99.2% / 98.9%, 1 lost of 34, onset p99 31 / 34 ms; 11 re-asserts fired, every one restored the wind-up on the first try (`was=-1/0 ok=1`) | +4.5 / +18.1 points | 0.00 (no new messages) | 0 / 0 | not measured separately (one SetActionChannel per dropped wind-up, ~1 per 5 s in a duel) | 13.8 -> 14.0 %, 200 fps | `p2-pve2` (600 v 600 bandit battle, 60 s): client wind-up 83.4% / release 91.7%, host 83.3% / 89.8% (documented PvE reference 81.7-86.3% / 89.0-89.7%); position error p50 0.10 m, p90 0.72 m (M4 reference 0.72 m), max 6.15 m with 1.3% of samples >= 2 m (movement code untouched by P2) | E2E 51 pass | n/a |
| 2026-09-05 | P8 latency robustness (measurement, no fix) | `LiteNetP2PClient` receive-side hold (`SetSimulatedLatency`, `TryHoldPayload`, `DispatchHeldPayloads`), DEBUG command `coop.debug.movement.simulate_latency`, `Duel-Test.ps1 -Latency 40,60` | every metric under a 40-60 ms one-way delay on both machines (~100 ms round trip), post-P2 build | 0 ms: onset 21-26 / p99 28-67 ms; wind-up 94-96% raw, 98.4-99.7% lag-comp.; release 90-97%; damage applied 22-27 ms; position p90 0.00-0.22 m | 40-60 ms: onset 80-90 / p99 88-106 ms (= delay + poll + frame); wind-up 82-87% raw, **96.4-99.4% lag-compensated**; release 82-90%; damage applied 86-89 ms (one hop); reaction agreement 100%; kicks rendered 99%; position p90 0.00-0.14 m, max 0.10-0.54 m (footwork/mixed); 0 lost wind-ups, 12 re-asserts; melee hits on a blocking owner: 2 (both at owner stage `Defend`, i.e. the release tail plus one hop) | latency-bound losses only; the mirror itself holds | 0 | 0 / 0 | 0 (hold is debug-only) | 14.0 -> 13.9 %, 200 fps | n/a | n/a | n/a |
| 2026-09-05 | P4 block release tail | `RemoteAgentActionProcessor.ClearRetainedGuardAction` defers the forced `act_none` (`PendingGuardRelease`, `SettlePendingGuardReleases`, 0.45 s grace): the puppet's own engine lowers the shield after the released flags and guard mode are applied, exactly as the owner's does; the old immediate `act_none` is only the fallback. DEBUG `guardrelease` duel events say which happened | `blocks`: owner `DefendShield` rendered on the puppet; `mixed`: melee hits landing while the owner's engine was still at stage `Defend` | p2-after: blocks 80% / 80% rendered; mixed hits-through-block 2 / 3 (all in the ~250 ms release tail); guard match at impact 90.5% / 75.0% | p4-tail: blocks **98% / 97%**; mixed hits-through-block **0 / 0**; guard match 88.9% / 100%; 59 of 59 releases ended naturally at 250-256 ms (owner's tail: ~250 ms), 0 forced; wind-up unchanged (96.0 / 98.7% lag-comp.) | +17 points rendered, -5 hits through block | 0 | 0 / 0 | 0 (no new writes; one fewer `SetActionChannel` per release) | 14.0 -> 13.9 %, 200 fps | p4-tail-lat50 (40-60 ms one-way): blocks rendered 94% / 95%, hits through block 1 / 0 (p8d: 2 / 0), 32 / 32 releases natural at 250-255 ms | E2E 51 pass | n/a |
| | | | | | | | | | | | | | |

### 10.3 Release summary (filled at P9, 2026-09-05, one-machine rig; Omar's laptop still to run)

| Metric | Before the plan (run 10/11) | After (p2-after, p8c) | Under 40-60 ms one-way delay (p8d) | Cost of the fixes |
|---|---|---|---|---|
| Wind-up rendered, player -> player, `mixed` (lag-compensated) | 94.7% / 80.9% | 99.2% / 98.9% (repeat 99.0% / 98.3%) | 98.3% / 98.4% | 0 wire, 0 messages; one SetActionChannel per dropped wind-up (about one per 5 s) |
| Wind-up rendered, `swings` / `thrusts` (lag-compensated) | 97.8-98.4% / 97.7-98.3% | 98.4-99.1% / 98.0-98.1% | 96.4-98.6% | - |
| Release rendered | 95-98% | 96-98% | 82-90% (a 60 ms hop out of a 300 ms release) | - |
| Direction / usage mirrored | 99-100% / 100% | 100% / 100% | 100% / 100% | - |
| Kick rendered | unmeasured (run 11: 99.1% / 98.9%) | 98.2-99.3% | 98.6-99.1% | - |
| Onset latency median / p99 | 20-28 / 31-61 ms (one poll + one frame) | 21-26 / 28-67 ms | 80-90 / 88-106 ms (= delay + poll + frame) | - |
| Guard onset / direction while held | 16-60 ms / 99.8% | same | not re-measured | - |
| Hit-through-block (melee, owner in a live block) | 2-3 per 60 s `mixed`, all in the ~250 ms release tail | **0** (p4-tail, after the deferred release) | 2 before the fix (p8d) -> **1 / 0** after it (p4-tail-lat50); held block rendered 94-95% under the delay (was ~80%); 32 of 32 releases natural at 250-255 ms | 0 wire, one fewer engine write per release |
| Damage latency (attacker collision -> victim's machine applies it) | 24-31 ms | 22-27 ms | 86-89 ms (one hop) | - |
| Reaction agreement (both machines flinch or neither) | 100% | 100% | 100% | - |
| Death mirrored on the attacker's screen | - | 63 ms, 70 ms after the fatal blow (kill runs) | not run | - |
| Position error, footwork, p90 / max | 0.00 / 0.10-0.20 m (bulk forced 20 Hz) | 0.00-0.10 / 0.14 m | 0.00-0.10 / 0.10 m | - |
| Player movement rate | tier 0, priority poll floor 40 Hz | unchanged; 60 Hz measured and rejected (+1.2 ms/s sender for no gain) | - | 0 |
| PvE regression (600 v 600) | client 81.7-86.3% wind-up / 89.0-89.7% release (documented) | client 83.4% / 91.7%, host 83.3% / 89.8%; position p90 0.72 m (M4 reference 0.72 m) | - | - |
| Process CPU % / fps, both clients | 14.0 / 200 | 13.8-14.0 / 200 | 13.9 / 200 | none measurable |
| Mounted: puppet horse position error at a gallop, p50 / p90 / max (section 13) | 0.2 / 0.3-0.7 / 0.5-1.4 m (loopback) | 0.0 / 0.1 / 0.3-0.4 m | 0.6-0.7 / 0.9-1.1 / 1.1-1.4 m -> **0.0-0.1 / 0.1-0.2 / 0.2-0.4 m** | 0 wire; one vector multiply per mounted puppet per frame |
| Mounted: couched lance on the other screen | flickering 52-68% of the couch time, 235-402 equipment packets per 10 s | **99.4-99.8%**, 1-3 packets per 10 s | same | one usage re-assert per couch |
| Mounted: javelins (standing and from the saddle) | not measured | mirrored 100%, reconstructed in 26-38 ms, damage applied 56-64 ms, every hit on the thrower's screen a hit on the target's | not run under delay | 0 (measured only) |
| Frame-rate mismatch 60 vs 40 fps (section 14) | - | no agreement metric moved; latency +16 to +35 ms onset, +9 to +22 ms damage | - | 0 (answered) |

Done after this summary (section 13): the moving mounted exchanges. Still not done: puppet health mirroring (no vanilla UI reads
it), kill-count credit on the scoreboard (`scoreboard_state` prints party membership only), the block-release tail
(design note above section 7.1), and the two-machine run on Omar's laptop, which is the only place real jitter and
packet loss can be measured.

## 11. Order of work and rough effort

| Step | Effort | Output |
|---|---|---|
| Move harness into `Scripts/Rig/`, add `Duel-Test.ps1` and `Capture-Load.ps1` skeletons | 1 day | Scripts committed, PvE run still passes, load capture proven on the PvE run |
| Duel fixture command, save, and the in-crowd variant | 0.5 day | Both clients enter a 1v1 with no clicks |
| `DuelDriver` and the six scripts | 1.5 days | Both players' agents perform identical sequences on demand |
| Timeline duel mode, `DuelEvents`, `analyze_duel.py` with `--baseline` | 1.5 days | Section 10.1 filled, hypotheses H1-H10 answered |
| P1 player lane, one knob at a time, each with its before/after row | 2 days | Lane in, budget proven, ledger rows |
| P2 attack visibility | 1-2 days | Targets met, tests added |
| P3 usage-mode ordering | 0.5-1 day | |
| P4 block fidelity | 2-3 days | Design chosen by the numbers, implemented, guarded |
| P5 hit feedback | 1-2 days | |
| P6 mounted | 2-4 days, M8 is the unknown | |
| P7 outcome mirror | 1 day | 20-duel run exact |
| P8 latency, two-machine runs with Omar | 1-2 days plus scheduling | |
| P9 hardening, package, play test, section 10.3 | 1 day | Release zip, this file finalised |

Roughly three and a half weeks of sessions. P0 is the only step that cannot be shortened; every later
estimate is conditional on what the baseline shows, and any milestone whose hypothesis the baseline
kills is closed as "not a bug" with its numbers, the way M2, M7 and M9 were.

---

## 12. Execution status (2026-09-05, one-machine rig; the two-machine run with Omar is still to do)

| Milestone | State | Evidence (run folders under `Scripts/Rig/runs/`) |
|---|---|---|
| P0 duel rig + baseline | **done** | `2026-09-05-1137-baseline` (table 10.1), `2026-09-05-1151-baseline` (kicks); rig traps in 4.6 |
| P1 player lane | **closed, measured, floor kept at 40 Hz** | `1201-lane-before` vs `1209-lane-after` (ledger 10.2); `PlayerLaneHz` named in code |
| P2 attack visibility | **fixed and verified** | `1218/1230/1235-p2diag*` (cause), `1249-p2-after` (99.2 / 98.9% lag-compensated wind-up in `mixed`, 1 lost of 34) |
| P3 usage mode | deferred, not reproducible on the rig | `1258-p3-spear` (usage index never changed; thrusts 99% lag-comp.) |
| P4 block fidelity | **fixed**: the release tail is now mirrored (puppet lowers the shield on its own engine, 250-256 ms like the owner); held-block rendering 80% -> 98%, hits through a live block 2-3 -> 0 in `mixed` | `1417-p4-tail`; under a 40-60 ms delay `1424-p4-tail-lat50`: blocks rendered 94% / 95%, hits through a live block 1 / 0 (p8d: 2 / 0), every release natural |
| P5 hit feedback | verified, no fix needed | reaction agreement 100% on every row; damage applied 22-33 ms after the collision |
| P6 mounted | **moving exchanges built and measured** (section 13): lance joust, couch, glaive drive-by, javelins standing and from the saddle, on the loopback rig and under a 40-60 ms one-way delay. Fixed: the puppet horse lead (velocity the owner sends x frame age + network delay), the couched-lance usage flicker on the wire (235 -> 1-3 packets per 10 s) and the couch usage not sticking on the puppet (usage-only apply + per-tick re-assert). Measured and left as is: javelin mirror 100% at ~30 ms with ghost impacts on the target's own physics for javelins that missed on the thrower's screen; the first hit of an exchange stalls both game threads 50-77 ms (engine); mounted wind-up variant timing (cosmetic) | `1316-p6-mounted`, `1513/1540-m-lance-*`, `1522/1549-m-glaive-*`, `1529/1557-m-jav-*`, `16xx-v2-*`, `v3-*` |
| Frame-rate mismatch (60 vs 40 fps) | **answered** (section 14): nothing desyncs; latency grows by one frame of the slower machine (onset +16 to +35 ms, damage +9 to +22 ms); every agreement metric unchanged. Recommendation: the slower player sets the in-game frame limiter to what the machine holds | `1533-fps-60-40` |
| P7 outcome mirror | death mirrored in 63 and 70 ms (two kill runs); both machines list the same two parties on the scoreboard after the kill; kill COUNT credit is not printed by `scoreboard_state`, so it stays unverified by the rig | `1309-p7-kill`, `1337-p7-kill3` (`scoreboard_*.txt`) |
| P8 latency robustness | **measured** under a real 40-60 ms one-way delay (`1354-p8d-lat50`; LiteNetLib's own SimulateLatency turned out inert in the shipped build, so the mesh client holds payloads itself): onset 80-90 ms, wind-up 96-99% lag-compensated, damage applied in one hop, position p90 <= 0.14 m, kicks 99%, no lost wind-ups; 2 melee hits landed in the block-release tail (the P4 note). `1327-p8-lat50` (no delay, argument bug) doubles as a second post-P2 sample | ledger 10.2 |
| P9 hardening | guard tests, Release build and PvE regression pass (`p2-pve2`: wind-up 83%, release 90-92%, position p90 0.72 m, all within the documented PvE range); full suites: Coop.Tests 743/743, GameInterface.Tests 1594/1594, E2E 2179/2183; the four failures (`TournamentWorldItemOrderingTests.UncorrelatedRuntimePickup_IsRejectedWithoutDetachedBroadcast`, `...ObservedDropTimeout_AfterIdentitylessPickup_RestoresObservedWeapon`, `BattleMountIdentityTests.UnregisteredMountHit_FallsBackToRiderKeyedRouting`, `BattleDamageMirrorTests.RegisterBlow_MissileWithUnsyncedProjectile_Throws_ModelingOnAgentHit`) fail identically on an untouched export of HEAD (`git archive` copy, same four, same messages), so they predate this work; duel rig sources moved out of the git-ignored `Debug/` folder into `source/Missions/Battles/DuelRig/` | `pve-p2b-console.log`, test logs in the session scratchpad |

Rules kept: every fix has a before and an after on the same rig; nothing was committed; all runs were unattended.

## 13. Mounted PvP: before / after (P6 continued, 2026-09-05)

Most real fights between the two players will be on horseback with a lance, a long glaive or javelins, so the
mounted lane gets its own before/after record. Same rig, same one-machine loopback, both players mounted on their own
war horses; every row is one exchange (attacker -> defender) and the "after" column is filled only from a run made
with the fix installed, on the same kit and script.

### 13.1 What the rig gained for this (harness only, no sync change)

| Piece | What | Why |
|---|---|---|
| `coop.debug.duel.arm w0 [w1] [w2] [w3]` | any four weapon slots (`-` = empty) | lance + shield, glaive alone, two javelin stacks + sword |
| `coop.debug.duel.face <id> [stand_off_m]` | stand-off override | javelins are thrown from 12 m, melee from 1.4 m |
| `coop.debug.duel.heal` | also heals both horses | most mounted blows land on the horse; a dead horse ends the ride |
| driver: explicit horse steering and braking | the rider's LOOK does not steer a horse, and neither does the input vector's x alone: the vanilla controller sets the `TurnRight` / `TurnLeft` MOVEMENT FLAGS (0x10 / 0x20) with it. The driver now sets those from the signed angle between the horse's heading and the aim point, cuts the throttle while pointing away, aims 1.5 m beside the opponent in riding scripts so two chargers pass instead of colliding, and REINS IN (input y = -1) inside the stand-off and whenever a script or `stop` lets go of a moving horse: a horse keeps its gait when the reins go slack | runs p6-ride and m-lance-before (twice): both riders left the boundary in ~30 s and the mission ended for everybody |
| `coop.debug.duel.boundary off` (`BattleBoundaryOverride`) | nobody is retreated for leaving the field on that machine; Duel-Test switches it off on both clients | a horse that overshoots must never end the mission for both players again; the driver still steers back inside |
| scripts `ride_swing`, `couch`, `throw` | drive-by glaive swings; gallop + X (couch) + pass + wheel, four passes; wield the javelins, aim at the chest with a gravity lead, six throws with the stack refilled | the three mounted weapons the players actually use |
| `MissileHandler` duel events | `shot seq=` on the thrower, `missile seq= how=reconstructed/dropped/failed ff=` on the other machine | the javelin mirror, matched by shot sequence on the shared clock |
| analyzer: mounted / missile table | attacker position error (the charger the defender sees), horse gait agreement, horse speed error, sliding % (body moving while the engine's own speed says still) per machine, throws / mirrored % / mirror latency / fast-forwards / missile hits / victim-side impacts | the M8 "ice skating" and the javelin mirror, as numbers |
| `coop.debug.movement.frame_limit <fps>` | the engine's own frame limiter | the 60 vs 40 fps question (section 14) |
| `Duel-Test.ps1 -Weapons -StandOff -FrameLimit -BuildDir` | kit, stand-off, frame caps, and a FROZEN build to install | a before/after chain keeps its "before" while the tree is rebuilt |
| riding scripts attack by proximity (`Step.HoldFrom` / `ReleaseAt`) | `ride`: wind up inside 12 m, release inside 5 m; `ride_swing`: 9 / 3.5 m; new `ride_throw`: aim inside 25 m, throw inside 12 m from the saddle | the first mounted runs timed their attacks and landed nothing on the move (0 blows in `ride` and `ride_swing`) |
| `coop.debug.movement.mounted_fixes <on|off> [lead|latch|reassert]`, `Duel-Test -MountedFixes off` | the mounted fixes switched off at runtime for a "before" run | one build per before/after pair instead of a frozen snapshot per fix |
| driver brakes for the closing speed of BOTH riders | `mustBrake` uses my speed + the opponent's | a 12 m javelin stand-off ended at 2.8 m when each horse braked for its own speed |
| DEBUG `routed` / `damage_rx` events | attacker send instant and victim receive instant of every routed blow | the damage latency split into pending / wire / drain legs (13.4: the slow first hit is a game-thread stall) |

### 13.2 Before / after table

One row per thing that can differ between the two screens; "before" is the frozen rig build without the mounted
fixes (`build-before4`: steering, braking and boundary immunity in the harness, sync code untouched), "after" the
same harness with the fixes of 13.3. Loopback rig, 200 fps both, no added delay unless the row says so.

| Kit / script | Dir | Metric | Before | After | Run before / after |
|---|---|---|---|---|---|
| lance+shield, standing `thrusts` | A->B / B->A | wind-up rendered (lag-comp.) / release / direction | 93.6 (97.8) / 96.0 / 100 ; 96.4 (99.2) / 92.3 / 100 | 92.9 (97.2) / 97.5 / 100 ; 92.4 (97.6) / 97.7 / 100 (unchanged: standing puppets get no lead) | `1513-m-lance-before` / `1540-m-lance-after` |
| lance+shield, standing `mixed` | A->B / B->A | held block rendered (DefendShield) ; hit-through-block ; guard match at impact | 97% ; 0 ; 100% ; 98% ; 0 ; 100% | 97% ; 1 (of 1 hit, at stage Defend with the same block on both screens: a lance point past a shield edge) ; 100% ; 97% ; 0 ; - | |
| lance+shield, standing `mixed` | A->B / B->A | damage applied on the victim's machine, median | 18 / 20 ms (first blow of an exchange: 59-151 ms, see 13.4) | 142 ms (one hit, and it was the first: the stall of 13.4) ; no hit | |
| lance+shield, `ride` (joust at 8-12 m/s) | A->B / B->A | attacker puppet position error p50 / p90 / max | 0.2 / 0.4 / 0.5 m ; 0.2 / 0.3 / 0.5 m | **0.1 / 0.2 / 0.4 m ; 0.1 / 0.1 / 0.3 m** (defender p90 0.3-0.4 -> 0.1-0.2) | |
| lance+shield, `ride` | A->B / B->A | horse engine-speed error p90 ; puppet horse sliding | 0.1 m/s ; 0.0% ; 0.1 m/s ; 0.0% | 0.3 m/s ; 0.0% ; 0.1 m/s ; 0.0% | |
| lance+shield, `couch` (gallop, X, pass) | A->B / B->A | attacker puppet position error p50 / p90 / max | 0.3 / 0.5 / 0.8 m ; 0.2 / 0.5 / 0.7 m | **0.1 / 0.2 / 0.6 m ; 0.1 / 0.2 / 0.7 m** (the max is the horse collision: both rear) | |
| lance+shield, `couch` | A->B / B->A | couched-lance usage agreement over the exchange ; equipment packets per 10 s while couched ; puppet lance couched | 69.8% ; 85.7% ; **235 packets** ; couched in 15-90 ms bursts (745 / 323 flips) | packets **1-3** (latch); but the puppet lance read couched for ONE sample of 1396 owner-couched: re-wielding the slot for the usage does not stick. Second fix (usage-only apply + per-tick re-assert) built for the v2 runs | |
| lance+shield, `couch` | A->B / B->A | horse collisions mirrored (Rear / HitObject samples owner vs puppet) | 380/380 ; 200/198 ; 141/141 | | |
| glaive, standing `swings` | A->B / B->A | wind-up (lag-comp.) / release | 94.5 (98.6) / 96.3 ; 95.1 (98.7) / 95.0 | 95.0 (98.1) / 96.7 ; 95.7 (99.1) / 95.4 | `1522-m-glaive-before` / `1549-m-glaive-after` |
| glaive, standing `mixed` | A->B / B->A | direction ; swing variant (exact animation index) | 100 ; 88.8 ; 100 ; 88.5 (the puppet's engine leaves the mounted ready-in animation for the hold loop ~640 ms before the owner's does; same direction) | 100 ; 93.3 ; 100 ; 87.8 (untouched by the fixes) | |
| glaive, `ride_swing` (drive-by) | A->B / B->A | attacker puppet position error p50 / p90 / max | 0.2 / 0.4 / 0.6 m ; 0.2 / 0.3 / 0.4 m | **0.1 / 0.1 / 0.3 m ; 0.1 / 0.1 / 0.4 m** | |
| javelins, standing `throw` at 10.7 m | A->B / B->A | throws mirrored ; reconstruction latency median / p99 | 6/6 (100%) ; 26 / 64 ms ; 6/6 ; 34 / 39 ms | | `1529-m-jav-before` / |
| javelins, `throw` | A->B / B->A | hits on the attacker's machine ; impacts the victim's own physics saw ; damage applied median | 3 ; 5 (two javelins that missed on the attacker's screen struck the victim on his own; no damage, suppressed) ; 56 ms ; 6 ; 6 ; 60 ms | | |
| javelins, `throw` | A->B / B->A | aim (ReadyRanged) / throw (ReleaseThrowing) / reload rendered | 85 / 97 / 48% ; 88 / 97 / 49% | 95 / 97 / 81% ; 88 / 96 / 48% (`1557-m-jav-after`, but thrown from 2.8 m: the two horses overshot the 12 m stand-off because each braked for its own speed, not the closing speed; fixed in the driver for the v2 runs) | |
| javelins, `throw` | A->B / B->A | throws mirrored ; reconstruction latency median / p99 ; hits ; victim-side impacts (point blank) | - | 6/6 ; 35.5 / 70 ms ; 6 ; 6 ; 6/6 ; 33.5 / 45 ms ; 6 ; 6 (every javelin that hit on the thrower's screen hit on the target's, and none that did not) | `1557-m-jav-after` |
| lance, v2 `ride` (proximity thrust on the pass, one build, fixes off -> on) | A->B / B->A | attacker puppet position error p50 / p90 / max | 0.2 / 0.4 / 0.5 m ; 0.2 / 0.3 / 0.7 m | 0.1 / 0.3 / 0.5 m ; 0.1 / 0.3 / 0.5 m | `1603-v2-lance-before` / `1608-v2-lance-after` |
| lance, v2 `ride` | A->B / B->A | horse engine-speed error p90 ; puppet horse sliding | 0.2 m/s ; 2.6% ; 0.9 m/s ; 2.6% | 0.1 m/s ; 0.0% ; 0.1 m/s ; 0.0% | |
| lance, v2 `ride` | B->A | blows landed on the move (thrusts at 5.7-7.3 m/s) ; damage applied median | 3 of 7 attacks, 1 on the horse ; 39 ms | 0 of 4 (the pass geometry, not the sync: wind-up 99%, direction 100% on both) | |
| lance, v2 `couch` | A->B / B->A | attacker puppet position error p50 / p90 / max ; horse speed error p90 ; sliding | 0.2 / 0.3 / 0.8 m ; 1.6 m/s ; 1.9% ; 0.2 / 0.3 / 0.5 m ; 0.7 m/s ; 0.0% | 0.1 / 0.2 / 0.4 m ; 0.3 m/s ; 0.0% ; 0.1 / 0.2 / 0.5 m ; 0.1 m/s ; 0.0% | |
| lance, v2 `couch` | A->B / B->A | puppet lance couched while the owner's is (usage-only apply + per-tick re-assert) ; equipment packets per 10 s | the owner never couched in the before run (speed at the toggle) | **0.0% of 4227** (client -> host puppet) ; **99.7% of 3570** (host -> client puppet) ; 1-3 packets. The watch only started when the first apply's read-back differed, and on the host it matched for the rest of that frame; made sticky (watch while the owner reports a non-default usage) for the v3 couch pair | |
| glaive, v2 `ride_swing` (proximity swing on the pass, fixes off -> on) | A->B / B->A | attacker puppet position error p50 / p90 / max | 0.3 / 0.7 / **1.4 m** ; 0.2 / 0.3 / 0.8 m | **0.1 / 0.3 / 0.5 m ; 0.1 / 0.2 / 0.4 m** | `1614-v2-glaive-before` / `1618-v2-glaive-after` |
| glaive, v2 `ride_swing` | A->B / B->A | horse engine-speed error p90 ; puppet horse sliding | **4.9 m/s ; 5.5%** ; 0.2 m/s ; 1.3% | 0.8 m/s ; 0.2% ; 0.1 m/s ; 0.0% | |
| glaive, v2 `ride_swing` | A->B / B->A | wind-up (lag-comp.) / release / direction ; blows on the move | 97.8 (98.8) / 97.1 / 100 ; 98.2 (99.6) / 94.9 / 100 ; 0 | 98.7 (99.3) / 96.6 / 100 ; 98.5 (99.6) / 94.2 / 100 ; 0 (the swings on the pass did not reach: geometry, not sync) | |
| javelins, v2 `ride_throw` (aim inside 25 m, throw inside 12 m from the saddle, fixes off -> on) | A->B / B->A | throws ; mirrored ; reconstruction latency median ; attacker puppet position error p50 / p90 / max | 4 ; 100% ; 30.5 ms ; 0.3 / 0.4 / 0.6 m ; 3 ; 100% ; 36 ms ; 0.2 / 0.3 / 0.4 m | 3 ; 100% ; 38 ms ; 0.1 / 0.4 / 0.6 m ; 4 ; 100% ; 29.5 ms ; 0.2 / 0.3 / 0.6 m (throws from the saddle are slower rides; the lead has less to remove) | `1622-v2-jav-before` / `1626-v2-jav-after` |
| javelins, v2 `ride_throw` | A->B / B->A | javelin hits on the move ; victim-side impacts ; damage applied | 1 ; 0 (the victim's own physics saw the hit javelin miss) ; 171 ms (first hit) ; 1 ; 1 ; 49 ms | 0 ; 0 ; - ; 0 ; 0 ; - (a moving target at 12-25 m: hits are rare either way) | |
| javelins, v2 `ride_throw` | A->B / B->A | horse engine-speed error p90 ; puppet horse sliding | 0.5 m/s ; 0.8% ; 0.2 m/s ; 1.5% | 0.2 m/s ; 0.0% ; 0.1 m/s ; 0.0% | |
| lance, v2 `ride` + `couch` under a 40-60 ms one-way delay (fixes off -> on, first lead) | A->B / B->A | attacker puppet position error p50 / p90 / max, `ride` ; `couch` | 0.8 / 1.1 / 1.4 m ; 0.4 / 0.6 / 1.0 m ; 0.5 / 0.7 / 0.9 m ; 0.6 / 0.7 / 0.9 m | 0.6 / 0.9 / 1.2 m ; 0.4 / 0.8 / 1.1 m ; 0.4 / 0.6 / 0.9 m ; 0.4 / 0.5 / 0.8 m: **the lead barely bit**. Cause found in the code: it derived velocity from two consecutive frames and refused intervals under 20 ms, and the player lane sends every 16.7 ms, so it almost never fired (the loopback gains came from the odd late frame). Rewritten to use the horse velocity the owner already sends; re-measured in the v3 pair below | `1631-v2-lance-before-lat50` / `1636-v2-lance-after-lat50` |
| lance, v2 `ride` under the delay | B->A | blows on the move ; damage applied median | 3 of 5 attacks ; 91 ms (one hop + the frame) | 0 of 4 | |
| lance, v3 `couch` (one build; fixes off -> on: usage latch + usage-only apply + sticky per-tick re-assert + velocity lead) | A->B / B->A | puppet lance couched while the owner's is ; equipment packets per 10 s ; usage applies per exchange | **52.2% / 68.1%** ; **402** ; 844 + 841 and 667 + 666 (the flicker, on the wire) | **99.4% / 99.8%** ; **3** ; 3 + 3 and 2 + 2, one re-assert each | `1645-v3-couch-before` / `1650-v3-couch-after` |
| lance, v3 `couch` | A->B / B->A | attacker puppet position error p50 / p90 / max (velocity lead, loopback) | 0.3 / 0.5 / 0.7 m ; 0.3 / 0.4 / 0.7 m | **0.0 / 0.1 / 0.4 m ; 0.0 / 0.1 / 0.3 m** (9262 leads applied, mean 0.30 m) | |
| lance, v3 `ride` + `couch` under a 40-60 ms one-way delay (one build; fixes off -> on with the VELOCITY lead) | A->B / B->A | attacker puppet position error p50 / p90 / max, `ride` ; `couch` | 0.7 / 1.1 / 1.4 m ; 0.6 / 0.9 / 1.2 m ; 0.6 / 0.9 / 1.1 m ; 0.7 / 0.9 / 1.1 m | **0.0 / 0.1 / 0.3 m ; 0.1 / 0.1 / 0.2 m ; 0.1 / 0.1 / 0.3 m ; 0.1 / 0.2 / 0.4 m** (8305 leads applied, mean 0.66 m, delay estimate 50 ms) | `1654-v3-lance-before-lat50` / `1700-v3-lance-after-lat50` |
| lance, v3 under the delay | A->B / B->A | defender puppet position error p90 ; horse engine-speed error p90 ; equipment packets per 10 s | 0.9-1.1 m ; 0.2-2.4 m/s ; 357 | **0.1 m** ; 0.2-0.4 m/s ; 1 | |
| approach (all mounted runs) | both | 191 m ride-in: arrived at stand-off, top speed, outside boundary | 2.5-2.7 m, 12.8-14.3 m/s, 0 probes outside | v2: the 12 m javelin stand-off held once (11.0 m) and failed once (2.5 m: the pair closed at 28 m/s); throttle now relative to the stand-off and the brake margin 1.6 x closing speed | |

### 13.3 Fixes (one row each, as they land)

| Date | Fix | Code | Measured by | Before | After |
|---|---|---|---|---|---|
| 2026-09-05 | Mounted lead: the puppet horse is eased toward where its owner IS, not where the frame said. Velocity = the horse velocity the owner sends in every frame (`MountMovementDirection x MountSpeed`); time = frame age (capped 0.1 s) + one-way delay (mesh ping / 2 plus the rig's simulated hold, capped 0.3 s) + 20 ms of presentation lag; lead capped 3 m, horse speeds 0.4-22 m/s only. The first version differenced two frames and refused intervals under 20 ms, so at the 60 Hz player lane it almost never fired; the 1/12 s "ease lag" term it carried was also unsupported by the zero-delay measurement (0.2 m at 10 m/s = one frame) and is gone | `AgentPositionInterpolator.TryComputeMountLead` / `FollowMounted`, `TargetFrame.OneWayLatencySeconds` / `MountVelocity`, `AgentMovementHandler.EstimateOneWayLatencySeconds`, `LiteNetP2PClient.SimulatedOneWayDelaySeconds`, runtime switch `MountedSyncSwitches.LeadEnabled`; tests `PuppetDeadReckoningTests` (8) | attacker puppet position error in `ride`, `couch`, `ride_swing`; the lagged pairs | loopback 0.2 / 0.3-0.7 / 0.5-1.4 m (p50 / p90 / max); under 40-60 ms 0.6-0.7 / 0.9-1.1 / 1.1-1.4 m | loopback 0.0 / 0.1 / 0.3-0.4 m; **under the delay 0.0-0.1 / 0.1-0.2 / 0.2-0.4 m** (`1700-v3-lance-after-lat50`); defender p90 1.0 -> 0.1 m |
| 2026-09-05 | Couched-lance usage flicker kept off the wire: a usage index that flips twice within 150 ms is latched to the value it toggles TO until it is still for 150 ms; a plain press still goes out at once | `WieldUsageLatch`, `AgentMovementHandler.StabilizeUsage`, switch `MountedSyncSwitches.UsageLatchEnabled`; tests `WieldUsageLatchTests` | `couch`: equipment packets per 10 s, usage applies per exchange | 235-402 packets per 10 s, ~840 applies per exchange | 1-3 packets, 2-3 applies (`1650-v3-couch-after`) |
| 2026-09-05 | Couch usage that sticks on the puppet: a usage-only change (same wielded slot) is applied with the vanilla client's `SetUsageIndexOfWeaponInSlotAsClient` instead of re-wielding the slot, and while the owner reports a non-default usage the puppet is watched and the usage re-asserted every tick after the native tick if its engine dropped it | `AgentEquipmentData.Apply` / `TryReassertUsage`, `PuppetUsageWatch`, `AgentMovementHandler.ReassertPuppetWeaponUsage` (called from `CoopMissionController` after the native tick), switch `MountedSyncSwitches.UsageReassertEnabled` | `couch`: puppet lance couched while the owner's is ("Alt usage rendered %") | 52-68% flickering (raw); 0% / 99.7% with the latch alone (re-wield did not stick on one machine) | **99.4% / 99.8%**, one re-assert per couch |

### 13.4 Open observations (measured, not yet fixed)

| Observation | Evidence | Note |
|---|---|---|
| The FIRST routed blow of an exchange reaches the victim 59-151 ms after the collision; every later one in 10-47 ms | lance `thrusts` 151, 47, 39, 28 ms; glaive `mixed` 151; foot `mixed` (p4-tail) 104, 25, 19, 14 ms. Split with the DEBUG `routed` / `damage_rx` events (glaive-after `swings`): first blow pending 60 + wire 70 + drain 6 ms, the next two 0 + 25 + 4 ms; the timeline shows a GAME-THREAD STALL on both machines at that moment (attacker frames of 51 and 52 ms, victim 77 ms, against 5 ms frames) | not the wire, not the router: both engines hitch on the first melee impact after a quiet spell (hit sound / effect streaming is the likely cause). Same in single player; a pre-warm at mission start is the only lever and is not done |
| Two of six javelins that missed on the thrower's screen struck the target on the target's own machine (no damage: the intercept drops a puppet shooter's missile blow) | `1529-m-jav-before` A->B: 3 attacker hits, 5 victim-side impacts | the reconstructed javelin flies the same path from the same launch; the two hitboxes differ by pose. A visible javelin sticking in a player who took no damage; the reverse case (damage from a javelin the victim saw miss) did not occur in 12 throws |
| Mounted wind-up animation variant: the puppet's engine switches from the ready-in index to the hold-loop index ~640 ms before the owner's does (2419 -> 2418 at +691 ms vs +1331 ms) | glaive `mixed`: swing variant 88.8 / 88.5%, direction 100% | same direction and pose family; the ready-in animation runs faster on the puppet. Cosmetic |
| Javelin aim (ReadyRanged) rendered 85-88%, reload 48% | `1529-m-jav-before` | one throw's aim started ~380 ms late on the puppet (onset p99); reload is the between-throws pull |
| Standing lance thrusts do 0 damage to the other horse (6 and 9 blows, `result=1`, `dmg=0`) | `1513-m-lance-before` `thrusts` | armour absorbs a standing thrust; both machines agree (nothing routed). Not a sync issue |
| A glaive-armed defender is hit past a block that covers another direction (six "hits through block" in glaive `mixed` A->B, every one with the attacker's screen showing the same block direction the owner held) | `1549-m-glaive-after` | legitimate: a weapon block stops one direction, a shield all four. The analyzer now applies the direction rule (`WEAPON_GUARD_COVERS`) |
| The couch usage applied by re-wielding the slot did not stick on the puppet (couched for one sample, then upright) once the flicker was latched off the wire | `1540-m-lance-after` `couch`: puppet couched samples 1 of 1396 owner-couched | the puppet's engine re-evaluates the couch itself. Fix in progress: usage-only changes go through `SetUsageIndexOfWeaponInSlotAsClient` (the vanilla client's call) and a per-tick re-assert (`PuppetUsageWatch`, `ReassertPuppetWeaponUsage`) while the owner reports the couch |

## 14. Frame-rate mismatch: does 60 fps vs 40 fps desync? (2026-09-05)

**Short answer: it cannot desync anything that persists, and the code audit below finds nothing frame-counted on the
authoritative path; what a slower machine changes is how OFTEN it sends and how STALE its puppets look on the other
screen.** The rig measures it directly (`fps-60-40` run, Ali capped at 60, Omar at 40 through the engine's own
limiter; results in 14.2).

### 14.1 Code audit

| Mechanism | Frame- or time-based | Effect of 40 vs 60 fps |
|---|---|---|
| Damage, kills, knock-downs | attacker's machine decides per blow, victim's owner applies (`BattleBlowInterceptPatch` -> `BattleDamageRouter`) | none: a decision, not a simulation that has to agree |
| Movement sends (`AgentMovementHandler.PollMovementAgents`) | time-based intervals (`1 / PriorityHz`, `1 / BulkHz`) polled once per frame | the player lane is 40 Hz (`PlayerLaneHz`), so a 40 fps client sends its player every frame and a 60 fps client every 1.5 frames: same wire rate |
| Adaptive bulk rate (`MovementRateController`) | normalizes fps against the FRAME LIMIT the client set | an uncapped machine that only reaches 40 fps is read as struggling and its BULK (troop) rate drops to 20-30 Hz by design; the same machine with a 40 fps limiter set in Video options is read as healthy and keeps 40 Hz. Recommend the slower player sets the limiter to the fps the machine actually holds |
| Receiver apply (`ApplyMovement`) | every queued snapshot applied once per frame, newest wins for position targets | at 40 fps, 1.5 snapshots per frame from a 60 Hz sender: harmless |
| Position ease / dead reckoning (`AgentPositionInterpolator`) | `dt`-based (`MountedFollowRate * dt`, frame age in seconds) | none beyond one frame of staleness (25 vs 17 ms) |
| Action replication (`AgentActionHandler`, `RemoteAgentActionProcessor`) | events on change; progress in seconds | onset latency grows by half a frame on the slower machine (about +4 ms) |
| Retained guard / re-assert windows | `MaximumReadyReasserts = 2` TICKS; guard phases per tick | the re-assert window is 50 ms at 40 fps, 33 ms at 60: cosmetic |
| Guard reaction capture / apply | `MaximumCaptureAttempts = 3`, `MaximumRemoteApplyAttempts = 6` TICKS | 75 / 150 ms at 40 fps vs 50 / 100 ms at 60: cosmetic, both far inside a flinch |
| Missile reconstruction retries | `MaxReconstructionAttempts = 30` DRAINS (frames) | 0.75 s at 40 fps vs 0.5 s at 60 before a shot is dropped: cosmetic |
| Deferred missile damage | `MinimumPresentationEpochs = 2` FRAMES plus seconds | 50 vs 33 ms hold before a routed javelin hit is applied: cosmetic |
| Synthetic mount turn clear | `RemoteSyntheticMountTurnClearGraceFrames = 3` FRAMES | 75 vs 50 ms: cosmetic |
| Engine simulation (animations, physics, collision sweeps) | `dt`-based inside the engine, one owner per agent | the owner's engine is the only one that matters for its own agent |

The frame-counted constants are all presentation grace windows on the RECEIVING machine (how long to wait before
giving up on a re-assert, a reaction, a missile). None decides damage, health, position authority or who owns what,
so a mismatch shows as a few tens of milliseconds of difference in when a cosmetic fallback fires, never as two
machines disagreeing about the fight.

### 14.2 Measured (run `1533-fps-60-40`, Ali 60 fps / Omar 40 fps through the engine limiter, on foot, swings + mixed + footwork, against the 200 / 200 fps baseline `1417-p4-tail`)

The caps held (load table: host 60 fps, client 40 fps); the 40 fps client's movement lane settled at 40 / 40 Hz
(bulk / priority), the 60 fps host's at 60 / 60; the receive queue read 16 and 23 ms instead of 4-5 (one frame of
the slower machine).

| Metric (`mixed`, 60 s, both directions) | 200 / 200 fps | 60 -> 40 fps (Ali attacks Omar) | 40 -> 60 fps (Omar attacks Ali) |
|---|---|---|---|
| Onset latency median / p99 | 20 / 40 ms ; 20 / 40 ms | 55 / 72 ms | 39 / 88 ms |
| Wind-up rendered raw / lag-compensated | 94.8 / 98.5% ; 96.0 / 98.7% | 89.6 / **98.4%** | 90.9 / **98.4%** |
| Release rendered | 95.2% ; 95.1% | 89.1% | 90.6% |
| Direction / usage | 100 / 100 | 100 / 100 | 100 / 100 |
| Kick rendered | 96.0% ; 98.6% | 98.8% | 95.7% |
| Guard match at impact / hits through a live block | 88.9% / 0 ; 100% / 0 | 91.2% / **0** | 94.3% / **0** |
| Damage applied on the victim's machine, median | 23 ms ; 28.5 ms | 45 ms | 37 ms |
| Reaction agreement | 100% | 100% | 100% |
| Position error p90 (footwork) | 0.10 m | 0.10 m | 0.10 m |
| Health at the end, owner vs puppet | equal | equal | equal |

Reading: nothing desynced. Every "agreement" metric (direction, usage, hit-through-block, reaction agreement, end
health, position error) is unchanged. What moved is LATENCY, by about one frame of the slower machine: onset +16 to
+35 ms, damage +9 to +22 ms; the raw wind-up and release percentages drop 4-6 points for the same reason and the
lag-compensated wind-up (the same comparison with the one-hop shift removed) stays at 98.4%, exactly the 200 fps
value. The 40 fps machine both sends less often (40 Hz lane, one send per frame) and applies what it receives one
25 ms frame later; the 60 fps machine watching it pays the send side, the 40 fps machine watching the 60 fps one
pays the apply side. Neither can make the two disagree about anything that persists.

### 14.3 Best way to handle it

1. Nothing to "solve" for correctness: authority is per agent and per blow, and every cadence is in seconds.
2. The slower player should set the in-game frame limiter to what the machine actually holds (40) rather than run
   uncapped: the adaptive bulk rate then treats 40 fps as healthy and keeps troop updates at 40 Hz instead of
   shedding to 20-30 Hz. (`coop.debug.movement.frame_limit` is the rig's way of doing the same.)
3. Optional tidy-up, low value: express the five frame-counted grace windows above in seconds so they read the same
   on every machine. Not done: the measurement in 14.2 shows no agreement metric moved, so there is nothing for it
   to fix; it would only make the cosmetic windows identical to the millisecond.
4. If the extra one-frame latency on a 40 fps machine ever matters (it is 25 ms, under the 40-60 ms of the
   network itself), the lever is the same as for the network: the mounted lead already covers the sender's frame
   age (13.3), and the on-foot dead reckoning covers one update period.
