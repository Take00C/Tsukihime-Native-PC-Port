"""Locate and identify the GNM shader binaries embedded in eboot.bin.

Each embedded shader is a PSSL ".sb" program (as produced by the SDK's orbis-wave-psslc)
stored back to back in the RW data segment (VA 0xDCA6E0..0xDD66B0, 49,104 bytes):

    +0x00  0x24-byte (36-byte) binary header: bytes 0..3 = 00 04 FE A5 in all 67;
           +0x04 u32 shader hash (the same value shadPS4 logs as "Compiling fs/vs shader
           0x..."); +0x08 u32 0; byte +0x0C = 1 for PS, 0 for VS; byte +0x0D = 1,
           +0x0E/+0x0F = 00 03 in all 67; +0x10 u32 = OrbShdr offset - 8; +0x14..+0x23 zero
    +0x24  Gnmx shader file header: "Shdr", u16 major=7, u16 minor=2,
           u8 type (1 = vertex/export shader, 2 = pixel shader),
           u8 shader-header size in dwords, u8 aux, u8 target GPU modes, u32 0
    +0x34  Gnmx VsShader / PsShader register block (size = header DWs * 4)
    ...    GCN machine code ending in s_endpgm
    ...    ShaderBinaryInfo footer "OrbShdr" + version 7, then the reflection tables:
           resources (e.g. texture/sampler/transition_tex/y_texture/cbcr_texture),
           constant buffers (TextureSizeConstants, TransitionParamsConstants with member
           transition_params, CommonFragmentParamsConstants), inputs/outputs with
           semantics; the string table ends with the stage's output semantic
           (S_POSITION / S_TARGET_OUTPUT), padded to 16 bytes = end of the blob.

The engine's shader classes (nexl_Shader*) store pointers to their VS and PS blobs in
their constructor (lea rdx/rcx, [rip + blob]) and register themselves by a short
key string in a 127-bucket hash table (VA 0x106860).  This module recovers the
blob list and the name -> (ctor, key, VS blob, PS blob) table from the code.

CLI:  python -m eboot.shaders [--dump DIR] [--json FILE]
"""
from __future__ import annotations

import json
import re
import struct
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import List, Optional

from eboot.disasm import Index
from eboot.selfelf import Eboot

SHDR_SIG = b"Shdr\x07\x00\x02\x00"
BLOB_HDR = 0x24
REGISTER_FN = 0x106860      # ShaderMgr::Register(shader, id, name)
ALLOC_FN = 0x12C430         # nexl allocator: alloc(size, align, tag, ...)


@dataclass
class ShaderBlob:
    index: int
    va: int
    size: int
    stage: str              # "VS" / "PS"
    gnmx_type: int
    header_dw: int
    assoc_hash: str             # u32 at +4 (shadPS4's shader hash), hex
    orbshdr_off: Optional[int]
    users: List[int] = field(default_factory=list)
    names: List[str] = field(default_factory=list)   # reflection strings after the footer


@dataclass
class ShaderClass:
    tag: str                # allocator tag, e.g. nexl_Shader_PS_Overlay
    key: str                # registration key, e.g. ps_overlay
    ctor: int
    vtable: Optional[int]
    vs: Optional[int]       # blob index
    ps: Optional[int]


def find_blobs(eb: Eboot) -> List[ShaderBlob]:
    ph = eb.data_seg
    buf = eb.segment_bytes(ph)
    hdrs = [m.start() for m in re.finditer(re.escape(SHDR_SIG), buf)]
    out: List[ShaderBlob] = []
    for i, h in enumerate(hdrs):
        start = h - BLOB_HDR
        typ, hdw = buf[h + 8], buf[h + 9]
        if buf[start:start + 4] != b"\x00\x04\xfe\xa5" or buf[start + 0x0C] != int(typ == 2):
            raise ValueError(f"shader blob at 0x{ph.vaddr + start:x}: unexpected binary header")
        o = buf.find(b"OrbShdr", h)
        if o < 0:
            raise ValueError(f"shader blob at 0x{ph.vaddr + start:x}: no OrbShdr footer")
        # The reflection string table (resource, buffer, input names) closes the blob; its
        # last string is the stage's system output semantic.  Blob end = that string's end
        # rounded up to 16 (matches the next blob's start for all 66 non-final blobs).
        sig = b"S_POSITION\0" if typ == 1 else b"S_TARGET_OUTPUT\0"
        p = buf.find(sig, o)
        if p < 0:
            raise ValueError(f"shader blob at 0x{ph.vaddr + start:x}: no {sig!r}")
        end = (p + len(sig) + 15) // 16 * 16
        if i + 1 < len(hdrs) and end != hdrs[i + 1] - BLOB_HDR:
            raise ValueError(f"shader blob {i}: end 0x{end:x} != next start")
        blob = ShaderBlob(i, ph.vaddr + start, end - start, {1: "VS", 2: "PS"}.get(typ, f"t{typ}"),
                          typ, hdw, f"{struct.unpack_from('<I', buf, start + 4)[0]:08x}",
                          o - start)
        blob.names = reflection_names(bytes(buf[start:end]), o - start)
        out.append(blob)
    return out


_LEA_RIP = re.compile(r"^(\w+), \[rip \+ (0x[0-9a-f]+)\]$")


def _lea_targets(ix: Index, func: int) -> List[int]:
    out = []
    for ins in ix.insns.get(func, []):
        m = _LEA_RIP.match(ins.ops) if ins.mnem == "lea" else None
        if m:
            out.append(ins.addr + ins.size + int(m.group(2), 16))
    return out


def find_classes(eb: Eboot, ix: Index, blobs: List[ShaderBlob]) -> List[ShaderClass]:
    by_va = {b.va: b for b in blobs}
    for b in blobs:
        b.users = sorted({f for f in (ix.func_of(r) for r in ix.code_refs(b.va)) if f is not None})
    out: List[ShaderClass] = []
    reg_sites = sorted({f for f in (ix.func_of(c) for c in ix.callers(REGISTER_FN)) if f is not None})
    for f in reg_sites:
        tag: Optional[str] = None
        ctor: Optional[int] = None
        for ins in ix.insns[f]:
            m = _LEA_RIP.match(ins.ops) if ins.mnem == "lea" else None
            if m:
                t = ins.addr + ins.size + int(m.group(2), 16)
                s = eb.cstr(t, 64)
                if s.startswith(b"nexl_Shader"):
                    tag, ctor = s.decode(), None
                elif tag and ctor and m.group(1) == "rcx" and s:
                    key = s.decode()
                    lt = _lea_targets(ix, ctor)
                    own = [by_va[x] for x in lt if x in by_va]
                    vt = next((x for x in lt if 0x400000 <= x < 0x600000), None)
                    vs = next((b.index for b in own if b.stage == "VS"), None)
                    ps = next((b.index for b in own if b.stage == "PS"), None)
                    out.append(ShaderClass(tag, key, ctor, vt, vs, ps))
                    tag, ctor = None, None
            elif ins.mnem == "call" and ins.ops.startswith("0x") and tag:
                t = int(ins.ops, 16)
                if t not in (ALLOC_FN, REGISTER_FN) and ctor is None:
                    ctor = t
    return out


_MIMG_OPS = {0x00: "image_load", 0x08: "image_store", 0x20: "image_sample",
             0x24: "image_sample_l", 0x25: "image_sample_b", 0x27: "image_sample_lz",
             0x40: "image_gather4"}


def gcn_classes(code: bytes) -> List[str]:
    """Instruction-class listing of GCN (Sea Islands) machine code up to s_endpgm.

    Only encodings are decoded (enough to size every instruction and to name the
    MIMG opcodes); this is a census tool, not a disassembler.  Unknown encodings
    are reported as "?"."""
    out: List[str] = []
    i, n = 0, len(code)
    while i + 4 <= n:
        w = struct.unpack_from("<I", code, i)[0]
        size, cls, lit = 4, "?", False
        if w >> 31 == 0:
            cls, lit = "VOP2", (w & 0x1FF) == 0xFF
        elif (w >> 25) == 0x3F:
            cls, lit = "VOP1", (w & 0x1FF) == 0xFF
        elif (w >> 25) == 0x3E:
            cls, lit = "VOPC", (w & 0x1FF) == 0xFF
        elif (w >> 23) == 0x17D:
            cls, lit = "SOP1", (w & 0xFF) == 0xFF
        elif (w >> 23) == 0x17E:
            cls, lit = "SOPC", (w & 0xFF) == 0xFF or ((w >> 8) & 0xFF) == 0xFF
        elif (w >> 23) == 0x17F:
            cls = "s_endpgm" if ((w >> 16) & 0x7F) == 1 else "SOPP"
        elif (w >> 28) == 0xB:
            cls = "SOPK"
        elif (w >> 30) == 0x2:
            cls, lit = "SOP2", (w & 0xFF) == 0xFF or ((w >> 8) & 0xFF) == 0xFF
        elif (w >> 27) == 0x18:
            cls, lit = "SMRD", (w & 0xFF) == 0xFF and not (w >> 8) & 1
        elif (w >> 26) == 0x32:
            cls = "VINTRP"
        elif (w >> 26) in (0x34, 0x36, 0x37, 0x38, 0x3A, 0x3E):
            cls = {0x34: "VOP3", 0x36: "DS", 0x37: "FLAT", 0x38: "MUBUF", 0x3A: "MTBUF",
                   0x3E: "EXP"}[w >> 26]
            size = 8
        elif (w >> 26) == 0x3C:
            op = (w >> 18) & 0x7F
            cls, size = _MIMG_OPS.get(op, f"mimg_{op:#x}"), 8
        if lit:
            size += 4
        out.append(cls)
        i += size
        if cls == "s_endpgm":
            break
    return out


_NAME = re.compile(rb"[A-Za-z_][A-Za-z0-9_.()]{2,}\x00")


def reflection_names(raw: bytes, orbshdr_off: int) -> List[str]:
    """NUL-terminated identifier strings of the reflection string table (resource,
    constant-buffer, member, input/output and semantic names), in table order."""
    out: List[str] = []
    for m in _NAME.finditer(raw, orbshdr_off + 8):
        # string table entries start right after a NUL
        if m.start() > 0 and raw[m.start() - 1] != 0:
            continue
        out.append(m.group(0)[:-1].decode("ascii"))
    return out


def blob_code(eb: Eboot, b: ShaderBlob) -> bytes:
    """GCN code of a blob: it starts right after the Gnmx register block."""
    raw = eb.read_va(b.va, b.size)
    return raw[BLOB_HDR + 0x10 + b.header_dw * 4:]


def _main(argv: List[str]) -> int:
    import argparse

    from eboot import eboot_path

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--dump", help="write each blob as <dir>/<index>_<stage>.sb")
    ap.add_argument("--json")
    a = ap.parse_args(argv)
    p = eboot_path()
    if p is None:
        print("game not found", file=sys.stderr)
        return 2
    eb = Eboot.open(p)
    ix = Index.build(eb)
    blobs = find_blobs(eb)
    classes = find_classes(eb, ix, blobs)
    nvs = sum(b.stage == "VS" for b in blobs)
    print(f"{len(blobs)} shader binaries ({nvs} VS, {len(blobs) - nvs} PS), "
          f"{sum(b.size for b in blobs)} bytes, VA 0x{blobs[0].va:x}..0x{blobs[-1].va + blobs[-1].size:x}")
    for c in classes:
        vs = blobs[c.vs] if c.vs is not None else None
        ps = blobs[c.ps] if c.ps is not None else None
        nsamp = "-"
        if ps is not None:
            cl = gcn_classes(blob_code(eb, ps))
            nsamp = f"{len(cl)} insns, {sum(x.startswith('image_sample') for x in cl)} tex samples"
        print(f"  {c.key:<24} {c.tag:<34} ctor 0x{c.ctor:x} "
              f"VS #{c.vs} {('0x%x' % vs.va) if vs else '-'}  PS #{c.ps} {('0x%x' % ps.va) if ps else '-'}"
              f"  [{nsamp}]")
    used = {c.vs for c in classes} | {c.ps for c in classes}
    print(f"{len(classes)} registered shader classes; blobs not owned by a registered class: "
          f"{[b.index for b in blobs if b.index not in used]}")
    if a.dump:
        d = Path(a.dump)
        d.mkdir(parents=True, exist_ok=True)
        for b in blobs:
            (d / f"{b.index:02d}_{b.stage}.sb").write_bytes(eb.read_va(b.va, b.size))
    if a.json:
        Path(a.json).write_text(json.dumps({"blobs": [asdict(b) for b in blobs],
                                            "classes": [asdict(c) for c in classes]}, indent=1),
                                encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
