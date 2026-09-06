"""Owner-vs-puppet animation comparison from two timeline dumps.

Both clients run on one machine, so sample timestamps share a clock and the same swing can
be compared on both sides at the same instant.

Reported PER DIRECTION. The one that matters is CLIENT: agents owned by the host, rendered
on the joining client. The host direction is the control - it is reported to be checked at
the end, not optimised against.
"""
import re, sys, bisect
from collections import Counter

NAME = {0:'none',1:'DefendFist',2:'DefendShield',15:'ReadyRanged',16:'ReleaseRanged',
        17:'ReleaseThrowing',18:'Reload',19:'ReadyMelee',20:'ReleaseMelee',21:'ParriedMelee',
        22:'BlockedMelee',23:'Fall',33:'EquipUnequip',35:'Idle',36:'Guard',
        48:'StruckLight',49:'StruckMedium',50:'StruckHeavy'}

MELEE  = (19, 20)                 # ReadyMelee, ReleaseMelee
RANGED = (15, 16, 17, 18)         # ReadyRanged, ReleaseRanged, ReleaseThrowing, Reload
def nm(t): return NAME.get(t, 't%d' % t)

SAMPLE = re.compile(r'(\d+):(-?\d+)/(\d+)@(-?\d+)x(-?\d+)d(-?\d+)(?:p(-?\d+),(-?\d+))?(?:m(-?\d+)/(-?\d+)/(-?\d+))?(?:h(-?\d+)a(-?\d+)f(-?\d+)u(-?\d+))?(?:k(-?\d+)/(-?\d+))?(?:g(-?\d+))?')
BLOCK  = re.compile(r'([0-9a-f]{8}) (PUPPET|LOCAL) n=(\d+) t0=(\d+) \[([^\]]*)\]')

def load(path):
    txt = open(path, encoding='utf-8-sig', errors='replace').read()
    agents = {}
    for m in BLOCK.finditer(txt):
        t0 = int(m.group(4))
        rows = []
        for s in SAMPLE.finditer(m.group(5)):
            px = int(s.group(7)) if s.group(7) else None
            py = int(s.group(8)) if s.group(8) else None
            mt = int(s.group(9)) if s.group(9) else None
            msp = int(s.group(11)) if s.group(11) else None
            rows.append((t0 + int(s.group(1)), int(s.group(2)), int(s.group(3)), int(s.group(4)),
                         px, py, mt, msp))
        agents[m.group(1)] = (m.group(2), rows)
    return agents

def direction(owner_agents, puppet_agents, types, tol_ms=40):
    """Owner plays, puppet renders. Returns (moments, pairs, per-owner-action detail)."""
    pairs, detail, moments = Counter(), {}, 0
    for g in sorted(set(owner_agents) & set(puppet_agents)):
        if owner_agents[g][0] != 'LOCAL' or puppet_agents[g][0] != 'PUPPET':
            continue
        ps = puppet_agents[g][1]
        if not ps:
            continue
        ptimes = [s[0] for s in ps]
        for t, _idx, typ, _prog, _px, _py, _mt, _ms in owner_agents[g][1]:
            if typ not in types:
                continue
            moments += 1
            k = bisect.bisect_left(ptimes, t)
            best = None
            for c in (k - 1, k):
                if 0 <= c < len(ptimes) and abs(ptimes[c] - t) <= tol_ms:
                    if best is None or abs(ptimes[c] - t) < abs(ptimes[best] - t):
                        best = c
            if best is None:
                pairs['(no sample)'] += 1
                continue
            shown = nm(ps[best][2])
            pairs[shown] += 1
            detail.setdefault(nm(typ), Counter())[shown] += 1
    return moments, pairs, detail

def report(label, moments, pairs, detail):
    print("--- %s ---" % label)
    if moments == 0:
        print("   NO DATA - tracked agents never swung in this direction.")
        return {}
    total = sum(pairs.values())
    print("   owner mid-swing moments: %d" % moments)
    for k, v in pairs.most_common(5):
        print("      %-14s %6d  %5.1f%%" % (k, v, 100.0 * v / total))
    out = {}
    for action in sorted(detail):
        c = detail.get(action)
        if not c:
            continue
        n = sum(c.values())
        out[action] = 100.0 * c.get(action, 0) / n
        print("   owner %-12s -> %s" % (action,
              ", ".join("%s %.0f%%" % (k, 100.0 * v / n) for k, v in c.most_common(4))))
    return out

def position_error(A, B, tol_ms=40):
    """How far apart the two machines place the SAME agent at the same instant, in metres."""
    import math
    errs, big = [], 0
    for g in sorted(set(A) & set(B)):
        b = [s for s in B[g][1] if s[4] is not None]
        if not b:
            continue
        bt = [s[0] for s in b]
        for t, _i, _ty, _pr, ax, ay, _mt, _ms in A[g][1]:
            if ax is None:
                continue
            k = bisect.bisect_left(bt, t)
            best = None
            for c in (k - 1, k):
                if 0 <= c < len(bt) and abs(bt[c] - t) <= tol_ms:
                    if best is None or abs(bt[c] - t) < abs(bt[best] - t):
                        best = c
            if best is None:
                continue
            dx = (ax - b[best][4]) / 10.0
            dy = (ay - b[best][5]) / 10.0
            d = math.sqrt(dx * dx + dy * dy)
            errs.append(d)
            if d >= 2.0:
                big += 1
    if not errs:
        print("POSITION ERROR: no comparable samples")
        return
    errs.sort()
    n = len(errs)
    def pct(q):
        return errs[min(n - 1, int(q * n))]
    print("POSITION ERROR between the two machines, same agent same instant (%d samples)" % n)
    print("   mean=%.2fm  p50=%.2fm  p90=%.2fm  p99=%.2fm  max=%.2fm  >=2m: %d (%.1f%%)"
          % (sum(errs) / n, pct(0.50), pct(0.90), pct(0.99), errs[-1], big, 100.0 * big / n))


def mount_animation(A, B, label_a, label_b, tol_ms=40):
    """Is the horse's own animation playing on both machines, and does it match its movement?"""
    from collections import Counter
    stats = {}
    for name, src in ((label_a, A), (label_b, B)):
        moving_still = moving = 0
        acts = Counter()
        for g in src:
            for s in src[g][1]:
                mt, ms = s[6], s[7]
                if mt is None or mt < 0 or ms is None or ms < 0:
                    continue          # no mount on this agent
                acts[mt] += 1
                if ms >= 15:          # 1.5 m/s or faster - definitely moving
                    moving += 1
                    if mt == 35 or mt == 0:   # Idle / Other while moving == sliding
                        moving_still += 1
        stats[name] = (moving, moving_still, acts)

    print("MOUNT ANIMATION (horses sampled with their riders)")
    for name, (moving, still, acts) in stats.items():
        if moving == 0:
            print("   %-6s no moving mounts sampled" % name)
            continue
        print("   %-6s moving samples=%d  SLIDING (moving, no leg animation)=%d (%.1f%%)"
              % (name, moving, still, 100.0 * still / moving))
        print("          mount actions: %s" % ", ".join(
            "%s:%d" % (nm(k), v) for k, v in acts.most_common(5)))


def run(host_path, client_path):
    host, client = load(host_path), load(client_path)
    print("agents: host=%d client=%d shared=%d"
          % (len(host), len(client), len(set(host) & set(client))))
    out = {}
    for kind, types in (('MELEE', MELEE), ('RANGED', RANGED)):
        print()
        print("################ %s ################" % kind)
        out[('client', kind)] = report(
            "CLIENT view (host's agents rendered on the client)  <- the target",
            *direction(host, client, types))
        print()
        out[('host', kind)] = report(
            "HOST view (client's agents rendered on the host)    <- control",
            *direction(client, host, types))

    print()
    print("=" * 70)
    for side in ('client', 'host'):
        m, r = out[(side, 'MELEE')], out[(side, 'RANGED')]
        print("%-6s MELEE  windup %5.1f%%  release %5.1f%%   |   "
              "RANGED  ready %5.1f%%  release %5.1f%%  reload %5.1f%%"
              % (side.upper(),
                 m.get('ReadyMelee', 0.0), m.get('ReleaseMelee', 0.0),
                 r.get('ReadyRanged', 0.0), r.get('ReleaseRanged', 0.0), r.get('Reload', 0.0)))
    print("=" * 70)
    print()
    H, Cl = load(host_path), load(client_path)
    position_error(H, Cl)
    print()
    mount_animation(H, Cl, 'HOST', 'CLIENT')
    return out

if __name__ == '__main__':
    run(sys.argv[1], sys.argv[2])
