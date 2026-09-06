<#
Map drift test: do the clients see AI parties where the server has them, and do those copies move smoothly?

Launches the rig (or reuses running PIDs), starts the campaign clock on the server, then every -SampleSeconds asks
the server and both clients for `coop.debug.mobile_party.positions_dump lords` (lord parties, caravans, armies) and
writes each answer to positions-<who>.txt in the run folder. `analyze_drift.py` joins the rows by party id on the
shared wall clock: distance between a client's copy and the server's is the drift; a copy that moves further between
two samples than its speed allows is a jump ("lords teleport"); a copy with somewhere to go and no next waypoint is
frozen. -BattleInterlude fights a short 100 v 100 in the middle so the return-to-map moment is sampled too.

    .\Map-Drift-Test.ps1 -Seconds 300
    .\Map-Drift-Test.ps1 -Seconds 420 -BattleInterlude
#>
param(
    [string]$Tag = 'drift',
    [string]$Save = 'pvp_duel',
    [int]$Seconds = 300,
    [int]$SampleSeconds = 5,
    [ValidateSet('Play_1x', 'Play_2x')][string]$TimeMode = 'Play_1x',
    [switch]$BattleInterlude,
    # Client-side delivery of position corrections: 'on' bleeds a correction in over a few frames, 'off' assigns it
    # in one (the behaviour before the fix). The counters at the end of the run say what the player would have seen.
    [ValidateSet('on', 'off', 'leave')][string]$Smoothing = 'leave',
    [int]$AliPid = 0,
    [int]$OmarPid = 0,
    [int]$ServerPid = 0,
    [string]$Ali = '76561198876156674',
    [string]$Omar = '76561199074278663'
)
$ErrorActionPreference = 'Continue'
$rig = $PSScriptRoot
$runDir = Join-Path $rig ("runs\" + (Get-Date -Format 'yyyy-MM-dd-HHmm') + "-$Tag")
New-Item -ItemType Directory -Force $runDir | Out-Null
function Say($m) { $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m; Write-Host $line; Add-Content (Join-Path $runDir 'run.log') $line }
function C($targetPid, $name, $cmdArgs, $t = 120000) {
    try {
        $o = (& "$rig\Send-LiveTest.ps1" -TargetPid $targetPid -Name $name -CommandArgs $cmdArgs -TimeoutMs $t) | ConvertFrom-Json
        if ($o.ok) { return [string]$o.result.output }
        return 'FAILED: ' + [string]$o.error
    } catch { return 'FAILED: ' + $_.Exception.Message }
}

Say "map drift run: seconds=$Seconds sample=$SampleSeconds time=$TimeMode battleInterlude=$BattleInterlude"
if (-not $AliPid -or -not $OmarPid -or -not $ServerPid) {
    Say "launching rig on save '$Save' (a few minutes)"
    $setup = & "$rig\Full-Setup.ps1" -Save $Save -Scenario None 2>&1 | Out-String
    $setup -split "`n" | ForEach-Object { if ($_ -match 'PID=|ready:|ABORT|windowed|window ') { Say ("    " + $_.Trim()) } }
    $AliPid    = [int]([regex]::Match($setup, 'ALI_PID=(\d+)')).Groups[1].Value
    $OmarPid   = [int]([regex]::Match($setup, 'OMAR_PID=(\d+)')).Groups[1].Value
    $ServerPid = [int]([regex]::Match($setup, 'SERVER_PID=(\d+)')).Groups[1].Value
    if (-not $AliPid -or -not $OmarPid -or -not $ServerPid) { Say "ABORT: rig did not come up"; exit 1 }
}
Set-Content (Join-Path $runDir 'pids.txt') "SERVER_PID=$ServerPid`nALI_PID=$AliPid`nOMAR_PID=$OmarPid" -Encoding utf8
$peers = @(@{ n = 'server'; id = $ServerPid }, @{ n = 'ali'; id = $AliPid }, @{ n = 'omar'; id = $OmarPid })

# The players are parked in Hold on the map; let the world run. Right after a launch the server refuses to unpause
# while a registered player still counts as disconnected (PlayersConnectedPolicy), so keep asking until it takes.
$tDeadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt $tDeadline) {
    $reply = C $ServerPid 'coop.debug.set_time_mode' @($TimeMode, 'force-live-test')
    Say ("  time mode -> " + $reply)
    if ($reply -match "to $TimeMode") { break }
    Start-Sleep -Seconds 10
}
Start-Sleep -Seconds 3
foreach ($p in $peers) { Say ("  " + $p.n + " time: " + ((C $p.id 'coop.debug.get_time_mode' @()) -replace "`r?`n", ' ')) }
if ($Smoothing -ne 'leave') {
    foreach ($p in @(@{ n = 'ali'; id = $AliPid }, @{ n = 'omar'; id = $OmarPid })) {
        Say ("  smoothing " + $p.n + " -> " + (C $p.id 'coop.debug.mobile_party.position_smoothing' @($Smoothing)))
    }
} else {
    foreach ($p in @(@{ n = 'ali'; id = $AliPid }, @{ n = 'omar'; id = $OmarPid })) {
        C $p.id 'coop.debug.mobile_party.position_smoothing' @('reset') | Out-Null
    }
}

# Load capture alongside (server + both clients), so the sampling cost is visible.
$capture = Start-Process powershell.exe -PassThru -WindowStyle Minimized -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$rig\Capture-Load.ps1`"",
    '-OutDir', "`"$runDir`"", '-ServerPid', $ServerPid, '-HostPid', $AliPid, '-ClientPid', $OmarPid, '-Seconds', "$($Seconds + 60)")

$t0 = Get-Date
$interludeAt = [int]($Seconds / 2)
$interludeDone = -not $BattleInterlude
$samples = 0
while (((Get-Date) - $t0).TotalSeconds -lt $Seconds) {
    $elapsed = [int]((Get-Date) - $t0).TotalSeconds
    if (-not $interludeDone -and $elapsed -ge $interludeAt) {
        # A short battle in the middle: both players leave the map and come back.
        Say "  battle interlude: fixture + 100 v 100"
        $b = & "$rig\Army-Test.ps1" -Tag ($Tag + '-interlude') -Preset even_small -AliPid $AliPid -OmarPid $OmarPid -ServerPid $ServerPid -MaxSeconds 300 2>&1 | Out-String
        ($b -split "`n" | Select-Object -Last 3) | ForEach-Object { Say ("    " + $_.Trim()) }
        foreach ($try in 1..12) { $reply = C $ServerPid 'coop.debug.set_time_mode' @($TimeMode, 'force-live-test'); Say ("  time mode -> " + $reply); if ($reply -match "to $TimeMode") { break }; Start-Sleep -Seconds 10 }
        $interludeDone = $true
    }
    foreach ($p in $peers) {
        $utcMs = [long]([DateTime]::UtcNow.Ticks / 10000)
        $dump = C $p.id 'coop.debug.mobile_party.positions_dump' @('lords') 30000
        ("SAMPLE utcMs=$utcMs elapsed=$elapsed`n" + $dump) | Add-Content (Join-Path $runDir ("positions-" + $p.n + ".txt")) -Encoding utf8
    }
    $samples++
    if ($elapsed % 60 -lt $SampleSeconds) {
        $head = ((Get-Content (Join-Path $runDir 'positions-server.txt') | Select-Object -Last 400 | Select-String 'POSITIONS') | Select-Object -Last 1).Line
        Say ("  +$elapsed s samples=$samples " + $head)
    }
    Start-Sleep -Seconds $SampleSeconds
}
Say ("  time mode -> " + (C $ServerPid 'coop.debug.set_time_mode' @('Pause', 'force-live-test')))
foreach ($p in @(@{ n = 'ali'; id = $AliPid }, @{ n = 'omar'; id = $OmarPid })) {
    $snap = C $p.id 'coop.debug.mobile_party.position_smoothing' @() 30000
    $snap | Out-File -FilePath (Join-Path $runDir ("smoothing-" + $p.n + ".txt")) -Encoding utf8
    Say ("  " + $p.n + " " + $snap)
}
Set-Content (Join-Path $runDir 'stop') 'stop'

# Each client's own log, for the [PartyDiag] census and any drift-correction lines.
$gameBin = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client'
foreach ($p in @(@{ n = 'ali'; id = $AliPid }, @{ n = 'omar'; id = $OmarPid })) {
    $src = Join-Path $gameBin ("Coop_client_" + $p.id + ".log")
    if (-not (Test-Path $src)) { $src = Join-Path $gameBin 'Coop_client.log' }
    if (Test-Path $src) { Copy-Item $src (Join-Path $runDir ("client-" + $p.n + ".log")) -ErrorAction SilentlyContinue }
}
$serverLog = "$env:USERPROFILE\CoopDebugServer\engine\bin\Win64_Shipping_Server\Coop_server.log"
if (Test-Path $serverLog) { Copy-Item $serverLog (Join-Path $runDir 'server.log') -ErrorAction SilentlyContinue }

Say "analysis:"
$report = & python "$rig\analyze_drift.py" $runDir 2>&1 | ForEach-Object { "$_" }
$report | Out-File -FilePath (Join-Path $runDir 'report.md') -Encoding utf8
$report | ForEach-Object { Write-Host $_ }
Say ("done. run folder: $runDir  pids server=$ServerPid ali=$AliPid omar=$OmarPid")
