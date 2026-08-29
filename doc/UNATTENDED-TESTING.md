# Unattended testing

How to leave the rig running overnight and read the result in the morning.

```powershell
cd "C:\Users\USER\Desktop\Bannerlord co op\Rig"
.\selfcheck-all.ps1                       # the rig's own rules, ~20s, no rig needed
.\Supervise-Rig.ps1 -MaxHours 8 -Work { & "C:\Users\USER\Desktop\Bannerlord co op\Rig\Work-Cycle.ps1" }
```

In the morning:

```powershell
.\Report-Night.ps1
```

---

## Why this shape

Three things had to be true before a night could produce anything, and none of them were.

**The rig had to stop needing a person.** Every failure up to this point ended with somebody noticing and
restarting by hand, which caps a run at however long it takes to hit the first wedge. A run started at midnight
ended at 00:20 and reported nothing.

**There had to be an oracle that scales.** Every check in the rig before this was an assertion written by hand:
it finds the one bug it was written for and is silent about everything else. A hundred bugs would have meant a
hundred assertions, each of which has to be thought of first.

**The result had to survive until morning.** Findings in a terminal buffer are findings nobody reads.

## The pieces

| Piece | What it does |
|---|---|
| `Supervisor.psm1` / `Supervise-Rig.ps1` | Watches every component, captures faults from the live processes, recovers what broke, journals it |
| `WorldDigestCommands.cs` (`coop.debug.world.digest`) | Emits a comparable digest of world state on either side |
| `Divergence.psm1` / `Sweep-Divergence.ps1` | Compares the two sides and records what disagrees |
| `Drive-Battle.ps1` | Drives one two-owner field battle and leaves the rig where it started |
| `Work-Cycle.ps1` | Sweeps every cycle; drives a battle every sixth and sweeps again straight after |
| `Report-Night.ps1` | The morning read |
| `*-selfcheck.ps1`, `selfcheck-all.ps1` | The rig's own rules, checked without a rig |

## The oracle

Instead of asserting a fact somebody predicted, ask both sides to describe the same world and report where they
disagree. If the server says a party holds 41 troops and the client says 38, that is a bug whether or not
anyone thought to check troop counts. One digest over 1608 parties covers ground that 1608 assertions would.

**The false-positive problem is the whole difficulty.** A live campaign changes while it is being read, so two
snapshots taken seconds apart disagree about everything that moves. The digest header therefore carries the
campaign day, the sweep stops the clock on the authority before reading, and the comparator refuses to compare
snapshots taken at different campaign instants — reporting `INCONCLUSIVE` rather than a divergence it cannot
attribute.

Measured: with the clock stopped, all three processes report the identical campaign day, and a sweep over 1608
parties × 13 fields produced **zero** false positives.

Findings are deduplicated by `(scope, field)`, because "men differs on parties" is one bug whether it shows up
on three parties or three hundred. The count is evidence about severity, recorded as `worstCount`.

## What is proven, and how

- **Fault detection and scoped recovery.** A client was killed under a live supervisor. It was detected as
  `ProcessGone`, evidence was captured, recovery was correctly scoped to that client rather than the whole rig,
  and the relaunched client genuinely rejoined — verified as `CampaignState` on the live channel, not inferred
  from the launcher.
- **The oracle detects.** A known divergence was injected into a real 1608-party client digest: one party lost
  three troops and another was removed. Both were caught and named; nothing else was reported.
- **A full cycle runs.** Rig start → components registered → sweep in 16 seconds → `AGREED` → journalled.

## What it does not cover

- **A passive sweep only sees what the campaign did on its own.** The AI moves parties and fights its own
  battles, so movement and ownership get exercised; paths only a player drives do not. That is what
  `Drive-Battle.ps1` is for, and it currently drives one scenario — a two-owner field battle.
- **Digest fields are a chosen subset.** Divergence in a field nobody listed is invisible. Adding a field to
  `WorldDigestCommands.cs` widens the net for every entity at once, which is the cheapest way to extend this.
- **`AGREED` is not "no bugs".** It means the two sides agree about the fields listed, at that instant.

## What the first driven battle found

The harness earned its keep on the first battle it drove unattended. The battle resolved correctly
(`resultState=AttackerVictory battleResolved=True`) but **the mission never tore down** — both clients sat in
it with 503 agents, and twelve `coop.debug.battle.leave` calls each returned
`outcome=callAgainWhenMissionIsGone steps=[mission=alreadyEnding ...]`. `encounter.leave` cleared the encounter;
the mission survived anyway.

The expensive part was the second-order effect. A client wedged like that **still answers its pipe and still
reports `CampaignState`**, so the supervisor called it healthy and sat at `faults=0`. Its campaign clock then
ran free — three campaign days ahead of the server — which made every subsequent sweep `INCONCLUSIVE`. Left
overnight, that run would have reported a serene night and found nothing.

Hence the `StuckInMission` verdict: a client reporting an active mission for longer than
`-MinutesInMissionBeforeStuck` (default 20, against battles this rig drives in three to four) is restarted.
That mitigates the night; it does not fix the hang, which is tracked separately.

## Named scenarios

Reproducing a reported bug needs a world arranged a particular way, and "load my save and do the thing" does
not survive a restart. Scenarios therefore **build their own world** and put it back afterwards.

```powershell
.\Run-PrisonerScenarios.ps1 -List
.\Run-PrisonerScenarios.ps1 -Name capture-kingdom-fight
.\Run-PrisonerScenarios.ps1 -All
```

The prisoner set is a matrix rather than a list of hunches: who the captive is to the winner (own clan, own
companion, kingdom, merely at peace, at war) crossed with how the battle is resolved (fought, or troops sent).
When a reported bug does not reproduce on the first arrangement, the matrix is what tells you which arrangement
it *does* need — the case that misbehaves names the bug.

`PrisonerLab.psm1` holds the shared parts: discovering which party a client actually drives (so nothing is
hardcoded to one save), forcing a relationship with `clan.add_companion` / `kingdom.declare_war` /
`kingdom.make_peace`, staging a captive, winning a battle, and restoring what it changed.

## Traps worth knowing

- **Stopping a rig-owning background task kills the rig.** The game processes are children of the script, so a
  tree kill takes them with it. Expect to bring the rig back up (about 2 minutes warm) after changing
  supervisor arguments. Use `-SkipInitialStart` only when a rig is genuinely live — check `Get-CoopEndpoint`.
- **`Get-CoopEndpoint` emits its array non-enumerated.** Assign it to a variable first, then pipe or index
  *the variable*. Piping the call directly — `Get-CoopEndpoint | Where-Object {...}` — hands `Where-Object` a
  single item that *is* the whole array, and so does `@(Get-CoopEndpoint)`. Every property read off that comes
  back as `Object[]`: it silently registered one component called `server` and no clients at all, and later
  produced `Cannot convert "System.Object[]" to type "System.Int32"` on `[int]$clients[0].pid`.

  ```powershell
  $endpoints = Get-CoopEndpoint -RunToken t1          # correct
  $clients = @($endpoints | Where-Object { $_.role -eq 'client' })
  ```
- **The build needs `-p:NuGetAudit=false`.** `Scriban 7.2.0` has advisories and the projects treat warnings as
  errors, so `dotnet build source/Coop/Coop.csproj -c Debug` fails on a clean tree for reasons unrelated to any
  change. This is a pre-existing repo condition, not a symptom of the work above.
- **During a mission each process runs its own campaign clock.** Measured in one sweep: server day
  `94200.25358`, clients `94200.24956` and `94200.25792` — drifting independently. Stopping the clock on the
  server alone is enough on the map, where clients follow it, but not during a battle. `Invoke-CoopDivergenceSweep`
  therefore stops the clock on every process. When it was only stopping the server, the mid-battle sweep
  correctly refused all ten comparisons rather than reporting ~1600 false divergences — the guard working, not
  failing.
- **The campaign clock is STOPPED when the rig loads.** Nothing about a battle resolves without it: a
  send-troops battle sat at `state=None` for 200 seconds, and reading the captive out of that unfinished fight
  reported a captured hero when nothing had happened at all.
- **Stop the clock again BEFORE reading an outcome.** For a minute or two after a map event finalises, a
  captive is held by a party that is already being destroyed, and the campaign then moves them again — measured:
  two captives read as prisoners of a destroyed party, and minutes later one was free and the other was in a
  town dungeon. Judge a settled, stopped world or you are describing something other than the battle.
- **`addprisoners` resolves a hero by NAME, not id.** Handed a hero id it matches nothing, adds nothing, and
  says so only in passing — after which a release test has nothing to release and reads as the very bug it was
  checking for. Use `set_prisoners <partyId> <troopId> <count>`, which is keyed by party.
- **The hosted save starts broken.** Both player parties load at 0 HP, fully wounded, months starved, so every
  fresh load begins `NOT_READY`. `Drive-Battle.ps1` repairs them on the server before it does anything else.
