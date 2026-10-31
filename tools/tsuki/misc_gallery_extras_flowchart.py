"""Gallery, extras, flowchart and unlock data of TSUKIHIME (PS4 CUSA47999, app 01.01).

Read-only extractors for every list the gallery screens use, with record layouts taken from
the reading code (eboot VAs below), plus a model of the engine's unlock tests and of the
flowchart column filter.  Format document: ``docs/formats/gallery.md``.

Lists (all inside eboot.bin; text VA = file - 0x2280, RELRO VA 0x400000 = file 0x32C210,
data VA = file - 0x365FA0 + 0x600000):

    CG groups        relro 0x400830   458 x 32 B   {entry*, i32 category, 0[4]}   (0x1C550/0x1C570, count 0x1C530)
    CG entries       relro 0x405240.. 1972 x 32 B  {char* name, i32 cg_th, i32 x, i32 y, i32 w, i32 h, i32 flag}
    CG aliases       relro 0x404170   105 x 0x28   {char* key, char* alias[4]}    (0x1C590, count 0x1C540)
    CG special names relro 0x4051E0   11 x char*   (0x1C3B0, 0x1BC4C..)
    seen-index map   data  0x86AA30   2144 x 16 B  {char* name, i32 id (runtime), i32 seen_index}  (0x95A60)
    flowchart        relro 0x418770   277 x 32 B   {i32 col, i32 group, i32 count, node*}  (0x21430)
    flowchart nodes  text  0x18FC90.. 1286 x 64 B  {scene, conn, link, script_scene, chapter_label, ord,
                                                    fc_th, title_ui, synopsis_ui, runtime[7]}
    music room       text  0x1A47D0   95 x 0x38    (0x2CBA0)
    ending list      text  0x18F810   25 x 20 B    {script_scene, fc_scene, unlock_a, unlock_b, msg}  (0x1F690, 0x1FB40)
    choice flags     text  0x2214E0   83 x 3 x i32 (0xB8620, called by _SELD)
    help messages    text  0x2D2D40   55 x i32 ui_text ids (0xC2A20)
    trophy scenes    text  0x221400   27 x {i32 scene, -1} (0xB7CC0)
    legacy (dead)    text  0x209510 music 55 x 16 B; relro 0x41C530 -> data 0x78C810 CG 1314 x 56 B

Unlock bitsets (RAM; system save block offsets in brackets):

    G[110..145] (+0x144+2*110)  scene_seen   bit n   set by _RDST(n)      test 0xB81A0
    G[300..335]                 scene_done   bit n   set by _RDED(n)      test 0xB7B50
    G[400..443]                 choice_seen  bit id-564 (ids 564..1267)   test 0xB7B80, set 0xB8520/0xB8620
    G[5]                        movie_seen   bits 0..5 (movies 5,6,4,35,36,37)  set 0x5F6F0, test 0xB7CA0
    0x164D5C0 [+0x52C], bits 0..3199     picture seen  set 0x95CA0 (_STCG), 0x7DD10, 0x85F90; test 0x95D60
                        bits 3200..3719  BGM heard     set 0x87250 (_MPLY), 0x87630; test 0x95E20

CLI:  python -m tsuki.misc_gallery_extras_flowchart export [--out DIR] [--sheets]
      python -m tsuki.misc_gallery_extras_flowchart summary
"""
from __future__ import annotations

import csv
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Set, Tuple, Union

# ---------------------------------------------------------------------------------------------
# addresses (CUSA47999 v01.01)
# ---------------------------------------------------------------------------------------------
CG_GROUP_VA = 0x400830
CG_GROUP_COUNT = 458            # 0x1C530: mov eax, 0x1CA
CG_ALIAS_VA = 0x404170
CG_ALIAS_COUNT = 105            # 0x1C540: mov eax, 0x69
CG_SPECIAL_VA = 0x4051E0
CG_SPECIAL_COUNT = 11           # 0x1C3B0 compares IMG1699 + 10 more (loop bound 0xA)
CG_CATEGORY_FUNC = 0x1C460
CG_CATEGORY_JUMPTABLE = 0x188380
CG_CATEGORY_JOIN = 0x1C51B      # common "call 0xB81A0" of the category switch
SEEN_MAP_VA = 0x86AA30
FLOW_COLUMN_VA = 0x418770
FLOW_COLUMN_COUNT = 0x115       # 277 (loop bound at 0x214B3)
MUSIC_VA = 0x1A47D0
MUSIC_COUNT = 95                # 0x2CBAB: cmp edi, 0x5E
EXTRA_VA = 0x18F810
EXTRA_COUNT = 25
CHOICE_FLAG_VA = 0x2214E0
CHOICE_FLAG_COUNT = 0x53        # 0xB8620: valid choice ids 1..0x52
HELP_MSG_VA = 0x2D2D40
HELP_MSG_COUNT = 0x37
TROPHY_SCENE_VA = 0x221400
TROPHY_SCENE_COUNT = 0x1B
EDGE_VA = 0x2218D0              # scene-transition edge flags (0xBA3C0), 0x30-byte records
EDGE_COUNT = 0x7620 // 0x30     # 630: rax runs from -0x7620 to 0 in steps of 0x30 (first match wins)
LEGACY_MUSIC_VA = 0x209510
LEGACY_MUSIC_COUNT = 55
LEGACY_CG_PTRS_VA = 0x41C530
LEGACY_CG_PTRS_COUNT = 444
SDT_NAMES_VA = 0x869F80
SDT_NAMES_COUNT = 63
BGM_NAMES_VA = 0x7A8820
BGM_NAMES_COUNT = 22
PICTURE_NAMES_VA = 0x7F6800     # names_allpac2 (14,744 + empty), NameTable_Lookup 0x78410
SPRITE_NAMES_COUNT = 9958       # names_sprites 0x7A8B10 == first 9958 picture names
IMAGE_TABLE_VA = 0x671900       # 0x48-byte records, +0x3F bit 0 = "_HH_VV" variant

SEEN_DIRECT_LIMIT = 0xA8D       # ids below 2701 are their own seen index (0x95CCF)
SEEN_BITS = 0xC80               # 3200 picture bits (cmp ebx, 0xC7F)
BGM_BIT_BASE = 0xC80            # 0x95E20 / 0x87630: bit 0xC80 + track, track <= 0x207
BGM_MAX_TRACK = 0x207
SEEN_BLOCK_SIZE = 0x20C         # RAM 0x164D5C0, system block +0x52C (OR-merged on load)
CHOICE_TAKEN_SIZE = 0x118       # RAM 0x164FBC0, system block +0x16F4 (0x110 bytes OR-merged)
SCENE_FLAG_LIMIT = 0x23F        # 0..575 for scene_seen / scene_done
CHOICE_ID_LO, CHOICE_ID_HI = 0x234, 0x4F3   # 564..1267 (0xB7B80: id-0x4F4 >= -0x2C0)
ALL_CG_SCENE = 0x22D            # 557 = QA_25: scene_done(557) unlocks every CG (0x13692)

G_SCENE_SEEN = 110              # G word index of bit 0 (byte +0xDC)
G_SCENE_DONE = 300              # +0x258
G_CHOICE_SEEN = 400             # +0x320
G_MOVIE = 5                     # +0xA

# movie gallery: item -> (movie id = item + 4 (0x2BD72), G[5] bit required or None) (0x2B610)
MOVIE_ITEMS: Tuple[Tuple[int, int, Optional[int]], ...] = ((0, 4, None), (1, 5, 0), (2, 6, 1))
# G[5] bit set when a movie finishes (0x5F6F0, jump table 0x2010B0 indexed by movie id - 4)
MOVIE_SEEN_BIT: Dict[int, int] = {5: 0, 6: 1, 4: 2, 35: 3, 36: 4, 37: 5}
# music room records whose unlock is not "BGM heard" alone (0x2CC2B..0x2CC8D)
MUSIC_SPECIAL: Dict[int, Tuple[str, int]] = {80: ("movie_bit", 0), 124: ("movie_bit", 1),
                                             82: ("scene_seen", 0x230), 126: ("scene_seen", 0x232)}
# gallery menu (cursor 0..5, GALLERY_PARTS_COMMON child 1) -> screen factory 0x18390
GALLERY_MENU: Tuple[Tuple[int, str, str], ...] = (
    (0, "CG Gallery", "cGalleryCG 0x18FB0"), (1, "Music Player", "cGalleryMusic 0x2C220"),
    (2, "Flowchart", "cFlowchart 0x20960 (needs 0x26430(1))"), (3, "Movie", "cGalleryMovie 0x2A970"),
    (4, "Ending List", "cGalleryEnding 0x1E890"), (5, "Title", "0x13360(gallery, 1): result 1"))
ARCHIVE_SCENE = 3000            # 0x68D50: gallery replay starts script scene 3000 (ARCHIVE), G[8] = target


# ---------------------------------------------------------------------------------------------
# records
# ---------------------------------------------------------------------------------------------
@dataclass(frozen=True)
class CgEntry:
    group: int
    index: int                  # position inside the group
    va: int
    name: str                   # without the 1-char prefix (code always uses name+1)
    prefix: str                 # ' ' or '-' (never read by code)
    cg_th: int                  # GALLERY_PARTS_CG_TH child (0x1BB13 -> 0xEB350)
    x: int                      # +0x0C, +0x10: copied into the photo viewer (0x1CF1E) [H: view offset]
    y: int
    w: int                      # +0x14, +0x18: viewer image size (pivot w/2, h/2; fit 1920/w, 1080/h)
    h: int
    flag: int                   # +0x1C byte: viewer widget vfunc +0xC0(1) when set (0x1CFA8) [H]
    category: int               # of the group


@dataclass(frozen=True)
class CgGroup:
    index: int
    va: int
    category: int
    entries: Tuple[CgEntry, ...]


@dataclass(frozen=True)
class CgAlias:
    index: int
    key: str
    aliases: Tuple[str, ...]


@dataclass(frozen=True)
class SeenMapEntry:
    index: int
    name: str
    seen_index: int


@dataclass(frozen=True)
class FlowNode:
    column: int                 # static column index (0..276)
    slot: int                   # row inside the column
    va: int
    scene: int                  # +0x00: >0 scene id (unlock scene_done), <0 link marker to -scene, 0 empty
    conn: int                   # +0x04: connector bits (-1 none) [H: line shape]
    link: int                   # +0x08: choice-flag id 564..1194 (choice_seen), -1 none
    script_scene: int           # +0x0C: _JUMP target started by the replay (ARCHIVE 3000 via G[8])
    chapter_label: int          # +0x10: rect index into 0x1A3EE0 (ja/zc/zt) / 0x1A3FD0 (en), sheet FC child 6
    order: int                  # +0x14: [H] unused by the flowchart code found
    fc_th: int                  # +0x18: GALLERY_PARTS_FC_TH child (= scene - 1 for every real node)
    title_ui: int               # +0x1C: ui_text id of the scene title (= 396 + F022 of the script)
    synopsis_ui: int            # +0x20: ui_text id of the synopsis


@dataclass(frozen=True)
class FlowColumn:
    index: int
    va: int
    col: int                    # +0x00 (== index)
    group: int                  # +0x04 (0..15) [H: chapter band]; look-ahead stops at a higher group
    count: int                  # +0x08
    nodes_va: int               # +0x10
    nodes: Tuple[FlowNode, ...]


@dataclass(frozen=True)
class MusicTrack:
    index: int                  # position in the music room list
    bgm: int                    # +0x00 bgm.hed index; unlock bit 0xC80 + bgm
    title_ui: int               # +0x04
    vocal_ui: int               # +0x08 credit ids (-1 none): ui 246 "Vocals:" etc.
    lyrics_ui: int              # +0x0C
    composer_ui: int            # +0x10
    arranger_ui: int            # +0x14
    length_ms: int              # +0x18
    loop_start: int             # +0x1C loop start sample (= RIFF smpl start in 94 of 95)
    loop_end: int               # +0x20 loop end sample (= RIFF smpl end in 88 of 95)
    variants: Tuple[int, ...]   # +0x24..+0x34: other bgm indices that also unlock it (-1 dropped)


@dataclass(frozen=True)
class ExtraScenario:
    index: int                  # table row (ending list cursor: row0 col c -> c, row r>0 -> c + 8r - 5)
    script_scene: int           # +0x00: 1000..1024 (QA_01..QA_25) passed to the replay
    fc_scene: int               # +0x04: 533..557 (the script's _RDST id)
    unlock_a: int               # +0x08: scene_done(unlock_a) or scene_done(unlock_b) unlocks
    unlock_b: int               # +0x0C
    message: int                # +0x10: help-message index -> ui_text via 0x2D2D40 ("Play ...?")
    message_ui: int


@dataclass(frozen=True)
class GalleryData:
    cg_groups: Tuple[CgGroup, ...]
    cg_aliases: Tuple[CgAlias, ...]
    cg_special: Tuple[str, ...]
    category_flags: Dict[int, Tuple[int, ...]]      # category -> scene_seen ids (any)
    seen_map: Tuple[SeenMapEntry, ...]
    columns: Tuple[FlowColumn, ...]
    music: Tuple[MusicTrack, ...]
    extras: Tuple[ExtraScenario, ...]
    choice_flags: Tuple[Tuple[int, int, int], ...]  # index = choice id (0 unused)
    edges: Tuple[Tuple[int, int, Tuple[int, ...], Tuple[int, ...]], ...]   # (from, to, g ids, f ids)
    help_messages: Tuple[int, ...]
    trophy_scenes: Tuple[int, ...]
    legacy_music: Tuple[Tuple[int, int, int, int], ...]
    legacy_cg: Tuple[Tuple[str, int, int, float, float], ...]
    sdt_names: Tuple[str, ...]
    bgm_names: Tuple[str, ...]
    picture_names: Tuple[str, ...] = field(repr=False, default=())
    picture_variant: Tuple[bool, ...] = field(repr=False, default=())

    # -- derived lookups --------------------------------------------------------------------
    @property
    def cg_entries(self) -> List[CgEntry]:
        return [e for g in self.cg_groups for e in g.entries]

    @property
    def nodes(self) -> List[FlowNode]:
        return [n for c in self.columns for n in c.nodes]


# ---------------------------------------------------------------------------------------------
# loading
# ---------------------------------------------------------------------------------------------
class _Reader:
    """VA reads plus relocation-checked pointers (R_X86_64_RELATIVE)."""

    def __init__(self, eboot_path: Union[str, Path]) -> None:
        from eboot.selfelf import R_X86_64_RELATIVE, Eboot
        self.eb = Eboot.open(Path(eboot_path))
        self.rel: Dict[int, int] = {r.offset: r.addend for r in self.eb.rela() if r.type == R_X86_64_RELATIVE}

    def close(self) -> None:
        self.eb.close()

    def read(self, va: int, n: int) -> bytes:
        return self.eb.read_va(va, n)

    def i32s(self, va: int, n: int) -> Tuple[int, ...]:
        return struct.unpack(f"<{n}i", self.read(va, 4 * n))

    def ptr(self, slot: int) -> int:
        """Pointer stored at ``slot`` (0 = NULL).  A non-zero value must carry a relocation."""
        raw = struct.unpack("<Q", self.read(slot, 8))[0]
        if raw == 0:
            if slot in self.rel and self.rel[slot] != 0:
                raise ValueError(f"slot 0x{slot:x}: zero in file but relocated to 0x{self.rel[slot]:x}")
            return 0
        if self.rel.get(slot) != raw:
            raise ValueError(f"slot 0x{slot:x}: file 0x{raw:x}, relocation {self.rel.get(slot)}")
        return raw

    def cstr(self, va: int, limit: int = 64) -> str:
        return self.eb.cstr(va, limit).decode("ascii")


def _decode_category_flags(r: _Reader) -> Dict[int, Tuple[int, ...]]:
    """Category 1..19 -> scene_seen ids from the switch at 0x1C460 (jump table 0x188380).
    Each case loads ``mov edi, imm32`` (bf imm32) once, or twice with an early-out
    ``call 0xB81A0; test al, al; jne`` between them (category 15 = 0x113 or 0x114)."""
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    out: Dict[int, Tuple[int, ...]] = {}
    for i in range(19):
        target = (CG_CATEGORY_JUMPTABLE + struct.unpack("<i", r.read(CG_CATEGORY_JUMPTABLE + 4 * i, 4))[0]) & 0xFFFFFFFF
        flags: List[int] = []
        for ins in md.disasm(r.read(target, 64), target):
            if ins.mnemonic == "mov" and ins.op_str.startswith("edi, "):
                flags.append(int(ins.op_str.split(", ")[1], 0))
            elif ins.mnemonic == "jmp" or ins.address >= CG_CATEGORY_JOIN:
                break
            elif ins.mnemonic == "call" and int(ins.op_str, 0) != 0xB81A0:
                raise ValueError(f"category {i + 1}: unexpected call at 0x{ins.address:x}")
        if not flags:
            raise ValueError(f"category {i + 1}: no flag at 0x{target:x}")
        out[i + 1] = tuple(flags)
    return out


def load(eboot_path: Union[str, Path, None] = None) -> GalleryData:
    """Extract every gallery / extras / flowchart list from eboot.bin."""
    if eboot_path is None:
        from eboot import eboot_path as _ep
        p = _ep()
        if p is None:
            raise FileNotFoundError("eboot.bin not found (TSUKI_GAME_DIR)")
        eboot_path = p
    r = _Reader(eboot_path)
    try:
        return _load(r)
    finally:
        r.close()


def _load(r: _Reader) -> GalleryData:
    # CG groups and entries -------------------------------------------------------------------
    groups: List[CgGroup] = []
    for g in range(CG_GROUP_COUNT):
        va = CG_GROUP_VA + 32 * g
        lst = r.ptr(va)
        category = r.i32s(va + 8, 1)[0]
        if r.read(va + 12, 20) != bytes(20):
            raise ValueError(f"CG group {g}: unexpected non-zero tail")
        entries: List[CgEntry] = []
        e = lst
        while True:
            name_ptr = r.ptr(e)
            if name_ptr == 0:
                break
            raw = r.cstr(name_ptr)
            th, x, y, w, h, flag = r.i32s(e + 8, 6)
            entries.append(CgEntry(g, len(entries), e, raw[1:], raw[0], th, x, y, w, h, flag, category))
            e += 32
        groups.append(CgGroup(g, va, category, tuple(entries)))
    aliases: List[CgAlias] = []
    for i in range(CG_ALIAS_COUNT):
        va = CG_ALIAS_VA + 0x28 * i
        names = [r.ptr(va + 8 * k) for k in range(5)]
        al = tuple(r.cstr(p) for p in names[1:] if p)
        aliases.append(CgAlias(i, r.cstr(names[0]), al))
    special = tuple(r.cstr(r.ptr(CG_SPECIAL_VA + 8 * i)) for i in range(CG_SPECIAL_COUNT))
    if r.ptr(CG_SPECIAL_VA + 8 * CG_SPECIAL_COUNT) != 0:
        raise ValueError("special CG name list longer than 11")
    # seen-index map (0x95A60 walks until a NULL name) ---------------------------------------
    seen: List[SeenMapEntry] = []
    va = SEEN_MAP_VA
    while True:
        p = r.ptr(va)
        if p == 0:
            break
        _, idx = r.i32s(va + 8, 2)
        seen.append(SeenMapEntry(len(seen), r.cstr(p), idx))
        va += 16
    # flowchart -------------------------------------------------------------------------------
    columns: List[FlowColumn] = []
    for c in range(FLOW_COLUMN_COUNT):
        va = FLOW_COLUMN_VA + 32 * c
        col, group, count, _pad = r.i32s(va, 4)
        nva = r.ptr(va + 16)
        nodes = []
        for j in range(count):
            f = r.i32s(nva + 64 * j, 16)
            if any(f[9:]):
                raise ValueError(f"flow node {c}/{j}: runtime fields not zero")
            nodes.append(FlowNode(c, j, nva + 64 * j, *f[:9]))
        columns.append(FlowColumn(c, va, col, group, count, nva, tuple(nodes)))
    # music room ------------------------------------------------------------------------------
    music: List[MusicTrack] = []
    for i in range(MUSIC_COUNT):
        f = r.i32s(MUSIC_VA + 0x38 * i, 14)
        music.append(MusicTrack(i, *f[:9], tuple(v for v in f[9:] if v >= 0)))
    # ending list (extra scenarios) -----------------------------------------------------------
    help_msgs = r.i32s(HELP_MSG_VA, HELP_MSG_COUNT)
    extras = []
    for i in range(EXTRA_COUNT):
        f = r.i32s(EXTRA_VA + 20 * i, 5)
        extras.append(ExtraScenario(i, *f, help_msgs[f[4]] if 0 <= f[4] < HELP_MSG_COUNT else -1))
    choice = tuple(tuple(r.i32s(CHOICE_FLAG_VA + 12 * i, 3)) for i in range(CHOICE_FLAG_COUNT))
    edges = []
    for i in range(EDGE_COUNT):
        f = r.i32s(EDGE_VA + 0x30 * i, 12)
        edges.append((f[0], f[1], tuple(v for v in f[2:7] if v > 0), tuple(v for v in f[7:12] if v > 0)))
    trophy = tuple(r.i32s(TROPHY_SCENE_VA + 8 * i, 2)[0] for i in range(TROPHY_SCENE_COUNT))
    legacy_music = tuple(tuple(r.i32s(LEGACY_MUSIC_VA + 16 * i, 4)) for i in range(LEGACY_MUSIC_COUNT))
    legacy_cg: List[Tuple[str, int, int, float, float]] = []
    seen_lists: Set[int] = set()
    for i in range(LEGACY_CG_PTRS_COUNT):
        lst = r.ptr(LEGACY_CG_PTRS_VA + 8 * i)
        e = lst
        while lst and e not in seen_lists:
            p = r.rel.get(e, 0)              # a list ends at the first slot without a pointer
            if p == 0:
                break
            seen_lists.add(e)
            w, h = r.i32s(e + 8, 2)
            sx, sy = struct.unpack("<2f", r.read(e + 16, 8))
            legacy_cg.append((r.cstr(p), w, h, sx, sy))
            e += 56
    sdt = tuple(r.cstr(SDT_NAMES_VA + 32 * i, 32) for i in range(SDT_NAMES_COUNT))
    bgm = tuple(r.cstr(BGM_NAMES_VA + 32 * i, 32) for i in range(BGM_NAMES_COUNT))
    # picture names (names_allpac2) and the variant flag of the image table -----------------
    pnames: List[str] = []
    va = PICTURE_NAMES_VA
    while True:
        rec = r.read(va, 32)
        if rec[0] == 0:
            break
        pnames.append(rec[:30].split(b"\0")[0].decode("ascii"))
        va += 32
    variant = tuple(bool(r.read(IMAGE_TABLE_VA + 0x48 * i + 0x3F, 1)[0] & 1) for i in range(len(pnames)))
    return GalleryData(tuple(groups), tuple(aliases), special, _decode_category_flags(r), tuple(seen),
                       tuple(columns), tuple(music), tuple(extras), choice, tuple(edges), help_msgs, trophy,
                       legacy_music, tuple(legacy_cg), sdt, bgm, tuple(pnames), variant)


# ---------------------------------------------------------------------------------------------
# unlock state and tests (models of the engine functions)
# ---------------------------------------------------------------------------------------------
@dataclass
class UnlockState:
    """The persistent unlock bits.  ``g`` holds G[0..499] (u16), ``seen`` the 0x20C-byte
    picture/BGM block (RAM 0x164D5C0), ``choice_taken`` the 0x118-byte block at 0x164FBC0."""
    g: List[int] = field(default_factory=lambda: [0] * 500)
    seen: bytearray = field(default_factory=lambda: bytearray(SEEN_BLOCK_SIZE))
    choice_taken: bytearray = field(default_factory=lambda: bytearray(CHOICE_TAKEN_SIZE))

    @classmethod
    def from_system_block(cls, block: bytes) -> "UnlockState":
        """From the 0x18AC-byte system block of savedata000 (eboot.savedata.SYSTEM_FIELDS)."""
        if len(block) < 0x180C:
            raise ValueError("system block too short")
        g = list(struct.unpack_from("<500H", block, 0x144))
        return cls(g, bytearray(block[0x52C:0x52C + SEEN_BLOCK_SIZE]),
                   bytearray(block[0x16F4:0x16F4 + CHOICE_TAKEN_SIZE]))

    def merge_loaded(self, other: "UnlockState") -> None:
        """SaveImage_LoadSystem (0x6F470): G is replaced (the OR list at 0x86AA14 is empty),
        the picture/BGM block (0x20C bytes) and the choice block (first 0x110 bytes) are ORed."""
        self.g = list(other.g)
        for i in range(SEEN_BLOCK_SIZE):
            self.seen[i] |= other.seen[i]
        for i in range(0x110):
            self.choice_taken[i] |= other.choice_taken[i]

    # -- bit helpers --------------------------------------------------------------------------
    def _gbit(self, word0: int, n: int) -> bool:
        return bool(self.g[word0 + (n >> 4)] >> (n & 15) & 1)

    def _set_gbit(self, word0: int, n: int) -> None:
        self.g[word0 + (n >> 4)] |= 1 << (n & 15)

    def scene_seen(self, n: int) -> bool:            # 0xB81A0
        return 0 <= n <= SCENE_FLAG_LIMIT and self._gbit(G_SCENE_SEEN, n)

    def scene_done(self, n: int) -> bool:            # 0xB7B50
        return 0 <= n <= SCENE_FLAG_LIMIT and self._gbit(G_SCENE_DONE, n)

    def choice_seen(self, cid: int) -> bool:         # 0xB7B80
        return CHOICE_ID_LO <= cid <= CHOICE_ID_HI and self._gbit(G_CHOICE_SEEN, cid - CHOICE_ID_LO)

    def movie_bit(self, bit: int) -> bool:           # 0xB7CA0
        return 0 <= bit <= 15 and bool(self.g[G_MOVIE] >> bit & 1)

    def picture_bit(self, i: int) -> bool:
        return 0 <= i < SEEN_BITS and bool(self.seen[i >> 3] >> (i & 7) & 1)

    def bgm_heard(self, track: int) -> bool:         # 0x95E20
        if not 0 <= track <= BGM_MAX_TRACK:
            return False
        i = BGM_BIT_BASE + track
        return bool(self.seen[i >> 3] >> (i & 7) & 1)

    def set_scene_seen(self, n: int) -> None:        # _RDST (0xB8110)
        if 0 <= n <= SCENE_FLAG_LIMIT:
            self._set_gbit(G_SCENE_SEEN, n)

    def set_scene_done(self, n: int) -> None:        # _RDED success path (0xB7E40)
        if 0 <= n <= SCENE_FLAG_LIMIT:
            self._set_gbit(G_SCENE_DONE, n)

    def set_choice_seen(self, cid: int) -> None:     # 0xB8520
        if CHOICE_ID_LO <= cid <= CHOICE_ID_HI:
            self._set_gbit(G_CHOICE_SEEN, cid - CHOICE_ID_LO)

    def set_picture_bit(self, i: int) -> None:
        if 0 <= i < SEEN_BITS:
            self.seen[i >> 3] |= 1 << (i & 7)

    def set_bgm_heard(self, track: int) -> None:     # 0x87630
        if 0 <= track <= BGM_MAX_TRACK:
            i = BGM_BIT_BASE + track
            self.seen[i >> 3] |= 1 << (i & 7)

    def set_movie_seen(self, movie_id: int) -> None:  # 0x5F6F0 -> 0xB7C80
        if movie_id in MOVIE_SEEN_BIT:
            self.g[G_MOVIE] |= 1 << MOVIE_SEEN_BIT[movie_id]


class Unlocks:
    """Engine unlock tests over :class:`GalleryData` and an :class:`UnlockState`."""

    def __init__(self, data: GalleryData, state: UnlockState) -> None:
        self.d = data
        self.s = state
        self._pidx: Dict[str, int] = {}
        for i, n in enumerate(data.picture_names):
            self._pidx.setdefault(n.upper(), i)
        self._alias = {a.key: a for a in data.cg_aliases}
        self._seen_map: Dict[int, int] = {}
        for m in data.seen_map:                    # 0x95A60: std::map keyed by the looked-up id
            i = self.lookup_picture(m.name)
            if i >= 0:
                self._seen_map[i] = m.seen_index   # operator[]: the last duplicate wins

    # -- names --------------------------------------------------------------------------------
    @staticmethod
    def _key(name: str) -> str:
        """NameTable_Lookup (0x78410): up to 32 chars, stop at NUL or '.', upper-cased."""
        return name[:32].split(".")[0].split("\0")[0].upper()

    def lookup_picture(self, name: str) -> int:
        """0x82CE0: index in names_allpac2 (14,744 names), -1 if absent."""
        return self._pidx.get(self._key(name), -1)

    def lookup_sprite_table(self, name: str) -> int:
        """0xA04B0: index in names_sprites (0x7A8B10 = the first 9,958 picture names), -1 if absent."""
        i = self._pidx.get(self._key(name), -1)
        return i if 0 <= i < SPRITE_NAMES_COUNT else -1

    def resolve_variant(self, pid: int) -> int:
        """Image_ResolveSprite (0x957B0) as used by 0x95CA0/0x95D60: a picture whose image
        table +0x3F bit 0 is set is replaced by the picture named without its ``_HH_VV``
        suffix (-1 if that name is absent)."""
        if pid < 0 or pid >= len(self.d.picture_variant) or not self.d.picture_variant[pid]:
            return pid
        n = self.d.picture_names[pid]
        return self.lookup_picture(n[:-6]) if len(n) > 6 else -1

    def seen_index(self, pid: int) -> int:
        """Bit index in the picture block for picture ``pid``, -1 if it has none."""
        i = self.resolve_variant(pid)
        if i >= SEEN_DIRECT_LIMIT:
            i = self._seen_map.get(i, -1)
        return i if 0 <= i < SEEN_BITS else -1

    def picture_seen(self, pid: int) -> bool:        # 0x95D60
        i = self.seen_index(pid)
        return i >= 0 and self.s.picture_bit(i)

    def mark_picture_seen(self, pid: int) -> None:   # 0x95CA0 (_STCG) and the display paths
        i = self.seen_index(pid)
        if i >= 0:
            self.s.set_picture_bit(i)

    # -- CG -----------------------------------------------------------------------------------
    def category_ok(self, category: int) -> bool:    # 0x1C460
        flags = self.d.category_flags.get(category)
        return True if flags is None else any(self.s.scene_seen(f) for f in flags)

    def alias_seen(self, name: str) -> bool:         # 0x13AB0
        a = self._alias.get(name)
        if a is None:
            return False
        for al in a.aliases:
            i = self.lookup_sprite_table(al)
            if i >= 0 or len(al) < 6:
                return self.picture_seen(i)
            for letter in "ALMS":                    # size letter at index 4 (0x13BC2..0x13C4E)
                if self.picture_seen(self.lookup_picture(al[:4] + letter + al[5:])):
                    return True
        return False

    def cg_unlocked(self, e: CgEntry) -> bool:       # 0x13630
        if not self.category_ok(e.category):
            return False
        if self.alias_seen(e.name):
            return True
        if self.picture_seen(self.lookup_sprite_table(e.name)):
            return True
        return self.s.scene_done(ALL_CG_SCENE)

    def cg_main_tiles(self) -> List[Tuple[CgGroup, CgEntry, int]]:
        """Main CG grid (0x1C620): (group, first unlocked entry, unlocked count) per group."""
        out = []
        for g in self.d.cg_groups:
            un = [e for e in g.entries if self.cg_unlocked(e)]
            if un:
                out.append((g, un[0], len(un)))
        return out

    # -- music / movies / extras ---------------------------------------------------------------
    def music_unlocked(self, t: MusicTrack) -> bool:  # 0x2CBA0 (also marks the main bit heard)
        if self.s.bgm_heard(t.bgm) or any(self.s.bgm_heard(v) for v in t.variants):
            return True
        sp = MUSIC_SPECIAL.get(t.bgm)
        if sp is None:
            return False
        return self.s.movie_bit(sp[1]) if sp[0] == "movie_bit" else self.s.scene_seen(sp[1])

    def movie_unlocked(self, item: int) -> bool:     # 0x2B610
        for it, _movie, bit in MOVIE_ITEMS:
            if it == item:
                return bit is None or self.s.movie_bit(bit)
        return False

    def extra_unlocked(self, i: int) -> bool:        # 0x1FB40 (rows 0) / 0x1FB80 (rows 1..)
        x = self.d.extras[i]
        if i == EXTRA_COUNT - 1:                      # QA_25: every other entry unlocked
            return all(self.extra_unlocked(k) for k in range(EXTRA_COUNT - 1))
        return self.s.scene_done(x.unlock_a) or self.s.scene_done(x.unlock_b)

    # -- flowchart -----------------------------------------------------------------------------
    def node_lit(self, n: FlowNode) -> bool:
        """Visibility test of one node inside 0x21430: scene_done(scene) or choice_seen(link)."""
        return (n.scene > 0 and self.s.scene_done(n.scene)) or (n.link > 0 and self.s.choice_seen(n.link))

    def flowchart_columns(self, raw: "FlowRaw") -> List[int]:
        """Columns kept by the filter of 0x21430 (see :func:`flow_filter`)."""
        return flow_filter(raw, self.s)


@dataclass(frozen=True)
class FlowRaw:
    """Raw view of the flowchart memory for :func:`flow_filter`: the engine reads node slots
    beyond ``count`` (``j > count`` tests), so nodes are fetched by address."""
    columns: Tuple[Tuple[int, int, int], ...]        # (group, count, nodes_va)
    node_words: Dict[int, Tuple[int, int]]           # node va -> (scene, link)

    @classmethod
    def from_eboot(cls, eboot_path: Union[str, Path]) -> "FlowRaw":
        r = _Reader(eboot_path)
        try:
            cols = []
            words: Dict[int, Tuple[int, int]] = {}
            for c in range(FLOW_COLUMN_COUNT):
                va = FLOW_COLUMN_VA + 32 * c
                _col, group, count, _ = r.i32s(va, 4)
                nva = r.ptr(va + 16)
                cols.append((group, count, nva))
                for j in range(max(count, 1) + 1):
                    a, _conn, link = r.i32s(nva + 64 * j, 3)
                    words[nva + 64 * j] = (a, link)
            return cls(tuple(cols), words)
        finally:
            r.close()

    def node(self, c: int, j: int) -> Tuple[int, int]:
        return self.node_words[self.columns[c][2] + 64 * j]


def flow_filter(raw: FlowRaw, s: UnlockState) -> List[int]:
    """Transcription of the column loop of cFlowchartScene build (VA 0x2149E..0x21774).

    For each static column c it counts ``vis`` (lit nodes) and ``diff`` (lit nodes that add
    something new compared with the neighbouring columns) and keeps the column if
    ``vis >= 2 and diff != 0`` or ``vis == 1 and (diff == 1 or count == 1)``.  ``prev`` is the
    previous *static* column whether or not it was kept.  Out-of-range rows are read like the
    engine does (comparisons use ``j > count``)."""
    kept: List[int] = []
    n_cols = len(raw.columns)
    for c in range(n_cols):
        group, count, _ = raw.columns[c]
        rows = count if count >= 2 else 1
        vis = diff = 0
        for j in range(rows):
            a, link = raw.node(c, j)
            if not ((a > 0 and s.scene_done(a)) or (link > 0 and s.choice_seen(link))):
                continue
            vis += 1
            if a > 0:
                diff += 1
                continue
            if a == 0:
                continue
            if c == 0 or j > raw.columns[c - 1][1]:
                diff += 1
                continue
            pa = raw.node(c - 1, j)[0]
            if s.scene_done(-a):
                if abs(pa) != abs(a):
                    diff += 1
                    continue
                if c > 0x113:
                    continue
                for k in range(c + 1, n_cols):
                    kg, kcount, _ = raw.columns[k]
                    if j > kcount:
                        diff += 1
                        break
                    if kg > group:
                        break
                    ka, klink = raw.node(k, j)
                    if ka != a or klink != link:
                        diff += 1
                        break
            else:
                diff += int(pa + a != 0)
        if (vis >= 2 and diff != 0) or (vis == 1 and (diff == 1 or count == 1)):
            kept.append(c)
    return kept


# ---------------------------------------------------------------------------------------------
# ui text helper
# ---------------------------------------------------------------------------------------------
def load_ui_text(eboot_path: Union[str, Path], lang: str = "en") -> List[str]:
    from eboot.selfelf import Eboot
    from eboot.uitext import read_table
    eb = Eboot.open(Path(eboot_path))
    try:
        return [u.text for u in read_table(eb)[lang]]
    finally:
        eb.close()


# ---------------------------------------------------------------------------------------------
# export
# ---------------------------------------------------------------------------------------------
def _write(path: Path, header: Sequence[str], rows: Iterable[Sequence[object]]) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    n = 0
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(header)
        for row in rows:
            w.writerow(row)
            n += 1
    return n


def export(out: Path, eboot_path: Union[str, Path, None] = None, image_dir: Optional[Path] = None) -> Dict[str, int]:
    """Write the CSV tables to ``out``; returns row counts."""
    from eboot import eboot_path as _ep
    ep = Path(eboot_path) if eboot_path else _ep()
    if ep is None:
        raise FileNotFoundError("eboot.bin not found")
    d = load(ep)
    ui = load_ui_text(ep, "en")
    u = Unlocks(d, UnlockState())
    nam = None
    if image_dir is not None:
        from .catalog import ImageNameIndex, IMAGE_SEARCH_ORDER, unpack_image_id
        from .nam import load_nam
        nam = ImageNameIndex({a: load_nam(image_dir / f"{a}.nam") for a in IMAGE_SEARCH_ORDER})

    def where(name: str) -> str:
        if nam is None:
            return ""
        from .catalog import unpack_image_id
        pid = nam.find(name)
        if pid is None:
            return "not found"
        a, i = unpack_image_id(pid)
        return f"{a}:{i}"

    def t(i: int) -> str:
        return ui[i].replace("^", " ") if 0 < i < len(ui) else ""

    counts: Dict[str, int] = {}
    counts["cg_groups.csv"] = _write(out / "cg_groups.csv", ["group", "va", "category", "category_scene_seen", "entries"],
                                     [(g.index, hex(g.va), g.category, "|".join(map(str, d.category_flags.get(g.category, ()))),
                                       len(g.entries)) for g in d.cg_groups])
    counts["cg_entries.csv"] = _write(
        out / "cg_entries.csv",
        ["group", "index", "va", "prefix", "name", "cg_th", "x", "y", "w", "h", "flag", "category", "special",
         "alias_key", "sprite_table_index", "seen_index", "full_image"],
        [(e.group, e.index, hex(e.va), "-" if e.prefix == "-" else "space", e.name, e.cg_th, e.x, e.y, e.w, e.h, e.flag,
          e.category, int(e.name in d.cg_special), int(e.name in u._alias), u.lookup_sprite_table(e.name),
          u.seen_index(u.lookup_sprite_table(e.name)), where(e.name)) for e in d.cg_entries])
    counts["cg_aliases.csv"] = _write(out / "cg_aliases.csv", ["index", "key", "alias1", "alias2", "alias3", "alias4"],
                                      [(a.index, a.key, *(list(a.aliases) + [""] * 4)[:4]) for a in d.cg_aliases])
    counts["seen_map.csv"] = _write(out / "seen_map.csv", ["index", "name", "picture_index", "seen_index"],
                                    [(m.index, m.name, u.lookup_picture(m.name), m.seen_index) for m in d.seen_map])
    counts["flowchart_columns.csv"] = _write(out / "flowchart_columns.csv", ["column", "va", "group", "count", "nodes_va"],
                                             [(c.index, hex(c.va), c.group, c.count, hex(c.nodes_va)) for c in d.columns])
    counts["flowchart_nodes.csv"] = _write(
        out / "flowchart_nodes.csv",
        ["column", "slot", "va", "kind", "scene", "conn", "link", "script_scene", "chapter_label", "order", "fc_th",
         "title_ui", "synopsis_ui", "title_en", "synopsis_en"],
        [(n.column, n.slot, hex(n.va), "scene" if n.scene > 0 else ("link" if n.scene < 0 else "empty"), n.scene,
          n.conn, n.link, n.script_scene, n.chapter_label, n.order, n.fc_th, n.title_ui, n.synopsis_ui,
          t(n.title_ui), t(n.synopsis_ui)) for n in d.nodes])
    counts["music_room.csv"] = _write(
        out / "music_room.csv",
        ["index", "bgm", "title_ui", "title_en", "vocal_ui", "lyrics_ui", "composer_ui", "arranger_ui", "length_ms",
         "loop_start", "loop_end", "variants", "special_unlock"],
        [(m.index, m.bgm, m.title_ui, t(m.title_ui), m.vocal_ui, m.lyrics_ui, m.composer_ui, m.arranger_ui, m.length_ms,
          m.loop_start, m.loop_end, "|".join(map(str, m.variants)),
          "" if m.bgm not in MUSIC_SPECIAL else "%s %d" % MUSIC_SPECIAL[m.bgm]) for m in d.music])
    counts["ending_list.csv"] = _write(
        out / "ending_list.csv",
        ["index", "script_scene", "fc_scene", "unlock_a", "unlock_b", "message", "message_ui", "message_en"],
        [(x.index, x.script_scene, x.fc_scene, x.unlock_a, x.unlock_b, x.message, x.message_ui, t(x.message_ui))
         for x in d.extras])
    counts["choice_flags.csv"] = _write(out / "choice_flags.csv", ["choice_id", "flag1", "flag2", "flag3"],
                                        [(i, *f) for i, f in enumerate(d.choice_flags) if i > 0])
    counts["flowchart_edges.csv"] = _write(out / "flowchart_edges.csv",
                                           ["index", "from_scene", "to_scene", "g_ids", "f_ids"],
                                           [(i, a, b, "|".join(map(str, g)), "|".join(map(str, f)))
                                            for i, (a, b, g, f) in enumerate(d.edges)])
    counts["legacy_music_0x209510.csv"] = _write(out / "legacy_music_0x209510.csv", ["index", "bgm", "page", "position", "group"],
                                                 [(i, *f) for i, f in enumerate(d.legacy_music)])
    counts["legacy_cg_0x78C810.csv"] = _write(out / "legacy_cg_0x78C810.csv", ["index", "name", "w", "h", "sx", "sy"],
                                              [(i, *f) for i, f in enumerate(d.legacy_cg)])
    counts["misc_names.csv"] = _write(out / "misc_names.csv", ["table", "index", "name"],
                                      [("sdt_0x869F80", i, n) for i, n in enumerate(d.sdt_names)]
                                      + [("bgm_names_0x7A8820", i, n) for i, n in enumerate(d.bgm_names)]
                                      + [("cg_special_0x4051E0", i, n) for i, n in enumerate(d.cg_special)])
    return counts


def _summary(d: GalleryData) -> str:
    real = [n for n in d.nodes if n.scene > 0]
    return "\n".join([
        f"CG groups {len(d.cg_groups)}, entries {len(d.cg_entries)}, aliases {len(d.cg_aliases)}, special {len(d.cg_special)}",
        f"seen map {len(d.seen_map)} (seen {min(m.seen_index for m in d.seen_map)}..{max(m.seen_index for m in d.seen_map)})",
        f"flowchart columns {len(d.columns)}, node slots {len(d.nodes)}, scene nodes {len(real)}",
        f"music room {len(d.music)}, ending list {len(d.extras)}, choice ids {len(d.choice_flags) - 1}",
        f"category flags {d.category_flags}",
        f"legacy: music {len(d.legacy_music)}, CG {len(d.legacy_cg)}; SDT names {len(d.sdt_names)}; bgm names {len(d.bgm_names)}",
    ])


def _main(argv: Optional[Sequence[str]] = None) -> int:
    import argparse
    from . import PROJECT_ROOT, image_dir
    ap = argparse.ArgumentParser(prog="python -m tsuki.misc_gallery_extras_flowchart")
    sub = ap.add_subparsers(dest="cmd", required=True)
    ex = sub.add_parser("export", help="write CSV tables (and contact sheets with --sheets)")
    ex.add_argument("--out", type=Path, default=PROJECT_ROOT / "samples" / "misc_gallery_extras_flowchart")
    ex.add_argument("--sheets", action="store_true")
    sub.add_parser("summary")
    a = ap.parse_args(argv)
    if a.cmd == "summary":
        print(_summary(load()))
        return 0
    img = image_dir()
    counts = export(a.out, image_dir=img)
    for k, v in counts.items():
        print(f"{k}: {v} rows")
    if a.sheets:
        if img is None:
            print("game folder missing: no contact sheets", file=sys.stderr)
            return 1
        from .misc_gallery_extras_flowchart_sheets import make_sheets
        for p in make_sheets(a.out, img):
            print("wrote", p)
    return 0


if __name__ == "__main__":
    sys.exit(_main())
