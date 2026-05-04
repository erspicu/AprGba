# Phase 2.6: Framework Generalisation Refactor

> **Status: Complete** (R1-R5 all implemented during Phase 2.5)
>
> The **real validation** of the R1-R5 promise — "swapping CPUs only
> requires swapping JSON" — is moved to Phase 4.5 (GB LR35902 port).
> See [`MD_EN/design/09-gb-lr35902-validation-plan.md`](/MD_EN/design/09-gb-lr35902-validation-plan.md).
>
> The document below is preserved as a **historical plan doc**, kept for
> reference structure for similar refactors in the future.

---

## Why insert this between 2.5.2 and 2.5.3

After Phase 2.5.2, parser/emitter already covered the complete ARM Data
Processing set (48 ALU + 3 PSR instructions). Pushing forward to 2.5.3
(memory transfer) would add more emitters and operand resolvers — **the
coupling surface keeps growing**. Now, while the footprint is still
small (~14 emitters, 4 resolvers), do the generalisation refactor.

If we delay until the end of 2.5, the number of emitters needing rewrite
more than doubles, and rewriting is more likely to introduce
regressions.

## Goal

Make `AprCpu.Core`, without modifying any C# code, drivable by different
CPU specs:
- Swap `cpu.json` + `<set>.json` to swap CPU architecture (provided the
  micro-op / operand resolver vocabulary is sufficient)
- Architecture-specific emitters can still be registered as plug-ins
  (no need to modify framework code, only call the corresponding
  RegisterAll in SpecCompiler at startup)

## Scope: 5 refactors

| ID | Content | Effort | Order dependency |
|---|---|---|---|
| R1 | CpuStateLayout switches to dynamic construction from `register_file` | ~1 day | First |
| R2 | Flag bit positions look up from `register_file.status[].fields` | ~0.5 day | Needs R1 |
| R3 | OperandResolver switches to registry pattern (mirroring emitter) | ~0.5 day | Independent |
| R4 | Condition gate becomes data-driven from `global_condition.table` | ~1 day | Needs R2 |
| R5 | ARM-specific emitters move to a separate class; generic parts stay in StandardEmitters | ~0.5 day | Last |

Total ~3.5 days of focused work.

## Invariant contract

After each R completes:
- All existing tests (64) must still be green
- The CLI `aprcpu --spec spec/arm7tdmi/cpu.json --output temp/arm7tdmi.ll`
  must still emit 62 functions, 0 diagnostics, and pass LLVM TryVerify
- The IR structure of the produced `.ll` may differ slightly (e.g. GEP
  index changes), but semantics are identical

## R1: CpuStateLayout data-driven

### Current state

`CpuStateLayout.cs`'s constructor only takes `LLVMContextRef`, with 39
hardcoded fields internally (R[16] + CPSR + 5 SPSR + 5 banked groups +
cycle + pending).

### After

```csharp
public sealed unsafe class CpuStateLayout
{
    public CpuStateLayout(LLVMContextRef ctx,
                          RegisterFile registerFile,
                          ProcessorModes? modes);

    public LLVMTypeRef StructType { get; }
    public LLVMTypeRef PointerType { get; }

    public LLVMValueRef GepGpr(LLVMBuilderRef b, LLVMValueRef state, int idx);
    public LLVMValueRef GepGprDynamic(...);
    public LLVMValueRef GepStatusRegister(LLVMBuilderRef b, LLVMValueRef state, string name);
    public LLVMValueRef GepBankedGpr(LLVMBuilderRef b, LLVMValueRef state, string mode, int idxInGroup);
    public LLVMValueRef GepCycleCounter(...);

    // For external use (e.g. flag-bit lookup helpers):
    public StatusRegister GetStatusRegisterDef(string name);
}
```

LLVM struct order:
1. GPRs (per spec `general_purpose.count`)
2. Each status register (in order of spec `status[]`)
3. Each banked GPR group (per `processor_modes.banked_registers`)
4. The "emulator-internal" fixed suffix: cycle_counter (i64),
   pending_exceptions (i32)

### Affected scope

- `CpuStateLayout.cs`: rewrite
- `SpecCompiler.cs`: pass in `loaded.Cpu.RegisterFile` + `ProcessorModes`
  when constructing layout
- `Emitters.cs`, `OperandResolvers.cs`: change `Layout.GepCpsr()` to
  `Layout.GepStatusRegister("CPSR")` (API renamed)
- `CpuStateLayout.CpsrBit_*` constants: keep temporarily to maintain
  compilation, removed by R2

### Risks

- If dynamic struct order is inconsistent with R2's bit-position lookup,
  errors result
- Mitigation: at construction time, validate status register width and
  field coverage range

## R2: Flag bit positions looked up from spec

### Current state

`Emitters.cs` writes to CPSR using constants like
`CpuStateLayout.CpsrBit_N` (= 31).

### After

Add helper:

```csharp
public static class CpsrFlags
{
    public static int GetBitIndex(CpuStateLayout layout, string register, string flag);
    // e.g. (layout, "CPSR", "N") -> 31, lookup via RegisterFile.Status["CPSR"].Fields["N"]
}
```

Emitters like `update_nz` change to:
```csharp
var nBit = CpsrFlags.GetBitIndex(ctx.Layout, "CPSR", "N");
CpsrHelpers.SetCpsrBit(ctx, nBit, ...);
```

The `CpuStateLayout.CpsrBit_*` constants are removed.

### Affected scope

- `IR/Emitters.cs`: all `update_*` emitters change to lookup
- `IR/OperandResolvers.cs`: `CarryReader.ReadCarryIn` changes to lookup
- New `IR/CpsrFlags.cs` helper

## R3: OperandResolver Registry

### Current state

`OperandResolvers.Apply(ctx)` contains a `switch (resolver.Kind)`;
adding a case is hard-coded.

### After

```csharp
public interface IOperandResolver
{
    string Kind { get; }
    void Resolve(EmitContext ctx, string operandName, OperandResolver resolver);
}

public sealed class OperandResolverRegistry { ... }
```

`SpecCompiler` at startup:
```csharp
var resolverReg = new OperandResolverRegistry();
StandardOperandResolvers.RegisterAll(resolverReg);  // pc_relative_offset, register_direct
ArmOperandResolvers.RegisterAll(resolverReg);       // immediate_rotated, shifted_register_*
```

### Affected scope

- New `IR/IOperandResolver.cs`, `IR/OperandResolverRegistry.cs`
- Refactor `IR/OperandResolvers.cs`: split the 4 existing resolvers into
  corresponding classes
- `SpecCompiler.cs`: build the registry and pass it to emit context

## R4: Condition Gate data-driven

### Current state

`InstructionFunctionBuilder.EmitConditionGate` hardcodes AL / EQ / NE,
defaulting other conds to false.

### After

Create `IR/ConditionEvaluator.cs`. From
`InstructionSetSpec.GlobalCondition.Table` (e.g.
`{ "0000": "EQ", "0001": "NE", ... }`), read each cond mnemonic, then
look up CPSR flags by mnemonic.

```csharp
public static class ConditionEvaluator
{
    public static LLVMValueRef EmitCheck(EmitContext ctx, GlobalCondition gc);
}
```

Internally maps `EQ` -> `CPSR.Z == 1`, `NE` -> `CPSR.Z == 0`,
`CS/HS` -> `CPSR.C == 1`, ... 14 entries complete. AL = always true,
NV = always false.

### Affected scope

- New `IR/ConditionEvaluator.cs`
- `InstructionFunctionBuilder.EmitConditionGate` simplified to call the
  evaluator

## R5: ARM-only emitter split out

### Current state

`StandardEmitters.RegisterAll` registers all emitters, mixing
ARM-specific and generic ops.

### After

Split into:
- `StandardEmitters.RegisterAll`: generic ops (add/sub/and/or/xor/shl/
  lsr/asr/mvn/bic/rsb/load/store/branch/branch_link/if/...)
- `ArmEmitters.RegisterAll`: ARM-specific (branch_indirect with T-bit,
  restore_cpsr_from_spsr, adc/sbc/rsc/update_c_*_carry/read_psr/
  write_psr)

`SpecCompiler` selects extra registration by `architecture.family`:
```csharp
StandardEmitters.RegisterAll(reg);
if (loaded.Cpu.Architecture.Family == "ARM")
    ArmEmitters.RegisterAll(reg);
```

### Affected scope

- Split `IR/Emitters.cs` into `Emitters/StandardEmitters.cs` +
  `Emitters/ArmEmitters.cs`
- `SpecCompiler.cs`: family-based registration

## Done criteria (achieved)

- [x] All existing tests green (64 at the time -> 159 at the end of
      Phase 2.5)
- [x] CLI on the ARM7TDMI spec still emits the complete instruction set
      (44 ARM mnemonics + ~30 Thumb mnemonics), 0 diagnostics
- [x] No `Cpsr` / `Spsr` string hardcoded inside `CpuStateLayout`
- [x] `OperandResolvers` and `Emitters` each have their own ARM-specific
      file (`ArmEmitters`, `ArmOperandResolvers`); generic files are
      directly reusable from non-ARM specs
- [x] Conditional gate behaves correctly for all 14 cond codes
- [ ] **Actually run another CPU's spec through the pipeline** — this
      item is moved to Phase 4.5 (GB LR35902 port); see
      `09-gb-lr35902-validation-plan.md`

## Hand-off

After completion, return to Phase 2.5.3 (ARM memory transfer) and
continue completing the full ARM7TDMI spec. The Phase 2.6 refactor does
not affect the time estimate of subsequent sub-phases — emitters /
resolvers added after 2.5.3 will naturally land in the correct location
(generic vs arm-specific) without needing further refactoring.
