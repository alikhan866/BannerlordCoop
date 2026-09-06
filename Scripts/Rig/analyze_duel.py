"""PvP duel analysis (PVP-SYNC-PLAN.md section 4.5).

    python analyze_duel.py <runDir> [--baseline <otherRunDir>]

Reads, per exchange, the two machines' `coop.debug.duel.record stop` dumps
(tl_<script>_<AtoB|BtoA>_<ali|omar>.txt): a per-tick timeline of both player agents plus the DUEL_EVENTS
log, all on one UTC millisecond clock (both clients run on this machine). Prints a markdown report, writes
metrics.json into the run folder, and with --baseline prints the delta against another run's metrics.json.

Directions: "AtoB" means ali attacks and omar blocks. On ali's machine ali's agent is LOCAL (the owner)
and omar's is a PUPPET; the same two agents are the other way round on omar's machine.
"""
import glob
import json
import os
import re
import statistics
import sys
from bisect import bisect_left
from collections import Counter, defaultdict

NAME = {0: 'none', 1: 'DefendFist', 2: 'DefendShield', 15: 'ReadyRanged', 16: 'ReleaseRanged',
        17: 'ReleaseThrowing', 18: 'Reload', 19: 'ReadyMelee', 20: 'ReleaseMelee', 21: 'ParriedMelee',
        22: 'BlockedMelee', 23: 'Fall', 33: 'EquipUnequip', 35: 'Idle', 36: 'Guard',
        37: 'Mount', 38: 'Dismount', 47: 'Rear', 48: 'StruckLight', 49: 'StruckMedium', 50: 'StruckHeavy',
        52: 'MountStrike'}
READY, RELEASE = 19, 20
# Ranged: ReadyRanged (aim) and ReleaseRanged / ReleaseThrowing (the shot) count as an attack too (javelins).
READY_TYPES = {19, 15}
RELEASE_TYPES = {20, 16, 17}
STRUCK = {21, 22, 48, 49, 50}
TOL_MS = 40


def nm(t):
    return NAME.get(t, 't%d' % t)


def is_defend(t):
    # Agent.ActionCodeType: the defend family sits between DefendAllBegin and DefendAllEnd, low in the enum,
    # plus Guard. Everything named Defend* in NAME is in it; unknown low codes are treated as defend too.
    return t == 36 or (1 <= t <= 14)


SAMPLE = re.compile(r'(\d+):(-?\d+)/(\d+)@(-?\d+)x(-?\d+)d(-?\d+)(?:p(-?\d+),(-?\d+))?(?:m(-?\d+)/(-?\d+)/(-?\d+))?'
                    r'(?:h(-?\d+)a(-?\d+)f(-?\d+)u(-?\d+))?(?:k(-?\d+)/(-?\d+))?(?:g(-?\d+))?')
KICKS = {28, 29, 30, 31}   # Agent.ActionCodeType Kick, KickContinue, KickHit, WeaponBash (channel 0)
# DefendDirection (timeline 'f') -> the Agent.UsageDirection it stops with a WEAPON block (a shield stops all four)
WEAPON_GUARD_COVERS = {4: '0', 5: '1', 6: '3', 7: '2'}
BLOCK = re.compile(r'([0-9a-f]{8}) (PUPPET|LOCAL) n=(\d+) t0=(\d+) \[([^\]]*)\]')
EVENT = re.compile(r'^(\d{12,}) (\w+) (.*)$', re.M)


class Track:
    __slots__ = ('role', 'rows', 'times')

    def __init__(self, role, rows):
        self.role = role
        self.rows = rows
        self.times = [r['t'] for r in rows]

    def at(self, t, tol=TOL_MS):
        """Nearest sample within tol ms, else None."""
        k = bisect_left(self.times, t)
        best = None
        for c in (k - 1, k):
            if 0 <= c < len(self.times) and abs(self.times[c] - t) <= tol:
                if best is None or abs(self.times[c] - t) < abs(self.times[best] - t):
                    best = c
        return None if best is None else self.rows[best]

    def first_after(self, t, pred, window_ms):
        k = bisect_left(self.times, t)
        while k < len(self.rows) and self.rows[k]['t'] <= t + window_ms:
            if pred(self.rows[k]):
                return self.rows[k]
            k += 1
        return None


def load_dump(path):
    """-> (agents: id8 -> Track, events: list of dict)"""
    txt = open(path, encoding='utf-8-sig', errors='replace').read()
    agents = {}
    for m in BLOCK.finditer(txt):
        t0 = int(m.group(4))
        rows = []
        for s in SAMPLE.finditer(m.group(5)):
            g = s.groups()
            rows.append({
                't': t0 + int(g[0]), 'idx': int(g[1]), 'type': int(g[2]), 'prog': int(g[3]),
                'px': int(g[6]) if g[6] else None, 'py': int(g[7]) if g[7] else None,
                'mt': int(g[8]) if g[8] else None, 'mp': int(g[9]) if g[9] else None, 'ms': int(g[10]) if g[10] else None,
                'h': int(g[11]) if g[11] else None, 'a': int(g[12]) if g[12] else None,
                'f': int(g[13]) if g[13] else None, 'u': int(g[14]) if g[14] else None,
                'k0i': int(g[15]) if g[15] else None, 'k0t': int(g[16]) if g[16] else None,
                'stage': int(g[17]) if g[17] else None,
            })
        agents[m.group(1)] = Track(m.group(2), rows)
    events = []
    for m in EVENT.finditer(txt):
        kv = dict(re.findall(r'(\w+)=(\S+)', m.group(3)))
        kv['t'] = int(m.group(1))
        kv['kind'] = m.group(2)
        kv['raw'] = m.group(3)
        events.append(kv)
    return agents, events


def pct(values, q):
    if not values:
        return None
    s = sorted(values)
    return s[min(len(s) - 1, int(q * len(s)))]


def fmt(v, unit='', digits=1):
    if v is None:
        return '-'
    if isinstance(v, float):
        return ('%.' + str(digits) + 'f%s') % (v, unit)
    return '%s%s' % (v, unit)


def owner_attacks(track):
    """Contiguous ReadyMelee runs followed by ReleaseMelee on the OWNER's machine -> list of attacks."""
    attacks = []
    rows = track.rows
    i = 0
    while i < len(rows):
        if rows[i]['type'] in READY_TYPES:
            start = i
            while i < len(rows) and rows[i]['type'] in READY_TYPES:
                i += 1
            ready_end = i
            release_start = None
            if i < len(rows) and rows[i]['type'] in RELEASE_TYPES:
                release_start = i
                while i < len(rows) and rows[i]['type'] in RELEASE_TYPES:
                    i += 1
            # A ReadyMelee that lasted under 30 ms is a sampling artefact (one or two ticks of a cancelled input), not
            # an attack anyone could have seen on either machine.
            if rows[ready_end - 1]['t'] - rows[start]['t'] < 30:
                continue
            attacks.append({
                'ready_t0': rows[start]['t'], 'ready_t1': rows[ready_end - 1]['t'],
                'release_t0': rows[release_start]['t'] if release_start is not None else None,
                'release_t1': rows[i - 1]['t'] if release_start is not None else None,
                'dir': rows[start]['a'],
                'ready_rows': rows[start:ready_end],
                'release_rows': rows[release_start:i] if release_start is not None else [],
            })
        else:
            i += 1
    return attacks


def positional_speeds(rows, window_ms=100):
    """Per-sample ground speed (m/s): the distance covered over the ~window_ms before each sample (samples are
    ~5 ms apart and positions are in decimetres, so a per-sample delta is too coarse)."""
    speeds = {}
    j = 0
    for i, b in enumerate(rows):
        while j < i and rows[j]['t'] < b['t'] - window_ms:
            j += 1
        a = rows[j]
        dt = b['t'] - a['t']
        if dt < window_ms * 0.6 or dt > window_ms * 2 or a['px'] is None or b['px'] is None:
            continue
        d = ((b['px'] - a['px']) ** 2 + (b['py'] - a['py']) ** 2) ** 0.5 / 10.0
        speeds[b['t']] = d / (dt / 1000.0)
    return speeds


def mount_metrics(owner, puppet):
    """Horse gait agreement, engine-speed error and sliding (body moving while the horse's own speed says still)."""
    if not any(r['mt'] is not None and r['mt'] >= 0 for r in owner.rows):
        return None
    gait_total = gait_hit = 0
    speed_err = []
    for r in owner.rows:
        if r['mt'] is None or r['mt'] < 0:
            continue
        p = puppet.at(r['t'])
        if p is None or p['mt'] is None or p['mt'] < 0:
            continue
        gait_total += 1
        gait_hit += (p['mt'] == r['mt'])
        if r['ms'] is not None and p['ms'] is not None and r['ms'] >= 0 and p['ms'] >= 0:
            speed_err.append(abs(r['ms'] - p['ms']) / 10.0)

    def sliding(track):
        speeds = positional_speeds(track.rows)
        moving = slid = 0
        for r in track.rows:
            v = speeds.get(r['t'])
            if v is None or r['ms'] is None or r['ms'] < 0:
                continue
            if v >= 1.5:
                moving += 1
                # legs (engine locomotion speed) say under 0.5 m/s while the body covers 1.5 m/s or more
                if r['ms'] < 5:
                    slid += 1
        return moving, slid

    om, os_ = sliding(owner)
    pm, ps = sliding(puppet)
    return {
        'gait_agree_pct': 100.0 * gait_hit / gait_total if gait_total else None,
        'gait_samples': gait_total,
        'speed_err_p50_mps': pct(speed_err, 0.50),
        'speed_err_p90_mps': pct(speed_err, 0.90),
        'owner_moving_samples': om, 'owner_slide_pct': 100.0 * os_ / om if om else None,
        'puppet_moving_samples': pm, 'puppet_slide_pct': 100.0 * ps / pm if pm else None,
        'owner_gaits': dict(Counter(nm(r['mt']) for r in owner.rows if r['mt'] is not None and r['mt'] >= 0).most_common(4)),
        'puppet_gaits': dict(Counter(nm(r['mt']) for r in puppet.rows if r['mt'] is not None and r['mt'] >= 0).most_common(4)),
    }


def face_report(run):
    """The kept approach recordings (face_*_<ali|omar>.txt): what the driver's probes say about steering and braking."""
    out = []
    for path in sorted(glob.glob(os.path.join(run, 'face_*_*.txt'))):
        try:
            _, events = load_dump(path)
        except Exception:
            continue
        probes = [e for e in events if e['kind'] == 'probe']
        if not probes:
            continue

        def num(e, key):
            try:
                return float(e.get(key, 'nan'))
            except ValueError:
                return float('nan')
        dists = [num(e, 'distance') for e in probes]
        speeds = [num(e, 'vel') for e in probes]
        angles = [abs(num(e, 'steerAngle')) for e in probes if 'steerAngle' in e]
        outside = sum(1 for e in probes if e.get('inside') == '0')
        arrived = next((e for e in events if e['kind'] == 'face' and e['raw'].startswith('arrived')), None)
        braked = [e for e in events if e['kind'] == 'brake']
        errors = [e['raw'][:120] for e in events if e['kind'] == 'script' and e['raw'].startswith('error')]
        d_ok = [d for d in dists if d == d]
        out.append({
            'file': os.path.basename(path),
            'probes': len(probes),
            'seconds': (probes[-1]['t'] - probes[0]['t']) / 1000.0,
            'distance_start_m': d_ok[0] if d_ok else None,
            'distance_min_m': min(d_ok) if d_ok else None,
            'distance_end_m': d_ok[-1] if d_ok else None,
            'speed_max_mps': max(v for v in speeds if v == v) if any(v == v for v in speeds) else None,
            'steer_angle_p90_rad': pct(angles, 0.9),
            'steer_angle_last_rad': angles[-1] if angles else None,
            'outside_boundary_probes': outside,
            'arrived': arrived['raw'][:80] if arrived else None,
            'brakes': [b['raw'][:100] for b in braked][:3],
            'errors': errors[:3],
        })
    return out


def analyze_exchange(script, label, attacker_name, defender_name, att_dump, def_dump):
    att_agents, att_events = load_dump(att_dump)
    def_agents, def_events = load_dump(def_dump)
    out = {'script': script, 'label': label, 'attacker': attacker_name, 'defender': defender_name, 'notes': []}

    att_id = next((i for i, tr in att_agents.items() if tr.role == 'LOCAL'), None)
    def_id = next((i for i, tr in def_agents.items() if tr.role == 'LOCAL'), None)
    if att_id is None or def_id is None:
        out['notes'].append('missing LOCAL agent in a dump (attacker=%s defender=%s)' % (att_id, def_id))
        return out
    att_owner = att_agents[att_id]                 # attacker as seen by its owner
    att_puppet = def_agents.get(att_id)            # attacker as drawn on the defender's machine
    def_owner = def_agents[def_id]
    def_puppet = att_agents.get(def_id)
    if att_puppet is None or def_puppet is None:
        out['notes'].append('one machine did not record the other player (puppet missing)')
        return out
    out['samples'] = {'attacker_owner': len(att_owner.rows), 'attacker_puppet': len(att_puppet.rows),
                      'defender_owner': len(def_owner.rows), 'defender_puppet': len(def_puppet.rows)}

    # ---- attacks: onset latency, wind-up / release rendered, direction match ----------------------------
    attacks = owner_attacks(att_owner)
    onsets, windup_hit, windup_total, release_hit, release_total, dir_hit, dir_total = [], 0, 0, 0, 0, 0, 0
    per_dir = defaultdict(lambda: [0, 0])
    # Which attack DIRECTION each ready action index means, learnt from the owner's own samples: a mounted wind-up
    # runs through two indices (ready-in, then the hold loop) and the puppet's engine may switch between them at a
    # different moment, which is a variant difference, not a wrong direction. Direction is judged through this map;
    # the exact-index agreement is reported separately as the variant match.
    idx_dir_votes = defaultdict(Counter)
    for r in att_owner.rows:
        if r['type'] in READY_TYPES and r['a'] is not None and r['a'] >= 0:
            idx_dir_votes[r['idx']][r['a']] += 1
    idx_dir = {idx: votes.most_common(1)[0][0] for idx, votes in idx_dir_votes.items()}
    variant_hit = variant_total = 0
    for a in attacks:
        # Look for the puppet's wind-up only while THIS attack is alive on the owner (plus a hop of slack): a
        # feint that the puppet never shows must count as missing, not borrow the next swing's wind-up and
        # report a second-long "onset".
        end_t = a['release_t1'] if a['release_t1'] is not None else a['ready_t1']
        first = att_puppet.first_after(a['ready_t0'] - 60, lambda r: r['type'] in READY_TYPES, max(200, end_t - a['ready_t0'] + 260))
        if first is not None:
            onsets.append(first['t'] - a['ready_t0'])
        for r in a['ready_rows']:
            p = att_puppet.at(r['t'])
            if p is None:
                continue
            windup_total += 1
            if p['type'] in READY_TYPES:
                windup_hit += 1
                # Direction is compared by ACTION INDEX, not Agent.AttackDirection: a puppet is driven by
                # SetActionChannel and its native attack-direction state stays -1, while the action index encodes
                # weapon + direction identically on both machines.
                dir_total += 1
                variant_total += 1
                if r['idx'] == p['idx']:
                    variant_hit += 1
                if idx_dir.get(p['idx'], -2) == r['a'] or r['idx'] == p['idx']:
                    dir_hit += 1
            per_dir[r['a']][1] += 1
            if p['type'] in READY_TYPES:
                per_dir[r['a']][0] += 1
        for r in a['release_rows']:
            p = att_puppet.at(r['t'])
            if p is None:
                continue
            release_total += 1
            if p['type'] in RELEASE_TYPES:
                release_hit += 1
    # Lag-compensated wind-up: the same comparison with the puppet track shifted by the median onset, so one
    # hop of latency (unavoidable) is not counted as a miss and what remains is really dropped or cut short.
    shift = statistics.median(onsets) if onsets else 0
    lag_hit = lag_total = 0
    for a in attacks:
        for r in a['ready_rows']:
            p = att_puppet.at(r['t'] + shift)
            if p is None:
                continue
            lag_total += 1
            if p['type'] in READY_TYPES:
                lag_hit += 1
    out['windup_pct_lag_compensated'] = 100.0 * lag_hit / lag_total if lag_total else None
    # kicks and bashes live on channel 0: owner ticks in a kick action, and whether the puppet showed one too
    kick_total = kick_hit = kick_runs = 0
    prev_kick = False
    kick_shift = statistics.median(onsets) if onsets else 25
    for r in att_owner.rows:
        in_kick = r['k0t'] in KICKS
        if in_kick and not prev_kick:
            kick_runs += 1
        prev_kick = in_kick
        if not in_kick:
            continue
        p = att_puppet.at(r['t'] + kick_shift)
        if p is None:
            continue
        kick_total += 1
        if p['k0t'] in KICKS:
            kick_hit += 1
    out['kicks'] = kick_runs
    out['kick_rendered_pct'] = 100.0 * kick_hit / kick_total if kick_total else None
    lost = []
    for a in attacks:
        rows = a['ready_rows']
        hit = 0
        for r in rows:
            p = att_puppet.at(r['t'] + (statistics.median(onsets) if onsets else 25))
            if p is not None and p['type'] in READY_TYPES:
                hit += 1
        if rows and hit < 0.5 * len(rows):
            t0 = a['ready_t0']
            near = [e for e in def_events if e['kind'] in ('apply', 'reaction', 'engine') and e.get('agent') == att_id
                    and t0 - 400 <= e['t'] <= (a['release_t1'] or a['ready_t1']) + 100]
            lost.append({'ready_t0': t0, 'ready_ms': a['ready_t1'] - t0, 'feint': a['release_t0'] is None,
                         'rendered_pct': 100.0 * hit / len(rows),
                         'events': ['%+d %s %s' % (e['t'] - t0, e['kind'], e['raw'][:150]) for e in near]})
    out['lost_windups'] = lost
    out['attacks'] = len(attacks)
    out['onset_ms_median'] = statistics.median(onsets) if onsets else None
    out['onset_ms_p99'] = pct(onsets, 0.99)
    out['onset_missing'] = len(attacks) - len(onsets)
    out['windup_pct'] = 100.0 * windup_hit / windup_total if windup_total else None
    out['release_pct'] = 100.0 * release_hit / release_total if release_total else None
    out['direction_pct'] = 100.0 * dir_hit / dir_total if dir_total else None
    out['variant_pct'] = 100.0 * variant_hit / variant_total if variant_total else None
    out['windup_by_dir'] = {str(d): (100.0 * v[0] / v[1] if v[1] else None) for d, v in per_dir.items()}

    # ---- every owner action type, rendered on the puppet (kicks, bashes, struck reactions, guards) ---------
    per_type = defaultdict(lambda: [0, 0])
    for r in att_owner.rows:
        t = r['type']
        if t in (0, 35):
            continue
        p = att_puppet.at(r['t'])
        if p is None:
            continue
        per_type[t][1] += 1
        if p['type'] == t:
            per_type[t][0] += 1
    out['rendered_by_type'] = {nm(t): (100.0 * v[0] / v[1], v[1]) for t, v in per_type.items() if v[1] >= 10}

    # ---- usage-mode match while the attacker is attacking ---------------------------------------------------
    u_total = u_hit = 0
    for r in att_owner.rows:
        if (r['type'] not in READY_TYPES and r['type'] not in RELEASE_TYPES) or r['u'] is None:
            continue
        p = att_puppet.at(r['t'])
        if p is None or p['u'] is None:
            continue
        u_total += 1
        if p['u'] == r['u']:
            u_hit += 1
    out['usage_pct'] = 100.0 * u_hit / u_total if u_total else None
    # usage over the WHOLE exchange (a couched lance is a usage, not an action): agreement at each instant, and how
    # long after each owner switch the puppet showed the new usage
    ua_total = ua_hit = 0
    switch_lat = []
    prev_u = None
    for r in att_owner.rows:
        if r['u'] is None:
            continue
        p = att_puppet.at(r['t'])
        if p is not None and p['u'] is not None:
            ua_total += 1
            ua_hit += (p['u'] == r['u'])
        if prev_u is not None and r['u'] != prev_u:
            m = att_puppet.first_after(r['t'] - 40, lambda q, want=r['u']: q['u'] == want, 1500)
            if m is not None:
                switch_lat.append(max(0, m['t'] - r['t']))
        prev_u = r['u']
    out['usage_all_pct'] = 100.0 * ua_hit / ua_total if ua_total else None
    out['usage_switches'] = sum(1 for a_, b_ in zip(att_owner.rows, att_owner.rows[1:]) if a_['u'] is not None and b_['u'] is not None and a_['u'] != b_['u'])
    out['usage_switch_ms_median'] = statistics.median(switch_lat) if switch_lat else None
    # the couched lance: the owner's raw usage flips 0/alt every frame while couched, so "owner couched" is any alt
    # usage within the last 40 ms; the puppet should show the alt usage throughout
    alts = [r['u'] for r in att_owner.rows if r['u'] is not None and r['u'] > 0]
    alt = max(alts) if alts else None
    alt_total = alt_hit = 0
    if alt is not None:
        recent = None
        for r in att_owner.rows:
            if r['u'] == alt:
                recent = r['t']
            if recent is None or r['t'] - recent > 40:
                continue
            p = att_puppet.at(r['t'])
            if p is None or p['u'] is None:
                continue
            alt_total += 1
            alt_hit += (p['u'] == alt)
    out['alt_usage'] = alt
    out['alt_usage_owner_samples'] = alt_total
    out['alt_usage_rendered_pct'] = 100.0 * alt_hit / alt_total if alt_total else None

    # ---- blows (attacker machine) and applied (defender machine) ----------------------------------------------
    blows = [e for e in att_events if e['kind'] == 'blow' and e.get('attacker') == att_id and e.get('victim') == def_id]
    applied = [e for e in def_events if e['kind'] == 'applied' and e.get('victim') == def_id]
    out['blows'] = len(blows)
    out['blows_hit'] = sum(1 for b in blows if int(b.get('dmg', '0')) > 0)
    out['blows_blocked'] = sum(1 for b in blows if int(b.get('dmg', '0')) <= 0)
    out['applied'] = len(applied)
    out['collision_results'] = dict(Counter(b.get('result', '?') for b in blows))

    # guard at impact: what the defender OWNER held versus what the attacker's machine DREW, at the blow instant
    guard_total = guard_match = 0
    guard_age_mismatch = []
    through_block = []
    pair_stats = defaultdict(lambda: [0, 0])   # (attackDir, ownerGuardDir) -> [hits, blocks]
    for b in blows:
        t = b['t']
        o = def_owner.at(t, 60)
        p = def_puppet.at(t, 60)
        if o is None or p is None:
            continue
        owner_guard = o['f'] if is_defend(o['type']) else -1
        puppet_guard = p['f'] if is_defend(p['type']) else -1
        guard_total += 1
        if owner_guard == puppet_guard:
            guard_match += 1
        else:
            # how long the owner had held this guard when the blow landed
            k = bisect_left(def_owner.times, t)
            age = 0
            j = k - 1
            while j >= 0:
                rr = def_owner.rows[j]
                g = rr['f'] if is_defend(rr['type']) else -1
                if g != owner_guard:
                    break
                age = t - rr['t']
                j -= 1
            guard_age_mismatch.append(age)
        key = '%s->%s' % (b.get('attackerDir', '?'), owner_guard)
        if int(b.get('dmg', '0')) > 0:
            pair_stats[key][0] += 1
            # A landed MELEE blow while the victim's own machine showed a guard: kicks (attackType 1) and bashes
            # (2) go through a shield by design and are not counted.
            # The owner's action STAGE decides whether the block was live: Defend (5) / DefendParry (6) block,
            # the lowering tail after the key is released reads as a defend TYPE but no longer stops anything.
            owner_live_block = (o['stage'] in (5, 6)) if o.get('stage') is not None else (owner_guard != -1)
            # A shield stops every direction; a weapon block stops only its own (guard 4 = up vs attack 0, 5 = down
            # vs 1, 6 vs attack 3 (right), 7 vs attack 2 (left), read off the blocked pairs of the glaive runs), so
            # a glaive-armed defender hit on the side it is not covering was outfought, not desynced.
            shield = o['type'] == 2
            covers = shield or WEAPON_GUARD_COVERS.get(owner_guard) == str(b.get('attackerDir'))
            if b.get('attackType', '0') == '0' and owner_live_block and covers:
                through_block.append({'t': t, 'dmg': int(b['dmg']), 'attackerDir': b.get('attackerDir'),
                                      'ownerGuard': owner_guard, 'puppetGuard': puppet_guard, 'ownerStage': o.get('stage')})
        else:
            pair_stats[key][1] += 1
    out['guard_match_at_impact_pct'] = 100.0 * guard_match / guard_total if guard_total else None
    out['guard_mismatch_owner_age_ms_median'] = statistics.median(guard_age_mismatch) if guard_age_mismatch else None
    out['guard_mismatch_owner_age_ms_max'] = max(guard_age_mismatch) if guard_age_mismatch else None
    out['attack_vs_guard_pairs'] = {k: {'hits': v[0], 'blocks': v[1]} for k, v in pair_stats.items()}
    # hit-through-block: a pair that blocks at least once in this run still produced hits while the owner held
    # that guard for longer than a hop (60 ms on one machine) - the cases to inspect.
    blocking_pairs = {k for k, v in pair_stats.items() if v[1] > 0}
    htb = 0
    for b in blows:
        if int(b.get('dmg', '0')) <= 0:
            continue
        t = b['t']
        o = def_owner.at(t, 60)
        if o is None or not is_defend(o['type']):
            continue
        key = '%s->%s' % (b.get('attackerDir', '?'), o['f'])
        if key not in blocking_pairs:
            continue
        k = bisect_left(def_owner.times, t)
        held = 0
        j = k - 1
        while j >= 0 and is_defend(def_owner.rows[j]['type']) and def_owner.rows[j]['f'] == o['f']:
            held = t - def_owner.rows[j]['t']
            j -= 1
        if held >= 60:
            htb += 1
    out['hit_through_block'] = len(through_block)
    out['hit_through_block_cases'] = through_block

    # damage latency: attacker's blow -> owner applies -> attacker's screen shows the health drop
    lat_apply, lat_attacker_screen, lat_owner_health = [], [], []
    used = set()
    for b in blows:
        dmg = int(b.get('dmg', '0'))
        if dmg <= 0:
            continue
        t = b['t']
        cand = [e for e in applied if id(e) not in used and e['t'] >= t - 20 and e['t'] <= t + 2500 and int(e.get('dmg', '-1')) == dmg]
        if cand:
            used.add(id(cand[0]))
            lat_apply.append(cand[0]['t'] - t)
        before_p = def_puppet.at(t, 60)
        if before_p is not None and before_p['h'] is not None:
            drop = def_puppet.first_after(t, lambda r, hb=before_p['h']: r['h'] is not None and r['h'] < hb, 3000)
            if drop is not None:
                lat_attacker_screen.append(drop['t'] - t)
        before_o = def_owner.at(t, 60)
        if before_o is not None and before_o['h'] is not None:
            drop = def_owner.first_after(t, lambda r, hb=before_o['h']: r['h'] is not None and r['h'] < hb, 3000)
            if drop is not None:
                lat_owner_health.append(drop['t'] - t)
    out['damage_latency_apply_ms_median'] = statistics.median(lat_apply) if lat_apply else None
    # the same latency split into its legs when the DEBUG timing events exist: blow -> routed (attacker's pending
    # queue), routed -> received (wire), received -> applied (victim's drain and RegisterBlow)
    routed = [e for e in att_events if e['kind'] == 'routed' and e.get('attacker') == att_id and e.get('victim') == def_id]
    received = [e for e in def_events if e['kind'] == 'damage_rx' and e.get('attacker') == att_id and e.get('victim') == def_id]
    legs = {'pending': [], 'wire': [], 'drain': []}
    for b in [x for x in blows if int(x.get('dmg', '0')) > 0]:
        r = next((x for x in routed if 0 <= x['t'] - b['t'] <= 600), None)
        if r is None:
            continue
        routed.remove(r)
        legs['pending'].append(r['t'] - b['t'])
        rx = next((x for x in received if 0 <= x['t'] - r['t'] <= 600), None)
        if rx is None:
            continue
        received.remove(rx)
        legs['wire'].append(rx['t'] - r['t'])
        ap = next((x for x in applied if 0 <= x['t'] - rx['t'] <= 600), None)
        if ap is not None:
            legs['drain'].append(ap['t'] - rx['t'])
    out['damage_leg_pending_ms'] = legs['pending']
    out['damage_leg_wire_ms'] = legs['wire']
    out['damage_leg_pending_ms_max'] = max(legs['pending']) if legs['pending'] else None
    out['damage_leg_wire_ms_max'] = max(legs['wire']) if legs['wire'] else None
    out['damage_leg_drain_ms'] = legs['drain']
    out['damage_latency_first_ms'] = lat_apply[0] if lat_apply else None
    out['damage_latency_apply_ms_p99'] = pct(lat_apply, 0.99)
    out['damage_latency_owner_health_ms_median'] = statistics.median(lat_owner_health) if lat_owner_health else None
    out['damage_latency_attacker_screen_ms_median'] = statistics.median(lat_attacker_screen) if lat_attacker_screen else None
    out['damage_latency_attacker_screen_ms_p99'] = pct(lat_attacker_screen, 0.99)

    # reaction rendered: owner flinches (Struck/Parried/Blocked) after a landed blow; does the attacker's screen show it
    react_total = react_owner = react_puppet = 0
    for b in blows:
        if int(b.get('dmg', '0')) <= 0:
            continue
        t = b['t']
        react_total += 1
        if def_owner.first_after(t, lambda r: r['type'] in STRUCK, 500) is not None:
            react_owner += 1
        if def_puppet.first_after(t, lambda r: r['type'] in STRUCK, 700) is not None:
            react_puppet += 1
    out['reaction_owner_pct'] = 100.0 * react_owner / react_total if react_total else None
    out['reaction_rendered_pct'] = 100.0 * react_puppet / react_total if react_total else None
    # agreement: the engine decides per blow whether a flinch plays at all (low damage: none on either machine),
    # so the mirror is judged by both machines making the same call
    agree = agree_total = 0
    for b in blows:
        if int(b.get('dmg', '0')) <= 0:
            continue
        t = b['t']
        o = def_owner.first_after(t, lambda r: r['type'] in STRUCK, 500) is not None
        p = def_puppet.first_after(t, lambda r: r['type'] in STRUCK, 700) is not None
        agree_total += 1
        agree += (o == p)
    out['reaction_agreement_pct'] = 100.0 * agree / agree_total if agree_total else None

    # position error of the DEFENDER at impact, and over the whole exchange
    errs_impact, errs_all = [], []
    for b in blows:
        o = def_owner.at(b['t'], 60)
        p = def_puppet.at(b['t'], 60)
        if o and p and o['px'] is not None and p['px'] is not None:
            errs_impact.append(((o['px'] - p['px']) ** 2 + (o['py'] - p['py']) ** 2) ** 0.5 / 10.0)
    for r in def_owner.rows:
        p = def_puppet.at(r['t'])
        if p and r['px'] is not None and p['px'] is not None:
            errs_all.append(((r['px'] - p['px']) ** 2 + (r['py'] - p['py']) ** 2) ** 0.5 / 10.0)
    out['pos_err_impact_p90_m'] = pct(errs_impact, 0.90)
    out['pos_err_impact_max_m'] = max(errs_impact) if errs_impact else None
    out['pos_err_all_p50_m'] = pct(errs_all, 0.50)
    out['pos_err_all_p90_m'] = pct(errs_all, 0.90)
    out['pos_err_all_max_m'] = max(errs_all) if errs_all else None

    # position error of the ATTACKER too: in a mounted charge the attacker's puppet is what the defender sees coming
    errs_att = []
    for r in att_owner.rows:
        p = att_puppet.at(r['t'])
        if p and r['px'] is not None and p['px'] is not None:
            errs_att.append(((r['px'] - p['px']) ** 2 + (r['py'] - p['py']) ** 2) ** 0.5 / 10.0)
    out['pos_err_att_all_p50_m'] = pct(errs_att, 0.50)
    out['pos_err_att_all_p90_m'] = pct(errs_att, 0.90)
    out['pos_err_att_all_max_m'] = max(errs_att) if errs_att else None

    # ---- mounts: does the puppet horse animate the gait its owner's does, at the speed it is really moving ------
    out['mount_attacker'] = mount_metrics(att_owner, att_puppet)
    out['mount_defender'] = mount_metrics(def_owner, def_puppet)

    # ---- missiles (javelins): every owner shot, and whether/when the other machine reconstructed it ------------
    shots = [e for e in att_events if e['kind'] == 'shot' and e.get('agent') == att_id]
    missiles = {e.get('seq'): e for e in def_events if e['kind'] == 'missile' and e.get('agent') == att_id}
    mirror_ms, ff, dropped, failed = [], 0, 0, 0
    for sh in shots:
        m = missiles.get(sh.get('seq'))
        if m is None:
            continue
        how = m.get('how')
        if how == 'reconstructed':
            mirror_ms.append(m['t'] - sh['t'])
            ff += m.get('ff') == '1'
        elif how == 'dropped':
            dropped += 1
        else:
            failed += 1
    out['throws'] = len(shots)
    out['missiles_mirrored'] = len(mirror_ms)
    out['missiles_mirrored_pct'] = 100.0 * len(mirror_ms) / len(shots) if shots else None
    out['missile_mirror_ms_median'] = statistics.median(mirror_ms) if mirror_ms else None
    out['missile_mirror_ms_p99'] = pct(mirror_ms, 0.99)
    out['missiles_fast_forwarded'] = ff
    out['missiles_dropped'] = dropped + failed
    out['missile_hits'] = sum(1 for b in blows if b.get('missile') == '1' and int(b.get('dmg', '0')) > 0)
    out['missile_blows'] = sum(1 for b in blows if b.get('missile') == '1')
    # the VICTIM machine's own physics: did the reconstructed javelin reach the victim's body there (its blow is
    # suppressed by the intercept, but the RegisterBlow postfix still records it with attackerLocal=0)
    out['missile_victim_side_impacts'] = sum(1 for e in def_events if e['kind'] == 'blow' and e.get('missile') == '1'
                                             and e.get('attacker') == att_id and e.get('victim') == def_id)
    out['throw_events'] = [e['raw'][:160] for e in att_events if e['kind'] == 'throw'][:8]

    # A duel has four agents at most: anything the attacker hit that is neither duellist is the defender's horse.
    mount_blows = [e for e in att_events if e['kind'] == 'blow' and e.get('attacker') == att_id
                   and e.get('victim') not in (att_id, def_id, '-', '?')]
    mount_ids = set(b['victim'] for b in mount_blows)
    mount_applied = [e for e in def_events if e['kind'] == 'applied' and e.get('victim') in mount_ids]
    out['mount_blows'] = len(mount_blows)
    out['mount_hits'] = sum(1 for b in mount_blows if int(b.get('dmg', '0')) > 0)
    out['mount_applied'] = len(mount_applied)
    lat = []
    for b in mount_blows:
        if int(b.get('dmg', '0')) <= 0:
            continue
        nxt = next((a for a in mount_applied if 0 <= a['t'] - b['t'] <= 400), None)
        if nxt is not None:
            lat.append(nxt['t'] - b['t'])
            mount_applied.remove(nxt)
    out['mount_dmg_latency_ms_median'] = statistics.median(lat) if lat else None

    # outcome mirror at the end of the exchange: last known health of each player on each machine
    def last_h(track):
        for r in reversed(track.rows):
            if r['h'] is not None:
                return r['h']
        return None
    out['health_end'] = {'attacker_owner': last_h(att_owner), 'attacker_puppet': last_h(att_puppet),
                         'defender_owner': last_h(def_owner), 'defender_puppet': last_h(def_puppet)}
    # Outcome mirror (P7): when the defender dies, both machines must see it, and close together. Death on a
    # track = health reaching 0 or the samples stopping while the other machine keeps going.
    # A dead agent stops being sampled (Mission.MainAgent turns null, the puppet is removed), so death is also a
    # track that ends while the other tracks of the exchange carry on.
    exchange_end = max(tr.rows[-1]['t'] for tr in (att_owner, att_puppet, def_owner, def_puppet) if tr.rows)
    def death_time(track):
        for r in track.rows:
            if r['h'] is not None and r['h'] <= 0:
                return r['t']
        if track.rows and exchange_end - track.rows[-1]['t'] > 500:
            return track.rows[-1]['t']
        return None
    d_owner = death_time(def_owner)
    d_puppet = death_time(def_puppet)
    out['defender_death_owner_ms'] = d_owner
    out['defender_death_puppet_ms'] = d_puppet
    out['death_mirror_latency_ms'] = (d_puppet - d_owner) if (d_owner is not None and d_puppet is not None) else None
    if d_owner is not None or d_puppet is not None:
        out['notes'].append('defender death: owner %s, attacker screen %s, mirror latency %s ms' % (
            'yes' if d_owner is not None else 'NO', 'yes' if d_puppet is not None else 'NO',
            '-' if out['death_mirror_latency_ms'] is None else out['death_mirror_latency_ms']))
    return out


def load_csv_medians(path):
    if not os.path.exists(path):
        return {}
    rows = [l.rstrip('\n').split(',') for l in open(path, encoding='utf-8-sig') if l.strip()]
    if len(rows) < 3:
        return {}
    head, data = rows[0], rows[1:]
    out = {}
    for col in ('cpuPercent', 'workingSetMb', 'wireBytesPerSecond', 'bulkHz', 'priorityHz', 'senderMsPerSecond',
                'receiverApplyMsPerSecond', 'receiverQueueMs', 'fps', 'localAgents', 'agents'):
        if col not in head:
            continue
        i = head.index(col)
        vals = []
        for r in data:
            if i < len(r) and r[i] not in ('', 'n/a'):
                try:
                    vals.append(float(r[i]))
                except ValueError:
                    pass
        if vals:
            out[col] = {'median': statistics.median(vals), 'p5': pct(vals, 0.05), 'p99': pct(vals, 0.99), 'max': max(vals), 'n': len(vals)}
    return out


def mesh_messages_per_second(path):
    """Median over the run of (packets in a 10 s mesh profile window / 10), from the client log's [Mesh] lines."""
    if not os.path.exists(path):
        return None
    txt = open(path, encoding='utf-8-sig', errors='replace').read()
    # '[Mesh] Packet profile over 10 seconds (486 bytes/sec avg): ["MovementPacket: 40 packets, 3,480 bytes", ...]'
    rates = []
    for secs, body in re.findall(r'Packet profile over (\d+) seconds \([^)]*\): \[([^\]]*)\]', txt):
        packets = [int(x.replace(',', '')) for x in re.findall(r'(\d[\d,]*) packets', body)]
        if packets and int(secs) > 0:
            rates.append(sum(packets) / float(secs))
    return statistics.median(rates) if rates else None


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    run = sys.argv[1]
    baseline = None
    if '--baseline' in sys.argv:
        baseline = sys.argv[sys.argv.index('--baseline') + 1]

    roles = {}
    rp = os.path.join(run, 'roles.txt')
    if os.path.exists(rp):
        for line in open(rp, encoding='utf-8-sig'):
            if '=' in line:
                k, v = line.strip().split('=', 1)
                roles[k] = v

    exchanges = []
    for path in sorted(glob.glob(os.path.join(run, 'tl_*_AtoB_ali.txt')) + glob.glob(os.path.join(run, 'tl_*_BtoA_omar.txt'))):
        base = os.path.basename(path)
        m = re.match(r'tl_(.+)_(AtoB|BtoA)_(ali|omar)\.txt', base)
        if not m:
            continue
        script, label, who = m.groups()
        attacker, defender = ('ali', 'omar') if label == 'AtoB' else ('omar', 'ali')
        att_dump = os.path.join(run, 'tl_%s_%s_%s.txt' % (script, label, attacker))
        def_dump = os.path.join(run, 'tl_%s_%s_%s.txt' % (script, label, defender))
        if not (os.path.exists(att_dump) and os.path.exists(def_dump)):
            continue
        exchanges.append(analyze_exchange(script, label, attacker, defender, att_dump, def_dump))

    approaches = face_report(run)
    load = {role: load_csv_medians(os.path.join(run, 'load-%s.csv' % role)) for role in ('server', 'host', 'client')}
    mesh = {role: mesh_messages_per_second(os.path.join(run, 'mesh-profile-%s.txt' % role)) for role in ('host', 'client')}

    metrics = {'run': os.path.basename(run.rstrip('\\/')), 'roles': roles, 'exchanges': exchanges, 'load': load, 'mesh_msgs_per_s': mesh,
               'approaches': approaches}
    with open(os.path.join(run, 'metrics.json'), 'w', encoding='utf-8') as f:
        json.dump(metrics, f, indent=1)

    base = None
    if baseline and os.path.exists(os.path.join(baseline, 'metrics.json')):
        base = json.load(open(os.path.join(baseline, 'metrics.json'), encoding='utf-8'))

    print('# Duel report: %s' % metrics['run'])
    print()
    print('roles: %s' % (', '.join('%s=%s' % kv for kv in roles.items()) or 'unknown'))
    print()
    cols = [('attacks', 'Attacks', ''), ('onset_ms_median', 'Onset med ms', ''), ('onset_ms_p99', 'Onset p99 ms', ''),
            ('windup_pct', 'Wind-up %', ''), ('windup_pct_lag_compensated', 'Wind-up % (lag-comp.)', ''), ('release_pct', 'Release %', ''), ('direction_pct', 'Direction %', ''), ('variant_pct', 'Swing variant %', ''),
            ('usage_pct', 'Usage %', ''), ('kicks', 'Kicks', ''), ('kick_rendered_pct', 'Kick rendered %', ''), ('blows', 'Blows', ''), ('blows_hit', 'Hits', ''), ('blows_blocked', 'Blocked', ''),
            ('guard_match_at_impact_pct', 'Guard match @impact %', ''), ('hit_through_block', 'Hit-through-block', ''),
            ('damage_latency_apply_ms_median', 'Dmg->applied med ms', ''), ('damage_latency_attacker_screen_ms_median', 'Dmg->attacker screen med ms', ''),
            ('reaction_owner_pct', 'Reaction owner %', ''), ('reaction_rendered_pct', 'Reaction rendered %', ''), ('reaction_agreement_pct', 'Reaction agreement %', ''),
            ('pos_err_impact_p90_m', 'Pos err @impact p90 m', ''), ('pos_err_impact_max_m', 'Pos err @impact max m', ''),
            ('pos_err_all_p90_m', 'Pos err all p90 m', '')]
    print('| Script | Dir | Att->Def | ' + ' | '.join(c[1] for c in cols) + ' |')
    print('|' + '---|' * (3 + len(cols)))
    for e in exchanges:
        row = [e['script'], e['label'], '%s->%s' % (e['attacker'], e['defender'])]
        for key, _, _ in cols:
            v = e.get(key)
            cell = fmt(v)
            if base:
                bv = next((x.get(key) for x in base.get('exchanges', []) if x['script'] == e['script'] and x['label'] == e['label']), None)
                if isinstance(v, (int, float)) and isinstance(bv, (int, float)):
                    cell += ' (%+.1f)' % (v - bv)
            row.append(cell)
        print('| ' + ' | '.join(row) + ' |')
    print()
    for e in exchanges:
        if e.get('notes'):
            print('- %s %s: %s' % (e['script'], e['label'], '; '.join(e['notes'])))
        for lw in e.get('lost_windups', []):
            print('- %s %s LOST wind-up (%d ms, %s, rendered %.0f%%); apply/reaction events on the defender machine, ms from onset:' % (
                e['script'], e['label'], lw['ready_ms'], 'feint' if lw['feint'] else 'released', lw['rendered_pct']))
            for line in lw['events'][:14]:
                print('    ' + line)
        rbt = e.get('rendered_by_type')
        if rbt:
            print('- %s %s rendered by owner action: %s' % (e['script'], e['label'], ', '.join(
                '%s %.0f%% (n=%d)' % (k, v[0], v[1]) for k, v in sorted(rbt.items()))))
        pairs = e.get('attack_vs_guard_pairs')
        if pairs:
            print('- %s %s attackDir->ownerGuard hits/blocks: %s' % (e['script'], e['label'], ', '.join(
                '%s %d/%d' % (k, v['hits'], v['blocks']) for k, v in sorted(pairs.items()))))
        he = e.get('health_end')
        if he:
            print('- %s %s health at end: attacker owner/puppet %s/%s, defender owner/puppet %s/%s' % (
                e['script'], e['label'], he['attacker_owner'], he['attacker_puppet'], he['defender_owner'], he['defender_puppet']))
    if approaches:
        print()
        print('## Approaches (face recordings): distance start -> min -> end, top speed, steering, boundary, braking')
        print()
        for a in approaches:
            print('- %s: %d probes over %.0f s, distance %s -> %s -> %s m, speed max %s m/s, |steer angle| p90 %s last %s rad, outside boundary %d, %s%s%s' % (
                a['file'], a['probes'], a['seconds'], fmt(a['distance_start_m']), fmt(a['distance_min_m']), fmt(a['distance_end_m']),
                fmt(a['speed_max_mps']), fmt(a['steer_angle_p90_rad'], '', 2), fmt(a['steer_angle_last_rad'], '', 2), a['outside_boundary_probes'],
                a['arrived'] or 'NOT arrived', ('; brakes: ' + ' | '.join(a['brakes'])) if a['brakes'] else '', ('; ERRORS: ' + ' | '.join(a['errors'])) if a['errors'] else ''))
    mounted = [e for e in exchanges if e.get('mount_attacker') or e.get('mount_defender') or e.get('throws') or e.get('mount_blows')]
    if mounted:
        print()
        print('## Mounted and missile mirror (attacker owner vs its puppet on the defender machine; defender likewise)')
        print()
        mcols = [('pos_err_att_all_p50_m', 'Att pos err p50 m'), ('pos_err_att_all_p90_m', 'Att pos err p90 m'), ('pos_err_att_all_max_m', 'Att pos err max m'),
                 ('pos_err_all_p90_m', 'Def pos err p90 m'),
                 ('mount_attacker.gait_agree_pct', 'Att horse gait agree %'), ('mount_attacker.speed_err_p90_mps', 'Att horse speed err p90 m/s'),
                 ('mount_attacker.owner_slide_pct', 'Att horse slide owner %'), ('mount_attacker.puppet_slide_pct', 'Att horse slide puppet %'),
                 ('mount_defender.gait_agree_pct', 'Def horse gait agree %'), ('mount_defender.puppet_slide_pct', 'Def horse slide puppet %'),
                 ('throws', 'Throws'), ('missiles_mirrored_pct', 'Mirrored %'), ('missile_mirror_ms_median', 'Mirror med ms'), ('missile_mirror_ms_p99', 'Mirror p99 ms'),
                 ('missiles_fast_forwarded', 'Fast-fwd'), ('missiles_dropped', 'Dropped'), ('missile_blows', 'Missile blows'), ('missile_hits', 'Missile hits'),
                 ('missile_victim_side_impacts', 'Victim-side impacts'),
                 ('mount_blows', 'Blows on horse'), ('mount_hits', 'Horse hits'), ('mount_applied', 'Horse applied'), ('mount_dmg_latency_ms_median', 'Horse dmg->applied med ms'),
                 ('usage_all_pct', 'Usage agree % (all)'), ('usage_switches', 'Usage switches'), ('usage_switch_ms_median', 'Usage switch med ms'),
                 ('alt_usage_owner_samples', 'Alt-usage owner samples'), ('alt_usage_rendered_pct', 'Alt usage rendered %')]

        def dig(e, key):
            v = e
            for part in key.split('.'):
                v = v.get(part) if isinstance(v, dict) else None
                if v is None:
                    return None
            return v
        print('| Script | Dir | ' + ' | '.join(c[1] for c in mcols) + ' |')
        print('|' + '---|' * (2 + len(mcols)))
        for e in mounted:
            row = [e['script'], e['label']]
            for key, _ in mcols:
                v = dig(e, key)
                cell = fmt(v)
                if base:
                    be = next((x for x in base.get('exchanges', []) if x['script'] == e['script'] and x['label'] == e['label']), None)
                    bv = dig(be, key) if be else None
                    if isinstance(v, (int, float)) and isinstance(bv, (int, float)):
                        cell += ' (%+.1f)' % (v - bv)
                row.append(cell)
            print('| ' + ' | '.join(row) + ' |')
        for e in mounted:
            for side in ('mount_attacker', 'mount_defender'):
                m = e.get(side)
                if m:
                    print('- %s %s %s gaits owner %s / puppet %s' % (e['script'], e['label'], side.replace('mount_', ''), m['owner_gaits'], m['puppet_gaits']))
            if e.get('throw_events'):
                print('- %s %s throw events: %s' % (e['script'], e['label'], ' | '.join(e['throw_events'][:4])))
    print()
    print('## Load (median over the run; p99 in metrics.json)')
    print()
    print('| Role | CPU % | Working set MB | Wire KB/s | bulkHz | priorityHz | Sender ms/s | Receiver apply ms/s | Recv queue ms | fps | Mesh msgs/s |')
    print('|---|---|---|---|---|---|---|---|---|---|---|')
    for role in ('host', 'client', 'server'):
        l = load.get(role) or {}
        def med(col, scale=1.0, digits=1):
            v = l.get(col)
            if not v:
                return '-'
            s = '%.*f' % (digits, v['median'] * scale)
            if base and base.get('load', {}).get(role, {}).get(col):
                s += ' (%+.*f)' % (digits, (v['median'] - base['load'][role][col]['median']) * scale)
            return s
        print('| %s | %s | %s | %s | %s | %s | %s | %s | %s | %s | %s |' % (
            role, med('cpuPercent'), med('workingSetMb', 1, 0), med('wireBytesPerSecond', 1 / 1024.0), med('bulkHz', 1, 0),
            med('priorityHz', 1, 0), med('senderMsPerSecond'), med('receiverApplyMsPerSecond'), med('receiverQueueMs'),
            med('fps', 1, 0), fmt(mesh.get(role))))
    print()
    print('metrics.json written to %s' % run)


if __name__ == '__main__':
    main()
