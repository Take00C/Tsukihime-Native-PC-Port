# ADV system state: history, save / resume, rewind, backlog, auto / skip, config, input actions

Scope: everything TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999, app 01.01) keeps
between lines, scenes and sessions, and how it restores it. This covers the history ring and
the 0x3A10-byte history record, the 0x1A08-byte scene state inside it, the 59 places that push
a record, the `_ATSV` command, the scene stack, the save-slot states and the load path, rewind,
previous / next scene, the backlog, auto mode, skip / fast-forward, the 0x140-byte config block
and its menu, and the 27 input actions. It answers FORMATS.md P1 items 8 and 10 and the
"27 action masks" part of P0 item 4, and RUNTIME_REQUIREMENTS items 8-11, 20, 22 and 23.

Python reference: `tools/tsuki/misc_adv_system_state.py` (field maps, readers, save checker,
CLI) and `tools/tsuki/misc_adv_system_state_emu.py` (unicorn harness that runs the game's own
code to check the maps). Tests: `tools/tests/test_misc_adv_system_state.py`.
Samples: `samples/misc_adv_system_state/` (`tables.md`, `history_fields.json`,
`push_sites.json`, `scene_state_fields.json`, `config_fields.json`, `config_pages.json`,
`actions.json`, `engine_made_save.md`).
Scratch tools and logs: `scratch/misc_adv_system_state/` (`push_map.py`, `restore_map.py`,
`kind0_effects.py`, `pushsites_cfg.py`, `atsv_forms.py`, `scene_state_fields.py`,
`make_engine_save_md.py`, `memscan.py`, `histscan.py`, `dis/*.asm`).
Alias document: `misc_adv_system_state.md`. The file names follow this run's assignment;
critic_gaps.json names `tools/eboot/advstate.py`, `tests/test_advstate.py` and
`samples/adv_state/` for the same content.

Confidence labels:

- **[V-emu]**: the game's own function was run under unicorn on random RAM and every byte of the
  claim was compared. History_Push: 13 seeds in the tests (41 in total). History_Restore: all
  kinds 0..9, 12, 13 with both values of `from_history`, 2 to 20 seeds per kind. Slot_Store,
  QuickSave_Store, SaveImage_Commit and Slot_Load -> 0x7E790 -> History_Restore on images made
  by the game's own code. The tests repeat these checks.
- **[V]**: checked on the eboot data (tables, constants) or on all 549 scripts by a test.
- **[E]**: read from the disassembly (text VA; file offset = VA + 0x2280; data VA = file
  offset - 0x365FA0 + 0x600000).
- **[H]**: hypothesis.

No retail save exists on this machine (the shadPS4 folder `savedata/` is empty, checked
2026-10-25). The save part was checked on save images produced by the game's own code
(section 7.6). `check_save_image` (7.7) runs the acceptance checks on a real save as soon as one
exists.

Section 13 lists what changed after the review of the first version, with the proofs.

---

## 1. State inventory

| state | RAM | size | lifetime | section |
|---|---|---|---|---|
| history ring | `*0x120A6E0` (heap, 808 records allocated, 800 used) | 800 x 0x3A10 | session; cleared by every load | 2-4 |
| ring indices | 0x120A6EA serial, 0x120A6EC newest (`cur`), 0x120A6EE next, 0x120A6F0 wrapped, 0x120A6F2 oldest, 0x120A6E8 rewind cursor | i16 each | session | 2.2 |
| scene stack | 0x120A700, top index i16 0x164B1C0 (-1 = empty) | 300 x 0x3A10 | session | 5.3 |
| scene-start counters | dword 0x174DE04 (2 at a scene start, -1 per message), dword 0x174DE08 (1 right after a slot load) | i32 | session | 2.5, 5.3 |
| slot staging | 0x12031E0 (slot image: state A = newest page-start record, state B = scene-stack top) | 0x7108 | session, kept current while playing | 7.1 |
| save slots | 0xE24600 `slots[140]`: 0..99 manual, 100..138 auto-save FIFO, 139 quick save | 140 x 0x7108 | persistent | 7 |
| system block | staging 0x1201660 (config, G[0..499], unlock bits, readmax, latest slot) | 0x18AC | persistent | 7.5, engine.md 7.3 |
| config | 0x66C578 | 0x140 | persistent (system block +0x04) | 9 |
| script state | F 0x164B1D0, Z, G; script_ptr 0x16543B0; script 0x86AA18 | | in each record (F partly) | 2 |
| scene state | 0x16500C0 and the committed copy 0x1651AC8 | 2 x 0x1A08 | in each record | 2.6 |
| pending layer change | F991 (word 0x164B98E): 1 between the first layer command and the next commit point | u16 | in each record | 2.3 |
| ADV mode words | 0x17DB230 auto/skip latch, 0x17DB232 skip active, 0x17DB236 log shown, 0x17DB23A blockers, 0x17DB23E window hidden; 0x16541F8 scene-jump request; 0x120A6D2 scene-jump running | u16 | session (cleared by the scan load 0xA3050) | 6 |
| game input words | `*0xDC7BC0` (0x203B170): +0x10 held, +0x18 pressed-or-repeat, +0x20 newly pressed | u64 | per frame | 10 |

---

## 2. History record (0x3A10 bytes) [V-emu unless noted]

One record is written before every state-changing command (not only before text lines). It
captures the state **before** the command, so restoring records newest-first undoes the commands
one by one. The same layout is used by the ring, the scene stack and the two states of a save
slot.

### 2.1 Field map

`push source` = the RAM History_Push (VA 0x7AF90) copies; `restore` = what History_Restore
(VA 0x7C6D0) does with it. Every byte is listed (`HISTORY_FIELDS`; a test checks that the
table tiles 0..0x3A10 and the emulation checks every source and destination).

| off | size | name | push source / writer | restore |
|---|---|---|---|---|
| +0x0000 | 4 | ring_index (i32) | ring clear loops write the record's own index (0x7AF20, 0xA2D50, 0xA30F0, 0x7EA50); not touched by the push | compared by the slot / scene loader 0x7E790 |
| +0x0004 | 4 | serial (i32) | i16 counter 0x120A6EA (then counter+1, reset to 0 when it would be <= 0); the next ring record gets -1 | >= 0 = valid |
| +0x0008 | 8 | cmd_offset (u64) | rdi - `*0x174D640`. Script_Step passes the address of the statement's name letter minus 2, i.e. the `;` before its `_` (0x95F2B `lea r12, [r14-2]`) | script_ptr = base + value for every kind: **the command runs again** unless the kind-0 re-display (from_history = 1) moves it to +0x10 |
| +0x0010 | 8 | resume_offset (u64) | rsi - base (Script_Step: the end of the statement) | read only by the kind-0 re-display with from_history = 1 (rewind) |
| +0x0018 | 2 | script (u16) | 0x86AA18 | reload allscr section script+3 (3 if >= 600) when it differs, decrypt with NOT |
| +0x001A | 2 | text_block (i16) | low word of i32 0x1654344 | 0x1654344 (and 0x165434C for the kind-0 re-display), sign-extended |
| +0x001C | 2 | text_line (i16) | low word of 0x1654348 | 0x1654348 (and 0x1654350) |
| +0x001E | 1 | speaker (i8) | low byte of u16 0x164FCF6; voice play overwrites it | 0x164FCF6 sign-extended; name-colour index (config +0x2A = 2) |
| +0x001F | 1 | i8 | low byte of 0x1654208 | 0x1654208 sign-extended |
| +0x0020 | 4 | unused | never written | |
| +0x0024 | 2 | kind (u16) | dx argument of the push (2.3) | selects the restore tail (3.2) |
| +0x0026 | 2 | bgm_track (i16) | 0x16545D0 | 0x16545D0; kind 2 restarts it |
| +0x0028 | 0x28 | loop_se_ids (i16[20]) | 0x1654240 | 0x1654240; kinds 3..5 restart the channels |
| +0x0050 | 2 | unused | | |
| +0x0052 | 2 | u16 | 0x16500BE | 0x16500BE |
| +0x0054 | 4 | unused | | |
| +0x0058 | 0x1A08 | scene_state | memcpy 0x16500C0 (after the push wrote +0x19A3, 2.2) | memcpy back for every kind (kind 0 then rewrites +0x1982 / +0x1984, 3.2), pushed to the renderer layer table 0xDDB5E8 (stride 0x2C8) |
| +0x1A60 | 0x1A08 | scene_committed | memcpy 0x1651AC8 | kinds 1 and 9: 0x1651AC8 = this, the renderer is rebuilt from it, then scene_state = +0x58 again |
| +0x3468 | 0x40 | bytes | 0x174DAB0 | 0x174DAB0 |
| +0x34A8 | 2 | u16 | 0x174D940 | 0x174D940 |
| +0x34AA | 0x4E | f_u16 | F[list 0x2160E0[k]], 39 entries, exact | F[...] = value |
| +0x34F8 | 2 | unused | | |
| +0x34FA | 6 | f_bool_bits | bit k = (F[list 0x216580[k]] != 0), 43 entries (index <= 1200). Slot_Store sets the bit of F1118 in the slot copy; the quick save does not (7.2) | F[...] = bit (restored last) |
| +0x3500 | 1 | unused | | |
| +0x3501 | 0x12C | f_s8 | low byte of F[list 0x2165F0[k]]; 276 entries used (+0x3501..+0x3614), list ends at the first negative entry | F[...] = sign-extended byte |
| +0x362D | 3 | unused | | |
| +0x3630 | 4 | voice_id (i32) | -1; voice play stores the line's voice (inline in the Z handler 0x92380; helper 0x7B520) | 0x1654204 (and 0x16541FC, 0x1654200 for kinds != 0) |
| +0x3634 | 2 | voice_character (i16) | -1; voice play [E] | volume lookup when a load replays the voice (7.3) [E] |
| +0x3636 | 2 | effect_active_bits | bit i = effect slot i active (0x5C660: dword 0xDF1830 + 0x134 i), 14 slots | slots active now but clear here are stopped (0x5C080) |
| +0x3638 | 2 | effect_blocking_bits | bit i = 0x5C680(i) (slot active and its motion flag 8 clear) | != 0: not a rewind stop, no slot snapshot |
| +0x363A | 2 | flags | bit 0 = word 0xDF08DC (WNTY: last message of the page) | not read |
| +0x363C | 2 | bgm_volume (i16) | 0x1654238 | 0x1654238 |
| +0x363E | 2 | config_04 | config +0x04 | if config +0x4C != 0: config +0x04 = value (<= 0x100) [E] |
| +0x3640 | 1 | u8 | not touched by the push; voice play writes 0, helper 0x7B590 writes byte 0x165420A [E] | not read |
| +0x3641 | 1 | i8 | not touched by the push; helper 0x7B560 writes byte 0x165420A into the record about to be pushed [E] | 0x165420A sign-extended |
| +0x3642 | 0x14 | i8[20] | low byte of u16 0x1654270[i] | 0x1654270[i] sign-extended |
| +0x3656 | 1 | unused | | |
| +0x3657 | 0x14 | i8[20] | low byte of u16 0x16542A0[i] | 0x16542A0[i] sign-extended |
| +0x366B | 1 | unused | | |
| +0x366C | 0x28 | i16[20] | 0x16542D0 | 0x16542D0 |
| +0x3694 | 0x28 | i16[20] | 0x1654300 | 0x1654300 |
| +0x36BC | 0x14 | u8[20], stale | **not written**: the push copies record -> RAM 0x1654330[i] (0x7B3BA; engine bug) | 0x1654330..0x1654343 |
| +0x36D0 | 0x1E0 | channels (20 x 0x18) | 0x16543F0 entry bytes +0..+2, +4..+5, +8..+0x15 (not +3, +6..+7, +0x16..+0x17) | same bytes back |
| +0x38B0 | 0x32 | window state | 0x164FD00 | 0x164FD00 |
| +0x38E2 | 0x1C | window state 2 | 0x164FD40 | kind 0: copied to 0x164FD40 (moved on to 0x164FD60 by the re-display); other kinds: byte 0x164FD40 = 0; then 0x16541EC = 0x16541F0 = (byte 0x164FD40 != 0) |
| +0x38FE | 0xFA | message text | push writes byte 0. After the push: `_ZM` 0x92380 / `_MSAD` 0x92B60 / 0x7B5C0 copy 0x174D9B0 (0xF8 bytes) here and clear +0x39F6; `_ATSV(n)` copies the current message, `_ATSV()` its argument 1 (2.5) | 0x174D9B0..0x174DAA9 |
| +0x39F8 | 0x18 | unused | | |

The F index lists are in rodata (`read_history_lists`): 39 u16 entries, 43 bool entries,
276 s8 entries (indices in vm_vars.py `HISTORY_U16/BOOL/S8`, owned by the variables work).
Two indices are in two lists, and the restore order u16, s8, bool decides which copy wins [V]:

- **F991** (pending layer change, 2.3) is in the u16 and the bool list. The bool copy is restored
  last, so F991 comes back only as 0 or 1.
- **F967** is in the u16 and the s8 list (the s8 copy wins). After the list copies the restore
  applies a rule (0x7D513..0x7D550) that only acts when F967 > 0: if F967 != scene word
  0x1651A66 (scene_state +0x19A6) + 1, then F967 = 0, 0x495C0(-1), 0x1653E00 |= 0x200 and
  0x5C6C0(2, 1). Values <= 0 are kept. 0x7E790 (slot load, previous scene) then sets F967 =
  +0x19A6 + 1 again [V-emu].

The 0x36BC bug: every push overwrites the live bytes 0x1654330[0..19] (passed as the volume to
0xAD60 when a loop SE is restarted) with whatever the target ring slot held before (zero
after allocation, or a value from a loaded record). A port should save 0x1654330 in this field
instead [V-emu].

### 2.2 Push algorithm (History_Push 0x7AF90) [V-emu]

```
History_Push(cmd_ptr, resume_ptr, kind):
    if scan_mode (byte 0x174DE48) or F929 != 0 or byte 0x16541F4 != 0: return
    if F1102 != 0 and kind not in (0, 11): return
    k = next (0x120A6EE); rec = ring[k]
    byte 0x174D9A8 = 0                              # "restored from history" flag
    cur (0x120A6EC) = k
    fill rec as in 2.1 (serial = counter; counter = counter+1, or 0 when that is <= 0)
    byte 0x1651A63 (scene_state +0x19A3) = low byte of F995        # 0x7B283 / 0x7B2A2: a live
                                                    # write, then copied with the block to +0x58
    0x1654330[0..19] = rec[+0x36BC..]               # the copy in the wrong direction
    next = k+1; if next >= 800: next = 0, wrapped (0x120A6F0) = 1
    if next == oldest (0x120A6F2): oldest = next+1 if next < 799 else 0
    ring[next].serial = -1
```

After the push the text commands fill +0x38FE from 0x174D9B0, and the voice command fills
+0x3630/+0x3634/+0x001E of the newest record (cur), except while 0x174D9A8 is set (a line
re-displayed by a rewind).

### 2.3 Record kinds (record +0x24 = push argument dx) [E push sites, V-emu restore tails]

`PUSH_SITES` lists all 59 direct calls of History_Push (2.4). "Dead" sites are in functions that
v01.01 never references: no call, jmp, rip-relative reference or relocated data pointer in the
`eboot.disasm` index, and no E8 / E9 to them in a byte scan.

| kind | live pushers | restore tail (jump table 0x20BD9C) |
|---|---|---|
| 0 | the `_ZM` message marker: Z handler 0x92380, site 0x9291F, before the text is shown (the second site 0x935CA in 0x93370 is dead) | 3.2: F931 / window words, then (rewind only) re-display the text and resume at +0x10 |
| 1 | **the first layer change since the last commit**: when F991 == 0 the command pushes kind 1, sets F991 = 1 and copies scene_state to the committed copy 0x1651AC8. Commands: STCH / SCH2 0x81760, STCC 0x82D30, STBR 0x830D0, STCL 0x83350, STCP 0x83850, STRT 0x83940, STZ4 0x83B70, STRC 0x84190, STRR 0x84520, STGS 0x84960, STRA 0x86EE0, STNS 0x87100, and the shared helper 0x7F410 (STEC 0x10390, SNX2 0x10610, SNX3 0x106E0, SQK2 / SQS2 / SHK2 / SHS2 / SSW2 / SSS2 0xB6B20.., FLNT .. FCSP 0xB7720.., STMA 0xC0330, SPMA 0xC0570). FADS 0x81520 pushes kind 1 unconditionally, then commits | committed scene copy (+0x1A60), layer refresh 0xE98D0, 0x7DD10 |
| 2 | music: MPLY 0x87250, MFAD 0x87660, MVOL 0x87940, MSTP (Script_Step 0x978C8) | restart BGM 0x16545D0 at volume 0x1654238 (0x98C0), or stop (0x9A90) |
| 3 | sound effects: SEPL / SEFI 0x893F0, SEVL 0x89B50, SEFD 0x89E40, SEPN 0x8A340, SEP3 0x8A5E0, SEAC 0x8A880, helper 0x88160, SESP (Script_Step 0x99723) | for the 20 channels with id >= 0 and 0x16542A0 > 0: 0xAA80, 0xAD60(vol 0x1654330), 0xA300(id, pitch 0x1654270), 0xA9E0, 0xAA50; others 0xAAB0 (stop) |
| 4, 5 | no push site | as 3 |
| 6 | scene start: `_ATSV()` (0x9853E), `_ATSV(8)` (0x9788F), `_ATSV(n)` with n != 8 and n & 3 in {0, 3} (0x97EA4); followed by SceneStack_Push (2.5) | if scene_stack[top].ring_index == this record's index: top -= 1 (not below -1), 0x174DE04 = 0x174DE08 = 0 |
| 7 | **choice save point**: `_ATSV(n)` with n & 3 == 1 (0x97EA4; kind = `6 \| (n & 3 == 1)`). In the scripts these are the 83 `_ATSV(1)` right before the choices. The record keeps the current message (the question) | as 6, then 0x7B790(record text) |
| 8 | only the dead site 0x93983 (0x937C0): **never pushed** in v01.01 | nothing extra |
| 9 | **commit point** while a layer change is pending: when F991 != 0, the committed copy = scene_state, push kind 9, F991 = 0. Done first by VPLY / VPL2 (0x968FD), the Z markers (0x9846B), WT__ / WTTM (0x9AC57) and MSAD (0x9AF0B) | as 1 |
| 12 | SELD (choice start, Script_Step 0x9B288) | outside the table: common part only |
| 13 | SELR (choice line, Script_Step 0x993A0) | common part only |

Kinds 1 and 9 bracket a batch of layer changes: the first change saves the scene before the batch
(kind 1, cmd = resume = the changing statement), and the next message / voice / wait records the
changed scene as committed (kind 9). Undoing both returns to the scene before the batch.

Mask 0x3001 at 0xA21E6 (kinds 0, 12, 13) only decides which record **at the rewind cursor** is
not undone before the walk starts. The rewind **stop** is a different test (0xA2259..0xA229B):
kind 0, effect_blocking_bits == 0 and the record's F823 <= 1 (5.1). The backlog lists kinds 0, 8,
11 and 12 (mask 0x1901 at 0x3C032); kinds 8 and 11 are never pushed in v01.01.

### 2.4 The 59 push sites [E, V test]

`scan_push_sites()` finds every E8 call of 0x7AF90 and decodes edx from the instructions before
it; a test compares the result with `PUSH_SITES` (all 59 rows, including the computed kind of
0x97EA4: `xor edx, edx; cmp r15w, 1; sete dl; or edx, 6`). The commands per site come from a
control-flow walk of Script_Step (0x95E60) from every command handler of
`samples/engine/vm_commands.json` (relative jump tables resolved; `pushsites_cfg.py`).

- 38 live sites: kind 0 x1 (0x9291F), kind 1 x14, kind 2 x4, kind 3 x9, kind 6 x3 (0x9788F,
  0x9853E, 0x98A4B), kind 6/7 x1 (0x97EA4), kind 9 x4, kind 12 x1, kind 13 x1. The kind-6 site
  0x98A4B (ATSV with no argument and n & 3 != 2) cannot be reached, because an `_ATSV()` always
  has low = 2.
- 21 dead sites in 19 functions: 0x7F460, 0x7F4B0, 0x7F510, 0x7F680, 0x84B70, 0x84F60, 0x85110,
  0x852F0, 0x85470, 0x85580 (2 sites), 0x86A30, 0x86B20 (2), 0x87820, 0x878F0, 0x8A100, 0x93370,
  0x937C0, 0x94D40, 0x94F50.

Full table: `samples/misc_adv_system_state/push_sites.json` and `tables.md`.

### 2.5 `_ATSV` (Script_Step 0x95F5D): scene start, choice save point, auto save [E; V forms]

`_ATSV` is the command that pushes kinds 6 and 7 (the first version of this document named
these sites "Script_Step scene start" and put 0x97EA4 under kind 0). Argument rules: argc = the
number of arguments (0 for `()`, else 1 + the number of commas); n = inline int of argument 0;
when argc == 0, n = low = 2, else low = n & 3. ld = word 0x174DE08 (1 right after a slot load,
7.3) is read at entry and set to 0 at the end (0x98BD1). In scan mode the command yields
(0x9B516).

```
ATSV(args):
    if next-scene skip running (0x120A6D2 != 0):
        0x7E620()                       # restart BGM 0x16545D0 and the loop SEs
        0x120A6D2 = 0                   # the skip ends here (5.2)
        if argc > 0 and low == 1: 0x7B790(0x174D9B0); goto L_n
    if low == 2:                                                 # _ATSV() and _ATSV(2|6|10..)
        0x16543C0 = argument 1 (up to ')' ';' ','; empty when absent)
        History_Push(stmt, end, 6)                               # 0x9853E
        if not re-displayed (byte 0x174D9A8 == 0):
            text buffer 0x174D9B0 = 0x16543C0; record +0x38FE = the same; +0x39F6 = 0
            Staging_SetSnapshot if allowed (7.1); dword 0xDF29D4 = 0
        goto SSP
L_n:
    if n == 8:                                                   # _ATSV(8)
        if 0xD1CD0(1) != 0: goto END
        History_Push(stmt, end, 6)                               # 0x9788F
        0x7B5C0("")                     # empty message text into the record, snapshot (7.1)
        SceneStack_Push(); goto AUTOSAVE
    History_Push(stmt, end, 6 | (low == 1))                      # 0x97EA4: kind 7 for low == 1
    if not re-displayed:
        record +0x38FE = current text 0x174D9B0 (0xF8 bytes); +0x39F6 = 0
        Staging_SetSnapshot if allowed; dword 0xDF29D4 = 0
    if n & 4 == 0: goto SSP
    if ld != 0: goto END
    goto AUTOSAVE
SSP: SceneStack_Push()                  # 0x7E2E0 (5.3)
     if ld != 0: goto END
AUTOSAVE:
     if 0x67460() == 0 and n == 8: AutoSave 0x73B70()           # 0x98BC5 (7.2)
END: 0x174DE08 = 0
```

The AutoSave block also contains `and ax, [config + 0x40]` (0x98BB8), but control only reaches it
when r13w == 8 and at the same time argc == 0 or n == 2, which cannot both hold. So config
+0x40 has no effect, and `_ATSV(8)` auto-saves unless it runs right after a load (ld != 0) or
0x67460() != 0 [E].

Forms in the 549 scripts (`atsv_forms.py`, test `test_atsv_forms_in_scripts`) [V]:

| form | count | path | kind |
|---|---|---|---|
| `_ATSV();` | 313 | 0x9853E, record text = empty argument 1 | 6 |
| `_ATSV(1);` | 83 | 0x97EA4, record text = the current message | 7 |
| `_ATSV(8);` | 2 | 0x9788F, scene stack, auto save | 6 |

So every scene start is a kind-6 record on the scene stack (rewind, previous scene and saves use
it), and every story choice is preceded by a kind-7 record that keeps the question text.

### 2.6 Scene state (0x16500C0, 0x1A08 bytes) [E static scan; V tiling, V offsets]

The record stores the scene state twice: +0x58 = the script-side state, +0x1A60 = the committed
copy (0x1651AC8, see kinds 1 / 9). `SCENE_STATE_FIELDS` maps every byte: 51 per-layer arrays
(40 entries each), 29 scalar globals and 4 gaps (8 bytes) that no instruction addresses
directly. The array offsets and entry formats are the layer work's
(`tsuki.misc_layer_state_and_motions.SCENE_ARRAYS`, read-only here; layers.md is not written yet),
and a test checks that both maps agree. Writers come from the static memory-operand scan
`scratch/misc_adv_system_state/memscan.py` (rip-relative operands and lea-base registers
followed through the function) via `scene_state_fields.py`. A test decodes every listed writer
of a global (93 instructions, 83 rip-relative) and checks that it stores into the field.

Whole-block writers (not repeated per field): History_Restore memcpy 0x7C9AB (every kind),
memmove 0x7D65E (kinds 1 / 9), 0x7D840; 0x7E4E0 (0x7E52A); the scene reset 0x789F0 and the
table-driven clear 0x847F0 (SQS2 / SHS2 / SSS2) write field by field.

Per-layer arrays (entry format in Python struct notation):

| off | entry | name | set by (layer work) | writer functions (static scan) |
|---|---|---|---|---|
| +0x0004 | h (2 B) | pic | STCH SCH2 STCC | 0x789F0, 0x7DD10, 0x81760, 0x82D30, 0x847F0, 0x84F60, 0x85110, 0x852F0, 0x85EB0, 0x85F90, 0xA3870, 0xBE4C0 |
| +0x0054 | h (2 B) | blend | STBR | 0x789F0, 0x81760, 0x82D30, 0x830D0, 0xBE4C0 |
| +0x00A4 | B (1 B) | flags | STBR a3=1 sets 0x80, SACL 0x40 | 0x789F0, 0x81760, 0x82D30, 0x830D0, 0x95E60 |
| +0x00CC | b (1 B) | fx_mode | STEC (and STCH from the global word 0x164B97C) | 0x10390, 0x789F0, 0x81760, 0x82D30, 0xA3870 |
| +0x00F4 | h (2 B) | fx_a | SNX2 | 0x10610, 0x789F0, 0x81760, 0x82D30, 0xBE4C0 |
| +0x0144 | b (1 B) | fx_b | SNX3 | 0x106E0, 0x789F0, 0x81760, 0x82D30, 0xBE4C0 |
| +0x016C | h (2 B) | x | STCH SCH2 STCL | 0x789F0, 0x81760, 0x82D30, 0x83350, 0x85F90, 0xA3870, 0xBE4C0 |
| +0x01BC | h (2 B) | y | STCH SCH2 STCL | 0x789F0, 0x81760, 0x82D30, 0x83350, 0x85F90, 0xA3870, 0xBE4C0 |
| +0x020C | h (2 B) | alpha | STCH SCH2 STGS | 0x789F0, 0x81760, 0x82D30, 0x84960, 0x84F60, 0x85110, 0x852F0, 0x85470, 0x85580, 0x85EB0, 0xA3870, 0xBE4C0 |
| +0x025C | b (1 B) | state | STCH SCH2 (0), engine | 0x789F0, 0x7DD10, 0x81760, 0x85470, 0x85580, 0x85F90, 0xA3870 |
| +0x0284 | B (1 B) | u284 | copied by STCH src | 0x789F0, 0x81760 |
| +0x02AC | B (1 B) | u2ac | copied by STCH src | 0x81760 |
| +0x02D4 | B (1 B) | u2d4 | copied by STCH src | 0x81760 |
| +0x02FC | h (2 B) | z | STCH SCH2 STCP | 0x789F0, 0x81760, 0x82D30, 0x83850, 0xBE4C0 |
| +0x0350 | q (8 B) | rot | STRT STPV(a3) STCH(reset 0) | 0x789F0, 0x81760, 0x82D30, 0x83940, 0x86440, 0xBE4C0 |
| +0x0490 | h (2 B) | scale | STCH SCH2 | 0x789F0, 0x81760, 0x82D30, 0x84B70, 0x85F90 |
| +0x04E0 | h (2 B) | anchor_x | STCH SCH2 STPV | 0x789F0, 0x81760, 0x82D30, 0x84B70, 0x86440, 0xBE4C0 |
| +0x0530 | h (2 B) | anchor_y | STCH SCH2 STPV | 0x789F0, 0x81760, 0x82D30, 0x84B70, 0x86440, 0xBE4C0 |
| +0x0580 .. +0x0850 | h (2 B) | u580, u5d0, u620, u670, u6c0, u710, u760, u7b0, u800, u850 (ten arrays, 0x50 apart) | copied by STCH src | 0x789F0, 0x81760 (all but u850); 0x85F90 (u620..u710, u7b0, u800); 0x84F60 (u7b0, u800); 0x7E620, 0x85110, 0x852F0, 0x85470, 0x85580, 0x85EB0 (u800); u850: 0x789F0 only |
| +0x08A0 | h (2 B) | zoom_x | STZ4 a3; STCH SCH2 reset to 1000 | 0x789F0, 0x81760, 0x82D30, 0x83B70, 0xBE4C0 |
| +0x08F0 | h (2 B) | zoom_y | STZ4 a4; STCH SCH2 reset to 1000 | 0x789F0, 0x81760, 0x82D30, 0x83B70, 0xBE4C0 |
| +0x0940 | b (1 B) | zoom_pivot | STZ4 a7; STCH SCH2 reset to 0 | 0x789F0, 0x81760, 0x82D30 |
| +0x0968 | h (2 B) | mask | STCH a11 | 0x789F0, 0x81760, 0x82D30 |
| +0x09B8 | h (2 B) | sttr | STTR | 0x789F0, 0x81760, 0x82D30, 0x95E60 |
| +0x0A08 | i (4 B) | parent | SBAS | 0x789F0, 0x81760, 0x82D30, 0x95E60 |
| +0x0AA8 | b (1 B) | soxy_x | SOXY a1 | 0x789F0, 0x81760, 0x82D30, 0x95E60 |
| +0x0AD0 | b (1 B) | soxy_y | SOXY a2 | 0x789F0, 0x81760, 0x82D30, 0x95E60 |
| +0x0AF8 | 4h (8 B) | crop | STCH a12..a15 | 0x789F0, 0x81760, 0x82D30, 0x84190, 0xBE4C0 |
| +0x0C38 | H (2 B) | a16 | STCH a16 | 0x789F0, 0x81760, 0x82D30, 0xBE4C0 |
| +0x0C88 | B (1 B) | a17 | STCH a17 | 0x789F0, 0x81760, 0x82D30, 0xBE4C0 |
| +0x0CB0 | h (2 B) | angle | STCH a19, STRR | 0x789F0, 0x81760, 0x82D30, 0x84520, 0xBE4C0 |
| +0x0D00 | 24s (24 B) | motion | STMA SPMA | pointer stores: STMA 0xC0330 (lea 0xC048F / 0xC0511), STCH 0x81760 (0x826BA, 0x827C3), STCC 0x82D30 (0x82F62), 0xBE4C0 |
| +0x10C0 | 2B (2 B) | shake_kind | SQK2 (slot 0) SHK2 / SSW2 (slot 1) | 0x789F0, 0x81760, 0x82D30 |
| +0x1110 | 2h (4 B) | shake_fade | SQK2 SHK2 SSW2 a5 | 0x789F0, 0x81760, 0x82D30 |
| +0x11B0 | 2h (4 B) | shake_time | SQK2 SHK2 SSW2 a4 | 0x789F0, 0x81760, 0x82D30 |
| +0x1250 | 12h (24 B) | shake_par | SQK2 SHK2 SSW2 | 0x81760, 0x82D30 |
| +0x1610 | h (2 B) | stra | STRA | 0x789F0, 0x81760, 0x82D30, 0x86EE0 |
| +0x1660 | B (1 B) | stns | STNS | 0x789F0, 0x82D30, 0x87100, 0xBE4C0 |
| +0x1688 | B (1 B) | stns_b | STNS | 0x789F0, 0x82D30, 0x87100, 0xBE4C0 |
| +0x16B0 | 18s (18 B) | ext | STCH STPV STEC | 0x81760, 0x84B70, 0x86440, 0xBE4C0 |
| +0x19AB | B (1 B) | u19ab | copied by STCH src | 0x789F0, 0x81760 |
| +0x19D3 | B (1 B) | u19d3 | copied by STCH src | 0x81760 |

Scalar globals and gaps (writer instruction VAs; 0x789F0 = the scene reset, 0x7E4E0 = the
re-sync after a restore, 0x847F0 = the table-driven clear):

| off | size | name | meaning | writer instructions |
|---|---|---|---|---|
| +0x0000 | 2 | scene_pic | i16 scene-level picture id (layer work: pic-seen bit + 0x49C20 at the commit); 0xA3810 (from 0x5B400) | 0x78AD2, 0x7E566, 0x7E5E0, 0x848A4, 0x848C2, 0x84912, 0xA3816 |
| +0x0002 | 2 | colour_mode | i16 colour-mode word -> render 0xDE2658 (0x49C30 at the commit; 2 = grey, 3 = sepia, compositing.md 9) | 0x78AD2, 0x7E566, 0x7E5E0, 0x84912, 0xA381F |
| +0x034C | 4 | gap | not addressed by any instruction (between the z and rot arrays) | block copies only |
| +0x1980 | 2 | pic_table_index | i16 index into the 12-byte table 0x873050 -> 0x493E0 at the commit (layer work) | 0x68AF4, 0x78AFD |
| +0x1982 | 2 | text_window_a | i16 copy of +0x1984 (kind-0 restore); `_ZM`, `_MSAD`; read by the HUD 0x53CF0 | 0x78AFD, 0x7D860, 0x7DBBE, 0x7E55A, 0x856EA (dead), 0x92840, 0x92DE0 |
| +0x1984 | 2 | text_window_b | i16 = F931 when F931 >= 0 (kind-0 restore); read by `_ZM` / `_MSAD` / prefetch 0xA0740 [H: message-window style] | 0x78AFD, 0x7D736, 0x7E54A, 0x856EA (dead) |
| +0x1986, +0x1988 | 2 + 2 | commit_49620 | i16 x2 -> 0x49620 at the commit (layer work) | 0x78AFD, 0x7E584, 0x7E5FE |
| +0x198A, +0x198C | 2 + 2 | commit_495f0 a, b | i16 -> 0x495F0 at the commit | 0x78AFD, 0x7E584, 0x7E5FE |
| +0x198E | 2 | commit_495f0 c | i16 -> 0x495F0 at the commit | 0x78AFD, 0x7E575, 0x7E5EF |
| +0x1990 | 8 | reset_1990 | u64, written only by the scene reset, never read | 0x78AED |
| +0x1998 | 2 | reset_1998 | u16, written only by the scene reset, never read | 0x78AF4 |
| +0x199A, +0x199C | 2 + 2 | word_199a / _199c | i16 pair (one dword store); Slot_Load clears it; read by the restore | 0x79119, 0xA2EC3 |
| +0x199E | 2 | word_199e | i16; Slot_Load clears it; read by the restore | 0x7914A, 0xA2ECD |
| +0x19A0 | 1 | byte_19a0 | u8; no writer found; read by the restore and 0x7E4E0 | block copies only |
| +0x19A1 | 1 | byte_19a1 | u8 -> 0x426A0(byte, 0, 0) at the restore | 0x78B05; 0x7F5C0, 0x7F68A (dead) |
| +0x19A2 | 1 | effect_19a2 | u8 screen effect: SNOW 0x81060, FLNT / FLOP / FLSP / FCLN / FCSP 0xB7720..0xB79C0 -> 0xA88B0(byte, 0) at the restore | 0x78E2A, 0x81187, 0xB776C, 0xB782E, 0xB78BC, 0xB7973, 0xB7A4B |
| +0x19A3 | 1 | f995_low | u8 = low byte of F995, written by every History_Push (2.2) | 0x78B0C, 0x7B2A2 |
| +0x19A4 | 1 | byte_19a4 | u8 -> 0x16541E4 at the restore | 0x78E31 |
| +0x19A5 | 1 | gap | not addressed | block copies only |
| +0x19A6 | 2 | f967_base | i16; F967 = this + 1 after a load (0x7E790) and the F967 rule (2.1) | 0x78E3B |
| +0x19A8, +0x19A9 | 1 + 1 | byte_19a8 / _19a9 | u8; no writer found; read by 0x7E790 / 0x7EC30 | block copies only |
| +0x19AA | 1 | byte_19aa | i8 -> 0x56CE0 at the commit (layer work) | 0x78B42 |
| +0x19FB | 1 | gap | not addressed (end of the u19d3 array is +0x19FA) | block copies only |
| +0x19FC | 2 | screen_effect | i16 screen-effect kind 0x1651ABC: SHAK 0x80180, SHAF 0x80AE0, 0x95130 helpers (0x80570, 0x811A0, 0x81290, 0x813A0); SHAS sets 0 (0x99817). Restore: 1 -> 0x42DC0, 2 -> 0x43160, >= 3 -> 0x46270, else 0x433F0(-1) | 0x78B13, 0x8054D, 0x80A60, 0x80BBB, 0x8126C, 0x8138A, 0x814C9, 0x85688 (dead), 0x9531E, 0x99817 |
| +0x19FE .. +0x1A04 | 4 x 2 | screen_effect_p0..p3 | i16 parameters of +0x19FC (same writers) | 0x80527.., 0x80A40.., 0x8124C.., 0x8136B.., 0x814A7.. (see `scene_state_fields.json`) |
| +0x1A06 | 2 | gap | not addressed | block copies only |

The meanings of the per-layer arrays belong to the layer work (layers.md, pending). For save
import and rewind, a port stores and restores the block as a whole [V-emu]. It must reproduce the
three live writes the history code makes: +0x19A3 at every push, and +0x1982 / +0x1984 at every
kind-0 restore.

---

## 3. History_Restore (VA 0x7C6D0)

### 3.1 Common part [V-emu copies, E control flow]

`History_Restore(i, from_history)`. **from_history = 1 only for the three calls in Adv_Rewind**
(0xA22AE, 0xA22EA, 0xA235B). Every other caller passes 0: 0x7E790 (slot load and previous scene,
0x7EA31 / 0x7EAC5) and the backlog jump 0x3CC70 (0x3CD69) [E].

```
History_Restore(i, from_history):
    rec = ring[i]; word 0x1750646 = 0
    text buffer 0x174D9B0 = rec+0x38FE; text fade 0x86AA1C = 1.0; 0x1653E24 = 0
    script_ptr = base + rec.cmd_offset
    scalars and arrays back as in 2.1 (RESTORE_COPIES), scene_state = rec+0x58
    stop effect slots that were not active in the record
    if config+0x4C: config+0x04 = rec+0x363E (<= 0x100)
    if rec.script != current: load and decrypt that script, 0x6B460(-1)
    F lists: u16, then s8, then bool
    window bytes 0x164FD00.. ; 0x164FD40.. only for kind 0, else byte 0x164FD40 = 0;
        0x16541EC = 0x16541F0 = (byte 0x164FD40 != 0)
    cur = i, next = i+1 (wrap at 800 sets wrapped)
    0x42DB0(F1100), 0xA88B0(byte 0x1651A62, 0), 0x16541E4 = byte 0x1651A64, 0xFBA0, 0xF060, 0xEA00
    renderer: per layer 0xF7F0 / 0xF350 and the table copies into 0xDDB5E8 (stride 0x2C8) and
              *0x435220 (stride 0x2C8), 0xC0290, 0xB64B0, 0x49930(layer, zoom..) for 40 layers
    screen transition word 0x1651ABC: 1 -> 0x42DC0, 2 -> 0x43160, >= 3 -> 0x46270, else 0x433F0(-1)
    0x426A0(byte 0x1651A61, 0, 0)
    F967 rule (2.1)
    tail by kind (2.3, 3.2)
    if rec+0x3638 == 0 and kind not in (11, 12) and F823 <= 1:
        Staging_SetSnapshot(rec, top+1, top >= 0 ? &scene_stack[top] : 0)   # 7.1 (a 4th
                                       # argument &scene_stack[top-1] is passed and ignored)
```

### 3.2 Kind tails; the kind-0 tail in full [V-emu, both from_history values]

The tails of kinds 1..13 are in 2.3. Kind 0 is the tail of every slot load (state A is a kind-0
page-start record), every previous-scene restore that lands on a message, and every rewind
target. The first version of this document had not emulated it.

```
kind 0 (0x7D6F2):
    if dword 0x86AA24 != 0: 0x75CD0(1); 0x86AA24 = 0
    word 0x1653AE0 = 0; byte 0x174DE00 = 0
    if F931 >= 0 (signed): scene +0x1984 (0x1651A44) = F931           # 0x7D736
    scene +0x1982 (0x1651A42) = scene +0x1984                          # 0x7D860
    if from_history == 0: done                                         # [rbp-0x54], 0x7D869
    # re-display (rewind only):
    byte 0x174D9A8 = 1; word 0x1654208 = 0; byte 0x16541F4 = 0
    copy the statement text at script_ptr + 0xA (after ";_ZMbbbll(") up to ')' or "@e" / "@n"
        (at most 0x300 bytes) to 0x16537E0, NUL-terminated; 0x1653AE0 = its length;
        0x1653AF0 = a 0x300-byte copy; end = 2 for "@e", 1 for "@n", else 0 with r = 1
    0x76040(0x16534E0, 0x16537E0, 0x300, 1); 0x56150(0, 1); 0x56270(config+0x9D == 1); 0x56140()
    colour: config +0x2A == 1 -> 0xDC6D-based read colour (0x53860), == 2 and voice set ->
        name colour table 0x86A8B0[speaker], else 0xFFFF
    0x1653E80 = 0x1653E84 = 768.0 if byte 0x174DAF0 else 0.0 (0x174DAF0 = 0)
    Text_Layout(14, 0x164FD40, 150, 20, 2, ...): name plate; 0x164FD60 = 0x164FD40 (0x1C bytes);
        byte 0x164FD40 = 0; 0x16541EC = 0x16541F0 = (byte != 0)
    Text_Layout of the message (box 0x1DA x 0xDC; with F1102 != 0 the bubble variant)
    0x16541D8 = 1; 0x16541FC = -1; scene +0x1982 = scene +0x1984 (again, 0x7DBBE)
    0x1653E70 |= 1
    if r and 0x56140() and F799 != 1: end = 2
    end == 1: 0x1653E08 = 0; 0x16541F4 = 1
    else:     0x1653E00 |= (end == 2 ? 0x140 : 0x500) | 0x1653E70
    0x1654344 = 0x165434C = rec.text_block; 0x1654348 = 0x1654350 = rec.text_line
    0x1654200 = -1
    script_ptr = base + rec.resume_offset                               # 0x7DC7A
```

Consequences:

- **A slot load does not re-display the saved line and does not resume at +0x10**: with
  from_history = 0, script_ptr stays at base + cmd_offset, so the `_ZM` statement runs again
  (shows the line, pushes a new kind-0 record and replays the voice through the normal command)
  [V-emu: `verify_restore` kind 0 and `verify_slot_load`, 7.3].
- Rewind re-displays the line from the record and continues after the statement.
- The live effects outside the record (0x1653E00 / 0x1653E70 bits, 0x16541D8, the window words
  +0x1982 / +0x1984) must be reproduced by a port that wants the same timing after a rewind.

Emulation (`kind0_effects.py`): same random world and record, kind 8 (whose tail does nothing)
against kind 0, full data / bss diff. With from_history = 0 the only differences are 0x86AA24,
0x164FD40 (+0x1C), 0x1651A42, 0x1653AE0, 0x16541EC, 0x16541F0, 0x174DE00 and the call
0x75CD0(1). With from_history = 1 the list adds exactly the re-display writes above. Checked by
`verify_restore(modes=(0,), from_history=(0, 1))` on 20 seeds (F931 >= 0 on seeds 2, 5, 6 ...;
F931 < 0 on seeds 31, 47), and on 4 seeds for all kinds.

---

## 4. Ring management [E]

- Allocation: `0x753E0` allocates 0xB74280 bytes = **808** records and zeroes them; the ring
  wraps at 800 (0x7B44C) and the accessor bounds check is 0x327 (807).
- Clear (`0x7AF10`, also inline in 0xA2D10 / 0xA3050 / 0x7E790): for i < 800: +0 = i, +4 = -1,
  bytes +0x38FE, +0x38B0, +0x38E2 = 0; indices 0; 0x174D9B0 = 0; scene-stack top = -1.
- The rewind cursor 0x120A6E8 follows `cur` while not rewinding (0x60710 0x60EFB).

---

## 5. Rewind, previous / next scene, scene stack, backlog

### 5.1 Rewind (hold action 20, Adv_Rewind 0xA2020) [E]

```
Adv_Rewind(speed, flags, input):         # one step per call while the action is held
    clear pending effects / waits (0x1653E00 bits, 0x4A6F0, 0x495C0(-1), 0x5C080(2)); 0x1653E0E = min(.., 0x50)
    if anything is still pending (0x1653E00 & ~0x10, 0x1653E08, 0x1653E0E, hold-repeat 0x1653E0A): return
    c = cursor (0x120A6E8)
    if ring[c] is valid and ring[c].kind not in (0, 12, 13): History_Restore(c, 1)  (kind 1: 0x75CD0(1))
    t = c
    repeat: t -= 1 (below 0: 799 if wrapped, else t = 0 and stop; an invalid record (serial < 0)
            on the way cancels the step: t = c)
            until ring[t] valid, effect_blocking_bits == 0, kind == 0 and the record's F823 <= 1
            (F823 = message index of the page; s8 list slot 32 = record +0x3521)
    for j = c-1 down to t+1: History_Restore(j, 1)        # undo every command in between
    if t valid and t != c: 0x56730(1); cursor = t; History_Restore(t, 1); 0x7E4E0(cur);
        reset and rerun the asset prefetcher (0xA0740, 0x7ECB0, Script_Prefetch(script_ptr, ..))
```

So rewind moves back one **page** at a time (to the first message of the previous page) and
undoes every recorded command on the way; nothing is re-executed. Fast-forwarding after a
rewind is ordinary script execution from the restored position (+0x10 of the target, 3.2).

### 5.2 Next scene (action 23) [E]

After the confirmation dialog (state machine 0xDF29D8 in 0x60710, fade counter 0xDF29DC) the
game calls `0x75420(1)`: `0x16541F8 = 1` (unless 0x1653E00 bit 0x10). Adv_Update (0xA126F) then
sets the running flag `0x120A6D2 = 1` and runs Script_Step in a loop with all waits cleared
(0xA12B9..0xA1351) until a command clears 0x120A6D2. The commands that clear it (Script_Step,
named by the CFG walk of 2.4) are:

- `_ATSV` (0x96CBD, 2.5; it also restarts the music with 0x7E620): the next scene start.
- A message marker (Z handler 0x966E6..0x98428): it ends the skip when F998 (word 0x164B99C) != 0,
  or when the message is unread (readmax rule of 6) and config +0x30 (Fast-Forward = read text
  only) is set. So with the default settings "next scene" also stops at the first unread line.
- JUMP (0x9AA49), RDED / RTMN / RTM_ (0x9AA7A), TOMP (0x9ABC4), END_ (0x9B08C), SELD (0x9B20C:
  a choice).
- Outside Script_Step: the script reset 0x789F0, 0x94BB0, 0x95340, 0x5F7B0.., 0xB82E0, 0xB8400.

Pushes happen normally during the loop.

### 5.3 Scene stack and previous scene (action 24) [E]

- Push (`SceneStack_Push` 0x7E2E0, called by `_ATSV` after its kind-6 / kind-7 push, 2.5): only
  when **no effect slot is blocking** (OR of 0x5C680(i) for i < 14 == 0). Then `top += 1` (at
  top >= 299 the stack first shifts down by one record, memmove of 299 records from 0x120E110),
  `scene_stack[top] = ring[cur]`, `0x174DE04 = 2`.
- 0x174DE04 counts down by one per `_ZM` message (0x92414). It is also cleared by the kind-6 / 7
  restore pop and Slot_Load, and set to 2 by 0x7E790. While it is > 0, the scene that just
  started has shown fewer than two messages.
- Previous scene: `0x75420(-1)` -> 0xA2630 sees 0x16541F8 == -1 -> `0xA23F0`:

```
PreviousScene (0xA23F0 tail, 0xA253F..0xA25C4):
    blocking = OR over i < 14 of 0x5C680(i)
    if not blocking and (0x1653E00 & 0x10):
        if top < 2: return                       # nothing restored
        0x75CD0(1); top -= 1
    else:
        if top <= 0: return
        if 0x174DE04 > 0:                        # fewer than 2 messages since the scene start
            if top < 2: return                   # (top == 1: nothing restored)
            top -= 1
        0x75CD0(1)
    0x7EFC0(1); 0x1653E0E = 0
    0x7E790(top)                                 # 7.3: restores scene_stack[top], then top -= 1
```

So "previous scene" restarts the current scene. Within its first two messages it goes back to
the start of the scene before. With only one scene on the stack (top == 0) nothing happens.

### 5.4 Backlog (action 10, Backlog_Open 0x3BFB0) [E]

The backlog is the history ring itself:

```
Backlog_Open:
    n = cur - oldest + 1 (if cur - oldest < -1: n = cur - oldest + 801 when wrapped, else 0)
    if n <= 0: return 0
    list (i32[800] at 0xDD9710, all -1 first; count 0xDDA390) =
         ring indices walking back from cur (n steps; 799 after 0 when wrapped)
         whose kind is 0, 8, 11 or 12
    if count <= 0: return
    if count == 1: list = empty (count = 0)                         # 0x3C0AD -> 0x3C232
    drop trailing kind-12 entries (from the oldest end, keeping list[0])
    if count == 1: list = empty
    k = the first index >= 1 with list[k] >= 0 and the record's F799 == 1
         (0x765C0(rec, 799): the record's s8 copy of F799, "last message of its page")
    if there is no such k: list = empty (count = 0)                 # 0x3C16D -> 0x3C232
    else: list = list[k..count-1] (count -= k)   # drops the current page, already on screen
```

The log screen 0x3CE90 draws entries with 0x3C4B0 (record text re-laid out, textbox.md
section 4) and replays a record's voice from +0x3630. Action 18 or a right click closes it.

### 5.5 Backlog jump (0x3CC70) [E]

Selecting an entry (after its fade timer 0xDDB090 runs out):

```
BacklogJump:
    0xECF40(0x13, 0x100, 0); 0xDD9700 = 0; 0x9A90() (stop BGM); 0x8EA0() (stop voice)
    i = list[selection]; if i < 0: 0xCC670(6) (error sound), 0xDD9700 = 3, return
    if ring[i].kind == 12 (SELD): i = i - 1 (799 after 0); ring[i].kind must be 13 (SELR), else error
    for c = cur, cur-1, ..., i+1 (wrapping):          # discard the newer records' scene starts
        if ring[c].serial < 0: break
        if ring[c].kind in (6, 7) and scene_stack[top].ring_index == c: top -= 1 (not below -1)
    0x1653E0E = 0; 0x1653E00 = 0; 0x1653E08 = 0; 0x1653E70 = 0; 0x16541FC = -1
    History_Restore(i, 0)                     # from_history = 0: the command at +0x08 runs again
    next = cur (= i)                          # its new record overwrites ring[i]
    0x7E4E0(i); 0xADF00(); free 0xDD96F8
```

---

## 6. Auto mode, skip and fast-forward [E]

ADV mode words: `0x17DB230` bit 0 = auto mode, bit 1 = latched skip; `0x17DB232` = skip
active (> 0); `0x1653E0E` = auto countdown (frames).

- Toggles (0x60710): action 13 flips bit 0 (auto), action 12 or the middle mouse button flips
  bit 1 (skip latch) unless 0x17DB23A & 0x2002; action 1 or a right click clears bit 1.
- Skip speed (0x611BD..0x612C4): while action 19 (fast-forward) is held and nothing blocks
  it, `0x17DB232 = 1` with step 64, or, when the action's mask is exactly R2 (presets 0 and 1)
  and R2 is held, `step = max(1, (t * 0x44444480) >> 32)` for the analog value t < 0xF4 (t / 3.75),
  else 64. When it is not held (0x6120E): `0x17DB230 >= 2` (skip latch) gives `0x17DB232 = 1`
  and `step = table 0x2014F0[0x17DB230] = {1, 1, 64, 64, 64}[..]`; otherwise `0x17DB232 = 0`
  and step 1. The step multiplies the frame count passed to Adv_Update.
- Read-only rule (0x612D0, 0x61B0D): while skipping, if config +0x30 != 0 (Fast-Forward = read
  text only) and the current line is unread, skipping stops (`0x17DB232 = 0`, 0x56710(5)).
  "Read" = `0x75C90`: `readmax[block] != 0 and (line <= readmax[block] or block >= 0xFB4)`.
- Auto countdown (0xA0B10, 0xA2947, 0xA2A6E): when a message has been revealed and the voice
  wait 0x1653E1C has run out, `countdown = trunc(ms / 1000 * 60)` with
  `ms = config +0x9E` (Auto-Mode Advance Page Speed) if F799 == 1 (last message of the page)
  else `config +0x2E` (Auto-Mode Wait Time); it then decreases by the frame count each frame
  (`min(countdown, value) - frames`, clamped at 0). Adv_Update ends the wait when bit 0 is set
  and the countdown is <= 0 (0xA1D78, 0xA1F14); with skip active the countdown is clamped to
  60 (0xA290E) and bit 1 ends the wait at once.
- Text speed: `config[+0x20 + 2 * read]` (0x56188, 0x561FC: `[rcx + rsi*2 + 0x20]`): +0x20
  unseen, +0x22 seen (textbox.md 8.1 gives the reveal formula).
- Voice: Adv_Update stops the voice on advance when config +0x4E (Interrupt Voices on
  Advancing Text) is set and F1123 == 0 (0xA1015, 0xA1149, 0xA1243 -> 0x8EA0).

---

## 7. Save slots and the load path

### 7.1 Slot staging is always current [E]

`Staging_SetSnapshot(rec, n, recB)` (0x73980) copies a history record into the slot staging
0x12031E8 (state A), sets staging +2 = 1 and +4 = (n > 0 ? 1 : n), and copies 0x36A8 bytes of
recB into state B when n > 0 and recB != 0. Callers: the `_ZM` handler 0x92380 (0x92ACA), `_MSAD`
0x92B60 (0x9304D), `_ATSV` (0x98A16, 0x98B6D; 0x7B745 inside 0x7B5C0 for `_ATSV(8)`), SELD
(0x9B42B), the end of History_Restore (0x7DD0B), and the dead 0x93370 / 0x937C0. Every caller
passes `n = top + 1`, `recB = &scene_stack[top]` (0 when top < 0), and calls only when the
record's effect_blocking_bits == 0, its kind is not 11 or 12, and **F823 <= 1** (the first
message of a page). So a save always captures the start of the current page plus the current
scene's start record.

### 7.2 Storing [V-emu for Slot_Store, QuickSave_Store, SaveImage_Commit; E for the rest]

- `Slot_Store(n)` (0x737D0): slot n = staging; dword +0 = 0x00012AF8 (magic + used); date from
  `Rtc_LocalTime` at +0x70C0..+0x70C6; end marker 0x2AF8 at +0x70C8; then it sets the bit of
  F1118 in state A (0x76500(+8, 1118, 1)), so a loaded manual save has F1118 = 1. Emulated with
  F1118 = 0 before the push: state A differs from the pushed record in exactly that bit
  (+0x34FF) [V-emu].
- `0xA9450(n)`: SaveImage_LoadSystem(1); n >= 0: Slot_Store(n), SaveImage_Commit(2);
  n < 0: SaveImage_Commit(0).
- Quick save (action 14, `QuickSave_Store` 0x73A70): if staging +2 != 0, staging -> slots[139]
  (0x11FA558), then +0 = 0x12AF8, the date and the end marker are written directly. **It does
  not call 0x76500, so a quick save has no F1118 bit**: emulated, state A equals the pushed
  record byte for byte [V-emu]. Then, if 0xEA970(0) and 0xEABE0(0), 0xA9450(-1). A 300-frame
  cooldown (0xDF29FC) applies.
- Auto save (`0x73B70`): if the staging is valid: slots[100..137] move to slots[101..138]
  (memmove 38 records) and 0xA9450(100). Called from `_ATSV(8)` (0x98BC5, 2.5; config +0x40 is
  never tested on a reachable path) and from 0x6D4E1, 0x6D584, 0x6D8C5 (in 0x6D450 / 0x6D540 /
  0x6D600, called from 0x6E200, 0x6DA90, 0x6DD60; not traced).
- SaveImage_Commit(mode) (0x6FEC0): writes **all 140 slot blocks** (SaveImage_WriteBlock stamps
  0x2AF8 at +0 of each, even empty ones), then the system block. The latest slot (+0x16EE) is
  set by mode: 2 = newest date among slots 0..138, 1 = 0x1202F14 (value loaded at boot),
  0 = unchanged.

### 7.3 Loading: History_Restore, not scan mode [V-emu on engine-made slots]

Entry points: the save/load screen (0x71C90 -> 0xA2D10), Continue on the title (0xB0B12:
0x73890(latest)), quick load (action 15: SaveImage_LoadSystem(1), 0x73890(139)), 0x605F0.
`0x73890(n)` copies slots[n] to the staging and jumps to `Slot_Load` (0xA2D10) when dword
0xE20CC8 == 2.

```
Slot_Load (0xA2D10):
    clear choices, 0x75CD0(1) x2, clear ring (top = -1), F (0x960 bytes) and Z (0xAF0) to 0, G untouched
    reset the sound arrays (0x1654240.. = -1, 0x1654270.. = 100, 0x16543F0 entries), BGM = -1
    0x7EFC0(1); scene +0x199A..+0x199F = 0; 0xCD5F0(-1), 0x433F0(-1), 0x42F00(0,0,0,0); 0x1653E0E = 0
    if staging end marker (+0x70C8) != 0x2AF8: goto ScanLoad          # 0xA3050
    n = staging +4 (0 or 1)
    scene_stack[n] = state A (0x3A10 bytes)
    if n >= 1: scene_stack[n-1] = state B (0x36A8 bytes); its text byte +0x38FE = 0
    (n >= 2 would copy from 0x73BE0(2) = 0, a null pointer: never taken, +4 is 0 or 1)
    0x7E790(n)
    per layer: 0x49930(i, zoom..)                                     # from the restored scene_state
    cur = next = 0; 0x174DE04 = 0; 0x174DE08 = 1; 0x17DB230 &= ~2 (skip latch off)

0x7E790(n):                                    # also "previous scene"
    reset the renderer layer tables
    r = scene_stack[n]; i = r.ring_index
    if (i < cur or wrapped) and ring[i].serial == r.serial: History_Restore(i, 0)   # ring still has it
    else: clear the ring; ring[0] = r; History_Restore(0, 0)
    top = n - 1; 0xA88B0(0,0); clear 0x1653E00/E70/E30/E68; 0x7E4E0(i)
    if r.voice_id >= 0 and voice enabled (0x41AB0: config +0x16 && +0x36):
        runtime voice volume 0x17DA275 = config[+0x76 + 2*0x3D2D0(r.voice_character)] * config+0x16 / 100
    F967 = scene word 0x1651A66 + 1 and 0x495C0 / 0x5C380, or F967 = 0 and 0x495C0(-1)
    0x56280(F1112), 0x562B0(F1113), 0x56290(), 0xCD5F0(-1); 0x174DE04 = 0x174DE08 = 2

ScanLoad (0xA3050), only for a slot without the end marker [H: saves of an older build]:
    clear as above; F from state A's three lists; load script state_A.script
    scene id 0x164B204 = table 0x216130[script]
    script_ptr = Script_Scan(F1122)      # scan mode: replay from the scene start to _PGST(F1122)
    F1103 = 1; ring indices 0; tail-call 0x7E620 (restart BGM 0x16545D0 and the loop SEs)
```

`verify_slot_load` runs 0x73890 -> Slot_Load -> 0x7E790 -> History_Restore on engine-made
slots (with and without state B) and checks the following [V-emu]:

- ScanLoad is not called.
- Every F index is 0 except the state-A list values (with the F967 re-sync), so F1118 = 1 after a
  manual save. Z is zero.
- scene_stack[n] = state A, scene_stack[n-1] = state B with its text byte cleared, ring[0] =
  state A, top = n - 1.
- **script_ptr = base + state A's cmd_offset**: the page's first `_ZM` statement runs again
  (3.2).
- scene_state = state A +0x58 with the kind-0 rule, cur = next = 0, the skip latch is cleared,
  0x174DE04 = 0 and 0x174DE08 = 1.
- The restore's snapshot runs as `Staging_SetSnapshot(ring[0], 0, 0)`, because top is still the
  -1 written by Slot_Load. Until the re-run message pushes again, the staging has no state B.

Every slot written by v01.01 has the end marker (Slot_Store 0x7385A, quick save 0x73ADC), so a
normal load restores the saved record and scan mode is only the fallback. A port's resume format
therefore needs the whole record (or the equivalent state), not only (script, page, variables).

### 7.4 What a save slot contains

| slot part | content |
|---|---|
| +0x0000 | u16 0x2AF8, u16 used (1) |
| +0x0004 | i32 state-B flag: 1 when the scene stack was not empty, else 0 (top + 1 <= 0) |
| +0x0008 | state A = the newest page-start history record (section 2); F1118 bit set for manual and auto saves (Slot_Store), not for the quick save |
| +0x3A18 | state B = the first 0x36A8 bytes of the scene-stack top record: everything up to the middle of the +0x3694 array (scene copies, F lists, voice and effect fields). The channel table, window state and message text are not kept, and the loader clears the text byte +0x38FE of its scene-stack copy |
| +0x70C0 | date, end marker (engine.md 7.4) |

### 7.5 System block parts not covered elsewhere [E]

- +0x16F2 (RAM 0x1202F16): loaded at 0x6F54E, written back at 0x700B5, no other access: a
  round-tripped unused word.
- +0x052C (RAM 0x164D5C0) and +0x16F4 (RAM 0x164FBC0): decoded in `gallery.md` (picture_seen
  / bgm_heard bits and choice_taken bytes); the loader ORs them into RAM, so they only grow.
- Config +0x5A..+0x75 (system block +0x5E..+0x79): two 14-byte records copied from RAM
  0x164FCD8 / 0x164FCE8 at commit (0x419D0) and back at load (0x41890); 0x75F10 fills them at
  boot from `*0x86AA00` / `*0x86AA08` (archive / PlayGo state) [H on meaning].
- Config +0x24 (language): SaveImage_LoadSystem replaces a negative value (the boot value 0xFFFF)
  with 0x407F0() (the system language) at 0x6FE94 [E].

### 7.6 Engine-made save images [V-emu]

No retail save is available, so `engine_slot_image()` builds images with the game's own code:

1. History_Push (kind 0) of the first `_ZM` statement of script 1 (00_00, `_ZM01801($000107);` at
   0x664), with the pointers Script_Step passes (name - 2, statement end), F1118 = 0 and F823 = 1.
2. Staging_SetSnapshot(record, n, recB), optionally with a scene-stack top record.
3. Slot_Store(slot), or QuickSave_Store.
4. SaveImage_Commit. The date is stubbed to 2026-10-25 12:34:56, and RAM readmax holds only that
   message.

Results (`samples/misc_adv_system_state/engine_made_save.md`, tests
`test_engine_made_slot_decodes`, `test_quick_save_has_no_f1118_bit`,
`test_engine_made_slot_loads_through_history_restore`):

- `eboot.savedata.parse` decodes the slot with the right date, script 1 and cmd_offset 0x663.
- State A = the pushed record except the F1118 bit (manual), or byte-identical (quick save).
- State B = the scene-stack record's first 0x36A8 bytes.
- All 140 slot blocks carry the magic, and only the stored slot has `used` set.
- `check_save_image` reports no problems, and `verify_slot_load` no mismatches.

### 7.7 Checks for a real save (`check_save_image`, CLI `save`) [V on engine-made images]

`python -m tsuki.misc_adv_system_state save [PATH]` (default: the shadPS4 savedata000; copy a
real save to scratch first) and `test_real_save_if_present` run these checks:

- The `eboot.savedata.parse` issues, except "end magic" on unused slots (12, errata for savedata.py).
- Every used slot:
  - end marker 0x2AF8, and a sane date (2020..2100, month 1..12, day 1..31, h < 24, m / s < 60);
  - state A (and state B when present): serial >= 0 and a known kind;
  - script number < 549 (the allscr.mrg script count);
  - **cmd_offset at a statement of that decoded script**: raw[cmd_offset + 1] == '_', since
    Script_Step passes name - 2;
  - resume_offset inside the script.
- **readmax** (system block +0x738, 4020 u8): every non-zero entry is a text block that has `_ZM`
  markers in the scripts, and is <= the block's highest marker line. The Z handler raises
  readmax[block] to the current (block, line) at the next marker (0x96615..0x96648, blocks <=
  0xFB3), so readmax only holds lines that have been passed. There are 42,590 markers in
  784 blocks, highest block 3784 and highest line 255 (`marker_max_lines`, the engine's marker
  decoding, 1.3 s).

---

## 8. ADV input flow (0x60710, per frame) [E]

1. Copy the game input words; apply mouse flags (0x178EC54: left 0x2, right 0x8, middle 0x20).
2. Dialog / fade state machines for quick load, next scene and previous scene (0xDF29E4,
   0xDF29D8 with counters 0xDF29E8 / 0xDF29DC; each fades over 20 frames, passing
   `1.5 - (n - 1) / 30` to 0x5A2D0).
3. Menu / log requests (0x120A6D6, 0x120A6D7), window hide (action 25, 0x17DB23E), backlog
   (action 10), quick save / load (14 / 15), voice replay (16), system menu (17), auto / skip
   toggles (13 / 12), fast-forward and rewind holds (19 / 20), next / previous scene (23 / 24,
   enabled by config +0x32).
4. Stick A y (input +0x36, beyond +-80) changes config +0x04 by `(|y| - 80) / 8` per frame
   in the stick's direction, clamped 0..255 (0x61321..0x6137F) [E; meaning H: a live display
   value, also stored per history record].
5. Then either `Adv_Rewind(speed * frames, flags, input)` while rewinding or
   `Adv_Update(speed * frames, flags, input)`; frames = min(0xD47C0(), 3).

---

## 9. Configuration (0x66C578, 0x140 bytes = system block +0x04)

### 9.1 The PS4 config screen

Three tabs (CONF_PARTS child 0: System / Sound / Controls). Two of them are data-driven menu
pages at 0x66E890 (stride 0x9D0) [V, `read_config_pages`]:

| page | item ids | rows | layout per row |
|---|---|---|---|
| 0 (System tab, "Text Settings", CONF_PARTS 9) | 52..78 | 16 | item grid +0x280 (26 x 5 i32), type grid +0x78 (3 label, 4 value, 5 slider, 2 checkbox, 6..10 radio groups, 1 test button), items per row +0x10.., row maximum +0x968 (250, 250, 10000, 10000) |
| 1 (Sound tab, CONF_PARTS 14 and 15) | 79..190 | 26 | rows 0..2 Music / SFX / System Effects (+ mute, test), 3..4 Play All Voices, 5..6 Interrupt Voices, 7..25 one row per character (label, value, slider, mute, test) |

The Controls tab only shows pictures (CONF_PARTS 16 / 17). Closing the screen (0x3F7C0) runs
Config_FromMenu (0x3E6E0), applies the volumes (0x3E9B0) and saves the system data (0xA9640)
when the block differs from the copy taken on opening (0xDDB3A4).

### 9.2 Field map

Menu items verified by emulating Config_FromMenu (`config_from_menu`, 37 entries) [V-emu];
boot values from the eboot data [V]; "Recommended Settings" (0x3EAC0) leaves the boot values
unchanged [V-emu]. Writers of the whole block, from the static scan (reviewer's `cfgw.py` and
`memscan.py`): Config_FromMenu, Recommended Settings and the system load.

Fields without a menu item have no other writer, so they are constants for a port, with these
exceptions [E]:

- +0x04: the stick in ADV (0x6137F), and History_Restore (0x7CA2A) only when +0x4C != 0. The
  unreferenced 0x41DB0 / 0x41FC0 write it under the same condition.
- +0x24: the language radio 0x406B0, and the system-language fix-up at load (7.5).
- +0x5A..+0x75: 0x419D0 at commit.
- +0xA5: Recommended Settings writes its boot value 1.

The first version listed 0x3C360 / 0x3F7C0 as writers of +0x00. Those scan hits are
`nop word ptr [rax + rax]` instructions, so +0x00 has no writer.

| off | size | option / meaning | boot = recommended | menu item | runtime readers |
|---|---|---|---|---|---|
| +0x00 | 2 | display value (0xCD640 arg / 4) [H] | 255 | - (no writer) | 0x41890, 0xA0AD0, 0xA2630 |
| +0x02 | 2 | display value [H] | 1 | - | 0x41890, HUD |
| +0x04 | 2 | live value 0..255 (stick A y, (\|y\| - 80) / 8 per frame) | 0 | - (writers above) | push +0x363E, restore |
| +0x06 | 2 | display value (0xCD650) [H] | 127 | - | 0x41890 |
| +0x08 | 8 | constants | 1, 255, 255, 255 | - | none found |
| +0x10, +0x12 | 2, 2 | HUD constants | 255, 0 | - | 0x53CF0 |
| +0x14 | 2 | Music volume 0..100 % | 40 | p1 item 82 | runtime byte 0x17DA268+0xC = (1 - mute) x value |
| +0x16 | 2 | Play All Voices (100 on / 0 off) | 100 | p1 item 92 (selected -> 100) | runtime 0x17DA268+0xD; voice code |
| +0x18 | 2 | SFX volume | 40 | p1 item 87 | 0x17DA268+0xE |
| +0x1A | 2 | System Effects volume | 10 | p1 item 91 | 0x17DA268+0xF |
| +0x1C, +0x1E | 2, 2 | movie / BGM constants [H] | 0, 0 | - | 0x68A70; BGM start 0x98C0 |
| +0x20 | 2 | Unseen Text Display Speed (max 250) | 35 | p0 item 55 | reveal 0x56160 / 0x561D0 |
| +0x22 | 2 | Seen Text Display Speed (max 250) | 15 | p0 item 59 | same, read lines |
| +0x24 | 2 | Language (0 ja, 1 en, 2 zc, 3 zt, >= 4 system) | 0xFFFF (system language at load) | p0 radio 75..78 via 0x406B0 | Lang_Index 0x3ED90 |
| +0x26, +0x28 | 2, 2 | text constants [H] | 1, 0 | - | block draw, HUD, backlog |
| +0x2A | 2 | text colour mode: 0 fixed, 1 read-text colour 0xDC6D, 2 per speaker (table 0x86A8B0) | 0 | - | restore 0x7D977, 0x60710 |
| +0x2C | 2 | audio constant [H] | 0 | - | MPLY, SEPL |
| +0x2E | 2 | Auto-Mode Wait Time (ms, max 10000) | 2200 | p0 item 65 | auto countdown (6) |
| +0x30 | 2 | Fast-Forward: 1 read text only, 0 all | 1 | p0 radio item 70 | skip rule (6) |
| +0x32 | 2 | next / previous scene enabled | 1 | - | 0x6101D |
| +0x34 | 2 | constant | 1 | - | 0x41890 |
| +0x36 | 2 | voice gate (with +0x16) | 1 | - | 0x41AB0 |
| +0x38 | 4 | unused | 0 | - | |
| +0x3C | 4 | per-character voice enable bits (19 used) | 0x3FFFFFFF | p1 items 98+5i (mute == 1 clears bit i) | voice play |
| +0x40 | 2 | auto-save mask: **unused** (its only reader 0x98BBF is unreachable, 2.5) | 3 | - | none effective |
| +0x42 | 2 | action preset 0..2 (section 10) | 0 | - | 0x41890 -> 0xCEEE0 |
| +0x44..+0x4A | 2 each | choice / HUD constants [H] | 0 | - | 0xA6180, 0x53CF0 |
| +0x4C | 2 | restore +0x04 from history | 0 | - | restore |
| +0x4E | 2 | Interrupt Voices on Advancing Text | 1 | p1 item 94 | Adv_Update |
| +0x50 | 2 | constant | 1 | - | 0x56AB0 |
| +0x52 | 2 | unused | 0 | - | |
| +0x54 | 2 | music volume copy | 40 | = +0x14 (0x3E84E) | 0x17DA268+0xB |
| +0x56, +0x58 | 2, 2 | constants [H] | 0 | - | 0x60710, 0x57080 |
| +0x5A | 0x1C | two persisted 14-byte RAM records (7.5) | 0 | - | 0x41890 / 0x419D0 |
| +0x76 | 0x26 | per-character voice volume u16[19] (0..100) | 19 x 50 | p1 items 100+5i | voice play |
| +0x9C | 1 | Display End of Page Instantly (unseen text) | 0 | p0 item 54 | 0x56140 latch |
| +0x9D | 1 | Display End of Page Instantly (seen text) | 0 | p0 item 58 | restore, 0x92380 |
| +0x9E | 2 | Auto-Mode Advance Page Speed (ms, max 10000) | 3000 | p0 item 62 | auto countdown (6) |
| +0xA0 | 2 | Auto Skip Cleared Scenes | 0 | p0 radio item 66 | 0x61F40 |
| +0xA2 | 1 | Show Confirmation Message (auto skip row checkbox) | 0 | p0 item 68 | 0x608F1 |
| +0xA3 | 1 | Text Display Animations | 0 | p0 radio item 72 | block draw 0xC8F50 |
| +0xA4 | 1 | Seen Text Display Animations | 0 | p0 radio item 74 | HUD 0x53CF0 |
| +0xA5 | 1 | constant | 1 | - (Recommended Settings) | FADS 0x81520 |
| +0xA6, +0xA7, +0xA8 | 1 each | Music / SFX / System Effects mute | 0 | p1 items 81, 85, 90 | volume formula |
| +0xA9 | 0x97 | unused | 0 | - | |

Runtime volume bytes (0x41890 at load, 0x3E9B0 on change): `0x17DA268 + 0xB = (1 - mute_music)
* cfg+0x54`, `+0xC = (1 - mute_music) * cfg+0x14`, `+0xD = cfg+0x16`, `+0xE = (1 - mute_sfx) *
cfg+0x18`, `+0xF = (1 - mute_sys) * cfg+0x1A` (the bytes 40, 40, 100, 40, 75 of engine.md 6 are
these; 75 is not the boot value 10 of +0x1A, so 0x17DA268+0xF is overwritten before the first
load [H]).

### 9.3 engine.md 8.4 options -> PS4 storage

| option (engine.md 8.4, from the shared help-text table) | PS4 storage |
|---|---|
| Sound reset / Recommended Settings | 0x3EAC0 (= boot values) then Config_ToMenu 0x3E4A0 for pages 0..2 |
| BGM / SE / system SE volume | +0x14 (+0x54), +0x18, +0x1A; mutes +0xA6..+0xA8 |
| voice volume | no master slider: +0x16 Play All Voices (100 / 0) |
| movie volume, BGM 2ch / 5.1, lower BGM during voice, system voice, window opacity, outline, shadow, window design, stop skip at choices, button help (x2), vibration | not on the PS4 screen; no writable field (constants +0x1C, +0x1E, +0x26, +0x28 may be their vestiges [H]) |
| stop voice on text advance | +0x4E |
| per-character voice on/off + volume (23 names) | 19 rows: bit i of +0x3C, u16 +0x76+2i |
| text speed / auto speed | +0x20 / +0x22; +0x9E / +0x2E |
| fixed vs change colour of read text | +0x2A (constant 0); +0xA4 Seen Text Display Animations |
| language | +0x24 |
| skip all text vs read only | +0x30 |
| auto quick save at choices / scene start | +0x40 (constant 3) is never tested on a reachable path: `_ATSV(8)` always auto-saves (2.5), and the other AutoSave callers do not read it |
| show controls | Controls tab (pictures only) |
| PS4-only: Display End of Page Instantly, Auto Skip Cleared Scenes, Show Confirmation Message, Text Display Animations | +0x9C / +0x9D, +0xA0, +0xA2, +0xA3, +0xA4 |

---

## 10. Input actions

### 10.1 Game input word (VA 0xE3A40) [E]

Built every frame from the pad manager's internal bits (engine.md 8.5) `b = (dev+0x4D0 &
dev+0x14C) | dev+0x2AC` of the user device (0x11F2C0, stride 0x580):

| bit | button | bit | button |
|---|---|---|---|
| 0x1 | L2 | 0x200 | L3 |
| 0x2 | R2 | 0x400 | R3 |
| 0x4 | L1 | 0x800 | OPTIONS |
| 0x8 | R1 | 0x1000 / 0x2000 / 0x4000 / 0x8000 | up / right / down / left |
| 0x10 | TRIANGLE | 0x10000..0x80000 | stick A up / right / down / left (axis beyond +-80; stick A = device +0x168/+0x16C, probably the left stick [H]) |
| 0x20 | decide | 0x100000 | touch pad click |
| 0x40 | cancel | 0x100 | never set by the pad |
| 0x80 | SQUARE | | |

Words: +0x10 held, +0x20 newly pressed (held & ~previous), +0x18 newly pressed or auto-repeat
of the d-pad; +0x30 / +0x32 analog R2 / L2 (0x11DCE0(dev, 9 / 8), only while held), +0x34 /
+0x36 stick A x / y, +0x38 / +0x3A stick B (two i16) [E].

### 10.2 The 27 actions

`*0xDB8778` points to one of three 27 x u64 tables at 0xDB84F0, chosen by config +0x42
(0xCEEE0 from 0x41890; v01.01 has no writer, so preset 0 is always used) [V tables, E use]:

| # | preset 0 | preset 1 | preset 2 | action | evidence |
|---|---|---|---|---|---|
| 0 | decide | decide, L3 | decide, L3 | confirm / advance text | Adv_Update 0xA0ED6, all menus [E] |
| 1 | cancel | L2, cancel | L2, cancel | cancel / back; ends latched skip | 0x61854 [E] |
| 2 | - | - | R2 | choice screen check | 0xA6706 [H] |
| 3 | R2 | R2 | R1 | choice screen repeat check | 0xA6773 [H] |
| 4 | R2 | R2 | R2 | choice screen select | 0xA67A4 [H] |
| 5 | - | - | R1 | unused | no reference [E] |
| 6 | R2 | R2 | R2 | list paging | 0x2FFF0, 0x69BB0, title [H] |
| 7 | R1 | R1 | R1 | list paging | 0x2FFF0 [H] |
| 8 | R2, L1 | R2, L1 | R2, L1 | list next (with right) | 0x30053 [H] |
| 9 | R1 | R1 | R1 | list previous (with up) | 0x30067 [H] |
| 10 | SQUARE | SQUARE | SQUARE | open the backlog | 0x615A1 -> 0x3BFB0 [E] |
| 11 | L2, cancel, SQUARE | same | same | dialog cancel | 0x6D682 [H] |
| 12 | R1 | R1 | OPTIONS | toggle latched skip (bit 1) | 0x6141F [E] |
| 13 | L1 | L1 | 0x100 | toggle auto mode (bit 0) | 0x61683 [E] |
| 14 | OPTIONS | - | - | quick save | 0x61B80 -> 0x73A70 [E] |
| 15 | 0x100 | - | - | quick load (unbound) | 0x61C72 [E] |
| 16 | - | - | - | replay voice (unbound) | 0x60DFA -> 0xC3A80 [E] |
| 17 | TRIANGLE | TRIANGLE | TRIANGLE | open the system menu | 0x615D3 [H] |
| 18 | L2, TRIANGLE, cancel | same | same | close the backlog | 0x3CA75 [E] |
| 19 | R2 | R2 | R1 | fast-forward (hold; analog R2) | 0x611C7, 0x61277 [E] |
| 20 | L2 | L2 | L1 | rewind (hold) | 0x6154D -> Adv_Rewind [E] |
| 21 | up | up | up | ADV shortcut | 0x60BF3 -> 0xA5DD0 [H] |
| 22 | cancel, down | L2, cancel, down | same | close the save/load menu | 0x6ECB9 [H] |
| 23 | - | OPTIONS | R2 | next scene (asks first) | 0x6104C [E] |
| 24 | - | 0x100 | L2 | previous scene (asks first) | 0x616DA [E] |
| 25 | cancel | cancel | cancel | hide / show the message window | 0x61CBF [E] |
| 26 | 0x100 | - | - | unused | no reference [E] |

In preset 0, next / previous scene, quick load and voice replay have no button; the game
offers them through the system menu and mouse / touch HUD buttons (0x60710 pointer paths) [H].

---

## 11. What a port should do

- Keep a history ring of records with the logical content of section 2. Store scene_state as
  a block (2.6), and store the 20 bytes 0x1654330 that the PS4 forgets. Push one record per
  state-changing command with the kinds of 2.3, before the command runs: kind 1 once per batch of
  layer changes (F991), kind 9 at the next commit point, kind 6 / 7 from `_ATSV`, kinds 2 / 3 for
  audio, 12 / 13 for choices, 0 for messages.
- Rewind = undo records newest-first down to the previous page start (5.1), re-display the
  target line and continue after it (3.2). Next scene = run without waits until `_ATSV` (5.2).
  Previous scene = restore the scene-stack top, or the one below within the first two messages
  (5.3). Backlog jump = restore the entry's record and run its command again (5.5).
- Save = the page-start record + the scene-start record (7.1). Load = restore that record without
  re-display and **run the page's first message statement again** from cmd_offset (3.2, 7.3), not
  scan mode. Keep slots 0..99 manual, 100..138 auto FIFO, 139 quick. Manual and auto saves set
  F1118; the quick save does not.
- Config: implement the fields with menu items (9.2); treat the others as constants with
  their boot values (+0x24: system language when negative).
- A PS4 save importer reads slot states with `slot_states()` / `parse_history_record()`, checks
  them with `check_save_image()`, and needs the variables work for the F lists and the layer work
  for scene_state.

---

## 12. Errata for other documents (proved here, not applied there)

### Errata for engine.md

- 5.6: the ring allocation holds **808** records (0xB74280 bytes, VA 0x753E0); the ring uses
  800. Record fields: +0x24 is the record kind (2.3), +0x26 the BGM track; +0x28..+0x4F is one
  array of 20 i16 (loop-SE ids), not "32 bytes + u64"; a second scene copy +0x1A60 (0x1651AC8)
  exists; +0x3642 and +0x3657 are two 20-byte arrays, +0x366C / +0x3694 two 20 x i16 arrays,
  +0x36BC 20 bytes the push never writes (bug); +0x38B0..+0x39F8 = 0x164FD00 (0x32),
  0x164FD40 (0x1C) and the message text (0xFA bytes). Rewind undoes every record between the
  cursor and the target and lands on page starts (5.1). A load restores without re-display and
  re-runs the saved command (3.2). [V-emu]
- 5.6 / 7: `_ATSV` (0x95F5D) is the scene-start / choice save-point command: kinds 6 / 7, the
  scene stack and the auto save (2.5). [E; V forms]
- 7.2 / 7.4: every commit stamps 0x2AF8 at +0 of **all 140** slot blocks; empty slots have used
  (+2) = 0 and no end marker. Slots 0..99 are manual, 100..138 an auto-save FIFO (0x73B70),
  139 the quick save (not "[H]"). State B is the scene-stack top record, present when the
  stack is not empty. The quick save has no F1118 bit. [V-emu, E]
- 7.3: +0x16F2 (0x1202F16) is only round-tripped (unused); +0x16EE is unchanged by commit mode
  0 (quick save). [E]
- 8.4: per-character volumes are **19** u16 at +0x76..+0x9B (16 x 50 from 0x1A66A0 + 3 x 50),
  not 20; +0x9C / +0x9D are the two "Display End of Page Instantly" bytes (0 by default). The
  option list is the shared help-text list; the PS4 screen has the System / Sound / Controls
  tabs of section 9. The item -> field mapping is section 9.2. Config +0x40 has no effect. [V-emu]
- 8.5: the action masks use the game input word of 10.1 (built at 0xE3A40), not the pad bits;
  names in 10.2. [E]

### Errata for script.md

- 3.6: scan mode is used only by the fallback load 0xA3050 (slot without the 0x2AF8 end
  marker); a normal load restores the saved history record (7.3). "Save data therefore needs
  only (scene, page, variables)" does not hold for the PS4 load path. [V-emu]
- `ATSV` row (H strong: auto-save point): it auto-saves only for `_ATSV(8)`; `_ATSV()` marks a
  scene start (kind 6 + scene stack), `_ATSV(1)` a choice save point (kind 7). [E; V counts]

### Errata for textbox.md

- 8.3: `0x41AB0` (config +0x16 != 0 and +0x36 != 0) is the voice-enabled test (Play All
  Voices), used before replaying a voice (0x7E790); auto mode is 0x17DB230 bit 0 and its
  timing is section 6. [E]
- 8.1: +0x20 / +0x22 are the Unseen / Seen Text Display Speed options, indexed by the read
  state (`[rcx + rsi*2 + 0x20]` at 0x56188, 0x561FC). [E + config screen labels]

### Errata for vm_variables.md

- The normal slot load is `Slot_Load` 0xA2D10: it clears F and Z (0xA2DC0 / 0xA2DD3) and then
  restores the saved record with History_Restore through 0x7E790, so the u16 / s8 / bool order
  of the rewind applies to loads too (F967 is then re-synchronised, F991 comes back as 0 / 1,
  2.1). 0xA3050 (the loops 0xA3293..0xA334F) runs only for a slot without the 0x2AF8 end
  marker. [V-emu]

### Errata for eboot/savedata.py (engine work)

- `parse` reports "end magic 0x0000" for every empty slot of a game-made image (139 issues
  for a one-slot save), because empty slots carry the magic too; only `used > 0` slots should be
  checked (`check_save_image` filters them). [V-emu]

---

## 13. Review repair (2026-10-25): findings and proofs

| finding | severity | resolution and proof |
|---|---|---|
| 59 push sites, not 58; 0x97EA4 is kind 6 / 7, not 0; kind 7 has a site | major | **Confirmed and fixed.** `scan_push_sites` (byte scan + decode) == `PUSH_SITES` (59 rows; test). 0x97EA4 = `6 \| (n & 3 == 1)` inside `_ATSV` (CFG walk, 2.4); 2.3, 2.5 rewritten; kind 0 now has one live site (0x9291F). New, beyond the review: 21 sites are dead code, kind 8 is never pushed, kind 1 is batched by F991 and kind 9 is the commit point |
| kind 0 restore never emulated; scene +0x1982 / +0x1984 and other effects | major | **Confirmed and fixed.** 3.2 gives the kind-0 tail; `verify_restore(modes=(0,), from_history=(0, 1))` passes on 20 seeds (both F931 signs); the test runs seeds 2 and 31. New: with from_history = 0 (loads, previous scene, backlog jump) the re-display is skipped and the saved command runs again; only rewind passes 1. The earlier text "from_history = 1 for backlog jumps" was wrong (0x3CD69 `xor esi, esi`) |
| scene_state not tied to writers; +0x0 / +0x2 / +0x34C gap missing; layers.md absent | major | **Fixed.** `SCENE_STATE_FIELDS` tiles all 0x1A08 bytes: 51 arrays, 29 globals and 4 gaps (8 bytes), each with writer instructions or functions, or marked "block copies only" (2.6). Reconciled with the layer work's `SCENE_ARRAYS` by a test (offsets, sizes, names); writer instructions of the globals are re-decoded by a test (93, of which 83 rip-relative and in the field) |
| push writes F995 into scene +0x19A3 | minor | **Confirmed**; in 2.2 and 2.6; `verify_push` checks RAM 0x1651A63 and record +0x58+0x19A3 |
| quick save has no F1118 bit; F1118 test vacuous | minor | **Confirmed.** The engine image now sets F1118 = 0 before the push: the manual slot differs from the record only in that bit, and the emulated QuickSave_Store slot is byte-identical (two tests) |
| 2.3 rewind stop vs mask 0x3001 | minor | **Fixed** (2.3 now matches 5.1) |
| backlog empty cases | minor | **Confirmed**, 5.4 pseudocode gives both (count 1; no k with F799 == 1) |
| F991 in two lists; F967 rule only for F967 > 0 | minor | **Confirmed**, 2.1. The F967 rule also sets 0x1653E00 \|= 0x200 and calls 0x495C0(-1) / 0x5C6C0(2, 1) (found when seed 58 failed the new kind-0 check) |
| 9.2 "never written" contradicted by +0x04, +0x00, +0x24 | minor | **Fixed**: the writers are listed (9.2). +0x00 has none: the cited 0x3C360 / 0x3F7C0 hits are nop instructions. +0x24 also gets the system language at load (0x6FE94) |
| previous-scene pseudocode (top == 1 returns; gate = blocking effect or bit 0x10) | minor | **Confirmed**, 5.3 rewritten from 0xA253F..0xA25C4; SceneStack_Push also gates on blocking effects, not on active ones |
| real-save test too weak; no load-back; no state B image | minor | **Fixed**: `check_save_image` (7.7: statement-start offset, readmax vs markers, ...) is used by the real-save test and run on engine images. `verify_slot_load` emulates 0x73890 -> Slot_Load -> 0x7E790 -> History_Restore on images with and without state B (7.3, test). The "< 600" bound is replaced by "< 549 scripts" |
| deliverable paths; test comment "auto" for action 12 | minor | Paths: this run's assignment names the `misc_adv_system_state` files (header note). The test comment now says skip latch (action 12) and checks auto = action 13 = L1 |

---

## 14. Evidence (commands actually run)

- `ls %APPDATA%/shadPS4/home/1000/savedata/` (empty), a 7-level `find` for `savedata000*`
  under the user profile and D: (none), `python -m eboot.savedata` ("no save file found").
- Static scans: `memscan.py build|range|funcs` (every data reference, with lea-base register
  tracking), `histscan.py`, `pushsites_cfg.py` (59 sites, kinds, commands by CFG walk from each
  handler with relative jump tables, liveness from the eboot.disasm index; 2 unresolved indirect
  jumps 0x99C25 / 0x99CB2 do not lead to push sites), `scene_state_fields.py`
  (`scene_state_scan.json`), the reviewer's `cfgw.py` (config writers), `actctx.py`, `inwriter.py`.
- `atsv_forms.py` over the 549 scripts: 313 `_ATSV();`, 83 `_ATSV(1);`, 2 `_ATSV(8);`.
- Disassembly read: 0x7AF90, 0x7C6D0 (whole, including 0x7D6F2..0x7DD0B), 0x7E790, 0x7E2E0,
  0x7B5C0, 0x7F410, 0x816B0.., 0xA2D10, 0xA3050, 0xA2020, 0xA23F0 (0xA253F..), 0xA2630 (part),
  0xA0B70 (parts), 0xA0B10, 0x60710 (most), 0x3BFB0, 0x3CC70, 0x765C0, 0x73980, 0x737D0, 0x73890,
  0x73A70, 0x73B70, 0x73BD0 / 0x73BE0 / 0x73C20, 0xA9450, 0x6FEC0 (0x6FE60..), 0x3D440, 0x3D360,
  0x3E6E0, 0x3EAC0, 0x3E9B0, 0x41890, 0x419D0, 0x41F70.., 0x42340.., 0x75F10, 0x3F7C0, 0xE3A40,
  0x11DB80, 0xCEEE0, 0x75420, 0x75490, 0x75C90, 0x92380 (prologue, 0x928E0..), Script_Step
  0x95F5D..0x96CDA (ATSV), 0x96E77.., 0x97800..0x978D0, 0x97921.., 0x97E91.., 0x98529..0x98BDB,
  0x968B2.., 0x98440.., 0x9AA23.., 0x9AC1A.., 0x9AECE.., 0x99800...
- Emulation (unicorn 2.1.4 with a Python VEX interpreter, `misc_adv_system_state_emu.py`):
  - `verify_push(seeds=1..13)` and `(200..227)`: 0 mismatches.
  - `verify_restore(seeds=(31, 47, 58, 99), modes=(0..9, 12, 13), from_history=(0, 1))` and
    `verify_restore(seeds=1..20, modes=(0,), from_history=(0, 1))`: 0 mismatches.
  - `kind0_effects.py` (kind 8 against kind 0 RAM diffs).
  - `engine_slot_image` (slot 7 with state B, slot 42, slot 3 with state B, quick save) with
    `check_save_image` and `verify_slot_load`: no problems.
  - `config_recommended()` == boot block; `config_from_menu()` == `FROM_MENU` (37 entries).
- `python -m tsuki.misc_adv_system_state dump samples/misc_adv_system_state`; `... save`
  ("no save file found", rc 2); `... save scratch/misc_adv_system_state/engine_made_save.bin`
  (1 used slot, 0 problems).
- `pytest -p no:faulthandler tools/tests/test_misc_adv_system_state.py`: 25 passed, 1 skipped
  (no real save) in about 8 s; with `TSUKI_GAME_DIR=/nonexistent`: 10 passed, 16 skipped. On
  Windows pytest's faulthandler prints "access violation" traces from unicorn's first `mem_map`.
  These are first-chance exceptions handled inside unicorn (the same happens with
  `tsuki.script_emu`), and `-p no:faulthandler` silences them.

---

## 15. Open questions and dependencies

1. No retail save: the save checks ran on engine-made images. Copy a real savedata000 to
   scratch and run `python -m tsuki.misc_adv_system_state save <copy>`; `test_real_save_if_present`
   then runs `check_save_image` on it.
2. Meaning of the per-layer arrays and the renderer copies done by the restore: layers.md
   (layer_state_and_motions work). The offsets already agree with that work's module (2.6).
3. F list semantics (which F index means what): F931 (copied to scene +0x1984 by the kind-0
   restore), F967 / F991 / F995 / F1118 / F1122 roles: vm_variables.md (vm_variables_errata work).
4. Loop-SE channel arrays 0x1654240..0x1654344 and the 20 x 0x18 table 0x16543F0, BGM volume
   0x1654238, voice volume path: sound_runtime.md (audio_runtime_semantics work).
5. Unlock bit sets in the system block (+0x52C, +0x16F4): documented in gallery.md
   (gallery_extras_flowchart work); not repeated here.
6. Title "Continue" and the choice screen (actions 2..4, the AutoSave callers 0x6D450 / 0x6D540
   / 0x6D600 and their callers 0x6E200 / 0x6DA90 / 0x6DD60): system_flow.md
   (boot_title_choice_flow work).
7. Not traced: the meaning of config +0x00 / +0x02 / +0x04 / +0x06 (display values passed to
   0xCD640 / 0xCD650), the list-screen actions 6..9, action 21, the record fields 0x174DAB0
   (+0x3468) and 0x174D940 (+0x34A8), the scene globals +0x19A0 / +0x19A8 / +0x19A9 (no writer
   found), and 0xD1CD0 / 0x67460 (the `_ATSV(8)` gates).
8. When a slot can lack the end marker (scan-mode load): only older builds are plausible [H].
