# Scheduler.Tick + DeliverIrqIfPending AggressiveInlining（Phase 7 B.h）— **GBA +1-2% noise**

> **策略**：B.g 已給 GbaMemoryBus hot methods 加 AggressiveInlining，
> 但 GbaSystemRunner.RunCycles 的 hot loop 還有兩個 per-instruction
> calls 沒處理：`Scheduler.Tick(cyclesPerInstr)` 跟 `DeliverIrqIfPending()`。
> 都加 `[MethodImpl(AggressiveInlining)]`，讓 .NET JIT 把整條 RunCycles
> hot path 編譯成扁平的 inline 序列。
>
> **Hypothesis**：兩個 method 都有 fast path early-return（Tick 沒 cycles
> 或無 scanline 跨越；DeliverIrqIfPending 沒 IRQ pending），inline 能省
> 每 instr 兩次 call overhead。
>
> **結果**：GBA arm +1.0% (8.26 → 8.33), GBA thumb +1.8% (8.24 → 8.39)。
> 微幅 — 證實 dispatcher 端剩餘 cost 已很小。
>
> **決定**：保留改動。等 mem-heavy bench 出現後可能再放大。

---

## 1. 結果（3-run avg）

| ROM                     | Backend     | runs | min  | **avg** | max  | C.a avg | starting baseline | **Δ vs C.a** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 3    | 8.25 | **8.33**| 8.40 | 8.26    | 3.82              | +0.8%        | **+118.1%**   |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 8.33 | **8.39**| 8.43 | 8.24    | 3.75              | **+1.8%**    | **+123.7%**   |

GB legacy / GB json-llvm 沒重跑（B.h 改 GBA-only path）。

real-time × 持續穩定 2.0×。

---

## 2. 改動範圍

```
src/AprCpu.Core/Runtime/Gba/GbaScheduler.cs:
  + [MethodImpl(AggressiveInlining)] on public void Tick(int cycles)

src/AprCpu.Core/Runtime/Gba/GbaSystemRunner.cs:
  + [MethodImpl(AggressiveInlining)] on public void DeliverIrqIfPending()
```

兩個 method 都被 GbaSystemRunner.RunCycles 的 inner loop 每條指令 call
一次。fast path 都很短（early-return），inline 應該無腦正確。

---

## 3. 為什麼收益小

GbaSystemRunner.RunCycles 已經做過大量 dispatcher-side 優化：
- F.x/F.y → fn ptr cache + pre-built decoded
- B.e → cached state offsets
- B.f → permanent pin
- B.g → bus methods inline
- E.a → fetch fast path
- C.a → width-correct flag

到了 B.h 這層，Scheduler.Tick + DeliverIrqIfPending 加起來 per-instruction
的 cost 大概只剩 ~3-5 ns。inline 之後可能省 1-2 ns。

剩下的 hot path 大約：
- JIT'd fn body 執行: ~50-70 ns（指令本身的 IR 計算）
- Dispatcher overhead: ~30-40 ns（fn ptr lookup → indirect call）
- Bus/Tick/IRQ checks: ~5-10 ns（剩餘的 host-side 工作）

要再顯著進步必須打 dispatcher 那 30-40 ns（block-JIT 把它分攤掉）或
JIT'd fn body 那 50-70 ns（lazy flag、register allocation 改善）。

---

## 4. Phase 7 累計（10 步）

| 階段 | GBA arm | GBA thumb | GB legacy | GB json-llvm |
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

GBA 兩條 path 慢慢往 8.4 推進。GBA thumb 累計 +124% from baseline。

---

## 5. 相關文件

- [`MD/performance/202605030002-jit-optimisation-starting-point.md`](/MD/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- 前 9 個 Phase 7 perf notes
- [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md) Phase 7 — B.h 標 done
