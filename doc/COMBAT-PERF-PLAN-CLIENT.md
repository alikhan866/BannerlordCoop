# Helping the CLIENT (Omar): a different problem from the host

`COMBAT-PERF-PLAN.md` was built from the HOST's profile - a peer spending 135.6 ms/s SENDING. Omar is
the mirror image, and the plan there does not transfer. This is his.

Measured from his 16-minute session on CoopFixes v0.1.4.0, 760 telemetry samples.

## His baseline

| Quantity | Omar (client) | My rig (host) | Note |
|----------|---------------|---------------|------|
| fps | **36.5** (frame 27.40 ms) | 43.5 | worse fps with HALF the agents |
| agents on field | 693 mean, 916 peak | ~1,400 | |
| **agents he OWNS** | **56.5 (8.2%)** | ~1,000 (70%) | he is almost purely a receiver |
| `senderMsPerSecond` | **28.4** | 135.6 | |
| `receiverApplyMsPerSecond` | **48.3** | 11.4 | **inverted from the host** |
| coop CPU total | 76.7 ms/s = **2.10 ms of a 27.40 ms frame (7.7%)** | 147.0 ms/s | |
| `wireBytesPerSecond` | 26.7 KB/s | 644 KB/s | bandwidth is not his problem |
| send/receive rate | **throttled to 15 Hz**, `reason=battle-performance` on 93% of samples | 20 Hz steady | |

Derived unit costs, which is what makes the plan below arithmetic rather than opinion:

- **0.503 ms/s per agent he OWNS** (28.4 / 56.5)
- **0.076 ms/s per agent he RECEIVES** (48.3 / 636)
- An owned agent costs him **6.6x** what a received one does.

**Ceiling:** deleting the entire mod takes him 36.5 -> 39.5 fps, **+8%**. Everything else in his frame
is the base game drawing 693-916 soldiers. So for Omar, fps is NOT where the win is - smoothness is.

---

## The plan

| # | Change | CPU effect | fps effect | Smoothness effect | Risk |
|---|--------|-----------|------------|-------------------|------|
| 1 | **Only receive agents that matter to him** (interest management, ~200 of 693) | **-33.1 ms/s** (48.3 -> 15.2) | **+1.3 fps** (36.5 -> 37.8) | unchanged | Medium - popping if the radius is wrong |
| 2 | **Spend that saving on RATE instead**: 200 agents at 30 Hz | -18 ms/s vs today (48.3 -> 30.4) | +0.7 fps | **2x update rate on everything he can see** | Medium - same as above |
| 3 | **Stop capping receive rate on fps** (no culling, just untie it) | +48.3 ms/s (15 Hz -> 30 Hz for all 693) | **-1.7 fps** (36.5 -> 34.8) | **2x update rate** | Low - it is a policy constant |
| 4 | **Lower his battle size / graphics** | n/a - this is the base game | **the only lever on the other 92%** | fewer agents to draw AND to receive | None - a settings change |
| 5 | ~~Give him more agents to own~~ | **+146 ms/s** if rebalanced to 50/50 | **-4.7 fps** (36.5 -> 31.8) | worse | **DO NOT** |

### Item 2 is the one to build

Culling to what he can actually see and *then* raising the rate gives him **double the update rate on
everything visible, while still using less CPU than he spends today**:

```
today      636 received agents @ 15 Hz  = 48.3 ms/s
proposed   200 received agents @ 30 Hz  = 30.4 ms/s     <- smoother AND 37% cheaper
```

That is the only item here that improves both numbers at once. Everything else trades one for the
other.

### Item 5 is a CORRECTION

`COMBAT-PERF-PLAN.md` lists ownership rebalancing (M6) as a no-risk win, and it is - **for the host**.
For Omar it is actively harmful. An owned agent costs him 0.503 ms/s; moving him from 56 agents to a
fair 346 would add **146 ms/s**, about 4 ms of a 27.4 ms frame, and cost him **4.7 fps**.

Rebalancing helps whoever is CPU-bound on sending. Omar is not. It should be driven by measured
sender load per peer, never by an even split.

---

## What is already helping him

Dead reckoning, shipped in `88a6bd710`, matters more at low update rates than at high ones - it fills
the gaps between updates by predicting where an agent is heading. At his throttled 15 Hz the gaps are
66 ms, which is exactly where it does the most work. His stutter would be worse without it.

## Order of work

1. **Item 4 first** - it is free, it is his actual bottleneck, and it costs a settings change.
2. **Item 3** - a constant, testable in one harness run, and it tells us whether smoothness at the
   cost of ~1.7 fps is a trade he likes. Make it a SETTING rather than a policy.
3. **Item 2** - the real fix, once item 3 has shown that update rate is what he is feeling.
4. **Never item 5** for a peer with his profile.

Everything is verified against the existing harness baselines: animation render >= 85%/91%, position
error mean/p90/max <= 0.28/0.72/2.48 m.
