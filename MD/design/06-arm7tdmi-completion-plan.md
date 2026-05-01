# Phase 2.5：ARM7TDMI 完整規格 + Parser 完整化

## 為什麼插在 Phase 2 與 Phase 3 之間

Phase 2 把 spec → IR 的端到端管線跑通，但 spec 內容只是示範子集
（ARM 4 條 Data Processing + B/BL/BX，Thumb F1/F3/F18），parser 也只
覆蓋會用到的 micro-op。

如果直接進 Phase 3（直譯器跑測試 ROM），我們會反覆遇到「碰到什麼指令、
就回頭補一點 spec、補一個 emitter」的零散增量，每次都得改三個地方且
缺乏整體視角。

**先把 ARM7TDMI 全部 ISA 在 spec 裡寫齊，parser/emitter 同步補完，
再進 Phase 3 跑驗證 ROM**，順序更乾淨：

- Phase 2.5 完工後，spec ＝ ARM7TDMI 完整規格（unit 是 spec 一致性）
- Phase 3 進場時，所有指令都能被 decoder/emitter 處理（unit 是「跑對」）

---

## 範圍

### 涵蓋（in scope）

**ARM 模式（ARMv4T 32-bit）— 13 種編碼空間：**

| 順位 | 編碼空間 | 主要指令 | 目前狀態 |
|---|---|---|---|
| 1  | Branch and Exchange      | BX                                              | ✅ Phase 2 |
| 2  | Multiply                 | MUL, MLA                                        | 待補 |
| 3  | Multiply Long            | UMULL, UMLAL, SMULL, SMLAL                      | 待補 |
| 4  | Single Data Swap         | SWP, SWPB                                       | 待補 |
| 5  | Halfword/Signed DT       | LDRH, STRH, LDRSB, LDRSH                        | 待補 |
| 6  | PSR Transfer             | MRS, MSR (imm/reg)                              | 待補 |
| 7  | Data Processing          | AND/EOR/SUB/RSB/ADD/ADC/SBC/RSC/TST/TEQ/CMP/CMN/ORR/MOV/BIC/MVN（×3 編碼變體：Imm、RegImmShift、RegRegShift） | 部分 ✅（Imm 4 條） |
| 8  | Single Data Transfer     | LDR, STR (含 byte/word/imm/reg/pre/post/wb)     | 待補 |
| 9  | Undefined                | (永遠觸發 UND 例外的 reserved 編碼)             | 待補 |
| 10 | Block Data Transfer      | LDM, STM (×4 modes ×S-bit ×writeback)           | 待補 |
| 11 | Branch / Branch with Link| B, BL                                            | ✅ Phase 2 |
| 12 | Coprocessor (3 種)        | CDP, MRC/MCR, LDC/STC                            | 待補（GBA 無 CP，stub 為 UND） |
| 13 | Software Interrupt       | SWI                                              | 待補 |

> 順位即 mask/match priority — 高位優先（PSR Transfer 必須在
> Data Processing 之前比對；Multiply 必須在 Data Processing 之前；
> Halfword DT 必須在 Data Processing 之前）。

**Thumb 模式（Thumb-1 16-bit）— 19 種官方格式：**

| 編號 | 名稱 | 目前狀態 |
|---|---|---|
| F1  | Move Shifted Register (LSL/LSR/ASR imm)     | ✅ Phase 2 |
| F2  | Add/Sub                                       | 待補 |
| F3  | Move/Compare/Add/Sub Immediate (8-bit)        | ✅ Phase 2 |
| F4  | ALU Operations (16 ops)                       | 待補 |
| F5  | Hi Register Operations + BX                   | 待補 |
| F6  | PC-Relative Load                              | 待補 |
| F7  | Load/Store with Register Offset               | 待補 |
| F8  | Load/Store Sign-Extended Byte/Halfword        | 待補 |
| F9  | Load/Store with Immediate Offset              | 待補 |
| F10 | Load/Store Halfword                           | 待補 |
| F11 | SP-Relative Load/Store                        | 待補 |
| F12 | Load Address                                   | 待補 |
| F13 | Add Offset to SP                              | 待補 |
| F14 | Push/Pop Registers                            | 待補 |
| F15 | Multiple Load/Store                           | 待補 |
| F16 | Conditional Branch                            | 待補 |
| F17 | Software Interrupt                            | 待補 |
| F18 | Unconditional Branch                          | ✅ Phase 2 |
| F19 | Long Branch with Link (兩半 BL)               | 待補 |

**Parser/Emitter 同步補完：**

- 所有上述格式所需 micro-op
- 完整 ARM cond table（補 CS/CC/MI/PL/VS/VC/HI/LS/GE/LT/GT/LE）
- 完整 banked register swap
- 真正的 `restore_cpsr_from_spsr`、`enter_mode`、`raise_exception`
- shifted_register_by_immediate / shifted_register_by_register operand
  resolver
- register_list operand resolver

### 不涵蓋（仍延後到 Phase 3+）

- 跑實際 GBA ROM
- LLVM JIT 執行（直譯模式才會需要 marshal CpuState）
- BIOS HLE
- 中斷實際處理流程
- DMA / Timer / PPU / APU
- ARMv5+ 的擴充（CLZ、Q-flag DSP 指令）

---

## 子階段排程

每個子階段 = 一塊指令格式 + 對應 emitter + 測試。每完成一個子階段就
commit + push，可獨立驗收。

| Sub-phase | 內容 | 預估 commits | 估時 |
|---|---|---|---|
| **2.5.1** | Spec authoring 規範文件 + lint 強化 | 1 | 0.5 天 |
| **2.5.2** | ARM Data Processing 全集（×3 編碼） + PSR Transfer | 1–2 | 1.5 天 |
| **2.5.3** | ARM Memory（SDT、Halfword/Signed、SWP）         | 1–2 | 1.5 天 |
| **2.5.4** | ARM Multiply（MUL/MLA + Long）                  | 1   | 0.5 天 |
| **2.5.5** | ARM Block Transfer + SWI + Coprocessor + UND    | 1–2 | 1 天 |
| **2.5.6** | Thumb 剩餘 16 種格式（F2、F4–F17、F19）          | 3–4 | 2 天 |
| **2.5.7** | 完整 cond table + banked swap + restore_cpsr     | 1   | 1 天 |
| **2.5.8** | Coverage 驗證 + 收尾 commit                       | 1   | 0.5 天 |

**估時合計：~8 天集中工時**（業餘換算 3–4 週）

---

## 子階段 detail

### 2.5.1 Spec authoring conventions + lint

**目標**：在補大量 spec 之前，把寫法規範化，避免後面到處不一致。

產出：
- `MD/design/07-spec-authoring-conventions.md`：bit-pattern 字母慣例
  （`c` 統一給 cond、`o` 給 opcode、`d` 給 Rd、`n` 給 Rn、`m` 給 Rm…）、
  micro-op 命名習慣、cycle 表達式格式。
- BitPatternCompiler / SpecLoader 強化 lint：
  - 警告：未宣告的 field 名（pattern 出現但 fields[] 沒列）
  - 警告：format 內有 instructions 但沒 selector（非預期）
  - 強制：mask/match 必須與 pattern 一致（已實作）
  - 強制：sub-opcode value 寬度必須符合 selector field 寬度

### 2.5.2 ARM Data Processing 全集

**新增到 arm.json**：

#### Data Processing — 完整 16 個 ALU op

opcode 表（4 bits）：
```
0000 AND  0001 EOR  0010 SUB  0011 RSB
0100 ADD  0101 ADC  0110 SBC  0111 RSC
1000 TST  1001 TEQ  1010 CMP  1011 CMN
1100 ORR  1101 MOV  1110 BIC  1111 MVN
```

TST/TEQ/CMP/CMN 不寫回 Rd（標 `writes_pc: never`、不要 store_reg），
強制 S=1（否則就是 PSR Transfer 編碼）。

#### 三種運算元編碼形式（全部在同一 opcode 表下）

| Format 名稱 | 編碼條件 | Pattern |
|---|---|---|
| `DataProcessing_Immediate`     | bit 25 = 1                 | `cccc 001 oooo s nnnn dddd rrrr iiiiiiii` ✅ 已有 |
| `DataProcessing_RegImmShift`   | bit 25 = 0, bit 4 = 0      | `cccc 000 oooo s nnnn dddd ssssstt 0 mmmm` |
| `DataProcessing_RegRegShift`   | bit 25 = 0, bit 7 = 0, bit 4 = 1 | `cccc 000 oooo s nnnn dddd ssss 0 tt 1 mmmm` |

#### PSR Transfer (overlap 在 Data Processing 編碼空間)

當 opcode = `10xx` 且 S = 0 時，這個編碼槽改用作 PSR Transfer。
priority 上 PSR Transfer 必須在 Data Processing 之前匹配。

| Mnemonic | Pattern |
|---|---|
| MRS | `cccc 00010 P 001111 dddd 000000000000` |
| MSR (reg) | `cccc 00010 P 1010001111 00000000 mmmm` |
| MSR (imm) | `cccc 00110 P 1010001111 rrrr iiiiiiii` |

`P` bit = SPSR 選擇（0 = CPSR、1 = SPSR_<current_mode>）。

#### 新增 micro-ops

- `adc`、`sbc`、`rsb`、`rsc`（運算）
- `mvn`（NOT）— 已用 `not` 實作
- `update_c_sub_with_carry`、`update_c_add_with_carry`（ADC/SBC）
- `read_psr` / `write_psr`（給 MRS/MSR；後者支援 field mask）
- `cmn` 邏輯 = sub flags 用 add flags（直接以 add+update_*_add 表達）
- `tst` / `teq` = and/xor + update_nz +（依 S-bit）update_c_shifter

#### 新增 operand resolvers

- `shifted_register_by_immediate`
- `shifted_register_by_register`

兩者都需要回傳 `{value, shifter_carry_out}`，且必須處理 ARM 的特例：
LSL #0 → 不位移、carry 來自 CPSR.C；LSR #0 → 視為 LSR #32；
ASR #0 → 視為 ASR #32；ROR #0 → 視為 RRX。

### 2.5.3 ARM 記憶體傳輸

- **SingleDataTransfer (SDT)**：LDR / STR + B-bit (byte/word) + I-bit
  (immediate vs reg-shift offset) + P-bit (pre/post indexing) + U-bit
  (add/sub offset) + W-bit (writeback)。每條指令的「形狀」由這些 bit
  決定，spec 寫起來會很多 sub-format 或一個格式內走 step 流程。
- **Halfword/Signed DT**：LDRH、STRH、LDRSB、LDRSH。三種 imm/reg offset
  變體。
- **SingleDataSwap**：SWP、SWPB（atomic load+store）。

新 micro-ops：
- `load`：`{addr, size, signed, rotate_unaligned}` → value
- `store`：`{addr, value, size}`
- `swap_word`：atomic exchange
- `add_with_writeback`：給 SDT writeback 路徑用

### 2.5.4 ARM Multiply

- **Multiply / Multiply-Accumulate**：MUL Rd, Rm, Rs；MLA Rd, Rm, Rs, Rn。
  Pattern：`cccc 000000 A S dddd nnnn ssss 1001 mmmm`。
- **Multiply Long**：UMULL/UMLAL/SMULL/SMLAL。寫回 RdHi:RdLo。
  Pattern：`cccc 00001 U A S hhhh llll ssss 1001 mmmm`。

新 micro-ops：
- `mul`（已有 i32 mul）
- `umul64`、`smul64`
- `truncate_lo`、`extract_hi`（取 i64 上下半）
- `write_reg_pair`

### 2.5.5 ARM Block Transfer + 其他

- **Block Data Transfer (LDM/STM)**：
  - Pattern: `cccc 100 P U S W L nnnn rrrrrrrrrrrrrrrr`
  - L=load/store、PU 組合決定 IA/IB/DA/DB、S=user-mode regs、W=writeback。
- **SWI**：`cccc 1111 oooooooooooooooooooooooo`（24-bit 立即值，HLE BIOS 用）
- **Coprocessor**：CDP/MRC/MCR/LDC/STC — GBA 無 coprocessor，emit
  `raise_exception(UndefinedInstruction)` 即可
- **Undefined**：`cccc 011x xxxxxxxxxxxxxxxxxxxx xxx 1 xxxx`（特定保留編碼）

新 micro-ops：
- `block_load` / `block_store`（吃 register_list、base_addr、mode）
- `raise_exception`（祭起例外向量、切 mode、寫 SPSR、設 PC）

新 operand resolvers：
- `register_list`（16-bit bitmap）

### 2.5.6 Thumb 完整 16 個格式

依官方手冊 17 條（剩 F2、F4–F17、F19）。Thumb 大多數語義可直接 reuse
ARM 的 micro-op；新增的不多：

- F14 PUSH/POP 用 `block_load`/`block_store`，但 register list 編碼不同
- F19 BL（long branch with link）兩半合成一個 32-bit 偏移；正確語義是
  「上半把 hi 11 bits 算進 LR，下半把 lo 11 bits OR 進去再交換 LR/PC」。
  這要產生兩個獨立指令的 emitter，但概念上是耦合的；用 quirk
  `bl_hi_half` / `bl_lo_half` 標註。

### 2.5.7 完整 cond table + banked swap

- InstructionFunctionBuilder 補完 14 個 cond 條件（剩 12 個）：
  - CS = C set；CC = C clear
  - MI = N set；PL = N clear
  - VS = V set；VC = V clear
  - HI = C set AND Z clear
  - LS = C clear OR Z set
  - GE = N == V
  - LT = N != V
  - GT = Z clear AND N == V
  - LE = Z set OR N != V
- Banked register swap：當 mode 變化（例如 IRQ 進入），把當前 R8–R14
  存到舊 mode 的 banked slot、把新 mode 的 banked slot 載入到 R8–R14
- `restore_cpsr_from_spsr`：當 ALU 寫 PC 且 S=1，從 SPSR 載回 CPSR
- `enter_mode`：mode 變化的整套流程封裝（給 SWI / IRQ / 例外用）

### 2.5.8 Coverage 驗證 + 收尾

- 編譯腳本：對 arm.json/thumb.json 中**所有** micro-op 名稱、所有
  operand_resolver kind 做 vocabulary check（缺 emitter 即報錯）
- 解碼覆蓋測試：50+ 條 known opcode 解出正確 mnemonic
- IR 驗證：完整 module 通過 LLVM `Verify`（Action = Print）
- 統計：spec 行數、emitter 數、function 數
- 更新 03-roadmap.md，標記 Phase 2.5 完成
- Push final commit

---

## Done criteria（全 Phase 2.5 結案標準）

1. ✅ `aprcpu --spec spec/arm7tdmi/cpu.json --output temp/arm7tdmi.ll`
   產出 ARMv4T 全部指令的 LLVM 函式（預估 80+ functions）
2. ✅ Module 通過 LLVM `Verify`，0 diagnostics
3. ✅ 所有 spec 中的 micro-op 都有對應 emitter
4. ✅ 所有 spec 中的 operand_resolver kind 都被處理
5. ✅ 14 個 ARM cond code 全部正確
6. ✅ 至少 50 條 known opcode 通過 decoder coverage test
7. ✅ xUnit 測試 100/100 全綠（粗估數量）
8. ✅ Roadmap 文件更新

---

## 與後續 Phase 的銜接

完工後，Phase 3 進場時：

- Decoder 已能對任意 GBA ROM bytes 解出指令身分
- IR emitter 能把任意指令轉成可 JIT 的函式
- Phase 3 重點轉到「讓這些函式真的執行起來」：
  - C# 端 CpuState struct 與 LLVM 的 layout match
  - JIT execution engine + 主迴圈 fetch-decode-execute
  - armwrestler 測試 ROM 跑通
