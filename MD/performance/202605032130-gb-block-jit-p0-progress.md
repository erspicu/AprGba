# GB block-JIT P0 progress — infrastructure landed, +70% perf, Blargg partial

> **Phase**: 7 GB block-JIT P0 (foundation tier per `MD/design/12-gb-block-jit-roadmap.md`)
> **Date**: 2026-05-03
> **Status**: P0.1-P0.4 shipped. GB block-JIT runs end-to-end with
> partial Blargg pass; one residual bug (`JR negative` subtest fail).

---

## 1. P0 步驟成果

| Step | Commit | Status |
|---|---|---|
| **P0.1** Variable-width `BlockDetector` + LR35902 length oracle | `3024100` | ✅ |
| **P0.2** 0xCB prefix as 2-byte atomic instruction | `0cb93a8` | ✅ |
| **P0.3** Immediate baking via `instruction_word` packing | `7a8305a` | ✅ |
| **P0.4** GB CLI `--block-jit` + JsonCpu wiring + Strategy 2 PC fixes | `adddade` | ⚠️ partial |

每步 T1 全綠 + T2 8-combo screenshot canonical hash 不變（GBA path 沒
regression）。

---

## 2. Perf 數字（GB Blargg `01-special.gb`，--frames=600）

| Backend | Real-time × | MIPS | Δ vs per-instr |
|---|---:|---:|---:|
| `--cpu=legacy` | (host JIT, 沒量這次) | ~31 | reference high |
| `--cpu=json-llvm` (per-instr) | 15.6× | 6.16 | baseline |
| `--cpu=json-llvm --block-jit` | 10.0× | **10.49** | **+70%** |

block-JIT 比 per-instr 快 +70%（10.5 vs 6.16 MIPS）。比 legacy 直譯
的 31 MIPS 還慢 3×，距「block-JIT 應該逼近或超 legacy」目標還有空間 —
P1 (block-local state caching) 應該再撈一筆。

---

## 3. 已修的 stale-PC bug（P0.4 中段一次 batch 抓出來）

block-JIT Strategy 2 機制：block 內 PC slot 不per-instruction 更新，所有
"PC reads" 應該走 `PipelinePcConstant`（baked 常數 = `bi.Pc + length`）。
P0.4 抓出 6 處違反這個假設的 emitter：

| # | Location | Bug | Symptom |
|---|---|---|---|
| 1 | `StackOps.Call` `BuildLoad2(pcPtr)` | reads stale block-start PC as return addr | RET 跳回 block 起始 → tight loop |
| 2 | `StackOps.Call` 沒 set PcWritten=1 | outer loop 蓋掉 call target | call → next instr 而非 target |
| 3 | `StackOps.CallCc` thenBB 同 #1 | conditional call return addr 錯 | RET wrong place |
| 4 | `StackOps.Ret` 沒 set PcWritten=1 | outer loop 蓋掉 popped PC | RET 退回到不該去的地方 |
| 5 | `Emitters.BranchCc` not-taken `BuildLoad2(pcPtr)` | 寫 stale PC 回 memory | 條件不滿足時 CPU 跳回 block 起始 → 無窮迴圈 |
| 6 | `BlockFunctionBuilder` per-instr cycle cost 固定 4 | scheduler under-tick | IRQ/MMIO 延遲 → test ROM 行為偏移 |
| 7 | `EmitContext.PipelinePcConstant` 用 spec.PcOffsetBytes（非 instruction length） | LR35902 multi-byte 指令的 read_pc 算錯 | JR/JP target wrong |

這些 bug 都跟「Phase 7 A.6.1 ARM Strategy 2 修補」是同一類，但 ARM 從沒
碰到（因 ARM 不用 generic `call`/`ret`/`branch_cc` ops，用自己的
ArmEmitters with proper Strategy 2 awareness）。**新增的 generic
StackOps + Emitters 在 LR35902 block-JIT 路徑第一次被驗證。**

---

## 4. 還沒解的 bug

**`JR negative` Blargg 子測試 fail**：`01-special` 印出
"01-special\n\n\nJR negative\n" 後停 — 表示 JR 用負 offset 的子測試在
block-JIT 路徑跟 per-instr 不一致。

可能 root cause（待 P0.5 debug 再驗）：
- 還有一處 stale-PC load 沒抓到（需逐 emitter audit）
- Block 中段 budget 用完 + 寫 next-PC 跟 branch_cc 互動有 race
- Block 跨段 BlockDetector 的 boundary 行為對某 JR 模式不對
- read_imm8 baking 對 JR e8 (signed) 的 sign 處理有差

接下來步驟：
1. 寫 minimal repro：手寫 ROM 含 `JR -2` / `JR -128` / `JR -1` 各種負 offset，比對 per-instr / block-JIT 兩 path 結果
2. 若 minimal repro 直接掛，dump block IR 看可疑 pattern
3. 若 minimal repro pass，問題在 IRQ/MMIO/scheduler 互動（Blargg 才會踩）

---

## 5. T1/T2/T3 紀錄

### T1 全段
| Step | unit tests | 新增 test |
|---|---|---|
| P0.1 | 363/363 | +3 (variable-width detect, LE pack, ctor-without-oracle) |
| P0.2 | 365/365 | +2 (CB prefix atomic, CB-without-subdec ends block) |
| P0.3 | 365/365 | 沒新增（IR 行為由 P0.4 端到端驗證) |
| P0.4 | 365/365 | 沒新增（手動 GB Blargg run） |

### T2 8-combo GBA
每步全綠 canonical hash `7e829e9e837418c0f48c038341440bcb`。沒 GBA
regression。

### T3 perf
- GBA HLE/BIOS bjit/pi: 沒重跑（GBA path 沒被改動到 hot path，
  `BlockFunctionBuilder` cycle-cost 改動對 ARM 行為等效，因 ARM
  cycles.form 多半 "1m" 仍 = 4）
- GB block-JIT: 第一次量到 10.5 MIPS（vs per-instr 6.16），+70%

---

## 6. 下一步（P0.5 預計）

1. 抓 `JR negative` bug — 必須先解才能把 P0 收乾淨
2. P0 收尾：寫完整 T3 bench（多 ROM、多 frame budget），紀錄到單獨檔
3. 進 P1：block-local state caching + detector 跨 unconditional B（roadmap §3 P1 tier）

---

## 7. 相關檔案

- `MD/design/12-gb-block-jit-roadmap.md` — 完整優先順序 roadmap
- `tools/knowledgebase/message/20260503_202431.txt` — Gemini 諮詢全文
  （variable-width / 0xCB prefix / narrow-int RA 三題）
- `src/AprCpu.Core/Runtime/Lr35902InstructionLengths.cs` — 256-entry length table（P0.1）
- `src/AprCpu.Core/Runtime/BlockDetector.cs` — sequential crawl + prefix dispatch（P0.1+P0.2）
- `src/AprCpu.Core/IR/Lr35902Emitters.cs` line 1125-1180 — read_imm8/16 baking（P0.3）
- `src/AprGb.Cli/Cpu/JsonCpu.cs` — block-JIT path（P0.4）
- `src/AprCpu.Core/IR/StackOps.cs` + `Emitters.cs` — Strategy 2 PC fixes（P0.4）
