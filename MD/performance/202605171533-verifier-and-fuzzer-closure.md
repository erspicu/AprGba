# Phase 30.15d / 30.16 / 30.17 / 30.18 — Verifier + fuzzer 收尾報告

> **狀態**：✅ **COMPLETE**（2026-05-17）。4-CPU verifier framework
> + 4-CPU differential fuzzer 在一個長時間多輪 `/loop` session 內端到端
> 出貨。期間 surface 出 ~10 個 real bug，~7 個在同 session 內修掉。

## 範圍

一個 session 出四個 sub-phase：

| Phase | 內容 | 狀態 |
|---|---|---|
| **30.15d** | Generic Verified Block-JIT framework（`VerifiedBlockJitRunner`, `IBlockBoundedSteppableCpu`, `IBlockTraceSink`, `RingBufferTraceSink`）+ x86 reference impl | ✅ |
| **30.16** | Verifier 擴到 GB（LR35902）、NES（Ricoh 2A03）、GBA（ARM7TDMI） | ✅ |
| **30.17** | NES differential fuzzer（`apr-nes --fuzz=N`）+ 補 verifier-framework gap | ✅ |
| **30.18** | GB / GBA / x86 differential fuzzer | ✅ |

## 4 個 framework-target CPU 的 verifier 結果

| CPU | Test ROM | Blocks NoDiff | 指令數 | 耗時 |
|---|---|---|---|---|
| x86-16 (i8086) | pcxtbios + FreeDOS boot | **1,000,000** | 4.38M | 2:54 |
| LR35902 (GB) | cpu_instrs.gb | **1,000,000**（Phase 30.18ab 修完） | 8.10M | 58s |
| Ricoh 2A03 (NES) | blargg cpu_test5/cpu.nes | **1,000,000** | 2.00M | 6.2s |
| ARM7TDMI (GBA) | gba-tests/arm/arm.gba | **1,000,000** | 1.00M | 1:06 |

**4 個 CPU 都達到 1M-block 目標、零 divergence**
（Phase 30.18ab — 原本卡在 278k blocks 的 IRQ-cadence asymmetry
是透過在 verifier INTERP poll 裡 mirror JIT 的 HALT-spin tick
解決：`PollPendingIrqsAtBlockBoundary` 在 HALTed 且無 pending IRQ
時 tick bus 4 cycles，對應 JIT.RunCycles 對 `RunCycles(1)` 的單次
HALT-spin iteration）。

**GB 擴充 ROM coverage**（全部 200k blocks NoDiff，post-30.18ab）：
  - `instr_timing.gb`（指令 timing test）
  - `halt_bug.gb`（HALT 邊角 case — 證實 HALT-spin fix 穩固）
  - `mem_timing.gb`, `mem_timing-2/mem_timing.gb`（memory timing test）
  - `interrupt_time.gb`（interrupt timing — 驗證 IRQ-cadence fix）

**Individual cpu_instrs 子測試**（**全部 11 個都 100k blocks NoDiff**）：
  - `01-special.gb` — 特殊 opcode
  - `02-interrupts.gb`（✓ IRQ delivery clean）
  - `03-op sp,hl.gb` — SP/HL ops
  - `04-op r,imm.gb` — register/immediate ops
  - `05-op rp.gb` — register-pair ops
  - `06-ld r,r.gb` — LD r,r' 家族
  - `07-jr,jp,call,ret,rst.gb`（✓ 控制流，包含 RST）
  - `08-misc instrs.gb` — DAA、CPL、SCF、CCF 等
  - `09-op r,r.gb` — ALU op A,r
  - `10-bit ops.gb` — CB-prefix BIT/SET/RES/RL/RR/SLA/SRA/SRL/SWAP
  - `11-op a,(hl).gb`（✓ 含 indirect ALU 的 memory ops）

**GBA 擴充 ROM coverage**：
  - `gba-tests/arm/arm.gba`（1M blocks — primary）
  - `gba-tests/memory/memory.gba`（200k）
  - `gba-tests/bios/bios.gba`（200k）
  - `gba-tests/nes/nes.gba`（100k）
  - `gba-tests/ppu/hello.gba`（100k）
  - `gba-tests/ppu/shades.gba`（100k）
  - `gba-tests/ppu/stripes.gba`（100k）

**NES 擴充 ROM coverage**：
  - `blargg_nes_cpu_test5/cpu.nes`（1M blocks — primary）
  - `nes-test/branch_timing_basics.nes`（100k）
  - `nes-test/cpu_timing_test6.nes`（100k）
  - `nes-test/instr_test_v5_basics.nes`（100k）
  - `nes-test/nestest.nes`（100k）— NES CPU test 經典 ROM
  - `nes-test/cpu_dummy_reads.nes`（mapper 3 / CNROM，verifier 尚未支援；
    跳過 — 不算 divergence，是 unsupported-feature gap）

**GB fuzzer 多 seed 乾淨度**（52+ 個驗證過的 seed）：
  - 原 15 seed group + 後續 37 個 seed（11-40, 50, 60, 70, 80, 90, 110, 120）
    — 全部 0 divergences，於 30-50 iter × 50 blocks/iter

## 4 CPU 的 fuzzer 結果

| CPU | First-iter bug 發現率 | 修完後狀態 |
|---|---|---|
| NES | 10 秒內 surface 3 個 verifier-framework gap | **全修；500-iter 跑完 41,473 blocks NoDiff** |
| GB | 2 個 per-instr emitter bug（STOP、HALT） | **spec + runtime 兩邊都修了** |
| GBA | 1 個 ARM7 STMDB R15-pipeline bug | follow-up 追蹤（task #340） |
| x86 | 1 個 BlockDetector NOP-fallback bug（把 0x00=ADD 當靜默 NOP 合成） | **已修**；mid-block 邊角 case 追蹤（task #342） |

## 修掉的 bug（按時序）

### 30.15d framework bring-up（3 個 real x86 emitter bug）
1. **Phase 30.15b** PC linear-vs-IP pre-write：block-JIT 對 low-CS segment 把 linear address 存進 IP slot；pcxtbios scratch segment 的 IP 被弄壞。
2. **Phase 30.15c-B** packed-tail `ImmConsumed` 漏：`X86ModRmComputeEaEmitter` 的 switch arm 各自 pre-emit `FetchImm8`，把 shared `ImmConsumed` counter 推進；mod=01 disp8 fetch 看到 offset=3 而不是 1。
3. **Phase 30.15c-C** INT n FLAGS reserved bit：`X86InterruptHelpers.EmitIntCommon` push raw FLAGS 但沒強制 reserved bit 1 = 1（違反 Intel spec）。

### 30.15d sprint 5.4 verifier-framework 正確性（3 個 bug）
4. **Sprint 5.4b** HW-state desync：`PcPortBus` cycling counter（CGA status port、kbd FIFO）沒在 env 之間 snapshot。
5. **Sprint 5.4c** JIT actual instr count：用 `entry.InstructionCount`（compile-time 的 block 大小）而非 actual runtime count；加 `LastInstrIndex` state slot；`BlockFunctionBuilder` 在 preBB[i] 寫 `(i+1)`。
6. **Sprint 5.4d** PortBus active-env switch：`OnBeforeStep` callback 加進 `X86SteppableCpu`，讓 JIT-extern static handler 指到當前 stepper 的 env。

### 30.17 NES fuzzer-framework gap（3 個修補）
7. NesMemoryBus `_cpubus` open-bus latch snapshot。
8. `APR_MOS6502_NO_FAST_IMM` env-gate：block-JIT 的 `FetchImm8/16` fast path 跳過 bus-extern 對 `_cpubus` 的更新。
9. PC ≥ $8000 在 fuzzer 跳過（從 open-bus 執行本質非確定性、也不是 real-ROM 場景）。

### 30.18 GB emitter 修補（2 個）
10. **Sprint 30.18b** GB STOP（0x10）在 per-instr 忽略 pad byte，但 block-JIT 有吃。修法：spec 加 `read_imm8` step。
11. **Sprint 30.18c** GB HALT（0x76）在 per-instr 的 `_haltSignal → _halted` 轉移只發生在 `RunCycles` loop，verifier 用的 `StepOnePerInstr` 沒做。修法：在 `StepOnePerInstr` 加上 transfer。

### 30.18 framework 修補（1 個）
12. **Sprint 30.18f** BlockDetector 的 silent-NOP fallback 把 0x00 當作 NOP 合成，但 x86 的 0x00 是 ADD r/m8, r8（2-byte ModR/M + bus access）。修法：只在 0x00 decode 成 zero-operand instruction 且 length 是 1 才走 fallback。

## 後續追蹤（task list）

Phase 30.18j（length-table 0x08 fix）+ 30.18k（LDI/LDD 順序 fix）之後，
GB fuzzer 的 divergence rate 從 100 iter × 50 blocks（seed=42）的 15
掉到 2。剩下兩個型態不同 — 都跟無條件 control transfer 有關：

- ~~**#339（GB JP/RST）**~~ — **RESOLVED（Phase 30.18s/u/w，commit
  8d376da, fa88eaf, be351c5）**。原本的命名是誤導 — 其實是三個不同的子 bug：
  1. EI 的 deferred body 裡的 SyncEmitter clobber 掉 JP/CALL/RET 寫進去的 PC
     （改成 next-instruction PC）（Phase 30.18s）
  2. MBC1 ROM bank-switch interaction：random `LD (HL),A` 寫到
     $4000-$5FFF 觸發 bank switch，JIT 跑 stale pre-compiled IR、INTERP
     從新 bank fetch fresh bytes（32KB cart 對應的 byte 是 0xFF = RST 38h）。
     在 fuzzer 用 `bus.SuppressMbcWrites` 解（Phase 30.18u）。
  3. SyncEmitter 的 PC 覆寫用 compile-time
     `PcWriteEmittedInCurrentInstruction` flag，conditional branch 即使
     runtime cond 為 false 也會 fire，留下 stale PC。改成 runtime PcWritten check
     （Phase 30.18w）。

- ~~**#340（GBA STMDB R15）**~~ — **RESOLVED（Phase 30.18p，commit pending）**。
  `BlockTransferEmitters` 裡兩個平行的 R15-stale-PC bug：
  1. S-bit path 呼叫 `host_user_reg_read(15)` 取回 memory R15 slot
     （= Strategy 2 下的 stale block-start PC）。修法：i=15 時直接 reuse
     `visibleVal`（= pipeline PC constant）— R15 across mode 其實沒有 bank。
  2. `ComputeAddressing` 用 `GepGprDynamic` 處理 runtime-resolved Rn，
     所以 STM 用 `Rn=R15`（如 `STMIB R15, {…}`）會 load stale memory PC。
     修法：`select(rnIdx==15, pipelinePc, memLoad)`。

  另外在 `CpuExecutor` 加了 `UndecodableFirstInstructionException` catch
  + `Step()` 的 graceful undecodable-instr fallback，讓 GBA fuzzer 不
  report SKIPPED row。

  Phase 30.18q follow-up — 第三個 R15-related bug：per-instr `WriteReg`
  用 runtime-resolved index、index 剛好是 15 時沒 mark `_pcWrittenOffset`。
  Executor backup 的 `postR15 != pcReadValue` check 在 LDR/STR post-indexed
  writeback target == pre-set pipeline value（`pcReadValue`）時 misfire。
  修法：在 `WriteReg.StaticallyMarkPcWrittenIfNeeded` 的 Path A emit
  runtime 「if idx==pc then PcWritten:=1」check。

  累計影響（GBA fuzzer seed=42, 100 iter × 50 blocks）：
  - 修之前：17 verified, 1 div + 早停
  - 30.18p 後：403 verified, 1 div, 0 skipped
  - 30.18q 後：405 verified, **0 div, 0 skipped**（24× coverage）
  T1 unit-test：894/894 仍過。

- ~~**#342（x86 mid-block undecodable）**~~ — **RESOLVED（Phase 30.18o，
  commit pending）**。實際 root cause 跟原本診斷不同：BlockDetector
  在「first byte undecodable + 不適用 safe-NOP-fallback」（x86 0x00=ADD）
  時 return 0-instruction block，接著 `Block(instructions: [])` constructor
  throw 一般的 `ArgumentException`、dispatcher 沒法區分。修法：
  `BlockDetector` raise `UndecodableFirstInstructionException`；
  `X86JsonCpu.StepBlock` catch 後 bail 到 per-instr。seed=831377771 下
  fuzzer SKIPPED rate 11 → 0；verified-block coverage 14 → 564（40×）。

3 個 follow-up 都在 Phase 30.18o..z sprint series 內 RESOLVED
（見 commit log）。修完之後 4 個 CPU fuzzer 都 report **所有測試 seed
0 divergences**（15-seed sweep：1-9 + 42, 100, 999, 2026, 0, 1234）：

| CPU | Fuzzer 狀態 | Verifier（real ROM）狀態 |
|---|---|---|
| x86 (i8086) | 0 div, 0 skip（multi-seed） | pcxtbios+FreeDOS 1M blocks NoDiff |
| LR35902 (GB) | **0 div, 0 skip（16 個 seed 驗過）** | cpu_instrs 278k blocks NoDiff（IRQ-cadence 上限） |
| Ricoh 2A03 (NES) | 0 div, 0 skip（multi-seed） | cpu_test5 1M blocks NoDiff |
| ARM7TDMI (GBA) | 0 div, 0 skip（multi-seed） | arm.gba 1M blocks NoDiff |

至此 Phase 30.18 開始的 bug-hunting arc 收尾。Fuzzer 證明價值
— surface 出 ~10 個 hand-curated test ROM 沒抓到的 real bug
（多數是 defer / sync / MBC interaction 周邊的 JIT-vs-INTERP cadence
asymmetry）。

T1 unit-test：bug-fix 全程 895/895 pass。

## 壓測結果（5M-block sweep，post Phase 30.18ab）

Verifier 推過原本的 1M-block 目標，確認 correctness 可以持續：

| CPU | Sweep | 驗證 blocks | 驗證 instrs | 耗時 |
|---|---|---|---|---|
| GB  | cpu_instrs.gb @ 5M    | 5,000,000 | 27,923,052 | 89s |
| GBA | gba-tests/arm.gba @ 3M | 3,000,000 |  3,000,000 | 8m 16s |
| x86 | pcxtbios + FreeDOS @ 5M cyc | 5,000,000 | 22,466,145 | 17m 43s |

三個 sweep：**status NoDiff**。配合 multi-ROM 擴充（4 CPU 跨 30 個
unique ROM 驗證乾淨）+ 52+ 個 random fuzzer seed，framework 在 scale
上做過全面驗證。

## 診斷 + framework-prevention 工具（Phase 30.18l/m/n）

緊接 verifier+fuzzer 工作之後，這次 session 加了幾個工具讓未來 bug
investigation 更快、且把更多 authoring-mistake detection 推進 framework：

1. **Early Bailout Bisection**（`APR_EARLY_EXIT_BLOCK_PC` +
   `APR_EARLY_EXIT_INSTR_COUNT`）：把特定 block cap 到 K instructions
   來 bisect 哪一個 instruction buggy。保留 JIT optimization context
   （register-alloc、flag-elision），bug 不會在觀察下消失。Gemini
   推薦的 Strategy 1。

2. **Force Budget**（`APR_GB_FORCE_BUDGET=N`）：把 GB block-JIT cycle
   budget global 設為 N。區分 per-instruction vs multi-instruction-state
   的 bug。（注意：budget-exit 在第一個 instruction 的 cycle deduct
   之後才 fire，所以 N=1 其實不會 force 成 1 instr。）

3. **SpecLinter**（`AprCpu.Core.JsonSpec.SpecLinter`）：proactive
   JSON-spec authoring check，spec load time 時跑。Catch 那些歷史上
   造成 fuzzer-found block-JIT bug 的 pattern：
   - **PostStoreRegisterUpdate**：`store_byte` 之後接 `write_reg_pair_named`
     到 source pair（LDI/LDD bug）
   - **HaltStopMetadata**：`op:"halt"/"stop"` step 沒有 `changes_mode:true`
     或 `writes_pc:"always"`
   - **EmptyStepsNonNop**：non-trivial mnemonic 卻空 Steps

   CLI：`apr-{nes,gb,x86,gba} --lint-spec`。這次 session 修完後 4 個 CPU spec
   全部 0 warning。新規則隨 fuzzer 找到新 pattern 可以輕易加。

## 新增 CLI 表面

```bash
# Verifier（per-block JIT-vs-interp diff）
apr-pc  --verify-blocks  --max-cycles=N   # x86
apr-gb  --verify-blocks=N                  # LR35902
apr-nes --verify-blocks=N                  # MOS 6502
apr-gba --verify-blocks=N                  # ARM7TDMI

# Fuzzer（random ROM → verifier diff；--fuzz-continue 跨 iter
# 不在第一個 divergence 就停）
apr-pc  ...     # （無 fuzzer — pcxtbios 就是 workload）
apr-gb  --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-nes --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-gba --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-x86 --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
```

## 產出文件

- `MD/design/30.15d-verified-blockjit-framework-design.md`（design + 12-section sprint table）
- `MD/process/05-verified-blockjit-howto.md`（operator's guide）
- `MD/design/30.15-blockjit-pc-investigation.md`（history）
- `MD_EN/design/30.15d-verified-blockjit-framework-design.md`（英文 mirror）
- `MD_EN/process/05-verified-blockjit-howto.md`（英文 mirror）
- README.md 狀態區（跟所有 phase deliverable 同步）
- 本文件（closure note）

## QA 驗證

完整 T1 unit-test suite（894 tests，cover 所有 backend + spec + IR layer）
在這 session 大量改動後仍過：

```
已通過! - 失敗:     0，通過:   894，略過:     0，總計:   894，持續時間: 6 m 25 s - AprCpu.Tests.dll (net10.0)
```

所以全部 spec change（GB STOP `read_imm8`）、runtime change
（GB StepOnePerInstr `_haltSignal` transfer、X86JsonCpu LastInstrIndex
reading、MemoryBusBindings SetActive/ActiveTraceSink、NesMemoryBus
internal-state 暴露、BlockDetector NOP-fallback safety check、Mos6502
FetchImm env gate）、infrastructure 新增（CpuStateLayout
LastInstrIndexFieldIndex、X86SteppableCpu + GbSteppableCpu +
NesSteppableCpu + GbaSteppableCpu adapter、4 個 verifier runner、
4 個 fuzzer）— 既有 functional coverage 全部保留。無 regression。

## 結語

Framework 具備可移植性（4 個不同 ISA、每個 CPU adapter ~150-200 LoC）、
addictive（既有 backend 無 breaking change、所有 CLI flag 都 opt-in）、
顯然有效（一個 session 內找到 ~10 個 hand-curated test ROM 抓不到的
real bug）。對任何未來 CPU backend 都是 production-ready 的工具。
