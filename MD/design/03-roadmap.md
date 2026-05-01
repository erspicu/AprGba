# 實作路線圖

採 **Plan B：階段性可交付**。每階段獨立可驗收，避免一次大爆炸。

業餘投入估計：每週 8–15 小時。

---

## Phase 0：環境與 Spike（1–2 週）

**目標**：確認 LLVMSharp 在 Windows 11 + .NET 10 可用

任務：
- [ ] `dotnet new sln`，建立架構文件中描述的專案骨架
- [ ] 安裝 `LLVMSharp.Interop`，鎖定 LLVM 版本（建議 17 或 18，視 LLVMSharp 對應）
- [ ] Hello-world：用 LLVMSharp emit `int add(int,int)`，JIT 執行回傳 7
- [ ] 驗證能輸出 `.ll` 檔案
- [ ] 設定 CI（GitHub Actions 或本地 build script）

**驗收**：執行 `add(3,4)` JIT 出 7。

**關鍵風險決策點**：若 LLVMSharp 不可用 → 改 LibLLVM P/Invoke 或 ClangSharp。

---

## Phase 1：JSON Schema 設計（2–3 週） ✅ 完成

**目標**：定義出可表達 ARM7TDMI 的 JSON 格式

完成項目：
- [x] 頂層結構（`cpu.json` + 多個 instruction-set 檔；`extends` 繼承機制）
- [x] Bit pattern 語法（`cccc_001_oooo_s_nnnn_dddd_rrrriiiiiiii`，自動推導 mask/match）
- [x] Field extraction 語法（`"31:28"` / `"5"`）
- [x] Mask/Match 計算規則 + lint 規則（pattern/mask/match 三者一致檢查）
- [x] Micro-op 詞彙表（~75 個 base op，分 13 類）
- [x] 條件執行 wrapper（instruction-set 層級 `global_condition`）
- [x] PC 偏移處理機制（`pc_offset_bytes`）
- [x] Cycle count 標記（S/N/I/C 形式 + extra_when_*）
- [x] 多模式指令長度（`width_bits: 16|32|"variable"` + Thumb-2 預備 `width_decision`）
- [x] CPU variant 與 feature flag（`requires_feature`、`since`/`until`）
- [x] 自訂 micro-op 機制（`custom_micro_ops`）
- [x] JSON Schema validator 檔（Draft 2020-12）
- [x] 完整 ARM7TDMI 範例：`cpu.json`、`arm.json`（MOV/ADD/SUB/CMP + B/BL/BX）、`thumb.json`（F1/F3/F18 子集）

**產出**：
- `MD/design/04-json-schema-spec.md` — schema 設計總文件（18 章）
- `MD/design/05-microops-vocabulary.md` — micro-op 完整參考
- `spec/schema/cpu-spec.schema.json` — JSON Schema validator
- `spec/arm7tdmi/cpu.json` / `arm.json` / `thumb.json` — 範例 spec

---

## Phase 2：JSON Parser + LLVM IR Emitter CLI（4–6 週） ✅ 完成

**目標**：對應 Gemini 對談中的 `aprcpu.exe --input sample.json --output out.ll`

完成項目：
- [x] JSON loader（System.Text.Json，`AprCpu.Core.JsonSpec.SpecLoader`）
- [x] Bit pattern 解析器：`AprCpu.Core.Decoder.BitPatternCompiler` — pattern → mask/match
      + 與 JSON 宣告值 cross-validation（mask/match/fields 三者一致）
- [x] Field extractor：runtime IR shift+mask（在 `EmitContext.ExtractField`）
- [x] Micro-op handler dictionary：`EmitterRegistry` + `IMicroOpEmitter`，~16 個 emitter
      已實作（read_reg/write_reg、add/sub/and/or/xor/shl/lsr/asr、6 種 flag update、
      if/branch/branch_link/branch_indirect、restore_cpsr_from_spsr stub）
- [x] Decoder dispatch table：`AprCpu.Core.Decoder.DecoderTable`（priority + first-match）
- [x] CpuState LLVM struct layout（`AprCpu.Core.IR.CpuStateLayout`）
- [x] 主編譯器：`AprCpu.Core.Compilation.SpecCompiler`
- [x] CLI 介面：`aprcpu --spec <cpu.json> --output <out.ll>`
- [x] Round-trip 驗證：LLVM `TryVerify` 成功（symbol-level shape OK）

**實際結果**：
- 載入 `spec/arm7tdmi/cpu.json` 並編譯產出 15 個 LLVM 函式（ARM 7 個 + Thumb 8 個）
- 35 個 xUnit 測試全綠（loader / pattern / decoder / spec-to-IR）
- 產出的 `.ll` 結構正確：condition gate + S-bit 分支 + register file GEP 都齊全

**Phase 2 故意未做（移到 Phase 2.5）**：
- 完整 ARM cond table（目前只實作 AL/EQ/NE，其他 cond 預設 false）
- LDM/STM block-data-transfer
- Multiply、Halfword Transfer
- 完整 banked register swap（restore_cpsr_from_spsr 是 stub）
- ARM Data Processing 剩餘 12 個 ALU op 與其他 2 種編碼變體
- PSR Transfer (MRS/MSR)
- Single Data Transfer (LDR/STR + variants)
- Single Data Swap (SWP/SWPB)
- SWI、Coprocessor stub、Undefined trap
- Thumb F2、F4–F17、F19

---

## Phase 2.5：ARM7TDMI 規格 + Parser 完整化（~3–4 週）

**目標**：把 spec 補完成 ARM7TDMI **完整 ISA**，parser/emitter 同步補齊
所有 micro-op 與 operand resolver。為 Phase 3 的 ROM 驗證打好「指令層
完整」基礎。

詳細子階段、deliverable 與 done-criteria 見
`MD/design/06-arm7tdmi-completion-plan.md`。

**子階段**：
- 2.5.1 Spec authoring 規範 + lint 強化
- 2.5.2 ARM Data Processing 全集（×3 編碼）+ PSR Transfer
- 2.5.3 ARM Memory Transfers（SDT、Halfword/Signed、SWP）
- 2.5.4 ARM Multiply（含 Multiply Long）
- 2.5.5 ARM Block Transfer + SWI + Coprocessor stub + Undefined
- 2.5.6 Thumb 剩餘 16 種格式（F2、F4–F17、F19）
- 2.5.7 完整 cond table + banked register swap + restore_cpsr_from_spsr
- 2.5.8 Coverage 驗證 + closeout

**驗收**：
- arm.json + thumb.json 涵蓋 ARMv4T 全部標準指令
- LLVM module 通過 Verify，0 diagnostics
- 所有 micro-op 名稱與 operand_resolver kind 都有 emitter
- 50+ known opcode 通過 decoder coverage test

---

## Phase 3：最小直譯器驗證（3–4 週）

**目標**：先不上 JIT，跑通指令語義正確性

任務：
- [ ] `CpuState` struct 與 register file
- [ ] 簡易 fetch–decode–execute 迴圈（無 cache）
- [ ] 條件執行檢查（每條 ARM 指令前檢查 cond）
- [ ] PC 自動 +4 與 branch 處理
- [ ] R15 讀取時補 +8 偏移
- [ ] Banked register swap（模式切換）
- [ ] 為 5 條指令寫 xUnit 單元測試，對照 ARM 手冊預期值
- [ ] 加入 `arm.gba` 測試 ROM 的子集（先跑 5–10 個 case）

**驗收**：對選定指令集，所有 case 通過。

---

## Phase 4：擴充 ARM 指令至完整集（6–8 週）

**目標**：JSON 涵蓋 ARM 模式所有主要指令

任務：
- [ ] **Data Processing 全集**：AND, EOR, SUB, RSB, ADD, ADC, SBC, RSC, TST, TEQ, CMP, CMN, ORR, MOV, BIC, MVN
- [ ] **Barrel Shifter**：LSL, LSR, ASR, ROR（與 Data Processing 整合）
- [ ] **Multiply**：MUL, MLA
- [ ] **Multiply Long**：UMULL, UMLAL, SMULL, SMLAL
- [ ] **Single Data Transfer**：LDR, STR + variants（含 byte / halfword / 對齊處理 / pre-post indexing）
- [ ] **Halfword Transfer**：LDRH, STRH, LDRSB, LDRSH
- [ ] **Block Data Transfer**：LDM, STM（最棘手，register list 可能需 C# handler）
- [ ] **Branch**：B, BL, BX
- [ ] **PSR Transfer**：MRS, MSR
- [ ] **Software Interrupt**：SWI
- [ ] **Coprocessor**：拋例外（GBA 沒用）
- [ ] armwrestler ARM 模式測試全綠

**驗收**：armwrestler ARM 模式測試 100% 通過。

---

## Phase 5：Memory Bus + ROM Loader + 跑 BIOS（3–4 週）

任務：
- [ ] `IMemoryBus` 實作 + GBA memory map
  - BIOS / EWRAM / IWRAM / IO / PRAM / VRAM / OAM / GamePak ROM / SRAM
- [ ] 載入 `.gba` ROM（headers、entry point）
- [ ] BIOS 載入策略：
  - 選項 A：使用者自備 BIOS（合法考量）
  - 選項 B：HLE BIOS（重寫關鍵 SWI handler，初版只實作必要 SWI）
- [ ] 從 `0x08000000` 開始跑 ROM entry point
- [ ] 處理基本 reset / SWI 中斷向量

**驗收**：能跑通 GBA BIOS 開機畫面或 ROM entry point 不 crash（PPU 還沒，看 log 即可）。

---

## Phase 6：Thumb 模式（4–6 週）

任務：
- [ ] `thumb.json`：Thumb 全部 19 類指令格式
- [ ] T bit / `CPSR.T` 狀態追蹤
- [ ] BX 切換模式邏輯
- [ ] PC 偏移：Thumb 為 +4
- [ ] Low/High register 限制
- [ ] thumb.gba 測試 ROM 全綠

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

**驗收**：上 JIT 後跑 armwrestler / thumb.gba 全綠 + 至少不慢於 Phase 5 直譯器。

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
- 第二顆 CPU 驗證框架通用性（如 NES 6502）
- AOT 預編譯 `.bc` 快取

---

## 風險與後備計畫

| 階段 | 風險 | 後備方案 |
|---|---|---|
| Phase 0 | LLVMSharp 不可用 | 改 LibLLVM P/Invoke 或 ClangSharp |
| Phase 4 | LDM/STM 太複雜難用 JSON 表達 | 視為 C# handler 例外 |
| Phase 5 | BIOS 取得問題 | HLE BIOS（自重寫關鍵 SWI handler） |
| Phase 7 | LLVM 編譯太慢 | 改 .NET DynamicMethod 自寫輕量 JIT |
| Phase 8 | Avalonia 整合難 | 改 WinForms 純 Windows 版 |

---

## 里程碑時程（業餘 8–15h/週）

| 里程碑 | 內容 | 累計時間 |
|---|---|---|
| **M1** | Phase 0–1 完成（環境 + JSON schema） ✅ | ~1 月 |
| **M2** | Phase 2 完成 ✅（CLI 工具 demo） | ~3 月 |
| **M2.5** | Phase 2.5 完成（ARM7TDMI 完整 spec + parser） | ~4 月 |
| **M3** | Phase 3–5 完成（直譯器 + armwrestler + 跑 BIOS） | ~7 月 |
| **M4** | Phase 6 完成（Thumb 跑得動） | ~9 月 |
| **M5** | Phase 7–8 完成（**畫面出來**） | ~12–15 月 |

---

## 下一步：Phase 2.5 — ARM7TDMI 完整化

Phase 0、1、2 已完成。CLI 已能從 JSON 產 LLVM IR；目前 spec 內容只是
ARM 7 條 + Thumb 8 條的示範子集。

進入 Phase 3（直譯器跑 ROM）之前，先把 spec 補完整、parser 同步補齊：

1. **2.5.1**：spec 寫法規範文件 + lint 強化
2. **2.5.2**：ARM Data Processing 全集（×3 編碼）+ PSR Transfer
3. **2.5.3**：ARM 記憶體傳輸（SDT、Halfword/Signed、SWP）
4. **2.5.4**：ARM Multiply（含 Multiply Long）
5. **2.5.5**：ARM Block Transfer + SWI + Coprocessor stub + Undefined
6. **2.5.6**：Thumb 剩餘 16 種格式
7. **2.5.7**：完整 cond table + banked register swap
8. **2.5.8**：Coverage 驗證 + closeout

詳細見 `MD/design/06-arm7tdmi-completion-plan.md`。
