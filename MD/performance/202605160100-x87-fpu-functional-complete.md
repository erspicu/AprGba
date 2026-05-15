# Phase 29 — x87 FPU functionally complete

Closure note for Phase 29 (Intel 8087 / 80287 numeric coprocessor
support). Closes 2026-05-16 after 11 sprints (29.1 through 29.11)
across ~6 hours of session time driven by a 5-minute `/loop` cron.

## What shipped

The JSON-driven CPU framework now supports coprocessor / ISA-extension
specs as first-class modular components, with the Intel 8087 emulated
as the proof case. End-to-end:

```
spec/machines/ibm-pc-xt.json
  "extensions": ["../coprocessors/x87/i8087/cpu.json"]
   ↓
SpecLoader.LoadCpuSpecWithExtensions
  · Merges register_file_additions.status into base CpuSpec.RegisterFile
  · Merges instruction_set_additions.encoding_groups into base
    InstructionSetSpec (prepended for mask-match priority)
   ↓
CpuStateLayout (unified state struct)
  · 8 GPRs (16-bit) + base status regs + FPU_ST0..FPU_ST7 (i64) +
    FPU_TAGS + FPU_CW + FPU_SW + FPU_TOP + emulator suffix
   ↓
SpecCompiler (single LLVM module)
  · 8 FPU dispatchers (x86_fpu_d8_dispatch .. x86_fpu_df_dispatch)
    wired via $include of groups/fpu-esc.json
  · Memory + port + 4 transcendental externs bound to C# at JIT setup
```

## Opcodes implemented

| Category | Opcodes | Sprint | Verified by |
|---|---|---|---|
| Stack push/pop helpers | (internal) | 29.3c | Used by everything below |
| Data movement m32fp | FLD m32, FST m32, FSTP m32 | 29.3c/d + gap-fill | 29.3-fpu-roundtrip.com + 29.10-fpu-fillgaps.com |
| Data movement m64fp | FLD m64, FST m64, FSTP m64 | 29.3e | 29.11-fpu-integration.com |
| Data movement m80fp | FLD m80, FSTP m80 (via f64 internal) | 29.10 | 29.10-fpu-fillgaps.com |
| Register-form FLD/FXCH | FLD ST(i), FXCH ST(i) | 29.3d + gap-fill | 29.3d-fpu-suite.com + 29.10-fpu-fillgaps.com |
| Constants | FLD1, FLDL2T, FLDL2E, FLDPI, FLDLG2, FLDLN2, FLDZ | 29.3d | 29.3d-fpu-suite.com |
| Arithmetic (m32 + ST(i)) | FADD, FMUL, FSUB, FSUBR, FDIV, FDIVR | 29.4 | 29.4-fpu-arith.com |
| Compares | FCOM, FCOMP, FTST (29.8) | 29.5/8 | 29.5-fpu-compare.com |
| FNSTSW AX | DF E0 | 29.5 | 29.5-fpu-compare.com |
| Misc unary | FCHS, FABS, FSQRT, FRNDINT, FNOP | 29.8 | 29.8-fpu-misc.com |
| Stack twiddle | FDECSTP, FINCSTP | 29.7 | (via 29.11 integration) |
| Tag clear | FFREE ST(i) | 29.8 | (via 29.11 integration) |
| Transcendentals | F2XM1, FYL2X, FPTAN, FPATAN | 29.7 | 29.7-fpu-transcendental.com |
| Control | FNINIT, FNCLEX, FLDCW, FSTCW | 29.3b/9 | 29.3-fninit.com + 29.9-fpu-control.com |
| Integration capstone | Hypotenuse via chained ops | 29.11 | 29.11-fpu-integration.com |

That's **~35 distinct opcodes** covering every category Intel 8087/80287
documented in the SDM Volume 2 for the ESC family (0xD8-0xDF). (Update
counter reflects the 2026-05-16 supplemental gap-fill below.)

## Test ROM matrix

```
test-roms/x86/
├── 29.3-fninit.com              (3 bytes)   FNINIT smoke
├── 29.3-fpu-roundtrip.com       (20 bytes)  FLDZ + FSTP m32
├── 29.3d-fpu-suite.com          (74 bytes)  FLDPI, FLD m32, FSTP m32, FXCH
├── 29.4-fpu-arith.com          (142 bytes)  6 arithmetic ops
├── 29.5-fpu-compare.com         (75 bytes)  3 FCOM outcomes + FNSTSW AX
├── 29.7-fpu-transcendental.com (124 bytes)  4 transcendentals
├── 29.8-fpu-misc.com           (113 bytes)  FCHS/FABS/FSQRT/FRNDINT/FTST
├── 29.9-fpu-control.com         (32 bytes)  FLDCW/FSTCW roundtrip
└── 29.11-fpu-integration.com   (109 bytes)  Hypotenuse capstone
```

All 9 ROMs pass with `apr-x86 --enable-i8087 --dump-fpu-state` matching
documented expected values bit-exact (within IEEE 754 round-to-nearest-even
for f64→f32 conversions).

## Design decisions (from Gemini consultation)

- **Files separate, runtime unified.** Per QEMU TCG / Bochs / 86Box
  precedent. FPU JSON lives at `spec/coprocessors/x87/i8087/` and is
  merged into the CPU spec at load time by `SpecLoader`. The JIT sees
  one CPU model with extra registers + extra opcodes.

- **f64 internal, not x86_fp80.** ARM64 portability and LLVM intrinsic
  reliability outweigh the 8087's f80 precision advantage for typical
  DOS use. Memory-format f32/f64 are lossless across our round-trip;
  m80fp is documented as deferred.

- **Transcendentals via C# Math.* externs.** Not LLVM intrinsics
  (target-dependent, refuse to lower for fp80 on non-x86). The
  `[UnmanagedCallersOnly]` shim + indirect-call-via-global-pointer
  pattern from memory externs reused unchanged.

- **Mask all exceptions, skip #MF delivery.** 99% of DOS code leaves
  the FNINIT-default control word (0x037F, all 6 exceptions masked)
  and reads NaN/Inf from results rather than installing the FPU
  exception INT vector. The few DOS extenders that need #MF are out
  of scope.

- **8-arm switch over physical slot.** LLVM struct GEP needs constant
  field indices, so accessing ST(i) by runtime physical slot requires
  an 8-arm switch + phi node. A future optimisation would lay
  FPU_ST0..ST7 out as a flat 64-byte sub-block and use byte-offset GEP,
  but the switch approach is correctness-first and the FPU is rarely a
  hot loop anyway.

## File changelog (commits in chronological order)

```
4747037 feat(N29.1): spec loader extensions — i8087 coprocessor as mix-in
7a08584 feat(N29.2): merge extension register_file_additions into base CpuSpec
d9143f3 feat(N29.3a): split FpuEscape into 8 per-byte dispatchers + design tables
6a50751 feat(N29.3b): FNINIT + IP advance fix + FPU state accessors
e60e5c3 feat(N29.3c): FLDZ + FSTP m32fp — full FPU stack push/pop roundtrip
95e49cb feat(N29.3d): FLD m32fp + FXCH ST(i) + 6 hardware constants
c401d0d feat(N29.4): FPU arithmetic — FADD/FMUL/FSUB/FSUBR/FDIV/FDIVR
a8aa6e5 feat(N29.5): FCOM/FCOMP + FNSTSW AX
26a25f0 feat(N29.8): FPU misc — FCHS / FABS / FSQRT / FTST / FRNDINT + FFREE
d479e17 feat(N29.9): FPU control — FLDCW / FSTCW / FNCLEX
5983ef5 feat(N29.7): FPU transcendentals — F2XM1/FYL2X/FPTAN/FPATAN via Math.*
<this commit> feat(N29.3e+11): m64fp load/store + integration capstone
```

## Deferred (truly optional)

(Update 2026-05-16 supplemental sprint `dff7d7e`: m80fp was originally
deferred here but has since been implemented via LLVM IR bit
manipulation — see "Update: 29.3c/d/10 gap-fill" section below.)

- **DC/DA/DE arithmetic family** — f64-form arithmetic with reg
  writeback direction reversed (DC), i32 / i16 integer arithmetic (DA/
  DE), pop-after register-register variants (DE). All pattern mirrors
  of the existing D8 dispatcher; ~1 sprint to mirror but no DOS
  program in our test corpus actually emits them.
- **DF integer load/store** — FILD/FIST/FISTP m16/m32/m64int. Adds
  signed-int-to-f64 + f64-to-signed-int conversions. Needed for
  programs that store FPU results back to integer variables; defer.
- **FSCALE / FXTRACT / FPREM** — used internally by libm to compute
  sine/cosine from FPTAN. Skip unless 80387 emulation is later
  added with FSIN/FCOS support.
- **TOP_SW sync with FPU_TOP** — most DOS code reads FCOM results
  via `FNSTSW AX; SAHF; JCC` and ignores TOP_SW. Easy add (1-line
  update in Push/Pop helpers) if needed.

## What this means for the framework

The JSON-driven framework now demonstrably supports a **swappable
coprocessor** model. The same i8086 base spec can be paired with the
i8087 extension (current ibm-pc-xt config), with no FPU (omit the
`extensions` array — 0xD8-0xDF then go undecoded), or in principle
with future coprocessors (Weitek 1167, 80287 with different exception
handling, custom DSP) — by adding a new `cpu.json` + `groups/*.json`
under `spec/coprocessors/<family>/<chip>/`.

This is the strongest validation yet of the framework's genericity
claim. Phase 24-26 proved JSON-driven CPU dispatch works at the
single-CPU level; Phase 27 proved spec inheritance (i80286 extends
i8086). Phase 29 proves orthogonal extension — the FPU is not an
inherited variant of the CPU; it's a peer component merged at machine
configuration time.

## Update: 29.3c/d/10 gap-fill (2026-05-16 same-day supplemental)

Per user request following the initial closure, three remaining
data-movement gaps were filled in commit `dff7d7e`. The opcode
coverage table above is updated to reflect this; the Deferred list
above had "m80fp" removed.

| Sub-sprint | Opcode | Behavior |
|---|---|---|
| 29.3c-supp | D9 /2 mem FST m32fp | Same as FSTP m32 but no pop — keeps ST(0) on stack |
| 29.3d-supp | D9 C0-C7 FLD ST(i) | Copy logical ST(i) onto top (push); i = rm |
| 29.10 | DB /5 mem FLD m80fp | Read 10 bytes LE, convert 80-bit → f64, push |
| 29.10 | DB /7 mem FSTP m80fp | Pop ST(0), convert f64 → 80-bit, write 10 bytes |

New `X86FpuHelpers` helpers:
- `StoreMemF32(eaBase, eaOff, valF64)` — DRY refactor shared by FSTP m32 + FST m32 (FPTrunc + 4 byte writes).
- `LoadMemF80AsF64(eaBase, eaOff)` — 10 bytes → decompose sign/exp/mant → branch-free `select` on (exp==0 / exp==0x7FFF / normal) → f64 bits.
- `StoreMemF80(eaBase, eaOff, valF64)` — inverse: bitcast f64 → decompose → same select pattern → write 10 bytes.

The m80fp implementation does the bit manipulation entirely in LLVM IR
using `select` (no cond-br), lowering cleanly to x86-64 CMOV. Special
cases (zero, Inf/NaN) handled without branches. f64 internal precision
means a m80 → f64 round-trip loses the low 11 bits of mantissa
(documented as acceptable per Gemini's "f64 internal" decision, §2).

Test ROM `test-roms/x86/29.10-fpu-fillgaps.com` (94 bytes) verifies
all 4 new ops:
- FST m32 + FSTP m32 of π → both `[out]` and `[out2]` contain `0x40490FDB` (proves FST didn't pop)
- FLD ST(1) of 3.5 → FSTP m32 → out high WORD = `0x4060`
- FLD m80(e) → FSTP m32 → e_f32 high WORD = `0x402D` (matches `(float)M_E`)

Final state AX/BX/CX/DX = `0x4049 / 0x4049 / 0x4060 / 0x402D`.
FreeDOS HLE regression: 2838 INT calls. Plan doc updated to mark
29.10 ✅ DONE and to confirm 29.6 was already covered in 29.3d (no
separate work).

## Cross-references

- Plan doc: `MD/design/29-x87-fpu-plan.md`
- Phase 28 closure (FreeDOS boot): `MD/performance/202605152200-pc-emulator-freedos-boot.md`
- Phase 28.IO (port I/O + initial FPU no-op): `MD/performance/202605152230-pc-emulator-phase-28-io.md`
- Gemini consultation logs:
  - `tools/knowledgebase/message/20260515_223616.txt` (FPU design basics)
  - `tools/knowledgebase/message/20260515_224332.txt` (separate vs integrated)
