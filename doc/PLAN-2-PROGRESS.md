# PLAN-2 progress — headless battles

Status of the 33 capabilities in `PLAN-2-headless-battles.md`, and what running them found.

Everything below was exercised against the real rig: a headless dedicated server plus two driven clients
(`jian` / `kan`) on save `saveauto3`, not against unit tests.

---

## Group A — observation (C1–C10)

| # | Capability | Status | Command |
|---|---|---|---|
| C1 | Battle state snapshot | **done** | `coop.debug.battle.snapshot [json]` |
| C2 | Spawn-progress liveness | **done** | `coop.debug.battle.spawn_progress [json\|reset]` |
| C3 | Expected-count assertions | **done** | `coop.debug.battle.expect_strength arm <def> <atk> <sec>` then poll |
| C4 | Supply-refusal reporting | **done** | `coop.debug.battle.supply_refusals [mapEventId]` |
| C5 | Reserve integrity checks | **done** | `coop.debug.battle.reserve_integrity` |
| C6 | Cross-client agent census | **done** | `coop.debug.battle.ownership_census` on each client, compared |
| C7 | Ownership census | **done** | same command; `unclaimed` is the C7 number |
| C8 | Battle outcome capture | **done** | `coop.debug.battle.outcome [mapEventId] [json]` |
| C9 | Battle timeline | **done** | `coop.debug.battle.timeline [mapEventId]`, `coop.debug.battle.mark <text>` |
| C10 | Scale as a parameter | **done** | `coop.debug.mapevent.start_nearest_bandit_attack <id> [excluded] [maxTroops]` |

### Supporting capability built along the way

Driving a headless client into a battle failed in a different place each time, so the client now reports
where it is stuck and can get itself out.

| Command | What it does |
|---|---|
| `coop.debug.blocker.state` | Innermost blocker: BARTER / CONVERSATION_CHOICE / CONVERSATION_CONTINUE / DEPLOYMENT / MISSION / MENU / ENCOUNTER / NONE, plus the readiness verdict and the commands that would clear it |
| `coop.debug.blocker.advance [fight\|leave]` | Clears it, never running an option whose condition fails; failures report their top stack frames |
| `coop.debug.blocker.escape` | Leaves a conversation this process cannot finish, by force if the clean exit throws |
| `coop.debug.party.battle_readiness [partyId]` | READY / SEND_TROOPS_ONLY / IN_BATTLE / NOT_READY from leader HP, morale, food and troops — decide before initiating, not after the campaign silently refuses |
| `coop.debug.battle.leave [retreat]` | Gets out of a battle and back to the map. Unwinds in stages and is safe to repeat; also reachable as `blocker.advance leave` |
| `coop.debug.battle.finish_deployment` | Commits deployment on demand instead of waiting out the ~2 min BR-025 timer — which is also what releases this client's troops to its peers |

The 20 % leader-health rule (below it, only `send_troops` works) lives as one named constant in
`PartyBattleReadinessCommand`.

---

## Group B — the gate (C11)

C11 answered: a mission **can** tick headless. The blocker is a native crash when the battle scene is loaded,
not a logic limit.

---

## Group C — running a mission headless (C12 done, C13 is the wall)

### C12 — renderer dependency inventory: **done**

The inventory was gathered by making a headless client actually try, and reading each failure. Every entry
below was a hard stop that killed the process; all five are fixed, each with a permanent diagnostic that says
what it stood in for.

| # | What failed | Where | Verdict |
|---|---|---|---|
| 1 | `PatrolPartiesCampaignBehavior.OnNewGameCreated` → `GetPartyTemplateForPatrolParty` NRE | vanilla new-game creation | **skip** — patrols are server-authoritative; nine sibling entry points were already disabled off the server and this one was missed |
| 2 | `Clan.PlayerClan` null → NRE in `SetSelectedCulture`, then in `ApplyFinalEffects` | `Campaign.PlayerDefaultFaction` | **replace** — it is assigned once, from `Hero.MainHero.Clan`, before the hero has a clan; restated after character creation |
| 3 | Face-generator stage reaches a Gauntlet view model | `CharacterCreationFaceGeneratorView` → `BodyGeneratorView` → `FaceGenVM` | **skip** — cosmetic, and replaced by the join baseline |
| 4 | `JoinAttemptOverlay.Show` builds a `GauntletLayer` | coop's own join UI | **skip** — it is a cancel *button*; a driven client cancels through the control channel |
| 5 | `CampaignReady` published once and latched | `HeadlessCampaignReadyPatch` | **replace** — a joining client reaches `MapState` twice (throwaway campaign, then the server's world) and the latch burned on the first, so `LoadingState` never completed |

Two non-fatal entries worth recording:

- `VanillaOrderVoiceService` cannot find `Modules\Native\Sounds\PC\voice.bank` in the server layout — a
  staging gap, not a renderer one. It already degrades to native voice playback, so it costs nothing.
- `MobilePartyAi.UpdateBehavior` and `MapLocator` engine asserts fire while the join baseline is applied.
  Noisy, non-fatal, and present on the rendered clients too.

**Result: a headless client is now a working third player.** It boots, creates its campaign, receives the
world, reaches `CampaignState` with `coopRunning=true`, owns a party, and answers every `coop.debug.*` command
including `blocker.state` and `party.battle_readiness`.

### C13 — render-free mission scene: **blocked, and the boundary is now exact**

A headless client driven into a battle gets **much** further than C11 assumed. In order, all on the render-free
process:

```
BLOCKER kind=MENU menu=encounter options=12 activated=true      <- the encounter menu works
BLOCKER_ADVANCE action=invoke id=attack                          <- attack is invoked
CoopBattleBehaviorAttacher  Attached coop battle behaviors to mission 'battle_terrain_p'
CoopFieldBattleLauncher     Opened coop field battle for MapEvent_Created_228700 (player side "Defender")
BattleHostHandler           Requested own battle reserves at entry
BattleInstanceLifecycle     Requesting P2P battle instance
BattleInstanceLifecycle     Announced MissionEntered
BattleMissionStartHandler   Attack mission opened: sequence=1 scene=battle_terrain_p
<process dies natively - no managed exception, no further line>
```

So the mission **opens** headless: behaviours attach, the launcher runs, reserves are requested and the P2P
instance is announced. The process dies immediately after, with no managed frame — the same signature
`HeadlessMapScene` was written to avoid on the campaign map.

The obvious fix — do what `HeadlessMapScene` does — **does not apply**, and it is worth writing down why so
nobody spends a day discovering it again. That class works because `MapScene.Load` is MANAGED: it calls
`Scene.CreateNewScene` and `scene.Read(name, ref initializationData, ...)`, so a subclass can pass a
`SceneInitializationData` with physics, flora, terrain blending and oros disabled.

A mission scene has no such seam. Following the call graph out of `MissionState.OpenNew`:

```
MissionState.HandleOpenNew -> MissionState.CreateMission -> new Mission(...)
Mission.Initialize -> IMBMission.InitializeMission(Pointer, InitializerRecord)   <- native
```

`Mission.Initialize` ends in a native call, and neither `Scene.CreateNewScene` nor any
`SceneInitializationData` constructor is called from managed code anywhere in `TaleWorlds.MountAndBlade.dll`
(checked by scanning every method body in the assembly). The engine builds the battle scene itself from
`MissionInitializerRecord`, and that record exposes only hints — `SceneLevels`, `SceneUpgradeLevel`,
`DecalAtlasGroup`, `DoNotUseLoadingScreen`, `DisableDynamicPointlightShadows`, `DisableCorpseFadeOut` — none of
which turns rendering off.

**Where exactly it dies, proven.** The last line the process writes is

```
Attack mission opened: sequence=1 scene=battle_terrain_003 missionStatePresent=true missionPresent=false
```

`missionPresent=false` is the proof: `Mission.Current` is not set yet, so **nothing managed had run inside the
mission at all**. `MissionState.OpenNew` only constructs the Mission and pushes the state; the native load
happens on the NEXT tick, in `TickLoading` → `LoadMission` → `Mission.Initialize` →
`IMBMission.InitializeMission`. Everything managed before that point succeeds — coop behaviours attach, the
launcher returns a mission, reserves are requested, the P2P instance is announced.

(Worth stating because it caught me out: `OpenCoopFieldBattle` returning non-null does **not** mean the scene
loaded. It means the mission was queued.)

**Four hypotheses tested against the live rig, all eliminated.** Each was set on the headless client through
`coop.debug.headless.mission_record`, confirmed applied in its log, and then driven into a battle:

| # | Hypothesis | Why it was plausible | Result |
|---|---|---|---|
| 1 | `sceneHasMapPatch = false` | campaign-only terrain stitching; a multiplayer record never sets it | **died** |
| 2 | `playingInCampaignMode = false` | the single biggest difference from a working MP dedicated-server mission | **died** |
| 3 | atmosphere + loading screen + shadows + corpse fade cleared | atmosphere loading is named in `HeadlessMapScene` as a render-free scene killer | **died** |
| 4 | `sceneName = mp_battle_map_001` | the campaign battle scenes are **not staged** in the headless layout at all — only MP maps are, and those are what TaleWorlds' own dedicated servers load | **died** |
| 5 | the real scene, with the 98 battle terrains staged for the first time | closes the loop on 4: with the assets actually on disk, does the campaign scene load? | **died** |

Hypotheses 4 and 5 deserve a note, because the setup looked damning and turned out not to be the cause.
`battle_terrain_*` lives in `Modules\SandBoxCore\SceneObj` (98 scenes), and the rig junctions `SandBoxCore`
from the *dedicated-server* copy, which ships no `SceneObj` at all. So the engine really was being asked to
load a scene that is not on disk — and pointing it at one that **is** staged died in exactly the same place.
Missing assets are a real gap in the rig - since fixed, so the staged layout now carries all 98 - but they
are not what kills the process: with the scene genuinely on disk, `battle_terrain_030` died in exactly the
same place.

**Also eliminated: the mission system handler.** `IMissionSystemHandler` has no implementation in
`TaleWorlds.MountAndBlade` — it lives in the view assembly a headless process never loads — so
`MissionState.Handler` is null there and the calls to it in `HandleOpenNew` and `FinishMissionLoading` are
null-tolerant. It is not the missing piece.

**No crash dump is produced**, in `C:\ProgramData\Mount and Blade II Bannerlord\crashes` or anywhere else,
and the log simply stops mid-line during ordinary campaign processing. Whatever ends the process does not go
through the game's own crash reporter.

**Lever tried and eliminated: the initializer record.** `MissionInitializerRecord` is the only thing managed
code still owns before the native load, so its presentational fields were cleared on a headless process —
`AtmosphereOnCampaign` (atmosphere loading is named in `HeadlessMapScene` as a render-free scene killer),
`DoNotUseLoadingScreen`, `DisableDynamicPointlightShadows`, `DisableCorpseFadeOut`. Confirmed applied in the
log (`stripped presentation from the mission record for battle_terrain_biome_034`) and **the process died
anyway**. The change was reverted rather than left in: it does not do what it was written to do.

Two traps in that experiment, both worth knowing:

- `MissionInitializerRecord` is a **struct**. A helper taking it by value mutates a copy and changes nothing,
  silently. It needs `ref`.
- Field-battle records are built on the **server** and sent in `NetworkStartAttackMission`. The client that
  opens the mission never calls `FieldBattleMissionInitializer`, so patching the initializer has no effect on
  the client — the only place that works is where the client opens the mission.

### Correction: the scene loads. It was never the scene.

Every statement above about `IMBMission.InitializeMission` being the wall was **wrong**, and it survived
several rounds of investigation because nothing had ever bracketed the call. Adding
`HeadlessMissionLoadTracePatch` — entry and exit lines around each step of `MissionState.LoadMission`, plus a
line per behaviour pre-load — produced this on a headless client:

```
[MissionLoad] 1/4 LoadMission entered
[MissionLoad] 1/4 preload BasicMissionHandler
... 36 behaviours, including MissionMapTimeView and PlayerNameplateMissionView ...
[MissionLoad] 3/4 Mission.Initialize entering (native) scene=Battle
[MissionLoad] 3/4 Mission.Initialize returned - the scene loaded
[MissionLoad] 4/4 LoadMission returned
```

**A render-free process loads a campaign battle scene without complaint.** All 36 mission behaviours pre-load,
including two `...View` classes. The access violation happens *after* `LoadMission` returns.

The lesson is worth keeping: "the last line in the log" named the mission being OPENED, which happens a whole
tick before the load, and every hypothesis was built on the assumption that the next thing to run was the one
that failed. Bracketing the calls cost one build and overturned five rounds of reasoning.

### How it dies

The process exits with **0xC0000005 — an access violation**, read from the exit code rather than a dump
(`Process.ExitCode` after `WaitForExit`; the value arrives as the negative Int32 -1073741819). That also
explains the missing crash dump: the fault kills the process before the game's own reporter runs.

### Where it dies

After `LoadMission`, `MissionState.FinishMissionLoading` runs:

```
Mission.Scene.SetOwnerThread -> Mission.Tick -> Handler.OnMissionAfterStarting
  -> Mission.AfterStart -> Handler.OnMissionLoadingFinished -> Scene.ResumeLoadingRenderings
```

That turned out to be the wrong sequence to look at, because **`FinishMissionLoading` is never entered**. The
trace stops after `LoadMission` returns, so the fault is in what `MissionState.TickLoading` does next:

```
LoadMission                          -> returns
Utilities.SetLoadingScreenPercentage <- a loading SCREEN, on a process with no screen
Mission.IsLoadingFinished            <- native, polls the scene load state
MissionState.FinishMissionLoading    <- never reached
```

`Scene.ResumeLoadingRenderings` was therefore never a suspect at all - execution does not get that far, so
that experiment was inconclusive by construction rather than negative.

Skipping the loading screen (both our own `LoadingWindow.EnableGlobalLoadingWindow` and the engine's
percentage calls) gets one step further, and the trace then ends on:

```
4.5/8 SetLoadingScreenPercentage SKIPPED (headless)
4.6/8 Mission.IsLoadingFinished -> false
```

### The scene load is ASYNCHRONOUS, and that explains everything

`Mission.Initialize` returning does not mean the scene loaded - it means the load **started**. Bannerlord
loads scenes on engine loader threads, `TickLoading` polls `IsLoadingFinished` until they are done, and the
access violation happens while that poll still reads `false`.

So the fault is **not on the game thread**, which is why it has never produced a managed frame no matter where
the brackets were placed, and why no crash dump names anything useful. Every earlier conclusion - "it dies in
`IMBMission.InitializeMission`", "it dies in `FinishMissionLoading`" - was looking for a synchronous failure
that was never going to be there.

What an asynchronous scene load prepares is render states. The engine has two managed switches for exactly
that, and both are applied to the mission's scene the moment `Initialize` hands it back:

```
Scene.SetDoNotWaitForLoadingStatesToRender(true)   - stop waiting on states that will never be ready
Scene.StallLoadingRenderingsUntilFurtherNotice()   - stop preparing renderings at all
```

Both were confirmed applied (`doNotWaitForRenderStates=true stalledLoadingRenderings=true`) and the process
**still faulted**. One trap on the way: `Mission.Current.Scene` reads **null** immediately after `Initialize`
returns, so the switches had to be applied from the load poll instead - the scene object only appears partway
through the asynchronous load.

### The answer: the process has no renderer at all

Comparing loaded modules settles it.

| Process | Graphics modules loaded |
|---|---|
| rendered client (`Bannerlord.exe`) | `d3d11.dll`, `dxgi.dll`, `D3DCOMPILER_47.dll`, `amd_ags_x64.dll` |
| headless client (`TaleWorlds.Starter.DotNetCore.exe`) | **none** |

The dedicated-server engine has no render device in-process. The native mission scene load needs one, and no
managed switch can conjure it. Every hypothesis that assumed otherwise was chasing a configuration problem
that was really an absence.

The campaign MAP loads render-free only because `MapScene.Load` is **managed** - `HeadlessMapScene` overrides
it and calls `Scene.Read` with a render-free `SceneInitializationData`. Missions have no such seam.

### What a future attempt would have to do

`Mission.Scene` has a **non-public setter** and a `<Scene>k__BackingField`, so a hand-built render-free scene
could in principle be injected the way `HeadlessMapScene` injects the map. But `IMBMission.InitializeMission`
does more than create a scene - it wires the mission's native side (agents, physics world, teams) - so
skipping it wholesale means writing a headless mission host, not applying a patch. That is a project, not a
fix, and it should be costed as one before anybody starts.

### What was kept

`HeadlessMissionLoadTracePatch` stays: headless-only, logging-only, and it is what turned two confident wrong
answers into the right one. The opt-in override and skip commands used to run the eight experiments were
**removed** - they existed to test hypotheses that are now closed, and code whose only purpose is answering a
settled question is dead weight.

Two smaller things worth doing regardless of C13:

- **Battle scenes are now staged.** `SandBoxCore` was junctioned wholesale from the dedicated-server copy,
  which ships no `SceneObj`. It is now staged subfolder-by-subfolder with `SceneObj` bridged from the game
  install - `scenes -> SandBoxCore\SceneObj from the game install (98 battle terrains)`. Only SandBoxCore's
  scenes, never SandBox's: that one holds the full 19 MB `Main_map`, which would shadow the stripped
  render-free map scene the headless server depends on. Not the cause of C13, but it would have bitten the
  moment C13 was solved.
- **`coop.debug.headless.mission_record`** is kept. It is inert unless a scenario sets a field, headless-only,
  and it turned each of the four hypotheses above from a twelve-minute build-and-restart into one command.

Until then C13 is **blocked**, and it is the one remaining gate for C14–C22.

---

## Findings

### 1. Own-party troops are withheld until the local deployment commit
`BattleDeploymentCoordinator.ShouldWithhold(isOwnPartyTroop) => !committed && isOwnPartyTroop`.

Until a client commits its own deployment, every other client sees only its own men. On a driven client
nothing clicks "Start Battle", so the commit only happens when the BR-025 deployment timer expires — measured
at **~2 minutes** after `MISSION_READY`. During that window a two-owner attacker side reports 251 of 502 on
**both** clients, and C1, C4, C5 and C7 all read `ok`, because each process is internally consistent.

Once deployment commits, both clients jump to 502 attackers / 514 agents **in lockstep**, and the census
agrees exactly (263 Jian + 251 Kan, 0 unclaimed, 0 migrated).

This is the reason C2 grew its second dimension. `coop.debug.battle.spawn_progress` now reports
`coverage=SHORT missing=251` for it while every other observable stays green.

*Not necessarily a defect* — the withhold rule is deliberate. What is missing is a way for a headless client
to finish deployment on demand: `coop.debug.mapevent.click_deployment_ready` refuses with
`deployment team setup is not complete`, and the blocker never reports `DEPLOYMENT` on this path.

### 2. A battle can wedge if a side is eliminated before the end-condition hold releases

**Downgraded from "does not end" - it does end, when deployment commits promptly.** With
`coop.debug.battle.finish_deployment` used to commit rather than waiting out the BR-025 timer, a 506-vs-13
battle concluded correctly on both clients (`mission ended=True result=victory=True enemyDepleted=true`) and
the server captured the outcome.

The mechanism behind the original failure is now named, and it is worth knowing because it is entirely ours:

**Nothing in the engine ever enables battle conclusion.** `Mission.CheckMissionEnd` asks each
`MissionLogic.MissionEnded`; only `BattleEndLogic` answers for a field battle; and its answer is gated behind
a private `_canCheckForEndCondition` whose only two callers in the whole codebase are in
`CoopBattleController`.

The release rule requires, **at one and the same tick**: `deploymentActivated` AND `attackerFielded` AND
`defenderFielded` (a side may be excused only if its reserve was deliberately abandoned). If a side is wiped
out before that combination is ever true - deployment activating late on the ~2 minute timer while a small
side dies meanwhile - then `defenderFielded` can never become true again, the gate stays false forever, and no
amount of depletion ends the battle. `PerformRoundRestart` re-arms the same hold, so a restart whose
re-release never comes back true has the same shape.

`coop.debug.battle.end_state` reports every input to the decision and names the first blocking reason in
engine order, so a recurrence reads `GATED-coop hold has not released` instead of looking like a hang.

Not yet fixed: a targeted repro (eliminate a side while the hold is still on) was not constructed, and
committing deployment promptly avoids it in practice.

### 2b. Original observation, for the record
Reproduced with 502 attackers against 12 defenders. The defender side reached `onField=0` with
`reserveRemoved=12` and stayed there for **3+ minutes**; `blocker.state` stayed `kind=MISSION`.

Meanwhile the **server had already destroyed the map event** — `coop.debug.mapevent.get_event` answered
`Failed to find MapEvent with id: MapEvent_Created_224077` while both clients were still in the mission
holding that instance id.

`BattleEndLogic` and `MissionCombatantsLogic` are both attached by `CoopFieldBattleLauncher`, and
`BattleResultReadyLogic.OnMissionResultReady` never fired, so the native mission never decided the battle was
resolved. Not yet root-caused.

### 3. Map conversations cannot be advanced, and cannot be ended cleanly either
Selecting any option throws `InvalidCastException` (`MapConversationAgent` → `Agent`). Frames now captured:

```
CampaignMissionComponent.ICampaignMission.OnConversationPlay
  <- ConversationManager.ProcessSentence
  <- ConversationManager.DoOption
```

`ConversationManager.EndConversation()` throws on the same node, which leaves
`IsConversationInProgress` true forever and blocks every later mission start. Pre-existing —
`coop.debug.conversation.select_index` fails identically. Worked around by `blocker.escape`, which force-clears
the manager's per-conversation state (deliberately **not** `ConversationManager.Clear()`, which would drop
every sentence in the campaign).

### 4. The headless server had no control channel
`Start-CoopDebug-DedicatedServer.ps1` never passed `/cooptestrun`, and `LiveTestControlServer` read only
`Environment.GetCommandLineArgs()` — which the dedicated-server starter does not populate. The rig could drive
every client and not the server, silently. Fixed on both sides; `LaunchArguments()` now concatenates the
engine's copy with the environment's, and `CoopMod` logs `[LiveTest] gate: enabled=… argCount=…` before the
decision either way. The launcher takes `-RunToken t1`.

### 5. A client can hang about a second AFTER leaving a battle

Seen once, in a three-client session (two rendered, one headless). The exit itself worked — jian logged
`[MissionEnd] EndMission`, `Announced MissionLeft`, `Stopping P2P Client`, `Mission finalizing sequence=1` and
`Game State is changing to MapState`. It then applied two queued world updates and **stopped logging
entirely** one second later. The process stayed alive with its game thread wedged; the live-test pipe was
unreachable for 17+ minutes afterwards.

The last thing it was doing was draining the backlog that accumulates while a battle runs —
`FactionStanceHandler` war stats and `PartyLifetimeHandler` "Applying destroy party". Not reproduced in the
earlier two-client run, where both clients left cleanly in two calls each and were `READY` again.

So `coop.debug.battle.leave` gets a client out of the mission, but it cannot be called reliable until this is
understood. Treat a wedged client after a leave as this, not as a new bug.

### 6. Three clients joining at once lose the baseline race

A join baseline is a snapshot of every party on the server. A party created by ANOTHER joiner while that
snapshot is in flight makes it arrive one party short, and the client that receives it disconnects to the main
menu:

```
Could not apply mobile-party join baseline: party count mismatch
  (baseline=1574, client=1573); onServerNotOnClient=[Player]
```

Launching two rendered clients and a headless one within seconds of each other lost **both** rendered clients
to the headless one's party. The count check is right to refuse — the snapshot really was incomplete — so the
fix belongs in the rig: join them in sequence, waiting for each to reach `CampaignState` before starting the
next.

---

## Running it

```powershell
# rig
Launcher\scripts\install-testmodule.ps1
Launcher\scripts\Start-CoopDebug-DedicatedServer.ps1 -SaveName saveauto3 -Visibility Public -RunToken t1 -Rebuild
# then one Bannerlord.exe per player with /cooptestrun t1 and a distinct /platformid
```

The save starts with both player parties at 0 HP, every troop wounded and months of starvation, so repair on
the server before any battle work — `set_hitpoints`, `inventory.giveitem <hero> grain 600`,
`mobileparty.set_troops <party> <troop> 250` — and confirm with `coop.debug.party.battle_readiness`.

A client that has not finished its join baseline reports `party=player_party … active=false`, so readiness
doubles as a join-completion probe. Wait for a real party id before driving anything.

---

## Not done

- **C13** — render-free battle scene. The one gate left; the boundary is now exact (see Group C above).
- **C14–C22** — gated on C13.
- **C23–C28** (scenarios), **C29–C32** (equivalence and performance), **C33** (server runs a mission).
- Root cause for finding 2 (a battle whose losing side is wiped does not end on the client). Worked around by
  `coop.debug.battle.leave`, which is enough to keep testing unattended.
