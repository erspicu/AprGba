# Micro-op Vocabulary (Reference)

This document is the reference spec for the **complete set of base micro-ops** usable in `steps[]` of the AprCpu schema (`04-json-schema-spec.md`). The parser maintains an LLVM IR emitter for each micro-op internally; when adding instructions, prefer composing these ops, and only declare `custom_micro_ops` when they don't suffice.

---

## 0. General Conventions

- **Width attribute**: arithmetic / logical / shift ops default to `width: 32` and can be overridden per-step (e.g. Thumb 8-bit immediate load uses `width: 8` + zero-extend).
- **`out` naming**: each step's `out` must be unique within the current instruction's scope. SSA-friendly.
- **`in` / `value`**: can be a "variable name string", `{ "const": ... }`, or `{ "field": ... }`. The parser auto-infers types and inserts zero/sign extends as needed.
- **Side effects**: ops marked ⚠ modify global CPU state (CPSR/PC/memory) and require special handling by the JIT block compiler.
- **LLVM IR mapping**: the column gives the **typical** mapping; actual emit may insert `zext`/`sext`/`trunc` due to type promotion.

---

## 1. Arithmetic

| op | inputs | out | LLVM IR | Description |
|---|---|---|---|---|
| `add`  | `in: [a, b]`        | `out` | `add` | Two's-complement add (no carry) |
| `adc`  | `in: [a, b, cin]`   | `out` | `add` ×2 | Add with Carry-in |
| `sub`  | `in: [a, b]`        | `out` | `sub` | a − b |
| `sbc`  | `in: [a, b, cin]`   | `out` | `sub` + adjust | a − b − !cin |
| `rsb`  | `in: [a, b]`        | `out` | `sub` | b − a (Reverse Sub) |
| `rsc`  | `in: [a, b, cin]`   | `out` |  | b − a − !cin |
| `mul`  | `in: [a, b]`        | `out` | `mul` | 32×32 → 32 (low half) |
| `mul_hi` | `in: [a, b]`      | `out` | `mul` + shift | High 32 bits |
| `umul64` | `in: [a, b]`      | `out` (i64) | `zext`+`mul` | 32×32 → 64 unsigned |
| `smul64` | `in: [a, b]`      | `out` (i64) | `sext`+`mul` | 32×32 → 64 signed |
| `neg`  | `in: [a]`           | `out` | `sub 0` | −a |
| `abs`  | `in: [a]`           | `out` | `select` | |a| |
| `min` / `max` | `in: [a, b], signed: bool` | `out` | `select` |  |

---

## 2. Logical

| op | inputs | out | LLVM IR | Description |
|---|---|---|---|---|
| `and`  | `in: [a, b]` | `out` | `and` |  |
| `or`   | `in: [a, b]` | `out` | `or`  |  |
| `xor`  | `in: [a, b]` | `out` | `xor` |  |
| `not`  | `in: [a]`    | `out` | `xor -1` | bitwise NOT |
| `bic`  | `in: [a, b]` | `out` | `and a, ~b` | a AND NOT b |

---

## 3. Shift / Rotate

| op | inputs | out | LLVM IR | Description |
|---|---|---|---|---|
| `shl`  | `in: [v, n]` | `out`, `carry_out?` | `shl` | logical shift left |
| `lsr`  | `in: [v, n]` | `out`, `carry_out?` | `lshr` | logical shift right |
| `asr`  | `in: [v, n]` | `out`, `carry_out?` | `ashr` | arithmetic shift right |
| `ror`  | `in: [v, n]` | `out`, `carry_out?` | intrinsic `fshr` | rotate right |
| `rrx`  | `in: [v, cin]` | `out`, `carry_out?` | `lshr 1` + insert | rotate right through carry |

ARM Barrel Shifter behavior (n=0 / n=32 / n>32 edge cases) is handled by `operands.shifted_register_*` at the emit stage; the micro-ops themselves do pure shifts only.

---

## 4. Compare / Test

| op | inputs | out | LLVM IR |
|---|---|---|---|
| `cmp_eq`  | `in: [a, b]` | `out: i1` | `icmp eq`  |
| `cmp_ne`  | `in: [a, b]` | `out: i1` | `icmp ne`  |
| `cmp_ult` | `in: [a, b]` | `out: i1` | `icmp ult` |
| `cmp_slt` | `in: [a, b]` | `out: i1` | `icmp slt` |
| `cmp_ule` | `in: [a, b]` | `out: i1` | `icmp ule` |
| `cmp_sle` | `in: [a, b]` | `out: i1` | `icmp sle` |

`CMP`/`TST` instruction implementations typically don't use these ops directly; they execute sub/and and update flags (see §6). These are reserved for control-flow conditional checks.

---

## 5. Bit Manipulation

| op | inputs | out | Description |
|---|---|---|---|
| `bitfield_extract` | `in: [v], lsb, width, signed: bool` | `out` | Extract bit field |
| `bitfield_insert`  | `in: [dst, val], lsb, width` | `out` | Insert bit field |
| `clz`              | `in: [v]` | `out` | Count Leading Zeros (ARMv5+) |
| `popcount`         | `in: [v]` | `out` | (future) |
| `byte_swap`        | `in: [v]` | `out` | endian swap (REV / BSWAP) |
| `sign_extend`      | `in: [v], from_bits` | `out` | sign-extend to target width |
| `zero_extend`      | `in: [v], from_bits` | `out` | zero-extend to target width |
| `truncate`         | `in: [v], to_bits` | `out` | Take low n bits |
| `bit_test`  | `in: [v], bit_index` | `out: i1` | Test specified bit (LR35902 BIT b,r → Z=!result) |
| `bit_set`   | `in: [v], bit_index` | `out` | Set specified bit to 1 (SET b,r) |
| `bit_clear` | `in: [v], bit_index` | `out` | Clear specified bit to 0 (RES b,r) |
| `shift`     | `in: [v, n], kind, width: 8\|16\|32, capture_carry: bool` | `out`, `carry_out?` | Unified shift wrapper (kind: `lsl`/`lsr`/`asr`/`rol`/`ror`/`rolc`/`rorc`/`swap_nibbles`); added in 5.4 refactor for shared use by LR35902 CB-shift and A-rotate |
| `sext`      | `in: [v], from_bits, to_bits` | `out` | width-aware sign-extend (added in 5.3; uses `to_bits` to explicitly specify target width without relying on step.width default) |
| `trunc`     | `in: [v], to_bits` | `out` | width-aware truncate (added in 5.7.B; same rationale; `truncate` already exists but infers target from step.width) |

---

## 6. Flag Update ⚠

Writes directly into the corresponding status register bit; the JIT side can do lazy-flag optimization.

| op | Description | Trigger |
|---|---|---|
| `update_n`       | N = result[31] | All flag-updating instructions |
| `update_z`       | Z = (result == 0) | Same |
| `update_nz`      | Updates both N and Z | Shorthand |
| `update_zero`    | Z = (result == 0), width-aware (added in 5.7.B; for 8/16-bit ALU; `update_z` defaults to i32 width) | LR35902 INC/DEC r |
| `update_c_add`   | C = (rn + op2) > 0xFFFFFFFF (unsigned overflow) | ADD/ADC/CMN |
| `update_c_sub`   | C = !(rn − op2) borrow (i.e. rn ≥ op2) | SUB/SBC/CMP/RSB |
| `update_c_shifter` | C = shifter_carry_out | logical/MOV with shift |
| `update_v_add`   | V = signed overflow of (rn + op2) | ADD/ADC/CMN |
| `update_v_sub`   | V = signed overflow of (rn − op2) | SUB/SBC/CMP/RSB |
| `update_q`       | Q = saturation occurred | DSP ext (ARMv5TE) |
| `update_h_add`   | H = ((a & 0xF) + (b & 0xF)) > 0xF (half-carry on add; added in 5.2) | LR35902 ADD/ADC/INC |
| `update_h_sub`   | H = (a & 0xF) < (b & 0xF) (half-carry on sub; added in 5.2) | LR35902 SUB/SBC/CP/DEC |
| `update_h_inc`   | H = ((a & 0xF) == 0xF) (INC-specific short-form; 5.7.B) | LR35902 INC r |
| `update_h_dec`   | H = ((a & 0xF) == 0) (DEC-specific short-form; 5.7.B) | LR35902 DEC r |
| `set_flag`       | `flag, value` directly specified | edge cases / SCF |
| `clear_flag`     | `flag` | edge cases |
| `toggle_flag`    | `flag`: flag = !flag (added in 5.7.A) | LR35902 CCF |

Example: `{ "op": "update_c_add", "in": ["rn_val", "op2_value"] }`

---

## 7. Register I/O ⚠ on write

| op | inputs | out | Description |
|---|---|---|---|
| `read_reg`  | `index: <field-or-int>` | `out` | Auto-handles banked register based on mode |
| `write_reg` | `index, value`           | (none) | Same; if index==15 it's equivalent to writing PC |
| `read_pc`   | (none) | `out` | Read current PC (under block-JIT mode resolved to baked-in constant; per-instr mode falls back to GPR[pc_index] load); added in 5.3 for LR35902 PC-relative |
| `read_reg_pair`  | `pair_name` | `out` (i16) | Read 8-bit pair (e.g. BC/DE/HL/AF), auto-composes `(high << 8) \| low`; requires `register_pairs` declared in spec |
| `write_reg_pair` | `pair_name, value` | (none) | Write 8-bit pair, auto-decomposes `value >> 8 → high; value & 0xFF → low` |
| `push_pair` | `value` (i16) | (none) | SP -= 2; `store_halfword(SP, value)` (added in 5.1; uses spec's `stack_pointer` metadata) |
| `pop_pair`  | (none) | `out` (i16) | `out = load_halfword(SP); SP += 2` (added in 5.1) |
| `read_psr`  | `which: "CPSR" \| "SPSR"` | `out` | Read (current mode's SPSR) |
| `write_psr` | `which, value, mask?`   | (none) | mask specifies which bits are writable |
| `restore_cpsr_from_spsr` | (none) | (none) | mode return (e.g. ALU writing PC + S-bit) |
| `swap_registers_for_mode` | `mode_id` | (none) | Forcibly perform banked register swap (internal use) |

**Special**: when `write_reg`'s target is PC, the emitter automatically:
1. Checks the low bit of `value` (Thumb switch rule, `bx` / `mov pc, ...`)
2. Aligns to the instruction set width
3. Triggers `block_terminate("indirect_branch")`

---

## 8. Immediate / Operand Resolution

Usually declared via the format's `operands` section rather than called directly; for low-level use:

| op | inputs | out | Description |
|---|---|---|---|
| `imm_rotated` | `imm8, rotate4` | `value`, `shifter_carry_out` | ARM 8-bit imm + 2*rotate |
| `imm_sign_extend` | `value, from_bits` | `out` | Sign-extend immediate |
| `pc_relative_address` | `offset` | `address` | PC (with pipeline offset) + offset |

---

## 9. Memory Access ⚠

| op | inputs | out | Description |
|---|---|---|---|
| `load`         | `addr, size: 8|16|32|64, signed: bool` | `value` | Read via memory bus, alignment policy applied |
| `store`        | `addr, value, size`                    | (none)  | Write memory; triggers SMC barrier check |
| `load_aligned` | `addr, size`                            | `value` | bypass alignment policy (known to be aligned) |
| `swap_word`    | `addr, value`                           | `old`   | SWP instruction's atomic exchange |
| `block_load`   | `base_addr, register_list, mode`       | (none)  | LDM/LDMIA/LDMFD family |
| `block_store`  | `base_addr, register_list, mode`       | (none)  | STM/STMIA/STMFD family |

`block_load`/`block_store` `mode` enumeration:
- `IA` (Increment After) / `IB` (Increment Before)
- `DA` (Decrement After) / `DB` (Decrement Before)
- `FD`/`FA`/`ED`/`EA` (stack-variant aliases)

---

## 10. Control Flow ⚠

| op | Required fields | Description |
|---|---|---|
| `if`              | `cond`, `then`, `else?` | Conditional branch (emit `br i1`) |
| `switch`          | `selector`, `cases`, `default?` | Multi-way dispatch (emit `switch i32`) |
| `branch`          | `target` | Immediate unconditional jump; performs `block_terminate` |
| `branch_link`     | `target` | BL: writes LR = next_pc then jumps |
| `branch_indirect` | `target_value` | BX-style; auto-handles T-bit and alignment |
| `branch_cc` | `cond_field, target` | conditional branch — eval cond then jump; added in 5.3 for LR35902 JR cc / JP cc |
| `call`      | `target` | Unified call wrapper: push next_pc → SP, PC = target; added in 5.3 for LR35902 CALL and RST |
| `call_cc`   | `cond_field, target` | conditional call; 5.3 |
| `ret`       | (none) | pop SP → PC (auto-handles PC alignment + block_terminate); 5.3 |
| `ret_cc`    | `cond_field` | conditional return; 5.3 |
| `target_const` | `value: int` | Constant branch target; added in 5.3 (replaces ambiguous inline `{ "const": ... }` style) |
| `block_terminate` | `reason`, `next_pc?` | Explicitly end the block, for the JIT |
| `nop`             |  | Truly does nothing (eliminable at compile time) |

**`reason` enumeration (for the block compiler to interpret)**:
- `branch_taken` / `branch_link` / `branch_indirect`
- `mode_change` / `instruction_set_switch`
- `software_interrupt` / `undefined_instruction`
- `io_barrier` (write to IO register; needs PPU/Timer sync)
- `breakpoint` / `wait_for_interrupt`

---

## 11. Exception / System ⚠

| op | Description |
|---|---|
| `raise_exception` | `vector: "SoftwareInterrupt" | "UndefinedInstruction" | ...` Full flow: save CPSR→SPSR, save next-PC→banked LR, switch mode and disable bits, call host_swap_register_bank, PC ← vector address |
| `restore_cpsr_from_spsr` | Use runtime CPSR.M to select the SPSR_<mode> banked slot and reload into CPSR; with host_swap_register_bank notification |
| `enter_mode`      | `mode_id`, with banked register swap and SPSR save (shares an internal helper with raise_exception) |
| `if_arm_cond`     | Uses per-instruction `cond_field` (4-bit) to gate the then-block; for Thumb F16 conditional branches (Thumb has no global cond gate) |
| `disable_interrupts` | `mask: ["I","F"]` |
| `enable_interrupts`  | `mask: ["I","F"]` |
| `wait_for_interrupt` | (ARMv6+) WFI etc. |
| `coprocessor_call`   | `cp_num, op1, crd, crn, crm, op2` (MCR/MRC, available in ARMv4) |
| `breakpoint`         | BKPT (ARMv5+) |

### `raise_exception` Detailed Fields

```json
{ "op": "raise_exception", "vector": "SoftwareInterrupt" }
```

`vector` must correspond to a `name` in the spec's `exception_vectors[]`. The emitter reads `enter_mode` and `disable` (a list of CPSR bit names to set, e.g. `["I"]`, `["I","F"]`) from the vector entry, so the step doesn't need to redeclare them.

Full lowered sequence (see `ArmEmitters.RaiseExceptionEmitter`):
1. read CPSR → `old_cpsr`
2. If SPSR is banked and the mode has an SPSR slot: store old_cpsr →
   `SPSR_<enter_mode>`
3. Compute next-PC = R15 − (pc_offset_bytes − instruction_size_bytes)
4. store next-PC → that mode's banked R14 slot (if any)
5. Compute new_cpsr = (old_cpsr & ~M_mask) | new_mode_enc | OR(disable bits...)
6. store new_cpsr → CPSR
7. call extern `host_swap_register_bank(state, old_mode, new_mode)`
8. store vector.address → R15

### `restore_cpsr_from_spsr` Detail

No fields. Emitted as a runtime switch over CPSR.M:
- For each mode the spec declares as `banked_per_mode` (FIQ/IRQ/Supervisor/
  Abort/Undefined), emit a case loading the corresponding SPSR_<mode>
- The default arm (User/System mode etc., no SPSR slot) preserves the old CPSR
- Merge with PHI, write back to CPSR
- End with `host_swap_register_bank(state, old_mode, new_mode)` call

### `if_arm_cond` Detail

```json
{
  "op": "if_arm_cond",
  "cond_field": "cond",
  "then": [ ...nested steps... ]
}
```

`cond_field` points to a 4-bit field in the format whose contents are an ARM cond code (0-15). The emitter uses `ConditionEvaluator.EmitCheckOnCondValue` (this helper was extracted from the global cond gate logic) to build a cond gate around the `then` block. Thumb F16 conditional branches use this op because Thumb has no instruction-set-level global condition.

---

## 12. Sync / Barrier ⚠

| op | Description |
|---|---|
| `cycle_advance`    | `count`: manually advance the cycle counter (internal use) |
| `io_write_barrier` | Notify host that an IO write is complete; external hardware needs sync |
| `dmb` / `dsb` / `isb` | (ARMv7+) memory barrier family; treated as nop in ARMv4T |

---

## 13. JIT / Debug Aids

| op | Description |
|---|---|
| `host_callback`     | `name, args`: directly invoke a C#-registered callback (for HLE BIOS, debug log) |
| `assert`            | `cond`: trap if false in dev mode |
| `trace_event`       | `event_name, args`: tracing |
| `mark_block_boundary` | (hint to the block compiler; usually stripped at emit) |

---

## 14. Lazy Flag Computation (future optimization)

In the long term, *lazy flags* will be introduced: do not compute N/Z/C/V immediately, but record "the operands and kind of the last update", and only compute when an instruction actually reads the flags. Interface:

| op | Description |
|---|---|
| `lazy_flags_set` | `kind, operands, result`: register but don't compute |
| `lazy_flags_resolve` | Actually compute the flags and write to CPSR |

The first version uses `update_*` directly; switching can happen later without affecting how specs are written (the parser internally chooses the implementation).

---

## 15. Custom Micro-ops (in-spec extension)

Declaration:

```json
"custom_micro_ops": [
  {
    "name": "bcd_add",
    "inputs":  [ {"name":"a","width":8}, {"name":"b","width":8}, {"name":"cin","width":1} ],
    "outputs": [ {"name":"sum","width":8}, {"name":"cout","width":1} ],
    "summary": "8-bit BCD add for 6502 D=1 mode"
  }
]
```

C# side implements the `IMicroOpEmitter` interface:

```csharp
public interface IMicroOpEmitter
{
    string OpName { get; }
    void Emit(IRBuilderContext ctx, MicroOpStep step);
    IReadOnlyList<string> InputNames  { get; }
    IReadOnlyList<string> OutputNames { get; }
}
```

Once registered, any spec's `steps` can reference it via `{ "op": "bcd_add", ... }`.

---

## 16. Full List (quick reference)

```
arithmetic   : add, adc, sub, sbc, rsb, rsc, mul, mul_hi, umul64, smul64,
               neg, abs, min, max
logical      : and, or, xor, not, bic
shift        : shl, lsr, asr, ror, rrx, shift (5.4 unified wrapper)
compare      : cmp_eq, cmp_ne, cmp_ult, cmp_slt, cmp_ule, cmp_sle
bit          : bitfield_extract, bitfield_insert, clz, popcount,
               byte_swap, sign_extend, zero_extend, truncate,
               bit_test, bit_set, bit_clear, sext, trunc
flag         : update_n, update_z, update_nz, update_zero,
               update_c_add, update_c_sub, update_c_shifter,
               update_v_add, update_v_sub, update_q,
               update_h_add, update_h_sub, update_h_inc, update_h_dec,
               set_flag, clear_flag, toggle_flag
register     : read_reg, write_reg, read_pc,
               read_reg_pair, write_reg_pair, push_pair, pop_pair,
               read_psr, write_psr,
               restore_cpsr_from_spsr, swap_registers_for_mode
operand      : imm_rotated, imm_sign_extend, pc_relative_address
memory       : load, store, load_aligned, swap_word,
               block_load, block_store
control      : if, if_arm_cond, switch, branch, branch_link,
               branch_indirect, branch_cc,
               call, call_cc, ret, ret_cc, target_const,
               block_terminate, nop
exception    : raise_exception, restore_cpsr_from_spsr, enter_mode,
               disable_interrupts, enable_interrupts,
               wait_for_interrupt, coprocessor_call, breakpoint
barrier      : cycle_advance, io_write_barrier, dmb, dsb, isb
jit_aux      : host_callback, assert, trace_event, mark_block_boundary
lazy_flag    : lazy_flags_set, lazy_flags_resolve
```

A total of **~95 base micro-ops** (expanded after the 5.1-5.7 emitter refactor + Phase 4.5 LR35902 validation), enough to cover the full ARMv4T instruction set, the full LR35902 (GB DMG) instruction set, and most classic 8/16/32-bit RISC/CISC architectures (6502, Z80, MIPS, SH-2, etc.).

---

## 17. Design Decisions Explained

### 17.1 Why split flag computation into `update_c_add` / `update_c_sub`?

ARM defines carry differently for add and sub (sub's C is NOT borrow). Encoding this distinction into the op name means the spec doesn't need to explain the detail; the emitter knows directly which IR to emit.

### 17.2 Why is PC writing handled implicitly by `write_reg` rather than as a standalone op?

Because "writing R15" in ARM can occur in many instructions (MOV, ADD, LDR, etc.). Encapsulating PC logic inside `write_reg` keeps the spec concise; on the JIT side, `write_reg(15, ...)` automatically inserts `block_terminate`.

### 17.3 Why is condition execution at the format level rather than a micro-op?

ARM's cond is a prefix gate on the "whole instruction". If every instruction prepended an `if cond`, the spec would be very verbose. Instead, declare `global_condition` at the instruction-set level; the parser auto-wraps the entire step block in `if` at emit time.

### 17.4 Why don't `update_*` directly take the result?

Some flags (V, C) need operands rather than the result (signed/unsigned overflow detection). Standardize on passing operands; the emitter decides internally how to compute.
