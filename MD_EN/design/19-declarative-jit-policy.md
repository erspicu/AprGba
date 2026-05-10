# Declarative JIT optimization policy — CPU spec / Machine spec split

> **Status (2026-05-09)**: design phase. Prerequisite: the N1.B'
> alloca+mem2reg refactor has shipped (doc #18); emitters are now agnostic
> to per-instr / block-JIT modes. This doc proposes the next layer of
> framework abstraction: move *block-JIT optimization policy* out of
> "per-CPU C# hardcode" and into a **declarative spec**, and introduce a
> two-level separation between the **CPU spec** and the **Machine spec**.
>
> Design rationale:
> 1. Internal design discussion (user-driven, 2026-05-09)
> 2. Gemini consultation (2026-05-09), logged at
>    [`tools/knowledgebase/message/20260509_163828.txt`](/tools/knowledgebase/message/20260509_163828.txt)
> 3. The system-separation conventions of QEMU TCG / Dynarmic / Dolphin
>
> **Goal**: when adding a 4th CPU plus the corresponding board, only JSON
> specs need to be written; the framework's C# layer does not change, does
> not get duplicated, and does not depend on the existence of the new CPU.

---

## 1. Motivation

### 1.1 Current problems

After N1.B', *state access* is now a unified framework mechanism
(alloca+mem2reg) and emitters are mode-agnostic. But the **other block-JIT
optimization strategies** are still hardcoded per-CPU in C#:

| Optimization | Current state | Where hardcoded |
|---|---|---|
| Cycle interpretation (m-cycle×4 vs 1×) | NesJsonCpu setter | `BlockFunctionBuilder.CyclesPerSpecUnit = 1` in NesJsonCpu C# |
| Compile-time imm extraction | LR35902 emitter internal if-branch | `Lr35902Emitters.FetchImmediate` |
| WRAM/HRAM region inline | LR35902-specific | `Lr35902Emitters.EmitWriteByteWithSyncAndRamFastPath` |
| SMC notification range | All addresses | `NesMemoryBus.SmcWriteHook` fires regardless of region |
| Block-boundary on bank switch | callback hack | `IMapper.PrgBankSwitched` + Mapper001 fires + JsonCpu invalidates |
| Block size cap | hardcoded 64 | `BlockDetector.DefaultMaxInstructions` |

Adding a new CPU = learning 5+ C# emitter patterns and individually deciding
which ones the new ISA wants.

### 1.2 The shape we want

The flow for adding a new CPU:
1. Write `spec/<arch>/cpu.json` (pure ISA semantics — already the existing
   design)
2. Write `spec/machines/<machine>.json` (the board's memory map — a new
   concept)
3. Do not change framework C#

The framework automatically:
- Reads both specs
- Configures BlockFunctionBuilder / BlockDetector / each BlockCache hook
- mem2reg + decoder pre-extract immediate (always on, not a toggle)
- SMC walks a page-bitset (32-bit-friendly scaling)

---

## 2. Core design: separating CPU spec from Machine spec

### 2.1 Why split

**The Ricoh 2A03 CPU spec should not know "NES WRAM lives at $0000-$1FFF"**
— that's a property of the NES board. If we encode the memory map in
cpu.json:

- Reusing the same 6502 spec on another machine (even hypothetically) breaks
- The CPU spec author has to understand board layout (mixed responsibilities)
- Framework code that needs to distinguish "ISA property vs board property"
  also gets muddled

### 2.2 The responsibilities of each spec

#### `spec/<arch>/cpu.json` — pure ISA

| Field | Example | Description |
|---|---|---|
| `architecture` | `"id": "Ricoh2A03", "family": "MOS6502"` | (existing) |
| `register_file` | A/X/Y + P/SP/PC | (existing) |
| `instruction_sets` | `Main` 256 opcodes | (existing) |
| **`isa_metadata`** (new) | (see below) | block-JIT-relevant ISA properties |

`isa_metadata` example (2A03):
```json
"isa_metadata": {
    "endianness": "little",
    "pc_alignment_bytes": 1,
    "instruction_size_bytes": "variable",       // "variable" or N
    "cycles_per_spec_unit": 1,                  // NES raw cycles; GB/ARM 4
    "cycle_nuances": {
        "branch_taken_penalty": 1,
        "page_cross_penalty": 1
    },
    "pc_update_policy": "lazy",                 // "lazy" or "eager"
    "interrupt_check_policy": "end_of_block"    // multi-choice: "every_instruction" / "end_of_block" / "backwards_branch"
}
```

#### `spec/machines/<machine>.json` — board / system

```json
{
    "name": "nes-ntsc",
    "cpu": "Ricoh2A03",
    "memory_regions": [
        {
            "name": "wram",
            "addr_start": "0x0000",
            "addr_end_exclusive": "0x2000",
            "type": "ram",
            "mirror_mask": "0x07FF",
            "fastmem_eligible": true,
            "smc_notify": true
        },
        {
            "name": "ppu_io",
            "addr_start": "0x2000",
            "addr_end_exclusive": "0x4000",
            "type": "io",
            "mirror_mask": "0x2007",
            "side_effects": ["ppu"],
            "forces_end_of_block": false
        },
        {
            "name": "apu_io",
            "addr_start": "0x4000",
            "addr_end_exclusive": "0x4020",
            "type": "io",
            "side_effects": ["apu", "oam_dma"]
        },
        {
            "name": "cart_prg",
            "addr_start": "0x4020",
            "addr_end_exclusive": "0x10000",
            "type": "io",
            "side_effects": ["mapper"],
            "forces_end_of_block": true
        }
    ],
    "interrupt_vectors": {
        "nmi":   "0xFFFA",
        "reset": "0xFFFC",
        "irq":   "0xFFFE"
    }
}
```

#### Region `type` semantics

| `type` | Meaning | Framework behavior |
|---|---|---|
| `ram` | General-purpose read/write RAM | `fastmem_eligible: true` → block-JIT may inline a GEP; writes trigger SMC notify |
| `rom` | Read-only code | `fastmem_eligible: true` (reads); writes are ignored; SMC is not triggered |
| `io` | Side-effect region (PPU / APU / mapper / cart) | always goes through bus extern; `forces_end_of_block: true` ends the block after a write |

#### Memory bus rewrite

NesMemoryBus / GbMemoryBus / GbaMemoryBus are rewritten to be built
dynamically from the Machine spec:

- Keep the `byte ReadByte(uint addr) / void WriteByte(uint addr, byte v)`
  API (unchanged)
- Internal dispatch changes from a hand-coded if-else chain to a
  spec-driven region table lookup
- bus.WriteByte automatically fires SMC notify if `region.smc_notify`
- `region.side_effects` triggers the corresponding host hook (PPU register
  write / APU / mapper)

Note: **the actual internal logic of an IO operation** (e.g. how a PPUCTRL
write affects PPU state) is still hardcoded in C# — JSON only describes
*meta-properties* such as "this addr belongs to which IO subsystem" and
"does writing this end the block".

---

## 3. Framework automatic behavior (no longer a toggle)

Incorporating Gemini's feedback — these are not "per-CPU optional
optimizations" but should always be on at the framework level:

### 3.1 Decoder pre-extract immediate

`DecoderTable.Decode(opcode)` already returns a `DecodedInstruction`
struct. Extend it to include a *pre-extracted* immediate value:

```csharp
public sealed record DecodedInstruction(
    InstructionDef Instruction,
    InstructionFormat Format,
    uint InstructionWord,
    // NEW:
    uint? Immediate,        // null if instr has no imm
    int   InstructionSize   // 1, 2, 3 ... bytes
);
```

For variable-width ISAs, after BlockDetector finishes the fetch the
instruction word holds the full instr bytes. The decoder extracts the imm
from the word and hands it to the emitter.

How emitters use it:

```csharp
// Mos6502 LDA #imm switches to ctx.DecodedInstruction.Immediate.Value
var imm = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8,
    ctx.DecodedInstruction.Immediate ?? throw, false);
ctx.Builder.BuildStore(imm, ctx.GepGpr(0));   // A := imm
```

No more `if (block-JIT) bake_const else bus.read()` branching. **One
unified path**.

### 3.2 Page-bitset SMC

Replace `BlockCache._coverageCount[byte * 64KB]`:

```csharp
private readonly ulong[] _coverageBitset;  // 1 bit per N-byte page
private readonly int _pageShift;            // log2(page_size); default 12 (4KB pages)
```

SMC notify hot path:
```csharp
public bool NotifyMemoryWrite(uint addr) {
    int page = (int)(addr >> _pageShift);
    int word = page >> 6;
    int bit  = page & 63;
    if ((_coverageBitset[word] & (1ul << bit)) == 0) return false;
    // slow path: linear scan blocks at this page
    ...
}
```

For the 16-bit NES (64KB / 4KB-page) = 16 pages = 1 ulong (64 bits, more
than enough), saving compared to today's 64KB byte array. For 32-bit ARM
(4GB / 4KB-page) = 1M pages = 128KB bitmap, still acceptable.

### 3.3 Region-driven block boundary

BlockDetector automatically checks whether each instruction writes to a
`forces_end_of_block: true` region. `STA $8000` writes to cart_prg
(flagged forces_end_of_block on the NES board) → BlockDetector ends the
block after this instruction.

The IMapper.PrgBankSwitched callback is no longer needed as a fallback
(it's kept as a secondary safety net but is no longer the main mechanism).

---

## 4. What should **not** go into JSON (avoid over-declarative)

| Item | Why C# stays | Alternative design |
|---|---|---|
| LLVM compile pass list | Tied to LLVM version; not an ISA property | C# enum: `OptimizationLevel.{None,Fast,Aggressive}` |
| Concrete IR shape (the GEP internals of a region inline) | This is "how to optimize", not "what to optimize" | C# emitter consumes the region property and constructs IR internally |
| MMC1 shift register logic | Mapper behavior is pure C# | `IMapper` interface (already exists) |
| Side effects of individual IO registers (PPUCTRL write) | Too many details | hook by `side_effects` array name |

**JSON describes *what is true*; C# handles *how to act on it*.**

---

## 5. Migration plan (step-by-step)

Each step finishes → T1 + the corresponding ROM tests + commit + push,
before moving on. **Any test capped at 5-minute timeout** (the CLAUDE.md
convention).

### Step 0: design doc landed (this doc)

Verification: this doc is committed + pushed. **No code changes**.

### Step 1: framework primitives (cross-cutting; must land in one shot)

These **cannot** be done per-CPU incrementally because they change the
shared layer:

1.1 Add a `MachineSpec` C# class + `spec/machines/_schema.json`
1.2 Extend the `DecodedInstruction` struct with `Immediate?` and
    `InstructionSize`
1.3 Move `BlockCache` byte-coverage → page-bitset
1.4 Add `forces_end_of_block` handling to `BlockDetector`

**Verification**: every existing test still passes (T1 + nestest across
three backends + blargg cpu_test5 across three backends + GB blargg
cpu_instrs json-llvm + block-jit + ARM7TDMI tests). **Old paths are
preserved**, the new infrastructure is additive — no CPU is required to
migrate first to enable the build.

### Step 2: ARM7TDMI / GBA migration

2.1 Write `spec/machines/gba.json` (BIOS / EWRAM / IWRAM / IO / Palette /
    VRAM / OAM / Cart ROM regions)
2.2 Add an `isa_metadata` section to `spec/cpu/arm7tdmi/cpu.json`
2.3 Switch GbaMemoryBus to MachineSpec-driven dispatch
2.4 Switch ArmEmitters to the new DecodedInstruction.Immediate (where it
    has use sites)

**Verification**: T1 + ARM tests + the GBA emulator running the same ROM
compared to previous perf.

### Step 3: LR35902 / GB migration

3.1 Write `spec/machines/gb_dmg.json`
3.2 Add `isa_metadata` to `spec/cpu/lr35902/cpu.json`
3.3 Switch GbMemoryBus to MachineSpec
3.4 Simplify Lr35902Emitters: `FetchImmediate` no longer branches (always
    uses DecodedInstruction.Immediate); `EmitWriteByteWithSyncAndRamFastPath`
    becomes region-table-driven
3.5 Remove GB's hardcoded `BindExtern`s like Lr35902WramBase /
    Lr35902HramBase (replaced by region-driven equivalents)

**Verification**: T1 + GB blargg cpu_instrs json-llvm + block-jit still
11/11. Compare perf to the N1.B' baseline; we want it equal or better.

### Step 4: Ricoh 2A03 / NES migration

4.1 Write `spec/machines/nes_ntsc.json`
4.2 Add `isa_metadata` to `spec/cpu/2a03/cpu.json` (cycles_per_spec_unit=1
    is read from here)
4.3 Switch NesMemoryBus to MachineSpec
4.4 Add imm-bake to Mos6502Emitters (via DecodedInstruction.Immediate;
    NES does not currently have this optimization, so the migration should
    bring a perf boost)
4.5 Remove NesJsonCpu.SmcWriteHook hardcoding — switch to region-driven
4.6 Remove the IMapper.PrgBankSwitched callback and the Program.cs wiring
    (forces_end_of_block takes over)

**Verification**: T1 + nestest across three backends + blargg cpu_test5
across three backends. Run a perf bench to see whether NES block-JIT goes
from 0.83 (per-instr) up to ≥1.5 MIPS (the expected gain from imm-bake).

### Step 5: cleanup

5.1 Mark the existing `BlockFunctionBuilder.CyclesPerSpecUnit` as obsolete
    (or delete it — it's now read from spec.json)
5.2 Mark `IMapper.PrgBankSwitched` as obsolete (or delete)
5.3 Add an N1 closeout patch to doc #18, update status on doc #19
5.4 Write a sample doc: "step-by-step adding a new CPU" using the post-
    N1.B' + N2 spec structure to explain to future contributors

---

## 6. Risks and mitigations

### 6.1 ARM may not benefit from region inline (it's not done today; question whether adding it is worth it)

`Lr35902Emitters.EmitWriteByteWithSyncAndRamFastPath` is GB-specific; ARM
has no equivalent. During migration ARM might be `fastmem_eligible` but
not actually emit fastmem IR — that matches the status quo with no
regression. Mitigation: stage it; in the ARM stage *read* MachineSpec but
*do not enable* fastmem emit; once stable, add it later.

### 6.2 Spec schema changes invalidate existing cpu.json

Adding the `isa_metadata` section is additive — old specs without it fall
back to defaults. The schema validator marks `isa_metadata` as optional.

### 6.3 Framework code growth + test state-space explosion

Gemini warning: too many boolean toggles cause N×2^k configuration
explosion.

Mitigation:
- Each option allows only "default" + "override", not multiple values
- Only three `region type` values (`ram`/`rom`/`io`), no custom ones
- Add a unit test "the same ROM should produce identical results under
  default + override" to ensure policy does not affect correctness

### 6.4 Reverting on a major step failure

One commit per step. On failure → `git revert <hash>` reverts to the last
stable point. This doc is detailed, the steps are small + verify-each,
risk is manageable.

---

## 7. Open questions

1. The relationship between `spec/machines/*.json` and multi-platform: in
   the future, if a machine has multiple variants (NTSC/PAL NES, DMG/CGB
   Game Boy) — separate files, or one file + variant section? (Leaning
   toward separate files, parallel to the variants section in the cpu
   spec.)

2. Whether to use namespace-style naming: `spec/cpus/` vs `spec/<arch>/`?
   Currently the cpu spec lives at `spec/<arch>/cpu.json`; aligning with
   `spec/machines/*` would mean `spec/cpus/<arch>.json`. But this is a
   large rename touching many things — **defer**.

3. Is `interrupt_check_policy: "end_of_block"` synonymous with the current
   "host checks ConsumePpuNmi after block exit"? Needs verification.

---

## 8. Reference

- Doc #18 (N1.B' state abstraction)
- Doc #16 (emulator completeness)
- Doc #17 (framework / emulator timing boundary)
- Gemini consultation record `tools/knowledgebase/message/20260509_163828.txt`
- Gemini consultation record `tools/knowledgebase/message/20260509_135453.txt` (#18 background)
