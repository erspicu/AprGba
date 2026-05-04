# Phase 7 H.a-instcombine — fixed (datalayout missing → struct GEP miscompile)

> **Strategy**: Add `instcombine<no-verify-fixpoint>` back to default pass
> list. Earlier theory that instcombine was related to `switch i32 poison`
> UB-propagation was completely wrong — the real root cause was that the
> module had no `target datalayout` set, so instcombine used a default
> layout (with different struct alignment) that computed wrong byte
> offsets for struct GEPs.
>
> **Result**: T1 + T2 + T3 all green. Bench slightly up / flat (+0.2~1.8%).
>
> **Decision**: Keep. Default pass list grows from 4 passes to 5.

---

## 1. Root cause analysis

### 1.1 Symptom

After adding `instcombine` to pass list, 3 BlockFunctionBuilderTests fail:
```
Block_ThreeMovs_AllExecuteAndPcAdvances:
  Expected: 2 (R1)
  Actual:   0
```

Adding `--filter Block_ThreeMovs` + `APR_IR_AFTER` dump to compare IR
with/without instcombine:

**Without instcombine** (passing) — uses struct-typed GEP:
```llvm
%pc_written_ptr = getelementptr { i32 ×37, i64, i32, i8, i32 }, ptr %state, i32 0, i32 39
%cycles_left_ptr = getelementptr { ... }, ptr %state, i32 0, i32 40
```

**With instcombine** (failing) — instcombine canonicalised to byte GEP:
```llvm
%pc_written_ptr1 = getelementptr i8, ptr %state, i64 160   ; ← should be 164
%cycles_left_ptr = getelementptr i8, ptr %state, i64 164   ; ← should be 168
```

The two sides **differ by 4 bytes**!

### 1.2 Why

CpuStateLayout struct (41 fields, ARM7TDMI typical):
```
field 0..36 : 37 × i32         → bytes 0..147
field 37    : i64 (CycleCounter) — needs 8-byte align → pad 4 → byte 152
field 38    : i32 (PendingExc)   → byte 160
field 39    : i8  (PcWritten)    → byte 164
field 40    : i32 (CyclesLeft)   → byte 168 (3-byte pad after i8)
```

Host runtime uses `LLVM.OffsetOfElement(_targetData, ..., 39)` = 164 — correct.
But when instcombine runs, the module has no `target datalayout` line, so
instcombine falls back to LLVM's built-in default datalayout. That default
does not necessarily enforce 8-byte alignment for i64 (or computes packing
differently), and gets field 39 = 160.

In fact, grepping the ir_no_ic.ll / ir_af.ll header for `target datalayout`
or `target triple` returns nothing:
```
; ModuleID = 'AprCpu_ARMv4T'
source_filename = "AprCpu_ARMv4T"
@host_swap_register_bank = ...   ← skips layout/triple entirely
```

The JIT path constructs datalayout this way (HostRuntime.cs before fix):
```csharp
public void Compile() {
    BindUnboundExternsToTrap(_initialModule);
    RunOptimizationPipeline(_initialModule);  // ← module has no datalayout here

    // ... after building LLJIT, get layout from OrcLLJITGetDataLayoutStr
    var dlStrPtr = LLVM.OrcLLJITGetDataLayoutStr(_lljit);
    _targetData = LLVMTargetDataRef.FromStringRepresentation(dlStr);
    StateSizeBytes = LLVM.SizeOfTypeInBits(_targetData, Layout.StructType) / 8;
}
```

`OffsetOfElement(_targetData, ...)` uses the right layout, but instcombine
inside `RunOptimizationPipeline` runs while the module has no layout — both
sides disagree.

### 1.3 Why didn't mem2reg/gvn/dse/simplifycfg trigger this?

Those 4 passes do not rewrite struct GEP into byte GEP — they preserve
struct GEP form. Struct GEP in LLVM IR is "structural reference" (symbolic
field 39), independent of layout. The actual byte offset is computed at
codegen stage (by which point LLJIT has set datalayout, so it's correct).

Only instcombine **canonicalises** struct GEP into `i8` GEP — that's when
it depends on datalayout's alignment rules — and a module without layout
guesses wrong.

### 1.4 Misleading lead: earlier hypothesis was wrong

In `202605031900-Ha-llvm-pass-pipeline-reenabled.md` it was guessed to be
`switch i32 poison` UB-propagation. First tried adding `BuildFreeze` in
`RestoreCpsrFromSpsr` to block poison — **no effect at all**, T1 still
failed exactly the same 3 tests. Only after dumping IR was the GEP offset
issue found, with no relation to switch / poison whatsoever. The freeze
patch was rolled back.

---

## 2. Fix

`HostRuntime.cs`:

1. **Compile() reordered**: build LLJIT first to get datalayout, **set
   datalayout on module first, then run RunOptimizationPipeline**, finally
   hand to JIT.
2. **Remember `_dataLayoutStr`**: `AddModule()` (per-block JIT module path)
   also needs SetDataLayout before running the pipeline, for consistency.
3. **Default pass list adds instcombine back**:
   `mem2reg,instcombine<no-verify-fixpoint>,gvn,dse,simplifycfg`

```csharp
// new helper
private static void SetModuleDataLayoutString(LLVMModuleRef module, string dlStr)
{
    if (string.IsNullOrEmpty(dlStr)) return;
    var bytes = System.Text.Encoding.ASCII.GetBytes(dlStr + "\0");
    fixed (byte* p = bytes) LLVM.SetDataLayout(module, (sbyte*)p);
}
```

---

## 3. Tier 4 QA results

| Stage | Result |
|---|---|
| T1 360 unit tests | ✅ 360/360 pass |
| T2 8-combo screenshot matrix | ✅ all 8 = `7e829e9e837418c0f48c038341440bcb` |
| T3 3-run loop100 bench | ✅ all green (see below) |

### T3 bench (3-run avg, MIPS)

| combo | run1 | run2 | run3 | avg | baseline (H.a 4-pass) | Δ |
|---|---|---|---|---|---|---|
| arm pi   | 8.17  | 8.21  | 7.99  | 8.12 | 7.98  | +1.8% |
| arm bjit | 10.50 | 10.29 | 10.15 | 10.31 | 10.22 | +0.9% |
| thumb pi | 8.18  | 8.21  | 8.09  | 8.16 | 8.14  | +0.2% |
| thumb bjit | 11.35 | 11.63 | 11.31 | 11.43 | 11.54 | -1.0% |

- Discreteness: max-min < 3.5% per combo (< 5% noise band)
- No combo regressed > 5% rule

Gain is small (per-instr +0~2%, bjit ±1%) — similar to main note's own
admission of "+0.5-0.6%". instcombine's main benefit is cleaner codegen
stage (less redundant mov/and/select), with limited impact on hot loop.
The point is **correctness restored** — no longer needs env-gating to
bisect this 4-byte GEP miscompile.

---

## 4. Alignment status with main

At this point recovery branch is fully aligned with main on H.a (both
include instcombine). The only remaining feature gap:

- C.b lazy flag — recovery defer (see 202605031930-Cb-lazy-flag-deferred.md),
  gain <1% not worth the complexity

Force-merging recovery → main next should not introduce feature regression
(recovery is strictly ahead).

---

## 5. Lessons learned

1. **Dumping IR for with/without comparison is the fastest root-cause
   path** — earlier freeze attempt was hypothesis-driven, wasted a
   build/test cycle. Direct dump comparison spotted the 4-byte offset
   diff immediately.
2. **Module without datalayout is a latent bug**: it had been working
   because LLJIT injects layout itself at codegen, so non-instcombine
   path still worked. But any IR-level pass that depends on layout will
   trip. **Before adding a pass, confirm the module has datalayout**.
3. **`switch i32 poison` is a common red herring**: a switch in a dead
   BB looks like UB to instcombine, but in practice LLVM's poison
   propagation in dead BBs is limited (especially since switches are
   usually simplified away by `block-unreachable` first). Don't guess
   this first when debugging.
