<#
Samples network and CPU load once per second for the rig's three processes and writes them as CSV, so every
duel or army run carries its before/after cost (PVP-SYNC-PLAN.md section 5, PVP-ARMY-SYNC-PLAN.md section 4.1).

    .\Capture-Load.ps1 -OutDir runs\2026-09-05-baseline -ServerPid 1234 -HostPid 2345 -ClientPid 3456

Runs until <OutDir>\stop exists or -Seconds elapse. At the end it also extracts the mesh packet profiles
from each client's log and the server's packet profile / worldQueue lines.

Per-second CSV columns (load-<role>.csv):
    utcMs,role,pid,cpuPercent,workingSetMb,threads,<every key=value the mod's coop.debug.movement.state prints>
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$ServerPid = 0,
    [int]$HostPid = 0,
    [int]$ClientPid = 0,
    [int]$Seconds = 3600,
    [string]$GameBin = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client',
    [string]$ServerLog = "$env:USERPROFILE\CoopDebugServer\engine\bin\Win64_Shipping_Server\Coop_server.log"
)

$ErrorActionPreference = 'Continue'
$rig = $PSScriptRoot
New-Item -ItemType Directory -Force $OutDir | Out-Null
$stopFile = Join-Path $OutDir 'stop'
if (Test-Path $stopFile) { Remove-Item $stopFile -Force }

function C($targetPid, $name, $cmdArgs, $t = 5000) {
    try {
        $o = (& "$rig\Send-LiveTest.ps1" -TargetPid $targetPid -Name $name -CommandArgs $cmdArgs -TimeoutMs $t) | ConvertFrom-Json
        if ($o.ok) { return [string]$o.result.output }
        return ""
    } catch { return "" }
}

$roles = @()
if ($ServerPid) { $roles += @{ role = 'server'; pid = $ServerPid; movement = $false } }
if ($HostPid)   { $roles += @{ role = 'host';   pid = $HostPid;   movement = $true } }
if ($ClientPid) { $roles += @{ role = 'client'; pid = $ClientPid; movement = $true } }
if ($roles.Count -eq 0) { Write-Output "nothing to capture"; exit 1 }

$cores = [Environment]::ProcessorCount
$lastCpu = @{}
$lastAt = @{}
$headerWritten = @{}
$movementKeys = @('profile','bulkHz','priorityHz','frameLimitHz','performanceCeilingHz','localAdaptiveHz','receiverCapHz',
                  'wireBytesPerSecond','localAgents','agents','senderMsPerSecond','receiverApplyMsPerSecond',
                  'receiverQueueMs','fps')

$deadline = (Get-Date).AddSeconds($Seconds)
$samples = 0
while ((Get-Date) -lt $deadline -and -not (Test-Path $stopFile)) {
    $tick = Get-Date
    # Same clock as the timeline and DuelEvents: .NET ticks since 0001-01-01, in milliseconds (not Unix time).
    $utcMs = [long]([DateTime]::UtcNow.Ticks / 10000)
    foreach ($r in $roles) {
        $proc = Get-Process -Id $r.pid -ErrorAction SilentlyContinue
        if (-not $proc) { continue }
        $cpu = ''
        $now = $proc.TotalProcessorTime.TotalMilliseconds
        if ($lastCpu.ContainsKey($r.pid)) {
            $elapsed = ($tick - $lastAt[$r.pid]).TotalMilliseconds
            if ($elapsed -gt 0) { $cpu = [math]::Round(100.0 * ($now - $lastCpu[$r.pid]) / $elapsed / $cores, 2) }
        }
        $lastCpu[$r.pid] = $now; $lastAt[$r.pid] = $tick
        $ws = [math]::Round($proc.WorkingSet64 / 1MB, 1)

        $fields = [ordered]@{}
        foreach ($k in $movementKeys) { $fields[$k] = '' }
        if ($r.movement) {
            $state = C $r.pid 'coop.debug.movement.state' @()
            # "key=value|key=value" or "key=value key=value": accept both separators.
            foreach ($pair in ($state -split '[|\s]+')) {
                if ($pair -match '^([A-Za-z]+)=(.*)$' -and $fields.Contains($Matches[1])) { $fields[$Matches[1]] = $Matches[2] }
            }
        }

        $file = Join-Path $OutDir ("load-" + $r.role + ".csv")
        if (-not $headerWritten.ContainsKey($r.role)) {
            ("utcMs,role,pid,cpuPercent,workingSetMb,threads," + ($movementKeys -join ',')) | Set-Content $file -Encoding utf8
            $headerWritten[$r.role] = $true
        }
        $row = @($utcMs, $r.role, $r.pid, $cpu, $ws, $proc.Threads.Count) + @($movementKeys | ForEach-Object { $fields[$_] })
        ($row -join ',') | Add-Content $file -Encoding utf8
    }
    $samples++
    $sleep = 1000 - [int]((Get-Date) - $tick).TotalMilliseconds
    if ($sleep -gt 0) { Start-Sleep -Milliseconds $sleep }
}

# Packet profiles: message COUNTS are what cause lag, and the mission mesh only shows up in the client logs.
foreach ($r in $roles) {
    if ($r.role -eq 'server') {
        if (Test-Path $ServerLog) {
            Select-String -Path $ServerLog -Pattern 'Packet profile|worldQueue|OverloadedPeer' |
                ForEach-Object { $_.Line } | Set-Content (Join-Path $OutDir 'server-profile.txt') -Encoding utf8
        }
        continue
    }
    $candidates = @((Join-Path $GameBin ("Coop_client_" + $r.pid + ".log")), (Join-Path $GameBin 'Coop_client.log'))
    $logPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($logPath) {
        Select-String -Path $logPath -Pattern '\[Mesh\] Packet profile|mesh routes|OverloadedPeer|MovementRate' |
            ForEach-Object { $_.Line } | Set-Content (Join-Path $OutDir ("mesh-profile-" + $r.role + ".txt")) -Encoding utf8
    }
}
Write-Output ("capture done: {0} samples -> {1}" -f $samples, $OutDir)
