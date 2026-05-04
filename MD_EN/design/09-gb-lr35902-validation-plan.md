# Phase 4.5: GB LR35902 Port Validation Plan — Complete (2026-05-02)

> Use [erspicu/AprGBemu](https://github.com/erspicu/AprGBemu) as the
> reference implementation, write the Game Boy's Sharp LR35902 CPU as a
> JSON spec, run it through the same host runtime, and validate that the
> framework really does support "swap CPU = swap JSON".
>
> **Status**: 4.5A/B/C all complete. The LR35902 spec covers the full
> ISA (501 opcodes, 23 group files), runs Blargg `cpu_instrs` 11/11 +
> master "Passed all tests", and matches the LegacyCpu screenshot
> exactly. The framework points originally "expected to be extended"
> (register pairs, F register half-carry, CB prefix dispatch, variable
> width fetch) were all implemented on the SpecLoader / Emitters side.
>
> Subsequent emitter structure optimisation (Phase 5.8 refactor) is
> covered in `MD/design/11-emitter-library-refactor.md`. Spec structure
> is in `MD/design/10-lr35902-bit-pattern-groups.md`. This document is
> preserved as a historical plan record.

---

## Why pick GB / LR35902 as the second CPU

Not chosen at random — **deliberately a CPU that forces out
currently-unvalidated facets of the framework**.

| Framework capability that exists but has not been truly exercised | Used by LR35902 | Used by 6502 / Z80? |
|---|---|---|
| Real variable-width decoding (1/2/3 bytes) | Yes | 6502 yes, Z80 yes |
| Multi-instruction-set switch (prefix opcode entering another space) | Yes (0xCB prefix -> bit ops space) | 6502 no, Z80 yes (much more complex) |
| 8-bit GPR width (`GprWidthBits = 8` path) | Yes (A/B/C/D/E/H/L are all 8-bit) | 6502 yes, Z80 yes |
| Aliased / paired registers (two 8-bit viewed as one 16-bit) | Yes (BC/DE/HL/AF) | 6502 no, Z80 yes |
| Status flag layout in a non-LSB position (high nibble) | Yes (Z/N/H/C in F bits 7,6,5,4) | 6502 partially, Z80 yes |
| Custom arithmetic flag (half-carry) | Yes (H flag = bit 3 -> 4 carry) | 6502 no, Z80 yes |

LR35902 hits all six items; 6502 is too simple (no prefix opcode, no
paired register), with insufficient coverage. Z80 would also work, but
the GB emulator (i.e. LR35902 = Z80 minus IX/IY/shadow registers + plus
LDH/SWAP) is moderately sized and has a ready reference implementation
(AprGBemu).

**Bonus**: AprGBemu is a "running" implementation; after porting we can
diff behaviour instruction-by-instruction, much more convenient than
reading hand-written documentation.

---

## Phase positioning

**Insertion timing**: after Phase 4 ends, before Phase 5.

| Phase | State after completion |
|---|---|
| 3 | Host runtime + JIT binding OK, can run a single IR function |
| 4 | armwrestler ARM mode all green — proves ARM side correct |
| **4.5** | **GB CPU spec written + reference comparison runs through** — proves framework can swap CPUs |
| 5 | Run GBA BIOS + ROM entry point |

**Why not earlier**: need host runtime first (Phase 3) before anything
can run; need ARM running correctly first (Phase 4) before we can be
sure that "doesn't run" is a GB spec problem rather than a framework
problem.

**Why not later**: discovering at Phase 7 (LLVM Block JIT) that the
framework doesn't work for GB would be too expensive to redo. Validating
generality before Phase 5 (GBA-specific work) lets us influence Phase
5+ design decisions (e.g. whether the memory bus interface should keep
multi-CPU shape).

---

## Scope and acceptance

> **Note**: the full scope and "the firmness of acceptance" (whole ISA
> vs Blargg subset vs only boot screen) is decided when we actually
> reach Phase 4.5. The below is a suggested starting point, not a
> commitment.

### Suggested minimum deliverable (MVP-of-Phase-4.5)

- [ ] `spec/lr35902/cpu.json` + `spec/lr35902/instructions/*.json`
  covering the LR35902 main instruction set (~245 main opcodes)
- [ ] CB-prefix sub-instruction set (256 bit ops)
- [ ] Host runtime hooked to GB memory map (ROM bank 0/1, VRAM, WRAM,
  OAM, IO, HRAM, IE) — **stub for now**, just enough to run the CPU
- [ ] Pull AprGBemu's ROM loading flow as reference
- [ ] **Behavioural comparison test**: run the same GB ROM, dump
  CpuState every N cycles, diff side-by-side with AprGBemu
  - Suggested starting point: Blargg
    `cpu_instrs/individual/01-special.gb`
  - After passing, push to 02 ~ 11

### Optional expanded scope (depends on time)

- [ ] All 11 Blargg cpu_instrs sub-tests passing
- [ ] PPU stub (DMG mode, tile-based, no sprite priority) so the boot
  screen displays
- [ ] Some Mooneye-gb test ROMs passing

---

## Where the framework is expected to be extended

Writing LR35902 in is expected to surface points where the framework
falls short and needs extending. Each such item is **a real test of the
framework's generality**.

### 1. Schema: register aliases / pairs

The GB spec will inevitably write something like:

```json
{
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
    ]
  }
}
```

`register_file` currently has no `register_pairs`; needs schema
extension + Loader read + new micro-ops `read_reg_pair` /
`write_reg_pair` (or operand resolver `paired_register`).

### 2. Schema: F register's special fields

```json
{
  "name": "F",
  "width_bits": 8,
  "fields": {
    "Z": { "high": 7, "low": 7 },
    "N": { "high": 6, "low": 6 },
    "H": { "high": 5, "low": 5 },
    "C": { "high": 4, "low": 4 }
  },
  "low_nibble_zero": true
}
```

Invariants like `low_nibble_zero` do not currently exist; options:
- Add a schema field + the emitter automatically masks on each
  `write_psr`
- Don't add it, the spec masks on each write itself (more tedious but
  doesn't extend the framework)

### 3. Half-carry flag computation

ARM's C/V are already implemented; GB's H flag (bit 3 -> bit 4 carry) is
new. New micro-ops:

- `update_h_add` — H = ((a & 0xF) + (b & 0xF)) > 0xF
- `update_h_sub` — H = (a & 0xF) < (b & 0xF)
- `update_h_add16` — 16-bit version (bit 11 -> bit 12 carry, for ADD HL,rr)

Can go in the `LR35902Emitters` class (mirroring `ArmEmitters`), without
polluting the generic parts.

### 4. Multi-instruction-set prefix dispatch

ARM/Thumb switching uses persistent state in the `CPSR.T` bit. GB 0xCB
is a momentary "the next byte belongs to another instruction set" switch.

`InstructionSetDispatch` already supports `selector` +
`selector_values`, but a prefix-byte mode may need a new
`transition_rule`:

```json
{
  "instruction_set_dispatch": {
    "selector": "fetched_byte_0xCB",
    "transition_rule": "single_instruction_in_alt_set",
    "alt_set": "CB_PREFIX"
  }
}
```

Or more simply: treat 0xCB-prefix as "the instruction at opcode 0xCB in
the main instruction set goes off and does another fetch + decode",
expressed using existing micro-ops. Which is cleaner is decided at
implementation time.

### 5. Variable-width truly exercised

ARM/Thumb is "fixed-width within the entire instruction set"; GB is "the
opcode within the same set decides 1/2/3 bytes". The `width_decision`
schema is prepared but only exercised in the ARM/Thumb "switching to
another set" usage; never exercised in the "same set, total length
decided by opcode" usage. The decoder may need fixes during
implementation.

---

## Port workflow

Recommended order, commit after each block:

1. **Build spec skeleton**: `spec/lr35902/cpu.json` writes architecture,
   register_file (including paired register schema extension),
   processor_modes (GB has no mode concept, this field can be omitted)
2. **8-bit Load group** (starting at AprGBemu CPU.cs line 22's
   `#region 8bit Load`) — simplest, ~50 opcodes, validate that the
   8-bit GPR path can be emitted
3. **16-bit Load group** — validate paired register writing
4. **8-bit ALU** (ADD/ADC/SUB/SBC/AND/OR/XOR/CP/INC/DEC) — validate
   H flag
5. **16-bit ALU** (ADD HL,rr / INC rr / DEC rr) — validate 16-bit
   add/H flag
6. **Jump/Call/Return** (JP/JR/CALL/RET including conditional variants)
7. **Misc** (CCF/SCF/CPL/DAA/STOP/HALT/DI/EI)
8. **0xCB prefix bit ops** (256 entries, but all are BIT/RES/SET/RLC/
   RRC/RL/RR/SLA/SRA/SWAP/SRL x 8 registers, large-scale format reuse)
9. **Host runtime adaptation**: memory bus stub, register file mirror,
   run a NOP-loop through
10. **Behavioural comparison test**: run Blargg 01-special.gb, dump and
    diff

---

## Risks and decision points

| Risk | Mitigation |
|---|---|
| Schema extension breaks existing ARM tests | Phase 2.5 already has 159 tests + CoverageTests; schema changes only merge when all green |
| AprGBemu's behaviour itself is incorrect | Use the Blargg test ROM as the start point rather than direct AprGBemu side-by-side; AprGBemu is only a "go look at the C# when stuck on writing spec" reference book |
| Paired register schema design gets stuck | Fallback: spec only declares 8-bit GPR, paired is reconstructed in the emitter via two read_reg + or + shl. Less clean but workable |
| GB host runtime turns out to be heavier than expected | This phase does not require a PPU; CPU + memory stub is enough; the ROM is only used to validate CPU behaviour |
| 0xCB prefix dispatch design gets stuck | Fallback: treat the byte after 0xCB as a 256-entry sub-format within the main instruction set, do not introduce the multi-set mechanism |

---

## Correspondence with AprGBemu

| AprGBemu file | Counterpart in this project |
|---|---|
| `Emu_GB/CPU.cs` (switch-case CPU) | `spec/lr35902/*.json` (data-driven) |
| `Emu_GB/MEM.cs` (memory bus) | host runtime memory bus implementation (C#) |
| `Emu_GB/Define.cs` (register declarations) | spec's `register_file` |
| `Emu_GB/INT.cs` (interrupts) | host runtime interrupt loop |
| `Emu_GB/GPU.cs` / `SOUND.cs` / `JOYPAD.cs` | not in scope for this phase |

AprGBemu is **WinForms + pure interpreter**; this project's goal is to
re-express its CPU portion as JSON and run it via LLVM IR + JIT. The
control's value lies in: running the same ROM on both should produce
the same CpuState sequence.

---

## Done criteria

When all of the following hold, declare Phase 4.5 complete:

1. Done: `spec/lr35902/` covers main + CB-prefix full ISA
2. Done: framework-side schema/parser/emitter extensions are merged and
   pass all ARM tests
3. Done: host runtime can load a GB ROM and run fetch-decode-execute
   without crashing
4. Done: at least one Blargg cpu_instrs sub-test passes (the actual
   number is decided when we reach there)
5. Done: Coverage tests also pass for the LR35902 spec (vocabulary
   lint, mnemonic baseline)
6. Done: docs updated — this document records the actual extension
   points encountered, roadmap marks Phase 4.5 complete

---

## Hand-off to following phases

After completion:
- Phase 5 (GBA Memory Bus + BIOS) returns to the ARM mainline
- The framework is from then on validated as **truly general**, and all
  later design decisions should keep "no pollution by ARM-specific
  assumptions"
- If this goes well, adding a third CPU later (MIPS R3000, RISC-V RV32I,
  etc.) becomes much easier

---

## 4.5C: spec structure design

After `LegacyCpu` interpreter (256-case big switch) finishes and passes
Blargg `cpu_instrs` 11/11, the same ISA is to be written into
`spec/lr35902/` using bit-pattern grouping, producing a `JsonCpu`
backend that runs the same ROM in comparison with legacy. The specific
grouping table is in
[`10-lr35902-bit-pattern-groups.md`](./10-lr35902-bit-pattern-groups.md).
