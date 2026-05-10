# Declarative JIT optimization policy — CPU spec / Machine spec split

> **Status (2026-05-09)**：設計階段。前置：N1.B' alloca+mem2reg refactor
> 已 ship（doc #18）；emitter 對 per-instr / block-JIT 模式無感。本 doc
> 提下一層 framework abstraction：把 *block-JIT 優化策略* 也從「per-CPU
> C# hardcode」搬到「**declarative spec**」，並引入 **CPU 規格 vs Machine
> 規格** 兩層分離。
>
> 設計依據：
> 1. 內部 design discussion（user-driven，2026-05-09）
> 2. Gemini 諮詢 (2026-05-09)，紀錄在
>    [`tools/knowledgebase/message/20260509_163828.txt`](/tools/knowledgebase/message/20260509_163828.txt)
> 3. QEMU TCG / Dynarmic / Dolphin 的 system separation 慣例
>
> **目標**：加第 4 個 CPU + 對應板子時，只寫 JSON spec 就好；framework
> C# 層不變動、不複製、不依賴新 CPU 的存在。

---

## 1. 動機

### 1.1 現狀問題

N1.B' 之後，*state access* 已經是 framework 一致機制（alloca+mem2reg），
emitter mode-agnostic。但**其他 block-JIT 優化策略**還是 per-CPU C#
hardcoded：

| 優化 | 現狀 | 哪裡 hardcoded |
|---|---|---|
| Cycles 解析（m-cycle×4 vs 1×） | NesJsonCpu setter | `BlockFunctionBuilder.CyclesPerSpecUnit = 1` 在 NesJsonCpu C# 中 |
| Compile-time imm 抽取 | LR35902 emitter 內 if-branch | `Lr35902Emitters.FetchImmediate` |
| WRAM/HRAM region inline | LR35902 specific | `Lr35902Emitters.EmitWriteByteWithSyncAndRamFastPath` |
| SMC notification range | 全 addr | `NesMemoryBus.SmcWriteHook` 不分 region 都 fire |
| Block-boundary on bank switch | callback hack | `IMapper.PrgBankSwitched` + Mapper001 fires + JsonCpu invalidate |
| Block size cap | 寫死 64 | `BlockDetector.DefaultMaxInstructions` |

新 CPU 要加 = 學 5+ 處 C# emitter pattern + 各別判斷 ISA 適合哪些。

### 1.2 想要的形狀

新 CPU 加入流程：
1. 寫 `spec/<arch>/cpu.json`（純 ISA 語意 — 已現有設計）
2. 寫 `spec/machines/<machine>.json`（板子 memory map — 新概念）
3. 不改 framework C#

Framework 自動：
- 讀兩份 spec
- 配置 BlockFunctionBuilder / BlockDetector / 各 BlockCache hook
- mem2reg + decoder pre-extract immediate（永遠 on，不是 toggle）
- SMC 走 page-bitset（scale 32-bit 友善）

---

## 2. 核心設計：CPU spec 跟 Machine spec 分離

### 2.1 為何要拆

**Ricoh 2A03 CPU spec 不該知道「NES WRAM 在 $0000-$1FFF」** — 這是
NES 板子的事。如果把 memory map 寫進 cpu.json：

- 同一個 6502 spec 給其他機器（即使 hypothetical）就壞
- CPU spec author 要懂 board layout（職責混亂）
- Framework 程式判斷時要區分「ISA 屬性 vs 板子屬性」也混亂

### 2.2 兩個 spec 的職責

#### `spec/<arch>/cpu.json` — 純 ISA

| 欄位 | 範例 | 描述 |
|---|---|---|
| `architecture` | `"id": "Ricoh2A03", "family": "MOS6502"` | （現有） |
| `register_file` | A/X/Y + P/SP/PC | （現有） |
| `instruction_sets` | `Main` 256 opcodes | （現有） |
| **`isa_metadata`**（新） | （見下方） | block-JIT 相關 ISA 屬性 |

`isa_metadata` 範例（2A03）：
```json
"isa_metadata": {
    "endianness": "little",
    "pc_alignment_bytes": 1,
    "instruction_size_bytes": "variable",       // "variable" or N
    "cycles_per_spec_unit": 1,                  // NES raw cycles; GB/ARM 4
    "cycle_nuances": {
        "branch_taken_penalty": 1,
        "page_cross_penalty": 1
    },
    "pc_update_policy": "lazy",                 // "lazy" or "eager"
    "interrupt_check_policy": "end_of_block"    // 多選: "every_instruction" / "end_of_block" / "backwards_branch"
}
```

#### `spec/machines/<machine>.json` — 板子 / 系統

```json
{
    "name": "nes-ntsc",
    "cpu": "Ricoh2A03",
    "memory_regions": [
        {
            "name": "wram",
            "addr_start": "0x0000",
            "addr_end_exclusive": "0x2000",
            "type": "ram",
            "mirror_mask": "0x07FF",
            "fastmem_eligible": true,
            "smc_notify": true
        },
        {
            "name": "ppu_io",
            "addr_start": "0x2000",
            "addr_end_exclusive": "0x4000",
            "type": "io",
            "mirror_mask": "0x2007",
            "side_effects": ["ppu"],
            "forces_end_of_block": false
        },
        {
            "name": "apu_io",
            "addr_start": "0x4000",
            "addr_end_exclusive": "0x4020",
            "type": "io",
            "side_effects": ["apu", "oam_dma"]
        },
        {
            "name": "cart_prg",
            "addr_start": "0x4020",
            "addr_end_exclusive": "0x10000",
            "type": "io",
            "side_effects": ["mapper"],
            "forces_end_of_block": true
        }
    ],
    "interrupt_vectors": {
        "nmi":   "0xFFFA",
        "reset": "0xFFFC",
        "irq":   "0xFFFE"
    }
}
```

#### Region `type` 語意

| `type` | 意義 | Framework 行為 |
|---|---|---|
| `ram` | 通用讀寫 RAM | `fastmem_eligible: true` → block-JIT 可 inline GEP；寫入觸發 SMC notify |
| `rom` | 唯讀程式碼 | `fastmem_eligible: true`（讀）；寫入忽略；不觸發 SMC |
| `io` | 副作用區（PPU / APU / mapper / cart） | 永遠走 bus extern；`forces_end_of_block: true` 寫入後結束 block |

#### Memory bus 改寫

NesMemoryBus / GbMemoryBus / GbaMemoryBus 改成從 Machine spec 動態 build：

- 保留 `byte ReadByte(uint addr) / void WriteByte(uint addr, byte v)` API（不變）
- 內部 dispatch 從硬寫的 if-else chain 改 spec-driven region table lookup
- bus.WriteByte 自動 fire SMC notify if region.smc_notify
- region.side_effects 觸發對應的 host hook（PPU register write / APU / mapper）

注意：**具體 IO 操作的內部邏輯**（例如 PPUCTRL 寫入怎麼影響 PPU 狀態）
還是 C# 寫死 — JSON 只描述「這個 addr 屬於哪個 IO subsystem」、
「這個寫入需不需要結束 block」之類的 *meta-properties*。

---

## 3. Framework 自動行為（不再是 toggle）

吸收 Gemini 反饋 — 這些東西不是「per-CPU 可選優化」而是「framework
應該永遠 on」：

### 3.1 Decoder pre-extract immediate

`DecoderTable.Decode(opcode)` 已經回 `DecodedInstruction` struct。把它擴
充為包含 *已抽好的* immediate value：

```csharp
public sealed record DecodedInstruction(
    InstructionDef Instruction,
    InstructionFormat Format,
    uint InstructionWord,
    // NEW:
    uint? Immediate,        // null 如果 instr 沒有 imm
    int   InstructionSize   // 1, 2, 3 ... bytes
);
```

Variable-width ISA 在 BlockDetector 走完 fetch 之後，instruction word 包
含完整 instr bytes。Decoder 從 word 抽 imm 給 emitter 用。

Emitter 怎麼用：

```csharp
// Mos6502 LDA #imm 改用 ctx.DecodedInstruction.Immediate.Value
var imm = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8,
    ctx.DecodedInstruction.Immediate ?? throw, false);
ctx.Builder.BuildStore(imm, ctx.GepGpr(0));   // A := imm
```

不再有 `if (block-JIT) bake_const else bus.read()` 分流。**統一一條路**。

### 3.2 Page-bitset SMC

替換 `BlockCache._coverageCount[byte * 64KB]`：

```csharp
private readonly ulong[] _coverageBitset;  // 1 bit per N-byte page
private readonly int _pageShift;            // log2(page_size); default 12 (4KB pages)
```

SMC notify hot path：
```csharp
public bool NotifyMemoryWrite(uint addr) {
    int page = (int)(addr >> _pageShift);
    int word = page >> 6;
    int bit  = page & 63;
    if ((_coverageBitset[word] & (1ul << bit)) == 0) return false;
    // slow path: linear scan blocks at this page
    ...
}
```

對 NES 16-bit (64KB / 4KB-page) = 16 pages = 1 ulong (64 bits 夠用)，比
現在 64KB byte array 還省。對 ARM 32-bit (4GB / 4KB-page) = 1M pages =
128KB bitmap，可接受。

### 3.3 Region-driven block boundary

BlockDetector 自動檢查每條 instr 是否寫入 `forces_end_of_block: true`
region。`STA $8000` 寫到 cart_prg (NES 板子中標 forces_end_of_block) →
BlockDetector 在這個 instr 後結束 block。

不需要 IMapper.PrgBankSwitched callback 補救（保留為 secondary safety net
但不再是主要機制）。

---

## 4. **不**該進 JSON 的東西（避免過度 declarative）

| 項目 | 為什麼留 C# | 替代設計 |
|---|---|---|
| LLVM compile pass list | LLVM 版本綁定；不是 ISA 屬性 | C# enum: `OptimizationLevel.{None,Fast,Aggressive}` |
| 具體 IR 形狀（region inline 的 GEP 內部結構） | 是「怎麼優化」非「what to optimize」 | C# emitter 接 region 屬性後內部組 IR |
| MMC1 shift register 邏輯 | mapper 行為純 C# | `IMapper` interface（已有） |
| 個別 IO 寄存器副作用（PPUCTRL 寫入） | 太多細節 | hook by `side_effects` array name |

**JSON 描述 *what is true*；C# 處理 *how to act on it***。

---

## 5. Migration 計畫（Step-by-step）

每個 step 完成 → T1 + 對應 ROM 測試 + commit + push 後再進下一步。**任何
測試 5 分鐘 timeout cap**（CLAUDE.md 慣例）。

### Step 0：design doc 落地（本 doc）

驗證：本文 commit + push。**不動 code**。

### Step 1：Framework primitives（cross-cutting，必須一次到位）

這些**不能** per-CPU incremental，因為改的是共用 layer：

1.1 加 `MachineSpec` C# class + `spec/machines/_schema.json`
1.2 `DecodedInstruction` struct 擴 `Immediate?` + `InstructionSize`
1.3 `BlockCache` byte-coverage → page-bitset
1.4 `BlockDetector` 加 `forces_end_of_block` 處理

**驗證**：所有現有 test 還過（T1 + nestest 三 backend + blargg cpu_test5
三 backend + GB blargg cpu_instrs json-llvm + block-jit + ARM7TDMI tests）。
**舊路徑保留**，新基建 additive — 沒有 CPU 一定要先 migrate 才能 build。

### Step 2：ARM7TDMI / GBA migrate

2.1 寫 `spec/machines/gba.json`（BIOS / EWRAM / IWRAM / IO / Palette /
    VRAM / OAM / Cart ROM regions）
2.2 寫 `spec/cpu/arm7tdmi/cpu.json` 加 `isa_metadata` section
2.3 GbaMemoryBus 改用 MachineSpec 驅動 dispatch
2.4 ArmEmitters 改用新 DecodedInstruction.Immediate（如有 use point）

**驗證**：T1 + ARM tests + GBA emulator 跑同一個 ROM 跟之前比 perf。

### Step 3：LR35902 / GB migrate

3.1 寫 `spec/machines/gb_dmg.json`
3.2 `spec/cpu/lr35902/cpu.json` 加 `isa_metadata`
3.3 GbMemoryBus 改用 MachineSpec
3.4 Lr35902Emitters 簡化：`FetchImmediate` 不再分流（永遠用 DecodedInstruction.Immediate）；`EmitWriteByteWithSyncAndRamFastPath` 從 region table driven
3.5 移除 GB 的 Lr35902WramBase / Lr35902HramBase 等 `BindExtern` hardcode（變 region driven）

**驗證**：T1 + GB blargg cpu_instrs json-llvm + block-jit 還 11/11。Perf
跟 N1.B' 的 baseline 比，希望一致或更好。

### Step 4：Ricoh 2A03 / NES migrate

4.1 寫 `spec/machines/nes_ntsc.json`
4.2 `spec/cpu/2a03/cpu.json` 加 `isa_metadata`（cycles_per_spec_unit=1 從這
    讀）
4.3 NesMemoryBus 改 MachineSpec
4.4 Mos6502Emitters 補 imm-bake（透過 DecodedInstruction.Immediate；現
    在 NES 沒這優化、移植後該有 perf 提升）
4.5 移除 NesJsonCpu.SmcWriteHook 寫死 — 改 region-driven
4.6 移除 IMapper.PrgBankSwitched callback 跟 Program.cs wiring（forces_end_of_block 接管）

**驗證**：T1 + nestest 三 backend + blargg cpu_test5 三 backend。
跑 perf bench 看 NES block-JIT 是否從 0.83 (per-instr) 拉到 ≥1.5 MIPS
（imm-bake 預期收益）。

### Step 5：cleanup

5.1 把現有 `BlockFunctionBuilder.CyclesPerSpecUnit` 標 obsolete（或刪 —
    因為現在從 spec.json 讀）
5.2 把 `IMapper.PrgBankSwitched` 標 obsolete（或刪）
5.3 doc #18 加 N1 closeout 補丁、doc #19 update status
5.4 寫個 sample doc：「加新 CPU 的 step-by-step」用 N1.B' + N2 之後的 spec
    structure 解釋給未來 contributor

---

## 6. 風險與 mitigation

### 6.1 ARM 可能對 region inline 沒收益（現有沒做、加上去看是否值得）

`Lr35902Emitters.EmitWriteByteWithSyncAndRamFastPath` 是 GB 特化、ARM 沒
做這個。Migration 時 ARM 可能就 fastmem_eligible 但不 emit fastmem IR
— 那就跟現狀一樣，沒退步。Mitigation：分階段，ARM 階段先 *讀* MachineSpec 但 *不啟用* fastmem
emit；確認穩定後再加。

### 6.2 Spec schema 變動會 invalidate 現有 cpu.json

加 `isa_metadata` section 是 additive — 舊 spec 沒這 section 就 fall back
default。schema validator 標 isa_metadata 為 optional。

### 6.3 Framework code 暴增 + 測試 state-space

Gemini 警告：boolean toggles 多了會 N×2^k 配置爆炸。

Mitigation：
- 每個 option 只允許「default」+「override」，不允許多種 value
- 每個 region type 只 3 種 (`ram`/`rom`/`io`)，不允許自訂
- 加 unit test 跑「同一 ROM 在 default + override 結果應該一致」確保
  policy 不改 correctness

### 6.4 Step 大失敗時 revert

每個 step 一個 commit。失敗 → `git revert <hash>` 即可回上一個穩定點。
本 doc 完成度高、step 小 + verify-each，風險可控。

---

## 7. 開放問題

1. `spec/machines/*.json` 跟 multi-platform 的關係：未來如果一個機器有多
   variant（NTSC/PAL NES、DMG/CGB Game Boy）— 是各別 file 還是 file +
   variant section？（傾向各別 file，跟 cpu spec 的 variants 平行）

2. 是否要 namespace 命名：`spec/cpus/` vs `spec/<arch>/`？目前 cpu spec
   在 `spec/<arch>/cpu.json`；若要對齊 `spec/machines/*` 可改 `spec/cpus/<arch>.json`。但這是 large rename 影響很多東西，**留待之後**。

3. `interrupt_check_policy: "end_of_block"` vs 現有「block 退出後 host 檢查
   ConsumePpuNmi」是同義嗎？需要驗證。

---

## 8. Reference

- Doc #18 (N1.B' state abstraction)
- Doc #16 (emulator completeness)
- Doc #17 (framework / emulator timing boundary)
- Gemini 諮詢 record `tools/knowledgebase/message/20260509_163828.txt`
- Gemini 諮詢 record `tools/knowledgebase/message/20260509_135453.txt` (#18 bg)
