# Engine: eboot.bin as the black box to replace

Scope: what the PS4 executable of TSUKIHIME -A piece of blue glass moon- (CUSA47999, app 01.01)
does, as far as a native PC runtime has to reproduce it: the platform API surface, the
rendering model, the script VM, the save image, the system flow, input and timing. The data
formats the engine reads are documented elsewhere (`containers.md` and the image/script/text/
audio documents); this file gives the engine-side view and the addresses to cross-check them.

All addresses are **text/data VAs of eboot.bin** as loaded at image base 0 (the shadPS4 patcher
uses base 0x400000; shadPS4 itself loaded the module at 0x5340000). Text VA = file offset - 0x2280.
Data VA = file offset - 0x365FA0 + 0x600000. All integers little-endian.

Confidence labels:

- **[E]** read directly from the game's code (VA given).
- **[V-n]** checked against the retail data on n items (or "all").
- **[L]** seen in the shadPS4 log of a real run (`%APPDATA%/shadPS4/log/shad_log.txt`).
- **[H]** hypothesis / interpretation; not proven.

Tools (all read-only, `PYTHONPATH=tools`):

| module | what it does |
|---|---|
| `eboot.selfelf` | SELF + ELF64 parser, VA<->file mapping, SCE dynamic table, imports, PLT/GOT |
| `eboot.nids` + `eboot/nid_db.tsv` | NID hash, verified NID->name table (337/337 imports) |
| `eboot.imports` | import surface as JSON/Markdown with call sites (`samples/engine/imports.*`) |
| `eboot.disasm` | capstone index: eh_frame functions, calls, RIP refs, relocated pointers; `dis/func/xref/strref/funcmap` CLI |
| `eboot.strings` | every C string with VA |
| `eboot.scriptvm` | recovers both script-interpreter command tables by vectorised concrete evaluation of the dispatch code over every 4-character name |
| `eboot.shaders` | finds the 67 embedded PSSL shader binaries, maps them to shader classes, GCN census |
| `eboot.savedata` | parser for the 4 MiB save image (layout from code) |
| `eboot.uitext` | exports the 5 x 1,499 UI string table (`ui_text`, RELRO 0x41D5F0) |
| `eboot.symbols` | 120 curated names (97 functions, 23 data/tables) used in this document |

Samples (`samples/engine/`): `imports.json/md`, `vm_commands.json/txt`,
`shaders.json` + `shaders/*.sb` (67 blobs, 49,104 bytes), `funcmap.tsv` (7,482 functions),
`symbols.tsv`, `ui_text_{ja,en,zc,zt,ko}.tsv` (id, VA, text), `script_cmd_counts.json`.

Revision note: section 12 lists what changed after the independent review (replay
interpreter table, UI text export, image table, pointer space, shader header, smaller
corrections) and the checks behind each change.

---

## 1. Executable overview

### 1.1 Identity

- Fake-signed SELF, 12,767,802 bytes; embedded ELF at file 0x160; entry VA 0xEF390 [V-all].
- Module name `devXXX` (DT_SCE_MODULE_INFO), original file name
  `F:/Projects/DEVmoonLang/svn_prog/trunk/pgm/moonML/ORBIS/Release/devXXX.elf` (DT_SCE_ORIGINAL_FILENAME) [V].
- The engine is Hunex's in-house library: allocator tags `nexl_*` (nexl_Graphics, nexl_PadMgr,
  nexl_Scheduler, nexl_LoadMgr, nexl_Sound, nexl_SaveData, nexl_MsgDialog, nexl_Trophy,
  nexl_ShaderMgr, ...) and the string "Hunex Consumer Library" (VA 0x2DA510) [V].
- SDK: param.sfo SDK 0x05050000, eboot SDK 0x05050001; Gnmx is statically linked (VA ~0x13B000..0x172000) [L][E].

### 1.2 SELF segment table (worked example)

```
00000000: 4f15 3d1d 0001 0112 0101 0000 b004 7004   magic 4F153D1D, header 0x4B0, meta 0x470
00000010: a8cc c200 0000 0000 0a00 2200 0000 0000   file size 0xC2CCA8, 10 segments, flags 0x22
00000020: 0400 1100 0000 0000 2009 0000 0000 0000   seg0 flags 0x00110004 (phdr 1 info) @0x920
...
00000040: 0428 0000 0000 0000 8022 0000 0000 0000   seg1 flags 0x00002804 (phdr 0, blocked) @0x2280
00000050: c89d 3200 0000 0000 c89d 3200 0000 0000         size 0x329DC8 / 0x329DC8 (not compressed)
```

Segment entry = 4 x u64: flags (bits 20..31 = ELF program header index, 0x800 = blocked/data,
0x8 = deflated, 0x2 = encrypted), file offset, stored size, plain size. All five data segments
of this eboot are plain (no deflate, no encryption) [V-all].

| SELF seg | phdr | type | ELF offset | VA | filesz | memsz | SELF file offset |
|---:|---:|---|---|---|---|---|---|
| 1 | 0 | LOAD R-X (text + rodata) | 0x4000 | 0x0 | 0x329DC8 | 0x329DC8 | 0x2280 |
| 3 | 1 | SCE_RELRO R-- (GOT, vtables) | 0x330000 | 0x400000 | 0x35ED0 | 0x35ED0 | 0x32C210 |
| 5 | 2 | LOAD RW- (data + bss) | 0x368000 | 0x600000 | 0x7D66E0 | 0x1A85790 | 0x365FA0 |
| 7 | 8 | SCE_DYNLIBDATA | 0xB3E6E0 | - | 0xEFE28 | - | 0xB3CE00 |
| 9 | 9 | SCE_COMMENT | 0xC2E510 | - | 0x58 | - | 0xC2CC50 |

PT_DYNAMIC (phdr 4, ELF 0xC2DC68, 0x8A0 bytes) lies inside DYNLIBDATA; PT_SCE_PROCPARAM is the
first 0x60 bytes of the data segment; PT_GNU_EH_FRAME is VA 0x31A974 (inside text). Map an ELF
file offset to the SELF file by finding the program header that contains it and adding the
offset of the SELF segment whose phdr id equals that header's index.

### 1.3 Address-space map (measured)

| VA range | content |
|---|---|
| 0x000000..0x000015 | PT_INTERP string `/libexec/ld-elf.so.1` |
| 0x000080..0x1734A0 | code: 7,482 functions listed by .eh_frame_hdr (first 0x80, last FDE 0x1733F0; DT_INIT 0x20, DT_FINI 0x173450) [V-all] |
| 0x1734A0..0x174980 | PLT: 334 stubs of 16 bytes, `FF 25 rel32` = `jmp [rip+GOT]` + `CC` padding [V-all] |
| 0x174000..0x2D34CC | read-only game tables: sprite/CG/BGM name lists (e.g. `IMG4160.MZP IMG4161.MZP` lists, `ARU_A_33_00_N00` sprite tables), switch jump tables, flowchart/gallery tables, C++ class names (cFlowchartScene, cGalleryGuide, cGalleryMusicList, cGalleryMusicPanel) |
| 0x2D34CC..0x2E0700 | engine strings (archive names, movie paths, nexl tags, shader names, error text) |
| 0x31A974 | .eh_frame_hdr (binary search table, 7,482 FDEs) |
| 0x400000..0x435ED0 | RELRO: vtables, GOT (DT_SCE_PLTGOT 0x435448), `ui_text` pointer table 0x41D5F0..0x42C028 (9) |
| 0x600000..0xDD66E0 | data (file backed): image table, embedded NAM tables, UI strings (0xD63F38..0xDB72F4), shader binaries, effect tables |
| 0xDD66E0..0x2085790 | bss: script buffer, variables, layer state, history ring pointer, save image pointers |

---

## 2. Platform API surface

### 2.1 Dynamic linking data (algorithm)

```
dyn       = PT_DYNAMIC entries (16 bytes: s64 tag, u64 value), stop at tag 0
dl        = file offset of PT_SCE_DYNLIBDATA
strtab    = dl + DT_SCE_STRTAB(0x61000035)      symtab = dl + DT_SCE_SYMTAB(0x61000039)
nsyms     = DT_SCE_SYMTABSZ(0x6100003F) / 24    (Elf64_Sym)
jmprel    = dl + DT_SCE_JMPREL(0x61000029), DT_SCE_PLTRELSZ(0x6100002D)/24 Elf64_Rela, type 7
rela      = dl + DT_SCE_RELA(0x6100002F), DT_SCE_RELASZ(0x61000031)/24
module    : DT_SCE_NEEDED_MODULE(0x6100000F) value = name_off:32 | minor:8 | major:8 | id:16
library   : DT_SCE_IMPORT_LIB(0x61000015)    value = name_off:32 | version:16 | id:16
symbol    : name "NID#L#M": 11-char NID, library id L and module id M in the alphabet
            "A..Za..z0..9+-" (base 64, most significant digit first)
NID(name) = base64( reverse( SHA1(name || 51 8D 64 A6 35 DE D8 C1 E6 B0 39 B1 C3 E5 52 30)[0:8] ) )[0:11],
            with '/' -> '-'
```

Worked example (`sceVideoOutSetFlipRate`):

```
symtab[220] @file 0xB3FBC8: 9d110000 12 00 0000 0000000000000000 0000000000000000   (FUNC GLOBAL, undefined)
strtab+0x119D @file 0xB3DFB5: "CBiu4mCE1DA#U#U"           ('U' = 20: library 20 / module 20 = libSceVideoOut)
jmprel @file 0xB40F60: 30574300 00000000 | 07000000 dc000000 | 0  -> GOT slot VA 0x435730, sym 220, JUMP_SLOT
PLT stub VA 0x173A40: ff 25 ea 1c 2c 00  = jmp [rip+0x2C1CEA] = jmp [0x435730]
nid("sceVideoOutSetFlipRate") = "CBiu4mCE1DA"
```

Counts [V-all]: 339 dynamic symbols (all undefined imports), 334 JUMP_SLOT relocations (all with
a PLT stub), 39,836 RELA entries = 39,459 RELATIVE + 377 R_X86_64_64. The 377 R_X86_64_64
entries bind six symbols: `__cxa_pure_virtual` 371 (pure-virtual slots of vtables), `Need_sceLibc`
2, `_ZSt7nothrow` 1, `vprintf` 1, `memcpy` 1 (all libc, library id 'I') and `__stack_chk_guard` 1
(libkernel, id 'H'); there is no `malloc`/`free` relocation. 337 distinct NIDs; 27 needed
modules and 27 `DT_SCE_IMPORT_LIB` entries, of which 26 libraries have imported symbols
(`libSceNpCommon`, id 25 = 'Z', is declared but nothing is imported from it).
**All 337 NIDs are resolved** (names verified by hash; candidate names came from SDK naming
knowledge and the function list of shadPS4's `gnmdriver.cpp`; one, `sincosf`, from its
call-site signature).

### 2.2 Libraries and the runtime feature each maps to

| library (module) | # | used for | functions (call sites) |
|---|---:|---|---|
| libSceAudioOut | 4 | audio output ports (48 kHz float stereo + 8 ch) | `sceAudioOutClose`(6), `sceAudioOutInit`(1), `sceAudioOutOpen`(4), `sceAudioOutOutput`(18) |
| libSceAudiodec | 6 | audio decode (ATRAC9 for bgm/vse/voice RIFF data) | `sceAudiodecClearContext`(1), `sceAudiodecCreateDecoder`(1), `sceAudiodecDecode`(1), `sceAudiodecDeleteDecoder`(2), `sceAudiodecInitLibrary`(1), `sceAudiodecTermLibrary`(2) |
| libSceAudiodecCpu | 3 | audio decode on CPU (HEVAG for vag.mrg 'VAGp' entries) | `sceAudiodecCpuDecode`(1), `sceAudiodecCpuInitDecoder`(1), `sceAudiodecCpuQueryMemSize`(1) |
| libSceAvPlayer | 17 | movie playback (.mp4: H.264 video + AAC audio) | `sceAvPlayerAddSource`(1), `sceAvPlayerClose`(2), `sceAvPlayerCurrentTime`(4), `sceAvPlayerEnableStream`(1), `sceAvPlayerGetAudioData`(1), `sceAvPlayerGetStreamInfo`(1), `sceAvPlayerGetVideoData`(2), `sceAvPlayerInit`(1), `sceAvPlayerIsActive`(9), `sceAvPlayerJumpToTime`(4), `sceAvPlayerPause`(1), `sceAvPlayerPostInit`(1), `sceAvPlayerResume`(1), `sceAvPlayerSetLooping`(3), `sceAvPlayerStart`(2), `sceAvPlayerStop`(3), `sceAvPlayerStreamCount`(1) |
| libSceCommonDialog | 1 | common dialog init (needed by Msg/SaveData dialogs) | `sceCommonDialogInitialize`(1) |
| libSceFios2 | 11 | file I/O (open, pread, async ops) for archives | `sceFiosFHCloseSync`(1), `sceFiosFHGetSize`(1), `sceFiosFHOpenSync`(1), `sceFiosFHPread`(1), `sceFiosFHPreadSync`(1), `sceFiosInitialize`(1), `sceFiosOpCancel`(3), `sceFiosOpDelete`(3), `sceFiosOpIsDone`(3), `sceFiosOpWait`(3), `sceFiosTerminate`(1) |
| libSceGameLiveStreaming | 3 | live streaming enable/disable | `sceGameLiveStreamingEnableLiveStreaming`(1), `sceGameLiveStreamingInitialize`(1), `sceGameLiveStreamingTerminate`(1) |
| libSceGnmDriver | 97 | GPU: command buffer submit+flip, shader stage setup, draw calls | see samples/engine/imports.md (97 names) |
| libSceMouse | 3 | mouse input | `sceMouseInit`(1), `sceMouseOpen`(1), `sceMouseRead`(1) |
| libSceMsgDialog | 6 | system message dialogs (yes/no, errors) | `sceMsgDialogClose`(1), `sceMsgDialogGetResult`(1), `sceMsgDialogInitialize`(1), `sceMsgDialogOpen`(1), `sceMsgDialogTerminate`(1), `sceMsgDialogUpdateStatus`(1) |
| libSceNpManager | 1 | PSN title id registration | `sceNpSetNpTitleId`(1) |
| libSceNpTrophy | 9 | trophies (unlock, query) | `sceNpTrophyCreateContext`(1), `sceNpTrophyCreateHandle`(1), `sceNpTrophyDestroyContext`(3), `sceNpTrophyDestroyHandle`(3), `sceNpTrophyGetGameInfo`(2), `sceNpTrophyGetTrophyInfo`(1), `sceNpTrophyGetTrophyUnlockState`(2), `sceNpTrophyRegisterContext`(1), `sceNpTrophyUnlockTrophy`(1) |
| libScePad | 8 | DualShock 4 input, vibration, light bar | `scePadClose`(3), `scePadGetControllerInformation`(2), `scePadInit`(1), `scePadOpen`(1), `scePadReadState`(1), `scePadResetLightBar`(1), `scePadSetLightBar`(1), `scePadSetVibration`(1) |
| libScePlayGo | 5 | PlayGo install-chunk locus (download-in-progress checks) | `scePlayGoClose`(1), `scePlayGoGetLocus`(5), `scePlayGoInitialize`(1), `scePlayGoOpen`(5), `scePlayGoTerminate`(1) |
| libSceRtc | 1 | local wall-clock time (save timestamps) | `sceRtcGetCurrentClockLocalTime`(4) |
| libSceSaveData | 11 | save data mount/umount, backup, icon, param.sfo fields | `sceSaveDataCheckBackupData`(1), `sceSaveDataDelete`(3), `sceSaveDataDirNameSearch`(1), `sceSaveDataInitialize3`(1), `sceSaveDataMount2`(3), `sceSaveDataRestoreBackupData`(3), `sceSaveDataSaveIcon`(1), `sceSaveDataSetParam`(1), `sceSaveDataTerminate`(1), `sceSaveDataUmount`(2), `sceSaveDataUmountWithBackup`(1) |
| libSceSaveDataDialog | 8 | system save-data dialogs (create/overwrite/corrupt/no space) | `sceSaveDataDialogClose`(6), `sceSaveDataDialogGetResult`(1), `sceSaveDataDialogInitialize`(1), `sceSaveDataDialogIsReadyToDisplay`(5), `sceSaveDataDialogOpen`(26), `sceSaveDataDialogProgressBarSetValue`(4), `sceSaveDataDialogTerminate`(1), `sceSaveDataDialogUpdateStatus`(1) |
| libSceScreenShot | 3 | screenshot enable/disable (spoiler protection) | `sceScreenShotDisable`(2), `sceScreenShotEnable`(1), `sceScreenShotSetOverlayImageWithOrigin`(1) |
| libSceSharePlay | 3 | Share Play prohibition | `sceSharePlayInitialize`(1), `sceSharePlaySetProhibition`(1), `sceSharePlayTerminate`(1) |
| libSceSysmodule | 2 | load system modules (audiodec, avplayer, dialogs, ...) | `sceSysmoduleLoadModule`(14), `sceSysmoduleUnloadModule`(3) |
| libSceSystemService | 6 | system params (LANG = 1, ENTER_BUTTON_ASSIGN = 1000, string 6), splash, BGM player | `sceSystemServiceDisableMusicPlayer`(1), `sceSystemServiceGetStatus`(1), `sceSystemServiceHideSplashScreen`(2), `sceSystemServiceParamGetInt`(10), `sceSystemServiceParamGetString`(1), `sceSystemServiceReenableMusicPlayer`(1) |
| libSceUserService | 5 | user login / initial user / user color | `sceUserServiceGetEvent`(2), `sceUserServiceGetInitialUser`(2), `sceUserServiceGetUserColor`(1), `sceUserServiceInitialize`(1), `sceUserServiceTerminate`(2) |
| libSceVideoOut | 7 | video output: 1920x1080 scan-out buffers, flip rate, vblank wait | `sceVideoOutClose`(3), `sceVideoOutGetVblankStatus`(1), `sceVideoOutOpen`(1), `sceVideoOutRegisterBuffers`(1), `sceVideoOutSetBufferAttribute`(1), `sceVideoOutSetFlipRate`(1), `sceVideoOutWaitVblank`(1) |
| libSceVideoRecording | 1 | video recording metadata / prohibition | `sceVideoRecordingSetInfo`(1) |
| libc | 68 | C/C++ runtime (malloc, string, math, printf, new/delete) | see samples/engine/imports.md (68 names) |
| libkernel | 48 | threads, mutexes, semaphores, event flags/queues, memory, files, time | `__stack_chk_fail`(810), `__stack_chk_guard`, `sceKernelAllocateDirectMemory`(2), `sceKernelCancelEventFlag`(1), `sceKernelClose`(3), `sceKernelCreateEqueue`(1), `sceKernelCreateEventFlag`(1), `sceKernelCreateSema`(1), `sceKernelDeleteEventFlag`(3), `sceKernelDeleteSema`(3), `sceKernelGetDirectMemorySize`(2), `sceKernelGetProcessTime`(4), `sceKernelGetProcessTimeCounter`(3), `sceKernelGetProcessTimeCounterFrequency`(1), `sceKernelIsNeoMode`(4), `sceKernelMapDirectMemory`(2), `sceKernelMunmap`(2), `sceKernelOpen`(2), `sceKernelRead`(1), `sceKernelReleaseDirectMemory`(2), `sceKernelSetEventFlag`(1), `sceKernelSignalSema`(1), `sceKernelStat`(1), `sceKernelUsleep`(16), `sceKernelWaitEqueue`(1), `sceKernelWaitEventFlag`(1), `sceKernelWaitSema`(1), `sceKernelWrite`(1), `scePthreadAttrDestroy`(2), `scePthreadAttrInit`(1), `scePthreadAttrSetaffinity`(1), `scePthreadAttrSetdetachstate`(1), `scePthreadAttrSetguardsize`(1), `scePthreadAttrSetinheritsched`(1), `scePthreadAttrSetschedparam`(1), `scePthreadAttrSetschedpolicy`(1), `scePthreadAttrSetstacksize`(1), `scePthreadCancel`(3), `scePthreadCreate`(1), `scePthreadExit`(1), `scePthreadJoin`(1), `scePthreadMutexDestroy`(3), `scePthreadMutexInit`(1), `scePthreadMutexLock`(1), `scePthreadMutexTrylock`(1), `scePthreadMutexUnlock`(1), `scePthreadSetaffinity`(1), `scePthreadSetprio`(1) |

GnmDriver and libc names: 

GnmDriver: sceGnmAddEqEvent, sceGnmAreSubmitsAllowed, sceGnmBeginWorkload, sceGnmComputeWaitOnAddress, sceGnmComputeWaitSemaphore, sceGnmCreateWorkloadStream, sceGnmDebugHardwareStatus, sceGnmDeleteEqEvent, sceGnmDestroyWorkloadStream, sceGnmDingDong, sceGnmDingDongForWorkload, sceGnmDisableMipStatsReport, sceGnmDispatchDirect, sceGnmDispatchIndirect, sceGnmDispatchIndirectOnMec, sceGnmDispatchInitDefaultHardwareState, sceGnmDrawIndex, sceGnmDrawIndexAuto, sceGnmDrawIndexIndirect, sceGnmDrawIndexIndirectCountMulti, sceGnmDrawIndexMultiInstanced, sceGnmDrawIndexOffset, sceGnmDrawIndirect, sceGnmDrawIndirectCountMulti, sceGnmDrawInitDefaultHardwareState350, sceGnmDrawInitToDefaultContextState400, sceGnmDrawOpaqueAuto, sceGnmDriverCaptureInProgress, sceGnmDriverTraceInProgress, sceGnmDriverTriggerCapture, sceGnmEndWorkload, sceGnmFlushGarlic, sceGnmGetEqEventType, sceGnmGetEqTimeStamp, sceGnmGetGpuBlockStatus, sceGnmGetGpuCoreClockFrequency, sceGnmGetGpuInfoStatus, sceGnmGetLastWaitedAddress, sceGnmGetNumTcaUnits, sceGnmGetOffChipTessellationBufferSize, sceGnmGetPhysicalCounterFromVirtualized, sceGnmGetProtectionFaultTimeStamp, sceGnmGetShaderProgramBaseAddress, sceGnmGetShaderStatus, sceGnmGetTheTessellationFactorRingBufferBaseAddress, sceGnmInsertDingDongMarker, sceGnmInsertPopMarker, sceGnmInsertPushColorMarker, sceGnmInsertPushMarker, sceGnmInsertSetColorMarker, sceGnmInsertSetMarker, sceGnmInsertWaitFlipDone, sceGnmIsUserPaEnabled, sceGnmLogicalCuIndexToPhysicalCuIndex, sceGnmLogicalCuMaskToPhysicalCuMask, sceGnmLogicalTcaUnitToPhysical, sceGnmMapComputeQueue, sceGnmMapComputeQueueWithPriority, sceGnmRequestFlipAndSubmitDone, sceGnmRequestFlipAndSubmitDoneForWorkload, sceGnmRequestMipStatsReportAndReset, sceGnmResetVgtControl, sceGnmSetCsShaderWithModifier, sceGnmSetEmbeddedPsShader, sceGnmSetEmbeddedVsShader, sceGnmSetEsShader, sceGnmSetGsRingSizes, sceGnmSetGsShader, sceGnmSetHsShader, sceGnmSetLsShader, sceGnmSetPsShader350, sceGnmSetVgtControl, sceGnmSetVsShader, sceGnmSetWaveLimitMultipliers, sceGnmSetupMipStatsReport, sceGnmSubmitAndFlipCommandBuffers, sceGnmSubmitAndFlipCommandBuffersForWorkload, sceGnmSubmitCommandBuffers, sceGnmSubmitCommandBuffersForWorkload, sceGnmSubmitDone, sceGnmUnmapComputeQueue, sceGnmUpdateGsShader, sceGnmUpdateHsShader, sceGnmUpdatePsShader350, sceGnmUpdateVsShader, sceGnmValidateDisableDiagnostics, sceGnmValidateDisableDiagnostics2, sceGnmValidateDispatchCommandBuffers, sceGnmValidateDrawCommandBuffers, sceGnmValidateGetDiagnosticInfo, sceGnmValidateGetDiagnostics, sceGnmValidateGetVersion, sceGnmValidateOnSubmitEnabled, sceGnmValidateResetState, sceGnmValidationRegisterMemoryCheckCallback, sceRazorCaptureImmediate, sceRazorIsLoaded

libc: Need_sceLibc, _Getptolower, _Getptoupper, _ZSt11_Xbad_allocv, _ZSt14_Xlength_errorPKc, _ZSt14_Xout_of_rangePKc, _ZSt7nothrow, _ZdaPv, _ZdlPv, _Znam, _Znwm, _ZnwmRKSt9nothrow_t, __cxa_atexit, __cxa_guard_acquire, __cxa_guard_release, __cxa_pure_virtual, _init_env, acosf, asinf, atan2f, atexit, bcmp, catchReturnFromMain, cos, cosf, difftime, exit, exp2, exp2f, fmodf, free, ldexp, ldexpf, log2f, malloc, memalign, memcmp, memcpy, memmove, memset, mktime, pow, powf, printf, puts, qsort, rand, sin, sincosf, sinf, snprintf, sprintf, srand, strcat, strchr, strcmp, strcpy, strlen, strncat, strncmp, strncpy, strpbrk, strrchr, strtof, strtol, tanf, vprintf, vsnprintf

Notes:

- Most of the 97 GnmDriver imports are thin wrappers inside the statically linked Gnmx library
  (VA 0x13B000..0x172000) that the game never calls; the engine's own path is
  `sceGnmSubmitAndFlipCommandBuffers` (wrapper 0x154310), `sceGnmSubmitDone` (0x154520),
  `sceGnmSetVsShader`/`sceGnmSetPsShader350`, `sceGnmDrawIndex*`, `sceGnmInsertWaitFlipDone` [E].
  A PC port replaces the whole GNM layer with its own 2D renderer (section 4).
- System modules loaded at start with `sceSysmoduleLoadModule` (14 calls in 0x129B00) [L][E]:
  0x88 Audiodec, 0xBD AudiodecCpu, 0xE1 AudiodecCpuHevag, 0xA5 AvPlayer, 0x8A JpegDec,
  0xA4 MsgDialog, 0xA0 SaveDataDialog, 0xAD NpTrophy, 0xA9 Mouse, 0x9C ScreenShot,
  0x85 VideoRecording, 0x81 GameLiveStreaming, 0xD3 SharePlay, 0x83 PlayGo.
  **libSceJpegDec is loaded but no JpegDec function is imported** [V]: no JPEG decoding is needed.
- Bundled PRX: libc.prx, libSceFios2.prx (file I/O), libSceNpToolkit2.prx (no NpToolkit import by
  the eboot; loaded as a dependency only) [V].
- Capture policy (0xEE550) [E]: in normal play screenshots are enabled with the overlay image
  `/app0/image/corp.png` (copyright watermark), live streaming/share play/video recording allowed;
  in prohibited sections all four are disabled. A PC port may ignore this.

### 2.3 Feature checklist derived from the imports

| feature | PS4 API | PC replacement |
|---|---|---|
| display 1920x1080 sRGB, vsync 60 Hz | VideoOut + GnmDriver | swapchain + 2D renderer (any API) |
| movies (134 .mp4: H.264 1920x1080 yuv420p at 30 or 29.97 fps; 43 with AAC 48 kHz stereo) | AvPlayer | FFmpeg/libav or OS media framework; output NV12 -> YCbCr shader |
| ATRAC9 (bgm/vse/voice RIFF) | Audiodec | FFmpeg `atrac9` decoder (verified by the audio work: ffprobe decodes the voice WAVs) |
| HEVAG (vag.mrg `VAGp`) | AudiodecCpu (+Hevag module) | own HE-VAG decoder (see audio.md / `tsuki.audio_hevag`); FFmpeg's `adpcm_psx` handles plain VAG only |
| audio out (48 kHz float, stereo + 8 ch) | AudioOut | WASAPI/SDL/miniaudio, 48 kHz float mixer |
| pad, vibration, light bar | Pad | SDL GameController / XInput |
| mouse / touch pad pointer | Mouse, Pad touch | OS mouse |
| save data (one 4 MiB file + icon + 3 text params) | SaveData, SaveDataDialog | plain file in the user profile; own dialogs |
| system dialogs | MsgDialog, CommonDialog | own UI |
| trophies | NpTrophy | optional achievements layer or no-op |
| language / enter button | SystemService ParamGetInt(1 = LANG, 1000 = ENTER_BUTTON_ASSIGN) | config / OS locale |
| clock for save stamps | Rtc | OS local time |
| install chunks | PlayGo | always "installed" |
| file I/O | Fios2 (`sceFiosFHOpenSync/PreadSync/Pread` + op wait/cancel) + kernel open/read | memory-mapped / positional reads |
| threads | scePthread*, semaphores, event flags, equeue | std::thread etc. |

---

## 3. Process model, threads and timing

### 3.1 Start-up [E]

```
_start (0xEF390) -> main (0xED210):
    SystemService_Init (0x129650): CommonDialog init, LANG / system name, sysmodule loads
    sceSaveDataInitialize3, sceSaveDataDialogInitialize
    scePlayGoInitialize(buf 0x200000), sceSharePlayInitialize, sceGameLiveStreamingInitialize
    Scheduler_Init(sched, 5, {.., 0x8000})                   (0x126A60, 'Scheduler sema', 'nexl_TaskThread';
                                                              5 = task slots, 0x8000 = stack [H])
    Scheduler_AddTask(slot 0, "task0",     0xED1B0)
    Scheduler_AddTask(slot 1, "task_game", 0xEEE50)
    Scheduler_Run(callback 0xED1F0)                          (0x12A8F0) -- returns at shutdown
    terminate SaveData, SaveDataDialog, PlayGo, SharePlay, GameLiveStreaming
```

`task_game` (0xEEE50) [E][L]: PlayGo open/locus; creates two 1920x1080 display surfaces
(0x780 x 0x438, each with 4 quad corners set by 0xEFC40) and 16 `nexl_Capture` surfaces of
1920x1080; `sceMouseInit`/`sceMouseOpen`; clears the save image (0xEA5D0); hides the splash
screen; then a 5-state boot machine (jump table 0x2D66A4: create/check system save data, wait
for the save thread, ...) and finally the main loop:

```
loop: Game_FrameUpdate (0xEEA90)          -- game processes, ADV, menus
      SaveData_Update  (0x1209C0)         -- pumps the nexl_SaveData operation
      Scheduler_Wait(1)                   -- yield one frame
      0xD42D0                             -- end-of-frame work
```

Game processes are registered by `Process_Register(id, ?, priority, update, draw)` (0xECE30)
from `Game_StartProcesses` (0x67530): ids 0x10 (prio 0x53), 0x0C (0x32), 0x0D (0x46), 3 (0x6E),
4 (0x78), 5 (0x82), 6 (0x8C); id 0 (0xE8280) is registered by `Game_OpenArchives` [E]. The
global game mode word at 0xE20CC8 is 2 while the ADV (novel) mode runs [H, from 0x674C0].

### 3.2 Threads created [E]

| name | creator | priority | stack | affinity |
|---|---|---:|---:|---|
| task0, task_game | Scheduler_AddTask | scheduler | 0x8000 | scheduler |
| waveout thread (x2) | 0x117E40 Waveout_Init | 0x2CB | 0x2000 | 0x3F |
| at9 thread | 0x10E4A0 | 0x2CD | 0x2000 | 0x3F |
| hevag decode thread, hevag waveout thread | 0x10E660 | 0x2CC | 0x2000 | 0x3F |
| av audio | 0x101AD0 | 0x2CA | 0x2000 | 0x3F |
| save thread / load thread / delete thread | nexl_SaveData ops (0x120EE0, 0x123600, 0x124D30, 0x126340, ...) | 0x2FC | 0x1000 | 0x3F |
| troph | 0x12B980 | 0xC8 | 0x4000 | 0x3F |
| stream loader | nexl_LoadMgr [H] | - | - | - |

(PS4 priorities: lower number = higher priority; 0x2FC is near the lowest.)

### 3.3 Frame pacing [E][V]

- `Graphics_Init` (0xF3EB0) and `Graphics_ReopenVideoOut` (0xF4440) call `sceVideoOutSetFlipRate(0)`
  = 60 Hz. `Graphics_SetFlipMode(mode)` (0xF4830) maps mode via the table at 0x2D7E90
  `{-1, 0, 1, 2}`: mode 1 = 60 Hz, 2 = 30 Hz, 3 = 20 Hz [V].
- VideoOut: 3 scan-out buffers 1920x1080, pitch 1920, A8R8G8B8 sRGB, tiling 0 [L]; `eop queue`
  equeue (0xFCCB0) [E].
- `Graphics_BeginFrame` (0xF4D60): waits for GPU labels of the previous frame, measures the frame
  time with `sceKernelGetProcessTimeCounter` and stores `min(1000 / ms, 99)` FPS in a 64-entry ring
  (+0x178), reads `sceVideoOutGetVblankStatus`, increments the frame counter (+0xFA0).
- `Graphics_EndFrame` (0xF4BE0): GPU time in percent of a 16.666668 ms frame, capped at 999
  (constants at 0x2D7E30: 16.666668, 100.0, ..., 999.0, 1000.0, 99.0); submit + flip, `sceGnmSubmitDone`,
  then `sceVideoOutWaitVblank` (0xFCC30).
- There is **no fixed-timestep accumulator**: game logic advances exactly one step per displayed
  frame (Scheduler_Wait counts frames). One `ScriptStep` call processes at most **1000
  segments** (`inc r11d; cmp r11d, 0x3E8; je 0x9B516` at the skip label VA 0x95EB0; every
  `;`-terminated segment that falls back to the loop counts, including segments that are not
  commands) and returns earlier at any yielding command (`exits` column in section 5.3) [E].
  The limit is per call, not per frame: `Adv_Update` (0xA0B70) normally calls ScriptStep once
  per frame (0xA1DAD), but when the dword 0x16541F8 is 1 it sets the word 0x120A6D2 = 1
  (0xA1296) and calls ScriptStep in a loop (0xA12B9..0xA1351, call at 0xA1324) until that
  word is 0 again, all in the same frame. The word is cleared by code in the dispatch function
  (e.g. 0x98428, 0x9AA49, 0x9AA7A, 0x9ABC4, 0x9B08C in the `_FADS` handler range, 0x9B20C in the
  `_SELD` range); 64 instructions in 50 functions read it (mostly sound and wait functions,
  which then skip their output) [E; H: a fast-forward used after a load or jump].
- Script wait times are milliseconds converted to 60 fps frames: `_WTTM(ms)` computes
  `frames = (ms * 3) / 50` (signed, truncated: `lea eax,[r8+r8*2]; imul 0x51EB851F; sar 36` at
  0x9ACF7..0x9AD0F); `_WTSV` (0x9AF6E) and `_WTTR` (0x9B00C) use the same conversion on their first
  argument [E]. The engine therefore counts time in frames of a fixed 60 Hz clock.
- Port rule: run the simulation at exactly 60 steps per second (one step per 16.667 ms,
  decoupled from the monitor refresh) and render the latest state; convert script milliseconds
  with the same integer formula to stay frame-exact [E for the formula, H for the port rule].

---

## 4. Rendering model

### 4.1 Surfaces and layers [E]

- Output: 1920x1080 (all layout constants in the eboot are for 1920x1080, e.g. surface size
  0x780 x 0x438 at 0xEEF09).
- Two `DisplaySurface` objects (1920x1080) created by task_game (stack objects at
  rsp+0x740 / rsp+0x40, pointers in 0x20850B8 / 0x2085140). Each owns a shader table (4.3), a
  blend mode byte (+0x608), transition parameters (+0x5C4 threshold, +0x5DC.., +0x60C progress) and
  a current shader pointer (+0x638) chosen by `DisplaySurface_SelectShader` (0xF0E70).
- 16 `nexl_Capture` surfaces of 1920x1080 (0x20850C0, 8-byte pointer array) used for screen
  captures (transitions from the previous picture, save thumbnails, backlog) [H for the uses].
- 40 sprite **layers** (`layers`, 0xDE2650, 40 x 0x2C8 bytes). The script commands address them by
  index 0..39 (e.g. `_STBR(layer, mode)`, `_STCH(layer, image, x, y, ...)`). Per-frame update
  calls 0x4B330(i) for i = 0..0x27 (0xA13D9..0xA156B) [E]. Known fields [E]:
  +0x18 i32 image id (allpac2 index, -1 if >= 0x59D8), +0x1C u16 blend mode, +0x1F u8,
  +0x20 u16, +0x22 u8, +0x2C f32 (value x constant 0x1A6DC0), +0x30 f32[2], +0x50 f32[2].
- The script-visible state of all layers lives in the 0x1A08-byte `scene_state` block (0x16500C0),
  which is saved in every history record; `_STBR` writes `scene_state+0x54+2*layer` (u16 mode)
  and sets bit 0x80 of `scene_state+0xA4+layer` when its 4th argument is 1 [E].

### 4.2 Shaders: 67 embedded PSSL binaries, 38 classes [V-all]

The eboot carries its own compiled shaders (no shader files on disk). Each is a PSSL `.sb`
program, stored back to back in the data segment (VA 0xDCA6E0..0xDD66B0, 49,104 bytes, 32 VS +
35 PS). Layout:

Binary header (0x24 = 36 bytes, little-endian) [V-67]:

| offset | size | type | value / meaning |
|---:|---:|---|---|
| 0x00 | 4 | bytes | `00 04 FE A5` in all 67 |
| 0x04 | 4 | u32 | shader hash (the value shadPS4 prints as "Compiling fs shader 0x46563f38" etc. [L]) |
| 0x08 | 4 | u32 | 0 |
| 0x0C | 1 | u8 | **stage flag: 1 = PS (35 blobs), 0 = VS (32 blobs)** |
| 0x0D | 1 | u8 | 1 in all 67 |
| 0x0E | 2 | bytes | `00 03` in all 67 |
| 0x10 | 4 | u32 | offset of the `OrbShdr` footer minus 8 |
| 0x14 | 16 | - | zero in all 67 |

(An earlier draft called this a 24-byte header with the stage in byte 13; byte 13 is 1 in every
blob. `eboot.shaders.find_blobs` now raises if byte 0x0C disagrees with the `Shdr` type byte.)

```
blob+0x00  0x24-byte binary header (table above)
blob+0x24  Gnmx shader file header: "Shdr", u16 major 7, u16 minor 2, u8 type (1 VS, 2 PS),
           u8 header size in DW, u8 aux, u8 target GPU modes, u32 0
blob+0x34  Gnmx VsShader/PsShader register block (header size x 4 bytes)
...        GCN machine code, ends with s_endpgm
...        "OrbShdr" ShaderBinaryInfo footer (version 7) and the reflection tables:
           resources, constant buffers + members, inputs/outputs + semantics, string table;
           the last string is the stage output semantic (S_POSITION / S_TARGET_OUTPUT),
           and the blob ends at that string rounded up to 16 bytes [V-67]
```

Worked example, blob #0 (`transition 2DTex` PS, VA 0xDCA6E0):

```
dca6e0: 00 04 fe a5 18 dd 8d c6 00 00 00 00 01 01 00 03  ................
dca6f0: 50 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00  P...............
dca700: 00 00 00 00 53 68 64 72 07 00 02 00 02 13 00 03  ....Shdr........   type 2 (PS), 0x13 DW header
dca710: 00 00 00 00 f4 00 00 03 00 00 00 00 4c 00 00 00  ................
dca760: ff 03 eb be 1a 00 00 00 10 03 fc be 00 0d 86 c2  ................   code: s_mov_b32 ..,0x1a; s_mov_b64; s_load_dwordx8
dca830: 42 03 00 00 da e2 5f 1a 4f 72 62 53 68 64 72 07  B....._.OrbShdr.
```

Shader classes are constructed by `ShaderMgr_CreateBasic` (0x106330) and
`ShaderMgr_CreateBlend` (0x106F60) and registered under a key string; draw code looks them up
by `crc32(key)` (standard reflected CRC-32, function 0xFD070; e.g. `mov esi, 0xE5B1F106` =
crc32("clear") at 0xF42F9) [E][V]:

| key (crc32 lookup) | class tag | ctor VA | VS blob # @VA (bytes) | PS blob # @VA (bytes) |
|---|---|---|---|---|
| `basic VtxCol` (0xFE32A6B4) | nexl_ShaderBasicVC | 0x132A20 | #16 0xDCCED0 (576) | #3 0xDCAE30 (320) |
| `basic VtxColTex` (0x207D254F) | nexl_ShaderBasicVCT | 0x132D30 | #15 0xDCCC30 (672) | #2 0xDCAC40 (496) |
| `basic 2D` (0x4EA69A58) | nexl_ShaderBasic2D | 0x132650 | #12 0xDCC590 (400) | #3 0xDCAE30 (320) |
| `basic 2DTex` (0x04142299) | nexl_ShaderBasic2DTex | 0x132830 | #11 0xDCC3A0 (496) | #2 0xDCAC40 (496) |
| `basic YCbCr` (0x74CCAF64) | nexl_ShaderBasicYCbCr | 0x1330B0 | #11 0xDCC3A0 (496) | #4 0xDCAF70 (704) |
| `transition 2D` (0xB67D2B5E) | nexl_ShaderTransition2D | 0x109AE0 | #14 0xDCC9D0 (608) | #1 0xDCA990 (688) |
| `transition 2DTex` (0xEC46CA76) | nexl_ShaderTransition2DTex | 0x109F10 | #13 0xDCC720 (688) | #0 0xDCA6E0 (688) |
| `sepia 2D` (0x266535DB) | nexl_ShaderSepia2D | 0x108340 | #12 0xDCC590 (400) | #9 0xDCBF40 (640) |
| `sepia 2DTex` (0xCC95B404) | nexl_ShaderSepia2DTex | 0x108770 | #11 0xDCC3A0 (496) | #8 0xDCBC10 (816) |
| `sepia transition 2D` (0x0D878162) | nexl_ShaderSepiaTransition2D | 0x108D40 | #14 0xDCC9D0 (608) | #7 0xDCB810 (1024) |
| `sepia transition 2DTex` (0xC8B79958) | nexl_ShaderSepiaTransition2DTex | 0x109430 | #13 0xDCC720 (688) | #6 0xDCB370 (1184) |
| `capture shader` (0x4F617D2C) | nexl_ShaderCapture | 0x133290 | #11 0xDCC3A0 (496) | #2 0xDCAC40 (496) |
| `dummy` (0x4FF4F23F) | nexl_ShaderDummy | 0x133B20 | #18 0xDCD330 (688) | #10 0xDCC1C0 (480) |
| `add` (0xFD1A73E7) | nexl_Shader_Add | 0x10A290 | #66 0xDD6400 (688) | #64 0xDD5E20 (816) |
| `addalpha` (0x833D9481) | nexl_Shader_AddAlpha | 0x10A4F0 | #65 0xDD6150 (688) | #63 0xDD5AD0 (848) |
| `darken` (0xB1C12D79) | nexl_Shader_Darken | 0x10A750 | #20 0xDCD920 (688) | #19 0xDCD5E0 (832) |
| `dodge` (0x916FCA8E) | nexl_Shader_Dodge | 0x10A9B0 | #42 0xDD2260 (688) | #21 0xDCDBD0 (912) |
| `lighten` (0xCA79C0FD) | nexl_Shader_Lighten | 0x10AC10 | #43 0xDD2510 (688) | #22 0xDCDF60 (832) |
| `multiplicative` (0xA70BB6BD) | nexl_Shader_Multiplicative | 0x10AE70 | #44 0xDD27C0 (688) | #23 0xDCE2A0 (848) |
| `opaque` (0x088B7686) | nexl_Shader_Opaque | 0x10B0D0 | #45 0xDD2A70 (688) | #24 0xDCE5F0 (816) |
| `screen` (0xDF4C6130) | nexl_Shader_Screen | 0x10D6D0 | #61 0xDD5570 (688) | #40 0xDD1BC0 (864) |
| `sub` (0x580282DC) | nexl_Shader_Sub | 0x10D930 | #62 0xDD5820 (688) | #41 0xDD1F20 (832) |
| `ps_add` (0x95A7837C) | nexl_Shader_PS_Add | 0x10B330 | #46 0xDD2D20 (688) | #25 0xDCE920 (848) |
| `ps_burn` (0x32E7EA38) | nexl_Shader_PS_Burn | 0x10B590 | #47 0xDD2FD0 (688) | #26 0xDCEC70 (864) |
| `ps_darken` (0xE1EB548C) | nexl_Shader_PS_Darken | 0x10B7F0 | #48 0xDD3280 (688) | #27 0xDCEFD0 (816) |
| `ps_diff` (0x22C5B097) | nexl_Shader_PS_Diff | 0x10BA50 | #50 0xDD37E0 (688) | #29 0xDCF640 (816) |
| `ps_diff5` (0xEAF8E8F9) | nexl_Shader_PS_Diff5 | 0x10BCB0 | #49 0xDD3530 (688) | #28 0xDCF300 (832) |
| `ps_dodge` (0xBE6703FE) | nexl_Shader_PS_Dodge | 0x10BF10 | #52 0xDD3D40 (688) | #31 0xDCFD00 (848) |
| `ps_dodge5` (0xDE0A93B6) | nexl_Shader_PS_Dodge5 | 0x10C170 | #51 0xDD3A90 (688) | #30 0xDCF970 (912) |
| `ps_exclusion` (0x9BB8D26E) | nexl_Shader_PS_Exclusion | 0x10C3D0 | #53 0xDD3FF0 (688) | #32 0xDD0050 (864) |
| `ps_hardlight` (0xCE100A91) | nexl_Shader_PS_Hardlight | 0x10C630 | #54 0xDD42A0 (688) | #33 0xDD03B0 (976) |
| `ps_lighten` (0x07FEEC17) | nexl_Shader_PS_Lighten | 0x10C890 | #55 0xDD4550 (688) | #34 0xDD0780 (816) |
| `ps_multi` (0xEA998A75) | nexl_Shader_PS_Multi | 0x10CAF0 | #56 0xDD4800 (688) | #35 0xDD0AB0 (816) |
| `ps_overlay` (0x74781054) | nexl_Shader_PS_Overlay | 0x10CD50 | #57 0xDD4AB0 (688) | #36 0xDD0DE0 (928) |
| `ps_screen` (0x8F6618C5) | nexl_Shader_PS_Screen | 0x10CFB0 | #58 0xDD4D60 (688) | #37 0xDD1180 (848) |
| `ps_softlight` (0x65487E49) | nexl_Shader_PS_Softlight | 0x10D210 | #59 0xDD5010 (688) | #38 0xDD14D0 (944) |
| `ps_sub` (0x30BF7247) | nexl_Shader_PS_Sub | 0x10D470 | #60 0xDD52C0 (688) | #39 0xDD1880 (832) |
| `clear` (0xE5B1F106) | nexl_ShaderClear | 0x133470 | #17 0xDCD110 (544) | #5 0xDCB230 (320) |

GCN census (`eboot.shaders.gcn_classes`, encoding-level decode, all 67 blobs end in `s_endpgm`
with no unknown encodings) [V-all]:

- `basic 2D`, `basic VtxCol`, `clear`: no texture; `basic 2DTex`, `basic VtxColTex`, `capture`: 1 sample.
- `basic YCbCr`: 2 samples (Y plane + interleaved CbCr plane) -> movie frames (NV12).
- `transition 2D` PS (#1): 1 sample of the rule mask `transition_tex`/`transition_samp`
  + `TransitionParamsConstants`; `transition 2DTex` PS (#0): 1 sample of the layer image
  `texture`/`sampler` + `TransitionParamsConstants`, no mask resource (the ctor 0x109F10 stores
  VS 0xDCC720 / PS 0xDCA6E0) and its inputs are only m_color/m_texcoord; neither PS lists
  `TextureSizeConstants` (that buffer is in the transition VS #13/#14 and in all blend VS).
  `sepia transition 2DTex`: 2 samples (`texture` + `transition_tex`).
- **All 24 blend shaders sample exactly 2 textures**: the layer and the backdrop (a copy of what is
  already on the surface). Blending is done in the pixel shader, not with fixed-function blend
  state; the backdrop is the `dst_texture` resource (reflection, below) [V-24].
- Reflection data inside the blobs (`ShaderBlob.names`) [V-67]:

  | shaders | resources | constants (members) | interpolants |
  |---|---|---|---|
  | 24 blend PS | `texture`/`sampler`, **`dst_texture`/`dst_sampler`** | `CommonFragmentParamsConstants` (`common_ps_params`) | m_color, m_texcoord, m_transition |
  | blend VS (shared code, hash 0x3DF22771) | - | `TextureSizeConstants` (`texture_size`) | out m_color, m_texcoord, m_transition, m_position |
  | transition 2D PS | `transition_tex`/`transition_samp` | `TransitionParamsConstants` (`transition_params`) | m_color, m_transition |
  | transition 2DTex PS | `texture`/`sampler` | `TransitionParamsConstants` (`transition_params`) | m_color, m_texcoord |
  | sepia 2D / 2DTex PS | (`texture`) | `SepiaConstants` (`sepia_Cb`, `sepia_Cr`, `sepia_blend`) | m_color (, m_texcoord) |
  | sepia transition 2DTex PS | `texture`, `transition_tex` | `TransitionParamsConstants`, `SepiaConstants` | m_color, m_texcoord, m_transition |
  | basic YCbCr PS | `y_texture`, `cbcr_texture` (+ samplers) | - | m_texcoord |
  | basic VtxCol / VtxColTex VS | - | `ShaderConstants` (`m_WorldViewProj`) | m_position, m_color (, m_texcoord) |
  | clear VS | - | `ColorConstants` (`m_color`, `m_depth`) | position |

  So the backdrop of the blend shaders is an explicit second texture (`dst_texture`) sampled at
  the `m_transition` (screen-space) coordinates, and sepia is a Cb/Cr tint with a blend factor.

### 4.3 Blend modes [E][V]

`DisplaySurface_Ctor` (0xEF3F0) fills a shader-pointer table at +0x2D8 (index i at +0x2D8+8i):

| index | shader | index | shader | index | shader |
|---:|---|---:|---|---:|---|
| 0 | basic 2DTex | 12 | add | 24 | ps_diff |
| 1 | basic 2D | 13 | addalpha | 25 | ps_diff5 |
| 2 | transition 2DTex | 14 | darken | 26 | ps_dodge |
| 3 | transition 2D | 15 | dodge | 27 | ps_dodge5 |
| 4 | sepia 2DTex | 16 | lighten | 28 | ps_exclusion |
| 5 | sepia 2D | 17 | multiplicative | 29 | ps_hardlight |
| 6 | sepia transition 2DTex | 18 | opaque | 30 | ps_lighten |
| 7 | sepia transition 2D | 19 | screen | 31 | ps_multi |
| 8..10 | not filled by the constructor | 20 | sub | 32 | ps_overlay |
| 11 | dummy | 21 | ps_add | 33 | ps_screen |
|  |  | 22 | ps_burn | 34 | ps_softlight |
|  |  | 23 | ps_darken | 35 | ps_sub |

Precisely: +0x338 add, +0x340 addalpha, +0x348 darken, +0x350 dodge, +0x358 lighten,
+0x360 multiplicative, +0x368 opaque, +0x370 screen, +0x378 sub, +0x380 ps_add, +0x388 ps_burn,
+0x390 ps_darken, +0x398 ps_diff, +0x3A0 ps_diff5, +0x3A8 ps_dodge, +0x3B0 ps_dodge5,
+0x3B8 ps_exclusion, +0x3C0 ps_hardlight, +0x3C8 ps_lighten, +0x3D0 ps_multi, +0x3D8 ps_overlay,
+0x3E0 ps_screen, +0x3E8 ps_softlight, +0x3F0 ps_sub (index = (offset - 0x2D8) / 8).

The surface **blend-mode byte** (+0x608, set by 0xF0690) selects the shader through the jump table
at 0x2D7CAC (0xF0E70):

| mode | shader | mode | shader | mode | shader |
|---:|---|---:|---|---:|---|
| 0 | normal: basic/transition/sepia chosen by texture + transition state | 9 | screen | 18 | ps_exclusion |
| 1 | add | 10 | sub | 19 | ps_hardlight |
| 2 | addalpha | 11 | ps_add | 20 | ps_lighten |
| 3 | basic 2DTex (normal) | 12 | ps_burn | 21 | ps_multi |
| 4 | darken | 13 | ps_darken | 22 | ps_overlay |
| 5 | dodge | 14 | ps_diff | 23 | ps_screen |
| 6 | lighten | 15 | ps_diff5 | 24 | ps_softlight |
| 7 | multiplicative | 16 | ps_dodge | 25 | ps_sub |
| 8 | opaque | 17 | ps_dodge5 | >25 | index 0 |

The blend-mode byte is only consulted behind a gate that covers **every** mode
(0xF0EB2..0xF0EF6) [E]:

```
b    = min(+0x610 - 1, 1)                       # (value <= 0 is used as is)
flag = bit b, MSB first, of the u32 bit set at +0x614   (dword[b >> 5] << (b & 31)) & 0x80000000
if not flag or source texture (+0x2C8) == 0:    # -> 0xF0F68, blend byte ignored
    b4 = min(+0x610 - 1, 4); f4 = bit b4 of +0x614 (same encoding)
    index = 1 + 2*(mask texture +0x2D0 != 0) + (4 if +0x5C4 > 0.0 and f4 else 0)   # 1/3/5/7
else:
    switch +0x608 via 0x2D7CAC (modes 0..25; > 25 -> 0xF10C0 = index 0):
      mode 0 (0xF0F11): index 4 or 6 for sepia (6 with mask), else 2 when a mask is present,
                        else 0, or 7 + [+0x51C] when +0x51C != 0 (slots 8..10, filled elsewhere)
      modes 1..25: the table above (mode 3 shares the > 25 target, index 0)
```

So +0x2C8 = source texture, +0x2D0 = transition (rule) mask texture [H for the names, E for
the selection logic].

The screen-effect table (`ScreenEffect_Apply` 0xD2BE0; effect id e in the dword 0x1DBD0A0)
uses **two index bases** [E 0xD2D69..0xD2DCB; V-51]:

```
dword = (byte 0x1DBD09C == 0) ? dword_table_0x2D55E0[e] : 0     # indexed by e itself
if 1 <= e <= 51:  mode = mode_table_0x2D5978[e - 1]; strength = float_table_0x2D59AC[e - 1]
else:             mode = 0; strength = 0.0
```

| e | mode | strength | dword |
|---|---|---|---|
| 1..25 | 1, 2, 0, 4, 5, ..., 25 (e=3 -> 0) | 0.0 | 1 |
| 26 | 0 | 0.0 | 6 |
| 27..51 | 1, 2, 0, 4, 5, ..., 25 (e=29 -> 0) | 1.0, except e=29: 0.0 | 1, except e=29: 6 |

Raw tables: modes `[1,2,0,4..25, 0, 1,2,0,4..25]` (51 bytes), strengths 26 x 0.0 then
1.0, 1.0, 0.0, 22 x 1.0, dwords (52 x i32) all 1 except [26] = [29] = 6. The dword is passed
to 0xF0830 (meaning open). Mode 3 never occurs in the effect table.

Script usage: `_STBR(layer, mode)` occurs 14,940 times; mode values 0..25 cover all but one use
(one `28`); the most frequent are 17 (3,473), 11 (3,156), 23 (1,909), 24 (1,388), 22 (1,369),
7 (863), 10 (629), 19 (486) [V-all]. That the `_STBR` mode is the surface blend mode above is
**[H]**: the chain `scene_state+0x54` -> `layer+0x1C` (0x49F90) is [E], the step from the layer
field to the per-surface mode byte was not traced.

Recommended port formulas (standard Photoshop definitions; PS_*5 variants unknown) [H]:
add `s+d`, sub `d-s`, multiplicative/ps_multi `s*d`, screen `1-(1-s)(1-d)`, darken `min`,
lighten `max`, ps_diff `|d-s|`, ps_exclusion `s+d-2sd`, ps_overlay `d<.5 ? 2sd : 1-2(1-s)(1-d)`,
ps_hardlight = overlay with s/d swapped, ps_softlight (W3C), ps_burn `1-(1-d)/s`,
ps_dodge `d/(1-s)`; then lerp by source alpha. Verify against the GCN code before shipping
(next step: full GCN disassembly of the 24 blend PS, 34..62 instructions each).

### 4.4 Transitions and rule masks [E][H]

- `transition 2D` (#1) samples the rule mask `transition_tex` at the screen-space
  `m_transition` coordinate; `transition 2DTex` (#0) samples only the layer `texture` (no mask);
  `sepia transition 2DTex` (#6) samples both. All take `TransitionParamsConstants`
  (`transition_params`), filled from the surface fields +0x5C4 = threshold / vague (0 disables),
  +0x60C = progress, +0x5FC = 1 - progress, +0x604 = 1.0 or 0 depending on the transition kind;
  kinds 5, 7, 9, 10 are special-cased at 0xF147C via the bit mask 0x6A0) [E].
- Rule images are the `RULE_xx` MZPs in allpac3 [V, container inventory]; a 33-name table
  `DEFAULT, MASK001..MASK016, MASK020..022, MASK070, MASK088..099` (0x869B30, 32-byte NAM records)
  is searched case-insensitively by 0xA3530 [E]. Which script argument selects the mask belongs to
  the script-command work.
- `_FADS` yields the frame and `_FADE` may (5.3); they are the main users of the transition path [H].

### 4.5 Text rendering [E]

- Fonts: `Font_Load` (0xC6980) opens `ft_<lang>.mrg` (name table of int32 offsets at 0x2D3560:
  ja, en, zc, zt, ko) into archive slot 3; section 0 is loaded into texture slot 0x21 and the
  remaining sections into slots 0x22.. (font pages). `ft_ko.mrg` is reachable only with language
  index 4, which `Lang_Index` never returns (Korean is not selectable in this build) [E].
- Message layout/rendering: `Text_Layout` (0xC6C20, 30 callers, 1,854 instructions), with the
  inline tags `[heart]`, `[heart2]`, `[star]` (0x2D3580..) and `^` as the line break marker (also
  in the UI tables). Glyph metrics/cmap come from the font archive (font work) [H].
- Config affects text: text speed, auto speed, window opacity, outline on/off, shadow on/off,
  fixed vs. "read" text colour, window design, language (section 8.4).

---

## 5. Script VM

### 5.1 Source and loading [E][V]

- `Script_OpenArchives` (0x788C0): `allscr.mrg` -> slot 0x1F, `script_text.mrg` -> slot 0x20;
  the decoded script buffer is the static `script_buffer` at 0x16545E0 (pointer stored in 0x174D640).
- `History_Restore` (0x7C6D0) loads script number n as allscr **section n+3** (`n < 600 ? n+3 : 3`),
  MZX-decodes it into the buffer and inverts every byte (`not`); sections 0..2 are the label/
  name tables and the nested image [E][V: 549 scripts decode, containers.md].
- Scripts are plain UTF-8 text of the form `_NAME(arg,arg,...);` e.g.

```
_ZZ00a01();_ZZ00b01(START);_WKST(F823,eq,0);_WKST(F847,eq,0);...;_WNTY(0);_CLO2();
_STCH(1,WHITE,240,136,1000,255,2,,CC);_FADS(1,0,0);_WKST(G008,eq,0);...;_JUMP(240,START);
```
  (allscr section 3, first 1,173 bytes after decoding.)

### 5.2 Dispatch (front end shared by both interpreters) [E]

```
ScriptStep(p):                                   # 0x95E60; returns the new script pointer
    n = 0
    loop:                                        # 0x95EC0
        q = p; first_us = None
        repeat at most 0x300 times:              # the window starts at p itself
            c = *q++
            if c == ';': break                   # q is now just after ';'
            if c == '_' and first_us is None: first_us = q     # points after '_'
        else: q = p + 0x300                      # no ';' in the window
        p = q                                    # next segment starts here in any case
        if first_us is None: goto skip
        c0 = first_us[0]; if not 'A' <= c0 <= 'Z': goto skip
        key = first_us[1]<<16 | first_us[2]<<8 | first_us[3]   # any byte values
        args = first_us + 4                      # handlers parse '(' ... themselves
        jump first_letter_table[c0 - 'A']        # 26 x int32 relative offsets at 0x20CAC0
        # each case: compare trees on key (cmp r13d, imm24 / je / jne / jg / jle), masked
        # compares (mov eax, r13d; and eax, ~2; cmp eax, imm), ranges (add r13, -K; cmp r13, N;
        # jbe) and dense switches (jump tables); every name not matched goes to skip
        handler(...)                             # may continue (goto skip) or yield
    skip/continue:                               # 0x95EB0
        n += 1; if n == 1000: return p           # per-call budget, counts every segment
        goto loop
    yield: return p                              # epilogue 0x9B516
```

Worked example: `_WKST(F823,eq,0);` -> c0 = 'W' -> case 0x9651A; key = 'K'<<16|'S'<<8|'T' =
0x4B5354; `cmp r13d, 0x4B5354` at 0x9654F, `jne 0x9A86B` (a no-op trampoline
`mov r11d, [rbp-0x1DC]; jmp 0x95EB0`) -> handler falls through at 0x96566 [E].

Letters with no command in ScriptStep (B D H K L N O Q U Y) jump straight to `skip`, so `_DMMY`
and `_NOP_` are no-ops by construction [E].

**How the tables were recovered** (`eboot.scriptvm.extract`) [V-all names]: the dispatch code
of each interpreter is executed concretely on all 63^3 = 250,047 names c1c2c3 over
`[A-Za-z0-9_]` for each of the 26 letters at once (numpy arrays, one element per name). The
evaluator models only what dispatch code does: register moves/loads of the name bytes,
`lea`/`shl`/`or`/`and`/`add`/`sub`, `cmp`/`test` with signed and unsigned flags, conditional
jumps (which split the name set), `jmp`, and jump tables. The first instruction that does
anything else (memory compare, store, call, ...) is the start of the handler; the handler VA
is the address reached by the last control transfer, after following `jmp` trampolines. A
name is "not a command" when it reaches the skip label directly or through a trampoline that
only loads registers other than the script pointer (`mov r11d, r14d; jmp skip` in ScriptStep;
`vmovups ymm9, [..]; mov ebx, [rsp+0xC]; movabs r10, ..; jmp skip` at 0x9D8CE in the
prefetcher). Handler -> name sets are compressed to patterns (`*` = any character). Over the
reviewers' alphabet `[A-Z0-9_]` the result equals an independent brute-force evaluator name
for name (ScriptStep: 101,416 names, all handler VAs identical; prefetcher: identical except
`Z`, see 5.4) [V]. An earlier syntactic compare-tree walker stopped at the masked compare
0x9F158 and lost 7 prefetcher names; the evaluator decides every name.

`Script_Scan` (0xA4060) calls ScriptStep with the global scan flag 0x174DE48 = 1; many handlers
then return immediately (`cmp byte [0x174DE48],0; jne yield`) [E]. script.md 3.6 ("Scan mode")
documents its use: restoring a save position by running from the scene start up to the target
`_PGST` page.

### 5.3 Command table, ScriptStep (112 entries: 110 names + `J***`, `Z***`) [E][V]

Recovered by `python -m eboot.scriptvm`. `dispatch`: the last decision before the handler
(`eq` = je taken, `ne-fallthrough` = jne not taken, `switch` = jump table, `range` = other
conditional, `masked` = compare after an `and` mask, `wildcard` = every name of the letter).
`exits`: static reachability of the handler: `continue` = returns to the dispatch loop (same
call), `yield` = reaches the epilogue (the call returns; the frame ends), `both` = conditional.
"uses" = occurrences in the 549 decoded allscr scripts counted the way the VM segments the text
(`samples/engine/script_cmd_counts.json`; `_Z***` counts the 46,810 text labels). Handler VAs
are the real code after `jmp` trampolines: `_END_` 0x96031, `_MSAD` 0x98409, `_TOMW` 0x964D8,
`_WTTM` 0x990FC and `_WT__` 0x99A4A are 5-byte `jmp`s to the VAs listed.

| command | handler VA | dispatch | exits | first callees | uses in allscr |
|---|---|---|---|---|---:|
| `_ATSV` | 0x95F5D | ne-fallthrough | both | 0x7E620, 0x7B790, 0xD1CD0 | 398 |
| `_CLO2` | 0x9A887 | switch | yield | 0x75CD0, 0x423F0, 0x42400 | 5 |
| `_CLO3` | 0x9A8B8 | switch | yield | 0x423F0, 0x42420 | 12208 |
| `_CLO4` | 0x9A8E7 | switch | yield | 0xD690, 0xD8C0, 0xDA50 | 978 |
| `_CLO5` | 0x9A939 | switch | yield | 0xD690, 0xD8C0, 0xDA50 | 596 |
| `_EIF_` | 0x96EFC | eq | continue | 0x79D30 | 0 |
| `_ELS_` | 0x96F2A | eq | continue |  | 0 |
| `_END_` | 0x9AB42 | ne-fallthrough | yield | 0x7E620, 0x7F1A0 | 519 |
| `_FADE` | 0x97973 | ne-fallthrough | both | 0x7F7E0 | 61 |
| `_FADS` | 0x9B06D | eq | yield | 0x81520 | 34040 |
| `_FCCL` | 0x982B5 | eq | both | 0xB7A90 | 0 |
| `_FCLN` | 0x96D12 | ne-fallthrough | both | 0xB7900 | 26 |
| `_FCSP` | 0x982A2 | eq | both | 0xB79C0 | 31 |
| `_FLNT` | 0x9606A | ne-fallthrough | both | 0xB7720 | 0 |
| `_FLOP` | 0x982C8 | eq | both | 0xB77B0 | 0 |
| `_FLSP` | 0x9715E | ne-fallthrough | both | 0xB7870 | 0 |
| `_GOTO` | 0x96E3F | eq | both | 0x794D0 | 101 |
| `_GOTS` | 0x9609A | ne-fallthrough | both | 0x76EA0, 0x794D0 | 170 |
| `_IFF_` | 0x97FC8 | eq | continue | 0x76690, 0x76690 | 0 |
| `_IFJP` | 0x9806F | eq | both | 0x794D0 | 0 |
| `_IFLA` | 0x96D4F | ne-fallthrough | both | 0x3ED90, 0x794D0 | 0 |
| `_IFNX` | 0x97F00 | eq | continue | 0x794D0 | 233 |
| `_IFSC` | 0x96185 | ne-fallthrough | both | 0x67430, 0x76EA0, 0x794D0 | 2 |
| `_IFUR` | 0x98161 | eq | both | 0xB7D80, 0x76EA0, 0x794D0 | 1 |
| `_IF__` | 0x9708D | ne-fallthrough | both | 0x76690, 0x76860, 0x76EA0 | 1194 |
| `_J***` | 0x961DC | wildcard | both | 0xD1CD0, 0xBA690, 0xE6DF0 | 1741 |
| `_MFAD` | 0x978F5 | eq | both | 0x87660 | 724 |
| `_MPLY` | 0x96E2C | ne-fallthrough | continue | 0x87250 | 1171 |
| `_MSAD` | 0x9AECE | ne-fallthrough | yield | 0x173650, 0x7AF90, 0x92B60 | 678 |
| `_MSQK` | 0x962D3 | ne-fallthrough | continue | 0xD690, 0xD8C0, 0xDA50 | 30 |
| `_MSTP` | 0x978BD | eq | continue | 0x7AF90, 0x9EE0 | 0 |
| `_MVOL` | 0x978AA | eq | continue | 0x87940 | 780 |
| `_PGST` | 0x9637E | ne-fallthrough | both |  | 17202 |
| `_RDAS` | 0x98118 | eq | continue | 0x13610, 0xB8020 | 530 |
| `_RDED` | 0x9813B | eq | both | 0xB7E40, 0x7E620, 0x7F1A0 | 550 |
| `_RDST` | 0x96DFF | ne-fallthrough | continue | 0xB8110 | 530 |
| `_RTM2` | 0x97FBC | eq | continue |  | 1 |
| `_RTMN` | 0x9643E | ne-fallthrough | both | 0x7E620, 0x7F1A0 | 1 |
| `_RTM_` | 0x97174 | eq | both | 0x7E620, 0x7F1A0 | 545 |
| `_RTST` | 0x97131 | ne-fallthrough | continue | 0xB8220 | 527 |
| `_SACL` | 0x99591 | eq | continue | 0xD690, 0xD8C0, 0xDA50 | 450 |
| `_SBAS` | 0x99609 | eq | continue | 0xD690, 0xD8C0, 0xDA50 | 943 |
| `_SCH2` | 0x98226 | ne-fallthrough | continue | 0xD690, 0x81760 | 4675 |
| `_SEAC` | 0x99846 | eq | continue | 0x8A880 | 0 |
| `_SEFD` | 0x99859 | eq | continue | 0x89E40 | 3516 |
| `_SEFI` | 0x98C8F | eq | continue | 0x893F0 | 0 |
| `_SELA` | 0x9B1B6 | switch | yield | 0xA7FA0, 0xB8400, 0xA7F90 | 26 |
| `_SELD` | 0x9B1DC | switch | yield | 0x7E620, 0x7AF90, 0x73980 | 170 |
| `_SELR` | 0x9936D | switch | continue | 0x76690, 0x7AF90, 0x76EA0 | 824 |
| `_SELS` | 0x9B435 | switch | yield | 0xB82E0 | 1 |
| `_SEP3` | 0x99934 | eq | continue | 0x8A5E0 | 0 |
| `_SEPL` | 0x98C8F | eq | continue | 0x893F0 | 14543 |
| `_SEPN` | 0x996CB | eq | continue | 0x8A340 | 0 |
| `_SESP` | 0x996E1 | eq | continue | 0x76690, 0x7AF90, 0xAAB0 | 18 |
| `_SEVL` | 0x98C07 | ne-fallthrough | continue | 0x89B50 | 1285 |
| `_SGMA` | 0x99568 | eq | continue | 0xC23B0 | 109 |
| `_SHAF` | 0x99581 | eq | continue | 0x80AE0, 0xE7F50 | 397 |
| `_SHAK` | 0x971D5 | ne-fallthrough | both | 0x80180 | 0 |
| `_SHAS` | 0x9980A | eq | continue | 0x433F0, 0xE7F50 | 0 |
| `_SHK2` | 0x9982D | eq | continue | 0xB6FA0 | 125 |
| `_SHOK` | 0x98C44 | ne-fallthrough | continue | 0x7FC80 | 870 |
| `_SHS2` | 0x99673 | eq | continue | 0xB7350 | 400 |
| `_SNOW` | 0x9968C | eq | continue | 0x81060 | 0 |
| `_SNX2` | 0x98289 | ne-fallthrough | continue | 0x10610 | 1678 |
| `_SNX3` | 0x9986C | eq | continue | 0x106E0 | 845 |
| `_SOXY` | 0x99885 | eq | continue | 0xD690, 0xD8C0, 0xDA50 | 208 |
| `_SPFC` | 0x98CD0 | ne-fallthrough | continue | 0x76EA0, 0xD690, 0xD8C0 | 487 |
| `_SPMA` | 0x9929C | eq | continue | 0xC0570 | 199 |
| `_SPRC` | 0x992B6 | eq | continue | 0x78950 | 85 |
| `_SPRD` | 0x96A28 | ne-fallthrough | continue | 0x76EA0, 0xD690, 0xD8C0 | 36 |
| `_SQK2` | 0x992C7 | eq | continue | 0xB6B20 | 1236 |
| `_SQS2` | 0x992E0 | eq | continue | 0xB6EF0 | 532 |
| `_SSS2` | 0x986CF | ne-fallthrough | continue | 0xB7670 | 398 |
| `_SSW2` | 0x99D4B | eq | continue | 0xB7400 | 200 |
| `_STBR` | 0x990C2 | ne-fallthrough | continue | 0x830D0 | 14940 |
| `_STCC` | 0x97EED | switch | continue | 0x82D30 | 63261 |
| `_STCF` | 0x9B448 | switch | yield | 0x83E50 | 5179 |
| `_STCG` | 0x99A4F | switch | continue | 0x82CE0, 0x95CA0 | 39 |
| `_STCH` | 0x99AB7 | switch | continue | 0xD690, 0x81760 | 54494 |
| `_STCL` | 0x99AF0 | switch | continue | 0x83350 | 9 |
| `_STCP` | 0x99B03 | switch | continue | 0x83850 | 51568 |
| `_STEC` | 0x9991B | eq | continue | 0x10390 | 12900 |
| `_STGS` | 0x99065 | ne-fallthrough | continue | 0x84960 | 8110 |
| `_STMA` | 0x9969F | eq | continue | 0xC0330 | 24333 |
| `_STNS` | 0x996B8 | eq | continue | 0x87100 | 196 |
| `_STPV` | 0x983AB | ne-fallthrough | continue | 0x86440 | 733 |
| `_STRA` | 0x97264 | switch | continue | 0x86EE0 | 569 |
| `_STRC` | 0x9952F | switch | continue | 0x84190 | 0 |
| `_STRR` | 0x99542 | switch | continue | 0x84520 | 448 |
| `_STRT` | 0x99555 | switch | continue | 0x83940 | 21683 |
| `_STTR` | 0x99947 | eq | continue | 0xD690, 0xD8C0, 0xDA50 | 111 |
| `_STZ4` | 0x99038 | ne-fallthrough | continue | 0x83B70 | 32317 |
| `_TOMP` | 0x96F4C | eq | both | 0xD690, 0xD8C0, 0xE3C0 | 48 |
| `_TOMW` | 0x9AB56 | ne-fallthrough | yield |  | 48 |
| `_TRPY` | 0x96FC3 | eq | continue | 0xEC080 | 1 |
| `_VCLR` | 0x97277 | eq | continue |  | 0 |
| `_VCWT` | 0x96504 | ne-fallthrough | both | 0x9210 | 75 |
| `_VPL2` | 0x968B2 | ne-fallthrough | both | 0x173650, 0x7AF90, 0x76690 | 16 |
| `_VPLY` | 0x968B2 | ne-fallthrough | both | 0x173650, 0x7AF90, 0x76690 | 16689 |
| `_WKAD` | 0x97AE6 | eq | continue | 0x76690 | 45679 |
| `_WKBO` | 0x97BB8 | eq | continue | 0x76690 | 0 |
| `_WKOF` | 0x96B98 | ne-fallthrough | continue | 0x76690 | 0 |
| `_WKOR` | 0x9798B | eq | continue | 0x76690 | 21 |
| `_WKST` | 0x96566 | ne-fallthrough | continue | 0x76690, 0x76690 | 487857 |
| `_WNTY` | 0x972F5 | ne-fallthrough | continue | 0x53600 | 58568 |
| `_WTSV` | 0x9AF36 | eq | yield | 0xD690, 0xD8C0, 0xDA50 | 28 |
| `_WTTM` | 0x9AC1A | ne-fallthrough | yield | 0x173650, 0x7AF90, 0x76690 | 14828 |
| `_WTTR` | 0x9AFD1 | eq | yield | 0xD690, 0xD8C0, 0xDA50 | 0 |
| `_WT__` | 0x9AC1A | ne-fallthrough | yield | 0x173650, 0x7AF90, 0x76690 | 0 |
| `_XRSM` | 0x96E52 | eq | continue | 0x5C6C0 | 2 |
| `_XSTP` | 0x965E5 | ne-fallthrough | continue | 0x5C380 | 2 |
| `_Z***` | 0x96615 | wildcard | both | 0x173650, 0x7AF90, 0x92380 | 46810 |

Commands used by the scripts but **not handled** by ScriptStep (they reach the skip label with no
side effect): `_SNX4` (8,758 uses), `_FANX` (2,741), `_IFP4` (203), `_DMMY` (71), `_STQK` (3),
`_NOP_` (1) [V-all: the evaluator sends these names to skip; a search of all code finds no
compare against 0x4E5834 'NX4', 0x414E58 'ANX', 0x465034 'FP4', 0x54514B 'TQK']. They are
leftovers of other platforms' engine builds; a port must treat them as no-ops to match the PS4.

Small handlers worth noting [E]: `_RTMN` = set byte 0x174DE0C to 1, then the `_RTM_` handler
(0x97174); `_RTM2` = only set byte 0x174DE0C to 1 and continue (the flag is read and cleared by
0x7F1A0), so `_RTM2` is **not** a no-op; `_VPL2` shares `_VPLY`'s handler, `_SEFI` shares `_SEPL`'s,
`_WT__` shares `_WTTM`'s.

Commands handled but not used in this game's scripts: EIF_, ELS_, FCCL, FLNT, FLOP, FLSP, IFF_,
IFJP, IFLA, MSTP, SEAC, SEFI, SEP3, SEPN, SHAK, SHAS, SNOW, STRC, VCLR, WKBO, WKOF, WTTR, WT__.

Counting rule: the VM takes the 4 bytes after the first `_` of each `;`-terminated segment, so
the typo `_STTR,0,26);` (allscr section 459 = script 456, decoded offset 68,799) is dispatched as `_STTR` although it has no
`(`. VM-style totals: 94 distinct names, 1,036,358 commands (+ 46,810 `_Z` labels) in 1,083,717
segments; a `_XXXX(` regex finds 1,036,357 (STTR 110 instead of 111) [V-all].

### 5.4 Second interpreter `Script_Prefetch` (0x9B760): look-ahead scanner, 43 entries [E][V]

Earlier drafts called this function `Script_Replay` and described it as a state-rebuild
interpreter for rewind; the code shows a look-ahead scanner that collects the archive entries
the script will need next (script.md 3.6 reached the same conclusion) [E for the mechanics,
H for "prefetch" as the purpose]:

```
Script_Prefetch(p, target):                      # 0x9B760; called with target = 0x18
    save var_F/var_Z/var_G into 0x164D7D0/0x164E130/0x164EC20   (memcpy 0x960/0xAF0/0xFA0)
    copy the 40 words scene_state+0x04..+0x53 to a local array (a word with bit 15 set is
    replaced by the word 0x84C bytes later, scene_state+0x850..)   [H: per-layer image ids]
    if count > target: goto exit                 # count = dword 0x1654030 (cmp at 0x9B837)
    n = 0                                        # r15d
    loop:                                        # 0x9B880, same scan/front end as 5.2
        ... dispatch on letter table 0x20D170 ...
    skip:                                        # 0x9FF30
        if n > 0x3A: goto exit                   # at most 60 passes per call
        n += 1
        if count <= target: goto loop
    exit:                                        # 0xA017F
        restore var_F/var_Z/var_G from the copies (memcpy at 0xA0193..0xA01D6)
        restore the word 0x165420C saved at entry (0x9B82A -> 0xA01DD)
        call 0xC3B00(local dword [rsp+0x25C])    # [H: hands the list to the loader]
        return p                                 # scan position reached (not stored by callers)
```

- The handlers follow control flow and variables (`_GOTO`, `_IF__`, `_IFF_`, `_IFJP`, `_ELS_`,
  `_EIF_`, `_WKST`, `_WKAD`, `_WKOR`, `_WKOF`, `_PGST`; `_ZY**` local labels copy the label to
  the argument buffer 0x16543C0 and set the word 0x165420C = 1, which `_IFJP`/`_ELS_`/`_EIF_`
  also use). The picture commands `_FADS`, `_STCH`, `_SCH2`, `_STCN`, `_STCU`, `_STCW`, `_STFC`,
  `_STFI`, `_STFO`, `_STMV`, `_STT2`, `_STBG`, `_STEV`, `_SSLA`, `_PLDB`, `_PLDF`, `_PLDS`, `_PLD_`,
  `_ANIM`, `_EVCL`, `_EVT0`, `_EVT1` resolve their picture to an archive entry and append it to
  `prefetch_list` (0x1653EA0, i32[100]: `(slot - 10) << 16 | entry`; count 0x1654030 capped at
  100 by `cmp eax, 0x63; jg`; de-duplicated). Example: the `_FADS` handler 0x9C5FC reads
  argument 2 via 0x76690, maps it with 0x6B4D0 and appends at 0x9C7E0..0x9C7EE. The running
  byte total of the listed entries is the dword 0x1654034 (sizes from `Archive_EntrySize`
  0xE6B30 = HED `size_sectors << 11`), bounded by the budget dword 0x86AA20 [E; the
  list of appending handlers is from a static walk of each handler, `scratch/engine` notes].
  `_VPLY` / `_VPL2` and `_PLDV` call 0x76C10 instead and do not touch the list [E; meaning open].
  The `V` case accepts **only VPLY and VPL2** (0x9BD0F: `cmp r13,'PLY'; setne; cmp r13,'PL2';
  setne; test; jne skip`); every other `V???` name is skipped (all 250,047 names over
  [A-Z0-9_a-z] emulated, vm_variables.md 11). Earlier versions of this table showed a `V***`
  wildcard because the extractor did not model `setcc`.
- `_J***`, `_END_`, `_FRET`, `_RTM_`, `_RTMN` go straight to the exit: the scan stops at jumps
  and returns. A handler at 0x9C7D1 sets n = 1000 (`cmovb r15d, ecx`) to force the exit [E].
- Callers [E]: `Adv_Update` (0xA0B70) after each ScriptStep call: clears count/bytes
  (0xA1DD5, 0xA1DE9), sets the byte budget 0x86AA20 = 12,000,000 (0xB71B00), seeds the list
  from the current scene (`Prefetch_SeedFromScene` 0xA0740), then `Script_Prefetch(p, 0x18)`
  (0xA1E0E). `Adv_Rewind` (0xA2020) seeds the list from the restored history record (0x7ECB0)
  and calls `Script_Prefetch(p, count + 0x12)` (0xA23C9).
- A port can implement it as an optional asset prefetcher: script variables and the word
  0x165420C are restored on exit, so only the prefetch list (and the scratch argument buffer)
  change [E]. Its `_WKST` / `_WKAD` handlers have no `C<n>` target case (vm_variables.md 4.7)
  [EMU].

Entries (same columns as 5.3; `exit` = leaves the scan):

| command | handler VA | dispatch | exits | first callees | uses in allscr |
|---|---|---|---|---|---:|
| `_ANIM` | 0x9B924 | ne-fallthrough | continue | 0xC3270, 0xE6B30 | 0 |
| `_EIF_` | 0x9CFA1 | eq | continue | 0x79D30 | 0 |
| `_ELS_` | 0x9C082 | ne-fallthrough | continue |  | 0 |
| `_END_` | 0xA017F | ne-fallthrough | exit |  | 519 |
| `_EVCL` | 0x9CD7C | eq | continue | 0x76690, 0x5C550, 0xE6B30 | 0 |
| `_EVT0` | 0x9CE91 | eq | continue | 0x76690, 0x5C550, 0xE6B30 | 0 |
| `_EVT1` | 0x9B9F3 | ne-fallthrough | continue | 0x76690, 0x5C550, 0xE6B30 | 0 |
| `_FADS` | 0x9C5FC | eq | continue | 0x76690, 0x6B4D0, 0xE6B30 | 34040 |
| `_FCAL` | 0x9C72C | eq | continue |  | 0 |
| `_FRET` | 0xA017F | ne-fallthrough | exit |  | 0 |
| `_GOTO` | 0x9BA71 | ne-fallthrough | continue | 0x794D0 | 101 |
| `_IFF_` | 0x9C563 | ne-fallthrough | continue | 0x76690, 0x76690 | 0 |
| `_IFJP` | 0x9C4C7 | eq | continue | 0x794D0 | 0 |
| `_IF__` | 0x9BAD5 | ne-fallthrough | continue | 0x76690, 0x794D0 | 1194 |
| `_J***` | 0xA017F | wildcard | exit |  | 1741 |
| `_PGST` | 0x9C911 | ne-fallthrough | continue |  | 17202 |
| `_PLDB` | 0x9BB72 | switch | continue | 0x78410, 0x49C40, 0xE6B30 | 0 |
| `_PLDF` | 0x9CFEE | switch | continue | 0x52B20, 0xE6B30 | 0 |
| `_PLDS` | 0x9D07F | switch | continue | 0x78410, 0x78410, 0x4A060 | 0 |
| `_PLDV` | 0x9D1A0 | switch | continue | 0x76C10 | 0 |
| `_PLD_` | 0x9D22C | switch | continue | 0xC3270, 0xE6B30 | 0 |
| `_RTMN` | 0xA017F | ne-fallthrough | exit |  | 1 |
| `_RTM_` | 0xA017F | ne-fallthrough | exit |  | 545 |
| `_SCH2` | 0x9EEEA | eq | continue | 0x78410, 0x78410, 0xD690 | 4675 |
| `_SSLA` | 0x9E583 | ne-fallthrough | continue | 0x78410, 0x78410, 0x4A060 | 0 |
| `_STBG` | 0x9BC91 | ne-fallthrough | continue | 0x78410, 0x78410, 0x4A060 | 0 |
| `_STCH` | 0x9EEEA | eq | continue | 0x78410, 0x78410, 0xD690 | 54494 |
| `_STCN` | 0x9EEEA | eq | continue | 0x78410, 0x78410, 0xD690 | 0 |
| `_STCU` | 0x9F169 | masked | continue | 0x78410, 0x78410, 0x4A060 | 0 |
| `_STCW` | 0x9F169 | masked | continue | 0x78410, 0x78410, 0x4A060 | 0 |
| `_STEV` | 0x9BC91 | ne-fallthrough | continue | 0x78410, 0x78410, 0x4A060 | 0 |
| `_STFC` | 0x9F714 | eq | continue | 0x76690, 0x6B4D0, 0xE6B30 | 0 |
| `_STFI` | 0x9FC9B | ne-fallthrough | continue | 0x76690, 0x6B4D0, 0xE6B30 | 0 |
| `_STFO` | 0x9FC9B | ne-fallthrough | continue | 0x76690, 0x6B4D0, 0xE6B30 | 0 |
| `_STMV` | 0x9FD92 | ne-fallthrough | continue | 0xCD720, 0xE6B30 | 0 |
| `_STT2` | 0x9F24E | ne-fallthrough | continue | 0x4A060, 0x758C0, 0x4A110 | 0 |
| `_VPL2` | 0x9BD2B | ne-fallthrough | continue | 0x76C10 | 16 |
| `_VPLY` | 0x9BD2B | ne-fallthrough | continue | 0x76C10 | 16689 |
| `_WKAD` | 0x9C99A | eq | continue | 0x76690 | 45679 |
| `_WKOF` | 0x9BE15 | ne-fallthrough | continue | 0x76690 | 0 |
| `_WKOR` | 0x9CAFC | eq | continue | 0x76690 | 21 |
| `_WKST` | 0x9BFA2 | ne-fallthrough | continue | 0x76690, 0x76690 | 487857 |
| `_ZY**` | 0x9BEE0 | ne-fallthrough | continue |  | 1633 |

(ANIM, EVCL, EVT0/1, FCAL/FRET, PLD*, SSLA, STBG, STCN, STCU/STCW, STFC/STFI/STFO, STMV, STT2,
STEV are engine features unused by TSUKIHIME's scripts. Only `_ZY**` labels are handled: the
`Z` case first tests `cmp al, 'Y'` on the second name byte (0x9BED8), so `_ZM`/`_ZZ`/`_ZD`
labels are skipped by the prefetcher.)

### 5.5 Arguments and variables [E][V]

- Handlers call `Args_Parse` (0xD690) on the text after the name; `Args_Has(args,i)` (0xD8C0) and
  `Args_Int(args,i)` (0xDA50) read them; empty arguments (`,,`) are "absent". A backquote right
  after `(` (byte 0x60 at name+5) introduces a prefixed argument such as `` `017:100`` (seen in
  `_SEPL`); the strings `` `017:``, `` `001:0``, `` `018:``, `` `001:`` are in rodata (0x20D7DF..) [E].
- Variables (corrected after the engine re-check; full model, emulation evidence and the
  operand table in **vm_variables.md**): `var_F` (0x164B1D0, u16[1200]), `var_Z` (0x164BB30,
  u16[1400]), `var_G` (0x164C620, u16[2000]). Each command decodes an operand from its first
  byte; the index is the inline-reader value of the argument (leading zeros allowed: `G008` =
  G[8]) [EMU]:
  - `_WKST` / `_WKAD` targets: `C<n>` -> **F[F823 + n]** (F823 = number of the current
    message on the page; with F823 = 0, `C847` is F[847]), `F<n>` -> F[n], `G<n>` -> G[n],
    `_<x>ddd` -> G[n - 600] for n in 600..700, else F[n]; any other first byte (including `Z`)
    -> nothing is written. `_WKOR` / `_WKOF` targets: the same without `C`; `_WKBO`: `F`, `G`
    only. Script_Prefetch: no `C` case.
  - Value positions (`_WKST` arg 2, `_WKAD` / `_WKOR` / `_WKOF` arg 1, both `_IF__` operands):
    only `F`, `G` and `_` read a variable; everything else is a literal, so `C3`, `Z3`, `X3`
    read as 3. `_IF__` does not use 0x79D30.
  - `Script_EvalCondition` (0x79D30) is the expression evaluator of `_EIF_` only (bracketed
    `F[n]` / `Z[n]` / `G[n]` operands; `_EIF_`, `_IFF_`, `_ELS_` occur 0 times in the scripts)
    [EMU-13].
  - `_GOTS` / `_SELD` / `_SELA` / `_SELS` use only `_?ddd` (3 digit bytes): `_D000` = F[0] is the
    choice result. `_SELD` (list) stores the chosen `_SELR` value, `_SELS` (two buttons) the
    button index 0 / 1; `_SELA` is the "save your progress?" prompt: it opens only when
    F1118 = 0, always sets F1118 = 0 and stores nothing. In scan mode the three change nothing
    and yield (vm_variables.md 4.5) [EMU].
  - A history record (and so a save slot) keeps **356 selected F values** (u16 / i8 / bit
    lists at 0x2160E0 / 0x2165F0 / 0x216580, 5.6), not all of F; a slot load zeroes the rest of
    F and all of Z; the first 500 G values are persisted in the system data (7.3) [EMU, E].
- Variable accesses have **no bounds check**: the address is `base + 2*n`, so
  `_WKST(F1200,eq,7)` writes 0x164BB30 = Z[0] and `_WKST(G2000,eq,7)` writes 0x164D5C0 (the
  block after var_G). `Z` operands are never written [V: unicorn emulation of ScriptStep,
  `scratch/engine/emu_wkst.py`, `test_wkst_variable_writes`, vm_variables.md 5]. The retail
  scripts stay in range: F up to F1123, G up to G070; operand uses are `_WKST` arg 0 F 402,379
  / **C 85,471** / G 7, arg 2 F 20 / G 1, `_WKAD` F 42,604 / **C 3,075**, `_WKOR` G 21, `_IF__`
  F 641 / G 553, `_GOTS` / `_SELD` / `_SELA` / `_SELS` `_D000` 170 / 170 / 26 / 1 [V-all]. A port
  should reject out-of-range indices instead of aliasing.
- Asset names in arguments (e.g. `WHITE`, `img2511`, `SIE_A_59_00_N00`) are resolved with
  `NameTable_Lookup` (0x78410): copy up to 32 characters, stop at NUL or '.', optionally upper-case,
  then look the string up in a `std::map` built lazily from an embedded 32-byte-record name table,
  returning a u16 index or 0xFFFF. Tables: `names_allpac2` (0x7F6800, 14,744 records; record i
  equals the **base name** of allpac2.nam record i, i.e. without the extension: `BLACK` vs
  `BLACK.MZP`; allpac2.nam has 14,746 records = these 14,744, `DUMMY.MRG` and one all-zero record
  [V-14744]) and `names_sprites` (0x7A8B10, 9,958 records, a subset of the allpac2 names).
  Character sprite names carry a `_NN_NN` suffix that `Image_ResolveSprite` (0x957B0) strips and
  turns into two numbers; unknown names fall back to `BG_ERROR` [E].
- `image_info` (0x671900) is the engine image table, documented in full in **images.md section
  8** and **archives_ui_movies.md section 4** (other agents' work; this document defers to
  them). Summary of the corrections to an earlier draft of this paragraph [V]:
  - 14,775 slots of 0x48 bytes (getter 0x41BA0 bounds `cmp edi, 0x39B6`), 14,744 used; slot k
    describes allpac2 entry k.
  - +0x08 is the image drawn first, which is the entry's own file only for 11,215 records;
    3,529 records (indices 9,958..14,540, paired character sprites) point to the shared body
    image `..._B.MZP` (an allpac0/1/1_2 file), with the face overlay at +0x10. Example record
    9958: +0x08 -> `AKI_A_00_00_N00_B.MZP`, +0x10 -> `AKI_A_00_00_N01.MZP`, while
    allpac2.nam[9958] = `AKI_A_00_00_N01.MZP`.
  - +0x2C/+0x2E are **not** the allpac2 picture size: they are `floor(w/3)` and
    `floor(h*362/1080)` of the logical size w x h at +0x30/+0x32, i.e. the size in the
    640x362-unit canvas (BLACK: 640x362 vs 1920x1080; the real allpac2 BLACK is 150x84, a
    1/12.8 copy, archives_ui_movies.md 4.1) [V-all 14,744 in `test_image_table_fields`].
    +0x34/+0x38 are anchor x/y in the same units, not a third size.

### 5.6 Text lines, read flags and history [E][V]

- Text commands are `_Z<type><XXX><YY>(...)`: type letter M (42,590), Z (2,570), Y (1,633),
  D (17); XXX = 3 **lower-case** hex digits "block" (10..3970, 3,419 distinct), YY = 2
  lower-case hex digits "line" (1..255; 0 does not occur) [V-46,810; no upper-case digit occurs].
  The handler (0x96615) parses them into 0x1654344 (block) / 0x1654348 (line). Digit decoding
  (0x96657..0x966E3) is `d = c - 0x57 if c >= 'a' else c - 0x30`, so upper-case `A`..`F`
  decode wrongly (`_ZM00A01` -> block 17, `_ZM0aB0C` -> block 178, line 19; emulated,
  `scratch/engine/emu_z.py`) [E][V]. A port should decode exactly this way (or reject
  upper case).
- **Read flags**: before switching to a new label the handler marks the previous one as read:
  `if prev_line > readmax[prev_block] and prev_block <= 0xFB3: readmax[prev_block] = prev_line`
  (`cmp edi, 0xFB3; jg` = a signed upper bound only; blocks 0xFB4..0xFFF, possible with 3 hex
  digits, are never recorded), where `readmax` is the byte array at 0x17DA278 (4,020 entries).
  The array is saved in the system data (+0x738, 0xFB4 bytes). It drives "skip read text only"
  and the read-text colour [E][H].
- **History ring**: `history_ptr` (0x120A6E0) -> 800 records x 0x3A10 bytes (11.9 MB); index words
  0x120A6EC/0x120A6EE (next), wrap flag 0x120A6F0 [E]. A record holds everything needed to resume:
  +0x04 i32 valid (>= 0), +0x08 u64 offset of the current command in the decoded script,
  +0x18 u16 script number, +0x1A/+0x1C i16, +0x1E/+0x1F i8, +0x24/+0x26 u16 (mode words),
  +0x28 32 bytes, +0x48 u64, +0x52 u16 + +0x58 0x1A08 bytes `scene_state`, +0x3468 64 bytes,
  +0x34A8 u16, +0x34AA 39 x u16 selected F values (index list at 0x2160E0), +0x34FA bit field
  (index list 0x216580, 43 bits), +0x3501.. 300 x i8 F values (index list 0x2165F0, entries
  <= 0x4B0), +0x3630 u32, +0x363C/+0x363E u16, +0x3641 i8, +0x3642 0x15-stride arrays,
  +0x366C..+0x36D0, +0x36D0 20 x 0x18-byte records (sound channels [H]), +0x38B0..+0x39F8 window/
  text state [H] (from `History_Restore`, 0x7C6D0).
- Rewind ("hold to rewind", "previous scene", "jump to selected scene" in the flowchart):
  `Adv_Rewind` (0xA2020) restores a history record with `History_Restore` (0x7C6D0: script,
  offset, variables, scene_state, sound and window state) and continues from the record's
  script position; it then only refills the prefetch list (0x7ECB0, `Script_Prefetch`), it does
  not re-execute commands (5.4) [E].

---

## 6. Archive slots and asset loading [E]

`Archive_OpenSlot` (0xE4FA0) and `Archive_OpenHedMrg` (0xE54C0) bind files to numbered slots:

| slot | file(s) | slot | file(s) |
|---:|---|---:|---|
| 3 | ft_<lang>.mrg | 0x0F | allpac4.hed/.mrg |
| 4 | allpac0.nam | 0x13 | voice.hed/.mrg |
| 5 | allpac1.nam | 0x15 | voice2.hed/.mrg |
| 6 | allpac1_2.nam | 0x17 | se_nam.mrg |
| 8 | allpac3.nam | 0x18 | parts.hed/.mrg |
| 9 | allpac4.nam | 0x1A | bgm.hed |
| 0x0A | allpac0.hed/.mrg | 0x1B | vse.hed |
| 0x0B | allpac1.hed/.mrg (*) | 0x1C | vse.mrg |
| 0x0C | allpac1_2.hed/.mrg (*) | 0x1D | vag.mrg |
| 0x0D | allpac2.hed/.mrg | 0x1E | vag.hed |
| 0x0E | allpac3.hed/.mrg | 0x1F / 0x20 | allscr.mrg / script_text.mrg |

(*) opened at start only when 0xEED80 (PlayGo locus check) does not return 1, otherwise later by
0xD1C10/0xD1C70/0xD1CD0 (download-in-progress handling) [E]. Note that allpac2.nam is **not**
loaded from disk: its names are embedded (5.5). `Game_OpenArchives` (0xD1F40) also registers vag
entries 0..49 with the sound player (loop 0xD21C0..0xD221E, 0x32 iterations, 0x110020; vag.hed
has 19 entries [V containers]), seeds `srand` from a clock-derived byte (0xC2D90/0xC2D80) [H],
clears the 0xFC8-byte block at 0x17DA268 (u16 0x2AF8 at +8; bytes 40,40,100,40,75 at +0xB..+0xF,
meaning unknown; the read-text table `readmax` starts at +0x10) [E].

The boot file order in the shadPS4 log (nam x5, hed x6, voice/voice2 hed, ft_ja.mrg, parts.hed,
se_nam.mrg, bgm.hed, vse.hed/.mrg, vag.hed/.mrg, allpac3.mrg, allpac0.mrg, data/if_ja.png, parts.mrg)
matches this code [L].

---

## 7. Save data

No save file existed on this machine when this was written: `%APPDATA%/shadPS4/home/1000/savedata/`
was empty (directory modified 2026-10-25 06:04) and no copy exists elsewhere under the user
profile. Everything below is from the code; `eboot.savedata` parses the layout and validates
the markers, and its real-data test runs automatically once a save exists.

### 7.1 Files [E]

```
dirName   = "%s%03d" % ("savedata", 0) = "savedata000"          (SaveAuto_Ctor 0x126140)
mount     = sceSaveDataMount2(mode 2 = RDWR, or 6 = CREATE|RDWR, blocks = cfg+0x61C)
            on SCE_SAVE_DATA_ERROR_BROKEN (0x809F000F): delete and recreate
params    = sceSaveDataSetParam(title, subtitle, detail, userParam)     (title strings 0x2D61B7..:
            "月姫 -A piece of blue glass moon-", "月姬 -A piece of blue glass moon-",
            "TSUKIHIME A piece of blue glass moon"; subtitle "%03d: %s")
file      = <mount>/savedata000, size 0x400000, written in 0x2000-byte sceKernelWrite chunks
            with sceSaveDataDialogProgressBarSetValue                (SaveData_WriteThread 0x121030)
icon      = sceSaveDataSaveIcon from /app0/data/if_<lang>.png       (Save_IconPath 0x41AD0)
unmount   = sceSaveDataUmountWithBackup
load      = Mount2 + CheckBackupData, nexl_Save::loadFile (0x120CA0: Stat/Open/Read/Close),
            RestoreBackupData on failure (0x121B00)
```

`Save_IconPath` has the bug the local shadPS4 patch works around: it stores two overlapping
8-byte immediates (`/app0/da`, `data/if_`) and calls `strcat` without writing the terminator at
buf[14], so the language code is appended after stale bytes (shadPS4 patch at 0x41B16/0x41B2B).

### 7.2 Image layout (4 MiB) [E]

| offset | size | content |
|---|---|---|
| 0x000000 | 0x18AC | system block (7.3), u16 0x2AF8 at +0 |
| 0x001A00 + i*0x7140 | 0x7108 | slot i, i = 0..139 (7.4) |
| 0x3E0900 | 0x1F700 | unused (zero). The slot area 0x1A00 + 140*0x7140 ends at 0x3E0900 (slot 139's record data ends at 0x3E08C8, the rest of its stride is padding); corrected from 0x3E0B80 / 0x1F480 after the engine re-check [E; decoded from the instructions by `misc_vm_variables_errata.save_image_layout`] |

`SaveImage_WriteBlock(slot, src, size)` (0xEACF0): `dst = slot < 0 ? image : image + 0x1A00 +
slot*0x7140; memcpy(dst, src, size); *(u16*)dst = 0x2AF8`. `SaveImage_ReadBlock` (0xEAC80)
returns 2 when the first u16 is 0. `SaveImage_Commit` (0x6FEC0) writes all 140 slot records from
`slots` (0xE24600) and then the system block from `system_staging` (0x1201660). The whole
image is zeroed at boot (0xEA5D0) and allocated twice as "savebuff" (image 0x1765788, shadow
copy 0x1765778) [E].

### 7.3 System block (0x18AC bytes, staging 0x1201660) [E]

Every byte of the block is listed (the table is `eboot.savedata.SYSTEM_FIELDS`; a test checks
that it tiles 0..0x18AC). `SaveImage_Commit` (0x6FEC0) fills the parts with a RAM source and
then copies the whole 0x18AC-byte staging block; `SaveImage_LoadSystem` (0x6F470) reads it back.

| offset | size | source (RAM) | meaning |
|---|---|---|---|
| +0x0000 | 2 | - | u16 0x2AF8 (11000), stamped by `SaveImage_WriteBlock` |
| +0x0002 | 2 | - | u16 1 (written by the commit at 0x6FF27; the loader tests it != 0) |
| +0x0004 | 0x140 | `config` 0x66C578 | configuration (8.4); language at config+0x24 |
| +0x0144 | 0x3E8 | `var_G` 0x164C620 | G[0..499] (u16) |
| +0x052C | 0x20C | 0x164D5C0 | unknown (directly after the G array in RAM). The loader ORs +0x52C..+0x72B (0x200 bytes, `vorps`) into RAM and reads +0x72C..+0x737 byte by byte [H: unlock bit sets] |
| +0x0738 | 0xFB4 | 0x17DA278 (= 0x17DA268 + 0x10) | `readmax[4020]` read-text table (5.6) |
| +0x16EC | 2 | - | not written by the commit (zero) |
| +0x16EE | 2 | 0x1202D4E | most recent slot (commit mode 2: slot with the newest timestamp, via mktime/difftime; mode 1: copy of 0x1202F14); "Continue" (0xAFDA0, 0xB06B0) reads it and requires <= 0x8A |
| +0x16F0 | 2 | - | not written by the commit (zero) |
| +0x16F2 | 2 | 0x1202F16 | u16, meaning unknown |
| +0x16F4 | 0x118 | 0x164FBC0 | unknown. The loader ORs +0x16F4..+0x1803 (0x110 bytes) into RAM and reads +0x1804..+0x180B byte by byte [H: unlock bit sets] |
| +0x180C | 0xA0 | - | no code reference found (staging is bss, so zero in a game-made image) |

### 7.4 Slot record (0x7108 bytes) [E]

Every byte listed (`eboot.savedata.SLOT_FIELDS`, tested to tile 0..0x7108):

| offset | size | meaning |
|---|---|---|
| +0x0000 | 2 | u16 0x2AF8 |
| +0x0002 | 2 | u16 used (1); `Slot_Store` writes the dword 0x00012AF8 at +0 |
| +0x0004 | 4 | i32 has state B: 1, else the (<= 0) value passed to `Staging_SetSnapshot` |
| +0x0008 | 0x3A10 | state A = one history record (5.6): +0x18 script number, +0x08 script offset |
| +0x3A18 | 0x36A8 | state B: first 0x36A8 bytes of a second record, copied only when +4 = 1 |
| +0x70C0 | 2 | u16 year |
| +0x70C2 | 1 x 5 | u8 month (1..12), day, hour, minute, second (`Rtc_LocalTime` 0xEAD50); the commit converts them with `tm_year = year - 1900`, `tm_mon = month - 1` |
| +0x70C7 | 1 | padding (copied from the staging record, never written) |
| +0x70C8 | 2 | u16 0x2AF8 end marker |
| +0x70CA | 0x3E | no code reference found (zero) |

`Slot_Store(n)` (0x737D0) copies `slot_staging` (0x12031E0) to slot n and stamps the date; the
staging record is filled by `Staging_SetSnapshot` (0x73980) with the current history record.
The save/load menu (0x70340) shows 4 slots per page and accepts slot ids 0..138 (0x8A); slot
139 is not reachable from the menu [E][H: quick/auto save]. `QuickSave_Store` (0x73A70) keeps a
separate dated record at 0x11FA558 [E].

### 7.5 What a port should do

Keep the same logical content (config, G variables, readmax, 139 slots with state records) in a
PC save format of its own; optional importer for PS4 images via `eboot.savedata`. The state
record is the engine's internal RAM layout, so importing PS4 slots requires the full meaning of
`scene_state` and the 0x3A10 record (open question).

---

## 8. System flow and features

### 8.1 Boot sequence [E][L]

1. Module load, sysmodules, user service, VideoOut open (3 buffers), pad, audio ports (8.6), NP
   title id, trophy context, PlayGo, mouse [L].
2. task_game: display surfaces, captures, mouse, save image clear, `sceSystemServiceHideSplashScreen`,
   reads `data/if_<lang>.png` (save icon) [L].
3. Boot state 0: `SaveSystem_CreateInitial` (0xEA390) builds the system save ("Create save data."
   dialog on first run) [E][L].
4. `Game_OpenArchives` (archive slots, vag preload) and `Game_StartProcesses`.
5. Caution screens (`caution_<lang>.mzp`, `caution1*`, `caution2*` names at 0x218A58..), logo
   (`abmp_logo.mrg`), title (`title_parts.mrg`, `BG_TOP.MZP`, title movies `titlebg1..4.mp4`),
   prologue movie `prologue[_en|_zc|_zt].mp4` on New Game, then the first script [V strings][H order].

### 8.2 Title menu (UI text, EN table) [V]

New game ("Begin a new game."), Continue ("Load game data from the last save."), Load, Config,
Gallery ("Enter the gallery."): CG ("View images."), Flowchart, Music ("Listen to music."), Voices
("Listen to voices."), Extra scenarios ("Play extra scenarios."), Return to title. Extras also
include 25 "Teach Me, Miss Ciel" episodes and 25 "Henteko Hint Corner" episodes (JP
`ヘンテコヒントコーナー(第N回)`), the opening "Glass Moon" replay, credits (credit00..06.mp4) and
endings (ed01_arc, ed02_ciel, ed03_ciel) [V].

### 8.3 In-game menu and ADV functions [V strings][E where noted]

Save, Load, Config, Voice playback ("Listen to voices."), Save system data, Return to title,
Close menu, Flowchart, Previous scene, Next scene, Rewind (hold), Fast-forward (hold), Auto on/off,
Quick save, Quick load, Replay last voice, Backlog ("Open the log."). Dialog texts cover
save/load/overwrite/copy/delete confirmation, corruption ("The data may be corrupted."), version
mismatch for save and system data, "Not enough memory available.", download/copy in progress
(PlayGo) and quick jump.

### 8.4 Configuration options [V strings][E for storage]

Sound: reset; BGM / voice / SE / system SE / movie volume; stop voice on text advance (on/off);
lower BGM during voice (on/off); BGM 2ch vs 5.1ch (JP; the EN text says mono/stereo).
Voice: reset; system voice; per-character voice on/off + volume for ???, misc. women, misc. men,
misc., 遠野志貴, アルクェイド, シエル, 遠野秋葉, 翡翠, 琥珀, 乾有彦, ノエル, マーリオゥ, ヴローヴ, ロア,
蒼崎青子, アラク博士, アンドウ, カリウス, 斎木業人, さつき, 遠野志貴（少年）, みお (23 entries).
Text: reset; text speed; auto speed; window opacity; outline on/off; shadow on/off; fixed colour vs
change colour of read text (first-time / previously read colours); window design; language.
System: reset; skip all text vs read text only; stop skip at choices on/off; auto quick save at
choices on/off; auto quick save at scene start on/off; button help text on/off (two contexts);
vibration on/off; show controls; apply recommended settings.

Storage: `config` (0x66C578, 0x140 bytes) [E]: +0x24 u16 language (0 ja, 1 en, 2 zc, 3 zt,
>= 4 = follow system language: 0 -> ja, 11 -> zc, 10 -> zt, else en, `Lang_Index` 0x3ED90).
Defaults written by 0x3EAC0: u16 +0x14 = 40, +0x16 = 100, +0x18 = 40, +0x1A = 10, +0x20 = 35,
+0x22 = 15, +0x2E = 2200, +0x30 = 1, u32 +0x3C = 0x3FFFFFFF (per-character voice enable bits),
+0x4E = 1, +0x54 = 40, +0x76..+0x9D = 20 u16 per-character volumes (16 from 0x1A66A0 + 4 x 50),
+0x9E = 3000, +0xA0 = 0, +0xA4 = 0, +0xA5 = 1. `0x3E6E0` copies menu item values into the block
(items 0x36..0x4A of page 0 -> +0x9C, +0x20, +0x9D, +0x22, +0x9E, +0x2E, +0xA0, +0xA2, +0x30,
+0xA3, +0xA4; page 1 items 0x51..0x5E -> +0xA6, +0x14, +0xA7, +0x18, +0xA8, +0x1A, +0x16
(item 0x5C == 1 -> 100 else 0), +0x4E; per-character items 0x62+5i/0x64+5i -> bit i of +0x3C and
u16 +0x76+2i for i < 19). Which item is which option is not mapped yet (open question).

### 8.5 Input [E]

`PadMgr_Update` (0x11E7A0) converts `ScePadData` into an internal 32-bit mask (`+0x1FC` current,
`+0x1F8` previous, `+0x200` pressed edge, `+0x208` released edge, `+0x20C` changed,
`+0x204` auto-repeat with initial delay `+0x438` and period `+0x43C` frames, per-bit counters
at +0x440):

| internal bit | source | internal bit | source |
|---|---|---|---|
| 0x0001 | touch pad click (0x100000) | 0x0400 | L1 (0x0400) |
| 0x0002 | L3 (0x0002) | 0x0800 | R1 (0x0800) |
| 0x0004 | R3 (0x0004) | 0x1000 | TRIANGLE (0x1000) |
| 0x0008 | OPTIONS (0x0008) | 0x2000 | **decide**: CIRCLE, or CROSS if ENTER_BUTTON_ASSIGN == 1 |
| 0x0010/20/40/80 | D-pad up/right/down/left | 0x4000 | **cancel**: the other of CIRCLE/CROSS |
| 0x0100 | L2 (0x0100) | 0x8000 | SQUARE (0x8000) |
| 0x0200 | R2 (0x0200) | 0x10000/20000/40000/80000 | left stick up/right/down/left (threshold) |
|  |  | 0x100000/200000/400000/800000 | right stick up/right/down/left |

Sticks are normalised to -1..1 with a dead zone (constants 0x2DA19C..0x2DA1AC); touch points
are tracked (two fingers, 0x12B690). Vibration: two motors from floats +0x540/+0x544 x 255;
light bar via `scePadSetLightBar` with the user colour (`sceUserServiceGetUserColor`).

`Pointer_Update` (0xCF240): `sceMouseRead` (up to 8 samples) plus touch-pad movement (delta / 3)
move a cursor (x dword 0x178EC34, y dword 0x178EC38) in a **640x448** logical space: one
path (0xCF5CC..0xCF611) clamps x to 0..639 (`mov edx, 0x27F; cmp eax, 0x280; jae`) and y to
**0..447** (`mov esi, 0x1BF; cmp edx, 0x1C0; jae`), the other (0xCF6DF..0xCF738) to 1..638 x
1..446 [E; V in `test_pointer_clamp`]. No 359/360 bound exists in the pointer code; 640x448 is
also the most common UI canvas size in the eboot (archives_ui_movies.md 7.5). Mouse buttons map
to pointer flags (newly pressed / held: left 0x2 / 0x8001, right 0x8 / 0x4, middle 0x20 / 0x10,
in the word at 0x178EC54) [E]. (An earlier draft said 0..359 / 640x360; that was wrong.)

Action bindings: game code tests `input_bits & *action_mask[k]` where `0xDB8778` points to the
active one of three 27-entry u64 tables at 0xDB84F0 (values like 0x20, 0x40, 0x2, 0x8, 0x80,
0xC1, 0x800, 0x100, 0x10, 0x51, 0x1000, 0x4040) [E]. The bit space of these masks is a
game-level remap (not the pad bits above) and the 27 action names are an open question.

### 8.6 Audio [E][L]

- `Waveout_Init` (0x117E40): AudioOut MAIN port, 1024 samples, 48 kHz, float stereo, and a second
  MAIN port 1024 samples, 48 kHz, float 8-channel (5.1 BGM option) [E][L].
- HEVAG player (0x10E660): MAIN port 256 samples, 48 kHz, float stereo; decode thread +
  waveout thread; `sceAudiodecCpu*` in the NEXL_HEVAG_CTRL code (0x115480..0x115E80) [E].
- ATRAC9 (NEXL_ATRAC9_CTRL, 0x113A50; decoder create/decode 0x1130B0/0x1132B0) for bgm/vse/voice,
  with `nexl_StreamFile` / `nexl_StreamMem` sources [E].
- Movie audio (0x1025C0): AvPlayer audio frames -> MAIN port 256 samples, float stereo at the
  stream rate [E].

### 8.7 Movies [E][V]

AvPlayer (`sceAvPlayerInit` in 0x1013A0) with `nexl_AvAlloc`/`nexl_AvAllocTex`; video frames are
uploaded as two textures ("avplayer texture Y", "avplayer texture CbCr") and drawn with the
`basic YCbCr` shader (`y_texture` + `cbcr_texture`). Paths are absolute strings in rodata
(`/app0/image/movie/*.mp4`, 0x2D66E8..0x2D76FC): titlebg1..4, prologue[_en/_zc/_zt], op1_arc,
op2_ciel (+_en/_zc), phaseout, phasetitle_01_00..15_01b (+_en/_zc/_zt), ed01_arc, ed02_ciel,
ed03_ciel, TM_CI, credit00..06. Looping (`sceAvPlayerSetLooping`) is used for title backgrounds [H].

ffprobe over the 134 files in image/movie [V-134]: all H.264 1920x1080 yuv420p, 52 at 30 fps and
82 at 29.97 fps; 43 carry an AAC 48 kHz stereo track (prologue*, op1/op2*, ed01_arc, ed03_ciel,
TM_CI and the `_zt` phase titles plus three others), the other 91 are video only (most phase titles,
titlebg*, credits). Movie audio is mixed through its own AudioOut port (8.6).

### 8.8 Platform services kept or dropped

Trophies (`troph` thread, 0x12B8B0..0x12BC30), PlayGo, Share Play, live streaming, screenshot
overlay, NP title id: not needed for a PC port except as optional extras.

---

## 9. UI text table [E][V-all]

The menu/dialog/config/music/chapter/flowchart texts live in one table of string pointers,
`ui_text` (RELRO VA 0x41D5F0, file 0x349800), read by `UiText_Get` (0xC29B0, 30 callers):

```
UiText_Get(id):                       # 0xC29B0
    if (u32)(id - 1) > 0x5D9: return NULL          # valid ids 1..1498
    i = (i16)config[+0x24] * 0x5DB + id            # language * 1499 + id
    if (u32)i > 0x1D47: return NULL                # 5 x 1499 = 7495 entries
    return ui_text[i]                              # char*, UTF-8, NUL-terminated
UiText_GetLang(id, lang)              # 0xC29F0: the same with lang as an argument
```

| field | value |
|---|---|
| table | `char *ui_text[5][1499]`, u64 pointers (data VAs). The values are present in the file (prelinked) and each slot also has an R_X86_64_RELATIVE relocation with the same addend [V-7495] |
| language index | 0 ja, 1 en, 2 zc (Simplified), 3 zt (Traditional), 4 ko |
| id 0 | placeholder `NULL` (ja, en) / `NULL ` (zc, zt, ko); never returned by the getter |
| ids 1..1498 | 1,498 strings per language; 7,495 distinct pointers |
| string storage | packed per language: ja 0xD63F38..0xD7BFCE, en 0xD7BFCE..0xD8DF2C, zc 0xD8DF2C..0xDA0AAA, zt 0xDA0AAA..0xDB3CE7, ko 0xDB3CE7..0xDB72F4 (each language's `NULL` placeholder opens its block) |
| ko column | placeholders only (`한글 system message 1`, `... 2`, ..., mostly `Text`): no Korean UI in this build |
| line break | `^` (also used by the script text renderer) |

Worked example (file offsets: RELRO VA - 0x400000 + 0x32C210; data VA - 0x600000 + 0x365FA0):

```
0041d5f0: 383f d600 0000 0000 403f d600 0000 0000   ui_text[ja][0] -> 0xD63F38 "NULL", [ja][1] -> 0xD63F40
004204c8: cebf d700 0000 0000 e0bf d700 0000 0000   ui_text[en][0] = slot 0x41D5F0 + 1499*8 -> 0xD7BFCE "NULL",
                                                    ui_text[en][1] -> 0xD7BFE0
00d7bfc8: 818b efbc 9f00 4e55 4c4c 0000 0000 0000   ...end of ja[1498] | "NULL" (en id 0) | padding
00d7bfd8: 0000 0000 0000 0000 4368 616e 6765 2061   "Change a
00d7bfe8: 7564 696f 2073 6574 7469 6e67 732e 0000    udio settings."  = en id 1
```

The language index comes straight from config+0x24 (not from `Lang_Index`), so the getter
returns NULL for a raw value >= 5 and Korean placeholders for 4 [E; how the engine keeps
config+0x24 below 4 at run time is not traced]. Contents: the config help lines (8.4), dialog
messages, music-room titles and credits ("Tsukihime: re", "Ciel's Theme", ... with
Vocals/Lyrics/Comp/Arr credits), chapter and scene titles ("00 硝子の月", "01 帰路の朝Ⅰ", ...),
the "Teach me, Miss Ciel" titles (`教えて！シエル先生　その１`.. = ja ids 888.., one per episode, with
U+3000 ideographic spaces; 63 ja strings contain U+3000) and the flowchart synopses. These
strings are **not** in script_text.mrg, so a port must extract them from the user's eboot.bin
(or ship its own translations).

`python -m eboot.uitext --out DIR` writes `ui_text_<lang>.tsv` (header `id, va, text`; 1,499
rows per language; in the text column backslash, TAB, LF and CR are written as `\\`, `\t`, `\n`, `\r`;
none of them occurs in v01.01). Nothing is
filtered: a pointer without matching relocation or a string that is not UTF-8 raises.
(An earlier export scanned the data segment heuristically and silently dropped the 63 ja strings
with U+3000; this table-driven export replaces it.)

Other embedded tables: `names_bgm` (0x7A8820: LLBGM01..19, LL_INS, LL_OP, LL_ED; used by
0xA3A40), the gallery/CG lists (`extra_parts_cg`, `IMG3000.MZP`.. at 0x204D9C.., `gallery_parts_*.mrg`),
the SE list (`moon_se02009`.. at 0x1A6774), eye-catch/ending parts (`eye_*.mrg`, `melty_*.mzp`,
`roll_cg_*.mzp` at 0x201188..), and 63 `SDTA/SDTB` names at 0x869F80 (no code reference found).

---

## 10. Open questions and next steps

1. Map the 27 action-mask slots (0xDB84F0) to actions (advance, menu, skip, auto, backlog, hide
   window, ...): find the code that builds `input_bits` (`[rbx+0x20]` tested by Adv_Update).
2. Config field <-> option mapping (8.4): decode the menu item table behind 0x3D440.
3. Full GCN disassembly of the 24 blend pixel shaders (to confirm the formulas and the meaning of
   PS_Diff5/PS_Dodge5) and of the transition shaders (mask threshold/softness formula, and what
   `transition 2DTex` does without a mask).
4. Confirm that the `_STBR` mode reaches DisplaySurface +0x608 unchanged (4.3); one script uses
   mode 28, which the selector maps to index 0.
5. Meaning of the 0x3A10 history record fields and `scene_state`, needed to import PS4 saves.
6. Obtain a real `savedata000` (play to the first save) and run
   `python -m eboot.savedata <file>` + `pytest tests/test_eboot.py` (the real-data test is ready).
7. Per-command argument semantics: the handler VAs in 5.3 are the entry points; the
   `first callees` column gives the subsystem functions to read next.
8. Why slot 139 is not in the save menu; what sets the fast-forward mode (dword 0x16541F8 = 1,
   3.3) and what clears the flag word in each case.
9. Duration units of `_FADS`/`_FADE` and the other timed commands (the waits use ms -> frames, 3.3).
10. The unknown system-block parts (+0x52C, +0x16F2, +0x16F4; 7.3) and the bytes 40,40,100,40,75
    at 0x17DA273 (6).
11. Which code uses which `ui_text` id (30 callers of `UiText_Get`; the id is usually an
    immediate at the call site), and how config+0x24 is kept below 4 when the "follow system
    language" setting is active (9).
12. Purpose of the dword passed to 0xF0830 by the screen-effect code (6 for effects 26 and 29),
    and of `_V***`/`_PLDV` in the prefetcher (0x76C10).
13. The SDTA/SDTB name table (0x869F80, 63 entries, no code reference found) and the purpose of
    the 9,958-name sprite table at 0x7A8B10 next to the full allpac2 table.

---

## 11. Evidence (commands actually run)

- `python -m eboot.selfelf`: 10 SELF segments, 12 phdrs, dynamic table, 27 needed modules, 27
  import-library entries (26 with symbols).
- `scratch/engine/resolve_nids.py`: 1,658 candidate names -> 337/337 NIDs resolved, written to
  `tools/eboot/nid_db.tsv` (each line re-hashed by `nids.load_db`).
- `python -m eboot.imports --json/--md`: 337 symbols, 334 PLT stubs, call sites per function.
- `python -m eboot.disasm` index: 7,482 eh_frame functions, 416,283 instructions, 39,459 relocated
  pointers; all 97 function names in `eboot.symbols` are eh_frame starts (tested).
- `python -m eboot.scriptvm --json samples/engine/vm_commands.json`: ScriptStep 112 entries
  (110 names + J***, Z***), Script_Prefetch 42 entries (39 names + J***, V***, ZY**), about 1.5 s
  per interpreter. Cross-checked against the reviewers' independent brute-force evaluator
  (`scratch/verify_engine_reproduce/main_eval.json`, `replay_eval.json`): ScriptStep 101,416
  names over `[A-Z0-9_]`, 0 differences; Script_Prefetch 0 differences outside the `Z` letter
  (the reviewer's evaluator stopped at `cmp al, 'Y'`; the new evaluator resolves it to `ZY**`).
- `scratch/engine/cmd_counts.py`: all 549 decoded scripts, VM-style segmentation: 1,083,717
  segments, 94 command names, 1,036,358 commands, 46,810 `_Z` labels (M 42,590, Z 2,570, Y 1,633,
  D 17); every used name except the six in 5.3 has a ScriptStep handler.
- `scratch/engine/prefetch_handlers.py`: static walk of the 42 prefetcher handlers; 22 append to
  `prefetch_list`.
- `scratch/engine/emu_wkst.py` (unicorn): `_WKST(F10|F1199|F1200|Z0|Z10|Z1399|G10|G1999|G2000|
  G010,eq,7)` and `_WKAD`; `scratch/engine/emu_z.py`: `_ZM00a01`, `_ZMf8205`, `_ZM00A01`,
  `_ZM0aB0C`, `_ZMfff01` -> block/line values quoted in 5.5/5.6.
- `python -m eboot.shaders --dump`: 67 blobs, 38 classes, every blob owned, GCN census clean;
  header byte census: (+0x0C, +0x0D, stage) = (1, 1, PS) x35, (0, 1, VS) x32.
- `python -m eboot.uitext --out samples/engine`: 5 languages x 1,499 entries, all pointers equal
  their relocations, 63 ja strings with U+3000 present.
- Image table: all 14,744 used records satisfy +0x2C = (w//3, h*362//1080); +0x08 equals
  allpac2.nam[k] for 11,215 records and is a `_B` body for 3,529 (indices 9,958..14,540).
- Disassembly reads of: 0x95E60 (dispatch, budget), 0x9B760/0x9FF30/0xA017F (prefetcher loop,
  variable save/restore), 0xA0740/0x75A60/0xE6B30 (prefetch list), 0xA1260..0xA1356 and
  0xA1DA1..0xA1E13 (ScriptStep/prefetch callers), 0x96615 (text labels/read flags), 0x7C6D0
  (history restore), 0x6FEC0/0x6F470/0x737D0/0x73980/0xEACF0/0xEAC80 (save image), 0x121030 (save
  file write), 0x126140 (dir name), 0xF4BE0/0xF4D60/0xF4830 (timing), 0x11E7A0 (pad), 0xCF240
  (pointer), 0x117E40/0x10E660/0x1025C0 (audio ports), 0xEF3F0/0xF0E70/0xD2BE0 (blend/effects),
  0x106330/0x106F60 (shader registration), 0xD1F40 (archive slots), 0xC6980 (fonts), 0xC29B0
  (UI text), 0x41BA0 (image table), 0x3ED90 (language), 0x3EAC0/0x3E6E0 (config), 0xEE550
  (capture policy).
- `pytest tools/tests/test_eboot.py`: 19 passed, 1 skipped (no save file) in about 6 s; with
  `TSUKI_GAME_DIR=/nonexistent`: 5 passed, 15 skipped. `mypy --ignore-missing-imports` and
  `pyflakes` clean on `tools/eboot` + the test.

---

## 12. Changes after the independent review

| review finding | resolution | proof |
|---|---|---|
| Replay interpreter has 42 names, not 35 (major) | Confirmed. `eboot.scriptvm` was rewritten: concrete vectorised evaluation instead of a syntactic tree walk. 42 entries incl. STCU/STCW (masked compare at 0x9F158), STFC, STFI, STFO, STMV, STT2; table 5.4. The function is a look-ahead prefetcher, renamed `Script_Prefetch` | 0 differences against the reviewer's brute force (main: all 101,416 names; prefetcher: all but `Z`, where the new result is finer: `ZY**`); `test_script_vm_prefetch_table` |
| UI text export drops 63 ja strings with U+3000 (major) | Confirmed. The export now reads the engine's own `ui_text[5][1499]` pointer table (RELRO 0x41D5F0, getter 0xC29B0) and filters nothing; failures raise | 7,495 entries exported, 63 ja strings with U+3000 present (`test_ui_text_table`) |
| Fifth (Korean) UI block undocumented; NULL entries | Confirmed and explained: `NULL` is id 0 of each language column; ko = language 4 with placeholders (9) | table dump, worked example in 9 |
| image_info +0x2C is not the allpac2 size; +0x08 is not the own name for 3,529 records; 14,775 slots (major) | Confirmed; 5.5 corrected and points to images.md 8 / archives_ui_movies.md 4 | all 14,744 records checked (`test_image_table_fields`) |
| Pointer Y clamp is 0..447 (640x448), not 0..359 (major) | Confirmed; 8.5 and RUNTIME_REQUIREMENTS item 20 corrected | `test_pointer_clamp` |
| Shader header is 0x24 bytes; stage flag is byte 0x0C (major) | Confirmed; 4.2 table, shaders.py docstring; `find_blobs` now validates byte 0x0C | byte census over 67 blobs; `test_shaders` |
| transition 2DTex PS has no mask (minor) | Confirmed; 4.2/4.4 corrected | reflection names of blobs #0/#1 (`test_shaders`) |
| 1000 commands per call, not per frame; fast-forward loop (minor) | Confirmed; 3.3 describes the caller loop 0xA12B9..0xA1351 and that every segment counts | disassembly 0xA1260..0xA1356 |
| Replay loop control undocumented (minor) | Documented in 5.4 (n > 0x3A exit, count <= target, forced exit, F/Z/G restore) | disassembly 0x9B760..0x9B915, 0x9FF30, 0xA017F..0xA01D8 |
| Effect tables use two index bases; id 29 strength 0.0 (minor) | Confirmed; 4.3 rewritten with the exact formula and table | `test_screen_effect_tables` |
| Blend gate covers every mode, not only mode 0 (minor) | Confirmed; 4.3 pseudocode | disassembly 0xF0EB2..0xF0FC2 |
| R_X86_64_64 targets are not malloc/free (minor) | Confirmed; 2.1 lists the six symbols | `test_imports_fully_resolved` |
| 27 IMPORT_LIB entries, 26 used (wording) | Confirmed; 2.1/11 reworded | `test_imports_fully_resolved` |
| names_allpac2 equals allpac2.nam only without extension; nam has 14,746 records (minor) | Confirmed; 5.5 | `test_embedded_name_tables_match_allpac2`, nam dump |
| `_Z` digits must be lower-case hex; line minimum 1; readmax bound is a signed upper bound only (minor) | Confirmed; 5.6 | unicorn emulation (`emu_z.py`), census of 46,810 labels |
| Variable writes unchecked; Z operands not written (minor) | Confirmed; 5.5 | `test_wkst_variable_writes` (unicorn) |
| Command count 1,036,358 VM-style (`_STTR,0,26);` typo) (minor) | Confirmed; counting now follows the VM's segmentation | `test_script_vm_against_scripts`, `cmd_counts.py` |
| Save tables do not cover every byte; savedata.py mislabels +0x738 (minor) | Confirmed; 7.3/7.4 and `SYSTEM_FIELDS`/`SLOT_FIELDS` now tile both blocks; +0x738 = readmax | `test_savedata_layout_constants` |
| savedata CLI prints the padded size (cosmetic) | Fixed: `SaveImage.size` is the real input size | `test_savedata_reports_real_size_of_short_file` |

No reviewer claim was found to be wrong.
