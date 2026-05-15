# Phase 29 — x87 FPU support

> **Status**: ✅ **FUNCTIONALLY COMPLETE** (2026-05-16). All core
> 8087/80287 ESC opcode categories shipped across 11 sprints with their
> own verifying test ROMs. The JSON-driven framework supports
> coprocessor extensions as first-class peers to the base CPU (separate
> spec files, runtime data model unified). Closure note:
> [`MD/performance/202605160100-x87-fpu-functional-complete.md`](../../MD/performance/202605160100-x87-fpu-functional-complete.md).
> Detailed plan + opcode tables + dispatcher design pseudo-code (incl.
> Gemini consultation summaries) live in the Chinese counterpart
> [`MD/design/29-x87-fpu-plan.md`](../../MD/design/29-x87-fpu-plan.md);
> this file is a brief English mirror.

## Goals and design

After Phase 28.IO added a no-op `0xD8-0xDF` stub to unblock real PC/XT
BIOS POST FPU-presence probing, Phase 29 implements proper 8087/287
emulation so DOS programs that actually do floating-point math (Turbo
C/Pascal, Microsoft C real-mode runtime, AutoCAD R1.4, Lotus 1-2-3 fp
options, Windows 3.x WIN87EM) have a working FPU.

Per Gemini consultation (two passes — "FPU design basics" + "separate
vs integrated spec"; logs in `tools/knowledgebase/message/20260515_*.txt`):

- **Files separate, runtime unified.** Mirror of QEMU TCG / Bochs /
  86Box / Unicorn — all of which keep FPU register state inside the
  same `CPUState` struct as integer GPRs and decode FPU instructions in
  the main translate loop. Conceptual swappability without runtime
  cross-module callbacks.
- **f64 internal precision, not x86_fp80.** LLVM `x86_fp80` is target-
  dependent (refuses to lower or falls back to software on ARM64).
  Standard `double` is adequate for DOS programs that use m32fp/m64fp
  in memory anyway.
- **Transcendentals via C# `Math.*` externs.** LLVM transcendental
  intrinsics are unreliable for fp80 on non-x86. `[UnmanagedCallersOnly]`
  shim + indirect-call-via-global-pointer pattern (already used for
  memory + port externs) routes to `Math.Tan` / `Math.Atan2` /
  `Math.Log2` / `Math.Pow(2,x)-1`.
- **Mask all exceptions; skip #MF delivery.** 99% of DOS code uses the
  FNINIT-default masked-all-exceptions state and reads NaN/Inf from
  results rather than installing an FPU INT vector.

## Spec layout

```
spec/
  machines/
    ibm-pc-xt.json
      "extensions": ["../coprocessors/x87/i8087/cpu.json"]
  coprocessors/
    x87/
      i8087/
        cpu.json              # register_file_additions + instruction_set_additions
        groups/
          fpu-esc.json        # 8 per-byte ESC formats (D8..DF)
  cpu/x86-16/i8086/...        # base CPU spec unchanged
```

`MachineSpec.Extensions` (added in Phase 29.1) is a string array of
relative paths resolved against the machine spec file's directory.
`SpecLoader.LoadCpuSpecWithExtensions(cpuPath, extensionPaths)` reads
the base CPU spec, then for each extension parses
`register_file_additions.status[]` (appended to base
`RegisterFile.Status[]` so base offsets stay stable) and
`instruction_set_additions.encoding_groups[]` (prepended to the matching
`InstructionSetSpec.EncodingGroups[]` so more-specific extension masks
win over base patterns). Returns a single merged `LoadedSpec` that
`SpecCompiler.Compile` consumes unchanged — one LLVM module, one CPU
state struct.

## Implemented opcodes

| Category | Opcodes | Sprint |
|---|---|---|
| Stack push/pop helpers | (internal) | 29.3c |
| Data movement m32fp | FLD m32, FSTP m32 | 29.3c/d |
| Data movement m64fp | FLD m64, FST m64, FSTP m64 | 29.3e |
| Register-form FXCH | FXCH ST(i) | 29.3d |
| Constants | FLD1, FLDL2T, FLDL2E, FLDPI, FLDLG2, FLDLN2, FLDZ | 29.3d |
| Arithmetic (m32 + ST(i)) | FADD, FMUL, FSUB, FSUBR, FDIV, FDIVR | 29.4 |
| Compares | FCOM, FCOMP, FTST | 29.5/8 |
| FNSTSW AX | DF E0 | 29.5 |
| Misc unary | FCHS, FABS, FSQRT, FRNDINT, FNOP, FFREE | 29.8 |
| Stack twiddle | FDECSTP, FINCSTP | 29.7 |
| Transcendentals | F2XM1, FYL2X, FPTAN, FPATAN | 29.7 |
| Control | FNINIT, FNCLEX, FLDCW, FSTCW | 29.3b/9 |

~30 distinct 8087/287 opcodes covering the entire ESC family Intel
documented for the 8087-tier ISA.

## Test ROM matrix

All 7 ROMs verify bit-exact f32/f64 results vs .NET `Math.*` /
glibc. Run with `apr-x86 --rom=PATH --enable-i8087 --backend=json
--dump-fpu-state`.

```
test-roms/x86/
├── 29.3-fninit.com              FNINIT smoke
├── 29.3-fpu-roundtrip.com       FLDZ + FSTP m32
├── 29.3d-fpu-suite.com          FLDPI, FLD m32, FSTP m32, FXCH
├── 29.4-fpu-arith.com           6 arithmetic ops
├── 29.5-fpu-compare.com         3 FCOM outcomes + FNSTSW AX
├── 29.7-fpu-transcendental.com  4 transcendentals
├── 29.8-fpu-misc.com            FCHS/FABS/FSQRT/FRNDINT/FTST
├── 29.9-fpu-control.com         FLDCW/FSTCW roundtrip
└── 29.11-fpu-integration.com    Hypotenuse capstone (chains everything)
```

## Sprint chain

| Sprint | commit |
|---|---|
| 29.1 spec loader extensions | `4747037` |
| 29.2 FPU register file | `7a08584` |
| 29.3a per-byte ESC dispatcher split | `d9143f3` |
| 29.3b FNINIT + IP advance fix + state accessors | `6a50751` |
| 29.3c FLDZ + FSTP m32 + Push/Pop helpers | `e60e5c3` |
| 29.3d FLD m32 + FXCH + 6 constants | `95e49cb` |
| 29.4 arithmetic 6 ops | `c401d0d` |
| 29.5 FCOM/FCOMP + FNSTSW AX | `a8aa6e5` |
| 29.8 misc unary + FFREE | `26a25f0` |
| 29.9 FLDCW/FSTCW/FNCLEX | `d479e17` |
| 29.7 transcendentals via Math externs | `5983ef5` |
| 29.3e + 29.11 m64fp + integration capstone | `2513905` |

## Deferred (truly optional)

- m80fp pack/unpack (10-byte extended format; rare outside Turbo
  Pascal Extended)
- DC/DA/DE arithmetic family (f64 reg arith + i32/i16 integer arith +
  pop-after register variants — pattern mirrors of D8)
- DF integer load/store (FILD/FIST/FISTP m16/m32/m64int — only
  FNSTSW AX done)
- FSCALE / FXTRACT / FPREM (libm internals; defer to future 80387)
- TOP_SW ↔ FPU_TOP sync (DOS code reads C bits via SAHF+JCC, doesn't
  read TOP_SW)
- #MF exception delivery (per Gemini, masked exceptions only)

## Why Phase 29 matters for the framework

Phase 24-26 proved single-CPU JSON dispatch works. Phase 27 proved spec
inheritance (i80286 extends i8086) works. Phase 29 proves the framework
supports **orthogonal coprocessor extensions** — same base CPU spec
paired with one of multiple peer components selected at machine-config
time. The 8087 is not an inherited variant of the 8086; it's a
historically separate die that physically sat next to the 8086 on the
ISA bus. The JSON spec layout reflects that.

A hypothetical future "PC with no FPU" machine config simply omits the
`extensions` array — `0xD8-0xDF` then have no decoder entry and the
CPU follows its existing unknown-opcode path. A future "PC with Weitek
1167" would add `spec/coprocessors/weitek/1167/cpu.json` with its own
opcode group and reference it from a new machine spec. Zero code
changes — purely JSON-driven swappable silicon.

## Cross-references

- Phase 28 closure (FreeDOS boot): `MD/performance/202605152200-pc-emulator-freedos-boot.md`
- Phase 28.IO (port I/O + initial FPU no-op): `MD/performance/202605152230-pc-emulator-phase-28-io.md`
- Phase 29 closure: `MD/performance/202605160100-x87-fpu-functional-complete.md`
- Gemini consultation logs:
  - `tools/knowledgebase/message/20260515_223616.txt` (FPU design basics)
  - `tools/knowledgebase/message/20260515_224332.txt` (separate vs integrated)
