# Phase 7 C.b lazy flag retry — **deferred** (correctness regression in BJIT/BIOS-LLE)

> **Strategy**: Use the alloca-shadow design from main `18051f6` as a
> blueprint and re-implement on the recovery branch. Expected gain:
> +0.5-0.6% MIPS (per main note).
>
> **Result**: Full 4-site changes implemented (`EmitContext.StatusShadowAllocas`/
> `EntryBlock`, `CpsrHelpers` SetFlag/ReadFlag/Drain/Reinit,
> `InstructionFunctionBuilder` end-of-fn drain, `ArmEmitters`
> drain+reinit pattern in WritePsr/RestoreCpsr/RaiseException). **360
> unit tests pass**, but T2 (8-combo screenshot matrix) crashes multiple
> combos in the BJIT + BIOS LLE path:
>
> - `arm.gba HLE block-JIT`: outputs blank screen (not "All tests passed")
> - `arm.gba BIOS LLE per-instr`: `Undecodable instruction 0x00C0E7F6
>   at PC=0x00002D92 (ARM)` — CPSR.T misaligned on some control-flow path
> - `arm.gba BIOS LLE block-JIT`: `Block must contain at least one
>   instruction` — block detector hits the undecodable region
> - `thumb.gba BIOS LLE` both modes: similar crash
>
> **Decision**: **Revert all C.b changes** (per Tier 3 QA workflow:
> T2 fail = no commit allowed). Restore to the clean state of H.a 4-pass set.

---

## 1. Attempted design (already reverted)

```
EmitContext.cs:
  + Dictionary<string, LLVMValueRef> StatusShadowAllocas
  + LLVMBasicBlockRef EntryBlock (captured in ctor)

Emitters.cs CpsrHelpers:
  ~ SetStatusFlagAt: writes shadow alloca (was real CPSR ptr)
  ~ ReadStatusFlag: reads shadow if exists, else real
  + GetOrCreateShadow: alloca + init at entry block (dominance)
  + DrainShadow: shadow → real
  + ReinitShadowFromReal: real → shadow (after raw write)
  + DrainAllShadows: drain all (called by builders before ret)

ArmEmitters.cs:
  + In WritePsr / RestoreCpsrFromSpsr / RaiseExceptionEmitter:
      DrainShadow(CPSR);    // step 1
      <raw CPSR write>      // step 2
      ReinitShadowFromReal(CPSR);  // step 3

InstructionFunctionBuilder.cs:
  + DrainAllShadows before BuildRetVoid (both normal + cond-skip path)

BlockFunctionBuilder.cs (already had):
  + DrainAllShadows in block_exit BB
```

---

## 2. Why it crashed (root cause hypothesis)

Shadow alloca must be in the entry block (LLVM mem2reg dominance requirement).
But the init load (= first read of real CPSR) also runs in the entry block
along with alloca — i.e. snapshotting "entry-time real CPSR" into shadow.

When an instruction body mid-way performs `MSR CPSR_c, ...` (raw CPSR write):
1. DrainShadow → writes shadow with pending bits back to real (containing
   entry-time value + pending flag bits)
2. WritePsr uses mask-and-or to overwrite real
3. ReinitShadowFromReal → loads post-write real into shadow ✓

Issue: **ReinitShadowFromReal is a mid-body store to shadow**. mem2reg
sees the alloca's value being written in multiple BBs (entry init store +
body reinit store + various SetFlag stores). It needs PHI to reconcile.

If a control-flow path goes from entry → cond-skip-endBlock (skipping body),
on that path shadow value = entry-init = entry-time real CPSR. Drain at
end writes entry-time CPSR back to real → overwrites the value after raw
write (if some other path modified it).

Worse: alloca becomes SSA after promotion, and the value at each BB
requires mem2reg to compute PHI. If PHI computation goes wrong (especially
the mix of reinit-after-MSR and cond-skip paths), wrong shadow value gets used.

The observed symptom supports this hypothesis: CPSR.T misaligned → CPU
uses wrong instruction-set decoder → gets undecodable bytes.

**Correct design probably needs:**
- Reinit shadow at every BB entry (implicit PHI), but high cost
- Or do not use alloca shadow, switch to explicit SSA value chain (the
  C.b first-attempt failure path, with dominance conflict)
- Or just give up — GVN+DSE is already doing most of the same work on raw
  CPSR pattern (the main commit itself admits "+0.5-0.6%" is small)

---

## 3. Why doesn't main branch (`18051f6`) have this issue?

Possible differences:
1. main does not have the Phase 1a/1b cycles_left + budget exit mechanism
   (extra BBs increasing control-flow complexity)
2. main has no block-JIT multi-instr block (per-instr functions are small,
   shadow pattern is simple)
3. main's BIOS LLE path is different (recovery fixed multiple A.6.1 bugs,
   main is still broken; baselines differ)

Main may also have a latent bug not caught by its test ROM coverage.

---

## 4. Decision + follow-up

**Defer C.b retry**. Reasons:
- Gain <1% MIPS (per main note self-attestation)
- GVN+DSE enabled in H.a → "merge N consecutive writes" gain on raw CPSR
  pattern is already realised
- Correct implementation is too complex (need to handle mem2reg PHI across
  raw-write dominance)
- Recovery branch's block-JIT IR structure (Phase 1a budget BB + multi-instr
  block) differs from main, main's C.b design is not directly applicable

**Keep environment (env-gated debug)**: in the future if perf measurements
show SetStatusFlag/ReadStatusFlag are hot paths (currently not), revisit
the redesign.

---

## 5. Tier 3 QA result

| Stage | Result |
|---|---|
| T1 360 unit tests | ✅ 360/360 pass |
| T2 8-combo screenshot | ❌ 5/8 fail (HLE BJIT 1 blank, 4 BIOS LLE crash) |
| T3 3-run loop100 bench | (not run — T2 fail aborted directly) |

**Post-revert state (HEAD = 1a9b908)**: T1 + T2 + T3 all green (per
202605031900-Ha-llvm-pass-pipeline-reenabled.md)

---

## 6. Related docs

- `MD/performance/202605030148-lazy-flag-attempt-postmortem.md` — first
  C.b attempt (deferred-batch + SSA dominance crash)
- `MD/performance/202605031900-Ha-llvm-pass-pipeline-reenabled.md` — H.a
  4-pass set re-enabled, current baseline
- main `18051f6` — main branch's C.b retry, **structure not suitable for
  direct cherry-pick to recovery branch**
- `MD/process/01-commit-qa-workflow.md` — this round used Tier 3 workflow,
  T2 fail aborted + reverted per rule
