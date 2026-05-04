# Phase 7 H.a re-enabled — explicit LLVM new-pass-manager pipeline (no instcombine yet)

> **策略**：原 H.a (`d929532`) 在 recovery branch 因 instcombine miscompile
> BIOS LLE 路徑被禁用。Recovery 分支完成 A.6.1 一系列 Strategy 2 修復後
> （`5af9d36` read_reg(15) after PC-write、`ab1204e` block_store STM with
> R15、`05c285a` MMIO sync re-entry guard 等），重新啟用 RunPasses 嘗試
> 取回 alloca→SSA + DSE + GVN 的優化收益。
>
> **Hypothesis**：A.6.1 修復後 IR pattern 較乾淨，instcombine 可能也修了；
> 5-pass 全跑或許 OK。
>
> **結果**：mem2reg + gvn + dse + simplifycfg → **360 tests pass**, BIOS LLE
> + ROM matrix 全綠 (md5 hash 一致), perf 在 noise band (-2% to +2%)。
> 加入 **instcombine** 仍會 miscompile 3 個 BlockFunctionBuilderTests
> (Block_ThreeMovs / Block_FirstInstructionCondFails / AddModule_*)
> — R-register store 被誤殺。先用 4-pass set 上線；instcombine 修復
> 留到 Phase 7 H.a-instcombine 另外追。
>
> **決定**：保留 4-pass set。perf 中性但邏輯上正確 + 為未來 C.b lazy
> flag retry 鋪 mem2reg 基礎。

---

## 1. 結果（3-run avg）

| ROM                  | Backend     | run1  | run2  | run3  | **avg** | 上次 (no H.a) | **Δ**     |
|----------------------|-------------|------:|------:|------:|--------:|--------------:|----------:|
| GBA arm-loop100      | json-llvm pi | 8.00 | 7.94  | 8.01  | **7.98**| 8.15          | -2.1%     |
| GBA arm-loop100      | block-JIT   | 10.22 | 10.18 | 10.25 | **10.22**| 10.35        | -1.3%     |
| GBA thumb-loop100    | json-llvm pi | 8.12 | 8.06  | 8.25  | **8.14**| 8.14          | 0.0%      |
| GBA thumb-loop100    | block-JIT   | 11.34 | 11.54 | 11.74 | **11.54**| 11.30        | +2.1%     |

**邏輯驗證 (Tier 2-4)：**
- 360 unit tests pass
- 8-combo screenshot matrix 全部 md5 hash 一致 (`7e829e9e837418c0f48c038341440bcb` = "All tests passed")
- BIOS LLE PI vs BJIT IRQs 對齊：263 / 263

---

## 2. 改動範圍

```
src/AprCpu.Core/Runtime/HostRuntime.cs:
  + Compile():  RunOptimizationPipeline(_initialModule)  uncommented
  + AddModule(): RunOptimizationPipeline(module)          uncommented (per-block JIT modules)
  ~ passes string: "mem2reg,gvn,dse,simplifycfg"  (instcombine excluded — see §3)
```

`RunOptimizationPipeline()` method 本身在 HEAD 已存在，只是兩處呼叫被 comment 掉。
Compile 多 ~50ms 一次性 cost，AddModule 每個 block 多 ~5-15ms。

---

## 3. instcombine 為何排除

啟用 `mem2reg,instcombine<no-verify-fixpoint>` 跑 unit tests：

```
失敗! - 失敗: 3，通過: 357
  AprCpu.Tests.BlockFunctionBuilderTests.Block_ThreeMovs_AllExecuteAndPcAdvances
  AprCpu.Tests.BlockFunctionBuilderTests.Block_FirstInstructionCondFails_SecondStillRuns
  AprCpu.Tests.BlockFunctionBuilderTests.AddModule_PostCompile_RoundTripExecutes
```

失敗形式：`Assert.Equal(2u, ReadI32(state, rt.GprOffset(1)))` 拿到 0 而非 2。
即 `MOV R1, #2` 的 store 被消除。

只用 `instcombine<no-verify-fixpoint>` 不加 mem2reg 也同樣 fail。
不加 instcombine 的其他組合（mem2reg / gvn / dse / simplifycfg / 任意組合）
都全綠。

**Hypothesis**：BlockFunctionBuilderTests 直接 invoke JIT 函數、CPSR=0x10
+ cycles_left=1_000_000 為已知常數。`instcombine` 可能：
- 把 `state[GPR[1]_offset] = 2` 視為 dead store（因函數內後續無讀取、且 pointer
  param 沒 noalias attribute 但 instcombine 自行做 escape analysis）
- 或常數折疊 budget check (`1_000_000 - 4 <= 0` always false) 後觸發
  cascade 把整個 instruction 的 IR 折掉

需另起一條線追：dump 加 instcombine 前後 IR、找具體 transformation、
可能是 emitter 端 IR pattern 要改（例如加 `volatile` 或 `noalias`），
或 instcombine 子 pass 要排除。

**負面影響**：失去 instcombine 的 peephole 優化（select/branch
simplification、bit-twiddling 折疊等）。對我們 IR 大多很短的特性影響有限。

---

## 4. 為什麼 perf 漲幅小

我們的 emitter IR 大多：
- per-instr fn 短小 (5-30 IR ops)
- 沒大量 alloca shadow（C.b lazy flag 還沒上、所以 mem2reg 沒太多 promote 對象）
- 沒重複 load 同一個 GPR 多次（CSE/GVN 沒太多 work）

block-JIT IR 較長 (16+ instrs/block) 但每條 instr 之間記憶體互動少，
DSE/GVN 找不到太多消除機會。

真正的收益要等：
1. **C.b lazy flag retry**（用 alloca shadow + mem2reg）— 預期 +5-15%
2. **instcombine 修復** — 額外 +1-3%
3. **block IR 更積極的 inline**（例如 inline cycle 計數的 sub/icmp pattern）

---

## 5. Phase 7 累計（recovery branch）

| 階段 | GBA arm pi | GBA arm bjit | GBA thumb pi | GBA thumb bjit |
|---|---:|---:|---:|---:|
| 7.B.h (recovery baseline f304376) | 8.33 | (broken) | 8.39 | (broken) |
| 7.A.6.1 BIOS LLE complete + STM PC fix (ab1204e) | 8.15 | 10.35 | 8.14 | 11.30 |
| **7.H.a re-enabled (this)** | **7.98** | **10.22** | **8.14** | **11.54** |

Block-JIT 從 broken → working → 11.54 MIPS / 2.8× real-time。
Per-instr 持平 (~8.0)。

---

## 6. 相關文件

- `MD/process/01-commit-qa-workflow.md` — 本次走 Tier 4 流程
- `MD/performance/202605030148-lazy-flag-attempt-postmortem.md` — C.b 第一次失敗紀錄
- 原 H.a commit `d929532`（main 分支，被 recovery 取代）
- 待辦：Phase 7 H.a-instcombine — bisect 哪個 instcombine sub-pass 出包
