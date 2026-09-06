# Installs the local DEBUG build as a SEPARATE Bannerlord module id ("CoopDebug").
#
# Why a separate id: a second folder also claiming id "Coop" makes the launcher load Coop twice
# (_MODULES_*Coop*Coop*_MODULES_) and the local build shadows the Steam Workshop one. That breaks
# normal play. CoopDebug is never ticked in the launcher; test runs pass it explicitly.
#
#   .\install-testmodule.ps1            # install / refresh
#   .\install-testmodule.ps1 -Uninstall # remove, restoring a clean vanilla+workshop setup

[CmdletBinding()]
param(
    [string] $Repo = (Split-Path (Split-Path $PSScriptRoot)),
    [string] $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord',
    [string] $ModuleId = 'CoopDebug',
    # A frozen copy of a build to install instead of source\Coop\bin\Debug, so a before/after rig chain
    # keeps its 'before' while the working tree is rebuilt.
    [string] $BuildDir = '',
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'
$moduleDir = Join-Path (Join-Path $GameDir 'Modules') $ModuleId

function Remove-TestModule {
    if (-not (Test-Path $moduleDir)) { Write-Host "  $ModuleId not installed"; return }

    # Anything running out of the folder keeps a handle and turns the delete into a half-move.
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($moduleDir, 'OrdinalIgnoreCase') } |
        ForEach-Object {
            Write-Host "  stopping $($_.Name) (pid $($_.ProcessId)) running from the module"
            & taskkill.exe /PID $_.ProcessId /T /F 2>&1 | Out-Null
        }
    Start-Sleep -Milliseconds 500
    Remove-Item $moduleDir -Recurse -Force
    Write-Host "  removed $moduleDir"
}

if ($Uninstall) {
    Write-Host "Uninstalling $ModuleId ..."
    Remove-TestModule
    $stray = Get-ChildItem (Join-Path $GameDir 'Modules') -Directory | Where-Object { $_.Name -match 'Coop' }
    Write-Host ("Coop modules left in the game: " + $(if ($stray) { ($stray.Name -join ', ') } else { 'none (workshop only)' }))
    return
}

$binSrc = if ($BuildDir) { $BuildDir } else { Join-Path $Repo 'source\Coop\bin\Debug' }
Write-Host "  build source: $binSrc"
if (-not (Test-Path (Join-Path $binSrc 'Coop.dll'))) { throw "No Debug build at $binSrc. Run: dotnet build source/Coop/Coop.csproj -c Debug" }

Remove-TestModule
$binDst = Join-Path $moduleDir 'bin\Win64_Shipping_Client'
New-Item -ItemType Directory -Force -Path $binDst | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $moduleDir 'GUI\Prefabs') | Out-Null

# Ship the mod's own assemblies, never the game's - loading a second copy of TaleWorlds.* breaks type identity.
Get-ChildItem "$binSrc\*.dll" |
    Where-Object { $_.Name -notmatch '^(TaleWorlds|SandBox|StoryMode)\.' } |
    Copy-Item -Destination $binDst -Force
foreach ($required in 'Coop.dll', 'Common.dll', 'Coop.Core.dll', 'GameInterface.dll') {
    if (-not (Test-Path (Join-Path $binDst $required))) { throw "Missing $required in the staged module" }
}

$uiSrc = Join-Path $Repo 'UIMovies'
if (Test-Path $uiSrc) { Copy-Item "$uiSrc\*" (Join-Path $moduleDir 'GUI\Prefabs') -Recurse -Force }

# The SubModule.xml written below declares these files by name, and the code reads them by module id. A
# module with the declarations but not the files is worse than one with neither: ModuleHelper throws for an
# id it cannot resolve, from inside OnSessionLaunched, which the engine does not guard - the campaign dies
# during load and the save is what gets blamed.
Copy-Item (Join-Path $Repo 'deploy\ModuleData') $moduleDir -Recurse -Force
$modConfig = Join-Path $Repo 'deploy\mod-config.default.json'
if (Test-Path $modConfig) { Copy-Item $modConfig $moduleDir -Force }

# Distinct Id AND Name so the launcher can never confuse this with the workshop module.
$gameVersion = (Select-String -Path (Join-Path $Repo 'source\Coop\Coop.csproj') -Pattern '<GameVersion>(.+?)</GameVersion>').Matches[0].Groups[1].Value
$coopVersion = (Select-String -Path (Join-Path $Repo 'source\Directory.Build.props') -Pattern '<CoopVersion>(.+?)</CoopVersion>').Matches[0].Groups[1].Value
(Get-Content (Join-Path $Repo 'deploy\SubModule.xml') -Raw) `
    -replace '<Name value="Coop"/>', "<Name value=`"Coop (Debug live-test)`"/>" `
    -replace '<Id value="Coop"/>', "<Id value=`"$ModuleId`"/>" `
    -replace '\$\{version\}', "v$coopVersion" `
    -replace '\$\{game_version\}', $gameVersion `
    -replace '\$\{main_class\}', 'CoopMod' `
    -replace '\$\{name\}', 'Coop' |
    Set-Content (Join-Path $moduleDir 'SubModule.xml') -Encoding UTF8

Write-Host "installed $ModuleId -> $moduleDir"
Write-Host ("  assemblies: " + (Get-ChildItem "$binDst\*.dll").Count)
Write-Host ("  live-test server present: " + [bool](Select-String -Path (Join-Path $moduleDir 'SubModule.xml') -Pattern $ModuleId -Quiet))
Write-Host "  launch with: _MODULES_*Native*SandBoxCore*Sandbox*CustomBattle*StoryMode*$ModuleId*_MODULES_ /autoconnect"
