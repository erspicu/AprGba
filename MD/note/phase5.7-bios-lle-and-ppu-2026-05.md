# Phase 5.5–5.7 完工 — BIOS LLE + PPU 完整化 + GB CLI 對齊 (2026-05-02)

> 接續 `phase5-gba-mvp-complete-2026-05.md`（Phase 5.1–5.4 完工筆記）。
> 這次再做 5 個 sub-slice：5.5 DISPSTAT/HALTCNT、5.6 cart 補 patch、
> 5.7 BIOS LLE + PPU + GB CLI 對齊。最終結果：**真 Nintendo 開機 logo
> 視覺與 mGBA 同等**，跨 GB + GBA 兩套 CLI 操作介面一致。

---

## 1. Phase 5.5 — DISPSTAT toggle 移除 + HALTCNT

Phase 4.x 留下的兩個 hack 影響 LLE 啟動：

### DISPSTAT VBLANK_FLG toggle hack（拔掉）

舊版 `GbaMemoryBus` 在 `ReadIoHalfword(0x04)` 時把 VBLANK_FLG bit toggle
一下，讓 jsmolka 的 `m_vsync` 不會死等。但 LLE BIOS 啟動後，scheduler
會自己 maintain VBLANK_FLG / HBLANK_FLG / VCOUNT_FLG bit，toggle 反而
覆蓋掉 BIOS 寫的 IRQ enable bits → 沒 VBlank IRQ 送出。**移除 toggle，
全交給 scheduler 維護**。

### HALTCNT (0xFF, 0x301)

BIOS 用 `SWI 02h` 走到 HALTCNT 寫 → CPU 應該停到 IE & IF != 0 才喚醒。
舊版直接忽略寫入 → CPU 繼續跑，吃 cycle 浪費掉。

新加 `bus.CpuHalted` flag + `GbaSystemRunner` 內 halt-aware 迴圈：
halted 時只 tick scheduler，IE & IF 變 non-zero 自動清 flag 喚醒。

---

## 2. Phase 5.6 — Cart Nintendo logo + header checksum patcher

真 BIOS 啟動時驗證 cart 的 Nintendo logo（0x004..0x09F, 156 bytes）跟
header checksum (0x0BD)。jsmolka / homebrew 多半 logo bytes 是隨機，
直接跑 LLE 會卡在 BIOS 永遠不出來。

新檔 `RomPatcher.cs`：
- `ExtractLogoFromBios()`：從 BIOS image 找 6-byte prefix `24 FF AE 51 69 9A`
  接後 156 bytes 作為 canonical logo
- `EnsureValidLogoAndChecksum()`：把 cart 的 logo bytes + 0xBD checksum
  in-place 改成符合 BIOS 期望

Idempotent（相同 cart + BIOS 多跑只 patch 第一次）。

---

## 3. Phase 5.7 — BIOS LLE 啟動 + 5 連環 ARM7TDMI bug

GBA BIOS 啟動 hang 在 0x19BC..0x1B0E loop 區。透過 `--trace-bios` flag
（每 100 個 BIOS-region instruction sample state）+ Gemini 規格諮詢，
找到 root cause：

### Bug 1: Thumb BCond +0 idiom 被當 no-op

`CpuExecutor.Step()` 用「post R15 vs pre-set R15 比對」偵測 branch。
當 branch target 剛好 == pre-set R15 (= pc + PcOffsetBytes)，detector
誤判為「沒分支」→ 強塞 PC = pc + InstrSize → 跳過 compiler idiom 的
back-edge，無限迴圈。

修：加 `PcWritten` byte flag 到 state struct；`Branch` / `BranchIndirectArm`
/ `LDM-with-R15` / `WriteReg(literal=15)` emitter 寫 PC 後 set 1，
executor 讀 flag 取代/補強值比對。

### Bug 2: ARM LSL/LSR by 32 carry flag

ARM ARM A5.1.7：count == 32 時 C = Rm[0] (LSL) 或 Rm[31] (LSR)，只有
count > 32 才 C = 0。我們的 `EmitLslShiftByReg` 註解直接寫「approximated
as 0」。

修：加上 `count == 32` 的 special case 取 rm[0] / rm[31]。

### Bug 3: ARM shift-by-register PC 讀錯偏移

ARM ARM A5.1.5：shift-by-register form 的 Rm/Rn 若是 PC，讀 `address + 12`
而不是普通的 `+ 8`（多 1 cycle pipeline）。我們 hardcoded `+ 8`。

修：加 `read_reg_shift_by_reg` op 給 Rn read，shift-by-reg resolver 內
為 Rm 加 `+ 4 if PC` 邏輯；DataProcessing_RegRegShift 14 個 spec 指令改用新 op。

### Bug 4: UMULLS / SMULLS / UMLALS / SMLALS 沒寫 N/Z flag

Multiply-long S-bit form 應該設 N = bit 63、Z = (result == 0)。我們 spec
完全沒 flag-update step。

修：加 `update_nz_64` op；spec 4 個 multiply-long mnemonic 末尾加 S-bit
條件 + nz_64 update。

### Bug 5: Exception entry 沒清 CPSR.T

ARM ARM B1.8.5：所有 exception entry (Reset/SWI/IRQ/...) 強制 ARM mode
（清 T bit），因為 vector table 0x00..0x1C 是 ARM code。我們保留原 T
→ Thumb 模式下進 IRQ vector 還用 Thumb 解 0x18 → undefined instruction。

修：`raise_exception` emitter + `GbaSystemRunner.DeliverIrqIfPending`
都 mask off CPSR.T 作為 mode swap 的一部分。

### 為什麼 5 個 bug 集中在這時浮出？

**jsmolka 的 fail-label 用相對 offset。如果 fail label 剛好在 +0
編碼位置，舊的 branch detector bug 把 BCond +0 當 fall-through →
跳過真正的 fail jump → 後續 4 個 ARM 指令 bug 全被「失敗 test 沒
被執行 m_exit」靜默吞掉 → R12 顯示 0 (passed)**。

修了 detector 後，4 個 CPU bug 全浮出來；接著一個個修完才 PASS。
五個 bug 一個 detector bug 掩蓋。

驗證：6 GBA-秒內透過真 BIOS LLE 跑通 jsmolka arm.gba/thumb.gba/bios.gba
全部 → "All tests passed"，跟 mGBA 同 timing 級別。

---

## 4. BIOS open-bus protection

GBATEK：BIOS 區 (0x00000000..0x00003FFF) 讀取，當 PC 在 BIOS 內回真值；
PC 在外回 sticky last-fetched-opcode。Sticky 來自 ARM7TDMI 3-stage
pipeline — execute PC=X 時 fetch 已到 PC + 2×instr_size。

實作：
- `IMemoryBus.NotifyExecutingPc(pc)` (default no-op) — 在 fetch 之前 call
- `IMemoryBus.NotifyInstructionFetch(pc, word, instr_size)` — fetch 之後 call
- `GbaMemoryBus` 維護 `ExecutingPc` + `LastBiosFetchWord`（取 BIOS[pc + 2×size]）
- BIOS-region read 時：PcInBios → 真值；else → sticky slice

關鍵踩雷：原本把兩件事併在 NotifyInstructionFetch 一個 call，導致 SWI
進 BIOS 時 ExecutingPc 還停在 cart → BIOS fetch 走 open-bus 回 sticky
→ CPU 解垃圾指令。**fetch 前先 update ExecutingPc** 才解。

---

## 5. PPU 完整化 — 從 Mode 3/4 stub 到完整 BG/OBJ pipeline

原本 `GbaPpu` 只 render Mode 3/4 + 其他 mode 全黑屏。完整擴展：

### 5.A Per-layer buffer composite pipeline

5 個 `ushort[Width × Height]` layer buffer (BG0..BG3, OBJ) + 每 pixel
OBJ priority/semi-transparent flag。`Composite()` 最後一 pass 走每
pixel 找 topmost + second-topmost opaque layer，套 BLDCNT。

### 5.B 完整 BLDCNT 三模式

- **alpha** (mode 1)：`min(31, (T1 × EVA + T2 × EVB) >> 4)`
- **brighten** (mode 2)：`T1 + ((31 - T1) × EVY) >> 4`
- **darken** (mode 3)：`T1 - (T1 × EVY) >> 4`
- OBJ-mode-1 sprites 強制 alpha-blend regardless of BLDCNT.target1

### 5.C OBJ Window mask + WININ/WINOUT layer gating

第一 pass 從 mode-2 sprite 算 mask（哪些 pixel 在 "OBJ window 內"）。
BG/OBJ render 時透過 `LayerVisibleAt(layer, x, y)` 檢查 Window 0/1/OBJ
規則，回傳該 layer 是否該畫。

### 5.D Mode 2 affine BG (BG2 + BG3)

Per GBATEK：8.8 fixed-point PA/PB/PC/PD matrix + 19.8 X/Y origin。
Wrap vs transparent overflow。

### 5.E OBJ sprite 完整實作

128 OAM entries × 8 bytes，affine matrix from 32 個 OAM-interleaved 矩陣，
12 種 size class (8×8 ~ 64×64)，1D + 2D mapping，4bpp + 8bpp，
hflip/vflip，priority sorting。

### 5.F Mode 0 (4 個 text BG) + Mode 1 (BG0/BG1 text + BG2 affine)

Tile-based scrollable BG：4bpp/8bpp + char base + screen base + size
0..3 (256×256 / 512×256 / 256×512 / 512×512) + 9-bit scroll +
multi-screen-block layout (`[SC0][SC1] / [SC2][SC3]`)。

### 5.G 找出來的 PPU bug

**8bpp 2D OBJ tile-row stride**：對照 mGBA `software-obj.c` 找到致命
bug — 我們 2D mapping 不論色深都用 `tilesPerRow = 32`，但 GBATEK 寫
**8bpp 2D 是 32×16 grid (16 tiles per row)**，因為 8bpp tile 占兩個
4bpp slot。修前 BIOS shape-2 size-3 (32×64) sprite 在 row 8 後讀錯
位置 → "3 個 GAME BOY" 疊影 artifact。修對 → BIOS logo 與 mGBA 視覺
同等。

**BG2CNT/BG3CNT 地址**：之前讀錯成 BG0CNT (0x08) / BG1CNT (0x0A)，
正確是 0x0C / 0x0E。

---

## 6. GB CLI 對齊 GBA + DMG BIOS

`apr-gb` 之前缺 BIOS 跟時間單位。對齊 `apr-gba`：

### CLI 新增

- `--bios=<path>` — DMG / DMG-0 boot ROM (256 bytes)
- `--seconds=N` — DMG-emulated wall-time (4,194,304 t-cyc/s)
- 輸出格式統一：「budget: cycles ≈ frames ≈ DMG seconds」+ 「ran ... 
  in X s host time (Y DMG-emulated s, Z× real-time)」

### Bus + CPU 改動

- `GbMemoryBus.LoadBios()` + `BiosEnabled` flag
- ReadByte：BiosEnabled && addr < 0x100 → return BIOS bytes
- WriteIo 0xFF50 → 任何 non-zero bit 永久 unmap BIOS（real hardware
  one-shot）
- LegacyCpu / JsonCpu Reset() 依 `bus.BiosEnabled` 切：
  - true：cold start (all regs 0, PC=0, SP=0)
  - false：post-BIOS state (PC=0x100, A=0x01, F=0xB0, SP=0xFFFE)

### 驗證

`apr-gb --bios=BIOS/gb_bios.bin --rom=blargg-cpu/cpu_instrs.gb --seconds=0.1
        --screenshot=result/gb/gb_bios_logo.png`

→ 經典 DMG 開機畫面：綠底 + 「Nintendo®」字樣

### Caveat

我們 BIOS 在 ~0.1 DMG-秒完成（real ~2.5s），因為 PPU 的 LY 寄存器
hardcoded 0x90 (fake VBlank) → BIOS 等 VBlank 的 polling loop 瞬間
完成。要真機 timing 需加 GB scheduler（per-scanline PPU clock） — 
未來 work。但 BIOS hand-off + cart 執行都正確（cpu_instrs serial
output 「cpu_instrs / 01」normal）。

---

## 7. 跨 commit 檔案改動匯總

`AprCpu.Core` (CPU framework)：
- `IR/CpuStateLayout.cs` — 加 `PcWrittenFieldIndex` (i8 flag)
- `IR/Emitters.cs` — `Branch`/`WriteReg` emit flag set；新 `ReadRegShiftByReg` op
- `IR/ArmEmitters.cs` — `BranchIndirectArm` flag set；`raise_exception` 清 T；
  新 `UpdateNz64` op
- `IR/OperandResolvers.cs` — LSL/LSR by 32 carry fix；shift-by-reg PC+12 helper
- `IR/BlockTransferEmitters.cs` — LDM 載 R15 set flag
- `Runtime/IMemoryBus.cs` — 加 `NotifyExecutingPc` + `NotifyInstructionFetch` 
  default no-op
- `Runtime/CpuExecutor.cs` — fetch 前/後 call bus；branched 偵測接 flag
- `Runtime/Gba/GbaMemoryBus.cs` — BIOS open-bus + sticky；HALTCNT；DISPSTAT 
  toggle 拔掉
- `Runtime/Gba/GbaSystemRunner.cs` — IRQ entry 清 T；halt-aware loop

`AprGba.Cli` (GBA CLI)：
- `Program.cs` — `--bios` / `--seconds` / 8 個 diagnostic flag (`--no-obj`、
  `--only-obj=N` 等); GbaTiming 常數
- `Video/GbaPpu.cs` — 完整重寫 ~600 lines (per-layer composite + Mode 0/1/2 + 
  OBJ + windowing + blending)
- `RomPatcher.cs` — 新檔，cart logo + checksum patch

`AprGb.Cli` (GB CLI)：
- `Program.cs` — `--bios` / `--seconds`; GbTiming 常數
- `Memory/GbMemoryBus.cs` — `LoadBios` / BiosEnabled / 0xFF50 unmap
- `Cpu/LegacyCpu.cs` + `Cpu/JsonCpu.cs` — Reset 分 cold-start vs post-BIOS

`spec/arm7tdmi/`：
- `groups/data-processing.json` — RegRegShift format 14 個指令的 read_reg(rn) 
  改成 read_reg_shift_by_reg
- `groups/multiply-long.json` — 4 個 mnemonic 末尾加 S-bit + update_nz_64

---

## 8. 驗證快照

| ROM / Test | 結果 | Backend |
|---|---|---|
| 345 unit tests | 全綠 | xUnit |
| jsmolka arm.gba (HLE boot) | All tests passed | apr-gba |
| jsmolka thumb.gba (HLE boot) | All tests passed | apr-gba |
| jsmolka arm.gba (BIOS LLE) | All tests passed @ 6s | apr-gba --bios |
| jsmolka thumb.gba (BIOS LLE) | All tests passed @ 6s | apr-gba --bios |
| jsmolka bios.gba (BIOS LLE) | All tests passed @ 6s | apr-gba --bios |
| BIOS startup logo @ 2.5s | mGBA-equivalent visual | apr-gba --bios |
| Mode 0 stripes.gba | 藍色直線條紋 ✓ | apr-gba (HLE) |
| Mode 0 shades.gba | 藍色漸層 ✓ | apr-gba (HLE) |
| Blargg cpu_instrs.gb | passes (serial) | apr-gb |
| DMG BIOS logo | "Nintendo®" 顯示 ✓ | apr-gb --bios |

PPU coverage：
- Mode 0 ✅、Mode 1 ✅、Mode 2 ✅、Mode 3 ✅、Mode 4 ✅
- Mode 5 ❌（小尺寸 RGB555 bitmap，少用）
- OBJ 完整 ✅（normal + affine + 4bpp/8bpp + 1D/2D mapping）
- BLDCNT alpha/brighten/darken ✅
- WININ/WINOUT + Window 0/1 + OBJ Window ✅
- Mosaic ❌（少用，下次補）

---

## 9. 下一步可選方向

按優先順序：

1. **GB scheduler (per-scanline PPU clock)** — 解決 DMG BIOS 跑太快的問題；
   也鋪路 GB 端走完整 LLE 啟動流程後加入 `--frames` 真正準
2. **Mode 5 + Mosaic** — 補完 PPU 剩下 corner case
3. **Audio (APU)** — hand-written，跟 PPU 同套寫法
4. **Phase 7 block-JIT** — 把 GBA 4.4 MIPS 拉到 ≥ real-time（test ROM 截圖
   不需要，但跑商業遊戲必要）
5. **第三顆 CPU 移植** — MIPS R3000 / RISC-V 之類，加碼驗證 framework
   通用性

---

## 10. 一句話總結

**Phase 5 全收完**：BIOS LLE + PPU 完整化 + GB CLI 對齊。GBA 端
從「test ROM CPU 驗證 + 截圖」拓展到「真 BIOS LLE + Nintendo
logo 視覺與 mGBA 同等 + arm/thumb/bios 三 ROM 全 PASS」。GB 端
拿到 `--bios` 跟 `--seconds` 對齊操作介面。**framework「換 CPU
+ 換 platform = 換 JSON + 換 emitter + 換 PPU」的論點兩個平台
都 end-to-end 驗證完成**。
