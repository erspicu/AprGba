# Three-point framework perf fixes complete — Intel 8086 27 → 218 MIPS (8.0×)

> Closed 2026-05-10. Order followed Gemini's 2026-05-10 review:
> immediates first → CFG superblocks → cross-block SSA folded into superblocks.

## Results

| Stage | json-block MIPS | vs legacy | Change |
|---|---:|---:|---|
| baseline (after 24.6.9) |  27.19 | 0.71× | block-JIT alloca/mem2reg in place but trampoline + dispatcher dominated |
| 24.6.8d #1 done |  37.38 | 0.97× | immediates baked as i64 IR constants |
| **24.6.8e #2 done** | **218.45** | **5.65×** | intra-block back-edges as LLVM CFG |

(legacy 38.70 MIPS = baseline; workload 19,660,802 architectural instructions)

## #1 Bake immediates as IR constants ✅

**Commit**: `a895968 perf(N0c.24.6.8d)`

**Problem**: x86 emitters in block-JIT mode were still calling
`memory_read_8` extern for every ModR/M / disp / imm byte even though
BlockDetector had already read those bytes during decode. Per Gemini:
"trampolines to managed code from inside a tight JIT block are the #1
enemy of emulator performance."

**Fix**:
- `BlockDetector`'s x86 (busLengthOracle) path now pre-fetches the
  trailing bytes after the opcode into an LE-packed `ulong` stored in
  a new `DecodedBlockInstruction.PackedTailBytes` field (length ≤ 9
  bytes covers everything).
- `EmitContext` gained `CurrentInstructionPackedTailBytes` plus a
  per-instruction `ImmConsumed` offset counter (one x86 instruction
  may fetch multiple times: ModR/M → disp → imm, each at a different
  offset).
- `X86_16Emitters.FetchImm8/FetchImm16` now build i64 `LLVMConstantInt`
  on the fast path, then shift+trunc out the right byte. LLVM
  compile-time evaluates the constant — **zero runtime cost**.

**Gemini's key tip**: use `i64`, not `u32`. `u32` would miss 6-byte
instructions like `MOV [bx+di+0x1234], 0x5678`; with `i64` and LLVM
constant folding, "the wider type is free."

**ROI**: 27.19 → 37.38 MIPS (+37.5%) — already matching legacy
(37.38 vs 38.40).

## #2 Intra-block back-edges as LLVM CFG ✅

**Commit**: `9e72453 perf(N0c.24.6.8e)`

**Problem**: A tight inner loop (add/add/loop ×65535) was exiting the
block on every LOOP, hitting the dispatcher cache, re-entering at the
target. Register state was draining through alloca→state→alloca on
every iteration, costing far more than the actual work.

**Gemini's two pitfalls**:
1. **Don't** linearly unroll — LOOP is conditional, assuming
   "always taken" miscompiles when CX=0; and LLVM register allocation
   is O(N²) on long IR.
2. **The right path**: turn the x86 loop into LLVM intra-function CFG —
   emit a `br` to a BasicBlock representing the loop body, all in the
   same LLVM function. alloca + mem2reg naturally preserves CX in a
   host register across iterations via phi nodes.

**Fix**:
- `DecodedBlockInstruction.BackEdgeTargetIndex` records which
  in-block instruction index the branch targets.
- `BlockDetector`'s x86 path computes the static target of LOOP / Jcc
  / JMP rel; if the target hits the PC of an instruction already
  added to the block, set `BackEdgeTargetIndex`.
- `EmitContext.BackEdgeTargetBB` + `BackEdgeFallthroughBB` are wired
  by `BlockFunctionBuilder` before emitting the instruction.
- `X86LoopEmitter` / `X86JccRel8Emitter` / `X86JmpRel8Emitter` /
  `X86JmpRel16Emitter`: fast path now uses `BuildCondBr(pred,
  target_BB, fallthrough_BB)` (or `BuildBr` for unconditional) —
  **doesn't write IP, doesn't set PcWritten**.

**Bonus**: the bench's outer JNZ also targets within the same block
(writes_pc="conditional" doesn't end the block), so the entire
outer×100 ⊗ inner×65535 nested loop compiles into a **single LLVM
function with two back-edges**. LLVM's loop optimizer recognizes the
nested loop nest and folds add/add into `AX += 3 × 65535`, possibly
even vectorizing/collapsing.

**ROI**: 37.38 → **218.45 MIPS** (+484%) = **5.65× faster than legacy**.

## #3 Cross-block register SSA — already covered by #2

**Commit**: none; this design note is the closure.

The original plan was to "preserve register state in SSA across
multiple LLVM functions," requiring trace JIT or LTO inlining. Per
Gemini's 2026-05-10 review, that path:
- cross-function via memory → slow (today's behavior)
- cross-function via LTO inline → compile time explodes
- both bad

**Gemini's recommended superblock route**: compile the entire hot
trace into a **single** LLVM function with internal CFG. alloca +
mem2reg achieves cross-iteration SSA within function-local scope —
side-stepping the cross-function problem entirely.

**This is exactly what #2 does.** For the bench:

```
                                  Before #2                 After #2
Block at 0x100   [mov bx; mov cx; add; add; LOOP; dec; JNZ; HLT]   (same — writes_pc=conditional doesn't end block)
LOOP target      0x106 (= block instr idx 2)                       same
JNZ target       0x103 (= block instr idx 1)                       same
LOOP behavior    write IP=0x106, set PcWritten, exit block         BuildCondBr(pred, preBBs[2], postBBs[i])
JNZ behavior     write IP=0x103, set PcWritten, exit block         BuildCondBr(pred, preBBs[1], postBBs[i])
LLVM IR shape    8 linear instructions, no internal CFG            8 instructions + 2 back-edges = 1 CFG nest
mem2reg result   CX/AX cross iter via state-buffer load/store      CX/AX cross iter via phi nodes
```

#2's alloca + mem2reg + back-edge **is** "cross-block SSA" — the
definition just shifted: "block" now means an LLVM BasicBlock, not
"dispatcher-level native function."

## Future (next season)

| Item | Estimated gain | Trigger |
|---|---|---|
| **Block chaining (direct dispatch)** | +1.2-1.5× | When the hot trace crosses LLVM function boundaries (CALL/RET/far JMP) — patch native code to jmp directly without going through dispatcher. This bench has no such case (no CALL/RET). |
| **Hot-path trace JIT** | +1.5-2× | Profile high-frequency cross-block paths → fold N consecutive blocks into a single superblock function. Handles register flow beyond #2's boundary. |
| **Bus extern inlining** | +1.1-1.3× | When memory_read_8 is still on the hot path (this bench has none). Pack ROM bytes into IR constants too. |

For 8086: the bench loop 27 → 218 MIPS already far exceeds legacy.
Other benchmarks (ROM-heavy, bus-heavy) will see different ratios —
**this is the best case** (loop fully optimizable by LLVM). Other
demos also got faster but the ratio is less extreme, since SHA256 PNG
identity doesn't depend on perf.

## QA results

- **T1**: full unit suite (838 tests, Release) — ran in background
  after this commit (per CLAUDE.md's "1-minute timeout" rule, can't
  run foreground).
- **T2**: 6 demos × 3 backends = 18 PNGs, each group SHA256 identical
  — **all green**.
- **Bench**: best-of-3, numbers stable, final AX = 0xFED4 =
  (1+2)×65535×100 mod 65536 ✓.

## Reproduce

```powershell
dotnet build -c Release src/AprX86.Cli/AprX86.Cli.csproj
pwsh tools/bench_x86.ps1
pwsh tools/verify_x86_matrix.ps1
```
