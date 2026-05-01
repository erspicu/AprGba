# Phase 2.6：Framework 通用化 Refactor

> **狀態：✅ 完成**（R1–R5 全部實作於 Phase 2.5 期間）
>
> R1–R5 的承諾——「換 CPU 只要換 JSON」——的**真正驗證**移到 Phase 4.5
> （GB LR35902 移植）。見 `MD/design/09-gb-lr35902-validation-plan.md`。
>
> 下方文件保留為**歷史 plan doc**，供日後類似 refactor 參考結構。

---

## 為什麼插在 2.5.2 與 2.5.3 之間

Phase 2.5.2 結束後，parser/emitter 已涵蓋 ARM Data Processing 全集
（48 條 ALU + 3 條 PSR）。再往前推 2.5.3（記憶體傳輸）會新增更多
emitter 與 operand resolver — **耦合面會繼續擴大**。趁現在 footprint
還小（~14 個 emitter、4 個 resolver），把通用化 refactor 做完。

如果延後到 2.5 收尾才做，需要重寫的 emitter 數量會翻倍以上，且
容易在重寫時引入 regression。

## 目標

讓 `AprCpu.Core` 在「不更動 C# code」的前提下，可由不同 CPU spec 驅動：
- 換 `cpu.json` + `<set>.json` 即可換 CPU 架構（前提是 micro-op /
  operand resolver vocabulary 夠用）
- 架構特有的 emitter 仍可用 plug-in 方式註冊（不需修改 framework
  code，只需在 SpecCompiler 啟動時呼叫對應的 RegisterAll）

## 範圍：5 個 Refactor

| ID | 內容 | 工作量 | 順序依賴 |
|---|---|---|---|
| R1 | CpuStateLayout 改為從 `register_file` 動態建構 | ~1 天 | 第一個做 |
| R2 | 旗標 bit 位置改為從 `register_file.status[].fields` 查詢 | ~0.5 天 | 需 R1 |
| R3 | OperandResolver 改為 registry pattern（仿 emitter） | ~0.5 天 | 獨立 |
| R4 | Condition gate 改為從 `global_condition.table` 資料驅動 | ~1 天 | 需 R2 |
| R5 | ARM-specific emitter 移到獨立 class，generic 部分留在 StandardEmitters | ~0.5 天 | 最後做 |

合計約 3.5 天集中工。

## 不變的契約

每個 R 完成後：
- 所有現有測試（64 個）必須仍綠
- CLI `aprcpu --spec spec/arm7tdmi/cpu.json --output temp/arm7tdmi.ll`
  必須仍 emit 62 functions、0 diagnostics、LLVM TryVerify 通過
- 產出 `.ll` 的 IR 結構容許微差（如 GEP 索引變動），但語義相同

## R1：CpuStateLayout 資料驅動

### 現況

`CpuStateLayout.cs` 建構式只接 `LLVMContextRef`，內部硬編 39 個欄位
（R[16] + CPSR + 5 SPSR + 5 banked groups + cycle + pending）。

### 之後

```csharp
public sealed unsafe class CpuStateLayout
{
    public CpuStateLayout(LLVMContextRef ctx,
                          RegisterFile registerFile,
                          ProcessorModes? modes);

    public LLVMTypeRef StructType { get; }
    public LLVMTypeRef PointerType { get; }

    public LLVMValueRef GepGpr(LLVMBuilderRef b, LLVMValueRef state, int idx);
    public LLVMValueRef GepGprDynamic(...);
    public LLVMValueRef GepStatusRegister(LLVMBuilderRef b, LLVMValueRef state, string name);
    public LLVMValueRef GepBankedGpr(LLVMBuilderRef b, LLVMValueRef state, string mode, int idxInGroup);
    public LLVMValueRef GepCycleCounter(...);

    // For external use (e.g. flag-bit lookup helpers):
    public StatusRegister GetStatusRegisterDef(string name);
}
```

LLVM struct 順序：
1. GPR（依 spec `general_purpose.count`）
2. 每個 status register（依 spec `status[]` 順序）
3. 每個 banked GPR group（依 `processor_modes.banked_registers`）
4. 「emulator-internal」固定後綴：cycle_counter (i64)、pending_exceptions (i32)

### 影響範圍

- `CpuStateLayout.cs`：重寫
- `SpecCompiler.cs`：建構 layout 時傳入 `loaded.Cpu.RegisterFile` + `ProcessorModes`
- `Emitters.cs`、`OperandResolvers.cs`：把 `Layout.GepCpsr()` 改成
  `Layout.GepStatusRegister("CPSR")`（API 改名）
- `CpuStateLayout.CpsrBit_*` 常數：暫時保留以維持編譯，由 R2 移除

### 風險

- 動態 struct 順序若與 R2 的 bit 位置查詢不一致會錯
- 對策：建構時驗證 status register 寬度與 fields 涵蓋範圍

## R2：旗標 bit 位置從 spec 查詢

### 現況

`Emitters.cs` 用 `CpuStateLayout.CpsrBit_N`（= 31）等常數寫入 CPSR。

### 之後

新增 helper：

```csharp
public static class CpsrFlags
{
    public static int GetBitIndex(CpuStateLayout layout, string register, string flag);
    // e.g. (layout, "CPSR", "N") -> 31, lookup via RegisterFile.Status["CPSR"].Fields["N"]
}
```

`update_nz` 等 emitter 改成：
```csharp
var nBit = CpsrFlags.GetBitIndex(ctx.Layout, "CPSR", "N");
CpsrHelpers.SetCpsrBit(ctx, nBit, ...);
```

`CpuStateLayout.CpsrBit_*` 常數移除。

### 影響範圍

- `IR/Emitters.cs`：所有 `update_*` emitter 改 lookup
- `IR/OperandResolvers.cs`：`CarryReader.ReadCarryIn` 改 lookup
- 新增 `IR/CpsrFlags.cs` helper

## R3：OperandResolver Registry

### 現況

`OperandResolvers.Apply(ctx)` 內含一個 `switch (resolver.Kind)`，加 case
是 hard-coded。

### 之後

```csharp
public interface IOperandResolver
{
    string Kind { get; }
    void Resolve(EmitContext ctx, string operandName, OperandResolver resolver);
}

public sealed class OperandResolverRegistry { ... }
```

`SpecCompiler` 啟動時：
```csharp
var resolverReg = new OperandResolverRegistry();
StandardOperandResolvers.RegisterAll(resolverReg);  // pc_relative_offset, register_direct
ArmOperandResolvers.RegisterAll(resolverReg);       // immediate_rotated, shifted_register_*
```

### 影響範圍

- 新增 `IR/IOperandResolver.cs`、`IR/OperandResolverRegistry.cs`
- 重構 `IR/OperandResolvers.cs`：把 4 個現有 resolver 拆到對應 class
- `SpecCompiler.cs`：建立 registry 並傳遞給 emit context

## R4：Condition Gate 資料驅動

### 現況

`InstructionFunctionBuilder.EmitConditionGate` hardcode AL / EQ / NE，
其餘 cond 預設 false。

### 之後

新建 `IR/ConditionEvaluator.cs`，從 `InstructionSetSpec.GlobalCondition.Table`
（如 `{ "0000": "EQ", "0001": "NE", ... }`）讀出每個 cond mnemonic，再依
mnemonic 對 CPSR flag 做查詢。

```csharp
public static class ConditionEvaluator
{
    public static LLVMValueRef EmitCheck(EmitContext ctx, GlobalCondition gc);
}
```

內部對 `EQ` → `CPSR.Z == 1`、`NE` → `CPSR.Z == 0`、`CS/HS` → `CPSR.C == 1`、…
14 個對應齊全。AL = always true、NV = always false。

### 影響範圍

- 新增 `IR/ConditionEvaluator.cs`
- `InstructionFunctionBuilder.EmitConditionGate` 簡化為呼叫 evaluator

## R5：ARM-only Emitter 切離

### 現況

`StandardEmitters.RegisterAll` 註冊全部 emitter，混雜 ARM-specific 與
通用 op。

### 之後

切成：
- `StandardEmitters.RegisterAll`：通用 op（add/sub/and/or/xor/shl/lsr/asr/
  mvn/bic/rsb/load/store/branch/branch_link/if/...）
- `ArmEmitters.RegisterAll`：ARM-specific（branch_indirect with T-bit、
  restore_cpsr_from_spsr、adc/sbc/rsc/update_c_*_carry/read_psr/write_psr）

`SpecCompiler` 依 `architecture.family` 選擇額外註冊：
```csharp
StandardEmitters.RegisterAll(reg);
if (loaded.Cpu.Architecture.Family == "ARM")
    ArmEmitters.RegisterAll(reg);
```

### 影響範圍

- 把 `IR/Emitters.cs` 拆成 `Emitters/StandardEmitters.cs` + `Emitters/ArmEmitters.cs`
- `SpecCompiler.cs`：family-based 註冊

## 完成標準（已達成）

- [x] 既有測試全綠（當時 64 個 → Phase 2.5 結束時 159 個）
- [x] CLI 對 ARM7TDMI spec 仍 emit 完整指令（44 個 ARM mnemonic + ~30 個
      Thumb mnemonic）、0 diagnostics
- [x] `CpuStateLayout` 內無任何 `Cpsr` / `Spsr` 字串硬編碼
- [x] `OperandResolvers` 與 `Emitters` 各自有 ARM 特化檔（`ArmEmitters`、
      `ArmOperandResolvers`），generic 檔可由非 ARM spec 直接重用
- [x] 條件 gate 對所有 14 cond code 行為正確
- [ ] **真正用「另一顆 CPU」spec 跑通 pipeline** — 此項移到 Phase 4.5（GB
      LR35902 移植），見 `09-gb-lr35902-validation-plan.md`

## 接下來銜接

完成後回到 Phase 2.5.3（ARM 記憶體傳輸），繼續完成 ARM7TDMI spec
全集。Phase 2.6 的 refactor 不影響後續 sub-phase 的時程估計，因為
2.5.3 之後新增的 emitter / resolver 自然會放在正確的位置（generic vs
arm-specific），不需重複 refactor。
