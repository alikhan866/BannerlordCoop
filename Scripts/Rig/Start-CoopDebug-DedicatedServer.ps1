# Runs a HEADLESS coop dedicated server on our own build - no game window, no second Bannerlord instance.
#
# Why this can exist at all: BannerlordCoopServer.exe hash-pins the Coop module it shipped with and refuses
# anything else (release-info.txt, exit code 4). But that check lives in the WRAPPER, not the engine. The
# workshop item also ships the headless engine itself (engine\bin\Win64_Shipping_Server), which is just
# Bannerlord with no renderer and loads whatever modules it is pointed at. So we skip the wrapper and drive
# the engine directly with our module.
#
# The engine is 5.7 GB, 5.2 GB of it read-only game assets, so those are DIRECTORY JUNCTIONS back to the
# workshop copy - junctions need no elevation and no disk. Only our own module is a real folder.
#
#   .\"Start CoopDebug DedicatedServer.ps1" -SaveName rescue2
#   .\"Start CoopDebug DedicatedServer.ps1" -SaveName rescue2 -Password qwe -Rebuild
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $SaveName,
    [string] $Password = '',
    [ValidateSet('Public', 'FriendsOnly', 'None')][string] $Visibility = 'Public',
    # Consumed by the engine's dedicated-server mode, not by coop - coop still listens on UDP 4200.
    [int] $Port = 7210,
    [string] $Region = 'EU',
    [string] $Root = "$env:USERPROFILE\CoopDebugServer",
    [string] $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord',
    [string] $WorkshopDs = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3770450698\DedicatedServer',
    # Opens the live-test control channel on the server. Without it the rig can drive every CLIENT and not
    # the server they are testing against, and the failure is silent from the outside - see the
    # '[LiveTest] gate:' line in Coop_server.log, which now reports the decision either way.
    [string] $RunToken = '',
    # Which installed module to host. CoopDebug is the live-test build; CoopFixes is the RELEASE build
    # that package-share.ps1 produces and that other players install from the zip. The id has to MATCH on
    # both ends - module-id validation rejects a client whose module list differs from the server's - so
    # hosting a game for someone using the shared zip means hosting CoopFixes, not CoopDebug.
    [string] $ModuleId = 'CoopDebug',
    [switch] $Rebuild,
    [switch] $StageOnly
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $WorkshopDs)) {
    throw "Dedicated-server files not found at:`n  $WorkshopDs`nSubscribe to the Coop workshop item so Steam downloads the DedicatedServer folder."
}

$srcEngine = Join-Path $WorkshopDs 'engine'
$dstEngine = Join-Path $Root 'engine'
$ourModule = Join-Path $GameDir "Modules\$ModuleId"

if (-not (Test-Path $ourModule)) {
    throw "$ModuleId is not installed at $ourModule. Run package-share.ps1 first."
}

function New-Junction {
    param([string] $Link, [string] $Target)
    if (Test-Path $Link) { return }
    New-Item -ItemType Junction -Path $Link -Target $Target -ErrorAction Stop | Out-Null
}

# --- stage -------------------------------------------------------------------------------------
if ($Rebuild -and (Test-Path $Root)) {
    Write-Host "clearing $Root"
    # The layout holds DIRECTORY JUNCTIONS into the game install and the workshop copy. A recursive delete that
    # follows one of them deletes the GAME (it has happened). So every reparse point is unlinked first, with a
    # non-recursive Directory.Delete that removes only the link, and the recursive delete runs only after a check
    # that none is left. Never merge these two steps back into one Remove-Item.
    $links = Get-ChildItem $Root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
    foreach ($link in ($links | Sort-Object { $_.FullName.Length } -Descending)) {
        [IO.Directory]::Delete($link.FullName)
    }
    $left = Get-ChildItem $Root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
    if ($left) { throw "Refusing to delete $Root - junctions still present: $($left.FullName -join ', ')" }
    Remove-Item $Root -Recurse -Force
}

New-Item -ItemType Directory -Force (Join-Path $dstEngine 'Modules') | Out-Null

# Read-only engine parts: junction straight back to the workshop copy.
foreach ($dir in 'dotnet', 'Parameters', 'XmlSchemas') {
    $src = Join-Path $srcEngine $dir
    if (Test-Path $src) { New-Junction (Join-Path $dstEngine $dir) $src }
}

# bin has to be a REAL copy, because our module's dependencies have to live in it.
#
# The engine loads Coop.dll from the module folder, but resolves everything Coop.dll REFERENCES - Serilog,
# 0Harmony, Common, Coop.Core, GameInterface - from the engine's own bin, the way .NET resolves from the
# application base. With bin junctioned to the workshop copy those lookups fail one after another
# ("Cannot load: Serilog.dll ... 0Harmony.dll ... GameInterface.dll") and the module dies on arrival. This
# is the same reason DedicatedServer.Core.dll could not load, and the reason the official wrapper hosts the
# engine in-process: it controls assembly resolution. We settle for putting the assemblies where the engine
# already looks. ~424 MB, the only real cost of the whole arrangement.
$dstBin = Join-Path $dstEngine 'bin'
if (-not (Test-Path $dstBin)) {
    Write-Host "copying engine bin (~424 MB, once)..."
    Copy-Item (Join-Path $srcEngine 'bin') $dstBin -Recurse -Force
}

# Stock modules the engine needs, junctioned so their gigabytes of assets cost nothing.
foreach ($mod in 'Native', 'DedicatedServer.Windows') {
    $src = Join-Path $srcEngine "Modules\$mod"
    if (Test-Path $src) { New-Junction (Join-Path $dstEngine "Modules\$mod") $src }
}

# SandBoxCore is staged subfolder-by-subfolder rather than as one junction, so the battle scenes can be
# bridged in from the game install.
#
# The workshop dedicated server ships SandBoxCore with no SceneObj at all - it never opens a mission, so it
# never needs a battle scene. A headless CLIENT does: MissionInitializerRecord.SceneName resolves to something
# like battle_terrain_t, and those 98 scenes exist only under the game's Modules\SandBoxCore\SceneObj. A
# plain junction cannot have a folder added inside it, hence the per-subfolder form.
#
# Only SandBoxCore's scenes are bridged, never SandBox's: SandBox\SceneObj holds the full 19 MB Main_map,
# which would shadow the stripped render-free one CoopDebug provides and take the headless server down on the
# map load. SandBoxCore has no Main_map, so it is safe.
$coreSrc = Join-Path $srcEngine 'Modules\SandBoxCore'
$coreDst = Join-Path $dstEngine 'Modules\SandBoxCore'
if ((Test-Path $coreSrc) -and -not (Test-Path $coreDst)) {
    New-Item -ItemType Directory -Force $coreDst | Out-Null
    foreach ($child in Get-ChildItem $coreSrc -Directory) {
        New-Junction (Join-Path $coreDst $child.Name) $child.FullName
    }
    Get-ChildItem $coreSrc -File | Copy-Item -Destination $coreDst -Force

    $coreScenes = Join-Path $GameDir 'Modules\SandBoxCore\SceneObj'
    if (Test-Path $coreScenes) {
        New-Junction (Join-Path $coreDst 'SceneObj') $coreScenes
        $battleScenes = @(Get-ChildItem $coreScenes -Directory -Filter 'battle_terrain*' -ErrorAction SilentlyContinue).Count
        Write-Host "scenes     -> SandBoxCore\SceneObj from the game install ($battleScenes battle terrains)"
    }
    else {
        Write-Host "scenes     -> SandBoxCore\SceneObj NOT FOUND in the game install; battle scenes will be missing"
    }
}

# SandBox needs its assemblies bridged, so it cannot be a plain junction.
#
# The engine resolves EVERY module's DLLs from bin\Win64_Shipping_Client - including when run with
# /dedicatedcustomserver, which does not change the layout it looks in. But this distribution ships
# SandBox.dll only under bin\Win64_Shipping_Server, so the engine reports
#   Couldn't find .dll: ..\Modules\SandBox\bin\Win64_Shipping_Client\SandBox.dll
# and carries on without the campaign assembly - no campaign, so nothing for coop to attach to, and it
# still logs "Finished All" as though it worked. The official wrapper hosts the engine in-process and
# resolves this itself; we bridge it by giving the module both layouts. Its ModuleData stays junctioned,
# so only 1.3 MB of assemblies is copied.
#
# Native and SandBoxCore ship no bin at all here: the "Couldn't find" lines they produce are client-only
# view assemblies (GauntletUI, Platform.PC) that a headless server genuinely does not need.
$sandboxSrc = Join-Path $srcEngine 'Modules\SandBox'
$sandboxDst = Join-Path $dstEngine 'Modules\SandBox'
if ((Test-Path $sandboxSrc) -and -not (Test-Path $sandboxDst)) {
    New-Item -ItemType Directory -Force $sandboxDst | Out-Null
    foreach ($child in Get-ChildItem $sandboxSrc -Directory | Where-Object { $_.Name -ne 'bin' }) {
        New-Junction (Join-Path $sandboxDst $child.Name) $child.FullName
    }
    Get-ChildItem $sandboxSrc -File | Copy-Item -Destination $sandboxDst -Force
    foreach ($target in 'Win64_Shipping_Server', 'Win64_Shipping_Client') {
        New-Item -ItemType Directory -Force "$sandboxDst\bin\$target" | Out-Null
        Copy-Item "$sandboxSrc\bin\Win64_Shipping_Server\*" "$sandboxDst\bin\$target" -Recurse -Force
    }
}

# StoryMode and CustomBattle, from the game install - the workshop dedicated server ships neither.
#
# These are not optional. The launcher enables them for singleplayer, so every save the client writes was
# written with them loaded, and StoryMode in particular registers campaign behaviors whose data is in the
# save. Loading without them makes the save report "ModuleRemovedFromGame" and leaves the campaign
# restoring behavior data for modules that are not there.
#
# Their bin is client-layout only, so it is mirrored into both, exactly as SandBox is.
foreach ($mod in 'CustomBattle', 'StoryMode') {
    $src = Join-Path $GameDir "Modules\$mod"
    $dst = Join-Path $dstEngine "Modules\$mod"
    if (-not (Test-Path $src) -or (Test-Path $dst)) { continue }

    New-Item -ItemType Directory -Force $dst | Out-Null
    foreach ($child in Get-ChildItem $src -Directory | Where-Object { $_.Name -ne 'bin' }) {
        New-Junction (Join-Path $dst $child.Name) $child.FullName
    }
    Get-ChildItem $src -File | Copy-Item -Destination $dst -Force
    foreach ($target in 'Win64_Shipping_Server', 'Win64_Shipping_Client') {
        New-Item -ItemType Directory -Force "$dst\bin\$target" | Out-Null
        Copy-Item "$src\bin\Win64_Shipping_Client\*" "$dst\bin\$target" -Recurse -Force
    }
    Write-Host "module     -> $mod staged from the game install"
}

# Our module is a real copy, so rebuilding it never writes into the workshop folder.
#
# The assemblies go in Win64_Shipping_Client even though this is the SERVER build of the engine: it resolves
# a module's DLLs by the CLIENT folder name regardless of its own build. Putting them only under
# Win64_Shipping_Server - which is what the stock dedicated-server module uses - produces
#   Couldn't find .dll: ..\Modules\CoopDebug\bin\Win64_Shipping_Client\Coop.dll
# and the submodule is skipped in silence: the engine still boots and logs "Finished All", just with no coop
# in it. Both folders are filled so the layout is right either way.
# Staged under our own module id, matching the client and the saves it writes.
#
# This was previously renamed to "Coop" so DedicatedServer.Windows - the coop team's own host, not
# TaleWorlds' multiplayer module - could resolve it by that id. We no longer load that host at all, and the
# rename actively hurt: a save written by "CoopDebug" loads under "Coop" as a module mismatch
# (CoopDebug removed / Coop added), which the loader only forces through.
$dstCoop = Join-Path $dstEngine "Modules\$ModuleId"
if (Test-Path $dstCoop) { Remove-Item $dstCoop -Recurse -Force }
foreach ($target in 'Win64_Shipping_Client', 'Win64_Shipping_Server') {
    New-Item -ItemType Directory -Force "$dstCoop\bin\$target" | Out-Null
    Copy-Item "$ourModule\bin\Win64_Shipping_Client\*" "$dstCoop\bin\$target" -Recurse -Force
}
foreach ($item in 'SubModule.xml', 'ModuleData', 'GUI', 'mod-config.default.json') {
    $src = Join-Path $ourModule $item
    if (Test-Path $src) { Copy-Item $src $dstCoop -Recurse -Force }
}

# The shipped manifest describes the copy players install: not a dedicated server, and not render-free.
# This staged copy is both, so it has to say so or the engine reads the manifest, logs "Loading
# submodules...", and never loads Coop.dll - no error, no coop, and a clean "Finished All" at the end.
$subXml = Join-Path $dstCoop 'SubModule.xml'
$xml = Get-Content $subXml -Raw
$xml = $xml -replace '<Tag key="DedicatedServerType" value="[^"]*" />', '<Tag key="DedicatedServerType" value="all" />'
$xml = $xml -replace '<Tag key="IsNoRenderModeElement" value="[^"]*" />', '<Tag key="IsNoRenderModeElement" value="true" />'
# The id the save records, so a save round-trips between this server and the client unchanged.
$xml = $xml -replace '<Id value="[^"]*"\s*/>', "<Id value=`"$ModuleId`" />"
$xml | Set-Content $subXml -Encoding UTF8

# Our module's dependencies, into the engine bin where the loader actually looks for them. The module's own
# copies stay put too, so nothing is moved - only mirrored. TaleWorlds.* are skipped: the engine's own are
# already there, and a second copy of an engine assembly breaks type identity.
$engineBinServer = Join-Path $dstBin 'Win64_Shipping_Server'
Get-ChildItem "$ourModule\bin\Win64_Shipping_Client\*.dll" |
    Where-Object { $_.Name -notmatch '^(TaleWorlds|SandBox|StoryMode)\.' } |
    Copy-Item -Destination $engineBinServer -Force

# The UI and campaign assemblies our module references but the headless engine does not ship:
# SandBox.dll, TaleWorlds.MountAndBlade.View, the GauntletUI family, ViewModelCollections, Steamworks.NET.
# The stock dedicated-server build has none of them, and neither does the client's engine bin - they live
# in module folders. The official DS solves this by shipping them INSIDE its Coop module, which is why that
# module carried eighteen assemblies ours does not. Same solution, same source: take them from there.
# Ours are copied first and not overwritten, so our build always wins for anything both provide.
# The multiplayer server module's own assemblies, for the same reason: DedicatedServer.Windows.dll
# references DedicatedServer.Core.dll, and the loader resolves that from the engine bin, not the module.
$dsWinBin = Join-Path $srcEngine 'Modules\DedicatedServer.Windows\bin\Win64_Shipping_Server'
if (Test-Path $dsWinBin) {
    $winBridged = 0
    foreach ($dll in Get-ChildItem "$dsWinBin\*.dll") {
        $target = Join-Path $engineBinServer $dll.Name
        if (-not (Test-Path $target)) { Copy-Item $dll.FullName $target -Force; $winBridged++ }
    }
    Write-Host "bridged $winBridged assemblies from DedicatedServer.Windows"
}

$dsCoopBin = Join-Path $srcEngine 'Modules\Coop\bin\Win64_Shipping_Server'
if (Test-Path $dsCoopBin) {
    $bridged = 0
    foreach ($dll in Get-ChildItem "$dsCoopBin\*.dll") {
        $target = Join-Path $engineBinServer $dll.Name
        if (-not (Test-Path $target)) { Copy-Item $dll.FullName $target -Force; $bridged++ }
    }
    Write-Host "bridged $bridged support assemblies from the stock dedicated-server module"
}

# Microsoft.CSharp is part of the runtime the engine already carries, but the module loader only searches
# the engine bin, so it has to be there too. Taken from the engine's OWN runtime folder rather than the
# machine's SDK, so the version always matches the CLR the engine reports.
$runtimeDir = Get-ChildItem (Join-Path $dstEngine 'dotnet\shared\Microsoft.NETCore.App') -Directory -EA SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
if ($runtimeDir) {
    # Everything the runtime provides that the module loader cannot see. Microsoft.CSharp was the first one
    # found this way; Microsoft.Win32.Primitives was the next, and it surfaced only as a native exit with
    # "Cannot load: Microsoft.Win32.Primitives.dll" buried in the engine's own rgl error log. Rather than
    # discover them one crash at a time, mirror the whole runtime EXCEPT the assemblies the engine already
    # carries: a second copy of one of those breaks type identity, which is far harder to diagnose than a
    # missing file. Existing files are never overwritten, so the engine's own always wins.
    $runtimeBridged = 0
    foreach ($dll in Get-ChildItem "$($runtimeDir.FullName)\*.dll") {
        $target = Join-Path $engineBinServer $dll.Name
        if (-not (Test-Path $target)) { Copy-Item $dll.FullName $target -Force; $runtimeBridged++ }
    }
    Write-Host "bridged $runtimeBridged runtime assemblies from the engine runtime ($($runtimeDir.Name))"
}

# The server's data lives where the coop team's own layout puts it, under the user's Documents:
#
#   ...\Mount and Blade II Bannerlord\Game Saves\           <name>.sav + <name>.json
#   ...\Mount and Blade II Bannerlord\CoopData\             mod-config.json
#   ...\Mount and Blade II Bannerlord\CoopData\DedicatedServer\   server-config.json, command.txt
#
# BANNERLORD_USER_DIR is NOT used to move any of it. It redirects only the coop half of a save:
# CoopSaveManager honours it for the session .json, but the engine's own FileDriver ignores it and writes
# the .sav to the Documents folder regardless. Setting it puts the two halves of one save in different
# folders, and a split pair loads with every player sent back to character creation - which is exactly
# what happened when this script set it.
$userRoot    = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Mount and Blade II Bannerlord'
$clientSaves = Join-Path $userRoot 'Game Saves'
$coopData    = Join-Path $userRoot 'CoopData\DedicatedServer'
New-Item -ItemType Directory -Force $coopData | Out-Null

if (-not (Test-Path (Join-Path $clientSaves "$SaveName.sav"))) {
    throw "Save '$SaveName' not found in $clientSaves"
}
if (-not (Test-Path (Join-Path $clientSaves "$SaveName.json"))) {
    Write-Warning "No $SaveName.json beside the save: returning players will land in character creation."
}

$serverConfig = @{
    saveName        = $SaveName
    autosaveMinutes = 30
    password        = $Password
    logFile         = $true
    steam           = ($Visibility -ne 'None')
} | ConvertTo-Json
Set-Content (Join-Path $coopData 'server-config.json') $serverConfig -Encoding UTF8

# The campaign map scene. HeadlessMapScene reads Main_map with render-free init data, but it still needs
# the scene's navmesh and terrain on disk or there is nothing to path over. Two sources, in order:
#
#   1. The dedicated server's own stripped Main_map - identical navmesh/terrain/flora, with a scene.xscene
#      cut from 19.8 MB to 217 KB because every visual entity has been removed. Proven to load without a
#      renderer, which is exactly the case here.
#   2. The game's own SandBox scene, if that is not installed. Same navmesh, but the full xscene, which
#      carries entities that reference meshes and textures this process cannot load.
#
# Junctioned rather than copied: it is ~36 MB of read-only asset per staging run.
$sceneSources = @(
    (Join-Path $srcEngine 'Modules\DedicatedServer.Windows\SceneObj\Main_map'),
    (Join-Path $GameDir   'Modules\SandBox\SceneObj\Main_map')
)
$sceneSrc = $sceneSources | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($sceneSrc) {
    $sceneDst = Join-Path $dstCoop 'SceneObj\Main_map'
    New-Item -ItemType Directory -Force (Split-Path $sceneDst) | Out-Null
    New-Junction $sceneDst $sceneSrc
    $xscene = (Get-Item (Join-Path $sceneSrc 'scene.xscene') -EA SilentlyContinue).Length
    Write-Host ("map scene  -> {0}  (scene.xscene {1:N0} bytes)" -f (Split-Path (Split-Path $sceneSrc) -Leaf), $xscene)
} else {
    Write-Warning "No Main_map scene found; the campaign will have no navigation mesh"
}

# The Coop module ships the net472 flavour of Harmony, because the game client runs .NET Framework
# (CLR 4.0.30319). This headless starter is .NET 6, and that flavour bundles a MonoMod whose IL emitter
# calls ILGenerator.MarkSequencePoint - an API .NET Core removed - so EVERY patch it applies dies with
# MissingMethodException and the server can never host. Same package, right target: the net6.0 build from
# our own Lib.Harmony dependency, dropped only into the server layout. It is byte-identical to the one the
# official dedicated server ships, which is how this was confirmed.
$harmonyNet6 = Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'source\packages\Lib.Harmony.2.4.2\lib\net6.0\0Harmony.dll'
if (-not (Test-Path $harmonyNet6)) {
    $harmonyNet6 = Join-Path $env:USERPROFILE '.nuget\packages\lib.harmony\2.4.2\lib\net6.0\0Harmony.dll'
}
if (Test-Path $harmonyNet6) {
    foreach ($t in @((Join-Path $dstEngine 'bin\Win64_Shipping_Server\0Harmony.dll'),
                     (Join-Path $dstCoop  'bin\Win64_Shipping_Server\0Harmony.dll'))) {
        Copy-Item $harmonyNet6 $t -Force
    }
    Write-Host "harmony    -> net6.0 flavour in the server layout"
} else {
    Write-Warning "net6.0 0Harmony.dll not found; the server will fail to patch on .NET 6"
}

# Steam identifies a process by the app id in steam_appid.txt beside the exe, and this engine ships
# 1863440 - the dedicated-server TOOL. Coop talks to Steam as Bannerlord itself (261550): with the tool's
# id the client API never initialises ("Steamworks is not initialized"), the game server never finishes its
# anonymous logon, and the server logs "Could not request a Steam lobby" forever. It still listens on 4200,
# but nobody can find it through Steam - which is how these players actually join.
$appIdFile = Join-Path $dstBin 'Win64_Shipping_Server\steam_appid.txt'
Set-Content $appIdFile '261550' -Encoding ASCII -NoNewline
$env:SteamAppId = '261550'

Write-Host "staged -> $Root"
if ($StageOnly) { return }

# --- launch ------------------------------------------------------------------------------------
$starter = Join-Path $dstEngine 'bin\Win64_Shipping_Server\TaleWorlds.Starter.DotNetCore.exe'
if (-not (Test-Path $starter)) { throw "Headless starter not found at $starter" }

# /dedicatedcustomserver decides which folder the engine resolves EVERY module's assemblies from. With it,
# Win64_Shipping_Server - which is the only layout the stock modules here ship. Without it the engine looks
# for Win64_Shipping_Client and cannot even find SandBox.dll, so the whole game fails to assemble.
#
# It takes three arguments (port, region, index), exactly as the official wrapper passes them. Handing it the
# bare flag crashes the engine natively during Module Initialize with no managed exception to read - which
# cost two attempts to work out.
# DedicatedServer.Windows IS loaded, and has to be: it carries the loop that keeps a headless engine
# running. Without it the engine initialises every module - CoopMod included, which logs its build and then
# nothing - finds no server to run, and exits cleanly. Its DedicatedServer.Core.dll is bridged into the
# engine bin below, the same problem our own dependencies had.
# DedicatedServer.Windows is deliberately NOT loaded. It SHA-256 pins the official Coop module and exits
# with code 4 on any modified build - ours included, by design. It is also not needed: the headless engine
# ticks modules on its own (the heartbeat's engine-tick counter proves it), so our own module carries the
# host and the console instead.
# The launcher's own singleplayer order, from Configs\LauncherData.xml: Native, SandBoxCore, CustomBattle,
# Sandbox, StoryMode, then ours. Order matters - modules initialise in list order and later ones build on
# earlier ones - and so does the SET, because the save records which modules wrote it.
$modules = "_MODULES_*Native*SandBoxCore*CustomBattle*SandBox*StoryMode*$ModuleId*_MODULES_"
$argList = @('/dedicatedcustomserver', "$Port", $Region, '0', $modules,
             '/server', '/coopheadless', '/coopsave', $SaveName, '/coopvisibility', $Visibility)
if ($Password) { $argList += @('/cooppassword', $Password) }
if ($RunToken) { $argList += @('/cooptestrun', $RunToken) }

Write-Host ""
Write-Host "hosting     : $SaveName"
Write-Host "visibility  : $Visibility"
Write-Host ("password    : " + $(if ($Password) { 'set' } else { 'none' }))
Write-Host ("live test   : " + $(if ($RunToken) { "run token $RunToken" } else { 'off (no control channel)' }))
Write-Host "log         : $dstEngine\bin\Win64_Shipping_Server\Coop_server.log"
Write-Host ""
Write-Host "commands    : write a line into"
Write-Host "              $coopData\command.txt   (e.g.  save )"
Write-Host "Closing the window or Ctrl+C stops the server."
Write-Host ""

# Run in the FOREGROUND, in this console. Start-Process returns immediately and leaves the server with no
# stdin to read commands from, which is the whole point of running it here rather than in a game window.
Push-Location (Split-Path $starter)
try   { & $starter @argList }
finally { Pop-Location }

