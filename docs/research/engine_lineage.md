# Engine lineage: which games share the TSUKIHIME remake engine, and do they help a PC port?

Research synthesis, 2026-10-25. Question: *Which other Type-Moon (or related) games run on the
same engine as TSUKIHIME -A piece of blue glass moon- (PS4/Switch 2021), and would any of them
help build a native PC port of it?*

Inputs: 12 verified candidate records, about 30 unverified or ruled-out entries, and 5 searcher
reports from the research workflow. All inputs were present and none looked truncated. I also
cross-checked them against our own PS4 reverse-engineering (`docs/formats/*.md`) and read three
KiriKiri Z source files for this write-up (section 3.3).

---

## 1. Short answer

- **Engine.** The remake runs on **HuneX's in-house console visual-novel engine**. Our own PS4
  eboot calls itself "Hunex Consumer Library" and uses `nexl_*` allocator tags
  (`docs/formats/engine.md` 1.1). TYPE-MOON did not write it, and it is unrelated to fuzz's
  AGES mkII engine (Fate/stay night REMASTERED). HuneX has used the same data formats since its
  PS2/PSP-era ports: HED/NAM/MRG, `mrgd00` MZP, MZX0, and `_XXXX(...)` statement scripts.
- **Closest sibling.** *Witch on the Holy Night* (Mahoutsukai no Yoru), 2022 console remaster.
  The Switch build is verified as the same engine. The PS4 build has not been checked.
- **Next closest.** HuneX's PS Vita ports *Fate/stay night [Réalta Nua]* (2012) and
  *Fate/hollow ataraxia* (2014), followed by about a dozen non-TYPE-MOON HuneX ports reaching
  back to PS2/PSP.
- **Only Windows build in the family.** *Witch on the Holy Night* on Steam (2023). It keeps the
  console **image** pipeline (MZP/MZX0/HEP tiles, PS2 alpha) but replaces the archive layer
  (HFA), the script VM (`.chs` bytecode), text (`.ctd`) and audio (`.hw` Ogg).
- **For our port, none of these games is worth buying now.** We have already decoded every data
  format. The authoritative record of the remaining *behaviour* is the Tsukihime PS4 eboot we
  already have, including its 67 embedded shaders and both script-command tables.
- **The most useful outside reference is not a HuneX game but KiriKiri Z.** The remake was
  finished first as a KiriKiri Z PC build and then ported. Our eboot's 25 non-normal blend modes
  match KiriKiri Z's blend-operation list one to one. That includes the two "5" variants that
  `engine.md` lists as unexplained: KiriKiri defines them as Photoshop 5.x-compatible modes. The
  remake's rule transition also has the same parameters as KiriKiri's `universal` transition:
  a rule image plus a "vague" softness width.
- **Community tools for the sibling games** (PS-HuneX_Tools, MahoyoHDRepack, hunex-tools,
  VitaScenariosTool) are useful as free cross-checks. Our own docs found real bugs in mangetsu
  and deepLuna (HED offsets above 128 MiB, MZP section sizes, the MZX clear-counter reset), so
  for the PS4 data these tools are *less* accurate than what we already have.

---

## 2. How to read this

| label | meaning here |
|---|---|
| **same_engine** | Shares the console engine's archive, compression and script formats at byte level. This does not prove identical code. |
| **related_lineage** | Same porter (HuneX) and format family, but with documented differences or an older or newer revision. |
| **different** | Another engine, even when the company or the IP is the same. |
| **unknown** | Not checked, or the evidence is too thin. |

Confidence levels: **high** = several independent primary sources agree. **medium** = one good
source, or several that depend on each other. **low** = inference or secondhand reports.

The verdicts are the verifiers' verdicts. Where the searchers disagreed, the verifiers'
judgement was kept. Where our own reverse-engineering contradicts a verifier's argument, I say
so (section 4.3) but keep the label.

"Useful for the port" is judged against what is still open: engine **behaviour** (text-box
layout, transitions and shaders, menus hard-coded in the executable, game flow). Data formats
are done.

---

## 3. Where the engine comes from

### 3.1 Findings and confidence

| finding | confidence | basis |
|---|---|---|
| HuneX built the PS4/Switch runtime the remake ships on | **high** | Our eboot: "Hunex Consumer Library" string at VA 0x2DA510 and the `nexl_Graphics`, `nexl_ShaderMgr`, ... tags (`engine.md` 1.1). ja.wikipedia's console staff list credits 開発 (development) to ヒューネックス (HuneX). HuneX lists the game on its own PS4 works page. en.wikipedia names HuneX as co-developer. |
| Delightworks produced the port; it did not develop it | high | ja.wikipedia: 移植版プロデュース (port producer) and 制作協力 (production cooperation) for Delightworks. |
| The game was authored first as a **KiriKiri 2/Z PC build** by TYPE-MOON and then ported by HuneX | **high** | Famitsu (2021-09-11): the PC version's main part was finished by end of 2020, then reproduced on Switch/PS4, with long work to make the consoles behave identically. ja.wikipedia main staff: プログラム (programming) Kiyobee; 吉里吉里2/Z 提供 (KiriKiri2/Z provided by) W.Dee and じん, who are the KiriKiri 2 and KiriKiri Z authors. |
| **Kiyobee is not the HuneX engine's programmer** | high (that the attribution is wrong) | Kiyobee is credited in the TYPE-MOON production list next to the KiriKiri credit, which points to the PC master build. HuneX is credited separately for the console port. No individual HuneX programmer is named anywhere we found. |
| Internal project name | high (string) / low (reading) | Our eboot's original file name is `F:/Projects/DEVmoonLang/svn_prog/trunk/pgm/moonML/ORBIS/Release/devXXX.elf`, where ORBIS is the PS4 SDK codename. Reading "moonML" as "moon, multi-language" is my guess. |
| The console engine descends from HuneX's PS2/PSP-era "MRG/MZX" engine | **high** for the formats, **medium** for the code | The `mrgd00` header and 8-byte section records are byte-identical to the 2014-2016 specs. The HED8 offset encoding (`sector_lo \| (sector_hi & 0xF000) << 4`) that we read from the eboot (`containers.md` 2.1, VA 0xE5CE8) is the same as in the Vita-era tools. The same holds for the inverted MZX0 variant, the `LV` voice wrapper, and the `_ZM`/`_MSAD`/`_SELR` dialect. No executable has been compared with ours, so shared *code* is inferred. |
| The PSP/PS2 traits in our data (480x272 placement units, 640x448 pointer space, GS TEX0 in HEP headers, CSM1 palettes, 0x80-opaque alpha) are inherited from that lineage | medium | PSP 480x272 example images and RGBATim2/CSM1 palettes appear in the 2016 MZP spec. HEP tiles appear in HuneX Vita titles and in Mahoyo Steam, where I decoded the documented header constants as a GS TEX0 register. That decoding is mine. |
| Direct TYPE-MOON predecessors: Vita *Réalta Nua* (2012) and Vita *hollow ataraxia* (2014) | high | Same `allscr.mrg` layout (name table, `scr_adr` label tables in the `name<pad>id,offset\r\n` form pointing at `_ZZ` markers, then scripts at section k+3), same XOR-inverted MZX, same macros. |
| The PS2 *Réalta Nua* (2007) is **not** in this line, despite also being a HuneX port | high | It uses MF/UFFA LZSS archives in DATA.BIN/DATA2 and KAG-like `*page`/`@command` scripts, a separate HuneX PS2 packaging also seen in *Lamune*. |
| Earliest use of MZX0 | low | A 2006 2ch thread reportedly decoded MZX0 from the PS2 *SNOW* and *CLANNAD* ports, but the log returned 403. A TCRF page about CLANNAD PS2 returned AI-directed injected text and was ignored. |
| Mahoyo Steam's PC runtime borrows BGI/Buriko script conventions | low | Only searcher reports: the `.chs` header "HunexCompiledScriptVer1.00" resembles BGI's "BurikoCompiledScriptVer1.00", and `.cbg` uses "CompressedBG_MT". Not verified. |

### 3.2 Timeline of the format family (only entries with some evidence)

| year | title (platform) | what is attested |
|---|---|---|
| ~2004-06 | SNOW, CLANNAD (PS2) | MZX0 and ALLPAC.MRG reported secondhand (low) |
| 2006 / 2009 | Ayakashibito (PS2 / PSP) | allpac.hed/nam/mrg, mrgd00, XOR-inverted MZX0, `_ZM`/`_MSAD`/`_SELR`/`_LVSV`, inline CP932 text (PSP only shown) |
| 2008 | Kyuuketsu Kitan Moon Ties (PS2) | named in the MZP spec, on HuneX's PS2 list (not verified) |
| 2009 | Like Life every hour (PSP) | named in the MZP spec |
| 2011-12 | Suzukaze no Melt (PS3 / PSP) | named in the MZP spec |
| 2012 | Fate/stay night [Réalta Nua] (Vita) | allscr.mrg with label tables, voice/voice2 |
| 2014 | Aiyoku no Eustia, Fate/hollow ataraxia, DRAMAtical Murder re:code (Vita) | the same, plus RGBATim2 palettes and LV voices; HEP (bitmap type 0x0C) inferred for re:code |
| 2016 | Meiji Tokyo Renka Full Moon, Otome Riron Bon Voyage (Vita) | mrgd00 + bitmap type 0x0C; per-title NAM variants |
| 2021 | **TSUKIHIME remake (PS4/Switch)** | split allpac0..4, `script_text.mrg` with `$NNNNNN` references, HEP + GS TEX0, ATRAC9, NXCX/NXGX on Switch |
| 2022 | Witch on the Holy Night (Switch; PS4 unchecked) | same as Tsukihime Switch |
| 2023 | Witch on the Holy Night (Steam) | same image layer; new HFA/.chs/.ctd/.hw layers |
| 2023 | Hana Awase New Moon (Switch) | hed/nam/mrg with 0x40-byte NAM records |

### 3.3 The KiriKiri connection (new in this synthesis)

The remake was authored on KiriKiri Z, and the console versions were tuned to behave like the
PC build. So HuneX's runtime has to reproduce KiriKiri behaviour. Our eboot shows this directly.
`DisplaySurface_SelectShader` (VA 0xF0E70) selects from 25 non-normal blend modes
(`engine.md` 4.3). KiriKiri Z's `tTVPBlendOperationMode` (`visual/LayerIntf.h`) lists exactly
the same set:

| our blend byte | PS4 shader | KiriKiri Z mode |
|---:|---|---|
| 0, 3 | normal (basic/transition/sepia) | `omAlpha` / `omPsNormal` |
| 1 | add | `omAdditive` |
| 2 | addalpha | `omAddAlpha` |
| 4 | darken | `omDarken` |
| 5 | dodge | `omDodge` |
| 6 | lighten | `omLighten` |
| 7 | multiplicative | `omMultiplicative` |
| 8 | opaque | `omOpaque` (= `ltCoverRect`) |
| 9 | screen | `omScreen` |
| 10 | sub | `omSubtractive` |
| 11 | ps_add | `omPsAdditive` |
| 12 | ps_burn | `omPsColorBurn` |
| 13 | ps_darken | `omPsDarken` |
| 14 | ps_diff | `omPsDifference` |
| 15 | **ps_diff5** | **`omPsDifference5`** |
| 16 | ps_dodge | `omPsColorDodge` |
| 17 | **ps_dodge5** | **`omPsColorDodge5`** |
| 18 | ps_exclusion | `omPsExclusion` |
| 19 | ps_hardlight | `omPsHardLight` |
| 20 | ps_lighten | `omPsLighten` |
| 21 | ps_multi | `omPsMultiplicative` |
| 22 | ps_overlay | `omPsOverlay` |
| 23 | ps_screen | `omPsScreen` |
| 24 | ps_softlight | `omPsSoftLight` |
| 25 | ps_sub | `omPsSubtractive` |

Our numbering is alphabetical by shader name, so only the *set* of modes matches, not
KiriKiri's numeric values. The match covers 25 of 25 modes, including two that are unique to
KiriKiri. I treat this as **medium-high** evidence that HuneX re-implemented KiriKiri's layer
compositing.

What KiriKiri Z's source says. I read these files through a summarising fetch tool, so check
the files directly before relying on details:

- **The "5" variants** (`visual/gl/blend_functor_c.h`). `ps_color_dodge5_blend_func` and
  `ps_diff5_blend_func` first scale the source by the layer opacity, then return the blend
  result **without** the final alpha mix that the non-5 versions apply. The source comment says
  they are compatible with Photoshop 5.x and earlier. This answers the "PS_Diff5/PS_Dodge5
  meaning" item in `engine.md` section 10, item 3, as a hypothesis to confirm against the GCN
  code. It matters because `_STBR` mode 17 (ps_dodge5) is the most common blend value in the
  scripts (3,473 uses, `engine.md` 4.3).
- **The rule transition** (`visual/TransIntf.cpp`). KiriKiri's `universal` transition takes
  `time`, `rule` (an 8-bit greyscale image) and `vague` (default 64). The phase runs from 0 to
  255 + vague, and vague sets the width of the soft band. The blend tables come from
  `TVPInitUnivTransBlendTable*` in tvpgl. This fits our `FADS(type, ms, rule [, x])` with type 2
  = rule wipe (`script.md`), and the transition shader's threshold/vague field at surface
  +0x5C4 (`engine.md` 4.4). Hypothesis: FADS's optional 4th argument is the vague width [H].
- **Cross-fade and scroll** transitions are defined in the same file and are worth comparing
  with FADS type 0 and with the transition kinds 5/7/9/10 that `engine.md` 4.4 says are special-cased.

Check KiriKiri Z's licence before copying any code. Reading it for formulas is not a problem.

### 3.4 Same names or same company, but a different engine

- **F&C "MRG" and elf "HED" archives**: unrelated formats that share the file extensions. The
  MZP spec warns about the F&C name collision, and GARbro has no HuneX `mrgd00` handler.
- **HuneX PS2 MF/UFFA line**: PS2 *Réalta Nua*, *Lamune*, and probably the PSP *Arcana
  Famiglia*.
- **HuneX / dramatic create Unity engine** (`hunex.UNAS.ADV`; HPAC `.hpb/.hph`, HESL
  `.heslnk/.hese`, `.hdlg` with HLZS): *Nijigasaki Tokimeki Roadmap*, *Hoshizora Tetsudou*, and
  reportedly the Meiji Tokyo Renka PC/Switch ports.
- **fuzz AGES mkII**: Fate/stay night REMASTERED (2024) and Fate/hollow ataraxia REMASTERED
  (2025). We had already ruled this out ("smash" paths, FPD packs, OpenGL).
- **KiriKiri 2 / KAG**: PC FSN (2004), FHA (2005), Réalta Nua PC (2011) and Mahoyo PC (2012).
  **NScripter**: Tsukihime (2000), Kagetsu Tohya and the related discs.

---

## 4. Ranked candidates

### 4.1 Overview (ranked by strength of technical link)

| # | game | verdict | conf. | layers shared with Tsukihime | HuneX-engine Windows build? | usefulness for our port |
|---:|---|---|---|---|---|---|
| 0 | **TSUKIHIME remake** (PS4/Switch 2021), the reference | same_engine | high | all | no (Windows "TBA" rests on a weak source) | it *is* the target; the PS4 eboot is the behaviour spec |
| 1 | **Witch on the Holy Night**, 2022 console (Switch; PS4 unverified) | same_engine | medium | HED/NAM/MRG, allscr.mrg, inverted MZX0, script_text.mrg with `$NNNNNN`, NXCX/NXGX, core statement set | no (that is the Steam build, row 6) | **medium**: second script corpus, extra command semantics |
| 2 | **Fate/stay night [Réalta Nua]** (Vita 2012) | same_engine (older revision) | high | mrgd00, inverted MZX0, allscr layout incl. label tables, MZP image header and 7-bit alpha, macro vocabulary | no | **low-medium**: opcode-to-KAG map, label-table round trip |
| 3 | **DRAMAtical Murder re:code** (Vita 2014; Switch 2025) | same_engine | medium | HED/NAM/MRG, mrgd00, MZX0, HEP tiles (inferred) | **announced**: Steam, winter 2026, Windows 11, ¥3,500, porter not named | **low now**; a lead if the Steam build is HuneX's |
| 4 | **Fate/hollow ataraxia** (Vita 2014) | related_lineage | high | the same as #2, plus LV voices, RGBATim2 | no | **low-medium**: best opcode-to-KAG table (VitaScenariosTool) |
| 5 | **Aiyoku no Eustia -Angel's blessing-** (Vita 2014) | related_lineage | high | mrgd00, HED 8/4, NAM32 / MRG.NAM, inverted MZX0, CSM1 palettes, `_ZM`/`_MSAD`/`SELR` | no | low: readable Python cross-checks |
| 6 | **Witch on the Holy Night**, Steam (2023, app 2052410) | related_lineage (not separately verified) | medium | image layer only: mrgd00 MZP, MZX0, HEP + GS TEX0, 7-bit alpha | **yes, the only one** (DirectX 11 per a searcher) | **medium at best**: PC rendering reference, *if* it reuses the console renderer (unknown) |
| 7 | **Meiji Tokyo Renka Full Moon** (Vita 2016); PSP 2013 original | related_lineage | medium | mrgd00, MZX0, bitmap type 0x0C (HEP inferred) | no (PC/Switch ports reportedly Unity) | low |
| 8 | **Otome Riron to Sono Shuuhen -Bon Voyage-** (Vita 2016) | related_lineage | medium | archive family, but newline NAM, longer `_ZM` hex, QLIE `ABMP` images | no (PC versions are QLIE / Artemis) | low: shows per-title variants |
| 9 | **Hana Awase New Moon** (Switch 2023) | related_lineage | medium | hed/nam/mrg, mrgd00; NAM records are 0x40 bytes, not 0x20 | unknown (MangaGamer announced Windows/Steam; shipped engine unverified) | low; a lead if a HuneX Windows build exists |
| 10 | **Ayakashibito** (PS2 2006 / PSP 2009) | related_lineage | medium | mrgd00, inverted MZX0, HED8, CSM1 palettes, text opcode names | no | low: origin of the tools, nothing new |
| 11 | **Like Life every hour** (PSP 2009) | related_lineage | medium | mrgd00 / MZX0 (one spec mention) | no | low |
| 12 | **Suzukaze no Melt** (PS3 2011 / PSP 2012) | related_lineage | medium | mrgd00 / MZX (one spec mention, single source) | no | low |

Prices are given only where a source states one: Mahoyo Steam US$19.99 (VNDB), DMMd re:code
Steam ¥3,500 (Nitro CHiRAL).

### 4.2 Notes per candidate

**0. TSUKIHIME remake (reference).**
- There is no official Windows release. The 2024 multilingual edition is PS4/Switch only.
- en.wikipedia's "Windows TBA" cites a TYPE-MOON Ace source that the page itself tags as
  unreliable.
- An internal PC master exists (the KiriKiri Z build). Any future Windows release may be that
  KiriKiri build rather than HuneX's engine.
- Refuted along the way:
  - The Switch archive list (`allpachd`, `w_allpac`, ...) is not corroborated by any source.
  - PS-HuneX_Tools was not written "for" the Vita Fate ports; it is a fork of
    psp-ayakashibito_tools.
  - Reading the `WOH_SE*` sound names in mangetsu's doc as a sound library shared with
    *Witch on the Holy Night* is speculation.

**1. Witch on the Holy Night, 2022 console.**
- The strongest technical match. MahoyoHDRepack (C#) documents the Switch RomFS:
  - HED/MRG/NAM sets `allpacml`, `allpachdml`, `jallpac`, `jallpachd`, `allui`, `parts`;
  - an `allscr.mrg` with the same section layout as ours;
  - `script_text.mrg` with per-language offset/text pairs, in the order JP, EN, ZH, ZH2, then
    an empty fifth pair of U+3000 characters;
  - MZX0 with the 0xFFFF inverted ring;
  - the commands `_RTM_`, `_IF__`, `_ZY`, `_SCH2`, `_END_`, `_JUMP`, `_WKST`, `_ZZ` and `_ZM`
    with `$NNNNNN` references.
- hunex-tools decodes both games' Switch archives with one code path. The Sumisora Chinese patch
  used a modified mangetsu, which is a Tsukihime tool.
- Caveats:
  - `_VPLY`, `_STCH`, `_SESP`, `_STCL` and `_SEPL` are **not** attested for Mahoyo.
  - The script_text layout was not byte-verified for Mahoyo.
  - **The PS4 build is unchecked**: allpacN, HEP, ATRAC9 and HE-VAG are expected but
    unconfirmed, and VNDB's PS4 entry has no engine field.
  - MahoyoHDRepack's notes say the multilingual Tsukihime build changed emoji and ruby handling.
    Even Tsukihime builds differ in their text renderer, so "same engine" means the same build
    family, not identical binaries.
- Programming credits on the console version: Kiyobee and AZ-UME.

**2. Fate/stay night [Réalta Nua], Vita.**
- The match goes down to individual bytes:
  - the label-table records (21-byte name, 3-hex id, comma, 5-hex offset, CRLF) match our
    `allscr.mrg` section 1;
  - scripts sit at section N+3;
  - offsets point at the `_ZZ` markers that `_JUMP` targets;
  - the MZP font page header and 7-bit alpha match.
- Older than Tsukihime in several ways: inline CP932 text, no `script_text.mrg`, different font
  archive names (`gotf`/`gotn`, ...), 8-byte voice NAM records. HEP tiles and the allpac split
  are unconfirmed.
- PC *Réalta Nua* (2011) is KiriKiri. Hintay's VitaScenariosTool maps Vita macros to KAG tags,
  for example MPLY to `@play`, MVOL to `@fadebgm`, VPLY to `@say`, CFAD to `@fadein`/`@bg`.
  Lining Vita macro calls up against the PC `.ks` scripts of the same story is a working Rosetta
  stone for argument meanings.

**3. DRAMAtical Murder re:code.**
- HuneX's own works lists credit the Vita (2014) and Switch (2025) versions. Evidence comes from
  PS-HuneX_Tools issue #6: mrgd00 MZPs that fail on bitmap type 0x0C.
- The HEP description in that thread names no game, so HEP in re:code is inferred.
- Its value is the **announced Steam version** (winter 2026, Windows 11, JP/EN/zh-Hans,
  ¥3,500). If HuneX builds it, it would be a second Windows build of this family. Until it ships
  and a depot file list shows `.hed/.nam/.mrg` or `HUNEXGGEFA10`, treat it as a lead.

**4. Fate/hollow ataraxia, Vita.**
- Same porter and same pattern as the remake: authored in KiriKiri, ported by HuneX.
- Hintay's 2016 commits name it as the test title for allpac.nam indexing, RGBATim2 palettes
  and the `LV` voice signature.
- VitaScenariosTool's `macroslist.py` maps FHA opcodes to the original KiriKiri macros (WTKY to
  `@l`, VPLY to `@say`, BTXO to text on/off, KIMG to image, WKST to variable increment, QADD
  quiz). The verifier's main reason for "related_lineage" rather than "same_engine" is wrong;
  see 4.3.

**5. Aiyoku no Eustia.**
- EustiaVitaTool and PS-HuneX_Tools-CN are clean Python implementations of the shared layer.
- Older payloads: no HEP tiles (type 0x03 "PEH" is unsupported), inline text, a single allpac.
- The two tools disagree on the voice NAM record size (8 vs 16 bytes).

**6. Witch on the Holy Night, Steam.**
- The only shipped Windows build that contains HuneX's console asset pipeline:
  - Kept from the console: mrgd00 MZP containers with MZX0-compressed HEP tiles. The header
    constants 0x25300000 / 6 decode as GS TEX0 = PSMT8, 512x256, TCC=1 (my decoding).
  - New on PC: HFA archives (`HUNEXGGEFA10`, 0x80-byte records); `.chs` scripts
    ("HunexCompiledScriptVer1.00", a numeric VM with PushDword/Jmp/JmpIf/Return); `.ctd` text
    (LenZu); `.ccit` fonts; `.hw` Ogg; `.cbg` ("CompressedBG_MT").
  - A possible difference: Steam HEP tile palettes are reported unswizzled, with a CSM1-style
    swap only in font atlases. Ours are CSM1-ordered.
  - Developers on Steam: HuneX, Lasengle and TYPE-MOON.
- **Open question for us:** does its DirectX 11 renderer carry the same `nexl_Shader*` set
  (ps_dodge5, transition 2DTex, ...)? If yes, its DXBC shaders are a readable twin of our PSSL
  blobs. If the PC renderer was rewritten, it tells us little.
- An unread lead may be the most comparable prior art: `david419kr/mahoyo-steam-android-port`,
  a native reimplementation of this runtime (runtime/vm folders, OpenGL ES 3).

**7-12.**
- Each of these confirms that the container and compression layer is a long-lived HuneX house
  format.
- Each also shows per-title variation: 0x40-byte NAM records (Hana Awase), newline-separated NAM
  and QLIE `ABMP` images (Otome Riron), and older image types (Eustia, Ayakashibito).
- Lesson for our loaders: do not hard-code what only Tsukihime does.
- None has a HuneX-engine PC build. Their PC originals are other studios' engines:
  - Like Life: Hooksoft, not GIGA.
  - Suzukaze no Melt: Whirlpool, not WhiteSoft.
  - Otome Riron: QLIE (PC original) and Artemis (Steam).
  - Hana Awase: Flash/AIR.
  - Ayakashibito: propeller.

### 4.3 Where our own reverse-engineering changes the picture

1. **The HED "generation change" is a reading artefact.**
   - The FHA verifier downgraded FHA to related_lineage mainly because the Vita-era HED
     (`ofs_low | (ofs_high & 0xF000) << 4`) "differs" from mangetsu's plain u32 sector offset.
     The Mahoyo verifier made the same point.
   - Our PS4 analysis read the eboot's own HED reader (VA 0xE5CE8) and MZP section reader
     (VA 0xE6DA0). Both use the split encoding, and mangetsu's plain u32 is wrong above
     0x10000 sectors (`containers.md` 2.1).
   - So for the offset field, the Vita tools and the 2021 engine **agree**.
   - The fourth field may still really differ on Switch: MahoyoHDRepack reads it as an
     uncompressed-sector count for NX-wrapped entries, while on PS4 it is the low 16 bits of the
     exact size. That was not compared.
   - In practice, FSN RN Vita (same_engine) and FHA Vita (related_lineage) are the same
     generation with the same evidence. Treat them as equals: an older revision of the same
     engine.
2. **DMMd re:code (same_engine) and Meiji Tokyo Renka Full Moon (related_lineage)** rest on the
   same GitHub issue: mrgd00 plus an unknown type 0x0C. The different labels are noise.
3. **MZX "XOR 0xFF after decompression" and the "inverted MZX" variant are the same thing.**
   Starting the ring and the last word at 0xFFFF and XOR-ing literals gives the same output as
   normal decoding followed by XOR 0xFF over the whole output (`script.md` 1). The Vita, Switch
   and PS4 descriptions therefore agree.
4. **`_WKST` as assignment** (MahoyoHDRepack) matches our [E] finding in `script.md`, which
   corrects mangetsu's "some sort of wait". That is independent corroboration.

### 4.4 Plausible but not verified

- **Kyuuketsu Kitan Moon Ties** (PS2): named in the MZP spec and on HuneX's PS2 list.
- **CLANNAD** (PS2): ALLPAC.MRG, voice.mrg with MZX0 and AHX audio, from a 2006 2ch log
  (secondhand). The TCRF page fetch returned prompt-injection text, so it is unusable.
- **SNOW** (PS2): MZX0 secondhand.
- **Mashiro-iro Symphony** (PSP): encrypted `allpac.cpk`.
- **Steam Prison** (Vita): someone tried to unpack its `allscr.mrg`.
- **Tokei Jikake no Ley Line**: `.mzp` to `.hep` extraction reported.
- **Tobidase! Trouble Hanafuda Douchuuki** (PSP, a TYPE-MOON spin-off): unchecked.
- The 2016-2024 HuneX / dramatic create PS4, Switch and Vita ports listed beside Tsukihime on
  HuneX's works pages (Fata Morgana, Sakura Moyu, Amazing Grace, Hello Lady!, Steam Prison
  Switch and others). Any of them could be on this engine or on the Unity engine.
- HuneX's self-published Steam titles (Steam Prison -Beyond the Steam-, EPHEMERAL, Nie no Machi
  Iromusubi, Arcana Famiglia Rinato): unchecked for HFA or Unity HPAC.
- The **Mahoyo smartphone version** (2026-11-20; VNDB engine "HuneX"; Kiyobee and AZ-UME
  credited) and **Tsukihime -The other side of red garden-**: unknown.

### 4.5 Ruled out (different engines)

Fate/stay night REMASTERED and Fate/hollow ataraxia REMASTERED (fuzz AGES mkII); PS2 Réalta
Nua and Lamune (HuneX MF/UFFA); HuneX's Unity UNAS titles; PC FSN, FHA, Réalta Nua and Mahoyo
2012 (KiriKiri); Tsukihime 2000 and its discs (NScripter); Melty Blood, Fate/Extra and 428
(other developers' engines); KID PSP (BIP), Prototype Vita (DAT/PSB/PAK) and MAGES. (MPK/CPK).

---

## 5. Usefulness for the port, by remaining work area

| work area | what we already have | best outside reference | realistic value |
|---|---|---|---|
| **Blend modes** | 67 PSSL blobs, 38 shader classes, selector logic (`engine.md` 4.2-4.3); formulas still [H], "5" variants unknown | **KiriKiri Z** `visual/gl/blend_functor_c.h` (1:1 mode set, "5" variants defined) | **high** and free |
| **Transitions / rule masks** | transition shaders, threshold/vague field, RULE_xx masks, FADS types (`engine.md` 4.4, `script.md`) | **KiriKiri Z** `visual/TransIntf.cpp` + tvpgl `TVPInitUnivTransBlendTable*` (universal = rule + vague; crossfade; scroll) | **high** and free |
| **Exact shader code** | GCN binaries; full disassembly is open item 3 | shadPS4, which we already run, translates these shaders to SPIR-V. If its shader dump gives SPIR-V, spirv-cross yields readable GLSL of *our own* shaders [H, check]. Mahoyo Steam's DXBC would only help if it shares the renderer | medium, free path first |
| **Text-box layout and text rendering** | `Text_Layout` (VA 0xC6C20), `textbox.md`, font archives | none good. Sibling renderers differ even between Tsukihime builds (ruby/emoji change). KiriKiri's KAG message layer describes the authored PC behaviour, but the console layout is HuneX's | low |
| **Menus hard-coded in the exe** (title, config, flowchart, gallery) | class names (cFlowchartScene, cGalleryGuide, ...), UI text table | none. These are Tsukihime-specific; Mahoyo's menus differ | **none** |
| **Script semantics and game flow** | 112-entry command table plus prefetcher, many [E]/[V-all] (`script.md`) | MahoyoHDRepack `format notes.txt` (`_IF__`, `_JUMP` id encoding, `_RTM_`); VitaScenariosTool `macroslist.py` (HuneX opcode to KAG tag) for commands still [H]/[D]; PC Réalta Nua `.ks` against the Vita scripts; Mahoyo Switch's 249 scripts as a second test corpus | **low-medium**, mostly corroboration |
| **Data formats** | complete, with bugs in mangetsu/deepLuna found and fixed | PS-HuneX_Tools(-CN), hunex-tools, MahoyoHDRepack as regression cross-checks | low (already done) |
| **PC platform design** (save, input, config, DX/GL) | `engine.md` 2-3, 7-8 | Mahoyo Steam shows how HuneX and Lasengle mapped one of these games to Windows | low; inspiration only |

---

## 6. Recommendation

1. **Buy nothing for now.** No sibling's data or code is needed to finish the port. The PS4
   eboot is the behaviour spec, and its open items (blend formulas, transition softness,
   per-command arguments) are better answered from its own code plus KiriKiri Z's source.
2. **Next steps, all free:**
   - Read KiriKiri Z's `blend_functor_c.h`, `TransIntf.cpp` and the tvpgl univ-trans table code.
     Write each formula as a hypothesis in `engine.md` 4.3/4.4, then confirm against the GCN
     disassembly of the 24 blend shaders and 3 transition shaders. Start with `ps_dodge5`, the
     most-used mode.
   - Test whether FADS's optional 4th argument is the vague width.
   - Try shadPS4's shader dump to get SPIR-V, then GLSL, of our 67 shaders.
   - Use VitaScenariosTool's opcode-to-KAG map and MahoyoHDRepack's notes only for commands
     still marked [H]/[D] in `script.md`.
3. **The one purchase that could pay off:** *Witch on the Holy Night* on Steam (US$19.99 per
   VNDB). It is the only native Windows build of HuneX's pipeline. Buy it only if the steps
   above leave rendering questions open. Even then, first check (with the user's own copy)
   whether its executable contains the `nexl_Shader*` / `ps_dodge5` names. If it does not, the
   PC renderer was rewritten and the purchase gives little. Read
   `david419kr/mahoyo-steam-android-port` before deciding.
4. **Do not buy** the Vita, PSP or PS2 titles. Their formats are documented by free tools. Their
   executables are older revisions in ARM or MIPS code and would need a console dump. Their
   behaviour is not Tsukihime's.
5. **Watch:**
   - an official Tsukihime Windows release. If it is the KiriKiri Z master, its scripts and TJS
     code would be the definitive behaviour reference, and would also change whether a fan port
     is needed at all;
   - the DMMd re:code Steam version (winter 2026) and the MangaGamer Windows *Hana Awase*. Check
     public depot file listings for `.hed/.nam/.mrg` or `HUNEXGGEFA10` before spending money.
6. Legal: work only from copies the user owns. Do not redistribute game data or decompiled code.

---

## 7. Completeness note: what this research could not establish

- **No binary comparison.** Nobody compared any sibling executable with our eboot. "Same engine"
  rests on data formats and credits, not shared code.
- **Mahoyo PS4** file layout is unverified (allpacN, HEP, ATRAC9 and HE-VAG are expected).
- **Whether Mahoyo Steam reuses the console renderer** is unknown. This is the deciding question
  for its usefulness.
- **People.** No HuneX engine programmer is identified. HuneX's works pages give no roles.
  AZ-UME's identity and Lasengle's role on Mahoyo Steam are unknown.
- **Origin of MZX0.** The 2ch log (403), the CLANNAD PS2 evidence (secondhand; TCRF returned
  injected text) and the SNOW PS2 evidence are unverified.
- **Vita details.** Archive names and HEP use in Vita *Réalta Nua* are unverified. HEP in DMMd
  re:code and Meiji Tokyo Renka is inferred from an unattributed comment.
- **The KiriKiri match (3.3)** is my inference, from our eboot and three KiriKiri Z files read
  through a summarising fetch. It has not been checked against the GCN shader code, and KiriKiri
  2 (as opposed to Z) was not compared.
- **Single or secondhand sources.** The BGI/Buriko link of Mahoyo Steam's `.chs` and the claim
  that `.chs` is compiled from the Switch scripts are unverified. hunex-tools is one author's
  very recent work (October 2026), only partly cross-checked.
- **Unconfirmed engines.** Meiji Tokyo Renka PC/Switch as Unity rests on GalgameReverse tools
  only; the Steam page names no engine. The engines of a future Tsukihime Windows release, the
  DMMd re:code Steam version and the Hana Awase Windows release are unknown.
- **Not checked at all.** Dozens of HuneX PS4/Switch/Vita ports and HuneX's own Steam titles.
- **Blocked sources.** Beast's Lair, MobyGames, SteamDB, PCGamingWiki, GBAtemp, aluigi's BMS
  pages, Lost Media Wiki and Bilibili were blocked (403, Cloudflare or captcha). All verifiers
  ran out of web-search budget partway through.
- **Unread leads.** `david419kr/mahoyo-steam-android-port`, Jannabie HuneX-Scripting,
  `radiotb/HuneX_Tools`, `xmoezzz/DramaticCreateToolset`, the PS-HuneX_Tools `Backup/*.zip`
  files, and mangetsu's `hfa.cpp` game target.

## 8. Housekeeping

- The TCRF CLANNAD (PS2) page returned AI-directed injected instructions twice. They were
  ignored, and that page is not used as evidence here.

---

## 9. Sources

### Our own reverse-engineering (primary)
- `docs/formats/engine.md`: 1.1 (Hunex Consumer Library, nexl tags, ORBIS
  build path), 4.2-4.4 (shaders, blend modes, transitions), 10 (open questions)
- `docs/formats/containers.md`: 2.1 (HED8 split offset, eboot VA 0xE5CE8 /
  0xE6DA0; mangetsu bug)
- `docs/formats/script.md`: 1 (allscr layout, XOR after MZX), command table
  (WKST, FADS, STBR, MPLY, VPLY)

### KiriKiri Z source (read for this synthesis)
- https://raw.githubusercontent.com/krkrz/krkrz/master/visual/LayerIntf.h (`tTVPBlendOperationMode`)
- https://raw.githubusercontent.com/krkrz/krkrz/master/visual/gl/blend_functor_c.h (`ps_*5` functors)
- https://raw.githubusercontent.com/krkrz/krkrz/master/visual/TransIntf.cpp (universal/crossfade/scroll)

### Credits and release facts
- https://ja.wikipedia.org/wiki/月姫_-A_piece_of_blue_glass_moon-
- https://www.famitsu.com/news/202109/11232354.html
- https://en.wikipedia.org/wiki/Tsukihime
- https://news.denfaminicogamer.jp/news/2108262i
- https://pc.watch.impress.co.jp/docs/release/newscast/1578949.html
- https://typemoon.com/products/tsukihime/ ; http://typemoon.com/products/mahoyo/product/
- HuneX works lists: https://www.hunex.co.jp/archives/works/ps4 , /switch , /switch/3 ,
  /switch/4 , /psv_01/2 , /psv_02/3 , /psp_01 , /psp_03 , /ps3 , /ps2_02 , /ps2_02/2 ,
  /ps2_02/3 , /ps2_02/4 , /steam
- https://ja.wikipedia.org/wiki/HuneX ; https://ja.wikipedia.org/wiki/Fate/hollow_ataraxia ;
  https://ja.wikipedia.org/wiki/Fate/stay_night ; https://ja.wikipedia.org/wiki/魔法使いの夜
- VNDB: https://vndb.org/r85789 , r85790 , r112866 (Mahoyo Switch/PS4/Steam); r21752 , r36304
  (FSN RN Vita); r32161 (FHA Vita); r17678 (RN PC); r118913 (FSN REMASTERED); r25939 , r112220 ,
  r46889 (Otome Riron); r3817 , r640 (Like Life); r162278 (Mahoyo Android); r1514 (Mahoyo 2012)
- https://ogdb.eu/index.php?section=title&titleid=34118
- https://www.nitrochiral.com/news/2026/3304.php (DMMd re:code Steam)
- https://mangagamer.org/hanaawase ; https://www.famitsu.com/news/202303/20296260.html
- https://store.steampowered.com/app/2779890/_Full_Moon/ ; https://store.steampowered.com/app/2396980/

### Format tools and specs
- https://github.com/rschlaikjer/mangetsu (incl. `doc/script_commands.md`, `include/mg/data/hfa.hpp`,
  `include/mg/data/mrg.hpp`, `src/data/mzp.cpp`, `src/data/nam.cpp`)
- https://github.com/Hakanaou/deepLuna (incl. `luna/mzx.py`, `luna/mrg_parser.py`, `luna/translation_db.py`)
- https://github.com/Hintay/PS-HuneX_Tools (incl. `Specifications/hed_format.md`, `mzp_format.md`,
  `mzx_compression.md`, `tools/unpack_allsrc.py`, `tools/prep_tpl.py`, issues #5 and #6, commit history)
- https://github.com/mchubby/psp-ayakashibito_tools
- https://github.com/lzhhzl/PS-HuneX_Tools-CN (incl. `tools/mrg_tool.py`, `tools/lib/hep.py`,
  `tools/lib/mrgd.py`, `Documents/02-EXTR_mzx.md`, commits 597a696, 31fe8c7832, dacd7f2a99)
- https://github.com/Hintay/VitaScenariosTool (`macroslist.py`, `psvstool.py`)
- https://github.com/tkn1540654460-oss/fate-staynight-localization ;
  https://github.com/tkn1540654460-oss/Fate-hollow-ataraxia-PSV-Chinese
- https://github.com/SpriteLisen/EustiaVitaTool
- https://github.com/nike4613/MahoyoHDRepack (`MrgFileSystem.cs`, `MzxFile.cs`, `format notes.txt`,
  `tsukire en notes.txt`)
- https://github.com/aaronbamblett/hunex-tools (docs: switch-archives, switch-script-text,
  mzp-image-format, hfa-format, opcodes)
- https://github.com/loicfrance/mahoyo_tools (incl. issue #7)
- https://mahotsukaicn.github.io/2022/09/18/MainPost/
- https://root-none.pages.dev/localization_notes/Hunex_Engine/note_hunex_3.md
- https://github.com/Anonym271/PS-HuneX_Tools_HanaAwase ; https://github.com/Anonym271/hana-awase-tools
- https://www.zenhax.com/viewtopic.php@t=8640.html , t=8674 , t=13787
- https://raw.githubusercontent.com/morkt/GARbro/master/ArcFormats/Qlie/ImageABMP.cs
- Different-engine evidence: https://github.com/RikuKH3/mf_pack ; https://github.com/woo3113/FSN-RN-PS2-KR ;
  https://github.com/wlt233/hunex_script ; https://github.com/shiikwi/HunexRev ;
  https://github.com/YuriSizuku/GalgameReverse ; https://github.com/mhcoma/fateha_kr ;
  https://alyinghood.github.io/fsnrnue-multiplatform/

### Leads not read (see section 7)
- https://github.com/david419kr/mahoyo-steam-android-port
- https://github.com/Jannabie/HuneX-Tsukire (archived; HuneX-Scripting not found)
- http://mimizun.com/log/2ch/leaf/1141063964/ (403)
