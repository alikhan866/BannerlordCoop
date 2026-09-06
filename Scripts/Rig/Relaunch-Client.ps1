# Relaunch one debug client after a simulated crash (Army-Test -Scenario leave -LeaveMode crash -RejoinAfterSeconds N).
# Same launch line as Full-Setup.ps1, the same "Mod change detected" dismissal and window placement, then waits until
# the server lists the player again. Prints CLIENT_PID=<pid> and connectedAfterSeconds=<s>.
param(
    [Parameter(Mandatory = $true)][string]$Id,
    [Parameter(Mandatory = $true)][int]$ServerPid,
    [int]$X = 1280,
    [string]$RunToken = 't1',
    [int]$MaxMinutes = 10
)
$ErrorActionPreference = 'Continue'
$rig  = $PSScriptRoot
$exe  = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client\Bannerlord.exe'
$mods = '_MODULES_*Native*SandBoxCore*Sandbox*CustomBattle*StoryMode*CoopDebug*_MODULES_'

function C($targetPid, $name, $cmdArgs, $t = 60000) {
    try {
        $o = (& "$rig\Send-LiveTest.ps1" -TargetPid $targetPid -Name $name -CommandArgs $cmdArgs -TimeoutMs $t) | ConvertFrom-Json
        if ($o.ok) { return [string]$o.result.output }
        return 'FAILED: ' + [string]$o.error
    } catch { return 'FAILED: ' + $_.Exception.Message }
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CoopWinR {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@ -ErrorAction SilentlyContinue

$t0 = Get-Date
$p = Start-Process $exe -ArgumentList @('/singleplayer', '/client', '/autoconnect', $mods, '/cooptestrun', $RunToken, '/platformid', $Id) `
    -WorkingDirectory (Split-Path $exe) -PassThru
Write-Output "CLIENT_PID=$($p.Id)"

$placed = $false
$deadline = (Get-Date).AddMinutes($MaxMinutes)
while ((Get-Date) -lt $deadline) {
    $proc = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if (-not $proc) { Write-Output 'ABORT: client exited'; exit 1 }
    if ($proc.MainWindowHandle -ne 0) {
        if ($proc.MainWindowTitle -like 'Mod change*') {
            [void][CoopWinR]::SetForegroundWindow($proc.MainWindowHandle); Start-Sleep -Milliseconds 400
            [void][CoopWinR]::PostMessage($proc.MainWindowHandle, 0x0100, [IntPtr]0x0D, [IntPtr]0x001C0001)
            Start-Sleep -Milliseconds 80
            [void][CoopWinR]::PostMessage($proc.MainWindowHandle, 0x0101, [IntPtr]0x0D, [IntPtr]0xC01C0001)
            Write-Output "  dismissed 'Mod change detected'"
        } elseif (-not $placed) {
            if ([CoopWinR]::IsIconic($proc.MainWindowHandle)) { [void][CoopWinR]::ShowWindow($proc.MainWindowHandle, 9) }
            [void][CoopWinR]::MoveWindow($proc.MainWindowHandle, $X, 0, 1280, 720, $true)
            $placed = $true
            Write-Output ("  window " + $proc.Id + " -> x=" + $X)
        }
    }
    # The server can still list the crashed player until its connection times out, so require the NEW process to
    # answer on its own live-test pipe as well (the pipe is up once the mod has loaded and the campaign is in).
    $answer = C $p.Id 'coop.debug.encounter.state' @() 10000
    $list = C $ServerPid 'coop.debug.players.list' @()
    # ENCOUNTER_STATE is only printed once the campaign is loaded; anything else is the pipe answering early.
    if ($answer -match 'ENCOUNTER_STATE' -and $list -match [regex]::Escape($Id)) {
        Write-Output ("connectedAfterSeconds=" + [int]((Get-Date) - $t0).TotalSeconds)
        exit 0
    }
    Start-Sleep -Seconds 5
}
Write-Output 'ABORT: client did not reconnect in time'
exit 1
