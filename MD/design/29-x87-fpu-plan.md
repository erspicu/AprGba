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

## Phase 29.x sprint plan

| Sprint | Scope | Notes |
|---|---|---|
| 29.1 | Spec loader extensions support — ✅ **DONE 2026-05-15** | `MachineSpec.Extensions` + `SpecLoader.LoadCpuSpecWithExtensions()` + `SpecCompiler.Compile(path, extensions)` + `X86JsonCpu(extensionPaths:)`. `spec/coprocessors/x87/i8087/cpu.json` + `groups/fpu-esc.json` created; FpuEscape entry **moved out** of `spec/cpu/x86-16/i8086/groups/misc.json` (proves merge end-to-end). FreeDOS regression intact (2838 HLE INT calls); real BIOS POST advanced F000:E706 → F000:F433. |
| 29.2 | FPU register file in state struct — ✅ **DONE 2026-05-15** | `register_file_additions.status[]` parsing in `LoadCpuSpecWithExtensions`. Extension status registers appended to base `RegisterFile.Status[]` so base CPU's pre-cached offsets (FLAGS/IP/CS/etc.) stay stable. i8087 extension declares ST0-ST7 (64-bit i64 slots, bitcast to f64 in Phase 29.3+ emitters per Gemini's "ARM64-friendly f64 over x86_fp80" decision), FPU_TAGS (16-bit, 2 bits per ST(i)), FPU_CW (16-bit with PC/RC/IC/exception-mask fields), FPU_SW (16-bit with C0/C1/C2/C3 condition codes + TOP_SW mirror + sticky exception flags), FPU_TOP (32-bit index 0-7). Build green; FreeDOS regression intact (2840 HLE INT calls); apr-x86 standalone Tom Harte path also untouched. |
| 29.3a | Per-byte FPU ESC dispatch split — ✅ **DONE 2026-05-15** | Replace single catch-all `FpuEscape` (mask=0xF8 match=0xD8) with 8 per-byte formats (mask=0xFF match=0xD8..0xDF) so each ESC byte gets its own dispatcher emitter (`x86_fpu_d8_dispatch` .. `x86_fpu_df_dispatch`). All 8 currently no-op so behavior is identical to pre-29.3, but the split lets us ship individual /reg sub-opcodes incrementally — D9 can land FLD/FSTP/FXCH/FLDZ while D8/DC stay no-op until 29.4 (arithmetic). FreeDOS regression: 2839 INT calls; real BIOS POST still advances to F000:F436. |
| 29.3b | FNINIT + IP advance fix + state accessor — ✅ **DONE 2026-05-15** | (1) Add `x86_fetch_modrm` + `x86_modrm_compute_ea` steps to all 8 FPU dispatcher formats so IP correctly advances past the ModR/M byte + any displacement (was: no-op stub left IP mid-instruction, BIOS POST got "lucky" muddling through misaligned bytes). (2) Implement `FNINIT` (DB E3) in `X86FpuDBDispatchEmitter` — detects `mod=11 reg=100 rm=011` and writes power-on defaults: FPU_CW=0x037F, FPU_SW=0x0000, FPU_TAGS=0xFFFF, FPU_TOP=0. (3) `X86JsonCpu.TryReadFpuTop / TryReadFpuCw / TryReadFpuSw / TryReadFpuTags / TryReadFpuPhysicalSt` accessors. (4) `apr-x86 --enable-i8087 --dump-fpu-state` flags. (5) Test ROM `test-roms/x86/29.3-fninit.com` (3 bytes: DB E3 F4): apr-x86 reports `TOP=00 CW=037F SW=0000 TAGS=FFFF`, IP advanced to 0103. FreeDOS HLE regression: 2839 INT calls. Real BIOS POST: same MDA retrace loop position (FPU detection was past long ago; remaining stall is unrelated port-0x3BA timing). |
| 29.3c | FLDZ + FSTP m32fp + stack push/pop helpers — ✅ **DONE 2026-05-15** | First sprint with f64↔i64 bitcast + memory store via segmented byte writes. `X86FpuHelpers.Push/Pop/SetTag/GepPhysicalSt` (8-arm switch over physical slot index). FLDZ (D9 EE) pushes f64(0.0), tags slot as Zero (01). FSTP m32fp (D9 /3 mem) pops ST(0), `FPTrunc` f64→f32, bitcasts to i32, writes 4 bytes little-endian via `SegmentedWrite8FromBase`, then clears tag to Empty (11) and advances TOP. Test ROM `29.3-fpu-roundtrip.com` (20 bytes): FNINIT → FLDZ → FSTP DWORD [scratch] → MOV AX,[scratch] → MOV BX,[scratch+2] → HLT; result AX=0000 BX=0000 (overwrote DEADBEEF pre-fill), TOP=00, TAGS=FFFF. FreeDOS HLE regression: 2839 INT calls. |
| 29.3d | FLD m32fp + FXCH ST(i) + FLD1/FLDPI/... — ✅ **DONE 2026-05-15** | (1) FLD m32fp (D9 /0 mod≠11) — read 4 bytes from EA via `SegmentedRead8FromBase`, assemble i32, bitcast f32, FPExt f64, push with Tag=Valid. (2) FXCH ST(i) (D9 C8..CF mod=11 reg=1) — read rm, swap physical slot TOP with `(TOP + rm) & 7` via new `X86FpuHelpers.SwapSlots` + tag-swap helper. (3) Six hardware constants D9 E8..ED (FLD1=1.0, FLDL2T=log2(10), FLDL2E=log2(e), FLDPI=π, FLDLG2=log10(2), FLDLN2=ln(2)) — all push via `Push(constReal, tag=Valid)`. Plus FLDZ moved from earlier ad-hoc check into the unified switch. Dispatcher restructured to clean mod-vs-mem branch + switch (was: chained cond-br). Test ROM `29.3d-fpu-suite.com` runs FNINIT → FLDPI → FSTP m32, FLD m32 → FSTP m32 roundtrip, and FXCH ST(1) swap; all 6 verification GPRs match expected (AX/BX=0x40490FDB for FLDPI→f32 round, SI/DI=0x40490FDA for byte-identical roundtrip, CX/DX confirm FXCH swap put 1.0 above 0.0). FreeDOS HLE regression: 2839 INT calls. |
| 29.3c | FLD m32fp + FXCH ST(i) + FLD1/FLDPI/FLDL2E/... | D9 /0 mem (FLD m32fp memory-form load), D9 C8+i (FXCH register-form), D9 E8-EE constant loads. |
| 29.3d | FLD/FST/FSTP m64fp | DD /0 /2 /3 mem forms (64-bit double load/store). |
| 29.4 | FPU arithmetic (D8 family) — ✅ **DONE 2026-05-15** | D8 dispatcher implements FADD / FMUL / FSUB / FSUBR / FDIV / FDIVR with both m32fp (mod≠11) and ST(i) (mod=11) operand forms. ST(0) := ST(0) <op> operand (or operand <op> ST(0) for reverse variants). New helpers: `LoadLogicalSt(i)` reads ST(i) via (TOP+i)&7 GEP, `StoreLogicalSt(i, val)` writes back + sets Valid tag, `LoadMemF32AsF64(eaBase, eaOff)` does the 4-byte→f32→f64 widening shared with FLD m32fp. Test ROM `29.4-fpu-arith.com` runs 6 sub-tests (FNINIT → FLD → D8 op → FSTP) and verifies all results via final GPR state: 3+4=7, 3×4=12, 10-3=7, 10-3=7 (FSUBR), 12÷4=3, 12÷4=3 (FDIVR). All 6 results match IEEE 754 f32 bit-exact (BX=40E0, CX=4140, DX=40E0, SI=40E0, DI=4040, BP=4040). FCOM/FCOMP (/2, /3) stay no-op — Phase 29.5. DC (f64-form arithmetic) and DE (pop-after register variants) follow the same pattern, deferred but easy to mirror. FreeDOS HLE regression: 2840 INT calls. |
| 29.5 | FCOM/FCOMP + FNSTSW AX — ✅ **DONE 2026-05-15** | D8 /2 FCOM and D8 /3 FCOMP wire up via new `X86FpuHelpers.Compare(st0, operand)` — uses LLVM unordered-aware `fcmp ueq` / `uno` / `ult` so NaN cases correctly set C2=C3=C0=1 per Intel SDM. Read-modify-write FPU_SW preserves bits outside the 0x4700 (C3\|C2\|C1\|C0) mask. FCOMP additionally calls Pop after the compare. DF E0 = FNSTSW AX implemented in `X86FpuDFDispatchEmitter` — reads FPU_SW i16 and stores into GPR 0 (AX). Test ROM `29.5-fpu-compare.com` runs three FNINIT → FLD → FCOM → FNSTSW AX → MOV [resultN], AX sequences and confirms AX=0x0100 (C0 for 3<4), AX=0x0000 (no C bits for 4>3), AX=0x4000 (C3 for 3==3). FCOMPP (DE D9), FTST (D9 E4), FUCOM (DD E0-E7), FUCOMP (DD E8-EF) follow same pattern, deferred to keep this sprint focused. Known limitation: TOP_SW (bits 11:13 of FPU_SW) not yet synchronized with FPU_TOP by Push/Pop — TODO if a real DOS program turns out to need it. FreeDOS HLE regression: 2840 INT calls. |
| 29.6 | Constants | FLDZ / FLD1 / FLDPI / FLDL2E / FLDL2T / FLDLG2 / FLDLN2 |
| 29.7 | Transcendentals | FPTAN / FPATAN / F2XM1 / FYL2X (via extern Math) |
| 29.8 | FPU misc (FCHS/FABS/FSQRT/FTST/FRNDINT + FFREE) — ✅ **DONE 2026-05-15** | D9 register-form refactor: single big switch on `combined` covers FXCH (0x08-0x0F range), FNOP (0x10), FCHS (0x20), FABS (0x21), FTST (0x24), 7 constants (0x28-0x2E), FSQRT (0x3A), FRNDINT (0x3C). FCHS uses LLVM `fneg`; FABS uses `llvm.fabs.f64` intrinsic; FSQRT uses `llvm.sqrt.f64`; FRNDINT uses `llvm.rint.f64` (round-to-nearest-even matches FNINIT default RC=00). New helper `X86FpuHelpers.BuildIntrinsicCallF64(name, arg)` declares + calls f64→f64 intrinsics lazily on first use. DD C0-C7 = FFREE ST(i) implemented in DD dispatcher — sets tag of slot (TOP+rm)&7 to Empty without touching data or TOP. Test ROM `29.8-fpu-misc.com` verifies FCHS -7→7, FABS -3.5→3.5, FSQRT 16→4, FRNDINT 3.7→4 (round-nearest-even), FTST -1<0 (C0 set) — AX=40E0 BX=4060 CX=4080 DX=4080 SI=0100. FSCALE/FXTRACT/FPREM deferred to 29.7 (transcendentals require Math externs anyway). FreeDOS HLE regression: 2841 INT calls. |
| 29.9 | FPU control (FLDCW/FSTCW/FNCLEX) — ✅ **DONE 2026-05-16** | D9 /5 mem = FLDCW m16 — `SegmentedRead16FromBase` into FPU_CW. D9 /7 mem = FSTCW m16 — load FPU_CW, write via `SegmentedWrite16FromBase`. Both use the existing memory-helper path that integer code uses, so segment-base resolution + handler dispatch behave identically. DB E2 = FNCLEX wired in DB dispatcher alongside FNINIT (switched on `combined` with cases 0x22 → FNCLEX and 0x23 → FNINIT; structure ready for FNSETPM E4 / FSETPM E5 if needed). FNCLEX masks FPU_SW with 0x7F00 (clears bits 0-7 = IE/DE/ZE/OE/UE/PE/SF/ES + bit 15 = B, preserves C0-C3 + TOP_SW). FNINIT/FNSTSW already handled (29.3b / 29.5). FSTENV / FLDENV / FNSAVE / FRSTOR (full FPU-environment block save/restore) deferred — rarely used by DOS code. Test ROM `29.9-fpu-control.com` verifies FNINIT-then-FSTCW reads back 0x037F, and FLDCW with a custom 0x0E72 pattern followed by FSTCW reads back byte-identical 0x0E72. FreeDOS HLE regression: 2839 INT calls. 29.5 + 29.4 prior test ROMs regression: identical outputs (no behavior drift). |
| 29.10 | Memory m80fp | Pack/unpack 10-byte format via extern helpers |
| 29.11 | Integration test | Turbo Pascal hello-world with real-mode float math |
| 29.12 | Capstone | AutoCAD R1.4 or Lotus 1-2-3 numeric demo |

## Dispatcher emitter design (Phase 29.3+)

Each `x86_fpu_d?_dispatch` emitter does a two-tier switch on the ModR/M
byte. The decoder framework's `x86_fetch_modrm` (called in the format's
step list before our dispatcher) caches `modrm_mod`, `modrm_reg`,
`modrm_rm` into `EmitContext.Values`, so the dispatcher just resolves
them and switches.

Pseudo-code shape (mirrors `X86FfGroupDispatchEmitter` for 0xFF):

```csharp
public void Emit(EmitContext ctx, MicroOpStep step) {
    var mod = ctx.Resolve("modrm_mod");
    var reg = ctx.Resolve("modrm_reg");
    var rm  = ctx.Resolve("modrm_rm");

    // Memory form: mod != 11. Dispatch on /reg.
    var memBB = ctx.Function.AppendBasicBlock("d9_mem");
    var regBB = ctx.Function.AppendBasicBlock("d9_regform");
    var endBB = ctx.Function.AppendBasicBlock("d9_end");
    var isMem = ctx.Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, mod, const_i32(3));
    ctx.Builder.BuildCondBr(isMem, memBB, regBB);

    ctx.Builder.PositionAtEnd(memBB);
    // Need EA — call x86_modrm_compute_ea ourselves or have the format's
    // step list emit it before dispatch. Cleaner: dispatch as the LAST
    // step after fetch_modrm + compute_ea so EA is in scope.
    var sw = ctx.Builder.BuildSwitch(reg, default_invalid, 8);
    for (int r = 0; r < 8; r++) {
        var arm = ctx.Function.AppendBasicBlock($"d9_mem_{r}");
        sw.AddCase(const_i32(r), arm);
        ctx.Builder.PositionAtEnd(arm);
        switch (r) {
            case 0: EmitFldM32(ctx);   break;   // FLD m32fp
            case 1: /* invalid */      break;
            case 2: EmitFstM32(ctx);   break;   // FST m32fp
            case 3: EmitFstpM32(ctx);  break;   // FSTP m32fp
            case 4: EmitFldenv(ctx);   break;   // FLDENV m14/m28
            case 5: EmitFldcw(ctx);    break;   // FLDCW m16
            case 6: EmitFstenv(ctx);   break;   // FSTENV
            case 7: EmitFstcw(ctx);    break;   // FSTCW m16
        }
        ctx.Builder.BuildBr(endBB);
    }

    // Register form: mod == 11. The full 6-bit (reg, rm) tuple identifies
    // the opcode. e.g., D9 C8 = FXCH ST(0), D9 EE = FLDZ.
    ctx.Builder.PositionAtEnd(regBB);
    var combined = ctx.Builder.BuildOr(
        ctx.Builder.BuildShl(reg, const_i32(3)),
        rm);  // 0..63
    var swReg = ctx.Builder.BuildSwitch(combined, default_unhandled, 32);
    swReg.AddCase(const_i32(0x00), bb_fld_st0);  // D9 C0 = FLD ST(0)
    // ... C0-C7 = FLD ST(i)
    // ... C8-CF = FXCH ST(i)
    swReg.AddCase(const_i32(0x10), bb_fnop);     // D9 D0 = FNOP
    swReg.AddCase(const_i32(0x20), bb_fchs);     // D9 E0 = FCHS
    swReg.AddCase(const_i32(0x21), bb_fabs);     // D9 E1 = FABS
    // ... E8-EE = FLD1 / FLDL2T / FLDL2E / FLDPI / FLDLG2 / FLDLN2 / FLDZ
}
```

### ST(i) access pattern

For "logical ST(i) → physical FPU_STn" addressing:

```csharp
LLVMValueRef GepLogicalSt(EmitContext ctx, LLVMValueRef logicalI /* i32 */) {
    var top = ctx.Builder.BuildLoad2(i32, fpuTopPtr, "top");
    var physI = ctx.Builder.BuildAnd(
        ctx.Builder.BuildAdd(top, logicalI),
        const_i32(7));
    // FPU_ST0..ST7 are 8 consecutive status slots in CpuStateLayout.
    // Compute the byte offset for slot (FPU_ST0 + physI * 8).
    var st0Off = ctx.Layout.StatusOffset("FPU_ST0");
    var byteOff = ctx.Builder.BuildAdd(
        const_i32((int)st0Off),
        ctx.Builder.BuildMul(physI, const_i32(8)));
    // GEP via byte-pointer math (state struct is byte-addressable in our layout).
    return ctx.Builder.BuildGEP2(i8, statePtr, byteOff, "st_phys_ptr");
}
```

Read as f64 via bitcast:
```csharp
var slot = GepLogicalSt(ctx, logicalI);
var asI64Ptr = ctx.Builder.BuildBitCast(slot, ptrToI64);
var asI64    = ctx.Builder.BuildLoad2(i64, asI64Ptr);
var asF64    = ctx.Builder.BuildBitCast(asI64, f64);
```

Write back:
```csharp
var asI64    = ctx.Builder.BuildBitCast(valF64, i64);
ctx.Builder.BuildStore(asI64, asI64Ptr);
```

### FPU stack push/pop semantics

**Push** (FLD, FLDZ, FILD, etc.):
1. `top := (top - 1) & 7`
2. Store new value into ST(0) (physical slot `top`)
3. Update FPU_TAGS for slot `top` to indicate Valid/Zero/Special

**Pop** (FSTP, FFREE, etc.):
1. Update FPU_TAGS for slot `top` to 11 (Empty)
2. `top := (top + 1) & 7`

The Gemini guidance is to compute `physI` once per instruction (not in
the LLVM IR) when possible — but our current dispatch is single-step
per instruction, so each access does its own GEP. Acceptable for
Phase 29.3 minimum-viable; constant-folding TOP reads is a 29.x
optimization.

## ESC byte → /reg opcode tables

Reference tables for the 8 dispatchers. Each entry's "phase" column
shows when it lands.

### D8 — f32 arithmetic family (mod ≠ 3 = memory; mod = 3 = ST(0) op ST(i))

| /reg | Memory form (mod ≠ 3) | Register form (mod = 3) | Phase |
|---|---|---|---|
| 0 | FADD m32fp     | FADD ST(0), ST(i)  | 29.4 |
| 1 | FMUL m32fp     | FMUL ST(0), ST(i)  | 29.4 |
| 2 | FCOM m32fp     | FCOM ST(0), ST(i)  | 29.5 |
| 3 | FCOMP m32fp    | FCOMP ST(0), ST(i) | 29.5 |
| 4 | FSUB m32fp     | FSUB ST(0), ST(i)  | 29.4 |
| 5 | FSUBR m32fp    | FSUBR ST(0), ST(i) | 29.4 |
| 6 | FDIV m32fp     | FDIV ST(0), ST(i)  | 29.4 |
| 7 | FDIVR m32fp    | FDIVR ST(0), ST(i) | 29.4 |

### D9 — data movement + constants + control (mod = 3 form is sub-opcode by full rm:reg)

| /reg | Memory form (mod ≠ 3) | Phase |
|---|---|---|
| 0 | FLD m32fp        | 29.3c |
| 1 | (invalid)        | — |
| 2 | FST m32fp        | 29.3c |
| 3 | FSTP m32fp       | 29.3b |
| 4 | FLDENV m14/m28   | 29.9 |
| 5 | FLDCW m16        | 29.9 |
| 6 | FSTENV/FNSTENV   | 29.9 |
| 7 | FSTCW/FNSTCW m16 | 29.9 |

| Register form (mod = 3) — full second byte | Op | Phase |
|---|---|---|
| C0-C7 | FLD ST(i)         | 29.3c |
| C8-CF | FXCH ST(i)        | 29.3c |
| D0    | FNOP              | 29.8 |
| E0    | FCHS              | 29.8 |
| E1    | FABS              | 29.8 |
| E4    | FTST              | 29.5 |
| E5    | FXAM              | 29.5 |
| E8    | FLD1              | 29.6 |
| E9    | FLDL2T            | 29.6 |
| EA    | FLDL2E            | 29.6 |
| EB    | FLDPI             | 29.6 |
| EC    | FLDLG2            | 29.6 |
| ED    | FLDLN2            | 29.6 |
| EE    | FLDZ              | 29.3b |
| F0-FF | Transcendentals (F2XM1/FYL2X/FPTAN/FPATAN/...) | 29.7 |

### DB — i32 ops + FNINIT + m80fp

| /reg | Memory form (mod ≠ 3) | Phase |
|---|---|---|
| 0 | FILD m32int     | 29.3d |
| 2 | FIST m32int     | 29.3d |
| 3 | FISTP m32int    | 29.3d |
| 5 | FLD m80fp       | 29.10 |
| 7 | FSTP m80fp      | 29.10 |

| Register form (mod = 3) — full second byte | Op | Phase |
|---|---|---|
| E2    | FNCLEX            | 29.9 |
| E3    | FNINIT / FINIT    | 29.3b |
| E4    | FNSETPM (287+, no-op on 8087) | — |

### DD — f64 data movement + restore/save + FFREE

| /reg | Memory form (mod ≠ 3) | Phase |
|---|---|---|
| 0 | FLD m64fp       | 29.3d |
| 2 | FST m64fp       | 29.3d |
| 3 | FSTP m64fp      | 29.3d |
| 4 | FRSTOR m94/m108 | 29.9 |
| 6 | FNSAVE m94/m108 | 29.9 |
| 7 | FNSTSW m16      | 29.5 |

| Register form (mod = 3) | Op | Phase |
|---|---|---|
| C0-C7 | FFREE ST(i)         | 29.8 |
| D0-D7 | FST ST(i)           | 29.3c |
| D8-DF | FSTP ST(i)          | 29.3c |
| E0-E7 | FUCOM ST(i)         | 29.5 |
| E8-EF | FUCOMP ST(i)        | 29.5 |

### DF — i16 / i64 / BCD ops + FNSTSW AX

| /reg | Memory form (mod ≠ 3) | Phase |
|---|---|---|
| 0 | FILD m16int     | 29.3d |
| 2 | FIST m16int     | 29.3d |
| 3 | FISTP m16int    | 29.3d |
| 4 | FBLD m80bcd     | — (deferred) |
| 5 | FILD m64int     | 29.3d |
| 6 | FBSTP m80bcd    | — (deferred) |
| 7 | FISTP m64int    | 29.3d |

| Register form (mod = 3) | Op | Phase |
|---|---|---|
| E0 | FNSTSW AX (the only x87 op that writes a CPU GPR directly) | 29.5 |

(DA / DC / DE follow similar patterns — i32 arithmetic, f64 arithmetic,
i16/popping-variant arithmetic respectively. Documented in MD as needed
during each sprint, not enumerated here.)

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
