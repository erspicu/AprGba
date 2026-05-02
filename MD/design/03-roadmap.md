# 實作路線圖

採 **Plan B：階段性可交付**。每階段獨立可驗收，避免一次大爆炸。

業餘投入估計：每週 8–15 小時。

> **狀態快照**（2026-05-02 更新）：Phase 0/1/2/2.5/2.6/3/4.1/4.2/4.3/4.4/
> 4.5/5/8 ✅ 完成；**Phase 5.8 emitter library refactor 5.1–5.7 完成**
> （`Lr35902Emitters.cs` 從 ~2620 → 1346 行，−49%；27 個 LR35902-specific
> ops 收成 generic + 通用 ops；剩 13 個全在 clearly-marked L3 intrinsic
> 區段）。GBA 端從 test-ROM 驗證一路推到 **真 Nintendo BIOS LLE** +
> 完整 PPU pipeline（Mode 0/1/2/3/4 + OBJ + BLDCNT alpha/brighten/darken
> + WININ/WINOUT/OBJ Window）。BIOS 開機 logo 視覺與 mGBA 同等。GB 端
> 拿到 BIOS LLE + DMG Nintendo® logo 截圖。345 個 unit test 全綠，跨
> GB + GBA 兩 CLI 操作介面 (`--bios` / `--cycles` / `--frames` /
> `--seconds` / `--screenshot`) 一致。完整收工筆記見
> `MD/note/phase5.7-bios-lle-and-ppu-2026-05.md`；refactor 進度見
> `MD/design/11-emitter-library-refactor.md`。
> 下一步可選：Phase 5.8/5.9 第三 CPU 驗證（RISC-V / MIPS）→ 驗證 L3
> floor 是否合理 → 視情況再進 Phase 7 block-JIT / APU。

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

## Phase 3：Host Runtime + 直譯器驗證（3–4 週） ✅ 完成

**目標**：把編出來的 LLVM IR 真的執行起來，確認指令語義正確。

任務：
- [x] **3.1 Host runtime skeleton**
  - C# 端 `CpuState` struct（與 `CpuStateLayout` 動態 layout 對齊）
  - Memory bus extern 實作（`host_mem_read8/16/32`、`host_mem_write8/16/32`）
  - `host_swap_register_bank` 實作（依 mode 把 banked R8–R14 swap 進可見槽位）
  - LLVM ORC JIT 綁定 externs
- [x] **3.2 Fetch-decode-execute loop**
  - 走 PC、`DecoderTable` 查身分、dispatch 到 JIT 過的函式、推進 PC
  - 條件執行已在 cond gate 內，主迴圈不再特別處理
  - R15 +8/+4 偏移已在 emitter 內，主迴圈不再特別處理
  - 無 code cache，每條指令一次 dispatch（先求對，不求快）
- [x] **3.3 Golden tests vs ARM 手冊**
  - 5–10 條手挑指令（ADD with flags、LDR with writeback、B with cond、
    MOV with shifter carry、MSR/MRS、SWI 驗證 banked swap）
  - 每個 test：建立 CpuState、跑、assert 後狀態符合 ARM ARM 預期值

**驗收**：選定指令集，所有 case 通過。`host_swap_register_bank` 經 SWI
測試走通。

**最大未知數**：LLVMSharp 20.x 的 ORC JIT API surface — 從這裡開始
de-risk。

---

## Phase 4：jsmolka arm.gba + thumb.gba CPU 驗證（~2 週） ✅ 完成

> **實際路線**：原規劃跑 armwrestler，後改用 jsmolka `gba-tests`（headless
> 設計，更適合自動化）。Phase 4.1 GBA memory bus、4.2 halt detection、
> 4.3 arm.gba 全綠、4.4 thumb.gba 全綠。armwrestler 留到 Phase 8 PPU
> 出來後做視覺對照。

完成項目：
- [x] **4.1** GBA memory bus + ROM loader + IO stub（DISPSTAT toggle，
  避免 m_vsync 死循環；BIOS 向量塞 MOVS PC, LR no-op）
- [x] **4.2** CpuExecutor.RunUntilHalt 偵測 B-to-self halt loop
- [x] **4.3** **jsmolka arm.gba 全部 subtest 通過**（R12=0，~535 個 subtests
  涵蓋 Data Processing/Multiply/SDT/HSDT/SWP/Block Transfer/Branch/PSR/
  SWI/Coprocessor stub/Undefined）。修了 13+ 個 ARMv4 真實 CPU 語意 bug。
- [x] **4.4** **jsmolka thumb.gba 全部 subtest 通過**（R7=0，~230 個 subtests
  涵蓋 logical/shifts/arithmetic/branches/memory）。修了 ~10 個 Thumb
  特有 bug。multi-set CpuExecutor 透過 CPSR.T dispatch ARM/Thumb。

**驗收結果**：
- arm.gba R12=0 in 7409 instructions
- thumb.gba R7=0 in 6874 instructions
- **195/195 unit tests 全綠**

CPU 正確性已通過真實 ROM 端到端驗證，framework 對 ARMv4T 完整指令集
的語意理解過了非常嚴格的考驗。

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

**現況** ✅ **全部完成**（2026-05-02）：
- ✅ 4.5A：apr-gb CLI + GB memory bus + ROM loader + serial capture
- ✅ 4.5B：`LegacyCpu`（big-switch 直譯）+ DMG PPU stub + PNG 截圖；通過
  Blargg cpu_instrs **11/11 全部子測試**（含 02-interrupts，以 EI 延遲 +
  cycle-table 驅動的 DIV/TIMA timer 實作完成）
- ✅ 4.5C：`spec/lr35902/*.json`（23 個 group 檔，501 opcodes）+
  `Lr35902Emitters.cs`（~50 個 micro-op）+ `JsonCpu` backend；通過
  Blargg cpu_instrs **11/11 + master "Passed all tests"**，跟 LegacyCpu
  截圖完全一致。設計依據見
  [`10-lr35902-bit-pattern-groups.md`](./10-lr35902-bit-pattern-groups.md)。

**「換 CPU = 換 JSON」這個論點已被驗證**（ARM7TDMI + LR35902 兩顆完全
不同的 CPU 都跑通了 framework 的 spec→IR→JIT 主路）。Phase 4.5 完工
紀錄參見 `MD/note/framework-emitter-architecture.md` 跟
`MD/note/performance-baseline-2026-05.md`。

---

## Phase 5：Memory Bus + BIOS LLE + PPU 完整化 ✅ 完成 (2026-05-02)

> **完工筆記**：`MD/note/phase5-gba-mvp-complete-2026-05.md` (5.1–5.4)
> + `MD/note/phase5.7-bios-lle-and-ppu-2026-05.md` (5.5–5.7)。
>
> Phase 5 從原本「memory bus + 最簡 IRQ」一路推到 **真 BIOS LLE +
> 完整 PPU**：
>
> - **5.1 IRQ + LLE BIOS foundation** — IO 寄存器、IF/IE/IME、bus.LoadBios
> - **5.2 DMA 4 channel** — 立即/VBlank/HBlank trigger + 16/32-bit transfer
> - **5.3 Cycle scheduler + IRQ delivery** — VBlank/HBlank/VCount IRQ 完整
> - **5.4 apr-gba CLI** — headless ROM screenshot
> - **5.5 DISPSTAT/HALTCNT** — 拔掉 phase 4.x toggle hack；HALTCNT 真停 CPU
> - **5.6 Cart logo + checksum patcher** — homebrew 過 BIOS 驗證
> - **5.7 BIOS LLE + 5 連環 ARM bug + PPU 完整化 + GB CLI 對齊**
>
> 5.7 修了 5 個 ARM7TDMI bug（Thumb BCond +0 idiom、LSL/LSR by 32 carry、
> shift-by-reg PC+12、UMULLS/SMULLS NZ flag、exception entry 清 T）；
> 重寫 PPU 成 per-layer composite + Mode 0/1/2 + 完整 BLDCNT + Windowing；
> GB CLI 加 `--bios` / `--seconds` 對齊 GBA。

### 完工驗收（5.1–5.7 累計）

| 驗收項 | 結果 |
|---|---|
| jsmolka arm.gba (HLE) | All tests passed |
| jsmolka thumb.gba (HLE) | All tests passed |
| jsmolka arm.gba (BIOS LLE) | All tests passed @ 6 GBA-s |
| jsmolka thumb.gba (BIOS LLE) | All tests passed @ 6 GBA-s |
| jsmolka bios.gba (BIOS LLE) | All tests passed @ 6 GBA-s |
| BIOS startup logo (2.5 GBA-s) | mGBA-equivalent visual |
| DMG BIOS Nintendo® logo (apr-gb) | 顯示正確 |
| Mode 0 stripes / shades | 視覺正確 |
| 345 unit tests | 全綠 |

---

## Phase 5.8：Emitter Library Refactor 🚧 進行中 (2026-05-02 起)

> **設計依據**：[`MD/design/11-emitter-library-refactor.md`](./11-emitter-library-refactor.md)。
>
> 動機：Phase 5.7 完工後 `Lr35902Emitters.cs` 達 ~2620 行（40 個 op），
> 違背 framework「換 CPU = 換 JSON」承諾。本階段把每顆 CPU 的 emitter
> 工作量從「~40 個 ops × C# 實作」降到「最多 5–10 個 truly-unique
> ops + 配置 metadata」。**不影響 Phase 5.7 的 jsmolka/Blargg/PPU
> 驗證結果，純結構優化。**

進度（持續更新；每 step 一個 commit + cleanup commit）：

| Step | 狀態 | 內容 |
|---|---|---|
| 5.1 | ✅ | Stack ops 通用化（push_pair/pop_pair/call/ret + spec stack_pointer） |
| 5.2 | ✅ | Flag setters（set_flag/update_h_add/update_h_sub） |
| 5.3 | ✅ | Branch / call_cc / ret_cc 統一 + read_pc/sext + LR35902 JP/JR/CALL/RET/RST 全 cc 變體 migration + cleanup |
| 5.4 | ✅ | Bit ops + 8 種 shift 統一 + LR35902 BIT/SET/RES + CB-shift + A-rotate migration + cleanup |
| 5.5 | ✅ | Memory IO region 統一（Binary auto-coerce + LDH/LD-(C) 改 generic `or` chain）+ cleanup |
| 5.6 | ✅ | 刪 cb_dispatch no-op；IME/HALT/DAA 標 L3 intrinsics with reasons |
| 5.7 | ✅ | flag micro-ops (CCF/CPL) + INC/DEC family + 16-bit selector splits + L3 marker for 操作元 resolver / compound ALU |
| 5.8/5.9 | ⏳ | 第三 CPU 驗證（RISC-V RV32I 或 MIPS R3000） |

**5.7 收工 snapshot**：

- `Lr35902Emitters.cs` 從 ~2620 行 → **1346 行（−49%）**
- 新增/擴大通用層：`StackOps.cs`（~415L）、`FlagOps.cs`（~155L）、
  `BitOps.cs`（~195L）、`Emitters.cs` 加 ~160L
- 已刪 **27 個 LR35902-specific ops + 多個 helper**
- 剩下 13 個 LR35902 ops 全部在 clearly-marked L3 intrinsic 區段，
  分三類：操作元 resolver（sss/dd 表）、compound ALU + flag rules、
  真 L3 hardware quirks（IME delay / HALT bug / DAA）
- 345/345 unit test 維持全綠
- Blargg 02–11 在 legacy + json-llvm 兩條後端 Passed

**驗證原則**：每個 step 結束跑全套 unit test + 至少 3 個 Blargg
sub-test（含該 step 改到的 op 直接覆蓋的測試）在兩條後端 Passed。
任一掉測就 stop-the-line，不往下走。

**Refactor 對效能的影響**：跑了同樣的 1200-frame loop100 bench
（commit `cdf04ce`），結果 GBA json-llvm 微幅進步 (+1-4%)、GB
json-llvm 微幅退步 (−3%)、GB legacy 退 −15%（多次 run 後仍偏離
baseline，但 single run 只 ~300ms 量測 noise 比例高）。詳細數據見
`MD/note/loop100-bench-2026-05-phase5.8.md`。**結論：refactor 對主
框架路徑 (json-llvm) 是 perf-neutral**，完成「結構乾淨 vs 速度中
性」的 trade-off。

**剩下要做的事**：
- 5.8/5.9 第三 CPU 驗證（RISC-V RV32I 或 MIPS R3000）— 真正驗證
  L3 floor 是否合理。等到第三 CPU 試 port 才知道是否需要再加
  「operand resolver registry」之類 spec schema 擴充。
- 在第三 CPU 之前，refactor 階段算告一段落。下一階段：第三 CPU。
  （Phase 7 block-JIT 跟 APU 暫不做 — 兩者都不影響「JSON-driven CPU
  framework」這個研究主題的驗證；商業遊戲相容 / 即時 60fps 不在 MVP
  scope。）

---

## Phase 5：Memory Bus 收尾 — 原 scope（已被擴展，僅供 reference）

> **Scope decision (2026-05)**：GBA 端的最終目標是「跑 test ROM + 截圖
> 驗證」（jsmolka 那種），**不**追商業遊戲、**不**追更多 homebrew 相容
> 性。所以 Phase 5 範圍從原本的「能放商業 ROM 不 crash」縮成「PPU 能拿
> 到正確 framebuffer 內容」。
>
> Phase 4.1–4.4 已經把 GBA memory bus + ROM loader 蓋掉一大半
> （`AprCpu.Core.Runtime.Gba.GbaMemoryBus` 已能載 jsmolka ROM 並從
> 0x08000000 跑通 ARM 跟 Thumb subtest）。

剩下最小任務：
- [ ] PPU 相關 IO 暫存器讀寫（DISPCNT / DISPSTAT / VCOUNT / BGxCNT /
  BGxHOFS/VOFS / WIN0H 等）— 只要 PPU 能讀到 CPU 寫的值
- [ ] VBlank 中斷觸發 + IE/IF/IME 基本中斷流程（test ROM 的 CPU 端
  HALT/IRQ 等待會用到）
- [ ] DMA channel 0–3（test ROM 大量用 DMA 把 tile data 跟 palette
  搬進 VRAM；不做的話畫面是空的）
- [ ] **BIOS LLE：自備 GBA BIOS file（gba_bios.bin），從 0x00000000
  開始跑**。不走 HLE shortcut — 跑完官方 BIOS intro → ROM entry，這樣
  CPU + memory bus + interrupt 都會被 BIOS 開機流程實際打到，比 HLE
  跳過去更有公信力
- [ ] BIOS 載入路徑：CLI flag `--bios=<path>` 或讀環境變數

**明確不做**：
- HLE BIOS shortcut（用戶要 LLE 真開機，不是假開機）
- 完整 SRAM / Flash / EEPROM save（test ROM 不需要）
- KEYINPUT polling（沒人按按鍵）
- SOUNDCNT / 音效相關（沒 APU）
- Timer 0–3 的精確版（PPU 用到的最簡 timing 直接內嵌進 PPU loop）

**驗收**：
- 帶 BIOS 跑 jsmolka arm.gba / thumb.gba 仍然全綠（之前跳 BIOS 直接
  從 ROM entry 開始）
- BIOS intro 跑完進 ROM entry 之間，PC trace 跟 mGBA 對得上
- test ROM 跑完後 VRAM 內容是「該長那樣」（用 mGBA dump 對拍）

---

## Phase 6：Thumb 模式驗證 ✅ 完成（併入 Phase 4.4）

原規劃的 Thumb 工作（CPSR.T 狀態追蹤、BX 模式切換、thumb.gba 驗證）
**全部在 Phase 4.4 完成**。CpuExecutor 用 multi-set 設計，每步讀
CPSR.T dispatch 到對應 InstructionSet 的 decoder 與 PC 偏移。
jsmolka thumb.gba 通過，ARM↔Thumb BX 切換在實 ROM 中驗證過。

---

## Phase 7：LLVM Block JIT + Code Cache — 可選優化

> **Scope decision (2026-05)**：原本 Phase 7 定位是「整個專案最具挑戰
> 性的階段、GBA 跑到實機速度以上的關鍵」。但 GBA 端目標縮成「test ROM
> + 截圖驗證」後，real-time 60fps 不再是必要 — test ROM 跑慢一點
> （現在 ~40% real-time，~10 秒跑完一個 jsmolka subtest）也只是等
> 截圖出來而已。**Phase 7 從必要降為可選**。

值得做的時機：
- 想實際把 framework 用在「跑遊戲」級別的場景（commercial GBA ROM、
  homebrew 平台、其他比 GBA 重的 CPU 模擬目標）
- 想 demo「JSON-driven CPU framework 經過 block-JIT 也能逼近 native
  interpreter 速度」這個進階論點
- 想做後續其他 CPU 移植的時候，避免每顆 CPU 都被 dispatch overhead
  拖累

**當前 canonical baseline（2026-05-02，Phase 5.7 完工後，instruction-level
JIT）見 `MD/note/loop100-bench-2026-05.md`**。1200 frames stress-test
ROM (`*-loop100.gba` / `09-loop100.gb`) 統一量法的 4 個 reference 數字：

| ROM                  | Backend     | Real-time × |  MIPS  |
|----------------------|-------------|------------:|-------:|
| GBA arm-loop100      | json-llvm   |   0.9×      |   3.68 |
| GBA thumb-loop100    | json-llvm   |   0.9×      |   3.70 |
| GB  09-loop100       | LEGACY      |  79.4×      |  38.36 |
| GB  09-loop100       | json-llvm   |   5.6×      |   2.73 |

Phase 7 完成後重跑這 4 個 case 看加速幅度（預期 json-llvm 拉到
25-50 MIPS = 8-13× 加速）。Phase 4.5 階段的舊 baseline
(`MD/note/performance-baseline-2026-05.md`) 已 superseded — 跑法
不統一、ROM 不同，僅作歷史紀錄。

### 真做的時候要做的事

完整清單。前面 Phase 5.8 emitter refactor 把結構整理乾淨後，這裡列
所有「真要把 JIT 推到極致」會碰到的優化面向。逐項做、每項做完跑
canonical loop100 bench (`MD/performance/202605030002-jit-optimisation-starting-point.md`)
記錄 before/after。

順序大致從低風險高回報 → 高風險未驗證。

#### A. Block-level JIT（核心，textbook 路徑）

- [ ] **Block detector**：從 PC 掃到 branch / return / IO 寫入為止
- [ ] **Block-level IR generation**（多條指令串接成一個 LLVM Function，
  讓 LLVM optimizer 在大 basic block 內做 register caching、CSE、
  constant folding、DCE）
- [ ] **LLVM JIT execution engine 升級到 ORC LLJIT**（解掉 MCJIT 的
  Windows COFF section 限制 + 解鎖 lazy compile / on-demand 編譯）
- [ ] **Code cache**（hashmap PC → fn pointer + LRU eviction）
- [ ] **SMC 偵測 + invalidation**：寫入「已編譯區域」時 invalidate
  (memory bus write 攔截 + page-level dirty bit table)
- [ ] **Indirect branch dispatch**：BX / JP HL / RET → cache lookup +
  fall-through to dispatcher
- [ ] **Block linking**：直接 patch native call site，後續 branch 不
  退出 JIT
- [ ] **State→register caching**：block entry 把常用 reg load 進
  LLVM virtual register，exit 才 store 回 state buffer
- [ ] Performance profiling 工具（在 host 端記錄 block 編譯次數 /
  執行次數 / 佔總執行時間比例）

#### B. IR-level inlining / 微指令融合（Gemini 建議 #1）

- [x] **OptLevel 調 O3** ~~+ 加入額外 LLVM passes (instcombine, gvn,
  simplifycfg, mem2reg, reassociate)~~ — 2026-05-03 完成。perf-neutral
  (±3%, see `MD/performance/202605030025-optlevel-0-to-3.md`)。原因：
  per-instruction function 太小、LLVM 沒空間發揮，瓶頸在 dispatcher
  overhead。保留 O3 給未來 block-JIT 用，後續策略應跳過 B.b/c/d 先攻
  A (block-JIT) 或 E (mem-bus fast path)。
- [ ] **同 register 的 GEP CSE**：目前每次 read_reg 都重新 GEP；同
  function 內多次 access 同 reg 應該 CSE 成一個 alloca
- [ ] **多 micro-op step 融合**：`add` + `update_zero` + `update_h_add`
  + `set_flag(N)` 經常一起出現，spec compiler 端可以辨識常見 pattern
  emit 成單一 inlined IR sequence
- [ ] **加 `nuw` / `nsw` hint**：常見的 PC+4 / SP-2 加減確定不溢位，
  加 hint 給 LLVM 更多 optimisation 餘地
- [x] **B.e Cache state-buffer offsets in CpuExecutor**（2026-05-03，perf
  note `MD/performance/202605030054-cache-state-offsets-cpuexecutor.md`）
  — Step() 之前每條指令呼叫 `_rt.PcWrittenOffset` / `_rt.GprOffset(_pcRegIndex)`
  都 cascade 進 LLVM.OffsetOfElement P/Invoke (4-5 次/指令)；cache 在
  ctor 後改用 cached `int _pcGprOffset` / `_pcWrittenOffset` + 加
  AggressiveInlining + 私有 ReadPc/WritePc fast path。**GBA arm +21%
  vs F.y (5.94 → 7.19 MIPS, 個別 run 1.9× real-time)**, GBA thumb 持平
  (噪聲), GB json-llvm +1.7% (JsonCpu 已自帶 cache, 沒受惠)。
- [ ] **scheduler.Tick / NotifyExecutingPc / NotifyInstructionFetch
  inlining**：目前 per-instruction 多 2-3 次 virtual call；改 inline
  C# method + `[MethodImpl(AggressiveInlining)]` 或乾脆把這些 hook
  emit 進 IR

#### C. Lazy flag computation（Gemini 建議 #2）

- [ ] **ARM CPSR NZCV lazy**：目前每條 ALU 指令都計算 N/Z/C/V 寫進
  CPSR；改成「只記錄 last-ALU-result + ALU-kind」，conditional
  execution 真要讀 flag 時再 derive
- [ ] **LR35902 F register lazy**：INC r 的 Z/N/H 不要每次都寫，後續
  指令若是 BIT/CP/JR cc 才 derive
- [ ] **Flag dependency tracking**：emitter 標 "this op produces
  flag X / consumes flag Y"，spec compiler 在 block 內做 def-use
  analysis 省略不必要的 flag 寫入
- [ ] **PcWritten flag 改 LLVM register hint**（5.8 已標 candidate）—
  避免每條指令 byte slot load/store；只在 branch 指令後檢查

#### D. Hot path / Tier compilation（Gemini 建議 #4 增補）

- [ ] **Block 執行次數 counter**：每次進 block 加 1，超過 threshold
  (e.g. 1000) 才升 tier
- [ ] **Cold block O0 編譯**：第一次見到 block 時用 O0 快編好（< 1ms），
  先讓 ROM 跑起來
- [ ] **Hot block O2/O3 重編 + register caching aggressive**：背景
  thread 重編 hot block，編好後 patch caller fall-through
- [ ] **Tier degradation**：SMC invalidate 後降回 cold tier
- [ ] **Profile-guided optimisation**：counter + branch-direction 統計，
  hot 的 cond branch flip 成 fallthrough

#### E. 減少 extern binding / mem-bus fast path（Gemini 建議 #5）

- [ ] **Mem-bus region table inline check**：JIT'd code 自帶
  「addr ∈ ROM/RAM region 直接 GEP；else fallback to extern call」
  fast path
- [ ] **Sub-page 粒度 fast-path**（GBA WRAM 0x02000000-0x0203FFFF、
  GB HRAM 0xFF80-0xFFFE、cart ROM 0x08000000-0x09FFFFFF 等熱區）
- [ ] **Read-only ROM fast path**：cart ROM read 直接 byte-array
  index，省 extern call 完全
- [ ] **Aligned word access**：4-byte / 2-byte aligned read/write 走
  i32/i16 直接 access，不拆成 byte sequence

#### F. Dispatcher / cycle-accounting 簡化

- [x] **F.x InstructionDef-keyed fn pointer cache**（2026-05-03，commit
  `9fcf511` + perf note `MD/performance/202605030036-fnptr-cache-by-instruction-def.md`）
  — dispatcher 改用 reference identity 而非 string-keyed cache。**GB
  json-llvm +82% (2.66 → 4.83 MIPS), GBA Thumb +25%**, GBA ARM +2%（noisy）。
- [x] **F.y Pre-built DecodedInstruction cache**（2026-05-03，perf
  note `MD/performance/202605030047-prebuilt-decoded-instruction.md`）
  — DecoderTable.Decode 改回傳預先建好的 instance，省掉 per-decode
  `new DecodedInstruction(...)` heap alloc + foreach IEnumerator alloc。
  **GBA arm +55% (3.82 → 5.94), GBA thumb +67% (3.75 → 6.27), GB
  json-llvm +137% (2.66 → 6.30)**，GBA 兩條 path 首次跨過 1.0× real-time。
- [ ] **Dispatcher 從 hash-lookup 改 direct table**（decoded opcode
  → fn pointer 陣列）— F.x/F.y 已撈大頭，這個是進一步壓榨
- [ ] **Cycle accounting trailing add**：block 結束時一次累加 cycle
  總數，不每條指令 inc
- [ ] **IRQ check 集中在 block exit**，不是每條指令

#### G. .NET host runtime 優化

- [ ] **Native AOT** — 把整個 host runtime AOT 編譯，避開 .NET
  tiered JIT 的 cold-start cost (對 GB legacy 那 −15% 量測 noise
  也可能改善)
- [ ] **UnmanagedCallersOnly trampolines 走 IL emit**：直接 patch
  entry，避免 .NET wrapper 的 prologue overhead

### 後備（萬一上面某項實作不順）

若 LLVM 編譯太慢造成不可接受 stutter：
- 後備 1：降 OptLevel 至 O0（或部分 block O0）
- 後備 2：tiered compilation（冷 block 用 O0，熱 block 升 O2 — 已在 D）
- 後備 3：改用 .NET `DynamicMethod` / `System.Reflection.Emit` 自寫
  輕量 IL JIT，整批跳過 LLVM
- 後備 4：對少數 hot-loop ROM 做 ahead-of-time `.bc` cache（spec
  → IR → bitcode 寫進 disk，下次直接 load）

### 怎麼跑這個 phase

每實作一項就跑 canonical loop100 bench，新檔寫進 `MD/performance/`，
檔名 `YYYYMMDDHHMM-<策略名>.md`，引用起始基準
`MD/performance/202605030002-jit-optimisation-starting-point.md`，
記錄 before/after delta + 該策略 hypothesis。**不要批一次做完幾項
混在一起跑**，會搞不清楚哪個帶來收益。

---

## Phase 8：PPU 完整化 ✅ 完成 (2026-05-02)

> **完工狀態**：Phase 8「最小 PPU + headless 截圖」原本 scope 是
> Mode 0 + Mode 3 + 截 jsmolka pass/fail。實際做出來涵蓋更廣 —
> Mode 0/1/2/3/4 + 完整 OBJ + BLDCNT alpha/brighten/darken +
> WININ/WINOUT/OBJ Window，BIOS 開機 logo 視覺與 mGBA 同等。
> 細節見 `MD/note/phase5.7-bios-lle-and-ppu-2026-05.md` 第 5 節。
>
> 仍未做：Mode 5（罕用）、Mosaic（少用）。
>
> 整套 PPU 仍是 hand-written 沒走 JSON 化（per scope decision），
> ~600 lines C# 在 `src/AprGba.Cli/Video/GbaPpu.cs`。

---

## Phase 8：最小 PPU + headless 截圖 — 原 scope（已被超越，僅供 reference）

> **Scope decision (2026-05)**：兩個關鍵 scope cut：
>
> 1. **PPU 不走 JSON spec 路線** — 直接 host code 寫一個 `GbaPpu`
>    class（學 GB 那邊的 `GbPpu`）。理由：PPU 不是 instruction stream
>    （CPU 才是），它是 fixed-function pipeline；JSON 描述沒有跨設備
>    reusability，硬 JSON 化沒有 framework 槓桿
> 2. **不做 GUI、不做 60fps loop、不做即時播放** — 只做 headless CLI
>    模式：跑 N cycles → render framebuffer → 寫 PNG。整個目標就是
>    「能用 PNG 截圖驗證 test ROM 算對了」

### 任務（最小可工作版）

- [ ] `GbaPpu` class 跟 `GbPpu` 一樣的形狀：拿一個 `GbaMemoryBus`，
  根據 DISPCNT 的 mode + VRAM/PRAM/OAM 內容渲染 240×160 framebuffer
- [ ] 至少支援 Mode 3（直接 RGB555 framebuffer）跟 Mode 0（4 個
  tile-based BG layer）— jsmolka test ROM 用 Mode 0 + tile-based 字
  形 print 結果
- [ ] Sprite 不一定需要（看 jsmolka 用不用）
- [ ] CLI: `apr-gba --rom=X.gba --cycles=N --screenshot=Y.png`，學
  `apr-gb` 的設計
- [ ] 截圖跟 mGBA 同 ROM 同 cycles 結果比對

### 明確不做

- Avalonia / WinForms 即時視窗
- 60fps 主迴圈
- VBlank-driven 畫面更新
- 商業遊戲 / 進階 homebrew 嘗試
- Mode 1/2/4/5、Window、Mosaic、Affine BG、Blend 等進階特效
  （jsmolka 用不到就不做）

### 驗收

- jsmolka arm.gba / thumb.gba 跑完後的 framebuffer PNG 跟 mGBA 截圖
  逐 pixel 一致（或至少肉眼一致 — 這已經完成「視覺驗證」目標）
- 整個 CLI 流程跟 apr-gb 一樣 headless：load ROM → run cycles →
  render → save PNG

### 🎯 里程碑

「test ROM 結果能用 PNG 看到」。GBA 端的視覺驗證收尾。

---

## Phase 9 及之後（不在 MVP 範圍）

未來可選方向：
- 完整 PPU 進階特效（Window、Mosaic、Affine BG mode 1/2、Blend）—
  Phase 8 沒做完的部分
- APU（音訊，4 個 channel + DMA-driven sound）— **hand-written，不
  JSON 化**（同 PPU 理由：不是 instruction stream）
- 商業遊戲相容性測試與 bug fix
- 開源、文件、社群
- AOT 預編譯 `.bc` 快取（避開 cold-start LLVM 編譯成本）
- 第三顆 CPU 驗證（Phase 4.5 GB 之後若想再驗證一次，可考慮 MIPS R3000、
  RISC-V RV32I 之類；6502 因為太簡單，覆蓋不到 framework 已驗證的點，
  不特別優先）

### 明確不打算做的事（避免 scope creep）

- **PPU/APU/DMA 寫成 JSON spec**：framework 的「資料 vs 動詞」拆分
  只對 instruction stream 有意義；fixed-function units 沒有跨設備的
  reusability，硬 JSON 化沒有槓桿效益（見 Phase 8 設計決定）
- **完整 cycle-accurate timing**：採 instruction-level catch-up 已
  足夠跑通常見遊戲；想做 cycle-perfect 留給更後期
- **GBA 之外的整機（NDS、PS1 等）**：第三顆 CPU 驗證可能做，但整機
  emulator 不在 scope

---

## 風險與後備計畫

| 階段 | 風險 | 後備方案 |
|---|---|---|
| Phase 0 | LLVMSharp 不可用 | ✅ 已驗證，LLVMSharp 20.x + libLLVM 20 + .NET 10 OK |
| Phase 3 | LLVMSharp ORC JIT API 不熟 | 從 Phase 0 spike 的 add(3,4) 樣板擴出去；最差走 MCJIT |
| Phase 4.5 | GB CPU spec 寫法卡到 paired register | 擴 schema 加 `register_aliases`；或在 spec 用兩個 8-bit + emitter helper |
| Phase 5 | BIOS 取得問題 | HLE 最小 SWI handler set；不需要自備 BIOS file |
| Phase 7 (可選) | LLVM 編譯太慢 | 改 .NET DynamicMethod 自寫輕量 JIT；或乾脆不做（test ROM 跑慢一點沒差） |
| Phase 8 | PPU mode 寫錯導致畫面不對 | 跟 mGBA dump VRAM 對拍；headless 截圖好對比 |

---

## 里程碑時程（業餘 8–15h/週）

| 里程碑 | 內容 | 累計時間（原估） | 實際 |
|---|---|---|---|
| **M1** | Phase 0–1 完成 | ~1 月 | ✅ |
| **M2** | Phase 2 完成 | ~3 月 | ✅ |
| **M2.5** | Phase 2.5 完成（ARM7TDMI 完整 spec + parser） | ~4 月 | ✅ 比預期快 |
| **M3** | Phase 3 完成（host runtime + 直譯器） | ~7 月 | ✅ 比預期快 |
| **M3.4** | Phase 4 完成（jsmolka arm.gba + thumb.gba 100% 通過） | — | ✅ |
| **M3.5** | Phase 4.5 完成（GB 驗證 framework 通用性 + Blargg 11/11） | — | ✅ 完工 (2026-05-02) |
| **M4** | Phase 5 完成（GBA memory bus + IRQ + DMA + scheduler + apr-gba CLI） | — | ✅ 完工 (2026-05-02) |
| **M4.5** | Phase 5.5–5.7（BIOS LLE + 5 ARM bug fix + GB CLI 對齊） | — | ✅ 完工 (2026-05-02) |
| **M5** | Phase 8 完成（PPU 完整化：Mode 0/1/2 + OBJ + BLDCNT + Window） | — | ✅ 完工 (2026-05-02) |
| **M6 (可選)** | Phase 7 完成（block-JIT，效能優化） | — | — |
| **M7 (可選)** | GB scheduler / Mosaic / APU / 第三顆 CPU | — | — |

---

## 下一步：可選方向

GBA 端 MVP 全收完（2026-05-02）：BIOS LLE + jsmolka arm/thumb/bios
三 ROM 全 PASS + BIOS 開機 logo 視覺與 mGBA 同等。GB 端拿到 BIOS LLE
+ DMG Nintendo® logo 截圖。「換 CPU = 換 JSON」+「換 platform =
換 emitter + 換 PPU + 換 bus」兩個論點都端到端驗過。

按優先順序排，下一步可選：

1. **GB scheduler (per-scanline PPU clock)** — 解決 GB BIOS 跑太快
   問題（real ~2.5s，我們 ~0.1s）；架構照 `GbaScheduler` 抄
2. **Mode 5 + Mosaic** — PPU 剩下 corner case，影響極少 ROM
3. **Audio (APU)** — hand-written，跟 PPU 同套寫法
4. **Phase 7 block-JIT** — 把 GBA 4.4 MIPS 拉到 ≥ real-time（test ROM
   截圖不需要，但跑商業遊戲或加 GUI 必要）
5. **第三顆 CPU 移植** — MIPS R3000 / RISC-V RV32I 之類，加碼驗證
   framework 通用性
6. **AOT 預編譯 `.bc` 快取** — 避開 cold-start LLVM 編譯成本（~250ms）

完整收工筆記見：
- `MD/note/phase5-gba-mvp-complete-2026-05.md`（5.1–5.4）
- `MD/note/phase5.7-bios-lle-and-ppu-2026-05.md`（5.5–5.7 + Phase 8）
