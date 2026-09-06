"""Map drift analyzer for Map-Drift-Test.ps1 run folders.

    python Scripts/Rig/analyze_drift.py <runDir>

positions-<who>.txt holds, per sample, a SAMPLE line with the rig's wall clock, then the dump header
(POSITIONS n=.. utcMs=.. campaignTicks=.. campaignHours=.. timeMode=.. side=..) and one row per party out on the
map:

    <id> <x> <y> <speed> <hasNextWaypoint 0|1> <shortTermBehavior> <moveMode> <kind> [<targetX> <targetY> <army>]

The campaign hours come from the game itself (CampaignTime.Now.ToHours), because the tick constants are filled in
by the engine and read as zero outside it. Party speed is map units per campaign hour, so the distance a party may
cover between two samples is speed x elapsed campaign hours.

Reported per client:
  - drift: how far the client's copy of a party is from the server's, at matched instants
  - SNAP: the client's copy moved much further than its speed allows and landed on the server's position. This is
    the "lords teleport" a player sees: the copy walked its own way, drifted, and was then put right in one frame.
  - world jump: the server's copy moved that far too, so the party really did jump (settlement exit, army attach)
  - frozen: the client's copy has somewhere to go and no next waypoint while the server's copy is moving
"""
import math
import os
import re
import statistics
import sys


def pct(values, q):
    if not values:
        return None
    v = sorted(values)
    return v[min(len(v) - 1, int(round(q * (len(v) - 1))))]


def fmt(v, d=1):
    return '-' if v is None else ('%.' + str(d) + 'f') % v


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def parse(path):
    """[{utcMs, elapsed, hours, timeMode, parties: {id: (x, y, speed, hasNext, behavior, moveMode, kind, army)}}]"""
    samples = []
    if not os.path.exists(path):
        return samples
    cur = None
    for line in open(path, encoding='utf-8-sig', errors='replace'):
        line = line.rstrip('\r\n')
        m = re.match(r'SAMPLE utcMs=(\d+) elapsed=(\d+)', line)
        if m:
            cur = {'utcMs': int(m.group(1)), 'elapsed': int(m.group(2)), 'hours': None, 'timeMode': None, 'parties': {}}
            samples.append(cur)
            continue
        if cur is None:
            continue
        m = re.match(r'POSITIONS n=(\d+) utcMs=(\d+) campaignTicks=(-?\d+)(?: campaignHours=(-?[\d.]+))? timeMode=(\S+) side=(\w+)', line)
        if m:
            cur['hours'] = float(m.group(4)) if m.group(4) else None
            cur['timeMode'] = m.group(5)
            continue
        parts = line.split(' ')
        if len(parts) >= 8 and re.match(r'-?[\d.]+$', parts[1]):
            try:
                cur['parties'][parts[0]] = (
                    float(parts[1]), float(parts[2]), float(parts[3]), parts[4] == '1',
                    parts[5], parts[6], parts[7], parts[10] if len(parts) > 10 else '-')
            except ValueError:
                pass
    return [s for s in samples if s['parties']]


def nearest(samples, utc_ms, max_ms=4000):
    best = None
    for s in samples:
        d = abs(s['utcMs'] - utc_ms)
        if best is None or d < best[0]:
            best = (d, s)
    return best[1] if best and best[0] <= max_ms else None


def main():
    run = sys.argv[1]
    server = parse(os.path.join(run, 'positions-server.txt'))
    print('# Map drift report: %s' % os.path.basename(run.rstrip('/\\')))
    print()
    if not server:
        print('no server samples')
        return
    have_hours = server[0]['hours'] is not None
    print('server samples: %d, parties per sample median %s, time mode(s) %s' % (
        len(server), fmt(statistics.median([len(s['parties']) for s in server]), 0),
        ','.join(sorted(set(s['timeMode'] for s in server)))))
    if have_hours and len(server) >= 2:
        hours = server[-1]['hours'] - server[0]['hours']
        wall = (server[-1]['utcMs'] - server[0]['utcMs']) / 1000.0
        print('campaign clock advanced %s hours in %s s of wall time (%s campaign hours per real minute)' % (
            fmt(hours, 2), fmt(wall, 0), fmt(hours / max(1e-6, wall) * 60, 1)))
        ratios = []
        for a, b in zip(server, server[1:]):
            dh = b['hours'] - a['hours']
            if dh <= 0:
                continue
            for pid, pa in a['parties'].items():
                pb = b['parties'].get(pid)
                if not pb or pa[2] <= 0:
                    continue
                moved = dist(pa, pb)
                if moved > 0.01:
                    ratios.append(moved / (dh * pa[2]))
        if ratios:
            print('server parties covered %s x (speed x hours) per interval (median; 1.0 confirms speed is map units per campaign hour)' % fmt(statistics.median(ratios), 2))
    elif not have_hours:
        print('NOTE: this run predates campaignHours in the dump, so jump detection is skipped.')
    print()

    for who in ('ali', 'omar'):
        client = parse(os.path.join(run, 'positions-%s.txt' % who))
        if not client:
            print('## %s: no samples' % who)
            continue
        drift = []
        worst = {}
        frozen = 0
        frozen_examples = []
        snaps = []
        snap_examples = []
        world_jumps = []
        intervals = 0
        compared = 0
        drift_by_party = {}
        for i, cs in enumerate(client):
            ss = nearest(server, cs['utcMs'])
            if ss is None:
                continue
            for pid, cp in cs['parties'].items():
                sp = ss['parties'].get(pid)
                if not sp:
                    continue
                compared += 1
                d = dist(cp, sp)
                drift.append(d)
                drift_by_party.setdefault(pid, []).append(d)
                if d > worst.get(pid, (0,))[0]:
                    worst[pid] = (d, cs['elapsed'], cp, sp)
                if not cp[3] and sp[3] and cp[2] > 0 and cp[6] != 'P':
                    frozen += 1
                    if len(frozen_examples) < 5:
                        frozen_examples.append('%s +%ds client (%s,%s) %s speed %s / server (%s,%s) moving' % (
                            pid, cs['elapsed'], fmt(cp[0]), fmt(cp[1]), cp[4], fmt(cp[2], 2), fmt(sp[0]), fmt(sp[1])))
            if i == 0 or not have_hours:
                continue
            prev = client[i - 1]
            prev_server = nearest(server, prev['utcMs'])
            if prev_server is None or cs['hours'] is None or prev['hours'] is None:
                continue
            dh = cs['hours'] - prev['hours']
            if dh <= 0:
                continue
            for pid, cp in cs['parties'].items():
                pp = prev['parties'].get(pid)
                sp, sp_prev = ss['parties'].get(pid), prev_server['parties'].get(pid)
                if not pp or not sp or not sp_prev:
                    continue
                intervals += 1
                client_moved = dist(pp, cp)
                server_moved = dist(sp_prev, sp)
                # Half a unit of slack for the sampling jitter, then 1.5x the speed the party claims.
                allowed = 0.5 + 1.5 * max(cp[2], pp[2]) * dh
                if client_moved <= allowed:
                    continue
                drift_before = dist(pp, sp_prev)
                drift_after = dist(cp, sp)
                if server_moved > allowed:
                    world_jumps.append(client_moved)
                    continue
                snaps.append(client_moved)
                if drift_before - drift_after > 1.0 and len(snap_examples) < 8:
                    snap_examples.append('%s +%ds moved %s units (its speed allows %s in %s campaign h); drift %s -> %s, so it was put on the server position' % (
                        pid, cs['elapsed'], fmt(client_moved, 1), fmt(allowed, 1), fmt(dh, 2), fmt(drift_before, 1), fmt(drift_after, 1)))
        print('## %s (%d samples, %d party comparisons, %d intervals)' % (who, len(client), compared, intervals))
        print()
        print('| Metric | Value |')
        print('|---|---|')
        print('| Drift, client copy vs server (map units) | p50 %s, p90 %s, p99 %s, max %s; over 2 units %s%% of samples, over 8 units %s%% |' % (
            fmt(pct(drift, 0.5), 2), fmt(pct(drift, 0.9), 2), fmt(pct(drift, 0.99), 2), fmt(max(drift) if drift else None, 2),
            fmt(100.0 * sum(1 for d in drift if d > 2) / max(1, len(drift))), fmt(100.0 * sum(1 for d in drift if d > 8) / max(1, len(drift)))))
        if have_hours:
            print('| SNAPS: client copy moved further than its speed allows while the server copy did not | %d of %d intervals (%s%%); size p50 %s, p90 %s, max %s units |' % (
                len(snaps), intervals, fmt(100.0 * len(snaps) / max(1, intervals)),
                fmt(pct(snaps, 0.5), 1), fmt(pct(snaps, 0.9), 1), fmt(max(snaps) if snaps else None, 1)))
            print('| World jumps: both copies moved that far (settlement exit, army attach, spawn) | %d of %d intervals; max %s units |' % (
                len(world_jumps), intervals, fmt(max(world_jumps) if world_jumps else None, 1)))
        print('| Frozen copies (has a destination, no next waypoint, server copy moving) | %d party-samples |' % frozen)
        parties_with_big_drift = sum(1 for pid, ds in drift_by_party.items() if max(ds) > 8)
        print('| Parties whose copy was ever more than 8 units out | %d of %d seen |' % (parties_with_big_drift, len(drift_by_party)))
        # An army drags its attached parties to the leader's position, so a copy that disagrees about the army is
        # a different kind of error from one that merely walked a slightly different line.
        big = [pid for pid, ds in drift_by_party.items() if max(ds) > 8]
        in_army = sum(1 for pid in big if (worst[pid][2][7] if pid in worst else '-') != '-')
        print('| Of those, attached to or leading an army | %d of %d |' % (in_army, len(big)))
        top = sorted(worst.items(), key=lambda kv: -kv[1][0])[:4]
        print('| Worst drift | %s |' % '; '.join('%s %s units at +%ds%s' % (
            pid.replace('MobileParty_', ''), fmt(w[0], 1), w[1],
            ' (army)' if w[2][7] != '-' else '') for pid, w in top))
        for ex in snap_examples:
            print('|   snap | %s |' % ex.replace('MobileParty_', ''))
        for ex in frozen_examples:
            print('|   frozen | %s |' % ex.replace('MobileParty_', ''))
        print()

    for who in ('ali', 'omar'):
        path = os.path.join(run, 'smoothing-%s.txt' % who)
        if os.path.exists(path):
            line = open(path, encoding='utf-8-sig', errors='replace').read().strip().replace('\n', ' ')
            print('%s position-correction delivery: %s' % (who, line))
    for who in ('ali', 'omar'):
        path = os.path.join(run, 'client-%s.log' % who)
        if not os.path.exists(path):
            continue
        lines = [l for l in open(path, encoding='utf-8-sig', errors='replace') if '[PartyDiag] side=client parties' in l]
        if lines:
            print('%s PartyDiag (last): %s' % (who, re.sub(r'^\[\(\d+\) ', '', lines[-1].strip())[:200]))


if __name__ == '__main__':
    main()
