<#
PvP army battle rig (PVP-ARMY-SYNC-PLAN.md A0). Two players, each with N troops, fight a field battle against each
other while both machines and the server are sampled every few seconds. No clicks.

    .\Army-Test.ps1 -Preset even_small                 # 100 v 100, both charge
    .\Army-Test.ps1 -Preset even_medium -Scenario hold_then_charge
    .\Army-Test.ps1 -TroopsA 800 -TroopsB 800 -Tag cap  # reserves and waves

Outputs in runs\<stamp>-<tag>\: run.log, census-<role>.csv (per sample: agents, enemy alive, fleeing, result),
size-*.txt, engine-size-*.txt, roster-*.txt (before and after, server + both clients), wu_*.txt, scoreboard-*.txt,
load-*.csv (Capture-Load.ps1), report.md (analyze_army.py).
#>
param(
    [string]$Tag = 'army',
    [string]$Save = 'pvp_duel',
    [ValidateSet('even_small', 'even_medium', 'cap', 'asymmetric', 'custom')][string]$Preset = 'even_small',
    [int]$TroopsA = -1,
    [int]$TroopsB = -1,
    [string]$TroopA = 'CharacterObject_imperial_veteran_infantryman',
    [string]$TroopB = 'CharacterObject_imperial_veteran_infantryman',
    [ValidateSet('charge', 'hold_then_charge', 'leave')][string]$Scenario = 'charge',
    [int]$HoldSeconds = 60,
    # leave: one player leaves the fight at +LeaveAtSeconds. retreat = the in-mission retreat confirmation (the
    # party leaves the battle, A11); crash = the client process is killed (party stays in the event, the server
    # must migrate the host and re-issue the reserves, A9). RejoinAfterSeconds > 0 relaunches the crashed client
    # and re-enters the running battle (A8 late join). Leaver: host | client | ali | omar.
    [string]$Leaver = 'host',
    [int]$LeaveAtSeconds = 45,
    [ValidateSet('retreat', 'crash')][string]$LeaveMode = 'retreat',
    [int]$RejoinAfterSeconds = 0,
    [int]$MaxSeconds = 720,
    [int]$SampleSeconds = 5,
    [string]$Latency = '',
    # Omar attacks Ali instead of Ali attacking Omar (the other role assignment of every scenario).
    [switch]$Swap,
    # Coop battle size the server dictates for this run (mod-config.json "battleSize", 200-1000); 0 = leave the file
    # alone. Server and both clients on this PC read the same CoopData\mod-config.json, so all three see it. The
    # previous value is restored when the run ends. The engine slider (Options > Battle Size) is a separate cap.
    [int]$BattleSize = 0,
    # Start Omar's mission this many seconds after Ali's: the server makes the FIRST mission-ready client the battle
    # host, so this puts Ali in the host seat (0 = both as soon as the event is replicated).
    [int]$DelayOmarSeconds = 0,
    [string[]]$WoundHeroes = @('Jian'),
    [switch]$EqualHeroes,
    # After the battle, drive the winner's post-battle walk without clicks: the encounter menu's option, Done on the
    # members / prisoners screen, donate every donatable item on the loot screen, Done; then snapshot the rosters
    # again as 'after_loot'. M12: donated items paid no troop XP.
    [switch]$LootWalk,
    # Before the loot walk, replace the winner's roster with fresh troops. Vanilla only gives XP to troops that can
    # still use it, and after a battle every survivor sits exactly on its upgrade threshold (measured 6 Sep 2026:
    # 88 recruits at 300 XP each, party capacity 0), so donated XP would land nowhere and prove nothing.
    [switch]$ResetTroopXpBeforeLoot,
    [string[]]$HeroNames = @('Alifreeze', 'Omar'),
    [string]$Baseline = '',
    [switch]$SkipSetup,
    [int]$AliPid = 0,
    [int]$OmarPid = 0,
    [int]$ServerPid = 0,
    [string]$Ali = '76561198876156674',
    [string]$Omar = '76561199074278663',
    [string]$AliParty = 'MobileParty_Created_63468',
    [string]$OmarParty = 'MobileParty_Player425',
    [string]$OmarClanId = ''
)

$ErrorActionPreference = 'Continue'
$rig = $PSScriptRoot
$runDir = Join-Path $rig ("runs\" + (Get-Date -Format 'yyyy-MM-dd-HHmm') + "-" + $Tag)
New-Item -ItemType Directory -Force $runDir | Out-Null

function C($targetPid, $name, $cmdArgs, $t = 120000) {
    try {
        $o = (& "$rig\Send-LiveTest.ps1" -TargetPid $targetPid -Name $name -CommandArgs $cmdArgs -TimeoutMs $t) | ConvertFrom-Json
        if ($o.ok) { return [string]$o.result.output }
        return "FAILED: " + $o.error.message
    } catch { return "THREW $_" }
}
function Say($m) { $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m; Write-Host $line; Add-Content (Join-Path $runDir 'run.log') $line }
function Field($text, $key) { $m = [regex]::Match($text, "(?<![A-Za-z])$key=([^|\s]+)"); if ($m.Success) { $m.Groups[1].Value } else { '' } }

switch ($Preset) {
    'even_small'  { if ($TroopsA -lt 0) { $TroopsA = 100 }; if ($TroopsB -lt 0) { $TroopsB = 100 } }
    'even_medium' { if ($TroopsA -lt 0) { $TroopsA = 400 }; if ($TroopsB -lt 0) { $TroopsB = 400 } }
    'cap'         { if ($TroopsA -lt 0) { $TroopsA = 800 }; if ($TroopsB -lt 0) { $TroopsB = 800 } }
    'asymmetric'  { if ($TroopsA -lt 0) { $TroopsA = 200 }; if ($TroopsB -lt 0) { $TroopsB = 700 } }
    default       { if ($TroopsA -lt 0) { $TroopsA = 100 }; if ($TroopsB -lt 0) { $TroopsB = 100 } }
}
Say "army run: preset=$Preset A=$TroopsA x $TroopA B=$TroopsB x $TroopB scenario=$Scenario latency='$Latency'"

# ---- 0. battle size for this run (read at server start, so before the rig comes up) ------------------------
$modConfigPath = Join-Path $env:USERPROFILE 'Documents\Mount and Blade II Bannerlord\CoopData\mod-config.json'
$modConfigBackup = ''
if ($BattleSize -gt 0 -and (Test-Path $modConfigPath)) {
    $modConfigBackup = Get-Content $modConfigPath -Raw
    $patched = [regex]::Replace($modConfigBackup, '"battleSize"\s*:\s*\d+', ('"battleSize": ' + $BattleSize))
    if ($patched -eq $modConfigBackup) { Say "  WARN: mod-config.json has no battleSize line; leaving it" ; $modConfigBackup = '' }
    else { Set-Content $modConfigPath $patched -Encoding utf8 -NoNewline; Say ("  mod-config battleSize -> " + $BattleSize + " (restored at the end)") }
}

# ---- 1. rig -------------------------------------------------------------------------
if ($SkipSetup -and $AliPid -and $OmarPid -and $ServerPid) {
    Say "reusing rig: server=$ServerPid ali=$AliPid omar=$OmarPid"
} else {
    Say "launching rig on save '$Save' (a few minutes)"
    $setup = & "$rig\Full-Setup.ps1" -Save $Save -Scenario None 2>&1 | Out-String
    $setup -split "`n" | ForEach-Object { if ($_ -match 'PID=|ready:|ABORT|windowed|window ') { Say ("    " + $_.Trim()) } }
    $AliPid    = [int]([regex]::Match($setup, 'ALI_PID=(\d+)')).Groups[1].Value
    $OmarPid   = [int]([regex]::Match($setup, 'OMAR_PID=(\d+)')).Groups[1].Value
    $ServerPid = [int]([regex]::Match($setup, 'SERVER_PID=(\d+)')).Groups[1].Value
    if (-not $AliPid -or -not $OmarPid -or -not $ServerPid) { Say "ABORT: rig did not come up"; exit 1 }
}
$dead = @{}
$peers = @(@{ n = 'ali'; id = $AliPid; ctrl = $Ali; party = $AliParty; troops = $TroopsA; troop = $TroopA },
           @{ n = 'omar'; id = $OmarPid; ctrl = $Omar; party = $OmarParty; troops = $TroopsB; troop = $TroopB })
Set-Content (Join-Path $runDir 'pids.txt') "SERVER_PID=$ServerPid`nALI_PID=$AliPid`nOMAR_PID=$OmarPid" -Encoding utf8

# ---- 2. fixture: two factions, N troops each, empty land, battle --------------------------------
Say "fixture"
if (-not $OmarClanId) {
    $players = C $ServerPid 'coop.debug.players.list' @()
    $m = [regex]::Match($players, [regex]::Escape($Omar) + '[\s\S]*?Clan: (Clan_\S+)')
    if ($m.Success) { $OmarClanId = $m.Groups[1].Value }
}
if ($OmarClanId) { Say ("  leave_kingdom $OmarClanId -> " + (C $ServerPid 'coop.debug.clan.leave_kingdom' @($OmarClanId))) }
$deadline = (Get-Date).AddMinutes(6)
while ((Get-Date) -lt $deadline) {
    $states = @(); foreach ($p in $peers) { $states += (C $ServerPid 'coop.debug.party.battle_readiness' @($p.party)) }
    if (($states -join "`n") -match 'active=false') { Say "  waiting: a player party is still inactive on the server"; Start-Sleep -Seconds 15; continue }
    break
}
foreach ($p in $peers) {
    $armyId = 'Army_' + ($p.party -replace '^MobileParty_', '')
    $disband = C $ServerPid 'coop.debug.army.destroy' @($armyId, 'ObjectiveFinished')
    if ($disband -notmatch 'Unable to get') { Say ("  army.destroy $armyId -> " + $disband) }
}
foreach ($p in $peers) {
    $left = C $ServerPid 'coop.debug.map_event.leave_settlement' @($p.ctrl)
    if ($left -notmatch 'already outside') { Say ("  leave_settlement " + $p.n + " -> " + $left) }
}
$spot = C $ServerPid 'coop.debug.mobile_party.find_clear_spot' @($OmarParty, '6', '80', $AliParty)
if ($spot -match 'x=([-\d.]+)\|y=([-\d.]+)') {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $x = [double]::Parse($Matches[1], $inv); $y = [double]::Parse($Matches[2], $inv)
    Say ("  clear spot: " + $spot)
    Say ("  move omar -> " + (C $ServerPid 'coop.debug.mobile_party.restore_position' @($OmarParty, $x.ToString('0.####', $inv), $y.ToString('0.####', $inv), 'true')))
    Say ("  move ali  -> " + (C $ServerPid 'coop.debug.mobile_party.restore_position' @($AliParty, ($x + 0.4).ToString('0.####', $inv), $y.ToString('0.####', $inv), 'true')))
    Start-Sleep -Seconds 3
} else { Say "ABORT: no clear spot: $spot"; exit 3 }
$partyNames = @()
foreach ($p in $peers) {
    $r = C $ServerPid 'coop.debug.mobile_party.set_troops' @($p.party, $p.troop, "$($p.troops)")
    Say ("  " + $p.n + " troops -> " + $r)
    if ($r -match "^(.+?) \(\S+\) now has") { $partyNames += $Matches[1] }
}
foreach ($p in $peers) { C $p.id 'coop.debug.battle.troop_preference' @('all') | Out-Null }
foreach ($name in $WoundHeroes) {
    $ids = C $ServerPid 'coop.debug.hero.id' @($name)
    $hid = ([regex]::Match($ids, 'Hero_[A-Za-z0-9_]+')).Value
    if ($hid) { Say ("  wound " + $name + " -> " + (C $ServerPid 'coop.debug.hero.set_hitpoints' @($hid, '1'))) }
}
# Rosters before the battle, on all three machines (the client copies must match the server's).
function Rosters($label) {
    foreach ($p in $peers) {
        $s = C $ServerPid 'coop.debug.mobile_party.roster_hash' @($p.party, 'lines')
        $s | Out-File -FilePath (Join-Path $runDir ("roster-" + $label + "-" + $p.n + "-server.txt")) -Encoding utf8
        Say ("  roster " + $label + " " + $p.n + " server: " + (($s -split "`n")[0]))
        foreach ($q in $peers) {
            if ($dead.ContainsKey($q.n)) { continue }
            $c = C $q.id 'coop.debug.mobile_party.roster_hash' @($p.party, 'lines')
            $c | Out-File -FilePath (Join-Path $runDir ("roster-" + $label + "-" + $p.n + "-" + $q.n + ".txt")) -Encoding utf8
            Say ("  roster " + $label + " " + $p.n + " on " + $q.n + ": " + (($c -split "`n")[0]))
        }
    }
}
function LootWalk() {
    # The winner is whichever client's last census row says playerVictory=True (column 4 + index of 'playerVictory').
    $winner = $null
    foreach ($p in $peers) {
        if ($dead.ContainsKey($p.n)) { continue }
        $csv = Join-Path $runDir ("census-" + $p.n + ".csv")
        if (-not (Test-Path $csv)) { continue }
        $last = (Get-Content $csv | Select-Object -Last 1) -split ','
        if ($last.Count -gt 13 -and $last[13] -eq 'True') { $winner = $p }
    }
    if (-not $winner) { Say "  loot walk: no client reports a victory; skipping"; return }
    Say ("  loot walk on " + $winner.n)
    function XpProbe($label) {
        $lines = @()
        $lines += ("server: " + ((C $ServerPid 'coop.debug.mobile_party.troop_xp' @($winner.party) 30000) -replace "`r?`n", ' '))
        $lines += ($winner.n + ": " + ((C $winner.id 'coop.debug.mobile_party.troop_xp' @($winner.party) 30000) -replace "`r?`n", ' '))
        ("== $label`n" + ($lines -join "`n")) | Add-Content (Join-Path $runDir 'loot-xp.txt') -Encoding utf8
        Say ("    xp probe " + $label + ": " + (($lines[0] -split '\|\|')[-1]).Trim())
    }
    XpProbe 'before'
    if ($ResetTroopXpBeforeLoot) {
        Say ("  fresh roster for " + $winner.n + " -> " + (C $ServerPid 'coop.debug.mobile_party.set_troops' @($winner.party, $winner.troop, '40') 60000))
        Start-Sleep -Seconds 8
        Rosters 'after'
        XpProbe 'reset'
    }
    $log = @(); $t0 = Get-Date; $lastSelect = [DateTime]::MinValue
    $deadline = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $deadline) {
        $inv = C $winner.id 'coop.debug.inventory.state' @() 15000
        if ($inv -match 'partyScreen=true') {
            $r = C $winner.id 'coop.debug.ui.party_screen_done' @(); $log += ("{0:0}s party screen done: {1}" -f ((Get-Date) - $t0).TotalSeconds, $r); Say ("    party screen: " + $r)
            Start-Sleep -Seconds 2; continue
        }
        if ($inv -match 'open=true') {
            $log += ("{0:0}s inventory: {1}" -f ((Get-Date) - $t0).TotalSeconds, $inv)
            $don = C $winner.id 'coop.debug.inventory.donate_all' @(); $log += "donate: $don"; Say ("    donate: " + $don)
            $cl = C $winner.id 'coop.debug.inventory.close' @(); $log += "close: $cl"; Say ("    close: " + $cl)
            Start-Sleep -Seconds 3; continue
        }
        $enc = C $winner.id 'coop.debug.encounter.state' @() 15000
        if ($enc -match 'active=false') { $log += ("{0:0}s encounter closed" -f ((Get-Date) - $t0).TotalSeconds); Say "    encounter closed"; break }
        if (((Get-Date) - $lastSelect).TotalSeconds -ge 10) {
            # The encounter menu's remaining option resumes the walk (PostBattleWalkAutoAdvancePatch carries the rest).
            # The encounter menu lists every option; after a victory the one that starts the walk is 'capture_the_enemy'
            # (menu_select checks the option's conditions, so an unavailable one is refused rather than run).
            $menu = C $winner.id 'coop.debug.ui.menu_options' @() 15000
            $log += ("{0:0}s menu: {1}" -f ((Get-Date) - $t0).TotalSeconds, ($menu -replace "`r?`n", ' | '))
            $sel = C $winner.id 'coop.debug.ui.menu_select' @('capture_the_enemy'); $log += "select capture_the_enemy: $sel"; Say ("    menu select capture_the_enemy: " + $sel)
            $lastSelect = Get-Date
        }
        Start-Sleep -Seconds 3
    }
    Start-Sleep -Seconds 6
    XpProbe 'after'
    Set-Content (Join-Path $runDir 'loot-walk.txt') (("winner=" + $winner.n) + "`n" + ($log -join "`n")) -Encoding utf8
    Rosters 'after_loot'
}

function WaitRosterSync($maxSeconds) {
    $t = Get-Date
    while (((Get-Date) - $t).TotalSeconds -lt $maxSeconds) {
        $pending = @()
        foreach ($p in $peers) {
            $s = C $ServerPid 'coop.debug.mobile_party.roster_hash' @($p.party)
            $want = Field (($s -split "`n")[0]) 'members'
            foreach ($q in $peers) {
                $have = Field (((C $q.id 'coop.debug.mobile_party.roster_hash' @($p.party)) -split "`n")[0]) 'members'
                if ($have -ne $want) { $pending += ($p.n + ' on ' + $q.n + ': ' + $have + '/' + $want) }
            }
        }
        if ($pending.Count -eq 0) { $w = ((Get-Date) - $t).TotalSeconds; Say ("  rosters replicated to both clients after {0:0.0} s" -f $w); Set-Content (Join-Path $runDir 'roster-sync.txt') ("rosterSyncSeconds=$w") -Encoding utf8; return }
        Start-Sleep -Milliseconds 500
    }
    Say ("  WARN: rosters still differ after $maxSeconds s: " + ($pending -join ', '))
    Set-Content (Join-Path $runDir 'roster-sync.txt') ("rosterSyncSeconds=timeout`npending=" + ($pending -join ', ')) -Encoding utf8
}
if ($EqualHeroes) {
    # Both heroes to zero skills and perks: the party leader's Medicine / Athletics perks add troop health
    # (123 vs 110 measured on 5 Sep), which decides an equal-troop battle before any sync question does.
    foreach ($name in $HeroNames) {
        $ids = C $ServerPid 'coop.debug.hero.id' @($name)
        $hid = Field $ids 'id'
        if (-not $hid) { $hid = ([regex]::Match($ids, 'Hero_[A-Za-z0-9_]+')).Value }
        # reset_skills matches the hero's game StringId or display name; hero.id returns the registry id (Hero_ + StringId).
        if ($hid) { $sid = $hid -replace '^Hero_', ''; Say ("  reset_skills " + $name + " -> " + (C $ServerPid 'coop.debug.hero_developer.reset_skills' @($sid))) }
        else { Say ("  WARN: hero id for " + $name + " not found: " + $ids) }
    }
}
WaitRosterSync 60
Rosters 'before'

$battle = ''
$deadline = (Get-Date).AddMinutes(3)
while ((Get-Date) -lt $deadline) {
    $battle = if ($Swap) { C $ServerPid 'coop.debug.map_event.start_player_field_battle' @($OmarParty, $AliParty) } else { C $ServerPid 'coop.debug.map_event.start_player_field_battle' @($AliParty, $OmarParty) }
    if ($battle -match 'started') {
        $eventId = ([regex]::Match($battle, 'MapEventId: (\S+)')).Groups[1].Value
        $ev = C $ServerPid 'coop.debug.map_event.get_event' @($eventId)
        $involved = @([regex]::Matches($ev, '(?m)^\s*Party: (.+?)\s*$') | ForEach-Object { $_.Groups[1].Value })
        $intruders = @($involved | Where-Object { $partyNames -notcontains $_ } | Select-Object -Unique)
        if ($intruders.Count -eq 0) { break }
        Say ("ABORT: other parties joined the battle: " + ($intruders -join ', ')); exit 3
    }
    if ($battle -match 'already pending') { C $ServerPid 'coop.debug.map_event.restore_player_field_battle' @() | Out-Null }
    Say ("  start_player_field_battle -> " + ($battle -split "`n")[0])
    Start-Sleep -Seconds 10
}
Say ("  battle: " + (($battle -split "`n") -join ' | '))
if ($battle -notmatch 'started') { Say "ABORT: player field battle did not start"; exit 1 }
$eventId = ([regex]::Match($battle, 'MapEventId: (\S+)')).Groups[1].Value

# ---- 3. into the mission, deployment finished as early as possible --------------------------------
Say "entering the mission"
$t0 = Get-Date
$attackDeadline = (Get-Date).AddMinutes(5); $started = @{}; $enterAt = @{}
while ((Get-Date) -lt $attackDeadline -and $started.Count -lt 2) {
    foreach ($p in $peers) {
        if ($started.ContainsKey($p.n)) { continue }
        if ($p.n -eq 'omar' -and $DelayOmarSeconds -gt 0 -and $started.ContainsKey('ali') -and ((Get-Date) - $enterAt['ali']).TotalSeconds -lt $DelayOmarSeconds) { continue }
        if ($p.n -eq 'omar' -and $DelayOmarSeconds -gt 0 -and -not $started.ContainsKey('ali')) { continue }
        $r = C $p.id 'coop.debug.map_event.start_attack_mission' @()
        if ($r -match 'Starting attack mission') { $started[$p.n] = $true; $enterAt[$p.n] = Get-Date; Say ("  " + $p.n + ": " + $r) }
        elseif ($r -notmatch 'no replicated map event') { Say ("  " + $p.n + ": " + $r) }
    }
    if ($started.Count -lt 2) { Start-Sleep -Seconds 5 }
}
if ($started.Count -lt 2) { Say "ABORT: could not start the mission on both clients"; exit 1 }
$deployAt = @{}
$deadline = (Get-Date).AddMinutes(6)
while ((Get-Date) -lt $deadline -and $deployAt.Count -lt 2) {
    foreach ($p in $peers) {
        if ($deployAt.ContainsKey($p.n)) { continue }
        $fd = C $p.id 'coop.debug.map_event.finish_deployment' @()
        if ($fd -match 'Finished') { $deployAt[$p.n] = Get-Date; Say ("  finish_deployment " + $p.n + " -> " + $fd) }
    }
    if ($deployAt.Count -lt 2) { Start-Sleep -Seconds 5 }
}
if ($deployAt.Count -eq 2) {
    $skewEnter = [math]::Abs(($enterAt['ali'] - $enterAt['omar']).TotalSeconds)
    $skewDeploy = [math]::Abs(($deployAt['ali'] - $deployAt['omar']).TotalSeconds)
    Say ("  start skew: enter {0:0.0} s, deployment {1:0.0} s" -f $skewEnter, $skewDeploy)
    Set-Content (Join-Path $runDir 'skew.txt') ("enterSkewSeconds=$skewEnter`ndeploySkewSeconds=$skewDeploy") -Encoding utf8
} else { Say "  WARN: deployment did not finish on both clients (native auto-finish will)" }
Start-Sleep -Seconds 8
$role = @{}
foreach ($p in $peers) {
    $st = C $p.id 'coop.debug.battle.state' @()
    $role[$p.n] = if ($st -match 'host=True') { 'host' } else { 'client' }
    (C $p.id 'coop.debug.battle.engine_size' @()) | Out-File -FilePath (Join-Path $runDir ("engine-size-" + $p.n + ".txt")) -Encoding utf8
    (C $p.id 'coop.debug.battle.size_state' @())   | Out-File -FilePath (Join-Path $runDir ("size-start-" + $p.n + ".txt")) -Encoding utf8
}
Say ("roles: ali=" + $role['ali'] + " omar=" + $role['omar'])
Set-Content (Join-Path $runDir 'roles.txt') ("ali=" + $role['ali'] + "`nomar=" + $role['omar']) -Encoding utf8
$attackerName = if ($Swap) { 'omar' } else { 'ali' }
Set-Content (Join-Path $runDir 'scenario.txt') ("scenario=$Scenario`nholdSeconds=$HoldSeconds`nholder=ali`nattacker=$attackerName`npreset=$Preset`ntroopsA=$TroopsA`ntroopsB=$TroopsB`nlatency=$Latency`nbattleSize=$BattleSize`nleaver=$Leaver`nleaveAtSeconds=$LeaveAtSeconds`nleaveMode=$LeaveMode`nrejoinAfterSeconds=$RejoinAfterSeconds`nequalHeroes=$EqualHeroes") -Encoding utf8
if ($Latency) {
    $lat = @(($Latency -split '[,\s]+') | Where-Object { $_ })
    foreach ($p in $peers) { Say ("  simulate_latency " + $p.n + " -> " + (C $p.id 'coop.debug.movement.simulate_latency' @($lat[0], $lat[-1]))) }
}

# ---- 4. recording ---------------------------------------------------------------------------------------
$hostPid = if ($role['ali'] -eq 'host') { $AliPid } else { $OmarPid }
$clientPid = if ($role['ali'] -eq 'host') { $OmarPid } else { $AliPid }
$capture = Start-Process powershell.exe -PassThru -WindowStyle Minimized -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$rig\Capture-Load.ps1`"",
    '-OutDir', "`"$runDir`"", '-ServerPid', $ServerPid, '-HostPid', $hostPid, '-ClientPid', $clientPid, '-Seconds', "$($MaxSeconds + 120)")
Say ("load capture pid " + $capture.Id)
foreach ($p in $peers) { C $p.id 'coop.debug.battle.windup' @('start') | Out-Null; C $p.id 'coop.debug.battle.blow_trace' @('start') | Out-Null }
$censusKeys = @('activeAgents', 'humans', 'ownActive', 'ownFleeing', 'enemyActive', 'enemyFleeing', 'damageReceivedEvents', 'resultState', 'battleResolved', 'playerVictory', 'host')
$sizeKeys = @('battleSize', 'defenderTotal', 'attackerTotal', 'defenderTarget', 'attackerTarget', 'targetTotal')
foreach ($p in $peers) {
    ("utcMs,elapsedS,who,role," + ($censusKeys -join ',') + "," + ($sizeKeys -join ',')) | Set-Content (Join-Path $runDir ("census-" + $p.n + ".csv")) -Encoding utf8
}

# ---- 5. scenario --------------------------------------------------------------------------------------
$fightStart = Get-Date
switch ($Scenario) {
    'charge' {
        foreach ($p in $peers) { Say ("  charge " + $p.n + ": " + (C $p.id 'coop.debug.battle.charge_owned_formations' @())) }
    }
    'hold_then_charge' {
        Say ("  charge omar: " + (C $OmarPid 'coop.debug.battle.charge_owned_formations' @()))
        Say ("  ali holds for $HoldSeconds s")
    }
    'leave' {
        foreach ($p in $peers) { Say ("  charge " + $p.n + ": " + (C $p.id 'coop.debug.battle.charge_owned_formations' @())) }
        Say ("  $Leaver leaves at +$LeaveAtSeconds s by $LeaveMode" + $(if ($RejoinAfterSeconds -gt 0) { ", rejoins $RejoinAfterSeconds s later" } else { '' }))
    }
}
$heldCharged = ($Scenario -ne 'hold_then_charge')
$left = $false; $rejoined = $false; $rejoinFailed = $false; $leaverName = ''; $dead = @{}
$resolved = @{}
$deadline = $fightStart.AddSeconds($MaxSeconds)
while ((Get-Date) -lt $deadline) {
    $now = Get-Date
    $utcMs = [long]([DateTime]::UtcNow.Ticks / 10000)
    $elapsed = [int]($now - $fightStart).TotalSeconds
    if (-not $heldCharged -and $elapsed -ge $HoldSeconds) {
        Say ("  charge ali: " + (C $AliPid 'coop.debug.battle.charge_owned_formations' @()))
        $heldCharged = $true
    }
    if ($Scenario -eq 'leave' -and -not $left -and $elapsed -ge $LeaveAtSeconds) {
        $leaverName = switch ($Leaver) { 'ali' { 'ali' } 'omar' { 'omar' } 'host' { if ($role['ali'] -eq 'host') { 'ali' } else { 'omar' } } default { if ($role['ali'] -eq 'host') { 'omar' } else { 'ali' } } }
        $lp = $peers | Where-Object { $_.n -eq $leaverName } | Select-Object -First 1
        $before = C $lp.id 'coop.debug.battle.state' @() 15000
        if ($LeaveMode -eq 'retreat') {
            Say ("  " + $leaverName + " retreat: " + (C $lp.id 'coop.debug.map_event.retreat_confirmation' @('show')))
            Start-Sleep -Seconds 1
            Say ("  " + $leaverName + " accept: " + (C $lp.id 'coop.debug.map_event.retreat_confirmation' @('accept')))
        } else {
            Stop-Process -Id $lp.id -Force -ErrorAction SilentlyContinue
            $dead[$leaverName] = $true
            Say ("  " + $leaverName + " process " + $lp.id + " killed (simulated crash)")
        }
        Set-Content (Join-Path $runDir 'leave.txt') ("leaver=$leaverName`nrole=" + $role[$leaverName] + "`nmode=$LeaveMode`nutcMs=$utcMs`nelapsed=$elapsed`nstateBefore=" + ($before -replace "`r?`n", ' ')) -Encoding utf8
        $left = $true
    }
    if ($left -and -not $rejoined -and $RejoinAfterSeconds -gt 0 -and $elapsed -ge ($LeaveAtSeconds + $RejoinAfterSeconds)) {
        $lp = $peers | Where-Object { $_.n -eq $leaverName } | Select-Object -First 1
        $x = if ($leaverName -eq 'ali') { 0 } else { 1280 }
        $tRejoin = Get-Date
        if ($LeaveMode -eq 'crash') {
            Say ("  relaunching " + $leaverName)
            $rl = & "$rig\Relaunch-Client.ps1" -Id $lp.ctrl -ServerPid $ServerPid -X $x 2>&1 | Out-String
            $rl -split "`n" | ForEach-Object { if ($_.Trim()) { Say ("    " + $_.Trim()) } }
            $newPid = [int]([regex]::Match($rl, 'CLIENT_PID=(\d+)')).Groups[1].Value
            if ($newPid) { $lp.id = $newPid; if ($leaverName -eq 'ali') { $AliPid = $newPid } else { $OmarPid = $newPid }; $dead.Remove($leaverName) }
        }
        $connectedS = [int]((Get-Date) - $tRejoin).TotalSeconds
        # The fixture answered the troop-preference prompt for the original process; the relaunched one must answer too,
        # or the server holds its mission start for the 30 s barrier timeout.
        if ($LeaveMode -eq 'crash') { C $lp.id 'coop.debug.battle.troop_preference' @('all') | Out-Null }
        # Re-enter the running battle; the first attempts can be refused while the join snapshot is still applying.
        $enter = ''; $tries = 0
        while ($tries -lt 24) {
            $enter = C $lp.id 'coop.debug.map_event.enter_current_battle' @() 30000
            # A refused command comes back as "Command '...' failed: ..."; keep asking until the join snapshot has applied.
            if ($enter -notmatch 'FAILED|failed|No active encounter|Unable') { break }
            $tries++; Start-Sleep -Seconds 5
        }
        Say ("  " + $leaverName + " enter_current_battle after " + $tries + " retries: " + $enter)
        $enteredS = [int]((Get-Date) - $tRejoin).TotalSeconds
        Set-Content (Join-Path $runDir 'rejoin.txt') ("leaver=$leaverName`nutcMs=$utcMs`nelapsed=$elapsed`nnewPid=" + $lp.id + "`nconnectedAfterSeconds=$connectedS`nenteredAfterSeconds=$enteredS`nenterResult=" + ($enter -replace "`r?`n", ' ')) -Encoding utf8
        $rejoined = $true
        # A rejoiner that never got back into the mission cannot resolve; do not wait for it.
        if ($enter -match 'FAILED|failed|No active encounter|Unable') { $rejoinFailed = $true; Say ('  ' + $leaverName + ' did not re-enter the mission; not waiting for its result') }
    }
    $line = ''
    foreach ($p in $peers) {
        if ($dead.ContainsKey($p.n)) { continue }
        $st = C $p.id 'coop.debug.battle.state' @() 15000
        $sz = C $p.id 'coop.debug.battle.size_state' @() 15000
        $row = @($utcMs, $elapsed, $p.n, $role[$p.n]) + @($censusKeys | ForEach-Object { Field $st $_ }) + @($sizeKeys | ForEach-Object { Field $sz $_ })
        ($row -join ',') | Add-Content (Join-Path $runDir ("census-" + $p.n + ".csv")) -Encoding utf8
        # Per-formation centroids (orders leak, A12) and the per-side identity census (spawn mirror, A1), same clock.
        ("$utcMs $elapsed " + (C $p.id 'coop.debug.battle.formations' @() 15000)) | Add-Content (Join-Path $runDir ("formations-" + $p.n + ".txt")) -Encoding utf8
        if ($elapsed % 30 -lt $SampleSeconds) {
            ("$utcMs $elapsed " + (C $p.id 'coop.debug.battle.agent_census' @() 30000)) | Add-Content (Join-Path $runDir ("agentcensus-" + $p.n + ".txt")) -Encoding utf8
        }
        $line += ("{0}: own={1} enemy={2} fleeing={3}/{4} result={5} | " -f $p.n, (Field $st 'ownActive'), (Field $st 'enemyActive'), (Field $st 'ownFleeing'), (Field $st 'enemyFleeing'), (Field $st 'resultState'))
        if ((Field $st 'battleResolved') -eq 'True' -and -not $resolved.ContainsKey($p.n)) { $resolved[$p.n] = $elapsed; Say ("  " + $p.n + " sees the battle resolved at +$elapsed s: " + (Field $st 'resultState')) }
    }
    if ($elapsed % 30 -lt $SampleSeconds) { Say ("  +$elapsed s " + $line) }
    # Finish when every client that still has a mission has resolved: a retreated player has none, a crashed one
    # has no process, and a rejoiner counts once it is back in the mission.
    $present = @($peers | Where-Object { -not $dead.ContainsKey($_.n) -and -not ($left -and $_.n -eq $leaverName -and (($LeaveMode -eq 'retreat' -and -not $rejoined) -or $rejoinFailed)) }).Count
    if ($resolved.Count -ge [math]::Max(1, $present) -and ($resolved.Count -eq 2 -or $left)) { break }
    Start-Sleep -Seconds $SampleSeconds
}
if ($resolved.Count -lt 2) { Say "  battle did not resolve on both clients within $MaxSeconds s" }
Set-Content (Join-Path $runDir 'resolved.txt') (($resolved.GetEnumerator() | ForEach-Object { $_.Key + "=" + $_.Value }) -join "`n") -Encoding utf8

# ---- 6. wrap up ---------------------------------------------------------------------------------------
foreach ($p in $peers) {
    if ($dead.ContainsKey($p.n)) { continue }
    if ($left -and $p.n -eq $leaverName) { (C $p.id 'coop.debug.encounter.state' @() 30000) | Out-File -FilePath (Join-Path $runDir 'leaver-encounter.txt') -Encoding utf8 }
    (C $p.id 'coop.debug.battle.blow_trace' @('stop', (Join-Path $runDir ("blowtrace-" + $p.n + ".txt"))) 300000) | Out-File -FilePath (Join-Path $runDir ("blowtrace-" + $p.n + ".result")) -Encoding utf8
    (C $p.id 'coop.debug.battle.windup' @('snapshot') 300000) | Out-File -FilePath (Join-Path $runDir ("wu_" + $p.n + ".txt")) -Encoding utf8
    (C $p.id 'coop.debug.map_event.scoreboard_state' @()) | Out-File -FilePath (Join-Path $runDir ("scoreboard-" + $p.n + ".txt")) -Encoding utf8
    (C $p.id 'coop.debug.battle.size_state' @()) | Out-File -FilePath (Join-Path $runDir ("size-end-" + $p.n + ".txt")) -Encoding utf8
}
(C $ServerPid 'coop.debug.map_event.get_event' @($eventId)) | Out-File -FilePath (Join-Path $runDir 'event-end-server.txt') -Encoding utf8
Start-Sleep -Seconds 25
Rosters 'after'
if ($LootWalk) { LootWalk }
(C $ServerPid 'coop.debug.map_event.encounter_state' @()) | Out-File -FilePath (Join-Path $runDir 'encounter-end-server.txt') -Encoding utf8
# The server's own account of the battle's end: casualties, XP commits, loot offers, prisoners, party removals.
$serverLog = "$env:USERPROFILE\CoopDebugServer\engine\bin\Win64_Shipping_Server\Coop_server.log"
if (Test-Path $serverLog) {
    $stamp = $t0.ToString('HH:mm')
    Select-String -Path $serverLog -Pattern 'CommitXp|WarStats|\[Loot\]|Casualty|PrisonerTaken|lost its leader|Destroyed instance of MobileParty|BattleState=|\[Roster\]|concluded' |
        Where-Object { $_.Line -notmatch 'PartyDiag' } | ForEach-Object { $_.Line } | Select-Object -Last 120 |
        Out-File -FilePath (Join-Path $runDir 'server-battle-lines.txt') -Encoding utf8
}
# Each client's own log, so drop reasons and the host election can be read per run (the per-PID file is
# overwritten by the next launch; the first client of a launch may write the un-suffixed file).
$gameBin = 'C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client'
foreach ($p in $peers) {
    $src = Join-Path $gameBin ("Coop_client_" + $p.id + ".log")
    if (-not (Test-Path $src)) { $src = Join-Path $gameBin 'Coop_client.log' }
    if (Test-Path $src) { Copy-Item $src (Join-Path $runDir ("client-" + $p.n + ".log")) -ErrorAction SilentlyContinue }
}
if (Test-Path $serverLog) { Copy-Item $serverLog (Join-Path $runDir 'server.log') -ErrorAction SilentlyContinue }
Set-Content (Join-Path $runDir 'stop') 'stop'
Start-Sleep -Seconds 4

Say "analysis:"
$analyzeArgs = @("$rig\analyze_army.py", $runDir)
if ($Baseline) { $analyzeArgs += @('--baseline', $Baseline) }
$report = & python @analyzeArgs 2>&1 | ForEach-Object { "$_" }
$report | Out-File -FilePath (Join-Path $runDir 'report.md') -Encoding utf8
$report | ForEach-Object { Write-Host $_ }

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CoopArmyWin {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@ -ErrorAction SilentlyContinue
foreach ($proc in (Get-Process -Name Bannerlord -ErrorAction SilentlyContinue)) {
    if ($proc.MainWindowHandle -ne 0) { [void][CoopArmyWin]::ShowWindow($proc.MainWindowHandle, 6) }
}
if ($modConfigBackup) { Set-Content $modConfigPath $modConfigBackup -Encoding utf8 -NoNewline; Say "  mod-config battleSize restored" }
Say ("done. run folder: $runDir  pids server=$ServerPid ali=$AliPid omar=$OmarPid")
