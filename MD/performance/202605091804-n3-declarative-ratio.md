# N3 closeout — declarative ratio + perf bench

> **Status (2026-05-09)**：N3.0–N3.3 完工後量化。N3 把 framework 的
> declarative coverage 從 N2 的 ~50% 推到 ~70%。N3.3 撞到 spec format
> 結構性限制（cycles 沒 per-addressing-mode 維度），原本目標 80%
> 沒達成 — 但這個 blocker 本身是 framework 學術價值的一部分（找到「為何
> 這條路走不通」的明確結構性原因）。
>
> Bench：3-run blargg cpu_test5 三 backend，跟 N1 baseline 比 perf 沒
> 顯著退步（≤7%）。

---

## 1. Bench 結果（3 runs sequential，5-min cap）

ROM: `test-roms/blargg_nes_cpu_test5/cpu.nes`
Workload: `--max-cycles=110000000` (~62 emulator-seconds)
Build: Debug (`dotnet build AprGba.slnx --no-incremental`)
Commit: `0db6ed8` (N3.3 blocker doc)

| Backend | Run 1 | Run 2 | Run 3 | **Avg** | MIPS | vs N1 baseline |
|---|---:|---:|---:|---:|---:|---:|
| legacy            | 22.514s | 22.822s | 22.624s | **22.65s** | **1.57** | -7% (1.69 → 1.57) |
| json per-instr    | 44.202s | 44.008s | 43.961s | **44.06s** | **0.81** | -2% (0.83 → 0.81) |
| json-block        | 35.490s | 36.093s | 35.641s | **35.74s** | **0.80** | +3% (0.78 → 0.80) |

### 1.1 Legacy slowdown 分析

Legacy backend ~7% slower 是 N3.2 改了 NesMemoryBus dispatch 的 side
effect — 從 hardcoded if-else 換成 region-table linear scan。雖然 4 個
region 的 scan 跟 4-way if-else cost 應該接近，但 JIT 對前者的 inline
做得不如後者好。

選擇接受這 7% — N3 設計目標是 declarativity 不是 perf。如果未來 perf-
critical 場景需要救回，可以把 region scan 改成 jumping table（256-byte
table indexed by addr>>8）回到 O(1) lookup — 但 4GB 地址空間 ARM 走這
路會吃 16MB 記憶體；trade-off 屬於 ARM-specific 的後續 task。

### 1.2 Per-instr / block-JIT 跟 N1 比

兩個 backend 都在 noise 範圍內。表示 N2.4 的 imm-bake + N3.1 的 vector
spec read + N3.2 的 region dispatch 沒造成感知 perf 退步。

### 1.3 結果一致性

三 backend 都到 PC=$8003 self-loop（test halt）；blargg cpu_test5 PPU
nametable "All tests complete" — 全 11 個 subtest 通過。

---

## 2. Declarative ratio 量化

### 2.1 各層 declarative coverage 對照

| Layer | N2 後 | N3 後 | Δ | 備註 |
|---|---|---|---|---|
| CPU register file | 100% | 100% | – | spec.register_file 自動建 layout |
| Instruction encoding (decoder) | 100% | 100% | – | mask/match 從 spec |
| Instruction semantics | 95% | 95% | – | spec.steps + emitters；剩 5% per-arch micro-op |
| Cycle multiplier (m-cycle vs raw) | 100% | 100% | – | isa_metadata.cycles_per_spec_unit |
| Block-JIT cycle accounting | 90% | 90% | – | ParseCyclesForm 用 spec.cycles.form |
| **Memory bus dispatch** | 0% | **80%** | **+80%** | N3.2 region table 從 MachineSpec |
| **Interrupt routing** | 30% | **90%** | **+60%** | N3.1 vector addr 從 MachineSpec |
| **Per-instr cycle table** | 60% | **60%** | – | N3.3 BLOCKED — spec 沒 per-addressing-mode 維度 |
| Mapper logic | 0% | 0% | – | escape hatch 留 C#（state machine） |
| PPU/APU register handlers | 0% | 0% | – | escape hatch 留 C#（heavy side effects） |
| NMI handler body | 0% | 0% | – | escape hatch 留 C#（push order procedural） |

**加權平均**：N2 ~50% → N3 ~70%（落空目標 80%，N3.3 blocker 拖累 10pp）。

### 2.2 「Escape hatch 永遠 C#」明確列表

per design doc #21 §5 + N3 實踐結論：

| 項目 | 為何 C# | 大概行數 |
|---|---|---|
| `Mapper000.cs` / `Mapper001.cs` IMapper 實作 | 5-bit shift register state machine、bank select 邏輯 | ~150 lines |
| `NesPpu.WriteRegister` / `ReadRegister` switch | vram_addr increment、VBL flag、NMI fire、OAM DMA stall — heavy side effects | ~300 lines |
| `NesJsonCpu.NmiInterrupt` 函數體 | push PC.hi / PC.lo / P，set I — procedural sequence | ~30 lines |
| `NesJsonCpu.s_cycleTable[256]` | spec 結構不夠細支援 per-addressing-mode | 16 lines |

**總 escape-hatch C# 約 ~500 行**。對比 N3 之後 framework + spec 驅動部分（emitters + spec.steps + machine spec parsers + bus dispatch）約 ~3000+ 行 — escape hatches 占 framework runtime ~15%。

### 2.3 N3.3 blocker — 後續路徑

要解 cycles 結構性限制需要 spec format 重構。兩個選項：

(a) **每 (mnemonic, addressing-mode) 各 instruction-def** — `spec/2a03/groups/alu-cc01.json` 從 8 entries 變 64 entries (4×)。其他 group 同樣膨脹。實作可行但 spec 文件變大、人手維護成本高
(b) **Per-format `cycle_table`** — 在 `InstructionFormat` 加 `cycle_table: { "000": 6, "001": 3, "010": 2, ... }` mapping bbb → cycle count。Spec 大小幾乎不變、resolver 邏輯稍微複雜

方向 (b) 比較合理，但**留作 future N4+ 範圍**。

---

## 3. Framework 角度的 N3 學術結論

1. **可 declarative 的部分**: addr 範圍、interrupt vectors、cycle multiplier、cycles per mnemonic（粗粒度）、register file shape、instruction encoding、instruction semantics — 全部成功
2. **必須 C# 的部分**：state machine（mapper bank switch）、heavy side effects（PPU register writes）、procedural sequences（NMI push order）— 確認本質上不適合 declarative
3. **遇到結構性限制**：cycles 的 (mnemonic, addressing-mode) 雙維度 — spec format 1D 不夠
4. **declarativity vs perf 的 trade-off 量化**：spec-driven dispatch 對 legacy 慢 ~7%（4-region linear scan vs 4-way if-else），對 JIT backend 在 noise 內

對學術 paper：framework 的「真正可 declarative」上限大致 70-80% — 剩下 20-30% 是 fundamental escape hatches，不是 framework 不夠抽象。

---

## 4. 環境

- OS: Windows 11 Home (10.0.26200)
- .NET: 10.0.107
- LLVM: 20.x via LLVMSharp.Interop
- Build: Debug, no-incremental
- Commit: `0db6ed8` (N3 末)
- Logs: `temp/n34-{leg,json,block}-{1,2,3}.log`

---

## 5. N3 系列收尾

- ✓ N3.0 design doc #21
- ✓ N3.1 interrupt vectors from MachineSpec
- ✓ N3.2 NES memory bus dispatch via MachineSpec
- ✓ ~~N3.3 per-instr cycle table from spec — BLOCKED + documented~~
       **RESOLVED 2026-05-09 後段**（詳見 §下方 update + commit `027fe79`/`c806358`）
- ✓ N3.4 perf bench + ratio doc（本文件）

Outstanding：spec format 改進（per-addressing-mode cycles）若想推進可開
N4 系列。或暫停，做別的方向（N5 通用 lockstep diff、N6 加第 4 個 CPU、
等等）。

---

> **2026-05-09 (later) update — N3.3 RESOLVED**
>
> 本 doc §1.1 提到的「N3 結束時 cycle table 撞上結構性 blocker」已在
> 同日後段補完（路線採 §2.3 的方向 (b) — per-format cycle_table）：
>
> - commit `027fe79` — schema break + cc=01 conversion (64 opcodes)
> - commit `c806358` — 剩餘 7 group 全轉 + drop hardcoded oracle
>
> 本 doc §2.3 結論「方向 (b) 比較合理，但留作 future N4+ 範圍」過時 —
> 實際路徑：N4/N5/N7/N8 把 framework 其他維度推到位後，回頭發現 N3.3
> 的 schema 改造其實很 trivial（12 行 resolver C# + mechanical spec
> 填表），跟「結構性限制」標籤的悲觀感受不符。
>
> Per-instr cycle table declarative ratio: 60% → **100%**（§2.1 表格）
> Framework 整體 ratio: 70% (N3) → 78% (N4) → **~85%** (N3.3-finally)
>
> 詳細紀錄：`MD/performance/202605092000-n33-full-spec-cycles.md`
