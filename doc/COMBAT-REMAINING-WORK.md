# Combat work: what is fixed, what is not, and what is still improvable

Companion to `COMBAT-BUGS-MILESTONES.md`, which holds the root causes and the wrong turns.
Every number here is measured by `scratchpad/Auto-AnimTest.ps1` (unattended, ~4 min per run) and
`analyze_timeline.py`, comparing the same agent on both machines at the same instant on a shared clock.

**Nothing in this work is committed.**

---

## 1. Fixed, with headroom left

| # | What | Before | After | Change | Cost (network / CPU) | Headroom left |
|---|------|--------|-------|--------|----------------------|---------------|
| M1 | Client renders owner's **wind-up** | 1.5% | **84.6%** | +83pp (56x) | **none** - one bool on an existing call, no wire change | 15% still missed: defend pose 9-12%, block recoil 4-5% |
| M1 | Client renders owner's **release** | 41.8% | **91.0%** | +49pp (2.2x) | **none** | 9% still missed |
| M1 | Host renders client's wind-up | 4.4% | **85.6%** | +81pp (19x) | **none** | same 15% |
| M4 | Puppet position error, **mean** | 0.41m | **0.28m** | **-32%** | network **0%**; CPU **+0.05 to +0.12% of a second** (0.52-1.16 ms/s, 0.39-0.43 us per call) = **+1.2% of coop CPU**, measured | trailing not eliminated, only reduced |
| M4 | Puppet position error, **p50** | 0.20m | **0.10m** | **-50%** | same as above: **+0.05-0.12% of a second** | |
| M4 | Puppet position error, **p90** | 1.08m | **0.72m** | **-33%** | same | still 0.72m against ~2m melee reach |
| M4 | Puppet position error, **p99** | 1.57m | **1.00m** | **-36%** | same | |
| M4 | Puppet position error, **max** | 3.33m | **2.48m** | **-26%** | same | worst case can still exceed weapon reach |
| M4 | Samples **>= 2m** off | 0.4% | **0.1%** | **-75%** | same | |
| M4 | Cavalry position error, mean / p90 / max | 0.23 / 0.54 / 2.21m | **0.17 / 0.42 / 1.52m** | **-26% / -22% / -31%** | same | best case measured; >=2m is 0% |
| M3 | Block sound event | `SoundCodePhysicsSwordlikeDefault` (a sword dropped on the ground) | `impact/metal_weapon/wood_shield` | 100% of blocks, 0 fallbacks | **none** - ids resolved ONCE and cached; the per-block work is the same switch as before, no wire change | not judged by ear yet |
| M5 | Scoreboard upgrade column | -201406 | clamped at 0 | fixed | **network REDUCED** - it stops thousands of repeated broadcasts per battle. One dict lookup per upgrade event | **never reproduced live** - `scoreboard_state` does not expose upgrade counts |
| M0 | Client windows | both at (640,360), stacked | (0,0) and (1280,0) | fixed | **none** at runtime - launch-time window placement only | three separate causes; see milestones |

---

## 1b. What the fixes cost - measured, in percentages

Measured by timing the added code paths directly (`hotPathCost`) over a 131-second cavalry battle with
~1,400 agents, alongside `coop.debug.movement.state` for the surrounding load.

### The two code paths this session added to a hot loop

| Path | Calls/sec | Time per call | Time per second | **% of one second** | **% of the coop CPU already being spent** |
|------|-----------|---------------|-----------------|---------------------|-------------------------------------------|
| Dead reckoning (M4), busier peer | 2,964 | 0.39 us | 1.158 ms | **0.116%** | **0.79%** |
| Dead reckoning (M4), quieter peer | 1,220 | 0.43 us | 0.523 ms | **0.052%** | **0.60%** |
| Damage attribution (diagnostic) | 29-36 | **14-19 us** | 0.505-0.554 ms | **0.050-0.055%** | **0.4-0.6%** |

"Coop CPU already being spent" is `senderMsPerSecond + receiverApplyMsPerSecond` on the same peer:
147 ms/s on the busier side, 87 ms/s on the quieter one - i.e. the mod already uses 8.7-14.7% of every
second before any of this work.

### Overall totals

| | Absolute | Percentage |
|---|---|---|
| **CPU added, busier peer** | +1.71 ms per second (1.158 + 0.554) | **+0.17% of wall clock**, **+1.2% of coop CPU** |
| **CPU added, quieter peer** | +1.03 ms per second (0.523 + 0.505) | **+0.10% of wall clock**, **+1.2% of coop CPU** |
| **Per frame at 43 fps** | +0.04 ms of a 23.3 ms frame | **+0.17% of the frame budget** |
| **Expected fps effect** | 43.5 -> ~43.4 | **about -0.07 fps, unmeasurable** |
| **Network added** | **0 bytes** | **0%** - no wire format changed by any fix |
| **Network removed (M5)** | thousands of repeated scoreboard broadcasts per battle | reduction, **not quantified** |

For scale, the measured traffic is 644 KB/s on the busier peer and 213 KB/s on the quieter one; none
of it changed.

### Why there is no measured before/after for total CPU and network

An A/B against a build with dead reckoning disabled was run and **thrown away**: `wireBytesPerSecond`
differed by 31-36% between the two runs, for a change that touches no wire format at all. The
run-to-run variance (battle phase, 1,365 vs 1,469 agents, which peer owns what) is far larger than a
1.7 ms/s effect, so no honest figure could be taken from it. Timing the paths directly, as above,
avoids the problem entirely - which is why these numbers are per-path rather than whole-process.

### Per-change summary

| Change | Network | CPU |
|--------|---------|-----|
| M1 `ignorePriority` for melee swings | 0% - no wire change | 0% - one boolean on a call that already happened |
| M3 real block sound | 0% - same message | ~0% - event ids resolved once and cached; the per-block work is the switch that was already there |
| M4 dead reckoning | **0%** - derived from frames already sent | **+0.05 to +0.12% of a second** in DEBUG. In RELEASE the cost timing is compiled out, so only the lead itself remains |
| M5 scoreboard clamp | **negative - saves traffic** | ~0% - one dict lookup per upgrade event |
| Timeline position/mount fields | 0% on the wire | diagnostic only, and only while recording. The snapshot DUMP grew from ~470KB toward the 1MB cap |

### Costs that are real and not yet paid for

| Item | Measured cost | Status |
|------|---------------|--------|
| `DamageAttributionPatch` | 14-19 us per blow, taking a **lock on the agent registry** | **GATED.** The whole class is now inside `#if DEBUG`, so the Harmony detour does not exist in a release build |
| `RegisterBlowPatch` (installed this session) | A Harmony detour on every `Agent.RegisterBlow`, not separately timed | `SUPPRESSED=0` every run, so no behaviour change, but the detour is new |
| Diagnostic call sites | Were unconditional | **GATED.** Dead-reckoning cost timing (~3,000 calls/sec), shield-impact counters (6 sites), and the damage-suppression counter are all `#if DEBUG` now. Verified by a clean **Release** build |
| Timeline snapshot size | 28 agents x 800 samples plus position and mount fields | Approaching the 1MB live-test cap. Reduce `TrackedAgents` before adding fields |

---

## 1c. Packet size: what a movement update costs on the wire

Source note: these are NOT from this session's combat work, which changed no wire format at all. They
come from the movement/position quantisation already in the tree, and they are sourced two ways -
the "before" figures from `AgentDataWireSizeTests` and `MovementQuantizer`, the "after" figures by
RUNNING those tests and reading what they print.

### Per agent, per movement update

| What | Before | After | Reduction |
|------|--------|-------|-----------|
| Foot agent, moving | ~57 bytes | **31 bytes** | **-46%** |
| Foot agent, stationary | ~57 bytes | **18 bytes** | **-68%** |
| Worst case (every component negative and extreme) | ~57 bytes | **32 bytes** | **-44%** |
| Regression ceiling enforced by the test | - | 34 bytes | trips if a full-precision Vec3 returns |

Where the old 57 bytes went: a `Vec3` surrogate cost 15 bytes and a `Vec2` cost 11, so four vectors
plus a float accounted for **52 of the 57**. Direction components are now a signed 16-bit fraction -
about 3.05e-5 per step, roughly 280x finer than the smallest direction change the sender will even
transmit (`DirectionDeltaThresholdSq`, 0.57 degrees). `DataFormat.FixedSize` is what keeps the worst
case at 32 rather than letting an agent facing away from the origin cost more than one facing it.

### Field measurement: Omar's client log, 28 Aug (`Coop_client.log`)

3,597 `[MovementRate]` samples, 3,043 of them with the peer actually simulating (`localAgents >= 20`).
This is a real session, not a driven test, so the rates and agent counts move around.

| Metric | mean | p50 | p90 | max |
|--------|------|-----|-----|-----|
| `wireBytesPerSecond` | 61,278 | 27,733 | 129,355 | **627,204** |
| `localAgents` (agents this peer OWNS and sends) | 61 | 26 | 128 | 284 |
| `agents` (total on the field) | 599 | 570 | 906 | 1,090 |
| `bulkHz` (send rate) | 24.6 | 20 | 30 | 60 |
| `senderMsPerSecond` | 39.3 | 31.4 | 60.6 | **177.7** |
| `receiverApplyMsPerSecond` | 31.4 | 31.0 | 48.1 | **634.5** |
| `receiverQueueMs` | 26.1 | 23.5 | 29.8 | **1,060.8** |
| `fps` | 50.7 | 47.0 | 71.0 | 156.3 |

Busiest 200 samples, when the peer owned 284 agents - the closest thing in the log to a heavy battle:

| Metric | mean | p50 | max |
|--------|------|-----|-----|
| `wireBytesPerSecond` | 352,791 | 406,698 | 627,204 |
| `senderMsPerSecond` | 125.0 | 129.3 | 177.7 |
| `fps` | 70.3 | 71.3 | 85.5 |
| `receiverQueueMs` | 17.6 | 17.1 | 26.9 |

### Per agent per update, and why the two figures differ

| Source | Bytes per owned agent per update | What it measures |
|--------|----------------------------------|------------------|
| `AgentDataWireSizeTests`, before quantisation | ~57 | protobuf payload only |
| `AgentDataWireSizeTests`, now (test run) | **31** moving, **18** still | protobuf payload only |
| Omar's log, 28 Aug, whole session | **43.4** mean, 45.5 p50 | `wireBytesPerSecond / localAgents / bulkHz` |
| Omar's log, busiest 200 samples | **40.7** mean, 47.9 p50 | as above |
| This session's cavalry battle | **~31-32** (644 KB/s, ~1,000 owned, 20 Hz) | as above, ownership from the census |

**The log figure is HIGHER than 31 and that is expected, not a contradiction.** The test measures the
protobuf payload of one `AgentData`; the log figure is derived from TOTAL wire traffic, which also
carries LiteNetLib framing, batching overhead and every non-movement message. Movement is 97% of the
bytes, not 100%. So 43 bytes of wire per agent per update is consistent with a 31-byte payload plus
framing and the other 3%.

The two are only safely compared with each other when derived the same way, which is why the last two
rows matter: **43.4 bytes on 28 Aug against ~31-32 today**, both from `wireBytesPerSecond / owned /
rate. That is roughly a 26-28% drop - but the sessions differ in agent count, send rate and battle
shape, so treat it as directional rather than as a clean before/after.

### What the log shows that a driven test does not

- `receiverQueueMs` peaked at **1,060 ms** and `receiverApplyMsPerSecond` at **634 ms** - a real stall
  where the client was over a second behind. Today's controlled runs never exceeded 39 ms of queue.
- `senderMsPerSecond` reached **177.7 ms**, meaning nearly 18% of every second was spent sending, from
  a peer owning only 284 agents.
- `bulkHz` ranged 10 to 60. The rate controller was actively throttling, so a per-update figure from
  this log is an average over a moving send rate.

### Aggregate, derived and then cross-checked against a live measurement

Movement is **97% of the mission mesh by bytes** - measured at 7.3 MB in ten seconds against 58 KB for
everything else combined.

| | Agents owned | Rate | Bytes/agent | Predicted wire | Source |
|---|---|---|---|---|---|
| Before quantisation | 638 | 20 Hz | 57 | **~727 KB/s** | derived; matches the 7.3 MB/10s figure recorded in the test file |
| After, moving | 638 | 20 Hz | 31 | **~396 KB/s** | derived |
| After, stationary | 638 | 20 Hz | 18 | **~230 KB/s** | derived |
| **Measured this session** | ~1,000 (busier peer) | 20 Hz | 31 | **644 KB/s observed** vs ~620 KB/s predicted | `coop.debug.movement.state` |

The last row is the useful one: the observed 644 KB/s sits within about 4% of what 31 bytes per agent
predicts, which is independent confirmation that the quantised size is what is actually going out.

**Combat work this session added 0 bytes to any of this.** No packet gained a field; the melee fix is
a local flag, dead reckoning consumes frames already being sent, and the scoreboard clamp REMOVES
messages.

---

## 2. NOT fixed

| # | What | Before | After | Change | Expected cost if fixed | Why it is still open |
|---|------|--------|-------|--------|------------------------|----------------------|
| M8 | Puppet horses sliding (client) | 7.6-10.1% | **7.6-10.1%** | **0%** | network zero; CPU likely LOWER - driving a mount through locomotion replaces a per-tick `TeleportToPosition` | Fix attempted and reverted - it made things worse |
| M8 | ...against local horses | 2.5-3.4% | 2.5-3.4% | - | - | The ~3x gap is the bug |
| M6 | Agent ownership split host/client | 407 / 173 of 580 | **407 / 173** | **0%** | **rebalancing MOVES load rather than removing it** - the client would gain sender cost and the host would shed it. Today the busier side spends 135.6 ms/s sending against 50.5 | Never attempted |
| - | Damage feedback round trip | unmeasured | unmeasured | - | one counter; negligible | Suspected, never measured |
| - | 16 dormant Harmony patches | 16 | 16 | 0% | each one installed adds a detour on its target method | Recorded, not reviewed |

### M8 - ice-skating horses
`FollowMounted` calls `TeleportToPosition` every tick (`MountedPositionEpsilon = 0.0001f`), moving the
horse with no locomotion, so the engine plays the gait for standing still.

Attempted: raise the epsilon to 0.35m so small drift resolves through the horse's own legs.
**Reverted** - position error more than doubled at the median (p50 0.14 -> 0.32m, max 1.53 -> 2.05m)
and sliding did not improve (7.6% -> 8.9%). The per-tick correction is load-bearing.

Remaining option: drive the mount through locomotion instead of teleporting it. That is a real change
to how horses move, with arcing and overshoot risk from a horse's turning radius. Cavalry position
error is currently the best measured anywhere (max 1.52m, >=2m at 0%) and is easy to wreck.

Caveat: the metric is population-sensitive - it depends on which side owns the mounts in a given run,
and one run put CLIENT LOCAL at 11.3%. Trust the ~3x direction, not the digits.

### M6 - agent ownership imbalance
The host owns 407 of 580 spawned agents against the client's 173 (70/30). Blows per attacker are
identical (11.5 vs 10.9), so it is not a combat difference - the host simply simulates 2.4x more
soldiers. It reads as a kill gap (the scoreboard showed 961 vs 1198, about 25%, not the "double" it
looks like) and it is also an uneven CPU and network load. Never investigated.

### Damage feedback round trip - suspected only
For a puppet victim, `Agent.RegisterBlow` is never called locally (measured: `SUPPRESSED=0` every
run). The blow is routed to the owner, applied there, and the health change replicates back. Damage
itself is NOT low - the player's own 22 blows averaged **133 against puppet cavalry versus 56 against
local cavalry**, with the speed bonus clearly applied (2.4-8.1, tracking attacker speed) - but the
health drop is a round trip behind the swing, which can feel like a weak hit mid-charge.

Unmeasured. The measurement would be: time from the blow registering to the victim's health changing,
split by victim ownership.

---

## 3. Checked and found NOT to be bugs

| # | What | Measured | Verdict |
|---|------|----------|---------|
| M2 | Archer / ranged animation | ready 96-99%, reload 97.6% | Already correct; ranged wins priority arbitration where melee lost it |
| M7 | Horse archers | reload 97.6% | Same as above |
| M9 | Melee cavalry, spear and sword | wind-up 81.7-86.3%, release 89-90% | Matches infantry; the M1 fix covers lancers and cataphracts |
| M4a | Melee hits landing from out of range | 0 of 5,600+ melee blows past 6m; impact point 1.77m vs 1.57m | Damage lands in contact range. The *drawing* was wrong, not the hit - that is M4 |
| - | Damage against puppet cavalry | player blows: 133 puppet vs 56 local | Puppet victims take MORE, not less |

---

## 4. Test debt

- `PartyBehaviorTest.ShouldApplyAuthoritativePosition` fails, and did so before this work started. It
  belongs to the separate uncommitted position work, not to anything here.
- `AgentPositionInterpolator.cs` already carried uncommitted position work before dead reckoning was
  added, so that file's diff now mixes both.
- The diagnostic counter classes are not `#if DEBUG`-gated, only the command layer is. Cheap, but
  worth a decision before shipping.
- M5's fix is covered by unit tests but was never seen working on screen.

## 5. Suggested order for the next session

1. **M8**, driving mounts through locomotion - highest visible payoff, and the measurement loop for
   both sliding and position error already exists so a regression shows in one run.
2. **Damage feedback round trip** - one counter, and it either explains the "hits feel weak" report or
   removes it from the list.
3. **The last 15% of wind-up** - the defend pose is still winning 9-12% of the time.
4. **M6 ownership split** - a fairness and load question rather than a visible bug.
