# Script-VM variables: namespaces, operands and read / write semantics

Scope: how the script VM of TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999, app 01.01)
stores its variables and how every command that names a variable decodes its operands and
reads or writes memory. This covers the three variable arrays, the operand forms (`F<n>`,
`G<n>`, `C<n>`, `_D<n>`, `Z<n>`, literals, tags, out-of-range indices), the first-character
dispatch of every reader (`_WKST`, `_WKAD`, `_WKOR`, `_WKOF`, `_WKBO`, `_IF__`, `_GOTS`,
`_SELR`, `_SELD`, `_SELA`, `_SELS`, the choice result stores, the look-ahead interpreter), the
engine flags that change what these commands do (scan mode, the if-block skip counter, the
History_Snapshot gate, F1118 for `_SELA`), the per-message variables addressed through F823,
which F values a history record and a save slot keep, and a census of operand forms over all
549 scripts. It also applies the verified findings of the engine re-check (section 11) and lists
corrections to other documents. Section 15 lists what the review of the first version found and
how it was fixed.

Python reference: `tools/tsuki/vm_vars.py` (operand parser and read / write model, the code a
port needs) and `tools/tsuki/misc_vm_variables_errata.py` (unicorn harness that runs the
engine's own handlers, fuzzer, sweeps, census, errata checks). Tests:
`tools/tests/test_vm_vars.py`, `tools/tests/test_misc_vm_variables_errata.py`. Samples:
`samples/misc_vm_variables_errata/` (`operand_table.md/.json`, `first_byte_sweep.md/.json`,
`operand_census.json`, `choice_commands.md/.json`). Scratch tools and logs: `scratch/vm_vars/`,
`scratch/misc_vm_variables_errata/`. `docs/formats/misc_vm_variables_errata.md` is a short
pointer to this document.

Confidence labels:

- **[EMU-n]**: the engine's own code was run under unicorn on n cases (statements, bytes or
  values) and compared with `tsuki.vm_vars`: every 16-bit read and write in the variable window
  VA 0x1620000..0x1660000 in program order, the label passed to the label lookup, the argument
  buffer, the choice globals (kind, index, id, mode, line count and values), whether ScriptStep
  yielded, whether History_Snapshot passed its gate, whether the choice UI was set up and the
  argument of `_SELD`'s flowchart call. 0 differences unless stated.
- **[E]**: read from the disassembly (text VA; file offset = VA + 0x2280).
- **[V-all]**: counted on all 549 decoded scripts.
- **[D]**: observed in the script data only.
- **[H]**: hypothesis.

---

## 1. Overview: what a port must implement

```
var_F  u16[1200]  VA 0x164B1D0   F<n>; C<n> = F[F823 + n] (WKST/WKAD targets only); _<x>ddd = F[n]
var_Z  u16[1400]  VA 0x164BB30   no script operand reaches it (only aliasing, the choice store
                                 kind 'Z' and the unused _EIF_ evaluator)
var_G  u16[2000]  VA 0x164C620   G<n>; _<x>ddd with n in 600..700 = G[n - 600]
```

1. Every operand is decoded from its **first byte** (after an optional 5-byte backtick tag);
   the index is the inline-reader value of the whole argument (script.md 3.3). Which first
   bytes select a variable depends on the position (section 3.2). Everything else is a
   literal in a value position and "do nothing" in a target position. [EMU-9108]
2. `C<n>` is only a **write target** of `_WKST` / `_WKAD`: address `F[F823 + n]`. F823 is the
   number of the current message on the page, so `C847` / `C859` are per-message slots
   F847..F857 / F860..F869 (F823 = 0..10 / 1..10 in the data). In every value position `C847`
   is the literal 847. [EMU, V-all]
3. `Z<n>` is never a variable in a script command: as a target nothing happens, as a value it
   is the literal n. Only `_EIF_` (unused) reads `Z[n]`. [EMU]
4. There is **no bounds check**: the address is `base + 2*n` with n a signed 16-bit value, so
   `F1200` is Z[0] and `F-1` is the word before var_F. The retail scripts stay in range
   (F <= 1123, G <= 70). A port can reject out-of-range indices. [EMU, V-all]
5. `_IF__(a,op,b,label)` **jumps when the condition is false** (structured if; the label ends
   the block). [EMU]
6. `_SELD(_D000,id)` records the target F[0]; the choice UI later writes the chosen line's
   `_SELR` argument 0 to F[0]; `_GOTS(_D000,L0,L1,...)` jumps to label number F[0]. `_SELS`
   (two buttons) stores the button index 0 / 1. `_SELA` is the "save your progress?" prompt:
   it opens only when **F1118 = 0**, always sets F1118 = 0 and stores no result. In the retail
   scripts it opens at the 26 chapter starts when the chapter is reached from the previous one.
   It stays shut when the chapter is entered from the archive, and when the game resumes from a
   save made before it (every slot store sets F1118 = 1, 2.1). [EMU, V-all, E]
7. A history record (and a save slot, which holds one) keeps **356 of the 1200 F values**, in
   three storage classes (u16, i8, bit). Every F index the scripts use is among them; G is
   not in the record (G[0..499] are in the system data). [EMU-28800, V-all]
8. In **scan mode** (byte 0x174DE48, the re-execution after a slot load) `_SELD`, `_SELS` and
   `_SELA` change no choice state and open nothing (`_SELA` still clears F1118), `_GOTS`
   yields, `_SELR` still registers its line and does C847 += 1, and the history snapshot of
   `_SELR` is not taken. `_WKST` / `_WKAD` / `_WKOR` / `_WKOF` / `_WKBO` and `_IF__` behave as
   outside scan mode. The if-block skip counter (always 0 in retail) only stops `_IF__` and
   `_GOTS` (4.3, 4.4). [EMU-61000 fuzz cases with these flags]

Commands that name a variable are about half of all commands (535,118 of 1,036,358, almost
all `_WKST`); commands with a C operand are 88,546 (8.5 %), the part a port built from the
earlier documents mishandled. [V-all]

---

## 2. Storage

### 2.1 The arrays and their neighbours [E, EMU]

| VA | size | content |
|---|---|---|
| 0x164B1C0 | u16 | written 0xFFFF by the slot loader (0xA3155); not a variable |
| 0x164B1D0 | u16[1200] (0x960 bytes) | `var_F` |
| 0x164BB30 | u16[1400] (0xAF0 bytes) | `var_Z` |
| 0x164C620 | u16[2000] (0xFA0 bytes) | `var_G` |
| 0x164D5C0 | 0x20C bytes | saved in the system data at +0x52C (engine.md 7.3; [H] unlock bit sets) |
| 0x164D7D0 / 0x164E130 / 0x164EC20 | 0x960 / 0xAF0 / 0xFA0 | Script_Prefetch's copies of F / Z / G (5.4 of engine.md) |
| 0x16543C0 | u8[0x40] | argument buffer: the handlers copy the operand being decoded here |
| 0x165420C | u16 | block flag (`_ZY**`, `_IFJP`, `_ELS_`, `_EIF_`) |
| 0x165420E | u16 | if-block skip counter (`_IFF_` / `_ELS_` / `_EIF_`); 0 throughout retail play |
| 0x16541F4 | u8 | "message continues" byte (textbox.md 3.1, set by `_ZM` mode 1; adv_state.md 2.2); non-zero blocks History_Snapshot (4.6) |
| 0x174DE48 | u8 | scan mode (Script_Scan 0xA4060, slot load) |
| 0x1750630 | u16[] | choice values (`_SELR` argument 0 per line; `_SELS` / `_SELA`: {0, 1}) |
| 0x1750646 | u16 | number of `_SELR` lines registered |
| 0x1750648 / 0x175064A | u8 / u16 | pending choice target: kind ('F' / 'G'), index |
| 0x175064C | u8[] | per-line 'locked' flag (`_SELR` argument 2 == 1) |
| 0x1750698 | i16 | choice id (`_SELD` argument 1, -1 when absent) |
| 0x17506A2 | u16 | choice cursor (index of the chosen line when the result is stored) |
| 0x17506B0 | u32 | choice mode: 1 `_SELD` list, 2 `_SELS` two buttons, 3 `_SELA` save prompt |
| 0x164FBC0 | u8[] | "choice taken" bits per choice id (list store, 4.5; system_flow.md 5.1) |

All three arrays are bss (zero at boot). The arrays are contiguous: F[1200 + i] = Z[i] and
F[2600 + i] = Z[1400 + i] = G[i].

F values the engine itself reads or writes around these commands [E, EMU, V-all]:

| F | role | who writes it |
|---|---|---|
| F1118 | `_SELA` gate: the save prompt opens only when F1118 = 0 (4.5) | the scripts: `_WKST(F1118,eq,1)` 510 times in the scene-start block of 492 scripts, `_WKST(F1118,eq,0)` 26 times at chapter ends before `_JUMP`; `_SELA` sets it to 0; **every slot store sets its bit in the stored record** (`0x76500(rec + 8, 1118, 1)` at 0x73837 in Slot_Store, and at 0x7231F / 0x72841 in the two other store paths; adv_state.md 7.2), so a loaded game has F1118 = 1. Kept as a bit in history records |
| F929 | History_Snapshot gate: non-zero = no snapshot (4.6) | engine (not by the scripts); history bit |
| F1102 | History_Snapshot gate for kinds other than 0 / 11 (4.6); also the text layout flag 0x80 (textbox.md) | engine; not kept by history records |
| F823, F847..F869 | per-message counters (2.3) | scripts, `_SELR`, `_ZM`, HUD |
| F0 | choice result (`_D000`) | choice UI (4.5) |
| F110, F265, F266 | flowchart marks of `_SELD` choice points 57 / 67 / 72 / 73 (4.5) | `_SELD` via 0xB8620 |
| F799 | "last message of the page" (1) / not last (0): end mode of `_ZM` / `_MSAD`, the kind-0 re-display, the auto wait (config +0x9E instead of +0x2E) and the backlog's page grouping (its copy in each history record) read it. `_SELD` writes it once per pending `_SELR` line: 1 for the last line, 0 for the others, and leaves 1 (section 16) [EMU] | `_WNTY(n)` (0x53600, F799 = n != 0), `_SELD` (0x9B281); kept as i8 in history records |

A port must apply the F1118 rule of Slot_Store (set F1118 = 1 in the record it stores). A save
made from the prompt's own save screen stores the page-start record from before the `_SELA`, so
after loading it the game runs into the same `_SELA` again; F1118 = 1 keeps the prompt from
reopening [E for the mechanism, H for the purpose].

### 2.2 Index arithmetic [E, EMU]

```
F operand:  addr = 0x164B1D0 + 2 * s16(n)           # n = inline value of the argument (i16 wrap)
G operand:  addr = 0x164C620 + 2 * s16(n)
C target:   addr = 0x164B1D0 + 2 * (s16(F[823]) + s16(n))      # 64-bit sum, no clamp
```

Examples (emulated, `samples/misc_vm_variables_errata/operand_table.md`): `F1200` -> Z[0],
`F2600` -> G[0], `F-3` -> 0x164B1CA, `F32768` -> 0x163B1D0 (n wraps to -32768), `F65535` ->
0x164B1CE (n = -1), `F70000` -> G[1864] (70000 mod 65536 = 4464), `G2000` -> 0x164D5C0, `G-1`
-> Z[1399].

### 2.3 Per-message variables (F823 and the C slots) [E, V-all, EMU]

F823 is the message counter of the current page. The scripts manage it (16,487
`_WKST(F823,eq,0)` page resets, 42,590 `_WKAD(F823,1)`, one per message) [V-all]; typical
sequences:

```
page start:   _WKST(F823,eq,0); _WKST(F847,eq,0) ... _WKST(F869,eq,0); _WKST(F822,eq,1); _WNTY(0)
each message: _WKAD(F823,1); _WKST(C847,eq,0); _WKST(C859,eq,0); _WNTY(x); _ZM<bbb><ll>($id)
after some:   _WKAD(C847,1)    (3,075: empty line after the message)
              _WKST(C847,eq,63) (291: next message continues on the same line, textbox.md 5.7)
```

so during message k (F823 = k, 1..10 in the data) `C847` = F[847 + k] and `C859` = F[859 + k].
Linear scan of the scripts: C847 is used with F823 = 0..10 (0: 346 times, all `_WKAD(C847,1)`
before the first message), C859 with F823 = 1..10 [V-all]. The engine treats the same F range as
per-message arrays indexed by F823 [E]:

| family | written by | read by |
|---|---|---|
| F[799 + k], F[810 + k] | `_ZM` handler 0x92380 (VA 0x92626 / 0x92631: high / low byte of the 16-bit value it also stores at 0x86AA2C; k = min(F823, 11); only when F822 == 1) | HUD 0x53CF0 (which also writes F[811 + k]) |
| F[824 + k], F[835 + k] | 0x92380 (VA 0x92640 / 0x9264F: copies of F31 / F32, the text scale words; textbox.md 5.8 "C824 / C835") | HUD 0x53CF0, 0x7C340 |
| F[847 + k] (`C847`) | scripts; `_SELR` (+1 on the first line of a choice group, 4.6) | HUD 0x53CF0 (line offset, textbox.md 5.8) |
| F[859 + k] (`C859`) | scripts (reset to 0); HUD 0x53CF0 (dimming timer, textbox.md 5.8) | HUD 0x53CF0 |

Script_Scan (0xA4060, run after a slot load) ends by zeroing F823 (u16 at 0x164B83E),
F847..F857 (16 bytes at 0x164B86E, 8 at 0x164B87C) and F859..F869 (16 bytes at 0x164B886, 8 at
0x164B894); F858 and F870 are not touched (VA 0xA43BE..0xA43E6) [E]. textbox.md 5.8 gives the
visual meaning of C847 / C859; its "C<n> is F[n + F823]" [H] is now [EMU].

---

## 3. Operand decoding

### 3.1 Getting the argument [E, EMU]

Two things are taken from argument i of a statement (p0 = first byte after the `(`):

```
start(i): skip i commas, at most 500 bytes per argument; ')' or ';' first -> missing
first byte  (copy_arg, e.g. VA 0x98786..0x987C0):
    missing argument  -> argbuf[0] = 0
    else p = start(i); if buf[p] == '`': p += 5            # tag `NNN: skipped blindly
         copy bytes to argbuf (VA 0x16543C0) up to the first ')' ',' ';' (NUL is copied
         like any byte), append NUL; first = argbuf[0]
index n     = inline_int(i): VA 0x76690 (script.md 3.3): -1 when missing, tag skipped,
              16 bytes max, digits accumulate, '+'/'-' start a new signed term, other
              bytes ignored, result wrapped to i16
```

So the letter is never part of the number: `F0012` = F[12], `F1+2` = F[3], `F1a2` = F[12], `F`
= F[0] (empty number). A blind 5-byte tag skip on a short operand runs into the following
statement (`` _WKST(F5,eq,`12); `` reads past `);` and decodes `_DMMY(` as the operand).

### 3.2 First-byte dispatch per position [EMU-9108 + 1008]

Every byte 0x00..0xFF except `)` `,` `;` was tried as the first byte of every position, in both
interpreters (`first_byte_sweep`, `samples/misc_vm_variables_errata/first_byte_sweep.md`); the
engine and `tsuki.vm_vars` agreed on all 10,116 runs. The 1,008 `_SELA` / `_SELS` target runs
(`choice_setter_sweep`) go through ScriptStep's own handlers with F1118 = 0, so that `_SELA`
reaches its dialog helper.

| position | `C` | `F` | `G` | `_` | anything else (incl. `Z`, `D`, `X`, lower case, digits, space) |
|---|---|---|---|---|---|
| `_WKST` / `_WKAD` target (ScriptStep) | F[F823 + n] | F[n] | G[n] | `_` rule | no effect |
| `_WKST` / `_WKAD` target (Script_Prefetch) | no effect | F[n] | G[n] | `_` rule | no effect |
| `_WKOR` / `_WKOF` target (both) | no effect | F[n] | G[n] | `_` rule | no effect |
| `_WKBO` target (ScriptStep only) | no effect | F[n] | G[n] | no effect | no effect |
| value: `_WKST` arg 2, `_WKAD` / `_WKOR` / `_WKOF` arg 1, `_IF__` args 0 and 2 (both interpreters) | literal n | F[n] | G[n] | `_` rule | literal n |
| `_WKBO` arg 1 | literal | literal | literal | literal | literal (bit number) |
| `_WKST` arg 1 (operator) | only first byte `e` makes arg 2 the value; otherwise arg 1 itself is the value operand (value rule) |
| `_IF__` arg 1 (operator) | `e` ==, `n` !=, `g` > (>= when byte 1 is `e`), `l` < (<= when byte 1 is `e`); anything else never true |
| `_GOTS` arg 0 | byte 0 must be `_`; byte 1 is ignored; index = 3 digit bytes (3.5) |
| `_SELD` / `_SELA` / `_SELS` arg 0 | byte 0 must be `_` (else the previous target stays); byte 1 `D` -> kind 'F', anything else -> kind 'G' (never stored); index = 3 digit bytes. Decoded only outside scan mode, and for `_SELA` only when F1118 = 0 (4.5) |

The `_` rule (VA 0x9880F: `lea eax,[n-0x2BD]; cmp ax,-0x66; ja`): with n the inline value of
the whole argument, n in 600..700 (mod 2^16) selects G[n - 600], every other n selects F[n].
Byte 1 is not checked: `_D012` = `_X012` = `_ 012` = F[12]; digits in byte 1 are part of the
number (`_1012` = F[1012], `_2012` = F[2012] = Z[812]).

Engine VAs [E]: value dispatch 0x987E3 (WKST), 0x98EEA, 0x98FCE, 0x99319, 0x9A248; WKST / WKAD
target jump tables 0x20D0B0 / 0x20CFF0 (index first - 'C', 0..0x1C); WKOR / WKOF target compare
chains 0x99BAA / 0x99B35; WKBO 0x97E6A; WKST operator test 0x98709 (`cmp cl, 0x65`); IF__
operator 0x9A474..0x9A64C.

### 3.3 Reference decoder (what `tsuki.vm_vars` does)

```
def value(arg):                         # value positions
    first, n = first_byte(arg), inline_int(arg)
    if first == 'F': return F[s16(n)]
    if first == 'G': return G[s16(n)]
    if first == '_': return G[n - 600] if 600 <= n % 65536 <= 700 else F[s16(n)]
    return n & 0xFFFF                   # literal: C, Z, D, X, digits, ...

def target(arg, role):                  # role: wk (WKST/WKAD), wk_pre, or (WKOR/WKOF), bo (WKBO)
    first, n = first_byte(arg), inline_int(arg)
    if first == 'C' and role == wk: return &F[s16(F[823]) + s16(n)]     # F823 read first
    if first == 'F': return &F[s16(n)]
    if first == 'G': return &G[s16(n)]
    if first == '_' and role != bo: return as in value()
    return None                         # the command does nothing

def three_digits(argbuf):               # GOTS / SEL* (VA 0x96109..0x9612D, 0x9B5AE..0x9B5CF)
    return argbuf[2]*100 + argbuf[3]*10 + argbuf[4] - 5328      # raw bytes, no digit check
```

### 3.4 Read / write order [EMU]

The emulation logs every access in program order and the model reproduces it: WKST reads the
target's F823 (C only) after evaluating the value; WKAD / WKOR / WKOF read the value operand,
then read and write the target; IF__ reads lhs then rhs. Only the final state matters to a port.

### 3.5 Three-digit selectors (`_GOTS`, `_SEL*`) [EMU]

`_GOTS` and the `_SEL*` targets do not use the inline reader: the index is bytes 2..4 of the
argument buffer taken as decimal digits without any check, so `_D1000` = 100, a short `_D1`
reads the NUL and whatever follows in the buffer (the engine's buffer contents, which the
fuzzer randomises), `_000` = (48*100 + 48*10 + 0) - 5328 = -48. For `_GOTS`, 600..700 selects
G[n - 600] like the `_` rule; for `_SEL*` the index is used as is (`_D650` -> F[650]).

---

## 4. Commands [EMU unless marked]

### 4.1 `_WKST(target, op, value)` (ScriptStep 0x96566, Script_Prefetch 0x9BFA2)

```
v = value(arg 2) if first_byte(arg 1) == 'e' else value(arg 1)
a = target(arg 0); if a: *a = v
```

`_WKST(F1,7)` stores 7, `_WKST(F1,ne,7)` stores int("ne") = 0, `_WKST(F16,eq)` stores 0xFFFF
(missing argument = -1). The retail operator is always `eq` (487,857 of 487,857) [V-all].

### 4.2 `_WKAD` / `_WKOR` / `_WKOF` / `_WKBO`

```
WKAD(t, v):  *a = *a + value(v)   (u16 wrap)    handler main 0x97AE6, prefetch 0x9C99A
WKOR(t, v):  *a = *a | value(v)                 main 0x9798B (target chain 0x99BAA), prefetch 0x9CAFC
WKOF(t, v):  *a = *a & ~value(v)                main 0x96B98 (target chain 0x99B35), prefetch 0x9BE15
WKBO(t, b):  *a = *a | (1 << (inline_int(b) & 31))   main 0x97BB8 (16-bit store: bits 16..31 vanish)
```

Script_Prefetch has no `_WKBO` (the name is skipped there).

### 4.3 `_IF__(lhs, op, rhs, label)` (ScriptStep 0x9708D, Script_Prefetch 0x9BAD5)

```
if skip_counter (0x165420E) != 0: next statement
l = value(arg 0); r = value(arg 2)            # signed 16-bit compare
if cond(op, l, r): next statement              # op as in 3.2
elif arg 3 present (VA 0x76860): goto label(arg 3)     # copied to the argument buffer
else: next statement
```

Retail: lhs F 641 / G 553, rhs always a literal, op `eq` 645, `ne` 521, `ge` 22, `lt` 4, `gt` 1,
`le` 1, labels `LVddxdd` [V-all].

### 4.4 `_GOTS(selector, label0, label1, ...)` (VA 0x9609A)

```
if scan_mode (0x174DE48): return from ScriptStep (yield)
if skip_counter: next statement
if first_byte(arg 0) != '_': next statement
n = three_digits(argbuf); v = G[n-600] if 600 <= n <= 700 else F[n]
k = s16(v + 1)
goto text of argument k (k <= 0: argument 0, i.e. the selector itself; argument missing: "")
```

The label text goes to the label lookup VA 0x794D0. Retail: 170 `_GOTS(_D000,SEL<n>_0,...)`
with 1..10 labels, so the jump target is label number F[0] [V-all].

### 4.5 Choice commands and the result store (`_SELD`, `_SELS`, `_SELA`, choice UI) [EMU, E]

All three are run through ScriptStep's own handlers in the emulation (helpers included; only
the UI set-up, the text layout and the flowchart call are stubbed, `VarEmu.run`). All three
**yield** (ScriptStep returns at VA 0x9B516) in every case, scan mode included.

```
SELD(target, N)    handler 0x9B1DC                                       mode 1, list
    if scan_mode: yield                       # 0x9B1DC: cmp byte [0x174DE48],0; jne 0x9B516
    mode = 1                                  # dword 0x17506B0 (0x9B1FF)
    if word 0x120A6D2 (scene jump running): 0x7E620(); word = 0     # adv_state.md 5.2: ends the scene jump
    per-line loop 0x9B228..0x9B25F over the pending _SELR lines
    decode_target(arg 0)                      # 0x9B53F..0x9B5D6
    if arg 1 present (0x76860): id = inline_int(arg 1); 0xB8620(id)  # flowchart marks, below
    else: id = -1                             # -> i16 0x1750698
    0x56150(1, seen)  with seen = u8 0x164FBC0[id] != 0 when id >= 0   (system_flow.md 5.1)
    lay out the lines, byte 0x1653E00 |= 0x10, UI set-up 0xA5DF0, yield

SELS(target)       handler 0x9B435: two_answer(mode 2) (helper 0xB82E0), yield
SELA(target)       handler 0x9B1B6:                                       mode 3, save prompt
    if F[1118] == 0:                          # 0xA7FA0: cmp word [F + 0x8BC], 0; sete al
        two_answer(mode 3)                    # helper 0xB8400
    F[1118] = 0                               # 0xA7F90 with edi = 0, always
    yield                                     # 0x9B1D7: jmp 0x9B516

two_answer(mode)   helpers 0xB82E0 / 0xB8400 (identical apart from the mode)
    if scan_mode (0x7B4B0): return            # nothing else happens
    mode dword = mode; scene-jump check as in SELD
    decode_target(object-parser str(arg 0), 16-byte buffer)    # 0xB835C / 0xB847C
    count = 2; values = {0, 1} (dword 0x10000 at 0x1750630); id = -1
    byte 0x1653E00 |= 0x10; UI set-up 0xA5DF0

decode_target(t):  if t[0] == '_': kind = 'F' if t[1] == 'D' else 'G'; index = three_digits(t)
                   (else kind / index keep their previous values)
```

The **choice UI** dispatches its input by the mode (VA 0xA6180: mode 3 -> 0xA6AD0, mode 2 ->
0xA6940, anything else -> the list code of 0xA6180 itself). Only two places in the eboot read
the kind byte 0x1750648 (0xA65E3 and 0xA7BA6), so these are the only result stores [E]:

```
mode 1 (and any mode but 2 / 3)  list input, store 0xA65E3..0xA662E
    v = values[s16 cursor]                          # cursor u16 0x17506A2
    afterwards u8 0x164FBC0[id] |= 1 << v           # 0xA6647..0xA6662, "choice taken" bits
mode 2  two-button input 0xA7AA0 (tail-called from 0xA6940), store 0xA7B99..0xA7BDE
    if u16 cursor > 1: no store
    v = cursor                                      # the button index itself (widgets 0xD5 / 0xD6)
mode 3  input 0xA7FD0: no store at all
    cursor 0 -> phase 3 (the dialog closes); cursor 1 -> phase 4: fade, 0x6F450(1),
    0xEA0D0(1), 0x6F460(1) -> phase 5: 0xECF40(0x16, 0, 0x100) -> phase 6: wait until
    0x71C90(0) == 3, 0xECF40(0x16, 0x100, 0) -> phase 7 -> phase 3   [E; H: 0x16 = save menu]
store (modes 1 and 2):  kind 'F': F[s16(index)] = v;  kind 'Z': Z[s16(index)] = v;  else no write
```

For `_SELS` the stored button index equals values[cursor] only because `_SELS` sets values =
{0, 1}. No setter produces kind 'Z', so a `_SELD(_G005)` result is simply not stored. Both
store paths were emulated against `vm_vars.choice_store` (`choice_store_check`: 7 kind /
index / value cases, the list path with the given cursor, the two-button path with cursors 0,
1, 2 and 0xFFFF; 0 differences).

**Flowchart marks of `_SELD`** [EMU-87]: 0xB8620(N) does nothing unless 1 <= N <= 82 (`lea
eax,[rdi-0x53]; cmp eax,-0x52; jae`). Otherwise it ORs the edge bits of choice point N (table
0x2214E0, 3 x i32 per N) into G[400..425]. For N = 57, 67, 72, 73 (jump table 0x21F040) it also
sets byte marks in G[142], G[332] and F[265] / F[266], further edge bits in G[418], G[420],
G[425], G[434] and G[437] and, when F24 != 0, F[110] = 0x200 / 0x201 / 0x20B / 0x205. `choice_point_writes` lists every write for N = -1..85
(`samples/misc_vm_variables_errata/choice_commands.md`). This is gallery state (gallery.md 5.3);
`tsuki.vm_vars` reports the call as `Effect.choice_point = N` and leaves the marking to the
gallery code.

**The save prompt in the retail scripts** [V-all, model walk `save_prompt_flow`]. Each of the
26 `_SELA(_D000,)` stands at a chapter start, in this shape (script 35, `02_00_ARC02_1`):

```
... _FADS(1,0,0);_IF__(F026,ne,0,LV00000);          # jumps when F026 == 0 (4.3)
_STCC(-1);_FADS(1,0,0);_WKST(F026,eq,0);_WKST(F823,eq,0);_WKST(F847,eq,0) ... _WKST(F869,eq,0);
_WKST(F1118,eq,1);_WKST(F028,eq,0);_ZY0ca01(LV00000);_SELA(_D000,);_WTSV();_WTTM(1000,1);_TOMP(9);
```

The chapter before it ends with `_WKST(F1118,eq,0);...;_RDED(n);_JUMP(next)` (25 of the 27
chapter-end jumps into a `_SELA` script; the other 2 come from the `_FLAG0` / `_FLAG1`
sub-scripts of 13_01_01_CIEL13_1, which write no F1118). Walking every `_SELA` script from its
start with tsuki.vm_vars (F1118 = 0 on entry, `_IF__` / `_GOTO` followed) gives:

- F026 = 0: the `_IF__` skips the block, F1118 is still 0 at the `_SELA` and the **prompt
  opens** (26 of 26);
- F026 != 0: the block runs, F1118 = 1 and the **prompt stays shut** (26 of 26).

F026 is 0 after every scene start (`_WKST(F026,eq,0)` in the block). Only the ARCHIVE script
sets it from G008 (`_WKST(F026,eq,G008)`, the flowchart / archive scene jump, gallery.md 3.2),
and TEST_SCRIPT sets it to 1. So the prompt appears when a chapter is reached by playing. It
does not appear when the chapter is started from the archive, or (through the Slot_Store F1118
bit, 2.1) after loading a save.

Retail targets: every target is `_D000` (SELD 170, SELA 26, SELS 1), so the results of
`_SELD` / `_SELS` go to **F[0]**, the variable the following `_GOTS(_D000,...)` reads. In
QACALL, `_SELS(_D000,);...;_GOTS(_D000,SEL_NO,SEL_YES)` makes button 1 `SEL_YES`. F[0] is in
the history record (u16).

### 4.6 `_SELR(value, $text, locked)` (VA 0x9936D..0x9952A) [EMU, E]

```
v = inline_int(arg 0)
if count (0x1750646) == 0:                 # first line of a choice group
    History_Snapshot(.., kind 0xD)          # 0x993A0 -> 0x7AF90, gate below
    if s16(F823) > 0: F[F823 + 847] += 1   # VA 0x993A5..0x993B8: C847 += 1 (also in scan mode)
values[count] = v                          # VA 0x99500
locked[count] = inline_int(arg 2) == 1     # VA 0x9951C
text[count]   = "$" + 5-digit id (table 0x174FB70, stride 0xFA)   [E, not modelled]
count += 1                                 # no yield: ScriptStep continues (0x95EB0)

History_Snapshot gate (0x7AF9B..0x7AFD7), in this order:
    if byte 0x174DE48 (scan mode) != 0: return        # F929 is not read
    if F929 != 0: return                              # 0x7AFA8
    if F1102 != 0 and kind not in (0, 0xB): return    # 0x7AFB6; read only when F929 == 0
    if byte 0x16541F4 != 0: return                    # 0x7AFD0
    body from 0x7AFDD                                 # the record itself: adv_state.md 2.2 (History_Push)
```

`vm_vars.snapshot_gate` models the gate with the engine's reads (F929, then F1102), and
`Effect.snapshot` says whether the body runs. The harness stops the engine at 0x7AFDD and
compares the two. With the body executed (F929 = F1102 = 0) the engine made 169 variable reads
and its only variable write was the C847 increment (`snapshot_body_writes`), so the snapshot
changes no variable. The model agrees with the engine on all 32 flag combinations
(`choice_flag_cases`, `samples/misc_vm_variables_errata/choice_commands.md`): scan mode x F929
x F1102 x block byte for `_SELR`, and scan mode x F1118 for `_SELA` / `_SELS` / `_SELD`.

Retail: 824 lines, values 0..9 [V-all].

### 4.7 Script_Prefetch (0x9B760)

Handles only `_WKST`, `_WKAD`, `_WKOR`, `_WKOF`, `_IF__` among the variable commands, with the
same dispatch except that `C` targets do nothing (no `C` case in its WKST / WKAD tables). It
copies F / Z / G at entry and restores them at exit (engine.md 5.4), so none of its writes
last. [EMU for the handlers, E for the copy]

### 4.8 `_EIF_` and Script_EvalCondition (0x79D30) [EMU-13, E]

The function engine.md calls `Script_EvalCondition` is the expression evaluator of `_EIF_`
only (callers 0x96F1D main, 0x9CFC5 prefetch, and itself). Its syntax uses bracketed
operands; in the 13 emulated cases `=` compares equal, `!` not equal, `&` and `|` combine, e.g.
`(F[12]=F[13])`, `(F[12]|Z[3])`. It reads `F[n]`, `Z[n]` and `G[n]` (unchecked: `F[1200]` reads
Z[0]; `X[..]` / `f[..]` read nothing) and writes the 0/1 result to the block flag 0x165420C
(VA 0x96F13). The full grammar was not explored. `_EIF_`, `_IFF_` and `_ELS_` occur 0 times in the scripts, so a port can
leave them out. `_IF__` never calls it.

---

## 5. Emulated operand table [EMU-912]

Engine result for each form (F823 = 5; for the SEL* readers F1118 = 0 and scan mode off; `lit
n` = literal value, `-` = nothing happens, `none (G..)` = choice kind 'G', never stored). Columns that behave identically are merged (the full
19-column table is `samples/misc_vm_variables_errata/operand_table.md`; "value" = WKST / WKAD /
WKOR value, IF__ lhs and rhs, in both interpreters).

| form | WKST/WKAD target | WKOR/WKOF target | WKBO target | value | WKBO value | GOTS | SEL* | prefetch WKST/WKAD target |
|---|---|---|---|---|---|---|---|---|
| `F12` | F[12] | F[12] | F[12] | F[12] | lit 12 | - | - | F[12] |
| `F0012` | F[12] | F[12] | F[12] | F[12] | lit 12 | - | - | F[12] |
| `F1200` | Z[0] | Z[0] | Z[0] | Z[0] | lit 1200 | - | - | Z[0] |
| `F2600` | G[0] | G[0] | G[0] | G[0] | lit 2600 | - | - | G[0] |
| `F-3` | 0x164b1ca | 0x164b1ca | 0x164b1ca | 0x164b1ca | lit -3 | - | - | 0x164b1ca |
| `F32768` | 0x163b1d0 | 0x163b1d0 | 0x163b1d0 | 0x163b1d0 | lit -32768 | - | - | 0x163b1d0 |
| `F70000` | G[1864] | G[1864] | G[1864] | G[1864] | lit 4464 | - | - | G[1864] |
| `F1+2` | F[3] | F[3] | F[3] | F[3] | lit 3 | - | - | F[3] |
| `F` | F[0] | F[0] | F[0] | F[0] | lit 0 | - | - | F[0] |
| `G070` | G[70] | G[70] | G[70] | G[70] | lit 70 | - | - | G[70] |
| `G2000` | 0x164d5c0 | 0x164d5c0 | 0x164d5c0 | 0x164d5c0 | lit 2000 | - | - | 0x164d5c0 |
| `C847` | F[852] | - | - | lit 847 | lit 847 | - | - | - |
| `C0` | F[5] | - | - | lit 0 | lit 0 | - | - | - |
| `C-5` | F[0] | - | - | lit -5 | lit -5 | - | - | - |
| `Z12` | - | - | - | lit 12 | lit 12 | - | - | - |
| `_D000` | F[0] | F[0] | - | F[0] | lit 0 | F[0] | F[0] | F[0] |
| `_X012` | F[12] | F[12] | - | F[12] | lit 12 | F[12] | none (G12) | F[12] |
| `_000` | F[0] | F[0] | - | F[0] | lit 0 | 0x164b170 | none (G-48) | F[0] |
| `_D599` | F[599] | F[599] | - | F[599] | lit 599 | F[599] | F[599] | F[599] |
| `_D650` | G[50] | G[50] | - | G[50] | lit 50 | G[50] | F[650] | G[50] |
| `_D701` | F[701] | F[701] | - | F[701] | lit 701 | F[701] | F[701] | F[701] |
| `_D1000` | F[1000] | F[1000] | - | F[1000] | lit 1000 | F[100] | F[100] | F[1000] |
| `D12`, `X12`, `f12`, `c12`, `z12`, `12` | - | - | - | lit 12 | lit 12 | - | - | - |
| `-12` | - | - | - | lit -12 | lit -12 | - | - | - |
| `` `001:F12 `` | F[12] | F[12] | F[12] | F[12] | lit 12 | - | - | F[12] |
| `` `001:12 `` | - | - | - | lit 12 | lit 12 | - | - | - |
| ` F12` (leading space) | - | - | - | lit 12 | lit 12 | - | - | - |
| (empty) | - | - | - | lit 0 | lit 0 | - | - | - |

Note the asymmetry of `_D600`..`_D700`: `_SEL*` stores to F[600..700] while `_GOTS`, `_IF__`
and the `_WK*` commands read G[0..100]. The retail scripts use only `_D000`.

---

## 6. History record, save slots and rewind [EMU-28800, E]

### 6.1 The three index lists [E, EMU]

`History_Snapshot` (0x7AF90, list loops VA 0x7B191..0x7B27C) copies selected F values into the
0x3A10-byte history record; the slot loader (0xA3050, loops 0xA3293..0xA334F) and the rewind
(`History_Restore` 0x7C6D0, loops 0x7CB99..0x7CC7F) copy them back. The index lists are i16
arrays in rodata (file offset = VA + 0x2280):

| list VA | entries | record field | stored | restored | range check |
|---|---:|---|---|---|---|
| 0x2160E0 | 39 | +0x34AA u16[39] | F exactly | exactly | none |
| 0x2165F0 | 276 (ends at the first negative entry, max 300) | +0x3501 i8[300] | low byte | sign-extended (`movsx`) | entries > 1200 skipped (slot still used) |
| 0x216580 | 43 (first entry 998 also hard-coded, `mov si, 0x3E6`) | +0x34FA bit field | bit = (F != 0) | 0 / 1 | entries > 1200 (unsigned) skipped |

Restore order is u16, then i8, then bit, so an index in two lists ends with the later class:
F967 (u16 and i8) is restored as i8, F991 (u16 and bit) as a bit.

```
u16 (39):  0..8, 22, 991, 985, 954, 967, 966, 965, 964, 931..939, 859..869, 110, 1122
bit (43):  998, 997, 996, 994, 993, 991, 992, 988, 987, 986, 984, 822, 1103, 1104, 929, 983,
           975, 460, 500, 54..74, 1118, 1123, 28
i8 (276):  298, 1100, 989, 982, 967, 963, 962, 1101, 981, 799..821, 823..858, 870..880, 23,
           50..53, 75..81, 24..27, 31, 32, 33, 1105..1108, 1110..1113, 1109, 501, 100, 101,
           1098, 201..272, 199, 200, 995, 600..687, 1120, 1121
union: 356 distinct indices, all < 1200
```

### 6.2 Slot load versus rewind [E, EMU]

```
slot load (0xA3050):  memset(var_F, 0, 0x960); memset(var_Z, 0, 0xAF0)   (VA 0xA3163 / 0xA3176)
                      restore the three lists from the slot's state record
                      ... Script_Scan (0xA4060) ... ends with F823 = 0, F847..F857 = 0,
                          F859..F869 = 0 (2.3)
rewind (0x7C6D0):     restore the three lists; nothing is cleared
var_G:                touched by neither (G[0..499] come from the system data, engine.md 7.3)
```

So after a slot load an F index that is not kept is 0; after a rewind it keeps its current
value. `vm_vars.history_roundtrip` / `history_after_rewind` model this; `history_check` ran the
engine's three loops on 24 random var_F images (half of the values s8 / bit edge cases) and
compared all 1200 indices per path: 28,800 comparisons, 0 differences. What Script_Scan
re-executes during a slot load belongs to the save / resume work (adv_system_state).

### 6.3 Classification of the variables the scripts use [V-all, E]

All 78 F indices that occur in a script operand (including the C slots at the F823 values of
the linear scan) are in the record; no literal the scripts store changes in a save / load round
trip (i8 slots only receive values -128..127, bit slots only 0 / 1):

| storage | F indices used by the scripts |
|---|---|
| u16 (13) | F0 (choice result), F22, F859..F869 (C859 slots) |
| i8 (38) | F23..F27, F50..F53, F75..F81, F823, F847..F858 (C847 slots F847..F857; F858 only in the page reset), F1105..F1113 |
| bit (27) | F28, F54..F74, F500, F822, F1104, F1118, F1123 |
| transient | none |

The 18 G indices the scripts use (G3, G4, G8, G9, G10, G12, G13, G15, G16, G18, G20..G23, G25,
G26, G29, G70) are all below 500, i.e. system data shared by all slots (route and unlock
state). The engine itself references 90 fixed F indices; 9 of them are not kept (F36, F953,
F955, F959, F961, F1096, F1097, F1099, F1102: engine-internal, transient) [E, static scan
`scratch/vm_vars/engine_refs.py`].

Not counted above, because no script operand names them: the variables the engine writes as a
side effect of a script command. These are the flowchart marks of `_SELD` (G[142], G[332],
G[400..437], F110, F265, F266; 4.5) and `_SELA`'s F1118 = 0 (F1118 is also a script operand).
All are kept: F110 u16, F265 / F266 i8, F1118 bit, and the G indices are below 500. F1118 is a
bit in the record, and the slot store forces that bit to 1 (2.1).

---

## 7. Corpus census [V-all]

`python -m tsuki.misc_vm_variables_errata census` (`samples/misc_vm_variables_errata/
operand_census.json`), segmentation as the VM does it (script.md 3.1): 549 scripts, **1,083,168
segments**.

| command | count | operand kinds (decoded as the engine does) |
|---|---:|---|
| `_WKST` | 487,857 | target F 402,379 (`Fddd` 397,997, `Fdddd` 4,382), **C 85,471** (C847 42,881, C859 42,590), G 7; operator `eq` 487,857; value literal 487,836, F 20, G 1 |
| `_WKAD` | 45,679 | target F 42,604, **C 3,075** (all C847); value literal 45,679 (one negative) |
| `_IF__` | 1,194 | lhs F 641, G 553; rhs literal 1,194 |
| `_SELR` | 824 | values 0..9 |
| `_SELD` | 170 | target `_D000` 170; id empty 87, numeric 83 |
| `_GOTS` | 170 | selector `_D000` 170 |
| `_DMMY` | 71 | dummies of removed lines (script.md 3.7); argument 0 `Cddd` 36, `Fddd` 17; no effect |
| `_SELA` | 26 | `_D000` |
| `_WKOR` | 21 | target G 21 (G12..G29 bit sets), value literal |
| `_SELS` | 1 | `_D000` |
| `_WKOF`, `_WKBO` | 0 | |

Value operands that are variables (all `_WKST`, 21): `_WKST(F078,eq,F051)`,
`_WKST(F079,eq,F078)`, `_WKST(F080,eq,F079)` (4 each), `_WKST(F075,eq,F050)`,
`_WKST(F076,eq,F075)`, `_WKST(F077,eq,F076)`, `_WKST(F081,eq,F080)` (2 each) and
`_WKST(F026,eq,G008)` (1).

---

## 8. Worked examples [V-all, EMU]

Each statement was run through the engine and through `tsuki.vm_vars` (identical in every
case): `scratch/misc_vm_variables_errata/worked_examples.py` (log `worked_examples.log`) for the
message block, `scratch/misc_vm_variables_errata/repair/worked_examples2.py` (log
`worked_examples2.log`) for the choice commands.

Message block of script 4:

```
_WKAD(F823,1);_WKST(C847,eq,0);_WKST(C859,eq,0);_WNTY(0);_ZM02172($001264);
```

F823 goes from 1 to 2 (read 1, write 2 at 0x164B83E); `C847` reads F823 = 2 and writes F[849]
(0x164B872) = 0, `C859` writes F[861] (0x164B88A) = 0.

First choice of script 4:

```
_SELR(0,$042569);_SELR(1,$042570);_SELR(2,$042571);_SELD(_D000,1);
_GOTS(_D000,SEL0_0,SEL0_1,SEL0_2);
```

- First `_SELR` (F823 = 3) with the snapshot gate open (F929 = F1102 = 0): reads F929 = 0,
  F1102 = 0, the snapshot is taken, then F823, F[850] += 1; value 0 stored, count 1. With
  F929 = 1 the engine reads only F929 and takes no snapshot. The next two lines store 1 and 2.
- `_SELD`: mode 1, kind 'F', index 0, id 1, flowchart call 0xB8620(1) (G[402] |= 0xE00), the UI
  opens, ScriptStep yields.
- The player picks line 2: the list store (cursor 2, values {0, 1, 2}) writes F[0] = 2.
- `_GOTS`: reads F[0] = 2, k = 3, label = argument 3 = `SEL0_2`.

Chapter start of script 35 (4.5): entered from the previous chapter, F1118 = 0 and the block
with `_WKST(F1118,eq,1)` is skipped, so `_SELA(_D000,)` reads F1118 = 0, sets mode 3 and
values {0, 1}, opens the prompt, writes F1118 = 0 and yields. Entered from the archive, or
after a load, F1118 = 1: `_SELA` reads 1, writes F1118 = 0, yields, and nothing else changes.

QACALL: `_SELS(_D000,)` sets mode 2, kind 'F', index 0 and values {0, 1}, then opens the
dialog. The two-button store writes F[0] = 0 or 1 (the button index), and
`_GOTS(_D000,SEL_NO,SEL_YES)` with F[0] = 1 jumps to `SEL_YES`.

---

## 9. Python API

```python
from tsuki import vm_vars as vv
st = vv.VarState()                       # memory = flat word map keyed by engine VA
st.mem.poke(vv.VAR_F + 2 * 823, 5)
eff, _ = vv.execute_text("_WKST(C847,eq,63);", st)
st.mem.writes()                          # [(0x164B878, 63)]  = F[852]
vv.parse_operand("C847", vv.Role.WK_TARGET)   # Operand('C', 847) -> F[F823+847]
vv.parse_operand("C847", vv.Role.VALUE)       # Operand('lit', 847)
vv.history_storage(848), vv.history_roundtrip(848, 200)   # ('s8', 0xFFC8)

st = vv.VarState(scan_mode=False)        # also: skip_counter, snapshot_block
eff, _ = vv.execute_text("_SELA(_D000,);", st)   # F1118 = 0 in fresh memory
eff.yielded, eff.choice_ui, st.choice_mode       # (True, True, 3)
vv.choice_store(st, 1)                   # None: the save prompt stores nothing
eff, _ = vv.execute_text("_SELR(0,$000001);", vv.VarState())
eff.snapshot                             # True: F929 = F1102 = 0, gate open
```

`Effect` fields: `handled`, `goto` (label text), `yielded` (ScriptStep returns), `snapshot`
(History_Snapshot body runs), `choice_ui` (choice UI set up), `choice_point` (argument of the
flowchart call of `_SELD`). `choice_store(st, cursor)` applies the store of the current mode.

CLI: `python -m tsuki.vm_vars "_SELA(_D000,);" --set F1118=1 [--scan] [--f823 5]`;
`python -m tsuki.misc_vm_variables_errata census | table | sweep | history | eif | choice |
fuzz N SEED | layout | vcase | all`.

---

## 10. Port checklist

- Three u16 arrays F[1200], Z[1400], G[2000], zero at boot. Reject (or log) indices outside the
  array instead of aliasing; the retail scripts never need aliasing.
- Decode operands by first byte as in 3.2 / 3.3, index via the inline reader (script.md 3.3).
- `C<n>` = F[F823 + n] as a `_WKST` / `_WKAD` target, literal elsewhere; ignore it in the
  prefetcher.
- `Z<n>` / lower case / other letters: literal in value positions, no-op as targets.
- `_IF__` jumps when false; operators by first byte e / n / g / l (+ second byte `e`).
- `_WKST` uses argument 2 only when argument 1 starts with `e`.
- Choice flow: `_SELR` collects values (and C847 += 1 on the first line when F823 > 0).
  `_SELD` / `_SELS` record target F[ddd] (byte 1 `D`) and open the list / two-button dialog.
  The UI stores values[cursor] (`_SELD`) or the button index (`_SELS`), and `_GOTS` jumps to the
  label in argument F[ddd] + 1 (label number F[ddd], counted from 0). All three `_SEL*` yield.
- `_SELA`: open the "save your progress?" prompt only when F1118 = 0, set F1118 = 0 every
  time, store no result (button 1 leads to the save screen). Set F1118 = 1 in every slot you
  store.
- Scan mode (re-execution after a load): `_SELD` / `_SELS` / `_SELA` open nothing and keep the
  choice state (`_SELA` still clears F1118); `_GOTS` yields; `_SELR` still counts its line and
  does C847 += 1 but takes no history snapshot.
- `_SELR`'s first line takes a history snapshot only when not in scan mode, F929 = 0, F1102 = 0
  and the "message continues" byte is 0 (4.6).
- `_SELD` with a choice point 1..82 marks the flowchart edges (gallery.md 5.3).
- Save / history: keep at least the 356 listed F indices (with their storage class if PS4
  slots are to be imported); a slot load zeroes the other F and all of Z; a rewind leaves them;
  G[0..499] live in the system data.

---

## 11. Engine re-check findings applied (errata (a)-(d))

| finding | result here | applied to |
|---|---|---|
| (a) `C<n>` operands missing / misdescribed | `C<n>` = F[F823 + n] as a WKST / WKAD target (F823 = 0 gives the F[847] alias the re-check saw); literal in value slots; `C3`, `Z3`, `X3` read as 3 [EMU] | script.md 3.5, engine.md 5.5, RUNTIME_REQUIREMENTS.draft.md item 6 |
| (b) Script_Prefetch V case | only VPLY and VPL2 reach the handler 0x9BD2B; all 250,047 `V???` names emulated [EMU-250047]; table 43 entries (41 names + `J***` + `ZY**`) | engine.md 5.4, RUNTIME_REQUIREMENTS line 59, `tools/eboot/scriptvm.py` (setcc), `samples/engine/vm_commands.json`, `tools/tests/test_eboot.py` |
| (c) save image unused area | slots end at 0x1A00 + 140 * 0x7140 = 0x3E0900; unused 0x3E0900..0x400000 = 0x1F700 bytes (decoded from the instructions, `save_image_layout`) [E] | engine.md 7.2 |
| (d) VM segment count | 1,083,168 (the old 1,083,717 = `split(';')` pieces, one empty piece after the final `;` of each of the 549 scripts) [V-all] | recorded below (engine.md 5.3 and 11 are outside this task's edit rights) |

### Errata for engine.md (sections not edited here)

Line numbers are those of engine.md as of 2026-10-25, after the edits to 5.4 / 5.5 / 7.2.

- 5.3 (line 782) and 11 (line 1348): "1,083,717" segments should be **1,083,168** (finding d;
  `corpus_census` prints both counts and the 549 trailing empty pieces).
- 11 (line 1343): "Script_Prefetch 42 entries (39 names + J***, V***, ZY**)" should be 43
  entries (41 names + J***, ZY**) (finding b).
- 12 (line 1381, row "Replay interpreter has 42 names, not 35"): "42 entries incl. STCU/STCW"
  should be 43 entries (41 names + J***, ZY**); the table in 5.4 already has 43 (finding b).
- 10 item 12 (line 1327): "`_V***`/`_PLDV` in the prefetcher (0x76C10)" should read `_VPLY` /
  `_VPL2` / `_PLDV`.
- 5.6 (lines 979..980): "+0x3501.. 300 x i8 F values (index list 0x2165F0, entries <= 0x4B0)":
  276 entries are used, the list ends at the first negative entry; the bit list's first index
  (998) is also an immediate in the code (6.1).
- `samples/engine/script_cmd_counts.json` (`_segments` 1,083,717) has the same counting
  difference as finding (d).

### Errata for textbox.md

- 5.8 "C<n> is `F[n + F823]` [H]": verified by emulation of the WKST / WKAD handlers (section
  2.3); it applies to script writes only (in a value slot C<n> is the literal n).

### Errata for docs/FORMATS.md

- 1.2: `formats/vm_variables.md` and `formats/misc_vm_variables_errata.md` are now delivered.
- 6.5 bullets 1, 2, 3, 4 are applied (engine.md 5.4 / 5.5 / 7.2; 5.3 recorded above).
- 7 P0 item 6 is done.

### Errata for script.md (beyond 3.5)

- 4.2 SELD row: "store the chosen k in `var`": the stored value is argument 0 of the chosen
  `_SELR` line (values 0..9; value 1 occurs 175 times, value 0 171 times, so it is not always
  the position); the second `_SELD` argument is stored as the choice id (i16 0x1750698, -1 when
  empty) and passed to the flowchart marking 0xB8620 (4.5); whether it is the default cursor
  belongs to the choice-screen work (system_flow.md 5.1: it is not).
- 4 SELA / SELS row (line 564): "(var, _) ... SELA = chapter-start 'continue' prompt" [D]:
  `_SELA` is the "save your progress?" prompt (system_flow.md 9.4). It opens only when F1118 = 0,
  always sets F1118 = 0 and stores no result. `_SELS` stores the button index 0 / 1 in `var`.
  In scan mode neither opens nor changes the choice state (4.5) [EMU].

### Errata for system_flow.md

- 5.1 `_SELA(var)` row: the handler first tests F1118 (0xA7FA0) and calls 0xB8400 only when it
  is 0, then writes F1118 = 0 (0xA7F90). Its mode-3 input (0xA7FD0) stores nothing into `var`;
  button 1 continues into the phases 4..7 (0x6F450 / 0xEA0D0 / 0x6F460, menu 0x16) (4.5) [E,
  EMU].
- 5.1 `_SELS(var)` row: the result written to `var` is the button index (0xA7B99..0xA7BDE,
  only for cursor 0 / 1), not values[cursor]; the two agree only because values = {0, 1}.
- 5.1: both helpers (0xB82E0 / 0xB8400) and `_SELD` return without any change in scan mode
  (byte 0x174DE48) [EMU].

### Errata for adv_state.md

- 14 item 3 (F1118 role): answered in 2.1 and 4.5 here (the `_SELA` save-prompt gate; the
  Slot_Store bit keeps a loaded save from re-asking). The roles of F967 and F1122 remain open
  (section 14 below).

---

## 12. Evidence (commands run for this document)

All with `PYTHONPATH=tools`, `.venv/Scripts/python.exe`;
logs in `scratch/misc_vm_variables_errata/` (the review repair: `repair/`).

- Re-check scripts re-run (step 1): `scratch/verify_engine_reproduce/r2/emu_c.py` (C847 ->
  0x164B86E, C859 -> 0x164B886, `_WKAD(C847,5)` -> 0x164B86E; C3 / Z3 / X3 read as 3; G3 / F3
  read the variable), `emu_vars.py` (F / G / Z targets, `f12`, F65535), `operands.py` (WKST arg
  0 shapes F 402,379 / C 85,471 / G 7), `cmp_all.py main` and `cmp_all.py pre` against the
  regenerated `vm_commands.json` (67,188 names each, 0 mismatches); `rerun_r2_*.log`.
- `v_names_all.py pre` / `main`: every `V???` name over [A-Z0-9_a-z] (250,047) through each
  interpreter's dispatch: prefetcher only VPLY / VPL2, main VCLR / VCWT / VPLY / VPL2
  (`rerun_v_names_*.log`).
- `python -m eboot.scriptvm --json` -> byte-identical to `samples/engine/vm_commands.json`
  (`rerun_scriptvm_cmp.log`).
- `misc_vm_variables_errata table`: 48 forms x 19 readers, 0 mismatches (`repair/table.log`).
- `misc_vm_variables_errata sweep`: 9,108 statement runs + 1,008 SELA / SELS runs (now through
  ScriptStep's handlers), 0 mismatches (`repair/sweep.log`, `first_byte_sweep.md`).
- `misc_vm_variables_errata choice` (`repair/choice.log`, `choice_commands.md/.json`): 32 flag
  cases (`_SELA` / `_SELS` / `_SELD` x scan x F1118; `_SELR` x scan x F929 x F1102 x block
  byte), 0 mismatches. The snapshot body executed: 169 reads, the only write F[850] += 1.
  Flowchart call for N = -1..85: writes only for 1..82. Both result stores: 0 mismatches. Save
  prompt walk over the 26 `_SELA` scripts: opens with F026 = 0 in 26, shut with F026 = 1 in 26.
- `misc_vm_variables_errata fuzz 1000 20261025` and `fuzz 20000 1|2|3` with the extended
  generator (`rand_case`: SELA / SELS added, F1118, F929 = 0 and F1102 cases, scan mode 15 %,
  skip counter 10 %, block byte 10 %): 61,000 cases, 0 mismatches. The comparison now also
  covers the yield, the snapshot gate, the UI set-up, the choice mode and the flowchart call
  (`repair/fuzz*.log`). Seed 1, for example, had 3,036 scan-mode, 1,993 skip-counter and 2,028
  block-byte runs, 3,882 yields, 204 snapshots, 444 opened `_SELA` prompts and 457 flowchart
  calls.
- `repair/corpus_choice.py`: every distinct retail `_SELA` / `_SELS` / `_SELD` / `_SELR` /
  `_GOTS` statement (959 distinct, 1,191 uses) x F1118 {0, 1} x scan {off, on} x snapshot gate
  {open, shut}: 7,672 runs, 0 mismatches (`repair/corpus_choice.log`).
- The reviewer's scripts re-run against the repaired model (`repair/review_scripts_rerun.log`,
  `repair/review_fuzz_rerun.log`): `rv_sela.py` (all 8 SELA / SELS rows now identical,
  including F1118 = 1 and scan mode), `rv_flags.py` (0 DIFF, SELD scan-mode globals unchanged
  as in the engine), `rv_selr.py` (0 diffs), `rv_fuzz.py odd 10000 1|2|3` and `corpus 8000
  31337` (0 mismatches).
- `repair/sela_context.py`, `repair/f1118_context.py`, `repair/sela_entry.py`: the retail F1118
  writes (510 = 1 in 492 scripts, 26 = 0) and the jumps into the `_SELA` scripts (25 of 27
  chapter-end jumps preceded by `_WKST(F1118,eq,0)`, 52 from the JUMPnn scripts).
  `repair/xref_f1118.py`: the only F1118 accesses in the eboot text are 0xA7F90 / 0xA7FA0
  (callers only the `_SELA` handler) and the three `mov esi, 0x45E` slot-store sites.
- Disassembly (capstone, `scratch/review_vm_variables_errata/rvdis.py`): 0x9B1B6..0x9B1D7
  (SELA), 0x9B1DC..0x9B238 and 0x9B5D6..0x9B74A (SELD), 0x9B435 (SELS), 0xB82E0..0xB83F0 /
  0xB8400..0xB8510 (helpers), 0x7B4B0, 0xA7F90 / 0xA7FA0, 0x7AF90..0x7B00F (snapshot gate),
  0x99370..0x993B8 (SELR), 0xA6180 / 0xA6940 / 0xA6AD0 (mode dispatch; jump table 0x216A30),
  0xA65E3..0xA6665 (list store), 0xA7AA0..0xA7BE2 (two-button store), 0xA7FD0..0xA81D2 (mode-3
  input), 0xB8620..0xB87F6 (flowchart marks; jump table 0x21F040).
- `repair/worked_examples2.py`: the choice examples of section 8 (identical).
- `scratch/misc_vm_variables_errata/worked_examples.py`: the message block of section 8
  through the engine and the model (identical; `worked_examples.log`).
- `vm_vars.segments` (fast segmentation used by the census) against the engine-style find loop:
  identical on all 549 scripts (1,083,168 segments) and on 20,000 random strings; the rewritten
  `vm_vars.arg_texts` against the earlier byte loop: identical on 50,000 random inputs; the
  two-pass census output is identical to the earlier one-pass output.
- `history`: 28,800 round trips, 0 mismatches. `eif`: 13 `_EIF_` expressions. `layout`: save
  image 0x400000, 140 slots x 0x7140 from 0x1A00, unused 0x3E0900..0x400000. `census`: the
  counts of section 7 (all `repair/*.log`, unchanged by the repair).
- `mypy --ignore-missing-imports` on vm_vars.py, misc_vm_variables_errata.py and both test
  files: no issues.
- Tests: section 13.

## 13. Tests

`tests/test_vm_vars.py`. Synthetic model tests always run: operand roles, C operands, value
dispatch, `_IF__`, the `_GOTS` / choice flow, the `_SELA` F1118 gate, scan mode, the two-button
store and the snapshot gate (`test_choice_commands_flags`). Engine tests: every form in every
reader, 1,000 fuzz cases with flags, the `_SEL*` handlers under F1118 / scan mode and both
stores. Corpus census.

`tests/test_misc_vm_variables_errata.py`: errata (a)-(d), operand table, first-byte sweep, the
choice commands under flags with the snapshot body (`test_choice_commands_under_flags`), the
flowchart call (`test_seld_choice_point_marks`), the retail save prompt walk
(`test_save_prompt_flow_in_retail_scripts`), history emulation, the `_EIF_` evaluator, fuzz with
another seed, census and classification.

Both files skip without the game, eboot.bin or unicorn / capstone. Measured after the repair:
the two files together take 23.5 s (27 passed). The longest test is the first-byte sweep
(12 s; it runs the prefetch interpreter on 22 representative bytes, the CLI `sweep` runs all of
them). With test_eboot.py and test_script.py the four files give 64 passed and 1 skipped
(section 15).

## 14. Open questions

1. The label lookup (0x794D0) result when a `_GOTS` / `_IF__` label does not exist (e.g. the
   selector text when F[ddd] + 1 <= 0) was not traced; retail never triggers it.
2. Script_Scan (0xA4060) after a slot load: which statements it re-executes, and therefore which
   transient engine F values are rebuilt (dependency: adv_system_state). Section 4.5 / 4.6 give
   what the choice commands do in scan mode; what Script_Scan does with the yields is open.
3. The record the SELR history snapshot (kind 0xD) takes, and how a rewind to a choice uses it
   (adv_system_state, boot_title_choice_flow). The gate is now decoded (4.6).
4. The meaning of most script flags (F22..F81, F1104..F1123, G3..G70) is route / scene state;
   naming them is gallery_extras_flowchart / adv_system_state work. F1118 is resolved (2.1).
   F967 (written at 0x7EB87 / 0x7EC49, cleared at 0x78E44 / 0x7D531 / 0x7EBBD / 0x7EC7E) and
   F1122 (written by a ScriptStep handler at 0x97852 and by Script_Prefetch at 0x9D370, read by
   the slot load at 0xA34C2), which adv_state.md 14 also asks about, were only located
   (`repair/xref_findex.py`), not explained.
5. `_IFF_` / `_ELS_` / `_EIF_` (unused) block semantics were not modelled beyond 4.8.
6. The `_SELA` prompt's button 1 path calls 0x6F450 / 0xEA0D0 / 0x6F460 and menu 0x16 [H: the
   save screen]; the UI side belongs to boot_title_choice_flow.

## 15. Review of the first version and what was changed

| severity | review finding | check | change |
|---|---|---|---|
| major | `_SELA` reads and clears F1118, and the scan-mode exits of `_SELA` / `_SELS` / `_SELD` were not modelled. The harness had bypassed the handlers (helpers called directly, 0x7B4B0 stubbed to 0) and the fuzz had no `_SELA` / `_SELS` | Confirmed by disassembly (0x9B1B6, 0xA7F90 / 0xA7FA0, 0x7B4B0, 0x9B1DC) and emulation (`rv_sela.py` reproduced) | `vm_vars`: `_sela` (F1118 gate, F1118 = 0, yield), `_sels`, `_seld` (scan mode yields), choice mode, `Effect.yielded` / `choice_ui` / `choice_point`. Harness: `_SEL*` through ScriptStep's own handlers (`run_choice_setter` removed), fuzz with `_SELA` / `_SELS`, F1118, scan mode, skip counter. Doc 1, 2.1, 3.2, 4.5, 8, 10 |
| minor | The `_SELR` snapshot gate also reads F1102 and skips F929 in scan mode | Confirmed (0x7AF9B..0x7AFD7; `rv_selr.py`) | `vm_vars.snapshot_gate`, `Effect.snapshot`; the harness stops at the body (0x7AFDD) and compares; fuzz covers F929 = 0, F1102, block byte; doc 4.6 |
| minor | The second store path writes the cursor, not values[cursor] | Confirmed (0xA7B99..0xA7BDE in the two-button input 0xA7AA0) and extended: mode 2 uses it, mode 3 has no store, mode 1 uses 0xA65E3 | `choice_store(st, cursor)` by mode; `run_choice_store(two_button=True)`; `choice_store_check`; doc 4.5 |
| minor | Stale engine.md line numbers; section 12 (line 1381) missing; Slot_Store F1118 bit missing from 2.1 | Confirmed by grep | Errata re-numbered and extended (11); F1118 table and the Slot_Store rule in 2.1 |

Found during the repair, beyond the review: `_SELD` with argument 1 calls 0xB8620, which writes
flowchart marks into G / F (4.5). The earlier harness stubbed that call without saying so. Now
it is recorded (`Effect.choice_point`, compared in every run) and emulated on its own
(`choice_point_writes`). The `_SELD` emulation now runs to the yield instead of stopping after
the target decode.

The F1118 flow was not in the review either: the 26 retail `_SELA` prompts open only on the
play-through path (4.5).

Test results after the repair: `pytest tests/test_vm_vars.py tests/test_misc_vm_variables_errata.py
tests/test_eboot.py tests/test_script.py` gives 64 passed and 1 skipped (test_eboot: no
savedata000) in 50 s. The two new files alone give 27 passed in 23.5 s. With `TSUKI_GAME_DIR`
pointing to a missing folder they give 8 passed and 19 skipped. `mypy --ignore-missing-imports`
reports no issues in the four touched Python files.

---

## 16. `_SELD`'s per-line loop (re-check finding P1; FORMATS.md 6.9)

The re-check found that `_SELD` runs a loop over the pending `_SELR` lines before it decodes its
target (4.5 only named it), and that the harness of sections 4-15 never ran it: it restored the
eboot image before every statement, so the line count 0x1750646 was always 0 when `_SELD` ran.
All 170 retail `_SELD` have 1..10 pending lines. This section documents the loop from emulation
**with pending lines**: the engine's own Script_Step runs whole retail choice blocks
(`_ATSV(1)`, the `_SELR` lines with their `_IF__` / `_GOTO` / marker / `_PGST` statements,
`_SELD`) with the state carried from statement to statement, and the real History_Push code
makes the records. Python: `tools/tsuki/misc_seld.py` (model and harness; `tsuki.vm_vars` is
unchanged). Tests: `tools/tests/test_misc_seld.py`. Scratch scripts and logs: `scratch/seld_loop/`.

### 16.1 The loop [EMU, E: VA 0x9B215..0x9B430]

`_SELD` (handler 0x9B1DC) runs, in this order: the scan-mode exit (yield, nothing else), mode =
1, the scene-jump end (0x7E620 and word 0x120A6D2 = 0, only when that word is set), **the
per-line loop**, then the target / id / flowchart / layout / UI part of 4.5.

```
count = s16 u16[0x1750646]                    # 0x9B215; count <= 0: no loop, F799 untouched
for k = 0, 1, ... while k < s16 count (count re-read every iteration, 0x9B247):
    F[799] = (k == count - 1) ? 1 : 0         # 0x9B273..0x9B281, 16-bit store
    History_Snapshot(end, end, 12)            # 0x9B288. rdi = rsi = rbx = the byte after the
                                              # statement's ';'. ecx (= the F799 value) is set
                                              # too, but History_Push never reads rcx before
                                              # overwriting it (0x7AFE5). Gate as for every push
                                              # (4.6): scan mode, F929, F1102 (kind 12 is not
                                              # 0 / 11), byte 0x16541F4
    if byte 0x174D9A8 == 0:                   # 0x9B28D; History_Push clears this byte
        slot = 0x174FB70 + 0xFA * k           # the _SELR line slot (16.3)
        rec  = ring[s16 0x120A6EC]            # the record just pushed or, when the gate was
                                              # shut, the newest older record
        0x174D9B0[0 .. 0xFA) = slot[0 .. 0xFA)            # current message text buffer
        rec[+0x38FE .. +0x39F6) = slot[0 .. 0xF8); u16 rec[+0x39F6] = 0
        if u16 rec[+0x3638] == 0 and u16 rec[+0x24] not in (11, 12) and s16 F[823] <= 1:
            top = s16 word 0x164B1C0                      # scene-stack top
            Staging_SetSnapshot(rec, top + 1, top >= 0 ? &stack[top] : 0,
                                top > 0 ? &stack[top - 1] : 0)    # 0x73980 (adv_state.md 7.1)
        dword 0xDF29D4 = 0                    # 0x9B23A (16.6)
```

The loop changes neither the count nor the values nor the slots. The copy-and-staging block is
the same as in `_ATSV` (0x98907..0x98A22), with the line slot as the text source.

### 16.2 What it leaves behind [EMU]

With the gate open (every retail choice, 16.5) the loop pushes **one kind-12 history record per
pending line**; the newest is the last line:

| record field | value |
|---|---|
| kind (+0x24) | 12 |
| cmd_offset (+0x08), resume_offset (+0x10) | both the end of the `_SELD` statement (where the `_GOTS` starts). A restore of such a record does not run the `_SELD` again (16.6) |
| F799 copy (s8 list slot 9, +0x350A) | 0, ..., 0, 1 (1 only for the last line) |
| text (+0x38FE) | line slot k, e.g. `$043960$042569` ("1. " + the choice text); +0x39F6 = 0 |
| everything else | as for every push (adv_state.md 2.2), taken after the F799 write |

After the loop: F799 = 1, text buffer 0x174D9B0 = the last line's slot, 0x174D9A8 = 0, dword
0xDF29D4 = 0, and no Staging_SetSnapshot call, because the record it would pass has kind 12.

Other states (all emulated, 16.10):

| state when `_SELD` runs | pushes | text copied into | Staging_SetSnapshot | 0xDF29D4 |
|---|---|---|---|---|
| gate open (retail) | 1 per line, kind 12 | each pushed record and the text buffer | never | 0 |
| gate shut (F929 != 0, F1102 != 0 or byte 0x16541F4 != 0) | none | the **newest existing record** (any kind), overwritten once per line, so it ends with the last line's text; text buffer = last line | once per line, when that record has no blocking effect bits, its kind is not 11 / 12 and F823 <= 1: `(rec, top + 1, stack[top] or 0, stack[top - 1] or 0)` | 0 |
| byte 0x174D9A8 = 1 (a rewind re-displayed a line) and gate shut | none | nothing | never | unchanged |
| byte 0x174D9A8 = 1 and gate open | the first push clears the byte; then as "gate open" | | | |
| scan mode | the loop is not reached (yield at 0x9B1E3) | | | |
| count <= 0 | no loop, F799 unchanged | | | |

F799 is written whenever the loop runs, whatever the gate.

### 16.3 The line slots [EMU + E]

`_SELR` writes slot k = 0x174FB70 + 0xFA * k (k = the count before the line; 11 slots fit before
the value table 0x1750630): `"$"` + the 6 digits of line 43960 + k (k = 0..8) or 43959 (k >= 9)
+ `strcat` of argument 1 as VA 0x76EA0 copies it (tag skipped, bytes up to `)` `,` `;`, a
missing argument is empty); system_flow.md 5.1 [E]. Emulated: every slot of every retail run
(2,437 lines) and the forms `` `001:$000123 ``, missing, empty and locked and the 10th / 11th
line (`$043959...`) [EMU]. Bytes after the NUL keep their previous contents and the record copy
takes 0xF8 bytes, so a port only needs the C string (at most 0xF7 characters survive in the
record).

### 16.4 Worked example: script 4, first choice [EMU]

```
_SHS2(-1);_SSS2(-1);_RDED(1);_ATSV(1);_SELR(0,$042569);_SELR(1,$042570);_SELR(2,$042571);
_SELD(_D000,1);_GOTS(_D000,SEL0_0,SEL0_1,SEL0_2);
```

Run from `_ATSV(1)` with 8 earlier records in the ring (the newest, index 7, holds the
question), F823 = 3 (linear scan), gate open, scene-stack top 0 (`python -m tsuki.misc_seld
example`, `scratch/seld_loop/example.log`; engine and model identical):

| ring | pushed by | kind | cmd / resume | F799 copy | text |
|---|---|---|---|---|---|
| 8 | `_ATSV(1)` (0x97EA4) | 7 | 46577 / 46587 | 0 | the question (text buffer) |
| 9 | first `_SELR` (0x993A0) | 13 | 46586 / 46604 | 0 | (empty) |
| 10 | `_SELD`, line 0 | 12 | 46653 / 46653 | 0 | `$043960$042569` |
| 11 | `_SELD`, line 1 | 12 | 46653 / 46653 | 0 | `$043961$042570` |
| 12 | `_SELD`, line 2 | 12 | 46653 / 46653 | 1 | `$043962$042571` |

Variable log of the `_SELD` statement (16-bit accesses to var_F..var_G and the scene-stack top,
History_Push's body left out): `w F799 = 0, r F929, r F1102, w F799 = 0, r F929, r F1102, w F799
= 1, r F929, r F1102`. Afterwards F799 = 1, count = 3, text buffer = `$043962$042571`, 0xDF29D4
= 0, and Staging_SetSnapshot was not called (nor by the `_ATSV(1)`, because F823 = 3 > 1).
Section 8's example of this choice lacks the three F799 writes and the three kind-12 records.

### 16.5 Retail census [V-all, EMU]

`python -m tsuki.misc_seld census` (model walk) and `retail` (engine vs model):

- 170 `_SELD` blocks. 83 are entered through `_ATSV(1)` (all 83 `_ATSV(1)` of the scripts stand
  directly before a choice block). 7 have `_IF__` / `_GOTO` around the lines (scripts 10, 43, 70,
  211, 361, 433, 492), so their line count depends on route flags.
- Pending lines with the `_IF__` variables all 0: 1 line in 1 block, 2 lines in 69, 3: 35, 4: 2,
  5: 3, 6: 7, 7: 4, 8: 4, 9: 5, 10: 40 (814 lines; 812 with the variables all 1, 811 with all 5).
  Every block has at least one line.
- Engine vs model, gate open, every block with the `_IF__` variables 0, 1 and 5: 510 runs, 2,437
  loop iterations, every one pushed as kind 12, **0 Staging_SetSnapshot calls from `_SELD`**, 0
  mismatches (`scratch/seld_loop/retail_gate_open.json`).
- Every block under the 17 scenarios of `FLAG_SCENARIOS` (gate shut by F929 / F1102 / the
  message-continues byte, the re-display flag with the gate open and shut, a blocking / kind-11 /
  kind-12 newest record, F823 = -1 / 0 / 1 / 2 with scene-stack tops -1 / 0 / 3, effect slots
  blocking, scan mode, scene jump): 2,798 runs (the scene-jump scenario only on the 78 blocks
  without a marker on the walk, 16.13), 12,403 loop iterations, 3,449 kind-12 pushes, 4,362
  Staging_SetSnapshot calls from `_SELD` (all in gate-shut scenarios), 0 mismatches
  (`retail_flags.json`).

So in retail play the loop always takes the gate-open row of 16.2. The gate-shut rows need F929,
F1102 or the message-continues byte set at a choice; no retail choice block sets them, but they
are engine-written [H: not set at a choice in play].

### 16.6 Consequences for the rest of the engine

- **Backlog** (adv_state.md 5.4). `scratch/seld_loop/backlog_demo.py` runs Backlog_Open
  (0x3BFB0) under unicorn on the ring that the engine's own `_SELD` made for script 4 (records
  0..3 a previous page whose last message has F799 = 1, records 4..7 the question page) [EMU]:
  - while the choice is on screen the list is the previous page only (records 3, 2, 1, 0): the
    question page and the choice lines count as the current page;
  - after the next page's first message (a kind-0 push through History_Push) the list is the
    three choice lines (12, 11, 10, newest first: "3.", "2.", "1."), then the question page (7..4),
    then the previous page. The kind-7 and kind-13 records are not listed (only kinds 0, 8, 11,
    12 are);
  - with the kind-12 records' F799 copies forced to 0, the same backlog drops the question page
    and the choice lines again (the list starts at record 3). **The F799 = 1 of the last line is
    what closes a choice as a page of the backlog.**
  - A backlog jump to a kind-12 entry restores the record before it, which must be kind 13
    (0x3CD16..0x3CD38, adv_state.md 5.5) [E]. Only line 0's record directly follows the kind-13
    `_SELR` record, so only that entry can be jumped to (the restore reruns the `_SELR` lines and
    the `_SELD`); the other lines' entries end in the error sound 0xCC670(6) [E; whether the log
    screen offers them is UI work, 16.13].
- **Rewind** (adv_state.md 5.1): the kind-12 records are undone like any other on the way back
  to the previous page's first message. History_Restore sets the line count to 0 at its entry
  (0x7C717) and restores F799 from each record [E].
- **Messages and auto mode**: the F799 = 1 that `_SELD` leaves never reaches a message in the
  retail scripts. Each of the 42,590 `_ZM` statements has a `_WNTY` since the previous `_ZM` in
  linear order (`wnty_before_zm.py`), and on the 812 branches after a `_SELD` + `_GOTS` the first
  relevant statement is `_JUMP` (730), another `_SELD` (71), `_TOMP` (6), `_WNTY` (4) or `_RTM_`
  (1) (`after_choice.py`) [V-all]. While the choice is up F799 = 1 would select the long auto
  wait (config +0x9E), but auto mode does not decide a choice.
- **Saving**: in retail the loop never calls Staging_SetSnapshot, so the slot staging keeps the
  page-start record and the scene start. The `_ATSV(1)` before a choice does call it when F823 <=
  1 (0x989CB) [EMU].
- **dword 0xDF29D4**: set to 1 by the new-game / script-start routine 0x60500 (0x605D1), set to 0
  by every text copy (`_ZM` 0x92ACF, `_MSAD` 0x93052, `_ATSV` 0x98A1B / 0x98B72, `_SELD`
  0x9B23A, 0x7B5C0) and by the load paths (0x6069F, 0xA3268). The ADV input handler 0x60710
  skips the start of action 20 (rewind) while it is != 0 (0x61775) [E; H: "nothing to rewind
  yet"].

### 16.7 Line-count resets (correction of FORMATS.md 6.9, third bullet) [E]

u16 0x1750646 is set to 0 by the choice UI (0xA6518, 0xA6A4A, 0xA6BDF) **and** by History_Restore
at its entry (0x7C717: every rewind step, backlog jump and slot-load restore), by 0x7E790
(0x7EA01: slot load, previous scene), by the script reset 0x789F0 (0x7915A) and by Slot_Load
0xA2D10 (0xA2D2B). `_SELS` / `_SELA` set it to 2 (4.5). No other instruction addresses it
(rip-relative scan of all functions, `scratch/seld_loop/xref.log`). `_SELD` and the result
stores do not reset it, so two `_SELR` groups without a decided choice between them make the
second `_SELD` loop over the lines of both groups (synthetic case "two groups"; engine and model
agree).

### 16.8 Found on the way [EMU]

- `_PGST(n)` (handler 0x9637E) writes **F1122 = n** (VA 0x97852) and nothing else in the variable
  window. All 1,014 distinct retail `_PGST` statements (17,202 uses) were run through the engine:
  the value written equals the inline reader's value (`pgst_check.py`). In scan mode it writes
  nothing and compares n with the scan target dword 0x873040 (0x9687D..0x96897; -1 in the eboot
  image) [E, EMU for "no match"]. F1122 is thus the current page number (script.md: `_PGST(10000
  + n)` after `LBL_n`), which answers part of open question 14.4; the slot load reads it at
  0xA34C2 [E; H: the scan target].
- Every `_ZY` / `_ZZ` marker outside scan mode reads F991 (the pending-layer-change test of the
  kind-9 commit point, adv_state.md 2.3); with F991 = 0 that is its only variable access [EMU].
  During a scene jump it reads F998 first (0x9670F, the next-scene stop test, adv_state.md 5.2;
  seen in the engine logs of a first scene-jump run, not modelled); in scan mode it reads no
  variable [EMU].

### 16.9 Model (`tsuki.misc_seld`) and what a port implements

```python
from tsuki import misc_seld as ms
ctx = ms.model_context(ms.Scenario(f={823: 3}))           # vm_vars.VarState + ring + globals
buf = b";_SELR(0,$042569);_SELR(1,$042570);_SELD(_D000,1);"
steps = ms.walk(ctx, buf, 1, labels={})                  # runs up to the _SELD
steps[-1].lines   # [LineEvent(k=0, f799=0, pushed=True, record=9, text=b'$043960$042569', ...), ...]
ctx.ring.pushed   # kind 13, then kind 12 per line, with offsets, F799 copies and texts
```

`seld_line_loop(ctx, stmt_end)` is the loop. `seld`, `selr` and `atsv_choice_point` wrap
`vm_vars.execute` and add the ring, the slots and the globals. `walk` runs a block (`_ATSV(1)`,
`_SELR`, `_SELD`, `_IF__`, `_GOTO`, `_WK*`, markers, `_PGST`). The `vm_vars` outputs are
unchanged: for `_SELD` the `Effect` and the choice state are exactly what `vm_vars.execute`
returns (test `test_count_not_reset_and_vm_vars_unchanged`).

A port (C#) runs this inside its `_SELD` handler, between the scene-jump end and the target
decode:

```
void SeldLineLoop(int stmtEnd) {
    for (int k = 0; k < (short)choiceCount; k++) {
        F[799] = (ushort)(k == (short)choiceCount - 1 ? 1 : 0);
        history.Push(cmd: stmtEnd, resume: stmtEnd, kind: 12);   // the gate of every push
        if (!redisplayed) {                                      // byte 0x174D9A8; Push clears it
            var rec = history.Current;
            currentText = lineSlot[k];                           // C string
            rec.Text = Truncate(lineSlot[k], 0xF7);
            if (rec.BlockingBits == 0 && rec.Kind != 11 && rec.Kind != 12 && (short)F[823] <= 1)
                staging.SetSnapshot(rec, sceneTop + 1, sceneTop >= 0 ? sceneStack[sceneTop] : null);
            rewindBlocked = false;                               // dword 0xDF29D4
        }
    }
}
```

The backlog must list the kind-12 records and group pages by the records' F799 copies
(adv_state.md 5.4); that is what makes the offered choices a page of the log once the story
moves on.

### 16.10 Evidence (commands run for this section)

All with `PYTHONPATH=tools` and the project venv; logs in `scratch/seld_loop/`.

- Disassembly (capstone): `seld_handler.asm` (0x9B1DC..0x9B435, 0x9B53F..0x9B74F),
  `selr_handler.asm` (0x9936D..0x9952F), `related.asm` (0x7C6E1.., 0x7E9E3.., 0x79112..,
  0xA2D1F.., the History_Push gate, Backlog_Open, 0x61758..), `script_step.asm` (Script_Step).
- `xref.py DF29D4 174D9A8 164B80E 1750646` (rip-relative references, function by function):
  `xref.log`.
- `python -m tsuki.misc_seld example`: 16.4, engine = model (`example.log`).
- `python -m tsuki.misc_seld retail` (gate open, `_IF__` variables 0 / 1 / 5): 510 runs, 2,437
  lines, 0 mismatches (`retail_gate_open.json`).
- `python -m tsuki.misc_seld retail --flags all --assigns 0`: 2,798 runs, 0 mismatches
  (`retail_flags.json`; 92 scene-jump runs of blocks with markers skipped).
- `python -m tsuki.misc_seld synthetic`: 16 cases (one line, no line, 11 lines, argument forms,
  two groups, gate shut by F929 / F1102 / the message-continues byte with F823 and scene-top
  variants, re-display flag with the gate shut and open, scene jump, scan mode, effect bits,
  `_ATSV(1)` context and gated, ring wrap at index 799), all identical (`synthetic.log`).
- `python -m tsuki.misc_seld census`: 16.5 (`census_model.log`); `census.py`: block contexts
  (`census.log`).
- `backlog_demo.py`: the backlog lists of 16.6 (`backlog_demo.log`).
- `wnty_before_zm.py`, `after_choice.py`: where the F799 of 16.6 can reach (`*.log`).
- `pgst_check.py`: 1,014 `_PGST` forms, 0 mismatches (`pgst_check.log`).

Compared in every run (engine = Script_Step 0x95E60 under unicorn through
`tsuki.misc_adv_system_state_emu.AdvEmu`, whose VEX instructions run in Python, so the ymm copies
of the loop really execute; model = `tsuki.misc_seld.walk`): the sequence of statements; per
statement every 16-bit read and write in 0x164B1C0..0x164D5C0 in program order (History_Push's
body, which only reads the F lists, left out); every History_Push call that passed its gate,
with its two offsets and kind; serial, kind, offsets, F799 copy, text, +0x39F6 and blocking bits
of every record; the ring indices; the line slots; the text buffer; 0x174D9A8; 0xDF29D4; every
Staging_SetSnapshot call (record, n, both scene-stack pointers, calling command); count, values,
locked flags, mode, F799, scene-stack top, scene-jump word and the 0x7E620 calls. Stubbed in the
engine run: the effect-slot tests 0x5C660 / 0x5C680 (scenario bits), 0x53620, `_SELD`'s layout,
flowchart and UI calls (as in 4.5), 0x67460, 0x73B70, Staging_SetSnapshot (arguments logged; its
copy is adv_state.md 7.1), SceneStack_Push (done in Python: top += 1 and a copy of ring[cur]
unless an effect slot blocks, adv_state.md 5.3; its own code uses `vpackssdw`, which the VEX
interpreter lacks) and the label lookup 0x794D0 (label-table offset).

### 16.11 Tests

`tools/tests/test_misc_seld.py`: 11 tests, about 20 s with the game. Model tests: slot text,
gate open / shut / re-display / scan, count not reset, vm_vars unchanged, `_PGST`. Engine tests:
all synthetic cases, the worked example, every 12th retail block x `_IF__` 0 / 1 x 4 flag
scenarios (120 runs), the 7 conditional blocks x 0 / 1 / 5. Census test. Engine and corpus tests
skip without the game, eboot.bin or unicorn / capstone. Nothing is written to disk.

### 16.12 Errata

For this document:

- 4.5: the loop is VA 0x9B215..0x9B430 (0x9B228..0x9B25F is only its set-up and loop test); it
  writes F799 and pushes the kind-12 records (16.1).
- 8 (script 4 example): add the three F799 writes and the three kind-12 records (16.4).
- 14 item 4: F1122 is the `_PGST` page number (16.8).

For other documents (not edited here):

- FORMATS.md 6.9, third bullet ("returns to 0 only in the choice UI"): History_Restore, 0x7E790,
  the script reset and Slot_Load reset it too (16.7). The first bullet is answered by this
  section.
- adv_state.md 2.3, kind table row 12 "SELD (choice start, Script_Step 0x9B288)": one kind-12
  record **per pending `_SELR` line**, cmd = resume = the end of the `_SELD` statement, text = the
  line slot, F799 copy 1 on the last line (16.2). 2.1, row +0x38FE: the `_SELD` loop also copies
  the line slot there. 7.1: `_SELD`'s Staging_SetSnapshot call (0x9B42B) happens only when its
  push was gated (a pushed kind-12 record is never staged).
- RUNTIME_REQUIREMENTS.md, "Not yet in the model (FORMATS 6.9)": now modelled in
  `tsuki.misc_seld`; "the choice UI resets the line count when a choice is decided" is
  incomplete (16.7).
- textbox.md 8.3 "F799 = 1 marks the last message of a page": also the last line of a choice,
  written by `_SELD` (16.1).

### 16.13 Open questions

1. Whether the log screen (0x3CE90) shows each kind-12 entry as its own line or merges a choice
   group, and what it does with the entries whose jump fails (16.6).
2. The run-time values of F929 / F1102 / the message-continues byte at a choice (engine-set; the
   gate-shut rows of 16.2 are emulated, not observed in play).
3. Who calls 0x7AF00 (the other writer of 0x174D9A8 = 1), and the exact meaning of dword
   0xDF29D4.
4. Markers during a scene jump (their skip-end tests) are not modelled; the retail "scene jump"
   scenario therefore covers only the blocks without markers on the walk.
