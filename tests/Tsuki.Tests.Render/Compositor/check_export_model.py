r"""D1 measurement (compositing.md 2.6): the fp16 round-toward-zero export model against the tsuki.gcn ISA
export, on the inputs of tools/tests/test_compose.py (35 PS x 4 parameter vectors x 4,096 lanes), and a few
sanity values of compose.rtz16 / export_mrt0. Needs the game folder (eboot.bin).

  $env:PYTHONPATH = "D:\TsukihimePort\tools"
  .venv\Scripts\python.exe tests\Tsuki.Tests.Render\Compositor\check_export_model.py
"""
import numpy as np
from tsuki import compose as cp

x = np.array([0.5, 0.49999, 1 / 3, 2 / 3, 0.1, 1.0, 0.0, 1.5, -0.2, np.nan, np.inf, 70000.0], np.float32)
print("rtz16      ", cp.rtz16(x))
print("export_mrt0", cp.export_mrt0(x))
v = (np.arange(255, dtype=np.float32) + 0.5) / np.float32(255)
v4 = np.repeat(v[:, None], 4, 1)
print("ties k+0.5 levels: to_u8 rounds up", int((cp.to_u8(v4)[:, 0] > np.arange(255)).sum()),
      "of 255; with export_mrt0", int((cp.to_u8(cp.export_mrt0(v4))[:, 0] > np.arange(255)).sum()))

# export model vs ISA export (the interpreter's v_cvt_pkrtz_f16_f32), on the test_compose inputs
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools" / "tests"))
import test_compose as tc  # noqa: E402

raws = tc.load_ps_blobs()
rng = np.random.default_rng(1)
n = 4096
C = rng.random((n, 4), dtype=np.float32)
tcd = rng.random((n, 2), dtype=np.float32)
tr = rng.random((n, 2), dtype=np.float32)
tex = {name: tc.proc_texture(i) for i, name in enumerate(("texture", "dst_texture", "transition_tex", "y_texture", "cbcr_texture"))}
S = tex["texture"](tcd[:, 0], tcd[:, 1]); D = tex["dst_texture"](tr[:, 0], tr[:, 1]); M = tex["transition_tex"](tr[:, 0], tr[:, 1])
Y = tex["y_texture"](tcd[:, 0], tcd[:, 1]); U = tex["cbcr_texture"](tcd[:, 0], tcd[:, 1])
Q = (0.08, -0.12, 0.7, 0.0)
tot = exact_f32 = exact_rtz = 0
worst_rtz = 0.0
for idx, (raw, names) in sorted(raws.items()):
    for P in ((0.25, 0.75, 0.0, 0.0), (0.0, 1.0, 1.0, 0.0), (-0.4, 0.6, 1.0, 0.3), (0.1, 0.9, 0.0, 1.0)):
        isa = cp.isa_reference(raw, names, tex, {"m_color": C, "m_texcoord": tcd, "m_transition": tr},
                               {"CommonFragmentParamsConstants": P, "TransitionParamsConstants": P, "SepiaConstants": Q})
        with np.errstate(all="ignore"):
            ref = cp.ps_reference(idx, S, D, C, M, Y, U, P, Q)
        fin = np.isfinite(isa) & np.isfinite(ref)
        a = isa[fin]; r = ref[fin]
        tot += a.size
        exact_f32 += int((a == r).sum())
        rr = cp.rtz16(r)
        exact_rtz += int((a == rr).sum())
        worst_rtz = max(worst_rtz, float(np.max(np.abs(a - rr) / np.maximum(1, np.abs(rr)))) if a.size else 0.0)
print(f"ISA export vs formula, {len(raws)} PS x 4 params, {tot} finite channels: bit-exact f32 {exact_f32 / tot:.4%}, "
      f"bit-exact after rtz16 {exact_rtz / tot:.4%}; worst rel. error after rtz16 {worst_rtz:.3g}")
