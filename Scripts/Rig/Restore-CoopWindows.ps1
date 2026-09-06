<# Bring the two coop clients back, side by side, ready to play. #>
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CoopShow {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    public struct R { public int L,T,Rr,B; }
}
"@ -ErrorAction SilentlyContinue
$ALI = '76561198876156674'
foreach ($p in (Get-Process -Name Bannerlord -ErrorAction SilentlyContinue)) {
    if ($p.MainWindowHandle -eq 0) { continue }
    if ([CoopShow]::IsIconic($p.MainWindowHandle)) { [void][CoopShow]::ShowWindow($p.MainWindowHandle, 9) }
    $x = if ($p.MainWindowTitle -match $ALI) { 0 } else { 1280 }
    [void][CoopShow]::MoveWindow($p.MainWindowHandle, $x, 0, 1280, 720, $true)
}
Start-Sleep -Milliseconds 500
foreach ($p in (Get-Process -Name Bannerlord -ErrorAction SilentlyContinue)) {
    if ($p.MainWindowHandle -eq 0) { continue }
    $r = New-Object CoopShow+R; [void][CoopShow]::GetWindowRect($p.MainWindowHandle, [ref]$r)
    $who = if ($p.MainWindowTitle -match $ALI) { 'ALI ' } else { 'OMAR' }
    "$who pid=$($p.Id) at ($($r.L),$($r.T)) size=$($r.Rr-$r.L)x$($r.B-$r.T)"
}
