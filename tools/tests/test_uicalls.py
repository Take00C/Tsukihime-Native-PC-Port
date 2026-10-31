"""Tests for eboot.uicalls, the constant-propagating call decoder of the eboot's UI code.

Synthetic tests assemble a few bytes of x86-64 and check the value model (constants, joins,
loops, the draw state, the widget pass).  Real-data tests decode the title draw 0xB1F30 and the choice
draw 0xA6E10 of the real eboot and compare every draw call with the hand-built draw lists of
``tsuki.misc_boot_title_choice_flow`` (the oracle): same texture slot, flags, blend, canvas, scale,
position, colour, effective rotation, sub-rectangle and alpha (the module-level ``*_FIELDS`` tuples list
what each kind of draw is compared on, and the negative control perturbs exactly those fields).  The
real-data tests skip only when the game folder is missing; the file collects without it.

Run:  cd D:/TsukihimePort/tools && ../.venv/Scripts/python.exe -m pytest tests/test_uicalls.py -q
"""
from __future__ import annotations

import dataclasses
import math
import random
import struct
import sys
from pathlib import Path
from typing import Any, Dict, Iterable, List, NamedTuple, Optional, Sequence, Tuple

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eboot import eboot_path  # noqa: E402
from eboot import uicalls as U  # noqa: E402
from tsuki import misc_boot_title_choice_flow as sf  # noqa: E402   # the oracle; imports without game data

needs_game = pytest.mark.skipif(eboot_path() is None, reason="game data not found")


# ---------------------------------------------------------------------------
# a tiny assembler and image for the synthetic tests
# ---------------------------------------------------------------------------
BASE = 0x1000


class FlatImage:
    """Minimal image: code at BASE, constants after it (all below RO_LIMIT)."""

    def __init__(self, blob: bytes) -> None:
        self.blob = blob

    def read_va(self, va: int, n: int) -> bytes:
        o = va - BASE
        if o < 0 or o + n > len(self.blob):
            raise KeyError(va)
        return self.blob[o:o + n]


class Asm:
    def __init__(self) -> None:
        self.b = bytearray()
        self.labels: Dict[str, int] = {}
        self.fix: List[tuple] = []

    @property
    def pc(self) -> int:
        return BASE + len(self.b)

    def raw(self, *bs: int) -> "Asm":
        self.b += bytes(bs)
        return self

    def label(self, name: str) -> "Asm":
        self.labels[name] = self.pc
        return self

    def mov_imm(self, reg: str, imm: int) -> "Asm":
        code = {"eax": 0xB8, "ecx": 0xB9, "edx": 0xBA, "ebx": 0xBB, "esi": 0xBE, "edi": 0xBF}[reg]
        return self.raw(code).raw(*struct.pack("<I", imm))

    def call(self, target: int) -> "Asm":
        self.raw(0xE8)
        self.b += struct.pack("<i", target - (self.pc + 4))
        return self

    def jcc(self, op: int, label: str) -> "Asm":
        self.raw(op, 0)
        self.fix.append((len(self.b) - 1, label))
        return self

    def movss_rip(self, xmm: int, va: int) -> "Asm":      # vmovss xmmN, [rip + disp]  (N < 8)
        self.raw(0xC5, 0xFA, 0x10, 0x05 | (xmm << 3))
        self.b += struct.pack("<i", va - (self.pc + 4))
        return self

    def code(self, extra: bytes = b"") -> FlatImage:
        for pos, name in self.fix:
            self.b[pos] = (self.labels[name] - (BASE + pos + 1)) & 0xFF
        return FlatImage(bytes(self.b) + extra)


SET_CANVAS, SET_SCALE, TEX_DRAW = 0xDCBE0, 0xD9DD0, 0xEB780


def test_constants_and_float_loads() -> None:
    data_va = BASE + 0x100
    a = Asm()
    a.mov_imm("edi", 0x7D).mov_imm("esi", 0x10).raw(0x31, 0xD2)            # edx = 0
    a.movss_rip(0, data_va).raw(0xC5, 0xF0, 0x57, 0xC9)                    # xmm0 = [data], xmm1 = 0
    a.call(TEX_DRAW).raw(0xC3)
    blob = bytearray(a.code().blob.ljust(0x100, b"\x90")) + struct.pack("<f", 12.5)
    tr = U.decode_function(FlatImage(bytes(blob)), BASE, BASE + len(a.b))
    (c,) = tr.calls
    assert c.callee is not None and c.callee.name == "TexDraw"
    assert c.named() == {"slot": 0x7D, "flags": 0x10, "blend": 0, "x": 12.5, "y": 0.0}


def test_forward_branch_joins_into_a_choice() -> None:
    a = Asm()
    a.mov_imm("esi", 5)
    a.raw(0x83, 0xF8, 0x00)                      # cmp eax, 0
    a.jcc(0x74, "skip")                          # je skip
    a.mov_imm("esi", 7)
    a.label("skip").mov_imm("edi", 1).call(TEX_DRAW).raw(0xC3)
    tr = U.decode_function(a.code(), BASE, a.pc)
    (c,) = tr.calls
    assert isinstance(c.args["rsi"], U.Choice) and set(c.args["rsi"].vals) == {5, 7}
    assert c.args["rdi"] == 1                    # same on both paths -> not a choice
    assert sorted(U.ev_all(c.args["rsi"], U.Env())) == [5, 7]


def test_draw_state_follows_setters() -> None:
    data_va = BASE + 0x100
    a = Asm()
    a.movss_rip(0, data_va).movss_rip(1, data_va + 4).raw(0xC5, 0xE8, 0x57, 0xD2, 0xC5, 0xE0, 0x57, 0xDB)   # xmm2 = xmm3 = 0
    a.call(SET_CANVAS)
    a.movss_rip(0, data_va + 8).movss_rip(1, data_va + 8).call(SET_SCALE)
    a.mov_imm("edi", 0x82).mov_imm("esi", 0x10).mov_imm("edx", 0x34).call(TEX_DRAW).raw(0xC3)
    blob = bytearray(a.code().blob.ljust(0x100, b"\x90")) + struct.pack("<3f", 960.0, 544.0, 0.5)
    tr = U.decode_function(FlatImage(bytes(blob)), BASE, BASE + len(a.b))
    sites = U.draw_sites(tr)
    assert [s.fn for s in sites] == ["TexDraw"]
    d = sites[0].draw
    assert d["canvas"] == (960.0, 544.0, 0.0, 0.0) and d["scale"] == (0.5, 0.5)
    assert sites[0].args["blend"] == 0x34


def test_loop_gives_a_carried_value_per_iteration() -> None:
    a = Asm()
    a.raw(0x31, 0xDB)                            # xor ebx, ebx
    a.label("top").raw(0x8D, 0x7B, 0x14)         # lea edi, [rbx + 0x14]
    a.call(TEX_DRAW)
    a.raw(0xFF, 0xC3)                            # inc ebx
    a.raw(0x83, 0xFB, 0x03)                      # cmp ebx, 3
    a.jcc(0x7C, "top").raw(0xC3)                 # jl top
    tr = U.decode_function(a.code(), BASE, a.pc)
    assert list(tr.loops) == [BASE + 2]
    (c,) = tr.calls
    slot = c.args["rdi"]
    assert "rbx" in U.fmt(slot) and isinstance(slot, U.Op)
    ent, back = tr.loops[BASE + 2].locs["rbx"]
    assert ent == 0 and back == U.Op("add", U.Carried(BASE + 2, "rbx"), 1)
    env = U.Env(loops=tr.loops)
    assert [U.ev(slot, env.with_iter(BASE + 2, k)) for k in range(3)] == [0x14, 0x15, 0x16]


def test_pending_state_through_padding_is_not_a_path() -> None:
    """nop padding after a jmp must not feed an empty state into the next block."""
    a = Asm()
    a.mov_imm("esi", 9).jcc(0xEB, "go")           # jmp go
    a.raw(0x90, 0x90)                             # alignment padding: dead, must not reach `go`
    a.label("go")
    a.call(TEX_DRAW).raw(0xC3)
    tr = U.decode_function(a.code(), BASE, a.pc)
    (c,) = tr.calls
    assert c.args["rsi"] == 9


def test_make_op_folds_float32() -> None:
    assert U.make_op("fadd", 0.1, 0.2) == U.f32(U.f32(0.1) + U.f32(0.2))
    assert U.make_op("fdiv", 1.0, 3.0) == U.f32(1.0 / 3.0)
    assert U.make_op("sx16", 0xFFFF) == (1 << 64) - 1
    assert U.make_op("cvt32", 0xFFFFFFFF) == -1.0
    assert U.join([1, 1]) == 1 and isinstance(U.join([1, 2]), U.Choice) and U.join([1, None]) is None
    assert U.join(list(range(U.MAX_CHOICE + 1))) is None


def test_parse_mem() -> None:
    m = U.parse_mem("dword ptr [rcx + rax*8 + 4]")
    assert m == U.MemOp(4, ((1, "rcx", 1), (1, "rax", 8)), 4)
    m = U.parse_mem("qword ptr [rbp - 0x38]")
    assert m == U.MemOp(8, ((1, "rbp", 1),), -0x38)
    assert U.parse_mem("rax") is None


WIDGET_PASS = 0xD0BE0


def test_widget_pass_clobbers_scale_and_is_found_by_group() -> None:
    """WidgetPass sets scale, colour and rotation from the widget records: the state after it is unknown
    (and a draw before it keeps its own).  widget_passes lists the passes, optionally of one group."""
    data_va = BASE + 0x100
    a = Asm()
    a.movss_rip(0, data_va).movss_rip(1, data_va).call(SET_SCALE)
    a.mov_imm("edi", 7).call(WIDGET_PASS)
    a.mov_imm("edi", 1).call(TEX_DRAW).raw(0xC3)
    blob = bytearray(a.code().blob.ljust(0x100, b"\x90")) + struct.pack("<f", 0.5)
    tr = U.decode_function(FlatImage(bytes(blob)), BASE, BASE + len(a.b))
    (site,) = U.draw_sites(tr)
    assert site.draw["scale"] is None
    (p,) = U.widget_passes([tr])
    assert p.callee is not None and p.callee.name == "WidgetPass" and p.args["rdi"] == 7
    assert p.draw["scale"] == (0.5, 0.5)
    assert U.widget_passes([tr], 7) == [p] and U.widget_passes([tr], 8) == []


def test_widget_table_reads_id_group_flags() -> None:
    def rec(wid: int, grp: int, fl: int) -> bytes:
        return struct.pack("<3i", wid, grp, fl).ljust(0x30, b"\0")
    blob = rec(0xE4, 10, 0x20) + rec(0xE5, 10, 0x20) + rec(-1, 0, 0) + rec(0xE6, 10, 0x20)
    assert U.widget_table(FlatImage(blob), BASE) == [(0xE4, 10, 0x20), (0xE5, 10, 0x20)]


def test_recipe_is_consistent() -> None:
    for va, c in U.CALLEES.items():
        assert c.kind in U.DRAW_KINDS + U.STATE_KINDS + ("other",)
        assert set(c.clobbers) <= set(U.STATE_KINDS)
        for _n, reg in c.args:
            assert reg in U.ARG_REGS or reg in U.XMM_ARGS
    assert U.CALLEES[0xEB780].kind == "tex" and U.CALLEES[0xDCBE0].kind == "canvas"


# ---------------------------------------------------------------------------
# the module and its tests need no game data to be collected
# ---------------------------------------------------------------------------
@needs_game
def test_collects_and_skips_cleanly_without_game_data() -> None:
    """With the game folder missing the file still collects: the synthetic tests run and the real-data
    tests skip (the skip is the conditional ``skipif`` above, never an unconditional one).  The nested
    run sees ``eboot_path() is None`` and skips this test, so there is no recursion."""
    import subprocess
    code = ("import sys; sys.path.insert(0, sys.argv[1]); import eboot; eboot.eboot_path = lambda: None; "
            "import pytest; sys.exit(pytest.main([sys.argv[2], '-q', '-p', 'no:cacheprovider']))")
    tools = str(Path(__file__).resolve().parents[1])
    r = subprocess.run([sys.executable, "-c", code, tools, str(Path(__file__).resolve())],
                       capture_output=True, text=True, timeout=120)
    out = r.stdout + r.stderr
    assert r.returncode == 0, out
    assert " passed" in out and " skipped" in out and "error" not in out.lower() and "failed" not in out.lower(), out


# ---------------------------------------------------------------------------
# real data: the extracted draw calls equal the oracle draw lists
# ---------------------------------------------------------------------------
TITLE_FUNC, CHOICE_FUNC = 0xB1F30, 0xA6E10
LOOP_WIDGETS, LOOP_CHOICE = 0xB27D2, 0xA70BC          # loop headers found by the decoder
GLOW_SITES = (0xA7299, 0xA7301, 0xA7369, 0xA73D9, 0xA744F, 0xA74B6, 0xA751E, 0xA758D)   # the 8 offset copies
# the oracle annotates logo band 1 with 0xB2316 (steady state); while the logo fades in the engine
# takes the other branch of the same band, 0xB22A5 (alpha = la^2, not la)
TITLE_BAND1_FADING = 0xB22A5
WIDGET_TABLES = (0x873780, 0x7A6C00, 0x7A76D0, 0x8731F0)   # title, menu, mode panel, prompt
CHOICE_REGISTER_FUNC = 0xA5DF0                    # registers the hit widgets 0xD5 + k, k < 9, in group 13
CHOICE_PASS_FUNCS = (0xA7CD0, 0xA8250)            # the two functions that call WidgetPass(13)


class Ctx(NamedTuple):
    """One decoded screen: image, trace, draw sites by VA, the traces whose WidgetPass calls draw the
    screen's widgets, and the widget id -> group map."""
    img: Any
    tr: U.Trace
    sites: Dict[int, U.DrawSite]
    passes: List[U.Trace]
    groups: Dict[int, int]


def widget_groups(img: Any) -> Dict[int, int]:
    """Widget id -> group, from the definition tables (WidgetTable) and from the registration loop of
    the choice hit widgets (WidgetRegister at 0xA5F5A: id 0xD5 + k, group in rsi)."""
    g: Dict[int, int] = {}
    for t in WIDGET_TABLES:
        g.update({w: grp for w, grp, _f in U.widget_table(img, t)})
    tr = U.decode_function(img, CHOICE_REGISTER_FUNC)
    (reg,) = [c for c in tr.calls if c.target == 0xCF9E0]
    (hdr,) = tr.loops
    for k in range(9):
        g[U.ev(reg.named()["widget"], U.Env(img, loops=tr.loops).with_iter(hdr, k))] = reg.named()["group"]
    return g


@pytest.fixture(scope="module")
def img():
    return U.open_eboot()


@pytest.fixture(scope="module")
def groups(img):
    return widget_groups(img)


@pytest.fixture(scope="module")
def title(img, groups) -> Ctx:
    tr = U.decode_function(img, TITLE_FUNC)
    return Ctx(img, tr, {s.va: s for s in U.draw_sites(tr)}, [tr], groups)


@pytest.fixture(scope="module")
def choice(img, groups) -> Ctx:
    tr = U.decode_function(img, CHOICE_FUNC)
    return Ctx(img, tr, {s.va: s for s in U.draw_sites(tr)}, [U.decode_function(img, f) for f in CHOICE_PASS_FUNCS],
               groups)


def close(a: float, b: float, tol: float = 1e-4) -> bool:
    return abs(float(a) - float(b)) <= tol * max(1.0, abs(float(b)))


def has(cands: Optional[Iterable[Any]], want: float, tol: float = 1e-4) -> bool:
    return cands is not None and any(close(c, want, tol) for c in cands)


def pair_has(cands: Optional[Iterable[Any]], want: Sequence[float], tol: float = 1e-4) -> bool:
    return cands is not None and any(close(c[0], want[0], tol) and close(c[1], want[1], tol) for c in cands)


class Checker:
    """Collects mismatches, how many fields were really compared and how many alternatives the
    extracted expression offered for them (a check that offers many is weak)."""

    def __init__(self) -> None:
        self.bad: List[str] = []
        self.bad_fields: set = set()
        self.compared: Dict[str, int] = {}
        self.max_cands: Dict[str, int] = {}
        self.generic_alpha = 0              # alpha comparisons at a value strictly between 0 and 1

    def field(self, what: str, ok: bool, detail: Any, cands: Sequence[Any] = ()) -> None:
        k = what.split(":")[-1]
        self.compared[k] = self.compared.get(k, 0) + 1
        self.max_cands[k] = max(self.max_cands.get(k, 0), len(cands))
        if not ok:
            self.bad.append(f"{what}: extracted {detail}")
            self.bad_fields.add(k)

    def has(self, what: str, got: Optional[Sequence[Any]], want: float, tol: float = 1e-4) -> None:
        self.field(what, has(got, want, tol), (got, want), got or ())

    def pair(self, what: str, got: Optional[Sequence[Any]], want: Sequence[float], tol: float = 1e-4) -> None:
        self.field(what, pair_has(got, want, tol), (got, want), got or ())


def effective_rotation(s: U.DrawSite, env: U.Env) -> Optional[List[float]]:
    """Angles (radians) a texture draw can be drawn with: the SetRot state counts only when the draw
    flag 0x20 is set, otherwise the draw is not rotated."""
    flags, rots = U.try_all(s.args["flags"], env), U.try_all(s.draw["rot"], env)
    if flags is None:
        return None
    out: List[float] = []
    for f in flags:
        if f & 0x20:
            if rots is None:
                return None
            out += [r[0] * 2.0 * math.pi / 65536.0 for r in rots]
        else:
            out.append(0.0)
    return out


def compare_site(chk: Checker, tag: str, s: U.DrawSite, d: Any, env: U.Env, fields: Sequence[str]) -> None:
    """Compare one extracted site with one oracle ``Draw`` on the named fields."""
    if "slot" in fields:
        chk.has(f"{tag}:slot", U.try_all(s.args["slot" if "slot" in s.args else "block"], env), d.slot)
    if "flags" in fields:
        chk.has(f"{tag}:flags", U.try_all(s.args["flags"], env), d.flags)
    if "blend" in fields:
        chk.has(f"{tag}:blend", U.try_all(s.args["blend"], env), d.blend)
    if "canvas" in fields:
        chk.pair(f"{tag}:canvas", U.try_all(s.draw["canvas"], env), d.canvas)
    if "scale" in fields:
        chk.pair(f"{tag}:scale", U.try_all(s.draw["scale"], env), d.scale)
    if "pos" in fields:
        chk.has(f"{tag}:x", U.try_all(s.args["x"], env), d.pos[0])
        chk.has(f"{tag}:y", U.try_all(s.args["y"], env), d.pos[1])
    if "alpha" in fields:
        if s.kind in ("text", "widget"):
            got = U.try_all(s.args["alpha"], env)
        else:
            col = U.try_all(s.draw["color"], env)
            got = [c[0] for c in col] if col else None
        chk.has(f"{tag}:alpha", got, d.alpha)
        chk.generic_alpha += 1 if 0.001 < d.alpha < 0.999 else 0
    if "rgb" in fields:
        col = U.try_all(s.draw["color"], env)
        chk.field(f"{tag}:rgb", col is not None and any(all(close(c[1 + i], d.rgb[i]) for i in range(3)) for c in col),
                  (col, d.rgb), col or ())
    if "rect" in fields:
        got_r = U.read_rect(s.call, env)
        chk.field(f"{tag}:rect", got_r is not None and tuple(d.rect) in got_r, (got_r, d.rect), got_r or ())
    if "rot" in fields:
        ang = effective_rotation(s, env)
        chk.field(f"{tag}:rot", ang is not None and any(close(g, d.rotation, 1e-3) for g in ang), (ang, d.rotation), ang or ())


def pass_canvas(c: Ctx, chk: Checker, tag: str, s: U.DrawSite, d: Any, env: U.Env) -> None:
    """The canvas of the WidgetPass calls of the group of the site's (extracted) widget equals the oracle's
    canvas for the widget.  (WidgetDraw only stores position and alpha; the canvas at its call site is not
    the widget's.)"""
    wids = U.try_all(s.args["widget"], env) or []
    for g in sorted({c.groups[w] for w in wids}):
        passes = U.widget_passes(c.passes, g)
        assert passes, f"no WidgetPass for group {g}"
        for p in passes:
            chk.pair(f"{tag}:passcanvas", U.try_all(p.draw["canvas"], env), d.canvas)


# -- title ----------------------------------------------------------------
def title_env(img: Any, tr: U.Trace, v: Any, item: Optional[int] = None) -> U.Env:
    """Runtime values the oracle's TitleView stands for (its globals; see title_draw_list)."""
    rows = sf.title_rows(v.continue_available)
    mems = {0x176220C: v.logo_alpha, 0x1762210: sf.title_logo_factor(v), 0x17621AC: v.menu_alpha,
            0x17621F8: v.menu_exit, 0x1762220: v.highlight_grow, 0x17621DC: v.glow_phase, 0x17621E0: 0.0,
            0x17621F0: v.press_alpha, 0x873904: v.cursor, 0x176219C: sf.title_row_count(v.continue_available),
            0x17621FC: 0}
    for i in range(7):
        mems[0x1762180 + 4 * i] = rows[i] & 0xFFFFFFFF
    # lang = Lang_Index; retb2453 = 0xD47C0 (frames since the last call): the oracle's steady state is 0
    return U.screen_env(img, "title", tr, mems, {"lang": sf._lang_key(v.lang), "retb2453": 0},
                        {LOOP_WIDGETS: item} if item is not None else {})


TITLE_VIEWS = [
    sf.TitleView(lang=1, g10=1, cursor=1),
    sf.TitleView(lang=0, g10=1, cursor=2, continue_available=True),
    sf.TitleView(lang=1, g10=1, cursor=0, continue_available=True),
    sf.TitleView(lang=1, g10=1, cursor=5),
    sf.TitleView(lang=0, g10=0),
    sf.TitleView(lang=1, g10=0, g2=3),
    sf.TitleView(lang=1, g10=1, cursor=3, logo_alpha=0.5, logo_fading=True, highlight_grow=0.5, menu_alpha=0.6),
    sf.TitleView(lang=1, g10=1, cursor=1, movie_time=3.0),
]

# What each check compares, per draw kind (the mutation test below perturbs exactly these fields).
# NOT compared: the frame of a title widget (texture slot, flags, blend, rectangle): it lives in the
# widget's frame list (pointer at +0x20 of the definition record), which uicalls does not read yet.
TITLE_TEX_FIELDS = ("slot", "flags", "blend", "canvas", "scale", "pos", "alpha", "rgb", "rot")
TITLE_WIDGET_FIELDS = ("widget", "pos", "alpha", "wscale", "passcanvas")


def check_title_draw(c: Ctx, v: Any, d: Any, chk: Checker) -> int:
    """Compare one oracle draw of a title view with the extracted site of the same call; returns its VA."""
    va = TITLE_BAND1_FADING if (d.va == 0xB2316 and v.logo_fading) else d.va
    s = c.sites.get(va)
    assert s is not None, f"no draw site at {va:#x} for {d.note}"
    tag = f"{va:#x}"
    if d.kind == "widget":
        item = int(d.note.split("widget ")[1].split()[0], 16) - 0xE4
        env = title_env(c.img, c.tr, v, item)
        chk.has(f"{tag}:widget", U.try_all(s.args["widget"], env), 0xE4 + item)
        compare_site(chk, tag, s, d, env, ("pos", "alpha"))
        (ws,) = [x for x in c.tr.calls if x.va == 0xB285A]            # the scale comes from the WidgetScale call
        chk.pair(f"{tag}:wscale", [(ws.named()["sx"], ws.named()["sy"])], d.scale)
        pass_canvas(c, chk, tag, s, d, env)
        return va
    env = title_env(c.img, c.tr, v)
    fields = list(TITLE_TEX_FIELDS) + (["rect"] if d.rect is not None else [])
    compare_site(chk, tag, s, d, env, fields)
    return va


def check_title_view(c: Ctx, v: Any, chk: Checker) -> set:
    return {check_title_draw(c, v, d, chk) for d in sf.title_draw_list(v)}


@needs_game
def test_title_draw_list_equals_the_oracle(title) -> None:
    chk = Checker()
    seen: set = set()
    for v in TITLE_VIEWS:
        seen |= check_title_view(title, v, chk)
    assert not chk.bad, "\n".join(chk.bad)
    # every field class really ran (not vacuously true)
    for k, n in {"slot": 20, "flags": 20, "blend": 20, "canvas": 20, "scale": 20, "x": 40, "y": 40,
                 "alpha": 40, "rgb": 20, "rot": 20, "rect": 8, "widget": 8, "wscale": 8, "passcanvas": 8}.items():
        assert chk.compared.get(k, 0) >= n, (k, chk.compared)
    # the extracted expressions offer few alternatives, so membership is a real test
    assert max(chk.max_cands.values()) <= 4, chk.max_cands
    # all draw sites of the function are explained by some view
    assert set(title.sites) == seen, {hex(v) for v in set(title.sites) ^ seen}


@needs_game
def test_title_site_table_is_the_expected_one(title) -> None:
    """The ten texture draw sites of 0xB1F30 and the widget draw (system_flow 4.5)."""
    sites = title.sites
    assert {va: (s.fn, U.try_all(s.args.get("slot"), U.Env())) for va, s in sites.items() if "slot" in s.args} == {
        0xB20BA: ("TexDraw", [0x7D]), 0xB223D: ("TexDrawRect", [0x7F]), 0xB22A5: ("TexDrawRect", [0x7F]),
        0xB2316: ("TexDrawRect", [0x7F]), 0xB236D: ("TexDrawRect", [0x7F]), 0xB2403: ("TexDraw", [0x82]),
        0xB25D8: ("TexDraw", [0x88]), 0xB25F6: ("TexDraw", [0x87]), 0xB2734: ("TexDrawRect", [0x80]),
        0xB29A6: ("TexDraw", [0x81])}
    assert sites[0xB27B9].fn == "WidgetDraw"


@needs_game
def test_title_tables_match_the_oracle_constants(img, title) -> None:
    """The language-indexed tables the draw reads equal the oracle's tables."""
    tr, sites = title.tr, title.sites
    for lang in (0, 1):
        env = U.screen_env(img, "title", tr, {}, {"lang": lang})
        # logo position, glow position (VA 0x218B20 / 0x218B50, 8 bytes per language)
        x = U.ev(sites[0xB223D].args["x"], env), U.ev(sites[0xB223D].args["y"], env)
        assert pair_has([x], sf.LOGO_POS[lang])
        g = U.ev(sites[0xB2403].args["x"], env), U.ev(sites[0xB2403].args["y"], env)
        assert pair_has([g], sf.GLOW_POS[lang])
        # logo bands: rectangles of the 3 draws (VA 0x873910 + 24 per language)
        for va, band in ((0xB223D, 0), (0xB2316, 1), (0xB236D, 2)):
            assert U.read_rect(sites[va].call, env) == [sf.LOGO_RECTS[lang][band]]
    # press-any-button and copyright positions (constants)
    for va, want in ((0xB25D8, sf.PRESS_POS), (0xB29A6, sf.COPYRIGHT_POS)):
        assert pair_has([(U.ev(sites[va].args["x"], U.Env()), U.ev(sites[va].args["y"], U.Env()))], want)
    # the highlight bar rectangle lives in a stack slot (immediate store 0x20016800180018)
    (r,) = U.read_rect(sites[0xB2734].call, U.Env())
    assert r == sf.HL_RECT
    # the background draw's position has two arms: (0, 0) and the intro glow layer (VA 0x2186E8)
    bg = sites[0xB20BA]
    for arg, want in (("x", sf.INTRO_GLOW_POS[0]), ("y", sf.INTRO_GLOW_POS[1])):
        got = U.ev_all(bg.args[arg], U.Env())
        assert len(got) == 2 and has(got, 0.0) and has(got, want)


# -- choice ---------------------------------------------------------------
def choice_env(img: Any, tr: U.Trace, v: Any, line: int = 0) -> U.Env:
    y0 = v.y0 if v.y0 is not None else sf.SEL_Y_DEFAULT[sf._lang_key(v.lang)]
    mems = {0x17506B0: 1, 0x17506A8: v.alpha, 0x17506AC: v.flash, 0x17506A2: v.cursor & 0xFFFF,
            0x17506A4: v.prev_cursor & 0xFFFF, 0x17506A0: v.phase, 0x1750646: v.count,
            0x17506B4: v.highlight, 0x17506B8: v.flare_pulse}
    # arg_xmm0 = the HUD alpha argument (1: the oracle folds it into `alpha`), freta6e6e = 0x6F430()
    # (0: message window not fading), outa6f58.rsi = the click-icon y that 0x52F10 returns
    syms = {"arg_xmm0": 1.0, "freta6e6e": 0.0, "outa6f58.rsi": y0}
    return U.screen_env(img, "choice", tr, mems, syms, {LOOP_CHOICE: line})


CHOICE_VIEWS = [
    sf.ChoiceView(lang=1, count=3, cursor=1, y0=191.04),
    sf.ChoiceView(lang=0, count=2, cursor=0, prev_cursor=1, highlight=0.4, alpha=0.8, flare_pulse=0.5),
    sf.ChoiceView(lang=1, count=3, cursor=-1),
    sf.ChoiceView(lang=1, count=3, cursor=1, phase=3, y0=191.04, flash=0.5, alpha=0.9),
    sf.ChoiceView(lang=1, count=4, cursor=2, prev_cursor=0, highlight=0.3, y0=100.0, shadow=True),
    sf.ChoiceView(lang=0, count=9, cursor=8, prev_cursor=7, highlight=0.7, y0=60.0, flare_pulse=0.4),
    sf.ChoiceView(lang=1, count=2, cursor=0, phase=3, flash=0.1, alpha=0.5),
]

# NOT compared: the text colour word (0x77FF / 0xC7FF / 0xFFFF) is the first stack argument of
# TextLayout (`push rax`), which the decoder does not track; the text blend (SetBlend is never called
# in 0xA6E10, so it is the caller's / the default 0; test_choice_text_blend_is_inherited pins that).
CHOICE_TEX_FIELDS = ("slot", "flags", "blend", "canvas", "scale", "pos", "alpha", "rgb", "rot")
CHOICE_TEXT_FIELDS = ("slot", "flags", "pos", "alpha", "canvas")
CHOICE_HIT_FIELDS = ("widget", "pos", "alpha", "passcanvas")


def check_choice_draw(c: Ctx, v: Any, d: Any, chk: Checker) -> int:
    """Compare one oracle draw of a choice view with the extracted site of the same call; returns its VA."""
    # the line index comes from the oracle's text / note, not from the slot, so a wrong slot is not self-consistent
    if d.kind == "text" and d.slot < 0x64:
        line, va = d.source[1], 0xA7128
    elif d.kind == "text":
        line, va = d.source[1], GLOW_SITES[int(d.note.split("glow ")[1].split()[0])]   # copy j: blocks 0x64 + j / 0x6C + j
    elif d.kind == "hit":
        line, va = int(d.note.split("line ")[1].split()[0]), 0xA7086
    else:
        line, va = v.cursor, d.va
    s, tag, env = c.sites[va], f"{va:#x}", choice_env(c.img, c.tr, v, line)
    if d.kind == "text":
        compare_site(chk, tag, s, d, env, CHOICE_TEXT_FIELDS)     # flags: 0x1020 plus 0x10 when the shadow option is on
    elif d.kind == "hit":
        chk.has(f"{tag}:widget", U.try_all(s.args["widget"], env), d.slot)
        compare_site(chk, tag, s, d, env, ("pos", "alpha"))       # y x 448 / 344.7
        pass_canvas(c, chk, tag, s, d, env)
    else:
        compare_site(chk, tag, s, d, env, CHOICE_TEX_FIELDS)
    return va


def check_choice_view(c: Ctx, v: Any, chk: Checker) -> set:
    return {check_choice_draw(c, v, d, chk) for d in sf.choice_draw_list(v)}


@needs_game
def test_choice_draw_list_equals_the_oracle(choice) -> None:
    chk = Checker()
    seen: set = set()
    for v in CHOICE_VIEWS:
        seen |= check_choice_view(choice, v, chk)
    assert not chk.bad, "\n".join(chk.bad)
    for k, n in {"slot": 80, "flags": 80, "blend": 8, "canvas": 80, "scale": 12, "x": 80, "y": 80,
                 "alpha": 120, "rgb": 12, "rot": 12, "widget": 20, "passcanvas": 20}.items():
        assert chk.compared.get(k, 0) >= n, (k, chk.compared)
    assert max(chk.max_cands.values()) <= 6, chk.max_cands
    assert set(choice.sites) == seen, {hex(v) for v in set(choice.sites) ^ seen}


@needs_game
def test_choice_loop_is_recovered(choice) -> None:
    """The line loop: header 0xA70BC, y advances by SEL_PITCH (VA 0x216920), the index by 1."""
    tr = choice.tr
    assert LOOP_CHOICE in tr.loops
    locs = tr.loops[LOOP_CHOICE].locs
    ent, back = locs["r15"]
    assert ent == 0 and back == U.Op("add", U.Carried(LOOP_CHOICE, "r15"), 1)
    ent, back = locs["s-84"]                                     # line y, the frame slot rbp-0x54
    assert isinstance(back, U.Op) and back.op == "fadd" and back.b == U.f32(sf.SEL_PITCH)


@needs_game
def test_choice_ring_formula_is_extracted_from_the_code(img, choice) -> None:
    """The phase-3 ring scale / turn come out of the sinf chain, not from the oracle's formula."""
    s = choice.sites[0xA776C]
    for flash in (1.0, 0.75, 0.4, 0.0):
        env = U.screen_env(img, "choice", choice.tr, {0x17506AC: flash, 0x17506A8: 1.0}, {"arg_xmm0": 1.0, "freta6e6e": 0.0})
        (sc,) = U.ev_all(s.draw["scale"], env)
        (rot,) = U.ev_all(s.draw["rot"], env)
        want_sc, want_ang = sf.choice_ring_transform(flash)
        assert close(sc[0], want_sc[0], 1e-6) and close(sc[1], want_sc[1], 1e-6)
        assert close(rot[0] * 2 * math.pi / 65536.0, want_ang, 1e-9)


@needs_game
def test_choice_text_blend_is_inherited(choice) -> None:
    """The oracle gives every choice text draw blend 0 (normal).  The code never calls SetBlend, so a
    text draw's blend is the caller's (the engine default), and no site carries a blend state."""
    assert not [c for c in choice.tr.calls if c.target == 0xD9D90]
    assert all(s.draw["blend"] is None for s in choice.sites.values())


# -- widgets: WidgetDraw is a setter, WidgetPass draws ---------------------------
@needs_game
def test_widget_groups_and_pass_canvases(img, title, choice, groups) -> None:
    """Widget ids -> groups come from the definition tables (title 0xE4..0xEA group 10, menu 0xC1..0xC5
    group 1, mode panel 0xCB..0xD2 group 14, prompt 0xD5, 0xD6 group 13) and, for the nine choice hit
    widgets 0xD5..0xDD, from the WidgetRegister loop in 0xA5DF0 (group 13).  The canvas of a group is
    the canvas of its WidgetPass calls."""
    assert {w: groups[w] for w in range(0xE4, 0xEB)} == {w: 10 for w in range(0xE4, 0xEB)}
    assert {w: groups[w] for w in range(0xC1, 0xC6)} == {w: 1 for w in range(0xC1, 0xC6)}
    assert {w: groups[w] for w in range(0xCB, 0xD3)} == {w: 14 for w in range(0xCB, 0xD3)}
    assert {w: groups[w] for w in range(0xD5, 0xDE)} == {w: 13 for w in range(0xD5, 0xDE)}
    assert [w for w, _g, _f in U.widget_table(img, 0x8731F0)] == [0xD5, 0xD6]
    canvas = {}
    for name, ctx, group in (("title", title, 10), ("choice", choice, 13)):
        ps = U.widget_passes(ctx.passes, group)
        assert ps and all(p.args["rdi"] == group for p in ps), name
        canvas[name] = {U.fmt(p.draw["canvas"]) for p in ps}
    assert canvas == {"title": {"(640, 448, 0, 0)"}, "choice": {"(640, 448, 0, 0)"}}
    assert [p.va for p in U.widget_passes(choice.passes, 13)] == [0xA7F81, 0xA8501]
    run = U.run_entry(img)
    (menu_pass,) = U.widget_passes([run.traces[0x6CB80]], 1)
    assert U.fmt(menu_pass.draw["canvas"]) == "(640, 448, 0, 0)"


@needs_game
def test_widget_setter_canvas_is_not_the_widget_canvas(choice) -> None:
    """WidgetDraw (0xCFC90) only stores x, y, alpha into the widget record; the choice function calls it
    while the text canvas (640 x 344.7) is set, but the widgets are drawn by WidgetPass on 640 x 448.
    The decoder's draw state of a widget site is the setter's, so it must not be read as the widget's."""
    site = choice.sites[0xA7086]
    assert site.fn == "WidgetDraw"
    setter = U.try_all(site.draw["canvas"], U.Env())
    assert pair_has(setter, (640.0, 344.7)) and not pair_has(setter, (640.0, 448.0))
    (p, *_rest) = U.widget_passes(choice.passes, 13)
    assert U.try_all(p.draw["canvas"], U.Env()) == [(640.0, 448.0, 0.0, 0.0)]
    assert U.CALLEES[0xCFC90].note.startswith("a SETTER")
    assert "setter" in U.site_row(site)


# -- randomised differential ------------------------------------------------
RANDOM_VIEWS = 40


def _rf(rng: random.Random) -> float:
    return rng.choice([0.0, 0.05, 0.3, 0.5, 0.77, 1.0, rng.random()])


def random_title_view(rng: random.Random) -> Any:
    la = rng.choice([1.0, _rf(rng), _rf(rng)])
    return sf.TitleView(lang=rng.randint(0, 1), g10=rng.randint(0, 1), g2=rng.randint(0, 3),
                        cursor=rng.choice([0, 1, 2, 3, 5]), continue_available=rng.random() < 0.5,
                        logo_alpha=la if la > 0 else 0.1, logo_fading=rng.random() < 0.4,
                        menu_alpha=rng.choice([1.0, _rf(rng)]), menu_exit=rng.choice([0.0, 0.0, _rf(rng)]),
                        highlight_grow=_rf(rng), glow_phase=_rf(rng), press_alpha=rng.choice([1.0, _rf(rng)]))


def random_choice_view(rng: random.Random) -> Any:
    n = rng.randint(1, 9)
    return sf.ChoiceView(lang=rng.randint(0, 1), count=n, cursor=rng.randint(-1, n - 1),
                         prev_cursor=rng.randint(-1, n - 1), phase=rng.choice([2, 3]),
                         alpha=rng.choice([1.0, _rf(rng)]), highlight=_rf(rng), flare_pulse=_rf(rng), flash=_rf(rng),
                         y0=rng.choice([None, 60.0, 100.0, 191.04, rng.uniform(20, 200)]), shadow=rng.random() < 0.5)


@needs_game
def test_random_views_equal_the_oracle(title, choice) -> None:
    """Seeded random views (fades, highlights, flashes, line counts, cursors): the alpha and position
    formulas are checked at generic inputs, where a wrong formula cannot hide behind an alternative that
    happens to equal the oracle's value (the steady-state alpha is only 0 or 1)."""
    rng = random.Random(20261025)
    t, c = Checker(), Checker()
    for _ in range(RANDOM_VIEWS):
        check_title_view(title, random_title_view(rng), t)
        check_choice_view(choice, random_choice_view(rng), c)
    assert not t.bad, "\n".join(t.bad[:10])
    assert not c.bad, "\n".join(c.bad[:10])
    assert sum(t.compared.values()) > 2000 and sum(c.compared.values()) > 3000, (t.compared, c.compared)
    assert t.generic_alpha > 100 and c.generic_alpha > 100, (t.generic_alpha, c.generic_alpha)


# -- negative control: every field the checks compare is sensitive ------------
def mutants(d: Any, name: str) -> List[List[Any]]:
    """Wrong oracle draws for one compared field: a list of independent mutations, each with a few
    alternative deltas (the first that the check notices counts; one that equals an alternative of the
    extracted expression is not noticed)."""
    R = dataclasses.replace
    if name == "slot":
        return [[R(d, slot=d.slot + 1), R(d, slot=d.slot + 17)]]
    if name == "flags":
        return [[R(d, flags=d.flags ^ 0x4), R(d, flags=d.flags ^ 0x100)]]
    if name == "blend":
        return [[R(d, blend=d.blend + 1), R(d, blend=d.blend ^ 0x40)]]
    if name in ("canvas", "passcanvas"):
        return [[R(d, canvas=(d.canvas[0] + 1.0, d.canvas[1])), R(d, canvas=(d.canvas[0] * 1.5, d.canvas[1]))],
                [R(d, canvas=(d.canvas[0], d.canvas[1] + 1.0)), R(d, canvas=(d.canvas[0], d.canvas[1] * 1.5))]]
    if name in ("scale", "wscale"):
        return [[R(d, scale=(d.scale[0] * 1.013, d.scale[1])), R(d, scale=(d.scale[0] * 0.5, d.scale[1]))],
                [R(d, scale=(d.scale[0], d.scale[1] * 1.013)), R(d, scale=(d.scale[0], d.scale[1] * 0.5))]]
    if name == "x":
        return [[R(d, pos=(d.pos[0] + 0.5, d.pos[1])), R(d, pos=(d.pos[0] - 3.7, d.pos[1]))]]
    if name == "y":
        return [[R(d, pos=(d.pos[0], d.pos[1] + 0.5)), R(d, pos=(d.pos[0], d.pos[1] - 3.7))]]
    if name == "alpha":
        return [[R(d, alpha=d.alpha + 0.0373), R(d, alpha=d.alpha - 0.0291), R(d, alpha=d.alpha * 0.5 + 0.2113)]]
    if name == "rgb":
        return [[R(d, rgb=(d.rgb[0] * 0.9, d.rgb[1], d.rgb[2])), R(d, rgb=(0.3, d.rgb[1], d.rgb[2]))],
                [R(d, rgb=(d.rgb[0], d.rgb[1] * 0.9, d.rgb[2])), R(d, rgb=(d.rgb[0], 0.3, d.rgb[2]))],
                [R(d, rgb=(d.rgb[0], d.rgb[1], d.rgb[2] * 0.9)), R(d, rgb=(d.rgb[0], d.rgb[1], 0.3))]]
    if name == "rect":
        return [[R(d, rect=(d.rect[0] + 1,) + tuple(d.rect[1:])), R(d, rect=tuple(d.rect[:3]) + (d.rect[3] + 7,))]]
    if name == "rot":
        return [[R(d, rotation=d.rotation + 0.01), R(d, rotation=d.rotation - 0.2)]]
    if name == "widget":
        return [[R(d, slot=d.slot + 1), R(d, slot=d.slot + 17)]]
    raise KeyError(name)


def _fields_of(screen: str, d: Any) -> Tuple[str, ...]:
    if screen == "title":
        if d.kind == "widget":
            return TITLE_WIDGET_FIELDS
        return TITLE_TEX_FIELDS + (("rect",) if d.rect is not None else ())
    return {"text": CHOICE_TEXT_FIELDS, "hit": CHOICE_HIT_FIELDS}.get(d.kind, CHOICE_TEX_FIELDS)


def _sensitivity(screen: str, ctx: Ctx, views: Sequence[Any], listfn: Any, checkfn: Any) -> Dict[str, List[int]]:
    """field -> [mutations, noticed by some delta, noticed by the first delta], over every oracle draw of every view."""
    stat: Dict[str, List[int]] = {}
    for v in views:
        for d in listfn(v):
            names = [n for f in _fields_of(screen, d) for n in (("x", "y") if f == "pos" else (f,))]
            for name in names:
                if name == "widget" and d.kind == "widget":
                    continue                 # a title widget's id comes from the draw's note: there is nothing to perturb
                for group in mutants(d, name):
                    row = stat.setdefault(name, [0, 0, 0])
                    row[0] += 1
                    for n, md in enumerate(group):
                        chk = Checker()
                        checkfn(ctx, v, md, chk)
                        if name in chk.bad_fields:
                            row[1] += 1
                            row[2] += 1 if n == 0 else 0
                            break
    return stat


@needs_game
def test_differential_check_is_sensitive_to_every_compared_field(title, choice) -> None:
    """Negative control over all draws of all fixed views: perturb one field of one oracle draw (two or
    three different wrong values) and require that the field's check reports it.  A field the check does
    not compare would show up here as unnoticed."""
    for screen, ctx, views, listfn, checkfn in (
            ("title", title, TITLE_VIEWS, sf.title_draw_list, check_title_draw),
            ("choice", choice, CHOICE_VIEWS, sf.choice_draw_list, check_choice_draw)):
        stat = _sensitivity(screen, ctx, views, listfn, checkfn)
        for name, (total, noticed, _first) in sorted(stat.items()):
            assert noticed == total, (screen, name, stat)
        want = {"title": {"slot", "flags", "blend", "canvas", "scale", "x", "y", "alpha", "rgb", "rot", "rect", "wscale",
                          "passcanvas"},
                "choice": {"slot", "flags", "blend", "canvas", "scale", "x", "y", "alpha", "rgb", "rot", "widget",
                           "passcanvas"}}[screen]
        assert want <= set(stat), (screen, set(stat))


# ---------------------------------------------------------------------------
# entries and processes: the roadmap's entry VAs are not instruction boundaries
# ---------------------------------------------------------------------------
# -- the system menu --------------------------------------------------------
@pytest.fixture(scope="module")
def sysmenu(img):
    return U.run_entry(img, U.SYSMENU_ENTRY)


@needs_game
def test_sysmenu_entry_registers_the_three_menu_processes(sysmenu) -> None:
    run = sysmenu
    assert run.func == 0x67530                                    # the function that contains 0x6758E
    tables = {t for _s, t, _f in run.tables}
    assert set(U.SYSMENU_TABLES) <= tables
    procs = {p.id: (p.prio, p.fn, p.exit, p.registered_in) for p in run.processes}
    assert procs[0x10] == (0x53, 0x6BF10, 0x6C560, 0x67530)       # backdrop, shadows, petals
    assert procs[0x11] == (0x54, 0x6CB80, 0x6D1F0, 0x6CA90)       # menu body
    assert procs[0x12] == (0x55, 0x6E810, 0x6EA20, 0x6E690)       # mode panel
    # Game_StartProcesses also registers the other screens' processes: config 0x3F140, save / load 0x70340
    assert procs[0x15][1] == 0x3F140 and procs[0x16][1] == 0x70340


@needs_game
def test_sysmenu_entries_are_inside_the_lea_before_the_call(img, sysmenu) -> None:
    """The roadmap's entry VAs are not instruction boundaries and not calls.  0x6758E is the 32-bit
    displacement of the 7-byte `lea rdi, [0x7A67F0]` at 0x6758B (then `mov esi, 1`; the UiTableLoad call is
    at 0x67597).  0x6CA83 and 0x6E683 are the displacements of the `lea rdi, [table]` that starts the
    16-byte helpers 0x6CA80 and 0x6E680 (`lea; xor esi, esi; jmp UiTableLoad`)."""
    run = sysmenu
    assert run.entry == 0x6758E and run.insn is not None
    assert run.insn[:2] == (0x6758B, 7) and run.insn[2].startswith("lea rdi, [rip + ")
    assert run.entry != run.insn[0]                                # not an instruction boundary
    assert run.load == (0x67597, 0x7A67F0, 1)
    assert U.entry_instruction(img, 0x67592) == (0x67592, 5, "mov esi, 1")
    assert U.entry_instruction(img, 0x67597)[:2] == (0x67597, 5)     # the call itself
    for entry, helper, table in ((0x6CA83, 0x6CA80, 0x7A6B20), (0x6E683, 0x6E680, 0x7A7680)):
        r = U.run_entry(img, entry)
        assert r.func == helper and r.insn is not None and r.insn[:2] == (helper, 7)
        assert r.tables == [(helper + 9, table, 0)] and r.load == r.tables[0] and not r.processes
        (c,) = r.traces[helper].calls
        assert c.kind == "jmp" and c.target == 0xC2AF0 and c.args["rsi"] == 0       # tail call, force = 0
    # the CLI report says it
    text = "\n".join(U.format_entry_run(run, U.SYSMENU_PROCESSES))
    assert "is inside the 7-byte instruction" in text and "call is at 0x67597" in text


@needs_game
def test_process_register_fifth_argument_is_the_exit_callback(img, sysmenu) -> None:
    """engine.md 3.1 names the fifth ProcessRegister argument `draw`.  The code uses it as an exit callback:
    ProcessRegister (0xECE30) stores rcx at +0x10 and r8 at +0x18 of the 0x48-byte entry; re-registering an
    active entry, the unregister function 0xECEE0 and the shutdown loop 0xED160 all CALL the old +0x18
    value; and the sysmenu's +0x18 function 0x6C560 frees the table 0x7A67F0 (it cannot run every frame)."""
    from eboot.disasm import disasm_range

    def text(lo: int, hi: int) -> Dict[int, str]:
        return {i.addr: f"{i.mnem} {i.ops}" for i in disasm_range(img, lo, hi)}
    reg = text(0xECE30, 0xECEE0)
    assert reg[0xECEC4] == "mov qword ptr [rbx + r13*8 + 0x10], rcx"
    assert reg[0xECEC9] == "mov qword ptr [rbx + r13*8 + 0x18], r8"
    assert reg[0xECE60] == "mov rax, qword ptr [rbx + r13*8 + 0x18]" and reg[0xECE72] == "call rax"
    un = text(0xECEE0, 0xECF3E)
    assert un[0xECF03] == "mov rax, qword ptr [r15 + r14*8 + 0x18]" and un[0xECF0D] == "call rax"
    sh = text(0xED160, 0xED1A6)
    assert sh[0xED193] == "mov rax, qword ptr [rbx + r14 + 0x18]" and sh[0xED19D] == "call rax"
    assert U.CALLEES[0xECE30].args[4] == ("exit", "r8")
    frees = [c for c in sysmenu.traces[0x6C560].calls if c.target == 0xEBCF0]
    assert [c.args["rdi"] for c in frees] == [0x7A67F0]


@needs_game
def test_sysmenu_draws_the_menu_texture_slots(sysmenu) -> None:
    """Menu parts tables 0x7A6B20 (menu 0..7 -> slots 320..327) and 0x7A67F0 (menu 8..17 -> 328..337)
    and the mode-panel table 0x7A7680 (slots 382, 383): the process functions draw them."""
    drawn = set()
    for f in (0x6BF10, 0x6C640, 0x6CB80, 0x6E810):
        for s in U.draw_sites(sysmenu.traces[f]):
            for v in U.try_all(s.args.get("slot"), U.Env()) or []:
                drawn.add(v)
    assert set(range(0x141, 0x148)) <= drawn                      # menu 1..7 (321..327)
    assert set(range(0x149, 0x152)) <= drawn                      # menu 9..17 (329..337)
    assert 0x17E in drawn                                         # mode panel background (382)


@needs_game
def test_sysmenu_body_layout(sysmenu) -> None:
    """The menu body 0x6CB80: parts at constant canvas positions, five item widgets 0xC1..0xC5."""
    sites = {s.va: s for s in U.draw_sites(sysmenu.traces[0x6CB80])}
    env = U.Env()
    pos = {va: (U.ev(sites[va].args["x"], env), U.ev(sites[va].args["y"], env))
           for va in (0x6CC63, 0x6CC7C, 0x6CC99, 0x6CCB6)}
    assert pair_has([pos[0x6CC63]], (18.6667, 1.24444)) and pair_has([pos[0x6CC7C]], (0.0, 0.0))
    assert pair_has([pos[0x6CC99]], (250.667, 0.0)) and pair_has([pos[0x6CCB6]], (0.0, 29.8667))
    widgets = [(U.ev(s.args["widget"], env), U.ev(s.args["x"], env), U.ev(s.args["y"], env))
               for s in sorted(sites.values(), key=lambda s: s.va) if s.fn == "WidgetDraw"]
    assert [w[0] for w in widgets] == [0xC1, 0xC2, 0xC3, 0xC4, 0xC5]
    assert all(close(w[1], 26.6667) for w in widgets)
    ys = [w[2] for w in widgets]
    assert all(close(b - a, 31.1111, 1e-3) for a, b in zip(ys, ys[1:]))      # item pitch 31.11 canvas units
    for va, s in sites.items():
        assert U.try_all(s.draw["canvas"], env) == [(640.0, 448.0, 0.0, 0.0)]
        assert U.try_all(s.draw["scale"], env) in ([(U.f32(1 / 3), U.f32(448 / 1080))], None) or va in (0x6CFFE,)
    rect = sites[0x6CD18].args["rect"]                            # item rectangle: one of three tables, by item
    assert U.fmt(rect).startswith("({0x7a6e70 | 0x7a6fa0 | 0x7a70d0}")


@needs_game
def test_sysmenu_report_counts(sysmenu) -> None:
    """Numbers recorded in docs/status/wave2/G.md: per function, (draw sites, sites whose x and y
    are constants).  The other positions are a constant plus an animation variable (0xE2377C /
    0xE23784 menu fade and slide, 0xE245F8 mode-panel scroll)."""
    out = {}
    for f in (0x6BF10, 0x6C640, 0x6CB80, 0x6E810):
        sites = U.draw_sites(sysmenu.traces[f])
        const = [s for s in sites if all(U.const_args(s).get(k) is not None for k in ("x", "y") if k in s.args)]
        out[f] = (len(sites), len(const))
    assert out == {0x6BF10: (14, 2), 0x6C640: (1, 0), 0x6CB80: (13, 11), 0x6E810: (9, 0)}, out


@needs_game
def test_title_background_redraw_row_table(img) -> None:
    """0xB29D0 redraws the background from the row in u16[0x218DE0 + 2 * lang] downwards (the particles
    are drawn under it): the oracle's BG_REDRAW_Y, here read through the extracted expression."""
    tr = U.decode_function(img, 0xB29D0)
    (s,) = U.draw_sites(tr)
    assert s.va == 0xB2AC5 and s.fn == "TexDrawRect2"
    for lang in (0, 1):
        env = U.Env(img, {0x873908: 0x7D}, {"lang": lang})
        assert U.ev(s.args["x"], env) == 0.0
        assert close(U.ev(s.args["y"], env) * 2, sf.BG_REDRAW_Y[lang])        # y = row * 540 / 1080
        assert U.ev(s.args["slot"], env) == 0x7D                                # the slot the still / movie used
        assert U.ev(s.draw["canvas"], env) == (960.0, 540.0, 0.0, 0.0)
