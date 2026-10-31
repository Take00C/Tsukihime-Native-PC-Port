"""Engine tables used by the script interpreter, extracted from eboot.bin.

These are small numeric/name tables that live in the PS4 executable, not in the
data archives.  They are embedded here so that the script tools work without
eboot.bin; :func:`read_scene_ids` and :func:`read_movie_files` re-extract them
from a user's eboot.bin (tests compare both).

Address conventions (CUSA47999 v01.01 eboot.bin, see docs/formats/script.md):
text VA = file offset - 0x2280; data VA = file offset - 0x365FA0 + 0x600000.

* ``SCENE_IDS[i]``: scene id of allscr script i (i16 table at text VA 0x216130,
  549 entries).  ``_JUMP(n)`` and the title menu address scripts by scene id;
  the engine maps id -> index by linear search (VA 0x792D0 / 0x79320).
* ``MOVIE_FILES[m]``: file name of movie id m (48-byte records at data VA
  0xDC8C70: u64 id, u64 char* path, ...).  ``_TOMP(m)`` plays movie m.
* ``ENGINE_MAIN``: every command name the main interpreter (VA 0x95E60)
  accepts -> (handler VA, frame behaviour); 112 entries: 110 names plus
  ``'J*'`` / ``'Z*'`` (every name starting with that letter).  Produced by
  emulating the dispatch (unicorn) for every first letter and every 24-bit
  suffix breakpoint of the compare tree, cross-checked by brute force over all
  26 x 37^3 names in [A-Z][A-Z0-9_]{3} (scratch/script/dispatch_emu.py).  The
  handler VA is the start of the basic block holding the first instruction
  with a side effect.  Frame behaviour: ``'yield'`` = the handler always ends
  the interpreter's per-frame batch (returns to the caller), ``'continue'`` =
  never does, ``'mixed'`` = only on some paths (waits, jumps, scan mode).
  Names not listed (e.g. DMMY, NOP_, IFP4, SNX4) are skipped as no-ops.
* ``ENGINE_ALIASES``: names that share a handler with another name.
* ``OBJ_COMMANDS``: commands whose handler parses its arguments with the object
  parser (VA 0xD690; integer arguments through the expression evaluator
  VA 0xDA50) instead of the inline readers VA 0x76690/0x76780
  (scratch/script/argreaders.py).
"""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Union

__all__ = [
    "SCENE_IDS", "MOVIE_FILES", "ENGINE_MAIN", "ENGINE_ALIASES", "OBJ_COMMANDS", "KEEP_BGM_FLAG_VA",
    "SCENE_TABLE_VA", "MOVIE_TABLE_VA", "engine_handler", "is_engine_command",
    "scene_index", "read_scene_ids", "read_movie_files",
    "Keyframe", "Motion", "read_motions", "MOTION_COUNT",
]

SCENE_TABLE_VA = 0x216130
MOVIE_TABLE_VA = 0xDC8C70
TEXT_FILE_DELTA = 0x2280                     # file offset = text VA + 0x2280
DATA_FILE_BASE, DATA_VA_BASE = 0x365FA0, 0x600000

SCENE_IDS: tuple[int, ...] = (
    110, 240, 260, 270, 300, 301, 302, 303, 304, 305, 306, 307,
    308, 309, 310, 311, 312, 313, 314, 315, 316, 317, 318, 319,
    320, 321, 322, 323, 324, 325, 326, 327, 328, 329, 330, 331,
    332, 333, 334, 335, 336, 337, 338, 339, 340, 341, 342, 343,
    344, 345, 346, 347, 348, 349, 350, 351, 352, 353, 354, 355,
    356, 357, 358, 359, 360, 361, 362, 363, 364, 365, 366, 367,
    368, 369, 370, 371, 372, 373, 374, 375, 376, 377, 378, 379,
    380, 381, 382, 383, 384, 385, 386, 387, 388, 389, 390, 391,
    392, 393, 394, 395, 396, 397, 398, 399, 400, 401, 402, 403,
    404, 405, 406, 407, 408, 409, 410, 411, 412, 413, 414, 415,
    416, 417, 418, 419, 420, 421, 422, 423, 424, 425, 426, 427,
    428, 429, 430, 431, 432, 433, 434, 435, 436, 437, 438, 439,
    440, 441, 442, 443, 444, 445, 446, 447, 448, 449, 450, 451,
    452, 453, 454, 455, 456, 457, 458, 459, 460, 461, 462, 463,
    464, 465, 466, 467, 468, 469, 470, 471, 472, 473, 474, 475,
    476, 477, 478, 479, 480, 481, 482, 483, 484, 485, 486, 487,
    488, 489, 490, 491, 492, 493, 494, 495, 496, 497, 498, 499,
    500, 501, 502, 503, 504, 505, 506, 507, 508, 509, 510, 511,
    512, 513, 514, 515, 516, 517, 518, 519, 520, 521, 522, 523,
    524, 525, 526, 527, 528, 529, 530, 531, 532, 533, 534, 535,
    536, 537, 538, 539, 540, 541, 542, 543, 544, 545, 546, 547,
    548, 549, 550, 551, 552, 553, 554, 555, 556, 557, 558, 559,
    560, 561, 562, 563, 564, 565, 566, 567, 568, 569, 570, 571,
    572, 573, 574, 575, 576, 577, 578, 579, 580, 581, 582, 583,
    584, 585, 586, 587, 588, 589, 590, 591, 592, 593, 594, 595,
    596, 597, 598, 599, 600, 601, 602, 603, 604, 605, 606, 607,
    608, 609, 610, 611, 612, 613, 614, 615, 616, 617, 618, 619,
    620, 621, 622, 623, 624, 625, 626, 627, 628, 629, 630, 631,
    632, 633, 634, 635, 636, 637, 638, 639, 640, 641, 642, 643,
    644, 645, 646, 647, 648, 649, 650, 651, 652, 653, 654, 655,
    656, 657, 658, 659, 660, 661, 662, 663, 664, 665, 666, 667,
    668, 669, 670, 671, 672, 673, 674, 675, 676, 677, 678, 679,
    680, 681, 682, 683, 684, 685, 686, 687, 688, 689, 690, 691,
    692, 693, 694, 695, 696, 697, 698, 699, 700, 701, 702, 703,
    704, 705, 706, 707, 708, 709, 710, 711, 712, 713, 714, 715,
    716, 717, 718, 719, 720, 721, 722, 723, 724, 725, 726, 727,
    728, 729, 730, 731, 732, 733, 734, 735, 736, 737, 738, 739,
    740, 741, 742, 743, 744, 745, 746, 747, 748, 749, 750, 751,
    752, 753, 754, 755, 756, 757, 758, 759, 760, 761, 762, 763,
    764, 765, 766, 767, 768, 769, 770, 771, 772, 773, 774, 775,
    776, 777, 778, 779, 780, 781, 782, 783, 784, 785, 786, 787,
    788, 789, 790, 1000, 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008,
    1009, 1010, 1011, 1012, 1013, 1014, 1015, 1016, 1017, 1018, 1019, 1020,
    1021, 1022, 1023, 1024, 2000, 2001, 2002, 2003, 2004, 2005, 2006, 2007,
    3000, 3100, 4000, 8000, 8001, 8002, 8003, 8004, 8005, 8006, 8007, 8008,
    8009, 8010, 8011, 8012, 8013, 8014, 8015, 8016, 8017,
)

MOVIE_FILES: tuple[str, ...] = (
    'titlebg1.mp4', 'titlebg2.mp4', 'titlebg3.mp4',
    'titlebg4.mp4', 'prologue.mp4', 'op1_arc.mp4',
    'op2_ciel.mp4', 'phaseout.mp4', 'phasetitle_01_00.mp4',
    'phasetitle_02_00.mp4', 'phasetitle_03_00.mp4', 'phasetitle_04_00.mp4',
    'phasetitle_05_00.mp4', 'phasetitle_05_01.mp4', 'phasetitle_06_00.mp4',
    'phasetitle_06_01.mp4', 'phasetitle_07_00.mp4', 'phasetitle_07_01.mp4',
    'phasetitle_08_00.mp4', 'phasetitle_08_01.mp4', 'phasetitle_09_00.mp4',
    'phasetitle_09_01.mp4', 'phasetitle_10_00.mp4', 'phasetitle_10_01.mp4',
    'phasetitle_10_02.mp4', 'phasetitle_11_00.mp4', 'phasetitle_11_01.mp4',
    'phasetitle_12_00.mp4', 'phasetitle_12_01.mp4', 'phasetitle_13_00.mp4',
    'phasetitle_13_01.mp4', 'phasetitle_14_00.mp4', 'phasetitle_14_01.mp4',
    'phasetitle_15_01a.mp4', 'phasetitle_15_01b.mp4', 'ed01_arc.mp4',
    'ed02_ciel.mp4', 'ed03_ciel.mp4', 'TM_CI.mp4',
    'credit00.mp4', 'credit01.mp4', 'credit02.mp4',
    'credit03.mp4', 'credit04.mp4', 'credit05.mp4',
    'credit06.mp4', 'prologue_en.mp4', 'prologue_zc.mp4',
    'phasetitle_01_00_en.mp4', 'phasetitle_02_00_en.mp4', 'phasetitle_03_00_en.mp4',
    'phasetitle_04_00_en.mp4', 'phasetitle_05_00_en.mp4', 'phasetitle_05_01_en.mp4',
    'phasetitle_06_00_en.mp4', 'phasetitle_06_01_en.mp4', 'phasetitle_07_00_en.mp4',
    'phasetitle_07_01_en.mp4', 'phasetitle_08_00_en.mp4', 'phasetitle_08_01_en.mp4',
    'phasetitle_09_00_en.mp4', 'phasetitle_09_01_en.mp4', 'phasetitle_10_00_en.mp4',
    'phasetitle_10_01_en.mp4', 'phasetitle_10_02_en.mp4', 'phasetitle_11_00_en.mp4',
    'phasetitle_11_01_en.mp4', 'phasetitle_12_00_en.mp4', 'phasetitle_12_01_en.mp4',
    'phasetitle_13_00_en.mp4', 'phasetitle_13_01_en.mp4', 'phasetitle_14_00_en.mp4',
    'phasetitle_14_01_en.mp4', 'phasetitle_15_01a_en.mp4', 'phasetitle_15_01b_en.mp4',
    'phasetitle_01_00_zc.mp4', 'phasetitle_02_00_zc.mp4', 'phasetitle_03_00_zc.mp4',
    'phasetitle_04_00_zc.mp4', 'phasetitle_05_00_zc.mp4', 'phasetitle_05_01_zc.mp4',
    'phasetitle_06_00_zc.mp4', 'phasetitle_06_01_zc.mp4', 'phasetitle_07_00_zc.mp4',
    'phasetitle_07_01_zc.mp4', 'phasetitle_08_00_zc.mp4', 'phasetitle_08_01_zc.mp4',
    'phasetitle_09_00_zc.mp4', 'phasetitle_09_01_zc.mp4', 'phasetitle_10_00_zc.mp4',
    'phasetitle_10_01_zc.mp4', 'phasetitle_10_02_zc.mp4', 'phasetitle_11_00_zc.mp4',
    'phasetitle_11_01_zc.mp4', 'phasetitle_12_00_zc.mp4', 'phasetitle_12_01_zc.mp4',
    'phasetitle_13_00_zc.mp4', 'phasetitle_13_01_zc.mp4', 'phasetitle_14_00_zc.mp4',
    'phasetitle_14_01_zc.mp4', 'phasetitle_15_01a_zc.mp4', 'phasetitle_15_01b_zc.mp4',
    'op1_arc_en.mp4', 'op2_ciel_en.mp4', 'op1_arc_zc.mp4',
    'op2_ciel_zc.mp4', 'prologue_zt.mp4', 'phasetitle_01_00_zt.mp4',
    'phasetitle_02_00_zt.mp4', 'phasetitle_03_00_zt.mp4', 'phasetitle_04_00_zt.mp4',
    'phasetitle_05_00_zt.mp4', 'phasetitle_05_01_zt.mp4', 'phasetitle_06_00_zt.mp4',
    'phasetitle_06_01_zt.mp4', 'phasetitle_07_00_zt.mp4', 'phasetitle_07_01_zt.mp4',
    'phasetitle_08_00_zt.mp4', 'phasetitle_08_01_zt.mp4', 'phasetitle_09_00_zt.mp4',
    'phasetitle_09_01_zt.mp4', 'phasetitle_10_00_zt.mp4', 'phasetitle_10_01_zt.mp4',
    'phasetitle_10_02_zt.mp4', 'phasetitle_11_00_zt.mp4', 'phasetitle_11_01_zt.mp4',
    'phasetitle_12_00_zt.mp4', 'phasetitle_12_01_zt.mp4', 'phasetitle_13_00_zt.mp4',
    'phasetitle_13_01_zt.mp4', 'phasetitle_14_00_zt.mp4', 'phasetitle_14_01_zt.mp4',
    'phasetitle_15_01a_zt.mp4', 'phasetitle_15_01b_zt.mp4',
)

ENGINE_MAIN: dict[str, tuple[int, str]] = {
    'ATSV': (0x95f5d, 'mixed'),
    'CLO2': (0x9a887, 'yield'),
    'CLO3': (0x9a8b8, 'yield'),
    'CLO4': (0x9a8e7, 'yield'),
    'CLO5': (0x9a939, 'yield'),
    'EIF_': (0x96efc, 'continue'),
    'ELS_': (0x96f2a, 'continue'),
    'END_': (0x9ab42, 'yield'),
    'FADE': (0x97973, 'mixed'),
    'FADS': (0x9b06d, 'yield'),
    'FCCL': (0x982b5, 'mixed'),
    'FCLN': (0x96d12, 'mixed'),
    'FCSP': (0x982a2, 'mixed'),
    'FLNT': (0x9606a, 'mixed'),
    'FLOP': (0x982c8, 'mixed'),
    'FLSP': (0x9715e, 'mixed'),
    'GOTO': (0x96e3f, 'mixed'),
    'GOTS': (0x9609a, 'mixed'),
    'IFF_': (0x97fc8, 'continue'),
    'IFJP': (0x9806f, 'mixed'),
    'IFLA': (0x96d4f, 'mixed'),
    'IFNX': (0x97f00, 'continue'),
    'IFSC': (0x96185, 'mixed'),
    'IFUR': (0x98161, 'mixed'),
    'IF__': (0x9708d, 'mixed'),
    'J*': (0x961dc, 'mixed'),
    'MFAD': (0x978f5, 'mixed'),
    'MPLY': (0x96e2c, 'continue'),
    'MSAD': (0x9aece, 'yield'),
    'MSQK': (0x962d3, 'continue'),
    'MSTP': (0x978bd, 'continue'),
    'MVOL': (0x978aa, 'continue'),
    'PGST': (0x9637e, 'mixed'),
    'RDAS': (0x98118, 'continue'),
    'RDED': (0x9813b, 'mixed'),
    'RDST': (0x96dff, 'continue'),
    'RTM2': (0x97fbc, 'continue'),   # sets the keep-BGM flag VA 0x174DE0C only
    'RTMN': (0x9643e, 'mixed'),      # sets the keep-BGM flag, then the RTM_ code 0x97174
    'RTM_': (0x97174, 'mixed'),
    'RTST': (0x97131, 'continue'),
    'SACL': (0x99591, 'continue'),
    'SBAS': (0x99609, 'continue'),
    'SCH2': (0x98226, 'continue'),
    'SEAC': (0x99846, 'continue'),
    'SEFD': (0x99859, 'continue'),
    'SEFI': (0x98c8f, 'continue'),
    'SELA': (0x9b1b6, 'yield'),
    'SELD': (0x9b1dc, 'yield'),
    'SELR': (0x9936d, 'continue'),
    'SELS': (0x9b435, 'yield'),
    'SEP3': (0x99934, 'continue'),
    'SEPL': (0x98c8f, 'continue'),
    'SEPN': (0x996cb, 'continue'),
    'SESP': (0x996e1, 'continue'),
    'SEVL': (0x98c07, 'continue'),
    'SGMA': (0x99568, 'continue'),
    'SHAF': (0x99581, 'continue'),
    'SHAK': (0x971d5, 'mixed'),
    'SHAS': (0x9980a, 'continue'),
    'SHK2': (0x9982d, 'continue'),
    'SHOK': (0x98c44, 'continue'),
    'SHS2': (0x99673, 'continue'),
    'SNOW': (0x9968c, 'continue'),
    'SNX2': (0x98289, 'continue'),
    'SNX3': (0x9986c, 'continue'),
    'SOXY': (0x99885, 'continue'),
    'SPFC': (0x98cd0, 'continue'),
    'SPMA': (0x9929c, 'continue'),
    'SPRC': (0x992b6, 'continue'),
    'SPRD': (0x96a28, 'continue'),
    'SQK2': (0x992c7, 'continue'),
    'SQS2': (0x992e0, 'continue'),
    'SSS2': (0x986cf, 'continue'),
    'SSW2': (0x99d4b, 'continue'),
    'STBR': (0x990c2, 'continue'),
    'STCC': (0x97eed, 'continue'),
    'STCF': (0x9b448, 'yield'),
    'STCG': (0x99a4f, 'continue'),
    'STCH': (0x99ab7, 'continue'),
    'STCL': (0x99af0, 'continue'),
    'STCP': (0x99b03, 'continue'),
    'STEC': (0x9991b, 'continue'),
    'STGS': (0x99065, 'continue'),
    'STMA': (0x9969f, 'continue'),
    'STNS': (0x996b8, 'continue'),
    'STPV': (0x983ab, 'continue'),
    'STRA': (0x97264, 'continue'),
    'STRC': (0x9952f, 'continue'),
    'STRR': (0x99542, 'continue'),
    'STRT': (0x99555, 'continue'),
    'STTR': (0x99947, 'continue'),
    'STZ4': (0x99038, 'continue'),
    'TOMP': (0x96f4c, 'mixed'),
    'TOMW': (0x9ab56, 'yield'),
    'TRPY': (0x96fc3, 'continue'),
    'VCLR': (0x97277, 'continue'),   # stores -1 to VA 0x16541FC and 0x1654200 (unused by scripts)
    'VCWT': (0x96504, 'mixed'),
    'VPL2': (0x968b2, 'mixed'),
    'VPLY': (0x968b2, 'mixed'),
    'WKAD': (0x97ae6, 'continue'),
    'WKBO': (0x97bb8, 'continue'),
    'WKOF': (0x96b98, 'continue'),
    'WKOR': (0x9798b, 'continue'),
    'WKST': (0x96566, 'continue'),
    'WNTY': (0x972f5, 'continue'),
    'WTSV': (0x9af36, 'yield'),
    'WTTM': (0x9ac1a, 'yield'),
    'WTTR': (0x9afd1, 'yield'),
    'WT__': (0x9ac1a, 'yield'),
    'XRSM': (0x96e52, 'continue'),
    'XSTP': (0x965e5, 'continue'),
    'Z*': (0x96615, 'mixed'),
}

ENGINE_ALIASES: dict[str, str] = {
    "SEFI": "SEPL", "VPL2": "VPLY", "WT__": "WTTM",
}

# RTM2 / RTMN set this byte; the script-end cleanup (VA 0x7F1A0, called by END_,
# RTM_, RTMN) then clears it instead of stopping the BGM (VA 0x9AE0).
KEEP_BGM_FLAG_VA = 0x174DE0C

OBJ_COMMANDS: frozenset[str] = frozenset({
    "CLO4", "CLO5", "FADE", "FADS", "FCCL", "FCLN", "FCSP", "FLNT", "FLOP", "FLSP", "MSQK",
    "RDAS", "RDED", "RDST", "RTST", "SACL", "SBAS", "SCH2", "SELA", "SELS", "SGMA", "SHK2",
    "SHS2", "SNX2", "SNX3", "SOXY", "SPFC", "SPMA", "SPRD", "SQK2", "SQS2", "SSS2", "SSW2",
    "STBR", "STCC", "STCF", "STCH", "STCL", "STCP", "STEC", "STGS", "STMA", "STNS", "STRA",
    "STRC", "STRR", "STRT", "STTR", "STZ4", "TOMP", "WTSV", "WTTR",
})


def engine_handler(name: str) -> tuple[int, str] | None:
    """(handler VA, frame behaviour) for a 4-character command name, or None
    when the PS4 main interpreter ignores the command."""
    if name[:1] in ("J", "Z"):
        return ENGINE_MAIN[name[0] + "*"]
    return ENGINE_MAIN.get(name)


def is_engine_command(name: str) -> bool:
    return engine_handler(name) is not None


_SCENE_INDEX = {sid: i for i, sid in enumerate(SCENE_IDS)}


def scene_index(scene_id: int) -> int | None:
    """Script index for a scene id (engine: linear search of the table)."""
    return _SCENE_INDEX.get(scene_id)


Pathish = Union[str, Path]


def read_scene_ids(eboot: Pathish, count: int = 549) -> tuple[int, ...]:
    """Re-extract the scene-id table from an eboot.bin (fake-signed SELF)."""
    with open(eboot, "rb") as f:
        f.seek(SCENE_TABLE_VA + TEXT_FILE_DELTA)
        return struct.unpack(f"<{count}h", f.read(2 * count))


def read_movie_files(eboot: Pathish, count: int = 134) -> tuple[str, ...]:
    """Re-extract the movie table (id -> file name) from an eboot.bin."""
    data = Path(eboot).read_bytes()
    base = MOVIE_TABLE_VA - DATA_VA_BASE + DATA_FILE_BASE
    out: list[str] = []
    for i in range(count):
        rid, ptr = struct.unpack_from("<QQ", data, base + 48 * i)
        if rid != i:
            raise ValueError(f"movie record {i} has id {rid}")
        o = ptr + TEXT_FILE_DELTA
        path = data[o:data.index(b"\0", o)].decode("ascii")
        out.append(path.rsplit("/", 1)[1])
    return tuple(out)


# ---------------------------------------------------------------------------
# layer motions (_STMA / _SGMA): keyframe animations compiled into eboot.bin
# ---------------------------------------------------------------------------
# accessors at VA 0xC2150 (duration), 0xC2170 (end index), 0xC2190 (field k
# of keyframe i, k = 0..27); consumer VA 0xBE4C0 (per-layer animation step),
# setter VA 0xC0330 (_STMA handler body).
MOTION_COUNT = 0x5E80                  # ids 1..24192 (0xC2152: cmp edi-1, 0x5E7F)
MOTION_DURATION_VA = 0x274538          # i64[MOTION_COUNT], text segment
MOTION_END_VA = 0x2A3938               # i64[MOTION_COUNT]: exclusive end keyframe index
MOTION_KEYS_VA = 0x873FF0              # 72-byte keyframes, data segment
MOTION_KEY_SIZE = 72
MOTION_MAX_KEY = 0x118E0               # 0xC2197: highest valid keyframe index
MOTION_NULL = -32767                   # i16 "field not animated" marker


@dataclass(frozen=True)
class Keyframe:
    time: int                     # field 0: u64 at +0 (milliseconds from motion start)
    fields: tuple[int, ...]       # fields 1..27: i16 at +8..+0x14 (1..7), i64 at +0x18 (8),
                                  # i16 at +0x20..+0x44 (9..27); -32767 = not set


@dataclass(frozen=True)
class Motion:
    id: int
    duration: int                 # ms (equals the last keyframe time for 24,135 of 24,192)
    keyframes: tuple[Keyframe, ...]


def _keyframe(data: bytes, off: int) -> Keyframe:
    t = struct.unpack_from("<q", data, off)[0]
    a = struct.unpack_from("<7h", data, off + 8)
    b = struct.unpack_from("<q", data, off + 0x18)
    c = struct.unpack_from("<19h", data, off + 0x20)
    return Keyframe(t, a + b + c)


def read_motions(eboot: Pathish) -> list[Motion]:
    """All layer motions (index i = motion id i + 1).  Motion k uses keyframes
    [end[k-2], end[k-1]) (start 0 for k = 1)."""
    data = Path(eboot).read_bytes()
    n = MOTION_COUNT
    dur = struct.unpack_from(f"<{n}q", data, MOTION_DURATION_VA + TEXT_FILE_DELTA)
    end = struct.unpack_from(f"<{n}q", data, MOTION_END_VA + TEXT_FILE_DELTA)
    base = MOTION_KEYS_VA - DATA_VA_BASE + DATA_FILE_BASE
    out: list[Motion] = []
    start = 0
    for k in range(n):
        keys = tuple(_keyframe(data, base + MOTION_KEY_SIZE * i) for i in range(start, end[k]))
        out.append(Motion(k + 1, dur[k], keys))
        start = end[k]
    return out
