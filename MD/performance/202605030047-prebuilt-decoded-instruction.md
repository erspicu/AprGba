# Pre-built DecodedInstruction cache（Phase 7 F.y）— **+137% GB JIT, +55-67% GBA**

> **策略**：DecoderTable.Decode 每條指令 match 時都做
> `new DecodedInstruction(format, insDef, pattern)` — 又一個 per-instruction
> heap allocation。改成 ctor 階段預先建好每個 (format, insDef) 對應的
> instance，Decode 直接回傳 cached instance。
>
> **Hypothesis**：F.x 之後 dispatcher 端剩下的主要 alloc 就是 Decode
> 的 record allocation。把它幹掉應該會繼續往上拉 MIPS。
>
> **結果**：**GB json-llvm +137% vs starting baseline (2.66 → 6.30 MIPS)，
> GBA arm +55% (3.82 → 5.94)，GBA thumb +67% (3.75 → 6.27)，GB legacy
> 不變**。Hypothesis 強烈成立，而且 GBA 端終於拉到實時 (1.4-1.5×)。
>
> **決定**：保留改動。GB JIT 從 starting baseline 的 5.5× real-time
> 跳到 12.9×，GBA 兩條 path 都從 0.9× 跨過 1.0× 進實時區。

---

## 1. 結果（多次 run，min/avg/max）

| ROM                     | Backend     | runs | min  | **avg** | max  | F.x avg | starting baseline | **Δ vs baseline** |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|------------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 5.88 | **5.94**| 6.02 | 3.90    | 3.82              | **+55.5%**        |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 6.20 | **6.27**| 6.32 | 4.69    | 3.75              | **+67.2%**        |
| GB 09-loop100.gb        | legacy      | 3    | 32.19| **32.65**| 33.17 | 32.49  | 32.76             | **−0.3%** (noise) |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.18 | **6.30**| 6.40 | 4.83    | 2.66              | **+136.8%**       |

real-time × 變化：
- GBA arm-loop100：0.9× → **1.4×** (跨過實時門檻)
- GBA thumb-loop100：0.9× → **1.5×**
- GB json-llvm：5.5× → **12.9×**

GBA 兩條 path **首次跨過 1.0× 實時**。GB json-llvm 從 starting baseline
2.6× 跳到 6.3×（加上 F.x 的 +82%、F.y 又再 +30%）。

---

## 2. 改動內容

### `src/AprCpu.Core/Decoder/DecoderTable.cs`

舊 hot path：

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

新版：

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

兩個改動：
1. **Pre-built array**：每個 Format 的所有 InstructionDef 都在 ctor 時
   pre-build 好對應的 DecodedInstruction，存進 entry.DecodedByDef
2. **Loop 改 indexed**：foreach over List<T> 在 .NET 上會 alloc 一個
   IEnumerator struct（小但 per-instruction × millions），改成 indexed
   `for` 就 zero overhead

每條指令省下：
- 1 × `new DecodedInstruction(...)` heap allocation (record class, ~24-32 bytes)
- 2 × IEnumerator allocation (outer + inner foreach)
- 對應的 GC pressure

---

## 3. 累計成果（Phase 7 開跑後）

3 連跳的 perf 改進：

| 階段 | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 MIPS / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| Phase 7 B.a (OptLevel O3) | 3.71 (perf-neutral) | 3.73 | 32.50 | 2.70 |
| Phase 7 F.x (id-keyed fn cache) | 3.90 (+2%, noisy) | 4.69 (+25%) | 32.49 | **4.83 (+82%)** |
| **Phase 7 F.y (pre-built decoded)** | **5.94 (+55%)** | **6.27 (+67%)** | 32.65 | **6.30 (+137%)** |

GB legacy 不變是預期 (path 不走 dispatcher / decoder 改動)。

對「real-time × 1.0」這條重要心理門檻：
- GBA path 從 5.7 baseline 永遠在 0.9× 邊緣徘徊，F.y 之後 GBA arm 1.4×、
  GBA thumb 1.5×，**首次達到 real-time 以上**
- GB JIT 從 5.5× 翻倍到 12.9×，從「能跑但慢」變「快速」

---

## 4. 還沒打到的瓶頸

從 Phase 7 ROI 表來看，F.x + F.y 把 dispatcher 端能打的容易目標基本
吃完了。剩下：

| 策略 | 預期 |
|---|---|
| **B.e** Tick / hook inlining | 中 — 攻 scheduler.Tick / NotifyExecutingPc 等 per-instr virtual call |
| **E** Mem-bus fast path | 中-高 — 攻 ROM/RAM region 的 extern call overhead |
| **A** Block-level JIT | 高 — 把整個 dispatcher amortise 掉 |
| **C** Lazy flag | 中 — 看 spec 裡 flag 寫多少 op |
| **F.b** Direct dispatcher table (進一步) | 低 — F.x/F.y 已撈大頭 |
| **G** Native AOT | 中 — 解 GB legacy run-to-run noise + 一些 cold-start |

下一個推薦 **B.e**（範圍小、低風險、攻 GBA ARM 的 noise）然後 **E**
（mem-bus 是另一個大頭）。**A** block-JIT 留到 B.e/E 完成後再做大改。

---

## 5. 改動範圍（驗證）

```
src/AprCpu.Core/Decoder/DecoderTable.cs:
  - sealed record Entry(EncodingFormat, CompiledPattern?)
  + sealed record Entry(EncodingFormat, CompiledPattern?, DecodedInstruction[])
  + ctor 多 build pre-cached array per format
  - foreach + new DecodedInstruction in Decode hot path
  + indexed for + entry.DecodedByDef[i] return

驗證：
  - 345/345 unit tests pass
  - 9/9 Blargg cpu_instrs sub-tests pass on json-llvm (02–11)
  - 4-ROM bench: 3 (GBA arm) / 3 / 3 / 3 runs
```

---

## 6. 相關文件

- [`MD/performance/202605030002-jit-optimisation-starting-point.md`](/MD/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- [`MD/performance/202605030025-optlevel-0-to-3.md`](/MD/performance/202605030025-optlevel-0-to-3.md) — Phase 7 B.a (perf-neutral)
- [`MD/performance/202605030036-fnptr-cache-by-instruction-def.md`](/MD/performance/202605030036-fnptr-cache-by-instruction-def.md) — Phase 7 F.x (+82%)
- [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md) Phase 7 — F.y 標 done
- [`MD/note/framework-emitter-architecture.md`](/MD/note/framework-emitter-architecture.md) §8.4 — Phase 7 累計成果摘要
