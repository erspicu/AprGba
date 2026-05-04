# JIT mem-bus trampoline fast path（Phase 7 E.b）— **noise on loop100, infrastructure for mem-heavy workloads**

> **策略**：MemoryBusBindings 的 Read8/Read16/Read32 trampolines 之前
> 都是 `_current!.ReadByte(addr)`（介面 dispatch + Locate + region
> switch）。加 typed cache `_currentGba` + ROM/IWRAM/EWRAM 三段 inline
> region check + direct array index 的 fast path。Reads 才動，Writes
> 不動（因為 IO/Palette/VRAM/OAM writes 都有 side effects）。
>
> **Hypothesis**：JIT'd code 對 LDR/STR 走這幾個 trampolines；inline
> region check 應該大幅縮短這條路徑。
>
> **結果**：**loop100 ROM 上邊際 (+0.6% noise)**。loop100 是 ALU-heavy
> stress test（100× 跑相同 ARM/Thumb test 邏輯），memory access 不算
> bottleneck。E.b 改動是 forward-looking — 對 mem-heavy workload
> (BIOS LLE / DMA-heavy test ROM / jsmolka armwrestler) 應該有顯著效果，
> 但 loop100 量不出來。
>
> **決定**：保留改動。沒退步，base 增加，未來 mem-heavy bench 才看得
> 到。也避免後續 trampoline-改動發生時 baseline 不一致。

---

## 1. 結果（loop100，4-run avg）

| ROM                     | Backend     | runs | min  | **avg** | max  | E.a avg | starting baseline | **Δ vs E.a** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 8.23 | **8.30**| 8.37 | 8.25    | 3.82              | +0.6% noise  | **+117.3%**   |
| GBA thumb-loop100.gba   | json-llvm   | 4    | 8.09 | **8.21**| 8.31 | 8.16    | 3.75              | +0.6% noise  | **+118.9%**   |

GB 路徑沒重跑（trampoline 改動只影響 GBA — _currentGba 為 null 時 fast
path 整個 short-circuit 掉）。

real-time × 持續 plateau 在 2.0× (GBA arm) / 1.9-2.0× (GBA thumb)。

---

## 2. 為什麼 loop100 沒看到顯著收益

loop100 stress test ROM 的特性：
- ROM body：100 次 iteration 跑相同邏輯
- 每 iteration：~600-800 個 ARM/Thumb 指令
- 大多數指令是 **ALU + register-to-register**（test 的核心）
- Memory access 類型：
  - Instruction fetch (per instr): 1 × ROM read  ← 但這條 E.a 已經 bypass
  - LDR Rd, =const literal pool reads: 偶爾，data 比例 < 5%
  - PUSH/POP for test framework: 每 iteration 幾次

所以 ALU instruction 的 hot loop body 沒受 E.b 影響。E.b 只影響少數
LDR/STR/LDM/STM 指令的 mem extern call。

要量到 E.b 真實影響需要 mem-heavy ROM：
- BIOS LLE: 大量 stack push/pop + 向量表 reads
- jsmolka armwrestler: LDM/STM/SWP 全測 → 大量 memory access
- DMA tests: bus 走 IO + memory 大量

未來補 mem-heavy bench 後 E.b 收益才會顯現。

---

## 3. 改動內容

### `src/AprCpu.Core/Runtime/MemoryBusBindings.cs`

加 typed cache + 3 個 helper + 改 3 個 read trampoline：

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

`Read16` / `Read32` 同 pattern（用 BinaryPrimitives.ReadUInt16/32LittleEndian）。

Writes 不改（IO writes have side effects: DMA trigger, IF clear-on-write
semantics, OAM dirty tracking — risky to bypass bus）。

---

## 4. 為什麼 fast path 用 `byte?` (nullable)

C# `byte?` (Nullable&lt;byte&gt;) 在 hot path 看起來是 boxed allocation 但
其實不是 — Nullable&lt;T&gt; 是 struct，沒有 heap alloc。檢查 `HasValue`
跟取值 `Value` 都是直接 field access。

JIT 對 `TryGbaFastReadByte(addr) ?? _current!.ReadByte(addr)` 會編出：
1. inline TryGbaFastReadByte
2. 如果 result.HasValue → 返回 result.Value
3. 否則 _current.ReadByte(addr)

跟手寫 `if (TryGbaFastReadByte(addr, out byte v)) return v; else return _current.ReadByte(addr);`
編出來幾乎一樣，但 source-side 更簡潔。

---

## 5. Phase 7 累計（8 步）

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
| **7.E.b mem trampoline fast path** | **8.30 / 2.0×** | **8.21 / 2.0×** | (n/a) | (n/a) |

GBA 兩條 path 雙雙穩定在 ~8.3 MIPS / 2.0× real-time。E.b 是「沒功勞但
有苦勞」的改動 — code 正確、減少 mem path 開銷，但 loop100 看不出來。

---

## 6. 還剩什麼

dispatcher 端 + bus 端 quick win 全做完。剩下：

- **A. Block-level JIT** — 高 ROI, 高複雜度（~1-2 週）
- **C. Lazy flag** — 中 ROI, 中複雜度（IR 層改動 + emitter 改動）
- 其他（cycle accounting batching 等） — 低 ROI

推薦：**先做 mem-heavy bench 驗證 E.b 效果**，再決定要不要繼續攻 A 或 C。

---

## 7. 改動範圍（驗證）

```
src/AprCpu.Core/Runtime/MemoryBusBindings.cs:
  + using AprCpu.Core.Runtime.Gba
  + private static GbaMemoryBus? _currentGba
  + Install() 設置 _currentGba; RestoreOnDispose 復原
  + 3 個 helper TryGbaFastRead{Byte,Halfword,Word}
  ~ Read8/Read16/Read32 trampolines 改用 helper ?? bus.Read* fallback

驗證：
  - 345/345 unit tests pass
```

---

## 8. 相關文件

- [`MD/performance/202605030002-jit-optimisation-starting-point.md`](/MD/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- 前 7 個 Phase 7 perf notes
- [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md) Phase 7 — E.b 標 done
