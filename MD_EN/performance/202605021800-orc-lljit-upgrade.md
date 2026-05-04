# A.3 ORC LLJIT upgrade — perf-neutral infrastructure swap

> **Goal is not perf**: A.3 is the enabler for block-JIT (A.4–A.7). MCJIT's
> `Compile()` finalizes the entire module, after which no new function can be
> added. Block-JIT cache miss must lazy-compile a new block and add it into the
> live JIT — with MCJIT each block would need a separate engine (per-block JIT)
> which is expensive. ORC LLJIT's `OrcLLJITAddLLVMIRModule` can be called
> repeatedly against the main JITDylib, and cross-module symbol resolution is
> handled automatically.
>
> **Result**: 351/351 unit tests + Blargg cpu_instrs 11/11 all green; perf
> within ±2% noise vs MCJIT (GBA +0.5–0.7%, GB JIT +1.8%) — a pure
> infrastructure swap with no perf regression and no perf gain. Next step
> A.4 then has the ORC module-add API available.

---

## 1. Results (multi-run avg)

| ROM                     | Backend     | runs | min  | **avg** | max  | C.b retry baseline (MCJIT) | **Δ vs MCJIT** |
|-------------------------|-------------|------|-----:|--------:|-----:|----------------------------:|---------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 8.36 | **8.55**| 8.63 | 8.49                        | +0.7% (noise)  |
| GBA thumb-loop100.gba   | json-llvm   | 4    | 8.46 | **8.55**| 8.61 | 8.51                        | +0.5% (noise)  |
| GB 09-loop100.gb        | legacy      | 3    | 32.05| **32.49**|32.92| 31.66                       | +2.6% (noise)  |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.59 | **6.64**| 6.71 | 6.52                        | +1.8% (noise)  |

GB legacy +2.6% is noise — A.3 doesn't affect the legacy backend, just timing variation.

Setup time (spec compile + JIT engine init): MCJIT 322ms → ORC LLJIT
~317ms — also within noise.

---

## 2. Why switch to ORC LLJIT

### 2.1 MCJIT structural limitations

MCJIT's working model is "finalize the entire module at once":

```
LLVMCreateMCJITCompiler(out engine, module, options);
// Module then becomes read-only — any AddFunction won't reach JIT'd code.
addr = LLVMGetFunctionAddress(engine, "name");
```

For per-instruction JIT this is fine: at spec-compile time emit all instruction
functions into the same module, Compile() codegens everything in one shot,
later just lookup.

For block-JIT this is a hard blocker: each hot block is only known after
detection. Cache miss → emit block IR → add to JIT → get fn ptr →
jump there to execute. MCJIT has no add-after-compile API.

### 2.2 ORC LLJIT model

ORC's working model is "JITDylib + module add":

```
OrcCreateLLJIT(&lljit, builder);                     // build engine
OrcLLJITAddLLVMIRModule(lljit, mainJD, tsm1);        // add module 1
OrcLLJITAddLLVMIRModule(lljit, mainJD, tsm2);        // add module 2 (any time)
OrcLLJITLookup(lljit, &addr, "name");                // look up symbol from any module
```

Key properties:
- Multiple `AddLLVMIRModule` calls against the same JITDylib coexist in a
  single symbol namespace
- Cross-module symbol references resolve automatically (a block module can
  call helper symbols defined in the initial module — no manual link needed)
- Internally uses `LazyCallThroughManager` for on-demand materialization (we
  don't leverage this yet, but future tier compilation could)

### 2.3 Why perf-neutral

We're still per-instruction JIT — emit all spec functions into a single module,
hand to ORC for one-shot compile. For this case ORC and MCJIT use the same
codegen pipeline, so perf differences only come from engine overhead
(dispatch / lookup), in the ms range — not measurable at the MIPS level.

After A.4 adds block-JIT, blocks share spec function symbols, and ORC's
lazy + cross-module resolution will pay off.

---

## 3. Changes

### 3.1 `src/AprCpu.Core/Runtime/HostRuntime.cs` rewrite (~290 lines)

**Kept**:
- `Build / BindExtern / Compile / GetFunctionPointer` API unchanged
- inttoptr-globals extern binding (engine-agnostic)
- BindUnboundExternsToTrap (trap stub for unwired externs)
- RunOptimizationPipeline (H.a's mem2reg/instcombine/gvn/dse/simplifycfg)
- All field offset accessors

**Changed**:
- Engine creation: `OrcCreateLLJITBuilder` → `OrcCreateLLJIT(&lljit, builder)`
- Get JITDylib main: `OrcLLJITGetMainJITDylib(lljit)`
- TargetData via `OrcLLJITGetDataLayoutStr` → `LLVMTargetDataRef.FromStringRepresentation`
- Adding module to JIT: single ThreadSafeContext + `OrcCreateNewThreadSafeModule`
  → `OrcLLJITAddLLVMIRModule`
- Lookup: `OrcLLJITLookup(lljit, &addr, name)` replaces `engine.GetFunctionAddress`
- Init: removed `LinkInMCJIT()` (ORC doesn't need it — LLJITBuilder selects its own object linking layer)
- Dispose: `OrcDisposeLLJIT` + `OrcDisposeThreadSafeContext`

**Added**:
- `AddModule(LLVMModuleRef)` — post-Compile add a module to the live JIT.
  Runs BindUnboundExternsToTrap + RunOptimizationPipeline, then wraps it
  into a TSM and adds to LLJIT. Paves the way for A.4 block-JIT cache-miss path.
- `ThrowIfError / ExtractAndDispose` — helpers for LLVM error messages

### 3.2 `src/AprGba.Cli/Program.cs` + `src/AprGb.Cli/Program.cs`

Setup-time message string: `MCJIT` → `ORC LLJIT`. Pure cosmetic, honestly
labels the backend.

---

## 4. Why we can keep the inttoptr-globals pattern

ORC provides `OrcAbsoluteSymbols` + `OrcJITDylibDefine` to register a
trampoline function pointer directly as a JIT symbol — this is the
"canonical" way to inject externs. We **didn't adopt** it because:

1. **Engine-agnostic, known to work**: inttoptr globals work in both MCJIT
   and ORC. AbsoluteSymbols is ORC-only.
2. **Don't have to redo the Phase 5.7 Windows COFF / RIP-relative load
   workarounds**: the existing setup already avoids all known landmines.
3. **Bind happens earlier — at the IR level**: the caller's mental model
   is "modify the IR", not "inject a symbol". During code review / debug,
   dumping IR shows the trampoline address directly.

Future A.4 block modules, if they need to call any "new trampoline not in
the initial module" (unlikely — blocks only call memory bus + bank swap,
which the initial module already declares), can reconsider AbsoluteSymbols.

---

## 5. Verification

```
$ dotnet test AprGba.slnx
Passed! - Failed: 0, Passed: 351, Skipped: 0, Total: 351

$ dotnet src/AprGb.Cli/.../apr-gb.dll --cpu=json-llvm \
    --rom=test-roms/blargg-cpu/cpu_instrs.gb --frames=12000
01:ok  02:ok  03:ok  04:ok  05:ok  06:ok  07:ok  08:ok  09:ok  10:ok  11:ok
Passed all tests
```

351 unit tests + Blargg cpu_instrs 11 sub-tests all green = ORC LLJIT
correctly codegens + dispatches both ARM7TDMI + LR35902 specs.

---

## 6. Next step

A.3 done → A.4 (code cache, hashmap PC → block fn pointer) is unblocked.
A.4 flow:

1. Block-JIT executor: `if (cache.TryGet(pc, out fn)) jump fn(state);`
2. Miss path: `block = detector.Detect(bus, pc); fresh module; bfb.Build(...); rt.AddModule(module); fn = rt.GetFunctionPointer(BlockFunctionName(...)); cache.Add(pc, fn);`
3. LRU eviction when size > N (TBD threshold)

A.6 (indirect branch dispatch) chains with A.4 — block exit writes PC,
dispatcher uses PC to look up cache.

---

## 7. Related documents

- `src/AprCpu.Core/Runtime/HostRuntime.cs` — rewritten source
- [`MD_EN/design/03-roadmap.md`](/MD_EN/design/03-roadmap.md) Phase 7 A.3 — marked done
- [`MD_EN/performance/202605030241-cb-alloca-shadow-retry.md`](/MD_EN/performance/202605030241-cb-alloca-shadow-retry.md) — last MCJIT-era
  perf change (A.3's baseline)
