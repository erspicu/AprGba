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

### 30.6c — ROL with CL count > 1 was wrong — FIXED (2026-05-16) ✅

**Real BIOS + FreeDOS BOOTS END-TO-END!** Text preview after the fix:
```
| FreeCom version 0.85a - WATCOMC - XMS_Swap [Jul 10 2021 19:28:06]        |
```

The root cause was traced via memory-write/read watches + per-CS CPU
trace filtering down to a CPU emulation bug:

**Buggy code path**: `src/AprCpu.Core/IR/X86_16Emitters.cs`
`X86ShiftRotateW16CountClEmitter` "rol" arm had a TODO stub that
delegated `ROL r/m16, CL` to count=1 logic (shift left by 1
regardless of actual CL value). The comment said:
> ROL/ROR/RCL/RCR with count!=1 have notoriously undefined silicon
> behaviour; we delegate to the count=1 IR ... This is wrong for
> count>1 but correct for count=1, which covers most real code that
> ever reaches D2/D3 with a small dynamic count.

pcxtbios.bin INT 13h handler uses `MOV CL, 4; ROL AX, CL` to compute
the 24-bit DMA base + page register split from the caller's 16-bit
ES segment:
```
F000:ED5F  MOV AX, [BP+0xC]   ; AX = caller's ES (= 0x1FE0)
F000:ED62  MOV CL, 4
F000:ED64  ROL AX, CL         ; expected 0xFE01 (true ROL by 4)
                              ; got 0x3FC0 (ROL by 1 stub)
F000:ED66  ...                ; rest of arithmetic
F000:ED75  OUT 0x04, AL       ; programs DMA base lo
```

With buggy ROL by 1, BIOS computed DMA base 0xA360 (linear `0x0A360`).
With correct ROL by 4, BIOS would compute 0x61A0 with page=2 (linear
`0x261A0` ≈ `(ES << 4) + BX` = `0x251A0` adjusted for the ROL trick).

**Fix**: implement proper count-based ROL using `lhs << n | lhs >> (16 - n)`
where `n = CL & 0x1F` (8086 doesn't mask but the i16 operand width
limits effective rotation to mod 16). Sets CF = LSB of result.
OF only architecturally defined for count=1 but we emit the count=1
formula for code that may sample it after multi-bit ROL.

```csharp
case "rol":
{
    var nWide = ctx.Builder.BuildAnd(clClamp,
        LLVMValueRef.CreateConstInt(i8, 15, false), "rolc16_n");
    var n16 = ctx.Builder.BuildZExt(nWide, i16, "rolc16_n16");
    var leftPart  = ctx.Builder.BuildShl(lhs, n16, "rolc16_left");
    var rightShift = ctx.Builder.BuildSub(
        LLVMValueRef.CreateConstInt(i16, 16, false), n16, "rolc16_rsh");
    var rightPart = ctx.Builder.BuildLShr(lhs, rightShift, "rolc16_right");
    result = ctx.Builder.BuildOr(leftPart, rightPart, "rolc16_r");
    // CF = LSB of result; OF = MSB(result) ^ CF (count=1 formula reused).
    ...
}
```

Standalone test ROM `test-roms/x86/30-rol-cl-test.com` validates:
- `0x1FE0 ROL 4 = 0xFE01` ✓
- `0xC123 ROL 8 = 0x23C1` ✓
- `0x0001 ROL 15 = 0x8000` ✓
- `0xFFFF ROL N = 0xFFFF` ✓

End-to-end test (`apr-pc --bios=BIOS/firmware/pcxtbios.bin
--floppy-a=BIOS/freedos-1.3-floppy.img`):
- BIOS POST ✓
- INT 19h loads boot sector via real FDC ✓
- Boot sector executes, self-relocates, loads root dir + FAT ✓
- FAT walker walks chain to kernel.sys ✓
- Kernel loaded, jumps to FreeDOS kernel.sys ✓
- COMMAND.COM (FreeCom 0.85a) banner printed to MDA framebuffer ✓

**Deferred but related**:
- Same count=1 stub still in W8 (8-bit) path (line ~7181). Real DOS
  code rarely uses `ROL r/m8, CL` with CL>1, but should be fixed for
  completeness. Same approach: `lhs << n | lhs >> (8 - n)` with
  n = CL mod 8.
- ROR/RCL/RCR variants also stubbed. ROR can be done similarly. RCL/
  RCR need carry-bit handling in the rotation ring (9-bit / 17-bit).
  None hit in the FreeDOS boot path.

### 30.6b — Trace-cpu identified real root cause (2026-05-16)

The 30.6 Gemini analysis said "boot sector self-relocation didn't
complete". A deeper trace-cpu investigation (added `--trace-cpu-cs=`
filter + memory write/read watch ranges) corrects the diagnosis:

**Boot sector self-relocation (REP MOVSW) WORKS CORRECTLY.** Standalone
REP MOVSW test (`test-roms/x86/30-rep-movsw-test.com`) confirms the
emitter is bit-correct. The relocated bytes that "appeared zero" in
earlier forensics were actually OVERWRITTEN later by the FAT walker's
STOSW loop spinning on cluster 0.

**The real cause is segment-register state mismatch around INT 13h.**
Trace comparison between HLE and real-BIOS paths shows the FreeDOS
boot sector's INT 13h call site at `1FE0:7D7B` (the JZ-skipped AH=41
extensions check) sees DIFFERENT `ES` values across iterations:

| Iteration | HLE ES | real-BIOS ES |
|---|---|---|
| 1 | 0x0060 | 0x0060 |
| 2 | 0x0060 | 0x0060 |
| 3 | **0x0080** | 0x0060 |
| 4 | 0x00A0 | 0x0060 |
| 5 | 0x00C0 | 0x0060 |
| 6 | 0x00E0 | 0x0060 |

**ES increments by 0x20 (= 512 bytes / 16 paragraphs) per iteration in
HLE but stays pinned at 0x0060 in real-BIOS.** That increment is how
FreeDOS allocates each sector to a fresh memory segment (0x60, 0x80,
0xA0, ...) so the FAT walker (later setting `DS=[BP+0x5C]=0x0060`)
can read the cluster chain across multiple loaded sectors.

In real-BIOS path, ES never advances → all sectors are loaded to the
same destination → only the LAST one is visible → walker reads bytes
from the wrong sector → FAT entry 0 → infinite loop on cluster 0.

**Likely culprits** (not yet narrowed further):
1. Real BIOS INT 13h handler clobbers a register that the boot
   sector relies on for its ES-increment math (e.g., AX, CX, or a
   memory cell at BP-something).
2. Our CPU's INT instruction or IRET pushes/pops wrong segment
   register state.
3. The boot sector's increment instruction itself is a string-op
   variant that has a state-dependent bug we haven't hit before.

**Next debugging step**: dump the instructions between successive
INT 13h call sites in BOTH HLE and real-BIOS traces, diff them to
find where the ES-increment diverges. Add `--watch-reg=ES` if
needed (would log every write to ES).

The CPU is `--backend=json` (per-instruction); no block-JIT caching
to invalidate. The bug appears in fully-decoded per-instruction
execution.

### 30.6 — Gemini consultation refined the root cause (2026-05-16)

Asked Gemini to analyze the FAT walker stuck symptom; full log
`tools/knowledgebase/message/20260516_011840.txt`. Key findings:

**Q1 — DMA at 0x0A360 is correct** (= `07C0:2760`). Single-sector
single-buffer is FreeDOS boot sector's intended pattern: read one
sector to scratch, process, read next, repeat. **NOT** a multi-sector
issue — no need to implement multi-sector READ DATA.

**Q5 — IRQ pacing was buggy.** Original code called `AssertIrq(6)`
when host read first result byte; this re-asserts edge-triggered
line and causes spurious second ISR. Fix: just clear local
`_interruptPending` flag, the IRQ already fired once (via
`DequeueNextVector` clearing pending bit). Shipped this fix in the
same supplemental commit.

**Root cause** (per Gemini analysis + memory forensics): DS=0x0060
at the FAT walker means the CPU is reading FAT scratch buffer from
the wrong segment. But deeper inspection via expanded boot-sector
forensics in HeadlessRunner shows: **boot sector self-relocation
copied only the SECOND HALF (bytes 0xFE-0x1FF = 258 bytes) to
`1FE0:7C00`**. The first 254 bytes of the relocated copy are all
zero. Diagnostic:

```
orig @ 0x07C00 [0..31]: EB 3C 90 46 52 44 4F 53 35 2E 31 00 02 01 01 00 ...
copy @ 0x27A00 [0..31]: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ...
orig vs copy diff: 224 bytes differ (first at offset 0x000)
```

Translating: the boot sector's `REP MOVSW` (or equivalent
relocation routine) only transferred 256 bytes starting from offset
0xFE, not the full 512 bytes from offset 0x00.

Most likely cause is a **CPU emulation bug** in one of:
- `REP MOVSW` (rep-prefix interaction with string-op + segment override)
- INT 13h register save/restore (real pcxtbios.bin clobbers CX / SI
  / DI; boot sector may rely on PUSHA/POPA which has its own quirks)
- Direction Flag (DF) handling around STOSW/MOVSW

Next debugging step: enable `--trace-cpu` for a narrow window
around the relocation routine, identify the exact instruction that
sets up the copy, and compare expected vs actual register/flag
state. Defer to Phase 30.6b.

### 30.6a — IRQ pacing fix shipped

`X86FpuHelpers` (sorry, `Fdc8272.ReadFifo`) no longer re-asserts
IRQ 6 when host begins reading result bytes. The original
`AssertIrq(6)` call was meant to "deassert" but actually fired the
edge again. Removed.

### 30.6 — original stuck point description (kept for context)

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
