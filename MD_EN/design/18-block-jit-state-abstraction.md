# Block-JIT state abstraction — alloca + mem2reg

> **Status (2026-05-09 update — N1 closeout)**: design + implementation +
> verification all complete. All three backends (legacy / json per-instr /
> json-block) pass nestest + blargg cpu_test5; the GB block-JIT runs on the
> new framework with no regression. Perf measurement is at
> `MD/performance/202605091559-nes-blockjit-vs-perinstr.md`.
>
> **B'.7 audit conclusion (addendum)**:
> The existing `PipelinePcConstant` / `CurrentInstructionBaseAddress` paths
> are **not legacy** — they are block-JIT-only "compile-time-known PC"
> markers that enable three optimizations **orthogonal** to alloca+mem2reg:
>
> 1. **Compile-time imm extraction** (`FetchImmediate` Lr35902:798): the imm8
>    is shift-extracted directly from the `ctx.Instruction` instruction word,
>    **skipping the bus.ReadByte extern call**. For block-JIT this saves one
>    indirect call per ALU+imm8 instruction.
> 2. **WRAM/HRAM region inline** (`EmitWriteByteWithSyncAndRamFastPath`
>    Lr35902:1141): in block-JIT mode bus.WriteByte does region check +
>    direct GEP store, skipping the extern call and the sync-flag mechanism
>    (per-instr mode does not need these).
> 3. **Sync-exit PC pre-write** (Lr35902:1345): in block-JIT, before a
>    mid-block ret, the next-PC is written as a const store + PcWritten=1,
>    handing control back to the outer loop.
>
> None of these can be **replaced by alloca+mem2reg** — alloca handles
> *state access*, while PipelinePcConstant handles *IR-shape variants*
> (when to bake a const, when to inline a region). Both together form the
> complete block-JIT toolbox.
>
> Therefore the B'.7 conclusion: **keep the PipelinePcConstant path as a
> first-class framework feature**; do **not** delete and do **not** deprecate.
> The future generalisation direction (filed as task #163) is to port the
> compile-time imm extraction into Mos6502Emitters as well, eliminating the
> bus extern call on each NES block-JIT imm fetch — a perf optimization, not
> a cleanup.
>
> Design phase (the historical content below is preserved). Current state:
> the LR35902 (GB) block-JIT has shipped; MOS6502 (NES) per-instr passes all
> ROMs. The attempt to wire the NES emitter onto block-JIT failed — exposing
> the implicit nature of the emitter-author contract.
>
> This doc proposes a framework-level fix: move the "per-instr vs block-JIT"
> distinction out of emitter code, and let `EmitContext` +
> `BlockFunctionBuilder` handle it automatically via the standard LLVM
> `alloca + mem2reg` pattern. **Emitters no longer need to know which mode
> they are in.**
>
> Design rationale:
> 1. Gemini consultation (2026-05-09), logged at
>    [`tools/knowledgebase/message/20260509_135453.txt`](/tools/knowledgebase/message/20260509_135453.txt)
> 2. QEMU TCG's Globals/Temps model
> 3. Existing codebase: `Lr35902Emitters` already uses `ctx.PipelinePcConstant`
>    + `ctx.CurrentInstructionBaseAddress` to be block-JIT-aware;
>    `Mos6502Emitters` does not — across the whole framework only one CPU
>    follows the contract, and the second one was forced to add a hack.
>
> **Goal**: the framework becomes a tool where "the emitter is written once,
> works in both per-instr and block-JIT". Adding a new CPU should not require
> rewriting the emitter for block-JIT.

---

## 1. Nature of the problem

### 1.1 Two modes, two PC-handling paths

| Mode | How PC is stored | How PC is read | How PC is written |
|---|---|---|---|
| **per-instr** | `state.PC` (memory) | `BuildLoad2(state + PC_off)` | `BuildStore(... state + PC_off)` |
| **block-JIT (LR35902 today)** | LLVM SSA register | `ctx.PipelinePcConstant` (compile-time const) | (lazy — only at block exit) |

Internally the LR35902 emitter branches on
`if (ctx.PipelinePcConstant is uint pc) ... else ...`.
**This is the contract** — the emitter must be aware of both paths.

The MOS6502 emitter does not do this — it always does
`BuildLoad2(state + PC_off)`. That works in per-instr mode, but in block-JIT
mode:
- PC is read/written from state for the entire block
- LLVM cannot see "PC is an i32 constant across instructions within the block"
- Cross-instruction CSE / DCE break down
- block-JIT runs as slowly as per-instr, sometimes slower (extra dispatch
  loop overhead)

### 1.2 Why the band-aid was wrong

The first attempt (now stashed on the
`nes-blockjit-bandaid-stash-202605091230` branch) added a
`BlockFunctionBuilder.PerInstructionPcPreWrite` flag — at the start of each
in-block instruction, write `state.PC = bi.Pc + 1` into the state buffer.

Problems:
1. **Performance**: one store per instr followed by an emitter load; LLVM
   cannot eliminate them (the state buffer is alias-uncertain memory, the
   store must be honored)
2. **Correctness**: emitters such as read_imm8 keep advancing PC (writing
   again). The pre-write only happens at the start of the instruction, but
   inside the emitter we may need "the PC value at the start of this instr"
   for some operation (e.g. BRK pushes PC+2); at that point we read "the
   PC after the previous instr's advance". blargg cpu_test5 fails
   empirically.

The band-aid **literally destroys the core advantage of block-JIT**
(PC living in SSA rather than memory), so even fixing the ordering would be
pointless.

### 1.3 The two real options

| Option | Change | LLVM optimization potential |
|---|---|---|
| **A. IStateContext refactor** | rewrite every emitter to use an abstract API; framework provides per-instr / block implementations | ✓ full |
| **B. alloca + mem2reg** (**chosen by this doc**) | not a single line of emitter is changed; the framework swaps the state pointer to an alloca in block mode | ✓ full (LLVM standard pass) |

A is more "architecturally pure", but it touches every emitter (~30 6502
ones + ~50 GB ones). B is the LLVM-idiomatic approach — `alloca`, then
`mem2reg` automatically promotes to SSA, equivalent to hand-written SSA.

---

## 2. Design: alloca + mem2reg

### 2.1 Mental model (mapping to QEMU TCG)

| QEMU concept | Our equivalent |
|---|---|
| Global (state struct field) | PC / GPR / status reg in the `state` buffer |
| Temp (in-block SSA value) | `alloca` (within a block); after mem2reg becomes a register |
| Sync (TCG global → memory) | at block exit / side-effect points, write the alloca back to the state buffer |

### 2.2 IR shape — before vs after

**Before (per-instr mode, or 6502 block-JIT band-aid):**
```llvm
define void @Execute_Block_Main_8000_g1(ptr %state, i32 %instr_word) {
entry:
  %pc_ptr = getelementptr i8, ptr %state, i64 32  ; PC offset
  %a_ptr  = getelementptr i8, ptr %state, i64 0   ; A offset

  ; instr 1 (LDA #$10):
  store i16 32769, ptr %pc_ptr      ; bandaid pre-write
  %pc1 = load i16, ptr %pc_ptr      ; read PC
  %imm_addr = zext i16 %pc1 to i32
  %imm = call i8 @memory_read_8(i32 %imm_addr)
  %pc1_inc = add i16 %pc1, 1
  store i16 %pc1_inc, ptr %pc_ptr   ; advance PC
  store i8 %imm, ptr %a_ptr         ; A := imm

  ; instr 2 (TAX):
  store i16 32770, ptr %pc_ptr      ; bandaid pre-write again
  %a2 = load i8, ptr %a_ptr
  store i8 %a2, ptr %x_ptr
  ret void
}
```

20 memory ops, none of which LLVM can eliminate.

**After (alloca + mem2reg):**

At build time:
```llvm
define void @Execute_Block_Main_8000_g1(ptr %state, i32 %instr_word) {
entry:
  ; --- prologue: allocate locals + load initial state ---
  %pc_local = alloca i16
  %a_local  = alloca i8
  %x_local  = alloca i8
  %p_local  = alloca i8
  %sp_local = alloca i8
  %pc_init = load i16, ptr (state + PC_off)
  store i16 %pc_init, ptr %pc_local
  %a_init  = load i8,  ptr (state + A_off)
  store i8 %a_init, ptr %a_local
  ; ... etc

  ; --- body: emitters use locals (via EmitContext redirect) ---
  ; instr 1 (LDA #$10):
  %pc1 = load i16, ptr %pc_local
  %imm_addr = zext i16 %pc1 to i32
  %imm = call i8 @memory_read_8(i32 %imm_addr)
  %pc1_inc = add i16 %pc1, 1
  store i16 %pc1_inc, ptr %pc_local
  store i8 %imm, ptr %a_local

  ; instr 2 (TAX):
  %a2 = load i8, ptr %a_local
  store i8 %a2, ptr %x_local

  ; --- epilogue: sync locals back to state ---
  %pc_final = load i16, ptr %pc_local
  store i16 %pc_final, ptr (state + PC_off)
  %a_final  = load i8, ptr %a_local
  store i8 %a_final, ptr (state + A_off)
  ; ... etc
  ret void
}
```

After (automatic) `mem2reg` pass:
```llvm
define void @Execute_Block_Main_8000_g1(ptr %state, i32 %instr_word) {
entry:
  ; --- prologue (single load per slot) ---
  %pc_init = load i16, ptr (state + PC_off)
  ; A/X etc. are not needed — body never reads them, only writes;
  ; reads happen later in the epilogue

  ; --- body (pure SSA, no memory) ---
  %imm_addr = zext i16 %pc_init to i32        ; PC uses init value directly
  %imm = call i8 @memory_read_8(i32 %imm_addr)
  %pc1_inc = add i16 %pc_init, 1              ; PC arithmetic in SSA
  ; instr 2 TAX: %imm forwarded directly to the X store
  ; final PC is %pc1_inc (after read_imm8 advanced it in LDA)

  ; --- epilogue (single store per dirty slot) ---
  store i16 %pc1_inc, ptr (state + PC_off)
  store i8 %imm, ptr (state + A_off)         ; A := %imm (also TAX's src)
  store i8 %imm, ptr (state + X_off)         ; X := %imm
  ret void
}
```

Cross-instruction SSA threads through; for two instructions we have only
one load (PC) and three stores. **This is what block-JIT should look like**,
and the emitters did not change at all.

### 2.3 EmitContext API

Add `EmitContext.GepGpr(int)` / `GepStatusRegister(string)` so that in block
mode they return the alloca pointer, and in per-instr mode they return the
state buffer GEP (existing behavior).

The implementation adds an `IStateSlotProvider` to EmitContext:

```csharp
public interface IStateSlotProvider {
    LLVMValueRef GprPtr(int index);
    LLVMValueRef StatusRegPtr(string name);
    LLVMValueRef BankedGprPtr(string mode, int index);
    void SyncToState();   // emitter calls this before a host extern call
}

public sealed class StateBufferProvider : IStateSlotProvider {
    // current GEP-into-state-pointer behavior
}

public sealed class AllocaSlotProvider : IStateSlotProvider {
    // maintains slot → alloca map; SyncToState writes dirty allocas back
    // to state. BlockFunctionBuilder constructs and injects this in the
    // prologue.
}
```

EmitContext holds an `IStateSlotProvider _slots`:
```csharp
public LLVMValueRef GepGpr(int idx) => _slots.GprPtr(idx);
```

### 2.4 Side-effect sync points

When in-block IR calls a host extern (memory_read_8, memory_write_8, other
host helpers), those externs may:
- trigger IRQ/NMI (the host's NMI handler needs the right state.PC)
- read state (no — externs are pure in/out)
- write state (no — externs go through host C# code that modifies state?
  in practice not; state is changed explicitly from the outside, e.g.
  BoundCpu.NmiInterrupt directly pushes state; the host-side trampolines
  for in-block extern calls do not write state fields)

Concrete analysis:
- `memory_read_8` / `memory_write_8` go through NesMemoryBus and may
  trigger PPU NMI; but NMI polling happens in the dispatch loop after
  block exit (per-instr behaves the same way). So in-block extern calls
  **do not need** to sync state.
- Exception: bus.WriteByte may write to $4014 (OAM DMA) and cause a +513
  stall — that cycle accounting is collected after block exit by the host
  via `bus.ConsumeStallCycles()` and does not affect in-block IR.
- LR35902's EI/DI uses a defer mechanism + sync extern — that existing
  design is preserved.

**Conclusion**: on NES, side-effect sync **only happens at block exit**.
GB's defer/sync mechanism is independent of this refactor and is unaffected.
A simplified version can skip mid-block sync entirely and only do prologue
+ epilogue.

### 2.5 Which slots get an alloca

**All hot state fields**:
- All GPRs (the N entries listed in `registerFile.GeneralPurpose.Names`)
- All status registers (`P`, `SP`, `PC`, plus GB's `F`)
- Cycle counter / PcWritten flag — these are register-resident already

Will too many allocas confuse LLVM? — mem2reg handles arbitrary numbers of
allocas just fine. GB's 8 GPRs + 3 status regs are already more than 6502's
3 GPRs + 3 status regs.

---

## 3. Implementation plan (B'.1 - B'.6)

### B'.1 — `IStateSlotProvider` + `EmitContext` switching

New file `src/AprCpu.Core/IR/IStateSlotProvider.cs`.
Refactor `EmitContext.GepGpr` / `GepStatusRegister` / `GepBankedGpr` to go
through the provider. In per-instr mode, inject `StateBufferProvider`
(preserving the existing behavior).

Verification: T1 with no regression (the per-instr path is fully equivalent).

### B'.2 — `BlockFunctionBuilder` injects the alloca provider

At the start of the block IR's entry block:
1. Allocate one alloca per spec-declared state field
2. Load the corresponding state-buffer values into the allocas
3. Build an `AllocaSlotProvider` recording the slot mapping
4. EmitContext uses this provider during emit
5. Before block exit, store every alloca value back into the state buffer
6. Remove the band-aid `PerInstructionPcPreWrite` flag (no longer needed)

### B'.3 — mem2reg pass

After building each block function, run the standard LLVM
`mem2reg` / `sroa` / `instcombine` passes. With LLVMSharp this is
`LLVMRunFunctionPassManager`.

Verification: dump the IR and confirm no alloca survives in the block body
and there are no redundant cross-instruction state load/stores.

### B'.4 — GB regression

Run the existing GB JsonCpu block-JIT tests (blargg cpu_instrs). Pass
criteria:
- Identical results (all 11 PASS)
- Perf does not regress significantly (baseline 21 MIPS → tolerate ±10%)

GB's existing `PipelinePcConstant` / `CurrentInstructionBaseAddress` paths
are **kept for now**; the alloca path is additive. If GB block-JIT runs
faster than before (mem2reg + cross-instr CSE > the existing baked-const
path), that's a bonus; staying flat is the expectation.

### B'.5 — NES block-JIT enable

NesJsonCpu adds a `--backend=json-block` path (mirroring GB). Mos6502Emitters
**is not touched at all**.

Verification:
- nestest --backend=json-block: PC→\$C66E, \$02=\$00 \$03=\$00
- blargg cpu_test5 --backend=json-block: "All tests complete"
- Both match legacy / json per-instr results

### B'.6 — Perf measurement

3-run blargg cpu_test5:
- legacy (baseline 1.69 MIPS)
- json per-instr (baseline 0.83 MIPS)
- **json-block (new)**

Expectation is for NES json-block to at least reach legacy levels (1.5-2
MIPS), possibly exceeding it (if mem2reg folds 6502's NVZC flag chain
cleanly).

Write into `MD/performance/<ts>-nes-blockjit-vs-perinstr.md`.

### B'.7 — (deferred) deprecate the old paths

Once B'.5 + B'.6 are all green, audit the spots in `Lr35902Emitters` /
`ArmEmitters` that use PipelinePcConstant / CurrentInstructionBaseAddress.
If the alloca path produces equivalent IR, remove the old paths — one
mechanism in the framework that works across all CPUs. Re-run GB block-JIT
+ ARM existing tests to confirm no regression.

If deprecation is risky (e.g. some GB emitter has a special dependency on
baked-const), document them as deprecated but keep them — at least new
CPU emitters won't be tempted to take the old path.

---

## 4. Risks and mitigations

### 4.1 mem2reg fails to promote

mem2reg requires the alloca to be at the entry block's start, with usage
that is purely load/store (no taking of address). All our allocas qualify
— emitters get the pointer through `ctx.GepGpr` and only do load/store, no
ptrtoint, never passed to a host extern.

**Mitigation**: write a unit test that dumps the block IR and confirms
post-mem2reg the alloca count is 0 (or ≤ the expected residual count).

### 4.2 alloca pointer escaping to host

If some emitter passes an alloca pointer to a host extern (e.g. a
hypothetical `host_dump_state(ptr state)`), mem2reg will fail.

**Mitigation**: grep the emitter source to confirm there is no escape; in
`AllocaSlotProvider.GprPtr` add a debug-only assertion "callers must not
store this pointer to an escapable location".

### 4.3 LR35902's existing PipelinePcConstant collides with the new alloca

LR35902's read_imm8 emitter checks
`if (ctx.PipelinePcConstant is uint pc) { /* bake const, advance C# tracker */ } else { /* per-instr path: load PC, advance, store */ }`.

In the new alloca path, the in-block PC is an alloca slot — to the emitter
this looks exactly like the "per-instr path" (load/store both happen), but
underneath it is an alloca rather than the state buffer.

If both paths coexist:
- The PipelinePcConstant baked-const path → does not touch the alloca → at
  block exit the alloca is still its init value → epilogue writes that
  back to state, **clobbering the real PC**

**Mitigation**: in B'.2's implementation, also wire GB block-JIT's
PipelinePcConstant onto the alloca — at block prologue, set
PipelinePcConstant to point at the alloca's current value (mem-read
semantics equivalent to the existing const), making the two paths
converge. Or more simply: in B'.4 test whether GB block-JIT still passes;
if it fails, fix it in B'.4.

### 4.4 SMC / bank switching

Not affected by this refactor — invalidation is a BlockCache concern;
allocas are an internal IR-shape change inside a single block function.

### 4.5 Perf regression

If for some reason mem2reg does not finish (e.g. the alloca is in a
non-entry block — careful with BlockFunctionBuilder's prologue placement),
block-JIT becomes as slow as per-instr.

**Mitigation**: the IR-dump test in B'.3. If after mem2reg it is still
slow, the problem is not in state access — it is likely in the dispatch
loop or cycle accounting.

---

## 5. Cross-doc references

- Reinforces [12-gb-block-jit-roadmap.md](12-gb-block-jit-roadmap.md) — GB
  path deprecation is in B'.7
- Aligned in direction with
  [11-emitter-library-refactor.md](11-emitter-library-refactor.md) (lift to
  the shared layer)
- The framework / emulator boundary drawn by
  [16-emulator-completeness.md](16-emulator-completeness.md) +
  [17-aprcpu-vs-emulator-timing-boundary.md](17-aprcpu-vs-emulator-timing-boundary.md)
  is unchanged

---

## 6. Wrap-up

After this refactor, **the emitter contract becomes**:

> The emitter does not need to know whether it is in per-instr or block-JIT
> mode. All state access goes through framework helpers like `ctx.GepGpr` /
> `ctx.GepStatusRegister` / `ctx.ReadStatusFlag`. Pure ALU / arithmetic /
> control-flow ops compose directly out of the generic standard emitters
> like `Binary` / `BranchCc` / etc.
>
> The one exception: when an emitter calls a host extern, if that extern's
> side effects read state (e.g. an NMI handler), the emitter must call
> `ctx.SyncStateToBuffer()` (provided by B'.2) beforehand. In practice the
> bus externs on both NES and GB do not read state, so today this rule
> reduces to "no need to call it".
>
> Adding a new CPU: write spec.json + add arch-specific micro-ops to
> `<Arch>Emitters.cs` as needed (addressing modes / flag combos / etc.),
> done. Per-instr / block-JIT framework support comes for free.

The "framework cost" of adding a new CPU drops from "audit 30+ emitters
for compliance with the implicit contract" to "write the emitter, run the
tests".
