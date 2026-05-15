# Phase 30 — 8272 FDC + 8237 DMA controller emulation

> **Goal**: Real-mode PC/XT BIOS (`pcxtbios.bin`) can complete an
> INT 19h bootstrap from a real .img file, loading the boot sector to
> 0:7C00 and jumping to it. End test: `apr-pc --bios=pcxtbios.bin
> --floppy-a=freedos-1.3-floppy.img` boots FreeDOS without HLE BIOS
> intercepts.
>
> **Status**: 📋 planning (2026-05-16). Phase 29 closed; this is the next
> integration milestone.
>
> **Predecessor**:
> - Phase 28.IO ([port I/O dispatch](../performance/202605152230-pc-emulator-phase-28-io.md))
>   established PcPortBus dispatch. Ports 0x3F0-0x3F7 (FDC) and
>   0x00-0x0F + 0x81-0x8F (DMA) currently return 0xFF (open bus).
> - Phase 29-supp (port 0x3BA/0x3DA retrace bit + MDA framebuffer
>   auto-detect) shipped real BIOS POST text output. Currently CPU
>   parks at F000:E858 (INT 16h kbd wait) because INT 19h's call to
>   real-FDC INT 13h fails on open-bus FDC ports.

## Scope

In: ~600 lines of C# emulating just enough of an 8272 / μPD765A FDC
and an 8237 DMA controller (channel 2 only) so real BIOS POST's
INT 19h can complete a sector read.

Out: format/write/verify, multi-drive, density auto-detect beyond
1.44MB, DMA channels 0/1/3, cycle-accurate FDC timing, FORMAT TRACK
support.

## Design per Gemini consultation (2026-05-16)

Full log: `tools/knowledgebase/message/20260516_004919.txt`.

### Minimum command set (6 active + 1 invalid handler)

| Opcode | Mnemonic | Result phase | IRQ 6 | Notes |
|---|---|---|---|---|
| 0x03 | SPECIFY | none | no | Step rate / head load time; pure config |
| 0x04 | SENSE DRIVE STATUS | 1 byte ST3 | no | Returns drive ready/track 0/write protect |
| 0x07 | RECALIBRATE | none | **yes** | Seeks to cyl 0; cleared by SENSE INT |
| 0x08 | SENSE INTERRUPT STATUS | 2 bytes ST0+PCN | no | Clears IRQ 6; required after RECALIBRATE/SEEK |
| 0x0A | READ ID | 7 bytes ST0/ST1/ST2/C/H/S/N | yes | Real BIOS uses for media detect; fake from "current seek" position |
| 0x0F | SEEK | none | **yes** | Move head to specified cyl; cleared by SENSE INT |
| 0x46/0x66/0xE6 | READ DATA | 7 bytes | yes | Multi-sector read; bit pattern: 0x40 MFM, 0x20 SK, 0x80 MT, 0x06 opcode |
| (any unmapped) | — | 1 byte ST0=0x80 | no | Force-known-state invalid-command response |

### MSR (0x3F4) state machine

```
RQM (b7) | DIO (b6) | NDM (b5) | CB (b4) | D3B-D0B (b3-b0)
```

| State | RQM | DIO | NDM | CB | Notes |
|---|---|---|---|---|---|
| Idle | 1 | 0 | 0 | 0 | Ready for command byte |
| Command phase (mid-write) | 1 | 0 | 0 | 1 | Between command bytes |
| Execution (DMA mode) | 0/1 | 0 | 0 | 1 | RQM follows DMA DRQ; we burst so always 1 |
| Result phase | 1 | 1 | 0 | 1 | Ready for host to read |
| Result drained | 1 | 0 | 0 | 0 | Back to idle |

BIOS poll-write idiom: `wait (MSR & 0xC0) == 0x80` then write to 0x3F5.
BIOS poll-read idiom: `wait (MSR & 0xC0) == 0xC0` then read from 0x3F5.

### DOR (0x3F2) bits

| Bit | Field | Effect |
|---|---|---|
| 1:0 | Drive Select | 00=A, 01=B, 10=C, 11=D |
| 2 | nRESET | 0 → reset FDC; 0→1 transition asserts IRQ 6 |
| 3 | DMAEN | Enable IRQ + DMA |
| 4 | Motor A | 1 = drive A motor on |
| 5 | Motor B | 1 = drive B motor on |
| 6 | Motor C | — |
| 7 | Motor D | — |

### 8237 DMA channel 2 register map (we use only ch2)

| Port | Register | Notes |
|---|---|---|
| 0x04 | Ch2 Base Addr (16-bit via flip-flop) | LO then HI |
| 0x05 | Ch2 Word Count (16-bit via flip-flop) | LO then HI; count = N-1 |
| 0x08 | Status (read) / Command (write) | Bit 2 = ch2 TC reached |
| 0x0A | Mask (write) | b0-b1 = channel select, b2 = mask bit |
| 0x0B | Mode (write) | Per-channel mode (read/write/auto-init/etc.) |
| 0x0C | Clear flip-flop (write any) | Reset LO/HI toggle |
| 0x0D | Master Reset (write any) | Reset whole DMAC |
| 0x81 | Ch2 Page Register | High 4 bits of 24-bit DMA address |

### DMA "synchronous burst" cheat

Per Gemini's guidance: real DMA is byte-by-byte interleaved with CPU,
but for BIOS boot we can do the entire sector transfer in one go
during the FDC execution phase. Algorithm when handling READ DATA:

```
1. Compute linear addr = (page[0x81] << 16) | base[0x04]
2. Read count = (count[0x05] + 1) bytes from disk image at the
   command's CHS position
3. memcpy to RAM[linear addr .. linear addr + count]
4. Update DMA state:
     base[0x04] += count
     count[0x05] = 0xFFFF (TC reached)
     status[0x08] |= 0x04 (ch2 TC)
5. Transition FDC to result phase + assert IRQ 6
```

### IRQ 6 wiring

- BIOS unmasks IRQ 6 via OCW1 (`OUT 0x21, mask`) before first command.
- We assert IRQ 6 via `Pic8259.RaiseIrq(6)` when:
  - DOR nRESET goes 0→1
  - SEEK / RECALIBRATE completes
  - READ DATA execution phase completes (→ result phase)
- We clear IRQ 6 when CPU reads the first byte from 0x3F5 in result
  phase, OR when CPU executes SENSE INTERRUPT STATUS (no result
  phase commands).

## Implementation plan (sprints)

| Sprint | Deliverable |
|---|---|
| 30.1 | `Fdc8272` skeleton + DOR/MSR ports + SPECIFY/SENSE INT — BIOS POST sees "FDC alive" |
| 30.2 | RECALIBRATE + SEEK + IRQ 6 wiring (no data transfer yet) |
| 30.3 | `Dma8237` skeleton + ch2 register file + flip-flop |
| 30.4 | READ DATA command + synchronous burst transfer + sector read from disk image |
| 30.5 | READ ID + SENSE DRIVE STATUS + invalid-command 0x80 fallback |
| 30.6 | End-to-end: pcxtbios.bin INT 19h reads FreeDOS boot sector → boot |
| 30.7 | Closure + test ROM matrix + screenshot |

### Status: 30.1-30.5 ✅ DONE (initial cut, 2026-05-16)

Functional MVP shipped in one push (`<next commit>`). pcxtbios.bin
POST completes; INT 19h loads FreeDOS boot sector via real FDC; boot
sector self-relocates to 1FE0:7C00 and starts reading root dir + FAT
sectors via real INT 13h → real FDC → real DMA. 24 FDC READ DATA
commands execute successfully with correct first-byte signatures:
boot sector starts `EB 3C 90 46 52 44 4F 53` (= "FRDOS5.1" OEM
signature), root dir first entry shows `46 44 31 33 2D 42 4F 4F 54
20 20 08` (= volume label "FD13-BOOT" with attribute 0x08).

### 30.6 — known stuck point: FAT walker infinite loop

After reading all 14 root dir sectors (LBA 19-32) + 9 FAT1 sectors
(LBA 1-9) via single-sector READ DATA commands (DMA count = 511,
each sector overwriting previous at fixed dest 0x0A360), CPU enters
infinite loop at `1FE0:7D04`:

```
1FE0:7D04: AD          LODSW
1FE0:7D05: 73 04       JAE +4
1FE0:7D07: B1 04       MOV CL, 4
1FE0:7D09: D3 E8       SHR AX, CL
1FE0:7D0B: 80 E4 0F    AND AH, 0F
1FE0:7D0E: 3D F8 0F    CMP AX, 0FF8h
1FE0:7D11: 72 E8       JC -24
```

Classic FAT12 cluster-chain walker. With AX=0 always (data at the
walker's SI pointer is zeros), AND AH,0F → 0, CMP 0FF8 → less than,
JC takes branch → infinite loop on cluster 0.

The 24-read pattern + same fixed DMA destination 0x0A360 across all
reads suggests the boot sector is using 0x0A360 as a single-sector
scratch buffer, overwriting it for each new sector. If the walker
expects FAT to be loaded fully (e.g., to 9 different memory locations),
the assumed memory layout in the boot sector might require multi-sector
INT 13h reads where the DMA base auto-advances. Need to investigate:
1. Does pcxtbios.bin's INT 13h handler honor multi-sector requests
   correctly? If AL=9 in INT 13h call, does it issue ONE FDC READ
   DATA with EOT=9 and DMA count=4607, or 9 separate FDC commands
   with single-sector DMA counts? Trace suggests latter.
2. Does the FreeDOS boot sector expect a single buffer or 9 distinct
   memory areas for FAT?

Likely path forward: implement multi-sector READ DATA where FDC
transfers continuous bytes until DMA TC. Currently we cap at
`Math.Min(dmaBytes, ...)` which is 512 for single-sector. If DMA
count = 4607, we should transfer all 9 sectors in one burst.

### Deferred

- Write support (WRITE DATA, FORMAT TRACK)
- HDD via FDC equivalent (Phase 30 is floppy-only)
- Cycle-accurate FDC timing (we burst synchronously)
- Multi-drive support beyond A:

## Sub-phase boundaries

- After 30.2: BIOS POST writes to DOR + reads MSR + issues SPECIFY/
  RECAL/SENSE INT successfully. BIOS doesn't yet read disk; it'd
  proceed past FDC init then later issue READ DATA which times out.
- After 30.4: BIOS POST INT 19h successfully loads boot sector. Boot
  sector executes (likely fails on later disk reads from DOS kernel).
- After 30.6: FreeDOS boots end-to-end via real BIOS, mirror of HLE
  path's 28.8 milestone.

## Open questions

- pcxtbios.bin geometry assumptions — does it know about 1.44MB
  (18 sectors/track) or only 360KB / 720KB / 1.2MB? If it caps at
  9-15 sectors/track our READ DATA would need to translate. Quick
  Google check or trace will reveal.
- BIOS Data Area equipment word (BDA 0x410) — must we set drive count
  bits 7:6 + bit 0 before BIOS reads it, or is BIOS POST setting them
  itself from CMOS / hardware probes?

## Cross-references

- Phase 28 closure: `MD/performance/202605152200-pc-emulator-freedos-boot.md`
- Phase 28.IO: `MD/performance/202605152230-pc-emulator-phase-28-io.md`
- Phase 29 closure: `MD/performance/202605160100-x87-fpu-functional-complete.md`
- Gemini consultation: `tools/knowledgebase/message/20260516_004919.txt`
