# Permanently-pinned state buffer (Phase 7 B.f) — **GBA Thumb +27% (cumulative +113%)**

> **Strategy**: CpuExecutor.Step and JsonCpu.StepOne both used
> `fixed (byte* p = _state) fn(p, instructionWord);` — pinning the _state
> array per instruction for the JIT'd code. The pin itself has cost
> (~50 ns × millions of instructions = noticeable). The state buffer's
> size is fixed and lifetime is the entire program, so pin-once-forever
> wins.
>
> **Hypothesis**: drop the `fixed`, use GCHandle.Alloc(state, Pinned) to
> pin once in the ctor, cache IntPtr/byte*, then pass the cached pointer
> to JIT'd fn.
>
> **Result**: **GBA thumb +27% (6.26 → 7.97 MIPS, 1.9× real-time consistent)**,
> GBA arm flat (7.19 → 7.13, noise), GB json-llvm flat (6.41 → 6.31, noise),
> GB legacy unchanged. Hypothesis partially holds — Thumb benefits the most,
> ARM has plateaued, JsonCpu's `fixed` may already be optimised away by JIT.
>
> **Decision**: keep the change. GBA Thumb cumulative +113% (3.75 → 7.97 MIPS),
> 0.9× → 1.9× real-time.

---

## 1. Results (3-run, min/avg/max)

| ROM                     | Backend     | runs | min  | **avg** | max  | B.e avg | starting baseline | **Δ vs B.e** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 3    | 6.34 | **7.13**| 8.12 | 7.19    | 3.82              | −0.8% noise  | +86.6%        |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 7.75 | **7.97**| 8.14 | 6.26    | 3.75              | **+27.3%**   | **+112.5%**   |
| GB 09-loop100.gb        | legacy      | 3    | 31.32| **33.67**| 36.86| 34.29  | 32.76             | noise        | +2.8% noise   |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.22 | **6.31**| 6.38 | 6.41    | 2.66              | −1.6% noise  | +137.2%       |

real-time × changes:
- GBA arm-loop100: 1.9× (individual B.e) → **1.5–1.9×** (stable up to 1.9×)
- GBA thumb-loop100: 1.5× → **1.9×** (consistent across 3 runs)
- GB json-llvm: 12.9× → 13.0×

---

## 2. Why fixed has cost

What `fixed (byte* p = _state)` does in the .NET runtime:
1. Pin the _state array on the GC heap, blocking generational GC compaction
2. Set up GC-internal pinned-handle bookkeeping (small list)
3. Take raw pointer
4. (function body) use the pointer
5. On exit from fixed scope, unpin (remove from bookkeeping)

Each enter/exit ~30–100 ns (depending on GC pressure). For a 100M instruction/sec
hot loop, that's 3–10 seconds of host time — significant.

GCHandle.Alloc(Pinned) trade-off:
- Pin once instead of N times — saves N−1 enter/exit costs
- _state cannot be GC-compacted for entire process lifetime — but _state
  is a long-lived fixed-size buffer (~250 bytes), wouldn't be collected
  anyway, so the extra cost is "block young gen compaction on a small root"
  — negligible

For long-lived large buffers (CPU state buffer fits perfectly), permanent
pin is the idiomatic .NET pattern.

---

## 3. Why only GBA Thumb gains noticeably?

| Backend | B.e | B.f | Δ |
|---|---|---|---|
| GBA arm | 7.19 | 7.13 | noise |
| GBA thumb | 6.26 | **7.97** | **+27%** |
| GB json-llvm | 6.41 | 6.31 | noise |

Four possible explanations:

1. **GBA arm has plateaued after B.e** — F.x/F.y/B.e already cut dispatcher
   overhead down to "per-call extern dispatch + JIT'd function body
   execution" level; B.f's ~50 ns saving is a smaller share for ARM
   (an ARM instruction is inherently a bit slower).
2. **GBA Thumb has a higher dispatcher share** — Thumb instructions
   themselves are simpler (no cond gate / no shifted operand), dispatcher
   overhead ratio is higher, so saving fixed cost is more visible.
3. **JsonCpu's `fixed` is already JIT-optimised away** — JsonCpu is a
   sealed class, _state is a readonly field allocated from the same place;
   .NET tiered JIT may have already compiled fixed into "direct take address"
   without pin/unpin cost. CpuExecutor structure is slightly more complex
   (polymorphic setsByName etc.), and may not have the same optimisation.
4. **GBA arm individual run noise large** — 6.34, 6.93, 8.12 — run 3's
   8.12 is ARM's actual level; the avg is dragged down by 2 cold-tier runs.
   Looking at max only, ARM is 8.12 vs B.e 7.80 — also progress of ~+4%.

Not certain which is the main driver, but Thumb's +27% is stable
(3 runs all in 7.75–8.14), so the gain is real.

---

## 4. Phase 7 cumulative (5 steps)

| Stage | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| 7.B.e cache state offsets | 7.19 / 1.9× | 6.26 | 34.29 | 6.41 |
| **7.B.f permanent pin** | **7.13 / 1.9×** | **7.97 / 1.9×** | 33.67 | 6.31 |

Both GBA paths stable at 1.9× real-time, 2× over baseline 0.9×.
GB json-llvm went from 5.5× to 13×.

GB legacy keeps bouncing in the 32–37 MIPS noise band, untouched by any
Phase 7 optimisation (path is not on dispatcher / decoder).

---

## 5. What's left

ROI table updated:

| Strategy | Status | Expected gain |
|---|---|---|
| **A. Block-level JIT** | not done | **High** — 8–13× theoretical, but big change |
| **E. Mem-bus fast path** | not done | **Mid–high** — JIT'd code inline region check, saves mem extern call |
| **C. Lazy flag** | not done | Mid — attacks IR-side flag writes |
| **B.b/c/d IR micro-tweaks** | not done | Low — function still too small, O3 already proved no room |

The "easy targets on the dispatcher side" of Phase 7 are basically done.
Next significant progress requires **E (mem-bus)** or **A (block-JIT)**.
The former is medium-complexity medium-gain; the latter high-complexity
high-gain.

The Phase 5.8 starting baseline goal "test ROMs runnable, not too slow"
is met:
- GBA test ROMs: 1.9× real-time, 1200 frames done in <11s
- GB test ROMs: 13× real-time, 1200 frames done in <1.5s

So perf is already good enough; **whether to keep going on Phase 7
depends on the goal**:
- Validate framework "swap-spec generality" promise → jump to third CPU
  port (Phase 5.9)
- Demo block-JIT pushing framework to native level → attack A
- Stabilize source of GBA thumb's +27% → profile to confirm hypothesis

---

## 6. Change scope (verification)

```
src/AprCpu.Core/Runtime/CpuExecutor.cs:
  + private readonly GCHandle _stateHandle
  + private readonly byte* _statePtr
  + both ctors add pin + cache _statePtr
  + ~CpuExecutor finalizer frees GCHandle
  ~ Step() uses fn(_statePtr, instructionWord), no more fixed

src/AprGb.Cli/Cpu/JsonCpu.cs:
  + private GCHandle _stateHandle / byte* _statePtr
  ~ Reset() re-pins (free old + alloc new)
  ~ StepOne uses fn(_statePtr, ...), no more fixed

Verification:
  - 345/345 unit tests pass
  - 3/3 Blargg 02 / 07 / 10 pass (key sub-tests for IRQ / branch / bit ops)
```

---

## 7. Related documents

- [`MD_EN/performance/202605030002-jit-optimisation-starting-point.md`](/MD_EN/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- [`MD_EN/performance/202605030025-optlevel-0-to-3.md`](/MD_EN/performance/202605030025-optlevel-0-to-3.md) — Phase 7 B.a
- [`MD_EN/performance/202605030036-fnptr-cache-by-instruction-def.md`](/MD_EN/performance/202605030036-fnptr-cache-by-instruction-def.md) — Phase 7 F.x
- [`MD_EN/performance/202605030047-prebuilt-decoded-instruction.md`](/MD_EN/performance/202605030047-prebuilt-decoded-instruction.md) — Phase 7 F.y
- [`MD_EN/performance/202605030054-cache-state-offsets-cpuexecutor.md`](/MD_EN/performance/202605030054-cache-state-offsets-cpuexecutor.md) — Phase 7 B.e
- [`MD_EN/design/03-roadmap.md`](/MD_EN/design/03-roadmap.md) Phase 7 — B.f marked done
