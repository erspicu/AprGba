# AggressiveInlining for GBA bus methods (Phase 7 B.g) — **GBA arm +13% (cumulative +111%)**

> **Strategy**: add `[MethodImpl(AggressiveInlining)]` hints to GbaMemoryBus
> hot methods (Locate, ReadWord, ReadHalfword, NotifyExecutingPc,
> HasPendingInterrupt). GbaMemoryBus is a sealed class, .NET tiered JIT
> can already devirtualize through the IMemoryBus interface, but whether
> it actually inlines into the caller depends on method size. The hint
> forces the inliner to try regardless of size.
>
> **Hypothesis**: CpuExecutor.Step and GbaSystemRunner.RunCycles call
> _bus.* methods multiple times per instruction. If these are inlined
> into the caller, multiple small functions can be optimised together
> (CSE, constant fold, register allocation jointly).
>
> **Result**: **GBA arm +13% vs B.f (7.13 → 8.05 MIPS, stable ~2× real-time)**,
> GBA thumb flat (7.97 → 8.10, plateaued already). Bonus: **GBA arm noise
> dropped substantially** (B.f 6.34–8.12 → B.g 7.85–8.20).
>
> **Decision**: keep the change. GBA arm cumulative +111% (3.82 → 8.05 MIPS,
> 0.9× → 2.0× real-time).

---

## 1. Results (multi-run, min/avg/max)

| ROM                     | Backend     | runs | min  | **avg** | max  | B.f avg | starting baseline | **Δ vs B.f** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 7.85 | **8.05**| 8.20 | 7.13    | 3.82              | **+12.9%**   | **+110.7%**   |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 8.03 | **8.10**| 8.13 | 7.97    | 3.75              | +1.6%        | **+116.0%**   |

GB legacy / GB json-llvm not re-run (B.g only touches GbaMemoryBus, doesn't
affect GB path). B.f results still apply: GB legacy 33.67, GB json-llvm 6.31.

real-time × changes:
- GBA arm-loop100: 1.9× (individual B.f) → **2.0×** (stable, individual run hits 2.0×)
- GBA thumb-loop100: 1.9× → **1.9×** (consistent)

Bonus observation: **GBA arm noise vanished**. B.f's 4 runs spanned
6.34–8.12 (~30% range); B.g's 4 runs span 7.85–8.20 (~5% range). Likely
cause: .NET tiered JIT previously had to promote hot methods before
inlining; with the hint, tier 0 directly inlines.

---

## 2. Change scope

`src/AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs` adds 5 attributes:

```
[MethodImpl(AggressiveInlining)] private static (Region, int) Locate(uint addr)
[MethodImpl(AggressiveInlining)] public void NotifyExecutingPc(uint pc) => ...
[MethodImpl(AggressiveInlining)] public ushort ReadHalfword(uint addr) => ...
[MethodImpl(AggressiveInlining)] public uint ReadWord(uint addr) => ...
[MethodImpl(AggressiveInlining)] public bool HasPendingInterrupt() => ...
```

ReadByte not added (not on instruction-fetch hot path; only LDR/STRB use it),
ReadIo* / write series also not added (runtime variation large, size larger,
easy to anti-optimise).

NotifyInstructionFetch not added (has BIOS region early-return inside, but
size is larger; inliner may decline anyway).

---

## 3. Why GBA arm benefits more than thumb

ARM hot path includes:
- 1 × `_bus.NotifyExecutingPc(pc)` per Step — single-line setter, should
  have been inlined
- 1 × `_bus.ReadWord(pc)` per Step (instruction fetch) — switch over 9
  cases, larger size but JIT willing to inline with hint
- 1 × `_bus.NotifyInstructionFetch(...)` — early-return for non-BIOS

Thumb takes the same path but individual runs are already at 8.0+ MIPS
plateau, possibly bound by other overhead (e.g. dispatcher, scheduler.Tick).
ARM was previously noisy, then jumped +13%, looking more like "JIT finally
inlined the entire fetch path".

---

## 4. Phase 7 cumulative (6 steps)

| Stage | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| 7.B.e cache state offsets | 7.19 / 1.9× | 6.26 | 34.29 | 6.41 |
| 7.B.f permanent pin | 7.13 / 1.9× | 7.97 / 1.9× | 33.67 | 6.31 |
| **7.B.g AggressiveInlining bus** | **8.05 / 2.0×** | **8.10 / 1.9×** | (unchanged) | (unchanged) |

Both GBA paths plateau at ~8 MIPS / 2× real-time.
GB json-llvm plateau at ~6.3 MIPS / 13× real-time (B.g doesn't affect GB path).

---

## 5. What's left

The dispatcher-side and inline-side quick wins are exhausted. To keep
making significant progress only:

| Strategy | ROI |
|---|---|
| **A. Block-level JIT** | High — removes dispatcher overhead; theoretical 8–13× speedup |
| **E. Mem-bus fast path** | Mid–high — JIT'd code inline region check + direct array index, saves mem extern call |
| **C. Lazy flag** | Mid — depends on flag op count in spec; significant for GBA (CPSR writes a lot) |
| **G. Native AOT** | Mid — solves GB legacy run-to-run noise + cold-start |

**Significant further progress requires E or A**. The dispatcher / inline
phase of Phase 7 has saturated.

Current "test ROM usability" is fine:
- GBA test ROMs: 2.0× real-time, 1200 frames done in 11s
- GB test ROMs: 13× real-time, 1200 frames done in 1.5s

Continuing E/A is "for commercial ROM smoothness / framework demo-grade
perf", not "test ROM won't run" critical-need territory.

---

## 6. Change scope (verification)

```
src/AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs:
  + [MethodImpl(AggressiveInlining)] × 5 (Locate, ReadWord, ReadHalfword,
                                            NotifyExecutingPc, HasPendingInterrupt)

Verification:
  - 345/345 unit tests pass
  - (Blargg not re-run — B.g only touches GbaMemoryBus, doesn't affect GB;
    GB JIT path already verified green in earlier commits.)
```

---

## 7. Related documents

- [`MD_EN/performance/202605030002-jit-optimisation-starting-point.md`](/MD_EN/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- Previous 5 Phase 7 perf notes (B.a / F.x / F.y / B.e / B.f)
- [`MD_EN/design/03-roadmap.md`](/MD_EN/design/03-roadmap.md) Phase 7 — B.g marked done
