"""Package K34: the click-wait icon as the PS4 HUD draws it, read back from the eboot under emulation.

Runs the real ADV HUD draw (process 0xF, VA 0x53CF0) frame after frame under unicorn for the message
`$000003` laid out into block 4 (474 x 220, leading 9, scale 0.74, as `_ZM` does) and records the icon
alpha 0xDF08E4, the stored position 0xDF088C / 0xDF0890, the tick counter 0x78122C, the TexDrawRect call
(0xEB890: UI slot, rect, x, y, picture scale 0xDC7BA4, colour alpha 0x1DBD0D8) and the projected pixel
quad (0xD2BE0 after the engine's executor 0xD9E10 ran the parent list).

Writes samples/golden_data/click_icon_oracle.json (local, git-ignored) for
tests/Tsuki.Tests.Data/Game/ClickIconOracleTests.cs. Scenarios:
  line_en / line_ja   F822 1, F799 0 (the line mark, UI slot 2), window 0, text complete, wait bit 0x400
  page_en / page_ja   F799 1 (the page mark, UI slot 3, half alpha)
  bubble              F1105 5 (the bubble mark, UI slot 4), English
  f822_0              F822 0: the page mark at x + 115, half alpha
  noclick             0x400 clear: alpha 0, nothing drawn
  partial             the head on a middle glyph: incomplete, alpha 0
  auto                mode word bit 0: the alpha rises, nothing is drawn
  line_zc             the line mark in Simplified Chinese (no -57, MirrorX 463; package K35)
  bubble_ja           the bubble mark in Japanese (package K35)

Usage: .venv\\Scripts\\python.exe -I tests\\Tsuki.Tests.Data\\Oracle\\gen_click_icon_golden.py
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
from eboot import eboot_path  # noqa: E402
from tsuki.misc_layer_state_and_motions_emu import BUF, LayerEmu  # noqa: E402
from tsuki.text import ScriptText  # noqa: E402

LISTS = 0x1DE83E0
SLOTS = 0x1DBD460
TEXTAB = 0x1DF9550
FVARS = 0x164B1D0
UISLOTS = 0x2077400          # UI texture handles, 0x1C bytes each (0xEB890: +4 flags, +8 image slot, +0xC second)
# UI slot -> (image slot we give it, sheet size): DISP_PARTS children 4 / 9 / 53 (registration table 0x780C90)
ICON_SLOTS = {2: (0x70, (4095, 39)), 3: (0x71, (4095, 39)), 4: (0x72, (2537, 46))}


def init_lists(E: LayerEmu) -> None:
    """What 0xD4D80 does for the list headers and the record pool (scratch/text_vscale/emu_chain.py)."""
    sent = 0x1DEB2C0
    for n in range(500):
        h = LISTS + 24 * n
        E.wr(h, struct.pack("<I", 0))
        E.wr(h + 8, struct.pack("<Q", sent + 0x70 * n))
        E.wr(h + 0x10, struct.pack("<Q", sent + 0x70 * n))
        E.wr(sent + 0x70 * n, bytes([1, 0, 0, 0]))
    E.wr(0x1DF8D80, bytes(0x7D0))
    pool = 0x1EB4D50
    E.wr(0x1FC645C, bytes(1))
    E.wr(0x1FC6460, struct.pack("<Q", pool))
    for k in range(10000):
        r = pool + 0x70 * k
        E.wr(r, bytes(4))
        E.wr(r + 0x68, struct.pack("<Q", r + 0x70 if k < 9999 else 0))
    E.wr(0x1FC6468, struct.pack("<Q", pool + 0x70 * 9999))


def font_slots(E: LayerEmu) -> None:
    """Texture slots 0x21..0x61 as load_font leaves them for a 1024 x 1024 one-tile page."""
    for slot in range(0x21, 0x62):
        base = SLOTS + 0x58 * slot
        E.wr(base, bytes(0x58))
        E.wr(base, bytes([1]))
        E.wr(base + 5, bytes([1]))
        E.wr(base + 0x20, struct.pack("<8h", 1024, 1024, 1024, 1024, 1, 1, 1, 0))
        flags = BUF + 0x1000 + 16 * slot
        ids = BUF + 0x2000 + 16 * slot
        tex_id = 0x100 + slot
        E.wr(flags, bytes([1]))
        E.wr(ids, struct.pack("<h", tex_id))
        E.wr(base + 0x30, struct.pack("<QQ", flags, ids))
        E.wr(TEXTAB + 32 * tex_id, bytes([1]))
        E.wr(TEXTAB + 32 * tex_id + 0x18, struct.pack("<Q", 1))


def icon_slots(E: LayerEmu) -> dict[int, int]:
    """UI slots 2..4 over image slots 0x70..0x72 as the loader leaves them for a one-tile sheet (size modes 0:
    DISP_PARTS children 4 / 9 / 53 have depth 0x14, bits 5..7 clear)."""
    tex_of: dict[int, int] = {}
    for ui, (img, (w, h)) in ICON_SLOTS.items():
        u = UISLOTS + 0x1C * ui
        E.wr(u, bytes(0x1C))
        E.wr(u + 8, struct.pack("<ii", img, -1))
        E.wr(u + 0x18, struct.pack("<i", -1))
        base = SLOTS + 0x58 * img
        E.wr(base, bytes(0x58))
        E.wr(base, bytes([1]))
        E.wr(base + 5, bytes([1]))
        tw, th = ((w + 255) // 256) * 256, ((h + 63) // 64) * 64
        E.wr(base + 0x20, struct.pack("<8h", w, h, tw, th, 1, 1, 1, 0))
        flags = BUF + 0x3000 + 16 * img
        ids = BUF + 0x4000 + 16 * img
        tex_id = 0x200 + img
        E.wr(flags, bytes([1]))
        E.wr(ids, struct.pack("<h", tex_id))
        E.wr(base + 0x30, struct.pack("<QQ", flags, ids))
        E.wr(TEXTAB + 32 * tex_id, bytes([1]))
        E.wr(TEXTAB + 32 * tex_id + 0x18, struct.pack("<Q", 1))
        tex_of[tex_id] = ui
    return tex_of


def setf(E: LayerEmu, n: int, v: int) -> None:
    E.wr(FVARS + 2 * n, struct.pack("<h", v))


def run(scenario: str, frames: int = 14) -> dict:
    # the language is the scenario's suffix (package K35 added line_zc and bubble_ja); the option values are ja 0, en 1, zc 2, zt 3
    suffix = scenario.rsplit("_", 1)[-1]
    lang_s = suffix if suffix in ("ja", "en", "zc", "zt") else "en"
    lang = {"ja": 0, "en": 1, "zc": 2, "zt": 3}[lang_s]
    text = ScriptText().get_line(lang_s, 3).encode("utf-8")
    E = LayerEmu(eboot_path())
    init_lists(E)
    font_slots(E)
    tex_of = icon_slots(E)
    E.wr(0x66C578 + 0x24, struct.pack("<h", lang))
    E.wr(0x66C578 + 0x26, struct.pack("<h", 1))
    E.wr(0x66C578 + 0x28, struct.pack("<h", 0))
    captured: list[dict] = []
    draw_calls: list[dict] = []
    calls: dict[str, int] = {}

    def d2be0() -> int:
        p = E.r("RDI")
        xy = [struct.unpack("<2f", E.rd(p + 0x18 * k + 0xC, 8)) for k in range(4)]
        uv = [struct.unpack("<2f", E.rd(p + 0x18 * k, 8)) for k in range(4)]
        sx, sy, ox, oy = (E.f32(0xDB9458 + 4 * i) for i in range(4))
        captured.append({"tex": E.i32(0x1FC6458), "uv": uv,
                         "px": [(x * sx + ox + 960.0, y * sy + oy + 540.0) for x, y in xy]})
        return 0

    def counter(name: str, ret: int = 0):
        def f() -> int:
            calls[name] = calls.get(name, 0) + 1
            return ret
        return f

    def on_eb890(uc: object, addr: int, size: int, _: object) -> None:
        rect = struct.unpack("<4h", E.rd(E.r("RCX"), 8))
        draw_calls.append({"ui_slot": E.r("RDI"), "flags": E.r("RSI"), "rect": rect,
                           "x": E.xmm_f(0), "y": E.xmm_f(1),
                           "scale": [E.f32(0xDC7BA4), E.f32(0xDC7BA8)],
                           "colour_alpha": E.f32(0x1DBD0D8)})

    from unicorn import UC_HOOK_CODE
    E.uc.hook_add(UC_HOOK_CODE, on_eb890, begin=0xEB890, end=0xEB890)
    E.stub(0xD2BE0, d2be0)
    E.stub(0xD7E70, 1)
    E.stub(0xD27E0, counter("solid_quad"))
    E.stub(0xD47C0, 1)                    # frame delta: one frame
    for va, name in ((0xA9310, "a9310"), (0xC3270, "find_picture"), (0xEB540, "eb540"),
                     (0x75CD0, "new_page"), (0xA6D00, "alt_choice"), (0xA6E10, "choice_screen"),
                     (0xA7A40, "a7a40"), (0x53200, "popup"), (0xA7CD0, "a7cd0"), (0xA8250, "a8250"),
                     (0xEB780, "picture_draw")):
        E.stub(va, counter(name, 0 if name != "find_picture" else 0xFFFFFFFF))
    # --- layout the message into block 4 (slot 0) as _ZM does (textbox.md 3.1.8) ----------------
    E.wr(BUF, text + b"\0" * 16)
    E.wr(0x178BE30 + 4 * 4, bytes(4))
    E.wr(0x177A210 + 4 * 0x190, bytes(0x190))
    window = 5 if scenario.startswith("bubble") else 0
    flags = (1 << 20) if window >= 3 else 0
    n = E.call(0xC6C20, {"RDI": 4, "RSI": BUF, "RDX": 474, "RCX": 220, "R8": 0, "R9": 9},
               {0: 0.74, 1: 0.74}, stack_args=[0xFFFF, flags])
    recs = []
    blk = 0x177A210 + 0x190 * 4
    base = struct.unpack("<Q", E.rd(blk + 0x180, 8))[0]
    nrec = struct.unpack("<h", E.rd(blk, 2))[0]
    for i in range(nrec):
        b = E.rd(base + 0x30 * i, 0x30)
        fl = struct.unpack_from("<Q", b, 0)[0]
        if fl == 0:
            continue
        line, tex, x0, y0, x2, y2, ctr = struct.unpack_from("<7H", b, 0xC)
        dx, dy, sx, sy = struct.unpack_from("<4f", b, 0x1C)
        recs.append({"i": i, "flags": fl, "line": line, "x0": x0, "x2": x2, "ctr": ctr, "dx": dx, "dy": dy, "sx": sx})
    # --- ADV state ----------------------------------------------------------------------------
    setf(E, 822, 0 if scenario == "f822_0" else 1)
    setf(E, 823, 1)
    setf(E, 799, 1 if scenario.startswith("page") else 0)
    setf(E, 1105, window)
    setf(E, 1109, 0)
    setf(E, 1103, 0)
    setf(E, 1104, 0)
    E.wr(0xDF0730, struct.pack("<f", 1.0))           # window fade A
    E.wr(0x781148, struct.pack("<f", 1.0))           # HUD alpha
    E.wr(0x86AA1C, struct.pack("<f", 1.0))           # text fade T
    head = 768.0
    tail = 768.0
    if scenario == "partial":
        mid = recs[len(recs) // 2]["ctr"]
        head, tail = float(mid), float(mid) - 2.0
    E.wr(0x1653E80, struct.pack("<ff", head, tail))
    e00 = 0x100 | 0x40 | (0 if scenario == "noclick" else 0x400)
    E.wr(0x1653E00, struct.pack("<Q", e00))
    E.wr(0x17DB230, struct.pack("<I", 1 if scenario == "auto" else 0))
    E.wr(0xDF08E4, struct.pack("<f", 0.0))
    E.wr(0x78122C, struct.pack("<i", -1))
    E.wr(0x16541D4, struct.pack("<i", 0))            # slot 0 -> block 4
    E.wr(0x120A6EC, struct.pack("<h", 0))            # history ring: nothing to re-lay out
    E.wr(0x120A6F2, struct.pack("<h", 2))
    E.wr(0x120A6F0, struct.pack("<h", 0))
    per_frame = []
    for fr in range(frames):
        init_lists(E)
        captured.clear()
        draw_calls.clear()
        E.call(0xDC740, {"RDI": 0})
        E.call(0x53CF0)
        E.wr(0x1FC6458, struct.pack("<i", -1))
        E.call(0xD9E10, {"RDI": 0})
        icon = [c for c in captured if c["tex"] in tex_of]
        per_frame.append({
            "frame": fr,
            "alpha": E.f32(0xDF08E4),
            "tick": E.i32(0x78122C),
            "icon_x": E.f32(0xDF088C),
            "icon_y": E.f32(0xDF0890),
            "draw": [d for d in draw_calls if d["ui_slot"] in ICON_SLOTS],
            "quads": [{"ui_slot": tex_of[c["tex"]], "px": c["px"], "uv": c["uv"]} for c in icon],
        })
    return {"scenario": scenario, "lang": lang_s, "text_line": 3, "window": window, "layout_flags": flags,
            "f822": 0 if scenario == "f822_0" else 1, "f799": 1 if scenario.startswith("page") else 0,
            "auto": 1 if scenario == "auto" else 0, "e00": e00, "head": head, "tail": tail,
            "layout_elements": n, "records": recs, "frames": per_frame}


def main() -> None:
    out_dir = ROOT / "samples" / "golden_data"
    out_dir.mkdir(parents=True, exist_ok=True)
    result = {}
    for name in ("line_en", "page_en", "line_ja", "page_ja", "bubble", "f822_0", "noclick", "partial", "auto", "line_zc", "bubble_ja"):
        result[name] = run(name)
        f0 = result[name]["frames"][0]
        d = f0["draw"][0] if f0["draw"] else None
        print(f"{name}: anchor ({f0['icon_x']}, {f0['icon_y']}) alpha {f0['alpha']:.2f} "
              + (f"slot {d['ui_slot']} rect {d['rect']} at ({d['x']:.4f}, {d['y']:.4f})" if d else "no draw"))
    path = out_dir / "click_icon_oracle.json"
    path.write_text(json.dumps(result, indent=1), encoding="utf-8")
    print("wrote", path)


if __name__ == "__main__":
    main()
