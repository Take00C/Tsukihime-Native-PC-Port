"""Package K44 oracle: the PS4's flowchart marks run under unicorn (tools/tsuki/flow_marks_emu.py), as JSON lines for
FlowMarksOracleTests.  Nothing is written to disk.

Lines, in order:
    {"state": i, "f": [[word, value], ...], "g": [[word, value], ...]}     the input states (all other words 0)
    {"ids": [node of scene id -2, -1, 0, ..., 10010]}                   0xB8870
    {"j": [from, to, pick, state], "s": special, "f": [[w, v]...], "g": [[w, v]...]}   0xBA690 (words that changed)
    {"c": [choice, state], "f": [...], "g": [...]}                      0xB8620
    {"summary": {...}}

    python -I tests/Tsuki.Tests.Data/Game/flow_marks_oracle.py      (needs eboot.bin, unicorn, capstone)
"""
import json
import random
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from eboot import eboot_path  # noqa: E402
from tsuki import flow_marks_emu as fm  # noqa: E402


def states(rng):
    zero_f, zero_g = [0] * fm.F_WORDS, [0] * fm.G_WORDS
    out = [(zero_f, zero_g)]
    for density, f24 in ((0.9, 0), (0.9, 1), (0.15, 0)):
        f = fm.random_f(rng, density)
        f[24] = f24
        out.append((f, list(zero_g)))
    full = [0] * fm.F_WORDS
    for w in fm.STATE_WORDS:
        full[w] = 0xFF
    full[24], full[50], full[54], full[110] = 1, 1, 1, 7
    out.append((full, list(zero_g)))
    g = [0] * fm.G_WORDS
    for w in range(100, 450):
        g[w] = rng.randrange(0, 0x10000) if rng.random() < 0.3 else 0
    out.append((fm.random_f(rng, 0.5), g))
    return out


def diff(a, b):
    return [[i, y] for i, (x, y) in enumerate(zip(a, b)) if x != y]


def main():
    p = eboot_path()
    if p is None:
        print("eboot.bin not found", file=sys.stderr)
        return 1
    emu = fm.FlowMarkEmu(p)
    rng = random.Random(20261102)
    st = states(rng)
    for i, (f, g) in enumerate(st):
        print(json.dumps({"state": i, "f": [[w, v] for w, v in enumerate(f) if v], "g": [[w, v] for w, v in enumerate(g) if v]}))
    print(json.dumps({"ids": [emu.node_of_scene_id(i) for i in range(-2, 10011)]}))

    d = Path(p).read_bytes()
    edges = [struct.unpack_from("<12i", d, fm.TEXT_OFF + 0x2218D0 + 0x30 * i) for i in range(630)]
    tos_of = {}
    for e in edges:
        tos_of.setdefault(e[0], set()).add(e[1])
    consts = fm.special_constants()
    special_froms = set(consts)
    pick_froms = {101, 166, 221, 369, 410}
    froms = sorted(set(range(-1, 532)) | {600, 905, 1196, 1197} | set(tos_of))
    jumps = 0
    for fr in froms:
        tos = set(tos_of.get(fr, ())) | {-1, 0, 1, 486, rng.randrange(1, 560)}
        if fr in special_froms:
            tos |= set(consts[fr])
        for to in sorted(tos):
            for pick in ((0, 1, 2, 0xFFFF) if fr in pick_froms else (0,)):
                for si, (f, g) in enumerate(st):
                    f2, g2 = emu.jump(fr, to, f, g, pick)
                    print(json.dumps({"j": [fr, to, pick, si], "s": emu.special_result, "f": diff(f, f2), "g": diff(g, g2)}))
                    jumps += 1
    choices = 0
    for c in range(-1, 91):
        for si, (f, g) in enumerate(st):
            f2, g2 = emu.choice(c, f, g)
            print(json.dumps({"c": [c, si], "f": diff(f, f2), "g": diff(g, g2)}))
            choices += 1
    print(json.dumps({"summary": {"states": len(st), "jumps": jumps, "choices": choices}}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
