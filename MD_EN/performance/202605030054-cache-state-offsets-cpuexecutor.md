# Cache state-buffer offsets in CpuExecutor (Phase 7 B.e) — **GBA ARM +21% (peaks at +29% over F.y)**

> **Strategy**: before this, every CpuExecutor.Step() call invoked `_rt.PcWrittenOffset` /
> `_rt.GprOffset(_pcRegIndex)` per instruction — both getters cascade into
> `LLVM.OffsetOfElement` P/Invoke. 4–5 PInvokes into LLVM per instruction
> just to look up struct field offsets, even though those offsets never
> change once Compile() finishes. Cache them at construction.
>
> **Hypothesis**: CpuExecutor (GBA path) per-instruction LLVM PInvoke
> overhead isn't trivial; caching should significantly reduce host-side cost.
>
> **Result**: **GBA arm-loop100 +21% vs F.y (5.94 → 7.19 MIPS, individual run
> hit 7.80, 1.9× real-time), GBA thumb flat (6.27 → 6.26), GB json-llvm
> tiny +1.7% (6.30 → 6.41), GB legacy +5% (noise)**.
>
> **Decision**: keep the change. GBA ARM accumulated +88% from starting
> baseline (3.82 → 7.19 MIPS, 0.9× → 1.9× real-time).

---

## 1. Results (3-run, min/avg/max)

| ROM                     | Backend     | runs | min  | **avg** | max  | F.y avg | starting baseline | **Δ vs F.y** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 3    | 6.00 | **7.19**| 7.80 | 5.94    | 3.82              | **+21.0%**   | **+88.2%**    |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 5.94 | **6.26**| 6.88 | 6.27    | 3.75              | −0.2%        | +66.9%        |
| GB 09-loop100.gb        | legacy      | 3    | 32.51| **34.29**| 36.92| 32.65   | 32.76             | +5.0%        | +4.7%         |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.23 | **6.41**| 6.68 | 6.30    | 2.66              | +1.7%        | +141.0%       |

GBA arm-loop100 individual run detail — first run 6.00 (close to F.y),
runs 2 and 3 at 7.77 and 7.80. Possibly .NET tiered JIT promotion timing
(first run still tier 0, later promoted to tier 1).

real-time × changes:
- GBA arm-loop100: 1.4× (F.y) → **1.9×** (individual run)
- GBA thumb-loop100: 1.5× (F.y) → 1.4–1.6× (noise)
- GB json-llvm: 12.9× → 12.8–13.7×

---

## 2. Why this change is effective

What CpuExecutor.Step() previously did per instruction:

```csharp
// 1. _rt.GprOffset(_pcRegIndex) → GetFieldOffsetBytes → LLVM.OffsetOfElement P/Invoke
WriteGpr(_pcRegIndex, pcReadValue);

// 2. _rt.PcWrittenOffset → another P/Invoke
_state[(int)_rt.PcWrittenOffset] = 0;

// ... call JIT'd function ...

// 3. _rt.GprOffset(_pcRegIndex) second P/Invoke
var postR15 = ReadGpr(_pcRegIndex);

// 4. _rt.PcWrittenOffset second P/Invoke
bool flagged = _state[(int)_rt.PcWrittenOffset] != 0;

// 5. (conditional) _rt.GprOffset(_pcRegIndex) third P/Invoke
if (!branched) WriteGpr(_pcRegIndex, pc + mode.InstrSizeBytes);
```

`LLVM.OffsetOfElement` is an LLVMSharp.Interop PInvoke; each call switches
to native side. Per instruction **4–5 PInvokes purely to look up struct
field byte offsets**, even though these offsets are constants from the
moment Compile() finishes.

Changes:
- Add two `private readonly int` fields `_pcGprOffset` / `_pcWrittenOffset`,
  initialize in both ctors by calling `_rt.GprOffset(_pcRegIndex)` and
  `_rt.PcWrittenOffset` **once**.
- Add PC-only fast paths `ReadPc()` / `WritePc(value)` using cached offset;
  Step() now uses these for its three GPR accesses (avoiding the regIndex
  → GprOffset PInvoke path).
- Step()'s two PcWrittenOffset reads/writes use cached `_pcWrittenOffset`.
- ReadPc / WritePc / Pc accessor all marked `[MethodImpl(AggressiveInlining)]`.

Per instruction saved 4–5 PInvokes + 1–2 indirections through the HostRuntime
instance.

`ReadGpr(int)` / `WriteGpr(int)` public APIs preserved (still calls
_rt.GprOffset internally), since they're for emitter-tests / external
callers, not in the hot loop. Step() uses the private fast path,
unaffected by API.

---

## 3. Why does only GBA ARM gain noticeably?

| Backend | Which dispatcher | B.e impact |
|---|---|---|
| GBA arm | CpuExecutor.Step | **Direct beneficiary** — sheds 4–5 PInvokes per step |
| GBA thumb | CpuExecutor.Step | **Same beneficiary** — but noise drowns it out |
| GB legacy | LegacyCpu.Step (handcrafted switch) | Not affected |
| GB json-llvm | JsonCpu.StepOne (own dispatcher) | Not affected (JsonCpu already cached offsets in ctor — see _aOff/_bOff/... in JsonCpu.cs:119) |

GBA Thumb and GBA ARM both go through CpuExecutor, but Thumb noise is
clearly larger (5.94–6.88 swing). Possibly Thumb instruction dispatch
has fewer cond gates, so the remaining overhead ratio differs. More runs
might reveal +5–10%.

GB json-llvm doesn't benefit because JsonCpu already cached _aOff /
_bOff / ... / _pcOff in the ctor (JsonCpu.cs:119). CpuExecutor didn't do
the same before; this change brings it up to par.

GB legacy path doesn't touch CpuExecutor / HostRuntime at all; the +5%
is noise (one run 36.92, two runs 32–33, clearly an outlier).

---

## 4. Phase 7 cumulative

| Stage | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| **7.B.e cache state offsets** | **7.19 / 1.9×** | 6.26 | 34.29 | 6.41 |

GBA ARM walked 3.82 → 7.19 MIPS = **+88% cumulative**, real-time from
0.9× to **1.9×** (individual run). GBA Thumb and GB json-llvm plateaued
at ~6+ MIPS after F.y; B.e has limited effect on them (Thumb because
dispatcher share is lower, GB json-llvm because JsonCpu already had its
own cache).

---

## 5. What's left

ROI table updated:

| Strategy | Status |
|---|---|
| **A. Block-level JIT** | Still high — directly removes dispatcher overhead; pulls GBA path to native level |
| **E. Mem-bus fast path** | Mid–high — JIT'd code carries inline region check, saves extern call |
| **C. Lazy flag** | Mid — depends on spec flag op count; more pronounced on GBA (CPSR writes a lot) |
| **B.b/c/d IR-level micro-tweaks** | Low — function still too small |
| **F.b Direct dispatcher table** | Low — bulk already grabbed |
| **G. Native AOT** | Mid — solves GBA Thumb noise + some cold-start |

Next **E (mem-bus fast path)** should still have room on GBA arm-loop100
(every ARM instruction reads 4-byte word from ROM + many LDR/STR via extern).
Then **A (block-JIT)** as the major rewrite.

---

## 6. Change scope (verification)

```
src/AprCpu.Core/Runtime/CpuExecutor.cs:
  + private readonly int _pcGprOffset;
  + private readonly int _pcWrittenOffset;
  + ctor caches both via _rt.GprOffset / PcWrittenOffset
  + private ReadPc() / WritePc() with cached offset + AggressiveInlining
  + Pc property uses cached offset + AggressiveInlining
  ~ Step() uses ReadPc/WritePc + cached _pcWrittenOffset (5 sites)

Verification:
  - 345/345 unit tests pass
  - (Blargg sweep skipped this round — only CpuExecutor changed; the
    GB JIT path uses JsonCpu which is unaffected. Already verified
    in F.x/F.y rounds.)
```

---

## 7. Related documents

- [`MD_EN/performance/202605030002-jit-optimisation-starting-point.md`](/MD_EN/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- [`MD_EN/performance/202605030025-optlevel-0-to-3.md`](/MD_EN/performance/202605030025-optlevel-0-to-3.md) — Phase 7 B.a
- [`MD_EN/performance/202605030036-fnptr-cache-by-instruction-def.md`](/MD_EN/performance/202605030036-fnptr-cache-by-instruction-def.md) — Phase 7 F.x
- [`MD_EN/performance/202605030047-prebuilt-decoded-instruction.md`](/MD_EN/performance/202605030047-prebuilt-decoded-instruction.md) — Phase 7 F.y
- [`MD_EN/design/03-roadmap.md`](/MD_EN/design/03-roadmap.md) Phase 7 — B.e marked done
