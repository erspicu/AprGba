# Emitter Library Refactor — 通用化 + Intrinsic 模式

> **狀態**：Phase 5.7 完工後的設計提案 (2026-05-02)。
> 在 Phase 7 block-JIT 之前先處理「per-CPU emitter 過度膨脹」的結構問題。
>
> 動機：目前 LR35902 emitter 檔案 2514 行（40 個 op），ARM 854 行
> （22 個 op），加新 CPU 等於再寫一檔 ~1000+ 行 C#。違背了
> framework 「換 CPU 等於換 JSON」的核心承諾。
>
> 目標：把每顆新 CPU 的 emitter 工作量從「~40 個 ops × C# 實作」
> 降到「最多 5-10 個 truly-unique ops + 配置 metadata」。

---

## 0. 實作進度（持續更新）

| Step | 狀態 | Commits | 檔案/op 變化 |
|---|---|---|---|
| 5.1 stack ops generalisation | ✅ 完成 | `29b0351` | +`StackOps.cs` 新 4 ops (`push_pair`/`pop_pair`/`call`/`ret`) + spec `stack_pointer` metadata |
| 5.2 flag setters generalisation | ✅ 完成 | `6c377ec` | +`FlagOps.cs` 新 3 ops (`set_flag`/`update_h_add`/`update_h_sub`)；LR35902 SCF migration |
| 5.3 branch / call_cc / ret_cc unification | ✅ 完成 | `7c0f486` `bd8f46d` `afe5dad` | 新 5 ops (`branch_cc`/`call_cc`/`ret_cc`/`read_pc`/`sext`) + `target_const`；LR35902 JP/JR/CALL/RET/RST 全 cc 變體；cleanup `-408 lines` |
| 5.4 bit ops + shift unification | ✅ 完成 | `1c1f6a5` `0e23ad7` `390eb7e` | +`BitOps.cs` 新 4 ops (`bit_test`/`bit_set`/`bit_clear`/`shift`)；LR35902 BIT/SET/RES + 8 種 CB shift + 4 個 A-rotate；cleanup `-485 lines` |
| 5.5 memory IO region 統一 | ✅ 完成 | `28eab8e` | Binary 加 width auto-coerce；LR35902 LDH/LD-(C) 改用 `or` + load_byte/store_byte；刪 LdhIoLoad/Store emitter |
| 5.6 IME / cb_dispatch cleanup | ✅ 完成 | `3661bbf` | 刪 `cb_dispatch` no-op (空 steps)；IME/HALT/DAA 標 L3 intrinsics 帶 divider |
| 5.7.A flag micro-ops cleanup | ✅ 完成 | `f6eede5` | `mvn` width-aware；新 `toggle_flag`；LR35902 CCF/CPL/SCF 完成 migration |
| 5.7.B inc/dec migration | ✅ 完成 | `7695858` | 新 `update_zero`/`update_h_inc`/`update_h_dec`/`trunc`；LR35902 INC/DEC r/(HL) + named-pair inc/dec |
| 5.7.C/D 16-bit selectors + L3 marker | ✅ 完成 | `2b277d1` | INC/DEC rr 改 4 selector variants；剩餘 13 個 LR35902 ops 標 L3 intrinsics with reasons |
| 5.8/5.9 第三 CPU 驗證 | ⏳ 待做 | — | RISC-V RV32I 或 MIPS R3000 |

**5.7 收工 snapshot (2026-05-02)**：

| 檔案 | 起點 (5.0 前) | 5.7 收工 | 變化 |
|---|---:|---:|---:|
| `Lr35902Emitters.cs` | ~2620 行 | 1346 行 | **−49%** |
| `Emitters.cs` (通用) | 613 行 | ~770 行 | +25%（吸收 LR35902 通用化） |
| 新增 `StackOps.cs` | — | ~415 行 | — |
| 新增 `FlagOps.cs` | — | ~155 行 | — |
| 新增 `BitOps.cs` | — | ~195 行 | — |

**驗證面**：每個 step 都跑 345/345 unit test + 至少 3 個 Blargg sub-test
（含 02-interrupts / 07-jr,jp,call,ret,rst / 10-bit ops）在 legacy +
json-llvm 兩條後端 Passed。345 unit test 從 5.0 到 5.7 結束數量沒變過 —
純結構，沒新增測試也沒掉測試。

**移除的 LR35902-specific emitters（5.1–5.7 累計）**：

```
push_qq / pop_qq                                   (5.1 stack)
jp / jp_cc / jr / jr_cc / call / call_cc /
  ret / ret_cc / rst                                (5.3 branch)
cb_bit / cb_bit_hl_mem / cb_set / cb_resset_hl_mem /
  cb_res / cb_shift / cb_shift_hl_mem               (5.4 CB-prefix)
ARotateEmitter (lr35902_rlca/rrca/rla/rra)         (5.4.B A-rotates)
EvalCondition / PcPointer / NormaliseToI16 helpers (5.3 cleanup)
ldh_io_load / ldh_io_store                          (5.5 IO region)
SimpleNoOpEmitter (cb_dispatch)                     (5.6 dispatch)
scf / ccf / cpl                                     (5.7.A flag ops)
inc_pair / dec_pair / inc_r8 / dec_r8 /
  inc_hl_mem / dec_hl_mem / inc_rr_dd / dec_rr_dd  (5.7.B/C inc/dec)
```

合計刪除 **27 個 LR35902-specific ops + 多個 helper**。剩下 13 個都在
clearly-marked L3 intrinsic 區段，分兩類：

1. **Operand resolvers** — `lr35902_read_r8` / `write_r8` / `write_rr_dd`：
   sss/dd field → register table 編碼是 LR35902 ISA 特性，跨 CPU 無法
   通用化（ARM 4-bit、RISC-V 5-bit、各自不同 table）。等第三 CPU 真要
   port 時，再加 spec-side operand resolver registry。

2. **Compound ALU + flag rules** — `alu_a_r8/hl/imm8`（一個 class 三個
   名字）、`add_hl_rr`、`add_sp_e8`、`ld_hl_sp_e8`：算術本身通用，但
   flag bit 位置 + derivation rules 是 arch-specific。拆成 10+ 個 generic
   ops/指令會把 spec 灌爆而沒實質收穫。

3. **真 L3 hardware quirks**（5.6 已標）— `lr35902_ime` / `ime_delayed`
   (EI delay)、`halt` / `stop` (halt-bug + STOP wakeup)、`lr35902_daa`
   (BCD adjust)。

**結論**：~62 個 arch-specific ops（refactor 起點）→ **13 個 L3 intrinsics**
（=−79%）。設計 doc §1.3 預估 5–10 個 L3 floor，實際稍高（13 個），主要差
在 operand resolver 跟 compound ALU 都還沒拆。等第三 CPU 上線再驗證
這個 floor 是否真的合理 — 這是 Phase 5.8/5.9 階段的工作。

**Refactor 對 perf 的影響**：跑了 4-ROM 1200-frame loop100 bench
（vs `MD/note/loop100-bench-2026-05.md` 5.7 baseline），主框架路徑
(json-llvm) GBA 微幅進步、GB 微幅退步，整體在 measurement noise 內
（±5%）。GB legacy 跌 −15% 但 single-run 量測 noise 比例高（~300ms
total）— 不是 framework 主路徑。詳細數據見
`MD/note/loop100-bench-2026-05-phase5.8.md`。「結構乾淨 vs 速度中性」
的 trade-off 站得住腳。

---

## 1. 現況盤點

### 1.1 Emitter 檔案 + op 數

| 檔案 | 行數 | op 數 | 類別 |
|---|---:|---:|---|
| `Emitters.cs` | 613 | 10 | 通用基礎 |
| `ArmEmitters.cs` | 854 | 22 | ARM 專用 |
| `Lr35902Emitters.cs` | 2514 | 40 | GB DMG 專用 |
| `BlockTransferEmitters.cs` | 369 | 2 | LDM/STM |
| `OperandResolvers.cs` | 480 | 0 | ARM operand 解析（resolver，不是 emitter） |
| `MemoryEmitters.cs` | 163 | (helpers) | 通用 mem hook 宣告 |
| `ConditionEvaluator.cs` | 136 | (helpers) | ARM/LR35902 共用 cond table |
| **合計（refactor 起點）** | **~5800 lines** | **74** ops | |

**2026-05-03 更新後實際狀態**（5.7 完工 + Phase 7 額外加 BlockFunctionBuilder
+ InstructionFunctionBuilder + 4.5c 跟 instcombine fix 後）：

| 檔案 | 行數 |
|---|---:|
| `Emitters.cs` | 1051（+438：吸收 LR35902 generic ops + IfStep const-fold + cpsr helpers） |
| `ArmEmitters.cs` | 909（+55：A.6.1 Strategy 2 + freeze switch 等修補後又移除） |
| `Lr35902Emitters.cs` | 1346（−47%：5.7 refactor 一次到位） |
| `BlockTransferEmitters.cs` | 399 |
| `BlockFunctionBuilder.cs` | 320（Phase 7.A.2 新加） |
| `InstructionFunctionBuilder.cs` | 139（Phase 7.A.2 新加） |
| `StackOps.cs` / `FlagOps.cs` / `BitOps.cs` | 425 / 182 / 261（5.1-5.4 新檔） |
| `OperandResolvers.cs` | 525 |
| `CpuStateLayout.cs` / `EmitContext.cs` / `MemoryEmitters.cs` / `ConditionEvaluator.cs` | 325 / 206 / 182 / 234 |
| `IMicroOpEmitter.cs` / `IOperandResolver.cs` | 42 / 61 |
| **目前合計** | **6607 lines** |

新增的行數主要進到 generic 區（`Emitters.cs` +438、新檔 `Stack/Flag/BitOps`），
LR35902-specific 區一直在縮小（−1168 行）。framework 通用化 trajectory 持續。

### 1.2 74 個 op 的「直覺分類」(WRONG — 過度保守)

第一輪盤點時把 ~30 個 LR35902 ops 標成 L3，但仔細看後發現：
**幾乎全部都是 CPU 通用功能**，只是 bit 位置 / 暫存器命名 / 位移
方式不同。真 L3 不該超過 5-10 個。

### 1.3 修正分類（aggressive — pushing 通用化）

| 類別 | op 範例 | 數量 | 為什麼通用 |
|---|---|---:|---|
| **L0：完全通用** — 跨所有 CPU 邏輯一致 | `read_reg`, `write_reg`, `add`, `sub`, `and`, `or`, `xor`, `shl`, `shr`, `sar`, `ror`, `mul`, `bic`, `mvn`, `read_imm8/16`, `store_byte`, `load_byte`, `if`, `select`, `branch`, `branch_link` | ~20 | 已經是 |
| **L1：parametric — 通用 op + spec metadata** | `update_flag`, `read_psr`, `write_psr`, `read_pair`, `write_pair`, `push`, `pop`, `call`, `ret`, `cond_branch`, `set_ime`, `bit_test`, `bit_set`, `bit_clear`, `byte_swap`, `nibble_swap`, `daa_class` | ~17 | Pattern 相同；bit 位置/暫存器/flag 名來自 spec 配置 |
| **L2：idiom 庫**（多 CPU 共享，但細節 variant 多）| `barrel_shift_with_carry { variant: arm\|z80\|x86 }`, `block_transfer { mode: ldm\|stm\|rep_movs }`, `raise_exception { vector: ...; mode_swap: ... }`, `bcd_adjust { variant: z80\|6502\|x86 }` | ~6-8 | 同 idiom 但 variant 配置驅動 |
| **L3：真正獨特** — 1-2 顆 CPU 才有的硬體怪癖 | `lr35902_ime_delay` (EI 延遲 1 指令)、ARM exception 完整路徑（SPSR bank swap + T-clear + R14 = pc+4 的奇怪 offset）、ARM7TDMI `LDM/STM rlist=0` 怪行為、PowerPC `lwarx/stwcx`、6502 BRK 的奇 7-cycle 行為... | **~5-10** | 真的只在 1-2 個架構出現的 hardware bug-as-feature |

### 1.4 為什麼之前看起來那麼多 L3？

抽 ~12 個被誤判 L3 的例子重看：

- **`lr35902_call` / `lr35902_call_cc` / `lr35902_ret` / `lr35902_ret_cc`** — CALL/RET 是 CPU 通用功能（M68k JSR/RTS、x86 CALL/RET、ARM BL/BX-LR、RISC-V JAL/JALR、6502 JSR/RTS 全有）。**全是 L1**：`call { target, push_reg: PC }` + `ret { pop_to: PC }`，spec 配置 push/pop 用哪個 reg + 用哪個 SP。

- **`lr35902_push_qq` / `lr35902_pop_qq`** — push/pop 16-bit。M68k MOVE.L SP-/+, x86 PUSH/POP, ARM STMFD/LDMFD 都有。**L1 generic**：`push { name }`, `pop { name }` 配上 spec 的 register pair 表。

- **`lr35902_jp` / `lr35902_jp_cc` / `lr35902_jr` / `lr35902_jr_cc`** — absolute jump + relative jump + conditional 變體。每顆 CPU 都有。**全 L1**：`branch { addr_mode: absolute\|relative, cond: ... }`。

- **`lr35902_cb_bit` / `lr35902_cb_set` / `lr35902_cb_res` / `lr35902_cb_shift`** + 對應 `_hl_mem` 變體（共 7 個 ops）— bit-test/set/clear、shift 是 Z80 家族 + M68k + x86 + RISC-V Zbb 都有的東西。**全 L1/L2**：`bit_test { src: reg\|memory, bit: int }` 一個 op 取代 7 個。

- **`lr35902_ldh_io_load` / `lr35902_ldh_io_store`** — IO 區段 shortcut。x86 IN/OUT、6502 zero-page、M68k 沒這個但有類似 short-form addressing。**L1**：spec 宣告 io_region，通用 mem ops 自動 dispatch。

- **`lr35902_ime`** — interrupt master enable。ARM CPSR.I bit、RISC-V mstatus.MIE、M68k SR.I bit、x86 EFLAGS.IF 全部都有。**L1**：`set_ime { value: 0\|1 }` 配 spec 中 ime_register + ime_bit 位置。

- **`lr35902_scf` / `lr35902_ccf` / `lr35902_cpl`** — set/complement carry flag、complement A。每 CPU 都有 set/clear/toggle flag 的指令。**全 L1**：`flag_op { reg, flag, action: set\|clear\|toggle }`。

- **`lr35902_add_hl_rr`** — 16-bit add，dest 是 HL pair。M68k ADDA, x86 ADD ax, ARM ADD r,r。**L1**：generic `add` op + spec 端讓 dest 用 register pair。

- **`lr35902_add_sp_e8` / `lr35902_ld_hl_sp_e8`** — SP + signed 8-bit offset。M68k LEA、x86 LEA、ARM ADD/SUB sp。**L1**：`add { src: reg, offset: signed_imm8 }`，dest 配置決定。

**修正後結論**：~40 個 LR35902 ops 中 **30+ 個是 L1/L2 通用 functionality**
偽裝成 CPU-specific。真 L3 大概 **5 個**：`lr35902_daa` (BCD adjust)、
`lr35902_ime` 的「EI 延遲 1 指令」副作用（IME bit 本身是 L1，但延遲機
制獨特）、`lr35902_rst` 的硬連線跳轉表（L1 但很 small-scale）、HALT bug、
`lr35902_pop_af` 對 F flag 強制清低 4 bit 的怪癖。

**ARM 部分**也類似：`update_nz / update_c_*` 是 L1，`read_psr / write_psr`
是 L1，`raise_exception` 是 L2。真 L3 只剩：banked register swap (mode
change 觸發 R8-R14 重映射) + ARM7TDMI 一些 documented bug（empty LDM rlist
quirk、LDR/STR mis-aligned rotate）。

---

## 2. 提議架構：4 層 emitter 體系

### 2.1 Layer 概念

```
┌─────────────────────────────────────────────────────────────┐
│ L0  CoreOps         — 跨所有 CPU 共用，0 配置                │
│      add, sub, and, or, xor, shl/r, mul, branch, mem r/w     │
├─────────────────────────────────────────────────────────────┤
│ L1  ParametricOps   — 同 pattern 不同 metadata，配置驅動      │
│      update_nz, update_c_add, raise_exception, write_psr     │
│      → 從 spec 讀 flag bit 位置、status reg name、mode encode │
├─────────────────────────────────────────────────────────────┤
│ L2  IdiomatedOps    — 多 CPU 共享的「複合 idiom」             │
│      barrel_shift_with_carry (ARM/Thumb/SH4 都有)            │
│      block_transfer (ARM LDM/STM, 6502 LDA-loop, etc.)       │
│      cond_branch (ARM cond/Thumb F16/Z80 jr/M68k Bcc)        │
├─────────────────────────────────────────────────────────────┤
│ L3  ArchIntrinsics  — 真 CPU-unique 怪癖 (last-resort)       │
│      lr35902_daa, lr35902_ime_delay, arm_msr_cpsr_with_swap  │
└─────────────────────────────────────────────────────────────┘
```

### 2.2 Layer 設計原則

| Layer | 增加新 CPU 的工作 | 何時用 |
|---|---|---|
| L0 | **零** — 直接用 | 簡單 ALU / mem / branch |
| L1 | 改 spec metadata（cpu.json 內 status 定義 + mode encoding 表）| status flag / exception / mode switch |
| L2 | 從 spec 配置 idiom 變體（"shift_style": "arm" / "z80" / "x86"）| 複合 idiom |
| L3 | 寫 ~50-150 行 C# emitter | 真正獨特怪癖 |

### 2.3 與 Gemini 建議 #3 的對應

Gemini 講的「Intrinsic 內建函式」概念對應到 **L2 IdiomatedOps**：JSON 寫 `"op": "barrel_shift"` 而不拆成 5 個 micro-op，由 framework 內建的最佳化 LLVM IR generator 處理。

但本文進一步區分：**L2 是多 CPU 共享 idiom**（值得做 framework 級優化）vs **L3 是單 CPU 怪癖**（接受 hand-written C#）。

---

## 3. 具體重構計畫

### 3.1 通用化現有 ops（從 L3/L2 → L1/L0）

優先順序（從高 impact 到低）：

#### A. Register Pair 抽象（影響 GB ~10 個 ops）

當前：`lr35902_read_r8`, `lr35902_write_r8`, `lr35902_read_reg_pair_u64`, `lr35902_write_reg_pair`, `lr35902_push_qq`, `lr35902_pop_qq`, `lr35902_call`, `lr35902_ret`, `lr35902_jp`, `lr35902_rst`...

提議：framework 加「register pair」一級抽象（spec 端宣告
`register_pairs: { BC: [B,C], DE: [D,E], HL: [H,L], AF: [A,F], SP: [SP] }`），
然後加 L1 ops:
- `read_pair { name }` — 讀 16-bit pair
- `write_pair { name, value }` — 寫
- `push { name }` — 寫到 SP，SP-=2
- `pop { name }` — SP+=2，讀
- `call { target }` — push PC, branch
- `ret` — pop PC, branch

**消除**：~10 個 lr35902_* ops。

#### B. Status Register 抽象 (status reg + flag table)

當前：每個 CPU 自寫 `update_nz / update_c_* / update_v_*`，bit 位置 hardcoded。

提議：spec 端宣告 status reg 跟 flag bit 位置（已經有 framework
`StatusRegister.Fields`），通用 emitter 做：
- `update_flag { reg: "CPSR", flag: "N", from_msb_of: "<value>" }`
- `update_flag { reg: "F", flag: "Z", from_zero_test_of: "<value>" }`
- `update_flag_carry { reg: "CPSR"|"F", flag: "C", source: "add"|"sub"|"shift_left"|"shift_right", in: [a, b] }`

**消除**：~10 個 update_* ops 的 ARM/GB 雙份實作。

#### C. CB-prefix 系列合併（GB 4 個 → 1 個）

當前：`lr35902_cb_bit`, `lr35902_cb_bit_hl_mem`, `lr35902_cb_res`, `lr35902_cb_resset_hl_mem`, `lr35902_cb_set`, `lr35902_cb_shift`, `lr35902_cb_shift_hl_mem` — 7 個 op 都在做相同 pattern (operand source = reg or memory)。

提議：
- `bit_test { src: reg|memory, bit: <int> }`
- `bit_set { src: reg|memory, bit: <int> }`
- `bit_clear { src: reg|memory, bit: <int> }`
- `shift { variant: rlc|rrc|rl|rr|sla|sra|swap|srl, src: reg|memory }`

**消除**：7 個 → 4 個 generic ops。同 pattern 也適用其他有 bit-test
指令的 CPU。

#### D. Memory + IO 合併

當前：`load_byte`, `store_byte`, `store_word`, `lr35902_ldh_io_load`,
`lr35902_ldh_io_store` — IO 跟 mem 訪問用不同 op。

提議：spec 加 IO 區段宣告（`io_region: { base: 0xFF00, size: 0x80 }`），
通用 mem ops 自動依地址 dispatch。`ldh` 在 spec 端展開為
`add 0xFF00 + load_byte`。

**消除**：2 個 lr35902 IO ops。

#### E. Conditional execution 統一

當前：ARM 全指令 conditional (cond field 4 bits)、Thumb F16 用
`if_arm_cond`、GB 用 `lr35902_jp_cc / call_cc / ret_cc / jr_cc`。

提議：framework 加 `cond_check` op + 統一 `branch_cc` 樣板。GB 各
條件變體 = 同一個 `branch_cc { cond_field: ... , target: ... }`，
sub-encoding 配置在 spec 端。

**消除**：4 個 lr35902_*_cc ops。

### 3.2 重構後預估（aggressive 版）

| 檔案 | 重構前 | 重構後 |
|---|---:|---:|
| `Emitters.cs`（L0 完全通用） | 613 lines, 10 ops | ~700 lines, 22-25 ops |
| `Parametric/`（L1 spec-driven 通用） | n/a | ~1000 lines, 15-18 ops |
| `Idioms/`（L2 多變體 idiom 庫） | n/a | ~500 lines, 6-8 ops |
| `ArmIntrinsics.cs`（L3 ARM 真獨特） | 854 lines, 22 ops | **~150 lines, 3-5 ops** |
| `Lr35902Intrinsics.cs`（L3 GB 真獨特） | 2514 lines, 40 ops | **~200 lines, 4-6 ops** |
| `Resolvers/`（operand resolvers，pre-existing） | 480 lines | ~300 lines（簡化） |
| **總** | **~4000 lines C#** | **~2850 lines C#** |

**~30% 整體 code 減量**，但更重要的是：
- L3 部分從 **62 ops 降到 7-11 ops** = -85%
- 加新 CPU 的 L3 emitter 部分從 ~800-1000 lines 降到 **~150-200 lines**
- 大部分新 CPU 工作 = 寫 spec JSON + 配置 metadata，不需要碰 framework
  C# 程式碼

### 3.3 「真 L3」清單（重構後僅剩這些）

ARM7TDMI L3 (~3-5 ops):
- `arm_swi_swap_bank` — SWI/IRQ entry 連動 banked R8-R14 swap（spec 描述
  banked_per_mode 表，但「mode change → 真的 swap memory slots」這
  個動作本身是 framework 級行為，但對非 banking-CPU 是 no-op）
- `arm_ldm_rlist_zero_quirk` — ARMv4T documented bug，rlist=0 時 LDM
  載入 PC + writeback +0x40
- `arm_ldr_misaligned_rotate` — LDR 從非 word-aligned 地址讀取時
  rotated read 行為

LR35902 L3 (~4-6 ops):
- `lr35902_daa` — BCD decimal adjust
- `lr35902_ime_delay` — EI 設 IME=1 延遲 1 指令 (RETI 是即時的)
- `lr35902_pop_af_low_clear` — POP AF 強制清掉 F 的低 4 bit
- `lr35902_halt_bug` — HALT-without-pending-IRQ-and-IF-empty 重複下個指令
- (可選) `lr35902_stop` — STOP 指令的 button-wake 行為（test ROM 用不到）

這 7-11 個 op 才是「真的需要 hand-write C# 處理的 hardware quirk」。
其他全部從 spec 配置驅動。

---

## 4. 加新 CPU 的工作量對照

### 4.1 Before（現況）— 加 RISC-V RV32I

需要：
1. spec/riscv32/cpu.json + 5-10 個指令 group json (~3000 lines spec)
2. 寫 `Riscv32Emitters.cs` ~800-1000 lines 處理：
   - Status reg 不同（mstatus 跟 ARM CPSR 完全不同）
   - 每 instruction 都 read_reg(rd) + ALU + write_reg(rd) — 簡單 patterns
   - Exception entry (mtvec mechanism, 跟 ARM vector table 不同)
   - 這些都重寫一份 C#
3. ~1 週工作量

### 4.2 After（重構後）— 加 RISC-V RV32I

需要：
1. spec/riscv32/cpu.json + group jsons (~3000 lines spec) — 沒變
2. **L1 配置**：在 cpu.json 宣告 `mstatus` flag 位置 + `mtvec` exception 機制 → 通用 update_flag / raise_exception 自動跑
3. 寫 `Riscv32Intrinsics.cs` 只處理 RV32I 真 unique 的東西（其實
   RISC-V 設計很乾淨，可能 0-3 個 L3 ops，例如 ECALL / EBREAK / FENCE）
4. ~2-3 天工作量（加上 spec authoring）

**減少 ~70-80% 的 per-CPU C# 工作量**。

### 4.3 對照 ARM / GB 的「重構後檔案」應該長什麼樣

`ArmIntrinsics.cs`（後綴從 Emitters → Intrinsics 改名強調 L3 性質）：
```csharp
// ~150 lines total, 3-5 ops
internal sealed class ArmSwiBankSwapEmitter : IMicroOpEmitter { ... 50 lines ... }
internal sealed class ArmLdmRlistZeroQuirk : IMicroOpEmitter { ... 30 lines ... }
internal sealed class ArmLdrMisalignedRotate : IMicroOpEmitter { ... 30 lines ... }
```
其他 17 個原本 ARM-only 的 ops（update_nz、update_c_*、read_psr、
write_psr、raise_exception、if_arm_cond）全部搬到 `Parametric/` 由
spec metadata 驅動。

`Lr35902Intrinsics.cs`：
```csharp
// ~200 lines total, 4-6 ops
internal sealed class Lr35902DaaEmitter : IMicroOpEmitter { ... 60 lines ... }
internal sealed class Lr35902ImeDelay : IMicroOpEmitter { ... 30 lines ... }
internal sealed class Lr35902PopAfLowClear : IMicroOpEmitter { ... 20 lines ... }
internal sealed class Lr35902HaltBug : IMicroOpEmitter { ... 40 lines ... }
```
其他 35 個原本 GB-only 的 ops 全搬出去：CALL/RET/PUSH/POP/JP/JR/CB-bit/
LDH-IO/IME/SCF/CCF/CPL/ADD-HL-RR/ADD-SP/LD-HL-SP 全部 spec 配置驅動。

---

## 5. 實作 Phase 規劃

更新版（aggressive 通用化方向）：

| Step | 內容 | 工時估 | 風險 |
|---|---|---|---|
| **5.1** | **Register pair / push / pop / call / ret** — spec schema 加 `register_pairs` + `stack_register`，新 L1 ops `read_pair / write_pair / push / pop / call / ret`，重構 LR35902 + ARM 對應 ops | 4-6 hours | 低（pure add，舊 ops 暫保留 alias） |
| **5.2** | **Status flag generalisation** — spec-driven flag table 給 update_*，移除 ARM/GB 各自實作；新 L1 op `update_flag { reg, flag, source: nz_test\|carry_add\|carry_sub\|carry_shl\|... }` | 6-8 hours | 中（cover ARM N/Z/C/V + GB Z/N/H/C 兩套；jsmolka + Blargg 守底） |
| **5.3** | **Conditional execution / branch 統一** — `branch { addr_mode: absolute\|relative, cond: ... }`，吃下 GB JP/JR/CALL/RET 的 cc 變體 + ARM Thumb F16 if_arm_cond | 3-4 hours | 中（涉 8+ 個 GB op 的合併） |
| **5.4** | **Bit ops 統一** — `bit_test / bit_set / bit_clear { src: reg\|memory, bit: int }` + `shift { variant, src }` 取代 GB 7 個 CB-* ops | 2-3 hours | 低（GB-only） |
| **5.5** | **Memory IO 區段** — io_region 配置 + 通用 mem dispatch；移除 GB IO shortcut ops；ARM 也統一 IO/mem 路徑 | 3-4 hours | 中（GBA bus binding 要驗 IO 寫入時機正確） |
| **5.6** | **IME / interrupt master enable 統一** — `set_ime { value, delay: 0\|1 }` 配 spec ime_register/ime_bit；GB delay=1, ARM delay=0 | 2-3 hours | 低（pure add） |
| **5.7** | **Operand resolver 通用化** — ARM shift-by-reg 跟 LR35902 PC-rel 拆出 generic resolvers；CPU-specific resolvers 變薄 | 3-4 hours | 中 |
| **5.8** | **L3 清理 + intrinsic naming** — 把剩下 7-11 個 L3 ops 集中到兩個 `*Intrinsics.cs` 檔，加完整 docstring 解釋為什麼這個 op 不能 generalize | 2-3 hours | 低 |
| **5.9** | **第三 CPU 驗證** — 挑 RISC-V RV32I（spec 乾淨）或 MIPS R3000（多人熟悉），跑通 framework 通用化承諾 | 1 週 | 看 spec 寫多深，第三 CPU 上線時間是 framework 是否成功的最大證明 |

**5.1–5.8 合計 ~25-35 小時** = 4-5 全工作日。

5.9 第三 CPU 驗證額外 1 週（含 spec authoring）。

### 5.10 實作順序考量

優先 5.1 (register pair + call/ret) 因為：
- LR35902 那邊 ~10 個 ops 一次解掉，立刻 pay off
- 不影響 ARM 既有路徑（ARM 用 banked R8-R14 + STMFD/LDMFD，本來就有 push/pop 概念）
- 有助於後面所有 step 設計（很多其他 ops 都依賴 register pair 抽象）

5.2 (status flag) 是次優先 — 影響面廣，做好整個 framework 看起來
就「乾淨多了」。但需要 cover ARM 4-flag + GB 4-flag 兩套，要小心
不要打破既有 jsmolka/Blargg 結果。每個 update_* op 重構一個就跑全套
unit tests + loop100 守底。

---

## 6. 跟 Gemini 其他建議的關係

Gemini 提的 5 點：

1. **Inlining / 微指令融合** — 屬 SpecCompiler 階段優化，跟本文獨立。等 emitter 重構完再做（要先有「通用 ops」才能找 fusion pattern）。
2. **State Tracking / Lazy Flag** — 同上，屬 SpecCompiler 階段。Lazy flag 特別有效因為 ARM 條件執行讀 flag 頻繁。
3. ✅ **Architecture-specific 特質化** — 本文 L3 ArchIntrinsics 概念。**已採納**。
4. **編譯快取 + 熱點分析** — 屬 Phase 7 block-JIT 範圍。
5. **減少 extern binding** — 屬 Phase 7 範圍。

本文聚焦 **#3 + 結構面**，#1/#2/#4/#5 等 Phase 7 處理。

---

## 7. 為什麼先做這個 (在 Phase 7 之前)

1. **Phase 7 block-JIT 會嵌進 emitter 內**：如果現在 emitter 結構還很混亂（每 CPU 一檔），Phase 7 改動會跨檔散開不好維護
2. **第三 CPU 驗證的 cost 直接決定能不能做 third-CPU 驗證**：當前 ~1 週/CPU 太貴；重構後 2-3 天就可以加新 CPU
3. **這是「framework 通用化承諾」的最後一哩路**：spec 端已經做到「換 CPU = 換 JSON」，emitter 端還沒 — 重構完才真的兌現論點
4. **不影響 Phase 5.7 完工狀態**：345 unit tests + jsmolka/Blargg PASS 都不改變，純結構改善

---

## 8. 風險 + 後備

| 風險 | 緩解 |
|---|---|
| Status flag generalisation 漏 cover 某個 ARM/GB flag-update 邊角 | 全套 unit tests + jsmolka/Blargg + loop100 都要重跑 |
| Spec schema 改動破壞既有 spec | schema version 化，舊 spec 自動兼容 |
| 重構過程出新 bug | 每個 step 獨立 commit，方便 git bisect |
| 重構完跑 loop100 bench 數字變化 | 預期 ±5% 內（純結構，沒新增 work），有變化要釐清 |
| 重構完最後沒省到那麼多行 | 接受 — 結構清晰本身就有價值 |

---

## 9. 一句話總結

**把「每 CPU 一個 1000+ 行 emitter 檔」這個結構債還掉，讓 framework
真的兌現「換 CPU = 換 JSON」承諾。**

關鍵洞察：當前 ~62 個 arch-specific ops 中只有 **7-11 個是真 L3
hardware quirk**（DAA、IME delay、HALT bug、ARM banked register
swap、ARMv4T LDM rlist=0 quirk 等），其他 ~50+ 都是 CPU 通用功能
（CALL/RET/PUSH/POP/JP/JR/Bit-ops/IO-access/Flag-update）偽裝成
arch-specific。

預估 25-35 小時工作量（4-5 全工作日），emitter L3 部分 -85%
（62 ops → 7-11 ops），新 CPU 上線時間從 1 週縮到 2-3 天，第三
CPU 驗證後才算真正兌現「換 CPU = 換 JSON」論點。完成後再進
Phase 7 block-JIT 才合理。
