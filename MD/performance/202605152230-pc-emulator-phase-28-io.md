# Phase 28.IO — port I/O dispatch + FPU detection stub

Closure note for the I/O-machinery sub-phase added to Phase 28 on
2026-05-15 (same-day follow-up to the FreeDOS-boot closure). User
trigger: *"補上欠缺且阻礙 booting 的 IO 功能"* — replace the no-op
IN/OUT stubs that prevented real PC/XT BIOS POST from making any
progress.

## Scope

Real PC BIOS POST programs DMA / PIC / PIT / 8042 / video CRTC / CMOS
registers via dozens of `IN AL, DX` / `OUT DX, AL` sequences before
ever touching the framebuffer. Our existing emitters (`X86InImm8Emitter`,
`X86OutImm8Emitter`, `X86InDxEmitter`, `X86OutDxEmitter`) compiled
those into LLVM IR that discarded the port number and returned 0
(reads) or did nothing (writes). FreeDOS via HLE didn't care because
the HLE INT handlers bypassed real hardware programming, but real
BIOS code does care — the very first DMA refresh setup OUT to port
0x0A0 failed silently and the BIOS rolled into infinite-loop garbage
from there.

This phase adds **functional port routing through a single dispatch
table** that real DOS / BIOS code can talk to.

## Delivered

### 1. JIT pipeline — port externs

`src/AprCpu.Core/IR/MemoryEmitters.cs` gained four new externs that
mirror the existing `MemoryRead8` / `MemoryWrite8` / `MemoryRead16` /
`MemoryWrite16` pattern:

- `extern PortRead8(port: i16) → i8`
- `extern PortRead16(port: i16) → i16`
- `extern PortWrite8(port: i16, value: i8) → void`
- `extern PortWrite16(port: i16, value: i16) → void`

Plus helpers `CallPortRead8(ctx, portI16, label)`, `CallPortRead16`,
`CallPortWrite8`, `CallPortWrite16` that emitters use to lower
`IN`/`OUT` micro-ops.

### 2. Rewritten x86 emitters

`src/AprCpu.Core/IR/X86_16Emitters.cs`:
- `X86InImm8Emitter` — `IN AL, imm8` (E4 ib) → `AL = PortRead8(imm8)`
- `X86OutImm8Emitter` — `OUT imm8, AL` (E6 ib) → `PortWrite8(imm8, AL)`
- `X86InDxEmitter` — `IN AL/AX, DX` (EC / ED) → routes to PortRead8 or PortRead16
- `X86OutDxEmitter` — `OUT DX, AL/AX` (EE / EF) → routes to PortWrite8 or PortWrite16

Previously these were no-op stubs that always returned 0 (reads) or
did nothing (writes).

### 3. PcPortBus dispatch

New `src/AprPc.Cli/Hardware/PcPortBus.cs` — singleton port dispatch
hooked into the LLVM externs via `[UnmanagedCallersOnly]` shims on
`AprX86.Cli.Cpu.X86JsonCpu`:

| Port range | Device | Behavior |
|---|---|---|
| 0x20 / 0x21 | PIC 8259A | `_pic.GetImr()` on read 0x21; ISR/IRR=0 on read 0x20 |
| 0x40 / 0x41 / 0x42 | PIT 8253 counters | reads return 0 (good enough for refresh logic) |
| 0x43 | PIT control | write-only; reads return 0 |
| 0x60 | 8042 keyboard data | returns shadow `_kbd60Data` |
| 0x64 | 8042 status | returns shadow `_kbd64Status` |
| 0x61 | speaker / port B | shadow byte for system control bits |
| 0x70 / 0x71 | CMOS index / data | CMOS array initialized: byte 0x14=0x21 (equipment), 0x15-16=640KB base mem |
| 0x80 | POST diagnostic | shadow byte (BIOS writes its POST progress code here) |
| 0xA0 | NMI mask | shadow byte |

Unknown ports: 0xFF on read (open-bus convention), ignored on write.

Cross-project wiring: `X86JsonCpu` (in `AprX86.Cli`) exposes static
`PortRead8Handler` / `PortRead16Handler` / `PortWrite8Handler` /
`PortWrite16Handler` delegate slots; `PcSystemRunner.Start()` (in
`AprPc.Cli`) constructs the `PcPortBus` and installs the four
delegates. This keeps the cross-project reference one-directional
(AprPc → AprX86, never the reverse).

### 4. FPU escape stub (0xD8-0xDF)

Real BIOS POST FPU detection sequence:
```
DB E3        FNINIT             ; init FPU to known state
BE 00 02     MOV  SI, 0x0200    ; scratch buffer in BIOS data area
C6 44 01 00  MOV  BYTE [SI+1], 0
D9 3C        FSTCW [SI]         ; store FPU control word
8A 64 01     MOV  AH, [SI+1]    ; read back high byte
80 FC 03     CMP  AH, 03        ; expect 0x03 = "FPU init state high byte"
```

If no FPU, FSTCW writes nothing → `[SI+1]` stays 0 → CMP fails → BIOS
takes "no FPU" branch and clears the equipment-flag FPU bit. Correct
semantics for an 8086 without 8087.

Implementation:
- `src/AprCpu.Core/Runtime/X86_16InstructionLengths.cs` adds
  `(has_modrm: true, immediate: 0)` for 0xD8-0xDF so the decoder
  computes correct instruction lengths (FNINIT = 2 bytes, FSTCW [SI]
  = 2 bytes with mod=00 rm=110 + no disp, etc.).
- `spec/cpu/x86-16/i8086/groups/misc.json` adds `FpuEscape` entry:
  `{ "mask": "0xF8", "match": "0xD8", "has_modrm": true,
     "instructions": [{ "steps": [{ "op": "x86_fpu_noop" }] }] }`.
- `src/AprCpu.Core/IR/X86_16Emitters.cs` adds `X86FpuNoopEmitter`
  registered in the emitter table — empty `Emit()` body (consume the
  decoded operands, do nothing).

## Verification

### FreeDOS regression — INTACT

`apr-pc --floppy-a=BIOS/freedos-1.3-floppy.img --headless --bios-mode=hle`
runs through 2839 HLE INT calls (INT 10h video / 13h disk / 16h kbd
across kernel.sys, COMMAND.COM, AUTOEXEC.BAT). FreeDOS reaches the
keyboard-wait blocking point exactly like the baseline 28.8e run.
Screenshot capture before timeout exit (now supported in
`HeadlessRunner` — previously timeout returned without snapshotting):
`result/pc/28.io-freedos-regress.png`.

### Real BIOS POST — significant advance

`apr-pc --bios=BIOS/firmware/pcxtbios.bin --headless --trace-io` now
exercises ~100 distinct I/O port hits during POST:

```
[IO] OUT port=0x0A0 ← 0x00     ; mask NMI
[IO] OUT port=0x3D8 ← 0x00     ; CGA mode control
[IO] OUT port=0x3B8 ← 0x01     ; MDA mode control
[IO] OUT port=0x041 ← 0x12     ; PIT channel 1 (DRAM refresh)
[IO] OUT port=0x081-0x083      ; DMA page registers
[IO] OUT port=0x00B            ; DMA mode (4 channels)
[IO] OUT port=0x040            ; PIT channel 0 (timer tick)
[IO] OUT port=0x020 ← 0x13     ; PIC ICW1
[IO] OUT port=0x021 ← 0x08/09/FF ; PIC ICW2/3/4 + IMR
[IO] OUT port=0x3B4/0x3B5      ; MDA CRTC programming
[IO] OUT port=0x3D4/0x3D5      ; CGA CRTC programming
... (many more)
```

POST advances all the way to `F000:E706` before stalling on a
joystick port (0x201) read loop. Bytes around the stall point match
the FPU detection sequence above — POST got past it (correct no-FPU
semantics) and is now in a delay loop reading port 0x201 expecting a
timing-dependent value. Full POST completion requires:
- Joystick port 0x201 to return time-varying values for delay
  calibration, OR
- Skip-on-fail logic in the BIOS for missing peripheral detection.

**Deferred to future work.** Phase 28's goal was "FreeDOS boots via
HLE" which is met; the real BIOS POST is a stretch target that this
sub-phase made significantly more reachable but didn't fully close.

## What this means for the framework

The JSON-driven CPU framework now supports **port I/O as a first-class
abstraction**, peer to memory I/O — both go through identical extern
patterns (`memory_read_8` / `port_read_8`). The PC port bus is a thin
adapter on top of those externs. Adding new x86 platforms (e.g.,
NEC PC-9801, Tandy 1000) only needs a new port bus implementation,
not changes to the CPU spec or emitter pipeline.

The FPU stub is also notable as the first **non-CPU coprocessor**
acknowledged in the spec. The next phase (29) formalizes this into a
separate `spec/coprocessors/x87/i8087.json` mixed in via machine-level
`"extensions"` (per Gemini design consultation; see
`MD/design/29-x87-fpu-plan.md`).

## File changelog

```
A  MD/design/29-x87-fpu-plan.md
A  MD/performance/202605152230-pc-emulator-phase-28-io.md
A  src/AprPc.Cli/Hardware/PcPortBus.cs
M  spec/cpu/x86-16/i8086/groups/misc.json     (+FpuEscape entry)
M  src/AprCpu.Core/IR/MemoryEmitters.cs        (+4 port externs)
M  src/AprCpu.Core/IR/X86_16Emitters.cs        (rewrite 4 IN/OUT emitters + X86FpuNoopEmitter)
M  src/AprCpu.Core/Runtime/X86_16InstructionLengths.cs  (+0xD8-0xDF entries)
M  src/AprPc.Cli/HeadlessRunner.cs             (screenshot on headless timeout)
M  src/AprPc.Cli/PcSystemRunner.cs             (PcPortBus construction + delegate install)
M  src/AprX86.Cli/Cpu/X86JsonCpu.cs            (+4 port shims + delegate slots)
```

## Commit

`feat(N28.IO): port I/O dispatch + PcPortBus + FPU detection stub`

## Cross-references

- Phase 28 plan: `MD/design/28-intel-pc-emulator-plan.md`
- Phase 28 main closure note: `MD/performance/202605152200-pc-emulator-freedos-boot.md`
- Phase 29 (x87 FPU) plan: `MD/design/29-x87-fpu-plan.md`
- Gemini consultation logs:
  - `tools/knowledgebase/message/20260515_223616.txt` (FPU design basics)
  - `tools/knowledgebase/message/20260515_224332.txt` (separate-vs-integrated FPU spec)
