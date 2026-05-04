# Width-correct status flag access (Phase 7 C.a) — **GB JIT +2.7%, infrastructure**

> **Strategy**: CpsrHelpers.SetStatusFlagAt / ReadStatusFlag always used i32
> read/write regardless of the actual width of the status register. Correct
> for ARM CPSR (i32); for LR35902 F (i8) it reads 4 bytes spanning into
> adjacent SP and PC then writes them back (preserving the other 3 bytes,
> so no correctness issue, but the alias view makes it hard for LLVM to
> combine consecutive flag updates into a single store). Changed to use
> i8/i16/i32 according to the status reg `WidthBits`.
>
> **Hypothesis**: A single LR35902 ALU instruction often performs multiple
> flag writes (Z + N + H + C), each a read-modify-write on F. After
> width-correct, LLVM should be able to fuse consecutive writes into a
> single i8 store.
>
> **Result**: **GB json-llvm +2.7% (6.31 → 6.48 MIPS)**, GBA path almost
> unchanged (CPSR is already i32, the change is a no-op for ARM). The
> hypothesis is partially confirmed — LLVM did pick up some optimisation,
> but smaller than expected. Possibly because LR35902 multi-flag updates
> are interleaved with other ALU operations (reading other registers,
> doing computation) that break store-to-store fusion.
>
> **True lazy flag** (cache last-ALU-op + defer flag-bit computation) is
> a larger architectural change, out of scope for this commit. This commit
> is the foundational "drop the width assumption" piece.
>
> **Decision**: Keep the change. Also fixes a latent "i32 access reads/writes
> 1-3 bytes of adjacent status reg out of bounds" issue (the read-then-write
> pattern preserved those bytes, but the semantics are not correct).

---

## 1. Result (3-run avg)

| ROM                     | Backend     | runs | min  | **avg** | max  | E.b avg | starting baseline | **Δ vs E.b** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 3    | 8.20 | **8.26**| 8.35 | 8.30    | 3.82              | −0.5% noise  | **+116.2%**   |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 8.12 | **8.24**| 8.32 | 8.21    | 3.75              | +0.4% noise  | **+119.7%**   |
| GB 09-loop100.gb        | legacy      | 3    | 32.31| **32.75**| 33.54| 33.67  | 32.76             | −2.7% noise  | unchanged     |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.33 | **6.48**| 6.74 | 6.31    | 2.66              | **+2.7%**    | **+143.6%**   |

real-time x sustained plateau: GBA 2.0x, GB JIT 13x.

---

## 2. Why only GB JIT benefits

ARM CPSR is i32 — before/after the change SetStatusFlagAt emits i32
load/store, **no diff**. The GBA path is entirely unchanged.

LR35902 F is i8 — before the change SetStatusFlagAt emitted `load i32, ptr
/ store i32, ptr` (4 bytes, including SP and part of PC). After the change
it emits `load i8, ptr / store i8, ptr` (1 byte, clean alias).

A typical LR35902 INC r flag-write sequence:
```
update_zero  → SetStatusFlag(F, Z, ...)  → load F, mask Z bit, OR z, store F
set_flag(N=0)→ SetStatusFlag(F, N, 0)    → load F, mask N bit, OR 0, store F
update_h_inc → SetStatusFlag(F, H, ...)  → load F, mask H bit, OR h, store F
```

Before, every load/store was i32 (4 bytes), and LLVM was unsure whether it
conflicted with adjacent SP writes. After, it is i8 (1 byte) and aliasing
is unambiguous.

But measured gain is only +2.7%, smaller than expected. Suspected reasons:
1. LLVM O3 had partially combined already, the change leaves only a marginal
   gain
2. Between consecutive flag updates there are still lr35902_read_r8 /
   lr35902_write_r8 GPR read/write operations that break fusion
3. trampoline and other overheads cost much more than flag store cost

---

## 3. Orthogonal win: fix latent semantic bug

`SetStatusFlagAt` writing i32 to an i8 status register was a previously
"invisible" bug:
- prev = load 4 bytes from F = [F | SP_lo | SP_hi | PC_lo]
- mask operations only touch prev's bit `bitIndex` (within the F byte)
- store back 4 bytes — SP_lo/SP_hi/PC_lo written back (same as read in)

So in practice the SP/PC bytes were not zeroed, **correctness ok**. But two
things were wrong:
1. Violates AArch64 / Windows alignment rules — reading 4 bytes from a
   1-byte aligned address. Although x86 tolerates unaligned access, in
   theory it should not be done this way
2. LLVM cannot assume "F does not alias SP" for store-to-store optimisation

C.a fixes both.

---

## 4. Why true lazy flag is not done yet

Full lazy flag computation (Gemini suggestion #2) would do:
1. Add state slots: `last_alu_kind` (0=invalid/1=add/2=sub/...), `last_alu_a`, `last_alu_b`, `last_alu_result`
2. ALU emitters do not write flag bits, instead write these 4 slots
3. Add op `derive_flag { which: N|Z|C|V, reg, flag }` — derive flag from last_alu_*
4. Conditional execution / MRS / raise_exception switched to derive_flag
5. Need to handle cache invalidation: MRS write to CPSR, exception entry, etc.

Effort: ~100-300 lines + careful validation across jsmolka / Blargg / unit
tests. Expected gain: significant for ARM (cond exec is frequent + multiple
flags written together) (+10-30%); moderate for LR35902 (+5-15%).

But after the dispatcher-side quick wins of Phase 7 are exhausted, continuing
to grind shows clear diminishing returns (C.a is only +2.7% on GB JIT). **The
cost-benefit of true lazy flag depends on whether to keep pushing perf** —
if the goal is smooth commercial ROM playback or to demo "JSON-driven
framework + lazy flag = native level" advanced argument, then it's worth doing.

---

## 5. Phase 7 cumulative (9 steps)

| Stage | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| 7.B.e cache state offsets | 7.19 / 1.9× | 6.26 | 34.29 | 6.41 |
| 7.B.f permanent pin | 7.13 | 7.97 / 1.9× | 33.67 | 6.31 |
| 7.B.g AggressiveInlining bus | 8.05 / 2.0× | 8.10 | (n/a) | (n/a) |
| 7.E.a fetch fast path | 8.25 | 8.16 | (n/a) | (n/a) |
| 7.E.b mem trampoline fast | 8.30 | 8.21 | (n/a) | (n/a) |
| **7.C.a width-correct flag** | **8.26** | **8.24** | 32.75 | **6.48 / 13.1×** |

GBA paths plateau steadily at 8 MIPS / 2x real-time, GB JIT 6.48 MIPS / 13x real-time.

The last 4 steps gained +2.5% / +0.6% / -0.5% / +2.7% respectively —
**clear diminishing returns**. Further significant progress requires
architectural changes (block-JIT or true lazy flag).

---

## 6. Change scope (verification)

```
src/AprCpu.Core/IR/Emitters.cs CpsrHelpers:
  ~ ReadStatusFlag: load uses status reg actual width, result zext to i32
  ~ SetStatusFlagAt: load+store uses status reg actual width;
    clearMask is now width-aware (allOnes ^ (1UL << bitIndex))
  + private static (Type, AllOnes) StatusTypeAndAllOnes(...)

Verification:
  - 345/345 unit tests pass
  - 9/9 Blargg cpu_instrs sub-tests on json-llvm (02–11)
```

---

## 7. Related docs

- [`MD_EN/performance/202605030002-jit-optimisation-starting-point.md`](/MD_EN/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- Previous 8 Phase 7 perf notes
- [`MD_EN/design/03-roadmap.md`](/MD_EN/design/03-roadmap.md) Phase 7 — C.a marked done
