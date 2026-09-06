<#
PvP duel rig (PVP-SYNC-PLAN.md section 4). Puts the two players in a 1v1 field battle, drives their agents
through scripted attacks and blocks in both directions, records both machines on a shared clock, captures load,
and analyses the result.

    .\Duel-Test.ps1 -Tag baseline                      # full run: rig + fixture + all scripts
    .\Duel-Test.ps1 -Tag fix1 -SkipSetup -AliPid 1 -OmarPid 2 -ServerPid 3 -Baseline runs\2026-09-05-baseline

No mouse clicks. The fixture takes Omar's clan out of the shared kingdom (the player field-battle fixture needs
distinct factions), empties both parties of troops so only the heroes fight, and starts the battle server-side.
#>
param(
    [string]$Latency = '',
    [switch]$Mounted,
    [int]$HealTo = 2000,
    [string[]]$WoundHeroes = @('Jian'),
    [string]$Sword = 'vlandia_sword_4_t4',
    [string]$Shield = 'reinforced_kite_shield',
    # Up to four weapon slots for the next spawn ('-' = empty); overrides -Sword/-Shield. Mounted kits:
    # lance+shield 'vlandia_lance_2_t4,reinforced_kite_shield', glaive 'khuzait_polearm_2_t5',
    # javelins 'eastern_javelin_2_t3,eastern_javelin_2_t3,vlandia_sword_4_t4'.
    [string[]]$Weapons = @(),
    # Stand-off for face (metres); 0 = the driver's default (1.4 on foot, 3 mounted). Javelins: 12.
    [double]$StandOff = 0,
    # Frame caps 'ali,omar' in fps applied once both are on the field, e.g. '60,40'; '' leaves the 200 fps cap.
    [string]$FrameLimit = '',
    # Install this frozen build instead of source\Coop\bin\Debug (before/after chains).
    [string]$BuildDir = '',
    # 'off' turns the mounted-sync fixes off on both clients for a "before" run of the same build ('' = leave on).
    [string]$MountedFixes = '',
    [switch]$IgnoreReach,
    [string]$Tag = 'duel',
    [string]$Save = 'pvp_duel',
    [switch]$SkipSetup,
    [int]$AliPid = 0,
    [int]$OmarPid = 0,
    [int]$ServerPid = 0,
    [string[]]$Scripts = @('swings', 'thrusts', 'blocks', 'kick_bash', 'mixed', 'footwork'),
    [int]$ForceRate = 0,
    [string]$Baseline = '',
    [int]$Seed = 1,
    [switch]$KeepWindowsUp,
    [string]$Ali = '76561198876156674',
    [string]$Omar = '76561199074278663',
    [string]$AliParty = 'MobileParty_Created_63468',
    [string]$OmarParty = 'MobileParty_Player425',
    [string]$OmarClanId = '',
    [string]$OmarClanNameHint = 'Omar'
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

# ---- 1. rig -------------------------------------------------------------------------
if ($SkipSetup -and $AliPid -and $OmarPid -and $ServerPid) {
    Say "reusing rig: server=$ServerPid ali=$AliPid omar=$OmarPid"
} else {
    Say "launching rig on save '$Save' (a few minutes)"
    $setup = & "$rig\Full-Setup.ps1" -Save $Save -Scenario None -BuildDir $BuildDir 2>&1 | Out-String
    $setup -split "`n" | ForEach-Object { if ($_ -match 'PID=|ready:|ABORT|windowed|window ') { Say ("    " + $_.Trim()) } }
    $AliPid    = [int]([regex]::Match($setup, 'ALI_PID=(\d+)')).Groups[1].Value
    $OmarPid   = [int]([regex]::Match($setup, 'OMAR_PID=(\d+)')).Groups[1].Value
    $ServerPid = [int]([regex]::Match($setup, 'SERVER_PID=(\d+)')).Groups[1].Value
    if (-not $AliPid -or -not $OmarPid -or -not $ServerPid) { Say "ABORT: rig did not come up"; exit 1 }
}
$peers = @(@{ n = 'ali'; id = $AliPid; ctrl = $Ali; party = $AliParty }, @{ n = 'omar'; id = $OmarPid; ctrl = $Omar; party = $OmarParty })
Set-Content (Join-Path $runDir 'pids.txt') "SERVER_PID=$ServerPid`nALI_PID=$AliPid`nOMAR_PID=$OmarPid" -Encoding utf8

# ---- 2. fixture: two factions, heroes only, battle ----------------------------------
Say "fixture"
if (-not $OmarClanId) {
    # players.list prints, per controller, the REGISTERED clan id (Clan_Player12), which is what
    # leave_kingdom resolves; clan.list prints StringIds and is no use for a player clan.
    $players = C $ServerPid 'coop.debug.players.list' @()
    $m = [regex]::Match($players, [regex]::Escape($Omar) + '[\s\S]*?Clan: (Clan_\S+)')
    if ($m.Success) { $OmarClanId = $m.Groups[1].Value }
    else {
        Say "  could not find Omar's clan in players.list; pass -OmarClanId. Output head:"
        Say ("  " + (($players -split "`n") | Select-Object -First 8) -join ' | ')
    }
}
if ($OmarClanId) {
    $leave = C $ServerPid 'coop.debug.clan.leave_kingdom' @($OmarClanId)
    Say ("  leave_kingdom $OmarClanId -> " + $leave)
}
# The field-battle fixture refuses a party that is inside a settlement or not yet active. Players in the save
# may be sitting in a castle, and a party is inactive until its client has applied the join snapshot.
$deadline = (Get-Date).AddMinutes(6)
while ((Get-Date) -lt $deadline) {
    $states = @()
    foreach ($p in $peers) { $states += (C $ServerPid 'coop.debug.party.battle_readiness' @($p.party)) }
    if (($states -join "`n") -match 'active=false') { Say "  waiting: a player party is still inactive on the server"; Start-Sleep -Seconds 15; continue }
    break
}
# A player leading an ARMY brings every attached lord into the field battle (the last run put 955 agents on a
# 'duel' field). Disband the players' armies first; army ids are Army_<leader party id>.
foreach ($p in $peers) {
    $armyId = 'Army_' + ($p.party -replace '^MobileParty_', '')
    # The command takes an ArmyDispersionReason; ObjectiveFinished is the neutral one (no relation penalty).
    $disband = C $ServerPid 'coop.debug.army.destroy' @($armyId, 'ObjectiveFinished')
    if ($disband -notmatch 'Unable to get') { Say ("  army.destroy $armyId -> " + $disband) }
}
foreach ($p in $peers) {
    $left = C $ServerPid 'coop.debug.map_event.leave_settlement' @($p.ctrl)
    if ($left -notmatch 'already outside') { Say ("  leave_settlement " + $p.n + " -> " + $left) }
}
# Stand both parties on a spot no other party is near. leave_settlement drops Ali on Ormanfard's gate, where a
# lord at war with Qin camped (run 5: Chusuntai, 47 troops), and Omar's own spot sat inside an army stack (run 6:
# 19 lord parties joined). Vanilla pulls every nearby co-belligerent into a fresh map event, so the duel needs
# an empty patch of land: the server searches outward from Omar for one and both players are teleported there.
$spot = C $ServerPid 'coop.debug.mobile_party.find_clear_spot' @($OmarParty, '6', '80', $AliParty)
if ($spot -match 'x=([-\d.]+)\|y=([-\d.]+)') {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $x = [double]::Parse($Matches[1], $inv); $y = [double]::Parse($Matches[2], $inv)
    Say ("  clear spot: " + $spot)
    Say ("  move omar -> " + (C $ServerPid 'coop.debug.mobile_party.restore_position' @($OmarParty, $x.ToString('0.####', $inv), $y.ToString('0.####', $inv), 'true')))
    Say ("  move ali  -> " + (C $ServerPid 'coop.debug.mobile_party.restore_position' @($AliParty, ($x + 0.4).ToString('0.####', $inv), $y.ToString('0.####', $inv), 'true')))
    Start-Sleep -Seconds 3
} else { Say "ABORT: no clear spot for the duel: $spot"; exit 3 }
$aliTroops  = C $ServerPid 'coop.debug.mobile_party.set_troops' @($AliParty,  'CharacterObject_imperial_veteran_infantryman', '0')
$omarTroops = C $ServerPid 'coop.debug.mobile_party.set_troops' @($OmarParty, 'CharacterObject_imperial_veteran_infantryman', '0')
Say ("  ali troops -> " + $aliTroops)
Say ("  omar troops -> " + $omarTroops)
# "Alifreeze's Party (Created_63468) now has ..." - the display names are how get_event lists involved parties.
$playerPartyNames = @($aliTroops, $omarTroops) | ForEach-Object { if ($_ -match "^(.+?) \(\S+\) now has") { $Matches[1] } }
foreach ($p in $peers) { C $p.id 'coop.debug.battle.troop_preference' @('all') | Out-Null }

# On foot, same sword and shield, before the battle exists: both heroes carry a warhorse (run 4) and thrust-only
# polearms (run 7); the owning client builds its own hero from local equipment and the spawn record carries the
# result. Done here rather than after the fixture starts because Hero.MainHero read as a companion for a moment
# right after the battle began (run 14) and the wrong hero was stripped.
$heroNames = @{}
# `-Weapons a,b` reaches a [string[]] as two elements from a script call and as "a,b" from -Command; accept both.
$armArgs = @(($Weapons | ForEach-Object { $_ -split '[,\s]+' }) | Where-Object { $_ })
if ($armArgs.Count -eq 0) { $armArgs = @($Sword, $Shield) }
Say ("  kit: " + ($armArgs -join ' + ') + $(if ($Mounted) { ' (mounted)' } else { '' }))
foreach ($p in $peers) {
    $expected = if ($p.n -eq 'ali') { ($playerPartyNames | Where-Object { $_ -notlike 'Omar*' } | Select-Object -First 1) } else { ($playerPartyNames | Where-Object { $_ -like 'Omar*' } | Select-Object -First 1) }
    $expected = ($expected -replace "'s Party$", '')
    $heroNames[$p.n] = $expected
    # The client's hero binding can lag its connection by tens of seconds; the commands refuse until it exists.
    $eqDeadline = (Get-Date).AddSeconds(150); $u = ''; $a = ''
    while ((Get-Date) -lt $eqDeadline) {
        $u = if ($Mounted) { 'kept the horse (' + $expected + ')' } else { C $p.id 'coop.debug.duel.unhorse' @() }
        if ($u -match 'not bound yet') { Start-Sleep -Seconds 10; continue }
        $a = C $p.id 'coop.debug.duel.arm' $armArgs
        break
    }
    Say ("  unhorse " + $p.n + " -> " + $u)
    Say ("  arm " + $p.n + " -> " + $a)
    if ($expected -and ($u -notmatch [regex]::Escape($expected) -or $a -notmatch [regex]::Escape($expected))) {
        Say ("ABORT: equipment step hit the wrong hero on " + $p.n + " (expected '" + $expected + "')"); exit 4
    }
}
# Companion heroes in a player's party spawn as AI and join the fight (run 14: Jian hit Omar for 15). A hero at
# 1 hit point is wounded and stays off the field; the server reloads the save every run, nothing is lost.
foreach ($name in $WoundHeroes) {
    $ids = C $ServerPid 'coop.debug.hero.id' @($name)
    $hid = ([regex]::Match($ids, 'Hero_[A-Za-z0-9_]+')).Value
    if ($hid) { Say ("  wound " + $name + " -> " + (C $ServerPid 'coop.debug.hero.set_hitpoints' @($hid, '1'))) }
    else { Say ("  WARN: no hero id for '" + $name + "': " + (($ids -split "`n")[0])) }
}

$battle = ''
$deadline = (Get-Date).AddMinutes(3)
while ((Get-Date) -lt $deadline) {
    $battle = C $ServerPid 'coop.debug.map_event.start_player_field_battle' @($AliParty, $OmarParty)
    if ($battle -match 'started') {
        # Vanilla pulls every nearby co-belligerent into a new battle (run 4: "Chusuntai's Party", 47 troops, on
        # Omar's side). A duel has exactly two parties, so tear the fixture down, remove the intruders - the
        # server reloads the save every run, nothing is lost - and start again.
        $eventId = ([regex]::Match($battle, 'MapEventId: (\S+)')).Groups[1].Value
        $ev = C $ServerPid 'coop.debug.map_event.get_event' @($eventId)
        $involved = @([regex]::Matches($ev, '(?m)^\s*Party: (.+?)\s*$') | ForEach-Object { $_.Groups[1].Value })
        $intruders = @($involved | Where-Object { $playerPartyNames -notcontains $_ } | Select-Object -Unique)
        if ($intruders.Count -eq 0) { break }
        # Nothing can be done from here: the fixture cannot be restored while its map event is alive, and
        # destroying the intruders does not detach them from it (run 6). The clear-spot search above is the fix.
        Say ("ABORT: other parties joined the duel: " + ($intruders -join ', ')); exit 3
    }
    if ($battle -match 'already pending') { C $ServerPid 'coop.debug.map_event.restore_player_field_battle' @() | Out-Null }
    Say ("  start_player_field_battle -> " + ($battle -split "`n")[0])
    Start-Sleep -Seconds 10
}
Say ("  battle: " + (($battle -split "`n") -join ' | '))
if ($battle -notmatch 'started') { Say "ABORT: player field battle did not start"; exit 1 }

# ---- 3. into the mission -------------------------------------------------------------
Say "entering the mission"
$attackDeadline = (Get-Date).AddMinutes(5); $started = @{}
while ((Get-Date) -lt $attackDeadline -and $started.Count -lt 2) {
    foreach ($p in $peers) {
        if ($started.ContainsKey($p.n)) { continue }
        $r = C $p.id 'coop.debug.map_event.start_attack_mission' @()
        if ($r -match 'Starting attack mission') { $started[$p.n] = $true; Say ("  " + $p.n + ": " + $r) }
        elseif ($r -notmatch 'no replicated map event') { Say ("  " + $p.n + ": " + $r) }
    }
    if ($started.Count -lt 2) { Start-Sleep -Seconds 8 }
}
if ($started.Count -lt 2) { Say "ABORT: could not start the mission on both clients"; exit 1 }

$deadline = (Get-Date).AddMinutes(6); $inMission = $false
while ((Get-Date) -lt $deadline) {
    $a = C $AliPid 'coop.debug.battle.state' @(); $o = C $OmarPid 'coop.debug.battle.state' @()
    if (($a -match 'activeAgents=(\d+)') -and ($o -match 'activeAgents=(\d+)')) {
        $na = [int]([regex]::Match($a, 'activeAgents=(\d+)')).Groups[1].Value
        $no = [int]([regex]::Match($o, 'activeAgents=(\d+)')).Groups[1].Value
        Say ("  agents ali=$na omar=$no")
        if ($na -ge $(if ($Mounted) { 4 } else { 2 }) -and $no -ge $(if ($Mounted) { 4 } else { 2 })) { $inMission = $true; break }
    }
    # The heroes only appear after deployment ends; left alone that is a two-and-a-half-minute countdown per
    # run. Finish it as soon as the teams are set up (the command refuses harmlessly until then).
    foreach ($p in $peers) {
        $fd = C $p.id 'coop.debug.map_event.finish_deployment' @()
        if ($fd -match 'Finished') { Say ("  finish_deployment " + $p.n + " -> " + $fd) }
    }
    Start-Sleep -Seconds 10
}
if (-not $inMission) { Say "ABORT: never reached the battlefield"; exit 1 }
# A duel has exactly two agents. Anyone else (a companion, a straggler) fights, kills or knocks the subject out.
Start-Sleep -Seconds 12
$a = C $AliPid 'coop.debug.battle.state' @(); $o = C $OmarPid 'coop.debug.battle.state' @()
$na = [int]([regex]::Match($a, 'activeAgents=(\d+)')).Groups[1].Value; $no = [int]([regex]::Match($o, 'activeAgents=(\d+)')).Groups[1].Value
$expectedAgents = if ($Mounted) { 4 } else { 2 }   # horses are agents too
if ($na -ne $expectedAgents -or $no -ne $expectedAgents) { Say "ABORT: not a 1v1 - agents ali=$na omar=$no (expected $expectedAgents)"; exit 5 }
foreach ($p in $peers) { Say ('  finish_deployment ' + $p.n + ' -> ' + (C $p.id 'coop.debug.map_event.finish_deployment' @())) }
# Crowd proxy: pin the adaptive bulk rate to what a big battle settles at (20 Hz) so the player lane's own rate
# (P1) is what differs between a before and an after run on this quiet two-agent field.
if ($ForceRate -gt 0) {
    foreach ($p in $peers) { Say ("  force_rate " + $p.n + " -> " + (C $p.id 'coop.debug.movement.force_rate' @("$ForceRate"))) }
}
# Latency proxy (P8): LiteNetLib's own receive-side delay on the mesh, e.g. -Latency 40,60 holds every packet
# 40-60 ms on both machines, so the pair fights across a ~100 ms round trip while still sharing one clock.
if ($Latency) {
    # PowerShell hands `-Latency 40,60` to a [string] as "40 60", so split on either separator.
    $lat = @(($Latency -split '[,\s]+') | Where-Object { $_ })
    foreach ($p in $peers) { Say ("  simulate_latency " + $p.n + " -> " + (C $p.id 'coop.debug.movement.simulate_latency' @($lat[0].Trim(), $lat[-1].Trim()))) }
}
if ($MountedFixes) {
    foreach ($p in $peers) { Say ("  mounted_fixes " + $p.n + " -> " + (C $p.id 'coop.debug.movement.mounted_fixes' @($MountedFixes))) }
}
# A horse that overshoots the field must not end the mission for everybody: nobody is retreated on either machine.
foreach ($p in $peers) { Say ("  boundary " + $p.n + " -> " + (C $p.id 'coop.debug.duel.boundary' @('off'))) }
# Frame-rate question: cap each client through the engine's own limiter (60 vs 40 fps) and fight the same scripts.
if ($FrameLimit) {
    $fl = @(($FrameLimit -split '[,\s]+') | Where-Object { $_ })
    Say ("  frame_limit ali -> " + (C $AliPid 'coop.debug.movement.frame_limit' @($fl[0].Trim())))
    Say ("  frame_limit omar -> " + (C $OmarPid 'coop.debug.movement.frame_limit' @($fl[-1].Trim())))
}
Start-Sleep -Seconds 8

$role = @{}
foreach ($p in $peers) { $st = C $p.id 'coop.debug.battle.state' @(); $role[$p.n] = if ($st -match 'host=True') { 'host' } else { 'client' } }
Say ("roles: ali=" + $role['ali'] + " omar=" + $role['omar'])
Set-Content (Join-Path $runDir 'roles.txt') ("ali=" + $role['ali'] + "`nomar=" + $role['omar']) -Encoding utf8

# ---- 4. face each other --------------------------------------------------------------
$Reach = if ($StandOff -gt 0) { $StandOff + 1.5 } elseif ($Mounted) { 3.6 } else { 1.8 }
$faceArgsAli = @($Omar); $faceArgsOmar = @($Ali)
if ($StandOff -gt 0) { $inv = [Globalization.CultureInfo]::InvariantCulture; $faceArgsAli += $StandOff.ToString('0.#', $inv); $faceArgsOmar += $StandOff.ToString('0.#', $inv) }
function FaceBoth {
    Say ("  face: ali -> " + (C $AliPid 'coop.debug.duel.face' $faceArgsAli))
    Say ("  face: omar -> " + (C $OmarPid 'coop.debug.duel.face' $faceArgsOmar))
    $deadline = (Get-Date).AddSeconds($(if ($Mounted) { 200 } else { 90 }))
    $polls = 0
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $sa = C $AliPid 'coop.debug.duel.state' @(); $so = C $OmarPid 'coop.debug.duel.state' @()
        if ($sa -match 'No active coop mission' -or $so -match 'No active coop mission') { Say "  ABORT: the mission ended during the approach (ali: $sa | omar: $so)"; return $false }
        $da = [regex]::Match($sa, 'distance=([\d.]+)').Groups[1].Value; $do = [regex]::Match($so, 'distance=([\d.]+)').Groups[1].Value
        # Every fifth poll, where the two are: a horse that rides the wrong way leaves the boundary and ends the
        # mission for everybody (run p6-ride), and the dump is gone with it.
        $polls++
        if ($polls % 5 -eq 0) { Say ("  approach: ali $da m " + ([regex]::Match($sa, 'phase=\w+').Value) + " | omar $do m " + ([regex]::Match($so, 'phase=\w+').Value)) }
        if ($da -and $do -and [double]$da -le $Reach -and [double]$do -le $Reach -and $sa -match 'phase=Idle' -and $so -match 'phase=Idle') {
            Say ("  in reach: ali sees $da m, omar sees $do m"); return $true
        }
    }
    Say "  WARN: not in reach (ali: $sa)"; return $false
}
# Record the approach too: if the pair never comes into reach, the engine probes in face_fail_*.txt say why,
# and the run stops here instead of producing twelve exchanges of two statues 300 m apart.
foreach ($p in $peers) { C $p.id 'coop.debug.duel.record' @('start') | Out-Null }
$inReach = FaceBoth
foreach ($p in $peers) {
    $dump = C $p.id 'coop.debug.duel.record' @('stop') 300000
    # Kept even when it worked: the mounted approach (steering, braking, boundary) is judged from these probes.
    $dump | Out-File -FilePath (Join-Path $runDir ($(if ($inReach) { 'face_0_' } else { 'face_fail_' }) + $p.n + ".txt")) -Encoding utf8
}
if (-not $inReach) {
    foreach ($p in $peers) { Say ("  state " + $p.n + ": " + (C $p.id 'coop.debug.duel.state' @())) }
    if (-not $IgnoreReach) { Say "ABORT: players never came into reach - see face_fail_*.txt"; exit 2 }
}

# ---- 5. load capture ------------------------------------------------------------------
$hostPid = if ($role['ali'] -eq 'host') { $AliPid } else { $OmarPid }
$clientPid = if ($role['ali'] -eq 'host') { $OmarPid } else { $AliPid }
# Paths carry spaces ("Bannerlord co op"); Start-Process joins the argument array with spaces and does not
# quote, so quote them here or Capture-Load receives half a path and dies before its first sample.
$capture = Start-Process powershell.exe -PassThru -WindowStyle Minimized -ArgumentList @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$rig\Capture-Load.ps1`"",
    '-OutDir', "`"$runDir`"", '-ServerPid', $ServerPid, '-HostPid', $hostPid, '-ClientPid', $clientPid, '-Seconds', '3600')
Say ("load capture pid " + $capture.Id)
foreach ($p in $peers) { C $p.id 'coop.debug.battle.windup' @('start') | Out-Null }

# ---- 6. scripts, both directions --------------------------------------------------------
# What the defender does while the attacker runs a script. Shield blocks stop every direction, so a defender
# that blocks throughout (run 7) takes no damage and the damage-feedback metrics stay empty; the plain attack
# scripts are met with a bare stance, and 'mixed' is the block-fidelity exchange.
$DefenderScript = @{ swings = 'hold'; thrusts = 'hold'; kick_bash = 'hold'; mixed = 'blocks'; blocks = 'blocks'; footwork = 'hold'; ride = 'ride'; ride_swing = 'ride'; ride_throw = 'ride_throw'; couch = 'couch'; throw = 'hold' }

function RunExchange($attacker, $defender, $script, $label) {
    $defScript = if ($DefenderScript.ContainsKey($script)) { $DefenderScript[$script] } else { 'hold' }
    Say ("exchange $label : " + $attacker.n + " runs '$script', " + $defender.n + " runs '$defScript'")
    # Full health on both machines for both duellists first: a bare defender dies in two exchanges otherwise.
    foreach ($p in $peers) { Say ("  heal " + $p.n + " -> " + (C $p.id 'coop.debug.duel.heal' @("$HealTo"))) }
    foreach ($p in $peers) { C $p.id 'coop.debug.duel.record' @('start') | Out-Null }
    Start-Sleep -Seconds 2
    $d = C $defender.id 'coop.debug.duel.script' @($defScript, "$Seed")
    $a = C $attacker.id 'coop.debug.duel.script' @($script, "$Seed")
    Say ("  " + $a + " / defender: " + $d)
    $expected = 0.0
    if ($a -match '([\d.]+) s') { $expected = [double]$Matches[1] }
    $deadline = (Get-Date).AddSeconds([math]::Max(20, $expected + 25))
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $sa = C $attacker.id 'coop.debug.duel.state' @()
        if ($sa -match 'phase=Idle') { break }
        # Keep the defender's stance up for the whole exchange: 'blocks' lasts 10 s, 'mixed' 60 s.
        $sd = C $defender.id 'coop.debug.duel.state' @()
        if ($sd -match 'phase=Idle') { C $defender.id 'coop.debug.duel.script' @($defScript, "$Seed") | Out-Null }
    }
    C $defender.id 'coop.debug.duel.stop' @() | Out-Null
    C $attacker.id 'coop.debug.duel.stop' @() | Out-Null
    Start-Sleep -Seconds 2
    foreach ($p in $peers) {
        $dump = C $p.id 'coop.debug.duel.record' @('stop') 300000
        $dump | Out-File -FilePath (Join-Path $runDir ("tl_" + $script + "_" + $label + "_" + $p.n + ".txt")) -Encoding utf8
    }
    Say ("  " + $attacker.n + ": " + (C $attacker.id 'coop.debug.duel.state' @()))
    Say ("  " + $defender.n + ": " + (C $defender.id 'coop.debug.duel.state' @()))
    # Kill runs (small -HealTo): the outcome mirror is the point, so record what each machine believes.
    if ($HealTo -lt 500) {
        foreach ($p in $peers) {
            (C $p.id 'coop.debug.map_event.scoreboard_state' @()) | Out-File -FilePath (Join-Path $runDir ("scoreboard_" + $label + "_" + $p.n + ".txt")) -Encoding utf8
            Say ("  battle " + $p.n + ": " + ((C $p.id 'coop.debug.battle.state' @()) -replace 'reserveSuppliers.*enemyParties', 'enemyParties'))
        }
    }
}

foreach ($script in $Scripts) {
    RunExchange $peers[0] $peers[1] $script 'AtoB'
    FaceBoth | Out-Null
    RunExchange $peers[1] $peers[0] $script 'BtoA'
    FaceBoth | Out-Null
}

# ---- 7. wrap up ---------------------------------------------------------------------------
foreach ($p in $peers) {
    (C $p.id 'coop.debug.battle.windup' @('snapshot') 300000) | Out-File -FilePath (Join-Path $runDir ("wu_" + $p.n + ".txt")) -Encoding utf8
}
Set-Content (Join-Path $runDir 'stop') 'stop'
Start-Sleep -Seconds 4

Say "analysis:"
$analyzeArgs = @("$rig\analyze_duel.py", $runDir)
if ($Baseline) { $analyzeArgs += @('--baseline', $Baseline) }
& python @analyzeArgs 2>&1 | Tee-Object -FilePath (Join-Path $runDir 'report.md') | Write-Output

# give the mouse back: a focused mission window pins the cursor to its centre.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CoopRelease2 {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@ -ErrorAction SilentlyContinue
if (-not $KeepWindowsUp) {
    foreach ($proc in (Get-Process -Name Bannerlord -ErrorAction SilentlyContinue)) {
        if ($proc.MainWindowHandle -ne 0) { [void][CoopRelease2]::ShowWindow($proc.MainWindowHandle, 6) }
    }
}
Say ("done. run folder: " + $runDir + "  pids server=$ServerPid ali=$AliPid omar=$OmarPid")
