# 進階 timing 準確處理 + 框架通用結構 — 設計觀念與方法

> **Status**: design synthesis (2026-05-04)
> **Scope**: 解釋 AprCpu framework 在 block-JIT 模式下如何在「保持
> cycle-accurate timing」跟「保持 framework 通用性」之間取得平衡的設計
> 觀念、方法、跟取捨。
>
> 對照：個別機制有自己的設計 doc（`13-defer-microop.md`、
> `14-irq-sync-fastslow.md`、`12-gb-block-jit-roadmap.md`）；本檔是
> synthesis，把底層觀念 + 通用化方法寫清楚。
>
> **目標讀者**：未來要 (a) 移植第三顆 CPU 的人、(b) 維護 timing 行為的
> 人、(c) 想理解「為何要設計成這樣」的 future me。

---

## 1. 為什麼這份 doc 有價值

Block-JIT 把 N 條指令編譯成一個 native function 一次跑完，**消除 dispatch
overhead** 是它的核心收益。但這個收益有代價：JIT'd 的 block 內部對外部
HW (PPU / Timer / IRQ controller / DMA) 是**不透明的**——HW 不知道你跑到
block 第幾條。所有需要「per-instruction granularity」的 emulator timing
behaviour 在 block-JIT 模式下都會被破壞。

行業裡解這題的方案各家都有（QEMU TCG、Dynarmic、mGBA、Dolphin），但都是
**arch-specific**：一套 ARM 邏輯、一套 PowerPC 邏輯、再一套 SH-4 邏輯。
我們的不同：**JSON-driven framework**——同一個 BlockFunctionBuilder 跑
ARM、Thumb、LR35902，未來再加 6502 / Z80 / 8080 也走同一條路。

這 doc 紀錄的是「**怎麼把 timing-accurate 機制設計成 framework-level，而
不是每顆 CPU 重寫一遍**」的觀念跟方法。寫下來的價值：

1. 後人 port 第三顆 CPU 時不用重新發明 sync / defer / SMC / region inline 等概念
2. 維護期遇到 timing bug 知道從哪條軸找（Pattern A/B/C 哪個出問題）
3. 每個 trade-off 被明確記錄、不藏在 magic constant

---

## 2. 核心 tension：為什麼 block-JIT 準確 timing 困難

Real CPU 跟 HW (PPU、Timer、DMA、IRQ controller) 是**同步時鐘運作**：

```
cycle:  0    1    2    3    4    5    6    7    8    9   10
CPU :  [LD A,n][        ADD HL,BC        ][JR NZ,e][......]
HW  :   ↑    ↑    ↑    ↑    ↑    ↑    ↑    ↑    ↑    ↑    ↑
        Timer tick, PPU advance, IRQ delivery check, DIV++
```

**Per-instruction interpreter** 自然對齊：執行 1 條 instr → tick HW → 檢查
IRQ → 下一條。每個 cycle 邊界 HW 都被觀測到，但**每條 instr 都付 dispatch
cost**（fetch + decode + indirect call + cycle book-keeping）。

**Block-JIT** 把 N 條 instr 一個 function call 跑完，攤掉 dispatch cost——但
HW 的觀測點消失了：

```
JIT call:   [block of 17 instructions runs natively]
HW  :       ?   ?   ?   ?   ?   ?   ?   ?   ?   ?   ?   ?
                ← HW 看不到 block 內部 cycle 邊界
```

**Block-JIT 性能收益正比於 block 平均長度**（block=1 → per-instr 速度；
block=20 → ~10× 加速）。所以**目標是 block 越長越好，但又要在「需要觀測
timing」的地方乖乖讓出**。這是核心 tension。

容易破壞 timing 的具體事件：

| 事件 | 為何破壞 timing | 範例 |
|---|---|---|
| **IRQ pending change** | HW 設了 IF.Timer，下一條 instr 邊界該跳 IRQ vector | Timer overflow 後該立刻服務 |
| **MMIO read 隨時間變** | DIV / LY / STAT 每 cycle 不同；read 在錯的 cycle 拿錯值 | Pokemon BUSY-wait DIV |
| **MMIO write 觸發 HW reset** | 寫 LCDC.0=0 立刻關螢幕、寫 0xFF04 reset DIV、寫 IF/IE 改 IRQ | LCDC 切換中 trigger STAT IRQ |
| **Self-modifying code (SMC)** | 寫 WRAM 然後 JR 過去；cached IR 是舊 byte | Blargg test framework copy 新 sub-test 進 WRAM |
| **Delayed-effect 指令** | EI 不是立刻設 IME，是「下一條 instr 完才生效」 | LR35902 EI 邊界 IRQ 處理 |
| **Conditional cycle cost** | JR cc 不 taken 8 cycles、taken 12 cycles | HALT 等 IRQ 醒來時間不固定 |
| **Pipeline-PC quirks** | ARM7 PC reads 看到 pc+8 (pipeline ahead) | BIOS LLE LDR pc, [r0, ...] |

每一條都是「block 裡沒處理 → 下游 HW 行為錯」。

---

## 3. 三個架構 pattern — 觀念主軸

我們不是「逐個 timing 問題零散修」——用的是**三個可重用架構 pattern**。任
何新的 timing 問題進來先問「歸到哪個 pattern」。**這節是這篇 doc 的主軸**。

### 3.1 Pattern A — Predictive downcounting（先預估、邊跑邊扣）

**用在**：cycle accounting（Phase 1a）、cycles_left budget。

**觀念**：
1. 進 block 前 host 計算「下個 scheduler event 還剩 N cycles」存到
   `state.cycles_left`
2. Block IR 每條 instr 跑完 deduct `cycles_left -= cycle_cost`
3. 達 ≤ 0 時 block 提早 exit (寫 next-PC + PcWritten=1)
4. Host 從 `cycles_consumed = N - state.cycles_left` 算實際 tick 數

**為何叫 predictive**：cycle cost 是 spec 給的，編譯時就知道；不用 runtime
查 lookup table。每條 instr 的 sub + icmp，LLVM 會 fold + branch predict。

**通用性**：
- 完全 spec-driven（cycle.form 解析在 BlockFunctionBuilder）
- 任何 CPU 用同 mechanism；只要 spec 寫好 cycle.form 就 work
- ARM 跟 LR35902 共用同一 path

### 3.2 Pattern B — Catch-up callback（事件發生那刻補 tick）

**用在**：MMIO 觸發 PPU/Timer/DMA 同步（Phase 1b）。

**觀念**：
1. Bus interface 暴露 `OnMmioWrite(addr, value, cyclesSinceLastTick)` callback
2. JIT'd block 寫 MMIO 時透過 bus extern；bus 處理寫之前先
   `Scheduler.Tick(cyclesSinceLastTick)` 把 PPU/Timer 推到「現在」
3. 然後才實際做寫；後續同 block 的 MMIO 讀讀到正確值

**為何 retroactive**：HW 應該每 cycle 都 tick；我們 batch tick；但在「會看
到 HW state 的時刻」(MMIO read/write) 必須先 catch up，不然讀到 stale。

**通用性**：
- Bus interface 是 platform 介面 (`IMemoryBus.OnMmioWrite`)
- Scheduler tick 邏輯 platform-specific（PPU / Timer model 不同）
- Block-JIT 端只負責「呼叫 bus」；catch-up 細節 bus 自己處理

### 3.3 Pattern C — Sync exit（HW 狀態變了就立刻退 block）

**用在**：IRQ delivery (P0.7)、SMC invalidation 觸發 (P1 #5b)。

**觀念**：
1. 某事件可能改變 HW 狀態 (write to MMIO 改 IRQ readiness、寫 cached
   code 區改 IR validity)
2. JIT'd code 在事件發生那刻檢查 sync flag (extern return value or coverage
   counter)
3. flag set → block 立刻 exit（寫 next-PC + PcWritten=1 + ret void）
4. Host 看到 PcWritten=1 進 dispatch loop，可以 deliver IRQ / re-compile

**通用性**：
- `sync` micro-op (`14-irq-sync-fastslow.md`) — spec 任何 step 後可以加
  `{ "op": "sync" }`；emitter 自動產生 mid-ret IR
- Bus extern 的 `Write*WithSync` variant 統一介面（return i8 sync flag）
- SMC inline notify 是同 pattern 的特化（fast path inline check + slow
  path notify call）

### 3.4 三 pattern 的決策樹

遇到新 timing 問題時，問序：

1. 「這個 timing 在 cycle 帳就能解？」→ **Pattern A**（predictive）
   - 例：每條 instr cycle cost、conditional branch taken cost
2. 「事件發生時刻必須先把 HW 推到現在？」→ **Pattern B**（catch-up）
   - 例：MMIO read 看到正確的 LY、STAT、DIV
3. 「事件之後 control 必須回 host？」→ **Pattern C**（sync exit）
   - 例：IRQ delivery、SMC invalidation、CPU mode change

不在三條軸上的 timing 問題——可能設計需要重檢。

---

## 4. 框架通用化的 9 個 method

底層觀念有了，怎麼**避免每顆新 CPU 都要重寫一遍**？9 個方法：

### 4.1 通用 micro-op 取代 arch-specific primitive

**做法**：把 timing 行為抽象成 spec micro-op，不寫死在 C# emitter。

**例子**：
- `defer { delay: N, body: [...] }` — 任何「N 條 instr 後生效」的延遲
  - LR35902 EI / Z80 STI / x86 STI / SH-2 branch delay slot 都用這個
- `sync` — 任何「需要 host 觀測一下」的 yield 點
  - LR35902 MMIO 寫 / IRQ-relevant 寫 / 將來其他 CPU 的 cache fence

**收益**：spec 寫一次，所有走 spec 的 CPU 自動 work。新 CPU port 時不用動
C# emitter，spec JSON 寫對就行。

**反例**：早期 P0.5b 的 hardcode `BlockDetector.HasEiDelayStep` 檢查
LR35902-specific step name——後來被 P0.6 generic `defer` 取代。

### 4.2 EmitContext 作為 routing layer

**做法**：emitter 不直接呼叫 `Layout.GepGpr(builder, statePtr, idx)`，
改呼叫 `ctx.GepGpr(idx)`。`ctx` 內部判斷要回 alloca shadow 還是 state-struct
GEP。

**為何**：block-JIT 模式下要把 GPR 放到 alloca shadow 讓 mem2reg 撈走；
per-instr 模式不需要。**Emitter 不應該關心**自己跑在哪個模式。

**收益**：
- 同一個 spec emit code 在 per-instr 跟 block-JIT 都 work
- 未來加新模式（AOT、dynamic recompile）只要 ctx 加新 routing 路徑
- Refactor scope 是「EmitContext 加方法」+「call site 改名」，不是「每個
  emitter 內加 if/else」

### 4.3 AST pre-pass 處理 control-flow transformation

**做法**：spec → IR 之間插入 transformation pass。

**例子**：
- `DeferLowering` — block 進 IR 之前 walk instructions、處理 defer 的
  「delay N → 注入 phantom step」
- 未來可加：dead-flag elim、constant folding within block、micro-op fusion

**為何**：spec 寫法直觀（defer 包住 body），但 IR 階段需要「攤平」成
phantom step。中間有 transformation pass，spec 跟 IR 互不污染。

**通用化**：每個 transformation pass 都是 stateless function `List<Instr>
→ List<Instr>`；可以 stack 多個 pass。

### 4.4 Spec-driven + callback escape hatch

**做法**：能用 spec field 解決就 spec；spec 表達不出來的用 callback 注入
host 邏輯。

**例子**：
- 變寬 detector — 95% 邏輯 spec-driven (mask/match/format)；length 表用
  `lengthOracle: Func<byte, int>` callback 注入
- Bus extern — function signature 在 spec / IR；實作在 host C# 的
  `[UnmanagedCallersOnly]` static method
- Prefix sub-decoder — spec 標 `prefix_to_set: "CB"`；sub-decoder 由
  `SpecLoader` 從另一個 spec file 載入

**準則**：先嘗試 spec；spec 寫不出來才 callback；callback 介面要 minimal
（一個 function、不要 stateful object）。

### 4.5 Cold-path inlining + LLVM `expect` hint

**做法**：fast path inline 進 IR；slow path 發 conditional branch + 把
cold BB 標冷。

**例子**：
- WRAM/HRAM inline write (`EmitWriteByteWithSyncAndRamFastPath`) — region
  check inline；MMIO/cart-RAM 走 extern + sync path
- SMC inline notify (`EmitSmcCoverageNotify`) — 1-byte counter check inline；
  非零才 call extern
- Sync micro-op — extern 回 0 直接 br；回 1 才走 mid-ret

**why it matters**：99%+ 路徑是 fast path（WRAM 寫不是 SMC、MMIO 寫不改
IRQ）；inline 把 9-12ns extern call 降到 1-2ns load+branch。

### 4.6 IR-level region check + base-pointer pinning

**做法**：把「memory bus dispatch」這層 pure host concept 部分內化進 IR。
透過 (a) pin host byte array (b) bake address as IR 常數 (c) region check
inline IR。

**例子**：P1 #7 WRAM/HRAM inline write
- JsonCpu.Reset 把 `bus.Wram` GCHandle.Pinned，`AddrOfPinnedObject()` 拿
  pointer
- HostRuntime.BindExtern 把 pointer bind 到 LLVM global `lr35902_wram_base`
- IR 用 `(addr - 0xC000)` GEP 直接 load/store

**為何**：bus extern call 是 9-12ns；inline GEP-store 是 1ns。對 RAM-heavy
workload 巨大。

**通用化**：`MemoryEmitters.GetOrDeclareRamBasePointer` 是 generic helper；
per-CPU spec 決定哪些 region 適合 inline（無 side effect 的純記憶體區）。

### 4.7 Strategy 2 PC handling — pipeline-PC 變編譯時常數

**做法**：block-JIT 模式下 PC 讀取 resolve 成 `bi.Pc + offset` baked
constant；只有真正分支才寫回 GPR[15] / state.PC。

**為何**：原本 per-instr executor 在每條 instr 前 pre-set
`R15 = pc + pc_offset_bytes` 模擬 ARM7 pipeline；block-JIT 內每條 instr
都做這件事會 (a) 製造大量 redundant write (b) 讓「PC 改了沒？」這個訊號
含糊（pre-set 跟真分支看起來一樣）。

**通用化**：
- ARM (PC = pc+8)、Thumb (PC = pc+4)、LR35902 (PC = pc+length) 用同 mechanism
- `EmitContext.PipelinePcConstant` 是統一介面
- 變寬 set 用 `bi.Pc + bi.LengthBytes`；定寬 set 用 `bi.Pc +
  spec.PcOffsetBytes`
- 真正分支用 `WriteReg.MarkPcWritten` 標記

### 4.8 Generation-counter for ORC duplicate-definition

**做法**：每次 re-compile 同一 PC 的 block 時 fn name 加 `_g{N}` suffix。

**為何**：ORC LLJIT 不釋放舊 module；同名 fn add 第二次拋 "Duplicate
definition"。

**通用化**：BlockFunctionBuilder.Build 接受 `int generation = 0` 參數；
JsonCpu 維護 monotonic `_blockGeneration` counter。

### 4.9 Lockstep diff harness 作為 framework infrastructure

**做法**：把 per-instr 跟 bjit 兩 backend 跑同一 ROM 同一狀態，每 N 條
instr 比對 register file + WRAM diff。發現分歧立刻停。

**例子**：`apr-gb --diff-bjit=N`（commit `3617240` 加上）

**為何**：bjit 行為微妙；T1 unit test 沒法覆蓋所有 (PC × state × instr)
組合；T2 screenshot test 慢且只看 final state。Lockstep diff 是「bjit 應該
每步等於 per-instr」的精確檢查。

**通用化**：同一 harness 對 ARM / LR35902 都 work；diff 檢查的 field 是
spec-driven (RegisterFile + status registers)；新 CPU 不用改 harness 邏輯。

---

## 5. 各 timing 機制 inventory + 對應 pattern/method

把上面三個 pattern + 九個 method 對照到實際機制：

| 機制 (commit) | Timing 問題 | Pattern | 用到的 method |
|---|---|---|---|
| Predictive cycle downcounting (`77396ca`) | Block 內無法 instruction-grain tick HW | A | spec-driven cycle.form |
| MMIO catch-up callback (`860d7fe`+`05c285a`) | mid-block MMIO 寫後讀讀到 stale value | B | bus interface |
| Generic `sync` micro-op (`999f9eb`) | mid-block IRQ relevant change → 該立刻 deliver | C | 4.1 generic micro-op |
| Bus sync extern variant (`0c001fc`) | block-JIT 區分 IRQ-relevant write | C | 4.5 cold-path inlining |
| `defer` micro-op (`51c2921`) | LR35902 EI / Z80 STI 等延遲生效 | (compile-time) | 4.1 + 4.3 AST pre-pass |
| Conditional branch taken-cycle (`f27450f`+`7dd1e04`) | JR cc taken vs not-taken 不同 cycle 數 | A 補強 | per-instr pre-exit BB |
| HALT/STOP block boundary (`c47d849`) | HALT 等 IRQ 醒來時間不固定 | (block 邊界) | 4.4 spec step boundary |
| SMC V1 (per-byte coverage) (`24a58d1`) | Self-modifying code 後 cached IR 過時 | (cache invalidation) | per-byte counter + bus path notify |
| SMC V2 inline notify + 精確 coverage (`377379c`) | P1 #7 inline write bypass bus path → SMC miss | C | 4.5 inline check + 4.4 callback notify |
| Strategy 2 PC handling (`5b4092f`) | block-JIT 內 PC pre-set 製造干擾 | (compile-time const) | 4.7 |
| Variable-width detector (`fdce42c`) | 變寬 ISA 沒法 fixed-stride decode | (per-set callback) | 4.4 lengthOracle |
| 0xCB prefix as atomic (`381595b`) | CB-prefix 是 ISA-level atomic 不是 switch | (sub-decoder) | 4.4 spec prefix_to_set |
| Immediate baking (`da8cf91`) | block-JIT 不需要 runtime read_imm bus call | (compile-time const) | instruction_word packing |
| WRAM/HRAM inline write (`787a8e5`) | 99% RAM 寫付不該付的 extern cost | (region inline) | 4.6 + 4.5 |
| Cross-jump follow (`b9dd0dd`) | block 平均 1.0-1.1 instr → dispatch 攤不平 | (compile-time block extension) | detector follow + IsFollowedBranch flag |
| Block-local register shadowing V1 (`db9375c`) | state-struct access 阻擋 mem2reg | (alloca + drain) | 4.2 EmitContext routing |
| Cross-jump-into-RAM 解禁 (`377379c`) | RAM 區也可以是 block target，但要 SMC 配合 | (cross-region block) | env-gated；跟 SMC V2 一起 |

---

## 6. 代價與取捨 — 我們付了什麼

每個 timing 機制都有成本。**準確 timing 不是免費**——這節記錄取捨。

### 6.1 直接成本（runtime）

| 代價 | 哪些機制造成 | 影響 |
|---|---|---|
| **IR 變大** | sync 步驟在每個 IRQ-relevant store 加 BB；shadow alloca 在每個 block 加 entry-load + exit-drain；SMC inline notify 加 BB | LLVM compile 變慢（每 block IR size +30-100%）；x86 codegen 暫存器壓力大 |
| **Branch overhead** | sync exit / SMC notify / shadow drain 都加 conditional branch；fast path 多 1-2 cycle | Hot loop -1~5% MIPS |
| **Memory write 帶 piggyback** | 每個 WRAM/HRAM 寫加 SMC coverage check；MMIO 寫加 sync flag | Memory-heavy workload 影響大 |
| **重複編譯** | SMC invalidation 後 re-compile；ORC LLJIT 留舊定義不釋放 → memory leak (慢慢累積) | 長跑 + 重 SMC ROM 記憶體佔用增加 |
| **Block 切短** | HALT/STOP/EI/IRQ-relevant store 切 block 邊界；block 平均 1.0-1.1 instr 時 dispatch overhead 攤不平 | BIOS LLE bjit 反慢於 per-instr |

### 6.2 間接成本（design / engineering）

| 代價 | 影響 |
|---|---|
| **每個 timing primitive 都要 spec-level 介面** | 不能 hardcode 在 C# emitter；要設計 spec micro-op；新 CPU port 時要 review 哪些寫法用得到 |
| **Lockstep diff harness 變必要** | 任何 timing 改動要 `--diff-bjit=N` 跑驗證；測試循環變慢但避免 silent corruption |
| **Cycle drift 不可避免** | bjit 跟 per-instr 多少會有 1-cycle 級的 drift；只要不影響功能性 test 就接受 |
| **Debug 難度上升** | bjit 跑出來不對時，要先確認是 timing 還是邏輯；diff harness 找第一個 divergence point；root-cause 常常在 N 條 instr 之前 |
| **Code coverage** | 每個 sync exit / shadow drain / SMC notify 都是新 code path；T1 unit test 沒法完整覆蓋，要靠 T2 (screenshot / Blargg) 去驗 |

### 6.3 具體取捨記錄 — 我們選了什麼

| 抉擇 | 我們選的 | 沒選的 | 為何 |
|---|---|---|---|
| Cycle accuracy 等級 | **block boundary + sync exit** = 半 cycle-accurate | per-cycle (每 cycle 都對) | per-cycle 完全無 batch 收益；半 cycle-accurate 對 commercial GBA / GB ROM 足夠 |
| SMC 偵測方式 | **lazy** — bus.WriteByte 路徑做 1-byte counter check | eager — page-protection + segfault handler 攔 | 跨 OS 一致性 + 不依賴 OS-level page fault |
| MMIO catch-up 點 | **bus.WriteByte 那刻 callback** | block 結束才 batch tick | mid-block MMIO 寫 (e.g. LCDC) 後續讀 (e.g. STAT) 在同 block 要看到正確值 |
| IRQ delivery granularity | **MMIO write 觸發 sync flag 立刻退 block** | 等 block 跑完才檢查 | IRQ 晚到 N cycles 會破 PPU 同步 |
| Defer 機制 | **AST pre-pass 注入 phantom instruction** | runtime counter | counter 路徑加 register pressure；compile-time 注入 zero runtime cost |
| Shadow alloc 範圍 | **unconditional alloc 7 GPR + F + SP** | live-range analysis precise alloc | V1 簡單實作；perf -4% acceptable；V2 留 future 翻正 |
| Cross-jump-into-RAM | **env-gated default OFF** | 永遠開 / 永遠關 | 機制做了但跟 cycle drift 互動沒解；保 V1 default correctness |
| 處理 illegal opcode | **synthesize 1-byte NOP** | 等於 HALT / 整個 emulator throw | NOP 跟主流 emulator 相容；HALT 會 hang test framework |
| Block 平均長度目標 | **5-20 instr**（cross-jump follow 後） | 64 instr (max cap) | 64 太大 IRQ delivery 太慢；5-20 是甜蜜點 |

### 6.4 我們**沒做**的 timing 處理 — 認賠

| 沒處理 | 後果 | 為什麼不處理 |
|---|---|---|
| Real per-cycle bus contention | GBA 32-bit ROM read 應該多 1 cycle；spec cycle.form 沒區分 | spec 沒這層；commercial ROM 影響極小 |
| HALT bug (LR35902 IME=0 + IRQ pending) | 真實 HW PC 不前進 1 條；我們 advance | 罕見且 Blargg 沒測 |
| ARM7 SWI cycle stretch | 進 SVC mode 多幾個 cycle | jsmolka 沒測；GBA BIOS 都用 SVC，影響均勻 |
| OAM bug (LR35902 inc/dec HL during OAM DMA) | 視覺 corruption | sprite-heavy commercial ROM 才看得到 |
| 完整 STAT IRQ blocking | LCDC.0=0 → STAT IRQ 立刻 fire 行為 | conservative MMIO sync flag 已涵蓋大部分 |

**準則**：HW 行為 → spec → block-JIT 三者準確度逐步降。spec 級 (per-instr)
追得上 hardware 99% 行為；block-JIT 追得上 spec 99%。複合 ~98% correctness。
對 commercial GBA / GB 軟體足夠，cycle-accurate emulation 等級還差一截。

---

## 7. 跨 CPU 驗證 — 框架通用化的成果

要證明這套設計真的是 framework-level（不是 ad-hoc patches）：

### 7.1 一個 spec emit pipeline 跑兩種 ISA

ARM (32-bit fixed-width with cond field + pipeline-PC) 跟 LR35902 (8-bit
variable-width with prefix + flat-PC) **走同一個** BlockFunctionBuilder /
EmitContext / micro-op registry。差別只在 spec JSON 描述跟一個 lengthOracle
callback。

### 7.2 Pattern 跨 CPU 重用

| Pattern / Method | ARM 用嗎 | LR35902 用嗎 | 第三 CPU 候選 |
|---|---|---|---|
| Predictive cycle downcounting | ✅ | ✅ | ✅ 任何 CPU |
| MMIO catch-up callback | ✅ (GBA bus) | ✅ (GB bus) | ✅ 任何有 MMIO 的 CPU |
| Sync micro-op | ⚠️ 可用 (ARM 沒 LR35902 那麼多 IRQ-relevant write) | ✅ | ✅ 6502/65816 |
| Defer micro-op | (ARM 沒延遲 IRQ enable 但有 SVC 延遲) | ✅ EI | ✅ Z80/STM/RISC-V fence.i |
| Strategy 2 PC | ✅ (pc+8) | ✅ (pc+length) | ✅ 任何 pipeline-PC ISA |
| Variable-width detector | (ARM/Thumb fixed-width) | ✅ | ✅ 6502/8080/Z80 |
| Prefix sub-decoder | (ARM 沒) | ✅ CB | ✅ Z80 DD/FD/ED, x86 REX/VEX |
| Immediate baking | ✅ (ARM imm field) | ✅ (read_imm8/16) | ✅ 任何 imm 編碼 |
| Block-local shadow | ⚠️ (ARM 因 GepGprDynamic 未啟用，但機制可開) | ✅ | ✅ 任何 narrow-int CPU |
| SMC infrastructure | ⚠️ (GBA cart RAM 不太自我修改) | ✅ | ✅ 任何 cached + writable code 平台 |
| WRAM/HRAM inline write | (GBA 還沒做但同 mechanism) | ✅ | ✅ 任何 fixed memory map 平台 |
| Cross-jump follow | (ARM/Thumb 也可，沒做) | ✅ | ✅ 任何 unconditional + computable target ISA |

### 7.3 一個 lockstep diff harness 驗證兩 ISA

`--diff-bjit=N` 對 ARM jsmolka 跟 LR35902 Blargg 都直接 work；diff 邏輯
spec-driven，沒寫 ARM/LR35902 specific 比對程式碼。

### 7.4 一個 SpecCompiler / DecoderTable 處理變寬 + 定寬

DecoderTable 機制 (mask/match) 對 ARM 32-bit instruction word 跟 LR35902
8-bit opcode 都 work；instruction_word 是 uint，對 ARM 是完整 32 bit、對
LR35902 是 1-3 byte LE-packed (含 imm baking)。

---

## 8. 設計價值 — 這套機制換來什麼

把上面所有東西總結成 4 條：

1. **Timing 是 framework 的 first-class concern，不是 add-on**
   - 不是「先做 emulator 再加 timing」，而是設計初期就決定 timing
     primitives 怎麼進 spec、怎麼進 IR
   - 結果：每個新 CPU port 不用重新發明 sync / defer / SMC / region
     inline 的概念

2. **抽象化的成本是 spec design effort，不是 runtime cost**
   - micro-op 在 IR 階段消失（compile-time lowered）；runtime 看不到
     抽象化痕跡
   - 例：defer micro-op 在 AST pre-pass 攤平；sync micro-op emit 成
     conditional br；shadow alloca 被 mem2reg promote 成 SSA value

3. **取捨被明確記錄而非藏在 code 裡**
   - 每個 trade-off 都有對應 spec field / env var / commit message
   - 例：cross-jump-into-RAM 是 `APR_CROSS_JUMP_RAM=1` 開關；future
     reader 看 env var 名就知道有這個維度
   - 不是「為什麼 block_size 限 64」這種埋在 magic constant 的決定

4. **驗證機制是 framework 的一部分**
   - lockstep diff harness、T2 8-combo screenshot matrix、Blargg
     suite 都是 framework infra
   - 任何 timing 改動都要過這三關；correctness regression 機率大幅降低

---

## 9. 給未來 me 的提醒

1. **加新 timing 機制前先問**：歸到 Pattern A/B/C 的哪個？沒有的話可能
   設計不對

2. **新 micro-op 進 spec 前先問**：N 個 CPU 會用？只有一個 CPU 用就先不
   抽象，等第二顆 CPU 也需要才升 framework

3. **任何 block-JIT 的「為了 timing 加 extern call」要量** — extern call
   ~10ns，整個 block-JIT 收益可能就 1 個 extern call 抵消掉

4. **遇到 cycle drift 不要急著修**：先用 diff harness 確認是真 drift 還是
   compile-time 路徑分歧；很多 drift 是 LLVM optimisation 微調造成的（不
   影響 functionality）

5. **SMC 跟 cross-jump 跟 block-local cache 三個機制互動複雜** — 同時改
   兩個就難 debug；one-at-a-time 改 + 全套 verification

6. **準確 timing 跟 framework 通用性會打架**：特定 CPU 的 quirk 想做極
   準會逼你 hardcode；忍住，spec 化它，承擔點 perf 損失

---

## 10. Reference

- [`MD/design/12-gb-block-jit-roadmap.md`](/MD/design/12-gb-block-jit-roadmap.md) — GB block-JIT 整體 roadmap
- [`MD/design/13-defer-microop.md`](/MD/design/13-defer-microop.md) — defer micro-op 設計
- [`MD/design/14-irq-sync-fastslow.md`](/MD/design/14-irq-sync-fastslow.md) — sync micro-op + bus extern split
- [`MD/performance/202605040000-gb-block-jit-p0-complete.md`](/MD/performance/202605040000-gb-block-jit-p0-complete.md) — P0 完工
- [`MD/performance/202605041800-gb-block-jit-p1-complete.md`](/MD/performance/202605041800-gb-block-jit-p1-complete.md) — P1 完工
- `tools/knowledgebase/message/20260503_*.txt` — Gemini 諮詢紀錄
  （QEMU TCG / FEX-Emu / Dynarmic / mGBA / Dolphin 設計參考）
