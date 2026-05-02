# AprCpu Framework — 設計概念與 Emitter 架構

> **原寫於 Phase 4.5 完工**（ARM7TDMI + LR35902 兩顆 CPU 都跑通的時點），
> **2026-05-03 大改：反映 Phase 5.8 emitter library refactor + Phase 7
> JIT 優化進行中的現況**。
>
> 紀錄整套框架是怎麼把「JSON CPU 描述」變成「跑得起 ROM 的 JIT」，
> 兩顆 CPU 各自的 emitter 真正在做什麼事，以及 Phase 5.8 通用化把
> LR35902 端的 emitter 量從 ~2620 行壓到 1346 行（−49%）後，剩下
> 的真正 L3 intrinsic 是哪些。

---

## 1. 框架的中心論點

> **「換 CPU 等於換 JSON。」**

這是 AprCpu 想證明的事。傳統 emulator 是手刻指令直譯器（一個大 switch-case
撐起所有 opcode），每顆 CPU 都重寫一遍。AprCpu 把 CPU 拆成兩部分：

- **資料**：JSON spec — register file、encoding、step list
- **動詞**：emitter — 每個 step 怎麼變成 LLVM IR

對應檔案（Phase 5.8 / 7 後現況）：

```
spec/<arch>/cpu.json + groups/*.json     ← 資料（每顆 CPU 自己的）
src/AprCpu.Core/IR/<Arch>Emitters.cs     ← 動詞（每顆 CPU 自己的；越來越薄）
src/AprCpu.Core/IR/Emitters.cs           ← L0/L1 通用 micro-op
src/AprCpu.Core/IR/StackOps.cs           ← L1 stack 通用 ops（Phase 5.8.1 新）
src/AprCpu.Core/IR/FlagOps.cs            ← L1 flag 通用 ops（Phase 5.8.2 新）
src/AprCpu.Core/IR/BitOps.cs             ← L1 bit 通用 ops（Phase 5.8.4 新）
src/AprCpu.Core/IR/MemoryEmitters.cs     ← 通用 memory bus 接點
src/AprCpu.Core/IR/BlockTransferEmitters.cs ← LDM/STM 用 (ARM)
src/AprCpu.Core/IR/ArmEmitters.cs        ← ARM 專用 emitter
src/AprCpu.Core/IR/Lr35902Emitters.cs    ← LR35902 專用 emitter
src/AprCpu.Core/Compilation/SpecCompiler.cs ← 通用編譯流程
src/AprCpu.Core/Runtime/HostRuntime.cs   ← MCJIT + extern binding
src/AprCpu.Core/Runtime/CpuExecutor.cs   ← fetch-decode-execute dispatcher
src/AprCpu.Core/Decoder/DecoderTable.cs  ← bit-pattern dispatch
```

**「換 CPU 不需要碰框架程式碼」這一條已經被驗證**：ARM7TDMI 跟 LR35902
的 emitter 完全互不引用，新增一顆不會破壞另一顆。Phase 5.8 之後，新加
CPU 的 emitter 工作量再降一半（很多原本要寫的 ops 已通用化進 StackOps /
FlagOps / BitOps）。

---

## 2. 四層架構（Phase 5.8 引入 L0/L1/L2/L3 分層）

```
┌──────────────────────────────────────────────────────────────┐
│ Layer 0 — L0 完全通用 ops                                    │
│ 跨所有 CPU 邏輯一致，零配置。                                 │
│                                                               │
│ Emitters.cs (StandardEmitters):                              │
│   register I/O: read_reg / write_reg                         │
│   算術: add / sub / and / or / xor / shl / lshr / ashr /     │
│        rsb / mul / mvn / bic / ror                           │
│   64-bit: umul64 / smul64 / add_i64                          │
│   控制流: if / select / branch / branch_link / branch_cc     │
│   PC: read_pc                                                │
│   Width: sext / trunc                                        │
│ MemoryEmitters.cs: load / store / read/write extern wiring   │
│ BlockTransferEmitters.cs: block_load / block_store           │
└──────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│ Layer 1 — L1 parametric — 通用 op + spec metadata            │
│ 同 pattern 不同 metadata。配置從 spec 來。                    │
│                                                               │
│ StackOps.cs (Phase 5.8.1):                                   │
│   push_pair / pop_pair / call / ret / call_cc / ret_cc       │
│   (透過 spec.stack_pointer 知道 SP 在哪)                      │
│ FlagOps.cs (Phase 5.8.2/7):                                  │
│   set_flag / toggle_flag / update_h_add / update_h_sub /     │
│   update_zero / update_h_inc / update_h_dec                  │
│   (透過 reg/flag 參數指定要動哪個 status reg 哪個 bit)        │
│ BitOps.cs (Phase 5.8.4):                                     │
│   bit_test / bit_set / bit_clear / shift {kind: ...}         │
└──────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│ Layer 2 — L2 idiom (planned, not yet built out)              │
│ 多 CPU 共享的「複合 idiom」，spec 配置變體。                  │
│                                                               │
│ 想法：barrel_shift_with_carry { variant: arm|z80|x86 }       │
│       block_transfer { mode: ldm|stm|... }                   │
│       raise_exception { vector, mode_swap }                  │
│ 目前還沒有獨立檔案；ARM 的 if_arm_cond + raise_exception     │
│ 有 L2 性質但住在 ArmEmitters。                                │
└──────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│ Layer 3 — L3 architecture-specific intrinsics                │
│ 真 CPU-unique 怪癖。只在這顆 CPU 出現的 hardware quirk 或    │
│ 形狀差異。                                                    │
│                                                               │
│ ArmEmitters.cs (~22 ops):                                    │
│   update_nz / update_c_* / update_v_* (CPSR 特有 flag)        │
│   adc / sbc / rsc (carry-aware ALU)                          │
│   read_psr / write_psr / restore_cpsr_from_spsr              │
│   if_arm_cond / branch_indirect / raise_exception            │
│ Lr35902Emitters.cs (~13 ops, post Phase 5.8 refactor):       │
│   read_reg_named / write_reg_named (named reg I/O)           │
│   read_reg_pair_named / write_reg_pair_named                 │
│   lr35902_read_r8 / write_r8 / write_rr_dd (operand resolver)│
│   lr35902_alu_a_r8/hl/imm8 (compound ALU + flag rules)       │
│   lr35902_add_hl_rr / add_sp_e8 / ld_hl_sp_e8 (16-bit + flag)│
│   lr35902_ime / ime_delayed / halt / stop (interrupt 路徑)   │
│   lr35902_daa (BCD adjust)                                   │
│   load_byte / store_byte / store_word / read_imm8/16         │
└──────────────────────────────────────────────────────────────┘
```

`SpecCompiler.Compile` 用 `architecture.family` 字串挑：

```csharp
StandardEmitters.RegisterAll(registry);   // L0/L1 都在這
if (family == "ARM")           ArmEmitters.RegisterAll(registry);
else if (family == "Sharp-SM83") Lr35902Emitters.RegisterAll(registry);
// 加第三顆只要再加一個 if ... 然後寫 spec/<新 CPU>/ 跟 <NewCpu>Emitters.cs
```

加第三顆 CPU 預估只要寫 5-10 個 L3 ops（Phase 5.8 把可通用化的全收掉
了）+ spec metadata。

---

## 3. JSON 端能描述到什麼程度

完全不需要寫 C# 就能搞定的事：

| 屬性 | 在 spec 哪 |
|---|---|
| 暫存器個數、寬度、命名 | `register_file.general_purpose` |
| 16-bit pair 怎麼從兩個 8-bit 拼出來 | `register_file.register_pairs` |
| Stack pointer 在哪（GPR index 或 status reg name） | `register_file.stack_pointer` (Phase 5.8.1 加) |
| Status reg 跟它的 flag bit 位置 | `register_file.status[].fields` |
| Banked register / processor mode | `processor_modes` |
| Exception 向量地址 | `exception_vectors` |
| 多 instruction set + 切換條件 | `instruction_set_dispatch` |
| Opcode 怎麼解碼（mask/match/fields） | `encoding_groups[].formats[].pattern` |
| 同 format 內多條指令選擇 | `instructions[].selector` |
| 每條指令 cycles | `instructions[].cycles.form` |
| 指令的執行步驟 | `instructions[].steps[]` |

L1 ops 透過 spec metadata 即可描述：

| 行為 | 步驟 |
|---|---|
| Push 16-bit pair | `{ "op": "push_pair", "name": "BC" }` |
| Conditional call | `{ "op": "call_cc", "target": "addr", "cond": { "reg": "F", "flag": "Z", "value": 0 } }` |
| 設 flag 為常數 | `{ "op": "set_flag", "reg": "F", "flag": "C", "value": 1 }` |
| 更新 Z = (val == 0) | `{ "op": "update_zero", "in": "result", "reg": "F", "flag": "Z" }` |
| Bit test | `{ "op": "bit_test", "in": "v", "bit_field": "bbb", "reg": "F", "flag": "Z" }` |
| Shift kind | `{ "op": "shift", "kind": "rlc", "in": "v", "out": "w", "reg": "F", "flag_z": "Z", "flag_c": "C" }` |

不能只用 spec 表達、必須寫 emitter 程式碼的事（這就是 L3 的領域）：

| 行為 | 為什麼 JSON 表達不了 |
|---|---|
| BCD 調整邏輯（DAA） | 一坨條件分支 + lookup table |
| Compound ALU 的 flag rules（ADC carry-in、ADD HL,rr 的 bit-11 H flag） | 多 step 跨層的條件累加，spec 化會把 IR builder 塞進 JSON |
| 「3-bit field 中某個值代表 memory access 不是 register」 | 需要 select chain + 條件 store + bus extern call |
| 通知 host runtime 「我 HALT 了」/「IME 延遲 1 指令」 | C# / IR 邊界 wiring + per-CPU 怪行為 |
| 通用的 push/pop 已可用 spec 描述 ✅ | (Phase 5.8.1 之前需要 emitter，現在不用) |
| 通用的 conditional branch 已可用 spec 描述 ✅ | (Phase 5.8.3 之前需要 emitter，現在不用) |
| 通用的 bit ops + shift 已可用 spec 描述 ✅ | (Phase 5.8.4 之前需要 emitter，現在不用) |

最後三條從 L3 降到 L1 是 Phase 5.8 refactor 的主要產出。

---

## 4. ArmEmitters 在做什麼

對應檔案：`src/AprCpu.Core/IR/ArmEmitters.cs`（**~870 行，22 ops**）。
未受 Phase 5.8 refactor 影響（refactor 主攻 LR35902 端的通用化）。

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

> Phase 5.8.2 加了通用 `set_flag` / `toggle_flag` / `update_zero` / 等
> L1 ops，但 ARM 端目前**還沒套用**（沒被 refactor 動到，因為主目標是
> LR35902）。第三 CPU 上線時才會把 ARM update_* 也改 spec-driven。

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
| `if_arm_cond` | ARM 14 個 cond code 求值（EQ/NE/CS/CC/MI/PL/...）|
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
`read_reg`、`write_reg`、`add`、`sub`、`mul`、`if`、`branch`、`branch_cc`
（5.8.3 加）等。所以 ArmEmitters.cs 純粹處理 ARM 不能共用的部分。

---

## 5. Lr35902Emitters 在做什麼（Phase 5.8 大幅縮減後）

對應檔案：`src/AprCpu.Core/IR/Lr35902Emitters.cs`（**~1346 行，~13 ops**）。

> **Phase 5.8 emitter library refactor 完工後的現況**：起點 ~2620 行 /
> ~40 ops，refactor 把 27 個 ops 通用化掉、文件減 49%。剩下 13 個 ops
> 都在 file 內 clearly-marked L3 intrinsic 區段，分三類：操作元 resolver、
> compound ALU + flag、真硬體怪癖。

### 5.1 跨架構 helper（住在 LR35902 檔但其實是 8-bit CPU 通用）

| op | 功能 |
|---|---|
| `read_reg_named` | 用名字（"A"/"B"/.../"SP"/"PC"）找暫存器，自動識別是 GPR 還是 status reg、自動配對應 LLVM 寬度 |
| `write_reg_named` | 同上，寫入時自動 trunc/zext 對齊寬度 |
| `read_reg_pair_named` | "BC"→組合 B/C 成 i16；"SP"→直接讀 i16 status |
| `write_reg_pair_named` | 反向；AF 特例強制 mask F 低 4 bit = 0 |

> 這四個之後若有第二顆 8-bit CPU 進來，可以 promote 到 StandardEmitters。
> 目前是 LR35902-only 但 op name 跟 schema 都已通用化。

### 5.2 Operand resolvers（L3 — 操作元解碼）

| op | 功能 |
|---|---|
| `lr35902_read_r8` | 3-bit `sss` field → B/C/D/E/H/L/(HL)/A 之一；select chain |
| `lr35902_write_r8` | 同上的鏡像 |
| `lr35902_write_rr_dd` | 2-bit `dd` field → BC/DE/HL/SP |

這三個是 LR35902 ISA 編碼的一部分。第三 CPU 上線時可考慮加 spec-side
operand resolver registry（讓 spec 宣告自己的 sss/dd 表），但目前先當
L3 留在 emitter。

### 5.3 Compound ALU + flag rules（L3 — 算術 + LR35902 flag 規則）

| op | 功能 |
|---|---|
| `lr35902_alu_a_r8` | ADD/ADC/SUB/SBC/AND/XOR/OR/CP，source 是 sss 選的 reg |
| `lr35902_alu_a_hl` | 同 8 個 op，source 是 memory[HL] |
| `lr35902_alu_a_imm8` | 同 8 個 op，source 是 imm8 |
| `lr35902_add_hl_rr` | 16-bit ADD HL,rr，H 從 bit 11→12 進位 |
| `lr35902_add_sp_e8` | SP ← SP + sign_ext(e8)；H/C 從 SP 跟 e8 的低 byte unsigned 加法（LR35902 怪規則） |
| `lr35902_ld_hl_sp_e8` | HL ← SP + sign_ext(e8)，flag 規則同上 |

算術本身通用，但 flag bit 位置 + derivation rules 是 LR35902 特有。
Refactor 評估認為拆成 10+ 個 generic ops/指令會把 spec 灌爆而沒實質
收穫，留 L3。

### 5.4 真硬體怪癖（L3 — hardware quirks）

| op | 功能 |
|---|---|
| `lr35902_ime` | call extern `host_lr35902_set_ime(0\|1)` (DI 0 / RETI 1) |
| `lr35902_ime_delayed` | call extern `host_lr35902_arm_ime_delayed`（EI 的延遲一指令語意） |
| `halt` | call extern `host_lr35902_halt` 設 _haltSignal |
| `stop` | 同 halt extern (DMG STOP 視為 halt) |
| `lr35902_daa` | BCD 調整：根據 N/H/C 跟 A 的當前值挑 +0x60 / +0x06 / -0x60 / -0x06 |

這幾個 op 的 host extern 跟 EI delay 機制不存在於其他 CPU 家族；確定
留 L3。

### 5.5 Bus-level helpers（L3 — currently lives in LR35902 file，未來搬走）

| op | lower 到 |
|---|---|
| `load_byte` | `memory_read_8(i32 addr)` → i8 |
| `store_byte` | `memory_write_8(i32 addr, i8 v)` |
| `store_word` | `memory_write_16(i32 addr, i16 v)`（host shim 拆兩 byte） |
| `read_imm8` | 從 PC 讀 1 byte 並 PC++ |
| `read_imm16` | 從 PC 讀 2 byte 並 PC += 2 |

op name 是 generic 的，但目前 implementation 在 LR35902 file。Phase
5.8 cleanup 時識別出這 5 個其實是 cross-arch 的 bus 包裝，後續會搬到
MemoryEmitters.cs 或新的 BusOps.cs。

### 5.6 Refactor 已收掉的（**不再存在於 codebase**）

Phase 5.8 把這 27 個 LR35902 ops 通用化進 L1 op：

```
push_qq / pop_qq                                   → push_pair / pop_pair
jp / jp_cc / jr / jr_cc / call / call_cc /
  ret / ret_cc / rst                               → branch / branch_cc / call / call_cc / ret / ret_cc
cb_bit / cb_bit_hl_mem / cb_set / cb_resset_hl_mem / cb_res
                                                   → bit_test / bit_set / bit_clear
cb_shift / cb_shift_hl_mem                          → shift {kind: ...}
ARotateEmitter (rlca/rrca/rla/rra)                 → shift {kind: rlc/rrc/rl/rr} + set_flag(Z=0)
ldh_io_load / ldh_io_store                          → or {const: 0xFF00} + load_byte/store_byte
scf / ccf / cpl                                     → set_flag / toggle_flag / mvn + set_flag
inc_pair / dec_pair / inc_r8 / dec_r8 /
  inc_hl_mem / dec_hl_mem / inc_rr_dd / dec_rr_dd  → read + add/sub + trunc + write + update_zero/h_inc/h_dec
SimpleNoOpEmitter (cb_dispatch)                    → empty steps in spec
```

所有這些原本 LR35902-only 的指令 patterns，現在 spec 端用 generic op
chain 描述，emitter 端不需要 LR35902-specific 程式碼。

---

## 6. 兩邊 emitter 的對照表（Phase 5.8 後）

| 面向 | ArmEmitters | Lr35902Emitters |
|---|---|---|
| 行數（Phase 5.8 後） | ~870 | **1346** (was ~2500) |
| Op 數 | ~22 | **~13** (was ~40) |
| 主要 GPR 寬度 | 32-bit 一致 | 8-bit + 16-bit (SP/PC/pair) 混合 |
| Flag layout | CPSR bit 31..28 (NZCV) + 7 (I) + 5 (T) | F bit 7..4 (ZNHC) |
| 特殊 flag 算法 | C, V (signed overflow) | H (half-carry, bit 3→4), DAA |
| Mode / banking | 7 modes, banked R8-R14 | 無 |
| Pipeline offset | 有（PC = current+8 ARM、+4 Thumb） | 無 |
| 多指令集切換 | CPSR.T 持久 state | 0xCB prefix 瞬時切 |
| Host extern | swap_register_bank（mode 切換時） | halt + set_ime + arm_ime_delayed |
| Memory access | 通用 load/store + block_load/store | load_byte/store_byte/store_word + read_imm |
| 條件控制流 | `if_arm_cond`（每條指令 prefix） + `branch_indirect` | 通用 `branch_cc` (Phase 5.8.3 加) |
| Field-driven r 選擇 | 直接 `read_reg index="rn"` | `lr35902_read_r8` (sss=6 是 memory) |
| BCD | 無 | DAA |

ARM 跟 LR35902 在「形狀」上的差異依然決定 emitter 量級，但 Phase 5.8
之後 LR35902 的「ALU/flag/control flow 包裝層」全部下放到 L1 通用 ops，
剩 13 個都是真 L3。

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
   - StandardEmitters.RegisterAll(registry)
     → 全部 L0/L1 ops 註冊 (StackOps/FlagOps/BitOps 都包在裡面)
   - Lr35902Emitters.RegisterAll(registry)
     → registry["lr35902_read_r8"] = Lr35902ReadR8Emitter
     → registry["lr35902_write_r8"] = Lr35902WriteR8Emitter
     → 等其他 13 個 L3 ops
   - 對每個 instruction 跑 InstructionFunctionBuilder.Build:
     - 建 LLVM function: void Execute_Main_LdReg_Reg_LD(state*, ins)
     - 加 attribute: nounwind, no-jump-tables (Windows MCJIT 救命用)
     - 對每個 step 找 emitter，呼叫 Emit(ctx, step)
       → ReadR8Emitter 產生：select chain 從 7 個 GPR 挑出 src
       → WriteR8Emitter 產生：select-merge stores 寫到對應 GPR
     - BuildRetVoid

4. HostRuntime.Compile:
   - BindUnboundExternsToTrap (memory_read_8 / write_8 / write_16)
   - JsonCpu 在 BindExtern 時把這些 global 的 initializer 設成 inttoptr(C# fn addr)
   - module.CreateMCJITCompiler with OptLevel=3 (Phase 7 B.a) → 機器碼

5. JsonCpu.StepOne (or CpuExecutor.Step):
   - Read opcode 0x78 at PC
   - DecoderTable.Decode(0x78)
     → 走預先建好的 entries list (Phase 7 F.y: 回傳 cached DecodedInstruction，
       不再每次 new 一個)
   - ResolveFunctionPointer(decoded.Instruction)
     → Phase 7 F.x: identity-keyed cache，不再每次 string-format 跟 dictionary
       string-hash
   - Cast IntPtr to (state*, uint) -> void
   - Call → 跑 native code，state buffer 內容更新
```

---

## 8. 邊界與演進方向

### 8.1 Phase 5.8 已收的「不夠泛化」之處

✅ **CB-prefix bit/shift dispatch** — 7 個 LR35902 ops → 4 個通用 (Phase 5.8.4)
✅ **Push/pop/call/ret** — 11 個 LR35902 ops → 4 個通用 (Phase 5.8.1)
✅ **Conditional branch** — 多個 _cc emitter → branch_cc + call_cc + ret_cc (Phase 5.8.3)
✅ **F register flag micro-ops** — set_flag/toggle_flag/update_h_*/update_zero (Phase 5.8.2/7)
✅ **A-rotate (RLCA/RRCA/RLA/RRA)** — 收進通用 shift (Phase 5.8.4.B)
✅ **LDH IO 區段定址** — 收進 `or` + auto-coerce (Phase 5.8.5)
✅ **INC/DEC family** — 收進 generic add/sub + trunc + flag updates (Phase 5.8.7.B)

### 8.2 Phase 5.8 沒做、刻意留 L3 的部分

- **Operand resolver registry**：`lr35902_read_r8/write_r8/write_rr_dd`
  仍是 LR35902-specific，等第三 CPU 上線才知道是否需要 spec-side
  resolver registry
- **Compound ALU + flag rules**：`alu_a_*` / `add_hl_rr` / `add_sp_e8` /
  `ld_hl_sp_e8` 拆成多 step 會把 spec 灌爆，留 L3
- **真硬體怪癖**：DAA、IME delay、HALT bug — 這些不該通用化
- **bus-level helpers**：`load_byte` / `store_byte` 等 op name 是 generic，
  但 implementation 還在 Lr35902Emitters.cs；後續搬到 MemoryEmitters

### 8.3 加第三顆 CPU 時的工作量估算（Phase 5.8 後）

假設要加 RISC-V RV32I（最乾淨的 32-bit RISC）：
- spec/riscv32/cpu.json + groups/*.json（~10-15 個 group 檔，比 ARM 簡單）
- Riscv32Emitters.cs：**預估 200-400 行**（vs Phase 5.8 之前的 1000+ 行）
  - 主要是 mstatus / mtvec exception entry、可能 0-3 個 RISC-V-unique
    intrinsic（ECALL / EBREAK / FENCE）
  - L0/L1 通用 ops cover 99% 的 ALU / load/store / branch
- 框架程式碼：**零修改**（除非碰到操作元 resolver 或 compound ALU 的
  泛化需要）
- 預估時間：3-5 工作日（vs 1 週多）

加第二顆 8-bit CPU（如 Z80 或 6502）：
- 8-bit + paired-register 的 helper 已經在 LR35902 檔，可上拉到通用
- LR35902 留下的 13 個 L3 ops 多半 Z80 也有對應（更多）；6502 比較簡單
- 預估時間：5-7 工作日

### 8.4 Phase 7 JIT 優化進行中（2026-05-03 開跑）

Refactor 完工後 emitter 量收斂得夠乾淨，開始攻 perf。Phase 7 設計了
A-G 七個分組（block-JIT / IR inlining / lazy flag / hot-path tier /
mem-bus fast path / dispatcher cleanup / .NET AOT），順序低風險到高
風險。

完成的（截至 2026-05-03 早晨）：

| Step | 動作 | 結果 |
|---|---|---|
| **B.a** OptLevel 0 → 3 | 一行 config | perf-neutral（per-instr function 太小，LLVM 無發揮空間）— 留著為 block-JIT 預備 |
| **F.x** Identity-keyed fn ptr cache | dispatcher cache 改 InstructionDef 引用 | **GB json-llvm +82%** (2.66 → 4.83 MIPS), GBA Thumb +25% (3.75 → 4.69) |

詳細數據 + 策略 ROI 分析見 `MD/performance/` 系列檔案，每個策略一份
獨立 note，referencing canonical baseline `202605030002-...`。

### 8.5 框架本身可以怎麼變更好

- **ARM update_* ops 也走 spec metadata**：FlagOps 已經把 LR35902 端
  收掉了，ARM 的 update_nz / update_c_* 等也可以改 L1。等 Phase 7
  穩定後做。
- **ORC LLJIT 取代 MCJIT**：解掉 Windows COFF section ordering 的歷史
  包袱，能 lazy compile + 重新 jit。Phase 7 group A 的 prerequisite。
- **Block JIT 取代 instruction JIT**：把連續 N 條指令 fuse 成一個 LLVM
  function，省 dispatch 開銷（Phase 7 group A 主菜）。
- **Cycle accounting 提到 spec**：目前 conditional branch 的「taken
  extra cycles」寫在 JsonCpu C# 端，理想上應該 emitter 從 spec 的
  `cycles.form` 自動產生。
- **Bus-level helper 搬出 LR35902Emitters**：load_byte 等已是 generic
  名字，搬到 MemoryEmitters 才符合分層。

---

## 9. 一句話總結

**JSON 描述 CPU 的「形狀」，emitter 描述「動詞的精確語義」。Phase 5.8
之後 emitter 又分成 L0/L1（跨 CPU 共用）跟 L3（CPU-unique），讓
「換 CPU = 換 JSON」承諾的可信度從「ARM/LR35902 兩顆驗證」推到「真要
加第三顆只剩寫 ~5-10 個 L3 op + 配置 metadata」。**

兩者加上通用的框架程式碼（SpecCompiler / HostRuntime / DecoderTable），
就能把一顆 CPU 的 spec 變成 JIT-compiled 的 native code。框架本身不需要
為新 CPU 修改，新 CPU 只要寫 spec + 越來越薄的 emitter，前一顆 CPU 不
會被影響。

這個論點在 ARM7TDMI（已經跑通 jsmolka armwrestler / arm.gba / thumb.gba
全綠）跟 LR35902（已經跑通 Blargg cpu_instrs 11/11 + master "Passed all
tests"）兩顆完全不同的 CPU 上得到驗證；Phase 5.8 emitter library refactor
進一步把每顆新 CPU 的 emitter 工作量降到「~5-10 個 L3 ops + 配置」級別。
