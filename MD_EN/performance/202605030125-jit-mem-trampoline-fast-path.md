# JIT mem-bus trampoline fast path (Phase 7 E.b) — **noise on loop100, infrastructure for mem-heavy workloads**

> **Strategy**: MemoryBusBindings' Read8/Read16/Read32 trampolines were
> previously `_current!.ReadByte(addr)` (interface dispatch + Locate +
> region switch). Add a typed cache `_currentGba` + ROM/IWRAM/EWRAM 3-region
> inline region check + direct array index fast path. Reads only, writes
> not touched (since IO/Palette/VRAM/OAM writes have side effects).
>
> **Hypothesis**: JIT'd code uses these trampolines for LDR/STR; inline
> region check should significantly shorten this path.
>
> **Result**: **marginal on loop100 ROM (+0.6% noise)**. loop100 is an
> ALU-heavy stress test (100× same ARM/Thumb test logic), memory access
> not the bottleneck. E.b is forward-looking — for mem-heavy workloads
> (BIOS LLE / DMA-heavy test ROMs / jsmolka armwrestler) it should have
> noticeable effect, but loop100 can't measure it.
>
> **Decision**: keep the change. No regression, base increased,
> mem-heavy bench will show it later. Also avoids baseline drift if
> later trampoline changes are made.

---

## 1. Results (loop100, 4-run avg)

| ROM                     | Backend     | runs | min  | **avg** | max  | E.a avg | starting baseline | **Δ vs E.a** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 8.23 | **8.30**| 8.37 | 8.25    | 3.82              | +0.6% noise  | **+117.3%**   |
| GBA thumb-loop100.gba   | json-llvm   | 4    | 8.09 | **8.21**| 8.31 | 8.16    | 3.75              | +0.6% noise  | **+118.9%**   |

GB path not re-run (trampoline change only affects GBA — when _currentGba
is null, fast path short-circuits entirely).

real-time × continues plateau at 2.0× (GBA arm) / 1.9–2.0× (GBA thumb).

---

## 2. Why loop100 doesn't show significant gain

loop100 stress test ROM characteristics:
- ROM body: 100 iterations of identical logic
- Each iteration: ~600–800 ARM/Thumb instructions
- Most instructions are **ALU + register-to-register** (the test's core)
- Memory access types:
  - Instruction fetch (per instr): 1 × ROM read  ← but E.a already bypassed this
  - LDR Rd, =const literal pool reads: occasional, data ratio < 5%
  - PUSH/POP for test framework: a few per iteration

So the ALU instruction hot loop body isn't affected by E.b. E.b only
touches the few LDR/STR/LDM/STM instructions' mem extern calls.

To measure E.b's real impact requires mem-heavy ROMs:
- BIOS LLE: large stack push/pop + vector table reads
- jsmolka armwrestler: full LDM/STM/SWP test → heavy memory access
- DMA tests: bus loaded with IO + memory traffic

Once mem-heavy benchmarks land, E.b's gain will be visible.

---

## 3. Changes

### `src/AprCpu.Core/Runtime/MemoryBusBindings.cs`

Add typed cache + 3 helpers + change 3 read trampolines:

```csharp
private static GbaMemoryBus? _currentGba;   // typed cache

// In Install(): _currentGba = bus as GbaMemoryBus;

[MethodImpl(AggressiveInlining)]
private static byte? TryGbaFastReadByte(uint addr)
{
    var bus = _currentGba;
    if (bus is null) return null;
    if (addr >= GbaMemoryMap.RomBase) {
        int off = (int)((addr - GbaMemoryMap.RomBase) & (RomMaxSize - 1));
        return off < bus.Rom.Length ? bus.Rom[off] : (byte)0;
    }
    if ((addr & 0xFF000000) == GbaMemoryMap.IwramBase) { ... bus.Iwram[off] ... }
    if ((addr & 0xFF000000) == GbaMemoryMap.EwramBase) { ... bus.Ewram[off] ... }
    return null;   // not in fast region; caller falls back
}

// trampoline:
private static byte Read8(uint addr) =>
    TryGbaFastReadByte(addr) ?? _current!.ReadByte(addr);
```

`Read16` / `Read32` follow the same pattern (using
BinaryPrimitives.ReadUInt16/32LittleEndian).

Writes not changed (IO writes have side effects: DMA trigger, IF
clear-on-write semantics, OAM dirty tracking — risky to bypass bus).

---

## 4. Why fast path uses `byte?` (nullable)

C# `byte?` (Nullable&lt;byte&gt;) on hot path looks like a boxed allocation
but isn't — Nullable&lt;T&gt; is a struct, no heap alloc. Checking
`HasValue` and reading `Value` are direct field accesses.

JIT compiles `TryGbaFastReadByte(addr) ?? _current!.ReadByte(addr)` into:
1. inline TryGbaFastReadByte
2. if result.HasValue → return result.Value
3. else _current.ReadByte(addr)

Almost identical to hand-written
`if (TryGbaFastReadByte(addr, out byte v)) return v; else return _current.ReadByte(addr);`,
but cleaner at the source level.

---

## 5. Phase 7 cumulative (8 steps)

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
| **7.E.b mem trampoline fast path** | **8.30 / 2.0×** | **8.21 / 2.0×** | (n/a) | (n/a) |

Both GBA paths stable at ~8.3 MIPS / 2.0× real-time. E.b is the
"no glory but groundwork" change — code is correct, mem path overhead is
reduced, but loop100 can't show it.

---

## 6. What's left

Dispatcher-side + bus-side quick wins all done. Remaining:

- **A. Block-level JIT** — High ROI, high complexity (~1–2 weeks)
- **C. Lazy flag** — Mid ROI, mid complexity (IR-layer changes + emitter changes)
- Others (cycle accounting batching etc.) — Low ROI

Recommend: **first add mem-heavy bench to verify E.b's effect**, then
decide whether to push on with A or C.

---

## 7. Change scope (verification)

```
src/AprCpu.Core/Runtime/MemoryBusBindings.cs:
  + using AprCpu.Core.Runtime.Gba
  + private static GbaMemoryBus? _currentGba
  + Install() sets _currentGba; RestoreOnDispose restores
  + 3 helpers TryGbaFastRead{Byte,Halfword,Word}
  ~ Read8/Read16/Read32 trampolines use helper ?? bus.Read* fallback

Verification:
  - 345/345 unit tests pass
```

---

## 8. Related documents

- `MD/performance/202605030002-jit-optimisation-starting-point.md` — baseline
- Previous 7 Phase 7 perf notes
- `MD/design/03-roadmap.md` Phase 7 — E.b marked done
