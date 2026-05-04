# Fn-pointer cache by InstructionDef ref（Phase 7 F.x）— **+82% GB JIT, +25% GBA Thumb**

> **策略**：dispatcher 把「JIT'd function pointer 的 cache」從
> string-keyed 改成 InstructionDef-reference-keyed。Hot path 不再
> 每條指令做 string interpolation + Dictionary string-hash；JsonCpu
> 那邊的舊路徑甚至每條指令還 allocate 一個全新 Dictionary 算 mnemonic
> ambiguity，更慘。改完只在 cache miss（每 opcode×selector 第一次）
> 才付那個成本。
>
> **Hypothesis**：dispatcher per-instruction overhead 是當前主要瓶頸
> （前一個 OptLevel 實驗證實 LLVM 路徑無法再壓縮），把每條指令的
> heap allocation 全部消掉應該會立即見效。
>
> **結果**：**GB json-llvm +82% (2.66 → 4.83 MIPS), GBA Thumb +25%
> (3.75 → 4.69 MIPS), GBA ARM +2% (噪音邊緣，3.82 → 3.90 6-run avg),
> GB legacy 不變 (32.76 → 32.49)**。Hypothesis 強烈成立。
>
> **決定**：保留改動。GB json-llvm 從 5.5× real-time 跳到 9.9×，
> 突破測試實用門檻。

---

## 1. 結果（多次 run，min/avg/max）

| ROM                     | Backend     | runs | min   | **avg**  | max   | baseline | **Δ** |
|-------------------------|-------------|------|------:|---------:|------:|---------:|-------:|
| GBA arm-loop100.gba     | json-llvm   | 6    | 3.61  | **3.90** | 4.77  | 3.82     | **+2.1%** |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 4.57  | **4.69** | 4.78  | 3.75     | **+25.1%** |
| GB 09-loop100.gb        | legacy      | 3    | 32.29 | **32.49**| 32.65 | 32.76    | **−0.8%** |
| GB 09-loop100.gb        | json-llvm   | 3    | 4.80  | **4.84** | 4.86  | 2.66     | **+81.6%** |

real-time × 同步變化：
- GBA arm-loop100：0.9× → 0.9× (個別 run 衝到 1.1×)
- GBA thumb-loop100：0.9× → **1.1×** (跨過實時門檻)
- GB json-llvm：5.5× → **9.9×**

GBA ARM 在 6 次 run 內波動 3.6-4.8 MIPS（~32% 範圍），明顯比 Thumb /
GB JIT 噪聲大。可能跟 ARM 端 IRQ / HALT / open-bus protect 路徑分支多
有關 — 留待後續策略再追。GB legacy 沒走 dispatcher 改動的 code path，
不變是預期。

---

## 2. 改動內容

兩個 dispatcher 都從 string-keyed cache 換成
`Dictionary<InstructionDef, IntPtr>` (with `ReferenceEqualityComparer.Instance`)。

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

更狠 — 原 hot path 每條指令還 allocate 一個 Dictionary 算 ambiguity：

before (hot path):
```csharp
var key = BuildFunctionKey(decoder.Name, decoded);  // ← 每次都 new Dictionary + 跑 foreach
var fnPtr = ResolveFunctionPointer(key);            // ← 然後 string-hash lookup
```

`BuildFunctionKey` 內部：
```csharp
var counts = new Dictionary<string, int>(StringComparer.Ordinal);  // ALLOCATION!!!
foreach (var i in fmt.Instructions)
    counts[i.Mnemonic] = counts.TryGetValue(i.Mnemonic, out var c) ? c + 1 : 1;
// ... 然後 string interpolation ...
return $"{setName}.{fmt.Name}{suffix}";
```

after：同 CpuExecutor，identity-keyed cache，string + ambiguity 計算只在
cache miss 跑。

每條指令省下：
- 1 × Dictionary allocation (16-24 bytes + array)
- 1 × foreach (over format.Instructions, typically 1-8 entries)
- 2-3 × string interpolation (FormattableString → string)
- 1 × string hash + lookup
- → 約 100-300 ns 的 GC + CPU work

GB 一條指令本來總成本 ~370 ns (2.66 MIPS = 376 ns/instr)，把這 200ns
左右省掉就接近 +80% (376/206 = 1.83×) — 跟實測 +82% 完全吻合。

---

## 3. 為什麼 GBA ARM 沒有等量收益？

GBA ARM 端的 `CpuExecutor.ResolveFunctionPointer` 改動 **沒有 GB 那邊
那麼大** — 它只省了 string interpolation + lookup，沒省 Dictionary
allocation（CpuExecutor 原本就用簡單 ambiguity 計數迴圈）。

然後 ARM 還有：
- BIOS open-bus protection 路徑（NotifyExecutingPc + NotifyInstructionFetch）
- IRQ delivery check 在 GbaSystemRunner.RunCycles 每 instr 1 次
- ARM banked register swap on mode change
- GBA scheduler 比 GB scheduler 多算 HBlank / VCount

合計這些固定 overhead 沒被 F.x 動到。前面那 6-run 平均 3.61-4.77 MIPS
範圍說明 ARM 還有別的 noise source，可能跟 .NET tiered JIT 在不同 run
裡 promote 不同 method 有關。後續可以靠 PGO + AggressiveInlining 收斂。

GBA Thumb 反而 +25% — 推測差異來自 Thumb 解碼比 ARM 簡單（10-bit table
vs 12-bit + cond gate），dispatcher 占比較高，所以 dispatcher 優化效
益放大。

---

## 4. 對下一步的暗示

把這次成果套回 Phase 7 ROI 排序：

| 策略 | F.x 之前預期 | F.x 之後新 status |
|---|---|---|
| **A. Block-level JIT** | 高 ROI | 仍然高（dispatcher 還沒消失，只是被 cache 加速；block-JIT 直接拔掉） |
| **B.e Tick / hook inlining** | 中 ROI | 依然中 — 每條 IRQ check / Tick(4) 還在 |
| **E. Mem-bus fast path** | 中-高 | 依然中-高，跟 dispatcher 獨立 |
| **F.y Direct dispatcher table** | 中 | **已經部分達成** (cache miss 從 hash + string concat 變 ref 比較)；要再進步只能拔掉 cache 直接 array index |
| **C. Lazy flag** | 中 | 依然中 |

**下一步可選**：
1. **B.e**：把 GbaSystemRunner.RunCycles 內的 `Bus.CpuHalted` 屬性
   getter / `Scheduler.Tick(4)` / `DeliverIrqIfPending()` 三者標
   AggressiveInlining；對 GBA path 應該能再撈幾個百分點，順便縮小
   ARM run-to-run variance
2. **E**：mem-bus fast path - JIT'd code 自帶 region check，省 extern call
3. **A**：block-JIT - 大改但 ROI 最高

我推薦下一個攻 **B.e**（短、低風險、針對 GBA ARM noise）然後 **E**
（中、針對 mem-bus extern overhead 是另一個獨立大頭）。**A** 留到最後
做大改。

---

## 5. 改動範圍

```
src/AprCpu.Core/Runtime/CpuExecutor.cs
  - Dictionary<string, IntPtr> _fnPtrCache (StringComparer.Ordinal)
  + Dictionary<InstructionDef, IntPtr> _fnPtrByDef (ReferenceEqualityComparer.Instance)
  + ResolveFunctionPointerSlow (cold path)

src/AprGb.Cli/Cpu/JsonCpu.cs
  - Dictionary<string, IntPtr> _fnPtrCache
  - BuildFunctionKey (跑 Dictionary alloc)
  - 舊 ResolveFunctionPointer(string key)
  + Dictionary<InstructionDef, IntPtr> _fnPtrByDef
  + ResolveFunctionPointer(string setName, DecodedInstruction decoded) — hot
  + ResolveFunctionPointerSlow — cold

驗證：
  - 345/345 unit tests pass
  - 9/9 Blargg cpu_instrs pass on json-llvm
```

---

## 6. Bench cmd（同 baseline）

跟前一份 perf note (`202605030025-optlevel-0-to-3.md`) §5 一樣，
不重複貼。

---

## 7. 相關文件

- [`MD/performance/202605030002-jit-optimisation-starting-point.md`](/MD/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- [`MD/performance/202605030025-optlevel-0-to-3.md`](/MD/performance/202605030025-optlevel-0-to-3.md) — 前一次（perf-neutral）
- [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md) Phase 7 — 改 F.x 標 done
