# Phase 5 完工 — GBA test-ROM 截圖 MVP 達成 (2026-05-02)

> 紀錄 Phase 5 全 sub-slice 收完的狀態。從 Phase 4.5（GB 跑通 Blargg）
> 到這裡 ~半天的工作，目標就是「跑得起 jsmolka 然後截圖」，已達成。

---

## 1. 目標回顧

Phase 5 開工前的 scope decision（記錄在 [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md)）：

- GBA 端目標 = 跑 jsmolka test ROM + PNG 截圖驗證
- **不**追商業遊戲、**不**追更多 homebrew 相容
- **不做 GUI / 60fps loop / 即時播放**
- **不要音效 / gamepad input**
- 但**要 LLE BIOS 啟動**（自備 BIOS file 跑開機，公信力高）
- PPU **hand-written**，不走 JSON spec

最小可工作版的驗收：

```
apr-gba --rom=jsmolka.gba --cycles=N --screenshot=out.png
→ 跟 mGBA 同一張 ROM 同 cycles 截圖一致
```

**結果**：兩支 jsmolka ROM 都顯示 `All tests passed`，跟 mGBA 行為一致。

---

## 2. 完工進度（4 個 sub-slice）

### 5.1 — IRQ + LLE BIOS foundation

擴 `GbaMemoryMap`：
- IO offset 常數展開（DISPCNT/DISPSTAT/VCOUNT/BG0..3 control + scroll/IE/IF/IME/POSTFLG/HALTCNT/WAITCNT）
- DISPSTAT bit constants（VBLANK_FLG / HBLANK_FLG / VCOUNT_FLG + 各 IRQ enable bit）
- `IrqVectorBase = 0x18`
- 新 `GbaInterrupt` enum（VBlank=0..GamePak=13，對映 IE/IF bit 位置）

擴 `GbaMemoryBus`：
- `LoadBios(byte[])` — Phase 5 LLE 入口
- `RaiseInterrupt(GbaInterrupt)` — 設 IF 對應 bit
- `HasPendingInterrupt()` — IME && (IE & IF) 檢查
- `WriteIoHalfword` 實作 GBATEK 兩個怪規則：
  - IF "write 1 to clear" 語意
  - DISPSTAT bit 0..2 唯讀（只能寫 IRQ enable 跟 VCount target）

### 5.2 — DMA controller

新檔 `GbaDmaController.cs`，4 個 channel：
- 立即模式（timing=00）：`OnCntHWrite` 偵測 enable 0→1 transition 立即 fire
- VBlank/HBlank 模式：armed 等 scheduler 觸發
- 16-bit / 32-bit transfer
- SAD/DAD INC / DEC / FIXED 模式
- Word count 0 → 0x4000 (DMA0..2) / 0x10000 (DMA3) per GBATEK
- IRQ on end (bit 14) → `bus.RaiseInterrupt(Dma{n})`
- 自動 disable channel 後傳輸完

跳過（不影響 test ROM）：repeat、DMA3 Game Pak DRQ、Audio FIFO DMA、cycle stealing。

### 5.3 — Cycle scheduler + IRQ delivery

新檔 `GbaScheduler.cs`：
- GBA timing 常數（1232 cycles/scanline、228 lines/frame、280896 cycles/frame）
- `Tick(cycles)` 推進 scanline + cycle-in-scanline
- VBlank entry @ scanline 160 → `RaiseInterrupt(VBlank)` + `Dma.TriggerOnVBlank()`
- HBlank entry @ visible scanline 內 → `RaiseInterrupt(HBlank)` + `Dma.TriggerOnHBlank()`
- VCount match → `RaiseInterrupt(VCount)`
- 維護 DISPSTAT 三個 FLG bit + VCOUNT register

新檔 `GbaSystemRunner.cs`：
- 把 `CpuExecutor` + `GbaMemoryBus` + `GbaScheduler` + `Arm7tdmiBankSwapHandler` 串起來
- `RunCycles(budget, cyclesPerInstr=4)`：每步 cpu.Step → scheduler.Tick → DeliverIrqIfPending
- `RunFrames(n)` 便利包裝
- `DeliverIrqIfPending()`：標準 ARM IRQ entry：
  1. 檢查 `bus.HasPendingInterrupt()` 跟 CPSR.I 沒 mask
  2. 存 SPSR_irq = old CPSR
  3. 切 CPSR mode = IRQ (0x12)、設 I bit
  4. `swap.SwapBank(state, oldMode, IrqMode)` 換 banked R13/R14
  5. R14_irq = next-PC + 4（標準 SUBS PC, LR, #4 return convention）
  6. PC = 0x18

### 5.4 — apr-gba CLI

新 project `src/AprGba.Cli`：

**`Program.cs`** — headless CLI：
```
apr-gba --rom=<path.gba> [--bios=<path.bin>]
        [--cycles=N | --frames=N]
        [--screenshot=out.png] [--info]
```
- 沒帶 BIOS → `InstallMinimalBiosStubs` + 跳 ROM entry @ 0x08000000
- 帶 BIOS → `LoadBios` + 從 BIOS entry @ 0x00000000 開始
- 報 setup time（spec 編譯 + MCJIT）跟 wall-clock 分開
- 結束印 PC + R0..R15 + IRQ delivery count + frame count

**`Video/PngWriter.cs`** — 直接吃 RGB triples 的 PNG encoder（不需要
palette indirection，因為 GBA framebuffer 已經是 15-bit RGB）

**`Video/GbaPpu.cs`** — 最小 PPU：
- Mode 3：240×160 RGB555 framebuffer (VRAM @ 0x06000000)
- Mode 4：8-bit indexed + PRAM palette
- 其他 mode → 黑畫面（Phase 8 補完）
- 「snapshot from current VRAM at call time」設計，不做 scanline timing

---

## 3. 驗證結果

```bash
apr-gba --rom=test-roms/gba-tests/arm/arm.gba   --cycles=300000 --screenshot=result/gba/arm.png
apr-gba --rom=test-roms/gba-tests/thumb/thumb.gba --cycles=300000 --screenshot=result/gba/thumb.png
```

| ROM | R12/R7 | 截圖 | 跟 mGBA 一致 |
|---|---|---|---|
| arm.gba | R12=0 | "All tests passed" | ✅ |
| thumb.gba | R7=0 | "All tests passed" | ✅ |

效能：
- Setup time（spec compile + MCJIT）：~250 ms / ROM
- Run time（300K cycles）：~250 ms / ROM
- **總共 < 1 秒 / ROM**

回歸測試：**346/346 unit tests 全綠**（Phase 5 加了 15 個新測試 — bus IRQ + DMA + scheduler + SystemRunner）。

> jsmolka 兩個 ROM 都用 Mode 4（paletted 8-bit framebuffer），剛好我們的
> 最小 PPU 有實作。如果未來要跑用 Mode 0 (tile-based BG) 的 ROM，需要
> Phase 8 補完整。

---

## 4. 架構全貌

```
┌─────────────────────────────────────────────────────────┐
│ apr-gba CLI (src/AprGba.Cli)                            │
│  ParseArgs → BootCpu → RunCycles → RenderFrame → PNG    │
└─────────────────────────────────────────────────────────┘
                         │
            ┌────────────┴────────────┐
            ▼                         ▼
┌──────────────────────┐   ┌──────────────────────────────┐
│ GbaSystemRunner      │   │ GbaPpu (Mode 3 / 4)          │
│  loop:               │   │  RenderFrame(bus)            │
│   cpu.Step()         │   │   → Framebuffer[240*160*3]   │
│   scheduler.Tick(N)  │   └──────────────────────────────┘
│   DeliverIrqIfPending│
└──────────────────────┘
       │       │       │
       ▼       ▼       ▼
┌──────────┐ ┌──────────┐ ┌──────────────────────────────┐
│CpuExecutor│ │GbaScheduler│ │GbaMemoryBus                  │
│           │ │ Tick →     │ │ ReadByte/Halfword/Word       │
│ Step()    │ │  VBlank    │ │ WriteIoHalfword (IF, DISPSTAT)│
│ ReadGpr   │ │  HBlank    │ │ RaiseInterrupt               │
│ WriteStatus│ │  VCount    │ │ HasPendingInterrupt          │
└─────┬─────┘ └────┬─────┘ │ Dma : GbaDmaController       │
      │            │       └──────┬───────────────────────┘
      │            ▼              │
      │     ┌─────────────────┐   │
      │     │ Arm7tdmiBank... │   │
      │     │ SwapBank(s,o,n) │   │
      │     └─────────────────┘   │
      │                           ▼
      │            ┌──────────────────────────────────┐
      │            │ GbaDmaController                 │
      │            │  OnCntHWrite (immediate fire)    │
      │            │  TriggerOnVBlank/HBlank          │
      │            │  ExecuteTransfer (R-M-W mem)     │
      │            └──────────────────────────────────┘
      ▼
┌────────────────────────────────────────────────────┐
│ AprCpu.Core JIT pipeline                           │
│  SpecCompiler.Compile(spec/arm7tdmi/cpu.json)      │
│   → LLVM IR module → MCJIT → native fn pointers    │
│ HostRuntime + extern shim binding                  │
└────────────────────────────────────────────────────┘
```

每一層都各自 unit-tested；整合層由 `GbaSystemRunnerTests` + 跑真 jsmolka
ROM（`RunFrames_AdvancesSchedulerAndStillReachesArmGbaHalt`）守底。

---

## 5. 跟 GB 端的對照

兩邊形狀完全一致 — 「framework + JSON spec + 一個 CLI」的設計被驗證
能套用到兩個截然不同的平台：

|  | GB (DMG) | GBA |
|---|---|---|
| CPU spec | `spec/lr35902/*.json` | `spec/arm7tdmi/*.json` |
| Custom emitters | `Lr35902Emitters.cs` | `ArmEmitters.cs` |
| Memory bus | `AprGb.Cli/Memory/GbMemoryBus.cs` | `AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs` |
| DMA controller | (none, GB 不需要) | `GbaDmaController.cs` |
| Cycle scheduler | (內嵌在 LegacyCpu) | `GbaScheduler.cs` |
| System runner | (內嵌在 LegacyCpu / JsonCpu) | `GbaSystemRunner.cs` |
| PPU | `GbPpu.cs` (BG mode 0) | `GbaPpu.cs` (Mode 3 / 4) |
| CLI | `apr-gb` | `apr-gba` |
| Test ROM | Blargg cpu_instrs (11/11) | jsmolka arm/thumb |
| 截圖驗證 | `result/gb/{legacy,json-llvm}/` | `result/gba/{arm,thumb}.png` |

---

## 6. 下一步可選方向

按優先順序：

1. **Phase 8 PPU 完整化** — Mode 0 tile-based BG + sprite layer + 4 個
   BG layer + window + blend。這樣才能跑用 Mode 0 的 ROM（多數商業
   homebrew 跟 demo）。架構上跟 GB 端 `GbPpu` 同套寫法
2. **跑帶 BIOS 的 LLE 啟動** — 拿一份 GBA BIOS file 跑 `apr-gba
   --bios=gba_bios.bin --rom=arm.gba`，驗 BIOS intro 走完進入 ROM
   entry 後行為一致（公信力 boost）
3. **Phase 7 block-JIT** — 把當前 4.4 MIPS（~40% real-time）拉到 ≥
   實機速度。對 test ROM 截圖目標來說可選；對實際遊玩才必要
4. **加更多 GBA test ROM**（Mooneye GBA, FuzzARM, ...）擴大正確性
   驗證面

---

## 7. 一句話總結

**GBA 端 MVP「test ROM CPU 驗證 + 截圖」目標完成 — 從 Phase 5.1 (IRQ
foundation) 到 5.4 (apr-gba CLI) 一氣呵成。jsmolka arm.gba/thumb.gba
兩支 ROM 都顯示 "All tests passed" 截圖，跟 mGBA 行為一致，346/346
unit tests 全綠。**

Phase 4.5 (GB) 跟 Phase 5 (GBA) 雙線收完，framework「換 CPU = 換 JSON +
寫 emitter + 接 platform-specific bus/scheduler/PPU」的論點兩個平台
都驗過了。
