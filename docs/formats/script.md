# Scenario scripts: allscr.mrg

Scope: the scenario scripts of TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999,
v01.01): how they are stored, the script language (syntax, tokenizer, argument rules), the
complete command set with frequencies and best-known semantics, how commands reference text,
voices, sound effects, music, movies, pictures and other scripts, the label tables, scene ids,
the New-Game entry point and the scene graph. It is written so that a C#/C++ runtime can be
implemented from it without reading the Python reference (`tools/tsuki/script.py`,
`tools/tsuki/script_tables.py`).

Containers (mrgd00, MZX, NAM) are described in `docs/formats/containers.md`; text lines and
their markup in the text document (`tools/tsuki/text.py`); pictures in the image document
(`tools/tsuki/image.py`, `tools/tsuki/image_catalog.py`).

Confidence labels:

- **[E]** read in the game's own code. Addresses are eboot.bin text VAs (file offset = VA +
  0x2280); data VAs map to file offset = VA - 0x600000 + 0x365FA0.
- **[V-all]** checked on every occurrence in the retail data (count given). **[V-n]** on n
  samples.
- **[V-emulated]** the eboot's own machine code was executed under the unicorn CPU emulator
  (`tools/tsuki/script_emu.py`) and its results compared with our implementation.
- **[V-rendered]** pictures / movie frames were rendered from the data and looked at.
- **[D]** inferred from consistent data patterns (no counter-example in the corpus).
- **[M]** stated by mangetsu's `doc/script_commands.md` (Switch build) and consistent with data.
- **[H]** hypothesis.

---

## 1. Overview

```
image/allscr.mrg  (standalone mrgd00, 552 sections, 9,585,000 bytes)
  section 0      NAM32 name table: name k <-> script k       (549 names + zero record)
  section 1      nested mrgd00, 549 children: label table of script k (32-byte text records)
  section 2      nested image MZP, 128x96, 1 tile (not referenced by the scripts) [D]
  section 3+k    script k: MZX0 stream; decode normally, then XOR every byte with 0xFF
```

A decoded script is a single line of ASCII text (UTF-8 in TEST_SCRIPT only) made of statements
`_NAME(arg,arg,...);`. There is no bytecode: the PS4 engine interprets the text directly,
statement by statement (main interpreter VA 0x95E60) [E]. Every statement is either a command
with a 4-letter name or a *marker* `_Z<k><bbb><ss>(...)` that carries a message, a label or a
read-tracking id. Arguments are read by one of two readers depending on the command: a simple
inline integer reader or an object parser with a C-like expression evaluator (3.2..3.3a).
Picture coordinates are in a 480 x 272 script space; one script unit is 4 pixels of the
1920 x 1080 frame and a picture pixel is one frame pixel at 100 % (4.5).

The engine loads a script by *scene id*: a 549-entry table in eboot.bin (VA 0x216130) maps
script index k -> scene id (e.g. 240 = `00_00`); `_JUMP(240)` and the title menu use scene ids
[E]. Only one script is resident at a time, decoded into a static buffer at data VA 0x16545E0
(pointer at VA 0x174D640, cursor at VA 0x16543B0) [E].

Measured corpus [V-all]: 549 scripts, 19,617,273 decoded bytes (55 .. 253,727 per script),
1,083,168 statements (4 .. 14,139 per script), 98 distinct statement names (94 commands + the
4 marker kinds ZM/ZZ/ZY/ZD), 3,654 labels, 43,930 text references.

## 2. Storage

### 2.1 Container [V-all]

allscr.mrg is an ordinary mrgd00 container (containers.md 4.x): `"mrgd00"`, u16 N = 0x228 (552),
552 section records, data_start = 8 + 8*552 = 0x1148.

```
00000000: 6d72 6764 3030 2802 0000 0000 0900 c044   mrgd00(........D   N=552; s0 = (0,0,9,0x44c0)
00000010: 0800 c804 3e00 18eb 4500 e807 0200 c004   s1 = (8,0x4c8,62,0xeb18)  s2 = (0x45,0x7e8,2,0x4c0)
00000020: 4600 b004 0100 8601 4600 3806 2100 8001   s3 = (0x46,0x4b0,1,0x186)  s4 = (0x46,0x638,33,0x180)
```

| section | offset | size | content |
|---:|---:|---:|---|
| 0 | 0x1148 | 0x44C0 | NAM32 (549 x 32-byte names + zero record) |
| 1 | 0x5610 | 0x1EB18 | nested mrgd00 with 549 label tables |
| 2 | 0x24130 | 0x4C0 | nested image MZP (128x96, 1 tile) |
| 3 | 0x245F8 | 0x186 | script 0 `START_SCRIPT` (MZX0, 1,173 bytes decoded) |
| 551 | 0x923BC8 | 0x59A | script 548 `JUMPQA` |

The engine picks section `k + 3` for script k when k < 600 (0x258), else section 3
(VA 0x7917A) [E].

### 2.2 Script encoding [V-all, E]

```
000245f8: 4d5a 5830 9504 0000 33a0 a5a5 cfcf 9ecf   "MZX0", size 0x495 = 1173, commands...
plain MZX decode:  a0 a5 a5 cf cf 9e cf ce d7 d6 c4 a0 ...
XOR 0xFF:          5f 5a 5a 30 30 61 30 31 28 29 3b 5f ...  "_ZZ00a01();_ZZ00b01(START);_WKST(F823,eq,0);..."
```

Loader VA 0x79170 / 0x79320: `mzx_size`, `mzx_decompress` into the script buffer, then a
vectorised XOR with all-ones over exactly `size` bytes (VA 0x791E4..0x7928B). A decoder that
XORs during decompression must use 0xFFFF as the initial *and* reset value of the MZX `last`
word (containers.md 5.4). All 549 scripts decode to their declared size; 0 failures.

### 2.3 Names (section 0) [V-all]

NAM32 records (32 bytes: NUL-padded ASCII name, `\r\n` at +30), record k names script k:
`START_SCRIPT`, `00_00`, `00_01`, `00_02`, `01_00_ARC01_1`, ..., `QA_01..QA_25`,
`12_01_CIEL12_3C_FLAG0`, ..., `ARCHIVE`, `QACALL`, `STAFFROLL`, `TEST_SCRIPT`,
`JUMP00..JUMP15`, `JUMPQA`. The full list with scene ids is `samples/script/scene_ids.tsv`.
Naming: `<chapter>_<route>_<scene>`; route 00 = Arcueid, 01 = Ciel [D].

### 2.4 Label tables (section 1) [V-all]

Child k of section 1 is the label table of script k: N records of exactly 32 bytes of ASCII,
no header, no terminator:

| off | size | content |
|---:|---:|---|
| 0 | 21 | label name, padded with spaces (longest in retail: 20 chars) |
| 21 | 3 | block id of the defining marker, 3 lower-case hex digits |
| 24 | 1 | `,` |
| 25 | 5 | byte offset of the defining marker in the decoded script, 5 hex digits |
| 30 | 2 | `\r\n` |

Example (START_SCRIPT and 00_00):

```
00006740: 5354 4152 5420 2020 2020 2020 2020 2020   START                   (script 0)
00006750: 2020 2020 2030 3062 2c30 3030 3062 0d0a        00b,0000b..      block 0x00b, offset 0x0000b
00006768: 5354 4152 5420 2020 ...  3031 352c 3030 3030 620d 0a   START   015,0000b   (script 1)
00006788: 4c42 4c5f 3030 3020 ...  3031 362c 3030 3032 650d 0a   LBL_000 016,0002e
000067a8: 4c56 3030 3030 3020 ...  3031 372c 3030 3263 620d 0a   LV00000 017,002cb
```

Offset 0xB in START_SCRIPT is `_ZZ00b01(START);` (block 0x00b). All 3,654 records point
exactly at a `_ZZ`/`_ZY` marker with the same block id whose argument is the label name, and
every named `_ZZ`/`_ZY` marker has a record [V-all, 3,654]. Offsets are byte offsets of the
UTF-8 script (matters only for TEST_SCRIPT).

Engine lookup (VA 0x794D0) [E]: copy the wanted name (stop at NUL, TAB, LF, CR, `.`, max 32),
scan the records (max 1000) comparing up to the first byte <= 0x20 of the record, then parse
the 5 hex digits at +25 (`0-9`, `a-f`) and return `script_base + offset`. A missing label
makes the engine read garbage (no error path); the retail data has none (4.1, 7).

### 2.5 Sizes

Decoded size per script ranges from 55 bytes (the `*_FLAG0/1` stubs) to 253,727
(`15_01A_CIEL15A_1`); the engine decodes into one static buffer (VA 0x16545E0) [E]. Longest statement: 207 bytes (a `_GOTS` in JUMP10), well
under the engine limit of 0x300 [V-all].

---

## 3. Script language

### 3.1 Statements and tokenizer [E VA 0x95EC0..0x95F4E, V-all]

```
run(cursor):                                   # main interpreter, VA 0x95E60; returns new cursor
  repeat at most 1000 times:                   # VA 0x95EB0: inc r11d; cmp 0x3E8
    end = cursor; name = NULL
    for i in 0 .. 0x2FF:                       # at most 0x300 bytes per statement
      c = script[cursor + i]
      if c == ';': end = cursor + i + 1; break
      if c == '_' and name == NULL: name = cursor + i + 1
    else: end = cursor + 0x300                 # no ';' (never happens in retail data)
    cursor = end
    if name == NULL or not ('A' <= script[name] <= 'Z'): continue        # skipped
    handler = dispatch(script[name], script[name+1..name+3])            # 3.6
    if handler == NONE: continue               # unknown command: ignored, no error
    execute handler (arguments start at name + 5, see 3.2)
    if the handler yields: return cursor (or the jump target)
  return cursor
```

Consequences, all observed in retail data:

- The separator after the 4-letter name is never checked: `_STTR,0,26);`
  (`14_01A_CIEL14A_2`) works like `_STTR(0,26);` [V]. (The 4 name bytes are taken even if a
  `;` is among them; no retail statement is that short.)
- Bytes after `)` up to `;` are ignored: `_STGS(0,,0,800,0)#);`, `_SQK2(...)+);`,
  `_MFAD(...)0);`, `_STZ4(...)gb);` (4 statements) [V].
- There are no line breaks, comments or whitespace in retail scripts. The 549 scripts
  re-tokenize to exactly their bytes (concatenation of statements == script) [V-all].

### 3.2 Arguments: two readers

Arguments start 6 bytes after the `_` (5 for the name, 1 separator) for commands, and 9 bytes
after it for markers (`_ZMbbbss(` is 9 characters). The PS4 engine has **two independent
argument readers**, and which one applies is a property of the command handler [E:
`scratch/script/argreaders.py` walks every handler and its callees (depth 3) and records the
reader calls]:

| reader | used by | integer syntax | empty argument | missing argument |
|---|---|---|---|---|
| **inline** readers VA 0x76690 (i16), 0x76780 (i32), 0x76860 ("present"); an i64 variant VA 0x768E0 has one call site (VA 0x850BE) | all commands not in the next row (WKST, WKAD, IF__, SEPL/SEVL/SEFD, MPLY/MVOL/MFAD, VPLY, WTTM, SHOK, STPV, SELR, SELD, ...); JUMP, GOTO, PGST, STCG ... parse inline with the same rules | sum of decimal terms, all other bytes ignored (3.3) | 0 ("present" = false) | -1 |
| **object parser** VA 0xD690 + getters (3.3a) | the 52 commands `CLO4 CLO5 FADE FADS FCCL FCLN FCSP FLNT FLOP FLSP MSQK RDAS RDED RDST RTST SACL SBAS SCH2 SELA SELS SGMA SHK2 SHS2 SNX2 SNX3 SOXY SPFC SPMA SPRD SQK2 SQS2 SSS2 SSW2 STBR STCC STCF STCH STCL STCP STEC STGS STMA STNS STRA STRC STRR STRT STTR STZ4 TOMP WTSV WTTR` (`OBJ_COMMANDS` in script_tables.py) | C-like expression (3.3a) | `has` = false: the handler's own default | `has` = false: the handler's own default |

Argument kinds in the data:

| form | example | meaning |
|---|---|---|
| integer / expression | `240`, `-1`, `258-18`, `154-19` | 22,536 arguments are `a-b` / `a+b` expressions; no retail argument of an object-parser command has two operators, so the readers' different associativity (3.3a) never matters in retail data [V-all] |
| decimal | `242.25`, `2000.0`, `109.286` | 28 arguments (24 STCH alpha, 3 STGS, 1 SSW2) in 9 scripts, all in object-parser commands, which read the integer part (242, 2000, 109); the inline reader would give 24225 / 20000 [V-all, E] |
| empty | `_STCH(1,WHITE,240,136,1000,255,2,,CC)` | see the table above |
| `()` | `_CLO3();` | 14,305 statements (CLO3 12,208, RTM_ 545, END_ 519, SHAF 397, ATSV 313, SPRC 85, VCWT 75, TOMW 48, ...): inline index 0 = **0** (empty, not missing), index >= 1 = -1; object parser: 0 arguments [E, V-emulated] |
| tagged (keyword) | `` `017:100 `` | backtick, 3 decimal digits, colon, value: a keyword parameter, 3.4 (32,221 arguments, all in inline-reader commands; the object parser has no tag support: `` `017:100 `` evaluates to 0) |
| name | `img2345`, `MOON_SE03001`, `START`, `B90_00_01_0001_0` | picture, SE, label, voice name ... |
| variable | `F823`, `G008`, `C847`, `_D000` | 3.5 |
| operator | `eq`, `ne`, `ge`, `gt`, `lt`, `le` | `_WKST`, `_IF__` |
| hex | `03fb5` | voice index (`_VPLY`), 5 hex digits |
| colour | `0xFFFFFF` | `_STEC` arg 2, `_SPFC` arg 7: copied as a string (VA 0xD910, at most 15 bytes) and converted with `strtol(s, NULL, 0)` (base 0: `0x` prefix = hex) [E VA 0x104F8..0x10509, 0x98E81..0x98E9F] |
| message | `$000003`, `@x$001397^$001398@n` | text references and markup, 5 |

### 3.3 Inline integer reader [E VA 0x76690 (i16), 0x76780 (i32), 0x76860; V-emulated]

`p0` = the byte after the separator (name + 5). All three routines share the skip loop.

```
skip(p0, index):                               # index is i16; index <= 0 skips nothing
  p = p0
  repeat index times:
    for k in 0 .. 499:                         # at most 500 bytes per argument
      c = script[p + k]
      if c == ')' or c == ';': return MISSING
      if c == ',': p = p + k + 1; break
    else: p = p + 500
  return p

read_int(p0, index, bits):                     # VA 0x76690 (bits 16) / 0x76780 (bits 32)
  p = skip(p0, index); if MISSING: return -1
  if script[p] == '`': p += 5                  # blind 5-byte skip of a keyword tag
  total = 0; num = 0; sign = +1
  for i in 0 .. 15:                            # at most 16 bytes
    c = script[p + i]
    if c in ",);": break
    if c == '+': total += sign*num; num = 0; sign = +1
    elif c == '-': total += sign*num; num = 0; sign = -1
    elif '0' <= c <= '9': num = num*10 + (c - '0')
    else: ignore c                             # '.', 'x', letters, NUL ... are skipped
  return (total + sign*num) truncated to `bits` (two's complement)

present(p0, index):                            # VA 0x76860
  p = skip(p0, index); if MISSING: return false
  if script[p] == '`': p += 5
  return script[p] not in ",);"
```

So `258-18` = 240, `1-2-3` = -4 (left to right), `-1` = -1, `` `001:4000 `` = 4000, empty =
0, `()` index 0 = 0, `(5)` index 1 = -1, `242.25` = 24225, `a5b6` = 56, `40000` = -25536 as
i16. The JUMP handler inlines the same algorithm (VA 0x96212..0x9629A) [E].

Verification: the three routines were executed on the real eboot code under the unicorn
emulator (`tools/tsuki/script_emu.py`) and compared with `tsuki.script.inline_int` /
`inline_present`: 35,776 + 17,888 reads on 4,000+ retail statements (argument indices 0..3,
16 and 32 bits) and 27,000 fuzzed reads, **0 mismatches** [V-emulated,
`scratch/script/vreaders.py`].

### 3.3a Object argument parser and expression evaluator [E VA 0xD690, 0xD8C0, 0xD910, 0xDA50, 0xDE30, 0xE310, 0xE3C0; V-emulated]

**Split** (VA 0xD690, called with `p` = the separator byte, name + 4). State: u64 count + 20
(start, end) pointer pairs.

```
split(p):
  args = []
  if script[p+1] in ");": return args          # "()" has 0 arguments
  q = p + 1
  loop:
    if script[q] == '"':                       # quoted: may contain , ) ;
      start = q + 1; end = strchr(start, '"')
    else:
      start = q;     end = strpbrk(start, ",);")
    args.append((start, end))
    skip = 1 if script[end] == '"' else 0
    if script[end + skip] in ");" or len(args) == 20: return args
    q = end + skip + 1                         # the byte after a closing quote is the separator
```

Getters: `has(i)` (VA 0xD8C0) = `i < count and start < end` (non-empty); `str(i, buf, n)`
(VA 0xD910) copies at most n-1 bytes; `expr(i)` (VA 0xDA50) evaluates the expression below and
returns 0 when the argument is missing or empty; `int(i, def)` (VA 0xE310) = `strtol(copy, &e,
10)` of the whole argument (1..15 bytes; leading white space and one sign accepted), `def` when
`*e != 0`; `tryint(i, &v)` (VA 0xE3C0) stores that strtol value even when it reports failure
(used by TOMP). Handlers almost always test `has(i)` first and use their own default when it is
false, so for these commands an empty and a missing argument mean the same thing. No retail
argument is quoted and none has more than 20 arguments [V-all].

**Tokenizer** (VA 0xDE30): a shunting-yard over the bytes `[start, end)` producing postfix
tokens; `{` `}` are the parentheses (a `{` recurses with terminator `}`; `(` `)` cannot be used
because they delimit arguments).

```
tokenize(p, end, term):                        # top level: term = NUL
  ops = []                                     # operator stack (one per nesting level)
  if p >= end or *p == 0 or *p == term: goto finish
  expect_operand = true
  loop:
    c = *p
    if not expect_operand and (p >= end or c == 0 or c == term): goto finish
    if c is '0'..'9':
      v = strtol(p, &p, 10)                    # base 10, 64-bit, clamped; '0x10' is 0 then 'x'
      emit NUMBER(int32(v)); expect_operand = false; continue
    if c == '{':
      r = tokenize(p + 1, end, '}')            # returns the position of '}' (or end)
      if r == NULL: CRASH                      # the engine dereferences address 1
      p = r + 1; expect_operand = false; continue
    if end - p < 2: return NULL                # operator as the last byte: STOP (no flush)
    op = operator starting at p (table below; two-byte operators consume p[1])
    if op unknown (letter, '.', '`', space, '(', '=' not followed by '=', ...): return NULL   # STOP
    while ops and level(op) > level(top(ops)): emit pop(ops)     # strictly greater
    push(ops, op); p += 1
    if p >= end or *p == 0 or *p == term: goto finish
    expect_operand = true
finish:
  while ops: emit pop(ops)
  return p
```

Operators (`level` = precedence, lower binds tighter; `+` `-` are unary when an operand is
expected):

| level | operators (token code) |
|---:|---|
| 2 (unary) | `+x` 0x22000001, `-x` 0x22000002, `!x` 0x22000003, `~x` 0x22000004 (`!` and `~` are unary even after an operand: `5!` is `!5` when more bytes follow) |
| 3 | `*` `/` `%` (0x33000001..3) |
| 4 | `+` `-` (0x34000001..2) |
| 5 | `<<` `>>` arithmetic (0x35000001..2) |
| 6 | `<` `<=` `>` `>=` (0x36000001..4) |
| 7 | `==` `!=` (0x37000001..2) |
| 8 / 9 / 10 | `&` / `^` / `\|` |
| 11 / 12 | `&&` / `\|\|` |

Because operators of equal level are not popped, **chains are right-associative**: `10-3-2` =
10-(3-2) = 9 (the inline reader gives 5). A STOP abandons the pending operators but keeps what
was already emitted: `242.25` -> 242, `5-` -> 5, `1 2` -> 1, `img1728` -> nothing -> 0.

**Evaluation** (VA 0xDA50): a stack of int32. NUMBER pushes; a unary operator replaces the top
(`-x`, `!x` = (x == 0), `~x`, `+x` = x); a binary operator pops b and a and pushes `a op b` with
32-bit two's-complement wrap-around (`/` and `%` truncate toward zero like C; `<<`/`>>` use the
low 5 bits of b; comparisons, `&&`, `||` give 0/1). The result is the top of the stack; an
empty token list gives 0. Undefined (the engine crashes or returns stack garbage): division by
zero or INT_MIN/-1, an operator without operands, an error inside `{...}`.

Examples: `258-18` 240, `2*-3` -6, `{10-3}-2` 5, `1<<4|1` 17, `-7/2` -3, `-7%3` -1, `0x000000`
0, `` `017:100 `` 0, `12345678901234567890` -1 (strtol clamps to LONG_MAX; low 32 bits).

Verification: every distinct argument text of the object-parser commands in the retail
corpus (35,474 texts) was evaluated by the engine's own code under unicorn and by
`tsuki.script.obj_expr`: **35,474 / 35,474 equal**; 8,608 retail statements split identically
(`obj_split`); 2,490 fuzzed expressions over the bytes ``0-9 + - * / % < > = ! & | ^ ~ { } . x ` a``
and space equal (510 undefined ones skipped); 3,000 fuzzed `int()` (VA 0xE310) equal
[V-emulated, `scratch/script/vreaders.py`, `tests/test_script.py::test_readers_match_engine_emulation`].

### 3.4 Keyword tags [E VA 0x87276..0x87440 for MPLY; D for the rest]

The audio handlers scan *all* arguments; an argument starting with `` `NNN: `` is filed under
tag NNN (3 decimal digits) regardless of position, untagged positions keep their positional
meaning. Tags seen [V-all]:

| tag | used by (count) | values | meaning |
|---|---|---|---|
| 001 | SEPL 2,788, SEFD 3,516, SEVL 1,285, MPLY 369, MVOL 780, MFAD 724 | 300..10000 | fade time in ms [E for MPLY: default 0; D] |
| 011 | MFAD 724 | always 0 | fade-out target volume [H] |
| 017 | SEPL 14,540, SEVL 1,285, MPLY 1,170, MVOL 780 | 0..100 | volume in percent [E for MPLY: default 100; D] |
| 018 | SEPL 15 | 32..96 | unknown (pan?) [H] |
| 049 | MPLY 1 | 0 | MPLY reads it with default 1 (loop flag?) [E default; H meaning] |
| 068 | SEPL 3,471, SEFD 583, SEVL 40 | 100..6000 | delay before the action, ms [H] |
| 069 | MPLY 131 | 348,160 .. 7,896,832 | read as i32 (VA 0x76780); start position in samples (48 kHz) [H] |
| 070 | SEPL 19 | 2..16 | repeat count [H] |

### 3.5 Variables [E VA 0x987C5..0x98902 (first-character dispatch); EMU; V-all]

Corrected after the engine re-check; the full model, emulation evidence and operand table are
in **vm_variables.md** (Python: `tsuki/vm_vars.py`). Storage: `var_F` u16[1200] at VA
0x164B1D0, `var_Z` u16[1400] at 0x164BB30, `var_G` u16[2000] at 0x164C620, no bounds check.

| namespace | used | storage and semantics |
|---|---|---|
| `F<n>` | 77 names, F022..F1123 (+ F0 via `_D000`: 78 indices) | F[n]; F823 = number of the current message on the page (the scripts reset it per page and `_WKAD(F823,1)` before each message) [EMU, V-all] |
| `G<n>` | 18 names, G003..G029 and `G70` | G[n]; all < 500, i.e. saved in the system data, shared by all slots [EMU, E] |
| `C<n>` | C847, C859 (85,471 `_WKST` + 3,075 `_WKAD` targets) | **not a separate counter**: as a `_WKST` / `_WKAD` target `C<n>` is F[F823 + n], so during message k `C847` = F[847 + k] and `C859` = F[859 + k] (k = 1..10 in the data; with F823 = 0, C847 = F[847]). In a value position `C<n>` is the literal n; Script_Prefetch ignores C targets [EMU] |
| `_D<n>` | `_D000` | F[0]: `_SELD` / `_SELS` record it as the choice target (byte 1 `D` = kind F); the choice UI writes the chosen `_SELR` value there (`_SELD`) or the button index 0 / 1 (`_SELS`), and `_GOTS` jumps to label number F[0]. `_SELA` (the "save your progress?" prompt) also records the target but its dialog stores nothing; it opens only when F1118 = 0 and always clears F1118 (vm_variables.md 4.5). In scan mode none of the three changes anything [EMU] |
| `Z<n>` | not used | never a variable in a script command: no effect as a target, the literal n as a value [EMU] |

Dispatch by the first byte of the argument (after an optional 5-byte `` ` `` tag): targets of
`_WKST` / `_WKAD` accept `C F G _`, `_WKOR` / `_WKOF` `F G _`, `_WKBO` `F G`; value positions
(`_WKST` arg 2, `_WKAD` / `_WKOR` / `_WKOF` arg 1, both `_IF__` operands) read `F G _` and take
every other argument as its inline-reader value (3.3). `_` operands select G[n - 600] for n in
600..700, else F[n]. Every F index the scripts use is kept by the history record / save slot
(vm_variables.md 6).

### 3.6 Dispatch [E VA 0x95F09..0x95F4E; V-emulated]

`c0 = name[0]` (must be `A`..`Z`) selects an entry of the 26-entry jump table at VA 0x20CAC0;
the rest of the name is the 24-bit key `name[1]<<16 | name[2]<<8 | name[3]`, matched by a
compiled compare tree (with ranges and jump tables, e.g. CLO2..CLO5 = key - 0x4C4F32 in 0..3).
Rules that follow from it:

- **every** name starting with `J` is JUMP (the `J` entry does not look at the other three
  characters); **every** name starting with `Z` is a marker (3.7); `B D H K L N O Q U Y` go
  straight to the loop continue (no command starts with them);
- aliases sharing a handler: `SEFI` = `SEPL`, `VPL2` = `VPLY`, `WT__` = `WTTM`. `RTMN` is
  **not** an alias of `RTM_`: it first sets the keep-BGM flag (4.1) and then runs the `RTM_`
  code;
- names without a case are ignored (no error). Used in retail scripts but **not implemented by
  the PS4 engine** (no-ops): `SNX4` 8,758, `FANX` 2,741, `IFP4` 203, `DMMY` 71, `STQK` 3,
  `NOP_` 1 (6 names) [E, V-emulated]. `RTM2` (1, end of 00_02) is handled (4.1);
- accepted but unused by the scripts: `VCLR` (stores -1 to VA 0x16541FC and 0x1654200, then
  continues), `IFF_ IFJP IFLA ELS_ EIF_ WKOF WKBO FLNT FLSP FLOP STRC SNOW SHAK SHAS FCCL MSTP
  SEPN SEAC SEP3 WTTR` (4.x).

The complete list is `ENGINE_MAIN` in `tools/tsuki/script_tables.py`: 110 names plus `J*` and
`Z*`, with handler entry VA and frame behaviour. Method [V-emulated]: the dispatch code was run
under unicorn (`scratch/script/dispatch_emu.py`) for every first letter and every key value at
the 848 breakpoints of the compare tree (all immediates compared with the key, +-1, and every
value of the range/jump-table cases), which covers all 2^24 keys per letter because the outcome
is constant between breakpoints; a brute-force run over all 26 x 37^3 names `[A-Z][A-Z0-9_]{3}`
(1,316,978 names, 234 s) gives the same 110 names. A name counts as handled when execution
reaches an instruction with a side effect (memory operand other than a frame spill or a switch
table load, call, push/pop) before the loop continue VA 0x95EB0; the handler VA is the start of
that basic block. (The first version of this table came from a symbolic evaluator that treated
stores as transparent and therefore missed RTM2 and VCLR and mis-assigned RTMN.)
`tests/test_script.py` re-runs the emulation for all 112 entries and the 6 no-ops.

**Frame behaviour.** Handlers end by either continuing with the next statement (jump to
0x95EB0) or returning from `run()` (jump to 0x9B516, "yield"). The caller (VA 0xA131D,
0xA1DA6) stores the returned cursor and calls `run()` again on a later frame. Always yield:
CLO2, CLO3, CLO4, CLO5, END_, FADS, MSAD, SELA, SELD, SELS, STCF, TOMW, WTSV, WTTM/WT__, WTTR.
May yield (waits, jumps, messages, scan mode): ATSV, FLNT, GOTO, GOTS, IF__, IFJP, IFLA, IFSC,
IFUR, J*, MFAD, PGST, RDED, RTM_, RTMN, SHAK, TOMP, VCWT, VPLY, Z*, ... Everything else
(including RTM2 and VCLR) never yields [E: static path analysis `scratch/script/hsummary.py`].
A runtime that executes one statement at a time and blocks on waits is equivalent as far as
the script is concerned [H].

**If-block skip counter** [E]: the engine-only block commands `IFF_`/`ELS_`/`EIF_` maintain a
counter at VA 0x165420E; while it is non-zero, GOTS, J*, GOTO, IF__, RTM_ and IFJP are ignored
(VA 0x960A7, 0x96204, 0x96E3F, 0x9708D, 0x97181, 0x9807D). No retail script uses these blocks,
so the counter is always 0.

**Scan mode** [E VA 0xA4380..0xA43B7, 0x96878]: to restore a save, the engine sets the byte
at VA 0x174DE48, stores the target page in VA 0x873040 and runs `run()` from the scene start
until a `_PGST(n)` with n == target; most handlers return early in this mode (no
sound/drawing/waits). Save data therefore needs only (scene, page, variables) [H for the
complete save contents].

**Second dispatcher** VA 0x9B760 (jump table VA 0x20D170) [E]: called after a script load with
a statement count (0x18) (VA 0xA1E0E, 0xA23C9). It accepts flow control (GOTO, IF__, IFF_,
IFJP, ELS_, EIF_, J*, END_, FRET, Z*), variables (WKST, WKAD, WKOR, WKOF), PGST, FADS,
STCH/SCH2/STCN, every `S*` and `V*` name, plus legacy names not used by any script (ANIM,
EVT0, EVT1, EVCL, EVC?, PLD_, PLDB, PLDF, PLDS, PLDV, STBG, STEV, SSLA, FCAL). It looks
ahead along the control flow and collects picture names (VA 0x78410) - an asset prefetcher [H].
A port does not need it for correctness.

### 3.7 Markers `_Z<k><bbb><ss>(arg)` [E VA 0x96615..0x96750, 0x98479; V-all, V-emulated]

| bytes after `_` | meaning |
|---|---|
| `Z` | marker |
| k | kind: `M` message (42,590), `Z` label / statement (2,570), `Y` local label (1,633), `D` disabled message (17) |
| bbb | block id, 3 lower-case hex digits (0x000..0xF82 in retail) |
| ss | sub id, 2 lower-case hex digits; 1, 2, 3 ... within a block |

Digit decoding (VA 0x9665A..0x966E3): `d = c - 0x30` if `c < 'a'` else `c - 0x57`,
`v = v*16 + d`, truncated to a signed 16-bit value. Only `0-9` and `a-f` decode correctly:
`00A` gives 0x11, not 0x00A. All 46,810 retail markers use lower-case hex [V-all];
`tsuki.script.marker_hex` reproduces the engine for any byte (500 fuzzed markers compared
under emulation, 0 mismatches).

Common processing for every kind, **in this order** [E]:

```
prev_block = [0x1654344]; prev_sub = [0x1654348]          # the previous marker's ids
if prev_sub > read_table[prev_block] and prev_block <= 0xFB3:
    read_table[prev_block] = prev_sub                     # read_table = VA 0x17DA268 + 0x10, u8 per block
block, sub = decode(bbb), decode(ss)
[0x1654344] = [0x165434C] = block; [0x1654348] = [0x1654350] = sub; [0x1654354] = 1
is_read = (read_table[block] != 0) and (sub <= read_table[block] or block >= 0xFB4)
```

So a message is recorded as read when the **next** marker is reached, not when it is shown;
the "already read" test for the new marker (used by skip / read-text colouring [H for the UI
use]) compares its sub id with the highest sub id recorded for its block. A history snapshot
may follow (VA 0x7AF90, conditional). Then:

- `ZM`: display the message argument (handler VA 0x92380 with name + 7; text, section 5). Yields.
- `ZY`: copy the argument (label name, from name + 8) to the scratch buffer VA 0x16543C0 and
  set VA 0x165420C = 1 [E; purpose H]. The argument is a label (`LV00000` ...).
- `ZZ`: nothing more; the argument (if any) is a label (`START`, `LBL_000`, `SEL0_1` ...).
  Empty `_ZZ...()` markers open every script.
- `ZD`: nothing more: a *disabled* message. All 17 `_ZD` follow the pattern
  `_DMMY(C847,eq,0);_DMMY(C859,eq,0);_DMMY(0);_ZD...($n)` - the compiler replaced the normal
  `_WKST(C847,eq,0);_WKST(C859,eq,0);_WNTY(0);_ZM...($n)` with dummies, i.e. lines removed
  from the game [D].

Block/sub ids increase strictly within a script and continue across scripts in archive order;
only variant scripts reuse ids: `00_02` reuses `00_01`'s header ids (0x19..0x1C) and JUMP01..JUMPQA
all start with JUMP00's header block 0xED8 [V-all: 46,810 markers, 3,419 distinct blocks].

---

## 4. Command catalogue

Columns: **count** = statements in the 549 retail scripts [V-all]; **args** = observed argument
syntax (`[...]` optional, `_` empty, `` `NNN `` tagged); **VA** = PS4 main-interpreter handler
(`-` = not implemented, the statement is skipped); **f** = frame behaviour (y = always yields,
m = may yield, c = continues); **semantics** with confidence. Commands marked (O) read their
arguments with the object parser (3.3a; missing/empty = handler default, decimals truncated,
right-associative expressions); all others use the inline reader (3.3). Layers are numbered
0..39 (the engine's per-layer arrays have 40 = 0x28 entries, VA 0x78DE9; STCH and STMA reject
layer > 0x27) [E]. Coordinates: 4.5.

### 4.1 Flow control, variables, scenes

| cmd | count | args | VA | f | semantics |
|---|---:|---|---|---|---|
| JUMP | 1,741 | (scene_id [, label]) | 0x961DC | m | Load the script whose scene id is `scene_id` (6.1; linear search VA 0x96755..0x96785) and continue at `label`, or at the script start when omitted (1,112 without label). [E lookup; V-all: all 1,741 targets exist, all 629 labels exist in the target] |
| GOTO | 101 | (label) | 0x96E3F | m | Continue at `label` of the current script: VA 0x9AB74 copies the label from name + 5 (after an optional tag) up to `)` `,` `;` and looks it up (2.4). Ignored while the if-block skip counter is set (3.6). [E, V-all] |
| IF__ | 1,194 | (var, op, int, label) | 0x9708D | m | If `var op value` then goto label; ops eq 645, ne 521, ge 22, lt 4, gt 1, le 1. [E reads/lookup; D semantics; V-all labels] |
| GOTS | 170 | (var, label0, label1, ...) | 0x9609A | m | Computed goto: jump to label[var] (var = `_D000` choice result; 1..10 labels). [D, V-all labels] |
| IFNX | 233 | (_, label) | 0x97F00 | c | Platform branch "if not NX (Switch) goto label": on PS4 it always jumps when the label is non-empty. [E] |
| IFP4 | 203 | (_, label) | - | | Platform branch "if not PS4 goto label": no-op on PS4. Used as `IFP4(,A) <PS4 variant> A: IFNX(,B) <Switch variant> B:`. [E, D] |
| IFSC | 2 | (_, label) | 0x96185 | m | Goto label if a system condition holds (VA 0x67430); used at the start of ARCHIVE. [E call; H meaning: scene-replay mode] |
| IFUR | 1 | (1, COMP_CHECK_OK, label) | 0x98161 | m | Goto label if system check 0xB7D80(1) passes (QA completion). [E call; H] |
| WKST | 487,857 | (var, eq, int\|var) | 0x96566 | c | Assignment `var = value` (operator always `eq`). 21 statements copy another variable (8 forms: `F078=F051` x4, `F079=F078` x4, `F080=F079` x4, `F075=F050`, `F076=F075`, `F077=F076`, `F081=F080` x2 each, `F026=G008` x1). Mangetsu's "some sort of wait" is wrong. [E: reads name, checks op 'e', reads value with VA 0x76690; D; V-all counts] |
| WKAD | 45,679 | (var, int) | 0x97AE6 | c | `var += value` (`_WKAD(F823,1)` before every message). [D, M] |
| WKOR | 21 | (Gnnn, int) | 0x9798B | c | `var \|= value` (always G variables, value 2). [H from name and use] |
| END_ | 519 | () | 0x9AB42 | y | End of script (in scan mode: yield only). Calls the script-end cleanup VA 0x7F1A0 (below), sets the cursor to the script start and the script state VA 0x174DE4C = 1. [E] |
| RTM_ | 545 | () | 0x97174 | m | Return to the title menu: cleanup VA 0x7F1A0, script state 2 (VA 0x9AA88). Ignored while the if-block skip counter is set; yields only in scan mode. Usually unreachable after a JUMP (`_JUMP(300);_RTM_();_END_();`); reached in TEST_SCRIPT `SEL_RETTIT` and after RTM2 at the end of 00_02. [E state; D meaning] |
| RTMN | 1 | () | 0x9643E | m | Sets the **keep-BGM flag** (byte VA 0x174DE0C = 1), then runs the RTM_ code at 0x97174: return to the title without stopping the music. End of 00_01 (`_RDED(485);_RTMN();_END_();`, after the prologue movie). [E, V-emulated] |
| RTM2 | 1 | () | 0x97FBC | c | Only sets the keep-BGM flag VA 0x174DE0C = 1 and continues. End of 00_02: `_RTM2();_RDED(485);_RTM_();_END_();`, i.e. return to the title with the BGM still playing. [E, V-emulated] |
| PGST | 17,202 | (int) | 0x9637E | m | Page marker. Story scripts (4..494) open with `_PGST(-1)`, then either `_PGST(10000+n)` right after their first `LBL_n` label (452 scripts) or `_PGST(0)` ... `LBL_000` `_PGST(10000)` (35 scripts; 4 others start with a plain page number). The rule is: `_ZZ...(LBL_n)` is immediately followed by `_PGST(10000+n)` (596 / 596 LBL markers, 568 of them in the story scripts); pages then count 1, 2, ... (step +1 except 17 places); `_PGST(11000+k)` follows choice label k. Scan mode (3.6) searches for it when loading a save. [E scan; V-all patterns; D numbering] |
| RDST (O) | 530 | (n) | 0x96DFF | c | Sets bit n (n <= 575) of the persistent "scene reached" bit set (system data VA 0x164C620+0xDC). [E VA 0xB8110] |
| RTST (O) | 527 | (n) | 0x97131 | c | Sets bit n of the per-game "scene visited" set (VA 0x164B1D0+0x192) and records n as the current scene for 533 <= n <= 557 or when a flag is set (VA 0xB8220). [E] |
| RDAS (O) | 530 | (n) | 0x98118 | c | Scene start bookkeeping (VA 0x13610, 0xB8020: checkpoint for save/"jump" lists). [E calls; H meaning] |
| RDED (O) | 550 | (n) | 0x9813B | m | If n is the current scene and visited, sets bit n of the persistent "scene completed" set (VA 0x164C620+0x258) (VA 0xB7E40). [E] |
| ATSV | 398 | ([n]) | 0x95F5D | m | Auto-save point; `ATSV(1)` comes right before 80 of the 83 story choice blocks (the other 3 have conditional `_SELR`s in between), `ATSV()` elsewhere. [H strong: name, position; E: history snapshot + save helpers] |
| WTSV (O) | 28 | () | 0x9AF36 | y | Wait for the save to finish (before chapter-title movies). [H] |
| TRPY | 1 | (n) | 0x96FC3 | c | Unlock trophy n (`_TRPY(28)` in QACALL); calls VA 0xEC080, the same routine the title code calls with 0x1C. [E call; H strong] |
| XSTP / XRSM | 2 / 2 | () | 0x965E5 / 0x96E52 | c | System state change 0xB,0x24 / resume (VA 0x5C380 / 0x5C6C0); used around one scene of CIEL13_7B. [E calls; H meaning] |
| DMMY | 71 | (anything) | - | | Disabled statement (compiler-commented): no-op. [E, D] |
| NOP_ | 1 | () | - | | no-op. [E] |

Engine-only flow names (no script uses them): `IFF_`, `IFJP`, `IFLA`, `ELS_`, `EIF_` (if/else
blocks), `WKOF`, `WKBO` (bit variable operations), `FLNT`, `FLSP`, `FLOP`, `VCLR` [E names only].

**Script-end cleanup** VA 0x7F1A0 (END_, RTM_, RTMN and the title code VA 0x6E1DD) [E]: for each
of the 20 SE channels reset the channel state and stop it (VA 0xAAB0); then, if the keep-BGM
flag VA 0x174DE0C is set, clear the flag; otherwise stop the BGM: VA 0x9AE0 puts the current
BGM channel (record VA 0xDD86A0 + 0x34 * current) into state 2 with the float 512.0 at +0x10
(a fade-out parameter [H]) and sets the current-track id VA 0x62437C = -1 (the BGM start
VA 0x98C0 compares the requested track with this id [E]; presumably so that a later `MPLY` of
the same track starts it again [H]). With the flag set, the music keeps playing into the title
menu (00_01, 00_02). Further resets
follow (VA 0x8EA0, 0xCD5F0(-1), 0x433F0(-1), 0xCBD20, 0x42F00, 0xC8E30(4)) [E calls; H
meaning].

### 4.2 Messages and choices

| cmd | count | args | VA | f | semantics |
|---|---:|---|---|---|---|
| ZM | 42,590 | (message) | 0x96615 | m | Show a message (5). Preceded by `_WKAD(F823,1);_WKST(C847,eq,0);_WKST(C859,eq,0);_WNTY(x)`. [E, D] |
| ZD | 17 | (message) | 0x96615 | m | Disabled message (3.7). Not displayed. [E, D] |
| MSAD | 678 | ([message]) | 0x9AECE | y | Add text to the current message (`_MSAD($000120)`, `_MSAD(@k@e)`, empty 45x). [D, VA 0x92B60] |
| WNTY | 58,568 | (0\|1) | 0x972F5 | c | Wait type of the next message: 1 = last message of the page (for 15,663 of the 15,759 `WNTY(1)` the message is followed by the page-end block of >= 20 `_WKST(F8xx,eq,0)` and `_PGST(p)` before the next message), 0 = within the page. [D; M] |
| CLO2 | 5 | () | 0x9A887 | y | Close the message window (with VA 0x75CD0(1)); script prologues. [E calls; H] |
| CLO3 | 12,208 | () | 0x9A8B8 | y | Close/clear the message window. [D; mangetsu "clear text"] |
| CLO4 (O) | 978 | (ms) | 0x9A8E7 | y | Close the message window with a fade of `ms`. [H] |
| CLO5 (O) | 596 | (ms) | 0x9A939 | y | Variant of CLO4 (re-opens? calls 0x42400/0x423F0). [H] |
| MSQK (O) | 30 | (ms, amp, n) | 0x962D3 | c | Shake the message window. [H] |
| FCSP (O) / FCLN (O) | 31 / 26 | ([ms]) / (x,y,_,w,h,mode) | 0x982A2 / 0x96D12 | m | Text-window special modes (full-screen text / framed narration); FCLN coordinates in the 480x272 space. [H] |
| SELR | 824 | (k, message\|name [, 1]) | 0x9936D | c | Define choice k (0-based): all 206 story choices use a message (`$042569`); the 618 in the debug scripts (TEST_SCRIPT, JUMPxx, QA menus) use a plain label shown as is (`00_00`, `next`, `Movie_Test`). [D, V-all] |
| SELD | 170 | (var, [default]) | 0x9B1DC | y | Show the choices defined by SELR, store the chosen k in `var` (`_D000`). Arg 2 (1..4 or empty) is the choice set / cursor default. [D; E: yields] |
| SELA (O) / SELS (O) | 26 / 1 | (var, _) | 0x9B1B6 / 0x9B435 | y | Choice variants: SELA = chapter-start "continue" prompt (always before `_WTSV();_TOMP(phase)`), SELS = yes/no system prompt (QACALL). [D] |

### 4.3 Pictures and layers

40 layers (0..39). Pictures are named by *image id* (`img0597`, `aki_a_30_01_n01`, `BLACK`);
see 6.4 for the resolution to archive entries. Layer changes become visible at the next
`_FADS` (transition) [D].

| cmd | count | args | VA | f | semantics |
|---|---:|---|---|---|---|
| STCH (O) | 54,494 | (layer, image, x, y, scale, alpha, z, _, anchor, [h, v, a11], [a12, a13, a14, a15, a16, a17], [src, angle]) | 0x99AB7 | c | Put a picture on `layer`; full argument table in 4.3a. Resets the layer first (VA 0x49570, 0x4A770). [E VA 0x81760 (edi = 1)] |
| SCH2 (O) | 4,675 | same as STCH, usually (layer, image, [x, y, scale, alpha, z], _, CC, ..., src) | 0x98226 | c | Same backend as STCH (VA 0x81760, edi = 0) without the layer reset: absent arguments keep the layer's values (alpha and z included). With `src` (arg 18), all per-layer fields are first copied from layer `src` and the variant digits are inherited (4.3a); src showed the same character in 4,672 of 4,674 cases, i.e. an expression change. [E; D] |
| STCC (O) | 63,261 | (layer [, 0]) | 0x97EED | c | Clear layer; -1 = all layers (564x). [D] |
| STCP (O) | 51,568 | (layer, v) | 0x99B03 | c | Per-layer parameter (v = 0 in 34,815, 1 in 11,073, 9250, 2, or a z value); appears as `STCP(l,0)` before fade-outs and `STCP(l,9250)...STCP(l,2)` around background swaps. [E VA 0x83850; H] |
| FADS (O) | 34,040 | (type, ms, rule [, x]) | 0x9B06D | y | Commit pending layer changes with a transition: type 1 = immediate (20,853, ms 0), type 0 = cross-fade over `ms` (12,240), type 2 = rule wipe with mask `rule` (947; rule n >= 1 = allpac3 entry MASK_TOP + n, i.e. RULE_nn.MZP); rules 49..52 (0x31..0x34) also play UI sound 0x19. [E VA 0x81520, 0x6B460 (rule = id("mask_top.mzp") + n); D] |
| FANX | 2,741 | (type, ms, rule [, x]) | - | | no-op on PS4 (Switch-side transition variant). [E] |
| FADE (O) | 61 | (layer, [deg, n]) | 0x97973 | m | Layer fade effect (VA 0x7F7E0). [H] |
| STZ4 (O) | 32,317 | (layer, zx0, zy0, zx1, zy1, t, e, f) | 0x99038 | c | Zoom in per mille (1000 = 100 %, negative = mirrored) about the picture's anchor, from (zx0, zy0) to (zx1, zy1) over t ms with easing e. In 32,311 statements start = end (a static zoom); 6 differ: `_STZ4(20,2943,,2643,,0,0,0)`, `_STZ4(10,1718,1718,1718,118,0,0,)`, `_STZ4(24,1050,1050,1060,1060,0,0,0)`, `_STZ4(29,1196,1196,965,965,1000,3,0)` (a timed zoom over 1 s, easing 3), `_STZ4(3,-5821,582,-582,582,0,0,0)`, `_STZ4(2,1000,1000,1000,1050,0,0,0)`; with t = 0 the end values apply at once [D; H for empty fields = unchanged and for the easing codes; V-all counts] |
| STRT (O) | 21,683 | (layer, r0, r1, 0, 0) | 0x99555 | c | Rotation in 1/100 degree (9000 = 90 deg); r0 == r1 in all 21,683 statements [D; V-all] |
| STRR (O) | 448 | (layer, r) | 0x99542 | c | Relative / continuous rotation. [H] |
| STRA (O) | 569 | (layer, n [, m]) | 0x97264 | c | [H] |
| STPV | 733 | (layer, x, y, _) | 0x983AB | c | Pivot / position. [H] |
| SOXY (O) | 208 | (layer, x0, y0, x1, y1) | 0x99885 | c | Offset x/y. [H] |
| STGS (O) | 8,110 | (layer, [a0], a1, ms, e) | 0x99065 | c | Gradual alpha change from a0 (empty = current) to a1 over ms with easing e. [D] |
| STBR (O) | 14,940 | (layer, mode [, _, 1]) | 0x990C2 | c | Blend / brightness mode 0..28 (17, 11, 23, 24, 22 most common). [H] |
| STEC (O) | 12,900 | (layer, mode [, 0xRRGGBB]) | 0x9991B | c | Colour effect (mode, optional colour; the colour string is converted with `strtol(s, NULL, 0)` and stored per layer, VA 0x104F8..0x10554) [E reads; H meaning] |
| STCF (O) | 5,179 | (layer [, 8]) | 0x9B448 | y | Layer filter / flag (always 8 when given). [H] |
| STMA (O) | 24,333 | (layer, motion [, m, n]) | 0x9969F | c | Run keyframe *motion* number `motion` (1..24,192) on the layer: the motions are compiled into eboot.bin (6.6). Ids are mostly unique per call and increase through the game. Out-of-range ids occur: 0 in 92 statements (e.g. `_STMA(28,0)` in 01_00_ARC01_7) and 101960 once (`_STMA(27,101960)`, 10_00_ARC10_7_2B); the duration and end accessors (VA 0xC2150 / 0xC2170: `dec edi; cmp edi, 0x5E7F; ja -> return 0`) give 0 for both, so they behave as an empty motion (most likely "stop / no motion") [E accessors; H effect; V-all counts: 24,349 in range, 92 zero, 1 too large over STMA+SGMA] |
| SGMA (O) | 109 | (layer, motion [, m]) | 0x99568 | c | Motion variant (ids from the same space). [E VA 0xC23B0; H] |
| SPMA (O) | 199 | (0\|1) | 0x9929C | c | Motion pause/resume. [H] |
| SNX2 (O) / SNX3 (O) | 1,678 / 845 | (layer, n) | 0x98289 / 0x9986C | c | Per-layer parameters. [H] |
| SNX4 | 8,758 | (layer, a, b) | - | | no-op on PS4; in 8,508 of 8,758 cases (a, b) equal the variant digits h/v (args 9/10) of the layer's last STCH. On PS4 those digits select a pre-made `_hh_vv` picture (4.3a); SNX4 is presumably the Switch build's run-time equivalent (blur strength) [E no-op; D; H Switch meaning] |
| STNS (O) | 196 | (layer, 11) | 0x996B8 | c | [H] |
| STTR (O) | 111 | (layer, n [, m]) | 0x99947 | c | [H] |
| SACL (O) | 450 | (layer, flag) | 0x99591 | c | Sets/clears flag 0x40 of the layer flag byte (VA 0x16500C0+0xA4+layer). [E] |
| SBAS (O) | 943 | (layer, base) | 0x99609 | c | Sets the layer's base/parent layer (i32 at VA 0x16500C0+0xA08+4*layer, default -1). [E] |
| STCL (O) | 9 | (0, layer, x0, y0, x1, y1, t, e) | 0x99AF0 | c | Layer move. [H] |
| STQK | 3 | (...) | - | | no-op on PS4. [E] |
| SPFC (O) | 487 | (layer, image, [x, y, ...], z, [0xRRGGBB]) | 0x98CD0 | c | Picture with colour fill / face; args 2..6 through the expression getter, the colour (arg 7) as `strtol(s, NULL, 0)` [E reads; H meaning] |
| SPRD (O) / SPRC | 36 / 85 | (layer, image, 0, 0, n, n) / () | 0x96A28 / 0x992B6 | c | Special picture effect set / clear. [H] |
| STCG | 39 | (image) | 0x99A4F | c | Mark a picture as **seen** without showing it: copies the name (inline, after an optional tag), looks up its picture id (VA 0x82CE0; ids >= 0xA8D become -1), maps a `_hh_vv` variant to its base picture (VA 0x957B0) and sets that id's bit in the "picture seen" bit set VA 0x164D5C0 (VA 0x95CA0, ids <= 0xC7F) - the same set the layer commit sets for every displayed picture (VA 0x7E0F1). The 39 STCGs at the end of 00_01 (after the prologue movie) include non-gallery pictures such as `img0597` (black backdrop) and `img2182` (vignette overlay): they mark everything the movie showed as seen. [E; D] |
| SQK2 (O) / SQS2 (O) | 1,236 / 532 | (-1\|layer, ...) / (n) | 0x992C7 / 0x992E0 | c | Screen/layer quake start / stop (-1 = all). [D names, mangetsu-style] |
| SHK2 (O) / SHS2 (O) | 125 / 400 | (layer, ...) / (n) | 0x9982D / 0x99673 | c | Layer shake start / stop. [H] |
| SSW2 (O) / SSS2 (O) | 200 / 398 | (layer, ...) / (n) | 0x99D4B / 0x986CF | c | Layer swing start / stop. [H] |
| SHOK | 870 | (layer, a, ms, n [, x]) | 0x98C44 | c | Shock effect. [H] |
| SHAF | 397 | () | 0x99581 | c | Stop all shake effects; used as `_SHAF();_SQS2(-1);_SHS2(-1);_SSS2(-1);` at scene starts, before choices and at scene ends. [D] |

Engine-only picture names: `STRC`, `SNOW`, `SHAK`, `SHAS`, `FCCL` [E names only].

### 4.3a STCH / SCH2 arguments [E VA 0x81760..0x82BC9; V-all counts]

Both commands parse with the object parser and call the backend VA 0x81760 (STCH with edi = 1:
the layer is reset first; SCH2 with edi = 0: no reset). "absent" = `has(i)` false (missing or
empty). Internal per-layer arrays are at VA 0x16500C0 (+offset + 2*layer for i16 fields).

| arg | name | reading | stored / effect |
|---:|---|---|---|
| 0 | layer | expr; absent = -1 | > 39 (or absent): the statement does nothing |
| 1 | picture | `str(1)`, at most 255 bytes | picture name, extended by the variant suffix (below), then upper-cased up to the first `.` and 32 bytes and looked up in the picture-name map (VA 0x78410; map built from the 32-byte name table at data VA 0x7F6800, value = catalog index = allpac2 index). Not found -> index -1 (the `BG_ERROR` fallback at VA 0x828A1 is only taken for indexes <= -2, i.e. never): the layer gets no picture [E; H: nothing is drawn] |
| 2 | x | expr | absent: unchanged; else +0x16C = trunc(x * 640 / 480) (4.5) |
| 3 | y | expr | absent: unchanged; else +0x1BC = trunc(y * 480 / 360) (4.5) |
| 4 | scale | expr | absent: unchanged; else +0x490 (per mille; 1000 in 54,484 of 54,494 STCH) |
| 5 | alpha | expr, clamped to 0..255 | STCH absent: 255; SCH2 absent: unchanged; +0x20C |
| 6 | z | expr | STCH absent: 10 * layer; SCH2 absent: unchanged; +0x2FC (draw priority: 2 for backdrops, 518..9114 for pictures and sprites) |
| 7 | (empty) | **not read** | - |
| 8 | anchor `CC` | **not read**: the PS4 always places the picture by its catalog anchor (4.5) | - |
| 9, 10 | h, v | expr; absent = 0 (or inherited, below) | variant suffix: if `h \| v != 0` the picture name gets `"_%02d_%02d" % (h % 100, v % 100)` (a value <= 0 gives `00`) appended: `_STCH(4,img1696,...,CC,11,11,)` shows `IMG1696_11_11` |
| 11 | - | expr | absent: unchanged; else +0x968 [H: meaning open] |
| 12..15 | rectangle | expr | absent: unchanged; else four i16 at +0xAF8 + 8*layer (+0, +2, +4, +6) [H: source crop x, y, w, h in picture pixels; e.g. `_STCH(11,img0114,940,218,1000,255,2178,,CC,,,,608,-219,1152,1512,120,0)`] |
| 16 | - | expr | absent: unchanged; else +0xC38 [H: time] |
| 17 | - | expr | absent: unchanged; else byte +0xC88 [H: easing] |
| 18 | src | expr; absent = -1 | 0..39: before everything else, copy all per-layer fields from layer `src` to `layer`, and default h/v to the digits of src's current picture name when that name ends in `_hh_vv` (needs length >= 7, `_` at len-6 and len-3, and the prefix `IMG` or length >= 15; the four digit bytes are not validated); explicit args 9/10 override them [E VA 0x81862..0x81935] |
| 19 | angle | expr | taken modulo 36000 (1/100 degree, VA 0x821DF..0x82215) [H: initial rotation] |

Variant pictures: 9,229 STCH/SCH2 statements select a `_hh_vv` picture (104 of them only through
digits inherited from the source layer); the catalog marks exactly the 7,288 `_hh_vv` names as variants of their
base picture [V-all]. They are pre-blurred copies: rendered side by side, `IMG1696_11_11` is
`IMG1696` with a strong blur and a canvas grown by 24 px per side
(`samples/script/xref/img1696_vs_11_11.png`); so h and v are the horizontal and vertical blur
strengths [V-rendered for this pair; D for the rest]. 5 statements name a variant that does not exist, so the engine finds no picture:
`IMG1637_28_28` (14_01A_CIEL14A_1), `IMG5007_02_02` (09_00_ARC09_3_2_1), `IMG5007_00_27`
(QA_23), `Z20_M_61_00_A00_10_00` (05_01_CIEL05_2_2B) and, if the inherited digits are as a
linear walk of the script finds them, `SIK_A_32_00_N00_02_30` (14_01B_02_CIEL14B_3B) [V-all].


### 4.4 Sound, voice, movies, waits

| cmd | count | args | VA | f | semantics |
|---|---:|---|---|---|---|
| VPLY | 16,689 | (voice_name, hex_index) | 0x968B2 | m | Play voice: entry `int(hex,16)` of voice.hed (6.2). [V-all 16,705 with VPL2] |
| VPL2 | 16 | (voice_name, hex_index) | 0x968B2 | m | Alias of VPLY (used in 00_02). [E] |
| VCWT | 75 | () | 0x96504 | m | Wait for the voice to end (VA 0x9210). [E call; D] |
| SEPL | 14,543 | (ch, loop, se_name, _, tags...) | 0x98C8F | c | Play SE `se_name` on channel 0..19 (20 channels, VA 0x7F1A0 loops 20), loop 0/1, tags `` `001 `` fade-in, `` `017 `` volume, `` `068 `` delay [H], `` `018 ``, `` `070 ``. [E VA 0x893F0; D; V-all names] |
| SEFD | 3,516 | (ch\|-1, _, _, `001 ms [, _, `068]) | 0x99859 | c | Fade out SE channel (-1 = all). [D] |
| SEVL | 1,285 | (ch, _, _, _, `001 ms, _, `017 vol [, _, `068]) | 0x98C07 | c | Change SE volume over time. [D] |
| SESP | 18 | (ch\|-1 [, _]) | 0x996E1 | c | Stop SE channel (VA 0xAAB0, the routine END_ calls per channel). [E] |
| MPLY | 1,171 | (bgm [, _, tags]) | 0x96E2C | c | Play BGM entry `bgm` of bgm.mrg with tags `` `017 `` volume (default 100), `` `001 `` fade-in (default 0), `` `049 `` (default 1), `` `069 `` (i32). [E VA 0x87250, 0x98C0] |
| MVOL | 780 | (_, _, `001 ms, _, `017 vol) | 0x978AA | c | Change BGM volume over time. [D] |
| MFAD | 724 | (_, _, `001 ms, _, `011 0) | 0x978F5 | m | Fade out BGM. [D] |
| TOMP (O) | 48 | (movie_id) | 0x96F4C | m | Play movie `movie_id` (system state 8, VA 0x5C380(2, 8, id)). The id is read with `tryint` (strtol of the leading part, stored even on failure; absent = 0). Id 7 (`phaseout.mp4`) is skipped while the word VA 0x120A6D2 is non-zero. 6.5. [E] |
| TOMW | 48 | () | 0x9AB56 | y | Wait for the movie to end. [D] |
| WTTM | 14,828 | (ms [, skippable]) | 0x9AC1A | y | Wait `ms` milliseconds; second argument 1 (14,763), 0 (57) or missing - probably "may be skipped by click". [D; E yields] |

Engine-only sound names: `MSTP` (BGM stop), `SEPN`, `SEAC`, `SEP3`, `WTTR` [E names only].

### 4.5 Coordinate system and picture placement [E + V-rendered]

- **Script space**: x 0..480, y 0..272, origin top-left, (240, 136) = screen centre [D: 13,232
  STCH x = 240, 10,587 y = 136].
- **Engine space** [E VA 0x82941..0x829CE]: STCH stores `x' = trunc(x * 640 / 480)` and
  `y' = trunc(y * 480 / 360)` (integer division by 480 and 360 via multiply-shift; both are
  x 4/3), i.e. a 640 x 362.67 space. The image catalog stores every picture's size in the same
  space: `w3 = floor(width / 3)`, `h3 = floor(height * 362 / 1080)`, and its anchor
  `(anchor_x, anchor_y)` in w3/h3 units (images document, catalog +0x2C..+0x38).
- **Frame**: the 640-wide engine space is 1/3 of the 1920 x 1080 frame (VideoOut 1920x1080).
  So at scale 1000 and zoom 1000, **one picture pixel is one frame pixel, and one script unit
  is 4 frame pixels (4 picture pixels)**. Picture sizes are the catalog's logical width/height,
  not the padded MZP canvas (images document 2.2).
- **Placement** [E VA 0x82B0C..0x82B56]: for STCH without a source layer the layer offset is set
  to `-(anchor_x, anchor_y)`: the picture's catalog anchor lands on (x, y). Zoom (STZ4, per mille,
  negative = mirrored) and scale (arg 4) act about that point [D].
- Typical uses: full-screen fills `WHITE`/`BLACK` are 1920 x 1080 at (240, 136): exactly the
  frame; the standard backdrop pictures are 2070 x 1230 (4,216 of the 4,302 `z = 2` backdrop
  STCHs at (240, 136) scale 1000), i.e. 75 px of margin per side for shakes; 4K backgrounds (3840 x 2160, e.g. IMG0114,
  IMG0143, IMG0360) are placed off-centre with zooms around 0.8..1.5 and show a part of the
  image; character sprites are anchored below the visible area (median y = 501 for size class
  A, 324 for L) so that only the upper body is on screen.

Verification by rendering [V-rendered, `scratch/script/compose_test.py`]: three script moments
were composed into 1920 x 1080 frames from the real pictures (tsuki.image_catalog renders, STCH
x/y/scale, STZ4 zoom, catalog anchor) under two hypotheses and the PNGs were viewed
(`samples/script/xref/compose_*`):

| moment | 1 picture px = 1 frame px (this section) | 1 picture px = 0.5 frame px ("8 px per unit") |
|---|---|---|
| 01_00_ARC01_1 #2157: IMG0009 (train) + SIE_L_12_03_N01 | train interior fills the frame; Ciel standing centred, framed head to waist | backdrop is a small rectangle in the lower right, Ciel tiny at the bottom, most of the frame empty |
| 03_00_ARC03_1 #1446: IMG0143 (4K bedroom) + HIS_L_20_00_N00 | room fills the frame, Hisui centred head to waist | (same failure) |
| 09_01_CIEL09_2B #5903: IMG0360 (4K tatami room) + SIE_A_39_00_N02 | close-up of Ciel's face and shoulders over the room | (same failure) |

Only the first hypothesis gives normal visual-novel frames. (A reviewer proposed 8 picture
pixels per script unit from the MZP canvas sizes of 4K pictures; the canvas includes padding
and those pictures are not the full-screen backdrops, see above.) Open: rotation and zoom
pivots for pictures placed through a source/base layer (SBAS), the size-class factors 1, 0.5,
0.25, 0.125 that VA 0x526A3 applies to parent-layer anchors, and the exact camera/projection
code [H].


---

## 5. Messages and text references [E VA 0x76040..0x76200, 0x92380; V-all]

Message arguments (`_ZM`, `_ZD`, `_MSAD`, `_SELR` arg 1) are markup strings in which `$NNNNNN`
(exactly 6 decimal digits) is replaced by line NNNNNN of the current language of
script_text.mrg: `len = off[n+1] - off[n]`; lines shorter than 3 bytes insert nothing;
otherwise `min(len, 0x301) - 2` bytes (the trailing CRLF dropped) [E]. Languages = section pairs
(0 ja, 1 en, 2 zc, 3 zt, 4 ko placeholder); see the text document. The argument ends at `)`;
the `ZM` handler's copy loop also stops at `@e` / `@n` and records which (VA 0x92473 ff.; the
same loop exists at VA 0x7D8A0..0x7D8FE) [E].

Forms in the corpus [V-all]:

| form | count | notes |
|---|---:|---|
| `$N` | 40,734 ZM + 532 MSAD + 200 SELR + 17 ZD | one line |
| `$N^$N` | 510 | two lines joined with `^` (line break) |
| `$N@k@e` | 425 | last line before a choice / scene end: wait for key, then end [D] |
| `$N@n` | 418 | continue without waiting [D] |
| `@x$N...` | 253 + | prefix modifier [H: no indent / no backlog] |
| `@cN$N` | 30 | colour N (colour table, text document) |
| `@r$N` | 5 | [H] |

43,930 references in total; every id is < 43,969 (the line count) [V-all]. Example: line 3
(the fourth message of the prologue script, a 70-character English line; `text.md` 2.3) is
shown by `_ZM01d04($000003);` at offset 0x1040 of `00_02`.

---

## 6. Cross references

### 6.1 Scene ids [E VA 0x216130; V-all]

`SCENE_IDS[k]` (i16, 549 entries, `tools/tsuki/script_tables.py`, `samples/script/scene_ids.tsv`):

| scripts | scene ids |
|---|---|
| 0 START_SCRIPT | 110 |
| 1..3 00_00, 00_01, 00_02 | 240, 260, 270 |
| 4..494 chapters 01..15 | 300..790 (consecutive) |
| 495..519 QA_01..QA_25 | 1000..1024 |
| 520..527 *_FLAG0/1 | 2000..2007 |
| 528 ARCHIVE, 529 QACALL, 530 STAFFROLL | 3000, 3100, 4000 |
| 531 TEST_SCRIPT, 532..548 JUMP00..JUMPQA | 8000, 8001..8017 |

Lookup: linear search (VA 0x792D0); unknown id -> 0xFFFF, and the loader then falls back to
section 3 = START_SCRIPT (VA 0x79355) [E].

### 6.2 Voices [V-all]

`_VPLY(name, hhhhh)`: `int(hhhhh, 16)` is the entry index in voice.hed/voice.mrg (HED4/LV/MZX,
containers.md 3 and 6). The name is the voice line name; voice.nam (NAM8) holds only its group
prefix: `name.startswith(voice_nam[index])` holds for 16,705 / 16,705 VPLY+VPL2 statements;
16,459 distinct entries are used (the 2 `DU` dummies and 3 others are not). Example:
`_VPLY(B90_00_01_0001_0,03d04)` -> voice entry 15,620 (`B90_`), ATRAC9 mono 48 kHz, 2.25 s,
spoken during line 3 (`samples/script/xref/voice_15620_B90_00_01_0001_0.wav`).

### 6.3 Sound effects and music [V-all]

- SE: `se_name` (SEPL arg 2) is looked up case-insensitively in se_nam.mrg section 2, whose
  index is the vse.hed entry: 1,314 / 1,314 distinct names found. Example `MOON_SE03028` ->
  vse 598 (RIFF ATRAC9 stereo 48 kHz, 346,920 bytes).
- BGM: `_MPLY(n)` plays bgm.hed entry n [E VA 0x98C0/0x9B40: entry = n, or n + 131 when the
  option byte at VA 0x66C578+0x1E is 1 and 1 <= n <= 131 (an alternative-BGM mode; such entries do
  not exist in this bgm.hed)]. Used ids 2..123, all < 132 entries. Example `_MPLY(2)` -> bgm 2
  (ATRAC9 stereo, 3,666,600 bytes).

### 6.4 Pictures [E name handling and map source; V-all resolution]

The STCH backend (VA 0x81760) builds the picture name (argument 1 plus the `_hh_vv` variant
suffix, 4.3a), upper-cases it up to the first `.` and 32 bytes (VA 0x78410, C-library toupper
table) and looks it up in a map built lazily (VA 0x780D0) from the 32-byte name table at data VA
0x7F6800 (NUL-padded names, CRLF, terminated by a 0xFF byte; map value = record index) [E]. That
table lists the picture names in catalog order: record i <-> image-catalog record i <-> allpac2
entry i (`tools/tsuki/image_catalog.py`, owned by the image document). The catalog record gives
the layers to draw: `name0` (base) and, for character sprites, `name1` (face) drawn over it, each
found in exactly one of allpac0 / allpac1 / allpac1_2 / allpac4.

Counts [V-all, `scratch/script/doc_checks.py`, `tests/test_script.py::test_asset_links`]:

- Picture ids as written in STCH/SCH2/SPFC/SPRD/STCG: 7,330 distinct strings, 7,329 ignoring
  case (`noe_a_01_00_a01` and `NOE_A_01_00_A01` both occur; the engine upper-cases).
- Effective names after the variant suffix (STCH/SCH2, source-layer digits followed by a
  linear walk of each script): 10,967 distinct; 10,962 have a catalog record (3,206 of them
  two-layer sprites); 5 have none (4.3a: the engine finds no picture).
- Missing layer: exactly one record has a layer that is in no archive: `NOE_L_60_03_N01`
  (catalog #12536, used once: `_SCH2(22,NOE_L_60_03_N01,,,,,,,CC,,,,,,,,,,26);` in
  13_01_01_CIEL13_5) has name0 `NOE_L_60_03_N00_B.MZP`, which none of allpac0/1/1_2/2/3/4
  contains; its face layer is allpac1_2 #1082. What the engine does [E]: the layer file search
  VA 0xC3270 returns -1, the result is cached in the catalog record (+0x20, VA 0x4B5E4..0x4B607),
  and the texture loader VA 0xEB190 returns immediately for a negative file index (`test esi,
  esi; js` at 0xEB1A1) - no error, the base is simply not loaded (its slot keeps whatever it
  held); the face layer is loaded through its own index [H: so only the face is drawn]. A port
  should treat a missing layer as "draw nothing for that layer" rather than fail.

The engine's name -> archive search (VA 0xC3270): upper-case the name, then search the NAM
tables in the order allpac4 (slot 9, id 0x50000|i), allpac3 (slot 8, 0x40000|i),
allpac1_2 (slot 6, 0x20000|i), allpac1 (slot 5, 0x10000|i) - these two only when the
full-size archives are mounted - and allpac0 (slot 4, id i); not found -> -1 [E].

Examples (rendered with tsuki.image / tsuki.image_catalog, `samples/script/xref/`):

| script id | catalog | layers | picture |
|---|---:|---|---|
| `img1685` (00_02 layer 6, behind line 3) | 1692 | allpac0 #270 IMG1685.MZP | dark Japanese room at night, shoji open on a moonlit forest |
| `aki_a_30_01_n01` (SPFC/SCH2) | 10137 | allpac1 #8540 AKI_A_30_01_N00_B + #8541 AKI_A_30_01_N01 | full-body Akiha sprite, smiling, arm raised |
| `img0597` (layer 0/1 in every scene prologue) | 604 | allpac0 #166 | black backdrop |
| `BLACK` / `WHITE` | 0 / 5 | allpac0 #0 / #5 | plain fills |
| `img1696` + h, v = 11, 11 (00_00, `_STCH(4,img1696,240,136,1000,255,1910,,CC,11,11,)`) | 6218 | allpac0 #1121 IMG1696_11_11.MZP | the window/curtain picture IMG1696 strongly blurred, 2154 x 1314 instead of 2106 x 1266 (`xref/img1696_vs_11_11.png`) |

Transition masks: `_FADS(2, ms, n)` uses the image `id("mask_top.mzp") + n` (VA 0x6B460),
i.e. allpac3 entry 85 + n = `RULE_nn.MZP` (allpac3 #85 is MASK_TOP.MZP, #86..#134 RULE_01..RULE_49);
rule 0 = no mask (the loader is not called for n <= 0) [E + D; rule ids used 0..49 except 46].

### 6.5 Movies [E table at data VA 0xDC8C70; V-all]

`_TOMP(m)`: m indexes a 134-record table of 48-byte records (u64 id = m, u64 pointer to the
path, ...): 0..3 `titlebg1..4.mp4`, 4 `prologue.mp4`, 5 `op1_arc.mp4`, 6 `op2_ciel.mp4`,
7 `phaseout.mp4`, 8..34 `phasetitle_01_00 .. phasetitle_15_01b.mp4`, 35..37 `ed01_arc`,
`ed02_ciel`, `ed03_ciel`, 38 `TM_CI.mp4`, 39..45 `credit00..06.mp4`, 46..133 the `_en`, `_zc`,
`_zt` variants (`MOVIE_FILES` in script_tables.py). Scripts use 40 ids (4..45 without 7 and 38);
all their files exist in `image/movie/` [V-all]. How the engine switches to a language variant
was not traced [H: by text language, cf. if_<lang>.png].

### 6.6 Layer motions (STMA/SGMA) [E VA 0xC2150, 0xC2170, 0xC2190, 0xBE4C0; V-all]

Motions are keyframe animations compiled into eboot.bin; a port must extract them:

| table | VA | type | content |
|---|---|---|---|
| duration | 0x274538 (text) | i64[24192] | motion length in ms |
| end index | 0x2A3938 (text) | i64[24192] | exclusive end keyframe index; motion k uses keyframes [end[k-2], end[k-1]) |
| keyframes | 0x873FF0 (data) | 72-byte records, 71,905 used (max index 0x118E0) | see below |

Keyframe record: +0x00 u64 time (ms from motion start); +0x08..+0x14 seven i16 fields;
+0x18 i64 field; +0x20..+0x44 nineteen i16 fields. Field value -32767 (and -100001 for the
i64) = "not animated". Motion 1 (applied by `_STMA(3,1)` in 00_00, right after
`_STCH(3,img2183,240,136,1000,255,2178,,CC,,,)`): keyframe 0 at t=0 has fields 1, 2 = 0, 0,
field 5 = 255 and field 27 = 2178 (the alpha and z of that STCH); keyframe 1 at t=2800 has
field 5 = 0 (fade to transparent) and everything else unset [V; field names H].
24,135 of 24,192 motions start at t=0 and end at their duration; 57 have delayed starts.
Keyframes per motion: 2 (15,468), 3 (4,738), 4 (2,020), ... up to 20+. Field meanings beyond
time/x/y/alpha/z (and scale fields matching STZ4) are open [H]. Reader:
`tsuki.script_tables.read_motions(eboot)`. Motion ids outside 1..24,192 (0 in 92 statements,
101960 once) read duration 0 and end index 0 through the accessors (4.3, STMA) [E].

### 6.7 Assets by command

| command | argument | resolves to |
|---|---|---|
| VPLY/VPL2 | arg 1 (hex) | voice.hed entry |
| SEPL | arg 2 (name) | se_nam.mrg s2 -> vse.hed entry |
| MPLY | arg 0 | bgm.hed entry |
| TOMP | arg 0 | movie table -> image/movie/*.mp4 |
| STCH/SCH2 | arg 1 + `_hh_vv` from args 9/10 (or the source layer, arg 18) | picture-name map / image catalog -> allpac0/1/1_2/4 layers (4.3a, 6.4) |
| SPFC/SPRD | arg 1 | image catalog -> allpac0/1/1_2/4 layers |
| STCG | arg 0 | same; sets the "picture seen" bit |
| FADS (type 2) | arg 2 | allpac3 RULE_nn |
| STMA/SGMA | arg 1 | eboot motion table |
| ZM/ZD/MSAD/SELR | `$NNNNNN` | script_text.mrg line |
| JUMP | arg 0 | scene id -> allscr script |

---

## 7. Entry point and scene graph

**New Game** [E VA 0x690EE..0x69490, title menu action 2 of the jump table at VA 0x209DC0]:
the start scene is taken from VA 0x2A1B0 if that returns >= 0 (a pending start scene); otherwise
it is **scene 110 (START_SCRIPT)** when bit 485 of the persistent "scene reached" set is set,
and **scene 260 (00_01)** when it is not (VA 0x6946A: `0xB81A0(0x1E5) ? 0x6E : 0x104`). The
scene is loaded by VA 0x60500 -> 0x79320.

- `00_01` (scene 260, read-tracking id 485): sets RDST/RTST/RDAS(485), black screen, plays
  movie 4 (`prologue.mp4`, or its language variant), marks the 39 pictures shown in the movie
  as seen with STCG, `RDED(485)`, `RTMN` (back to the title, BGM kept). Bit 485 is set at its
  start, so the next New Game starts at 110. [E + V]
- `START_SCRIPT` (110): initialises about 70 variables (F822 = 1, F823, F847..F869, F1104..F1109,
  G008, F024..F026, F500, F050..F074 = 0), shows `WHITE`, `_JUMP(240,START)` -> `00_00`
  (hospital: its first line is id 107, 43 characters, starts `I wake`). `00_00` ends with `_RDED(486);_JUMP(300);` -> `01_00_ARC01_1`.
- `00_02` (scene 270) is the scripted (non-movie) prologue: 96 references to lines 0..106 with voices
  `B90_00_01_*`, pictures (img1685 dark room, ...) and the opening-credit movies 39..45
  (`credit00..06`) between pages. "The familiar scent of straw mats and paper screens hangs in
  the air." is its line 3 on page 1. It ends `_RTM2();_RDED(485);_RTM_();_END_();` (title, BGM
  kept). No script jumps to 270 except the debug menu JUMP00, and no eboot code path selecting
  270 was found. The prologue movie is the pre-rendered version of this scene: the frame of
  `prologue_en.mp4` at 25 s shows the dark tatami room with open shoji onto a moonlit forest and
  the first three English lines of 00_02 burned in (`samples/script/xref/prologue_en_25s.png`)
  [V-rendered; both reviewers saw the same frames].

**Graph** (`samples/script/scene_graph.json`, `.dot`): 1,741 JUMP edges [V-all: all targets
exist]; from START_SCRIPT 526 of 549 scripts are reachable through JUMPs. Not reachable:
`00_01`, `00_02` (title code / debug), `ARCHIVE` (3000, selected by the title code at
VA 0x68D50), `STAFFROLL` (4000), `TEST_SCRIPT` and `JUMP00..JUMPQA` (debug menus), and
`02_00_ARC02_3_2` (only reachable from JUMP02) [V]. Inside scripts: 1,194 IF__, 817 GOTS
targets, 436 platform branches, 101 GOTO, 3 IFSC/IFUR - all labels exist [V-all]. Choice blocks
look like:

```
_ATSV(1);_SELR(0,$042569);_SELR(1,$042570);_SELR(2,$042571);_SELD(_D000,1);
_GOTS(_D000,SEL0_0,SEL0_1,SEL0_2);
_ZZ02201(SEL0_0);_PGST(11000);_JUMP(324,LBL_000);
_ZZ02301(SEL0_1);_PGST(11001);_JUMP(302,LBL_001);
_ZZ02401(SEL0_2);_PGST(11002);_CLO3();_JUMP(325,LBL_001);
```

**Scene skeleton** (typical story script) [D]:

```
_ZZbbb01();                       empty marker
_ZZbbb01(START);_PGST(-1);        (35 scripts continue with _PGST(0); first)
_ZZbbb01(LBL_k);_PGST(10000+k);_WKST(G010,eq,1);...;_RDST(n);_RTST(n);_SHAF();...;_RDAS(n);
_IF__(F026,ne,0,LV00000);_STCC(-1);_FADS(1,0,0);_WKST(F026,eq,0);  <24 x page-reset WKST>
_ZYbbb01(LV00000);  ... pages: { messages: _WKAD(F823,1);_WKST(C847,eq,0);_WKST(C859,eq,0);
                                 _WNTY(0|1);[_VPLY(...);]_ZM...($n);   page end: 24 x WKST; _PGST(p) }
... _RDED(n);_JUMP(next[,label]);_RTM_();_END_();
```

---

## 8. Comparison with mangetsu `doc/script_commands.md` (Switch)

| mangetsu | PS4 finding |
|---|---|
| `_VPLY(filename, ??)` play audio | voice; `??` = hex voice.hed index (16,705/16,705) - confirmed and completed |
| `_WKST(F858,eq,0)` "some sort of wait?" | **wrong**: variable assignment `var = value` |
| `_STCC(int)` ?? | clear layer n (-1 = all) |
| `_WNTY([0:1])` 1 at page end | confirmed (the page-end block follows 15,663 of 15,759 WNTY(1)) |
| `_STCH(...)` image load | confirmed: layer picture with position/scale/alpha/z, blur-variant digits, source layer and angle; the `CC` anchor argument is not read on PS4 (4.3a) |
| `_STCP(uint, uint)` ?? | per-layer parameter; still open |
| `_WKAD(F823,1)` flag modification? | `var += n` |
| `_FADS(1,0,0)` fade? | commit layer changes with transition (type, ms, rule) |
| `_STZ4` ?? | zoom (x0,y0,x1,y1 per mille) |
| `_STMA(23,0)` ?? | run eboot keyframe motion n on a layer |
| `_STRT` ? | rotation in 1/100 degree |
| `_PGST(316)` "move relative page number?" | absolute page marker, used to resume saves (scan mode) |
| `_STBR` ? | blend/brightness mode (H) |
| `_SEPL(0,0,WOH_SE12027,,`017:100)` ? | play SE: channel, loop, name, keyword params |
| `_ZMxxxxx($043578)` text from memory | `$` = script_text line id; hex = 3-digit block + 2-digit sub id |
| `_ZZ...(SEL12_01_...)` load tag? | label / read-tracking marker; label table in section 1 |
| `_ZY...(LV01b00)` ? | local label marker |
| frequency list | PS4 counts differ slightly (Switch-only `STCP)21,0)` typo is gone; PS4 adds `END_` 519, `SPFC` 487, `DMMY` 71, `ZD` 17, `NOP_` 1 and many more SACL/SHS2/SSS2/SQS2/IFNX/IFP4/SOXY/STTR); full PS4 counts in 4 and `samples/script/command_stats.json` |

---

## 9. Reference implementation

`tools/tsuki/script.py`:

- `AllScr([path])`: names, `raw(k)` (decoded bytes), `commands(k)`, `labels(k)`, `index(name|#scene)`.
- `parse_script(bytes) -> list[Command]`: engine-faithful tokenizer; `Command.key/name/args/
  block/sub/trailer/handled/text_ids`, `tagged(tag)`; argument readers: `int_arg(i, bits)` and
  `present(i)` (inline readers, 3.3), `obj_args`, `obj_has(i)`, `expr_arg(i)` (object parser,
  3.3a), and `value(i)` = the reader the command's PS4 handler uses (`OBJ_COMMANDS`).
- Free functions: `inline_int(buf, p0, index, bits)`, `inline_present`, `engine_int(str, bits)`,
  `obj_split(buf, sep)`, `obj_expr(text)` (raises `ExprUndefined` where the engine has no
  defined value), `obj_strict_int`, `obj_partial_int`, `marker_hex`, `parse_labels(bytes)`,
  `text_refs(str)`.
- Pictures: `picture_key(cmd, src_name)` (variant suffix rule, 4.3a), `LayerTracker` (current
  picture per layer in statement order), `AssetResolver`: `image(id)` (catalog layers, `missing`
  layer files), `voice(cmd)`, `se(name)`, `bgm(n)`, `movie(n)`, `scene(id)`;
  `command_assets(cmd, res, tracker)`.
- `script_edges(k, cmds)`, `scene_graph(scr)`, `render_script(...)` (readable dump).
- CLI: `python -m tsuki.script list | dump <k|name|#scene> [--lang en|ja|zc|zt|ko] [--raw] |
  stats | graph [--dot]`. Output is always UTF-8 (stdout is reconfigured, so redirection works
  on a cp932 Windows console).

`tools/tsuki/script_tables.py`: `SCENE_IDS`, `MOVIE_FILES`, `ENGINE_MAIN` (name -> handler VA,
frame behaviour; 110 names + `J*` + `Z*`), `ENGINE_ALIASES`, `OBJ_COMMANDS`, `KEEP_BGM_FLAG_VA`,
`scene_index`, readers `read_scene_ids`, `read_movie_files`, `read_motions` that re-extract the
tables from eboot.bin.

`tools/tsuki/script_emu.py` (development aid, needs `unicorn` + `capstone`): `EngineEmu(eboot)`
runs the eboot's own `inline_int`/`inline_present`/`obj_split`/`obj_expr`/`obj_strict_int`,
the marker id decoding and the name dispatch under unicorn, with Python stubs for the libc
imports they call (strtol, strpbrk, strchr, memset, memmove, operator new/delete). CLI:
`python -m tsuki.script_emu expr -- "258-18" "10-3-2"`. (On Windows, unicorn's first `mem_map`
raises a first-chance access violation that it handles itself; a Python faulthandler prints it.)

Samples (`samples/script/`): readable dumps `000_START_SCRIPT.en.txt`, `001_00_00.en.txt`,
`002_00_01.en.txt`, `003_00_02.{en,ja}.txt`, `004_01_00_ARC01_1.en.txt`, `532_JUMP00.en.txt`
(WKST runs folded; text, voice/SE/BGM/movie/picture references resolved, `_hh_vv` variants
included); `raw/` decoded scripts and label tables of the same scenes; `scene_ids.tsv`,
`scene_graph.{json,dot}`, `command_stats.json` (count, argument counts, handler VA, frame
behaviour, argument reader per name); `xref/` rendered pictures, the blur-variant comparison
`img1696_vs_11_11.png`, the coordinate experiment `compose_*_{H4,H8}.png`, the prologue movie
frame `prologue_en_25s.png` and one voice line.

## 10. Evidence (commands run)

- `scratch/script/extract.py`: all 549 scripts and label tables decoded; `stats1/2/3.py`:
  byte set, tokenization, per-argument statistics (98 names, 1,083,168 statements, 1 statement
  without `(`).
- `scratch/script/fulldis.py` (capstone linear sweep of the text segment, 1,160,978
  instructions), `ebdisasm.py` (annotated ranges): loader 0x79170/0x79320, label lookup 0x794D0,
  interpreter 0x95E60, second dispatcher 0x9B760, argument readers 0x76690/0x76780/0x76860,
  object parser 0xD690/0xD8C0/0xD910/0xDA50/0xDE30/0xE310/0xE3C0, text expansion 0x76040,
  MPLY 0x87250, BGM 0x98C0/0x9AE0/0x9B40, script-end cleanup 0x7F1A0, END_/RTM_/RTMN/RTM2
  0x9AB42/0x97174/0x9643E/0x97FBC, markers 0x96615..0x96750, FADS 0x81520, rule 0x6B460, STCH
  backend 0x81760..0x82BC9, picture-name map 0x78410/0x780D0, image lookup 0xC3270, texture
  load 0xEB190, STCG 0x99A4F/0x95CA0, STEC/SPFC colours 0x104F8/0x98E81, STMA
  0xC0330/0xBE4C0/0xC2150..0xC2190, RDST/RTST/RDED 0xB8110/0xB8220/0xB7E40, New Game
  0x690EE..0x6949B, slots 0xD1F40. libc imports resolved with the engine document's NID table
  (`python -m eboot.imports`): 0x1735E0 strtol, 0x1735D0 strpbrk, 0x1735C0 strchr, 0x1736E0
  strcat, 0x1734F0 memset.
- `scratch/script/dispatch_emu.py`: unicorn evaluation of the name dispatch at 848 key
  breakpoints x 26 letters (110 names + J/Z) and brute force over 1,316,978 names: identical
  results; RTM2 (0x97FBC), VCLR (0x97277) and RTMN (0x9643E) corrected. `argreaders.py`:
  reader calls per handler -> `OBJ_COMMANDS` (52 names).
- `scratch/script/vreaders.py` (unicorn, the eboot's own code vs tsuki.script): 35,474 corpus
  expression texts, 8,608 corpus splits, 35,776 + 17,888 corpus inline reads, 2,490 + 3,000 +
  18,000 + 9,000 + 2,985 fuzzed expressions / strict ints / inline reads / presence tests /
  splits, 500 fuzzed marker ids: **0 mismatches**.
- `scratch/script/doc_checks.py` (`doc_checks.txt`): PGST openings and LBL rule (596/596, 568 in story scripts), STZ4
  start != end (6), STRT r0 != r1 (0), STMA ids (24,349 / 92 zero / 1 too large), WKST copies
  (21 in 8 forms), `()` statements (14,305), decimal arguments (28 in 9 scripts), picture ids
  (7,330 / 7,329 written, 10,967 effective, 5 unresolved, 1 missing layer).
- `scratch/script/compose_test.py`: three frames composed under the two coordinate hypotheses
  and viewed (4.5).
- `tools/tests/test_script.py`: 18 tests (8 synthetic always run; 10 need the game data /
  eboot.bin and skip cleanly without it); about 25..50 s with the game data, also with
  `TSUKI_NO_NUMBA=1`; includes the unicorn comparison of readers, splits, markers and the
  dispatch of all 112 `ENGINE_MAIN` entries and the 6 no-ops.
- `scratch/script/check_graph.py`: reachability (526/549 from START_SCRIPT).
- `scratch/script/xref_images.py`, `xref_audio.py`: end-to-end resolutions; pictures viewed
  (img1685 = dark room with shoji and moonlit forest; aki_a_30_01_n01 = full Akiha sprite
  composed from base + face; img0597 black; IMG1696 vs IMG1696_11_11 = sharp vs blurred),
  voice 15,620 decoded by ffmpeg (ATRAC9 mono, 2.26 s, speech-like 100 ms RMS envelope
  0 .. 8,358), 30 SE and 1 BGM entries are RIFF ATRAC9, the 40 movie ids used by `_TOMP` all
  name existing files; `prologue_en.mp4` frame at 25 s viewed.
- CLI: `dump 493 --lang en`, `dump 531`, `dump 207 --lang zc|zt`, `dump 3 --lang ja` redirected
  to files: all exit 0 and are valid UTF-8 (9,145 / 375 lines ...).

## 11. Open questions / next steps

1. Exact semantics of the layer parameters marked [H] (STCP, STBR, STEC, STCF, SNX2/3, STNS,
   STRA, STTR, STPV, SOXY, SPFC, SPRD, STCH args 11..17): read their backends (VA 0x83850,
   0x830D0, 0x10390, 0x83E50, 0x10610, 0x106E0, 0x87100, 0x86EE0, ...) and the layer structs at
   VA 0x16500C0 (script side) / 0xDDB5E8 and 0xDE2650 (render side, 0x2C8 bytes per layer;
   setters VA 0x49F60..0x4A040 copy the script side at commit, VA 0x7E107..0x7E1DD).
2. Field meanings of the motion keyframes (6.6) beyond time (alpha/z are likely, the rest open);
   how a motion combines with the layer's STCH values (absolute vs relative), easing; whether
   motion id 0 explicitly stops a running motion.
3. Wait semantics: what exactly WTTM's second argument, WNTY, CLO4/CLO5 and the `@k`, `@e`,
   `@n`, `@x`, `@r` modifiers wait for (text document + handler 0x92380).
4. Variable storage and persistence (which F/G variables live in the save, which in system data);
   meaning of C847/C859 and F847..F869.
5. Movie language variant selection (ids 46..133); what the word VA 0x120A6D2 (TOMP id 7 skip,
   END_/RTM_ call VA 0x7E620 when set) is.
6. Keyword tags 011, 018, 049, 068, 069, 070.
7. Choice UI details (SELD second argument; SELA/SELS), the `@x` prefix.
8. The second dispatcher (VA 0x9B760): confirm it is only an asset prefetcher.
9. What VA 0x2A1B0 returns (a pending start scene: chapter select / "continue"?), and the other
   title-menu actions that set a scene (ARCHIVE 3000, ids 9000+n for actions 10..31, 180, 190,
   1800 which are not scene ids and fall back to START_SCRIPT).
10. The image in allscr section 2 (128x96, plain white) is not used by the scripts; its purpose
    is unknown.
11. Rendering details beyond 4.5: the projection code, pivots for SBAS parent layers and the
    size-class factors at VA 0x526A3; the visible effect of a missing base layer (6.4) and of a
    picture index of -1.
12. The BGM fade-out parameter 512.0 written by VA 0x9AE0 (units) and the exact effect of the
    "same track" test in the BGM start VA 0x98C0.

## 12. Review response (repair round)

Independent reviewers checked the first version. Changes, with the proof used:

| reviewer finding | resolution |
|---|---|
| RTM2 is handled, not a no-op (keep-BGM flag) | **Confirmed and fixed** (4.1, 3.6, `ENGINE_MAIN`, tests). Emulated dispatch reaches 0x97FBC (`mov byte [0x174DE0C], 1`); the flag is read only by the cleanup VA 0x7F1A0, which then skips the BGM stop VA 0x9AE0. |
| RTMN is not a pure alias of RTM_ | **Confirmed and fixed**: RTMN = set the flag at 0x9643E, then the RTM_ code; `ENGINE_ALIASES` no longer lists it. |
| Object parser not specified; decimal claim wrong | **Confirmed and fixed**: full split / tokenizer / evaluator spec (3.3a), per-command reader list (3.2, `OBJ_COMMANDS`), `obj_expr`/`obj_split`/`value()` in the library, verified against the engine on all 35,474 corpus argument texts. 28 decimals (not 8), read as their integer part by the object parser. |
| Missing layer NOE_L_60_03_N01 | **Confirmed**: documented with the engine behaviour (6.4); `AssetRef.missing`; the test asserts exactly this one. |
| Picture pixels vs 480x272 units; reviewer suggested 8 px per unit | **Gap confirmed, the 8 px proposal refuted**: the engine converts x, y by 4/3 into the 640-wide catalog space (E), so 1 picture px = 1 frame px and 4 per script unit; three composed frames look right only under that rule (4.5). The 4080x2286 sizes were MZP canvases of 4K pictures, which are not the full-screen backdrops (those are 2070x1230 logical). |
| VCLR accepted but missing | **Confirmed and fixed** (110 names); the evaluator was replaced by emulation + brute force. |
| Marker read-table update order; lower-case hex | **Confirmed and fixed** (3.7, `marker_hex`). |
| `()` index 0 is 0, not -1 | **Confirmed and fixed** (`int_arg` now runs the engine's skip loop on the statement bytes; emulation-verified). |
| PGST opening rule | **Confirmed and fixed**: every `LBL_n` marker is immediately followed by `_PGST(10000+n)`: 596/596 in all scripts, of which 568 in the story scripts 4..494 (the reviewer's count). |
| STZ4 start/end "always equal" | **Confirmed and fixed**: 6 exceptions listed; documented as an interpolated zoom. |
| STMA ids 0 / 101960 | **Confirmed and documented** (4.3, 6.6). |
| GOTO continues via 0x9B0B0 | **Confirmed and fixed**: 0x9AB74 (label from name + 5). |
| WKST copies: 21, not 8 | **Confirmed and fixed** (8 distinct forms, one copies G008). |
| STCG = gallery unlock | **Confirmed and fixed**: "mark picture as seen" (same bit set as displaying it). |
| CLI crashes with cp932 | **Confirmed and fixed**: stdout reconfigured to UTF-8. |
| 7,329 vs 7,330 ids; test time without numba | **Clarified**: 7,330 strings / 7,329 ignoring case (both asserted); timings in 10. |
