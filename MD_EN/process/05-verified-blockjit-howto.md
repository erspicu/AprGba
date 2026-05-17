# Verified Block-JIT — how-to

> Brief English mirror of [`MD/process/05-verified-blockjit-howto.md`](../../MD/process/05-verified-blockjit-howto.md).
> See that file for full details, env-var listings, and divergence-
> report interpretation walkthrough.

## TL;DR

```
apr-pc --bios=BIOS/firmware/pcxtbios.bin \
       --video-bios=BIOS/firmware/videorom.bin \
       --floppy-a=BIOS/freedos-1.3-floppy.img \
       --verify-blocks --max-cycles=1000000
```

For every cached JIT block, the verifier runs the JIT once + the
interpreter the same number of architectural instructions from the
same pre-block state, then 3-axis compares (CPU state + mem-write
trace + side-effect log). Any divergence pinpoints the buggy block
+ axis + instruction.

## What it verifies

1. **CPU state at block exit** — all registers + flags + PC
2. **Memory-write trace** — count, address, value, size, in order
3. **Side-effect log** — port writes + IRQ asserts

A `CpuStateMismatch` usually means an arithmetic/flag emitter is
wrong. A `MemWriteTraceMismatch` usually means a store emitter has
wrong address/value computation. A `SideEffectMismatch` usually means
an I/O emitter is wrong.

## Adding a CPU backend

Implement `IBlockBoundedSteppableCpu` (lives in `AprCpu.Core/Validation/`).
The x86 reference impl is in `src/AprX86.Cli/Validation/X86LockstepAdapter.cs`.
Key callbacks:

- `StepOneArchitecturalInstruction` — bypass block-JIT, force per-instr
- `BeginTrace/EndTrace` — swap a static trace-sink field your
  JIT-emitted externs read on every call
- `SnapshotMemoryState/LoadMemoryState` — memcpy RAM + clone CPU state
- `AdditionalSnapshot/Restore` — optional hook for HW-chip state
  (e.g. PortBus cycling counters)
- `OnBeforeStep` — fire env.Activate() before each step so JIT-extern
  static handlers point at the stepper's own env

## When to run

| Situation | Recommended invocation |
|---|---|
| Active emitter dev | 5-10s `--verify-blocks` after any change |
| Pre-commit (Tier 3 changes) | `--max-cycles=1000000` (~3 min, surfaces almost any issue) |
| Bug-hunt for "works per-instr, breaks block-JIT" | Run `--verify-blocks` first — it usually tells you exactly which block + axis + instruction in seconds |
| CI gate (proposed) | 100k-block verify on every PR touching `BlockFunctionBuilder` / `AllocaSlotProvider` / `*Emitters.cs` |

## Limitations

- Single block at a time (doesn't catch cross-block bugs directly,
  though invalidations surface as divergences in the FIRST block
  after).
- PIT IRQs disabled (verifier needs determinism).
- V1 snapshot is full RAM memcpy (~1MB × ~15k blocks/sec). CoW
  optimisation deferred — see design doc §4.6.
