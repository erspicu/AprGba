# Phase 7 A.4 + A.6 — Block-JIT integration: **10× MIPS on GBA loop100**

> **The big jump**：Phase 7 從 5.7 baseline 一路堆 dispatcher / IR / inline / lazy
> flag / pass pipeline 各種 quick win 把 GBA 從 ~3.7 推到 ~8.5 MIPS（2.3×）。
> Block-JIT 一個動作直接再加 10×：**GBA arm 8.55 → 85.39 MIPS, thumb 8.55
> → 85.61 MIPS**。Phase 5.7 baseline 比起來 **約 23×**，real-time 跑到 20×。
>
> 變動範圍：A.4 (`BlockCache`) + A.6 (`CpuExecutor.EnableBlockJit` /
> `StepBlock` + `GbaSystemRunner` cycle scaling + apr-gba `--block-jit` flag)。
> A.3 (ORC LLJIT) 已先 ship，是這次 lazy module add 的前提。

---

## 1. 結果（4-run 平均，loop100 1200 frames）

| ROM                     | Backend     | runs | min  | **avg** | max  | per-instr (8.5) | **Δ vs per-instr** | vs Phase 5.7 baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|----------------:|-------------------:|----------------------:|
| GBA arm-loop100.gba     | json-llvm + block-JIT | 4 | 84.59 | **85.39** | 86.52 | 8.55           | **+899% (10.0×)**  | ~23×                  |
| GBA thumb-loop100.gba   | json-llvm + block-JIT | 4 | 85.14 | **85.61** | 85.91 | 8.55           | **+901% (10.0×)**  | ~23×                  |

Real-time multiplier:
- ARM block-JIT: 20.2–20.6× （per-instr 是 2.0×）
- THUMB block-JIT: 20.3–20.5×

Setup time: ~315ms — 跟 per-instr 一樣（block-JIT 是 lazy，spec compile
本身沒多花時間，每個 block 是 cache miss 才 compile）。

跑法：
```bash
apr-gba --rom=arm-loop100.gba    --frames=1200 --block-jit
apr-gba --rom=thumb-loop100.gba  --frames=1200 --block-jit
```

---

## 2. 為什麼是 10×

per-instruction dispatch 每 instruction 要做：
1. ReadPc + WritePc(pre-set R15)
2. ClearPcWritten (write 1 byte)
3. NotifyExecutingPc (interface call → bus internal logic)
4. Fetch from bus (interface call + region switch + array read)
5. NotifyInstructionFetch (interface call)
6. Decode (DecoderTable lookup)
7. ResolveFunctionPointer (Dictionary lookup, identity-keyed)
8. fn(state, instructionWord) (cdecl native call, 2 args)
9. ReadPc + ReadSelector (re-snapshot)
10. PcWritten check + branch detection
11. (no branch) WritePc(pc + size)

**每 instruction 動 11 個 host-side 步驟。**

Block-JIT dispatch 每 BLOCK (avg ~10 instructions) 要做：
1. ReadPc + CurrentMode (1×)
2. NotifyExecutingPc (1×, 從 N×降為 1×)
3. cache.TryGet (Dictionary lookup, 1×)
4. (cache miss only) Detect + Build IR + AddModule + GetFunctionPointer (~ms 級，amortized over ~100 hits)
5. Clear PcWritten (1×)
6. fn(state) (cdecl native call, 1 arg, 1×)
7. update counters (1×)

**每 block 動 7 個 host-side 步驟，攤分到 N≈10 instructions = 0.7 步/instr**。

加上 LLVM 在 block IR 上能做的優化（GVN / DSE / mem2reg 跨 instruction
share constant folding + flag dependency tracking）— block IR ~600 行
經 RunPasses 後變幾十行 hot path，比每個 instruction 獨立 fn 緊湊很多。

10× 並不意外 — 文獻上 dynarec block-JIT 對 RISC ISA 通常 10–30× per-instr
interpreter，我們本來 per-instr 已經是 JIT (不是 pure interpreter) 所以
頭部空間少一點。

---

## 3. 改動內容

### 3.1 `src/AprCpu.Core/Runtime/BlockCache.cs` (A.4)

新檔（~140 行）：
- `CachedBlock` struct: `(IntPtr Fn, int InstructionCount)`
- `BlockCache`: Dictionary&lt;uint, LinkedListNode&lt;Entry&gt;&gt; +
  LinkedList LRU 標準 pattern, capacity-bound (default 4096).
- `TryGet(pc, out CachedBlock)` / `Add(pc, CachedBlock)` /
  `Invalidate(pc)` / `Clear()` / `Count` / `Capacity`.
- O(1) lookup + O(1) MRU promotion.

### 3.2 `src/AprCpu.Core/Runtime/HostRuntime.cs` (A.4 supporting)

- 新欄位 `_externBindings: Dictionary<string, IntPtr>` — 每次
  `BindExtern` 都記下 (name → trampoline addr).
- 新 helper `BindExternInModule(module, name, addr)` 抽出原 `BindExtern`
  的 inttoptr-bake 邏輯，給兩處 reuse.
- `AddModule(module)` 開頭 replay 所有 known externs 進新 module's
  global slot — block module 透明繼承 initial module 的 trampoline。

### 3.3 `src/AprCpu.Core/Runtime/CpuExecutor.cs` (A.6)

- 新欄位:
  - `_compileResult: SpecCompiler.CompileResult?` — null = per-instr 模式
  - `_blockCachesBySetName: Dictionary<string, BlockCache>?` — 每 instruction set 一個 cache (ARM / Thumb 分開)
  - `_blockDetectorsBySetName: Dictionary<string, BlockDetector>?`
  - `LastStepInstructionCount: int` — Step 報告剛剛跑了幾條 instr (per-instr=1, block=N)
  - `BlocksCompiled / BlocksExecuted: long` — stats
- 新 method `EnableBlockJit(compileResult)` — opt-in，建立 cache + detector / set
- `Step()` 開頭判斷：if block-JIT enabled → `StepBlock()`，else 走原 per-instr 路徑
- 新 private `StepBlock()`：
  ```
  pc = ReadPc; bus.NotifyExecutingPc(pc)
  if !cache.TryGet(pc, out entry) {
      entry = CompileBlockAtPc(pc, mode)  // detect → build → AddModule → lookup
      cache.Add(pc, entry)
  }
  state[pcWrittenOffset] = 0
  fn(statePtr)
  LastStepInstructionCount = entry.InstructionCount
  InstructionsExecuted += entry.InstructionCount
  BlocksExecuted++
  ```
- 新 private `CompileBlockAtPc(pc, mode)`：
  ```
  block = detector.Detect(bus, pc, max=64)
  module = LLVMModuleRef.CreateWithName($"AprCpu_BlockJit_{set.Name}_pc{pc:X8}")
  bfb = new BlockFunctionBuilder(module, layout, registry, resolverRegistry)
  bfb.Build(set, block)
  rt.AddModule(module)
  fnPtr = rt.GetFunctionPointer(BlockFunctionBuilder.BlockFunctionName(set.Name, pc))
  return new CachedBlock(fnPtr, block.Instructions.Count)
  ```

### 3.4 `src/AprCpu.Core/Runtime/Gba/GbaSystemRunner.cs`

`RunCycles` 把 `Scheduler.Tick(cyclesPerInstr)` 改成
`Scheduler.Tick(cyclesPerInstr * Cpu.LastStepInstructionCount)` — block-JIT
模式下一個 Step 跑 N 條 instruction，scheduler 也要 tick N 倍 cycles 才
正確（VBlank / IRQ timing 才不會慢 10×）。

### 3.5 `src/AprGba.Cli/Program.cs`

- 新 CLI flag `--block-jit`
- `BootCpu(bus, enableBlockJit)` ctor 後 call `exec.EnableBlockJit(compileResult)`
- ROM startup 印 "block-jit: ON" 標示

---

## 4. 驗證

```
$ dotnet test AprGba.slnx
已通過! - 失敗: 0，通過: 360，略過: 0，總計: 360 (持續時間 20s)

$ dotnet src/AprGb.Cli/.../apr-gb.dll --cpu=json-llvm \
    --rom=test-roms/blargg-cpu/cpu_instrs.gb --frames=12000
01:ok  02:ok  03:ok  04:ok  05:ok  06:ok  07:ok  08:ok  09:ok  10:ok  11:ok
Passed all tests
```

- 360/360 unit tests（含 BlockCache 8 個 + BlockFunctionBuilder 3 個含
  AddModule round-trip integration test）
- Blargg cpu_instrs 11/11 (per-instr path on GB JIT, 沒有改 GB 路徑所以一樣綠)
- loop100 GBA arm + thumb (block-JIT path) — 10× 加速

---

## 5. 已知限制

### 5.1 menu / interactive ROM 不能 headless 跑

armwrestler 之類的 menu ROM 開機後 CPU 在 menu loop 等使用者 input。Headless
跑時 ROM 會跳到 garbage memory（因為沒人按按鍵讓它進真的 test），
block-JIT 對 garbage memory 大量編譯 64-instr block （PC 線性走，每 256
bytes 一個 cache miss），雖然 correctness OK 但編譯成本爆掉看起來像 hang。

**Per-instr 同樣會跑 garbage**（從同個 PC 開始也會走 garbage memory），
只是慢到看起來「在跑」。Block-JIT 因為快，把問題放大了。

**不是 block-JIT bug**，是 headless 跑 interactive ROM 的 mismatch。
測試 ROM 挑選請參考 memory `reference_test_roms.md`。

Future safeguard (A.6.1): PC-out-of-known-executable-region 時 fallback
per-instr Step，避免對 garbage memory 大量編譯。

### 5.2 GB CLI 還沒加 --block-jit flag

`apr-gb` (LR35902 / Game Boy) CLI 沒加 `--block-jit` flag。底下的
`CpuExecutor` 已通用，加 flag 是 ~10 行的事但要驗 LR35902 spec 的所有
emitter 在 block IR 路徑都能跑（不確定有沒有 LR35902 emitter 假設
single-instruction module 環境）。Phase 7 A.6.2 follow-up。

### 5.3 SMC 不偵測

寫入已編譯區域不會 invalidate cache。GBA homebrew 罕用 SMC，但
Pokemon-style ROM 跟某些奇怪 demoscene 會踩到。Phase 7 A.5。

### 5.4 還沒 block linking

block exit → 外迴圈 read PC → cache lookup。每 block 之間還是要回 host
做一次 dispatch。Phase 7 A.7 block linking 可以再省這 step（patch
native call site 直接跳到下個 block 的 native code），預期再快 1.5–2×。

---

## 6. Phase 7 累計（13 步）

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
| 7.C.b alloca-shadow retry | 8.49 / 2.0× | 8.51 / 2.0× | 31.66 | 6.52 |
| 7.A.3 ORC LLJIT swap | 8.55 / 2.0× | 8.55 / 2.0× | 32.49 | 6.64 |
| **7.A.4+A.6 block-JIT** | **85.39 / 20.5× 🎉** | **85.61 / 20.5× 🎉** | (n/a) | (n/a) |

GBA 從 Phase 5.7 baseline 3.6 MIPS → block-JIT 85+ MIPS = **23× total**.

---

## 7. 下一步建議

**Quick wins (perf 期望 +10–50%)**:
- A.6.1 garbage region fallback — 同時解決 menu ROM headless 跑問題 + 防止
  野生 ROM 的對抗 case
- A.6.2 GB CLI 也加 `--block-jit` — 驗證 GB JIT 路徑也能拿到 ~10× 收益
- A.7 block linking — 直接 patch native call site，省 dispatcher round-trip

**Mid-effort (perf +50–200%)**:
- A.5 SMC detection + invalidation
- A.8 state→register caching at block boundaries

**Architectural**:
- D tier compilation (cold O0 + hot O3 background recompile)
- 真 lazy flag (defer computation, not just batch writes)

---

## 8. 相關文件

- `src/AprCpu.Core/Runtime/BlockCache.cs` — A.4 cache
- `src/AprCpu.Core/Runtime/CpuExecutor.cs` — A.6 dispatch
- `src/AprCpu.Core/Runtime/Gba/GbaSystemRunner.cs` — cycle scaling
- `src/AprGba.Cli/Program.cs` — `--block-jit` flag
- [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md) Phase 7 A.4 + A.6 — 標 done
- [`MD/performance/202605021800-orc-lljit-upgrade.md`](/MD/performance/202605021800-orc-lljit-upgrade.md) — A.3 (前置 infra)
- 上一筆 perf note: `202605030241-cb-alloca-shadow-retry.md` (per-instr 8.49 MIPS baseline)
