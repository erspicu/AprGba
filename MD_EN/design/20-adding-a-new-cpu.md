# Adding a new CPU to the framework — contributor guide

> **Status (2026-05-09)**: SOP doc written after N2 wrap-up. After two
> framework-refactor rounds — N1.B' (alloca+mem2reg) and N2 (declarative
> JIT policy + CPU/Machine spec split) — the flow for adding a new CPU
> simplifies to "write JSON specs + add arch-specific micro-ops to the
> emitter file; the framework automatically handles both per-instr and
> block-JIT modes".
>
> The 3 CPUs already implemented can be referenced as examples:
> - **ARM7TDMI** (GBA) — `spec/arm7tdmi/cpu.json` + `src/AprCpu.Core/IR/ArmEmitters.cs`
> - **LR35902** (GB DMG) — `spec/lr35902/cpu.json` + `src/AprCpu.Core/IR/Lr35902Emitters.cs`
> - **Ricoh 2A03** (NES NTSC) — `spec/2a03/cpu.json` + `src/AprCpu.Core/IR/Mos6502Emitters.cs`
>
> When adding a 4th, follow this doc step-by-step, one commit per step,
> with tests passing before moving on.

---

## 1. Pre-flight — confirm the requirements

Answering these 4 questions decides the scope. Most CPUs should resemble
one of 6502 / GB / ARM.

1. **Word size** — 8-bit / 16-bit / 32-bit / 64-bit?
2. **Instruction encoding** — fixed width (ARM/Thumb) vs variable
   (LR35902/6502/x86)?
3. **Register file** — how many GPRs / banked or not / how status registers
   are arranged?
4. **Memory model** — endianness / alignment / any mode-banked memory
   (e.g. ARM monitor mode)?

---

## 2. Step-by-step

### Step 1 — `spec/<arch>/cpu.json` (ISA semantics)

Express the ISA abstraction in declarative JSON. The schema is at
`spec/schema/cpu-spec.schema.json`.

Minimum that works:

```json
{
    "$schema": "../schema/cpu-spec.schema.json",
    "spec_version": "1.0",
    "architecture": {
        "id":             "<unique-id>",
        "family":         "<family-id>",
        "endianness":     "little",
        "word_size_bits": 8
    },
    "variants": [{ "id": "<chip-id>", "core": "<arch-id>" }],
    "isa_metadata": {
        "endianness":             "little",
        "cycles_per_spec_unit":   1,
        "pc_update_policy":       "lazy",
        "interrupt_check_policy": "end_of_block"
    },
    "register_file": { ... },
    "exception_vectors": [...],
    "instruction_sets": [{ "name": "Main", "include": "main.json" }]
}
```

`isa_metadata.cycles_per_spec_unit` matters:
- **GB / ARM convention**: `cycles.form: "3m"` is interpreted as 3 m-cycles
  = 12 t-cycles → set 4
- **6502 convention**: `cycles.form: "3m"` is interpreted as 3 raw cycles
  → set 1
- If unspecified the framework defaults to 4

### Step 2 — `spec/<arch>/groups/*.json` + `<arch>/main.json` (instruction definitions)

Each instruction is described by encoding + steps. Steps are a sequence
of micro-ops, mapped by the framework emitters into LLVM IR.

Minimum example (single NOP):

```json
{
    "name": "Nop",
    "formats": [{
        "name": "Nop",
        "pattern": "11101010",
        "fields": {},
        "mask": "0xFF",
        "match": "0xEA",
        "instructions": [{
            "mnemonic": "NOP",
            "since": "<arch-id>",
            "cycles": { "form": "2m" },
            "steps": []
        }]
    }]
}
```

For "recurring ALU patterns", **first compose using the generic emitters**
(add/sub/and/or/xor/shl/lsr/asr/branch_cc/load_byte/store_byte/sext/trunc/
update_zero/update_sign/set_flag/...) — all in `StandardEmitters.RegisterAll`.

Only when generic isn't enough should you write an arch-specific emitter
(see step 4).

### Step 3 — `spec/machines/<machine>.json` (board-level memory map)

The CPU spec should not know the memory map; the board spec describes it.
The schema is at `spec/schema/machine-spec.schema.json`.

```json
{
    "$schema": "../schema/machine-spec.schema.json",
    "name": "<machine-id>",
    "cpu":  "<cpu-arch-id>",
    "memory_regions": [
        { "name": "ram",  "addr_start": "0x0000", "addr_end_exclusive": "0x2000",
          "type": "ram", "fastmem_eligible": true, "smc_notify": true },
        { "name": "rom",  "addr_start": "0x8000", "addr_end_exclusive": "0x10000",
          "type": "rom", "fastmem_eligible": true }
    ],
    "interrupt_vectors": { "reset": "0xFFFC" }
}
```

Three region `type` values:
- `ram` — general read/write; `smc_notify` default-on (modifiable by
  self-modifying code)
- `rom` — read-only; `writable` default-off
- `io` — side-effect region (PPU/APU/mapper/...); `forces_end_of_block`
  default-on (any instruction that writes to an io region ends the block —
  because IO writes may change the memory map / trigger interrupts / etc.)

### Step 4 — `<Arch>Emitters.cs` (arch-specific micro-op emitters)

New file at `src/AprCpu.Core/IR/<Arch>Emitters.cs`. Mirror the structure of
the existing files (look at `Mos6502Emitters.cs`, ~2000 lines;
`Lr35902Emitters.cs` similar).

Only write what generic can't handle:
- Addressing-mode dispatchers (e.g. the 8-way switch for the 6502 cc bbb
  pattern)
- Arch-specific flag combos (6502 ADC writing NVZC at once, ARM CPSR mode)
- Stack-bound control flow (JSR / RTS / RTI / BRK on 6502; CALL / RET /
  IRQ on GB)
- Asymmetric push/pop (6502 page-1 stack vs GB pre-decrement vs ARM
  stack-of-anything)

**Key points for writing emitters**:

1. Use `ctx.GepStatusRegister(name)` / `ctx.GepGpr(idx)` to obtain state
   pointers; **don't use `ctx.StatePtr` directly** — the framework
   redirects pointers based on mode to either an alloca (block-JIT) or
   the state buffer (per-instr); the emitter should not be aware of the
   difference.
2. PC writes must set `Layout.GepPcWritten = 1` so BlockFunctionBuilder
   knows to exit the block.
3. Conditional branches go through `branch_cc`, or compose `BuildSelect`
   + a PC store yourself.
4. Compile-time-known immediates (block-JIT) branch on
   `ctx.CurrentInstructionBaseAddress is not null`, extracting the imm
   from `ctx.Instruction` via shift+trunc to skip the bus extern call.
   Per-instr fallback uses `bus.ReadByte` walking the PC.
   Example: `Mos6502Emitters.FetchImm8` (lines ~150).

### Step 5 — Decoder dispatch + arch family branch

In the family switch in `src/AprCpu.Core/Compilation/SpecCompiler.cs`, add
the new arch:

```csharp
else if (string.Equals(family, "<your-arch-family>", StringComparison.OrdinalIgnoreCase))
{
    YourArchEmitters.RegisterAll(registry);
}
```

### Step 6 — `Apr<Cpu>.Cli/Cpu/<CpuName>JsonCpu.cs` (host class)

Mirror `NesJsonCpu.cs` (~390 lines) structure:

- ctor: load spec, build HostRuntime, bind memory externs, allocate state buffer
- StepOne: fetch opcode, decode, dispatch fn pointer
- StepBlock: BlockDetector + BlockCache + cycles_left budget tracking
- NMI / IRQ handler in C# (6502 / GB pattern)

When block-JIT is enabled:
```csharp
var bfb = new BlockFunctionBuilder(...) {
    CyclesPerSpecUnit = _spec.Cpu.IsaMetadata?.CyclesPerSpecUnit ?? 4
};
```

### Step 7 — pass a test ROM

You need at least one small instruction-test ROM passing:
- 6502 family: nestest / blargg cpu_test5
- ARM: armwrestler
- GB: blargg cpu_instrs

Verify **all three backends agree** — legacy interpreter / json per-instr
/ json-block. Run `--diff` and `--diff-block` (NES) / the equivalent
lockstep harness to catch divergences.

---

## 3. Anti-patterns — don't do these

### Combining the CPU and the board into a single spec

cpu.json **must not** know any board-level concept (memory map, mapper,
IO addresses). Including those = locking the ISA into a single board,
making it impossible to reuse later.

### Writing mode-aware code in emitters

```csharp
// Don't do this
if (perInstrMode) { /* path A */ } else { /* path B */ }
```

The framework already handles mode-routing through abstractions like
`EmitContext.GepStatusRegister`. Write the emitter once, and it
automatically works in both per-instr and block-JIT modes.

The one exception: block-JIT compile-time imm extraction (checking
`ctx.CurrentInstructionBaseAddress`) — that is an **orthogonal optimization**,
not duplicated mode-handling work.

### Putting optimization logic into spec.json

The spec only describes "facts" (this region is RAM, that instr writes
PC, etc.). **How to optimize** is the responsibility of emitter / framework
C#. Otherwise JSON turns into a mini-language (Greenspun's tenth rule).

### Hardcoding bus dispatch paths

NesMemoryBus / GbMemoryBus's ReadByte/WriteByte switch chains are still
hardcoded today — that's because changing the hot path is risky and
needs perf-bench gating. **A new CPU** should drive from the MachineSpec
memory_regions table from day one (O(N) lookup or sorted binary search),
even though the existing 3 CPUs have not yet migrated to that shape.

If you want to do this work as part of adding your CPU, the data is
already there in `MachineSpec.MemoryRegions`; just write a
`MachineSpec.LocateRegion(uint addr)` helper that finds the matching
region in the table.

---

## 4. Reference — building blocks the existing framework provides

### Shared micro-op emitters (StandardEmitters.RegisterAll)

`read_reg` / `write_reg` / `read_reg_named` / `write_reg_named` /
`add` / `sub` / `and` / `or` / `xor` / `shl` / `lsr` / `asr` / `ror` /
`bic` / `mvn` / `mul` / `umul64` / `smul64` / `add_i64` /
`load_byte` / `store_byte` / `read_imm8` / `read_imm16` /
`read_pc` / `sext` / `trunc` /
`branch` / `branch_link` / `branch_cc` / `if` / `select` /
`push_pair` / `pop_pair` / `push8` / `pop8` / `call` / `ret` /
`call_cc` / `ret_cc` /
`set_flag` / `toggle_flag` / `update_zero` / `update_sign` /
`update_h_add` / `update_h_sub` / `update_h_inc` / `update_h_dec` /
`bit_test` / `bit_set` / `bit_clear` / `shift` / `sync` / `defer`

### Block-JIT pipeline

`BlockDetector` walks PC until a boundary and returns a `Block`;
`BlockCache` is an LRU cache of compiled results; `BlockFunctionBuilder`
emits a single LLVM function covering the entire block; `HostRuntime`
wraps ORC LLJIT. See doc #18 (state abstraction) + doc #19 (declarative
policy) for details.

### State access abstraction

Emitters call `ctx.GepGpr(idx)` / `ctx.GepStatusRegister(name)` to obtain
pointers for load/store. In block-JIT mode the framework automatically
redirects to allocas and runs the mem2reg pass, producing IR equivalent
to hand-written SSA.

### Doc cross-reference

- [#15](15-timing-and-framework-design.md) — shared timing model / framework generalisation
- [#16](16-emulator-completeness.md) — completeness inventory of each emulator
- [#17](17-aprcpu-vs-emulator-timing-boundary.md) — framework / emulator boundary
- [#18](18-block-jit-state-abstraction.md) — alloca+mem2reg details
- [#19](19-declarative-jit-policy.md) — design rationale for this N2 series

---

## 5. Estimated effort (against the 3 CPUs already implemented)

| Phase | 8-bit RISC-like (6502/Z80) | 32-bit RISC (ARM/MIPS) | Variable-width CISC (x86/68k) |
|---|---|---|---|
| Step 1-3 (writing specs) | 1-2 days | 2-3 days | 3-5 days |
| Step 4 (emitter) | 3-7 days | 1-2 weeks | 2-4 weeks |
| Step 5-7 (host class + test ROM) | 1-2 days | 2-4 days | 3-5 days |

Actual time depends on:
- ISA complexity (6502 ~150 official + 105 unofficial = medium; ARM
  ARMv4T = medium-high; m68k = high; x86 = very high)
- Whether there's a reference implementation to port (OldProject /
  open-source emulator)
- Test ROM completeness (blargg-style + cycle-accurate? trace?)

---

## 6. If you get stuck

- Spec compile failure → `dotnet test --filter SpecCompilerTests`, look
  at the Diagnostics
- Block-JIT bug → use `--diff-block` lockstep to find the divergence
- Wrong IR → `HostRuntime.PrintModuleIR()` dump, eyeball compare with
  the corresponding shape in `Lr35902Emitters`
- Per-instr / block-JIT divergence → 99% of the time the emitter is
  violating "mode-agnostic state access" (reading the state buffer
  directly rather than the alloca)
- Some piece of the framework is unclear → grep doc #18 / #19, it's
  usually written there
- Gemini consultation — `tools/knowledgebase/gemini_query.py` can answer
  LLVM-detail questions (CLAUDE.md has rules)

You're welcome to add new CPUs and file framework issues. A new CPU
exposing framework gaps is a great opportunity for the framework to
evolve.
