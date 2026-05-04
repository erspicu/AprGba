# Performance Baseline — Post Phase 4.5 wrap-up (2026-05-02)

> **SUPERSEDED by `MD/note/loop100-bench-2026-05.md`** — that file is
> the official baseline post-Phase-5.7 wrap-up (after GB scheduler,
> PPU completion, BIOS open-bus, 5 ARM bug fixes and other correctness
> changes), with unified methodology of "running 1200 frames of
> stress-test ROM (loop100)", reproducible.
>
> This file is retained as a historical snapshot of the Phase 4.5 era;
> **the Phase 7 block-JIT comparison baseline should reference numbers
> in `loop100-bench-2026-05.md`**.
>
> ---
>
> Original (Phase 4.5 wrap-up baseline):
>
> Records the baseline numbers after Phase 4.5C wrap-up, before any
> performance optimisation (block-JIT / cross-instruction register
> caching / spec-driven dispatch are all not yet done). Once Phase 7
> block-JIT is finished, this can be used as a comparison reference
> for measuring speedup.

---

## 1. Measurement methodology

### apr-gb (GB / DMG)

```
apr-gb --bench --rom=<rom.gb> [--cycles=N]
```

Runs both LegacyCpu + JsonCpu backends, each wrapping `RunCycles(N)`
in a `Stopwatch`, reporting wall-clock time + accumulated
`InstructionsExecuted`, computing MIPS. Setup time (spec compile +
MCJIT JIT engine) is reported separately and **not counted toward
MIPS**.

### aprcpu (GBA / ARM7TDMI)

```
aprcpu --bench-rom <rom.gba> [--steps N]
```

GBA side has only the JSON-LLVM backend (no hand-written ARM
counterpart), so only a single number is reported. Boot flow is
identical to `GbaRomExecutionTests.BootGba` (minimal BIOS stub +
three extern bindings + multi-set CpuExecutor). One warm-up step
runs before the timer starts.

### Environment

- Windows 11 x64
- .NET 10 Release build
- LLVMSharp.Interop 20.x, MCJIT, OptLevel=0
- Both add `nounwind` + `no-jump-tables` function attributes (avoiding
  the Windows COFF section-ordering relocation crash, see
  `MD/note/framework-emitter-architecture.md`)

---

## 2. Raw data

### GB (LR35902)

ROM: `test-roms/blargg-cpu/cpu_instrs.gb` — Blargg master suite
(11 cpu_instrs subtests strung together, includes MBC1 banking)

Execution budget: 400,000,000 t-cycles

```
setup time: legacy=0 ms, json-llvm=313 ms (incl. spec compile + MCJIT)

legacy   :  0.637 s,  35,611,151 instr ->  55.91 MIPS
json-llvm: 10.666 s,  36,097,325 instr ->   3.38 MIPS
ratio    : 16.52x slower (json-llvm vs legacy)
```

### GBA (ARM7TDMI)

ROM 1: `test-roms/gba-tests/arm/arm.gba` — jsmolka ARM mode suite
ROM 2: `test-roms/gba-tests/thumb/thumb.gba` — jsmolka Thumb mode suite

Execution budget: 50,000,000 instructions (after the ROM finishes it
spins in halt loop `B .`)

```
arm.gba    setup: 328 ms, run: 11.331 s, 50M instr -> 4.41 MIPS
thumb.gba  setup: 200 ms, run: 11.681 s, 50M instr -> 4.28 MIPS
```

---

## 3. Comparison vs real hardware CPU

### Real-hardware IPS estimate

| Platform | Master clock | Avg cycles/instr | Real-hw IPS |
|---|---|---|---|
| GB DMG (LR35902) | 4.194 MHz T-cycles (1.048 MHz M-cycles) | ~2 M-cycles | **~0.5 MIPS** |
| GBA (ARM mode)   | 16.78 MHz cycles | ~1.5 cycles | **~10 MIPS** |
| GBA (Thumb mode) | 16.78 MHz cycles | ~1.2 cycles | **~12 MIPS** |

(Actual IPS varies with memory wait state, PSR ops, interrupts, etc.
These are mixed ALU + memory typical-game-code estimates.)

### json-llvm vs real hardware

| Platform | Real-hw IPS | json-llvm IPS | json-llvm relative speed |
|---|---|---|---|
| **GB DMG** | ~0.5 | 3.4 | **~7x real-time** |
| **GBA (ARM)** | ~10 | 4.4 | **~40-50% real-time** |
| **GBA (Thumb)** | ~12 | 4.3 | **~30-40% real-time** |

### Legacy comparison (hand-written upper bound reference)

| Platform | Legacy IPS | vs real hw |
|---|---|---|
| GB DMG | 55.91 | **~110x real-time** |

Legacy GB is a 1900-line hand-written `switch (opcode)` interpreter;
the C# JIT can heavily inline + register-allocate + be branch
prediction friendly, hence much faster. This serves as a reference
point for "the speed ceiling a hand-tuned interpreter can reach on
the same hardware".

---

## 4. Conclusion

### GB side — production-ready

JsonCpu 3.4 MIPS vs 0.5 MIPS real hardware, **~7x headroom**. Even
adding:

- DMG PPU rendering (~70K cycles per frame)
- APU sound synthesis
- Various IO + DMA

running GB games at full 60 fps is no problem. The framework's claim
"JSON can be used directly" on 8-bit ISAs holds.

### GBA side — framework correct, perf insufficient

11/11 ARM + Thumb tests all green, **correctness is fine**. But 4.4
MIPS vs 10+ MIPS real hardware = **currently about 40% real-time**.
This means a 60 fps game would drop to ~24 fps.

More pessimistic details:

1. **The jsmolka ROMs we tested mostly halt-loop `B .` (cheapest
   instruction). Actual game code has:
   - More memory access (multiple `memory_read_8` / `memory_write_*`
     extern calls)
   - More PSR ops + condition evaluation
   - More mode switches / banked register handling
   - Occasional interrupts
   Actual game MIPS may drop to **2-3 MIPS**
2. PPU rendering not yet counted (one GBA scanline computes 128 sprites
   x 16-bit pixels)
3. APU, DMA, etc. not yet counted

**Conclusion**: instruction-level JIT is not fast enough on GBA.
Phase 7 block-JIT is required.

---

## 5. Why json-llvm is so much slower than Legacy

Per-instruction dispatch overhead:

```
JsonCpu.StepOne:
  bus.ReadByte(pc)          - virtual call into bus
  WriteI16(pcOff, pc+1)     - state buffer write
  decoder.Decode(opcode)    - dictionary lookup -> entry walk
  BuildFunctionKey(...)     - string concatenation per call
  _fnPtrCache[key]          - dictionary lookup
  fn(state*, instructionWord) - indirect call into JIT'd code
  CyclesFor(def)            - iterate cycles.form string
  ConditionalBranchExtraCycles(...) - switch-case
```

Each instruction also incurs inside the JIT'd function:

- Load `state` pointer
- One load + store each on registers / PC / F
- Most ALU ops also call `memory_read_8`-style extern shim (C# delegate
  trampoline, ~30-100 ns each)

Whereas LegacyCpu is:

```
case 0x47: _b = _a; break;  // one mov, C# JIT inlines directly
```

`_a`/`_b` are instance fields, register-allocated by C# JIT into CPU
registers.

**Roughly where the gap comes from**:
- ~5x: JIT'd functions reload/restore state buffer per instruction,
  no cross-instruction register caching
- ~3x: each instruction goes through the dispatch chain (decode + key
  + fn pointer lookup + indirect call)
- ~2x: memory accesses go through extern shim instead of direct inline

Roughly ~30x ceiling; measured 16x because some instructions are
already dispatch-heavy, compressing the relative gap.

---

## 6. Speedup roadmap

In priority order (highest to lowest ROI):

### A. Block JIT (Phase 7) — biggest impact

Fuse N consecutive instructions into one LLVM function. For example
a stretch of branchless linear code:

```
MOV R0, #1
ADD R1, R0, #2
STR R1, [R2]
B .next
```

Currently is 4 dispatches + 4 indirect calls. Block-JIT becomes 1
dispatch + 1 indirect call, with everything internally inlined.

**Expected speedup**: 5-10x (longer blocks, better), GBA approaches
native interpreter speed.

### B. State buffer -> register caching

Have the JIT'd function load commonly-used registers (PC, SP, A, F,
HL...) into LLVM virtual registers at entry, store back to state
buffer at exit. LLVM register-allocates them into CPU registers,
saving the intermediate load/store.

**Expected speedup**: 2-3x. Pairs even better with block-JIT (all
instructions in a block share loaded registers).

### C. Memory bus extern inlining

memory_read_8 etc. are C#-side trampolines. Each call crosses cdecl
boundary, ~30-100 ns. If changed to do mapped-memory pointer
arithmetic directly inside IR (spec's memory map hardcoded into IR),
this layer can be skipped.

**Expected speedup**: 1.5-2x (more pronounced for memory-heavy code).

### D. Decode table specialisation

Currently DecoderTable does "iterate mask/match against opcode". Can
precompute a 256-entry / 65536-entry table. For 8-bit GB it's directly
256 entries; ARM 16-bit Thumb is 65536; ARM 32-bit too large to fit
(use hash table).

**Expected speedup**: 1.2-1.5x.

### E. C# host loop hot-path tightening

`StepOne`'s `BuildFunctionKey` uses string concat; `_fnPtrCache` is a
generic Dictionary. Can be changed to pre-cache an `IntPtr` field on
DecodedInstruction, saving one dictionary lookup.

**Expected speedup**: 1.1-1.2x.

### F. ORC LLJIT replacing MCJIT

Doesn't directly speed up dispatch, but resolves the historical baggage
of Windows COFF section ordering and supports lazy compile / re-jit.
Necessary prerequisite for dynamic recompilation post-block-JIT.

**Expected speedup**: ~0 (pure maintenance investment, but Phase 7
requires it).

---

## 7. Baseline summary (one-line version)

Current (2026-05-02, Phase 4.5C wrap-up):

- **GB JsonCpu = 3.4 MIPS (~7x real-time) -> production-ready**
- **GBA JsonCpu = 4.4 MIPS (~40% real-time) -> awaiting Phase 7 block-JIT**
- **Reference: Legacy GB = 56 MIPS, hand-written upper bound around here**

After speedup, when doing ROM lockstep diff, numbers should move toward
real-hw direction: GB stays "well above real hardware", GBA needs to
exceed real hardware by at least 1.5-2x (to leave margin for PPU/APU/DMA
and other work).
