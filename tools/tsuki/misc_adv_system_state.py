"""ADV system state of TSUKIHIME -A piece of blue glass moon- (PS4 CUSA47999 v01.01).

Field maps for everything a runtime must keep and restore between lines, scenes and
sessions (docs/formats/adv_state.md):

* ``HISTORY_FIELDS``   the 0x3A10-byte history record (ring of 800 at *0x120A6E0, scene
                       stack of 300 at 0x120A700, save-slot states A/B): for every byte
                       range the RAM variable History_Push (VA 0x7AF90) copies, the writer
                       VA, and where History_Restore (VA 0x7C6D0) puts it back.
* ``HISTORY_MODES``    the record kind (record +0x24) per pushing command and the restore
                       branch it selects (jump table VA 0x20BD9C).
* ``PUSH_SITES``       all 59 direct calls of History_Push (kind argument, command, live or
                       dead code); ``scan_push_sites`` re-derives them from the eboot.
* ``ATSV_PATHS``       the `_ATSV` command (Script_Step 0x95F5D): scene-start / choice push,
                       scene-stack push, auto save.
* ``SCENE_STATE_FIELDS`` every byte of the 0x1A08-byte scene_state 0x16500C0 (51 per-layer
                       arrays, 29 scalar globals, 4 unreferenced gaps) with its writers
                       (stored twice per record, at +0x58 and +0x1A60).
* ``CONFIG_FIELDS``    the 0x140-byte config block 0x66C578 (= system block +0x04): menu
                       item, default, range and the code that reads it.
* ``ACTIONS``          the 27 action masks (three presets at 0xDB84F0, selected by
                       config +0x42) and ``GAME_INPUT_BITS`` (the bit space they use).

Readers: ``parse_history_record``, ``history_f_values``, ``slot_states``,
``read_action_tables``, ``read_config_pages``, ``read_static_config``, ``check_save_image``
(the acceptance checks for a real save: script, offset, date, readmax).
Emulation checks of the maps live in ``tsuki.misc_adv_system_state_emu``.

CLI: python -m tsuki.misc_adv_system_state tables | save [PATH] | dump OUTDIR
"""
from __future__ import annotations

import json
import re
import struct
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple, Union

TEXT_OFF = 0x2280                       # text / rodata VA = file offset - 0x2280
DATA_OFF, DATA_VA = 0x365FA0, 0x600000  # data VA = file offset - 0x365FA0 + 0x600000

# ---------------------------------------------------------------------------
# RAM addresses (data / bss VAs of v01.01)
# ---------------------------------------------------------------------------
HISTORY_PTR = 0x120A6E0          # -> 808 records allocated (0xB74280 bytes, VA 0x753E0); ring uses 800
RING_COUNT = 800                 # 0x320, wrap at VA 0x7B44C
RING_ALLOC = 808                 # bounds check 0x327 at VA 0x7B4C3
RECORD_SIZE = 0x3A10
RING_SERIAL = 0x120A6EA          # i16 serial counter -> record +0x04
RING_CUR = 0x120A6EC             # i16 index of the newest record
RING_NEXT = 0x120A6EE            # i16 index the next push writes
RING_WRAPPED = 0x120A6F0         # u16 1 once the ring wrapped
RING_OLDEST = 0x120A6F2          # i16 oldest valid index once wrapped
REWIND_CURSOR = 0x120A6E8        # i16 record the rewind is positioned on
SCENE_STACK = 0x120A700          # 300 records (scene-start snapshots), ends at 0x164B1C0
SCENE_STACK_COUNT = 300
SCENE_STACK_TOP = 0x164B1C0      # i16 top index (-1 = empty)
SLOT_STAGING = 0x12031E0         # 0x7108-byte slot image kept current while playing
QUICK_SLOT = 139                 # slots[139] (0x11FA558) = quick save
AUTO_SLOTS = range(100, 139)     # FIFO of automatic saves (VA 0x73B70)
MANUAL_SLOTS = range(0, 100)
SCRIPT_BASE_PTR = 0x174D640      # decoded script buffer; record offsets are relative to it
SCENE_STATE = 0x16500C0          # 0x1A08 bytes, per-layer script-side state
SCENE_COMMITTED = 0x1651AC8      # 0x1A08 bytes, copy taken by picture commands
SCENE_STATE_SIZE = 0x1A08
VAR_F = 0x164B1D0
SCAN_MODE = 0x174DE48
CONFIG = 0x66C578
CONFIG_SIZE = 0x140
ACTION_TABLES = 0xDB84F0         # 3 x 27 x u64
ACTION_PTR = 0xDB8778            # -> active table (VA 0xCEEE0, index = config +0x42)
CONFIG_PAGES = 0x66E890          # 2 used pages x 0x9D0 (menu item grids)
INPUT_STATE_PTR = 0xDC7BC0       # -> game input words (built by VA 0xE3A40)
READMAX = 0x17DA278              # u8[4020]
PUSH_VA = 0x7AF90                # History_Push
RESTORE_VA = 0x7C6D0             # History_Restore

# F index lists in rodata (VA): u16 values, bool bits, i8 values
HIST_LIST_U16 = 0x2160E0         # 39 x i16
HIST_LIST_BOOL = 0x216580        # 43 x i16
HIST_LIST_S8 = 0x2165F0          # <= 300 x i16, ended by a negative entry


# ---------------------------------------------------------------------------
# history record
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class HField:
    off: int
    size: int
    name: str
    ctype: str                   # u8 i8 u16 i16 i32 u64 bytes bits
    push_src: Optional[int]      # RAM VA History_Push copies (None: computed / argument / not written)
    writer: str                  # writer VA(s) and how
    restore: str                 # what History_Restore (0x7C6D0) / the loaders do with it
    conf: str = "E"              # V = checked by emulation (tests), E = read from code, H = hypothesis


HISTORY_FIELDS: List[HField] = [
    HField(0x0000, 4, "ring_index", "i32", None,
           "ring/scene clear loops write the record's own index (VA 0x7AF20, 0xA2D50, 0xA30F0, 0x7EA50); "
           "not touched by History_Push",
           "0x7E790 (slot / previous-scene load) compares it with the live ring index and the ring "
           "record's serial to decide between restoring in place and rebuilding the ring", "V"),
    HField(0x0004, 4, "serial", "i32", None,
           "History_Push 0x7B068: = (i16) counter 0x120A6EA before it is advanced "
           "(counter+1, reset to 0 when it would be <= 0); the following ring record gets -1 (0x7B49B)",
           ">= 0 = valid; rewind / backlog skip records < 0", "V"),
    HField(0x0008, 8, "cmd_offset", "u64", None,
           "History_Push 0x7B010: rdi - *0x174D640 (start of the pushing command in the decoded script)",
           "script_ptr = base + value (0x7C7DE) for every kind: the command runs again unless the kind-0 "
           "re-display (from_history = 1) moves it to +0x10", "V"),
    HField(0x0010, 8, "resume_offset", "u64", None,
           "History_Push 0x7B015: rsi - *0x174D640 (second pointer argument; text lines pass the "
           "position after the message)",
           "kind 0 with from_history = 1 (rewind) only: script_ptr = base + value after the text is "
           "re-laid out (0x7DC7A); slot loads, previous scene and backlog jumps (from_history = 0) "
           "do not read it", "V"),
    HField(0x0018, 2, "script", "u16", 0x86AA18, "History_Push 0x7B029",
           "if != current script: reload section script+3 (or 3 when >= 600) of allscr.mrg, "
           "decrypt (NOT), 0x6B460(-1) (0x7CA3B..0x7CB90)", "V"),
    HField(0x001A, 2, "text_block", "i16", 0x1654344, "History_Push 0x7B036 (low word of the i32)",
           "0x1654344 = sign-extended (0x7C7EB); mode 0 also 0x165434C", "V"),
    HField(0x001C, 2, "text_line", "i16", 0x1654348, "History_Push 0x7B043",
           "0x1654348 = sign-extended (0x7C7F7); mode 0 also 0x1654350", "V"),
    HField(0x001E, 1, "speaker", "i8", 0x164FCF6,
           "History_Push 0x7B050 (low byte of u16 0x164FCF6); voice play 0x7B520 / Z handlers "
           "0x92380, 0x93370, 0x937C0 overwrite it in the newest record",
           "0x164FCF6 = sign-extended (0x7C803); indexes the name colour table 0x86A8B0 (config +0x2A = 2)", "V"),
    HField(0x001F, 1, "var_1654208", "i8", 0x1654208, "History_Push 0x7B05C (low byte)",
           "0x1654208 = sign-extended (0x7C810)", "V"),
    HField(0x0020, 4, "unused_20", "bytes", None, "never written", "never read", "V"),
    HField(0x0024, 2, "mode", "u16", None, "History_Push 0x7B06D: dx argument (HISTORY_MODES)",
           "jump table 0x20BD9C selects the per-mode tail (HISTORY_MODES); != 0 clears 0x164FD40", "V"),
    HField(0x0026, 2, "bgm_track", "i16", 0x16545D0, "History_Push 0x7B347",
           "0x16545D0; mode 2 restarts the track with volume 0x1654238 (0x7D742)", "V"),
    HField(0x0028, 0x28, "loop_se_ids", "i16[20]", 0x1654240, "History_Push 0x7B365 (32 bytes) + 0x7B373 (8)",
           "0x1654240; modes 3..5 restart every channel with id >= 0 and 0x16542A0 > 0 (0x7D575)", "V"),
    HField(0x0050, 2, "unused_50", "bytes", None, "never written", "never read", "V"),
    HField(0x0052, 2, "scene_word", "u16", 0x16500BE, "History_Push 0x7B2BB", "0x16500BE (0x7C9A4)", "V"),
    HField(0x0054, 4, "unused_54", "bytes", None, "never written", "never read", "V"),
    HField(0x0058, 0x1A08, "scene_state", "bytes", SCENE_STATE, "History_Push memcpy 0x7B2C1",
           "memcpy to 0x16500C0 for every kind (0x7C9AB), then pushed to the renderer "
           "(0xDDB5E8 layer table, stride 0x2C8; 0x7CD6D..0x7D43B); kind 0 then rewrites +0x1982 / "
           "+0x1984 (0x7D736, 0x7D860); the push itself first writes +0x19A3 = F995 & 0xFF", "V"),
    HField(0x1A60, 0x1A08, "scene_committed", "bytes", SCENE_COMMITTED, "History_Push memcpy 0x7B2DA",
           "modes 1 and 9 only: memcpy to 0x1651AC8 and memmove to 0x16500C0, then per-layer 0xE98D0 "
           "and 0x7DD10(0,-1) (0x7D629..0x7D6B4)", "V"),
    HField(0x3468, 0x40, "state_174dab0", "bytes", 0x174DAB0, "History_Push 0x7B314 / 0x7B326",
           "0x174DAB0 (0x7C83B)", "V"),
    HField(0x34A8, 2, "var_174d940", "u16", 0x174D940, "History_Push 0x7B337", "0x174D940 (0x7C854)", "V"),
    HField(0x34AA, 0x4E, "f_u16", "u16[39]", None,
           "History_Push 0x7B1B9: F[list 0x2160E0[k]] for k < 39 (exact)",
           "F[list[k]] = value (0x7CBD2); slot load 0xA32B6 after zeroing F and Z", "V"),
    HField(0x34F8, 2, "unused_34f8", "bytes", None, "never written", "never read", "V"),
    HField(0x34FA, 6, "f_bool_bits", "bits[43]", None,
           "History_Push 0x7B272: bit k = (F[list 0x216580[k]] != 0) for list entries <= 1200; "
           "Slot_Store 0x737D0 sets the bit of F1118 in the slot copy (0x76500); the quick save "
           "0x73A70 does not",
           "F[list[k]] = bit (0x7CC78); restored last, so it wins over the other lists", "V"),
    HField(0x3500, 1, "unused_3500", "bytes", None, "never written", "never read", "V"),
    HField(0x3501, 0x12C, "f_s8", "i8[300]", None,
           "History_Push 0x7B1FD: low byte of F[list 0x2165F0[k]] (list ends at the first negative "
           "entry: 276 used, +0x3501..+0x3614)",
           "F[list[k]] = sign-extended byte (0x7CC22); Adv_Rewind reads slot 32 (+0x3521 = F823, "
           "message index of the page) to land on page starts", "V"),
    HField(0x362D, 3, "unused_362d", "bytes", None, "never written", "never read", "V"),
    HField(0x3630, 4, "voice_id", "i32", None,
           "History_Push 0x7B07E: -1; voice play (0x7B520 pattern inline in 0x92380, 0x93370, 0x937C0) "
           "stores the voice of the line",
           "0x1654200 / 0x16541FC / 0x1654204 (0x7C9BB); 0x7E790 replays it after a load when the voice "
           "is enabled (0x41AB0)", "V"),
    HField(0x3634, 2, "voice_character", "i16", None,
           "History_Push 0x7B073: -1; voice play stores the character (0x7B53F)",
           "0x7E790: volume = config[+0x76 + 2*0x3D2D0(char)] * config +0x16 / 100", "E"),
    HField(0x3636, 2, "effect_active_bits", "bits[14]", None,
           "History_Push 0x7B11B: bit i = effect slot i active (0x5C660: dword 0xDF1830 + 0x134*i)",
           "slots active now but clear in the record are stopped (0x5C080) (0x7C9D9)", "V"),
    HField(0x3638, 2, "effect_blocking_bits", "bits[14]", None,
           "History_Push 0x7B16B: bit i = 0x5C680(i) (slot active and motion record flag 8 clear)",
           "!= 0: record is not a rewind target (0xA2260) and no slot snapshot is taken (0x7DC89)", "V"),
    HField(0x363A, 2, "flags_363a", "bits", None,
           "History_Push 0x7B186: bit 0 = word 0xDF08DC (WNTY: last message of the page), other bits 0",
           "not read by the restore", "V"),
    HField(0x363C, 2, "bgm_volume", "i16", 0x1654238, "History_Push 0x7B354",
           "0x1654238; float argument of the BGM start 0x98C0 in mode 2", "V"),
    HField(0x363E, 2, "config_04", "u16", CONFIG + 0x04, "History_Push 0x7B137 (config +0x04)",
           "when config +0x4C != 0: 0x423D0() and config +0x04 = value if <= 0x100 (0x7CA00)", "V"),
    HField(0x3640, 1, "var_3640", "u8", None,
           "voice play: 0 (0x7B553); 0x7B590 pattern: byte 0x165420A; not touched by History_Push",
           "never read", "E"),
    HField(0x3641, 1, "var_165420a", "i8", None,
           "0x7B560 pattern (Script_Step 0x9...): byte 0x165420A into the record about to be pushed "
           "(index 0x120A6EE); not touched by History_Push",
           "0x165420A = sign-extended (0x7C817)", "V"),
    HField(0x3642, 0x14, "se_vol_a", "i8[20]", None,
           "History_Push 0x7B385: low byte of u16 0x1654270[i]", "0x1654270[i] = sign-extended (0x7C903)", "V"),
    HField(0x3656, 1, "unused_3656", "bytes", None, "never written", "never read", "V"),
    HField(0x3657, 0x14, "se_vol_b", "i8[20]", None,
           "History_Push 0x7B392: low byte of u16 0x16542A0[i]", "0x16542A0[i] = sign-extended (0x7C90F)", "V"),
    HField(0x366B, 1, "unused_366b", "bytes", None, "never written", "never read", "V"),
    HField(0x366C, 0x28, "se_arr_c", "i16[20]", 0x16542D0, "History_Push 0x7B39F", "0x16542D0 (0x7C8AC)", "V"),
    HField(0x3694, 0x28, "se_arr_d", "i16[20]", 0x1654300, "History_Push 0x7B3AD", "0x1654300 (0x7C8BE)", "V"),
    HField(0x36BC, 0x14, "se_arr_e_stale", "u8[20]", None,
           "NOT written: History_Push 0x7B3BA copies record -> RAM 0x1654330[i] (engine bug: the live "
           "array is overwritten with stale ring bytes on every push)",
           "0x1654330..0x1654343 = these bytes (0x7C8DF, 0x7C8EF)", "V"),
    HField(0x36D0, 0x1E0, "channels", "rec18[20]", None,
           "History_Push 0x7B3D3..0x7B427: per 0x18-byte entry bytes +0..+2, +4..+5, +8..+0x15 "
           "(+3, +6..+7, +0x16..+0x17 not copied)",
           "same bytes back to 0x16543F0 (0x7C918..0x7C96B)", "V"),
    HField(0x38B0, 0x32, "window_164fd00", "bytes", 0x164FD00,
           "History_Push 0x7B09B / 0x7B0AD (+0x38B0 byte also zeroed by the ring clear loops)",
           "0x164FD00 (0x7CC93)", "V"),
    HField(0x38E2, 0x1C, "window_164fd40", "bytes", 0x164FD40,
           "History_Push 0x7B0B7 (byte 0) then 0x7B0C8 / 0x7B0DA (copy)",
           "mode 0: 0x164FD40 (0x7CCCB); other modes: byte 0x164FD40 = 0; then 0x16541EC/F0 = (byte != 0)", "V"),
    HField(0x38FE, 0xFA, "message_text", "bytes",
           None, "History_Push 0x7B08A: byte 0; after the push the text commands copy 0x174D9B0 "
           "(0xF8 bytes) here and clear +0x39F6 (_ZM 0x92380, _MSAD 0x92B60, 0x7B5C0); _ATSV(n) "
           "copies the current message (0x98907), _ATSV() its argument 1 (0x98550, via 0x16543C0)",
           "0x174D9B0..0x174DAA9 (0x7C75D..0x7C7D1); backlog and page history re-lay it out", "V"),
    HField(0x39F8, 0x18, "unused_39f8", "bytes", None, "never written", "never read", "V"),
]

# record kinds (record +0x24 = dx of History_Push): (live pushers, restore tail).
# Every direct call site is in PUSH_SITES; "dead" = the containing function has no call, jmp,
# rip reference or relocated data pointer in v01.01 (eboot.disasm index + E8/E9 byte scan).
HISTORY_MODES: Dict[int, Tuple[str, str]] = {
    0: ("message: the _ZM marker handler 0x92380 (site 0x9291F, before the text is shown); "
        "the second site 0x935CA (0x93370) is dead code",
        "common part, then 0x7D6F2: 0x86AA24 -> 0x75CD0(1); 0x1653AE0 = 0; 0x174DE00 = 0; scene +0x1984 = "
        "F931 if F931 >= 0; scene +0x1982 = +0x1984; with from_history = 1 only: re-lay out the record's "
        "statement text, colour by config +0x2A, resume at +0x10 (0x7D86F..0x7DC7A)"),
    1: ("first layer change since the last commit (F991 == 0 -> push, F991 = 1, committed copy "
        "0x1651AC8 = scene_state): STCH/SCH2 0x81760, STCC 0x82D30, STBR 0x830D0, STCL 0x83350, "
        "STCP 0x83850, STRT 0x83940, STZ4 0x83B70, STRC 0x84190, STRR 0x84520, STGS 0x84960, "
        "STRA 0x86EE0, STNS 0x87100 and the shared helper 0x7F410 (STEC, SNX2, SNX3, SQK2, SQS2, "
        "SHK2, SHS2, SSW2, SSS2, FLNT.., STMA, SPMA ...); FADS 0x81520 pushes kind 1 unconditionally "
        "and commits",
        "restore +0x1A60 as both scene copies, refresh layers (0x7D629)"),
    2: ("music: MPLY 0x87250, MFAD 0x87660, MVOL 0x87940, MSTP (Script_Step 0x978C8)",
        "restart BGM 0x16545D0 with volume 0x1654238 or stop it (0x7D742)"),
    3: ("sound effect: SEPL/SEFI 0x893F0, SEVL 0x89B50, SEFD 0x89E40, SEPN 0x8A340, SEP3 0x8A5E0, "
        "SEAC 0x8A880, helper 0x88160, SESP (Script_Step 0x99723)",
        "restart the 20 loop-SE channels (0x7D575)"),
    4: ("(no push site)", "as 3"),
    5: ("(no push site)", "as 3"),
    6: ("scene start: _ATSV() (0x9853E), _ATSV(8) (0x9788F), _ATSV(n) with n & 3 in {0, 3} (0x97EA4); "
        "each is followed by SceneStack_Push 0x7E2E0 (ATSV_PATHS)",
        "if the scene-stack top holds this ring index: pop it, 0x174DE04 = 0x174DE08 = 0 (0x7D78C)"),
    7: ("choice save point: _ATSV(n) with n & 3 == 1 (0x97EA4; the 83 _ATSV(1) of the scripts, "
        "right before the choices); the record keeps the current message text",
        "as 6, then 0x7B790(record text) (0x7D7DB)"),
    8: ("only the dead site 0x93983 (0x937C0): never pushed in v01.01", "nothing extra (0x7DC81)"),
    9: ("commit point while a layer change is pending (F991 != 0 -> committed copy = scene_state, "
        "push, F991 = 0): VPLY/VPL2 (0x968FD), Z markers (0x9846B), WT__/WTTM (0x9AC57), "
        "MSAD (0x9AF0B)", "as 1"),
    12: ("choice start SELD (Script_Step 0x9B288)",
         "out of table range: common part only; never a rewind target or slot snapshot (0x7DC9B)"),
    13: ("choice line SELR (Script_Step 0x993A0)", "out of table range: common part only"),
}
# Mask 0x3001 (0xA21E6, kinds 0, 12, 13) only decides which record AT the rewind cursor is not
# undone before the walk; the rewind STOP is kind 0 with effect_blocking_bits == 0 and the
# record's F823 <= 1 (0xA2259..0xA229B).  The backlog lists kinds 0, 8, 11, 12 (mask 0x1901 at
# 0x3C032); kinds 8 and 11 are never pushed in v01.01.


@dataclass(frozen=True)
class PushSite:
    site: int                    # VA of the E8 call to History_Push 0x7AF90
    func: int                    # containing function (eh_frame start)
    kinds: Tuple[int, ...]       # value(s) of edx at the call
    command: str                 # script command(s) whose handler reaches the site
    live: bool                   # False: the containing function is never referenced (dead code)


_L, _D = True, False
PUSH_SITES: List[PushSite] = [
    PushSite(0x7F42F, 0x7F410, (1,), "layer helper (STEC SNX2 SNX3 SQK2 SQS2 SHK2 SHS2 SSW2 SSS2 FLNT.. STMA SPMA)", _L),
    PushSite(0x7F49E, 0x7F460, (9,), "-", _D), PushSite(0x7F4F7, 0x7F4B0, (9,), "-", _D),
    PushSite(0x7F63A, 0x7F510, (1,), "-", _D), PushSite(0x7F790, 0x7F680, (1,), "-", _D),
    PushSite(0x816D7, 0x81520, (1,), "FADS", _L), PushSite(0x817FC, 0x81760, (1,), "STCH SCH2", _L),
    PushSite(0x82D7A, 0x82D30, (1,), "STCC", _L), PushSite(0x83116, 0x830D0, (1,), "STBR", _L),
    PushSite(0x8339A, 0x83350, (1,), "STCL", _L), PushSite(0x83894, 0x83850, (1,), "STCP", _L),
    PushSite(0x8398A, 0x83940, (1,), "STRT", _L), PushSite(0x83BEF, 0x83B70, (1,), "STZ4", _L),
    PushSite(0x841DA, 0x84190, (1,), "STRC", _L), PushSite(0x84566, 0x84520, (1,), "STRR", _L),
    PushSite(0x849AA, 0x84960, (1,), "STGS", _L),
    PushSite(0x84BBA, 0x84B70, (1,), "-", _D), PushSite(0x8502E, 0x84F60, (1,), "-", _D),
    PushSite(0x851DE, 0x85110, (1,), "-", _D), PushSite(0x853BE, 0x852F0, (1,), "-", _D),
    PushSite(0x85547, 0x85470, (1,), "-", _D), PushSite(0x85705, 0x85580, (1,), "-", _D),
    PushSite(0x85762, 0x85580, (9,), "-", _D), PushSite(0x86A52, 0x86A30, (1,), "-", _D),
    PushSite(0x86B42, 0x86B20, (1,), "-", _D), PushSite(0x86EBC, 0x86B20, (9,), "-", _D),
    PushSite(0x86F26, 0x86EE0, (1,), "STRA", _L), PushSite(0x87144, 0x87100, (1,), "STNS", _L),
    PushSite(0x87271, 0x87250, (2,), "MPLY", _L), PushSite(0x8767A, 0x87660, (2,), "MFAD", _L),
    PushSite(0x878D6, 0x87820, (2,), "-", _D), PushSite(0x87901, 0x878F0, (2,), "-", _D),
    PushSite(0x87A7A, 0x87940, (2,), "MVOL", _L),
    PushSite(0x882A6, 0x88160, (3,), "SE helper (SEPL SEFI SEVL SEFD SEAC)", _L),
    PushSite(0x89851, 0x893F0, (3,), "SEPL SEFI", _L), PushSite(0x8986D, 0x893F0, (3,), "SEPL SEFI", _L),
    PushSite(0x89D6A, 0x89B50, (3,), "SEVL", _L), PushSite(0x89FED, 0x89E40, (3,), "SEFD", _L),
    PushSite(0x8A2AA, 0x8A100, (3,), "-", _D), PushSite(0x8A52C, 0x8A340, (3,), "SEPN", _L),
    PushSite(0x8A7CC, 0x8A5E0, (3,), "SEP3", _L), PushSite(0x8B246, 0x8A880, (3,), "SEAC", _L),
    PushSite(0x9291F, 0x92380, (0,), "_ZM (Z marker handler)", _L),
    PushSite(0x935CA, 0x93370, (0,), "-", _D), PushSite(0x93983, 0x937C0, (8,), "-", _D),
    PushSite(0x94DE1, 0x94D40, (1,), "-", _D), PushSite(0x9507A, 0x94F50, (1,), "-", _D),
    PushSite(0x968FD, 0x95E60, (9,), "VPLY VPL2", _L), PushSite(0x9788F, 0x95E60, (6,), "ATSV(8)", _L),
    PushSite(0x978C8, 0x95E60, (2,), "MSTP", _L),
    PushSite(0x97EA4, 0x95E60, (6, 7), "ATSV(n), n != 8 and n & 3 in {0, 1, 3}", _L),
    PushSite(0x9846B, 0x95E60, (9,), "Z markers", _L),
    PushSite(0x9853E, 0x95E60, (6,), "ATSV(), ATSV(n) with n & 3 == 2", _L),
    PushSite(0x98A4B, 0x95E60, (6,), "ATSV branch argc == 0 with n & 3 != 2: unreachable", _L),
    PushSite(0x993A0, 0x95E60, (13,), "SELR", _L), PushSite(0x99723, 0x95E60, (3,), "SESP", _L),
    PushSite(0x9AC57, 0x95E60, (9,), "WT__ WTTM", _L), PushSite(0x9AF0B, 0x95E60, (9,), "MSAD", _L),
    PushSite(0x9B288, 0x95E60, (12,), "SELD", _L),
]

# _ATSV (Script_Step 0x95F5D): argc = number of arguments (0 for "()"), n = inline int of
# argument 0 (2 when argc == 0), low = n & 3 (2 when argc == 0).  ld = word 0x174DE08 (1 right
# after a slot load) is read at entry and cleared at the end (0x98BD1).  Scan mode: 0x9B516.
ATSV_PATHS: List[Tuple[str, str, int, str]] = [
    # (condition, push site, kind, what follows)
    ("low == 2: _ATSV() (313 in the scripts) / _ATSV(2|6|..)", "0x9853E", 6,
     "argument 1 (up to ')' ';' ',', empty when absent) -> 0x16543C0 -> text buffer 0x174D9B0 and "
     "record +0x38FE; Staging_SetSnapshot if allowed; dword 0xDF29D4 = 0; SceneStack_Push"),
    ("n == 8: _ATSV(8) (2 in the scripts)", "0x9788F", 6,
     "skipped when 0xD1CD0(1) != 0; 0x7B5C0(\"\") (empty text); SceneStack_Push; AutoSave 0x73B70 "
     "unless ld != 0 or 0x67460() != 0"),
    ("other n, low == 1: _ATSV(1) (83, before the choices)", "0x97EA4", 7,
     "unless re-displayed (0x174D9A8): record +0x38FE = current text 0x174D9B0 (0xF8 bytes), "
     "Staging_SetSnapshot if allowed, 0xDF29D4 = 0; SceneStack_Push when n & 4 == 0"),
    ("other n, low in {0, 3}", "0x97EA4", 6, "as the previous row"),
]
# While a next-scene skip runs (0x120A6D2 != 0) _ATSV first calls 0x7E620 (restart BGM and loop
# SEs) and clears 0x120A6D2 (the skip ends here); _ATSV(n) with low == 1 also calls
# 0x7B790(0x174D9B0).  The AutoSave branch tests config +0x40 only on a path that cannot be
# reached (0x98BB1: r13w must be 8 there and is 2), so +0x40 has no effect.


def atsv_path(argc: int, n: int) -> Tuple[int, int]:
    """(History_Push site, kind) that `_ATSV` takes for ``argc`` arguments and argument 0 = ``n``
    (Script_Step 0x96C68..0x97EA4; ATSV_PATHS)."""
    if argc == 0:
        n = 2
    low = n & 3 if argc else 2
    if low == 2:
        return 0x9853E, 6
    if n == 8:
        return 0x9788F, 6
    return 0x97EA4, 6 | (1 if low == 1 else 0)


def scan_push_sites(eboot: Union[str, Path, None] = None) -> List[Tuple[int, Tuple[int, ...]]]:
    """(site, kinds) for every E8 call of History_Push in the text segment: the kind is read from
    the instructions that set edx before the call (mov imm, xor, or the sete/or pair at 0x97EA4).
    Needs capstone."""
    import capstone
    path = _eboot(eboot)
    with open(path, "rb") as fh:
        fh.seek(TEXT_OFF)
        t = fh.read(0x329DC8)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    out = []
    i = t.find(b"\xe8")
    while i != -1 and i + 5 <= len(t):
        if i + 5 + struct.unpack_from("<i", t, i + 1)[0] == PUSH_VA:
            kinds: Tuple[int, ...] = ()
            for back in (0x20, 0x40, 0x60):          # a window that decodes on instruction bounds
                ins = list(md.disasm_lite(t[i - back:i], i - back))
                if not ins or ins[-1][0] + ins[-1][1] != i:
                    continue
                sete = False
                for _a, _s, mn, ops in reversed(ins):
                    if mn == "sete" and ops == "dl":
                        sete = True
                    elif mn == "or" and ops == "edx, 6":
                        kinds = (6, 7)
                    elif mn == "mov" and ops.startswith("edx, "):
                        kinds = kinds or (int(ops[5:], 0),)
                        break
                    elif mn == "xor" and ops == "edx, edx":
                        kinds = kinds or (0,)
                        break
                if kinds == (6, 7) and not sete:
                    kinds = ()
                if kinds:
                    break
            out.append((i, kinds))
        i = t.find(b"\xe8", i + 1)
    return out


def check_history_tiling() -> List[str]:
    """Problems with HISTORY_FIELDS (gaps / overlaps / end), empty when it tiles 0..0x3A10."""
    out = []
    pos = 0
    for f in HISTORY_FIELDS:
        if f.off != pos:
            out.append(f"gap or overlap at 0x{pos:x} (next field 0x{f.off:x})")
        pos = f.off + f.size
    if pos != RECORD_SIZE:
        out.append(f"ends at 0x{pos:x}")
    return out


# ---------------------------------------------------------------------------
# scene_state (VA 0x16500C0, 0x1A08 bytes; record +0x58 and, as the committed copy 0x1651AC8,
# record +0x1A60).  Every byte is in exactly one field (test).  Per-layer arrays: offsets and
# element formats agree with the layer work (tsuki.misc_layer_state_and_motions.SCENE_ARRAYS,
# reconciled by a test); 40 entries each.  Writers: the instructions (globals) or functions
# (arrays) that store into the field, from the static memory-operand scan
# (scratch/misc_adv_system_state/memscan.py + scene_state_fields.py; lea-base registers
# tracked); writer VAs of globals are re-checked by a test that decodes each instruction.
# Block writers of the whole state (not listed per field): History_Restore memcpy 0x7C9AB
# (every kind) and memmove 0x7D65E (kinds 1 / 9), 0x7D840, 0x7E4E0 (0x7E52A); the scene
# reset 0x789F0 and the table-driven clear 0x847F0 (SQS2 / SHS2 / SSS2) write field by field.
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class SField:
    off: int
    size: int
    kind: str                    # 'array' (40 per-layer entries), 'global', 'gap'
    name: str
    writers: str                 # writer instruction VAs (globals) / functions (arrays)
    note: str


SCENE_STATE_FIELDS: List[SField] = [
    SField(0x0000, 0x2, 'global', 'scene_pic', '0x78AD2, 0x7E566, 0x7E5E0, 0x848A4, 0x848C2, 0x84912, 0xA3816',
           'i16 scene-level picture id (layer work: pic-seen bit + 0x49C20 at the commit); reset 0x789F0, 0x7E4E0 (restore re-sync), 0xA3810 (from 0x5B400)'),
    SField(0x0002, 0x2, 'global', 'colour_mode', '0x78AD2, 0x7E566, 0x7E5E0, 0x84912, 0xA381F',
           'i16 colour-mode word -> render 0xDE2658 (0x49C30 at the commit; 2 = grey, 3 = sepia, compositing.md 9); 0xA3810'),
    SField(0x0004, 0x50, 'array', 'pic', '0x789F0, 0x7DD10, 0x81760, 0x82D30, 0x847F0, 0x84F60, 0x85110, 0x852F0, 0x85EB0, 0x85F90, 0xA3870, 0xBE4C0',
           'h x 40 (stride 2); commands STCH SCH2 STCC'),
    SField(0x0054, 0x50, 'array', 'blend', '0x789F0, 0x81760, 0x82D30, 0x830D0, 0xBE4C0',
           'h x 40 (stride 2); commands STBR'),
    SField(0x00A4, 0x28, 'array', 'flags', '0x789F0, 0x81760, 0x82D30, 0x830D0, 0x95E60',
           'B x 40 (stride 1); commands STBR a3=1 -> |0x80, SACL -> 0x40'),
    SField(0x00CC, 0x28, 'array', 'fx_mode', '0x10390, 0x789F0, 0x81760, 0x82D30, 0xA3870',
           'b x 40 (stride 1); commands STEC (and STCH from the global word 0x164B97C)'),
    SField(0x00F4, 0x50, 'array', 'fx_a', '0x10610, 0x789F0, 0x81760, 0x82D30, 0xBE4C0',
           'h x 40 (stride 2); commands SNX2'),
    SField(0x0144, 0x28, 'array', 'fx_b', '0x106E0, 0x789F0, 0x81760, 0x82D30, 0xBE4C0',
           'b x 40 (stride 1); commands SNX3'),
    SField(0x016C, 0x50, 'array', 'x', '0x789F0, 0x81760, 0x82D30, 0x83350, 0x85F90, 0xA3870, 0xBE4C0',
           'h x 40 (stride 2); commands STCH SCH2 STCL'),
    SField(0x01BC, 0x50, 'array', 'y', '0x789F0, 0x81760, 0x82D30, 0x83350, 0x85F90, 0xA3870, 0xBE4C0',
           'h x 40 (stride 2); commands STCH SCH2 STCL'),
    SField(0x020C, 0x50, 'array', 'alpha', '0x789F0, 0x81760, 0x82D30, 0x84960, 0x84F60, 0x85110, 0x852F0, 0x85470, 0x85580, 0x85EB0, 0xA3870, 0xBE4C0',
           'h x 40 (stride 2); commands STCH SCH2 STGS'),
    SField(0x025C, 0x28, 'array', 'state', '0x789F0, 0x7DD10, 0x81760, 0x85470, 0x85580, 0x85F90, 0xA3870',
           'b x 40 (stride 1); commands STCH SCH2 (0), engine'),
    SField(0x0284, 0x28, 'array', 'u284', '0x789F0, 0x81760',
           'B x 40 (stride 1); commands copied by STCH src'),
    SField(0x02AC, 0x28, 'array', 'u2ac', '0x81760',
           'B x 40 (stride 1); commands copied by STCH src'),
    SField(0x02D4, 0x28, 'array', 'u2d4', '0x81760',
           'B x 40 (stride 1); commands copied by STCH src'),
    SField(0x02FC, 0x50, 'array', 'z', '0x789F0, 0x81760, 0x82D30, 0x83850, 0xBE4C0',
           'h x 40 (stride 2); commands STCH SCH2 STCP'),
    SField(0x034C, 0x4, 'gap', 'gap_034c', 'none (block copies only)',
           'no instruction addresses these bytes: moved only by the block copies'),
    SField(0x0350, 0x140, 'array', 'rot', '0x789F0, 0x81760, 0x82D30, 0x83940, 0x86440, 0xBE4C0',
           'q x 40 (stride 8); commands STRT STPV(a3) STCH(reset 0)'),
    SField(0x0490, 0x50, 'array', 'scale', '0x789F0, 0x81760, 0x82D30, 0x84B70, 0x85F90',
           'h x 40 (stride 2); commands STCH SCH2'),
    SField(0x04E0, 0x50, 'array', 'anchor_x', '0x789F0, 0x81760, 0x82D30, 0x84B70, 0x86440, 0xBE4C0',
           'h x 40 (stride 2); commands STCH SCH2 STPV'),
    SField(0x0530, 0x50, 'array', 'anchor_y', '0x789F0, 0x81760, 0x82D30, 0x84B70, 0x86440, 0xBE4C0',
           'h x 40 (stride 2); commands STCH SCH2 STPV'),
    SField(0x0580, 0x50, 'array', 'u580', '0x789F0, 0x81760',
           'h x 40 (stride 2); commands copied by STCH src'),
    SField(0x05D0, 0x50, 'array', 'u5d0', '0x789F0, 0x81760',
           'h x 40 (stride 2); commands copied by STCH src'),
    SField(0x0620, 0x50, 'array', 'u620', '0x789F0, 0x81760, 0x85F90',
           'h x 40 (stride 2); commands copied by STCH src'),
    SField(0x0670, 0x50, 'array', 'u670', '0x789F0, 0x81760, 0x85F90',
           'h x 40 (stride 2); commands copied by STCH src'),
    SField(0x06C0, 0x50, 'array', 'u6c0', '0x789F0, 0x81760, 0x85F90',
           'h x 40 (stride 2); commands copied by STCH src'),
    SField(0x0710, 0x50, 'array', 'u710', '0x789F0, 0x81760, 0x85F90',
           'h x 40 (stride 2); commands copied by STCH src'),
    SField(0x0760, 0x50, 'array', 'u760', '0x789F0, 0x81760',
           'h x 40 (stride 2); commands copied by STCH src'),
    SField(0x07B0, 0x50, 'array', 'u7b0', '0x789F0, 0x81760, 0x84F60, 0x85F90',
           'h x 40 (stride 2); commands copied by STCH src'),
    SField(0x0800, 0x50, 'array', 'u800', '0x789F0, 0x7E620, 0x81760, 0x84F60, 0x85110, 0x852F0, 0x85470, 0x85580, 0x85EB0, 0x85F90',
           'h x 40 (stride 2); commands copied by STCH src'),
    SField(0x0850, 0x50, 'array', 'u850', '0x789F0',
           'h x 40 (stride 2); commands copied by STCH src'),
    SField(0x08A0, 0x50, 'array', 'zoom_x', '0x789F0, 0x81760, 0x82D30, 0x83B70, 0xBE4C0',
           'h x 40 (stride 2); commands STZ4 a3; STCH SCH2 reset to 1000'),
    SField(0x08F0, 0x50, 'array', 'zoom_y', '0x789F0, 0x81760, 0x82D30, 0x83B70, 0xBE4C0',
           'h x 40 (stride 2); commands STZ4 a4; STCH SCH2 reset to 1000'),
    SField(0x0940, 0x28, 'array', 'zoom_pivot', '0x789F0, 0x81760, 0x82D30',
           'b x 40 (stride 1); commands STZ4 a7; STCH SCH2 reset to 0'),
    SField(0x0968, 0x50, 'array', 'mask', '0x789F0, 0x81760, 0x82D30',
           'h x 40 (stride 2); commands STCH a11'),
    SField(0x09B8, 0x50, 'array', 'sttr', '0x789F0, 0x81760, 0x82D30, 0x95E60',
           'h x 40 (stride 2); commands STTR'),
    SField(0x0A08, 0xA0, 'array', 'parent', '0x789F0, 0x81760, 0x82D30, 0x95E60',
           'i x 40 (stride 4); commands SBAS'),
    SField(0x0AA8, 0x28, 'array', 'soxy_x', '0x789F0, 0x81760, 0x82D30, 0x95E60',
           'b x 40 (stride 1); commands SOXY a1'),
    SField(0x0AD0, 0x28, 'array', 'soxy_y', '0x789F0, 0x81760, 0x82D30, 0x95E60',
           'b x 40 (stride 1); commands SOXY a2'),
    SField(0x0AF8, 0x140, 'array', 'crop', '0x789F0, 0x81760, 0x82D30, 0x84190, 0xBE4C0',
           '4h x 40 (stride 8); commands STCH a12..a15'),
    SField(0x0C38, 0x50, 'array', 'a16', '0x789F0, 0x81760, 0x82D30, 0xBE4C0',
           'H x 40 (stride 2); commands STCH a16'),
    SField(0x0C88, 0x28, 'array', 'a17', '0x789F0, 0x81760, 0x82D30, 0xBE4C0',
           'B x 40 (stride 1); commands STCH a17'),
    SField(0x0CB0, 0x50, 'array', 'angle', '0x789F0, 0x81760, 0x82D30, 0x84520, 0xBE4C0',
           'h x 40 (stride 2); commands STCH a19, STRR'),
    SField(0x0D00, 0x3C0, 'array', 'motion', 'pointer stores: STMA 0xC0330 (lea 0xC048F / 0xC0511), STCH 0x81760 (0x826BA, 0x827C3), STCC 0x82D30 (0x82F62), 0xBE4C0',
           '24s x 40 (stride 24); commands STMA SPMA'),
    SField(0x10C0, 0x50, 'array', 'shake_kind', '0x789F0, 0x81760, 0x82D30',
           '2B x 40 (stride 2); commands SQK2 (slot 0) SHK2/SSW2 (slot 1)'),
    SField(0x1110, 0xA0, 'array', 'shake_fade', '0x789F0, 0x81760, 0x82D30',
           '2h x 40 (stride 4); commands SQK2 SHK2 SSW2 a5'),
    SField(0x11B0, 0xA0, 'array', 'shake_time', '0x789F0, 0x81760, 0x82D30',
           '2h x 40 (stride 4); commands SQK2 SHK2 SSW2 a4'),
    SField(0x1250, 0x3C0, 'array', 'shake_par', '0x81760, 0x82D30',
           '12h x 40 (stride 24); commands SQK2 SHK2 SSW2'),
    SField(0x1610, 0x50, 'array', 'stra', '0x789F0, 0x81760, 0x82D30, 0x86EE0',
           'h x 40 (stride 2); commands STRA'),
    SField(0x1660, 0x28, 'array', 'stns', '0x789F0, 0x82D30, 0x87100, 0xBE4C0',
           'B x 40 (stride 1); commands STNS'),
    SField(0x1688, 0x28, 'array', 'stns_b', '0x789F0, 0x82D30, 0x87100, 0xBE4C0',
           'B x 40 (stride 1); commands STNS'),
    SField(0x16B0, 0x2D0, 'array', 'ext', '0x81760, 0x84B70, 0x86440, 0xBE4C0',
           '18s x 40 (stride 18); commands STCH STPV STEC'),
    SField(0x1980, 0x2, 'global', 'pic_table_index', '0x68AF4, 0x78AFD',
           'i16 index into the 12-byte table 0x873050 -> 0x493E0 at the commit (layer work); 0x68A70'),
    SField(0x1982, 0x2, 'global', 'text_window_a', '0x78AFD, 0x7D860, 0x7DBBE, 0x7E55A, 0x856EA, 0x92840, 0x92DE0',
           'i16 copy of +0x1984 (kind-0 restore 0x7D860 / 0x7DBBE, 0x7E55A); _ZM 0x92840, _MSAD 0x92DE0; read by the HUD 0x53CF0'),
    SField(0x1984, 0x2, 'global', 'text_window_b', '0x78AFD, 0x7D736, 0x7E54A, 0x856EA',
           'i16 = F931 when F931 >= 0 (kind-0 restore 0x7D736), 0x7E54A; read by _ZM / _MSAD / prefetch 0xA0740 [H: message-window style]'),
    SField(0x1986, 0x2, 'global', 'commit_49620_a', '0x78AFD, 0x7E584, 0x7E5FE',
           'i16 -> 0x49620 at the commit (layer work)'),
    SField(0x1988, 0x2, 'global', 'commit_49620_b', '0x78AFD, 0x7E584, 0x7E5FE',
           'i16 -> 0x49620 at the commit (layer work)'),
    SField(0x198A, 0x2, 'global', 'commit_495f0_a', '0x78AFD, 0x7E584, 0x7E5FE',
           'i16 -> 0x495F0 at the commit (layer work)'),
    SField(0x198C, 0x2, 'global', 'commit_495f0_b', '0x78AFD, 0x7E584, 0x7E5FE',
           'i16 -> 0x495F0 at the commit'),
    SField(0x198E, 0x2, 'global', 'commit_495f0_c', '0x78AFD, 0x7E575, 0x7E5EF',
           'i16 -> 0x495F0 at the commit'),
    SField(0x1990, 0x8, 'global', 'reset_1990', '0x78AED',
           'u64, written only by the scene reset 0x789F0, never read'),
    SField(0x1998, 0x2, 'global', 'reset_1998', '0x78AF4',
           'u16, written only by the scene reset 0x789F0, never read'),
    SField(0x199A, 0x2, 'global', 'word_199a', '0x79119, 0xA2EC3',
           'i16 (dword store with +0x199C); scene reset, Slot_Load 0xA2EC3 = 0; read by the restore'),
    SField(0x199C, 0x2, 'global', 'word_199c', '0x79119, 0xA2EC3',
           'i16 (see +0x199A)'),
    SField(0x199E, 0x2, 'global', 'word_199e', '0x7914A, 0xA2ECD',
           'i16; scene reset, Slot_Load 0xA2ECD = 0; read by the restore'),
    SField(0x19A0, 0x1, 'global', 'byte_19a0', 'none found (block copies only)',
           'u8; no static writer (moved only by the block copies); read by the restore, 0x7E4E0'),
    SField(0x19A1, 0x1, 'global', 'byte_19a1', '0x78B05, 0x7F5C0, 0x7F68A',
           'u8 -> 0x426A0(byte, 0, 0) at the restore; reset; dead writers 0x7F510 / 0x7F680'),
    SField(0x19A2, 0x1, 'global', 'effect_19a2', '0x78E2A, 0x81187, 0xB776C, 0xB782E, 0xB78BC, 0xB7973, 0xB7A4B',
           'u8 screen effect (SNOW 0x81060, FLNT/FLOP/FLSP/FCLN/FCSP 0xB7720..0xB79C0) -> 0xA88B0(byte, 0) at the restore'),
    SField(0x19A3, 0x1, 'global', 'f995_low', '0x78B0C, 0x7B2A2',
           'u8 = low byte of F995, written by every History_Push (0x7B2A2) before the copy'),
    SField(0x19A4, 0x1, 'global', 'byte_19a4', '0x78E31',
           'u8 -> 0x16541E4 at the restore; reset'),
    SField(0x19A5, 0x1, 'gap', 'gap_19a5', 'none (block copies only)',
           'no instruction addresses these bytes: moved only by the block copies'),
    SField(0x19A6, 0x2, 'global', 'f967_base', '0x78E3B',
           'i16; F967 = this + 1 after a load (0x7E790) and the restore rule 0x7D513; reset'),
    SField(0x19A8, 0x1, 'global', 'byte_19a8', 'none found (block copies only)',
           'u8; no static writer; read by 0x7E790 / 0x7EC30'),
    SField(0x19A9, 0x1, 'global', 'byte_19a9', 'none found (block copies only)',
           'u8; no static writer; read by 0x7E790 / 0x7EC30'),
    SField(0x19AA, 0x1, 'global', 'byte_19aa', '0x78B42',
           'i8 -> 0x56CE0 at the commit (layer work); reset'),
    SField(0x19AB, 0x28, 'array', 'u19ab', '0x789F0, 0x81760',
           'B x 40 (stride 1); commands copied by STCH src'),
    SField(0x19D3, 0x28, 'array', 'u19d3', '0x81760',
           'B x 40 (stride 1); commands copied by STCH src'),
    SField(0x19FB, 0x1, 'gap', 'gap_19fb', 'none (block copies only)',
           'no instruction addresses these bytes: moved only by the block copies'),
    SField(0x19FC, 0x2, 'global', 'screen_effect', '0x78B13, 0x8054D, 0x80A60, 0x80BBB, 0x8126C, 0x8138A, 0x814C9, 0x85688, 0x9531E, 0x99817',
           'i16 screen-effect kind 0x1651ABC (SHAK 0x80180, SHAF 0x80AE0, 0x95130 helpers; SHAS sets 0 at 0x99817); restore: 1 -> 0x42DC0, 2 -> 0x43160, >= 3 -> 0x46270, else 0x433F0(-1)'),
    SField(0x19FE, 0x2, 'global', 'screen_effect_p0', '0x80527, 0x80A40, 0x8124C, 0x8136B, 0x814A7',
           'i16 parameter of +0x19FC (same writers)'),
    SField(0x1A00, 0x2, 'global', 'screen_effect_p1', '0x8052E, 0x80A48, 0x81254, 0x81373, 0x814AE',
           'i16 parameter of +0x19FC'),
    SField(0x1A02, 0x2, 'global', 'screen_effect_p2', '0x80536, 0x80A50, 0x8125C, 0x8137B, 0x814B6',
           'i16 parameter of +0x19FC'),
    SField(0x1A04, 0x2, 'global', 'screen_effect_p3', '0x8053F, 0x80A58, 0x81263, 0x81383, 0x814BE',
           'i16 parameter of +0x19FC'),
    SField(0x1A06, 0x2, 'gap', 'gap_1a06', 'none (block copies only)',
           'no instruction addresses these bytes: moved only by the block copies'),
]



def check_scene_state_tiling() -> List[str]:
    """Problems with SCENE_STATE_FIELDS (gaps / overlaps / end), empty when it tiles 0..0x1A08."""
    out = []
    pos = 0
    for f in SCENE_STATE_FIELDS:
        if f.off != pos:
            out.append(f"gap or overlap at 0x{pos:x} (next field 0x{f.off:x})")
        pos = f.off + f.size
    if pos != SCENE_STATE_SIZE:
        out.append(f"ends at 0x{pos:x}")
    return out


# ---------------------------------------------------------------------------
# config block 0x66C578 (system block +0x04)
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class CField:
    off: int
    size: int
    name: str
    static: int                  # value in the eboot data image (boot default)
    recommended: Optional[int]   # value written by "Apply recommended settings" 0x3EAC0 (None = untouched)
    menu: str                    # menu item (page, item id) copied by Config_FromMenu 0x3E6E0
    readers: str
    conf: str = "E"


CONFIG_FIELDS: List[CField] = [
    CField(0x00, 2, "display value (0xCD640 arg /4)", 255, None, "-", "0x41890, 0x3EE10, 0xA0AD0, 0xA0B10, 0xA2630; "
           "no writer (the scan hits at 0x3C3F7 / 0x3F81C are nop instructions)", "H"),
    CField(0x02, 2, "display value (0xCD640 arg)", 1, None, "-", "0x41890, 0x45910, 0x53CF0, 0x57080", "H"),
    CField(0x04, 2, "live value 0..255 (stick A y in ADV: 0x61321..0x6137F, +-(|y| - 80) / 8 per frame)", 0, None, "-",
           "written by 0x60710 (0x6137F) and History_Restore 0x7CA2A (only when +0x4C != 0; dead 0x41DB0 / 0x41FC0 "
           "likewise); read by History_Push +0x363E, 0x41890 (0xCD650)", "E"),
    CField(0x06, 2, "display value (0xCD650 arg)", 127, None, "-", "0x41890, 0x3EE10", "H"),
    CField(0x08, 8, "constants", 0x00FF00FF00FF0001, None, "-", "no reader found", "E"),
    CField(0x10, 2, "HUD constant", 255, None, "-", "0x53200, 0x53CF0 (HUD)", "E"),
    CField(0x12, 2, "HUD constant", 0, None, "-", "0x53CF0", "E"),
    CField(0x14, 2, "music volume 0..100 (%)", 40, 40, "page 1 item 82 (Sound: Music value)",
           "0x41890/0x3E9B0: runtime byte 0x17DA268+0xC = (1 - mute +0xA6) * value; UI 0x412F0", "E"),
    CField(0x16, 2, "Play All Voices: 100 = on, 0 = off", 100, 100, "page 1 item 92 (== 1 -> 100 else 0)",
           "runtime 0x17DA268+0xD; voice code 0x41AB0, 0x7E790, 0x92380, 0x95E60, 0xA300, 0xAD80", "E"),
    CField(0x18, 2, "SFX volume 0..100", 40, 40, "page 1 item 87", "runtime 0x17DA268+0xE = (1 - mute +0xA7) * value", "E"),
    CField(0x1A, 2, "System Effects volume 0..100", 10, 10, "page 1 item 91",
           "runtime 0x17DA268+0xF = (1 - mute +0xA8) * value", "E"),
    CField(0x1C, 2, "constant (movie path)", 0, None, "-", "0x68A70", "H"),
    CField(0x1E, 2, "constant (BGM start)", 0, None, "-", "0x98C0, 0x9B40 (BGM)", "H"),
    CField(0x20, 2, "Unseen Text Display Speed (ms per character unit, max 250)", 35, 35, "page 0 item 55",
           "reveal speed 0x56160 / 0x561D0 / 0xA2630 (textbox.md 8.1); UI preview 0x40C30", "E"),
    CField(0x22, 2, "Seen Text Display Speed (max 250)", 15, 15, "page 0 item 59",
           "reveal speed for read lines (textbox.md 8.1)", "E"),
    CField(0x24, 2, "language (0 ja, 1 en, 2 zc, 3 zt, >= 4 system)", 0xFFFF, None,
           "page 0 items 75..78 (written by 0x406B0); SaveImage_LoadSystem 0x6FE94: < 0 (boot 0xFFFF) -> 0x407F0() "
           "(system language)", "Lang_Index 0x3ED90 and 13 other readers", "E"),
    CField(0x26, 2, "text draw constant", 1, None, "-", "0x53CF0, 0x7C340, 0xC8F50 (block draw)", "H"),
    CField(0x28, 2, "text constant", 0, None, "-", "0x3A030, 0x3C360, 0x3C4B0, 0x53200, 0x53CF0, 0x7C340, 0xA6E10, 0xD0190", "H"),
    CField(0x2A, 2, "text colour mode: 0 fixed, 1 read-text colour 0xDC6D, 2 per-speaker colour (0x86A8B0)", 0, None,
           "-", "History_Restore 0x7D977, 0x60710, Z handlers 0x92380.., 0x7B790", "E"),
    CField(0x2C, 2, "audio constant", 0, None, "-", "0x80180.., MPLY 0x87AF0, SEPL 0x893F0, 0x95130", "H"),
    CField(0x2E, 2, "Auto-Mode Wait Time (ms, max 10000)", 2200, 2200, "page 0 item 65", "auto mode (Adv_Update), UI 0x40C30", "E"),
    CField(0x30, 2, "Fast-Forward: 1 = read text only, 0 = all text", 1, 1, "page 0 item 70 (row 'Fast-Forward')",
           "0x60710 0x612DF / 0x61B0D stop skipping at unread text (0x75C90); Adv_Update 0xA0E71; Script_Prefetch", "E"),
    CField(0x32, 2, "scene-jump buttons enabled", 1, None, "-", "0x60710 0x6101D (next/previous scene)", "E"),
    CField(0x34, 2, "constant", 1, None, "-", "0x41890, 0x3EE10", "H"),
    CField(0x36, 2, "constant (voice gate with +0x16)", 1, None, "-", "0x41AB0: voice enabled = +0x16 != 0 && +0x36 != 0", "E"),
    CField(0x38, 4, "unused", 0, None, "-", "no reader", "E"),
    CField(0x3C, 4, "per-character voice enable bits (bit i, 19 used)", 0x3FFFFFFF, 0x3FFFFFFF,
           "page 1 items 98+5i (mute toggle == 1 clears bit i)", "voice play 0xA300/0xAD80, 0x92380, 0x95E60, 0x9B760", "E"),
    CField(0x40, 2, "auto save mask: unused (its only reader 0x98BBF in _ATSV is unreachable; boot value 3)", 3, None,
           "-", "Script_Step 0x98BBF (dead branch: _ATSV(8) auto-saves without testing it)", "E"),
    CField(0x42, 2, "action preset (0..2, selects the action-mask table)", 0, None, "-", "0x41890 -> 0xCEEE0", "E"),
    CField(0x44, 2, "choice-screen constant", 0, None, "-", "0xA6180, 0xA6940, 0xA6AD0", "H"),
    CField(0x46, 2, "HUD constant", 0, None, "-", "0x53CF0", "H"),
    CField(0x48, 2, "unused", 0, None, "-", "no reader", "E"),
    CField(0x4A, 2, "HUD constant", 0, None, "-", "0x45910, 0x45A30, 0x53CF0, 0x57080", "H"),
    CField(0x4C, 2, "restore +0x04 from history (gate)", 0, None, "-", "History_Restore 0x7CA07, 0x41DB0, 0x41FC0", "E"),
    CField(0x4E, 2, "Interrupt Voices on Advancing Text", 1, 1, "page 1 item 94", "Adv_Update 0xA1027, 0xA115B, 0xA1254 -> 0x8EA0 (stop voice)", "E"),
    CField(0x50, 2, "constant", 1, None, "-", "0x56AB0", "H"),
    CField(0x52, 2, "unused", 0, None, "-", "no reader", "E"),
    CField(0x54, 2, "music volume copy (ducking base)", 40, 40, "copy of +0x14 (0x3E84E)",
           "runtime 0x17DA268+0xB = (1 - mute +0xA6) * value", "E"),
    CField(0x56, 2, "dialog constant", 0, None, "-", "0x60710 (> 0: 0xA5DC0 dialog), 0xC6E0, 0xE8280", "H"),
    CField(0x58, 2, "constant", 0, None, "-", "0x57080, 0x5A5E0", "H"),
    CField(0x5A, 0x1C, "two 14-byte records copied from 0x164FCD8 / 0x164FCE8 by 0x419D0 at commit and back by "
           "0x41890 at load (filled at boot by 0x75F10 from *0x86AA00 / *0x86AA08)", 0, None, "-", "0x41890, 0x419D0", "E"),
    CField(0x76, 0x26, "per-character voice volume u16[19] (0..100)", 50, 50,
           "page 1 items 100+5i (i < 19); defaults 16 x 50 (0x1A66A0) + 3 x 50",
           "voice play 0xA300/0xAD80, 0x7E790, 0x3E9B0 test playback", "E"),
    CField(0x9C, 1, "Display End of Page Instantly (unseen text)", 0, 0, "page 0 item 54",
           "page option latch 0x56140 (textbox.md 8.3)", "E"),
    CField(0x9D, 1, "Display End of Page Instantly (seen text)", 0, 0, "page 0 item 58",
           "History_Restore 0x7D956, 0x92380", "E"),
    CField(0x9E, 2, "Auto-Mode Advance Page Speed (ms, max 10000)", 3000, 3000, "page 0 item 62", "auto mode, UI 0x40C30", "E"),
    CField(0xA0, 2, "Auto Skip Cleared Scenes", 0, 0, "page 0 item 66", "0x61F40", "E"),
    CField(0xA2, 1, "Show Confirmation Message (auto skip)", 0, 0, "page 0 item 68", "0x60710 0x608F1", "E"),
    CField(0xA3, 1, "Text Display Animations", 0, 0, "page 0 item 72", "block draw 0xC8F50", "E"),
    CField(0xA4, 1, "Seen Text Display Animations", 0, 0, "page 0 item 74", "HUD 0x53CF0 (previous-message fade, textbox.md 5.8)", "E"),
    CField(0xA5, 1, "constant", 1, 1, "-", "FADS 0x81520, 0xC0330", "H"),
    CField(0xA6, 1, "Music mute", 0, 0, "page 1 item 81", "volume formula above", "E"),
    CField(0xA7, 1, "SFX mute", 0, 0, "page 1 item 85", "volume formula above", "E"),
    CField(0xA8, 1, "System Effects mute", 0, 0, "page 1 item 90", "volume formula above", "E"),
    CField(0xA9, 0x97, "unused", 0, None, "-", "no reader found", "E"),
]

# Config_FromMenu (0x3E6E0): (page, item) -> config offset, width (verified by emulation)
FROM_MENU: List[Tuple[int, int, int, int]] = [
    (0, 0x37, 0x20, 2), (0, 0x36, 0x9C, 1), (0, 0x3B, 0x22, 2), (0, 0x3A, 0x9D, 1), (0, 0x3E, 0x9E, 2),
    (0, 0x41, 0x2E, 2), (0, 0x42, 0xA0, 2), (0, 0x44, 0xA2, 1), (0, 0x46, 0x30, 2), (0, 0x48, 0xA3, 1),
    (0, 0x4A, 0xA4, 1), (1, 0x52, 0x14, 2), (1, 0x51, 0xA6, 1), (1, 0x57, 0x18, 2), (1, 0x55, 0xA7, 1),
    (1, 0x5B, 0x1A, 2), (1, 0x5A, 0xA8, 1), (1, 0x5E, 0x4E, 2),
] + [(1, 100 + 5 * i, 0x76 + 2 * i, 2) for i in range(19)]

# options listed in engine.md 8.4 (from the shared UI help-text table) -> PS4 storage
ENGINE_84_OPTIONS: List[Tuple[str, str]] = [
    ("Sound: reset", "Recommended Settings button: 0x3EAC0 (defaults above)"),
    ("BGM volume", "+0x14 (and +0x54), mute +0xA6"),
    ("voice volume", "no master slider on PS4: +0x16 = Play All Voices (100/0); per-character +0x76"),
    ("SE volume", "+0x18, mute +0xA7"),
    ("system SE volume", "+0x1A, mute +0xA8"),
    ("movie volume", "not on the PS4 screen; no config field found (movie audio uses its own port)"),
    ("stop voice on text advance", "+0x4E Interrupt Voices on Advancing Text"),
    ("lower BGM during voice", "not on the PS4 screen; +0x54 (copy of the music volume) feeds runtime byte 0x17DA268+0xB"),
    ("BGM 2ch / 5.1ch (mono/stereo)", "not on the PS4 screen; BGM start reads constant +0x1E"),
    ("Voice: reset / system voice", "not on the PS4 screen"),
    ("per-character voice on/off + volume (23 names)", "19 rows: bit i of +0x3C, u16 +0x76+2i"),
    ("text speed", "+0x20 unseen, +0x22 seen"),
    ("auto speed", "+0x9E Auto-Mode Advance Page Speed, +0x2E Auto-Mode Wait Time (ms)"),
    ("window opacity", "not on the PS4 screen; +0x04 is a live value changed with the right stick"),
    ("outline on/off", "not on the PS4 screen; +0xA3 Text Display Animations occupies the row"),
    ("shadow on/off", "not on the PS4 screen"),
    ("fixed colour vs change colour of read text", "+0x2A (constant 0 on PS4), +0xA4 Seen Text Display Animations"),
    ("window design", "not on the PS4 screen"),
    ("language", "+0x24 (radio items 75..78, written by 0x406B0)"),
    ("System: skip all text vs read only", "+0x30 Fast-Forward (1 = read only)"),
    ("stop skip at choices", "not on the PS4 screen"),
    ("auto quick save at choices / at scene start", "+0x40 (constant 3) is never tested on a reachable path: "
     "_ATSV(8) always auto-saves; 0x6D450 / 0x6D540 / 0x6D600 (called from 0x6E200, 0x6DA90, 0x6DD60, "
     "not traced) call AutoSave 0x73B70 without reading it"),
    ("button help text (two contexts)", "not on the PS4 screen"),
    ("vibration", "not on the PS4 screen"),
    ("show controls", "Controls tab: pictures CONF_PARTS 16/17 only"),
    ("apply recommended settings", "0x3EAC0 then Config_ToMenu 0x3E4A0 for pages 0..2"),
    ("Display End of Page Instantly (PS4 only)", "+0x9C unseen / +0x9D seen"),
    ("Auto Skip Cleared Scenes + Show Confirmation Message (PS4 only)", "+0xA0 / +0xA2"),
]


# ---------------------------------------------------------------------------
# input actions
# ---------------------------------------------------------------------------
# game input word (VA 0xE3A40: *0xDC7BC0 +0x10 held, +0x18 pressed-or-repeat, +0x20 newly pressed)
GAME_INPUT_BITS: Dict[int, str] = {
    0x1: "L2", 0x2: "R2", 0x4: "L1", 0x8: "R1", 0x10: "TRIANGLE", 0x20: "decide", 0x40: "cancel",
    0x80: "SQUARE", 0x100: "(never set by the pad)", 0x200: "L3", 0x400: "R3", 0x800: "OPTIONS",
    0x1000: "up", 0x2000: "right", 0x4000: "down", 0x8000: "left",
    0x10000: "L-stick up", 0x20000: "L-stick right", 0x40000: "L-stick down", 0x80000: "L-stick left",
    0x100000: "touch pad click",
}


def bits_name(mask: int) -> str:
    if mask == 0:
        return "-"
    return "|".join(GAME_INPUT_BITS.get(1 << b, f"bit{b}") for b in range(24) if mask >> b & 1)


@dataclass(frozen=True)
class Action:
    index: int
    name: str
    users: str
    conf: str


ACTIONS: List[Action] = [
    Action(0, "confirm / advance text", "Adv_Update 0xA0ED6 (pressed advances), every menu, title 0xB132D", "E"),
    Action(1, "cancel / back; in ADV it also ends latched skip (0x17DB230 bit 1)", "menus; 0x60710 0x61854 (or right click)", "E"),
    Action(2, "choice screen: hold check (only preset 2: R2)", "SELD input 0xA6180 0xA6706/0xA6737", "H"),
    Action(3, "choice screen: repeat check", "0xA6180 0xA6773/0xA684B", "H"),
    Action(4, "choice screen: select (pressed)", "0xA6180 0xA67A4", "H"),
    Action(5, "unused (preset 2: R1)", "no reference found", "E"),
    Action(6, "list paging (with down)", "0x2FFF0, 0x69BB0, title 0xB1363", "H"),
    Action(7, "list paging", "0x2FFF0 0x30089, 0x69BB0", "H"),
    Action(8, "list next (with right)", "0x2FEB0, 0x2FFF0 0x30053", "H"),
    Action(9, "list previous (with up)", "0x2FFF0 0x30067", "H"),
    Action(10, "open the backlog", "0x60710 0x615A1 -> Backlog_Open 0x3BFB0; save menu 0x6ECB5", "E"),
    Action(11, "dialog cancel", "0x6D600 0x6D682", "H"),
    Action(12, "toggle latched skip / fast-forward (0x17DB230 bit 1)", "0x60710 0x6141F / 0x6163B (or middle click)", "E"),
    Action(13, "toggle auto mode (0x17DB230 bit 0)", "0x60710 0x61683; Adv_Update 0xA1D78 / 0xA1F14", "E"),
    Action(14, "quick save", "0x60710 0x61B80 -> QuickSave_Store 0x73A70 (300-frame cooldown)", "E"),
    Action(15, "quick load (unbound: 0x100 / 0 / 0)", "0x60710 0x61C72 (slot 139 present)", "E"),
    Action(16, "replay voice (unbound)", "0x60710 0x60DFA -> 0xC3A80(0x1654204)", "E"),
    Action(17, "open the system menu", "0x60710 0x615D3 / 0x610E2", "H"),
    Action(18, "close the backlog", "backlog 0x3CA75 (or right click)", "E"),
    Action(19, "fast-forward (hold; analog R2 sets the speed)", "0x60710 0x611C7 / 0x61277", "E"),
    Action(20, "rewind (hold; analog when the mask is L2)", "0x60710 0x6154D -> Adv_Rewind 0xA2020", "E"),
    Action(21, "ADV shortcut (up)", "0x60710 0x60BF3 -> 0xA5DD0", "H"),
    Action(22, "close the save/load menu", "save menu 0x6ECB9 (with action 10)", "H"),
    Action(23, "jump to the next scene (asks first)", "0x60710 0x6104C -> 0x75440(1), state 0xDF29D8 = 1", "E"),
    Action(24, "jump to the previous scene (asks first)", "0x60710 0x616DA -> 0x75440(-1), state 4", "E"),
    Action(25, "hide / show the message window (0x17DB23E)", "0x60710 0x61CBF; held: 0x60DBE", "E"),
    Action(26, "unused (preset 0: 0x100)", "no reference found", "E"),
]


# ---------------------------------------------------------------------------
# eboot readers
# ---------------------------------------------------------------------------
def _eboot(path: Union[str, Path, None]) -> Path:
    if path is not None:
        return Path(path)
    from tsuki import game_dir
    g = game_dir()
    if g is None:
        raise FileNotFoundError("game folder not found (set TSUKI_GAME_DIR)")
    return g / "eboot.bin"


def _read(path: Path, off: int, n: int) -> bytes:
    with open(path, "rb") as fh:
        fh.seek(off)
        return fh.read(n)


def read_data(eboot: Union[str, Path, None], va: int, n: int) -> bytes:
    return _read(_eboot(eboot), va - DATA_VA + DATA_OFF, n)


def read_text(eboot: Union[str, Path, None], va: int, n: int) -> bytes:
    return _read(_eboot(eboot), va + TEXT_OFF, n)


def read_history_lists(eboot: Union[str, Path, None] = None) -> Tuple[List[int], List[int], List[int]]:
    """(u16 list, bool list, s8 list) as History_Push walks them (s8 cut at the first
    negative entry; entries > 1200 keep their slot but are skipped)."""
    u16 = list(struct.unpack("<39h", read_text(eboot, HIST_LIST_U16, 78)))
    bl = list(struct.unpack("<43h", read_text(eboot, HIST_LIST_BOOL, 86)))
    s8 = list(struct.unpack("<300h", read_text(eboot, HIST_LIST_S8, 600)))
    end = next((k for k, v in enumerate(s8) if v < 0), len(s8))
    return u16, bl, s8[:end]


def read_action_tables(eboot: Union[str, Path, None] = None) -> List[List[int]]:
    """[preset][action] u64 masks (3 x 27) at 0xDB84F0."""
    b = read_data(eboot, ACTION_TABLES, 3 * 27 * 8)
    return [list(struct.unpack_from("<27Q", b, 27 * 8 * t)) for t in range(3)]


def read_static_config(eboot: Union[str, Path, None] = None) -> bytes:
    """The config block as stored in the eboot data image (the boot defaults)."""
    return read_data(eboot, CONFIG, CONFIG_SIZE)


@dataclass
class MenuRow:
    items: List[int]
    types: List[int]
    max_value: int
    value: int


@dataclass
class MenuPage:
    first: int
    last: int
    rows: List[MenuRow] = field(default_factory=list)


def read_config_pages(eboot: Union[str, Path, None] = None, n: int = 2) -> List[MenuPage]:
    """Static config menu pages at 0x66E890 (stride 0x9D0): item id range (+0, +4), row count
    (+0xC), items per row (+0x10..), type grid (+0x78, 26 x 5 i32), item grid (+0x280),
    float positions (+0x488..), current value per row (+0x900), maximum per row (+0x968)."""
    out = []
    for p in range(n):
        b = read_data(eboot, CONFIG_PAGES + p * 0x9D0, 0x9D0)
        first, last, _m1, nrows = struct.unpack_from("<iiii", b, 0)
        page = MenuPage(first, last)
        for r in range(nrows):
            items = [struct.unpack_from("<i", b, 0x280 + 20 * r + 4 * c)[0] for c in range(5)]
            types = [struct.unpack_from("<i", b, 0x78 + 20 * r + 4 * c)[0] for c in range(5)]
            k = struct.unpack_from("<i", b, 0x10 + 4 * r)[0]
            page.rows.append(MenuRow(items[:k], types[:k], struct.unpack_from("<i", b, 0x968 + 4 * r)[0],
                                     struct.unpack_from("<i", b, 0x900 + 4 * r)[0]))
        out.append(page)
    return out


# ---------------------------------------------------------------------------
# record / save decoding
# ---------------------------------------------------------------------------
_FMT = {"u8": "<B", "i8": "<b", "u16": "<H", "i16": "<h", "i32": "<i", "u64": "<Q"}


def parse_history_record(rec: bytes) -> Dict[str, object]:
    """Decode the scalar fields of one 0x3A10-byte record (arrays returned as bytes)."""
    if len(rec) < RECORD_SIZE:
        raise ValueError(f"record of 0x{len(rec):x} bytes")
    out: Dict[str, object] = {}
    for f in HISTORY_FIELDS:
        raw = rec[f.off:f.off + f.size]
        if f.ctype in _FMT:
            out[f.name] = struct.unpack(_FMT[f.ctype], raw)[0]
        elif f.name == "message_text":
            out[f.name] = raw.split(b"\0", 1)[0]
        elif not f.name.startswith("unused"):
            out[f.name] = raw
    return out


def history_f_values(rec: bytes, eboot: Union[str, Path, None] = None,
                     lists: Optional[Tuple[List[int], List[int], List[int]]] = None) -> Dict[int, int]:
    """F[index] -> u16 value as History_Restore writes it (order u16, s8, bool)."""
    u16, bl, s8 = lists or read_history_lists(eboot)
    f: Dict[int, int] = {}
    for k, idx in enumerate(u16):
        f[idx] = struct.unpack_from("<H", rec, 0x34AA + 2 * k)[0]
    for k, idx in enumerate(s8):
        if 0 <= idx <= 0x4B0:
            f[idx] = struct.unpack_from("<b", rec, 0x3501 + k)[0] & 0xFFFF
    for k, idx in enumerate(bl):
        if 0 <= idx <= 0x4B0:
            f[idx] = (rec[0x34FA + (k >> 3)] >> (k & 7)) & 1
    return f


def slot_states(slot: bytes) -> Tuple[bytes, Optional[bytes]]:
    """(state A, state B or None) of a 0x7108-byte slot record (eboot.savedata layout)."""
    a = slot[8:8 + RECORD_SIZE]
    has_b = struct.unpack_from("<i", slot, 4)[0]
    return a, (slot[0x3A18:0x3A18 + 0x36A8] if has_b == 1 else None)


def describe_slot(slot: bytes) -> Dict[str, object]:
    a, b = slot_states(slot)
    ra = parse_history_record(a)
    d = {k: ra[k] for k in ("serial", "script", "cmd_offset", "resume_offset", "text_block",
                            "text_line", "mode", "bgm_track", "voice_id")}
    d["has_state_b"] = b is not None
    if b is not None:
        rb = parse_history_record(b + bytes(RECORD_SIZE - len(b)))
        d["b_script"] = rb["script"]
        d["b_cmd_offset"] = rb["cmd_offset"]
    return d


_ZM_RX = re.compile(rb"(?:^|;)[^_;]*_ZM(.....)", re.S)   # first '_' of a statement = its name


def marker_max_lines(scr: Optional[object] = None) -> Dict[int, int]:
    """text block -> highest line of the `_ZM` markers in all scripts (engine marker decoding,
    tsuki.script.marker_hex).  42,590 markers in 784 blocks on v01.01."""
    from tsuki.script import AllScr, marker_hex
    own = scr is None
    s = AllScr() if own else scr
    try:
        out: Dict[int, int] = {}
        for i in range(len(s)):                                   # type: ignore[arg-type]
            for mt in _ZM_RX.finditer(s.raw(i)):                   # type: ignore[union-attr]
                blk, line = marker_hex(mt.group(1)[:3]), marker_hex(mt.group(1)[3:])
                out[blk] = max(out.get(blk, -1), line)
        return out
    finally:
        if own:
            s.close()                                             # type: ignore[union-attr]


def check_save_image(buf: bytes, scr: Optional[object] = None,
                     markers: Optional[Dict[int, int]] = None) -> List[str]:
    """Acceptance checks for a save image (adv_state.md 7.7); returns the problems found.

    * eboot.savedata.parse issues, except "end magic" of unused slots (empty slots carry the
      block magic but no end marker, adv_state.md 12);
    * every used slot: end marker 0x2AF8, sane date, serial >= 0, known kind, script number
      < number of scripts, cmd_offset at a statement start of the decoded script (the byte
      before a '_' that starts the statement name, as Script_Step passes name - 2), resume
      offset inside the script; the same for state B when present;
    * readmax (system block +0x738, 4020 bytes): every non-zero entry is a text block that has
      `_ZM` markers and is <= the block's highest marker line."""
    from eboot import savedata
    from tsuki.script import AllScr
    img = savedata.parse(buf)
    issues: List[str] = []
    for i in img.issues:
        mt = re.match(r"slot (\d+): end magic", i)
        if mt and not img.slots[int(mt.group(1))].used:
            continue                                  # empty slot: magic without end marker
        issues.append(i)
    own = scr is None
    s = AllScr() if own else scr
    try:
        nscr = len(s)                                             # type: ignore[arg-type]
        for sl in img.slots:
            if not sl.present:
                continue
            blk = buf[sl.offset:sl.offset + savedata.SLOT_SIZE]
            a, b = slot_states(blk)
            tag = f"slot {sl.index}"
            t = sl.time
            if t is None or not (2020 <= t.year <= 2100 and 1 <= t.month <= 12 and 1 <= t.day <= 31
                                 and t.hour < 24 and t.minute < 60 and t.second < 60):
                issues.append(f"{tag}: date {t}")
            for which, rec in (("A", a), ("B", b)):
                if rec is None:
                    continue
                r = parse_history_record(rec + bytes(RECORD_SIZE - len(rec)))
                if r["serial"] < 0:                                # type: ignore[operator]
                    issues.append(f"{tag} state {which}: serial {r['serial']}")
                if r["mode"] not in HISTORY_MODES:
                    issues.append(f"{tag} state {which}: kind {r['mode']}")
                sn = int(r["script"])                              # type: ignore[arg-type]
                if not 0 <= sn < nscr:
                    issues.append(f"{tag} state {which}: script {sn} >= {nscr}")
                    continue
                raw = s.raw(sn)                                    # type: ignore[union-attr]
                off = int(r["cmd_offset"])                         # type: ignore[arg-type]
                if not (off + 2 < len(raw) and raw[off + 1] == 0x5F):
                    issues.append(f"{tag} state {which}: cmd_offset 0x{off:x} not at a statement of script {sn}")
                if which == "A" and not int(r["resume_offset"]) <= len(raw):   # type: ignore[arg-type]
                    issues.append(f"{tag} state A: resume_offset 0x{int(r['resume_offset']):x} outside script {sn}")
        mk = markers if markers is not None else marker_max_lines(s)
    finally:
        if own:
            s.close()                                             # type: ignore[union-attr]
    rmax = buf[0x738:0x738 + 0xFB4]
    for blk_id, v in enumerate(rmax):
        if v and blk_id not in mk:
            issues.append(f"readmax[{blk_id}] = {v}: block has no _ZM markers")
        elif v and v > mk[blk_id]:
            issues.append(f"readmax[{blk_id}] = {v} > highest marker line {mk[blk_id]}")
    return issues


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------
def tables_markdown(eboot: Union[str, Path, None] = None) -> str:
    lines = ["# ADV system state tables (generated by tsuki.misc_adv_system_state)", ""]
    lines += ["## History record (0x3A10 bytes)", "", "| off | size | name | type | push source | conf |",
              "|---|---|---|---|---|---|"]
    for f in HISTORY_FIELDS:
        src = f"0x{f.push_src:X}" if f.push_src is not None else "-"
        lines.append(f"| +0x{f.off:04X} | 0x{f.size:X} | {f.name} | {f.ctype} | {src} | {f.conf} |")
    lines += ["", "## History_Push call sites (59)", "", "| site | function | kind | command | live |",
              "|---|---|---|---|---|"]
    for x in PUSH_SITES:
        lines.append(f"| 0x{x.site:X} | 0x{x.func:X} | {'/'.join(map(str, x.kinds))} | {x.command} | "
                     f"{'yes' if x.live else 'dead code'} |")
    lines += ["", "## scene_state (0x16500C0, 0x1A08 bytes)", "", "| off | size | kind | name | writers | note |",
              "|---|---|---|---|---|---|"]
    for f in SCENE_STATE_FIELDS:
        note = f.note.replace("|", r"\|")
        lines.append(f"| +0x{f.off:04X} | 0x{f.size:X} | {f.kind} | {f.name} | {f.writers} | {note} |")
    tabs = read_action_tables(eboot)
    lines += ["", "## Action masks (presets 0/1/2)", "", "| # | preset 0 | preset 1 | preset 2 | name | conf |",
              "|---|---|---|---|---|---|"]
    for a in ACTIONS:
        lines.append(f"| {a.index} | {bits_name(tabs[0][a.index])} | {bits_name(tabs[1][a.index])} | "
                     f"{bits_name(tabs[2][a.index])} | {a.name} | {a.conf} |")
    st = read_static_config(eboot)
    lines += ["", "## Config fields", "", "| off | size | name | boot value | recommended | menu | conf |",
              "|---|---|---|---|---|---|---|"]
    for c in CONFIG_FIELDS:
        v = int.from_bytes(st[c.off:c.off + min(c.size, 8)], "little")
        rec = "-" if c.recommended is None else str(c.recommended)
        lines.append(f"| +0x{c.off:02X} | {c.size} | {c.name} | {v} | {rec} | {c.menu} | {c.conf} |")
    return "\n".join(lines) + "\n"


def dump_samples(outdir: Union[str, Path], eboot: Union[str, Path, None] = None) -> List[Path]:
    out = Path(outdir)
    out.mkdir(parents=True, exist_ok=True)
    written = []
    p = out / "tables.md"
    p.write_text(tables_markdown(eboot), encoding="utf-8")
    written.append(p)
    pages = read_config_pages(eboot)
    p = out / "config_pages.json"
    p.write_text(json.dumps([asdict(x) for x in pages], indent=1), encoding="utf-8")
    written.append(p)
    tabs = read_action_tables(eboot)
    p = out / "actions.json"
    p.write_text(json.dumps([{"index": a.index, "name": a.name, "conf": a.conf, "users": a.users,
                              "masks": [hex(tabs[t][a.index]) for t in range(3)],
                              "buttons": [bits_name(tabs[t][a.index]) for t in range(3)]} for a in ACTIONS],
                            indent=1), encoding="utf-8")
    written.append(p)
    p = out / "history_fields.json"
    p.write_text(json.dumps([asdict(f) for f in HISTORY_FIELDS], indent=1), encoding="utf-8")
    written.append(p)
    p = out / "config_fields.json"
    p.write_text(json.dumps([asdict(c) for c in CONFIG_FIELDS], indent=1), encoding="utf-8")
    written.append(p)
    p = out / "push_sites.json"
    p.write_text(json.dumps([{"site": hex(x.site), "func": hex(x.func), "kinds": list(x.kinds),
                              "command": x.command, "live": x.live} for x in PUSH_SITES], indent=1),
                 encoding="utf-8")
    written.append(p)
    p = out / "scene_state_fields.json"
    p.write_text(json.dumps([dict(asdict(f), off=hex(f.off), size=hex(f.size)) for f in SCENE_STATE_FIELDS],
                            indent=1), encoding="utf-8")
    written.append(p)
    return written


def _main(argv: Sequence[str]) -> int:
    import argparse
    ap = argparse.ArgumentParser(prog="python -m tsuki.misc_adv_system_state")
    ap.add_argument("cmd", choices=["tables", "save", "dump"])
    ap.add_argument("path", nargs="?")
    ap.add_argument("--eboot")
    a = ap.parse_args(list(argv))
    if a.cmd == "tables":
        sys.stdout.write(tables_markdown(a.eboot))
        return 0
    if a.cmd == "dump":
        for p in dump_samples(a.path or "samples/misc_adv_system_state", a.eboot):
            print(p)
        return 0
    from eboot import savedata
    p = Path(a.path) if a.path else savedata.default_save_path()
    if p is None or not p.is_file():
        print("no save file found", file=sys.stderr)
        return 2
    buf = p.read_bytes()                           # read-only; copy a real save to scratch first
    img = savedata.parse(buf)
    for s in img.slots:
        if s.present:
            slot = buf[s.offset:s.offset + savedata.SLOT_SIZE]
            print(s.index, s.time, describe_slot(slot))
    problems = check_save_image(buf)
    for i in problems:
        print("problem:", i)
    print(f"{sum(1 for s in img.slots if s.present)} used slots, {len(problems)} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))


# ---------------------------------------------------------------------------
# History_Restore copies (record offset, size, RAM VA, how); checked by emulation
# ---------------------------------------------------------------------------
RESTORE_COPIES: List[Tuple[int, int, int, str]] = [
    (0x001A, 2, 0x1654344, "sx16_32"), (0x001C, 2, 0x1654348, "sx16_32"),
    (0x001E, 1, 0x164FCF6, "sx8_16"), (0x0026, 2, 0x16545D0, "copy"), (0x0028, 0x28, 0x1654240, "copy"),
    (0x0052, 2, 0x16500BE, "copy"), (0x3468, 0x40, 0x174DAB0, "copy"), (0x34A8, 2, 0x174D940, "copy"),
    (0x3630, 4, 0x1654204, "copy"), (0x363C, 2, 0x1654238, "copy"), (0x3641, 1, 0x165420A, "sx8_16"),
    (0x366C, 0x28, 0x16542D0, "copy"), (0x3694, 0x28, 0x1654300, "copy"), (0x36BC, 0x14, 0x1654330, "copy"),
    (0x38B0, 0x32, 0x164FD00, "copy"), (0x38FE, 0xFA, 0x174D9B0, "copy"),
] + [(0x3642 + i, 1, 0x1654270 + 2 * i, "sx8_16") for i in range(20)] \
  + [(0x3657 + i, 1, 0x16542A0 + 2 * i, "sx8_16") for i in range(20)] \
  + [(0x36D0 + 0x18 * i + a, n, 0x16543F0 + 0x18 * i + a, "copy") for i in range(20)
     for a, n in ((0, 3), (4, 2), (8, 0xE))]
# every kind restores scene_state from +0x58 first; kinds 1 and 9 then use +0x1A60 for both
# copies; kind 0 then sets +0x1984 = F931 (if >= 0) and +0x1982 = +0x1984
# (misc_adv_system_state_emu.kind0_scene_state).  0x1654344 / 0x1654348 are also rewritten by
# the kind-0 re-display (from_history = 1).
