# Spec-driven runtime — closing the declarative-vs-hardcoded gap

> **Status (2026-05-09)**: N3 design phase. N2 prepared the spec
> structure (MachineSpec / IsaMetadata / DecodedBlockInstruction.Immediate / page-bitset SMC /
> forces_end_of_block hook), but **most of the runtime does not actually consume the spec yet**.
> This doc inventories the gap between what the spec describes vs. what is actually implemented,
> identifies which parts can realistically be moved into the spec and which parts must remain in C#
> (and why), and lays out a migration plan.
>
> **Goal**: push declarative coverage from ~50% (post-N2) to ~80%. The remaining
> 20% are framework escape hatches — documenting **why** they should stay in C#
> is itself part of the framework's design value.

---

## 1. Post-N2 inventory

### 1.1 Already declarative + actually wired ✓

| Item | Spec location | How runtime uses it |
|---|---|---|
| Register file | `cpu.json::register_file` | `CpuStateLayout` auto-built from spec |
| Instruction encoding | `cpu.json::instruction_sets` + `groups/*.json` | `DecoderTable` auto-built from spec |
| Instruction semantics | `groups/*.json::steps[]` | `SpecCompiler` emits IR via `EmitterRegistry` dispatch |
| Cycle multiplier (m-cycle vs raw) | `cpu.json::isa_metadata.cycles_per_spec_unit` | NesJsonCpu reads spec starting in N2.5 |
| Block-JIT cycle accounting | `groups/*.json::cycles.form` (per-instruction) | parsed by `BlockFunctionBuilder.ParseCyclesForm` |
| Per-CPU emitter mode-agnostic | (no spec field — IR layer) | `IStateSlotProvider` (alloca / state-buffer) auto-switch |

### 1.2 Already declarative but **not wired yet** ⚠️

| Item | Spec location (exists) | Where the runtime still hardcodes it |
|---|---|---|
| Memory map (region ranges / types) | `spec/machines/*.json::memory_regions[]` | if-else chains in `NesMemoryBus.ReadByte/WriteByte`; same in `GbMemoryBus.cs` and `GbaMemoryBus.cs` |
| `forces_end_of_block` flag | `region.forces_end_of_block` | BlockDetector has the hook but NesJsonCpu does not wire it (still relies on the `IMapper.PrgBankSwitched` callback) |
| `smc_notify` region range | `region.smc_notify` | `NesMemoryBus.SmcWriteHook` fires for all addresses (no region filter) |
| Interrupt vectors | `machine_spec.interrupt_vectors` | `NesJsonCpu.NmiInterrupt` hardcoded to `$FFFA` / `$FFFB` |
| Region side_effects tags | `region.side_effects: ["ppu", "apu", "mapper", ...]` | hardcoded `WritePpu` / `WriteApu` / `mapper.CpuWrite` C# dispatch |

### 1.3 Never declarative ❌

| Item | Not in spec | C# implementation |
|---|---|---|
| Per-instruction cycle table | (could in theory be derived from cycles.form) | `NesJsonCpu.s_cycleTable[256]` hardcodes the 6502 chart (mirroring LegacyCpu byte-for-byte) |
| Page-cross extra cycle | no spec field | not implemented |
| Mapper logic (NROM / MMC1 control reg / shift register) | no spec | `Mapper000.cs` / `Mapper001.cs` pure C# IMapper impl |
| PPU register write side effects | spec tags `side_effects: ["ppu"]` but the details are missing | `NesPpu.WriteRegister` switch case |
| NMI handler body (push state / set flags) | no spec | `NesJsonCpu.NmiInterrupt` pure C# |

---

## 2. What can / should / should not move into the spec — design criteria

### 2.1 Rule: criteria for moving into the spec

✓ **Naturally and effortlessly declarative** (addr range / cycle count / region type) — one line of JSON beats one C# class
✓ **Recurs across multiple CPUs** (memory regions, interrupt vectors, cycle costs all show up in all 3 CPUs)
✓ **Does not need a turing-complete description** (a fixed schema is enough; do not invent a sub-language)
✓ **Runtime read cost is acceptable** (startup-time parsing OK; hot-path lookup at 100Hz OK; hot-path lookup at 100MHz no good)

### 2.2 Rule: criteria for keeping it in C#

✗ **State-machine behaviour** (MMC1 5-bit shift register + 4 register banks → pure logic)
✗ **Side-effect specific procedures** (PPU $2007 write triggers a vram_addr increment plus possibly fires NMI)
✗ **Tightly coupled to runtime mutable state** (NMI handler push order, CPSR mode swap)
✗ **Per-instruction hot path (called millions of times per second)**

### 2.3 Grey area

? Region dispatch hot path — describing the region range in spec is declarative, but **finding the region from an address** is a runtime lookup. Once per memory access = hot path. Acceptable upper bound ~10ns (one binary search over ~10 regions).
? Declarative mapper interface — the mapper's **behaviour** stays in C#, but its **annotated config** ("PRG bank size = 16KB", "CHR mode 8KB/4KB") can be declarative. Incremental: extract the config first.

---

## 3. N3 scope — actual migration

Only touch things that **pass rule 2.1**, avoid 2.2, and start with the simplest pieces of the grey area to lower risk.

### N3.1 Interrupt vectors (lowest risk)

Change NesJsonCpu to read `interrupt_vectors["nmi"]` / `["irq"]` / `["reset"]` from MachineSpec.
- C# delta: 3 lines (NmiInterrupt swaps `$FFFA` for `_machineSpec.InterruptVectors["nmi"]`)
- Risk: low (vector addr does not change, only the source of the read)
- Value: documentation-grade — proof that the spec really can drive the runtime
- LegacyCpu untouched (kept hardcoded as oracle reference)

### N3.2 NES memory bus → MachineSpec table-driven

Rewrite NesMemoryBus.ReadByte/WriteByte from a hardcoded `Func<addr> → handler` dispatch to a region table lookup.

Concrete design:
1. At construction time, build a sorted region array + handler delegate from MachineSpec.
2. ReadByte: binary search a region by addr range (log₂(10) = ~3 comparisons), call the corresponding handler delegate.
3. The handler delegate is resolved by the region.side_effects name (`"ppu"` → bus.PpuRead, `"mapper"` → mapper.CpuRead, `"ram"` → direct array index with mirror_mask).

Risk: each byte access pays ~3 extra comparisons + 1 indirect call. For NES (1.7MHz × 3 = 5M memory accesses/s) = ~50M extra ops/s vs the original — should land within ±10%. If perf regresses by more than 10%, reconsider.

GB / GBA buses untouched (perf-sensitive; do them separately).

### N3.3 Per-instruction cycle table from spec — **BLOCKED** (spec format limit)

Attempt: spec-derived cycles compared against the LegacyCpu oracle for all 256 opcodes,
**145 mismatches**. Reason: spec.cycles.form is per-mnemonic (one form per ALU op),
not split per addressing mode. LegacyCpu is per-opcode (256 independent values).

Example: ORA spec `"3m"` → parses as 3 cycles. But the actual 6502:
- `$09` ORA #imm = 2 cycles
- `$05` ORA zp = 3 cycles
- `$0D` ORA abs = 4 cycles
- `$01` ORA (zp,X) = 6 cycles
- ...

Supporting per-opcode cycles requires a spec format restructure:

(a) A separate instruction-def per (mnemonic, addressing-mode) — ALU 8 → 64
    entries (4-5× spec bloat)
(b) Add `cycle_table` on InstructionFormat — bbb selector mapping to a cycle-count
    table

**Neither is in N3 scope**. NesJsonCpu keeps using the hardcoded `s_cycleTable[256]`
(mirroring LegacyCpu so the oracle stays consistent); block-JIT uses the coarser spec.form value
(within subtest tolerance).

The corresponding test: `MachineSpecTests.Mos6502CycleTable_DerivedFromSpec_DivergesFromOracle`
is marked `[Fact(Skip)]` with an XML doc explaining the limitation — once the spec is improved we can clear the skip and verify the new spec passes.

### N3.4 Branch-taken / page-cross cycle nuances

block-JIT already has `ParseCyclesFormBoth` handling "Nm_or_Mm". The per-instruction backend should align — taken-branch +1 cycle can be derived from the spec (the data is already there, the per-instruction backend just does not honour it).

Page-cross extra: add a new field to the spec `cycle_nuances.page_cross_addr_modes: ["abs_x", "abs_y", ...]` or instr-level `page_cross: true`. This is new spec schema. Design first, do not rush implementation — weighed against the actual perf gain it may not be worth it.

### N3.5 What stays in C# (explicitly marked)

- **Mapper000/Mapper001 C# classes** — state machine + bank-switch logic
- **NesPpu register write handlers** — heavy in side effects (vram_addr increment, VBL flag, NMI fire)
- **NesJsonCpu.NmiInterrupt body** — vector is read from spec, but push order and flag set are procedural code
- **NesPpu.Tick** cycle-accurate scanline counter — too dense for declarative

Spelled out in the doc so we do not relitigate "should this be declarative?" later.

---

## 4. Quantification — expected declarative ratio

Rough estimate of "framework code lines that drive the runtime" (eyeballed):

| Layer | Post-N2 declarative | Post-N3 (estimated) |
|---|---|---|
| CPU semantics | 95% (spec.steps + emitters) | same |
| Memory bus dispatch | 0% (pure C# if-else) | ~70% (NES on spec; GB/GBA still C#) |
| Interrupt routing | 30% (vector tagged in spec but NesJsonCpu reads it hardcoded) | 90% (vectors actually read) |
| Cycle accounting | 60% (block-JIT goes through spec, per-instruction hardcoded) | 95% (per-instruction also goes through spec) |
| Mapper / IO devices | 0% (pure C#) | 0% (kept) |
| **Weighted average** | **~50%** | **~80%** |

The remaining 20% stays in C# permanently (mapper / device side effects / NMI body) — documented as "framework escape hatches".

---

## 5. Risk + rollback

1. **N3.2 NES bus perf regresses > 10%** — revert that commit; either add caching to spec-driven dispatch (per-addr inline cache or similar) or abandon the step.
2. **N3.3 cycle table mismatch** — spec disagrees with LegacyCpu → either spec is inaccurate (fix the spec) or LegacyCpu has a quirk (keep the hardcoded path as fallback).
3. **GB/GBA affected** — N3 scope only touches NES. GB/GBA changes are a future N4+ topic.

---

## 6. Migration order (conservative)

1. **N3.0** Land this doc (commit, no code changes)
2. **N3.1** Interrupt vectors (simplest, lowest risk)
3. **N3.2** NES bus dispatch (largest delta, perf risk)
4. **N3.3** Per-instruction cycle table (medium risk)
5. **N3.4** Closeout — perf bench + quantify declarative ratio + write into perf doc

**N3.5** is not done (mapper / NMI body / PPU regs stay as C# escape hatches, documented).

After every step → T1 + nestest (three backends) + blargg (three backends) + commit + push, then move to the next. **5-minute timeout cap on all tests** (CLAUDE.md convention).

---

> **2026-05-09 update — N4 invalidations recorded**
>
> The N3.4 conclusion "legacy backend ~7% slower" (1.69 → 1.57 MIPS) has been
> dramatically improved by the N4 series. N4.3 page-table O(1) dispatch + N4.4
> offset semantics push legacy back to 1.65 MIPS, leaving only ~2% from the N1 baseline.
>
> The N3.4 doc mentions "future perf-critical work could turn the region scan into a
> jump table" — N4.3 has implemented exactly this approach (32-byte page → 2048 entries).
>
> N3.4 §1.1 conclusion: "we choose to accept the 7%" is now obsolete — N4.3 actually
> recovered it.
>
> N3.4 §2.1 "Memory bus dispatch 80% declarative" has also been pushed to 95% by N4
> (see `MD/performance/202605091900-n4-memory-spec-v2.md` for details).
>
> The N4 design + closeout records are at:
> - `MD/design/22-memory-spec-v2.md` — schema v2 design
> - `MD/performance/202605091900-n4-memory-spec-v2.md` — 3-run bench + ratio

---

> **2026-05-09 (later) update — N3.3 BLOCKED → RESOLVED**
>
> N3.3 "per-(mnemonic, addressing-mode) cycle granularity" is fully closed out.
> Two stages:
> 1. commit `027fe79` — added `cycles.table` schema (CycleTable record),
>    cc=01 ALU group 64 opcodes converted.
> 2. commit `c806358` — remaining 7 groups all converted + 12 KIL forms fixed;
>    the hardcoded oracle `NesJsonCpu.s_cycleTable[256]` was removed from the
>    codebase entirely, replaced by `BuildSpecCycleTable(decoder, cyclesPerSpecUnit)`
>    which dynamically derives it in the constructor.
>
> The N3.3 §"Pragmatic fix would require restructuring spec..." paragraph is obsolete —
> we took the **(b) per-format cycle_table** path (the option Gemini recommended during
> consultation), the spec size barely changed, and the resolver logic took only 12 lines of C#.
>
> Per-instruction cycle table declarative ratio: 60% → **100%**. Framework
> overall ratio: ~78% → **~85%**.
>
> Detailed record: `MD/performance/202605092000-n33-full-spec-cycles.md`
