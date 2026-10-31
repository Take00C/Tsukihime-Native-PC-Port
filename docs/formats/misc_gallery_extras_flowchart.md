# Gallery, extras, flowchart and unlock data (alias)

The full document for this topic is [gallery.md](gallery.md). It covers the CG gallery (458
groups, 1,972 entries, 105 aliases), the flowchart (277 columns, 1,286 nodes, 630 edges, the
column filter verified against the engine under unicorn), the music player, movie gallery,
ending list (25 "Teach Me, Miss Ciel" extra scenarios), the persistent unlock bit sets
(scene_seen / scene_done / edge_seen / movie_seen in var_G, picture_seen and bgm_heard in
system block +0x52C, choice_taken in +0x16F4), the gallery replay through script ARCHIVE
(scene 3000), and the dead extra_parts / legacy tables.

Python: `tools/tsuki/misc_gallery_extras_flowchart.py` (+ `_emu.py`, `_sheets.py`).
Tests: `tools/tests/test_misc_gallery_extras_flowchart.py`.
Samples: `samples/misc_gallery_extras_flowchart/`.
