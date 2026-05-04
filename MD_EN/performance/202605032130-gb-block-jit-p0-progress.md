# GB block-JIT P0 progress — infrastructure landed, +70% perf, Blargg partial

> **Phase**: 7 GB block-JIT P0 (foundation tier per [`MD_EN/design/12-gb-block-jit-roadmap.md`](/MD_EN/design/12-gb-block-jit-roadmap.md))
> **Date**: 2026-05-03
> **Status**: P0.1-P0.4 shipped. GB block-JIT runs end-to-end with
> partial Blargg pass; one residual bug (`JR negative` subtest fail).

---

## 1. P0 step results

| Step | Commit | Status |
|---|---|---|
| **P0.1** Variable-width `BlockDetector` + LR35902 length oracle | `fdce42c` | ✅ |
| **P0.2** 0xCB prefix as 2-byte atomic instruction | `381595b` | ✅ |
| **P0.3** Immediate baking via `instruction_word` packing | `da8cf91` | ✅ |
| **P0.4** GB CLI `--block-jit` + JsonCpu wiring + Strategy 2 PC fixes | `5b4092f` | ⚠️ partial |

Each step T1 all-green + T2 8-combo screenshot canonical hash unchanged
(GBA path has no regression).

---

## 2. Perf numbers (GB Blargg `01-special.gb`, --frames=600)

| Backend | Real-time × | MIPS | Δ vs per-instr |
|---|---:|---:|---:|
| `--cpu=legacy` | (host JIT, not measured this round) | ~31 | reference high |
| `--cpu=json-llvm` (per-instr) | 15.6× | 6.16 | baseline |
| `--cpu=json-llvm --block-jit` | 10.0× | **10.49** | **+70%** |

Block-JIT is +70% faster than per-instr (10.5 vs 6.16 MIPS). Still 3x
slower than legacy interpreter at 31 MIPS, with room to close the
"block-JIT should approach or surpass legacy" target — P1 (block-local
state caching) should pull more.

---

## 3. Stale-PC bugs fixed (a batch caught during P0.4)

block-JIT Strategy 2 mechanism: PC slot inside a block is not updated
per-instruction; all "PC reads" should go through `PipelinePcConstant`
(baked constant = `bi.Pc + length`). P0.4 caught 6 emitter sites violating
this assumption:

| # | Location | Bug | Symptom |
|---|---|---|---|
| 1 | `StackOps.Call` `BuildLoad2(pcPtr)` | reads stale block-start PC as return addr | RET jumps back to block start → tight loop |
| 2 | `StackOps.Call` did not set PcWritten=1 | outer loop overwrites call target | call → next instr instead of target |
| 3 | `StackOps.CallCc` thenBB same as #1 | conditional call return addr wrong | RET wrong place |
| 4 | `StackOps.Ret` did not set PcWritten=1 | outer loop overwrites popped PC | RET returns to wrong place |
| 5 | `Emitters.BranchCc` not-taken `BuildLoad2(pcPtr)` | writes stale PC back to memory | when condition not met, CPU jumps back to block start → infinite loop |
| 6 | `BlockFunctionBuilder` per-instr cycle cost fixed at 4 | scheduler under-tick | IRQ/MMIO delay → test ROM behavior off |
| 7 | `EmitContext.PipelinePcConstant` used spec.PcOffsetBytes (not instruction length) | LR35902 multi-byte instruction read_pc miscalculated | JR/JP target wrong |

These bugs are the same class as "Phase 7 A.6.1 ARM Strategy 2 patches",
but ARM never ran into them (because ARM does not use the generic
`call`/`ret`/`branch_cc` ops, but uses its own ArmEmitters with proper
Strategy 2 awareness). **The newly added generic StackOps + Emitters were
validated for the first time on the LR35902 block-JIT path.**

---

## 4. Unresolved bug

**`JR negative` Blargg subtest fail**: `01-special` prints
"01-special\n\n\nJR negative\n" then stalls — meaning the JR-with-negative-offset
subtest is inconsistent between block-JIT and per-instr.

Possible root causes (to verify in P0.5 debug):
- Another stale-PC load not yet caught (need per-emitter audit)
- Mid-block budget exhaustion + writing next-PC has race with branch_cc
- Block boundary behaviour from BlockDetector across segments wrong for some JR pattern
- read_imm8 baking handles JR e8 (signed) sign incorrectly

Next steps:
1. Write minimal repro: hand-write ROM with `JR -2` / `JR -128` / `JR -1`
   various negative offsets, compare per-instr / block-JIT path results
2. If minimal repro fails directly, dump block IR for suspicious patterns
3. If minimal repro passes, the issue is in IRQ/MMIO/scheduler interaction
   (only Blargg triggers it)

---

## 5. T1/T2/T3 record

### T1 by step
| Step | unit tests | new tests |
|---|---|---|
| P0.1 | 363/363 | +3 (variable-width detect, LE pack, ctor-without-oracle) |
| P0.2 | 365/365 | +2 (CB prefix atomic, CB-without-subdec ends block) |
| P0.3 | 365/365 | none (IR behavior validated end-to-end by P0.4) |
| P0.4 | 365/365 | none (manual GB Blargg run) |

### T2 8-combo GBA
Each step all-green canonical hash `7e829e9e837418c0f48c038341440bcb`. No
GBA regression.

### T3 perf
- GBA HLE/BIOS bjit/pi: not re-run (GBA path's hot path was not changed,
  `BlockFunctionBuilder` cycle-cost change is equivalent for ARM behavior
  because ARM cycles.form is mostly "1m" still = 4)
- GB block-JIT: first measurement at 10.5 MIPS (vs per-instr 6.16), +70%

---

## 6. Next steps (P0.5 / P0.5b shipped)

### P0.5 progress update (2026-05-03 follow-up)

| Commit | What was fixed | Result |
|---|---|---|
| `c47d849` | HALT/STOP added as block boundary (detector watches step `op:"halt"` / `"stop"`) | minimal repro `temp/jr-neg-repro.gb` per-instr/bjit consistent |
| `771d170` | EI delay: detector adds one more instr after `lr35902_ime_delayed` before splitting block | T1 all-green, Blargg still failing (partial fix) |

### Unresolved bugs

| Bug | Observation | Hypothesis |
|---|---|---|
| Blargg 01-special "JR negative" subtest fail | block-JIT runs to 60000 frames still stuck → infinite loop (test's own fail-loop) | running into wrong state triggers test failure check; some JR path inconsistent with per-instr; possibly (a) more stale-PC sites (b) flag computation off (c) memory write ordering |
| Blargg 02-interrupts "EI" subtest fail | EI fix only splits block to EI+1; if subsequent block has multiple instrs, those still IME=0 | full fix needs writing eiDelay countdown into IR, or forcing the block after EI to also be 1-instr |

### Reasons for limited debug progress

- No ROM source — cannot directly read what Blargg "JR negative" subtest is doing
- Lockstep comparison between block-JIT and per-instr requires instrumentation (block-JIT cannot precisely 1-instr step)
- Writing simple minimal repros one by one takes time and is hard to hit Blargg corner cases

### Three next-step options

- **(A) Continue chasing JR negative bug**: write a lockstep comparison harness (extend CpuDiff to support bjit), run Blargg to find the first PC where registers diverge. Estimated 1-2 days
- **(B) Fully fix EI delay**: move `_eiDelay` logic into IR — add new micro-op or host extern call. Estimated 0.5-1 day
- **(C) Accept P0 partial, move to P1**: roadmap §3 P1 tier — block-local state caching and detector cross unconditional B. Expected another 30-50% perf. Known partial Blargg pass-rate is documented limitation. Estimated 3-4 days

---

## 7. Related files

- [`MD_EN/design/12-gb-block-jit-roadmap.md`](/MD_EN/design/12-gb-block-jit-roadmap.md) — full priority roadmap
- [`tools/knowledgebase/message/20260503_202431.txt`](/tools/knowledgebase/message/20260503_202431.txt) — full Gemini consultation (variable-width / 0xCB prefix / narrow-int RA three questions)
- `src/AprCpu.Core/Runtime/Lr35902InstructionLengths.cs` — 256-entry length table (P0.1)
- `src/AprCpu.Core/Runtime/BlockDetector.cs` — sequential crawl + prefix dispatch (P0.1+P0.2)
- `src/AprCpu.Core/IR/Lr35902Emitters.cs` line 1125-1180 — read_imm8/16 baking (P0.3)
- `src/AprGb.Cli/Cpu/JsonCpu.cs` — block-JIT path (P0.4)
- `src/AprCpu.Core/IR/StackOps.cs` + `Emitters.cs` — Strategy 2 PC fixes (P0.4)
