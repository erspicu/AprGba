# Spec 撰寫規範

寫新 instruction format / instruction 時遵循這些慣例，可避免後續維護
時的拼字漂移、欄位命名衝突、與 emitter 行為不一致。本文件不是 schema
強制規則（schema 在 `spec/schema/cpu-spec.schema.json`），而是「人類
慣例」。Lint 會檢查強制項目；其他屬於 code review 範疇。

---

## 1. 檔案組織

| 路徑 | 內容 |
|---|---|
| `spec/<arch_id>/cpu.json`              | CPU model 頂層（一份） |
| `spec/<arch_id>/<set_name>.json`       | 一個 instruction set（如 `arm.json`、`thumb.json`） |
| `spec/<arch_id>/groups/*.json`         | （可選）encoding-group 分割檔，用 `$include` 引入 |
| `spec/<arch_id>/formats/*.json`        | （可選）更細顆粒的 format / instruction 分割 |
| `spec/schema/cpu-spec.schema.json`     | JSON Schema validator |
| `MD/design/0X-...md`                   | 設計文件 |

`<arch_id>` 用小寫去掉版本號（`arm7tdmi`、`mos6502`）。`<set_name>` 與
JSON `name` 欄位完全一致（`ARM`、`Thumb`）。

### 1.1 何時拆分

當單一 instruction-set 檔超過 ~500 行、或包含多個概念分明的 encoding
group 時，建議拆分。每個拆分檔通常代表一個 encoding group 或一個
format family。

### 1.2 `$include` 機制

任何 array 內的元素可以是 `{ "$include": "<relative-path>" }` 指令：

```json
"encoding_groups": [
  { "$include": "groups/branch-exchange.json" },
  { "$include": "groups/data-processing.json" },
  { "name": "InlineGroup", "formats": [ ... ] }
]
```

規則：
- **路徑相對於含 directive 的檔案**（不是相對於 cwd 或 repo root）
- **被引入檔的 root 是 array 時**：splice 進父 array（多個元素一次引入）
- **root 是 object 時**：替換 directive 物件位置（一對一）
- **遞迴解析**：被引入檔本身可包含更多 `$include`
- **Cycle 偵測**：A → B → A 會被拒絕（chain-based，同檔多處引用 OK）
- **Schema 驗證在解析後**：拆分檔片段不需要單獨通過 schema，整體合併後才驗

### 1.3 拆分慣例

- 用 `kebab-case` 命名拆分檔（`data-processing.json` 而非
  `DataProcessing.json`）
- 每個 group 檔的 root 是該 group 的完整物件 `{ "name": "...", "formats": [...] }`
- format-level 拆分（更細）：root 通常是單一 format 物件或 format 陣列
- 拆分檔不需要 `$schema` / `spec_version` 欄位 — 那些只在頂層有意義

---

## 2. Bit-pattern 字母慣例

Pattern 是手寫的，最容易飄。**統一字母約定**讓相同語意的 bit 在不同
format 看起來一致：

| 字母 | 用途 | 範例 |
|---|---|---|
| `c`  | Condition code (4 bits)         | `cccc_...` |
| `o`  | Opcode / sub-opcode             | `cccc_001_oooo_...` |
| `d`  | Destination register (Rd)       | `..._dddd_...` |
| `n`  | First source register (Rn)      | `..._nnnn_...` |
| `m`  | Second source register (Rm)     | `..._mmmm` |
| `s`  | S-bit (set flags) **或** Rs (shift register) — 看上下文 | `..._s_..._ssss_..._mmmm` |
| `i`  | Immediate value bits            | `..._iiiiiiii` |
| `r`  | Rotate count（ARM imm8 rotate） | `..._rrrr_iiiiiiii` |
| `t`  | Shift type (LSL/LSR/ASR/ROR)    | `..._tt_..._mmmm` |
| `h`  | RdHi / Rs_high                  | (Multiply Long) |
| `l`  | RdLo / Rs_low                   | (Multiply Long) |
| `L`  | Link bit (B vs BL)              | `cccc_101_L_...` |
| `B`  | Byte/word bit (LDR vs LDRB)     | (SDT) |
| `P`  | Pre/post indexing 或 PSR select | (SDT、PSR Transfer) |
| `U`  | Up/down (add/sub offset)        | (SDT) |
| `W`  | Writeback bit                   | (SDT) |
| `A`  | Accumulate bit (MUL vs MLA)     | (Multiply) |
| `S`  | Signed bit (Multiply Long)      | (區別 UMULL/SMULL) |
| `x`  | Don't-care bit                  | reserved 區段 |

**衝突解決**：若同一 format 內 `s` 兼具 S-bit 與 Rs 兩種角色，於
`fields` 中明確命名（`"s_bit": "20"`、`"rs": "11:8"`）並讓 pattern
letter 對應同一 letter（不同位置不同語意，靠位置區分）。pattern 內
單一 letter 不可在不相連的位置重複（這是 lint 強制）。

---

## 3. Format 命名慣例

`{Group}_{Variant}` 格式：

- `Group` ＝ ARM 編碼空間或 Thumb format 編號
  - `DataProcessing`, `PSR`, `Multiply`, `MultiplyLong`, `SingleDataTransfer`,
    `HalfwordSignedDataTransfer`, `SingleDataSwap`, `BlockDataTransfer`,
    `Branch`, `BranchExchange`, `SoftwareInterrupt`, `Coprocessor`,
    `Undefined`
  - Thumb: `Thumb_F1` ~ `Thumb_F19`
- `Variant` ＝ 區分編碼差異的次要標籤
  - `Immediate`、`RegImmShift`、`RegRegShift`
  - `MRS`、`MSR_Imm`、`MSR_Reg`
  - `B`（unconditional）、`BL`（with link）

範例：
- `DataProcessing_Immediate`
- `DataProcessing_RegImmShift`
- `SingleDataTransfer_Immediate`
- `BlockDataTransfer`
- `Thumb_F4_AluOps`
- `Thumb_F5_HiRegOps`

---

## 4. JSON 欄位命名

- 全部 `lower_snake_case`
- 通用 field 名稱固定：`cond`、`opcode`、`s_bit`、`rd`、`rn`、`rm`、`rs`、
  `imm8`、`imm12`、`imm24`、`offset11`、`offset24`、`rotate`、`shift_type`、
  `shift_amount`、`reg_list`、`p_bit`、`u_bit`、`w_bit`、`b_bit`、`l_bit`、
  `a_bit`、`signed_bit`
- 立即值 field 命名標 bit 寬：`imm8` (8-bit)、`imm12` (12-bit) 等
- `_bit` 後綴僅用於單 bit 旗標欄位

---

## 5. Selector

每個 `instructions[]` 條目（除非 format 只有單一 instruction）必須有
`selector`，格式：

```json
{ "selector": { "field": "opcode", "value": "0100" } }
```

- `field` ＝ format `fields` 中已宣告的欄位名稱
- `value` ＝ 二進位字串（推薦，可讀性高）或十進位整數
- 二進位字串長度 **必須** 等於 selector field 寬度（lint 強制）

---

## 6. Step 寫法常見 idiom

### 6.1 條件執行
不要在 instruction 的 steps 內寫 cond 檢查；instruction-set 層級的
`global_condition` 會自動由 emitter 包成 conditional gate。例外：
標記 `unconditional: true` 的指令（如 Thumb F18 B、ARM v5+ BLX(label)）。

### 6.2 比較類指令（不寫回）

CMP / CMN / TST / TEQ：
```json
{ "writes_pc": "never",
  "steps": [
    { "op": "read_reg", "index": "rn", "out": "rn_val" },
    { "op": "sub", "in": ["rn_val", "op2_value"], "out": "result" },
    { "op": "update_nz",    "value": "result" },
    { "op": "update_c_sub", "in": ["rn_val", "op2_value"] },
    { "op": "update_v_sub", "in": ["rn_val", "op2_value", "result"] }
  ]
}
```

對應關係：
- CMP = sub + update flags（不寫 Rd）
- CMN = add + update flags（不寫 Rd）
- TST = and + update_nz + update_c_shifter
- TEQ = xor + update_nz + update_c_shifter

### 6.3 ALU 寫回 + 條件 flag 更新

```json
"steps": [
  { "op": "read_reg", "index": "rn", "out": "rn_val" },
  { "op": "<op>", "in": ["rn_val", "op2_value"], "out": "result" },
  { "op": "write_reg", "index": "rd", "value": "result" },
  { "op": "if", "cond": { "field": "s_bit", "eq": 1 }, "then": [
    { "op": "if", "cond": { "field": "rd", "eq": 15 }, "then": [
      { "op": "restore_cpsr_from_spsr" }
    ], "else": [
      { "op": "update_nz", "value": "result" },
      { "op": "update_<c_kind>", "in": [...] },
      { "op": "update_<v_kind>", "in": [...] }
    ] }
  ] }
]
```

「rd == 15 → restore CPSR from SPSR」是 ARM ALU 寫 PC 且 S=1 的標準
邏輯，所有支援該行為的 ALU 指令都應遵此 idiom。

### 6.4 記憶體存取
讀寫指令統一用 `load` / `store` micro-op；不要自己組 GEP。 alignment
與 byte rotate 由 micro-op 內處理（emitter 知道 memory_model 設定）。

### 6.5 Branch 變體

| 指令 | 用 |
|---|---|
| `B`             | `branch` |
| `BL`            | `branch_link`（自動寫 LR） |
| `BX`            | `branch_indirect`（自動處理 T-bit + 對齊） |

---

## 7. Cycles 標記

最常見格式：
```json
"cycles": { "form": "1S" }
"cycles": { "form": "2S+1N" }
"cycles": { "form": "1S",       "extra_when_dest_pc": "+1S+1N" }
"cycles": { "form": "1S+1N+1I", "extra_when_load_pc": "+1S+1N" }
"cycles": { "form": "(n)S+1N+1I", "computed_at": "runtime" }
```

`S/N/I/C` 字母即標準 ARM cycle notation；`+` 加總。`computed_at:
"runtime"` 表示週期值不能在 compile time 算定（如 LDM 跟 register list
位元數有關）。

---

## 8. Quirks

`quirks` 是字串陣列，標註 emitter 需要特別處理的硬體怪癖。已使用：
- `unaligned_rotates` — 未對齊 LDR 的 rotated read
- `bl_hi_half` / `bl_lo_half` — Thumb F19 BL 的兩半
- `pc_in_writeback` — SDT writeback 時 Rn=PC 的特例
- `smc_hazard` — 寫入後可能觸發 SMC，block compiler 留意

新增 quirk 時先在本檔追加說明再使用。

---

## 9. `manual_ref`

格式：`"ARM ARM A4.1.3"` 或 `"GBATEK CPU.Instructions.ARM"`。讓人查到
原始規格段落。對 ARMv4T 主要參考：
- *ARM Architecture Reference Manual*（DDI 0100 或 ARMv5 含 v4T 章節）
- *ARM7TDMI Data Sheet*
- *GBATEK*（Martin Korth 維護的 GBA 完整規格）

---

## 10. `$comment` 與 `comment`

- JSON 標準沒有 native comments，所以採兩種：
  - 鍵值對 `"comment": "..."`：附在 format/instruction 上，描述用
  - 鍵 `"$comment_<topic>"`：附在物件層，工具忽略此前綴
- Schema 已宣告 `additionalProperties: false`，但 `$comment_*` 鍵仍會被
  schema 拒絕；使用 `comment` 比較保險（schema 已允許）

---

## 11. 指令排序習慣

format 內 `instructions[]` 順序：
1. 第一順位：mnemonic 字母順序
2. 例外：當 selector 值有自然順序（如 ARM Data Processing 的 0000–1111）
   時，按 selector value 升冪排

format 之間（同一 group 內）：依 priority — 高 specificity（mask 中
1 較多者）先列。

---

## 12. Lint 強制項目（會被 SpecLoader 拒絕）

- pattern 長度 = `width_bits`
- pattern 字母 run 與 `fields[]` 宣告的 BitRange 完全對應
- pattern 推導的 mask/match 與 declared mask/match 一致
- field BitRange 範圍不超出 `[0, width_bits-1]`
- field BitRange 之間不重疊
- selector value 的 bit-string 長度等於 selector field 寬度
  （integer 形式則檢查值不超過 `2^width - 1`）

---

## 13. Lint 警告項目（會印到 stdout 但不擋）

- format 有 >1 instruction 卻無 selector
- pattern 含 `x` (don't-care) 但鄰接欄位範圍未 cover 該位置（可能是漏寫）
- micro-op 名稱不在標準 vocabulary 也不在 `custom_micro_ops`
  （emitter 階段會 hard error，但 lint 階段先警告）

---

## 14. 變更時的影響檢查

新增/修改 spec 後，必須跑：

```
dotnet test                     # 35+ tests, includes coverage matrix
dotnet run --project src/AprCpu.Compiler -- \
    --spec spec/arm7tdmi/cpu.json --output temp/arm7tdmi.ll
```

CLI 必須輸出 0 diagnostics；測試必須全綠。
