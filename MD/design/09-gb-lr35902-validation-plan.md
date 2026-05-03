# Phase 4.5：GB LR35902 移植驗證計畫 ✅ 完成 (2026-05-02)

> 用 [erspicu/AprGBemu](https://github.com/erspicu/AprGBemu) 當 reference
> implementation，把 Game Boy 的 Sharp LR35902 CPU 寫成 JSON spec、跑通
> 同樣的 host runtime，驗證 framework 真的可以「換 CPU 只要換 JSON」。
>
> **狀態**：4.5A/B/C 全部完成。LR35902 spec 涵蓋全 ISA（501 opcodes、
> 23 group files），跑通 Blargg `cpu_instrs` 11/11 + master "Passed all
> tests"，跟 LegacyCpu 截圖完全一致。原本「預期會擴充」的 framework
> 點（register pairs、F register 半進位、CB prefix dispatch、variable
> width fetch）全都在 SpecLoader / Emitters 端實作完成。
>
> 後續 emitter 結構優化（Phase 5.8 refactor）見
> `MD/design/11-emitter-library-refactor.md`。spec 結構見
> `MD/design/10-lr35902-bit-pattern-groups.md`。本檔保留為歷史計畫紀錄。

---

## 為什麼選 GB / LR35902 當第二顆 CPU

不是隨便挑的 — 是**故意選一個會逼出 framework 目前未驗證面**的 CPU。

| Framework 已有但未真正跑過的能力 | LR35902 會用到 | 6502 / Z80 會用到嗎 |
|---|---|---|
| 真正的 variable-width 解碼（1/2/3 bytes） | ✅ 是 | 6502 ✅、Z80 ✅ |
| Multi-instruction-set 切換（prefix opcode 進入另一空間） | ✅ 0xCB prefix → bit ops 空間 | 6502 ❌、Z80 ✅ (但複雜很多) |
| 8-bit GPR width（`GprWidthBits = 8` 路徑） | ✅ A/B/C/D/E/H/L 都是 8-bit | 6502 ✅、Z80 ✅ |
| Aliased / paired registers（兩個 8-bit 視為 1 個 16-bit） | ✅ BC/DE/HL/AF | 6502 ❌、Z80 ✅ |
| Status flag layout 在非 LSB（high nibble） | ✅ Z/N/H/C 在 F 的 bit 7,6,5,4 | 6502 部分、Z80 ✅ |
| Custom 算術 flag（half-carry） | ✅ H flag = bit 3→4 進位 | 6502 ❌、Z80 ✅ |

LR35902 命中所有六項；6502 太簡單（沒有 prefix opcode、沒有 paired
register），覆蓋面不足。Z80 大致也行，但 GB 模擬器（即 LR35902 = Z80 去
掉 IX/IY/shadow registers + 加 LDH/SWAP）規模適中、又有現成 reference
（AprGBemu）。

**額外好處**：AprGBemu 是「能跑」的 implementation，移植後可逐指令
diff 行為，比對人寫文件方便很多。

---

## 階段定位

**插入時機**：Phase 4 結束後、Phase 5 之前。

| Phase | 完成後狀態 |
|---|---|
| 3 | Host runtime + JIT 綁定 OK，能跑單條 IR 函式 |
| 4 | armwrestler ARM 模式全綠 — 證明 ARM 端正確 |
| **4.5** | **GB CPU spec 寫完 + 跑通 reference comparison** — 證明 framework 可換 CPU |
| 5 | 跑 GBA BIOS + ROM entry point |

**為什麼不更早**：要先有 host runtime（Phase 3）才能跑東西；要先 ARM 跑
得對（Phase 4）才能確認「跑不過」是 GB spec 的問題不是 framework 問題。

**為什麼不更晚**：等到 Phase 7（LLVM Block JIT）才發現 framework 對 GB
不通，重做成本太高。在 Phase 5（GBA-specific 工作）之前驗證通用性，可以
影響 Phase 5+ 的設計（例如 memory bus 介面是否該保留多 CPU 形狀）。

---

## 範圍與驗收

> **Note**：完整範圍與「驗收的硬度」（全 ISA vs Blargg 子集 vs 只跑開機
> 畫面），真的跑到 Phase 4.5 的時候再決定。下方是建議起點，不是承諾。

### 建議的最小可交付（MVP-of-Phase-4.5）

- [ ] `spec/lr35902/cpu.json` + `spec/lr35902/instructions/*.json`
  涵蓋 LR35902 主指令集（~245 個 main opcode）
- [ ] CB-prefix 子指令集（256 個 bit op）
- [ ] Host runtime 接 GB memory map（ROM bank 0/1、VRAM、WRAM、OAM、IO、
  HRAM、IE）— **暫時用 stub**，跑得起 CPU 即可
- [ ] 撈 AprGBemu 的 ROM 載入流程當參考
- [ ] **行為對照測試**：跑同一支 GB ROM，每 N 個 cycle dump CpuState，
  與 AprGBemu 並排 diff
  - 起點建議：Blargg `cpu_instrs/individual/01-special.gb`
  - 通過後再往 02 ~ 11 推

### 可選擴大範圍（看時間決定）

- [ ] 全部 11 個 Blargg cpu_instrs 子測試通過
- [ ] PPU stub（DMG mode、tile-based、不做 sprite priority）讓開機
  畫面出來
- [ ] Mooneye-gb test ROM 部分通過

---

## Framework 預期會擴充的地方

把 LR35902 寫進去的過程，預計會發現以下 framework 不夠用、需要擴充的
點。每一條都是**對 framework 通用性的真正驗證**。

### 1. Schema：register aliases / pairs

GB spec 必然會寫類似：

```json
{
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
    ]
  }
}
```

`register_file` 目前沒有 `register_pairs`，需要 schema 擴充 + Loader
讀 + 新 micro-op `read_reg_pair` / `write_reg_pair`（或 operand resolver
`paired_register`）。

### 2. Schema：F register 的特殊欄位

```json
{
  "name": "F",
  "width_bits": 8,
  "fields": {
    "Z": { "high": 7, "low": 7 },
    "N": { "high": 6, "low": 6 },
    "H": { "high": 5, "low": 5 },
    "C": { "high": 4, "low": 4 }
  },
  "low_nibble_zero": true
}
```

`low_nibble_zero` 之類的 invariant 目前沒有；可以選擇：
- 加 schema 欄位 + emitter 在每次 `write_psr` 時自動 mask
- 不加，spec 在每次 write 自己 mask（費事但不擴 framework）

### 3. Half-carry flag 計算

ARM 的 C/V 都已實作；GB 的 H flag（bit 3 → bit 4 carry）是新需求。
新 micro-op：

- `update_h_add` — H = ((a & 0xF) + (b & 0xF)) > 0xF
- `update_h_sub` — H = (a & 0xF) < (b & 0xF)
- `update_h_add16` — 16-bit 版（bit 11 → bit 12 carry，給 ADD HL,rr 用）

可以放在 `LR35902Emitters` class（仿照 `ArmEmitters`），不污染 generic 部分。

### 4. Multi-instruction-set 的 prefix dispatch

ARM/Thumb 切換是靠 `CPSR.T` bit 持久狀態。GB 0xCB 是「下一個 byte 屬於
另一個指令集」的瞬時切換。

`InstructionSetDispatch` 目前已支援 `selector` + `selector_values`，但
prefix-byte 模式可能要新增一種 `transition_rule`：

```json
{
  "instruction_set_dispatch": {
    "selector": "fetched_byte_0xCB",
    "transition_rule": "single_instruction_in_alt_set",
    "alt_set": "CB_PREFIX"
  }
}
```

或者更簡單：把 0xCB-prefix 視為「主指令集裡 opcode 0xCB 的指令會去執行
另一個 fetch + decode」，用現有 micro-op 組合表達。哪個比較乾淨待實作
時決定。

### 5. Variable-width 真正走通

ARM/Thumb 是「整個 instruction set 內固定寬度」；GB 是「同一個 set 內
opcode 決定 1/2/3 bytes」。`width_decision` schema 已預備但只跑過 ARM/
Thumb 的「另一個 set 切換」用法，沒跑過「同一個 set 內依 opcode 決定
總長度」。實作時可能要修 decoder。

---

## 移植工作流

建議照這個順序，每完成一塊就 commit：

1. **建 spec 骨架**：`spec/lr35902/cpu.json` 寫 architecture、
   register_file（含 paired register schema 擴充）、processor_modes
   （GB 沒有 mode 概念，這欄可省）
2. **8-bit Load 群** （AprGBemu CPU.cs 第 22 行起的 `#region 8bit Load`）
   — 最簡單，~50 個 opcode，先驗證 8-bit GPR 路徑能編出來
3. **16-bit Load 群** — 驗證 paired register 寫法
4. **8-bit ALU**（ADD/ADC/SUB/SBC/AND/OR/XOR/CP/INC/DEC）— 驗證 H flag
5. **16-bit ALU**（ADD HL,rr / INC rr / DEC rr）— 驗證 16-bit add/H flag
6. **Jump/Call/Return**（JP/JR/CALL/RET 含條件版）
7. **Misc**（CCF/SCF/CPL/DAA/STOP/HALT/DI/EI）
8. **0xCB prefix bit ops**（256 個，但全部是 BIT/RES/SET/RLC/RRC/RL/RR/
   SLA/SRA/SWAP/SRL × 8 暫存器，可大量重用 format）
9. **Host runtime 改造**：memory bus stub、register file mirror、跑通 NOP-loop
10. **行為對照測試**：跑 Blargg 01-special.gb，dump 並 diff

---

## 風險與決策點

| 風險 | 緩解 |
|---|---|
| Schema 擴充破壞 ARM 既有測試 | Phase 2.5 已有 159 測試 + CoverageTests，schema 改動全綠才合 merge |
| AprGBemu 行為本身不正確 | 起步用 Blargg test ROM 而不是 AprGBemu 直接對拍；AprGBemu 只當「我寫 spec 卡住的時候去翻 C# 怎麼寫」的工具書 |
| paired register schema 設計卡住 | 退路：spec 只宣告 8-bit GPR，paired 在 emitter 端用兩個 read_reg + or + shl 拼出來。乾淨度差但可動 |
| GB host runtime 比想像中肥 | 此 phase 不要求 PPU；CPU + memory stub 跑通即可，ROM 只用來驗證 CPU 行為 |
| 0xCB prefix dispatch 設計卡住 | 退路：把 0xCB 後的 byte 視為主指令集裡一個 256-entry 子 format，不引入 multi-set 機制 |

---

## 與 AprGBemu 的對應關係

| AprGBemu 檔案 | 本專案對應產出 |
|---|---|
| `Emu_GB/CPU.cs`（switch-case CPU） | `spec/lr35902/*.json`（資料化）|
| `Emu_GB/MEM.cs`（memory bus） | host runtime memory bus 實作（C#） |
| `Emu_GB/Define.cs`（暫存器宣告） | spec 的 `register_file` |
| `Emu_GB/INT.cs`（中斷） | host runtime interrupt loop |
| `Emu_GB/GPU.cs` / `SOUND.cs` / `JOYPAD.cs` | 不在本 phase 範圍 |

AprGBemu 是 **WinForms + 純直譯**；本專案目標是把它的 CPU 部分用 JSON
重新表達後，透過 LLVM IR + JIT 跑。對照組價值在於：兩邊跑完同一個 ROM
應該得到相同的 CpuState 序列。

---

## 完成標準

當以下都成立，宣告 Phase 4.5 完成：

1. ✅ `spec/lr35902/` 涵蓋 LR35902 主 + CB-prefix 全 ISA
2. ✅ Framework 端的 schema/parser/emitter 擴充已合入並通過全部 ARM 測試
3. ✅ Host runtime 能載入 GB ROM、跑 fetch-decode-execute 不 crash
4. ✅ 至少一個 Blargg cpu_instrs 子測試通過（範圍真到那邊再決定要幾個）
5. ✅ Coverage tests 對 LR35902 spec 也通過（vocabulary lint、mnemonic
   baseline）
6. ✅ 文件更新：本檔記錄實際遇到的擴充點、roadmap 標 Phase 4.5 完成

---

## 與後續 Phase 的銜接

完工後：
- Phase 5（GBA Memory Bus + BIOS）回到 ARM 主線
- Framework 從此被驗證為**真·通用**，後續所有設計決策都該保持「不污染
  ARM-specific 假設」
- 如果順利，未來想加第三顆 CPU（MIPS R3000、RISC-V RV32I 等）會輕鬆很多

---

## 4.5C：spec 結構設計

`LegacyCpu` 直譯（256-case big switch）走完並通過 Blargg `cpu_instrs`
11/11 後，要把同一份 ISA 用 bit-pattern 分群寫進 `spec/lr35902/`，產出
`JsonCpu` backend，跟 legacy 跑同一份 ROM 對照。具體分群表參見
[`10-lr35902-bit-pattern-groups.md`](./10-lr35902-bit-pattern-groups.md)。
