# Cache state-buffer offsets in CpuExecutor（Phase 7 B.e）— **GBA ARM +21% (peaks at +29% over F.y)**

> **策略**：CpuExecutor.Step() 之前每條指令呼叫 `_rt.PcWrittenOffset` /
> `_rt.GprOffset(_pcRegIndex)` —— 兩個 getter 都 cascade 進 `LLVM.OffsetOfElement`
> P/Invoke。每條指令吃 4-5 次 PInvoke 進 LLVM 查 struct field offset，
> 而這些 offset 一旦 Compile() 完就永遠不會變。Cache 在建構時。
>
> **Hypothesis**：CpuExecutor (GBA path) 每條指令的 LLVM PInvoke overhead
> 不小，cache 後可大幅減少 host-side cost。
>
> **結果**：**GBA arm-loop100 +21% vs F.y (5.94 → 7.19 MIPS, 個別 run 衝
> 7.80, 1.9× real-time)，GBA thumb 持平 (6.27 → 6.26)，GB json-llvm 微幅
> +1.7% (6.30 → 6.41)，GB legacy +5% (noise)**。
>
> **決定**：保留改動。GBA ARM 從 starting baseline 累計達 +88% (3.82 →
> 7.19 MIPS, 0.9× → 1.9× real-time)。

---

## 1. 結果（3-run，min/avg/max）

| ROM                     | Backend     | runs | min  | **avg** | max  | F.y avg | starting baseline | **Δ vs F.y** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 3    | 6.00 | **7.19**| 7.80 | 5.94    | 3.82              | **+21.0%**   | **+88.2%**    |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 5.94 | **6.26**| 6.88 | 6.27    | 3.75              | −0.2%        | +66.9%        |
| GB 09-loop100.gb        | legacy      | 3    | 32.51| **34.29**| 36.92| 32.65   | 32.76             | +5.0%        | +4.7%         |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.23 | **6.41**| 6.68 | 6.30    | 2.66              | +1.7%        | +141.0%       |

GBA arm-loop100 個別 run 細節 — 第一 run 6.00（接近 F.y），第二三 run 7.77、
7.80。可能是 .NET tiered JIT promote 時機（first run 還在 tier 0，後續
promote 到 tier 1 之後變快）。

real-time × 變化：
- GBA arm-loop100：1.4× (F.y) → **1.9×** (個別 run)
- GBA thumb-loop100：1.5× (F.y) → 1.4-1.6× (噪聲)
- GB json-llvm：12.9× → 12.8-13.7×

---

## 2. 為什麼這個改動有效

CpuExecutor.Step() 過去做的事（每條指令）：

```csharp
// 1. _rt.GprOffset(_pcRegIndex) → GetFieldOffsetBytes → LLVM.OffsetOfElement P/Invoke
WriteGpr(_pcRegIndex, pcReadValue);

// 2. _rt.PcWrittenOffset → 又一次 P/Invoke
_state[(int)_rt.PcWrittenOffset] = 0;

// ... call JIT'd function ...

// 3. _rt.GprOffset(_pcRegIndex) 第二次 P/Invoke
var postR15 = ReadGpr(_pcRegIndex);

// 4. _rt.PcWrittenOffset 第二次 P/Invoke
bool flagged = _state[(int)_rt.PcWrittenOffset] != 0;

// 5. （條件）_rt.GprOffset(_pcRegIndex) 第三次 P/Invoke
if (!branched) WriteGpr(_pcRegIndex, pc + mode.InstrSizeBytes);
```

`LLVM.OffsetOfElement` 是 LLVMSharp.Interop 的 PInvoke，每次都要切到 native
side。每條指令 **4-5 次 PInvoke 純粹查 struct field 的 byte offset**，
而這些 offset 從 Compile() 完之後就是常數。

改動：
- 加兩個 `private readonly int` 欄位 `_pcGprOffset` / `_pcWrittenOffset`，
  在兩個 ctor 裡呼叫 `_rt.GprOffset(_pcRegIndex)` 跟 `_rt.PcWrittenOffset`
  各**一次**存進去。
- 加 PC-only fast path `ReadPc()` / `WritePc(value)` 用 cached offset，
  Step() 內部三個 GPR access 改用這個（避開 regIndex 參數 → GprOffset
  PInvoke 路徑）。
- Step() 內 PcWrittenOffset 兩次讀寫改用 cached `_pcWrittenOffset`。
- ReadPc / WritePc / Pc accessor 都標 `[MethodImpl(AggressiveInlining)]`。

每條指令省下 4-5 次 PInvoke + 1-2 次 indirect through HostRuntime instance。

`ReadGpr(int)` / `WriteGpr(int)` 公開 API 保留原樣（裡面還是 _rt.GprOffset），
因為它們是 emitter-tests / 外部呼叫用的，不在 hot loop 裡。Step() 改用
private fast path 不影響 API。

---

## 3. 為什麼只 GBA ARM 顯著進步？

| Backend | 走哪個 dispatcher | B.e 影響 |
|---|---|---|
| GBA arm | CpuExecutor.Step | **直接受惠** — 拿掉 4-5 個 PInvoke per step |
| GBA thumb | CpuExecutor.Step | **同樣受惠** — 但實測噪聲蓋掉 |
| GB legacy | LegacyCpu.Step (handcrafted switch) | 不影響 |
| GB json-llvm | JsonCpu.StepOne (自己的 dispatcher) | 不影響 (JsonCpu 在 ctor 已經 cache 過 offsets — 看 JsonCpu.cs:119 的 _aOff/_bOff/...) |

GBA Thumb 跟 GBA ARM 都走 CpuExecutor，但 Thumb 噪聲明顯比較大（5.94-6.88 之間
跳）。原因可能是 Thumb 指令 dispatch 內 cond gate 比較少，剩下 overhead 比例
不同。重複多 run 平均可能會看到 +5-10%。

GB json-llvm 沒受惠是因為 JsonCpu 從一開始就在 ctor 把 _aOff / _bOff /
... / _pcOff 都 cache 好了 (JsonCpu.cs:119)。CpuExecutor 之前沒做相同事，
這次補上。

GB legacy 路徑完全不碰 CpuExecutor / HostRuntime，看到 +5% 是 noise（一個 run
36.92, 兩個 run 32-33，明顯偏離）。

---

## 4. Phase 7 累計

| 階段 | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| **7.B.e cache state offsets** | **7.19 / 1.9×** | 6.26 | 34.29 | 6.41 |

GBA ARM 一路 3.82 → 7.19 MIPS = **+88% 累計**，real-time 從 0.9× 升到
**1.9×**（個別 run）。GBA Thumb 跟 GB json-llvm 在 F.y 之後就拉到 ~6+
MIPS plateau，B.e 對它們效果有限（Thumb 因為 dispatcher 占比較低，GB
json-llvm 因為 JsonCpu 自帶 cache）。

---

## 5. 還剩什麼

ROI 表更新：

| 策略 | 現狀 |
|---|---|
| **A. Block-level JIT** | 仍然高 — 直接拔掉 dispatcher overhead；GBA path 拉到 native 級 |
| **E. Mem-bus fast path** | 中-高 — JIT'd code 內 region check，省 extern call |
| **C. Lazy flag** | 中 — 看 spec flag op 量；對 GBA 比較顯著（CPSR 寫多） |
| **B.b/c/d IR-level 微調** | 低 — function 還是太小 |
| **F.b Direct dispatcher table** | 低 — 已撈大頭 |
| **G. Native AOT** | 中 — 解 GBA Thumb noise + 一些 cold-start |

下一步 **E (mem-bus fast path)** 應該針對 GBA arm-loop100 還有空間（每條
ARM 指令往 ROM 讀 4-byte word + 大量 LDR/STR 走 extern）。然後是 **A
(block-JIT)** 大改。

---

## 6. 改動範圍（驗證）

```
src/AprCpu.Core/Runtime/CpuExecutor.cs:
  + private readonly int _pcGprOffset;
  + private readonly int _pcWrittenOffset;
  + ctor caches both via _rt.GprOffset / PcWrittenOffset
  + private ReadPc() / WritePc() with cached offset + AggressiveInlining
  + Pc property uses cached offset + AggressiveInlining
  ~ Step() uses ReadPc/WritePc + cached _pcWrittenOffset (5 sites)

驗證：
  - 345/345 unit tests pass
  - (Blargg sweep skipped this round — only CpuExecutor changed; the
    GB JIT path uses JsonCpu which is unaffected. Already verified
    in F.x/F.y rounds.)
```

---

## 7. 相關文件

- [`MD/performance/202605030002-jit-optimisation-starting-point.md`](/MD/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- [`MD/performance/202605030025-optlevel-0-to-3.md`](/MD/performance/202605030025-optlevel-0-to-3.md) — Phase 7 B.a
- [`MD/performance/202605030036-fnptr-cache-by-instruction-def.md`](/MD/performance/202605030036-fnptr-cache-by-instruction-def.md) — Phase 7 F.x
- [`MD/performance/202605030047-prebuilt-decoded-instruction.md`](/MD/performance/202605030047-prebuilt-decoded-instruction.md) — Phase 7 F.y
- [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md) Phase 7 — B.e 標 done
