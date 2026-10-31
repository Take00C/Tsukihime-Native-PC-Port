"""Curated names for eboot.bin functions and data (CUSA47999 v01.01).

Every entry was identified during the engine analysis (docs/formats/engine.md);
`evidence` says how.  Functions are eh_frame function starts (checked by
tests/test_eboot.py).  Names are ours ("nexl" is the Hunex engine library prefix
seen in the allocator tags, e.g. "nexl_ShaderMgr").

CLI:  python -m eboot.symbols [--tsv out.tsv]
"""
from __future__ import annotations

import sys
from typing import List, NamedTuple


class Sym(NamedTuple):
    va: int
    name: str
    kind: str        # "func" / "data" / "table"
    evidence: str


SYMBOLS: List[Sym] = [
    # ---- boot / scheduler
    Sym(0x0EF390, "_start", "func", "ELF entry point"),
    Sym(0x0ED210, "main", "func", "called by _start; inits SaveData/PlayGo/SharePlay/LiveStreaming, creates tasks task0/task_game"),
    Sym(0x0ED1B0, "task0_main", "func", "task 'task0' entry (0x126EE0 arg)"),
    Sym(0x0EEE50, "task_game_main", "func", "task 'task_game': PlayGo, 2 display surfaces 1920x1080, 16 nexl_Capture, mouse, boot states, main loop"),
    Sym(0x0ED1F0, "scheduler_frame_cb", "func", "callback passed to 0x12A8F0"),
    Sym(0x126EE0, "Scheduler_AddTask", "func", "(sched, slot, name, fn): 'task0'/'task_game'"),
    Sym(0x1270D0, "Scheduler_Wait", "func", "(sched, n) yield n frames; used in every task loop"),
    Sym(0x12A8F0, "Scheduler_Run", "func", "main-thread loop entered from main()"),
    Sym(0x126A60, "Scheduler_Init", "func", "'Scheduler sema', 'nexl_TaskThread'"),
    Sym(0x12AFF0, "nexl_CreateThread", "func", "scePthreadCreate wrapper, name 'NEXL_THREAD', affinity mask"),
    Sym(0x12C430, "nexl_Alloc", "func", "(size?, align, tag, ...) allocation with tag string"),
    Sym(0x12A000, "System_Init", "func", "maps onion/garlic/system memory; creates nexl_UserService..nexl_Trophy"),
    Sym(0x129650, "SystemService_Init", "func", "sceSystemServiceParamGetInt(LANG), GetString(6), sysmodule unload"),
    Sym(0x0D1F40, "Game_OpenArchives", "func", "opens *.nam/*.hed/*.mrg into slots 4..0x1E"),
    Sym(0x0E4FA0, "Archive_OpenSlot", "func", "(name, slot) standalone .mrg/.nam/.hed"),
    Sym(0x0E54C0, "Archive_OpenHedMrg", "func", "(hed, mrg, slot)"),
    Sym(0x0E6DF0, "Mzp_SectionPtr", "func", "(section, slot) -> data"),
    Sym(0x0E7070, "Mzx_Size", "func", "MZX0 header size"),
    Sym(0x0E7080, "Mzx_Decompress", "func", "MZX0 decoder (see containers.md)"),
    Sym(0x0C6980, "Font_Load", "func", "ft_<lang>.mrg via table 0x2D3560 into slot 3; sections -> textures 0x21.."),
    Sym(0x03ED90, "Lang_Index", "func", "config +0x24 if <4 else system LANG: 0 ja,1 en,2 zc,3 zt"),
    Sym(0x041AD0, "Save_IconPath", "func", "builds '/app0/data/if_<lang>.png' (unterminated strcat bug)"),
    Sym(0x067530, "Game_StartProcesses", "func", "registers game processes with 0xECE30"),
    Sym(0x0ECE30, "Process_Register", "func", "(id, ?, priority, update_fn, draw_fn)"),
    Sym(0x0EEA90, "Game_FrameUpdate", "func", "per-frame update called from task_game loop"),
    # ---- graphics
    Sym(0x0F3EB0, "Graphics_Init", "func", "VideoOut open, SetFlipRate(0), RegisterBuffers"),
    Sym(0x0F4440, "Graphics_ReopenVideoOut", "func", "VideoOut open + SetFlipRate(0) + RegisterBuffers"),
    Sym(0x0F4830, "Graphics_SetFlipMode", "func", "mode -> sceVideoOutSetFlipRate(table 0x2D7E90)"),
    Sym(0x0F4D60, "Graphics_BeginFrame", "func", "waits GPU labels, FPS ring (1000/ms, cap 99), vblank status, frame++"),
    Sym(0x0F4BE0, "Graphics_EndFrame", "func", "GPU-load % (ms/16.6667*100, cap 999), submit+flip, SubmitDone, WaitVblank"),
    Sym(0x0FCBA0, "VideoOut_Open", "func", "sceVideoOutOpen wrapper"),
    Sym(0x0FCCB0, "VideoOut_RegisterBuffers", "func", "SetBufferAttribute + RegisterBuffers + 'eop queue'"),
    Sym(0x0FCC40, "VideoOut_SetFlipRate", "func", "sceVideoOutSetFlipRate wrapper"),
    Sym(0x0FCC30, "VideoOut_WaitVblank", "func", "sceVideoOutWaitVblank tail call"),
    Sym(0x0FCBD0, "VideoOut_GetVblankStatus", "func", "sceVideoOutGetVblankStatus wrapper"),
    Sym(0x106330, "ShaderMgr_CreateBasic", "func", "registers 12 basic/transition/sepia/capture shaders"),
    Sym(0x106F60, "ShaderMgr_CreateBlend", "func", "registers dummy, 24 blend shaders, clear"),
    Sym(0x106860, "ShaderMgr_Register", "func", "(mgr, id, shader, key) hash = crc32(key), 127 buckets"),
    Sym(0x106C10, "ShaderMgr_Find", "func", "(mgr, crc32(key))"),
    Sym(0x0FD070, "crc32", "func", "reflected CRC-32, init ~0, final ~ (table 0x20851C0)"),
    Sym(0x0EF3F0, "DisplaySurface_Ctor", "func", "shader table at +0x2D8..+0x3F0 by crc32 key"),
    Sym(0x0F0E70, "DisplaySurface_SelectShader", "func", "blend mode byte +0x608 -> shader index (table 0x2D7CAC)"),
    Sym(0x0F0690, "DisplaySurface_SetBlendMode", "func", "stores byte +0x608"),
    Sym(0x0F12F0, "DisplaySurface_Draw", "func", "draw with shader +0x638, transition params +0x5DC..+0x60C"),
    Sym(0x0D2BE0, "ScreenEffect_Apply", "func", "effect id (0x1DBD0A0, 1..51) -> blend mode table 0x2D5978, strength 0x2D59AC"),
    Sym(0x0C6C20, "Text_Layout", "func", "message layout/render; inline tags [heart] [heart2] [star]"),
    Sym(0x0C29B0, "UiText_Get", "func", "(id 1..1498) -> ui_text[config.language*1499 + id] or NULL (30 callers)"),
    Sym(0x0C29F0, "UiText_GetLang", "func", "(id, lang) -> ui_text[lang*1499 + id] or NULL"),
    Sym(0x049F60, "Layer_SetImage", "func", "layer[i]+0x18 = image id (<0x59D8 else -1); layers at 0xDE2650"),
    Sym(0x049F90, "Layer_SetBlend", "func", "layer[i]+0x1C = u16 blend mode"),
    # ---- script VM
    Sym(0x095E60, "Script_Step", "func", "main interpreter: up to 1000 segments per call, returns new script pointer"),
    Sym(0x09B760, "Script_Prefetch", "func", "second interpreter (called Script_Replay in earlier drafts): look-ahead scanner, saves/restores F/Z/G, collects archive entries into prefetch_list; <= 60 commands per call"),
    Sym(0x0A0B70, "Adv_Update", "func", "calls Script_Step, stores result in 0x16543B0"),
    Sym(0x0A4060, "Script_Scan", "func", "calls Script_Step with scan flag 0x174DE48 = 1"),
    Sym(0x0A0740, "Prefetch_SeedFromScene", "func", "adds the images of scene_state to prefetch_list (dedup, <= 100 entries)"),
    Sym(0x075A60, "Prefetch_AddPending", "func", "adds the entries of the i32 list 0x1654040 (count 0x16541D0) to prefetch_list within the byte budget 0x86AA20"),
    Sym(0x0E6B30, "Archive_EntrySize", "func", "(entry, slot) -> size_sectors << 11 from the slot's HED, -1 if empty"),
    Sym(0x0A2020, "Adv_Rewind", "func", "restores history record, seeds prefetch_list (0x7ECB0), then Script_Prefetch(count+0x12)"),
    Sym(0x07C6D0, "History_Restore", "func", "restores a 0x3A10-byte state record; loads allscr section n+3, NOT-decrypts"),
    Sym(0x0788C0, "Script_OpenArchives", "func", "allscr.mrg slot 0x1F, script_text.mrg slot 0x20; buffer 0x16545E0"),
    Sym(0x079D30, "Script_EvalCondition", "func", "operands F<n> (0x164B1D0), Z<n> (0x164BB30), G<n> (0x164C620)"),
    Sym(0x078410, "NameTable_Lookup", "func", "uppercase, cut at '.', std::map lookup -> u16 index or 0xFFFF"),
    Sym(0x0957B0, "Image_ResolveSprite", "func", "allpac2 name minus '_NN_NN' suffix -> table lookup, fallback BG_ERROR"),
    Sym(0x0830D0, "Cmd_STBR", "func", "_STBR(layer 0..39, mode[, ,flag]) -> scene block 0x16500C0+0x54+2*layer"),
    Sym(0x00D690, "Args_Parse", "func", "splits '(a,b,...)' into an argument array"),
    Sym(0x00D8C0, "Args_Has", "func", "(args, i) argument present"),
    Sym(0x00DA50, "Args_Int", "func", "(args, i) argument as integer"),
    # ---- save data
    Sym(0x120110, "SaveData_StartSave", "func", "creates nexl_SaveAuto op"),
    Sym(0x126140, "SaveAuto_Ctor", "func", "dir name '%s%03d' ('savedata', 0); subtitle '%03d: %s'"),
    Sym(0x121030, "SaveData_WriteThread", "func", "Mount2(mode 2/6), SetParam, write '%s/%s' 0x2000 chunks, SaveIcon, UmountWithBackup"),
    Sym(0x121650, "SaveData_LoadThread", "func", "Mount2, CheckBackupData, '%s/%s' path, Umount (read via 0x120CA0)"),
    Sym(0x120CA0, "Save_LoadFile", "func", "'nexl_Save::loadFile': sceKernelStat/Open/Read/Close with progress bar"),
    Sym(0x121B00, "SaveData_RestoreBackup", "func", "sceSaveDataRestoreBackupData"),
    Sym(0x121950, "SaveData_DeleteThread", "func", "sceSaveDataDelete with progress bar"),
    Sym(0x0EA390, "SaveSystem_CreateInitial", "func", "boot state 0: 'savedata'/'bin', Title/subtitle/detail, icon, size 0x400000"),
    Sym(0x0EA5D0, "SaveImage_Clear", "func", "memset(save image, 0, 0x400000)"),
    Sym(0x0C2DE0, "SaveImage_Alloc", "func", "two 'savebuff' allocations (image + shadow)"),
    Sym(0x0C2F60, "SaveImage_Ptr", "func", "returns *(0x1765788)"),
    Sym(0x0EACF0, "SaveImage_WriteBlock", "func", "(slot, src, size): slot<0 -> +0, else +0x1A00+slot*0x7140; marks u16 0x2AF8"),
    Sym(0x0EAC80, "SaveImage_ReadBlock", "func", "(slot, dst, size), returns 2 if empty"),
    Sym(0x0EAC40, "SaveImage_SlotUsed", "func", "u16 at block start != 0"),
    Sym(0x06FEC0, "SaveImage_Commit", "func", "140 slots x 0x7108 from 0xE24600 + system block 0x18AC from 0x1201660"),
    Sym(0x06F470, "SaveImage_LoadSystem", "func", "system block -> RAM: config, G, OR-merges +0x52C/+0x16F4 bit blocks, readmax, latest slot"),
    Sym(0x0737D0, "Slot_Store", "func", "copy staging 0x12031E0 into slot n, stamp date, markers"),
    Sym(0x073980, "Staging_SetSnapshot", "func", "staging+8 = 0x3A10 state, staging+0x3A18 = 0x36A8 second state"),
    Sym(0x073A70, "QuickSave_Store", "func", "staging -> 0x11FA558 record with date"),
    Sym(0x070340, "SaveMenu_Draw", "func", "4 slots per page, date digits from slot+0x70C0.."),
    Sym(0x0EAD50, "Rtc_LocalTime", "func", "sceRtcGetCurrentClockLocalTime -> u16 year, u8 mon/day/h/m/s"),
    # ---- input
    Sym(0x11E7A0, "PadMgr_Update", "func", "scePadReadState -> internal button bits, sticks, repeat, vibration"),
    Sym(0x11F330, "PadMgr_Init", "func", "scePadInit"),
    Sym(0x11E3C0, "PadMgr_Open", "func", "scePadOpen + GetControllerInformation"),
    Sym(0x0CF240, "Pointer_Update", "func", "sceMouseRead + touch pad -> cursor clamped to 0..639 x 0..447 (640x448 space)"),
    # ---- audio / movie
    Sym(0x117E40, "Waveout_Init", "func", "AudioOut ports: stereo float 1024 + 8ch float 1024 @48 kHz; 'waveout thread'"),
    Sym(0x10E660, "Hevag_Init", "func", "AudioOut stereo float 256 @48 kHz; 'hevag decode/waveout thread'"),
    Sym(0x10E4A0, "Atrac9_Init", "func", "'at9 event', 'at9 thread' (sceAudiodec)"),
    Sym(0x1025C0, "AvPlayer_AudioThread", "func", "sceAvPlayerGetAudioData -> AudioOut stereo float 256"),
    Sym(0x101C60, "AvPlayer_Stop", "func", "sceAvPlayerStop/Close"),
    # ---- data
    Sym(0x20CAC0, "Script_Step_letter_table", "table", "26 x int32 relative"),
    Sym(0x20D170, "Script_Prefetch_letter_table", "table", "26 x int32 relative"),
    Sym(0x16545E0, "script_buffer", "data", "decoded current script (bss)"),
    Sym(0x16543B0, "script_ptr", "data", "current position in script_buffer"),
    Sym(0x164B1D0, "var_F", "data", "F variables, 1200 x u16"),
    Sym(0x164BB30, "var_Z", "data", "Z variables, 1400 x u16"),
    Sym(0x164C620, "var_G", "data", "G variables, 2000 x u16 (first 500 persisted in system data)"),
    Sym(0x16500C0, "scene_state", "data", "0x1A08-byte scene/layer state block (saved in each history record at +0x58)"),
    Sym(0x0DE2650, "layers", "data", "40 layer objects x 0x2C8"),
    Sym(0x120A6E0, "history_ptr", "data", "-> 800 history records x 0x3A10 (rewind / previous scene)"),
    Sym(0x0E24600, "slots", "data", "140 save slot records x 0x7108"),
    Sym(0x12031E0, "slot_staging", "data", "slot record being prepared"),
    Sym(0x1201660, "system_staging", "data", "system block (0x18AC) being prepared"),
    Sym(0x066C578, "config", "data", "0x140-byte config block (language at +0x24)"),
    Sym(0x0671900, "image_info", "table", "14,775 x 0x48-byte slots (14,744 used, record k = allpac2 entry k); layout in images.md 8 / archives_ui_movies.md 4"),
    Sym(0x041D5F0, "ui_text", "table", "RELRO char*[5][1499]: ja/en/zc/zt/ko UI strings, id 0 = 'NULL' placeholder (eboot.uitext)"),
    Sym(0x1653EA0, "prefetch_list", "data", "i32[100] archive entries (slot-10)<<16|index; count 0x1654030, bytes 0x1654034"),
    Sym(0x07F6800, "names_allpac2", "table", "14744 x 32-byte NAM records = allpac2.nam base names (no extension), without DUMMY.MRG"),
    Sym(0x07A8B10, "names_sprites", "table", "9958 x 32-byte NAM records (subset of allpac2 names)"),
    Sym(0x07A8820, "names_bgm", "table", "22 x 32-byte: LLBGM01..LLBGM19, LL_INS, LL_OP, LL_ED"),
    Sym(0x0869B30, "names_masks", "table", "33 x 32-byte: DEFAULT, MASK001..MASK099"),
    Sym(0x0DB84F0, "input_action_masks", "table", "3 schemes x 27 u64 action masks; 0xDB8778 -> active scheme"),
    Sym(0x0DCA6E0, "shader_blobs", "table", "67 PSSL .sb programs (see eboot.shaders)"),
]


def _main(argv: List[str]) -> int:
    import argparse

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--tsv")
    a = ap.parse_args(argv)
    lines = [f"{s.va:08x}\t{s.kind}\t{s.name}\t{s.evidence}" for s in sorted(SYMBOLS)]
    if a.tsv:
        with open(a.tsv, "w", encoding="utf-8", newline="\n") as f:
            f.write("va\tkind\tname\tevidence\n" + "\n".join(lines) + "\n")
    else:
        print("\n".join(lines))
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
