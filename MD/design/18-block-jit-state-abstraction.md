# Block-JIT state abstraction — alloca + mem2reg

> **Status (2026-05-09 update — N1 closeout)**：設計 + 實作 + 驗證全
> 完工。三個 backend (legacy / json per-instr / json-block) 都通過
> nestest + blargg cpu_test5；GB block-JIT 跑在新框架沒回歸。perf
> measurement 在 `MD/performance/202605091559-nes-blockjit-vs-perinstr.md`。
>
> **B'.7 audit 結論（追記）**：
> 既有的 `PipelinePcConstant` / `CurrentInstructionBaseAddress` 路徑
> **不是 legacy** — 它們是 block-JIT-only 的「編譯期已知 PC」標記，
> 啟用三個跟 alloca+mem2reg **正交** 的優化：
>
> 1. **編譯期 imm 抽取** (`FetchImmediate` Lr35902:798)：從 `ctx.Instruction`
>    的 instruction word 直接 shift-extract imm8，**跳過 bus.ReadByte
>    extern call**。block-JIT 一條 ALU+imm8 指令省 1 個 indirect call
>    per instr。
> 2. **WRAM/HRAM region inline** (`EmitWriteByteWithSyncAndRamFastPath`
>    Lr35902:1141)：bus.WriteByte 在 block-JIT 模式下做 region 檢查 +
>    direct GEP store，跳過 extern call 跟 sync-flag 機制（per-instr
>    模式不需要這些）。
> 3. **Sync-exit PC pre-write** (Lr35902:1345)：block-JIT 中段 ret 前
>    把 next-PC 寫成 const store + PcWritten=1，讓 outer loop 接管。
>
> 這些都是 alloca+mem2reg **無法替代** 的 — alloca 處理 *state 存取*，
> PipelinePcConstant 處理 *IR 形狀變體（什麼時候插 const、什麼時候
> inline 區段）*。兩者並用才是完整的 block-JIT 工具箱。
>
> 因此 B'.7 結論：**保留 PipelinePcConstant 路徑作為 framework 一級
> feature**，**不刪、不 deprecate**。Future generalisation 方向（加進
> task #163）：把編譯期 imm 抽取也 port 到 Mos6502Emitters，省掉 NES
> block-JIT 每個 imm fetch 的 bus extern call — 是 perf 優化、不是 cleanup。
>
> 設計階段（保留下方歷史內容）。現有狀況：LR35902 (GB) block-JIT
> 已 ship；MOS6502 (NES) per-instr 已通過所有 ROM。試圖把 NES emitter
> 接到 block-JIT 失敗 — 暴露出 emitter-author contract 的隱性化問題。
>
> 本 doc 提出 framework-level 解法：把 "per-instr vs block-JIT" 的差
> 異從 emitter 程式碼裡移走，改由 `EmitContext` + `BlockFunctionBuilder`
> 透過 LLVM 標準 `alloca + mem2reg` pattern 自動處理。**Emitter 不再
> 需要知道自己在哪個模式**。
>
> 設計依據：
> 1. Gemini 諮詢 (2026-05-09)，紀錄在
>    [`tools/knowledgebase/message/20260509_135453.txt`](/tools/knowledgebase/message/20260509_135453.txt)
> 2. QEMU TCG 的 Globals/Temps 模型
> 3. 現有 codebase：`Lr35902Emitters` 已用 `ctx.PipelinePcConstant`
>    + `ctx.CurrentInstructionBaseAddress` 做 block-JIT-aware；
>    `Mos6502Emitters` 沒做 — 整個 framework 只有一條 CPU 走 contract，
>    第二條就被迫加 hack
>
> **目標**：framework 變成「emitter 寫對一次、per-instr / block-JIT 都
> work」的工具。新 CPU 加入時不需要為 block-JIT 重寫 emitter。

---

## 1. 問題本質

### 1.1 兩種 mode、兩套 PC handling

| Mode | PC 怎麼存 | PC 怎麼讀 | PC 怎麼寫 |
|---|---|---|---|
| **per-instr** | `state.PC` (memory) | `BuildLoad2(state + PC_off)` | `BuildStore(... state + PC_off)` |
| **block-JIT (LR35902 today)** | LLVM SSA register | `ctx.PipelinePcConstant` (compile-time const) | (lazy — only at block exit) |

LR35902 emitter 內部判 `if (ctx.PipelinePcConstant is uint pc) ... else ...`
分流。**這是 contract** — emitter 必須知道兩條路。

MOS6502 emitter 沒做這個 — 一律 `BuildLoad2(state + PC_off)`。在 per-instr
模式 work，在 block-JIT 模式：
- 整個 block 內 PC 永遠從 state 讀寫
- LLVM 看不到「block 內 PC 是 i32 跨 instr 不變的常數」
- 跨 instr CSE / DCE 失效
- block-JIT 跟 per-instr 一樣慢，甚至更慢（多了 dispatch loop overhead）

### 1.2 嘗試的繃帶為什麼錯

第一次嘗試（已 stash 在 `nes-blockjit-bandaid-stash-202605091230` branch）
加了 `BlockFunctionBuilder.PerInstructionPcPreWrite` flag — 在每個 in-block
instruction 開頭把 `state.PC = bi.Pc + 1` 寫進 state buffer。

問題：
1. **效能**：每 instr 一個 store，後面 emitter load 一次，LLVM 沒辦
   法消掉（state buffer 是 alias-uncertain memory，store 必須 honor）
2. **正確性**：read_imm8 等 emitter 內會繼續 advance PC（再 store）。
   pre-write 只在 instruction 開頭發生，但 emitter 中可能要「instr 開頭
   的 PC 值」做某些事（例如 BRK push PC+2），這時讀到的是「上條 instr
   advanced 後的 PC」。實測 blargg cpu_test5 fail。

繃帶**字面上摧毀 block-JIT 的核心優勢**（PC 在 SSA 不在 memory），
所以即使修對 ordering 也沒意義。

### 1.3 兩條真正的選項

| 方案 | 改動 | LLVM 優化潛力 |
|---|---|---|
| **A. IStateContext refactor** | emitter 全改用 abstract API；framework 提供 per-instr / block 兩種實作 | ✓ 完整 |
| **B. alloca + mem2reg**（**本 doc 選**） | emitter 一行不改；framework 在 block 模式把 state pointer 偷換成 alloca | ✓ 完整（LLVM standard pass） |

A 比較「architecturally pure」但要動每個 emitter (~30 個 6502 + ~50 個 GB)。
B 是 LLVM 慣用的 idiom — `alloca` 起來、`mem2reg` 自動提升成 SSA、做完
跟手寫 SSA 等價。

---

## 2. 設計：alloca + mem2reg

### 2.1 Mental model（QEMU TCG 對應）

| QEMU 概念 | 我們的對應 |
|---|---|
| Global (state struct field) | `state` buffer 裡的 PC / GPR / status reg |
| Temp (in-block SSA value) | `alloca`（block 內），mem2reg 後變 register |
| Sync (TCG global → memory) | block exit / side-effect 點把 alloca 寫回 state buffer |

### 2.2 IR shape — 改動前 vs 後

**現在（per-instr 模式或 6502 block-JIT 繃帶）：**
```llvm
define void @Execute_Block_Main_8000_g1(ptr %state, i32 %instr_word) {
entry:
  %pc_ptr = getelementptr i8, ptr %state, i64 32  ; PC offset
  %a_ptr  = getelementptr i8, ptr %state, i64 0   ; A offset

  ; instr 1 (LDA #$10):
  store i16 32769, ptr %pc_ptr      ; bandaid pre-write
  %pc1 = load i16, ptr %pc_ptr      ; read PC
  %imm_addr = zext i16 %pc1 to i32
  %imm = call i8 @memory_read_8(i32 %imm_addr)
  %pc1_inc = add i16 %pc1, 1
  store i16 %pc1_inc, ptr %pc_ptr   ; advance PC
  store i8 %imm, ptr %a_ptr         ; A := imm

  ; instr 2 (TAX):
  store i16 32770, ptr %pc_ptr      ; bandaid pre-write again
  %a2 = load i8, ptr %a_ptr
  store i8 %a2, ptr %x_ptr
  ret void
}
```

20 個 memory ops、LLVM 沒辦法消掉。

**改動後（alloca + mem2reg）：**

Build 時：
```llvm
define void @Execute_Block_Main_8000_g1(ptr %state, i32 %instr_word) {
entry:
  ; --- prologue: allocate locals + load initial state ---
  %pc_local = alloca i16
  %a_local  = alloca i8
  %x_local  = alloca i8
  %p_local  = alloca i8
  %sp_local = alloca i8
  %pc_init = load i16, ptr (state + PC_off)
  store i16 %pc_init, ptr %pc_local
  %a_init  = load i8,  ptr (state + A_off)
  store i8 %a_init, ptr %a_local
  ; ... etc

  ; --- body: emitters use locals (via EmitContext redirect) ---
  ; instr 1 (LDA #$10):
  %pc1 = load i16, ptr %pc_local
  %imm_addr = zext i16 %pc1 to i32
  %imm = call i8 @memory_read_8(i32 %imm_addr)
  %pc1_inc = add i16 %pc1, 1
  store i16 %pc1_inc, ptr %pc_local
  store i8 %imm, ptr %a_local

  ; instr 2 (TAX):
  %a2 = load i8, ptr %a_local
  store i8 %a2, ptr %x_local

  ; --- epilogue: sync locals back to state ---
  %pc_final = load i16, ptr %pc_local
  store i16 %pc_final, ptr (state + PC_off)
  %a_final  = load i8, ptr %a_local
  store i8 %a_final, ptr (state + A_off)
  ; ... etc
  ret void
}
```

`mem2reg` pass 後（自動）：
```llvm
define void @Execute_Block_Main_8000_g1(ptr %state, i32 %instr_word) {
entry:
  ; --- prologue (single load per slot) ---
  %pc_init = load i16, ptr (state + PC_off)
  ; A/X 等不需要 — body 沒讀，只寫，讀的時機後面 epilogue 才需要

  ; --- body (pure SSA, no memory) ---
  %imm_addr = zext i16 %pc_init to i32        ; PC 直接用 init 值
  %imm = call i8 @memory_read_8(i32 %imm_addr)
  %pc1_inc = add i16 %pc_init, 1              ; PC arithmetic in SSA
  ; instr 2 TAX: %imm 直接 forward 到 X 寫入
  ; 結尾 PC 是 %pc1_inc (從 LDA 的 read_imm8 advance 後)

  ; --- epilogue (single store per dirty slot) ---
  store i16 %pc1_inc, ptr (state + PC_off)
  store i8 %imm, ptr (state + A_off)         ; A := %imm (TAX 的 src 也是這個)
  store i8 %imm, ptr (state + X_off)         ; X := %imm
  ret void
}
```

跨 instr SSA 串通；2 個 instr 只 1 個 load (PC)、3 個 store。**這就是
block-JIT 該有的樣子**，emitter 一行沒改。

### 2.3 EmitContext API

新增 `EmitContext.GepGpr(int)`/`GepStatusRegister(string)` 在 block 模式
下回傳 alloca pointer。Per-instr 模式回傳 state buffer GEP（現有行為）。

實作上加一個 `IStateSlotProvider` 給 EmitContext：

```csharp
public interface IStateSlotProvider {
    LLVMValueRef GprPtr(int index);
    LLVMValueRef StatusRegPtr(string name);
    LLVMValueRef BankedGprPtr(string mode, int index);
    void SyncToState();   // emitter 在 host extern call 前呼叫
}

public sealed class StateBufferProvider : IStateSlotProvider {
    // 現在的 GEP-into-state-pointer 行為
}

public sealed class AllocaSlotProvider : IStateSlotProvider {
    // 維護 slot → alloca map；SyncToState 把 dirty alloca 寫回 state
    // BlockFunctionBuilder 在 prologue 建立並注入
}
```

EmitContext 持有 `IStateSlotProvider _slots`：
```csharp
public LLVMValueRef GepGpr(int idx) => _slots.GprPtr(idx);
```

### 2.4 Side-effect sync points

當 block 內 IR 要呼 host extern (memory_read_8, memory_write_8, 其他
host helper) 而那些 extern 可能：
- 觸發 IRQ/NMI（需要正確的 state.PC 給 host 的 NMI handler）
- 讀 state（沒有 — externs 都是純 in/out）
- 寫 state（沒有 — externs 透過 host C# code 改 state？實際上不會，
  state 從外面 explicitly 改，例如 BoundCpu.NmiInterrupt 直接 push state；
  block 內呼 extern 的 host 端 trampolines 不寫 state 欄位）

實際分析：
- `memory_read_8` / `memory_write_8` 走 NesMemoryBus，可能 trigger PPU NMI；
  但 NMI 的 polling 在 block exit 後的 dispatch loop 才發生（per-instr 也
  是這樣）。所以 in-block 的 extern call **不需要** sync state。
- 例外：bus.WriteByte 可能寫 $4014（OAM DMA）導致 +513 stall — 這個
  cycle accounting 在 block exit 後 host 端用 `bus.ConsumeStallCycles()`
  收掉，不影響 block 內 IR
- LR35902 的 EI/DI 走 defer 機制 + sync extern — 這個保留現有設計

**結論**：side-effect sync 在 NES 上**只發生在 block exit**。GB 的
defer/sync 機制獨立於本 refactor，沒影響。簡化版本可以先不做 mid-block
sync，只 prologue + epilogue。

### 2.5 哪些 slot 要 alloca

**全部 hot 的 state field**：
- 所有 GPR (`registerFile.GeneralPurpose.Names` 對應的 N 個)
- 所有 status register (`P`, `SP`, `PC`, GB 的 `F`)
- Cycle counter / PcWritten flag — 這些本來就常駐 register

如果 alloca 太多 LLVM 會處理不好？— mem2reg 對任意 alloca 數量都好。
GB 的 8 GPR + 3 status 已經比 6502 的 3 GPR + 3 status 多。

---

## 3. 實作計畫（B'.1 - B'.6）

### B'.1 — `IStateSlotProvider` + `EmitContext` 切換

新檔 `src/AprCpu.Core/IR/IStateSlotProvider.cs`。
重構 `EmitContext.GepGpr` / `GepStatusRegister` / `GepBankedGpr` 走
provider。Per-instr mode 注入 `StateBufferProvider`（保留現有行為）。

驗證：T1 不回歸（per-instr 路徑全等價）。

### B'.2 — `BlockFunctionBuilder` 注入 alloca provider

Block IR 的 entry block 開頭：
1. 為每個 spec-declared state field 配 alloca
2. Load 對應 state-buffer 值灌進 alloca
3. 建立 `AllocaSlotProvider` 把 slot 映射表記下來
4. EmitContext 在 emit 期間用這個 provider
5. Block exit 之前：把所有 alloca 值 store 回 state buffer
6. 移除繃帶 `PerInstructionPcPreWrite` flag（不需要了）

### B'.3 — mem2reg pass

Build 完每個 block function 跑 LLVM 標準 `mem2reg`/`sroa`/`instcombine`
passes。LLVMSharp 走 `LLVMRunFunctionPassManager`。

驗證：dump IR 確認 block body 沒有 alloca 殘留 + 沒有跨 instruction
冗餘 state load/store。

### B'.4 — GB regression

跑 GB JsonCpu block-JIT existing test (blargg cpu_instrs)。Pass 條件：
- 結果一致（all 11 PASS）
- Perf 不大幅退步（baseline 21 MIPS → 容忍 ±10%）

GB 既有的 `PipelinePcConstant` / `CurrentInstructionBaseAddress` 路徑
**先保留**，alloca path 是 additive。如果 GB block-JIT 跑得比之前快
（mem2reg + 跨 instr CSE > 既有 baked-const 路徑），那是 bonus；如果
持平就是預期。

### B'.5 — NES block-JIT enable

NesJsonCpu 加 `--backend=json-block` 路徑（mirror GB）。Mos6502Emitters
**完全不動**。

驗證：
- nestest --backend=json-block：PC→\$C66E，\$02=\$00 \$03=\$00
- blargg cpu_test5 --backend=json-block："All tests complete"
- 兩者跟 legacy / json per-instr 結果一致

### B'.6 — Perf 量

3-run blargg cpu_test5：
- legacy（baseline 1.69 MIPS）
- json per-instr（baseline 0.83 MIPS）
- **json-block （新）**

預期 NES json-block 至少達到 legacy 等級（1.5-2 MIPS），有可能超越
（如果 mem2reg 對 6502 的 NVZC flag chain 折得乾淨）。

寫進 `MD/performance/<ts>-nes-blockjit-vs-perinstr.md`。

### B'.7 — (deferred) deprecate 舊路徑

B'.5 + B'.6 全綠後，audit `Lr35902Emitters` / `ArmEmitters` 用 PipelinePcConstant
/ CurrentInstructionBaseAddress 的地方。如果 alloca path IR 等價，把
舊路徑刪掉 — framework 一條 mechanism 跨所有 CPU。重跑 GB block-JIT
+ ARM 既有 test 確認沒回歸。

如果 deprecation 有風險（例如 GB 某個 emitter 對 baked-const 有特殊
依賴），就用文件 mark deprecated 但保留 — 至少新 CPU 的 emitter 不
會被誘導走舊路徑。

---

## 4. 風險與 mitigation

### 4.1 mem2reg promote 失敗

mem2reg 要求 alloca 在 entry block 開頭、用法是純 load/store
（沒有 take address）。我們的 alloca 都符合 — emitter 透過
`ctx.GepGpr` 拿 pointer 做 load/store，沒做 ptrtoint / 沒傳給 host
extern。

**Mitigation**：寫一個單元測試 dump block IR 確認 mem2reg 之後 alloca 數 = 0
（或 ≤ 預期殘留數）。

### 4.2 alloca pointer 被 escape 到 host

如果某個 emitter 把 alloca pointer 傳給 host extern（例如假想的
`host_dump_state(ptr state)`），mem2reg 會 fail。

**Mitigation**：grep emitter source 確認沒有 escape；framework 在
`AllocaSlotProvider.GprPtr` 加 debug-only assertion「呼叫者不應 store
這個 pointer 到 escapable location」。

### 4.3 LR35902 既有 PipelinePcConstant 跟新 alloca 衝突

LR35902 的 read_imm8 emitter 會 check `if (ctx.PipelinePcConstant is uint pc)
{ /* bake const, advance C# tracker */ } else { /* per-instr 路徑：
load PC、advance、store */ }`。

新 alloca path 的 in-block PC 是 alloca slot — 從 emitter 看就是「per-instr
路徑」（load/store 都在），但底下是 alloca 不是 state buffer。

兩條路同時存在會：
- 走 PipelinePcConstant baked-const → 不碰 alloca → block exit 時 alloca
  仍是 init 值 → epilogue 寫回 state，**蓋掉真正的 PC**

**Mitigation**：B'.2 實作時把 GB block-JIT 的 PipelinePcConstant 也接到
alloca — block prologue 設定 PipelinePcConstant 指向 alloca 的當前值（透過
mem-read semantics 等價於現有 const），讓兩者路徑收斂。或者更簡單：B'.4
測試 GB block-JIT 是否 still pass，若 fail 就在 B'.4 修。

### 4.4 SMC / bank switching

不影響本 refactor — invalidation 是 BlockCache 層級的事；alloca 只是
單一 block function 內部 IR shape 的變動。

### 4.5 Perf 退步

如果 mem2reg 出於某種原因沒做完（例如 alloca 在非 entry block —
要小心 BlockFunctionBuilder 的 prologue 位置），block-JIT 變成跟
per-instr 一樣慢。

**Mitigation**：B'.3 的 IR-dump test。如果 mem2reg 做完還是慢，問題
就不在 state access — 可能在 dispatch loop 或 cycle accounting。

---

## 5. 跨 doc 對照

- 補強 [12-gb-block-jit-roadmap.md](12-gb-block-jit-roadmap.md) — GB 路
  徑 deprecation 在 B'.7
- 跟 [11-emitter-library-refactor.md](11-emitter-library-refactor.md)
  方向一致（lift 到共用層）
- [16-emulator-completeness.md](16-emulator-completeness.md) +
  [17-aprcpu-vs-emulator-timing-boundary.md](17-aprcpu-vs-emulator-timing-boundary.md)
  劃定的 framework / emulator 邊界不變

---

## 6. 收尾

完成這次 refactor 後，**emitter contract 變成**：

> Emitter 不需要知道自己是 per-instr 還是 block-JIT 模式。所有 state
> 存取走 `ctx.GepGpr` / `ctx.GepStatusRegister` / `ctx.ReadStatusFlag`
> 等 framework helper。Pure ALU / arithmetic / 控制流 ops 用 generic
> `Binary` / `BranchCc` / 等 standard emitter 直接組合。
>
> 唯一例外：emitter 呼叫 host extern 時，如果該 extern 的副作用會 read
> state（例如 NMI handler），呼叫前要 `ctx.SyncStateToBuffer()`（B'.2
> 提供）。實務上 NES + GB 的 bus extern 都不 read state，所以這個
> 規則目前等於「不需要呼叫」。
>
> 加新 CPU 流程：寫 spec.json + 必要時加 arch-specific micro-op 到
> `<Arch>Emitters.cs`（addressing modes / flag combos / 等），完。
> Per-instr / block-JIT framework 自動兼用。

加新 CPU 的「框架成本」從現在的「audit 30+ emitter 是否符合 implicit
contract」降到「寫 emitter，跑測」。
