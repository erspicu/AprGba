# AggressiveInlining for GBA bus methods（Phase 7 B.g）— **GBA arm +13% (累計 +111%)**

> **策略**：在 GbaMemoryBus 的 hot methods (Locate, ReadWord, ReadHalfword,
> NotifyExecutingPc, HasPendingInterrupt) 加 `[MethodImpl(AggressiveInlining)]`
> hint。GbaMemoryBus 是 sealed class，.NET tiered JIT 已經能 devirtualize
> 透過 IMemoryBus 介面，但是否真的 inline 進 caller 取決於 method size。
> 加 hint 強迫 inliner 不管 size 都試著 inline。
>
> **Hypothesis**：CpuExecutor.Step 跟 GbaSystemRunner.RunCycles 每條指令多
> 次呼叫 _bus.* methods。如果這些被 inline 進 caller，多個小函式可以
> 一次最佳化（CSE、constant fold、register allocation 一起）。
>
> **結果**：**GBA arm +13% vs B.f (7.13 → 8.05 MIPS, 穩定 ~2× real-time)**,
> GBA thumb 持平 (7.97 → 8.10, 已 plateau)。額外好處：**GBA arm 噪聲
> 大幅降低**（B.f 6.34-8.12 → B.g 7.85-8.20）。
>
> **決定**：保留改動。GBA arm 累計 +111% (3.82 → 8.05 MIPS, 0.9× → 2.0×
> real-time)。

---

## 1. 結果（多 run，min/avg/max）

| ROM                     | Backend     | runs | min  | **avg** | max  | B.f avg | starting baseline | **Δ vs B.f** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 7.85 | **8.05**| 8.20 | 7.13    | 3.82              | **+12.9%**   | **+110.7%**   |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 8.03 | **8.10**| 8.13 | 7.97    | 3.75              | +1.6%        | **+116.0%**   |

GB legacy / GB json-llvm 沒重跑（B.g 只動 GbaMemoryBus，不影響 GB 路徑）。
B.f 結果繼續適用：GB legacy 33.67, GB json-llvm 6.31。

real-time × 變化：
- GBA arm-loop100：1.9× (B.f 個別) → **2.0×** (穩定，個別 run 可達 2.0×)
- GBA thumb-loop100：1.9× → **1.9×** (consistent)

額外觀察：**GBA arm 噪聲消失**了。B.f 的 4 個 run 在 6.34-8.12 之間（範圍
~30%），B.g 的 4 個 run 在 7.85-8.20 之間（範圍 ~5%）。原因可能是
.NET tiered JIT 之前要 promote hot methods 才能 inline，現在 hint 一下
tier 0 就直接 inline。

---

## 2. 改動範圍

`src/AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs` 加 5 個 attribute：

```
[MethodImpl(AggressiveInlining)] private static (Region, int) Locate(uint addr)
[MethodImpl(AggressiveInlining)] public void NotifyExecutingPc(uint pc) => ...
[MethodImpl(AggressiveInlining)] public ushort ReadHalfword(uint addr) => ...
[MethodImpl(AggressiveInlining)] public uint ReadWord(uint addr) => ...
[MethodImpl(AggressiveInlining)] public bool HasPendingInterrupt() => ...
```

ReadByte 沒加（不在 instruction-fetch hot path 上；只 LDR/STRB 用），
ReadIo* / write 系列也沒加（runtime 變化大、size 較大、容易反優化）。

NotifyInstructionFetch 沒加（內部有 BIOS 區段早 return，但 size 較大；
可能 inliner 自己拒絕 inline）。

---

## 3. 為什麼 GBA arm 比 thumb 多受惠

ARM 端 hot path 中：
- 1 × `_bus.NotifyExecutingPc(pc)` per Step — 一行 setter，本來該被 inline
- 1 × `_bus.ReadWord(pc)` per Step (instruction fetch) — switch 9 個 case，
  size 較大但加 hint 後 JIT 願意 inline
- 1 × `_bus.NotifyInstructionFetch(...)` — early-return for non-BIOS

Thumb 端走相同路徑但個別 run 已在 8.0+ MIPS plateau，可能是被別的
overhead bound (e.g. dispatcher、scheduler.Tick)。ARM 之前 noisy 然後升
+13%，比較像是「JIT 終於 inline 整個 fetch path」的 jump。

---

## 4. Phase 7 累計（6 步）

| 階段 | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| 7.B.e cache state offsets | 7.19 / 1.9× | 6.26 | 34.29 | 6.41 |
| 7.B.f permanent pin | 7.13 / 1.9× | 7.97 / 1.9× | 33.67 | 6.31 |
| **7.B.g AggressiveInlining bus** | **8.05 / 2.0×** | **8.10 / 1.9×** | (unchanged) | (unchanged) |

GBA 兩條 path 都 plateau 在 ~8 MIPS / 2× real-time。
GB json-llvm plateau 在 ~6.3 MIPS / 13× real-time（B.g 沒動 GB 路徑）。

---

## 5. 還剩什麼

dispatcher 端跟 inline 端的 quick win 都吃完了。要繼續顯著進步只剩：

| 策略 | ROI |
|---|---|
| **A. Block-level JIT** | 高 — 拔掉 dispatcher overhead；理論 8-13× 加速 |
| **E. Mem-bus fast path** | 中-高 — JIT'd code 內 region check + 直接 array index，省 mem extern call |
| **C. Lazy flag** | 中 — 看 spec 內 flag op 量；對 GBA 顯著（CPSR 寫多） |
| **G. Native AOT** | 中 — 解 GB legacy 跨 run 噪聲 + cold-start |

**想再有顯著進步必須做 E 或 A**。Phase 7 dispatcher / inline 階段正式
saturate。

對「test ROM 可用性」現況已經很好：
- GBA test ROMs：2.0× real-time，1200 frames 跑 11s 完成
- GB test ROMs：13× real-time，1200 frames 跑 1.5s 完成

繼續做 E/A 是「為了 commercial ROM 流暢度 / framework demo 級 perf」，
不是「test ROM 跑不動」這種剛需。

---

## 6. 改動範圍（驗證）

```
src/AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs:
  + [MethodImpl(AggressiveInlining)] × 5 (Locate, ReadWord, ReadHalfword,
                                            NotifyExecutingPc, HasPendingInterrupt)

驗證：
  - 345/345 unit tests pass
  - (Blargg 沒重跑 — B.g 只動 GbaMemoryBus，不影響 GB; 已在前述 commit
    驗證過 GB JIT 路徑仍綠。)
```

---

## 7. 相關文件

- [`MD/performance/202605030002-jit-optimisation-starting-point.md`](/MD/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- 前 5 個 Phase 7 perf notes (B.a / F.x / F.y / B.e / B.f)
- [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md) Phase 7 — B.g 標 done
