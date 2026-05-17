# Verified Block-JIT — 使用手冊

Phase 30.15d 出了一個 per-block differential verifier，每執行一個 block 就把
JIT backend 跟 interpreter 比對，emitter bug 發生當下就 catch — 不會拖好幾週後
才透過某個莫名其妙的 boot failure 才被發現。本文件說明：

1. Framework 驗證的是什麼（CPU state、mem-write trace、side-effect log）
2. 怎麼跑（`--verify-blocks`）
3. 怎麼看 divergence report
4. 怎麼幫新 CPU backend 接上
5. 開發 loop 裡什麼時候該跑

設計理由跟歷史見
[`MD/design/30.15d-verified-blockjit-framework-design.md`]。

---

## 1. 驗證的是什麼

每個 cached JIT block，verifier：

1. **Snapshot** JIT env 的 pre-block state：完整 guest 記憶體 + CPU
   register + 選用的 HW-chip state（例如 PortBus cycling counter）。
2. **跑** 一次 JIT block；把每個 memory write、port write、IRQ assert 都
   記到 ring-buffer trace。
3. **Restore** 同樣 pre-block state 到平行的 INTERP env（每個 env 自己擁有
   memory + CPU instance — 不共享）。
4. **跑** interpreter N 次，N 是 JIT 實際執行的 instruction count（不是
   compile-time 的 block 大小 — 詳見 sprint 5.4c）。
5. **3-axis 比對**：
   - Block exit 時的 CPU state（regs + flags + PC）
   - Memory-write trace（count、address、value、size，按順序）
   - Side-effect log（port write、IRQ assert）

任何不 match = 那 N 個 instruction 裡有一個 JIT emitter 是錯的。Verifier 會 report：

- Block index + start PC
- 實際 instruction 數
- 哪個 axis diverged
- 第一個 mismatch entry，附完整 context
- JIT vs INTERP 的 CPU state snapshot

這樣可以把 bug pin 到具體 block + axis，不用讀 assembly listing。

## 2. CLI 用法

```
apr-pc \
    --bios=BIOS/firmware/pcxtbios.bin \
    --video-bios=BIOS/firmware/videorom.bin \
    --floppy-a=BIOS/freedos-1.3-floppy.img \
    --verify-blocks \
    --max-cycles=1000000
```

- `--verify-blocks` 啟動 verification mode（取代正常的 headless / windowed
  run；不開 UI window）。
- `--max-cycles=N` cap 驗證的 BLOCK 數（不是 host cycle）。預設 100,000。
- 其他 PC-CLI flag（`--floppy-a`、`--video-bios` 等）正常作用。

PIT timer + IRQ 會自動停 — verifier 需要 deterministic execution，
PIT 會引入 wall-clock 相關的 IRQ delivery 變數。

### 有用的 env var（debug verifier 本身用，平常用不到）

| Env var | 效果 |
|---|---|
| `APR_X86_TRACE_BLOCKLEN=1` | 每個 block-JIT entry 印 `[BLOCKLEN] pc=0xXXXXX detected=N actual=M`。懷疑是 JIT block-detection 或 runtime instr count 出問題時用。 |
| `APR_X86_NO_BLOCK_CACHE=1` | 每個 block 都強制 recompile（不 reuse cache）。確認 cache 新鮮度沒有遮住 stale-IR bug。 |
| `APR_X86_TRACE_COMPILE=0xPC` | 在 PC ±0x40 範圍內的每次 compile 都 log。確認 divergent block 實際上 compile 了哪些 byte。 |
| `APR_X86_IR_DUMP=1` | 每個新 compile 的 block 把 LLVM IR dump 到 temp file。bug 像是 IR-level 而不是 emitter-level 時用。 |

## 3. 怎麼讀 divergence report

範例 output：

```
running verified-block up to 1,000,000 blocks...

  elapsed:           00:00:07.2304521
  verified blocks:   19470 OK before divergence
  verified instrs:   37640
  status:            CpuStateMismatch
  diverged at block: #19471, pc=0xFE2A9, instrs in block=9
  detail:            CPU state diverged: AX: A=0x344 B=0x348
  JIT post-block:    PC=0xFE2C6 AX=0x0344 BX=0x0000 CX=0x0000 DX=0xC000 ...
  INTERP post-block: PC=0xFE2C6 AX=0x0348 BX=0x0000 CX=0x0000 DX=0xC000 ...
```

讀 report：

- **`verified blocks: N OK`** — 在 bug surface 之前兩邊跑 N 個 block 都一樣。
  N 越高 = bug 只在 deep state 才發生；N 越低 = bug 在 early-boot code
  或某個很常見的 instruction。
- **`status:`** — 哪個 axis 先壞。
  - `CpuStateMismatch`：register/flag/PC 不同。通常是 arithmetic / flag-update
    emitter 錯了。
  - `MemWriteTraceMismatch`：memory write 在 count / address / value / 順序
    上不同。通常是 store-instruction emitter 算錯 address 或 value，或者
    non-deterministic HW-state read 漏進 trace。
  - `SideEffectMismatch`：port write / IRQ assert 不同。通常是 I/O emitter 錯。
  - `CouldNotVerify`：framework hit 到限制（trace 被截斷，或 interp 在
    verification 半路 throw）。
- **`pc=0xXXXXX, instrs in block=N`** — block 從 PC 開始，JIT 跑了 N 個
  instruction（這是 ACTUAL count；即使 BlockDetector detect 到更多也以此為準，
  見 sprint 5.4c）。
- **`detail:`** — 第一個 mismatch 的精確 field。state mismatch 會 name 出
  diverge 的 register；trace mismatch 會 show 第一個不同的 trace record。
- **`JIT post-block:` / `INTERP post-block:`** — 兩邊的完整 register dump。
  比對找出哪個 arithmetic / flag 算錯。

### 怎麼 localise bug

1. 記下 block start PC 跟 instruction count。
2. 從 PC 開始 disassemble N 個 instruction：
   ```
   python tools/x86_disasm.py BIOS/firmware/pcxtbios.bin 0xFE2A9 9
   ```
   （或用任何 8086 disassembler 開）
3. Diverging axis 指出是哪一類 instruction 錯：
   - State 只 AX 差 → 在那個 block 裡找會碰 AX 的 instruction。
   - Mem-write trace 差 → 找 STORE instruction。
   - Side effect 差 → 找 IN/OUT / INT instruction。
4. 候選 instruction 太多就再用 `APR_X86_BLOCK_MAX=1`（每個 instruction
   各自 compile 成一個 block）做 bisect。

## 4. 幫新 CPU backend 接上

要讓 CPU 變 verifier-aware，實作 `IBlockBoundedSteppableCpu`：

```csharp
public interface IBlockBoundedSteppableCpu : ISteppableCpu
{
    int LastBlockInstructionCount { get; }           // actual count
    void StepOneArchitecturalInstruction();          // forces per-instr
    object BeginTrace(IBlockTraceSink sink);         // opaque token
    void EndTrace(object token);
    object SnapshotMemoryState();                    // memcpy RAM + CPU
    void LoadMemoryState(object snapshot);
}
```

x86 reference impl 見 `src/AprX86.Cli/Validation/X86LockstepAdapter.cs`。
重點 wiring：

- `StepOneArchitecturalInstruction` 必須 bypass 所有 block-JIT path
  （在 CPU 上用 "force per-instr" entry point）。
- `BeginTrace` swap 一個 static trace-sink field，你 JIT 出來的
  mem-write / port-write extern 每次呼叫都會讀它。
- `SnapshotMemoryState` 回傳的 state 要夠 `LoadMemoryState` 重現一樣的
  後續 execution。x86 V1 整個 1MB RAM memcpy；address space 更大就用
  CoW page table。

如果你的 backend 有 HW-chip state 會隨 port-read 演化（例如 GB APU envelope
counter、NES PPU dot counter），那就 expose `AdditionalSnapshot` /
`AdditionalRestore` callback 讓 harness 接 HW snapshot/restore
（x86 在 sprint 5.4b 對 `PcPortBus.Snapshot()` 的 pattern）。

Per-CPU verifier CLI 是 generic runner 的 thin wrapper：

```csharp
var jitStepper = new MyCpuSteppableCpu("JIT", envJit.Cpu, ...);
var interpStepper = new MyCpuSteppableCpu("INTERP", envInterp.Cpu, ...);
jitStepper.OnBeforeStep = () => envJit.Activate();      // sprint 5.4d
interpStepper.OnBeforeStep = () => envInterp.Activate();
var runner = new VerifiedBlockJitRunner(jitStepper, interpStepper);
for (long b = 0; b < maxBlocks; b++) {
    var r = runner.RunAndVerifyOneBlock();
    if (r.Status != VerifiedBlockStatus.Ok) { /* report */ break; }
}
```

## 5. 什麼時候該跑

- **Emitter 開發中**：改 `BlockFunctionBuilder.cs`、`*Emitters.cs` 或
  `AllocaSlotProvider.cs` 之後跑 `--verify-blocks` 對 5-10s 的 workload。
  regression 一發生就抓到。
- **Pre-commit（Tier 3）**：對 pcxtbios + FreeDOS boot 跑完整
  `--verify-blocks --max-cycles=1000000`。~3 min；幾乎任何 block-JIT
  issue 都會 surface 並附 concrete diagnostic context。
- **Bug-hunt（互動模式）**：當 real workload 在 block-JIT 下 hang 或行為
  異常、但 per-instr 正常時，先跑 `--verify-blocks` — 通常幾秒內就告訴你
  確切是哪個 block + axis + instruction，不用手動 bisect。
- **CI gate（提案，sprint 5.8 follow-up）**：任何 touch 到 `BlockFunctionBuilder`、
  `AllocaSlotProvider`、或任何 `*Emitters.cs` 的 PR 都跑 100k-block 驗證。
  Fail 阻擋 merge。

## 6. 限制

- **一次一個 block**：抓不到「只在多個 chained block 才 manifest」的 bug
  （例如 cache invalidation issue）。
  緩解：連續跑很多 block；cache invalidation 會 surface 成 invalidation 後
  第一個 block 的 divergence。
- **PIT IRQ 關掉**：verifier 需要 determinism。要驗證 IRQ delivery code path
  就寫具體的 test ROM 帶已知 IRQ sequence、用 lockstep mode 而不是 verify-blocks。
- **Snapshot 是 full memcpy**：V1 verifier 每個 block copy 整個 guest RAM。
  ~1MB × ~15k blocks/sec = ~15 GB/s mem-bandwidth。verification mode 可接受，
  CoW optimisation deferred 在 design doc §4.6。

## 7. Companion：differential fuzzer（Phase 30.17 / 30.18）

每個 verifier-aware CPU backend 都多了一個 fuzzer mode，產生隨機 instruction
stream 丟給 verifier。這會 surface hand-curated test ROM 抓不到的 emitter bug
（每個 CPU 的 fuzzer 第一次跑就至少找到一個 real bug）：

```bash
apr-nes --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-gb  --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-gba --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
apr-x86 --fuzz=N [--fuzz-blocks=M] [--fuzz-seed=S] [--fuzz-continue]
```

Flag：
- `--fuzz=N` — iteration 數（每次 iter 換新的 random ROM）
- `--fuzz-blocks=M` — 每個 iter 最多 verify 多少 block（預設 50-100；
  bound 每個 iter 的 runtime）
- `--fuzz-seed=S` — 可重現 seed（不給就用 time-based）
- `--fuzz-continue` — 不在第一個 divergence 就停；全部報告繼續跑（拿來
  measure bug density 有用；預設在第一個就停以便快速 bisect）

每個 fuzzer 還會 dump divergent block 附近的 ROM byte，方便 offline 拿去
disassemble。配合 verifier 的 pre-block state report（`pre:` line）就有
足夠 context 找出 buggy emitter。

### Fuzzer 找到的 bug 範例（Phase 30.18 session）

- **GB STOP (0x10)**：per-instr 忽略 pad byte 但 block-JIT 有吃。
  Spec fix：加 `read_imm8` step。
- **GB HALT (0x76)**：per-instr 的 `_haltSignal → _halted` 轉移只在
  `RunCycles` 裡做，verifier 用的 `StepOnePerInstr` 沒做。
  Runtime fix：加上 transfer。
- **x86 BlockDetector NOP-fallback**：把 0x00 當 silent NOP 合成（x86 的
  0x00 是 ADD r/m8, r8，不是 NOP），跑錯 semantics。
  Framework fix：只在 0x00 有 0 個 operand step 且 length = 1 才走 fallback。
- **GB SyncEmitter PC clobber (30.18s)**：EI 的 deferred `sync` body
  把 JP/CALL/RET 的 branch target 覆寫成 bi.Pc+length。
  改成 runtime PcWritten check 解。
- **GB MBC interaction (30.18u)**：random `LD (HL),A` 寫到 $4000-$5FFF
  觸發 MBC bank switch、JIT 跑 stale pre-compiled IR。
  在 fuzzer 用 `bus.SuppressMbcWrites=true` 解。
- **GBA STMDB R15 (30.18p)**：user-mode read path 對 R15 返回 stale PC。
  改成走 PipelinePcConstant 解。
- **GBA LDR/STR Rn=R15 (30.18q)**：per-instr WriteReg 用 runtime index =
  R15 沒 mark PcWritten。runtime check 解。
- **GB INC/DEC (HL) flag ordering (30.18v)**：store_byte 的 sync-exit
  跳過 post-store flag update。重排 spec step 解。
- **GB IRQ-cadence (30.18y)**：verifier INTERP 在 block boundary 沒 poll IRQ。
  用 `PollPendingIrqsAtBlockBoundary()` 解。
- **GB HALT-spin cadence (30.18ab)**：JIT.RunCycles 在 HALT-spin 會 tick
  bus（可能 timer overflow 喚醒），INTERP 沒做。在 `PollPendingIrqsAtBlockBoundary`
  裡 mirror 一次 tick 解。

Fuzzer 是找「下一個 emitter bug」的 production tool；verifier 是「證明 known-good
workload 跨 emitter 改動仍 bit-identical」的 production tool。Phase 30.18 sprint
之後，兩種 mode 對 4 CPU 在 primary test ROM（各 1M blocks）AND 52+ 個
random fuzzer seed 上都 report **0 divergences**。把這當成 regression baseline。

## 7.4 Spec linter（Phase 30.18n）

`SpecLinter` 走過 loaded 的 `CpuSpec.InstructionDef`、對歷史上造成 fuzzer-found
block-JIT bug 的 pattern 發 warning。每次 edit CPU spec 或加新 CPU 都該跑：

```bash
apr-nes --lint-spec     # NES (2A03)
apr-gb  --lint-spec     # GB (LR35902)
apr-x86 --lint-spec     # x86-16 (i8086)
apr-gba --lint-spec     # GBA (ARM7TDMI)
```

Exit code 0 = clean；4 = 有 warning。目前 rule set：

| Rule | Catch 的問題 |
|---|---|
| `PostStoreRegisterUpdate` | LDI/LDD 樣式的 step-order bug — `store_byte` 後面接 `write_reg_pair_named` 到同一 pair；store 內的 sync-exit 會吃掉 pair update |
| `HaltStopMetadata` | HALT/STOP 沒 `changes_mode:true` 或 `writes_pc:"always"` — block-detector 可能不會在這個 instruction 結束 block |
| `EmptyStepsNonNop` | non-trivial mnemonic 的 `Steps` 是空的 — 通常是打字錯或在做一半的 spec entry |

Fuzzer 找到新 pattern 就加新 rule。Pattern 寫在
`src/AprCpu.Core/JsonSpec/SpecLinter.cs` — 複製既有 rule 改一改即可。

## 7.5 Bisection 工具（Phase 30.18l/m）

Fuzzer 在 multi-instruction block 上 report divergence 時，要找出是哪個
instruction buggy，以前要手動讀 LLVM IR 或在每個 emitter 加 println trace。
兩個 env-var-driven bisection 工具（Gemini 推薦 Strategy 1）讓這變得又快又
不入侵：

### Early Bailout Bisection（`APR_EARLY_EXIT_*`）

把某個 SPECIFIC block（用 start-PC 比對）cap 到 N instruction、只重新 compile
這一個 block、再跑一次 verifier。其他所有 block 的 JIT optimization context
（register allocation、flag elision、cross-instruction dead-store
elimination）都保留 — 那些 block 的 bug 不會被 cap 遮住。

```bash
APR_EARLY_EXIT_BLOCK_PC=4DF5 \
APR_EARLY_EXIT_INSTR_COUNT=8 \
apr-gb --rom=... --fuzz=80 --fuzz-blocks=5 --fuzz-seed=42
```

工作流程：二分搜尋找 K：cap=K 會 diverge、cap=K-1 不會 → instruction K 就是
buggy 的。實作在 `BlockDetector.cs` 的 instruction-loop entry。

### Force Budget（`APR_GB_FORCE_BUDGET`）

把 GB block-JIT cycle budget 全域 cap 到 N。用來區分 per-instruction vs
multi-instruction-state 的 bug：如果 `APR_GB_FORCE_BUDGET=1`（≈ 單 instruction
block）divergence 還在，bug 在 single-instruction emitter；否則在
multi-instruction block state。

```bash
APR_GB_FORCE_BUDGET=1 \
apr-gb --rom=... --fuzz=80 --fuzz-blocks=50 --fuzz-seed=42 --fuzz-continue
```

注意：GB block-JIT 的 budget-exit 在 instruction N 的 cycle deduct 之後才 fire
（不是之前），所以第一個 instruction 無論 budget=1 與否都會跑。要真的 force
single-instruction block 就用 early-bailout + `APR_EARLY_EXIT_INSTR_COUNT=1`
+ 對應的 block PC。

## 8. 相關文件

- Design：`MD/design/30.15d-verified-blockjit-framework-design.md`
- Investigation history：`MD/design/30.15-blockjit-pc-investigation.md`
- General PC emulator testing：`MD/process/04-advanced-pc-emulator-testing.md`
- Commit QA tier：`MD/process/01-commit-qa-workflow.md`
