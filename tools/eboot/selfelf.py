"""SELF + ELF64 reader for PS4 executables (fake-signed / decrypted SELF).

Layout (verified on CUSA47999 eboot.bin, see docs/formats/engine.md section 2):

SELF header (little-endian)
    0x00 u32  magic 0x1D3D154F  (bytes 4F 15 3D 1D)
    0x04 u8   version, u8 mode, u8 endian, u8 attributes
    0x08 u32  key type
    0x0C u16  header size, 0x0E u16 metadata size
    0x10 u64  file size
    0x18 u16  segment count, 0x1A u16 flags
    0x20      segment table, 32 bytes each:
                u64 flags   (bits 20..31 = ELF program header index,
                             0x800 = blocked, 0x8 = deflated, 0x2 = encrypted)
                u64 file offset, u64 compressed size, u64 decompressed size
    then      the embedded ELF64 header + program headers.

PS4 "SCE" dynamic linking: PT_SCE_DYNLIBDATA (0x61000000) holds the string
table, symbol table and relocations; the DT_SCE_* values are offsets relative
to the start of that segment.  Symbol names of imports are
"<11-char NID>#<library id>#<module id>" with ids in a base64-like alphabet.
"""
from __future__ import annotations

import mmap
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, Iterator, List, Optional, Tuple

SELF_MAGIC = b"\x4f\x15\x3d\x1d"

PT_NAMES: Dict[int, str] = {
    0: "NULL", 1: "LOAD", 2: "DYNAMIC", 3: "INTERP", 4: "NOTE", 7: "TLS",
    0x60000000: "SCE_RELA", 0x61000000: "SCE_DYNLIBDATA", 0x61000001: "SCE_PROCPARAM",
    0x61000002: "SCE_MODULE_PARAM", 0x61000010: "SCE_RELRO",
    0x6FFFFF00: "SCE_COMMENT", 0x6FFFFF01: "SCE_LIBVERSION", 0x6474E550: "GNU_EH_FRAME",
}

DT_NAMES: Dict[int, str] = {
    0: "NULL", 1: "NEEDED", 5: "STRTAB", 6: "SYMTAB", 7: "RELA", 8: "RELASZ", 9: "RELAENT",
    10: "STRSZ", 11: "SYMENT", 12: "INIT", 13: "FINI", 14: "SONAME", 15: "RPATH",
    16: "SYMBOLIC", 20: "PLTREL", 21: "DEBUG", 22: "TEXTREL", 24: "BIND_NOW",
    25: "INIT_ARRAY", 26: "FINI_ARRAY", 27: "INIT_ARRAYSZ", 28: "FINI_ARRAYSZ",
    30: "FLAGS", 32: "PREINIT_ARRAY", 33: "PREINIT_ARRAYSZ",
    0x61000005: "SCE_IDTABENTSZ", 0x61000007: "SCE_FINGERPRINT",
    0x61000009: "SCE_ORIGINAL_FILENAME", 0x6100000D: "SCE_MODULE_INFO",
    0x6100000F: "SCE_NEEDED_MODULE", 0x61000011: "SCE_MODULE_ATTR",
    0x61000013: "SCE_EXPORT_LIB", 0x61000015: "SCE_IMPORT_LIB",
    0x61000017: "SCE_EXPORT_LIB_ATTR", 0x61000019: "SCE_IMPORT_LIB_ATTR",
    0x6100001D: "SCE_STUB_MODULE_NAME", 0x6100001F: "SCE_STUB_MODULE_VERSION",
    0x61000021: "SCE_STUB_LIBRARY_NAME", 0x61000023: "SCE_STUB_LIBRARY_VERSION",
    0x61000025: "SCE_HASH", 0x61000027: "SCE_PLTGOT", 0x61000029: "SCE_JMPREL",
    0x6100002B: "SCE_PLTREL", 0x6100002D: "SCE_PLTRELSZ", 0x6100002F: "SCE_RELA",
    0x61000031: "SCE_RELASZ", 0x61000033: "SCE_RELAENT", 0x61000035: "SCE_STRTAB",
    0x61000037: "SCE_STRSZ", 0x61000039: "SCE_SYMTAB", 0x6100003B: "SCE_SYMENT",
    0x6100003D: "SCE_HASHSZ", 0x6100003F: "SCE_SYMTABSZ",
}

R_X86_64_64 = 1
R_X86_64_GLOB_DAT = 6
R_X86_64_JUMP_SLOT = 7
R_X86_64_RELATIVE = 8
R_X86_64_DTPMOD64 = 16

_ID_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-"


def decode_id(s: str) -> int:
    """Decode the library/module id part of an encoded PS4 symbol name."""
    v = 0
    for ch in s:
        i = _ID_ALPHABET.find(ch)
        if i < 0:
            raise ValueError(f"bad id character {ch!r} in {s!r}")
        v = v * 64 + i
    return v


@dataclass(frozen=True)
class SelfSegment:
    index: int
    flags: int
    offset: int
    comp_size: int
    dec_size: int

    @property
    def phdr_id(self) -> int:
        return (self.flags >> 20) & 0xFFF

    @property
    def blocked(self) -> bool:
        return bool(self.flags & 0x800)

    @property
    def deflated(self) -> bool:
        return bool(self.flags & 0x8)

    @property
    def encrypted(self) -> bool:
        return bool(self.flags & 0x2)


@dataclass(frozen=True)
class Phdr:
    index: int
    type: int
    flags: int
    offset: int
    vaddr: int
    paddr: int
    filesz: int
    memsz: int
    align: int

    @property
    def type_name(self) -> str:
        return PT_NAMES.get(self.type, hex(self.type))

    @property
    def perms(self) -> str:
        return ("R" if self.flags & 4 else "-") + ("W" if self.flags & 2 else "-") + (
            "X" if self.flags & 1 else "-")


@dataclass(frozen=True)
class Symbol:
    index: int
    name: str
    info: int
    other: int
    shndx: int
    value: int
    size: int

    @property
    def bind(self) -> int:
        return self.info >> 4

    @property
    def type(self) -> int:
        return self.info & 0xF


@dataclass(frozen=True)
class Rela:
    offset: int     # VA patched
    sym: int
    type: int
    addend: int


@dataclass
class ModuleRef:
    id: int
    name: str
    version_major: int
    version_minor: int


@dataclass
class LibraryRef:
    id: int
    name: str
    version: int
    attr: Optional[int] = None


@dataclass
class Import:
    """One imported symbol (function via PLT/GOT or data object)."""
    sym_index: int
    nid: str
    library: str
    module: str
    lib_id: int
    mod_id: int
    sym_type: int          # 2 = FUNC, 1 = OBJECT, 6 = TLS
    got_va: Optional[int]  # GOT slot VA (JUMP_SLOT) or data slot (GLOB_DAT/64)
    reloc_type: int
    plt_va: Optional[int] = None   # PLT stub VA in text (filled by find_plt)
    name: Optional[str] = None     # resolved name (filled by eboot.nids)


@dataclass
class Eboot:
    path: Path
    data: mmap.mmap
    self_segments: List[SelfSegment]
    elf_off: int
    entry: int
    phdrs: List[Phdr]
    dynamic: List[Tuple[int, int]] = field(default_factory=list)

    # ------------------------------------------------------------------ open
    @classmethod
    def open(cls, path: Path) -> "Eboot":
        f = open(path, "rb")
        try:
            m = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        finally:
            f.close()
        if m[:4] != SELF_MAGIC:
            raise ValueError(f"{path}: not a SELF (magic {m[:4].hex()})")
        nseg = struct.unpack_from("<H", m, 0x18)[0]
        segs = []
        for i in range(nseg):
            fl, off, cs, ds = struct.unpack_from("<QQQQ", m, 0x20 + i * 0x20)
            segs.append(SelfSegment(i, fl, off, cs, ds))
        elf_off = 0x20 + nseg * 0x20
        if m[elf_off:elf_off + 4] != b"\x7fELF":
            raise ValueError(f"{path}: no ELF header at 0x{elf_off:x}")
        (e_type, e_machine, e_version, e_entry, e_phoff, e_shoff, e_flags, e_ehsize,
         e_phentsize, e_phnum) = struct.unpack_from("<HHIQQQIHHH", m, elf_off + 0x10)
        phdrs = []
        for i in range(e_phnum):
            t, fl, off, va, pa, fs, ms, al = struct.unpack_from(
                "<IIQQQQQQ", m, elf_off + e_phoff + i * e_phentsize)
            phdrs.append(Phdr(i, t, fl, off, va, pa, fs, ms, al))
        eb = cls(Path(path), m, segs, elf_off, e_entry, phdrs)
        for s in segs:
            if s.blocked and (s.deflated or s.encrypted):
                raise ValueError(f"segment {s.index} is compressed/encrypted; need a decrypted SELF")
        eb.dynamic = eb._parse_dynamic()
        return eb

    def close(self) -> None:
        self.data.close()

    # ------------------------------------------------------------ mapping
    def phdr_file_offset(self, ph: Phdr) -> Optional[int]:
        """SELF file offset where program header `ph`'s file bytes start."""
        for s in self.self_segments:
            if s.blocked and s.phdr_id == ph.index:
                return s.offset
        return None

    def elf_to_file(self, elf_off: int) -> int:
        """Map an ELF file offset to a SELF file offset."""
        for ph in self.phdrs:
            if ph.filesz and ph.offset <= elf_off < ph.offset + ph.filesz:
                base = self.phdr_file_offset(ph)
                if base is not None:
                    return base + (elf_off - ph.offset)
        raise KeyError(f"ELF offset 0x{elf_off:x} is not backed by a SELF segment")

    def load_segments(self) -> List[Phdr]:
        return [p for p in self.phdrs if p.type in (1, 0x61000010)]

    def va_to_file(self, va: int) -> Optional[int]:
        """SELF file offset for a VA inside a loaded segment's file image, else None
        (VA in .bss or unmapped)."""
        for ph in self.load_segments():
            if ph.vaddr <= va < ph.vaddr + ph.filesz:
                base = self.phdr_file_offset(ph)
                if base is None:
                    return None
                return base + (va - ph.vaddr)
        return None

    def segment_of(self, va: int) -> Optional[Phdr]:
        for ph in self.load_segments():
            if ph.vaddr <= va < ph.vaddr + ph.memsz:
                return ph
        return None

    def read_va(self, va: int, n: int) -> bytes:
        """Read n bytes at VA. Bytes in .bss (beyond filesz) read as zero."""
        ph = self.segment_of(va)
        if ph is None:
            raise KeyError(f"VA 0x{va:x} not mapped")
        out = bytearray(n)
        avail = max(0, min(n, ph.vaddr + ph.filesz - va))
        if avail:
            fo = self.va_to_file(va)
            assert fo is not None
            out[:avail] = self.data[fo:fo + avail]
        return bytes(out)

    def u64(self, va: int) -> int:
        return struct.unpack("<Q", self.read_va(va, 8))[0]

    def u32(self, va: int) -> int:
        return struct.unpack("<I", self.read_va(va, 4))[0]

    def cstr(self, va: int, limit: int = 4096) -> bytes:
        fo = self.va_to_file(va)
        if fo is None:
            return b""
        end = self.data.find(b"\0", fo, fo + limit)
        return bytes(self.data[fo:end if end >= 0 else fo + limit])

    def segment_bytes(self, ph: Phdr) -> bytes:
        base = self.phdr_file_offset(ph)
        if base is None:
            raise KeyError(f"phdr {ph.index} has no SELF data")
        return bytes(self.data[base:base + ph.filesz])

    @property
    def text(self) -> Phdr:
        return next(p for p in self.phdrs if p.type == 1 and p.flags & 1)

    @property
    def data_seg(self) -> Phdr:
        return next(p for p in self.phdrs if p.type == 1 and p.flags & 2)

    @property
    def relro(self) -> Optional[Phdr]:
        return next((p for p in self.phdrs if p.type == 0x61000010), None)

    # ------------------------------------------------------------ dynamic
    def _phdr(self, t: int) -> Optional[Phdr]:
        return next((p for p in self.phdrs if p.type == t), None)

    def _parse_dynamic(self) -> List[Tuple[int, int]]:
        ph = self._phdr(2)
        if ph is None:
            return []
        fo = self.elf_to_file(ph.offset)
        out = []
        for i in range(ph.filesz // 16):
            tag, val = struct.unpack_from("<qQ", self.data, fo + i * 16)
            out.append((tag & 0xFFFFFFFFFFFFFFFF, val))
            if tag == 0:
                break
        return out

    def dyn(self, tag: int) -> List[int]:
        return [v for t, v in self.dynamic if t == tag]

    def dyn1(self, tag: int) -> int:
        v = self.dyn(tag)
        if len(v) != 1:
            raise KeyError(f"dynamic tag {DT_NAMES.get(tag, hex(tag))}: {len(v)} entries")
        return v[0]

    def dynlib_file(self) -> int:
        ph = self._phdr(0x61000000)
        if ph is None:
            raise KeyError("no SCE_DYNLIBDATA")
        fo = self.phdr_file_offset(ph)
        if fo is None:
            fo = self.elf_to_file(ph.offset)
        return fo

    def dynstr(self, off: int) -> str:
        base = self.dynlib_file() + self.dyn1(0x61000035)
        end = self.data.find(b"\0", base + off)
        return self.data[base + off:end].decode("ascii", "replace")

    def modules(self) -> Dict[int, ModuleRef]:
        out: Dict[int, ModuleRef] = {}
        for v in self.dyn(0x6100000F):
            name = self.dynstr(v & 0xFFFFFFFF)
            out[(v >> 48) & 0xFFFF] = ModuleRef((v >> 48) & 0xFFFF, name, (v >> 40) & 0xFF,
                                                (v >> 32) & 0xFF)
        return out

    def libraries(self) -> Dict[int, LibraryRef]:
        out: Dict[int, LibraryRef] = {}
        for v in self.dyn(0x61000015):
            lid = (v >> 48) & 0xFFFF
            out[lid] = LibraryRef(lid, self.dynstr(v & 0xFFFFFFFF), (v >> 32) & 0xFFFF)
        for v in self.dyn(0x61000019):
            lid = (v >> 48) & 0xFFFF
            if lid in out:
                out[lid].attr = v & 0xFFFFFFFFFFFF
        return out

    def module_info(self) -> Tuple[str, int]:
        v = self.dyn1(0x6100000D)
        return self.dynstr(v & 0xFFFFFFFF), v >> 32

    def symbols(self) -> List[Symbol]:
        base = self.dynlib_file() + self.dyn1(0x61000039)
        n = self.dyn1(0x6100003F) // 24
        out = []
        for i in range(n):
            st_name, info, other, shndx, value, size = struct.unpack_from(
                "<IBBHQQ", self.data, base + i * 24)
            out.append(Symbol(i, self.dynstr(st_name), info, other, shndx, value, size))
        return out

    def _relas(self, off_tag: int, size_tag: int) -> List[Rela]:
        base = self.dynlib_file() + self.dyn1(off_tag)
        n = self.dyn1(size_tag) // 24
        out = []
        for i in range(n):
            off, info, add = struct.unpack_from("<QQq", self.data, base + i * 24)
            out.append(Rela(off, info >> 32, info & 0xFFFFFFFF, add))
        return out

    def jmprel(self) -> List[Rela]:
        return self._relas(0x61000029, 0x6100002D)

    def rela(self) -> List[Rela]:
        return self._relas(0x6100002F, 0x61000031)

    # ------------------------------------------------------------ imports
    def imports(self) -> List[Import]:
        """All imported symbols with their GOT slot.  Order: JMPREL (functions) then
        RELA symbol relocations (data imports)."""
        syms = self.symbols()
        libs = self.libraries()
        mods = self.modules()
        out: List[Import] = []
        seen = set()
        for r in self.jmprel() + [r for r in self.rela() if r.sym]:
            s = syms[r.sym]
            if "#" not in s.name or s.shndx != 0:
                continue
            nid, lid, mid = s.name.split("#")
            li, mi = decode_id(lid), decode_id(mid)
            key = (r.sym, r.offset)
            if key in seen:
                continue
            seen.add(key)
            out.append(Import(r.sym, nid, libs[li].name if li in libs else f"lib#{li}",
                              mods[mi].name if mi in mods else f"mod#{mi}", li, mi, s.type,
                              r.offset, r.type))
        return out

    def find_plt(self, imps: List[Import]) -> int:
        """Locate PLT stubs (FF 25 jmp [rip+disp]) whose target is an import's GOT slot.
        Fills Import.plt_va; returns the number found."""
        text = self.text
        code = self.segment_bytes(text)
        by_got = {i.got_va: i for i in imps if i.reloc_type == R_X86_64_JUMP_SLOT}
        found = 0
        pos = code.find(b"\xff\x25")
        while pos >= 0:
            disp = struct.unpack_from("<i", code, pos + 2)[0]
            tgt = text.vaddr + pos + 6 + disp
            imp = by_got.get(tgt)
            if imp is not None and imp.plt_va is None:
                imp.plt_va = text.vaddr + pos
                found += 1
            pos = code.find(b"\xff\x25", pos + 1)
        return found

    def iter_strings(self, ph: Phdr, min_len: int = 4) -> Iterator[Tuple[int, bytes]]:
        """NUL-terminated printable (ASCII or UTF-8) strings in a segment."""
        buf = self.segment_bytes(ph)
        i, n = 0, len(buf)
        while i < n:
            j = buf.find(b"\0", i)
            if j < 0:
                j = n
            s = buf[i:j]
            if len(s) >= min_len and _printable(s):
                yield ph.vaddr + i, s
            i = j + 1


def _printable(s: bytes) -> bool:
    try:
        t = s.decode("utf-8")
    except UnicodeDecodeError:
        return False
    return all(c.isprintable() or c in "\t\r\n" for c in t)


def _main(argv: List[str]) -> int:
    import argparse

    from eboot import eboot_path

    ap = argparse.ArgumentParser(description="Dump SELF/ELF structure of eboot.bin")
    ap.add_argument("path", nargs="?", help="eboot.bin (default: game folder)")
    a = ap.parse_args(argv)
    p = Path(a.path) if a.path else eboot_path()
    if p is None:
        print("game not found", file=sys.stderr)
        return 2
    eb = Eboot.open(p)
    print(f"SELF segments ({len(eb.self_segments)}):")
    for s in eb.self_segments:
        print(f"  [{s.index}] flags=0x{s.flags:08x} phdr={s.phdr_id} blocked={s.blocked} "
              f"off=0x{s.offset:x} size=0x{s.comp_size:x}")
    print(f"ELF at file 0x{eb.elf_off:x}, entry VA 0x{eb.entry:x}")
    for ph in eb.phdrs:
        fo = eb.phdr_file_offset(ph)
        print(f"  phdr[{ph.index}] {ph.type_name:<16} {ph.perms} elfoff=0x{ph.offset:x} "
              f"va=0x{ph.vaddr:x} filesz=0x{ph.filesz:x} memsz=0x{ph.memsz:x} "
              f"self_off={'0x%x' % fo if fo is not None else '-'}")
    print("dynamic:")
    for t, v in eb.dynamic:
        print(f"  {DT_NAMES.get(t, hex(t)):<26} 0x{v:x}")
    name, ver = eb.module_info()
    print(f"module: {name} (0x{ver:x})")
    for m in eb.modules().values():
        print(f"  needs module {m.id:2d} {m.name} v{m.version_major}.{m.version_minor}")
    for lib in eb.libraries().values():
        print(f"  imports lib {lib.id:2d} {lib.name} v{lib.version} attr={lib.attr}")
    eb.close()
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
