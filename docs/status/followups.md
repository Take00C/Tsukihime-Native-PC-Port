# Follow-ups

Open items found by agents and reviewers that belong to a later work package.
Each line names where it came from and who should pick it up. Remove a line
when it is done (the commit that fixes it should say so).

## From M0-prep (oracle exporter + review, 2026-10-25)

- **Oracle slice coverage for M2.** `samples/golden/slice/eboot/catalog.json` has catalog records
  only for the 18 displayed pictures. 23 of the 39 `_STCG` names that `00_01` executes have no
  record (img0700, img0758, img0784, img1592, img1621, img1626, img1686, img2285, img2343,
  img2459-2461, img2599-2606, img2634-2636). Extend `tools/oracle/export_slice.py`. Owner: wave-2
  oracle/MZP+catalog package.
- **Retail path not exported (RUNTIME_REQUIREMENTS 7.6).** START_SCRIPT (allscr section 3), 00_00
  up to 0x2341, voice #16309, img1696_11_11, `_STBR(4,23)`, motions 1..5. The slice tracer stops on
  JUMP/GOTO/choices; it needs jump handling. Owner: wave-2 segmenting/readers -> flow package.
- **M2 acceptance 7-9 golden data missing.** SoundModel trace for
  `_SEPL(7,1,MOON_SE03028,,\`001:8000,,\`017:30)`, `_WTTM(4000,1)` and reveal-speed traces, sampled
  motion values, and the oracle page-1 render (`tools/oracle/render.py`). Owner: model-trace and
  render areas (ARCHITECTURE 10.1).
- **RUNTIME_REQUIREMENTS 7.2 correction.** On a normal New Game, `_IF__` jumps when false and F026
  is 0, so both 00_01 and 00_02 skip statements 40..68 (`_STCC(-1)`, `_FADS(1,0,0)` and the resets).
  00_01 ends at `_RTMN` (0x7D4); its `_END_` never runs. RR 7.5 check 2 should compare against the
  executed trace (`trace.steps`), not the full statement list. Owner: docs.
- **ROADMAP M0 says 17 slice pictures; it is 18** (17 in 00_02 plus BLACK in 00_01). Owner: docs.
- **`tsuki.textbox` constants are 1 ulp off the eboot float32 tables**: `SCALE_TABLE[8]`, `[9]`
  (VA 0x2D3730) and `_WSIZE` widths [2], [4], [21] (VA 0x200BB0). They affect `@h`/`@w` scaling and
  speech-bubble windows 35, 37, 54. The export already uses the eboot values. Owner: textbox port.
- **Golden manifest provenance.** `verify_area` does not detect tool-source drift or input-hash
  drift; the manifest uses an undocumented `project:` input prefix (document it in the schema).
  Owner: oracle.
- **Real-data oracle tests leave game-derived output in pytest's tmp_path** (283 MB was found and
  deleted from %TEMP% on 2026-10-25). Make the tests delete their export directories. Owner: oracle.
- **Click-wait icon source undocumented** (textbox.md 3.1/8.3, flag 0x400 raises icon alpha at
  VA 0xDF08E4). Needed if the M2 page-1 checkpoint is captured while waiting for input. Owner:
  textbox / UI.
- **Pre-commit allow-list gaps** reported by the setup agent: binaries such as .png are accepted
  outside samples/ (ARCHITECTURE 10.8 wants them blocked unless documented). Owner: lead.
- **`ps4_capture_targets.py` has no test.** Owner: lead.

## Needs the user (skip until they are back)

- PS4 Share screenshots/clips of the effect checkpoints (before M4) and menus (before M7), listed in
  `docs/status/ps4_captures.md`.
- Review sessions listed in ROADMAP (M2: 30 min of the opening in ja/en, and later ones).
- A second Windows account with English display language for the release-candidate test (M6/M8).
