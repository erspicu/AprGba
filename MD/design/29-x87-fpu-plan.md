# Phase 29 — x87 FPU support (planning)

Status: **planning** — captured during Phase 28.IO when the no-op FPU
stub (0xD8-0xDF → `x86_fpu_noop`) was added to unblock real PC/XT BIOS
POST. Real BIOS FPU detection passes via the no-op path (BIOS sees
FSTCW write nothing, concludes "no FPU", clears equipment bit). A full
8087 emulation is not required for FreeDOS boot, but is needed for any
DOS program that touches floating-point math (Turbo C/Pascal, AutoCAD,
MS Flight Simulator, Windows 3.x WIN87EM, etc.).

This document captures the architectural decisions agreed with the
Gemini knowledgebase consultation on 2026-05-15 (two passes: design
basics + separate-vs-integrated). The actual implementation will land
in Phase 29.x sprints once Phase 28.x is fully closed.

---

## 1. Spec layout — separate JSON file, merged at load time

**Vote: Integrated data model via compile-time JSON composition (mix-in).**

Separate JSON for modularity, but merge into the same JIT pipeline:

```
spec/
  cpu/x86-16/i8086/cpu.json          (no FPU — D8-DF undefined here)
  cpu/x86-16/i80286/cpu.json
  coprocessors/x87/i8087.json        (NEW — registers + D8-DF opcodes)
  coprocessors/x87/i80287.json       (NEW — overrides for 287 exception handling)
  machines/ibm-pc-xt.json            (machine references both via extensions)
```

Machine config:

```json
{
  "name": "IBM PC/XT",
  "components": {
    "main_cpu": {
      "spec": "cpu/x86-16/i8086/cpu.json",
      "extensions": ["coprocessors/x87/i8087.json"]
    }
  }
}
```

**Loader behavior**: at startup, read `cpu.json`, then each extension. Append
FPU registers (ST0-ST7, control word, status word, tag word, TOP) to the
base CPU's register array. Append D8-DF opcode entries to the decode tree.
By the time the LLVM emitter starts JITting, it sees one unified CPU
model with D8-DF defined and 8 new floating-point registers.

This matches QEMU TCG (`CPUX86State.fpregs`), Bochs (`bx_cpu_c` monolithic
class), 86Box, and Unicorn — all major emulators integrate FPU into the
main CPU state at runtime despite separating it conceptually.

## 2. Register file representation

Flat array + TOP index, declared in JSON, rotation handled in C# emitter:

```json
{
  "fpu_regs":  { "type": "f64[8]", "comment": "stack-allocated rotating registers" },
  "fpu_tags":  { "type": "u8[8]",  "comment": "0=valid 1=zero 2=special 3=empty" },
  "fpu_top":   { "type": "u32",    "comment": "0-7, points to ST(0)" },
  "fpu_cw":    { "type": "u16",    "comment": "control word — rounding/precision/exception masks" },
  "fpu_sw":    { "type": "u16",    "comment": "status word — C0-C3, TOP, exception flags" }
}
```

**Why flat `f64` not `x86_fp80`**: LLVM `x86_fp80` is target-dependent
and especially painful on ARM64 (software emulation or refuses to lower).
99.9% of DOS programs use 32-bit float or 64-bit double in memory and
only rely on the 80-bit internal precision to avoid intermediate rounding.
Standard f64 IEEE-754 is adequate for Turbo C, AutoCAD, etc.

**Catch — m80fp memory format**: `FLD m80fp` / `FSTP m80fp` read/write
10 bytes. Write extern helpers (`fpu_load_m80(addr)`, `fpu_store_m80(addr, val)`)
that pack/unpack the 10-byte format into f64 (lose precision on load,
synthesize the extra bits on store).

**Rotation strategy**: don't emit `(TOP + i) % 8` per access into LLVM IR
(SSA bloat). C# emitter reads `fpu_top` once per instruction, computes
absolute index, uses `getelementptr` (GEP) to access the flat array.

## 3. Opcode encoding — pivot on ESC byte + ModR/M reg field

Don't explode into 200+ JSON entries. x87 encoding is logical:
- ESC byte (D8-DF) + ModR/M `reg` field (bits 3-5) → operation
- ModR/M `mod` field (bits 6-7) → operand source (mod=3 → ST(i), else memory)
- ESC byte → memory operand size (D8=f32, DC=f64, etc.)

JSON shape per Gemini:

```json
{
  "mask": "0xFF", "match": "0xD8", "reg_match": 0, "has_modrm": true,
  "op": "fpu_add", "mem_size": "f32", "st_op": true
}
```

The C# `fpu_add` emitter at JIT time:
- If memory operand: emit `memory_read_f32` extern call, extend to f64, emit LLVM `fadd`.
- If register (ST(i)): emit GEP to `fpu_regs[top+i mod 8]`, emit LLVM `fadd`.

## 4. Transcendentals — extern call to C# Math library

**Don't** use LLVM intrinsics (`llvm.sin.f80` etc.) — target-dependent,
unreliable for fp80 on non-x86.

**Don't** emit polynomial expansions inline.

**Do** call out to C# externs:
```c#
[UnmanagedCallersOnly] static double FpuTan(double x) => Math.Tan(x);
[UnmanagedCallersOnly] static double FpuSqrt(double x) => Math.Sqrt(x);
```

Bind once at JIT setup; the transition overhead is negligible since
transcendentals are hundreds of cycles on real silicon.

**Historical note**: 8087 and 80287 did NOT have FSIN/FCOS/FSINCOS —
those are 80387+. The 8087 only had FPTAN (tangent) and FPATAN (arctan);
guest software computed sine/cosine from tangent identities. If we are
strictly emulating 8087, we only need: FPTAN, FPATAN, F2XM1, FYL2X,
FYL2XP1, FSCALE, FXTRACT, FSQRT, FRNDINT, FPREM, FABS, FCHS.

## 5. Status word + C0-C3 condition codes

**Don't** do lazy flag evaluation for FPU compares — they're rare
relative to integer ALU ops, and guest software almost always reads
the status word immediately via `FNSTSW AX; SAHF` after `FCOM`.

After each `FCOM` / `FTST` / `FUCOM`:
1. Emit LLVM `fcmp` (oeq / ogt / olt / uno) yielding i1 values.
2. Shift each i1 into the correct bit of `fpu_sw`:
   - C0 → bit 8
   - C1 → bit 9
   - C2 → bit 10
   - C3 → bit 14
3. Emit a single store into `fpu_sw`.

`FNSTSW AX` reads `fpu_sw` and writes it into the CPU's AX. Because
the FPU spec is merged into the same state struct at load time, this
compiles to a single LLVM struct-field store — no cross-module callbacks.

## 6. Pending exceptions (#MF) — mask everything

Default 8087 init state (after FINIT) is all exceptions masked. In this
state, divide-by-zero produces IEEE infinity, invalid ops produce QNaN.

99% of DOS/Win16 software:
1. Calls FINIT or boots into the masked default.
2. Never installs an FPU exception handler (INT 10h on 8086 IRQ-via-NMI route).
3. Checks for NaNs manually if it bothers checking at all.

**Recommendation**: hardcode our control word to ignore guest attempts to
unmask exceptions. Let LLVM's default IEEE-754 behavior produce
infinities and NaNs normally. Skip #MF delivery entirely.

If a specific program (rare protected-mode DOS extender) later crashes
because it expects an INT 75h on DivZero, add it then. For now: dead weight.

---

## Phase 29.x sprint plan (deferred)

| Sprint | Scope | Notes |
|---|---|---|
| 29.1 | Spec loader extensions support | Read `"extensions"` array, merge JSON ASTs |
| 29.2 | FPU register file in state struct | f64[8] + tags + top + cw + sw |
| 29.3 | Data movement | FLD / FST / FSTP / FXCH / FCMOV (mem+reg forms) |
| 29.4 | Arithmetic | FADD / FSUB / FMUL / FDIV (+R variants, +P variants) |
| 29.5 | Compares | FCOM / FCOMP / FCOMPP / FTST / FUCOM + FNSTSW AX |
| 29.6 | Constants | FLDZ / FLD1 / FLDPI / FLDL2E / FLDL2T / FLDLG2 / FLDLN2 |
| 29.7 | Transcendentals | FPTAN / FPATAN / F2XM1 / FYL2X (via extern Math) |
| 29.8 | Misc | FSQRT / FABS / FCHS / FRNDINT / FSCALE / FXTRACT / FPREM |
| 29.9 | Control | FNINIT / FNCLEX / FNSTCW / FLDCW / FNSTSW / WAIT |
| 29.10 | Memory m80fp | Pack/unpack 10-byte format via extern helpers |
| 29.11 | Integration test | Turbo Pascal hello-world with real-mode float math |
| 29.12 | Capstone | AutoCAD R1.4 or Lotus 1-2-3 numeric demo |

## Open questions

- Whether to ship as `i8087` first (simplest, 1980 ISA) or `i80287` (1982,
  same opcodes + protected-mode integration). The 8087-only opcodes (FENI,
  FDISI) are no-ops on later chips; otherwise the spec is upward-compatible.
- Whether `"extensions"` should be inside `cpu.json` or only in `machines/`.
  Gemini argues machines/ (because the same CPU might pair with no-FPU or
  Weitek-1167); current consensus = machines/.
- Whether FWAIT (0x9B) should be a separate JSON entry or already-existing
  no-op. Real 8087 used FWAIT to synchronize CPU+FPU; our integrated model
  can decode FWAIT as a real no-op (single-byte) and skip the sync.

## Cross-references

- Phase 28.IO closure note: `MD/performance/202605152230-pc-emulator-phase-28-io.md`
  (port dispatch + no-op FPU stub that unblocked real BIOS POST)
- Gemini consultation logs:
  - `tools/knowledgebase/message/20260515_223616.txt` (FPU design basics)
  - `tools/knowledgebase/message/20260515_224332.txt` (separate-vs-integrated)
