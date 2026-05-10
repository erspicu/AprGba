# Adding a new CPU to the framework — contributor guide

> **Status (2026-05-09)**：N2 收尾後寫的 SOP doc。經過 N1.B' (alloca+mem2reg)
> + N2 (declarative JIT policy + CPU/Machine spec split) 兩輪 framework
> 重構，加新 CPU 流程簡化到「寫 JSON spec + 加 arch-specific micro-op
> 到 emitter 檔；framework 自動處理 per-instr / block-JIT 兩種模式」。
>
> 目前已實作的 3 個 CPU 例子可以參照：
> - **ARM7TDMI**（GBA）— `spec/cpu/arm7tdmi/cpu.json` + `src/AprCpu.Core/IR/ArmEmitters.cs`
> - **LR35902**（GB DMG）— `spec/cpu/lr35902/cpu.json` + `src/AprCpu.Core/IR/Lr35902Emitters.cs`
> - **Ricoh 2A03**（NES NTSC）— `spec/cpu/2a03/cpu.json` + `src/AprCpu.Core/IR/Mos6502Emitters.cs`
>
> 加第 4 個跟著本 doc 的 step-by-step 走，一個 step 一個 commit、跑測試
> 才往下一步。

---

## 1. Pre-flight — 確認需求

回答這 4 題決定 scope。多數 CPU 應該都類似 6502 / GB / ARM 的其中一個。

1. **Word size** — 8-bit / 16-bit / 32-bit / 64-bit?
2. **Instruction encoding** — 固定寬度（ARM/Thumb）vs 變寬（LR35902/6502/x86）?
3. **Register file** — 幾個 GPR / 是否 banked / status reg 怎麼放?
4. **Memory model** — endianness / alignment / 是否有 mode-banked memory（ARM monitor mode 之類）?

---

## 2. Step-by-step

### Step 1 — `spec/<arch>/cpu.json` (ISA semantics)

把 ISA 抽象寫成 declarative JSON。schema 在 `spec/cpu/_schema.json`。

最小可運作：

```json
{
    "$schema": "../schema/cpu-spec.schema.json",
    "spec_version": "1.0",
    "architecture": {
        "id":             "<unique-id>",
        "family":         "<family-id>",
        "endianness":     "little",
        "word_size_bits": 8
    },
    "variants": [{ "id": "<chip-id>", "core": "<arch-id>" }],
    "isa_metadata": {
        "endianness":             "little",
        "cycles_per_spec_unit":   1,
        "pc_update_policy":       "lazy",
        "interrupt_check_policy": "end_of_block"
    },
    "register_file": { ... },
    "exception_vectors": [...],
    "instruction_sets": [{ "name": "Main", "include": "main.json" }]
}
```

`isa_metadata.cycles_per_spec_unit` 重要：
- **GB / ARM convention**：`cycles.form: "3m"` 當作 3 m-cycles = 12 t-cycles → 設 4
- **6502 convention**：`cycles.form: "3m"` 當作 3 raw cycles → 設 1
- 沒寫的話 framework default 是 4

### Step 2 — `spec/<arch>/groups/*.json` + `<arch>/main.json` (instruction definitions)

每個 instruction 描述 encoding + steps。Steps 是 micro-op 序列，由 framework
emitter 對應 LLVM IR。

最小範例（單一 NOP）：

```json
{
    "name": "Nop",
    "formats": [{
        "name": "Nop",
        "pattern": "11101010",
        "fields": {},
        "mask": "0xFF",
        "match": "0xEA",
        "instructions": [{
            "mnemonic": "NOP",
            "since": "<arch-id>",
            "cycles": { "form": "2m" },
            "steps": []
        }]
    }]
}
```

對「重複出現的 ALU pattern」，**先用 generic emitter 組合**（add/sub/and/or/
xor/shl/lsr/asr/branch_cc/load_byte/store_byte/sext/trunc/update_zero/
update_sign/set_flag/...）— 都在 `StandardEmitters.RegisterAll`。

只有 generic 不夠時才寫 arch-specific emitter（見 step 4）。

### Step 3 — `spec/machines/<machine>.json` (board-level memory map)

CPU spec 不應該知道 memory map，板子 spec 才描述。schema 在
`spec/machines/_schema.json`。

```json
{
    "$schema": "../schema/machine-spec.schema.json",
    "name": "<machine-id>",
    "cpu":  "<cpu-arch-id>",
    "memory_regions": [
        { "name": "ram",  "addr_start": "0x0000", "addr_end_exclusive": "0x2000",
          "type": "ram", "fastmem_eligible": true, "smc_notify": true },
        { "name": "rom",  "addr_start": "0x8000", "addr_end_exclusive": "0x10000",
          "type": "rom", "fastmem_eligible": true }
    ],
    "interrupt_vectors": { "reset": "0xFFFC" }
}
```

Region `type` 三種：
- `ram` — 通用讀寫；`smc_notify` default-on（可被 self-modifying code 改）
- `rom` — 唯讀；`writable` default-off
- `io` — 副作用區（PPU/APU/mapper/...）；`forces_end_of_block` default-on
  （任何寫到 io region 的 instruction 結束 block — 因為 IO 寫可能改變
  memory map / 觸發 interrupt 等）

### Step 4 — `<Arch>Emitters.cs`（arch-specific micro-op emitters）

新檔在 `src/AprCpu.Core/IR/<Arch>Emitters.cs`。Mirror 既有檔結構（看
`Mos6502Emitters.cs` 大概 2000 行的 size，`Lr35902Emitters.cs` 類似）。

只寫 generic 處理不到的：
- Addressing-mode dispatchers（如 6502 的 cc bbb 模式 8-way switch）
- Arch-specific flag combos（6502 ADC 一次寫 NVZC、ARM CPSR mode）
- 控制流帶 stack（JSR / RTS / RTI / BRK 在 6502；CALL / RET / IRQ 在 GB）
- 不對稱 push/pop（6502 page-1 stack vs GB pre-decrement vs ARM stack-of-anything）

**emitter 寫法重點**：

1. 用 `ctx.GepStatusRegister(name)` / `ctx.GepGpr(idx)` 拿 state pointer，
   **不要直接用 `ctx.StatePtr`** — framework 會根據 mode 把 pointer 重定向
   到 alloca（block-JIT）或 state buffer（per-instr）；emitter 不該知道
   差別。
2. PC 寫入要 set `Layout.GepPcWritten = 1`，讓 BlockFunctionBuilder 知道
   要 exit block。
3. 條件分支用 `branch_cc` 或自行用 `BuildSelect` + PC store。
4. 編譯期已知 immediate（block-JIT）走 `ctx.CurrentInstructionBaseAddress is not null`
   分流，從 `ctx.Instruction` shift+trunc 抽 imm，省掉 bus extern call。
   Per-instr fallback 用 `bus.ReadByte` walk PC。
   範例：`Mos6502Emitters.FetchImm8` (lines ~150).

### Step 5 — Decoder dispatch + arch family branch

在 `src/AprCpu.Core/Compilation/SpecCompiler.cs` 的 family 切換加新 arch：

```csharp
else if (string.Equals(family, "<your-arch-family>", StringComparison.OrdinalIgnoreCase))
{
    YourArchEmitters.RegisterAll(registry);
}
```

### Step 6 — `Apr<Cpu>.Cli/Cpu/<CpuName>JsonCpu.cs`（host class）

Mirror `NesJsonCpu.cs` (~390 lines) 結構：

- ctor: load spec, build HostRuntime, bind memory externs, allocate state buffer
- StepOne: fetch opcode, decode, dispatch fn pointer
- StepBlock: BlockDetector + BlockCache + cycles_left budget tracking
- NMI / IRQ handler in C#（6502 / GB pattern）

block-JIT enable 時：
```csharp
var bfb = new BlockFunctionBuilder(...) {
    CyclesPerSpecUnit = _spec.Cpu.IsaMetadata?.CyclesPerSpecUnit ?? 4
};
```

### Step 7 — 測試 ROM 跑 PASS

至少要有 1 個小規模 instruction-test ROM 跑通：
- 對 6502 family：nestest / blargg cpu_test5
- 對 ARM：armwrestler
- 對 GB：blargg cpu_instrs

驗證跑 **三 backend 一致** — legacy interpreter / json per-instr / json-block。
跑 `--diff` 跟 `--diff-block`（NES）/ 對應 lockstep harness 找 divergence。

---

## 3. 反模式 — 別這樣做

### ❌ 把 CPU + 板子合成一個 spec

cpu.json **不該** 知道任何 board 概念（memory map、mapper、IO addresses）。
寫進去 = 把 ISA 鎖死在某個板子，未來無法重用。

### ❌ Emitter 寫 mode-aware code

```csharp
// 不要這樣
if (perInstrMode) { /* path A */ } else { /* path B */ }
```

framework 已經透過 `EmitContext.GepStatusRegister` 等抽象把 mode 處理好了。
Emitter 寫一次、自動 work 在 per-instr / block-JIT 兩種模式。

唯一例外：block-JIT 編譯期 imm 抽取（檢查 `ctx.CurrentInstructionBaseAddress`），
那是**正交優化**，不是 mode-handling 重複工作。

### ❌ 把優化邏輯寫進 spec.json

Spec 只描述「事實」（這個 region 是 RAM、那個 instr 寫 PC 等）。**怎麼優化**
是 emitter / framework C# 的事。否則 JSON 變成 mini-language（Greenspun's
tenth rule）。

### ❌ Hardcode bus dispatch 路徑

NesMemoryBus / GbMemoryBus 的 ReadByte/WriteByte switch 鏈現在還是
hardcoded — 那是因為改 hot path 風險高、需要 perf bench gate。**新 CPU**
從一開始就應該驅動 from MachineSpec 的 memory_regions table（O(N) lookup
or sorted binary search），雖然現有 3 個 CPU 還沒 migrate 到這個形狀。

如果你的 CPU 加進來時想做這件事，看 `MachineSpec.MemoryRegions` 已經有
所有資料；寫一個 `MachineSpec.LocateRegion(uint addr)` helper 從 region
table 找對應 region 即可。

---

## 4. Reference — 既有 framework 提供的 building blocks

### 共用 micro-op emitter (StandardEmitters.RegisterAll)

`read_reg` / `write_reg` / `read_reg_named` / `write_reg_named` /
`add` / `sub` / `and` / `or` / `xor` / `shl` / `lsr` / `asr` / `ror` /
`bic` / `mvn` / `mul` / `umul64` / `smul64` / `add_i64` /
`load_byte` / `store_byte` / `read_imm8` / `read_imm16` /
`read_pc` / `sext` / `trunc` /
`branch` / `branch_link` / `branch_cc` / `if` / `select` /
`push_pair` / `pop_pair` / `push8` / `pop8` / `call` / `ret` /
`call_cc` / `ret_cc` /
`set_flag` / `toggle_flag` / `update_zero` / `update_sign` /
`update_h_add` / `update_h_sub` / `update_h_inc` / `update_h_dec` /
`bit_test` / `bit_set` / `bit_clear` / `shift` / `sync` / `defer`

### Block-JIT pipeline

`BlockDetector` 走 PC 直到 boundary、回 `Block`；`BlockCache` LRU 存編譯
結果；`BlockFunctionBuilder` emit 一個 LLVM function 涵蓋整個 block；
`HostRuntime` 包 ORC LLJIT。詳情看 doc #18 (state abstraction) + doc #19
(declarative policy)。

### State access抽象

Emitter 透過 `ctx.GepGpr(idx)` / `ctx.GepStatusRegister(name)` 拿 pointer
做 load/store。framework 在 block-JIT 模式自動 redirect 到 alloca、跑
mem2reg pass，產出跟手寫 SSA 等價的 IR。

### Doc cross-reference

- [#15](15-timing-and-framework-design.md) — 共用 timing 模型 / 框架通用化
- [#16](16-emulator-completeness.md) — 各 emulator 完成度盤點
- [#17](17-aprcpu-vs-emulator-timing-boundary.md) — framework / emulator 邊界
- [#18](18-block-jit-state-abstraction.md) — alloca+mem2reg 細節
- [#19](19-declarative-jit-policy.md) — 本 N2 系列的設計依據

---

## 5. 預估工時（對照已實作 3 個 CPU）

| 階段 | 8-bit RISC-like (6502/Z80) | 32-bit RISC (ARM/MIPS) | 變寬 CISC (x86/68k) |
|---|---|---|---|
| Step 1-3 (spec 寫) | 1-2 天 | 2-3 天 | 3-5 天 |
| Step 4 (emitter) | 3-7 天 | 1-2 週 | 2-4 週 |
| Step 5-7 (host class + 測試 ROM) | 1-2 天 | 2-4 天 | 3-5 天 |

實際時間取決於：
- ISA 複雜度（6502 ~150 official + 105 unofficial = 中；ARM ARMv4T = 中-高；
  m68k = 高；x86 = 非常高）
- 是否有 reference impl 可 port（OldProject / 開源 emulator）
- Test ROM 完整度（blargg-style + cycle-accurate? trace? ）

---

## 6. 如果卡關

- Spec 編譯失敗 → `dotnet test --filter SpecCompilerTests`，看 Diagnostics
- Block-JIT bug → 用 `--diff-block` lockstep 找 divergence
- IR 不正確 → `HostRuntime.PrintModuleIR()` dump、肉眼比 `Lr35902Emitters` 對應
  shape
- Per-instr / block-JIT divergence → 99% 是 emitter 違反「mode-agnostic
  state access」（直接讀 state buffer 而非 alloca）
- 看不懂某個 framework piece → grep doc #18 / #19，多半有寫
- Gemini 諮詢 — `tools/knowledgebase/gemini_query.py` 可以查 LLVM 細節
  (CLAUDE.md 有規則)

歡迎加新 CPU + 給 framework 提 issue。新 CPU 暴露 framework 不足是 framework
進化的好機會。
