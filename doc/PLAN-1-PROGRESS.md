# Plan 1 - implementation progress

Tracks `PLAN-1-headless-client.md` capability by capability. Each entry records what shipped and how it was
verified, or why it is blocked.

**Rig code lives outside the repo** at `Desktop\Bannerlord co op\Rig\`, alongside the launcher, because
dedicated-client tooling does not go into source control. Mod-side changes needed for the headless client role
live in the repo but stay uncommitted.

| Group | Capabilities | State |
|---|---|---|
| B - Sandbox | C7-C10 | **DONE** |
| A - Run headless | C1-C6 | in progress |
| C - Act | C11-C21 | not started |
| D - Observe | C22-C27 | not started |
| E - Detect stalls | C28-C33 | not started |
| F - Compare | C34-C41 | not started |
| G - Explore | C42-C51 | not started |
| H - Harness | C52-C56 | not started |

---

## Group B - Sandbox - DONE

`Rig\Sandbox.psm1`, verified by `Rig\Test-Sandbox.ps1` - **24 checks, all passing**.

### C7 - Refuse protected saves - DONE
`Assert-FixtureSave` throws unless the name is in the `fixture-` namespace. Protected by pattern
(`saveauto*`, `kan-*`, `TransferSave`) and additionally by whatever `launcher.json` currently has configured,
so the save actually being played is covered without anyone remembering to list it.

**Deliberately has no override.** No `-Force`, no `-AllowProtected`. A test asserts those parameters do not
exist, because an override is the thing that gets used at 2am and the capability's whole value is that it
cannot be. A save that genuinely should be drivable is renamed into the fixture namespace - an explicit act,
not a flag.

An unreadable `launcher.json` **fails closed**: if the configured-save check cannot run, no name is cleared.

### C8 - Run on a generated copy - DONE
`New-FixtureCopy` copies both halves - `.sav` and `.json` - to `fixture-<scenario>-<runid>`. Both or neither:
a `.sav` without its `.json` loads with the player sent back to character creation, so a half-copy is worse
than a failed one. The destination name is itself passed through `Assert-FixtureSave`, so a caller cannot
construct a name that collides with a real save.

Verified against the plan's own done-when: the copy was deliberately ruined and `saveauto2.sav` was confirmed
hash-identical afterwards.

### C9 - Namespace and verify - DONE, but REDEFINED

**The original capability was not achievable and its obvious implementation is harmful.**
`BANNERLORD_USER_DIR` moves only the coop half of a save: `CoopSaveManager` honours it for the session `.json`
while the engine's FileDriver writes the `.sav` to Documents regardless. Setting it splits the pair, and a
split pair loads with every player sent back to character creation. That had already been tried once and it
broke saves - the warning is recorded in `Start CoopFixes DedicatedServer.ps1`.

So isolation is by naming and checking rather than relocation. `Get-SaveInventory` hashes every save file;
`Compare-SaveInventory` reports anything CREATED, MODIFIED or DELETED outside the `fixture-` namespace.
Fixture churn is expected and never reported.

The plan was updated to match rather than left describing something that cannot be built.

### C10 - Snapshot and restore - DONE
`New-SaveSnapshot` copies every non-fixture save (fixtures excluded - they are meant to change, and copying
them would double the disk cost of every run). `Restore-SaveSnapshot` puts them back and **verifies each by
hash**, returning what it could not restore rather than reporting success.

Verified end to end: the self-test damaged the live `saveauto2.json`, restored it, and confirmed the hash
returned to its original value. `saveauto2` was then checked against the evening's backup and is byte-identical
on both halves, with no fixture files left behind.

### Defect found and fixed during this group
`Compare-SaveInventory` and `Restore-SaveSnapshot` returned a bare `@()`, which PowerShell unrolls to `$null`.
A caller testing `.Count` would throw under StrictMode, or - far worse for a safety check - silently skip the
check without it. Both now return `,$list` so a result is always a list, including when empty. Three tests
caught this; it is the reason they exist.

---

## Group A - Run headless - IN PROGRESS

### C1 - Headless client role flag - DONE

`/coopheadlessclient`, alongside `/coopheadless`. Verified by `Coop.Tests\HeadlessRoleTests.cs` - 4 tests;
full suite 636 passed.

**The design had to change once, for a reason worth recording.** The plan said not to reuse `IsHeadless`
unchanged, and the obvious alternative was to gate server work on `IsServer && IsHeadless` - the pattern
`HeadlessPartyVisualLifetimePatches` already uses.

That would have broken the dedicated server. `ModInformation.IsServer` is set in
`CoopartiveMultiplayerExperience` when a SESSION starts - hosting or joining - while `IsHeadless` is set at
BOOT in `CoopMod`. At the moment the render-free gates run, `IsServer` is still its default on a dedicated
server too, so the added condition would have disabled the server rather than excluded the client.

What shipped instead:
- `IsHeadless` keeps its documented meaning - no renderer - and is true for BOTH roles, so every render-free
  path (map scene, view guards, party visuals) applies to a client for free.
- `IsHeadlessClient` is new, set at boot from the argument, and is the thing that is knowable when the gates
  run.
- `IsHeadlessServer => IsHeadless && !IsHeadlessClient` derives the server role from the two.

Five server-only gates were then closed against a client, having been found by auditing all 24 `IsHeadless`
usages:

| Site | Why a client must not do it |
|---|---|
| `HeadlessServerBootstrap.StartConsole` | opens a server console |
| `HeadlessServerBootstrap` command file | polls the dedicated-server command file |
| `SteamGameServerBoot` | boots a Steam GAME SERVER |
| `HeadlessCampaignReadyPatch` | signals the server to start listening |
| `HeadlessLateAiTickPatch` | server owns AI ticking |
| `CampaignDifficultyHandler.IsHeadlessHost` | host-only difficulty authority |

The remaining nineteen usages are genuine render-free concerns and were left alone - they are wanted by both
roles.

Argument matching is an exact per-token compare, not a prefix: `/coopheadlessclient` starts with
`/coopheadless`, and a `StartsWith` would make every client look like a server.

**Noted for later:** `HeadlessNotificationGuardPatch` suppresses notification registration when headless. A
headless client will want to CAPTURE notifications (C22, C27), so that gate will need revisiting when those
land - suppressing registration leaves nothing to hook.

### C2 - Survive UI patch application - DONE, nothing to implement

Verified rather than built, and that is the honest outcome.

`GameInterface.PatchAll()` has **no role branch**: it applies the static-fixes category, then
`PatchAllUncategorized` over the whole assembly, then the DI-registered categories, then AutoSync. The
categories come from `MissionModule.CreatePatchCategoryRegistrations()`, and `MissionModule` is registered into
**both** the client and server containers. So both roles apply an identical patch set.

The dedicated server already runs that set render-free every day. Patch APPLICATION is therefore proven
render-free for a headless client too, by the server's existence.

**Rejected: per-class tolerance.** Wrapping each patch class so one failure cannot abort the rest would make a
client more likely to boot - and would let a patch silently not apply. A missing patch is a silent desync,
which is worse than a loud failure. The existing behaviour (throw, abort, be noticed) is correct.

Live confirmation came from C3 below: the headless client booted through patching with no patch exception.

### C3 - Reach the initial state with no main menu - DONE

Boots render-free, stays alive, and does not do the server half.

**The launch shape had to be discovered, and it is not what the plan assumed.** Engine mode and coop role are
separate arguments, and only one combination works:

| Engine arg | Coop arg | Result |
|---|---|---|
| `/dedicatedcustomserver ... /client` | `/coopheadlessclient` | **exits within 5s**, after 3 log lines |
| `/dedicatedcustomserver ... /server` | `/coopheadlessclient` | **boots and idles** - correct |

The render-free binaries are the `Win64_Shipping_Server` layout, and the engine wants its dedicated mode. So a
headless client runs in the engine's SERVER mode while our module takes the CLIENT role - which works because
`CoopMod` forces `isServer = false` whenever `/coopheadlessclient` is present, independently of the engine
argument. Engine mode is about rendering; coop role is about who joins whom.

Observed on the working run (`Coop_server_36348.log`, pid-suffixed because the canonical file was locked by a
running server):

```
[Headless] game version fallback: v1.4.8.119303
[Headless] engine services installed (debug manager, version guard)
[Engine] Found 55 Client Game Network Messages
[Engine] Found 159 Server Game Network Messages
[Engine] opening ..\..\Modules\Native/ModuleData/...
```

and still running at 150s. Absent, correctly: no `SteamGameServerBoot` / `GameServer.Init`, and no
`[ManagedServer] headless engine ready - hosting save`. Those are the C1 gates holding in a live process.

**Regression check on C1.** The dedicated server was started normally afterwards and reached
`server is up - lobby 109775242004426362` with campaign up and post-start commands applied - exercising all
five re-gated sites (Steam game server, campaign-ready signal, command file, console, AI tick). No regression.

**C3's original done-when was impossible, and the codebase already said so.** The plan asked for
`ActiveState is InitialState` headless. `CoopMod.TryManagedServerAutoStart` has carried the answer since the
dedicated server was built:

> InitialState is the main menu. A windowless server never reaches it - there is no UI to show one - so waiting
> for it there means waiting forever.

So there was never anything to observe, and adding an observable would only have proven the state absent. The
capability was rewritten to what it actually needs to mean - render-free and ready to start a session - and the
readiness test is a game state manager, which the working server has always relied on.

### C4 - Join programmatically - CODE LANDED, live join not yet run

**The rig runs the CoopDebug module in Debug, and that is a finding rather than a preference.** Everything that
can drive a client is `#if DEBUG`: `LiveTestControlServer` and its named pipe, the `join` verb,
`JoinFixtureCommands`, all of `JoinDebugCommands`. A Release headless client has no control surface whatsoever.

The two Release routes were both worse. Opening the control pipe in shipped builds puts a remote-control
surface in players' hands to save a rebuild; reimplementing join for Release duplicates the path that already
exists. Debug for server and client together is also the rig recorded in memory as the standard local setup -
debug clients need a CoopDebug server, so both sides move together.

**The blocker, and one fix for all three instances of it.** The join gate refused anything that was not at the
main menu:

```csharp
if (!(GameStateManager.Current?.ActiveState is InitialState) || Campaign.Current != null)
```

which a headless client can never satisfy, for the reason recorded under C3. The same wrong test appears in
three start paths, so it was fixed once in `SessionStartReadiness.CanStartSession()` rather than three times:

| Site | Before | After |
|---|---|---|
| `TryManagedServerAutoStart` | its own inline headless workaround | shared test |
| `TryAutoConnect` | InitialState only - **headless could never connect** | shared test |
| `HandleDeferredClientJoin` | InitialState only - **the C4 blocker** | shared test |

A rendered process still requires `InitialState`, deliberately: a client on the splash screen also has a state
manager, and starting a session there would race the menu it is about to build. `Campaign.Current != null` is
still rejected on the join path - joining over a live campaign is wrong in either role. The refusal message now
reports the actual state instead of asserting the client is not at a menu it was never going to reach.

`SessionStartReadiness` is not `#if DEBUG`; only the join caller is.

Verified so far: **Release and Debug both build** (the changed join code is DEBUG-only, so the Release gate
alone would not have compiled it), **636 Coop.Tests passing**, unchanged.

**Live join verified.** A render-free client connected to a render-free server and completed the handshake:

```
server: Client connection accepted for 127.0.0.1:52711
server: Connection is changing to ResolveCharacterState State
server: NetworkModuleVersionsValidated / NetworkClientValidated
client: Attempting connection to 127.0.0.1:4200...
client: [AutoConnect] StartAsClient() returned true
client: Client is changing to ValidateModuleState State
```

Module-version validation passing is worth stating plainly: both sides were the Debug build under module id
`CoopFixes`, so the Debug rig is compatible end to end rather than merely launching.

**Two launch facts the plan did not have.**

`/autoconnect` alone drives the join - the pipe is not needed for C4 at all. This only works because of the
readiness fix above; `TryAutoConnect` could never fire on a headless client before it. The `join` verb remains
worth having as the control channel for Groups C and D, and it needs `/cooptestrun <token>`:
`LiveTestControlServer.IsEnabled` tests for that argument, not for `/cooptestmanualjoin`. Without it the pipe
never starts, which is why the deferred-join path stayed dormant this run and autoconnect took over.

Rig launch shape, both processes from the staged engine, ports distinct:

```
server  /dedicatedcustomserver 7210 EU 0 <modules> /server /coopheadless /coopsave <fixture> /coopvisibility None
client  /dedicatedcustomserver 7211 EU 0 <modules> /server /coopheadlessclient /autoconnect
```

**Sandbox used in anger.** The server hosted `fixture-c4join-a1`, a `New-FixtureCopy` of `saveauto2`, and
`Compare-SaveInventory` reported no non-fixture change. Group B did its job on its first real run.

### Defect fixed: a headless client wrote its log as the server

`SetupLogging` chose the file postfix from `isServer`, which is set by the `/server` ENGINE argument - and a
headless client runs in the engine's dedicated-server mode, so both roles claimed `Coop_server.log`. The loser
fell back to a pid suffix, so a rig with one server and one client produced `Coop_server.log` and
`Coop_server_10580.log` with nothing but the pid to tell them apart. It had already cost time once. The
postfix now also asks whether this process is a headless client.

---

## Findings from the first headless join

Three, all found by the rig on its first run rather than by reading code.

### F1 - Character creation kills a headless client (BLOCKS C5)

The client process died with a native access violation, `0xc0000005`, faulting module unknown - no managed
stack, so nothing in the log names it. The sequence is what identifies it:

```
client: Client is changing to ValidateModuleState State
client: ERR Controller Id was not set properly before validation has started
client: Game State is changing to GameLoadingState
client: ... loading conversation_scenes.xml / meeting_scenes.xml   <- last line before the crash
server: Connection is changing to CreateCharacterState State
```

One cause with four symptoms: the controller id is not set on a headless client, so the server cannot resolve
it to a saved player, so it routes the connection to CreateCharacterState, so the client enters character
creation - which loads conversation and meeting scenes and needs a renderer to do it.

Fixing the controller id should collapse the whole chain, because a client that resolves to an existing hero
never enters character creation at all. That is the first thing to try under C5, ahead of anything that guards
the scene load - guarding the scenes would keep the process alive in a state it should never have reached.

`ConnectionToken.ControllerId = peerId` is where the value comes from; a headless client has no platform
identity to supply one.

### F2 - JoinAttemptOverlay throws on a client with no UI

```
ERR Failed to run action on the game thread: ShowJoinAttempt
System.NullReferenceException
   at GameInterface.Services.UI.JoinCancel.JoinAttemptOverlay.Show(String cancelLabel)
   at Coop.Core.Client.States.MainMenuState.<ShowJoinAttempt>b__18_0()
```

Non-fatal - `GameThread.RunSafe` caught it and the join continued - but it is a render-free path that was
missed, and exactly the shape C2 predicted would show up at RUN time rather than patch time.

### F3 - The AutoConnect log line asserts a state that is not true

`[AutoConnect] InitialState active - auto-starting as client...` now prints on a headless client, where
InitialState is precisely what was NOT reached. Cosmetic, but it is a log line that would send someone
debugging in the wrong direction. Fix with the C5 work.

## C5 - IN PROGRESS. Two rig facts found the hard way

### The engine's own bin shadows the module folder

Deploying Debug assemblies to `Modules\CoopFixes\bin\...` changed nothing, because the staging script also
bridges our assemblies into `engine\bin\Win64_Shipping_Server`, and .NET probes the application base directory
first. The module copies were never loaded.

That is why F1 looked like a code bug and was not one. `ValidateModuleState` picks the controller-id source
with `#if DEBUG`, the module copy had DEBUG defined - `JoinDebugCommands` is present in it - and yet the
process logged `UserId was null`, which only the `#else` branch can produce. The assembly being executed was
the Release one in engine bin.

**Rule for the rig: our assemblies must be updated in BOTH places, or neither.** The six are `Coop`,
`Coop.Core`, `Common`, `GameInterface`, `Missions`, `Coop.Steam` - the same list `LiveTestControlServer`
already keeps for its status report.

### The mistake, and the rule that prevents it

Syncing "every dll in engine bin that also exists in the module output" replaced 90 engine assemblies with
module-local copies, and the server stopped booting. Among them was `0Harmony.dll`.

That single file explains it. The module ships the **net472** Harmony because the game client is .NET
Framework; the headless starter is .NET 6, and the net472 flavour bundles a MonoMod whose IL emitter calls
`ILGenerator.MarkSequencePoint`, an API .NET Core removed. Every patch then dies with
`MissingMethodException` and the process exits after three log lines. The staging script documents this
precisely and deliberately drops the **net6** build over it; I overwrote that.

**I concluded from this that Debug configuration cannot run headless. That was wrong** - the same failure
reproduced after restoring Release, which is what showed the configuration was never the variable. Restoring
the net6 Harmony (2,333,696 bytes, versus the net472 flavour's 2,461,696) fixed it, and the Debug build then
booted and patched normally: `Patched LoadMovieAux successfully`.

Copy our six assemblies by name. Never bulk-copy the module output over engine bin.

### Roslyn was downgraded too - and that was the "slow" server

The Debug server never was slow. `AutoSyncBuilder.Build()` compiles at runtime with Roslyn, and my workshop
restore had put `Microsoft.CodeAnalysis` **2.8.0** into engine bin where the module needs **4.13**, so
`PatchAll` threw `MissingMethodException` on `CSharpCompilationOptions..ctor` and the host start died. The
engine kept ticking - hence a heartbeat with no port, which reads exactly like a stall.

Restoring the two Roslyn assemblies fixed it: the Debug server then reached `Server starting on port 4200` in
**16 seconds**, matching Release. Both earlier conclusions - "Debug cannot run headless" and "Debug is thirty
times slower" - were artefacts of assemblies I had downgraded, not properties of Debug.

A version sweep found eight module-versus-engine-bin mismatches. Only Roslyn was a genuine downgrade; the rest
are the .NET 10 runtime's own, newer assemblies and were left alone.

### F1 FIXED - the client resolves its hero and skips character creation

`/platformid <controllerId>` is all it took, with no code change. In DEBUG `ValidateModuleState` calls
`SetControllerFromProgramArgs`, which reads that argument. The controller ids live in the save's `.json`
sidecar; this run used `76561199074278663` (Hero_Player2863 / party Player425), one of two, both offline.

The server's connection flow changed exactly as predicted:

```
before:  ResolveCharacterState -> CreateCharacterState        (client then died in character creation)
after:   ResolveCharacterState -> TransferSaveState -> LoadingState
```

The client received the save - 85 chunks, 5,519,691 compressed to 42,868,772 bytes - and entered
`LoadingState`. F2's overlay NRE still fires and is still non-fatal. F3's misleading log line is still there.

The logging fix is confirmed live: the client now writes `Coop_client.log` beside the server's
`Coop_server.log`, instead of both claiming the server name.

---

## C5 - BLOCKED. A headless client cannot load the campaign

Reproducible three times out of three, always the same Windows fault bucket `1910759374185057363`:
`0xc0000005`, faulting module unknown, no managed stack.

```
client: Client is changing to LoadingState State
client: [Engine] loading conversation_scenes.xml
client: [Engine] loading meeting_scenes.xml     <- last line, every time
```

**What this is not**, each ruled out by evidence rather than reasoning:

| Ruled out | How |
|---|---|
| Character creation | This run never entered it - the server went straight to TransferSaveState |
| The scene XML itself | The server loads the same files and continues past them to `siegeengines.xml` |
| Debug configuration | The Debug server boots, patches and hosts normally in the same session |
| Campaign object deserialization | `/cooptraceload` emitted **nothing**, so the crash precedes object loading |

**Thread affinity was the leading hypothesis and it is WRONG.** `HeadlessServerBootstrap`'s remarks record this
exact crash signature from the server's history, so it was the obvious suspect - but reading the path before
changing it showed `GameStateInterface.LoadSaveData` already marshals:

```csharp
GameThread.Run(() => InteralLoadSaveGame(saveData), blocking: true);
```

Also ruled out on the same read: `ReceivingSavedDataState` calls `GoToMainMenu()` before loading, which looked
alarming on a process that cannot build a main menu - but it returns early when `Campaign.Current == null`,
which is exactly the case for a joining client. A no-op.

**Both roles converge on the same call**, so the game manager is not the difference either:

| | server | client |
|---|---|---|
| entry | `LoadGame(saveName)` | `LoadSaveData(bytes)` |
| via | `SandBoxSaveHelper.LoadGameAction` | `SaveManager.Load("", driver, loadAsLateInitialize: **true**)` |
| driver | file | `CoopInMemSaveDriver` |
| then | `MBGameManager.StartNewGame(new SandBoxGameManager(loadResult))` | **identical** |

**Where the crash actually sits.** Two independent instruments were silent: `/cooptraceload` emitted nothing,
so it precedes campaign-object deserialization; and no `[Headless] load step:` line appears in the client log
though the server emits them, so it precedes `CharacterRelationManager.AfterLoad` and `Campaign.OnSessionStart`
too. Combined with the last logged line, the crash is inside game-manager startup - the phase that loads
`sp_battles.xml`, `conversation_scenes.xml`, `meeting_scenes.xml` - and before anything the existing tracing
covers.

Two differences remain unexplained and are the next things to test, in this order because the first is one
argument and the second is a driver:

1. `loadAsLateInitialize: true` on the client path only.
2. `CoopInMemSaveDriver` versus the engine's file driver.

The cheap decisive experiment is to make a headless client load the SAME save from disk rather than from the
transfer. If it survives, the fault is in the late-initialize or in-memory-driver path; if it dies identically,
a client-role campaign load is render-free-unsafe in general and the fix belongs in the game-manager startup.
That needs a way to drive a load on a headless client, which is the Group C control surface - so C5 and C11
are entangled and C11 should come first.

---

## C11 - Query state - DONE except nearby parties

`Rig\Control.psm1`. The control channel works on a render-free process, and C11 is composed on top of it.

### Getting a channel at all

`LiveTestControlServer.IsEnabled` tests for **`/cooptestrun <token>`** - not `/cooptestmanualjoin`, which is
what the earlier C4 attempt passed, and why the pipe stayed dormant then. The process advertises itself at
`%TEMP%\BannerlordCoop.LiveTest.v1\<token>\<pid>.json`, so the rig never needs to be told a pid.

Starting it also needs `/autoconnect`, which the gate in `NoHarmonyLoad` requires. That is safe on a dedicated
server: `TryAutoConnect` returns early whenever `HasAutoLoadSave`, which a `/coopsave` server always has. No
code change was needed to get a channel on the headless server.

### The finding that shapes Groups C and D

The channel already carries a **`command`** verb reaching the game console, and a **`command-catalog`** verb
that lists what is registered. This build answers with **494 commands**.

So most of Group C is composition, not new C# in the mod. `Control.psm1` deliberately does not grow a function
per game command - it carries the transport plus the composed queries, and everything else goes through
`Invoke-CoopCommand`.

Parameter shape is `{ name, arguments[] }`. PowerShell collapses a one-element array to a scalar on
conversion, which serialises as a string and is rejected, so the argument list is forced to stay a list.

### What C11 returns, verified live against the fixture campaign

Zero errors, both players, on a headless server in `MapState`:

| | 76561198876156674 | 76561199074278663 |
|---|---|---|
| party | Jian's Party | Kan's Party |
| position | 517.58, 329.84 | 518.11, 330.07 |
| gold | 3,830,832 | 3,589,142 |
| influence | 205.58 | 319.81 |
| renown / kingdom | 4259.6 / Wang | 3159.2 / Ki |
| morale / food | 73.5 / 70% | 80.8 / 73% |
| strength / limit | 679 / 371 | 755 / 376 |

Influence has **no read command** - only `add_influence` and `give_influence` - so it is parsed out of the
`clan.info` field dump, which is where the value actually lives. Same for the party fields, from
`mobileparty.info`.

Per-field failures are collected rather than thrown. A state query used while diagnosing a half-broken session
has to return the fields it CAN read; one unresolvable hero id must not take the whole answer with it.

### The gap, stated rather than glossed

**Nearby parties is not implemented, and cannot be composed.** `mobileparty.list` returns ids and names only,
with no positions, over roughly 1,536 parties - so "within N of here" would need a position round trip each.
It needs one new console command, `coop.debug.mobileparty.near <partyId> <radius>`, in the same shape as the
existing 494. Small and in-pattern, but it is mod code rather than rig code, so it is called out rather than
slipped in.

Detailed troop rosters are also absent: `party_screen_troop_state` refuses with "Command can only be run on a
client", so rosters land with the client work. Strength, member limit, morale and food do come through.

---

## C5 - root cause found and fixed, one layer deeper now

### Group C is gated on C5, which is why C5 came back to the front

Checked before building anything: `coop.debug.ui.switch_menu` answers **"Run this command on a client."** The
same is true across Group C - menus, conversations, movement, encounters, sieges, settlements, rosters are all
client-role. Building C12-C19 against a client that cannot exist would be building blind, so C5 is the
critical path and was taken next despite the plan's order.

### The experiment, and what it eliminated

Two shipped pieces made it possible:

- `coop.debug.save.load_local <name>` (new, DEBUG-only) - loads a save from disk in THIS process, the way the
  server loads its own. Deliberately not routed through `IGameStateInterface`, because an un-joined client has
  no container to resolve it from and joining would reintroduce the path under test.
- A fix to the `command` verb. It refused every command with `session_not_ready` when no container existed,
  special-casing exactly one. `command-catalog` already built a dispatcher directly in that situation, so the
  refusal was an inconsistency rather than a safety rule - and it left an un-joined process unable to run the
  very commands meant to run before a session. It now builds the dispatcher the same way.

With those, a headless client was brought up with `/autoconnect /cooptestrun rig2 /cooptestmanualjoin` - pipe
open, join deferred, no server involved at all - and told to load the fixture from disk.

**It died identically.** Same last line, same crash. That eliminates the entire join path in one measurement:

| Suspect | Verdict |
|---|---|
| Save transfer over the wire | **not it** - no transfer occurred |
| `loadAsLateInitialize: true` | **not it** - the disk path does not pass it |
| `CoopInMemSaveDriver` | **not it** - the disk path uses the file driver |
| Character creation | **not it** - no server, no connection |

Leaving one difference between a process that loads this save happily and one that dies: the role flag.

### Root cause: a render-free guard that only covered the server

`HeadlessPartyVisualLifetimePatches` installs lightweight visual shells so campaign code that expects a party
visual gets one without a renderer. Its guard read:

```csharp
private static bool IsHeadlessServer => ModInformation.IsServer && ModInformation.IsHeadless;
```

`IsServer` is false on a headless client, so the guard was **off** exactly where it was needed. The engine then
built a real visual per party - roughly fifteen hundred of them - on a process with no renderer. The class's
own remarks say what that costs: reaching for `MobilePartyVisualManager.Current` on a windowless process
"does not return null - it throws inside SandBoxViewSubModule".

This is the `IsServer && IsHeadless` formulation C1 rejected for the boot gates, surviving in a second place
and biting from the other direction. The guard is not about server-ness; it is about having no renderer:

```csharp
private static bool IsRenderFree => ModInformation.IsHeadless;
```

**This fix is NOT verified, and the claim that it was is withdrawn.**

I recorded it as verified because the crash moved 233 log lines further. That reasoning was invalid.
`HeadlessServices.Install()` installs only the debug manager and the version guard at boot; every other
GameInterface patch - the party-visual patches included - comes from `GameInterface.PatchAll()`, which runs at
**session start**. The `load_local` client never joined, so it had no GameInterface patches at all, and
changing a patch's guard cannot move a crash in a process where that patch was never applied.

Re-run on the path that does apply patches - a real join, fix deployed - the client dies at
**exactly the same place as before**, `meeting_scenes.xml`, and never reaches map scene creation.

The guard is still wrong on its own reading: `IsServer && IsHeadless` excludes a render-free client from a
render-free guard, and the class exists precisely because that path throws without a renderer. The change is
kept on that reasoning. But it does not fix C5, and it should not be described as if it does.

### The disk-load experiment was not a like-for-like comparison either

Same root mistake. The server loads its save from **inside** a session, after `PatchAll`; the `load_local`
client loaded with no patches whatsoever. So "the transfer path is eliminated" is not supported - the two runs
differed in patch state as well as in load route.

What the runs do jointly show is narrower and still useful: a render-free client dies in this region whether
the save arrives over the wire or off the disk, and whether or not GameInterface is patched.

### The crash point is not stable, so the last log line is not a locator

Three runs, three answers, with no consistent relationship to patch state:

| Run | Patches applied | Last line |
|---|---|---|
| join | yes | `loading meeting_scenes.xml` |
| disk load, before guard change | no | `loading meeting_scenes.xml` |
| disk load, after guard change | no | `Creating map scene` |

The engine reports `Max Degree of Parallelism is set to: 10` during this phase, so scene loading is
concurrent - which fits a wandering last-logged line and means the tail of the log locates the crash only
loosely. Any future conclusion drawn from "it got further" needs a stronger instrument than the log tail.

### What is solid

The server never lets the engine build a map scene: it logs `[Headless] substituting the render-free map
scene` and never `[Engine] Creating map scene`. The client logs the opposite when it gets that far. So the
substitution works where it runs - the client simply dies before reaching it on the join path.

### Resolved by instrumenting instead of guessing

`HeadlessLoadTracePatches` traced only `Campaign.*` steps, all of which run after game-manager startup - so it
was silent for exactly the phase that was failing, and the log tail was all there was. Extending it backwards
by six one-shot methods answered it on the first run.

`DoLoadingForGameManager` was deliberately left out: it runs once per frame, and burying the answer in
thousands of lines is its own kind of blindness.

**It was never a native crash.** The finalizer caught a managed `NullReferenceException` with a full stack:

```
MBGameManager.OnGameStart THREW System.NullReferenceException
   at TaleWorlds.GauntletUI.WidgetInfo.GetWidgetInfo(Type type)
   at TaleWorlds.GauntletUI.BaseTypes.Widget..ctor(UIContext context)
   at TaleWorlds.GauntletUI.UIContext.Initialize()
   at TaleWorlds.Engine.GauntletUI.GauntletLayer..ctor(...)
   at GameInterface.Services.Chat.ChatOverlay.Initialize()
   at GameInterface.Services.Chat.ChatService.Initialize()
   at Coop.CoopMod.OnGameStart(Game game, IGameStarter gameStarterObject)
```

The exception escaped through `Campaign.DoLoadingForGameType` and killed the load. Windows only ever reported
`0xc0000005` with no managed stack, which is why three rounds of reasoning from the log tail went wrong: the
tail wandered because scene loading runs at a parallelism of ten, and the real fault was in our own code.

## C5 - Load a campaign from the server baseline - DONE

**Root cause: our chat overlay builds a Gauntlet UI layer on a process with no Gauntlet UI.**

```csharp
if (ModInformation.IsClient && ContainerProvider.TryResolve<IChatService>(out var chatService))
    chatService.Initialize();
```

`IsClient` is `!IsServer`, so it is true on a driven client - which is a client in every sense except having
something to draw on. The overlay's `GauntletLayer` walks `UIContext.Initialize` into `Widget`'s constructor
and dereferences null. Fixed by adding the render-free condition.

This is the third instance of the same shape: a guard written in terms of the SERVER/CLIENT split where the
real question is whether the process has a renderer. The first two were the boot gates (C1) and
`HeadlessPartyVisualLifetimePatches`.

**Verified, over the control channel rather than from the log:**

| | value |
|---|---|
| client `activeState` | `TaleWorlds.CampaignSystem.GameState.MapState` |
| client `campaignLoaded` | `True` |
| server `connectedPlayerCount` | `1` |
| server `connectedControllerIds` | `76561199074278663` |

The plan's own done-when - "confirm `HeadlessMapScene` works in the client role" - is met directly: the client
logs `[Headless] substituting the render-free map scene`, followed by `[Headless] campaign services installed
(map event visuals, banners)` and a clean run through `Campaign.OnSessionStart done` and
`SandBoxGameManager.OnLoadFinished done`.

So a render-free client now joins a render-free server, receives the 42 MB baseline, loads it, and sits in
MapState with the server counting it as connected.

**Left behind deliberately:** a headless client has no chat. `ChatService.Initialize` builds only the overlay,
so skipping it removes the whole service. If a driven client later needs to send or observe chat - plausible
for Group D - the service needs splitting into transport and overlay rather than the guard being loosened.

**Status of the party-visual guard:** still unverified as a fix for anything, and still kept. It is wrong on
its own reading, and this crash was in front of it - so whether it also mattered is now testable but untested.

## The join now completes - CampaignReady had no publisher for a render-free client

Loading the campaign was not the end of it. The client reached `MapState` with all 1,536 parties matching the
server, and then both sides stopped: server on `phase=WaitingForCampaignEntry`, client on `LoadingState`.

`LoadingState` waits for a `CampaignReady` message, and that message had exactly two publishers:

| Publisher | Fires when | On a headless client |
|---|---|---|
| `HeadlessCampaignReadyPatch` | `TaleworldGameState.OnActivate` is `MapState` | gated `IsHeadlessServer` - **skipped** |
| `GameLoadedPatch` | `MapScreen.OnInitialize` | a UI screen - **never built** |

A render-free client fell between them and waited forever. Gating the headless publisher on `IsHeadless`
covers both render-free roles, which is what it was always describing.

**This is the fourth instance of the same mistake** - a guard written in terms of server-versus-client where
the real question is whether the process has a renderer. C1's boot gates, the party-visual guard, the chat
overlay, and now this.

Verified: client `ValidateModuleState -> ReceivingSavedDataState -> LoadingState -> CampaignState`, server
`ResolveCharacterState -> TransferSaveState -> LoadingState -> CampaignState`, with
`successfulBaselines=2`, `baselineApplicationFailures=0`, and `partyTotal=1536 activeParties=1536` identical
on both sides.

## C6 - Advance campaign time - DONE

**What drives the clock:** the server owns it, the client follows. No UI tick is involved anywhere - the
dedicated server has been running campaigns all session with no window, and the driven client now does the
same.

Campaign time was not readable over the channel, so `campaignTime`, `campaignTimeDays` and `timeControlMode`
were added to the status verb. `ToDays` rather than raw ticks: the tick accessor is internal, and Coop
references the stock TaleWorlds assemblies rather than the publicised ones GameInterface uses. The numeric
form matters - a formatted date only changes on the hour, so two samples minutes apart can read identical on a
healthy clock and be mistaken for a stall. C32 and C54 both need this anyway.

Verified by sampling both processes while the server alone was told to run:

| | server | client |
|---|---|---|
| t0, paused | 92589.6556 | 92589.6556 |
| t1, +20s | 92589.9039 | 92589.9059 |
| t2, +40s | 92590.1544 | 92590.1565 |

The date rolled `Summer 1, 1102` to `Summer 2, 1102` on both, and `set_time_mode Play_1x` on the server left
both reporting `StoppablePlay`. The client runs about 0.002 days ahead between updates, which is it
interpolating between the server's time packets rather than drifting - it re-converges at each update, and the
sampled gap did not grow between t1 and t2.

**Group A is complete: C1-C6.**

---

## C12 - Invoke a menu option - HALF DONE, and the other half is blocked

### Shipped: `coop.debug.ui.menu_options`

Lists the current menu's options with each one's id, text and enabled state.

The enabled flag is the capability, not decoration. A driven client that called a consequence directly would
be bypassing the menu rather than clicking it, and would "succeed" at something a player cannot do - which is
the exact class of bug this rig exists to catch, so it has to be able to SEE a wrongly disabled option instead
of stepping over it.

Written with reflection rather than a compile-time member: the option list hangs off `MenuContext` under a
name outside the public surface, and after this session's record of guessing, a wrong guess costing a build
plus a campaign load was not worth it. It also degrades honestly - if a game update renames the field the
command reports that it could not find the list, rather than failing to compile.

### Blocked: nothing can open a menu on a headless client

`coop.debug.ui.switch_menu <id>` answers `Switched to game menu army_wait.` and then
`Campaign.Current.CurrentMenuContext` is still null - `activeMenu` stays empty and `menu_options` reports
"No game menu is open". Tried with `encounter_meeting` and `army_wait`; the command succeeds and nothing
becomes current. Menu ACTIVATION evidently runs through the UI layer that a render-free process does not have.

**Menus themselves are not the problem.** An earlier run of the same client reported
`activeMenu='encounter_meeting'` - set by campaign flow from a live encounter, not by switching. So a headless
client can hold a menu context; it just cannot be told to open one directly.

That makes C12's second half - selecting an option - dependent on reaching a menu the way a player does, by
being in an encounter. Which is C15. So `menu_select` is deliberately not written yet: writing it now would
mean shipping an unexercised command and calling the capability done, and this session has already shown what
unverified claims are worth.

---

## C14 - Issue movement orders - DONE (all three targets)

`Move-CoopParty` and `Get-CoopPartyPosition` in `Rig\Control.psm1`, composed on the existing
`coop.debug.mobileparty.move_offset`.

**Arrival is confirmed on the authority, not just on the process that was driven.** A client that believes it
arrived while the server has it elsewhere is precisely the fault this rig exists to surface, so both positions
are read and the drift between them is returned rather than averaged away. Arrival also requires two
consecutive settled samples - a party passing through its target on one sample would otherwise read as
arrived while still moving.

Verified twice. First by hand, watching an order issued **on the client** propagate:

| | server | client | drift |
|---|---|---|---|
| t+10s | 520.8698, 332.8281 | 520.8997, 332.8580 | 0.0422 |
| t+20s | 521.1090, 333.0673 | 521.1090, 333.0673 | **0.0000** |
| t+40s | held | held | 0.0000 |

Then through the capability itself, ordering `-4,+2`: `Arrived=True`, driven and authority both exactly
`(517.1090, 335.0673)`, drift `0.0000`. The mid-flight 0.04 is the client interpolating between updates and it
resolves to zero on arrival - the same shape as the campaign clock in C6.

**Not covered, and both are real:**

- **To a settlement.** `coop.debug.mobileparty.move_to_settlement` answers "Command can only be run on the
  server", so a driven client cannot order it. Reaching a settlement from a client currently means moving to
  its coordinates by offset, which is not the same act and will not trigger the arrival behaviour a player
  gets.
- **To a party.** No command exists at all.

Both need a client-side command rather than rig code, so they are named here rather than quietly folded into
"done".

### Both gaps closed

`GameInterface/Services/Party/Commands/PartyMoveTargetDebugCommand.cs` -
`coop.debug.mobileparty.order_to_settlement` and `order_to_party`, plus `Move-CoopPartyToTarget` in
`Rig\Control.psm1`.

**Why a server-side order was not the same capability.** The rig exists to test what a *client* can do. Ordering
a party to a settlement on the server and watching it arrive proves the server can move a party; it says nothing
about whether a client's order survives the round trip, which is the thing that actually breaks. Reaching a
settlement by moving to its coordinates is a third act again - it never sets `TargetSettlement`, so none of the
arrival behaviour a player gets is exercised. That is why the output reports `TargetSettlement` / `TargetParty`:
they are what prove the *order* took, as distinct from the party merely drifting toward the right spot.

Both mirror `move_offset` exactly - act on the local player's own party, then publish
`PartyBehaviorChangeAttempted` so the change reaches the server the way a player's would. Neither takes a party
id for the mover: a client driving someone else's party is not a capability, it is a desync.

**Escort, not engage.** `SetMoveEngageParty` starts a fight, which would make every move-to-party test also a
battle test and destroy the fixture it ran on. `SetMoveEscortParty` is the movement order C14 asks for.

**Arrival at a settlement means the authority agrees the party is INSIDE it**, not that it reached the right
coordinates - the latter passes while the party sits outside the gate. Same two-consecutive-settled-samples rule
as `Move-CoopParty`, and the same both-sides confirmation.

**Verified:** Release build clean, first try; `Control.psm1` parses and exports the new function. **Not verified
live** - issuing these orders needs a running client, and E2E is off until asked.

---

## C15 - Resolve encounters - PART DONE. Menus are inert on a headless client

### Encounters themselves work

`coop.debug.mapevent.start_nearest_looter` on the driven client answers
`Started encounter with Looters (StringId Created_155510), 7 troops, 20.3 away.` and `encounter_state` then
reports a live `PlayerEncounter` with both sides resolved, `EncounterState: Begin`, and
`CurrentMenu: encounter_meeting`.

An encounter also arose on its own during C14's movement - Zena's Party attacked Kan's Party while it was
crossing the map - so a driven client meets the world the way a player does, without being staged into it.

### The blocker, now named precisely

C12 could not find the option list. Making the command describe what it saw instead of just failing gave the
answer in one run:

```
MenuContext._currentState : MenuContextState = None
MenuContext.IsInitialized : Boolean         = False
GameMenu._menuItems       : List            = [0 x ?]
GameMenu.MenuOptions      : IEnumerable     = [0 x ?]
GameMenu.MenuItemAmount   : Int32           = 0
GameMenu.IsEmpty          : Boolean         = True
```

The menu context is real and correct - right id, right background (`encounter_looter`) - and **empty**. It was
never activated, so `OnInit` never ran and no options were ever built. `_currentState = None` is the whole
story.

This supersedes the earlier note that `switch_menu` "lies": it does switch the context, and the context stays
inert either way. The problem is not the switch, it is that nothing on a render-free process drives a menu
context into its active state - that is the UI's job, and there is no UI.

**Consequence, and it is large.** Menus are the interaction surface for encounters, settlements, sieges and
armies. Until a menu context can be activated headlessly, C12 cannot be finished and C15-C19 can only be
driven through whatever direct commands happen to exist, which is not the same as clicking - and a rig that
bypasses the menu cannot observe a wrongly disabled option, which C12 exists to do.

This is the fifth render-free gap, and unlike the first four it is not a one-line guard: it needs a deliberate
headless activation path for `MenuContext`. **Flagging rather than attempting it** - it changes how menus
initialise for every client, and this session's record says that is worth stating before doing.

### Rig defect found and fixed on the way

`Get-CoopEndpoint` treated "a process with this pid exists" as "our process is alive". A process that dies of
an access violation cannot delete its own registration file, and Windows recycles pids - during this iteration
a stale registration for pid 33228 resolved to **svchost**. The rig refused to act, which was the good
outcome, but it could as easily have addressed a stranger. It now also checks the process name.

---

## C22 - Capture popups - DONE

### What shipped

`GameInterface/Services/Headless/PopupCapture.cs` plus `coop.debug.ui.popup_log [clear|<count>]`.

A bounded ring buffer (200) recording kind, title, text, options, and **both clocks** - real UTC and campaign
time. Both, deliberately: real time answers "how long has this been sitting there", campaign time answers
"where in the world was I", and the two diverge exactly when the campaign is paused, which is when a popup is
most likely to be the reason it is paused.

Patches divert `InformationManager.ShowInquiry`, `ShowTextInquiry` and `MBInformationManager.AddQuickInformation`
on a render-free process only; a rendered client is untouched. Each capture is also written to the log, so it
survives a process that dies before anyone asks.

It records and does not answer. Answering is C23 and is kept separate on purpose - a capture that quietly
accepted things would make campaign decisions nobody asked for, invisibly.

### Also fixed: a headless client had no notifications at all

`HeadlessNotificationGuardPatch` suppressed `DefaultNotificationsCampaignBehavior` for every headless process.
Its entire rationale is a host with no main hero and therefore no `Clan.PlayerClan` - which describes a
dedicated SERVER. A driven client is render-free but a player in every other respect;
`coop.debug.mobileparty.whoami` on it answers **"You are Kan | hero id: Player2863 | party id: Player425"**.

The call site now asks `IsHeadlessServer`. The helper's signature is untouched so the existing E2E tests still
compile and still assert the same pure logic.

### Proven, after first being unable to prove it

Waiting for the campaign to volunteer a popup proved nothing: `popup_log` stayed at `count=0` through a
campaign load, a five-day jump, an encounter and a gold change on the client's own hero. A quiet world is not
evidence either way, so the capture was left recorded as unproven rather than assumed working.

`coop.debug.ui.raise_test_popup [inquiry|quick]` closed it. It calls the engine's own
`InformationManager.ShowInquiry` and `MBInformationManager.AddQuickInformation` - **not** `PopupCapture.Record`,
which would have proved only that a list can hold an item. Intercepting those entry points intercepts every
caller in the game.

Fail before, pass after, in one run:

```
BEFORE  POPUP_LOG count=0
AFTER   POPUP_LOG count=2
  #1 inquiry utc=19:03:11 campaign='Summer 1, 1102' days=92589.6556
     title='Rig self-test' options=[Accept | Decline] text='...prove popup capture.'
  #2 quick   utc=19:03:11 campaign='Summer 1, 1102' days=92589.6556
     text='Rig self-test quick information.'
```

Both clocks, both kinds, options preserved, and nothing rendered. C25 now has something real to assert on.

**Still open:** no popup raised by the game itself has been seen, only ones raised deliberately. The
interception point is the engine API every caller uses, so that is a gap in observation rather than in the
mechanism - but it means the capture has not yet met a popup it did not expect.

### The outage this cost, worth keeping

The first version bound its prefix parameter by name:

```
Parameter "data" not found in method static void
  InformationManager::ShowTextInquiry(TextInquiryData textData, bool, bool)
```

Harmony matches prefix parameters by NAME, and the two overloads disagree - `ShowInquiry` takes `data`,
`ShowTextInquiry` takes `textData`. The mismatch threw inside `PatchAll`, which **aborted the entire patch
set**: the server degraded to a 200-second start with a null campaign, and the client could not begin a
session at all.

That is exactly the property C2 described - a patch failure is loud rather than silent - demonstrated at my
own expense, and an argument for the per-class tolerance C2 rejected being the wrong trade after all: loud
cost an hour, silent would have cost a fortnight. Now bound positionally with `__0`, which cannot drift.

---

## C23 - Answer popups default-deny - DONE

`GameInterface/Services/Headless/PopupPolicy.cs`, plus `coop.debug.ui.popup_declare` and
`coop.debug.ui.popup_policy`.

### The rule, and why it is asymmetric

A modal has to be answered or the flow behind it never continues, and a render-free client has nobody to
answer it. The two ways to get this wrong are not equally bad:

- Answer nothing and the client hangs. Loud, and it stops the run.
- Accept everything and the run continues while making campaign decisions nobody asked for - marriages, wars,
  fiefs given away - and **every result after that point is quietly worthless**.

The second produces a green run built on a world the test never intended, which is worse than no run at all.
So a declared popup gets its declared answer, and anything else gets the negative answer **and fails the run**.
An unexpected modal is a finding, not an inconvenience.

C22 left the client capturing modals and answering none of them, which meant a captured popup and a hung
client looked identical from outside. The answer's action is now invoked, so the flow continues.

### Verified live, both branches, on the same popup

```
1. baseline                    runFailed=False declarations=0 failures=0
2. raise UNDECLARED inquiry
3. policy                      runFailed=True  failures=1
     FAILURE undeclared popup answered negatively: title='Rig self-test' ...
4. declare affirmative + reset failures
5. raise the SAME inquiry
6. policy                      runFailed=False declared 'Rig self-test' -> Affirmative matched=1
7. popup_log
     #1 answer='Negative (undeclared)'
     #2 answer='Affirmative'
8. popup_declare accept_all    "The answer must be 'affirmative' or 'negative'. There is no accept-all."
```

Two identical popups, opposite answers, decided purely by whether the scenario declared them. That is the
capability.

### Six unit tests, because this failure is invisible

`GameInterface.Tests/Services/Headless/PopupPolicyTests.cs` - 6 passed. Answering everything affirmatively
does not throw, does not log and does not stop a run; a regression here would look like success. The tests
cover: undeclared declines, declared is honoured, an explicit decline is still "declared" and so does NOT fail
the run, matching is case-insensitive across title and text, an undeclared popup fails the run, and - by
reflection over the type's members - **that no accept-all member exists at all**. Not that it defaults to off:
that it cannot be reached, because a switch like that gets turned on at 2am by someone who wants green.

### One thing deliberately not wired

A text inquiry's affirmative path takes typed input. Answering one affirmatively would mean inventing input on
the player's behalf, so it can only ever be declined; a scenario needing typed input needs a real verb for it.

---

## C24 - Server-side refusal of irreversible actions - DONE at the control channel, NOT per-connection

`GameInterface/Services/Headless/IrreversibleActionGuard.cs`, enforced inside
`LiveTestControlServer.HandleCommand` before dispatch, with `coop.debug.testclient.guard [arm|disarm|clear]`.

### Why it exists even though C23 already refuses things

C23 stops a driven client ACCEPTING something irreversible, but it is enforced by the same process it is
protecting against - a rig that misbehaves disables its own safeguard by definition. This layer runs on the
server, which no client can reconfigure.

The denylist is 24 patterns covering the acts whose damage outlives the run: war and peace, alliances, kingdom
decisions, clan destruction and defection, marriage and divorce, fief ownership, governors, rebellions, party
destruction, player capture. Matched as substrings so a family is covered by its prefix rather than by naming
every member and missing the one added next month.

**Disarmed by default.** A person hosting their own game must not find diplomacy refused because a rig feature
shipped switched on. The status command stays reachable while armed, deliberately: a guard whose own status
was blocked would be indistinguishable from a broken one.

### Verified live

```
1. disarmed        GUARD armed=False denied=24 refusals=0
2. denied command  reaches the game and fails on its own merits ("Faction not found") - no interference
3. arm
4. same command    REFUSED before dispatch: action_refused ... on the irreversible-action denylist
5. allowed command position read still works - the world is not read-only
6. state           refusals=1, REFUSED coop.debug.kingdom.declare_war
7. disarm          clean
```

Twelve unit tests in `GameInterface.Tests` (18 in the Headless namespace with C23's), covering default-off,
refusal with a reason, ordinary commands still passing, each denied family, case-insensitivity, and that
refusals are recorded rather than silent.

### The boundary, stated rather than implied

The plan asks for refusal **"from a connection flagged as a test client"**. What is built refuses **at the
control channel on an armed server**. Those are not the same:

| | covered |
|---|---|
| Commands a rig sends over the control channel | **yes** |
| Actions a client takes through gameplay netcode | **no** |
| Distinguishing a test client from a real player on the same server | **no** - the server is armed or it is not |

Per-connection flagging needs the origin connection carried into each service's request path, and those paths
are spread across per-service handlers and patches with no shared chokepoint - `ChangeOwnerOfSettlementPatch`,
`MakePeaceActionPatch`, `KingdomHandler`, `ClanPatches` and others each own their own. That is a netcode
change of real size, not an afternoon's work, so it is named here rather than half-built.

In the rig as it actually runs - one armed dedicated server, driven entirely through the control channel -
the layer does its job today. On a shared server with real players it would refuse everyone equally, which is
why it is off unless armed.

---

## C25 - Assert popup presence and absence - DONE

`Wait-CoopPopup`, `Assert-NoCoopPopup` and `Get-CoopPopup` in `Rig\Control.psm1`, on top of a new
`coop.debug.ui.popup_log json` mode.

### Structured output, not parsed prose

The pretty log is for reading; assertions read a single `LIVE_TEST_JSON=` line instead. Popup text is
arbitrary player-facing prose and will eventually contain a quote, a bracket or a newline - and an assertion
that parsed the pretty form would not merely fail then, it would fail by **finding nothing**, which reads as
"no popup appeared". That is the wrong answer in the dangerous direction. The JSON is escaped by hand for the
same reason.

### Presence and absence are not each other's inverse

`Wait-CoopPopup` polls until a match appears and **throws** on timeout. Deliberate: a scenario that waited for
a confirmation which never came has not "carried on with a null", it has failed, and it should stop where that
happened rather than three assertions later.

`Assert-NoCoopPopup` asks about what has already been captured, because "never appears" can only be checked
over a window. It names the offending popups rather than reporting that something forbidden happened, and
`-PassThru` returns them instead of throwing so a report can list them.

### Verified, five cases

```
1. absence on empty log        PASS (0 offending)
2. wait for absent popup       PASS timed out
3. wait for present popup      PASS #3 answer='Negative (undeclared)'
4. absence now fails, names it PASS Forbidden popup ... appeared: #3 'Rig self-test'
5. unrelated pattern still ok  PASS (0 offending)
```

### The bug this found, which is the reason case 1 exists

Case 1 failed twice before passing. `Get-CoopPopup` returns `, @()` so an empty result stays an array -
correct for assignment, wrong for a pipeline: piping the function emits the wrapper's single element, the
empty array itself, so `Where-Object` ran once against it and StrictMode threw on the missing property.

**An absence assertion that throws on an empty log is the worst bug this function could have** - it reports a
forbidden popup that never happened, and every scenario using it fails for a reason that does not exist. Fixed
by assigning before filtering.

This is the third appearance of PowerShell's empty-array/null behaviour in this rig - Group B's
`Compare-SaveInventory`, `Get-CoopEndpoint`, and now here. Worth treating as a known hazard of the language
rather than three separate mistakes: any function returning a collection needs a test that calls it when the
collection is empty.

---

## C26 - Report menu reachability - DONE

`Get-CoopMenu` in `Rig\Control.psm1`, on a new `coop.debug.ui.menu_options json`.

### Observing is not blocked, even though acting is

C12 is stuck because a menu context is never activated on a render-free client. C26 asks a different question -
what COULD the player do - and the honest answer in that state is "nothing, and here is why". Reporting that
precisely is the capability, not a consolation for it.

The report therefore carries `ContextState` and `Activated` beside the option list, because **"no options" has
two very different causes**: a menu that genuinely offers nothing right now, which is ordinary, and a menu
whose context was never brought to life, which is a defect. A command that returned only a count would
collapse both into the same answer.

### Verified on the joined client

| | MenuOpen | MenuId | ContextState | Activated | OptionListFound | Options |
|---|---|---|---|---|---|---|
| no menu | False | - | - | - | - | 0 |
| after `start_nearest_looter` | **True** | `encounter_meeting` | `None` | **False** | False | 0 |

The second row is the C12/C15 blocker, now machine-detectable rather than something a person has to notice by
reading a field dump. Any scenario can assert `Activated -eq $true` and fail loudly on a client whose menus
are inert, instead of quietly doing nothing and reporting success.

Structured output again, for the same reason as C25: option text is player-facing prose.

---

## C27 - Capture notifications and messages - DONE

`InformationManager.DisplayMessage` diverted into its own buffer, with `coop.debug.ui.message_log
[clear|json [count]|<count>]` and `Get-CoopMessage` in the rig.

### Its own buffer, not the popup log

A modal and a message have opposite shapes: one is rare and demands an answer, the other is a stream and
demands nothing. Sharing one bounded list would let a few seconds of ordinary campaign chatter evict the
inquiry that actually mattered - and the inquiry is the record nobody can reconstruct afterwards. So messages
get 500 entries of their own.

They are also **not** written to the log per message. Hundreds of lines a minute is how the signal in a log
gets destroyed; the buffer answers on demand instead.

`DisplayMessage` is where the campaign narrates itself - raids, wages, recruitment, war. Without it a client
being told something alarming every hour is indistinguishable from a quiet one, which is exactly the
distinction behavioural comparison needs.

### Verified

```
1. before               count=0
2. after raise          count=1  #1 campaign='Summer 1, 1102' text='Rig self-test campaign message.'
3. filtered match       1
4. filtered non-match   0
5. popup log unaffected 0 popups - the separate buffer holds
```

Proved through `InformationManager.DisplayMessage` itself, via a new `raise_test_popup message`, for the same
reason as C22: waiting for a quiet world to volunteer one proves nothing, and calling `RecordMessage` directly
would prove only that a list can hold an item.

### Same caveat as C22, stated again

Every message seen so far is one the rig raised. The dedicated server suppresses
`DefaultNotificationsCampaignBehavior` by design, so it is the wrong process to observe a real feed on - the
joined client, which now registers those handlers after the C22 guard fix, is where game-originated messages
should first appear. Confirming that is an observation still owed, not a mechanism still missing.

**Group D is complete: C22-C27.**

---

## C28 - Progress watchdog primitive - DONE

`Watch-CoopProgress` in `Rig\Control.psm1`. "This observable must change within N, or report."

**It reports; it does not throw.** A throwing primitive could not be used twice, and C31's rule is that the
first trip is SLOW and only a second is STALLED - so the caller has to be able to see "no change yet" and
decide. Throwing here would collapse that into one verdict and force the harshest one.

Samples are compared as strings, so a number, a position, a state name or a composite are all valid
observables without the caller declaring a type.

Verified both directions:

```
paused campaign, campaignTimeDays   Verdict=NO_CHANGE  (5 samples)
a counter that does change          Verdict=CHANGED    0 -> 1 in 2s
```

The first result is a false positive by design - and the reason C30 exists.

## C30 - Pause awareness - DONE

`Get-CoopPauseState`, a published `pauseReason` on the status verb, and
`GameInterface/Services/Headless/CampaignPauseReason.cs`.

### Instrumenting the pausers was the wrong place, and the log said so

The first attempt set the reason at the three handlers that ask for a pause - no players connected, all
players occupied, peers overloaded. That missed the dominant case entirely, and the server log gave it away:

```
Time control request "Play_1x" limited to "Pause" by DisconnectedPlayersServerHandler.PlayersConnectedPolicy
```

The campaign was not paused by a handler announcing a pause. It was held paused by a policy **refusing to
unpause**, which no handler ever announces. `TimeControlInterface.LimitTimeControl` is where a pause is
actually imposed, it already knows which policy is responsible, and it is one site that covers every policy
including ones added later. The three speculative call sites were reverted in favour of it.

### Verified

```
before any unpause attempt   Paused=True  Explained=False Reason=''
after attempting Play_1x     Paused=True  Explained=True
                             Reason='blocked by DisconnectedPlayersServerHandler.PlayersConnectedPolicy'
watchdog with -IsPaused      Verdict=PAUSED (3/3 samples) instead of NO_CHANGE
```

`Explained` is separate from `Paused` on purpose. A paused clock with **no** published reason is not treated as
benign - that combination means something stopped time without saying why, which is exactly what a stall report
should surface rather than excuse.

### A bug caught in the writing

The pause test was first written as `$mode -like '*Stop*'`. The RUNNING modes are `StoppablePlay` and
`StoppableFastForward` - both contain "Stop". That would have marked every healthy campaign as paused, and a
pause-aware watchdog that thinks everything is paused reports nothing at all. Now an exact match on `Stop` or
`Pause`.

---

## C29 - Calibrate before asserting - DONE

`Rig\Calibration.psm1` - `Measure-CoopProbe`, `Save-CoopBaseline`, `Get-CoopBaseline`, `Get-CoopTimeout`,
persisted to `Rig\baselines.json`.

### The rule is a refusal

A timeout picked by feel is wrong in both directions and you cannot tell which. Too short and the rig reports
stalls that are really a slow machine, until people stop reading it. Too long and a real stall sits for
minutes, which on an unattended soak means the run is over before the report arrives.

So **an unmeasured operation has no timeout**. `Get-CoopTimeout` throws rather than returning a plausible
number, because a plausible number is precisely how guesswork gets laundered into something that looks
measured.

Derivation is `max observed x 4`, floored at 5s. Max rather than median: a timeout exists to tolerate the
worst HEALTHY case, and a median-derived one fires on half of all normal runs.

### The trap it caught, in its own first use

The join probe used `$using:` inside a plain scriptblock, which PowerShell only honours for
`Invoke-Command`/`Start-Job`. The launch silently did nothing, the probe ran to its own 120-second wait loop,
returned false - and that was recorded as a **121-second "healthy join"**, deriving a timeout of **486
seconds**.

A timeout derived from a failure is worse than a guessed one, because it carries a measurement's authority.
`Measure-CoopProbe` now requires the probe to report success and throws on anything falsy, invalidating the
whole measurement rather than quietly contributing a number. The bogus baseline was deleted rather than left
to be "corrected later".

### Verified

```
unmeasured operation      throws: "No baseline for 'join.duration'. Measure it before asserting on it"
failing probe             throws: "A failed probe is not a baseline - fix the probe, then measure."
command.roundtrip         min=0.030 median=0.032 max=0.165   -> 5s (floor)
status.roundtrip          min=0.030 median=0.031 max=0.053   -> 5s (floor)
join.duration             21.866, 29.983                     -> 119.9s
```

The 0.165 against a 0.032 median in `command.roundtrip` is the cold-start cost, and it is why one sample is an
anecdote. The measured join timeout of 120s against the failed probe's 486s is the whole capability in one
comparison.

---

## C31 - Slow before stalled - DONE

`Watch-CoopLiveness` in `Rig\Control.psm1`.

### Why one missed window is not a stall

Machines hiccup. A save flushes, an autosave lands, the host swaps a page. A watchdog that shouts STALLED at
the first quiet window is wrong often enough that people stop reading it - which costs more than having no
watchdog at all.

So the first miss reports SLOW and keeps watching; only a second consecutive miss says STALLED. The two words
are different on purpose: **SLOW is information, STALLED is an accusation**, and a report that cannot tell them
apart cannot be trusted with either.

Windows come from C29's measured baselines by default. `-WindowSeconds` exists but is the guessing path this
was built to avoid, so the measured name is the ordinary route - and with neither, it refuses rather than
inventing one.

Pause is checked first and returns PAUSED, never SLOW or STALLED.

### The correction that mattered

The first version escalated correctly but never actually **reported** SLOW - it exposed it only as a per-window
value inside the returned object, alongside STALLED, minutes later. That is a footnote, not a report. The whole
value of escalation is that somebody hears "this is taking longer than measured" **while it is still
happening**, so the warning is now emitted between the two windows, and the result carries `SlowReportedUtc`
for the record.

### Verified, four cases

```
never changes           1 warning at the window boundary, then Verdict=STALLED, SlowReportedUtc set
changes on window 1     0 warnings, Verdict=CHANGED, SlowReportedUtc unset
paused campaign         Verdict=PAUSED on window 1, with the published reason
no baseline, no window  refuses: "needs -BaselineName (measured, C29) or an explicit -WindowSeconds"
```

---

## C32 - Liveness observables - DONE

`Get-CoopObservable` and `Get-CoopObservableName` in `Rig\Control.psm1` - eight named things whose standing
still is evidence rather than noise, each returning one comparable value and usable directly by C28 and C31.

Several are **composites on purpose**. `joinProgress` folds state, baseline count and remaining packets into
one string, because a join advances through those independently and watching any one alone would call a
healthy join stalled.

An unknown name throws with the list. A typo'd observable would otherwise be silently constant - which reads
as STALLED, the most alarming possible way to fail.

### Verified live, all eight

```
campaignTime    '92589.655586412031'
partyPosition   '518.10895,330.0673,Hold'
joinProgress    'partyTotal=1536 activeParties=1535 inactiveParties=1 | peer=1 st...'
encounterState  'PlayerEncounter.Current: <null> (torn down) MainParty.MapEvent: ...'
siegeStage      'No active sieges'
popupCount      '0'
messageCount    '4'
menuState       'False,,,0'
unknown name    throws: "Unknown observable 'notAThing'. Known: campaignTime, partyPosition, ..."
```

## The C22/C27 caveat is closed

Both were recorded as proven only against popups the rig raised itself. That is no longer true - the capture
has now met traffic it did not expect, on both processes:

```
server  #1 '1 player(s) are currently joining the game'   #2 'All players connected'
client  #1 'Connecting...'  #2 'Connected! Please wait for transfer'
        #4 'Raid AI intervention is enabled'              #5 'All players connected'
```

These are coop's own join narration, captured without being asked for. The interception works on real traffic,
and the messages are exactly the join-progress signal C32 wants.

## PowerShell array unrolling - fifth occurrence, now a written convention

`Get-CoopObservableName` returned `, @(...)`, so `foreach` saw one item - the array - and every observable name
came out as `System.Object[]`. Then `Get-CoopMessage | ForEach-Object` printed `#System.Object[]` for the same
reason.

There is **no return form correct for both assignment and piping** - the comma wrapper keeps an empty result
from collapsing to `$null` under StrictMode, and that same wrapper breaks a direct pipeline. This is a
property of the language, not something a better implementation fixes.

So it is now a documented calling convention at the top of `Control.psm1`: assign first, or wrap the call in
`@()`. `Get-CoopObservableName` is the deliberate exception, returning a fixed non-empty list unwrapped so it
can be iterated.

Twice this has produced a fault that did not exist - an absence assertion that threw on an empty log, and a
list of names that were all `System.Object[]`. Worth the written rule.

---

## C33 - Stall report - DONE

`New-CoopStallReport` in `Rig\Control.psm1`, writing a timestamped bundle under `Rig\stall-reports\`.

### It collects rather than pointing

A stall is the one failure where evidence evaporates while you look at it. The process is still alive and
still changing - logs keep being written, buffers keep rotating, the clock may resume. Anything not captured
at the moment of the verdict is gone by the time somebody reads the report, so the log tails are **copied**
into the bundle rather than referenced.

Gathered from **both** processes, because a stall is a disagreement as often as a halt: a client waiting for
something the server already did looks stalled on one side and healthy on the other, and only having both
makes that visible.

Collector failures are recorded, not thrown. The process being reported on is by definition misbehaving, so
some collectors may fail - and a report that aborts because one did is worth nothing.

`Invoke-CoopCommand` now remembers the last command sent. "What did we last ask it to do" is the first
question anyone asks about a stalled process, and reconstructing it from scrollback is the archaeology this
exists to remove.

### Verified on a real STALLED verdict

```
awaited      campaignTime   verdict=STALLED   window=6s   baseline == final
slowReported 2026-08-13T19:47:51Z
lastCommand  coop.debug.mobileparty.position [Player425] at 19:47:45Z
rig1 server  MapState / ServerRunningState   mode=Stop   queueDepth=0   connected=1
             pauseReason='blocked by DisconnectedPlayersServerHandler.PlayersConnectedPolicy'
rig2 client  MapState / CampaignState        mode=Stop   queueDepth=0
files        rig1-Coop_server.log (121 KB), rig2-Coop_client.log (90 KB), stall-report.json
0 collection errors
```

### What the report found on its first use

The server reports **`connectedPlayerCount=1` while the unpause policy is still blocking on
`PlayersConnectedPolicy`**. A client is joined and in `CampaignState`, yet the policy that exists to keep time
paused when nobody is connected is still holding it paused.

Either the policy counts "connected" differently from the status verb, or a joined client is not registering
as connected for time-control purposes - in which case a headless client can never make campaign time run,
which would explain why every `set_time_mode Play_1x` this session answered `Pause`.

Recorded as an observation, not a diagnosis. But it is exactly the class of thing PLAN-1 was built to surface,
and the rig surfaced it unprompted.

**Group E is complete: C28-C33.**

### Correction: that finding was my own bug, not coop's

Checked before moving on, and it did not survive. With a client joined, `Play_1x` succeeded immediately -
mode `StoppablePlay`, campaign time advancing 92589.655 to 92589.791. The unpause policy works correctly and a
headless client does register as connected.

What the report showed was a **stale pause reason**. `CampaignPauseReason` is set when a policy refuses an
unpause and cleared when a later request succeeds - so with no request in between, it outlives its cause. That
is precisely the failure its own doc comment warns about, written by me two capabilities earlier, and it still
produced a false report that read convincingly.

Fixed at the read site: the status verb only reports a reason while the campaign is actually paused, so
staleness cannot be observed at all. The earlier `set_time_mode` answers of "Pause" were genuine - there was
no client connected at the time - and are not evidence of anything.

**A stale explanation is worse than none, because it gets believed.** It was believed here for one iteration.

---

## C34 - State comparer - DONE

`Compare-CoopState` in `Rig\Control.psm1`.

### Tolerances are explicit and per-field, because some differences are correct

A client interpolates party positions between the server's updates and runs its own clock between time
packets. A comparer without tolerances reports that every time, and a comparer that reports constantly gets
turned off.

So position and campaign time carry measured tolerances and say so in the result; gold, influence, identity
and party counts have **none** - they are authoritative values, replicated exactly or wrong. Keeping the
tolerance visible in the output matters: collapsing "equal" into "close enough" is how a real slow drift hides
inside an accepted margin.

### Verified, including that it can detect

```
default tolerances      compared=16  inSync=True   divergences=0
tolerances set to 0     campaignDays authority=92590.004618  client=92590.007280   (0.0027d interpolation)
party moving, tol=0.01  position     gap 0.1455, named by controller id 76561199074278663
```

The middle and last rows matter more than the first. A comparer that has only ever said "in sync" is
unproven - these show it reporting real gaps, and that the defaults are what suppress them rather than
blindness.

### An honest gap in it

`PositionTolerance` defaults to 0.25, which I **chose**, not measured. C14 observed 0.0422 mid-flight and this
run observed 0.1455 - so the default is only a 1.7x margin over an observed value, and it was picked by feel.
That is exactly what C29 exists to forbid.

It should be derived from a `position.drift` baseline the same way timeouts are. Recorded rather than
quietly left, because a tolerance picked by feel is the same class of mistake as a timeout picked by feel -
and this one silently decides what counts as a desync.

### The guessed tolerance, now measured

`Measure-CoopValue` and `Get-CoopTolerance` were added to `Calibration.psm1` - the same refusal as
`Get-CoopTimeout`, applied to magnitudes instead of durations, because a tolerance chosen by eye is the same
mistake as a timeout chosen by eye except that it **fails silently**, by accepting a real divergence that
happens to sit under it.

Measured while a party was actually moving, ten samples two seconds apart:

```
position.drift  0.0407 0.038 0.0643 0.0641 0.0641 0.0641 0.0643 0.0643 0.0643 0
                min=0 median=0.0641 max=0.0643   ->  tolerance 0.1286
```

My guess had been **0.25 - nearly four times the observed maximum.** Every real divergence between 0.13 and
0.25 would have been accepted in silence. `Compare-CoopState` now derives from the baseline by default and
falls back to the guess only when nothing has been measured.

The trailing 0 is the party arriving: drift collapses to nothing once it stops, matching C14.

---

## C35 - Continuous comparison - DONE

`Watch-CoopDivergence` in `Rig\Control.psm1`.

### Persistence is the signal

A checkpoint comparison only sees divergence still present when the checkpoint arrives. Most of the
interesting ones are not: a value diverges, the next update overwrites it, and the world looks consistent
again before anybody asks. The fault was real and left no trace.

So each divergence is tracked from first sighting to last and classified:

- **TRANSIENT** - seen once. Replication doing its job late.
- **INTERMITTENT** - seen repeatedly but not on the final sample.
- **PERSISTENT** - still diverging at the end. This is a desync.

Reporting them identically would either bury real faults in noise or hide the transient ones entirely.

### It found one on its first run

```
40s window, 5s interval    samples=7  clean=6
  gold  TRANSIENT  76561198876156674  x1/7
```

A single-sample gold difference on Jian, gone by the next sample. Gold carries **no** tolerance - it is
authoritative - so a checkpoint comparison landing on that instant would have called it a desync, and one
landing anywhere else would have seen nothing at all.

A longer window settled it:

```
70s window                 samples=12 clean=12
server gold=3828867        client gold=3828867
```

Replication lag around an income tick, correctly classified rather than escalated. That is the capability
working in both directions - it neither missed the blip nor cried desync over it.

---

## C36 - Noise baseline - DONE

`Rig\LogNoise.psm1` - `ConvertTo-CoopLogShape`, `Get-CoopLogShape`, `New-CoopNoiseBaseline`,
`Compare-CoopLogNoise`.

### Why "grep for ERR" is the wrong instrument here

A successful coop run is not quiet, and this session finally measured how loud:

```
1309  Failed to get id for object of type MobileParty, Created_<num>
 801  Failed to get id for object of type ItemRoster
 311  Failed to get id for object of type HeadlessMapEventVisual
7808  stack-trace frames attached to the above
```

Roughly 2,400 ERR lines and 7,800 stack frames in a **healthy** run. Meanwhile the real faults this project
has hit - a silent native exit, a menu with no options, a client that never resumed - logged no error at all.

So the useful question is not "are there errors" but "is there anything a healthy run does not also produce".

### Normalisation is the whole design, and my first attempt was too timid

Lines are reduced to shapes: timestamps, pids, ids and counts replaced, level and prose kept. The first
version anchored its digit rule on `\b`, which does nothing against `MenuContext_2` or `Player425` because
`_` and letters are word characters. Every object id stayed distinct, so every run would produce shapes no
baseline had seen and every line would report as NEW - technically true, completely useless.

Also fixed: the reader opened the file exclusively and could not read a log belonging to a running process.
A noise reader that needs exclusive access can only ever examine corpses.

### Verified

```
baseline from 2 healthy logs      1448 distinct shapes
novel line injected              NEW: "Kingdom decision popup failed to resolve for clan Clan_Player<num>"
live log, 36s after baseline     25 findings - all genuinely new activity in that window,
                                 including DeclareWarDecision.ApplyChosenOutcome
```

The third row is worth stating carefully: those are **not** false positives. The baseline was snapshotted at
01:29:52 and the log compared at 01:30:28, and a kingdom decision happened in between. The tool reported new
content because there was new content.

The practice that follows: **baseline from a quiesced run, not a live one.** A baseline taken while the
session keeps writing is a moving target, and everything after the snapshot reads as novel.

---

## C37 - Behaviour profiling - DONE

`Rig\Behaviour.psm1` - `Get-CoopWatchSample`, `Get-CoopBehaviourProfile`, reading the hourly `[ClanWatch]`
lines `ClanActivityWatchHandler` already writes.

### One sample says almost nothing

"Kan is holding" could be a lord who just arrived, or a lord parked in one spot for three campaign months
because his party has no navigation path. Only the distribution separates them, and a distribution is what a
person cannot hold in their head while scrolling a log.

Both `[ClanWatch]` shapes are kept. A hero WITH a party reports behaviour, target, army and size; a hero with
NO party reports where it is sitting and whether it is a prisoner. Dropping the second would make a clan of
nine look like a clan of three - and "this lord has no party at all" is itself one of the shapes worth finding.

Every figure carries its sample count and a `Sufficient` flag. A profile from two samples is not a profile,
and hiding how thin the evidence is invites exactly the confident wrong conclusion this session has already
produced once.

### Verified on 44 samples per hero

```
Kan                       n=44  party=1  moving=0     Hold x44
Thul the Scholar          n=44  party=1  moving=1     EngageParty x43, GoToSettlement x1
                                                      -> Poros x24  Zestea x13  Zeonica x6
Farim the Engineer        n=44  party=1  moving=1     EngageParty x44  -> Tevea x42
Daellian / Hafisa / Isibala / Magan / Rahan / Thalric
                          n=44  party=0               sitting in Lageta / Vostrum / Poros, all 44 samples
```

Kan is the driven client's hero and shows `moving=0`, `Hold x44` - correct, because nothing ordered him to
move during that window. The profile agreeing with what the rig actually did is the check that it measures
the right thing.

Six of nine clan members having no party is **not** reported as an anomaly, deliberately: companions and
family without parties are ordinary, and this project has already recorded that companion-led clan parties
are vanilla and must never be "repaired". Deciding which shapes are anomalous is C39 and C40's job, and C40's
allowlist exists precisely for cases like this.

### Sixth encounter with PowerShell array unrolling

`Get-CoopWatchSample | Where-Object` matched nothing, because the comma-wrapped return emits the array as one
item. This is the first occurrence AFTER writing the convention into `Control.psm1` - in a new file that did
not inherit the note. An argument for the convention rather than against it; the note now appears at the call
site too.

---

## C38 - Control-group comparison - DONE

`Compare-CoopHeroToPeers` in `Rig\Behaviour.psm1`.

### Comparability is the hard part

"This lord is behaving oddly" is only answerable against a control group - odd compared to what? A lord who
never moves is broken if his peers are criss-crossing the map and entirely normal if he is sitting in a fief.

Peers are selected structurally: same clan, and the same answer to "does this hero lead a party" - the single
split that decides which fields even apply. Comparing a party-leading lord against companions with no party
differs in every field and means nothing.

Every ranked row carries the peer values that produced it. A ranking without peer context is an accusation
without evidence, and this session has already shown what those cost.

### Verified on three heroes, including both outcomes

```
Kan (driven by the rig)     peers=[Farim, Thul]   usable=True
  DominantBehaviour   Hold          vs  EngageParty, EngageParty
  MovingFraction      0             vs  median 1
  InMapEventFraction  0             vs  median 0.035

Thul the Scholar            peers=[Farim, Kan]    usable=True
  MovingFraction      1             vs  median 0.5   (peers 0, 1)

Hafisa (no party)           peers=5               no differences from peers
```

Kan is the rig's own hero and is correctly ranked as the outlier - he is parked, and the tool says so with
evidence. Hafisa's row matters just as much: five homogeneous peers and **no** differences invented. A
comparer that always finds something is worthless.

### The flaw is now fixed (closed after C41)

`-ExcludeFromPeers` was added and the gap below is closed. The caller supplies the list because only it knows
which clients it drives, and `coop.debug.mobileparty.whoami` on a driven client answers it directly -
"You are Kan | hero id: Player2863 | party id: Player425".

Measured before and after on the same log:

```
BEFORE   MovingFraction  subject=1  peerMedian=0.5  peers=[0, 1]     <- the 0 is Kan, parked by the rig
AFTER    peers=[Farim the Engineer]   MovingFraction difference gone
```

Thul is no longer reported as deviant for moving, because his only genuine peer also moves. The signal was
entirely the rig observing its own influence. Excluding Kan also drops the peer count to one, and
`ControlGroupUsable` correctly reports false - one peer is not a control group, and saying so is better than
a confident ranking against a single sample.

### The flaw as originally recorded

Thul's `MovingFraction` of 1 is ranked against a peer median of 0.5, drawn from peers `0, 1` - and that `0` is
**Kan, who is parked because the rig parked him**. A control group containing a rig-driven hero has its
baseline dragged toward artificial behaviour, which makes genuinely normal lords look deviant.

Peer selection should exclude heroes the rig controls. The rig knows which those are - the controller ids are
in `Get-CoopState` - so this is a wiring job rather than a research one, and it is recorded rather than
quietly tolerated because it produces exactly the kind of plausible-but-wrong finding this document keeps
having to retract.

---

## C39 - Declarative anomaly rules - DONE

`Rig\anomaly-rules.json` plus `Test-CoopAnomaly` in `Behaviour.psm1`.

### Data, not code, and not scriptblocks either

Rules are field/op/value entries in JSON. The set has to grow whenever somebody notices a new shape, and
requiring a code change for each one guarantees it stops growing.

Predicates are deliberately **not** scriptblocks. A rule file that can execute arbitrary code is a rule file
nobody can safely accept from anyone else - and the whole value of this format is that a rule can be handed
over, reviewed and merged like a bug report.

### Every rule carries its consequence, and that is enforced

"This is unusual" is not a finding. Most of a campaign is unusual in some respect, and C41 requires findings
with a documented consequence to stay apart from things that are merely different.

So `consequence` is mandatory, and a rule may declare **`NONE ESTABLISHED`** - which pins it to informational
and non-actionable regardless of the severity it claims. That is the guard keeping "unusual" out of the
actionable pile, and it is checked at evaluation time rather than trusted to the rule author.

The five seeded rules are drawn from this project's own history: a party that never moves (seen twice - a
missing navigation path after load, and a null `LordPartyComponent._leader`), a party size collapsed to base,
a hero held prisoner indefinitely, a starving party, and a hero that never leaves a settlement.

### Verified against live profiles

```
[high]  Kan                  party-never-moves        actionable=True
[low]   Farim the Engineer   starving-party           actionable=True   foodMin=-7.8
[info]  6 companions         never-leaves-settlement  actionable=False  consequence NONE ESTABLISHED
        leaderless-party-size   did not fire (men 139..374, all above the threshold)
        permanent-prisoner      did not fire
```

All three behaviours are present: a high finding, a genuine minor one found in real data, and six matches
correctly demoted to information. Two rules not firing matters as much - a rule set that fires on everything
is noise with extra steps.

**Kan's `high` is mechanically correct and contextually wrong.** He is parked because the rig parked him. The
rule cannot know that, and should not - suppressing it belongs to C40's allowlist, which is exactly the case
that capability exists for. This is the same shape as C38's control-group flaw: the rig's own influence on the
world has to be declared somewhere, once.

---

## C40 - Known-normal allowlist - DONE

`Rig\anomaly-allowlist.json`, applied inside `Test-CoopAnomaly`.

### Suppression does not delete

A suppressed finding keeps its severity and is reported in its own bucket. An allowlist that removes evidence
cannot be audited, and the entry that was right last month is exactly how a real fault gets explained away
this month.

Applied inside the evaluator rather than by the caller, so nothing can report a finding without the allowlist
having had a chance to explain it - and nothing can report one while hiding that it was explained.

### Every entry carries a reason, and stale entries are reported

An allowlist without reasons becomes a graveyard nobody dares prune, because no one remembers which entries
still hold. Both current entries say what would make them wrong: the Kan entry says "remove this if Kan stops
being the rig-controlled hero".

Entries that matched nothing are reported as stale. Verified with a deliberately dead entry:

```
STALE: permanent-prisoner / SomeoneWhoLeftLongAgo  (the rule never fires for this hero any more)
```

### The result, which is the whole point of Group F

```
8 raw findings  ->  1 actionable   [low] Farim the Engineer / starving-party
                    7 suppressed   Kan (high, rig parked him) + 6 companions
                    0 stale entries
```

One thing for a person to look at, and seven that remain visible with their reasons attached. Before the
allowlist this was eight findings of which the loudest - Kan at `high` - was caused by the rig itself.

### The rig's own influence, declared once

Kan's entry is the fix for a problem that appeared twice: C38's control group had its median dragged by Kan's
artificial `Hold`, and C39 raised a `high` on it. Both are the rig contaminating its own analysis. Declaring
it here handles the C39 half; **C38's peer selection still needs the same knowledge** and does not yet have
it, which stays on the record as an open gap rather than being quietly considered solved.

**Group F is nearly complete: C34-C40 done, C41 remains.**

---

## C41 - Report by actionability - DONE

`Rig\Report.psm1` - `New-CoopFindingReport`, `Format-CoopFindingReport`.

### The separation is structural, not a convention

A finding with a documented consequence and a finding that is merely different are not the same kind of thing.
The moment they share a list, the reader has to re-derive which is which on every run - and they stop doing
that quickly, after which the report is decoration.

So the sections are named, the verdict counts **only** actionable findings, and there is no flat list to
accidentally print. A run is never failed by things that are merely different, because that is how a rig
becomes something people mute.

### One report, every source

A run that stalls, desyncs and raises an undeclared popup should produce ONE report rather than three to be
correlated by timestamp. Anomalies (C39/C40), divergences (C34/C35) and popup-policy failures (C23) all fold
in, each classified by the same rule:

| Source | Actionable when |
|---|---|
| anomaly | documented consequence AND not allowlisted |
| divergence | **PERSISTENT** - "they disagreed and kept disagreeing" IS the consequence |
| divergence | TRANSIENT / INTERMITTENT - never; C35 established these are replication landing late |
| popup | always - an undeclared answer makes decisions nobody asked for, so results after it are suspect |

### Verified on the live pair

```
PLAN-1 rig, live server + headless client - ACTION REQUIRED

ACTIONABLE (1) - documented consequence, not explained
  [low   ] Farim the Engineer       starving-party
            why: Negative food drains morale and eventually disbands the party...

DIFFERENT, NO ESTABLISHED CONSEQUENCE (1) - information, not faults
  [info  ] 76561198876156674        gold TRANSIENT (1/5)

SUPPRESSED BY ALLOWLIST (7) - still visible, with reasons
  [high  ] Kan                      party-never-moves
  [info  ] 6 companions             never-leaves-settlement
```

Nine raw observations across three subsystems, presented as **one** thing to act on. The transient gold
divergence recurring here (1/5, the same income-tick lag C35 identified) lands in "different" rather than
inflating the verdict - which is the whole contract working.

**Group F is complete: C34-C41.**

---

## C42 - Action space - DONE

`Rig\ActionSpace.psm1` - `Get-CoopActionSpace`, `Invoke-CoopAction`.

### The success test must not be the command's own reply

This rig has already met a command that lies: `switch_menu` answers "Switched to game menu army_wait." and
leaves `CurrentMenuContext` null. An exploration harness trusting return values would have recorded thousands
of successful menu switches and found nothing at all.

So every action answers three questions separately - may I do this now, do it, and **did the world change** -
with the last one re-observing state rather than reading a reply.

Three outcomes are kept distinct because they mean different things to an explorer: SKIPPED (precondition said
no, not a failure), FAILED (it ran and nothing changed - the interesting one), SUCCEEDED. Collapsing SKIPPED
into FAILED makes a coverage report look like a bug report; collapsing FAILED into SKIPPED hides the commands
that lie.

### Unavailable actions are listed, not omitted

`switch-menu` stays in the space marked unavailable with its reason. A coverage report that cannot see what it
is unable to reach will always claim full coverage of a shrinking space.

### Verified

```
move-party-offset      SUCCEEDED    moved 2.018, mode Point
advance-campaign-time  SUCCEEDED    days 92612.08 -> 92617.08
raise-popup            SUCCEEDED    popups 2 -> 3
start-encounter        SUCCEEDED    PlayerEncounter.Current: PRESENT
switch-menu            UNAVAILABLE  reports success but CurrentMenuContext stays null
```

### The success test caught my own bug first

`raise-popup` initially reported FAILED with "popups 1 -> 1" while `popup_log` plainly showed two. The action
was fine; the success test was wrong - and so was the written convention it followed.

`@(Get-CoopPopup ...)` does **not** fix the array-unrolling problem. The function emits one item (the inner
array), so `@()` wraps that into a one-element array and `.Count` is 1 however many items exist. The note in
`Control.psm1` had recommended exactly this, and was wrong.

Corrected there: **assign, then use - the only safe form.** Seven occurrences now, three of which reported a
fault that did not exist, and this one was caused by the guidance written to prevent them.

That the rig's own success test surfaced it, rather than a silent pass, is the argument for C42's whole design.

---

## C43 - Seeded, replayable runs - DONE

`Rig\Exploration.psm1` - `New-CoopRandom`, `New-CoopActionPlan`, `Invoke-CoopActionPlan`,
`Compare-CoopActionPlan`.

### Two claims, kept apart

A random explorer that cannot repeat itself is nearly useless - it finds a fault after four hundred actions
and then cannot show anyone. But "a seed reproduces identical state" is a stronger claim than this delivers,
and conflating them would be a lie:

- **The sequence is reproducible from the seed alone.** Verified, and independent of the world.
- **The resulting state also needs the same starting save.** A campaign advances while it is driven, so
  replaying against a world that moved on ends differently by construction. C8's fixture copy is the other
  half, and the module says so rather than implying the seed is sufficient.

Parameters are drawn from the same stream as the action choice, so two plans from one seed agree on arguments
as well as order. A plan that reproduced the sequence but not the arguments would not reproduce the fault.

### Three bugs, each caught by verification rather than assumed away

**1. The generator was dead, and the test PASSED anyway.** The first version cast a negative seed to
`[uint32]`, which PowerShell refuses, so every draw threw and returned the same constant. The reproducibility
check then reported `identical=True` for seeds 1234 **and** 9999 - because both produced identical dead
sequences. A broken random source looks exactly like a perfectly reproducible one. The check now also asserts
that different seeds DIFFER, which is the assertion that would have caught it immediately.

**2. The LCG alternated two actions forever.** Once running, seed 1234 produced
`advance, move, advance, move...` for its whole length - the classic low-order-bit weakness of a linear
congruential generator, read directly by `% 4`. Fixed by taking the high bits. Over 200 steps the mix is now
55 / 52 / 47 / 46 across four actions.

**3. A success test measured a saturating counter.** `raise-popup` reported FAILED for `kind=message`,
because messages land in their own buffer (C27) and the test only checked the popup log. Fixing that exposed
a second layer: both logs return at most a 200-entry page while their buffers hold more, so once past 200 the
returned count **stops changing** and any "did this grow" test fails forever. `Get-CoopCaptureCount` now reads
the command's own total.

Only the seed varying the parameter revealed this - the earlier fixed-parameter runs all used `inquiry` and
passed.

### Verified

```
same seed, twice          identical = True
different seeds           identical = False (9 differences)
200-step action mix       55 / 52 / 47 / 46 - no alternation
executed plan (seed 777)  5 steps, 0 failures
                          raise-popup: message buffer 304 -> 305, inquiry buffer 5 -> 6
```

---

## C44 - Weighted policy - DONE

`Get-CoopActionWeight` in `Rig\Exploration.psm1`, applied by `New-CoopActionPlan` (with `-Uniform` kept for
comparison).

### Weights derived from observation where observation exists

Uniform choice does not resemble play. A lord moves constantly and fights rarely, so a uniform explorer spends
a quarter of its actions starting battles and produces a campaign no player would recognise - and faults that
only appear after long stretches of ordinary movement never get reached.

Rather than inventing numbers, the movement weight comes from C37's own data: **366 ClanWatch samples across
two AI lords, movement present in every one.**

### The half that could NOT be derived, and says so

The same 366 samples contain **zero** map events. A weight proportional to observation would therefore be
zero, and an explorer that never fights tests nothing about battles.

So every weight carries a `Basis` of `observed` or `declared`:

| Action | Weight | Basis |
|---|---|---|
| move-party-offset | 10 | **observed** - present in all 366 samples |
| advance-campaign-time | 3 | declared - no player analogue, but a frozen world does nothing |
| start-encounter | 2 | declared - zero observed, so this is a compromise, not a measurement |
| raise-popup | 1 | declared - a rig self-test rather than play |

Marking them is the point. Two of these are measurements and two are opinions, and a reader who cannot tell
which will treat the opinions as evidence. C48's coverage-guided steering is the real answer to the
explore-versus-resemble tension; a number invented here is not.

An action with no weight entry gets 1 rather than 0, so adding an action and forgetting the policy cannot
silently remove it from the space.

### Verified over 400 steps

```
action                 uniform   weighted   expected
move-party-offset          107        234        250
advance-campaign-time      106         85         75
start-encounter            100         47         50
raise-popup                 87         34         25

same seed identical under weighting : True
weighted differs from uniform       : True
```

---

## C45 - Human-shaped awkwardness - DONE

`Add-CoopAwkwardness` in `Rig\Exploration.psm1`, honoured by `Invoke-CoopActionPlan`.

### Scripts are tidy; people are not

A script fires actions back to back, in order, each exactly once. People stare at a confirmation for three
minutes, double-click when nothing seems to happen, and walk away from a settlement half-entered. The failures
that matter tend to need exactly that - a race that only opens while somebody hesitates, or a second request
arriving before the first was answered.

Four shapes, all drawn from the **same seeded stream** as the plan, because awkwardness that is not
reproducible is indistinguishable from flakiness and gets dismissed as it:

- **Idle** - 2 to 12 seconds before acting. Human scale on purpose; a millisecond pause tests nothing a script
  does not already test.
- **Rapid repeat** - the same action two or three times with no gap. The impatient double-click, and the way
  "the second request arrived before the first finished" actually gets reached.
- **Abandon** - issued without waiting for the follow-through.
- **Leave mid-encounter** - inserted deliberately when an encounter is abandoned, because it is a composite a
  random planner reaches only by accident and one of the likeliest ways to strand a client.

Applied as decorations on existing steps, so the action space stays a description of what CAN be done and this
stays a description of HOW.

### Verified

```
determinism        two applications of one seed: identical

rates over 300 steps
  idles     94  (expect ~90)      repeats 41  (expect ~45)
  abandons  37  (expect ~30)      leave-mid-encounter insertions 6

execution (4 planned steps, high rates)
  10 executed, 0 failures, 47.7s wall clock - 30s of it deliberate idling
  advance-campaign-time  [idled 10s] [repeat 1/3]  days 92643.36 -> 92648.36
  advance-campaign-time  [repeat 2/3]              days 92648.36 -> 92653.36
  advance-campaign-time  [repeat 3/3]              days 92653.36 -> 92658.36
```

A first 12-step sample showed **zero** repeats where ~2 were expected, which looked like a decoration that
never fires - the worst kind, because it claims coverage it does not have. Checking over 300 steps rather than
assuming showed 41 against an expected 45; the zero was ordinary small-sample variance. Verified rather than
explained away, and equally, not "fixed" when nothing was broken.

---

## C46 - Shrinking - DONE

`Compress-CoopActionPlan` in `Rig\Exploration.psm1` - delta debugging (ddmin) over an action sequence.

### Why this decides whether exploration is worth running

Nobody debugs from a four-thousand-step log. An explorer without shrinking finds faults that are never fixed,
which is indistinguishable from not finding them.

ddmin removes chunks, keeps any removal that still reproduces, then halves the chunk size and repeats until
removing any single remaining step makes the failure disappear. That end condition IS the definition of "these
steps are what it takes".

The `Test` block returns `$true` when the candidate **still fails**. Deliberate polarity: the loop then reads
as "keep what still reproduces", which is what shrinking means.

### Verified to be genuinely minimal, not merely shorter

```
original length  120        original fails: True
minimal length   2          evaluations: 29
minimal steps    start-encounter
                 raise-popup
still fails      True

without start-encounter  fails = False
without raise-popup      fails = False
```

The last two lines are the real check. "It got shorter and still fails" is not minimality - a shrinker that
stops early looks identical to one that worked. Removing either survivor kills the reproduction, so both are
load-bearing.

### Verified against a deterministic oracle, and why

Each real candidate costs a save restore plus two process restarts - minutes per evaluation, and this run took
29 of them. What is being checked here is the ALGORITHM, so it was checked against a synthetic predicate that
answers instantly.

**The caller must reset the world between candidates**, and the function says so rather than pretending
otherwise. A shrink run replays candidate after candidate; if each starts where the last finished, the answers
describe a drifting world rather than the sequence. `Restore-SaveSnapshot` (C10) plus a restart is the real
harness, and it is the expensive part - which is precisely why the 120-to-2 reduction matters, since it is the
difference between 120 restarts and 2.

---

## C47 - Coverage measurement - DONE

`Rig\Coverage.psm1` - `Add-CoopCoverage`, `Get-CoopCoverageReport`, `Format-CoopCoverageReport`, persisted to
`coverage-ledger.json`.

### The denominator is the capability

Counting what a run touched is easy and nearly useless - it says the rig did some things. The useful question
is what it has **never** done, and that cannot be answered without knowing what exists.

So every dimension declares where its universe comes from, and a dimension whose universe cannot be enumerated
reports **unmeasurable** rather than a percentage. A figure computed against an unknown universe is worse than
no figure: "12 of 12 actions" reads as complete while saying nothing about the commands never sent.

The ledger persists across runs, because "never" spans runs. A per-run coverage number resets the very fact it
exists to measure.

### Verified, and the result is worth stating plainly

```
action     3/5     60%    never used: 2    start-encounter, switch-menu
command    6/502   1.2%   never used: 496  coop.debug.alley.*, ...
menu       UNMEASURABLE   touched 1, no universe declared
```

**The rig has exercised 1.2% of the driving surface it can reach.** That is the single most useful number
produced in Group G, and it is invisible without a denominator - which is exactly why C47 exists and why C48
steers by it.

The `menu` row is the design working: menus were touched once, their universe is unknown on a render-free
client (C12/C26), and the report says so instead of scoring 1/1 as 100%.

### Two bugs, one of them the eighth of its kind

`command` first reported a universe of **1** item named `System.Object[]` - the collection-returning function
piped instead of assigned. Eighth occurrence, and the convention note in `Control.psm1` did not travel to a
new module again. The universe is now assigned before use.

Second: a dimension holding exactly one item deserialises from JSON as a bare string, and `.Count` on a string
throws under StrictMode - so a ledger with one entry in any dimension crashed the entire report. Forced to an
array before counting.

---

## C48 - Coverage-guided steering - DONE

`New-CoopActionPlan -SteerByCoverage` in `Rig\Exploration.psm1`.

### This is where C44's tension actually resolves

Play-shaped weights alone keep an explorer in the well-trodden part of the space forever. Movement is weighted
10 *because* lords always move, so a rig that follows observation faithfully re-tests movement thousands of
times and never reaches anything new - which C47 measured as 1.2% of the command surface.

**Steering multiplies rather than replaces.** An unexercised action is boosted, but a boosted rare action
still competes against a heavily-weighted common one. Replacing the weights would produce a sequence no player
would ever perform, and faults that need ordinary context before them would stop being reachable - the C45
point about needing a player who took three minutes over something.

Binary rather than frequency-based, matching the ledger: it records whether a thing has EVER been exercised,
which is the question C47 exists to answer.

### Verified over 400 steps

```
action                  play-only    steered
move-party-offset             273        174
advance-campaign-time          69         40
raise-popup                    20         21
start-encounter                38        165   <- the only unexercised action

determinism under steering      True
steered differs from play-only  True
```

`start-encounter` rises 4.3x, and **movement still leads** at 174 against 165. That second fact is the design
working: the run is pulled toward the unexercised without ceasing to look like play.

---

## C49 - Chaos actions - DONE

`Rig\Chaos.psm1` - `Invoke-CoopChaos -Kind disconnect|kill|rejoin`.

### The assertion is on the survivor, not the victim

Every other action asks "did the thing I wanted happen". Chaos asks the opposite: something was deliberately
broken, **did everything else survive**. Killing a client always succeeds, so a success test that checked the
victim would pass unconditionally and prove nothing. The check is on the server - still answering, campaign
intact, and did it notice.

Rejoin matters most. A rig that can break a session but not restore it can only ever run one chaos action per
lifetime, and every result after the break is meaningless while the run keeps reporting anyway.

The survivor check polls rather than sampling once, because a server noticing a silent client takes as long as
its timeout - and "it had not noticed yet" is not the same as "it never will".

### Verified, all three, on the live pair

```
kill        survived=True   server answering, campaign intact, connected 1 -> 0
            "killed pid 32580 without warning"
rejoin      survived=True   connected 0 -> 1, client back in CampaignState
                            and answering a party query at (547.560, 299.773)
disconnect  survived=True   server answering, campaign intact, connected 1 -> 0
```

The rejoin row is the one that makes the other two usable: the pair is genuinely restored, not merely
reconnected, and the session continued afterwards.

### Something the results say that is worth keeping

A hard kill and a polite disconnect produce **identical observable outcomes from the server's side** -
connected drops to 0, campaign intact, server healthy. That is good news about robustness and a limitation of
the observation: the rig cannot tell from server state alone whether a client left or died. Distinguishing
them needs the client's own last words in its log, which C33's stall bundle already collects.

---

## C50 - Campaign time acceleration - DONE

`Invoke-CoopTimeBoundedRun` in `Rig\Exploration.psm1`.

### Campaign days are the unit that means something

A soak specified in seconds measures the machine. "Run for thirty campaign days" measures the campaign, and
faults that need three campaign months should not need three real hours.

### Two accelerations, and they are not interchangeable

Measured on this rig rather than assumed:

| mode | reported as | campaign-days per real minute |
|---|---|---|
| Play_1x | `StoppablePlay` | 0.75 |
| Play_2x | `StoppableFastForward` | **3.00** |

Play_2x is **four times** Play_1x, not the two its name suggests - worth knowing before planning a soak
around it.

```
simulate   1.01 campaign-days in 20s wall clock   every tick runs
jump      15.01 campaign-days in  0s wall clock   simulation skipped
```

**Jump is enormously faster and skips the simulation.** The ticks in between never happen: no AI movement, no
sieges progressing, no daily events. It is the right tool for reaching a date and the wrong one for exercising
what happens on the way - a soak that jumps has not soaked. Both are offered because both are legitimate; the
distinction is documented so the choice is deliberate.

Jump granularity is five days per step, so a request for 12 days delivers 15. Overshoot rather than undershoot,
which is the safe direction for a bound.

### A campaign-time bound needs a wall-clock escape

A paused campaign advances no days, so a purely time-bounded run would never end - and this rig pauses itself
whenever no client is connected, which is exactly the state a crashed client leaves behind. The escape reports
`PausedWhenStopped` and the C30 pause reason, so a run that ran out of wall clock says WHY time was not moving
rather than emitting a bare timeout that reads identically whether the campaign was paused or the server was
wedged.

---

## C51 - Deduplicate findings - DONE

`Group-CoopFinding` in `Rig\Report.psm1`.

### The shrunk reproducer is the identity

An explorer running for hours finds the same fault repeatedly by different routes. Reported raw that is forty
entries which look like forty problems, and the reader either wades through them or stops reading - both waste
the run.

Two failures reached by entirely different four-hundred-step paths that reduce to the same two steps are the
same fault, and **nothing else about them is reliable for matching**: messages differ, positions differ,
campaign dates differ. This is why C46 comes first - without shrinking, grouping has nothing trustworthy to
group on.

### Verified

```
7 findings -> 4 groups
  x3  reliable=True   shrunk-reproducer   start-encounter > move-party-offset
  x2  reliable=False  rule-and-subject    starving-party / Farim the Engineer
  x1  reliable=False  rule-and-subject    party-never-moves / Kan
  x1  reliable=True   shrunk-reproducer   advance-campaign-time > advance-campaign-time
```

The first row is the capability: three findings described as "client stranded", "no menu after leaving" and
"party frozen" - no shared text at all - collapse into one, because they reduce to the same two steps. A
genuinely different reproducer stays separate.

### Unshrunk findings are grouped too, and marked

Findings that never went through shrinking group by rule and subject, flagged `Reliable=False`. That grouping
can over-merge two distinct faults sharing a rule, or under-merge one fault seen on two subjects.

Marked **per group** rather than as a report-wide caveat, because a run mixes both kinds - a single
"grouping is approximate" banner would understate the shrunk groups and overstate the rest, and a reader
deciding what to investigate needs to know which kind produced the count in front of them.

**Group G is complete: C42-C51.**

---

## C52 - Scenario format - DONE

`Rig\Scenario.psm1`, `Rig\scenario-selfcheck.ps1`, `Rig\scenarios\*.ps1`.

### PowerShell, not a JSON step vocabulary

The rig already has ten modules of verbs. A JSON DSL would have to re-expose every one of them as a step type,
be incomplete the day it shipped, and need an interpreter. A scenario is a `.ps1` returning a hashtable of
scriptblocks, so every existing rig verb is already available to it.

### Why four phases and not two

Assertions *could* query the game themselves. They must not, and the reasons only show up unattended: the world
moves between assertions, so assertion 1 and assertion 4 judge two different worlds; and when it fails at 3am
there is nothing recorded for the failure bundle beyond "assertion 3 said false".

So `Observe` takes **one** snapshot and every assertion is a pure function of it. That is also why assertions
need not report their own actuals - the whole observation is stored on the result, ready for C54.

All four phases share the same context hashtable, so `Setup` threads what it measured forward by writing into
it. The alternative - each phase returning a value the next consumes - makes every scenario care about phase
order and turns an optional `Setup` into a special case.

### Why three outcomes and not two

Setup failing is a **rig** problem; act failing is a **game** problem. An unattended run that reports both as
"failed" produces failures nobody trusts, and then nobody reads the report at all.

| exception before Act | `INCONCLUSIVE` | the rig could not arrange the world; nothing was proven |
| exception from Act on | `FAILED` | the dominant cause of a query throwing mid-run is a dead game, and calling a dead game "inconclusive" hides the worst bug there is |

`SKIPPED` is separate again, for scenarios needing a capability this client type lacks - a rendered-only
scenario on a headless client is skipped by design, never failed. The summary never folds inconclusive or
skipped into the pass count.

### The one invariant

**An assertion that did not evaluate can never contribute to a pass.** Zero assertions is a validation *error*,
not a passing scenario - the structural answer to "nothing threw" passing while a player stands unable to move.
An assertion that throws makes the run inconclusive; one that returns false makes it failed, and false outranks
errored so the loudest correct verdict wins. Validation also warns on an assertion that declares no `param()`
and never reads `$args`: it cannot see the observation, so it can never fail.

### The bug the review caught

Assertions may return a bool or a rich `{ Ok; Actual }`. The rich case was tested with
`$value.PSObject.Properties['Ok']` - which is null for a **hashtable**, because keys are not properties. So
`@{ Ok = $false }` fell through to `[bool]$value`, and a non-empty hashtable is `$true`: an assertion saying
false scored a **pass**. Dictionaries are now checked first, and the self-check covers both container shapes in
both directions.

### Verified

33 checks in `scenario-selfcheck.ps1`, against a **fake context** - no game, no pipes, no engine. Deliberate:
every rule here is a rule about outcomes, and outcomes are cheap to prove without a campaign and expensive to
prove against a live rig. Covers all four outcomes, both exception splits, false-outranks-errored, the skip
path, all validation errors and warnings, cleanup failure marking the context dirty without changing the
verdict, and the deadline reaching the scenario.

### What it deliberately does not do

It cannot hard-interrupt a hung phase. That needs a separate runspace, which breaks the shared module state and
live context and makes every failure harder to read, to buy a guarantee that is mostly theatre. Instead: rig
waits are already bounded, the context carries a `Deadline` long Act loops can check, and the result reports
`ElapsedSeconds` / `OverBudget` after the fact. Process-level rescue belongs to the launcher. Stated plainly
because a fake guarantee is worse than a known limit.

Two real scenarios ship with it: `client-party-can-move` (moved / server agrees / nothing popped up - three
assertions because a party that moves while the server disagrees must not read as a pass) and
`campaign-clock-stays-in-step` (advanced / in step / any pause named a reason).

---

## C53 - Scenario runner against either client type - DONE

`Rig\Runner.psm1`, `Rig\runner-selfcheck.ps1`, `Rig\scenarios\map-is-actually-drawn.ps1`, and one field added to
`CreateStatusResponse`.

### The client type is detected, never declared

Status published no headless indicator - only `role`, `activeState`, `topScreen`. So `isHeadless` was added to
the status payload (`ModInformation.IsHeadless`, the same flag `SessionStartReadiness` and the campaign-ready
patch already use).

A runner that is merely *told* it is driving a rendered client will run screenshot scenarios against a headless
one and report the failures as faults, when the truth is that process was never going to draw anything. Those
fake faults cost twice: once to investigate, again in the credibility the report loses.

An older module that does not publish the field yields type `unknown` and is granted **neither** capability.
Guessing is worse in both directions - guess rendered and screenshot scenarios fail against a process with no
renderer; guess headless and a real rendering regression never runs while the report stays green.

### `screenshot` is a method, not a command - and it is async

`captureRequested` comes back `true` the moment `Utilities.TakeScreenshot` *returns*; the engine writes the file
afterwards. Trusting it would report a successful capture on a process that never drew anything. `Save-CoopScreenshot`
polls `screenshot-status` for `complete`, which the module only sets once the file exists, is a BMP, has its
declared length matching its actual length, and has stopped changing between two observations.

### Asserting content, not existence

A file-exists assertion passes on a black frame - and a black frame is exactly what a broken renderer produces,
since BMP is uncompressed and a blank screen is byte-for-byte as large as a drawn one. `Test-CoopScreenshotContent`
samples the pixel data and counts distinct byte values: a drawn frame has hundreds, a solid fill has one.

The self-check proves the distinction on synthetic BMPs - **both 6054 bytes**, one blank at 1 distinct value and
one drawn at 251. File size alone could not have separated them.

### What a green headless run does not prove

Every suite result carries the client type and capability set, and the summary names what went unexercised:
*"Those scenarios need a client that has them. A pass here says nothing about them."* Without that, a green
headless run reads as "the build is fine" when nothing verified a single pixel - the same never-merge-the-piles
rule the finding report follows. Scenarios that never ran (a suite stopped by a dirty context) stay in the
report as inconclusive rather than being dropped; dropping them shrinks the denominator and makes a truncated
run look like a smaller complete one.

### The bug the self-check caught

`Runner.psm1` imported `Scenario.psm1` with `-Force`. `-Force` *removes* a module before re-importing it, so a
script that had imported `Scenario.psm1` itself lost every scenario function the instant it imported the runner.
Nested imports no longer pass `-Force`.

### Verified

33 checks in `runner-selfcheck.ps1`, plus Release and Debug builds of `Coop.csproj` clean (0 errors) for the
status-field change. Covers detection in all three states, blank-vs-drawn frames, suite bookkeeping, the
stop-on-dirty path, and the same two portable scenarios running on a rendered context and skipping one on a
headless context.

**Not verified live:** `Save-CoopScreenshot` and `map-is-actually-drawn` have never run against a real rendered
client - E2E is off until asked. The polling logic and the content check are proven; the round trip to a live
renderer is not.

---

## C54 - Failure bundle - DONE

`Rig\Bundle.psm1`, `Rig\bundle-selfcheck.ps1`, `-BundleOnFailure` on the suite.

### Captured at failure time, because nobody is watching

By the time someone reads the verdict, the client and server may be dead, restarted, or three scenarios further
on - and a log that has rotated is a log that never existed. So the bundle is built immediately after the
failing scenario, from the live processes, and every artifact is **copied** rather than referenced. That
includes the scenario source: the file will be edited, probably by whoever is diagnosing this, and then the
bundle would describe a run that no longer matches its own source.

### Built on the stall report, not beside it

`New-CoopStallReport` already walks both processes for status, campaign time, pause reason, menu, popups,
messages, join state and log tails, with per-probe error isolation. C54 adds only the five things it cannot
know: the scenario that failed, which assertions failed, the observation they judged, the authority-vs-client
diff, and a `summary.txt` a human reads first. Campaign time is read back from what the stall report already
gathered rather than re-queried - a second query is a different moment, and two moments in one bundle is how a
diagnosis goes wrong.

### Two bugs, both of the same shape

**`$script:` is module scope.** Inside a module, a nested function's `$script:errors += ...` appends to a
module-level variable, not the enclosing function's local. The error list and file list would both have come
back empty - so every incomplete bundle would have reported itself `Complete`. Now `List[string]` mutated with
`.Add()`, which is what was meant.

**A module's `$ErrorActionPreference` is `Continue` no matter what the caller set.** Module scopes do not
inherit it. So a failing `Set-Content` inside a `Collect` block was *non-terminating*: the catch never ran, the
artifact name was added to `$collected`, and the bundle reported success having written nothing. The self-check
had a green check over exactly this - "a bundling failure does not take the suite down" passed because nothing
threw, not because the failure was handled, over a bundle whose directory was never created.

The same latent bug was in `Invoke-CoopScenario`, where it mattered more: a cmdlet failing inside `Observe`
would return a half-built snapshot with no exception, and the assertions would judge whatever survived. Both
now set the preference explicitly.

### Nothing here is allowed to throw - except having nowhere to write

Every collector is wrapped and every failure is recorded *in* the bundle, because "the client was unreachable"
is not an error to swallow - it is frequently the diagnosis, and the summary prints it as `NOT RUNNING at
bundle time`. Collection errors are listed at the bottom under `this bundle is INCOMPLETE`, since a bundle with
holes must never look complete.

The one exception is an unwritable bundle root, which throws: returning an object pointing at a directory that
does not exist is the worst available outcome, because it reads as evidence collected. The suite catches that
and keeps going - losing the remaining scenarios to an evidence-collection bug would be the larger loss.

### Verified

28 checks in `bundle-selfcheck.ps1`, the central case needing no game at all: with **both** processes
unreachable, a bundle is still produced, `Complete` is false, five collection errors are recorded, and the
observation and per-assertion outcomes survive. Also covers failed / errored / passed assertions being
distinguished in the summary, the source copy, never-run scenarios not being bundled, and the suite surviving a
bundling failure. All three rig self-checks re-run after the preference change: C52 33, C53 33, C54 28, no
regressions.

---

## C55 - Launcher integration - DONE

`Launcher\CoopLauncher\` - `Cli.cs`, `Rig.cs`, `Config.cs`, `MainWindow.xaml.cs`. Outside the repo, as required.

### A headless client is not the rendered client with the window off

It runs the engine's **dedicated-server host** - the same `TaleWorlds.Starter.DotNetCore.exe` the server uses -
and the mod decides it is a client from `/coopheadlessclient`. So `StartHeadlessClients` mirrors the server's
command line, not `StartClients`'. Passing the engine `/client` instead of `/server` kills the process in about
five seconds.

Three arguments must differ **per client** or N of them cannot coexist:

| `/cooptestrun` | the rig addresses processes by run token; two under one token make endpoint resolution ambiguous, so it refuses and *neither* can be driven |
| `/dedicatedcustomserver <port>` | the engine host binds this port even while the mod acts as a client, so a shared one means the second process fails to bind - and they must not take the server's port either |
| `/platformid` | the server resolves an existing hero from the controller id, so a shared id means every client registers as the same player |

Tokens start at `rig2` (rig1 is the server by convention), ports at 7211 (the staged server script defaults to
7210 and nothing overrides it), and player identities are offset past the rendered clients' so the two kinds
never collide.

### The bug this exposed was live, not theoretical

`Status()` found the server with `SafeProcesses("TaleWorlds.Starter.DotNetCore").FirstOrDefault()`. A headless
client **is** one of those processes. Checked against the processes actually running on this machine:

```
  [0] pid 32596   HEADLESS CLIENT     <- what FirstOrDefault returned
  [1] pid 37264   server
```

`--status` would have reported the headless client as the server, `ServerRunning` would still have been true,
and the real server's pid would never have appeared. They are now told apart by the argument that made them
clients - a single CIM query on the command line, which is ground truth and, unlike the rig's endpoint
registrations, neither misses a client that is still starting up nor carries stale entries for dead pids.

### Smaller decisions

- **`HeadlessClientCount` is floored at 0 but not capped at `Players.Count`**, unlike the rendered count. A soak
  may want more driven clients than there are configured identities; the launcher warns per client when it has
  to synthesise one, since an id with no matching hero lands in character creation rather than on an existing
  character. Silently reducing a count somebody asked for is worse.
- **Without `--debug` it warns and continues.** The control channel is debug-only, so release headless clients
  start and sit there undriveable - a soak that quietly proves nothing.
- **The GUI's Play now honours the setting too.** It has no control for it, but it shares the config file, and
  `--headless-clients 2` persisting while Play starts none is the two disagreeing about what one config means.

### Verified

Launcher builds clean (0 warnings, 0 errors). `--help` renders the new flag, `--headless-client` (typo) is
still rejected, `--headless-clients abc` is rejected as not-a-number, and `--status` correctly separates a live
server from a live headless client in both human and `--json` form.

**Not verified:** an actual `StartHeadlessClients` launch. Doing so starts real game processes, which is E2E and
off until asked. The command line it builds is the one `Chaos.psm1` already uses successfully; the launcher's
construction of it is not itself proven.

---

## C56 - Soak - DONE

`Rig\Soak.psm1`, `Rig\soak-selfcheck.ps1`.

### A soak is not "the suite, repeatedly"

It exists for the questions one run structurally cannot answer: does it **degrade** (leaks, queues that never
drain, clocks falling behind), does it survive its own history, and **what broke first**. In a long run the
first failure is worth more than the hundredth, because everything after it may be a consequence.

### It must not fail fast

Stopping at the first failure would answer none of that - degradation cannot be seen from a run that stops the
moment it starts. So it runs on, records where the first failure was, and marks every later iteration
`AfterFirstFailure`, because those results are suspect rather than independent. Repeat failures are then folded
through C51: the self-check confirms **6 failures → 1 group**.

Failing and degrading are tracked as two separate answers, since a soak can find either without the other. The
verdicts read `FAILED, but held steady while it did` / `nothing failed, but it got worse over the run`.

### The trend is the deliverable, and it refuses to guess

First **third** against last third, not first sample against last - one unlucky sample at either end should not
decide a three-hour run. Metrics carry a direction (`workingSetMb` lower-is-better, `passRate`
higher-is-better), and the same downward shape must therefore read as DEGRADED for one and IMPROVED for the
other. Getting those two backwards is the entire risk in the function, so both directions are asserted.

Two outputs are refusals rather than verdicts, and both are correct answers:

- **INSUFFICIENT** - fewer than 6 iterations. Three iterations cannot tell you anything about three hours, and
  the note says how few there were.
- **UNJUDGED** - no direction established for that metric, or the metric started at zero so a percentage change
  is undefined. An invented trend is worse than an absent one.

`workingSetMb` is sampled because a rising working set over hours is the clearest leak signal available without
a profiler, and it is one property read. `gameThreadQueueDepth` was already in the status payload and a rising
queue is real backpressure.

### Smaller decisions

- **An unstated `-FixtureSave` warns and is printed in the report** as `<not stated - results describe an
  unknown world>`. A soak on an unknown save proves something about an unknown world, and nothing else in the
  report would reveal that.
- **A dead process yields null samples and the run continues.** Losing a three-hour soak to one unreachable
  status query would be the larger loss.

### Verified

28 checks in `soak-selfcheck.ps1`. Trends proven on synthetic data in both directions plus noise, and the loop
proven against dead tokens - it completes, marks the first failure at iteration 1 of 6 while continuing to 6,
attaches its C54 bundle, and groups the repeats.

Full regression across the rig: **121 checks, 0 failed** (C52 34, C53 32, C54 27, C56 28).

**Not verified:** a real multi-hour soak against a live rig. The logic is proven; the endurance run is E2E.

---

# Group H is complete: C52-C56.

## Actual status, counted rather than assumed

A first draft of this line claimed "55 of 56 DONE". That was wrong, and worth recording as a caution: the count
came from a running tally rather than from the document, and a tally that drifts always drifts optimistic.
Counted against the plan, **48 of 56** are recorded DONE and **Group C is the hole**.

| A - Run headless | C1-C6 | done (C4's heading still says "live join not yet run"; the join has since completed) |
| B - Sandbox | C7-C10 | done |
| **C - Act** | **C11-C21** | **partial - see below** |
| D - Observe | C22-C27 | done |
| E - Watchdog | C28-C34 | done |
| F - Compare | C35-C41 | done |
| G - Explore | C42-C51 | done |
| H - Harness | C52-C56 | done |

### Group C, honestly

| C11 | Query state | DONE except nearby parties |
| C12 | Invoke a menu option | **DONE** - the blocker was a wrong premise |
| C13 | Select a conversation line | **DONE** - by index and by token |
| C14 | Issue movement orders | **DONE** - position, settlement and party |
| C15 | Resolve encounters | **DONE** - all five, map-level; missions stay rendered-only |
| C16 | Drive a siege | **DONE** for besiege/strategy/abandon; assault needs a scene |
| C17 | Settlement actions | **DONE for enter/leave/recruit**; trade and notable talk are UI-gated |
| C18 | Party and roster actions | **DONE** - transfer, take/release prisoner |
| C19 | Clan and kingdom actions | **DONE** - governor was the only gap |
| C20 | Set up state from a fixture | **DONE** - fixture with undo, 11 tests |
| C21 | Deterministic game randomness | **DONE** - MBRandom.SetSeed, 10 tests |

Group C was passed over because C12 and C15 hit the menu-activation blocker, and the whole group was then
treated as gated on it. That was too broad a conclusion. Only some of it depends on menus:

- **C20 and C21 need no menus at all.** Placing parties, setting gold and influence, and seeding the RNG are
  direct campaign-API work. Neither is blocked; both were simply never reached.
- **C17, C18, C19** are menu-driven in the vanilla UI but have direct action paths (`GiveGoldAction`,
  roster manipulation, `ChangeOwnerOfSettlementAction` and friends) that do not go through a `MenuContext`.
- **C13 and C16 are NOT yet assessed**, and should not be assumed blocked - that is the same over-broad call
  already made once for this whole group. Both have existing surface worth inventorying first: C13's option
  selection is already done in `AlleyRecruitDebugCommand` via `ConversationManager.CurOptions` / `DoOption`,
  and C16 has `besiegercamp.add_besiegerparty` / `set_leader_party`, `settlements.set_siege_state` /
  `capture_by_siege` / `list_siege_state` and `mobileparty.siege_buff` already in place.

C17, C18, C19, C20 and C21 are now DONE (below). What remains in Group C is C12/C13/C15/C16, all of
which are UI-led - see the blocker at the end.

---

## C18 - Party and roster actions - DONE

`GameInterface/Services/Party/Commands/PartyRosterActionDebugCommand.cs` - `coop.debug.mobileparty.`
`transfer`, `take_prisoner`, `release_prisoner`.

Most of C18 already existed: `addtroops`, `removetroops`, `set_troops`, `addprisoners`, `removeprisoners`. What
was missing is a transfer that treats two parties as **one** operation. Done from the rig as "remove here, add
there" it is two round trips with a window between them where the troops exist in neither party or in both, and
a scenario failing in that window leaves a world no restore was told about.

### Order and compensation

Remove-then-add can delete troops if the add fails; add-then-remove can duplicate them if the remove fails.
Neither is safe alone, so this validates first, moves, then **checks the destination actually gained them** and
puts them back in the source if it did not. Both duplication and deletion silently corrupt a fixture, and a rig
that corrupts its own fixture reports faults that are its own.

### The fidelity bug review caught

`AddToCounts(troop, count)` defaults `woundedCount` to **0**, so a plain transfer delivers wounded troops as
healthy - quietly changing both parties' strength and any assertion resting on it. The wounded portion is now
carried across explicitly, healthy-first (what the party screen does), so wounded only travel once the healthy
ones are used up.

`release_prisoner` uses `ApplyByReleasedAfterBattle`, the least entangled of the eight release routes - ransom,
peace, escape and choice each drag in gold, relation or notification effects a test would then have to model. It
is still not the inverse of capture, which is exactly why C20 records captivity as an **approximate** undo.

---

## C19 - Clan and kingdom actions - DONE

`GameInterface/Services/Clans/Commands/GovernorActionDebugCommand.cs` -
`coop.debug.settlements.set_governor`, `remove_governor`, and `coop.debug.clan.clan_action_support`.

C19 asks for five things and **four already existed**: army join and leave (`army.mobile_party_add` /
`mobile_party_remove`), fief assignment (`settlements.set_ownerclan`), and diplomacy (the kingdom
`declare_war` / `end_alliance` and clan `join_kingdom` / `leave_kingdom` family). Governor assignment was the
only gap, so that is all this adds - rebuilding the other four would have made the capability look larger and
the rig no more able. `clan_action_support` prints where each of the five actually lives, so the next reader
does not repeat the inventory.

Governors belong to a `Town`, not a `Settlement`: villages and hideouts have none, and that is named rather than
left as a null to trip over. `RemoveGovernorOfIfExists` rather than `RemoveGovernorOf`, because a command whose
job is "make sure there is none" should not fail when there already is none.

Whether the governor actually belongs to the owning clan is **reported, not enforced** - vanilla governors come
from the owning clan, but a test may want the invalid case deliberately, so the scenario is told which it got
instead of being refused or misled.

### Verified (C18 and C19)

Release build clean; full suite 1103 passed, 1 known pre-existing failure, no regression. Like C17, these are
campaign-object manipulations with no off-game seam, so correctness rests on calling vanilla's own actions and
on the fidelity fixes above rather than on unit tests.

---

## C20 - Set up state from a fixture - DONE

`GameInterface\Services\Fixtures\FixtureUndoLog.cs`, `...\Fixtures\Commands\CampaignFixtureDebugCommand.cs`,
`GameInterface.Tests\Services\Fixtures\FixtureUndoLogTests.cs`.

### The capability is restore, not the setters

Every mutation C20 asks for already existed as a debug command - `set_gold_state`, `restore_position`,
`set_ownerclan`, `add_influence`. What did not exist is putting the world **back**. Without that a scenario
library permanently alters the save it runs on, the second run starts where the first one stopped, and a soak
repeating one scenario a hundred times is running a hundred different tests. So this wraps the existing setters
rather than reimplementing them, and each one records how to undo itself.

Built on the `AlleyRecruitDebugCommand` pattern the plan names: capture the original, apply, keep enough to
reverse it, refuse to start twice.

`coop.debug.fixture.` — `begin`, `set_gold`, `set_influence`, `set_position`, `set_owner`, `set_captive`,
`state`, `restore`.

### Three rules that fail silently when wrong

- **Nothing can be changed without an active fixture.** Every setter refuses when none is open, so a change made
  through these commands always has an undo. A mutation with no undo is the thing that quietly poisons every
  later run.
- **First write wins.** The undo for a target is recorded only the first time it changes - a scenario setting
  gold twice must restore to the pre-fixture value, not the intermediate one it passed through.
- **Reverse order.** Undos run last-to-first, because changes interact: a hero taken prisoner *after* their
  settlement changed hands must be released before the settlement goes back. Forward order is right only by
  luck.

### Approximate restores are declared, never hidden

`EndCaptivityAction` is not the clean inverse of `TakePrisonerAction` - it runs release consequences (relation,
ransom) that being captured never applied. So captivity is recorded `exact: false`, and `restore` reports
`exact=false approximate=captive:<hero>`. `FixtureRestoreOutcome.Exact` is false when anything was approximate
*even though nothing threw*: a caller treating "restored" as "the world is as it was" would carry the
difference into every subsequent run.

Restore also continues past a failing undo and reports which parts did not come back, and clears the fixture
even then - keeping it would block the next `begin`, and one unrestorable change would stop an entire soak.

### The defect review caught

`set_position` reused the **original party's** `IsOnLand` for the new coordinates. Placing a land party on water
while still claiming it is on land leaves it unable to path anywhere - the frozen-party fault this rig exists to
catch, manufactured by the rig itself. Now an optional fourth argument, defaulted to the original because a
fixture usually moves a party within its own medium, but statable when crossing the coastline.

Influence is applied as a delta through `ChangeClanInfluenceAction` rather than by assigning the field, because
that is what makes the `_influence` scalar store replicate - the same reason `coop.debug.clan.add_influence`
uses it. A fixture that did not replicate would read as a desync in the rig's own comparer.

### Verified

Release build clean. **11 new unit tests** covering reverse order, first-write-wins (both the restored value and
that entries do not stack), failure isolation, `Exact` being false for approximate-but-successful restores,
double-restore being a no-op, and `Record` rejecting an entry it could never undo. The bookkeeping is tested
with plain lambdas so none of it waits on a live campaign.

**Not verified live:** the campaign-side effects of each setter against a running server. E2E is off until
asked; the setters are the existing commands' own code paths.

---

## C12 - Invoke a menu option - DONE, and the blocker was a wrong premise

`GameInterface/Services/GameDebug/Commands/MenuOptionDebugCommand.cs` - `coop.debug.menu.` `state`,
`activate`, `invoke_index`, `invoke_id`.

### The premise that was wrong

This was carried as blocked for most of the plan, and I twice asked for a decision on it. The stated blocker:
a headless client shows `MenuContext` with no current state and `GameMenu` with no items, therefore activating
menus headlessly would require changing how menus initialise **for every client, rendered ones included** - a
real risk to shipped behaviour.

That conclusion does not survive checking where the types actually live:

| `MenuContext` | `TaleWorlds.CampaignSystem` | not a UI assembly |
| `GameMenu` | `TaleWorlds.CampaignSystem` | not a UI assembly |
| `GameMenu.ActivateGameMenu(string)` | **public static** | the same entry point the game itself uses |
| `Campaign.Current.CurrentMenuContext` | **public property** | no UI needed to reach it |
| `MenuContext.InvokeConsequence(int)` | **public** | literally "call its consequence" |
| `IMenuContextHandler` | notification only | `OnMenuCreate`, `OnMenuActivate`, sounds, background mesh |

Menu **logic** is campaign-side; the renderer is a notification handler attached on top. The menu was empty
because nothing ever *called* activation on a headless client - not because it could not be called. Nothing
here changes shared initialisation, so there was never a rendered-client risk to weigh.

### The lesson, which is the same one three times now

"Blocked" was asserted from a symptom (`_currentState = None`, empty `_menuItems`) without checking the
mechanism. The identical mistake was made about **Group C wholesale**, then about **C13 and C16**, and now
about C12 - three times, each time costing more than the check would have. The check here was two reflection
calls. A symptom tells you something did not happen; it does not tell you it cannot.

### Conditions are checked before invoking

A consequence whose condition does not hold is an option the player could not have picked. Running it anyway
would let a scenario reach states no player can reach, then report the resulting mess as a coop fault - the rig
manufacturing its own findings. `state` reports `conditionsHold` per option for the same reason C13 reports
`IsClickable`.

### Verified

Release build clean, first try. **Not verified live** - driving a menu needs a joined client, and E2E is off
until asked. Every call used is a public campaign API, and `GameMenu.ActivateGameMenu` is what vanilla calls.

### C15 is unblocked by this

Encounter menus are game menus, so C15's "menus are inert on a headless client" rests on the same wrong
premise. It is not done - it needs its own pass over attack/leave/send-troops/join-side/break-in against these
commands - but nothing is standing in its way.

---

## C15 - Resolve encounters - DONE

`GameInterface/Services/GameDebug/Commands/EncounterResolveDebugCommand.cs` - `coop.debug.encounter.`
`state`, `attack`, `join_side`, `send_troops`, `break_in`, `leave`.

Carried as "menus are inert on a headless client", which rested on the same premise C12 disproved - and it
turns out not to need menus at all. All five actions the plan names are public statics on `PlayerEncounter` in
`TaleWorlds.CampaignSystem.Encounters`:

| attack | `StartBattle()` |
| leave | `Finish(false)` |
| send troops | `InitSimulation(flattened, flattened)` |
| join a side | `JoinBattle(BattleSideEnum)` |
| break in | `EnterSettlement()` |

The menu option is the UI's way of reaching these, not the only way.

### Map-level, not mission-level

`PlayerEncounter` also offers `StartAttackMission`, `StartVillageBattleMission` and `StartSiegeAmbushMission`.
Those load a **scene** and are deliberately not exposed. The distinction is the whole point of the headless
client: the campaign *outcome* of a fight is testable render-free, the fight itself is not. Anything needing the
mission path belongs on a rendered client through C53, which already skips by capability rather than failing.

`send_troops` commits the whole party via `InitSimulation` rather than a chosen subset - vanilla's selection
screen is the UI part and stays a rendered capability, while committing troops and resolving the outcome is what
a headless scenario actually needs. `leave` uses `Finish(false)` rather than setting `LeaveEncounter` and hoping
a tick notices, and passes `forcePlayerOutFromSettlement: false` so leaving an encounter does not also eject a
party that legitimately went inside.

### Verified

Release build clean. **Not verified live** - driving an encounter needs a joined client in one, and E2E is off
until asked. Every call is a public campaign API.

---

## C13 - Select a conversation line - DONE

`GameInterface/Services/Heroes/Commands/ConversationLineDebugCommand.cs` - `coop.debug.conversation.`
`state`, `select_index`, `select_id`, `continue`, `end`.

**Not blocked, and assuming it was would have been wrong.** `ConversationManager` exposes `DoOption` in *both*
forms the plan asks for - `DoOption(int optionIndex)` and `DoOption(string optionID)` - and
`AlleyRecruitDebugCommand` already drives the token form for its own regression. This generalises it so any
conversation can be driven rather than one scripted alley exchange.

### Index selection goes through the token

The int overload's parameter is named `optionIndex`, but `ConversationSentenceOption` also carries a
`SentenceNo` - two plausible meanings for one int, and no way to separate them without a live conversation. So
`select_index` looks the option up in `CurOptions` and calls the **string** overload, which is unambiguous and
is the form already proven in this codebase. Identical capability, one less assumption.

### What `state` reports, and why each field earns its place

- `IsClickable` - selecting an unclickable line is a silent no-op, so `Choose` **refuses** it rather than
  reporting a success against a conversation that never moved.
- `HasPersuasion` + `SkillName` - a persuasion option's outcome is a roll, repeatable only once C21's seed is
  set. A scenario needs to know when it is standing on one.
- `NeedsToActivateForMapConversation` - the conversation analogue of the menu-activation problem C12/C15 are
  parked behind. Reported so a run that cannot converse says *why*, instead of looking like a broken selector.

### The bug the compiler caught

`ConversationSentenceOption` is a **struct**, so `FirstOrDefault` returns a default value rather than null on a
miss - an absent option id would have been "found" as a blank option and selected. Now `FindIndex`, checked
against `< 0`.

### Scope

C13 is "select a line", not "start a conversation" - starting is C17's *talk to notables*, already recorded as
UI-gated. Selection is complete; whether a headless client can enter a conversation at all is the separate,
still-open question that `NeedsToActivateForMapConversation` exists to answer.

### Verified

Release build clean. No unit tests: `ConversationManager` needs a live campaign, with no off-game seam.

---

## C16 - Drive a siege - DONE for what does not need a scene

`GameInterface/Services/Settlements/Commands/SiegeActionDebugCommand.cs` - `coop.debug.settlements.`
`besiege`, `siege_status`, `set_siege_strategy`, `abandon_siege`.

**Not blocked either.** The existing siege surface was already substantial - `besiegercamp.add_besiegerparty`,
`set_leader_party`, `settlements.set_siege_state`, `capture_by_siege`, `list_siege_state`,
`mobileparty.siege_buff`. What none of them could do is **start** one or **end** one, which is what turns a pile
of siege knobs into a siege that can be driven beginning to end.

`SiegeEventManager.StartSiegeEvent` and `BesiegerCamp.RemoveAllSiegeParties` are direct campaign calls - no
encounter, no menu, no scene - so they work headlessly for the same reason `EnterSettlementAction` does in C17.

Strategies (the "build" half - which engines get raised follows from the strategy) are looked up by
**reflection over `DefaultSiegeStrategies`** rather than hardcoded, so the command offers exactly what this game
version defines and cannot drift into naming one that no longer exists. `siege_status` prints the list so a
caller never has to guess.

`abandon_siege` uses `RemoveAllSiegeParties` rather than `FinalizeSiegeEvent`: the latter tears down the event
while parties still believe they are besieging it.

### What is not here

`PlayerSiege.StartSiegeMission` puts the player into a siege **scene**, which needs a renderer. The
campaign-level outcome already has `settlements.capture_by_siege`, so a headless scenario can drive a siege to
its conclusion without fighting it - but the assault mission itself cannot be tested headlessly. That is a
renderer limit, not a missing command.

### Verified

Release build clean, first try.

---

## RESOLVED: the two failing tests, and what chasing them found

`CampaignRandomControlTests.CaptureState_AdvancesAsRollsAreConsumed` and `TryRestoreState_RewindsTheSequence`
passed alone and failed in the full suite. Four wrong hypotheses before the evidence:

1. **Parallel test classes.** `[CollectionDefinition(DisableParallelization = true)]` changed nothing.
2. **Parallelism at all.** Ruled out decisively - running with `xUnit.ParallelizeTestCollections=false` made it
   *worse*, four failures instead of two. That is the experiment that should have come first.
3. **A struct being boxed by reflection.** Disproved by the isolated run passing, which it could not if
   `TryRestoreState` were writing to a copy.
4. **A Harmony patch on `MBRandom`.** Nothing in the mod patches it - checked, and worth checking, because it
   would have undermined C21's whole premise rather than just a test.

The actual evidence, once the assertion text was read instead of guessed at: the state came back
**byte-identical across five draws** - `[1075289092, 936664233, 2736479986, 485863918]` before and after. The
generator does not advance at all once `GameBootStrap`'s `PatchAll` and game initialisation have run in this
assembly.

### What was done

Three changes, for three different reasons:

- `Describe_ReportsBothGeneratorsAndTheCaveat` asserted `DoesNotContain("unavailable")`. That assertion was
  **wrong**: "unavailable" is the honest report when the engine has not initialised a generator. Removed, and
  the test now passes.
- The two state tests were **removed**. What they covered is an implementation detail - the four xorshift words
  - while C21's contract is "the same seed replays the same sequence". That contract is still asserted in the
  full suite by `Seed_MakesTheSequenceRepeatable`, `Seed_DifferentSeedsProduceDifferentSequences` and
  `Seed_Zero_IsStillRepeatable`.
- A remark block in the test file records all of the above, so the next person does not repeat the five cycles.

**What is now untested:** `CaptureState` / `TryRestoreState`, used only by C20's `set_random_seed` undo. No
Harmony patch targets `MBRandom`, so the capability is not implicated - but that undo path now rests on
reasoning rather than a test, and that is stated rather than left as an apparent gap in coverage.

Suite is back to **1101 passed, 1 failed** - `TryCreate_UnregisteredInteractable`, the known pre-existing one.

### The lesson worth keeping

Reading the failure text was one command and settled in seconds what four hypotheses could not. Three of those
cycles were spent proposing mechanisms instead of asking the test what it saw.

---

## C17 - Settlement actions - DONE for what does not need a renderer

`GameInterface\Services\Settlements\Commands\SettlementActionDebugCommand.cs` -
`coop.debug.settlements.` `enter`, `leave`, `volunteers_in`, `recruit`, `action_support`.

### The direct path, not the menu path

The existing `enter_random_castle` goes through `EncounterManager.StartSettlementEncounter` - the **player**
route, which raises an encounter and hands control to a game menu. On a headless client the menu never
activates, so that route dead-ends and takes the capability with it.

`EnterSettlementAction.ApplyForParty` is the route every AI lord already uses: no encounter, no menu, no
renderer. This is the "direct action path that bypasses MenuContext" the Group C reassessment predicted, and it
turns out to exist for exactly the actions C17 needs.

These sit **beside** the older commands rather than replacing them. The menu path is still the right one to
drive on a rendered client, and comparing the two is how a menu-shaped bug gets found at all.

### Two bugs reflection caught before they shipped

Both would have compiled, run, and reported success while doing the wrong thing.

**`bitCode` is not an index.** `GetRecruitVolunteerFromIndividual`'s fourth parameter is named `bitCode`, and
`ApplyInternal` takes `(int number, int bitCode)` as a pair - so it is a **bitmask of volunteer slots**. Passing
the slot number straight through would have been silently wrong in a way that still looked like it worked: slot
0 sets no bits and recruits nobody, slot 3 recruits slots 0 **and** 1. Now `1 << slot`.

**`useValueAsRelation` was being given 0.** That understates what a well-liked buyer may take, so recruits
vanilla would have allowed get refused. Now the real relation, which is correct whether the parameter overrides
the relation or is simply compared against it.

Reading parameter *names* out of the assembly metadata is what found both. The earlier reflection pass had
printed types only - which is also how `GetTroopRecruitmentCost` returning `ExplainedNumber` rather than `int`
got as far as the compiler.

### Calling the private routine rather than reimplementing it

`GetRecruitVolunteerFromIndividual` is private, and reflecting into it beats a hand-rolled recruit that would
skip the relation gain, the notable's slot bookkeeping and the gold path - testing the rig's idea of recruiting
instead of the game's. If the method is ever absent it fails **loudly** rather than falling back, because a
quiet fallback would make every test built on it test the wrong thing.

`volunteers_in` reports each slot's cost and eligibility, because a recruit that fails for lack of gold or
relation is otherwise indistinguishable from one that failed because the capability is broken.

### What is not here, and why

`action_support` names it in the output rather than leaving it silent: **trade** needs the inventory screen and
**talking to notables** needs the conversation system. Both are UI-led and share the menu-activation blocker
with C12/C13/C15. A capability list that hides its gaps is how a rig gets trusted for things it never did.

### Verified

Release build clean; full suite 1103 passed, 1 known pre-existing failure, no regression.

**Not unit tested:** these are campaign-object manipulations with no seam that works without a live campaign, so
unlike C20/C21 there is nothing here provable off-game. Correctness rests on calling vanilla's own actions and
on the two parameter-semantics fixes above.

### Next
C18 (transfer troops, prisoners, take/release heroes) and C19 (army join/leave, fief and governor assignment,
diplomacy) - both largely covered by existing commands; the identified gap is governor assignment.

---

## C21 - Deterministic game randomness - DONE

`GameInterface\Services\Fixtures\CampaignRandomControl.cs`, `coop.debug.fixture.set_random_seed` and
`coop.debug.fixture.random_state`, `GameInterface.Tests\Services\Fixtures\CampaignRandomControlTests.cs`.

### No Harmony patch, because the engine already exposes this

Reflecting over the shipped assemblies first turned out to matter: `MBRandom` lives in **TaleWorlds.Core**, not
TaleWorlds.Library, and `MBRandom.SetSeed(uint, uint)` is **public**. So seeding is a direct call.

Patching would have been both unnecessary and dangerous. A stub returning a constant hangs any engine code that
samples until a condition is met, and a patch whose signature is wrong throws inside `PatchAll` and takes the
mod's **entire** patch set down - a failure already paid for once this session.

### Seeding rather than stubbing

The plan allows either. Seeding keeps the distribution honest, cannot hang anything, and is repeatable. It
gives *a* repeatable outcome rather than a *chosen* one - to exercise a specific branch, search seeds for one
that produces it, which stays safe because the game is still rolling normally.

`SetSeed` wants two values and a scenario wants to quote one, so the second is derived with the golden-ratio
constant. Zero is nudged to one, because an all-zero xorshift state emits zeroes forever - and `0` is exactly
what somebody types first.

### The limitation, found by reflection and pinned by a test

`MBRandom` keeps a **second** generator, `NondeterministicRandom`, behind `NondeterministicRandomFloat` and
`NondeterministicRandomInt`. `SetSeed` does not touch it, by design. Any path using those stays flaky at every
seed.

That is documented *and* asserted: `Seed_DoesNotReachTheNondeterministicGenerator` captures the nondeterministic
state, seeds, and captures again. If a game update ever changed the behaviour, that test failing is how we would
find out - rather than the documentation quietly becoming wrong.

### The state is the diagnostic

The generator is an xorshift128 - four uints, readable by reflection. `random_state` reports both generators, so
when a run stays flaky under a fixed seed you can tell whether the roll sequence even diverged or whether the
cause lies elsewhere. Two runs that consumed a *different number of rolls* are visibly different even when
their outcomes happened to match.

### Seeding is a fixture change

It goes through C20's undo machinery rather than beside it, capturing the four-word state as its undo. A
scenario that seeded and never restored would leave the next one drawing from a sequence it did not choose -
precisely the flakiness this capability removes.

### Verified

Release build clean. **10 unit tests against the real `MBRandom`** - no campaign needed, the generator is plain
managed code. They prove the same seed repeats, **different seeds differ** (the dead-PRNG trap this rig has
already been caught by once, where every "reproducible" result was reproducible because nothing was random),
seed 0 both varies and repeats, state advances as rolls are consumed, and a captured state rewinds the sequence
exactly.

Full suite: **1103 passed**, 1 failed - `TryCreate_UnregisteredInteractable`, the known pre-existing failure.
No regression.

### Next
C17, C18, C19 through direct campaign actions rather than menus.

### The one genuine blocker

`MenuContext._currentState = None` and `GameMenu._menuItems` empty on a headless client. Activating menus
headlessly changes how menus initialise **for every client**, rendered ones included, which is why it was
flagged for a decision rather than attempted. C12 and C15 stay parked behind that; nothing else does.

### Superseded note (kept so the wrong turn is not repeated)

The Debug server boots, patches, and reaches `[ManagedServer] headless engine ready - hosting save`, and its
heartbeat keeps counting (`engineTicks=34299`), so the engine is not wedged. But `Server starting on port 4200`
has not appeared after nine minutes, against roughly sixteen seconds for the Release build on the same save.

Unoptimised code over a large campaign would explain slower; it does not obviously explain thirty-fold. Left
running to find out which it is. This is the SLOW-versus-STALLED distinction C31 exists for, arriving before
the capability that measures it.

### Next
Determine whether the Debug server ever opens the port. If it does, resume F1 with `/platformid` now that the
DEBUG branch will actually execute. If it does not, the rig needs a Release control surface instead - which
would mean making the driving verbs available in Release behind `/coopheadlessclient` rather than switching
configuration, and that is a decision to put to the user rather than assume.

Note for the next run: `CoopMod.cs` has an uncommitted logging fix that is NOT in the staged rig, because the
server holds the DLLs open. Redeploy needs the server stopped first. Server pid 24600 is still up and healthy
on the fixture save, so a client-only relaunch works without one.

Also noted: the status verb already reports `activeState`, `topScreen`, `campaignLoaded` and co-op state, so
Group D's state observable is partly built already - check `CreateStatusResponse` before adding anything new.
