# Phase 2.5: ARM7TDMI Complete Spec + Parser Completion

> **Status: Complete** (2026-05)
>
> **Completion metrics**:
> - **159 xUnit tests all green** (including CoverageTests vocabulary and
>   mnemonic baseline enforcement checks)
> - **44 ARM mnemonics + ~30 Thumb mnemonics**, all able to emit LLVM IR
> - Every micro-op in the spec has an emitter; no dead emitters
> - LLVM module passes `Verify` with 0 diagnostics
> - Phase 2.6 generalisation refactor (R1-R5) was folded into Phase 2.5
>
> The document below is preserved as a **historical plan doc**, kept for
> reference when doing the Phase 4.5 GB port.

---

## Why insert this between Phase 2 and Phase 3

Phase 2 made the spec → IR end-to-end pipeline run, but the spec content
was only a demo subset (4 ARM Data Processing instructions + B/BL/BX,
Thumb F1/F3/F18), and the parser only covered the micro-ops that were
used.

If we went straight to Phase 3 (run the interpreter on test ROMs), we
would repeatedly hit a "encounter some instruction, go back and patch a
bit of spec, patch one emitter" piecemeal increment, having to change
three places each time and lacking an overall view.

**Write the entire ARM7TDMI ISA into the spec first, finish parser/emitter
in sync, then enter Phase 3 to run validation ROMs** — the order is
cleaner this way:

- After Phase 2.5 is done, spec = the complete ARM7TDMI specification
  (the unit of work is "spec consistency")
- When Phase 3 starts, every instruction can be processed by the
  decoder/emitter (the unit of work is "runs correctly")

---

## Scope

### In scope

**ARM mode (ARMv4T 32-bit) — 13 encoding spaces:**

| Priority | Encoding space | Main instructions | Status |
|---|---|---|---|
| 1  | Branch and Exchange      | BX                                              | Done |
| 2  | Multiply                 | MUL, MLA                                        | Done |
| 3  | Multiply Long            | UMULL, UMLAL, SMULL, SMLAL                      | Done |
| 4  | Single Data Swap         | SWP, SWPB                                       | Done |
| 5  | Halfword/Signed DT       | LDRH, STRH, LDRSB, LDRSH                        | Done |
| 6  | PSR Transfer             | MRS, MSR (imm/reg)                              | Done |
| 7  | Data Processing          | 16 ops x 3 encoding variants (Imm, RegImmShift, RegRegShift) | Done |
| 8  | Single Data Transfer     | LDR, STR (incl. byte/word/imm/reg/pre/post/wb)  | Done |
| 9  | Undefined                | (reserved encoding that always raises UND exception) | Done |
| 10 | Block Data Transfer      | LDM, STM (x4 modes x S-bit x writeback)         | Done |
| 11 | Branch / Branch with Link| B, BL                                            | Done |
| 12 | Coprocessor (3 kinds)    | CDP, MRC/MCR, LDC/STC (stub -> UND)             | Done |
| 13 | Software Interrupt       | SWI                                              | Done |

> Priority is the mask/match priority — higher priority is matched first
> (PSR Transfer must match before Data Processing; Multiply must match
> before Data Processing; Halfword DT must match before Data Processing).

**Thumb mode (Thumb-1 16-bit) — 19 official formats: all done**

| Number | Name |
|---|---|
| F1  | Move Shifted Register (LSL/LSR/ASR imm) |
| F2  | Add/Sub |
| F3  | Move/Compare/Add/Sub Immediate (8-bit) |
| F4  | ALU Operations (16 ops) |
| F5  | Hi Register Operations + BX |
| F6  | PC-Relative Load |
| F7  | Load/Store with Register Offset |
| F8  | Load/Store Sign-Extended Byte/Halfword |
| F9  | Load/Store with Immediate Offset |
| F10 | Load/Store Halfword |
| F11 | SP-Relative Load/Store |
| F12 | Load Address |
| F13 | Add Offset to SP |
| F14 | Push/Pop Registers |
| F15 | Multiple Load/Store |
| F16 | Conditional Branch |
| F17 | Software Interrupt |
| F18 | Unconditional Branch |
| F19 | Long Branch with Link (two-half BL) |

**Parser/Emitter completion in sync:**

- All micro-ops required by the formats above
- Complete ARM cond table (fill in CS/CC/MI/PL/VS/VC/HI/LS/GE/LT/GT/LE)
- Complete banked register swap
- Real `restore_cpsr_from_spsr`, `enter_mode`, `raise_exception`
- shifted_register_by_immediate / shifted_register_by_register operand
  resolvers
- register_list operand resolver

### Out of scope (still deferred to Phase 3+)

- Running actual GBA ROMs
- LLVM JIT execution (only interpreter mode needs to marshal CpuState)
- BIOS HLE
- Actual interrupt handling flow
- DMA / Timer / PPU / APU
- ARMv5+ extensions (CLZ, Q-flag DSP instructions)

---

## Sub-phase schedule

Each sub-phase = one block of instruction formats + corresponding
emitters + tests. Commit + push at the end of each sub-phase, so each
can be accepted independently.

| Sub-phase | Content | Estimated commits | Estimated time |
|---|---|---|---|
| **2.5.1** | Spec authoring conventions doc + lint hardening | 1 | 0.5 day |
| **2.5.2** | ARM Data Processing complete set (x3 encodings) + PSR Transfer | 1-2 | 1.5 days |
| **2.5.3** | ARM Memory (SDT, Halfword/Signed, SWP)         | 1-2 | 1.5 days |
| **2.5.4** | ARM Multiply (MUL/MLA + Long)                  | 1   | 0.5 day |
| **2.5.5** | ARM Block Transfer + SWI + Coprocessor + UND   | 1-2 | 1 day |
| **2.5.6** | Thumb remaining 16 formats (F2, F4-F17, F19)    | 3-4 | 2 days |
| **2.5.7** | Complete cond table + banked swap + restore_cpsr | 1   | 1 day |
| **2.5.8** | Coverage validation + closing commit            | 1   | 0.5 day |

**Total estimate: ~8 days of focused work** (~3-4 weeks of after-hours
work).

---

## Sub-phase detail

### 2.5.1 Spec authoring conventions + lint

**Goal**: Before writing a large amount of spec, normalise the writing
style to avoid inconsistencies later.

Deliverables:
- `MD/design/07-spec-authoring-conventions.md`: bit-pattern letter
  conventions (`c` always for cond, `o` for opcode, `d` for Rd, `n` for
  Rn, `m` for Rm...), micro-op naming convention, cycle expression
  format.
- BitPatternCompiler / SpecLoader lint hardening:
  - Warn: undeclared field name (appears in pattern but not listed in
    fields[])
  - Warn: format has instructions but no selector (unexpected)
  - Enforce: mask/match must be consistent with pattern (already done)
  - Enforce: sub-opcode value width must match selector field width

### 2.5.2 ARM Data Processing complete set

**Add to arm.json**:

#### Data Processing — complete 16 ALU ops

opcode table (4 bits):
```
0000 AND  0001 EOR  0010 SUB  0011 RSB
0100 ADD  0101 ADC  0110 SBC  0111 RSC
1000 TST  1001 TEQ  1010 CMP  1011 CMN
1100 ORR  1101 MOV  1110 BIC  1111 MVN
```

TST/TEQ/CMP/CMN do not write back to Rd (mark `writes_pc: never`, no
store_reg), and S=1 is forced (otherwise the encoding becomes PSR
Transfer).

#### Three operand encoding variants (all under the same opcode table)

| Format name | Encoding condition | Pattern |
|---|---|---|
| `DataProcessing_Immediate`     | bit 25 = 1                 | `cccc 001 oooo s nnnn dddd rrrr iiiiiiii` (already exists) |
| `DataProcessing_RegImmShift`   | bit 25 = 0, bit 4 = 0      | `cccc 000 oooo s nnnn dddd ssssstt 0 mmmm` |
| `DataProcessing_RegRegShift`   | bit 25 = 0, bit 7 = 0, bit 4 = 1 | `cccc 000 oooo s nnnn dddd ssss 0 tt 1 mmmm` |

#### PSR Transfer (overlaps Data Processing encoding space)

When opcode = `10xx` and S = 0, this encoding slot is reused for PSR
Transfer. In priority, PSR Transfer must match before Data Processing.

| Mnemonic | Pattern |
|---|---|
| MRS | `cccc 00010 P 001111 dddd 000000000000` |
| MSR (reg) | `cccc 00010 P 1010001111 00000000 mmmm` |
| MSR (imm) | `cccc 00110 P 1010001111 rrrr iiiiiiii` |

`P` bit = SPSR select (0 = CPSR, 1 = SPSR_<current_mode>).

#### New micro-ops

- `adc`, `sbc`, `rsb`, `rsc` (arithmetic)
- `mvn` (NOT) — already implemented as `not`
- `update_c_sub_with_carry`, `update_c_add_with_carry` (ADC/SBC)
- `read_psr` / `write_psr` (for MRS/MSR; the latter supports field mask)
- `cmn` logic = sub flags using add flags (express directly as
  add+update_*_add)
- `tst` / `teq` = and/xor + update_nz + (depending on S-bit)
  update_c_shifter

#### New operand resolvers

- `shifted_register_by_immediate`
- `shifted_register_by_register`

Both must return `{value, shifter_carry_out}`, and must handle ARM's
special cases: LSL #0 -> no shift, carry comes from CPSR.C; LSR #0 ->
treated as LSR #32; ASR #0 -> treated as ASR #32; ROR #0 -> treated as
RRX.

### 2.5.3 ARM memory transfer

- **SingleDataTransfer (SDT)**: LDR / STR + B-bit (byte/word) + I-bit
  (immediate vs reg-shift offset) + P-bit (pre/post indexing) + U-bit
  (add/sub offset) + W-bit (writeback). The "shape" of each instruction
  is determined by these bits; the spec ends up with many sub-formats
  or a single format with a step-based flow.
- **Halfword/Signed DT**: LDRH, STRH, LDRSB, LDRSH. Three imm/reg offset
  variants.
- **SingleDataSwap**: SWP, SWPB (atomic load+store).

New micro-ops:
- `load`: `{addr, size, signed, rotate_unaligned}` -> value
- `store`: `{addr, value, size}`
- `swap_word`: atomic exchange
- `add_with_writeback`: for SDT writeback path

### 2.5.4 ARM Multiply

- **Multiply / Multiply-Accumulate**: MUL Rd, Rm, Rs; MLA Rd, Rm, Rs, Rn.
  Pattern: `cccc 000000 A S dddd nnnn ssss 1001 mmmm`.
- **Multiply Long**: UMULL/UMLAL/SMULL/SMLAL. Writes back to RdHi:RdLo.
  Pattern: `cccc 00001 U A S hhhh llll ssss 1001 mmmm`.

New micro-ops:
- `mul` (already has i32 mul)
- `umul64`, `smul64`
- `truncate_lo`, `extract_hi` (take upper/lower halves of i64)
- `write_reg_pair`

### 2.5.5 ARM Block Transfer + others

- **Block Data Transfer (LDM/STM)**:
  - Pattern: `cccc 100 P U S W L nnnn rrrrrrrrrrrrrrrr`
  - L=load/store, PU combinations decide IA/IB/DA/DB, S=user-mode regs,
    W=writeback.
- **SWI**: `cccc 1111 oooooooooooooooooooooooo` (24-bit immediate, used
  by HLE BIOS)
- **Coprocessor**: CDP/MRC/MCR/LDC/STC — GBA has no coprocessor, so
  emit `raise_exception(UndefinedInstruction)`
- **Undefined**: `cccc 011x xxxxxxxxxxxxxxxxxxxx xxx 1 xxxx` (specific
  reserved encoding)

New micro-ops:
- `block_load` / `block_store` (consume register_list, base_addr, mode)
- `raise_exception` (raise exception vector, switch mode, write SPSR,
  set PC)

New operand resolvers:
- `register_list` (16-bit bitmap)

### 2.5.6 Thumb complete 16 formats

Per the official manual's 17 entries (remaining: F2, F4-F17, F19). Most
Thumb semantics can directly reuse ARM micro-ops; not many new ones:

- F14 PUSH/POP uses `block_load`/`block_store`, but the register list
  encoding is different
- F19 BL (long branch with link): the two halves are combined into a
  32-bit offset; the correct semantics are "the upper half adds the hi
  11 bits to LR, the lower half ORs in the lo 11 bits and then swaps
  LR/PC". This needs emitters for two independent instructions, but they
  are conceptually coupled; mark them with quirks `bl_hi_half` /
  `bl_lo_half`.

### 2.5.7 Complete cond table + banked swap

- InstructionFunctionBuilder fills in 14 cond conditions (12 remaining):
  - CS = C set; CC = C clear
  - MI = N set; PL = N clear
  - VS = V set; VC = V clear
  - HI = C set AND Z clear
  - LS = C clear OR Z set
  - GE = N == V
  - LT = N != V
  - GT = Z clear AND N == V
  - LE = Z set OR N != V
- Banked register swap: when mode changes (e.g. entering IRQ), save
  current R8-R14 to the old mode's banked slot, and load the new mode's
  banked slot into R8-R14
- `restore_cpsr_from_spsr`: when ALU writes PC and S=1, restore CPSR
  from SPSR
- `enter_mode`: encapsulates the full flow of mode change (used by SWI
  / IRQ / exception)

### 2.5.8 Coverage validation + closing

- Compilation script: do a vocabulary check against **all** micro-op
  names and operand_resolver kinds in arm.json/thumb.json (missing
  emitters report errors)
- Decoding coverage test: 50+ known opcodes resolve to the correct
  mnemonic
- IR validation: full module passes LLVM `Verify` (Action = Print)
- Statistics: spec line count, emitter count, function count
- Update 03-roadmap.md, mark Phase 2.5 complete
- Push final commit

---

## Done criteria (closing standard for all of Phase 2.5)

1. Done: `aprcpu --spec spec/arm7tdmi/cpu.json --output temp/arm7tdmi.ll`
   produces LLVM functions for all ARMv4T instructions (estimated 80+
   functions)
2. Done: Module passes LLVM `Verify` with 0 diagnostics
3. Done: every micro-op in the spec has a corresponding emitter
4. Done: every operand_resolver kind in the spec is handled
5. Done: all 14 ARM cond codes correct
6. Done: at least 50 known opcodes pass the decoder coverage test
7. Done: xUnit tests 100/100 all green (rough count)
8. Done: roadmap doc updated

---

## Hand-off to following phases

After completion, when Phase 3 starts:

- Decoder is able to identify any GBA ROM bytes as instructions
- IR emitter can turn any instruction into a JIT-able function
- Phase 3 focus shifts to "actually run these functions":
  - The C# CpuState struct must layout-match the LLVM side
  - JIT execution engine + main fetch-decode-execute loop
  - armwrestler test ROM running through
