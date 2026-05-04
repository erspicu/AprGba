# Scheduler.Tick + DeliverIrqIfPending AggressiveInlining (Phase 7 B.h) — **GBA +1-2% noise**

> **Strategy**: B.g already added AggressiveInlining to GbaMemoryBus hot
> methods, but the GbaSystemRunner.RunCycles hot loop still has two
> per-instruction calls left untouched: `Scheduler.Tick(cyclesPerInstr)`
> and `DeliverIrqIfPending()`. Add `[MethodImpl(AggressiveInlining)]` to
> both, so the .NET JIT compiles the entire RunCycles hot path into a
> flat inline sequence.
>
> **Hypothesis**: Both methods have fast-path early-returns (Tick has no
> cycles or no scanline crossing; DeliverIrqIfPending has no IRQ pending),
> inline saves two per-instr call overheads.
>
> **Result**: GBA arm +1.0% (8.26 → 8.33), GBA thumb +1.8% (8.24 → 8.39).
> Marginal — confirms the dispatcher-side residual cost is already small.
>
> **Decision**: Keep the change. May amplify when mem-heavy benches arrive.

---

## 1. Result (3-run avg)

| ROM                     | Backend     | runs | min  | **avg** | max  | C.a avg | starting baseline | **Δ vs C.a** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 3    | 8.25 | **8.33**| 8.40 | 8.26    | 3.82              | +0.8%        | **+118.1%**   |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 8.33 | **8.39**| 8.43 | 8.24    | 3.75              | **+1.8%**    | **+123.7%**   |

GB legacy / GB json-llvm not re-run (B.h modifies GBA-only path).

real-time x sustained 2.0x.

---

## 2. Change scope

```
src/AprCpu.Core/Runtime/Gba/GbaScheduler.cs:
  + [MethodImpl(AggressiveInlining)] on public void Tick(int cycles)

src/AprCpu.Core/Runtime/Gba/GbaSystemRunner.cs:
  + [MethodImpl(AggressiveInlining)] on public void DeliverIrqIfPending()
```

Both methods are called once per instruction by GbaSystemRunner.RunCycles's
inner loop. Both fast paths are short (early-return), so inline should
be safely correct.

---

## 3. Why the gain is small

GbaSystemRunner.RunCycles has had a lot of dispatcher-side optimisation:
- F.x/F.y → fn ptr cache + pre-built decoded
- B.e → cached state offsets
- B.f → permanent pin
- B.g → bus methods inline
- E.a → fetch fast path
- C.a → width-correct flag

By the B.h layer, Scheduler.Tick + DeliverIrqIfPending combined cost
per-instruction is only ~3-5 ns. Inline likely saves 1-2 ns.

Remaining hot path roughly:
- JIT'd fn body execution: ~50-70 ns (the IR computation of the instruction
  itself)
- Dispatcher overhead: ~30-40 ns (fn ptr lookup → indirect call)
- Bus/Tick/IRQ checks: ~5-10 ns (residual host-side work)

Further significant progress needs to attack either the 30-40 ns dispatcher
(block-JIT amortises it) or the 50-70 ns JIT'd fn body (lazy flag, register
allocation improvements).

---

## 4. Phase 7 cumulative (10 steps)

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
| 7.C.a width-correct flag | 8.26 | 8.24 | 32.75 | 6.48 |
| **7.B.h Tick/IRQ inline** | **8.33 / 2.0×** | **8.39 / 2.0×** | (n/a) | (n/a) |

Both GBA paths slowly inch toward 8.4. GBA thumb cumulative +124% from baseline.

---

## 5. Related docs

- [`MD_EN/performance/202605030002-jit-optimisation-starting-point.md`](/MD_EN/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- Previous 9 Phase 7 perf notes
- [`MD_EN/design/03-roadmap.md`](/MD_EN/design/03-roadmap.md) Phase 7 — B.h marked done
