"""One PVP-ARMY-SYNC-PLAN section 8.1 row from a run folder's metrics.json.

    python Scripts/Rig/army_row.py <runDir> "<scenario label>" ["<note appended to the fairness column>"]

Prints a single markdown table row with the columns of the 8.1 baseline table. Numbers come from
metrics.json (written by analyze_army.py); nothing is typed by hand.
"""
import json
import os
import sys


def fmt(v, digits=1):
    if v is None or v == '':
        return '-'
    try:
        v = float(v)
    except (TypeError, ValueError):
        return str(v)
    return ('%%.%df' % digits) % v


def main():
    run = sys.argv[1]
    label = sys.argv[2] if len(sys.argv) > 2 else os.path.basename(run)
    note = sys.argv[3] if len(sys.argv) > 3 else ''
    m = json.load(open(os.path.join(run, 'metrics.json'), encoding='utf-8'))
    roles = m.get('roles', {})
    host = 'Ali' if roles.get('ali') == 'host' else 'Omar'
    client = 'Omar' if host == 'Ali' else 'Ali'
    scenario = m.get('scenario', {}) or {}
    tag = os.path.basename(run.rstrip('/\\'))[11:]
    attacker = scenario.get('attacker') or ('omar' if 'swap' in tag else 'ali')
    attacker = attacker.capitalize()
    roles_col = '%s host, %s client; %s attacks (`%s`)' % (host, client, attacker, tag)

    skew = '%s / %s' % (fmt(m.get('startSkewEnterS')), fmt(m.get('startSkewDeployS')))
    size = m.get('size', {})
    sa, so = size.get('ali', {}), size.get('omar', {})
    agree = all('agree=true' in (x.get('engine') or '') for x in (sa, so))
    size_col = '%s coop, engine %s' % (sa.get('battleSize', '-'), 'agrees on both' if agree else 'DIFFERS')
    alloc_col = 'def %s/%s atk %s/%s (fielded/target)' % (sa.get('defenderTotal', '-'), sa.get('defenderTarget', '-'), sa.get('attackerTotal', '-'), sa.get('attackerTarget', '-'))

    census = m.get('agentCensus') or []
    if census:
        agree_n = sum(1 for c in census if c['hashAgree'])
        mirror_col = 'identity hash agrees %d / %d samples (misses are 1-2 deaths in flight)' % (agree_n, len(census))
    else:
        t = m.get('totalAgentsDiff', {})
        mirror_col = 'count diff median %s max %s (%s samples)' % (fmt(t.get('median'), 0), fmt(t.get('max'), 0), t.get('n'))

    peak = m.get('peakAgents', {})
    reserve_col = 'peak agents %s / %s' % (fmt(peak.get('ali'), 0), fmt(peak.get('omar'), 0))

    a, o = m.get('aliSideMirrorDiff', {}), m.get('omarSideMirrorDiff', {})
    kill_col = 'per-side alive counts: max diff %s / %s' % (fmt(a.get('max'), 0), fmt(o.get('max'), 0))
    fin = m.get('final', {})
    rout_col = 'fleeing %s / %s at the end' % (fin.get('ali', {}).get('enemyFleeing', '-'), fin.get('omar', {}).get('enemyFleeing', '-'))

    k = m.get('kills', {})
    d = m.get('damage', {})
    da, do_ = d.get('ali', {}), d.get('omar', {})
    fair = "Ali's side lost %s of %s, Omar's lost %s of %s; puppet hits %s @ %s dmg vs %s @ %s dmg" % (
        fmt(k.get('aliSideLost_perAli'), 0), fmt(k.get('aliSideStart'), 0), fmt(k.get('omarSideLost_perOmar'), 0), fmt(k.get('omarSideStart'), 0),
        da.get('puppetHits', '-'), fmt(da.get('puppetDmgMean')), do_.get('puppetHits', '-'), fmt(do_.get('puppetDmgMean')))
    prof = (m.get('sideProfile') or {}).get('ali') or {}
    hp = {side: (prof.get(side) or {}).get('hpLimit') for side in ('Attacker', 'Defender')}
    if any(hp.values()):
        ali_side = 'Attacker' if attacker == 'Ali' else 'Defender'
        omar_side = 'Defender' if ali_side == 'Attacker' else 'Attacker'
        fair += "; troop HealthLimit Ali's %s vs Omar's %s" % (fmt(hp.get(ali_side), 0), fmt(hp.get(omar_side), 0))
    if note:
        fair += '; ' + note

    routing = m.get('routing') or {}
    if routing:
        parts = []
        for direction, r in sorted(routing.items()):
            lat = r.get('latencyMs', {})
            if lat.get('n'):
                parts.append('%s %s/%s (%s%% applied)' % (direction, fmt(lat.get('p50'), 0), fmt(lat.get('p99'), 0),
                                                        fmt(100.0 * r['applied'] / max(1, r['puppetHits'] - r['zeroDamage']), 0)))
        routed_col = '; '.join(parts) or '-'
    else:
        routed_col = '-'

    orders = m.get('orders') or {}
    if orders:
        oa = orders.get('ali', {})
        holder = oa.get('Attacker') or {}
        charger = oa.get('Defender') or {}
        orders_col = 'holder moved %s m during hold, charger %s m; same on both machines' % (fmt(holder.get('movedDuringHoldM'), 0), fmt(charger.get('movedDuringHoldM'), 0))
    else:
        orders_col = 'n/a'

    res = m.get('resolvedAtS', {})
    end_col = '%s on both at +%s s (skew %s s); rosters agree on counts on all three' % (
        fin.get('ali', {}).get('resultState', '-'), fmt(res.get('ali'), 0), fmt(m.get('resolutionSkewS')))
    if not all((m.get('rosterAgree') or {}).values()):
        end_col = end_col.replace('rosters agree on counts on all three', 'ROSTERS DIFFER')

    L = m.get('load', {})

    def med(role, key, digits=1):
        return fmt(L.get(role, {}).get(key, {}).get('median'), digits)

    def p(role, key, q, digits=0):
        return fmt(L.get(role, {}).get(key, {}).get(q), digits)

    wire = '%s / %s' % (fmt(float(L.get('host', {}).get('wireBytesPerSecond', {}).get('median') or 0) / 1024.0), fmt(float(L.get('client', {}).get('wireBytesPerSecond', {}).get('median') or 0) / 1024.0))
    mesh = m.get('meshMsgsPerS', {})
    mesh_col = '%s / %s' % (fmt(mesh.get('host'), 0), fmt(mesh.get('client'), 0))
    srv = (m.get('composition') or {}).get('server', {}).get('(all types)', {})
    server_col = '%s med, %s peak' % (fmt(srv.get('medianPerS'), 0), fmt(srv.get('maxPerS'), 0))
    wq = m.get('worldQueue', {})
    wq_col = '%s (max %s)' % (fmt(wq.get('median'), 0), wq.get('max', '-'))
    sender = '%s / %s' % (med('host', 'senderMsPerSecond'), med('client', 'senderMsPerSecond'))
    recv = '%s / %s' % (med('host', 'receiverApplyMsPerSecond'), med('client', 'receiverApplyMsPerSecond'))
    rq = '%s / %s' % (p('host', 'receiverQueueMs', 'p99'), p('client', 'receiverQueueMs', 'p99'))
    cpu = '%s / %s / %s' % (med('host', 'cpuPercent'), med('client', 'cpuPercent'), med('server', 'cpuPercent'))
    ws = '%s / %s / %s' % (med('host', 'workingSetMb', 0), med('client', 'workingSetMb', 0), med('server', 'workingSetMb', 0))
    fps = '%s (p5 %s) / %s (p5 %s)' % (med('host', 'fps', 0), p('host', 'fps', 'p5'), med('client', 'fps', 0), p('client', 'fps', 'p5'))

    cols = [label, roles_col, skew, size_col, alloc_col, mirror_col, reserve_col, kill_col, rout_col, fair, routed_col,
            'n/a', 'n/a', 'n/a', orders_col, end_col, wire, mesh_col, server_col, wq_col, sender, recv, rq, cpu, ws, fps]
    print('| ' + ' | '.join(cols) + ' |')


if __name__ == '__main__':
    main()
