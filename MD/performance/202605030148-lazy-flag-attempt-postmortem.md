# Phase 7 C.b 嘗試 post-mortem — full lazy flag 設計衝突 SSA dominance

> **狀態**：**未 commit，working tree 改動已 revert**。
>
> 嘗試在 EmitContext 加 `PendingFlagWrites` deferred-batch 機制，把
> 每個 status register 連續多次 SetStatusFlag 合併成單一 read-modify-store。
> 概念正確、code 寫完了 + 編譯過了，但跑 unit test 5/345 失敗，根本
> 原因是 LLVM IR SSA dominance 規則衝突。
>
> **教訓**：lazy flag 不是「加個 batch dictionary」這種純局部改動就能做。
> 跨 basic block 的 SSA value 需要 alloca-based shadow + LLVM mem2reg
> 才能正確處理控制流。
>
> 本檔留下設計記錄 + 失敗診斷，供未來重做參考。

---

## 1. 嘗試的 design

```csharp
// EmitContext 加：
public Dictionary<string, FlagWriteBatch> PendingFlagWrites { get; }
    = new(StringComparer.Ordinal);

public sealed class FlagWriteBatch {
    public Dictionary<int, LLVMValueRef> NewBits { get; } = new();
    public void SetBit(int bitIndex, LLVMValueRef i1Value) { ... }
}

// CpsrHelpers 改：
public static void SetStatusFlag(...)
    => DeferFlagWrite(...);  // 不直接 emit IR，存進 dict

public static LLVMValueRef ReadStatusFlag(...) {
    FlushFlagWritesForReg(ctx, register);  // 讀前先 drain
    // 原本的 read 邏輯
}

public static void FlushFlagWritesForReg(EmitContext ctx, string register) {
    if (no pending) return;
    var prev = load status_reg
    var combined = prev
    foreach (bitIndex, i1Value in batch.NewBits) {
        combined = (combined & ~mask) | (zext(i1Value) << bitIndex)
    }
    store combined to status_reg
}

// InstructionFunctionBuilder 加：
// before BuildRetVoid:
CpsrHelpers.FlushAllFlagWrites(ctx);
```

設計 looks clean。原本期待：
- 連續 4 次 update_nz / update_c_add / update_v_add → 4 次 SetStatusFlag
  → 全部寫進 batch dict → end-of-fn 一次 read-modify-store
- LLVM 看到單一 store，不需要做跨 emitter 的 fusion

---

## 2. 為什麼掛了：SSA dominance 衝突

跑 `dotnet test`：5/345 失敗，包含 `MOV_LSL_Immediate_NoFlagUpdate`、
`JsmolkaArmGba_AllTestsPass_R12IsZero`、SpecCompiler 兩個編譯測試。

LLVM Verify 抱怨：

```
Instruction does not dominate all uses!
```

**根本原因**：

ARM7TDMI spec 用 conditional execution，每條指令的 wrapper 是：

```llvm
entry:
  ; cond gate 評估 CPSR
  br_cond %should_execute, body, ret_skip
body:
  ; instruction steps
  ; ... step1 ...
  ; ... step2: SetStatusFlag → batch.SetBit(bit_idx, %i1_v_step2)
  br ret_skip   ; <-- 隱含 fall-through to ret
ret_skip:
  ret void
```

C.b 在 `BuildRetVoid` 之前 emit `FlushAllFlagWrites`，但「之前」是指
position 在 ret_skip block。flush 內 emit 的 store 會用 `%i1_v_step2`
這個來自 body block 的 SSA value。

**ret_skip 不在 body 的 dominance frontier 內**（cond gate 可能 skip
body 直接到 ret_skip）— 所以 `%i1_v_step2` 不 dominate flush 的 store
位置。LLVM Verify 拒絕。

實際上 `if`/`branch_cc` 等內部 emitter 也會建 sub-blocks，使得 deferred
SSA values 跟 flush location 跨 block，問題類似。

---

## 3. 正確的解法（沒在這次做）：alloca-based shadow

LLVM idiom 解決跨 BB SSA：用 alloca 當 mutable scalar storage，最後跑
mem2reg pass。

```llvm
entry:
  %cpsr_shadow = alloca i32
  %cpsr_init = load i32, %cpsr_real_ptr
  store i32 %cpsr_init, %cpsr_shadow
  br_cond ..., body, exit

body:
  ; SetStatusFlag(N, %v):
  %prev = load i32, %cpsr_shadow
  %cleared = and %prev, mask_~N
  %shifted = shl (zext %v to i32), n_bit
  %new = or %cleared, %shifted
  store i32 %new, %cpsr_shadow
  ; ... 同樣的 pattern for Z, C, V ...
  br exit

exit:
  ; FlushAll:
  %final = load i32, %cpsr_shadow
  store i32 %final, %cpsr_real_ptr
  ret void
```

跑 `mem2reg` pass：
- alloca 消失，shadow 變 SSA
- store-load 序列折疊成 SSA value chain
- GVN/DSE 把 mask-or chain 合併成單個 final value
- 最後變成 0-1 個 store of CPSR (DSE 若 final 跟 init 相同甚至省掉 store)

**好處**：mem2reg 處理 PHI nodes for cond branches 自動（這是 mem2reg
存在的目的）。**SSA dominance 由 alloca 機制保證 — 任何 block 都能
load/store**。

---

## 4. 為什麼這次沒做 alloca 方案

時間 / 風險評估：

1. **Alloca approach 工作量** ≈ 200-300 行 + 需要 audit 每個直接 read
   CPSR/F/SPSR 的地方都改用 shadow（如 raise_exception / write_psr /
   restore_cpsr_from_spsr / ReadCarryIn / DAA / ConditionEvaluator）
2. **驗證成本**：345 unit + 9 Blargg + jsmolka arm/thumb/bios 3 ROM 都要
   重跑驗證；每個 false negative 都得 debug LLVM IR
3. **預期收益**：基於 C.a 的 +2.7% 推估，full lazy flag 約 +5-15%。
   對 GBA arm 8.26 MIPS 而言收益約 0.4-1.2 MIPS — 還是 1.9-2.0× real-time
4. **比較其他 step**：A (block-JIT) 預期 +500-1000% 一次到位

**Cost-benefit 不對稱**：alloca-based lazy flag 高風險中收益。block-JIT
高複雜度高收益。比起來 block-JIT 更值得。

---

## 5. 對未來工作的指引

如果未來真的要做 full lazy flag，按以下步驟：

### 5.1 設計 alloca shadow

- EmitContext 加 `Dictionary<string, LLVMValueRef> StatusShadowAllocas`
- Helper `GetOrCreateShadow(ctx, "CPSR") → alloca ptr`：
  - First call：在 entry block insert alloca + load CPSR + store shadow，
    cache 進 dict
  - Subsequent calls：return cached
- `SetStatusFlag` → load shadow, mask-or, store shadow
- `ReadStatusFlag` → load shadow

### 5.2 處理 raw CPSR access

需要 audit 所有 `GepStatusRegister(...)` callers 中 read/write CPSR raw 的：
- `read_psr`, `write_psr`: drain shadow before reading raw
- `raise_exception`, `restore_cpsr_from_spsr`: drain shadow + invalidate
- `ReadCarryIn` (LR35902 ADC carry): drain F shadow first
- `Lr35902DaaEmitter`: drain F shadow before reading

每一處要加 `DrainAndInvalidateShadow(ctx, "CPSR" or "F")`。

### 5.3 End-of-fn drain

InstructionFunctionBuilder.Build 在 BuildRetVoid 前加 `DrainAllShadows(ctx)` —
load shadow, store to real status reg。

### 5.4 確認 mem2reg 跑

可能需要在 LLVM pass manager 加 `PassManager.AddPromoteMemoryToRegisterPass()`
顯式跑 mem2reg。OptLevel=3 (Phase 7 B.a) 應該已包括，但要驗證 IR dump。

### 5.5 驗證

每改一個 emitter / spec 跑 345 unit + 9 Blargg + jsmolka arm.gba +
jsmolka thumb.gba + jsmolka bios.gba。任一掉測 stop-the-line。

---

## 6. 結論

C.a (width-correct flag access) 是 lazy flag 的 baby step，已 commit
(`fd8c15f`) 帶來 GB JIT +2.7%。

C.b (full lazy flag via deferred batch) **嘗試失敗**，revert。檔案 working
tree 已恢復 C.a 狀態。

**建議下一步**：
- 直接做 A (block-JIT)，收益遠大於 C lazy flag 系列
- 或先把 Phase 7 declared "saturated"，去做 5.9 第三 CPU port
- alloca-based 真 lazy flag 留作日後（如果 commercial ROM 流暢度 真的成
  瓶頸再做）

---

## 7. 改動明細（已 revert）

```
src/AprCpu.Core/IR/EmitContext.cs   — 加 PendingFlagWrites + FlagWriteBatch
src/AprCpu.Core/IR/Emitters.cs      — CpsrHelpers 改 deferred (DeferFlagWrite/FlushFlagWritesForReg/FlushAllFlagWrites)
src/AprCpu.Core/IR/InstructionFunctionBuilder.cs  — BuildRetVoid 前 FlushAll

git checkout -- 恢復 working tree。 345/345 unit tests 重綠。
```

---

## 8. 相關文件

- `MD/performance/202605030135-width-correct-flag-access.md` — C.a (有 commit)
- 其他 Phase 7 perf notes
- `MD/design/03-roadmap.md` Phase 7 — C 區段保留為「未完成 — 真 lazy flag」
