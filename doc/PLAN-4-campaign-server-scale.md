# Plan 4 - Campaign server scale

**Goal.** Raise the player count the campaign server can carry.

**Why this is separate.** Battles shard cleanly - they are already instances with ids, owners and lifecycles,
so Plan 3 plus a placement layer scales them across machines. The campaign does not shard: one world, one
clock, one diplomacy graph, parties crossing every region. It is therefore the ceiling for the whole system,
and raising it is a different project from anything in Plans 1-3.

**The shape of the limit.** It is mostly NOT bandwidth. It is that Bannerlord's campaign is a single-threaded
simulation and coop marshals every authoritative mutation onto that one thread (`GameThread.Run` /
`GameThread.RunSafe`). More cores and more money do not help; only single-core speed does, linearly at best.

**Depends on.** Plan 1, for headless clients to generate load. Measuring a 20-player campaign otherwise
requires 20 people.

---

## What already exists (do not rebuild)

| Piece | Where | Use |
|---|---|---|
| Per-message bandwidth profile | `Common.Logging.PacketProfiler` | Already reports bytes/sec and a per-type breakdown |
| Party sync timing | `PartySyncPerformanceClock`, `PartySyncPerformanceFileWriter`, `PartySyncPerformancePartyProvider` | Existing perf instrumentation |
| Game-thread marshalling | `GameThread.Run`, `GameThread.RunSafe`, `AllowedThread` | The chokepoint, and the guard around it |
| Batching precedent | `NetworkTroopRosterElementBatch` | Proves batching is acceptable here |
| Adaptive rate precedent | `MovementRateController`, adaptive movement hz | Proves rate adaptation is acceptable here |
| Serialised commits | `ServerBattleCompletionHandler` - `RunSerialized` | Where battle results queue |
| Join baseline | `JoinCampaignBaselineSender`, `MobilePartyBehaviorSnapshot.TryApplyJoinBaseline` | The largest single payload |
| Retry cap | `LoadingState.MaxBaselinesWithoutProgress` | Bounds a stuck join |

---

## The numbers to beat

Measured on a live two-player session, 12-13 Aug:

| Observation | Value |
|---|---|
| Join baseline payload | **~5.5 MB per joining client** |
| Stuck join resend rate | ~9/sec, ~550 KB/s, per stuck peer |
| Server outbound during battle | **152 KB/s** to one or two clients |
| Heaviest single message type | `NetworkTroopRosterElementBatch` - 5,448 packets / 452 KB per 10s |
| Parties in the world | ~1,549 |
| Campaign time while a client catches up | **paused for everyone** |

---

# Group A - Measure before changing anything

Every fix in this codebase that stuck came from a measurement, and every one that did not came from reasoning
about what ought to be slow. Nothing in Groups B-F should start before A is usable.

### S1 - Game-thread tick budget
Where each tick goes: vanilla campaign versus coop handlers, broken down by handler.
**Done when** the top ten consumers of the game thread can be named with their share.

### S2 - Per-message cost, not just size
`PacketProfiler` reports bytes. Add the game-thread time each message type costs to apply.
**Done when** message types can be ranked by CPU as well as by bandwidth.

### S3 - Join cost breakdown
What actually composes the 5.5 MB, by category and count.
**Done when** the payload can be attributed to its contents.

### S4 - Scaling curve
Measure at 2, 4, 8, 16 clients using headless clients from Plan 1: tick time, bandwidth, join duration,
paused fraction of wall clock.
**Done when** the curve for each is known and the first thing to break is identified by measurement rather
than by argument.

---

# Group B - Shrink the join

The single largest payload, and the one that couples to campaign time.

### S5 - Incremental resends
A retry currently resends the whole baseline. Send only what has not been acknowledged.
**Done when** a retry costs a fraction of a first send.

### S6 - Delta from a known base
A client that already holds the save, or a previous baseline, needs only the difference. Ship the base once,
then deltas.
**Done when** a rejoin costs materially less than a first join.

### S7 - Compression
Cheap and independent of the above.
**Done when** the wire size drops with the CPU cost measured, not assumed.

### S8 - Chunked transfer with progress
So a partial transfer is resumable and observable rather than all-or-nothing.
**Done when** a join reports progress and survives an interruption.

### S9 - Cheaper validation
`TryApplyJoinBaseline` builds sets across ~1,500 parties and walks them, per join. At 20 simultaneous joins
that is 20 passes over the world on the game thread.
**Done when** validation cost per join is sub-linear in world size, or moved off the game thread.

---

# Group C - Stop one player stopping the world

Time is currently a shared resource that any single player can hold. Two mechanisms do it: the campaign pauses
while a client catches up, and fast-forward is capped while any player is in a map event
(`CapFastForwardForMapEvent`), with a third ad-hoc hold beside them - `[BattleHost] Sieges held while their
players fight`.

Each was reasonable for a handful of players. None survives 100, where somebody is always joining, always in a
battle, always besieging. The target is not a better hold. **It is no global holds at all.**

### S10 - Let the world run during catch-up
The joining client applies a baseline, then the delta accumulated while it transferred, and only then enters.
Requires the baseline to be a consistent snapshot carrying a sequence number the delta continues from.
**Done when** a join no longer stops campaign time for other players.

### S11 - Bound and report catch-up
A client that cannot converge is dropped with a reason rather than holding the world - the existing retry cap
generalised.
**Done when** a slow client degrades only its own experience.

### S12 - Enumerate what campaign advancement can break under a live mission
The cap exists because the world moving can invalidate a battle in progress. Enumerate every such
interaction: the map event finalized, a party destroyed, an army disbanded, peace or war declared, a settlement
changing hands, a besieging force leaving, a participating hero dying or being captured elsewhere.
**Done when** the list is complete and each entry has an observed or reasoned consequence.

### S13 - Make each one safe instead of held
For every entry in S12, the running mission survives it and reconciles at the end. `FinalizedBattleRetention`
is the worked example: a battle whose map event is finalized mid-mission now keeps the object it needs and
commits its result against it, rather than the world being frozen to prevent the situation.
**Done when** each enumerated interaction can occur during a live mission without loss.

### S14 - Delete the global holds
Once S13 covers the list, remove `CapFastForwardForMapEvent`, the siege hold and the occupancy pause. They are
workarounds for S12, and keeping them after the fix reintroduces the ceiling they cause.
**Done when** no single player's activity can slow or stop campaign time for anyone else.

### S15 - A world clock that scales
With the holds gone, speed is a shared decision rather than an individual veto. Options, to be chosen with
measurement: a fixed world rate that nobody controls, a consensus or majority rate, or a rate that is fixed
while any battle is live and free otherwise.
**Done when** a defined model runs at 100 players and no player can veto it alone.

---

# Group D - Get work off the game thread

The thread is the ceiling. Everything that does not strictly need it should leave it.

### S16 - Classify every handler
Which genuinely mutate campaign objects, and which only serialise, validate, diff or log.
**Done when** every handler is labelled must-marshal or need-not.

### S17 - Move pure work off-thread
Serialisation, validation, divergence computation and logging are pure and can run off the game thread;
only the mutation marshals.
**Done when** the top pure consumers identified in S1 no longer run on the game thread.

### S18 - Batch mutations per tick
One marshalled action per message is expensive at volume. Coalesce per tick.
**Done when** marshalled actions per tick fall materially without behaviour changing.

### S19 - Protect the tick from a slow or throwing handler
A handler that throws escapes into `Game.OnTick` and wedges the loop - seen live, followed by "a blocking Run
action was not processed by the game loop" and a frozen client. Isolate and report instead.
**Done when** an artificially throwing or slow handler degrades itself and nothing else.

---

# Group E - Steady-state traffic

### S20 - Interest management on the campaign map
A client probably does not need per-tick detail for parties on the far side of the world. Send by relevance.
**Caution:** this interacts with join-baseline validation, which counts parties and rejects a mismatch. Reduced
delivery must not become a reduced world. Coverage rules and the count check have to be designed together, or
this trades a bandwidth problem for a join failure.
**Done when** bandwidth per client falls with no change to what the client believes exists.

### S21 - Rate adaptation for campaign updates
`MovementRateController` established this for agents; the campaign map has the same opportunity.
**Done when** update rate responds to relevance and load.

### S22 - Batch more message types
`NetworkTroopRosterElementBatch` is the precedent and was the heaviest type measured. Find the next ones from
S2.
**Done when** the top types by count are batched.

### S23 - Fan-out cost
Per-client serialisation versus serialise-once-send-many.
**Done when** outbound CPU per client is measured and reduced where the payload is identical.

---

# Group F - Commit throughput

### S24 - Measure commit contention
Battle results serialise through `RunSerialized`. With many concurrent battles this becomes a queue.
**Done when** commit latency under concurrent battle load is known.

### S25 - Batch or pipeline commits
Without losing the ordering guarantees the serialisation exists to provide.
**Done when** throughput rises with ordering preserved.

---

# Group G - Prove it at scale

### S26 - Synthetic load from headless clients
Plan 1's clients as load generators rather than test subjects.
**Done when** N clients can be run against one campaign server with N configurable.

### S27 - Soak at target player count
**Done when** a sustained run at the target holds tick time, bandwidth and paused fraction inside budget.

### S28 - Publish a supported ceiling
A measured, honest number for how many players the campaign server carries, with the first thing that breaks
beyond it named.
**Done when** the number exists and is reproducible.

---

---

# Group H - Beyond one campaign server

Only after Groups A-G. If per-client work still runs on the authority's game thread, adding machines does not
relieve it - it adds a hop to the same bottleneck.

## Why the world itself cannot be split

Recorded so it is not re-proposed. Spatial sharding - a server per region of Calradia - fails on the engine
rather than on the networking:

- **The campaign code assumes the whole world is resident.** Everything iterates globally:
  `Campaign.Current.Clans`, `Campaign.Current.Factions`, `CampaignObjectManager.MobileParties`. War checks walk
  every faction; military AI reads the whole map. A shard would need the entire world anyway - no saving, and a
  consistency problem that did not previously exist.
- **The state that matters is not spatial.** Diplomacy is faction-wide, kingdom decisions are global, armies
  form across regions, and there is one clock for the daily tick and the seasons.
- **Boundaries are crossed constantly.** Parties move continuously, and a battle near a boundary involves
  parties owned by two shards.

Doing it would mean rewriting TaleWorlds' campaign systems into something that resembles Bannerlord rather
than being it.

## What can be split: the connections

One authoritative simulation, plus edge servers that hold a replica, serve clients, and forward actions to the
authority.

The reason this is the right cut: single-player Bannerlord already simulates this world on one thread without
difficulty. **The scaling problem is not the simulation - it is the per-client coop overhead landing on the
same thread.** That is what an edge can take.

### S29 - Replica feed
An edge maintains a consistent replica from the authority, with a sequence the replica can be resumed from -
the same snapshot-plus-delta mechanism S10 needs for joins.
**Done when** an edge holds a replica that converges and can report its lag.

### S30 - Serve joins from the edge
The 5.5 MB baseline is built and sent by the edge, not the authority. This is the single largest per-client
cost and the one that most directly limits player count.
**Done when** a join costs the authority a constant amount regardless of client count.

### S31 - Fan-out from the edge
Per-client serialisation and delivery move off the authority. The authority sends once per edge.
**Done when** the authority's outbound cost scales with edges rather than clients.

### S32 - Action forwarding
Client to edge to authority, with ordering and identity preserved so the authority can still reject an action
on the same grounds it does today.
**Done when** an action applied via an edge is indistinguishable at the authority from a direct one.

### S33 - Lag budget
An edge is behind the authority by definition. Establish how far is acceptable, what a client sees while an
action is in flight, and how a rejected action is reconciled.
**Done when** the budget is measured and enforced, and exceeding it is reported.

### S34 - Edge failure and reconnection
An edge dying must cost its clients a reconnect, not the world.
**Done when** killing an edge under load moves its clients without affecting others.

### S35 - Measured ceiling with edges
The S28 number, recomputed with the edge topology, naming the next thing that breaks.
**Done when** the number exists and the new bottleneck is identified by measurement.

---

## Limits that remain

| Limit | Why |
|---|---|
| Single-threaded campaign | An engine property. This plan relieves pressure on the thread; it cannot remove the thread |
| The world does not shard | One clock, one diplomacy graph, parties crossing regions. Battles shard; the campaign does not |
| Vertical scaling only for the simulation | Single-core clock speed. Edges (Group H) scale the connections, never the simulation |
| The world cannot be split by region | The engine assumes a resident world and the important state is global. See Group H |
| Player count is bounded, not unbounded | The goal is to move the ceiling from roughly 10-20 to something higher and known - not to remove it |
