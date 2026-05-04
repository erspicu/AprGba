# Phase 5 wrap-up — GBA test-ROM screenshot MVP achieved (2026-05-02)

> Records the state after all Phase 5 sub-slices wrapped up. From
> Phase 4.5 (GB passes Blargg) to here was ~half a day of work; the
> goal was "get jsmolka running and take a screenshot", achieved.

---

## 1. Goal recap

Phase 5's pre-launch scope decision (recorded in `MD/design/03-roadmap.md`):

- GBA-side goal = run jsmolka test ROMs + verify via PNG screenshot
- **Not** chasing commercial games, **not** chasing more homebrew compatibility
- **Not** doing GUI / 60fps loop / real-time playback
- **No** audio / gamepad input
- But **must support LLE BIOS boot** (boot from a real BIOS file —
  high credibility)
- PPU **hand-written**, not driven by JSON spec

Minimum viable acceptance criteria:

```
apr-gba --rom=jsmolka.gba --cycles=N --screenshot=out.png
-> matches mGBA's screenshot at the same ROM and same cycle count
```

**Result**: Both jsmolka ROMs display `All tests passed`, behaviour
matches mGBA.

---

## 2. Wrap-up progress (4 sub-slices)

### 5.1 — IRQ + LLE BIOS foundation

Extended `GbaMemoryMap`:
- IO offset constants expanded (DISPCNT/DISPSTAT/VCOUNT/BG0..3 control
  + scroll/IE/IF/IME/POSTFLG/HALTCNT/WAITCNT)
- DISPSTAT bit constants (VBLANK_FLG / HBLANK_FLG / VCOUNT_FLG + each
  IRQ enable bit)
- `IrqVectorBase = 0x18`
- New `GbaInterrupt` enum (VBlank=0..GamePak=13, mapped to IE/IF bit
  positions)

Extended `GbaMemoryBus`:
- `LoadBios(byte[])` — Phase 5 LLE entry point
- `RaiseInterrupt(GbaInterrupt)` — sets the corresponding IF bit
- `HasPendingInterrupt()` — checks IME && (IE & IF)
- `WriteIoHalfword` implements two GBATEK quirks:
  - IF "write 1 to clear" semantics
  - DISPSTAT bit 0..2 read-only (only IRQ enable + VCount target writable)

### 5.2 — DMA controller

New file `GbaDmaController.cs`, 4 channels:
- Immediate mode (timing=00): `OnCntHWrite` detects enable 0->1
  transition and fires immediately
- VBlank/HBlank mode: armed, awaits scheduler trigger
- 16-bit / 32-bit transfer
- SAD/DAD INC / DEC / FIXED modes
- Word count 0 -> 0x4000 (DMA0..2) / 0x10000 (DMA3) per GBATEK
- IRQ on end (bit 14) -> `bus.RaiseInterrupt(Dma{n})`
- Auto-disables channel after transfer completes

Skipped (does not affect test ROMs): repeat, DMA3 Game Pak DRQ, Audio
FIFO DMA, cycle stealing.

### 5.3 — Cycle scheduler + IRQ delivery

New file `GbaScheduler.cs`:
- GBA timing constants (1232 cycles/scanline, 228 lines/frame, 280896
  cycles/frame)
- `Tick(cycles)` advances scanline + cycle-in-scanline
- VBlank entry @ scanline 160 -> `RaiseInterrupt(VBlank)` +
  `Dma.TriggerOnVBlank()`
- HBlank entry @ within visible scanline -> `RaiseInterrupt(HBlank)` +
  `Dma.TriggerOnHBlank()`
- VCount match -> `RaiseInterrupt(VCount)`
- Maintains DISPSTAT three FLG bits + VCOUNT register

New file `GbaSystemRunner.cs`:
- Wires together `CpuExecutor` + `GbaMemoryBus` + `GbaScheduler` +
  `Arm7tdmiBankSwapHandler`
- `RunCycles(budget, cyclesPerInstr=4)`: each step runs cpu.Step ->
  scheduler.Tick -> DeliverIrqIfPending
- `RunFrames(n)` convenience wrapper
- `DeliverIrqIfPending()`: standard ARM IRQ entry:
  1. Check `bus.HasPendingInterrupt()` and CPSR.I not masked
  2. Save SPSR_irq = old CPSR
  3. Switch CPSR mode = IRQ (0x12), set I bit
  4. `swap.SwapBank(state, oldMode, IrqMode)` swaps banked R13/R14
  5. R14_irq = next-PC + 4 (standard SUBS PC, LR, #4 return convention)
  6. PC = 0x18

### 5.4 — apr-gba CLI

New project `src/AprGba.Cli`:

**`Program.cs`** — headless CLI:
```
apr-gba --rom=<path.gba> [--bios=<path.bin>]
        [--cycles=N | --frames=N]
        [--screenshot=out.png] [--info]
```
- No BIOS provided -> `InstallMinimalBiosStubs` + jump to ROM entry @
  0x08000000
- BIOS provided -> `LoadBios` + start from BIOS entry @ 0x00000000
- Reports setup time (spec compile + MCJIT) separately from wall-clock
- On exit prints PC + R0..R15 + IRQ delivery count + frame count

**`Video/PngWriter.cs`** — PNG encoder taking RGB triples directly (no
palette indirection needed, since the GBA framebuffer is already
15-bit RGB)

**`Video/GbaPpu.cs`** — minimal PPU:
- Mode 3: 240x160 RGB555 framebuffer (VRAM @ 0x06000000)
- Mode 4: 8-bit indexed + PRAM palette
- Other modes -> black screen (filled in later in Phase 8)
- "Snapshot from current VRAM at call time" design, no scanline timing

---

## 3. Verification results

```bash
apr-gba --rom=test-roms/gba-tests/arm/arm.gba   --cycles=300000 --screenshot=result/gba/arm.png
apr-gba --rom=test-roms/gba-tests/thumb/thumb.gba --cycles=300000 --screenshot=result/gba/thumb.png
```

| ROM | R12/R7 | Screenshot | Matches mGBA |
|---|---|---|---|
| arm.gba | R12=0 | "All tests passed" | yes |
| thumb.gba | R7=0 | "All tests passed" | yes |

Performance:
- Setup time (spec compile + MCJIT): ~250 ms / ROM
- Run time (300K cycles): ~250 ms / ROM
- **Total < 1 second / ROM**

Regression tests: **346/346 unit tests all green** (Phase 5 added 15
new tests — bus IRQ + DMA + scheduler + SystemRunner).

> Both jsmolka ROMs use Mode 4 (paletted 8-bit framebuffer), which our
> minimal PPU happens to implement. Future ROMs that use Mode 0
> (tile-based BG) need Phase 8 completion.

---

## 4. Architecture overview

```
+---------------------------------------------------------+
| apr-gba CLI (src/AprGba.Cli)                            |
|  ParseArgs -> BootCpu -> RunCycles -> RenderFrame -> PNG|
+---------------------------------------------------------+
                         |
            +------------+------------+
            v                         v
+----------------------+   +------------------------------+
| GbaSystemRunner      |   | GbaPpu (Mode 3 / 4)          |
|  loop:               |   |  RenderFrame(bus)            |
|   cpu.Step()         |   |   -> Framebuffer[240*160*3]  |
|   scheduler.Tick(N)  |   +------------------------------+
|   DeliverIrqIfPending|
+----------------------+
       |       |       |
       v       v       v
+----------+ +----------+ +------------------------------+
|CpuExecutor| |GbaScheduler| |GbaMemoryBus                  |
|           | | Tick ->    | | ReadByte/Halfword/Word       |
| Step()    | |  VBlank    | | WriteIoHalfword (IF, DISPSTAT)|
| ReadGpr   | |  HBlank    | | RaiseInterrupt               |
| WriteStatus| |  VCount   | | HasPendingInterrupt          |
+-----+-----+ +----+-----+ | Dma : GbaDmaController       |
      |            |       +------+-----------------------+
      |            v              |
      |     +-----------------+   |
      |     | Arm7tdmiBank... |   |
      |     | SwapBank(s,o,n) |   |
      |     +-----------------+   |
      |                           v
      |            +----------------------------------+
      |            | GbaDmaController                 |
      |            |  OnCntHWrite (immediate fire)    |
      |            |  TriggerOnVBlank/HBlank          |
      |            |  ExecuteTransfer (R-M-W mem)     |
      |            +----------------------------------+
      v
+----------------------------------------------------+
| AprCpu.Core JIT pipeline                           |
|  SpecCompiler.Compile(spec/arm7tdmi/cpu.json)      |
|   -> LLVM IR module -> MCJIT -> native fn pointers |
| HostRuntime + extern shim binding                  |
+----------------------------------------------------+
```

Each layer has its own unit tests; the integration layer is guarded
by `GbaSystemRunnerTests` + running real jsmolka ROMs
(`RunFrames_AdvancesSchedulerAndStillReachesArmGbaHalt`).

---

## 5. Comparison with the GB side

The two sides are shape-identical — the design "framework + JSON spec
+ a CLI" is verified to apply to two completely different platforms:

|  | GB (DMG) | GBA |
|---|---|---|
| CPU spec | `spec/lr35902/*.json` | `spec/arm7tdmi/*.json` |
| Custom emitters | `Lr35902Emitters.cs` | `ArmEmitters.cs` |
| Memory bus | `AprGb.Cli/Memory/GbMemoryBus.cs` | `AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs` |
| DMA controller | (none, GB doesn't need it) | `GbaDmaController.cs` |
| Cycle scheduler | (embedded in LegacyCpu) | `GbaScheduler.cs` |
| System runner | (embedded in LegacyCpu / JsonCpu) | `GbaSystemRunner.cs` |
| PPU | `GbPpu.cs` (BG mode 0) | `GbaPpu.cs` (Mode 3 / 4) |
| CLI | `apr-gb` | `apr-gba` |
| Test ROM | Blargg cpu_instrs (11/11) | jsmolka arm/thumb |
| Screenshot verification | `result/gb/{legacy,json-llvm}/` | `result/gba/{arm,thumb}.png` |

---

## 6. Possible next steps

In priority order:

1. **Phase 8 PPU completion** — Mode 0 tile-based BG + sprite layer +
   4 BG layers + window + blend. This enables ROMs using Mode 0 (most
   commercial homebrew and demos). Architecturally same approach as
   GB-side `GbPpu`
2. **Run BIOS-equipped LLE boot** — get a GBA BIOS file and run
   `apr-gba --bios=gba_bios.bin --rom=arm.gba`, verify behaviour after
   BIOS intro completes and ROM entry is reached matches (credibility
   boost)
3. **Phase 7 block-JIT** — pull the current 4.4 MIPS (~40% real-time)
   up to >= real-hw speed. Optional for the test ROM screenshot goal;
   necessary for actual gameplay
4. **Add more GBA test ROMs** (Mooneye GBA, FuzzARM, ...) to widen
   correctness verification

---

## 7. One-line summary

**The GBA-side MVP "test ROM CPU verification + screenshot" is
complete — from Phase 5.1 (IRQ foundation) to 5.4 (apr-gba CLI), in
one sweep. jsmolka arm.gba/thumb.gba both display "All tests passed"
in screenshots, behaviour matches mGBA, 346/346 unit tests all green.**

Phase 4.5 (GB) and Phase 5 (GBA) wrapped up on dual tracks; the
framework's claim "swap CPU = swap JSON + write emitter + wire
platform-specific bus/scheduler/PPU" is verified on both platforms.
