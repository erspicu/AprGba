# AprGba / AprCpu — Project Overview

## One-line Definition

A data-driven, general-purpose CPU emulation framework that **describes CPU instruction specifications in JSON**, has a **C# Parser automatically translate them to LLVM IR**, and uses the **LLVM JIT to compile to native machine code**, with **GBA (ARM7TDMI)** as the primary validation target.

## Why Build This

Traditional emulators (mGBA, VBA) hardcode their CPU cores in C, with one case per instruction. Maintenance is painful, horizontal extension to other CPUs is hard, and performance optimization is all manual.

Differentiators of this project:

- **Data-driven**: CPU spec = JSON, logic = a set of generic micro-ops. Swapping CPUs only requires swapping the JSON
- **Dimensional reduction**: N micro-ops can compose any instruction, O(1) instead of O(N)
- **LLVM backend**: industrial-grade optimizations like register allocation, SSA, DCE, and vectorization come for free (assuming LLVM compile cost is acceptable, see `01-feasibility.md`)
- **Gap in the .NET ecosystem**: a modern, developer-friendly CPU emulation framework is currently missing

## Scope

### Primary Goal (MVP) ✅ Fully complete (2026-05-02)

- ARM7TDMI (ARMv4T) — both ARM 32-bit and Thumb 16-bit modes ✅
- Pass jsmolka GBA test ROMs (arm.gba / thumb.gba / bios.gba) + PNG screenshots
  for visual validation ✅
- **Boot with a real BIOS file** (LLE, run through the official BIOS intro → ROM entry),
  giving more credible validation (not an HLE shortcut) ✅ — boot logo visually matches mGBA
- PPU (Mode 0/1/2/3/4 + full OBJ + BLDCNT alpha/brighten/darken +
  WININ/WINOUT/OBJ Window) — **hand-written host code, not driven by JSON spec**
  (rationale in "Explicit exclusions" below) ✅
- **headless CLI mode**: `apr-gba --rom=X --bios=B
  [--cycles=N | --frames=N | --seconds=N] --screenshot=Y.png`,
  **no GUI / no 60fps loop / no real-time playback** ✅
- Windows platform first ✅

Completion snapshot (as of 2026-05-09): **455 unit tests all green**; arm/thumb/bios.gba all PASS within 6 GBA-seconds via real BIOS LLE; on the GB side, BIOS LLE + DMG Nintendo® logo screenshot captured; on the NES side, nestest (three backends) + blargg cpu_test5 (three backends) all PASS (PC=$8003 self-loop, all 11 subtests). Full closing notes:
- [`MD_EN/note/phase5-gba-mvp-complete-2026-05.md`](/MD_EN/note/phase5-gba-mvp-complete-2026-05.md) (5.1–5.4)
- [`MD_EN/note/phase5.7-bios-lle-and-ppu-2026-05.md`](/MD_EN/note/phase5.7-bios-lle-and-ppu-2026-05.md) (5.5–5.7 + Phase 8)
- [`MD_EN/note/loop100-bench-2026-05.md`](/MD_EN/note/loop100-bench-2026-05.md) (**canonical performance baseline**
  — pre-Phase-7 reference, 1200-frame loop100 ROM measurement)

### Extension of the Primary Goal: framework generality validation ✅ Complete (2026-05-02)

- **GB LR35902 port** (Phase 4.5) is complete — using `erspicu/AprGBemu`'s (an existing
  GB emulator) hand-written CPU as reference, the GB CPU was written as a JSON
  spec, runs on the same host runtime, and **passes Blargg cpu_instrs 11/11 + master
  "Passed all tests"**, with screenshots fully matching LegacyCpu. The core promise
  of "swap CPUs by swapping JSON" is now validated. Details in
  [`MD_EN/design/09-gb-lr35902-validation-plan.md`](/MD_EN/design/09-gb-lr35902-validation-plan.md),
  [`MD_EN/design/10-lr35902-bit-pattern-groups.md`](/MD_EN/design/10-lr35902-bit-pattern-groups.md),
  [`MD_EN/note/framework-emitter-architecture.md`](/MD_EN/note/framework-emitter-architecture.md).

### Third CPU port: Ricoh 2A03 / NES ✅ Complete (2026-05-09)

- **NES 2A03 port** (N0-N1 series) is complete — the third CPU runs on
  the same framework, with nestest + blargg cpu_test5 all PASS across
  three backends (legacy / json per-instr / json-block). Details in
  [`MD_EN/design/20-adding-a-new-cpu.md`](/MD_EN/design/20-adding-a-new-cpu.md).
- **Spec-driven runtime deepening** (N3-N9 series) — pushed the
  framework's declarative ratio from N2's ~50% to ~85%: interrupt
  vectors, memory bus dispatch, per-(mnem, addressing-mode) cycle table,
  access widths, and dynamic cycle penalties are all now spec-driven.
  Details in [`MD_EN/design/21-spec-driven-runtime.md`](/MD_EN/design/21-spec-driven-runtime.md),
  [`MD_EN/design/22-memory-spec-v2.md`](/MD_EN/design/22-memory-spec-v2.md);
  perf benches in `MD/performance/202605091900-n4-memory-spec-v2.md` +
  `MD/performance/202605092000-n33-full-spec-cycles.md`.
- **New framework infrastructure** (N5/N7/N8/N10/N11) — generic
  lockstep diff toolkit (any CPU implementing `ISteppableCpu` can be
  cross-tested), fastmem query API (`TryGetHostPointer`), ARM
  page-table dispatch (GbaMemoryBus also became spec-driven),
  allowed_widths debug-mode enforcement, fastmem block-JIT inline path
  (opt-in).
- **JSON spec driven**: `spec/2a03/cpu.json` + 7 group files +
  `spec/machines/nes-ntsc.json`; all 256 opcodes are spec-derivable
  including cycle counts. N3.3 BLOCKED → RESOLVED.

### Explicit exclusions (not in v1, 2026-05 scope decisions)

- **Audio (APU)** — not done
- **Gamepad / KEYINPUT polling** — not done (test ROMs don't press buttons)
- **GUI / real-time 60fps window** — pure headless CLI, output PNG when done
- **Commercial game compatibility / more homebrew** — the goal is jsmolka test ROM
  visual validation; passing them is enough, no chasing other ROMs
- **Master Clock cycle-accurate model** (using instruction-level catch-up instead)
- ~~**A third CPU** (MIPS, RISC-V, 6502, etc.) — Phase 4.5 GB validation already covers
  the key unvalidated dimensions of framework generality (variable-width decoding,
  prefix opcodes, paired registers, 8-bit GPRs, special flag layouts); adding more
  CPUs has diminishing marginal returns~~
  → **2026-05-09 update**: 6502 / NES 2A03 was done after all (N0-N11). The actual
  marginal benefit of "adding another CPU" turned out to be much higher than originally
  estimated — driving a third ISA through the same framework exposed multiple spec
  format gaps (per-(mnem, addressing-mode) cycle granularity, memory bus
  declarativity, generalising page-table dispatch); after closing those, the
  framework's declarative ratio jumped from ~50% to ~85%. Next 4th-CPU candidate:
  **8086** (segmented memory + 16-bit CISC) — using a previously hand-written
  emulator as a reference oracle.
- **Link cable, infrared, rumble, and other peripherals**
- **Cross-platform host** (Linux/Mac, ARM64 host CPU)
- **PPU / APU / DMA written as JSON spec** — the framework's "data vs verb"
  split only makes sense for instruction streams; fixed-function units (PPU
  pipeline, APU 4-channel, DMA controller) have no cross-device reusability,
  and forcing them into JSON just turns if-else into a data table, **with no
  framework leverage**. Write these directly as host code, following the
  `GbPpu` pattern from the GB side
- **Block-JIT performance optimization** — downgraded from required to optional;
  test ROMs running a bit slow doesn't matter
  (current GBA bench ~40% real-time, sufficient for screenshots)

### Late-stage extensions (pending, explicitly not in v1)

- Full PPU advanced effects (Window, Mosaic, Affine BG mode 1/2, Blend)
- Full DMA / Timer / Interrupt emulation
- AOT precompiled cache (`.bc` files, avoiding cold-start LLVM compile cost)

## Naming

- Project overall: **AprGba**
- CPU framework core: **AprCpu**
- CLI tool: `aprcpu.exe`

## Key Design Beliefs

1. **LLVM is not a silver bullet.** It solves register allocation and codegen, but compile time is a real cost that must be confronted.
2. **100% pure JSON is an unrealistic goal.** A few ARM quirks (Barrel Shifter edges, LDM/STM, rotated reads on unaligned access) will fall through to C# handlers. Accept this reality.
3. **Phased deliverables** > one big bang. Each phase must be independently demoable.
4. **Pragmatic accuracy**: instruction-level catch-up handles 95% of GBA games; the remaining 5% gets shored up via IO-write-triggered sync. No Master Clock.

## Target Audience

- Myself (learning CPU emulation, JIT, compiler backends)
- Developers wanting to try .NET + LLVM integration
- Emulator community (if open-sourced and stable enough)

## Document Map

- `00-overview.md` — this document, project overview
- `01-feasibility.md` — feasibility analysis (with risks)
- `02-architecture.md` — system architecture and component design
- `03-roadmap.md` — phased implementation roadmap
- `04-json-schema-spec.md` — full CPU spec JSON schema
- `05-microops-vocabulary.md` — micro-op vocabulary
- `06-arm7tdmi-completion-plan.md` — Phase 2.5 plan (complete)
- `07-spec-authoring-conventions.md` — spec authoring conventions
- `08-portability-refactor.md` — Phase 2.6 generalization refactor (complete)
- `09-gb-lr35902-validation-plan.md` — Phase 4.5 GB CPU port validation plan ✅
- `10-lr35902-bit-pattern-groups.md` — LR35902 bit-pattern grouping table (Phase 4.5C spec structure)
- `11-emitter-library-refactor.md` — Phase 5.8 emitter library refactor design and progress
- `12-gb-block-jit-roadmap.md` — Phase 7 GB block-JIT design + progress
- `13-defer-microop.md` — `defer` micro-op design (generic pattern for
  delayed-effect instructions)
- `14-irq-sync-fastslow.md` — `sync` micro-op + bus extern split
- `15-timing-and-framework-design.md` — synthesis of timing accuracy +
  framework genericity
- `16-emulator-completeness.md` — emulator completeness vs framework
  genericity trade-off
- `17-aprcpu-vs-emulator-timing-boundary.md` — boundary between AprCpu
  and the emulator on timing concerns
- `18-block-jit-state-abstraction.md` — block-JIT state abstraction
  (alloca + mem2reg) design
- `19-declarative-jit-policy.md` — declarative JIT policy + CPU/Machine
  spec split
- `20-adding-a-new-cpu.md` — **SOP for adding a new CPU** (with
  ARM7TDMI / LR35902 / 2A03 three reference examples)
- `21-spec-driven-runtime.md` — N3 spec-driven runtime (vector / bus /
  cycle table) + N3.3 RESOLVED record
- `22-memory-spec-v2.md` — N4 memory spec v2 (handler registry /
  page-table / offset semantics)

Cross-phase processes:
- [`MD_EN/process/01-commit-qa-workflow.md`](/MD_EN/process/01-commit-qa-workflow.md) — Tier 0-4 commit QA workflow (decide which tier to run before any commit, based on the nature of the change)
