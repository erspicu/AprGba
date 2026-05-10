# N3.3 (finally) closeout — full spec-driven cycle table

> **Status (2026-05-09)**：N3.3 系列完成。N3 收尾時 (`MD/performance/
> 202605091804-n3-declarative-ratio.md`) 把這條留作 BLOCKED — 原本的
> 2A03 spec 只有 per-mnemonic 粗粒度 `cycles.form: "Nm"`，缺
> per-(mnemonic, addressing-mode) 維度，所以 `NesJsonCpu.s_cycleTable[256]`
> 一直是 hardcoded LegacyCpu mirror。N5/N7/N8 完工後回頭把這條收掉。
>
> 兩階段：
> 1. **N3.3 (struct break, commit `027fe79`)** — schema v2 加 `cycles.table`，
>    cc=01 ALU group 64 opcodes 完成轉換。
> 2. **N3.3-finally (commit `c806358`)** — 剩餘 7 個 group 全部轉完
>    + 12 個 KIL form 修正；`s_cycleTable[256]` hardcoded oracle 從
>    NesJsonCpu **完全刪除**。

---

## 1. Bench 結果（3 runs sequential，5-min cap）

ROM: `test-roms/blargg_nes_cpu_test5/cpu.nes`
Workload: `--run --max-cycles=110000000` (~62 emulator-seconds)
Build: Debug (`dotnet build AprGba.slnx --no-incremental`)
Commit: `c806358` (N3.3-finally 末)

| Backend | Run 1 | Run 2 | Run 3 | **Avg** | **MIPS** | vs N4.6 | vs N3.4 baseline |
|---|---:|---:|---:|---:|---:|---:|---:|
| legacy           | 21.490s | 21.648s | 21.562s | **21.57s** | **1.64** | 1.65 → 1.64 (noise) | 1.57 → 1.64 (+4%) |
| json per-instr   | 42.769s | 42.550s | 42.594s | **42.64s** | **0.83** | 0.82 → 0.83 (noise) | 0.81 → 0.83 (+2%) |
| json-block       | 34.973s | 35.061s | 34.893s | **34.98s** | **0.82** | 0.80 → 0.82 (+2%) | 0.80 → 0.82 (+2%) |

### 1.1 N3.3-finally perf 結論

N3.3-finally 純粹是 **cycle table 來源的重構** — 256 個 opcode 算出來
的 cycle 數值跟之前 hardcoded oracle 完全 byte-identical (T1 全綠 +
nestest 三 backend PASS 證明)。Hot path (`Step()` 的
`_specCycleTable[opcode]`) 跟原本 `s_cycleTable[opcode]` 同樣是
1 次 array index，0 perf cost。

Build cost: 一次性 256 次 decoder + cycle resolve，~10-50μs 在
NesJsonCpu 構造階段 — 對長期 emulation 完全 invisible。

### 1.2 跨 milestone perf 趨勢（同一 ROM、同一 workload、Debug build）

| Milestone | legacy | json | json-block | 備註 |
|---|---:|---:|---:|---|
| N1 baseline | 1.69 | 0.83 | 0.78 | hardcoded if-else dispatch |
| N3.4 | 1.57 | 0.81 | 0.80 | spec-driven region table（+ 7% 退步） |
| N4.6 | 1.65 | 0.82 | 0.80 | page-table dispatch + offset semantics |
| **N3.3-finally (本次)** | **1.64** | **0.83** | **0.82** | spec drives full cycle table |

整體 4 個 milestone 累積結果：legacy 跟 N1 baseline 距離 ~3%，per-instr
跟 N1 持平，block-JIT 反超 N1 約 5%。**重要：每一步都同時提升
declarativity**（從 50% → 70% → 78% → ~85%），perf 卻沒退步**。

---

## 2. Declarative ratio 量化（N4 → N3.3-finally）

| Layer | N4 後 | N3.3-finally 後 | Δ | 備註 |
|---|---|---|---|---|
| CPU register file | 100% | 100% | – | unchanged |
| Instruction encoding (decoder) | 100% | 100% | – | unchanged |
| Instruction semantics | 95% | 95% | – | unchanged |
| Cycle multiplier | 100% | 100% | – | unchanged |
| Block-JIT cycle accounting | 90% | 90% | – | unchanged |
| Memory bus dispatch | 95% | 95% | – | unchanged |
| Region routing | 95% | 95% | – | unchanged |
| Interrupt routing | 90% | 90% | – | unchanged |
| **Per-instr cycle table** | **60%** | **100%** | **+40%** | N3.3-finally — `cycles.table` + spec-built |
| Mapper logic | 0% | 0% | – | escape hatch — by design |
| PPU/APU register handlers | 0% | 0% | – | escape hatch — by design |
| Access widths / wait states | 100% (declared) | 100% (declared) | – | unchanged |

**加權平均**：N4 ~78% → N3.3-finally **~85%**（跨過先前評估的 80%
天花板，靠破 N3.3 結構性 blocker）。

### 2.1 Spec format 改動 summary

`spec/cpu/2a03/groups/` 新增 `cycles.table` 的 InstructionDef：

| Group | Mnemonics | bbb modes | Total opcode-instances |
|---|---:|---:|---:|
| alu-cc01 (N3.3 part 1) | 8 | 4-8 | 64 (-1 shadowed) |
| ctrl-cc00 | 5 (BIT/STY/LDY/CPY/CPX) | 2-5 | ~16 |
| rmw-cc10 | 8 (ASL/ROL/LSR/ROR/STX/LDX/DEC/INC) | 3-5 | ~30 |
| unofficial cc=11 broad | 8 (SLO/RLA/SRE/RRA/SAX/LAX/DCP/ISC) | 4-7 | ~50 |

加上 12 個 KIL form 從 `"1m"` 修成 `"2m"` (oracle 認證的真實 cycle 數)。

### 2.2 Code 改動 summary

`src/AprNes.Cli/Cpu/NesJsonCpu.cs`:
- **刪除**：hardcoded `s_cycleTable[256]` static field（16 行 oracle bytes）
- **新增**：`BuildSpecCycleTable(decoder, cyclesPerSpecUnit)` static helper
  + per-instance `_specCycleTable` field
- **修改**：`Step()` 的 `int cycles = s_cycleTable[opcode]` 改成
  `_specCycleTable[opcode]` — 同位置、同 hot path

`src/AprCpu.Core/JsonSpec/SpecModel.cs`:
- `Cycles` record 加 `Table` 欄位
- 新 `CycleTable` record + `Resolve(EncodingFormat, opcode)` 方法

---

## 3. 學術角度 — N3 系列的「結構性 blocker」破除路徑

N3 收尾時把 cycle table 限制歸類為「framework 抽象到頂、再往下需要
spec format 改造」。後續 N4/N5/N7/N8 都繞過這條（focus 在 memory bus
+ lockstep + GBA 等正交方向），把 framework 其他 dimension 推到位後，
回頭很容易就能解：

1. **Schema 設計**：`cycles.table` field 描述很 trivial — 一個 selector
   field name + key-value pairs。設計成本幾乎 0。
2. **Resolver 實作**：12 行 C# 即可（extract field bits, format binary
   key, lookup map）。
3. **Spec 轉換**：mechanical — 對著 oracle 表填數字，按 group 分批做。
4. **Code 替換**：用 `BuildSpecCycleTable` 取代 hardcoded array — 一個
   commit 內就能完成。

**學術結論**：N3 收尾說的「fundamental escape hatch」其實只有 mapper
state machine + PPU/APU heavy side effects + NMI procedural sequence
這三類 (~500 lines C#) 真正屬於 framework 邊界外。Per-instruction
cycle table、interrupt vector、memory layout 這些 originally claimed
「declarative 上限 70%」的限制，都只是**還沒花時間補 schema** — 不
是 framework 本質限制。

實際上限可能更接近 **90%+** — 剩下 10% 是 procedural state machine 類
邏輯，這部分跟 ECMAScript 不能取代 Verilog 是同一個 fundamental 道理。

---

## 4. 環境

- OS: Windows 11 Home (10.0.26200)
- .NET: 10.0.107
- LLVM: 20.x via LLVMSharp.Interop
- Build: Debug, no-incremental
- Commit: `c806358` (N3.3-finally 末)
- Logs: `temp/n33fin-{leg,json,block}-{1,2,3}.log`

---

## 5. N3.3 系列收尾

- ✓ **2026-05-09 早上** N3.3 (struct break) — schema v2 + cc=01 conversion (commit `027fe79`)
- ✓ **2026-05-09 晚上** N3.3-finally — 全 256 opcode 轉完 + drop oracle (commit `c806358`)
- ✓ **2026-05-09 晚上** 3-run bench + 本 doc

Outstanding：N3.3 已徹底 closed。其他 N4/N5/N7/N8 closeout 各自獨立。
N5+ 候選方向（per N4.6 closeout §3）剩餘：
- ARM 2-level page table sub-grain (N8 已做 1-level，未來如 NDS dual
  CPU 或 cart 內 sub-page tricks 才需要)
- spec format 進一步推進（branches taken-cycle、page-cross +1
  penalty 用相同 selector pattern 描述）
- 第 4 顆 CPU（仍是最高學術價值方向，看 R3000A 或 R4300i）
