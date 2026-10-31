"""Generator of StageOracleCases.g.cs: synthetic test vectors for package K2 (the stage), computed by the Python oracle.

No game data is involved: the pictures, the motion tables and the statements are synthetic and seeded.  The oracle modules are
``tsuki.misc_layer_state_and_motions`` (scene model, commit, motions, easing, projection) and ``tsuki.quake`` (rand, sinf, quake, shake,
swing, MSQK, SHOK).  Run from the repository root:

    .venv/Scripts/python.exe -I tests/Tsuki.Tests.Synthetic/Stage/gen_stage_cases.py tests/Tsuki.Tests.Synthetic/Stage/StageOracleCases.g.cs
"""
from __future__ import annotations

import math
import random
import struct
import sys
from pathlib import Path
from types import SimpleNamespace
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from tsuki import quake as Q  # noqa: E402
from tsuki import misc_layer_state_and_motions as L  # noqa: E402

SEED = 20261026


def fbits(x: float) -> str:
    return "0x%08X" % struct.unpack("<I", struct.pack("<f", x))[0]


def dbits(x: float) -> str:
    return "0x%016XUL" % struct.unpack("<Q", struct.pack("<d", x))[0]


def fb(x: float) -> str:
    """Canonical float text: hex of the float32 bits, 'nan' for NaN (as StageCanon writes it)."""
    if x != x:
        return "nan"
    return "%08x" % struct.unpack("<I", struct.pack("<f", x))[0]


def cs(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


# -- synthetic picture catalog -------------------------------------------------------------------------------------------------------------
PICS = [
    # name, anchor_x, anchor_y, scale_class, draw_flags, has_face
    ("BLACK", 320, 181, 0, 3, False),
    ("WHITE", 320, 181, 0, 3, False),
    ("IMG0001", 345, 206, 0, 0, False),
    ("IMG0002", 100, 80, 1, 0, True),
    ("IMG0003", 60, 40, 2, 0, False),
    ("IMG0004", 30, 20, 3, 0, False),
    ("IMG0143", 400, 300, 0, 0, False),
    ("IMG0143_01_01", 410, 306, 0, 0, False),
    ("IMG0143_11_11", 421, 311, 0, 0, False),
    ("IMG0143_01_02", 412, 330, 0, 0, False),
    ("IMG0143_02_01", 416, 304, 0, 0, False),
    ("BOY_A_00_00_N01", 150, 120, 1, 0, True),
]
CAT = [SimpleNamespace(anchor_x=a, anchor_y=b, scale_class=c, draw_flags=d, name1="face" if f else None) for (_, a, b, c, d, f) in PICS]
NAMES = [p[0] for p in PICS]


# -- canonical text of a record (the subset that the synthetic tests compare) -------------------------------------------------------------
def canon_rec(f: dict) -> str:
    m = f["motion"]
    mid, mdiv = struct.unpack_from("<hh", m, 0)
    mfr = struct.unpack_from("<q", m, 8)[0]
    col = f["colour"].hex() if isinstance(f["colour"], (bytes, bytearray)) else f["colour"]
    return (f"pic:{f['pic']} face:{f['face']} blend:{f['blend']} fx:{f['fx_mode']},{f['fx_a']},{f['fx_b']} alpha:{fb(f['alpha'])} xy:{fb(f['x'])},{fb(f['y'])} "
            f"off:{fb(f['off_x'])},{fb(f['off_y'])} rot:{f['rot']} scale:{f['scale']} anchor:{f['anchor_x']},{f['anchor_y']} "
            f"zoom:{f['zoom_x']},{f['zoom_y']} zd:{fb(f['zoom_dx'])},{fb(f['zoom_dy'])} tz:{f['tw_zoom'][1]} crop:{','.join(map(str, f['crop']))} "
            f"a:{f['a16']},{f['a17']},{f['angle']} stns:{f['stns']},{f['stns_b']} motion:{mid},{mdiv},{mfr},{m[16]} colour:{col} acur:{fb(f['alpha_cur'])} zo:{f.get('z_order', 0)}")


def canon_commit(c: dict) -> str:
    crop = ",".join(map(str, c["crop"]))
    return (f"pic:{c['pic']} blend:{c['blend']} fx:{c['fx_mode']},{c['fx_a']},{c['fx_b']} xy:{fb(c['x'])},{fb(c['y'])} off:{fb(c['off_x'])},{fb(c['off_y'])} "
            f"alpha:{fb(c['alpha'])} state:{c['state']} scale:{c['scale']} anchor:{c['anchor_x']},{c['anchor_y']} rot:{c['rot']} mask:{c['mask']} parent:{c['parent']} "
            f"sttr:{c['sttr']} soxy:{c['soxy_x']},{c['soxy_y']} flags:{c['flags']} crop:{crop} a:{c['a16']},{c['a17']},{c['angle']} stns:{c['stns']},{c['stns_b']}")


# -- A. easing -------------------------------------------------------------------------------------------------------------------------------
def gen_ease(out: list[str]) -> None:
    tuples = [(0.0, 0.0, 1.0, 60.0), (30.0, 0.0, 1.0, 60.0), (60.0, 0.0, 1.0, 60.0), (17.0, 0.0, 1.0, 59.0), (1234.5, -20.0, 700.0, 3000.0),
              (5.0, 10.0, -4.0, 12.0), (0.25, 0.0, 1.0, 1.0), (0.75, 0.0, 1.0, 1.0), (101.0, 3.0, 9.0, 100.0), (-5.0, 0.0, 1.0, 100.0),
              (333.3, 1000.0, -500.0, 400.0), (0.0, 1.0, 1.0, -1.0)]
    lines = []
    for eid in list(range(0, 46)) + [50, 61, 62, 70, 71, -3]:
        for (t, b, c, d) in tuples:
            if eid >= 12 and eid not in L.EASE_EXACT and d <= 0:
                continue
            v = L.ease(eid, t, b, c, d)
            lines.append(f"        ({eid}, {t!r}, {b!r}, {c!r}, {d!r}, {dbits(v)}),")
    out.append("    /// <summary>(id, t, b, c, d, bits of the oracle's result)</summary>")
    out.append("    public static readonly (int Id, double T, double B, double C, double D, ulong Bits)[] Ease =\n    [")
    out += lines
    out.append("    ];\n")


# -- B. sinf / rand ----------------------------------------------------------------------------------------------------------------------------
def gen_rand(out: list[str]) -> None:
    rr = random.Random(SEED)
    args = [0.0, -0.0, 1e-4, 3e-4, 0.5, 0.7853981, 0.7853982, 1.0, 1.5707963, 2.0, 2.356, 3.1415927, 4.0, 5.5, 6.2831855, 7.0, 10.0, 30.0, 100.0,
            1000.0, 12345.678, 65536.0, 200000.0, 4.0e8, -1.0, -2.5, -3.9, -100.0]
    for _ in range(120):
        args.append(rr.uniform(-60.0, 60.0))
    for _ in range(80):
        args.append(rr.uniform(-4000.0, 4000.0))
    for _ in range(60):
        args.append(float(Q.f32(rr.random() * 6.2831855 * rr.randint(0, 80))))
    lines = [f"        ({fbits(Q.f32(a))}, {fbits(Q.ps4_sinf(Q.f32(a)))})," for a in args]
    out.append("    /// <summary>(float32 bits of x, float32 bits of the libc sinf)</summary>")
    out.append("    public static readonly (uint X, uint Sin)[] Sinf =\n    [")
    out += lines
    out.append("    ];\n")
    r = Q.Ps4Rand()
    vals = [r.rand() for _ in range(6)]
    r.srand(Q.title_seed(12, 34))
    vals += [r.rand() for _ in range(4)]
    out.append("    /// <summary>rand() from state 1 (6 values), then after srand(title_seed(12, 34)) (4 values)</summary>")
    out.append("    public static readonly int[] Rand = [" + ", ".join(map(str, vals)) + "];\n")
    r = Q.Ps4Rand()
    r2 = Q.Rnd2(r)
    v2 = [r2() for _ in range(5)]
    out.append("    /// <summary>Rnd2 from the initial state over a fresh rand (5 values)</summary>")
    out.append("    public static readonly ulong[] Rnd2 = [" + ", ".join(f"{v}UL" for v in v2) + "];\n")
    r = Q.Ps4Rand()
    rg = [Q.rnd_range(r, -2.5, 2.5) for _ in range(5)] + [Q.rnd_range(r, Q.f32(3.1111112), Q.f32(-3.1111112)) for _ in range(3)]
    out.append("    /// <summary>rnd_range(-2.5, 2.5) x5 then rnd_range(3.1111112, -3.1111112) x3 from state 1 (float32 bits)</summary>")
    out.append("    public static readonly uint[] RndRange = [" + ", ".join(fbits(v) for v in rg) + "];\n")


# -- C. shakes ---------------------------------------------------------------------------------------------------------------------------------
def args_of(stmt: str) -> list[Any]:
    a = L._Args(stmt)
    return [a.expr(i) if a.has(i) else None for i in range(10)]


SHAKES = [
    "_SQK2(5,3,3,,1000,,,,);", "_SQK2(5,4,6,100,600,0,0,0,0);", "_SQK2(5,5,5,,-1,300,,,);", "_SQK2(5,15,15,40,3000,,1,900,);", "_SQK2(5,2,2,,500,,,500,);",
    "_SHK2(5,4,6,500,1000);", "_SHK2(5,0,15,,-1);", "_SHK2(5,8,4,300,2000,0,1,200,);", "_SHK2(5,10,10,100,-1,0,,,3);",
    "_SSW2(5,7,15,1000,2000,1,2000);", "_SSW2(5,2,20,300,800,2,-1,200);", "_SSW2(5,7,15,,,,4000);",
]


def gen_shakes(out: list[str]) -> None:
    lines = []
    for si, stmt in enumerate(SHAKES):
        sl = Q.ShakeLayers(Q.Ps4Rand(1000 + si))
        sl.layers[5].used = True
        kind = stmt[1:5]
        args = args_of(stmt)
        res = {"SQK2": Q.sqk2_start, "SHK2": Q.shk2_start, "SSW2": Q.ssw2_start}[kind](args)
        assert res is not None
        sl.start(res[1])
        frames = []
        rr = random.Random(SEED + si)
        for f in range(40):
            dt = rr.choice([1, 1, 1, 2, 3])
            sl.update(dt)
            s0, s1 = sl.layers[5].slots
            frames.append((dt, fbits(s0.x), fbits(s0.y), fbits(s1.x), fbits(s1.y), s0.kind, s1.kind, s0.elapsed, s1.elapsed))
        lines.append(f"        ({cs(stmt)}, {1000 + si}UL, {sl.rng.state}UL,\n         [" + ", ".join(f"({d}, {a}, {b}, {c}, {e}, {k0}, {k1}, {e0}, {e1})" for (d, a, b, c, e, k0, k1, e0, e1) in frames) + "]),")
    out.append("    /// <summary>(statement on layer 5, rand seed state, final rand state, 40 frames of (dt, slot0 x, y, slot1 x, y, kind0, kind1, elapsed0, elapsed1))</summary>")
    out.append("    public static readonly (string Statement, ulong Seed, ulong FinalRand, (int Dt, uint X0, uint Y0, uint X1, uint Y1, int K0, int K1, int E0, int E1)[] Frames)[] Shakes =\n    [")
    out += lines
    out.append("    ];\n")

    # screen effects
    lines = []
    for stmt in ["_SHOK(0,3,100,10,0);", "_SHOK(5,1,300,3,0);", "_SHOK(2,2,200,2,150);", "_SHOK(0,4,50,1,0);"]:
        sh = Q.ScreenShock()
        a = Q.ScreenShock.parse([x for x in re_args(stmt)])
        sh.start(*a)
        fr = []
        for f in range(45):
            sh.update(1)
            fr.append((fbits(sh.x), fbits(sh.y)))
        lines.append(f"        ({cs(stmt)}, [" + ", ".join(f"({x}, {y})" for x, y in fr) + "]),")
    out.append("    /// <summary>(SHOK statement, 45 frames of (x bits, y bits))</summary>")
    out.append("    public static readonly (string Statement, (uint X, uint Y)[] Frames)[] Shocks =\n    [")
    out += lines
    out.append("    ];\n")
    lines = []
    for stmt in ["_MSQK(300,5,5);", "_MSQK(600,8,3);"]:
        mq = Q.MessageQuake()
        rng = Q.Ps4Rand(7)
        a = args_of(stmt)
        mq.start(a)
        fr = []
        for f in range(24):
            mq.update(rng)
            fr.append((fbits(mq.x), fbits(mq.y)))
        lines.append(f"        ({cs(stmt)}, [" + ", ".join(f"({x}, {y})" for x, y in fr) + "]),")
    out.append("    /// <summary>(MSQK statement, 24 frames of (x bits, y bits)) with rand state 7</summary>")
    out.append("    public static readonly (string Statement, (uint X, uint Y)[] Frames)[] Msqk =\n    [")
    out += lines
    out.append("    ];\n")


def re_args(stmt: str) -> list[Any]:
    body = stmt[stmt.index("(") + 1:stmt.rindex(")")]
    return [None if t.strip() == "" else int(t) for t in body.split(",")]


# -- D. scene model, commit ----------------------------------------------------------------------------------------------------------------------
def gen_statement(rr: random.Random) -> str:
    def l() -> int:
        return rr.choice([0, 1, 2, 3, 4, 5, 6])

    def opt(v: Any, p: float = 0.8) -> str:
        return "" if rr.random() > p else str(v)

    pic = lambda: rr.choice(NAMES + ["IMG9999"])  # noqa: E731
    kind = rr.choices(["STCH", "SCH2", "STCC", "STCP", "STBR", "STEC", "SNX", "STZ4", "STRT", "STRR", "STRA", "STPV", "SOXY", "STGS", "SPMA", "STNS", "STTR",
                       "SACL", "SBAS", "STCL", "SQK2", "SHK2", "SSW2", "STOP"],
                      weights=[16, 8, 4, 5, 4, 4, 3, 5, 5, 2, 2, 4, 2, 5, 1, 2, 2, 2, 2, 3, 3, 2, 2, 3])[0]
    if kind == "STCH":
        crop = [opt(rr.randint(-300, 600), 0.2) for _ in range(4)]
        if not any(crop):
            crop = ["", "", "", ""]
        a9, a10 = (str(rr.choice([0, 1, 2, 11])), str(rr.choice([0, 1, 2, 11]))) if rr.random() < 0.25 else ("", "")
        args = [str(l()), pic() if rr.random() > 0.05 else "", opt(rr.randint(-100, 600)), opt(rr.randint(-50, 350)), opt(rr.choice([500, 900, 1000, 1200])),
                opt(rr.randint(-20, 300)), opt(rr.choice([0, 5, 10, 100, 2178])), "", "CC", a9, a10, opt(rr.choice([2, 3, 4]), 0.2)] + crop \
            + [opt(rr.randint(0, 120), 0.1), opt(rr.randint(0, 7), 0.1), opt(rr.choice([0, 1, 2, 3, 4, 5, 6]), 0.25), opt(rr.randint(-30000, 30000), 0.05)]
        while args and args[-1] == "":
            args.pop()
        return "_STCH(" + ",".join(args) + ");"
    if kind == "SCH2":
        args = [str(l()), pic(), "", "", "", "", "", "", "CC", "", "", "", "", "", "", "", "", "", opt(rr.choice([0, 1, 2, 3, 4, 5, 6]), 0.4)]
        while args and args[-1] == "":
            args.pop()
        return "_SCH2(" + ",".join(args) + ");"
    if kind == "STCC":
        return f"_STCC({rr.choice([l(), l(), -1])});"
    if kind == "STCP":
        return f"_STCP({l()},{rr.choice([0, 3, 50, 700, 2178, 9250])});"
    if kind == "STBR":
        return f"_STBR({l()},{rr.choice([0, 1, 5, 11, 17, 25])},,{opt(1, 0.3)});"
    if kind == "STEC":
        return f"_STEC({l()},1,0x{rr.randint(0, 0xFFFFFF):06X});" if rr.random() < 0.4 else f"_STEC({l()},{rr.choice([0, 2, 3, 23, 76, 81, 82, -1])});"
    if kind == "SNX":
        return f"_{rr.choice(['SNX2', 'SNX3'])}({l()},{rr.randint(-255, 255)});"
    if kind == "STZ4":
        return "_STZ4({},{},{},{},{},{},{},{});".format(l(), opt(rr.randint(-1500, 2500)), opt(rr.randint(-1500, 2500)), opt(rr.randint(-1500, 2500)),
                                                        opt(rr.randint(-1500, 2500)), opt(rr.choice([0, 100, 500, 1000, 2000])), opt(rr.choice([0, 3, 35])), opt(rr.choice([0, 1])))
    if kind == "STRT":
        return "_STRT({},{},{},{},{});".format(l(), opt(rr.randint(-20000, 20000)), opt(rr.randint(-20000, 20000)), opt(rr.choice([0, 100, 500, 1000])), opt(rr.choice([0, 2, 35])))
    if kind == "STRR":
        return f"_STRR({l()},{opt(rr.randint(-40000, 40000))});"
    if kind == "STRA":
        return f"_STRA({l()},{rr.choice([0, 1, 3])},{rr.choice([0, 500])});"
    if kind == "STPV":
        return "_STPV({},{},{},{});".format(l(), opt(rr.randint(-100, 300)), opt(rr.randint(-100, 300)), opt(rr.randint(-9000, 9000), 0.3))
    if kind == "SOXY":
        return f"_SOXY({l()},{rr.randint(-100, 100)},{rr.randint(-100, 100)});"
    if kind == "STGS":
        return "_STGS({},{},{},{},{});".format(l(), opt(rr.randint(0, 300)), opt(rr.randint(0, 300)), opt(rr.choice([0, 200, 500])), opt(rr.choice([0, 3, 35])))
    if kind == "SPMA":
        return f"_SPMA({rr.choice([0, 1])});"
    if kind == "STNS":
        return f"_STNS({l()},{rr.choice([0, 1, 2, 3, 5, 12, 13, 40])});"
    if kind == "STTR":
        return f"_STTR({l()},{rr.choice([-1, 0, 1, 5, 26])},{opt(rr.choice([0, 1, 2]), 0.5)});"
    if kind == "SACL":
        return f"_SACL({l()},{rr.choice([0, 1])});"
    if kind == "SBAS":
        return f"_SBAS({l()},{rr.choice([-1, 2, 3, 4, 5, 6])});"
    if kind == "STCL":
        return "_STCL(0,{},{},{},{},{},{},{});".format(l(), opt(rr.randint(-100, 600)), opt(rr.randint(-50, 350)), opt(rr.randint(-100, 600)), opt(rr.randint(-50, 350)),
                                                      opt(rr.choice([0, 300, 900])), opt(rr.choice([0, 3, 6]), 0.5))
    if kind == "SQK2":
        return "_SQK2({},{},{},{},{},{},{},{},{});".format(l(), opt(rr.randint(1, 15)), opt(rr.randint(1, 15)), opt(rr.choice([-1, 0, 30, 100])), opt(rr.choice([-1, 200, 1000])),
                                                          opt(rr.choice([0, 100]), 0.3), opt(rr.choice([0, 1]), 0.3), opt(rr.choice([0, 500]), 0.3), "")
    if kind == "SHK2":
        return "_SHK2({},{},{},{},{},{},{},{},{});".format(l(), opt(rr.randint(0, 15)), opt(rr.randint(0, 15)), opt(rr.choice([100, 300])), opt(rr.choice([-1, 600])),
                                                          opt(rr.choice([0, 100]), 0.3), opt(rr.choice([0, 1]), 0.3), opt(rr.choice([0, 200]), 0.3), opt(rr.choice([1, 2]), 0.3))
    if kind == "SSW2":
        return "_SSW2({},{},{},{},{},{},{},{});".format(l(), opt(rr.randint(1, 15)), opt(rr.randint(1, 15)), opt(rr.choice([300, 1000])), opt(rr.choice([800, 2000])),
                                                       opt(rr.choice([1, 2]), 0.3), opt(rr.choice([-1, 1500])), opt(rr.choice([0, 200]), 0.3))
    return f"_{rr.choice(['SQS2', 'SHS2', 'SSS2'])}({rr.choice([l(), -1])});"


def gen_scene(out: list[str]) -> None:
    rr = random.Random(SEED + 77)
    model = L.SceneModel(CAT, NAMES)
    prev = bytes(model.s.b)
    cases = []
    committed = None
    for i in range(260):
        stmt = gen_statement(rr)
        if i % 9 == 8:
            stmt = "_FADS(1,0,0);"
        if stmt.startswith("_FADS"):
            expect_b = [canon_commit(L.commit(model.s, l)) for l in range(8)]
            cases.append((stmt, [], expect_b))
            continue
        model.step(stmt)
        cur = bytes(model.s.b)
        patch = []
        j = 0
        n = len(cur)
        while j < n:
            if prev[j] != cur[j]:
                k = j
                while k < n and (prev[k] != cur[k] or (k + 1 < n and prev[k + 1] != cur[k + 1])):
                    k += 1
                patch.append((j, cur[j:k].hex()))
                j = k
            else:
                j += 1
        prev = cur
        cases.append((stmt, patch, []))
    out.append("    /// <summary>(statement, scene-state patches [offset, hex] the oracle's SceneModel writes, committed records of layers 0..7 when the statement is _FADS)</summary>")
    out.append("    public static readonly (string Statement, (int Offset, string Hex)[] Patch, string[] Committed)[] Scene =\n    [")
    for stmt, patch, b in cases:
        p = ", ".join(f"({o}, {cs(h)})" for o, h in patch)
        bb = ", ".join(cs(x) for x in b)
        out.append(f"        ({cs(stmt)}, [{p}], [{bb}]),")
    out.append("    ];\n")


# -- E. motions ------------------------------------------------------------------------------------------------------------------------------------
def make_table(rr: random.Random, nmot: int) -> tuple[L.MotionTable, list[dict]]:
    T = L.MotionTable.__new__(L.MotionTable)
    T.times = []
    T.vals = []
    T.motions = {}
    flags = [0]
    info = []
    UN, HO = L.UNSET, L.HOLD
    for mid in range(1, nmot + 1):
        nk = rr.randint(2, 6)
        start = len(T.times)
        t = 0
        mask = 0
        for fld, m in L.FIELD_MASKS.items():
            if rr.random() < 0.28:
                mask |= m
        if rr.random() < 0.3:
            mask |= L.ANGLE_BIT
        flags.append(mask)
        for k in range(nk):
            t = 0 if k == 0 and rr.random() < 0.8 else t + rr.choice([100, 250, 400, 800])
            row = [UN] * 27
            row[0] = rr.choice([0, 0, 3, 7, 35, 10, 27, 12, 17])           # field 1: easing
            row[1] = rr.choice([0, 0, 0, 1, 2])                            # field 2: interpolation
            for f in range(3, 28):
                if rr.random() < 0.6:
                    if f == 8:
                        row[f - 1] = rr.choice([-100001, -100000, 0, 1500, -4500, 9000])
                    elif f in (3, 4):
                        row[f - 1] = rr.choice([UN, HO, 0, 100, 240, 480, -60, 333])
                    elif f == 5:
                        row[f - 1] = rr.choice([UN, HO, 0, 64, 128, 255, 300])
                    elif f in (6, 7):
                        row[f - 1] = rr.choice([0, 50, 120, -30])
                    elif f in (9, 10):
                        row[f - 1] = rr.choice([UN, HO, 1000, 1500, 733, -1000, 2000])
                    elif f in (22, 23):
                        row[f - 1] = rr.choice([UN, 0, 1, 2, 11])
                    elif f in (19, 20, 21):
                        row[f - 1] = rr.choice([0, 80, 200, 255])
                    elif f == 18:
                        row[f - 1] = rr.choice([0, 1])
                    elif f == 17:
                        row[f - 1] = rr.choice([0, 5, 11, 17])
                    elif f == 26:
                        row[f - 1] = rr.choice([0, 1, 5])
                    elif f == 27:
                        row[f - 1] = rr.choice([0, 20, 700])
                    else:
                        row[f - 1] = rr.choice([UN, HO, 0, 7, 40, -5, 90])
            if rr.random() < 0.15:
                row[0] = HO
            T.times.append(t)
            T.vals.append(tuple(row))
        T.motions[mid] = L.Motion(mid, t + rr.choice([0, 100, 500]), start, len(T.times))
        info.append({"id": mid, "dur": T.motions[mid].duration, "start": start, "end": len(T.times)})
    T._flags = flags
    return T, info


def gen_motions(out: list[str]) -> None:
    rr = random.Random(SEED + 5)
    T, info = make_table(rr, 14)
    loops = {mid: rr.choice([0, 0, 1, 200, 300]) for mid in range(1, 15)}
    # table arrays
    out.append("    /// <summary>Synthetic motion table: per motion (duration, key end, field mask, loop point)</summary>")
    out.append("    public static readonly (long Duration, int End, ulong Mask, int Loop)[] MotionHeader =\n    [")
    for m in info:
        out.append(f"        ({m['dur']}L, {m['end']}, 0x{T._flags[m['id']]:X}UL, {loops[m['id']]}),")
    out.append("    ];\n")
    out.append("    /// <summary>Synthetic motion table: per key (time, 27 fields)</summary>")
    out.append("    public static readonly (long Time, long[] Fields)[] MotionKeys =\n    [")
    for i in range(len(T.times)):
        out.append(f"        ({T.times[i]}L, [" + ", ".join(str(v) for v in T.vals[i]) + "]),")
    out.append("    ];\n")
    # direct motion_value queries
    qs = []
    for mid in range(1, 15):
        m = T.motions[mid]
        for _ in range(12):
            f = rr.choice([3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15, 16, 17, 19, 22, 24, 26, 27])
            t = rr.choice([0.0, 50.0, 133.5, 250.0, 400.0, 777.0, float(m.duration), float(m.duration) + 50.0]) if rr.random() < 0.9 else rr.random() * m.duration
            t = float(Q.f32(t))
            k = next((i for i in range(m.start, m.end) if float(T.val(i, 0)) >= t), None)
            if k is None:
                continue
            cur = float(Q.f32(rr.choice([0.0, 12.5, -40.0, 255.0])))
            v = L.motion_value(T, f, m.start, m.end - 1, k, t, cur, rr.random() < 0.1 and False)
            qs.append((mid, f, k, t, cur, v))
    out.append("    /// <summary>(motion id, field, key k, t, cur, bits of motion_value)</summary>")
    out.append("    public static readonly (int Motion, int Field, int K, double T, double Cur, ulong Bits)[] MotionValues =\n    [")
    for (mid, f, k, t, cur, v) in qs:
        out.append(f"        ({mid}, {f}, {k}, {t!r}, {cur!r}, {dbits(v)}),")
    out.append("    ];\n")
    # apply_motion runs: STMA at t = 0 then frames
    pn = L.PictureNames(NAMES)
    runs = []
    loop_of = lambda mid: loops[mid]  # noqa: E731
    for run in range(30):
        mid = rr.randint(1, 14)
        div = rr.choice([0, 0, 2, 3])
        pic = rr.choice([2, 3, 4, 6, 7, 8, 11])
        scene = L.SceneModel(CAT, NAMES).s
        scene.set("pic", 3, pic)
        for nm, v in (("anchor_x", CAT[pic].anchor_x), ("anchor_y", CAT[pic].anchor_y)):
            scene.set(nm, 3, v)
        rec = L.steady_record({"pic": pic, "x": 100.0, "y": 80.0, "alpha": 1.0, "anchor_x": CAT[pic].anchor_x, "anchor_y": CAT[pic].anchor_y,
                               "off_x": float(-CAT[pic].anchor_x), "off_y": float(-CAT[pic].anchor_y), "zoom_x": 1000, "zoom_y": 1000, "scale": 1000})
        rec["motion"] = L.motion_record(rec["motion"], mid, div)
        scene.set("motion", 3, L.motion_record(scene.get("motion", 3), mid, div))
        steps = []
        L.apply_motion(T, rec, 3, 0, CAT, scene, loops=loop_of(mid), names=pn)
        steps.append((0, canon_rec(rec)))
        for _ in range(10):
            dt = rr.choice([1, 1, 2, 3, 10, 30])
            rec["tw_zoom"] = (rec["tw_zoom"][0], rec["tw_zoom"][1], rec["tw_zoom"][2])
            L.apply_motion(T, rec, 3, dt, CAT, scene, loops=loop_of(mid), names=pn)
            steps.append((dt, canon_rec(rec)))
        runs.append((mid, div, pic, steps, bytes(scene.b).hex()[:0]))
    out.append("    /// <summary>(motion id, divisor, picture, steps of (dt, canonical record after apply_motion)); every run starts with the STMA record change and dt = 0</summary>")
    out.append("    public static readonly (int Motion, int Divisor, int Pic, (int Dt, string Record)[] Steps)[] MotionRuns =\n    [")
    for (mid, div, pic, steps, _) in runs:
        out.append(f"        ({mid}, {div}, {pic}, [" + ", ".join(f"({d}, {cs(c)})" for d, c in steps) + "]),")
    out.append("    ];\n")


# -- F. projection ------------------------------------------------------------------------------------------------------------------------------------
def gen_projection(out: list[str]) -> None:
    rr = random.Random(SEED + 9)
    cases = []
    for c in range(40):
        recs = []
        specs = []
        for l in range(4):
            pic = rr.choice([0, 2, 3, 4, 5, 6])
            spec = {"used": 1, "pic": pic, "x": float(rr.randint(-100, 700)), "y": float(rr.randint(-50, 400)),
                    "anchor_x": CAT[pic].anchor_x if rr.random() < 0.7 else rr.randint(-50, 500), "anchor_y": CAT[pic].anchor_y if rr.random() < 0.7 else rr.randint(-50, 400),
                    "rot": rr.choice([0, 0, 1500, -4500, 9000, 18000]), "scale": rr.choice([1000, 700, 1300]), "zoom_x": rr.choice([1000, 1000, -1000, 1500, 733]),
                    "zoom_y": rr.choice([1000, 1000, -1000, 1500, 600]), "zoom_pivot": rr.choice([0, 0, 0, 1]),
                    "parent": rr.choice([-1, -1, -1, 0, 1]) if l > 0 else -1,
                    "soxy_x": rr.choice([0, 0, 5, -7]), "soxy_y": rr.choice([0, 0, 3, -9]),
                    "rot_cur": rr.choice([0, 0, -300]), "scale_cur": rr.choice([0.0, 0.0, 0.125])}
            spec["parent"] = min(spec["parent"], l - 1) if spec["parent"] >= l else spec["parent"]
            spec["off_x"] = float(-spec["anchor_x"]) + rr.choice([0.0, 0.0, -0.4, 0.6])
            spec["off_y"] = float(-spec["anchor_y"]) + rr.choice([0.0, 0.0, 0.25, -0.7])
            if rr.random() < 0.3:
                spec["tw_zoom"] = (3, rr.choice([1500, 4096, 0]), -100)
                spec["zoom_dx"] = float(Q.f32(rr.choice([0.4, -0.8])))
                spec["zoom_dy"] = float(Q.f32(rr.choice([0.2, -0.3])))
            if rr.random() < 0.3:
                spec["tw_move"] = (2, rr.choice([2000, 4096, 0]), -100)
                spec["move_dx"] = float(rr.randint(-50, 50))
                spec["move_dy"] = float(rr.randint(-50, 50))
            for k_, v_ in list(spec.items()):
                if isinstance(v_, float):
                    spec[k_] = float(Q.f32(v_))      # the records hold float32 values
            specs.append(spec)
            recs.append(L.steady_record(spec))
        l = rr.randint(0, 3)
        extra = (float(Q.f32(rr.choice([0.0, 1.25, -3.5]))), float(Q.f32(rr.choice([0.0, 0.75, 2.0]))))
        m = L.layer_matrix(recs, l, CAT, extra)
        fm = L.frame_matrix(recs, l, CAT, extra=extra)
        cases.append((specs, l, extra, m, fm))

    def spec_text(sp: dict) -> str:
        parts = []
        for k, v in sp.items():
            if isinstance(v, tuple):
                parts.append(f"{k}=" + ",".join(map(str, v)))
            elif isinstance(v, float):
                parts.append(f"{k}={fbits(v)}")
            else:
                parts.append(f"{k}={v}")
        return " ".join(parts)

    out.append("    /// <summary>(record specs of layers 0..3, layer, extra x bits, extra y bits, bits of the six layer_matrix numbers a b c d e f, bits of the six frame_matrix numbers)</summary>")
    out.append("    public static readonly (string[] Specs, int Layer, uint ExtraX, uint ExtraY, ulong[] Matrix, ulong[] Frame)[] Projection =\n    [")
    for specs, l, extra, m, fm in cases:
        st = ", ".join(cs(spec_text(s)) for s in specs)
        out.append(f"        ([{st}], {l}, {fbits(extra[0])}, {fbits(extra[1])}, [" + ", ".join(dbits(v) for v in (m.a, m.b, m.c, m.d, m.e, m.f)) + "], ["
                   + ", ".join(dbits(v) for v in (fm.a, fm.b, fm.c, fm.d, fm.e, fm.f)) + "]),")
    out.append("    ];\n")


def main(path: str) -> None:
    out: list[str] = []
    out.append("// <auto-generated>")
    out.append("// Written by tests/Tsuki.Tests.Synthetic/Stage/gen_stage_cases.py from tsuki.misc_layer_state_and_motions and tsuki.quake;")
    out.append(f"// seed {SEED}. Synthetic inputs only, no game data. Do not edit: regenerate.")
    out.append("// </auto-generated>")
    out.append("namespace Tsuki.Tests.Synthetic.Stage;\n")
    out.append("internal static class StageOracleCases\n{")
    out.append("    /// <summary>The synthetic picture catalog: (name, anchor x, anchor y, size class, draw flags, has face)</summary>")
    out.append("    public static readonly (string Name, int Ax, int Ay, int Class, int Flags, bool Face)[] Pictures =\n    [")
    for (n, a, b, c, d, f) in PICS:
        out.append(f"        ({cs(n)}, {a}, {b}, {c}, {d}, {'true' if f else 'false'}),")
    out.append("    ];\n")
    gen_ease(out)
    gen_rand(out)
    gen_shakes(out)
    gen_scene(out)
    gen_motions(out)
    gen_projection(out)
    out.append("}")
    Path(path).write_text("\n".join(out) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {path}: {len(out)} lines")


if __name__ == "__main__":
    main(sys.argv[1])
