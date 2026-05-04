# Phase 7 C.b attempt post-mortem — full lazy flag design conflicts with SSA dominance

> **Status**: **Not committed, working tree changes reverted**.
>
> Tried adding a `PendingFlagWrites` deferred-batch mechanism in EmitContext
> to merge consecutive SetStatusFlag calls per status register into a single
> read-modify-store. The concept is correct, the code was written and
> compiled, but 5/345 unit tests failed when run. The root cause is a
> conflict with LLVM IR SSA dominance rules.
>
> **Lesson**: lazy flag is not a purely local "add a batch dictionary"
> change. SSA values across basic blocks need alloca-based shadow + LLVM
> mem2reg to correctly handle control flow.
>
> This file leaves a design record + failure diagnosis for future redo
> reference.

---

## 1. Attempted design

```csharp
// Added to EmitContext:
public Dictionary<string, FlagWriteBatch> PendingFlagWrites { get; }
    = new(StringComparer.Ordinal);

public sealed class FlagWriteBatch {
    public Dictionary<int, LLVMValueRef> NewBits { get; } = new();
    public void SetBit(int bitIndex, LLVMValueRef i1Value) { ... }
}

// CpsrHelpers changes:
public static void SetStatusFlag(...)
    => DeferFlagWrite(...);  // does not emit IR directly, stores into dict

public static LLVMValueRef ReadStatusFlag(...) {
    FlushFlagWritesForReg(ctx, register);  // drain before read
    // original read logic
}

public static void FlushFlagWritesForReg(EmitContext ctx, string register) {
    if (no pending) return;
    var prev = load status_reg
    var combined = prev
    foreach (bitIndex, i1Value in batch.NewBits) {
        combined = (combined & ~mask) | (zext(i1Value) << bitIndex)
    }
    store combined to status_reg
}

// Added to InstructionFunctionBuilder:
// before BuildRetVoid:
CpsrHelpers.FlushAllFlagWrites(ctx);
```

Design looks clean. Original expectation:
- 4 consecutive update_nz / update_c_add / update_v_add → 4 SetStatusFlag
  → all written into batch dict → end-of-fn one read-modify-store
- LLVM sees a single store, no need to do cross-emitter fusion

---

## 2. Why it failed: SSA dominance conflict

Running `dotnet test`: 5/345 failed, including `MOV_LSL_Immediate_NoFlagUpdate`,
`JsmolkaArmGba_AllTestsPass_R12IsZero`, and two SpecCompiler compilation tests.

LLVM Verify complained:

```
Instruction does not dominate all uses!
```

**Root cause**:

The ARM7TDMI spec uses conditional execution; each instruction's wrapper is:

```llvm
entry:
  ; cond gate evaluates CPSR
  br_cond %should_execute, body, ret_skip
body:
  ; instruction steps
  ; ... step1 ...
  ; ... step2: SetStatusFlag → batch.SetBit(bit_idx, %i1_v_step2)
  br ret_skip   ; <-- implicit fall-through to ret
ret_skip:
  ret void
```

C.b emits `FlushAllFlagWrites` before `BuildRetVoid`, but "before" means
the position is in ret_skip block. Stores emitted inside flush would use
`%i1_v_step2`, an SSA value from the body block.

**ret_skip is not in body's dominance frontier** (cond gate may skip body
straight to ret_skip) — so `%i1_v_step2` does not dominate the flush's
store position. LLVM Verify rejects.

In practice, internal emitters like `if`/`branch_cc` also create sub-blocks,
making deferred SSA values cross blocks relative to flush location. The
problem is similar.

---

## 3. The correct solution (not done this round): alloca-based shadow

LLVM idiom for cross-BB SSA: use alloca as mutable scalar storage, then
run mem2reg pass at the end.

```llvm
entry:
  %cpsr_shadow = alloca i32
  %cpsr_init = load i32, %cpsr_real_ptr
  store i32 %cpsr_init, %cpsr_shadow
  br_cond ..., body, exit

body:
  ; SetStatusFlag(N, %v):
  %prev = load i32, %cpsr_shadow
  %cleared = and %prev, mask_~N
  %shifted = shl (zext %v to i32), n_bit
  %new = or %cleared, %shifted
  store i32 %new, %cpsr_shadow
  ; ... same pattern for Z, C, V ...
  br exit

exit:
  ; FlushAll:
  %final = load i32, %cpsr_shadow
  store i32 %final, %cpsr_real_ptr
  ret void
```

Run `mem2reg` pass:
- alloca disappears, shadow becomes SSA
- store-load sequences fold into an SSA value chain
- GVN/DSE merge the mask-or chain into a single final value
- ends as 0-1 stores of CPSR (DSE may even drop the store if final equals init)

**Benefit**: mem2reg automatically handles PHI nodes for cond branches
(this is the purpose of mem2reg). **SSA dominance is guaranteed by the
alloca mechanism — any block can load/store**.

---

## 4. Why the alloca approach was not done this time

Time / risk assessment:

1. **Alloca approach effort** ≈ 200-300 lines + need to audit every direct
   read of CPSR/F/SPSR and switch to shadow (e.g. raise_exception /
   write_psr / restore_cpsr_from_spsr / ReadCarryIn / DAA / ConditionEvaluator)
2. **Verification cost**: 345 unit + 9 Blargg + jsmolka arm/thumb/bios
   3 ROMs all need re-running for verification; each false negative needs
   debugging the LLVM IR
3. **Expected gain**: based on C.a's +2.7%, full lazy flag is estimated
   +5-15%. For GBA arm 8.26 MIPS that's ~0.4-1.2 MIPS gain — still 1.9-2.0x
   real-time
4. **Compared with other steps**: A (block-JIT) is expected to deliver
   +500-1000% in one step

**Cost-benefit asymmetric**: alloca-based lazy flag is high risk medium
gain. block-JIT is high complexity high gain. Comparatively block-JIT is
more worthwhile.

---

## 5. Guidance for future work

If full lazy flag is to be done in the future, follow these steps:

### 5.1 Design alloca shadow

- Add `Dictionary<string, LLVMValueRef> StatusShadowAllocas` to EmitContext
- Helper `GetOrCreateShadow(ctx, "CPSR") → alloca ptr`:
  - First call: insert alloca + load CPSR + store shadow into entry block,
    cache into dict
  - Subsequent calls: return cached
- `SetStatusFlag` → load shadow, mask-or, store shadow
- `ReadStatusFlag` → load shadow

### 5.2 Handle raw CPSR access

Need to audit every `GepStatusRegister(...)` caller that reads/writes raw CPSR:
- `read_psr`, `write_psr`: drain shadow before reading raw
- `raise_exception`, `restore_cpsr_from_spsr`: drain shadow + invalidate
- `ReadCarryIn` (LR35902 ADC carry): drain F shadow first
- `Lr35902DaaEmitter`: drain F shadow before reading

Each location needs `DrainAndInvalidateShadow(ctx, "CPSR" or "F")`.

### 5.3 End-of-fn drain

InstructionFunctionBuilder.Build adds `DrainAllShadows(ctx)` before
BuildRetVoid — load shadow, store to real status reg.

### 5.4 Confirm mem2reg runs

May need to add `PassManager.AddPromoteMemoryToRegisterPass()` to LLVM pass
manager to explicitly run mem2reg. OptLevel=3 (Phase 7 B.a) should already
include it, but it needs IR-dump verification.

### 5.5 Verification

For every emitter / spec change, run 345 unit + 9 Blargg + jsmolka arm.gba
+ jsmolka thumb.gba + jsmolka bios.gba. Any test regression triggers
stop-the-line.

---

## 6. Conclusion

C.a (width-correct flag access) is the baby step of lazy flag, already
committed (`d1f1a1d`), gave GB JIT +2.7%.

C.b (full lazy flag via deferred batch) **attempt failed**, reverted.
Working tree restored to C.a state.

**Suggested next step**:
- Go directly to A (block-JIT), gain far exceeds the C lazy flag series
- Or first declare Phase 7 "saturated" and proceed to 5.9 third CPU port
- Leave alloca-based true lazy flag for the future (only if commercial
  ROM smoothness really becomes a bottleneck)

---

## 7. Change details (already reverted)

```
src/AprCpu.Core/IR/EmitContext.cs   — added PendingFlagWrites + FlagWriteBatch
src/AprCpu.Core/IR/Emitters.cs      — CpsrHelpers changed to deferred (DeferFlagWrite/FlushFlagWritesForReg/FlushAllFlagWrites)
src/AprCpu.Core/IR/InstructionFunctionBuilder.cs  — FlushAll before BuildRetVoid

git checkout -- restored working tree. 345/345 unit tests green again.
```

---

## 8. Related docs

- `MD/performance/202605030135-width-correct-flag-access.md` — C.a (committed)
- Other Phase 7 perf notes
- `MD/design/03-roadmap.md` Phase 7 — C section kept as "incomplete — true lazy flag"
