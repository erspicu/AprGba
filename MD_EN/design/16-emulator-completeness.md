# GBA / GB CLI Emulator Completeness Report — Everything Beyond the CPU

> **Status**: completeness audit (2026-05-05)
> **Scope**: An audit of all non-CPU modules across `src/AprGba.Cli/`,
> `src/AprGb.Cli/`, and the related `src/AprCpu.Core/Runtime/Gba/`. The CPU
> side (ARM7TDMI / Thumb / LR35902 spec, block-JIT, interpreter) goes through
> the `AprCpu` framework and is finished in P0 + P1
> ([`MD_EN/design/12-gb-block-jit-roadmap.md`](/MD_EN/design/12-gb-block-jit-roadmap.md));
> this document does not repeat that material.
>
> **Target audience**: (a) anyone wanting to know how far AprGba / AprGb is
> from being a "complete, usable emulator"; (b) anyone wanting to take it the
> rest of the way to "running commercial games."

---

## 1. Big picture — this project is not currently an end-user emulator

Positioning first:

| Perspective | Current state |
|---|---|
| **Framework demo** (primary goal) | ✅ achieved — same framework runs both ARM7TDMI and LR35902 ISAs; jsmolka + Blargg PASS |
| **CPU correctness** | ✅ achieved — Blargg cpu_instrs 11/11, jsmolka arm/thumb all PASS |
| **GBA homebrew test ROMs** | ✅ runs — including BIOS LLE |
| **GBA commercial games** | ❌ most will fail — missing audio / parts of timer / save / input |
| **GB commercial games** | ❌ almost all fail — missing sprite / window / sound / MBC3+ / input |
| **Interactivity** | ❌ pure CLI — outputs PNG when finished, no real-time window, no keyboard input |

**Corresponds to README §2 "What this project is NOT"**: deliberately not
chasing mGBA-grade complete emulation; the CPU and framework are done first,
peripheral work (PPU details / audio / input / save) comes later. This
document spells out what is still missing.

---

## 2. GBA — `src/AprGba.Cli/` + `src/AprCpu.Core/Runtime/Gba/`

### 2.1 Implemented subsystems

| Subsystem | File | Completeness | Notes |
|---|---|---|---|
| **Memory Bus** | [`GbaMemoryBus.cs`](/src/AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs) (483 lines) | ✅ 95% | Full memory map: BIOS / IWRAM 32K / EWRAM 256K / IO 1K / Palette 1K / VRAM 96K / OAM 1K / ROM up to 32M / SRAM 64K. Region-based ReadByte/WriteByte/ReadHalf/ReadWord with unaligned-access handling |
| **Memory Map metadata** | [`GbaMemoryMap.cs`](/src/AprCpu.Core/Runtime/Gba/GbaMemoryMap.cs) | ✅ | IRQ source enum covers Timer0-3 / DMA0-3 / Keypad / Serial / Cartridge — but some are merely defined and not yet implemented |
| **PPU** | [`GbaPpu.cs`](/src/AprGba.Cli/Video/GbaPpu.cs) (760 lines) | ✅ 90% | **Major work done**: Mode 0 (4 BG tile), Mode 1 (BG0/1 tile + BG2 affine), Mode 2 (BG2/3 affine), Mode 3 (15-bit bitmap), Mode 4 (paletted bitmap, two pages), Mode 5 (160×128 bitmap); OBJ sprites with affine + mosaic + semi-transparent (mode-1); BG semi-transparency alpha blending (BLDCNT/BLDALPHA/BLDY including brighten/darken); OBJ-Window mask (mode-2 sprites); Window 0/1/OBJ; priority 8-way compositing |
| **DMA** | [`GbaDmaController.cs`](/src/AprCpu.Core/Runtime/Gba/GbaDmaController.cs) (205 lines) | ✅ 80% | DMA0/1/2/3 with immediate / VBlank / HBlank / Special timing; Audio FIFO DMA shape (but audio itself is not implemented so this never actually fires) |
| **Scheduler** | [`GbaScheduler.cs`](/src/AprCpu.Core/Runtime/Gba/GbaScheduler.cs) (191 lines) | ✅ | PPU dot-driven (240 dots HDraw + 68 HBlank) × (160 visible + 68 VBlank) = 1232 cycles/scanline × 228 scanlines/frame = 280896 cycles/frame; HBlank/VBlank/VCount-match IRQ |
| **IRQ delivery** | [`GbaSystemRunner.cs`](/src/AprCpu.Core/Runtime/Gba/GbaSystemRunner.cs) (202 lines) | ✅ | ARM IRQ entry sequence: SPSR_irq save, CPSR mode + I-bit, bank swap, R14_irq = PC + 4, PC = 0x18 |
| **BIOS LLE** | (uses real gba_bios.bin) | ✅ | jsmolka arm/thumb PASS on the BIOS LLE path (screenshot in README) |
| **ROM patcher** | [`RomPatcher.cs`](/src/AprGba.Cli/RomPatcher.cs) | ✅ | Auto-fixes Nintendo logo + cart header checksum for homebrew ROMs |
| **Output** | [`PngWriter.cs`](/src/AprGba.Cli/Video/PngWriter.cs) | ✅ | Screenshot output as PNG (zlib + CRC32 hand-rolled, no external lib) |
| **CLI flags** | [`Program.cs`](/src/AprGba.Cli/Program.cs) | ✅ | `--rom`, `--bios`, `--cycles/--frames/--seconds`, `--screenshot`, `--block-jit`, `--info`, debug `--no-obj/--no-bg/--only-obj=N` |

### 2.2 Missing subsystems (must-haves for commercial games)

| Subsystem | Severity | Estimate | Notes |
|---|---|---|---|
| **Timers (TM0-TM3)** | 🔥 critical | 1-2 days | Currently only the IRQ source enum is defined; TIMER counter (`0x04000100..0x04000110`) read/write, cascade, prescaler (1/64/256/1024), overflow → IRQ are all unwritten. **Almost every commercial game uses timers for audio sync / animation timing.** Without them games will barely move. |
| **APU (audio)** | 🔥 critical | 1-2 weeks | 4 PSG channels (square × 2 + wave + noise) + DirectSound A/B (FIFO from DMA1/2). Currently entirely absent. Without audio some games hang in sound init or get stuck waiting on an audio IRQ |
| **Joypad input (KEYINPUT/KEYCNT)** | 🔥 critical | 1-3 days | 0x04000130 KEYINPUT register + 0x04000132 KEYCNT (key IRQ). Currently keyinput always returns 0xFFFF (no buttons pressed); games can't get past the menu. Need to wire up .NET keyboard input or a CLI input file |
| **Save support: SRAM** | high | 1 day | The 64KB SRAM region exists but is not loaded from `.sav` at startup or written back on shutdown. A simple byte-array dump suffices |
| **Save support: Flash 64K/128K** | high | 3-5 days | Flash command sequence (write enable / sector erase / chip erase / device ID); used by Pokemon FRLG and similar |
| **Save support: EEPROM 4K/64K** | high | 3-5 days | Different read/write protocol; used by Pokemon Ruby/Sapphire/Emerald and similar |
| **RTC (Real-Time Clock)** | medium | 2-3 days | Used by Pokemon RSE / Boktai; I2C-like protocol over GPIO pins |
| **Audio output (host)** | medium | 1 week | Even with the APU done, samples need to be streamed to the host (NAudio / WASAPI / Pulse). Or dump to a WAV file first |
| **Real-time display window** | medium | 1 week | Currently only PNG. To actually play games, we need real-time display (OpenGL / Direct3D / SDL / Avalonia / plain GDI WinForm) |
| **Cycle stretch (waitstates)** | low-med | 2-3 days | GBA cart ROM read at 32-bit width takes one extra cycle (n+1); the current cycle accounting does not differentiate region speeds. Affects both perf and timing precision |
| **Serial / Multiboot / Link cable** | low | 1 week+ | Multiplayer, Pokemon trade, Mario Kart link communication. Low impact on single-player ROMs |
| **Mosaic effect** | (already in PPU) | — | check this is fully tested |
| **Mode 5 verification** | (already in PPU) | — | rare mode, possible untested edge case |
| **Cartridge ROM size auto-detection** | low | half a day | Currently relies on file size; ROM mirroring (16M ROM in 32M space) is not handled |

### 2.3 GBA staged-goal recommendations

| Goal | Required |
|---|---|
| **Run most homebrew + simple commercial ROMs** | Timers + Joypad + SRAM save + real-time display window (~2 weeks) |
| **Run 80% of commercial GBA games** | + APU + Flash/EEPROM save + audio output (~1 month total) |
| **Run 95% of commercial games including Pokemon** | + RTC + cycle-accurate waitstates + cart-specific quirks (~2-3 months total) |

---

## 3. Game Boy DMG — `src/AprGb.Cli/`

### 3.1 Implemented subsystems

| Subsystem | File | Completeness | Notes |
|---|---|---|---|
| **Memory Bus** | [`GbMemoryBus.cs`](/src/AprGb.Cli/Memory/GbMemoryBus.cs) (248 lines) | ✅ 70% | Full memory map: ROM bank 0 / switchable / VRAM 8K / ExtRAM 32K / WRAM 8K / OAM / IO / HRAM / IE. MBC1 banking complete (including ROM/RAM mode bit and bank-0 quirk). Serial port capture (used to read Blargg results) |
| **MBC1** | (in `GbMemoryBus.cs`) | ✅ | ROM bank lo 5-bit + RAM bank / upper ROM bit, quirky bank-0 → bank-1 mapping, $0x6000 mode select |
| **Timer (DIV/TIMA)** | (in `GbMemoryBus.cs:Tick`) | ✅ | DIV (0xFF04) increment, TIMA (0xFF05) with 4 prescaler choices + reload from TMA + IF.Timer set on overflow |
| **PPU (minimal)** | [`GbPpu.cs`](/src/AprGb.Cli/Video/GbPpu.cs) (80 lines) | ⚠️ 30% | **BG layer only**! No sprites, no window. Adequate for Blargg and the BIOS boot screen, but inadequate for any actual game |
| **Scheduler** | [`GbScheduler.cs`](/src/AprGb.Cli/Memory/GbScheduler.cs) (152 lines) | ✅ | T-cycle advancement, scanline counting, VBlank IRQ |
| **CPU diff harness** | [`CpuDiff.cs`](/src/AprGb.Cli/Diff/CpuDiff.cs) (262 lines) | ✅ | Two backends in lockstep on the same ROM for comparison; used for block-JIT correctness |
| **Output** | [`PngWriter.cs`](/src/AprGb.Cli/Video/PngWriter.cs) + [`PpmWriter.cs`](/src/AprGb.Cli/Video/PpmWriter.cs) | ✅ | Screenshot PNG / PPM |
| **CLI flags** | [`Program.cs`](/src/AprGb.Cli/Program.cs) | ✅ | `--rom`, `--bios`, `--cpu={legacy,json-llvm}`, `--cycles/--frames/--seconds`, `--block-jit`, `--diff=N`, `--diff-bjit=N`, `--bench` |

### 3.2 Missing subsystems (must-haves for commercial games)

| Subsystem | Severity | Estimate | Notes |
|---|---|---|---|
| **PPU sprites (OAM)** | 🔥 critical | 3-5 days | 40 sprites × 8×8 / 8×16, flip H/V, palette OBP0/OBP1, priority. **Without this no game shows any character on screen** |
| **PPU window layer** | 🔥 critical | 1-2 days | WIN_X/WIN_Y, HUD overlay. A few games skip it, but most menu/status bars depend on it |
| **PPU mode timing + STAT IRQ** | 🔥 critical | 2-3 days | Mode 0 (HBlank) / 1 (VBlank) / 2 (OAM scan) / 3 (drawing) — 4 phases; STAT IRQ source bits (HBlank/VBlank/OAM/LYC=LY). Most games rely on the STAT IRQ for mid-frame palette swaps and HDMA |
| **MBC3 + RTC** | 🔥 critical | 3-5 days | Pokemon RBY/G/S, all of Gen 2 use MBC3. Without this Pokemon won't even boot. RTC (real-time clock with seconds/minutes/hours/days/halt) is part of MBC3 |
| **MBC2** | medium | 1 day | Less common but simple; 4-bit nibble RAM |
| **MBC5** | medium | 2 days | Pokemon Crystal, late-era games, includes rumble bit |
| **APU (sound)** | high | 1-2 weeks | 4 channels (square × 2 + wave + noise). Game Boy sound is practically a cultural icon; every commercial game uses it |
| **Joypad input (0xFF00)** | 🔥 critical | half a day | A/B/Start/Select/D-pad; select bits 5/4 toggle dir vs button. Without this you can't enter the menu |
| **Battery save (.sav)** | high | half a day | Read `.sav` → ExtRam at boot, write back at shutdown (or auto-save). MBC1/3/5 all have battery variants |
| **Audio output** | medium | 1 week | Same as GBA |
| **Real-time display window** | medium | 1 week | Same as GBA |
| **GBC (Game Boy Color)** | low | 1-2 weeks | Dual-speed CPU, dual VRAM banks, palette RAM, HDMA, infrared. **Spec is already LR35902 = SM83; the GBC CPU only differs by KEY1 speed switch + extra IO**; CPU changes are small, but PPU/IO additions are large |

### 3.3 GB staged-goal recommendations

| Goal | Required |
|---|---|
| **Run early DMG games (Tetris / Mario Land etc.)** | PPU sprites + window + STAT IRQ + Joypad + battery save (~2 weeks) |
| **Run most DMG games including Pokemon RBY** | + MBC3 + APU + audio output + real-time window (~1 month) |
| **Run GBC games** | + GBC PPU + KEY1 + GBC palette/HDMA (~2 months total) |

---

## 4. Cross-platform missing pieces

These benefit both platforms when done once:

| Item | Estimate | Notes |
|---|---|---|
| **Real-time display window** (host UI) | 1-2 weeks | Currently only PNG screenshots. Avalonia / WPF / WinForm / SDL / SkiaSharp all work; pin a 60fps framebuffer push |
| **Audio backend** (host streaming) | 1 week | NAudio (Windows) / Pulse (Linux) / CoreAudio (macOS). Even before APU is done, having an audio sink in place lets it plug in later |
| **Keyboard / Gamepad input** (host wiring) | 3-5 days | DirectInput / XInput / SDL gamepad; needs an abstraction shared by GBA Joypad and GB Joypad |
| **Save file management** (UI layer) | 2-3 days | Auto load/save `<rom>.sav`, auto-save interval, save slots |
| **Frame skip / speed control** | 2-3 days | Speed up / slow down / pause / single-step; useful for both debugging and ordinary users |
| **Debug viewer** | 1-2 weeks | VRAM viewer (tile / OAM), palette viewer, disassembly view, breakpoints. Framework-grade debug experience |

---

## 5. Recommended implementation priority (if pushing this project to a "playable" level)

Sorted by user-visible payoff:

### Phase A — Interactive foundation (~3 weeks)

Make the emulator something to "actually use" rather than just a batch tool.

1. **Real-time display window** (Avalonia or SkiaSharp) — 1 week
2. **Joypad input wiring** (GBA + GB both hooked up) — 3-5 days
3. **Save file read/write** (SRAM `.sav` for GB MBC1/MBC3 + GBA SRAM) — 2-3 days
4. **GBA Timers** — 1-2 days
5. **GB PPU sprites + window + STAT IRQ** — 1 week

After this: homebrew and simple commercial ROMs are playable to completion.

### Phase B — Advanced game support (~1 month)

6. **GBA APU** — 1-2 weeks
7. **GB MBC3 + RTC** — 3-5 days
8. **GB MBC5** — 2 days
9. **Audio backend (host)** — 1 week
10. **GB APU** — 1-2 weeks

After this: the Pokemon series, Zelda, and the vast majority of commercial games run.

### Phase C — Full commercial game support (~2 months)

11. **GBA Flash 64K/128K save** — 3-5 days
12. **GBA EEPROM 4K/64K save** — 3-5 days
13. **GBA RTC (Pokemon RSE)** — 2-3 days
14. **GBA cycle-accurate waitstates** — 2-3 days
15. **GBC support** — 2 weeks
16. **Cycle-perfect PPU timing details** — 1 week

After this: correctness is competitive with mGBA / SameBoy and other professional emulators.

### Phase D — UX completion (as needed)

17. Debug viewer / disassembly UI
18. Save state (memory snapshot, distinct from cart save)
19. Cheat code support (Action Replay / GameShark)
20. Settings UI, controller remap, video filter
21. Multi-platform (Linux / macOS RID + UI port)

---

## 6. Why we currently choose not to do these

A few reasons, in order of importance:

1. **The project's purpose is a framework demo, not a user product.** Getting
   `AprCpu` to run ARM7TDMI + LR35902 with correctness, with a complete
   block-JIT mechanism — once that goal is achieved, anything could be the
   next application (emulator, taint analysis, visual teaching material,
   binary translator, ...). Emulator work is not mandatory.

2. **PPU / APU / input / save are platform-specific, not framework-level.**
   Even a complete GBA APU implementation has nothing to do with the next
   CPU port. Framework investment should go into "does the next CPU port
   port smoothly", not "is the GBA experience complete".

3. **Time-bounded research project.** A hobby research project, expanded to
   build a full emulator, will be eaten by PPU / APU / input minutiae for
   months, polluting framework design quality. Converge the framework first;
   if it ever makes sense, invest in an emulator phase separately.

4. **Limited value in benchmarking against industry emulators.** mGBA /
   SameBoy etc. are already professional-grade; AprGba's value is not in
   "yet another GBA emulator" but in "the same framework can also produce a
   GBA emulator". Once the latter is shown, the former is up to the
   maintainer's interest.

If anyone wants to fork this and push it to a user-grade emulator, this doc
is the starting point. Follow the §5 phase ordering, do not touch the
framework-level design patterns (`AprCpu` and `EmitContext`), and add
PPU / APU / input / save accordingly.

---

## 7. Reference

- [`MD_EN/design/12-gb-block-jit-roadmap.md`](/MD_EN/design/12-gb-block-jit-roadmap.md) — CPU framework / block-JIT progress (not duplicated here)
- [`MD_EN/design/15-timing-and-framework-design.md`](/MD_EN/design/15-timing-and-framework-design.md) — Design concepts for accurate timing + framework generalization
- Industry comparisons: mGBA (https://mgba.io), SameBoy (https://sameboy.github.io), Emulicious, BGB, VisualBoyAdvance-M
- [GBATEK](https://problemkaputt.de/gbatek.htm) — full GBA hardware reference (PPU / APU / DMA / IO complete); Pan Docs (https://gbdev.io/pandocs/) — Game Boy hardware reference
