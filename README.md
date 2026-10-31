<img width="1922" height="1112" alt="Screenshot 2026-10-30 204551" src="https://github.com/user-attachments/assets/17b113af-6c33-4785-964b-e635377908fa" />
<img width="1922" height="1112" alt="Screenshot 2026-10-30 171103" src="https://github.com/user-attachments/assets/78e890b9-9da2-4d87-817c-40601b702f6f" />
<img width="1922" height="1112" alt="Screenshot 2026-10-30 205237" src="https://github.com/user-attachments/assets/b424d8e7-3434-41b7-ae4e-70e6535355f2" />
<img width="1922" height="1112" alt="Screenshot 2026-10-30 171243" src="https://github.com/user-attachments/assets/16ec727f-6bc6-49cc-9dcb-201d9c2febca" />
<img width="1922" height="1112" alt="Screenshot 2026-10-30 171523" src="https://github.com/user-attachments/assets/00a63570-0757-447a-b95c-a61624800b5f" />
<img width="1593" height="926" alt="Screenshot 2026-10-30 171957" src="https://github.com/user-attachments/assets/28158d84-44d7-4974-b643-5d64e7fd4577" />

# Tsukihime PC port

> **Players: start with the [setup guide](docs/SETUP_GUIDE.md)** (`docs/SETUP_GUIDE.md`; the same text travels beside the programs as `SETUP_GUIDE.txt`, and the
> launcher's Help & about page opens it). It says what you need, how your own game files must be laid out, the first start, the keys and what every message means.

**Tsukihime PC port** is a native Windows version of *TSUKIHIME -A piece of blue glass moon-* (PS4) that plays the files of your own copy.
Fan-made, open source.

> **Tsukihime PC port is an unofficial fan project.** It is not affiliated with, endorsed by or supported by TYPE-MOON, Aniplex, HuneX or Sony.
> TSUKIHIME and all of its art, music, text and characters belong to their rights holders. This program contains **no game data**: it only plays
> the files of a copy of *TSUKIHIME -A piece of blue glass moon-* (PS4) that you own. You must supply those files yourself; this project does not
> provide the files or link to any source of them, and it will not accept contributions that do.

## What you need

- Windows 10 or 11, 64-bit.
- Any graphics card or chip with OpenGL 3.3, **laptop integrated graphics included** (Intel HD 4000 or newer, any AMD or NVIDIA of the last ten years).
- Your own extracted PS4 game folder: `CUSA47999` (about 44 GB) holding `eboot.bin`, `image\`, `data\` and `sce_sys\`. The port reads it and never changes it;
  a dump split into the base game and its update folder works too (the setup guide shows the layouts).
- About 260 MB of disk for the port (the zip is about 105 MB, unpacked about 250 MB). Nothing to install: the .NET runtime and every library are in the download.

## Download and play

1. Download `Tsukihime-v0.1.0-win-x64.zip` from the repository's **[Releases page](../../releases)** (the *Releases* link in the
   right-hand column of the repository's front page on GitHub).
2. Unzip it anywhere and start `TsukihimeLauncher.exe`.
3. Point it at your game folder and press **PLAY**. (`Tsukihime.exe` also starts on its own; it asks for the folder on the first start.)

## What it does

The whole game in Japanese, English, Simplified and Traditional Chinese, with its voices, music, sound and movies; saves (140 slots with
thumbnails), quick save and load, a backlog with voice replay, rewind, auto and skip modes, the flowchart, the gallery (CGs, music, movies,
endings); keyboard, mouse, PlayStation and Xbox pads with matching button prompts; windowed or borderless fullscreen; F12 screenshots;
text-hooker output for dictionary tools (LunaHook / Textractor, a WebSocket feed for Yomitan pages, the clipboard); import of a PS4 or
shadPS4 save.

## Status

Playable end to end on the build it was developed against (CUSA47999, update 1.01); other dumps are identified and their tables located by
content, or the game refuses to start and says what is missing. **v0.1.0 is the first public build and a pre-release.** Report problems in
Issues with the log and the build line ([CONTRIBUTING.md](CONTRIBUTING.md) says how); **never attach game files, dumps or screenshots of the
game**.

## Credits and licences

- [knife](https://github.com/AuroraWright/knife) by AuroraWright (MPL-2.0): the menu code and several effects are translations of it, listed
  in [docs/THIRD_PARTY.md](docs/THIRD_PARTY.md).
- [FFmpeg](https://ffmpeg.org) (LGPL v3 or later, an unmodified BtbN build), [SDL3](https://github.com/libsdl-org/SDL) (zlib), the .NET
  runtime and WPF, [Silk.NET](https://github.com/dotnet/Silk.NET), [SDL3-CS](https://github.com/ppy/SDL3-CS) and
  [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) (MIT): the notices are in `docs/THIRD_PARTY.md` and travel with the download
  in `licenses\`.
- The engine research started from the public work on the HuneX engine (mangetsu, deepLuna, knife); the format documents under `docs/` cite
  their sources.
- The port was built with the help of AI coding agents; the status notes under `docs/status/` record what was verified, how, and what was not.
- **Licence:** source code: MPL-2.0 (see [LICENSE](LICENSE)). Files translated from knife remain under MPL-2.0 and are listed in
  `docs/THIRD_PARTY.md`.

---

## For developers

The rest of this file is the developer's document. The port is a clean reimplementation of the engine of **TSUKIHIME -A piece of blue glass moon-** (PS4,
CUSA47999) as a native PC runtime that reads the user's own game files at run time. The PS4
executable runs HuneX's in-house engine ("Hunex Consumer Library", module tags `nexl_*`); it is
not KiriKiri, so existing KiriKiri tooling does not apply.

The port is **playable end to end**: `src/` holds the C# engine ("Tsuki", .NET 10, OpenGL 3.3, SDL3,
FFmpeg), the game program `Tsukihime.exe` (project `Tsuki.App`) and the WPF launcher `TsukihimeLauncher.exe` (project `Tsuki.Launcher`); "How to run" below starts
it. The documents under `docs/` remain the reference the engine was written from: the data formats and
the PS4 engine's behaviour, backed by Python reference readers and tests that run against a real copy
of the game.

Where to start reading:

- [`docs/FORMATS.md`](docs/FORMATS.md): the format map. It indexes every format document, gives the
  status of each format, how the pieces reference each other and the open problems in priority
  order.
- [`docs/RUNTIME_REQUIREMENTS.md`](docs/RUNTIME_REQUIREMENTS.md): what the runtime must implement,
  the codecs, the output, the priorities and the first vertical slice.
- [`docs/formats/`](docs/formats/): one document per format or subsystem (containers, images,
  text, script, audio, fonts, text box, archives / UI / movies, engine).

---

## Legal note

- **This port never contains or distributes game data.** The repository holds only our own
  documentation and code: no archives, pictures, audio, video, script text, fonts, `eboot.bin`,
  and no tables or strings extracted from it.
- **Users supply their own legitimately owned copy.** The runtime and the tools read the game
  files (the extracted `CUSA47999` folder) at run time, read-only. Nothing is ever written into
  the game folder.
- **`samples/` holds data extracted from the copyrighted game files** (decoded pictures, audio,
  text and script dumps, tables from `eboot.bin`) **for local research only. It must never be
  shared**: not committed, pushed, uploaded, attached or posted anywhere. The same applies to
  anything the tools write from game data, and to `scratch/`. Both folders are excluded in
  `.gitignore`.
- `ref/` (local, git-ignored) holds third-party projects (knife, mangetsu, deepLuna) used as read-only references under
  their own licences. They are not part of this project and are not redistributed with it. The files
  translated from knife carry its MPL-2.0 header and are listed in `docs/THIRD_PARTY.md`; facts taken
  from the others are cited in a comment, no code of theirs is copied.
- TSUKIHIME is the property of its rights holders. This is an unofficial, independent project and
  is not affiliated with or endorsed by TYPE-MOON, Aniplex, HuneX or Sony (the disclaimer at the top).

---

## How to run

`Tsukihime.exe` is the game: it reads your own copy of the PS4 game (the extracted `CUSA47999` folder, never modified) and
plays it with sound, movies, menus and saves. It starts on its own (a double-click, or a desktop shortcut the launcher makes) once the game folder is known: the first
start without one explains what it needs, in your language, and asks for the folder. Build and start it (.NET 10 SDK; any graphics with OpenGL 3.3, laptop integrated graphics included):

```
tools\setup\ffmpeg_dlls.ps1                                # once: copies the FFmpeg 9 LGPL DLLs from the installed winget package BtbN.FFmpeg.LGPL.Shared.9.0 into third_party/ffmpeg/bin
                                                           # (if the package is missing it prints the winget install command and stops; the build stops with TSUKI001 without the DLLs)
dotnet build src/Tsuki.slnx -c Release
src/Tsuki.App/bin/Release/net10.0/Tsukihime.exe            # or: dotnet run --project src/Tsuki.App -c Release
```

- **Game folder:** `--game <dir>`, else the `TSUKI_GAME_DIR` environment variable, else the folder remembered from the last run, else a folder dialog
  asks for it (the choice is remembered in `%APPDATA%\Tsuki\game_dir.txt`).
- **Saves and settings:** one file, `%APPDATA%\Tsuki\save.dat` (140 slots with thumbnails, the system data and the config; the previous
  generation is kept as `save.dat.bak`). The folder keeps its `Tsuki` name although the programs are called Tsukihime, so saves made with earlier builds keep working. `--save-dir <dir>` uses another folder. Language: `--lang ja|en|zc|zt` for a new profile, then the config screen.
  The PC port's own choices (the button prompts) are in a small text file beside it, `%APPDATA%\Tsuki\settings.ini`, not in the PS4 save data.
- **Button prompts:** the guide line at the bottom of the menus and the Controls tab follow the device you used last: key caps (Enter, Esc, arrows, W A S D, the
  mouse wheel) for the keyboard and the mouse, the game's own PlayStation icons for a PlayStation pad, drawn Xbox glyphs (A, B, X, Y, LB, RT, ...) for any
  other pad. Config > Controls > Button Prompts pins one style (Auto, Keyboard, PlayStation, Xbox); `--prompts auto|keyboard|playstation|xbox` does the same for one run.
- **Window and icon:** the title bar shows the game's own name in the game's language, read from your copy's `sce_sys/param.sfo` (月姫 -A piece of blue glass moon- in Japanese, TSUKIHIME ... in English, 月姬 ... in Chinese) and changing the moment the language changes in Config; the icon of both programs is the port's own crescent (`assets/icon`, drawn by `tools/setup/make_icon.py`, original art); **while the game folder is valid, the game window, the launcher window, the taskbar and the game's shortcuts show Arcueid's face instead** (package K23): a close-up cut at run time from your own copy's sprite `ARU_A_00_00_N00` (her largest sprite, the neutral smile; the square is anchored on her red eyes), rendered at 16, 24, 32, 48, 64, 128 and 256 px with a rounded-square mask and a thin moon-blue rim (a tighter crop up to 32 px so the face stays readable) into `%APPDATA%\Tsuki\icons\tsukihime.ico`, built on the first start and again when the game folder changes (`tsukihime.src` beside it says which folder made it). Her art stays in your files: nothing of it is in the repository or in the programs, and the bare `.exe` files keep the crescent (unless you make a **personal build** for your own machine, "Personal build" below). 1920 x 1080 canvas, letterboxed into any window size, vsync. Windowed mode is a real window with a title bar, centred on the monitor and never bigger than 90 % of its work area (a size that does not fit is scaled down, 16:9 kept); the default is 1600 x 900 (or the largest 16:9 size that fits), and a size you drag the window to is remembered. Fullscreen is borderless fullscreen on the monitor the window is on. **Config > System > Display Mode (Windowed / Fullscreen) and Window Size (1280x720, 1600x900, 1920x1080, 2560x1440: the ones that fit the monitor)** change both at once; Alt+Enter or F11 toggle the mode too, and the row shows what they did. The mode is remembered in `save.dat`, the size in `settings.ini`. `--fullscreen` or `--windowed` decides the window mode of one run and is remembered for the next; started with neither, the game uses the remembered one. The launcher passes one of the two on every PLAY, so its Fullscreen switch always wins and always shows what the game will use.
- **settings.ini:** `%APPDATA%\Tsuki\settings.ini` is `key=value` lines, read once at start (`#` starts a comment, unknown keys and bad values are ignored). The launcher writes it; you can edit it by hand.

  | Key | Values | Effect |
  |---|---|---|
  | `prompts` | `auto`, `keyboard`, `playstation`, `xbox` | button prompts (the config screen writes this key too) |
  | `fullscreen` | `on`, `off` | the launcher's pending choice for the next PLAY (the switch shows it until then, else the remembered mode of `save.dat`). The game does not read it by itself: PLAY passes `--fullscreen` / `--windowed`, the game stores that in the config block of `save.dat` (where Alt+Enter and F11 keep it) and removes the line |
  | `language` | `ja`, `en`, `zc`, `zt` | the game language of the next start. Applied **once** to the config block, then removed; the config screen changes it later. `--lang` on the command line wins |
  | `window` | `1600x900` (640x360 to 16384x16384) | the size of the window when it is not fullscreen (default 1600x900). The game clamps it to 90 % of the monitor's work area (so `2560x1440` on a 2560 x 1440 monitor opens as 2224 x 1251, not as a window that covers the screen); Config > System > Window Size and a resize by hand write the key back. Kept in the file |
  | `monitor` | `x,y` | the display to open on, named by its top-left corner in virtual-desktop pixels (`0,0`, `1920,0`, `-2560,0`); the window is centred there, also before it goes fullscreen. A corner that matches no display is ignored. Kept in the file |
  | `vsync` | `on`, `off` | presents wait for the monitor (default `on`); `off` runs on the game's own 60 Hz timer. Kept in the file |
  | `mute_in_background` | `on`, `off` | **Config > Sound > Mute in Background** (default `off`): every sound fades out over half a second when the game window loses the focus and back in when it returns. Kept in the file |
  | `copy_text` | `on`, `off` | **Config > System > Copy Text to Clipboard** (default `off`): each new message's plain text (markup and ruby removed, as the backlog shows it) is put on the clipboard once when it starts displaying, a choice's lines one per line, for text hookers and dictionary tools (Yomitan, Textractor). Kept in the file |
  | `text_hooker` | `on`, `off` | **Config > System > Text Hooker Output** (default `on`, package K23): each new line (the same plain text) is passed once through the Windows GDI text calls that LunaHook and Textractor hook by default (`ExtTextOutW`, `TextOutW`, `GetTextExtentPoint32W`) on an invisible memory device context, so a hooker attached to `Tsukihime.exe` sees the text although the game draws its glyphs with OpenGL. Kept in the file |
  | `text_websocket` | `on`, `off` | **Config > System > Text WebSocket** (default `off`, package K23): a WebSocket server on `127.0.0.1` only (`ws://127.0.0.1:6677`) sends each new line as one text frame to every connected page (texthooker-ui and the like, with Yomitan); a page that connects late gets the current line first. Kept in the file |
  | `text_websocket_port` | `1`..`65535` | the WebSocket's port (default `6677`, the one texthooker-ui connects to); there is no config row for it, edit the file. Kept in the file |
  | `screenshot_notice` | `on`, `off` | **Config > System > Screenshot Notice** (default `on`, package K24): the short "Screenshot saved" notice in the bottom left corner after F12; `off` saves the picture silently. Kept in the file |
- **PC extras (package K21):** **F12** saves the frame on the screen, at the window's own size, as `%USERPROFILE%\Pictures\Tsukihime\Tsukihime_<date>_<time>.png` and shows a small "Screenshot saved" notice for a second and a half in the bottom left corner (never in the picture; **Config > System > Screenshot Notice** turns it off; package K24). **Print Screen, Win+Print Screen, Alt+Print Screen and Win+Shift+S are Windows' own screenshot keys and do nothing in the game** (K21 had Print Screen take the game's screenshot too, so the game's notice ended up in the Windows picture); while the Windows key is held no key reaches the game at all, so a Windows screenshot never presses PRESS ANY KEY or moves a menu cursor. The window's **close button and Alt+F4 ask the game's own quit question** first (a second close while it is up quits at once; the quit writes the system data through a temp file and never a new slot); **Config > System > Text Window Opacity** (0..100 % in 5 % steps, default 100 % = the PS4 look) scales the message window's dark background only, never the text (it is the PS4's own window-opacity byte, config +0x10, which the PS4 screen never showed; Recommended Settings puts it back). `--screenshot-dir <dir>` moves the screenshots for one run.
- **Text hookers (package K23):** see "Text hookers and dictionaries" below: the GDI hook point (on by default), the WebSocket feed and the clipboard all carry the same plain text.
- **Launcher:** see "Launcher" below.

| Input | Action |
|---|---|
| Enter, Space, Z, left click | advance the text; decide in menus |
| Esc, right click, M | system menu (save, load, config, flowchart, title); back in menus; **on the title: asks "Are you sure you want to quit without saving?"** (the game's own quit question; Yes ends the game). The Yes / No buttons of every dialog read はい / いいえ in Japanese (the PS4's Japanese sheet says Yes / No in English; the Chinese sheets have 是 / 否) |
| X | back in menus; hide or show the message window |
| H | hide or show the message window (in the story) |
| Arrow keys (Up in the story) | move the cursor; Up opens the mode panel (previous scene, rewind, auto, skip, next scene, quick save / load, voice) |
| W, A, S, D | the same as the arrow keys **in the menus only** (the title, the system menu, the lists, the config screen, the gallery); in the story they do nothing but A, which still toggles auto mode. The guide line's key caps show them beside the arrows |
| Mouse wheel up, Page Up, I | backlog (voices replay, jump back) |
| Mouse wheel, Page Up / Page Down | scroll lists and pages in menus (a page key is five wheel steps) |
| Mouse: scroll bar | drag the thumb, or click the track above / below it for a page (it repeats while held); the thumb lights under the pointer |
| Mouse: config sliders | click a step dot or the line, or drag the knob (it follows the pointer off the row) |
| Mouse: guide line | click Back, Confirm, Delete Data (save list) or the shoulder pair (left half L1, right half R1) at the bottom of a menu |
| Mouse: flowchart, CG viewer | drag the chart to pan it, click a node; click the viewer for the next picture (left quarter: previous), drag to pan, wheel to zoom |
| Mouse: story | pointer at the top edge opens the mode panel, at the bottom edge hides the message window; wheel button toggles skip; drag from the middle right / left to fast-forward / rewind |
| Story prompts | the chapter-start "Do you want to save your progress?" (and the yes / no prompt of the QA menu) opens with no button selected: arrows or the pointer select No (left) or Yes (right), Enter or a click answers; Esc and the cancel button do nothing there. Yes opens the save screen, and the story goes on when you leave it |
| Delete | delete the slot under the cursor in the save list (the pad's TRIANGLE); M copies it (SQUARE) |
| Alt+Enter, F11 | fullscreen on / off (the same as Config > System > Display Mode; the row follows the key and the key follows the row) |
| F12 | screenshot: a PNG at the window's size into `Pictures\Tsukihime`, then a small "Screenshot saved" notice in the bottom left corner (Config > System > Screenshot Notice turns the notice off). Print Screen and its combinations stay Windows' own and do nothing in the game |
| Close button, Alt+F4 | the game's quit question ("Are you sure you want to quit without saving?"); Yes ends the game, a second close while it is up ends it at once |
| Ctrl (held) | fast-forward; Tab toggles skip; A toggles auto mode |
| Backspace (held) | rewind page by page |
| F5, F9 | quick save, quick load |
| N, B, V | next scene, previous scene (both ask first), replay the voice |
| Q, E | previous / next tab or page in menus |
| Gamepad | south = decide, east = cancel, west = system menu, north = backlog, shoulders = auto / skip, left trigger = rewind, right trigger = fast-forward, start = quick save |
| Title menu | CONTINUE (with a save), NEW GAME, LOAD GAME, CONFIG, GALLERY and **EXIT** (the port's addition). The PS4 art has no EXIT, so its cell is composed at run time from the letters of the other items in your own files (E, I, T cut out of them; the X made from the Y of GALLERY), with their spacing, colours and glow, in the normal and the cursor look; a sheet the composer does not recognise falls back to the game's glyph font. Keys, W / S, the pointer and the pad reach it; Enter, a click or decide asks the quit question, Esc / right click / cancel ask it from anywhere on the title |

Checking a build without anyone at the keyboard: `Tsukihime.exe --hidden --mute --autoplay --fast --save-dir <scratch> --shot-dir <dir> --shot-on-choice`
opens a hidden window, plays no sound, clicks through the boot notice, the title and the story to the first choice and writes PNGs
(`--script`, `--shot-at`, `--exit-after`, `--choices`: see `Tsukihime.exe --help` and `docs/status/knife/K6.md`; the status files before K20 call the program `Tsuki.App.exe`, its name until then).
The script verbs `screenshot`, `close`, `blur` and `focus` drive the K21 extras (the screenshot key, the window's close request through the event pump, the focus); `--clipboard-file <file>`
makes Copy Text to Clipboard write there instead of the desktop clipboard. The log's `state:` line counts the text outputs (`copies N, hooked N, websocket port P (C clients) sent N`),
and `window icon: Arcueid from ...` / `face icon: built from ...` say what the icon did.

### Other dumps of the game (package K25)

The port was played end to end on one build (CUSA47999, Asia, app 01.01, one `eboot.bin` whose SHA-256 the code knows). Other players' dumps differ, and the
port handles them like this:

- **Folder layouts.** You may choose the extracted game folder itself, the folder above it (the game is found one or two levels down), a folder inside it
  (`image\`, `data\`, `sce_sys\`), or the file `eboot.bin`. A dump split into the **base game and the update in two folders** (`CUSA47999` and `CUSA47999-patch`
  as some PKG extractors write them, `CUSA47999` and `CUSA47999-UPDATE` as shadPS4 does, `CUSA47999-app` and `CUSA47999-patch`) is read as one game: the
  update's files take precedence over the base's and the update's `eboot.bin` and `param.sfo` are the ones that count, as on the console. Exception: when the game
  folder already holds a known 1.01 eboot (a merged dump with a stale update folder beside it, as a PKG extractor's install step leaves behind), the folder is used as it
  is and the launcher says the update folder is not needed. A `.pkg` chosen instead of a folder is refused with the words to extract it first (shadPS4, or a PKG
  extractor), an update folder alone with the words to put the base game beside it, a folder with several games with their names. The folder dialog of the game, the
  launcher's Game files page and `Tsuki.Cli verify` all say the same, in the game's language in the game.
- **Build identification.** `sce_sys/param.sfo` (title id, version, content id, the titles), the eboot's size and SHA-256 and the languages the data carries are read
  and shown: the launcher's Ready check and Game files page (rows *Build* and *Languages*), the game's log (`layout:`, `build:`, `languages:`, `tables:`) and the verify
  command. The known builds table (`KnownBuilds` in `src/Tsuki.Data/Verification/BuildIdentity.cs`) holds the verified eboot and the one other eboot checked
  against it (the 1.01 eboot of the 5.05 / 6.72 / 7.xx backport: 418 bytes differ, none in a table). Anything else is *another version of a known release* (title id
  CUSA47999) or an *unknown build*.
- **Other eboots.** The engine reads many tables from `eboot.bin`. For the verified eboot they are read at the documented addresses; for any other eboot every table is
  located by its content first (`EbootLocator`: record strides, pointer ranges, terminators, monotonic indices, the strings the engine itself uses, and the two table
  functions by their instruction pattern), validated, and only then read. On the verified eboot the locator finds every table at exactly the documented address (a test
  checks all 66). If a table cannot be found, the game refuses to start with a message that names the build and the missing tables and asks for a report with the
  log; nothing is guessed. The log file (`%APPDATA%\Tsuki\last_run.log` when started from the launcher) carries the per-table report.
- **Languages.** A language the dump does not carry (no script text, glyph pages or UI sheets for it) is not offered: the launcher hides its tile, the game's Config
  hides its button, and a profile set to such a language falls back to the start-up language or the first one the data has.

### Text hookers and dictionaries (Japanese learners)

The game draws its text with OpenGL from the PS4's pre-rendered glyph pages, so a text hooker sees nothing of it by itself. The port offers the same plain text (the
line as the backlog shows it: markup and ruby removed, a choice list one line per choice, the speaker's name above the line when the game shows a name plate) in
three ways; all three come from one producer, so they never disagree:

1. **Text Hooker Output (on by default)** for **LunaHook / LunaTranslator** and **Textractor**: start the game (`Tsukihime.exe`, or PLAY in the launcher), then in the
   hooker attach to the process `Tsukihime.exe` (LunaTranslator: *Select game* / the process list; Textractor: *Attach to game*). Advance one line in the game and
   pick the text thread that shows that line: it is named after one of the GDI functions the port calls for each line, `ExtTextOutW`, `TextOutW` or
   `GetTextExtentPoint32W` (all three carry the same text; `ExtTextOutW` is the usual pick). Each line goes through once, as one string, when it starts displaying,
   so the thread shows whole lines, never letter by letter. No H-code is needed. Nothing is drawn anywhere by these calls (a memory device context), and the row
   **Config > System > Text Hooker Output** turns them off. This was not tried with LunaHook or Textractor running (nobody at the keyboard during the package);
   the calls are the ones their default GDI hooks intercept (Textractor `texthook/engine/native/pchooks.cc`, `hookGDIFunctions`).
2. **Text WebSocket (off by default)** for browser texthooker pages with **Yomitan** (texthooker-ui, or anything that reads plain text frames): switch on **Config >
   System > Text WebSocket** (or `text_websocket=on` in `settings.ini`), open the page and let it connect to `ws://127.0.0.1:6677` (texthooker-ui's default; the port
   is `text_websocket_port=`). Each new line arrives as one text frame; a page that connects while a line is on screen gets that line at once. The server listens on
   the loopback address only, so no firewall prompt and nothing from outside the machine can connect; a toast says `Text WebSocket: ws://127.0.0.1:6677` when it
   starts, or why it could not (a port in use).
3. **Copy Text to Clipboard (off by default)** for clipboard watchers (Yomitan's clipboard monitor, Textractor's clipboard thread, the "Clipboard" input of translators).

### Launcher

`TsukihimeLauncher.exe` (`src/Tsuki.Launcher`, WPF, Windows only) is the front door of the port, in a "blue glass moon" look: a sidebar with seven pages and a footer with a status line, **Log** and a big **PLAY**. You do not have to go through it every time: **Desktop shortcut** (Play page, under the quick settings) puts `Tsukihime.lnk` on your desktop, which starts `Tsukihime.exe` directly with the folder it remembers.

- **Play:** the banner (the game's own title art and logo, decoded from your files when the game folder is valid, a drawn moon before that), the **Ready check** (game folder and version, game files, game program and FFmpeg DLLs, graphics adapter, saves; each row has a check mark or a warning and a *Fix* link that opens the right page) and quick settings (language, fullscreen, button prompts, the desktop shortcut).
- **Game files:** the folder (Browse..., Open), the build check of `Tsuki.Data` (eboot.bin by size and SHA-256, archives, counts), version, size. The folder is remembered in `%APPDATA%\Tsuki\game_dir.txt` and passed to the game as `--game`. Nothing in it is ever changed. **Shortcuts:** `Tsukihime.lnk` (the game) and `Tsukihime Launcher.lnk` on the desktop, and both in Start menu > Programs > Tsukihime; a shortcut that exists is replaced only after you confirm. The game's shortcut carries the Arcueid icon of `%APPDATA%\Tsuki\icons\tsukihime.ico` when the folder is valid (the launcher builds it, as the game does), else the executable's crescent.
- **Display:** fullscreen, monitor, window size (the presets 1280x720, 1600x900, 1920x1080, 2560x1440 that fit the chosen monitor's work area; the note under them says the size the game will open with), vsync. The game's own Config > System has the same Display Mode and Window Size rows, and the launcher shows what the game last chose. **Language & sound:** the game language (volumes are set in the game's own Config, because they live in the save data). **Controls:** button prompts, the key table, connected controllers.
- **Saves:** open the folder, **Back up now** (`save.dat` and `settings.ini` into `%APPDATA%\Tsuki\backups\<timestamp>\`), **Restore** (asks first, backs the current save up first), and **Import a PS4 save** (the 4 MiB `savedata000` image of a PS4 or shadPS4; it is read, never written, and replaces `save.dat` after a backup). **Help & about:** what the port is, the key list, the log, licence notices.

Everything the pages change goes into `settings.ini` (above); the game reads it when it starts. `PLAY` starts `Tsukihime.exe` from the launcher's own folder with `--game <folder> --log %APPDATA%\Tsuki\last_run.log --fullscreen` (or `--windowed`, as the Fullscreen switch says) (the **Log** button opens that file, else `crash.log`).

```
dotnet build src/Tsuki.slnx -c Release                    # the launcher's own output folder also gets Tsukihime.exe and its libraries, so TsukihimeLauncher.exe there works as it is
dotnet publish src/Tsuki.Launcher -c Release -r win-x64 --self-contained -o dist\Tsuki
                                                          # one folder: TsukihimeLauncher.exe next to Tsukihime.exe (the launcher's publish publishes the game into the same folder)
```

Checking the pages without showing a window: `TsukihimeLauncher.exe --render <dir> [--game <dir>] [--data <dir>] [--page play|gamefiles|display|language|controls|saves|help] [--height N]` draws them to PNG files and exits (`--data` replaces `%APPDATA%\Tsuki`, `--game` the remembered folder).

### Personal build: Arcueid on the .exe files themselves (package K28)

The window, the taskbar and the shortcuts take Arcueid's face from `%APPDATA%\Tsuki\icons\tsukihime.ico` at run time, but the icon Explorer shows for `Tsukihime.exe` and
`TsukihimeLauncher.exe` is the one compiled into them, the crescent. Her art is TYPE-MOON's and must never be in a build that leaves your machine, so a build with her face
on the files is an **opt-in personal build**, made with one MSBuild property that names your game folder:

```
dotnet build src/Tsuki.slnx -c Release -p:TsukiPersonalIcon=D:\Games\CUSA47999          # or a publish with the same property
```

Before the compile of each program, `src/Tsuki.PersonalIcon.targets` builds `Tsuki.Cli` and runs `Tsuki.Cli face-icon --game <folder> --out <obj>` (the same arithmetic
as the run-time icon, `src/Tsuki.Data/Icons/FaceIcon.cs`) into the project's `obj/` folder, which git ignores, and uses that `.ico` as the `ApplicationIcon`. A personal
build is marked three ways so that it cannot be released by mistake: the assembly attribute `AssemblyMetadata("TsukiPersonalBuild", "true")`, an informational version
ending in `-personal` (the game's log says `personal build: ... must not be shared`), and `PERSONAL_BUILD.txt` beside the programs (in the build output and in a publish
folder; a later build without the property into the same folder removes it again). **Release zips are always built without the property**, and `Tsuki.Cli release-check
<folder>` refuses a folder that holds any of the marks or a program whose icon group is not the crescent (`docs/PUBLISHING.md`); the build fails rather than falls back
to the crescent when the folder has no usable picture. Without the property nothing changes: the crescent stays and no mark is written.

---

## Repository layout

```
src/                            the C# port (.NET 10; src/Tsuki.slnx)
  Tsuki.Data/                   the game folder: archives, tables, scripts, pictures, audio banks (read-only); Icons/: the run-time Arcueid icon and the PE icon reader
  Tsuki.PersonalIcon.targets    the opt-in personal build (-p:TsukiPersonalIcon=<game folder>): the Arcueid icon on the .exe files, marked, never released
  Tsuki.Engine/                 the interpreter: script VM, stage, sound model, waits, history ring, saves
  Tsuki.Media/                  mixer, clip decoding, movie pipeline over FFmpeg
  Tsuki.Render/                 OpenGL 3.3 compositor, texture cache, colour grading
  Tsuki.Platform/               SDL3: window, GL context, input, audio device, dialogs
  Tsuki.Ui/                     the menus (title, system menu, save / load, config, backlog, gallery, flowchart)
  Tsuki.App/                    Tsukihime.exe: the game and its verification mode
  Tsuki.Cli/                    dev tools: verify, ui-shot, face-icon (the personal build's icon), release-check (refuses a personal build), ...
  Tsuki.Launcher/               TsukihimeLauncher.exe (WPF): settings.ini, ready check, saves, shortcuts, PLAY
  Tsuki.DevBridge/              dev-only helpers (not in Release)
tests/
  Tsuki.Tests.Synthetic/        hand-made data, no game needed
  Tsuki.Tests.Data/             against a real copy of the game (TSUKI_GAME_DIR)
  Tsuki.Tests.Render/           a real hidden SDL window and GL context
assets/icon/                    the port's icon (original art): tsukihime.ico, the 256 px PNG, the embedded 64 px RGBA; tools/setup/make_icon.py draws them
third_party/ffmpeg/             LOCAL ONLY: the FFmpeg DLLs, filled by tools/setup/ffmpeg_dlls.ps1
artifacts/                      LOCAL ONLY: build outputs (--artifacts-path)
docs/
  FORMATS.md                    format map and entry point
  RUNTIME_REQUIREMENTS.md       requirements for the native runtime
  RUNTIME_REQUIREMENTS.draft.md superseded engine draft (history only)
  formats/                      per-format documents (containers, images, text, script, audio,
                                fonts, textbox, archives_ui_movies, engine, ...)
tools/
  tsuki/                        Python reference readers for the game data
    mrg.py nam.py mzp.py mzx.py sniff.py       containers: HED/NAM/MRG, mrgd00, MZX0, LV
    image.py image_catalog.py                  picture formats, eboot image catalog
    catalog.py catalog_eboot.py                archive roles, lookups, UI tables, movies
    text.py text_refs.py                       script_text.mrg, markup, text references
    script.py script_tables.py script_emu.py   scripts, command tables, engine emulation
    font.py font_eboot.py textbox.py           fonts, glyph tables, text-box layout
    audio.py audio_hevag.py                    ATRAC9 RIFF banks, HE-VAG decoder
    vm_vars.py compose.py gcn.py misc_*.py     work in progress of the follow-up investigations
  eboot/                        eboot.bin helpers: SELF/ELF (selfelf), NIDs and imports,
                                disassembly index (disasm), symbols, strings, script-VM tables
                                (scriptvm), shaders, save image (savedata), UI text (uitext)
  tests/                        pytest suite (test_<area>.py, test_misc_<slug>.py)
  inventory.py                  per-archive inventory -> samples/inventory/
samples/                        LOCAL ONLY: extracted / decoded game data (never share)
scratch/                        LOCAL ONLY: working folders of the investigations
ref/                            LOCAL ONLY: third-party reference sources (read-only)
.venv/                          LOCAL ONLY: Python virtual environment
```

---

## Setup

Requirements:

- Python 3.11 (developed with 3.11.9 on Windows 11).
- FFmpeg and ffprobe on `PATH` (the audio tests decode ATRAC9 with FFmpeg; `TSUKI_FFMPEG` can point
  to a specific `ffmpeg` executable).
- An extracted copy of the game (the `CUSA47999` folder with `eboot.bin`, `image/`, `data/`,
  `sce_sys/`).

Create the virtual environment in the project root and install the packages:

```powershell
py -3.11 -m venv .venv
.venv\Scripts\python.exe -m pip install numpy pillow pytest
# optional, used by parts of the tools and tests:
.venv\Scripts\python.exe -m pip install numba capstone unicorn keystone-engine mypy pyflakes
```

- `numpy`, `pillow`, `pytest`: required.
- `numba`: JIT kernels for MZX0 (measured 868 MB/s against 36 MB/s in pure Python) and the HE-VAG
  decoder; set `TSUKI_NO_NUMBA=1` to use the pure Python paths.
- `capstone`, `unicorn`: eboot disassembly and the tests that run the game's own code under
  emulation to check our implementations; `keystone-engine`: one work-in-progress layer module.
- `mypy`, `pyflakes`: linting.

Tell the tools where the game is with `TSUKI_GAME_DIR` (the `CUSA47999` folder). There is no
built-in default: without the variable `tsuki.game_dir()` returns `None`, the readers stop with
"game folder not found (set TSUKI_GAME_DIR)" and the real-data tests skip. The game folder is only
ever opened read-only.

```powershell
$env:TSUKI_GAME_DIR = "D:\path\to\CUSA47999"
```

## Running the tools

The packages live in `tools/`, so put it on `PYTHONPATH`. PowerShell:

```powershell
$env:PYTHONPATH = "D:\TsukihimePort\tools"
.venv\Scripts\python.exe -m tsuki.script dump 3 --lang en            # readable dump of script 00_02
.venv\Scripts\python.exe -m tsuki.catalog image img2511 WHITE        # script picture id -> archive entries
.venv\Scripts\python.exe -m tsuki.image allpac1_2 ARU_L_81_00_N00.MZP --base ARU_L_81_00_N00_B.MZP -o out.png
.venv\Scripts\python.exe -m tsuki.textbox render en out.png 3 4 5    # lay out and draw messages 3..5
.venv\Scripts\python.exe -m tsuki.audio decode bgm 33 out.wav --loops 1
.venv\Scripts\python.exe -m eboot.uitext --out <dir>                 # export the UI string table
.venv\Scripts\python.exe tools\inventory.py --deep                   # rebuild samples/inventory/
```

Git Bash (the form used in the documents):

```bash
cd D:/TsukihimePort
PYTHONPATH=D:/TsukihimePort/tools .venv/Scripts/python.exe -m tsuki.script dump 3 --lang en
```

Each module has a CLI (`python -m tsuki.<module> --help`); the format documents list the
commands they used. Everything these commands write from game data is copyrighted material:
write it to `samples/` or `scratch/` (or another local folder) and keep it local.

## Running the tests

```bash
cd D:/TsukihimePort
PYTHONPATH=D:/TsukihimePort/tools .venv/Scripts/python.exe -m pytest tools/tests -q
```

PowerShell:

```powershell
$env:PYTHONPATH = "D:\TsukihimePort\tools"
.venv\Scripts\python.exe -m pytest tools\tests -q
```

- Real-data tests skip cleanly when the game folder is missing; with
  `TSUKI_GAME_DIR=/nonexistent` only the synthetic tests run.
- The full suite takes one to two minutes with the game present. One test (the real-save test in
  `test_eboot.py`) skips until a PS4 save file (`savedata000`) is available.
- Lint: `.venv/Scripts/python.exe -m mypy --ignore-missing-imports tools/tsuki tools/eboot` and
  `.venv/Scripts/python.exe -m pyflakes tools`.

## Conventions

- Addresses in the documents are eboot.bin virtual addresses of the analysed build
  (`docs/RUNTIME_REQUIREMENTS.md` section 3.3): text VA = file offset - 0x2280; RELRO VA 0x400000 =
  file 0x32C210; data VA = file offset - 0x365FA0 + 0x600000.
- Claims carry confidence labels: `[E]` read from the engine code, `[V]` verified on the retail
  data (counts given), `[H]` hypothesis.
- Archives are tens of gigabytes: code seeks or memory-maps, and never reads a whole archive into
  memory.
