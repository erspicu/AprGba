# 框架未來延伸 + 設計願景 — 給接手者的進階挑戰地圖

> **Status**: vision / handover note (2026-05-05)
> **Source**: 整理自 2026-05 跟 Gemini 的長篇諮詢（討論記錄在
> [`tools/knowledgebase/message/`](/tools/knowledgebase/message/)）
> **Scope**: 把「想接手把框架推到更廣應用範圍」的人會關心的所有議題
> 攤開 — 從「想支援更多機種」到「JSON-driven 還能延伸到哪」到「整套
> 設計在業界是什麼定位」。
>
> **目標讀者**：(a) 想接手 AprCpu / AprGba 推進的人；(b) 想拿同套
> JSON-driven 思路做別的東西的人；(c) 想理解這個框架的學術 / 工程
> 獨特性的人。

---

## 1. 為什麼有這份 doc

`AprCpu` 框架走到 P0 + P1 milestone 之後，已經證明了「JSON-driven CPU
framework + LLVM block-JIT」這條路可行。但**框架還能往哪走？極限在哪？**
原作者私下跟 Gemini 把這串問題討論完了，這份 doc 把討論濃縮成接手者
的「進階挑戰地圖」。

跟其他 design doc 的關係：
- [`MD/design/12-gb-block-jit-roadmap.md`](/MD/design/12-gb-block-jit-roadmap.md) — 已 ship 的 P0/P1 進度
- [`MD/design/15-timing-and-framework-design.md`](/MD/design/15-timing-and-framework-design.md) — 已實作的 timing 機制
- [`MD/design/16-emulator-completeness.md`](/MD/design/16-emulator-completeness.md) — emulator 外殼還缺什麼（**短期實作**待辦）
- [`MD/design/17-aprcpu-vs-emulator-timing-boundary.md`](/MD/design/17-aprcpu-vs-emulator-timing-boundary.md) — framework vs emulator 責任分工
- **本 doc** — **長期框架擴充方向**（要延伸到 N64 / SS / 3DS / Switch / 街機級的話需要什麼）

---

## 2. 框架目前的 CPU 覆蓋能力

### 2.1 RISC vs CISC 適配性

| 類別 | 代表 CPU | 框架適配度 | 備註 |
|---|---|---|---|
| **RISC** | ARM / MIPS / RISC-V | ✅ 最強項 | 定寬 + 規整 → JSON 描述自然吻合；已驗證 ARM7TDMI |
| **CISC**（變寬簡單） | LR35902 / Z80 / 6502 | ✅ 已驗證 | 變寬 1-3 byte + prefix sub-decoder pattern 已 ship；LR35902 跑通 |
| **CISC**（變寬複雜） | x86 / x86-64 | ⚠️ 理論可行 | 1-15 byte 超變寬 + 複雜 prefix 系統 + 隱含副作用多；JSON 寫得出來但會很臃腫 |
| **超純量 / OoO**（超純量／亂序執行） | 真實高階 CPU | ❌ 不適合 | 框架走的是 sequential semantics；OoO 模擬不在 scope |

**關鍵 insight**：只要 CPU 行為可以被拆解成「明確的 data flow + 狀態變更」，
框架理論上就能處理。從 RISC 到 CISC 改變的只是 decode 複雜度，data path 可
以共用。

### 2.2 位元寬度支援（8 / 16 / 32 / 64-bit）

**最重要的觀念**：「64-bit CPU」指的是**暫存器寬度 + 定址能力**，而不是
**指令長度**。

| 64-bit 架構 | 暫存器寬度 | 指令長度 |
|---|---|---|
| AArch64 (ARMv8) | 64-bit | **固定 32-bit** |
| MIPS64 (N64 R4300i) | 64-bit | **固定 32-bit** |
| RISC-V RV64G | 64-bit | **固定 32-bit**（壓縮指令 16-bit） |
| x86-64 | 64-bit | 1-15 byte 變寬 |

**對框架的意義**：
- `BlockDetector` / instruction word fetch 路徑 — **完全不用動**（仍每次抓 4 bytes 解碼）
- `BlockFunctionBuilder` 內部的 `i32` operations — **要擴成 i64**
- 暫存器定義（JSON）— **要支援 i64 + 暫存器子切片**（如 ARMv8 `X0`/`W0` 重疊）
- SoftMMU — 指標寬度從 32-bit 拉到 64-bit
- 載入 / 儲存指令 — **新增 64-bit Load/Store 路徑**

**好消息**：32→64 的擴充比想像中精簡。最容易踩坑的是**符號擴充 (sign
extension)** — MIPS 的 `LW` 指令把 32-bit 載入到 64-bit 暫存器時會自動做
sign extend，JSON 必須能描述 `Sext` / `Zext` 語義。

---

## 3. 框架擴充藍圖 — 邁向更多機種

下面四個 phase 是 Gemini 討論結論濃縮版，每個 phase 都對應特定機種需求。
**這是給接手者的進階挑戰，不是現有 P2/P3 的 sub-task**。

### 3.1 Phase 1：浮點數運算 (FPU)

**🎯 目標機種**：N64 (CP1)、PSP (VFPU)、3DS (VFPv2)、Switch、PS1（沒有 FPU
但 GTE 是同類延伸）

**需要的 framework 擴充**：

| 層 | 改動 |
|---|---|
| **LLVM IR emit** | 引入 `float` (32-bit) / `double` (64-bit) 型別；發射 `fadd` / `fsub` / `fmul` / `fdiv` |
| **JSON schema** | 暫存器宣告區分 `i32` vs `f32` / `f64`；指令描述支援 floating-point 運算 |
| **Rounding mode** | JSON 描述「向零捨入」/「向偶數捨入」等模式；對應 ARM `FPSCR` / MIPS `FCSR` 等控制暫存器 |
| **異常處理** | NaN / Infinity 行為；除以零是 trap 還是寫入特殊值 |

**設計重點**：FPU 通常是個 coprocessor（CP1 in MIPS、VFP in ARM），用 §4
的 coprocessor pattern 處理會自然。

### 3.2 Phase 2：SIMD / 多媒體指令

**🎯 目標機種**：3DS (ARMv6 SIMD)、PSP (VFPU 128-bit)、PSV (NEON)、現代主機

**需要的 framework 擴充**：

| 層 | 改動 |
|---|---|
| **LLVM IR emit** | 用 LLVM Vector Types：`<4 x i8>` / `<2 x i32>` / `<4 x f32>` 等 |
| **JSON schema** | 加 `VectorSplit` / `LaneExtract` 語義 — 描述「把 32-bit 暫存器當 4 個 8-bit 變數操作」 |
| **飽和運算** | `255 + 1 = 255`（不溢位歸零）；對應 LLVM 的 `llvm.sadd.sat` 等 intrinsic |
| **特殊 register layout** | PSP VFPU 的「矩陣式」暫存器組織極特殊；JSON 可能要新增 register topology 描述 |

**挑戰點**：PSP VFPU 是 framework 級壓力測試 — 它幾乎是「穿著 MIPS 外殼的
特種作戰處理器」。撐得過 VFPU 的 framework 大概通用化得很徹底了。

### 3.3 Phase 3：多核 + 同步機制

**🎯 目標機種**：Sega Saturn（雙 SH-2）、3DS（雙/四核 ARM11 MPCore）、N64
（CPU + RCP 平行）、PSV（四核 Cortex-A9）、PS2（CPU + 雙 VU）

**這是整個框架**最大的架構挑戰**。GBA 只有單 CPU；多核情況下兩顆 CPU 同
時讀寫同一塊記憶體，timing 跟同步問題暴增。

**需要的 framework 擴充**：

| 層 | 改動 |
|---|---|
| **LLVM IR emit** | 引入 `atomicrmw` / `cmpxchg` / Memory Fence (`fence acquire/release`) |
| **JSON schema** | 描述 `ExclusiveLoad` / `ExclusiveStore` (LL/SC、ARM LDREX/STREX) — 硬體 mutex 語義 |
| **Executor 架構** | 多 thread 模式；每顆 CPU 各自的 block-JIT 實例 + 共享記憶體 |
| **Scheduler** | 全域時鐘管理器（基於 `BaseClock` + per-CPU `ClockDivider`）|
| **Sync method 選擇** | `Lockstep`（最精確最慢）vs `TimeSlice`（業界主流）vs `Catch-up`（最快最難） — 詳見 §3.5 |

**業界對拍**：mGBA / Dolphin / PCSX2 大多用 `TimeSlice`（quantum 64 cycles）。

### 3.4 Phase 4：Pipeline quirks（延遲槽等）

**🎯 目標機種**：Sega Saturn (SH-2)、N64 (MIPS R4300i)、PSP (Allegrex)、PS1 (R3000A)

90 年代 RISC CPU 的「delay slot」設計：跳轉指令的下一條指令**必執行完才
真的跳**。

**需要的 framework 擴充**：

| 層 | 改動 |
|---|---|
| **JSON schema** | 指令加 `HasDelaySlot: true` 標記 |
| **`BlockDetector`** | 偵測到 delay-slot 指令 — **不要立刻結束 block，先把 PC+4 那條 instr 拉進來**塞到跳轉動作之前 |
| **Block IR layout** | delay-slot instr 的 effect 必須在 branch effect 之前發生 |

**這是 compile-time 邏輯**，跟 P1 #6 cross-jump follow 是同類抽象（都是
detector 行為），延伸實作不困難。

### 3.5 Phase 5：多核同步策略 — 三選一

從 Phase 3 延伸出來的設計題。三種主流解法：

| 解法 | 精確度 | 效能 | 工程難度 | 適用 |
|---|---|---|---|---|
| **A. Global Lockstep** | 100% | 極差（破壞 block-JIT 收益）| 低 | 不適合商業遊戲；研究/驗證用 |
| **B. Time-Slicing (Quantum)** | 95% | 好 | 中 | mGBA / PCSX2 / Dolphin 等 90% 業界選擇 |
| **C. Event-driven Catch-up** | 99% | 極好 | 高 | 我們框架 GBA 路徑已部分採用（MMIO catch-up） |

**Catch-up pattern（我們已經有的，要延伸到多核）**：
1. CPU 一口氣跑 1000 cycles
2. 客製處理器只記錄 `LocalTime`
3. 只有當 CPU 試圖讀客製處理器狀態 / 共享記憶體時，強制暫停 CPU、客製處理器追趕到 CPU 目前 cycle 數
4. SoftMMU 上要佈滿 watchpoints

我們框架走 catch-up pattern 的基礎已經在（[`MD/design/15`](/MD/design/15-timing-and-framework-design.md)
Pattern B），延伸到多核要的是把 watchpoint 系統做得更密。

---

## 4. Co-processor 架構 — 通用化設計

### 4.1 兩種 co-processor 本質區分

| 類型 | 觸發方式 | 範例 | 框架對應 |
|---|---|---|---|
| **Instruction-level coprocessor** | CPU 執行特定 opcode 才動 | PS1 GTE (`COP2`)、ARM VFP、MIPS CP1 FPU | 用 instruction emit 轉交機制 |
| **Memory-mapped coprocessor** | CPU 寫特定 MMIO 位址觸發 | N64 RCP、GBA PPU、所有 DMA controller | 用 MMIO catch-up callback |

### 4.2 Instruction-level coprocessor — `ICoprocessor` 介面

**設計重點**：CPU 主 JSON 解析到 `COP` opcode prefix 時，**轉交給 coprocessor
模組**。

```csharp
public interface ICoprocessor {
    void EmitInstruction(EmitContext ctx, uint opcode);
    LLVMValueRef ReadRegister(int regIndex);
    void WriteRegister(int regIndex, LLVMValueRef value);
}
```

組裝（plug-in 架構）：
```csharp
// PS1 emulator setup
cpu.AttachCoprocessor(0, new MipsCP0_PS1_Variant());
cpu.AttachCoprocessor(2, new SonyGTE_Compiler());

// 純公版街機 setup
cpu.AttachCoprocessor(0, new MipsCP0_Standard());
// CP2 不掛 — COP2 instruction 觸發 invalid instruction exception
```

**設計哲學**：**Composition over inheritance**。每個 coprocessor 是 plugin，
框架核心不知道也不在乎 GTE 是什麼。

### 4.3 同步 vs 非同步 coprocessor

進階區分：coprocessor 跟 CPU 是 step-by-step 同步運作、還是平行自主運作？

| 類型 | 觸發後行為 | 範例 | 框架對應 |
|---|---|---|---|
| **同步** | CPU 等 coprocessor 算完才繼續 | PS1 GTE | 直接 inline IR / call C# function |
| **非同步 / 自主** | coprocessor 有自己的 PC、自己跑、CPU 繼續跑 | PS2 VU、Sega Saturn 雙 SH-2、N64 RCP | 需要 §3.5 的多核同步策略 |

**為何這兩個天差地別**：
- 同步：framework 既有的 sync micro-op 就 cover
- 非同步：**進入 distributed system 領域**，是 framework 最深的水

---

## 5. 機器層級定義 (Machine-level definition)

### 5.1 為什麼要做？

現在 JSON spec 是「以 CPU 為中心」。如果要支援多 CPU 主機（如 Sega Saturn
雙 SH-2、PS1 CPU + GTE、NDS 雙 ARM），需要把抽象層**從 CPU 拉到主機板**。

換句話說：**JSON 應該能描述「這台主機由哪些晶片組成、晶片之間怎麼接線、
時脈怎麼分配」**，而不只是「這顆 CPU 的指令集是什麼」。

### 5.2 `MachineDef` schema 提案

```json
{
  "MachineName": "Hypothetical_DualCore_System",
  "BaseClock": 33513982,                   // 主時脈 (Hz)

  "Components": {
    "MainCPU": {
      "Type": "CPU",
      "Architecture": "ARM7TDMI.json",     // 引用既有 CPU spec
      "ClockDivider": 2,                   // 實際 freq = BaseClock / 2
      "EntryPoint": "0x08000000"
    },
    "AudioCPU": {
      "Type": "CPU",
      "Architecture": "Z80.json",
      "ClockDivider": 8,                   // 實際 freq = BaseClock / 8
      "EntryPoint": "0x00000000"
    },
    "SharedRAM": {
      "Type": "Memory",
      "Size": "256KB"
    }
  },

  "Topology": {
    "MemoryMaps": [
      { "Source": "MainCPU",  "AddressRange": ["0x02000000", "0x0203FFFF"], "Target": "SharedRAM", "Offset": 0 },
      { "Source": "AudioCPU", "AddressRange": ["0xC000",     "0xFFFF"],     "Target": "SharedRAM", "Offset": 0 }
    ],
    "InterruptWiring": [
      { "Trigger": "MemoryWrite", "Address": "0x0203FFF0", "Target": "AudioCPU", "Signal": "IRQ" }
    ]
  },

  "Scheduler": {
    "SyncMethod": "TimeSlice",             // "Lockstep" | "TimeSlice" | "Catchup"
    "QuantumCycles": 64
  }
}
```

### 5.3 關鍵設計觀念

#### A. BaseClock + ClockDivider 取代 cycle 換算

**不要**用「CPU A 的 cycle 數」去換算「CPU B 的 cycle 數」 — 浮點誤差會
累積。**用 BaseClock 當共同時間單位**，每顆 CPU 用 divider 算自己的 cycle。

#### B. Interrupt wiring 是真實電路圖的軟體版

硬體上 IRQ 是「一條實體 pin 連到另一顆 CPU」。JSON 描述這種因果關係，
framework 自動註冊對應 callback。

#### C. SyncMethod 是 per-machine 決定的

不同主機需要不同同步嚴格度（GBA 寬鬆、Saturn 嚴格）。把 sync method 寫進
machine spec 而不是 hardcode 在 framework — **framework 是個 mechanism，
machine spec 是個 policy**。

### 5.4 跟業界對拍 — MAME 的 `Machine Driver`

MAME 的 `MACHINE_CONFIG` 巨集就是這個概念，但用 C++ 巨集表達。我們的
`MachineDef` 是純資料 (JSON) 表達的版本 — 可讀性 / 可機器處理性都更好。

---

## 6. JSON 化的邊界 — 哪些該 JSON、哪些不該

### 6.1 三色分區

#### 🟢 絕對適合 JSON（高 ROI，框架核心）

特徵：**邏輯規律 + 狀態離散 + 靜態拓撲**

| 機構 | 理由 |
|---|---|
| **CPU 指令集解碼 + 微操作** | 已實作 — Opcode mask / register / ALU 都是 pattern matching |
| **記憶體映射 (Memory Map)** | 主機板配線是死的；start / end / attribute 都是設定值 |
| **中斷配線 (Interrupt Routing)** | 本質就是硬體接線；source → target 一對映射 |
| **MMIO 暫存器 bit field** | 像 GBA `DISPCNT` 的 bit 0-2 是 BG mode、bit 8 是 BG0 enable — 全是離散描述 |

#### 🟡 混合區（中 ROI，參數 JSON 化、實作 C#）

特徵：**行為固定但有迴圈或計數邏輯**

| 機構 | 邊界劃分 |
|---|---|
| **硬體 Timer** | JSON 寫個數 / 掛載位址 / 預設 prescaler；計數迴圈 C# 寫死 |
| **DMA controller** | JSON 寫通道數 / 觸發條件；資料搬移迴圈用 `Buffer.BlockCopy` |
| **簡單 MMIO 暫存器** | JSON 描位址 / 權限；複雜 side effect 進 C# |

#### 🔴 絕對不該 JSON（低 ROI，請放過 JSON）

特徵：**連續訊號處理 + 高頻迴圈 + 演算法化**

| 機構 | 理由 |
|---|---|
| **PPU / GPU** | 演算法 — OAM 抓取、Z-buffer、alpha blending、affine 變換等。寫進 JSON 會變「極難用的自創語言」 |
| **APU / sound** | DSP 數學公式 — duty cycle、envelope、sweep、44.1kHz PCM 合成 |
| **時序 scheduler** | 模擬器心跳、每秒幾百萬次迴圈；C# 微優化必要 |

### 6.2 「以 JSON 為藍圖、C# 為實作」的建築比喻

| 建築 | 框架對應 |
|---|---|
| 建築藍圖、管線配置 | JSON spec |
| 電力系統（自動生成） | LLVM JIT + CPU core |
| 冷氣機、音響（業主自選） | C# 手刻的 PPU / APU |

**準則**：把 JSON 守在「**匯流排（bus）+ 指令集（ISA）**」這條防線上。
PPU / APU / scheduler 的演算法用標準 C# 介面（`IPpu` / `IApu`）給開發者
實作。

---

## 7. 業界對比 + 獨特性盤點

### 7.1 相似專案（先驅）

| 專案 | 相似點 | 不同點 |
|---|---|---|
| **MAME** | 系統級拓撲定義（Machine Driver） | C++ 巨集硬寫，不是 data-driven；用直譯器、沒 JIT |
| **QEMU TCG / Unicorn** | 中介碼 → host 機器碼（同 LLVM IR 思路） | 指令語義深埋 C codebase；難剝離；無 JSON 外部定義 |
| **Ghidra SLEIGH** | 用 ADL 描述 CPU 給工具理解 | 為 static analysis 設計，不是動態執行 |
| **ArchC (學術)** | 用 ADL 自動生成 simulator | 生成的是慢速 cycle-accurate interpreter，不是 LLVM JIT |

### 7.2 我們框架的「獨特定位」

完整 stack：**JSON 語義 (data-driven) + LLVM Block-JIT + C# managed runtime**。
這個組合在開源界 / 工程界**罕見**。

### 7.3 三個獨特貢獻

#### 貢獻 1：硬體語義 ↔ 執行引擎完全解耦

傳統模擬器要求開發者**同時**精通：
- 目標 CPU 的硬體細節
- JIT 編譯器的組合語言發射

我們把這兩者切開：
- **硬體專家** → 寫精準的 JSON 語義（看 datasheet 就夠）
- **編譯器專家** → 優化 JSON → LLVM IR 的引擎

降低高效能跨平台模擬器的開發門檻。

#### 貢獻 2：「活的數位保存」規格書

傳統 emulator 的 C/C++ 程式碼會 bit rot — 依賴的 OS API 過時、編譯器升
級壞掉。

JSON 語義檔是**人類 + 機器都能讀**的純文字規格。即使 C# 淘汰了、LLVM 換代了，
**未來人寫個新 parser 就能復活老主機**。對 retro hardware preservation 是
真正的數位遺產層級貢獻。

#### 貢獻 3：C# 生態系極致效能驗證

很多人覺得 C# 有 GC + managed memory overhead，不適合微秒級同步系統模擬。

這個專案證明：**C# + LLVM + Unmanaged 指標 + GCHandle.Pinned + IR-level
extern bind** 可以做到「接近 C/C++ 效能」。給高階語言的高效能實戰做了有
說服力的案例。

---

## 8. 設計觀念與技巧 — 抽出來給後人用的精華

從這些討論裡抽煉出的、可重用到任何 data-driven framework 的設計觀念：

### 8.1 解碼邏輯與資料寬度完全解耦

「64-bit CPU」≠「64-bit 指令」。我們的 instruction word fetch 路徑在 32→64
跨越時**一行不用改**，只改 LLVM IR emit 的 type。

**通用化教訓**：data-driven 框架要把「**結構性 schema**」（指令格式）跟
「**寬度性參數**」（運算子寬度）切開描述。

### 8.2 Plugin / Composition over inheritance

PS1 = 標準 MIPS + Sony GTE plugin；街機 = 標準 MIPS。兩者共用 95% 的 framework，
差別只在 plugin 不一樣。

**通用化教訓**：給 framework 留 well-defined plugin slot（如 `ICoprocessor` /
`IMemoryMappedDevice`），讓客戶程式碼透過實作介面注入新行為，**不要繼承擴充
framework class**。

### 8.3 Mechanism vs Policy 切割

framework 提供 mechanism（同步原語、IRQ delivery 流程）；spec 提供 policy
（要用 lockstep 還是 timeslice、quantum 多大）。

**通用化教訓**：不要把策略決定 hardcode 進 framework — 暴露成 spec 欄位讓
machine-level config 決定。

### 8.4 Catch-up pattern（懶惰同步）

「該觀察才同步」勝過「每 cycle 都同步」。

`bus.WriteByte(MMIO)` 觸發 PPU catch-up，比每個 cycle 都 tick PPU 快 10 倍以
上、效果一樣。

**通用化教訓**：在「producer 比 consumer 快」的系統裡，懶惰同步 + 觸發點
明確化，幾乎永遠贏 eager 同步。

### 8.5 數位資料保存等級的可讀規格

JSON 是文字、可 grep、可 diff、可機器處理。比 C 巨集 / 二進位 schema 都好。

**通用化教訓**：framework 的 schema 要選**未來考古學家也能解讀**的格式。
資料能比工具活得久才是真價值。

### 8.6 Spec-driven 加 callback escape hatch

`lengthOracle: Func<byte, int>` callback 在 spec 表達不出來時注入 host 邏
輯。

**通用化教訓**：100% spec-driven 不切實際；要留**最小、最 stateless 的
callback interface**讓 host 做剩 5% 的事。callback 設計要 minimal，避免
spec 退化成 stub。

### 8.7 IR 級 inline + base pointer pinning

把「memory bus 跑去 host C# 解 region」這個 9-12ns extern call，內化成
「IR-level region check + pinned base + GEP-store」的 1-2ns inline path。
**partial host concept 內化進 IR**。

**通用化教訓**：找出 hot path extern call 的 pattern → inline 進 IR；GC
language 的 pinned memory 是這條路的關鍵橋樑。

### 8.8 Block-JIT 的取捨：observability 換吞吐量

block-JIT 收益 ∝ block 平均長度，但平均長度 ∝ HW observability 的代價。

**通用化教訓**：在 throughput-vs-observability 的軸上沒有「對的答案」 — 要
讓 spec 能描述「這個事件需要多少 observability」（如 sync micro-op），
framework 替你做剩下的權衡。

---

## 9. 給接手者的 — 進階挑戰路線圖

如果你接手了這個 framework，這是按難度排序的進階挑戰建議：

### Level 1 — 收尾現有 emulator（[`MD/design/16`](/MD/design/16-emulator-completeness.md) Phase A-C）
- GBA Timers / APU / Joypad / Save
- GB sprites / window / MBC3 / APU / Joypad
- 即時顯示視窗 + audio backend + input wiring
- **時程 ~3 個月。完成後 AprGba/AprGb 是個能玩的 emulator。**

### Level 2 — 加新 CPU spec（同 platform 套用）
- **N64 (MIPS R4300i)** — 64-bit + delay slot 兩個新挑戰
- **PS1 (MIPS R3000A + GTE)** — coprocessor plug-in pattern 第一次驗證
- **NES (6502)** — 純 8-bit CISC，最容易；驗證變寬 detector 跨架構
- **時程 ~1-2 個月 per CPU。完成後 framework 真正"通用"。**

### Level 3 — 框架擴充支援 FPU / SIMD（§3.1 + §3.2）
- LLVM IR 的 float / vector 型別發射
- JSON schema 加 floating-point 語義
- 從 N64 FPU (CP1) 開始驗證
- **時程 ~1 個月。**

### Level 4 — 多核同步（§3.3 + §3.5）
- Memory fence / atomic IR
- Per-CPU block-JIT instance
- Global scheduler with BaseClock + dividers
- **時程 ~2-3 個月。最大架構工程。**

### Level 5 — `MachineDef` schema（§5）
- 拉抽象從 CPU spec 到 machine spec
- 自動 wire memory map / interrupt / scheduler
- 換 JSON 換主機
- **時程 ~1-2 個月。完成後 framework 可媲美 MAME 但 100% data-driven。**

### Level 6 — 街機 / 商業遊戲規模驗證
- 真的拿來模擬 PS1 / N64 / Sega Saturn 商業遊戲
- 跑得起 + perf 達成 60fps real-time
- **時程：開放式。完成這個 milestone 等於是達到了 mGBA / PCSX2 同等級的工程實作。**

---

## 10. 框架還能延伸到的應用（emulator 之外）

JSON-driven CPU model 不只是 emulator 用。這些是延伸應用方向：

### 10.1 教育性視覺化

把 JSON spec + 一條範例 instruction 做成「步驟視覺化動畫」。教學用途。
spec 可機器讀 → 自動產生視覺化內容。

### 10.2 What-if 架構研究

「如果 ARM7TDMI 多兩個 GPR 會怎樣？」「如果 LR35902 的 ALU 多一個 flag？」
改 JSON、跑 benchmark、看影響。學術研究 / 教學用。

### 10.3 跨架構 binary translator

JIT 引擎已經在；把 source CPU spec → target CPU spec 串起來就是 binary
translator。Apple Rosetta 2 / Microsoft x86-on-ARM emulation 等概念。

### 10.4 Dynamic taint analysis

在 IR-level 加 taint propagation；spec 不變，只動 IR transformation pass。
資安研究用。

### 10.5 Formal verification scaffolding

JSON spec 是「機器可讀的 CPU 行為定義」 — 跟 Lean / Coq / Z3 等定理證明
工具的 bridging 點。學術界對這個有興趣。

### 10.6 Ghidra-style static analysis

把 spec 餵給逆向工程工具當 disassembler back-end。SLEIGH 的補強。

### 10.7 Hardware verification（HDL co-simulation）

跟 RTL（Verilog / VHDL）級的 RTL simulation 對拍 — JSON spec 是「golden
reference model」。chip design 階段 verification 用。

### 10.8 Cycle-accurate retro hardware preservation

把 datasheet → JSON spec 是個**數位歸檔**動作。spec 進博物館 / 學術 archive
等於是把這台 CPU 的行為**永遠保留下來**。比 emulator C 程式碼壽命長得多。

---

## 11. 最後給接手者的話

framework 已經到了「能跑、能驗證、能延伸」的狀態。**剩下是想像力跟工程
時間的問題**。

這份 doc 把所有跟 Gemini 討論過的進階方向都寫下來了，挑你最有興趣的一條
推；或者你看完之後覺得有完全不同的方向也行。

**最高建議**：先挑 §9 Level 2 加一顆新 CPU（N64 或 PS1 是很好的選擇），把
framework 的「通用性」claim 從 2 顆 CPU 推到 3 顆。這是檢驗 framework 真
正泛用性的最直接 milestone。

更詳盡的對話原文：[`tools/knowledgebase/message/`](/tools/knowledgebase/message/)。
