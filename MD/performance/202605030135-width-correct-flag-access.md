# Width-correct status flag access（Phase 7 C.a）— **GB JIT +2.7%, infrastructure**

> **策略**：CpsrHelpers.SetStatusFlagAt / ReadStatusFlag 永遠用 i32 read/write
> 不管 status register 實際宽度。對 ARM CPSR（i32）正確；對 LR35902 F（i8）
> 是讀 4 bytes 跨進相鄰 SP 跟 PC 然後寫回（preserved 其他 3 bytes，所以
> 無 correctness 問題，但 alias 視圖讓 LLVM 不容易 combine 連續 flag updates
> 進單一 store）。改成依 status reg `WidthBits` 用 i8/i16/i32。
>
> **Hypothesis**：LR35902 一條 ALU 指令常做多次 flag write (Z + N + H + C)，
> 各自 read-modify-write F register。改 width-correct 之後 LLVM 應該能把
> 連續寫合併成單一 i8 store。
>
> **結果**：**GB json-llvm +2.7% (6.31 → 6.48 MIPS)**, GBA path 幾乎沒變
> (CPSR 已是 i32, 改動對 ARM 是 no-op)。Hypothesis 部分成立 — LLVM 確實
> 撈到一些優化，但比預期小。可能是因為 LR35902 多 flag updates 之間有其他
> ALU 操作（讀別的 register、做計算），打斷 store-to-store 的 fusion。
>
> **真 lazy flag**（cache last-ALU-op + 延後計算 flag bits）是更大架構改動，
> 不在本 commit scope。本 commit 是「拿掉 width 假設」的基礎建設。
>
> **決定**：保留改動。同時修正了潛在的「i32 access 越界讀寫相鄰 status reg
> 1-3 bytes」問題（雖然 read-then-write 模式保留了那些 bytes，但語義不正確）。

---

## 1. 結果（3-run avg）

| ROM                     | Backend     | runs | min  | **avg** | max  | E.b avg | starting baseline | **Δ vs E.b** | Δ vs baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|--------:|------------------:|-------------:|--------------:|
| GBA arm-loop100.gba     | json-llvm   | 3    | 8.20 | **8.26**| 8.35 | 8.30    | 3.82              | −0.5% noise  | **+116.2%**   |
| GBA thumb-loop100.gba   | json-llvm   | 3    | 8.12 | **8.24**| 8.32 | 8.21    | 3.75              | +0.4% noise  | **+119.7%**   |
| GB 09-loop100.gb        | legacy      | 3    | 32.31| **32.75**| 33.54| 33.67  | 32.76             | −2.7% noise  | unchanged     |
| GB 09-loop100.gb        | json-llvm   | 3    | 6.33 | **6.48**| 6.74 | 6.31    | 2.66              | **+2.7%**    | **+143.6%**   |

real-time × 持續 plateau：GBA 2.0×、GB JIT 13×。

---

## 2. 為什麼只 GB JIT 受惠

ARM CPSR 是 i32 — 改動前後 SetStatusFlagAt 都 emit i32 load/store，**no
diff**。GBA path 整個沒變。

LR35902 F 是 i8 — 改動前 SetStatusFlagAt emit `load i32, ptr / store i32,
ptr` (4 bytes 含 SP 跟 PC 一部分)。改動後 emit `load i8, ptr / store i8,
ptr` (1 byte，乾淨 alias)。

LR35902 一條典型 INC r 指令的 flag 寫入序列：
```
update_zero  → SetStatusFlag(F, Z, ...)  → load F, mask Z bit, OR z, store F
set_flag(N=0)→ SetStatusFlag(F, N, 0)    → load F, mask N bit, OR 0, store F
update_h_inc → SetStatusFlag(F, H, ...)  → load F, mask H bit, OR h, store F
```

改前每個 load/store 是 i32 (4 bytes)，LLVM 不確定是否跟相鄰 SP 寫衝突。
改後是 i8 (1 byte)，aliasing 明確。

但實測收益只 +2.7%，比預期小。猜測原因：
1. LLVM O3 已部分 combine，改動只剩餘 marginal gain
2. 連續 flag updates 之間還有 lr35902_read_r8 / lr35902_write_r8 等 GPR
   read/write 操作，打斷 fusion
3. trampoline 等其他 overhead 比 flag store cost 大很多

---

## 3. 正交收穫：fix latent semantic bug

`SetStatusFlagAt` 對 i8 status register 寫 i32 之前是「無法看出」的 bug：
- prev = load 4 bytes from F = [F | SP_lo | SP_hi | PC_lo]
- mask 操作只動 prev 的 bit `bitIndex`（在 F 那 byte 內）
- store back 4 bytes — SP_lo/SP_hi/PC_lo 寫回 (跟讀進來一樣)

所以實際上 SP/PC 的 byte 沒被清掉，**correctness ok**。但兩件事不對：
1. 違反 AArch64 / Windows alignment rules — 讀 4 bytes 從 1-byte aligned
   address，雖然 x86 容忍 unaligned，但理論不該這樣
2. LLVM 不能假設「F 跟 SP 不 alias」做 store-to-store optimization

C.a 修了這兩個。

---

## 4. 真 lazy flag 為什麼還沒做

完整 lazy flag computation（Gemini 建議 #2）做法：
1. 加 state slot: `last_alu_kind` (0=invalid/1=add/2=sub/...), `last_alu_a`, `last_alu_b`, `last_alu_result`
2. ALU emitter 不寫 flag bits，改寫這 4 個 slots
3. 加 op `derive_flag { which: N|Z|C|V, reg, flag }` — 從 last_alu_* 推 flag
4. Conditional execution / MRS / raise_exception 改用 derive_flag
5. 須處理 cache invalidation：MRS write to CPSR、exception entry 等

工作量：~100-300 lines + 要小心 jsmolka / Blargg / unit tests 全部驗
完。預期收益：對 ARM (cond exec 頻繁 + 多 flag 一起寫) 顯著 (+10-30%);
對 LR35902 中等 (+5-15%)。

但 Phase 7 dispatcher 端的 quick win 都吃完之後，繼續 grind 的邊際遞減
明顯（C.a 只 +2.7% on GB JIT）。**真 lazy flag 的 cost-benefit 取決於
是否要繼續 push perf** — 如果目標是 commercial ROM 流暢、demo「JSON-driven
framework + lazy flag = native level」這種進階論點，就值得做。

---

## 5. Phase 7 累計（9 步）

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
| **7.C.a width-correct flag** | **8.26** | **8.24** | 32.75 | **6.48 / 13.1×** |

GBA paths 在 8 MIPS / 2× real-time plateau 穩定，GB JIT 6.48 MIPS / 13× real-time。

最後 4 個 step 收益分別是 +2.5% / +0.6% / -0.5% / +2.7% — **明顯邊際遞減**。
進一步顯著進步需要架構級改動 (block-JIT 或 真 lazy flag)。

---

## 6. 改動範圍（驗證）

```
src/AprCpu.Core/IR/Emitters.cs CpsrHelpers:
  ~ ReadStatusFlag: load 改用 status reg actual width, 結果 zext to i32
  ~ SetStatusFlagAt: load+store 改用 status reg actual width;
    clearMask 改為 width-aware (allOnes ^ (1UL << bitIndex))
  + private static (Type, AllOnes) StatusTypeAndAllOnes(...)

驗證：
  - 345/345 unit tests pass
  - 9/9 Blargg cpu_instrs sub-tests on json-llvm (02–11)
```

---

## 7. 相關文件

- `MD/performance/202605030002-jit-optimisation-starting-point.md` — baseline
- 前 8 個 Phase 7 perf notes
- `MD/design/03-roadmap.md` Phase 7 — C.a 標 done
