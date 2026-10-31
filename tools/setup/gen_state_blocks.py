"""Generate the state-block contracts of ARCHITECTURE 3.2 (PACE 7.1, package L-1).

Writes src/Tsuki.Engine/Contracts/Generated/*.g.cs: SceneState (0x1A08), HistoryRecord (0x3A10),
SoundChannelRecord (0x34), ConfigBlock (0x140), SystemBlock (0x18AC) and Vars (F[1200], Z[1400],
G[2000]). Each is a StateBlock (Contracts/StateBlock.cs) with typed little-endian accessors and a
`Fields` table that lists every byte of the block once.

Sources (the documented field maps, in their machine-readable form in the Python oracle):

  SceneState          layers.md 3, adv_state.md 2.6  tsuki.misc_adv_system_state.SCENE_STATE_FIELDS
                                                     + tsuki.misc_layer_state_and_motions.SCENE_ARRAYS
  HistoryRecord       adv_state.md 2.1               tsuki.misc_adv_system_state.HISTORY_FIELDS
                                                     + tsuki.vm_vars.HISTORY_U16 / _BOOL / _S8
  SoundChannelRecord  sound_runtime.md 2.2           CHANNEL_FIELDS below, checked against the doc
                                                     table and tsuki.misc_audio_runtime_semantics.Rec
  ConfigBlock         adv_state.md 9.2               tsuki.misc_adv_system_state.CONFIG_FIELDS
  SystemBlock         adv_state.md 7.5, engine.md 7.3 eboot.savedata.SYSTEM_FIELDS
  Vars                vm_variables.md 2.1, 2.2       tsuki.vm_vars (VAR_F / VAR_Z / VAR_G, counts)

The oracle tables carry descriptions, not C# names, for the config and system blocks; the name
tables below are keyed by offset and the generator stops when they no longer match the oracle.

Usage (from the repository root):
    .venv/Scripts/python.exe tools/setup/gen_state_blocks.py           # write the files
    .venv/Scripts/python.exe tools/setup/gen_state_blocks.py --check   # exit 1 if they are stale

Output is deterministic (no time stamps, LF line ends, sorted by offset), so a re-run is
byte-identical; --check compares with CRLF folded to LF so a git checkout with autocrlf passes.
"""
from __future__ import annotations

import argparse
import hashlib
import re
import struct
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))

from eboot import savedata  # noqa: E402
from tsuki import misc_adv_system_state as adv  # noqa: E402
from tsuki import misc_audio_runtime_semantics as snd  # noqa: E402
from tsuki import misc_layer_state_and_motions as lay  # noqa: E402
from tsuki import vm_vars  # noqa: E402

OUT_DIR = ROOT / "src" / "Tsuki.Engine" / "Contracts" / "Generated"
GENERATOR = "tools/setup/gen_state_blocks.py"

# element types: C# type, size, StateBlock read / write helper suffix
TYPES: Dict[str, Tuple[str, int, str]] = {
    "u8": ("byte", 1, "U8"), "i8": ("sbyte", 1, "I8"),
    "u16": ("ushort", 2, "U16"), "i16": ("short", 2, "I16"),
    "u32": ("uint", 4, "U32"), "i32": ("int", 4, "I32"),
    "u64": ("ulong", 8, "U64"), "i64": ("long", 8, "I64"),
    "f32": ("float", 4, "F32"),
}
STRUCT_CODE = {"b": "i8", "B": "u8", "h": "i16", "H": "u16", "i": "i32", "I": "u32", "q": "i64", "Q": "u64"}


class GenError(Exception):
    pass


def need(cond: bool, msg: str) -> None:
    if not cond:
        raise GenError(msg)


# ---------------------------------------------------------------------------
# model
# ---------------------------------------------------------------------------
@dataclass
class Field:
    name: str                 # C# accessor name
    oracle: str               # name in the source table
    off: int
    size: int
    kind: str                 # Scalar Array Bits Bytes Block Unused
    type: str = "bytes"       # element type (TYPES key), 'bytes', or the block class
    count: int = 1            # entries (Array) / bits (Bits)
    per: int = 1              # values per entry (Array of several values)
    doc: str = ""
    default: Any = None       # SceneState per-layer default / ConfigBlock boot value
    index: str = "index"      # name of the entry index parameter

    @property
    def entry(self) -> int:
        return self.size // self.count if self.kind == "Array" else self.size


@dataclass
class SubField:
    """A typed value inside each opaque entry of an Array field (motion / ext records)."""
    array: str
    name: str
    pos: int
    type: str
    doc: str


@dataclass
class Block:
    cls: str
    size: int
    summary: str
    sources: str
    fields: List[Field]
    subfields: Sequence[SubField] = ()
    extra: Optional[Callable[["Block"], List[str]]] = None


def pascal(name: str) -> str:
    """snake_case oracle name -> PascalCase; parts with digits are hex addresses (upper case)."""
    out = []
    for part in name.split("_"):
        if not part:
            continue
        if any(c.isdigit() for c in part):
            out.append(part.upper())
        else:
            out.append(part[0].upper() + part[1:])
    return "".join(out)


def check_tiling(block: str, fields: Sequence[Field], size: int) -> None:
    pos = 0
    for f in fields:
        need(f.off == pos, f"{block}: gap or overlap at 0x{pos:X} (next field {f.oracle} at 0x{f.off:X})")
        need(f.size > 0, f"{block}: empty field {f.oracle}")
        if f.kind == "Array":
            need(f.size % f.count == 0, f"{block}: {f.oracle} size not a multiple of its count")
        if f.kind in ("Scalar",):
            need(TYPES[f.type][1] == f.size, f"{block}: {f.oracle} type {f.type} vs size {f.size}")
        pos = f.off + f.size
    need(pos == size, f"{block}: fields end at 0x{pos:X}, block is 0x{size:X}")
    names = [f.name for f in fields if f.kind != "Unused"]
    need(len(names) == len(set(names)), f"{block}: duplicate accessor names")


# ---------------------------------------------------------------------------
# SceneState (layers.md 3, adv_state.md 2.6)
# ---------------------------------------------------------------------------
GLOBAL_TYPE = re.compile(r"^(u8|i8|u16|i16|u32|i32|u64|i64)\b")

# motion record (layers.md 3 row 0xD00 and 8.1; lay.motion_record / motion_copy) and the ext record
# (layers.md 3 row 0x16B0; lay.EXT_OFF_X / EXT_OFF_Y / EXT_COLOUR)
SCENE_SUBFIELDS = (
    SubField("Motion", "MotionId", 0x00, "i16", "motion id (+0; 0 = none)"),
    SubField("Motion", "MotionDivisor", 0x02, "i16", "speed divisor n of _STMA (+2)"),
    SubField("Motion", "MotionFrames", 0x08, "i64", "frame counter (+8; the scene copy holds the quantised count)"),
    SubField("Motion", "MotionPause", 0x10, "u8", "pause byte (+0x10)"),
    SubField("Ext", "ExtOffsetX", lay.EXT_OFF_X, "i16", "picture offset x = -anchor x (+2)"),
    SubField("Ext", "ExtOffsetY", lay.EXT_OFF_Y, "i16", "picture offset y = -anchor y (+4)"),
    SubField("Ext", "ExtColourR", lay.EXT_COLOUR, "u8", "colour R of STEC mode 1 (+6; white after STCH / SCH2)"),
    SubField("Ext", "ExtColourG", lay.EXT_COLOUR + 1, "u8", "colour G (+7)"),
    SubField("Ext", "ExtColourB", lay.EXT_COLOUR + 2, "u8", "colour B (+8)"),
)


def scene_block() -> Block:
    need(not adv.check_scene_state_tiling(), "SCENE_STATE_FIELDS does not tile 0x1A08")
    need(adv.SCENE_STATE_SIZE == lay.SCENE_LEN == 0x1A08, "scene_state size")
    arrays = {a.name: a for a in lay.SCENE_ARRAYS}
    n = lay.NUM_LAYERS
    fields: List[Field] = []
    seen = set()
    for s in adv.SCENE_STATE_FIELDS:
        if s.kind == "array":
            a = arrays.get(s.name)
            if a is None:
                raise GenError(f"scene array {s.name} missing from SCENE_ARRAYS")
            need(a.off == s.off and a.stride * n == s.size, f"scene array {s.name}: layer and adv maps disagree")
            seen.add(s.name)
            m = re.fullmatch(r"(\d*)([bBhHiIqQs])", a.fmt)
            if m is None:
                raise GenError(f"scene array {s.name}: format {a.fmt}")
            rep = int(m.group(1) or 1)
            code = m.group(2)
            doc = f"{a.writers}; committed to {a.commit}"
            if code == "s":
                fields.append(Field(pascal(s.name), s.name, s.off, s.size, "Array", "bytes", n, 1, doc,
                                    bytes(a.default), "layer"))
            else:
                t = STRUCT_CODE[code]
                dflt = a.default if isinstance(a.default, tuple) else (a.default,)
                need(len(dflt) == rep, f"scene array {s.name}: default arity")
                fields.append(Field(pascal(s.name), s.name, s.off, s.size, "Array", t, n, rep, doc, dflt, "layer"))
        elif s.kind == "global":
            m = GLOBAL_TYPE.match(s.note)
            if m is None:
                raise GenError(f"scene global {s.name}: no type in its note")
            fields.append(Field(pascal(s.name), s.name, s.off, s.size, "Scalar", m.group(1), doc=s.note))
        else:
            need(s.kind == "gap", f"scene field kind {s.kind}")
            fields.append(Field(pascal(s.name), s.name, s.off, s.size, "Unused", doc=s.note))
    need(seen == set(arrays), f"SCENE_ARRAYS not in SCENE_STATE_FIELDS: {sorted(set(arrays) - seen)}")
    check_tiling("SceneState", fields, 0x1A08)
    for sf in SCENE_SUBFIELDS:
        arr = next(f for f in fields if f.name == sf.array)
        need(sf.pos + TYPES[sf.type][1] <= arr.entry, f"sub-field {sf.name} outside its entry")

    def extra(b: Block) -> List[str]:
        out = [
            "    /// <summary>Number of layers; every per-layer array has one entry per layer (layers.md 3).</summary>",
            f"    public const int Layers = {n};",
            "",
            "    /// <summary>",
            "    /// Sets every per-layer field of <paramref name=\"layer\"/> to its value after <c>_STCC</c> / scene start",
            "    /// (layers.md 3 column \"default\"; <c>tsuki.misc_layer_state_and_motions.SceneState.reset_layer</c>).",
            "    /// </summary>",
            "    public void ResetLayer(int layer)",
            "    {",
            "        CheckIndex(layer, Layers);",
        ]
        for f in b.fields:
            if f.kind != "Array":
                continue
            if f.type == "bytes":
                need(not any(f.default), f"{f.oracle}: non-zero byte default")
                out.append(f"        {f.name}(layer).Clear();")
            elif f.per == 1:
                out.append(f"        Set{f.name}(layer, {cs_literal(f.type, f.default[0])});")
            else:
                for j, v in enumerate(f.default):
                    out.append(f"        Set{f.name}(layer, {j}, {cs_literal(f.type, v)});")
        out += [
            "    }",
            "",
            "    /// <summary>A new block with every layer reset (<see cref=\"ResetLayer\"/>) and the globals zero, like the oracle's <c>SceneState()</c>.</summary>",
            "    public static SceneState CreateDefault()",
            "    {",
            "        var s = new SceneState();",
            "        for (int layer = 0; layer < Layers; layer++)",
            "        {",
            "            s.ResetLayer(layer);",
            "        }",
            "",
            "        return s;",
            "    }",
        ]
        return out

    return Block("SceneState", 0x1A08,
                 "The script-side layer state at VA 0x16500C0 (0x1A08 bytes): 51 per-layer arrays of 40 entries, "
                 "the scalar globals and 4 unaddressed gaps (layers.md 3, adv_state.md 2.6). A history record holds "
                 "it twice (+0x58 and the committed copy +0x1A60).",
                 "layers.md 3, adv_state.md 2.6: tsuki.misc_adv_system_state.SCENE_STATE_FIELDS + "
                 "tsuki.misc_layer_state_and_motions.SCENE_ARRAYS",
                 fields, SCENE_SUBFIELDS, extra)


def cs_literal(t: str, v: Any) -> str:
    cs, size, _ = TYPES[t]
    bits = 8 * size
    iv = int(v) & ((1 << bits) - 1)
    if t.startswith("i") and iv >> (bits - 1):
        iv -= 1 << bits                  # e.g. the boot language 0xFFFF as an i16 is -1
    if cs in ("int",):
        return str(iv)
    return f"({cs}){iv}" if iv >= 0 else f"({cs})({iv})"


# ---------------------------------------------------------------------------
# HistoryRecord (adv_state.md 2.1)
# ---------------------------------------------------------------------------
HISTORY_NAMES = {          # oracle name -> C# name where the PascalCase form is unclear
    "mode": "Kind",        # adv_state.md 2.1 calls +0x24 "kind"
    "f_u16": "FU16",
    "f_bool_bits": "FBoolBit",
    "f_s8": "FS8",
    "channels": "Channel",
}


def history_block() -> Block:
    need(not adv.check_history_tiling(), "HISTORY_FIELDS does not tile 0x3A10")
    need(adv.RECORD_SIZE == savedata.SNAPSHOT_SIZE == 0x3A10, "history record size")
    fields: List[Field] = []
    for h in adv.HISTORY_FIELDS:
        name = HISTORY_NAMES.get(h.name, pascal(h.name))
        doc = f"{h.writer}. Restore: {h.restore}"
        t = h.ctype
        if h.name.startswith("unused"):
            fields.append(Field(name, h.name, h.off, h.size, "Unused", doc=doc))
        elif t in TYPES:
            fields.append(Field(name, h.name, h.off, h.size, "Scalar", t, doc=doc))
        elif h.name in ("scene_state", "scene_committed"):
            need(h.size == 0x1A08, f"{h.name} size")
            fields.append(Field(name, h.name, h.off, h.size, "Block", "SceneState", doc=doc))
        elif t == "bytes":
            fields.append(Field(name, h.name, h.off, h.size, "Bytes", doc=doc))
        elif t.startswith("bits"):
            m = re.fullmatch(r"bits(?:\[(\d+)\])?", t)
            if m is None:
                raise GenError(f"history {h.name}: {t}")
            if h.size == 2:      # a 16-bit mask: one u16 value
                fields.append(Field(name, h.name, h.off, h.size, "Scalar", "u16", doc=doc))
            else:
                nb = int(m.group(1))
                need(nb <= 8 * h.size, f"history {h.name}: {nb} bits in {h.size} bytes")
                fields.append(Field(name, h.name, h.off, h.size, "Bits", "bit", nb, doc=doc, index="bit"))
        else:
            m = re.fullmatch(r"(u8|i8|u16|i16|rec(\d+))\[(\d+)\]", t)
            if m is None:
                raise GenError(f"history {h.name}: type {t}")
            cnt = int(m.group(3))
            if m.group(2):
                need(int(m.group(2), 16) * cnt == h.size, f"history {h.name}: record size")
                fields.append(Field(name, h.name, h.off, h.size, "Array", "bytes", cnt, doc=doc))
            else:
                need(TYPES[m.group(1)][1] * cnt == h.size, f"history {h.name}: array size")
                fields.append(Field(name, h.name, h.off, h.size, "Array", m.group(1), cnt, doc=doc))
    check_tiling("HistoryRecord", fields, 0x3A10)

    u16, bl, s8 = vm_vars.HISTORY_U16, vm_vars.HISTORY_BOOL, vm_vars.HISTORY_S8
    by = {f.oracle: f for f in fields}
    need(len(u16) == by["f_u16"].count == 39, "u16 list length")
    need(len(bl) == by["f_bool_bits"].count == 43, "bool list length")
    need(len(s8) == 276 and len(s8) <= by["f_s8"].count, "s8 list length")

    def extra(b: Block) -> List[str]:
        out: List[str] = []
        for cname, seq, field, doc in (
            ("FU16Indices", u16, "FU16", "F indices kept exactly (u16) in <see cref=\"GetFU16\"/>, entry k = F[FU16Indices[k]] (eboot list VA 0x2160E0, 39 entries)"),
            ("FBoolIndices", bl, "FBoolBit", "F indices kept as a bit (value != 0) in <see cref=\"GetFBoolBit\"/>, bit k = F[FBoolIndices[k]] (eboot list VA 0x216580, 43 entries)"),
            ("FS8Indices", s8, "FS8", "F indices kept as their low byte (restored sign-extended) in <see cref=\"GetFS8\"/>, entry k = F[FS8Indices[k]] (eboot list VA 0x2165F0, 276 entries; the list ends at the first negative entry)"),
        ):
            out.append(f"    /// <summary>{doc}. Restore order u16, s8, bool: an index in two lists ends with the later list's value (adv_state.md 2.1; tsuki.vm_vars.HISTORY_*).</summary>")
            out.append(f"    public static ImmutableArray<int> {cname} {{ get; }} =")
            out.append("    [")
            for i in range(0, len(seq), 16):
                out.append("        " + ", ".join(str(v) for v in seq[i:i + 16]) + ",")
            out.append("    ];")
            out.append("")
        return out[:-1]

    return Block("HistoryRecord", 0x3A10,
                 "One history record (0x3A10 bytes): the state before a state-changing command, used by the "
                 "history ring, the scene stack and both states of a save slot (adv_state.md 2.1).",
                 "adv_state.md 2.1: tsuki.misc_adv_system_state.HISTORY_FIELDS; F lists tsuki.vm_vars.HISTORY_U16 / _BOOL / _S8",
                 fields, (), extra)


# ---------------------------------------------------------------------------
# SoundChannelRecord (sound_runtime.md 2.2)
# ---------------------------------------------------------------------------
CHANNEL_FIELDS: Tuple[Tuple[int, str, str, str, str], ...] = (
    # off, type, C# name, oracle name (tsuki.misc_audio_runtime_semantics.Rec), doc
    (0x00, "u32", "State", "state", "0 idle, 1 playing, 2 fading out (stop when the volume reaches 0)"),
    (0x04, "u32", "VolumeClass", "kind", "volume class (\"kind\"): 1 = SE (volume byte +0xE), 3 = system (+0xF), else voice (+0xD)"),
    (0x08, "u16", "Started", "w8", "1 = a stream was started (the frame update skips the record when 0)"),
    (0x0A, "u16", "Entry", "idx", "bgm.hed / vse.hed entry"),
    (0x0C, "f32", "Volume", "vol", "current volume, 0..100 (the script's percentage)"),
    (0x10, "f32", "VolumeTarget", "vol_to", "target volume"),
    (0x14, "f32", "VolumeStep", "vol_rate", "ramp step per frame (<= 0 is replaced by 0.2)"),
    (0x18, "f32", "Pan", "pan", "current pan, -1000..1000 (stream pan = value * 0.001)"),
    (0x1C, "f32", "PanTarget", "pan_to", "pan target"),
    (0x20, "f32", "PanStep", "pan_rate", "pan step per frame (0 = no sweep)"),
    (0x24, "f32", "Ramp3", "p3", "third ramp, current (set only by the unused _SEP3; never sent to the stream)"),
    (0x28, "f32", "Ramp3Target", "p3_to", "third ramp, target"),
    (0x2C, "f32", "Ramp3Step", "p3_rate", "third ramp, step"),
    (0x30, "u8", "Character", "char", "character 1..19 (`070) for a voice-like SE, else 0"),
)
CHANNEL_SIZE = 0x34


def channel_doc_rows() -> Dict[int, str]:
    """offset -> type from the table in sound_runtime.md 2.2."""
    text = (ROOT / "docs" / "formats" / "sound_runtime.md").read_text(encoding="utf-8")
    m = re.search(r"^### 2\.2 Channel record \(0x34 bytes\)\n(.*?)^### ", text, re.S | re.M)
    if m is None:
        raise GenError("sound_runtime.md 2.2 not found")
    rows: Dict[int, str] = {}
    for line in m.group(1).splitlines():
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) < 2 or not cells[0].startswith("0x"):
            continue
        for off in cells[0].split("/"):
            rows[int(off.strip(), 16)] = cells[1]
    return rows


def channel_block() -> Block:
    doc = channel_doc_rows()
    need({o: t for o, t, *_ in CHANNEL_FIELDS} == doc,
         f"CHANNEL_FIELDS disagree with sound_runtime.md 2.2: {doc}")
    # Rec.pack must put every field at our offset: pack distinct values and read them back
    rec_fields = {f for f in snd.Rec.__dataclass_fields__}
    need({o for _, _, _, o, _ in CHANNEL_FIELDS} == rec_fields, "CHANNEL_FIELDS vs Rec fields")
    vals = {}
    for i, (off, t, _, oname, _) in enumerate(CHANNEL_FIELDS):
        vals[oname] = snd.F32(i + 0.5) if t == "f32" else i + 1
    packed = snd.Rec(**vals).pack()
    need(len(packed) == CHANNEL_SIZE, "Rec.pack size")
    fmt = {"u32": "<I", "u16": "<H", "u8": "<B", "f32": "<f"}
    for off, t, _, oname, _ in CHANNEL_FIELDS:
        need(struct.unpack_from(fmt[t], packed, off)[0] == vals[oname], f"Rec.pack puts {oname} elsewhere")
    fields = [Field(n, o, off, TYPES[t][1], "Scalar", t, doc=d) for off, t, n, o, d in CHANNEL_FIELDS]
    end = fields[-1].off + fields[-1].size
    need(not any(packed[end:]), "Rec.pack writes after +0x30")
    fields.append(Field("Padding31", "pad", end, CHANNEL_SIZE - end, "Unused", doc="padding (zero in Rec.pack)"))
    check_tiling("SoundChannelRecord", fields, CHANNEL_SIZE)

    def extra(b: Block) -> List[str]:
        return [
            "    /// <summary>Data VA of the two BGM records (streams 0, 1; sound_runtime.md 2.1).</summary>",
            f"    public const long BgmRecordsVa = 0x{snd.VA_BGM_REC:X};",
            "",
            "    /// <summary>Data VA of the four voice records (streams 2..5).</summary>",
            f"    public const long VoiceRecordsVa = 0x{snd.VA_VOICE_REC:X};",
            "",
            "    /// <summary>Data VA of the twenty script SE records (streams 6..25, channel c = stream - 6).</summary>",
            f"    public const long SeRecordsVa = 0x{snd.VA_SE_REC:X};",
        ]

    return Block("SoundChannelRecord", CHANNEL_SIZE,
                 "One sound channel record (0x34 bytes): BGM slots, voice streams and script SE channels "
                 "(sound_runtime.md 2.2).",
                 "sound_runtime.md 2.2 (table checked by the generator), tsuki.misc_audio_runtime_semantics.Rec",
                 fields, (), extra)


# ---------------------------------------------------------------------------
# ConfigBlock (adv_state.md 9.2)
# ---------------------------------------------------------------------------
CONFIG_NAMES: Dict[int, Tuple[str, str]] = {   # offset -> (C# name, type); 'bytes' / 'u16[n]' / 'unused'
    0x00: ("Display00", "u16"), 0x02: ("Display02", "u16"), 0x04: ("LiveValue04", "u16"),
    0x06: ("Display06", "u16"), 0x08: ("Constants08", "u64"), 0x10: ("Hud10", "u16"),
    0x12: ("Hud12", "u16"), 0x14: ("MusicVolume", "u16"), 0x16: ("PlayAllVoices", "u16"),
    0x18: ("SfxVolume", "u16"), 0x1A: ("SystemSeVolume", "u16"), 0x1C: ("Word1C", "u16"),
    0x1E: ("Word1E", "u16"), 0x20: ("UnseenTextSpeed", "u16"), 0x22: ("SeenTextSpeed", "u16"),
    0x24: ("Language", "i16"), 0x26: ("TextDraw26", "u16"), 0x28: ("Text28", "u16"),
    0x2A: ("TextColourMode", "u16"), 0x2C: ("Audio2C", "u16"), 0x2E: ("AutoModeWait", "u16"),
    0x30: ("FastForwardReadOnly", "u16"), 0x32: ("SceneJumpButtons", "u16"), 0x34: ("Constant34", "u16"),
    0x36: ("VoiceGate36", "u16"), 0x38: ("Unused38", "unused"), 0x3C: ("VoiceEnableBits", "u32"),
    0x40: ("AutoSaveMask", "u16"), 0x42: ("ActionPreset", "u16"), 0x44: ("Choice44", "u16"),
    0x46: ("Hud46", "u16"), 0x48: ("Unused48", "unused"), 0x4A: ("Hud4A", "u16"),
    0x4C: ("RestoreLiveValueGate", "u16"), 0x4E: ("InterruptVoices", "u16"), 0x50: ("Constant50", "u16"),
    0x52: ("Unused52", "unused"), 0x54: ("MusicVolumeCopy", "u16"), 0x56: ("Dialog56", "u16"),
    0x58: ("Constant58", "u16"), 0x5A: ("BootRecords", "bytes"), 0x76: ("CharacterVoiceVolume", "u16[19]"),
    0x9C: ("PageEndInstantUnseen", "u8"), 0x9D: ("PageEndInstantSeen", "u8"), 0x9E: ("AutoModePageSpeed", "u16"),
    0xA0: ("AutoSkipCleared", "u16"), 0xA2: ("SkipConfirm", "u8"), 0xA3: ("TextAnimations", "u8"),
    0xA4: ("SeenTextAnimations", "u8"), 0xA5: ("ConstantA5", "u8"), 0xA6: ("MusicMute", "u8"),
    0xA7: ("SfxMute", "u8"), 0xA8: ("SystemSeMute", "u8"), 0xA9: ("UnusedA9", "unused"),
}


def config_block() -> Block:
    need(adv.CONFIG_SIZE == 0x140, "config size")
    need(sorted(CONFIG_NAMES) == [c.off for c in adv.CONFIG_FIELDS],
         "CONFIG_NAMES offsets no longer match CONFIG_FIELDS")
    fields: List[Field] = []
    for c in adv.CONFIG_FIELDS:
        name, t = CONFIG_NAMES[c.off]
        oname = f"config+0x{c.off:02X}"      # CONFIG_FIELDS has descriptions, not names
        doc = f"{c.name}. Readers: {c.readers}. Menu: {c.menu}. Boot value {c.static}"
        if t == "unused":
            need(c.static == 0, f"config {name}: unused but non-zero boot value")
            fields.append(Field(name, oname, c.off, c.size, "Unused", doc=doc))
        elif t == "bytes":
            need(c.static == 0, f"config {name}: bytes with a non-zero boot value")
            fields.append(Field(name, oname, c.off, c.size, "Bytes", doc=doc))
        elif t.endswith("]"):
            m = re.fullmatch(r"(u16)\[(\d+)\]", t)
            if m is None or 2 * int(m.group(2)) != c.size:
                raise GenError(f"config {name}: {t}")
            fields.append(Field(name, oname, c.off, c.size, "Array", "u16", int(m.group(2)), 1, doc, (c.static,)))
        else:
            need(TYPES[t][1] == c.size, f"config {name}: {t} vs size {c.size}")
            fields.append(Field(name, oname, c.off, c.size, "Scalar", t, doc=doc, default=c.static))
    check_tiling("ConfigBlock", fields, 0x140)

    def extra(b: Block) -> List[str]:
        out = [
            "    /// <summary>",
            "    /// The block as stored in the eboot data image at VA 0x66C578, i.e. the boot defaults",
            "    /// (adv_state.md 9.2 \"boot value\"; equal to <c>tsuki.misc_adv_system_state.read_static_config</c>).",
            "    /// </summary>",
            "    public static ConfigBlock CreateBootDefaults()",
            "    {",
            "        var c = new ConfigBlock();",
        ]
        for f in b.fields:
            if f.kind == "Scalar" and int(f.default) != 0:
                out.append(f"        c.{f.name} = {cs_literal(f.type, f.default)};")
            elif f.kind == "Array" and int(f.default[0]) != 0:
                out += [
                    f"        for (int i = 0; i < {f.name}Count; i++)",
                    "        {",
                    f"            c.Set{f.name}(i, {cs_literal(f.type, f.default[0])});",
                    "        }",
                ]
        out += ["", "        return c;", "    }"]
        return out

    return Block("ConfigBlock", 0x140,
                 "The configuration block at VA 0x66C578 (0x140 bytes), stored as system block +0x04 "
                 "(adv_state.md 9.2; sound_runtime.md 2.4).",
                 "adv_state.md 9.2: tsuki.misc_adv_system_state.CONFIG_FIELDS (names: CONFIG_NAMES in the generator)",
                 fields, (), extra)


def config_boot_bytes() -> bytes:
    out = bytearray(0x140)
    for c in adv.CONFIG_FIELDS:
        name, t = CONFIG_NAMES[c.off]
        if t.startswith("u16["):
            for i in range(c.size // 2):
                struct.pack_into("<H", out, c.off + 2 * i, c.static)
        elif t in TYPES:
            out[c.off:c.off + c.size] = (c.static & ((1 << (8 * c.size)) - 1)).to_bytes(c.size, "little")
    return bytes(out)


# ---------------------------------------------------------------------------
# SystemBlock (adv_state.md 7.5, engine.md 7.3; gallery.md for the two bit blocks)
# ---------------------------------------------------------------------------
SYSTEM_NAMES: Dict[int, Tuple[str, str, int]] = {   # offset -> (C# name, type, count)
    0x0000: ("Magic", "u16", 1),
    0x0002: ("Used", "u16", 1),
    0x0004: ("Config", "ConfigBlock", 1),
    0x0144: ("G", "u16", 500),
    0x052C: ("UnlockBit", "bits", 0x20C * 8),
    0x0738: ("Readmax", "u8", 4020),
    0x16EC: ("Unused16EC", "unused", 1),
    0x16EE: ("LatestSlot", "u16", 1),
    0x16F0: ("Unused16F0", "unused", 1),
    0x16F2: ("Word16F2", "u16", 1),
    0x16F4: ("ChoiceTaken", "u8", 0x118),
    0x180C: ("Unused180C", "unused", 1),
}


def system_block() -> Block:
    need(savedata.SYSTEM_SIZE == 0x18AC, "system block size")
    need(sorted(SYSTEM_NAMES) == [f[0] for f in savedata.SYSTEM_FIELDS],
         "SYSTEM_NAMES offsets no longer match eboot.savedata.SYSTEM_FIELDS")
    fields: List[Field] = []
    for off, size, ram, meaning in savedata.SYSTEM_FIELDS:
        name, t, cnt = SYSTEM_NAMES[off]
        doc = meaning + (f" (RAM 0x{ram:X})" if ram is not None else "")
        if t == "unused":
            fields.append(Field(name, f"system+0x{off:X}", off, size, "Unused", doc=doc))
        elif t == "ConfigBlock":
            need(size == 0x140 and ram == adv.CONFIG, "system config part")
            fields.append(Field(name, f"system+0x{off:X}", off, size, "Block", t, doc=doc))
        elif t == "bits":
            need(cnt == 8 * size, f"system {name}: bit count")
            fields.append(Field(name, f"system+0x{off:X}", off, size, "Bits", "bit", cnt, doc=doc, index="bit"))
        elif cnt > 1:
            need(TYPES[t][1] * cnt == size, f"system {name}: array size")
            fields.append(Field(name, f"system+0x{off:X}", off, size, "Array", t, cnt, doc=doc))
        else:
            need(TYPES[t][1] == size, f"system {name}: size")
            fields.append(Field(name, f"system+0x{off:X}", off, size, "Scalar", t, doc=doc))
    check_tiling("SystemBlock", fields, 0x18AC)

    def extra(b: Block) -> List[str]:
        return [
            "    /// <summary>The magic word at +0 (and at the start of every stored block): 0x2AF8.</summary>",
            f"    public const ushort MagicValue = 0x{savedata.MAGIC:X};",
        ]

    return Block("SystemBlock", 0x18AC,
                 "The persistent system block (0x18AC bytes, save image offset 0, staging RAM 0x1201660): "
                 "config, G[0..499], unlock bits, readmax, latest slot (adv_state.md 7.5; engine.md 7.3).",
                 "adv_state.md 7.5, engine.md 7.3: eboot.savedata.SYSTEM_FIELDS (names: SYSTEM_NAMES in the generator)",
                 fields, (), extra)


# ---------------------------------------------------------------------------
# Vars (vm_variables.md 2.1, 2.2)
# ---------------------------------------------------------------------------
def vars_block() -> Block:
    fc, zc, gc = vm_vars.F_COUNT, vm_vars.Z_COUNT, vm_vars.G_COUNT
    need((fc, zc, gc) == (1200, 1400, 2000), "variable counts")
    need(vm_vars.VAR_Z == vm_vars.VAR_F + 2 * fc and vm_vars.VAR_G == vm_vars.VAR_Z + 2 * zc
         and vm_vars.VAR_AFTER_G == vm_vars.VAR_G + 2 * gc, "F, Z and G are not contiguous")
    size = 2 * (fc + zc + gc)
    fields = [
        Field("F", "var_F", 0, 2 * fc, "Array", "u16", fc,
              doc=f"var_F u16[{fc}] at VA 0x{vm_vars.VAR_F:X}; F[{fc} + i] = Z[i], F[{fc + zc} + i] = G[i]"),
        Field("Z", "var_Z", 2 * fc, 2 * zc, "Array", "u16", zc,
              doc=f"var_Z u16[{zc}] at VA 0x{vm_vars.VAR_Z:X}; no script operand reaches it"),
        Field("G", "var_G", 2 * (fc + zc), 2 * gc, "Array", "u16", gc,
              doc=f"var_G u16[{gc}] at VA 0x{vm_vars.VAR_G:X}; G[0..499] persist in the system block"),
    ]
    check_tiling("Vars", fields, size)
    f823 = vm_vars.MSG_INDEX_VAR

    def accessors(b: Block) -> List[str]:
        return f"""    /// <summary>Number of F variables (var_F u16[{fc}]).</summary>
    public const int FCount = {fc};

    /// <summary>Number of Z variables (var_Z u16[{zc}]).</summary>
    public const int ZCount = {zc};

    /// <summary>Number of G variables (var_G u16[{gc}]).</summary>
    public const int GCount = {gc};

    /// <summary>Word index of Z[0] in the block (F[{fc} + i] = Z[i]).</summary>
    public const int ZWord = {fc};

    /// <summary>Word index of G[0] in the block (F[{fc + zc} + i] = Z[{zc} + i] = G[i]).</summary>
    public const int GWord = {fc + zc};

    /// <summary>Number of u16 words in the block.</summary>
    public const int WordCount = {fc + zc + gc};

    /// <summary>VA of var_F (word 0).</summary>
    public const long FVa = 0x{vm_vars.VAR_F:X};

    /// <summary>VA of var_Z.</summary>
    public const long ZVa = 0x{vm_vars.VAR_Z:X};

    /// <summary>VA of var_G.</summary>
    public const long GVa = 0x{vm_vars.VAR_G:X};

    /// <summary>F823: message counter of the page; <c>C&lt;n&gt;</c> = F[F823 + n] (vm_variables.md 2.3).</summary>
    public const int MessageIndexVar = {f823};

    /// <summary>True when word index <paramref name="word"/> (0 = F[0]) lies in the block.</summary>
    public static bool InBlock(int word) => (uint)word < WordCount;

    /// <summary>
    /// Word index of the operand <c>F&lt;n&gt;</c>: the engine adds 2 * s16(n) to var_F with no bounds check
    /// (vm_variables.md 2.2), so <c>F1200</c> is Z[0], <c>F65535</c> is the word before F[0] (-1) and
    /// <c>F70000</c> is G[1864]. The result can lie outside the block (<see cref="InBlock"/>).
    /// </summary>
    public static int FWordOfOperand(int n) => (short)n;

    /// <summary>Word index of the operand <c>G&lt;n&gt;</c>: var_G + 2 * s16(n) (<c>G-1</c> is Z[1399], <c>G2000</c> is outside).</summary>
    public static int GWordOfOperand(int n) => GWord + (short)n;

    /// <summary>
    /// Word index of the target <c>C&lt;n&gt;</c> of <c>_WKST</c> / <c>_WKAD</c> while F823 = <paramref name="f823"/>:
    /// var_F + 2 * (s16(F823) + s16(n)), a 64-bit sum with no clamp (vm_variables.md 2.2; tsuki.vm_vars.target_address).
    /// </summary>
    public static int CWordOfOperand(ushort f823, int n) => (short)f823 + (short)n;

    /// <summary>The engine VA of word <paramref name="word"/>.</summary>
    public static long VaOfWord(int word) => FVa + (2L * word);

    /// <summary>The word index of an engine VA inside the block (even addresses only).</summary>
    public static bool TryWordOfVa(long va, out int word)
    {{
        long d = va - FVa;
        if (d >= 0 && d < 2L * WordCount && (d & 1) == 0)
        {{
            word = (int)(d >> 1);
            return true;
        }}

        word = -1;
        return false;
    }}

    /// <summary>Word <paramref name="word"/> of the block (0 = F[0]); throws outside the block.</summary>
    public ushort GetWord(int word) => ReadU16(2 * CheckIndex(word, WordCount));

    /// <summary>Sets word <paramref name="word"/> of the block; throws outside the block.</summary>
    public void SetWord(int word, ushort value) => WriteU16(2 * CheckIndex(word, WordCount), value);

    /// <summary>F[<paramref name="index"/>]; indices {fc}..{fc + zc + gc - 1} reach Z and G as on the PS4.</summary>
    public ushort GetF(int index) => GetWord(index);

    /// <summary>Sets F[<paramref name="index"/>] (aliasing as in <see cref="GetF"/>).</summary>
    public void SetF(int index, ushort value) => SetWord(index, value);

    /// <summary>Z[<paramref name="index"/>] (negative indices reach F, indices from {zc} reach G).</summary>
    public ushort GetZ(int index) => GetWord(ZWord + index);

    /// <summary>Sets Z[<paramref name="index"/>].</summary>
    public void SetZ(int index, ushort value) => SetWord(ZWord + index, value);

    /// <summary>G[<paramref name="index"/>] (negative indices reach Z and F).</summary>
    public ushort GetG(int index) => GetWord(GWord + index);

    /// <summary>Sets G[<paramref name="index"/>].</summary>
    public void SetG(int index, ushort value) => SetWord(GWord + index, value);

    /// <summary>Word index of <c>C&lt;n&gt;</c> with the current F823.</summary>
    public int CWord(int n) => CWordOfOperand(GetF(MessageIndexVar), n);

    /// <summary>The value of <c>C&lt;n&gt;</c> = F[F823 + n].</summary>
    public ushort GetC(int n) => GetWord(CWord(n));

    /// <summary>Sets <c>C&lt;n&gt;</c> = F[F823 + n].</summary>
    public void SetC(int n, ushort value) => SetWord(CWord(n), value);""".split("\n")

    return Block("Vars", size,
                 "The script variables (vm_variables.md 2.1): var_F u16[1200], var_Z u16[1400] and var_G u16[2000], "
                 "contiguous as on the PS4, with the F / G / C operand index arithmetic of vm_variables.md 2.2.",
                 "vm_variables.md 2.1, 2.2: tsuki.vm_vars (VAR_F / VAR_Z / VAR_G, F_COUNT / Z_COUNT / G_COUNT, MSG_INDEX_VAR)",
                 fields, (), accessors)


# ---------------------------------------------------------------------------
# C# emission
# ---------------------------------------------------------------------------
def xml(s: str) -> str:
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def summary(text: str, indent: str = "    ") -> List[str]:
    return [f"{indent}/// <summary>{xml(text)}</summary>"]


def hx(v: int) -> str:
    return f"0x{v:04X}"


def emit_field(b: Block, f: Field) -> List[str]:
    where = f"+{hx(f.off)}"
    out: List[str] = []
    if f.kind == "Unused":
        return out
    if f.kind == "Scalar":
        cs, _, h = TYPES[f.type]
        out += summary(f"{where} {f.type} {f.oracle}: {f.doc}")
        out += [f"    public {cs} {f.name}", "    {",
                f"        get => Read{h}({hx(f.off)});",
                f"        set => Write{h}({hx(f.off)}, value);", "    }"]
    elif f.kind == "Bytes":
        out += summary(f"{where}, 0x{f.size:X} bytes, {f.oracle}: {f.doc}")
        out.append(f"    public Span<byte> {f.name} => Slice({hx(f.off)}, 0x{f.size:X});")
    elif f.kind == "Block":
        cache = "_" + f.name[0].lower() + f.name[1:]
        out.append(f"    private {f.type}? {cache};")
        out.append("")
        out += summary(f"{where}, 0x{f.size:X} bytes, {f.oracle}: a view of the {f.type} in this block. {f.doc}")
        out.append(f"    public {f.type} {f.name} => {cache} ??= new {f.type}(Storage, Offset + {hx(f.off)});")
    elif f.kind == "Bits":
        out += summary(f"Number of bits of {f.name}.")
        out.append(f"    public const int {f.name}Count = {f.count};")
        out.append("")
        out += summary(f"{where}, bit {f.index} of {f.oracle} (bit k = byte k / 8, bit k % 8): {f.doc}")
        out.append(f"    public bool Get{f.name}(int {f.index}) => GetBit({hx(f.off)}, CheckIndex({f.index}, {f.name}Count));")
        out.append("")
        out += summary(f"Sets bit {f.index} of {f.oracle}.")
        out.append(f"    public void Set{f.name}(int {f.index}, bool value) => SetBit({hx(f.off)}, CheckIndex({f.index}, {f.name}Count), value);")
    elif f.kind == "Array":
        layer = f.index == "layer"
        count = "Layers" if layer else f"{f.name}Count"
        if not layer:
            out += summary(f"Number of entries of {f.name}.")
            out.append(f"    public const int {f.name}Count = {f.count};")
            out.append("")
        idx = f"{f.entry} * CheckIndex({f.index}, {count})"
        base = f"{hx(f.off)} + ({idx})"
        if f.type == "bytes":
            out += summary(f"{where}, {f.count} x 0x{f.entry:X} bytes, {f.oracle}: entry {f.index}. {f.doc}")
            out.append(f"    public Span<byte> {f.name}(int {f.index}) => Slice({base}, 0x{f.entry:X});")
        else:
            cs, es, h = TYPES[f.type]
            if f.per == 1:
                out += summary(f"{where}, {f.type} x {f.count}, {f.oracle}[{f.index}]: {f.doc}")
                out.append(f"    public {cs} Get{f.name}(int {f.index}) => Read{h}({base});")
                out.append("")
                out += summary(f"Sets {f.oracle}[{f.index}].")
                out.append(f"    public void Set{f.name}(int {f.index}, {cs} value) => Write{h}({base}, value);")
            else:
                out += summary(f"Values per entry of {f.name}.")
                out.append(f"    public const int {f.name}PerEntry = {f.per};")
                out.append("")
                sub = f"{base} + ({es} * CheckIndex(index, {f.name}PerEntry))"
                out += summary(f"{where}, {f.per} x {f.type} per entry x {f.count}, {f.oracle}[{f.index}][index]: {f.doc}")
                out.append(f"    public {cs} Get{f.name}(int {f.index}, int index) => Read{h}({sub});")
                out.append("")
                out += summary(f"Sets {f.oracle}[{f.index}][index].")
                out.append(f"    public void Set{f.name}(int {f.index}, int index, {cs} value) => Write{h}({sub}, value);")
    return out


def emit_subfield(b: Block, sf: SubField) -> List[str]:
    arr = next(f for f in b.fields if f.name == sf.array)
    cs, _, h = TYPES[sf.type]
    count = "Layers" if arr.index == "layer" else f"{arr.name}Count"
    at = f"{hx(arr.off)} + ({arr.entry} * CheckIndex({arr.index}, {count})) + 0x{sf.pos:02X}"
    out = summary(f"{sf.array} entry +0x{sf.pos:X}, {sf.type}: {sf.doc}")
    out.append(f"    public {cs} Get{sf.name}(int {arr.index}) => Read{h}({at});")
    out.append("")
    out += summary(f"Sets the {sf.array} entry's {sf.name}.")
    out.append(f"    public void Set{sf.name}(int {arr.index}, {cs} value) => Write{h}({at}, value);")
    return out


def emit_block(b: Block) -> str:
    L: List[str] = [
        "// <auto-generated>",
        f"//   Generated by {GENERATOR}. Do not edit: change the field map (or the generator) and run",
        f"//   .venv/Scripts/python.exe {GENERATOR}",
        f"//   Source: {b.sources}",
        "// </auto-generated>",
        "#nullable enable",
        "",
        "using System.Collections.Immutable;",
        "",
        "namespace Tsuki.Engine.Contracts;",
        "",
    ]
    L += summary(b.summary, "")
    L += [f"public sealed partial class {b.cls} : StateBlock", "{"]
    L += summary(f"Size of the block in bytes (0x{b.size:X}).")
    L += [f"    public const int Size = 0x{b.size:X};", ""]
    L += summary("Creates a zeroed block.")
    L += [f"    public {b.cls}()", "        : base(Size)", "    {", "    }", ""]
    L += summary("Creates a view of the Size bytes of storage at offset (shared, not copied).")
    L += [f"    public {b.cls}(byte[] storage, int offset = 0)", "        : base(storage, offset, Size)", "    {", "    }", ""]
    L += summary("Every byte of the block in exactly one field, in offset order.")
    L += ["    public static ImmutableArray<StateField> Fields { get; } =", "    ["]
    for f in b.fields:
        need(re.fullmatch(r"[A-Za-z0-9_+]+", f.oracle) is not None, f"{b.cls}: oracle name {f.oracle!r}")
        L.append(f"        new(\"{f.name}\", \"{f.oracle}\", {hx(f.off)}, 0x{f.size:X}, StateFieldKind.{f.kind}, "
                 f"\"{f.type}\", {f.count}, {f.per}),")
    L += ["    ];", ""]
    L += summary("A copy of the block in a new array.")
    L += [f"    public {b.cls} Clone()", "    {", f"        var c = new {b.cls}();", "        c.CopyFrom(this);",
          "        return c;", "    }"]
    if b.extra is not None:
        L.append("")
        L += b.extra(b)
    for f in b.fields:
        if b.cls == "Vars":
            break          # the Vars accessors are written by its extra(), with the aliasing rules
        part = emit_field(b, f)
        if part:
            L.append("")
            L += part
    for sf in b.subfields:
        L.append("")
        L += emit_subfield(b, sf)
    L.append("}")
    return "\n".join(L) + "\n"


def blocks() -> List[Block]:
    return [scene_block(), history_block(), channel_block(), config_block(), system_block(), vars_block()]


def render() -> Dict[str, bytes]:
    return {f"{b.cls}.g.cs": emit_block(b).encode("ascii") for b in blocks()}


def verify_against_eboot() -> str:
    """Optional: the generated boot config equals the eboot data image (needs the game folder)."""
    try:
        eb = adv.read_static_config()
    except (OSError, FileNotFoundError, ValueError) as e:  # game folder missing
        return f"eboot check skipped ({e.__class__.__name__})"
    need(eb == config_boot_bytes(), "CONFIG_FIELDS boot values differ from the eboot data image")
    return "boot config equals the eboot data image (0x140 bytes)"


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--check", action="store_true", help="compare with the files on disk; write nothing")
    ap.add_argument("--out", type=Path, default=OUT_DIR, help="output folder (default: Contracts/Generated)")
    args = ap.parse_args(argv)
    try:
        files = render()
        note = verify_against_eboot()
    except GenError as e:
        print(f"gen_state_blocks: {e}", file=sys.stderr)
        return 2
    stale = []
    for name, data in sorted(files.items()):
        path = args.out / name
        sha = hashlib.sha256(data).hexdigest()
        old = path.read_bytes() if path.exists() else None
        same = old is not None and old.replace(b"\r\n", b"\n") == data
        if args.check:
            print(f"{'ok   ' if same else 'STALE'} {name} {len(data)} bytes sha256 {sha}")
            if not same:
                stale.append(name)
        else:
            if not same or old != data:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
            print(f"{'kept ' if same and old == data else 'wrote'} {name} {len(data)} bytes sha256 {sha}")
    extra = sorted(p.name for p in args.out.glob("*.g.cs") if p.name not in files) if args.out.exists() else []
    for name in extra:
        print(f"UNKNOWN {name} (not produced by this generator)")
    print(note)
    if args.check and (stale or extra):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
