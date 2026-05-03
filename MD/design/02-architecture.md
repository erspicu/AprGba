# 系統架構

## 高層架構圖

```
┌──────────────────────────────────────────────────────────┐
│                    AprGba 模擬器主程式                      │
├──────────────────────────────────────────────────────────┤
│  GUI / Display  │  Input  │  ROM Loader  │  Debugger      │
│  (Avalonia)     │         │              │                │
├──────────────────────────────────────────────────────────┤
│              Memory Bus (memory map dispatch)              │
│  ┌──────┬──────┬──────┬──────┬──────┬──────┬──────────┐    │
│  │BIOS  │EWRAM │IWRAM │IO Reg│PRAM  │VRAM  │GamePak ROM│    │
│  └──────┴──────┴──────┴──────┴──────┴──────┴──────────┘    │
├──────────────────────────────────────────────────────────┤
│               AprCpu 核心 (CPU 框架)                      │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐      │
│  │ Code Cache   │  │ Block         │  │ Register File│      │
│  │ (PC → fnPtr) │  │ Compiler     │  │ R0–R15, CPSR │      │
│  └──────┬───────┘  └──────┬────────┘  └──────────────┘      │
│         │                  │                                 │
│  ┌──────▼──────────────────▼─────────────────────────────┐  │
│  │ JSON Parser + LLVM IR Emitter (LLVMSharp)             │  │
│  └──────────────────────┬────────────────────────────────┘  │
└─────────────────────────┼───────────────────────────────────┘
                          │
                   ┌──────▼──────┐
                   │ JSON Spec   │
                   │ arm.json    │
                   │ thumb.json  │
                   └─────────────┘
```

## 組件職責

### 1. JSON 規格（`spec/arm7tdmi/*.json`）
- 定義指令編碼格式（encoding format）：bit pattern、mask/match
- 定義語義（micro-op steps 序列）
- 定義 cycle count 提示
- **不含執行邏輯**，只是「說明書」
- 是純資料，可被多種後端讀取（IR emitter、文件產生器、靜態分析）

### 2. JSON Parser + IR Emitter（`AprCpu.Core/IR`）
- 讀 JSON
- 解析 bit pattern → mask/match 配對表
- 為每個 micro-op 呼叫 LLVMSharp API emit IR
- 輸出 `.ll`（除錯）或記憶體中的 `LLVMModuleRef`（執行）

### 3. Block Compiler（`AprCpu.Core/IR/BlockFunctionBuilder.cs` +
   `AprCpu.Core/Runtime/BlockDetector.cs`）
- 從給定 PC 開始 fetch、decode、累積指令（detector 上限 64 instr）
- 遇到 `writes_pc:"always"` / `switches_instruction_set` / `changes_mode`
  即結束 block
- 將整個 block 的所有指令 IR 串成一個 LLVM Function（per-instr 4 個內部
  BB：pre/exec/post/advance + 共用 `block_exit`）
- 呼叫 ORC LLJIT (`HostRuntime.AddModule`)，得到原生函式指標

### 4. Code Cache（`AprCpu.Core/Runtime/BlockCache.cs`）
- `Dictionary<uint, LinkedListNode<Entry>>` + LRU 雙向鏈，capacity-bound
  (default 4096)
- 命中即跳轉執行（`CpuExecutor.StepBlock`）
- SMC 偵測：寫入「已編譯區域」時 invalidate（A.5 未實作，pending）
- 進階：block linking（patch native call 直接跳下一個 block）（A.7 未實作）

### 5. Register File / CPU State（`AprCpu.Core/IR/CpuStateLayout`）
- **由 spec 動態建構**：layout 不寫死成 ARM 形狀；`CpuStateLayout` 讀取
  `RegisterFile` + `ProcessorModes` 動態組出 LLVM struct
- 結構：`[GPRs] + [status registers + per-mode banked status slots] +
  [per-mode banked GPR groups] + [cycle_counter i64, pending_exceptions i32]`
- 同一份 framework code 可同時為 ARM7TDMI（GPR×16 + CPSR + 5×SPSR + 5
  banked groups）與 LR35902（GPR×7 8-bit + F flag）產生對應的 layout
- C# host 端的 `CpuState` 鏡像此 layout（Phase 3 工作項），透過 `unsafe`
  指標傳給 JIT 機器碼直接存取，避免 marshal

### 6. Memory Bus（`AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs`，
   實作 `AprCpu.Core/Runtime/IMemoryBus.cs`）
- `IMemoryBus` 介面，CPU JIT 透過 trampoline 呼叫
- **Fast Path**：RAM 區段直接 unsafe ptr 讀寫（IWRAM/EWRAM/VRAM bulk），
  GBA bus 用 typed cast (`bus as GbaMemoryBus`) + cart-ROM range check
  bypass interface dispatch
- **Slow Path**：IO 暫存器走 callback 分派 + IRQ 觸發
- 預留 SMC write barrier hook
- 預留 IO 寫入觸發同步點 hook（給 PPU 追趕用，Phase 1a/1b downcounting
  + MMIO catch-up callback 已實作）

### 7. PPU（`AprGba.Cli/Video/`，hand-written，不走 JSON spec）
- LCD 暫存器：DISPCNT、DISPSTAT、VCOUNT
- VBlank / HBlank 中斷觸發
- Mode 3（240×160 RGB555 framebuffer）— 最簡單，部分 homebrew 用
- Mode 0（4 個 tile-based BG layer）— jsmolka test ROM 印結果用
- Sprite / Window / Mosaic / Affine BG / Blend：jsmolka 用不到就不做
- **不走 JSON spec**（2026-05 scope decision）：PPU 不是 instruction
  stream，是 fixed-function pipeline，硬 JSON 化沒有 framework 槓桿；
  學 GB 端 `GbPpu` 寫法直接 host code 即可

### 8. CLI / Host（`AprGba.Cli`，單一專案，純 headless）
- **不做 GUI、不做 60fps loop、不做即時播放**（2026-05 scope decision）
- 包含 `Program.cs` (entry)、`Video/` (PPU host code)、`RomPatcher.cs`
  (Nintendo logo / header checksum 補丁) — bus / scheduler / system
  runner 都在 `AprCpu.Core/Runtime/Gba/` 下，CLI 只負責 wiring
- 學 `apr-gb` 的設計：`apr-gba --rom=X.gba --bios=Y.bin --cycles=N
  --screenshot=Z.png [--block-jit]`
- 跑完 N cycles 後呼叫 `GbaPpu.RenderFrame(bus)` → PNG 寫檔
- 可選 `--info` 印 ROM header / cartridge type，跟 mGBA dump 對拍
  PRAM/VRAM 用

---

## 執行模型

### Phase 3-6：純直譯模式（無 JIT）

```
loop:
  ins = MemoryBus.Read32(PC)              # 或 Read16 看模式
  format = Decoder.Match(ins)
  if not CheckCondition(ins, CPSR):
    PC += step; continue
  Execute(format, ins)                     # 透過 emit 出來的 LLVM 函式或解譯
  PC += instruction_size
  cycle_count += instruction_cycles
  if cycle_count >= scanline_cycles:
    PPU.tick_scanline()
    HandleInterrupts()
```
無 cache、無 block。用於 bring-up 與正確性驗證。

### Phase 7：Block JIT 模式

```
loop:
  block = CodeCache.Lookup(PC)
  if block == null:
    block = BlockCompiler.Compile(PC)      # JSON → IR → JIT
    CodeCache.Store(PC, block)
  cycles = block.Execute(state*, bus*)     # native call
  cycle_count += cycles
  PPU.CatchUp(cycle_count)
  HandleInterrupts()
```

### 同步策略

- 預設 **instruction-level catch-up**
- 每個 block 結束才同步 PPU / Timer / DMA
- **強制同步點**：寫入 IO 暫存器（`0x04000000–0x040003FE` 區段）→ block 立即結束
- **不做 Master Clock**

---

## 資料流

### 啟動流程

1. 載入 `arm.json`、`thumb.json`
2. Parser 解析 JSON，建構 encoding format 表（mask/match 排序）
3. （可選）預先 emit 共用的 micro-op handler 函式
4. 載入 GBA BIOS 與 ROM 到 Memory Bus
5. 設 PC = 0x00000000（BIOS entry point）或 0x08000000（ROM）
6. 進入主迴圈

### Block 編譯流程（Phase 7）

```
1. PC=X，code cache miss
2. Decoder 從 X 開始：
   a. Fetch 指令 (依模式 16/32 bit)
   b. 找符合的 encoding format（mask/match 比對）
   c. Field extraction
   d. 將 micro-op steps 逐一 emit 為 LLVM IR
   e. Emit cycle 累加
   f. 判斷是否為 block terminator (B/BL/BX/MOV PC, etc.)
3. Block 結尾 emit return + 最終 PC
4. 呼叫 LLVM JIT 編譯整個 LLVMFunction
5. 取得函式指標寫入 code cache
```

---

## 介面契約

### `IMemoryBus`

```csharp
public interface IMemoryBus
{
    byte   Read8 (uint addr);
    ushort Read16(uint addr);
    uint   Read32(uint addr);
    void   Write8 (uint addr, byte v);
    void   Write16(uint addr, ushort v);
    void   Write32(uint addr, uint v);

    int GetCyclesForAccess(uint addr, AccessSize size, AccessKind kind);
    bool IsCodeRegion(uint addr);  // 給 SMC barrier 用
}
```

### `CpuState`（傳給 JIT 的 context — **由 spec 決定 layout**）

`CpuStateLayout` 從 spec 動態組出 LLVM struct；C# host 端用對應的
`StructLayout(LayoutKind.Sequential)` mirror。**以下只是 ARM7TDMI 範例**，
換 spec（如 LR35902）會產出不同的欄位序列。

ARM7TDMI 的 layout 大致長這樣：

```csharp
// 由 CpuStateLayout 在執行期決定 — 不是硬編碼
[StructLayout(LayoutKind.Sequential)]
public unsafe struct CpuState_Arm7tdmi
{
    public fixed uint R[16];           // 通用暫存器
    public uint CPSR;
    public uint SPSR_fiq, SPSR_irq, SPSR_svc, SPSR_abt, SPSR_und;
    public fixed uint R_fiq[7];        // R8–R14 banked
    public fixed uint R_irq[2];        // R13–R14 banked
    public fixed uint R_svc[2];
    public fixed uint R_abt[2];
    public fixed uint R_und[2];
    public ulong CycleCounter;
    public uint   PendingExceptions;   // bitmask
}
```

LR35902 會是完全不同的 layout（A/B/C/D/E/H/L/F 都 8-bit、SP/PC 16-bit、
無 banked、無 SPSR）。framework code 不變，只 spec 不同。

### JIT 函式簽章

Per-instruction (Phase 7.A.6 之前的 dispatcher 路徑，per-instr mode 仍使用)：
```
void Execute_<Set>_<Format>_<Mnemonic>(CpuState* state, uint32_t instruction_word);
// 寫入 state、回傳 void。caller 從 state.PcWritten / state.GPR[pc_index]
// 推算下一條 PC。
```

Block (Phase 7.A.6+，`--block-jit` flag 啟用)：
```
void ExecuteBlock_<Set>_pc<XXXXXXXX>(CpuState* state);
// instruction word 都已 baked into IR (block compile 時從 bus 讀完)。
// 函式內每條指令是一個 instr_pre/exec/post/advance BB sequence；
// block 結束 fall through 到共用的 block_exit BB → ret void。
// caller (CpuExecutor.StepBlock) 從 state.PcWritten 判斷是否跳轉、
// 從 state.CyclesLeft delta 算實際消耗 cycles (Phase 1a downcounting)。
```

---

## JSON Schema

完整 schema 規範見 `04-json-schema-spec.md`；micro-op 詞彙表見
`05-microops-vocabulary.md`；實際 spec 範例見 `spec/arm7tdmi/`。

---

## 目錄結構（實際）

```
AprGba/
├── MD/
│   ├── design/                  ← 設計文件（本目錄）
│   ├── note/                    ← 收工筆記
│   ├── performance/             ← bench 紀錄（每個策略一檔）
│   └── process/                 ← 跨 phase 流程（QA workflow 等）
├── src/
│   ├── AprCpu.Core/             ← CPU 框架核心（spec → IR → JIT 都在這）
│   │   ├── JsonSpec/              ← JSON loader & schema
│   │   ├── Decoder/               ← bit pattern matching + DecoderTable
│   │   ├── IR/                    ← LLVMSharp emitter (Emitters/ArmEmitters/
│   │   │                            Lr35902Emitters/StackOps/FlagOps/BitOps/
│   │   │                            BlockFunctionBuilder/InstructionFunctionBuilder)
│   │   └── Runtime/               ← HostRuntime (ORC LLJIT) + CpuExecutor +
│   │       │                        BlockCache + BlockDetector
│   │       └── Gba/                 ← GBA-specific bus / scheduler / system
│   │                                  runner (GbaMemoryBus / GbaScheduler /
│   │                                  GbaSystemRunner)
│   ├── AprCpu.Compiler/         ← CLI: `aprcpu --spec X.json --output Y.ll`
│   ├── AprCpu.Tests/            ← xUnit (360 tests)
│   ├── AprGba.Cli/              ← `apr-gba` GBA harness (Program/Video/RomPatcher)
│   └── AprGb.Cli/               ← `apr-gb` Game Boy harness (legacy + json-llvm)
├── spec/
│   ├── arm7tdmi/                ← cpu.json + arm/thumb sub-specs
│   └── lr35902/                 ← cpu.json + 25 group files (block0/1/2/3 + cb-*)
├── test-roms/                   ← gba-tests/ + gb-tests/
├── BIOS/                        ← gba_bios.bin (LLE)
├── ref/                         ← vendor manuals + datasheets (gitignored 大檔)
└── temp/                        ← 本地 scratch (gitignored)
```

---

## 關鍵設計決策

| 決策 | 選擇 | 原因 |
|---|---|---|
| 描述語言 | JSON | 親切、工具齊全；後期可考慮 YAML 或自訂 DSL |
| JIT 後端 | LLVM (via LLVMSharp) | 工業級優化；後備 = .NET DynamicMethod |
| Host 形式 | **純 headless CLI**（不做 GUI） | scope = test ROM 截圖驗證，不需要即時播放 |
| 同步模型 | Instruction-level catch-up | 平衡精度與效能；強制同步 IO 寫入 |
| Memory bus | callback + fast/slow path | 解耦、可攔截 |
| CPU State | byte buffer + 直接指標 | 避免 marshal 開銷；可被 host runtime 動態 layout |
| BIOS 載入 | **LLE（自備 BIOS file）** | 跑完官方 BIOS intro → ROM entry，比 HLE 更有公信力 |
| PPU 範圍 | Mode 3 + Mode 0，hand-written | jsmolka 印結果用得到的最小 mode 集 |
| PPU 不走 JSON spec | hand-written `GbaPpu` | fixed-function pipeline 沒有跨設備 reusability |
| Block-JIT (Phase 7) | 可選優化 | test ROM 不需要 real-time，慢一點沒差 |
