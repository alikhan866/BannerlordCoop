# Plan 1 - Headless client capabilities (campaign map, no battles)

**Goal.** A client that boots with no renderer, joins the existing dedicated server, and exposes enough
capability to be driven and observed from outside the process.

This document lists **capabilities**, not investigations. What those capabilities are later used FOR - chasing a
desync, checking whether a lord behaves oddly, reproducing a specific report - is a use of the rig, not part of
building it. Where a real defect is named below, it is only to justify why a capability is needed.

**Out of scope.** Missions - see Plan 2. Anything requiring a renderer.

**Depends on.** Nothing. This is buildable today.

---

## What already exists (do not rebuild)

| Piece | Where | Use |
|---|---|---|
| Render-free boot | `HeadlessServerBootstrap.IsRequested()`, `ModInformation.IsHeadless` | Same pattern for the client |
| UI-patch tolerance | `CoopMod` - `catch (Exception uiPatchFailure) when (headless)` | Extend to the client's patch set |
| Render-free map scene | `HeadlessMapScene`, distance cache lookup | Currently server-gated; needs the client role |
| Control channel | `LiveTestControlServer` - `status`, `command`, `command-catalog`, `join`, `screenshot`, `shutdown` | Already a socket into a client |
| Command dispatch | `LiveTestCommandDispatcher` | Where new verbs land |
| Fixture pattern | `AlleyRecruitDebugCommand` - `*_fixture_start`, `*_fixture_state`, `*_fixture_restore` | Template for state setup |
| Popup surface | `InformationManager.ShowInquiry`, `MBInformationManager.AddQuickInformation` | Intercept instead of render |
| Popup patch precedent | `PlayerPartyBarterMBInformationManagerPatches` | Proves it is hookable |
| Per-lord observation | `ClanActivityWatchHandler`, `Clan-Activity-Report.ps1` | Basis of behaviour profiling |
| Save redirection | `BANNERLORD_USER_DIR` | Basis of the sandbox |
| Load failure reporting | `HeadlessLoadTracePatches` finalizer | Managed exception when campaign load dies |
| Divergence naming | `MobilePartyBehaviorSnapshot.DescribeDivergence` | Template for the comparer |

---

# A - Run headless

### C1 - Headless client role
`/coopheadlessclient` alongside `/coopheadless`, with its own flag. A headless CLIENT joins, loads a baseline
and owns a player party; a headless server does none of those.

### C2 - Survive UI patching
Every patch category the client applies either tolerates the absence of a renderer or is explicitly skipped.

### C3 - Reach a startable state with no main menu
Boot render-free and become ready to start a session.

**Not "reach InitialState" - a render-free process never does.** There is no UI to build a main menu with, so
the state is never pushed and anything waiting for it waits forever. The readiness test is a game state manager
to push a loading state onto, which is what the dedicated server has always actually waited for.

### C4 - Join programmatically
Wire the existing `join` verb to the session start path, bypassing the join UI.

**The rig runs the CoopDebug module in Debug configuration.** The whole driving surface - the
`LiveTestControlServer` named pipe, the `join` verb, every `coop.debug.*` command - is `#if DEBUG`. Release has
no way in at all, and the alternatives are worse: opening a control pipe in shipped builds, or reimplementing
the join path twice. Debug for both server and client is also the existing named-debug-client rig, so this is
the setup that already works rather than a new one.

### C5 - Load a campaign from the server baseline
Confirm `HeadlessMapScene` works in the client role.

### C6 - Advance campaign time
Establish what drives the campaign clock without a UI tick.

---

# B - Sandbox

Capabilities that make the rig safe to point at anything. These land before any driving capability is used.

### C7 - Refuse protected saves
A fixture allowlist; anything named in `launcher.json` is protected. No override.

### C8 - Run on a generated copy
Both halves - `.sav` and `.json` - since they only work as a pair.

### C9 - Namespace and verify, NOT redirect
Every file a run creates carries a generated fixture name, and the run verifies afterwards that nothing outside
that namespace changed.

**Redirection is not available and must not be attempted.** `BANNERLORD_USER_DIR` moves only the coop half of a
save: `CoopSaveManager` honours it for the session `.json`, while the engine's own FileDriver ignores it and
writes the `.sav` to Documents regardless. Setting it puts the two halves in different folders, and a split
pair loads with every player sent back to character creation. That has already been tried and it broke saves.

So isolation is achieved by naming and checking, not by relocation: fixture-named files only (C8), and a
before/after inventory of the saves folder that fails the run if anything outside the namespace was touched.

### C10 - Snapshot and restore around a run

---

# C - Act

Each capability is a verb on the control channel, individually invocable and individually reportable.

### C11 - Query state
Position, current menu, rosters, party status, nearby parties, gold, influence, clan.

### C12 - Invoke a menu option
Look the option up and call its consequence.
**Must evaluate `OnCondition` first and refuse if false** - otherwise this is a bypass, not a click, and it
cannot observe a wrongly-disabled option.

### C13 - Select a conversation line
By index or token.

### C14 - Issue movement orders
To a position, settlement or party, with arrival reported.

### C15 - Resolve encounters
Attack, leave, send troops, join a side, break in.

### C16 - Drive a siege
Besiege, build, wait, assault, abandon.

### C17 - Settlement actions
Enter, leave, recruit, trade, talk to notables.

### C18 - Party and roster actions
Transfer troops, manage prisoners, take and release heroes.

### C19 - Clan and kingdom actions
Army join and leave, fief and governor assignment, diplomacy.
*Note: some of these are irreversible - see C24.*

### C20 - Set up state from a fixture
Place parties, set gold and influence, establish captivity or ownership before a run. Follow the
`AlleyRecruitDebugCommand` fixture pattern.

### C21 - Deterministic game randomness
Seed or stub the RNG the campaign uses, so paths involving persuasion or chance are repeatable.
**Without this, anything with a roll in it cannot be a stable capability.**

---

# D - Observe

### C22 - Capture popups
Patch `ShowInquiry` and `AddQuickInformation`; record title, text, options, real and campaign time into a
queryable log instead of rendering.

### C23 - Answer popups default-deny
A modal must be answered or the client hangs; blind acceptance makes campaign decisions nobody asked for.
Declared popups get their declared answer. **Undeclared popups get the negative answer and fail the run.**
There is no accept-all mode, not behind a flag.

### C24 - Server-side refusal of irreversible actions
A second layer outside the client's control: the server refuses a denylisted set from a connection flagged as a
test client - marriage, war and peace, fief grants, executions, clan destruction, kingdom decisions.

### C25 - Assert popup presence and absence
Wait for a popup matching a pattern; and fail if a forbidden one appears.

### C26 - Report menu reachability
What menu am I in, what options exist, which are enabled. Distinct from C12: this observes what the player
COULD do.

### C27 - Capture notifications and messages

---

# E - Detect stalls

Nothing throws when the game simply stops progressing, which is why this needs its own capability rather than
falling out of error handling.

### C28 - Progress watchdog primitive
"This observable must change within N, or report." Everything else here is a use of it.

### C29 - Calibrate before asserting
Measure what healthy looks like - siege stage duration, encounter resolution, baselines per join - and derive
timeouts from measurement rather than guesswork.

### C30 - Pause awareness
A paused campaign is not a stuck one. The server publishes its reason; the watchdog consults it before
reporting.

### C31 - Slow before stalled
First trip reports SLOW after a grace period; only a second reports STALLED.

### C32 - Liveness observables
Party movement after an order, siege stage advancement, encounter resolution, campaign time, join progress.

### C33 - Stall report
What was awaited, last state change, last popup, last menu, last command, both logs.

---

# F - Compare

### C34 - State comparer
Diff server against each client: party count and positions, member and prisoner rosters, gold, influence,
active map events. Name what differs, in the style of `DescribeDivergence`.

### C35 - Continuous comparison
Run during play, not only at checkpoints.

### C36 - Noise baseline
Record what a healthy session emits so only deltas are reported.
*Motivation: ~1,550 `Failed to get id` lines and an "Invalid version type" assert both appear in successful
runs; a mid-mission finalize warning fired ten times in one evening, nine of them harmless.*

### C37 - Behaviour profiling
Aggregate per-hero observations over campaign time into a profile: behaviour distribution, fraction of samples
moving, destinations, army participation, battles, settlements. `ClanActivityWatchHandler` already emits the
inputs.

### C38 - Control-group comparison
Given a hero, select comparable heroes and rank the differences between them.

### C39 - Declarative anomaly rules
Rules expressed as data, each carrying its documented consequence, so the rule set grows without code changes.
*Example of the shape: a null `LordPartyComponent._leader` collapses party size to base and parks the party.*

### C40 - Known-normal allowlist
Suppress shapes that are legitimately unusual, each entry carrying a reason.
*Motivation: a companion-led clan party is vanilla and must never be "repaired".*

### C41 - Report by actionability
Separate findings with a documented consequence from findings that are merely different. Never merge them.

---

# G - Explore

### C42 - Action space
Enumerate what may be driven, with preconditions and a success test per action.

### C43 - Seeded, replayable runs
A seed reproduces an identical action sequence and identical resulting state.

### C44 - Weighted policy
Action weights that approximate real play rather than uniform choice.

### C45 - Human-shaped awkwardness
Idle before answering, abandon half-way, repeat rapidly, leave mid-encounter.
*Motivation: the failures that matter tend to need a player who took three minutes over something.*

### C46 - Shrinking
Reduce a failing action sequence to the shortest that still fails.
**The capability that decides whether exploration is useful.** Four thousand actions is not a report; six is.

### C47 - Coverage measurement
Which menus, options, encounter types, settlement types and systems have ever been exercised - and which never
have.

### C48 - Coverage-guided steering
Bias selection toward the unexercised.

### C49 - Chaos actions
Disconnect, kill and rejoin a client mid-action.

### C50 - Campaign time acceleration
Bound a run by campaign time rather than wall clock.

### C51 - Deduplicate findings
Group by shrunk reproducer so one recurring fault is one report.

---

# H - Harness

### C52 - Scenario format
Set up, act, observe, assert - runnable unattended.

### C53 - Scenario runner against either client type
The same scenario must run headless or rendered; `screenshot` already exists on the rendered path.
*This is the only route to catching a rendering-shaped fault, and it is delayed rather than immediate.*

### C54 - Failure bundle
Both logs, the scenario, the comparer diff, the popup log, the stall report, campaign time.

### C55 - Launcher integration
`--headless-clients N` beside `--clients N`.

### C56 - Soak
Server plus headless clients for an extended run on a fixture save.

---

## Acceptance policy for changes made using this rig

Not capabilities of the client - rules for how the rig's output is used. Kept here so they are not lost.

- **Fail before, pass after.** A scenario that passes both ways proves nothing and looks like proof.
- **Run the whole library before accepting**, not only the target scenario. Fixed X, broke Y is the most likely
  unattended failure.
- **Assert capability, not the absence of errors.** "Nothing threw" passes while a player stands unable to move.
- **Autonomy follows the oracle.** Where correctness is machine-checkable, the loop can close unattended. Where
  "fixed" is a judgement, propose and stop.

---

## What is out of reach, and stays out

| Limit | Why |
|---|---|
| Rendering faults | There is no renderer. C26 catches the data-shaped half; C53 catches the rest at a delay |
| Exhaustive coverage | The action space is combinatorial. C47 reports what has been reached; it never claims completeness |
| Whether behaviour is *correct* | The rig measures what happens. Whether it should happen is a judgement |
| Missions | Plan 2 |
