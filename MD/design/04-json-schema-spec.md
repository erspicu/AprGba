# CPU 規格 JSON Schema 設計

本文件定義 AprCpu 框架使用的 CPU 規格描述格式。一份規格檔（或一組規格檔）
完整描述一款 CPU，包含暫存器架構、模式切換、指令編碼格式、語義微操作、
週期成本與例外向量。Parser 讀入後即可產生對應的解碼器、IR emitter 與
執行邏輯。

---

## 1. 設計目標

| 目標 | 具體要求 |
|---|---|
| **完整性** | 能完整描述 ARM7TDMI；架構可橫向擴展到 ARMv5/v6/v7-A、6502、MIPS |
| **多模式指令長度** | 支援 16/32 bit 切換（ARM/Thumb）、Thumb-2 變長混合、未來 ARM64 純 32 |
| **CPU 型號區分** | 同一架構可有多個 variant（ARM7TDMI、ARM946E-S）共享 base spec |
| **編碼空間覆蓋** | 用 mask/match 表達 RISC 規律編碼，自動生成解碼樹 |
| **語義可執行** | Micro-op 序列可直接 emit LLVM IR，不需人工撰寫每條指令 |
| **可繼承擴充** | ARMv5 spec 用 `extends: ARMv4T` 即可繼承並 override |
| **可機器驗證** | 對應 JSON Schema 可在 IDE / CI 自動 lint |
| **人類可讀** | 直接看 JSON 即可理解編碼與語義（avoid 過度縮寫） |

## 2. 設計原則

1. **資料優先**：spec 檔為純資料，零執行邏輯。所有「行為」由命名 micro-op
   表達，micro-op 實作在 Parser 中（不在 spec 中）。
2. **Encoding-based 描述**：以「指令格式」為核心單位，而非窮舉 opcode。
   一個格式涵蓋一族（family）指令，內部以 sub-opcode 分派。
3. **顯式優於隱式**：bit 範圍、register 索引、立即值來源都明確標注，
   避免「靠慣例推斷」。
4. **模組分檔**：頂層 `cpu.json` 描述 CPU 模型，每個 instruction set
   獨立一個檔（`arm.json`、`thumb.json`），方便維護。
5. **版本化**：頂層欄位 `spec_version` 紀錄 schema 版本，未來改版可平滑遷移。

---

## 3. 檔案組織

```
spec/
├── schema/
│   └── cpu-spec.schema.json         # JSON Schema (Draft 2020-12)
├── arm7tdmi/
│   ├── cpu.json                     # CPU 模型（regs、modes、vectors）
│   ├── arm.json                     # ARM (32-bit) instruction set
│   └── thumb.json                   # Thumb (16-bit) instruction set
└── mos6502/                         # （未來擴充示範）
    ├── cpu.json
    └── main.json
```

`cpu.json` 透過 `instruction_sets` 欄位指向同目錄下的其他檔。Parser 讀
`cpu.json` 即拉起完整模型。

---

## 4. 頂層結構（cpu.json）

```json
{
  "$schema": "../schema/cpu-spec.schema.json",
  "spec_version": "1.0",

  "architecture": {
    "id":         "ARMv4T",
    "family":     "ARM",
    "extends":    null,
    "endianness": "little",
    "word_size_bits": 32
  },

  "variants": [
    {
      "id":   "ARM7TDMI",
      "core": "ARM7",
      "features": ["T", "D", "M", "I"],
      "notes": "GBA / NDS ARM core"
    }
  ],

  "register_file": { /* §5 */ },
  "processor_modes": { /* §6 */ },
  "exception_vectors": [ /* §7 */ ],

  "instruction_sets": [
    { "name": "ARM",   "file": "arm.json"   },
    { "name": "Thumb", "file": "thumb.json" }
  ],

  "instruction_set_dispatch": {
    "selector":       "CPSR.T",
    "selector_values": { "0": "ARM", "1": "Thumb" },
    "switch_via":     ["BX", "BLX"],
    "transition_rule":
      "Target address bit[0]==1 → switch to Thumb (T←1) and clear bit[0] before fetch."
  },

  "memory_model": {
    "default_endianness": "little",
    "alignment_policy": {
      "load_unaligned":  "rotate_within_word",
      "store_unaligned": "force_align_low_bits"
    }
  }
}
```

### 4.1 `architecture`
- `id`: 唯一識別（`ARMv4T`、`ARMv5TE`、…）
- `family`: 大類（`ARM`、`MIPS`、`6502`、…）
- `extends`: 若繼承自其他 spec，填其 `id`，繼承其 register/mode/format
  定義，當前 spec 只需描述差異
- `endianness`: 預設位元序，可被 `memory_model` 或個別指令 override
- `word_size_bits`: 主要字長（解碼樹分組、立即值上限參考用）

### 4.2 `variants`
同一架構下的 CPU 型號表。`features` 對應 ARM 字尾標記（T=Thumb、D=Debug、
M=Multiplier、I=ICE、E=DSP extensions、J=Jazelle…），框架據此啟用對應
指令集區段。

### 4.3 `instruction_sets`
列出各模式對應的 spec 檔。Parser 依 `instruction_set_dispatch.selector`
（CPU 狀態暫存器中的某 bit）切換解碼路徑。

### 4.4 `memory_model`
描述記憶體存取的「全 CPU 共通」行為。指令層級若有特例（例如 LDM/STM 對
PC 的特殊處理），於指令定義 `quirks` 標注。

---

## 5. 暫存器檔（`register_file`）

```json
"register_file": {
  "general_purpose": {
    "count": 16,
    "width_bits": 32,
    "names": ["R0","R1","R2","R3","R4","R5","R6","R7",
              "R8","R9","R10","R11","R12","R13","R14","R15"],
    "aliases": { "SP": "R13", "LR": "R14", "PC": "R15" },
    "pc_index": 15
  },

  "status": [
    {
      "name": "CPSR",
      "width_bits": 32,
      "fields": {
        "N": "31", "Z": "30", "C": "29", "V": "28",
        "Q": "27",
        "I": "7",  "F": "6",  "T": "5",
        "M": "4:0"
      }
    },
    {
      "name": "SPSR",
      "width_bits": 32,
      "banked_per_mode": ["FIQ", "IRQ", "Supervisor", "Abort", "Undefined"]
    }
  ]
}
```

- `pc_index` 明示 PC 位置（ARM = 15、MIPS = 隱含於 special、6502 = 獨立）。
- `aliases` 提供 micro-op 引用 SP/LR/PC 的便利路徑。
- 狀態暫存器以 `fields` 描述位元配置；`update_n`、`update_z` 等 micro-op
  透過此表知道要寫入哪個 bit。
- `banked_per_mode` 列出此暫存器在哪些處理器模式下有獨立副本（影子暫存器）。

### 5.1 `register_pairs`（Phase 4.5 加入）

對於 LR35902 (Game Boy)、Z80、6502 等 8-bit 主架構，會把兩個 8-bit GPR
組成 16-bit pair 用：

```json
"register_file": {
  "general_purpose": {
    "count": 7,
    "width_bits": 8,
    "names": ["A", "B", "C", "D", "E", "H", "L"]
  },
  "register_pairs": [
    { "name": "BC", "high": "B", "low": "C" },
    { "name": "DE", "high": "D", "low": "E" },
    { "name": "HL", "high": "H", "low": "L" },
    { "name": "AF", "high": "A", "low": "F" }
  ],
  "stack_pointer": "SP"
}
```

- `name` 是 pair 的對外名稱，spec steps / operand resolvers 可用它直接讀寫。
- `high` / `low` 對應到 `general_purpose.names` 裡的 8-bit register。
- micro-op `read_reg_pair` / `write_reg_pair` 會自動 emit
  `(high << 8) | low` / `(value >> 8 & 0xFF) → high; value & 0xFF → low`
  的合成/拆解 IR。
- pair 名稱也可以在 `aliases` 出現給 SP/LR 等 alias 用（LR35902 的
  `stack_pointer: "SP"` 視為獨立 16-bit register 而非 pair）。

ARM7TDMI 不需要這欄位（GPR 全 32-bit）；schema 上是可選的。

---

## 6. 處理器模式（`processor_modes`）

```json
"processor_modes": {
  "modes": [
    { "id": "User",       "encoding": "10000", "privileged": false },
    { "id": "FIQ",        "encoding": "10001", "privileged": true  },
    { "id": "IRQ",        "encoding": "10010", "privileged": true  },
    { "id": "Supervisor", "encoding": "10011", "privileged": true  },
    { "id": "Abort",      "encoding": "10111", "privileged": true  },
    { "id": "Undefined",  "encoding": "11011", "privileged": true  },
    { "id": "System",     "encoding": "11111", "privileged": true  }
  ],

  "banked_registers": {
    "FIQ":        ["R8","R9","R10","R11","R12","R13","R14"],
    "IRQ":        ["R13","R14"],
    "Supervisor": ["R13","R14"],
    "Abort":      ["R13","R14"],
    "Undefined":  ["R13","R14"],
    "System":     []
  }
}
```

`encoding` 即 CPSR.M 欄位的 bit pattern。Parser 依此實作 mode-switch 邏輯
與 banked register swap。

---

## 7. 例外向量（`exception_vectors`）

```json
"exception_vectors": [
  { "name": "Reset",            "address": "0x00000000",
    "enter_mode": "Supervisor", "disable": ["I","F"] },
  { "name": "UndefinedInstruction", "address": "0x00000004",
    "enter_mode": "Undefined" },
  { "name": "SoftwareInterrupt",    "address": "0x00000008",
    "enter_mode": "Supervisor", "disable": ["I"] },
  { "name": "PrefetchAbort",        "address": "0x0000000C",
    "enter_mode": "Abort",      "disable": ["I"] },
  { "name": "DataAbort",            "address": "0x00000010",
    "enter_mode": "Abort",      "disable": ["I"] },
  { "name": "IRQ",                  "address": "0x00000018",
    "enter_mode": "IRQ",        "disable": ["I"] },
  { "name": "FIQ",                  "address": "0x0000001C",
    "enter_mode": "FIQ",        "disable": ["I","F"] }
]
```

`disable` 列出進入向量時自動遮罩的中斷旗標。

---

## 8. 指令集（`arm.json` / `thumb.json`）

```json
{
  "$schema": "../schema/cpu-spec.schema.json",
  "spec_version": "1.0",

  "name":              "ARM",
  "width_bits":        32,
  "alignment_bytes":   4,
  "pc_offset_bytes":   8,
  "endian_within_word": "little",

  "global_condition": {
    "field": "31:28",
    "table": {
      "0000": "EQ",  "0001": "NE",  "0010": "CS",  "0011": "CC",
      "0100": "MI",  "0101": "PL",  "0110": "VS",  "0111": "VC",
      "1000": "HI",  "1001": "LS",  "1010": "GE",  "1011": "LT",
      "1100": "GT",  "1101": "LE",  "1110": "AL",  "1111": "NV"
    },
    "applies_to": "all_unless_marked_unconditional"
  },

  "decode_strategy": "mask_match_priority",

  "encoding_groups": [
    { "name": "DataProcessing", "formats": [ /* §9 */ ] },
    { "name": "PSR_Transfer",   "formats": [ ... ] },
    { "name": "Multiply",       "formats": [ ... ] },
    { "name": "LoadStore",      "formats": [ ... ] },
    { "name": "Branch",         "formats": [ ... ] },
    { "name": "Coprocessor",    "formats": [ ... ] }
  ]
}
```

### 8.1 `width_bits` / `alignment_bytes`
- ARM = 32/4，Thumb = 16/2
- 未來 Thumb-2 = `"variable"`，搭配 `width_decision` 欄位描述如何依首
  5 bits 判定指令長度（見 §13）

### 8.2 `pc_offset_bytes`
ARM 三段式 pipeline 造成 R15 讀取時超前。執行時讀 PC 自動加上此值。
ARM = +8、Thumb = +4。

### 8.3 `global_condition`
若所有指令前 4 bits 都是 condition（ARM 經典模式），於此統一描述，避免
每個格式重複。`applies_to` 例外：`unconditional`（如 `BLX(label)`）會在
個別指令層級以 `unconditional: true` 標注。

### 8.4 `decode_strategy`
- `mask_match_priority`：依宣告順序逐一比對，第一個命中即用。需開發者
  自己保證沒有歧義。
- `mask_match_specificity`：自動依 mask 中 1 的數量排序（特殊優先於通用）
- `tree`：Parser 自動建解碼樹（建構成本高、執行最快）

第一版先支援 `mask_match_priority`。

---

## 9. 編碼格式（Encoding Format）

格式是「一族指令」的共用模板。下面為 ARM Data Processing（暫存器位移
立即值版）的範例：

```json
{
  "name": "DataProcessing_RegImmShift",
  "comment": "cccc 000 oooo s nnnn dddd ssss s tt 0 mmmm",

  "pattern": "cccc_000_oooo_s_nnnn_dddd_ssssstt0mmmm",
  "fields": {
    "cond":         "31:28",
    "opcode":       "24:21",
    "s_bit":        "20",
    "rn":           "19:16",
    "rd":           "15:12",
    "shift_amount": "11:7",
    "shift_type":   "6:5",
    "rm":           "3:0"
  },
  "mask":  "0x0E000010",
  "match": "0x00000000",

  "operands": {
    "op2": {
      "kind": "shifted_register_by_immediate",
      "rm":            "rm",
      "shift_type":    "shift_type",
      "shift_amount":  "shift_amount",
      "outputs": ["op2_value", "shifter_carry_out"]
    }
  },

  "instructions": [
    {
      "selector": { "field": "opcode", "value": "0100" },
      "mnemonic": "ADD",
      "since":    "ARMv4",
      "cycles":   { "form": "1S", "extra_when_dest_pc": "+1S+1N" },
      "steps": [
        { "op": "read_reg",  "index": "rn",     "out": "rn_val" },
        { "op": "add",       "in": ["rn_val", "op2_value"], "out": "result" },
        { "op": "write_reg", "index": "rd",     "value": "result" },
        { "op": "if", "cond": { "field": "s_bit", "eq": 1 }, "then": [
          { "op": "if", "cond": { "var": "rd", "eq": 15 }, "then": [
            { "op": "restore_cpsr_from_spsr" }
          ], "else": [
            { "op": "update_nz",      "value": "result" },
            { "op": "update_c_add",   "in": ["rn_val", "op2_value"] },
            { "op": "update_v_add",   "in": ["rn_val", "op2_value", "result"] }
          ]}
        ]}
      ],
      "writes_pc": "conditional_via_rd"
    }
  ]
}
```

### 9.1 `pattern`
位元字串，**固定長度 = `width_bits`**，分組以底線分隔僅為視覺。每個字元
代表一個 bit：

| 字元 | 意義 |
|---|---|
| `0` / `1` | 該 bit 必為此值（進入 mask/match） |
| 字母（`a`–`z`） | 欄位佔位，由 `fields` 命名 |
| `x` | don't-care（不進 mask） |

Parser 從 pattern 推導 `mask` 與 `match`：
- `mask`  = 所有 `0`/`1` 位元為 1，其餘為 0
- `match` = `0`/`1` 直接套入

`pattern`、`mask`、`match` 三者擇一即可（建議三者並寫，由 Parser 在
load 時 cross-check，發現不一致即報錯）。

### 9.2 `fields`
欄位名 → bit 範圍。範圍語法 `"hi:lo"`（含端點）或單一 `"n"`。
所有欄位範圍 union 加上 mask bits 必須等於整條指令寬度，否則 lint 報錯。

### 9.3 `operands`
描述「複合運算元」的解析步驟。常見的 `kind`：

| kind | 說明 | outputs |
|---|---|---|
| `register_direct`            | 直接讀某欄位作為暫存器索引 | `value` |
| `immediate_value`            | 直接以欄位 bits 為立即值 | `value` |
| `immediate_rotated`          | ARM 8-bit imm + 4-bit rotate | `value`, `shifter_carry_out` |
| `shifted_register_by_immediate` | Rm + shift type + shift amount | `value`, `shifter_carry_out` |
| `shifted_register_by_register`  | Rm + shift type + Rs (低 8 bits) | `value`, `shifter_carry_out` |
| `pc_relative_offset`         | sign-extend imm + PC | `address` |
| `register_list`              | 16-bit register bitmap (LDM/STM) | `list` |

`operands` 中定義的 outputs 在 `steps` 中可直接以名稱引用。

### 9.4 `instructions[]`
同格式下以 `selector` 在 sub-opcode 上分派。

| 欄位 | 必填 | 說明 |
|---|---|---|
| `selector`   | ✓ | `{ "field": "opcode", "value": "0100" }` |
| `mnemonic`   | ✓ | 助記符 |
| `since`      |   | 自哪個 ISA 版本起可用 |
| `until`      |   | 哪個 ISA 版本起被棄用 |
| `cycles`     | ✓ | 週期成本 |
| `steps`      | ✓ | Micro-op 序列 |
| `unconditional` |   | true 時忽略 global_condition |
| `writes_pc`  |   | `"never"` / `"always"` / `"conditional_via_rd"` |
| `quirks`     |   | 字串陣列；標記特殊行為（ex: `"unaligned_rotates"`、`"smc_hazard"`） |
| `manual_ref` |   | 「ARM ARM §A4.1.6」之類來源出處 |

---

## 10. Micro-op 步驟格式

每個 step 是一個 JSON 物件，必要欄位 `op` 表示操作名，其餘欄位依 op 而異。
完整 vocabulary 見 `05-microops-vocabulary.md`。

### 10.1 變數命名空間
Step 可引用的「值」來自：

1. **Field 值**（從格式 `fields` 抽出的 raw bits）— 直接用欄位名引用，
   值為 unsigned 整數
2. **Operand 解析輸出**（`operands` 段定義的 outputs）— 直接用 output 名引用
3. **前面 step 的 `out`** — 後續 step 透過名稱引用
4. **特殊內建符號**：`PC`, `CPSR`, `SPSR`, `MODE`, `CYCLE_COUNTER`

命名衝突偵測由 Parser 在 lint 階段報錯。

### 10.2 引用語法

| 語法 | 意義 |
|---|---|
| `"name"` | 引用一個值（field / operand output / step out） |
| `{ "field": "rd", "eq": 15 }` | 條件比較式（給 `if`、`switch`） |
| `{ "var": "result", "lt": 0 }` | 同上，引用 step out 變數 |
| `{ "const": 42 }` | 立即常數 |
| `{ "const": "0xFF000000" }` | 16 進位常數 |

### 10.3 控制流類 micro-op

```json
{ "op": "if",
  "cond": { "field": "s_bit", "eq": 1 },
  "then": [ /* steps */ ],
  "else": [ /* steps, optional */ ] }

{ "op": "switch",
  "selector": "opcode",
  "cases": {
    "0000": [ /* steps */ ],
    "0001": [ /* steps */ ]
  },
  "default": [ /* steps */ ] }

{ "op": "block_terminate", "reason": "branch_taken", "next_pc": "target" }
```

`block_terminate` 是 JIT 友善的核心 op：告訴 block compiler 「此處 block
結束，跳到 next_pc」，後端據此決定是否 inline 或 fall-back to host loop。

---

## 11. 週期成本（`cycles`）

ARM 經典 cycle notation：
- **S** (Sequential)  — 依序記憶體存取
- **N** (Non-sequential) — 非依序存取
- **I** (Internal)    — 內部運算
- **C** (Coprocessor) — coprocessor 存取

```json
"cycles": {
  "form": "1S",
  "form_alt": ["1S", "1N", "1I"],
  "extra_when_dest_pc":   "+1S+1N",
  "extra_when_load_pc":   "+1S+1N",
  "computed_at":          "compile_time"
}
```

| 欄位 | 意義 |
|---|---|
| `form`            | 主要週期式 |
| `form_alt`        | 條件性多種形式（依運算元類型） |
| `extra_when_*`    | 條件加成 |
| `computed_at`     | `compile_time`（常數週期）／`runtime`（依匯流排狀態，例如 LDM） |

**第一版實作備註**：先以 `form` 內 S/N/I 個數加總為粗略週期值即可，wait
state 由 memory bus 在 runtime 補正。完整 cycle-accurate 屬未來工作。

---

## 12. 繼承與擴充

### 12.1 ISA 繼承

```json
{
  "architecture": {
    "id": "ARMv5TE",
    "family": "ARM",
    "extends": "ARMv4T"
  },
  "instruction_sets": [
    { "name": "ARM",
      "file": "arm-v5te-additions.json",
      "extends": { "spec": "ARMv4T", "set": "ARM" } }
  ]
}
```

`extends` 表示**這個 spec 為 base 的疊加層**：
- 共用的 register/mode/vector 不需重述
- `encoding_groups` 採併集；同名 format 視為 override
- 個別 instruction 可用 `selector` 完全相同 + `since: "ARMv5TE"` 表示新增
- 標 `until` 即表示在新 ISA 中該指令無效

### 12.2 CPU variant 啟用 feature

`variants[].features` 列舉的 feature flag 可在 instruction 層級當條件：

```json
{ "mnemonic": "BKPT", "requires_feature": "D" }
```

Parser 載入特定 variant 時，過濾掉不符合 feature 的指令。

### 12.3 自訂 micro-op

若 spec 需要 base vocabulary 沒有的特殊操作（例如 ARM Jazelle、6502 BCD
加法），可宣告：

```json
"custom_micro_ops": [
  { "name": "bcd_add",
    "inputs": ["a", "b", "carry_in"],
    "outputs": ["sum", "carry_out"],
    "implementation_hint": "model A+B as decimal nibbles" }
]
```

C# 端必須註冊對應的 emitter handler；spec 中可立即引用 `{ "op": "bcd_add" }`。

---

## 13. 變長指令（Thumb-2 預備設計）

Thumb-2 指令長度由首 5 bits 決定：

```json
{
  "name": "Thumb2",
  "width_bits": "variable",
  "width_decision": {
    "first_unit_bits": 16,
    "rule": {
      "field": "[15:11]",
      "long_when_in": ["11101", "11110", "11111"],
      "long_total_bits": 32
    }
  }
}
```

第一版（Phase 1–6）只實作 Thumb 1（固定 16），但 schema 預留此欄位以
保證未來擴充不需破壞性改版。

---

## 14. 指令的 cycle / state 副作用標註

完整模擬需要知道一條指令是否：

- 會修改 PC？(`writes_pc`)
- 會修改記憶體？(`writes_memory: ["normal", "io_register"]`)
- 是否會觸發 mode switch？(`changes_mode: true`)
- 是否會切換 instruction set？(`switches_instruction_set: true`)
- 是否需要 IO write barrier？(`requires_io_barrier: true`)

這些是 JIT 後端的 hint：例如 `requires_io_barrier` 為 true 的指令會被
標記為 block terminator，強制執行完後同步 PPU/Timer。

---

## 15. 驗證規則（Schema lint）

JSON Schema validator 與 Parser 的 lint 階段共同執行下列檢查：

| 檢查 | 等級 |
|---|---|
| pattern 長度 == width_bits | error |
| field 範圍超出 width | error |
| field 範圍互相重疊 | error |
| pattern / mask / match 三者一致 | error |
| 同 set 內 mask/match 互相覆蓋（歧義） | warning（mask_match_priority 模式下） |
| sub-opcode value 長度與 selector field 寬度不符 | error |
| step 引用未定義的變數 | error |
| step `out` 名稱重複 | error |
| micro-op 名稱不在 vocabulary 也不在 custom_micro_ops | error |
| `cycles.form` 字串格式不合 | warning |
| `extends` 指向不存在的 base spec | error |

---

## 16. 完整檔案載入流程

```
1. cpu.json
   ↓ resolve $schema, validate
   ↓ load architecture, register_file, modes, vectors
2. instruction_sets[].file → arm.json / thumb.json
   ↓ resolve $schema, validate
   ↓ if extends: pull base spec, merge encoding_groups
3. For each format:
   ↓ derive mask/match from pattern (or verify consistency)
   ↓ register operand resolvers
   ↓ register instructions (filter by variant features)
4. Build decoder structure (priority list / tree)
5. Emit pre-compiled micro-op handler templates (Phase 7+)
```

---

## 17. 未來擴充欄位（保留，第一版不實作）

- `pipeline_model` — Fetch/Decode/Execute stages 描述（cycle-accurate 用）
- `coprocessor_interface` — CP14/CP15 完整 register map
- `simd_lanes` — NEON / VFP register file
- `tlb_model` — MMU 模擬
- `power_states` — WFI/WFE/sleep 行為
- `microarchitecture_hints` — branch predictor 配置等

---

## 18. 與其他規格語言的對照

| 概念 | AprCpu schema | Ghidra SLEIGH | QEMU decodetree | Sail |
|---|---|---|---|---|
| 編碼描述 | `pattern` 字串 | `:opname mask & match` | `pattern` + `field` | `mapping` clause |
| 語義 | micro-op JSON 步驟 | P-Code | 內嵌 C macro | Sail expressions |
| 暫存器 | `register_file` JSON | `define register` | C 全域 | `register` 宣告 |
| 條件執行 | `global_condition` + `if` micro-op | macro 包裝 | helper function | match clause |
| 繼承 | `extends` | `include` | C #include | `include` |
| 自訂 op | `custom_micro_ops` | 內嵌 P-Code | 直接寫 C | 任意 expression |

我們的特色：**完全結構化 JSON、語義 micro-op vocabulary 受控、明確繼承
語意、適合 .NET 工具鏈與 LLVM 後端**。

---

## 附錄 A：ARM7TDMI 完整 spec 規模估計

| 項目 | 數量 |
|---|---|
| 處理器模式 | 7 |
| 例外向量 | 7 |
| ARM encoding format | ~12（Data Processing × 3、Multiply × 2、Load/Store × 3、Branch、PSR、SWI、Coprocessor） |
| ARM 指令（mnemonic 不重複） | ~40 |
| Thumb encoding format | 19（依官方分類） |
| Thumb 指令（mnemonic 不重複） | ~30 |
| 共用 micro-op | ~50 |
| 估計 spec 檔總行數 | 1500–2500 行 JSON |
