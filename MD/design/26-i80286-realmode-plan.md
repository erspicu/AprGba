# Phase 26 — Intel 80286 real-mode implementation plan

> **Status**: ready-to-execute (2026-05-10)
> **Parent design**: [23-cpu-spec-inheritance.md](23-cpu-spec-inheritance.md) +
> [25-i80186-implementation-plan.md](25-i80186-implementation-plan.md)
> **Predecessor**: Phase 25 (i80186 — completed `4ec2465`, depth-2 chain shipped)
> **Goal**: 把 Intel 80286 加入 framework — **real-mode only**。Protected
> mode 切到 Phase 27（多週工程，descriptor 表 + privilege check + TSS
> task switch + 整個新 exception 模型）。
>
> Real-mode 80286 = i80186 + 13 個 system 指令的 real-mode 半身。其中
> LMSW/SMSW 在 real-mode 還是有意義（單一 MSW 16-bit register 的
> read/write，sets PE bit 是進 protected mode 的入口）。本 phase 不
> 啟用 protected mode 行為，只 wire 指令進 spec + emit no-op-ish IR。
>
> **Inheritance demo 角度**：本 phase 驗證 chain depth = 3
> (i80286 → i80186 → i8086) — 25.1 shipped 的 depth cap = 4 invariant
> 在這裡實際被利用。

---

## 0. 前置盤點

### 0.1 已完成（Phase 24 + Phase 25 shipped）

- spec/x86-16/i8086/cpu.json — base spec, 149 instructions with stable IDs.
- spec/x86-16/i80186/cpu.json — extends i8086, +26 instructions, 1 silicon override.
- src/AprCpu.Core/IR/X86_16Emitters.cs — 8086 + 80186 emitters all wired.
- src/AprCpu.Core/JsonSpec/SpecLoader.cs — extends merge logic, depth-cap=4, cycle detect.
- 21/21 inheritance unit tests, T2 visual matrix 18+12 PNG SHA256 identical.

### 0.2 80286 real-mode 範圍

13 個 system instructions (Intel 80286 manual)：

| Mnemonic | Encoding | 功能（real mode） |
|---|---|---|
| LGDT m48 | 0F 01 /2 | Load GDTR (in real mode: silently store the 6-byte image, no enforcement) |
| LIDT m48 | 0F 01 /3 | Load IDTR — same |
| LLDT r/m16 | 0F 00 /2 | Load LDTR (no-op-ish in real mode) |
| SLDT r/m16 | 0F 00 /0 | Store LDTR |
| LMSW r/m16 | 0F 01 /6 | Load MSW; setting PE bit (bit 0) historically enters protected mode but we no-op the mode switch |
| SMSW r/m16 | 0F 01 /4 | Store MSW |
| LTR r/m16 | 0F 00 /3 | Load Task Register (no-op-ish) |
| STR r/m16 | 0F 00 /1 | Store Task Register |
| LAR r16, r/m16 | 0F 02 | Load access rights from selector (no-op in real mode) |
| LSL r16, r/m16 | 0F 03 | Load segment limit (no-op in real mode) |
| VERR r/m16 | 0F 00 /4 | Verify readable (no-op real) |
| VERW r/m16 | 0F 00 /5 | Verify writable (no-op real) |
| CLTS | 0F 06 | Clear Task Switched flag in MSW |
| ARPL r/m16, r16 | 0x63 | Adjust RPL — note: 0x63 was BOUND on 80186, repurposed in 80286 protected mode. **In real mode 80286, 0x63 is still BOUND** per Intel manual; ARPL only valid in protected mode. So we do NOT override BOUND for i80286-real. |

### 0.3 0F 兩位元組 opcode prefix infrastructure

8086/80186 的 decoder 全是 1-byte primary opcode。80286 引入 0x0F 作為 escape prefix：實際 opcode 是 0x0F + 第二個 byte（合稱 two-byte opcode）。

Framework 對 0x0F 的處理選擇：
- **Option A** (chosen)：BlockDetector 對 0x0F 識別為 prefix，length oracle 多算 1 byte，instruction word 的 LSB 是第二個 byte（mask 0xFF 配合 second byte match）。Decoder 透過 prefix sub-decoder（已存在的 `prefixSubDecoders` 機制，目前 LR35902 CB-prefix 用過）。
- **Option B**：把 0x0F XX 編成 16-bit `word` (0x0F << 8 | XX)，mask 0xFFFF / match 0x0FXX。需要擴 decoder 寬度。

Option A 跟既有 framework infrastructure 一致，少改動。

### 0.4 不在本 phase 範圍

- Protected mode 任何元素（descriptor / privilege / TSS / faults）→ Phase 27
- ARPL 跳到 Phase 27（real-mode 0x63 仍是 BOUND）
- CPUID-style feature detection（80386+）

---

## 1. Sprint 結構

```
Sprint 26.1  i80286 spec — extends i80186, +13 system instructions      [authoring]
Sprint 26.2  0F-prefix decoder infra — extend prefixSubDecoders for x86 [framework]
Sprint 26.3  System emitters (LGDT/LIDT/LMSW/SMSW/SLDT/STR/...)         [implementation]
Sprint 26.4  MSW status register — add to layout, init at reset         [state]
Sprint 26.5  CLI variant + tests + demo                                 [integration]
Sprint 26.6  Closure docs + perf note                                   [closure]
```

---

## Sprint 26.1 — i80286 spec authoring

### 26.1.1 — Directory + cpu.json

```
spec/x86-16/i80286/
├── cpu.json                              # extends i80186, declares MSW + GDTR/IDTR/LDTR/TR registers
└── groups/
    └── i80286-system.json                # 13 system instructions
```

cpu.json 結構：
```jsonc
{
  "architecture": {
    "id": "Intel80286",
    "family": "x86-16",
    "extends": "Intel80186",
    "extends_path": "../i80186/cpu.json",
    ...
  },
  "register_file_diff": {
    "additions": {
      "status": [
        { "name": "MSW", "width_bits": 16, "fields": { "PE": "0", "MP": "1", "EM": "2", "TS": "3" } },
        { "name": "GDTR_BASE", "width_bits": 32, "fields": {} },
        { "name": "GDTR_LIMIT", "width_bits": 16, "fields": {} },
        { "name": "IDTR_BASE", "width_bits": 32, "fields": {} },
        { "name": "IDTR_LIMIT", "width_bits": 16, "fields": {} },
        { "name": "LDTR", "width_bits": 16, "fields": {} },
        { "name": "TR", "width_bits": 16, "fields": {} }
      ]
    }
  },
  "instruction_set_diff": {
    "Main": {
      "additions": [
        { "$include": "groups/i80286-system.json" }
      ]
    }
  }
}
```

注意：`register_file_diff` 是 25.1 schema 預留但未實作的 feature。本 sprint 要把它接通（小範圍 — 只支援 status registers additions）。

### 26.1.2 — i80286-system.json

13 個 instruction entries。簡化版本（real mode only）：
- LMSW / SMSW pair：full implementation（MSW register 真的讀寫）
- LGDT / LIDT / SGDT / SIDT：store/load 6-byte image to dedicated state registers
- LLDT / SLDT / LTR / STR：簡化為單 16-bit r/w
- LAR / LSL / VERR / VERW：no-op stubs（real mode 行為不明確）
- CLTS：clear MSW.TS bit

### 26.1.3 — 估時：3-4 小時

---

## Sprint 26.2 — 0F-prefix decoder infrastructure

x86 跟 LR35902 CB-prefix 共享同 `prefixSubDecoders` 機制 — 看現有 LR35902 寫法，照樣做：
1. BlockDetector 認 0x0F 是 prefix，length += 1 (額外的第一 byte)。
2. 第二 byte 是真正的 opcode，丟給 sub-decoder。
3. spec 用 `instruction_set_dispatch.switch_via` 機制 declare 0x0F 是 prefix。

x86 已有 dedicated `_busLengthOracle` path（24.6.8a）。要擴：當第一 byte == 0x0F 時，length += what x86_16 sub-prefix length oracle says about second byte.

### 估時：4-5 小時

---

## Sprint 26.3 — System emitters

13 個 emitter（其中很多是 stub）：
- `x86_lmsw_rm16` / `x86_smsw_rm16` — full
- `x86_lgdt_m48` / `x86_lidt_m48` / `x86_sgdt_m48` / `x86_sidt_m48` — store/load 48-bit (16+32) value to dedicated regs
- `x86_lldt_rm16` / `x86_sldt_rm16` / `x86_ltr_rm16` / `x86_str_rm16` — single 16-bit r/w
- `x86_lar_r16_rm16` / `x86_lsl_r16_rm16` — no-op stubs (real mode only)
- `x86_verr_rm16` / `x86_verw_rm16` — no-op stubs
- `x86_clts` — clear MSW.TS bit

### 估時：5-6 小時

---

## Sprint 26.4 — MSW + GDTR/IDTR registers in CPU state

擴充 X86Memory's CPU state struct + Layout 定義：
- MSW (16 bit)
- GDTR_BASE (32 bit, but only low 24 in real mode — 80286 has 24-bit address space)
- GDTR_LIMIT (16 bit)
- 同上 IDTR
- LDTR / TR (16 bit each)

Reset：MSW=0xFFF0 per Intel manual (top 4 bits of MSW reset state).

### 估時：2-3 小時

---

## Sprint 26.5 — CLI variant + tests + demo

- `--variant=i80286 | i80286-real` (only real-mode supported in Phase 26)
- 寫 `26-msw.com` demo：SMSW → 把 MSW 值寫到 CGA → HLT。 Demo 截圖 = `result/x86-16/protmode-msr-i80286.png`（matches doc 24 §410）
- 跑 6 demo × 3 variant (i8086 / i80186 / i80286) SHA256 全 identical (no demo uses 286 ops)
- 寫 inheritance test：load i80286, verify chain depth = 3, MSW status reg present

### 估時：3-4 小時

---

## Sprint 26.6 — Closure docs

- `MD/performance/<時戳>-i80286-realmode.md`
- 更新 doc 24 24.8 row → ✅
- 更新 doc 23 chain depth validation note

### 估時：1 小時

---

## 2. Phase 26 vs Phase 27 邊界

Phase 26 (real mode) 完成後，i80286 backend 能跑：
- 全部 8086 / 80186 既有 ROM
- 13 個系統指令（real-mode 半身）

但 NOT 跑：
- Protected mode programs
- Descriptor-loading programs that expect proper segment limit checks
- Task switching

Phase 27 (protected mode) 才完成上述。預計 Phase 27 自己會分 5-8 個 sprints。

## 3. 預估時程

| Sprint | 估時 |
|---|---|
| 26.1 spec | 3-4h |
| 26.2 0F prefix | 4-5h |
| 26.3 emitters | 5-6h |
| 26.4 state | 2-3h |
| 26.5 CLI + tests | 3-4h |
| 26.6 docs | 1h |
| **總計** | **18-23 小時** (~3 工作天) |

## 4. 進度 status

| Sprint | Status | Commit | 完成日 |
|---|---|---|---|
| 26.1 i80286 spec | ✅ | `6b1e2d6` | 2026-05-10 |
| 26.2 0F prefix (length only) | ✅ partial | `089b108` | 2026-05-10 |
| 26.2b 0F decoder dispatch | ✅ | `19635de` | 2026-05-10 |
| 26.3 SMSW emitter (1st observable 286) | ✅ partial | `e5d023f` | 2026-05-10 |
| 26.4 State + MSW | ⏳ pending | — | — |
| 26.5 CLI + tests | ⏳ pending | — | — |
| 26.6 Docs | ⏳ pending | — | — |
