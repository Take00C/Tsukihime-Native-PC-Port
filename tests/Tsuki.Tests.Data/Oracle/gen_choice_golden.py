#!/usr/bin/env python
"""Writes the expectations of tests/Tsuki.Tests.Data/Game/ChoiceDrawOracleTests.cs from the Python oracle.

    D:/TsukihimePort/.venv/Scripts/python.exe -I tests/Tsuki.Tests.Data/Oracle/gen_choice_golden.py [--game DIR] [--out FILE]

Output: samples/golden_data/choice_oracle.json (local only, git-ignored; it holds positions and numbers, no game text). For the first story choice (script
01_00_ARC01_1, choice point 1) in English and Japanese it records the page origins, the click-icon y, the first line y, the laid-out line counts and
the draw list of the PS4 choice function 0xA6E10 (tsuki.misc_boot_title_choice_flow.choice_draw_list) for a few states of the choice machine. The states
are produced here by the same per-frame rules the engine uses (highlight +0.1, flare pulse +-0.0111 between 0.333 and 1, decided alpha -0.028 and flash
-0.02, all in float32), so the C# test can drive the host to them and compare the host's own numbers.
"""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import replace
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from tsuki import misc_boot_title_choice_flow as cf  # noqa: E402
from tsuki import textbox as tb  # noqa: E402
from tsuki import text_vscale as tv  # noqa: E402
from tsuki.font import Font  # noqa: E402

F32 = np.float32
SCRIPT = "01_00_ARC01_1"


class Machine:
    """The choice machine's per-frame numbers (VA 0xA6180..0xA6940, system_flow.md 5.2), float32."""

    def __init__(self) -> None:
        self.phase, self.cursor, self.prev = 2, -1, -1
        self.alpha, self.highlight, self.flash = F32(1.0), F32(1.0), F32(0.0)
        self.pulse, self.step = F32(1.0), F32(-0.0111)

    def set_cursor(self, c: int) -> None:
        if c != self.cursor:
            self.prev, self.cursor = self.cursor, c
            self.highlight = F32(0.0)
            self.pulse, self.step = F32(1.0), F32(-0.0111)

    def decide(self) -> None:
        self.phase, self.flash = 3, F32(1.0)

    def update(self) -> None:
        self.highlight = F32(min(F32(1.0), F32(self.highlight + F32(0.1))))
        self.pulse = F32(self.pulse + self.step)
        if self.pulse <= F32(0.333):
            self.pulse, self.step = F32(0.333), F32(0.0111)
        elif self.pulse >= F32(1.0):
            self.pulse, self.step = F32(1.0), F32(-0.0111)
        if self.phase == 3:
            self.alpha = F32(max(F32(0.0), F32(self.alpha - F32(0.028))))
            self.flash = F32(self.flash - F32(0.02))

    def view(self, lang: int, count: int, y0: float) -> cf.ChoiceView:
        return cf.ChoiceView(lang=lang, count=count, cursor=self.cursor, prev_cursor=self.prev, phase=self.phase,
                             alpha=float(self.alpha), highlight=float(self.highlight), flare_pulse=float(self.pulse),
                             flash=float(self.flash), y0=y0)


def run_scenarios(lang: int, count: int, y0: float, names: list[str]) -> dict[str, dict[str, object]]:
    m = Machine()
    out: dict[str, dict[str, object]] = {}

    def snap(name: str) -> None:
        if name in names:
            v = m.view(lang, count, y0)
            out[name] = {"view": {"phase": v.phase, "cursor": v.cursor, "prev": v.prev_cursor, "alpha": v.alpha,
                                  "highlight": v.highlight, "pulse": v.flare_pulse, "flash": v.flash},
                         "draws": [draw_json(d) for d in cf.choice_draw_list(v) if d.kind != "hit"]}

    snap("A_open")                                  # selectable, nothing highlighted
    m.set_cursor(1)
    for _ in range(5):
        m.update()
    snap("B_cursor1_5f")                            # cursor on line 1, half way through the highlight fade
    m.set_cursor(2)
    for _ in range(3):
        m.update()
    snap("C_cursor2_from1_3f")                      # cross-fade: line 2 rising, line 1 fading out
    m.decide()                                      # a decision within 10 frames of a move: the previous glow is still drawn
    for _ in range(2):
        m.update()
    snap("E_decided_2_after_move")
    m2 = Machine()
    m2.set_cursor(2)
    for _ in range(12):
        m2.update()
    m2.set_cursor(1)
    m2.decide()
    for _ in range(18):
        m2.update()
    m = m2
    snap("D_decided_1_18f")
    return out


def draw_json(d: cf.Draw) -> dict[str, object]:
    return {"kind": d.kind, "slot": d.slot, "pos": [float(d.pos[0]), float(d.pos[1])], "alpha": float(d.alpha), "flags": d.flags,
            "scale": [float(d.scale[0]), float(d.scale[1])],
            "colour": int(d.source[2]) if d.kind == "text" else 0, "line": int(d.source[1]) if d.kind == "text" else -1}


def scene_numbers(game: Path | None, lang: int) -> dict[str, object]:
    scene = cf.find_choice_scene(SCRIPT, 0, lang, game)
    font = Font(cf.LANG_CODES[lang])
    p = tb.main_box_params(lang)
    blocks = [tb.layout_message(m, lang, p, font=font, block=79 + j if j else 4) for j, m in enumerate(scene.page_messages)]
    origins = tb.page_positions(blocks, lang, blank_before=scene.page_blank_before, origin=(p.x, p.y))
    icon_y = float(F32(F32(tb.last_line_offset(blocks[-1])) + F32(origins[-1][1])))
    y0 = cf.choice_first_y(lang, f823=scene.f823, icon_y=icon_y)
    lines = [tb.layout_box(t, lang, font=font, width=cf.choice_width(lang), height=0xDC, spacing=0, leading=9, color=0xFFFF, flags=0,
                           sx=0.74, sy=0.74, block=0x14 + k).nlines for k, t in enumerate(scene.choice_texts)]
    names = ["A_open", "B_cursor1_5f", "C_cursor2_from1_3f", "E_decided_2_after_move", "D_decided_1_18f"]
    # the page text as the HUD draws it: list 0x1E nested (1, 1.3) under 640 x 448 (text_vscale.MAIN_TEXT): every quad's top-left pixel and its pixels per texel
    page_quads = []
    for b, (ox, oy) in zip(blocks, origins):
        for q in tb.block_quads(b, ox, oy, draw_flags=0x1020, lang=lang):
            g = tv.MAIN_TEXT.glyph_quad(q.x, q.y, q.u0, q.v0, q.u1, q.v1, q.sx, q.sy)
            page_quads.append([g.x0, g.y0, (g.y1 - g.y0) / max(q.v1 - q.v0, 1), (g.x1 - g.x0) / max(q.u1 - q.u0, 1), q.u0, q.v0])
    return {"page_quads": page_quads, "page_scale": [tv.KX_HUD, tv.KY_HUD], "choice_scale": [3.0, tv.KY_CHOICE], "count": len(scene.choice_texts), "f823": scene.f823, "page_origins": [[float(a), float(b)] for a, b in origins],
            "page_messages": len(scene.page_messages), "icon_y": icon_y, "y0": y0, "choice_width": cf.choice_width(lang),
            "line_counts": lines, "scenarios": run_scenarios(lang, len(scene.choice_texts), y0, names)}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=None)
    ap.add_argument("--out", default=str(Path(__file__).resolve().parents[3] / "samples" / "golden_data" / "choice_oracle.json"))
    a = ap.parse_args()
    game = Path(a.game) if a.game else None
    data = {"script": SCRIPT, "canvas": [640.0, 344.70001220703125], "pitch": 26.02000045776367,
            "en": scene_numbers(game, 1), "ja": scene_numbers(game, 0)}
    Path(a.out).parent.mkdir(parents=True, exist_ok=True)
    Path(a.out).write_text(json.dumps(data, indent=1), encoding="utf-8")
    print("wrote", a.out)
    for lang in ("en", "ja"):
        d = data[lang]
        print(lang, "icon_y", d["icon_y"], "y0", d["y0"], "lines", d["line_counts"], "origins", d["page_origins"])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
