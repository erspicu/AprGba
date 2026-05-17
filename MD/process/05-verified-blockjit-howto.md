# Verified Block-JIT — how-to

Phase 30.15d shipped a per-block differential verifier that compares
the JIT backend against the interpreter for every executed block,
catching emitter bugs the moment they happen rather than weeks later
through obscure boot failures. This document explains:

1. What the framework verifies (CPU state, mem-write trace,
   side-effect log)
2. How to run it (`--verify-blocks`)
3. How to interpret a divergence report
4. How to add a new CPU backend
5. When to run it in your development loop

For the design rationale and history, see
[`MD/design/30.15d-verified-blockjit-framework-design.md`].

---

## 1. What it verifies

For each cached JIT block, the verifier:

1. **Captures** pre-block state from the JIT env: full guest memory
   + CPU registers + optional HW-chip state (e.g. PortBus cycling
   counters).
2. **Runs** the JIT block once; records every memory write, port
   write, and IRQ assert into a ring-buffer trace.
3. **Restores** the same pre-block state into a parallel INTERP env
   (each env owns its own memory + CPU instance — no aliasing).
4. **Runs** the interpreter N times, where N is the JIT's
   actual-executed instruction count (NOT the compile-time block
   size — see sprint 5.4c).
5. **Compares 3 axes**:
   - CPU state at block exit (regs + flags + PC)
   - Memory-write trace (count, address, value, size in order)
   - Side-effect log (port writes, IRQ asserts)

Any mismatch = the JIT emitter for one of those N instructions is
wrong. The verifier reports:

- Block index + start PC
- Actual instruction count
- Which axis diverged
- First mismatching entry, with full context
- JIT vs INTERP CPU state snapshots

This pinpoints bugs to a specific block + axis without needing to
read assembly listings.

## 2. CLI invocation

```
apr-pc \
    --bios=BIOS/firmware/pcxtbios.bin \
    --video-bios=BIOS/firmware/videorom.bin \
    --floppy-a=BIOS/freedos-1.3-floppy.img \
    --verify-blocks \
    --max-cycles=1000000
```

- `--verify-blocks` activates verification mode (replaces normal
  headless / windowed run; no UI window opens).
- `--max-cycles=N` caps the number of verified BLOCKS (not host
  cycles). Default = 100,000.
- Other PC-CLI flags (`--floppy-a`, `--video-bios`, etc.) work as
  normal.

PIT timer + IRQ are stopped automatically — the verifier needs
deterministic execution and the PIT introduces wall-clock-dependent
IRQ delivery.

### Useful env vars (for debugging the verifier itself, not normal use)

| Env var | Effect |
|---|---|
| `APR_X86_TRACE_BLOCKLEN=1` | Print `[BLOCKLEN] pc=0xXXXXX detected=N actual=M` per block-JIT entry. Use when investigating whether JIT block-detection or runtime instr count is suspect. |
| `APR_X86_NO_BLOCK_CACHE=1` | Force recompile of every block (no cache reuse). Verifies that cache freshness isn't masking a stale-IR bug. |
| `APR_X86_TRACE_COMPILE=0xPC` | Log every compile within ±0x40 of address PC. Use to confirm what bytes were actually compiled into a divergent block. |
| `APR_X86_IR_DUMP=1` | Dump the LLVM IR for every newly-compiled block to a temp file. Use when the bug appears IR-level rather than emitter-level. |

## 3. Interpreting a divergence report

Example output:

```
running verified-block up to 1,000,000 blocks...

  elapsed:           00:00:07.2304521
  verified blocks:   19470 OK before divergence
  verified instrs:   37640
  status:            CpuStateMismatch
  diverged at block: #19471, pc=0xFE2A9, instrs in block=9
  detail:            CPU state diverged: AX: A=0x344 B=0x348
  JIT post-block:    PC=0xFE2C6 AX=0x0344 BX=0x0000 CX=0x0000 DX=0xC000 ...
  INTERP post-block: PC=0xFE2C6 AX=0x0348 BX=0x0000 CX=0x0000 DX=0xC000 ...
```

Reading the report:

- **`verified blocks: N OK`** — N blocks ran identical on both sides
  before the bug surfaced. Higher = the bug only happens after deep
  state. Lower = bug is in early-boot code or a very common
  instruction.
- **`status:`** — which axis broke first.
  - `CpuStateMismatch`: registers/flags/PC differ. Usually means an
    arithmetic / flag-update emitter is wrong.
  - `MemWriteTraceMismatch`: memory writes differ in count, address,
    value, or order. Usually means a store-instruction emitter has
    wrong address or value computation, OR a non-deterministic
    HW-state read is leaking into the trace.
  - `SideEffectMismatch`: port writes / IRQ asserts differ. Usually
    means an I/O emitter is wrong.
  - `CouldNotVerify`: framework hit a limitation (trace truncated, or
    interp threw mid-verification).
- **`pc=0xXXXXX, instrs in block=N`** — block starts at PC, JIT ran
  N instructions (this is the ACTUAL count, even if BlockDetector
  detected more — see sprint 5.4c).
- **`detail:`** — exact first-mismatching field. For state mismatches
  it names the register that diverged; for trace mismatches it shows
  the first different trace record.
- **`JIT post-block:` / `INTERP post-block:`** — full register dump
  from both sides. Compare to spot which arithmetic / flag was wrong.

### How to localise the bug

1. Note the block start PC and instruction count.
2. Disassemble the N instructions starting at PC:
   ```
   python tools/x86_disasm.py BIOS/firmware/pcxtbios.bin 0xFE2A9 9
   ```
   (or open in any 8086 disassembler)
3. The diverging axis points to which instruction class is wrong:
   - State only AX differs → look for the AX-touching instruction(s)
     in that block.
   - Mem-write trace differs → look for the STORE instructions.
   - Side effects differ → look for IN/OUT / INT instructions.
4. If multiple candidate instructions, re-run with
   `APR_X86_BLOCK_MAX=1` (compile each instruction as its own block)
   to bisect.

## 4. Adding a new CPU backend

To make a CPU verifier-aware, implement `IBlockBoundedSteppableCpu`:

```csharp
public interface IBlockBoundedSteppableCpu : ISteppableCpu
{
    int LastBlockInstructionCount { get; }           // actual count
    void StepOneArchitecturalInstruction();          // forces per-instr
    object BeginTrace(IBlockTraceSink sink);         // opaque token
    void EndTrace(object token);
    object SnapshotMemoryState();                    // memcpy RAM + CPU
    void LoadMemoryState(object snapshot);
}
```

See `src/AprX86.Cli/Validation/X86LockstepAdapter.cs` for the x86
reference impl. Key wiring:

- `StepOneArchitecturalInstruction` must bypass any block-JIT path
  (use a "force per-instr" entry point on the CPU).
- `BeginTrace` swaps a static trace-sink field that your JIT-emitted
  mem-write / port-write extern reads on every call.
- `SnapshotMemoryState` must return enough state for `LoadMemoryState`
  to reproduce identical subsequent execution. For x86 V1 we memcpy
  the full 1MB RAM. For larger address spaces use a CoW page table.

If your backend has HW-chip state that evolves per-port-read (e.g.
GB's APU envelope counters, NES's PPU dot counter), expose
`AdditionalSnapshot` / `AdditionalRestore` callbacks so the harness
can plug in HW snapshot/restore (see x86's `PcPortBus.Snapshot()`
pattern in sprint 5.4b).

Per-CPU verifier CLI is a thin wrapper around the generic runner:

```csharp
var jitStepper = new MyCpuSteppableCpu("JIT", envJit.Cpu, ...);
var interpStepper = new MyCpuSteppableCpu("INTERP", envInterp.Cpu, ...);
jitStepper.OnBeforeStep = () => envJit.Activate();      // sprint 5.4d
interpStepper.OnBeforeStep = () => envInterp.Activate();
var runner = new VerifiedBlockJitRunner(jitStepper, interpStepper);
for (long b = 0; b < maxBlocks; b++) {
    var r = runner.RunAndVerifyOneBlock();
    if (r.Status != VerifiedBlockStatus.Ok) { /* report */ break; }
}
```

## 5. When to run

- **During emitter development**: run `--verify-blocks` on a 5-10s
  workload after any change to `BlockFunctionBuilder.cs`,
  `*Emitters.cs`, or `AllocaSlotProvider.cs`. Catches regressions
  the moment they're introduced.
- **Pre-commit (Tier 3)**: full `--verify-blocks --max-cycles=1000000`
  run on pcxtbios + FreeDOS boot. ~3 min runtime; surfaces almost
  any block-JIT issue with concrete diagnostic context.
- **Bug-hunt (interactive)**: when a real workload hangs or
  misbehaves under block-JIT but works per-instr, run
  `--verify-blocks` first — it likely tells you exactly which block +
  axis + instruction in seconds, instead of you bisecting by hand.
- **CI gate (proposed, sprint 5.8 follow-up)**: run a 100k-block
  verify on every PR that touches `BlockFunctionBuilder`,
  `AllocaSlotProvider`, or any `*Emitters.cs`. Failure blocks merge.

## 6. Limitations

- **Single block at a time**: doesn't catch bugs that only manifest
  across multiple chained blocks (e.g. cache invalidation issues).
  Mitigation: run for many consecutive blocks; cache invalidations
  surface as divergences in the FIRST block after the
  invalidation.
- **PIT IRQs disabled**: verifier needs determinism. To verify IRQ
  delivery code paths, write specific test ROMs with known IRQ
  sequences and use lockstep mode rather than verify-blocks.
- **Snapshot is full memcpy**: V1 verifier copies the entire guest
  RAM per block. ~1MB × ~15k blocks/sec = ~15 GB/s mem-bandwidth.
  Acceptable for verification mode, deferred CoW optimisation
  available in design doc §4.6.

## 7. Companion: differential fuzzer (Phase 30.17 / 30.18)

Each verifier-aware CPU backend now exposes a fuzzer mode that
generates random instruction streams and feeds them through the
verifier. This surfaces emitter bugs that hand-curated test ROMs
don't reach (each per-CPU fuzzer found at least one real bug on
first run):

```bash
apr-nes --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-gb  --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-gba --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-x86 --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
```

Flags:
- `--fuzz=N` — number of iterations (each gets a fresh random ROM)
- `--fuzz-blocks=M` — max blocks to verify per iteration (default 50-100; bound runtime per iter)
- `--fuzz-seed=S` — reproducibility seed (omit for time-based)
- `--fuzz-continue` — don't stop at first divergence; report all and continue (useful for bug-density measurement; default stops at first for fast bisection)

Each fuzzer also dumps the ROM bytes near the divergent block so the
exact instruction sequence can be disassembled offline. Combine with
the verifier's pre-block state report (`pre:` line) to get the full
context needed to identify the buggy emitter.

### Fuzzer-found bug examples (from Phase 30.18 session)

- **GB STOP (0x10)**: per-instr ignored pad byte while block-JIT
  consumed it. Spec fix: added `read_imm8` step.
- **GB HALT (0x76)**: per-instr `_haltSignal → _halted` transfer
  only happened in `RunCycles`, not in `StepOnePerInstr` used by
  verifier. Runtime fix: added transfer.
- **x86 BlockDetector NOP-fallback**: synthesizing 0x00 (= ADD r/m8,r8
  on x86, not NOP) as silent NOP for unknown opcodes ran wrong
  semantics. Framework fix: only do fallback when 0x00 has zero
  operand steps + length = 1.

The fuzzer is the production tool for finding the *next* emitter
bug; the verifier is the production tool for proving a known-good
workload stays bit-identical across emitter changes.

## 7.5 Bisection tools (Phase 30.18l/m)

When the fuzzer reports a divergence in a multi-instruction block,
narrowing down which instruction is buggy used to require manually
inspecting the LLVM IR or adding println traces inside each emitter.
Two env-var-driven bisection tools (per Gemini-recommended Strategy 1)
make this fast and non-invasive:

### Early Bailout Bisection (`APR_EARLY_EXIT_*`)

Cap a SPECIFIC block (matched by start-PC) to N instructions, recompile
just that block, run verifier again. Preserves JIT optimization
context (register allocation, flag elision, dead-store elimination
across instructions) for ALL OTHER blocks — bugs in those don't get
hidden by the cap.

```bash
APR_EARLY_EXIT_BLOCK_PC=4DF5 \
APR_EARLY_EXIT_INSTR_COUNT=8 \
apr-gb --rom=... --fuzz=80 --fuzz-blocks=5 --fuzz-seed=42
```

Workflow: binary search to find K where cap=K diverges but cap=K-1
doesn't → instruction K is the buggy one. Implemented in
`BlockDetector.cs` at the instruction-loop entry.

### Force Budget (`APR_GB_FORCE_BUDGET`)

Cap GB block-JIT cycle budget globally to N cycles. Useful for
distinguishing per-instruction vs multi-instruction-state bugs: if
divergence persists with `APR_GB_FORCE_BUDGET=1` (≈ single-instruction
blocks), the bug is in a single-instruction emitter; otherwise it's
in multi-instruction block state.

```bash
APR_GB_FORCE_BUDGET=1 \
apr-gb --rom=... --fuzz=80 --fuzz-blocks=50 --fuzz-seed=42 --fuzz-continue
```

NOTE: GB block-JIT budget-exit fires AFTER instruction N's cycle
deduct (not before), so the first instruction always runs regardless
of budget=1. To truly force single-instruction blocks, use early-
bailout with `APR_EARLY_EXIT_INSTR_COUNT=1` and the matching block PC.

## 8. Related docs

- Design: `MD/design/30.15d-verified-blockjit-framework-design.md`
- Investigation history: `MD/design/30.15-blockjit-pc-investigation.md`
- General PC emulator testing: `MD/process/04-advanced-pc-emulator-testing.md`
- Commit QA tiers: `MD/process/01-commit-qa-workflow.md`
