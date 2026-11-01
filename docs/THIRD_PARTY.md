# Third-party code

The port's own code, everything the sections below do not list, is under the Mozilla Public License 2.0: `LICENSE` at the
repository root, published as `licenses\LICENSE.txt` beside the programs. This file lists the code and the libraries of others
that the repository or the release download carries, each with the notice its licence asks for. The licence texts travel with the
programs in the `licenses\` folder (`FFmpeg-LGPL.txt`, `FFmpeg-GPL-3.0.txt`, `FFmpeg-SOURCE.txt`, `knife-MPL-2.0.txt`, `SDL3-zlib.txt`,
`LICENSE.txt`, and this file as `THIRD_PARTY.md`); the copies the build takes are tracked in `src/Tsuki.Launcher/Licenses/`.

Self-contained publishes include `licenses/dotnet-LICENSE.txt` and
`licenses/dotnet-THIRD-PARTY-NOTICES.txt`, copied unchanged from the resolved .NET runtime pack.
These full notices must match the runtime in the package.

## knife (MPL-2.0)

knife by AuroraWright, https://github.com/AuroraWright/knife, is a Rust reimplementation of the same
engine (Switch build) and is licensed under the Mozilla Public License 2.0. The files below translate
knife source files into C#. Under MPL-2.0 each of them stays under the MPL and carries its notice;
the rest of Tsuki is unaffected. The rules are in `docs/design/knife_port.md` 1.

| Tsuki file | translated from (knife commit b12433c) |
|---|---|
| `src/Tsuki.Ui/Framework/UiBackdrop.cs` | `src/fx.rs` (MenuBg, phase), `src/ui.rs` (draw_menu_layer, STRIPS) |
| `src/Tsuki.Ui/Framework/UiBanner.cs` | `src/ui.rs` (Banner, draw_banner) |
| `src/Tsuki.Ui/Framework/UiWidgets.cs` | `src/ui.rs` (GUIDE_SEQ, draw_guide, draw_scroll_bar, draw_dialog_win, draw_digits); the guide sequences themselves were then re-decoded from the PS4 eboot |
| `src/Tsuki.Ui/Screens/BacklogScreen.cs` | `src/ui.rs` (Screen::Backlog: key_nav, shoulder, backlog_to, draw), `src/game.rs` (backlog_quads) |
| `src/Tsuki.Ui/Screens/ConfigScreen.cs` | `src/ui.rs` (Ctl, conf_layout_at, conf_rect, conf_slider, conf_change, conf_nav, draw_config, en_radio_cell, en_toggle_cell, refresh_voice_rows); control positions come from the PS4 eboot pages |
| `src/Tsuki.Ui/Screens/Dialogs.cs` | `src/ui.rs` (Confirm, Popup, DlgOut, draw_dialog_*) |
| `src/Tsuki.Ui/Screens/ModePanelScreen.cs` | `src/ui/touch.rs` (Panel, panel_y, panel_button, panel_press, draw_touch_game): the slide-in mode panel only, laid out from MODEPNL_PARTS |
| `src/Tsuki.Ui/Screens/SaveLoadScreen.cs` | `src/ui.rs` (Screen::SaveLoad: save_list, save_frame, draw, key_nav, save / load / delete flows, INFO_ROUTE) |
| `src/Tsuki.Ui/Screens/SideFocus.cs` | `src/ui.rs` (enter_side, leave_side, side_nav) |
| `src/Tsuki.Ui/Screens/SystemMenuScreen.cs` | `src/ui.rs` (draw_menu_base, menu items and input) |
| `src/Tsuki.Ui/Screens/TitleScreen.cs` | `src/ui.rs` (Intro, enter_title, draw_title, title_entries, title_slot, act: NewGame / Continue), `src/fx.rs` (LogoFx) |
| `src/Tsuki.Ui/Screens/FlowchartChart.cs` | `src/flow.rs` (the chart layout of cFlowchart: visible rows, bands, connector shapes, node pictures, cursor movement and its line tracing); the column filter reads the PS4 table and its extra record, the cells come from the PS4 sheets |
| `src/Tsuki.Ui/Screens/FlowchartScreen.cs` | `src/ui.rs` (Screen::Flowchart: open_flow_at, flow_follow, flow_scroll_*, flow_press, flow_keep_cursor, the chart / cursor / scene card draw) |
| `src/Tsuki.Ui/Screens/GalleryScreen.cs` | `src/ui.rs` (the cGallery hub: hub_items, draw_hub_bg, draw_hub_menu, gal_nav, enter_mode, gal_replay, gallery_back, guide_for, gal_guide_tick) |
| `src/Tsuki.Ui/Screens/GalleryCg.cs` | `src/ui.rs` (cGalleryCGMain / cGalleryCGSub grid: cg_cell, cg_show, gal_follow, gal_keep_cursor, draw_cg_grid; cGalleryCGPhoto viewer: cg_geom, cg_step, cg_viewed) |
| `src/Tsuki.Ui/Screens/GalleryMusic.cs` | `src/ui.rs` (cGalleryMusic: MUSIC_CTL, the rows and panel draw, play_track, stop_track, Action::MusicCtl, the music arms of gal_nav, the music clock) |
| `src/Tsuki.Ui/Screens/GalleryMovies.cs` | `src/ui.rs` (cGalleryMovie: movie_list, Screen::Movies, play_gallery_movie, gal_movie_step, gal_movie_skip) |
| `src/Tsuki.Ui/Screens/GalleryEndings.cs` | `src/ui.rs` (cGalleryEnding: the Screen::Endings draw, ending_move, Action::Ending, Confirm::Ending) |
| `src/Tsuki.Ui/Screens/GalleryScroll.cs` | `src/ui.rs` (ListScroll, the sub_27BD4 tween) |
| `src/Tsuki.Engine/Game/GameHost.FlowJump.cs` | `src/game.rs` (`jump_node`, `jump_record`: the structure of the flowchart jump; the keep lists, the clear pairs and the node rows come from the PS4 eboot) |
| `src/Tsuki.Engine/Game/GameHost.Effects.cs` | `src/game.rs` (the `FCLN`, `FCSP`, `FLNT`, `FLOP`, `FLSP`, `FADE` arms); the state and the particles are in the two files below |
| `src/Tsuki.Engine/Game/Effects/FocusEffect.cs` | `src/game/focus.rs` (`Focus`: start, step, the line and note rolls, `quads`, `line_uv`, the per-kind line scale); the keyframes and the note records are read from the PS4 eboot (K1b), not knife's `LINES` / `NOTES` |
| `src/Tsuki.Engine/Game/Effects/GhostEffect.cs` | `src/game/ghost.rs` (`Ghost`: the presets, the 60 Hz step, the amplitude and angle animation, the channel offsets and colour words of `render`); the draw is rebuilt as display-list commands |
| `src/Tsuki.Engine/Stage/StageDraw.Effects.cs` | `src/stage.rs` (`Haze::params`: the haze amplitude, waves and phases of one STRA entry; `noise_pass`: the STNS picture, scale and offsets); the mask / rule group is our own reading of layers.md 9 |
| `src/Tsuki.Engine/Stage/StageDraw.Clip.cs` | `src/stage.rs` (`ClipRect::inset_rect`, `ClipRect::soft`, `ClipRect::band`, unrotated branches only: the scissor shrink, the soft nine-slice mask and the ridge of STCH a16 / a17); the texture radii come from the PS4 eboot (K1b), the group order is the stage's own |

## FFmpeg (LGPL v3 or later)

The game decodes its sound (ATRAC9: the voices, the effects, the music) and its movies (H.264 video, AAC audio) with FFmpeg's shared libraries,
https://ffmpeg.org: `avcodec-63.dll`, `avformat-63.dll`, `avutil-61.dll` and `swresample-7.dll` are loaded (`FfmpegLibrary.RequiredDlls`); `swscale-10.dll`,
`avfilter-12.dll` and `avdevice-63.dll` travel with them from the same package and are not used. They are the unmodified DLLs of BtbN's `win64-lgpl-shared-9.0` build of
FFmpeg 9.0.1 (`ffmpeg version n9.0.1-11-ge47273f4d9-20260831`), winget package `BtbN.FFmpeg.LGPL.Shared.9.0` 9.0.1-20260831; the release page,
https://github.com/BtbN/FFmpeg-Builds/releases, carries the zips and the build scripts, and the FFmpeg source of that tag is at https://git.ffmpeg.org/ffmpeg.git
(`n9.0.1`). `tools/setup/ffmpeg_dlls.ps1` copies the DLLs into `third_party/ffmpeg/bin` (git-ignored) with the package's `LICENSE.txt` one level up and writes
`third_party/ffmpeg/SOURCE.txt` there (package, build, licence, the SHA-256 of every DLL); `Directory.Build.props` copies the DLLs next to `Tsukihime.exe` (the game program; it was `Tsuki.App.exe` before package K20).

The build is configured with `--enable-version3`, so the libraries are under the **GNU Lesser General Public License, version 3 or later**: `avcodec_license()` answers
"LGPL version 3 or later" (`Tsukihime.exe --smoke` prints it) and the package's `LICENSE.txt` is the LGPL v3 text (ARCHITECTURE 0.5). The release ships that text
as `licenses\FFmpeg-LGPL.txt` and, because the LGPL v3 is a set of additional permissions on top of the GNU General Public License v3 and incorporates its terms, the
GPL v3 text as `licenses\FFmpeg-GPL-3.0.txt` next to it (both tracked in `src/Tsuki.Launcher/Licenses/`). No GPL component is in the build (there is no `postproc`
DLL): the GPL text is there as the document the LGPL refers to, not because any shipped library is GPL-licensed. Tsuki does not link against FFmpeg: `src/Tsuki.Media/Ffmpeg/FfmpegLibrary.cs` loads the DLLs from the application folder at run time
through the `FFmpeg.AutoGen` 9.0.1.1 bindings (https://github.com/Ruslan-B/FFmpeg.AutoGen, MIT), so a user can replace them with another build of the same major
versions, as the LGPL asks.

## SDL3 (zlib)

The window and its OpenGL context, the keyboard, the mouse and the game pads, the audio device, the folder dialog and the message boxes come from Simple DirectMedia
Layer 3, https://github.com/libsdl-org/SDL, under the zlib licence (`LICENSE.txt` of that repository). `SDL3.dll` is SDL 3.5.0, shipped unmodified in the NuGet package
`ppy.SDL3-CS` 2026.1002.1 (https://github.com/ppy/SDL3-CS: the C# bindings, MIT), which only `src/Tsuki.Platform` uses (ARCHITECTURE 3.1). `Silk.NET.OpenGL` 2.23.0
(https://github.com/dotnet/Silk.NET, MIT) is the OpenGL 3.3 binding of `src/Tsuki.Render`. The versions are pinned in `Directory.Packages.props`.

## MIT libraries (.NET runtime and WPF, Silk.NET, SDL3-CS, FFmpeg.AutoGen)

Four MIT-licensed components ship as DLLs in the release download (the self-contained publish of `src/Tsuki.Launcher`). The MIT licence
asks that its copyright notice and permission notice travel with every copy, so each is given here in full; the versions are the ones
pinned in `Directory.Packages.props` and by the .NET SDK (checked against the packages' `.nuspec` files and licence files on 2026-10-30).

### .NET runtime and WPF

The download carries the .NET 10 runtime (`Microsoft.NETCore.App`: `coreclr.dll`, `System.*.dll`, `msquic.dll`, ...) and the Windows
Desktop runtime for the launcher (`Microsoft.WindowsDesktop.App`: `PresentationCore.dll`, `PresentationFramework.dll`, `WindowsBase.dll`,
...), both self-contained so that players install nothing. Both are MIT: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT and
https://github.com/dotnet/wpf/blob/main/LICENSE.TXT. The runtime itself contains further third-party components, listed in
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT.

The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR
ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### Silk.NET

`Silk.NET.OpenGL.dll`, `Silk.NET.Core.dll` and `Silk.NET.Maths.dll` 2.23.0 (https://github.com/dotnet/Silk.NET; the NuGet packages
`Silk.NET.OpenGL`, `Silk.NET.Core`, `Silk.NET.Maths` declare the licence expression `MIT` and the author ".NET Foundation and
Contributors") are the OpenGL 3.3 binding of `src/Tsuki.Render`.

MIT License

Copyright (c) 2019-2020 Ultz Limited
Copyright (c) 2021- .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR
ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### SDL3-CS

`SDL3-CS.dll` from the NuGet package `ppy.SDL3-CS` 2026.1002.1 (https://github.com/ppy/SDL3-CS; licence expression `MIT`, copyright
"Copyright (c) 2024 ppy Pty Ltd") is the C# binding of SDL3 that `src/Tsuki.Platform` uses. The native `SDL3.dll` in the same package is
SDL's own, under the zlib licence (the section above).

MIT License

Copyright (c) 2024 ppy Pty Ltd

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR
ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### FFmpeg.AutoGen

`FFmpeg.AutoGen.dll` 9.0.1.1 (https://github.com/Ruslan-B/FFmpeg.AutoGen) is the C# binding through which `src/Tsuki.Media` loads
FFmpeg's libraries at run time. The package's `LICENSE.txt` is the MIT licence below (the project's older releases were LGPL; the 9.x
packages are MIT).

MIT License

Copyright (c) 2025 Ruslan Balanukhin (Rationale One)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR
ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Research references (no code copied)

The format research cites mangetsu (https://github.com/rschlaikjer/mangetsu, MIT) and deepLuna (https://github.com/Hakanaou/deepLuna,
BSD-2-Clause) in comments of `tools/tsuki/mrg.py`, `mzp.py`, `mzx.py`, `nam.py`, `text.py` and `src/Tsuki.Data/Text/TextTable.cs` as the
source of a format fact. No code of either project is in this repository or in the download; both were read-only references in the
git-ignored `ref/` folder.
