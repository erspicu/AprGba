# Fn-pointer cache by InstructionDef ref (Phase 7 F.x) — **+82% GB JIT, +25% GBA Thumb**

> **Strategy**: change the dispatcher's "JIT'd function pointer cache"
> from string-keyed to InstructionDef-reference-keyed. Hot path no longer
> does string interpolation + Dictionary string-hash per instruction;
> the legacy path on the JsonCpu side was even worse — it allocated a
> brand new Dictionary per instruction to compute mnemonic ambiguity. After
> the change those costs only run on cache miss (first time per opcode×selector).
>
> **Hypothesis**: dispatcher per-instruction overhead is the current main
> bottleneck (the previous OptLevel experiment confirmed the LLVM path
> can't be compressed further); eliminating all per-instruction heap
> allocations should pay off immediately.
>
> **Result**: **GB json-llvm +82% (2.66 → 4.83 MIPS), GBA Thumb +25%
> (3.75 → 4.69 MIPS), GBA ARM +2% (noise edge, 3.82 → 3.90 6-run avg),
> GB legacy unchanged (32.76 → 32.49)**. Hypothesis strongly holds.
>
> **Decision**: keep the change. GB json-llvm jumped from 5.5× real-time
> to 9.9×, crossing into practical-for-testing territory.

---

## 1. Results (multi-run, min/avg/max)

| ROM                     | Backend     | runs | min   | **avg**  | max   | baseline | **Δ** |
|-------------------------|-------------|------|------:|---------:|------:|---------:|-------:|
| GBA arm-loop100.gba     | json-llvm   | 6    | 3.61  | **3.90** | 4.77  | 3.82     | **+2.1%** |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 4.57  | **4.69** | 4.78  | 3.75     | **+25.1%** |
| GB 09-loop100.gb        | legacy      | 3    | 32.29 | **32.49**| 32.65 | 32.76    | **−0.8%** |
| GB 09-loop100.gb        | json-llvm   | 3    | 4.80  | **4.84** | 4.86  | 2.66     | **+81.6%** |

real-time × correlated changes:
- GBA arm-loop100: 0.9× → 0.9× (individual run hit 1.1×)
- GBA thumb-loop100: 0.9× → **1.1×** (crossed real-time threshold)
- GB json-llvm: 5.5× → **9.9×**

GBA ARM swings 3.6–4.8 MIPS over 6 runs (~32% range), clearly noisier
than Thumb / GB JIT. Could relate to the ARM-side IRQ / HALT / open-bus
protect path having more branching — leave for later strategies. GB legacy
doesn't go through the changed dispatcher code path; unchanged is expected.

---

## 2. Changes

Both dispatchers switched from string-keyed cache to
`Dictionary<InstructionDef, IntPtr>` (with `ReferenceEqualityComparer.Instance`).

### 2.1 `src/AprCpu.Core/Runtime/CpuExecutor.cs`

before:
```csharp
private readonly Dictionary<string, IntPtr> _fnPtrCache
    = new(StringComparer.Ordinal);

private IntPtr ResolveFunctionPointer(DecodedInstruction decoded, string setName)
{
    var format = decoded.Format;
    var def    = decoded.Instruction;
    var ambiguous = false;
    for (int i = 0, hits = 0; i < format.Instructions.Count; i++)
        if (format.Instructions[i].Mnemonic == def.Mnemonic && ++hits > 1)
        { ambiguous = true; break; }
    var disambig = ambiguous && def.Selector is not null
        ? $"{def.Mnemonic}_{def.Selector.Value}"
        : def.Mnemonic;
    var fnName = $"Execute_{setName}_{format.Name}_{disambig}";   // ← string alloc per call
    if (_fnPtrCache.TryGetValue(fnName, out var p)) return p;     // ← string-hash lookup
    p = _rt.GetFunctionPointer(fnName);
    _fnPtrCache[fnName] = p;
    return p;
}
```

after:
```csharp
private readonly Dictionary<InstructionDef, IntPtr> _fnPtrByDef
    = new(ReferenceEqualityComparer.Instance);

private IntPtr ResolveFunctionPointer(DecodedInstruction decoded, string setName)
{
    if (_fnPtrByDef.TryGetValue(decoded.Instruction, out var cached)) return cached;
    var p = ResolveFunctionPointerSlow(decoded, setName);
    _fnPtrByDef[decoded.Instruction] = p;
    return p;
}

// String building moved to ResolveFunctionPointerSlow — only runs on cache miss.
```

### 2.2 `src/AprGb.Cli/Cpu/JsonCpu.cs`

Even worse — the original hot path allocated a Dictionary per instruction
to compute ambiguity:

before (hot path):
```csharp
var key = BuildFunctionKey(decoder.Name, decoded);  // ← new Dictionary + foreach every call
var fnPtr = ResolveFunctionPointer(key);            // ← then string-hash lookup
```

`BuildFunctionKey` internals:
```csharp
var counts = new Dictionary<string, int>(StringComparer.Ordinal);  // ALLOCATION!!!
foreach (var i in fmt.Instructions)
    counts[i.Mnemonic] = counts.TryGetValue(i.Mnemonic, out var c) ? c + 1 : 1;
// ... then string interpolation ...
return $"{setName}.{fmt.Name}{suffix}";
```

after: same as CpuExecutor, identity-keyed cache, string + ambiguity
calculation only on cache miss.

Per instruction saved:
- 1 × Dictionary allocation (16–24 bytes + array)
- 1 × foreach (over format.Instructions, typically 1–8 entries)
- 2–3 × string interpolation (FormattableString → string)
- 1 × string hash + lookup
- → roughly 100–300 ns of GC + CPU work

A GB instruction originally cost ~370 ns total (2.66 MIPS = 376 ns/instr);
saving ~200 ns brings total to ~206 ns/instr (376/206 = 1.83× = +83%) —
matching measured +82% perfectly.

---

## 3. Why doesn't GBA ARM gain equally?

GBA ARM's `CpuExecutor.ResolveFunctionPointer` change **isn't as big as
the GB side** — it only saves string interpolation + lookup, not Dictionary
allocation (CpuExecutor originally used a simple ambiguity-counting loop).

ARM additionally has:
- BIOS open-bus protection path (NotifyExecutingPc + NotifyInstructionFetch)
- IRQ delivery check in GbaSystemRunner.RunCycles, 1× per instr
- ARM banked register swap on mode change
- GBA scheduler computing HBlank / VCount on top of GB scheduler

These fixed overheads total weren't touched by F.x. The 6-run avg range
3.61–4.77 MIPS shows ARM has another noise source — possibly .NET tiered
JIT promoting different methods across runs. Future PGO + AggressiveInlining
should converge it.

GBA Thumb instead +25% — the diff likely comes from Thumb decoding being
simpler than ARM (10-bit table vs 12-bit + cond gate), making dispatcher
share larger, so dispatcher optimisation gain is amplified.

---

## 4. Implication for next step

Updating the Phase 7 ROI ranking with this round's outcome:

| Strategy | Pre-F.x expected | Post-F.x new status |
|---|---|---|
| **A. Block-level JIT** | High ROI | Still high (dispatcher hasn't disappeared, just got cached; block-JIT removes it entirely) |
| **B.e Tick / hook inlining** | Mid ROI | Still mid — per-instr IRQ check / Tick(4) still there |
| **E. Mem-bus fast path** | Mid–high | Still mid–high, independent of dispatcher |
| **F.y Direct dispatcher table** | Mid | **Partially achieved already** (cache miss went from hash + string concat to ref equality); further gain requires dropping the cache for direct array index |
| **C. Lazy flag** | Mid | Still mid |

**Next step options**:
1. **B.e**: tag GbaSystemRunner.RunCycles' `Bus.CpuHalted` property
   getter / `Scheduler.Tick(4)` / `DeliverIrqIfPending()` with
   AggressiveInlining; should grab a few more % on GBA path and
   incidentally narrow ARM run-to-run variance
2. **E**: mem-bus fast path — JIT'd code carries region check inline,
   saves extern call
3. **A**: block-JIT — big change but highest ROI

I recommend **B.e** next (short, low-risk, targets GBA ARM noise) then
**E** (mid, targets mem-bus extern overhead which is another independent
big chunk). **A** saved for last as the major rewrite.

---

## 5. Change scope

```
src/AprCpu.Core/Runtime/CpuExecutor.cs
  - Dictionary<string, IntPtr> _fnPtrCache (StringComparer.Ordinal)
  + Dictionary<InstructionDef, IntPtr> _fnPtrByDef (ReferenceEqualityComparer.Instance)
  + ResolveFunctionPointerSlow (cold path)

src/AprGb.Cli/Cpu/JsonCpu.cs
  - Dictionary<string, IntPtr> _fnPtrCache
  - BuildFunctionKey (allocated Dictionary)
  - old ResolveFunctionPointer(string key)
  + Dictionary<InstructionDef, IntPtr> _fnPtrByDef
  + ResolveFunctionPointer(string setName, DecodedInstruction decoded) — hot
  + ResolveFunctionPointerSlow — cold

Verification:
  - 345/345 unit tests pass
  - 9/9 Blargg cpu_instrs pass on json-llvm
```

---

## 6. Bench cmd (same as baseline)

Same as previous perf note (`202605030025-optlevel-0-to-3.md`) §5,
not repeated here.

---

## 7. Related documents

- [`MD_EN/performance/202605030002-jit-optimisation-starting-point.md`](/MD_EN/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- [`MD_EN/performance/202605030025-optlevel-0-to-3.md`](/MD_EN/performance/202605030025-optlevel-0-to-3.md) — previous (perf-neutral)
- [`MD_EN/design/03-roadmap.md`](/MD_EN/design/03-roadmap.md) Phase 7 — F.x marked done
