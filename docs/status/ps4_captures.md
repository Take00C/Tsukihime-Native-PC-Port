# PS4 captures: what to take on the console, where to find it, what to look for

Written 2026-10-25 (setup job) from the format documents. The Python models and the C# port
are checked against each other; only the console can show where both share a
misunderstanding (ARCHITECTURE 10.6, R2). This file lists the screenshots and short video
clips the user is asked to take on the PS4 (ROADMAP 5):

- **before M4**: the effect, text and sound checkpoints of sections 3 to 6. M4 acceptance 7
  compares them, and every mismatch is triaged;
- **before M7**: the menu captures of section 7. M7 acceptance 5 compares every screen side by
  side with them.

Captures stay local in `samples/ps4/` (git-ignored; the pre-commit hook refuses anything
under `samples/`). This file names places by script, statement and text id only. It quotes no
game text.

---

## 1. Capture settings and file handling

On the PS4 (menu names as in recent system software; check on your console):

| setting | value | why |
|---|---|---|
| Settings > Sharing and Broadcasts > Screenshot Settings > Image Format | **PNG** | JPEG artefacts would swamp 1-2 LSB differences |
| Settings > Sound and Screen > Video Output Settings > Resolution / HDR | 1080p / **HDR off** | the engine draws 1920 x 1080 SDR; HDR captures are tone-mapped |
| Settings > Sharing and Broadcasts > Video Clip Settings | the highest resolution offered; microphone audio off | the PS4 records clips at 30 fps |
| game config | factory defaults, except the language named in each item | timings and colours depend on text speed, window opacity and the read-text colour option (adv_state 9) |

- **Screenshot**: SHARE button (a short press with "Easy Screenshots", otherwise a long press).
  **Video clip**: double-press SHARE to start and stop recording.
- **Watermark**: in normal play the game sets an overlay on screenshots,
  `image/corp.png` (131 x 48), at origin 3, probably bottom-right (archives_ui_movies 8,
  engine 2.2). Comparisons must mask that box.
- **Blocked sections**: the game turns screenshots and recording off in some modes (spoiler
  protection, VA 0xEE550; which sections is not known). If the console refuses, write
  "blocked" for that item.
- **Frame-exact work** (quake, shake, swing; section 3.4): a 30 fps clip gives amplitude and
  rhythm but not the per-frame formula. An HDMI capture card at 1080p60 is better, if one is
  available (Settings > System > Enable HDCP must be off). It is optional.
- **Transfer**: Capture Gallery > Options > Copy to USB Storage Device. Then copy the files
  into `samples\ps4\`.
- **File names**: `<id>_<lang>_<n>.<png|mp4>`, for example `E01_ja_1.mp4` or `T04_en_2.png`.
  Add one line per file to `samples/ps4/notes.txt`: the file name, the item id, the language,
  "fast-forwarded" if skip was used to get there, and anything unexpected.

---

## 2. Finding a checkpoint on the console

- **Script names** follow `CC_RR_<route><CC>_<part>`: chapter `CC`; `RR` 00 for the ARC
  scripts and 01 for the CIEL scripts [inferred from the names]. `00_00` is the hospital
  prologue. `QA_01`..`QA_25` are the 25 extra-scenario episodes (gallery 8), and `QACALL` is
  their menu script.
- **F023** is the chapter number the game itself shows (ui_text 358 + F023). **F022** is the
  flowchart scene (ui_text 396 + F022, gallery 9). Both are set at the top of each script.
- **"after message k of n"**: the effect comes after the k-th `_ZM` message of that script
  (0 = before the first message, in the scene setup). **last text ids** is the `$NNNNNN` id
  of the last message before it. The backlog (SQUARE) shows that line.
- **Reaching the prologue**: the first New Game plays the prologue movie (`00_01`) and
  returns to the title. The second New Game runs `START_SCRIPT` and then `00_00`
  (system_flow 3.4, RR 7.6).
- **Later scenes**: play to them, or use the flowchart. A scene is replayable from title >
  Gallery > Flowchart once it is done, and in game from the system menu's flowchart with
  its rewind-to-scene command (ui 1498; gallery 5.4, 5.5). If a scene is not reached yet, skip the item and
  write "not reached".
- **Fast-forward** (hold R2) is fine for getting close, but release it a few messages before
  the checkpoint. Transitions are skipped or drawn differently while fast-forwarding
  (compositing 7.1, 14.7).
- **Local helper.** This prints the chapter name, the scene title and the line just before
  each checkpoint in your language. The output is game text for your terminal only; never
  paste it into documents or commits.

  ```powershell
  $env:PYTHONPATH = "tools"
  .venv\Scripts\python.exe tools\setup\ps4_capture_targets.py --limit 1 --show-text ja
  ```

Priorities:

- **P1**: before M4, inside the M4 human-check range (New Game through chapter 1; scripts
  `00_*`, `01_*`, F023 <= 4).
- **P2**: chapters 2-5.
- **P3**: later or rare (single uses, extras).

The locations below are the first hit in script order. Section 9 has the generated table
with every column. `--limit 3` gives alternatives.

---

## 3. Effect checkpoints (M4)

### 3.1 Transitions

| id | P | capture | where (script, F023 / F022, after message) | statement | what to look for | ref |
|---|---|---|---|---|---|---|
| E01 | P1 | video, from 1 s before to 1 s after | `00_00`, 1 / 1, msg 366 of 387 (two wipes in a row) | `_FADS(2,900,19)`, then `_FADS(2,1100,19)` | **Rule wipe.** The old picture dissolves into the new one along the RULE_19 pattern: dark rule pixels change first, soft edges, no seams. Duration 900 / 1100 ms (54 / 66 frames at 60 Hz). Check whether the message window and text belong to the old frame, so that they dissolve with it, or are drawn on top. | compositing 7.2-7.4, 14 #2 |
| E01-FF | P1 | video | same spot, holding R2 through it | | **Fast-forward.** Is the wipe still shown, shortened, or cut? (The rule is not reloaded and the speed may exceed 1.) | compositing 7.1, 14 #7 |
| E02.26 | P2 | video | `02_00_ARC02_1B`, 5 / 35, msg 104 of 138 | `_FADS(2,500,26)` | Shape of RULE_26 (the most used rule, 147 wipes) | compositing 7.3 |
| E02.27 | P2 | video | `02_00_ARC02_4`, 5 / 46, msg 48 of 62 | `_FADS(2,600,27)` | Shape of RULE_27 (141 wipes) | compositing 7.3 |
| E02.17 | P2 | video | `03_00_ARC03_5B`, 7 / 68, msg 163 of 193 | `_FADS(2,2000,17)` | RULE_17, slow (2 s) | compositing 7.3 |
| E02.28 | P2 | video | `04_00_ARC04_4_1B`, 9 / 89, msg 48 of 113 | `_FADS(2,300,28)` | RULE_28, fast | compositing 7.3 |
| E03 | P1 | video + a screenshot halfway if you can | `01_00_ARC01_1`, 3 / 3, msg 85 of 115 | `_FADS(0,1500,0,0)` | **Cross-fade with text on screen.** Does the text window fade out with the old frame, stay on top, or vanish before the fade? Is it a smooth 90-frame fade? | compositing 7.2, 7.4; ARCHITECTURE 10.6 |
| E04.2 | P2 | video | `03_01_CIEL03_2_1`, 8 / 72, msg 79 of 100 | `_FADS(0,300,0,2)`, then `_FADS(1,0,0,2)` | Window argument 2. Does the message window hide, or the script wait, around the transition? | compositing 7.1, 14 #6 |
| E04.1 | P3 | video | `09_00_ARC09_3_1`, 20 / 288, msg 58 of 67 (only use) | `_FADS(0,1000,0,1)` | Window argument 1, the same question | compositing 7.1 |

### 3.2 Blend modes (`_STBR`)

Take a screenshot once the layers have settled, usually when the next message appears. Add a
short video if the layer moves.

| id | P | where | statement | what to look for | ref |
|---|---|---|---|---|---|
| E05 | P1 | `00_00`, 1 / 1, msg 269 | `_STBR(4,17)`, `_STBR(5,17)` | **ps_dodge5 glow.** Brightness and saturation where the dodge layer is bright; pure black must stay black (the d < 3/255 guard) | compositing 2.3, 5.2 |
| E05f | P2 | `03_00_ARC03_5B`, 7 / 68, msg 92 | `_STBR(33,17,,1)` | With the flag, mode 17 should look the same as without it | compositing 5.2 |
| E06 | P1 | `01_00_ARC01_7`, 3 / 23, scene setup (msg 0) | `_STBR(5,5)` | nexl dodge without the flag: the black guard in dark areas | compositing 2.3 |
| E06f | P2 | `02_00_ARC02_6`, 5 / 52, msg 58 | `_STBR(17,5,,1)` (with `_STNS(17,11)`, E19) | Dodge with the flag: blending is off and k = picture alpha | compositing 5.2 |
| E07.7f | P3 | `05_00_ARC05_9`, 11 / 134, msg 149 | `_STBR(10,7,,1)` | Multiplicative with the flag: the colour changes (k = picture alpha) | compositing 5.2 |
| E07.9f | P2 | `03_00_ARC03_5B`, 7 / 68, msg 92 | `_STBR(35,9,,1)` | Screen with the flag: the colour changes | compositing 5.2 |
| E07.10f | P3 | `05_00_ARC05_9B`, 11 / 135, msg 98 | `_STBR(13,10,,1)` | Sub with the flag: must look the same as without it | compositing 5.2 |
| E08.23 | P1 | `00_00`, 1 / 1, scene setup (statement 63) | `_STBR(4,23)` | ps_screen on the opening backdrop | compositing 2.3 |
| E08.22 | P1 | `00_00`, msg 32 | `_STBR(12,22)` | ps_overlay | compositing 2.3 |
| E08.19 | P1 | `00_00`, msg 114 | `_STBR(35,19)` | ps_hardlight (the vertex colour is applied to S first) | compositing 2.3 |
| E08.24 | P1 | `00_00`, msg 135 | `_STBR(10,24)` | ps_softlight, power form (not the W3C formula) | compositing 2.3 |
| E08.11 | P1 | `00_00`, msg 192 | `_STBR(38,11)` | ps_add (uses the vertex colour rgb) | compositing 2.3 |

Modes 11, 17, 19, 23 and 24 are also the modes of spike S3.

### 3.3 Colour effects (`_STEC`, `_SNX2`, `_SNX3`)

`_STEC` is emulated bit-exactly (756 cases). These captures mainly check *when* the recolour
is applied, at the `_STCH` or at the commit [H], and the swapped luma weights.

| id | P | where | statement | what to look for | ref |
|---|---|---|---|---|---|
| E09.1 | P1 | `00_00`, msg 49 | `_STEC(18,1,0x000000)` | Black silhouette fill (alpha kept) | compositing 9 |
| E09.4 | P1 | `00_00`, msg 125 | `_STEC(3,4)` | Negative | compositing 9 |
| E09.3 | P1 | `01_00_ARC01_1`, 3 / 3, scene setup | `_STEC(3,3)` | Sepia (ids 2..7 use the swapped weights B 161 / R 56) | compositing 9 |
| E09.2 | P1 | `01_00_ARC01_1`, msg 27 | `_STEC(15,2)` | Grey with the swapped weights | compositing 9 |
| E09.77 | P2 | `03_00_ARC03_1`, 7 / 58, scene setup | `_STEC(6,77)` | Grey with the BT.709 weights; compare its tint with E09.2 on bluish pictures | compositing 9 |
| E09.44 | P2 | `03_01_CIEL03_3_3C`, 8 / 78, msg 27 | `_STEC(20,44)` | A colour grade (the most used, 465) | compositing 9 |
| E10.2 | P1 | `00_00`, msg 114 | `_SNX2(35,27)` | Brightness +27 | compositing 9 |
| E10.3 | P1 | `00_00`, msg 235 | `_SNX3(21,32)` | Contrast 32 | compositing 9 |

### 3.4 Quake, shake, swing and screen effects

Video, at least 5 s from the command on. The per-frame formulas are **not modelled**
(`rand()` / `sinf` in 0x58206..0x58827). Note the amplitude in pixels, the direction (x, y,
zoom, rotation), the rhythm and the fade-out.

| id | P | where | statement | what to look for | ref |
|---|---|---|---|---|---|
| E11 | P1 | `00_00`, msg 8 | `_SQK2(-1,1,4,0,600,0,0,0,0)` | Layer quake on all layers, 600 ms. A stronger one follows at msg 86, `_SQK2(-1,12,30,...)` | layers 4.4, 13 #1 |
| E12 | P3 | `05_00_ARC05_9_2B`, 11 / 138, msg 95 | `_SHK2(17,0,3,300,-1,0)` (layers 17..19) | Layer shake until stopped | layers 4.4 |
| E13 | P2 | `02_00_ARC02_1`, 5 / 34, msg 14 | `_SSW2(11,7,4,1000,2000,1,-1,0)` | Swing (rotation) | layers 4.4 |
| E14.SHOK | P2 | `02_00_ARC02_1B`, 5 / 35, msg 65 | `_SHOK(1,3,100,18)` | Screen quake (globals 0xDF0818..) | layers 4.4 |
| E14.MSQK | P3 | `QA_02` (extras), msg 17 | `_MSQK(500,5,1)` | Message-window quake | layers 4.4 |
| E15 | P1 | `00_00`, msg 68, until the next `_FADE(41)` | `_FADE(40,)` | **Screen effect 4: what it draws is unknown.** Film 10 s or more and describe the effect: particles, streaks, direction | compositing 8, 14 #5 |
| E15a | P2 | `02_00_ARC02_1`, 5 / 34, msg 14 | `_FADE(40,-90,16)` | The same effect with angle -90 and speed preset 16 | compositing 8 |

### 3.5 Masks, crops, per-layer rules, commands with no known reader

| id | P | capture | where | statement | what to look for | ref |
|---|---|---|---|---|---|---|
| E16.0 | P1 | screenshot | `00_00`, msg 49 | `_STCH(17,img1688,...,160,-285,888,1650,90,0)` | **Crop / mask edge.** The retail mask pattern (layers 17-19): a hard or feathered edge, feather width against a16 = 90 | layers 9, 13 #2 |
| E16.1 | P1 | screenshot | `01_00_ARC01_1`, 3 / 3, msg 13 | `_STCH(12,img0008,...,61,-154,964,1389,30,1)` | Cut-in frame with a17 = 1, a16 = 30 | layers 9 |
| E16.2 | P1 | screenshot | `01_00_ARC01_1`, msg 74 | `_STCH(16,img0007,...,1273,-256,980,1587,15,2)` | a17 = 2 | layers 9 |
| E16.7 | P2 | screenshot | `04_00_ARC04_6_2B`, 9 / 95, msg 58 | `_STCH(12,img5008,...,1,42,470,135,30,7)` | a17 = 7 | layers 9 |
| E16.6 | P3 | screenshot | `14_01A_CIEL14A_5`, 33 / 456, msg 37 (only use) | `_STCH(7,img1788,...,532,92,704,1740,0,6)` | a17 = 6 | layers 9 |
| E17 | P2 | video | `02_00_ARC02_4B`, 5 / 47, scene setup and the layers' first appearance | `_STTR(3,26)`, `_STTR(39,17)` | Per-layer rule transition (rules 26 and 17) | layers 4.2, compositing 7.3 |
| E18 | P1 | video | `00_00`, msg 20 | `_STCF(1,8)` (5,159 uses) | **The reader is not traced.** Watch for any visible change near it; nothing may show | layers 4.5, 13 #9 |
| E19.STNS | P2 | video | `02_00_ARC02_6`, 5 / 52, msg 58 | `_STNS(17,11)` | Noise animation | layers 4.2 |
| E19.STRA | P1 | video | `00_00`, msg 135 | `_STRA(12,1)` | **No reader was found.** Expect nothing visible; confirm | layers 4.2, 13 #3 |
| E20 | P3 | screenshot | `07_00_ARC07_1_2`, 16 / 200, msg 53 | `_SPFC(29,aki_a_30_01_n01,...)` | Special picture: what it shows and where [H] | layers 4.5 |

---

## 4. Text and message-window checkpoints (M4)

Take these in **both ja and en** unless noted: full-screen 1080p PNG, no zoom.

| id | P | where | what to capture and look for | ref |
|---|---|---|---|---|
| T04 | P1 | `00_00`, 1 / 1, the page whose 4th message is msg 7 (`$000114`) | (a) Right after the 4th message has fully appeared. (b) The same page after 30 s without input. Look for: **previous-message dimming** (older messages fade to about 0.39 alpha, or not at all); the **line pitch** (78 px from the engine constants vs 81 px in the prologue movie); glyph x positions; drop shadow and outline | textbox 5.8, 6.3, 12 #1, #7; ARCHITECTURE 11 #2 |
| T05 | P1 | `01_00_ARC01_10`, 3 / 4, msg 4 (`$002565`) | The same as T04 with the window picture hidden (F1104 = 1): text directly on the scene | textbox 7, 10.1 |
| T10 | P1 | `01_00_ARC01_1`, 3 / 3, the end of the script (first story choice) | The choice menu with each option highlighted, in ja and en. The glyph height on the 640 x 344.7 choice canvas is open | system_flow 5.3, 5.6 |
| T01 | P2 | `03_00_ARC03_5B`, 7 / 68, msg 19 (`$004478`; text ids 4478..4620, 13 runs) | **The `@g` style.** What glyph flag bit 2 looks like (colour, slant, shake?) | text 6.2, 9 #1 |
| T02 | P2 | `03_00_ARC03_5B`, msg 68 (`$004527`; also 4529, 4531, 4533) | **`@g@b`**: the extra look of glyph flag bit 16 | text 6.2, 9 #1 |
| T03.c1 | P3 | `08_01_CIEL08_5`, 19 / 277, msg 183 (`$019508`) | **`@c1` text colour.** Red means RGB565 (0xF800), blue means BGR565 | text 6.2, 9 #2; ARCHITECTURE 11 #3 |
| T03.c4 | P3 | `10_00_ARC10LIVING`, 24 / 320, msg 170 (`$027005`) | `@c4` (0xAD55) colour | text 6.2 |
| T03.c2 | P3 | `14_01B_01_CIEL14B_1B`, 34 / 459, msg 33 (`$038475`) | `@c2` (0x345F) colour | text 6.2 |
| T03.c3 | P3 | `05_01_CIEL05_1C`, 12 / 142, msg 17 (`$011123`; also carries `@c7`) | `@c3` (0x1082) colour and whatever `@c7` (extension value, not decoded) does | text 6.2 |
| T07 | P2 | `04_00_ARC04_5`, 9 / 91, from the start | Dark overlay window 1 (DISP_PARTS 12): how dark, compared with the window opacity setting | textbox 7 |
| T08 | P3 | `06_00_ARC06_1_3`, 14 / 171, msgs 42-43 | `_FCLN(600,200,,,32,4)`, then `_FCSP()`: the full-screen narration layout, before and after | textbox 9, 12 #4 |
| T09 | P2 | `05_00_ARC05_3_1`, 11 / 122, msg 18 (`$009328`); `09_01_CIEL09_6`, 21 / 312, msg 94 (`$023613`) | Video of the reveal: with `@t`, both parts appear at the same time | text 6.2 |
| T06 | P3 | `QA_01` (extras), from msg 0 | Speech bubbles (window 5): the bubble position and the text inside it. They occur only in the extras | textbox 7 |
| T11 | P1 | any read page, with the config option for the colour of read text on | The same page unread and read again: the read-text colour | textbox 12 #2; adv_state 9 |

---

## 5. Sound checkpoint (M4)

| id | P | where | what | ref |
|---|---|---|---|---|
| A01 | P1 | `00_00`, msg 26 | Video with game audio, default volumes, starting a few messages before `_VPLY(D10_00_00_0000_0,03fb5)` (voice #16309, the first voiced line of the retail path). It is the reference for the voice / BGM balance and ducking in the M4 human check | sound_runtime; RR 7.6 |

---

## 6. Boot and title (M4: boot and the title state machine)

| id | P | capture | what to look for | ref |
|---|---|---|---|---|
| B01 | P1 | video from launching the game to the title menu, no input | The order and duration of: white cover (61 frames), TM_CI movie (6 s), caution2 (123..602 frames), title fade-in (121 frames), BGM start. Repeat while pressing decide, to see what can be skipped | system_flow 3.3, 3.4 |
| B02 | P1 | video, 30 s of the title screen without input | Title particles (150-frame life, alpha 0.3..0.75, 80..100 degree directions), the shimmer, the background movie loop. Note how far your save has progressed: G[2] picks the background | system_flow 4.2, 8 #7 |
| B03 | P2 | video | Title > New Game on a save that has seen the prologue: the white fade (73 frames) into `00_00` | system_flow 3.4 |

---

## 7. Menu captures (M7)

Take each screen in ja and en. Screenshots of the screen and of each state; a video of
opening, navigating and closing (that also records the menu transitions, M19). In the ADV
screens the PS4 buttons of action preset 0 are (adv_state 10.2): TRIANGLE system menu,
SQUARE backlog, OPTIONS quick save, R1 skip toggle, L1 auto, R2 fast-forward (hold), L2 rewind
(hold), cancel hides the window.

| id | screen | how to reach it | capture | open points it settles | ref |
|---|---|---|---|---|---|
| M01 | system menu (and its mode panel) | TRIANGLE in the ADV screen | each item highlighted, with its help line | layout of the entry 0x6758E / 0x6CA83 / 0x6E683 | ARCHITECTURE 9.3; engine 8.3 |
| M02 | save screen | system menu > Save | an empty page, a page with saved slots (thumbnail, date, scene title), page switching, overwrite, copy and delete confirmations, the completion notice | layout of entry 0x6F423 | adv_state 7 |
| M03 | load screen and Continue | system menu > Load; title > Load; title > Continue | the slot list including auto saves (100..138) and the quick save (139), the load confirmation | | adv_state 7.2, 7.3 |
| M04 | `_SELA` save prompt (ui_text 216) | at a chapter start reached by playing, not after a load (F1118 = 0). First: `02_00_ARC02_1`, F023 5 / F022 34, statement 57 | both buttons highlighted | widget 0xD5 / 0xD6 layout: read from the eboot (system_flow 5.7, package K13); a capture would check it | system_flow 5.1, 8 #4; vm_variables 4.5 |
| M05 | `_SELS` prompt | the extra-scenario menu script (`QACALL`, statement 204) | both buttons highlighted | the same widgets (system_flow 5.7) | system_flow 5.1 |
| M06 | backlog | SQUARE in the ADV screen | open, scroll to older pages, an entry with the voice-replay mark, voice replay, the jump confirmation, close | layout of entry 0x3BFB0 | adv_state 5.4, 5.5 |
| M07 | config | system menu or title > Config | all three tabs: text settings (16 rows), sound (26 rows including the 19 character rows), controls (pictures); every row focused, a slider moved, the test buttons | faithful layout check | adv_state 9.1 |
| M08 | in-game flowchart | system menu > flowchart | the chart, a node selected, the rewind-to-scene confirmation (ui 1498) | | gallery 5.5 |
| M09 | quick save, quick load, autosave notices | OPTIONS in ADV; system menu > quick load; the autosave notice at boot | each notice | | adv_state 7.2; system_flow 3.4 |
| M10 | confirm dialogs | system menu: return to title, save system data, next scene, previous scene | each dialog with both buttons | the dialog layout | engine 8.3 |
| M11 | title menu | after the boot | each item highlighted, with its help line | | system_flow 4.4 |
| M12 | gallery menu | title > Gallery | each item and its help line; is there a voice-listening item (ui 226)? | which help-line ids are shown; whether ui 226 is used | gallery 13 #8 |
| M13 | CG gallery | Gallery > CG | category tabs, a page with locked and unlocked tiles, the viewer (zoom, pan, variant switching), page change | viewer behaviour of x / y and the +0x1C flag | gallery 4, 13 #3 |
| M14 | music player | Gallery > music | the list, the playing state, the credits panel; does a track loop? | | gallery 6 |
| M15 | movie gallery | Gallery > movies | the card order, locked cards | which card is item 0 | gallery 7, 13 #7 |
| M16 | extra scenarios / ending list | Gallery > extra scenarios | the 25-entry list with locked and unlocked rows, the play confirmation (help messages, ui 1472..1496) | | gallery 8 |
| M17 | flowchart (gallery) | Gallery > Flowchart | several scroll positions (start, a chapter boundary, a branch with several rows); the connector lines between nodes; chapter labels; the node preview (thumbnail, title, synopsis); the launch confirmation; video of scrolling | **line drawing not decoded** (0x25950 / 0x25F60 / 0x260D0, connector codes) | gallery 5, 13 #1 |
| M18 | menu transitions | open and close each screen above | video, about 2 s per open / close | engine transition types 18, 20..24, 68..70 (zoom, slide, second-rule variants), now a cross-fade stand-in | compositing 7.4, 14 #8; ARCHITECTURE 11 #5 |
| M19 | ADV mode indicators | L1 auto, R1 skip, R2 fast-forward, L2 rewind (hold), cancel to hide the window | video: the HUD marks and how the screen looks during rewind | | adv_state 5.1, 6 |

---

## 8. Optional: a real save

A read-only copy of a real `savedata000` would let `check_save_image` run on PS4-made records
(adv_state 15 #1; RR 9.5). Two tests skip today because none exists ("no savedata000"). The
tests look in the shadPS4 folder `%APPDATA%\shadPS4\home\1000\savedata\CUSA47999\savedata000`
(read only; copy it to scratch first). Save data copied from a retail PS4 to USB is
encrypted by the console, so a plain USB copy is probably not readable here [not verified].

---

## 9. Locations (generated)

Generated on 2026-10-25 with
`.venv\Scripts\python.exe tools\setup\ps4_capture_targets.py --limit 1` (PYTHONPATH = tools).
Script order is the allscr order; `00_02` (debug-only), `TEST_SCRIPT` and `JUMP*` are left
out, and the extras are searched last. "uses" counts the matching statements in those scripts.
Regenerate the table rather than editing it by hand.

| id | checkpoint | uses | script (index) | scene | F023 | F022 | stmt | offset | after message | last text ids | statement |
|---|---|---:|---|---:|---:|---:|---:|---|---|---|---|
| E01 | first rule wipe _FADS(2,ms,rule) | 946 | 00_00 (1) | 240 | 1 | 1 | 7965 | 0x22074 | 366 of 387 | $000479 | `_FADS(2,900,19);` |
| E02.26 | rule wipe with RULE_26 (most used) | 147 | 02_00_ARC02_1B (36) | 332 | 5 | 35 | 2413 | 0xa9de | 104 of 138 | $002802 | `_FADS(2,500,26);` |
| E02.27 | rule wipe with RULE_27 | 141 | 02_00_ARC02_4 (48) | 344 | 5 | 46 | 1009 | 0x4416 | 48 of 62 | $003112 | `_FADS(2,600,27);` |
| E02.17 | rule wipe with RULE_17 | 76 | 03_00_ARC03_5B (70) | 366 | 7 | 68 | 3316 | 0xea18 | 163 of 193 | $004628 | `_FADS(2,2000,17);` |
| E02.28 | rule wipe with RULE_28 | 60 | 04_00_ARC04_4_1B (91) | 387 | 9 | 89 | 1258 | 0x5b37 | 48 of 113 | $006322 | `_FADS(2,300,28);` |
| E03 | cross-fade _FADS(0, >=500 ms) while a message is on screen | 202 | 01_00_ARC01_1 (4) | 300 | 3 | 3 | 2001 | 0x89f4 | 85 of 115 | $001235 | `_FADS(0,1500,0,0);` |
| E04.1 | _FADS with window argument 1 while a message is on screen | 1 | 09_00_ARC09_3_1 (290) | 586 | 20 | 288 | 1476 | 0x6201 | 58 of 67 | $021608 | `_FADS(0,1000,0,1);` |
| E04.2 | _FADS with window argument 2 while a message is on screen | 45 | 03_01_CIEL03_2_1 (74) | 370 | 8 | 72 | 1790 | 0x7db1 | 79 of 100 | $004958 | `_FADS(0,300,0,2);` |
| E05 | _STBR mode 17 (ps_dodge5), no flag | 2931 | 00_00 (1) | 240 | 1 | 1 | 5466 | 0x17051 | 269 of 387 | $000382 | `_STBR(4,17);` |
| E05f | _STBR mode 17 with flag (4th argument 1) | 538 | 03_00_ARC03_5B (70) | 366 | 7 | 68 | 1701 | 0x764b | 92 of 193 | $004553 | `_STBR(33,17,,1);` |
| E06 | _STBR mode 5 (nexl dodge), no flag | 106 | 01_00_ARC01_7 (24) | 320 | 3 | 23 | 56 | 0x46b | 0 of 208 | - | `_STBR(5,5);` |
| E06f | _STBR mode 5 with flag | 178 | 02_00_ARC02_6 (54) | 350 | 5 | 52 | 1071 | 0x4627 | 58 of 125 | $003547 | `_STBR(17,5,,1);` |
| E07.7f | _STBR mode 7 (multiplicative) with flag | 56 | 05_00_ARC05_9 (136) | 432 | 11 | 134 | 3961 | 0x1268b | 149 of 149 | $010259 | `_STBR(10,7,,1);` |
| E07.9f | _STBR mode 9 (screen) with flag | 16 | 03_00_ARC03_5B (70) | 366 | 7 | 68 | 1712 | 0x7764 | 92 of 193 | $004553 | `_STBR(35,9,,1);` |
| E07.10f | _STBR mode 10 (sub) with flag | 64 | 05_00_ARC05_9B (137) | 433 | 11 | 135 | 2970 | 0xe15c | 98 of 122 | $010357 $010358 | `_STBR(13,10,,1);` |
| E08.11 | _STBR mode 11 (opening) | 3150 | 00_00 (1) | 240 | 1 | 1 | 3789 | 0xfde2 | 192 of 387 | $000304 | `_STBR(38,11);` |
| E08.19 | _STBR mode 19 (opening) | 484 | 00_00 (1) | 240 | 1 | 1 | 2256 | 0x9630 | 114 of 387 | $000223 | `_STBR(35,19);` |
| E08.22 | _STBR mode 22 (opening) | 1366 | 00_00 (1) | 240 | 1 | 1 | 652 | 0x2b0e | 32 of 387 | $000139 | `_STBR(12,22);` |
| E08.23 | _STBR mode 23 (opening) | 1908 | 00_00 (1) | 240 | 1 | 1 | 63 | 0x42f | 0 of 387 | - | `_STBR(4,23);` |
| E08.24 | _STBR mode 24 (opening) | 1387 | 00_00 (1) | 240 | 1 | 1 | 2654 | 0xb102 | 135 of 387 | $000245 | `_STBR(10,24);` |
| E09.1 | _STEC 1 (solid colour silhouette) | 3626 | 00_00 (1) | 240 | 1 | 1 | 967 | 0x4006 | 49 of 387 | $000156 | `_STEC(18,1,0x000000);` |
| E09.2 | _STEC 2 (grey, swapped luma weights) | 1338 | 01_00_ARC01_1 (4) | 300 | 3 | 3 | 611 | 0x2852 | 27 of 115 | $001173 | `_STEC(15,2);` |
| E09.3 | _STEC 3 (sepia) | 502 | 01_00_ARC01_1 (4) | 300 | 3 | 3 | 81 | 0x528 | 0 of 115 | - | `_STEC(3,3);` |
| E09.4 | _STEC 4 (negative) | 81 | 00_00 (1) | 240 | 1 | 1 | 2490 | 0xa603 | 125 of 387 | $000235 | `_STEC(3,4);` |
| E09.77 | _STEC 77 (grey, BT.709 weights) | 102 | 03_00_ARC03_1 (60) | 356 | 7 | 58 | 79 | 0x54e | 0 of 71 | - | `_STEC(6,77);` |
| E09.44 | _STEC 44 (colour grade, most used) | 465 | 03_01_CIEL03_3_3C (80) | 376 | 8 | 78 | 673 | 0x2d49 | 27 of 100 | $005495 | `_STEC(20,44);` |
| E10.2 | _SNX2 brightness | 1678 | 00_00 (1) | 240 | 1 | 1 | 2258 | 0x9655 | 114 of 387 | $000223 | `_SNX2(35,27);` |
| E10.3 | _SNX3 contrast | 845 | 00_00 (1) | 240 | 1 | 1 | 4763 | 0x14012 | 235 of 387 | $000348 | `_SNX3(21,32);` |
| E11 | _SQK2 layer quake | 1236 | 00_00 (1) | 240 | 1 | 1 | 215 | 0xeb3 | 8 of 387 | $000114 | `_SQK2(-1,1,4,0,600,0,0,0,0);` |
| E12 | _SHK2 layer shake | 125 | 05_00_ARC05_9_2B (140) | 436 | 11 | 138 | 3541 | 0x110fa | 95 of 169 | $010517 | `_SHK2(17,0,3,300,-1,0);` |
| E13 | _SSW2 layer swing | 200 | 02_00_ARC02_1 (35) | 331 | 5 | 34 | 399 | 0x1a48 | 14 of 99 | $002610 | `_SSW2(11,7,4,1000,2000,1,-1,0);` |
| E14.SHOK | _SHOK screen quake | 870 | 02_00_ARC02_1B (36) | 332 | 5 | 35 | 1636 | 0x743a | 65 of 138 | $002761 $002762 | `_SHOK(1,3,100,18);` |
| E14.MSQK | _MSQK message-window quake | 30 | QA_02 (496) | 1001 | 37 | 493 | 1362 | 0x687f | 17 of 18 | $042852 | `_MSQK(500,5,1);` |
| E15 | _FADE(40) screen effect 4, default angle | 10 | 00_00 (1) | 240 | 1 | 1 | 1331 | 0x58fa | 68 of 387 | $000175 | `_FADE(40,);` |
| E15a | _FADE(40,angle,preset) | 18 | 02_00_ARC02_1 (35) | 331 | 5 | 34 | 407 | 0x1b64 | 14 of 99 | $002610 | `_FADE(40,-90,16);` |
| E16.0 | crop / mask layer, edge style a17 = 0 | 2174 | 00_00 (1) | 240 | 1 | 1 | 964 | 0x3f6e | 49 of 387 | $000156 | `_STCH(17,img1688,266,82,1000,255,518,,CC,,,,160,-285,888,1650,90,0);` |
| E16.1 | crop layer, a17 = 1 | 211 | 01_00_ARC01_1 (4) | 300 | 3 | 3 | 345 | 0x16ea | 13 of 115 | $001158 | `_STCH(12,img0008,710,3,1000,255,1450,,CC,,,,61,-154,964,1389,30,1);` |
| E16.2 | crop layer, a17 = 2 | 12 | 01_00_ARC01_1 (4) | 300 | 3 | 3 | 1642 | 0x6e59 | 74 of 115 | $001224 | `_STCH(16,img0007,962,-405,1000,0,2766,,CC,,,,1273,-256,980,1587,15,2);` |
| E16.7 | crop layer, a17 = 7 | 37 | 04_00_ARC04_6_2B (97) | 393 | 9 | 95 | 1397 | 0x5ef0 | 58 of 140 | $006918 | `_STCH(12,img5008,258,164,1000,192,6214,,CC,,,,1,42,470,135,30,7);` |
| E16.6 | crop layer, a17 = 6 (single use) | 1 | 14_01A_CIEL14A_5 (459) | 755 | 33 | 456 | 845 | 0x3da8 | 37 of 162 | $038117 | `_STCH(7,img1788,351,134,1000,255,858,,CC,10,,,532,92,704,1740,0,6);` |
| E17 | _STTR per-layer rule | 111 | 02_00_ARC02_4B (49) | 345 | 5 | 47 | 62 | 0x43e | 0 of 70 | - | `_STTR(3,26);` |
| E18 | _STCF (reader not traced) | 5159 | 00_00 (1) | 240 | 1 | 1 | 55 | 0x367 | 0 of 387 | - | `_STCF(0,8);` |
| E19.STNS | _STNS noise | 196 | 02_00_ARC02_6 (54) | 350 | 5 | 52 | 1072 | 0x4636 | 58 of 125 | $003547 | `_STNS(17,11);` |
| E19.STRA | _STRA (no reader found) | 569 | 00_00 (1) | 240 | 1 | 1 | 2664 | 0xb209 | 135 of 387 | $000245 | `_STRA(12,1);` |
| E20 | _SPFC special picture | 487 | 07_00_ARC07_1_2 (202) | 498 | 16 | 200 | 1239 | 0x5572 | 53 of 61 | $016959 | `_SPFC(29,aki_a_30_01_n01,,,,,12,);` |
| T01 | @g style run (text ids 4478..4620) | 136 | 03_00_ARC03_5B (70) | 366 | 7 | 68 | 386 | 0x1af0 | 19 of 193 | $004477 | `_ZM [$004478]` |
| T02 | @g@b (text ids 4527, 4529, 4531, 4533) | 4 | 03_00_ARC03_5B (70) | 366 | 7 | 68 | 1311 | 0x5bd0 | 68 of 193 | $004526 | `_ZM [$004527]` |
| T03.c1 | @c1 colour word in a message | 17 | 08_01_CIEL08_5 (279) | 575 | 19 | 277 | 4984 | 0x16f0a | 183 of 203 | $019507 | `_ZM [$019508]` |
| T03.c2 | @c2 | 6 | 14_01B_01_CIEL14B_1B (462) | 758 | 34 | 459 | 872 | 0x3ef4 | 33 of 151 | $038474 | `_ZM [$038475]` |
| T03.c3 | @c3 | 1 | 05_01_CIEL05_1C (144) | 440 | 12 | 142 | 491 | 0x2527 | 17 of 173 | $011122 | `_ZM [$011123]` |
| T03.c4 | @c4 | 6 | 10_00_ARC10LIVING (322) | 618 | 24 | 320 | 3883 | 0x10b03 | 170 of 218 | $027004 | `_ZM [$027005]` |
| T03.c7 | @c7 (extension value) | 1 | 05_01_CIEL05_1C (144) | 440 | 12 | 142 | 491 | 0x2527 | 17 of 173 | $011122 | `_ZM [$011123]` |
| T04 | 4th message of a page, window picture drawn (F1104 = 0) | 3147 | 00_00 (1) | 240 | 1 | 1 | 182 | 0xc6c | 7 of 387 | $000113 | `_ZM [$000114]` |
| T05 | 4th message of a page, window picture hidden (F1104 = 1) | 237 | 01_00_ARC01_10 (5) | 301 | 3 | 4 | 117 | 0x763 | 4 of 34 | $002563 | `_ZM [$002565]` |
| T06 | speech-bubble window (F1105 = 3..32) | 631 | QA_01 (495) | 1000 | 37 | 492 | 274 | 0x167b | 0 of 28 | - | `_WKST(F1105,eq,5);` |
| T07 | dark overlay window 1 or 2 (F1105 = 1..2) | 26 | 04_00_ARC04_5 (93) | 389 | 9 | 91 | 53 | 0x345 | 0 of 73 | - | `_WKST(F1105,eq,1);` |
| T08.FCLN | _FCLN full-screen narration layout | 26 | 06_00_ARC06_1_3 (173) | 469 | 14 | 171 | 1263 | 0x58b9 | 42 of 65 | $014232 | `_FCLN(600,200,,,32,4);` |
| T08.FCSP | _FCSP | 31 | 06_00_ARC06_1_3 (173) | 469 | 14 | 171 | 1302 | 0x5c2f | 43 of 65 | $014233 | `_FCSP();` |
| T09 | @t simultaneous text (ids 9328, 23613, 36678) | 3 | 05_00_ARC05_3_1 (124) | 420 | 11 | 122 | 452 | 0x2092 | 18 of 86 | $009327 | `_ZM [$009328]` |
| T10 | first story choice menu _SELD | 83 | 01_00_ARC01_1 (4) | 300 | 3 | 3 | 2644 | 0xb62e | 115 of 115 | $001265 | `_SELD` |
| A01 | first voiced line _VPLY (voice over BGM, ducking) | 16593 | 00_00 (1) | 240 | 1 | 1 | 537 | 0x2341 | 26 of 387 | $000133 | `_VPLY(D10_00_00_0000_0,03fb5);` |
| M04 | _SELA prompt | 26 | 02_00_ARC02_1 (35) | 331 | 5 | 34 | 57 | 0x377 | 0 of 99 | - | `_SELA` |
| M05 | _SELS prompt | 1 | QACALL (529) | 3100 | 37 | 492 | 204 | 0xdca | 0 of 0 | - | `_SELS` |

Cross-checks against the documents: A01's offset 0x2341 is the one RR 7.6 gives for the
first voiced line. E05 and E05f together give 3,469 mode-17 statements, against 3,473 in
compositing 5.2. E01 finds 946 rule wipes, against 947 there. In both cases the difference is
the statements in the excluded scripts (`00_02` holds one `_FADS(2,1500,29)`). Easing id 70
(layers 13 #8) needs no capture: no retail motion or command uses it (layers 8.5).

---

## 10. Checklist

Tick when the files are in `samples/ps4/` and listed in `notes.txt`.

| id | P | ja | en | notes |
|---|---|---|---|---|
| E01, E01-FF | P1 | [ ] | - | |
| E03 | P1 | [ ] | - | |
| E05, E06 | P1 | [ ] | - | |
| E08.11 / .19 / .22 / .23 / .24 | P1 | [ ] | - | |
| E09.1 / .2 / .3 / .4, E10.2 / .3 | P1 | [ ] | - | |
| E11, E15, E18, E19.STRA | P1 | [ ] | - | |
| E16.0 / .1 / .2 | P1 | [ ] | - | |
| T04, T05, T10, T11 | P1 | [ ] | [ ] | |
| A01 | P1 | [ ] | - | |
| B01, B02 | P1 | [ ] | [ ] | |
| E02.26 / .27 / .17 / .28, E04.2 | P2 | [ ] | - | |
| E05f, E06f, E07.9f | P2 | [ ] | - | |
| E09.77, E09.44 | P2 | [ ] | - | |
| E13, E14.SHOK, E15a | P2 | [ ] | - | |
| E16.7, E17, E19.STNS | P2 | [ ] | - | |
| T01, T02, T07, T09 | P2 | [ ] | [ ] | |
| B03 | P2 | [ ] | - | |
| E04.1, E07.7f, E07.10f, E12, E14.MSQK, E16.6, E20 | P3 | [ ] | - | |
| T03.c1 / .c2 / .c3 / .c4, T06, T08 | P3 | [ ] | [ ] | |
| M01-M19 (before M7) | - | [ ] | [ ] | |
