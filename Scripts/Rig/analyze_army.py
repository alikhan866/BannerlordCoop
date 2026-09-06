"""
analyze_army.py <runDir> [--baseline <runDir>]

Reads an Army-Test.ps1 run folder and prints the A0 numbers (PVP-ARMY-SYNC-PLAN.md section 3.4 / 8.1):
start skew, battle-size agreement, allocation, the per-sample mirror of agent counts between the two machines,
kill mirror (each side's alive count as seen from both machines), resolution skew and result agreement, roster
hashes before and after on all three machines, routed-damage counters, and the load medians from Capture-Load.
Writes metrics.json next to the report.
"""
import json
import os
import re
import statistics
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze_duel as duel  # noqa: E402  (load_csv_medians, mesh_messages_per_second)


def read_csv(path):
    if not os.path.exists(path):
        return []
    rows = []
    with open(path, encoding='utf-8-sig') as f:
        header = None
        for line in f:
            line = line.rstrip('\n')
            if not line.strip():
                continue
            parts = line.split(',')
            if header is None:
                header = parts
                continue
            rows.append(dict(zip(header, parts)))
    return rows


def num(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def pct(values, q):
    if not values:
        return None
    s = sorted(values)
    return s[min(len(s) - 1, int(q * len(s)))]


def fmt(v, digits=1):
    if v is None:
        return '-'
    if isinstance(v, float):
        return ('%.' + str(digits) + 'f') % v
    return str(v)


def read_kv(path):
    out = {}
    if not os.path.exists(path):
        return out
    for line in open(path, encoding='utf-8-sig'):
        m = re.match(r'\s*([A-Za-z]+)=(.*)$', line.strip())
        if m:
            out[m.group(1)] = m.group(2)
    return out


def roster_hashes(run, label):
    """{party: {machine: (count_hash, members, wounded, prisoners, full_hash)}}

    The count hash covers troop, healthy and wounded numbers (members and prisoners) and ignores XP: troop XP is
    replicated only to the party's controlling client by design (TroopRosterDeltaHandler routes SetXp to the
    controller peer and strips XP from observer batches), so an observer's copy legitimately reads 0 XP.
    """
    out = {}
    for name in os.listdir(run):
        m = re.match(r'roster-%s-(ali|omar)-(server|ali|omar)\.txt$' % label, name)
        if not m:
            continue
        txt = open(os.path.join(run, name), encoding='utf-8-sig', errors='replace').read()
        h = re.search(r'hash=([0-9a-f]+)', txt)
        members = re.search(r'members=(\d+)', txt)
        wounded = re.search(r'wounded=(\d+)', txt)
        prisoners = re.search(r'prisoners=(\d+)', txt)
        counts = sorted(re.sub(r'/-?\d+\s*$', '', ln.strip()) for ln in txt.splitlines() if re.match(r'\s*[mp]:', ln))
        import hashlib
        count_hash = hashlib.sha1('|'.join(counts).encode('utf-8')).hexdigest()[:16]
        out.setdefault(m.group(1), {})[m.group(2)] = (
            count_hash,
            int(members.group(1)) if members else None,
            int(wounded.group(1)) if wounded else None,
            int(prisoners.group(1)) if prisoners else None,
            h.group(1) if h else None)
    return out


def formations(run, who):
    """[(elapsed, {side/fN: (n, x, y, v)})] from formations-<who>.txt"""
    path = os.path.join(run, 'formations-%s.txt' % who)
    if not os.path.exists(path):
        return []
    out = []
    for line in open(path, encoding='utf-8-sig', errors='replace'):
        m = re.match(r'\s*(\d+) (\d+) FORMATIONS (.*)$', line.strip())
        if not m:
            continue
        groups = {}
        for g in re.finditer(r'(\w+/f-?\d+) n=(\d+) x=(-?[\d.]+) y=(-?[\d.]+) v=(-?[\d.]+)', m.group(3)):
            groups[g.group(1)] = (int(g.group(2)), float(g.group(3)), float(g.group(4)), float(g.group(5)))
        out.append((int(m.group(2)), groups))
    return out


def side_centroid(groups, side):
    """Unit-weighted centroid of one side's formations: (n, x, y) or None."""
    n = x = y = 0.0
    for key, (gn, gx, gy, gv) in groups.items():
        if key.startswith(side + '/'):
            n += gn
            x += gx * gn
            y += gy * gn
    return (n, x / n, y / n) if n else None


def orders_leak(run, who, hold_seconds):
    """For each side: how far its centroid moved during the hold and after it, as seen on this machine."""
    rows = formations(run, who)
    if not rows:
        return {}
    out = {}
    for side in ('Attacker', 'Defender'):
        pts = [(e, side_centroid(g, side)) for e, g in rows]
        pts = [(e, c) for e, c in pts if c is not None]
        if len(pts) < 2:
            continue
        start = pts[0][1]
        hold = [c for e, c in pts if e <= hold_seconds]
        after = [c for e, c in pts if e > hold_seconds]
        def moved(cs):
            return max((((c[1] - start[1]) ** 2 + (c[2] - start[2]) ** 2) ** 0.5 for c in cs), default=None)
        out[side] = {'movedDuringHoldM': moved(hold), 'movedAfterM': moved(after), 'samplesHold': len(hold), 'samplesAfter': len(after)}
    return out


def agent_census(run, who):
    """[(elapsed, {side: (n, registered, hash, troops)})] from agentcensus-<who>.txt"""
    path = os.path.join(run, 'agentcensus-%s.txt' % who)
    if not os.path.exists(path):
        return []
    out = []
    for line in open(path, encoding='utf-8-sig', errors='replace'):
        m = re.match(r'\s*(\d+) (\d+) AGENT_CENSUS humans=(\d+) unregistered=(\d+)(.*)$', line.strip())
        if not m:
            continue
        sides = {}
        for g in re.finditer(r'\| (\w+) n=(\d+) registered=(\d+) hash=([0-9a-f]+) troops=(\S*)', m.group(5)):
            sides[g.group(1)] = (int(g.group(2)), int(g.group(3)), g.group(4), g.group(5))
        out.append((int(m.group(2)), int(m.group(3)), int(m.group(4)), sides))
    return out


def profile_composition(path):
    """Per packet type from '[Mesh]'/server 'Packet profile over N seconds' lines: {type: (median/s, max/s, median bytes/s)}."""
    if not os.path.exists(path):
        return {}
    txt = open(path, encoding='utf-8-sig', errors='replace').read()
    per_type = {}
    windows = 0
    for secs, body in re.findall(r'Packet profile over (\d+) seconds \([^)]*\): \[([^\]]*)\]', txt):
        secs = float(secs)
        if secs <= 0:
            continue
        windows += 1
        total = 0
        for name, packets, nbytes in re.findall(r'"([^":]+(?::[^":]+)?): (\d[\d,]*) packets, (\d[\d,]*) bytes"', body):
            total += int(packets.replace(',', ''))
        per_type.setdefault('(all types)', []).append((total / secs, 0.0))
        for name, packets, nbytes in re.findall(r'"([^":]+(?::[^":]+)?): (\d[\d,]*) packets, (\d[\d,]*) bytes"', body):
            per_type.setdefault(name, []).append((int(packets.replace(',', '')) / secs, int(nbytes.replace(',', '')) / secs))
    out = {}
    for name, rows in per_type.items():
        rates = [r for r, b in rows] + [0.0] * (windows - len(rows))  # a type absent from a window sent nothing in it
        out[name] = {'medianPerS': statistics.median(rates), 'maxPerS': max(rates), 'medianBytesPerS': statistics.median([b for r, b in rows] + [0.0] * (windows - len(rows))), 'windows': len(rows)}
    return out


def blow_trace(run, who):
    """Events from blowtrace-<who>.txt as [(utcMs, kind, {field: value})]; kinds are blow / routed / applied."""
    path = os.path.join(run, 'blowtrace-%s.txt' % who)
    if not os.path.exists(path):
        return []
    out = []
    for line in open(path, encoding='utf-8-sig', errors='replace'):
        m = re.match(r'\s*(\d{12,}) (blow|routed|applied) (.*)$', line.strip())
        if not m:
            continue
        out.append((int(m.group(1)), m.group(2), dict(re.findall(r'(\w+)=(\S+)', m.group(3)))))
    return out


def routing_funnel(run):
    """Per direction (attacker's machine -> victim's owner): melee hits scored on puppets, how many carried damage,
    how many were routed, how many the owner applied, and the routed latency from pairing routed -> applied on
    (victim, attacker, damage). The two clients share one clock on the rig machine."""
    import bisect
    ev = {who: blow_trace(run, who) for who in ('ali', 'omar')}
    res = {}
    for src, dst in (('ali', 'omar'), ('omar', 'ali')):
        # every hit this machine scored on a puppet, melee and missile alike; zero-damage ones are never routed
        blows = [e for e in ev[src] if e[1] == 'blow' and e[2].get('attackerLocal') == '1' and e[2].get('victimLocal') == '0']
        with_damage = [e for e in blows if int(e[2].get('dmg', '0')) > 0]
        routed = [e for e in ev[src] if e[1] == 'routed']
        applied = [e for e in ev[dst] if e[1] == 'applied']
        pool = {}
        for e in applied:
            pool.setdefault((e[2].get('victim'), e[2].get('attacker'), e[2].get('dmg')), []).append(e[0])
        for times in pool.values():
            times.sort()
        latencies, unmatched = [], 0
        for e in routed:
            times = pool.get((e[2].get('victim'), e[2].get('attacker'), e[2].get('dmg')))
            if not times:
                unmatched += 1
                continue
            idx = bisect.bisect_left(times, e[0] - 5)  # a few ms of slack for the two processes' clock reads
            if idx >= len(times):
                unmatched += 1
                continue
            latencies.append(times.pop(idx) - e[0])
        res[src + '->' + dst] = {
            'puppetHits': len(blows), 'zeroDamage': len(blows) - len(with_damage), 'routed': len(routed),
            'applied': len(applied), 'paired': len(latencies), 'unmatchedRouted': unmatched,
            'damageScored': sum(int(e[2]['dmg']) for e in with_damage),
            'damageApplied': sum(int(e[2].get('dmg', '0')) for e in applied),
            'latencyMs': {'p50': pct(latencies, 0.5), 'p90': pct(latencies, 0.9), 'p99': pct(latencies, 0.99),
                          'max': max(latencies) if latencies else None, 'n': len(latencies)},
            'traceLines': {src: len(ev[src]), dst: len(ev[dst])},
        }
    return res


def side_profile(run, who):
    """From the first agent-census sample on <who>: per side {'hpLimit', 'hp', 'weapons'}; from the first formations
    sample: per formation key its captain. The two machines' copies must agree (a puppet is spawned with its owner's
    values); two sides of one troop type that differ in hpLimit differ in party-leader or captain perks."""
    out = {}
    path = os.path.join(run, 'agentcensus-%s.txt' % who)
    if os.path.exists(path):
        for line in open(path, encoding='utf-8-sig', errors='replace'):
            if 'AGENT_CENSUS' not in line:
                continue
            for m in re.finditer(r'\| (\w+) n=(\d+) registered=\d+ hash=[0-9a-f]+ troops=(\S*)(?: troopHpLimitMean=([\d.]+) troopHpMean=([\d.]+))?(?: weapons=(\S*))?', line):
                out[m.group(1)] = {'n': int(m.group(2)), 'troops': m.group(3), 'hpLimit': num(m.group(4)), 'hp': num(m.group(5)), 'weapons': m.group(6) or ''}
            break
    path = os.path.join(run, 'formations-%s.txt' % who)
    if os.path.exists(path):
        for line in open(path, encoding='utf-8-sig', errors='replace'):
            if 'FORMATIONS' not in line:
                continue
            caps = dict(re.findall(r'(\w+/f-?\d+) n=\d+ [^;]*cap=(\S+);', line))
            for key, cap in caps.items():
                out.setdefault(key.split('/')[0], {}).setdefault('captains', {})[key] = cap
            break
    return out


def leave_report(run, rows_by_who, census_cmp):
    """The leave scenario: who left, how, and what the remaining client saw (agent count continuity, host flag flip,
    resolution); and, after a relaunch, how long the rejoin took and whether the census agreed afterwards."""
    leave = read_kv(os.path.join(run, 'leave.txt'))
    if not leave:
        return {}
    leaver = leave.get('leaver')
    other = 'omar' if leaver == 'ali' else 'ali'
    t_leave = num(leave.get('elapsed')) or 0
    rows = rows_by_who.get(other) or []

    def at(pred):
        for r in rows:
            e = num(r.get('elapsedS'))
            if e is not None and pred(e):
                return r
        return None
    before = None
    for r in rows:
        e = num(r.get('elapsedS'))
        if e is not None and e < t_leave:
            before = r
    after = at(lambda e: e >= t_leave + 5)
    later = at(lambda e: e >= t_leave + 30)
    host_flip = None
    if before is not None and (before.get('host') or '').lower() != 'true':
        for r in rows:
            e = num(r.get('elapsedS'))
            if e is not None and e >= t_leave and (r.get('host') or '').lower() == 'true':
                host_flip = e
                break
    out = {
        'leaver': leaver, 'role': leave.get('role'), 'mode': leave.get('mode'), 'atS': t_leave, 'remaining': other,
        'remainingAgents': {k: (num(v.get('activeAgents')) if v else None) for k, v in (('before', before), ('after5s', after), ('after30s', later))},
        'remainingHumans': {k: (num(v.get('humans')) if v else None) for k, v in (('before', before), ('after5s', after), ('after30s', later))},
        'remainingHostBefore': (before or {}).get('host'), 'remainingHostAfter30s': (later or {}).get('host'), 'hostFlipAtS': host_flip,
    }
    rejoin = read_kv(os.path.join(run, 'rejoin.txt'))
    if rejoin:
        t_in = (num(rejoin.get('elapsed')) or 0) + (num(rejoin.get('enteredAfterSeconds')) or 0)
        after_rejoin = [c for c in census_cmp if c['elapsed'] >= t_in]
        out['rejoin'] = {
            'connectedAfterSeconds': num(rejoin.get('connectedAfterSeconds')), 'enteredAfterSeconds': num(rejoin.get('enteredAfterSeconds')),
            'enterResult': rejoin.get('enterResult'), 'inMissionAtS': t_in,
            'censusAgreeAfter': sum(1 for c in after_rejoin if c['hashAgree']), 'censusSamplesAfter': len(after_rejoin),
        }
    enc = os.path.join(run, 'leaver-encounter.txt')
    if os.path.exists(enc):
        out['leaverEncounter'] = ' '.join(open(enc, encoding='utf-8-sig', errors='replace').read().split())[:300]
    return out


def roster_member_xp(run, label, party, who):
    """Sum of member-line XP and the per-troop lines (character -> (count, wounded, xp)) from roster-<label>-<party>-<who>.txt."""
    path = os.path.join(run, 'roster-%s-%s-%s.txt' % (label, party, who))
    if not os.path.exists(path):
        return None, {}
    lines = {}
    for line in open(path, encoding='utf-8-sig', errors='replace'):
        m = re.match(r'\s*m:([^:]+):(\d+)/(\d+)/(\d+)', line)
        if m:
            lines[m.group(1)] = (int(m.group(2)), int(m.group(3)), int(m.group(4)))
    return sum(v[2] for v in lines.values()), lines


def loot_xp_probe(run):
    """{label: {who: {'total': int, 'canAbsorb': int, 'troops': {id: (n, xp, canGain)}}}} from loot-xp.txt."""
    path = os.path.join(run, 'loot-xp.txt')
    if not os.path.exists(path):
        return {}
    out, label = {}, None
    for line in open(path, encoding='utf-8-sig', errors='replace'):
        line = line.strip()
        m = re.match(r'== (\w+)$', line)
        if m:
            label = m.group(1)
            out[label] = {}
            continue
        m = re.match(r'(\w+): .*?TROOP_XP party=(\S+) men=(\d+)(.*)$', line)
        if not m or label is None:
            continue
        troops = {}
        for t in re.finditer(r'\| (\S+) n=(\d+) xp=(\d+) canGain=(\d) upgradeCost=(\d+)', m.group(4)):
            troops[t.group(1)] = (int(t.group(2)), int(t.group(3)), t.group(4) == '1')
        total = re.search(r'totalXp=(\d+)', m.group(4))
        absorb = re.search(r'partyCanAbsorb=(-?\d+)', m.group(4))
        out[label][m.group(1)] = {'men': int(m.group(3)), 'troops': troops,
                                  'total': int(total.group(1)) if total else None,
                                  'canAbsorb': int(absorb.group(1)) if absorb else None}
    return out


def loot_walk(run):
    """What the loot walk did and what it did to the winner's troop XP, per machine."""
    kv = read_kv(os.path.join(run, 'loot-walk.txt'))
    if not kv:
        return {}
    probe = loot_xp_probe(run)
    winner = kv.get('winner')
    text = open(os.path.join(run, 'loot-walk.txt'), encoding='utf-8-sig', errors='replace').read()
    donate = re.search(r'DONATED lines=(\d+) skipped=(\d+) xpBefore=([\d.]+) xpAfter=([\d.]+) canGainXp=(\w+)', text)
    out = {'winner': winner, 'closed': 'encounter closed' in text,
           'donated': {'lines': int(donate.group(1)), 'skipped': int(donate.group(2)), 'xp': float(donate.group(4)), 'canGainXp': donate.group(5)} if donate else None,
           'xp': {}, 'probe': probe}
    for who in ('ali', 'omar', 'server'):
        before, before_lines = roster_member_xp(run, 'after', winner, who)
        after, after_lines = roster_member_xp(run, 'after_loot', winner, who)
        if before is None or after is None:
            continue
        per_troop = {c: after_lines[c][2] - before_lines.get(c, (0, 0, 0))[2] for c in after_lines}
        out['xp'][who] = {'before': before, 'after': after, 'delta': after - before,
                          'perTroop': {c: d for c, d in per_troop.items() if d != 0}}
    return out


def damage_counters(run, who):
    path = os.path.join(run, 'wu_%s.txt' % who)
    if not os.path.exists(path):
        return {}
    txt = open(path, encoding='utf-8-sig', errors='replace').read()
    out = {}
    for key in ('registerBlow', 'routedReapply', 'appliedLocal', 'SUPPRESSED'):
        m = re.search(r'%s=(\d+)' % key, txt)
        if m:
            out[key] = int(m.group(1))
    m = re.search(r'blows local=(\d+) over (\d+) attackers remote=(\d+) over (\d+) attackers', txt)
    if m:
        out['blowsLocal'], out['localAttackers'], out['blowsRemote'], out['remoteAttackers'] = map(int, m.groups())
    m = re.search(r'hitTiming: blocked=(\d+)', txt)
    if m:
        out['blocked'] = int(m.group(1))
    m = re.search(r'REMOTE=(\d+) progressAtBlow~(\d+)%', txt)
    if m:
        out['remoteBlows'], out['remoteProgressAtBlow'] = int(m.group(1)), int(m.group(2))
    m = re.search(r'puppetFoot=n:(\d+) dmg:([\d.]+) base:([\d.]+)', txt)
    if m:
        out['puppetHits'], out['puppetDmgMean'], out['puppetBaseMean'] = int(m.group(1)), float(m.group(2)), float(m.group(3))
    m = re.search(r'localFoot=n:(\d+) dmg:([\d.]+)', txt)
    if m:
        out['ownSideHits'], out['ownSideDmgMean'] = int(m.group(1)), float(m.group(2))
    return out


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    run = sys.argv[1]
    baseline = None
    if '--baseline' in sys.argv:
        bpath = os.path.join(sys.argv[sys.argv.index('--baseline') + 1], 'metrics.json')
        if os.path.exists(bpath):
            baseline = json.load(open(bpath))

    roles = read_kv(os.path.join(run, 'roles.txt'))
    skew = read_kv(os.path.join(run, 'skew.txt'))
    resolved = read_kv(os.path.join(run, 'resolved.txt'))
    ali = read_csv(os.path.join(run, 'census-ali.csv'))
    omar = read_csv(os.path.join(run, 'census-omar.csv'))
    by_elapsed = {}
    for r in ali:
        by_elapsed.setdefault(r['elapsedS'], {})['ali'] = r
    for r in omar:
        by_elapsed.setdefault(r['elapsedS'], {})['omar'] = r

    total_diff, ali_side_diff, omar_side_diff = [], [], []
    side_track = []   # (elapsed, ali side per ali, ali side per omar, omar side per omar, omar side per ali)
    peak = {'ali': 0, 'omar': 0}
    samples = 0
    for elapsed, pair in sorted(by_elapsed.items(), key=lambda kv: int(kv[0])):
        a, o = pair.get('ali'), pair.get('omar')
        if a is None or o is None:
            continue
        at, ae = num(a['activeAgents']), num(a['enemyActive'])
        ot, oe = num(o['activeAgents']), num(o['enemyActive'])
        if None in (at, ae, ot, oe):
            continue
        # Humans per side when the state command reports them (ownActive); otherwise total minus enemies, which
        # carries the riders' horses as a constant offset.
        a_own = num(a.get('ownActive')) if a.get('ownActive') else at - ae
        o_own = num(o.get('ownActive')) if o.get('ownActive') else ot - oe
        samples += 1
        peak['ali'] = max(peak['ali'], at)
        peak['omar'] = max(peak['omar'], ot)
        total_diff.append(abs(at - ot))
        # Ali's own side as Ali counts it against Ali's side as Omar counts it (his enemies), and the reverse.
        ali_side_diff.append(abs(a_own - oe))
        omar_side_diff.append(abs(o_own - ae))
        side_track.append((int(elapsed), a_own, oe, o_own, ae))

    def summary(values):
        return {'median': statistics.median(values) if values else None, 'p90': pct(values, 0.9), 'max': max(values) if values else None, 'n': len(values)}

    size = {}
    for who in ('ali', 'omar'):
        rows = [r for r in (ali if who == 'ali' else omar) if r.get('battleSize')]
        if rows:
            first = rows[0]
            size[who] = {k: first.get(k) for k in ('battleSize', 'defenderTotal', 'attackerTotal', 'defenderTarget', 'attackerTarget', 'targetTotal')}
        engine = os.path.join(run, 'engine-size-%s.txt' % who)
        if os.path.exists(engine):
            size.setdefault(who, {})['engine'] = open(engine, encoding='utf-8-sig', errors='replace').read().strip().splitlines()[0][:200]

    final = {}
    for who, rows in (('ali', ali), ('omar', omar)):
        if rows:
            last = rows[-1]
            final[who] = {k: last.get(k) for k in ('activeAgents', 'enemyActive', 'enemyFleeing', 'resultState', 'battleResolved', 'playerVictory', 'damageReceivedEvents')}

    rosters = {'before': roster_hashes(run, 'before'), 'after': roster_hashes(run, 'after')}
    roster_agree = {}
    for label, parties in rosters.items():
        for party, machines in parties.items():
            hashes = {m: v[0] for m, v in machines.items()}
            full = {m: v[4] for m, v in machines.items()}
            roster_agree['%s/%s' % (label, party)] = (len(set(hashes.values())) == 1, hashes, {m: v[1:4] for m, v in machines.items()},
                                                     len(set(full.values())) == 1)

    load = {role: duel.load_csv_medians(os.path.join(run, 'load-%s.csv' % role)) for role in ('server', 'host', 'client')}
    mesh = {role: duel.mesh_messages_per_second(os.path.join(run, 'mesh-profile-%s.txt' % role)) for role in ('host', 'client')}
    composition = {role: profile_composition(os.path.join(run, 'mesh-profile-%s.txt' % role)) for role in ('host', 'client')}
    composition['server'] = profile_composition(os.path.join(run, 'server-profile.txt'))
    world_queue = None
    sp = os.path.join(run, 'server-profile.txt')
    if os.path.exists(sp):
        qs = [int(x) for x in re.findall(r'worldQueue=(\d+)', open(sp, encoding='utf-8-sig', errors='replace').read())]
        if qs:
            world_queue = {'median': statistics.median(qs), 'max': max(qs), 'n': len(qs)}
    damage = {who: damage_counters(run, who) for who in ('ali', 'omar')}

    # kills dealt: each side's fall from its first to its last human count, as seen by its owner and by the other
    kills = {}
    if side_track:
        first, last = side_track[0], side_track[-1]
        kills = {
            'aliSideLost_perAli': first[1] - last[1], 'aliSideLost_perOmar': first[2] - last[2],
            'omarSideLost_perOmar': first[3] - last[3], 'omarSideLost_perAli': first[4] - last[4],
            'aliSideStart': first[1], 'omarSideStart': first[3],
        }
    warstats = []
    sbl = os.path.join(run, 'server-battle-lines.txt')
    if os.path.exists(sbl):
        for line in open(sbl, encoding='utf-8-sig', errors='replace'):
            if 'WarStats' in line and 'casualties' in line:
                m = re.search(r'"(Attacker|Defender)" of \S+ recorded (\d+) casualties', line)
                if m:
                    warstats.append((m.group(1), int(m.group(2))))
    scenario = read_kv(os.path.join(run, 'scenario.txt'))
    hold_seconds = int(num(scenario.get('holdSeconds')) or 0) if scenario.get('scenario') == 'hold_then_charge' else 0
    orders = {who: orders_leak(run, who, hold_seconds) for who in ('ali', 'omar')} if hold_seconds else {}
    # agent census: at every shared sample, do the two machines hold the same identities per side?
    census_a = {e: sides for e, h, u, sides in agent_census(run, 'ali')}
    census_o = {e: sides for e, h, u, sides in agent_census(run, 'omar')}
    census_cmp = []
    for e in sorted(set(census_a) & set(census_o)):
        for side in ('Attacker', 'Defender'):
            sa, so = census_a[e].get(side), census_o[e].get(side)
            if sa and so:
                census_cmp.append({'elapsed': e, 'side': side, 'nAli': sa[0], 'nOmar': so[0], 'hashAgree': sa[2] == so[2],
                                   'troopsAgree': sa[3] == so[3], 'registeredAli': sa[1], 'registeredOmar': so[1]})
    unregistered = {who: max((u for e, h, u, sides in agent_census(run, who)), default=None) for who in ('ali', 'omar')}
    routing = routing_funnel(run)
    loot = loot_walk(run)
    leave = leave_report(run, {'ali': ali, 'omar': omar}, census_cmp)
    profile = {who: side_profile(run, who) for who in ('ali', 'omar')}
    roster_sync = read_kv(os.path.join(run, 'roster-sync.txt')).get('rosterSyncSeconds')
    res_ali, res_omar = num(resolved.get('ali')), num(resolved.get('omar'))
    metrics = {
        'routing': routing,
        'lootWalk': loot,
        'leave': leave,
        'sideProfile': profile,
        'rosterSyncSeconds': roster_sync,
        'scenario': scenario,
        'orders': orders,
        'agentCensus': census_cmp,
        'maxUnregistered': unregistered,
        'run': os.path.basename(run.rstrip('\\/')),
        'roles': roles,
        'startSkewEnterS': num(skew.get('enterSkewSeconds')),
        'startSkewDeployS': num(skew.get('deploySkewSeconds')),
        'samples': samples,
        'peakAgents': peak,
        'totalAgentsDiff': summary(total_diff),
        'aliSideMirrorDiff': summary(ali_side_diff),
        'omarSideMirrorDiff': summary(omar_side_diff),
        'size': size,
        'final': final,
        'resolvedAtS': {'ali': res_ali, 'omar': res_omar},
        'resolutionSkewS': (abs(res_ali - res_omar) if res_ali is not None and res_omar is not None else None),
        'resultAgree': (final.get('ali', {}).get('resultState') == final.get('omar', {}).get('resultState')
                        and final.get('ali', {}).get('playerVictory') != final.get('omar', {}).get('playerVictory')) if final else None,
        'rosterAgree': {k: v[0] for k, v in roster_agree.items()},
        'rosterAgreeWithXp': {k: v[3] for k, v in roster_agree.items()},
        'rosters': {k: {'hashes': v[1], 'counts': v[2]} for k, v in roster_agree.items()},
        'damage': damage,
        'kills': kills,
        'warStats': warstats,
        'load': load,
        'meshMsgsPerS': mesh,
        'composition': composition,
        'worldQueue': world_queue,
    }
    json.dump(metrics, open(os.path.join(run, 'metrics.json'), 'w'), indent=1, default=str)

    def med(role, key, digits=1):
        return fmt(load.get(role, {}).get(key, {}).get('median'), digits)

    print('# Army report: %s' % metrics['run'])
    print()
    print('roles: ali=%s, omar=%s' % (roles.get('ali', '?'), roles.get('omar', '?')))
    print()
    print('| Metric | Value |')
    print('|---|---|')
    print('| Start skew (enter / deployment) | %s s / %s s |' % (fmt(metrics['startSkewEnterS']), fmt(metrics['startSkewDeployS'])))
    for who in ('ali', 'omar'):
        s = size.get(who, {})
        print('| Battle size on %s (%s) | battleSize=%s def %s/%s atk %s/%s (total/target); %s |' % (
            who, roles.get(who, '?'), s.get('battleSize', '-'), s.get('defenderTotal', '-'), s.get('defenderTarget', '-'),
            s.get('attackerTotal', '-'), s.get('attackerTarget', '-'), s.get('engine', '-')))
    print('| Peak agents (ali / omar) | %s / %s |' % (fmt(peak['ali'], 0), fmt(peak['omar'], 0)))
    print('| Total-agent mirror abs diff median / p90 / max (n) | %s / %s / %s (%d) |' % (
        fmt(metrics['totalAgentsDiff']['median']), fmt(metrics['totalAgentsDiff']['p90']), fmt(metrics['totalAgentsDiff']['max']), samples))
    print("| Ali's side alive: Ali's count vs Omar's count, abs diff median / p90 / max | %s / %s / %s |" % (
        fmt(metrics['aliSideMirrorDiff']['median']), fmt(metrics['aliSideMirrorDiff']['p90']), fmt(metrics['aliSideMirrorDiff']['max'])))
    print("| Omar's side alive: Omar's count vs Ali's count, abs diff median / p90 / max | %s / %s / %s |" % (
        fmt(metrics['omarSideMirrorDiff']['median']), fmt(metrics['omarSideMirrorDiff']['p90']), fmt(metrics['omarSideMirrorDiff']['max'])))
    print('| Resolved at (ali / omar), skew | %s s / %s s, %s s |' % (fmt(res_ali, 0), fmt(res_omar, 0), fmt(metrics['resolutionSkewS'])))
    for who in ('ali', 'omar'):
        f = final.get(who, {})
        print('| Final on %s | agents=%s enemyActive=%s fleeing=%s result=%s resolved=%s victory=%s damageEvents=%s |' % (
            who, f.get('activeAgents', '-'), f.get('enemyActive', '-'), f.get('enemyFleeing', '-'), f.get('resultState', '-'),
            f.get('battleResolved', '-'), f.get('playerVictory', '-'), f.get('damageReceivedEvents', '-')))
    if census_cmp:
        agree = sum(1 for c in census_cmp if c['hashAgree'])
        counts_agree = sum(1 for c in census_cmp if c['nAli'] == c['nOmar'])
        troops_agree = sum(1 for c in census_cmp if c['troopsAgree'])
        print('| Agent census (per side, per 30 s sample): identity hash agree / count agree / composition agree | %d / %d / %d of %d; max unregistered ali=%s omar=%s |' % (
            agree, counts_agree, troops_agree, len(census_cmp), unregistered.get('ali'), unregistered.get('omar')))
        for c in [c for c in census_cmp if not c['hashAgree']][:6]:
            print('|   census mismatch | +%ds %s: ali n=%d reg=%d, omar n=%d reg=%d, composition %s |' % (
                c['elapsed'], c['side'], c['nAli'], c['registeredAli'], c['nOmar'], c['registeredOmar'], 'same' if c['troopsAgree'] else 'DIFFERS'))
    if orders:
        for who in ('ali', 'omar'):
            for side, o in sorted(orders.get(who, {}).items()):
                print('| Orders on %s: %s centroid moved during the %d s hold / after | %s m / %s m (%d / %d samples) |' % (
                    who, side, hold_seconds, fmt(o['movedDuringHoldM']), fmt(o['movedAfterM']), o['samplesHold'], o['samplesAfter']))
    if leave:
        ra, rh = leave['remainingAgents'], leave['remainingHumans']
        print('| Leave | %s (%s) left at +%s s by %s; on %s: agents %s -> %s (+5 s) -> %s (+30 s), humans %s -> %s -> %s; host flag %s -> %s%s |' % (
            leave['leaver'], leave['role'], fmt(leave['atS'], 0), leave['mode'], leave['remaining'],
            fmt(ra['before'], 0), fmt(ra['after5s'], 0), fmt(ra['after30s'], 0), fmt(rh['before'], 0), fmt(rh['after5s'], 0), fmt(rh['after30s'], 0),
            leave['remainingHostBefore'], leave['remainingHostAfter30s'],
            (' (became host at +%s s)' % fmt(leave['hostFlipAtS'], 0)) if leave.get('hostFlipAtS') is not None else ''))
        if leave.get('leaverEncounter'):
            print('| Leaver after the battle | %s |' % leave['leaverEncounter'])
        rj = leave.get('rejoin')
        if rj:
            print('| Rejoin | client back after %s s, in the mission after %s s (+%s s battle time); census agrees %s / %s samples afterwards; %s |' % (
                fmt(rj['connectedAfterSeconds'], 0), fmt(rj['enteredAfterSeconds'], 0), fmt(rj['inMissionAtS'], 0),
                rj['censusAgreeAfter'], rj['censusSamplesAfter'], (rj.get('enterResult') or '')[:160]))
    if loot and loot.get('probe'):
        for who in ('server', loot.get('winner')):
            # 'reset' exists when the run gave the winner a roster with room; that is the baseline the donation
            # is measured against, not the roster it had before the reset.
            baseline = loot['probe'].get('reset') or loot['probe'].get('before') or {}
            before = baseline.get(who)
            after = (loot['probe'].get('after') or {}).get(who)
            if not before or not after:
                continue
            gains = {t: after['troops'][t][1] - before['troops'].get(t, (0, 0, False))[1] for t in after['troops']}
            print('| Winner troop XP on %s | %d -> %d (%+d); capacity left %s -> %s; %s |' % (
                who, before['total'], after['total'], after['total'] - before['total'],
                before['canAbsorb'], after['canAbsorb'],
                '; '.join('%s x%d xp %d%s %+d' % (t.replace('CharacterObject_', ''), after['troops'][t][0], after['troops'][t][1],
                                                 '' if after['troops'][t][2] else ' (CANNOT gain)', gains[t])
                          for t in list(after['troops'])[:4])))
    if loot:
        d = loot.get('donated')
        print('| Loot walk | winner %s; %s; donated %s line(s) for %s XP shown on the screen (canGainXp=%s); troop XP after the battle -> after the loot walk: %s |' % (
            loot.get('winner'), 'encounter closed' if loot.get('closed') else 'encounter NOT closed within the walk',
            d['lines'] if d else '-', fmt(d['xp'], 0) if d else '-', d['canGainXp'] if d else '-',
            '; '.join('%s %d -> %d (%+d%s)' % (who, x['before'], x['after'], x['delta'],
                                              ', ' + ', '.join('%s %+d' % (c.replace('CharacterObject_', ''), v) for c, v in list(x['perTroop'].items())[:3]) if x['perTroop'] else '')
                      for who, x in sorted(loot.get('xp', {}).items())) or 'no roster snapshots'))
    for side in ('Attacker', 'Defender'):
        pa, po = profile['ali'].get(side), profile['omar'].get(side)
        if not pa and not po:
            continue
        pa, po = pa or {}, po or {}
        caps = sorted(set(list((pa.get('captains') or {}).values()) + list((po.get('captains') or {}).values())))
        print('| %s side at spawn | troops %s; troop HealthLimit mean %s on ali / %s on omar%s; captains %s; weapons agree: %s |' % (
            side, pa.get('troops') or po.get('troops'), fmt(pa.get('hpLimit')), fmt(po.get('hpLimit')),
            '' if pa.get('hpLimit') == po.get('hpLimit') else ' <-- DIFFER',
            ','.join(caps) or '-', 'yes' if (pa.get('weapons') or '') == (po.get('weapons') or '') else 'NO'))
    if roster_sync is not None:
        print('| Fixture roster replicated to both clients after | %s s |' % roster_sync)
    for direction, r in sorted(routing.items()):
        if not r['puppetHits'] and not r['routed']:
            continue
        lat = r['latencyMs']
        print('| Routed damage %s | puppet hits %d (%d zero-damage), routed %d, applied by owner %d (%s%% of damaging hits); paired %d, unmatched %d; latency p50 / p90 / p99 / max %s / %s / %s / %s ms; damage scored %d -> applied %d |' % (
            direction, r['puppetHits'], r['zeroDamage'], r['routed'], r['applied'],
            fmt(100.0 * r['applied'] / max(1, r['puppetHits'] - r['zeroDamage']), 0), r['paired'], r['unmatchedRouted'],
            fmt(lat['p50'], 0), fmt(lat['p90'], 0), fmt(lat['p99'], 0), fmt(lat['max'], 0), r['damageScored'], r['damageApplied']))
    if kills:
        print("| Side losses (start -> lost): Ali's side per Ali / per Omar; Omar's side per Omar / per Ali | %s -> %s / %s ; %s -> %s / %s |" % (
            fmt(kills['aliSideStart'], 0), fmt(kills['aliSideLost_perAli'], 0), fmt(kills['aliSideLost_perOmar'], 0),
            fmt(kills['omarSideStart'], 0), fmt(kills['omarSideLost_perOmar'], 0), fmt(kills['omarSideLost_perAli'], 0)))
    if warstats:
        print('| Server casualties (WarStats) | %s |' % ', '.join('%s %d' % w for w in warstats))
    for key, (agree, hashes, counts, with_xp) in sorted(roster_agree.items()):
        print('| Roster %s (counts) | %s%s; count hashes %s; members/wounded/prisoners %s |' % (
            key, 'AGREE' if agree else 'DIFFER', '' if with_xp else ' (XP differs: observer copies carry no XP by design)',
            ' '.join('%s=%s' % (m, (h or '-')[:8]) for m, h in sorted(hashes.items())),
            ' '.join('%s=%s/%s/%s' % (m, c[0], c[1], c[2]) for m, c in sorted(counts.items()))))
    for who in ('ali', 'omar'):
        d = damage.get(who, {})
        if d:
            print('| Damage per puppet hit on %s | %s hits, mean damage %s (base magnitude %s); own-side (zero-damage) hits %s |' % (
            who, d.get('puppetHits', '-'), fmt(d.get('puppetDmgMean')), fmt(d.get('puppetBaseMean')), d.get('ownSideHits', '-')))
        print('| Damage on %s | registerBlow=%s routedReapply=%s appliedLocal=%s suppressed=%s; blows local=%s remote=%s; blocked=%s; remote blows=%s at ~%s%% progress |' % (
                who, d.get('registerBlow', '-'), d.get('routedReapply', '-'), d.get('appliedLocal', '-'), d.get('SUPPRESSED', '-'),
                d.get('blowsLocal', '-'), d.get('blowsRemote', '-'), d.get('blocked', '-'), d.get('remoteBlows', '-'), d.get('remoteProgressAtBlow', '-')))
    print()
    print('## Load (median over the run)')
    print()
    print('| Role | CPU % | Working set MB | Wire KB/s (med / p99) | bulkHz | priorityHz | Sender ms/s | Receiver apply ms/s | Recv queue ms (med / p99) | fps (med / p5) | Mesh msgs/s |')
    print('|---|---|---|---|---|---|---|---|---|---|---|')
    for role in ('host', 'client', 'server'):
        L = load.get(role, {})
        wire = L.get('wireBytesPerSecond', {}).get('median')
        wire99 = L.get('wireBytesPerSecond', {}).get('p99')
        print('| %s | %s | %s | %s / %s | %s | %s | %s | %s | %s / %s | %s / %s | %s |' % (
            role, med(role, 'cpuPercent'), med(role, 'workingSetMb', 0),
            fmt(wire / 1024.0 if wire is not None else None, 1), fmt(wire99 / 1024.0 if wire99 is not None else None, 1),
            med(role, 'bulkHz', 0), med(role, 'priorityHz', 0), med(role, 'senderMsPerSecond'), med(role, 'receiverApplyMsPerSecond', 2),
            med(role, 'receiverQueueMs'), fmt(L.get('receiverQueueMs', {}).get('p99')), med(role, 'fps', 0), fmt(L.get('fps', {}).get('p5'), 0), fmt(mesh.get(role))))
    print()
    print('## Message composition (packets/s: median / max over the 10 s profile windows; KB/s median)')
    print()
    print('| Peer | Type | msgs/s med | msgs/s max | KB/s med | windows present |')
    print('|---|---|---|---|---|---|')
    for role in ('host', 'client', 'server'):
        rows = sorted(composition.get(role, {}).items(), key=lambda kv: -kv[1]['maxPerS'])[:12]
        for name, c in rows:
            print('| %s | %s | %s | %s | %s | %d |' % (role, name.replace('MessagePacket:', ''), fmt(c['medianPerS']), fmt(c['maxPerS']), fmt(c['medianBytesPerS'] / 1024.0, 2), c['windows']))
    print()
    print('server worldQueue: %s' % (('median %s, max %s over %d samples' % (fmt(world_queue['median'], 0), world_queue['max'], world_queue['n'])) if world_queue else '-'))
    if baseline:
        print()
        print('## Against baseline %s' % baseline.get('run'))
        for key in ('totalAgentsDiff', 'aliSideMirrorDiff', 'omarSideMirrorDiff'):
            b = baseline.get(key, {})
            print('- %s median %s -> %s, p90 %s -> %s, max %s -> %s' % (
                key, fmt(b.get('median')), fmt(metrics[key]['median']), fmt(b.get('p90')), fmt(metrics[key]['p90']), fmt(b.get('max')), fmt(metrics[key]['max'])))
        print('- resolution skew %s -> %s s; start skew %s -> %s s' % (
            fmt(baseline.get('resolutionSkewS')), fmt(metrics['resolutionSkewS']), fmt(baseline.get('startSkewDeployS')), fmt(metrics['startSkewDeployS'])))
    print()
    print('metrics.json written to %s' % run)


if __name__ == '__main__':
    main()
