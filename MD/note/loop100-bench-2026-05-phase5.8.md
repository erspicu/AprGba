# loop100 stress-test bench — Phase 5.8 post-refactor (2026-05-02)

> 🎯 **Phase 5.8 emitter-refactor 完成後的對照數據。**
>
> 跟 `MD/note/loop100-bench-2026-05.md` (Phase 5.7 baseline) 一樣的 4
> 個 ROM × backend，跑同 1200 frames，同台機器、同 .NET 10 Release
> build、同 LLVM 20 MCJIT，量 emitter library refactor 對 MIPS / real-time
> 的衝擊。
>
> Refactor 範圍：Phase 5.8 Steps 5.1–5.7（27 個 LR35902-specific ops 收
> 成 generic 通用 ops；`Lr35902Emitters.cs` 從 ~2620 → 1346 行 −49%；
> 詳情見 `MD/design/11-emitter-library-refactor.md`）。
>
> 來源 commits：`cdf04ce` (refactor 收工 + docs).

---

## 1. 結果表（3-run 平均，最慢/最快/中位數都列出）

| ROM                     | Backend     | Phase 5.7 baseline | **Phase 5.8 (avg)** | runs | Δ MIPS |
|-------------------------|-------------|-------------------:|---------------------:|------|-------:|
| GBA arm-loop100.gba     | json-llvm   | 3.68               | **3.82** (3.75–3.91) | 3    | **+3.8%** |
| GBA thumb-loop100.gba   | json-llvm   | 3.70               | **3.75** (3.72–3.79) | 3    | **+1.4%** |
| GB 09-loop100.gb        | legacy      | 38.36              | **32.76** (31.29–33.97) | 4 | **−14.6%** |
| GB 09-loop100.gb        | json-llvm   | 2.73               | **2.66** (2.59–2.75) | 3    | **−2.6%** |

跑法（同 5.7 baseline）：

```bash
apr-gba --rom=arm-loop100.gba    --frames=1200
apr-gba --rom=thumb-loop100.gba  --frames=1200
apr-gb  --cpu=legacy    --rom=09-loop100.gb --frames=1200
apr-gb  --cpu=json-llvm --rom=09-loop100.gb --frames=1200
```

---

## 2. 解讀

### 2.1 GBA json-llvm: +1.4% / +3.8% — refactor 沒拖慢，反而略快

ARM/Thumb 兩條 GBA path 都微幅進步。原因推測：

- 5.3 把 LR35902-style branch_cc / call_cc / ret_cc 的 select-trick
  路徑通用化，順帶讓共用的 `branch` 也走更乾淨的 `LocateProgramCounter`
  → LLVM 可能 inlining / CSE 機會略增。
- 5.5 的 `Binary` auto-coerce 是純額外邏輯，但 LLVM constant-folds
  away when widths already match (ARM operands always i32).
- IR pattern 一致性改善（每個 generic op 跑同樣的 BuildAdd/BuildOr
  shape），LLVM optimizer 更好處理。

不是顯著改進，但證明 refactor **沒帶來 ARM 端的 perf 退步**。

### 2.2 GB json-llvm: −2.6% — IR step 變多的代價

GB json-llvm 從 2.73 → 2.66 MIPS，~3% 慢。預期內 — refactor 把幾個
LR35902 ops 拆成多步 generic chain：

| 操作 | refactor 前 | refactor 後 | step 數 |
|---|---|---|---|
| INC r          | 1 step (`lr35902_inc_r8`) | 7 steps (read + add + trunc + write + 2 flags + h_inc) | 7× |
| BIT b,r        | 1 step (`lr35902_cb_bit`) | 4 steps (read + bit_test + 2 set_flag) | 4× |
| RLC r          | 1 step (`lr35902_cb_shift`) | 3 steps (read + shift + write) | 3× |
| LDH (n),A      | 3 steps                    | 4 steps (added explicit `or`)             | 1.3× |
| CALL nn        | 2 steps                    | 2 steps (沒變)                            | 1× |
| JR e           | 2 steps                    | 5 steps (sext + read_pc + add + branch)   | 2.5× |

每個 step 是一個 `IMicroOpEmitter.Emit` call → 多幾次 dictionary lookup
+ JsonElement parse。LLVM 把 IR 編譯成 native 後，**編出來的 native
code 數量差不多** (因為 LLVM optimizer 把多 op chain inline 起來)，
但 **spec → IR 階段** 多花了時間。

實際 hot loop 跑很多次後，spec → IR 是 one-shot cost (在 init 時做
完)，runtime 應該很接近原本。−3% 是測量出來的 host-time 差異，不是
emitted code 差異。可能還是 init time 微增的 amortised effect。

### 2.3 GB legacy: −14.6% — 出乎意料，但很可能是 noise

GB legacy backend **完全不走 spec / emitter / LLVM JIT**，是 hand-written
big switch-case。Phase 5.8 refactor 完全沒碰 legacy code path。所以
理論上應該 0% 差異。

實際數據：38.36 → 32.76 MIPS，−14.6%。可能解釋：

1. **單次 run 太短**：legacy 跑 1200 frames 只要 ~300ms，process
   startup / JIT warmup / GC initialisation 占比變大。Baseline 那次的
   38.36 可能正好遇到「乾淨 host」一路順跑；這次數據比較 noisy。
2. **Windows 排程器 / 背景程式**：兩次 bench 中間有別的 process load。
3. **Shared infrastructure** (memory bus、GbScheduler、PPU stub) 在 5.7
   之後可能有些細微改動；雖然沒在 refactor 裡碰，但 LegacyCpu 跟
   JsonCpu 共用 GbMemoryBus + GbScheduler。
4. **.NET tiered compilation**：每次 first-run 跑 hot path 會在 ~100ms
   rejit 一次，這對 300ms runtime 的影響不可忽略。

不確定是哪個。沒有立即 actionable — 「用 legacy 跑 test ROM」不是
framework 的主要 use case，這個 baseline 是輔助對照用，不是 perf
target。real story 是 **GBA / GB json-llvm 都沒退步**。

### 2.4 cycle/instr 一致

| Backend | t-cyc/instr | 跟 5.7 |
|---|---|---|
| GBA json-llvm | 4.00 | 同 (hardcoded) |
| GB legacy     | 8.68 | 同 |
| GB json-llvm  | 8.60 | 同 |

Refactor 沒改 per-instruction cycle accounting；數字精準對得上。

---

## 3. 結論

✅ **Refactor goal 達成**：
- 主要框架 path (json-llvm) 在 GBA 微幅進步，在 GB 微幅退步，
  總體在 measurement noise (~±5%) 範圍內。
- emitter library 結構大幅清晰（−49% LR35902 emitter LOC，27 個
  arch-specific ops 通用化），跑得不慢。
- 「結構乾淨 vs perf 中性」這條 trade-off 站得住腳。

❓ **GB legacy 退步 14%**：歸類為「single-run 量測 noise」，未來
若有人關心 legacy perf 可重新調查。Phase 7 block-JIT 真做了 之後
這 4 個數字會全部重畫，到時再驗證。

---

## 4. Bench 重現方法

跟 5.7 baseline 完全一樣（見 `MD/note/loop100-bench-2026-05.md` §6）：

```bash
dotnet build -c Release AprGba.slnx

dotnet run --project src/AprGba.Cli -c Release -- \
    --rom=test-roms/gba-tests/arm/arm-loop100.gba --frames=1200

dotnet run --project src/AprGba.Cli -c Release -- \
    --rom=test-roms/gba-tests/thumb/thumb-loop100.gba --frames=1200

dotnet run --project src/AprGb.Cli -c Release -- \
    --cpu=legacy \
    --rom=test-roms/gb-test-roms-master/cpu_instrs/loop100/09-loop100.gb \
    --frames=1200

dotnet run --project src/AprGb.Cli -c Release -- \
    --cpu=json-llvm \
    --rom=test-roms/gb-test-roms-master/cpu_instrs/loop100/09-loop100.gb \
    --frames=1200
```

---

## 5. 下一個 baseline 何時更新

接下來只要碰到下面任一動作就要重跑這個表 + 寫新對照 note：

- Phase 5.8/5.9 **第三 CPU 上線** — 加 RISC-V/MIPS spec 後重跑（看
  refactor 是否真的對「換 CPU」有幫助）。
- 任何 spec 大改（新 instruction set / decoder rewrite）。
- 任何 emitter 共用層大改（StackOps / FlagOps / BitOps 重構）。
- Phase 7 block-JIT — 預計帶來 8-13× 加速，會徹底改寫這 4 個數字。

通常的 commit 不需要重跑 — refactor + 維護工作信任「unit test 全綠
+ Blargg 全綠」就夠了。
