# Instruction-fetch fast path for GBA cart ROM（Phase 7 E.a）— **GBA arm +2.5%**

> **策略**：CpuExecutor.Step() 每條指令呼叫 `_bus.ReadWord(pc)` /
> `_bus.ReadHalfword(pc)` 取 instruction word。bus call 走介面 dispatch
> + Locate + region switch。對 GBA test ROM 來說 PC 99% 在 cart ROM
> (0x08000000+)；inline 一個快速分支直接 array index Rom byte[]，跳過
> bus 整套 dispatch。
>
> **Hypothesis**：B.g 已經把 bus.ReadWord inline 進 caller，但 switch +
> Locate 還在；直接 array index 應該再省一些 cycles。
>
> **結果**：**GBA arm +2.5% (8.05 → 8.25 MIPS)**, GBA thumb 持平 (噪聲)。
> 比預期小 — B.g AggressiveInlining 之後 bus.ReadWord 已經接近最佳，剩下
> 的 cost 主要在「fall-through 後的 region switch」，但即使跳過那個
> switch 收益也有限（~1-2 ns/instr）。
>
> **決定**：保留改動。GBA arm 累計 +116% (3.82 → 8.25 MIPS, 0.9× → 2.0×
> real-time consistent)。

---

## 1. 結果（4-run，min/avg/max）

| ROM                     | Backend     | runs | min  | **avg** | max  | B.g avg | starting baseline | **Δ vs B.g** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 4    | 7.98 | **8.25**| 8.40 | 8.05    | 3.82              | **+2.5%**    | **+116.0%**   |
| GBA thumb-loop100.gba   | json-llvm   | 4    | 7.96 | **8.16**| 8.43 | 8.10    | 3.75              | +0.7% noise  | **+117.6%**   |

GB 路徑沒重跑（E.a 只動 CpuExecutor，不影響 JsonCpu）。GB JIT 仍 plateau
~6.31 MIPS / 13× real-time。

real-time × 變化：
- GBA arm-loop100：2.0× consistent (B.g 2.0× 已達)
- GBA thumb-loop100：1.9-2.0× consistent

---

## 2. 改動內容

### `src/AprCpu.Core/Runtime/CpuExecutor.cs`

加 typed cache：

```csharp
private readonly GbaMemoryBus? _gbaBus;   // null when bus isn't GBA

ctor:
  _gbaBus = bus as GbaMemoryBus;
```

Step() fetch path 加 fast lane：

```csharp
uint instructionWord;
var gbaBus = _gbaBus;
if (gbaBus is not null
    && pc >= GbaMemoryMap.RomBase
    && pc < GbaMemoryMap.RomBase + (uint)gbaBus.Rom.Length)
{
    int off = (int)(pc - GbaMemoryMap.RomBase);
    var rom = gbaBus.Rom;
    instructionWord = mode.InstrSizeBytes switch
    {
        4 when off + 3 < rom.Length =>
            BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(off, 4)),
        2 when off + 1 < rom.Length =>
            BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(off, 2)),
        _ => 0u
    };
}
else
{
    // 原 bus.ReadWord/ReadHalfword path
}
```

額外成本（fast path）：2 個 comparison + 1 個 const subtraction + 1 個
ReadUInt32LittleEndian。**省下**: bus 的 interface dispatch +
GbaMemoryBus.ReadWord (內含 Locate 呼叫 + 9-case switch)。
即使 B.g 之後 ReadWord 都被 inline 進 Step，switch 本身還有 jump-table
lookup 跟 case body — 總共可省 ~3-5 ns/instr。

---

## 3. 為什麼收益比預期小

| Phase 7 step | bus 端優化 | 累計效果 |
|---|---|---|
| F.x | 改 fn ptr cache | dispatcher 內 string alloc 消失 |
| F.y | 改 decoded cache | dispatcher 內 record alloc 消失 |
| B.e | cache state offsets | LLVM PInvoke 消失 |
| B.f | permanent pin | per-step `fixed` 消失 |
| B.g | AggressiveInlining bus | bus.ReadWord inline 進 Step |
| **E.a** | **bypass bus.ReadWord 整個 region switch** | 邊際收益 — switch 已被 inline |

B.g 之後 bus call 跟 inline ReadWord 的差距已經很小，E.a 的 +2.5% 是
最後一點 squeeze。如果 B.g 沒做，E.a 應該能拿到 ~10-15%。

「對的優化順序」會讓後面的優化收益遞減 — 這次是 inline 比 bypass 順序
顛倒的話 E.a 會更亮眼。但結果都是好的，代表 fetch path 真的接近 hardware
limit (per-instruction host work ~120 ns at 8 MIPS)。

---

## 4. Phase 7 累計（7 步）

| 階段 | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| 7.B.e cache state offsets | 7.19 / 1.9× | 6.26 | 34.29 | 6.41 |
| 7.B.f permanent pin | 7.13 / 1.9× | 7.97 / 1.9× | 33.67 | 6.31 |
| 7.B.g AggressiveInlining bus | 8.05 / 2.0× | 8.10 / 1.9× | (n/a) | (n/a) |
| **7.E.a fetch fast path** | **8.25 / 2.0×** | 8.16 / 1.9-2.0× | (n/a) | (n/a) |

GBA 兩條 path 雙雙 plateau 在 **8 MIPS / 2× real-time**。GB JIT plateau
在 **6.3 MIPS / 13× real-time**。

---

## 5. E.b 計畫 — JIT-side data load/store fast path

E.a 只動 CpuExecutor 端的 instruction fetch。data load/store
(LDR/STR/LDM/STM 等) 走 JIT'd code 內的 `call memory_read_8(addr)` extern
trampoline。trampoline body 在 C# 中 `[UnmanagedCallersOnly]` static method
→ `_activeBus.ReadByte(addr)`。

下一步 E.b：
- 同樣 typed-cache `_activeBus` for fast access
- Trampoline 內 inline region check + direct array index
- 對 LDR/STR 多的 ROM 應該再有顯著 gain

E.b 留下一個 commit 處理。

---

## 6. 改動範圍（驗證）

```
src/AprCpu.Core/Runtime/CpuExecutor.cs:
  + using AprCpu.Core.Runtime.Gba
  + private readonly GbaMemoryBus? _gbaBus
  + 兩個 ctor 都加 _gbaBus = bus as GbaMemoryBus
  ~ Step() fetch 改 if-else fast/slow lane

驗證：
  - 345/345 unit tests pass
```

---

## 7. 相關文件

- [`MD/performance/202605030002-jit-optimisation-starting-point.md`](/MD/performance/202605030002-jit-optimisation-starting-point.md) — baseline
- 前 6 個 Phase 7 perf notes (B.a / F.x / F.y / B.e / B.f / B.g)
- [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md) Phase 7 — E.a 標 done
