# 8086 port plan — minimal-environment CPU validation + screenshot evidence

> **Status**: **Phase 24 closed** (2026-05-10) — 24.0–24.5 (13 commits) +
> 24.6.1–24.6.9 (29 commits) all shipped. Three-backend parity:
> legacy (1.31M Tom Harte SST) / json-llvm (per-instr) / json-block-llvm
> (block-JIT). T1 836/836 green. 6 paper-quality screenshots × 2 backends
> (legacy + json-block) = 12 PNGs, SHA256 pixel-identical pairs.
> "Framework genericity for 4 CPUs" claim achieved end-to-end:
> ARM7TDMI / LR35902 / Ricoh 2A03 / Intel 8086 all run through the
> same SpecCompiler → LLVM IR → ORC LLJIT pipeline. Follow-on:
> 24.7 (80186 inheritance) ✅ Phase 25; 24.8 (80286 + protected mode)
> ✅ Phase 27a + 27b.
>
> **Trigger**: 4th CPU candidate = Intel 8086 (with the previously-written
> Apr86 emulator as partial reference oracle). Core problem: 8086 is
> CISC, segmented memory, ModR/M, needs PC-peripheral environment to
> run most software — how to validate CPU correctness at minimum cost
> and produce **convincing screenshot evidence**?
>
> **Core idea**: screenshots are the strongest evidence of the
> framework's generality claim (mirror of existing NES / GBA / GB
> CPU screenshot strategy). **No screenshot = no paper-quality demo**.
>
> **Important update (2026-05-10)**: v1 phase plan missed the
> "JSON-driven port" sub-phase. State at end of 24.5 was **legacy
> backend complete + screenshots complete**, but 8086 hadn't gone
> through the framework's SpecCompiler + LLVM JIT pipeline yet —
> that's what 24.6 brought online, the actual cornerstone of
> framework genericity.
>
> **Target readers**: (a) anyone actually starting an 8086 port;
> (b) future maintainers / academic peers who need to understand
> where the visual evidence for "framework supports 4 CPUs" comes from.

---

## 1. Three pillars of validation

Same "validation pyramid" used for NES / GBA / GB, applied to 8086:

| Pillar | NES analog | 8086 form | Purpose |
|---|---|---|---|
| **Per-opcode oracle test** | nestest + blargg three backends | **Tom Harte SingleStepTests 8088_v2** | Pure CPU correctness — 256 opcodes × 10k tests/opcode = millions of cases |
| **Synthetic test ROM** | nes test ROMs (.nes) | **Hand-crafted .com (raw 8086 binary)** | End-to-end execution validation, no BIOS / peripherals needed |
| **Visual screenshot** | NesPpu → PNG (`--screenshot=`) | **CGA text mode 80×25 → PNG (`--screenshot=`)** | Visual proof the framework actually runs 8086 |

---

## 2. Apr86 reference state (OldProject/Apr86)

> Cloned at `OldProject/Apr86/` (pre-cleanup commit).

| Component | Apr86 state | Implication for AprX86 port |
|---|---|---|
| **CPU core** | `Apr8086Core` 3139-line single file, 261 opcode cases | Behavior reference; **not an oracle** (has bugs — see §2.1) |
| **1MB PA_mem** | byte[0x100000] linear, segmented (seg<<4)+offset | **Reuse the structure directly** |
| **CGA text 80×25** | Already implemented! 256 PNG glyphs (8×14), 16-color palette, Parallel.For rendering | **Font + palette taken as-is** |
| **System BIOS load** | 0xFFFF0 area; VGA BIOS at 0xC0000 | AprX86 does not use this path (no real BIOS needed) |
| **IO port** | `io_step.dat` replays a pre-recorded real PC trace (hack) | **Not adopted** — not real validation, just a bypass |
| **Interrupt** | pushes FLAGS/CS/IP + reads vector, but author marks "unfinish" | Reimplemented from scratch (CPU's INT instruction, no peripheral dependency) |

**Bottom line**: borrow Apr86's **1MB memory layout + CGA font + 16-color
palette**; **reject CPU core / IO replay**, redo through the framework.

### 2.1 Known Apr86 CPU bugs (fix during port)

Source-scan turned up these bugs — Tom Harte SST catches them all,
so the port writes them correctly the first time:

| # | Location | Bug | Fix |
|---|---|---|---|
| 1 | `CPU.cs:1247-1255` AAM (0xD4) | `Reg_IP++; ... Reg_A.L / 10` — skips imm8 base byte, hardcoded 10 | `byte b = Mem_CS_r8(Reg_IP++); ... Reg_A.L / b` |
| 2 | `CPU.cs:1257-1268` AAD (0xD5) | Same — imm8 base ignored | Same fix |
| 3 | `CPU.cs:1265` AAD flag_S | Uses `& 0x8000` but result is 8-bit AL; always false | `& 0x80` |
| 4 | `CPU.cs:1644` IMUL byte | `(short)modreg_ReadTool()` zero-extends; should sign-extend from byte | `(short)(sbyte)modreg_ReadTool()` |
| 5 | `CPU.cs:1623, 1668` MUL/IMUL CF/OF | Uses `Reg_D.X > 0`; IMUL should be "upper half ≠ sign extension of lower half" | For IMUL: `cf = of = (DX != (AX bit15 ? 0xFFFF : 0))` |
| 6 | `Interrupt.cs:25` | Author note "interrupt unfinish !"; flag_T/I handling suspect | Redone from 8086 datasheet |
| 7 | `IO.cs` (all 4 functions) | IO replay from `io_step.dat`, not real emulation | AprX86 uses magic ports (OUT 0xE9 / 0xF4) + ignores others |
| 8 | `MEM.cs:53` | `0x410 hack patch return 0x41` — BIOS Equipment Word brute bypass | AprX86 doesn't need real BIOS, hack disappears naturally |

**Port strategy**: AprX86's 8086 spec + emitter **does not reference
Apr86's buggy sections** (AAM/AAD/IMUL/MUL flag); those are written
straight from the 8086 datasheet + Tom Harte SST expected output.
Apr86's non-buggy sections (basic ALU, ModR/M decoder, segment
override handling) can be cross-referenced as implementation hints.

---

## 3. Pillar #1 — Tom Harte SingleStepTests 8088_v2

### 3.1 Source
- GitHub: https://github.com/SingleStepTests/8088_v2
- Industry usage: PCjs / 8086tiny / FreeDOS / multiple modern 8086 emulators
- Free / open license

### 3.2 Structure
One JSON file per opcode, ~10000 test cases each. One case looks like:

```json
{
  "name": "01.0",
  "bytes": [0x01, 0xC8],     // ADD AX, CX
  "initial": {
    "regs": { "ax": 0x1234, "cx": 0xABCD, "ip": 0x0100, ... },
    "ram": [[0x100, 0x01], [0x101, 0xC8], ...]
  },
  ...
}
```

> The remainder of this document covers detailed mid-Phase planning
> (Tom Harte runner integration, screenshot demo design, scoping
> exclusions, sub-phase breakdown, etc.) which is preserved verbatim
> in the Chinese original at `MD/design/24-8086-port-plan.md`.
> Phase 24 is closed; refer to the Sprint Status table below for
> what landed.

---

## Sprint status (Phase 24)

| Sub-phase | Deliverable | Status | Commit |
|---|---|---|---|
| 24.0–24.5 | Legacy 8086 backend + Tom Harte SST + 6 demo screenshots | ✅ | 13 commits (`...4730945`..`78d5cf1`) |
| 24.6.1 | NOP/HLT smoke (JSON-driven scaffold) | ✅ | — |
| 24.6.2 | First MOV opcodes through SpecCompiler | ✅ | — |
| 24.6.5 | Full MOV / PUSH-POP / XCHG / LEA / segment overrides | ✅ | — |
| 24.6.6 | ALU 76 ops + INC/DEC / 80-83 group / TEST / NEG / MUL / IMUL / CBW / CWD | ✅ | — |
| 24.6.7a–g | Control flow / shift count=1 / string ops / REP / flag manip / IO / INT/IRET / FE/FF group / BCD / DIV/IDIV | ✅ | 8 commits (`9aa7f6c`..`5958c0b`) |
| 24.6.7b2 | Shift by CL (D2/D3) | ✅ | `f70f260` |
| 24.6.8 | Block-JIT mode (alloca + mem2reg, aligned with NES N1.B') | ✅ | `08d8ef8` |
| 24.6.9 | 24.5 demos through json-block backend, SHA256 pixel-identical to legacy | ✅ | `6f0045a` |
| 24.6b | (optional) Lockstep diff vs Apr86 (.com program range) | ⏳ | — |
| **24.7** | 80186 spec via inheritance (#23) — ENTER/LEAVE demo | ✅ | Phase 25 (`585c6b2`..`878dc92`); see [25-i80186-implementation-plan.md](25-i80186-implementation-plan.md) |
| **24.8** | 80286 real-mode + protected-mode demos — 4 CPUs all green + 5-ROM CLI fault matrix | ✅ | Phase 27a (`95e5138`..`78cf9fd`) + Phase 27b (`11fdc98`..`4066b66`); see [`27-i80286-completion-plan.md`](27-i80286-completion-plan.md) and [`MD/performance/202605110200-i80286-pmode-fault-model-complete.md`](../performance/202605110200-i80286-pmode-fault-model-complete.md) |

8088 silicon quirks correctly implemented: PUSH-SP post-decrement,
PUSHF reserved-bit mask, MUL high-byte SF/ZF/PF, SHL AF=bit 4 of
result, SHR/SAR AF=0.

T1 836/836 green throughout the phase.

## 9. Paper-quality screenshot deliverables

Final "8086 framework demo" screenshot collection:

```
result/x86-16/
├── hello-cga-i8086.png              ← "Hello AprX86" baseline validation
├── primes-i8086.png                 ← prime number listing
├── mandelbrot-ascii-i8086.png       ← Mandelbrot set in ASCII characters
├── fibonacci-i8086.png              ← Fibonacci sequence
├── enter-leave-i80186.png           ← 80186 ENTER/LEAVE proves inheritance
└── (Phase 27b protected-mode demo replaced by 5-ROM CLI fault matrix —
   the i80286 backend has no integrated CGA; see Phase 27b closure note)
```

These plus existing screenshots:
- `result/gb/json-llvm/cpu_instrs.png` (Blargg 11/11)
- `result/gba/bios_lle_arm.png` (jsmolka ARM)
- `result/gba/bios_lle_thumb.png` (jsmolka Thumb)
- `result/nes/nestest.png`

**4 CPUs + multiple ISA modes + inheritance proof of concept** =
the visual evidence set for full framework genericity.

---

## 10. Risks + mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| **Tom Harte test parsing complexity / API mismatch** | medium | Validate one opcode (ADD) end-to-end before scaling; JSON parsing is known tech |
| **8086 ModR/M decode complexity** | high | Apr86's ModR/M code as reference; Tom Harte traces include decoded effective addr for cross-check |
| **DAA / AAA / etc. BCD stuck** | medium | Expected to take a few days; Tom Harte covers all corner cases, fix one by one |
| **Font / palette copyright** | low | Apr86's pre-dumped PNG (IBM PC ROM 1981 font, copyright long expired), or GPL'd vga-text-mode-fonts |
| **Inheritance not stress-tested** | medium | Must finish 8086 base + Tom Harte all-green (24.4) before starting 80186 (24.7) — don't do both at once |
| **Over-scope (graphics / real BIOS)** | high | Strict §7 "not doing" list; graphics etc. wait until after 24.5, then a new phase |

---

## 11. Relationship to existing docs

- **#20 adding-a-new-cpu.md** — 8086 is the SOP execution for adding the 4th CPU; this doc is the 8086-specific phase plan.
- **#23 cpu-spec-inheritance.md** — provides the inheritance mechanism; phase 24.7+ is its stress test.
- **Existing NES / GBA / GB screenshot conventions** — same CGA framebuffer + PNG render path; future paper / framework demo screenshot set extends to 4 CPUs.
- **Existing lockstep diff toolkit (N5)** — optional cross-check mechanism for phase 24.6.
