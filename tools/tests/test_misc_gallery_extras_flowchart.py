"""Tests for tsuki.misc_gallery_extras_flowchart (gallery, extras, flowchart, unlock data).

Real-data tests read eboot.bin / allpac3 and skip when the game folder is missing.  The
flowchart filter is compared with the engine's own code under unicorn when unicorn is
installed (tsuki.misc_gallery_extras_flowchart_emu)."""
from __future__ import annotations

import struct
from collections import Counter
from typing import Dict, Tuple

import pytest

from tsuki import game_dir
from tsuki import misc_gallery_extras_flowchart as gx

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None, reason="game folder not found (TSUKI_GAME_DIR)")


@pytest.fixture(scope="module")
def data() -> gx.GalleryData:
    if GAME is None:
        pytest.skip("game folder not found")
    return gx.load()


@pytest.fixture(scope="module")
def eb():
    if GAME is None:
        pytest.skip("game folder not found")
    from eboot import eboot_path
    from eboot.selfelf import Eboot
    e = Eboot.open(eboot_path())
    yield e
    e.close()


# ---------------------------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------------------------
def _raw(cols: Tuple[Tuple[int, Tuple[Tuple[int, int], ...]], ...]) -> gx.FlowRaw:
    words: Dict[int, Tuple[int, int]] = {}
    out = []
    va = 0x1000
    for group, nodes in cols:
        out.append((group, len(nodes), va))
        for j, n in enumerate(nodes + ((0, -1),)):
            words[va + 64 * j] = n
        va += 64 * (len(nodes) + 1)
    return gx.FlowRaw(tuple(out), words)


def test_flow_filter_synthetic() -> None:
    s = gx.UnlockState()
    s.set_scene_done(1)
    s.set_scene_done(2)
    # column 0: scene 1 (new) -> kept; column 1: link marker -1 repeating column 0's scene in
    # row 0 -> the look-ahead reaches the end without a difference -> dropped; column 2:
    # scene 3 not done, nothing lit -> dropped; column 3: scene 2 -> kept.
    raw = _raw(((0, ((1, -1),)), (0, ((-1, -1),)), (0, ((3, -1),)), (0, ((2, -1),))))
    assert gx.flow_filter(raw, s) == [0, 3]
    assert gx.flow_filter(raw, gx.UnlockState()) == []


def test_unlock_state_bits() -> None:
    s = gx.UnlockState()
    s.set_scene_seen(486)
    s.set_scene_done(557)
    s.set_choice_seen(605)
    s.set_bgm_heard(0x207)
    s.set_movie_seen(6)
    assert s.g[110 + 486 // 16] == 1 << (486 % 16)
    assert s.scene_done(557) and not s.scene_done(556) and not s.scene_seen(557)
    assert s.choice_seen(605) and s.g[400 + (605 - 564) // 16] == 1 << ((605 - 564) % 16)
    assert not s.choice_seen(563) and not s.choice_seen(1268)
    assert s.bgm_heard(0x207) and s.seen[(0xC80 + 0x207) >> 3] == 1 << ((0xC80 + 0x207) & 7)
    assert not s.bgm_heard(0x208)
    assert s.movie_bit(1) and s.g[5] == 2


def test_system_block_merge() -> None:
    block = bytearray(0x18AC)
    struct.pack_into("<H", block, 0x144 + 2 * 300, 0x8000)      # scene_done(15)
    block[0x52C] = 0x01                                          # picture bit 0
    block[0x16F4 + 3] = 0x04
    loaded = gx.UnlockState.from_system_block(bytes(block))
    ram = gx.UnlockState()
    ram.set_picture_bit(9)
    ram.set_scene_done(1)
    ram.merge_loaded(loaded)
    assert ram.scene_done(15) and not ram.scene_done(1)          # G replaced
    assert ram.picture_bit(0) and ram.picture_bit(9)             # picture block ORed
    assert ram.choice_taken[3] == 0x04


# ---------------------------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------------------------
@needs_game
def test_cg_lists(data: gx.GalleryData) -> None:
    assert len(data.cg_groups) == 458
    ents = data.cg_entries
    assert len(ents) == 1972
    th = [e.cg_th for e in ents]
    assert len(set(th)) == 1972 and min(th) == 0 and max(th) == 1978
    assert sorted(set(range(1979)) - set(th)) == [335, 337, 911, 1264, 1266, 1269, 1270]
    assert Counter(e.prefix for e in ents) == {" ": 1146, "-": 826}
    assert Counter(g.category for g in data.cg_groups)[0] == 270
    assert sum(1 for e in ents if e.flag) == 6
    assert len(data.cg_aliases) == 105 and all(1 <= len(a.aliases) <= 4 for a in data.cg_aliases)
    keys = {e.name for e in ents}
    assert all(a.key in keys for a in data.cg_aliases)
    assert data.cg_special[0] == "IMG1699.MZP" and data.cg_special[-1] == "IMG4026.MZP"
    assert data.category_flags == {1: (2,), 2: (3,), 3: (4,), 4: (5,), 5: (383,), 6: (208,), 7: (7,),
                                   8: (8,), 9: (9,), 10: (226,), 11: (10,), 12: (11,), 13: (12,), 14: (13,),
                                   15: (275, 276), 16: (14,), 17: (297,), 18: (300,), 19: (309,)}


@needs_game
def test_cg_thumbnail_children(data: gx.GalleryData) -> None:
    """Every CG_TH child is 231x130 except the 11 four-frame ones of the special names."""
    from tsuki.image import parse_header
    from tsuki.mrg import MrgArchive
    from tsuki.mzp import MzpArchive
    with MrgArchive(GAME / "image" / "allpac3") as a:
        arc = MzpArchive(a.read(a.find("GALLERY_PARTS_CG_TH.MRG").index))
        assert arc.count == 1979
        sizes = {i: (h.width, h.height) for i in range(arc.count)
                 for h in [parse_header(arc.child(i).read(0))]}
    special = {e.cg_th for e in data.cg_entries if e.name in data.cg_special}
    assert len(special) == 11
    assert {i for i, s in sizes.items() if s == (231, 520)} == special
    assert Counter(sizes.values()) == {(231, 130): 1968, (231, 520): 11}


@needs_game
def test_cg_unlock_classes(data: gx.GalleryData) -> None:
    u = gx.Unlocks(data, gx.UnlockState())
    direct = alias = neither = 0
    for e in data.cg_entries:
        d = u.seen_index(u.lookup_sprite_table(e.name)) >= 0
        a = e.name in u._alias
        direct += d
        alias += a
        neither += not d and not a
    assert (direct, alias, neither) == (952, 105, 923)
    assert len(data.seen_map) == 2144 and len(u._seen_map) == 718
    assert {m.seen_index for m in data.seen_map} <= set(range(2701, 2918))
    assert not any(u.cg_unlocked(e) for e in data.cg_entries)
    # scene 557 (QA_25) done + every category flag seen: everything unlocks
    s = gx.UnlockState()
    s.set_scene_done(gx.ALL_CG_SCENE)
    for flags in data.category_flags.values():
        s.set_scene_seen(flags[0])
    u = gx.Unlocks(data, s)
    assert all(u.cg_unlocked(e) for e in data.cg_entries)
    assert len(u.cg_main_tiles()) == 458


@needs_game
def test_cg_picture_and_alias_unlock(data: gx.GalleryData) -> None:
    s = gx.UnlockState()
    u = gx.Unlocks(data, s)
    e = data.cg_groups[0].entries[0]                       # ' IMG1685.MZP', category 0
    assert e.name == "IMG1685.MZP" and e.category == 0
    u.mark_picture_seen(u.lookup_picture("IMG1685"))
    assert u.cg_unlocked(e)
    # alias: IMG3757 is unlocked by AOK_A_30_00_N00 shown in any size letter
    key = next(a for a in data.cg_aliases if a.key == "IMG3757.MZP")
    assert key.aliases[0] == "AOK_A_30_00_N00.MZP"
    target = next(x for x in data.cg_entries if x.name == "IMG3757.MZP")
    assert not u.cg_unlocked(target) or not u.category_ok(target.category)
    assert u.lookup_sprite_table("AOK_A_30_00_N00") == -1            # not among the first 9,958 names
    pid = u.lookup_picture("AOK_A_30_00_N00")                         # found by the 'A' size-letter retry
    assert pid == 10294 and u.seen_index(pid) == 2709                 # via the 0x86AA30 map
    u.mark_picture_seen(pid)
    for f in data.category_flags.get(target.category, ()):
        s.set_scene_seen(f)
    assert u.alias_seen("IMG3757.MZP") and u.cg_unlocked(target)


@needs_game
def test_flowchart_tables(data: gx.GalleryData) -> None:
    assert len(data.columns) == 277 and all(c.col == c.index for c in data.columns)
    nodes = data.nodes
    assert len(nodes) == 1286
    real = [n for n in nodes if n.scene > 0]
    assert len(real) == 495 and sorted(n.scene for n in real) == list(range(1, 496))
    assert all(n.fc_th == n.scene - 1 for n in real)                     # 0 unresolved thumbnails
    assert sorted(n.fc_th for n in real) == list(range(495))
    assert all(397 <= n.title_ui <= 887 and 914 <= n.synopsis_ui <= 1408 for n in real)
    assert all(0 <= n.chapter_label <= 27 for n in real)
    assert sorted(n.script_scene for n in real if n.script_scene >= 2000) == list(range(2000, 2008))
    other = [n for n in nodes if n.scene <= 0]
    assert Counter(n.scene < 0 for n in other) == {True: 146, False: 645}
    assert all(n.fc_th == -1 and n.title_ui == -1 for n in other)
    links = [n.link for n in nodes if n.link > 0]
    assert min(links) >= gx.CHOICE_ID_LO and max(links) == 1194


@needs_game
def test_flowchart_filter_matches_engine() -> None:
    pytest.importorskip("unicorn")
    from eboot import eboot_path
    from tsuki.misc_gallery_extras_flowchart_emu import compare
    import faulthandler
    was = faulthandler.is_enabled()
    faulthandler.disable()          # unicorn's mem_map raises a handled SEH exception on Windows
    try:
        n, bad = compare(eboot_path(), trials=10, seed=7)
    finally:
        if was:
            faulthandler.enable()
    assert n == 12 and bad == []


@needs_game
def test_music_room(data: gx.GalleryData, eb) -> None:
    m = data.music
    assert len(m) == 95 and len({t.bgm for t in m}) == 95
    assert all(256 <= t.title_ui <= 357 for t in m)
    assert all(0 <= t.loop_start < t.loop_end for t in m)
    # special unlock immediates in 0x2CBA0
    assert eb.read_va(0x2CC3D, 5) == b"\xbf" + struct.pack("<I", 0x230)
    assert eb.read_va(0x2CC58, 5) == b"\xbf" + struct.pack("<I", 0x232)
    assert {t.bgm for t in m} >= set(gx.MUSIC_SPECIAL)
    s = gx.UnlockState()
    u = gx.Unlocks(data, s)
    t1 = m[0]
    assert t1.bgm == 1 and t1.variants == (2,)
    s.set_bgm_heard(2)
    assert u.music_unlocked(t1)
    lifeline = next(t for t in m if t.bgm == 80)
    assert not u.music_unlocked(lifeline)
    s.set_movie_seen(5)                                                     # op1_arc -> G[5] bit 0
    assert u.music_unlocked(lifeline)


@needs_game
def test_movie_seen_bits(eb) -> None:
    base = 0x2010B0
    targets = {i + 4: (base + struct.unpack("<i", eb.read_va(base + 4 * i, 4))[0]) & 0xFFFFFFFF for i in range(0x22)}
    case_bit = {0x5F74E: 0, 0x5F734: 1, 0x5F72D: 2, 0x5F73B: 3, 0x5F742: 4, 0x5F749: 5}
    got = {mv: case_bit[t] for mv, t in targets.items() if t in case_bit}
    assert got == gx.MOVIE_SEEN_BIT


@needs_game
def test_ending_list(data: gx.GalleryData) -> None:
    x = data.extras
    assert len(x) == 25
    assert sorted(e.script_scene for e in x) == list(range(1000, 1025))
    assert all(e.fc_scene == e.script_scene - 467 for e in x)
    assert sorted(e.message_ui for e in x) == list(range(1472, 1497))
    s = gx.UnlockState()
    u = gx.Unlocks(data, s)
    assert not any(u.extra_unlocked(i) for i in range(25))
    for e in x[:24]:
        s.set_scene_done(e.unlock_a)
    assert all(u.extra_unlocked(i) for i in range(25))
    assert x[24].unlock_a == 557


@needs_game
def test_choice_and_legacy_tables(data: gx.GalleryData) -> None:
    assert data.choice_flags[0] == (-1, -1, -1) and len(data.choice_flags) == 83
    ids = [v for row in data.choice_flags[1:] for v in row if v > 0]
    assert min(ids) >= gx.CHOICE_ID_LO and max(ids) <= 970
    assert len(data.legacy_music) == 55 and len(data.legacy_cg) == 1314
    assert len({c[0] for c in data.legacy_cg}) == 1314
    assert data.sdt_names[0] == "SDTA0414" and data.sdt_names[-1] == "SDTB0524"
    assert data.bgm_names[:2] == ("LLBGM01", "LLBGM02") and data.bgm_names[-1] == "LL_ED"
    assert data.trophy_scenes[0] == 335 and len(data.trophy_scenes) == 27


@needs_game
def test_edge_table(data: gx.GalleryData) -> None:
    e = data.edges
    assert len(e) == 630 == 0x7620 // 0x30
    assert e[0] == (485, 528, (1170,), (1170,))
    assert e[628][:2] == (526, 300) and e[629][:2] == (527, 309)
    assert all(g == f for _, _, g, f in e)
    assert [i for i, x in enumerate(e) if len(x[2]) == 5] == [455]
    ids = {v for x in e for v in x[2]}
    assert min(ids) == 565 and max(ids) == 1194
    node_links = {n.link for n in data.nodes if n.link > 0}
    choice_ids = {v for row in data.choice_flags for v in row if v > 0}
    assert node_links <= ids | choice_ids
