# Spec Authoring Conventions

When writing a new instruction format / instruction, follow these
conventions to avoid spelling drift, field name conflicts, and
inconsistencies with emitter behaviour during later maintenance. This
document is not schema-enforced (the schema lives at
`spec/schema/cpu-spec.schema.json`) — it is "human convention". Lint
checks the enforced items; everything else falls under code review.

---

## 1. File organisation

| Path | Content |
|---|---|
| `spec/<arch_id>/cpu.json`              | CPU model top level (one file) |
| `spec/<arch_id>/<set_name>.json`       | One instruction set (e.g. `arm.json`, `thumb.json`) |
| `spec/<arch_id>/groups/*.json`         | (Optional) encoding-group split files, included via `$include` |
| `spec/<arch_id>/formats/*.json`        | (Optional) finer-grained format / instruction split |
| `spec/schema/cpu-spec.schema.json`     | JSON Schema validator |
| `MD/design/0X-...md`                   | Design documents |

`<arch_id>` uses lowercase with version numbers stripped (`arm7tdmi`,
`mos6502`). `<set_name>` matches the JSON `name` field exactly (`ARM`,
`Thumb`).

### 1.1 When to split

When a single instruction-set file exceeds ~500 lines or contains
multiple conceptually distinct encoding groups, splitting is
recommended. Each split file usually represents one encoding group or
one format family.

### 1.2 `$include` mechanism

Any element inside an array can be a `{ "$include": "<relative-path>" }`
directive:

```json
"encoding_groups": [
  { "$include": "groups/branch-exchange.json" },
  { "$include": "groups/data-processing.json" },
  { "name": "InlineGroup", "formats": [ ... ] }
]
```

Rules:
- **Path is relative to the file containing the directive** (not to cwd
  or repo root)
- **When the included file's root is an array**: splice into the parent
  array (multiple elements imported at once)
- **When the root is an object**: replace the directive object position
  (one-to-one)
- **Recursive resolution**: an included file can itself contain more
  `$include`
- **Cycle detection**: A -> B -> A is rejected (chain-based, multiple
  references to the same file are OK)
- **Schema validation happens after resolution**: split fragments do not
  need to pass the schema individually; only the merged whole is
  validated

### 1.3 Splitting conventions

- Use `kebab-case` for split file names (`data-processing.json` rather
  than `DataProcessing.json`)
- Each group file's root is the complete object for that group: `{ "name": "...", "formats": [...] }`
- Format-level splits (finer): root is usually a single format object or
  an array of formats
- Split files do not need `$schema` / `spec_version` — those only make
  sense at the top level

---

## 2. Bit-pattern letter conventions

Patterns are hand-written and most prone to drift. **Unifying letter
conventions** makes bits with the same semantics look the same across
formats:

| Letter | Use | Example |
|---|---|---|
| `c`  | Condition code (4 bits)         | `cccc_...` |
| `o`  | Opcode / sub-opcode             | `cccc_001_oooo_...` |
| `d`  | Destination register (Rd)       | `..._dddd_...` |
| `n`  | First source register (Rn)      | `..._nnnn_...` |
| `m`  | Second source register (Rm)     | `..._mmmm` |
| `s`  | S-bit (set flags) **or** Rs (shift register) — depending on context | `..._s_..._ssss_..._mmmm` |
| `i`  | Immediate value bits            | `..._iiiiiiii` |
| `r`  | Rotate count (ARM imm8 rotate)  | `..._rrrr_iiiiiiii` |
| `t`  | Shift type (LSL/LSR/ASR/ROR)    | `..._tt_..._mmmm` |
| `h`  | RdHi / Rs_high                  | (Multiply Long) |
| `l`  | RdLo / Rs_low                   | (Multiply Long) |
| `L`  | Link bit (B vs BL)              | `cccc_101_L_...` |
| `B`  | Byte/word bit (LDR vs LDRB)     | (SDT) |
| `P`  | Pre/post indexing or PSR select | (SDT, PSR Transfer) |
| `U`  | Up/down (add/sub offset)        | (SDT) |
| `W`  | Writeback bit                   | (SDT) |
| `A`  | Accumulate bit (MUL vs MLA)     | (Multiply) |
| `S`  | Signed bit (Multiply Long)      | (distinguishes UMULL/SMULL) |
| `x`  | Don't-care bit                  | reserved sections |

**Conflict resolution**: if `s` plays both the S-bit and Rs roles in the
same format, name them explicitly in `fields` (`"s_bit": "20"`,
`"rs": "11:8"`) and have the pattern letters map to the same letter
(different positions distinguish meaning). A single letter cannot repeat
in non-contiguous positions in a pattern (this is enforced by lint).

---

## 3. Format naming convention

`{Group}_{Variant}` form:

- `Group` = ARM encoding space or Thumb format number
  - `DataProcessing`, `PSR`, `Multiply`, `MultiplyLong`, `SingleDataTransfer`,
    `HalfwordSignedDataTransfer`, `SingleDataSwap`, `BlockDataTransfer`,
    `Branch`, `BranchExchange`, `SoftwareInterrupt`, `Coprocessor`,
    `Undefined`
  - Thumb: `Thumb_F1` ~ `Thumb_F19`
- `Variant` = secondary tag distinguishing encoding differences
  - `Immediate`, `RegImmShift`, `RegRegShift`
  - `MRS`, `MSR_Imm`, `MSR_Reg`
  - `B` (unconditional), `BL` (with link)

Examples:
- `DataProcessing_Immediate`
- `DataProcessing_RegImmShift`
- `SingleDataTransfer_Immediate`
- `BlockDataTransfer`
- `Thumb_F4_AluOps`
- `Thumb_F5_HiRegOps`

---

## 4. JSON field naming

- All `lower_snake_case`
- Common field names are fixed: `cond`, `opcode`, `s_bit`, `rd`, `rn`,
  `rm`, `rs`, `imm8`, `imm12`, `imm24`, `offset11`, `offset24`,
  `rotate`, `shift_type`, `shift_amount`, `reg_list`, `p_bit`, `u_bit`,
  `w_bit`, `b_bit`, `l_bit`, `a_bit`, `signed_bit`
- Immediate value fields are named with bit width: `imm8` (8-bit),
  `imm12` (12-bit), etc.
- The `_bit` suffix is only used for single-bit flag fields

---

## 5. Selector

Each entry in `instructions[]` (unless the format has only one
instruction) must have a `selector`, in the form:

```json
{ "selector": { "field": "opcode", "value": "0100" } }
```

- `field` = a field name already declared in the format's `fields`
- `value` = a binary string (recommended, more readable) or decimal
  integer
- Binary string length **must** equal the selector field width (lint
  enforces)

---

## 6. Common idioms in step writing

### 6.1 Conditional execution
Do not write cond checks inside an instruction's steps; the
instruction-set-level `global_condition` is automatically wrapped as a
conditional gate by the emitter. Exception: instructions marked
`unconditional: true` (e.g. Thumb F18 B, ARM v5+ BLX(label)).

### 6.2 Comparison instructions (no writeback)

CMP / CMN / TST / TEQ:
```json
{ "writes_pc": "never",
  "steps": [
    { "op": "read_reg", "index": "rn", "out": "rn_val" },
    { "op": "sub", "in": ["rn_val", "op2_value"], "out": "result" },
    { "op": "update_nz",    "value": "result" },
    { "op": "update_c_sub", "in": ["rn_val", "op2_value"] },
    { "op": "update_v_sub", "in": ["rn_val", "op2_value", "result"] }
  ]
}
```

Mapping:
- CMP = sub + update flags (no Rd write)
- CMN = add + update flags (no Rd write)
- TST = and + update_nz + update_c_shifter
- TEQ = xor + update_nz + update_c_shifter

### 6.3 ALU writeback + conditional flag update

```json
"steps": [
  { "op": "read_reg", "index": "rn", "out": "rn_val" },
  { "op": "<op>", "in": ["rn_val", "op2_value"], "out": "result" },
  { "op": "write_reg", "index": "rd", "value": "result" },
  { "op": "if", "cond": { "field": "s_bit", "eq": 1 }, "then": [
    { "op": "if", "cond": { "field": "rd", "eq": 15 }, "then": [
      { "op": "restore_cpsr_from_spsr" }
    ], "else": [
      { "op": "update_nz", "value": "result" },
      { "op": "update_<c_kind>", "in": [...] },
      { "op": "update_<v_kind>", "in": [...] }
    ] }
  ] }
]
```

"rd == 15 -> restore CPSR from SPSR" is the standard ARM ALU logic when
writing PC with S=1; every ALU instruction supporting that behaviour
should follow this idiom.

### 6.4 Memory access
Use `load` / `store` micro-ops uniformly for read/write instructions; do
not assemble GEP yourself. Alignment and byte rotate are handled inside
the micro-op (the emitter knows the memory_model setting).

### 6.5 Branch variants

| Instruction | Use |
|---|---|
| `B`             | `branch` |
| `BL`            | `branch_link` (writes LR automatically) |
| `BX`            | `branch_indirect` (handles T-bit + alignment automatically) |

---

## 7. Cycles markup

Most common forms:
```json
"cycles": { "form": "1S" }
"cycles": { "form": "2S+1N" }
"cycles": { "form": "1S",       "extra_when_dest_pc": "+1S+1N" }
"cycles": { "form": "1S+1N+1I", "extra_when_load_pc": "+1S+1N" }
"cycles": { "form": "(n)S+1N+1I", "computed_at": "runtime" }
```

The letters `S/N/I/C` are standard ARM cycle notation; `+` sums them.
`computed_at: "runtime"` means the cycle count cannot be settled at
compile time (e.g. LDM depends on the number of bits in the register
list).

---

## 8. Quirks

`quirks` is a string array marking hardware oddities that need special
handling by the emitter. Already in use:
- `unaligned_rotates` — rotated read for unaligned LDR
- `bl_hi_half` / `bl_lo_half` — the two halves of Thumb F19 BL
- `pc_in_writeback` — special case where Rn=PC during SDT writeback
- `smc_hazard` — a write may trigger SMC; block compiler should be
  cautious

When adding a new quirk, document it here first before using it.

---

## 9. `manual_ref`

Form: `"ARM ARM A4.1.3"` or `"GBATEK CPU.Instructions.ARM"`. Lets people
look up the original spec section. Main references for ARMv4T:
- *ARM Architecture Reference Manual* (DDI 0100, or the ARMv5 manual
  with v4T chapters)
- *ARM7TDMI Data Sheet*
- *GBATEK* (the complete GBA spec maintained by Martin Korth)

---

## 10. `$comment` and `comment`

- The JSON standard has no native comments, so we use two:
  - Key-value `"comment": "..."`: attached to format/instruction, for
    description
  - Key `"$comment_<topic>"`: attached at object level, tools ignore
    keys with this prefix
- The schema declares `additionalProperties: false`, so `$comment_*`
  keys are still rejected by the schema; using `comment` is safer (the
  schema permits it)

---

## 11. Instruction ordering convention

Order of `instructions[]` within a format:
1. Primary: alphabetical mnemonic order
2. Exception: when the selector value has a natural order (e.g. ARM
   Data Processing's 0000-1111), order by ascending selector value

Between formats (within the same group): by priority — higher
specificity (more 1s in mask) listed first.

---

## 12. Lint enforced items (rejected by SpecLoader)

- pattern length = `width_bits`
- pattern letter runs correspond exactly to BitRanges declared in
  `fields[]`
- mask/match derived from pattern matches declared mask/match
- field BitRange does not exceed `[0, width_bits-1]`
- field BitRanges do not overlap each other
- selector value's bit-string length equals selector field width (for
  integer form, value does not exceed `2^width - 1`)

---

## 13. Lint warning items (printed to stdout but not blocking)

- format has >1 instruction but no selector
- pattern contains `x` (don't-care) but adjacent field ranges do not
  cover that position (possibly an oversight)
- micro-op name is not in the standard vocabulary nor in
  `custom_micro_ops` (the emitter phase will hard error, but lint warns
  first)

---

## 14. Impact check on changes

After adding/modifying spec, the following must be run:

```
dotnet test                     # 35+ tests, includes coverage matrix
dotnet run --project src/AprCpu.Compiler -- \
    --spec spec/arm7tdmi/cpu.json --output temp/arm7tdmi.ll
```

The CLI must produce 0 diagnostics; tests must all be green.
