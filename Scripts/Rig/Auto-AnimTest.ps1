<#
Unattended animation test. Launches the rig, drives both clients into a battle, makes the
armies charge, records the timeline on both, and prints the owner-versus-puppet numbers.

No mouse clicks. Every step the player used to do by hand has a command:
    start_attack_mission     the "Attack" button
    finish_deployment        the deployment screen
    charge_owned_formations  ordering the troops in
The timeline arms itself once enough agents have been SEEN SWINGING, so the recording
covers the melee rather than the walk towards it.
#>
param(
    [string]$Tag = 'run',
    [switch]$SkipSetup,
    [int]$AliPid = 0,
    [int]$OmarPid = 0,
    [int]$RecordSeconds = 60,
    [string]$AliTroop = '',
    [string]$OmarTroop = '',
    [string]$EnemyTroop = '',
    [switch]$KeepWindowsUp
)

$ErrorActionPreference = 'Continue'
$sp = $PSScriptRoot
$runs = Join-Path $PSScriptRoot "runs"
New-Item -ItemType Directory -Force $runs | Out-Null

function C($targetPid, $name, $cmdArgs, $t = 120000) {
    try {
        $o = (& "$sp\Send-LiveTest.ps1" -TargetPid $targetPid -Name $name -CommandArgs $cmdArgs -TimeoutMs $t) | ConvertFrom-Json
        if ($o.ok) { return [string]$o.result.output }
        return "FAILED: " + $o.error.message
    } catch { return "THREW $_" }
}
function Say($m) { Write-Output ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m) }

# ---- 1. rig -------------------------------------------------------------------------
if ($SkipSetup -and $AliPid -and $OmarPid) {
    Say "reusing rig: ali=$AliPid omar=$OmarPid"
} else {
    Say "launching rig (this takes a few minutes)"
    $setup = & "$sp\Full-Setup.ps1" 2>&1 | Out-String
    $setup -split "`n" | ForEach-Object { if ($_ -match 'PID=|battle:|ABORT') { Write-Output ("    " + $_.Trim()) } }
    $AliPid    = [int]([regex]::Match($setup, 'ALI_PID=(\d+)')).Groups[1].Value
    $OmarPid   = [int]([regex]::Match($setup, 'OMAR_PID=(\d+)')).Groups[1].Value
    $ServerPid = [int]([regex]::Match($setup, 'SERVER_PID=(\d+)')).Groups[1].Value
    if (-not $AliPid -or -not $OmarPid) { Say "ABORT: no client pids"; exit 1 }

    # Optional roster override, applied BEFORE the mission starts so the troops actually spawn.
    # Archers are needed to produce any ranged animation at all to measure.
    $enemyParty = ([regex]::Match($setup, 'enemy party = (MobileParty_\S+)')).Groups[1].Value
    if ($AliTroop)   { Say ("  ali roster   -> " + (C $ServerPid 'coop.debug.mobile_party.set_troops' @('MobileParty_Created_63468', $AliTroop, '5000'))) }
    if ($OmarTroop)  { Say ("  omar roster  -> " + (C $ServerPid 'coop.debug.mobile_party.set_troops' @('MobileParty_Player425', $OmarTroop, '5000'))) }
    if ($EnemyTroop -and $enemyParty) { Say ("  enemy roster -> " + (C $ServerPid 'coop.debug.mobile_party.set_troops' @($enemyParty, $EnemyTroop, '10000'))) }
}
$peers = @(@{n='ali'; id=$AliPid}, @{n='omar'; id=$OmarPid})

# ---- 2. answer the troop prompt up front so nothing blocks ---------------------------
foreach ($p in $peers) { C $p.id 'coop.debug.battle.troop_preference' @('all') | Out-Null }

# ---- 3. press Attack ----------------------------------------------------------------
# The map event has to have REPLICATED to a client before it will start the mission, and that lands a
# little after the server reports the battle. Retry rather than abort: "The main party has no replicated
# map event" just means we asked too early.
Say "entering the mission"
$attackDeadline = (Get-Date).AddMinutes(5)
$started = @{}
while ((Get-Date) -lt $attackDeadline -and $started.Count -lt 2) {
    foreach ($p in $peers) {
        if ($started.ContainsKey($p.n)) { continue }
        $r = C $p.id 'coop.debug.map_event.start_attack_mission' @()
        if ($r -match 'Starting attack mission') { $started[$p.n] = $true; Say ("  " + $p.n + ": " + $r) }
        elseif ($r -notmatch 'no replicated map event') { Say ("  " + $p.n + ": " + $r) }
    }
    if ($started.Count -lt 2) { Start-Sleep -Seconds 10 }
}
if ($started.Count -lt 2) { Say "ABORT: could not start the mission on both clients"; exit 1 }

# ---- 4. wait until both are actually in the mission ----------------------------------
$deadline = (Get-Date).AddMinutes(6); $inMission = $false
while ((Get-Date) -lt $deadline) {
    $states = @{}
    foreach ($p in $peers) { $states[$p.n] = C $p.id 'coop.debug.battle.state' @() }
    if (($states['ali'] -match 'activeAgents=(\d+)') -and ($states['omar'] -match 'activeAgents=(\d+)')) {
        $a = [int]([regex]::Match($states['ali'],  'activeAgents=(\d+)')).Groups[1].Value
        $o = [int]([regex]::Match($states['omar'], 'activeAgents=(\d+)')).Groups[1].Value
        Say ("  agents ali=$a omar=$o")
        if ($a -gt 50 -and $o -gt 50) { $inMission = $true; break }
    }
    Start-Sleep -Seconds 15
}
if (-not $inMission) { Say "ABORT: never reached the battlefield"; exit 1 }

# ---- 5. skip deployment, order the charge -------------------------------------------
foreach ($p in $peers) { C $p.id 'coop.debug.map_event.finish_deployment' @() | Out-Null }
Start-Sleep -Seconds 10
foreach ($p in $peers) { Say ("  charge " + $p.n + ": " + (C $p.id 'coop.debug.battle.charge_owned_formations' @())) }

# ---- 6. which side is host? (it changes between runs) --------------------------------
$role = @{}
foreach ($p in $peers) {
    $st = C $p.id 'coop.debug.battle.state' @()
    $role[$p.n] = if ($st -match 'host=True') { 'host' } else { 'client' }
}
Say ("roles: ali=" + $role['ali'] + " omar=" + $role['omar'])

# ---- 7. arm the counters and the timeline -------------------------------------------
foreach ($p in $peers) {
    C $p.id 'coop.debug.battle.windup' @('start') | Out-Null
    C $p.id 'coop.debug.battle.animation_timeline' @('start') | Out-Null
}
Say "recording - the timeline arms itself once the armies are in melee"

# ---- 8. wait for contact, then for the buffers to fill --------------------------------
$deadline = (Get-Date).AddMinutes(12); $armed = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 20
    $snap = C $AliPid 'coop.debug.battle.windup' @('snapshot') 180000
    $arrived = 0
    if ($snap -match 'WINDUP arrived=(\d+)') { $arrived = [int]$Matches[1] }
    # BOTH sides must have armed, or their recording windows do not overlap and every owner
    # moment lands with no puppet sample to compare against.
    $tracked = @{}
    foreach ($p in $peers) {
        $tl = C $p.id 'coop.debug.battle.animation_timeline' @('snapshot') 300000
        $tracked[$p.n] = ([regex]::Matches($tl, '[0-9a-f]{8} (PUPPET|LOCAL) n=')).Count
    }
    Say ("  windups=$arrived tracked ali=" + $tracked['ali'] + " omar=" + $tracked['omar'])
    if ($tracked['ali'] -gt 0 -and $tracked['omar'] -gt 0) { $armed = $true; break }
}
if (-not $armed) { Say "ABORT: timeline never armed - the armies never met" }

Say ("recording for $RecordSeconds s")
Start-Sleep -Seconds $RecordSeconds

# ---- 9. dump ------------------------------------------------------------------------
$aliFile  = "$runs\tl_${Tag}_ali.txt"
$omarFile = "$runs\tl_${Tag}_omar.txt"
(C $AliPid  'coop.debug.battle.animation_timeline' @('snapshot') 300000) | Out-File -FilePath $aliFile  -Encoding utf8
(C $OmarPid 'coop.debug.battle.animation_timeline' @('snapshot') 300000) | Out-File -FilePath $omarFile -Encoding utf8
(C $AliPid  'coop.debug.battle.windup'   @('snapshot') 300000) | Out-File -FilePath "$runs\wu_${Tag}_ali.txt"  -Encoding utf8
(C $OmarPid 'coop.debug.battle.windup'   @('snapshot') 300000) | Out-File -FilePath "$runs\wu_${Tag}_omar.txt" -Encoding utf8

# ---- 10. analyse (host file first, then client) ---------------------------------------
$hostFile   = if ($role['ali'] -eq 'host') { $aliFile } else { $omarFile }
$clientFile = if ($role['ali'] -eq 'host') { $omarFile } else { $aliFile }
Say "analysis:"
& python "$sp\analyze_timeline.py" $hostFile $clientFile
# ---- 11. give the mouse back -----------------------------------------------------------
# A running mission CAPTURES the mouse and pins it to the window centre for mouse-look, so leaving a
# client focused at the end of a run leaves the cursor stuck. Minimising both releases it. The rig
# stays up and the battle keeps running; restore the windows when you actually want to play.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CoopRelease {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowA(string cls, string win);
}
"@ -ErrorAction SilentlyContinue
if (-not $KeepWindowsUp) {
    foreach ($proc in (Get-Process -Name Bannerlord -ErrorAction SilentlyContinue)) {
        if ($proc.MainWindowHandle -ne 0) {
            [void][CoopRelease]::ShowWindow($proc.MainWindowHandle, 6)   # SW_MINIMIZE
        }
    }
    $desktop = [CoopRelease]::FindWindowA("Progman", $null)
    if ($desktop -ne [IntPtr]::Zero) { [void][CoopRelease]::SetForegroundWindow($desktop) }
    Say "clients minimized - your mouse is free (use Restore-CoopWindows.ps1 to bring them back)"
}

Say ("done. pids ali=$AliPid omar=$OmarPid")
