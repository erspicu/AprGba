# 2026-05-08–09 兩日衝刺：N 系列完整紀錄

> **Status**：work-log / sprint summary（2026-05-09 末）
> **Scope**：把 2026-05-08（N0c 開始）到 2026-05-09 末的所有架構性 +
> 功能性產出整理成單一可查的檔案。前置基礎是 Phase 0–9 + Phase 5.8
> emitter refactor；本 sprint 把 framework 從「ARM7TDMI + LR35902 兩
> 顆 CPU」推到「**ARM7TDMI + LR35902 + Ricoh 2A03 三顆 CPU + ~85%
> declarative runtime ratio**」。
>
> **目標讀者**：(a) 想快速知道「這兩天做了什麼」的接手者；(b) 撰寫
> paper / project closeout 時要引用具體 commit 跟 metric 的場合；
> (c) 為後續第 4 顆 CPU 移植留作 process post-mortem。

---

## 1. 總覽 metric

| 指標 | 數值 |
|---|---:|
| Sprint 期間 | 2026-05-08 → 2026-05-09 |
| Commit 數 | **42** |
| File-touches（累積） | 183 |
| Unique files modified/created | 136 |
| Lines added | **+15,424** |
| Lines deleted | −731 |
| Unit test growth | **365 → 455**（+90 tests, 0 skipped） |
| Declarative ratio growth | **~50% → ~85%** |
| New CPU integrated | Ricoh 2A03 (NES 6502, 第三顆) |
| New design docs | **5** (#18-22) + 1 sprint summary（本 doc） |
| New performance docs | **5** (N0–N11 perf logs) |
| MD_EN 全英文鏡像 | 全 sync（10 新 + 8 同步更新） |

---

## 2. 架構性產出（framework 層）

### 2.1 Block-JIT state abstraction（N1.B'，commit `9c32f3b`）

把「per-instr vs block-JIT 模式差異」從 emitter source 移到 framework
層，用 LLVM 標準 `alloca + mem2reg` idiom 取代既有的 `PipelinePcConstant`
hardcode 路徑。Emitter 不再需要知道自己在哪個模式 — 同一份 emitter source
跑兩條 backend，一條最終 SSA、一條 state-buffer 直存。

- 新增介面：`IStateSlotProvider`（`StateBufferProvider` / `AllocaSlotProvider`）
- `EmitContext.GepGpr` / `GepStatusRegister` 走 provider；in-block 自動
  redirect 到 alloca slot
- LLVM `mem2reg` pass 在 epilogue 之後自動把 alloca 提升成 SSA register
- 設計 doc：`MD/design/18-block-jit-state-abstraction.md`

**B'.7 audit 結論**：保留既有 `PipelinePcConstant` / `CurrentInstructionBaseAddress`
作為 framework 一級 feature（提供 alloca 不能取代的編譯期 imm 抽取
+ region inline + sync-exit PC pre-write 三項正交優化）。

### 2.2 Declarative JIT policy + CPU/Machine spec split（N2，commit `189f8c0` 等）

把 spec 從「單一 cpu.json」拆成：
- **CpuSpec** (`spec/<arch>/cpu.json`) — 純 ISA 規格（register file、encoding、
  micro-op steps、isa_metadata）
- **MachineSpec** (`spec/machines/<system>.json`) — 板級規格（memory regions、
  interrupt vectors、CPU 引用）

新增的 framework primitives：
- `MachineSpec` + `MachineSpecLoader`
- `Immediate` (block-JIT 編譯期 imm bake)
- `pageShift` (per-CPU SMC coverage page 大小)
- `forces_end_of_block` (volatile region hint)
- `IsaMetadata`（`endianness` / `cycles_per_spec_unit` / `pc_update_policy`）
  並 wire 到三顆 CPU
- 設計 doc：`MD/design/19-declarative-jit-policy.md`、
  `MD/design/20-adding-a-new-cpu.md`

### 2.3 Spec-driven runtime（N3，commits `6c09585` / `03f18bb` / `0db6ed8`）

NES bus / interrupt vector / cycle table 全改 spec-driven：
- N3.1 — NES interrupt vectors 從 `nes-ntsc.json` 載入
- N3.2 — NES memory bus dispatch 從 `MachineSpec.memory_regions` 表驅動
- N3.3 起初標 BLOCKED — spec format 缺 per-(mnemonic, addressing-mode)
  cycle 維度
- 設計 doc：`MD/design/21-spec-driven-runtime.md`

### 2.4 Memory spec v2（N4，commits `914cda0` → `4367154`，6 commits）

吸收 Gemini 7-point critique，把 N3.2 暴露的所有架構 issue 一次處理：
- N4.0 設計 — `MD/design/22-memory-spec-v2.md`
- N4.1 — schema v2 fields + parser（handler / allowed_widths / readable / writable / volatile / observers / wait_states / unmapped_behavior）
- N4.2 — handler registry pattern；`ClassifyRegion` 改用 v2 explicit
  `Handler` 取代 `side_effects[0]` 隱式 magic
- **N4.3 — O(1) page-table dispatch**（32-byte pages, 2048 entries × 8B
  struct = 16KB）取代 N3.2 linear scan
- N4.4 — handlers 接 region-local **offset** 而非絕對 addr；PPU 完全解
  耦於 0x2000 起點
- N4.5 — GBA + GB DMG spec 升級 v2（allowed_widths / wait_states /
  explicit handler 全 declared）
- N4.6 — 3-run perf bench + closeout doc

**Perf 結果**：legacy backend 從 N3.2 退步的 1.57 MIPS 救回 1.65 MIPS
（recover ~5pp 的 7pp regression），距 N1 baseline 1.69 MIPS 只差 ~2%。

### 2.5 通用 lockstep diff toolkit（N5，commit `eb2885c`）

把既有 NES-only `Program.cs` 內手寫 lockstep loop 抽成 framework-level
util：
- `ISteppableCpu` interface — Name / Step / Snapshot / 可選 ReadByteFromBus
- `ICpuStateSnapshot` — Pc + Registers (string→ulong) + CycleCount
- `LockstepDiff.Run` — 兩 CPU side-by-side, halt-condition / divergence
  / fault / max-step 任一觸發即停
- `LockstepResult` 含 trail buffer + diverged-fields list +
  `FormatReport()` helper
- NES adapter (`NesLockstepAdapter`) wrap 既有 `INesCpuBackend`
- 兩個 unit test：(a) 真 workload 跑 500 instr 證 toolkit 不誤判
  no-divergence；(b) toy CPU 故意第 3 步分歧證 toolkit 真的 catch 得到

未來第 4 顆 CPU + GB / GBA 的 lockstep 全可走同一 toolkit。

### 2.6 N4 closeout §3 follow-ups（N7，commit `9ab55a0`）

三個 query API 上線（沒動 hot path、純 expose spec-declared 值）：
- `NesMemoryBus.TryGetHostPointer(addr, out arr, out offset)` — block-JIT
  fastmem 用，WRAM hit 給 host array+offset，IO/mapper 區段返回 false
- `NesMemoryBus.IsAccessWidthAllowed(addr, widthBits)` — query 是否
  在 spec.allowed_widths 內
- `NesMemoryBus.GetWaitStates(addr)` — placeholder（NES 無 wait state）

PageEntry 加 1-byte `AllowedWidthsMask`（cache-line 仍 8 bytes）；
6 個 query API 測試。

### 2.7 ARM page-table dispatch for GBA（N8，commit `c845b04`）

GbaMemoryBus.Locate 從 switch-based dispatch 改 **256-entry page table
indexed by `addr >> 24`**：
- `GbaPageEntry` 含 Region kind + MirrorMode（None / PowerOf2 / Modulo）
  + Base / Size / WrapMask
- BIOS 16K + IO 1K = sub-page bounds-check
- EWRAM/IWRAM/Palette/OAM/ROM 6 頁 = power-of-2 mirror
- VRAM 96K = 唯一 modulo path
- 5 N8 tests 含 cross-validate page table 對齊 spec/machines/gba.json

`GbaMemoryBusTests` 17 個原 test 全 PASS；jsmolka arm.gba smoke 0.97 MIPS
保持。

### 2.8 Spec format dynamic cycle penalties（N9，commit `45ce9c5`）

Cycles record 加兩個 optional int field：
- `extra_when_taken: int` — 條件分支命中
- `extra_when_page_cross: int` — 載入 addressing-mode 跨 page

8 個 6502 conditional branches (BPL/BMI/BVC/BVS/BCC/BCS/BNE/BEQ) 全部
declarative declare +1/+1。Schema-as-documentation scope；runtime IR step
仍動態應用，但 spec 已成 source-of-truth，未來 runtime 可 read 而非 hardcode。

### 2.9 allowed_widths runtime debug enforcement（N10，commit `6c88d45`）

GbaMemoryBus 加 `EnforceAllowedWidths` debug-mode flag。Default OFF（zero
hot-path cost）。當 ON 時，spec ↔ runtime invariant 強制：
- VRAM/Palette/OAM/IO 8-bit 寫 → throw
- EWRAM/IWRAM/BIOS/cart_rom 全 widths 接受
- 8 個 N10 unit tests 證 default-OFF 不影響既有行為 + ON 時 invariant 強制

第一個把 spec.allowed_widths 真正驅動 runtime check 的點。

### 2.10 fastmem block-JIT integration（N11，commit `392c021`）

證明 N7 `TryGetHostPointer` query API 能 propagate 到 LLVM IR fastpath：
- 加 `Mos6502WramBase` extern
- `Mos6502Emitters.BusRead8` 可選地 emit inline range-check (addr<0x2000)
  + GEP-load from `_wram[]`
- 由 env var `APR_MOS6502_FASTMEM=1` gate；default OFF
- NesJsonCpu 構造時 pin `_bus.Wram[]` 並 bind extern

**Empirical 結果**：default-OFF 0.81 MIPS（baseline 持平）；ON 0.79 MIPS
（−2% — cond-br + phi-merge 開銷壓過 extern call savings on 6502）。
**Foundation 證明**：N7 API 真的能驅動 JIT inline path；6502 perf-neutral
但其他 architecture (CISC + 長讀) 受惠潛力留給未來。Writes 仍走 bus
保 SMC notify。

---

## 3. 功能性產出（concrete shippable features）

### 3.1 第三顆 CPU：Ricoh 2A03 / NES（N0–N1，10 commits）

從零起新增 `AprNes.Cli` harness：
- N0 — 2A03 JSON spec（`spec/cpu/2a03/cpu.json` + 7 個 group + unofficial）
  256 opcode decoder coverage
- N0b — LegacyCpu (Ricoh2A03Cpu) wired 到 NesMemoryBus + nestest PASS
  at PC=$C66E
- N0c — NesPpu + screenshot output；MMC1 + PPU NMI/mirroring → blargg
  cpu_test5 PASS
- N1.A.1 — 通用 `update_sign` / `push8` / `pop8` micro-op 加進 framework
- N1.A.2-6 — `Mos6502Emitters` + spec.steps（JSON-driven 6502 語義）
- N1.A.7 — NesJsonCpu per-instr backend (`--backend=json`) — nestest PASS
- N1.A.8 — JsonCpu 通過 blargg cpu_test5（修 LAX zp,Y/abs,Y + SHY/SHX）
- N1.B'.5 — NES block-JIT (`--backend=json-block`) — blargg cpu_test5 PASS

### 3.2 NES CLI 介面對齊既有

`AprNes.Cli/Program.cs`：
- `--rom=<path>` / `--info` / `--run` / `--nestest` 模式
- `--backend=legacy|json|json-block`
- `--diff` (legacy vs json) / `--diff-block` (json vs json-block) lockstep
- `--start-pc=<hex>` / `--max-cycles=N` / `--expect-pc=<hex>`
- `--screenshot=<path>` 輸出 PNG
- 一致對齊 GB / GBA harness 慣例

### 3.3 三 backend 全 PASS 驗證

| Backend | nestest | blargg cpu_test5 |
|---|---|---|
| legacy (Ricoh2A03Cpu hand-coded) | ✓ PC→\$C66E | ✓ PC→\$8003 (all 11 subtest) |
| json per-instr (NesJsonCpu) | ✓ PC→\$C66E | ✓ PC→\$8003 |
| json-block (NesJsonCpu + block-JIT) | ✓ PC→\$C66E | ✓ PC→\$8003 |

---

## 4. Spec format 擴張總表

| 新增 field | 位置 | 用途 |
|---|---|---|
| `MachineSpec.spec_version` | machine root | v1/v2 區分 |
| `MachineSpec.unmapped_behavior` | machine root | gap 行為（zero / ignore / fault / last_bus_value） |
| `MemoryRegion.handler` | per region | 顯式 routing key（取代 side_effects[0] magic） |
| `MemoryRegion.allowed_widths` | per region | 8/16/32-bit access 約束 |
| `MemoryRegion.readable` / `writable` | per region | 顯式 permission |
| `MemoryRegion.volatile` | per region | 取代 v1 `forces_end_of_block` |
| `MemoryRegion.observers` | per region | metadata-only side-effect tags |
| `MemoryRegion.wait_states` | per region | optional cart timing |
| `Cycles.table` (`CycleTable`) | per instruction | per-(mnemonic, addressing-mode) cycle 數 |
| `Cycles.extra_when_taken` | per instruction | 條件分支 +1 |
| `Cycles.extra_when_page_cross` | per instruction | 載入跨 page +1 |
| `IsaMetadata.endianness` / `cycles_per_spec_unit` / `pc_update_policy` | cpu root | declarative arch metadata |

## 5. Performance 跨 milestone

ROM: `test-roms/blargg_nes_cpu_test5/cpu.nes`，3-run avg, MIPS

| Backend | N1 baseline | N3.4 | N4.6 | N3.3-finally | **N9-end (now)** |
|---|---:|---:|---:|---:|---:|
| legacy           | 1.69 | 1.57 | 1.65 | 1.64 | **1.66** |
| json (per-instr) | 0.83 | 0.81 | 0.82 | 0.83 | **0.83** |
| json-block       | 0.78 | 0.80 | 0.80 | 0.82 | **0.81** |

**結論**：跨 4 milestone 累積 — legacy 距 N1 baseline ~3%，per-instr 持
平，block-JIT 反超 N1 約 5%。**每一步都同時提升 declarativity，perf
卻沒退步**。

---

## 6. Declarative ratio 量化

| Layer | N2 後 | N3 後 | N4 後 | **N3.3-finally / N9-end** |
|---|---|---|---|---|
| CPU register file | 100% | 100% | 100% | 100% |
| Instruction encoding | 100% | 100% | 100% | 100% |
| Instruction semantics | 95% | 95% | 95% | 95% |
| Cycle multiplier | 100% | 100% | 100% | 100% |
| Block-JIT cycle accounting | 90% | 90% | 90% | 90% |
| **Memory bus dispatch** | 0% | 80% | 95% | 95% |
| **Region routing** | – | 70% | 95% | 95% |
| Interrupt routing | 30% | 90% | 90% | 90% |
| **Per-instr cycle table** | 60% | 60% | 60% | **100%** |
| Mapper logic | 0% | 0% | 0% | 0% (escape hatch — by design) |
| PPU/APU register handlers | 0% | 0% | 0% | 0% (escape hatch) |
| Access widths / wait states | 0% | 0% | declared | declared + GBA enforced |
| Dynamic cycle penalties | 0% | 0% | 0% | declared (N9) |
| **加權平均** | **~50%** | **~70%** | **~78%** | **~85%** |

「真正 fundamental escape hatch」估計：原本 30% → 縮到 **約 10%**
（mapper state machine、PPU/APU heavy side effects、NMI procedural
sequence ~500 LOC C#）。

---

## 7. 文件 / 知識資產產出

### 7.1 新增 design docs（5 份，~1645 lines）

| Doc | 內容 |
|---|---|
| `MD/design/18-block-jit-state-abstraction.md` | alloca + mem2reg refactor 設計 + B'.7 audit |
| `MD/design/19-declarative-jit-policy.md` | declarative JIT policy + CPU/Machine spec split |
| `MD/design/20-adding-a-new-cpu.md` | 加新 CPU SOP（含 ARM/LR35902/2A03 三例對照） |
| `MD/design/21-spec-driven-runtime.md` | N3 spec-driven runtime + N3.3 BLOCKED→RESOLVED 紀錄 |
| `MD/design/22-memory-spec-v2.md` | N4 memory spec v2 + N7-N11 follow-up status table |

### 7.2 新增 performance docs（5 份，862 lines）

| Doc | 內容 |
|---|---|
| `202605091229-nes-jsoncpu-per-instr-baseline.md` | NES JsonCpu per-instr baseline before block-JIT |
| `202605091559-nes-blockjit-vs-perinstr.md` | NES block-JIT vs per-instr first-cut measurement |
| `202605091804-n3-declarative-ratio.md` | N3 closeout — declarative ratio + bench |
| `202605091900-n4-memory-spec-v2.md` | N4 closeout — memory spec v2 + perf bench |
| `202605092000-n33-full-spec-cycles.md` | N3.3-finally closeout — full spec-driven cycle table |

### 7.3 既有 docs 同步更新（10 份 MD + 8 份 MD_EN sync）

更新 `MD/design/00-overview.md`、`02-architecture.md`、`03-roadmap.md`、
`15-timing-and-framework-design.md`、`16-emulator-completeness.md`、
`22-memory-spec-v2.md`、`MD/note/framework-emitter-architecture.md`、
`MD/note/framework-future-extensions-and-vision.md`、
`MD/process/01-commit-qa-workflow.md`、`README.md` — 從「2 顆 CPU /
360 tests / 2026-05-05 state」推到「3 顆 CPU / 455 tests / 2026-05-09
state + N0-N11 系列」。

### 7.4 MD_EN 全英文鏡像 sync（commit `4b9e7a1`）

- 新增 10 個英文 doc（5 design + 5 perf）= ~2741 lines 英文翻譯
- 同步更新 8 個既有英文 doc 跟 MD/ 對齊
- Full parity：每個 MD 文件都有 MD_EN 對應（exception：2 個 .tsv 純資料 trace）
- Zero CJK 在英文 prose（grep U+4E00-U+9FFF 通過）

---

## 8. Test 覆蓋成長

| 指標 | N0 開始前 | Sprint 末 | Δ |
|---|---:|---:|---:|
| Unit tests (T1) | 365 | **455** | +90 |
| Skipped tests | 1 (N3.3 documenting) | **0** | −1 |
| 三 backend nestest PASS | 0 | **3** | +3 |
| 三 backend blargg cpu_test5 PASS | 0 | **3** | +3 |
| MachineSpec coverage tests | 6 | 9 | +3 |
| GbaMemoryBus N8 cross-spec tests | 0 | 5 | +5 |
| GbaMemoryBus N10 width tests | 0 | 8 | +8 |
| NesMemoryBus N7 query tests | 0 | 6 | +6 |
| LockstepDiff toolkit tests | 0 | 2 | +2 |

---

## 9. Commit-by-commit timeline（42 commits）

按時間順序（早到晚）：

```
2026-05-08
  7ce81a4  feat(N0c): NesPpu + screenshot output for apr-nes CLI

2026-05-09
  a8ddbef  feat(N0c): MMC1 + PPU NMI/mirroring → blargg cpu_test5 PASS
  75f086d  feat(N1.A.1): generic update_sign + push8/pop8 micro-ops
  dc1bdd9  feat(N1.A.2-6): JSON-driven 6502 semantics — Mos6502Emitters + spec.steps
  d5a18e9  feat(N1.A.7): NesJsonCpu per-instr backend + --backend=json — nestest PASS
  cd0adab  feat(N1.A.8): JsonCpu passes blargg cpu_test5 — fix LAX zp,Y/abs,Y + SHY/SHX
  cb3d0ad  chore(nes): strip dev-phase debug prints + dbgWrite scaffolding
  b4f1787  docs(perf): NES JsonCpu per-instr baseline before block-JIT
  69d668d  docs(design): #18 block-JIT state abstraction — alloca + mem2reg refactor
  9c32f3b  refactor(framework): block-JIT state via alloca + mem2reg (#18)
  9f2f4a2  feat(N1.B'.5): NES block-JIT — blargg cpu_test5 PASS
  e45d7ee  docs(perf): NES block-JIT vs per-instr first-cut measurement
  a9c8795  docs(design): #18 — N1 closeout + B'.7 audit findings
  a576f97  docs(design): #19 — declarative JIT policy + CPU/Machine split
  189f8c0  feat(N2.1): framework primitives — MachineSpec / Immediate / pageShift / forces_end_of_block
  20ad897  feat(N2.2): ARM7TDMI / GBA — machine spec + isa_metadata authored
  b6c6080  feat(N2.3): LR35902 / GB DMG — machine spec + isa_metadata authored
  e06bd0e  feat(N2.4): Ricoh 2A03 / NES — isa_metadata + Mos6502 imm-bake fast path
  ed23767  feat(N2.5): wire isa_metadata + contributor guide
  94d1611  docs(design): #21 — spec-driven runtime (N3.0 design)
  6c09585  feat(N3.1): NES interrupt vectors loaded from MachineSpec
  03f18bb  feat(N3.2): NES memory bus dispatch via MachineSpec region table
  0db6ed8  docs(N3.3): document spec-vs-oracle cycle granularity gap (BLOCKED)
  8e12ec1  docs(perf): N3 closeout — declarative ratio + bench
  b906fb8  docs(design): #22 — memory spec v2 (handler registry + offset mirror + page table)
  914cda0  feat(N4.1): memory spec v2 — schema fields + parser + nes-ntsc.json upgrade
  81103d8  feat(N4.2): handler registry + ClassifyRegion uses v2 explicit Handler
  61066a2  feat(N4.3): O(1) page-table dispatch in NesMemoryBus
  101ae98  feat(N4.4): handlers receive region-local offset, not absolute addr
  abad579  feat(N4.5): upgrade gba.json + gb-dmg.json to memory spec v2
  4367154  docs(N4.6): closeout — perf bench (3-run) + N3.4 invalidations
  027fe79  feat(N3.3): break per-(mnemonic,addressing-mode) cycle blocker
  eb2885c  feat(N5): generic lockstep diff toolkit
  9ab55a0  feat(N7): N4 closeout §3 query APIs — fastmem + width + wait_states
  c845b04  feat(N8): GbaMemoryBus page-table dispatch — ARM 1-level for GBA
  c806358  feat(N3.3-finally): full spec-driven cycle table — drop hardcoded oracle
  23aafe7  docs(N3.3-finally): 3-run perf bench + closeout doc + invalidations
  392c021  feat(N11): fastmem block-JIT integration — opt-in inline WRAM read path
  6c88d45  feat(N10): allowed_widths debug-mode enforcement on GbaMemoryBus
  45ce9c5  feat(N9): spec format — declarative dynamic cycle penalties
  5ddee13  docs: sync MD with 2026-05-09 N0-N11 reality (3rd CPU + ~85% declarative)
  4b9e7a1  docs(MD_EN): full English mirror sync — add 10 new docs + update 8 stale
```

---

## 10. 學術洞察

### 10.1 N3.3 BLOCKED → RESOLVED — framework 上限其實沒到

N3 closeout 原本把「per-(mnemonic, addressing-mode) cycle granularity」
標為 BLOCKED 跟「framework 抽象到頂、需要 spec format 結構性改造」，
悲觀估「framework declarative 上限 ~70%」。

實際做完 N4–N11 之後回頭破解：12 行 C# resolver (`CycleTable.Resolve`) +
mechanical 填表 (cycle counts copy 自既有 oracle table)。整個 N3.3-finally
**一晚做完**。

**結論**：原本標 BLOCKED 的「結構性 limit」其實只是「**還沒花時間補
schema**」。Framework declarative 上限從原本 ~70% 推到實證 ~85%。

剩下「真正 fundamental escape hatch」估計只有 **~10%**：
- Mapper state machine（5-bit shift register / bank select）
- PPU/APU heavy side effects（vram_addr increment、VBL flag、NMI fire、OAM DMA stall）
- NMI procedural sequence（push PC.hi/lo/P 的固定順序）

這三類本質上跟 ECMAScript 不能取代 Verilog 是同一個 fundamental 道理 —
**state machine 跟 procedural sequence 在 declarative 抽象層真的沒有
槓桿效益**。

### 10.2 加 CPU 邊際效益遠超原本估計

N3 收尾原本判定「Phase 4.5 GB 驗證已涵蓋 framework 通用性的關鍵未驗證
面，再加更多 CPU 邊際效益遞減」。

實際做了 NES（第 3 顆）後發現完全相反 — 暴露了 **5+ 個 spec format / 通
用 pattern 缺口**：
1. `cycles.table` per-(mnem, addr-mode) cycle granularity
2. Memory bus declarativity（vector / regions / handler routing）
3. Page-table dispatch 通用化（從 NES 16-bit 推到 GBA 32-bit）
4. Handler registry pattern（取代 side_effects[0] magic）
5. allowed_widths / wait_states / volatile 等 schema fields
6. 通用 lockstep diff toolkit (`ISteppableCpu` interface)

每補一個都直接提升 declarative ratio。**第 3 顆 CPU 是「framework 的
通用性壓力測試」最有效的工具** — 比寫第 4 個 doc 或 refactor 第 N 次
都有用。

### 10.3 「同套 framework」的 perf 取捨已可量化

N3.2 第一次 spec-driven dispatch 7% perf regression 引發焦慮，但 N4.3
page-table O(1) 架構救回；N4.4 offset semantics 額外 ~2%；N3.3-finally
+ N9 schema 擴張不影響 perf。

跨 4 milestone 累積：
- legacy backend：1.69 → 1.66 MIPS（**−2%**）
- json per-instr：0.83 → 0.83 MIPS（**0%**）
- json-block：0.78 → 0.81 MIPS（**+4%**）

「Declarativity 上升必導致 perf 下降」這個 folklore 在 framework 設計
層級**不成立** — 用對 data structure（page-table dispatch）+ 標準 LLVM
idiom（mem2reg）就能讓兩個目標同時達成。

---

## 11. 剩餘任務（已盤點）

按優先序：

| 任務 | 性質 | 估計 | 觸發條件 |
|---|---|---|---|
| **第 4 顆 CPU = Intel 8086** | 框架壓力測試 + 學術價值 | 大 — 新 emitter + spec + harness | 用以前寫過的 emulator 當 reference oracle |
| ARM 2-level page table sub-grain | perf / coverage | 小 | 等 NDS dual CPU 或 cart sub-page tricks 才需要 |
| Spec format 進階 | declarative 完善度 | 中 | per-(mode) page-cross +1 declarative；branches taken-cycle runtime read spec |
| fastmem inline path 跨 architecture 啟用 | perf 優化 | 中 | 等其他 CPU profile 顯示能受惠 |
| Allowed widths runtime hot-path enforce | validation tool | 小 | 想加 strict-validation mode 時 |
| Wait states GBA cart timing 從 spec read | declarative 完善度 | 中 | GBA timing-accurate 升級時 |

最高學術價值 = **第 4 顆 CPU**。其他都是現有功能的 incremental 改進。
**根基已打完**，加 8086 直接 follow `MD/design/20-adding-a-new-cpu.md`
SOP 即可。

---

## 12. 設計概念（Architectural concepts）

把 sprint 用到的關鍵設計觀念抽出來、命名、解釋為什麼這樣設計。

### 12.1 Spec as source-of-truth（規格即真理）

**觀念**：runtime 行為由 declarative spec 驅動而非 hardcode。任何 CPU /
machine-level 性質能寫進 JSON 就寫進去；C# 只負責「動詞」（emitter +
runtime helpers），不負責「資料」（regions / vectors / cycles / widths）。

**為什麼**：
1. 加新 CPU 工作量收斂到「寫 spec + 必要時加 ~5-10 個 L3 op」級別
2. Declarative metadata 同時是「runtime 配置」+「文件」+「可被 tooling
   靜態分析的資料」
3. 規格改動 review 比 code 改動 review 容易很多（diff 是 data 不是
   semantics）

**實踐**：N3 把 vectors / bus regions 搬進 `MachineSpec`；N3.3 把 cycle
table 搬進 `CycleTable`；N9 把 dynamic cycle penalties 搬進 `Cycles`
extra fields。

### 12.2 Two-tier spec：CpuSpec vs MachineSpec

**觀念**：spec 拆兩層 — 「ISA 規格」（純 CPU 性質）跟「板級規格」（特
定機種的 memory map / interrupt vectors / cart 設定）。

**為什麼**：同一顆 CPU 可能裝在不同機種（如 6502 在 NES / Apple II /
C64；ARM7TDMI 在 GBA / NDS）；ISA 部分可重用，板級部分各機種不同。

**實踐**：`spec/cpu/2a03/cpu.json`（ISA） + `spec/machines/nes-ntsc.json`
（板級）；`spec/cpu/arm7tdmi/cpu.json` + `spec/machines/gba.json`。
詳見 `MD/design/19-declarative-jit-policy.md`。

### 12.3 Page-table dispatch（替代 switch / linear scan）

**觀念**：memory bus 區段查詢用 array-indexed lookup 取代 switch /
linear scan。Index 是 `addr >> page_shift`，每個 entry 含 region kind +
base + mirror mask + 其他 metadata。

**為什麼**：
- O(1) 查詢，hot path 1 array load + 1 switch on enum
- 同一個架構 scale 到 NES (16-bit / 32-byte page = 2048 entries) 跟 GBA
  (32-bit / 16 MB page = 256 entries)
- 編譯時從 spec build；runtime 不重算
- 容易擴展（加 entry field 不動 hot-path shape）

**實踐**：N4.3 NesMemoryBus 用 32-byte page；N8 GbaMemoryBus 用 16 MB
page。兩者跑同一個 mental model 但 page size 因 addr space 大小不同。

**設計選擇**：選 page size 依「最小不切分 region 的對齊」 — NES 最小
region 是 apu_io 32 bytes 起在 0x4000，所以 page-shift=5 (32 bytes)；
GBA 16 MB 對齊清乾淨，page-shift=24。

### 12.4 alloca + mem2reg pattern（LLVM 慣用 idiom）

**觀念**：block-JIT IR 在 entry block 為每個 state field 配 alloca、
prologue 把 state buffer 值載進 alloca、emitter 透過 ctx.GepGpr 拿 alloca
pointer 操作、epilogue 把 dirty alloca 寫回 state buffer。最後跑 LLVM
標準 `mem2reg` pass 自動把 alloca 提升成 SSA register。

**為什麼**：
- Emitter source 跟 per-instr 模式完全一致 — 不需要分流「block-JIT 路
  徑要從 SSA 讀，per-instr 路徑要從 state buffer 讀」
- LLVM 標準 idiom — 跨 architecture 跨 compiler 都驗過
- mem2reg 跟 SSA construction 是 LLVM 強項，效果跟手寫 SSA 等價
- 加新 CPU 時不用重新發明 state-access mode-agnostic 機制

**實踐**：N1.B' 的 `IStateSlotProvider` + `AllocaSlotProvider`；
`EmitContext.GepGpr` / `GepStatusRegister` 走 provider；
`BlockFunctionBuilder` 在 entry block 注入 prologue / 在 epilogue
sync 回去。詳見 `MD/design/18-block-jit-state-abstraction.md`。

### 12.5 Handler registry 取代 implicit string magic

**觀念**：MemoryRegion.handler 顯式 string key，runtime 透過
`bus.RegisterHandler(name, reader, writer)` 註冊；spec 改 handler 不
動 bus core code。

**為什麼**：N3.2 用 `side_effects[0]` 隱式決定 dispatch 是 hidden
semantic rule — 規格 author 容易踩坑，且加新 region kind 要改多處。
Explicit string + registry 把 routing 變成可 traceable 的資料。

**實踐**：N4.2 引入；N4.5 把所有三機種 (NES / GBA / GB-DMG) 的 spec
都升級用 explicit handler。

### 12.6 Offset-based mirror semantics（解耦 region 從固定 base）

**觀念**：handler 接 region-local **offset** (0..region_size-1) 而非絕
對 addr。Mirror mask 套 offset、不套絕對 addr。

**為什麼**：
- PPU register handler 不需要知道自己住在 0x2000；moving the PPU base
  in spec 不需要動 handler code
- mirror_mask 變乾淨（NES PPU 從 `"0x2007"` 收成 `"0x0007"` — 只保留
  低 3 bit）
- 跨 region kind 的 mirror 邏輯統一

**實踐**：N4.4 全 NES 切到 offset semantics；GB / GBA 既有 hardcoded
bus 暫保留（它們本來就 region-local 處理）。

### 12.7 Lockstep diff 作為 framework primitive

**觀念**：把「兩 backend 比對 step-by-step」抽成
`ISteppableCpu` interface + `LockstepDiff.Run` toolkit。任何新 CPU
backend 實作 interface 即可使用、不需重新發明 trail-buffer / divergence-
detection / halt-condition 機制。

**為什麼**：correctness validation 是 framework 開發的核心活動；不
generic 化會每個 CPU 都重寫一遍 diff loop。

**實踐**：N5；NesLockstepAdapter wrap INesCpuBackend；toy CPU 故意分
歧 test 證 toolkit 真的能 catch。

### 12.8 三類 fundamental escape hatch（明確邊界劃定）

**觀念**：framework declarative 抽象有明確邊界。**State machine** /
**heavy side effects** / **procedural sequences** 三類本質上不是 framework
能涵蓋的範疇 — 由 hand-written C# 接管，**這不是 framework limitation
而是 design**。

| 類型 | 範例 | 為什麼留 C# |
|---|---|---|
| State machine | Mapper000/001 (5-bit shift register / bank select) | 純 procedural，沒 leverage |
| Heavy side effects | NesPpu register write (vram_addr inc / VBL flag / NMI fire / OAM DMA stall) | per-bit semantics 全是 ad-hoc |
| Procedural sequence | NMI handler push order (PC.hi → PC.lo → P → set I) | 6 個 step 全 fixed order，declarative 表達等於資料表跟 6 行 code 一樣 |

**實踐**：明確列在 `MD/performance/202605091804-n3-declarative-ratio.md`
§2.2 + `MD/performance/202605092000-n33-full-spec-cycles.md`。劃清楚邊
界後，剩下「真正能 declarative」的部分推到極限是合理的。

### 12.9 Schema-as-documentation（declared 但 runtime 不一定 read）

**觀念**：spec 可以 declare runtime 暫時還沒 read 的 metadata。先把
資訊放進 spec，runtime 可以 incremental 升級去 honor。

**為什麼**：
- 規格的可讀性 / 完整性比 runtime 強制 enforcement 更重要
- 未來想加 strict-validation mode / debug mode 時資料已備齊
- Schema 演化可以做在「只加 field 不破 backwards compat」

**實踐**：N4.5 GBA / GB-DMG spec declare allowed_widths / wait_states
但 runtime 沒 enforce；N7 加 query API；N10 GBA 上線 debug-mode enforce；
N9 dynamic cycle penalties 同樣是 schema 先 declare、runtime later read。

---

## 13. 工作方法（Methodology）

把 sprint 用到的工作流程、節奏、品管慣例抽出來描述。

### 13.1 Per-step commit + push 工作節奏

**做法**：每個 N 子任務一個 commit、push 後再進下一步。Commit message
含完整驗證結果（T1 數字、跑通的 ROM、bench MIPS 等）。

**為什麼**：
- 出 bug 容易 bisect 到單一 N 步
- Push 後 origin/main 是 known-good baseline，本機隨時敢實驗
- Commit history 本身是 progress log

**配套**：5 分鐘 timeout cap on tests（CLAUDE.md 規則）— 跑超時直接
找 root cause、不拉長 timeout。

### 13.2 Doc-driven design（write design → implement → closeout）

**做法**：每個 N 子系列流程：
1. **設計 doc 先寫**（如 `MD/design/22-memory-spec-v2.md`）— 定下
   schema / API / migration plan / 風險 + mitigation
2. **Implement** 跟著 doc 的 sub-step plan
3. **Closeout perf doc**（如 `MD/performance/202605091900-n4-memory-spec-v2.md`）
   — 3-run bench + cross-milestone 對照表 + ratio 量化 + 既有 doc
   invalidations 紀錄

**為什麼**：
- 設計 doc 強迫先想清楚範圍 / 風險 / 驗證條件
- Closeout doc 是「下次的 baseline」 — 跨 sprint 能對照
- 三份 doc（design / implementation in code / closeout）形成可追溯的
  decision trail

### 13.3 Gemini 諮詢 pattern

**做法**：碰瓶頸（unknown LLVM 行為、vendor manual corner case、設計
取捨難判）用 `tools/knowledgebase/gemini_query.py` 問 Gemini。**一次只
問一個問題**，問題具體 + 附 context。

**為什麼**：
- 第三方 reference 比自己想更廣
- 問問題的過程強迫把問題壓縮成「一句話」 — 通常壓縮過程就想到答案
- Log 在 `tools/knowledgebase/message/` 留紀錄供未來追溯

**實踐**：N4 的 Gemini 7-point critique 直接決定 N4 schema 設計；N1
也諮詢過 alloca+mem2reg vs IStateContext refactor 的取捨。

### 13.4 Test-first for new features

**做法**：先寫 test 表達 expected behavior，再寫實作。

**實踐**：
- N3.3 的 `Mos6502CycleTable_DerivedFromSpec_MatchesOracleForCc01` 先
  寫，把「spec-derived cycle 必須等於 oracle」的 invariant 鎖死，再
  逐 group 轉換
- N5 的 toy-CPU divergence test 跟 real-workload test 一起寫，確保
  toolkit 不會 false-pass
- N7 / N10 全是新 unit test 證 query API + enforce flag 行為符預期

### 13.5 Cross-validation tests（避免 spec drift）

**做法**：寫 test 檢查 「spec 跟 runtime constants 一致」。

**實踐**：
- `Loads_Gba_MachineSpec_AndAlignsWith_GbaMemoryMap` — spec 的
  `bios.AddrStart` 必須等於 `GbaMemoryMap.BiosBase`
- `PageTable_AlignsWith_MachineSpec_GbaJson` (N8) — page table build
  出來的 region 必須對應 spec/machines/gba.json 宣告的 region
- 任何一邊改了另一邊沒跟 → test 失敗 → catch 住

**為什麼**：spec drift 是 framework 最容易踩的坑；用 test 強制鎖住
avoids 隱性 inconsistency。

### 13.6 Opt-in gate for risky perf experiments

**做法**：可能影響 perf 的新 inline path / optimization 默認 OFF，用 env
var 或 flag 開啟。

**實踐**：
- N11 fastmem inline `APR_MOS6502_FASTMEM=1` — default OFF 因為 6502
  perf-neutral；ON 留給 future 其他 architecture 試
- N10 `EnforceAllowedWidths` — default OFF 零 hot-path cost；ON 留給
  debug mode

**為什麼**：避免「foundation 證明」跟「baseline perf 不退步」衝突 —
infrastructure 在 code 裡，但是否啟用是 case-by-case。

### 13.7 3-run perf bench 慣例

**做法**：任何 perf-impacting 改動末尾跑 3-run bench：
- ROM 固定（blargg cpu_test5）
- max-cycles 固定（110M cycles ≈ 62 emulator-seconds）
- 三 backend (legacy / json per-instr / json-block) 各 3 run
- 結果記錄到 `MD/performance/<時戳>-<topic>.md`

**為什麼**：
- 單 run 可能受 system load 影響；3-run 取 avg 抓 noise floor
- 跨 milestone 比較有共同 baseline
- 留 perf doc 等於留「未來 regression 可對照的 baseline」

### 13.8 Crossed-out + replacement for stale docs

**做法**：doc 裡的舊結論不刪、用 `~~strikethrough~~` 標廢棄、後接
「2026-05-09 update」block 解釋現況。

**為什麼**：
- 保留歷史推理過程；後人看得出「為什麼當時這樣想」
- 對照新舊結論直接看出框架 / 認知是怎麼演化的
- 避免「為什麼當時要這樣寫」的 archaeology 工作

**實踐**：`MD/design/03-roadmap.md` 的「third CPU not done」段、
`MD/note/framework-future-extensions-and-vision.md` 的「推到 3 顆」段
都用此 pattern 處理。

### 13.9 Schema-first migration（先 schema 後 runtime）

**做法**：spec format 改動分階段：
1. **Phase 1**：parser 認新 field、runtime 還沒 read（backwards-compat）
2. **Phase 2**：runtime 漸進去 read（per-region 或 per-CPU）
3. **Phase 3**：dropping legacy fields（如果有需要）

**為什麼**：
- backwards-compat 不破現有 spec / 不阻擋 runtime work
- 風險分散在多個 commit；每個 commit 都能單獨驗證
- 可以隨時暫停在某個 phase；剩下的 phase 留 future

**實踐**：N4 把 v2 fields 先加進 schema (N4.1)、再 incremental migrate
runtime usage (N4.2-N4.4)。N9 dynamic cycle penalties 只做 Phase 1
（schema 加 field、runtime IR 暫沒 read）。

### 13.10 Closeout invalidation 紀錄

**做法**：寫 closeout doc 時去 update 之前所有引用過時結論的 doc，
加 invalidation note 指回新 doc 的位置。

**為什麼**：closeout 是「過時舊結論集中清理」的好時機；不做的話 doc
之間會出現矛盾，未來讀者迷路。

**實踐**：N4.6 closeout 同時更新 `MD/design/21-spec-driven-runtime.md`
+ `MD/performance/202605091804-n3-declarative-ratio.md` 的 N3.4 結論；
N3.3-finally closeout 再更新一次同樣兩份 doc。

---

## 14. 參考連結

設計 docs：
- [`MD/design/18-block-jit-state-abstraction.md`](/MD/design/18-block-jit-state-abstraction.md)
- [`MD/design/19-declarative-jit-policy.md`](/MD/design/19-declarative-jit-policy.md)
- [`MD/design/20-adding-a-new-cpu.md`](/MD/design/20-adding-a-new-cpu.md)
- [`MD/design/21-spec-driven-runtime.md`](/MD/design/21-spec-driven-runtime.md)
- [`MD/design/22-memory-spec-v2.md`](/MD/design/22-memory-spec-v2.md)

Performance docs（時間序）：
- [`MD/performance/202605091229-nes-jsoncpu-per-instr-baseline.md`](/MD/performance/202605091229-nes-jsoncpu-per-instr-baseline.md)
- [`MD/performance/202605091559-nes-blockjit-vs-perinstr.md`](/MD/performance/202605091559-nes-blockjit-vs-perinstr.md)
- [`MD/performance/202605091804-n3-declarative-ratio.md`](/MD/performance/202605091804-n3-declarative-ratio.md)
- [`MD/performance/202605091900-n4-memory-spec-v2.md`](/MD/performance/202605091900-n4-memory-spec-v2.md)
- [`MD/performance/202605092000-n33-full-spec-cycles.md`](/MD/performance/202605092000-n33-full-spec-cycles.md)

Roadmap synthesis：
- [`MD/design/03-roadmap.md`](/MD/design/03-roadmap.md) §「N 系列」段
- [`MD/design/00-overview.md`](/MD/design/00-overview.md) §「第三顆 CPU 移植」段
