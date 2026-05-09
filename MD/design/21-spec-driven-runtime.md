# Spec-driven runtime — closing the declarative-vs-hardcoded gap

> **Status (2026-05-09)**：N3 設計階段。N2 把 spec 結構準備好了
> （MachineSpec / IsaMetadata / DecodedBlockInstruction.Immediate / page-bitset SMC /
> forces_end_of_block hook），但**runtime 大半還沒實際用 spec**。本 doc
> 盤點 spec 描述 vs 真正實作的落差，劃出可以實際搬進 spec 的部分跟
> 必須留 C# 的部分（與其原因），並排定 migration plan。
>
> **目標**：把 declarative coverage 從 N2 的 ~50% 推到 ~80%。剩下的
> 20% 是 framework escape hatches —文件化它們**為什麼**該留 C#，本身就是
> framework 設計價值的一部分。

---

## 1. N2 後現況盤點

### 1.1 已 declarative + 真正 wire ✓

| 項目 | spec 位置 | runtime 怎麼用 |
|---|---|---|
| Register file | `cpu.json::register_file` | `CpuStateLayout` 從 spec 自動建 |
| Instruction encoding | `cpu.json::instruction_sets` + `groups/*.json` | `DecoderTable` 從 spec 自動建 |
| Instruction semantics | `groups/*.json::steps[]` | `SpecCompiler` emit IR via `EmitterRegistry` dispatch |
| Cycle multiplier (m-cycle vs raw) | `cpu.json::isa_metadata.cycles_per_spec_unit` | NesJsonCpu N2.5 開始讀 spec |
| Block-JIT cycle accounting | `groups/*.json::cycles.form` (per-instr) | `BlockFunctionBuilder.ParseCyclesForm` 解析 |
| Per-CPU emitter mode-agnostic | (no spec field — IR layer) | `IStateSlotProvider` (alloca / state-buffer) auto-switch |

### 1.2 已 declarative 但**還沒 wire** ⚠️

| 項目 | spec 位置（已有） | runtime 還是 hardcode 在哪 |
|---|---|---|
| Memory map (region 範圍 / 類型) | `spec/machines/*.json::memory_regions[]` | `NesMemoryBus.ReadByte/WriteByte` if-else 鏈、`GbMemoryBus.cs` 同樣、`GbaMemoryBus.cs` 同樣 |
| `forces_end_of_block` 標記 | `region.forces_end_of_block` | BlockDetector 有 hook 但 NesJsonCpu 沒 wire（仍靠 `IMapper.PrgBankSwitched` callback） |
| `smc_notify` region 範圍 | `region.smc_notify` | `NesMemoryBus.SmcWriteHook` 對所有 addr fire（沒 region filter） |
| Interrupt vectors | `machine_spec.interrupt_vectors` | `NesJsonCpu.NmiInterrupt` hardcoded `$FFFA` / `$FFFB` |
| Region side_effects 標籤 | `region.side_effects: ["ppu", "apu", "mapper", ...]` | 寫死 `WritePpu` / `WriteApu` / `mapper.CpuWrite` C# dispatch |

### 1.3 從來沒 declarative 過 ❌

| 項目 | 沒在 spec | C# 實作 |
|---|---|---|
| Per-instr cycle table | （理論可從 cycles.form 推導） | `NesJsonCpu.s_cycleTable[256]` 硬寫 6502 chart（mirror LegacyCpu byte-for-byte） |
| Page-cross extra cycle | 沒 spec 欄位 | 沒實作 |
| Mapper logic（NROM / MMC1 control reg / shift register） | 沒 spec | `Mapper000.cs` / `Mapper001.cs` 純 C# IMapper impl |
| PPU register write 副作用 | spec 標 `side_effects: ["ppu"]` 但細節沒有 | `NesPpu.WriteRegister` switch case |
| NMI handler body（push state / set flags） | 沒 spec | `NesJsonCpu.NmiInterrupt` 純 C# |

---

## 2. 可以 / 該 / 不該 搬到 spec — 設計判準

### 2.1 規則：搬到 spec 的判準

✓ **Declarative 表達自然不費力**（addr range / cycle count / region type）— 一行 JSON 比一個 C# class 簡單
✓ **重複出現在多個 CPU**（memory regions、interrupt vectors、cycle costs 全 3 個 CPU 都有）
✓ **不用 turing-complete 描述**（fixed schema 夠用，不要 sub-language）
✓ **runtime 讀取成本可接受**（startup-time 解析 OK；hot-path 100Hz lookup 也 OK；hot-path 100MHz lookup 不行）

### 2.2 規則：留 C# 的判準

✗ **State machine 行為**（MMC1 5-bit shift register + 4 register banks → 純邏輯）
✗ **副作用具體 procedure**（PPU $2007 寫入觸發 vram_addr 增量 + 可能 fire NMI）
✗ **跟 runtime mutable state 強耦合**（NMI handler push 順序、CPSR mode swap）
✗ **per-instr hot path（每秒呼幾百萬次）**

### 2.3 灰色地帶

? Region dispatch hot path —— spec 描述 region 範圍是 declarative，但**怎麼從 addr 找 region** 是 runtime lookup。每 memory access 一次 = hot path。可接受成本上限 ~10ns（一次 binary search over ~10 regions）。
? Mapper interface declarative —— mapper **行為**是 C#，但 mapper **annotated config**（"PRG bank size = 16KB", "CHR mode 8KB/4KB"）可以 declarative。漸進式：先把 config 抽出去。

---

## 3. N3 範圍 — 真實 migration

只動**規則 2.1 通過**的東西、避開 2.2、灰色地帶降低風險先做最簡單的部分。

### N3.1 Interrupt vectors（最低風險）

NesJsonCpu 改從 MachineSpec 讀 `interrupt_vectors["nmi"]` / `["irq"]` / `["reset"]`。
- C# 改動：3 行（NmiInterrupt 把 `$FFFA` 改成 `_machineSpec.InterruptVectors["nmi"]`）
- 風險：低（vector addr 不變、只是讀取來源變）
- 值：documentation-grade — 證明 spec 真的可以 drive runtime
- LegacyCpu 不動（保留 hardcoded 作 oracle reference）

### N3.2 NES memory bus → MachineSpec table-driven

NesMemoryBus.ReadByte/WriteByte 從 `Func<addr> → handler` 寫死改成 region table lookup。

具體 design：
1. 構造時從 MachineSpec build region 排序陣列 + handler delegate
2. ReadByte: binary search region by addr range（log₂(10) = ~3 comparisons），呼對應 handler delegate
3. handler delegate 透過 region.side_effects 名稱解析（`"ppu"` → bus.PpuRead，`"mapper"` → mapper.CpuRead，`"ram"` → 直接 array index with mirror_mask）

風險：每 byte access 多 ~3 個比較 + 1 個 indirect call。對 NES（1.7MHz × 3 = 5M memory accesses/s）= 額外 ~50M ops/s 跟原本相比 —— 應該還在 ±10% 內。如果 perf > 10% 退步、reconsider。

GB / GBA bus 不動（perf 敏感、分開做）。

### N3.3 Per-instr cycle table from spec

NesJsonCpu 構造時 walk `Main` decoder 256 個 opcodes、parse 每個 spec.cycles.form，build 一個 `byte[256]` 表（一次性、O(N)）。replace `s_cycleTable[]` hardcoded。

驗證：spec-derived 表跟 LegacyCpu 的 hardcoded `cycle_tableData` byte-for-byte 對比 — 若有差異標 diagnostic 並決定怎處理（可能 spec 的 cycle.form 對某些 opcode 寫得不準）。

### N3.4 Branch-taken / page-cross cycle nuances

block-JIT 已有 `ParseCyclesFormBoth` 處理 "Nm_or_Mm"。per-instr backend 也該對齊 — taken-branch +1 cycle 從 spec 推（已經有資料、只是 per-instr 沒 honor）。

Page-cross extra: spec 加新 field `cycle_nuances.page_cross_addr_modes: ["abs_x", "abs_y", ...]` 或 instr-level `page_cross: true`。這是新 spec schema。先做 design 不急實作 — 跟 actual perf gain 比、可能不值得。

### N3.5 留 C# 的東西（明確標記）

- **Mapper000/Mapper001 C# class** — state machine + bank switch logic
- **NesPpu register write handlers** — side-effect heavy（vram_addr increment、VBL flag、NMI fire）
- **NesJsonCpu.NmiInterrupt body** — 雖然 vector 從 spec 讀，但 push order、flag set 是 procedural code
- **NesPpu.Tick** cycle-accurate scanline counter — too dense for declarative

寫進 doc 解釋為什麼，避免未來再次糾結「這應該 declarative 嗎」。

---

## 4. 量化 — 期望 declarative ratio

粗估「framework code lines that drive runtime」(目測):

| Layer | N2 後 declarative | N3 後（預估） |
|---|---|---|
| CPU semantics | 95% (spec.steps + emitters) | 同 |
| Memory bus dispatch | 0% (純 C# if-else) | ~70%（NES 走 spec；GB/GBA 還 C#） |
| Interrupt routing | 30%（vector 標 spec 但 NesJsonCpu hardcoded 讀） | 90%（vectors 真讀） |
| Cycle accounting | 60%（block-JIT 走 spec, per-instr hardcoded） | 95%（per-instr 也走 spec） |
| Mapper / IO devices | 0%（純 C#） | 0%（保留） |
| **加權平均** | **~50%** | **~80%** |

剩 20% 永遠留 C#（mapper / device side-effects / NMI body）— 文件化為「framework escape hatches」。

---

## 5. 風險 + rollback

1. **N3.2 NES bus perf 退步 > 10%** — revert 該 commit、把 spec-driven dispatch 加 cache（per-addr inline cache 之類）或放棄整 step
2. **N3.3 cycle table mismatch** — spec 跟 LegacyCpu 不同 → 可能是 spec 寫不準（修 spec）或 LegacyCpu 有 quirk（保留 hardcoded path 作 fallback）
3. **GB/GBA 受影響** — N3 範圍只動 NES。GB/GBA 改動是 future N4+ 議題

---

## 6. Migration 順序（保守）

1. **N3.0** 本 doc 落地（commit 不動 code）
2. **N3.1** Interrupt vectors（最簡單、最低風險）
3. **N3.2** NES bus dispatch（最大改動、有 perf 風險）
4. **N3.3** Per-instr cycle table（中度風險）
5. **N3.4** Closeout — perf bench + 量化 declarative ratio + 寫進 perf doc

**N3.5** 不做（mapper / NMI body / PPU regs 留 C# escape hatch、文件化）。

每個 step 結束 → T1 + nestest（三 backend）+ blargg（三 backend）+ commit + push 後再進下一步。**5 分鐘 timeout cap on all tests**（CLAUDE.md 慣例）。
