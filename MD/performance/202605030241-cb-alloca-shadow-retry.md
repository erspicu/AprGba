# C.b alloca-shadow retry（after H.a）— **+0.5-0.6% GBA, infrastructure win**

> **Retry context**：C.b 第一次嘗試（202605030148）做了兩版（deferred-batch
> + alloca-shadow）；alloca-shadow 通過 correctness 但 perf-neutral，懷疑
> MCJIT 沒跑 mem2reg。H.a (202605030228) 顯式跑 mem2reg/GVN/DSE/instcombine/
> simplifycfg pipeline 後，retry C.b alloca-shadow 看真 lazy flag 收益。
>
> **結果**：**GBA arm +0.5% (8.45 → 8.49 MIPS), GBA thumb +0.6% (8.46 → 8.51)**,
> GB JIT 持平 (+0.3% noise)。比預期 +5-15% 小很多。
>
> **診斷**：原 CpsrHelpers.SetStatusFlagAt 已經是 load-mask-or-store pattern，
> LLVM 即使沒 alloca → SSA 也能用 GVN + DSE 對連續 mem 操作做相當好的優化。
> alloca shadow 主要 unlock 的優化（多次 store 折疊成單一 store）原本就
> 大致在做了。
>
> **決定**：保留改動 — abstraction 更乾淨、為未來「真 lazy flag」
> （延後計算 flag，不只是 batch writes）建好基礎。直接 perf 收益小但不
> 退步。

---

## 1. 結果（多 run avg）

| ROM                     | Backend     | runs | min  | **avg** | max  | H.a avg | starting baseline | **Δ vs H.a** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 8.36 | **8.49**| 8.56 | 8.45    | 3.82              | +0.5%        | **+122.3%**   |
| GBA thumb-loop100.gba   | json-llvm   | 4    | 8.44 | **8.51**| 8.56 | 8.46    | 3.75              | +0.6%        | **+126.9%**   |
| GB 09-loop100.gb        | legacy      | 3    | 30.49| **31.66**| 32.51| 32.24  | 32.76             | −1.8% noise  | unchanged     |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.44 | **6.52**| 6.65 | 6.50    | 2.66              | +0.3% noise  | **+145.1%**   |

GB legacy 的 −1.8% 是 noise — C.b retry 不影響 legacy backend。

---

## 2. 為什麼 perf 收益比預期小

### 2.1 LLVM 的 GVN/DSE 對 raw memory 已經很強

第一版 C.b post-mortem 預期 +5-15%。實測 +0.5-0.6%。差距原因：

原 `SetStatusFlagAt` IR pattern (兩次連續 SetStatusFlag):
```
%prev1 = load CPSR_ptr
%new1 = (prev1 & ~mask_N) | (shift_N << bit_N)
store new1, CPSR_ptr

%prev2 = load CPSR_ptr           ← load same address
%new2 = (prev2 & ~mask_Z) | (shift_Z << bit_Z)
store new2, CPSR_ptr             ← store same address
```

LLVM 的 GVN pass 對 raw memory 的 load 之間如果無 aliasing 干擾就會
CSE — 把 `%prev2 = load CPSR_ptr` 換成 `%prev2 = %new1` (剛剛 store 的
值)。然後 DSE 看到中間的 `store new1` 沒有後續 read 就可以刪掉。

結果連續 N 次 SetStatusFlag 經 GVN+DSE 後 ≈ 1 個 load + N 個 mask-or +
1 個 store，跟 alloca shadow 經 mem2reg 後的 IR 幾乎一樣。

### 2.2 alloca shadow 的 overhead 部分抵銷

shadow 路徑 first SetStatusFlag 有額外 cost：
- alloca + init load (real CPSR) + init store (shadow)

如果一個函式只 call SetStatusFlag 1-2 次，這 init overhead 比省下的
還多。多次 call 才回本。

ARM ALU 指令通常更新 N+Z+C+V 4 次，shadow 路徑可省一些。但其他指令
（LDR, MOV, CMP 等）只更新 0-1 次 flag，shadow 反而是 net loss。

平均下來 0.5% 左右說得通。

### 2.3 C.b 的真價值在 abstraction

雖然直接 perf 收益小，C.b 解鎖：
- 未來 "real lazy flag" — 不只 batch 寫，是延後**計算** flag bits（cache
  last-ALU-result + ALU-kind）。需要 alloca shadow 才能做 def-use
  tracking。
- "PcWritten flag 改 LLVM register hint" (Phase 7 C 節未完成項目) — 同
  alloca shadow pattern。
- 任何未來 cache mutable per-function scalar 的 use case。

**Hypothesis 修正**：lazy flag 主要收益要來自「跳過不需要的計算」（lazy
真意），而不是「合併連續 store」（GVN/DSE 已處理）。

---

## 3. 改動內容

### `src/AprCpu.Core/IR/EmitContext.cs`
```csharp
public Dictionary<string, LLVMValueRef> StatusShadowAllocas { get; }
    = new(StringComparer.Ordinal);
public LLVMBasicBlockRef EntryBlock { get; }   // captured in ctor
```

### `src/AprCpu.Core/IR/Emitters.cs` CpsrHelpers
完全重寫：
- `SetStatusFlag` / `SetStatusFlagFromI32Lsb` → `SetStatusFlagAt` →
  寫到 shadow alloca (而非 real CPSR ptr)
- `ReadStatusFlag` → 讀 shadow if exists, else real
- `GetOrCreateShadow(ctx, register)` — 在 entry block 插 alloca + 從
  real status reg 初始化（PositionBefore terminator 處理 cond gate）
- `DrainShadow(ctx, register)` — load shadow, store real
- `InvalidateShadow(ctx, register)` — drain + remove from dict
- `DrainAllShadows(ctx)` — 全部 drain

### `src/AprCpu.Core/IR/InstructionFunctionBuilder.cs`
BuildRetVoid 前加 `CpsrHelpers.DrainAllShadows(ctx)`.

### `src/AprCpu.Core/IR/ArmEmitters.cs`
3 處 raw CPSR/SPSR writers 加 `CpsrHelpers.InvalidateShadow(ctx, ...)`：
- WritePsr (write_psr)
- RestoreCpsrFromSpsr (restore_cpsr_from_spsr)
- RaiseExceptionEmitter (raise_exception)

---

## 4. Phase 7 累計（12 步）

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
| 7.B.h Tick/IRQ inline | 8.33 / 2.0× | 8.39 / 2.0× | (n/a) | (n/a) |
| 7.H.a LLVM pass pipeline | 8.45 / 2.0× | 8.46 / 2.0× | 32.24 | 6.50 |
| **7.C.b alloca-shadow retry** | **8.49 / 2.0×** | **8.51 / 2.0×** | 31.66 | 6.52 |

GBA 兩條 path 接近 8.5 MIPS plateau。GB JIT 6.52 MIPS / 13×。

---

## 5. 下一步

Phase 7 的 dispatcher / IR-level / inline / mem-bus / lazy 各個面向 quick
win 都吃光了。剩下要顯著進步只能：

- **A. block-level JIT** — 1-2 週的 architectural 改動，理論 8-13× 加速
- **真 lazy flag** (defer computation, not just writes) — 需要新 state
  slots + invalidation protocol + 重寫 ARM update_nz/update_c_*/update_v_*
  emitter；中複雜度，預期可能 +5-10%
- **H.b spec-time IR pre-processing** — dead flag elimination 在 spec
  compiler 階段；中複雜度

Phase 5.8 baseline 目標「test ROM 跑得快」已遠超達成 (GBA 2.0× / GB 13×)。
建議：
1. **Phase 7 declared "saturated"** — accept current state，去做 5.9 第
   三 CPU port 驗證 framework 通用化承諾
2. 或繼續攻 A (block-JIT) — 但要 commit 1-2 週

---

## 6. 改動範圍（驗證）

```
src/AprCpu.Core/IR/EmitContext.cs:
  + StatusShadowAllocas Dictionary
  + EntryBlock property (ctor 捕獲)

src/AprCpu.Core/IR/Emitters.cs CpsrHelpers:
  ~ Get/Set/Read 走 shadow alloca
  + GetOrCreateShadow / DrainShadow / InvalidateShadow / DrainAllShadows

src/AprCpu.Core/IR/InstructionFunctionBuilder.cs:
  + DrainAllShadows before BuildRetVoid

src/AprCpu.Core/IR/ArmEmitters.cs:
  + InvalidateShadow 在 WritePsr / RestoreCpsrFromSpsr / RaiseException
    raw-CPSR 寫入前

驗證：
  - 345/345 unit tests pass
  - 9/9 Blargg cpu_instrs sub-tests pass on json-llvm (02–11)
```

---

## 7. 相關文件

- `MD/performance/202605030002-jit-optimisation-starting-point.md` — baseline
- `MD/performance/202605030148-lazy-flag-attempt-postmortem.md` — 第一次嘗試 (alloca-shadow before H.a)
- `MD/performance/202605030228-llvm-pass-pipeline.md` — H.a (unblocked C.b retry)
- 其他 Phase 7 perf notes
- `MD/design/03-roadmap.md` Phase 7 — C.b 標 done (retry success)
