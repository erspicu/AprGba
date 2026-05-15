# Phase 28 — Intel PC Emulator (DOS / FreeDOS boot target)

> **Status**: 📋 **PLANNED** (2026-05-11). Pre-work checklist green
> 2026-05-11; ready to start at Phase 28.0.
> Sub-project / extension phase. The goal is to take the existing
> AprX86 (i8086 / i80186 / i80286) and assemble a **minimum viable
> IBM PC compatible** capable of booting DOS / FreeDOS to a prompt
> and running .COM / .EXE programs. **This is application-layer
> framework demo, not CPU spec work** — once started it involves a
> lot of IO peripherals.
>
> **Predecessor**: Phase 27 (i80286 complete) —
> `MD/performance/202605110200-i80286-pmode-fault-model-complete.md`.
> Note: FreeDOS 1.x runs fine on 8086 real mode, **no protected mode
> needed**. Protected mode only becomes necessary for Win 3.x
> standard mode / DOS extenders — that's Phase 29+ territory.

---

## 0. Scope — clear lines

### In scope (all Phase 28 sub-phases combined)

| Component | Minimum requirement |
|---|---|
| **CPU** | i8086 (existing, real mode). Protected mode not enabled. |
| **Memory** | 1 MB linear PA_mem + zoned (BIOS / IVT / video / 640 KB conventional) |
| **BIOS** | INT 10h / 13h / 14h / 16h / 17h / 19h / 1Ah via **HLE** (C# trap handlers); no real BIOS image |
| **PIC** | Single 8259A (master) — IRQ 0/1 must work; IRQ 6/14 added later |
| **Timer (PIT)** | 8253 channel 0 → IRQ 0 (~18.2 Hz), channel 2 → speaker gate |
| **Keyboard** | 8042 + simplified — port 60h scancode + IRQ 1 |
| **CGA video** | Text mode 80×25, B800:0000 memory-mapped framebuffer, char/attr 16 color |
| **Floppy disk** | INT 13h HLE backed by `.img` file (no FDC emulation) |
| **Hard disk** | INT 13h HLE backed by `.img` file (no ATA emulation) |
| **Speaker** | port 61h bit 1 + PIT ch2 → track on/off + freq (audio output optional) |
| **Mouse** | INT 33h HLE (DOS driver level), no PS/2 emulation |

### Out of scope (explicit, to avoid scope creep)

- Real BIOS image LLE (copyright issues + complexity)
- CGA graphics modes / EGA / VGA / SVGA
- 80386+ protected mode / paging / V86 mode
- Adlib / Sound Blaster / MIDI
- Serial / parallel port real transfer
- DMA controller (8237)
- Full CMOS / RTC
- Multitasking / Windows 3.x / DOS extender (DPMI / VCPI)
- PCI / USB / any ISA expansion card

> Principle: **HLE first**. If an INT vector trap + C# function can
> handle it, don't emulate the port/controller. Once HLE-only works
> to boot FreeDOS, individual subsystems can be selectively swapped
> to LLE later if it adds learning value.

---

## 1. Why FreeDOS

FreeDOS 1.x is GPL, boots from a single 1.44 MB floppy image,
freely downloadable, fully ecosystem-compatible with DOS software.
MS-DOS / PC DOS are license-blocked. Win 3.x+ requires 386 + V86
(Phase 29+). FreeDOS is the clean first target.

---

## 2. Three-pillar validation (same pattern as NES/GB/GBA/x86)

| Pillar | Phase 28 form |
|---|---|
| Per-component unit test | Fixture .com binaries that exercise INT 1xh behavior |
| Synthetic boot ROM | Self-written 512-byte boot sector printing "Hello AprPc!" via INT 10h teletype, then HLT |
| Visual screenshot | FreeDOS `A:\>` prompt + `dir` / `type` / `cls` / `ver` outputs |

---

## 3. Architecture

```
   CLI args ──▶  apr-pc.exe (single launchable)
                     │
                     │  parse args → open UI window
                     ▼
   ┌─────────────────────────────────────────────────────────┐
   │   AprPc.Ui (WinForms main window — UI thread)           │
   │   ┌─ Menu (File / Emulation / Disk / View / Help)        │
   │   ├─ CGA framebuffer canvas (640×400 px scaled)          │
   │   └─ Status bar (CPU MIPS / disk LED / capslock)         │
   │           ▲                          │                   │
   │           │ framebuffer              │ key/mouse event   │
   │           │ (BitmapData @ 60Hz)      ▼                   │
   │   ┌─ PcSystemRunner (emulator thread)                    │
   │   │   ├─ AprX86Backend (CPU per --cpu CLI arg)           │
   │   │   ├─ PcMemoryBus  (1MB + MMIO regions)               │
   │   │   ├─ HleBios      (INT vectors)                      │
   │   │   ├─ Pic8259 / Pit8253 / Kbd8042                     │
   │   │   └─ FloppyImg / HddImg                              │
   └─────────────────────────────────────────────────────────┘
```

**Threading model**: UI thread (WinForms message pump + framebuffer
blt + input event capture); emulator thread (CPU dispatch + IO
controllers + disk image I/O). Communication via thread-safe
framebuffer lock + input event queue.

**Framework choice**: WinForms — built into .NET 10 via
`Microsoft.WindowsDesktop.App`, minimal LoC for single-window
emulator UI, mature keyboard hook + timer. WPF / Avalonia / MAUI
not in Phase 28 scope.

### Reuse from existing codebase

- New project `src/AprPc.Cli/` (keeps `src/AprX86.Cli/` as pure
  CPU harness for Tom Harte / .com runs)
- `AprX86.Cli.Cpu.X86JsonCpu` reused as the CPU component
- `AprX86.Cli.X86CgaRenderer` reused for framebuffer → Bitmap blt
- `spec/cpu/x86-16/i8086/cpu.json` unchanged
- New: `spec/machines/ibm-pc-xt.json` (memory map / IRQ wiring /
  port ranges)

### CLI interface

```
apr-pc [options]

# Disk inputs (one required)
  --floppy-a=PATH           A: floppy image (.img / .ima, 1.44 / 720 / 360 KB)
  --hdd=PATH                C: hard disk image (.img, FAT12/16 partition)

# System config (all have defaults)
  --cpu=i8086 | i8088 | i80186 | i80188 | i80286   [default: i8086]
  --bios=PATH               Real BIOS image (LLE mode); omit for HLE
  --memory=640k | 1m         conventional RAM size              [default: 640k]
  --backend=json | json-block | legacy              [default: json-block]

# UI config
  --window-scale=1 | 2 | 3                          [default: 2]
  --window-title="..."      main window caption                 [default: "AprPc"]
  --fullscreen              fullscreen

# Headless / CI mode
  --headless                no UI window
  --screenshot=PATH         output PNG (with --headless)
  --max-cycles=N            halt after N cycles
  --frames=N                halt after N frames
  --keys="text\r..."        keystroke script (for CI)

# Debug
  --trace-int               log every INT instruction (vector + AH)
  --trace-io                log every IN/OUT port + value
  --trace-irq               log every PIC IRQ delivery
  --verbose                 print full system config at start
```

### Window menu

```
File:       Open Floppy A... / Open HDD... / Recent / Exit
Emulation:  Reset (Ctrl+R) / Pause-Resume (F5) / Step Instr (F10) / Step Frame (F11)
Disk:       Eject Floppy A / Floppy Write-Protect / HDD Read-Only
View:       Window Scale (1×/2×/3×) / Show CPU MIPS / Show Disk LED /
            Take Screenshot... (PrintScreen)
Help:       Keyboard Shortcuts / About...
```

---

## 4. Sub-phase breakdown

Each sub-phase = 1 commit milestone. Internal micro-sprints within.

| Phase | Deliverable | Estimate |
|---|---|---|
| **28.0** | AprPc.Cli scaffolding + WinForms UI shell + emulator-thread plumbing. `apr-pc` opens an empty UI window; menus respond with "TODO". | 2 days |
| **28.1** | Memory map + IVT + reset vector + BIOS Data Area. `mov bx, [0x410]` reads correct equipment word. | 1 day |
| **28.2** | HLE BIOS framework + INT 10h (teletype / cursor / scroll / mode 3) + UI 60 Hz framebuffer blt. `28.2-hello.com` puts "Hi" on the screen. | 2 days |
| **28.3** | INT 16h HLE + 8042 keyboard + IRQ 1 + WinForms `KeyDown` → scancode queue. `28.3-echo.com` echoes typed characters. | 2 days |
| **28.4** | PIT 8253 + IRQ 0 timer tick + INT 1Ah. 1 second yields ~18 ticks. | 1-2 days |
| **28.5** | INT 13h floppy/HDD HLE + `.img` loader. Read/write sectors against image file. | 2 days |
| **28.6** | INT 19h bootstrap + self-written boot sector. Screenshot shows "AprPc bootstrap OK". **First visual milestone.** | 1 day |
| **28.7** | Pic8259 + full IRQ delivery model + `sync` micro-op for STI-class delay. | 1-2 days |
| **28.8** | FreeDOS 1.3 boot attempt. **Expected to split into many micro-sprints** debugging unfamiliar INT calls / port I/O / DOS internals. | **3-5 days** (high uncertainty) |
| **28.9** | Interactive `A:\>` + `dir` / `type` / `cls` / `ver` captured as 4 screenshots. | 2 days |
| **28.10** | INT 33h mouse HLE | 1 day (optional) |
| **28.11** | PC speaker PCM → WAV | 1-2 days (optional) |
| **28.12** | Closure docs + README PC section + plan doc closure | 1 day |

**Total**: ~3-4 weeks continuous work; with /loop interruptions ~1.5–2 months.

---

## 5. Screenshot deliverables

```
result/pc/
├── 28.2-hello.png              ← INT 10h teletype "Hi"
├── 28.3-echo.png               ← INT 16h read + echo
├── 28.6-bootstrap.png          ← self-written boot sector
├── 28.8-freedos-boot.png       ← first FreeDOS A:\> prompt (key milestone)
├── 28.9-dir.png                ← A:\> dir output
├── 28.9-type.png               ← A:\> type readme.txt
├── 28.9-cls.png                ← A:\> cls cleared screen
└── 28.9-ver.png                ← A:\> ver showing FreeDOS version
```

Sits alongside the existing `result/{gb,gba,nes,x86-16}/` screenshot
sets — visual evidence that "framework genuinely boots commercial OS".

---

## 6. Risks

| Risk | Severity | Mitigation |
|---|---|---|
| FreeDOS uses an INT call we haven't implemented | High | Trace mode logs unknown INTs with args; fill in one at a time |
| CPU side-effect bug only manifests during boot | Medium | Tom Harte SST covers 1.31M cases; new bugs always reproduced as unit test first |
| IRQ delivery timing wrong → keyboard buffer overflow | Medium | Use existing `sync` micro-op pattern (mirror LR35902 EI) |
| HLE/LLE boundary unclear, program hits raw port | Medium | Trace which INT was HLE-handled; if program bypasses INT, add port stubs |
| Disk image format quirks (FAT12 BPB) | Low | Use existing FreeDOS images, don't assemble ourselves |
| Scope creep (graphics mode / Adlib / 386) | **High** | Strict §0 out-of-scope list; new features open Phase 29+ |

---

## 7. Pre-work checklist

Before starting Phase 28.0:

- [x] **FreeDOS 1.3 floppy image** → `BIOS/freedos-1.3-floppy.img`
      (2026-05-11; from ibiblio.org `FD13-FloppyEdition.zip`,
      1,474,560 bytes, SHA256 `3F7834EA...`, OEM `FRDOS5.1`,
      boot magic `55 AA` confirmed)
- [x] **NASM 3.01** at `C:\Program Files\NASM\nasm.exe`
- [x] **Tom Harte SST regression baseline**: X86TomHarteTests
      subset 147/147 in 3m 55s; opcode 0x00 standalone 10000/10000
      in 1.73s
- [x] **Visual matrix baseline**: T2 18 PNGs SHA256 identical;
      i8086/i80186 variant matrix 6 demos identical

**All four green on 2026-05-11. Phase 28.0 unblocked.**

---

## 8. Gemini consultation pattern (per `MD_EN/process/02-ai-collaboration-workflow.md` Pattern B)

Phase 28 is more prone to spec-detail traps than any prior phase —
IBM PC peripherals each have 40 years of history, BIOS vendor
behaviors diverge from Intel docs. **Consult Gemini for these
question types before guessing:**

| Question type | Example |
|---|---|
| PC controller register details | "8259 ICW1/ICW2 ordering; ELCR support per board"; "8042 status bit 7 meaning"; "PIT mode 2 vs mode 3 on channel 0" |
| BIOS INT corner cases | "INT 13h AH=02h reading past track end"; "INT 10h AH=06h scroll 0 lines (clear vs no-op)"; "INT 16h AH=00h vs AH=10h" |
| DOS internals assumptions | "FreeDOS boot sector: INT 13h CHS or BIOS table first?"; "INT 25h/26h FAT12 sector numbering"; "COMMAND.COM resident size" |
| 8086 silicon ambiguities | (Confirmed: PUSH SP is pre-dec); "REP MOVSB CX behavior on IRQ" |
| Industry-comparison | "How does DOSBox / PCem / 86Box handle this case?"; "Minimal PC: HLE or LLE for this?" |

**Method**: English, one question at a time, with version + current
approach + why we think there's a problem.
`python tools/knowledgebase/gemini_query.py "<question>"` —
auto-logs to `tools/knowledgebase/message/`.

**Don't ask just to ask**: things resolvable by reading Intel 80286
PRM / Apr86 source / FreeDOS source don't need Gemini. Pattern A
(no Gemini) handles 90% of work; Pattern B is reserved for genuine
fork points.

---

## 9. Why this phase isn't spec-driven

Unlike Phase 24–27 (CPU spec inheritance), Phase 28's additions are
**machine-level**, not ISA-level. `MachineSpec` already supports the
memory-map declarative form (docs #19, #22) and will carry
`spec/machines/ibm-pc-xt.json`. But IO controller behavior
(8259 / 8253 / 8042 / disk emulation / HLE INT handlers) is host code,
won't be declarativized — that would require an entirely new
mini-emulator-spec language to be meaningful.

**Phase 28's value is not "another framework genericity proof"** —
that's done. Phase 28's value is the application milestone: pushing
the framework all the way to commercial OS boot.

> **Bottom line**: Phase 28 is application layer, not framework
> layer. After completion the framework is unchanged; we have one
> more *consumer*.

---

> The original Chinese version at `MD/design/28-intel-pc-emulator-plan.md`
> contains the same content with more detail on individual sub-phase
> deliverables, the full sprint status table, and section numbering
> aligned with the rest of MD/.
