"""Tests for the container layer: tsuki.nam, tsuki.mrg, tsuki.mzp, tsuki.mzx.

Synthetic tests always run; real-data tests skip when the game folder is
missing (set TSUKI_GAME_DIR to override the default location).
Run:  cd D:/TsukihimePort/tools && ../.venv/Scripts/python.exe -m pytest tests/test_containers.py -q
"""
from __future__ import annotations

import hashlib
import mmap
import random
import struct
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tsuki import image_dir, mzx  # noqa: E402
from tsuki.mrg import HedFormat, MrgArchive, parse_hed  # noqa: E402
from tsuki.mzp import MzpArchive, decode_size, pad_after  # noqa: E402
from tsuki.nam import load_nam, looks_like_nam32, parse_nam32, parse_nam8  # noqa: E402
from tsuki.sniff import LV_HEADER, classify_mzp, image_header, riff_info, sniff  # noqa: E402

IMG = image_dir()
needs_game = pytest.mark.skipif(IMG is None, reason="game data not found")

TRIPLETS = ["allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4",
            "bgm", "vse", "vag", "parts", "voice", "voice2"]
EXPECTED_COUNTS = {"allpac0": 2449, "allpac1": 9968, "allpac1_2": 4317, "allpac2": 14745,
                   "allpac3": 136, "allpac4": 37, "bgm": 132, "vse": 1327, "vag": 19,
                   "parts": 24, "voice": 16464, "voice2": 2}


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------

def _mzx(size: int, body: bytes) -> bytes:
    return b"MZX0" + struct.pack("<I", size) + body


def test_mzx_literal_roundtrip() -> None:
    rnd = random.Random(1)
    for n in (0, 1, 2, 3, 127, 128, 129, 5000):
        raw = bytes(rnd.randrange(256) for _ in range(n))
        for inv in (False, True):
            enc = mzx.compress_literal(raw, inv)
            for numba in (False, True):
                assert mzx.decompress(enc, inv, use_numba=numba) == raw


def test_mzx_commands() -> None:
    # literal "ABCD" (2 words), RLE x2 of last ("CD"), backref dist 2 words len 2,
    # ringbuf slot 0 ("AB")
    body = bytes([0x07]) + b"ABCD" + bytes([0x04]) + bytes([0x05, 0x01]) + bytes([0x02])
    out = mzx.decompress(_mzx(14, body), use_numba=False)
    assert out == b"ABCD" + b"CDCD" + b"CDCD" + b"AB"
    assert mzx.decompress(_mzx(14, body), use_numba=True) == out
    # inverted stream: literal bytes are XOR 0xFF, initial last word 0xFFFF
    inv_body = bytes([0x03]) + bytes([0xBE, 0xBD]) + bytes([0x00])
    assert mzx.decompress(_mzx(4, inv_body), True, use_numba=False) == b"AB" + b"AB"
    assert mzx.decompress(_mzx(2, bytes([0x00])), True, use_numba=False) == b"\xff\xff"
    assert mzx.decompress(_mzx(2, bytes([0x00])), False, use_numba=False) == b"\x00\x00"


def test_mzx_reset_rule() -> None:
    # 0x1000 words of literal 0x1111 then one RLE word: with the reset rule the
    # RLE emits the initial value (0x0000), without it repeats 0x1111.
    words = 0x1000
    body = bytearray()
    for _ in range(words // 64):
        body.append(0xFF)                     # literal, 64 words
        body += b"\x11" * 128
    body.append(0x00)                         # RLE x1
    stream = _mzx(2 * words + 2, bytes(body))
    for numba in (False, True):
        assert mzx.decompress(stream, use_numba=numba)[-2:] == b"\x00\x00"
        assert mzx.decompress(stream, reset=False, use_numba=numba)[-2:] == b"\x11\x11"


def test_mzx_overshoot() -> None:
    # declared size 2, but the only command is a 64-word literal: the decoder
    # must consume the whole command, drop 126 surplus bytes and not overflow.
    body = bytes([0xFF]) + bytes(range(128))
    for size, over in ((2, 126), (3, 125), (1, 127), (128, 0)):
        for numba in (False, True):
            r = mzx.decompress_ex(_mzx(size, body), use_numba=numba)
            assert r.data == bytes(range(128))[:size]
            assert (r.overshoot, r.consumed) == (over, 8 + 129)
    assert mzx.MAX_OVERSHOOT == 127
    # 64-word RLE and backref commands overrun the same way
    rle = bytes([0x03]) + b"AB" + bytes([0xFC])            # literal 1 word, RLE x64
    assert mzx.decompress_ex(_mzx(4, rle), use_numba=False).overshoot == 126


def test_mzx_invert_is_xor_of_plain() -> None:
    # reset makes last = INIT (0xFFFF when inverted): decode-then-XOR equality
    # must hold across a 0x1000-word reset boundary as well.
    body = bytearray()
    for _ in range(0x1000 // 64):
        body.append(0xFF)
        body += bytes((i * 7) & 0xFF for i in range(128))
    body += bytes([0x00, 0x00, 0x05, 0x03, 0x02])     # RLE after reset, RLE, backref, ringbuf
    stream = _mzx(0x2000 + 2 + 2 + 4 + 2, bytes(body))
    for numba in (False, True):
        plain = mzx.decompress(stream, use_numba=numba)
        assert mzx.decompress(stream, True, use_numba=numba) == bytes(b ^ 0xFF for b in plain)
        assert plain[0x2000:0x2002] == b"\0\0"


def test_lv_parse_synthetic() -> None:
    raw = b"x" * 1000
    entry = b"LV\x03\x00\x00\x09\x00" + mzx.compress_literal(raw)
    h = mzx.parse_lv(entry)
    assert (h.present, h.magic, h.length, h.payload, h.body_offset) == (True, b"LV", 3, b"\x00\x09\x00", 7)
    d = mzx.decompress_lv(entry + b"\x0c\x00" * 50)        # trailing sector padding
    assert d.compressed and d.data == raw and d.consumed == len(entry)
    # other payload lengths, no LV chunk at all, raw (non-'M') body
    assert mzx.decompress_lv(b"LV\x01\x00\x07" + mzx.compress_literal(raw)).data == raw
    assert mzx.decompress_lv(mzx.compress_literal(raw)).header.present is False
    r = mzx.decompress_lv(b"LV\x00\x00RIFF....")
    assert not r.compressed and r.data == b"RIFF...."
    assert sniff(b"LV\x01\x00\x07" + mzx.compress_literal(raw)) == "lv-mzx"
    with pytest.raises(mzx.MzxError):
        mzx.parse_lv(b"LV\xff\x00abc")


def test_mzx_errors() -> None:
    with pytest.raises(mzx.MzxError):
        mzx.decompress(b"MZX1\0\0\0\0")
    with pytest.raises(mzx.MzxError):           # backref before start
        mzx.decompress(_mzx(2, bytes([0x01, 0x00])), use_numba=False)
    with pytest.raises(mzx.MzxError):           # truncated literal
        mzx.decompress(_mzx(4, bytes([0x07, 0x41])), use_numba=False)
    with pytest.raises(mzx.MzxError):           # input exhausted
        mzx.decompress(_mzx(8, bytes([0x03]) + b"AB"), use_numba=True)


def test_decode_size_wrap() -> None:
    assert decode_size(0x20, 0xFFF8) == 0xFFF8            # mangetsu's formula gives 0x1FFF8
    assert decode_size(0x21, 0x0008) == 0x10008
    assert decode_size(1, 0x0800) == 0x800
    assert decode_size(11, 0x5618) == 0x5618
    assert [pad_after(e) for e in (8, 9, 15, 16)] == [8, 7, 1, 8]


def test_mzp_synthetic() -> None:
    a, b = b"hello", b"world!!!"
    # section 0 at data_start (8+16=24), 5 bytes -> pad 3 -> section 1 at 32
    hdr = b"mrgd00" + struct.pack("<H", 2)
    hdr += struct.pack("<4H", 0, 0, 1, len(a)) + struct.pack("<4H", 0, 8, 1, len(b))
    blob = hdr + a + b"\xff" * 3 + b + b"\xff" * 8
    arc = MzpArchive(blob)
    assert [arc.read(0), arc.read(1)] == [a, b]
    assert arc.validate() == []
    assert arc.packed_size == len(blob)


def test_nam_synthetic() -> None:
    rec = b"ABC.MZP".ljust(30, b"\0") + b"\r\n"
    tagged = b"SE_A0018".ljust(28, b"\0") + b"se\r\n"
    t = parse_nam32(rec + tagged + b"\0" * 32)
    assert t.names == ["ABC.MZP", "SE_A0018"] and t.terminator and t.records[1].tag == "se"
    assert looks_like_nam32(rec + b"\0" * 32)
    t8 = parse_nam8(b"DU\0\0\0\0\0\0A00_\0\0\0\0\0\0")
    assert t8.names == ["DU", "A00_"] and t8.trailing == b"\0\0"


def test_hed_bank_rule_synthetic() -> None:
    # three entries of 0x80000 sectors: offsets 0, 0x80000 (nibble 8), 0x100000 (wraps to 0)
    recs = b""
    for off in (0, 0x80000, 0x100000):
        field = (off & 0xFFFF) | (((off >> 16) & 0xF) << 28)
        recs += struct.pack("<IHH", field, 0x8000, 0)  # size field irrelevant here
    t = parse_hed(recs + b"\xff" * 16, HedFormat.HED8)
    assert [e.sector_offset for e in t.entries] == [0, 0x80000, 0x100000]
    assert t.bank_starts == [2]
    assert not t.contiguous  # 0x8000-sector sizes do not fill the gaps


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------

@needs_game
@pytest.mark.parametrize("name", TRIPLETS)
def test_triplet_structure(name: str) -> None:
    assert IMG is not None
    with MrgArchive(IMG / name) as a:
        assert len(a) == EXPECTED_COUNTS[name]
        assert a.validate() == []
        assert a.hed.contiguous
        last = a.entries[-1]
        assert last.offset + last.span == a.file_size
        assert all(e.offset + e.span <= a.file_size for e in a.entries)
        if name in ("bgm",):
            assert a.hed.terminator == b""
        else:
            assert a.hed.terminator == b"\xff" * 16
        if name.startswith("allpac") or name.startswith("voice"):
            assert a.names_aligned()
        assert a.format == (HedFormat.HED4 if name.startswith("voice") else HedFormat.HED8)


@needs_game
def test_bank_starts_and_known_entries() -> None:
    assert IMG is not None
    with MrgArchive(IMG / "allpac1") as a:
        assert a.hed.bank_starts == [311, 799, 1403, 2212, 3449, 3725, 4197, 5033, 6199, 7280, 9115]
        assert a.entries[0].name == "IMG0000.MZP"
        assert a.file_size == 24257312768
    with MrgArchive(IMG / "allpac0") as a:
        e = a.entries[0]
        assert (e.name, e.offset, e.size, e.size_sectors) == ("BLACK.MZP", 0, 0x5618, 11)
        assert a.find("blue.mzp").index == 1


@needs_game
def test_nam_files() -> None:
    assert IMG is not None
    t = load_nam(IMG / "allpac3.nam")
    assert t.record_size == 32 and t.terminator and len(t) == 136
    assert t.names[:2] == ["CONF_PARTS_JA.MRG", "CONF_PARTS_EN.MRG"]
    v = load_nam(IMG / "voice.nam")
    assert v.record_size == 8 and len(v) == 16464 and v.trailing == b"\0\0"
    assert v.names[:3] == ["DU", "DU", "A00_"]


@needs_game
def test_mzp_entries_sample() -> None:
    assert IMG is not None
    rnd = random.Random(5)
    for name, n in (("allpac0", 40), ("allpac1", 40), ("allpac2", 200), ("allpac3", 20), ("parts", 24)):
        with MrgArchive(IMG / name) as a:
            for i in rnd.sample(range(len(a)), min(n, len(a))):
                e = a.entries[i]
                arc = MzpArchive(a.mm, e.offset, e.size)
                assert arc.validate() == [], (name, i)
                assert arc.packed_size == e.size, (name, i)
                assert classify_mzp(arc) in ("image", "archive"), (name, i)
                for j in range(len(arc)):
                    if arc.is_nested(j):
                        assert arc.child(j).validate() == []


@needs_game
def test_black_mzp_image() -> None:
    assert IMG is not None
    with MrgArchive(IMG / "allpac0") as a:
        e = a.entries[0]
        arc = MzpArchive(a.mm, e.offset, e.size)
        assert len(arc) == 21
        hdr = image_header(arc.head(0, 16))
        assert hdr is not None and (hdr["w"], hdr["h"], hdr["tiles_x"], hdr["tiles_y"]) == (1920, 1080, 4, 5)
        tile = mzx.decompress_ex(arc.read(1))
        assert len(tile.data) == 0x20000 and not any(tile.data)
        assert tile.consumed == arc.sections[1].size


@needs_game
def test_mzx_reset_rule_on_real_tile() -> None:
    """allpac2 #9325 tile 0: the clear-counter reset changes the output; the
    reset version is the artefact-free one (checked visually)."""
    assert IMG is not None
    with MrgArchive(IMG / "allpac2") as a:
        e = a.entries[9325]
        arc = MzpArchive(a.mm, e.offset, e.size)
        d = arc.read(1)
        good = mzx.decompress(d)
        assert hashlib.sha1(good).hexdigest() == "dcb1a237b4e61a6ffeb01fc03c308c32200c8ef8"
        assert mzx.decompress(d, reset=False) != good
        assert mzx.decompress(d, use_numba=False) == good


@needs_game
def test_mzx_plain_truecolor_tile() -> None:
    """IMG5004 ("END / Arcueid Brunestud Route Ending" caption): plain (non-inverted)
    MZX; opaque white text pixels inside the 240x87 area, transparent padding."""
    assert IMG is not None
    with MrgArchive(IMG / "allpac1") as a:
        e = a.entries[2271]
        assert e.name == "IMG5004.MZP"
        arc = MzpArchive(a.mm, e.offset, e.size)
        px = mzx.decompress(arc.read(1))
        assert len(px) == 512 * 512 * 4
        assert hashlib.sha1(px).hexdigest() == "bf4af1cdc2b1e31eb84976306e1e45ea99153080"
        assert px[:16] == b"\0" * 16
        o = (40 * 512 + 100) * 4
        assert px[o:o + 8] == b"\xff" * 8


@needs_game
def test_allscr_scripts() -> None:
    assert IMG is not None
    with open(IMG / "allscr.mrg", "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm)
            assert len(arc) == 552 and arc.validate() == [] and arc.packed_size == len(mm)
            names = parse_nam32(arc.read(0)).names
            assert len(names) == 549 and names[0] == "START_SCRIPT"
            s3 = mzx.decompress_ex(arc.read(3), invert=True)
            assert s3.data.startswith(b"_ZZ00a01();_ZZ00b01(START);")
            assert s3.consumed == arc.sections[3].size
            for k in (4, 100, 551):
                d = mzx.decompress(arc.read(k), invert=True)
                assert sniff(d[:256]) == "script-ascii"
            labels = arc.child(1)
            assert len(labels) == 549
            assert labels.read(0).startswith(b"START")
        finally:
            mm.close()


@needs_game
def test_standalone_files() -> None:
    assert IMG is not None
    for name, count in (("script_text", 10), ("ft_en", 65), ("ft_ja", 65), ("se_nam", 4)):
        data = (IMG / f"{name}.mrg").read_bytes()
        arc = MzpArchive(data)
        assert len(arc) == count
        assert arc.validate() == []
        assert arc.packed_size == len(data)
    se = MzpArchive((IMG / "se_nam.mrg").read_bytes())
    vse_names = parse_nam32(se.read(2)).names
    assert len(vse_names) == EXPECTED_COUNTS["vse"] and vse_names[0] == "DUMMY"


@needs_game
def test_voice_entries() -> None:
    assert IMG is not None
    rnd = random.Random(3)
    with MrgArchive(IMG / "voice") as a:
        for i in [0, 2, len(a) - 1] + rnd.sample(range(len(a)), 30):
            raw = a.read(i)
            assert raw[:7] == LV_HEADER
            res = mzx.decompress_ex(raw[7:])
            ri = riff_info(res.data)
            assert ri is not None and ri["codec"] == "atrac9"
            assert ri["riff_size"] == mzx.mzx_size(raw[7:15]) == len(res.data)
            assert 7 + res.consumed <= a.entries[i].span
        r2 = mzx.decompress_ex(a.read(2)[7:])
        assert (len(r2.data), r2.consumed) == (107236, 96206)


@needs_game
def test_hed4_edge_entries() -> None:
    """voice.hed entry 7850 is 'ff ff 1b 30' (word0 = 0xFFFF but a real entry:
    the engine treats a record as empty only if BOTH words are 0xFFFF).  The
    HED4 sector count is floor(exact/0x800)+1, which exceeds ceil() for the 4
    entries whose exact size is a multiple of 0x800."""
    assert IMG is not None
    with MrgArchive(IMG / "voice") as a:
        e = a.entries[7850]
        assert e.hed_raw == bytes.fromhex("ffff1b30")
        assert (e.sector_offset, e.size_sectors) == (0x3FFFF, 27)
        d = mzx.decompress_lv(a.read_sectors(7850))
        assert d.compressed and d.consumed == 53403 and d.data[:4] == b"RIFF"
        assert e.size_sectors == d.consumed // 0x800 + 1
        for i, exact, ss in ((3028, 94208, 47), (4044, 18432, 10), (13042, 26624, 14), (13595, 18432, 10)):
            d = mzx.decompress_lv(a.read_sectors(i))
            assert (d.consumed, a.entries[i].size_sectors) == (exact, ss)
            assert ss == exact // 0x800 + 1 == -(-exact // 0x800) + 1
        rnd = random.Random(11)
        for i in rnd.sample(range(len(a)), 40):
            d = mzx.decompress_lv(a.read_sectors(i))
            assert d.header.payload == b"\x00\x09\x00"
            assert a.entries[i].size_sectors == d.consumed // 0x800 + 1


@needs_game
def test_mzx_overshoot_real() -> None:
    """The last command routinely writes past the declared size (up to 127 B)."""
    assert IMG is not None
    with MrgArchive(IMG / "allpac4") as a:
        e = a.entries[0]
        arc = MzpArchive(a.mm, e.offset, e.size)
        for numba in (False, True):
            r = mzx.decompress_ex(arc.read(1), use_numba=numba)
            assert (len(r.data), r.overshoot, r.consumed) == (263200, 6, arc.sections[1].size)
    with open(IMG / "allscr.mrg", "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm)
            d = arc.read(294)
            for numba in (False, True):
                r = mzx.decompress_ex(d, True, use_numba=numba)
                assert (len(r.data), r.overshoot, r.consumed) == (7313, 127, len(d))
            # inverted decode == plain decode XOR 0xFF (engine behaviour)
            for k in (3, 294, 551):
                d = arc.read(k)
                assert mzx.decompress(d, True) == bytes(b ^ 0xFF for b in mzx.decompress(d))
            del arc, d
        finally:
            mm.close()


@needs_game
def test_script_text_tables() -> None:
    """Each BE u32 offset table holds exactly one 0xFFFFFFFF (its last entry);
    the 8 bytes of 0xFF after it are the mrgd00 inter-section pad."""
    assert IMG is not None
    data = (IMG / "script_text.mrg").read_bytes()
    arc = MzpArchive(data)
    for k in range(0, 10, 2):
        s = arc.sections[k]
        vals = struct.unpack(f">{s.size // 4}I", arc.read(k))
        assert len(vals) == 43972
        assert [i for i, v in enumerate(vals) if v == 0xFFFFFFFF] == [43971]
        assert vals[-2] == vals[-3] == arc.sections[k + 1].size
        assert data[s.end:arc.sections[k + 1].offset] == b"\xff" * pad_after(s.end) == b"\xff" * 8


@needs_game
def test_allpac3_dummy_mrg_is_an_image() -> None:
    assert IMG is not None
    blobs = set()
    with MrgArchive(IMG / "allpac3") as a:
        kinds: dict[tuple[str, str], int] = {}
        for e in a.entries:
            assert e.name is not None
            k = (e.name.rsplit(".", 1)[1], classify_mzp(MzpArchive(a.mm, e.offset, e.size)))
            kinds[k] = kinds.get(k, 0) + 1
        assert kinds == {("MRG", "archive"): 71, ("MZP", "image"): 64, ("MRG", "image"): 1}
        e = a.find("DUMMY.MRG")
        assert e.index == 135
        hdr = image_header(MzpArchive(a.mm, e.offset, e.size).head(0, 16))
        assert hdr is not None and (hdr["w"], hdr["h"], hdr["tiles_x"], hdr["tiles_y"]) == (16, 16, 1, 1)
        blobs.add(a.read(e.index))
    for name, idx, nm in (("allpac0", 2448, "DUMMY.MRG"), ("allpac1_2", 4316, "DUMMY.MRG"),
                          ("allpac1_2", 3291, "CGMODE_TOP.MRG"), ("allpac2", 14744, "DUMMY.MRG"),
                          ("allpac4", 36, "DUMMY.MRG")):
        with MrgArchive(IMG / name) as a:
            assert a.entries[idx].name == nm
            blobs.add(a.read(idx))
    assert len(blobs) == 1


@needs_game
def test_audio_sizes() -> None:
    assert IMG is not None
    for name in ("bgm", "vse"):
        with MrgArchive(IMG / name) as a:
            for e in a.entries:
                ri = riff_info(a.head(e.index, 0x80))
                assert ri is not None and ri["riff_size"] == e.size and ri["codec"] == "atrac9"
    with MrgArchive(IMG / "vag") as a:
        for e in a.entries:
            h = a.head(e.index, 48)
            assert h[:4] == b"VAGp" and struct.unpack_from(">I", h, 12)[0] + 48 == e.size
