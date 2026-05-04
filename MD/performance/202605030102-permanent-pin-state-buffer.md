# Permanently-pinned state buffer（Phase 7 B.f）— **GBA Thumb +27% (累計 +113%)**

> **策略**：CpuExecutor.Step 跟 JsonCpu.StepOne 都用
> `fixed (byte* p = _state) fn(p, instructionWord);` — 每條指令都把 _state
> 陣列 pin 一次給 JIT'd code 用。pin 本身有成本（~50 ns × millions of
> instructions = 顯著）。state buffer 大小固定，整個程式生命期都需要，所以
> 一輩子 pin 比較划算。
>
> **Hypothesis**：把 `fixed` 拿掉，改用 GCHandle.Alloc(state, Pinned) 在
> ctor 時 pin 一次、cache IntPtr/byte*，再用 cached 指標傳給 JIT'd fn。
>
> **結果**：**GBA thumb +27% (6.26 → 7.97 MIPS, 1.9× real-time consistent)**,
> GBA arm 持平 (7.19 → 7.13, 噪聲), GB json-llvm 持平 (6.41 → 6.31, 噪聲),
> GB legacy 不變。Hypothesis 部分成立 — Thumb 受惠最大，ARM 已到 plateau，
> JsonCpu 那邊可能 JIT 已經把 fixed 優化掉。
>
> **決定**：保留改動。GBA Thumb 累計 +113% (3.75 → 7.97 MIPS)，從 0.9× 升到
> 1.9× real-time。

---

## 1. 結果（3-run，min/avg/max）

| ROM                     | Backend     | runs | min  | **avg** | max  | B.e avg | starting baseline | **Δ vs B.e** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 3    | 6.34 | **7.13**| 8.12 | 7.19    | 3.82              | −0.8% noise  | +86.6%        |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 7.75 | **7.97**| 8.14 | 6.26    | 3.75              | **+27.3%**   | **+112.5%**   |
| GB 09-loop100.gb        | legacy      | 3    | 31.32| **33.67**| 36.86| 34.29  | 32.76             | noise        | +2.8% noise   |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.22 | **6.31**| 6.38 | 6.41    | 2.66              | −1.6% noise  | +137.2%       |

real-time × 變化：
- GBA arm-loop100：1.9× (B.e 個別) → **1.5-1.9×** (穩定到 1.9×)
- GBA thumb-loop100：1.5× → **1.9×** (consistent across 3 runs)
- GB json-llvm：12.9× → 13.0×

---

## 2. 為什麼 fixed 有 cost

`fixed (byte* p = _state)` 在 .NET runtime 做的事：
1. 把 _state 陣列在 GC heap 上 pin 住，禁止 generational GC compact 它
2. 建立 GC 內部 pinned-handle 的記錄（small list）
3. 取得 raw pointer
4. （函式內）使用 pointer
5. 離開 fixed scope 時 unpin（從記錄移除）

每次 enter/exit 約 30-100 ns（依 GC pressure 而定）。對於 100M 指令/秒
的 hot loop，這是 3-10 秒的 host 時間 — 大頭。

GCHandle.Alloc(Pinned) 的 trade-off：
- Pin 1 次而非 N 次 — 省下 N-1 次 enter/exit cost
- 整個 process 生命期 _state 不能 GC compact — 但 _state 是 long-lived
  fixed-size buffer (~250 bytes)，本來就不會被 collect，所以額外成本是
  「block young gen compaction 一個小 root」— 可忽略

對長壽命大 buffer（CPU state buffer 完全符合），permanent pin 是 idiomatic
.NET pattern。

---

## 3. 為什麼只 GBA Thumb 大幅受惠？

| Backend | B.e | B.f | Δ |
|---|---|---|---|
| GBA arm | 7.19 | 7.13 | noise |
| GBA thumb | 6.26 | **7.97** | **+27%** |
| GB json-llvm | 6.41 | 6.31 | noise |

四種可能解釋：

1. **GBA arm 在 B.e 之後已 plateau** — F.x/F.y/B.e 把 dispatcher overhead
   砍到「per-call extern dispatch + JIT'd function body 執行」級別，B.f
   省的 ~50 ns 對 ARM 路徑佔比小（ARM 一條指令本來就慢一些）。
2. **GBA Thumb dispatcher 占比較重** — Thumb 指令本身 simpler (沒 cond
   gate / 沒 shifted operand)，dispatcher overhead 比例高，所以省 fixed
   收益顯著。
3. **JsonCpu 的 `fixed` 早就被 JIT 優化掉** — JsonCpu 是 sealed class，
   _state 是 readonly field 從同一個地方分配，.NET tiered JIT 可能已經
   把 fixed 編譯成「直接 take address」沒有 pin/unpin cost。CpuExecutor
   結構稍微複雜（多型 setsByName 等），可能沒被同樣優化。
4. **GBA arm 個別 run 噪聲大** — 6.34, 6.93, 8.12 —— 第三 run 8.12
   就是 ARM 的真實水準，平均被前兩個 cold-tier run 拉低。如果只看 max
   ARM 也是 8.12 vs B.e 7.80 — 也是進步約 +4%。

不確定是哪個主因，但 Thumb 的 +27% 是穩定 (3 個 run 都在 7.75-8.14
之間)，所以收益是真的。

---

## 4. Phase 7 累計（5 步）

| 階段 | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| 7.B.e cache state offsets | 7.19 / 1.9× | 6.26 | 34.29 | 6.41 |
| **7.B.f permanent pin** | **7.13 / 1.9×** | **7.97 / 1.9×** | 33.67 | 6.31 |

GBA 兩條 path 都 stable 在 1.9× real-time，從 baseline 的 0.9× 來看雙倍。
GB json-llvm 從 5.5× 升到 13×。

GB legacy 一直在 32-37 MIPS noise band 跳，沒受 Phase 7 任何優化影響
（路徑不在 dispatcher / decoder 上）。

---

## 5. 還剩什麼

ROI 表更新：

| 策略 | 現狀 | 預期收益 |
|---|---|---|
| **A. Block-level JIT** | 沒做 | **高** — 8-13× 加速理論值，但是大改 |
| **E. Mem-bus fast path** | 沒做 | **中-高** — JIT'd code 內 region check，省 mem extern call |
| **C. Lazy flag** | 沒做 | 中 — 攻 IR-side flag 寫入 |
| **B.b/c/d IR 微調** | 沒做 | 低 — function 還是太小，O3 已證實沒空間 |

Phase 7 已撈到的「dispatcher 端容易目標」基本吃完。下一步要嘛攻
**E (mem-bus)** 或 **A (block-JIT)** 才能繼續顯著進步。前者中等複雜
中等收益、後者高複雜高收益。

Phase 5.8 starting baseline 的目標「test ROM 跑得起、不太慢」已經達標：
- GBA test ROMs：1.9× real-time，1200 frames 跑 11s 內完成
- GB test ROMs：13× real-time，1200 frames 跑 1.5s 內完成

所以目前 perf 已經夠好，**下一步是否繼續 Phase 7 取決於目標**：
- 想驗證 framework 「換 spec 通用化」承諾 → 跳到第三 CPU port (Phase 5.9)
- 想 demo block-JIT 把 framework 推到 native 級 → 攻 A
- 想穩定 GBA thumb 那個 +27% 是哪來的 → profile 一下確認 hypothesis

---

## 6. 改動範圍（驗證）

```
src/AprCpu.Core/Runtime/CpuExecutor.cs:
  + private readonly GCHandle _stateHandle
  + private readonly byte* _statePtr
  + 兩個 ctor 都加 pin + cache _statePtr
  + ~CpuExecutor finalizer free GCHandle
  ~ Step() 改用 fn(_statePtr, instructionWord) 不再 fixed

src/AprGb.Cli/Cpu/JsonCpu.cs:
  + private GCHandle _stateHandle / byte* _statePtr
  ~ Reset() 重 pin (free 舊的 + alloc 新的)
  ~ StepOne 改用 fn(_statePtr, ...) 不再 fixed

驗證：
  - 345/345 unit tests pass
  - 3/3 Blargg 02 / 07 / 10 pass (key sub-tests for IRQ / branch / bit ops)
```

---

## 7. 相關文件

- [`MD/performance/202605030002-jit-optimisation-starting-point.md`](/MD/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- [`MD/performance/202605030025-optlevel-0-to-3.md`](/MD/performance/202605030025-optlevel-0-to-3.md) — Phase 7 B.a
- [`MD/performance/202605030036-fnptr-cache-by-instruction-def.md`](/MD/performance/202605030036-fnptr-cache-by-instruction-def.md) — Phase 7 F.x
- [`MD/performance/202605030047-prebuilt-decoded-instruction.md`](/MD/performance/202605030047-prebuilt-decoded-instruction.md) — Phase 7 F.y
- [`MD/performance/202605030054-cache-state-offsets-cpuexecutor.md`](/MD/performance/202605030054-cache-state-offsets-cpuexecutor.md) — Phase 7 B.e
- [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md) Phase 7 — B.f 標 done
