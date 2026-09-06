# Client performance in battles: what to do, what it is worth

**These are PROJECTIONS, not measurements.** The baseline is measured; the savings are arithmetic on
top of it. Every figure that says "saved" is an estimate and is marked with its confidence. The
measured facts are in `COMBAT-REMAINING-WORK.md`.

---

## Measured baseline - the busier peer, which is the one that bottlenecks

| Quantity | Value | Source |
|----------|-------|--------|
| fps | **43.5** (frame = 22.99 ms) | `coop.debug.movement.state`, 5 samples |
| `senderMsPerSecond` | **135.6 ms/s** = 13.6% of a second | same |
| `receiverApplyMsPerSecond` | 11.4 ms/s | same |
| Coop CPU total | **147.0 ms/s** = 14.7% of a second | sender + receiver |
| `wireBytesPerSecond` | **644 KB/s** (budget 52 MB/s) | same |
| Action-sender visits | **48,130 per second** | 6,304,757 over 131 s |
| ...of which mounts, discarded | **54%** (3,427,537) | `actionSend` counters |
| ...of which nothing changed | **43%** (2,725,014) | same |
| ...producing a message | **1.1%** (66,529 = 508/s) | same |
| Agents owned, host vs client | **407 / 173** (70/30) | ownership census |

**The absolute ceiling on all of this:** removing 100% of coop CPU (147 ms/s) takes the frame from
22.99 ms to 19.61 ms - **43.5 to 51.0 fps, +17%**. Nothing below can beat that, because the rest of
the frame is the base game drawing 570-1,090 agents.

---

## The one assumption everything rests on

The share of `senderMsPerSecond` that is the action-sender SCAN is **not measured**. Movement has a
separate send path. Every CPU figure below scales linearly with it, so it is shown three ways:

| Scan share of the 135.6 ms/s | Scan cost |
|------------------------------|-----------|
| pessimistic 15% | 20.3 ms/s |
| **base case 30%** | **40.7 ms/s** |
| optimistic 50% | 67.8 ms/s |

**Measure this first.** `HotPathCostDiagnostics` already exists; timing the scan, the serialise and
the send separately is a couple of hours and turns every row below from an estimate into a number.

---

## The plan

| # | Change | CPU saved (base case) | Network saved | fps gain | Gameplay risk | Confidence |
|---|--------|----------------------|---------------|----------|---------------|------------|
| 1 | **Stop visiting mounts** - 54% of visits are mounts that are ALREADY discarded after being walked | **22.0 ms/s** (2.2% of a second) <br> range 11.0-36.6 | 0 - byte-identical output | **+2.3%** (43.5 -> 44.5) <br> range +1.1 to +3.8% | **None.** Those visits already sent nothing | **High** - the 54% is counted, not guessed |
| 2 | **Dirty-tracking instead of polling** for the remaining 43% that report no change | **17.5 ms/s** (1.8%) <br> range 8.7-29.2 | 0 - same messages | **+1.8%** (43.5 -> 44.3) <br> range +0.9 to +3.0% | **Low.** A missed dirty flag drops an update - the animation-render metric catches it | Medium |
| 3 | **Rebalance agent ownership** 407/173 -> ~290/290 | **42.6 ms/s** (4.3%) on the busier peer | **-33%** on the busier peer (644 -> ~430 KB/s) | **+4.4%** (43.5 -> 45.4) | **None** to simulation - same agents, different owner | High - independent of the assumption above |
| 4 | **Fix the 1,060 ms receive stall** | 0 steady state; up to **634 ms/s** during the spike | 0 | No fps change; removes a **1 second freeze** | **Negative risk** - it is a regression TODAY | Low - only reproduces in real sessions |
| 5 | **Interest management** - send only agents relevant to each peer (~60% fewer) | **~81 ms/s** standalone, **~40 ms/s** stacked after 1-3 | **-60%** (644 -> ~260 KB/s) | **+4.3%** stacked | **REAL.** Agents popping in, being hit by something unseen | Low - needs prototyping |

### Totals

| Stack | CPU saved | Network saved | fps | Gain | Risk |
|-------|-----------|---------------|-----|------|------|
| **Items 1+2** (scan waste only) | 39.7 ms/s <br> range 19.8-66.2 | 0 | 43.5 -> **45.3** | **+4.1%** <br> range +2.1 to +7.1% | none / low |
| **Items 1+2+3 - the safe stack** | **82.3 ms/s** (8.2% of a second) | **-33%** busier peer | 43.5 -> **47.4** | **+9.0%** | **none to gameplay** |
| **Items 1+2+3+5** | ~122 ms/s (12.2%) | **-60%** | 43.5 -> **49.6** | **+14%** | item 5 carries real risk |
| *Theoretical ceiling* - all coop CPU removed | 147.0 ms/s | - | 43.5 -> **51.0** | *+17%* | - |

**The safe stack gets ~53% of the way to the theoretical ceiling with no gameplay risk at all.**
Item 5 buys another 5 points for the only genuine regression risk in the list.

---

## Caveats worth reading before trusting any of this

- **Item 3 moves load, it does not remove it.** The busier peer gains 4.4%; the quieter peer loses
  about the same. It is right because the loaded peer is the one dropping frames, but system-wide CPU
  is unchanged.
- **fps does not scale linearly with saved milliseconds** if the thread is ever GPU-bound or waiting.
  These conversions assume the saved time turns into frames, which is the optimistic reading.
- **Bandwidth is not a constraint today**: 644 KB/s against a 52 MB/s budget. Item 5's network saving
  is nice but is not what fixes anything - its value is CPU and the message count.
- **Items 1 and 2 are the same scan.** Doing both does not give 54% + 43% of the sender - it gives
  97.6% of the SCAN, which is what the combined row shows.
- The base game drawing agents dominates the frame. Every row here competes for the ~15% of the
  second that coop owns.

## Order of work

1. **Attribute `senderMsPerSecond`** (hours). Turns this whole document from estimates into numbers.
2. **Item 1** (small). Highest confidence, zero risk, roughly half the scan.
3. **Item 3** (medium). Independent of the assumption, helps the peer that is actually struggling.
4. **Item 2** (medium). Finishes the scan.
5. **Item 4** when real-session logs are available.
6. **Item 5** last, if the first four have not been enough.

Every step is verified against the harness baselines - animation render >= 85% / 91%, position error
mean/p90/max <= 0.28 / 0.72 / 2.48 m - so a gameplay regression shows up in one 4-minute run.
