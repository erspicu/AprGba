# Phase 28 — Intel PC Emulator (DOS / FreeDOS boot target)

> **Status**: ✅ **CLOSED** (2026-05-15). Architectural milestone met:
> FreeDOS 1.3 boots end-to-end (BIOS → boot sector → kernel.sys →
> COMMAND.COM → AUTOEXEC.BAT → FreeDOS LOGO printed). Closure note:
> `MD/performance/202605152200-pc-emulator-freedos-boot.md`. The
> remaining 28.8f / 28.9 (interactive A:\\> + dir/type/cls/ver) are
> deferred polish — blocked on the LOGO program waiting for a key
> that our `--keys=` script can't reach at runtime; needs a stdin
> pump (mechanical, not framework). 28.10 (mouse) and 28.11 (sound)
> were optional in the original plan.
>
> **Original status** (2026-05-11): 📋 **PLANNED**. Sub-project / 延伸 phase。
> 目的：用既有的 AprX86 (i8086 / i80186 / i80286) 把一台**最小可運行
> 的 IBM PC compatible** 拼出來，能 boot DOS / FreeDOS 到 prompt、可以
> 跑 .COM / .EXE 程式。**這是 framework 應用層 demo，不再是 CPU spec
> 工作** — 一旦動就會牽涉很多 IO 周邊。
>
> **Trigger**: 既有 AprX86 backend 已能正確跑 1.31M Tom Harte SST + 7
> 個 demo .com binary，但都不是「真實 PC software」。要展示框架真的
> 可商用，下一步是「跑 commercial OS」 — 最低成本選擇是 FreeDOS。
>
> **本文件目標**: 把這個 multi-week / multi-phase 工程切成可追蹤的
> sub-phase。每個 sub-phase 自成 deliverable，可以單獨 commit /
> demo / 回頭暫停。不期待一次做完。
>
> **Predecessor**: Phase 27 (i80286 complete) — `MD/performance/202605110200-i80286-pmode-fault-model-complete.md`.
> Note：FreeDOS 1.x 在 8086 real-mode 跑得起來，**不需要保護模式**。
> 保護模式只在跑 Win 3.x standard mode / DOS extender 才需要 — 那是
> Phase 29+ 才考慮的事。

---

## 0. Scope — clear lines

### In scope (Phase 28 全部 sub-phase 加總)

| 元件 | 最小需求 |
|---|---|
| **CPU** | i8086（已有，real mode）。保護模式不啟用、避免不必要複雜度 |
| **Memory** | 1 MB linear PA_mem + 區段化（BIOS / IVT / 影像記憶體 / 640KB conventional） |
| **BIOS** | INT 10h / 13h / 14h / 16h / 17h / 19h / 1Ah 走 **HLE**（C# trap handler），不跑真 BIOS image |
| **PIC** | 單 8259A（master）— IRQ 0/1 至少要動，IRQ 6/14 後續加 |
| **Timer (PIT)** | 8253 channel 0 → IRQ 0 (~18.2 Hz)，channel 2 → speaker gate |
| **Keyboard** | 8042 + 簡化版 — port 60h scancode + IRQ 1 (HLE INT 16h 也並行可用) |
| **CGA video** | text mode 80×25，B800:0000 memory-mapped framebuffer，char/attr 16 color |
| **Floppy disk** | INT 13h HLE 對接 `.img` 檔（raw 1.44 MB / 720 KB image），不模擬 765 FDC |
| **Hard disk** | INT 13h HLE 對接 `.img` 檔（FAT12/16 image），不模擬 ATA controller |
| **Speaker** | port 61h bit 1 + PIT channel 2 → 紀錄 on/off + freq，**不一定產出 audio** |
| **Mouse** | INT 33h HLE（DOS mouse driver level），不模擬 PS/2 controller |

### Out of scope（明確不做，免得 scope creep）

- **真 BIOS image LLE 作為主路徑** — Phoenix / AMI / Award 有 copyright，HLE 法律 + 工程都單純。
  - **註 (2026-05-15)**: `BIOS/firmware/` 有兩個 user 提供的 **public test BIOS** (`pcxtbios.bin` 8KB PC XT-class + `videorom.bin` 32KB Tseng Labs VGA)，無 license 問題；Phase 28.6+ 可選擇性走 LLE 路線取代部份 HLE INT handler 作為 learning value。HLE 仍然是預設。
  - **Update Phase 28.6-Modes**: `--bios=PATH` flag added; real BIOS image **does load** into 0xFE000-0xFFFFF and CPU executes its code starting from the real reset vector. POST gets stuck early because port I/O is unimplemented (DRAM check, PIC programming, FPU detection via 0xDB/0xD9 are all no-ops in i8086 spec). Visible result: black CGA framebuffer — real BIOS never reaches its first INT 10h print before hitting unimplemented hardware. Documented as expected behavior; the `--bios=` plumbing is correct, the limitation is port I/O scope.
- **CGA graphics mode**（mode 4/5/6 等 320×200 / 640×200 graphics）— text mode 夠 demo
- **EGA / VGA / SVGA**
- **80386+ 保護模式 / paging / V86 mode**
- **Adlib / Sound Blaster / MIDI**（PC speaker 上方）
- **Serial / parallel port 真實傳輸**（HLE INT 14h / 17h 收到當無事發生）
- **DMA controller** (8237)（floppy 真實傳輸才會用，HLE INT 13h 不需要）
- **CMOS / RTC 完整實作**（INT 1Ah 給時間 + 基本 NVRAM byte 就夠）
- **Multitasking / Windows 3.x / DOS extender (DPMI / VCPI)** — Phase 29+
- **PCI / USB / 任何 ISA expansion card**

> 原則：**HLE 優先**。能用 INT vector 攔截 + C# function 處理掉的事，
> 不要碰 port / IO controller emulation。等 HLE 整套通了能跑 FreeDOS 後，
> 再選擇性把某個 HLE 換成 LLE（learning value > emulation correctness 的話）。

---

## 1. 為什麼是 FreeDOS

| 候選 OS | Boot 難度 | 軟體 ecosystem | License | 結論 |
|---|---|---|---|---|
| **FreeDOS** | low（一張 1.44MB floppy / 一個 partition）| 完整 DOS ecosystem | GPL | ✅ 首選 |
| **MS-DOS 6.22** | low | 真正 DOS | proprietary | ❌ License 麻煩 |
| **PC DOS** | low | DOS | proprietary | ❌ 同上 |
| **Win 3.0/3.1** | high（需 386 + paging + V86）| GUI | proprietary | ❌ Phase 29+ |
| **Win 95** | very high（需 386 + 完整 chipset）| GUI | proprietary | ❌ 遠期 |
| **Linux ELKS** | medium | 極少 | GPL | 可，但比 FreeDOS 軟體少 |

FreeDOS 還有個額外好處：boot media 跟 `.img` 都社群現成下載得到、版本明確、
不需要自己組 boot disk。

---

## 2. 三個方法支柱 — 對應到 framework 既有套路

跟 NES / GB / GBA / x86 既有四 CPU 同樣的「驗證金字塔」：

| 支柱 | 既有對應 | Phase 28 對應 |
|---|---|---|
| **單元 oracle test** | Tom Harte SST、Blargg、jsmolka | DOS testbed → 寫 fixture .com 驗 INT 1xh 行為（自己寫，不靠 Tom Harte） |
| **Synthetic test ROM** | nestest.nes / cpu_instrs.gb / 27-pmode-*.com | **bootable .img**：寫一個極小 boot sector 印 "Hello AprPc!" 就 halt |
| **截圖證明** | result/gb/*.png 等 | **FreeDOS A:\\> prompt 截圖** + `dir`、`type readme.txt` 等簡單命令的執行截圖 |

---

## 3. 架構總覽

```
   CLI args ──▶  apr-pc.exe (single launchable, 不是 headless tool)
                     │
                     │  parse args → open UI window
                     ▼
   ┌─────────────────────────────────────────────────────────┐
   │   AprPc.Ui (WinForms window — main thread)              │
   │   ┌─────────────────────────────────────────────────┐   │
   │   │  Menu: File / Emulation / Disk / View / Help    │   │
   │   ├─────────────────────────────────────────────────┤   │
   │   │                                                  │   │
   │   │   CGA framebuffer canvas (640×400 px scaled)     │   │
   │   │   ← bitmap blt from emulator thread @ 60 Hz      │   │
   │   │                                                  │   │
   │   ├─────────────────────────────────────────────────┤   │
   │   │  Status bar: CPU MIPS / disk LED / capslock      │   │
   │   └─────────────────────────────────────────────────┘   │
   │           ▲                          │                   │
   │           │ framebuffer              │ key/mouse event   │
   │           │ (BitmapData)             ▼                   │
   │   ┌─────────────────────────────────────────────────┐   │
   │   │   PcSystemRunner (emulator thread)              │   │
   │   │                                                  │   │
   │   │   ├─▶ AprX86Backend (existing CPU)              │   │
   │   │   │     spec: i8086 / i80186 / i80286 (per CLI) │   │
   │   │   ├─▶ PcMemoryBus                               │   │
   │   │   ├─▶ HleBios (or LLE BIOS image if --bios=)    │   │
   │   │   ├─▶ Pic8259 / Pit8253 / Kbd8042               │   │
   │   │   └─▶ FloppyImg / HddImg                        │   │
   │   └─────────────────────────────────────────────────┘   │
   └─────────────────────────────────────────────────────────┘
```

### Threading 模型

- **UI thread**：WinForms message pump，負責 menu / framebuffer 顯示 /
  input event 捕捉。
- **Emulator thread**：跑 CPU dispatch loop、IO controllers、disk image
  讀寫。專屬 thread 不卡 UI。
- **Buffering**：framebuffer 用 `lock(...)` 共用 byte[]；input event 用
  thread-safe queue 從 UI thread 推到 emulator thread。
- **暫停 / 步進**：UI menu「Pause / Step / Reset」訊號 emulator thread
  在 dispatch loop 邊界 check。

> WinForms 是首選，理由：(1) .NET 10 內建（`Microsoft.WindowsDesktop.App`
> workload），(2) 單窗 + 選單 + bitmap blt 寫起來最少 line，
> (3) keyboard hook + timer 都 mature。WPF 之後想換不難（已有 thread
> 邊界）。Avalonia / MAUI 等跨平台 UI 不是 Phase 28 scope。

### 跟現有 codebase 的關係

- **新 project**：`src/AprPc.Cli/`（含 UI）— 雙身分：
  - 預設 launch → 開 UI 視窗（互動模式）
  - `--headless --screenshot=path` flag → 維持 headless 模式可給 CI 跑
- **重用**：
  - `AprX86.Cli.Cpu.X86JsonCpu` — 直接當 CPU 元件
  - `AprX86.Cli.Memory.X86Memory` 的設計 — 擴成 MMIO 友善版（PcMemoryBus）
  - 現有 CGA text-mode renderer（`src/AprX86.Cli/X86CgaRenderer.cs`）— 直接拿來用做 framebuffer → Bitmap 轉換
  - `spec/cpu/x86-16/i8086/cpu.json` — CPU spec 不動
- **新 spec**：
  - `spec/machines/ibm-pc-xt.json`（machine-level：memory map、IRQ wiring、port range）

### CLI 介面

```
apr-pc [options]

# 必選一個（互斥）
  --floppy-a=PATH           A: 軟碟映像（.img / .ima，1.44 MB / 720 KB / 360 KB）
  --hdd=PATH                C: 硬碟映像（.img，FAT12/16 partition）
  # 兩個可同時帶；BIOS 開機優先順序 A: → C:

# 系統設定（皆有預設）
  --cpu=i8086 | i8088 | i80186 | i80188 | i80286   [default: i8086]
  --bios=PATH               真實 BIOS image（LLE 模式）；省略則 HLE
  --memory=640k | 1m         conventional RAM 大小             [default: 640k]
  --backend=json | json-block | legacy              [default: json-block]

# UI 設定
  --window-scale=1 | 2 | 3                          [default: 2]
                            視窗放大倍率（CGA 640×400 像素 × scale）
  --window-title="..."      主視窗 caption                     [default: "AprPc"]
  --fullscreen              開啟全螢幕模式

# Headless / CI 模式
  --headless                不開 UI 視窗
  --screenshot=PATH         指定截圖輸出（headless 配合）
  --max-cycles=N            執行 N cycle 後 halt（headless 自動結束）
  --frames=N                執行 N 個 frame 後 halt

# Debug
  --trace-int               每個 INT 指令印 vector + AH（log 到 stderr）
  --trace-io                每個 IN/OUT port 印 port + value
  --trace-irq               每次 PIC IRQ deliver 印 vector
  --verbose                 啟動時印完整 system config

# 範例
  apr-pc --floppy-a=BIOS/freedos-1.3.img
  apr-pc --hdd=disks/c.img --cpu=i80286
  apr-pc --floppy-a=BIOS/test-boot.img --window-scale=3 --trace-int
  apr-pc --floppy-a=BIOS/test.img --headless --screenshot=temp/out.png --max-cycles=10000000
```

### UI 視窗 menu

```
File
  Open Floppy A...
  Open HDD...
  Recent Files →
  Exit

Emulation
  Reset (Ctrl+R)
  Pause / Resume (F5)
  Step One Instruction (F10)
  Step One Frame (F11)

Disk
  Eject Floppy A
  Floppy Write-Protect ▣
  HDD Read-Only ▣

View
  Window Scale  →  1× / 2× / 3×
  Show CPU MIPS ▣
  Show Disk LED ▣
  Take Screenshot... (PrintScreen)

Help
  Keyboard Shortcuts
  About...
```

---

## 4. Sub-phase 切分

每個 sub-phase = 1 commit milestone。Sub-phase 內部用 micro-sprint 推進。

### Phase 28.0 — Project scaffolding + UI shell（~2 day）

| Deliverable | Done when |
|---|---|
| `src/AprPc.Cli/AprPc.Cli.csproj` — TargetFramework `net10.0-windows`, `UseWindowsForms=true`，references AprCpu.Core + AprX86.Cli | `dotnet build` passes (含 Windows desktop workload) |
| `Program.cs` — CLI arg parsing（§3 CLI 介面），分流：default → `AprPc.Ui.MainForm.Run(...)`；`--headless` → `HeadlessRunner.Run(...)` | `apr-pc --help` 印完整 usage；`apr-pc` （無 arg）→ 開空 UI 視窗 |
| `MainForm.cs`（WinForms）：menu bar、固定 640×400 px canvas、status bar，視窗放大 scale 從 `--window-scale` 來 | 視窗開得起來；menu 各 entry 都連到 dummy handler（顯示 "TODO" MessageBox） |
| `PcSystemRunner.cs`（emulator thread skeleton） — owns CPU + memory + (empty) IO components；thread-safe Start/Pause/Stop/Step API | UI thread 跟 emulator thread 互動 plumbing 完成（先跑 dummy loop） |
| `spec/machines/ibm-pc-xt.json` 骨架（先抄 `gba.json` 結構，標記 TODO） | SpecLoader 載得起來 |
| MD/design/28-... 更新 ✅ status | This doc, with status table |

**Demo**: `apr-pc` → 開空白 UI 視窗，menu 都點得開（但都 "TODO"），右下角 status bar 顯示 "Idle / 0 MIPS"。

**Commit**: `feat(N28.0): AprPc.Cli + WinForms UI shell + emulator thread plumbing`

### Phase 28.1 — Memory map + IVT + reset vector（~1 day）

| Deliverable | Done when |
|---|---|
| `PcMemoryBus` 1 MB linear，region table 從 `spec/machines/ibm-pc-xt.json` 讀 | Memory read/write 對地址範圍正確 |
| IVT @ 0000:0000 — 256 個 4-byte vector slot | Reset 後 IVT slot 全 0；C# 可寫入 |
| BIOS ROM region @ F000:0000-F000:FFFF — 暫塞 NOP + jmp 0xFE05B (= cold-start) | CS:IP = FFFF:0000 fetch 到 jmp 指令 |
| Memory write @ B800:0000-B8FFF — 給 CGA framebuf | 寫不錯誤、暫不做 render |
| Equipment word @ 0040:0010 = 0x0061（floppy present, 80×25 color, no FPU） | Read 回正確值 |
| Memory size @ 0040:0013 = 0x0280（640 KB） | 同上 |

**Test**: 一個 .asm 寫 `mov bx, [0x410]; hlt` 跑下去要看到 BX = 0x0061。

**Commit**: `feat(N28.1): PC memory map + reset vector + BIOS data area`

### Phase 28.2 — HLE BIOS framework + INT 10h (video)（~2 day）

| Deliverable | Done when |
|---|---|
| `HleBios` class — 攔截 CPU 的 INT 指令、查 AH 分派 | INT instruction 跳到 HleBios.Dispatch() 而不是 IVT |
| 機制：BIOS ROM 內每個 INT vector 指到 F000:某 offset 的 `IRET` opcode；CPU 真的去 push CS:IP+FLAGS、jmp F000:offset、跑 IRET — 但中間 host 攔截 fetch 觸發 HLE call。或者用 magic instruction trap pattern。決定走哪條 → 寫在 `MD/design/28.2-hle-bios-mechanics.md` | 機制 decision 寫好 + 實作 |
| **INT 10h** subset:<br>  • AH=0Eh teletype output (`char to current cursor`)<br>  • AH=02h set cursor position<br>  • AH=03h get cursor position<br>  • AH=06h scroll up<br>  • AH=09h write char + attr at cursor<br>  • AH=0Fh get current video mode<br>  • AH=00h set video mode（只支援 mode 3 = 80×25 color）| 各自寫 unit test：mov ah, <fn>; int 0x10 → 看 CGA framebuf / cursor 狀態 |
| **UI 整合**：MainForm canvas 每 16.7 ms（60 Hz）從 emulator thread 拉 B800 framebuffer → BitmapData blt | 跑 28.2-hello.com 即時看到 "Hi" 出現在 UI 視窗 |
| Headless 模式：`--screenshot=` 時掃 B800 framebuffer 渲染 PNG | 既有 `X86CgaRenderer` 接過來 |

**Demo**: 寫一個 `28.2-hello.com`：
```asm
mov ah, 0x0E       ; teletype
mov al, 'H'
int 0x10
mov al, 'i'
int 0x10
hlt
```
跑下去截 PNG 看到 "Hi" 在左上角。

**Commit**: `feat(N28.2): HLE BIOS framework + INT 10h teletype/cursor/scroll`

### Phase 28.3 — INT 16h (keyboard) + 8042 + IRQ 1（~2 day）

| Deliverable | Done when |
|---|---|
| `Kbd8042` 元件 — scancode buffer + port 60h read + port 64h status | 港內讀寫對 |
| Host 端 input pump — `MainForm.KeyDown/KeyUp` → thread-safe queue → emulator thread → 8042 scancode buffer。Headless 走 `--keys=` 參數模擬 | UI 視窗 focus 時敲 key 看得到 scancode 進 buffer |
| IRQ 1 wiring：scancode 寫入時觸發 PIC IRQ 1 → CPU acknowledge → IVT jump | CPU 看得到 INT 9 |
| **INT 16h** HLE:<br>  • AH=00h read char and scancode (block until key)<br>  • AH=01h check if key available (zero flag)<br>  • AH=02h read shift flags state | unit test 通 |
| BIOS Data Area: keyboard buffer @ 0040:001E-003D + head/tail pointer | 一致 |

**Demo**: 寫一個 `28.3-echo.com` — INT 16h 讀 char、INT 10h 印 char、按 ESC 停。手動鍵入 "hi" 應在畫面看到 "hi"。

**Commit**: `feat(N28.3): HLE INT 16h + 8042 scancode buffer + IRQ 1`

### Phase 28.4 — PIT 8253 + IRQ 0 timer tick + INT 1Ah（~1-2 day）

| Deliverable | Done when |
|---|---|
| `Pit8253` — channel 0 mode 3 square wave，default reload = 0x0000 = 65536（~18.2 Hz） | port 40h read/write 對 |
| Tick counter — 每 N CPU cycle 增加 channel 0 latch / 觸發 IRQ 0 | scheduler decide |
| BIOS Data Area: tick count @ 0040:006C (DWORD), midnight rollover @ 0040:0070 (BYTE) | INT 1Ah read 對 |
| **INT 1Ah** HLE: AH=00h get ticks since midnight | 跑 1 秒看 tick 跳 ~18 次 |
| Channel 2 + port 61h speaker gate — 紀錄 on/off + 計算 freq（不一定要 audio out） | port 61h I/O 對 |

**Commit**: `feat(N28.4): PIT 8253 + IRQ 0 timer tick + INT 1Ah`

### Phase 28.5 — Floppy/HDD INT 13h HLE + .img loader（~2 day）

| Deliverable | Done when |
|---|---|
| `FloppyImg` — 讀 `.img` 檔，CHS/LBA 互轉 | 1.44 MB image 對 CHS = 80c × 2h × 18s × 512B = 1,474,560 bytes |
| `HddImg` — 同樣，多 partition table 支援 | C:.img 可載 |
| **INT 13h** HLE:<br>  • AH=00h reset<br>  • AH=01h get last status<br>  • AH=02h read sectors (CHS)<br>  • AH=03h write sectors (CHS)<br>  • AH=08h get drive params<br>  • AH=15h get drive type | 讀寫 image file 對 |
| Drive number wire: 00h = A:, 01h = B:, 80h = C: | INT 13h AL=80h 對應 HDD |

**Test**: 寫 `.img` 含 boot sector + 一些 sector，用 .com 程式 INT 13h 讀進 0x7C00、印 head magic byte。

**Commit**: `feat(N28.5): INT 13h HLE + floppy/HDD .img loader`

### Phase 28.6 — Bootstrap (INT 19h) + 第一個 boot sector 跑起來（~1 day）

| Deliverable | Done when |
|---|---|
| **INT 19h** HLE：load A: sector 0 (CHS=0,0,1) 進 0x7C00、設 DL=00h、jmp 0000:7C00 | trigger 後 IP=7C00 |
| Reset 流程：CPU reset → FFFF:0000 → BIOS stub jmp 到 boot 流程 → INT 19h | 程式 flow OK |
| **手寫 boot sector demo**：50 行 8086 asm，從 0x7C00 起 - 印 "AprPc bootstrap OK" via INT 10h teletype - 進無限 loop | 跑 + 截圖 + 看見 "AprPc bootstrap OK" |
| 用 NASM build `test-roms/x86/pc/28-hello-boot.asm` → `28-hello-boot.img`（512 byte boot sector + zero padding 到 1.44 MB） | image 對 |

**Commit**: `feat(N28.6): INT 19h bootstrap + first booted .img demo`

**這是 phase 28 的第一個 visual milestone** — 「我們有 PC 可以 boot 了」。

### Phase 28.7 — IRQ delivery model（~1-2 day）

| Deliverable | Done when |
|---|---|
| `Pic8259` 單片 — IRQ mask register / ISR / EOI / port 20h-21h | port I/O 對 |
| CPU 端 interrupt acknowledge cycle — IF flag 處理、push FLAGS+CS+IP、查 IVT、jump、IRET 清 IF | 跟 NES 既有 IRQ delivery 機制對齊 |
| Scheduler：每 N cycle 檢查 pending IRQ，按 priority decide | timer + keyboard 都能正確 deliver |
| `sync` micro-op：x86 的 EI-class instructions (STI 後 1 instr 才能 deliver) 透過 framework 既有 `sync` 機制 | LR35902 EI 同設計 |

**Test**: timer tick + STI 後 IRQ 0 handler 真的有跑（counter 增加）。

**Commit**: `feat(N28.7): Pic8259 + IRQ delivery + CPU interrupt acknowledge`

### Phase 28.8 — FreeDOS boot attempt（~3-5 day，預期會卡很多次）

| Deliverable | Done when |
|---|---|
| 下載 FreeDOS 1.3 floppy boot disk image | `.img` 就位 |
| 跑 `apr-pc --floppy=fdboot.img --max-cycles=...` | 不要 crash |
| 第一個 boot sector → IO.SYS → MSDOS.SYS → COMMAND.COM 整段流程能跑 | 截圖看到 `A:\>` prompt |
| 通常會卡的點:<br>  • INT 21h DOS calls — 由 FreeDOS kernel 自己 implement，我們不用 HLE！<br>  • 但 INT 25h / 26h absolute disk read/write 我們要支援<br>  • CMOS reads (port 70h/71h) — 至少 byte 0x10 給 floppy type<br>  • DMA controller stubs（即使不傳輸也要 port 不爆）<br>  • A20 gate (port 92h)（real mode 8086 通常不碰，但 FreeDOS 可能查） | 各個 corner case 修正一條一條 |

**這 phase 註定會 split 成很多 micro-sprint**。每解一個 hang 都是一個 commit。

**Commit prefix**: `fix(N28.8.X): <reason FreeDOS 卡住的 thing>`

### Phase 28.9 — Interactive `A:\>` + 基本 command 跑得了（~2 day）

| Deliverable | Done when |
|---|---|
| UI keyboard input 在 FreeDOS prompt 下正常運作（包含 BackSpace / Enter / arrow keys / 大小寫） | 鍵入 `dir` 看得到結果 |
| PrintScreen hotkey → 自動寫 PNG 到 `temp/` | menu / hotkey 都動作 |
| 跑 FreeDOS 內建 4 個基本 command 都成功：<br>  • `dir` — 列 A:\ 內容<br>  • `type readme.txt` — 印檔案內容<br>  • `cls` — 清螢幕<br>  • `ver` — 印 FreeDOS 版本 | 4 個截圖證明 |
| Headless 模式：`--keys="dir\r"` style script input 也通 | CI 友好 |

**Commit**: `feat(N28.9): FreeDOS interactive mode + 4 basic commands captured`

### Phase 28.10 — Mouse INT 33h HLE（~1 day，optional）

| Deliverable | Done when |
|---|---|
| INT 33h driver-level: AH=00 reset / 01 show / 02 hide / 03 get state / 0B get motion delta | 可餵假 mouse delta 給 DOS program |
| Demo: FreeDOS edit (`edit`) 用 mouse 點選 | 截圖 |

**Commit**: `feat(N28.10): INT 33h HLE mouse driver`

### Phase 28.11 — Sound (PC speaker) PCM 輸出（~1-2 day，optional）

| Deliverable | Done when |
|---|---|
| 從 PIT channel 2 + port 61h 推導 freq / on-off | log 即可 |
| 簡易 PCM 累加器 → WAV 輸出（`--audio=out.wav`） | 開檔聽得到 beep |
| Demo: 跑某個 DOS program 會 beep（boot success beep / error beep） | 對 |

**Commit**: `feat(N28.11): PC speaker PCM output`

### Phase 28.12 — 收口 + closure docs（~1 day）

| Deliverable | Done when |
|---|---|
| Closure note: `MD/performance/<timestamp>-pc-emulator-freedos-boot.md` | 完整 sprint 表 + 截圖矩陣 + perf 數字 |
| README.md 新增「PC harness」section | bilingual |
| Phase 28 plan 標 ✅ COMPLETE | this doc |

**Commit**: `docs(N28.12): Phase 28 closure — FreeDOS boots on AprPc`

---

## 5. 各 phase 截圖路徑

```
result/pc/
├── 28.2-hello.png                  ← INT 10h teletype "Hi"
├── 28.3-echo.png                   ← INT 16h read + echo
├── 28.6-bootstrap.png              ← 自寫 boot sector "AprPc bootstrap OK"
├── 28.8-freedos-boot.png           ← 第一次 FreeDOS A:\> prompt
├── 28.9-dir.png                    ← FreeDOS A:\> dir 結果
├── 28.9-type.png                   ← A:\> type readme.txt
├── 28.9-cls.png                    ← A:\> cls 後乾淨畫面
└── 28.9-ver.png                    ← A:\> ver 印 FreeDOS 版本
```

跟既有 `result/gb/`、`result/gba/`、`result/nes/`、`result/x86-16/` 並列 —
就是「framework 真的能跑 DOS」的視覺證據。

---

## 6. 時程估計

| Phase | 估計 | Cumulative |
|---|---|---|
| 28.0 | **2 day**（UI shell 加進來） | 2 |
| 28.1 | 1 day | 3 |
| 28.2 | 2 day | 5 |
| 28.3 | 2 day | 7 |
| 28.4 | 1-2 day | 9 |
| 28.5 | 2 day | 11 |
| 28.6 | 1 day | 12 |
| 28.7 | 1-2 day | 14 |
| 28.8 | **3-5 day**（高度不確定）| 17-19 |
| 28.9 | 2 day | 19-21 |
| 28.10 | 1 day（optional） | 20-22 |
| 28.11 | 1-2 day（optional） | 21-24 |
| 28.12 | 1 day | 22-25 |

**~3-4 週** 連續工作日。配合 /loop 跟雜事，現實 1.5-2 個月。

關鍵不確定性集中在 28.8（FreeDOS 真的 boot）— 經驗上 PC compatibility 的尾巴永遠很長。但跟 phase 1-10 一樣，一條一條解就行。

---

## 7. 風險 + Mitigation

| 風險 | 嚴重度 | Mitigation |
|---|---|---|
| **FreeDOS 用到某個我們沒實作的 INT call** | 高 | 加 trace mode — 不認得的 INT call dump argument + log，逐一補 |
| **CPU side effect bug 卡 boot 流程** | 中 | 既有 Tom Harte SST 已涵蓋 1.31M case；新 bug 一律先寫單元 test 重現再修 |
| **IRQ delivery timing 不對導致 keyboard buffer overflow / drop** | 中 | 用既有 framework 的 `sync` micro-op 機制 — 跟 LR35902 EI 同設計 |
| **HLE 跟 LLE 邊界沒劃清楚，某個 DOS program assume real port I/O** | 中 | trace 時記錄哪個 INT 被 HLE 攔了；如果 program 直接打 port 而不走 INT，得多做 port stub |
| **Disk image format 細節（FAT12 boot sector header / extended BPB）** | 低 | 直接用 FreeDOS 既有 image，自己不組 |
| **scope creep — 想做 graphics mode / Adlib / 386** | 高 | 嚴守第 0 節 out-of-scope；想做就開 Phase 29+，不污染 28 |
| **效能差 → FreeDOS 啟動慢** | 低 | json-block backend 已 218 MIPS；real-mode DOS 很輕，可以接受 |

---

## 8. 跟既有 doc 的關係

- **#20 adding-a-new-cpu.md** — 不適用；這 phase 沒加新 CPU
- **#21 spec-driven-runtime.md** — `spec/machines/ibm-pc-xt.json` 沿用本 doc 模式
- **#15 timing-and-framework-design.md** — IRQ delivery 走 sync micro-op 套路
- **#23 cpu-spec-inheritance.md** — 不適用；CPU spec 不變
- **#27 i80286-completion-plan.md** — 保護模式 work 是 Phase 28 的 *successor*，不是 prerequisite
- **既有 NES IRQ 機制** — 直接 mirror（PIC + IRQ vector + CPU interrupt ack）
- **既有 X86CgaRenderer.cs** — 直接重用

---

## 9. Sprint status

| Phase | Sub-phase | Status | Commit | 完成日 |
|---|---|---|---|---|
| 28.0  | Project scaffolding + UI shell | ✅ | `245729e` | 2026-05-11 |
| 28.1  | Memory map + IVT + BIOS Data Area + reset vector | ✅ | `35a73ea` | 2026-05-11 |
| 28.2  | HLE BIOS framework + INT 10h + UI framebuffer blt | ✅ | `413afeb` | 2026-05-11 |
| 28.3  | INT 16h + keyboard buffer + WinForms KeyDown queue | ✅ | `92d8e11` | 2026-05-11 |
| 28.4  | PIT 8253 + INT 1Ah + wall-clock BDA tick | ✅ | `ee1098e` | 2026-05-11 |
| 28.5  | INT 13h floppy/HDD HLE + DiskImage + .img loader | ✅ | `d5cceee` | 2026-05-11 |
| 28.6  | INT 19h bootstrap (partial-LLE) + self-written boot sector | ✅ | `ebbcaab` | 2026-05-11 |
| 28.6-LLE | Upgrade to full LLE bootstrap (real 37-byte 8086 routine at F000:E05B) — unblocked by 28.8a/c far-control-flow opcodes | ✅ | `403953d` | 2026-05-15 |
| 28.6-Modes | `--bios-mode=hle\|lle` CLI flag + `--bios=PATH` real-BIOS-image load | ✅ | (this commit) | 2026-05-15 |
| 28.7  | Pic8259 + IRQ delivery model (PIT IRQ 0 + keyboard IRQ 1) | ✅ | `8c8f2b6` | 2026-05-11 |
| 28.8a | FreeDOS boot blocker #1 — add 0xEA (JMP ptr16:16) to i8086 spec | ✅ | `e71f15a` | 2026-05-15 |
| 28.8b | FreeDOS boot blocker #2 — install HLE INT 8/9 defaults; IRQ 0 → IVT[8]=0:0 wandering | ✅ | `9012790` | 2026-05-15 |
| 28.8c | FreeDOS blockers #3-4 — add 0xCB/0xCA RETF + 0x9A CALL far + pre-install all 256 IVT defaults | ✅ | `7d7bda3` | 2026-05-15 |
| 28.8d | FreeDOS kernel full banner (3 lines + WATCOMC + FAT32 + Copyright) printed | ✅ | `837ede2` | 2026-05-15 |
| 28.8e | FreeDOS COMMAND.COM (FreeCom 0.85a) loads + executes AUTOEXEC.BAT → FreeDOS ASCII logo | ✅ | `7a3b8ef` | 2026-05-15 |
| 28.8f | Interactive `A:\\>` prompt | ⏳ deferred — LOGO program (run by AUTOEXEC.BAT) blocks waiting for key; current `--keys=` script only injects at boot. Needs run-time stdin pump OR image with no LOGO. Mechanical, not framework. |
| 28.9 | dir/type/cls/ver screenshot matrix | ⏳ deferred — gated on 28.8f. INT 16h + 21h DOS calls already in place; mechanical. |
| 28.10 | INT 33h mouse HLE | ⏳ deferred (optional in original plan) |
| 28.11 | PC speaker PCM output | ⏳ deferred (optional in original plan) |
| 28.12 | Phase 28 closure docs (this `MD/performance/` note + plan doc + roadmap update) | ✅ | (this commit) | 2026-05-15 |
| 28.8x | block-JIT INT instruction loses HLE trap (use --backend=json for FreeDOS) | ⏳ deferred | — | — |
| 28.8  | FreeDOS boot | ⏳ pending | — | — |
| 28.9  | Interactive commands | ⏳ pending | — | — |
| 28.10 | INT 33h mouse | ⏳ optional | — | — |
| 28.11 | PC speaker PCM | ⏳ optional | — | — |
| 28.12 | Closure docs | ⏳ pending | — | — |

---

## 10. Phase 29+ 後續可能性（不在本 doc 規劃，只列）

| 主題 | 為什麼有意思 |
|---|---|
| **386 + 保護模式 / paging / V86 mode** | Phase 27b 已做了 286 protected-mode foundations；386 paging / V86 是自然延伸；可讓 Win 3.x standard mode 跑 |
| **EGA / VGA video** | graphics mode；可跑 DOS game（Wolfenstein 3D / DOOM 等等） |
| **Adlib / Sound Blaster** | 音效；DOS game 的核心 |
| **Real BIOS image LLE** | 學習意義；replace HLE INT handler |
| **PCI / IDE / 真實 ATA controller** | for Win 9x compatibility |
| **Multi-machine spec** — XT / AT / 286 / 386 系列 | machine-spec inheritance |

---

## 11. Gemini 諮詢時機（per `MD/process/02-ai-collaboration-workflow.md` Pattern B）

Phase 28 比前面所有 phase 都更容易踩 spec 細節坑 — IBM PC 周邊每個都
40 年歷史、各家 BIOS 處理 quirk 不同、Intel 寫的不一定是業界實際採用的。
**遇到下面這類問題優先查 Gemini，不要憑訓練資料硬寫**：

| 問題類型 | 例子 | 為什麼問 Gemini |
|---|---|---|
| **PC 周邊 controller register 細節** | "8259 ICW1 ICW2 順序、ICW4 ELCR 哪台 PC 有"、"8042 status port bit 7 含意"、"PIT mode 2 vs mode 3 在 channel 0 行為差" | vendor datasheet 跟業界實作有出入；Gemini 看過多種 emulator source |
| **BIOS INT 行為 corner case** | "INT 13h AH=02h 讀超過軌道末端怎麼處理"、"INT 10h AH=06h 滾動 0 行的意思（清屏 vs no-op）"、"INT 16h AH=00h 跟 AH=10h 差別" | Intel BIOS spec 寫得不全；常見 commercial BIOS 行為要看實作 |
| **DOS internals 假設** | "FreeDOS boot sector 是不是先用 INT 13h CHS 還是直接 BIOS table"、"INT 25h/26h FAT12 sector 編號"、"COMMAND.COM resident size" | DOS 內部不是 spec 寫死，要看 source |
| **8086 silicon quirk 確認** | "PUSH SP 是 pre-dec 還是 post-dec"（已確認 pre-dec）、"REP MOVSB 在 IRQ 觸發時 CX 行為" | Intel 文件 ambiguous 時 query |
| **業界做法對照** | "DOSBox / PCem / 86Box 在這個 case 怎麼處理"、"做最小 PC 主流選擇是 HLE 還是 LLE" | Phase 28 跨完整實作可以省 corner case 數天 |

**怎麼問**（per workflow doc）：
- 用英文，一次一個問題
- 附上 context：版本、我們目前怎麼做、為什麼覺得有問題
- `python tools/knowledgebase/gemini_query.py "<question>"` — 自動 log 到 `tools/knowledgebase/message/`

**不要為了問而問**：能自己讀 Intel 80286 PRM / Apr86 source / FreeDOS
source 解掉的不需要 Gemini。Pattern A（不問）90% 的工作 OK；Pattern B
（問）保留給 fork point。

---

## 12. 開工前 checklist

開 phase 28.0 前先確認：

- [x] **FreeDOS 1.3 floppy image** 下載到 `BIOS/freedos-1.3-floppy.img`（不入 repo）
      → 2026-05-11 從 ibiblio.org `FD13-FloppyEdition.zip` (20.7 MB) 取出
      `144m/x86BOOT.img` (1,474,560 bytes)；SHA256
      `3F7834EA4575BA05D106E4B8F59F886DA7BFB1979EE386BE2A2DEBA8DF518925`；
      OEM 簽名 `FRDOS5.1`、boot magic `55 AA` ✓。
- [x] **NASM 可用**（已裝 `C:\Program Files\NASM\nasm.exe`，版本 3.01）。
- [x] **既有 `apr-x86 --variant=i8086` Tom Harte SST clean**（regression baseline）
      → X86TomHarteTests subset 147/147 通過 (3m 55s)。額外
      `apr-x86 --tomharte=OldProject/8088/v2/00.json.gz` 跑 ADD r/m8,r8
      10000 case 全綠 (1.73s)。
- [x] **既有 6 個 x86-16 demo screenshots SHA256 全綠**（regression baseline）
      → T2 18 PNGs (6 demos × 3 backends) pixel-identical；
      i8086 vs i80186 variant matrix 6 demos pixel-identical ✓。

**Checklist 全 ✅ 於 2026-05-11**。可動工從 28.0 開始。

---

## 13. 為什麼這 phase 不靠 spec 機制做

跟 Phase 24-27 的 CPU spec inheritance 不同 — Phase 28 主要的擴充
是「IBM PC 周邊系統」，這些是 *machine-level* 而不是 *ISA-level* 的事。

`MachineSpec`（已存在 / doc #19, #22）已經有 memory map 的 declarative
表達，可以承載 `spec/machines/ibm-pc-xt.json`。但 IO controller behavior
（8259 / 8253 / 8042 / disk emulation / HLE INT handler）是 host code，
不會宣告化（會變成另一個 mini-emulator-spec language 才合理）。

**Phase 28 的價值不在「再次驗證框架可宣告化」** — 那部分 Phase 27 已經
做完。Phase 28 的價值在「把框架推到能跑 commercial OS」這個工程
milestone，是 framework genericity claim 的最後一塊拼圖。

> **bottom line**：Phase 28 是 application 層、不是 framework 層。
> 完工後 framework 還是那個 framework；多的是一個 *consumer*。
