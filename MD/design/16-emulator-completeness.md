# GBA / GB CLI 模擬器完成度報告 — CPU 以外的部分

> **Status**: completeness audit (2026-05-05)
> **Scope**: 把 `src/AprGba.Cli/` 跟 `src/AprGb.Cli/` 跟相關的
> `src/AprCpu.Core/Runtime/Gba/` 兩端的 CPU 以外模組做盤點。CPU 部分
> （ARM7TDMI / Thumb / LR35902 spec、block-JIT、interpreter）走 `AprCpu`
> 框架，已在 P0 + P1 完工 ([`MD/design/12-gb-block-jit-roadmap.md`](/MD/design/12-gb-block-jit-roadmap.md))
> ，本檔不重複。
>
> **目標讀者**：(a) 想知道 AprGba / AprGb 距離「完整實用 emulator」還有多
> 遠的人；(b) 想接手把它推到「跑商業遊戲」的人。

---

## 1. Big picture — 這個專案目前不是 end-user emulator

要先把定位講清楚：

| 視角 | 目前位置 |
|---|---|
| **Framework demo** (主要目的) | ✅ 達成 — 同框架跑 ARM7TDMI + LR35902 兩 ISA、jsmolka + Blargg PASS |
| **CPU correctness** | ✅ 達成 — Blargg cpu_instrs 11/11、jsmolka arm/thumb 全 PASS |
| **GBA homebrew test ROM** | ✅ 跑得起 — 含 BIOS LLE |
| **GBA 商業遊戲** | ❌ 大多數會掛 — 缺 audio / 部分 timer / save / input |
| **GB 商業遊戲** | ❌ 幾乎都掛 — 缺 sprite / window / sound / MBC3+/input |
| **互動性** | ❌ 純 CLI — 跑完輸出 PNG，沒有即時視窗、沒有鍵盤輸入 |

**對應 README 的 §2 "What this project is NOT"**：刻意不追 mGBA-grade 的完整
emulation；CPU 跟框架部分先做到位，外圍 (PPU 細節 / audio / input / save)
是後續工作。本檔列清楚還欠什麼。

---

## 2. GBA — `src/AprGba.Cli/` + `src/AprCpu.Core/Runtime/Gba/`

### 2.1 已實作子系統

| 子系統 | 檔案 | 完成度 | 備註 |
|---|---|---|---|
| **Memory Bus** | [`GbaMemoryBus.cs`](/src/AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs) (483 行) | ✅ 95% | 完整記憶體圖：BIOS / IWRAM 32K / EWRAM 256K / IO 1K / Palette 1K / VRAM 96K / OAM 1K / ROM up to 32M / SRAM 64K。Region-based ReadByte/WriteByte/ReadHalf/ReadWord 含 unaligned-access 處理 |
| **Memory Map metadata** | [`GbaMemoryMap.cs`](/src/AprCpu.Core/Runtime/Gba/GbaMemoryMap.cs) | ✅ | IRQ source enum 含 Timer0-3 / DMA0-3 / Keypad / Serial / Cartridge — 但有些只是定義、沒實作 |
| **PPU** | [`GbaPpu.cs`](/src/AprGba.Cli/Video/GbaPpu.cs) (760 行) | ✅ 90% | **重點完成**：Mode 0 (4 BG tile)、Mode 1 (BG0/1 tile + BG2 affine)、Mode 2 (BG2/3 affine)、Mode 3 (15-bit bitmap)、Mode 4 (paletted bitmap, 兩 page)、Mode 5 (160×128 bitmap)；OBJ sprites 含 affine + mosaic + 半透明 (mode-1)；BG 半透明 alpha blending (BLDCNT/BLDALPHA/BLDY 含 brighten/darken)；OBJ-Window mask (mode-2 sprites)；Window 0/1/OBJ；priority 8-way compositing |
| **DMA** | [`GbaDmaController.cs`](/src/AprCpu.Core/Runtime/Gba/GbaDmaController.cs) (205 行) | ✅ 80% | DMA0/1/2/3 含 immediate / VBlank / HBlank / Special timing；Audio FIFO DMA shape (但 audio 本身沒實作所以實際不會 fire) |
| **Scheduler** | [`GbaScheduler.cs`](/src/AprCpu.Core/Runtime/Gba/GbaScheduler.cs) (191 行) | ✅ | PPU dot-driven (240 dots HDraw + 68 HBlank) × (160 visible + 68 VBlank) = 1232 cycles/scanline × 228 scanlines/frame = 280896 cycles/frame；HBlank/VBlank/VCount-match IRQ |
| **IRQ delivery** | [`GbaSystemRunner.cs`](/src/AprCpu.Core/Runtime/Gba/GbaSystemRunner.cs) (202 行) | ✅ | ARM IRQ entry sequence: SPSR_irq save、CPSR mode + I-bit、bank swap、R14_irq = PC + 4、PC = 0x18 |
| **BIOS LLE** | (走真實 gba_bios.bin) | ✅ | jsmolka arm/thumb 在 BIOS LLE 路徑 PASS（screenshot in README） |
| **ROM patcher** | [`RomPatcher.cs`](/src/AprGba.Cli/RomPatcher.cs) | ✅ | 自動修 Nintendo logo + cart header checksum 給 homebrew ROM |
| **Output** | [`PngWriter.cs`](/src/AprGba.Cli/Video/PngWriter.cs) | ✅ | screenshot 輸出 PNG (zlib + CRC32 自寫，無外部 lib) |
| **CLI flags** | [`Program.cs`](/src/AprGba.Cli/Program.cs) | ✅ | `--rom`、`--bios`、`--cycles/--frames/--seconds`、`--screenshot`、`--block-jit`、`--info`、debug `--no-obj/--no-bg/--only-obj=N` |

### 2.2 缺的子系統（要跑商業遊戲必補）

| 子系統 | 嚴重度 | 估時 | 備註 |
|---|---|---|---|
| **Timers (TM0-TM3)** | 🔥 critical | 1-2 天 | 目前只有 IRQ source enum 定義；TIMER counter (`0x04000100..0x04000110`) read/write、cascade、prescaler (1/64/256/1024)、overflow → IRQ 都沒寫。**幾乎所有商業遊戲都用 timer 做 audio sync / animation timing**。沒這個遊戲幾乎不會動。 |
| **APU (audio)** | 🔥 critical | 1-2 週 | 4 PSG channel (square × 2 + wave + noise) + DirectSound A/B (FIFO from DMA1/2)。現在完全沒有。沒 audio 一些遊戲在 sound init 時掛、或卡在等 audio IRQ |
| **Joypad input (KEYINPUT/KEYCNT)** | 🔥 critical | 1-3 天 | 0x04000130 KEYINPUT register + 0x04000132 KEYCNT (key IRQ)。目前 keyinput 永遠回 0xFFFF (no buttons pressed)，遊戲跑不出 menu。要接 .NET 鍵盤輸入或 CLI input file |
| **Save support: SRAM** | high | 1 天 | 64KB SRAM region 存在但開機時沒從 .sav 讀、結束時沒寫。簡單的 byte array dump |
| **Save support: Flash 64K/128K** | high | 3-5 天 | Flash command sequence (write enable / sector erase / chip erase / device ID)；Pokemon FRLG 等用 |
| **Save support: EEPROM 4K/64K** | high | 3-5 天 | 不同的 read/write protocol；Pokemon Ruby/Sapphire/Emerald 等用 |
| **RTC (Real-Time Clock)** | medium | 2-3 天 | Pokemon RSE / Boktai 用；I2C-like protocol 透過 GPIO pins |
| **Audio output (host)** | medium | 1 週 | 即使 APU 實作好了，也要 stream sample 到 host (NAudio / WASAPI / Pulse)。或先 dump WAV 檔 |
| **Real-time display window** | medium | 1 週 | 目前只能 PNG。要能玩遊戲必須有即時顯示（OpenGL / Direct3D / SDL / Avalonia / 純 GDI WinForm） |
| **Cycle stretch (waitstates)** | low-med | 2-3 天 | GBA cart ROM read 在 32-bit 寬時要多 1 cycle (n+1)；目前 cycle 帳沒區分 region speed。對 perf 跟 timing precision 都有影響 |
| **Serial / Multiboot / Link cable** | low | 1 週+ | 多人遊戲、Pokemon trade、Mario Kart 通訊。對 single-player ROM 影響低 |
| **Mosaic effect** | (PPU 已有) | — | check this is fully tested |
| **Mode 5 verification** | (PPU 已有) | — | 少見模式，可能 untested edge case |
| **Cartridge ROM size 自動偵測** | low | 半天 | 現在依賴 file size，如果有 ROM mirror 機制 (16M ROM 在 32M space) 沒處理 |

### 2.3 GBA 階段性目標建議

| 目標 | 必補 |
|---|---|
| **跑大多數 homebrew + 簡單商業 ROM** | Timers + Joypad + SRAM save + 即時顯示視窗 (~2 週) |
| **跑 80% 商業 GBA 遊戲** | + APU + Flash/EEPROM save + audio output (~1 個月加總) |
| **跑 95% 商業遊戲含寶可夢** | + RTC + cycle-accurate waitstates + cart-specific quirks (~2-3 個月加總) |

---

## 3. Game Boy DMG — `src/AprGb.Cli/`

### 3.1 已實作子系統

| 子系統 | 檔案 | 完成度 | 備註 |
|---|---|---|---|
| **Memory Bus** | [`GbMemoryBus.cs`](/src/AprGb.Cli/Memory/GbMemoryBus.cs) (248 行) | ✅ 70% | 完整記憶體圖：ROM bank 0 / switchable / VRAM 8K / ExtRAM 32K / WRAM 8K / OAM / IO / HRAM / IE。MBC1 banking 完整（含 ROM/RAM mode bit、bank-0 quirk）。Serial port capture (給 Blargg 讀 result 用) |
| **MBC1** | (in `GbMemoryBus.cs`) | ✅ | ROM bank lo 5-bit + RAM bank / upper ROM bit、quirky bank-0 → bank-1 mapping、$0x6000 mode select |
| **Timer (DIV/TIMA)** | (in `GbMemoryBus.cs:Tick`) | ✅ | DIV (0xFF04) 增量、TIMA (0xFF05) 含 prescaler 4 種 + reload from TMA + IF.Timer set on overflow |
| **PPU (minimal)** | [`GbPpu.cs`](/src/AprGb.Cli/Video/GbPpu.cs) (80 行) | ⚠️ 30% | **只有 BG layer**！沒 sprite、沒 window。對 Blargg 跟 BIOS 啟動畫面夠用，對任何遊戲不夠 |
| **Scheduler** | [`GbScheduler.cs`](/src/AprGb.Cli/Memory/GbScheduler.cs) (152 行) | ✅ | T-cycle 推進、scanline 計數、VBlank IRQ |
| **CPU diff harness** | [`CpuDiff.cs`](/src/AprGb.Cli/Diff/CpuDiff.cs) (262 行) | ✅ | 雙 backend lockstep 跑同 ROM 比對；給 block-JIT correctness 用 |
| **Output** | [`PngWriter.cs`](/src/AprGb.Cli/Video/PngWriter.cs) + [`PpmWriter.cs`](/src/AprGb.Cli/Video/PpmWriter.cs) | ✅ | screenshot PNG / PPM |
| **CLI flags** | [`Program.cs`](/src/AprGb.Cli/Program.cs) | ✅ | `--rom`、`--bios`、`--cpu={legacy,json-llvm}`、`--cycles/--frames/--seconds`、`--block-jit`、`--diff=N`、`--diff-bjit=N`、`--bench` |

### 3.2 缺的子系統（要跑商業遊戲必補）

| 子系統 | 嚴重度 | 估時 | 備註 |
|---|---|---|---|
| **PPU sprites (OAM)** | 🔥 critical | 3-5 天 | 40 sprite × 8×8 / 8×16，flip H/V、palette OBP0/OBP1、priority。**沒這個任何遊戲都看不到角色** |
| **PPU window layer** | 🔥 critical | 1-2 天 | WIN_X/WIN_Y、HUD overlay。少數遊戲不用，但常見 menu/status bar 都靠它 |
| **PPU mode timing + STAT IRQ** | 🔥 critical | 2-3 天 | Mode 0 (HBlank) / 1 (VBlank) / 2 (OAM scan) / 3 (drawing) 4 階段；STAT IRQ source bit (HBlank/VBlank/OAM/LYC=LY)。多數遊戲依賴 STAT IRQ 做 mid-frame palette swap、HDMA |
| **MBC3 + RTC** | 🔥 critical | 3-5 天 | Pokemon RBY/G/S、所有 Gen 2 用 MBC3。沒這個 Pokemon 都跑不起來。RTC (real-time clock with seconds/minutes/hours/days/halt) 是 MBC3 子功能 |
| **MBC2** | medium | 1 天 | 較少見但簡單；4-bit nibble RAM |
| **MBC5** | medium | 2 天 | Pokemon Crystal、後期遊戲、含 rumble bit |
| **APU (sound)** | high | 1-2 週 | 4 channel (square × 2 + wave + noise)。Game Boy 音效幾乎是文化標誌；商業遊戲全部用 |
| **Joypad input (0xFF00)** | 🔥 critical | 半天 | A/B/Start/Select/D-pad；select bit 5/4 切換 dir vs button。沒這個進不了 menu |
| **Battery save (.sav)** | high | 半天 | 開機讀 .sav → ExtRam、結束時 (或 Auto-save 階段) 寫回。MBC1/3/5 都有 battery 變體 |
| **Audio output** | medium | 1 週 | 同 GBA |
| **Real-time display window** | medium | 1 週 | 同 GBA |
| **GBC (Game Boy Color)** | low | 1-2 週 | 雙 speed CPU、雙 VRAM bank、palette RAM、HDMA、紅外線。**spec 已是 LR35902 = SM83，跟 GBC CPU 差別只在 KEY1 速度切換 + 額外 IO**；CPU 改動小，PPU/IO 增量大 |

### 3.3 GB 階段性目標建議

| 目標 | 必補 |
|---|---|
| **跑早期 DMG 遊戲 (Tetris / Mario Land 等)** | PPU sprites + window + STAT IRQ + Joypad + battery save (~2 週) |
| **跑大部分 DMG 遊戲含 Pokemon RBY** | + MBC3 + APU + audio output + 即時視窗 (~1 個月) |
| **跑 GBC 遊戲** | + GBC PPU + KEY1 + GBC palette/HDMA (~2 個月加總) |

---

## 4. 兩平台共通缺的東西

這幾項做一次兩邊都受惠：

| 項目 | 估時 | 備註 |
|---|---|---|
| **即時顯示視窗** (host UI) | 1-2 週 | 目前只能 PNG screenshot。Avalonia / WPF / WinForm / SDL / SkiaSharp 都行；定 60fps 的 framebuffer push |
| **Audio backend** (host streaming) | 1 週 | NAudio (Windows) / Pulse (Linux) / CoreAudio (macOS)。即使 APU 還沒做，先有 audio sink 之後接得上 |
| **鍵盤 / Gamepad input** (host wiring) | 3-5 天 | DirectInput / XInput / SDL gamepad；要做成 abstraction 給 GBA Joypad + GB Joypad 共用 |
| **Save 檔管理** (UI 層) | 2-3 天 | `<rom>.sav` 自動載/存、auto-save interval、save slot |
| **Frame skip / speed control** | 2-3 天 | 加快 / 減慢 / 暫停 / 單步；對 debug 跟一般 user 都有用 |
| **Debug viewer** | 1-2 週 | VRAM viewer (tile / OAM)、palette viewer、disassembly view、breakpoint。framework 級的 debug experience |

---

## 5. 推薦實作優先順序（如果要把這專案推到「能玩」級）

按 user-visible 收益排序：

### Phase A — 互動基礎（~3 週）

讓 emulator 能被「實際用」而不只是 batch tool。

1. **Real-time display window** (Avalonia 或 SkiaSharp) — 1 週
2. **Joypad input wiring** (GBA + GB 都接) — 3-5 天
3. **Save 檔讀寫** (SRAM .sav for GB MBC1/MBC3 + GBA SRAM) — 2-3 天
4. **GBA Timers** — 1-2 天
5. **GB PPU sprites + window + STAT IRQ** — 1 週

完成後：homebrew 跟簡單商業 ROM 可玩到通關。

### Phase B — 進階遊戲支援（~1 個月）

6. **GBA APU** — 1-2 週
7. **GB MBC3 + RTC** — 3-5 天
8. **GB MBC5** — 2 天
9. **Audio backend (host)** — 1 週
10. **GB APU** — 1-2 週

完成後：Pokemon 系列、Zelda、絕大多數商業遊戲可跑。

### Phase C — 商業遊戲完整支援（~2 個月）

11. **GBA Flash 64K/128K save** — 3-5 天
12. **GBA EEPROM 4K/64K save** — 3-5 天
13. **GBA RTC (Pokemon RSE)** — 2-3 天
14. **GBA cycle-accurate waitstates** — 2-3 天
15. **GBC support** — 2 週
16. **Cycle-perfect PPU timing details** — 1 週

完成後：跟 mGBA / SameBoy 等專業 emulator 能拼正確性。

### Phase D — UX 完整化（按需求）

17. Debug viewer / disassembly UI
18. Save state (記憶體 snapshot 而不是 cart save)
19. Cheat code support (Action Replay / GameShark)
20. Settings UI、controller remap、video filter
21. Multi-platform (Linux / macOS RID + UI port)

---

## 6. 為何目前選擇不做這些

幾個原因，依重要性排序：

1. **專案目的是 framework demo 不是 user product**。讓 `AprCpu` 跑 ARM7TDMI
   + LR35902 兩 ISA 的 correctness 達標、block-JIT 機制完整 — 這個目標達成
   後，後續可以是任何 application（emulator、taint analysis、視覺化教材、binary
   translator、…），不一定非要做 emulator。

2. **PPU / APU / input / save 是 platform-specific，不是 framework-level**。
   即使做了 GBA 完整的 APU，這跟下顆 CPU port 沒任何關係。framework 投入點
   應在「下一顆 CPU port 能不能順」上，不是「GBA 完整體驗」上。

3. **Time-bounded research project**。一個業餘 research project 攤開做整個
   完整 emulator 會被 PPU / APU / input 細節吃掉幾個月，pollute framework
   設計品質。先把 framework 收斂、後面有需要再單獨投入 emulator 階段。

4. **跟業界 emulator 對拍意義有限**。mGBA / SameBoy 等已是專業級，AprGba
   的價值不在「再做一個 GBA emulator」，而在「同框架也能做 GBA emulator」。
   後者證明完了，前者按需要看 maintainer 興趣。

如果有人想接這個 fork 推到 user-grade emulator，這份 doc 就是 starting point。
按 §5 phase 排序、framework 級 design pattern (`AprCpu` 跟 `EmitContext`)
不要動到，外加 PPU / APU / input / save 即可。

---

## 7. Reference

- [`MD/design/12-gb-block-jit-roadmap.md`](/MD/design/12-gb-block-jit-roadmap.md) — CPU 框架 / block-JIT 進度（這份 doc 不重複）
- [`MD/design/15-timing-and-framework-design.md`](/MD/design/15-timing-and-framework-design.md) — Timing 準確 + 框架通用化的設計觀念
- 業界對拍：mGBA (https://mgba.io)、SameBoy (https://sameboy.github.io)、Emulicious、BGB、VisualBoyAdvance-M
- [GBATEK](https://problemkaputt.de/gbatek.htm) — GBA 完整 hardware reference (PPU / APU / DMA / IO 全) ；Pan Docs (https://gbdev.io/pandocs/) — Game Boy hardware reference
