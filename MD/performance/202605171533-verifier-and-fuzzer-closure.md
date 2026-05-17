# Phase 30.15d / 30.16 / 30.17 / 30.18 — Verifier + fuzzer closure

> **Status**: ✅ **COMPLETE** (2026-05-17). 4-CPU verifier framework
> + 4-CPU differential fuzzer shipped end-to-end across a long
> multi-iteration `/loop` session. ~10 real bugs surfaced and ~7 of
> them fixed during the same session.

## Scope

Four sub-phases delivered across one session:

| Phase | What | Status |
|---|---|---|
| **30.15d** | Generic Verified Block-JIT framework (`VerifiedBlockJitRunner`, `IBlockBoundedSteppableCpu`, `IBlockTraceSink`, `RingBufferTraceSink`) + x86 reference impl | ✅ |
| **30.16** | Extend verifier to GB (LR35902), NES (Ricoh 2A03), GBA (ARM7TDMI) | ✅ |
| **30.17** | NES differential fuzzer (`apr-nes --fuzz=N`) + close verifier-framework gaps | ✅ |
| **30.18** | GB / GBA / x86 differential fuzzers | ✅ |

## Verifier results across all 4 framework-target CPUs

| CPU | Test ROM | Blocks NoDiff | Instructions | Runtime |
|---|---|---|---|---|
| x86-16 (i8086) | pcxtbios + FreeDOS boot | **1,000,000** | 4.38M | 2:54 |
| LR35902 (GB) | cpu_instrs.gb | 278,872 (IRQ-asym limit) | 1.48M | 4.6s |
| Ricoh 2A03 (NES) | blargg cpu_test5/cpu.nes | **1,000,000** | 2.00M | 6.2s |
| ARM7TDMI (GBA) | gba-tests/arm/arm.gba | **1,000,000** | 1.00M | 1:06 |

Three of four CPUs hit the 1M-block target without divergence. GB
hits an IRQ-delivery-cadence asymmetry between JIT (block-boundary)
and per-instr (instruction-boundary) at block #278,872; this is a
known semantic gap, not an emitter bug.

## Fuzzer results across all 4 CPUs

| CPU | First-iter bug-find rate | Status after fixes |
|---|---|---|
| NES | 3 distinct verifier-framework gaps surfaced in 10 seconds | **All fixed; 41,473 blocks NoDiff in 500-iter sweep** |
| GB | 2 per-instr emitter bugs (STOP, HALT) | **Both fixed in spec + runtime** |
| GBA | 1 ARM7 STMDB R15-pipeline bug | Tracked for follow-up (task #340) |
| x86 | 1 BlockDetector NOP-fallback bug (synthesizing 0x00=ADD as silent NOP) | **Fixed**; mid-block edge case tracked (task #342) |

## Bugs found + fixed (chronological)

### 30.15d framework bring-up (3 real x86 emitter bugs)
1. **Phase 30.15b** PC linear-vs-IP pre-write: block-JIT stored linear address into IP slot for low-CS segments; corrupted IP in pcxtbios scratch segment.
2. **Phase 30.15c-B** packed-tail `ImmConsumed` leak: switch arms in `X86ModRmComputeEaEmitter` each pre-emitted `FetchImm8` which bumped the shared `ImmConsumed` counter; mod=01 disp8 fetch saw offset=3 instead of 1.
3. **Phase 30.15c-C** INT n FLAGS reserved bit: `X86InterruptHelpers.EmitIntCommon` pushed raw FLAGS without forcing reserved bit 1 = 1 per Intel spec.

### 30.15d sprint 5.4 verifier-framework correctness (3 bugs)
4. **Sprint 5.4b** HW-state desync: `PcPortBus` cycling counters (CGA status port, kbd FIFO) not snapshotted between envs.
5. **Sprint 5.4c** JIT actual instr count: `entry.InstructionCount` (compile-time block size) used instead of actual runtime count; `LastInstrIndex` state slot added; `BlockFunctionBuilder` writes `(i+1)` at preBB[i].
6. **Sprint 5.4d** PortBus active-env switch: `OnBeforeStep` callback added to `X86SteppableCpu` so JIT-extern static handlers point at the current stepper's env.

### 30.17 NES fuzzer-framework gaps (3 fixes)
7. NesMemoryBus `_cpubus` open-bus latch snapshot.
8. `APR_MOS6502_NO_FAST_IMM` env-gate: block-JIT `FetchImm8/16` fast path skipped bus-extern updates of `_cpubus`.
9. PC ≥ $8000 skip in fuzzer (execute-from-open-bus is fundamentally non-deterministic and not a real-ROM scenario).

### 30.18 GB emitter fixes (2 fixes)
10. **Sprint 30.18b** GB STOP (0x10) per-instr ignored pad byte while block-JIT consumed it. Fix: added `read_imm8` step to spec.
11. **Sprint 30.18c** GB HALT (0x76) per-instr `_haltSignal → _halted` transfer only happened in `RunCycles` loop, not in `StepOnePerInstr` used by verifier. Fix: added transfer to `StepOnePerInstr`.

### 30.18 framework fixes (1 fix)
12. **Sprint 30.18f** BlockDetector silent-NOP fallback was synthesizing 0x00 as if it were NOP, but for x86 0x00 is ADD r/m8, r8 (2-byte ModR/M + bus access). Fix: only do the fallback when 0x00 decodes to zero-operand instruction and length is 1.

## Open follow-ups (tracked in task list)

After Phase 30.18j (length-table 0x08 fix) + 30.18k (LDI/LDD reorder
fix), the GB fuzzer divergence rate dropped from 15 → 2 in 100 iter
× 50 blocks/iter (seed=42). The two remaining have a different
shape — both involve unconditional control transfers:

- **#339 (GB JP/RST)** — When the LAST instruction in a block is JP nn
  (0xC3) or RST $tt (0xC7/CF/D7/DF/E7/EF/F7/FF), JIT block-JIT runs
  past the control transfer instead of taking it. INTERP per-instr
  correctly jumps to the target. Looks like the block-detector +
  branch/call emitter chain isn't ending the block at the writes_pc=
  "always" instruction in some path. iter 78 (all 0xFF bytes) shows
  JIT running 20× RST sequentially while INTERP takes the first one
  to vector $38; iter 79 (random with JP) shows JIT advancing past
  JP while INTERP takes JP target. Investigation: BlockDetector
  line 570 `def.WritesPc == "always"` check looks right; suspect
  is either `WritesPc` not being populated for selector-based
  RST/JP entries, or the emitter setting PcWritten=1 but the
  block IR not branching to blockExit. Reproduce: `apr-gb --fuzz=100
  --fuzz-blocks=50 --fuzz-seed=42 --fuzz-continue`.

- **#340 (GBA STMDB R15)** — BlockTransferEmitters uses
  `PipelinePcConstant` for R15 reads (line 350-353); fix is correct
  in principle. Bug is subtler — possibly cross-jump-followed block
  PC drift at the STM instruction's bi.Pc.

- ~~**#342 (x86 mid-block undecodable)**~~ — **RESOLVED (Phase 30.18o,
  commit pending).** Actual root cause was different from initial
  diagnosis: BlockDetector returned 0-instruction blocks when the
  first byte was undecodable AND the safe-NOP-fallback didn't apply
  (x86 0x00=ADD), then constructing `Block(instructions: [])` threw
  generic `ArgumentException` which the dispatcher couldn't
  distinguish. Fix: `BlockDetector` now raises
  `UndecodableFirstInstructionException`; `X86JsonCpu.StepBlock`
  catches it and bails to per-instr. Fuzzer SKIPPED rate at
  seed=831377771 dropped 11 → 0; verified-block coverage jumped
  14 → 564 (40×).

All three are real emitter / block-detector bugs that the fuzzer
reliably surfaces. Each is its own focused investigation sprint;
none unblocks the existing real-ROM verifier workload (cpu_instrs.gb
278k blocks NoDiff, pcxtbios+FreeDOS 1M blocks NoDiff).

## Diagnostic + framework-prevention tools added (Phase 30.18l/m/n)

After the immediate verifier+fuzzer work, this session added several
tools to make future bug investigation faster and to push more
authoring-mistake detection into the framework:

1. **Early Bailout Bisection** (`APR_EARLY_EXIT_BLOCK_PC` +
   `APR_EARLY_EXIT_INSTR_COUNT`): cap a specific block at K
   instructions to bisect which instruction is buggy. Preserves
   JIT optimization context (register-alloc, flag-elision) so bugs
   don't disappear under observation. Gemini-recommended Strategy 1.

2. **Force Budget** (`APR_GB_FORCE_BUDGET=N`): set GB block-JIT cycle
   budget globally to N. Distinguish per-instruction vs multi-
   instruction-state bugs. (Caveat: budget-exit fires AFTER first
   instruction's cycle deduct, so N=1 doesn't truly force 1 instr.)

3. **SpecLinter** (`AprCpu.Core.JsonSpec.SpecLinter`): proactive
   JSON-spec authoring check, runs at spec-load time. Catches
   patterns that historically led to fuzzer-found block-JIT bugs:
   - **PostStoreRegisterUpdate**: `store_byte` followed by
     `write_reg_pair_named` to the source pair (LDI/LDD bug)
   - **HaltStopMetadata**: `op:"halt"/"stop"` step without
     `changes_mode:true` or `writes_pc:"always"`
   - **EmptyStepsNonNop**: empty Steps on a non-trivial mnemonic

   CLI: `apr-{nes,gb,x86,gba} --lint-spec`. All 4 CPU specs report
   0 warnings after this session's fixes. New rules trivially
   extensible as the fuzzer surfaces more patterns.

## CLI surface added

```bash
# Verifiers (per-block JIT-vs-interp diff)
apr-pc  --verify-blocks  --max-cycles=N   # x86
apr-gb  --verify-blocks=N                  # LR35902
apr-nes --verify-blocks=N                  # MOS 6502
apr-gba --verify-blocks=N                  # ARM7TDMI

# Fuzzers (random ROM → verifier diff, --fuzz-continue spans all
# iterations instead of stopping at first divergence)
apr-pc  ...     # (no fuzzer — pcxtbios is the workload)
apr-gb  --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-nes --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-gba --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-x86 --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
```

## Docs produced

- `MD/design/30.15d-verified-blockjit-framework-design.md` (design + 12-section sprint table)
- `MD/process/05-verified-blockjit-howto.md` (operator's guide)
- `MD/design/30.15-blockjit-pc-investigation.md` (history)
- `MD_EN/design/30.15d-verified-blockjit-framework-design.md` (English mirror)
- `MD_EN/process/05-verified-blockjit-howto.md` (English mirror)
- README.md status section (sync'd with all phase deliverables)
- This file (closure note)

## QA validation

Full T1 unit-test suite (894 tests, all backends + spec + IR layers)
passes after the session's many changes:

```
已通過! - 失敗:     0，通過:   894，略過:     0，總計:   894，持續時間: 6 m 25 s - AprCpu.Tests.dll (net10.0)
```

So all the spec changes (GB STOP `read_imm8`), runtime changes
(GB StepOnePerInstr `_haltSignal` transfer, X86JsonCpu
LastInstrIndex reading, MemoryBusBindings SetActive/ActiveTraceSink,
NesMemoryBus internal-state exposure, BlockDetector NOP-fallback
safety check, Mos6502 FetchImm env gate), and infrastructure
additions (CpuStateLayout LastInstrIndexFieldIndex, X86SteppableCpu
+ GbSteppableCpu + NesSteppableCpu + GbaSteppableCpu adapters,
4 verifier runners, 4 fuzzers) preserve the existing functional
coverage. No regressions.

## Conclusion

The framework is portable (4 different ISAs, ~150-200 LoC per-CPU adapter), additive (no breaking changes to existing backends, all CLI flags are opt-in), and demonstrably effective at finding bugs that hand-curated test ROMs don't reach (~10 real bugs in one session). It's production-ready for any future CPU backend.
