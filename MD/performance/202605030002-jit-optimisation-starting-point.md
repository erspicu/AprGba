# JIT 優化實驗起始點 — Phase 5.8 refactor 收工後

> **角色**：這份文件是後續 JIT 優化嘗試的 **canonical 起始基準**。
> 之後每次試新策略（block-JIT、lazy flag、SMC handling、tier
> compilation、IR-level optimisation passes 等）都拿這個表做
> before/after 對照，新檔案以同樣 `YYYYMMDDHHMM-` prefix 寫進
> `MD/performance/`。
>
> **時間點**：Phase 5.8 emitter library refactor 5.1–5.7 完成
> 後（commit `ce57b35`，2026-05-03）。
> **跑法**：4-ROM × 1200 frame，Release build (.NET 10) + LLVM 20
> MCJIT + Windows 11 + 同台開發機。

---

## 1. 起始基準數據

| ROM                     | Backend     | Host time | Real-time × | **MIPS** |
|-------------------------|-------------|----------:|------------:|---------:|
| GBA arm-loop100.gba     | json-llvm   | 22.05 s   | 0.9×        | **3.82** |
| GBA thumb-loop100.gba   | json-llvm   | 22.51 s   | 0.9×        | **3.75** |
| GB 09-loop100.gb        | legacy      |  0.297 s  | 67.6×       | **32.76**|
| GB 09-loop100.gb        | json-llvm   |  3.681 s  | 5.5×        | **2.66** |

> 數據都是 3-run 平均；個別 run + 詳細解讀見
> `MD/note/loop100-bench-2026-05-phase5.8.md`。

---

## 2. 系統現況（影響 perf 的 baseline 條件）

- **JIT 引擎**：LLVMSharp.Interop 20.x + MCJIT（不是 ORC LLJIT）
- **Code cache**：每條指令一個 LLVM function；無 block-level
  caching；每條指令 dispatch 透過 indirect call 進 JIT'd function
- **Optimization level**：LLVM `OptLevel.Default`（O2 等級，但
  function 太小 inliner 通常沒得做）
- **State layout**：CpuStateLayout 動態 build，所有 register
  / status / pc-written 都是 byte slot 在一個 struct 裡，每個
  emitter GEP 進去
- **Memory bus**：每條 load/store 走 extern function pointer
  call（host_mem_read8 / write8 / write16）；trampolines 是
  `[UnmanagedCallersOnly]` 靜態 C# methods
- **Cycle accounting**：hardcoded `cyclesPerInstr=4` (GBA) /
  per-instr 從 spec cycle table (GB)
- **Scheduler tick**：每條指令呼叫 GbaScheduler.Tick / GbScheduler
  .Tick (per scanline LY 推進、IRQ 觸發)
- **PcWritten flag**：每條指令多 1 次 byte slot write + dispatcher
  讀 1 次 (用來偵測 branch 是否寫了 PC，避免雙倍推進)
- **Open-bus pre-fetch protection**：BIOS 區段觸發 NotifyExecutingPc
  + NotifyInstructionFetch (每條指令 2 次 virtual call)
- **Spec → IR cost**：one-shot at startup，~50-200 ms (沒精確量過)，
  沒含在 MIPS 數字裡

---

## 3. 可能的優化策略（待逐一試）

排序大致從低風險高回報到高風險未驗證：

### 3.1 IR-level 優化（不改架構）

- **a)** OptLevel 調 O3 + 加 LLVM passes (instcombine, gvn, simplifycfg)
- **b)** 把 PcWritten flag 改成 LLVM register hint，避免每條指令都
  load/store 進 byte slot
- **c)** Inline `read_reg` / `write_reg` 結果在同 function 內
  的多次 access — 目前每次都重新 GEP + load
- **d)** 把常見 Binary chain（add+sub+and）標 `nuw nsw` 給
  LLVM 更多 optimisation 餘地
- **e)** Cycle-accounting 改 trailing add 而不是 mid-function
  call，讓 scheduler.Tick 變 inlinable 候選

預期：5-15% 加速，全部放在 single-instruction emitter 改寫，
低風險。

### 3.2 Dispatcher 路徑優化

- **a)** Dispatcher 從 hash-lookup 改 direct table (decoded
  opcode → fn pointer 陣列)
- **b)** PcWritten 的 dispatcher 端 read 改 lazy（只在 branch
  指令後才檢查，不是每條都檢查）
- **c)** Memory-bus extern call 改 inline check + slow path call
  (fast path: ROM/RAM 直接 GEP；slow path: IO write extern)
- **d)** 把 instruction set switch (CPSR.T for ARM/Thumb,
  CB-prefix for LR35902) 改 smaller dispatch table

預期：10-25% 加速，影響面比 3.1 大但仍局部。

### 3.3 Block-level JIT（Phase 7 原規劃）

- 從 PC 掃到 branch / return / IO write 為止，把多條 instr
  串成一個 LLVM function
- 預期 8-13× 加速（dispatch overhead 攤掉、LLVM 優化大 block 比
  小 function 給力）
- 風險高：SMC detection、indirect branch dispatch、code cache LRU、
  block-linking 都要做。1-2 週工作量。
- **目前已決定不做**（不影響 framework 研究主題；real-time 60fps
  不在 MVP scope）

### 3.4 .NET Native AOT

- 把整個 host runtime AOT 編譯（包括 dispatcher、bus shims）
  避開 .NET tiered JIT 的 cold-start cost
- 對 GB legacy 的 −15% 可能是 .NET tiered 還沒 promote 到 tier-1，
  AOT 應該能還原原速度
- 跟 LLVM JIT 路徑無關 — 只影響 host runtime perf

預期：解決 GB legacy noise；對 json-llvm 影響微小。

### 3.5 還沒想到的

每次試新優化要把這份文件 §3 補一條，記錄 hypothesis + 預期收益 +
實際結果。

---

## 4. 對照基準的紀律

- **同跑法**：同台機器、同 OS load (跑前確認沒大型 process)、
  同 1200 frame、同 4 ROM、同 Release build
- **每組 ≥ 3 run**：取平均，看到個別 run > ±10% 偏離平均要追
  原因不能直接 average drown
- **Apples to apples**：新策略要寫進 same loop100 ROM 同樣 1200
  frame；改 ROM / 改 frame 數會破壞對照
- **每組獨立檔**：新檔名 `YYYYMMDDHHMM-<策略名>.md`，引用本
  檔當 baseline；不覆蓋本檔
- **誠實寫退步**：如果新策略某個指標退步，要寫上去；不擇優報

---

## 5. Bench 重現 cmd（複製就跑）

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

每行末尾印 `instructions: N → M.MM MIPS`。3-run 平均寫進新檔表格。

---

## 6. 相關文件

- `MD/note/loop100-bench-2026-05.md` — Phase 5.7 原始 baseline
- `MD/note/loop100-bench-2026-05-phase5.8.md` — Phase 5.8 refactor
  完工後對照（refactor 影響：基本 perf-neutral）
- `MD/design/03-roadmap.md` — Phase 進度，Phase 7 已標「不做」
- `MD/design/11-emitter-library-refactor.md` — emitter 通用化設計
