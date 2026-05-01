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

### 第一目標 (MVP)

- ARM7TDMI (ARMv4T) — ARM 32-bit 與 Thumb 16-bit 雙模式
- 跑得動 GBA homebrew 與部分商業 ROM
- Framebuffer 等級的 PPU（VRAM Mode 3/4）— 僅用於驗證 CPU 正確性
- Windows 平台優先

### 明確排除（第一版不做）

- Master Clock cycle-accurate 模型（採 instruction-level catch-up）
- 完整 PPU 渲染（Tile mode 0/1/2、Sprite、Window、Mosaic、Affine）
- 跨主機 CPU 框架驗證（如 NES 6502、MIPS）— 框架預留設計，但不實作
- 音訊 (APU)
- 連線、紅外線、震動等周邊
- 跨平台 host（Linux/Mac、ARM64 host CPU）

### 後期延伸（pending，明確不在第一版）

- 完整 PPU + Sprite + 特效
- DMA / Timer / Interrupt 完整模擬
- 第二顆 CPU 驗證框架通用性
- AOT 預編譯快取（`.bc` 檔）

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
