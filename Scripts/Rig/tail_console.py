"""Print the last N lines of a rig console log whatever mix of encodings Tee-Object/Out-File left in it.

    python Scripts/Rig/tail_console.py runs/duel-<tag>-console.log [N]
"""
import re
import sys


def decode_mixed(raw):
    # A UTF-8 header line followed by UTF-16LE (Tee-Object's default) is the usual shape; decode each run
    # separately by looking for the UTF-16 BOM.
    parts = []
    pos = 0
    while True:
        bom = raw.find(b'\xff\xfe', pos)
        if bom < 0:
            parts.append(raw[pos:].decode('utf-8', errors='replace'))
            break
        parts.append(raw[pos:bom].decode('utf-8', errors='replace'))
        end = len(raw)
        # UTF-16 runs until the next UTF-8 BOM or the end
        nxt = raw.find(b'\xef\xbb\xbf', bom + 2)
        if nxt > 0 and (nxt - bom) % 2 == 0:
            end = nxt
        parts.append(raw[bom + 2:end].decode('utf-16-le', errors='replace'))
        pos = end
        if pos >= len(raw):
            break
    text = ''.join(parts)
    # a stray NUL means an odd split; drop them
    return text.replace('\x00', '')


def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass
    path = sys.argv[1]
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 12
    raw = open(path, 'rb').read()
    text = decode_mixed(raw).replace('\r', '')
    lines = [l for l in text.split('\n') if l.strip()]
    print('== %s (%d lines)' % (path, len(lines)))
    for l in lines[-n:]:
        print('  ' + l[:300])


if __name__ == '__main__':
    main()
