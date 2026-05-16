# Real-BIOS keyboard pipeline closure — GUI end-to-end FreeDOS interactive prompt

**Date**: 2026-05-16
**Status**: ✅ Functional. WinForms GUI launches, real `pcxtbios.bin` POST + FreeDOS 1.3 boot + language menu + LOGO + AUTOEXEC.BAT + installer prompt + interactive `A:\>` all work end-to-end. User types commands, DOS echoes, `ver` / `dir` run to completion (just slowly, see "What was NOT done").

**Detailed report** in the Chinese counterpart:
[`MD/performance/202605161900-realbios-keyboard-gui-end-to-end.md`](../../MD/performance/202605161900-realbios-keyboard-gui-end-to-end.md).
This file is a brief English mirror per CLAUDE.md convention.

## What this closure covers

Started from "Phase 30 real-BIOS path proven (headless screenshot of FreeCom)" and ended at "GUI mode interactive A:\> prompt with working keyboard". Took ~16 sub-bug investigations in one session.

## The 16 sub-bugs

| # | Sub-bug | Layer | Fix |
|---|---|---|---|
| 1 | MainForm `ToolStripMenuItem.ShortcutKeys = Keys.PrintScreen` throws InvalidEnumArgumentException at ctor | UI / WinForms | F12 instead of PrintScreen |
| 2 | `AllocaSlotProvider.RegisterStatus` no case for width=64 (Phase 29 FPU_ST0..ST7) → block-JIT crash on first compile | AprCpu.Core IR | Added `64 => LLVMTypeRef.Int64` (workaround; semantically still wrong for block-JIT FPU — see 30.9) |
| 3 | GUI framebuffer renderer hardcoded CGA (0xB8000) — real BIOS writes MDA (0xB0000) | UI | `X86CgaRenderer.PickFramebufferBase` auto-detect |
| 4 | GUI `Program.cs` didn't mount floppy between Start() and Resume() | Program | Cloned HeadlessRunner mount sequence |
| 5 | `HleBios.Install` pre-populated IVT 0-255 with HLE trap pointers even in real-BIOS mode → real BIOS POST left INT 9 pointing at no-op HLE | PcSystemRunner | Skip Install when `BiosPath != null` |
| 6 | Real-BIOS keyboard route: `PcKeyboard.Enqueue` wrote BDA directly (HLE convention), bypassing real BIOS INT 9 ISR's scancode→ASCII translation | PortBus + MainForm | New `PcPortBus.InjectScancode` writes to 8042 port 0x60 FIFO + IRQ 1 |
| 7 | 8042 status port 0x64 bit 0 (OBF) was 0 — some BIOS ISRs require OBF=1 before reading port 0x60 | PortBus | Set OBF on InjectScancode, clear on Dequeue60 |
| 8 | **XLAT (opcode 0xD7) missing from i8086 spec** — `pcxtbios.bin` INT 9 ISR uses `MOV BX, 0xE885; XLAT CS:` to look up scancode→ASCII; decoder returns null → IP rewind → infinite hot loop at F000:E9CB | spec/cpu/x86-16 + AprCpu.Core IR | Added `Xlat` JSON entry + `X86XlatEmitter` |
| 9 | Unknown-opcode hot loop was silent (#8 took an hour to find) | X86JsonCpu | `OnUnknownOpcode` callback fires once per unique (CS:IP, opcode); AprPc.Cli routes to kbd-trace |
| 10 | HLT didn't wake on IRQ — emulator-thread dispatch slept 50ms in Halted branch, IRQ check came after `continue` | PcSystemRunner + X86JsonCpu | Halted-branch checks `IF && PIC.DequeueNextVector`; if vector available, `ClearHalted` + DeliverInterrupt. Sleep dropped from 50ms to 2ms |
| 11 | **FDC motor 500ms BIOS stall on every INT 13h** — real BIOS waits for physical spin-up; our FDC is instant | Fdc8272.WriteDor | Force motor bits 4-7 to 1 always |
| 12 | **Edge-triggered IRQ 1 + per-AssertIrq stranded scancodes** — KeyDown + KeyPress both fire, 2 scancodes in FIFO but PIC pending bit only set once, second strands until next key | PortBus | Option C (Gemini): port 0x61 ack pulse drives IRQ line state; LOW→HIGH de-asserts, HIGH→LOW pops next FIFO entry + fresh edge. Also suppressed KeyPress in real-BIOS mode |
| 13 | Real-BIOS path needed full PC XT scancode set 1 map for KeyDown letters/digits | MainForm | Added full map (A-Z, 0-9, Esc, Tab, Space, Backspace, Enter, F1, F10, arrows, F11 dump) |
| 14 | GUI mode max-cycles auto-exit + screenshot dump for unattended automation | MainForm | When InstructionsExecuted ≥ MaxCycles, render framebuffer to ScreenshotPath, close form |
| 15 | Periodic CPU dump every 3s for hang diagnosis | MainForm | Logs CS:IP, GPRs, halted, delta_instr, same_csip to kbd-trace |
| 16 | F11 debug hotkey for on-demand CPU snapshot (replaced "Step One Frame" menu shortcut which was eating it) | MainForm | Direct write to kbd-trace, no scancode injection |

## Three Gemini consultations

All logged under `tools/knowledgebase/message/`:
- `20260516_115334.txt` — XLAT opcode missing diagnosis (sub-bug #8)
- `20260516_135110.txt` — keyboard 1-key lag + slowness root cause (sub-bugs #11, #12, #13)
- `20260516_140553.txt` — `dir` partial output / hang-after-volume-label diagnosis

## What was NOT done

- **block-JIT + Phase 29 FPU correctness** — biggest interactive-speed win available (~10-100×). Phase 30.9.
- **Phase 28.8x block-JIT INT trap loss** — bundled with 30.9 above.
- **`dir` / `ver` interactive speed** — currently 5-10 minutes per command on per-instruction backend. CPU diagnostics confirm progressing, not stuck. Phase 30.8 / 30.9.
- **W8/W16 ROR/RCL/RCR shift-rotate emitters** — still count=1 stubs, not hit by FreeDOS.
- **PIT > 18Hz override** — tested at 200Hz, caused FreeDOS time computation hangs. Reverted to default.

## Cross-references

- Phase 30 ROL fix closure: `MD_EN/design/30-fdc-dma-plan.md`
- Phase 30 + 28 design plans updated alongside this commit
- Gemini consultations: `tools/knowledgebase/message/2026051[5,6]_*.txt`
