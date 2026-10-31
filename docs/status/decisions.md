# Lead decisions

Decisions the lead (L) took so work can proceed. Each names its source; reverse one by editing
this file and the affected code in the same commit.

## 2026-10-25, after the M0 review (PACE.md 7.1 L-3 and M0.md open items)

1. **S1 loop seam stays engine-exact.** No crossfade before M7. bgm #33's seam step is in the
   original game too (the PS4 engine adds no crossfade). A crossfade option can be offered later
   as a non-default setting.
2. **The data stream's Python generators move to `tools/oracle/data.py`** in package A1, through
   the oracle runner; `tests/Tsuki.Tests.Data/Oracle/` keeps only thin callers.
3. **`GameFolderLocator`'s hard-coded default path is for dev builds only** (`#if TSUKI_DEV`).
   Release builds use `TSUKI_GAME_DIR` or the first-run folder dialog.
4. **GPU and timing measurements run one at a time.** A measurement must hold the lock file
   `artifacts/gpu.lock` (create it exclusively; delete it when done; wait and
   retry if it exists, and treat a lock older than 30 minutes as stale). Functional GPU tests that
   assert pixels, not timings, do not need the lock.
5. **`Tsuki.Data` may use `AllowUnsafeBlocks`** for memory-mapped access and the MZX0 hot loop. The
   no-P/Invoke rule for Data and Engine stays.
6. **`Directory.Build.props` / `Directory.Packages.props` stay at the repo root** (tests/ is outside
   src/). The spikes stay in `src/Spikes/` until M2 is accepted, then they are deleted. Package L2
   aligns ARCHITECTURE.md and ROADMAP.md with both.
7. **Parallel builds use per-agent output folders**: build and test with
   `--artifacts-path artifacts/<package-id>` so agents never lock each other's
   binaries. Never leave a built app, spike or test host running after a command.
8. **Hook tightening (L-4) waits until wave 2 has finished**, so no running agent is blocked
   mid-task by a new rule. It is the first item after the wave-2 review.
9. **D2 (layer model and motions) starts only after the concurrent layer-motions research has
   landed and been committed.** It is not in the first wave-2 launch.
10. **The FFmpeg build in use is LGPL v3** (`avcodec_license()`); L2 records this in ARCHITECTURE
    0.5 and 13. Shipping DLLs stays an M8 question.
