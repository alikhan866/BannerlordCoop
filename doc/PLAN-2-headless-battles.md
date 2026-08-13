# Plan 2 - Battle capabilities (observation first, headless execution second)

**Goal.** Be able to observe, assert on, and eventually run battles without a person watching them.

Two independent halves, and the order matters:

- **Group A - observe battles.** Works against battles fought TODAY by a rendered client. Needs nothing from
  the rest of this plan.
- **Groups B onward - run battles headless.** Gated on whether a mission can tick without a renderer.

Written as capabilities, not investigations. Where a real defect is named, it is only to justify a capability.

**Depends on.** Plan 1 for the control channel, the comparer and the scenario runner. Group A additionally
depends on nothing at all - it can be built first and pays off immediately.

---

## What already exists (do not rebuild)

| Piece | Where | Use |
|---|---|---|
| Per-side battle state | `BattleTeamDiagnostics` - `[BattleSize] battleSize=.. onField(def=..,atk=..) defPhase(total=..,remaining=..)` | The core observable |
| Supply decisions | `CoopTroopSupplier` - "engine asked for N, my remaining quota is M; supplying K" | Why troops did or did not arrive |
| Sizing source | `BattleFieldRoom`, `BattleSizeTargets` | Target per side |
| Reserve construction | `BattleTroopReserveBuilder` - logs `DUPLICATE HERO` | Reserve integrity |
| Agent ownership | `OwnedAgentReplicator`, `PuppetSpawner`, `AgentPositionInterpolator` | Who owns and who mirrors |
| Damage routing | `BattleDamageRouter`, `RegisterBlowPatch` | Blow adjudication |
| Casualty and result | `PuppetDeathApplier`, `BattleResultCommitter`, `ServerBattleCompletionHandler` | Outcome |
| Render-free precedent | `HeadlessMapScene` | Pattern for a render-free scene |
| Host election | `BattleHostHandler` | Who simulates today |

---

# Group A - Observe battles (no headless mission required)

Everything here consumes diagnostics that already exist, from battles fought by a normal rendered client.
**Build this first.** It is the cheapest capability in either plan and it covers the defect class that has cost
the most time.

### C1 - Battle state snapshot
Per side, on demand: battle size, target, agents on field, reserve total, reserve remaining, supplied pointer.
The inputs are already emitted by `BattleTeamDiagnostics` and `CoopTroopSupplier`.

### C2 - Spawn-progress liveness
Report when a side sits below its target while its reserve is NOT draining.
*Motivation: measured as `onField(atk=1)` against `atkPhase(total=1309, remaining=1123)` - 1,123 men who could
never reach the field, for three and a half minutes.*

### C3 - Expected-count assertions
"This battle should field N per side within T." Fails if the field never reaches strength.

### C4 - Supply-refusal reporting
Surface every refusal with its reason - requested, quota, supplied - rather than leaving it in a log to be
found later.

### C5 - Reserve integrity checks
Duplicate heroes, a hero in two parties, reserves that do not sum to the side total. `BattleTroopReserveBuilder`
already detects the first two.

### C6 - Cross-client agent census
Both clients must agree how many agents each side has, and which parties they came from.

### C7 - Ownership census
Every live agent has exactly one owner. Report agents nobody claims.
*Motivation: unroutable blows were dropped, producing 483 dropped hits in three minutes and troops that could
not be killed.*

### C8 - Battle outcome capture
Winner, casualties per party, prisoners, loot offered and claimed - and whether the result committed to the
campaign at all.

### C9 - Battle timeline
One ordered record per battle: spawns, waves, refusals, deaths, host changes, map-event lifecycle, conclusion.
The artefact a person reads when a battle goes wrong.

### C10 - Scale as a parameter
Observation must hold at the sizes where faults appear. Small battles hid this class entirely; the failure
needed battle size 400 against ~2,800 reserve troops.

---

# Group B - GATE: can a mission tick with no renderer?

### C11 - The spike
Start a mission in a headless process, tick it, read the clock. Timebox to one day.
**If yes:** Groups C onward are reachable.
**If no:** stop. Everything below is unreachable, Plan 3 becomes archival, and the answer is to harden the
existing seam using Group A. Write up the blocking call.

TaleWorlds' own multiplayer dedicated servers run missions without rendering, so this is not impossible in
principle. Campaign missions are the harder case - they drag in deployment and order UI that MP missions do not
have.

---

# Group C - Run a mission headless

### C12 - Renderer dependency inventory
Every call in mission startup needing a renderer, scene view or UI manager, each marked replace / skip / unknown.

### C13 - Render-free mission scene
The analogue of `HeadlessMapScene`.

### C14 - Navigation available
The dedicated server already loads a navmesh during campaign load; establish what a mission scene adds.

### C15 - Agents without visuals
Spawn, query position and health, with no mesh, animation or sound.

### C16 - Deployment completes
`CheckDeployment` reserves `InitialSpawnNumber - ReservedTroopsCount` and skips the whole side - plan-making
included - while short. Deployment must never be under-delivered.

### C17 - Formations and orders without the order UI
AI-driven only.

### C18 - Mission tick loop
Whatever drives mission time without a UI tick, at a known rate.

### C19 - Coop behaviours attach
`CoopBattleBehaviorAttacher`, `CoopBattleController`, `BattleInstanceLifecycle`.

### C20 - Supply and reinforcement headless
`CoopTroopSupplier` is already server-fed and sizing no longer depends on the campaign object surviving.

### C21 - Damage and death headless
`BattleDamageRouter`, `PuppetDeathApplier`, `RegisterBlowPatch`, with casualties reaching the campaign.

### C22 - Conclusion and commit headless
Result reconciliation, loot, prisoners. Interacts with `FinalizedBattleRetention`.

---

# Group D - Drive battles

### C23 - Enter a battle from a scenario
Force a map event and enter its mission from the control channel.

### C24 - Deterministic resolution
Resolve without human aim - the `kill_own_team` family - so the same scenario ends the same way.

### C25 - Battle-shape parameters
Side sizes, reserve sizes, party counts, army composition, siege versus field, attacker versus defender.
The rig must be able to ASK for the shape a fault needs rather than wait for one.

### C26 - Mid-battle events
Reinforcements arriving, a party joining or leaving, a host disconnecting, the map event ending underneath the
mission.
*Motivation: the last of those is the exact condition that froze both sides' reinforcements.*

### C27 - One headless client against AI

### C28 - Two headless clients against each other

---

# Group E - Judge a battle

### C29 - Outcome equivalence
Compare campaign outcomes across runs and across client types - casualties, prisoners, loot, winner.
**Never compare agent positions.** One owner simulates and everyone else interpolates the frame that owner
reported, so position differences measure interpolation timing, not correctness.

### C30 - Repeatability
The same scenario and seed produce the same campaign outcome.

### C31 - Headless versus rendered equivalence
The same scenario run both ways must agree on outcome.

### C32 - Performance baseline
Agents fielded, CPU per agent, bandwidth per client, tick rate held. The numbers Plan 3 is measured against.

---

# Group F - Server-side missions

### C33 - The server runs a mission
The first attempt at the dedicated server hosting a battle with no client hosting it. The true prerequisite for
Plan 3, and the largest single item in either plan.

---

## What is out of reach, and stays out

| Limit | Why |
|---|---|
| Rendering faults in battle | No renderer. Group A observes data; a rendered pass catches the rest at a delay |
| Player skill and timing | A driven client does not aim, feint or block like a person |
| Whether a battle FEELS right | Pacing, difficulty and fairness are judgements, not measurements |
| Agent-position equality | Not a correctness property - see C29 |
