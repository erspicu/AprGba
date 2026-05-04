# Phase 5.5-5.7 wrap-up — BIOS LLE + PPU completion + GB CLI alignment (2026-05-02)

> Continues from `phase5-gba-mvp-complete-2026-05.md` (Phase 5.1-5.4
> wrap-up notes). This round added 5 more sub-slices: 5.5 DISPSTAT/HALTCNT,
> 5.6 cart patcher, 5.7 BIOS LLE + PPU + GB CLI alignment. End result:
> **real Nintendo boot logo visually matches mGBA**, with consistent
> CLI interface across both GB and GBA.

---

## 1. Phase 5.5 — DISPSTAT toggle removal + HALTCNT

Two hacks left over from Phase 4.x affected LLE boot:

### DISPSTAT VBLANK_FLG toggle hack (removed)

The old `GbaMemoryBus` toggled the VBLANK_FLG bit in
`ReadIoHalfword(0x04)` so that jsmolka's `m_vsync` wouldn't busy-wait
forever. But after LLE BIOS boot, the scheduler maintains
VBLANK_FLG / HBLANK_FLG / VCOUNT_FLG bits itself — the toggle then
overwrote the IRQ enable bits that BIOS wrote, resulting in no VBlank
IRQs being delivered. **Removed the toggle, leaving everything to the
scheduler**.

### HALTCNT (0xFF, 0x301)

The BIOS uses `SWI 02h` to write to HALTCNT -> CPU should halt until
IE & IF != 0. The old version simply ignored the write -> CPU kept
running, wasting cycles.

Added `bus.CpuHalted` flag + halt-aware loop in `GbaSystemRunner`:
when halted, only ticks the scheduler; once IE & IF becomes non-zero,
the flag is cleared automatically and the CPU resumes.

---

## 2. Phase 5.6 — Cart Nintendo logo + header checksum patcher

The real BIOS at boot validates the cart's Nintendo logo (0x004..0x09F,
156 bytes) and header checksum (0x0BD). jsmolka / homebrew often have
random logo bytes, so running LLE directly hangs in the BIOS forever.

New file `RomPatcher.cs`:
- `ExtractLogoFromBios()`: searches the BIOS image for the 6-byte
  prefix `24 FF AE 51 69 9A`, takes the following 156 bytes as the
  canonical logo
- `EnsureValidLogoAndChecksum()`: in-place patches the cart's logo
  bytes + 0xBD checksum to match what the BIOS expects

Idempotent (same cart + BIOS run multiple times only patches once).

---

## 3. Phase 5.7 — BIOS LLE boot + 5 cascading ARM7TDMI bugs

GBA BIOS boot was hanging in the 0x19BC..0x1B0E loop. Through the
`--trace-bios` flag (samples state every 100 instructions in the
BIOS region) + Gemini spec consultation, root cause was found:

### Bug 1: Thumb BCond +0 idiom treated as no-op

`CpuExecutor.Step()` detects branches via "post R15 vs pre-set R15
comparison". When the branch target happens to == pre-set R15 (= pc +
PcOffsetBytes), the detector mistakenly treats it as "did not branch"
-> force-sets PC = pc + InstrSize -> skips the compiler idiom's
back-edge -> infinite loop.

Fix: added `PcWritten` byte flag to state struct; `Branch` /
`BranchIndirectArm` / `LDM-with-R15` / `WriteReg(literal=15)` emitters
set 1 after writing PC, executor reads the flag instead of/in addition
to value comparison.

### Bug 2: ARM LSL/LSR by 32 carry flag

ARM ARM A5.1.7: when count == 32, C = Rm[0] (LSL) or Rm[31] (LSR);
only when count > 32 does C = 0. Our `EmitLslShiftByReg` comment
literally said "approximated as 0".

Fix: added the `count == 32` special case taking rm[0] / rm[31].

### Bug 3: ARM shift-by-register reads PC at wrong offset

ARM ARM A5.1.5: in shift-by-register form, if Rm/Rn is PC, read
`address + 12` instead of the usual `+ 8` (1 extra pipeline cycle).
We had hardcoded `+ 8`.

Fix: added `read_reg_shift_by_reg` op for Rn read, with `+ 4 if PC`
logic in the Rm shift-by-reg resolver; 14 spec instructions in
DataProcessing_RegRegShift switched to the new op.

### Bug 4: UMULLS / SMULLS / UMLALS / SMLALS not writing N/Z flag

Multiply-long S-bit form should set N = bit 63, Z = (result == 0). Our
spec had no flag-update step at all.

Fix: added `update_nz_64` op; spec for the 4 multiply-long mnemonics
adds an S-bit conditional + nz_64 update at the end.

### Bug 5: Exception entry not clearing CPSR.T

ARM ARM B1.8.5: all exception entries (Reset/SWI/IRQ/...) force ARM
mode (clear T bit), because vector table 0x00..0x1C is ARM code. We
preserved the original T -> in Thumb mode, entering the IRQ vector
still decoded 0x18 in Thumb mode -> undefined instruction.

Fix: `raise_exception` emitter + `GbaSystemRunner.DeliverIrqIfPending`
both mask off CPSR.T as part of the mode swap.

### Why did 5 bugs surface concentrated at this point?

**jsmolka's fail-label uses relative offsets. If the fail label happens
to be at a +0 encoding position, the old branch detector bug treated
the BCond +0 as fall-through -> skipped the actual fail jump -> the
subsequent 4 ARM instruction bugs all got silently swallowed by
"failing tests not executing m_exit" -> R12 displays 0 (passed)**.

After fixing the detector, all 4 CPU bugs surfaced; fixing them one
by one finally led to PASS. Five bugs hidden by one detector bug.

Verification: Within 6 GBA seconds via real BIOS LLE, jsmolka
arm.gba/thumb.gba/bios.gba all pass -> "All tests passed", same
timing tier as mGBA.

---

## 4. BIOS open-bus protection

GBATEK: BIOS region (0x00000000..0x00003FFF) reads return real values
when PC is inside BIOS; when PC is outside, returns sticky
last-fetched-opcode. The sticky comes from ARM7TDMI's 3-stage
pipeline — when executing PC=X, fetch has already reached PC + 2x
instr_size.

Implementation:
- `IMemoryBus.NotifyExecutingPc(pc)` (default no-op) — called before fetch
- `IMemoryBus.NotifyInstructionFetch(pc, word, instr_size)` — called
  after fetch
- `GbaMemoryBus` maintains `ExecutingPc` + `LastBiosFetchWord` (taken
  from BIOS[pc + 2x size])
- BIOS-region read: PcInBios -> real value; else -> sticky slice

Key gotcha: originally combined the two events into a single
NotifyInstructionFetch call, causing ExecutingPc to still be on cart
when SWI entered BIOS -> BIOS fetch went open-bus and returned sticky
-> CPU decoded garbage. **Updating ExecutingPc before fetch** was the
fix.

---

## 5. PPU completion — from Mode 3/4 stub to full BG/OBJ pipeline

Originally `GbaPpu` only rendered Mode 3/4 + black screen for other
modes. Full expansion:

### 5.A Per-layer buffer composite pipeline

5 `ushort[Width x Height]` layer buffers (BG0..BG3, OBJ) + per-pixel
OBJ priority/semi-transparent flag. `Composite()` final pass walks
each pixel finding topmost + second-topmost opaque layer, applies
BLDCNT.

### 5.B Full BLDCNT three modes

- **alpha** (mode 1): `min(31, (T1 * EVA + T2 * EVB) >> 4)`
- **brighten** (mode 2): `T1 + ((31 - T1) * EVY) >> 4`
- **darken** (mode 3): `T1 - (T1 * EVY) >> 4`
- OBJ-mode-1 sprites force alpha-blend regardless of BLDCNT.target1

### 5.C OBJ Window mask + WININ/WINOUT layer gating

First pass computes mask from mode-2 sprites (which pixels are "inside
OBJ window"). BG/OBJ render checks Window 0/1/OBJ rules via
`LayerVisibleAt(layer, x, y)`, returns whether the layer should be drawn.

### 5.D Mode 2 affine BG (BG2 + BG3)

Per GBATEK: 8.8 fixed-point PA/PB/PC/PD matrix + 19.8 X/Y origin.
Wrap vs transparent overflow.

### 5.E Full OBJ sprite implementation

128 OAM entries x 8 bytes, affine matrix from 32 OAM-interleaved
matrices, 12 size classes (8x8 ~ 64x64), 1D + 2D mapping, 4bpp + 8bpp,
hflip/vflip, priority sorting.

### 5.F Mode 0 (4 text BGs) + Mode 1 (BG0/BG1 text + BG2 affine)

Tile-based scrollable BG: 4bpp/8bpp + char base + screen base + size
0..3 (256x256 / 512x256 / 256x512 / 512x512) + 9-bit scroll +
multi-screen-block layout (`[SC0][SC1] / [SC2][SC3]`).

### 5.G PPU bugs found

**8bpp 2D OBJ tile-row stride**: by cross-referencing mGBA's
`software-obj.c`, found a critical bug — our 2D mapping always used
`tilesPerRow = 32` regardless of color depth, but GBATEK says
**8bpp 2D is a 32x16 grid (16 tiles per row)** because 8bpp tiles
occupy two 4bpp slots. Before fixing, BIOS shape-2 size-3 (32x64)
sprites read from wrong positions after row 8 -> "3 GAME BOY"
overlapping artifact. Once fixed -> BIOS logo visually matches mGBA.

**BG2CNT/BG3CNT addresses**: previously read from BG0CNT (0x08) /
BG1CNT (0x0A); correct addresses are 0x0C / 0x0E.

---

## 6. GB CLI aligned with GBA + DMG BIOS

`apr-gb` was missing BIOS support and time units. Aligned with `apr-gba`:

### CLI additions

- `--bios=<path>` — DMG / DMG-0 boot ROM (256 bytes)
- `--seconds=N` — DMG-emulated wall-time (4,194,304 t-cyc/s)
- Output format unified: "budget: cycles ~ frames ~ DMG seconds" +
  "ran ... in X s host time (Y DMG-emulated s, Z x real-time)"

### Bus + CPU changes

- `GbMemoryBus.LoadBios()` + `BiosEnabled` flag
- ReadByte: BiosEnabled && addr < 0x100 -> return BIOS bytes
- WriteIo 0xFF50 -> any non-zero bit permanently unmaps BIOS (real
  hardware is one-shot)
- LegacyCpu / JsonCpu Reset() switches based on `bus.BiosEnabled`:
  - true: cold start (all regs 0, PC=0, SP=0)
  - false: post-BIOS state (PC=0x100, A=0x01, F=0xB0, SP=0xFFFE)

### Verification

`apr-gb --bios=BIOS/gb_bios.bin --rom=blargg-cpu/cpu_instrs.gb --seconds=0.1
        --screenshot=result/gb/gb_bios_logo.png`

-> classic DMG boot screen: green background + "Nintendo(R)" text

### Caveat

Our BIOS finishes in ~0.1 DMG-second (real hardware is ~2.5s) because
the PPU's LY register is hardcoded to 0x90 (fake VBlank) -> the BIOS's
VBlank-polling loop completes instantly. Achieving real-hw timing
requires adding a GB scheduler (per-scanline PPU clock) — future work.
But BIOS hand-off + cart execution are correct (cpu_instrs serial
output "cpu_instrs / 01" is normal).

---

## 7. Cross-commit file change summary

`AprCpu.Core` (CPU framework):
- `IR/CpuStateLayout.cs` — added `PcWrittenFieldIndex` (i8 flag)
- `IR/Emitters.cs` — `Branch`/`WriteReg` emit flag set; new
  `ReadRegShiftByReg` op
- `IR/ArmEmitters.cs` — `BranchIndirectArm` flag set; `raise_exception`
  clears T; new `UpdateNz64` op
- `IR/OperandResolvers.cs` — LSL/LSR by 32 carry fix; shift-by-reg
  PC+12 helper
- `IR/BlockTransferEmitters.cs` — LDM loading R15 sets flag
- `Runtime/IMemoryBus.cs` — added `NotifyExecutingPc` +
  `NotifyInstructionFetch` default no-ops
- `Runtime/CpuExecutor.cs` — call bus before/after fetch; branched
  detection uses flag
- `Runtime/Gba/GbaMemoryBus.cs` — BIOS open-bus + sticky; HALTCNT;
  DISPSTAT toggle removed
- `Runtime/Gba/GbaSystemRunner.cs` — IRQ entry clears T; halt-aware loop

`AprGba.Cli` (GBA CLI):
- `Program.cs` — `--bios` / `--seconds` / 8 diagnostic flags
  (`--no-obj`, `--only-obj=N`, etc.); GbaTiming constants
- `Video/GbaPpu.cs` — full rewrite ~600 lines (per-layer composite +
  Mode 0/1/2 + OBJ + windowing + blending)
- `RomPatcher.cs` — new file, cart logo + checksum patch

`AprGb.Cli` (GB CLI):
- `Program.cs` — `--bios` / `--seconds`; GbTiming constants
- `Memory/GbMemoryBus.cs` — `LoadBios` / BiosEnabled / 0xFF50 unmap
- `Cpu/LegacyCpu.cs` + `Cpu/JsonCpu.cs` — Reset distinguishes cold-start
  vs post-BIOS

`spec/arm7tdmi/`:
- `groups/data-processing.json` — 14 RegRegShift-format instructions'
  read_reg(rn) changed to read_reg_shift_by_reg
- `groups/multiply-long.json` — 4 mnemonics get S-bit + update_nz_64
  appended

---

## 8. Verification snapshot

| ROM / Test | Result | Backend |
|---|---|---|
| 345 unit tests | all green | xUnit |
| jsmolka arm.gba (HLE boot) | All tests passed | apr-gba |
| jsmolka thumb.gba (HLE boot) | All tests passed | apr-gba |
| jsmolka arm.gba (BIOS LLE) | All tests passed @ 6s | apr-gba --bios |
| jsmolka thumb.gba (BIOS LLE) | All tests passed @ 6s | apr-gba --bios |
| jsmolka bios.gba (BIOS LLE) | All tests passed @ 6s | apr-gba --bios |
| BIOS startup logo @ 2.5s | mGBA-equivalent visual | apr-gba --bios |
| Mode 0 stripes.gba | blue vertical stripes, ok | apr-gba (HLE) |
| Mode 0 shades.gba | blue gradient, ok | apr-gba (HLE) |
| Blargg cpu_instrs.gb | passes (serial) | apr-gb |
| DMG BIOS logo | "Nintendo(R)" displayed, ok | apr-gb --bios |

PPU coverage:
- Mode 0 ok, Mode 1 ok, Mode 2 ok, Mode 3 ok, Mode 4 ok
- Mode 5 (small RGB555 bitmap, rarely used) — not done
- OBJ complete (normal + affine + 4bpp/8bpp + 1D/2D mapping)
- BLDCNT alpha/brighten/darken — done
- WININ/WINOUT + Window 0/1 + OBJ Window — done
- Mosaic — not done (rarely used, fill in later)

---

## 9. Possible next steps

In priority order:

1. **GB scheduler (per-scanline PPU clock)** — solves the "DMG BIOS
   runs too fast" problem; also paves the way for GB-side full LLE
   boot flow once added, with `--frames` becoming truly accurate
2. **Mode 5 + Mosaic** — fill in remaining PPU corner cases
3. **Audio (APU)** — hand-written, same approach as the PPU
4. **Phase 7 block-JIT** — pull GBA's 4.4 MIPS up to >= real-time (not
   needed for test ROM screenshots, but necessary for commercial games)
5. **Third CPU port** — MIPS R3000 / RISC-V or similar, additional
   verification of framework generality

---

## 10. One-line summary

**Phase 5 fully wrapped up**: BIOS LLE + PPU completion + GB CLI
alignment. The GBA side expanded from "test ROM CPU verification +
screenshot" to "real BIOS LLE + Nintendo logo visually matches mGBA +
arm/thumb/bios three ROMs all PASS". The GB side gained `--bios` and
`--seconds` to align CLI interfaces. **The framework's claim "swap CPU
+ swap platform = swap JSON + swap emitter + swap PPU" is now
end-to-end verified on both platforms**.
