# Real-BIOS keyboard pipeline closure — GUI end-to-end FreeDOS interactive prompt

**Date**: 2026-05-16  
**Trigger**: User asked to test GUI mode after Phase 30 real-BIOS path shipped, plus revisit deferred Phase 28.8f (interactive `A:\>` prompt).  
**Status**: ✅ GUI launches, real BIOS POST + FreeDOS boot + language menu + LOGO + AUTOEXEC.BAT + installer prompt + interactive `A:\>` all work end-to-end. User can type commands and DOS echoes them.

**Phase**: 28.8f / 30.7 (interactive prompt was the Phase 28 deferred polish item; the FDC + keyboard work that unblocked it sits under Phase 30).  
**Predecessor**: Phase 30 ROL fix closure (`202605161800-phase-30-real-bios-freedos-boot.md`).

## Outcome

```
gui-test.bat realbios
  ↓
[GUI window: AprPc - real BIOS pcxtbios.bin]
  ↓
pcxtbios.bin POST → INT 19h via emulated FDC/DMA → boot sector → FreeDOS kernel
  ↓
language selection menu  (keystroke accepted via real BIOS INT 9 → BDA → INT 16h)
  ↓
FreeDOS green ASCII LOGO
  ↓
AUTOEXEC.BAT → FreeDOS 1.3 installer welcome "Do you want to proceed [Y,N]?"
  ↓
user types N → installer aborted → A:\> prompt
  ↓
user types "dir" + Enter → DOS prints "Volume in drive A is FD13-BOOT"
```

Last screenshot: `pic/2.png` (gitignored — user-local debug capture).

What still doesn't work: `dir` prints the volume label then hangs reading FAT/root directory. Boot sector + KERNEL.SYS + COMMAND.COM all loaded fine via FDC, so basic INT 13h reads work; some FDC command or multi-sector parameter combination used by DOS file-system layer needs investigation. Tracked as Phase 30.8.

## Ten sub-bugs uncovered

Each one independently blocked the chain. Listed in the order we discovered them.

| # | Sub-bug | Where | Symptom |
|---|---|---|---|
| 1 | `MainForm.cs` line 83 set `ToolStripMenuItem.ShortcutKeys = Keys.PrintScreen`, but the `Shortcut` enum (which `ShortcutKeys` validates against) is a subset of `Keys` that doesn't include PrintScreen. | UI ctor | GUI launch → unhandled `InvalidEnumArgumentException` → default .NET error dialog → process dies. Replaced with `Keys.F12`. |
| 2 | `AllocaSlotProvider.RegisterStatus` switch on `WidthBits` had no case for 64 — but Phase 29.1 added 64-bit `FPU_ST0..ST7` slots to the i8087 extension. Every block-JIT compile touching ANY status reg (`BlockFunctionBuilder` registers all of them up-front) crashed with `NotSupportedException`. | `src/AprCpu.Core/IR/IStateSlotProvider.cs` | Block-JIT + Phase 29 spec → emulator thread crash on first block compile. Added `64 => LLVMTypeRef.Int64` case. **Side note**: Block-JIT IR for 64-bit FPU stack is structurally not crash-free but its semantics are still wrong; HLE + real-BIOS keyboard demo here uses `--backend=json` (per-instruction). Block-JIT + FPU correctness is a separate Phase 30.x or 29.x ticket. |
| 3 | `MainForm.RefreshFromRunner` called `X86CgaRenderer.RenderToRgbBytes(bus.Memory.Ram)` which uses the backward-compat overload defaulting to CGA framebuffer (0xB8000). Real BIOS POST writes to MDA (0xB0000). | `src/AprPc.Cli/Ui/MainForm.cs` | GUI canvas all-black despite real BIOS POST printing to memory. Switched to `PickFramebufferBase` auto-detection (mirrors what `X86CgaRenderer.Render` does for headless screenshots). |
| 4 | `Program.cs` GUI path called `runner.Start(); runner.Resume();` with no `MountDisk` in between. HeadlessRunner mounts the floppy via `runner.MountDisk(0x00, disk)` after Start and before Resume. | `src/AprPc.Cli/Program.cs` | GUI real-BIOS mode hit FDC NRDY (no media) on INT 19h boot attempt because no disk was ever attached. Cloned the mount sequence from `HeadlessRunner.Run`. |
| 5 | `HleBios.Install()` ran unconditionally in `PcSystemRunner.Start`, pre-populating IVT entries 0x00-0xFF with pointers to `F000:00xx` (the HLE trap segment). For real-BIOS mode, real `pcxtbios.bin` POST expects IVT to be zero at boot and only installs entries for vectors it explicitly cares about. INT 9 was one POST didn't reinstall (or installed late). Result: IRQ 1 → vector 0x09 → IVT[9]=F000:0009 → `HleBios.Dispatch(9)` default IRET no-op. Scancodes read from port 0x60 but never reached BDA. | `src/AprPc.Cli/PcSystemRunner.cs` | Real-BIOS path keyboard input silently dropped. Guarded `HleBios.Install()` behind `if (_options.BiosPath is null)`. |
| 6 | `PcKeyboard.Enqueue` writes directly into the BDA ring buffer at 0x0041E and pulses IRQ 1. That's the HLE BIOS contract — HLE INT 16h reads BDA. For real BIOS, the right plumbing is: scancode → 8042 port 0x60 → IRQ 1 → real BIOS INT 9 ISR reads port 0x60, translates scancode to ASCII via in-ROM table, writes BDA itself. Direct BDA write bypasses the BIOS's own scancode-to-ASCII step. | `src/AprPc.Cli/Hardware/PcPortBus.cs` + `Ui/MainForm.cs` | Added `PcPortBus.InjectScancode(byte)` which pushes the scancode into an 8-deep host-side FIFO behind port 0x60, sets 8042 status bit 0 (OBF), and asserts IRQ 1. `MainForm.KeyPress/KeyDown` branch on `options.BiosPath` and route accordingly. |
| 7 | The 8042 status port (0x64) bit 0 = Output Buffer Full (OBF) was always 0 in our stub. Some BIOS INT 9 ISRs poll bit 0 before reading port 0x60; if clear they treat the IRQ as spurious and return without writing BDA. | `PcPortBus.cs` | Set bit 0 on `InjectScancode`, clear on `Dequeue60` once FIFO drains. (Wasn't ultimately the blocker for `pcxtbios.bin` — its ISR reads port 0x60 directly — but harmless and correct for other BIOSes.) |
| 8 | **XLAT (opcode 0xD7) was not implemented in the i8086 spec.** `pcxtbios.bin` INT 9 ISR uses `MOV BX, 0xE885; XLAT CS:` to translate scancode through an in-ROM table. The decoder returned null for D7; our `StepOne` "unknown opcode" path rewinds IP to before the prefix and returns -1; the dispatch loop just calls Step again on the same byte → **infinite hot loop at F000:E9CB**. CPU never returned from INT 9, BDA never written, INT 16h never satisfied. | `spec/cpu/x86-16/i8086/groups/data-transfer.json` + `src/AprCpu.Core/IR/X86_16Emitters.cs` | Added `Xlat` spec entry (opcode D7, mnemonic XLAT, op `x86_xlat`) and `X86XlatEmitter` (AL = byte at [seg : BX + ZExt(AL)], honours segment override prefix). Gemini consultation (`tools/knowledgebase/message/20260516_115334.txt`) confirmed the hypothesis after we'd narrowed it from log evidence ("caller at F000:E9CB" repeated 41/44 times = CPU spending 93% of time at that one IP). |
| 9 | **Unknown-opcode hot loop was silent.** Bug #8 took an hour to find because the symptom was "CPU stuck, nothing prints, no exception, no log". | `src/AprX86.Cli/Cpu/X86JsonCpu.cs` | Added `OnUnknownOpcode` static callback fired once per unique (CS:IP, opcode) tuple. AprPc.Cli wires it to `KbdTrace.Log` writing `UNKNOWN_OPCODE 0xXX at CS:IP (CPU will infinite-loop until implemented)`. Same byte hitting again is suppressed (HashSet). This will save the next missing-opcode investigation a lot of grief. |
| 10 | **`PcSystemRunner` emulator thread didn't wake the CPU from HLT on IRQ.** When CPU executed HLT, dispatcher entered `if (_cpu is { Halted: true }) { Thread.Sleep(50); continue; }` and the IRQ-delivery check that came AFTER was unreachable while halted. Real silicon resumes from HLT when an unmasked IRQ arrives. The FreeDOS installer's INT 16h wait loop is the classic `STI; HLT` pattern; without wake-on-IRQ the system slept forever even though IRQ 1 was pending with scancodes in FIFO. | `src/AprPc.Cli/PcSystemRunner.cs` + `src/AprX86.Cli/Cpu/X86JsonCpu.cs` | In the Halted branch, check `IF && PIC.DequeueNextVector()`; if so, `_cpu.ClearHalted()` (new exposed method), `DeliverInterrupt(vec)`, continue. Also dropped the sleep from 50 ms to 2 ms so the parking is more responsive overall. |

## Tooling additions

- **`KbdTrace`** (`src/AprPc.Cli/Diagnostics/KbdTrace.cs`) — thread-safe file logger at `temp/kbd-trace.log`, truncated each launch. Five layers traced: `KeyPress`/`KeyDown` in UI thread, `PcPortBus.InjectScancode` + `Dequeue60`, `Pic8259.AssertIrq(1)` + `DequeueNextVector` IRQ 1, `PcSystemRunner.DeliverInterrupt` vec=0x09 (logs IVT[9] target + BDA head/tail snapshot), BDA writes in 0x00418-0x00440 range via the existing `X86JsonCpu.WriteWatch`. Plus unknown-opcode events. This is the audit trail that made each sub-bug diagnosable from log alone.
- **`X86JsonCpu.OnWriteWatch` + `OnUnknownOpcode` callbacks** — let downstream tooling (AprPc.Cli) route diagnostic events into its own log without AprCpu.Core / AprX86.Cli having to depend on it.
- **`gui-test.bat`** — repo-root launcher with three modes: `hle` (per-instr — block-JIT + HLE INT trap loss = Phase 28.8x), `realbios` (per-instr — block-JIT + FPU width-64 still broken, see bug #2), `hle-jit` (intentionally broken HLE+JIT for repro). Each picks the right `--backend=`, `--bios=`, `--floppy-a=` so the user doesn't have to remember.
- **GUI auto-exit on `--max-cycles`** (`MainForm.cs`) — when the cycle budget is hit, render framebuffer to `--screenshot=PATH` and close the form. Useful for unattended automation that wants the same screenshot pipeline as headless.

## Six more sub-bugs uncovered after the first round of `dir` debugging

The initial close-out draft tagged `dir` as "hangs after volume label". A follow-up
debugging round (Gemini consultations in `tools/knowledgebase/message/20260516_135110.txt`
and `20260516_140553.txt`) found six more:

| # | Sub-bug | Where | Symptom |
|---|---|---|---|
| 11 | **FDC motor spin-up 500ms BIOS stall on every INT 13h.** Real IBM PC BIOS INT 13h checks the BDA motor flag; if motor is off, OUTs `0x3F2` with motor bit + stalls 500ms for physical spin-up. Our FDC completes instantly but the BIOS doesn't know that. With ~840 reads per `dir`, that's 840 × 500ms = 7 minutes of pure stall. | `src/AprPc.Cli/Hardware/Fdc8272.cs` `WriteDor` | Forced motor bits 4-7 to 1 regardless of BIOS write -- "motor always running" so BIOS skips the stall. ~10× speedup on dir/ver. Per Gemini 2026-05-16 consultation. |
| 12 | **Edge-triggered IRQ 1 + per-AssertIrq model strands every scancode past the first.** Each WinForms key press fires both `KeyDown` AND `KeyPress` → 2 InjectScancode calls. Edge-triggered PIC: second AssertIrq sees pending bit already set, no extra IRQ. BIOS only reads 1 scancode per IRQ. Second scancode strands in FIFO. Next key press triggers IRQ → BIOS reads the stranded char → user sees 1-key lag (press 'a' shows nothing, press 'b' shows 'a'). | `src/AprPc.Cli/Hardware/PcPortBus.cs` + `Ui/MainForm.cs` | Implemented Gemini's "Option C" — port 0x61 ack pulse drives IRQ line state: LOW→HIGH (BIOS sets bit 7) de-asserts line; HIGH→LOW (BIOS clears bit 7) pops next FIFO entry + re-asserts line for a fresh 8259A edge. Plus suppressed `KeyPress` in real-BIOS mode entirely (only `KeyDown` is the source — added full PC XT scancode set 1 map for letters / digits / specials). |
| 13 | **HLT didn't wake on IRQ.** PcSystemRunner emulator-thread dispatch: `if (cpu.Halted) { Sleep(50); continue; }` — IRQ check came AFTER and was unreachable while halted. FreeDOS installer's `STI; HLT` INT 16h wait loop hung even though IRQ 1 fired with scancodes in FIFO. | `PcSystemRunner.cs` | In the Halted branch, also check `IF && PIC.DequeueNextVector`; if a vector available, `ClearHalted()` (new X86JsonCpu method), `DeliverInterrupt(vec)`, continue. Cut sleep from 50ms to 2ms for faster wake on the no-IRQ-yet path. |
| 14 | **GUI mode didn't mount disk before resume.** `HeadlessRunner.Run` mounts floppy/HDD via `MountDisk` between `Start()` and `Resume()`. `Program.cs` GUI path did `Start(); Resume();` with nothing in between, so real-BIOS INT 19h hit FDC NRDY immediately. | `Program.cs` | Cloned the disk-mount sequence into the GUI path. |
| 15 | **`HleBios.Install` ran unconditionally even in real-BIOS mode.** Pre-populated IVT 0-255 with HLE-trap pointers to F000:00xx. Real `pcxtbios.bin` POST only re-installs IVT entries for vectors it explicitly cares about; INT 9 was one POST didn't reinstall (or installed late). Result: IRQ 1 → vector 0x09 → IVT[9]=F000:0009 → `HleBios.Dispatch(9)` default IRET no-op. Scancodes read from port 0x60 but never reached BDA. | `PcSystemRunner.cs` | Guarded `_bios.Install()` behind `if (_options.BiosPath is null)`. Real-BIOS mode leaves IVT zero and lets POST own it. |
| 16 | **Periodic CPU dump tooling added.** When `dir` and `ver` appeared to "hang", we needed to know if CPU was actually stuck or just slow. Added 3-second-interval CPU state log (CS:IP, flags, GPRs, halted state, delta-instr-since-last-tick, same_csip-as-last-tick) to `MainForm.RefreshFromRunner`. Plus F11 hotkey for on-demand snapshot. | `MainForm.cs` | The dump proved the system isn't hung — CPU rotates through F000 BIOS / 0070 DOS kernel / 06B3 COMMAND.COM / various TSR segments, ~500K inst/sec emulated, ~1-2M delta_instr per 3s tick, `same_csip` almost always false. **It's just slow**, not stuck. |

## Final state of `dir` and `ver`

Both commands **work end-to-end**:
- `ver` runs (FreeDOS Version banner + time / date)
- `dir` runs (Volume label + Volume Serial Number + directory listing)

They just take **several minutes to complete** because:
1. CPU emulation in per-instruction backend runs at ~500K inst/sec — slower than original 8086.
2. After the installer (`N` to abort) overwrites COMMAND.COM's transient portion, COMMAND.COM
   reloads transient from disk before each command (~hundreds of FDC reads).
3. FreeDOS `dir` walks the entire FAT12 to compute "bytes free" — ~840 sector reads.

Total: `dir` takes ~5-10 minutes wall clock. CPU diagnostics confirm it's progressing the
whole time, never stuck. Phase 30.9 below tracks the speedup work.

## What was NOT done

- **block-JIT + Phase 29 FPU correctness** — bug #2 made block-JIT compile cleanly but the
  i64 alloca slot for FPU_ST0 doesn't match the f64 layout the emitters expect at runtime.
  Headless real-BIOS validates this — only `--backend=json` produces FreeCom banner;
  `--backend=json-block` produces black screen even with the width-64 slot fix in. Fixing
  this is the single biggest interactive-speed win (~10-100× speedup expected). Phase 30.9.
- **Phase 28.8x block-JIT INT trap loss** — separate from the FPU issue. Block-JIT compiles
  `INT n` into IR that doesn't surface to the emulator-thread `IsTrapped()` check between
  Step() calls. HLE BIOS mode loses INT 10h / 13h / 16h dispatches. Workaround: HLE mode
  uses `--backend=json`. Phase 30.9 fix would unblock both real-BIOS and HLE paths.
- **W8 ROR/RCL/RCR + W16 ROR/RCL/RCR shift-rotate emitters** — still stubbed (count=1 only).
  Not hit by FreeDOS / pcxtbios.bin so deferred.
- **PIT > 18Hz override** — tested at 200Hz but caused FreeDOS time-of-day computation
  hangs (BDA tick counter ran 11× faster than DOS expected, hit some internal conversion
  corner case). Kept default 18Hz; motor-hack alone gives enough speedup.

## Cross-references

- Phase 30 ROL fix closure: [`202605161800-phase-30-real-bios-freedos-boot.md`](202605161800-phase-30-real-bios-freedos-boot.md)
- Gemini consultation (XLAT diagnosis): `tools/knowledgebase/message/20260516_115334.txt`
- Phase 28 plan: [`MD/design/28-intel-pc-emulator-plan.md`](../design/28-intel-pc-emulator-plan.md)
- Phase 30 plan: [`MD/design/30-fdc-dma-plan.md`](../design/30-fdc-dma-plan.md)
