# Phase 7 H.a-instcombine — fixed (datalayout missing → struct GEP miscompile)

> **策略**：把 `instcombine<no-verify-fixpoint>` 加回 default pass list。
> 之前認為 instcombine 跟 `switch i32 poison` UB-propagation 有關
> 完全錯了 — 真正 root cause 是 module 沒設 `target datalayout`，
> instcombine 用預設 layout（struct align 不同）導致 struct GEP 算出錯誤
> byte offset。
>
> **結果**：T1 + T2 + T3 全綠。bench 微增 / 持平 (+0.2~1.8%)。
>
> **決定**：保留。default pass list 從 4-pass 變 5-pass。

---

## 1. Root cause analysis

### 1.1 Symptom

加 `instcombine` 進 pass list 後 3 個 BlockFunctionBuilderTests 都掛：
```
Block_ThreeMovs_AllExecuteAndPcAdvances:
  Expected: 2 (R1)
  Actual:   0
```

加 `--filter Block_ThreeMovs` + `APR_IR_AFTER` dump 對照 with/without
instcombine 的 IR：

**Without instcombine** (passing) — uses struct-typed GEP:
```llvm
%pc_written_ptr = getelementptr { i32 ×37, i64, i32, i8, i32 }, ptr %state, i32 0, i32 39
%cycles_left_ptr = getelementptr { ... }, ptr %state, i32 0, i32 40
```

**With instcombine** (failing) — instcombine canonicalised to byte GEP:
```llvm
%pc_written_ptr1 = getelementptr i8, ptr %state, i64 160   ; ← 應該 164
%cycles_left_ptr = getelementptr i8, ptr %state, i64 164   ; ← 應該 168
```

兩邊**差 4 bytes**！

### 1.2 Why

CpuStateLayout 結構（41 fields，ARM7TDMI typical）：
```
field 0..36 : 37 × i32         → bytes 0..147
field 37    : i64 (CycleCounter) — needs 8-byte align → pad 4 → byte 152
field 38    : i32 (PendingExc)   → byte 160
field 39    : i8  (PcWritten)    → byte 164
field 40    : i32 (CyclesLeft)   → byte 168 (3-byte pad after i8)
```

Host runtime 用 `LLVM.OffsetOfElement(_targetData, ..., 39)` = 164 — 對的。
但 instcombine 跑的時候 module 沒設 `target datalayout` line，instcombine
fall back 到 LLVM 內建 default datalayout，那個 default 對 i64 不一定強制
8-byte alignment（或對 packed 計算方式不同），算出來 field 39 = 160。

實際上 ir_no_ic.ll / ir_af.ll header 裡 grep 完全沒 `target datalayout`
或 `target triple`：
```
; ModuleID = 'AprCpu_ARMv4T'
source_filename = "AprCpu_ARMv4T"
@host_swap_register_bank = ...   ← 直接跳過 layout/triple
```

JIT path 是這樣建 datalayout 的（HostRuntime.cs 修改前）：
```csharp
public void Compile() {
    BindUnboundExternsToTrap(_initialModule);
    RunOptimizationPipeline(_initialModule);  // ← 此時 module 沒 datalayout

    // ... 建 LLJIT 後才從 OrcLLJITGetDataLayoutStr 拿到 layout
    var dlStrPtr = LLVM.OrcLLJITGetDataLayoutStr(_lljit);
    _targetData = LLVMTargetDataRef.FromStringRepresentation(dlStr);
    StateSizeBytes = LLVM.SizeOfTypeInBits(_targetData, Layout.StructType) / 8;
}
```

`OffsetOfElement(_targetData, ...)` 用對的 layout，但 instcombine 在
`RunOptimizationPipeline` 內跑時 module 還沒 layout — 兩邊 disagree。

### 1.3 Why 之前 mem2reg/gvn/dse/simplifycfg 沒事？

那 4 個 pass 不會把 struct GEP 重寫成 byte GEP — 它們保留 struct GEP 形式，
struct GEP 在 LLVM IR 裡是「結構性指代」 (符號性的 field 39)，不依賴 layout。
真正的 byte offset 算到 codegen 階段才發生（那時 LLJIT 已經設了 datalayout，
所以對的）。

只有 instcombine 會把 struct GEP **canonicalise** 成 `i8` GEP — 那時就
依賴 datalayout 的對齊規則 — module 沒 layout 就猜錯。

### 1.4 詐騙線索：earlier hypothesis was wrong

之前在 `202605031900-Ha-llvm-pass-pipeline-reenabled.md` 推測是
`switch i32 poison` UB-propagation。先試了在 `RestoreCpsrFromSpsr`
加 `BuildFreeze` blocks poison — **完全沒影響**，T1 仍掛 3 個一模一樣
test。dump IR 才發現是 GEP offset 問題，跟 switch / poison 一點關係都沒有。
freeze patch 已撤回。

---

## 2. Fix

`HostRuntime.cs`：

1. **Compile() 重排序**：先建 LLJIT 拿到 datalayout，**先 SetDataLayout
   到 module，再跑 RunOptimizationPipeline**，最後才 hand 給 JIT。
2. **記住 `_dataLayoutStr`**：`AddModule()`（per-block JIT module path）
   也要先 SetDataLayout 才跑 pipeline，邏輯一致。
3. **default pass list 加回 instcombine**：
   `mem2reg,instcombine<no-verify-fixpoint>,gvn,dse,simplifycfg`

```csharp
// 新 helper
private static void SetModuleDataLayoutString(LLVMModuleRef module, string dlStr)
{
    if (string.IsNullOrEmpty(dlStr)) return;
    var bytes = System.Text.Encoding.ASCII.GetBytes(dlStr + "\0");
    fixed (byte* p = bytes) LLVM.SetDataLayout(module, (sbyte*)p);
}
```

---

## 3. Tier 4 QA results

| Stage | Result |
|---|---|
| T1 360 unit tests | ✅ 360/360 pass |
| T2 8-combo screenshot matrix | ✅ all 8 = `7e829e9e837418c0f48c038341440bcb` |
| T3 3-run loop100 bench | ✅ 全綠（見下） |

### T3 bench (3-run avg, MIPS)

| combo | run1 | run2 | run3 | avg | baseline (H.a 4-pass) | Δ |
|---|---|---|---|---|---|---|
| arm pi   | 8.17  | 8.21  | 7.99  | 8.12 | 7.98  | +1.8% |
| arm bjit | 10.50 | 10.29 | 10.15 | 10.31 | 10.22 | +0.9% |
| thumb pi | 8.18  | 8.21  | 8.09  | 8.16 | 8.14  | +0.2% |
| thumb bjit | 11.35 | 11.63 | 11.31 | 11.43 | 11.54 | -1.0% |

- Discreteness: max-min < 3.5% per combo（< 5% noise band）
- 沒有 combo 退步 > 5% rule

收益小（per-instr +0~2%, bjit ±1%）— 跟 main note 自承的 "+0.5-0.6%"
類似。instcombine 主要好處在 codegen 階段更乾淨（少冗餘
mov/and/select），對 hot loop 影響有限。重點是**正確性恢復** —
拿掉這個 4-byte GEP miscompile 不再需要 env-gate 來 bisect。

---

## 4. 與 main 對齊狀態

到此 recovery branch 跟 main 在 H.a 完全對齊（都帶 instcombine）。
剩下唯一 feature gap：

- C.b lazy flag — recovery defer (見 202605031930-Cb-lazy-flag-deferred.md)，
  收益 <1% 不值複雜度

接下來 force-merge recovery → main 不應該有功能 regression（recovery 是
strictly ahead 的）。

---

## 5. Lessons learned

1. **dump IR 對照 with/without 是 root-cause 最快路徑** — earlier
   freeze attempt 是用 hypothesis-driven，浪費一輪 build/test。直接
   dump 對照可立刻 spot 4-byte offset diff。
2. **module 沒設 datalayout 是 latent bug**：原本因為 LLJIT 在 codegen
   時自己塞 layout 進去，所以非-instcombine path 仍 work。但任何依賴
   layout 的 IR-level pass 都會踩。**未來加 pass 前先確認 module
   有 datalayout**。
3. **`switch i32 poison` 是個常見假象**：dead BB 裡的 switch 在
   instcombine 看起來像 UB，但實際上 LLVM 對 dead BB 的 poison
   propagation 是有限的（特別是 switch 通常會被 `block 不可達`
   先 simplify 掉）。debug 別第一個猜這條。
