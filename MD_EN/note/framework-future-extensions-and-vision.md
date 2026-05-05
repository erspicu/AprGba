# Framework future extensions + design vision — an advanced challenge map for whoever takes over

> **Status**: vision / handover note (2026-05-05)
> **Source**: synthesized from a long 2026-05 consultation with Gemini
> (discussion logs at
> [`tools/knowledgebase/message/`](/tools/knowledgebase/message/))
> **Scope**: lay out every issue that someone wanting to push this
> framework into broader application areas would care about — from
> "supporting more machines" to "where else can JSON-driven go" to
> "where this whole design sits in the industry".
>
> **Intended audience**: (a) anyone who wants to take over and push
> AprCpu / AprGba forward; (b) anyone who wants to apply the same
> JSON-driven approach to something else; (c) anyone who wants to
> understand the academic / engineering uniqueness of this framework.

---

## 1. Why this doc exists

After the `AprCpu` framework hit its P0 + P1 milestones, it has proven
that "JSON-driven CPU framework + LLVM block-JIT" is a viable path. But
**where else can the framework go? Where is the limit?** The original
author talked through this whole chain of questions privately with
Gemini, and this doc condenses that discussion into an "advanced
challenge map" for whoever takes over.

Relationship to other design docs:
- [`MD_EN/design/12-gb-block-jit-roadmap.md`](/MD_EN/design/12-gb-block-jit-roadmap.md) — the P0/P1 progress that has shipped
- [`MD_EN/design/15-timing-and-framework-design.md`](/MD_EN/design/15-timing-and-framework-design.md) — the timing mechanism already implemented
- [`MD_EN/design/16-emulator-completeness.md`](/MD_EN/design/16-emulator-completeness.md) — what the emulator shell still lacks (**short-term implementation** TODO)
- [`MD_EN/design/17-aprcpu-vs-emulator-timing-boundary.md`](/MD_EN/design/17-aprcpu-vs-emulator-timing-boundary.md) — framework vs emulator responsibility split
- **This doc** — **long-term framework extension directions** (what's needed if you want to extend toward N64 / SS / 3DS / Switch / arcade-class machines)

---

## 2. The framework's current CPU coverage

### 2.1 RISC vs CISC fit

| Category | Representative CPUs | Framework fit | Notes |
|---|---|---|---|
| **RISC** | ARM / MIPS / RISC-V | ✅ Strongest area | Fixed width + regular → JSON description maps naturally; ARM7TDMI already validated |
| **CISC** (simple variable width) | LR35902 / Z80 / 6502 | ✅ Validated | Variable 1-3 byte + prefix sub-decoder pattern shipped; LR35902 runs |
| **CISC** (complex variable width) | x86 / x86-64 | ⚠️ Theoretically possible | 1-15 byte super-variable width + complex prefix system + many implicit side effects; expressible in JSON but would be bloated |
| **Superscalar / OoO** (superscalar / out-of-order execution) | Real high-end CPUs | ❌ Not suitable | The framework follows sequential semantics; OoO simulation is out of scope |

**Key insight**: as long as a CPU's behavior can be decomposed into
"explicit data flow + state changes", the framework can in principle
handle it. Going from RISC to CISC only changes decode complexity; the
data path can be shared.

### 2.2 Bit-width support (8 / 16 / 32 / 64-bit)

**The most important concept**: a "64-bit CPU" refers to **register
width + addressing capability**, not **instruction length**.

| 64-bit architecture | Register width | Instruction length |
|---|---|---|
| AArch64 (ARMv8) | 64-bit | **Fixed 32-bit** |
| MIPS64 (N64 R4300i) | 64-bit | **Fixed 32-bit** |
| RISC-V RV64G | 64-bit | **Fixed 32-bit** (compressed instructions 16-bit) |
| x86-64 | 64-bit | 1-15 byte variable width |

**Implications for the framework**:
- `BlockDetector` / instruction word fetch path — **no changes needed at all** (still grabs 4 bytes per decode)
- `i32` operations inside `BlockFunctionBuilder` — **need to be widened to i64**
- Register definitions (JSON) — **need to support i64 + register sub-slicing** (e.g. ARMv8 `X0`/`W0` overlap)
- SoftMMU — pointer width pulled from 32-bit up to 64-bit
- Load / store instructions — **add 64-bit Load/Store paths**

**Good news**: 32→64 extension is leaner than expected. The trickiest
trap is **sign extension (sext)** — a MIPS `LW` instruction loading
32-bit into a 64-bit register automatically sign-extends, so the JSON
must be able to describe `Sext` / `Zext` semantics.

---

## 3. Framework extension roadmap — toward more machines

The four phases below are a condensed version of the conclusions from
the Gemini discussion; each phase corresponds to specific machine
requirements. **This is an advanced challenge for whoever takes over,
not a sub-task of the existing P2/P3.**

### 3.1 Phase 1: floating-point arithmetic (FPU)

**🎯 Target machines**: N64 (CP1), PSP (VFPU), 3DS (VFPv2), Switch, PS1
(no FPU but the GTE is a same-class extension)

**Required framework extensions**:

| Layer | Change |
|---|---|
| **LLVM IR emit** | Introduce `float` (32-bit) / `double` (64-bit) types; emit `fadd` / `fsub` / `fmul` / `fdiv` |
| **JSON schema** | Distinguish `i32` vs `f32` / `f64` in register declarations; instruction descriptions must support floating-point operations |
| **Rounding mode** | JSON describes "round toward zero" / "round to even" etc.; corresponds to ARM `FPSCR` / MIPS `FCSR` and similar control registers |
| **Exception handling** | NaN / Infinity behavior; whether divide-by-zero traps or writes a special value |

**Key design principle**: an FPU is usually a coprocessor (CP1 in MIPS,
VFP in ARM); handling it via the §4 coprocessor pattern is natural.

### 3.2 Phase 2: SIMD / multimedia instructions

**🎯 Target machines**: 3DS (ARMv6 SIMD), PSP (VFPU 128-bit), PSV (NEON),
modern consoles

**Required framework extensions**:

| Layer | Change |
|---|---|
| **LLVM IR emit** | Use LLVM Vector Types: `<4 x i8>` / `<2 x i32>` / `<4 x f32>` etc. |
| **JSON schema** | Add `VectorSplit` / `LaneExtract` semantics — describe "treat a 32-bit register as four 8-bit operands" |
| **Saturating arithmetic** | `255 + 1 = 255` (no overflow wraparound); corresponds to LLVM intrinsics like `llvm.sadd.sat` |
| **Special register layout** | The PSP VFPU's "matrix-style" register organization is highly unusual; the JSON may need a new register topology description |

**Challenge**: PSP VFPU is a framework-level stress test — it is almost
"a special-ops processor wearing a MIPS shell". A framework that
survives VFPU has likely been generalized very thoroughly.

### 3.3 Phase 3: multi-core + synchronization

**🎯 Target machines**: Sega Saturn (dual SH-2), 3DS (dual/quad-core ARM11
MPCore), N64 (CPU + RCP in parallel), PSV (quad-core Cortex-A9), PS2
(CPU + dual VU)

**This is the framework's** biggest architectural challenge**. The GBA
has only a single CPU; with multi-core, two CPUs read and write the same
memory simultaneously, and timing/sync issues explode.

**Required framework extensions**:

| Layer | Change |
|---|---|
| **LLVM IR emit** | Introduce `atomicrmw` / `cmpxchg` / Memory Fence (`fence acquire/release`) |
| **JSON schema** | Describe `ExclusiveLoad` / `ExclusiveStore` (LL/SC, ARM LDREX/STREX) — hardware mutex semantics |
| **Executor architecture** | Multi-thread mode; a separate block-JIT instance per CPU + shared memory |
| **Scheduler** | Global clock manager (based on `BaseClock` + per-CPU `ClockDivider`) |
| **Sync method choice** | `Lockstep` (most precise, slowest) vs `TimeSlice` (industry mainstream) vs `Catch-up` (fastest, hardest) — see §3.5 |

**Industry analog**: mGBA / Dolphin / PCSX2 mostly use `TimeSlice`
(quantum 64 cycles).

### 3.4 Phase 4: pipeline quirks (delay slots etc.)

**🎯 Target machines**: Sega Saturn (SH-2), N64 (MIPS R4300i), PSP
(Allegrex), PS1 (R3000A)

The "delay slot" design of 90s RISC CPUs: the instruction immediately
after a branch **must execute before the branch actually takes effect**.

**Required framework extensions**:

| Layer | Change |
|---|---|
| **JSON schema** | Add `HasDelaySlot: true` markers to instructions |
| **`BlockDetector`** | When a delay-slot instruction is detected — **don't end the block immediately; pull in the instr at PC+4** and place it before the branch action |
| **Block IR layout** | The delay-slot instr's effect must occur before the branch effect |

**This is compile-time logic**, the same kind of abstraction as P1 #6
cross-jump follow (both are detector behaviors); extension implementation
is not difficult.

### 3.5 Phase 5: multi-core sync strategy — pick one of three

A design problem extending out of Phase 3. Three mainstream solutions:

| Solution | Precision | Performance | Engineering difficulty | Use case |
|---|---|---|---|---|
| **A. Global Lockstep** | 100% | Terrible (destroys block-JIT gains) | Low | Not suitable for commercial games; research/validation only |
| **B. Time-Slicing (Quantum)** | 95% | Good | Medium | mGBA / PCSX2 / Dolphin etc. — 90% of industry choices |
| **C. Event-driven Catch-up** | 99% | Excellent | High | Our framework's GBA path already partially adopts this (MMIO catch-up) |

**Catch-up pattern (what we already have, extending to multi-core)**:
1. The CPU runs 1000 cycles in one go
2. Custom processors only record `LocalTime`
3. Only when the CPU tries to read custom-processor state / shared memory does it forcibly pause the CPU, with the custom processor catching up to the CPU's current cycle count
4. Watchpoints have to be sprinkled across the SoftMMU

The foundation for the catch-up pattern is already in place
([`MD_EN/design/15`](/MD_EN/design/15-timing-and-framework-design.md)
Pattern B); extending to multi-core means making the watchpoint system
more dense.

---

## 4. Co-processor architecture — generalized design

### 4.1 Two essential coprocessor types

| Type | Trigger | Examples | Framework correspondence |
|---|---|---|---|
| **Instruction-level coprocessor** | Activates only when the CPU executes a specific opcode | PS1 GTE (`COP2`), ARM VFP, MIPS CP1 FPU | Use the instruction-emit handoff mechanism |
| **Memory-mapped coprocessor** | Triggered when the CPU writes to specific MMIO addresses | N64 RCP, GBA PPU, all DMA controllers | Use MMIO catch-up callback |

### 4.2 Instruction-level coprocessor — `ICoprocessor` interface

**Key design principle**: when the main CPU JSON parser sees a `COP`
opcode prefix, **hand off to the coprocessor module**.

```csharp
public interface ICoprocessor {
    void EmitInstruction(EmitContext ctx, uint opcode);
    LLVMValueRef ReadRegister(int regIndex);
    void WriteRegister(int regIndex, LLVMValueRef value);
}
```

Wiring (plug-in architecture):
```csharp
// PS1 emulator setup
cpu.AttachCoprocessor(0, new MipsCP0_PS1_Variant());
cpu.AttachCoprocessor(2, new SonyGTE_Compiler());

// Pure stock arcade setup
cpu.AttachCoprocessor(0, new MipsCP0_Standard());
// CP2 not attached — a COP2 instruction triggers an invalid instruction exception
```

**Design philosophy**: **composition over inheritance**. Each coprocessor
is a plugin; the framework core neither knows nor cares what a GTE is.

### 4.3 Synchronous vs asynchronous coprocessors

A finer distinction: does the coprocessor run step-by-step in sync with
the CPU, or does it run autonomously in parallel?

| Type | Behavior after trigger | Examples | Framework correspondence |
|---|---|---|---|
| **Synchronous** | CPU waits for the coprocessor to finish before continuing | PS1 GTE | Inline IR directly / call C# function |
| **Asynchronous / autonomous** | The coprocessor has its own PC, runs on its own; the CPU keeps running | PS2 VU, Sega Saturn dual SH-2, N64 RCP | Requires the multi-core sync strategy in §3.5 |

**Why these two are worlds apart**:
- Synchronous: the framework's existing sync micro-op already covers it
- Asynchronous: **enters the territory of distributed systems**, the
  deepest water in the framework

---

## 5. Machine-level definition

### 5.1 Why do this?

Right now the JSON spec is "CPU-centric". To support multi-CPU systems
(Sega Saturn dual SH-2, PS1 CPU + GTE, NDS dual ARM), the abstraction
layer needs to be **lifted from the CPU up to the motherboard**.

In other words: **the JSON should be able to describe "what chips make
up this system, how the chips are wired together, and how the clock is
distributed"**, not just "what this CPU's instruction set is".

### 5.2 `MachineDef` schema proposal

```json
{
  "MachineName": "Hypothetical_DualCore_System",
  "BaseClock": 33513982,                   // Master clock (Hz)

  "Components": {
    "MainCPU": {
      "Type": "CPU",
      "Architecture": "ARM7TDMI.json",     // Reference an existing CPU spec
      "ClockDivider": 2,                   // Effective freq = BaseClock / 2
      "EntryPoint": "0x08000000"
    },
    "AudioCPU": {
      "Type": "CPU",
      "Architecture": "Z80.json",
      "ClockDivider": 8,                   // Effective freq = BaseClock / 8
      "EntryPoint": "0x00000000"
    },
    "SharedRAM": {
      "Type": "Memory",
      "Size": "256KB"
    }
  },

  "Topology": {
    "MemoryMaps": [
      { "Source": "MainCPU",  "AddressRange": ["0x02000000", "0x0203FFFF"], "Target": "SharedRAM", "Offset": 0 },
      { "Source": "AudioCPU", "AddressRange": ["0xC000",     "0xFFFF"],     "Target": "SharedRAM", "Offset": 0 }
    ],
    "InterruptWiring": [
      { "Trigger": "MemoryWrite", "Address": "0x0203FFF0", "Target": "AudioCPU", "Signal": "IRQ" }
    ]
  },

  "Scheduler": {
    "SyncMethod": "TimeSlice",             // "Lockstep" | "TimeSlice" | "Catchup"
    "QuantumCycles": 64
  }
}
```

### 5.3 Key design concepts

#### A. BaseClock + ClockDivider replaces cycle conversion

**Don't** convert "CPU A cycle count" into "CPU B cycle count" —
floating-point error accumulates. **Use BaseClock as the common time
unit**, and let each CPU compute its own cycles via its divider.

#### B. Interrupt wiring is the software version of the real circuit diagram

In hardware, an IRQ is "a physical pin connected to another CPU". The
JSON describes this causal relationship and the framework auto-registers
the corresponding callback.

#### C. SyncMethod is decided per-machine

Different systems need different levels of sync strictness (GBA loose,
Saturn strict). Write the sync method into the machine spec rather than
hardcoding it in the framework — **the framework is a mechanism, the
machine spec is a policy**.

### 5.4 Industry comparison — MAME's `Machine Driver`

MAME's `MACHINE_CONFIG` macro is exactly this concept, but expressed
through C++ macros. Our `MachineDef` is the pure-data (JSON) version of
the same idea — better readability, better machine-processability.

---

## 6. The boundary of JSON-ification — what should and shouldn't be JSON

### 6.1 Three-color zoning

#### 🟢 Definitely belongs in JSON (high ROI, framework core)

Characteristics: **regular logic + discrete state + static topology**

| Mechanism | Reason |
|---|---|
| **CPU instruction-set decode + micro-ops** | Already implemented — opcode mask / register / ALU are all pattern matching |
| **Memory map** | Motherboard wiring is fixed; start / end / attribute are config values |
| **Interrupt routing** | Essentially hardware wiring; source → target one-to-one mapping |
| **MMIO register bit fields** | Like GBA `DISPCNT` where bits 0-2 are BG mode and bit 8 is BG0 enable — entirely discrete description |

#### 🟡 Hybrid zone (medium ROI, parameters in JSON, implementation in C#)

Characteristics: **fixed behavior but with loops or counter logic**

| Mechanism | Boundary split |
|---|---|
| **Hardware Timer** | JSON: count / mount address / default prescaler; counter loop hardcoded in C# |
| **DMA controller** | JSON: number of channels / trigger conditions; data move loop uses `Buffer.BlockCopy` |
| **Simple MMIO registers** | JSON: address / permissions; complex side effects in C# |

#### 🔴 Definitely should NOT be JSON (low ROI, leave JSON alone)

Characteristics: **continuous signal processing + high-frequency loops + algorithmic**

| Mechanism | Reason |
|---|---|
| **PPU / GPU** | Algorithms — OAM fetch, Z-buffer, alpha blending, affine transforms, etc. Putting them in JSON would become "an extremely hard-to-use invented language" |
| **APU / sound** | DSP math — duty cycle, envelope, sweep, 44.1kHz PCM synthesis |
| **Timing scheduler** | Emulator heartbeat, millions of loops per second; C# micro-optimization is essential |

### 6.2 The "JSON as blueprint, C# as implementation" architectural metaphor

| Building element | Framework correspondence |
|---|---|
| Architectural blueprints, plumbing layout | JSON spec |
| Electrical system (auto-generated) | LLVM JIT + CPU core |
| AC, audio (owner's choice) | Hand-coded C# PPU / APU |

**Rule**: keep JSON guarded along the line of "**bus + ISA**". The
algorithms for PPU / APU / scheduler should be implemented by the
developer through standard C# interfaces (`IPpu` / `IApu`).

---

## 7. Industry comparison + uniqueness inventory

### 7.1 Similar projects (predecessors)

| Project | Similarity | Difference |
|---|---|---|
| **MAME** | System-level topology definition (Machine Driver) | C++ macros, hardcoded, not data-driven; uses an interpreter, no JIT |
| **QEMU TCG / Unicorn** | IR → host machine code (same LLVM IR concept) | Instruction semantics are buried deep in the C codebase; hard to extract; no JSON external definition |
| **Ghidra SLEIGH** | Uses an ADL to describe CPUs to tools | Designed for static analysis, not dynamic execution |
| **ArchC (academic)** | Auto-generates simulator from an ADL | Generates a slow cycle-accurate interpreter, not an LLVM JIT |

### 7.2 Our framework's "unique position"

A complete stack: **JSON semantics (data-driven) + LLVM block-JIT + C#
managed runtime**. This combination is **rare** in open-source / industry.

### 7.3 Three unique contributions

#### Contribution 1: hardware semantics ↔ execution engine fully decoupled

Traditional emulators require the developer to be **simultaneously**
expert in:
- The target CPU's hardware details
- A JIT compiler's machine-code emission

We split these two:
- **Hardware experts** → write precise JSON semantics (reading the
  datasheet is enough)
- **Compiler experts** → optimize the JSON → LLVM IR engine

This lowers the barrier to entry for high-performance cross-platform
emulator development.

#### Contribution 2: a "living digital preservation" specification

Traditional emulator C/C++ code rots — the OS APIs it depends on become
obsolete, compiler upgrades break it.

The JSON semantic file is a plain-text specification **readable by both
humans and machines**. Even if C# is retired and LLVM has a generation
shift, **future people can write a new parser and revive the old
machine**. This is a genuine digital-heritage-grade contribution to
retro hardware preservation.

#### Contribution 3: extreme-performance validation in the C# ecosystem

Many people feel C# has GC + managed memory overhead and isn't suited
for microsecond-level synchronous system simulation.

This project proves: **C# + LLVM + unmanaged pointers + GCHandle.Pinned
+ IR-level extern bind** can achieve "near-C/C++ performance". A
convincing case study for high-performance practice in high-level
languages.

---

## 8. Design concepts and techniques — the essence extracted for posterity

Design concepts distilled from these discussions, reusable in any
data-driven framework:

### 8.1 Decode logic and data width fully decoupled

"64-bit CPU" ≠ "64-bit instructions". Our instruction word fetch path
**didn't change a single line** going from 32→64; only the LLVM IR
emit type changed.

**Generalization lesson**: a data-driven framework must describe
"**structural schema**" (instruction format) separately from
"**width parameters**" (operator width).

### 8.2 Plugin / composition over inheritance

PS1 = standard MIPS + Sony GTE plugin; arcade = standard MIPS. Both
share 95% of the framework, the only difference is the plugin.

**Generalization lesson**: leave well-defined plugin slots in the
framework (such as `ICoprocessor` / `IMemoryMappedDevice`), letting
client code inject new behavior by implementing the interface; **don't
extend the framework by inheriting framework classes**.

### 8.3 Mechanism vs Policy split

The framework provides mechanism (sync primitives, IRQ delivery flow);
the spec provides policy (whether to use lockstep or timeslice, how
large a quantum is).

**Generalization lesson**: don't hardcode policy decisions in the
framework — expose them as spec fields and let machine-level config
decide.

### 8.4 Catch-up pattern (lazy synchronization)

"Synchronize when observed" beats "synchronize every cycle".

`bus.WriteByte(MMIO)` triggering PPU catch-up is more than 10x faster
than ticking the PPU every cycle, with the same effect.

**Generalization lesson**: in systems where "the producer is faster
than the consumer", lazy sync + explicit trigger points almost always
beats eager sync.

### 8.5 Digital-archive-grade readable specs

JSON is text — greppable, diffable, machine-processable. Better than
C macros / binary schemas.

**Generalization lesson**: the framework's schema should pick a format
**that future archaeologists can also read**. Data outliving the tools
is the real value.

### 8.6 Spec-driven plus callback escape hatch

The `lengthOracle: Func<byte, int>` callback injects host logic when
the spec can't express it.

**Generalization lesson**: 100% spec-driven is impractical; leave a
**minimal, stateless callback interface** so the host can do the
remaining 5%. Callback design must be minimal, otherwise the spec
degenerates into a stub.

### 8.7 IR-level inline + base pointer pinning

Take "memory bus jumping out to host C# to resolve the region" — a
9-12ns extern call — and internalize it into "IR-level region check +
pinned base + GEP-store", a 1-2ns inline path. **A partial host concept
internalized into IR**.

**Generalization lesson**: identify hot-path extern call patterns →
inline them into IR; pinned memory in a GC language is the key bridge
on this road.

### 8.8 Block-JIT trade-off: observability for throughput

Block-JIT gain ∝ average block length, but average length ∝ the cost of
HW observability.

**Generalization lesson**: on the throughput-vs-observability axis there
is no "right answer" — the spec should be able to describe "how much
observability does this event need" (e.g. sync micro-op), and the
framework handles the rest of the trade-off for you.

---

## 9. For whoever takes over — advanced challenge roadmap

If you take over this framework, here are the advanced challenges
sorted by difficulty:

### Level 1 — finish the existing emulator ([`MD_EN/design/16`](/MD_EN/design/16-emulator-completeness.md) Phase A-C)
- GBA Timers / APU / Joypad / Save
- GB sprites / window / MBC3 / APU / Joypad
- Live display window + audio backend + input wiring
- **Timeline ~3 months. Once done, AprGba/AprGb are playable emulators.**

### Level 2 — add new CPU specs (apply to the same framework)
- **N64 (MIPS R4300i)** — two new challenges: 64-bit + delay slot
- **PS1 (MIPS R3000A + GTE)** — first validation of the coprocessor plug-in pattern
- **NES (6502)** — pure 8-bit CISC, easiest; validates variable-width detector across architectures
- **Timeline ~1-2 months per CPU. Once done, the framework is genuinely "general-purpose".**

### Level 3 — extend the framework to support FPU / SIMD (§3.1 + §3.2)
- LLVM IR float / vector type emission
- Add floating-point semantics to the JSON schema
- Start validation from N64 FPU (CP1)
- **Timeline ~1 month.**

### Level 4 — multi-core synchronization (§3.3 + §3.5)
- Memory fence / atomic IR
- Per-CPU block-JIT instance
- Global scheduler with BaseClock + dividers
- **Timeline ~2-3 months. The largest architectural engineering effort.**

### Level 5 — `MachineDef` schema (§5)
- Lift abstraction from CPU spec to machine spec
- Auto-wire memory map / interrupt / scheduler
- Swap JSON, swap machine
- **Timeline ~1-2 months. Once done, the framework rivals MAME but is 100% data-driven.**

### Level 6 — arcade / commercial-game-scale validation
- Actually use it to emulate PS1 / N64 / Sega Saturn commercial games
- Runs + perf hits 60fps real-time
- **Timeline: open-ended. Hitting this milestone equals reaching the same engineering tier as mGBA / PCSX2.**

---

## 10. Other applications the framework can extend to (beyond emulation)

A JSON-driven CPU model isn't only for emulators. Extension directions:

### 10.1 Educational visualization

Turn JSON spec + a sample instruction into a "step-by-step animated
visualization". For teaching. Spec is machine-readable → visualization
content can be auto-generated.

### 10.2 What-if architecture research

"What if ARM7TDMI had two more GPRs?" "What if LR35902's ALU had one
more flag?" Edit the JSON, run benchmarks, see the effect. Academic
research / teaching use.

### 10.3 Cross-architecture binary translator

The JIT engine is already there; chain source CPU spec → target CPU
spec and you have a binary translator. Same concept as Apple Rosetta 2 /
Microsoft x86-on-ARM emulation.

### 10.4 Dynamic taint analysis

Add taint propagation at IR-level; the spec doesn't change, only the IR
transformation pass. For security research.

### 10.5 Formal verification scaffolding

The JSON spec is a "machine-readable definition of CPU behavior" — a
bridging point with theorem provers like Lean / Coq / Z3. Of academic
interest.

### 10.6 Ghidra-style static analysis

Feed the spec to reverse-engineering tools as a disassembler back-end.
Augmenting SLEIGH.

### 10.7 Hardware verification (HDL co-simulation)

Cross-check against RTL (Verilog / VHDL) level RTL simulation — the JSON
spec serves as a "golden reference model". Used for verification during
the chip design phase.

### 10.8 Cycle-accurate retro hardware preservation

Going from datasheet → JSON spec is a **digital archiving** action. A
spec entering a museum / academic archive is equivalent to **preserving
this CPU's behavior forever**. Far longer-lived than emulator C code.

---

## 11. Closing words to whoever takes over

The framework is in a state where it "runs, validates, and extends".
**What's left is a question of imagination and engineering time.**

This doc has written down all the advanced directions discussed with
Gemini; pick whichever interests you most and push it forward; or if
after reading you have a completely different direction in mind, that's
fine too.

**Top recommendation**: first take §9 Level 2 and add a new CPU (N64 or
PS1 are excellent choices), pushing the framework's "generality" claim
from 2 CPUs to 3. This is the most direct milestone for verifying
real-world generality of the framework.

Fuller original conversation logs: [`tools/knowledgebase/message/`](/tools/knowledgebase/message/).
