# Phase 30 closure — real BIOS + FreeDOS end-to-end boot

**Date**: 2026-05-16  
**Phase**: 30 (8272 FDC + 8237 DMA + CPU ROL fix)  
**Status**: ✅ Functional — pcxtbios.bin + freedos-1.3-floppy.img boots to COMMAND.COM banner with zero HLE BIOS intercept.  
**Plan**: [`MD/design/30-fdc-dma-plan.md`](../design/30-fdc-dma-plan.md)  
**Predecessor closure notes**:
- Phase 28 HLE: [`202605152200-pc-emulator-freedos-boot.md`](202605152200-pc-emulator-freedos-boot.md)
- Phase 28.IO: [`202605152230-pc-emulator-phase-28-io.md`](202605152230-pc-emulator-phase-28-io.md)
- Phase 29 FPU: [`202605160100-x87-fpu-functional-complete.md`](202605160100-x87-fpu-functional-complete.md)

## Deliverables

| Item | Commit | Notes |
|---|---|---|
| `Fdc8272` + `Dma8237` MVP | `12ae222` | 7 commands (SPECIFY/SENSE INT/RECALIBRATE/SEEK/READ DATA/READ ID/SENSE DRIVE STATUS), synchronous burst DMA ch2 per Gemini consultation, IRQ 6 wiring through existing PIC8259A |
| IRQ deassert fix | `0ab519d` | Result-phase FIFO read no longer re-fires ISR (was causing double-entry into BIOS INT 0Eh handler) |
| Debug tools | `c18bcb4` | `--trace-cpu-cs=`, `--watch-mem=LO:HI`, `--watch-read=LO:HI`, wider HeadlessRunner memory dump (±32 bytes around IP, 8-row orig-vs-copy diff of boot sector) |
| **CPU ROL r/m16, CL count > 1 fix** | `e62a462` | `X86ShiftRotateW16CountClEmitter` now properly count-rotates via `lhs << n \| lhs >> (16 - n)` instead of forwarding to count=1 stub |
| End-to-end boot | `e62a462` | Screenshot `result/pc/30-rolfix-realbios.png` |

## Run command

```
dotnet run --project src/AprPc.Cli -- \
  --bios=BIOS/firmware/pcxtbios.bin \
  --floppy-a=BIOS/freedos-1.3-floppy.img \
  --headless --seconds=12
```

Visible on the MDA framebuffer after ~100M CPU cycles:

```
| FreeCom version 0.85a - WATCOMC - XMS_Swap [Jul 10 2021 19:28:06] |
```

## The bug that took the longest — `ROL r/m16, CL` with count > 1

The FDC + DMA emulation (`12ae222`) was functionally correct from day one.
Three days of debugging followed because pcxtbios.bin exposes a CPU bug none
of the earlier test ROMs hit.

### Symptom

After `12ae222`, BIOS POST printed `Insert BOOT disk in A:` and INT 19h
read the boot sector. The boot sector was supposed to:

1. Self-relocate from 0000:7C00 to 1FE0:7C00 (REP MOVSW).
2. Far-jump to 1FE0:7C00 + small offset.
3. Read root dir + FAT from disk.
4. Walk FAT12 cluster chain to find KERNEL.SYS.
5. Load KERNEL.SYS at 0060:0000.
6. Far-jump into kernel.

Instead it spun at step 4 reading all-zero cluster entries.

### False leads

1. **REP MOVSW broken** — forensic dump of the relocated boot sector at
   0x27A00 showed 224 of 512 bytes differing from the original at 0x07C00.
   A standalone `30-rep-movsw-test.com` ROM disproved this — REP MOVSW
   emitter is correct. The differences turned out to be downstream
   corruption from the FAT walker writing back zeros via STOSW.

2. **ES register not incrementing** — initial trace comparison between HLE
   and real-BIOS paths read as if ES was pinned at 0x0060. Closer reading
   showed ES evolved identically in both paths (0x60 → 0x80 → 0xA0 ...) —
   misread on my part.

### Real cause (found via `--watch-mem` + `--trace-cpu-cs=F000`)

`X86ShiftRotateW16CountClEmitter` had a TODO stub that, for ROL with
count != 1, just emitted the count=1 IR (single left rotate regardless of
CL). The original comment claimed `count=1 covers 99% of real code`.

pcxtbios.bin INT 13h handler at F000:ED5F-ED75 disagrees. It uses
`MOV CL, 4; ROL AX, CL` to split the caller's 16-bit segment value into
the 8237 DMA controller's 16-bit base register (lo + hi bytes) + 4-bit
page register:

```
F000:ED5F  MOV  AX, [BP+0xC]   ; AX = caller's ES, e.g. 0x1FE0
F000:ED62  MOV  CL, 4
F000:ED64  ROL  AX, CL         ; expected 0xFE01; old stub gave 0x3FC0
F000:ED66  ...                 ; subsequent base/page arithmetic
F000:ED75  OUT  0x04, AL       ; DMA base lo
```

With the broken ROL, the BIOS programmed DMA base 0xA360 instead of the
correct 0x251A0. The FDC dutifully transferred sector data to physical
address 0xA360 (right in the middle of MDA video memory). Boot sector
REP MOVSB then expected sectors at the original ES:BX (0x251A0) and
copied uninitialised RAM. FAT12 cluster walker followed `cluster 0 < 0x0FF8`
and looped forever.

### Fix

Proper count-based rotation in IR:

```csharp
var nWide  = builder.BuildAnd(clClamp, const_i8(15), "rolc16_n");
var n16    = builder.BuildZExt(nWide, i16, "rolc16_n16");
var left   = builder.BuildShl(lhs, n16, "rolc16_left");
var rsh    = builder.BuildSub(const_i16(16), n16, "rolc16_rsh");
var right  = builder.BuildLShr(lhs, rsh, "rolc16_right");
result     = builder.BuildOr(left, right, "rolc16_r");
// CF = result LSB; OF = result MSB XOR CF
```

Standalone `30-rol-cl-test.com` verifies 4 cases (0x1FE0 ROL 4 = 0xFE01,
0xC123 ROL 8 = 0x23C1, 0x0001 ROL 15 = 0x8000, 0xFFFF ROL 4 = 0xFFFF).
All pass after the fix.

## Cross-phase dependencies — real-BIOS chain

Real-BIOS path requires ALL of the following six pieces. Removing any one
breaks boot:

1. **Phase 28.0-28.7** — HLE BIOS infrastructure (still partially used for
   INT 1Ah time-of-day even when real BIOS is loaded).
2. **Phase 28.IO** — port I/O dispatch via `PcPortBus` extern routing,
   without which pcxtbios.bin POST can't talk to PIC/PIT/PPI/MDA at all.
3. **Phase 29** — i8087 extension. BIOS POST executes `FNINIT / FNSTSW`
   early to detect an 8087; without Phase 29 this is invalid opcode.
4. **Phase 29-supp** — port 0x3BA/0x3DA retrace bit + MDA framebuffer
   auto-detect, needed by BIOS POST text output.
5. **Phase 30** — 8272 FDC + 8237 DMA (this phase).
6. **Phase 30.6c** — CPU `ROL r/m16, CL` count > 1 fix (uncovered during
   this phase, not predictable from spec inspection).

The HLE BIOS path (`--bios-mode=hle`, no `--bios=`) still works without
29/30/30.6c because HLE INT 13h talks to DiskImage directly and doesn't
go through FDC/DMA/ROL.

## Why this matters for the framework

This is the first time the framework has run **unmodified production BIOS
ROM code**. `pcxtbios.bin` is a public test BIOS (not original IBM) but
follows IBM PC/XT conventions — port I/O sequences, INT handler
conventions, the ROL trick. All 1980s real-world code, none of it written
to suit emulator quirks. Booting it end-to-end proves the framework is
polished enough that generic x86-16 BIOS code completes POST + bootstraps
a real OS without per-quirk patches.

## Deferred

- W8 (8-bit) `ROL r/m8, CL` with count > 1 still uses the same stub
  pattern (`X86_16Emitters.cs` ~line 7181). DOS code rarely uses 8-bit
  variable-count ROL but it should be fixed for completeness.
- ROR / RCL / RCR variants in both W8 and W16 paths still stubbed. ROR
  follows the same pattern as ROL. RCL / RCR need carry-bit handling
  inside the 9-bit / 17-bit rotation ring.
- None of these are hit by FreeDOS or pcxtbios.bin POST.

## References

- Plan doc: [`MD/design/30-fdc-dma-plan.md`](../design/30-fdc-dma-plan.md)
- Gemini consultation: `tools/knowledgebase/message/20260516_004919.txt`
- Screenshot: `result/pc/30-rolfix-realbios.png`
- Standalone test ROMs: `test-roms/x86/src/30-rep-movsw-test.asm`,
  `test-roms/x86/src/30-rol-cl-test.asm`
