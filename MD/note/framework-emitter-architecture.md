# AprCpu Framework — 設計概念與 Emitter 架構

> 寫於 Phase 4.5 全部完工後（ARM7TDMI + LR35902 兩顆 CPU 都跑通的時點）。
> 紀錄整套框架是怎麼把「JSON CPU 描述」變成「跑得起 ROM 的 JIT」，
> 以及兩顆 CPU 各自的 emitter 真正在做什麼事。

---

## 1. 框架的中心論點

> **「換 CPU 等於換 JSON。」**

這是 AprCpu 想證明的事。傳統 emulator 是手刻指令直譯器（一個大 switch-case
撐起所有 opcode），每顆 CPU 都重寫一遍。AprCpu 把 CPU 拆成兩部分：

- **資料**：JSON spec — register file、encoding、step list
- **動詞**：emitter — 每個 step 怎麼變成 LLVM IR

對應檔案：

```
spec/<arch>/cpu.json + groups/*.json     ← 資料（每顆 CPU 自己的）
src/AprCpu.Core/IR/<Arch>Emitters.cs     ← 動詞（每顆 CPU 自己的）
src/AprCpu.Core/Compilation/SpecCompiler.cs  ← 通用編譯流程
src/AprCpu.Core/IR/Emitters.cs               ← 通用 micro-op
src/AprCpu.Core/IR/MemoryEmitters.cs         ← 通用 memory bus 接點
src/AprCpu.Core/Runtime/HostRuntime.cs       ← MCJIT + extern binding
src/AprCpu.Core/Decoder/DecoderTable.cs      ← bit-pattern dispatch
```

**「換 CPU 不需要碰框架程式碼」這一條已經被驗證**：ARM7TDMI 跟 LR35902
的 emitter 完全互不引用，新增一顆不會破壞另一顆。

---

## 2. 三層架構

```
┌────────────────────────────────────────────────────────┐
│ Layer 1 — JSON spec                                    │
│ 純資料，零 C# 碼。SpecLoader 把它讀成 POCO model。     │
│                                                         │
│ • register_file: count / width / names / pairs         │
│ • status registers: width + flag bit positions          │
│ • exception_vectors                                     │
│ • instruction_set_dispatch (例：CPSR.T 切 ARM/Thumb)    │
│ • encoding_groups → formats → instructions              │
│   每個 instruction 帶 mnemonic + cycles + steps[]       │
│ • step 是一個物件 { "op": "...", ...其他欄位 }          │
└────────────────────────────────────────────────────────┘
                  ↓ SpecCompiler 走 step 找 emitter
┌────────────────────────────────────────────────────────┐
│ Layer 2 — StandardEmitters（通用 micro-op）             │
│ 不關心是哪顆 CPU，只關心輸入輸出形狀。                  │
│                                                         │
│ register I/O: read_reg / write_reg / read_reg_pair_u64 │
│              / write_reg_pair                            │
│ 算術 / 邏輯: add / sub / and / or / xor / shl / lshr   │
│              / ashr / rsb / mul / mvn / bic / ror      │
│ 64-bit:      umul64 / smul64 / add_i64                 │
│ 控制流:      if / select / branch / branch_link         │
│ 記憶體:      load / store (走 memory_read_8/16/32 extern) │
│ 區塊傳輸:    block_load / block_store (LDM/STM 用)      │
└────────────────────────────────────────────────────────┘
                  ↓ 看到不認識的 op
┌────────────────────────────────────────────────────────┐
│ Layer 3 — Architecture-specific Emitters                │
│ 每顆 CPU 一個 .cs 檔，靠 SpecCompiler 看                │
│ architecture.family 動態註冊。                           │
│                                                         │
│ ArmEmitters.cs       (ARM)                              │
│ Lr35902Emitters.cs   (Sharp SM83 / GB)                  │
│ ──────────────────────────────────────────────          │
│ 處理那顆 CPU 獨有的：                                   │
│ • Flag layout（位置在哪、什麼時候要更新）                │
│ • 特殊算術（half-carry、ARM 的 V flag）                  │
│ • 特殊定址（barrel shifter / (HL) memory）              │
│ • Mode 切換 / banked register                           │
│ • Host-side state 通知（HALT、IME、bank swap）          │
└────────────────────────────────────────────────────────┘
```

`SpecCompiler.Compile` 用 `architecture.family` 字串挑：

```csharp
if (family == "ARM")           ArmEmitters.RegisterAll(registry);
else if (family == "Sharp-SM83") Lr35902Emitters.RegisterAll(registry);
// 加第三顆只要再加一個 if ... 然後寫 spec/<新 CPU>/ 跟 <NewCpu>Emitters.cs
```

---

## 3. JSON 端能描述到什麼程度

完全不需要寫 C# 就能搞定的事：

| 屬性 | 在 spec 哪 |
|---|---|
| 暫存器個數、寬度、命名 | `register_file.general_purpose` |
| 16-bit pair 怎麼從兩個 8-bit 拼出來 | `register_file.register_pairs` |
| Status reg 跟它的 flag bit 位置 | `register_file.status[].fields` |
| Banked register / processor mode | `processor_modes` |
| Exception 向量地址 | `exception_vectors` |
| 多 instruction set + 切換條件 | `instruction_set_dispatch` |
| Opcode 怎麼解碼（mask/match/fields） | `encoding_groups[].formats[].pattern` |
| 同 format 內多條指令選擇 | `instructions[].selector` |
| 每條指令 cycles | `instructions[].cycles.form` |
| 指令的執行步驟 | `instructions[].steps[]` |

不能只用 spec 表達、必須寫 emitter 程式碼的事：

| 行為 | 為什麼 JSON 表達不了 |
|---|---|
| Half-carry 計算公式 | 是個小算法（i32 widening + 比較），寫進 JSON 等於把 IR builder 塞進 spec |
| BCD 調整邏輯（DAA） | 一坨條件分支 |
| 「3-bit field 中某個值代表 memory access 不是 register」 | 需要 select chain + 條件 store + bus extern call |
| F register 寫入時要按位置 shift+or 拼起來 | 程式邏輯，不是宣告式資料 |
| 通知 host runtime 「我 HALT 了」 | C# / IR 邊界 wiring |
| 條件分支真正的 basic-block 控制流（例 CALL cc 的條件 push） | LLVM IR 控制流 builder API |

---

## 4. ArmEmitters 在做什麼

對應檔案：`src/AprCpu.Core/IR/ArmEmitters.cs`（~800 行）。
ARM7TDMI 觸發的 micro-op 一共約 **15 個**：

### 4.1 CPSR / SPSR flag 更新

ARM 的 CPSR 把 N/Z/C/V 放在 bit 31..28。每條 ALU 指令（如果有 S 後綴）
要算這四個 flag。

| op | 功能 |
|---|---|
| `update_nz` | N = 結果最高 bit；Z = (結果 == 0) |
| `update_c_add` | C = 32-bit 加法的進位（i64 widening） |
| `update_c_sub` | C = NOT borrow |
| `update_v_add` | V = signed overflow on add |
| `update_v_sub` | V = signed overflow on sub |
| `update_c_shifter` | C ← shifter 算出來的 carry-out（barrel shifter 用） |
| `update_c_add_carry` | ADC 用：C = (a + b + Cin) > 0xFFFFFFFF |
| `update_c_sub_carry` | SBC/RSC 用 |

### 4.2 帶進位的 ALU

| op | 功能 |
|---|---|
| `adc` | A + B + Cin |
| `sbc` | A - B - !Cin |
| `rsc` | B - A - !Cin（RSB 帶 carry） |

通用的 `add` / `sub`（在 StandardEmitters）不夠用，因為要讀 CPSR.C。

### 4.3 PSR 存取與 mode 切換

| op | 功能 |
|---|---|
| `read_psr` | 讀 CPSR 或 SPSR_<mode>（依當前 mode 選 banked slot） |
| `write_psr` | 寫 CPSR/SPSR；如果改 CPSR.M（mode bits）會 call extern `host_swap_register_bank` 做 banked register 重組 |
| `restore_cpsr_from_spsr` | LDM ^ / SUBS PC,LR 那種「I-bit + mode 一起恢復」的特殊路徑；emit 一個 switch over CPSR.M[4:0]，每個 banked-SPSR mode 一個 case，PHI 合併，再 call swap_bank |

### 4.4 控制流 / 條件 / 例外

| op | 功能 |
|---|---|
| `branch_indirect` | BX：寫 R15，根據 target bit[0] 設 CPSR.T 切 ARM/Thumb |
| `if_arm_cond` | ARM 14 個 cond code 求值（EQ/NE/CS/CC/MI/PL/...） |
| `raise_exception` | SWI/UND 用：save CPSR→SPSR_<entry_mode>、save next-PC→banked R14、改 CPSR.M、設 I bit、call swap_bank、PC ← vector |

### 4.5 ARM 端 emitter 的特性

- **GPR 一律 i32**（16 個 R0~R15 都是 32-bit），所以 `read_reg`
  / `write_reg` 通用版直接能用
- **R15 = PC**：這是個 GPR slot，不需要特別處理，但 read 出來自帶 pipeline
  offset（host runtime 在 step 開始前 pre-set 好）
- **Banked register**：CpuStateLayout 在 struct 裡多塞 banked slot；
  `write_psr` / `restore_cpsr_from_spsr` 觸發時 call host extern 重排
- **Memory access**：直接用 StandardEmitters 的 `load` / `store`，搭配
  ARM-specific 的 `block_load` / `block_store`（LDM/STM）

ARM 跟 LR35902 共用的 emitter（沒寫在 ArmEmitters 裡）：
`read_reg`、`write_reg`、`add`、`sub`、`mul`、`if`、`branch` 等。所以
ArmEmitters.cs 這 800 行純粹處理 ARM 不能共用的部分。

---

## 5. Lr35902Emitters 在做什麼

對應檔案：`src/AprCpu.Core/IR/Lr35902Emitters.cs`（~2500 行）。
LR35902 觸發的 micro-op 大概 **40 個**，比 ARM 多很多，原因是 8-bit
CPU 的「形狀」跟通用 micro-op 假設的 32-bit 模型差更遠。

### 5.1 跨架構 helper（住在 LR35902 檔但其實是 8-bit CPU 通用）

| op | 功能 |
|---|---|
| `read_reg_named` | 用名字（"A"/"B"/.../"SP"/"PC"）找暫存器，自動識別是 GPR 還是 status reg、自動配對應 LLVM 寬度 |
| `write_reg_named` | 同上，寫入時自動 trunc/zext 對齊寬度 |
| `read_reg_pair_named` | "BC"→組合 B/C 成 i16；"SP"→直接讀 i16 status |
| `write_reg_pair_named` | 反向；AF 特例強制 mask F 低 4 bit = 0 |

> 這四個之後若有第二顆 8-bit CPU 進來，可以 promote 到 StandardEmitters。

### 5.2 LR35902 ALU + flag

LR35902 把 Z/N/H/C 放在 F register 的 bit 7..6..5..4。每條 ALU 都要更新。
Half-carry 是 LR35902 獨有（ARM 沒有），定義是「bit 3→4 的進位」。

| op | 功能 |
|---|---|
| `lr35902_alu_a_r8` | ADD/ADC/SUB/SBC/AND/XOR/OR/CP，source 是 3-bit `sss` field 選的 reg |
| `lr35902_alu_a_hl` | 同 8 個 op，source 是 memory[HL] |
| `lr35902_alu_a_imm8` | 同 8 個 op，source 是 imm8（emitter 自己 fetch + 進 PC） |
| `lr35902_inc_r8` / `lr35902_dec_r8` | 8-bit field-driven INC/DEC（C 保留） |
| `lr35902_inc_hl_mem` / `lr35902_dec_hl_mem` | (HL) 變體：memory R-M-W |
| `lr35902_add_hl_rr` | 16-bit ADD HL,rr，H 從 bit 11→12 進位 |
| `lr35902_inc_rr_dd` / `lr35902_dec_rr_dd` | 16-bit ±1，不影響 flag |
| `lr35902_write_rr_dd` | LD rr,nn dispatch by 2-bit dd |

flag-only ops（純粹改 F）：

| op | 功能 |
|---|---|
| `lr35902_scf` | C=1, N=H=0, Z 保留 |
| `lr35902_ccf` | C 翻轉 |
| `lr35902_cpl` | A = ~A, N=H=1 |
| `lr35902_daa` | BCD 調整：根據 N/H/C 跟 A 的當前值挑 +0x60 / +0x06 / -0x60 / -0x06 |

A-rotate（block 0 的 RLCA/RRCA/RLA/RRA，跟 CB 版不同的是 Z 永遠清零）：

| op | 功能 |
|---|---|
| `lr35902_rlca` / `rrca` / `rla` / `rra` | 1-bit 旋轉，C 從滾出去的 bit 來 |

### 5.3 3-bit field with (HL) special case

LR35902 的 `sss` / `ddd` 3-bit 欄位：000=B, 001=C, 010=D, 011=E,
100=H, 101=L, **110=memory[HL]**, 111=A。**110 不是某個 register**，
而是「去讀 / 寫 memory[HL]」。這個古怪規則在通用 read_reg 模型裡不存在。

| op | 功能 |
|---|---|
| `lr35902_read_r8` | 8 個 case 的 select chain，sss=110 走 placeholder（spec 把 (HL) 路徑分到別的 format） |
| `lr35902_write_r8` | 同上的鏡像 |

實作策略：spec 把 (HL) 變體都 split 出來成獨立 format（用 priority
shadow），所以 read_r8 / write_r8 跑的時候 sss/ddd 一定不是 6。

### 5.4 16-bit register pair 操作

| op | 功能 |
|---|---|
| `lr35902_inc_pair` / `lr35902_dec_pair` | LD (HL+),A / (HL-),A 用的 ±1 |

### 5.5 控制流（CALL / RET / JR / JP / RST / PUSH / POP）

LR35902 全部用 i16 PC（PC 在 status reg），不是 GPR slot；所以 ARM 的
`branch_indirect` / `branch` 不能直接套。

| op | 功能 |
|---|---|
| `lr35902_jp` | PC ← address |
| `lr35902_jp_cc` | 條件 JP，用 select |
| `lr35902_jr` | PC ← PC + sign_ext(e8) |
| `lr35902_jr_cc` | 條件 JR |
| `lr35902_call` | 把當前 PC 推 stack（兩 byte），PC ← address |
| `lr35902_call_cc` | **必須用 basic-block 條件控制流**（不能 select）— 因為 push 寫 memory 是 side effect，不能無條件 emit |
| `lr35902_ret` | PC ← pop |
| `lr35902_ret_cc` | 條件 ret |
| `lr35902_rst` | push PC, PC ← (ttt × 8) |
| `lr35902_push_qq` | dispatch by 2-bit qq (BC/DE/HL/AF)，寫兩 byte |
| `lr35902_pop_qq` | 反向，POP AF 走 DecomposePairValue 自動 mask F 低 nibble |

### 5.6 CB-prefix shift / BIT / RES / SET

| op | 功能 |
|---|---|
| `lr35902_cb_shift` | RLC/RRC/RL/RR/SLA/SRA/SWAP/SRL，dispatch by `shift_op` 字串 |
| `lr35902_cb_bit` | Z = !(r >> b & 1), N=0, H=1, C 保留 |
| `lr35902_cb_res` / `lr35902_cb_set` | r &= ~(1<<b) / r |= (1<<b) |
| `lr35902_cb_shift_hl_mem` | 同 cb_shift 但 source/dest 是 memory[HL] |
| `lr35902_cb_bit_hl_mem` | BIT b, (HL) |
| `lr35902_cb_resset_hl_mem` | RES/SET b, (HL)（用 is_set bool 區分） |

### 5.7 Host-side state（不在 LLVM struct 裡）

HALT / IME / EI-delay 不適合放 register file（它們不是 register），
所以放 JsonCpu 的 C# 靜態欄位，emitter 透過 extern call 通知 host：

| op | 功能 |
|---|---|
| `halt` / `stop` | call extern `host_lr35902_halt` 設 _haltSignal |
| `lr35902_ime` | call extern `host_lr35902_set_ime(0|1)` |
| `lr35902_ime_delayed` | call extern `host_lr35902_arm_ime_delayed`（EI 的延遲一指令語意） |
| `lr35902_cb_dispatch` | 0xCB prefix 的 stub；host runtime 會攔截切 CB 集合 |

### 5.8 Memory bus extern wiring

ARM 跟 LR35902 共用 `memory_read_8` / `memory_write_8` /
`memory_write_16` extern。LR35902 自己有幾個 spec-層的 memory op，
都 lower 到這些 extern：

| op | lower 到 |
|---|---|
| `load_byte` | `memory_read_8(i32 addr)` → i8 |
| `store_byte` | `memory_write_8(i32 addr, i8 v)` |
| `store_word` | `memory_write_16(i32 addr, i16 v)`（host shim 拆兩 byte） |
| `read_imm8` | 從 PC 讀 1 byte 並 PC++ |
| `read_imm16` | 從 PC 讀 2 byte 並 PC += 2 |
| `lr35902_ldh_io_load` | 0xFF00 + offset 那條，組地址 + read_8 |
| `lr35902_ldh_io_store` | 同上的寫入版 |

### 5.9 Lr35902-specific 的特殊算術

| op | 功能 |
|---|---|
| `lr35902_add_sp_e8` | SP ← SP + sign_ext(e8)；Z=N=0；H/C 從 SP 跟 e8 的低 byte unsigned 加法算（LR35902 怪規則） |
| `lr35902_ld_hl_sp_e8` | HL ← SP + sign_ext(e8)，flag 規則同上 |

---

## 6. 兩邊 emitter 的對照表

| 面向 | ArmEmitters | Lr35902Emitters |
|---|---|---|
| 行數（大概） | ~800 | ~2500 |
| Op 數 | ~15 | ~40 |
| 主要 GPR 寬度 | 32-bit 一致 | 8-bit + 16-bit (SP/PC/pair) 混合 |
| Flag layout | CPSR bit 31..28 (NZCV) + 7 (I) + 5 (T) | F bit 7..4 (ZNHC) |
| 特殊 flag 算法 | C, V (signed overflow) | H (half-carry, bit 3→4) |
| Mode / banking | 7 modes, banked R8-R14 | 無 |
| Pipeline offset | 有（PC = current+8 ARM、+4 Thumb） | 無 |
| 多指令集切換 | CPSR.T 持久 state | 0xCB prefix 瞬時切 |
| Host extern | swap_register_bank（mode 切換時） | halt + set_ime + arm_ime_delayed（CPU 狀態通知） |
| Memory access | 通用 load/store + block_load/store | load_byte/store_byte/store_word + read_imm + ldh_io |
| 條件控制流 | `if_arm_cond`（每條指令前 prefix 14 種 cond） | `_cc` 指令各自處理（select 或 basic block） |
| Field-driven r 選擇 | 直接 `read_reg index="rn"` | 自寫 select chain，因為 sss=6 是 memory |
| BCD | 無 | DAA |

可以看出 **ARM 跟 LR35902 在「形狀」上的差異就決定了 emitter 量級**。
LR35902 的 8-bit + 古怪 (HL) field + half-carry 撐起了大部分多出來的
emitter 數量。

---

## 7. Emitter 跟其他層的合作

一個典型的「LD A, B」(opcode 0x78) 從 spec 到 native code 經過：

```
1. spec/lr35902/groups/block1-ld-reg-reg.json:
   { "name": "LdReg_Reg", "pattern": "01dddsss", ...
     "instructions": [{ "mnemonic": "LD", "steps": [
       { "op": "lr35902_read_r8",  "field": "sss", "out": "src" },
       { "op": "lr35902_write_r8", "field": "ddd", "value": "src" } ]}] }

2. SpecLoader.LoadCpuSpec → 變成 InstructionSetSpec object

3. SpecCompiler.Compile:
   - Lr35902Emitters.RegisterAll(registry)
     → registry["lr35902_read_r8"] = Lr35902ReadR8Emitter
     → registry["lr35902_write_r8"] = Lr35902WriteR8Emitter
   - 對每個 instruction 跑 InstructionFunctionBuilder.Build:
     - 建 LLVM function: void Execute_Main_LdReg_Reg_LD(state*, ins)
     - 給 function 加 attribute: nounwind, no-jump-tables (Windows MCJIT 救命用)
     - 對每個 step 找 emitter，呼叫 Emit(ctx, step)
       → ReadR8Emitter 產生：select chain 從 7 個 GPR 挑出 src
       → WriteR8Emitter 產生：select-merge stores 寫到對應 GPR
     - BuildRetVoid

4. HostRuntime.Compile:
   - BindUnboundExternsToTrap (memory_read_8 / write_8 / write_16)
   - JsonCpu 在 BindExtern 時把這些 global 的 initializer 設成 inttoptr(C# fn addr)
   - module.CreateMCJITCompiler → 機器碼

5. JsonCpu.StepOne:
   - Read opcode 0x78 at PC
   - DecoderTable.Decode(0x78) → format=LdReg_Reg, instruction=LD
   - GetFunctionPointer("Execute_Main_LdReg_Reg_LD") → IntPtr
   - Cast to (state*, uint) -> void
   - Call → 跑 native code，state buffer 內容更新
```

---

## 8. 邊界與演進方向

### 目前 emitter 的「不夠泛化」之處

1. **F register flag 寫回**：每個 ALU emitter 都自己寫
   `f_new = (z << 7) | (n << 6) | (h << 5) | (c << 4)`，但 flag bit
   位置 spec 已經有了。理論上可以做 helper `WriteFlagsByName(ctx, "Z", z, "N", n, ...)`。
2. **Half-carry 算法**：寫死在 alu_a_r8 / inc_r8 / dec_r8 / add_hl_rr /
   ...。如果有第二顆有 half-carry 的 CPU，這段會 copy-paste。
3. **(HL) special case dispatch**：spec 端 split format + emitter 端
   placeholder 的雙重處理，有點繞。可以考慮 emitter 端統一用 basic-block
   做正規 R-M-W，spec 不用 split。

### 加第三顆 CPU 時的工作量估算

假設要加 Z80：
- spec/z80/cpu.json + groups/*.json（約 20-30 個 group 檔，比 GB 多 40%
  因為有 IX/IY/shadow register 跟 ED-prefix 額外 instruction set）
- Z80Emitters.cs：可以直接 fork Lr35902Emitters，~80% 不變，加 IX/IY 偏移
  定址、shadow register swap、ED 子指令集 dispatch
- 框架程式碼：**零修改**
- 預估時間：1-2 週

加 6502：
- spec/mos6502/cpu.json + groups/*.json（~15 個 group，6502 比 GB 簡單）
- Mos6502Emitters.cs：~1000 行，主要是各種 addressing mode（zero-page、
  indirect-X、indirect-Y...）跟 BCD mode 相關的 ALU
- 框架程式碼：可能要擴 schema 加 `addressing_modes`，或不擴用 spec
  metadata 表達
- 預估時間：1-2 週

加 MIPS R3000：
- spec/mips_r3000/cpu.json + groups/*.json
- 32-bit RISC，跟 ARM 的形狀更像，可能可以**重用 ArmEmitters 70%**（換個
  flag 處理）
- 主要新東西：delay-slot branches、coprocessor-0 (CP0) 例外處理

### 框架本身可以怎麼變更好

- **更多 spec 元資料驅動**：把 flag policy / half-carry rule / addressing
  mode 移到 spec，emitter 變成「讀 spec 配置」的查表器
- **ORC LLJIT 取代 MCJIT**：解掉 Windows COFF section ordering 的歷史
  包袱，能 lazy compile + 重新 jit
- **Block JIT 取代 instruction JIT**：把連續 N 條指令 fuse 成一個 LLVM
  function，省 dispatch 開銷（這是 Phase 7 的工作）
- **Cycle accounting 提到 spec**：目前 conditional branch 的「taken
  extra cycles」寫在 JsonCpu C# 端，理想上應該 emitter 從 spec 的
  `cycles.form` 自動產生

---

## 9. 一句話總結

**JSON 描述 CPU 的「形狀」，emitter 描述「動詞的精確語義」。**

兩者加上通用的框架程式碼（SpecCompiler / HostRuntime / DecoderTable），
就能把一顆 CPU 的 spec 變成 JIT-compiled 的 native code。框架本身不需要
為新 CPU 修改，新 CPU 只要寫 spec + emitter，前一顆 CPU 不會被影響。

這個論點在 ARM7TDMI（已經跑通 jsmolka armwrestler / arm.gba / thumb.gba
全綠）跟 LR35902（已經跑通 Blargg cpu_instrs 11/11 + master "Passed all
tests"）兩顆完全不同的 CPU 上得到驗證。
