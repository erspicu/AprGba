# Intel 8086 backend MIPS comparison (2026-05-10)

First-cut MIPS measurement across the three Apr-X86 backends after
fixing the silent block-JIT-degenerates-to-per-instr bug (commit
`24a7dc8`).

## Workload

`test-roms/x86/bench-loop.com` — synthetic outer-loop test, 18 bytes:

```
mov bx, 100        BB 64 00       ; outer counter
outer_top:
  mov cx, 0xFFFF   B9 FF FF       ; inner counter
inner_top:
  add ax, 1        05 01 00       ; 2 ALU ops in inner body
  add ax, 2        05 02 00
  loop inner_top   E2 F8          ; CX-=1, jump if CX!=0
  dec bx           4B
  jnz outer_top    75 F2
hlt                F4
```

Per outer iter: 1 (mov cx) + 65535 × 3 (add/add/loop) + 1 (dec) +
1 (jnz) = 196,608 architectural instructions × 100 outer = **19,660,801**
inst total + 1 (final hlt) = **19,660,802 architectural instructions**.

## Results (best of 3 runs, Release build)

| Backend       | Wall ms | Startup ms | Net ms | Net MIPS | vs legacy |
|---------------|---------|-----------|--------|---------:|----------:|
| `legacy`      |     574 |        63 |    511 |    38.48 |    1.00×  |
| `json`        |    3872 |      1179 |   2693 |     7.30 |    0.19×  |
| `json-block`  |    1212 |       489 |    723 |    27.19 |    0.71×  |

## Interpretation

**Startup baselines** (1-byte HLT-only ROM, just .NET CLR + LLVM init):
- `legacy` 63 ms — dotnet CLR + assembly load
- `json` 1179 ms — adds LLVMSharp init + SpecCompiler eagerly compiles
  all ~256 opcode functions through ORC LLJIT
- `json-block` 489 ms — adds LLVM init but defers compile until first
  block hits cache miss; cheaper because only the few unique blocks
  this workload touches get compiled

**Net throughput**:
- `legacy` 38 MIPS — hand-coded C# switch dispatch, no JIT overhead
- `json` 7 MIPS (5.3× slower than legacy) — per-instruction LLVM call
  overhead dominates. Each Step() = (managed → native fn ptr → managed)
  trampoline plus state-buffer load/store for every register touch.
- `json-block` 27 MIPS (1.4× slower than legacy, 3.7× faster than `json`) —
  alloca + mem2reg promotes register state to LLVM SSA values inside
  the block; cross-instruction transitions are pure LLVM IR rather
  than managed-native trampolines.

**Block-JIT batching**: the inner loop body (add/add/loop) becomes
one block. Each Step() runs that 3-instr block once (LOOP iterates
back, sets PcWritten, exits the block — re-entered on next Step).
Step-to-instruction ratio: `19,660,802 / 6,553,500 = 3.0 instr/Step`.

## What we'd need to close the legacy gap

Per Gemini's 2026-05-10 architectural review (see commit `74d27a4`):

1. **Bake immediates as IR constants** (24.6.8d future work) —
   replace the runtime `memory_read_8` calls in `x86_fetch_imm8`/16
   with `LLVM ConstantInt` literals captured at decode time.
   Eliminates the per-instruction trampoline + bus dispatch for
   immediates. Easy ~2× win.

2. **Cross-jump follow into back-edges** (already done for LR35902 in
   `BlockDetector` for JR/JP — port to x86 LOOP/JMP rel8 with
   constant target). Lets a tight loop body compile into ONE big
   unrolled block instead of restarting at the LOOP each iteration.
   Could push block size from 3 to 65535 instructions for our bench.

3. **CPU state shadow promotion across blocks** (P1 #5 in Phase 7
   plan). Currently registers are flushed back to the state buffer
   at every block exit. Keeping them in LLVM SSA across hot paths
   would close most of the remaining gap.

These are framework extensions that benefit ALL CPUs (NES already has
some), not just x86. Reasonable next-quarter targets.

## Reproduce

```
dotnet build -c Release src/AprX86.Cli/AprX86.Cli.csproj
pwsh tools/bench_x86.ps1
```

Bench script writes the `.com` ROM into `test-roms/x86/bench-loop.com`
and the baseline into `temp/baseline-hlt.com` automatically.
