# Phase 7 H.a re-enabled — explicit LLVM new-pass-manager pipeline (no instcombine yet)

> **Strategy**: The original H.a (`d929532`) was disabled in the recovery
> branch because instcombine miscompiled the BIOS LLE path. After the
> recovery branch finished a series of A.6.1 Strategy 2 fixes (`5af9d36`
> read_reg(15) after PC-write, `ab1204e` block_store STM with R15,
> `05c285a` MMIO sync re-entry guard, etc.), RunPasses is re-enabled to
> recover the alloca→SSA + DSE + GVN optimisation gains.
>
> **Hypothesis**: After A.6.1 fixes, the IR pattern is cleaner and
> instcombine may also be fixed; running all 5 passes might be OK.
>
> **Result**: mem2reg + gvn + dse + simplifycfg → **360 tests pass**, BIOS
> LLE + ROM matrix all green (md5 hash matches), perf within noise band
> (-2% to +2%). Adding **instcombine** still miscompiles 3
> BlockFunctionBuilderTests (Block_ThreeMovs / Block_FirstInstructionCondFails
> / AddModule_*) — R-register store is incorrectly killed. Ship the 4-pass
> set first; instcombine fix deferred to a separate Phase 7 H.a-instcombine
> investigation.
>
> **Decision**: Keep 4-pass set. Perf neutral but logically correct + lays
> the mem2reg foundation for a future C.b lazy flag retry.

---

## 1. Result (3-run avg)

| ROM                  | Backend     | run1  | run2  | run3  | **avg** | last (no H.a) | **Δ**     |
|----------------------|-------------|------:|------:|------:|--------:|--------------:|----------:|
| GBA arm-loop100      | json-llvm pi | 8.00 | 7.94  | 8.01  | **7.98**| 8.15          | -2.1%     |
| GBA arm-loop100      | block-JIT   | 10.22 | 10.18 | 10.25 | **10.22**| 10.35        | -1.3%     |
| GBA thumb-loop100    | json-llvm pi | 8.12 | 8.06  | 8.25  | **8.14**| 8.14          | 0.0%      |
| GBA thumb-loop100    | block-JIT   | 11.34 | 11.54 | 11.74 | **11.54**| 11.30        | +2.1%     |

**Logic verification (Tier 2-4):**
- 360 unit tests pass
- 8-combo screenshot matrix all md5 hash matches (`7e829e9e837418c0f48c038341440bcb` = "All tests passed")
- BIOS LLE PI vs BJIT IRQs aligned: 263 / 263

---

## 2. Change scope

```
src/AprCpu.Core/Runtime/HostRuntime.cs:
  + Compile():  RunOptimizationPipeline(_initialModule)  uncommented
  + AddModule(): RunOptimizationPipeline(module)          uncommented (per-block JIT modules)
  ~ passes string: "mem2reg,gvn,dse,simplifycfg"  (instcombine excluded — see §3)
```

`RunOptimizationPipeline()` method already exists in HEAD; only the two
call sites were commented out. Compile takes ~50ms one-time cost,
AddModule takes ~5-15ms per block.

---

## 3. Why instcombine is excluded

Enabling `mem2reg,instcombine<no-verify-fixpoint>` and running unit tests:

```
Failed! - Failed: 3, Passed: 357
  AprCpu.Tests.BlockFunctionBuilderTests.Block_ThreeMovs_AllExecuteAndPcAdvances
  AprCpu.Tests.BlockFunctionBuilderTests.Block_FirstInstructionCondFails_SecondStillRuns
  AprCpu.Tests.BlockFunctionBuilderTests.AddModule_PostCompile_RoundTripExecutes
```

Failure form: `Assert.Equal(2u, ReadI32(state, rt.GprOffset(1)))` returns
0 instead of 2. That is, the `MOV R1, #2` store has been eliminated.

Using only `instcombine<no-verify-fixpoint>` without mem2reg also fails.
Other combinations without instcombine (mem2reg / gvn / dse / simplifycfg
/ any combination) all pass.

**Hypothesis**: BlockFunctionBuilderTests directly invoke JIT functions,
with CPSR=0x10 + cycles_left=1_000_000 as known constants. `instcombine`
may:
- Consider `state[GPR[1]_offset] = 2` a dead store (because there's no
  later read in the function and the pointer param has no noalias attribute,
  but instcombine does its own escape analysis)
- Or constant-fold the budget check (`1_000_000 - 4 <= 0` always false)
  and trigger a cascade that folds away the entire instruction's IR

Need a separate investigation: dump IR before and after instcombine, find
the specific transformation. May need emitter-side IR pattern changes
(e.g. add `volatile` or `noalias`), or exclude an instcombine sub-pass.

**Negative impact**: Loses instcombine's peephole optimisations
(select/branch simplification, bit-twiddling fold, etc.). Limited impact
given our IR is mostly very short.

---

## 4. Why the perf gain is small

Our emitter IR is mostly:
- per-instr fn short (5-30 IR ops)
- no large alloca shadow (C.b lazy flag not yet up, so mem2reg has few
  promotion targets)
- no repeated load of the same GPR (CSE/GVN has little to work on)

block-JIT IR is longer (16+ instrs/block) but has little memory interaction
between instructions, so DSE/GVN find few elimination opportunities.

Real gains await:
1. **C.b lazy flag retry** (with alloca shadow + mem2reg) — expected +5-15%
2. **instcombine fix** — additional +1-3%
3. **More aggressive block IR inline** (e.g. inline cycle-counting sub/icmp pattern)

---

## 5. Phase 7 cumulative (recovery branch)

| Stage | GBA arm pi | GBA arm bjit | GBA thumb pi | GBA thumb bjit |
|---|---:|---:|---:|---:|
| 7.B.h (recovery baseline f304376) | 8.33 | (broken) | 8.39 | (broken) |
| 7.A.6.1 BIOS LLE complete + STM PC fix (ab1204e) | 8.15 | 10.35 | 8.14 | 11.30 |
| **7.H.a re-enabled (this)** | **7.98** | **10.22** | **8.14** | **11.54** |

Block-JIT from broken → working → 11.54 MIPS / 2.8x real-time.
Per-instr flat (~8.0).

---

## 6. Related docs

- `MD/process/01-commit-qa-workflow.md` — this round used Tier 4 workflow
- `MD/performance/202605030148-lazy-flag-attempt-postmortem.md` — first C.b attempt failure record
- Original H.a commit `d929532` (main branch, replaced by recovery)
- TODO: Phase 7 H.a-instcombine — bisect which instcombine sub-pass is broken
