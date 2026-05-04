# IRQ delivery granularity — Hybrid Fast-Path / Slow-Path Sync

> **Status**: design doc (2026-05-03). Implementation tracked as Phase 7
> GB block-JIT P0.7 (see `MD/design/12-gb-block-jit-roadmap.md`).
>
> **Origin**: Gemini consultation (`tools/knowledgebase/message/20260503_224732.txt`)
> on industry patterns for matching per-instruction IRQ delivery
> granularity inside a block-JIT, without sacrificing throughput.
>
> **Goal**: per-instruction IRQ delivery granularity in block-JIT mode
> with **near-zero perf cost** in the common case (RAM access without
> IRQ trigger). Generic across CPU specs — no per-CPU MMIO knowledge in
> the framework or JSON spec.

---

## 1. Problem statement

Current state (P0.6 shipped):
- Per-instr backend checks IRQ pending after EVERY instruction
- Block-JIT backend checks IRQ pending only AFTER the whole block (~5-30
  instructions per block)
- IRQ-timing test ROMs (Blargg 02-interrupts) detect divergence and fail

Concrete example: Blargg 02-interrupts subtest 2 sets up STAT IRQ, runs
`LDH (FF41), A` which writes to STAT register, expects IRQ vector at the
NEXT instruction boundary. Per-instr delivers correctly. Block-JIT
delivers IRQ ~10-30 instructions late (after current block ends). Test
detects divergence → fails.

This is the last major correctness gap blocking GB block-JIT from
matching per-instr behaviour exactly.

---

## 2. Why naive solutions don't work

| Approach | Cost | Gemini's evaluation |
|---|---|---|
| (1) Inline IRQ check after every instruction | ~5-15% MIPS hit | IR bloat, L1 instruction cache pressure, blocks LLVM cross-instr optimization |
| (3) Static MMIO-aware block exit | low cost | **anti-pattern**: JSON spec shouldn't know `0xFF0F` is IRQ-relevant; not generic |
| (5) Profile-guided adaptive | high complexity | overkill for stable emulator patterns |
| (2) Cycle-budget proactive exit | 0 cost | only works for predictable events (timer/scanline); fails for software-triggered (MMIO writes that set IF directly) |

The real-world solution combines **(2) cycle-budget for predictable** +
**a generic variant of (3) for unpredictable** — but resolved
DYNAMICALLY (no spec-side MMIO knowledge), not statically.

---

## 3. Architecture: Hybrid Fast-Path/Slow-Path Sync

Split IRQ delivery into two categories handled separately:

### 3.1 Predictable IRQs (timer / scanline) — already covered by Phase 1a

Scheduler calculates "cycles until next pending event" before each
RunCycles call → loaded into `cycles_left` budget → block IR decrements
per instr (Phase 1a) → block exits early at the right cycle boundary.

**Cost**: 0 (already implemented).

### 3.2 Unpredictable IRQs (MMIO writes) — new in P0.7

Only check IRQ state after instructions that COULD mutate IRQ state:
- Memory writes (MMIO might set IF or IE)
- Special CPU instructions (EI/DI/MSR)

**Key insight**: the JSON spec doesn't need to know which addresses are
IRQ-relevant. The C# bus knows. Just have bus.Write return a "sync
needed" flag; if true, JIT'd block exits early.

#### 3.2.1 Bus extern signature change

Before:
```csharp
[UnmanagedCallersOnly]
public static void BusWrite(uint addr, byte value);
```

After:
```csharp
[UnmanagedCallersOnly]
public static byte BusWrite(uint addr, byte value);   // returns 1 if IRQ sync needed, 0 otherwise
```

The bus implementation knows the IRQ-relevant address ranges per CPU
(GB: 0xFF0F IF, 0xFFFF IE, plus device-specific like 0xFF41 STAT). When
a write to one of these registers MAY have changed IRQ state, return 1.

#### 3.2.2 Memory write emitter — fast/slow split + sync check

LLVM IR pattern (per write):
```llvm
mem_write:
  %is_ram = icmp ult i32 %addr, RAM_END           ; cheap region check
  br i1 %is_ram, label %fast_path, label %slow_path
                                ; ↑ predicted to fast_path 99%

fast_path:
  ; inline GEP + store — 0 extra branch beyond region check
  store ...
  br label %continue

slow_path:
  ; Phase 1b MMIO callback (always paid for MMIO anyway)
  %sync = call i8 @BusWrite(i32 %addr, i8 %val)
  %sync_b = icmp eq i8 %sync, 1
  %sync_h = call i1 @llvm.expect.i1(i1 %sync_b, i1 false)  ; cold path
  br i1 %sync_h, label %exit_block_for_sync, label %continue

exit_block_for_sync:
  ; serialize PC + cycles_consumed, return
  store next_pc, ptr %pc_slot
  store i8 1, ptr %pc_written_slot
  ret void

continue:
  ; next instruction in block
```

**Cost analysis**:
- RAM writes (vast majority): 1 region-check branch, perfectly predicted
  → ~0 extra cost (branch predictor saturates to 0 cycles)
- MMIO writes (rare): existing P/Invoke callback overhead dominates;
  the new sync-check is one i8 compare + branch + the exit cleanup —
  ~2ns extra, completely dwarfed
- Block-exit-due-to-sync (very rare — only when MMIO actually changed
  IRQ state): one block ends slightly early, outer loop delivers IRQ,
  same as per-instr would have done

#### 3.2.3 Special CPU instructions — `sync` micro-op + defer integration

For instructions that change IRQ state without touching memory (EI / DI /
ARM MSR etc.), use a new generic micro-op:

```json
{ "op": "sync" }
```

Emitter for `sync` simply emits `ret void` (or branch to block_exit BB).
After `sync`, control returns to outer loop which re-checks IRQ.

For LR35902 EI (with the existing P0.6 `defer` mechanism):
```json
{
  "mnemonic": "EI",
  "steps": [{
    "op": "defer", "delay_value": 1, "action_id": 0,
    "body": [
      { "op": "lr35902_ime", "value": 1 },
      { "op": "sync" }
    ]
  }]
}
```

After P0.6's AST pre-pass injects this body into the EI+1 instruction's
end, the resulting block:
- Runs EI+1 instruction's normal steps
- Sets IME=1
- `sync` → `ret void` → outer loop checks IRQ

If an IRQ is pending and IME just became 1, the outer loop delivers it
NOW — exactly matching per-instr's "between EI+1 and EI+2" point.

---

## 4. Generality

The mechanism is fully generic:

| CPU | IRQ-state mutators | sync mechanism |
|---|---|---|
| LR35902 | EI/DI, write to IF (0xFF0F) / IE (0xFFFF) / STAT (0xFF41) etc. | EI/DI use `sync` step in spec; bus tracks MMIO addrs |
| ARM7TDMI | MSR CPSR_c (sets I/F bits), write to IF/IE registers | MSR uses `sync` step; bus tracks 0x04000200/202/208 |
| RISC-V | CSRRW to mip/mie/mstatus, ECALL/EBREAK | CSRRW uses `sync` step; CSRs are register-based not memory |
| MIPS R3000 | mtc0 to Status/Cause registers | mtc0 uses `sync` step |

Spec describes the SEMANTIC ("this op may change IRQ state, sync after").
Framework knows the per-CPU MMIO map only at the C# bus implementation
level. JSON spec stays generic.

---

## 5. Implementation steps (P0.7)

### 5.1 Step 1 — Bus extern signature change (~0.5 day)

- `MemoryEmitters.ExternFunctionNames`: add new `Write8WithSync` extern
  variant (keep old `Write8` for backward compat with non-sync callers
  during migration; remove later)
- C# bus shims (GbMemoryBus, GbaMemoryBus): implement new signature,
  return sync flag from MMIO writes that touch IRQ-relevant addresses

### 5.2 Step 2 — Memory write emitter fast/slow split (~1 day)

- `Lr35902StoreByteEmitter` + ARM equivalents: emit fast-path region
  check + slow-path callback + sync check
- For block-JIT: sync exit path stores `next_pc + PcWritten=1` then
  `ret void` (similar to budget-exit path)
- Per-instr mode: sync is no-op (per-instr already checks IRQ between
  every instruction)

### 5.3 Step 3 — `sync` micro-op (~0.5 day)

- New emitter `SyncEmitter` in `Emitters.cs` (generic, all CPUs)
- Block-JIT emit: store `next_pc`, mark PcWritten=1, branch to block_exit
- Per-instr emit: no-op (outer loop checks IRQ regardless)
- Add to `EmitterRegistry.RegisterStandard`

### 5.4 Step 4 — Update LR35902 EI spec (~0.5 day)

- Add `{ "op": "sync" }` after `lr35902_ime` in EI's defer body
- Should also update DI (it changes IRQ state to NO interrupts; safe to
  not sync since IRQ won't fire anyway, but cleaner to sync for symmetry)

### 5.5 Step 5 — GbMemoryBus IRQ-relevant address tracking (~0.5 day)

- Identify GB IRQ-relevant addresses: 0xFF0F (IF), 0xFFFF (IE),
  0xFF41 (STAT — bits 3-6 control STAT IRQ sources)
- New `WriteByte` overload (or extend) returns sync flag
- Document in GbMemoryBus header

### 5.6 Step 6 — Verify (~0.5 day)

- T1 unit tests (no expected change, but add test for sync emitter)
- T2 GBA matrix (regression check)
- Lockstep diff Blargg 01-special (still 0 divergence)
- Lockstep diff Blargg 02-interrupts (should now be 0 or close to 0
  divergence)
- Blargg 02-interrupts block-JIT: should PASS (was "EI Failed #2")
- Bench: measure perf impact (predicted < 1% loss, possibly 0 due to
  branch prediction)

**Total**: ~3 days work.

---

## 6. Edge cases / gotchas

### 6.1 Sync exit during budget downcount

If sync exit fires mid-block, the cycles_left budget might not equal
"cycles consumed = budget initial - cycles_left at exit" because budget
exit and sync exit are different paths. Ensure both paths properly
update cycles_left so outer loop's bus.Tick gets the right value.

### 6.2 Multiple syncs in a block

If a block has multiple `sync` micro-ops (e.g. multiple EI within one
block), only the FIRST one fires — block exits immediately. Per-instr
behaviour: every instruction's IRQ check might fire its own IRQ. With
sync exits, each block ends at most once. Outer loop re-enters, runs
next block from there, can fire again.

### 6.3 RAM region check size

For LR35902, "RAM" = WRAM (0xC000-0xDFFF) + HRAM (0xFF80-0xFFFE). MMIO
is 0xFF00-0xFF7F + 0xFFFF. Cart RAM is 0xA000-0xBFFF (sometimes MBC-
controlled). For simplicity V1 uses just `addr < 0xFF00 || addr ==
0xFFxx_specific_region` checks; V2 can expand to multi-range.

### 6.4 Sync flag dropped if MMIO not really changing IRQ state

C# bus could be conservative ("any MMIO write returns sync=1") or
precise ("only writes that actually change IF/IE/STAT bits return
sync=1"). Conservative is simpler + cheaper to implement; precise saves
spurious block exits. Start conservative; profile and tighten if needed.

### 6.5 Per-instr backend cost

Per-instr DOESN'T need the fast/slow split because outer loop checks
IRQ every instruction anyway. Per-instr emitter can just call the bus
write extern and ignore the return value. Cleaner: per-instr uses the
old `Write8` (no sync return); block-JIT uses new `Write8WithSync`.
Detected at emit time via `ctx.CurrentInstructionBaseAddress` (existing
block-JIT mode marker).

---

## 7. Why this is high engineering value

If successful, this gives:
1. **Block-JIT IRQ correctness equal to per-instr** — Blargg 02-interrupts
   PASS, real games' IRQ-sensitive code (DMA, sound timing) work
   correctly under block-JIT
2. **Generic framework** — works for ANY CPU spec; new CPU only needs
   `sync` annotations on its IRQ-mutator instructions + bus to track
   IRQ-relevant addresses
3. **Near-zero perf cost** — RAM writes pay only the well-predicted
   region-check branch (~0ns); MMIO writes pay one extra sync-check
   branch beyond the existing P/Invoke (~2ns) which is dwarfed by the
   MMIO callback itself
4. **Production-grade pattern** — same architecture used by QEMU TCG
   (`gen_io_start` + `cpu_loop_exit`), Dynarmic (`halt_requested`
   flag), DOSBox dynarec
5. **Unblocks future work** — once IRQ delivery is per-instr accurate,
   block-JIT can be the default backend for both ARM and LR35902 (and
   future CPUs); per-instr becomes the debug fallback only

This is the architectural decision that determines whether the
framework's block-JIT is "good enough for screenshot tests only" or
"good enough for any commercial ROM". P0.7 is the gate.
