# Pre-built DecodedInstruction cache (Phase 7 F.y) — **+137% GB JIT, +55–67% GBA**

> **Strategy**: every match in DecoderTable.Decode does
> `new DecodedInstruction(format, insDef, pattern)` — yet another
> per-instruction heap allocation. Switch to building each (format, insDef)
> instance up-front in the ctor, and have Decode return the cached instance.
>
> **Hypothesis**: after F.x, the remaining major alloc on the dispatcher
> side is Decode's record allocation. Killing it should keep pushing MIPS up.
>
> **Result**: **GB json-llvm +137% vs starting baseline (2.66 → 6.30 MIPS),
> GBA arm +55% (3.82 → 5.94), GBA thumb +67% (3.75 → 6.27), GB legacy
> unchanged**. Hypothesis strongly holds, and GBA finally pulled into
> real-time (1.4–1.5×).
>
> **Decision**: keep the change. GB JIT jumped from starting baseline
> 5.5× real-time to 12.9×; both GBA paths crossed 1.0× real-time from 0.9×.

---

## 1. Results (multi-run, min/avg/max)

| ROM                     | Backend     | runs | min  | **avg** | max  | F.x avg | starting baseline | **Δ vs baseline** |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|------------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 5.88 | **5.94**| 6.02 | 3.90    | 3.82              | **+55.5%**        |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 6.20 | **6.27**| 6.32 | 4.69    | 3.75              | **+67.2%**        |
| GB 09-loop100.gb        | legacy      | 3    | 32.19| **32.65**| 33.17 | 32.49  | 32.76             | **−0.3%** (noise) |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.18 | **6.30**| 6.40 | 4.83    | 2.66              | **+136.8%**       |

real-time × changes:
- GBA arm-loop100: 0.9× → **1.4×** (crossed real-time threshold)
- GBA thumb-loop100: 0.9× → **1.5×**
- GB json-llvm: 5.5× → **12.9×**

Both GBA paths **cross 1.0× real-time for the first time**. GB json-llvm
jumped from starting baseline 2.6× to 6.3× (with F.x's +82% then F.y +30% on top).

---

## 2. Changes

### `src/AprCpu.Core/Decoder/DecoderTable.cs`

Old hot path:

```csharp
public DecodedInstruction? Decode(uint instruction)
{
    foreach (var entry in _entries)
    {
        if ((instruction & entry.Format.Mask) != entry.Format.Match) continue;
        foreach (var insDef in entry.Format.Instructions)
        {
            if (insDef.Selector is null)
                return new DecodedInstruction(entry.Format, insDef, entry.Pattern!);
                // ↑ heap alloc per instruction, garbage collected
            // ... selector match check
            return new DecodedInstruction(entry.Format, insDef, entry.Pattern!);
                // ↑ same
        }
    }
    return null;
}
```

New version:

```csharp
public DecoderTable(InstructionSetSpec spec)
{
    // ... existing entries build ...
    foreach (var format in ...)
    {
        var decodedByDef = new DecodedInstruction[format.Instructions.Count];
        for (int i = 0; i < format.Instructions.Count; i++)
            decodedByDef[i] = new DecodedInstruction(format, format.Instructions[i], pattern!);
        _entries.Add(new Entry(format, pattern, decodedByDef));
    }
}

public DecodedInstruction? Decode(uint instruction)
{
    for (int e = 0; e < _entries.Count; e++)   // index loop, no IEnumerator alloc
    {
        var entry = _entries[e];
        if ((instruction & entry.Format.Mask) != entry.Format.Match) continue;
        var insDefs = entry.Format.Instructions;
        for (int i = 0; i < insDefs.Count; i++)
        {
            var insDef = insDefs[i];
            if (insDef.Selector is null)
                return entry.DecodedByDef[i];          // cached, zero alloc
            // ... selector match check
            return entry.DecodedByDef[i];
        }
    }
    return null;
}

private sealed record Entry(
    EncodingFormat Format,
    CompiledPattern? Pattern,
    DecodedInstruction[] DecodedByDef);
```

Two changes:
1. **Pre-built array**: at ctor time, pre-build each Format's DecodedInstruction
   for all InstructionDefs and store in entry.DecodedByDef
2. **Loop changed to indexed**: foreach over List<T> on .NET allocates an
   IEnumerator struct (small but per-instruction × millions); changing to
   indexed `for` is zero overhead

Per instruction saved:
- 1 × `new DecodedInstruction(...)` heap allocation (record class, ~24–32 bytes)
- 2 × IEnumerator allocation (outer + inner foreach)
- corresponding GC pressure

---

## 3. Cumulative results (since Phase 7 kicked off)

3 consecutive perf-improvement steps:

| Stage | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 MIPS / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| Phase 7 B.a (OptLevel O3) | 3.71 (perf-neutral) | 3.73 | 32.50 | 2.70 |
| Phase 7 F.x (id-keyed fn cache) | 3.90 (+2%, noisy) | 4.69 (+25%) | 32.49 | **4.83 (+82%)** |
| **Phase 7 F.y (pre-built decoded)** | **5.94 (+55%)** | **6.27 (+67%)** | 32.65 | **6.30 (+137%)** |

GB legacy unchanged is expected (path doesn't go through dispatcher / decoder changes).

On the important psychological "real-time × 1.0" threshold:
- GBA path was always hovering at 0.9× off the 5.7 baseline; after F.y,
  GBA arm 1.4×, GBA thumb 1.5× — **first time hitting real-time and beyond**
- GB JIT went from 5.5× to 12.9×, from "runnable but slow" to "fast"

---

## 4. Bottlenecks not yet hit

From the Phase 7 ROI table, F.x + F.y has eaten the easy targets on the
dispatcher side. Remaining:

| Strategy | Expected |
|---|---|
| **B.e** Tick / hook inlining | Mid — attacks scheduler.Tick / NotifyExecutingPc and other per-instr virtual calls |
| **E** Mem-bus fast path | Mid–high — attacks ROM/RAM region extern call overhead |
| **A** Block-level JIT | High — amortizes the entire dispatcher away |
| **C** Lazy flag | Mid — depends on how many flag ops in spec |
| **F.b** Direct dispatcher table (further) | Low — F.x/F.y already grabbed the bulk |
| **G** Native AOT | Mid — solves GB legacy run-to-run noise + some cold-start |

Recommend next **B.e** (small scope, low risk, targets GBA ARM noise) then
**E** (mem-bus is another big chunk). **A** block-JIT saved as the major
rewrite after B.e/E.

---

## 5. Change scope (verification)

```
src/AprCpu.Core/Decoder/DecoderTable.cs:
  - sealed record Entry(EncodingFormat, CompiledPattern?)
  + sealed record Entry(EncodingFormat, CompiledPattern?, DecodedInstruction[])
  + ctor adds pre-cached array per format
  - foreach + new DecodedInstruction in Decode hot path
  + indexed for + entry.DecodedByDef[i] return

Verification:
  - 345/345 unit tests pass
  - 9/9 Blargg cpu_instrs sub-tests pass on json-llvm (02–11)
  - 4-ROM bench: 3 (GBA arm) / 3 / 3 / 3 runs
```

---

## 6. Related documents

- [`MD_EN/performance/202605030002-jit-optimisation-starting-point.md`](/MD_EN/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- [`MD_EN/performance/202605030025-optlevel-0-to-3.md`](/MD_EN/performance/202605030025-optlevel-0-to-3.md) — Phase 7 B.a (perf-neutral)
- [`MD_EN/performance/202605030036-fnptr-cache-by-instruction-def.md`](/MD_EN/performance/202605030036-fnptr-cache-by-instruction-def.md) — Phase 7 F.x (+82%)
- [`MD_EN/design/03-roadmap.md`](/MD_EN/design/03-roadmap.md) Phase 7 — F.y marked done
- [`MD_EN/note/framework-emitter-architecture.md`](/MD_EN/note/framework-emitter-architecture.md) §8.4 — Phase 7 cumulative summary
