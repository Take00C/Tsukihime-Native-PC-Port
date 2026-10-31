"""Caller of the engine under emulation for package K2 (the stage): random layer-statement scenarios.

The ground truth of the layer model is the engine's own code (``tsuki.misc_layer_state_and_motions_emu.Rig``: unicorn running
eboot.bin, with the PS4 ``rand()`` and the libc ``sinf`` installed as ``scratch/quake/verify_emu.py`` does).  This script generates a
reproducible scenario from a seed (layer statements and per-frame updates), runs it through the engine and prints

* line 1: ``{"seed": ..., "steps": [["s", "<statement>"] | ["u", dt] | ["d", 0], ...]}`` - the scenario (``d`` = draw the scene; the step's line has
  ``draw``, the layers the engine drew in order),
* then one JSON line per step with the effect of the step: for a statement the changed bytes of the 0x1A08-byte scene state
  (``scene``: list of ``[offset, hex]``), for every step the layers whose drawn record A (``A``) or committed record B (``B``) changed,
  as canonical field strings, and ``Z`` (the committed draw-order slots) / ``rand`` (the rand() state) when they changed,
* a last line ``{"done": true}``.

The canonical strings are written by ``canon`` below; ``tests/Tsuki.Tests.Data/Stage/StageTrace.cs`` writes the same strings from the C#
records.  Floats are written as the hex of their float32 bits.  ``tests/Tsuki.Tests.Data/Stage/StageEngineTests.cs`` runs the C# stage on
the scenario and compares every line.

    python -I tests/Tsuki.Tests.Data/Stage/engine_stage.py <seed> <statements> [--layers N]
    python -I tests/Tsuki.Tests.Data/Stage/engine_stage.py --scenario <file.json> [--layers N]

``--scenario`` runs the steps of a JSON file (``{"steps": [["s", "<statement>"] | ["u", dt], ...]}``, written by the golden-slice test from the
statements the traced paths of the slice execute) instead of a random one, and adds ``M`` to every step in which a drawn record changed: for every
used layer of the tracked ones whose frame matrix changed, the six numbers of ``frame_matrix`` (the oracle's layer projection, float64 bits as hex) computed
from the engine's records and the engine's own image catalog.
"""
from __future__ import annotations

import json
import random
import struct
import sys
import types
from pathlib import Path
from typing import Any, Optional

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from tsuki import game_dir  # noqa: E402
from tsuki.misc_layer_state_and_motions import RenderRecord, frame_matrix  # noqa: E402
from tsuki.misc_layer_state_and_motions_emu import ARR_A, ARR_B, LAYER_SIZE, SCENE, SCENE_LEN, Rig  # noqa: E402
from tsuki.quake import ps4_sinf  # noqa: E402

LAYERS = list(range(12))
PICS = ["img0597", "img1696", "img2183", "img1688", "BLACK", "WHITE", "img0143", "img2345", "img0008"]
FACE_PICS = ["boy_a_00_00_n01", "ARU_L_90_00_N02"]


def fb(x: float) -> str:
    if x != x:
        return "nan"
    return "%08x" % struct.unpack("<I", struct.pack("<f", x))[0]


def canon(E: Any, base: int, layer: int, committed: bool) -> str:
    """The canonical string of one render record (A or B) of the engine."""
    a = base + LAYER_SIZE * layer
    raw = E.rd(a, 0x2D8)
    f = RenderRecord.from_bytes(raw).f
    m = raw[0x160:0x178]
    mid, mdiv = struct.unpack_from("<hh", m, 0)
    mfr = struct.unpack_from("<q", m, 8)[0]
    parts = [f"used:{f['used']}", f"face:{f['face']}", f"pic:{f['pic']}", f"blend:{f['blend']}", f"flags:{f['flags']}",
             f"fx:{f['fx_mode']},{f['fx_a']},{f['fx_b']}", f"state:{f['state']}", f"alpha:{fb(f['alpha'])}",
             f"xy:{fb(f['x'])},{fb(f['y'])}", f"off:{fb(f['off_x'])},{fb(f['off_y'])}", f"rot:{f['rot']}", f"scale:{f['scale']}",
             f"anchor:{f['anchor_x']},{f['anchor_y']}", f"zoom:{f['zoom_x']},{f['zoom_y']},{f['zoom_pivot']}",
             f"mask:{f['mask']}", f"parent:{f['parent']}", f"sttr:{f['sttr']}", f"soxy:{f['soxy_x']},{f['soxy_y']}",
             f"crop:{','.join(map(str, f['crop']))}", f"a:{f['a16']},{f['a17']},{f['angle']}", f"stns:{f['stns']},{f['stns_b']}",
             f"motion:{mid},{mdiv},{mfr},{m[16]}", f"colour:{f['colour'].hex() if isinstance(f['colour'], bytes) else f['colour']}"]
    if not committed:
        parts += [f"rotd:{f['rot_delta']},{f['rot_cur']}", f"tw:{';'.join(','.join(map(str, f[k])) for k in ('tw_move', 'tw_scale', 'tw_rot', 'tw_alpha', 'tw_zoom'))}",
                  f"cur:{fb(f['alpha_cur'])},{fb(f['scale_delta'])},{fb(f['scale_cur'])}",
                  f"zd:{fb(f['zoom_dx'])},{fb(f['zoom_dy'])}", f"md:{fb(f['move_dx'])},{fb(f['move_dy'])}", f"ad:{fb(f['alpha_delta'])}",
                  "stra:" + ",".join(str(struct.unpack_from('<h', raw, o)[0]) for o in (0x2C8, 0x2CA, 0x2CC, 0x2CE, 0x2D0))]
    for s in (0, 1):
        k = raw[0x178 + s]
        dl, tm = struct.unpack_from("<hh", raw, 0x17A + 2 * s)[0], struct.unpack_from("<h", raw, 0x17E + 2 * s)[0]
        par = struct.unpack_from("<6h", raw, 0x182 + 12 * s)
        if committed:
            parts.append(f"slot{s}:{k},{dl},{tm},{','.join(map(str, par))}")
        else:
            el, ph = struct.unpack_from("<h", raw, 0x19A + 2 * s)[0], struct.unpack_from("<h", raw, 0x19E + 2 * s)[0]
            x, y = struct.unpack_from("<f", raw, 0x1A4 + 4 * s)[0], struct.unpack_from("<f", raw, 0x1AC + 4 * s)[0]
            w = struct.unpack_from("<7h", raw, 0x1B4 + 14 * s)
            parts.append(f"slot{s}:{k},{dl},{tm},{','.join(map(str, par))},{el},{ph},{fb(x)},{fb(y)},{','.join(map(str, w))}")
    return " ".join(parts)


def scene_patch(prev: bytes, cur: bytes) -> list[list[Any]]:
    out: list[list[Any]] = []
    i = 0
    n = len(cur)
    while i < n:
        if prev[i] != cur[i]:
            j = i
            while j < n and (prev[j] != cur[j] or (j + 1 < n and prev[j + 1] != cur[j + 1])):
                j += 1
            out.append([i, cur[i:j].hex()])
            i = j
        else:
            i += 1
    return out


def gen_statement(rr: random.Random, motions: list[int], variants: list[str]) -> str:
    def l() -> int:
        return rr.choice(LAYERS[2:])

    def opt(v: Any, p: float = 0.8) -> str:
        return "" if rr.random() > p else str(v)

    pic = lambda: rr.choice(PICS + variants)  # noqa: E731
    kind = rr.choices(
        ["STCH", "SCH2", "STCC", "STCP", "STBR", "STEC", "SNX", "STZ4", "STRT", "STRR", "STRA", "STPV", "SOXY", "STGS", "STMA", "SPMA",
         "STNS", "STTR", "SACL", "SBAS", "STCL", "SQK2", "SHK2", "SSW2", "STOP", "SGMA", "SHOK", "SHAF", "MSQK"],
        weights=[14, 7, 4, 5, 4, 4, 3, 6, 5, 2, 2, 3, 2, 6, 6, 1, 2, 2, 2, 2, 3, 4, 2, 2, 3, 2, 2, 1, 2])[0]
    if kind == "STCH":
        n = rr.choice([None, None, 9, 10, 11, 12])
        a9, a10 = (str(rr.choice([0, 1, 2, 11])), str(rr.choice([0, 1, 2, 11]))) if rr.random() < 0.25 else ("", "")
        crop = [opt(rr.randint(-300, 600), 0.2), opt(rr.randint(-300, 600), 0.2), opt(rr.randint(1, 900), 0.2), opt(rr.randint(1, 900), 0.2)]
        if not any(crop):
            crop = ["", "", "", ""]
        mask = opt(rr.choice([2, 3, 4, 5, 6, 7, 8]), 0.2)
        src = opt(rr.choice(LAYERS), 0.15)
        pics = pic() if rr.random() > 0.05 else ""
        args = [str(l()), pics, opt(rr.randint(-100, 600)), opt(rr.randint(-50, 350)), opt(rr.choice([500, 900, 1000, 1200])),
                opt(rr.randint(0, 300)), opt(rr.choice([0, 5, 10, 100, 500, 2178, 9250])), "", "CC", a9, a10, mask] + crop \
            + [opt(rr.randint(0, 120), 0.1), opt(rr.randint(0, 7), 0.1), src, opt(rr.randint(-30000, 30000), 0.05)]
        while args and args[-1] == "":
            args.pop()
        return "_STCH(" + ",".join(args) + ");"
    if kind == "SCH2":
        src = opt(rr.choice(LAYERS), 0.4)
        args = [str(l()), pic(), "", "", "", "", "", "", "CC", "", "", "", "", "", "", "", "", "", src]
        while args and args[-1] == "":
            args.pop()
        return "_SCH2(" + ",".join(args) + ");"
    if kind == "STCC":
        return f"_STCC({rr.choice([l(), l(), -1])});"
    if kind == "STCP":
        return f"_STCP({l()},{rr.choice([0, 3, 50, 700, 2178, 9250])});"
    if kind == "STBR":
        return f"_STBR({l()},{rr.choice([0, 1, 5, 7, 11, 17, 23, 25, 28])},,{opt(1, 0.3)});"
    if kind == "STEC":
        if rr.random() < 0.4:
            return f"_STEC({l()},1,0x{rr.randint(0, 0xFFFFFF):06X});"
        return f"_STEC({l()},{rr.choice([0, 2, 3, 23, 44, 76, 81, 82, -1])});"
    if kind == "SNX":
        return f"_{rr.choice(['SNX2', 'SNX3'])}({l()},{rr.randint(-255, 255)});"
    if kind == "STZ4":
        return "_STZ4({},{},{},{},{},{},{},{});".format(l(), opt(rr.randint(-1500, 2500)), opt(rr.randint(-1500, 2500)), opt(rr.randint(-1500, 2500)),
                                                        opt(rr.randint(-1500, 2500)), opt(rr.choice([0, 100, 500, 1000, 2000])), opt(rr.choice([0, 2, 3, 6, 10, 35, 41])), opt(rr.choice([0, 1])))
    if kind == "STRT":
        return "_STRT({},{},{},{},{});".format(l(), opt(rr.randint(-20000, 20000)), opt(rr.randint(-20000, 20000)), opt(rr.choice([0, 100, 500, 1000])),
                                              opt(rr.choice([0, 2, 3, 35])))
    if kind == "STRR":
        return f"_STRR({l()},{opt(rr.randint(-40000, 40000))});"
    if kind == "STRA":
        return f"_STRA({rr.choice([l(), l(), -1])},{rr.choice([0, 0, 1, 3, 5])},{rr.choice([0, 100, 500, 1500])});"
    if kind == "STPV":
        return "_STPV({},{},{},{});".format(l(), opt(rr.randint(-100, 300)), opt(rr.randint(-100, 300)), opt(rr.randint(-9000, 9000), 0.3))
    if kind == "SOXY":
        return f"_SOXY({l()},{rr.randint(-100, 100)},{rr.randint(-100, 100)});"
    if kind == "STGS":
        return "_STGS({},{},{},{},{});".format(l(), opt(rr.randint(0, 300)), opt(rr.randint(0, 300)), opt(rr.choice([0, 200, 500, 1000])), opt(rr.choice([0, 3, 35])))
    if kind == "STMA":
        return "_STMA({},{},{},{});".format(l(), rr.choice(motions), opt(rr.choice([0, 1, 2]), 0.5), opt(rr.choice([0, 2, 3]), 0.3))
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
        return "_SQK2({},{},{},{},{},{},{},{},{});".format(rr.choice([l(), l(), -1]), opt(rr.randint(1, 15)), opt(rr.randint(1, 15)), opt(rr.choice([-1, 0, 30, 100])),
                                                          opt(rr.choice([-1, 200, 600, 1000, 3000])), opt(rr.choice([0, 100, 400]), 0.3), opt(rr.choice([0, 1]), 0.3),
                                                          opt(rr.choice([0, 100, 500, 900]), 0.3), "")
    if kind == "SHK2":
        return "_SHK2({},{},{},{},{},{},{},{},{});".format(rr.choice([l(), l(), -1]), opt(rr.randint(0, 15)), opt(rr.randint(0, 15)), opt(rr.choice([100, 300, 500])),
                                                          opt(rr.choice([-1, 600, 2000])), opt(rr.choice([0, 100]), 0.3), opt(rr.choice([0, 1]), 0.3),
                                                          opt(rr.choice([0, 200]), 0.3), opt(rr.choice([1, 2, 3]), 0.3))
    if kind == "SSW2":
        return "_SSW2({},{},{},{},{},{},{},{});".format(l(), opt(rr.randint(1, 15)), opt(rr.randint(1, 15)), opt(rr.choice([300, 1000])), opt(rr.choice([800, 2000])),
                                                       opt(rr.choice([1, 2]), 0.3), opt(rr.choice([-1, 1500, 4000])), opt(rr.choice([0, 200]), 0.3))
    if kind == "STOP":
        return f"_{rr.choice(['SQS2', 'SHS2', 'SSS2'])}({rr.choice([l(), -1])});"
    if kind == "SGMA":
        return f"_SGMA({l()},{rr.choice(motions)},{rr.choice([0, 1])});"
    if kind == "SHOK":
        return f"_SHOK({rr.randint(0, 5)},{rr.randint(0, 5)},{rr.choice([100, 300])},{rr.choice([2, 3, 10])},{rr.choice([0, 0, 200])});"
    if kind == "SHAF":
        return "_SHAF();"
    return f"_MSQK({rr.choice([300, 600])},5,5);"


def scenario(seed: int, n: int, motions: list[int], variants: list[str]) -> list[list[Any]]:
    rr = random.Random(seed)
    steps: list[list[Any]] = []
    for _ in range(n):
        steps.append(["s", gen_statement(rr, motions, variants)])
        r = rr.random()
        if r < 0.22:
            steps.append(["s", rr.choice(["_FADS(1,0,0);", "_FADS(0,500,0);", "_FADS(2,300,5);", "_FADS(1,0,0);"])])
            steps.append(["u", rr.choice([1, 1, 2, 3])])
        if r < 0.7:
            for _ in range(rr.choice([1, 1, 2, 4, 8])):
                steps.append(["u", rr.choice([1, 1, 1, 2, 3])])
        if rr.random() < 0.35:
            steps.append(["d", 0])
    return steps


class EngineCatalog:
    """The engine's image table (data VA 0x671900, 0x48-byte records) as ``frame_matrix`` reads a catalog: anchor (+0x34, +0x38) and size class (+0x42)."""

    def __init__(self, E: Any) -> None:
        self.E = E

    def __len__(self) -> int:
        return 14775

    def __getitem__(self, i: int) -> Any:
        raw = self.E.rd(0x671900 + 0x48 * i, 0x48)
        ax, ay = struct.unpack_from("<II", raw, 0x34)
        return types.SimpleNamespace(anchor_x=ax, anchor_y=ay, scale_class=struct.unpack_from("<H", raw, 0x42)[0])


def matrix_bits(m: Any) -> list[str]:
    return ["%016x" % struct.unpack("<Q", struct.pack("<d", float(v)))[0] for v in (m.a, m.b, m.c, m.d, m.e, m.f)]


def main(argv: list[str]) -> int:
    args = list(argv)
    layers = 12
    if "--layers" in args:
        i = args.index("--layers")
        layers = int(args[i + 1])
        del args[i:i + 2]
    projection = False
    eboot = game_dir() / "eboot.bin"
    if args and args[0] == "--scenario":
        steps = json.loads(Path(args[1]).read_text(encoding="utf-8"))["steps"]
        seed = 0
        projection = True
    else:
        seed, n = int(args[0]), int(args[1])
        motions = [1, 2, 3, 4, 5, 10, 23, 24, 100, 200, 22523, 22524, 22526, 12000, 24000, 21128, 9999]
        variants = ["IMG0143_11_11", "IMG0143_01_02"]
        steps = scenario(seed, n, motions, variants)
    tracked = list(range(layers))
    print(json.dumps({"seed": seed, "steps": steps}), flush=True)

    R = Rig(eboot)
    E = R.E
    state = [1]
    M64 = (1 << 64) - 1

    def rnd() -> int:
        state[0] = (state[0] * 0x5851F42D4C957F2D + 1) & M64
        return (state[0] >> 32) & 0x3FFFFFFF

    E._imp_rand = rnd  # type: ignore[attr-defined]
    E._imp_sinf = lambda: E._fret(ps4_sinf(E.xmm_f(0)))  # type: ignore[attr-defined]
    R.reset()
    state[0] = 1
    prevA: dict[int, str] = {}
    prevB: dict[int, str] = {}
    prevZ: Optional[list[int]] = None
    prevScene = R.scene()
    prevRand = 1
    cat = EngineCatalog(E)
    prevRaw: Optional[list[bytes]] = None
    prevM: dict[int, list[str]] = {}
    # first the state after the base set-up, as reference for the diffs
    for k in tracked:
        prevA[k] = canon(E, ARR_A, k, False)
        prevB[k] = canon(E, ARR_B, k, True)
    print(json.dumps({"init": {"A": prevA, "B": prevB, "Z": [E.i32(0xDE2538 + 4 * k) for k in range(40)]}}), flush=True)
    prevZ = [E.i32(0xDE2538 + 4 * k) for k in range(40)]
    for i, (kind, arg) in enumerate(steps):
        out: dict[str, Any] = {"i": i}
        if kind == "s":
            R.F.step(arg)
        elif kind == "d":
            # the engine's scene draw (0x56DE0) with the picture entry points recorded: its side effects on the drawn records and the layers it draws, in order
            # a window layer is drawn twice (0x5A5E0: the layer, the layers masked by it, the layer again as alpha mask): the first appearance counts
            order: list[int] = []
            try:
                calls = R.F.draw()
            except Exception as exc:  # noqa: BLE001 - the emulation has no textures: a draw that follows a null texture pointer ends the run
                print(json.dumps({"halt": f"step {i}: the engine draw failed under emulation: {type(exc).__name__}: {exc}"}), flush=True)
                print(json.dumps({"done": True}), flush=True)
                return 0
            for call in calls:
                layer = call["slot"] - 0xE8
                if call["slot"] >= 0xE8 and layer not in order:
                    order.append(layer)
            # a layer with a running STRA haze is drawn by the haze pass (other entry points than the plain sprite call): left out
            out["draw"] = [k for k in order if struct.unpack_from("<h", E.rd(ARR_A + LAYER_SIZE * k + 0x2C8, 2))[0] == 0]
        else:
            R.F.update(arg)
        scene = R.scene()
        if scene != prevScene:
            out["scene"] = scene_patch(prevScene, scene)
            prevScene = scene
        dA = {}
        dB = {}
        for k in tracked:
            ca = canon(E, ARR_A, k, False)
            if ca != prevA[k]:
                dA[k] = ca
                prevA[k] = ca
            cb = canon(E, ARR_B, k, True)
            if cb != prevB[k]:
                dB[k] = cb
                prevB[k] = cb
        if dA:
            out["A"] = dA
        if dB:
            out["B"] = dB
        if projection:
            raws = [E.rd(ARR_A + LAYER_SIZE * k, 0x2D8) for k in range(40)]
            if raws != prevRaw:
                prevRaw = raws
                recs = [RenderRecord.from_bytes(r).f for r in raws]
                dM = {}
                for k in tracked:
                    if not recs[k]["used"] or recs[k]["pic"] < 0:
                        prevM.pop(k, None)
                        continue
                    try:
                        bits = matrix_bits(frame_matrix(recs, k, cat))
                    except Exception as exc:  # noqa: BLE001 - reported, the run goes on
                        print(f"frame_matrix({k}) failed at step {i}: {exc!r}", file=sys.stderr)
                        continue
                    if prevM.get(k) != bits:
                        prevM[k] = bits
                        dM[k] = bits
                if dM:
                    out["M"] = dM
        z = [E.i32(0xDE2538 + 4 * k) for k in range(40)]
        if z != prevZ:
            out["Z"] = z
            prevZ = z
        if state[0] != prevRand:
            out["rand"] = state[0]
            prevRand = state[0]
        print(json.dumps(out), flush=True)
    print(json.dumps({"done": True}), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
