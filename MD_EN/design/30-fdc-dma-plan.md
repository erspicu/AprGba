# Phase 30 — 8272 FDC + 8237 DMA controller emulation

> **Status**: ✅ **COMPLETE — END-TO-END FreeDOS BOOT VIA REAL BIOS**
> (2026-05-16). Detailed plan + opcode tables + Gemini consultation
> summaries live in the Chinese counterpart
> [`MD/design/30-fdc-dma-plan.md`](../../MD/design/30-fdc-dma-plan.md);
> this file is a brief English mirror.

## Goal

Real-mode PC/XT BIOS (`pcxtbios.bin`) completes an INT 19h
bootstrap from a real .img file. End test:
`apr-pc --bios=BIOS/firmware/pcxtbios.bin
--floppy-a=BIOS/freedos-1.3-floppy.img` boots FreeDOS with no HLE
BIOS intercept.

## Result

End test passes. MDA framebuffer text preview after ~100M CPU cycles:

```
| FreeCom version 0.85a - WATCOMC - XMS_Swap [Jul 10 2021 19:28:06] |
```

Full chain: BIOS POST → INT 19h via real FDC → boot sector loaded →
self-relocate to 1FE0:7C00 → read root dir + FAT via real INT 13h
→ FAT12 cluster walker finds KERNEL.SYS → load kernel → FreeDOS
kernel runs → loads COMMAND.COM → COMMAND.COM banner printed.
Screenshot: `result/pc/30-rolfix-realbios.png`.

## Scope

In: ~600 lines of C# emulating just enough of an 8272 / μPD765A FDC
and an 8237 DMA controller (channel 2 only) so real BIOS POST's
INT 19h can complete a sector read. Plus three follow-up sub-fixes
uncovered while debugging.

Out: format / write / verify FDC commands, multi-drive, density
auto-detect beyond 1.44MB, DMA channels 0 / 1 / 3, cycle-accurate
FDC timing, FORMAT TRACK.

## Design (per Gemini 2026-05-16 consultation)

Log: `tools/knowledgebase/message/20260516_004919.txt`.

### Minimum FDC command set

| Opcode | Mnemonic | Result phase | IRQ 6 |
|---|---|---|---|
| 0x03 | SPECIFY | none | no |
| 0x04 | SENSE DRIVE STATUS | 1 byte ST3 | no |
| 0x07 | RECALIBRATE | none | yes |
| 0x08 | SENSE INTERRUPT STATUS | ST0 + PCN | no (clears IRQ) |
| 0x0A | READ ID | 7 bytes ST0/ST1/ST2/C/H/S/N | yes |
| 0x0F | SEEK | none | yes |
| 0x06 + variants | READ DATA | 7 bytes | yes |
| (any other) | Invalid Command | 1 byte ST0=0x80 | no |

### DMA channel 2 register coverage

`0x04` base (16-bit via flip-flop), `0x05` count, `0x08` status,
`0x0A` mask, `0x0B` mode, `0x0C` clear flip-flop, `0x0D` master
reset, `0x81` ch2 page register. Per Gemini's "synchronous burst
cheat" — when FDC READ DATA executes, compute physical address from
`(page << 16) | base`, memcpy from DiskImage to RAM, update DMA
state, assert IRQ 6.

## Implementation chain

| Sprint | Deliverable | Commit |
|---|---|---|
| 30.1-30.5 | Fdc8272 + Dma8237 MVP | `12ae222` |
| 30.6a | IRQ deassert fix (no spurious second ISR fire) | `0ab519d` |
| 30.6b | Debug tools: `--trace-cpu-cs`, `--watch-mem`, `--watch-read`, wider memory dump | `c18bcb4` |
| 30.6c | **CPU `ROL r/m16, CL` count > 1 fix** | `e62a462` |
| 30.6 (end-to-end) | pcxtbios.bin + FreeDOS boot to COMMAND.COM | `e62a462` |

## The critical bug — CPU `ROL r/m16, CL` with count > 1

The FDC + DMA emulation (`12ae222`) was functionally correct from
day one. End-to-end boot took two more days because pcxtbios.bin
exposed a CPU emulation bug we hadn't hit before:

`X86ShiftRotateW16CountClEmitter` had a TODO stub that delegated
`ROL r/m16, CL` with `count != 1` to a count=1 IR (shift left by 1
regardless of CL). The comment said this was "correct for count=1
which covers most real code."

pcxtbios.bin INT 13h disagrees. It uses `MOV CL, 4; ROL AX, CL` to
split the caller's 16-bit ES segment into the 8237 DMA controller's
16-bit base register + 4-bit page register:

```
F000:ED5F  MOV AX, [BP+0xC]   ; AX = caller's ES (= 0x1FE0)
F000:ED62  MOV CL, 4
F000:ED64  ROL AX, CL         ; expected 0xFE01; buggy → 0x3FC0
F000:ED66  ...                ; subsequent base/page arithmetic
F000:ED75  OUT 0x04, AL       ; DMA base low byte
```

With the buggy ROL, the BIOS computed DMA base 0xA360 instead of
the correct 0x251A0. FDC wrote sector data to the wrong physical
memory area. Subsequent boot sector REP MOVSB (which expects
sectors at the BIOS-loaded ES:BX) read zeros and copied zeros to
its working buffer at segment 0x60. The FAT12 cluster walker
following all-zero data hit `cluster 0 < end-of-chain 0x0FF8` and
looped forever.

Fix: proper count-based ROL using `lhs << n | lhs >> (16 - n)` with
`n = CL & 15`.

## Cross-phase dependencies for real-BIOS boot

The pcxtbios.bin → FreeDOS boot path requires ALL of:

1. Phase 28.0-28.7 (HLE BIOS infrastructure — still partially used)
2. Phase 28.IO (port I/O dispatch via `PcPortBus` extern routing)
3. Phase 29 i8087 extension (BIOS POST FPU detection)
4. Phase 29-supp port 0x3BA/0x3DA retrace bit + MDA framebuffer
   auto-detect (BIOS POST text output)
5. Phase 30 8272 FDC + 8237 DMA (this doc)
6. Phase 30.6c CPU ROL fix (uncovered debugging Phase 30)

Removing any one breaks the real-BIOS boot. The HLE BIOS path
(`--bios-mode=hle`, no `--bios=`) still works without 29/30/30.6c
because HLE INT 13h talks to DiskImage directly without going
through FDC/DMA/ROL.

## Why Phase 30 matters for the framework

Validates that the framework can run **unmodified production BIOS
ROM code**. `pcxtbios.bin` is a public test BIOS (not the original
IBM ROM) but follows IBM PC/XT conventions — port I/O sequences,
INT handler conventions, the ROL trick — all 1980s real-world code.
Booting it end-to-end proves the framework is polished enough that
generic x86-16 BIOS code completes POST + bootstraps a real OS.

## Deferred but related

- W8 (8-bit) `ROL r/m8, CL` with count > 1 has the same stub
  (line ~7181 of `X86_16Emitters.cs`). DOS code rarely uses 8-bit
  ROL with variable count but should be fixed for completeness.
- ROR / RCL / RCR variants in both W8 + W16 paths also stubbed.
  ROR can be done similarly. RCL / RCR need carry-bit handling in
  the 9-bit / 17-bit rotation ring.
- None of these are hit in the FreeDOS boot path.

## Cross-references

- Phase 28 closure: `MD/performance/202605152200-pc-emulator-freedos-boot.md`
- Phase 28.IO: `MD/performance/202605152230-pc-emulator-phase-28-io.md`
- Phase 29 closure: `MD/performance/202605160100-x87-fpu-functional-complete.md`
- Gemini consultation: `tools/knowledgebase/message/20260516_004919.txt`
