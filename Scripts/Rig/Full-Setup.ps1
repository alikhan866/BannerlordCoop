<#
Launches the local two-client rig: installs the Debug build as the CoopDebug module, starts the headless
dedicated server on a save, launches two windowed clients side by side, and optionally stages a PvE bandit
battle (the original animation-test setup).

    .\Full-Setup.ps1                          # bandit battle on the default save (PvE animation runs)
    .\Full-Setup.ps1 -Save pvp_duel -Scenario None   # rig only; Duel-Test.ps1 stages its own fight

Prints SERVER_PID=, ALI_PID=, OMAR_PID= lines that the driving scripts parse.
No mouse clicks: everything the player used to do by hand has a command.
#>
param(
    [string]$Save = '6-18_31_08_2026',
    [ValidateSet('Bandit', 'None')][string]$Scenario = 'Bandit',
    [string]$RunToken = 't1',
    [switch]$Rebuild,
    [switch]$SkipInstall,
    [string]$BuildDir = '',
    [string]$Ali = '76561198876156674',
    [string]$Omar = '76561199074278663',
    [string]$AliParty = 'MobileParty_Created_63468',
    [string]$OmarParty = 'MobileParty_Player425',
    [string]$Troop = 'CharacterObject_imperial_veteran_infantryman',
    [int]$Troops = 5000
)

$ErrorActionPreference = 'Continue'
$rig  = $PSScriptRoot
$log  = "$env:USERPROFILE\CoopDebugServer\engine\bin\Win64_Shipping_Server\Coop_server.log"
$exe  = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client\Bannerlord.exe'
$mods = '_MODULES_*Native*SandBoxCore*Sandbox*CustomBattle*StoryMode*CoopDebug*_MODULES_'

function C($targetPid, $name, $cmdArgs, $t = 300000) {
    try {
        $o = (& "$rig\Send-LiveTest.ps1" -TargetPid $targetPid -Name $name -CommandArgs $cmdArgs -TimeoutMs $t) | ConvertFrom-Json
        if ($o.ok) { return [string]$o.result.output }
        return "FAILED: " + $o.error.message
    } catch { return "THREW $_" }
}

Write-Output "== 1. kill + install module =="
Get-CimInstance Win32_Process |
    Where-Object { $_.Name -like 'Bannerlord*' -or $_.Name -like 'TaleWorlds.Starter*' -or $_.Name -like 'Watchdog*' } |
    ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch {} }
Start-Sleep -Seconds 4
if (-not $SkipInstall) { & "$rig\install-testmodule.ps1" -BuildDir $BuildDir | Out-Null }

Write-Output "== 2. server on $Save =="
$serverArgs = @{ SaveName = $Save; Visibility = 'Public'; RunToken = $RunToken }
if ($Rebuild) { $serverArgs['Rebuild'] = $true }
# The server runs in the foreground of its own console; launch it detached so this script can go on.
$serverScript = "$rig\Start-CoopDebug-DedicatedServer.ps1"
$argString = "-NoProfile -ExecutionPolicy Bypass -File `"$serverScript`" -SaveName `"$Save`" -Visibility Public -RunToken $RunToken" + $(if ($Rebuild) { ' -Rebuild' } else { '' })
Start-Process powershell.exe -ArgumentList $argString -WindowStyle Minimized | Out-Null

$deadline = (Get-Date).AddMinutes(9); $serverPid = $null
while ((Get-Date) -lt $deadline) {
    if (Test-Path $log) {
        $tail = Get-Content $log -Tail 400 -ErrorAction SilentlyContinue
        $m = $tail | Select-String -Pattern 'Listening on BannerlordCoop\.LiveTest\.v1\.(\d+) as server' | Select-Object -Last 1
        if ($m) { $serverPid = [int]$m.Matches[0].Groups[1].Value }
        if ($serverPid -and ($tail -match 'console ready')) { break }
    }
    Start-Sleep -Seconds 8
}
if (-not $serverPid) { Write-Output "ABORT: no server endpoint"; exit 1 }
Write-Output "SERVER_PID=$serverPid"

# Windowed, so the two clients do not both grab the screen and trap the mouse in the middle of it.
# The file is read-only and the game rewrites it on exit, so clear the flag and set it every launch.
$engineCfg = "$([Environment]::GetFolderPath('MyDocuments'))\Mount and Blade II Bannerlord\Configs\engine_config.txt"
if (Test-Path $engineCfg) {
    try {
        Set-ItemProperty -Path $engineCfg -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        $cfg = Get-Content $engineCfg
        $cfg = $cfg -replace '^display_mode\s*=.*',   'display_mode = 0'
        $cfg = $cfg -replace '^display_width\s*=.*',  'display_width = 1280'
        $cfg = $cfg -replace '^display_height\s*=.*', 'display_height = 720'
        Set-Content -Path $engineCfg -Value $cfg -Encoding utf8
        Write-Output "  windowed 1280x720 (display_mode=0)"
    } catch { Write-Output ("  WARN could not force windowed: " + $_) }
}

Write-Output "== 3. clients =="
$cp = @{}
foreach ($id in @($Ali, $Omar)) {
    $p = Start-Process $exe -ArgumentList @('/singleplayer','/client','/autoconnect',$mods,'/cooptestrun',$RunToken,'/platformid',$id) `
        -WorkingDirectory (Split-Path $exe) -PassThru
    $cp[$id] = $p.Id
}
Write-Output "ALI_PID=$($cp[$Ali])"
Write-Output "OMAR_PID=$($cp[$Omar])"

# Both clients open at the SAME centred rectangle, one exactly on top of the other, so the focused one
# captures the mouse and pins it to the middle of the screen while the other is hidden underneath. That is
# what "my cursor gets stuck in the middle" is. Windowed mode alone does not fix it - a mission always
# captures the mouse for mouse-look - they have to be side by side to be usable at all.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CoopWin {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@ -ErrorAction SilentlyContinue
# "Mod change detected": the game compares the module set on disk with LauncherData.xml and blocks on a prompt
# until someone presses Enter. Nobody is there to press it, so the rig does - with a posted key event, because
# SendKeys is refused from a non-interactive session.
function Dismiss-ModChangePrompt {
    foreach ($proc in (Get-Process -Name Bannerlord -ErrorAction SilentlyContinue)) {
        if ($proc.MainWindowHandle -eq 0 -or $proc.MainWindowTitle -notlike 'Mod change*') { continue }
        [void][CoopWin]::SetForegroundWindow($proc.MainWindowHandle); Start-Sleep -Milliseconds 400
        [void][CoopWin]::PostMessage($proc.MainWindowHandle, 0x0100, [IntPtr]0x0D, [IntPtr]0x001C0001)   # WM_KEYDOWN VK_RETURN
        Start-Sleep -Milliseconds 80
        [void][CoopWin]::PostMessage($proc.MainWindowHandle, 0x0101, [IntPtr]0x0D, [IntPtr]0xC01C0001)   # WM_KEYUP
        Write-Output ("  dismissed 'Mod change detected' on " + $proc.Id)
    }
}
$placed = @{}
$winDeadline = (Get-Date).AddMinutes(4)
while ((Get-Date) -lt $winDeadline -and $placed.Count -lt 2) {
    Dismiss-ModChangePrompt
    foreach ($proc in (Get-Process -Name Bannerlord -ErrorAction SilentlyContinue)) {
        if ($proc.MainWindowHandle -eq 0 -or $placed.ContainsKey($proc.Id)) { continue }
        # Match on PROCESS ID, not window title. The title is still the stock one for the first few
        # seconds - it only becomes "Coop Client <steamid>" once the mod has loaded - so a title match
        # here silently sends BOTH clients to the same slot, which is the stacked-window bug all over
        # again. The pids are known from launch and are true immediately.
        $x = if ($proc.Id -eq $cp[$Ali]) { 0 } else { 1280 }
        # A minimized window reports (-32000,-32000) and MoveWindow will not bring it back, so it stays
        # invisible and the placement silently does nothing. Restore first, then place.
        if ([CoopWin]::IsIconic($proc.MainWindowHandle)) {
            [void][CoopWin]::ShowWindow($proc.MainWindowHandle, 9)   # SW_RESTORE
        }
        [void][CoopWin]::MoveWindow($proc.MainWindowHandle, $x, 0, 1280, 720, $true)
        $placed[$proc.Id] = $x
        Write-Output ("  window " + $proc.Id + " -> x=" + $x)
    }
    if ($placed.Count -lt 2) { Start-Sleep -Seconds 5 }
}

Write-Output "== 4. waiting until both clients are connected =="
# players.list only says "registered" while a client is still applying the join snapshot, so the scenario
# steps below retry their first server command until it stops refusing. This wait just avoids asking before
# either client has even connected.
$deadline = (Get-Date).AddMinutes(15); $ready = ''
while ((Get-Date) -lt $deadline) {
    Dismiss-ModChangePrompt
    $ready = C $serverPid 'coop.debug.players.list' @()
    if ($ready -match [regex]::Escape($Ali) -and $ready -match [regex]::Escape($Omar)) { break }
    Start-Sleep -Seconds 15
}
Write-Output ("ready: " + (($ready -split "`n" | Select-Object -First 4) -join ' | '))
Start-Sleep -Seconds 20

if ($Scenario -eq 'Bandit') {
    Write-Output "== 5. bandit battle for $Ali =="
    $deadline = (Get-Date).AddMinutes(10); $battle = ''
    while ((Get-Date) -lt $deadline) {
        $battle = C $serverPid 'coop.debug.map_event.start_nearest_bandit_attack' @($Ali, '6000')
        if ($battle -match 'Started attack by') { break }
        Start-Sleep -Seconds 20
    }
    Write-Output "battle: $battle"
    if ($battle -notmatch 'Started attack by') { Write-Output "ABORT: battle never started"; exit 1 }

    $enemy = ([regex]::Match($battle, 'registry id (MobileParty_\S+?)[,)]')).Groups[1].Value
    Write-Output "enemy party = $enemy"

    Write-Output "== 6. rosters: $Troops / $Troops / $($Troops * 2) =="
    Write-Output ("  ali  : " + (C $serverPid 'coop.debug.mobile_party.set_troops' @($AliParty, $Troop, "$Troops")))
    Write-Output ("  omar : " + (C $serverPid 'coop.debug.mobile_party.set_troops' @($OmarParty, $Troop, "$Troops")))
    if ($enemy) {
        Write-Output ("  enemy: " + (C $serverPid 'coop.debug.mobile_party.set_troops' @($enemy, $Troop, "$($Troops * 2)")))
    }

    Write-Output "== 7. put Omar into Alifreeze's battle =="
    Write-Output ("  " + (C $serverPid 'coop.debug.map_event.add_player_to_battle' @($Ali, $Omar)))

    Start-Sleep -Seconds 5
    Write-Output "== 8. verify =="
    Write-Output ("  ali  : " + (C $cp[$Ali]  'coop.debug.encounter.state' @() 60000))
    Write-Output ("  omar : " + (C $cp[$Omar] 'coop.debug.encounter.state' @() 60000))
}
Write-Output "== done =="
