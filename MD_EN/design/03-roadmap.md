# Implementation Roadmap

Adopting **Plan B: phased deliverables**. Each phase is independently shippable, avoiding one big-bang delivery.

Hobby-time estimate: 8–15 hours per week.

> **Status snapshot** (updated 2026-05-03): Phase 0/1/2/2.5/2.6/3/4.1/4.2/4.3/4.4/
> 4.5/5/7 (partial)/8 done.
>
> - **MVP**: GBA-side test ROM → real Nintendo BIOS LLE → full PPU pipeline
>   (Mode 0/1/2/3/4 + OBJ + BLDCNT + WIN). GB-side BIOS LLE + DMG Nintendo(R)
>   logo screenshot.
> - **Framework generality**: Phase 4.5 LR35902 full ISA spec (501 opcodes /
>   23 group files) + Blargg cpu_instrs 11/11 → "swap CPU = swap JSON"
>   validated. Phase 5.8 emitter refactor 5.1–5.7 done (`Lr35902Emitters.cs`
>   went from ~2620 → 1346 lines, −49%; 27 LR35902-specific ops folded into
>   generic ones).
> - **Phase 7 block-JIT**: A.1-A.6 + A.6.1 (Strategy 2 BIOS LLE patches) +
>   Phase 1a/1b (predictive cycle downcounting + MMIO catch-up callback) +
>   H.a (LLVM pass pipeline including instcombine, datalayout root cause
>   fix on 2026-05-03) all shipped. The `--block-jit` flag delivers
>   ~10-11 MIPS on HLE arm/thumb loop100 (vs per-instr ~8 MIPS, +30-40%);
>   BIOS LLE bjit ends up slower than per-instr because average block length
>   is only 1.0-1.1 instr — recorded as a known characteristic + followup.
>   C.b lazy flag deferred (correctness regression in BJIT/BIOS-LLE; the
>   main-branch version doesn't fit the recovery branch's structure).
> - **QA workflow**: every commit runs the corresponding tier based on the
>   change profile, see `MD/process/01-commit-qa-workflow.md` (T0 docs /
>   T1 360 unit tests / T2 8-combo screenshot matrix / T3 3-run loop100
>   bench / T4 baseline update).
>
> All 360 unit tests green; CLI flags consistent across GB + GBA
> (`--bios` / `--cycles` / `--frames` / `--seconds` / `--screenshot` /
> `--block-jit`).
>
> Full closeout notes: `MD/note/phase5.7-bios-lle-and-ppu-2026-05.md`;
> refactor progress: `MD/design/11-emitter-library-refactor.md`; Phase 7
> perf records: `MD/performance/`.
>
> Possible next steps: (a) Phase 5.8/5.9 third-CPU validation
> (RISC-V / MIPS); (b) Phase 7 follow-ups: A.5 SMC invalidation,
> A.7 block linking, E.c IR-level region check, fix BIOS LLE bjit
> short-block problem (detector follows through unconditional B);
> (c) Phase 9 APU.

---

## Phase 0: Environment + Spike (1–2 weeks) — Done

**Goal**: confirm LLVMSharp works on Windows 11 + .NET 10.

Completed:
- [x] `dotnet new sln`, set up the project skeleton described in the architecture doc
- [x] Install `LLVMSharp.Interop` 20.x + `libLLVM.runtime.win-x64`
- [x] Hello-world: emit `int add(int,int)` via LLVMSharp, JIT-execute, return 7
- [x] Verify `.ll` file output works

**Result**: `dotnet run --project src/AprCpu.Compiler -- --jit-only`
runs end-to-end. LLVM 20 + .NET 10 + LLVMSharp 20.x combination is stable.

---

## Phase 1: JSON Schema Design (2–3 weeks) — Done

**Goal**: define a JSON format capable of expressing ARM7TDMI.

Completed; see `MD/design/04-json-schema-spec.md`,
`MD/design/05-microops-vocabulary.md`,
`spec/schema/cpu-spec.schema.json`.

---

## Phase 2: JSON Parser + LLVM IR Emitter CLI (4–6 weeks) — Done

**Goal**: match the `aprcpu.exe --input sample.json --output out.ll` flow
from the Gemini conversation.

Completed:
- [x] JSON loader (System.Text.Json, `AprCpu.Core.JsonSpec.SpecLoader`)
- [x] `$include` mechanism, allowing specs to be split across files
- [x] Bit-pattern parser: `AprCpu.Core.Decoder.BitPatternCompiler`
- [x] Field extractor: `EmitContext.ExtractField`
- [x] Micro-op handler dictionary: `EmitterRegistry` + `IMicroOpEmitter`
- [x] Decoder dispatch table: `AprCpu.Core.Decoder.DecoderTable`
- [x] CpuState LLVM struct layout: `AprCpu.Core.IR.CpuStateLayout`
- [x] Main compiler: `AprCpu.Core.Compilation.SpecCompiler`
- [x] CLI: `aprcpu --spec <cpu.json> --output <out.ll>`
- [x] LLVM `TryVerify` passes

**Intentionally deferred from Phase 2 (completed in Phase 2.5)**: see
Phase 2.5 below.

---

## Phase 2.5: ARM7TDMI Spec + Parser Completion — Done

**Goal**: extend the spec to a **complete ARM7TDMI ISA**, with the
parser/emitter brought up to match.

Detailed sub-phases in `MD/design/06-arm7tdmi-completion-plan.md`.

Sub-phases:
- [x] 2.5.1 Spec authoring guidelines + lint hardening
- [x] 2.5.2 Full ARM Data Processing (×3 encodings) + PSR Transfer
- [x] 2.5.3 ARM Memory Transfers (SDT, Halfword/Signed, SWP)
- [x] 2.5.4 ARM Multiply (including Multiply Long)
- [x] 2.5.5 ARM Block Transfer + SWI + Coprocessor stub + Undefined
- [x] 2.5.6 Remaining 16 Thumb formats (F2, F4–F17, F19)
- [x] 2.5.7 Banked register swap + real raise_exception / restore_cpsr_from_spsr
- [x] 2.5.8 Coverage validation + closeout

**Acceptance**:
- arm.json + thumb.json cover the full ARMv4T standard ISA
- LLVM module passes Verify, 0 diagnostics
- **159 xUnit tests all green**
- **44 ARM mnemonics + ~30 Thumb mnemonics** all emit correctly
- Every micro-op has a corresponding emitter (CoverageTests enforces)
- No dead emitters (CoverageTests enforces)

---

## Phase 2.6: Framework Portability Refactor — Done

**Insertion point**: after Phase 2.5.2, before 2.5.3 begins.
**Status: R1–R5 all completed during Phase 2.5.**

Details in `MD/design/08-portability-refactor.md`.

5 refactors:
- [x] R1: build CpuStateLayout dynamically from `register_file`
- [x] R2: look up flag bit positions from `register_file.status[].fields`
- [x] R3: switch OperandResolver to a registry pattern
- [x] R4: drive cond gate from `global_condition.table` data
- [x] R5: split ARM-only emitters into a separate class

The real "swap CPU" validation moves to Phase 4.5 (GB LR35902 port) —
see below.

---

## Phase 3: Host Runtime + Interpreter Validation (3–4 weeks) — Done

**Goal**: actually execute the emitted LLVM IR and confirm instruction
semantics are correct.

Tasks:
- [x] **3.1 Host runtime skeleton**
  - C#-side `CpuState` struct (matches `CpuStateLayout`'s dynamic layout)
  - Memory-bus extern impls (`host_mem_read8/16/32`, `host_mem_write8/16/32`)
  - `host_swap_register_bank` impl (swaps banked R8–R14 into the visible
    slots based on mode)
  - LLVM ORC JIT extern bindings
- [x] **3.2 Fetch-decode-execute loop**
  - Walk PC, look up identity in `DecoderTable`, dispatch to JIT'd fn,
    advance PC
  - Conditional execution lives inside the cond gate; the main loop no
    longer special-cases it
  - R15 +8/+4 offset handled in the emitter; no longer in the main loop
  - No code cache, one dispatch per instruction (correctness first, perf
    later)
- [x] **3.3 Golden tests vs ARM Architecture Reference Manual**
  - 5–10 hand-picked instructions (ADD with flags, LDR with writeback,
    B with cond, MOV with shifter carry, MSR/MRS, SWI to verify banked
    swap)
  - Each test: build CpuState, run, assert post-state matches ARM ARM
    expected values

**Acceptance**: chosen instruction subset, all cases passing.
`host_swap_register_bank` validated via SWI tests.

**Biggest unknown**: LLVMSharp 20.x's ORC JIT API surface — start
de-risking from here.

---

## Phase 4: jsmolka arm.gba + thumb.gba CPU Validation (~2 weeks) — Done

> **Actual route**: originally planned to run armwrestler, but switched
> to jsmolka `gba-tests` (headless-friendly, better for automation).
> Phase 4.1 GBA memory bus, 4.2 halt detection, 4.3 arm.gba all green,
> 4.4 thumb.gba all green. armwrestler is reserved for Phase 8 once the
> PPU is up — for visual diffing.

Completed:
- [x] **4.1** GBA memory bus + ROM loader + IO stub (DISPSTAT toggle to
  avoid m_vsync infinite loop; BIOS vector stuffed with MOVS PC, LR no-op)
- [x] **4.2** CpuExecutor.RunUntilHalt detects B-to-self halt loops
- [x] **4.3** **jsmolka arm.gba all subtests pass** (R12=0, ~535 subtests
  covering Data Processing/Multiply/SDT/HSDT/SWP/Block Transfer/Branch/
  PSR/SWI/Coprocessor stub/Undefined). Fixed 13+ real ARMv4 CPU semantic
  bugs.
- [x] **4.4** **jsmolka thumb.gba all subtests pass** (R7=0, ~230 subtests
  covering logical/shifts/arithmetic/branches/memory). Fixed ~10
  Thumb-specific bugs. Multi-set CpuExecutor dispatches ARM/Thumb via
  CPSR.T.

**Acceptance**:
- arm.gba R12=0 in 7409 instructions
- thumb.gba R7=0 in 6874 instructions
- **195/195 unit tests all green**

CPU correctness has been end-to-end validated against real ROMs; the
framework's understanding of the full ARMv4T ISA passed a very strict
test.

---

## Phase 4.5: GB LR35902 Portability Validation (~2 weeks)

**Purpose**: use `erspicu/AprGBemu` (a GB emulator, ~94KB CPU.cs) as a
reference, write the GB CPU as a JSON spec, and **prove the framework
really can swap CPUs**.

Details in `MD/design/09-gb-lr35902-validation-plan.md`.

**Why here**:
- Phase 3 done → host runtime can already run IR
- Phase 4 done → framework verified correct on ARM
- This is the best moment to validate generality; if we waited until
  Phase 7 (LLVM Block JIT) to discover GB doesn't work, the rework cost
  would be too high
- AprGBemu is a ready-made reference implementation, allowing
  per-instruction lockstep diff of fetch-decode-execute state

**Why GB rather than 6502**:
- The GB CPU (Sharp LR35902, Z80-like) exercises several framework areas
  that are currently unverified:
  - True variable-width decoding (1/2/3 bytes)
  - Multi-instruction-set switching (CB-prefix opcodes enter a separate
    space)
  - Aliased / paired registers (A/F, B/C, D/E, H/L = 8-bit and also
    16-bit pair)
  - 8-bit GPR width (so far we've only really run 32-bit)
  - Different status flag layout (Z/N/H/C in the high nibble)
- 6502's variable-width and paired-register stories are simpler than GB,
  so it wouldn't cover the validation points above

**Acceptance**: see `09-gb-lr35902-validation-plan.md`. Scope (full ISA
vs Blargg subset) decided when we actually get there.

**Status** — **all complete** (2026-05-02):
- 4.5A: apr-gb CLI + GB memory bus + ROM loader + serial capture
- 4.5B: `LegacyCpu` (big-switch interpreter) + DMG PPU stub + PNG
  screenshots; passes Blargg cpu_instrs **11/11 all sub-tests** (including
  02-interrupts, with EI delay + cycle-table-driven DIV/TIMA timer
  implemented)
- 4.5C: `spec/lr35902/*.json` (23 group files, 501 opcodes) +
  `Lr35902Emitters.cs` (~50 micro-ops) + `JsonCpu` backend; passes Blargg
  cpu_instrs **11/11 + master "Passed all tests"**, screenshots match
  LegacyCpu exactly. Design rationale in
  [`10-lr35902-bit-pattern-groups.md`](./10-lr35902-bit-pattern-groups.md).

The **"swap CPU = swap JSON" claim is validated** (two completely
different CPUs, ARM7TDMI + LR35902, both run end-to-end through the
framework's spec→IR→JIT pipeline). Phase 4.5 closeout notes:
`MD/note/framework-emitter-architecture.md` and
`MD/note/performance-baseline-2026-05.md`.

---

## Phase 5: Memory Bus + BIOS LLE + PPU Completion — Done (2026-05-02)

> **Closeout notes**: `MD/note/phase5-gba-mvp-complete-2026-05.md` (5.1–5.4)
> + `MD/note/phase5.7-bios-lle-and-ppu-2026-05.md` (5.5–5.7).
>
> Phase 5 grew from "memory bus + minimal IRQ" all the way to **real
> BIOS LLE + full PPU**:
>
> - **5.1 IRQ + LLE BIOS foundation** — IO registers, IF/IE/IME,
>   bus.LoadBios
> - **5.2 DMA 4-channel** — immediate/VBlank/HBlank trigger + 16/32-bit
>   transfer
> - **5.3 Cycle scheduler + IRQ delivery** — full VBlank/HBlank/VCount IRQ
> - **5.4 apr-gba CLI** — headless ROM screenshot
> - **5.5 DISPSTAT/HALTCNT** — removed the phase 4.x toggle hack;
>   HALTCNT actually halts the CPU
> - **5.6 Cart logo + checksum patcher** — homebrew passes BIOS
>   verification
> - **5.7 BIOS LLE + 5 chained ARM bugs + PPU completion + GB CLI
>   alignment**
>
> 5.7 fixed 5 ARM7TDMI bugs (Thumb BCond +0 idiom, LSL/LSR by 32 carry,
> shift-by-reg PC+12, UMULLS/SMULLS NZ flag, exception entry clearing T);
> rewrote the PPU as per-layer composite + Mode 0/1/2 + full BLDCNT +
> Windowing; aligned the GB CLI's `--bios` / `--seconds` with GBA.

### Closeout acceptance (5.1–5.7 cumulative)

| Item | Result |
|---|---|
| jsmolka arm.gba (HLE) | All tests passed |
| jsmolka thumb.gba (HLE) | All tests passed |
| jsmolka arm.gba (BIOS LLE) | All tests passed @ 6 GBA-s |
| jsmolka thumb.gba (BIOS LLE) | All tests passed @ 6 GBA-s |
| jsmolka bios.gba (BIOS LLE) | All tests passed @ 6 GBA-s |
| BIOS startup logo (2.5 GBA-s) | mGBA-equivalent visual |
| DMG BIOS Nintendo(R) logo (apr-gb) | Renders correctly |
| Mode 0 stripes / shades | Visually correct |
| 345 unit tests | All green |

---

## Phase 5.8: Emitter Library Refactor — In progress (since 2026-05-02)

> **Design reference**: [`MD/design/11-emitter-library-refactor.md`](./11-emitter-library-refactor.md).
>
> Motivation: after Phase 5.7 closeout, `Lr35902Emitters.cs` reached
> ~2620 lines (40 ops), violating the framework's "swap CPU = swap JSON"
> promise. This phase brings each CPU's emitter workload from "~40 ops ×
> C# implementations" down to "at most 5–10 truly-unique ops + config
> metadata". **Does not affect Phase 5.7's jsmolka/Blargg/PPU validation
> results — pure structural cleanup.**

Progress (live; one commit per step + cleanup commit):

| Step | Status | Content |
|---|---|---|
| 5.1 | Done | Generalised stack ops (push_pair/pop_pair/call/ret + spec stack_pointer) |
| 5.2 | Done | Flag setters (set_flag/update_h_add/update_h_sub) |
| 5.3 | Done | Unified branch / call_cc / ret_cc + read_pc/sext + LR35902 JP/JR/CALL/RET/RST all-cc-variants migration + cleanup |
| 5.4 | Done | Bit ops + 8 shift forms unified + LR35902 BIT/SET/RES + CB-shift + A-rotate migration + cleanup |
| 5.5 | Done | Memory IO region unified (Binary auto-coerce + LDH/LD-(C) switched to generic `or` chain) + cleanup |
| 5.6 | Done | Removed cb_dispatch no-op; tagged IME/HALT/DAA as L3 intrinsics with reasons |
| 5.7 | Done | flag micro-ops (CCF/CPL) + INC/DEC family + 16-bit selector splits + L3 marker for operand resolver / compound ALU |
| 5.8/5.9 | Pending | Third-CPU validation (RISC-V RV32I or MIPS R3000) |

**5.7 closeout snapshot**:

- `Lr35902Emitters.cs` from ~2620 lines → **1346 lines (−49%)**
- Added/expanded shared layer: `StackOps.cs` (~415L), `FlagOps.cs`
  (~155L), `BitOps.cs` (~195L), `Emitters.cs` +~160L
- Removed **27 LR35902-specific ops + several helpers**
- Remaining 13 LR35902 ops all live in clearly-marked L3 intrinsic
  sections, in three categories: operand resolver (sss/dd tables),
  compound ALU + flag rules, true L3 hardware quirks (IME delay /
  HALT bug / DAA)
- 345/345 unit tests still green
- Blargg 02–11 passes on both legacy and json-llvm backends

**Validation principle**: at the end of each step, run the full unit-test
suite + at least 3 Blargg sub-tests (covering the ops touched in that
step) on both backends. Any regression triggers stop-the-line — no
pushing forward.

**Refactor's perf impact**: ran the same 1200-frame loop100 bench
(commit `cdf04ce`); GBA json-llvm slightly improved (+1-4%), GB
json-llvm slightly regressed (−3%), GB legacy down −15% (still off
baseline after multiple runs, but each run is only ~300ms so
measurement noise dominates). Detailed numbers in
`MD/note/loop100-bench-2026-05-phase5.8.md`. **Conclusion: the refactor
is perf-neutral on the main framework path (json-llvm)**, accomplishing
the "clean structure vs neutral speed" trade-off.

**Remaining work**:
- 5.8/5.9 third-CPU validation (RISC-V RV32I or MIPS R3000) — actually
  proves whether the L3 floor is reasonable. Won't know whether we need
  spec schema extensions like an "operand resolver registry" until the
  third CPU port is attempted.
- Until that third CPU lands, the refactor phase is essentially done.
  Next phase: third CPU. (Phase 7 block-JIT and APU are paused —
  neither affects validation of the "JSON-driven CPU framework"
  research thesis; commercial-game compatibility / real-time 60fps
  are not in MVP scope.)

---

## Phase 5: Memory Bus Closeout — original scope (superseded, kept for reference)

> **Scope decision (2026-05)**: GBA-side end-goal is "run test ROMs +
> screenshot-verify" (jsmolka-style); we are **not** chasing commercial
> games or extra homebrew compatibility. So Phase 5 scope was narrowed
> from the original "load commercial ROMs without crashing" to "PPU can
> get correct framebuffer contents".
>
> Phase 4.1–4.4 already covered most of GBA memory bus + ROM loader
> (`AprCpu.Core.Runtime.Gba.GbaMemoryBus` already loads jsmolka ROMs
> and runs both ARM and Thumb subtests starting from 0x08000000).

Remaining minimal tasks:
- [ ] PPU IO register R/W (DISPCNT / DISPSTAT / VCOUNT / BGxCNT /
  BGxHOFS/VOFS / WIN0H, etc.) — just enough that the PPU can read what
  the CPU wrote
- [ ] VBlank IRQ trigger + IE/IF/IME basic interrupt flow (test ROMs'
  CPU-side HALT/IRQ wait depends on this)
- [ ] DMA channel 0–3 (test ROMs heavily use DMA to move tile data and
  palettes into VRAM; without DMA, the screen is empty)
- [ ] **BIOS LLE: bring your own GBA BIOS file (gba_bios.bin), run from
  0x00000000**. No HLE shortcut — finish the official BIOS intro → ROM
  entry, so the CPU + memory bus + interrupt system are exercised by
  the actual BIOS boot flow, which is more credible than HLE-jumping
- [ ] BIOS load path: CLI flag `--bios=<path>` or env var

**Explicitly skipped**:
- HLE BIOS shortcut (we want a real LLE boot, not a fake one)
- Full SRAM / Flash / EEPROM save (test ROMs don't need it)
- KEYINPUT polling (no buttons being pressed)
- SOUNDCNT / sound (no APU)
- Precise Timer 0–3 (the minimal timing the PPU needs is inlined into
  the PPU loop)

**Acceptance**:
- jsmolka arm.gba / thumb.gba still green when run with BIOS (previously
  we skipped BIOS and started straight from ROM entry)
- Between BIOS intro and ROM entry, PC trace matches mGBA
- After test ROMs finish, VRAM contents look "as expected" (mGBA dump
  diff)

---

## Phase 6: Thumb Mode Validation — Done (folded into Phase 4.4)

The originally-planned Thumb work (CPSR.T state tracking, BX mode
switching, thumb.gba validation) **all happened in Phase 4.4**. The
multi-set design of CpuExecutor reads CPSR.T each step and dispatches
to the matching InstructionSet's decoder + PC offset. jsmolka thumb.gba
passes, ARM↔Thumb BX switching validated against real ROMs.

---

## Phase 7: LLVM Block JIT + Code Cache — **mandatory step**

> **Scope decision (2026-05-03 update)**: originally Phase 7 was billed
> as "the most challenging phase of the project, the key to running GBA
> faster than real hardware". In 2026-05 it was briefly downgraded to
> optional once the GBA-side goal narrowed to "test ROM + screenshot
> verify". **Now re-promoted to mandatory** — once we push the framework
> toward "running commercial games" or porting a third CPU,
> instruction-level dispatcher overhead will be a hard limit; block-JIT
> is the core capability that lets the framework stay viable across
> different CPUs and workloads.
>
> Already shipped: A.1-A.6 + A.6.1 sub-phase + Phase 1a/1b + H.a
> (with instcombine) + cleanup (see progress snapshot below). Remaining
> A.5 SMC detection / A.7 block linking / A.8 state→reg caching /
> A.9 profiling / E.c IR-level region check / H.b-i etc. are extension
> optimisations; only when those are done is Phase 7 truly complete.

Reasons to keep pushing:
- Use the framework at "actually running games" level (commercial GBA
  ROMs, homebrew platforms, other CPU emulation targets heavier than
  GBA)
- Demonstrate the claim that "a JSON-driven CPU framework, after
  block-JIT, can approach native interpreter speed"
- Future CPU ports won't suffer from per-instruction dispatch overhead

**Current canonical baseline (2026-05-02, post-Phase-5.7,
instruction-level JIT) in `MD/note/loop100-bench-2026-05.md`**.
1200-frame stress-test ROM (`*-loop100.gba` / `09-loop100.gb`),
unified measurement, 4 reference numbers:

| ROM                  | Backend     | Real-time × |  MIPS  |
|----------------------|-------------|------------:|-------:|
| GBA arm-loop100      | json-llvm   |   0.9×      |   3.68 |
| GBA thumb-loop100    | json-llvm   |   0.9×      |   3.70 |
| GB  09-loop100       | LEGACY      |  79.4×      |  38.36 |
| GB  09-loop100       | json-llvm   |   5.6×      |   2.73 |

After Phase 7 closes, rerun these 4 cases to see speedups (expecting
json-llvm to climb to 25-50 MIPS = 8-13× speedup). The earlier
Phase-4.5-era baseline (`MD/note/performance-baseline-2026-05.md`) is
**superseded** — measurement methodology and ROMs differ; kept only as
historical record.

### What needs to happen when this is actually built

Full list. After the Phase 5.8 emitter refactor cleaned up the
structure, this section enumerates every optimisation angle that
"really pushing the JIT to the limit" hits. Do them one at a time, run
the canonical loop100 bench
(`MD/performance/202605030002-jit-optimisation-starting-point.md`)
after each, log before/after.

Roughly low-risk-high-reward → high-risk-unverified order.

#### Progress snapshot (2026-05-03 update)

**14 steps + A.6.1 sub-phase + follow-up cleanup done** (commit
chronological order):
B.a OptLevel O3 → F.x id-keyed fn cache → F.y pre-built decoded
→ B.e cache state offsets → B.f permanent pin → B.g inline bus
→ E.a fetch fast path → E.b mem trampoline fast path
→ C.a width-correct flag → B.h Tick/IRQ inline
→ H.a LLVM pass pipeline (4-pass version) → A.1-A.6 block-JIT integration
→ A.6.1 Strategy 2 BIOS LLE patch sub-phase (13 commits, see below)
→ Phase 1a/1b predictive downcounting + MMIO catch-up
→ H.a-instcombine fix (datalayout root cause, 5-pass enabled 2026-05-03)
→ debug log cleanup (removed A.6.1 dev-only env-gated logging,
   bjit BIOS +7%)

**Cumulative result (vs Phase 5.8 starting baseline)**:

| Combo                | baseline | per-instr | block-JIT | real-time (bjit) |
|----------------------|---------:|----------:|----------:|-----------------:|
| HLE arm   loop100    | 3.82 MIPS | 7.5-8.2  | **10.3** | **2.4×** |
| HLE thumb loop100    | 3.75      | 8.0-8.2  | **11.4** | **2.7×** |
| BIOS arm   loop100   |    —      | 7.4-7.5  | 5.4-5.7  | 1.3× (bjit < pi: see known characteristic ↓) |
| BIOS thumb loop100   |    —      | 7.3-7.6  | 5.6-6.0  | 1.4× |
| GB  09-loop100 json  | 2.66      | ~6.5     |    —     | ~13× (GB on per-instr) |
| GB  09-loop100 LEGACY| 32.76     | ~31.7    |    —     | ~67× |

#### Progress snapshot (2026-05-04 update — GB block-JIT P0+P1 shipped)

GB block-JIT from scratch; sub-roadmap in
`MD/design/12-gb-block-jit-roadmap.md`. P0 4 steps + P0.5/P0.6/P0.7/P0.7b
follow-ups, P1 #5/#5b/#6/#7 main bodies all shipped.

**P0 4 steps** (full detail in
`MD/performance/202605040000-gb-block-jit-p0-complete.md`):
1. Variable-width `BlockDetector` (`3024100`) — `lengthOracle` callback
2. 0xCB prefix as 2-byte atomic (`0cb93a8`) — `prefix_to_set: "CB"` +
   sub-decoder
3. Immediate baking via instruction_word packing (`7a8305a`)
4. GB CLI `--block-jit` + Strategy 2 PC fixes (`adddade`)

**P0 follow-ups**: HALT/STOP boundary (`a10a718`), generic defer
micro-op (`ca248e8`), hybrid IRQ delivery sync micro-op
(`2a1de15`+`674316f`), conditional branch taken-cycle accounting
(`34f9f4b`+`d7314a8`).

**P1 main body** (`MD/design/12-gb-block-jit-roadmap.md` §3 Tier P1):
- **P1 #5 V1 block-local register shadowing** (`0e1e280`) — EmitContext
  shadow slot infra + ctx.GepGpr/GepStatusRegister + DrainShadowsToState;
  7 GPR + F + SP alloca shadows; mem2reg promotes to SSA. V1 -4% perf
  cost (entry/exit overhead exceeds savings on small blocks); V2 with
  per-block live-range analysis pending to flip the sign.
- **P1 #5b SMC V2** (`6c04422`) — IR-level inline notify (env
  `APR_SMC_INLINE_NOTIFY`) + precise per-instr coverage (always-on) +
  cross-jump-into-RAM unlock (env `APR_CROSS_JUMP_RAM`). Both env vars
  default OFF preserving V1 behaviour; with them ON, cpu_instrs sub-test
  03 livelocks due to invalidation cycle drift — pending V3
  deferred-invalidation fix.
- **P1 #6 cross-jump follow** (`dd99c98`) — JR/JP unconditional follow,
  ROM-only V1; V2 (P1 #5b unlock) env-gated.
- **P1 #7 IR-level WRAM/HRAM inline write** (`15f913f`) — bypasses bus
  extern; per-CPU pinned base pointers (lr35902_wram_base /
  hram_base).
- **P2 #8 A.5 SMC V1 infrastructure** (`8ce66ac`) — per-byte coverage
  counter + bus-extern path notify. Evolved into V2 by P1 #5b; merged
  into P1 scope.

**Cumulative result** (GB block-JIT path, cpu_instrs master):

| Mode | per-instr (json-llvm) | block-JIT | vs legacy |
|---|---:|---:|---:|
| @ 10k frames | ~9 MIPS | **~21 MIPS** | -32% (legacy 31) |
| @ 60k frames (compile amortised) | — | **~27 MIPS** | -13% |

**GBA-path known regression**: P0.7b left a -16% on GBA bjit (after
commit `d7314a8`); correctness OK but perf still pending. HLE arm went
from 10.3 → 8.7 MIPS.

**Remaining P2/P3/P4**: see `MD/design/12-gb-block-jit-roadmap.md` §3.
Suggested next pick: A.9 profiling tool (S/L/diagnostic, prerequisite
for further perf work) → choose between P1 #5 V2 / P1 #5b V3 / GBA bjit
P0.7b regression.

**Known characteristic — BIOS LLE bjit slower**: full detail in
`MD/performance/202605032030-bios-loop100-bench.md`. The detector
splits unconditional `B`/`BL`/`BX` into block boundaries; on the BIOS
path, the loop100 ROM averages **1.0-1.1 instr per block** → block-fn
call overhead can't amortise. HLE path also averages ~1.0 per block,
but cache hit rate is high + compile volume is small (518 unique blocks
vs BIOS 1675), so attempted block-JIT advantage holds. Followup: have
the detector also follow into unconditional B targets for continuous
compilation (expecting average block to climb to 5-10 instr, bjit
should beat per-instr by 50%+).

**Status of remaining items** (each unchecked one in B-G is tagged):
- **[paused-blocked]** = needs a prerequisite (mostly blocked on A.5 SMC
  or A.7 block linking)
- **[skipped]** = ROI evaluated as too low / already covered by another
  step / LLVM does it itself
- **[ ]** = doable, not yet done (**2026-05-04 update**: A.5 SMC done,
  A.8 state→reg caching done (overlaps with P1 #5), E.c IR-level region
  check done (`15f939f` LR35902 WRAM/HRAM inline write). Remaining:
  A.7 block linking, A.9 profiling, G.a/b host-runtime tweaks,
  H.b-i variants)

**Tried but deferred**:
- **C.b alloca-based shadow lazy flag** — two retries failed: first
  (2026-05-03 morning) MCJIT didn't run mem2reg; second (2026-05-03
  after H.a) on the recovery branch passed T1 but T2 8-combo screenshot
  failed 5/8 (BIOS LLE bjit hit a stale shadow → CPSR.T mismatch →
  undecodable). Root cause: shadow alloca init load is in the entry
  block, but after MSR-induced raw CPSR write, ReinitShadowFromReal
  crosses cond-skip paths and mem2reg PHI computes incorrectly.
  **Per main note's own admission, the gain is +0.5-0.6%**, with most of
  the same work already taken by GVN+DSE. Deferred until a future perf
  measurement confirms ReadFlag/SetFlag is on the hot path — at which
  point we redesign with explicit per-BB PHI rather than alloca. Detail
  in `MD/performance/202605031930-Cb-lazy-flag-deferred.md`.

**Quick wins on dispatcher / mem-bus / bus inline are saturated.**
Pushing Phase 7 further now requires either architectural changes
(A.7/A.8 / detector continuous-compilation across B) or LLVM passes
pipeline work (H.b/c).

#### A. Block-level JIT (core, textbook path)

> **Whole section is one unit**: items in A all depend on each other,
> can't be done individually; it's a 1-2 week architectural change
> project that significantly rewrites dispatcher + DecoderTable +
> JsonCpu/CpuExecutor. Theoretical gain 8-13× over current (per
> starting baseline note §4). **Currently 0/9, whole group untouched.**

- [x] **A.1 Block detector** (done 2026-05-03) — new files
  `src/AprCpu.Core/Runtime/Block.cs` (Block + DecodedBlockInstruction +
  BlockEndReason enum) + `src/AprCpu.Core/Runtime/BlockDetector.cs`
  (walks PC, queries decoder, stops at boundary). Boundary conditions:
  `writes_pc: "always"` (B/BL/BX/SWI/UND/Coproc) /
  `switches_instruction_set` / `changes_mode` / undecodable / 64-instr
  cap. `conditional_via_rd` is **not** a boundary (too pessimistic,
  would split every ALU op). 4 new unit tests green, 349/349 full
  suite.
- [x] **A.2 Block-level IR generation** (done 2026-05-03) — new file
  `src/AprCpu.Core/IR/BlockFunctionBuilder.cs`: 4 internal BBs per
  instruction (pre/exec/post/advance) — pre defaults R15+offset/clear
  PcWritten/cond gate; exec runs spec steps; post checks PcWritten;
  advance writes PC=pc+size and jumps to next instruction or
  block_exit; block_exit drains shadows + ret void. Instruction word
  becomes a baked-in const. EmitContext gains
  `BeginInstruction(format, def, word)` to switch per-instruction
  state. SpecCompiler CompileResult exposes EmitterRegistry/
  ResolverRegistry/Layout to downstream consumers (block builder +
  future code cache). 2 new unit tests (3 MOVs straight-line + cond
  gate fail/pass), 351/351 green.
- [x] **A.3 LLVM JIT execution engine upgraded to ORC LLJIT** (done
  2026-05-02) — `src/AprCpu.Core/Runtime/HostRuntime.cs` swaps MCJIT
  for ORC LLJIT (`OrcCreateLLJIT` / `OrcLLJITAddLLVMIRModule` /
  `OrcLLJITLookup`). Extern binding keeps the original
  inttoptr-globals pattern (engine-agnostic); no-jump-tables / nounwind
  attribute also retained — both still correct under ORC. Added
  `HostRuntime.AddModule(LLVMModuleRef)` paving the way for A.4
  cache-miss path: post-Compile, fresh modules can still feed into
  LLJIT. target data now comes from `OrcLLJITGetDataLayoutStr`; state
  struct sizing unchanged.
  **Validation**: 351/351 unit tests + Blargg cpu_instrs 11/11 green;
  perf vs MCJIT within ±2% noise (GBA arm 8.49→8.55, thumb 8.51→8.55,
  GB JIT 6.52→6.64) — pure infra swap, no perf regression.
- [x] **A.4 Code cache (hashmap PC → fn pointer + LRU eviction)**
  (done 2026-05-02) — new file `src/AprCpu.Core/Runtime/BlockCache.cs`:
  Dictionary&lt;uint, LinkedListNode&lt;Entry&gt;&gt; + LinkedList LRU
  pattern, capacity-bound (default 4096). Value type changed to
  `CachedBlock` struct holding `(Fn, InstructionCount)` for cycle
  accounting. HostRuntime `BindExtern` now records (name → addr) into a
  dict, and `AddModule` automatically replays all known bindings into
  the new module — so block modules transparently inherit the initial
  module's memory_read_* / bank_swap etc. trampolines. 8 new unit tests
  (round-trip / miss / capacity / MRU promotion / invalidate / clear /
  ctor guard).
- [x] **A.5 SMC detection + invalidation** (done 2026-05-04, V1+V2) —
  V1 (`8ce66ac`) per-byte coverage counter + bus-extern path notify;
  V2 (`6c04422`) IR-level inline notify (env `APR_SMC_INLINE_NOTIFY`
  gated) + precise per-instr coverage (always-on). Detail in
  `MD/design/12-gb-block-jit-roadmap.md` P1 #5b table. Followup: V3
  deferred-invalidation pattern fixes cycle drift.
- [x] **A.6 Indirect branch dispatch + CpuExecutor block-JIT
  integration** (done 2026-05-02) — `CpuExecutor` adds
  `EnableBlockJit(compileResult)` + internal `StepBlock()` +
  `CompileBlockAtPc()`: cache hit jumps directly to block fn; on miss,
  `BlockDetector.Detect` → `BlockFunctionBuilder.Build` on a fresh
  `LLVMModuleRef` → `HostRuntime.AddModule` (replay externs +
  RunPasses + JIT add) → `GetFunctionPointer` → into cache → call.
  `LastStepInstructionCount` reports block size,
  `GbaSystemRunner.RunCycles` uses it to scale
  `Scheduler.Tick(cyclesPerInstr × N)` for cycle accounting. apr-gba
  gains a `--block-jit` flag. The "indirect branch finds next block"
  step degenerates into "block fn exits → outer loop reads PC → next
  iteration cache lookup", equivalent to dispatcher.
  **Validation**: 360/360 unit tests + Blargg cpu_instrs 11/11 green
  (per-instr path didn't regress).
  **Perf (loop100, 1200 frames)**: GBA arm 8.55 → **85.39 MIPS (10.0×
  speedup)**, GBA thumb 8.55 → **85.61 MIPS (10.0× speedup)**,
  real-time 20× faster. (These are A.6's initial numbers; after A.6.1
  + Phase 1a/1b + H.a-instcombine fix, HLE bjit settles at ~10-11 MIPS
  and BIOS bjit at ~5-6 MIPS — see Progress Snapshot above.)
  **Limitation**: menu / interactive ROMs (e.g. armwrestler) running
  headless will execute garbage memory in the menu loop; block-JIT
  compiles tons of 64-instr blocks of garbage and appears to hang —
  not a correctness bug, just ROM behaviour (per-instr also runs
  garbage but slowly enough not to notice). Followup: PC-out-of-region
  fallback to per-instr Step.
- [x] **A.6.1 BIOS-LLE Strategy 2 patch sub-phase** (done 2026-05-03,
  13 commits) — when A.6 shipped, HLE ran jsmolka, but the BIOS LLE
  path crashed under block-JIT with CPSR.T / PC corruption /
  undecodable instruction issues. Root cause: PC handling inside the
  block IR was inconsistent with the per-instr executor's "pre-set
  R15 = pc + pc_offset" assumption. Switched to **Strategy 2**: block
  fn doesn't pre-set PC, doesn't advance PC at the tail; pipeline-PC
  reads are resolved at emit time into baked-in constants
  (`bi.Pc + offset`); only real branches write GPR[15]. Cleanup
  follow-ups:
    - `read_reg(15)` after PC-write reverts to memory load (commit
      260cbb0)
    - `block_store STM` containing R15 uses Strategy 2 constant
      (0fa2153)
    - `IfStep` constant-cond fold reduces dead-BB IR (1a9b908)
    - `OperandResolvers` stale-PC reads patched (3fa5b17)
    - LDM with PC in rlist must `MarkPcWritten` (ea7f1c8)
    - `RaiseException` emitter Strategy 2 awareness (9e7a77c)
    - `RestoreCpsrFromSpsr` PHI alignment patched
  **Validation**: 8-combo screenshot matrix (arm/thumb × HLE/BIOS ×
  pi/bjit) all produce the canonical "All tests passed" md5
  (`7e829e9e837418c0f48c038341440bcb`).
- [x] **Phase 1a — predictive cycle downcounting in block-JIT IR**
  (2026-05-03, commit 738c90e) — block fn now uses an "entry computes
  budget, decrement per-instruction, on hitting 0 exit early + write
  next-PC + PcWritten=1" pattern, letting the caller
  (`CpuExecutor.StepBlock`) compute actual cycles consumed from a
  `cycles_left` snapshot diff, instead of the caller passing
  instruction count and multiplying.
- [x] **Phase 1b — MMIO catch-up callback + double-tick fix**
  (2026-05-03, commit 3252165 + 8290cb1) — bus invokes `OnMmioRead`/
  `OnMmioWrite` callbacks on MMIO writes; the scheduler does a
  catch-up tick to sync external hardware (PPU / Timer / IRQ
  delivery). Fixed BIOS LLE path's IRQ-skip-frame MMIO sync re-entry
  guard and per-instr cycle double-counting issues.
- [ ] **A.7 Block linking**: directly patch native call sites
  (relocate); subsequent branches don't exit JIT, saving one dispatch.
  **Why not done**: high complexity native-code patching; ORC LLJIT
  provides stub-rewriting. **Estimate**: 3-4 days + Windows / Linux
  patching mechanisms each need validation.
- [x] **A.8 State→register caching** (V1 done 2026-05-04, overlaps
  with P1 #5) — mechanism in commit `0e1e280` (P1 #5 V1): EmitContext
  gains GprShadowSlots / StatusShadowSlots; block entry alloca + load
  state→shadow, exit drain shadow→state; mem2reg promotes to SSA.
  LR35902 path is always-on (gated GprWidthBits==8); ARM path doesn't
  enable because GepGprDynamic. V1 unconditionally allocs 7 GPR + F +
  SP, costing -4% on small cpu_instrs blocks (entry/exit overhead
  exceeds internal savings); V2 with per-block live-range pending.
  Detail in `MD/design/12-gb-block-jit-roadmap.md` P1 #5.
- [ ] **A.9 Performance profiling tool**: host-side records block
  compile count / execution count / total-time share, cli flag
  `--bench-blocks` dumps a report. **Why not done**: profiling is
  nice-to-have, doesn't affect functionality; only meaningful once
  Group A is fully done. **Estimate**: 1 day.

#### B. IR-level inlining / micro-op fusion (Gemini suggestion #1)

- [x] **OptLevel set to O3** ~~+ extra LLVM passes added (instcombine,
  gvn, simplifycfg, mem2reg, reassociate)~~ — done 2026-05-03.
  perf-neutral (±3%, see
  `MD/performance/202605030025-optlevel-0-to-3.md`). Reason:
  per-instruction functions are too small for LLVM to do much; the
  bottleneck is dispatcher overhead. O3 is kept for future block-JIT
  use; subsequent strategy should skip B.b/c/d and attack A
  (block-JIT) or E (mem-bus fast path) first.
- [skipped — LLVM handles it] **GEP CSE for the same register**:
  read_reg computes the same GEP for the same reg index, so
  recomputing each time looks wasteful. **Why skipped**: LLVM's O3
  pipeline already runs GVN (global value numbering), which CSEs
  identical GEP instructions automatically; hand-rolled alloca
  actively interferes with mem2reg (per the C.b alloca-shadow
  experience). **When to revisit**: dump IR after H.a runs mem2reg +
  GVN to confirm GEPs are actually CSE'd; if not, then consider
  doing it ourselves.
- [skipped — high effort low ROI] **Multi-micro-op step fusion**:
  spec-compiler-side pattern matching, fusing a common sequence like
  "`add` + `update_zero` + `update_h_add` + `set_flag(N)`" into a
  single inlined IR. **Why skipped**:
  (1) the pattern matching itself requires ~300-500 lines of spec
  analyzer;
  (2) C.a/C.b experience shows LLVM's optimisation room on short IR
  sequences is limited; emitted size after fusion is similar;
  (3) Phase 7 overall has saturated at the 8.x MIPS plateau.
  **When to revisit**: after a third-CPU port, if multiple specs
  share common step patterns, this might be worth doing as a
  generalisation.
- [skipped — low ROI] **Add `nuw` / `nsw` hints**: common PC+4 / SP-2
  add/sub could be tagged `nuw` (no unsigned wrap) / `nsw` (no signed
  wrap), letting LLVM simplify branch conditions. **Why skipped**:
  (1) per-instruction functions are too small; there's no loop hoist /
  strength reduction etc. that depends on wrap-flag assumptions;
  (2) ARM7TDMI / LR35902 both have instructions that intentionally
  wrap (e.g. add with carry); mistagging is UB;
  (3) gain estimated at <1%, not worth the audit risk.
  **When to revisit**: after block-JIT (A) lands, blocks containing
  loops would make nuw/nsw start to matter.
- [x] **B.e Cache state-buffer offsets in CpuExecutor** (2026-05-03,
  perf note `MD/performance/202605030054-cache-state-offsets-cpuexecutor.md`)
  — before, every instruction in Step() called `_rt.PcWrittenOffset` /
  `_rt.GprOffset(_pcRegIndex)` cascading into LLVM.OffsetOfElement
  P/Invoke (4-5 times/instr); cache in ctor, use cached
  `int _pcGprOffset` / `_pcWrittenOffset` + AggressiveInlining + private
  ReadPc/WritePc fast path. **GBA arm +21% vs F.y (5.94 → 7.19 MIPS,
  individual run 1.9× real-time)**, GBA thumb flat (noise),
  GB json-llvm +1.7% (JsonCpu already has its own cache, doesn't
  benefit).
- [x] **B.f Permanent pin of state buffer** (2026-05-03, perf note
  `MD/performance/202605030102-permanent-pin-state-buffer.md`) —
  removed `fixed (byte* p = _state) fn(p, ...)` from the hot path,
  changed to a single GCHandle.Alloc(_state, Pinned) in ctor, caching
  byte* for JIT use. **GBA thumb +27% (6.26 → 7.97 MIPS, stable 1.9×
  real-time)**, GBA arm flat (plateau), GB both paths flat (noise).
- [x] **B.g AggressiveInlining for GBA bus methods** (2026-05-03,
  perf note `MD/performance/202605030108-aggressive-inlining-bus-methods.md`)
  — added `[MethodImpl(AggressiveInlining)]` to GbaMemoryBus's Locate
  / ReadWord / ReadHalfword / NotifyExecutingPc /
  HasPendingInterrupt, letting .NET JIT inline the entire fetch path
  into CpuExecutor.Step. **GBA arm +13% (7.13 → 8.05 MIPS, stable
  2.0× real-time, run-to-run noise dropped from ~30% to ~5%)**, GBA
  thumb flat (8.10, plateau). GB path untouched.
- [x] **B.h Scheduler.Tick + DeliverIrqIfPending AggressiveInlining**
  (2026-05-03, perf note `MD/performance/202605030209-scheduler-irq-inline.md`)
  — added inline hints to both methods called per-instruction inside
  GbaSystemRunner.RunCycles. NotifyExecutingPc was already inlined in
  B.g. **GBA arm +0.8% (8.26 → 8.33), GBA thumb +1.8% (8.24 → 8.39)**.
  Noisy but consistent advance.
- [skipped partial — risk vs payoff doesn't add up]
  **NotifyInstructionFetch inlining**: per-instruction call handles
  BIOS open-bus bookkeeping (pc+8 prefetch address calc + BIOS range
  check + conditional store of sticky value). **Why skipped**:
  (1) method body is big (~30 lines); forcing AggressiveInlining
  expands every caller's generated code, possibly increasing
  instruction-cache pressure and slowing things down;
  (2) the PC < BIOS_END early-return takes most of the time; .NET JIT
  can inline the early-return path under PGO automatically;
  (3) GBA arm is stable at 8.3 MIPS; this method's cost is ~< 2 ns/instr,
  inlining benefit hard to measure.
  **When to revisit**: if BIOS LLE bench shows NotifyInstructionFetch
  becoming a hot path (have to profile to know).

#### C. Lazy flag computation (Gemini suggestion #2)

- [x] **C.a Width-correct status flag access** (2026-05-03, perf note
  `MD/performance/202605030135-width-correct-flag-access.md`) —
  CpsrHelpers's SetStatusFlagAt / ReadStatusFlag previously always
  used i32 read/write regardless of status reg's actual width. For
  LR35902 F (i8) that's a 4-byte read straddling adjacent SP/PC,
  preserving other bytes (so no correctness bug) but LLVM can't see
  the aliasing and can't combine successive flag updates. Changed to
  use i8/i16/i32 by WidthBits. **GB json-llvm +2.7% (6.31 → 6.48
  MIPS)**, GBA unaffected (CPSR already i32). Also fixes a potential
  unaligned i32 access issue.
- [paused-deferred — 2026-05-03 retry failed] **C.b Alloca-based
  shadow lazy flag** — the main branch (`18051f6`) implementation was
  done on the morning of 2026-05-03 (+0.5%), but the retry on the
  recovery branch failed: T1 (360 unit tests) passed, but T2 8-combo
  screenshot failed 5/8 (HLE bjit blank screen + 4 BIOS LLE crashes
  "Undecodable instruction at PC=0x..." CPSR.T mismatch). Root-cause
  hypothesis: shadow alloca init load must live in the entry block
  (mem2reg dominance), but ReinitShadowFromReal happens mid-body
  (after raw MSR write). Some control-flow paths jump from entry
  straight to cond-skip-endBlock without going through body, causing
  DrainAllShadows to overwrite the post-raw-write real CPSR with the
  entry-init stale shadow → CPSR.T corrupted → wrong-instruction-set
  decoder.
  **Defer reasons**:
    - gain small (<1% per main's own admission, GVN+DSE already takes
      most)
    - correct implementation needs explicit per-BB PHI design (alloca
      + mem2reg insufficient)
    - recovery branch's IR structure (Phase 1a budget BB + multi-instr
      block) differs from main; main's alloca-shadow design isn't
      directly cherry-pickable
  Detail in `MD/performance/202605031930-Cb-lazy-flag-deferred.md`.
  **When to revisit**: when future perf measurement confirms
  ReadFlag/SetFlag is on the hot path, redesign with per-BB PHI.
- [revised hypothesis] **True ARM CPSR NZCV lazy** — defer compute
  (not C.b's defer-store): originally we expected lazy's main gain to
  be "skip unneeded compute" rather than "merge consecutive writes"
  (C.b already tried). Needs new state slots
  (last_alu_kind/a/b/result) + new ops + cond eval deriving from
  cache. Revised expected gain: was +10-30%, post-C.b retry revised
  down to +5-10% (since GVN/DSE already captured the batch portion).
- [paused-blocked — needs H.a first] **ARM CPSR NZCV lazy** (**true**
  lazy flag — not just batched, deferred compute): currently every ALU
  instruction computes N/Z/C/V and writes CPSR; change to "record
  last-ALU-result + ALU-kind only", and derive on demand when
  conditional execution actually reads a flag. Needs new state slots
  + new ops + invalidation protocol. **Prerequisite**: H.a (LLVM pass
  pipeline runs mem2reg explicitly) — otherwise alloca-shadow won't
  lift to SSA, as C.b confirmed.
- [paused-blocked — same] **LR35902 F register lazy**: INC r's Z/N/H
  shouldn't be written every time; defer until a subsequent
  BIT/CP/JR cc instruction needs them.
- [paused-blocked — large architectural change] **Flag dependency
  tracking**: emitters tag "this op produces flag X / consumes flag Y";
  spec compiler does def-use analysis within a block to skip
  unnecessary flag writes. More aggressive than lazy flag, requires
  large spec schema + emitter API changes.
- [paused-blocked — same as lazy flag] **PcWritten flag → LLVM
  register hint**: same pattern as alloca-shadow; C.b's outcome
  applies — needs H.a first.

#### D. Hot path / Tier compilation (Gemini suggestion #4 supplement)

> **Whole section blocked on Group A (block-level JIT)** — these all
> rest on the premise that "block is the optimisation unit". Without
> block-JIT, there's no block to counter / tier-compile / patch /
> invalidate. Group A is a prerequisite for group D.

- [paused-blocked on A.4] **D.1 Block execution counter**: increment by
  1 each entry, escalate tier past a threshold (e.g. 1000).
  **Prerequisite**: A.4 code cache — without a block object there's
  nowhere to put the counter.
- [paused-blocked on A.2/A.3] **D.2 Cold block O0 compile**: first
  time we see a block, compile fast at O0 (< 1ms) so the ROM gets
  going; only escalate to O2/O3 once it's hot. **Prerequisite**: A.2
  (block-level IR gen) + A.3 (ORC LLJIT supporting per-block opt
  level).
- [paused-blocked on A.7 + A.4] **D.3 Hot block O2/O3 recompile +
  aggressive register caching**: background thread recompiles hot
  blocks; once done, patch the caller fall-through. **Prerequisite**:
  A.7 (block linking enables call-site patching) + A.4 (code cache
  introduces block concept).
- [paused-blocked on A.5 + D.1] **D.4 Tier degradation**: SMC
  invalidation → recompile back to cold tier. **Prerequisite**: A.5
  (SMC detection) + D.1 (counter-reset protocol).
- [paused-blocked on D.1] **D.5 Profile-guided optimisation**:
  counter + branch-direction stats; on hot conditional branches, flip
  to fallthrough (less taken-branch in steady state).
  **Prerequisite**: D.1 (need counter to profile); itself requires
  LLVM module-level rewrite, the most complex of group D.

#### E. Reduce extern bindings / mem-bus fast path (Gemini suggestion #5)

- [x] **E.a Instruction-fetch fast path** (CpuExecutor side)
  (2026-05-03, perf note
  `MD/performance/202605030120-instruction-fetch-fast-path.md`) —
  CpuExecutor's instruction fetch gains a typed cache
  (`bus as GbaMemoryBus`) + cart-ROM range check directly array
  index, skipping bus's interface dispatch + region switch. **GBA arm
  +2.5% (8.05 → 8.25 MIPS)**, GBA thumb flat (noise). Modest gain
  because B.g already inlined bus.ReadWord into the caller, leaving
  little switch overhead.
- [x] **E.b JIT-side data load/store fast path** (extern trampoline
  side) (2026-05-03, perf note
  `MD/performance/202605030125-jit-mem-trampoline-fast-path.md`) —
  MemoryBusBindings adds a typed cache `_currentGba` + 3 fast-path
  helpers for ROM/IWRAM/EWRAM reads. Reads' 3 trampolines get fast
  lanes; writes stay on slow path (IO/Palette/VRAM/OAM writes have
  side effects). **+0.6% noise on loop100** — this ROM is ALU-heavy,
  not mem-heavy; change is correct but mem-heavy bench needed to see
  real gain.
- [x] **E.c Mem-bus region table inline check (IR layer)** (LR35902
  WRAM/HRAM portion done 2026-05-04, commit `15f913f`): inside JIT'd
  code, emit "if addr ∈ WRAM (0xC000-0xDFFF) / HRAM (0xFF80-0xFFFE),
  GEP-store directly; else call sync-flag extern" branches. LR35902
  write path implements WRAM/HRAM only; MMIO/cart-RAM still goes
  through sync extern (side effects). Read paths, ARM/GBA side, cart
  ROM region still pending. Pinned base pointers
  `lr35902_wram_base` / `lr35902_hram_base` bound by JsonCpu.Reset.
  The "more aggressive than E.b" design goal is partially achieved;
  GBA side + read path remain followups.
  - **What changes**: (1) MemoryEmitters.cs IR generation — change
    `call @memory_read_8(addr)` to `if region check then load else
    call`; (2) expose region base addresses (e.g. cart ROM byte[]'s
    pinned address) as LLVM module globals; (3) handle GBA/GB region
    map differences (GBA uses GbaMemoryMap, GB uses 0x0000-0xFFFF
    page table).
  - **Expected gain**: mem-heavy workloads (LDR/STR-heavy ROMs, BIOS
    LLE) significantly faster; loop100 (ALU-heavy) should also be
    +5-10% since instruction fetch goes through this path.
  - **Complexity**: medium. Larger than E.a/E.b but can be incremental
    — start by inlining cart ROM reads, validate, then add IWRAM/EWRAM.
  - **The only group-E item still worth doing**. Recommend attacking
    before H.a (LLVM pass pipeline) — IR-level fast path doesn't
    depend on mem2reg, can be done independently.
- [skipped partial — already covered by E.b] **E.d Sub-page granularity
  fast-path** (GBA WRAM 0x02000000-0x0203FFFF, GB HRAM 0xFF80-0xFFFE,
  cart ROM 0x08000000-0x09FFFFFF, etc. hot regions): trampolines
  short-circuit at page granularity. **Status**: GBA IWRAM
  (0x03000000+) / EWRAM (0x02000000+) / ROM (0x08000000+) three hot
  regions are already fast-pathed in E.b's trampolines. GB HRAM/WRAM
  trampoline-side fast-paths not done — H.d (LR35902 dispatcher / bus
  parity) lists them. **When to do**: H.d; or after E.c (IR layer)
  which naturally covers all sub-pages.
- [skipped — covered by E.a + E.b] **E.e Read-only ROM fast path**
  (cart ROM read directly byte-array index, eliminating extern call):
  instr fetch goes through CpuExecutor.Step's cart-ROM fast lane (E.a),
  data LDR/STR goes through trampoline fast lane (E.b includes ROM).
  **Remaining**: full IR-level inline in E.c — that's where we fully
  eliminate the trampoline call's C# cost.
- [skipped — JIT handles it] **E.f Aligned word access** (4-byte /
  2-byte aligned read/write going through i32/i16 directly rather
  than byte sequences): cross-architecture load/store using i16/i32
  rather than 4× i8. **Why skipped**:
  (1) JIT'd code on aligned addresses already emits `BuildLoad2 i32` /
  `BuildLoad2 i16` directly; emitter isn't splitting into byte
  sequences;
  (2) LLVM backend on x86-64 emits a single MOV/MOVZ for aligned
  access;
  (3) misaligned access on ARMv4T is the rotation rule, not an
  error, and the emitter handles it correctly.
  Nothing extra to do.

#### F. Dispatcher / cycle-accounting simplification

- [x] **F.x InstructionDef-keyed fn pointer cache** (2026-05-03,
  commit `9fcf511` + perf note
  `MD/performance/202605030036-fnptr-cache-by-instruction-def.md`) —
  dispatcher switched to reference identity rather than string-keyed
  cache. **GB json-llvm +82% (2.66 → 4.83 MIPS), GBA Thumb +25%**,
  GBA ARM +2% (noisy).
- [x] **F.y Pre-built DecodedInstruction cache** (2026-05-03, perf
  note `MD/performance/202605030047-prebuilt-decoded-instruction.md`)
  — DecoderTable.Decode now returns a pre-built instance, eliminating
  per-decode `new DecodedInstruction(...)` heap alloc + foreach
  IEnumerator alloc. **GBA arm +55% (3.82 → 5.94), GBA thumb +67%
  (3.75 → 6.27), GB json-llvm +137% (2.66 → 6.30)** — both GBA paths
  cross 1.0× real-time for the first time.
- [skipped — F.x/F.y captured the bulk] **F.b Dispatcher from
  hash-lookup to direct table** (decoded opcode → fn pointer array):
  bypass Dictionary, array-index by opcode bits. **Why skipped**:
  (1) F.x identity-keyed cache turned Dictionary lookup from
  string-hash to ref-equality (≈ 5-10 ns → ≈ 1-2 ns), F.y pre-built
  saved per-decode alloc. Remaining dispatch overhead is almost
  entirely the indirect call itself, not the lookup.
  (2) Doing array-index also requires maintaining opcode (12-bit ARM
  / 10-bit Thumb / 8-bit LR35902) → array index mapping, adding spec
  compiler complexity.
  (3) Estimated saving ~2-3 ns/instr further, <5% gain. **When to
  revisit**: after Group A reshuffles dispatch path, redo at the
  same time.
- [paused-blocked on A.2 + A.4] **F.c Cycle accounting trailing add**:
  accumulate cycles once at block end rather than incrementing per
  instr. **Why blocked**: (1) need a block concept to "accumulate at
  block exit"; (2) GbaSystemRunner's per-instr += 4 is already minimal
  (one add instr, ~1 ns); amortising across blocks doesn't save much
  (~3-5 ns/block assuming 16 instr blocks → ~0.2 ns/instr).
- [paused-blocked on A.2 + A.6] **F.d IRQ check centralised at block
  exit**: only check IRQ pending at block end, eliminating per-instr
  fast-path call. **Why blocked**: same — need block concept.
  **Status**: B.h already inlined per-instr DeliverIrqIfPending
  fast-path into RunCycles' hot loop, leaving cost ~1-2 ns/instr;
  block-batched can drive that to ~0 but requires a block first.

#### H. Missed optimisation directions (added 2026-05-03)

Beyond A-F, taking stock of optimisations discovered while
implementing Phase 7:

**Note**: original Group G (.NET host runtime optimisations) — Native
AOT, UnmanagedCallersOnly trampoline IL emit — has been removed from
Phase 7's main steps and moved to the "non-essential / fallback"
section below. Reason: build-time / .NET-internal micro-optimisations,
they don't affect the framework's JSON-driven core thesis; ROI vs risk
is poor.

- [x] **H.a LLVM pass pipeline tuning** (2026-05-03, perf notes
  `MD/performance/202605031900-Ha-llvm-pass-pipeline-reenabled.md` +
  `MD/performance/202605031940-Ha-instcombine-fix.md`) —
  HostRuntime.Compile, before handing the module to ORC LLJIT,
  explicitly runs (new pass-manager API) `LLVM.RunPasses` with
  `mem2reg,instcombine<no-verify-fixpoint>,gvn,dse,simplifycfg`.
  **Process**: instcombine initially broke 3 BlockFunctionBuilderTests
  (R1=0 expected 2); first shipped a stable 4-pass version
  (mem2reg/gvn/dse/simplifycfg), then later (commit ea08d17) found
  the root cause — module didn't set `target datalayout`, so
  instcombine canonicalised struct GEP into byte GEP using the
  default layout and computed a wrong 4-byte offset (i64 alignment
  handled differently). Fix: reorder Compile() to first build LLJIT,
  fetch datalayout, write it via `LLVM.SetDataLayout` into the
  module, then run pipeline. AddModule (per-block path) follows the
  same flow.
  **Perf**: 4-pass version straight +1-2%; 5-pass (with instcombine)
  direct gain +0.2-1.8% per-instr / ±1% bjit; T2 8-combo screenshot
  all green canonical hash.
  **Indirect value**: was expected to unblock C.b alloca-shadow lazy
  flag, but C.b retry hit a shadow drain edge case and was deferred —
  but instcombine itself is pure IR-level wins, sticking.
- [ ] **H.b Spec-time IR pre-processing** — at SpecCompiler, scan
  every instruction's step sequence for dead-flag-elimination: if an
  ALU instruction's update_nz writes N but no later step in the same
  instruction or any callable cond gate ever reads it, emit nothing.
  Needs def-use analysis on flag bits.
- [ ] **H.c Inline hot opcodes into the dispatcher** — bypass fn
  pointer call; for the top-N most-frequent opcodes (e.g. ARM
  MOV/ADD/LDR/STR/B), inline them directly into a switch case in the
  dispatcher, eliminating per-call indirect-call cost. Top-N comes
  from PGO stats.
- [ ] **H.d LR35902 dispatcher parity with GBA path** — F.x/F.y are
  applied to JsonCpu, but B.e (cache state offsets) / B.f (permanent
  pin) / B.g (AggressiveInlining bus) each have GBA-path and GB-path
  versions. Audit JsonCpu / GbMemoryBus and apply equivalently —
  GB JIT plateaued at 6.4 MIPS but there may still be room.
- [ ] **H.e True cycle-accounting batch** — group F's "Cycle
  accounting trailing add" not done. GbaSystemRunner += 4 every instr;
  could batch += N*4 at block end. Risk: IRQ delivery timing may be
  delayed.
- [ ] **H.f Per-process opcode profiling persistence** — disk-cache
  opcode usage stats; on startup, prefer hot path's load order, JIT
  compile order, code-cache pre-warming.
- [ ] **H.g LLVM IR custom calling convention** — currently JIT'd fns
  use standard cdecl, passing `(state*, ins)`. Could switch to a custom
  cc that passes `state*` and hot regs (e.g. PC/SP) in registers
  rather than stack, making cross-fn calls cheaper. Requires LLVM
  tablegen-level changes, high risk.
- [ ] **H.h SIMD batch state operations** — if there were a mode that
  ran the same instruction N times in batch (no current use case),
  SIMD could help. Not applicable to single-instruction dispatch.
- [ ] **H.i AOT bitcode cache** — serialise spec→IR result to disk
  (`.bc` file); on next startup, load directly, skipping SpecCompiler.
  Helps startup time, not runtime perf. Already listed as fallback #4.

### Non-essential / fallback (doesn't affect Phase 7 done-criterion)

#### G. .NET host runtime optimisations (demoted from Phase 7 main steps)

build-time / .NET-internal micro-optimisations; doesn't affect the
framework's JSON-driven core thesis, and isn't directly related to
"pushing block-JIT to the limit". Phase 7's done-criterion doesn't
require this group; only consider when squeezing the last few %.

- [ ] **G.a Native AOT** — AOT-compile the entire host runtime,
  avoiding .NET tiered JIT's cold-start cost (might also help that
  −15% measurement noise on GB legacy). Requires
  `<PublishAot>true</PublishAot>` + confirming all dynamic code
  (LLVMSharp interop) is AOT-compatible. Medium complexity, pure
  build-time change.
- [ ] **G.b UnmanagedCallersOnly trampolines via IL emit**: directly
  patch entry, avoiding .NET wrapper prologue overhead. Micro
  optimisation, requires .NET IL internals.

#### Fallbacks if a main step's implementation goes badly

If LLVM compilation is too slow causing unacceptable stutter:
- Fallback 1: drop OptLevel to O0 (or O0 for some blocks)
- Fallback 2: tiered compilation (cold blocks O0, hot blocks O2 —
  already in D)
- Fallback 3: switch to .NET `DynamicMethod` /
  `System.Reflection.Emit` writing a lightweight IL JIT, skipping
  LLVM entirely
- Fallback 4: for a handful of hot-loop ROMs, do ahead-of-time `.bc`
  cache (spec → IR → bitcode written to disk, loaded directly next
  time)

### How to run this phase

For each item implemented, run the canonical loop100 bench, write a
new file in `MD/performance/`, file name
`YYYYMMDDHHMM-<strategy>.md`, reference the starting baseline
`MD/performance/202605030002-jit-optimisation-starting-point.md`,
record before/after delta + that strategy's hypothesis. **Don't
batch several items into one run** — you won't be able to tell which
brought the gain.

**Pre-commit QA**: run the appropriate tier based on the change type,
defined in `MD/process/01-commit-qa-workflow.md`:
- T0 = pure docs / comments → no QA
- T1 = refactor / debug helper → 360 unit tests
- T2 = bug fix / new emitter / spec change → T1 + 8-combo screenshot
       matrix
- T3 = hot path / JIT IR / dispatcher / bus change → T1 + T2 + 3-run
       loop100 bench + record to `MD/performance/`
- T4 = large architectural change → T1 + T2 + T3 + full matrix +
       baseline update

T2 fail = no commit; T3 regression > 5% has to find root cause before
deciding ship/revert.

---

## Phase 8: PPU Completion — Done (2026-05-02)

> **Closeout status**: Phase 8 "minimal PPU + headless screenshot" was
> originally scoped as Mode 0 + Mode 3 + screenshot jsmolka pass/fail.
> Actual delivery covers more — Mode 0/1/2/3/4 + full OBJ + BLDCNT
> alpha/brighten/darken + WININ/WINOUT/OBJ Window. BIOS startup logo
> visually equivalent to mGBA. Detail in
> `MD/note/phase5.7-bios-lle-and-ppu-2026-05.md` section 5.
>
> Still not done: Mode 5 (rare), Mosaic (rare).
>
> The PPU is still hand-written, not JSON-ified (per scope decision),
> ~600 lines of C# in `src/AprGba.Cli/Video/GbaPpu.cs`.

---

## Phase 8: Minimal PPU + headless screenshot — original scope (superseded, kept for reference)

> **Scope decision (2026-05)**: two key scope cuts:
>
> 1. **PPU does not go through the JSON spec route** — write a `GbaPpu`
>    class directly in host code (mirroring GB-side `GbPpu`). Reason:
>    PPU is not an instruction stream (CPU is), it's a fixed-function
>    pipeline; JSON description has no cross-device reusability,
>    forcing JSON gives no framework leverage
> 2. **No GUI, no 60fps loop, no real-time playback** — headless CLI
>    only: run N cycles → render framebuffer → write PNG. Whole goal
>    is "be able to verify test ROMs computed correctly via PNG
>    screenshot"

### Tasks (minimal working version)

- [ ] `GbaPpu` class same shape as `GbPpu`: takes a `GbaMemoryBus`,
  renders 240×160 framebuffer based on DISPCNT mode + VRAM/PRAM/OAM
  contents
- [ ] Support at least Mode 3 (direct RGB555 framebuffer) and Mode 0
  (4 tile-based BG layers) — jsmolka test ROMs use Mode 0 + tile-based
  glyphs to print results
- [ ] Sprites probably not needed (depends on whether jsmolka uses them)
- [ ] CLI: `apr-gba --rom=X.gba --cycles=N --screenshot=Y.png`,
  modelled on apr-gb
- [ ] Diff screenshot against mGBA on the same ROM at the same cycles

### Explicitly skipped

- Avalonia / WinForms real-time window
- 60fps main loop
- VBlank-driven screen refresh
- Commercial games / advanced homebrew attempts
- Mode 1/2/4/5, Window, Mosaic, Affine BG, Blend etc. advanced effects
  (jsmolka doesn't use, so don't implement)

### Acceptance

- After jsmolka arm.gba / thumb.gba runs, framebuffer PNG matches
  mGBA's screenshot pixel-for-pixel (or at least visually identical
  — that already accomplishes "visual verification")
- Whole CLI flow is headless like apr-gb: load ROM → run cycles →
  render → save PNG

### Milestone

"Test ROM result is visible as a PNG." GBA-side visual verification
closeout.

---

## Phase 9 and beyond (out of MVP scope)

Possible future directions:
- Full PPU advanced effects (Window, Mosaic, Affine BG mode 1/2,
  Blend) — the parts Phase 8 didn't finish
- APU (audio, 4 channels + DMA-driven sound) — **hand-written, not
  JSON-ified** (same reason as PPU: not an instruction stream)
- Commercial-game compatibility testing and bug fixes
- Open source, docs, community
- AOT-precompiled `.bc` cache (avoid cold-start LLVM compile cost)
- Third-CPU validation (after Phase 4.5 GB, if we want one more
  validation, MIPS R3000 or RISC-V RV32I are candidates; 6502 is too
  simple, doesn't cover what the framework already validates, no
  particular priority)

### Explicitly not planning to do (avoid scope creep)

- **Writing PPU/APU/DMA as JSON spec**: the framework's "data vs
  verbs" split only makes sense for instruction streams; fixed-function
  units have no cross-device reusability, forcing JSON gives no leverage
  (see Phase 8 design decision)
- **Full cycle-accurate timing**: instruction-level catch-up is enough
  to run common games; cycle-perfect can wait for a much later phase
- **Other full systems beyond GBA (NDS, PS1 etc.)**: third-CPU
  validation may happen, but full-system emulators are out of scope

---
