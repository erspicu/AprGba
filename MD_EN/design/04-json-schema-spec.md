# CPU Spec JSON Schema Design

This document defines the CPU spec description format used by the AprCpu framework. A spec file (or set of spec files) fully describes a single CPU, including the register architecture, mode switching, instruction encoding formats, semantic micro-ops, cycle costs, and exception vectors. Once the parser ingests the spec, it can produce the corresponding decoder, IR emitter, and execution logic.

---

## 1. Design Goals

| Goal | Concrete Requirement |
|---|---|
| **Completeness** | Can fully describe ARM7TDMI; architecture extensible to ARMv5/v6/v7-A, 6502, MIPS |
| **Multi-mode instruction widths** | Supports 16/32-bit switching (ARM/Thumb), Thumb-2 variable-width mixed, future ARM64 pure 32 |
| **CPU model differentiation** | The same architecture can have multiple variants (ARM7TDMI, ARM946E-S) sharing a base spec |
| **Encoding-space coverage** | Use mask/match to express RISC's regular encoding; auto-generate decoding trees |
| **Executable semantics** | Micro-op sequences can directly emit LLVM IR; no need to hand-write each instruction |
| **Inheritable extension** | An ARMv5 spec can use `extends: ARMv4T` to inherit and override |
| **Machine-verifiable** | A corresponding JSON Schema can be auto-linted in IDE / CI |
| **Human-readable** | The encoding and semantics should be understandable by reading the JSON directly (avoid over-abbreviation) |

## 2. Design Principles

1. **Data-first**: spec files are pure data, with zero execution logic. All "behavior" is expressed via named micro-ops, whose implementations live in the parser (not in the spec).
2. **Encoding-based description**: the core unit is "instruction format" rather than enumerated opcodes. One format covers a family of instructions, with sub-opcode dispatch internally.
3. **Explicit over implicit**: bit ranges, register indices, and immediate sources are all explicitly annotated; no "infer by convention".
4. **Modular files**: a top-level `cpu.json` describes the CPU model, and each instruction set lives in its own file (`arm.json`, `thumb.json`) for maintainability.
5. **Versioning**: top-level `spec_version` records the schema version, enabling smooth migration on future revisions.

---

## 3. File Organization

```
spec/
├── schema/
│   └── cpu-spec.schema.json         # JSON Schema (Draft 2020-12)
├── arm7tdmi/
│   ├── cpu.json                     # CPU 模型（regs、modes、vectors）
│   ├── arm.json                     # ARM (32-bit) instruction set
│   └── thumb.json                   # Thumb (16-bit) instruction set
└── mos6502/                         # （未來擴充示範）
    ├── cpu.json
    └── main.json
```

`cpu.json` points to other files in the same directory via the `instruction_sets` field. The parser starts with `cpu.json` and pulls up the full model from there.

---

## 4. Top-Level Structure (cpu.json)

```json
{
  "$schema": "../schema/cpu-spec.schema.json",
  "spec_version": "1.0",

  "architecture": {
    "id":         "ARMv4T",
    "family":     "ARM",
    "extends":    null,
    "endianness": "little",
    "word_size_bits": 32
  },

  "variants": [
    {
      "id":   "ARM7TDMI",
      "core": "ARM7",
      "features": ["T", "D", "M", "I"],
      "notes": "GBA / NDS ARM core"
    }
  ],

  "register_file": { /* §5 */ },
  "processor_modes": { /* §6 */ },
  "exception_vectors": [ /* §7 */ ],

  "instruction_sets": [
    { "name": "ARM",   "file": "arm.json"   },
    { "name": "Thumb", "file": "thumb.json" }
  ],

  "instruction_set_dispatch": {
    "selector":       "CPSR.T",
    "selector_values": { "0": "ARM", "1": "Thumb" },
    "switch_via":     ["BX", "BLX"],
    "transition_rule":
      "Target address bit[0]==1 → switch to Thumb (T←1) and clear bit[0] before fetch."
  },

  "memory_model": {
    "default_endianness": "little",
    "alignment_policy": {
      "load_unaligned":  "rotate_within_word",
      "store_unaligned": "force_align_low_bits"
    }
  }
}
```

### 4.1 `architecture`
- `id`: unique identifier (`ARMv4T`, `ARMv5TE`, …)
- `family`: broad category (`ARM`, `MIPS`, `6502`, …)
- `extends`: if inheriting from another spec, fill in its `id` to inherit register/mode/format definitions; the current spec only needs to describe the differences
- `endianness`: default byte order; can be overridden by `memory_model` or individual instructions
- `word_size_bits`: primary word size (used for decode-tree grouping and immediate-value upper-bound reference)

### 4.2 `variants`
Table of CPU models under the same architecture. `features` corresponds to ARM suffix tags (T=Thumb, D=Debug, M=Multiplier, I=ICE, E=DSP extensions, J=Jazelle, …); the framework uses these to enable the corresponding instruction-set sections.

### 4.3 `instruction_sets`
Lists the spec files for each mode. The parser switches the decode path according to `instruction_set_dispatch.selector` (a particular bit in the CPU status register).

### 4.4 `memory_model`
Describes "all-CPU-common" memory access behavior. Per-instruction exceptions (e.g. LDM/STM's special handling of PC) are annotated in the instruction definition's `quirks`.

---

## 5. Register File (`register_file`)

```json
"register_file": {
  "general_purpose": {
    "count": 16,
    "width_bits": 32,
    "names": ["R0","R1","R2","R3","R4","R5","R6","R7",
              "R8","R9","R10","R11","R12","R13","R14","R15"],
    "aliases": { "SP": "R13", "LR": "R14", "PC": "R15" },
    "pc_index": 15
  },

  "status": [
    {
      "name": "CPSR",
      "width_bits": 32,
      "fields": {
        "N": "31", "Z": "30", "C": "29", "V": "28",
        "Q": "27",
        "I": "7",  "F": "6",  "T": "5",
        "M": "4:0"
      }
    },
    {
      "name": "SPSR",
      "width_bits": 32,
      "banked_per_mode": ["FIQ", "IRQ", "Supervisor", "Abort", "Undefined"]
    }
  ]
}
```

- `pc_index` explicitly states the PC location (ARM = 15, MIPS = implicit-in-special, 6502 = standalone).
- `aliases` provides convenient paths for micro-ops to reference SP/LR/PC.
- Status registers describe their bit layout via `fields`; micro-ops like `update_n`, `update_z` use this table to know which bit to write.
- `banked_per_mode` lists which processor modes have an independent copy (shadow register) of this register.

### 5.1 `register_pairs` (added in Phase 4.5)

For 8-bit-primary architectures like LR35902 (Game Boy), Z80, 6502, two 8-bit GPRs are paired into a 16-bit pair:

```json
"register_file": {
  "general_purpose": {
    "count": 7,
    "width_bits": 8,
    "names": ["A", "B", "C", "D", "E", "H", "L"]
  },
  "register_pairs": [
    { "name": "BC", "high": "B", "low": "C" },
    { "name": "DE", "high": "D", "low": "E" },
    { "name": "HL", "high": "H", "low": "L" },
    { "name": "AF", "high": "A", "low": "F" }
  ],
  "stack_pointer": "SP"
}
```

- `name` is the externally-visible pair name; spec steps / operand resolvers can use it to read/write directly.
- `high` / `low` map to 8-bit registers in `general_purpose.names`.
- The micro-ops `read_reg_pair` / `write_reg_pair` automatically emit the
  `(high << 8) | low` / `(value >> 8 & 0xFF) → high; value & 0xFF → low`
  compose/decompose IR.
- Pair names can also appear in `aliases` for SP/LR-style aliasing (LR35902's
  `stack_pointer: "SP"` is treated as a standalone 16-bit register, not a pair).

ARM7TDMI doesn't need this field (GPRs are all 32-bit); it's optional in the schema.

---

## 6. Processor Modes (`processor_modes`)

```json
"processor_modes": {
  "modes": [
    { "id": "User",       "encoding": "10000", "privileged": false },
    { "id": "FIQ",        "encoding": "10001", "privileged": true  },
    { "id": "IRQ",        "encoding": "10010", "privileged": true  },
    { "id": "Supervisor", "encoding": "10011", "privileged": true  },
    { "id": "Abort",      "encoding": "10111", "privileged": true  },
    { "id": "Undefined",  "encoding": "11011", "privileged": true  },
    { "id": "System",     "encoding": "11111", "privileged": true  }
  ],

  "banked_registers": {
    "FIQ":        ["R8","R9","R10","R11","R12","R13","R14"],
    "IRQ":        ["R13","R14"],
    "Supervisor": ["R13","R14"],
    "Abort":      ["R13","R14"],
    "Undefined":  ["R13","R14"],
    "System":     []
  }
}
```

`encoding` is the bit pattern of the CPSR.M field. The parser uses this to implement mode-switch logic and banked register swapping.

---

## 7. Exception Vectors (`exception_vectors`)

```json
"exception_vectors": [
  { "name": "Reset",            "address": "0x00000000",
    "enter_mode": "Supervisor", "disable": ["I","F"] },
  { "name": "UndefinedInstruction", "address": "0x00000004",
    "enter_mode": "Undefined" },
  { "name": "SoftwareInterrupt",    "address": "0x00000008",
    "enter_mode": "Supervisor", "disable": ["I"] },
  { "name": "PrefetchAbort",        "address": "0x0000000C",
    "enter_mode": "Abort",      "disable": ["I"] },
  { "name": "DataAbort",            "address": "0x00000010",
    "enter_mode": "Abort",      "disable": ["I"] },
  { "name": "IRQ",                  "address": "0x00000018",
    "enter_mode": "IRQ",        "disable": ["I"] },
  { "name": "FIQ",                  "address": "0x0000001C",
    "enter_mode": "FIQ",        "disable": ["I","F"] }
]
```

`disable` lists the interrupt flags that are automatically masked when entering the vector.

---

## 8. Instruction Sets (`arm.json` / `thumb.json`)

```json
{
  "$schema": "../schema/cpu-spec.schema.json",
  "spec_version": "1.0",

  "name":              "ARM",
  "width_bits":        32,
  "alignment_bytes":   4,
  "pc_offset_bytes":   8,
  "endian_within_word": "little",

  "global_condition": {
    "field": "31:28",
    "table": {
      "0000": "EQ",  "0001": "NE",  "0010": "CS",  "0011": "CC",
      "0100": "MI",  "0101": "PL",  "0110": "VS",  "0111": "VC",
      "1000": "HI",  "1001": "LS",  "1010": "GE",  "1011": "LT",
      "1100": "GT",  "1101": "LE",  "1110": "AL",  "1111": "NV"
    },
    "applies_to": "all_unless_marked_unconditional"
  },

  "decode_strategy": "mask_match_priority",

  "encoding_groups": [
    { "name": "DataProcessing", "formats": [ /* §9 */ ] },
    { "name": "PSR_Transfer",   "formats": [ ... ] },
    { "name": "Multiply",       "formats": [ ... ] },
    { "name": "LoadStore",      "formats": [ ... ] },
    { "name": "Branch",         "formats": [ ... ] },
    { "name": "Coprocessor",    "formats": [ ... ] }
  ]
}
```

### 8.1 `width_bits` / `alignment_bytes`
- ARM = 32/4, Thumb = 16/2
- Future Thumb-2 = `"variable"`, paired with a `width_decision` field describing how the instruction length is determined from the first 5 bits (see §13)

### 8.2 `pc_offset_bytes`
The ARM 3-stage pipeline causes R15 to read ahead. At runtime, reading PC automatically adds this value. ARM = +8, Thumb = +4.

### 8.3 `global_condition`
If all instructions have a 4-bit condition prefix (ARM classic mode), describe it once here to avoid repetition in every format. Exceptions to `applies_to`: `unconditional` (e.g. `BLX(label)`) are annotated at the per-instruction level via `unconditional: true`.

### 8.4 `decode_strategy`
- `mask_match_priority`: compare in declaration order; first match wins. Developer must guarantee no ambiguity.
- `mask_match_specificity`: auto-sort by the number of 1-bits in the mask (specific before generic)
- `tree`: parser auto-builds a decode tree (high build cost, fastest at runtime)

The first version supports only `mask_match_priority`.

---

## 9. Encoding Format

A format is a shared template for "a family of instructions". Below is an example for ARM Data Processing (register-shift-by-immediate variant):

```json
{
  "name": "DataProcessing_RegImmShift",
  "comment": "cccc 000 oooo s nnnn dddd ssss s tt 0 mmmm",

  "pattern": "cccc_000_oooo_s_nnnn_dddd_ssssstt0mmmm",
  "fields": {
    "cond":         "31:28",
    "opcode":       "24:21",
    "s_bit":        "20",
    "rn":           "19:16",
    "rd":           "15:12",
    "shift_amount": "11:7",
    "shift_type":   "6:5",
    "rm":           "3:0"
  },
  "mask":  "0x0E000010",
  "match": "0x00000000",

  "operands": {
    "op2": {
      "kind": "shifted_register_by_immediate",
      "rm":            "rm",
      "shift_type":    "shift_type",
      "shift_amount":  "shift_amount",
      "outputs": ["op2_value", "shifter_carry_out"]
    }
  },

  "instructions": [
    {
      "selector": { "field": "opcode", "value": "0100" },
      "mnemonic": "ADD",
      "since":    "ARMv4",
      "cycles":   { "form": "1S", "extra_when_dest_pc": "+1S+1N" },
      "steps": [
        { "op": "read_reg",  "index": "rn",     "out": "rn_val" },
        { "op": "add",       "in": ["rn_val", "op2_value"], "out": "result" },
        { "op": "write_reg", "index": "rd",     "value": "result" },
        { "op": "if", "cond": { "field": "s_bit", "eq": 1 }, "then": [
          { "op": "if", "cond": { "var": "rd", "eq": 15 }, "then": [
            { "op": "restore_cpsr_from_spsr" }
          ], "else": [
            { "op": "update_nz",      "value": "result" },
            { "op": "update_c_add",   "in": ["rn_val", "op2_value"] },
            { "op": "update_v_add",   "in": ["rn_val", "op2_value", "result"] }
          ]}
        ]}
      ],
      "writes_pc": "conditional_via_rd"
    }
  ]
}
```

### 9.1 `pattern`
Bit string, **fixed length = `width_bits`**, with underscores grouping bits purely for visual separation. Each character represents one bit:

| Char | Meaning |
|---|---|
| `0` / `1` | This bit must equal the value (counts toward mask/match) |
| Letter (`a`–`z`) | Field placeholder, named in `fields` |
| `x` | don't-care (excluded from mask) |

The parser derives `mask` and `match` from `pattern`:
- `mask`  = 1 for every `0`/`1` bit, 0 otherwise
- `match` = `0`/`1` substituted directly

It is enough to provide any one of `pattern`, `mask`, or `match` (recommended: provide all three so the parser can cross-check at load time and report mismatches).

### 9.2 `fields`
Field name → bit range. Range syntax `"hi:lo"` (inclusive) or single `"n"`.
The union of all field ranges plus mask bits must equal the full instruction width, otherwise lint reports an error.

### 9.3 `operands`
Describes the resolution steps for "compound operands". Common `kind` values:

| kind | Description | outputs |
|---|---|---|
| `register_direct`            | Read a field directly as a register index | `value` |
| `immediate_value`            | Use field bits directly as an immediate value | `value` |
| `immediate_rotated`          | ARM 8-bit imm + 4-bit rotate | `value`, `shifter_carry_out` |
| `shifted_register_by_immediate` | Rm + shift type + shift amount | `value`, `shifter_carry_out` |
| `shifted_register_by_register`  | Rm + shift type + Rs (low 8 bits) | `value`, `shifter_carry_out` |
| `pc_relative_offset`         | sign-extend imm + PC | `address` |
| `register_list`              | 16-bit register bitmap (LDM/STM) | `list` |

The outputs defined in `operands` can be referenced by name directly in `steps`.

### 9.4 `instructions[]`
Within the same format, dispatch on sub-opcode via `selector`.

| Field | Required | Description |
|---|---|---|
| `selector`   | ✓ | `{ "field": "opcode", "value": "0100" }` |
| `mnemonic`   | ✓ | Mnemonic |
| `since`      |   | First ISA version that has it |
| `until`      |   | ISA version where it becomes deprecated |
| `cycles`     | ✓ | Cycle cost |
| `steps`      | ✓ | Sequence of micro-ops |
| `unconditional` |   | If true, ignore global_condition |
| `writes_pc`  |   | `"never"` / `"always"` / `"conditional_via_rd"` |
| `quirks`     |   | Array of strings; flags special behavior (e.g. `"unaligned_rotates"`, `"smc_hazard"`) |
| `manual_ref` |   | Source citation like "ARM ARM §A4.1.6" |

---

## 10. Micro-op Step Format

Each step is a JSON object. The required field `op` names the operation; remaining fields depend on the op. Full vocabulary in `05-microops-vocabulary.md`.

### 10.1 Variable Namespace
"Values" referenceable by a step come from:

1. **Field values** (raw bits extracted from the format's `fields`) — referenced by field name; value is an unsigned integer
2. **Operand resolution outputs** (outputs defined in the `operands` section) — referenced by output name
3. **Previous step's `out`** — referenced by name from later steps
4. **Special built-in symbols**: `PC`, `CPSR`, `SPSR`, `MODE`, `CYCLE_COUNTER`

Naming-collision detection is reported by the parser at the lint stage.

### 10.2 Reference Syntax

| Syntax | Meaning |
|---|---|
| `"name"` | Reference a value (field / operand output / step out) |
| `{ "field": "rd", "eq": 15 }` | Conditional comparison expression (for `if`, `switch`) |
| `{ "var": "result", "lt": 0 }` | Same as above, referencing a step out variable |
| `{ "const": 42 }` | Immediate constant |
| `{ "const": "0xFF000000" }` | Hexadecimal constant |

### 10.3 Control-Flow Micro-ops

```json
{ "op": "if",
  "cond": { "field": "s_bit", "eq": 1 },
  "then": [ /* steps */ ],
  "else": [ /* steps, optional */ ] }

{ "op": "switch",
  "selector": "opcode",
  "cases": {
    "0000": [ /* steps */ ],
    "0001": [ /* steps */ ]
  },
  "default": [ /* steps */ ] }

{ "op": "block_terminate", "reason": "branch_taken", "next_pc": "target" }
```

`block_terminate` is a JIT-friendly core op: it tells the block compiler "block ends here, jump to next_pc"; the backend uses this to decide whether to inline or fall back to the host loop.

---

## 11. Cycle Cost (`cycles`)

ARM's classic cycle notation:
- **S** (Sequential)  — sequential memory access
- **N** (Non-sequential) — non-sequential access
- **I** (Internal)    — internal computation
- **C** (Coprocessor) — coprocessor access

```json
"cycles": {
  "form": "1S",
  "form_alt": ["1S", "1N", "1I"],
  "extra_when_dest_pc":   "+1S+1N",
  "extra_when_load_pc":   "+1S+1N",
  "computed_at":          "compile_time"
}
```

| Field | Meaning |
|---|---|
| `form`            | Primary cycle expression |
| `form_alt`        | Conditionally multiple forms (depending on operand type) |
| `extra_when_*`    | Conditional adders |
| `computed_at`     | `compile_time` (constant cycles) / `runtime` (depends on bus state, e.g. LDM) |

**Implementation note for v1**: initially, just sum the count of S/N/I in `form` for a coarse cycle value; wait states get corrected at runtime by the memory bus. Full cycle-accurate work is future scope.

---

## 12. Inheritance and Extension

### 12.1 ISA Inheritance

```json
{
  "architecture": {
    "id": "ARMv5TE",
    "family": "ARM",
    "extends": "ARMv4T"
  },
  "instruction_sets": [
    { "name": "ARM",
      "file": "arm-v5te-additions.json",
      "extends": { "spec": "ARMv4T", "set": "ARM" } }
  ]
}
```

`extends` means **this spec is an overlay on top of the base**:
- Shared register/mode/vector definitions don't need to be restated
- `encoding_groups` are unioned; same-named formats are treated as overrides
- An individual instruction can use the exact same `selector` + `since: "ARMv5TE"` to indicate it is new
- Tagging `until` indicates the instruction is invalid in newer ISAs

### 12.2 CPU Variant Feature Flags

Feature flags listed in `variants[].features` can act as conditions at the instruction level:

```json
{ "mnemonic": "BKPT", "requires_feature": "D" }
```

When the parser loads a particular variant, instructions that don't match the feature set are filtered out.

### 12.3 Custom Micro-ops

If a spec needs a special operation not in the base vocabulary (e.g. ARM Jazelle, 6502 BCD addition), it can declare:

```json
"custom_micro_ops": [
  { "name": "bcd_add",
    "inputs": ["a", "b", "carry_in"],
    "outputs": ["sum", "carry_out"],
    "implementation_hint": "model A+B as decimal nibbles" }
]
```

The C# side must register a corresponding emitter handler; once registered, the spec can immediately reference `{ "op": "bcd_add" }`.

---

## 13. Variable-Length Instructions (Thumb-2 Provisional Design)

Thumb-2 instruction length is determined by the first 5 bits:

```json
{
  "name": "Thumb2",
  "width_bits": "variable",
  "width_decision": {
    "first_unit_bits": 16,
    "rule": {
      "field": "[15:11]",
      "long_when_in": ["11101", "11110", "11111"],
      "long_total_bits": 32
    }
  }
}
```

The first version (Phase 1–6) implements only Thumb 1 (fixed 16); the schema reserves this field to ensure future extension does not require breaking changes.

---

## 14. Per-Instruction Cycle / State Side-Effect Annotations

Full emulation needs to know whether an instruction:

- Modifies PC? (`writes_pc`)
- Modifies memory? (`writes_memory: ["normal", "io_register"]`)
- Triggers a mode switch? (`changes_mode: true`)
- Switches instruction set? (`switches_instruction_set: true`)
- Requires an IO write barrier? (`requires_io_barrier: true`)

These are hints for the JIT backend: e.g. instructions with `requires_io_barrier: true` are marked as block terminators and force PPU/Timer sync after execution.

---

## 15. Validation Rules (Schema lint)

The JSON Schema validator and the parser's lint stage jointly perform the following checks:

| Check | Severity |
|---|---|
| pattern length == width_bits | error |
| field range exceeds width | error |
| field ranges overlap | error |
| pattern / mask / match are consistent | error |
| Within the same set, mask/match overlap (ambiguity) | warning (under mask_match_priority mode) |
| sub-opcode value length doesn't match selector field width | error |
| Step references an undefined variable | error |
| Duplicate step `out` names | error |
| Micro-op name not in vocabulary nor in custom_micro_ops | error |
| `cycles.form` string format invalid | warning |
| `extends` points to a non-existent base spec | error |

---

## 16. Full File Loading Flow

```
1. cpu.json
   ↓ resolve $schema, validate
   ↓ load architecture, register_file, modes, vectors
2. instruction_sets[].file → arm.json / thumb.json
   ↓ resolve $schema, validate
   ↓ if extends: pull base spec, merge encoding_groups
3. For each format:
   ↓ derive mask/match from pattern (or verify consistency)
   ↓ register operand resolvers
   ↓ register instructions (filter by variant features)
4. Build decoder structure (priority list / tree)
5. Emit pre-compiled micro-op handler templates (Phase 7+)
```

---

## 17. Future Extension Fields (reserved, not implemented in v1)

- `pipeline_model` — Fetch/Decode/Execute stage description (for cycle-accurate)
- `coprocessor_interface` — full CP14/CP15 register map
- `simd_lanes` — NEON / VFP register file
- `tlb_model` — MMU emulation
- `power_states` — WFI/WFE/sleep behavior
- `microarchitecture_hints` — branch predictor configuration, etc.

---

## 18. Comparison with Other Spec Languages

| Concept | AprCpu schema | Ghidra SLEIGH | QEMU decodetree | Sail |
|---|---|---|---|---|
| Encoding description | `pattern` string | `:opname mask & match` | `pattern` + `field` | `mapping` clause |
| Semantics | micro-op JSON steps | P-Code | inline C macros | Sail expressions |
| Registers | `register_file` JSON | `define register` | C globals | `register` declaration |
| Conditional execution | `global_condition` + `if` micro-op | macro wrappers | helper function | match clause |
| Inheritance | `extends` | `include` | C #include | `include` |
| Custom ops | `custom_micro_ops` | inline P-Code | write C directly | arbitrary expression |

Our distinguishing features: **fully structured JSON, controlled semantic micro-op vocabulary, explicit inheritance semantics, well-suited to the .NET toolchain and the LLVM backend**.

---

## Appendix A: ARM7TDMI Full Spec Size Estimate

| Item | Count |
|---|---|
| Processor modes | 7 |
| Exception vectors | 7 |
| ARM encoding formats | ~12 (Data Processing × 3, Multiply × 2, Load/Store × 3, Branch, PSR, SWI, Coprocessor) |
| ARM instructions (unique mnemonics) | ~40 |
| Thumb encoding formats | 19 (per official classification) |
| Thumb instructions (unique mnemonics) | ~30 |
| Shared micro-ops | ~50 |
| Estimated total spec lines | 1500–2500 lines of JSON |
