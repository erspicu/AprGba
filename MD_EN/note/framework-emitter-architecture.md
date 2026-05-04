# AprCpu Framework — Design concept and emitter architecture

> **Originally written at Phase 4.5 wrap-up** (when both ARM7TDMI +
> LR35902 CPUs were running), **2026-05-03 major rewrite: reflects
> Phase 5.8 emitter library refactor + Phase 7 JIT optimisation in
> progress**.
>
> Documents how the framework turns "JSON CPU description" into
> "JIT capable of running ROMs", what the two CPUs' emitters actually
> do, and after Phase 5.8 generalisation reduced LR35902-side emitter
> volume from ~2620 lines to 1346 lines (-49%), which ops are the
> remaining true L3 intrinsics.

---

## 1. The framework's central thesis

> **"Swapping CPU equals swapping JSON."**

This is what AprCpu aims to prove. Traditional emulators are
hand-coded instruction interpreters (one giant switch-case carries
every opcode), and each CPU is rewritten from scratch. AprCpu splits
a CPU into two parts:

- **Data**: JSON spec — register file, encoding, step list
- **Verbs**: emitter — how each step turns into LLVM IR

Corresponding files (status post Phase 5.8 / 7):

```
spec/<arch>/cpu.json + groups/*.json     <- data (per-CPU)
src/AprCpu.Core/IR/<Arch>Emitters.cs     <- verbs (per-CPU; getting thinner)
src/AprCpu.Core/IR/Emitters.cs           <- L0/L1 generic micro-ops
src/AprCpu.Core/IR/StackOps.cs           <- L1 stack generic ops (added Phase 5.8.1)
src/AprCpu.Core/IR/FlagOps.cs            <- L1 flag generic ops (added Phase 5.8.2)
src/AprCpu.Core/IR/BitOps.cs             <- L1 bit generic ops (added Phase 5.8.4)
src/AprCpu.Core/IR/MemoryEmitters.cs     <- generic memory bus interface
src/AprCpu.Core/IR/BlockTransferEmitters.cs <- LDM/STM (ARM)
src/AprCpu.Core/IR/ArmEmitters.cs        <- ARM-specific emitters
src/AprCpu.Core/IR/Lr35902Emitters.cs    <- LR35902-specific emitters
src/AprCpu.Core/Compilation/SpecCompiler.cs <- generic compilation flow
src/AprCpu.Core/Runtime/HostRuntime.cs   <- MCJIT + extern binding
src/AprCpu.Core/Runtime/CpuExecutor.cs   <- fetch-decode-execute dispatcher
src/AprCpu.Core/Decoder/DecoderTable.cs  <- bit-pattern dispatch
```

**"Swapping CPU does not require touching framework code" has been
verified**: the ARM7TDMI and LR35902 emitters do not reference each
other at all, and adding a new one will not break the other. After
Phase 5.8, the emitter workload for adding a new CPU is roughly
halved (many ops that previously had to be written are now
generalised into StackOps / FlagOps / BitOps).

---

## 2. Four-tier architecture (L0/L1/L2/L3 layering introduced in Phase 5.8)

```
+--------------------------------------------------------------+
| Layer 0 - L0 fully generic ops                               |
| Logic identical across all CPUs, zero configuration.         |
|                                                              |
| Emitters.cs (StandardEmitters):                              |
|   register I/O: read_reg / write_reg                         |
|   arithmetic: add / sub / and / or / xor / shl / lshr / ashr |
|        / rsb / mul / mvn / bic / ror                         |
|   64-bit: umul64 / smul64 / add_i64                          |
|   control flow: if / select / branch / branch_link / branch_cc|
|   PC: read_pc                                                |
|   Width: sext / trunc                                        |
| MemoryEmitters.cs: load / store / read/write extern wiring   |
| BlockTransferEmitters.cs: block_load / block_store           |
+--------------------------------------------------------------+
+--------------------------------------------------------------+
| Layer 1 - L1 parametric - generic op + spec metadata         |
| Same pattern, different metadata. Configuration from spec.   |
|                                                              |
| StackOps.cs (Phase 5.8.1):                                   |
|   push_pair / pop_pair / call / ret / call_cc / ret_cc       |
|   (knows where SP is via spec.stack_pointer)                 |
| FlagOps.cs (Phase 5.8.2/7):                                  |
|   set_flag / toggle_flag / update_h_add / update_h_sub /     |
|   update_zero / update_h_inc / update_h_dec                  |
|   (reg/flag parameters specify which status reg's bit)       |
| BitOps.cs (Phase 5.8.4):                                     |
|   bit_test / bit_set / bit_clear / shift {kind: ...}         |
+--------------------------------------------------------------+
+--------------------------------------------------------------+
| Layer 2 - L2 idiom (planned, not yet built out)              |
| "Composite idioms" shared by multiple CPUs, spec configures  |
| variants.                                                    |
|                                                              |
| Idea: barrel_shift_with_carry { variant: arm|z80|x86 }       |
|       block_transfer { mode: ldm|stm|... }                   |
|       raise_exception { vector, mode_swap }                  |
| No standalone file yet; ARM's if_arm_cond + raise_exception  |
| have L2 character but currently live in ArmEmitters.         |
+--------------------------------------------------------------+
+--------------------------------------------------------------+
| Layer 3 - L3 architecture-specific intrinsics                |
| True CPU-unique quirks. Hardware quirks or shape differences |
| that only appear on this CPU.                                |
|                                                              |
| ArmEmitters.cs (~22 ops):                                    |
|   update_nz / update_c_* / update_v_* (CPSR-specific flags)  |
|   adc / sbc / rsc (carry-aware ALU)                          |
|   read_psr / write_psr / restore_cpsr_from_spsr              |
|   if_arm_cond / branch_indirect / raise_exception            |
| Lr35902Emitters.cs (~13 ops, post Phase 5.8 refactor):       |
|   read_reg_named / write_reg_named (named reg I/O)           |
|   read_reg_pair_named / write_reg_pair_named                 |
|   lr35902_read_r8 / write_r8 / write_rr_dd (operand resolver)|
|   lr35902_alu_a_r8/hl/imm8 (compound ALU + flag rules)       |
|   lr35902_add_hl_rr / add_sp_e8 / ld_hl_sp_e8 (16-bit + flag)|
|   lr35902_ime / ime_delayed / halt / stop (interrupt path)   |
|   lr35902_daa (BCD adjust)                                   |
|   load_byte / store_byte / store_word / read_imm8/16         |
+--------------------------------------------------------------+
```

`SpecCompiler.Compile` selects via the `architecture.family` string:

```csharp
StandardEmitters.RegisterAll(registry);   // L0/L1 both here
if (family == "ARM")           ArmEmitters.RegisterAll(registry);
else if (family == "Sharp-SM83") Lr35902Emitters.RegisterAll(registry);
// Adding a third CPU is just one more if ... then writing spec/<new CPU>/
// and <NewCpu>Emitters.cs
```

Adding a third CPU is estimated to require only 5-10 L3 ops (Phase 5.8
absorbed all the generalisable ones) + spec metadata.

---

## 3. How far the JSON side can describe

Things doable without writing any C#:

| Property | Where in spec |
|---|---|
| Register count, width, naming | `register_file.general_purpose` |
| 16-bit pair composition from two 8-bit regs | `register_file.register_pairs` |
| Where stack pointer is (GPR index or status reg name) | `register_file.stack_pointer` (added Phase 5.8.1) |
| Status reg + its flag bit positions | `register_file.status[].fields` |
| Banked register / processor mode | `processor_modes` |
| Exception vector addresses | `exception_vectors` |
| Multiple instruction sets + switching condition | `instruction_set_dispatch` |
| Opcode decoding (mask/match/fields) | `encoding_groups[].formats[].pattern` |
| Choosing among instructions in same format | `instructions[].selector` |
| Per-instruction cycles | `instructions[].cycles.form` |
| Instruction execution steps | `instructions[].steps[]` |

L1 ops are describable through spec metadata:

| Behavior | Steps |
|---|---|
| Push 16-bit pair | `{ "op": "push_pair", "name": "BC" }` |
| Conditional call | `{ "op": "call_cc", "target": "addr", "cond": { "reg": "F", "flag": "Z", "value": 0 } }` |
| Set flag to constant | `{ "op": "set_flag", "reg": "F", "flag": "C", "value": 1 }` |
| Update Z = (val == 0) | `{ "op": "update_zero", "in": "result", "reg": "F", "flag": "Z" }` |
| Bit test | `{ "op": "bit_test", "in": "v", "bit_field": "bbb", "reg": "F", "flag": "Z" }` |
| Shift kind | `{ "op": "shift", "kind": "rlc", "in": "v", "out": "w", "reg": "F", "flag_z": "Z", "flag_c": "C" }` |

Things that cannot be expressed via spec alone and require emitter
code (this is the L3 territory):

| Behavior | Why JSON cannot express it |
|---|---|
| BCD adjust logic (DAA) | A pile of conditional branches + lookup table |
| Compound ALU flag rules (ADC carry-in, ADD HL,rr's bit-11 H flag) | Multi-step, cross-layer conditional accumulation; spec-ifying would push IR builder logic into JSON |
| "Some value in a 3-bit field means memory access, not register" | Needs select chain + conditional store + bus extern call |
| Notify host runtime "I HALTed" / "IME delayed by 1 instruction" | C# / IR boundary wiring + per-CPU quirks |
| Generic push/pop now describable in spec | (before Phase 5.8.1 needed emitter; not anymore) |
| Generic conditional branch now describable in spec | (before Phase 5.8.3 needed emitter; not anymore) |
| Generic bit ops + shift now describable in spec | (before Phase 5.8.4 needed emitter; not anymore) |

The last three lines moving from L3 down to L1 is the main output of
the Phase 5.8 refactor.

---

## 4. What ArmEmitters does

Corresponding file: `src/AprCpu.Core/IR/ArmEmitters.cs` (**~870 lines,
22 ops**). Not affected by the Phase 5.8 refactor (which targeted
LR35902-side generalisation).

### 4.1 CPSR / SPSR flag updates

ARM's CPSR places N/Z/C/V at bits 31..28. Every ALU instruction (when
S-suffixed) computes these four flags.

| op | Function |
|---|---|
| `update_nz` | N = result top bit; Z = (result == 0) |
| `update_c_add` | C = carry of 32-bit add (i64 widening) |
| `update_c_sub` | C = NOT borrow |
| `update_v_add` | V = signed overflow on add |
| `update_v_sub` | V = signed overflow on sub |
| `update_c_shifter` | C <- carry-out from shifter (used by barrel shifter) |
| `update_c_add_carry` | ADC: C = (a + b + Cin) > 0xFFFFFFFF |
| `update_c_sub_carry` | SBC/RSC |

> Phase 5.8.2 added generic `set_flag` / `toggle_flag` / `update_zero`
> and other L1 ops, but ARM side **has not adopted them yet** (the
> refactor didn't touch ARM since the main target was LR35902). When
> the third CPU comes online, ARM update_* will also be moved to
> spec-driven.

### 4.2 ALU with carry

| op | Function |
|---|---|
| `adc` | A + B + Cin |
| `sbc` | A - B - !Cin |
| `rsc` | B - A - !Cin (RSB with carry) |

Generic `add` / `sub` (in StandardEmitters) is insufficient because
they need to read CPSR.C.

### 4.3 PSR access and mode switching

| op | Function |
|---|---|
| `read_psr` | Reads CPSR or SPSR_<mode> (selects banked slot based on current mode) |
| `write_psr` | Writes CPSR/SPSR; if CPSR.M (mode bits) changes, calls extern `host_swap_register_bank` to rearrange banked registers |
| `restore_cpsr_from_spsr` | The "I-bit + mode restored together" special path used by LDM ^ / SUBS PC,LR; emits a switch over CPSR.M[4:0], one case per banked-SPSR mode, PHI merging, then calls swap_bank |

### 4.4 Control flow / conditional / exceptions

| op | Function |
|---|---|
| `branch_indirect` | BX: writes R15, sets CPSR.T based on target bit[0] to switch ARM/Thumb |
| `if_arm_cond` | ARM 14 condition codes evaluated (EQ/NE/CS/CC/MI/PL/...) |
| `raise_exception` | For SWI/UND: save CPSR -> SPSR_<entry_mode>, save next-PC -> banked R14, change CPSR.M, set I bit, call swap_bank, PC <- vector |

### 4.5 Characteristics of the ARM-side emitters

- **GPRs are uniformly i32** (all 16 R0~R15 are 32-bit), so generic
  `read_reg` / `write_reg` work directly
- **R15 = PC**: this is a GPR slot, no special handling needed, but
  reads come with pipeline offset (host runtime pre-sets it before
  step starts)
- **Banked register**: CpuStateLayout reserves banked slots in the
  struct; `write_psr` / `restore_cpsr_from_spsr` trigger calls to
  host extern to rearrange them
- **Memory access**: directly uses StandardEmitters' `load` / `store`,
  combined with ARM-specific `block_load` / `block_store` (LDM/STM)

Emitters shared by ARM and LR35902 (not living in ArmEmitters):
`read_reg`, `write_reg`, `add`, `sub`, `mul`, `if`, `branch`,
`branch_cc` (added 5.8.3), and so on. So ArmEmitters.cs purely handles
the ARM-non-shareable parts.

---

## 5. What Lr35902Emitters does (after Phase 5.8 major reduction)

Corresponding file: `src/AprCpu.Core/IR/Lr35902Emitters.cs` (**~1346
lines, ~13 ops**).

> **Status after Phase 5.8 emitter library refactor wrap-up**:
> starting point ~2620 lines / ~40 ops; refactor generalised 27 ops
> away, file shrank 49%. Remaining 13 ops are all in clearly-marked
> L3 intrinsic sections, in three categories: operand resolvers,
> compound ALU + flag, true hardware quirks.

### 5.1 Cross-arch helpers (live in LR35902 file but generic to 8-bit CPUs)

| op | Function |
|---|---|
| `read_reg_named` | Look up register by name ("A"/"B"/.../"SP"/"PC"); auto-recognises GPR vs status reg, auto-aligns LLVM width |
| `write_reg_named` | Same; auto trunc/zext on write to align width |
| `read_reg_pair_named` | "BC" -> compose B/C into i16; "SP" -> read i16 status directly |
| `write_reg_pair_named` | Reverse; AF special case forces F low 4 bits = 0 |

> If a second 8-bit CPU comes in later, these four can be promoted to
> StandardEmitters. Currently LR35902-only but op name and schema are
> already generalised.

### 5.2 Operand resolvers (L3 - operand decode)

| op | Function |
|---|---|
| `lr35902_read_r8` | 3-bit `sss` field -> one of B/C/D/E/H/L/(HL)/A; select chain |
| `lr35902_write_r8` | Mirror of the above |
| `lr35902_write_rr_dd` | 2-bit `dd` field -> BC/DE/HL/SP |

These three are part of the LR35902 ISA encoding. When the third CPU
comes online, may consider adding a spec-side operand resolver
registry (allowing specs to declare their own sss/dd tables); but for
now, they remain L3 in the emitter.

### 5.3 Compound ALU + flag rules (L3 - arithmetic + LR35902 flag rules)

| op | Function |
|---|---|
| `lr35902_alu_a_r8` | ADD/ADC/SUB/SBC/AND/XOR/OR/CP, source is sss-selected reg |
| `lr35902_alu_a_hl` | Same 8 ops, source is memory[HL] |
| `lr35902_alu_a_imm8` | Same 8 ops, source is imm8 |
| `lr35902_add_hl_rr` | 16-bit ADD HL,rr; H from bit 11 -> 12 carry |
| `lr35902_add_sp_e8` | SP <- SP + sign_ext(e8); H/C from low byte unsigned add of SP and e8 (LR35902 quirk) |
| `lr35902_ld_hl_sp_e8` | HL <- SP + sign_ext(e8), flag rules same as above |

Arithmetic itself is generic, but the flag bit positions + derivation
rules are LR35902-specific. Refactor evaluation determined splitting
into 10+ generic ops/instructions would explode the spec without
substantive gain — kept as L3.

### 5.4 True hardware quirks (L3 - hardware quirks)

| op | Function |
|---|---|
| `lr35902_ime` | call extern `host_lr35902_set_ime(0\|1)` (DI 0 / RETI 1) |
| `lr35902_ime_delayed` | call extern `host_lr35902_arm_ime_delayed` (EI's delayed-by-one-instruction semantics) |
| `halt` | call extern `host_lr35902_halt`, sets _haltSignal |
| `stop` | Same halt extern (DMG STOP treated as halt) |
| `lr35902_daa` | BCD adjust: pick +0x60 / +0x06 / -0x60 / -0x06 based on N/H/C and current value of A |

These ops' host externs and EI-delay mechanism do not exist in other
CPU families; firmly L3.

### 5.5 Bus-level helpers (L3 - currently lives in LR35902 file, will move out)

| op | Lowers to |
|---|---|
| `load_byte` | `memory_read_8(i32 addr)` -> i8 |
| `store_byte` | `memory_write_8(i32 addr, i8 v)` |
| `store_word` | `memory_write_16(i32 addr, i16 v)` (host shim splits into two bytes) |
| `read_imm8` | Reads 1 byte from PC and PC++ |
| `read_imm16` | Reads 2 bytes from PC and PC += 2 |

The op names are generic, but the implementation currently sits in
the LR35902 file. Phase 5.8 cleanup identified these 5 as actually
cross-arch bus wrappers; they will later move to MemoryEmitters.cs
or a new BusOps.cs.

### 5.6 Already absorbed by the refactor (**no longer exists in codebase**)

Phase 5.8 generalised these 27 LR35902 ops into L1 ops:

```
push_qq / pop_qq                                   -> push_pair / pop_pair
jp / jp_cc / jr / jr_cc / call / call_cc /
  ret / ret_cc / rst                               -> branch / branch_cc / call / call_cc / ret / ret_cc
cb_bit / cb_bit_hl_mem / cb_set / cb_resset_hl_mem / cb_res
                                                   -> bit_test / bit_set / bit_clear
cb_shift / cb_shift_hl_mem                          -> shift {kind: ...}
ARotateEmitter (rlca/rrca/rla/rra)                 -> shift {kind: rlc/rrc/rl/rr} + set_flag(Z=0)
ldh_io_load / ldh_io_store                          -> or {const: 0xFF00} + load_byte/store_byte
scf / ccf / cpl                                     -> set_flag / toggle_flag / mvn + set_flag
inc_pair / dec_pair / inc_r8 / dec_r8 /
  inc_hl_mem / dec_hl_mem / inc_rr_dd / dec_rr_dd  -> read + add/sub + trunc + write + update_zero/h_inc/h_dec
SimpleNoOpEmitter (cb_dispatch)                    -> empty steps in spec
```

All these previously-LR35902-only instruction patterns are now
described in the spec via generic op chains, and the emitter side
needs no LR35902-specific code.

---

## 6. Side-by-side comparison of the two emitters (post Phase 5.8)

| Aspect | ArmEmitters | Lr35902Emitters |
|---|---|---|
| Line count (post Phase 5.8) | ~870 | **1346** (was ~2500) |
| Op count | ~22 | **~13** (was ~40) |
| Primary GPR width | uniform 32-bit | mixed 8-bit + 16-bit (SP/PC/pair) |
| Flag layout | CPSR bits 31..28 (NZCV) + 7 (I) + 5 (T) | F bits 7..4 (ZNHC) |
| Special flag arithmetic | C, V (signed overflow) | H (half-carry, bit 3 -> 4), DAA |
| Mode / banking | 7 modes, banked R8-R14 | none |
| Pipeline offset | yes (PC = current+8 ARM, +4 Thumb) | none |
| Multi-instruction-set switching | CPSR.T persistent state | 0xCB prefix instantaneous |
| Host externs | swap_register_bank (on mode switch) | halt + set_ime + arm_ime_delayed |
| Memory access | generic load/store + block_load/store | load_byte/store_byte/store_word + read_imm |
| Conditional control flow | `if_arm_cond` (per-instruction prefix) + `branch_indirect` | generic `branch_cc` (added Phase 5.8.3) |
| Field-driven r selection | direct `read_reg index="rn"` | `lr35902_read_r8` (sss=6 means memory) |
| BCD | none | DAA |

Differences in "shape" between ARM and LR35902 still determine
emitter volume, but post Phase 5.8, LR35902's "ALU/flag/control flow
wrapping layer" has all moved down to L1 generic ops, leaving 13
true L3 ops.

---

## 7. How emitters cooperate with other layers

A typical "LD A, B" (opcode 0x78) goes from spec to native code via:

```
1. spec/lr35902/groups/block1-ld-reg-reg.json:
   { "name": "LdReg_Reg", "pattern": "01dddsss", ...
     "instructions": [{ "mnemonic": "LD", "steps": [
       { "op": "lr35902_read_r8",  "field": "sss", "out": "src" },
       { "op": "lr35902_write_r8", "field": "ddd", "value": "src" } ]}] }

2. SpecLoader.LoadCpuSpec -> becomes InstructionSetSpec object

3. SpecCompiler.Compile:
   - StandardEmitters.RegisterAll(registry)
     -> all L0/L1 ops registered (StackOps/FlagOps/BitOps included)
   - Lr35902Emitters.RegisterAll(registry)
     -> registry["lr35902_read_r8"] = Lr35902ReadR8Emitter
     -> registry["lr35902_write_r8"] = Lr35902WriteR8Emitter
     -> and 13 other L3 ops
   - For each instruction, run InstructionFunctionBuilder.Build:
     - Build LLVM function: void Execute_Main_LdReg_Reg_LD(state*, ins)
     - Add attributes: nounwind, no-jump-tables (lifesaver for Windows MCJIT)
     - For each step, find emitter, call Emit(ctx, step)
       -> ReadR8Emitter generates: select chain picking src from 7 GPRs
       -> WriteR8Emitter generates: select-merge stores writing to corresponding GPR
     - BuildRetVoid

4. HostRuntime.Compile:
   - BindUnboundExternsToTrap (memory_read_8 / write_8 / write_16)
   - JsonCpu's BindExtern sets these globals' initializers to inttoptr(C# fn addr)
   - module.CreateMCJITCompiler with OptLevel=3 (Phase 7 B.a) -> machine code

5. JsonCpu.StepOne (or CpuExecutor.Step):
   - Read opcode 0x78 at PC
   - DecoderTable.Decode(0x78)
     -> traverses pre-built entries list (Phase 7 F.y: returns cached
        DecodedInstruction, no longer new'd each time)
   - ResolveFunctionPointer(decoded.Instruction)
     -> Phase 7 F.x: identity-keyed cache; no longer string-format and
        dictionary string-hash each time
   - Cast IntPtr to (state*, uint) -> void
   - Call -> runs native code, state buffer contents updated
```

---

## 8. Boundaries and evolution direction

### 8.1 "Insufficiently generalised" parts already absorbed in Phase 5.8

- **CB-prefix bit/shift dispatch** — 7 LR35902 ops -> 4 generic ops (Phase 5.8.4)
- **Push/pop/call/ret** — 11 LR35902 ops -> 4 generic ops (Phase 5.8.1)
- **Conditional branch** — multiple _cc emitters -> branch_cc + call_cc + ret_cc (Phase 5.8.3)
- **F register flag micro-ops** — set_flag/toggle_flag/update_h_*/update_zero (Phase 5.8.2/7)
- **A-rotate (RLCA/RRCA/RLA/RRA)** — absorbed into generic shift (Phase 5.8.4.B)
- **LDH IO region addressing** — absorbed into `or` + auto-coerce (Phase 5.8.5)
- **INC/DEC family** — absorbed into generic add/sub + trunc + flag updates (Phase 5.8.7.B)

### 8.2 Phase 5.8 deliberately left at L3

- **Operand resolver registry**: `lr35902_read_r8/write_r8/write_rr_dd`
  remain LR35902-specific; need to wait for the third CPU to know
  whether a spec-side resolver registry is needed
- **Compound ALU + flag rules**: `alu_a_*` / `add_hl_rr` / `add_sp_e8` /
  `ld_hl_sp_e8` would explode the spec if split into multiple steps;
  kept as L3
- **True hardware quirks**: DAA, IME delay, HALT bug — these should
  not be generalised
- **bus-level helpers**: `load_byte` / `store_byte` etc. have generic
  op names but implementation still in Lr35902Emitters.cs; will move
  to MemoryEmitters later

### 8.3 Workload estimate for adding a third CPU (post Phase 5.8)

Assume adding RISC-V RV32I (the cleanest 32-bit RISC):
- spec/riscv32/cpu.json + groups/*.json (~10-15 group files, simpler than ARM)
- Riscv32Emitters.cs: **estimated 200-400 lines** (vs 1000+ before Phase 5.8)
  - Mainly mstatus / mtvec exception entry, possibly 0-3 RISC-V-unique
    intrinsics (ECALL / EBREAK / FENCE)
  - L0/L1 generic ops cover 99% of ALU / load/store / branch
- Framework code: **zero modification** (unless operand resolver or
  compound ALU generalisation is needed)
- Estimated time: 3-5 working days (vs ~1 week+)

Adding a second 8-bit CPU (e.g. Z80 or 6502):
- 8-bit + paired-register helpers already in LR35902 file, can be
  promoted to generic
- Most of the 13 L3 ops left in LR35902 have analogues in Z80 (more
  even); 6502 is simpler
- Estimated time: 5-7 working days

### 8.4 Phase 7 JIT optimisation in progress (started 2026-05-03)

After the refactor wrap-up, emitter volume converged cleanly enough
to start attacking perf. Phase 7 has seven groupings A-G (block-JIT /
IR inlining / lazy flag / hot-path tier / mem-bus fast path /
dispatcher cleanup / .NET AOT), ordered low-risk to high-risk.

Completed (as of 2026-05-03 morning):

| Step | Action | Result |
|---|---|---|
| **B.a** OptLevel 0 -> 3 | one-line config | perf-neutral (per-instr functions too small for LLVM to make use of) — kept in place as preparation for block-JIT |
| **F.x** Identity-keyed fn ptr cache | dispatcher cache changed to InstructionDef references | **GB json-llvm +82%** (2.66 -> 4.83 MIPS), GBA Thumb +25% (3.75 -> 4.69) |

Detailed numbers + strategy ROI analysis are in the
`MD/performance/` series; one note per strategy, all referencing
canonical baseline `202605030002-...`.

### 8.5 How the framework itself can improve

- **Move ARM update_* ops to spec metadata as well**: FlagOps
  already absorbed the LR35902 side; ARM's update_nz / update_c_* etc.
  can also become L1. To be done after Phase 7 stabilises.
- **ORC LLJIT replacing MCJIT**: resolves the historical baggage of
  Windows COFF section ordering, supports lazy compile + re-jit.
  Prerequisite for Phase 7 group A.
- **Block JIT replacing instruction JIT**: fuse N consecutive
  instructions into a single LLVM function, saving dispatch overhead
  (Phase 7 group A main course).
- **Cycle accounting hoisted to spec**: currently the
  "taken extra cycles" of conditional branches is in JsonCpu's C#
  side; ideally the emitter should auto-generate it from spec's
  `cycles.form`.
- **Bus-level helpers moved out of LR35902Emitters**: load_byte etc.
  already have generic names; moving them to MemoryEmitters fits
  the layering.

---

## 9. One-line summary

**JSON describes the CPU's "shape", emitters describe "the precise
semantics of each verb". After Phase 5.8, emitters are further split
into L0/L1 (cross-CPU shared) and L3 (CPU-unique), pushing the
credibility of the "swap CPU = swap JSON" promise from "verified on
ARM/LR35902 two CPUs" to "adding the third CPU only requires writing
~5-10 L3 ops + configuring metadata".**

Together with the generic framework code (SpecCompiler / HostRuntime /
DecoderTable), a CPU's spec becomes JIT-compiled native code. The
framework itself does not need to change for a new CPU; the new CPU
only needs spec + an increasingly thin emitter, and the previous CPU
remains unaffected.

This thesis is verified on ARM7TDMI (already passes jsmolka armwrestler
/ arm.gba / thumb.gba green) and LR35902 (already passes Blargg
cpu_instrs 11/11 + master "Passed all tests"), two completely
different CPUs. The Phase 5.8 emitter library refactor further
brought the emitter workload of each new CPU down to "~5-10 L3 ops
+ configuration".
