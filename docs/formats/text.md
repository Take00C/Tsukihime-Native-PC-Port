# Text format: script_text.mrg, message markup, text references

Scope: the game's text in every language (`image/script_text.mrg`), how scripts
reference it, and every inline control/markup sequence. This is for
TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999, v01.01). The container layer
(mrgd00/MZP, MZX) is in `containers.md`. Script bytecode as a whole is in
`script.md`; this document covers only the text commands.

The Python reference is `tools/tsuki/text.py` (reader, tokenizer, style state machine,
`$` expansion) plus `tools/tsuki/text_refs.py` (scan of allscr.mrg text commands). You
should be able to write a C# reader from this document alone.

Confidence labels:

- **[V-all]**: verified on every line, entry or command in the retail data (counts given).
- **[E]**: read from the game code (eboot.bin text VA; file offset = VA + 0x2280). Disassembled with capstone.
- **[D]**: a data observation, e.g. how the translators use a code, with no engine proof.
- **[H]**: hypothesis.

All multi-byte integers in script_text.mrg are **big-endian** (unlike the rest of the
archives). Text is **UTF-8**. The engine processes text **byte by byte**.

Revision: repaired after independent review (section 11 lists what changed and why).

---

## 1. Overview

```
image/script_text.mrg  = mrgd00 container, 10 sections (12,601,040 bytes)
   section 2k   : offset table of language k   (BE u32)
   section 2k+1 : string blob of language k    (UTF-8, every string ends in CR LF)
   k = 0 ja, 1 en, 2 zc (Simplified Chinese), 3 zt (Traditional Chinese), 4 ko (blank placeholder)

image/allscr.mrg       = scripts; text commands such as _ZM01d01($000000) reference
                         line ids as $NNNNNN (6 decimal digits)
```

- Every language has **43,969 lines** and line id *i* is the same sentence in every
  language [V-all]. Ids are dense indexes, 0..43968.
- A displayed message is the argument of a script text command with each `$NNNNNN`
  replaced by that line of the current language. The result is then interpreted for
  markup: ruby `<base|ruby>`, newline `^`, `@` codes and `[icon]` names.
- Ruby markup exists only in non-English text. The engine **turns ruby parsing off when
  the layout language is English**, so `<`, `|` and `>` are ordinary glyphs there [E].
- A few lines are not shown through text commands at all: 11 opening lines are drawn as
  pre-rendered images (5.7), and the choice handler builds choice numbers itself (5.4).

---

## 2. Container layout

script_text.mrg is a standard mrgd00 container (`containers.md` section 4). Section
table [V-all]:

| sec | offset | size (bytes) | content |
|---:|---:|---:|---|
| 0 | 0x000058 | 175,888 (0x2AF10) | ja offset table |
| 1 | 0x02AF70 | 3,410,336 (0x3409A0) | ja strings |
| 2 | 0x36B918 | 175,888 | en offset table |
| 3 | 0x396830 | 2,724,070 (0x2990E6) | en strings |
| 4 | 0x62F918 | 175,888 | zc offset table |
| 5 | 0x65A830 | 2,638,229 (0x284195) | zc strings |
| 6 | 0x8DE9C8 | 175,888 | zt offset table |
| 7 | 0x9098E0 | 2,728,975 (0x29A40F) | zt strings |
| 8 | 0xBA3CF0 | 175,888 | ko offset table |
| 9 | 0xBCEC08 | 219,845 (0x35AC5) | ko strings (43,969 × `E3 80 80 0D 0A`) |

Sections are not compressed.

### 2.1 Offset table (section 2k)

| offset | size | type | meaning |
|---:|---:|---|---|
| 0 | 4·N | BE u32 [N] | `off[i]` = byte offset of string *i* in the blob, *i* = 0..N-1 (N = 43,969) |
| 4·N | 4 | BE u32 | blob size (= end of string N-1) |
| 4·N+4 | 4 | BE u32 | blob size again |
| 4·N+8 | 4·m | BE u32 [m] | 0xFFFFFFFF end mark(s): retail m = 1; mangetsu repacks write m = 3 |

Retail: 43,972 entries = 175,888 bytes in every language, with exactly one 0xFFFFFFFF, at
the last index [V-all]. The 8 bytes of 0xFF that follow in a hex dump are the mrgd00
section pad, not table entries. Offsets are strictly increasing in all five tables. Every
string is at least its CR LF, so there are no zero-length entries before the two guard
entries [V-all].

The count is N = (entries before the first 0xFFFFFFFF) - 2. A reader should accept any
number m ≥ 1 of trailing end marks, as long as only end marks follow the first one: the
engine reads only `off[id]` and `off[id+1]` (section 4), so it accepts both layouts.
`parse_offset_table(data)` does this; `parse_offset_table(data, strict=True)` demands m = 1,
and `ScriptText.validate()` reports m ≠ 1 as an issue.

### 2.2 String blob (section 2k+1)

String *i* is the bytes `blob[off[i] .. off[i+1])`, in UTF-8. Every string ends with
`0D 0A` (CR LF), and no string contains CR or LF anywhere else [V-all, 5 × 43,969]. All
five blobs decode as strict UTF-8. There is no NUL, no BOM and no character outside the
BMP (no U+E000..F8FF private-use glyphs either) [V-all]. The last string ends exactly at
the blob end.

### 2.3 Worked example (hex from the retail file)

```
00000000: 6d72 6764 3030 0a00 0000 0000 5600 10af   "mrgd00", 10 sections; s0 = (0,0,0x56,0xaf10)
00000010: 5500 1807 8306 a009 d706 c000 5600 10af   s1 = (0x55,0x718,0x683,0x09a0) -> 0x58+0x55*0x800+0x718 = 0x2af70
...
00000058: 0000 0000 0000 0041 0000 0055 0000 007e   ja off[0]=0, off[1]=0x41, off[2]=0x55, off[3]=0x7e
0002af58: 0034 0998 0034 09a0 0034 09a0 ffff ffff   off[43968]=0x340998, size, size, end mark
0002af68: ffff ffff ffff ffff                       mrgd00 pad (8 bytes)
0002af70: e380 80.. .... .... .... .... ...          ja string 0: U+3000, then 20 characters (63 bytes in all; the text itself is not quoted here)
0002afa0: ... e380 820d 0ae3 8080 .... ...           "。" CR LF | string 1: U+3000, then 5 characters (18 bytes)
00396830: .... .... .... .... .... .... .... ....    en string 0: 75 ASCII characters, no indent
00396870: .... .... .... .... ..20 200d 0a20 20..    "...  " CR LF | string 1: two spaces, then 13 characters
```

Id 3 (the line asked for; the game's wording is not reproduced in this repository, so the table gives
the shape of each line and a made-up line of the same shape):

| lang | line 3: shape | a made-up line of the same shape |
|---|---|---|
| en | two spaces, then a 68-character sentence ending in `.` (70 characters, 70 bytes) | `  The sample sentence of this document stands in for the real line here.` |
| ja | U+3000, then 25 characters with three ruby groups (26 characters, 60 bytes; `tokenize` gives Text, Ruby, Text, Ruby, Ruby, Text; the plain text is 13 characters) | `　ここは<見本\|みほん>の<文\|ぶん><章\|しょう>である。` |
| zc | U+3000, then 18 Simplified characters ending in `。` (19 characters, 57 bytes) | `　这是一个替代原文的示例句子，仅作说明。` |
| zt | U+3000, then 15 Traditional characters ending in `。` (16 characters, 48 bytes) | `　這是一個替代原文的示例句子。` |
| ko | `　` (U+3000 only) | |

It is the 4th message of script `00_02` (the opening scene). Ids 0..2 come before it (three short
sentences: en 75, 15 and 17 characters), and each of those three has a voice clip
(`_VPLY(B90_00_01_0000_1..3, ...)`).

---

## 3. Languages

| index k | suffix | language | sections | notes |
|---:|---|---|---|---|
| 0 | ja | Japanese | 0/1 | ruby used (3,476 groups) |
| 1 | en | English | 2/3 | no ruby; `@i`/`@r` italics |
| 2 | zc | Chinese, Simplified (system language 11) | 4/5 | ruby used (763) |
| 3 | zt | Chinese, Traditional (system language 10) | 6/7 | ruby used (1,092) |
| 4 | ko | Korean | 8/9 | placeholder: every line is U+3000; not selectable |

Engine evidence [E]:
- The text fetch at VA 0x75F80 / 0x76040 reads sections `2*lang` and `2*lang+1` of archive
  slot 0x20. That slot is opened at VA 0x788C0 with the string "script_text.mrg"; allscr.mrg goes into slot 0x1F.
- The language option is a u16 at VA 0x66C59C (struct 0x66C578 + 0x24). Its **shipped value is 0xFFFF**
  (data bytes `ff ff 01 00` at 0x66C59C). At load, VA 0x6FE94 replaces any negative value with the
  system-language mapping VA 0x407F0: `sceSystemServiceParamGetInt(LANG)` 0 -> 0, 11 -> 2, 10 -> 3, any other
  value -> 1, and 1 (English) if the call fails.
- Two readers use the word differently:
  - The layout / font code calls the getter VA 0x3ED90: stored value if it is 0..3 (unsigned < 4),
    otherwise the system-language mapping above.
  - `$` expansion (VA 0x76040) reads the dword at 0x66C59C directly, without the getter. Only its low
    16 bits matter (section index = (int16)(2 × value)). So a stored 4 (possible only with an edited
    save) would take text from the blank ko slot while the layout uses the system language.
  `tsuki.text.effective_languages(option_word, system_language)` returns both values.
- The options handler at VA 0x406B0 cycles the value through 0..3 (`v+1` if < 4 else 0; `v-1` wrapping to 3), and
  its store at VA 0x40747 is the only direct write. Index 4 can therefore never be selected in a normal game.
- The suffix table at VA 0x42C030 = `ja, en, zc, zt, ko` (used for `if_<lang>.png`, `ft_<lang>.mrg`, ...).
- VA 0x41A20 ("is the language English?") selects English word-wrapping. The layout function
  stores `0x3ED90()` in a local (rbp-0xC4) and skips ruby parsing when it equals 1.

Measured per language [V-all]:

| lang | blob bytes | lines | empty lines (CR LF only) | longest line (bytes incl. CR LF) | distinct line texts |
|---|---:|---:|---|---|---:|
| ja | 3,410,336 | 43,969 | 0 | 590 (id 35103) | 40,967 |
| en | 2,724,070 | 43,969 | 3 (ids 16947, 24958, 25401; ja `■`) | 469 (id 35103) | 40,692 |
| zc | 2,638,229 | 43,969 | 3 (same ids) | 494 (id 4645) | 39,887 |
| zt | 2,728,975 | 43,969 | 3 (same ids) | 569 (id 28963) | 40,468 |
| ko | 219,845 | 43,969 | 0 | 5 (every line) | 1 |

Alignment by id [V-all]:
- All tables have the same N.
- The correlation of plain-text line lengths at the same id is 0.902 for ja-en, 0.942 for ja-zc, 0.955 for ja-zt and 0.979 for zc-zt.
  The same correlation shifted by ±1 id is 0.07..0.10 for every pair.
- Of the 11,524 ja lines that open with 「 or 『, the line with the same id opens with a quote in
  98.9 % of en lines, 99.5 % of zc and 99.5 % of zt.
- The `@g`/`@b`/`@t`/`@k` code sets are identical per id in ja, zc and zt. en differs only at id 4577 (one redundant `@g`).

---

## 4. Reading a line (engine semantics)

```
line_bytes(lang, id, dst):                   # VA 0x75F80 (edi = id, edx = lang, rsi = dst)
    if id < 0: dst[0] = NUL; return 0        # js at 0x75F90
    tab  = section(2*lang); blob = section(2*lang+1)
    a = be32(tab, 4*id); b = be32(tab, 4*id + 4)
    n = b - a                                # signed 32-bit
    if n < 3: return 0                       # CR LF only, or a guard / end-mark entry; dst NOT written
    k = min(n, 0x301) - 2                    # strip CR LF; at most 0x2FF = 767 bytes
    copy blob[a .. a+k) to dst; dst[k] = NUL; return k
```

- id = N and id = N+1 read the guard entries and give "" (for N+1, `0xFFFFFFFF - size` is
  negative as an int32). Larger ids read past the table: the engine does not check
  bounds, and a port should reject them (`TextTable.engine_line` raises IndexError).
- The `n < 3` path leaves `dst` untouched; callers that need a string must not rely on it being
  NUL-terminated. The `$` expander (5.3) inlines this routine and simply appends nothing.
- The 767-byte cap never triggers. The longest line is 590 bytes [V-all].
- Python: `ScriptText().get_line(lang, id)` returns the string without CR LF.
  `TextTable.engine_line(id)` reproduces the routine above byte for byte.

---

## 5. How scripts reference text

### 5.1 Text commands

Scripts (allscr.mrg sections 3..551, inverted MZX, ASCII/UTF-8) are `;`-separated
`_NAME(args)` commands. Four commands carry a message [V-all over 549 scripts]:

| command | count | argument | refs |
|---|---:|---|---:|
| `_ZM<5 hex>(<msg>)` | 42,590 | message. `ZM` is a *marker* of kind `M`; the 5 hex chars are a 3-digit block id and 2-digit sub id (`script.md` 3.7) | 42,576 have `$` refs; 14 inline-only (debug/test scripts) |
| `_MSAD(<msg>)` | 678 | text appended to the current message (normally after a `...@n` message); 45 have an empty argument (5.8) | 617 refs |
| `_SELR(<n>,<msg>[,<locked>])` | 824 | choice *n*. Story choices use refs (206, n = 0..4); the other 618 are debug jump menus in TEST_SCRIPT / JUMP00..JUMPQA with inline labels (`_SELR(0,FlagOFF_scene_jump)`, `_SELR(9,next)`). 6 story choices have a third argument `1` (5.2) | 206 refs |
| `_ZD<5 hex>(<msg>)` | 17 | a *disabled* message: marker kind `D`. The marker dispatcher at VA 0x98479 acts only on kinds `Y` and `M` (`cmp r15d, 0x59` / `0x4D`), so a `_ZD` is **never displayed** [E] | 17 refs |

Total: **43,930 `$` references**, to 43,902 distinct ids, all < 43,969 (maximum 43,958) [V-all].
- 13 ids are referenced only by `_ZD` and are therefore never displayed: 7695, 7927, 8662, 8663, 8736, 8737,
  8749, 8761, 8912, 9036, 9308, 10483, 10484 [V-all, `text_refs.zd_only_ids`].
- 26 ids are referenced two or three times: alternative `_SELR` variants under `_IF__` (7 ids:
  42575, 42596, 42597, 42600, 42642, 42751, 42759), repeated passages in alternative branches (15 ids:
  13315-13317, 14114-14116, 14256, 19035-19040, 31427, 31428), and 4 ids that a `_ZD` and the following
  `_ZM` both reference (8710, 8800, 8910, 9598) [V-all for the lists; D for the reading].
- 67 ids are referenced by no script. `text_refs.classify_unreferenced` sorts them into five classes
  (per-id list with ja/en text: `samples/text/unreferenced_ids.tsv`):

| class | count | ids | evidence |
|---|---:|---|---|
| choice-number | 10 | 43959..43968 (`０．`..`９．`) | the choice handler builds `$0439NN` itself (5.4) [E, V] |
| image-text | 11 | 12-16, 59, 63, 68, 82, 87, 93 | shown as pre-rendered images in `00_02` (5.7) [V: every image rendered and read] |
| merged-fragment | 22 | 800, 2564, 2566, 4325, 4961, 8801, 13131, 14668, 15415, 19007, 22184, 22694, 22719, 23430, 25659, 25747, 26126, 28384, 32193, 37851, 41589, 41814 | the ja text of the id is repeated at the end of the previous line or the start of the next (e.g. ja 2563 ends in `、`, ja 2564 is `、`). Includes all 15 English `<->` placeholders (2564, 2566, 4325, 4961, 14668, 15415, 19007, 22184, 22694, 22719, 23430, 32193, 37851, 41589, 41814). 14 of the 22 sit where the script has an empty `_MSAD()` (5.8) [D] |
| blank | 4 | 16947, 24958, 25401, 43675 | ja `■` (en/zc/zt empty) or blank (43675) [D] |
| not-shown | 20 | 4101-4108, 4128-4135, 8711, 8911, 9599, 17637 | 4101-4108 and 4128-4135 are two identical copies of one 8-line Akiha dialogue; ja 8711 is an editor's note left in the data (19 characters in full-width parentheses, `（※…）`, saying a page was deleted and the text adjusted) while en 8711 is a real sentence; 8911, 9599, 17637 sit next to `_ZD`/`_ZM` pairs or plain gaps. Cut or duplicated passages [D] |

The earlier version of this document called 42 of these "cut or duplicated passages"; that was wrong
for the 11 image-text ids (they are shown) and imprecise for the rest.

### 5.2 Message argument syntax

Argument shapes, with `$N` standing for any 6-digit reference [V-all, 44,109 commands]:

| shape | count | | shape | count |
|---|---:|---|---|---:|
| `ZM($N)` | 40,734 | | `ZM(@x$N)` | 253 |
| `SELR(n,<label>)` (debug menus) | 618 | | `SELR(n,$N)` | 200 |
| `MSAD($N)` | 532 | | `ZM($N^@n)` | 170 |
| `ZM($N^$N)` | 510 | | `MSAD($N@n)` | 49 |
| `ZM($N@k@e)` | 425 | | `ZM(@c1$N)` | 15 |
| `ZM($N@n)` | 418 | | `SELR(n,$N,1)` | 6 |
| ... (full list in `samples/text/markup_catalogue.md`) | | | | |

Codes that occur inside message arguments: `@n` 678, `@k` 469, `@e` 469, `@x` 287,
`@c1` 20, `@c2` 6, `@c4` 6, `@r` 5, `@c3` 1, `@c5` 1, `@c7` 1. At most 2 references per
command (514 commands have 2, 42,902 have 1, 693 have 0).

**_ZM / _MSAD** (and the argument form of the never-displayed `_ZD`) [E: the copy loops at VA 0x92458..0x924B0 (`_ZM`, handler 0x92380),
0x92BC0..0x92C11 (`_MSAD`, handler 0x92B60, called from 0x9AECE); the same loop in 0x93370 and 0x7C6D0]:

```
_ZM:   pos = 0                                   # raw message buffer at VA 0x16537E0 (0x300 bytes)
_MSAD: pos = current length (word); if pos > 0x2FF: append nothing
copy argument bytes after '(' into buf[pos..]:
    stop at ')'            -> end mode 0
    stop at "@e"           -> end mode 2   (the "@e" and everything after it are dropped)
    stop at "@n"           -> end mode 1   (likewise)
    stop when pos reaches 0x300 (no NUL written then)
buf[pos] = NUL; store the length
expand(out = VA 0x16534E0, src = buf, src_len = 0x300, mode 1)    # 5.3: the WHOLE buffer
```

So `_MSAD` appends raw text and the whole message (the `_ZM` text plus all appended `_MSAD`
texts) is expanded again; the 0x2FC output cap of 5.3 applies to the combined message. NUL is not a
stop byte of the copy loop. `@e` and `@n` never reach the layout code. A literal `)` cannot
appear in an argument; the `@2` escape (section 6) exists for that.
- End mode 0 sets message flag 0x400 (for `_MSAD`, the mode on `)` is 2 or 0 depending on a
  condition from VA 0x56140, not traced).
- End mode 1 sets a "continue" byte, and the next `_MSAD` appends to the same message.
- End mode 2 does neither. In the data it always follows `@k` (469/469).
The exact runtime meaning of the modes is [H]: mode 0 = normal, mode 1 = no click wait / continue, mode 2 = end after an explicit `@k` wait.

**_SELR** does **not** use that loop [E, handler VA 0x9936D..0x9952A]:

```
n      = int_arg(cmd, 0)                         # VA 0x76690
if word[0x1750646] == 0: start a new menu        # call 0x7AF90(..., 0xD)
msg    = string_arg(cmd, 1)                      # VA 0x76EA0, see below
k      = word[0x1750646]                         # slot counter = choices added so far
slot   = 0x174FB70 + k * 0xFA                    # 250-byte slot
slot   = "$" + 6 decimal digits of (k < 9 ? 43960 + k : 43959) ; strcat(slot, msg)
word[0x1750630 + 2k] = n
byte[0x175064C + k]  = (int_arg(cmd, 2) == 1)    # the "locked" flag
word[0x1750646] = k + 1
```

The generic string getter VA 0x76EA0 skips `index` arguments (each scan stops at `,`, or
gives "" at `)` / `;`, max 500 bytes), skips 5 bytes if the argument starts with a backtick
(the `` `NNN: `` keyword prefix, `script.md` 3.2), then copies until `,` `)` or `;` (bit mask
0x0800120000000000; NUL is *not* a stop byte), then NUL-terminates. Consequences:
- A `_SELR` message ends at `,`; `@e`/`@n` have no special meaning in it.
- In `_SELR(1,$042600,1)` the `1` is argument 2, not message text. All 6 three-argument
  commands are story choices with argument 2 = `1`: `$042600` ×2 (03_00_ARC03_5B), `$042642`
  (07_00_ARC07_3_1), `$042751` (13_01_01_CIEL13_4), `$042759` ×2 (14_01_CIEL14_1C). Each one is the
  `_IF__`-guarded twin of the same choice without the flag (e.g. `_IF__(G003,ne,1,LV00100);_SELR(1,$042642,1)`
  ... `_IF__(G003,eq,1,LV00200);_SELR(1,$042642)`) [V-all].
- The menu cursor / pointer code (VA 0xA6360..0xA67E9) tests the flag byte and skips slots whose
  flag is non-zero [E, partial]. So argument 2 = 1 most likely shows the choice **locked**
  (visible, not selectable) [H]. `TextCommand.locked` reports it.

### 5.3 `$` expansion

```
expand(out, src, src_len, mode):             # VA 0x76040 (rdi, rsi, dx, ecx)
    if mode == 0: memcpy(out, src, src_len); return          # no expansion, no terminator
    pos = 0
    if src_len > 0:
      i = 0
      loop:
        c = src[i]
        if c == '$':
            id = Σ src[i+1+k] * 10^(5-k), k = 0..5 ; id -= 0x30 * 111111   # NO digit check
            i += 6
            if id >= 0:                          # js at 0x760EB
                n = line_bytes(lang_raw, id)     # section 4, inlined; lang_raw = word at 0x66C59C (3)
                copy min(n, 0x2FC - pos) bytes of it to out[pos..]; pos += that
        else:
            out[pos] = c; pos += 1
        if pos > 0x2FB: break                    # output is at most 0x2FC = 764 bytes
        if c == NUL: break                       # the NUL is copied first
        i += 1
        if i >= src_len: break
    *(u32 *)(out + pos) = 0                      # 4-byte zero after the output
```

Notes for a byte-exact port:
- The output can reach **0x2FC = 764** bytes (the comparison is `inc; cmp r15d, 0x2FB; jle`),
  inside a 0x300-byte buffer.
- `src_len` is 0x300 for text messages and 0xFA for choice slots (5.4).
- A `$` within 6 bytes of the end reads past the message (stale buffer bytes in the engine). Any
  6 bytes are accepted: `$12` + NUL NUL NUL NUL gives id 5,400,000 - 5,333,328 = 66,672 (out of range).
  `expand_refs` reads missing bytes as NUL.
- Ids beyond the table are not rejected by the engine (5.1: no retail reference is out of range).

Measured: the longest single expanded command message is 588 bytes in ja, 467 in en, 492 in zc
and 567 in zt. The longest *combined* message (`_ZM` + appended `_MSAD`s, 42,590 messages, up to 12
parts) is 669 bytes in ja, 547 in en, 555 in zc and 567 in zt, all below 764 [V-all]. The `$`
expansion happens before markup interpretation, so line text and argument text form one markup
string. Example: `_MSAD(@c5$009810@n)` gives `@c5` + line 9810.

Python: `expand_refs(arg, fetch, limit=0x2FC, *, src_len=None, expand=True)` implements the
routine above; `ScriptText.expand(arg, lang, src_len=None)` / `expand_bytes` use it with
`TextTable.engine_line` as `fetch`.

### 5.4 Choices and engine-built references

Each `_SELR` writes a 250-byte (0xFA) slot `"$0439NN" + msg` (5.2). The number id is chosen by the
**slot counter** k (the number of `_SELR` already executed for the current menu, word at VA
0x1750646), not by the `n` argument: k < 9 uses 43960 + k (`１．`..`９．`, en `1. `..`9. `),
k ≥ 9 uses 43959 (`０．`) [E: 0x993F7..0x9940A; V: the 10 lines]. n and k differ when an `_IF__`
skips an earlier `_SELR`. When the menu is drawn, the loop at VA 0x9B6B5..0x9B6E0 expands every
slot with `expand(buf, slot, 0xFA, 1)` and lays it out (unless the menu mode dword at VA
0x17506B0 is 2 or 3). So the choice number line is **prepended** to the choice text,
e.g. slot 1 of `_SELR(1,$042600)` expands to `2. ...No, this is the reality I've created.` (en).
Whether the number is visible on screen depends on the menu layout [H].
Python: `choice_number_id(k)`, `choice_slot_text(k, msg)`, `TextCommand.choice_slot(k)`.

### 5.5 Voice links (not part of the text)

Lines carry no voice id. A voiced line is preceded by a separate `_VPLY(<name>,<hex>[,<x>])`
command; `_VPL2` (16 commands) is an alias with the same handler (`script.md`, VA 0x968B2).
`<hex>` is the global voice.hed / voice.nam entry index: the name's group prefix equals
`voice.nam[int(hex,16)]` for 16,688 of the 16,688 two-argument `_VPLY` commands. The one
three-argument command is `_VPLY(A10_07_00_0147_1,01b40,0)` [V]. Example:
`_VPLY(D10_00_00_0000_0,03fb5);...;_ZM0181b($000134)`.

How many text commands follow each voice command before the next voice command (or the
script end) [V-all, `text_refs.voice_text_counts`]:
- `_VPLY` (16,689): never 0; exactly 1 for 10,641; 2 for 1,359; 3 for 1,485; 4 for 1,023; ... up to 103.
  So a voice clip starts with the next text command but is not bound to exactly one message.
- `_VPL2` (16): 0 for 13 (the 12 in `00_02`, which voice the image cards of 5.7, and the one in
  15_01B_CIEL15B_1), 1 for 2 (09_01_CIEL09_6B, QA_20), 13 for 1 (14_01_CIEL14_1C).
The audio and script documents own the details.

### 5.6 Speaker names, placeholders

- There is no speaker-name field and no name markup. Dialogue is marked only by the
  quote characters inside the line: ja 「」『』, en `"`, zc “”, zt 「」『』“” [D].
- The only substitution placeholder is the script-level `$NNNNNN`. `%` in zc/zt (8 lines)
  is a literal percent sign. No line in any language contains `$`.

### 5.7 Text shown as images (opening scene `00_02`)

The opening scene shows 12 lines as pre-rendered pictures instead of text-window messages.
Each picture is an image MZP in `allpac0` (decode with `tsuki.image`, `images.md`) holding the
line in four horizontal bands, top to bottom ja / en / zc / zt, white on transparent. The
script puts them on screen with `_STCH(layer, imgNNNN, ...)` or `_SCH2(...)`, animates them with
`_STMA`/`_SGMA`, and plays the voice with `_VPL2(<voice>, <hex>)` [V: each image rendered over
black and read]:

The ja band (1) and the en band (2) of each picture carry the same wording as the `script_text` line
of the id (the text itself is not reproduced here; the lengths are those of the `script_text` lines):

| image | line id | ja (band 1) | en (band 2) | voice |
|---|---:|---|---|---|
| IMG2458 | (2560, shown elsewhere) | 13 characters; the band omits the leading dashes | 19 characters | `_VPL2(B90_00_01_0000_0,03d00)` |
| IMG2629 | 12 | 15 characters, starts `もし、` ("what if") | 34 characters, starts `What if` | A12_00_01_0000_0 |
| IMG2630 | 13 | 16 characters, starts `もし、` | 45 characters, starts `What if` | A12_00_01_0000_1 |
| IMG2631 | 14 | 17 characters, starts `もし、` | 31 characters, starts `What if` | A12_00_01_0000_2 |
| IMG2632 | 15 | 17 characters, starts `もし、` | 32 characters, starts `What if` | A12_00_01_0000_3 |
| IMG2633 | 16 | 25 characters, a dash run `―――` inside | 41 characters, starts `And what if` | A12_00_01_0000_4 |
| IMG2634 | 59 | 10 characters, starts `―――` | 27 characters | B90_00_01_0011_0 |
| IMG2635 | 63 | 13 characters, starts `―――`, a U+3000 inside | 27 characters (the same en wording as 59) | B90_00_01_0013_0 |
| IMG2636 | 68 | 22 characters, starts `―――`, two U+3000 inside | 58 characters, `...` pauses | B90_00_01_0015_0 |
| IMG2459 | 82 | 27 characters, starts `―――`, no final `。` | 38 characters, `@i` italic | B90_00_01_0019_0 |
| IMG2460 | 87 | 17 characters, starts `―――`, no final `。` | 43 characters, `@i` italic | B90_00_01_0021_0 |
| IMG2461 | 93 | 28 characters, starts `―――`, a U+3000 inside, no final `。` | 49 characters, `@i` italic | B90_00_01_0023_0 |

- IMG2629..IMG2633 (layers 11..7) are set up together (`_STCH(7..11, img2633..img2629, ...)`) and
  revealed one by one: `_VPL2(A12_00_01_0000_k); _STMA(11-k, 22531+k)`.
- The script_text copies of these lines (ids 12-16, 59, 63, 68, 82, 87, 93) are never referenced;
  the image bands carry the same wording, show the base text without ruby (checked on a full-size
  crop of IMG2459), and the en lines that are `@i` italic in script_text (82, 87, 93) are drawn
  italic. IMG2458 has no line of its own: the same sentence is id 2560 (ja with a leading U+3000 and `―――`, 13 characters; en 19 characters), which a later scene shows normally (`_ZM02801($002560)` in 01_00_ARC01_10).
  IMG2459..2461 are 2040×5080 with the four bands spread vertically; the others are 510 or 1020 or
  1530 × 1016.
- How the engine picks the band for the current language is part of the image/sprite code
  (`images.md`, `script.md`), not of this document [H: the `_STCH` source rectangle].
- Samples: `samples/text/textimg_IMG2629_id12.png` (over black), `textimg_IMG2459_id82_quarter.png`.

### 5.8 Empty `_MSAD()`

45 `_MSAD` commands have an empty argument [V-all]. 44 of them follow a message that ended in
`@n` (the other follows `_ZMd4a1c($043674)` in QA_20), so they end the pending message without
adding text. In 17 of them the id right after the previous message's id is unreferenced
(14 merged fragments and 3 blank lines: 16947, 25401, 43675): the script's reference was
removed there, the fragment having been merged into a neighbouring line [D]. The other 28 sit
between two consecutive referenced ids.

---

## 6. Markup (inline control sequences)

Markup is interpreted by the layout function at VA 0xC6C20. It makes two passes over
the expanded message: pass 1 classifies line-break opportunities (0xC6D80..0xC7375);
pass 2 places glyphs and applies the codes (0xC76C0..0xC8DE1). The byte-consumption
rules below are those of pass 2 [E]. Everything is counted in **bytes** of UTF-8.
Anything else is a glyph. Glyphs are decoded as UTF-8 by the lead byte: `<0x80` 1 byte,
`110xxxxx` 2, `1110xxxx` 3, `11110xxx` 4 (what the engine does with a stray continuation
byte as a lead byte is not traced; it never happens in retail data).

### 6.1 Tokenization algorithm

Loop head at VA 0xC76C0: a byte ≥ 0x80 goes straight to the glyph path; then `@`, `^` and NUL are
tested; only then the ruby characters. So codes, `^` and icons work **inside** ruby groups too.

```
state_ruby = 0                                   # 0 none, 1 base, 2 reading
while c = s[i] != NUL:
  if c == '@':                                   # dispatch at 0xC77D0, jump table VA 0x2D32C0 (index s[i+1]-'1', 0x4A entries)
     k = s[i+1]
     case k in 'a','c','f','h','w': param = s[i+2] (ONE byte); i += 3
     case k == 's': i += 2; read at most 256 bytes: digits accumulate (v = 10v + d), '-' sets the
                    sign, other bytes are skipped; ',' or ')' or NUL ends the number and is
                    consumed (a NUL terminator is consumed too: the engine reads on past it);
                    without a terminator i ends 256 bytes later. scale = ±v / 64
     case k in 'x','y': up to 3 following digit bytes are the parameter (none allowed); @y first
                    stores 0, then the value if there are digits
     case k == '1': s[i+2] := '^' and set the literal flag (VA 0x178CF10); i += 2  # the '^' is drawn as a glyph
     case k == '2': s[i+2] := ')'; i += 2                                            # ')' is drawn as a glyph
     case k in 'b','g','i','k','l','o','p','r','t','u','z','T': i += 2
     default (any other byte, including NUL and bytes >= 0x80):                       # 0xC8625
                  if s[i+2] == '(' : scan s[i+3 .. i+3+254] (255 bytes): NUL -> i += 2;
                                     ')' at p -> i = p + 1; neither within 255 bytes -> i += 2
                  else i += 2            # '@' + NUL therefore skips the terminator (overrun)
  elif c == '^': newline, unless the literal flag is set (then: glyph '^', flag cleared)
  elif lang != en and c == '<': state_ruby = 1; measure the base (scan ahead)      # 0xC7717
  elif lang != en and c == '|' and state_ruby == 1: state_ruby = 2                 # reading glyphs follow
  elif lang != en and c == '|' and state_ruby == 2: (swallowed)
  elif lang != en and c == '>': state_ruby = 0                                     # swallowed even outside ruby
  elif c == '[' and s[i:] starts with one of the 39 icon names: icon, skip the name
  else: glyph (UTF-8 sequence)
```

- A new `<` while a group is open restarts the group (state 1). At NUL the state is dropped.
- Parameters are single **bytes**: `@c` followed by `é` takes the lead byte 0xC3 as its
  parameter and leaves the continuation byte 0xA9 behind.
- Overruns a port should not copy: `@` as the last byte, `@1`/`@2` as the last bytes (they overwrite
  the NUL), and `@s` ended by NUL all make the engine read past the end of the string. A port
  should stop at the end of the string.

Python: `tsuki.text.tokenize(s, lang)` works on the UTF-8 bytes exactly like this and returns
`Text`, `Ruby(base, ruby)`, `Newline`, `Icon(name, index)` and `Code(code, arg)` tokens. A `Ruby` also
carries `base_tokens` / `ruby_tokens` (everything inside each part, including codes) and `closed`.
Bytes split from a multi-byte character by a parameter are kept with `surrogateescape`.
`iter_tokens()` walks into ruby groups. With `script=True` it also returns `Ref(id)` and stops at
`@e`/`@n` (a convenience for `_ZM`/`_ZD`/`_MSAD` arguments; the engine itself only sees the
expanded message). The retail data has no markup inside ruby groups, no unclosed group and no
non-ASCII parameter [V-all], so the byte rules matter only for new or modded text.

### 6.2 Code table

Counts: number of occurrences in script_text lines (ja / en / zc / zt; ko has none) and in
script message arguments (S). Codes the engine knows but the data never uses are listed
too, because a port must accept them.

| code | params | effect | ja / en / zc / zt / S | conf. |
|---|---|---|---|---|
| `^` | - | forced line break | 4 / 175 / 6 / 5 / 724 (706 in messages with refs) | E, V |
| `<b\|r>` | - | ruby: glyphs of *b* on the baseline, *r* above them. **Not in English** (literal glyphs). If caller flag 0x2000 is set, the reading is skipped | 3,476 / - / 763 / 1,092 / 0 | E, V |
| `[name]` | - | inline icon glyph, see 6.4 | 136 / 132 / 135 / 136 / 0 | E |
| `@g` | - | text style 1 (glyph flag bit 2). Used only for the murder-impulse monologue: ids 4478..4620 in 13 runs | 127 / 128 / 127 / 127 / 0 | E (look: H) |
| `@b` | - | sets glyph flag bit 16 (byte VA 0x178BE2C); cleared by `@r`. Only seen as `@g@b` (ids 4527, 4529, 4531, 4533) | 4 / 4 / 4 / 4 / 0 | E (look: H) |
| `@i` | - | English layout: italic (style 0→3, g 1→4, z 2→5). Other languages: same as `@g` (style 1) | - / 1,288 / - / - / 0 | E |
| `@r` | - | reset: style 4→1, 5→2, else 0; also colour (default), `@b`, `@a`, the `@c6..9` value and the `@h`/`@w`/`@s` scales. Does **not** reset `@o` or the typewriter counter | - / 1,288 / 1 / 1 / 5 | E |
| `@z` | - | text style 2 (glyph flags 0x100000). The caller can also start a message in style 2 (layout flag bit 20, VA 0xC6C63) | 0 | E (look: H) |
| `@cN` | byte | N = byte - 0x30 as u8. N=0: default colour, clear ext. N=1..5: colour word `COLOR[N]` (u16 table VA 0x2D34C0 = F800, 345F, 1082, AD55, 001F). N=6..9: ext value N in glyph field +0x0A. N ≥ 10 (non-digit): ignored (0xC8CD0) | `@c5`: - / - / 1 / 1; S: `@c1` 20, `@c2` 6, `@c3` 1, `@c4` 6, `@c5` 1, `@c7` 1 | E (565 channel order: H) |
| `@t` | - | typewriter counter := 0. The next glyphs appear together with the start of the message: simultaneous speech (ids 23613, 36678) and side-by-side choices (9328), always as `^@t` | 3 / 3 / 3 / 3 / 0 | E + D |
| `@u` / `@l` | - | save / restore the typewriter counter | 0 | E |
| `@k` | - | ignored by the layout. Click wait; a message whose text ends in `@k` is kept on the page (backlog test VA 0x55D01) | 1 / 1 / 1 / 1 / 469 | E (layout), H (wait) |
| `@o` / `@p` | - | turn automatic line wrapping off / on (local rbp-0x88). en 12500 ends `.@r@o"` so the closing quote is not wrapped alone | - / 1 / - / - / 0 | E |
| `@x` | 0-3 digits | no digits: continue at the saved x position (global VA 0x178BE20, set via VA 0xC6BB0), breaking the line if it no longer fits. This is the "glued to the previous message" mark. With digits: absolute x | 0 / 0 / 0 / 0 / 287 (always without digits) | E (digit units: H) |
| `@y` | 0-3 digits | first stores 0 in the word at text-slot record +0x1E (rsi-0x52, rsi = record+0x70; VA 0xC89EB), then the value if digits follow. A bare `@y` clears the field | 0 | E (meaning: H) |
| `@aN` | byte | animation mode N = byte - 0x30 (global VA 0x178BE28). Non-zero: every glyph that is not a dash-ligature piece gets glyph flag bit 3 and random offsets/params (rand()); cleared by `@r`. Ignored when caller flag 0x40 is set | 0 | E (look: H) |
| `@hN` / `@wN` | byte | glyph height / width = base × `SCALE[N]`, with SCALE = 0.34375, 0.37240, 0.40104, 0.42969, 0.45833, 0.48698, 0.22917, 0.25781, 0.28646, 0.31510 (VA 0x2D3730). Ignored while global VA 0x177A200 ≠ 0 | 0 | E |
| `@s<int>` | number | width = height = int/64 (same gating); parsing in 6.1 | 0 | E |
| `@fN` | byte | replaces the glyph metric (normally from VA 0x538F0) with dword table VA 0x2D3758[N] (480, 536, 524 for N=0..2); N is not range-checked | 0 | E (meaning: H) |
| `@T` | - | ignored by the layout | 0 | E |
| `@1X` / `@2X` | escape | the next byte X becomes a literal `^` / `)` | 0 | E |
| `@e` / `@n` | - | script level only: end of a `_ZM`/`_ZD`/`_MSAD` message (5.2); not special in `_SELR` | 0 / S 469 / 678 | E |
| `@X(...)` | group | any other byte: skips 2 bytes, or the whole `(...)` group if one follows within 255 bytes | 0 | E |

No other `@` sequence occurs in any language or script [V-all]. The full per-id census
is in `samples/text/markup_catalogue.{md,json}`.

### 6.3 Ruby

`<base|reading>`: the base text is drawn normally and the reading is drawn small above
it. Pass 2 '<' at VA 0xC7717 scans ahead to measure the base width. The state values
are 1 (base) and 2 (reading); `>` returns to 0. Measured [V-all]:
- ja: 3,476 groups in 2,052 lines; zc: 763 in 166; zt: 1,092 in 404.
- Every group has exactly one `|`, a non-empty base and a non-empty reading. There is no nesting and no other markup inside a group.
- The longest base is 10 characters and the longest reading 14 (zt `<天梯|Jacob's Ladder>`: 8 zt readings contain spaces).
- Malformed forms do not occur. The engine handles them as described in 6.1: `<abc>` draws `abc` without ruby, extra `|` are swallowed, a second `<` restarts the group, and a stray `>` is swallowed in ja/zc/zt.

In English, `<...>` is ordinary punctuation: 236 `<` and 237 `>` in en lines. It is used for
Vlov's foreign-language speech (line 6570, a quoted sentence of the form `"<I shall ...>"`) and
for the 15 `<->` placeholders. en contains no `|`.

### 6.4 Icons

Table at VA 0x2D3580 (pass 1). A second copy at VA 0x187C80 is used by the glyph lookup at
VA 0x11750. There are 39 entries of 11 bytes, each a NUL-padded `[name]`. For icon *k*,
the glyph lookup derives a byte code `0xF9 + k` and a cell offset `0x49 * k` (the font
document covers the atlas). The last icon, zap00, has a fixed rectangle instead [E, partial].

| k | name | k | name | k | name |
|---:|---|---:|---|---:|---|
| 0 | `[heart]` | 4 | `[edia]` | 7..37 | `[ber00]`..`[ber30]` |
| 1 | `[heart2]` | 5 | `[dummy]` | 38 | `[zap00]` |
| 2 | `[star]` | 6 | `[onpu]` (♪) | | |
| 3 | `[star2]` | | | | |

Used in the data (ja / en / zc / zt):
- `[heart2]` 30/31/29/30; `[heart]` 18/18/18/18; `[star2]` 17/14/17/17; `[onpu]` 11/9/11/11;
- `[ber00]` 52 in 5 lines (screams: `「[ber00]×21」`); `[zap00]` 8 in 1 line (3571).

Every `[` in the data starts an icon [V-all].

### 6.5 Characters with special rendering (not markup)

- `―` U+2015, `─` U+2500 and `■` U+25A0 are joined into continuous bars when repeated. VA 0x11B80 looks at the previous and next
  character and picks start, middle or end pieces [E]. `■` is the censor block (en 478 uses).
- Leading indentation is part of the line text [D]. Counts [V-all], with the definition stated:
  - lines whose first character is U+3000: ja 29,971, zc 31,123, zt 31,104 (after optional leading `@` codes: ja 30,094, zc 31,246, zt 31,227);
  - en lines whose first two characters are spaces: 29,974 (29,979 after optional leading `@` codes); en lines starting with `"`: 11,572.
  ja/zc/zt narration starts with U+3000 and English narration with two spaces; dialogue starts with a quote.
  Choice lines are padded with trailing spaces or U+3000.
- `/` and `／` in en/ja are literal (stylistic slashes, e.g. 4526 `My chance /to ■■■ her/ for sure.`).
- Non-ASCII in English: `―` 1,091, `■` 478, `é` 15, `â` 3, `ê` 2, `è`, `ä`, `°` 1 each.

### 6.6 Style state (for a renderer)

`tsuki.text.to_runs(tokens, lang)` applies the codes and returns runs with a `Style`:
- `color`: None = the caller's default, e.g. 0xFFFF or 0xF6FD as passed by the text-window
  handlers;
- `color_ext`;
- `style`: 0 normal, 1 g, 2 z, 3 italic, 4 g+italic, 5 z+italic;
- `flag_b`, `anim`, `nowrap`, `scale_w`, `scale_h`, `font_metric`;
- a typewriter `seq` index.
Codes inside a ruby group change the style after that group's run.

Glyph flags written by the layout [E, 0xC790C..0xC7B39], `tsuki.text.glyph_flags(style)`:

```
flags = 1 | (style == 1) << 2 | {2: 0x100000, 3: 0x200000, 4: 0x400000, 5: 0x600000}[style]
if @a != 0 and the glyph is not a dash-ligature piece (0x11B80 returned -1): flags |= 8   # 0xC794F..0xC795C
if @b: flags |= 0x10000                                                                    # byte +2 bit 0, 0xC7B30
```

The visual meaning of these bits is up to the renderer document [H].

---

## 7. Python API (tools/tsuki/text.py, tools/tsuki/text_refs.py)

```python
from tsuki.text import ScriptText, tokenize, plain_text, to_runs, effective_languages, choice_slot_text
st = ScriptText()                 # opens <game>/image/script_text.mrg read-only
st.count                          # 43969
st.get_line("en", 3)              # '  The ...' (two spaces, then the 68-character sentence)
st.get_line(0, 3)                 # by index (0 ja, 1 en, 2 zc, 3 zt, 4 ko)
st.find("en", "<some words>")     # the ids of the lines that contain the words, e.g. [3]
tokenize(st.get_line("ja", 3), "ja")   # [Text, Ruby(base, reading), Text, Ruby, Ruby, Text]
plain_text(line, "ja", ruby="paren")   # the line with each ruby as base(reading), e.g. '　ここは見本(みほん)の...'
st.expand("$000119^", "en")       # $-expansion as in the engine (expand_bytes: raw bytes)
st.expand(choice_slot_text(1, "$042600"), "en", src_len=0xFA)   # '2. ...' (the second choice line, numbered by the engine)
effective_languages(0xFFFF, 11)   # (2, 2): shipped option + Simplified-Chinese system

from tsuki import text_refs
scripts = text_refs.load_scripts()            # [(name, script text)] from allscr.mrg
cmds = list(text_refs.iter_text_commands(scripts))   # TextCommand: op, arg, message, end_mode, locked, refs
text_refs.scene_transcript(st, scripts, "00_02", "en")
text_refs.message_groups(cmds)                # _ZM + appended _MSAD, as the engine buffers them
text_refs.classify_unreferenced(st, cmds)     # {id: class} for the 67 unreferenced ids
text_refs.voice_text_counts(scripts)          # text commands after each _VPLY / _VPL2
```

CLI:
- `python -m tsuki.text info | get ID.. [--lang L] [--tokens] | dump LANG [--start --count --plain -o FILE] | find LANG TEXT | census | expand MESSAGE [--lang L]`
- `python -m tsuki.text_refs summary | scene NAME --lang L | where ID..`

Samples (in `samples/text/`):
- `<lang>_first2000.txt`: `id<TAB>raw line` for ids 0..1999 in all 5 languages;
- `markup_catalogue.md/.json`: the full census, unreferenced classes, voice counts;
- `unreferenced_ids.tsv`: the 67 unreferenced ids with class, image and ja/en text;
- `scene_00_02_<lang>.txt`: the opening scene, expanded; `scene_03_00_ARC03_5B_en.txt`: a scene with locked choices;
- `textimg_IMG2629_id12.png`, `textimg_IMG2459_id82_quarter.png`: two of the text images of 5.7;
- `alignment.txt`: the cross-language checks.

---

## 8. Evidence (commands run)

- `python -m tsuki.text info`: 5 × 43,969 lines, table entries 43,972, one end mark per table, 0 validation
  issues. Validation covers: CR LF endings, no inner CR/LF, strict UTF-8, monotonic offsets,
  `size,size,0xFFFFFFFF` tail, last string ends at the blob end, number of end marks.
- `python -m tsuki.text find en "The familiar scent of straw mats and paper screens"` gives id 3.
  `get 3` prints the ja/zc/zt lines in the table of section 2.3.
- `scratch/text/explore2.py`: character census per language (ASCII punctuation, PUA, control,
  non-BMP). `explore4.py`: census of `@x`, `[..]`, `^`, `%`. `checks.py`: line lengths, ruby validity.
- `scratch/text/alignment.py` (output in `samples/text/alignment.txt`): length correlations, quote openers,
  code and icon sets per id, `<->` ids, ko placeholder check.
- `python -m tsuki.text_refs summary`:
  - 549 scripts, 44,109 text commands, 43,930 refs, 43,902 distinct ids, 0 out of range, 67 unreferenced
    (classes as in 5.1), 13 ids referenced only by `_ZD`;
  - `_SELR`: 824, 6 with a third argument (all locked), no message containing `,`;
  - argument shapes and code counts as in 5.2; voice counts as in 5.5; longest combined messages as in 5.3.
- `scratch/text/repair_checks.py` (repair pass): the new byte tokenizer gives the same tokens as the
  previous one on all 5 × 43,969 lines; token `raw` concatenation reproduces every line and all 44,109
  script arguments; the engine-exact `expand` equals the previous expansion on every message in 4 languages.
- `scratch/text/unref_classify.py`, `unref2.py`, `msad_empty.py`: script context of every unreferenced id
  and of the 45 empty `_MSAD()`. `scratch/text/render_textimg.py`: rendered IMG2458, 2459..2461,
  2629..2636 from allpac0 over black (`scratch/text/textimg/`); every band read and matched to 5.7.
- eboot disassembly (capstone, `scratch/text/eb.py`, `fulldis.py`, `cmpscan.py`, `xref_fast.py`):
  - loaders and the `$` path: 0x788C0 (archive open), 0x75F80 (line copy), 0x76040..0x763B8 (`$` expansion,
    incl. the mode-0 memcpy tail jump at 0x76351 and the final dword store at 0x76390);
  - language: 0x3ED90 / 0x407F0 / 0x41A20 / 0x41AD0 / 0x406B0 / 0x6FE94, data bytes at 0x66C59C, suffix table 0x42C030;
  - layout 0xC6C20: pass 1 and pass 2, the `@` dispatch at 0xC77D0 and jump table at 0x2D32C0 (handlers for
    `1 2 T a b c f g h i k l o p r s t u w x y z`, every other byte → 0xC8625), `@s` 0xC8836..0xC88AC,
    default group skip 0xC8625..0xC865D, `@c` 0xC869E / 0xC8CCA, `@y` 0xC89E4, glyph flags 0xC790C..0xC7B39;
  - layout tables: colour table 0x2D34C0, scale table 0x2D3730, metric table 0x2D3758, style flag table 0x2D34E0, icon tables 0x2D3580 and 0x187C80, dash ligature 0x11B80;
  - message handlers 0x92380 (`_ZM` copy loop 0x92458) / 0x92B60 (`_MSAD` append loop 0x92BC0) / 0x93370 / 0x7C6D0;
    marker dispatch 0x98479 (`_ZD` not displayed);
  - `_SELR` handler 0x9936D..0x9952A, string getter 0x76EA0, choice display loop 0x9B6B5..0x9B6E0, menu cursor code 0xA6360..0xA67E9.
- `pytest tools/tests/test_text.py`: 26 tests pass (7 s on an idle machine, up to 30 s under load; most of it decompressing allscr). With
  `TSUKI_GAME_DIR=/nonexistent`: 14 pass, 12 skip. `mypy` and `pyflakes` are clean on text.py and text_refs.py.

## 9. Open questions

1. What styles 1 (`@g`), 2 (`@z`), `@b` and `@aN` look like, i.e. how the renderer uses glyph flag bits 2, 3, 16 and 20..22.
   Needs the glyph draw routine (VA 0xC8F50 ff.) or a screenshot of ids 4478..4533.
2. Whether the colour words are RGB565 or BGR565. With RGB565, `@c1` (0xF800) is red and `@c5` (0x001F) blue.
   `@c6..@c9` (glyph field +0x0A) are not decoded.
3. The exact runtime behaviour of the end modes (`)` / `@n` / `@e`) and of `@k` (a click wait inside a message
   versus at its end), the `_MSAD` mode condition (VA 0x56140), and how backlog pages are grouped. This belongs to the script/UI document.
4. Units of `@x<digits>` / `@y<digits>`, and the meaning of the `@f` metric values (480/536/524). None of them is used in the data.
5. The caller flags of the layout function (0x40: ignore `@a`/`@b`/`@x` positioning; 0x2000: hide ruby;
   bit 20: start in style 2; 0x4000, 0x20000): which UI contexts set them.
6. The Korean slot (sections 8/9) is a blank placeholder and ft_ko.mrg exists. The game's language option cannot
   select index 4 (only an edited save could store it; see 3).
7. Line-break rules (pass 1 classes 1, 2, 3, 5, 0xD, 0xE, 0x71 for kinsoku / punctuation, English space wrapping)
   are only outlined here. A port that wants identical line breaks must transcribe 0xC6D80..0xC7375 and 0xC8002..0xC8BFF.
8. `_SELR` third argument: the cursor code skips flagged slots, so "locked" is the likely meaning; how a locked choice is drawn is not traced.
9. Whether the `１．`/`1. ` number of a choice slot is visible on screen (the menu layout may clip or hide it), and what the menu mode dword at VA 0x17506B0 selects.
10. How the 5.7 text images pick their language band (`_STCH` arguments / image code).

(Resolved: the `$` reference count. `tests/test_script.py` now also asserts 43,930 refs, the same definition as here.)

## 10. Notes on the reference tools

- mangetsu `script_text_to_content_json.cpp` reads only pair 0 and searches for CR LF itself.
  `repack_script_text_translation.cpp` writes the table as offsets, size, size and **three** 0xFFFFFFFF.
  Retail has **one** 0xFFFFFFFF. The engine reads only `off[id]` and `off[id+1]`, so both layouts work,
  and `tsuki.text.parse_offset_table` accepts both (2.1).
- deepLuna `translation_db.py` reads `[off[i], off[i+1])` (with CR LF) and stops at the first empty
  entry. Its packer writes the same `size, size, 0xFFFFFFFF` tail as retail and fills
  pairs 1..4 with blank lines. That matches only the ko slot in this PS4 version: en/zc/zt are real translations here.
- deepLuna's `%{...}` codes (`%{n}`, `%{i}`, ...) are its own pre-processing syntax. They are not engine markup and do
  not occur in the data.

## 11. Review log (repair pass)

Independent reviewers checked the first version. All findings were re-checked against the
disassembly and the data; none was refuted. What changed:

| finding | fix | proof |
|---|---|---|
| `_SELR` message kept `,1` (6 commands) | `text_refs` parses `_SELR` arguments like the getter VA 0x76EA0; `TextCommand.extra` / `locked`; doc 5.2 | 0x76EA0 stop mask 0x0800120000000000 (`,` `)` `;`); scene 03_00_ARC03_5B now prints `> choice 1 (locked): ...created.` |
| "42 cut passages" wrong; `_VPL2` undocumented | classes in 5.1, text images in 5.7, `_VPL2` in 5.5 | 12 images rendered and read (8 lines matched by both reviewers, plus IMG2630..2632, IMG2459..2461 here) |
| `$` cap off by one, missing `src_len`/mode | `expand_refs` rewritten byte-exact (cap 0x2FC, `src_len`, `expand=False`); doc 5.3 | 0x76062..0x76074, 0x761AE..0x761BE, 0x76205..0x7637E, 0x76390 |
| choice number from slot counter, slot layout | doc 5.4, `choice_number_id`, `choice_slot_text` | 0x99381, 0x993D9..0x9940A, 0x99427..0x99525, 0x9B6E0; `0x1750630 + 2k` holds n (one reviewer wrote 0x1750690; the lea at 0x994EA gives 0x1750630) |
| "each _VPLY precedes exactly one text command" | 5.5 gives the distribution | `voice_text_counts`: 10,641 of 16,689 have exactly one |
| `@y` clears first; glyph flag bit 3 | CODE_SPECS, 6.2, 6.6, `glyph_flags` | 0xC89EB `mov word [rsi-0x52], 0`; 0xC794F..0xC795C `or rax, 8` (only when 0x11B80 returned -1) |
| 0x75F80 / 0x76040 edge cases | doc 4 and 5.3; `engine_line(-1)` returns b"" | 0x75F90 `js`, 0x76002 `jl` skipping the NUL store at 0x76031 |
| language word default and raw read | doc 3, `effective_languages` | data bytes `ff ff 01 00` at 0x66C59C; 0x6FE94; 0x760F1..0x76104 |
| parser rejected mangetsu tables | `parse_offset_table` accepts m ≥ 1 end marks (`strict=True` for retail) | synthetic test with three end marks |
| tokenizer counted code points, ignored markup inside ruby | byte-level tokenizer, `Ruby.base_tokens/ruby_tokens`, `iter_tokens`; doc 6.1 | loop head 0xC76C0..0xC7711 tests `@`/`^` before ruby; windows 0xC8630 (0xFF) and 0xC8849 (0x100); same tokens as before on all retail lines |
| indentation counts depended on an unstated definition | 6.5 states both definitions | recounted |
| (found during the repair) `_ZD` is never displayed; `_MSAD` re-expands the whole buffer; `@c` ≥ 10 ignored | 5.1, 5.2, 5.3, 6.2; `message_groups`, `zd_only_ids` | 0x98479; 0x92B8F..0x92C4D; 0xC8CD0 |
