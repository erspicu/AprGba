# AprGba / AprCpu — 專案總覽

## 一句話定義

透過 **JSON 描述 CPU 指令規格**，由 **C# Parser 自動轉譯為 LLVM IR**，再經 **LLVM JIT 編譯為原生機器碼**，建構一個資料驅動 (data-driven) 的通用 CPU 模擬框架，以 **GBA (ARM7TDMI)** 為首要驗證目標。

## 為什麼要做

傳統模擬器（mGBA、VBA）的 CPU 核心多以 C 寫死，每條指令一個 case。維護痛、難以橫向擴展到別的 CPU、效能優化全靠手工。

本專案的差異化：

- **資料驅動**：CPU 規格 = JSON，邏輯 = 一組通用 micro-ops。換 CPU 只要換 JSON
- **降維打擊**：N 種 micro-op 即可組合任意指令，O(1) 而非 O(N)
- **LLVM 後端**：免費換來 register allocation、SSA、DCE、向量化等工業級優化（前提是 LLVM 編譯成本可接受，見 `01-feasibility.md`）
- **.NET 圈空白**：目前缺少現代化、開發者友善的 CPU 模擬框架

## 範圍 (Scope)

### 第一目標 (MVP) ✅ 全部完成 (2026-05-02)

- ARM7TDMI (ARMv4T) — ARM 32-bit 與 Thumb 16-bit 雙模式 ✅
- 跑通 jsmolka GBA test ROM（arm.gba / thumb.gba / bios.gba）+ PNG 截圖
  做視覺驗證 ✅
- **真的帶 BIOS file 啟動**（LLE，跑完官方 BIOS intro → ROM entry），
  讓驗證更有公信力（不是 HLE shortcut） ✅ — 開機 logo 視覺與 mGBA 同等
- PPU（Mode 0/1/2/3/4 + 完整 OBJ + BLDCNT alpha/brighten/darken +
  WININ/WINOUT/OBJ Window）— **hand-written host code，不走 JSON spec**
  （理由見下方「明確排除」） ✅
- **headless CLI 模式**：`apr-gba --rom=X --bios=B
  [--cycles=N | --frames=N | --seconds=N] --screenshot=Y.png`，
  **不做 GUI / 60fps loop / 即時播放** ✅
- Windows 平台優先 ✅

完工狀態快照（截至 2026-05-09）：**455 unit tests 全綠**；arm/thumb/bios.gba
三 ROM 透過真 BIOS LLE 6 GBA-秒內全 PASS；GB 端拿到 BIOS LLE + DMG Nintendo®
logo 截圖；NES 端 nestest（三 backend）+ blargg cpu_test5（三 backend）全 PASS
（PC=$8003 self-loop, all 11 subtests）。完整收工筆記：
- [`MD/note/phase5-gba-mvp-complete-2026-05.md`](/MD/note/phase5-gba-mvp-complete-2026-05.md)（5.1–5.4）
- [`MD/note/phase5.7-bios-lle-and-ppu-2026-05.md`](/MD/note/phase5.7-bios-lle-and-ppu-2026-05.md)（5.5–5.7 + Phase 8）
- [`MD/note/loop100-bench-2026-05.md`](/MD/note/loop100-bench-2026-05.md)（**canonical performance baseline**
  — pre-Phase-7 對照基準，1200-frame loop100 ROM 量法）

### 第一目標的延伸：framework 通用性驗證 ✅ 完成 (2026-05-02)

- **GB LR35902 移植**（Phase 4.5）已完工 — 用 `erspicu/AprGBemu`（既有
  GB emulator）的 hand-written CPU 當 reference，把 GB CPU 寫成 JSON
  spec、跑通同一套 host runtime，**通過 Blargg cpu_instrs 11/11 + master
  "Passed all tests"**，跟 LegacyCpu 截圖完全一致。「換 CPU 只要換 JSON」
  這個核心承諾驗證完成。詳見
  [`MD/design/09-gb-lr35902-validation-plan.md`](/MD/design/09-gb-lr35902-validation-plan.md)、
  [`MD/design/10-lr35902-bit-pattern-groups.md`](/MD/design/10-lr35902-bit-pattern-groups.md)、
  [`MD/note/framework-emitter-architecture.md`](/MD/note/framework-emitter-architecture.md)。

### 第三顆 CPU 移植：Ricoh 2A03 / NES ✅ 完成 (2026-05-09)

- **NES 2A03 移植**（N0-N1 系列）已完工 — 第三顆 CPU 走同一套
  framework，nestest + blargg cpu_test5 三 backend (legacy / json
  per-instr / json-block) 全 PASS。詳見 [`MD/design/20-adding-a-new-cpu.md`](/MD/design/20-adding-a-new-cpu.md)。
- **Spec-driven runtime 深化**（N3-N9 系列）— 推 framework 宣告式比例從
  N2 的 ~50% 到 ~85%：interrupt vectors、memory bus dispatch、per-(mnem,
  addressing-mode) cycle table、access widths、dynamic cycle penalties
  全部 spec-driven。詳見 [`MD/design/21-spec-driven-runtime.md`](/MD/design/21-spec-driven-runtime.md)、
  [`MD/design/22-memory-spec-v2.md`](/MD/design/22-memory-spec-v2.md)、
  perf bench 在 `MD/performance/202605091900-n4-memory-spec-v2.md` +
  `MD/performance/202605092000-n33-full-spec-cycles.md`。
- **新增 framework 基礎建設**（N5/N7/N8/N10/N11）— 通用 lockstep diff
  toolkit（任何 CPU 實作 `ISteppableCpu` 即可互測）、fastmem query API
  (`TryGetHostPointer`)、ARM page-table dispatch (GbaMemoryBus 也 spec-driven)、
  allowed_widths debug-mode enforcement、fastmem block-JIT inline path
  (opt-in)。
- **JSON spec 驅動**：`spec/2a03/cpu.json` + 7 個 group + `spec/machines/nes-ntsc.json`，
  256 opcode 全部 spec-derivable 含 cycle 數。N3.3 BLOCKED → RESOLVED。

### 明確排除（第一版不做，2026-05 scope decisions）

- **音訊 (APU)** — 不做
- **Gamepad / KEYINPUT polling** — 不做（test ROM 不按按鍵）
- **GUI / 即時 60fps 視窗** — 純 headless CLI，跑完輸出 PNG
- **商業遊戲相容性 / 更多 homebrew** — 目標就是 jsmolka test ROM 視覺
  驗證，跑通即可，不追求其他 ROM
- **Master Clock cycle-accurate 模型**（採 instruction-level catch-up）
- ~~**第三顆 CPU**（MIPS、RISC-V、6502 等）— Phase 4.5 GB 驗證已涵蓋
  framework 通用性的關鍵未驗證面，再加更多 CPU 邊際效益遞減~~
  → **2026-05-09 update**：6502 / NES 2A03 還是做了（N0-N11），實際發現
  「再加 CPU」邊際效益遠高於原本估計 — 用同一個 framework 驅動第三顆
  ISA 的過程暴露了多個 spec format 缺口（per-(mnem, addressing-mode)
  cycle granularity、memory bus declarativity、page-table dispatch 通
  用化），補完之後 framework 宣告式比例從 ~50% 推到 ~85%。第 4 顆候選
  ：8086（segmented memory + 16-bit CISC）— 用以往寫過的 emulator 當
  reference oracle。
- **連線、紅外線、震動等周邊**
- **跨平台 host**（Linux/Mac、ARM64 host CPU）
- **PPU / APU / DMA 寫成 JSON spec** — framework 的「資料 vs 動詞」
  拆分只對 instruction stream 有意義；fixed-function units（PPU
  pipeline、APU 4-channel、DMA controller）沒有跨設備的 reusability，
  硬 JSON 化只是把 if-else 改成資料表，**沒有 framework 槓桿效益**。
  這些直接 host code 寫，學 GB 那邊的 `GbPpu` 寫法
- **Block-JIT 效能優化** — 從必要降為可選；test ROM 跑慢一點沒差
  （current GBA bench ~40% real-time，截圖夠用）

### 後期延伸（pending，明確不在第一版）

- 完整 PPU 進階特效（Window、Mosaic、Affine BG mode 1/2、Blend）
- DMA / Timer / Interrupt 完整模擬
- AOT 預編譯快取（`.bc` 檔，避開 cold-start LLVM 編譯成本）

## 命名

- 專案總體：**AprGba**
- CPU 框架核心：**AprCpu**
- CLI 工具：`aprcpu.exe`

## 關鍵設計信念

1. **LLVM 不是萬能銀彈**。它解掉 register allocation 與 codegen，但編譯時間是真實成本，必須面對。
2. **100% 純 JSON 是不切實際的目標**。少數 ARM 怪癖（Barrel Shifter 邊界、LDM/STM、不對齊讀取的 rotated read）會落到 C# handler。接受這個現實。
3. **階段性可交付** > 一次大爆炸。每個 phase 都要能獨立 demo。
4. **務實精度**：instruction-level catch-up 能搞定 95% GBA 遊戲，剩下 5% 用 IO 寫入觸發同步補強。不上 Master Clock。

## 目標讀者

- 自己（學習 CPU 模擬、JIT、編譯器後端）
- 想嘗試 .NET + LLVM 整合的開發者
- 模擬器社群（如能開源並穩定）

## 文件導覽

- `00-overview.md` — 本文件，專案總覽
- `01-feasibility.md` — 可行性分析（含風險）
- `02-architecture.md` — 系統架構與組件設計
- `03-roadmap.md` — 階段性實作路線圖
- `04-json-schema-spec.md` — CPU spec JSON schema 完整規範
- `05-microops-vocabulary.md` — micro-op 詞彙表
- `06-arm7tdmi-completion-plan.md` — Phase 2.5 計畫（已完成）
- `07-spec-authoring-conventions.md` — spec 寫作規範
- `08-portability-refactor.md` — Phase 2.6 通用化 refactor（已完成）
- `09-gb-lr35902-validation-plan.md` — Phase 4.5 GB CPU 移植驗證計畫 ✅
- `10-lr35902-bit-pattern-groups.md` — LR35902 bit-pattern 分群表（Phase 4.5C spec 結構）
- `11-emitter-library-refactor.md` — Phase 5.8 emitter library refactor 設計與進度
- `12-gb-block-jit-roadmap.md` — Phase 7 GB block-JIT 設計 + 進度
- `13-defer-microop.md` — `defer` micro-op 設計（延遲生效指令的通用 pattern）
- `14-irq-sync-fastslow.md` — `sync` micro-op + bus extern split
- `15-timing-and-framework-design.md` — Timing 準確 + 框架通用化的 synthesis
- `16-emulator-completeness.md` — emulator 完整性 vs framework genericity trade-off
- `17-aprcpu-vs-emulator-timing-boundary.md` — AprCpu 跟 emulator 的 timing 邊界劃分
- `18-block-jit-state-abstraction.md` — block-JIT state abstraction (alloca+mem2reg) 設計
- `19-declarative-jit-policy.md` — declarative JIT policy + CPU/Machine spec 拆分
- `20-adding-a-new-cpu.md` — **加一顆新 CPU 的 SOP**（含 ARM7TDMI / LR35902 / 2A03 三個對照範例）
- `21-spec-driven-runtime.md` — N3 spec-driven runtime（vector / bus / cycle table）+ N3.3 RESOLVED 紀錄
- `22-memory-spec-v2.md` — N4 memory spec v2（handler registry / page-table / offset semantics）

跨 phase 流程：
- [`MD/process/01-commit-qa-workflow.md`](/MD/process/01-commit-qa-workflow.md) — Tier 0-4 commit QA workflow（任何 commit 前依改動性質決定要跑哪一 tier）
