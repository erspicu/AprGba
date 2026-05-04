# Emitter Library Refactor — Generalisation + Intrinsic Pattern

> **Status**: design proposal after Phase 5.7 wraps up (2026-05-02).
> Address the structural problem of "per-CPU emitter bloat" before
> Phase 7 block-JIT.
>
> Motivation: the LR35902 emitter file is currently 2514 lines (40 ops)
> and ARM is 854 lines (22 ops). Adding a new CPU means writing another
> ~1000+ line C# file. This violates the framework's core promise of
> "swap CPU = swap JSON".
>
> Goal: reduce per-new-CPU emitter work from "~40 ops x C# implementation"
> to "at most 5-10 truly-unique ops + configuration metadata".

---

## 0. Implementation progress (continuously updated)

| Step | Status | Commits | File / op delta |
|---|---|---|---|
| 5.1 stack ops generalisation | Done | `a6f8f61` | +`StackOps.cs` 4 new ops (`push_pair`/`pop_pair`/`call`/`ret`) + spec `stack_pointer` metadata |
| 5.2 flag setters generalisation | Done | `93d0772` | +`FlagOps.cs` 3 new ops (`set_flag`/`update_h_add`/`update_h_sub`); LR35902 SCF migration |
| 5.3 branch / call_cc / ret_cc unification | Done | `61b73ca` `1684cae` `9758cca` | 5 new ops (`branch_cc`/`call_cc`/`ret_cc`/`read_pc`/`sext`) + `target_const`; LR35902 JP/JR/CALL/RET/RST all cc variants; cleanup `-408 lines` |
| 5.4 bit ops + shift unification | Done | `6e14512` `d218c04` `2d7f71a` | +`BitOps.cs` 4 new ops (`bit_test`/`bit_set`/`bit_clear`/`shift`); LR35902 BIT/SET/RES + 8 CB shifts + 4 A-rotates; cleanup `-485 lines` |
| 5.5 memory IO region unified | Done | `bf7fd77` | Binary adds width auto-coerce; LR35902 LDH/LD-(C) switched to `or` + load_byte/store_byte; deleted LdhIoLoad/Store emitters |
| 5.6 IME / cb_dispatch cleanup | Done | `5dd8c34` | Deleted `cb_dispatch` no-op (empty steps); IME/HALT/DAA marked L3 intrinsics with divider |
| 5.7.A flag micro-ops cleanup | Done | `b463590` | `mvn` width-aware; new `toggle_flag`; LR35902 CCF/CPL/SCF migration complete |
| 5.7.B inc/dec migration | Done | `e850ee3` | New `update_zero`/`update_h_inc`/`update_h_dec`/`trunc`; LR35902 INC/DEC r/(HL) + named-pair inc/dec |
| 5.7.C/D 16-bit selectors + L3 marker | Done | `4668a83` | INC/DEC rr switched to 4 selector variants; remaining 13 LR35902 ops marked L3 intrinsics with reasons |
| 5.8/5.9 third-CPU validation | Pending | — | RISC-V RV32I or MIPS R3000 |

**5.7 wrap-up snapshot (2026-05-02)**:

| File | Start (pre-5.0) | 5.7 wrap-up | Delta |
|---|---:|---:|---:|
| `Lr35902Emitters.cs` | ~2620 lines | 1346 lines | **-49%** |
| `Emitters.cs` (generic) | 613 lines | ~770 lines | +25% (absorbed LR35902 generalisation) |
| New `StackOps.cs` | — | ~415 lines | — |
| New `FlagOps.cs` | — | ~155 lines | — |
| New `BitOps.cs` | — | ~195 lines | — |

**Validation surface**: each step ran 345/345 unit tests + at least 3
Blargg sub-tests (including 02-interrupts / 07-jr,jp,call,ret,rst /
10-bit ops) on both legacy and json-llvm backends, all Passed. The 345
unit-test count was unchanged from 5.0 to the end of 5.7 — pure
structure, no new tests added or lost.

**Removed LR35902-specific emitters (5.1-5.7 cumulative)**:

```
push_qq / pop_qq                                   (5.1 stack)
jp / jp_cc / jr / jr_cc / call / call_cc /
  ret / ret_cc / rst                                (5.3 branch)
cb_bit / cb_bit_hl_mem / cb_set / cb_resset_hl_mem /
  cb_res / cb_shift / cb_shift_hl_mem               (5.4 CB-prefix)
ARotateEmitter (lr35902_rlca/rrca/rla/rra)         (5.4.B A-rotates)
EvalCondition / PcPointer / NormaliseToI16 helpers (5.3 cleanup)
ldh_io_load / ldh_io_store                          (5.5 IO region)
SimpleNoOpEmitter (cb_dispatch)                     (5.6 dispatch)
scf / ccf / cpl                                     (5.7.A flag ops)
inc_pair / dec_pair / inc_r8 / dec_r8 /
  inc_hl_mem / dec_hl_mem / inc_rr_dd / dec_rr_dd  (5.7.B/C inc/dec)
```

A total of **27 LR35902-specific ops + several helpers** removed. The
remaining 13 are all in clearly-marked L3 intrinsic sections, in two
classes:

1. **Operand resolvers** — `lr35902_read_r8` / `write_r8` /
   `write_rr_dd`: encoding sss/dd field -> register table is a feature
   of the LR35902 ISA, cannot be generalised across CPUs (ARM 4-bit,
   RISC-V 5-bit, each with different tables). Wait until a third CPU
   really needs to be ported, then add a spec-side operand resolver
   registry.

2. **Compound ALU + flag rules** — `alu_a_r8/hl/imm8` (one class with
   three names), `add_hl_rr`, `add_sp_e8`, `ld_hl_sp_e8`: arithmetic
   itself is generic, but flag bit positions + derivation rules are
   arch-specific. Splitting into 10+ generic ops/instructions would
   bloat the spec without substantive gain.

3. **Real L3 hardware quirks** (already marked in 5.6) —
   `lr35902_ime` / `ime_delayed` (EI delay), `halt` / `stop` (halt-bug
   + STOP wakeup), `lr35902_daa` (BCD adjust).

**Conclusion**: ~62 arch-specific ops (refactor starting point) -> **13
L3 intrinsics** (= -79%). The design doc 1.3 estimated a 5-10 L3 floor;
the actual is slightly higher (13), mostly in operand resolver and
compound ALU not yet split. Wait for the third CPU to come online
before validating whether this floor is reasonable — that's the work of
the Phase 5.8/5.9 stage.

**Refactor's perf impact**: ran a 4-ROM 1200-frame loop100 bench (vs
[`MD_EN/note/loop100-bench-2026-05.md`](/MD_EN/note/loop100-bench-2026-05.md) 5.7 baseline). The main framework
path (json-llvm) GBA improved slightly, GB regressed slightly; overall
within measurement noise (+/-5%). GB legacy dropped -15% but the
single-run measurement noise ratio is high (~300ms total) — it's not
the framework main path. Detailed numbers in
[`MD_EN/note/loop100-bench-2026-05-phase5.8.md`](/MD_EN/note/loop100-bench-2026-05-phase5.8.md). The trade-off "structural
clean vs speed-neutral" stands.

---

## 1. Current state inventory

### 1.1 Emitter files + op count

| File | Lines | op count | Class |
|---|---:|---:|---|
| `Emitters.cs` | 613 | 10 | generic baseline |
| `ArmEmitters.cs` | 854 | 22 | ARM-specific |
| `Lr35902Emitters.cs` | 2514 | 40 | GB DMG-specific |
| `BlockTransferEmitters.cs` | 369 | 2 | LDM/STM |
| `OperandResolvers.cs` | 480 | 0 | ARM operand resolution (resolver, not emitter) |
| `MemoryEmitters.cs` | 163 | (helpers) | generic mem hook declarations |
| `ConditionEvaluator.cs` | 136 | (helpers) | shared ARM/LR35902 cond table |
| **Total (refactor starting point)** | **~5800 lines** | **74** ops | |

**Actual state after 2026-05-03 update** (5.7 done + Phase 7 added
BlockFunctionBuilder + InstructionFunctionBuilder + after 4.5c and
instcombine fix):

| File | Lines |
|---|---:|
| `Emitters.cs` | 1051 (+438: absorbed LR35902 generic ops + IfStep const-fold + cpsr helpers) |
| `ArmEmitters.cs` | 909 (+55: A.6.1 Strategy 2 + freeze switch fixes etc., later removed) |
| `Lr35902Emitters.cs` | 1346 (-47%: 5.7 refactor done in one go) |
| `BlockTransferEmitters.cs` | 399 |
| `BlockFunctionBuilder.cs` | 320 (newly added in Phase 7.A.2) |
| `InstructionFunctionBuilder.cs` | 139 (newly added in Phase 7.A.2) |
| `StackOps.cs` / `FlagOps.cs` / `BitOps.cs` | 425 / 182 / 261 (5.1-5.4 new files) |
| `OperandResolvers.cs` | 525 |
| `CpuStateLayout.cs` / `EmitContext.cs` / `MemoryEmitters.cs` / `ConditionEvaluator.cs` | 325 / 206 / 182 / 234 |
| `IMicroOpEmitter.cs` / `IOperandResolver.cs` | 42 / 61 |
| **Current total** | **6607 lines** |

The added lines went mostly into the generic area (`Emitters.cs` +438,
new files `Stack/Flag/BitOps`); the LR35902-specific area kept
shrinking (-1168 lines). The framework generalisation trajectory
continues.

### 1.2 The "intuitive" classification of 74 ops (WRONG — too conservative)

In the first pass, ~30 LR35902 ops were marked L3, but on closer look:
**almost all are CPU-generic functionality**, just with different bit
positions / register naming / shift forms. Real L3 should not exceed
5-10.

### 1.3 Corrected classification (aggressive — pushing generalisation)

| Class | Op examples | Count | Why generic |
|---|---|---:|---|
| **L0: fully generic** — same logic across all CPUs | `read_reg`, `write_reg`, `add`, `sub`, `and`, `or`, `xor`, `shl`, `shr`, `sar`, `ror`, `mul`, `bic`, `mvn`, `read_imm8/16`, `store_byte`, `load_byte`, `if`, `select`, `branch`, `branch_link` | ~20 | already is |
| **L1: parametric** — generic op + spec metadata | `update_flag`, `read_psr`, `write_psr`, `read_pair`, `write_pair`, `push`, `pop`, `call`, `ret`, `cond_branch`, `set_ime`, `bit_test`, `bit_set`, `bit_clear`, `byte_swap`, `nibble_swap`, `daa_class` | ~17 | same pattern; bit positions / reg / flag names from spec config |
| **L2: idiom library** (multi-CPU shared, but variant details) | `barrel_shift_with_carry { variant: arm\|z80\|x86 }`, `block_transfer { mode: ldm\|stm\|rep_movs }`, `raise_exception { vector: ...; mode_swap: ... }`, `bcd_adjust { variant: z80\|6502\|x86 }` | ~6-8 | same idiom but variant config-driven |
| **L3: truly unique** — hardware quirks present in only 1-2 CPUs | `lr35902_ime_delay` (EI delays 1 instruction), ARM exception full path (SPSR bank swap + T-clear + R14 = pc+4 strange offset), ARM7TDMI `LDM/STM rlist=0` quirk, PowerPC `lwarx/stwcx`, 6502 BRK strange 7-cycle behaviour... | **~5-10** | really only present in 1-2 architectures, hardware bug-as-feature |

### 1.4 Why did it look like there were so many L3 before?

Re-examining ~12 examples that were misjudged as L3:

- **`lr35902_call` / `lr35902_call_cc` / `lr35902_ret` / `lr35902_ret_cc`** — CALL/RET is generic CPU functionality (M68k JSR/RTS, x86 CALL/RET, ARM BL/BX-LR, RISC-V JAL/JALR, 6502 JSR/RTS all have it). **All are L1**: `call { target, push_reg: PC }` + `ret { pop_to: PC }`, spec configures which reg to push/pop and which SP to use.

- **`lr35902_push_qq` / `lr35902_pop_qq`** — push/pop 16-bit. M68k MOVE.L SP-/+, x86 PUSH/POP, ARM STMFD/LDMFD all have it. **L1 generic**: `push { name }`, `pop { name }` paired with spec's register pair table.

- **`lr35902_jp` / `lr35902_jp_cc` / `lr35902_jr` / `lr35902_jr_cc`** — absolute jump + relative jump + conditional variants. Every CPU has them. **All L1**: `branch { addr_mode: absolute\|relative, cond: ... }`.

- **`lr35902_cb_bit` / `lr35902_cb_set` / `lr35902_cb_res` / `lr35902_cb_shift`** + corresponding `_hl_mem` variants (7 ops total) — bit-test/set/clear and shift exist in the Z80 family + M68k + x86 + RISC-V Zbb. **All L1/L2**: one op `bit_test { src: reg\|memory, bit: int }` replaces 7.

- **`lr35902_ldh_io_load` / `lr35902_ldh_io_store`** — IO region shortcut. x86 IN/OUT, 6502 zero-page; M68k doesn't have this but has similar short-form addressing. **L1**: spec declares io_region, generic mem ops auto-dispatch.

- **`lr35902_ime`** — interrupt master enable. ARM CPSR.I bit, RISC-V mstatus.MIE, M68k SR.I bit, x86 EFLAGS.IF — all have it. **L1**: `set_ime { value: 0\|1 }` paired with the spec's ime_register + ime_bit position.

- **`lr35902_scf` / `lr35902_ccf` / `lr35902_cpl`** — set/complement carry flag, complement A. Every CPU has set/clear/toggle flag instructions. **All L1**: `flag_op { reg, flag, action: set\|clear\|toggle }`.

- **`lr35902_add_hl_rr`** — 16-bit add, dest is HL pair. M68k ADDA, x86 ADD ax, ARM ADD r,r. **L1**: generic `add` op + spec lets dest be a register pair.

- **`lr35902_add_sp_e8` / `lr35902_ld_hl_sp_e8`** — SP + signed 8-bit offset. M68k LEA, x86 LEA, ARM ADD/SUB sp. **L1**: `add { src: reg, offset: signed_imm8 }`, dest is configured.

**Corrected conclusion**: of the ~40 LR35902 ops, **30+ are L1/L2
generic functionality** masquerading as CPU-specific. Real L3 is
roughly **5**: `lr35902_daa` (BCD adjust), the "EI delays 1
instruction" side effect of `lr35902_ime` (the IME bit itself is L1,
but the delay mechanism is unique), `lr35902_rst`'s hard-wired jump
table (L1 but very small-scale), HALT bug, and POP AF's quirk forcing
F low 4 bits to clear.

The **ARM side** is similar: `update_nz / update_c_*` is L1,
`read_psr / write_psr` is L1, `raise_exception` is L2. Real L3 only
leaves: banked register swap (mode change triggers R8-R14 remap) + a
few documented ARM7TDMI bugs (empty LDM rlist quirk, LDR/STR
mis-aligned rotate).

---

## 2. Proposed architecture: 4-tier emitter system

### 2.1 Tier concept

```
+-------------------------------------------------------------+
| L0  CoreOps         — shared across all CPUs, 0 config       |
|      add, sub, and, or, xor, shl/r, mul, branch, mem r/w     |
+-------------------------------------------------------------+
| L1  ParametricOps   — same pattern, different metadata,      |
|                       config-driven                          |
|      update_nz, update_c_add, raise_exception, write_psr     |
|      -> read flag bit position, status reg name, mode encode |
|         from spec                                            |
+-------------------------------------------------------------+
| L2  IdiomatedOps    — multi-CPU shared "compound idioms"     |
|      barrel_shift_with_carry (ARM/Thumb/SH4 all have)        |
|      block_transfer (ARM LDM/STM, 6502 LDA-loop, etc.)       |
|      cond_branch (ARM cond/Thumb F16/Z80 jr/M68k Bcc)        |
+-------------------------------------------------------------+
| L3  ArchIntrinsics  — truly CPU-unique quirks (last-resort)  |
|      lr35902_daa, lr35902_ime_delay, arm_msr_cpsr_with_swap  |
+-------------------------------------------------------------+
```

### 2.2 Tier design principles

| Tier | Work to add a new CPU | When to use |
|---|---|---|
| L0 | **zero** — use directly | simple ALU / mem / branch |
| L1 | edit spec metadata (cpu.json status definition + mode encoding table) | status flag / exception / mode switch |
| L2 | configure idiom variant from spec ("shift_style": "arm" / "z80" / "x86") | compound idioms |
| L3 | write ~50-150 lines of C# emitter | truly unique quirks |

### 2.3 Correspondence with Gemini suggestion #3

What Gemini calls "intrinsic builtins" maps to **L2 IdiomatedOps**: in
JSON write `"op": "barrel_shift"` rather than splitting into 5 micro-ops,
and the framework's built-in optimised LLVM IR generator handles it.

This document goes one step further: **L2 is multi-CPU shared idioms**
(worth doing framework-level optimisation) vs **L3 is single-CPU
quirks** (accept hand-written C#).

---

## 3. Concrete refactor plan

### 3.1 Generalise existing ops (from L3/L2 -> L1/L0)

Priority order (high impact to low):

#### A. Register Pair abstraction (affects GB ~10 ops)

Currently: `lr35902_read_r8`, `lr35902_write_r8`,
`lr35902_read_reg_pair_u64`, `lr35902_write_reg_pair`, `lr35902_push_qq`,
`lr35902_pop_qq`, `lr35902_call`, `lr35902_ret`, `lr35902_jp`,
`lr35902_rst`...

Proposal: framework adds a "register pair" first-class abstraction
(spec side declares `register_pairs: { BC: [B,C], DE: [D,E], HL: [H,L], AF: [A,F], SP: [SP] }`),
then add L1 ops:
- `read_pair { name }` — read 16-bit pair
- `write_pair { name, value }` — write
- `push { name }` — write to SP, SP-=2
- `pop { name }` — SP+=2, read
- `call { target }` — push PC, branch
- `ret` — pop PC, branch

**Eliminates**: ~10 lr35902_* ops.

#### B. Status Register abstraction (status reg + flag table)

Currently: each CPU writes its own `update_nz / update_c_* / update_v_*`
with hardcoded bit positions.

Proposal: the spec side declares the status reg and flag bit positions
(framework already has `StatusRegister.Fields`); the generic emitter
does:
- `update_flag { reg: "CPSR", flag: "N", from_msb_of: "<value>" }`
- `update_flag { reg: "F", flag: "Z", from_zero_test_of: "<value>" }`
- `update_flag_carry { reg: "CPSR"|"F", flag: "C", source: "add"|"sub"|"shift_left"|"shift_right", in: [a, b] }`

**Eliminates**: ~10 `update_*` ops in dual ARM/GB implementations.

#### C. CB-prefix series merge (GB 4 -> 1)

Currently: `lr35902_cb_bit`, `lr35902_cb_bit_hl_mem`, `lr35902_cb_res`,
`lr35902_cb_resset_hl_mem`, `lr35902_cb_set`, `lr35902_cb_shift`,
`lr35902_cb_shift_hl_mem` — 7 ops all doing the same pattern (operand
source = reg or memory).

Proposal:
- `bit_test { src: reg|memory, bit: <int> }`
- `bit_set { src: reg|memory, bit: <int> }`
- `bit_clear { src: reg|memory, bit: <int> }`
- `shift { variant: rlc|rrc|rl|rr|sla|sra|swap|srl, src: reg|memory }`

**Eliminates**: 7 -> 4 generic ops. The same pattern also applies to
other CPUs with bit-test instructions.

#### D. Memory + IO merge

Currently: `load_byte`, `store_byte`, `store_word`, `lr35902_ldh_io_load`,
`lr35902_ldh_io_store` — IO and memory access use different ops.

Proposal: spec adds an IO region declaration (`io_region: { base: 0xFF00, size: 0x80 }`),
and generic mem ops automatically dispatch by address. `ldh` is
expanded on the spec side as `add 0xFF00 + load_byte`.

**Eliminates**: 2 lr35902 IO ops.

#### E. Conditional execution unified

Currently: ARM all instructions are conditional (cond field 4 bits),
Thumb F16 uses `if_arm_cond`, GB uses
`lr35902_jp_cc / call_cc / ret_cc / jr_cc`.

Proposal: framework adds a `cond_check` op + a unified `branch_cc`
template. All GB conditional variants = same `branch_cc { cond_field: ... , target: ... }`,
sub-encoding configured on the spec side.

**Eliminates**: 4 lr35902_*_cc ops.

### 3.2 Post-refactor estimate (aggressive version)

| File | Pre-refactor | Post-refactor |
|---|---:|---:|
| `Emitters.cs` (L0 fully generic) | 613 lines, 10 ops | ~700 lines, 22-25 ops |
| `Parametric/` (L1 spec-driven generic) | n/a | ~1000 lines, 15-18 ops |
| `Idioms/` (L2 multi-variant idiom library) | n/a | ~500 lines, 6-8 ops |
| `ArmIntrinsics.cs` (L3 ARM truly unique) | 854 lines, 22 ops | **~150 lines, 3-5 ops** |
| `Lr35902Intrinsics.cs` (L3 GB truly unique) | 2514 lines, 40 ops | **~200 lines, 4-6 ops** |
| `Resolvers/` (operand resolvers, pre-existing) | 480 lines | ~300 lines (simplified) |
| **Total** | **~4000 lines C#** | **~2850 lines C#** |

**~30% overall code reduction**, but more importantly:
- L3 portion drops from **62 ops to 7-11 ops** = -85%
- The L3 emitter portion for a new CPU drops from ~800-1000 lines to
  **~150-200 lines**
- Most new-CPU work = writing spec JSON + configuring metadata, no need
  to touch framework C# code

### 3.3 "True L3" list (only these remain after refactor)

ARM7TDMI L3 (~3-5 ops):
- `arm_swi_swap_bank` — SWI/IRQ entry coupled with banked R8-R14 swap
  (spec describes the banked_per_mode table, but "mode change -> really
  swap memory slots" is a framework-level behaviour itself; for
  non-banking CPUs it's a no-op)
- `arm_ldm_rlist_zero_quirk` — ARMv4T documented bug, when rlist=0 LDM
  loads PC + writeback +0x40
- `arm_ldr_misaligned_rotate` — LDR's rotated read behaviour when
  reading from a non-word-aligned address

LR35902 L3 (~4-6 ops):
- `lr35902_daa` — BCD decimal adjust
- `lr35902_ime_delay` — EI sets IME=1 with a 1-instruction delay (RETI
  is immediate)
- `lr35902_pop_af_low_clear` — POP AF forces F low 4 bits to clear
- `lr35902_halt_bug` — HALT-without-pending-IRQ-and-IF-empty repeats
  the next instruction
- (optional) `lr35902_stop` — STOP instruction's button-wake behaviour
  (test ROMs don't use it)

These 7-11 ops are the "true hardware quirks needing hand-written C#".
Everything else is driven from spec configuration.

---

## 4. Per-new-CPU effort comparison

### 4.1 Before (current state) — adding RISC-V RV32I

Need:
1. spec/riscv32/cpu.json + 5-10 instruction group jsons (~3000 lines spec)
2. Write `Riscv32Emitters.cs` ~800-1000 lines handling:
   - Different status reg (mstatus is completely different from ARM CPSR)
   - Each instruction has read_reg(rd) + ALU + write_reg(rd) — simple patterns
   - Exception entry (mtvec mechanism, different from ARM vector table)
   - All these rewritten in C#
3. ~1 week of work

### 4.2 After (post-refactor) — adding RISC-V RV32I

Need:
1. spec/riscv32/cpu.json + group jsons (~3000 lines spec) — unchanged
2. **L1 configuration**: in cpu.json declare mstatus flag positions +
   mtvec exception mechanism -> generic update_flag /
   raise_exception runs automatically
3. Write `Riscv32Intrinsics.cs` only handling RV32I truly unique stuff
   (RISC-V is a clean design — possibly 0-3 L3 ops, like ECALL /
   EBREAK / FENCE)
4. ~2-3 days of work (plus spec authoring)

**~70-80% reduction in per-CPU C# work**.

### 4.3 What the refactored ARM / GB files should look like

`ArmIntrinsics.cs` (renamed from Emitters -> Intrinsics to emphasise
L3 nature):
```csharp
// ~150 lines total, 3-5 ops
internal sealed class ArmSwiBankSwapEmitter : IMicroOpEmitter { ... 50 lines ... }
internal sealed class ArmLdmRlistZeroQuirk : IMicroOpEmitter { ... 30 lines ... }
internal sealed class ArmLdrMisalignedRotate : IMicroOpEmitter { ... 30 lines ... }
```
The other 17 originally-ARM-only ops (update_nz, update_c_*, read_psr,
write_psr, raise_exception, if_arm_cond) all move to `Parametric/`,
driven by spec metadata.

`Lr35902Intrinsics.cs`:
```csharp
// ~200 lines total, 4-6 ops
internal sealed class Lr35902DaaEmitter : IMicroOpEmitter { ... 60 lines ... }
internal sealed class Lr35902ImeDelay : IMicroOpEmitter { ... 30 lines ... }
internal sealed class Lr35902PopAfLowClear : IMicroOpEmitter { ... 20 lines ... }
internal sealed class Lr35902HaltBug : IMicroOpEmitter { ... 40 lines ... }
```
The other 35 originally-GB-only ops are all moved out: CALL/RET/PUSH/
POP/JP/JR/CB-bit/LDH-IO/IME/SCF/CCF/CPL/ADD-HL-RR/ADD-SP/LD-HL-SP all
spec-config-driven.

---

## 5. Implementation phase plan

Updated version (aggressive generalisation direction):

| Step | Content | Time estimate | Risk |
|---|---|---|---|
| **5.1** | **Register pair / push / pop / call / ret** — spec schema adds `register_pairs` + `stack_register`, new L1 ops `read_pair / write_pair / push / pop / call / ret`, refactor LR35902 + ARM corresponding ops | 4-6 hours | low (pure add, old ops kept as alias temporarily) |
| **5.2** | **Status flag generalisation** — spec-driven flag table for update_*, remove ARM/GB separate implementations; new L1 op `update_flag { reg, flag, source: nz_test\|carry_add\|carry_sub\|carry_shl\|... }` | 6-8 hours | medium (covers ARM N/Z/C/V + GB Z/N/H/C; jsmolka + Blargg as backstop) |
| **5.3** | **Conditional execution / branch unified** — `branch { addr_mode: absolute\|relative, cond: ... }`, swallows GB JP/JR/CALL/RET cc variants + ARM Thumb F16 if_arm_cond | 3-4 hours | medium (involves merge of 8+ GB ops) |
| **5.4** | **Bit ops unified** — `bit_test / bit_set / bit_clear { src: reg\|memory, bit: int }` + `shift { variant, src }` replacing GB 7 CB-* ops | 2-3 hours | low (GB-only) |
| **5.5** | **Memory IO region** — io_region config + generic mem dispatch; remove GB IO shortcut ops; ARM also unifies IO/mem path | 3-4 hours | medium (GBA bus binding must verify IO write timing correct) |
| **5.6** | **IME / interrupt master enable unified** — `set_ime { value, delay: 0\|1 }` paired with spec ime_register/ime_bit; GB delay=1, ARM delay=0 | 2-3 hours | low (pure add) |
| **5.7** | **Operand resolver generalisation** — split out generic resolvers from ARM shift-by-reg and LR35902 PC-rel; CPU-specific resolvers thin out | 3-4 hours | medium |
| **5.8** | **L3 cleanup + intrinsic naming** — concentrate the remaining 7-11 L3 ops into two `*Intrinsics.cs` files, add full docstrings explaining why this op cannot generalise | 2-3 hours | low |
| **5.9** | **Third-CPU validation** — pick RISC-V RV32I (clean spec) or MIPS R3000 (familiar to many), validate framework generalisation promise | 1 week | depends on how deep the spec is written; the third CPU's online time is the biggest proof of framework success |

**5.1-5.8 totals ~25-35 hours** = 4-5 full work days.

5.9 third-CPU validation adds 1 week (including spec authoring).

### 5.10 Implementation order considerations

5.1 (register pair + call/ret) is prioritised because:
- Resolves ~10 LR35902 ops in one go, immediate pay-off
- Doesn't affect existing ARM path (ARM uses banked R8-R14 + STMFD/
  LDMFD, already has push/pop concept)
- Helps design every later step (many other ops depend on the register
  pair abstraction)

5.2 (status flag) is the next priority — broad-impact, the entire
framework looks "much cleaner" after it. But needs to cover ARM 4-flag
+ GB 4-flag, must be careful not to break existing jsmolka/Blargg
results. Each `update_*` op refactor runs all unit tests + loop100 as
a backstop.

---

## 6. Relationship with other Gemini suggestions

Gemini's 5 points:

1. **Inlining / micro-op fusion** — belongs to the SpecCompiler-stage
   optimisation, independent of this document. Wait until after emitter
   refactor (need "generic ops" first to find fusion patterns).
2. **State Tracking / Lazy Flag** — same as above, SpecCompiler-stage.
   Lazy flag is especially effective because ARM conditional execution
   reads flags frequently.
3. Adopted: **Architecture-specific characterisation** — this document's
   L3 ArchIntrinsics concept. **Adopted**.
4. **Compilation cache + hotspot analysis** — belongs to the Phase 7
   block-JIT scope.
5. **Reduce extern binding** — belongs to Phase 7 scope.

This document focuses on **#3 + structural side**; #1/#2/#4/#5 handled
in Phase 7.

---

## 7. Why do this before Phase 7

1. **Phase 7 block-JIT will get embedded inside the emitter**: if the
   emitter structure is still messy (one file per CPU), Phase 7 changes
   will scatter across files and become hard to maintain
2. **Third-CPU validation cost directly determines whether third-CPU
   validation is feasible**: currently ~1 week/CPU is too expensive;
   post-refactor 2-3 days can add a new CPU
3. **This is the "framework generalisation promise" last mile**: spec
   side has already achieved "swap CPU = swap JSON", the emitter side
   has not — only after the refactor does the argument really hold
4. **Doesn't affect Phase 5.7 wrap-up state**: 345 unit tests +
   jsmolka/Blargg PASS unchanged, purely structural improvement

---

## 8. Risks + fallbacks

| Risk | Mitigation |
|---|---|
| Status flag generalisation misses a corner of some ARM/GB flag-update | Full unit tests + jsmolka/Blargg + loop100 all rerun |
| Spec schema changes break existing specs | Schema versioning, old specs auto-compatible |
| New bugs introduced during refactor | Each step gets its own commit, easy git bisect |
| Post-refactor loop100 bench numbers shift | Expected within +/-5% (purely structural, no new work added); shifts must be analysed |
| Post-refactor doesn't end up saving as many lines | Accept it — structural clarity is itself valuable |

---

## 9. One-sentence summary

**Pay off the "one 1000+ line emitter file per CPU" structural debt so
the framework actually delivers on its "swap CPU = swap JSON" promise.**

Key insight: of the current ~62 arch-specific ops, only **7-11 are real
L3 hardware quirks** (DAA, IME delay, HALT bug, ARM banked register
swap, ARMv4T LDM rlist=0 quirk, etc.); the other ~50+ are CPU-generic
functionality (CALL/RET/PUSH/POP/JP/JR/Bit-ops/IO-access/Flag-update)
masquerading as arch-specific.

Estimated 25-35 hours of work (4-5 full work days), L3 emitter portion
-85% (62 ops -> 7-11 ops), new-CPU online time drops from 1 week to
2-3 days. The "swap CPU = swap JSON" argument is only really delivered
after third-CPU validation. After completing this, only then is it
reasonable to enter Phase 7 block-JIT.
