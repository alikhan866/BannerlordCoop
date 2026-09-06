"""Before/after summary of a mounted rig pair: python Scripts/Rig/v2_summary.py <before-run-dir> <after-run-dir>

Prints, per exchange, the numbers the mounted plan tracks (PVP-SYNC-PLAN 13.2): attacker puppet position error,
blows and hits on the move, the couch usage rendered on the puppet, equipment packets while couched, missile mirror.
"""
import glob
import json
import os
import re
import sys


def load(run):
    m = json.load(open(os.path.join(run, 'metrics.json'), encoding='utf-8'))
    return {(e['script'], e['label']): e for e in m['exchanges']}


def packets(run):
    counts = []
    for path in glob.glob(os.path.join(run, 'mesh-profile-*.txt')):
        for m in re.finditer(r'AgentEquipmentPacket: ([0-9,]+) packets', open(path, encoding='utf-8', errors='replace').read()):
            counts.append(int(m.group(1).replace(',', '')))
    return max(counts) if counts else 0


def f(v, d=1):
    if v is None:
        return '-'
    return ('%.' + str(d) + 'f') % v if isinstance(v, float) else str(v)


def main():
    before, after = sys.argv[1], sys.argv[2]
    b, a = load(before), load(after)
    print('| Script | Dir | Metric | Before | After |')
    print('|---|---|---|---|---|')
    for key in sorted(set(b) | set(a)):
        eb, ea = b.get(key, {}), a.get(key, {})
        rows = [
            ('attacker puppet pos err p50 / p90 / max m',
             lambda e: '%s / %s / %s' % (f(e.get('pos_err_att_all_p50_m')), f(e.get('pos_err_att_all_p90_m')), f(e.get('pos_err_att_all_max_m')))),
            ('defender puppet pos err p90 m', lambda e: f(e.get('pos_err_all_p90_m'))),
            ('attacks / blows / hits / hit-through-block',
             lambda e: '%s / %s / %s / %s' % (f(e.get('attacks')), f(e.get('blows')), f(e.get('blows_hit')), f(e.get('hit_through_block')))),
            ('wind-up (lag-comp.) / release / direction %',
             lambda e: '%s (%s) / %s / %s' % (f(e.get('windup_pct')), f(e.get('windup_pct_lag_compensated')), f(e.get('release_pct')), f(e.get('direction_pct')))),
            ('damage applied median / first ms', lambda e: '%s / %s' % (f(e.get('damage_latency_apply_ms_median')), f(e.get('damage_latency_first_ms')))),
            ('blows on horse / horse hits / applied', lambda e: '%s / %s / %s' % (f(e.get('mount_blows')), f(e.get('mount_hits')), f(e.get('mount_applied')))),
            ('alt usage (couch) rendered % / owner samples', lambda e: '%s / %s' % (f(e.get('alt_usage_rendered_pct')), f(e.get('alt_usage_owner_samples')))),
            ('throws / mirrored % / mirror med ms / missile hits / victim-side impacts',
             lambda e: '%s / %s / %s / %s / %s' % (f(e.get('throws')), f(e.get('missiles_mirrored_pct')), f(e.get('missile_mirror_ms_median')), f(e.get('missile_hits')), f(e.get('missile_victim_side_impacts')))),
            ('horse speed err p90 m/s / puppet slide %',
             lambda e: '%s / %s' % (f((e.get('mount_attacker') or {}).get('speed_err_p90_mps')), f((e.get('mount_attacker') or {}).get('puppet_slide_pct')))),
        ]
        for name, fn in rows:
            vb = fn(eb) if eb else '-'
            va = fn(ea) if ea else '-'
            if vb.replace('-', '').replace('/', '').strip() == '' and va.replace('-', '').replace('/', '').strip() == '':
                continue
            print('| %s | %s | %s | %s | %s |' % (key[0], key[1], name, vb, va))
    print('| all | | max AgentEquipmentPacket per 10 s | %d | %d |' % (packets(before), packets(after)))


if __name__ == '__main__':
    main()
