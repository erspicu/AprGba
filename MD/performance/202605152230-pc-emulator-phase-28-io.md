# Phase 28.IO — port I/O dispatch + FPU detection stub

2026-05-15 加進 Phase 28 的 I/O-machinery sub-phase 的收尾筆記
（同日 follow-up FreeDOS-boot 收尾）。User trigger：
*「補上欠缺且阻礙 booting 的 IO 功能」* — 取代擋住 real PC/XT BIOS POST
任何進展的 no-op IN/OUT stub。

## Scope

Real PC BIOS POST 程式在碰 framebuffer 之前，用幾十個 `IN AL, DX` /
`OUT DX, AL` 序列對 DMA / PIC / PIT / 8042 / video CRTC / CMOS register
做 DMA / 程式化動作。我們既有的 emitter（`X86InImm8Emitter`、
`X86OutImm8Emitter`、`X86InDxEmitter`、`X86OutDxEmitter`）把這些 compile
成 LLVM IR、丟掉 port number、return 0（read）或什麼都不做（write）。
FreeDOS 透過 HLE 不在意，因為 HLE INT handler 繞過真實硬體 programming，
但 real BIOS code 在意 — 第一個 DMA refresh setup OUT 到 port 0x0A0
靜默失敗、BIOS 從那裡 roll 進 infinite-loop garbage。

這個 phase 加 **functional port routing 透過單一 dispatch table**，
讓 real DOS / BIOS code 可以對話。

## 出貨內容

### 1. JIT pipeline — port extern

`src/AprCpu.Core/IR/MemoryEmitters.cs` 多了 4 個新 extern、mirror 既有的
`MemoryRead8` / `MemoryWrite8` / `MemoryRead16` / `MemoryWrite16` pattern：

- `extern PortRead8(port: i16) → i8`
- `extern PortRead16(port: i16) → i16`
- `extern PortWrite8(port: i16, value: i8) → void`
- `extern PortWrite16(port: i16, value: i16) → void`

加上 helper `CallPortRead8(ctx, portI16, label)`、`CallPortRead16`、
`CallPortWrite8`、`CallPortWrite16`，emitter 用來 lower `IN`/`OUT` micro-op。

### 2. 重寫 x86 emitter

`src/AprCpu.Core/IR/X86_16Emitters.cs`：
- `X86InImm8Emitter` — `IN AL, imm8` (E4 ib) → `AL = PortRead8(imm8)`
- `X86OutImm8Emitter` — `OUT imm8, AL` (E6 ib) → `PortWrite8(imm8, AL)`
- `X86InDxEmitter` — `IN AL/AX, DX` (EC / ED) → 路由到 PortRead8 或 PortRead16
- `X86OutDxEmitter` — `OUT DX, AL/AX` (EE / EF) → 路由到 PortWrite8 或 PortWrite16

之前這些是永遠 return 0（read）或什麼都不做（write）的 no-op stub。

### 3. PcPortBus dispatch

新檔 `src/AprPc.Cli/Hardware/PcPortBus.cs` — singleton port dispatch、
透過 `AprX86.Cli.Cpu.X86JsonCpu` 的 `[UnmanagedCallersOnly]` shim 鉤進
LLVM extern：

| Port 範圍 | Device | 行為 |
|---|---|---|
| 0x20 / 0x21 | PIC 8259A | read 0x21 回 `_pic.GetImr()`；read 0x20 回 ISR/IRR=0 |
| 0x40 / 0x41 / 0x42 | PIT 8253 counter | read 回 0（refresh logic 夠了） |
| 0x43 | PIT control | write-only；read 回 0 |
| 0x60 | 8042 keyboard data | 回 shadow `_kbd60Data` |
| 0x64 | 8042 status | 回 shadow `_kbd64Status` |
| 0x61 | speaker / port B | system control bit 的 shadow byte |
| 0x70 / 0x71 | CMOS index / data | CMOS array 初始化：byte 0x14=0x21（equipment）、0x15-16=640KB base mem |
| 0x80 | POST diagnostic | shadow byte（BIOS 在這裡寫 POST progress code） |
| 0xA0 | NMI mask | shadow byte |

未知 port：read 回 0xFF（open-bus 慣例）、write 忽略。

跨 project 接線：`X86JsonCpu`（在 `AprX86.Cli`）暴露 static
`PortRead8Handler` / `PortRead16Handler` / `PortWrite8Handler` /
`PortWrite16Handler` delegate slot；`PcSystemRunner.Start()`（在
`AprPc.Cli`）構建 `PcPortBus` 並 install 4 個 delegate。讓 cross-project
reference 維持單向（AprPc → AprX86、不會反向）。

### 4. FPU escape stub (0xD8-0xDF)

Real BIOS POST FPU 偵測序列：
```
DB E3        FNINIT             ; init FPU to known state
BE 00 02     MOV  SI, 0x0200    ; scratch buffer in BIOS data area
C6 44 01 00  MOV  BYTE [SI+1], 0
D9 3C        FSTCW [SI]         ; store FPU control word
8A 64 01     MOV  AH, [SI+1]    ; read back high byte
80 FC 03     CMP  AH, 03        ; expect 0x03 = "FPU init state high byte"
```

沒 FPU 時，FSTCW 什麼都不寫 → `[SI+1]` 留 0 → CMP 失敗 → BIOS 走
「no FPU」branch、清 equipment-flag FPU bit。對 8086 沒 8087 的正確 semantics。

實作：
- `src/AprCpu.Core/Runtime/X86_16InstructionLengths.cs` 加
  `(has_modrm: true, immediate: 0)` for 0xD8-0xDF，decoder 算對 instruction
  長度（FNINIT = 2 byte、FSTCW [SI] = 2 byte with mod=00 rm=110 + no disp 等）。
- `spec/cpu/x86-16/i8086/groups/misc.json` 加 `FpuEscape` entry：
  `{ "mask": "0xF8", "match": "0xD8", "has_modrm": true,
     "instructions": [{ "steps": [{ "op": "x86_fpu_noop" }] }] }`。
- `src/AprCpu.Core/IR/X86_16Emitters.cs` 加 `X86FpuNoopEmitter`、
  register 到 emitter table — `Emit()` body 空（吃 decoded operand、
  什麼都不做）。

## 驗證

### FreeDOS regression — INTACT

`apr-pc --floppy-a=BIOS/freedos-1.3-floppy.img --headless --bios-mode=hle`
跑完 2839 個 HLE INT call（INT 10h video / 13h disk / 16h kbd 跨
kernel.sys、COMMAND.COM、AUTOEXEC.BAT）。FreeDOS 到鍵盤等待 block 點
跟 baseline 28.8e run 完全一樣。timeout 退出前 screenshot 捕捉（現在
`HeadlessRunner` 支援 — 之前 timeout 不 snapshot 就回）：
`result/pc/28.io-freedos-regress.png`。

### Real BIOS POST — 顯著進展

`apr-pc --bios=BIOS/firmware/pcxtbios.bin --headless --trace-io` 現在在
POST 期間 exercise ~100 個不同的 I/O port hit：

```
[IO] OUT port=0x0A0 ← 0x00     ; mask NMI
[IO] OUT port=0x3D8 ← 0x00     ; CGA mode control
[IO] OUT port=0x3B8 ← 0x01     ; MDA mode control
[IO] OUT port=0x041 ← 0x12     ; PIT channel 1 (DRAM refresh)
[IO] OUT port=0x081-0x083      ; DMA page register
[IO] OUT port=0x00B            ; DMA mode (4 channel)
[IO] OUT port=0x040            ; PIT channel 0 (timer tick)
[IO] OUT port=0x020 ← 0x13     ; PIC ICW1
[IO] OUT port=0x021 ← 0x08/09/FF ; PIC ICW2/3/4 + IMR
[IO] OUT port=0x3B4/0x3B5      ; MDA CRTC programming
[IO] OUT port=0x3D4/0x3D5      ; CGA CRTC programming
... (still many more)
```

POST 推進到 `F000:E706` 才在 joystick port (0x201) read loop 上 stall。
stall 點周圍 byte match 上面 FPU 偵測序列 — POST 過了（正確的 no-FPU
semantics）、現在在 delay loop 讀 port 0x201 等 timing-dependent 值。
完整 POST 完成需要：
- joystick port 0x201 回時變值給 delay calibration、或
- BIOS 對缺周邊偵測有 skip-on-fail logic。

**延後到未來工作**。Phase 28 的目標是「FreeDOS via HLE boot」、達成了；
real BIOS POST 是 stretch target、這個 sub-phase 讓它顯著更可達、但沒完全收。

## 這對 framework 意味著什麼

JSON-driven CPU framework 現在支援 **port I/O as a first-class abstraction**、
跟 memory I/O 對等 — 兩者透過完全一樣的 extern pattern
（`memory_read_8` / `port_read_8`）。PC port bus 是這些 extern 上的薄
adapter。加新的 x86 platform（例如 NEC PC-9801、Tandy 1000）只需要
新的 port bus 實作、不需要改 CPU spec 或 emitter pipeline。

FPU stub 也是值得注意的 — 是 spec 第一次承認 **非 CPU coprocessor**。
下一 phase（29）把它形式化為獨立 `spec/coprocessors/x87/i8087.json`、
透過 machine-level `"extensions"` mix in（按 Gemini design consult、
看 `MD/design/29-x87-fpu-plan.md`）。

## File changelog

```
A  MD/design/29-x87-fpu-plan.md
A  MD/performance/202605152230-pc-emulator-phase-28-io.md
A  src/AprPc.Cli/Hardware/PcPortBus.cs
M  spec/cpu/x86-16/i8086/groups/misc.json     (+FpuEscape entry)
M  src/AprCpu.Core/IR/MemoryEmitters.cs        (+4 port extern)
M  src/AprCpu.Core/IR/X86_16Emitters.cs        (重寫 4 個 IN/OUT emitter + X86FpuNoopEmitter)
M  src/AprCpu.Core/Runtime/X86_16InstructionLengths.cs  (+0xD8-0xDF entry)
M  src/AprPc.Cli/HeadlessRunner.cs             (headless timeout 截 screenshot)
M  src/AprPc.Cli/PcSystemRunner.cs             (PcPortBus 構建 + delegate install)
M  src/AprX86.Cli/Cpu/X86JsonCpu.cs            (+4 port shim + delegate slot)
```

## Commit

`feat(N28.IO): port I/O dispatch + PcPortBus + FPU detection stub`

## 交叉參考

- Phase 28 plan: `MD/design/28-intel-pc-emulator-plan.md`
- Phase 28 主收尾筆記: `MD/performance/202605152200-pc-emulator-freedos-boot.md`
- Phase 29 (x87 FPU) plan: `MD/design/29-x87-fpu-plan.md`
- Gemini consult log:
  - `tools/knowledgebase/message/20260515_223616.txt` (FPU 設計基礎)
  - `tools/knowledgebase/message/20260515_224332.txt` (separate vs integrated FPU spec)
