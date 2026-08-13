# Plan 3 - Server-authoritative PvP

**Goal.** Move combat authority from an elected client to the dedicated server, so no player has a host
advantage and no battle depends on a peer staying connected.

**Depends on.** Plan 2, and specifically B18 (the server hosting a mission at all). Nothing here is reachable
until that lands. Plan 2's B1 gate decides whether this file is buildable or archival.

---

## Why the host is a client today

Not a preference - a consequence. `BattleHostHandler` elects **the first MISSION-READY client** (BR-010), and
only clients have a mission at all. The dedicated server has no renderer, so it never loads one. It was never a
candidate.

The resulting split: **server authoritative over the campaign, host client authoritative over the fight.** The
server owns reserves, casualties, loot and results; the host owns agents and blows. Most sync defects live on
that seam - including the 12 Aug case where an unroutable blow was dropped and troops could not be killed.

## What moving the seam buys

| | Client-hosted (today) | Server-hosted |
|---|---|---|
| Host's own latency | zero | one RTT |
| Everyone else | one RTT to the host | one RTT, **symmetric** |
| PvP fairness | host wins ties | equal |
| Host leaves mid-battle | migration, epochs, successor line | nothing to do |
| CPU | host renders AND simulates | server simulates only |
| Cheat surface | host's process is authoritative | server is authoritative |

**The honest caveat for the current rig:** the dedicated server runs on the same desktop as a client. Moving
simulation there adds no hardware - it splits one machine between rendering and simulating, and adds a hop for
the local player. Server hosting wins clearly only once the server is on its own box.

---

## Milestones

### S1 - Choose the authority model
Two options, and the choice governs everything after it.
- **Full server simulation.** Server runs the mission, clients render a replica. Maximum fairness, maximum work,
  requires prediction to stay playable.
- **Server-arbitrated hits.** Clients keep simulating locally; the server adjudicates blows and deaths only.
  Much smaller change, removes the worst of the host advantage, keeps some client trust.
**Done when** one is chosen with the reasoning written down.

### S2 - Server hosts a mission
Inherited from Plan 2 B18. Restated here because it is the gate.
**Done when** the dedicated server runs a battle with no client hosting.

### S3 - Election becomes assignment
`BattleHostHandler` stops electing and always names the server.
**Done when** `[BattleHost] Elected host ...` is replaced by a fixed server assignment.

### S4 - Retire migration
Epochs, successor lines, `PrepareForReserveOwnershipExpansion`, `BattleHostMigrated`, stale-epoch rejection
(BR-102) - all of it exists because the host can leave. A server that never leaves deletes the category.
**Do not delete before S2 is proven.** Keep it behind a flag until the server path is trusted.
**Done when** the migration paths are unreachable and the tests that covered them are retired or repurposed.

### S5 - Input model
Clients stop applying their own blows and start sending intent: move, attack, block, order.
**Done when** a client's input reaches the server and produces an agent action.

### S6 - Replication to clients
Every client renders the server's agents. `OwnedAgentReplicator` and `PuppetSpawner` already do most of this -
the difference is that now every agent is a puppet, including your own.
**Done when** a client renders a battle it is not simulating.

### S7 - Client-side prediction
Without it, your own character responds one RTT late and the game feels broken. This is the single hardest item
in all three files.
**Done when** local input feels immediate at 80ms simulated latency.

### S8 - Reconciliation
When the server disagrees with the prediction, correct without visible snapping.
**Done when** an induced disagreement resolves smoothly.

### S9 - Lag compensation
Rewind to what the shooter saw when adjudicating a hit, or accept that high-ping players miss.
**Done when** hit registration is fair at asymmetric pings.

### S10 - Interest management
The server now sends every agent to every client. Cull by relevance before this becomes the bandwidth ceiling.
**Done when** bandwidth per client is bounded and measured.

### S11 - Bandwidth and CPU budget
Compare against Plan 2 B17's baseline.
**Done when** there are numbers for 2, 4 and 8 clients.

### S12 - Fairness validation
Two clients, deliberately unequal simulated latency, same duel repeated.
**Done when** win rate is not predicted by who is closer to the server.

### S13 - Cheat surface review
With the server authoritative, enumerate what a modified client can still assert.
**Done when** the remaining trusted-client claims are listed and each is accepted or closed.

### S14 - Rollback plan
If performance is unacceptable, how do we get back to client hosting? S4 is the risky one-way door.
**Done when** reverting is a flag flip, not an archaeology project.

---

## Risks

- **S7 is where projects like this die.** Prediction and reconciliation are a genuinely hard problem, and
  Bannerlord's combat - directional blocks, timing windows - is unforgiving of it. If S1 chooses full
  simulation, budget most of the effort here.
- **S4 is a one-way door.** Deleting migration is a large simplification and hard to undo. Flag it, do not
  delete it, until the server path has survived real sessions.
- **The current hardware makes this look worse than it is.** Measuring server hosting on the same desktop as the
  client will under-sell it. Test on separate hardware before judging.
- **Consider S1's second option seriously.** Server-arbitrated hits removes most of the host advantage for a
  fraction of the work, and it does not need prediction. Full simulation is the better end state; arbitration
  may be the better next step.
