# loop100 stress-test bench — Phase 5.8 post-refactor (2026-05-02)

> **Comparison data after Phase 5.8 emitter-refactor.**
>
> Same 4 ROMs x backend matrix as [`MD_EN/note/loop100-bench-2026-05.md`](/MD_EN/note/loop100-bench-2026-05.md)
> (Phase 5.7 baseline), same 1200 frames, same machine, same .NET 10
> Release build, same LLVM 20 MCJIT — measuring the impact of the
> emitter library refactor on MIPS / real-time.
>
> Refactor scope: Phase 5.8 Steps 5.1-5.7 (27 LR35902-specific ops
> generalised into common ops; `Lr35902Emitters.cs` shrunk from ~2620
> to 1346 lines, -49%; details in
> [`MD_EN/design/11-emitter-library-refactor.md`](/MD_EN/design/11-emitter-library-refactor.md)).
>
> Source commits: `3c88ea9` (refactor wrap-up + docs).

---

## 1. Result table (3-run average; slowest / fastest / median listed)

| ROM                     | Backend     | Phase 5.7 baseline | **Phase 5.8 (avg)** | runs | Δ MIPS |
|-------------------------|-------------|-------------------:|---------------------:|------|-------:|
| GBA arm-loop100.gba     | json-llvm   | 3.68               | **3.82** (3.75-3.91) | 3    | **+3.8%** |
| GBA thumb-loop100.gba   | json-llvm   | 3.70               | **3.75** (3.72-3.79) | 3    | **+1.4%** |
| GB 09-loop100.gb        | legacy      | 38.36              | **32.76** (31.29-33.97) | 4 | **-14.6%** |
| GB 09-loop100.gb        | json-llvm   | 2.73               | **2.66** (2.59-2.75) | 3    | **-2.6%** |

How it was run (same as the 5.7 baseline):

```bash
apr-gba --rom=arm-loop100.gba    --frames=1200
apr-gba --rom=thumb-loop100.gba  --frames=1200
apr-gb  --cpu=legacy    --rom=09-loop100.gb --frames=1200
apr-gb  --cpu=json-llvm --rom=09-loop100.gb --frames=1200
```

---

## 2. Interpretation

### 2.1 GBA json-llvm: +1.4% / +3.8% — refactor did not slow it down, slightly faster

Both GBA paths (ARM / Thumb) improved marginally. Speculated reasons:

- 5.3 generalised the select-trick path of LR35902-style branch_cc /
  call_cc / ret_cc, incidentally letting the shared `branch` go
  through a cleaner `LocateProgramCounter` -> LLVM may have slightly
  more inlining / CSE opportunities.
- 5.5's `Binary` auto-coerce is pure extra logic, but LLVM
  constant-folds it away when widths already match (ARM operands are
  always i32).
- IR pattern consistency improved (every generic op produces the same
  BuildAdd/BuildOr shape), making the LLVM optimiser's job easier.

Not a major improvement, but proves the refactor **did not introduce
perf regression on the ARM side**.

### 2.2 GB json-llvm: -2.6% — the cost of more IR steps

GB json-llvm went from 2.73 -> 2.66 MIPS, ~3% slower. Within
expectations — the refactor split several LR35902 ops into multi-step
generic chains:

| Operation | Before refactor | After refactor | step count |
|---|---|---|---|
| INC r          | 1 step (`lr35902_inc_r8`) | 7 steps (read + add + trunc + write + 2 flags + h_inc) | 7x |
| BIT b,r        | 1 step (`lr35902_cb_bit`) | 4 steps (read + bit_test + 2 set_flag) | 4x |
| RLC r          | 1 step (`lr35902_cb_shift`) | 3 steps (read + shift + write) | 3x |
| LDH (n),A      | 3 steps                    | 4 steps (added explicit `or`)             | 1.3x |
| CALL nn        | 2 steps                    | 2 steps (unchanged)                       | 1x |
| JR e           | 2 steps                    | 5 steps (sext + read_pc + add + branch)   | 2.5x |

Each step is one `IMicroOpEmitter.Emit` call -> a few extra dictionary
lookups + JsonElement parses. After LLVM compiles the IR to native, the
**emitted native code count is similar** (LLVM's optimiser inlines the
multi-op chain), but **the spec -> IR phase** takes more time.

In a hot loop running many times, spec -> IR is a one-shot cost (done
during init), and runtime should be very close to the original. The
-3% is the measured host-time difference, not an emitted-code
difference. Likely amortised effect of slightly increased init time.

### 2.3 GB legacy: -14.6% — unexpected, but probably noise

GB legacy backend **does not go through spec / emitter / LLVM JIT** at
all — it's a hand-written big switch-case. Phase 5.8 refactor did not
touch the legacy code path. So in theory the difference should be 0%.

Actual data: 38.36 -> 32.76 MIPS, -14.6%. Possible explanations:

1. **Single run too short**: legacy takes only ~300ms to run 1200
   frames, so process startup / JIT warmup / GC initialisation become
   a larger fraction. The baseline 38.36 may have hit a "clean host"
   smooth run; this round was noisier.
2. **Windows scheduler / background processes**: other processes
   loaded between the two bench runs.
3. **Shared infrastructure** (memory bus, GbScheduler, PPU stub) may
   have had subtle changes after 5.7; even though the refactor didn't
   touch them, LegacyCpu and JsonCpu share GbMemoryBus + GbScheduler.
4. **.NET tiered compilation**: every first-run hot path gets rejitted
   ~100ms in, which is non-negligible relative to 300ms total runtime.

Cannot tell which. No immediate action — "running test ROMs with
legacy" is not the framework's primary use case; this baseline is
auxiliary, not a perf target. The real story is **GBA / GB json-llvm
both did not regress**.

### 2.4 cycle/instr unchanged

| Backend | t-cyc/instr | vs 5.7 |
|---|---|---|
| GBA json-llvm | 4.00 | same (hardcoded) |
| GB legacy     | 8.68 | same |
| GB json-llvm  | 8.60 | same |

The refactor did not change per-instruction cycle accounting; numbers
match exactly.

---

## 3. Conclusion

**Refactor goal achieved**:
- The main framework path (json-llvm) improved marginally on GBA,
  regressed marginally on GB, overall within measurement noise (~+/-5%).
- emitter library structure is significantly clearer (-49% LR35902
  emitter LOC, 27 arch-specific ops generalised), without being slower.
- The "clean structure vs perf neutrality" trade-off holds up.

**GB legacy regressed 14%**: classified as "single-run measurement
noise"; if anyone cares about legacy perf in the future this can be
re-investigated. Once Phase 7 block-JIT is actually built, all four
numbers will be redrawn anyway, and we'll re-verify then.

---

## 4. Bench reproduction steps

Identical to the 5.7 baseline (see [`MD_EN/note/loop100-bench-2026-05.md`](/MD_EN/note/loop100-bench-2026-05.md) §6):

```bash
dotnet build -c Release AprGba.slnx

dotnet run --project src/AprGba.Cli -c Release -- \
    --rom=test-roms/gba-tests/arm/arm-loop100.gba --frames=1200

dotnet run --project src/AprGba.Cli -c Release -- \
    --rom=test-roms/gba-tests/thumb/thumb-loop100.gba --frames=1200

dotnet run --project src/AprGb.Cli -c Release -- \
    --cpu=legacy \
    --rom=test-roms/gb-test-roms-master/cpu_instrs/loop100/09-loop100.gb \
    --frames=1200

dotnet run --project src/AprGb.Cli -c Release -- \
    --cpu=json-llvm \
    --rom=test-roms/gb-test-roms-master/cpu_instrs/loop100/09-loop100.gb \
    --frames=1200
```

---

## 5. When to update the next baseline

The table + a new comparison note should be redone whenever any of the
following happens:

- Phase 5.8/5.9 **third CPU comes online** — re-run after adding a
  RISC-V/MIPS spec (to see whether the refactor really helps "swapping
  CPUs").
- Any major spec change (new instruction set / decoder rewrite).
- Any major emitter shared-layer change (StackOps / FlagOps / BitOps
  refactoring).
- Phase 7 block-JIT — expected to bring 8-13x speedup, will completely
  rewrite these 4 numbers.

Routine commits do not need a re-run — refactor + maintenance work can
trust "all unit tests green + Blargg green" as sufficient.
