# AprGba — A JSON-driven CPU simulation framework

> A research project exploring whether an entire CPU emulator can be
> *generated* from a machine-readable specification — and whether the
> generated code can run fast enough to be practical.

**Last updated:** 2026-05-11 (Asia/Taipei)
**License:** [WTFPL v2](LICENSE) — do what the fuck you want to.
**Status:** Active research. **Six CPU variants** running through the
same framework — ARM7TDMI, LR35902, Ricoh 2A03, and the **Intel x86-16
family** (i8086 / i80186 / i80286, with 80286 protected-mode segmentation
and a 4-baseline-check fault model live). Block-JIT path live for all of
them. Memory bus + cycle table + interrupt vectors + access widths +
spec inheritance all spec-driven (i80186 / i80286 land via JSON Merge
Patch on i8086 with **zero** runtime overhead). 894 unit tests passing.

---

## English

### 1. What is this project, really?

The repository is named **AprGba**, and you'll find a Game Boy Advance harness inside. **But GBA is not the goal.** The actual product of this project is **`AprCpu`** — a JSON-driven CPU simulation *framework*. The GBA emulator is the test vehicle that proves the framework can be pushed to a non-trivial, real-world workload (commercial-grade ARM7TDMI emulation with LLVM block-JIT).

Think of it this way:

| Component | Role |
|---|---|
| **`AprCpu`** | The framework. CPU spec loader + decoder generator + IR emitters + LLVM JIT runtime + block detector + cache + page-table dispatch + lockstep diff toolkit + spec inheritance. **This is the core.** |
| **`AprGba`** | One concrete consumer of the framework — full GBA system (ARM7TDMI + Thumb + memory bus + PPU + scheduler). Used to push `AprCpu` to its limits. |
| **`AprGb`** | A second consumer — Game Boy DMG (LR35902 / SM83). Used as a *control case* and to prove the framework genuinely supports a second, different ISA. |
| **`AprNes`** | A third consumer — NES (Ricoh 2A03 / MOS 6502). Adds variable-width 1-3 byte 8-bit ISA with the framework's most extreme declarativity exercise: ~85% of the runtime (memory bus, cycle table, interrupt vectors, region routing) drives off `spec/cpu/2a03/*.json` + `spec/machines/nes-ntsc.json`. |
| **`AprX86`** | A fourth consumer — Intel x86-16 family (i8086 / 8088 / i80186 / 80188 / i80286). Validates spec **inheritance** (`spec/cpu/x86-16/i80286/cpu.json` extends i80186 extends i8086, depth 3). i80286 protected-mode segmentation + 4-check fault model end-to-end demoable. |

### 2. Why does this project exist?

#### The problem

Writing a CPU emulator is a frequently-rediscovered chore. Every new platform — every new homebrew console, every retro-computing project, every "let me try emulating an X" — leads to the same hand-coded dispatcher loop, the same opcode `switch` statement copy-pasted with new bit fields, the same flag-update boilerplate, the same partial-register stalls and pipeline-PC quirks rediscovered the hard way.

There are excellent emulators out there (mGBA, Dolphin, QEMU, FCEUX). But they're each tightly coupled to *their* CPU. Porting an mGBA-quality JIT to a new ISA usually means writing a new emulator.

#### The hypothesis

> **What if the CPU were a JSON file?**

What if the entire ISA — encoding patterns, register file layout, condition codes, micro-op semantics, cycle costs, pipeline behaviour — were declarative data, and the emulator framework could compile that data into a working interpreter *and* a working LLVM JIT?

#### The goals, in priority order

1. **Build a framework that's actually generic.** Not "generic in theory" — generic in the sense that genuinely different CPUs (ARM7TDMI + LR35902 + Ricoh 2A03 + Intel x86-16) compile through the same pipeline with no per-CPU C# code in the emit pipeline.
2. **Take the framework all the way to block-JIT.** Per-instruction interpreters are easy to make generic. The hard part is whether the framework can survive the architectural pressure of LLVM JIT, cycle accounting, IRQ delivery, SMC detection, and pipeline-PC quirks — *while staying spec-driven*.
3. **Validate against real workloads.** Pass Blargg's `cpu_instrs.gb`, jsmolka's ARM/Thumb tests, blargg NES `cpu_test5`, Tom Harte's 8088 SST (1.31M cases). Boot the GBA BIOS via LLE. Render canonical screenshots with cycle-accurate matrix tests.
4. **Stress the framework with spec inheritance.** Adding new CPUs in the same ISA family should cost a JSON diff, not a re-implementation. The Intel x86-16 chain (8086 → 80186 → 80286, with full 80286 protected-mode segmentation + fault model) shipped in this mode and serves as the proof.
5. **Document the design philosophy.** Every trade-off recorded. Every architectural pattern named. Future maintainers — including future-me — should be able to tell *why* a design choice was made, not just *what* the code does.

#### What this project is **not**

- **Not** a competitor to mGBA. mGBA is a polished end-user emulator; we are a research framework.
- **Not** chasing maximum cycle accuracy. We are deliberately at "instruction-grained timing accuracy with sync exits at HW-relevant moments" — enough for commercial ROMs, not enough for cycle-perfect demoscene work.
- **Not** trying to be the fastest emulator. The framework's value is *generality*, not raw speed. (That said: the Intel 8086 block-JIT path runs at 218 MIPS on a tight inner loop — 5.65× faster than a hand-coded interpreter — once Gemini-suggested LLVM CFG superblocks were in place.)

#### Proof of execution — test ROM screenshots

Visual evidence the framework actually runs correctness-grade workloads end-to-end:

##### Game Boy — Blargg `cpu_instrs.gb` (JSON-LLVM block-JIT path)

![Blargg cpu_instrs all 11 sub-tests pass](result/gb/json-llvm/cpu_instrs.png)

Run command: `apr-gb --rom=test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb --cpu=json-llvm --block-jit --frames=10000`. The serial output ends with **"Passed all tests"**. All 11 sub-tests pass through the JSON-driven LR35902 spec compiled to LLVM IR and run via ORC LLJIT block-JIT.

##### Game Boy Advance — jsmolka `arm.gba` and `thumb.gba` (BIOS LLE path)

![jsmolka arm tests pass under real GBA BIOS](result/gba/bios_lle_arm.png)

![jsmolka thumb tests pass under real GBA BIOS](result/gba/bios_lle_thumb.png)

Run command: `apr-gba --rom=test-roms/gba-tests/arm/arm.gba --bios=BIOS/gba_bios.bin --block-jit`. **LLE** = *Low-Level Emulation* — instead of HLE-stubbing the BIOS calls, we execute the actual Nintendo GBA BIOS through our ARM7TDMI emulation. Both ARM-mode and Thumb-mode test groups pass — covering ~5000+ test vectors per mode across every ARM7TDMI instruction class (data-processing, multiply, single/block data transfer, branch, PSR transfer, SWI, mode switches).

##### NES — blargg `cpu_test5/cpu.nes` (JSON-block JIT path, Ricoh 2A03)

![blargg NES cpu_test5 all subtests pass](result/nes/blargg_nes_cpu_test5_cpu_jsonblock.png)

Run command: `apr-nes --rom=test-roms/blargg_nes_cpu_test5/cpu.nes --run --max-cycles=110000000 --backend=json-block --screenshot=...`. The PPU nametable is rendered as a CGA-style PNG. blargg's `cpu_test5/cpu.nes` covers MOS 6502 official + unofficial opcodes through the JSON-driven Ricoh 2A03 spec compiled via SpecCompiler → LLVM IR → ORC LLJIT block-JIT. The "All tests complete" string is the test ROM's own success signal.

##### Intel 8086 — CGA text-mode demos (legacy + json-block backends)

![Intel 8086 mandelbrot ASCII rendered through JSON-driven block-JIT](result/x86-16/mandelbrot-ascii-i8086.png)

![Intel 8086 primes through JSON-driven block-JIT](result/x86-16/primes-i8086.png)

![Intel 8086 fibonacci through JSON-driven block-JIT](result/x86-16/fibonacci-i8086.png)

These are hand-crafted .com binaries running through the Intel 8086 backend (`apr-x86 --rom=... --backend=json-block --variant=i8086`). The CGA text-mode framebuffer (80×25 chars × 16 colors, 8×14 glyphs) is rendered to PNG by a small renderer in the harness; the CPU itself is **fully JSON-driven** — no per-instruction C# code in the emit pipeline.

The mandelbrot demo computes the Mandelbrot set in fixed-point integer math and renders it with ASCII shading — exercises ALU / control flow / nested loops / signed comparison through the framework. All six 8086 demos (hello-cga / primes / fibonacci / mandelbrot / string-copy / factorial) produce **byte-identical PNGs across all three backends** (legacy / json-llvm / json-block), validating end-to-end framework correctness.

##### Intel 80286 — protected-mode fault matrix (5-ROM CLI demo)

The 80286 backend has no integrated CGA renderer (yet); protected-mode segmentation is demoed via a 5-ROM **fault matrix**. Each ROM is a 96-byte hand-crafted `.com` (assembled from NASM source under `test-roms/x86/src/27-pmode-*.asm`), entering protected mode via `LMSW` and then loading a segment register with a deliberately-malformed selector. The 80286 backend's descriptor-fetch + 4-check fault pipeline (P-bit / NULL-SS / DPL / type) catches each violation:

| ROM | Selector → reg | Descriptor | Architectural outcome | Observed |
|---|---|---|---|---|
| `27-pmode-entry.com` | `0x0008 → DS` | P=1, S=1, DPL=0, writable data | OK; `mov bx,[0]` reads DS_BASE=0x100 | `BX=0xF1B8`, no EXC |
| `27-pmode-np.com` | `0x0008 → DS` | **P=0** | `#NP(sel)` per Intel | `EXC vector=0x0B error=0x0008` |
| `27-pmode-null-ss.com` | `0x0000 → SS` | (NULL) | `#GP(0)` per Intel | `EXC vector=0x0D error=0x0000` |
| `27-pmode-dpl-gp.com` | `0x000B → DS` (RPL=3) | DPL=0 | `#GP(sel)`: max(CPL=0,RPL=3) > DPL=0 | `EXC vector=0x0D error=0x0008` |
| `27-pmode-ss-bad-type.com` | `0x0008 → SS` | type=executable code | `#GP(sel)`: SS demands writable data | `EXC vector=0x0D error=0x0008` |

These 6 screenshots + 5 fault-matrix ROMs together demonstrate that the **same `AprCpu` framework**, with **the same `BlockFunctionBuilder` / `EmitContext` / micro-op registry**, compiles and correctly executes:
1. A variable-width 8-bit CPU (LR35902) with prefix-byte sub-decoding
2. ARM-mode 32-bit fixed-width with 16-condition-code dispatch
3. Thumb-mode 16-bit fixed-width with 19 distinct encoding formats
4. A variable-width 8-bit CPU with unofficial opcodes (Ricoh 2A03 / MOS 6502)
5. A 16-bit CISC family (Intel 8086 / 80186 / 80286) with segmented memory, ModR/M, prefix bytes, and **descriptor-based protected-mode segmentation + 4-check fault model**

— without any per-CPU C# code in the emit pipeline. **This is the core claim of the project, and these images + ROMs are the proof.**

### 3. Honest acknowledgement: the `AprGb` legacy interpreter

The Game Boy interpreter under `src/AprGb.Cli/Cpu/LegacyCpu*` is **not** original to this project. It is imported from an earlier hand-coded emulator of mine — see [erspicu/AprGBemu](https://github.com/erspicu/AprGBemu).

Why import it?

1. **Provide a reference oracle.** Lockstep diff against a known-good interpreter is invaluable when developing a JSON-driven path. Every Blargg PASS we celebrate gets cross-checked against the legacy interpreter producing identical state.
2. **Establish a perf baseline.** The legacy interpreter runs cpu_instrs at ~31 MIPS — early on this was faster than our JIT. (For 8086, after Gemini-suggested LLVM CFG superblocks, json-block hits **218 MIPS**, 5.65× over legacy on the bench loop.)
3. **Demonstrate the framework's real value isn't raw speed.** It's *generality*. The same `AprCpu` pipeline that compiles ARM7TDMI also compiles LR35902, Ricoh 2A03, and Intel x86-16 — no architectural hardcoding.

### 4. What's interesting about the framework?

Beyond "JSON in, working emulator out", these are the framework-level designs that took deliberate effort and are documented in [`MD_EN/design/`](MD_EN/design/):

- **Spec inheritance via JSON Merge Patch (RFC 7386).** Within one ISA family, a child spec is a diff over the parent's resolved spec — `spec/cpu/x86-16/i80186/cpu.json` adds 26 instructions on top of i8086 (~330 lines vs ~3000 from scratch); `spec/cpu/x86-16/i80286/cpu.json` adds the system instructions + protected-mode plumbing on top of that. Inheritance is **build/load-time data overlay**: SpecLoader merges the chain once at load time, downstream (SpecCompiler / DecoderTable / runtime) sees no hierarchy at all. Zero runtime overhead — i80186 perf == i8086 perf on shared workloads. See [`MD_EN/design/23-cpu-spec-inheritance.md`](MD_EN/design/23-cpu-spec-inheritance.md).
- **Variable-width detection without spec coupling.** A `lengthOracle` callback turns a 256-entry static table into a per-CPU plug-in. ARM (4-byte fixed), Thumb (2-byte fixed), LR35902 (1-3 byte variable, with 0xCB-prefix sub-decoder), Intel x86 (1-7 byte variable, with prefix bytes / ModR/M / SIB / disp / imm) all share the same `BlockDetector`.
- **Intel 80286 protected-mode segmentation, fully spec-driven.** When `MSW.PE = 1`, the i80286 backend fetches an 8-byte descriptor from the GDT, validates it through 4 baseline checks (P-bit / NULL-SS / DPL/RPL/CPL privilege / segment type), and populates the hidden segment-register cache; subsequent ModR/M memory accesses use `<seg>_BASE` from the cache, not `(visible-selector << 4)`. Validation faults set `EXC_PENDING` / `EXC_VECTOR` / `EXC_ERROR` slots without contaminating the cache. **All of this is in shared `X86_16Emitters` C# helpers gated by `register_file` slot existence** — i8086 / i80186 specs don't declare the cache slots, so the helpers no-op via try/catch and -1 sentinels. See [`MD_EN/design/27-i80286-completion-plan.md`](MD_EN/design/27-i80286-completion-plan.md) and [`MD_EN/performance/202605110200-i80286-pmode-fault-model-complete.md`](MD_EN/performance/202605110200-i80286-pmode-fault-model-complete.md).
- **Generic `defer` micro-op for delayed-effect instructions.** Whether it's LR35902 `EI` (IME=1 after one more instruction), Z80 `STI`, or x86 `STI`, the spec writes `defer { delay: 1, body: [...] }` and an AST pre-pass injects the delayed body as a phantom step. Zero runtime cost — it's compile-time lowered.
- **Generic `sync` micro-op for control-yield to host.** A spec step can declare "after this point, the host might want to deliver an IRQ". The block-JIT emitter turns this into a conditional mid-block `ret void`. Same mechanism services LR35902 MMIO writes, IRQ-relevant memory writes, and (eventually) any new CPU's HW-state-change boundary.
- **Three architectural patterns for timing-accurate block-JIT.** Predictive cycle downcounting (compute-once-deduct-as-you-go), MMIO catch-up callbacks (HW gets ticked at the moment it's observed), and sync exits (block ret-voids when HW state changes). See [`MD_EN/design/15-timing-and-framework-design.md`](MD_EN/design/15-timing-and-framework-design.md).
- **`EmitContext` as a routing layer.** Spec emitters call `ctx.GepGpr(idx)` instead of `Layout.GepGpr(builder, statePtr, idx)`. The context decides whether the access goes to a state-struct GEP or a block-local alloca shadow. Per-instruction mode and block-JIT mode share emitter code.
- **Self-modifying-code detection at framework level.** A per-byte coverage counter is incremented when a block compiles, decremented when it's invalidated. Memory writes do a 1-byte counter check inline; if non-zero, a slow-path notify scans cached blocks and invalidates the matching ones. Generic — any cached + writable-code platform reuses it.
- **Cross-jump follow + LLVM-CFG superblocks.** The detector follows unconditional `JR`/`JP` (and equivalents) into their target. For x86, intra-block back-edges (LOOP / Jcc / JMP rel) are emitted as **LLVM CFG within a single function**: alloca + mem2reg promotes register state across iterations through phi nodes, letting LLVM's loop optimizer collapse / vectorize where possible. This is what took 8086 from 27 → 218 MIPS on the bench loop.
- **Lockstep diff as framework infrastructure.** `apr-gb --diff-bjit=N` runs both backends side-by-side and reports the first divergence. Generalized — `AprCpu.Core/Validation/LockstepDiff.cs` defines an `ISteppableCpu` interface so any CPU implementation can be lockstep-tested against another.
- **Hardware-style screenshot matrix.** GBA test ROMs render through 8 combinations (`arm/thumb` × `HLE/BIOS-boot` × `per-instr/block-JIT`); 8086 demos render through 3 backends × 2 variants (i8086/i80186) × 6 demos. Single canonical SHA256 hash means all combos produced bit-identical output. Regression-proof for any framework change.
- **Spec-driven runtime.** Memory bus dispatch (NES + GBA), interrupt vector addresses, per-(mnemonic, addressing-mode) cycle counts, allowed access widths, and dynamic cycle penalties all read from `spec/`. The 2A03 NES integration drives ~85% of the runtime declaratively.
- **Page-table dispatch.** Both NES (32-byte / 2048 entries / 16 KB) and GBA (16 MB / 256 entries) memory buses use O(1) page-table dispatch built from `spec/machines/*.json` at construction.

### 5. Project layout

```
AprGba/
├── src/
│   ├── AprCpu.Core/        ← THE FRAMEWORK. Spec loader + IR emitters + LLVM JIT
│   │   ├── JsonSpec/       ← spec deserialisation (RegisterFile, EncodingFormat, …)
│   │   │   └── (incl. JsonMergePatch for spec inheritance)
│   │   ├── IR/             ← LLVM IR generation (BlockFunctionBuilder, EmitContext, micro-op emitters)
│   │   └── Runtime/        ← block detector + cache + ORC LLJIT host runtime
│   ├── AprCpu.Compiler/    ← CLI: spec → LLVM IR (used for inspection / smoke tests)
│   ├── AprCpu.Tests/       ← 894 unit tests covering decoder, emitters, block detector, cache, spec inheritance, …
│   ├── AprGba.Cli/         ← GBA harness (ARM7TDMI + Thumb + bus + PPU + scheduler + screenshot)
│   ├── AprGb.Cli/          ← Game Boy harness (LR35902 + bus + PPU; legacy interpreter from AprGBemu)
│   ├── AprNes.Cli/         ← NES harness (Ricoh 2A03 + bus + PPU + Mapper000/001 + screenshot)
│   └── AprX86.Cli/         ← Intel x86-16 harness (i8086/8088/i80186/80188/i80286 + CGA framebuffer)
├── spec/
│   ├── cpu/                ← All CPU specs (with co-located _schema.json)
│   │   ├── _schema.json    ← JSON schema for cpu specs
│   │   ├── arm7tdmi/       ← ARM7TDMI ISA spec (cpu.json + ARM groups + Thumb groups)
│   │   ├── lr35902/        ← LR35902 ISA spec (cpu.json + Main + CB-prefix groups)
│   │   ├── 2a03/           ← Ricoh 2A03 / NES 6502 spec (cpu.json + 7 cc-pattern groups + unofficial)
│   │   └── x86-16/         ← Intel x86-16 family (i8086 → i80186 → i80286 inheritance chain)
│   │       ├── i8086/
│   │       ├── i80186/     ← extends i8086 (depth 2)
│   │       └── i80286/     ← extends i80186 (depth 3); + protected-mode descriptor + fault model
│   └── machines/           ← MachineSpec — memory bus regions / interrupt vectors / allowed_widths per system
│       ├── _schema.json    ← JSON schema for machine specs
│       ├── nes-ntsc.json
│       ├── gba.json
│       └── gb-dmg.json
├── test-roms/              ← Blargg cpu_instrs, jsmolka arm/thumb, blargg NES, Tom Harte 8088 SST, x86 demos
│   └── x86/src/            ← NASM source for protected-mode fault demos (Phase 27b)
├── result/                 ← Canonical screenshots (gb / gba / nes / x86-16)
├── MD/                     ← Traditional Chinese authoring source
├── MD_EN/                  ← English mirror of MD/
├── tools/                  ← Build helpers (jsmolka/blargg/nasm ROM builders), Gemini knowledgebase
├── BIOS/                   ← (not in repo) place gba_bios.bin / gb_bios.bin here for LLE tests
├── ref/                    ← Vendor manuals + datasheets (ARM ARM, GB CPU manual, Intel iAPX 86/88, …)
├── temp/                   ← (gitignored) scratch dir for IR dumps, screenshots, log files
├── etc/                    ← (gitignored) local working notes
├── CLAUDE.md               ← Project rules for AI agents (Claude Code et al.)
└── AprGba.slnx             ← .NET solution file (target framework: net10.0)
```

### 6. Quick start

#### Prerequisites

- **.NET 10 SDK** (target framework `net10.0`).
- **Windows x64.** Linux / macOS untested for now — `libLLVM.runtime.win-x64` is the only RID currently referenced.
- **LLVM 20** is provided via the `libLLVM.runtime.win-x64` NuGet package — no separate install required.
- **NASM 3.x** (only if you want to rebuild the Phase 27b protected-mode `.com` demos from `test-roms/x86/src/*.asm`). On Windows: `winget install NASM.NASM`.

#### Build & test

```sh
dotnet build AprGba.slnx
dotnet test  AprGba.slnx       # 894 tests
```

#### Run the GBA harness

```sh
dotnet run --project src/AprGba.Cli -- \
    --rom=test-roms/gba-tests/arm/arm.gba \
    --bios=BIOS/gba_bios.bin \
    --frames=300 --block-jit \
    --screenshot=temp/arm-out.png
```

#### Run the Game Boy harness

```sh
dotnet run --project src/AprGb.Cli -- \
    --rom="test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb" \
    --cpu=json-llvm --block-jit --frames=10000
```

#### Run the NES harness

```sh
dotnet run --project src/AprNes.Cli -- \
    --rom=test-roms/nes-test/nestest.nes \
    --nestest --backend=json-block

dotnet run --project src/AprNes.Cli -- \
    --rom=test-roms/blargg_nes_cpu_test5/cpu.nes \
    --run --max-cycles=110000000 --backend=json-block \
    --screenshot=temp/blargg-nes.png
```

#### Run the Intel x86-16 harness

```sh
# 8086 mandelbrot demo
dotnet run --project src/AprX86.Cli -- \
    --rom=test-roms/x86/24.5-mandelbrot.com \
    --backend=json-block --variant=i8086 \
    --screenshot=temp/mandelbrot.png

# 80186-only ENTER/LEAVE demo (validates spec inheritance)
dotnet run --project src/AprX86.Cli -- \
    --rom=test-roms/x86/25-enter-leave.com \
    --backend=json-block --variant=i80186

# 80286 protected-mode fault matrix
for r in entry np null-ss dpl-gp ss-bad-type; do
  dotnet run --project src/AprX86.Cli -- \
      --rom=test-roms/x86/27-pmode-$r.com \
      --backend=json-block --variant=i80286
done
```

### 7. How to contribute / take over development

#### Read these in order

1. **[`MD_EN/design/00-overview.md`](MD_EN/design/00-overview.md)** — what this project is at the highest level.
2. **[`MD_EN/design/02-architecture.md`](MD_EN/design/02-architecture.md)** — how the pieces fit.
3. **[`MD_EN/design/12-gb-block-jit-roadmap.md`](MD_EN/design/12-gb-block-jit-roadmap.md)** — the active GB roadmap.
4. **[`MD_EN/design/15-timing-and-framework-design.md`](MD_EN/design/15-timing-and-framework-design.md)** — Timing & framework-genericity synthesis. **Read this before touching any timing code.**
5. **[`MD_EN/design/23-cpu-spec-inheritance.md`](MD_EN/design/23-cpu-spec-inheritance.md)** — the inheritance mechanism (drives everything from i80186 onward).
6. **[`MD_EN/design/27-i80286-completion-plan.md`](MD_EN/design/27-i80286-completion-plan.md)** — protected-mode segmentation + fault model (current frontier).
7. **[`CLAUDE.md`](CLAUDE.md)** — project rules (commit QA workflow, scratch-file conventions, naming).

#### Adding a new CPU

The current architecture supports any ISA expressible as:

- A register file (general-purpose + status registers, optionally banked per mode)
- A set of encoding formats with bit-pattern matching (`mask` / `match`)
- A set of micro-op steps per instruction (declarative semantics: `read_reg`, `add`, `set_flag`, `store`, `defer`, `sync`, …)
- Optionally: a `lengthOracle` callback for variable-width ISAs
- Optionally: a `prefix_to_set` field for prefix-byte sub-decoders
- Optionally: an `extends` / `extends_path` parent for inheritance within an ISA family

Look at `spec/cpu/lr35902/cpu.json` + `spec/cpu/lr35902/groups/*.json` for a complete variable-width example. ARM7TDMI is at `spec/cpu/arm7tdmi/`. Spec inheritance lives at `spec/cpu/x86-16/i80186/cpu.json` (extends i8086).

#### Tools

- **`tools/knowledgebase/gemini_query.py`** — Gemini API consult. One question at a time. Logs to `tools/knowledgebase/message/`.
- **`tools/build_blargg.sh`**, **`tools/build_jsmolka.sh`**, **`tools/build_loop100.sh`** — re-build test ROMs from source.
- **`tools/build_27_pmode_demos.py`** — assemble the 5 protected-mode fault demos via NASM.
- **`tools/verify_x86_matrix.ps1`** / **`tools/verify_x86_variant_matrix.ps1`** — visual regression matrix (T2-tier QA).
- **`tools/bench_x86.ps1`** — 8086 best-of-3 MIPS benchmark.

### 8. Where this could go

- **More CPUs.** Z80 (Master System / GG), 8080 (CP/M), 68000 (Genesis / Neo Geo / early Mac), MIPS R3000 (PS1), MIPS R4300i (N64), 80386 (next x86 family chain) — all expressible in the same JSON model. Variable-width + prefix-decoded + unofficial-opcode ISAs already work (LR35902 0xCB; 2A03 unofficial cc=11; x86 0x0F escape + ModR/M + SIB).
- **Additional execution backends.** The `EmitContext` routing layer means a future AOT compiler, WebAssembly target, or different IR backend can slot in alongside the LLVM JIT.
- **Spec-time IR pre-passes.** Dead-flag elimination, micro-op fusion, hot-opcode inlining — all naturally extend the existing AST pre-pass mechanism.
- **More protected-mode features.** TSS task switching, full LDT (TI=1) descriptor lookup, far-jump CS handling under PE=1, visible-sreg rewind on fault — all additive on top of the `EmitSegCacheUpdate` + `EmitRaiseException` helpers landed in Phase 27b.
- **Beyond emulation.** A JSON-driven CPU model is also a *specification artefact* — usable for: educational visualisations, what-if architectural studies, cross-architecture binary translators, dynamic taint analysis, formal verification scaffolding.

> **Want to push the framework further?** The long synthesis doc
> [`MD_EN/note/framework-future-extensions-and-vision.md`](MD_EN/note/framework-future-extensions-and-vision.md)
> lays out a concrete advanced-challenge roadmap.

### 9. References & acknowledgements

- **Vendor manuals** (in `ref/`) — ARM Architecture Reference Manual, Game Boy CPU manual, Pan Docs, Intel iAPX 86/88, Intel 80286 PRM.
- **Test suites** — Blargg's cpu_instrs, jsmolka's arm/thumb, armwrestler, Tom Harte SingleStepTests 8088_v2.
- **Industry references** — design hints cross-checked against QEMU TCG, FEX-Emu, Dynarmic, mGBA, Dolphin via Gemini consultation logs (`tools/knowledgebase/message/`).
- **Predecessor projects** — [erspicu/AprGBemu](https://github.com/erspicu/AprGBemu) (LR35902 interpreter, source of `AprGb.Cli/Cpu/LegacyCpu.cs`), and the older `Apr86` 8086 emulator (referenced for CGA framebuffer + PA_mem layout).
