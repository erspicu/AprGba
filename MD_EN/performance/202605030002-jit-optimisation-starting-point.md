# JIT optimisation starting point — after Phase 5.8 refactor wrap-up

> **Role**: This document is the **canonical starting baseline** for
> subsequent JIT optimisation experiments. Every new strategy attempted
> later (block-JIT, lazy flag, SMC handling, tier compilation,
> IR-level optimisation passes, etc.) compares before/after against this
> table, with new files written to `MD/performance/` using the same
> `YYYYMMDDHHMM-` prefix.
>
> **Timing**: After Phase 5.8 emitter library refactor 5.1–5.7 was done
> (commit `ce57b35`, 2026-05-03).
> **How run**: 4-ROM × 1200 frame, Release build (.NET 10) + LLVM 20
> MCJIT + Windows 11 + same dev machine.

---

## 1. Starting baseline numbers

| ROM                     | Backend     | Host time | Real-time × | **MIPS** |
|-------------------------|-------------|----------:|------------:|---------:|
| GBA arm-loop100.gba     | json-llvm   | 22.05 s   | 0.9×        | **3.82** |
| GBA thumb-loop100.gba   | json-llvm   | 22.51 s   | 0.9×        | **3.75** |
| GB 09-loop100.gb        | legacy      |  0.297 s  | 67.6×       | **32.76**|
| GB 09-loop100.gb        | json-llvm   |  3.681 s  | 5.5×        | **2.66** |

> All numbers are 3-run averages; per-run details + interpretation in
> `MD/note/loop100-bench-2026-05-phase5.8.md`.

---

## 2. System state (baseline conditions affecting perf)

- **JIT engine**: LLVMSharp.Interop 20.x + MCJIT (not ORC LLJIT)
- **Code cache**: one LLVM function per instruction; no block-level
  caching; each instruction dispatched via indirect call into JIT'd function
- **Optimization level**: LLVM `OptLevel.Default` (O2 tier, but
  functions are too small for inliner to do much)
- **State layout**: CpuStateLayout built dynamically; all registers /
  status / pc-written are byte slots in one struct, each emitter GEPs in
- **Memory bus**: each load/store goes through extern function pointer
  call (host_mem_read8 / write8 / write16); trampolines are
  `[UnmanagedCallersOnly]` static C# methods
- **Cycle accounting**: hardcoded `cyclesPerInstr=4` (GBA) /
  per-instr from spec cycle table (GB)
- **Scheduler tick**: each instruction calls GbaScheduler.Tick / GbScheduler
  .Tick (per-scanline LY advance, IRQ trigger)
- **PcWritten flag**: per instruction adds 1× byte slot write + dispatcher
  reads 1× (to detect whether branch wrote PC, avoiding double advance)
- **Open-bus pre-fetch protection**: BIOS region triggers NotifyExecutingPc
  + NotifyInstructionFetch (2 virtual calls per instruction)
- **Spec → IR cost**: one-shot at startup, ~50–200 ms (not measured precisely),
  not included in MIPS numbers

---

## 3. Possible optimisation strategies (to try one by one)

Roughly ordered from low-risk-high-return to high-risk-unverified:

### 3.1 IR-level optimisations (no architectural changes)

- **a)** Bump OptLevel to O3 + add LLVM passes (instcombine, gvn, simplifycfg)
- **b)** Switch PcWritten flag to LLVM register hint, avoid byte slot
  load/store every instruction
- **c)** Inline `read_reg` / `write_reg` results across multiple accesses
  in the same function — currently each access re-GEPs + loads
- **d)** Tag common Binary chains (add+sub+and) with `nuw nsw` to give
  LLVM more optimisation room
- **e)** Cycle-accounting via trailing add instead of mid-function call,
  making scheduler.Tick an inlinable candidate

Expected: 5–15% speedup, all changes inside single-instruction emitters,
low risk.

### 3.2 Dispatcher path optimisations

- **a)** Switch dispatcher from hash-lookup to direct table (decoded
  opcode → fn pointer array)
- **b)** Make PcWritten dispatcher-side read lazy (only check after branch
  instructions, not every instruction)
- **c)** Memory-bus extern call → inline check + slow path call
  (fast path: ROM/RAM direct GEP; slow path: IO write extern)
- **d)** Switch instruction set switch (CPSR.T for ARM/Thumb,
  CB-prefix for LR35902) to a smaller dispatch table

Expected: 10–25% speedup, broader impact than 3.1 but still local.

### 3.3 Block-level JIT (Phase 7 original plan)

- Scan from PC to next branch / return / IO write, fuse multiple instr
  into one LLVM function
- Expected 8–13× speedup (dispatch overhead amortized; LLVM is more
  effective optimising large blocks vs small functions)
- High risk: SMC detection, indirect branch dispatch, code cache LRU,
  block-linking all needed. 1–2 weeks of work.
- **Currently decided not to do** (doesn't affect framework research
  topic; real-time 60fps not in MVP scope)

### 3.4 .NET Native AOT

- AOT-compile the entire host runtime (dispatcher, bus shims, etc.)
  to avoid .NET tiered JIT cold-start cost
- The −15% on GB legacy might be tier-1 promotion not happening yet;
  AOT should restore original speed
- Independent of LLVM JIT path — only affects host runtime perf

Expected: solves GB legacy noise; minor effect on json-llvm.

### 3.5 Not yet thought of

When trying a new optimisation, append a row in §3 with hypothesis +
expected gain + actual result.

---

## 4. Discipline for comparing against baseline

- **Same run setup**: same machine, same OS load (verify no large
  process running pre-bench), same 1200 frames, same 4 ROMs, same
  Release build
- **≥ 3 runs per group**: take avg; if any run is > ±10% off avg,
  investigate cause — don't average it out
- **Apples to apples**: new strategies measured against the same loop100
  ROM at the same 1200 frames; changing ROM / frame count breaks the comparison
- **Independent file per group**: new filename `YYYYMMDDHHMM-<strategy>.md`,
  cite this doc as baseline; do not overwrite this doc
- **Honestly write regressions**: if a new strategy regresses some metric,
  write it down; no cherry-picking

---

## 5. Bench reproduction cmd (copy-paste runnable)

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

Each line ends prints `instructions: N → M.MM MIPS`. Write the 3-run avg
into the new file's table.

---

## 6. Related documents

- `MD/note/loop100-bench-2026-05.md` — Phase 5.7 raw baseline
- `MD/note/loop100-bench-2026-05-phase5.8.md` — comparison after Phase 5.8
  refactor (refactor impact: basically perf-neutral)
- `MD/design/03-roadmap.md` — Phase progress, Phase 7 marked "won't do"
- `MD/design/11-emitter-library-refactor.md` — emitter generalization design
