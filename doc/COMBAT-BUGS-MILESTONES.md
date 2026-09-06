# Combat bug milestones

Rule for every item: **confirm it is real from logs first, fix only what is measurably broken.**
Driven unattended via `scratchpad/Auto-AnimTest.ps1` (~4 min per cycle, no clicks).

| # | Milestone | Status |
|---|-----------|--------|
| M0 | Clients launch windowed so the mouse is not trapped | **DONE - windows also placed side by side** |
| M1 | Melee attack animation invisible on puppets | **DONE - fixed** |
| M2 | Ranged/archer animation | **DONE - not a bug** |
| M3 | Block sound does not play on the client | **DONE - real sound event now used** |
| M4 | Client gets hit while too far from the enemy | **FIXED - dead reckoning, error down on every metric** |
| M5 | Upgrade screen shows a large negative number | **DONE - fixed, tested** |
| M6 | Kill gap: host owns 2.4x the agents (407 vs 173) | deferred, understood |
| M7 | Horse archer animation | **DONE - not a bug** |
| M8 | Horse legs not animating (ice skating) | **REAL, root cause found, fix attempted and reverted** |
| M9 | Melee cavalry, spear and sword | **DONE - not a bug** |

## M0 - windowed clients
`engine_config.txt` had `display_mode = 1` at 2560x1440. Full-Setup now forces
`display_mode = 0` at 1280x720 before every launch, clearing the read-only flag first
(the game rewrites the file on exit, so it must be set each time).

## M1 - melee animation invisible on puppets  (FIXED)
`SetActionChannel` was called with `ignorePriority: false` for a replicated melee swing, so the
engine arbitrated our write against the puppet's own action priority and the native controller -
which has no attack input for that agent - reclaimed the channel immediately. The call reported
success either way, which is why every delivery counter looked clean throughout.

Owner-versus-puppet, same swing at the same instant:

|            | before | after |
|------------|--------|-------|
| client wind-up | 1.5-4.6% | **83.7-85.8%** |
| client release | 37-42%   | **90.2-91.7%** |
| host wind-up   | 4.4%     | **84.9-86.7%** |
| host release   | 38.5%    | **90.1-90.3%** |

Guarded by `ReplicatedSwingPriorityTests`. Four earlier fixes aimed at the guard system all
failed; `guardCommandEffect` proved the guard was never the culprit (`REACHED=100%`,
`DESTROYED_WINDUP=0` over 90,517 commands).

## M2 - ranged animation  (NOT A BUG)
Measured with both parties fielding `imperial_veteran_archer`:

```
CLIENT  RANGED  ready 98.1%  reload 97.5%
HOST    RANGED  ready 98.5%  reload 97.2%
```

Ranged actions already render correctly - they evidently win priority arbitration where melee
swings lose it. No change made. Do not widen the melee-swing flag to cover ranged "for
consistency": it feeds five call sites including the guard logic.

## M3 - block sound  (CHAIN WORKS; SOUND CHOICE IS WRONG)
Instrumented end to end. Nothing is lost:

```
ALI  blockedSeen=1426 published=1426 SENT=1426 sendFailedNoIdentity=0
OMAR                                 RECEIVED=1428 PLAYED=1427   dropped=[victimUnknown:1]
SOUND_INDEX_INVALID=0  farFromVictim=0  indices=[1056: all]  weaponClasses=[2: all]
```

Every block is published, sent, received and played, at a valid index, within 5m of the blocker.

The defect is WHICH sound. Index 1056 is `ItemPhysicsSoundContainer.SoundCodePhysicsSwordlikeDefault` -
the noise a sword makes when DROPPED ON THE GROUND. Vanilla's block sound is produced natively, and
native never resolves a puppet's melee collision, so the client only ever gets this substitute. That
matches "sometimes my shield doesn't make a sound": blocks against a LOCAL attacker sound right,
blocks against a puppet get the item-physics clatter.

`CombatSoundContainer` holds the real impact sounds (Blunt/Cut/Pierce, High/Med/Low) but has no
shield-block entry. Choosing a replacement is an ear judgement, not something the logs can settle,
so nothing was changed. NOT FIXED - needs a decision on which sound.

## M3b - the actual sound fix
`Native/ModuleData` ships real weapon-on-shield events. The client now resolves and plays
`event:/mission/combat/impact/metal_weapon/wood_shield` (and the metal/wood/punch variants) via
`SoundEvent.GetEventIdFromString`, falling back to the old item-physics set only if the lookup
fails, so a bad id degrades to the previous sound rather than to silence.

```
REAL_BLOCK_EVENT=1207  fellBackToItemPhysics=0  SOUND_INDEX_INVALID=0  indices=[395: all]
```

Sound index moved from 1056 (dropped sword) to 395 (weapon on wooden shield) on every block, and
melee rendering was unaffected (85-87%). Judge by ear.

## M4 - hit from too far away  (NOT A HIT-RANGE BUG)
First measurement looked damning - blows from remote attackers at mean 4.7-5.6m against 1.2m for
local ones - but it was an artifact: `attacker.Position` is read when the blow is APPLIED, and a
routed blow is applied after the attacker has moved on.

The collision point settles it:

```
IMPACT_POINT_TO_VICTIM  local=1.57m  remote=1.77m  remoteFar4m=0/218 and 0/129
```

Hits always land in contact range. Rejecting "distant" blows would have thrown away legitimate
damage.

What IS real: when the blow is applied, the remote attacker stands 4.7-5.6m from where its own
weapon struck. The hit is right; the attacker is DRAWN IN THE WRONG PLACE at the moment it lands.
That is a position/timing problem - either the puppet's position lags, or routed damage is applied
late enough for the attacker to walk several metres. Distinguishing those two is the next step.

## M4 - conclusion: ARROWS, not a position bug
Splitting the measurement on `blow.IsMissile` settles it. Melee only:

```
ALI  localAttacker mean=1.03m [<2m:3850 <4m:166 <6m:0 <10m:0 10m+:0]
OMAR localAttacker mean=1.00m [<2m:1755 <4m:51  <6m:0 <10m:0 10m+:0]
     remoteAttacker mean=1.78m [<2m:7 <4m:1]
MISSILES_EXCLUDED local=45/19  remote=115/236
```

Zero melee blows past 6m out of 5,600+. Every "distant" hit was an arrow - the excluded missiles are
exactly the population that produced the earlier 4.7-6.15m averages.

Two wrong turns are recorded here deliberately, because both nearly became fixes:
- `attacker.Position` read when the blow is APPLIED is inflated for remote attackers simply because
  they walked on. The collision point (1.77m remote vs 1.57m local, 0/347 beyond 4m) shows the hit
  itself always lands in contact range. Rejecting "distant" blows would have discarded real damage.
- The deferral queue was suspected next. `APPLY_DELAY mean=26-29ms max=35ms` over 9-19 blows: far too
  small and too rare to move anyone metres.

If hits still feel like they come from nowhere, the thing to check is whether ARROWS render on the
client - being shot by an archer you cannot see feels identical.

## M5 - scoreboard upgrade count runs to -201406  (FIXED)
`TroopUpgradeTracker.CheckUpgradedCount` reports a NEGATIVE count to withdraw a troop type no longer
in the party roster:

```csharp
else if (_upgradedRegulars.TryGetValue((party, character), out value2) && value2 > 0)
    result = -value2;
```

It never clears `_upgradedRegulars` on that branch, so it returns the same negative every time it is
asked. Vanilla asks rarely; HitRewardHandler asks on EVERY hit reward and EVERY agent removal and
broadcasts each answer, and the client's `BattleObserver.TroopNumberChanged` ADDS it to a running
total. Thousands of repeats walked one party to -201406 while the other, whose roster still held its
troops, read a normal 239.

`ScoreboardUpgradeTotals` clamps the broadcast total at zero: the one real withdrawal still goes out,
the repeats after it send nothing. Covered by `ScoreboardUpgradeTotalsTests` (7 cases; the repeated-
withdrawal case fails against the old behaviour).

NOT live-reproduced: `scoreboard_state` reports party presence only, not the upgrade counts, so the
on-screen number could not be read back from a driven battle. Root cause is from vanilla's own
decompiled source rather than from a reproduction.

## M0b - the mouse was never about windowed mode
Windowed mode applied correctly (`display_mode = 0`, 1280x720) and the cursor was STILL trapped,
because a mission captures the mouse for mouse-look whatever the window mode. The actual cause:

```
pid=41276 rect=(640,360)-(1936,1119)
pid=51476 rect=(640,360)-(1936,1119)   <- identical
```

Both clients opened at the SAME centred rectangle, one exactly on top of the other. The focused one
captured the mouse and pinned it to that centre while the other sat hidden underneath. Full-Setup now
places them side by side at x=0 and x=1280 after launch.

## M4 - REOPENED, and it is real
The earlier "arrows, not a bug" answer was only half right. Arrows explained the 5m outliers in the
blow-distance data; they did not explain the report. The measurement that does:

```
POSITION ERROR between the two machines, same agent same instant (20,311 samples)
mean=0.41m  p50=0.20m  p90=1.08m  p99=1.57m  max=3.33m  >=2m: 84 (0.4%)
```

A puppet is DRAWN up to 3.3m from where its owner actually has it. Melee reach is about 2m, so an
enemy landing a perfectly legitimate hit can appear to strike from over 3m. The damage is right; the
drawing is wrong, which is why measuring hit distance found nothing.

Root cause, from `AgentPositionInterpolator`:

```csharp
if (agent.Position.Distance(pair.Value.Position) <= snapDistance)   // RiderSnapDistance = 6f
    MoveTowardTarget(agent, pair.Value);                            // WALKS toward it
else
    Teleport(agent, pair.Value);
```

Below six metres the puppet is told to WALK to where its owner was, using its own locomotion. While
the owner keeps moving the puppet is permanently chasing and never arrives, and because the drift
stays under the 6m snap threshold it is never corrected. The measured error is that steady-state
trailing.

NOT FIXED. The remedy is dead reckoning - aim at the target plus the owner's velocity times the
target's age - which needs the previous frame kept per agent. `AgentPositionInterpolator.cs` already
carries uncommitted position work, so this should not be changed underneath it without agreeing first.

## M7 / M9 - horse archers and melee cavalry  (NOT A BUG)
Khuzait lancers (spear) versus Imperial cataphracts (sword) versus Khuzait heavy horse archers:

```
CLIENT MELEE windup 81.7% release 89.0%   RANGED reload 97.6% release 65.4%
HOST   MELEE windup 86.3% release 89.7%
```

Mounted melee matches infantry, so the M1 fix covers lancers and cataphracts. Horse-archer reload
renders at 97.6%. `RANGED ready 0.0%` is zero SAMPLES, not zero rendering - mounted archers barely
pass through ReadyRanged.

## M8 - ice-skating horses  (REAL)
First attempt measured the mount's ACTION TYPE and reported 100% sliding on both machines - which was
meaningless. Horse legs animate through the engine's locomotion system, not action channels, so
`Other` is correct for a moving horse and both machines agreeing proves nothing.

The valid test compares how far the mount actually moved against the velocity the engine reports,
since the gait is chosen from velocity:

```
                moving   actual     engine     SLIDING (moving, engine says still)
HOST   LOCAL     1551   4.14 m/s   3.95 m/s     3.1%
HOST   PUPPET    7989   4.37 m/s   3.74 m/s     3.0%
CLIENT LOCAL     8406   4.36 m/s   4.15 m/s     2.5%
CLIENT PUPPET    1750   3.33 m/s   2.64 m/s     8.8%   <- 3.5x
```

Puppet horses on the client move while the engine believes them nearly stationary 8.8% of the time,
with reported velocity 21% below actual against ~5% for local horses. Intermittent, which matches
"it happens sometimes".

Root cause is `FollowMounted`, whose own comment says it eases the horse "toward the owner's reported
position WITHOUT a physical seek" - displacement with no locomotion, so no gait, so it slides.

NOT FIXED, and same caution as M4: it is the same file and the same in-flight work.

## M4 - FIXED with dead reckoning
A puppet below the 6m snap distance WALKS to its replicated position. While the owner keeps moving it
never arrives, and the drift never trips the snap, so it is never corrected. The fix keeps the previous
target frame per agent, derives the owner's velocity from two frames, and aims the puppet at
`position + velocity * frameAge` instead of at a stale position.

Two attempts, because the first regressed the half that matters:

```
                 mean   p50    p90    p99    max    >=2m
baseline         0.41   0.20   1.08   1.57   3.33   0.4%
lead, unclamped  0.30   0.10   0.76   2.76   6.08   2.4%   <- median better, TAIL WORSE
lead, clamped    0.28   0.10   0.72   1.00   2.48   0.1%   <- better everywhere
```

The unclamped version overshot whenever an agent stopped or turned, and 6.08m is past the snap
distance, so puppets started teleporting. Clamping the lead to at most ONE update period, and to 1m,
fixed the tail while keeping the gain. The tail is the half that makes a blow look like it landed from
out of reach, so it was never acceptable to trade it for a better median.

Cavalry benefits too: `mean 0.23 -> 0.17m, p90 0.54 -> 0.42m, max 2.21 -> 1.52m, >=2m 0%`.

Guarded by `PuppetDeadReckoningTests` (13 cases, all about the clamps: a dropped or reordered update
must never be multiplied into a fling). The 70 existing `MountedPuppetMovementTests` still pass.

## M8 - real, but NOT fixed
The signal reproduces: client puppet horses move while the engine reports them near-stationary
2.9-10.1% of the time against 2.5-3.4% for locally simulated ones, roughly 3x, across four runs.
Cause is `FollowMounted` calling `TeleportToPosition` EVERY tick (`MountedPositionEpsilon = 0.0001f`),
which moves the horse with no locomotion, so the engine plays the gait for standing still.

Raising the epsilon to 0.35m so small drift resolves through the horse's own legs was tried and
REVERTED: position error more than doubled at the median (p50 0.14 -> 0.32m, max 1.53 -> 2.05m) and
sliding did not improve (7.6% -> 8.9%). The per-tick correction is load-bearing.

The remaining option is to drive the mount through locomotion rather than teleporting it, which is a
real change to how horses move and carries a genuine risk of arcing and overshoot given a horse's
turning radius. Not attempted.

Also note the metric is population-sensitive: the local-versus-puppet split depends on which side owns
the mounts in a given run, and one run had CLIENT LOCAL at 11.3%. Trust the direction, not the digits.

---

# Campaign bugs, 2 Sep

| # | Milestone | Status |
|---|-----------|--------|
| M10 | Armies do not survive a load - they should come back the same | PARKED at your request; could not reproduce |
| M11 | Lords in my army never sell their prisoners until the army disbands | **FIXED - verified in game** |
| M12 | Discarding items after a battle loses the troops' XP | **FIXED - three faults, measured on the rig** |
| M13 | Lords and armies teleport on the campaign map | **FIXED - the correction is now delivered over frames** |

Rule unchanged: confirm from code and logs first, fix only what is measurably broken, and only call it
fixed once it has been VERIFIED IN GAME.

## M11 - lords in an army never sell their prisoners  (FIXED, VERIFIED IN GAME)

Prisoners are sold from `PartiesSellPrisonerCampaignBehavior.OnSettlementEntered` - an entry EVENT.
Recruitment, which does work for the same lords, runs from `HourlyTickParty`, which only asks "am I
inside a settlement" and never needed an event. That asymmetry is the whole bug.

`MobileParty`'s CurrentSettlement setter pushes the value down to every attached party:

```csharp
foreach (MobileParty attachedParty in _attachedParties)
    attachedParty.CurrentSettlement = value;
```

That PLACES the lords in the settlement and raises nothing. Vanilla makes up for it in
`EnterSettlementAction.ApplyInternal` by calling `ApplyForParty` on each attached party, which does
raise the events - but only for `MobileParty.MainParty`, which on a coop server is a dummy campaign
hero with no party at all. So neither player's army ever announces its lords, and they sit inside the
walls holding prisoners until the army disbands and they wander into a town under their own AI.

Fixed with a postfix on `EnterSettlementAction.ApplyForParty`: when a PLAYER party that leads an army
enters a settlement, raise the three settlement-entered events for its attached parties. Deliberately
raising events rather than forcing a second entry - the setter has already done the positioning, and
re-entering would fight the idempotency guard in the existing prefix, which is there for its own good
reasons.

Verified on a live server, army led by Alifreeze with two AI lords attached:

```
BEFORE   Honoratus 12,  Nicasor 9,  ARMY_PRISONER_TOTAL=85
AFTER    Honoratus  0,  Nicasor 0,  ARMY_PRISONER_TOTAL=64
[ArmyEnter] leader=Created_63468 attached=2 entered=0 announced=2
```

Alifreeze's own 64 prisoners correctly remain - the sell path skips player parties, who sell manually.

Three test-setup mistakes are recorded because each one produced a convincing false negative:
- adding a party to an army makes it a MEMBER, not ATTACHED; only attached parties enter with the
  leader, so the first run measured nothing (`attach_all` now exists)
- the lords borrowed for the test were Vlandian and the town was Qin's, and a lord will not sell
  prisoners in an enemy town (`!MapFaction.IsAtWarWith`) - peace had to be made first
- entry is idempotent, so re-entering an army already inside raises no event (`leave_settlement` now
  exists)

New debug commands: `coop.debug.army.prisoners`, `give_prisoners`, `attach_all`, `enter_settlement`,
`leave_settlement`.

---

## M12 - donated loot pays no troop XP  (FIXED)

Reported as "when we win a battle we can discard equipment for unit experience, but even if I discard all of
them I don't see my units gain any experience". Driven end to end on the rig with
`Army-Test.ps1 -LootWalk`, which fights a 100 v 100, then works the winner's post-battle screens with no
mouse: the encounter menu's option, Done on the troops and prisoners screen, donate everything on the loot
screen, Done. Three separate faults, each measured.

**1. The XP the player earned never left their machine.** Vanilla counts donations in
`InventoryLogic.XpGainFromDonations` while the loot screen is open and applies that one number in
`DoneLogic`. Coop replaces `DoneLogic` with a prefix that sends the screen to the server, and the number was
not among the things it sent; the server recomputed XP from the items LEFT on the pile instead. It now
travels in `TradeAttempted` / `CompleteTrade`, and the server grants exactly it.

A client that reports its own reward has to be bounded, so the server prices what that party could actually
have donated - the item lines of the spoils it was offered, plus what it was already carrying - through the
same discard model that holds the Steward perk gates, and grants the smaller of the two
(`TradeHandler.CapDonationXp`). No perks, no XP, exactly as in single player.

**2. In a two-player battle the winner's spoils offer was erased before it could be answered.** The results
handler forgot every outstanding offer for a map event before registering each player's, so the second
player's offer wiped the first player's. Live on 5 Sep: the winner's 208-line offer was refused
`UnknownOffer` 54 seconds after it was made, and the 25 troops and every item on it were never applied. Only
that party's own earlier offers are forgotten now (`BattleLootOfferRegistry.ForgetPartyOffers`).

**3. The XP arrived and stayed invisible.** A `TroopRoster` hands out its rows from a list it rebuilds only
when its version changes, and writing XP does not change the version. So the roster held the new XP and every
row view of it - the party screen among them - kept showing the old. It hides during a battle, where
casualties change counts and the rows refresh anyway, and shows up afterwards, when nothing else changes.
Proven offline against the real engine in `TroopRosterXpVisibilityTests`; fixed by bumping the version after
the coop XP writes, the same way the perk handlers already do.

### What the runs show

| Run | What happened |
|---|---|
| `2026-09-05-2134-army-100-lootwalk2` (before) | Loot screen showed 40,170 XP to donate; nothing reached the party; the winner's offer was refused `UnknownOffer` |
| `2026-09-06-0150-army-100-recruit-lootwalk` | Offer accepted; server granted 47,125 XP; roster unchanged - every troop was already at its upgrade threshold (`partyCanAbsorb=0`) |
| `2026-09-06-0207-army-100-lootwalk-fixed` | Winner given a roster with room first (capacity 52,000): donated loot paid **0 -> 40,170 XP**, the same on the server and on the owning client |

Two things worth knowing that are NOT bugs. Vanilla only gives XP to troops that can still use it: after a
battle every survivor sits exactly on its upgrade threshold (88 recruits at 300 XP each, party capacity zero),
and XP offered to a full party is dropped. And a player's own troops are the only ones that see their XP -
the other player's copy of that roster carries none, by design.

Guards: `TradeDonationXpTests` (the cap: offered spoils and carried items price it, no perks means nothing,
a screen that cannot donate grants nothing), `BattleLootOfferRegistryTests` (one answerable offer per party,
an answered offer still bounds the cap), `TroopRosterXpVisibilityTests` (the row view and the version).

---

## M13 - lords and armies teleport on the campaign map  (FIXED)

Reported as "on the campaign map sometimes lords and armies seem to teleport to siege a certain city, or to
catch me". Measured with a new rig (`Map-Drift-Test.ps1`) that samples every party on the server and both
clients on one wall clock and joins them by party id (`analyze_drift.py`).

Every machine simulates the AI parties' movement itself - only their decisions are replicated - so the copies
drift apart. Over five campaign-map minutes at 18 campaign hours a minute, across about 300 lord parties:

| | ali | omar |
|---|---|---|
| Drift of a client's copy from the server's, p50 / p90 / p99 / max (map units) | 1.4 / 12.1 / 33.8 / 104.7 | 1.1 / 10.2 / 27.8 / 101.1 |
| Parties ever more than 8 units out | 215 of 328 | 137 of 328 |
| Five-second intervals containing a snap | 214 of 16,142 | 151 of 16,156 |
| Snap size p50 / max (map units) | 12.4 / 96.4 | 12.0 / 100.6 |

The server re-states a party's position once its own copy has moved 8 units since it last reported, and the
client took that in a single assignment - the copy was in one place, and in the next frame it was somewhere
else. The analyzer names them: "moved 21.7 units, its speed allows 9.7, drift 17.2 -> 2.5". They arrive in
groups, because an army's parties are corrected together: eight parties of one army moved 18 to 20 units in
the same instant, which is what "an army teleported onto me" is.

The correction is right and is still obeyed in full; only its delivery changed. The difference is held as a
residual and bled into the party's position each frame, closing in about a third of a second while the party's
own AI keeps walking it (`PartyPositionSmoothing`). Nothing new goes on the wire. A correction beyond 40 units
is still assigned at once - that size is a settlement exit, an army attaching or a spawn, and sliding a party
across it would draw a journey that never happened - and so is a correction to the player's own party or to one
standing in a settlement.

### Before and after, one rig, corrections assigned then delivered over frames

| Measured on the client | Assigned (before) | Over frames (after) |
|---|---|---|
| Distance a party is moved per frame by a drift correction, mean | 2.58 map units | **0.24** |
| Largest single-frame move from a drift correction | 24.7 | **11.1** |
| Drift of the client's copy from the server's, p50 | 0.61 | **0.19** |
| Drift p90 | 4.98 | 4.76 |
| Drift p99 / max | 14.0 / 46.8 | 21.3 / 74.0 |
| Corrections the client had to apply | 4,452 | 4,073 |

The largest smoothed step is a fifth of the gap by construction, so under 9 units for the biggest correction
that is smoothed at all; the 11.1 that remains is one of the cases the fix deliberately assigns. The tail (p99)
is higher because a large correction is now in flight during some samples rather than already applied - the
party is a little behind for a third of a second instead of arriving in one frame.

### Three wrong turns, all found by measuring

The campaign tick constants read as zero outside the running game, so the dump asks the game for
`CampaignTime.Now.ToHours` and the analyzer uses that. Neither campaign tick is a frame: `Campaign.Tick` runs
about once a second and `Campaign.RealTick` about twice, and draining on either stretched a third-of-a-second
correction over tens of seconds, which left copies lagging and drove the 90th-percentile drift from 5 map units
to 12. The map's own `OnMapModeTick` runs at about 50 Hz, and the drain now measures and reports its own rate.

And the drain itself threw on every frame for a whole run: writing a value back into a dictionary while
enumerating it is not allowed on this runtime, and the guard around the drain reported it ONCE and swallowed it
silently after that - 423 corrections outstanding at a time, each living 47 seconds. The loop now steps a
snapshot of the keys, and a repeated failure is reported every 30 seconds with a count rather than once.
Guards: `PartyPositionSmoothingTests` (what is assigned, what is slid, the drain never overshoots, terminates,
and takes the same time at any frame rate).
