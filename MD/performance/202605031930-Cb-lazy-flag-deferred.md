# Phase 7 C.b lazy flag retry — **deferred** (correctness regression in BJIT/BIOS-LLE)

> **策略**：以 main `c5d32c6` 的 alloca-shadow 設計為藍本，在 recovery
> 分支重新實作。預期收益：+0.5-0.6% MIPS（per main 紀錄）。
>
> **結果**：實作了完整 4 處改動（`EmitContext.StatusShadowAllocas`/
> `EntryBlock`、`CpsrHelpers` SetFlag/ReadFlag/Drain/Reinit、
> `InstructionFunctionBuilder` end-of-fn drain、`ArmEmitters`
> drain+reinit pattern in WritePsr/RestoreCpsr/RaiseException）。**360
> unit tests pass**，但 T2 (8-combo screenshot matrix) 在 BJIT + BIOS LLE
> path crash 多個 combo:
>
> - `arm.gba HLE block-JIT`: 跑出空白畫面（不是 "All tests passed"）
> - `arm.gba BIOS LLE per-instr`: `Undecodable instruction 0x00C0E7F6
>   at PC=0x00002D92 (ARM)` — CPSR.T 在某個 control-flow 路徑沒對齊
> - `arm.gba BIOS LLE block-JIT`: `Block must contain at least one
>   instruction` — block detector 撞到 undecodable 區
> - `thumb.gba BIOS LLE` 兩種模式：類似 crash
>
> **決定**：**revert 全部 C.b 改動**（per Tier 3 QA workflow：T2 fail =
> 不准 commit）。回到 H.a 4-pass set 的乾淨狀態。

---

## 1. 嘗試的設計（已 revert）

```
EmitContext.cs:
  + Dictionary<string, LLVMValueRef> StatusShadowAllocas
  + LLVMBasicBlockRef EntryBlock (captured in ctor)

Emitters.cs CpsrHelpers:
  ~ SetStatusFlagAt: writes shadow alloca (was real CPSR ptr)
  ~ ReadStatusFlag: reads shadow if exists, else real
  + GetOrCreateShadow: alloca + init at entry block (dominance)
  + DrainShadow: shadow → real
  + ReinitShadowFromReal: real → shadow (after raw write)
  + DrainAllShadows: drain all (called by builders before ret)

ArmEmitters.cs:
  + In WritePsr / RestoreCpsrFromSpsr / RaiseExceptionEmitter:
      DrainShadow(CPSR);    // step 1
      <raw CPSR write>      // step 2
      ReinitShadowFromReal(CPSR);  // step 3

InstructionFunctionBuilder.cs:
  + DrainAllShadows before BuildRetVoid (both normal + cond-skip path)

BlockFunctionBuilder.cs (already had):
  + DrainAllShadows in block_exit BB
```

---

## 2. 為何崩了（root cause hypothesis）

shadow alloca 必須在 entry block (LLVM mem2reg dominance 要求)。
但 init load (= 第一次讀 real CPSR) 也跟著 alloca 在 entry block 跑
— 等於把「entry-time real CPSR」snapshot 進 shadow。

當 instruction body 中段做 `MSR CPSR_c, ...` (raw CPSR write) 時：
1. DrainShadow → 把 shadow 帶 pending 寫回 real (含 entry-time 值 +
   pending flag bits)
2. WritePsr 用 mask-and-or 改寫 real
3. ReinitShadowFromReal → 把 post-write real 載回 shadow ✓

問題：**ReinitShadowFromReal 是在 body 中段做的 store 到 shadow**。
mem2reg 看到 alloca 的 value 在多個 BB 被寫（entry init store + body
reinit store + various SetFlag stores）。它需要 PHI 來 reconcile。

如果某條 control-flow path 從 entry → cond-skip-endBlock（沒走 body），
那條路徑上 shadow value = entry-init = entry-time real CPSR。drain
at end 寫 entry-time CPSR 回 real → 蓋掉 raw write 之後的值（如果某
其他路徑改了它）。

更糟：alloca 是 SSA-promoted 後在每 BB 的值需要 mem2reg 算 PHI。如果
PHI 算錯（特別是 reinit-after-MSR 跟 cond-skip 路徑的混合）就會用錯
shadow value。

實測現象支持此假說：CPSR.T 沒對齊 → CPU 用錯指令集 decoder → 拿到
undecodable byte。

**正確的設計可能要：**
- 每個 BB 入口 reinit shadow（隱式 PHI），但成本高
- 或不用 alloca shadow，改用 explicit SSA value chain（C.b 第一次嘗試
  失敗的方式，dominance 衝突）
- 或乾脆放棄 — GVN+DSE 已在 raw CPSR pattern 上做大半同樣工作（main
  commit 自己也承認 "+0.5-0.6%" 是小幅）

---

## 3. 為何主分支 (`c5d32c6`) 沒這個問題？

可能差異：
1. main 沒 Phase 1a/1b 的 cycles_left + budget exit 機制（額外 BB 增加
   control-flow 複雜度）
2. main 沒 block-JIT 的 multi-instr block（per-instr 函數小、shadow
   pattern 簡單）
3. main 的 BIOS LLE path 不一樣（recovery 修了多個 A.6.1 bugs，main
   仍 broken；對照基準不同）

也可能 main 也有 latent bug 但 test ROM coverage 沒抓到。

---

## 4. 決定 + 後續

**Defer C.b retry**。原因：
- 收益 <1% MIPS（per main note 自己證實）
- GVN+DSE 在 H.a 已啟用 → raw CPSR pattern 的「合併 N 連續寫」收益
  已實現
- 正確實作太複雜（需處理 mem2reg PHI 跨 raw-write 的 dominance）
- recovery 分支的 block-JIT IR 結構（Phase 1a budget BB + 多 instr
  block）跟 main 不同，main 的 C.b 設計不可直接套用

**保留環境 (env-gated debug)**：未來如果有 perf 量測到 SetStatusFlag/
ReadStatusFlag 是 hot path（目前不是），再考慮重新設計。

---

## 5. Tier 3 QA 結果

| 階段 | 結果 |
|---|---|
| T1 360 unit tests | ✅ 360/360 pass |
| T2 8-combo screenshot | ❌ 5/8 fail (HLE BJIT 1 個空白、4 個 BIOS LLE crash) |
| T3 3-run loop100 bench | (沒跑 — T2 fail 直接 abort) |

**revert 後狀態 (HEAD = 227a436)**：T1 + T2 + T3 全綠 (per
202605031900-Ha-llvm-pass-pipeline-reenabled.md)

---

## 6. 相關文件

- [`MD/performance/202605030148-lazy-flag-attempt-postmortem.md`](/MD/performance/202605030148-lazy-flag-attempt-postmortem.md) — 第一次
  C.b 嘗試 (deferred-batch + SSA dominance crash)
- [`MD/performance/202605031900-Ha-llvm-pass-pipeline-reenabled.md`](/MD/performance/202605031900-Ha-llvm-pass-pipeline-reenabled.md) — H.a
  4-pass set re-enabled，目前 baseline
- main `c5d32c6` — main 分支的 C.b retry，**結構不適合直接 cherry-pick
  到 recovery 分支**
- [`MD/process/01-commit-qa-workflow.md`](/MD/process/01-commit-qa-workflow.md) — 本次走 Tier 3 流程，T2 fail
  按規則 abort + revert
