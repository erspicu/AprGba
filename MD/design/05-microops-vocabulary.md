# Micro-op 詞彙表（Reference）

本文件為 AprCpu schema (`04-json-schema-spec.md`) 中 `steps[]` 可使用的
**全部 base micro-op** 規格參考。Parser 內部為每個 micro-op 維護一個
LLVM IR emitter；新增指令時優先以這些 op 組合表達，不夠才宣告
`custom_micro_ops`。

---

## 0. 通用約定

- **Width attr**：算術 / 邏輯 / 位移類預設 `width: 32`，可在 step 上覆寫
  （如 Thumb 8-bit immediate 載入用 `width: 8` + zero-extend）。
- **`out` 命名**：每個 step 的 `out` 在當前指令範圍內必須唯一。SSA 友善。
- **`in` / `value`**：可為「變數名稱字串」、`{ "const": ... }`、
  `{ "field": ... }`。Parser 自動推斷型別並補 zero/sign extend。
- **副作用**：標 ⚠ 者代表會修改 CPU 全域狀態（CPSR/PC/memory），JIT block
  compiler 需特別處理。
- **LLVM IR 對應**：欄位給予的是 **典型** mapping，實際 emit 可能因型別
  推升而插入 `zext`/`sext`/`trunc`。

---

## 1. 算術 (Arithmetic)

| op | inputs | out | LLVM IR | 說明 |
|---|---|---|---|---|
| `add`  | `in: [a, b]`        | `out` | `add` | 二補數加法（無 carry） |
| `adc`  | `in: [a, b, cin]`   | `out` | `add` ×2 | 加上 Carry-in |
| `sub`  | `in: [a, b]`        | `out` | `sub` | a − b |
| `sbc`  | `in: [a, b, cin]`   | `out` | `sub` + adjust | a − b − !cin |
| `rsb`  | `in: [a, b]`        | `out` | `sub` | b − a (Reverse Sub) |
| `rsc`  | `in: [a, b, cin]`   | `out` |  | b − a − !cin |
| `mul`  | `in: [a, b]`        | `out` | `mul` | 32×32 → 32（取低位） |
| `mul_hi` | `in: [a, b]`      | `out` | `mul` + shift | 取高 32 bits |
| `umul64` | `in: [a, b]`      | `out` (i64) | `zext`+`mul` | 32×32 → 64 unsigned |
| `smul64` | `in: [a, b]`      | `out` (i64) | `sext`+`mul` | 32×32 → 64 signed |
| `neg`  | `in: [a]`           | `out` | `sub 0` | −a |
| `abs`  | `in: [a]`           | `out` | `select` | |a| |
| `min` / `max` | `in: [a, b], signed: bool` | `out` | `select` |  |

---

## 2. 邏輯 (Logical)

| op | inputs | out | LLVM IR | 說明 |
|---|---|---|---|---|
| `and`  | `in: [a, b]` | `out` | `and` |  |
| `or`   | `in: [a, b]` | `out` | `or`  |  |
| `xor`  | `in: [a, b]` | `out` | `xor` |  |
| `not`  | `in: [a]`    | `out` | `xor -1` | bitwise NOT |
| `bic`  | `in: [a, b]` | `out` | `and a, ~b` | a AND NOT b |

---

## 3. 位移 (Shift / Rotate)

| op | inputs | out | LLVM IR | 說明 |
|---|---|---|---|---|
| `shl`  | `in: [v, n]` | `out`, `carry_out?` | `shl` | logical shift left |
| `lsr`  | `in: [v, n]` | `out`, `carry_out?` | `lshr` | logical shift right |
| `asr`  | `in: [v, n]` | `out`, `carry_out?` | `ashr` | arithmetic shift right |
| `ror`  | `in: [v, n]` | `out`, `carry_out?` | intrinsic `fshr` | rotate right |
| `rrx`  | `in: [v, cin]` | `out`, `carry_out?` | `lshr 1` + insert | rotate right through carry |

ARM Barrel Shifter 行為（n=0 / n=32 / n>32 等邊界）由
`operands.shifted_register_*` 在 emit 階段處理，micro-op 本身只做純粹位移。

---

## 4. 比較 / 測試 (Compare / Test)

| op | inputs | out | LLVM IR |
|---|---|---|---|
| `cmp_eq`  | `in: [a, b]` | `out: i1` | `icmp eq`  |
| `cmp_ne`  | `in: [a, b]` | `out: i1` | `icmp ne`  |
| `cmp_ult` | `in: [a, b]` | `out: i1` | `icmp ult` |
| `cmp_slt` | `in: [a, b]` | `out: i1` | `icmp slt` |
| `cmp_ule` | `in: [a, b]` | `out: i1` | `icmp ule` |
| `cmp_sle` | `in: [a, b]` | `out: i1` | `icmp sle` |

通常 `CMP`/`TST` 指令的實作不直接用這幾個 op，而是執行 sub/and 並 update
flags（見 §6）。這些保留給控制流條件判斷。

---

## 5. 位元操作 (Bit manipulation)

| op | inputs | out | 說明 |
|---|---|---|---|
| `bitfield_extract` | `in: [v], lsb, width, signed: bool` | `out` | 取 bit field |
| `bitfield_insert`  | `in: [dst, val], lsb, width` | `out` | 插入 bit field |
| `clz`              | `in: [v]` | `out` | Count Leading Zeros (ARMv5+) |
| `popcount`         | `in: [v]` | `out` | （未來）|
| `byte_swap`        | `in: [v]` | `out` | endian swap (REV / BSWAP) |
| `sign_extend`      | `in: [v], from_bits` | `out` | sign-extend to target width |
| `zero_extend`      | `in: [v], from_bits` | `out` | zero-extend to target width |
| `truncate`         | `in: [v], to_bits` | `out` | 取低 n bits |

---

## 6. 旗標更新 (Flag update) ⚠

直接寫入 CPSR 對應 bit；JIT 端可做 lazy flag 優化。

| op | 說明 | 觸發條件 |
|---|---|---|
| `update_n`       | N = result[31] | 所有更新 flag 的指令 |
| `update_z`       | Z = (result == 0) | 同上 |
| `update_nz`      | 同時更新 N 與 Z | 縮寫 |
| `update_c_add`   | C = (rn + op2) > 0xFFFFFFFF (unsigned overflow) | ADD/ADC/CMN |
| `update_c_sub`   | C = !(rn − op2) borrow (i.e. rn ≥ op2) | SUB/SBC/CMP/RSB |
| `update_c_shifter` | C = shifter_carry_out | logical/MOV with shift |
| `update_v_add`   | V = signed overflow of (rn + op2) | ADD/ADC/CMN |
| `update_v_sub`   | V = signed overflow of (rn − op2) | SUB/SBC/CMP/RSB |
| `update_q`       | Q = saturation occurred | DSP ext (ARMv5TE) |
| `set_flag`       | `flag, value` 直接指定 | edge cases |
| `clear_flag`     | `flag` | edge cases |

例：`{ "op": "update_c_add", "in": ["rn_val", "op2_value"] }`

---

## 7. 暫存器存取 (Register I/O) ⚠ on write

| op | inputs | out | 說明 |
|---|---|---|---|
| `read_reg`  | `index: <field-or-int>` | `out` | 依 mode 自動處理 banked register |
| `write_reg` | `index, value`           | (none) | 同上；若 index==15 等同寫 PC |
| `read_psr`  | `which: "CPSR" | "SPSR"` | `out` | 讀取（current mode 的 SPSR） |
| `write_psr` | `which, value, mask?`   | (none) | mask 指定哪些 bits 可寫 |
| `restore_cpsr_from_spsr` | (none) | (none) | mode return（如 ALU 寫 PC + S-bit） |
| `swap_registers_for_mode` | `mode_id` | (none) | 強制做 banked register swap（內部用） |

**特殊**：`write_reg` 若目標為 PC，emitter 自動：
1. 檢查 `value` 的低 bit (Thumb 切換規則，`bx` / `mov pc, ...`)
2. 對齊到 instruction set 寬度
3. 觸發 `block_terminate("indirect_branch")`

---

## 8. 立即值 / 運算元解析 (Operand Resolution)

通常透過 format 的 `operands` 段宣告而非直接呼叫；但低階寫法可用：

| op | inputs | out | 說明 |
|---|---|---|---|
| `imm_rotated` | `imm8, rotate4` | `value`, `shifter_carry_out` | ARM 8-bit imm + 2*rotate |
| `imm_sign_extend` | `value, from_bits` | `out` | 立即值符號擴展 |
| `pc_relative_address` | `offset` | `address` | PC（含 pipeline offset）+ offset |

---

## 9. 記憶體存取 (Memory) ⚠

| op | inputs | out | 說明 |
|---|---|---|---|
| `load`         | `addr, size: 8|16|32|64, signed: bool` | `value` | 由 memory bus 讀，自動套 alignment policy |
| `store`        | `addr, value, size`                    | (none)  | 寫 memory；觸發 SMC barrier 檢查 |
| `load_aligned` | `addr, size`                            | `value` | bypass alignment policy（已知對齊） |
| `swap_word`    | `addr, value`                           | `old`   | SWP 指令的原子交換 |
| `block_load`   | `base_addr, register_list, mode`       | (none)  | LDM/LDMIA/LDMFD 等族群 |
| `block_store`  | `base_addr, register_list, mode`       | (none)  | STM/STMIA/STMFD 等族群 |

`block_load`/`block_store` 的 `mode` 列舉：
- `IA` (Increment After) / `IB` (Increment Before)
- `DA` (Decrement After) / `DB` (Decrement Before)
- `FD`/`FA`/`ED`/`EA`（stack 變體別名）

---

## 10. 控制流 (Control flow) ⚠

| op | 必填欄位 | 說明 |
|---|---|---|
| `if`              | `cond`, `then`, `else?` | 條件分支（emit `br i1`） |
| `switch`          | `selector`, `cases`, `default?` | 多路分派（emit `switch i32`） |
| `branch`          | `target` | 立即無條件跳轉，會 `block_terminate` |
| `branch_link`     | `target` | BL：寫 LR = next_pc 後跳轉 |
| `branch_indirect` | `target_value` | BX 等；自動處理 T-bit 與對齊 |
| `block_terminate` | `reason`, `next_pc?` | 顯式結束 block，給 JIT 用 |
| `nop`             |  | 真正不做事（編譯時可消除） |

**`reason` 列舉（給 block compiler 解讀）**：
- `branch_taken` / `branch_link` / `branch_indirect`
- `mode_change` / `instruction_set_switch`
- `software_interrupt` / `undefined_instruction`
- `io_barrier` (寫入 IO 暫存器，需要 PPU/Timer 同步)
- `breakpoint` / `wait_for_interrupt`

---

## 11. 例外 / 系統 (Exception / System) ⚠

| op | 說明 |
|---|---|
| `raise_exception` | `vector: "SoftwareInterrupt" | "UndefinedInstruction" | ...` 完整流程：save CPSR→SPSR、save next-PC→banked LR、切 mode 與 disable bits、call host_swap_register_bank、PC ← vector address |
| `restore_cpsr_from_spsr` | 用 runtime CPSR.M 選擇 SPSR_<mode> 的 banked slot 載回 CPSR；附帶 host_swap_register_bank 通知 |
| `enter_mode`      | `mode_id`，附帶 banked register swap 與 SPSR 儲存（與 raise_exception 共用內部 helper） |
| `if_arm_cond`     | 用 per-instruction `cond_field`（4-bit）gate 包住 then-block；給 Thumb F16 conditional branch 用（Thumb 沒有 global cond gate） |
| `disable_interrupts` | `mask: ["I","F"]` |
| `enable_interrupts`  | `mask: ["I","F"]` |
| `wait_for_interrupt` | (ARMv6+) WFI 等 |
| `coprocessor_call`   | `cp_num, op1, crd, crn, crm, op2` (ARMv4 已有 MCR/MRC) |
| `breakpoint`         | BKPT (ARMv5+) |

### `raise_exception` 詳細欄位

```json
{ "op": "raise_exception", "vector": "SoftwareInterrupt" }
```

`vector` 必須對應 spec `exception_vectors[]` 的某個 `name`。emitter 會
從 vector 條目讀出 `enter_mode` 與 `disable`（要 set 的 CPSR bit 名稱
列表，如 `["I"]`、`["I","F"]`），不在 step 內重複宣告。

完整下放序列（見 `ArmEmitters.RaiseExceptionEmitter`）：
1. read CPSR → `old_cpsr`
2. 若 SPSR 是 banked 且該 mode 有 SPSR slot：store old_cpsr →
   `SPSR_<enter_mode>`
3. 計算 next-PC = R15 − (pc_offset_bytes − instruction_size_bytes)
4. store next-PC → 該 mode 的 banked R14 slot（若有）
5. 計算 new_cpsr = (old_cpsr & ~M_mask) | new_mode_enc | OR(disable bits...)
6. store new_cpsr → CPSR
7. call extern `host_swap_register_bank(state, old_mode, new_mode)`
8. store vector.address → R15

### `restore_cpsr_from_spsr` 詳細

無欄位。emit 為 runtime switch over CPSR.M：
- 對每個 spec 宣告為 `banked_per_mode` 的 mode（FIQ/IRQ/Supervisor/
  Abort/Undefined）emit 一個 case，載入對應 SPSR_<mode>
- default arm（User/System mode 等無 SPSR slot 的）保留 old CPSR
- 用 PHI 合併、寫回 CPSR
- 結尾 call `host_swap_register_bank(state, old_mode, new_mode)`

### `if_arm_cond` 詳細

```json
{
  "op": "if_arm_cond",
  "cond_field": "cond",
  "then": [ ...nested steps... ]
}
```

`cond_field` 指向 format 的某個 4-bit field，內容為 ARM cond code
（0-15）。emitter 用 `ConditionEvaluator.EmitCheckOnCondValue`（這個
helper 是從 global cond gate 邏輯抽出來的）建立 cond gate，包住
`then` 區塊。Thumb F16 conditional branch 用此 op，因為 Thumb 沒有
instruction-set 層級的 global condition。

---

## 12. 同步 / Barrier ⚠

| op | 說明 |
|---|---|
| `cycle_advance`    | `count`：手動推進 cycle counter（內部用） |
| `io_write_barrier` | 通知 host 寫入 IO 已完成，需同步外部硬體 |
| `dmb` / `dsb` / `isb` | (ARMv7+) memory barrier 系列；ARMv4T 視為 nop |

---

## 13. JIT / 偵錯輔助

| op | 說明 |
|---|---|
| `host_callback`     | `name, args`：直接呼叫 C# 註冊的 callback（給 HLE BIOS、debug log） |
| `assert`            | `cond`：dev mode 下若不成立則 trap |
| `trace_event`       | `event_name, args`：tracing |
| `mark_block_boundary` | （給 block compiler 的 hint，emit 時通常去除） |

---

## 14. Lazy flag 計算（將來最佳化）

長期會引入 *lazy flag*：不立即計算 N/Z/C/V，而是儲存「最後一次更新的
operands + 種類」，下次有指令真的讀 flag 時才計算。介面為：

| op | 說明 |
|---|---|
| `lazy_flags_set` | `kind, operands, result`：登記但不算 |
| `lazy_flags_resolve` | 真的算出 flag 並寫入 CPSR |

第一版直接 `update_*`；後續可換手不影響 spec 寫法（Parser 內部選擇實作）。

---

## 15. 自訂 micro-op（spec 內擴充）

宣告：

```json
"custom_micro_ops": [
  {
    "name": "bcd_add",
    "inputs":  [ {"name":"a","width":8}, {"name":"b","width":8}, {"name":"cin","width":1} ],
    "outputs": [ {"name":"sum","width":8}, {"name":"cout","width":1} ],
    "summary": "8-bit BCD add for 6502 D=1 mode"
  }
]
```

C# 端對應 `IMicroOpEmitter` 介面：

```csharp
public interface IMicroOpEmitter
{
    string OpName { get; }
    void Emit(IRBuilderContext ctx, MicroOpStep step);
    IReadOnlyList<string> InputNames  { get; }
    IReadOnlyList<string> OutputNames { get; }
}
```

註冊後即可在任何 spec 的 `steps` 中以 `{ "op": "bcd_add", ... }` 引用。

---

## 16. 完整列表（速查）

```
arithmetic   : add, adc, sub, sbc, rsb, rsc, mul, mul_hi, umul64, smul64,
               neg, abs, min, max
logical      : and, or, xor, not, bic
shift        : shl, lsr, asr, ror, rrx
compare      : cmp_eq, cmp_ne, cmp_ult, cmp_slt, cmp_ule, cmp_sle
bit          : bitfield_extract, bitfield_insert, clz, popcount,
               byte_swap, sign_extend, zero_extend, truncate
flag         : update_n, update_z, update_nz,
               update_c_add, update_c_sub, update_c_shifter,
               update_v_add, update_v_sub, update_q,
               set_flag, clear_flag
register     : read_reg, write_reg, read_psr, write_psr,
               restore_cpsr_from_spsr, swap_registers_for_mode
operand      : imm_rotated, imm_sign_extend, pc_relative_address
memory       : load, store, load_aligned, swap_word,
               block_load, block_store
control      : if, if_arm_cond, switch, branch, branch_link,
               branch_indirect, block_terminate, nop
exception    : raise_exception, restore_cpsr_from_spsr, enter_mode,
               disable_interrupts, enable_interrupts,
               wait_for_interrupt, coprocessor_call, breakpoint
barrier      : cycle_advance, io_write_barrier, dmb, dsb, isb
jit_aux      : host_callback, assert, trace_event, mark_block_boundary
lazy_flag    : lazy_flags_set, lazy_flags_resolve
```

共 **~75 個 base micro-op**，足以覆蓋 ARMv4T 全部指令以及大多數 8/16/32-bit
經典 RISC/CISC 架構（6502、Z80、MIPS、SH-2 等）。

---

## 17. 設計決策說明

### 17.1 為何把 flag 計算切細到 `update_c_add` / `update_c_sub`？

ARM 的 carry 對 add 與 sub 定義不同（sub 的 C 是 NOT borrow）。把這個區別
編入 op 名稱，讓 spec 不需要解釋細節，emitter 直接知道該 emit 哪段 IR。

### 17.2 為何 PC 寫入由 `write_reg` 自動處理而非獨立 op？

因為 ARM 的「寫入 R15」可發生在很多指令（MOV、ADD、LDR 等）。把 PC 邏輯
封裝到 `write_reg` 內，spec 寫起來簡潔；JIT 端在 emit `write_reg(15, ...)`
時自動插入 `block_terminate`。

### 17.3 為何 condition execution 不是 micro-op 而是格式層級？

ARM 的 cond 是「整條指令」的 prefix gate。若每條指令前面都加 `if cond`，
spec 會非常冗長。改在 instruction-set 層級宣告 `global_condition`，
Parser 在 emit 時自動把整個 step 區塊包進 `if`。

### 17.4 為何 `update_*` 不直接吃 result？

部份 flag（V、C）需要 operands 而非結果（signed/unsigned overflow 判定）。
規定統一傳 operands，emitter 內部決定如何計算。
