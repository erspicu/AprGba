# pcxtbios.bin — Device Specification 手冊

> **Source**：自 Jon Petrosky (Plasma)、Ya'akov Miles 的
> [Super PC/Turbo XT BIOS v3.1](https://github.com/virtualxt/pcxtbios) 衍生。
> 原從 Taiwanese Generic Turbo XT BIOS reverse-engineer 來。
> **編譯日期**：2026-05-16、Phase 30.7d。
>
> **為什麼存在**：模擬 pcxtbios.bin 的 PC 硬體行為時、**BIOS source 是
> 真理**。Gemini consultation 偶爾自相矛盾（我們碰過多次）。有疑問時、
> 問 Gemini 之前先 grep pcxtbios.asm。
>
> **章節慣例**：每個 device 章節給 (a) pcxtbios 實際碰的 I/O port +
> memory address、(b) 它讀/寫的 BIOS Data Area (BDA) field、(c) init 序列、
> (d) 我們 emulator 的實作 note。

---

## 目錄

1. [Memory Map](#1-memory-map)
2. [BIOS Data Area (BDA) 完整 Layout](#2-bios-data-area-bda-完整-layout)
3. [Equipment Flag (BDA[0x10])](#3-equipment-flag-bda010)
4. [Interrupt Vector Table — 安裝的 Handler](#4-interrupt-vector-table--安裝的-handler)
5. [Video — INT 10h、CRTC、MDA & CGA](#5-video--int-10hcrtcmda--cga)
6. [Keyboard — INT 9、INT 16h、8042 PPI](#6-keyboard--int-9int-16h8042-ppi)
7. [Floppy — INT 13h、NEC 765 (8272A) FDC、8237 DMA](#7-floppy--int-13hnec-765-8272a-fdc8237-dma)
8. [Timer — 8253 PIT、INT 8、INT 1Ah](#8-timer--8253-pitint-8int-1ah)
9. [Interrupt Controller — 8259A PIC](#9-interrupt-controller--8259a-pic)
10. [DMA Controller — 8237](#10-dma-controller--8237)
11. [PPI 8255 — Port 0x60-0x63](#11-ppi-8255--port-0x60-0x63)
12. [CMOS / RTC](#12-cmos--rtc)
13. [Serial — 8250 UART、INT 14h](#13-serial--8250-uartint-14h)
14. [Parallel / Printer — INT 17h](#14-parallel--printer--int-17h)
15. [Bootstrap — INT 19h](#15-bootstrap--int-19h)
16. [Power-On Self Test (POST) 序列](#16-power-on-self-test-post-序列)
17. [pcxtbios-Specific Quirk](#17-pcxtbios-specific-quirk)
18. [ROM 內的 Indexed Lookup Table](#18-rom-內的-indexed-lookup-table)
19. [如何對 source 驗證假設](#19-如何對-source-驗證假設)

---

## 1. Memory Map

| 範圍 | Size | 用途 |
|---|---|---|
| `0x00000-0x003FF` | 1 KB | Interrupt Vector Table（256 × 4-byte vector） |
| `0x00400-0x004FF` | 256 B | BIOS Data Area（segment 0x40） |
| `0x00500-0x005FF` | 256 B | DOS communication area（segment 0x50） |
| `0x00600-0x07BFF` | ~30 KB | Real-mode kernel / TSR / driver 區（隨 DOS） |
| `0x07C00-0x07DFF` | 512 B | Boot sector load address（INT 19h load 這裡） |
| `0x07E00-0x9FFFF` | ~610 KB | Conventional memory（user） |
| `0xA0000-0xAFFFF` | 64 KB | EGA/VGA framebuffer（pcxtbios 不用） |
| `0xB0000-0xB0FFF` | 4 KB | MDA framebuffer（text） |
| `0xB8000-0xBBFFF` | 16 KB | CGA framebuffer（text/graphics） |
| `0xC0000-0xCFFFF` | 64 KB | Option ROM 區（掃 0x55 0xAA signature） |
| `0xF0000-0xFFFFF` | 64 KB | System BIOS ROM（pcxtbios.bin = 0xFE000 處 8 KB） |
| `0xFFFF0` | 16 B | CPU reset vector（跳 BIOS POST entry F000:E05B） |

COM port + LPT port base address 跟 equipment-detection 結果 boot 時寫到 BDA。

---

## 2. BIOS Data Area (BDA) 完整 Layout

全部 address 相對 segment `0x40`（physical 0x400）。pcxtbios 在 POST
init 每個 field。

| Offset | Size | Field | Note |
|---|---|---|---|
| 0x00 | 8 B | COM port base address | 4 × WORD（COM1-COM4） |
| 0x08 | 8 B | LPT port base address | 4 × WORD（LPT1-LPT4） |
| 0x10 | 2 B | **Equipment flag** | 看 §3 |
| 0x12 | 1 B | Expansion ROM flag | |
| 0x13 | 2 B | Memory size (KB) | INT 12h 讀這個 |
| 0x15 | 1 B | IPL error code | |
| 0x17 | 1 B | **Keyboard shift flag (lo)** | bit 1=L-shift、bit 0=R-shift、bit 2=ctrl、bit 3=alt 等 |
| 0x18 | 1 B | Keyboard shift flag (hi) | Caps/Num/Scroll lock + insert |
| 0x19 | 1 B | Alt-keypad accumulator | |
| 0x1A | 2 B | Keyboard buffer **head** pointer | BDA 內 offset |
| 0x1C | 2 B | Keyboard buffer **tail** pointer | |
| 0x1E | 32 B | **Keyboard circular buffer** | 16 × (ASCII + scancode) pair |
| 0x3E | 1 B | Floppy recalibrate status | bit 0-3 per drive |
| 0x3F | 1 B | **Floppy motor status** | bit 0-3 per drive、bit 7 = write-in-progress |
| 0x40 | 1 B | Floppy motor turn-off counter | 由 INT 8h ISR decrement |
| 0x41 | 1 B | Floppy disk status | 上次 operation 結果 code |
| 0x42 | 7 B | NEC 765 result byte（ST0/ST1/ST2/C/H/S/N） | |
| 0x49 | 1 B | **目前 video mode** | 0-6 = CGA、7 = MDA |
| 0x4A | 2 B | CRT column 數 | 40 或 80 |
| 0x4C | 2 B | Regen buffer size | 0x0800（mode 0-1）/ 0x1000（mode 2-3）/ 0x4000（graphics） |
| 0x4E | 2 B | Regen buffer offset | 通常 0 |
| 0x50 | 16 B | Cursor 位置 × 8 page | each = (col, row) |
| 0x60 | 2 B | Cursor shape | start/end scan line |
| 0x62 | 1 B | Active video page | 0-7（CGA）/ 0 only（MDA） |
| 0x63 | 2 B | **CRT base address** | 0xB000 mono / 0xB800 color |
| 0x65 | 1 B | CRT mode register | 上次寫到 0x3D8/0x3B8 的值 |
| 0x66 | 1 B | 目前 CGA palette | for INT 10h AH=0Bh |
| 0x67 | 4 B | Expansion ROM base | 上次 0xC0000-area 掃結果 |
| 0x6B | 1 B | Spurious IRQ counter | |
| 0x6C | 4 B | **Timer tick**（24-bit） | INT 8h @ 18.2 Hz increment |
| 0x70 | 1 B | New-day flag | INT 8h 在午夜 rollover 時 set |
| 0x71 | 1 B | Break flag | INT 1Bh (Ctrl-Break) 時 set |
| 0x72 | 2 B | **Warm boot flag** | 0x1234 = warm、其他 cold |
| 0x74 | 7 B | Hard disk scratch | |
| 0x78 | 4 B | LPT timeout | per port |
| 0x7C | 4 B | COM timeout | per port |
| 0x80 | 2 B | Keyboard buffer **start** offset | 通常 0x001E |
| 0x82 | 2 B | Keyboard buffer **end** offset | 通常 0x003E |
| 0x96 | 1 B | Enhanced keyboard status flag | for 101-key |

---

## 3. Equipment Flag (BDA[0x10])

POST 時從 PPI port 0x62 read + FPU detect + ROM scan set 的 WORD bitfield：

| Bit | 意義 |
|---|---|
| 0 | Floppy installed（1 = yes） |
| 1 | Math coprocessor (FPU) installed（1 = yes） |
| 2-3 | Planar RAM（00=16K、01=32K、10=48K、**11=64K+ XT 預設**） |
| **4-5** | **Initial video mode**：00=EGA/VGA、01=CGA 40x25、10=CGA 80x25、**11=MDA 80x25** |
| 6-7 | Floppy drive count - 1（00 = 1 drive、01 = 2、…） |
| 8-10 | Serial port count |
| 11 | Game adapter installed |
| 12 | Internal modem |
| 13 | Reserved |
| 14-15 | Parallel port count |

**INT 10h Set Video Mode (AH=0) 用 bit 4-5 決定 MDA vs CGA**：

```asm
mov al, [ds:10h]        ; equipment flag
and al, 00110000b       ; 隔 video bit
cmp al, 00110000b       ; mono?
mov dx, 3B4h            ; MDA CRTC port
mov bl, 7               ; 強迫 mode 7
jz @@reset              ; mono：忽略 caller AL
mov bl, [bp+2]          ; color：拿 caller AL
cmp bl, 7
ja invalid
mov dl, 0D4h            ; CGA CRTC port
```

---

## 4. Interrupt Vector Table — 安裝的 Handler

pcxtbios 在 POST 安裝下列 vector。沒列的 vector 預設 do-nothing IRET stub。

| Vector | Hex | ROM Entry | 用途 |
|---|---|---|---|
| INT 8 | 0x08 | F000:* | PIT timer tick — increment BDA[0x6C-0x6F]、decrement motor timeout |
| INT 9 | 0x09 | F000:E987 | Keyboard scancode (IRQ 1) — read 0x60、透過 table 翻譯、寫 BDA buffer |
| INT B | 0x0B | * | COM1 IRQ (no-op stub) |
| INT C | 0x0C | * | COM2 IRQ |
| INT D | 0x0D | * | (reserved) |
| INT E | 0x0E | F000:EF57 | Floppy IRQ (IRQ 6) — set BDA[0x3E] bit 7 = done |
| INT F | 0x0F | * | Printer IRQ |
| INT 10 | 0x10 | F000:F065 | Video services (看 §5) |
| INT 11 | 0x11 | * | Equipment check — 回 BDA[0x10] |
| INT 12 | 0x12 | * | Memory size — 回 BDA[0x13] |
| INT 13 | 0x13 | F000:EC59 | Floppy disk services (看 §7) |
| INT 14 | 0x14 | F000:E739 | Serial RS-232 |
| INT 15 | 0x15 | * | System services (XT 上多 stub) |
| INT 16 | 0x16 | F000:E82E | Keyboard services (看 §6) |
| INT 17 | 0x17 | F000:EFD2 | Parallel printer |
| INT 18 | 0x18 | * | ROM BASIC entry (call「No ROM BASIC」message) |
| INT 19 | 0x19 | F000:E6F2 | Bootstrap loader |
| INT 1B | 0x1B | * | Ctrl-Break handler (set BDA[0x71]) |
| INT 1C | 0x1C | * | User timer tick (從 INT 8 鏈出 — 預設 IRET) |
| INT 1D | 0x1D | F000:F0A4 | 指向 video parameter table |
| INT 1E | 0x1E | F000:EFC7 | 指向 floppy parameter table |
| INT 1F | 0x1F | * | Graphics character set (沒用) |
| INT 60 | 0x60 | * | ROM BASIC entry 替代 |

---

## 5. Video — INT 10h、CRTC、MDA & CGA

### 5.1 Port

| Port | Direction | Device | 用途 |
|---|---|---|---|
| 0x3B4 | W | MDA CRTC | Index register |
| 0x3B5 | RW | MDA CRTC | Data register |
| 0x3B8 | RW | MDA | Mode control |
| 0x3B9 | W | MDA | Palette |
| **0x3BA** | R | MDA | **Status register** — bit 0 = retrace、bit 3 = video on |
| 0x3D4 | W | CGA CRTC | Index register |
| 0x3D5 | RW | CGA CRTC | Data register |
| 0x3D8 | RW | CGA | Mode control |
| 0x3D9 | W | CGA | Palette |
| **0x3DA** | R | CGA | **Status register** — bit 0 = display enable、bit 3 = vertical retrace |

### 5.2 Framebuffer

| Mode | BDA[0x49] | Base | Layout | Size |
|---|---|---|---|---|
| 0 | 0 | 0xB8000 | 40x25 text、no color | 2 KB |
| 1 | 1 | 0xB8000 | 40x25 text、16 color | 2 KB |
| 2 | 2 | 0xB8000 | 80x25 text、no color | 4 KB |
| 3 | 3 | 0xB8000 | 80x25 text、16 color | 4 KB |
| 4 | 4 | 0xB8000 | 320x200 graphics、4 color | 16 KB |
| 5 | 5 | 0xB8000 | 320x200 graphics、B&W | 16 KB |
| 6 | 6 | 0xB8000 | 640x200 graphics、B&W | 16 KB |
| **7** | **7** | **0xB0000** | **80x25 mono (MDA)** | **4 KB** |

### 5.3 6845 CRTC init table（per mode）

每個 table 16 byte、透過 `OUT 0x3D5`（CGA）或 `OUT 0x3B5`（MDA）寫到
CRTC register 0x00-0x0F：

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

Register 0-15 = horizontal total / display enable / sync position / sync
width / vertical total 等。

### 5.4 INT 10h function dispatch（`F000:F045` table）

| AH | Function | 碰的 BDA field |
|---|---|---|
| 0x00 | Set video mode | [0x49] mode、[0x4A] cols、[0x4C] regen size、[0x63] CRT base、[0x65] mode reg、[0x66] palette |
| 0x01 | Set cursor type | [0x60] cursor shape |
| 0x02 | Set cursor 位置 | [0x50+page*2] |
| 0x03 | Read cursor | [0x50+page*2]、[0x60] |
| 0x04 | Read light pen | (light pen pos) |
| 0x05 | Select active page | [0x62] |
| 0x06 | Scroll up | (CRTC scroll) |
| 0x07 | Scroll down | |
| 0x08 | Read cursor 處 char + attribute | |
| 0x09 | Write char + attribute (帶 count) | (寫 char + attr 兩個) |
| 0x0A | Write 只 char (保留 attribute) | (只寫 char) |
| 0x0B | Set palette | [0x66] |
| 0x0C | Write pixel (graphics) | |
| 0x0D | Read pixel | |
| **0x0E** | **Teletype output** | **只寫 char — 保留 attribute** |
| 0x0F | Get video state | |

**KEY EMULATOR NOTE**：`AH=0E` teletype 只 char。Attribute byte 留之前
任何值。如果 cell 從沒被 real attribute init、寫那裡的 char **隱形**
（attr=0 = 真硬體上 black on black）。Phase 30.7d 文件化這個。

### 5.5 MDA attribute byte（真硬體 pattern-match、不是 palette index）

| `fg`（bit 0-2） | `bg`（bit 4-6） | Effect |
|---|---|---|
| 000 | 000 | 隱形（black on black） |
| 001 | 000 | Underline（其他 render 為 normal） |
| 000 | 111 | 反白（black on light gray/green） |
| 其他 | 其他 | Normal（light gray/green on black） |

Bit 3 = intensity（non-reverse cell 內 brighten fg）。Bit 7 = blink。

---

## 6. Keyboard — INT 9、INT 16h、8042 PPI

### 6.1 硬體

- **Port 0x60**：keyboard data（scancode read）
- **Port 0x61**：PPI Port B（system control）
  - bit 7：keyboard ACK pulse（1=clear shift register、0=re-enable）
  - bit 1：speaker gate
- **Port 0x64**：status（newer PS/2-style；XT BIOS 不用）
- IRQ 1：keyboard data ready

### 6.2 INT 9 ISR Flow（`F000:E987`）

```asm
STI                     ; 允許 nested IRQ
PUSH AX/BX/CX/DX/SI/DI/DS
CLD
MOV AX, 0x0040
MOV DS, AX              ; DS = BDA
IN  AL, 60h             ; 讀 scancode
PUSH AX
IN  AL, 61h             ; 讀 port 0x61
PUSH AX
OR  AL, 80h
OUT 61h, AL             ; ACK pulse high
POP AX
OUT 61h, AL             ; restore
POP AX                  ; AL = scancode
MOV AH, AL
MOV BX, [0x0096]        ; BDA enhanced kbd flag
CALL processing         ; 透過 table 翻譯、update BDA[0x17] shift state
JNS  ...                ; break code (bit 7) update shift；make code 翻 ASCII
                        ; 並寫 (ASCII, scan) 到 BDA buffer at [0x041C]++
MOV AL, 20h
OUT 20h, AL             ; EOI to PIC
POP DS/DI/SI/DX/CX/BX/AX
IRET
```

### 6.3 INT 16h function

| AH | Function | Returns |
|---|---|---|
| 0x00 | Wait for keystroke | AL = ASCII、AH = scancode；drain BDA buffer |
| 0x01 | Check for keystroke | ZF=1 if buffer 空；否則 AL/AH 如上（peek、no drain） |
| 0x02 | Get shift flag | AL = BDA[0x17] |
| 0x10、0x11、0x12 | Extended（enhanced kbd）variant |

### 6.4 Scancode 翻譯 table

在 `F000:E885`：
- `ascii`：64-byte unshifted scancode → ASCII
- `non_alpha`：shifted 副
- `ctrl_upper` / `ctrl_lower`：ctrl 組合
- `alt_key`：alt 副
- `num_pad`：numeric keypad（「789-456+1230.」）

---

## 7. Floppy — INT 13h、NEC 765 (8272A) FDC、8237 DMA

### 7.1 FDC port

| Port | Direction | 用途 |
|---|---|---|
| 0x3F2 | W | DOR（Digital Output Register）：drive select bit 0-1、DMA enable bit 3、nRESET bit 2、motor bit 4-7 |
| 0x3F4 | R | MSR（Main Status Register）：bit 7 RQM (ready)、bit 6 DIO (direction)、bit 4 CB (busy)、bit 0-3 drive busy |
| 0x3F5 | RW | Data FIFO（command + result phase） |
| **0x3F7** | R | DIR（Digital Input Register）：**bit 7 = DSKCHG (disk change)** — XT BIOS 不讀這個！ |

### 7.2 FDC command set（INT 13h 用的最小）

| Opcode | Command | Length | Result | IRQ |
|---|---|---|---|---|
| 0x03 | SPECIFY | 3 | 無 | 否 |
| 0x04 | SENSE DRIVE STATUS | 2 | 1 (ST3) | 否 |
| 0x07 | RECALIBRATE | 2 | 無 | 是 |
| 0x08 | SENSE INTERRUPT STATUS | 1 | 2 (ST0+PCN) | 否 (清 IRQ) |
| 0x0A | READ ID | 2 | 7 | 是 |
| 0x0F | SEEK | 3 | 無 | 是 |
| 0x06 (with MFM bit 0x40) | READ DATA | 9 | 7 | 是 |

### 7.3 INT 13h function

| AH | Function |
|---|---|
| 0x00 | Reset disk system（FDC reset + recalibrate） |
| 0x01 | Get status（上次 result code） |
| 0x02 | Read sector（DMA + READ DATA） |
| 0x03 | Write sector |
| 0x04 | Verify sector |
| 0x05 | Format track |
| 0x08 | Get drive parameter（從 INT 1Eh table 回） |

### 7.4 INT 1Eh — Floppy parameter table（`F000:EFC7`）

11-byte 結構 for FDC SPECIFY + INT 13h disk geometry。標準 1.44 MB：
80 cyl × 2 head × 18 sec、512 byte/sector。

### 7.5 IRQ 6 / INT E handler

只 set BDA[0x3E] bit 7 = 「operation done」並 EOI。INT 13h 在這個 flag 上等。

### 7.6 Motor handling

INT 13h check BDA[0x3F] motor flag。Motor off 時 set DOR 內 motor bit
+ **等 500 ms** for spin-up。Motor timeout countdown 由 INT 8h tick handler
decrement BDA[0x40]；到 0 motor off。

**EMULATOR NOTE**：我們 FDC 瞬間完成、所以 500 ms BIOS stall 浪費。Phase
30.7a 在 `Fdc8272.WriteDor` 強迫所有 motor bit 1 讓 BIOS skip wait。

---

## 8. Timer — 8253 PIT、INT 8、INT 1Ah

### 8.1 Port

| Port | Channel | 用途 |
|---|---|---|
| 0x40 | 0 | System tick（驅動 IRQ 0 / INT 8）— 預設 reload 0x10000 → 18.2065 Hz |
| 0x41 | 1 | DRAM refresh（BIOS program 為 18） |
| 0x42 | 2 | Speaker gate（透過 port 0x61 bit 1） |
| 0x43 | — | Control register（channel select、mode、latch） |

### 8.2 INT 8h ISR

Increment BDA[0x6C-0x6F]（DWORD tick）。午夜 rollover（1,573,040 tick）
時 set BDA[0x70] flag。Decrement BDA[0x40] motor counter；零時 OUT 0x3F2
帶 motor bit 清。Call INT 1Ch（user timer chain、預設 IRET）。EOI to PIC。IRET。

### 8.3 INT 1Ah — Time of day

| AH | Function | Returns/Sets |
|---|---|---|
| 0x00 | Read tick | CX:DX = BDA[0x6C-0x6F]；AL = BDA[0x70]（上次 read 之後 rolled over、然後清） |
| 0x01 | Set tick | BDA[0x6C-0x6F] = CX:DX |
| 0x02 | Read RTC time | (用 CMOS if present) |
| 0x06 | Set RTC alarm | |

### 8.4 POST init 序列

```asm
mov al, 01010100b    ; channel 1、mode 2 (memory refresh)
out 43h, al
mov al, 12h          ; divisor 0x12 (= ~66 KHz refresh)
out 41h, al
mov al, 00110110b    ; channel 0、mode 3、lo+hi 16-bit
out 43h, al
xor al, al           ; divisor 0 = 65536 → 18.2 Hz
out 40h, al
out 40h, al
```

---

## 9. Interrupt Controller — 8259A PIC

### 9.1 Port

| Port | Direction | 用途 |
|---|---|---|
| 0x20 | RW | Command/status：write OCW2 (EOI = 0x20)、read ISR/IRR |
| 0x21 | RW | OCW1：IMR (interrupt mask) |

### 9.2 POST init（ICW1 + ICW2 + ICW3 + ICW4）

```asm
mov al, 13h         ; ICW1：edge-triggered、ICW4 needed
out 20h, al
mov al, 8           ; ICW2：vector base = 0x08 (IRQ 0 → INT 8)
out 21h, al
mov al, 9           ; ICW3：cascade master、IR2 上 slave (XT 單 PIC)
out 21h, al
mov al, 0FFh        ; OCW1：全 mask
out 21h, al         ; (BIOS 之後 unmask IRQ 0 + 1)
```

### 9.3 IRQ → vector mapping（init 後）

| IRQ | Vector | 用途 |
|---|---|---|
| 0 | INT 8 | PIT tick (18.2 Hz) |
| 1 | INT 9 | Keyboard |
| 2 | INT A | Cascade（AT 的 slave PIC；XT 不用） |
| 3 | INT B | COM2 |
| 4 | INT C | COM1 |
| 5 | INT D | (AT 的 LPT2、XT BIOS 不用) |
| 6 | INT E | Floppy controller |
| 7 | INT F | LPT1 / spurious |

EOI = `mov al, 0x20; out 0x20, al`。

---

## 10. DMA Controller — 8237

### 10.1 Port

| Port | Direction | 用途 |
|---|---|---|
| 0x00-0x07 | RW | Channel 0-3 base/count register（pair、flip-flop 分 lo/hi） |
| 0x08 | R | Status register：bit 0-3 = per channel TC reached |
| 0x09 | W | DMA request |
| 0x0A | W | Channel mask register |
| 0x0B | W | Mode register (per channel) |
| 0x0C | W | Clear flip-flop |
| 0x0D | W | Master reset |
| 0x0E | W | Clear mask register |
| 0x0F | W | All mask register |
| 0x81 | W | DMA Channel 2 page register (physical address 高 4 bit) |
| 0x82 | W | Channel 3 page |
| 0x83 | W | Channel 0/1 page |

### 10.2 POST init

```asm
xor al, al
out 0Dh, al          ; master reset
out 81h, al          ; 清 page
out 82h, al
out 83h, al
mov al, 01011000b    ; ch0 mode：read、autoinit、increment (DRAM refresh)
out 0Bh, al
mov al, 01000001b    ; ch1 mode：verify、single transfer
out 0Bh, al
; ch2、ch3 之後由 driver set
mov al, 0FFh
out 1, al            ; ch0 count low
out 1, al            ; ch0 count high
mov al, 1
out 0Ah, al          ; unmask ch0
```

Mode byte 格式（channel = bit 0-1）：
- bit 7-6：transfer mode（00=demand、01=single、10=block）
- bit 5：address dec (0) / inc (1)
- bit 4：auto-init enable
- bit 3-2：00=verify、01=write to mem、10=read from mem

---

## 11. PPI 8255 — Port 0x60-0x63

### 11.1 Port

| Port | Direction | 用途 |
|---|---|---|
| 0x60 | R | Port A：keyboard data |
| 0x61 | RW | Port B：control bit |
| 0x62 | R | Port C：DIP switch readback（多工） |
| 0x63 | W | PPI control word |

### 11.2 Port B (0x61) bit layout

| Bit | 用途 |
|---|---|
| 7 | Keyboard clear / shift register reset (pulse) |
| 6 | Reserved |
| 5 | Reserved |
| 4 | RAM parity check enable |
| 3 | I/O channel check enable |
| 2 | Port C selector（看 §11.3） |
| 1 | Speaker gate（跟 PIT ch2） |
| 0 | Speaker on |

### 11.3 Port C (0x62) bit layout — **pcxtbios reading 序列**

pcxtbios 讀 port 0x62 **兩次**、中間 `OUT 0xAD, 0x61`（= Port B = 0xAD）：

```asm
in al, 62h          ; FIRST read：memory size 在低 4 bit
and al, 0Fh
mov ah, al          ; save
mov al, 10101101b   ; 0xAD = 1010 1101
out 61h, al         ; toggle PPI selector
in al, 62h          ; SECOND read：video+floppy 在低 4 bit
mov cl, 4
shl al, cl          ; shift 到高 nibble
or al, ah           ; combine → equipment flag
```

**Port 0x62 的低 4 bit**：
- **Selector = 0**（第一次 read 時 Port B bit 2 不 set）：memory size bit 0-3
  - 0x03 = 256 KB planar (XT 預設)
- **Selector = 1**（`OUT 0xAD` 之後）：低 4 bit 是 video + floppy
  - bit 0-1：video（00=EGA、01=CGA 40x25、10=CGA 80x25、**11=MDA 80x25**）
  - bit 2-3：floppy count - 1（00 = 1 drive）

**EMULATOR NOTE（Phase 30.7c）**：我們 `Read62()` 基於 `_port61 & 0x04`
回對的 nibble。Gemini consult 在 bit 2 vs bit 3 作為 selector 不同意；
bit 2 是 user 驗證對 CGA work 的版本。

### 11.4 PPI control word（0x63 write）

`mov al, 10011001b; out 63h, al` — 把 Port A + C program 為 input、
Port B 為 output。標準 XT POST init。

---

## 12. CMOS / RTC

XT 5160 **無 battery-backed CMOS**。pcxtbios 透過 POST 的 `clock_check`
proc 支援 add-in RTC card。Probe base：0x2C1、0x241、0x341（per source）。

對我們 emulator、既然無真 RTC、INT 1Ah AH=00-01（tick-based time）夠。
AH=02/04（RTC read）被 FreeDOS `time` / `date` 命令打到；可以 stub。

---

## 13. Serial — 8250 UART、INT 14h

### 13.1 Per COM port 的 port

| COM | Base | Offset |
|---|---|---|
| COM1 | 0x3F8 | +0 data、+1 IER、+2 IIR、+3 LCR、+4 MCR、+5 LSR、+6 MSR |
| COM2 | 0x2F8 | (同 offset) |

### 13.2 Baud rate divisor table（`F000:E729`）

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

Set LCR bit 7（DLAB）之後寫到 UART divisor latch。

### 13.3 INT 14h function

| AH | Function |
|---|---|
| 0x00 | Init port（baud、parity 等） |
| 0x01 | Send char（poll LSR for TBE） |
| 0x02 | Receive char（poll LSR for data ready） |
| 0x03 | Get line + modem status |

**EMULATOR NOTE**：我們 stub 回 LSR=0x60（TBE+TSE 永遠 ready）、
MSR=0x30（CTS+DSR ready）。試圖寫 COM1 的 DOS program 看「永遠 ready」、
char 被靜默丟掉。

---

## 14. Parallel / Printer — INT 17h

### 14.1 Per LPT 的 port

| LPT | Data | Status | Control |
|---|---|---|---|
| LPT1 | 0x378 | **0x379** | 0x37A |
| LPT2 | 0x278 | **0x279** | 0x27A |

### 14.2 Status register bit（從 0x379 讀）

| Bit | 意義 |
|---|---|
| 7 | nBUSY（active low：1 = ready） |
| 6 | nACK |
| 5 | PAPER OUT |
| 4 | SELECT（online） |
| 3 | nERROR（active low：1 = no error） |

**EMULATOR NOTE**：stub 0x379/0x279 = 0xD8 = ready、no error、online、
no paper out。

### 14.3 INT 17h function

| AH | Function |
|---|---|
| 0x00 | Send byte |
| 0x01 | Init printer |
| 0x02 | Get status |

---

## 15. Bootstrap — INT 19h

`F000:E6F2`。透過 INT 13h AH=02、drive 0、CHS 0/0/1、1 sector 載 boot
sector 到 0000:7C00。跳 0000:7C00。Load fail retry；全部 retry fail 且
有 hard disk、試 hard disk 0x80；否則 fall through 到 INT 18h（ROM BASIC
/「No ROM BASIC」message）。

---

## 16. Power-On Self Test (POST) 序列

`F000:E05B` cold-start entry。大致：

1. Set DS=0、SS=0、SP=0x7C00。
2. Disable NMI（OUT 0xA0, 0x80 — 寫到 NMI mask）。
3. CPU type detect（`cpu_check`）。
4. 跑 optional CPU self-test（ifdef TEST_CPU）。
5. 8255 PPI init：`OUT 0x63, 0x99`。
6. 8253 PIT init（channel 0 = 18.2 Hz、channel 1 = refresh）。
7. 8259 PIC init（ICW1-3、OCW1 全 mask）。
8. 8237 DMA init。
9. Memory test（清 vector + BDA + user mem、count free）。
10. **Video init** — INT 10h 帶 equipment=mono（試 MDA）、然後 equipment=color
    （試 CGA）。最後成功的贏。
11. **PPI Port C read** for memory + floppy + video bit → BDA[0x10]
    equipment flag。
12. FPU detect（`fpu_check`）。
13. ROM scan 0xC0000-0xCFFFF for option ROM（看 `0x55 0xAA` signature、
    CALL 它們的 `+3` entry）。
14. Floppy controller init（透過 DOR bit 2 toggle 做 RESET）。
15. RTC detect。
16. 印 banner + 偵測到的 device。
17. INT 19h（bootstrap）。

---

## 17. pcxtbios-Specific Quirk

這些跟 IBM 5160 stock BIOS 不同。值得知道因為 Gemini-trained spec 描述
通常假設 IBM 並在這裡 break。

- **Port 0x62 selector**：pcxtbios 對 BOTH memory 跟 video+floppy 用
  port C 的 **低 nibble**（透過 Port B 多工），不是 Gemini 第一次答的
  高 nibble = 永遠 video+floppy。
- **Video init**：pcxtbios call INT 10h 兩次（mono 先、color 後）；
  LAST init 贏。如果我們 emulator 內兩個 VRAM（0xB0000 + 0xB8000）都
  read/writable（它們是 — 全 1 MB RAM）、CGA 永遠贏 → BDA[0x49] 即使
  --video=mda 要求也結尾 = 3。（Phase 30.8：強迫 CGA CRTC probe fail。）
- **TELETYPE SCROLL BUG（Phase 30.10 部分緩解）**：`int_10_func_14`
  （INT 10h AH=0Eh teletype）end-of-screen 隱式 scroll 透過 AH=08h
  讀 scroll-fill attribute。pcxtbios 內、read path 走
  `mov bh, 0; jb @@scroll_up; mov ah, 8; int 10h; mov bh, ah`
  （line 4138-4143）。AH=08h call 回目前 cursor cell 的 attribute —
  如果 THAT cell 剛好 attr=0（例如、前次 scroll）、read 給 0 → BH=0
  → 下次 scroll 用 attr=0 填新 bottom row → 隱形 char。
  Phase 30.10 緩解：
    - Binary patch `mov bh, ah`（在 pcxtbios.bin 內 file offset 0x16BE =
      F000:F6BE）→ `mov bh, 0x07` 讓 scroll fill 永遠正常 mono attribute、
      忽略 AH=08h 結果。最後 byte 的 checksum filler 調整補。
    - 加 `PcSystemRunner.EmulatorThreadProc` 內 runtime intercept 抓任何
      caller（含 BIOS-internal）text mode 下 AH=06 with BH=0、強制 set
      BH=0x07。
- **DOS DIRECT VRAM WRITE WITH ATTR=0（Phase 30.10、UNFIXED）**：即使
  有 scroll fix、FreeDOS kernel 的 CON driver 對 MDA framebuffer 做
  **直 word-write**（mov [es:di], ax pattern、char + attr byte 在一個
  instruction）。對某些 output path（特別是 `dir` body row）、DOS 用
  attr=0x00 直 write、讓那些 cell 即使有 char 也隱形。Symptom：F12
  framebuffer dump 顯示完整 dir content 為 char byte 但 row 15-22 attr=0/80。
  在同 timestamp 透過 `MDA_WRITE r16c05 CHAR=0x63 / ATTR=0x00` 診斷 =
  單 word write。不是 pcxtbios bug — 是 FreeDOS / DOS CON driver 對
  MDA mode 特有的 quirk。CGA mode 正常 work（DOS 對 color path 用
  不同 attr）。Fix 要嘛改 FreeDOS source、要嘛 HLE-intercept word-write
  到 VRAM。Phase 30.x+；延後。**`--video=cga` 是 working 建議**。
- **預設無 CMOS-based clock**；用 BDA[0x6C] 的 tick-counter。
- **Optional feature** 由 source 頂的 ifdef 控制 — IBM_PC、TURBO_ENABLED
  等。我們用的出貨 `pcxtbios.bin` 是 XT-mode、多數 feature on。
- **Manufacturing test mode**：SW1-1（port 0x62 SW1 nibble 的低 bit）= 0
  讓 BIOS 進 POST-loop test mode。在 stub 內**永遠 set 1**。
- **Floppy DSKCHG（port 0x3F7 bit 7）**：XT BIOS 完全不讀這個 port；
  FreeDOS 在某些 path 讀。我們 emulator 防禦性 stub 為 0x7F（no change）。

---

## 18. ROM 內的 Indexed Lookup Table

你可能要手動 disassemble 的 table 的快速參考。

| Table | ROM addr | Size | 用途 |
|---|---|---|---|
| `int_1D` video param | F000:F0A4 | 64+ byte | 4 × 16-byte CRTC init block（mode 0-1、2-3、4-6、7） |
| `regen_len` | (int_1D 內) | 8 byte | Per mode group 的 framebuffer size |
| `max_cols` | (int_1D 內) | 8 byte | 28h/28h/50h/50h/28h/28h/50h/50h |
| `mode` | (int_1D 內) | 8 byte | Per mode 的 mode control register 值 |
| `ascii` scancode → ASCII | F000:E885 | 64 byte | Unshifted PC-XT scan set 1 |
| `non_alpha` shifted | (ascii 後) | 32 byte | |
| `ctrl_upper` / `ctrl_lower` | (non_alpha 後) | 32+32 | |
| `alt_key` | (ctrl 後) | 32 byte | |
| `num_pad` | | 13 byte | "789-456+1230." |
| `func_table` FDC command | int_13 內 | 6 | INT 13h AH=02-07 per NEC opcode |
| `dma_table` | int_13 內 | 6 | Per FDC command 的 DMA mode |
| `int_1E` floppy param | F000:EFC7 | 11 byte | SPECIFY + geometry |
| `baud` | F000:E729 | 16 byte | 8 × WORD UART divisor |

---

## 19. 如何對 source 驗證假設

1. **WebFetch raw source**：
   `https://raw.githubusercontent.com/virtualxt/pcxtbios/master/pcxtbios.asm`
   （file ~250 KB；一次 WebFetch 回 ~25-30 KB processed、把 query target
   特定 symbol 或 label）

2. **Source 優於 Gemini** 的情況：
   - 假設涉及 bit 編號、port address、或 BDA offset。
   - Gemini 自相矛盾（會發生 — port 0x61 selector bit 2 vs bit 3 碰到）。
   - Symptom 是有清楚 control-flow 答案的 real-mode BIOS 行為（例如「pcxtbios
     poll port 0x3F7 for DSKCHG 嗎？」→ grep source for `3F7h` 或 `dx, 3F7`）。

3. **有用的 WebFetch prompt**：
   - "Find all instructions touching I/O port 0x3F7"
   - "Show the entire `int_10_func_X` subroutine"
   - "List all `DB`/`DW` tables in this file with their addresses"
   - "Find any `STI`/`HLT` patterns and what immediately precedes them"

4. **Gemini consultation 適當時**：
   - Generic spec 問題（「PPI Port B bit 7 在 IBM PC 上控制什麼？」）
   - Cross-reference check（「這個 BIOS code match 標準 IBM 慣例嗎？」）
   - 演算法設計（「我該怎麼模擬 keyboard FIFO + IRQ 互動？」）
   - 已經驗過 source 需要 outside 意見時。

5. **捕捉答案**：自動 save Gemini transcript 到
   `tools/knowledgebase/message/YYYYMMDD_HHMMSS.txt`（`gemini_query.py`
   tool 做這個）；在 commit message 跟 closure note 引用。

---

## 交叉參考

- `MD/design/30-fdc-dma-plan.md` — Phase 30 實作 plan
- `MD/performance/202605161900-realbios-keyboard-gui-end-to-end.md` — Phase 30.7a 收尾
- `tools/knowledgebase/message/2026051[5,6]_*.txt` — Gemini consultation 歷史
