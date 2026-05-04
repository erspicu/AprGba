# Phase 4.5C: LR35902 bit-pattern grouping table

> Phase 4.5 has completed the reference implementation (`LegacyCpu`
> interpreter, 256 opcodes) and passed Blargg `cpu_instrs` 11/11. The
> work of Phase 4.5C is to write the same ISA into `spec/lr35902/`
> using bit-pattern grouping, reusing the SpecCompiler proven on
> ARM7TDMI, producing the `JsonCpu` backend, and obtaining the same
> result on the same ROM.
>
> This document is the design rationale for that spec structure.

---

## Why group instead of 256-case

`LegacyCpu.Step.cs` has 1900+ lines, one case per instruction; this is
a reasonable shape for a reference implementation (faithfulness >
elegance, easy bug-for-bug comparison). But it doesn't suit the
framework's JSON spec:

- Lots of duplicated code. `LD r,r'` has 49 cases, ALU
  `ADD/ADC/.../CP` has 64 cases, all rewriting the same thing
- Fixing one bug requires changes in many places
- Cannot reuse the SpecCompiler / encoding decision path validated on
  ARM7TDMI

LR35902's encoding is even more regular than ARM, with almost everything
fitting into four blocks:

```
00 xxx xxx — block 0: misc / 16-bit ops / load imm / inc/dec / control
01 ddd sss — block 1: LD r, r'         (ddd=110/sss=110 is the HALT exception)
10 ooo sss — block 2: ALU A, r         (ooo: ADD/ADC/SUB/SBC/AND/XOR/OR/CP)
11 xxx xxx — block 3: jump/call/ret/push/pop/ALU imm/RST/CB-prefix
```

Just block 1 + block 2 already covers 113 cases with two patterns + one
register lookup table. The remaining block 0 / block 3 are split into
several sub-groups plus a few truly-irregular special cases. Estimated
total group count is **~20**, more compact than ARM7TDMI's 13 ARM groups
+ 19 Thumb formats.

---

## Block 1: `01 ddd sss` LD r, r'

**Pattern**: `01_ddd_sss`, mask `0xC0`, match `0x40`. Exception:
`01_110_110` (0x76) = HALT, must be excluded in the mask or handled as
a special case in the instructions selector.

**reg index mapping** (`ddd` / `sss` shared):

| Encoding | Register |
|---|---|
| 000 | B |
| 001 | C |
| 010 | D |
| 011 | E |
| 100 | H |
| 101 | L |
| 110 | (HL) (memory indirect) |
| 111 | A |

**Instruction count**: `8 x 8 - 1 (HALT) = 63`, all sharing the same
step list (`read_r8(sss)` -> `write_r8(ddd)`), differing only in reg
lookup.

Filename: `spec/lr35902/groups/block1-ld-reg-reg.json`

---

## Block 2: `10 ooo sss` ALU A, r

**Pattern**: `10_ooo_sss`, mask `0xC0`, match `0x80`.

**ALU op mapping** (`ooo`):

| Encoding | Mnemonic | Description |
|---|---|---|
| 000 | ADD A, r | A <- A + r |
| 001 | ADC A, r | A <- A + r + C |
| 010 | SUB r    | A <- A - r |
| 011 | SBC A, r | A <- A - r - C |
| 100 | AND r    | A <- A & r |
| 101 | XOR r    | A <- A ^ r |
| 110 | OR r     | A <- A \| r |
| 111 | CP r     | flags <- A - r (A not written back) |

`sss` uses the same reg lookup as block 1 (including (HL) and immediate
in block 3's `11_ooo_110`).

**Instruction count**: 8 x 8 = 64, sharing a step list shaped like
`alu_op8(A, src, op_kind)`, differing only in ALU op and source reg.
Flag setting is also table-driven (each op's effect on Z/N/H/C is fixed
by rule).

Filename: `spec/lr35902/groups/block2-alu-reg.json`

---

## Block 3: ALU A, imm8 — `11 ooo 110`

**Pattern**: `11_ooo_110`, mask `0xC7`, match `0xC6`.

Same ALU op table as block 2; the only difference is the source is a
fetched imm8 rather than a reg. Ideally should share the step list with
block 2, swapping only the source operand resolver — `operands.src.kind:
"imm8"` vs `"reg8_by_field"`.

**Instruction count**: 8.

Filename: `spec/lr35902/groups/block3-alu-imm8.json`

---

## Block 0: 8-bit immediate load — `00 ddd 110`

**Pattern**: `00_ddd_110`, mask `0xC7`, match `0x06`.

`LD r, n` (including `LD (HL), n`). 8 entries.

Filename: `spec/lr35902/groups/block0-ld-r8-imm8.json`

---

## Block 0: 8-bit INC / DEC — `00 ddd 10x`

**Pattern**: `00_ddd_10c`, c=0/1:

- `00_ddd_100` = INC r
- `00_ddd_101` = DEC r

16 entries (including (HL)). INC does not affect C flag; DEC similar.
H flag must be computed in both.

Filename: `spec/lr35902/groups/block0-inc-dec-r8.json`

---

## Block 0: 16-bit immediate load — `00 dd0 001`

**Pattern**: `00_dd0_001`, mask `0xCF`, match `0x01`.

| dd | Target |
|---|---|
| 00 | BC |
| 01 | DE |
| 10 | HL |
| 11 | SP |

`LD rr, nn`, 4 entries. Validates paired register schema.

Filename: `spec/lr35902/groups/block0-ld-rr-imm16.json`

---

## Block 0: 16-bit INC / DEC / ADD HL,rr — `00 dd? 011/001`

Three small groups, but the register field encoding is consistent:

- `00_dd0_011` = INC rr     (4 entries)
- `00_dd1_011` = DEC rr     (4 entries)
- `00_dd1_001` = ADD HL, rr (4 entries; affects H/C, Z unchanged, N=0)

Filename: `spec/lr35902/groups/block0-alu-rr.json`

---

## Block 3: conditional / unconditional jump — JP/JR/CALL/RET

**JP**:

- `1100_0011` = JP nn          (unconditional)
- `11_0cc_010` = JP cc, nn     (conditional, cc = NZ/Z/NC/C)
- `1110_1001` = JP HL

**JR**:

- `0001_1000` = JR e8          (unconditional)
- `00_1cc_000` = JR cc, e8     (conditional)

**CALL**:

- `1100_1101` = CALL nn
- `11_0cc_100` = CALL cc, nn

**RET / RETI**:

- `1100_1001` = RET
- `1101_1001` = RETI
- `11_0cc_000` = RET cc

The condition bits `cc` (2 bits) map:

| cc | flag |
|---|---|
| 00 | NZ (Z=0) |
| 01 | Z  (Z=1) |
| 10 | NC (C=0) |
| 11 | C  (C=1) |

Reasonable split: one group per verb (`block3-jp.json` /
`block3-jr.json` / `block3-call.json` / `block3-ret.json`); conditional
and unconditional variants share the step list, distinguished by
selector.

---

## Block 3: PUSH / POP — `11 qq0 0p1`

**Pattern**: `11_qq0_0p1`, p=1=PUSH, p=0=POP (encoding direction needs
to be re-verified).

| qq | Pair |
|---|---|
| 00 | BC |
| 01 | DE |
| 10 | HL |
| 11 | AF |

POP AF has to mask out the low 4 bits of F (GB's F register forces low
nibble = 0).

8 entries. Filename: `spec/lr35902/groups/block3-push-pop.json`

---

## Block 3: RST — `11 ttt 111`

**Pattern**: `11_ttt_111`, mask `0xC7`, match `0xC7`.

`RST` jumps to `ttt x 8` = 0x00 / 0x08 / 0x10 / 0x18 / 0x20 / 0x28 /
0x30 / 0x38. 8 entries.

Filename: `spec/lr35902/groups/block3-rst.json`

---

## CB-prefix: bit-manipulation

Opcode 0xCB is a prefix — the next fetched byte is in another 256-entry
space. This is the only place in the entire GB ISA that needs the
multi-instruction-set mechanism (or an equivalent "prefix-byte enters
sub-format" design).

**CB byte structure**: `oo_bbb_sss`

| oo  | Action |
|---|---|
| 00 | shift/rotate (bbb further distinguishes 8 kinds) |
| 01 | BIT b, r — Z = !(r >> b & 1) |
| 10 | RES b, r — r <- r & ~(1 << b) |
| 11 | SET b, r — r <- r \| (1 << b) |

Shift/rotate sub-division (`oo=00`, bbb encodes the op):

| bbb | op |
|---|---|
| 000 | RLC |
| 001 | RRC |
| 010 | RL  |
| 011 | RR  |
| 100 | SLA |
| 101 | SRA |
| 110 | SWAP |
| 111 | SRL |

`sss` uses the same reg lookup as the main set (including (HL)).

**Group split**:

- `cb-prefix-shift.json` — CB 00 ooo sss (64 entries)
- `cb-prefix-bit.json`   — CB 01 bbb sss (64 entries)
- `cb-prefix-res.json`   — CB 10 bbb sss (64 entries)
- `cb-prefix-set.json`   — CB 11 bbb sss (64 entries)

The whole CB 256-byte space = 4 groups x 64 entries.

---

## The truly irregular ones ("irregular" group)

The following opcodes don't fit any of the above patterns and are
listed independently:

| opcode | Mnemonic | Note |
|---|---|---|
| 0x00 | NOP | |
| 0x07 | RLCA | block 0's `00_xxx_111` line, but with too many op variations, listing independently is cleaner |
| 0x0F | RRCA | same as above |
| 0x17 | RLA  | same as above |
| 0x1F | RRA  | same as above |
| 0x27 | DAA  | half-carry/N/C logic special |
| 0x2F | CPL  | A <- ~A |
| 0x37 | SCF  | C <- 1 |
| 0x3F | CCF  | C <- !C |
| 0x10 | STOP | two bytes (followed by 0x00) |
| 0x76 | HALT | block 1's `01_110_110` exception |
| 0xF3 | DI   | |
| 0xFB | EI   | delayed by one instruction |
| 0xE0 | LDH (n), A     | (0xFF00 + n) <- A |
| 0xF0 | LDH A, (n)     | A <- (0xFF00 + n) |
| 0xE2 | LD (C), A      | (0xFF00 + C) <- A |
| 0xF2 | LD A, (C)      | A <- (0xFF00 + C) |
| 0xEA | LD (nn), A     | |
| 0xFA | LD A, (nn)     | |
| 0x08 | LD (nn), SP    | |
| 0xF8 | LD HL, SP+e8   | flag effect special |
| 0xF9 | LD SP, HL      | |
| 0xE8 | ADD SP, e8     | flag effect special |
| 0x02 | LD (BC), A     | |
| 0x12 | LD (DE), A     | |
| 0x22 | LD (HL+), A    | post-increment |
| 0x32 | LD (HL-), A    | post-decrement |
| 0x0A | LD A, (BC)     | |
| 0x1A | LD A, (DE)     | |
| 0x2A | LD A, (HL+)    | |
| 0x3A | LD A, (HL-)    | |

**~30** irregular opcodes total. Options:

- Stuff them all into one `irregular.json` group (30 instructions, each
  with its own pattern)
- Or split into two or three smaller groups: `misc-control.json` (NOP/
  STOP/HALT/DI/EI/DAA/CPL/...), `ldh-and-mem.json` (high-memory access),
  `stack-arith.json` (SP-related)

---

## Final file structure proposal

```
spec/lr35902/
  cpu.json                          — top-level: register_file (with paired),
                                       processor_modes (GB has no mode, omitted),
                                       instruction_set_dispatch (CB prefix)
  groups/
    block0-ld-r8-imm8.json          — 8 entries
    block0-ld-rr-imm16.json         — 4 entries
    block0-inc-dec-r8.json          — 16 entries
    block0-alu-rr.json              — 12 entries (INC/DEC/ADD HL,rr)
    block0-misc-control.json        — NOP/RLCA/RRCA/RLA/RRA/DAA/CPL/SCF/CCF/STOP (10 entries)
    block0-mem-indirect.json        — LD (BC)/(DE)/(HL+)/(HL-) <-> A (8 entries)
    block0-ld-nn-sp.json            — 1 entry
    block1-ld-reg-reg.json          — 63 entries (HALT excluded)
    block1-halt.json                — 1 entry (with HALT-bug note)
    block2-alu-reg.json             — 64 entries
    block3-alu-imm8.json            — 8 entries
    block3-jp.json                  — 6 entries
    block3-jr.json                  — 5 entries
    block3-call.json                — 5 entries
    block3-ret.json                 — 7 entries (RET/RETI/RET cc)
    block3-push-pop.json            — 8 entries
    block3-rst.json                 — 8 entries
    block3-ldh-mem-high.json        — LDH (n)/(C) <-> A, LD (nn) <-> A (6 entries)
    block3-stack-arith.json         — LD HL,SP+e8 / LD SP,HL / ADD SP,e8 (3 entries)
    block3-di-ei.json               — 2 entries
    cb-prefix-shift.json            — 64 entries
    cb-prefix-bit.json              — 64 entries
    cb-prefix-res.json              — 64 entries
    cb-prefix-set.json              — 64 entries
```

**Total**: ~24 group files, ~506 instruction entries (main 256 + CB
256, minus 11 unused opcodes). Compared to the 1900-line big-switch,
spec volume is estimated at 40-60% depending on step list reuse.

---

## Comparison with ARM7TDMI groups

| Dimension | ARM7TDMI | LR35902 |
|---|---|---|
| Number of instruction sets | 2 (ARM + Thumb) | 2 (main + CB) |
| Switch mechanism | CPSR.T persistent state | 0xCB prefix byte (momentary) |
| Group count | 13 ARM + 19 Thumb = 32 | ~24 |
| Encoding orthogonality | ARM high, Thumb medium | very high (block 1/2 close to 100%) |
| Operand resolver kinds | shifted_register, imm_rotated, ... | reg8_by_field, imm8, imm16, reg_pair_by_field, ... |
| Flag micro-ops | update_nzcv, update_carry_from_shifter | update_znhc_add, update_znhc_sub, update_h_add16, ... |
| Half-carry | N/A | new requirement (micro-op or emitter helper) |

---

## Expected framework extension points (consolidated from 09 + this design)

1. **`register_file.register_pairs`** — schema + Loader + new micro-ops
   `read_reg_pair` / `write_reg_pair`
2. **`F register low_nibble_zero` invariant** — schema field + auto-mask
   on write
3. **Half-carry micro-ops** — `update_h_add` / `update_h_sub` /
   `update_h_add16`, in `Lr35902Emitters` (mirroring `ArmEmitters`)
4. **Prefix-byte instruction-set transition** — `0xCB` in main set
   triggers single-instruction switch to CB set. Fallback: treat CB as a
   256-entry sub-format inside main set
5. **Variable-width within the same set** — `width_decision` for
   1/2/3-byte main-set instructions actually exercised

---

## Progress

| Step | Status |
|---|---|
| 1. `cpu.json` skeleton (register_file with pairs, F/SP/PC, 5 IRQ vectors, Main+CB dispatch) | Done |
| 2. block 1 (LD r,r' + HALT, 3 formats, priority correctly handles 0x76) | Done |
| 2. block 2 (ALU A,r, 2 formats, 64 instructions, (HL)/reg cycle distinction) | Done |
| 3. block 0 all (ld_r8_imm8 / inc_dec_r8 / ld_rr_imm16 / alu_rr / mem_indirect / misc_control / jr / ld_nn_sp) | Done |
| 4. block 3 all (jp / call / ret / rst / push_pop / ldh_mem_high / stack_arith / di_ei / cb_prefix / alu_imm8) | Done |
| 5. CB-prefix 4 groups (shift / bit / res / set, all 256 entries) | Done |
| **JSON side complete**: 245 main + 256 CB = **all 501 opcodes decode through** | Done |
| 6. IR emitters translating `lr35902_*` micro-ops to LLVM IR | Pending — next round |
| 7. `JsonCpu` backend wired up -> run Blargg `01-special.gb` against `LegacyCpu` | Pending |
| 8. All 11 sub-tests cross-checked -> Phase 4.5C done | Pending |

## Actual file structure (consistent with original plan)

```
spec/lr35902/
  cpu.json
  main.json
  cb.json
  groups/
    block0-misc-control.json     — NOP/STOP/RLCA/RRCA/RLA/RRA/DAA/CPL/SCF/CCF (10 entries)
    block0-jr.json               — JR e8 + 4 JR cc,e8 (5 entries)
    block0-ld-nn-sp.json         — LD (nn),SP (1 entry)
    block0-mem-indirect.json     — LD (BC)/(DE)/(HL+)/(HL-) <-> A (8 entries)
    block0-ld-rr-imm16.json      — LD rr,nn (4 entries)
    block0-alu-rr.json           — INC rr / DEC rr / ADD HL,rr (12 entries)
    block0-inc-dec-r8.json       — INC r / DEC r (16 entries)
    block0-ld-r8-imm8.json       — LD r,n (8 entries)
    block1-halt.json             — HALT (1 entry, priority above LD r,r')
    block1-ld-reg-reg.json       — LD r,r' (63 entries, 3 formats with (HL) split)
    block2-alu-reg.json          — ALU A,r (64 entries, 2 formats with (HL) split)
    block3-jp.json               — JP nn / JP cc nn / JP HL (6 entries)
    block3-call.json             — CALL nn / CALL cc nn (5 entries)
    block3-ret.json              — RET / RETI / RET cc (6 entries)
    block3-rst.json              — RST t (8 entries)
    block3-push-pop.json         — PUSH rr / POP rr (8 entries)
    block3-ldh-mem-high.json     — LDH/LD <-> A (6 entries)
    block3-stack-arith.json      — ADD SP,e8 / LD HL,SP+e8 / LD SP,HL (3 entries)
    block3-di-ei.json            — DI / EI (2 entries)
    block3-cb-prefix.json        — 0xCB prefix dispatch (1 entry)
    block3-alu-imm8.json         — ALU A,n (8 entries)
    cb-prefix-shift.json         — RLC/RRC/RL/RR/SLA/SRA/SWAP/SRL x 8 sources (64 entries)
    cb-prefix-bit.json           — BIT b,r (64 entries, single instruction with bbb/sss)
    cb-prefix-res.json           — RES b,r (64 entries)
    cb-prefix-set.json           — SET b,r (64 entries)
```

**Total**: 23 group files, ~120 instruction definitions (the same
instruction with different selectors counts as multiple), covering the
full ISA 245 main + 256 CB = **501 opcodes**. Massively compressed
compared to the 1900-line `LegacyCpu.Step.cs`.

## Differences from the design document

Small adjustments during implementation relative to the original plan
(top half of [10-...md]):

1. **F register is not in the GPR file**: originally `general_purpose.names`
   contained F, later changed to only A/B/C/D/E/H/L 7 entries (GPR count
   = 7). F is in status, alongside SP/PC. `AF.high="A"/low="F"` in
   register_pairs still holds — each emitter resolves source by name,
   not requiring high/low to be in the same section.
2. **SP/PC are 16-bit status registers**: to avoid extending the schema
   with a new special-register section, 16-bit registers are
   provisionally placed in status with empty fields. The framework does
   not automatically treat these as PC; the host runtime handles them
   — a different design branch from ARM's R15 in GPR.
3. **`width_bits: 8` for main set**: originally intended variable-width,
   later realised LR35902 decode only needs the first byte; extra bytes
   are execution-time fetches. This avoids needing variable-width
   support in DecoderTable.
