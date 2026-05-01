# 實作路線圖

採 **Plan B：階段性可交付**。每階段獨立可驗收，避免一次大爆炸。

業餘投入估計：每週 8–15 小時。

> **狀態快照**（2026-05）：Phase 0/1/2/2.5/2.6 ✅ 完成；Phase 4 與 Phase 6
> 的「JSON 涵蓋」工作已被 Phase 2.5 吸收；下一個進行中：**Phase 3 — Host
> runtime + 直譯器驗證**。

---

## Phase 0：環境與 Spike（1–2 週） ✅ 完成

**目標**：確認 LLVMSharp 在 Windows 11 + .NET 10 可用

完成項目：
- [x] `dotnet new sln`，建立架構文件中描述的專案骨架
- [x] 安裝 `LLVMSharp.Interop` 20.x + `libLLVM.runtime.win-x64`
- [x] Hello-world：用 LLVMSharp emit `int add(int,int)`，JIT 執行回傳 7
- [x] 驗證能輸出 `.ll` 檔案

**結果**：`dotnet run --project src/AprCpu.Compiler -- --jit-only`
端到端跑通，LLVM 20 + .NET 10 + LLVMSharp 20.x 組合穩定。

---

## Phase 1：JSON Schema 設計（2–3 週） ✅ 完成

**目標**：定義出可表達 ARM7TDMI 的 JSON 格式

完成項目見 `MD/design/04-json-schema-spec.md`、
`MD/design/05-microops-vocabulary.md`、
`spec/schema/cpu-spec.schema.json`。

---

## Phase 2：JSON Parser + LLVM IR Emitter CLI（4–6 週） ✅ 完成

**目標**：對應 Gemini 對談中的 `aprcpu.exe --input sample.json --output out.ll`

完成項目：
- [x] JSON loader（System.Text.Json，`AprCpu.Core.JsonSpec.SpecLoader`）
- [x] `$include` 機制，spec 可分檔組織
- [x] Bit pattern 解析器：`AprCpu.Core.Decoder.BitPatternCompiler`
- [x] Field extractor：`EmitContext.ExtractField`
- [x] Micro-op handler dictionary：`EmitterRegistry` + `IMicroOpEmitter`
- [x] Decoder dispatch table：`AprCpu.Core.Decoder.DecoderTable`
- [x] CpuState LLVM struct layout：`AprCpu.Core.IR.CpuStateLayout`
- [x] 主編譯器：`AprCpu.Core.Compilation.SpecCompiler`
- [x] CLI：`aprcpu --spec <cpu.json> --output <out.ll>`
- [x] LLVM `TryVerify` 通過

**Phase 2 故意未做（已於 Phase 2.5 補完）**：見下方 Phase 2.5。

---

## Phase 2.5：ARM7TDMI 規格 + Parser 完整化 ✅ 完成

**目標**：把 spec 補成 ARM7TDMI **完整 ISA**，parser/emitter 同步補齊。

詳細子階段見 `MD/design/06-arm7tdmi-completion-plan.md`。

子階段：
- [x] 2.5.1 Spec authoring 規範 + lint 強化
- [x] 2.5.2 ARM Data Processing 全集（×3 編碼）+ PSR Transfer
- [x] 2.5.3 ARM Memory Transfers（SDT、Halfword/Signed、SWP）
- [x] 2.5.4 ARM Multiply（含 Multiply Long）
- [x] 2.5.5 ARM Block Transfer + SWI + Coprocessor stub + Undefined
- [x] 2.5.6 Thumb 剩餘 16 種格式（F2、F4–F17、F19）
- [x] 2.5.7 Banked register swap + 真正的 raise_exception / restore_cpsr_from_spsr
- [x] 2.5.8 Coverage 驗證 + closeout

**驗收結果**：
- arm.json + thumb.json 涵蓋 ARMv4T 全部標準指令
- LLVM module 通過 Verify，0 diagnostics
- **159 個 xUnit 測試全綠**
- **44 個 ARM mnemonic + ~30 個 Thumb mnemonic** 全部能編出來
- 所有 micro-op 都有對應 emitter（CoverageTests 強制 enforce）
- 沒有 dead emitter（CoverageTests 強制 enforce）

---

## Phase 2.6：Framework 通用化 Refactor ✅ 完成

**插入時機**：Phase 2.5.2 結束、進 2.5.3 之前。**狀態：R1–R5 全部完成於
Phase 2.5 期間。**

詳見 `MD/design/08-portability-refactor.md`。

5 個 refactor：
- [x] R1：CpuStateLayout 從 `register_file` 動態建構
- [x] R2：旗標 bit 位置從 `register_file.status[].fields` 查詢
- [x] R3：OperandResolver 改 registry pattern
- [x] R4：Cond gate 從 `global_condition.table` 資料驅動
- [x] R5：ARM-only emitter 切到獨立 class

**真正的「換 CPU」驗證**移到 Phase 4.5（GB LR35902 移植）— 見下文。

---

## Phase 3：Host Runtime + 直譯器驗證（3–4 週） ⏳ 下一階段

**目標**：把編出來的 LLVM IR 真的執行起來，確認指令語義正確。

任務：
- [ ] **3.1 Host runtime skeleton**
  - C# 端 `CpuState` struct（與 `CpuStateLayout` 動態 layout 對齊）
  - Memory bus extern 實作（`host_mem_read8/16/32`、`host_mem_write8/16/32`）
  - `host_swap_register_bank` 實作（依 mode 把 banked R8–R14 swap 進可見槽位）
  - LLVM ORC JIT 綁定 externs
- [ ] **3.2 Fetch-decode-execute loop**
  - 走 PC、`DecoderTable` 查身分、dispatch 到 JIT 過的函式、推進 PC
  - 條件執行已在 cond gate 內，主迴圈不再特別處理
  - R15 +8/+4 偏移已在 emitter 內，主迴圈不再特別處理
  - 無 code cache，每條指令一次 dispatch（先求對，不求快）
- [ ] **3.3 Golden tests vs ARM 手冊**
  - 5–10 條手挑指令（ADD with flags、LDR with writeback、B with cond、
    MOV with shifter carry、MSR/MRS、SWI 驗證 banked swap）
  - 每個 test：建立 CpuState、跑、assert 後狀態符合 ARM ARM 預期值

**驗收**：選定指令集，所有 case 通過。`host_swap_register_bank` 經 SWI
測試走通。

**最大未知數**：LLVMSharp 20.x 的 ORC JIT API surface — 從這裡開始
de-risk。

---

## Phase 4：armwrestler ARM 模式驗證（2–3 週）

> **註**：原 Phase 4 的「JSON 涵蓋 ARM 模式所有主要指令」已被 Phase 2.5
> 吸收（Data Processing / Multiply / SDT / Halfword / Block Transfer /
> Branch / PSR / SWI / Coprocessor 全已 emit）。剩餘工作只剩「跑真的
> ROM 驗證」。

任務：
- [ ] armwrestler `arm.gba` ROM loader（極簡 GBA memory map：BIOS/IWRAM/
  EWRAM/GamePak ROM）
- [ ] 跑 ROM、attach scoreboard 讀取（通常在某個記憶體位置）
- [ ] 失敗的指令逐條 debug：對照 mGBA / NanoBoyAdvance 行為比對

**驗收**：armwrestler ARM 模式測試 100% 通過。

---

## Phase 4.5：GB LR35902 移植驗證（~2 週） 🆕

**目的**：用 `erspicu/AprGBemu`（GB 模擬器，~94KB CPU.cs）當對照組，
把 GB CPU 寫成 JSON spec，**驗證 framework 真的可換 CPU**。

詳見 `MD/design/09-gb-lr35902-validation-plan.md`。

**為什麼放這裡**：
- Phase 3 完成 → host runtime 已能跑 IR
- Phase 4 完成 → 已驗證 framework 對 ARM 跑得正確
- 此時是驗證通用性的最佳時機；若要等到 Phase 7（LLVM Block JIT）才發現
  GB 跑不通，重做成本太高
- AprGBemu 是現成 reference implementation，可逐指令對拍 fetch-decode-execute
  狀態

**為什麼用 GB 不用 6502**：
- GB CPU（Sharp LR35902，類 Z80）會逼出 framework 目前未驗證的多個區：
  - 真正的 variable-width 解碼（1/2/3 bytes）
  - Multi-instruction-set 切換（CB-prefix opcode 進入另一空間）
  - Aliased / paired registers（A/F、B/C、D/E、H/L = 8-bit 也是 16-bit pair）
  - 8-bit GPR width（目前實際只跑過 32-bit）
  - 不同 status flag layout（Z/N/H/C 在 high nibble）
- 6502 變動寬度與 paired register 都比 GB 簡單，覆蓋不到上述驗證點

**驗收**：見 `09-gb-lr35902-validation-plan.md`。範圍（全 ISA vs Blargg 子集）
真的跑到那個階段再決定。

---

## Phase 5：Memory Bus + ROM Loader + 跑 BIOS（3–4 週）

任務：
- [ ] `IMemoryBus` 完整 GBA memory map（BIOS / EWRAM / IWRAM / IO / PRAM /
  VRAM / OAM / GamePak ROM / SRAM）
- [ ] 載入 `.gba` ROM（headers、entry point）
- [ ] BIOS 載入策略（自備 BIOS 或 HLE 重寫關鍵 SWI handler）
- [ ] 從 `0x08000000` 開始跑 ROM entry point
- [ ] 處理基本 reset / SWI 中斷向量

**驗收**：能跑通 GBA BIOS 開機畫面或 ROM entry point 不 crash。

---

## Phase 6：Thumb 模式驗證（1–2 週）

> **註**：原 Phase 6 的 `thumb.json` 19 種格式、BX 切換、PC +4 偏移、
> Low/High register 限制 全已在 Phase 2.5.6 spec 端做完。剩餘工作只剩
> host 端 T-bit 狀態追蹤與跑 thumb.gba。

任務：
- [ ] Host 主迴圈追蹤 `CPSR.T` 狀態
- [ ] BX 觸發指令集切換（IR 已寫入 PC.bit0；host 依 bit0 切換 fetch 寬度）
- [ ] thumb.gba 測試 ROM 跑通

**驗收**：thumb.gba 測試 100% 通過 + ARM 與 Thumb 互相 BX 切換正確。

---

## Phase 7：LLVM Block JIT + Code Cache（6–10 週）⚠️ 最硬

**整個專案最具挑戰性的階段**。

任務：
- [ ] Block detector：從 PC 掃到 branch / return / IO 寫入為止
- [ ] Block-level IR generation（多條指令串接成一個 Function）
- [ ] LLVM JIT execution engine 整合（LLJIT 或 MCJIT）
- [ ] Code cache 資料結構（hashmap + LRU）
- [ ] **SMC 偵測**：寫入「已編譯區域」時 invalidate
- [ ] **Indirect branch dispatch**：透過 host callback 或 inline lookup
- [ ] **Block linking**（優化）：直接 patch native call，不退出 JIT
- [ ] Cycle counter 內嵌
- [ ] Performance profiling：測 LLVM 編譯耗時是否造成 stutter
- [ ] OptLevel 調整（建議 O0/O1，避免 O3）

**驗收**：上 JIT 後跑 armwrestler / thumb.gba 全綠 + 至少不慢於 Phase 5
直譯器。

**風險決策點**：若 LLVM 編譯太慢造成不可接受 stutter
- 後備 1：降 OptLevel 至 O0
- 後備 2：tiered compilation（冷 block 用 O0，熱 block 升 O2）
- 後備 3：改用 .NET `DynamicMethod` / `System.Reflection.Emit` 自寫輕量 IL JIT

---

## Phase 8：Framebuffer PPU 驗證（3–4 週）

任務：
- [ ] LCD 暫存器：DISPCNT、DISPSTAT、VCOUNT
- [ ] VBlank / HBlank 中斷觸發
- [ ] Mode 3：240×160 RGB555 framebuffer
- [ ] Mode 4：256-color palette + framebuffer
- [ ] Avalonia 視窗 + `WriteableBitmap`
- [ ] 60fps 主迴圈
- [ ] VRAM 寫入時觸發畫面更新
- [ ] 嘗試跑 tonc 或其他 GBA homebrew

**驗收**：螢幕看到 GBA homebrew 的 Mode 3/4 畫面。

**🎯 重大里程碑**：第一次「看到畫面」。

---

## Phase 9 及之後（不在 MVP 範圍）

未來可選方向：
- 完整 PPU（Tile Mode 0/1/2、Sprite、Window、Mosaic、Affine）
- DMA 通道 0–3
- Timer 0–3
- 中斷控制器
- APU（音訊）
- 商業遊戲相容性測試與 bug fix
- 開源、文件、社群
- AOT 預編譯 `.bc` 快取
- 第三顆 CPU 驗證（Phase 4.5 GB 之後若想再驗證一次，可考慮 MIPS R3000、
  RISC-V RV32I 之類；6502 因為太簡單，覆蓋不到 framework 已驗證的點，不
  特別優先）

---

## 風險與後備計畫

| 階段 | 風險 | 後備方案 |
|---|---|---|
| Phase 0 | LLVMSharp 不可用 | ✅ 已驗證，LLVMSharp 20.x + libLLVM 20 + .NET 10 OK |
| Phase 3 | LLVMSharp ORC JIT API 不熟 | 從 Phase 0 spike 的 add(3,4) 樣板擴出去；最差走 MCJIT |
| Phase 4.5 | GB CPU spec 寫法卡到 paired register | 擴 schema 加 `register_aliases`；或在 spec 用兩個 8-bit + emitter helper |
| Phase 5 | BIOS 取得問題 | HLE BIOS（自重寫關鍵 SWI handler） |
| Phase 7 | LLVM 編譯太慢 | 改 .NET DynamicMethod 自寫輕量 JIT |
| Phase 8 | Avalonia 整合難 | 改 WinForms 純 Windows 版 |

---

## 里程碑時程（業餘 8–15h/週）

| 里程碑 | 內容 | 累計時間（原估） | 實際 |
|---|---|---|---|
| **M1** | Phase 0–1 完成 | ~1 月 | ✅ |
| **M2** | Phase 2 完成 | ~3 月 | ✅ |
| **M2.5** | Phase 2.5 完成（ARM7TDMI 完整 spec + parser） | ~4 月 | ✅ 比預期快 |
| **M3** | Phase 3–5 完成（直譯器 + armwrestler + 跑 BIOS） | ~7 月 | 進行中 |
| **M3.5** | Phase 4.5 完成（GB 驗證 framework 通用性） | — | 新增 |
| **M4** | Phase 6 完成（Thumb 跑得動） | ~9 月 | — |
| **M5** | Phase 7–8 完成（**畫面出來**） | ~12–15 月 | — |

---

## 下一步：Phase 3 — Host Runtime + 直譯器驗證

Phase 0/1/2/2.5/2.6 已完成。CLI 已能從 JSON 產出 ARM7TDMI 完整 ISA 的
LLVM IR；159 測試全綠。

接下來進 Phase 3：
1. **3.1**：Host runtime skeleton（C# CpuState、memory bus、swap_register_bank、
   LLVM ORC JIT 綁定）
2. **3.2**：fetch-decode-execute 主迴圈
3. **3.3**：Golden tests 對照 ARM 手冊

之後 Phase 4 跑 armwrestler，Phase 4.5 用 GB 驗證 framework 通用性。
