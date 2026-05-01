# Phase 4.5C：LR35902 bit-pattern 分群表

> Phase 4.5 已經完成 reference implementation（`LegacyCpu` 直譯 256 個
> opcode）並通過 Blargg `cpu_instrs` 11/11。Phase 4.5C 的工作是把同樣
> 的 ISA 用 bit-pattern 分群寫進 `spec/lr35902/`，沿用 ARM7TDMI 那一套
> SpecCompiler，產出 `JsonCpu` backend，再對同一份 ROM 取得相同結果。
>
> 本文件是該 spec 結構的設計依據。

---

## 為什麼分群而不是 256-case

`LegacyCpu.Step.cs` 有 1900+ 行、一個指令一個 case，這是 reference
implementation 的合理形狀（faithfulness > elegance，便於 bug-for-bug
對照）。但對 framework 的 JSON spec 不適合：

- 重複碼大量。`LD r,r'` 49 個 case、ALU `ADD/ADC/.../CP` 64 個 case，
  全部都在重新寫同一件事
- 修一個 bug 要動很多地方
- 無法重用 ARM7TDMI 已驗證的 SpecCompiler / 編碼決策路徑

LR35902 的編碼比 ARM 還規則，幾乎全部能塞進四個 block：

```
00 xxx xxx — block 0：misc / 16-bit ops / load imm / inc/dec / control
01 ddd sss — block 1：LD r, r'         （ddd=110/sss=110 是 HALT 例外）
10 ooo sss — block 2：ALU A, r         （ooo: ADD/ADC/SUB/SBC/AND/XOR/OR/CP）
11 xxx xxx — block 3：jump/call/ret/push/pop/ALU imm/RST/CB-prefix
```

光 block 1 + block 2 就能用兩條 pattern + 一張 reg lookup 表蓋掉
113 個 case。剩下 block 0 / block 3 各分數個子群再加幾個真·不規則的
特例。預估總 group 數 **~20 個**，比 ARM7TDMI 的 13 個 ARM group +
19 個 Thumb format 還精簡。

---

## Block 1：`01 ddd sss` LD r, r'

**Pattern**：`01_ddd_sss`，mask `0xC0`，match `0x40`，例外：`01_110_110`
（0x76）= HALT，要在 mask 中排除或在 instructions selector 中以 special
case 處理。

**reg index 對映**（`ddd` / `sss` 共用）：

| 編碼 | 暫存器 |
|---|---|
| 000 | B |
| 001 | C |
| 010 | D |
| 011 | E |
| 100 | H |
| 101 | L |
| 110 | (HL)（記憶體間接） |
| 111 | A |

**指令數**：`8 × 8 - 1 (HALT) = 63`，全部共用同一個 step list（`read_r8(sss)`
→ `write_r8(ddd)`），差別只在 reg lookup。

檔名：`spec/lr35902/groups/block1-ld-reg-reg.json`

---

## Block 2：`10 ooo sss` ALU A, r

**Pattern**：`10_ooo_sss`，mask `0xC0`，match `0x80`。

**ALU op 對映**（`ooo`）：

| 編碼 | 助記符 | 描述 |
|---|---|---|
| 000 | ADD A, r | A ← A + r |
| 001 | ADC A, r | A ← A + r + C |
| 010 | SUB r    | A ← A - r |
| 011 | SBC A, r | A ← A - r - C |
| 100 | AND r    | A ← A & r |
| 101 | XOR r    | A ← A ^ r |
| 110 | OR r     | A ← A \| r |
| 111 | CP r     | flags ← A - r（A 不寫回） |

`sss` 同 block 1 的 reg lookup（含 (HL) 與立即值在 block 3 的
`11_ooo_110`）。

**指令數**：8 × 8 = 64，共用 step list 形如 `alu_op8(A, src, op_kind)`，
差別只在 ALU op 與 source reg。Flag 設定也是表驅動（每個 op 對 Z/N/H/C
的影響規則固定）。

檔名：`spec/lr35902/groups/block2-alu-reg.json`

---

## Block 3：ALU A, imm8 — `11 ooo 110`

**Pattern**：`11_ooo_110`，mask `0xC7`，match `0xC6`。

跟 block 2 完全同一張 ALU op 表，只差 source 是 fetch 來的 imm8 而非
reg。理想情況下應該跟 block 2 共用 step list、只換 source operand
resolver — `operands.src.kind: "imm8"` vs `"reg8_by_field"`。

**指令數**：8。

檔名：`spec/lr35902/groups/block3-alu-imm8.json`

---

## Block 0：8-bit immediate load — `00 ddd 110`

**Pattern**：`00_ddd_110`，mask `0xC7`，match `0x06`。

`LD r, n`（含 `LD (HL), n`）。共 8 個。

檔名：`spec/lr35902/groups/block0-ld-r8-imm8.json`

---

## Block 0：8-bit INC / DEC — `00 ddd 10x`

**Pattern**：`00_ddd_10c`，c=0/1：

- `00_ddd_100` = INC r
- `00_ddd_101` = DEC r

共 16 個（含 (HL)）。INC 不影響 C flag、DEC 同。H flag 都要算。

檔名：`spec/lr35902/groups/block0-inc-dec-r8.json`

---

## Block 0：16-bit immediate load — `00 dd0 001`

**Pattern**：`00_dd0_001`，mask `0xCF`，match `0x01`。

| dd | 目標 |
|---|---|
| 00 | BC |
| 01 | DE |
| 10 | HL |
| 11 | SP |

`LD rr, nn`，4 個。Validates paired register schema。

檔名：`spec/lr35902/groups/block0-ld-rr-imm16.json`

---

## Block 0：16-bit INC / DEC / ADD HL,rr — `00 dd? 011/001`

三個小群，但 register field 編碼一致：

- `00_dd0_011` = INC rr     （4 個）
- `00_dd1_011` = DEC rr     （4 個）
- `00_dd1_001` = ADD HL, rr （4 個，影響 H/C，Z 不變、N=0）

檔名：`spec/lr35902/groups/block0-alu-rr.json`

---

## Block 3：條件 / 無條件 跳轉 — JP/JR/CALL/RET

**JP**：

- `1100_0011` = JP nn          （無條件）
- `11_0cc_010` = JP cc, nn     （條件，cc = NZ/Z/NC/C）
- `1110_1001` = JP HL

**JR**：

- `0001_1000` = JR e8          （無條件）
- `00_1cc_000` = JR cc, e8     （條件）

**CALL**：

- `1100_1101` = CALL nn
- `11_0cc_100` = CALL cc, nn

**RET / RETI**：

- `1100_1001` = RET
- `1101_1001` = RETI
- `11_0cc_000` = RET cc

條件位 `cc`（占 2-bit）對映：

| cc | flag |
|---|---|
| 00 | NZ (Z=0) |
| 01 | Z  (Z=1) |
| 10 | NC (C=0) |
| 11 | C  (C=1) |

合理拆法：每個動詞一個 group（`block3-jp.json` / `block3-jr.json` /
`block3-call.json` / `block3-ret.json`），條件版跟無條件版共用 step list、
selector 區分。

---

## Block 3：PUSH / POP — `11 qq0 0p1`

**Pattern**：`11_qq0_0p1`，p=1=PUSH、p=0=POP（要再 verify 編碼方向）。

| qq | 對  |
|---|---|
| 00 | BC |
| 01 | DE |
| 10 | HL |
| 11 | AF |

POP AF 要 mask 掉 F 的低 4 bit（GB 的 F register 強制低 nibble = 0）。

共 8 個。檔名：`spec/lr35902/groups/block3-push-pop.json`

---

## Block 3：RST — `11 ttt 111`

**Pattern**：`11_ttt_111`，mask `0xC7`，match `0xC7`。

`RST` 跳轉到 `ttt × 8` = 0x00 / 0x08 / 0x10 / 0x18 / 0x20 / 0x28 /
0x30 / 0x38。共 8 個。

檔名：`spec/lr35902/groups/block3-rst.json`

---

## CB-prefix：bit-manipulation

opcode 0xCB 是 prefix — 下一個 fetch 來的 byte 在另一個 256-entry 空間。
這是整個 GB ISA 唯一一處需要 multi-instruction-set 機制（或等價的
"prefix-byte 進入子格式" 設計）。

**CB byte 結構**：`oo_bbb_sss`

| oo  | 動作 |
|---|---|
| 00 | shift/rotate（再用 bbb 區分 8 種） |
| 01 | BIT b, r — Z = !(r >> b & 1) |
| 10 | RES b, r — r ← r & ~(1 << b) |
| 11 | SET b, r — r ← r \| (1 << b) |

shift/rotate 細分（`oo=00`，bbb 編碼 op）：

| bbb | op |
|---|---|
| 000 | RLC |
| 001 | RRC |
| 010 | RL  |
| 011 | RR  |
| 100 | SLA |
| 101 | SRA |
| 110 | SWAP |
| 111 | SRL |

`sss` 跟 main set 同樣用 reg lookup（含 (HL)）。

**Group 拆法**：

- `cb-prefix-shift.json` — CB 00 ooo sss（64 個）
- `cb-prefix-bit.json`   — CB 01 bbb sss（64 個）
- `cb-prefix-res.json`   — CB 10 bbb sss（64 個）
- `cb-prefix-set.json`   — CB 11 bbb sss（64 個）

整個 CB 256-byte 空間 = 4 個 group × 64 entries。

---

## 真正不規則的（"irregular" group）

下列 opcode 不符合任何上面的 pattern，獨立列：

| opcode | 助記符 | 註 |
|---|---|---|
| 0x00 | NOP | |
| 0x07 | RLCA | block 0 的 `00_xxx_111` 那一條，但 op 變化太多，獨立列乾淨 |
| 0x0F | RRCA | 同上 |
| 0x17 | RLA  | 同上 |
| 0x1F | RRA  | 同上 |
| 0x27 | DAA  | half-carry/N/C 邏輯特殊 |
| 0x2F | CPL  | A ← ~A |
| 0x37 | SCF  | C ← 1 |
| 0x3F | CCF  | C ← !C |
| 0x10 | STOP | 兩 byte（後面跟 0x00） |
| 0x76 | HALT | block 1 的 `01_110_110` 例外 |
| 0xF3 | DI   | |
| 0xFB | EI   | 延遲一條指令 |
| 0xE0 | LDH (n), A     | (0xFF00 + n) ← A |
| 0xF0 | LDH A, (n)     | A ← (0xFF00 + n) |
| 0xE2 | LD (C), A      | (0xFF00 + C) ← A |
| 0xF2 | LD A, (C)      | A ← (0xFF00 + C) |
| 0xEA | LD (nn), A     | |
| 0xFA | LD A, (nn)     | |
| 0x08 | LD (nn), SP    | |
| 0xF8 | LD HL, SP+e8   | flags 影響特殊 |
| 0xF9 | LD SP, HL      | |
| 0xE8 | ADD SP, e8     | flags 影響特殊 |
| 0x02 | LD (BC), A     | |
| 0x12 | LD (DE), A     | |
| 0x22 | LD (HL+), A    | post-increment |
| 0x32 | LD (HL-), A    | post-decrement |
| 0x0A | LD A, (BC)     | |
| 0x1A | LD A, (DE)     | |
| 0x2A | LD A, (HL+)    | |
| 0x3A | LD A, (HL-)    | |

共 **~30 個** irregular opcode。可考慮：

- 全部塞 `irregular.json` 一個 group（30 個 instruction、各自 pattern）
- 或拆兩三個小 group：`misc-control.json`（NOP/STOP/HALT/DI/EI/DAA/CPL/...）、
  `ldh-and-mem.json`（高位記憶體存取）、`stack-arith.json`（SP 相關）

---

## 最終檔案結構提案

```
spec/lr35902/
  cpu.json                          — top-level：register_file (含 paired)、
                                       processor_modes（GB 沒 mode → 省）、
                                       instruction_set_dispatch（CB prefix）
  groups/
    block0-ld-r8-imm8.json          — 8 條
    block0-ld-rr-imm16.json         — 4 條
    block0-inc-dec-r8.json          — 16 條
    block0-alu-rr.json              — 12 條（INC/DEC/ADD HL,rr）
    block0-misc-control.json        — NOP/RLCA/RRCA/RLA/RRA/DAA/CPL/SCF/CCF/STOP（10 條）
    block0-mem-indirect.json        — LD (BC)/(DE)/(HL+)/(HL-) ↔ A（8 條）
    block0-ld-nn-sp.json            — 1 條
    block1-ld-reg-reg.json          — 63 條（HALT 排除）
    block1-halt.json                — 1 條（含 HALT bug 註記）
    block2-alu-reg.json             — 64 條
    block3-alu-imm8.json            — 8 條
    block3-jp.json                  — 6 條
    block3-jr.json                  — 5 條
    block3-call.json                — 5 條
    block3-ret.json                 — 7 條（RET/RETI/RET cc）
    block3-push-pop.json            — 8 條
    block3-rst.json                 — 8 條
    block3-ldh-mem-high.json        — LDH (n)/(C) ↔ A、LD (nn) ↔ A（6 條）
    block3-stack-arith.json         — LD HL,SP+e8 / LD SP,HL / ADD SP,e8（3 條）
    block3-di-ei.json               — 2 條
    cb-prefix-shift.json            — 64 條
    cb-prefix-bit.json              — 64 條
    cb-prefix-res.json              — 64 條
    cb-prefix-set.json              — 64 條
```

**總計**：~24 個 group 檔，~ 506 instruction entries（main 256 + CB 256，
扣除 11 個未使用 opcode）。比起 1900 行 big-switch，spec 體積估約
40-60% 視 step list 重用程度。

---

## 與 ARM7TDMI groups 的對照

| 維度 | ARM7TDMI | LR35902 |
|---|---|---|
| 指令集數 | 2（ARM + Thumb） | 2（main + CB） |
| 切換機制 | CPSR.T 持久 state | 0xCB prefix byte（瞬時） |
| Group 數 | 13 ARM + 19 Thumb = 32 | ~24 |
| 編碼正交性 | ARM 高、Thumb 中 | 非常高（block 1/2 接近 100%） |
| Operand resolver kinds | shifted_register、imm_rotated、... | reg8_by_field、imm8、imm16、reg_pair_by_field、... |
| Flag micro-ops | update_nzcv、update_carry_from_shifter | update_znhc_add、update_znhc_sub、update_h_add16、... |
| Half-carry | N/A | 新增需求（micro-op 或 emitter helper） |

---

## 預期會擴充 framework 的點（彙整自 09 + 本次設計）

1. **`register_file.register_pairs`** — schema + Loader + 新 micro-op
   `read_reg_pair` / `write_reg_pair`
2. **`F register low_nibble_zero` invariant** — schema 欄位 + write 時自動
   mask
3. **Half-carry micro-ops** — `update_h_add` / `update_h_sub` /
   `update_h_add16`，放在 `Lr35902Emitters`（仿 `ArmEmitters`）
4. **Prefix-byte instruction-set transition** — `0xCB` 在 main set 觸發
   單條指令切到 CB set。退路：把 CB 視為 main set 的 256-entry 子格式
5. **Variable-width 同集合內** — main set 內 1/2/3 byte 的 `width_decision`
   實際走通

---

## 進度

| 步驟 | 狀態 |
|---|---|
| 1. `cpu.json` 骨架（register_file 含 pairs、F/SP/PC、5 IRQ vectors、Main+CB dispatch） | ✅ |
| 2. block 1（LD r,r' + HALT，3 formats，priority 對 0x76 處理正確） | ✅ |
| 2. block 2（ALU A,r，2 formats，64 instructions，(HL)/reg cycle 區分） | ✅ |
| 3. block 0 全部（ld_r8_imm8 / inc_dec_r8 / ld_rr_imm16 / alu_rr / mem_indirect / misc_control / jr / ld_nn_sp） | ✅ |
| 4. block 3 全部（jp / call / ret / rst / push_pop / ldh_mem_high / stack_arith / di_ei / cb_prefix / alu_imm8） | ✅ |
| 5. CB-prefix 4 群（shift / bit / res / set，全 256 entries） | ✅ |
| **JSON 端完成**：245 main + 256 CB = **501 個 opcode 全部 decode 通過** | ✅ |
| 6. IR emitters 把 `lr35902_*` micro-ops 翻成 LLVM IR | ⏳ 下一輪 |
| 7. `JsonCpu` backend 串通 → 跑 Blargg `01-special.gb` 對照 `LegacyCpu` | ⏳ |
| 8. 全 11 子測試對照 → Phase 4.5C 完工 | ⏳ |

## 實際檔案結構（與原計畫一致）

```
spec/lr35902/
  cpu.json
  main.json
  cb.json
  groups/
    block0-misc-control.json     — NOP/STOP/RLCA/RRCA/RLA/RRA/DAA/CPL/SCF/CCF（10 條）
    block0-jr.json               — JR e8 + 4 條 JR cc,e8（5 條）
    block0-ld-nn-sp.json         — LD (nn),SP（1 條）
    block0-mem-indirect.json     — LD (BC)/(DE)/(HL+)/(HL-) ↔ A（8 條）
    block0-ld-rr-imm16.json      — LD rr,nn（4 條）
    block0-alu-rr.json           — INC rr / DEC rr / ADD HL,rr（12 條）
    block0-inc-dec-r8.json       — INC r / DEC r（16 條）
    block0-ld-r8-imm8.json       — LD r,n（8 條）
    block1-halt.json             — HALT（1 條，priority 高於 LD r,r'）
    block1-ld-reg-reg.json       — LD r,r'（63 條，3 formats with (HL) split）
    block2-alu-reg.json          — ALU A,r（64 條，2 formats with (HL) split）
    block3-jp.json               — JP nn / JP cc nn / JP HL（6 條）
    block3-call.json             — CALL nn / CALL cc nn（5 條）
    block3-ret.json              — RET / RETI / RET cc（6 條）
    block3-rst.json              — RST t（8 條）
    block3-push-pop.json         — PUSH rr / POP rr（8 條）
    block3-ldh-mem-high.json     — LDH/LD ↔ A（6 條）
    block3-stack-arith.json      — ADD SP,e8 / LD HL,SP+e8 / LD SP,HL（3 條）
    block3-di-ei.json            — DI / EI（2 條）
    block3-cb-prefix.json        — 0xCB prefix dispatch（1 條）
    block3-alu-imm8.json         — ALU A,n（8 條）
    cb-prefix-shift.json         — RLC/RRC/RL/RR/SLA/SRA/SWAP/SRL × 8 sources（64 條）
    cb-prefix-bit.json           — BIT b,r（64 條，single instruction with bbb/sss）
    cb-prefix-res.json           — RES b,r（64 條）
    cb-prefix-set.json           — SET b,r（64 條）
```

**總計**：23 個 group 檔，~120 個 instruction definition（同個 instruction 在 selector
不同時算多個），覆蓋全 ISA 245 main + 256 CB = **501 個 opcode**。
比 1900 行的 `LegacyCpu.Step.cs` 體積大幅壓縮。

## 與設計文件的差異

實作過程相對於原計畫 ([10-...md] 上半段) 的小調整：

1. **F register 不在 GPR file**：原本 `general_purpose.names` 含 F，後來改成只放
   A/B/C/D/E/H/L 7 個（GPR count = 7）。F 在 status，跟 SP/PC 一起。register_pairs
   裡 `AF.high="A"/low="F"` 仍然成立 — 各個 emitter 看名字解析來源，不限定 high/low
   要在同一個 section。
2. **SP/PC 是 16-bit 的 status register**：避免擴 schema 引入新的 special-register
   section，現階段把 16-bit 暫存器塞 status，欄位空。framework 不會自動把它當 PC，
   由 host runtime 處理 — 跟 ARM 的 R15 走 GPR 不同的設計分支。
3. **`width_bits: 8` for main set**：原本想用 variable-width，後來發現 LR35902 decode
   只需要 first byte，多餘的 byte 是 execution-time fetch。這讓 DecoderTable 不用支援
   variable-width。
