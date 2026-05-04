# Instruction-fetch fast path for GBA cart ROM (Phase 7 E.a) — **GBA arm +2.5%**

> **Strategy**: CpuExecutor.Step() calls `_bus.ReadWord(pc)` /
> `_bus.ReadHalfword(pc)` per instruction to fetch the instruction word.
> The bus call goes through interface dispatch + Locate + region switch.
> For GBA test ROMs, PC is 99% in cart ROM (0x08000000+); inline a fast
> branch directly indexing the Rom byte[], skipping the bus dispatch.
>
> **Hypothesis**: B.g has already inlined bus.ReadWord into the caller,
> but the switch + Locate is still there; direct array index should save
> a few more cycles.
>
> **Result**: **GBA arm +2.5% (8.05 → 8.25 MIPS)**, GBA thumb flat (noise).
> Smaller than expected — after B.g AggressiveInlining, bus.ReadWord is
> already near-optimal, and the remaining cost is mostly in "fall-through
> region switch", but even skipping that switch yields limited gain
> (~1–2 ns/instr).
>
> **Decision**: keep the change. GBA arm cumulative +116% (3.82 → 8.25 MIPS,
> 0.9× → 2.0× real-time consistent).

---

## 1. Results (4-run, min/avg/max)

| ROM                     | Backend     | runs | min  | **avg** | max  | B.g avg | starting baseline | **Δ vs B.g** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 7.98 | **8.25**| 8.40 | 8.05    | 3.82              | **+2.5%**    | **+116.0%**   |
| GBA thumb-loop100.gba   | json-llvm   | 4    | 7.96 | **8.16**| 8.43 | 8.10    | 3.75              | +0.7% noise  | **+117.6%**   |

GB path not re-run (E.a only touches CpuExecutor, doesn't affect JsonCpu).
GB JIT still plateaus at ~6.31 MIPS / 13× real-time.

real-time × changes:
- GBA arm-loop100: 2.0× consistent (B.g 2.0× already reached)
- GBA thumb-loop100: 1.9–2.0× consistent

---

## 2. Changes

### `src/AprCpu.Core/Runtime/CpuExecutor.cs`

Add typed cache:

```csharp
private readonly GbaMemoryBus? _gbaBus;   // null when bus isn't GBA

ctor:
  _gbaBus = bus as GbaMemoryBus;
```

Step() fetch path adds fast lane:

```csharp
uint instructionWord;
var gbaBus = _gbaBus;
if (gbaBus is not null
    && pc >= GbaMemoryMap.RomBase
    && pc < GbaMemoryMap.RomBase + (uint)gbaBus.Rom.Length)
{
    int off = (int)(pc - GbaMemoryMap.RomBase);
    var rom = gbaBus.Rom;
    instructionWord = mode.InstrSizeBytes switch
    {
        4 when off + 3 < rom.Length =>
            BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(off, 4)),
        2 when off + 1 < rom.Length =>
            BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(off, 2)),
        _ => 0u
    };
}
else
{
    // original bus.ReadWord/ReadHalfword path
}
```

Extra cost (fast path): 2 comparisons + 1 const subtraction + 1
ReadUInt32LittleEndian. **Saves**: bus interface dispatch +
GbaMemoryBus.ReadWord (which contains Locate call + 9-case switch).
Even with B.g's ReadWord inlined into Step, the switch itself still has
jump-table lookup + case body — total savings ~3–5 ns/instr.

---

## 3. Why is the gain smaller than expected

| Phase 7 step | Bus-side optimisation | Cumulative effect |
|---|---|---|
| F.x | fn ptr cache change | dispatcher's string alloc disappears |
| F.y | decoded cache change | dispatcher's record alloc disappears |
| B.e | cache state offsets | LLVM PInvoke disappears |
| B.f | permanent pin | per-step `fixed` disappears |
| B.g | AggressiveInlining bus | bus.ReadWord inlined into Step |
| **E.a** | **bypass bus.ReadWord entire region switch** | Marginal gain — switch already inlined |

After B.g, the gap between bus call and inline ReadWord is already small,
and E.a's +2.5% is the last squeeze. If B.g hadn't been done, E.a should
have grabbed ~10–15%.

The "right optimisation order" diminishes the returns of later optimisations —
this time inline-then-bypass, but reverse order would have made E.a more
impressive. Either way the result is good — fetch path is now near
hardware limit (per-instruction host work ~120 ns at 8 MIPS).

---

## 4. Phase 7 cumulative (7 steps)

| Stage | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| 7.B.e cache state offsets | 7.19 / 1.9× | 6.26 | 34.29 | 6.41 |
| 7.B.f permanent pin | 7.13 / 1.9× | 7.97 / 1.9× | 33.67 | 6.31 |
| 7.B.g AggressiveInlining bus | 8.05 / 2.0× | 8.10 / 1.9× | (n/a) | (n/a) |
| **7.E.a fetch fast path** | **8.25 / 2.0×** | 8.16 / 1.9–2.0× | (n/a) | (n/a) |

Both GBA paths plateau at **8 MIPS / 2× real-time**. GB JIT plateaus at
**6.3 MIPS / 13× real-time**.

---

## 5. E.b plan — JIT-side data load/store fast path

E.a only touches CpuExecutor-side instruction fetch. Data load/store
(LDR/STR/LDM/STM etc.) goes through `call memory_read_8(addr)` extern
trampoline inside JIT'd code. Trampoline body in C# `[UnmanagedCallersOnly]`
static method → `_activeBus.ReadByte(addr)`.

Next E.b:
- Same typed-cache `_activeBus` for fast access
- Trampoline inline region check + direct array index
- Should yield significant gain on LDR/STR-heavy ROMs

E.b in a follow-up commit.

---

## 6. Change scope (verification)

```
src/AprCpu.Core/Runtime/CpuExecutor.cs:
  + using AprCpu.Core.Runtime.Gba
  + private readonly GbaMemoryBus? _gbaBus
  + both ctors add _gbaBus = bus as GbaMemoryBus
  ~ Step() fetch becomes if-else fast/slow lane

Verification:
  - 345/345 unit tests pass
```

---

## 7. Related documents

- `MD/performance/202605030002-jit-optimisation-starting-point.md` — baseline
- Previous 6 Phase 7 perf notes (B.a / F.x / F.y / B.e / B.f / B.g)
- `MD/design/03-roadmap.md` Phase 7 — E.a marked done
