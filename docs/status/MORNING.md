# Overnight report (2026-10-25 evening)

Work stopped at 21:45 because the weekly usage limit was close: from about 21:31 agents began
dying with API errors. Both running workflows were stopped by the lead so no more usage was spent
on failing retries. Everything below is committed; nothing in flight was lost except the partial
work of the three agents listed under "Stopped mid-task" (their files are on disk and committed).

## State at the stop

- C# solution builds with `-warnaserror`: 0 warnings, 0 errors.
- C# tests: **870 / 870 pass** (Synthetic 294, Render 48 GPU tests, Data 528 on the real game data).
- Python oracle tests: **493 passed, 2 skipped** (both need a retail PS4 save that does not exist here).

## Wave 2 (PACE.md 7.2): what landed

| package | result | review |
|---|---|---|
| L1 state-block contracts (SceneState, HistoryRecord, SoundChannelRecord, ConfigBlock, SystemBlock, Vars) | done | not refuted |
| L2 docs aligned with the spikes and decisions | done | not refuted |
| H1 app skeleton (`Tsuki.App --smoke`, SDL3 + GL 3.3, FFmpeg lookup) | done | not refuted |
| H2 GoldenSource (IGameData over the golden slice) | done | 1 major fixed, re-check clean |
| A1 MZP images (all formats) + oracle data generators moved | done | not refuted |
| A2 SELF/ELF + catalog (14,744 records, name search, ui_text) | done | not refuted |
| B Text_Layout port (+ textbox constants corrected to eboot float32) | done | not refuted |
| C1 statement segmenting + both argument readers (549 scripts) | done | not refuted |
| C2 VM variables + flow (fuzz cases, slice traces) | done | not refuted |
| D1 display-list executor (blend states, shaders, capture path, uploads) | done | **re-check still reports 1 serious issue** (see wave2/D1.md) |
| E1 media (ATRAC9 decoder, mixer, HE-VAG, movie thread) | partial | not refuted |
| G uicalls (title/choice draw lists) | done | 1 major fixed, re-check clean |
| H3a oracle gaps for M2 (23 _STCG records, retail path, traces, provenance) | repaired | re-check did not finish (limit) |
| A3a text/fonts/script tables | **stopped mid-task** (3 attempts died on API errors); its files build and its tests pass, but the package is not reviewed |
| A3b, H3b, E2 | not started | |

Per-package details and the exact proof commands: `docs/status/wave2/<id>.md`.

## Research fix-ups (5 open engine questions)

| question | result |
|---|---|
| text vertical scale (81 vs 78 px) | decoded, review clean (`docs/research/text_vertical_scale.md`, `tools/tsuki/text_vscale.py`) |
| wait model per message end mode | decoded; review found 2 serious issues, repaired, re-check clean (`docs/formats/wait_model.md`) |
| layer quad formula for motion-animated anchors | decoded, review clean (layers.md 7.2) |
| `_SELD` per-line loop (choices) | decoded, review clean (`tools/tsuki/misc_seld.py`) |
| quake / shake / swing | decoded; review found 2 serious issues; **repair stopped mid-task** (limit) |

## Next steps when usage resets

1. Re-check H3a; repair D1's remaining issue; finish quake's repair.
2. Review A3a, then run A3b (GameFolderSource + census), H3b (oracle page-1 render), E2 (SoundModel),
   finish E1.
3. Wave 3: D2 (layer model, now unblocked by the layers research), then M2a integration on
   GoldenSource: the first playable scene.
4. Hook tightening (decisions.md item 8).

## Needs the user

Unchanged from `followups.md`: PS4 screenshots before M4/M7, the M2 review session (30 min of the
opening in ja/en once M2 runs), and an English-display Windows account before M6/M8.
