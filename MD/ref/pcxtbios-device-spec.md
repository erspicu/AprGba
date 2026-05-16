# pcxtbios.bin — Device Specification Handbook

> **Source**: derived from [Super PC/Turbo XT BIOS v3.1](https://github.com/virtualxt/pcxtbios)
> by Jon Petrosky (Plasma), Ya'akov Miles. Originally reverse-engineered from
> Taiwanese Generic Turbo XT BIOS.
> **Date compiled**: 2026-05-16, Phase 30.7d.
>
> **Why this exists**: when emulating PC hardware behaviour for pcxtbios.bin,
> the **BIOS source is the ground truth**. Gemini consultations occasionally
> contradict themselves (we hit this multiple times). When in doubt, grep
> pcxtbios.asm before asking Gemini.
>
> **Section convention**: each device section gives (a) the I/O ports +
> memory addresses pcxtbios actually touches, (b) the BIOS Data Area (BDA)
> fields it reads/writes, (c) the init sequence, (d) implementation notes
> for our emulator.

---

## Table of Contents

1. [Memory Map](#1-memory-map)
2. [BIOS Data Area (BDA) Full Layout](#2-bios-data-area-bda-full-layout)
3. [Equipment Flag (BDA[0x10])](#3-equipment-flag-bda010)
4. [Interrupt Vector Table — Installed Handlers](#4-interrupt-vector-table--installed-handlers)
5. [Video — INT 10h, CRTC, MDA & CGA](#5-video--int-10h-crtc-mda--cga)
6. [Keyboard — INT 9, INT 16h, 8042 PPI](#6-keyboard--int-9-int-16h-8042-ppi)
7. [Floppy — INT 13h, NEC 765 (8272A) FDC, 8237 DMA](#7-floppy--int-13h-nec-765-8272a-fdc-8237-dma)
8. [Timer — 8253 PIT, INT 8, INT 1Ah](#8-timer--8253-pit-int-8-int-1ah)
9. [Interrupt Controller — 8259A PIC](#9-interrupt-controller--8259a-pic)
10. [DMA Controller — 8237](#10-dma-controller--8237)
11. [PPI 8255 — Ports 0x60-0x63](#11-ppi-8255--ports-0x60-0x63)
12. [CMOS / RTC](#12-cmos--rtc)
13. [Serial — 8250 UART, INT 14h](#13-serial--8250-uart-int-14h)
14. [Parallel / Printer — INT 17h](#14-parallel--printer--int-17h)
15. [Bootstrap — INT 19h](#15-bootstrap--int-19h)
16. [Power-On Self Test (POST) Sequence](#16-power-on-self-test-post-sequence)
17. [pcxtbios-Specific Quirks](#17-pcxtbios-specific-quirks)
18. [Indexed Lookup Tables in ROM](#18-indexed-lookup-tables-in-rom)
19. [How to verify a hypothesis against the source](#19-how-to-verify-a-hypothesis-against-the-source)

---

## 1. Memory Map

| Range | Size | Purpose |
|---|---|---|
| `0x00000-0x003FF` | 1 KB | Interrupt Vector Table (256 × 4-byte vectors) |
| `0x00400-0x004FF` | 256 B | BIOS Data Area (segment 0x40) |
| `0x00500-0x005FF` | 256 B | DOS communication area (segment 0x50) |
| `0x00600-0x07BFF` | ~30 KB | Real-mode kernel / TSR / driver region (depends on DOS) |
| `0x07C00-0x07DFF` | 512 B | Boot sector load address (INT 19h loads here) |
| `0x07E00-0x9FFFF` | ~610 KB | Conventional memory (user) |
| `0xA0000-0xAFFFF` | 64 KB | EGA/VGA framebuffer (not used by pcxtbios) |
| `0xB0000-0xB0FFF` | 4 KB | MDA framebuffer (text) |
| `0xB8000-0xBBFFF` | 16 KB | CGA framebuffer (text/graphics) |
| `0xC0000-0xCFFFF` | 64 KB | Option ROM area (scanned for 0x55 0xAA signatures) |
| `0xF0000-0xFFFFF` | 64 KB | System BIOS ROM (pcxtbios.bin = 8 KB at 0xFE000) |
| `0xFFFF0` | 16 B | CPU reset vector (jumps to BIOS POST entry F000:E05B) |

The COM port + LPT port base addresses and equipment-detection result are
written to BDA at boot.

---

## 2. BIOS Data Area (BDA) Full Layout

All addresses relative to segment `0x40` (physical 0x400). pcxtbios
initialises every field at POST.

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0x00 | 8 B | COM port base addresses | 4 × WORD (COM1-COM4) |
| 0x08 | 8 B | LPT port base addresses | 4 × WORD (LPT1-LPT4) |
| 0x10 | 2 B | **Equipment flag** | See §3 |
| 0x12 | 1 B | Expansion ROM flag | |
| 0x13 | 2 B | Memory size (KB) | INT 12h reads this |
| 0x15 | 1 B | IPL error code | |
| 0x17 | 1 B | **Keyboard shift flags (lo)** | bit 1=L-shift, bit 0=R-shift, bit 2=ctrl, bit 3=alt, etc. |
| 0x18 | 1 B | Keyboard shift flags (hi) | Caps/Num/Scroll lock + insert |
| 0x19 | 1 B | Alt-keypad accumulator | |
| 0x1A | 2 B | Keyboard buffer **head** pointer | offset into BDA |
| 0x1C | 2 B | Keyboard buffer **tail** pointer | |
| 0x1E | 32 B | **Keyboard circular buffer** | 16 × (ASCII + scancode) pairs |
| 0x3E | 1 B | Floppy recalibrate status | bit 0-3 per drive |
| 0x3F | 1 B | **Floppy motor status** | bits 0-3 per drive, bit 7 = write-in-progress |
| 0x40 | 1 B | Floppy motor turn-off counter | decremented by INT 8h ISR |
| 0x41 | 1 B | Floppy disk status | last operation result code |
| 0x42 | 7 B | NEC 765 result bytes (ST0/ST1/ST2/C/H/S/N) | |
| 0x49 | 1 B | **Current video mode** | 0-6 = CGA, 7 = MDA |
| 0x4A | 2 B | CRT columns | 40 or 80 |
| 0x4C | 2 B | Regen buffer size | 0x0800 (mode 0-1) / 0x1000 (mode 2-3) / 0x4000 (graphics) |
| 0x4E | 2 B | Regen buffer offset | usually 0 |
| 0x50 | 16 B | Cursor position × 8 pages | each = (col, row) |
| 0x60 | 2 B | Cursor shape | start/end scan line |
| 0x62 | 1 B | Active video page | 0-7 (CGA) / 0 only (MDA) |
| 0x63 | 2 B | **CRT base address** | 0xB000 mono / 0xB800 color |
| 0x65 | 1 B | CRT mode register | last value written to 0x3D8/0x3B8 |
| 0x66 | 1 B | Current CGA palette | for INT 10h AH=0Bh |
| 0x67 | 4 B | Expansion ROM base | last 0xC0000-area scan result |
| 0x6B | 1 B | Spurious IRQ counter | |
| 0x6C | 4 B | **Timer ticks** (24-bit) | incremented by INT 8h @ 18.2 Hz |
| 0x70 | 1 B | New-day flag | set by INT 8h on midnight rollover |
| 0x71 | 1 B | Break flag | set by INT 1Bh (Ctrl-Break) |
| 0x72 | 2 B | **Warm boot flag** | 0x1234 = warm, else cold |
| 0x74 | 7 B | Hard disk scratch | |
| 0x78 | 4 B | LPT timeout | per port |
| 0x7C | 4 B | COM timeout | per port |
| 0x80 | 2 B | Keyboard buffer **start** offset | usually 0x001E |
| 0x82 | 2 B | Keyboard buffer **end** offset | usually 0x003E |
| 0x96 | 1 B | Enhanced keyboard status flag | for 101-key |

---

## 3. Equipment Flag (BDA[0x10])

WORD bitfield set at POST from PPI port 0x62 reads + FPU detect + ROM scan:

| Bits | Meaning |
|---|---|
| 0 | Floppy installed (1 = yes) |
| 1 | Math coprocessor (FPU) installed (1 = yes) |
| 2-3 | Planar RAM (00=16K, 01=32K, 10=48K, **11=64K+ XT default**) |
| **4-5** | **Initial video mode**: 00=EGA/VGA, 01=CGA 40x25, 10=CGA 80x25, **11=MDA 80x25** |
| 6-7 | Floppy drive count - 1 (00 = 1 drive, 01 = 2, ...) |
| 8-10 | Serial port count |
| 11 | Game adapter installed |
| 12 | Internal modem |
| 13 | Reserved |
| 14-15 | Parallel port count |

**INT 10h Set Video Mode (AH=0) uses bits 4-5 to decide MDA vs CGA**:

```asm
mov al, [ds:10h]        ; equipment flag
and al, 00110000b       ; isolate video bits
cmp al, 00110000b       ; mono?
mov dx, 3B4h            ; MDA CRTC port
mov bl, 7               ; force mode 7
jz @@reset              ; mono: ignore caller's AL
mov bl, [bp+2]          ; color: take caller's AL
cmp bl, 7
ja invalid
mov dl, 0D4h            ; CGA CRTC port
```

---

## 4. Interrupt Vector Table — Installed Handlers

pcxtbios installs the following vectors at POST. Vectors NOT listed default
to a do-nothing IRET stub.

| Vector | Hex | ROM Entry | Purpose |
|---|---|---|---|
| INT 8 | 0x08 | F000:* | PIT timer tick — increments BDA[0x6C-0x6F], decrements motor timeout |
| INT 9 | 0x09 | F000:E987 | Keyboard scancode (IRQ 1) — read 0x60, translate via tables, write BDA buffer |
| INT B | 0x0B | * | COM1 IRQ (no-op stub) |
| INT C | 0x0C | * | COM2 IRQ |
| INT D | 0x0D | * | (reserved) |
| INT E | 0x0E | F000:EF57 | Floppy IRQ (IRQ 6) — sets BDA[0x3E] bit 7 = done |
| INT F | 0x0F | * | Printer IRQ |
| INT 10 | 0x10 | F000:F065 | Video services (see §5) |
| INT 11 | 0x11 | * | Equipment check — returns BDA[0x10] |
| INT 12 | 0x12 | * | Memory size — returns BDA[0x13] |
| INT 13 | 0x13 | F000:EC59 | Floppy disk services (see §7) |
| INT 14 | 0x14 | F000:E739 | Serial RS-232 |
| INT 15 | 0x15 | * | System services (mostly stubs on XT) |
| INT 16 | 0x16 | F000:E82E | Keyboard services (see §6) |
| INT 17 | 0x17 | F000:EFD2 | Parallel printer |
| INT 18 | 0x18 | * | ROM BASIC entry (calls "No ROM BASIC" message) |
| INT 19 | 0x19 | F000:E6F2 | Bootstrap loader |
| INT 1B | 0x1B | * | Ctrl-Break handler (sets BDA[0x71]) |
| INT 1C | 0x1C | * | User timer tick (chains from INT 8 — default IRET) |
| INT 1D | 0x1D | F000:F0A4 | Pointer to video parameter tables |
| INT 1E | 0x1E | F000:EFC7 | Pointer to floppy parameter table |
| INT 1F | 0x1F | * | Graphics character set (unused) |
| INT 60 | 0x60 | * | ROM BASIC entry alternative |

---

## 5. Video — INT 10h, CRTC, MDA & CGA

### 5.1 Ports

| Port | Direction | Device | Purpose |
|---|---|---|---|
| 0x3B4 | W | MDA CRTC | Index register |
| 0x3B5 | RW | MDA CRTC | Data register |
| 0x3B8 | RW | MDA | Mode control |
| 0x3B9 | W | MDA | Palette |
| **0x3BA** | R | MDA | **Status register** — bit 0 = retrace, bit 3 = video on |
| 0x3D4 | W | CGA CRTC | Index register |
| 0x3D5 | RW | CGA CRTC | Data register |
| 0x3D8 | RW | CGA | Mode control |
| 0x3D9 | W | CGA | Palette |
| **0x3DA** | R | CGA | **Status register** — bit 0 = display enable, bit 3 = vertical retrace |

### 5.2 Framebuffer

| Mode | BDA[0x49] | Base | Layout | Size |
|---|---|---|---|---|
| 0 | 0 | 0xB8000 | 40x25 text, no color | 2 KB |
| 1 | 1 | 0xB8000 | 40x25 text, 16 color | 2 KB |
| 2 | 2 | 0xB8000 | 80x25 text, no color | 4 KB |
| 3 | 3 | 0xB8000 | 80x25 text, 16 color | 4 KB |
| 4 | 4 | 0xB8000 | 320x200 graphics, 4 color | 16 KB |
| 5 | 5 | 0xB8000 | 320x200 graphics, B&W | 16 KB |
| 6 | 6 | 0xB8000 | 640x200 graphics, B&W | 16 KB |
| **7** | **7** | **0xB0000** | **80x25 mono (MDA)** | **4 KB** |

### 5.3 6845 CRTC init tables (per mode)

Each table is 16 bytes written to CRTC registers 0x00-0x0F via `OUT 0x3D5` (CGA) or `OUT 0x3B5` (MDA):

```asm
; Mode 0-1 (40x25 color)
db 38h, 28h, 2Dh, 0Ah, 1Fh, 6, 19h, 1Ch, 2, 7, 6, 7, 0, 0, 0, 0
; Mode 2-3 (80x25 color)
db 71h, 50h, 5Ah, 0Ah, 1Fh, 6, 19h, 1Ch, 2, 7, 6, 7, 0, 0, 0, 0
; Mode 4-5 (320x200 graphics)
db 38h, 28h, 2Dh, 0Ah, 7Fh, 6, 64h, 70h, 2, 1, 6, 7, 0, 0, 0, 0
; Mode 7 (80x25 MDA mono)
db 61h, 50h, 52h, 0Fh, 19h, 6, 19h, 19h, 2, 0Dh, 0Bh, 0Ch, 0, 0, 0, 0
```

Register 0-15 = horizontal total / display enable / sync position / sync widths / vertical total / etc.

### 5.4 INT 10h function dispatch (`F000:F045` table)

| AH | Function | BDA fields touched |
|---|---|---|
| 0x00 | Set video mode | [0x49] mode, [0x4A] cols, [0x4C] regen size, [0x63] CRT base, [0x65] mode reg, [0x66] palette |
| 0x01 | Set cursor type | [0x60] cursor shape |
| 0x02 | Set cursor position | [0x50+page*2] |
| 0x03 | Read cursor | [0x50+page*2], [0x60] |
| 0x04 | Read light pen | (light pen pos) |
| 0x05 | Select active page | [0x62] |
| 0x06 | Scroll up | (CRTC scroll) |
| 0x07 | Scroll down | |
| 0x08 | Read char + attribute at cursor | |
| 0x09 | Write char + attribute (with count) | (writes char + attr both) |
| 0x0A | Write char only (preserves attribute) | (only writes char) |
| 0x0B | Set palette | [0x66] |
| 0x0C | Write pixel (graphics) | |
| 0x0D | Read pixel | |
| **0x0E** | **Teletype output** | **writes char only — preserves attribute** |
| 0x0F | Get video state | |

**KEY EMULATOR NOTE**: `AH=0E` teletype is char-only. The attribute byte
stays whatever it was before. If a cell was never initialised with a real
attribute, chars written there are **invisible** (attr=0 = black on black
in real hardware). Phase 30.7d documented this.

### 5.5 MDA attribute byte (real hardware pattern-match, not palette index)

| `fg` (bits 0-2) | `bg` (bits 4-6) | Effect |
|---|---|---|
| 000 | 000 | Invisible (black on black) |
| 001 | 000 | Underline (otherwise renders as normal) |
| 000 | 111 | Reverse video (black on light gray/green) |
| anything else | anything else | Normal (light gray/green on black) |

Bit 3 = intensity (brightens fg in non-reverse cells). Bit 7 = blink.

---

## 6. Keyboard — INT 9, INT 16h, 8042 PPI

### 6.1 Hardware

- **Port 0x60**: keyboard data (scancode read)
- **Port 0x61**: PPI Port B (system control)
  - bit 7: keyboard ACK pulse (1=clear shift register, 0=re-enable)
  - bit 1: speaker gate
- **Port 0x64**: status (newer PS/2-style; XT BIOS doesn't use)
- IRQ 1: keyboard data ready

### 6.2 INT 9 ISR Flow (`F000:E987`)

```asm
STI                     ; allow nested IRQ
PUSH AX/BX/CX/DX/SI/DI/DS
CLD
MOV AX, 0x0040
MOV DS, AX              ; DS = BDA
IN  AL, 60h             ; read scancode
PUSH AX
IN  AL, 61h             ; read port 0x61
PUSH AX
OR  AL, 80h
OUT 61h, AL             ; ACK pulse high
POP AX
OUT 61h, AL             ; restore
POP AX                  ; AL = scancode
MOV AH, AL
MOV BX, [0x0096]        ; BDA enhanced kbd flags
CALL processing         ; translate via tables, update BDA[0x17] shift state
JNS  ...                ; if break code (bit 7), update shift; if make, translate ASCII
                        ; and write (ASCII, scan) to BDA buffer at [0x041C]++
MOV AL, 20h
OUT 20h, AL             ; EOI to PIC
POP DS/DI/SI/DX/CX/BX/AX
IRET
```

### 6.3 INT 16h functions

| AH | Function | Returns |
|---|---|---|
| 0x00 | Wait for keystroke | AL = ASCII, AH = scancode; drains BDA buffer |
| 0x01 | Check for keystroke | ZF=1 if buffer empty; else AL/AH as above (peek, no drain) |
| 0x02 | Get shift flags | AL = BDA[0x17] |
| 0x10, 0x11, 0x12 | Extended (enhanced kbd) variants |

### 6.4 Scancode translation tables

In ROM at `F000:E885`:
- `ascii`: 64-byte unshifted scancode → ASCII
- `non_alpha`: shifted secondary
- `ctrl_upper` / `ctrl_lower`: ctrl combinations
- `alt_key`: alt secondary
- `num_pad`: numeric keypad ("789-456+1230.")

---

## 7. Floppy — INT 13h, NEC 765 (8272A) FDC, 8237 DMA

### 7.1 FDC ports

| Port | Direction | Purpose |
|---|---|---|
| 0x3F2 | W | DOR (Digital Output Register): drive select bits 0-1, DMA enable bit 3, nRESET bit 2, motor bits 4-7 |
| 0x3F4 | R | MSR (Main Status Register): bit 7 RQM (ready), bit 6 DIO (direction), bit 4 CB (busy), bits 0-3 drive busy |
| 0x3F5 | RW | Data FIFO (command + result phase) |
| **0x3F7** | R | DIR (Digital Input Register): **bit 7 = DSKCHG (disk change)** — XT BIOS doesn't read this! |

### 7.2 FDC command set (minimum used by INT 13h)

| Opcode | Command | Length | Result | IRQ |
|---|---|---|---|---|
| 0x03 | SPECIFY | 3 | none | no |
| 0x04 | SENSE DRIVE STATUS | 2 | 1 (ST3) | no |
| 0x07 | RECALIBRATE | 2 | none | yes |
| 0x08 | SENSE INTERRUPT STATUS | 1 | 2 (ST0+PCN) | no (clears IRQ) |
| 0x0A | READ ID | 2 | 7 | yes |
| 0x0F | SEEK | 3 | none | yes |
| 0x06 (with MFM bit 0x40) | READ DATA | 9 | 7 | yes |

### 7.3 INT 13h functions

| AH | Function |
|---|---|
| 0x00 | Reset disk system (FDC reset + recalibrate) |
| 0x01 | Get status (last result code) |
| 0x02 | Read sectors (DMA + READ DATA) |
| 0x03 | Write sectors |
| 0x04 | Verify sectors |
| 0x05 | Format track |
| 0x08 | Get drive parameters (returns from INT 1Eh table) |

### 7.4 INT 1Eh — Floppy parameter table (`F000:EFC7`)

11-byte structure for FDC SPECIFY + INT 13h disk geometry. Standard 1.44 MB: 80 cyl × 2 head × 18 sec, 512 bytes/sector.

### 7.5 IRQ 6 / INT E handler

Just sets BDA[0x3E] bit 7 = "operation done" and EOIs. INT 13h waits on this flag.

### 7.6 Motor handling

INT 13h checks BDA[0x3F] motor flag. If motor off, sets motor bit in DOR + **waits 500 ms** for spin-up. Motor timeout countdown driven by INT 8h tick handler decrements BDA[0x40]; reaching 0 turns motor off.

**EMULATOR NOTE**: our FDC completes instantly so the 500 ms BIOS stall is wasted. Phase 30.7a forced all motor bits to 1 in `Fdc8272.WriteDor` so BIOS skips the wait.

---

## 8. Timer — 8253 PIT, INT 8, INT 1Ah

### 8.1 Ports

| Port | Channel | Purpose |
|---|---|---|
| 0x40 | 0 | System tick (drives IRQ 0 / INT 8) — default reload 0x10000 → 18.2065 Hz |
| 0x41 | 1 | DRAM refresh (BIOS programs to 18) |
| 0x42 | 2 | Speaker gate (via port 0x61 bit 1) |
| 0x43 | — | Control register (channel select, mode, latch) |

### 8.2 INT 8h ISR

Increments BDA[0x6C-0x6F] (DWORD ticks). On midnight rollover (1,573,040 ticks), sets BDA[0x70] flag. Decrements BDA[0x40] motor counter; if zero, OUTs 0x3F2 with motor bits cleared. Calls INT 1Ch (user timer chain, default IRET). EOI to PIC. IRET.

### 8.3 INT 1Ah — Time of day

| AH | Function | Returns/Sets |
|---|---|---|
| 0x00 | Read ticks | CX:DX = BDA[0x6C-0x6F]; AL = BDA[0x70] (rolled over since last read, then cleared) |
| 0x01 | Set ticks | BDA[0x6C-0x6F] = CX:DX |
| 0x02 | Read RTC time | (uses CMOS if present) |
| 0x06 | Set RTC alarm | |

### 8.4 POST init sequence

```asm
mov al, 01010100b    ; channel 1, mode 2 (memory refresh)
out 43h, al
mov al, 12h          ; divisor 0x12 (= ~66 KHz refresh)
out 41h, al
mov al, 00110110b    ; channel 0, mode 3, lo+hi 16-bit
out 43h, al
xor al, al           ; divisor 0 = 65536 → 18.2 Hz
out 40h, al
out 40h, al
```

---

## 9. Interrupt Controller — 8259A PIC

### 9.1 Ports

| Port | Direction | Purpose |
|---|---|---|
| 0x20 | RW | Command/status: write OCW2 (EOI = 0x20), read ISR/IRR |
| 0x21 | RW | OCW1: IMR (interrupt mask) |

### 9.2 POST init (ICW1 + ICW2 + ICW3 + ICW4)

```asm
mov al, 13h         ; ICW1: edge-triggered, ICW4 needed
out 20h, al
mov al, 8           ; ICW2: vector base = 0x08 (IRQ 0 → INT 8)
out 21h, al
mov al, 9           ; ICW3: cascade master, slave on IR2 (single PIC in XT)
out 21h, al
mov al, 0FFh        ; OCW1: mask everything
out 21h, al         ; (BIOS later unmasks IRQ 0 + 1)
```

### 9.3 IRQ → vector mapping (post init)

| IRQ | Vector | Purpose |
|---|---|---|
| 0 | INT 8 | PIT tick (18.2 Hz) |
| 1 | INT 9 | Keyboard |
| 2 | INT A | Cascade (slave PIC on AT; not used on XT) |
| 3 | INT B | COM2 |
| 4 | INT C | COM1 |
| 5 | INT D | (LPT2 on AT, not used on XT BIOS) |
| 6 | INT E | Floppy controller |
| 7 | INT F | LPT1 / spurious |

EOI = `mov al, 0x20; out 0x20, al`.

---

## 10. DMA Controller — 8237

### 10.1 Ports

| Port | Direction | Purpose |
|---|---|---|
| 0x00-0x07 | RW | Channel 0-3 base/count registers (paired, flip-flop separates lo/hi) |
| 0x08 | R | Status register: bits 0-3 = TC reached per channel |
| 0x09 | W | DMA request |
| 0x0A | W | Channel mask register |
| 0x0B | W | Mode register (per channel) |
| 0x0C | W | Clear flip-flop |
| 0x0D | W | Master reset |
| 0x0E | W | Clear mask register |
| 0x0F | W | All mask register |
| 0x81 | W | DMA Channel 2 page register (high 4 bits of physical address) |
| 0x82 | W | Channel 3 page |
| 0x83 | W | Channel 0/1 page |

### 10.2 POST init

```asm
xor al, al
out 0Dh, al          ; master reset
out 81h, al          ; clear pages
out 82h, al
out 83h, al
mov al, 01011000b    ; ch0 mode: read, autoinit, increment (DRAM refresh)
out 0Bh, al
mov al, 01000001b    ; ch1 mode: verify, single transfer
out 0Bh, al
; ch2, ch3 set later by drivers
mov al, 0FFh
out 1, al            ; ch0 count low
out 1, al            ; ch0 count high
mov al, 1
out 0Ah, al          ; unmask ch0
```

Mode byte format (channel = bits 0-1):
- bits 7-6: transfer mode (00=demand, 01=single, 10=block)
- bit 5: address dec (0) / inc (1)
- bit 4: auto-init enable
- bits 3-2: 00=verify, 01=write to mem, 10=read from mem

---

## 11. PPI 8255 — Ports 0x60-0x63

### 11.1 Ports

| Port | Direction | Purpose |
|---|---|---|
| 0x60 | R | Port A: keyboard data |
| 0x61 | RW | Port B: control bits |
| 0x62 | R | Port C: DIP switch readback (multiplexed) |
| 0x63 | W | PPI control word |

### 11.2 Port B (0x61) bit layout

| Bit | Purpose |
|---|---|
| 7 | Keyboard clear / shift register reset (pulse) |
| 6 | Reserved |
| 5 | Reserved |
| 4 | RAM parity check enable |
| 3 | I/O channel check enable |
| 2 | Port C selector (see §11.3) |
| 1 | Speaker gate (with PIT ch2) |
| 0 | Speaker on |

### 11.3 Port C (0x62) bit layout — **pcxtbios reading sequence**

pcxtbios reads port 0x62 **twice**, with an `OUT 0xAD, 0x61` (= Port B = 0xAD) in between:

```asm
in al, 62h          ; FIRST read: memory size in low 4 bits
and al, 0Fh
mov ah, al          ; save
mov al, 10101101b   ; 0xAD = 1010 1101
out 61h, al         ; toggle PPI selector
in al, 62h          ; SECOND read: video+floppy in low 4 bits
mov cl, 4
shl al, cl          ; shift to high nibble
or al, ah           ; combine → equipment flag
```

**Low 4 bits of port 0x62**:
- **Selector = 0** (Port B bit 2 not set per first read): memory size bits 0-3
  - 0x03 = 256 KB planar (XT default)
- **Selector = 1** (after `OUT 0xAD`): video + floppy in low 4 bits
  - bits 0-1: video (00=EGA, 01=CGA 40x25, 10=CGA 80x25, **11=MDA 80x25**)
  - bits 2-3: floppy count - 1 (00 = 1 drive)

**EMULATOR NOTE (Phase 30.7c)**: Our `Read62()` returns the right nibble based on `_port61 & 0x04`. The Gemini consults disagreed on bit 2 vs bit 3 as selector; bit 2 is the version user-verified to work for CGA.

### 11.4 PPI control word (0x63 write)

`mov al, 10011001b; out 63h, al` — programs Ports A + C as input, Port B as output. Standard XT POST init.

---

## 12. CMOS / RTC

XT 5160 has **no battery-backed CMOS**. pcxtbios DOES support add-in RTC cards via `clock_check` proc at POST. Bases probed: 0x2C1, 0x241, 0x341 (per source).

For our emulator, since we have no real RTC, INT 1Ah AH=00-01 (tick-based time) is sufficient. AH=02/04 (RTC read) is hit by FreeDOS `time` / `date` commands; we can stub.

---

## 13. Serial — 8250 UART, INT 14h

### 13.1 Ports per COM port

| COM | Base | Offsets |
|---|---|---|
| COM1 | 0x3F8 | +0 data, +1 IER, +2 IIR, +3 LCR, +4 MCR, +5 LSR, +6 MSR |
| COM2 | 0x2F8 | (same offsets) |

### 13.2 Baud rate divisor table (`F000:E729`)

```asm
dw 0417h    ; 110 baud
dw 0300h    ; 150
dw 0180h    ; 300
dw 00C0h    ; 600
dw 0060h    ; 1200
dw 0030h    ; 2400
dw 0018h    ; 4800
dw 000Ch    ; 9600
```

Written to UART divisor latch after setting LCR bit 7 (DLAB).

### 13.3 INT 14h functions

| AH | Function |
|---|---|
| 0x00 | Init port (baud, parity, etc.) |
| 0x01 | Send char (poll LSR for TBE) |
| 0x02 | Receive char (poll LSR for data ready) |
| 0x03 | Get line + modem status |

**EMULATOR NOTE**: our stubs return LSR=0x60 (TBE+TSE always ready), MSR=0x30 (CTS+DSR ready). DOS programs that try to write to COM1 see "always ready", chars get discarded silently.

---

## 14. Parallel / Printer — INT 17h

### 14.1 Ports per LPT

| LPT | Data | Status | Control |
|---|---|---|---|
| LPT1 | 0x378 | **0x379** | 0x37A |
| LPT2 | 0x278 | **0x279** | 0x27A |

### 14.2 Status register bits (read from 0x379)

| Bit | Meaning |
|---|---|
| 7 | nBUSY (active low: 1 = ready) |
| 6 | nACK |
| 5 | PAPER OUT |
| 4 | SELECT (online) |
| 3 | nERROR (active low: 1 = no error) |

**EMULATOR NOTE**: stub 0x379/0x279 = 0xD8 = ready, no error, online, no paper out.

### 14.3 INT 17h functions

| AH | Function |
|---|---|
| 0x00 | Send byte |
| 0x01 | Init printer |
| 0x02 | Get status |

---

## 15. Bootstrap — INT 19h

`F000:E6F2`. Loads boot sector via INT 13h AH=02, drive 0, CHS 0/0/1, 1 sector to 0000:7C00. Jumps to 0000:7C00. If load fails, retries; if all retries fail and a hard disk is present, tries hard disk 0x80; else falls through to INT 18h (ROM BASIC / "No ROM BASIC" message).

---

## 16. Power-On Self Test (POST) Sequence

`F000:E05B` cold-start entry. Roughly:

1. Set DS=0, SS=0, SP=0x7C00.
2. Disable NMI (OUT 0xA0, 0x80 — write to NMI mask).
3. CPU type detect (`cpu_check`).
4. Run optional CPU self-test (ifdef TEST_CPU).
5. 8255 PPI init: `OUT 0x63, 0x99`.
6. 8253 PIT init (channel 0 = 18.2 Hz, channel 1 = refresh).
7. 8259 PIC init (ICW1-3, mask all in OCW1).
8. 8237 DMA init.
9. Memory test (clear vector + BDA + user mem, count free).
10. **Video init** — INT 10h with equipment=mono (try MDA), then equipment=color (try CGA). Wins the last-success.
11. **PPI Port C read** for memory + floppy + video bits → BDA[0x10] equipment flag.
12. FPU detect (`fpu_check`).
13. ROM scan 0xC0000-0xCFFFF for option ROMs (look for `0x55 0xAA` signature, CALL their `+3` entry).
14. Floppy controller init (RESET via DOR bit 2 toggle).
15. RTC detect.
16. Print banner + detected devices.
17. INT 19h (bootstrap).

---

## 17. pcxtbios-Specific Quirks

These differ from IBM 5160 stock BIOS. Worth knowing because Gemini-trained
spec descriptions often assume IBM and break here.

- **Port 0x62 selector**: pcxtbios reads with the **low nibble** of port C
  for BOTH memory and video+floppy (multiplexed via Port B), NOT high
  nibble = always video+floppy as Gemini's first answer claimed.
- **Video init**: pcxtbios calls INT 10h twice (mono first, color second);
  the LAST init wins. If both VRAMs (0xB0000 + 0xB8000) are read/writable
  in our emulator (they are — full 1 MB RAM), CGA always wins → BDA[0x49]
  ends up = 3 even when --video=mda requested. (Phase 30.8: force CGA
  CRTC probe fail.)
- **No CMOS-based clock by default**; uses tick-counter at BDA[0x6C].
- **Optional features** controlled by ifdefs at top of source — IBM_PC,
  TURBO_ENABLED, etc. The shipped `pcxtbios.bin` we use is XT-mode with
  most features on.
- **Manufacturing test mode**: SW1-1 (low bit of port 0x62 SW1 nibble) = 0
  puts BIOS in POST-loop test mode. ALWAYS set this to 1 in our stub.
- **Floppy DSKCHG (port 0x3F7 bit 7)**: XT BIOS doesn't read this port at
  all; FreeDOS does in some paths. Our emulator stubs it to 0x7F (no
  change) defensively.

---

## 18. Indexed Lookup Tables in ROM

Quick reference for tables you might need to disassemble manually.

| Table | ROM addr | Size | Purpose |
|---|---|---|---|
| `int_1D` video params | F000:F0A4 | 64+ bytes | 4 × 16-byte CRTC init blocks (modes 0-1, 2-3, 4-6, 7) |
| `regen_len` | (within int_1D) | 8 bytes | Framebuffer size per mode group |
| `max_cols` | (within int_1D) | 8 bytes | 28h/28h/50h/50h/28h/28h/50h/50h |
| `mode` | (within int_1D) | 8 bytes | Mode control register values per mode |
| `ascii` scancode → ASCII | F000:E885 | 64 bytes | Unshifted PC-XT scan set 1 |
| `non_alpha` shifted | (after ascii) | 32 bytes | |
| `ctrl_upper` / `ctrl_lower` | (after non_alpha) | 32+32 | |
| `alt_key` | (after ctrl) | 32 bytes | |
| `num_pad` | | 13 bytes | "789-456+1230." |
| `func_table` FDC commands | within int_13 | 6 | NEC opcodes per INT 13h AH=02-07 |
| `dma_table` | within int_13 | 6 | DMA mode per FDC command |
| `int_1E` floppy params | F000:EFC7 | 11 bytes | SPECIFY + geometry |
| `baud` | F000:E729 | 16 bytes | 8 × WORD UART divisors |

---

## 19. How to verify a hypothesis against the source

1. **WebFetch the raw source**:
   `https://raw.githubusercontent.com/virtualxt/pcxtbios/master/pcxtbios.asm`
   (the file is ~250 KB; one WebFetch returns ~25-30 KB processed, so target
   your query at a specific symbol or label)

2. **Prefer source over Gemini** when:
   - The hypothesis involves bit numbering, port addresses, or BDA offsets.
   - Gemini self-contradicts (this happens — we hit it on port 0x61 selector
     bit 2 vs bit 3).
   - The symptom is a real-mode BIOS behaviour with a clear control-flow
     answer (e.g., "does pcxtbios poll port 0x3F7 for DSKCHG?" → grep the
     source for `3F7h` or `dx, 3F7`).

3. **Useful WebFetch prompts**:
   - "Find all instructions touching I/O port 0x3F7"
   - "Show the entire `int_10_func_X` subroutine"
   - "List all `DB`/`DW` tables in this file with their addresses"
   - "Find any `STI`/`HLT` patterns and what immediately precedes them"

4. **When Gemini consultation IS appropriate**:
   - Generic spec questions ("what does PPI Port B bit 7 control on the IBM PC?")
   - Cross-reference checks ("does this BIOS code match the standard IBM convention?")
   - Algorithm design ("how should I emulate the keyboard FIFO + IRQ
     interaction?")
   - When you've already verified the source and need an outside opinion.

5. **Capture the answer**: save Gemini transcripts to
   `tools/knowledgebase/message/YYYYMMDD_HHMMSS.txt` automatically (the
   `gemini_query.py` tool does this); cite them in commit messages and
   closure notes.

---

## Cross-references

- `MD/design/30-fdc-dma-plan.md` — Phase 30 implementation plan
- `MD/performance/202605161900-realbios-keyboard-gui-end-to-end.md` — Phase 30.7a closure
- `tools/knowledgebase/message/2026051[5,6]_*.txt` — Gemini consultation history
- Upstream source: https://github.com/virtualxt/pcxtbios/blob/master/pcxtbios.asm
