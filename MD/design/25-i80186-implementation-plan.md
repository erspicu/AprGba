# Phase 25 — Intel 80186 implementation plan

> **Status**: in-progress (2026-05-10)
> **Parent design**: [23-cpu-spec-inheritance.md](23-cpu-spec-inheritance.md) (DRAFT v2, Gemini-reviewed)
> **Predecessor**: Phase 24 (8086 — completed, three-backend parity, 218 MIPS block-JIT)
> **Goal**: 把 Intel 80186 加入 framework，**主要 demo target 是 spec inheritance + override 機制本身**。
>
> 80186 vs 8086 ISA 共通度約 96% — 只新增 12 個 opcode + 兩個 silicon
> quirk override（PUSH SP / shift mask）。是 inheritance 機制的最佳首次
> validation case：spec 預期 ~250 行 diff（不含繼承的 ~3000 行 8086 spec）。

## 進度 status

| Sprint | Status | Commit | 完成日 |
|---|---|---|---|
| 25.1 Inheritance infra | ✅ | `585c6b2` | 2026-05-10 |
| 25.2 ID retrofit (8086) | ✅ | `5d64c00` | 2026-05-10 |
| 25.3 i80186 spec | ✅ | `a201d82` | 2026-05-10 |
| 25.4 Emitters | ✅ | `8f2f1c7` | 2026-05-10 |
| 25.5 CLI wiring | ⏳ pending | — | — |
| 25.6 Tests + demos | ⏳ pending | — | — |
| 25.7 Docs + push | ⏳ pending | — | — |

**Sprint 25.1 deliverables**:
- `src/AprCpu.Core/JsonSpec/JsonMergePatch.cs` (RFC 7386 helper, ~80 行)
- `src/AprCpu.Core/JsonSpec/SpecModel.cs` 加 `Architecture.ExtendsPath`、`InstructionDef.Id` / `OriginCpu` / `OverriddenBy`、`InstructionSetDiff` + `PerSetDiff` records
- `src/AprCpu.Core/JsonSpec/SpecLoader.cs` 加 `LoadCpuSpecInternal` recursive resolver (depth cap 4 + cycle detect) + `ApplyInstructionSetDiff` + `ApplyDiffToSet` (additions / overrides via JsonMergePatch / removals) + `TagInstructionsWithOrigin` provenance
- `src/AprCpu.Tests/JsonMergePatchTests.cs` (13 tests, RFC 7386 conformance + spec-inheritance-specific cases)
- `src/AprCpu.Tests/SpecInheritanceTests.cs` (7 tests, end-to-end via temp tiny CPU specs)
- T2 18 PNG SHA256 identical = 既有 i8086 spec 完全 backwards-compat
- T1 838 tests background 跑中

---

## 0. 前置盤點 — 現有狀態

### 0.1 已完成（24.x 留下的）

- `spec/x86-16/i8086/cpu.json`：3000+ 行 spec，6 個 group files。
- `src/AprCpu.Core/IR/X86_16Emitters.cs`：~3700 行，~120 個 emitter，覆蓋 8086 全部標準指令。
- 三個 backend（legacy / json / json-block）+ 24.6.8d/e perf 優化（27 → 218 MIPS）。
- 6 個 demo ROM、Tom Harte 8088 SST 全綠、T1 838 unit tests 全綠、T2 18-PNG matrix。

### 0.2 半成品（要先補完才能 demo inheritance）

| 項目 | 狀態 | 補完成本 |
|---|---|---|
| `extends` field 存在於 schema/model | ✓ parse 進來但**不會 merge** | sprint 25.1 補 |
| `register_file` / `instruction_sets` 等繼承欄位 | 模型有，loader 不繼承 | sprint 25.1 補 |
| 每條 instruction 的 stable ID（`"id": "MUL_rm16"` 等） | ✗ 目前 instruction 用 mnemonic + format 識別，無唯一 ID | sprint 25.2 補（需 audit 8086 既有 spec 全加 ID） |
| `instruction_set_diff` schema 支援（additions / overrides / removals） | ✗ | sprint 25.1 補 |
| `traits` schema 支援 | ✗ | **不在本 phase 範圍**（80186 用不到 trait） |
| Inheritance depth ≤ 4 invariant | ✗ | sprint 25.1 補 |
| Schema validator 對 inheritance 規則的 hard-fail 檢查 | ✗ | sprint 25.1 補 |

### 0.3 不在本 phase 範圍

- 80286（保留到 phase 26+）
- protected mode 任何元素
- traits 機制（80186 不需要 FPU 這類 trait — 80187 是 80287 之後才出且罕見）
- 跨 family 機制（x86-32 起新 chain — phase 27+）

---

## 1. Sprint 結構（4 個 sprint，可平行/序列）

```
Sprint 25.1  Inheritance infra (extends merge logic)         [foundation]
Sprint 25.2  8086 spec retrofit — 加 stable ID               [foundation, blocks 25.3]
Sprint 25.3  i80186 spec — diff against i8086                [demo, blocks 25.4]
Sprint 25.4  Emitter additions + behavior overrides          [implementation]
Sprint 25.5  Backend wiring + CLI variant select             [integration]
Sprint 25.6  Tests, demos, Tom Harte SST filter              [validation]
Sprint 25.7  Documentation + commit/push                     [closure]
```

依賴：`25.1 → 25.2 → 25.3 → 25.4 → 25.5 → 25.6 → 25.7`。可平行的：25.1 跟 25.2（後者只依賴 schema 設計，不必等 25.1 merge logic 完成）。

---

## Sprint 25.1 — Inheritance infrastructure

> **目標**：SpecLoader 真的去找 parent spec 並 merge。完成後既有 spec
> 行為不變（沒 extends），新 spec 用 extends 能正確產出 merged spec。

### 25.1.1 — 設計 group-level merge semantics（紙上）

**Files**: `MD/design/25-i80186-implementation-plan.md`（本文）+ 可能擴充 `23-cpu-spec-inheritance.md`

**內容**：
- Group file 是否需要 `id`？答：**否**。Group 只是組織檔，實際 merge 看內部 instruction 的 `id`。
- 相同 group filename 在 child / parent 都出現怎麼處理？答：**不應該**。Child 的 group 都該放在 child 自己的 directory。merge 時 child 的 group 是 additions（add 進 instruction set）。
- Override 細節要 RFC 7386 (JSON Merge Patch)：`steps` 整段 replace（dict 內部不 merge）；`cycles` 物件內 merge 子欄位（`form` / `extra` 各自獨立）。
- `removals` 用 ID list — 用於 80186 把 8086 的 POP CS（`POP_CS`，0x0F）從 instr set 拿掉（80186 把 0x0F 改成 escape prefix 為以後 286 留位）。

**輸出**：本 phase doc 末段「Override merge rules」一節定稿。

**估**：~30 分鐘設計；與 doc 23 對齊。

### 25.1.2 — Schema 擴充

**Files**: `spec/schema/cpu-spec.schema.json`

- Architecture: `"extends": <string|null>`（parent CPU id；不再強制 null）
- 加 `instruction_set_diff` schema：
  ```jsonc
  "instruction_set_diff": {
    "type": "object",
    "additionalProperties": {     // key = set name (e.g. "Main")
      "type": "object",
      "properties": {
        "additions": { "type": "array", "items": {...group/include...} },
        "overrides": { "type": "object", "additionalProperties": {...partial-instruction...} },
        "removals":  { "type": "array", "items": {"type": "string"} }
      }
    }
  }
  ```
- 每個 instruction 加 `"id": <string>` 必填（spec-wide unique）。
- 加 `register_file_diff`、`status_register_diff`、`exception_vectors_diff` schemas（80186 用不到，但留 schema 給 286+）。

**Acceptance**: schema validator 對 i80186 草稿不報錯；對 i8086（無 extends）仍 pass。

**QA Tier**: 1（schema 改不直接動 runtime，但 unit test 必跑）。

**估**: 1-2 小時。

### 25.1.3 — SpecLoader 實作 merge 演算法

**Files**: `src/AprCpu.Core/JsonSpec/SpecLoader.cs`

實作 doc 23 §2.3 的 algorithm：
1. `LoadCpuSpec(path)`: parse → 看 `architecture.extends` → 若非 null，先 `LoadCpuSpec(parentPath)` → 拿 merged parent → 對 child 套 diff。
2. `ApplyInstructionSetDiff(parent, diff)`：
   - `additions`: 逐個 instruction add；ID 衝突 → throw `SpecValidationException`。
   - `overrides`: 逐個 (id, partialJson) 對 parent instruction 套 RFC 7386 merge patch。Parent 沒 ID → throw。
   - `removals`: 逐個 ID remove from parent set。Parent 沒 ID → throw。
3. Cycle detection: 維護 `loaded_set: HashSet<string>` 防 A → B → A。
4. Depth cap: chain depth > 4 → throw。
5. JSON Merge Patch (RFC 7386): 寫一個 helper `MergePatch(JsonElement target, JsonElement patch) → JsonElement`。

**Files**:
- 新增 `src/AprCpu.Core/JsonSpec/JsonMergePatch.cs`（pure function）。
- 修改 `src/AprCpu.Core/JsonSpec/SpecLoader.cs`：
  - 加 `LoadCpuSpec` 的 recursive variant。
  - 加 `ApplyDiff` / `ApplyOverrides` / `ApplyAdditions` / `ApplyRemovals` 內部 method。

**Acceptance**:
- 既有 4 個 CPU spec（無 extends）載入結果 byte-identical（除 `instructions` 多 `id` 後的 model 表示有差）。
- 新增單元測試覆蓋：
  - 純 additions chain
  - 純 overrides（含 cycle / steps / mnemonic 各層 partial merge）
  - 純 removals
  - Cyclic A→B→A 觸發 SpecValidationException
  - Depth > 4 觸發 SpecValidationException
  - Override 不存在的 ID 觸發 SpecValidationException
  - Removal 不存在的 ID 觸發 SpecValidationException

**QA Tier**: 2（runtime path 改，但純 load-time、不影響 emitter）。T1 838 tests 全綠是 gate。

**估**: 4-6 小時（含寫測試）。

### 25.1.4 — Provenance metadata（debug aid）

**Files**: `src/AprCpu.Core/JsonSpec/SpecModel.cs`

每個 merged instruction 加：
- `OriginCpu: string` — 最初定義的 CPU id（最深的 base）
- `OverriddenBy: string?` — 若被 override，最後 override 的 CPU id

debugger / spec dump 用。

**估**: 1 小時。

### 25.1.5 — Sprint 25.1 完成 gate

```
✓ schema 接受 extends + diff
✓ SpecLoader 跑 i8086（無 extends）跟既有 spec model 一致
✓ 新 unit tests pass
✓ T1 全 838 tests 全綠（無 regression）
✓ T2 x86 6-demo × 3-backend 18 PNG SHA256 全 identical（驗證 i8086 仍 work）
→ commit + push
```

---

## Sprint 25.2 — 8086 spec retrofit (加 stable ID)

> **目標**：給 8086 既有 ~120 個 instruction 各加唯一 `"id"`，作為
> override target 的 anchor。

### 25.2.1 — ID 命名 convention

**Convention**: `<MNEMONIC>_<OPERAND_SHAPE>` 全大寫底線：
- `MOV_r16_imm16` (0xB8-0xBF)
- `MOV_AL_moffs8` (0xA0)
- `ADD_rm8_r8` (0x00)
- `ADD_AL_imm8` (0x04)
- `IMUL_rm16` (0xF7 /5)
- `PUSH_SP` (0x54) — single-register PUSH 用 reg 名（這個就是要 override 的對象）
- `INT_3` (0xCC) — 寫死的 int 用 immediate
- `INT_imm8` (0xCD)
- group-byte（F6/F7/FE/FF/D0-D3/80-83）的 sub-mnemonic 用 `_GRP<reg>`：例如 `NEG_rm16` (`F7 /3`)

**Files**: 新增 `MD/design/25.2-instruction-id-conventions.md`（短 doc，~80 行）。

**估**: 1 小時。

### 25.2.2 — 自動掃描補 ID

**Files**: `tools/spec_id_audit.py`（新增）

寫個 Python 腳本：
1. 掃描 `spec/x86-16/i8086/groups/*.json` 每個 instruction
2. 從 mnemonic + 一組 fields 組出 candidate ID
3. 列出衝突（兩個 instruction map 到同 ID）
4. dump 到 `temp/i8086-id-proposals.json` 給人看

人工 review、調整 → 把 ID 寫回 spec。

**估**: 2-3 小時（腳本 1 小時 + 人工 review 1-2 小時）。

### 25.2.3 — Spec 驗證 pass

**Files**: `spec/schema/cpu-spec.schema.json`

加 `id` 為 instruction required field。SpecLoader 在 load time hard-fail 沒 ID 的 instruction（per Gemini critique）。

**Acceptance**:
- T1 838 tests 全綠
- T2 18 PNG 仍 SHA256 identical
- 新增 unit test：load i8086 → 驗證 ID 全 unique + 全非 null

**QA Tier**: 1。

**估**: 1 小時。

### 25.2.4 — Sprint 25.2 commit + push

---

## Sprint 25.3 — i80186 spec authoring

> **目標**：寫出 80186 的 spec — 全 diff 形式，預期 ~250 行 JSON。

### 25.3.1 — i80186 directory structure

```
spec/x86-16/i80186/
├── cpu.json                              # extends i8086, ~80 lines
└── groups/
    ├── i80186-additions.json             # 12 new instructions, ~150 lines
    └── i80186-overrides.json             # 2 overrides (PUSH SP, shift mask), ~30 lines
```

**估**: 5 分鐘。

### 25.3.2 — `cpu.json` — 架構繼承宣告

```jsonc
{
  "$schema": "../../schema/cpu-spec.schema.json",
  "spec_version": "1.0",
  "architecture": {
    "id":             "Intel80186",
    "family":         "x86-16",
    "extends":        "Intel8086",       // ← key change
    "endianness":     "little",
    "word_size_bits": 16
  },
  "variants": [
    { "id": "i80186", "core": "Intel80186", "features": [], "notes": "Intel 80186 (1982) — 8086 + 12 new opcodes + silicon-quirk fixes (PUSH SP / shift mask)." },
    { "id": "i80188", "core": "Intel80186", "features": [], "notes": "8-bit external bus variant; ISA-identical." }
  ],
  // register_file / status / exception_vectors / isa_metadata 全繼承
  "instruction_set_diff": {
    "Main": {
      "additions": [
        { "$include": "groups/i80186-additions.json" }
      ],
      "overrides": {
        "$include": "groups/i80186-overrides.json"
      }
      // removals: 暫無（PUSH CS/POP CS 在 8086 仍 valid，80186 才開始 reserve 0x0F；
      //         若我們 8086 spec 沒寫 POP CS 就不需 remove）
    }
  }
}
```

**估**: 30 分鐘（含校對 schema）。

### 25.3.3 — `i80186-additions.json` — 12 新指令

依 Intel iAPX 186 manual：

| ID | Opcode | 說明 |
|---|---|---|
| `PUSH_imm8`     | 0x6A           | sign-extend imm8 to 16, push |
| `PUSH_imm16`    | 0x68           | push imm16 |
| `PUSHA`         | 0x60           | push AX/CX/DX/BX/orig SP/BP/SI/DI |
| `POPA`          | 0x61           | reverse of PUSHA (skip stored SP) |
| `BOUND_r16_m16` | 0x62           | check r16 vs [m16,m16+2]; INT 5 if out of range |
| `IMUL_r16_rm16_imm8`  | 0x6B    | sign-extend imm8 multiply |
| `IMUL_r16_rm16_imm16` | 0x69    | imm16 multiply |
| `INSB` / `INSW` | 0x6C / 0x6D    | input byte/word from DX to ES:[DI] |
| `OUTSB` / `OUTSW` | 0x6E / 0x6F  | output byte/word from DS:[SI] to DX |
| `ENTER_imm16_imm8`  | 0xC8       | nested stack frame setup |
| `LEAVE`         | 0xC9           | tear down ENTER's frame |
| Shift-imm8 group | 0xC0 / 0xC1   | r/m8/16 SHL/SHR/SAL/SAR/ROL/ROR/RCL/RCR with imm count (group-byte ModR/M reg=op) |

**Files**: `spec/x86-16/i80186/groups/i80186-additions.json` (~150 lines)

**Step list 範例**（`ENTER_imm16_imm8`）：

```jsonc
{
  "id": "ENTER_imm16_imm8",
  "encoding": { "primary_opcode": "0xC8" },
  "mnemonic": "ENTER",
  "since": "Intel80186",
  "writes_pc": "never",
  "manual_ref": "Intel iAPX 186 §ENTER",
  "cycles": { "form": "15m_plus_4n" },
  "steps": [
    { "op": "x86_fetch_imm16", "out": "alloc_size" },
    { "op": "x86_fetch_imm8",  "out": "nest_level" },
    // ... ENTER 的真實邏輯：push BP, set BP=SP, copy nest_level frames, sub SP, alloc_size
    // 細節在 emitter 實作 (sprint 25.4)
    { "op": "x86_enter", "alloc_size": "alloc_size", "nest_level": "nest_level" }
  ]
}
```

**注意事項**：
- 0xC0/0xC1 的 sub-mnemonic（SHL_rm8_imm8 / SHR_rm8_imm8 / etc）8 個都要 stable ID。
- `INS` / `OUTS` 跟 8086 既有 `MOVS` / `STOS` / `LODS` 共用 string-op 框架，但 IO port 是新 emitter。

**估**: 4-5 小時（含 cross-check Intel manual）。

### 25.3.4 — `i80186-overrides.json` — silicon-quirk patches

```jsonc
{
  // 8086 silicon: PUSH SP 推 post-decrement value（即 SP-2）
  // 80186 silicon: PUSH SP 推 pre-decrement value（即原 SP）
  "PUSH_SP": {
    "steps": [
      { "op": "x86_push_sp_pre_decrement" }   // ← 新 micro-op，sprint 25.4 emitter 實作
    ]
  },

  // 8086: SHL/SHR/SAL/SAR/ROL/ROR/RCL/RCR with CL count uses full 8-bit count
  // 80186: count masked to bottom 5 bits (count & 0x1F)
  // 影響 8086 既有 0xD2 / 0xD3 (shift by CL) — 全 8 個 sub-mnemonic 都要 patch
  "SHL_rm8_CL": {
    "steps": [
      { "op": "x86_shift_rm8_count_masked", "kind": "shl" }   // ← 新 micro-op
    ]
  },
  // SHR_rm8_CL / SAL_rm8_CL / SAR_rm8_CL / ROL_rm8_CL / ROR_rm8_CL / RCL_rm8_CL / RCR_rm8_CL
  // SHL_rm16_CL ... 共 16 個 entry
  ...
}
```

**Files**: `spec/x86-16/i80186/groups/i80186-overrides.json` (~30 lines for the two silicon quirks)

**估**: 1-2 小時。

### 25.3.5 — Sprint 25.3 完成 gate

```
✓ i80186 spec 載入成功（merged size 跟 i8086 + 12 new + 18 overrides 對應）
✓ Spec dump tool 顯示 PUSH_SP 的 OverriddenBy = "Intel80186"
✓ T1 全綠（i8086 + 新 i80186 spec 共存）
→ commit (尚未跑得起來，因為 emitter 還沒寫)
```

---

## Sprint 25.4 — Emitter additions + overrides

> **目標**：把 spec 用到的 micro-op 全在 X86_16Emitters.cs 補上。

### 25.4.1 — 新 micro-op emitter（12 個 + 1 個 group dispatcher）

| Emitter | 對應 instruction | 預估行數 |
|---|---|---|
| `X86PushImmEmitter`              | PUSH imm8/16              | 30 |
| `X86PushaEmitter`                | PUSHA (8 push, store orig SP) | 60 |
| `X86PopaEmitter`                 | POPA (7 pop, skip SP slot) | 50 |
| `X86BoundEmitter`                | BOUND r16, m16 (range check + INT 5) | 80 |
| `X86ImulRegRmImmEmitter`         | IMUL r16, r/m16, imm8/imm16 | 70 |
| `X86InsStringEmitter`            | INSB / INSW (port → ES:DI) | 60 |
| `X86OutsStringEmitter`           | OUTSB / OUTSW (DS:SI → port) | 60 |
| `X86EnterEmitter`                | ENTER imm16, imm8 (含 nested level loop) | 100 |
| `X86LeaveEmitter`                | LEAVE | 20 |
| `X86ShiftImm8Emitter` (group)    | 8086 0xC0/0xC1 group dispatch on ModR/M reg | 80 |
| `X86PushSpPreDecrementEmitter`   | 80186 PUSH SP override | 20 |
| `X86ShiftCountMaskedEmitter` (group) | 80186 0xD2/0xD3 override (count & 0x1F before shift) | 80 |

總計：~700 行 emitter。

**Files**: 全加在 `src/AprCpu.Core/IR/X86_16Emitters.cs` 末段（在現有 8086 emitter 之後）。

**注意事項**：
- IN/OUT port 在現有 emitter library 已有 `x86_in_dx_port` / `x86_out_dx_port` 可重用。
- INS / OUTS 結合 string-op 框架（已存在的 `x86_string_step_setup` 等）。
- BOUND 的 INT 5 路徑要走既有的 `x86_int_imm` + 5 號 vector（不要寫死路徑）。
- ENTER nested level 在 LLVM IR 內用 BasicBlock loop 實作（dec count, br back to body）— 這直接套用 24.6.8e back-edge pattern。

**Acceptance**:
- 每個 emitter 過 unit test（先寫測試再 emit IR）
- T2 既有 18 PNG 仍 identical（沒打破 8086）

**QA Tier**: 3（hot path 改動 — emitter library；但沒改 dispatcher / bus）。Run T1 + T2。

**估**: 8-12 小時（含寫測試）。

### 25.4.2 — Sprint 25.4 完成 gate

```
✓ 全 13 emitter 接通 (spec 引用的 micro-op 都有對應 IEmitter)
✓ T1 全綠
✓ T2 8086 backend 18 PNG 仍 identical (no regression)
→ commit (i80186 backend 尚未 wire up, 但 emitter library ready)
```

---

## Sprint 25.5 — Backend wiring + CLI variant select

> **目標**：apr-x86 CLI 能 `--variant=i8086|i80186|i80188`，分別載入對應 spec。

### 25.5.1 — CLI 加 `--variant=` flag

**Files**: `src/AprX86.Cli/Program.cs`

- 預設 `i8086`（向後兼容）
- 新 flag：`--variant=i8086|i80186|i80188`
- 對應傳給 X86JsonCpu / X86LegacyCpu 載入哪份 spec

**Files**:
- `src/AprX86.Cli/Cpu/X86JsonCpu.cs`：constructor 加 `string variant = "i8086"`，內部切 spec path
- `src/AprX86.Cli/Cpu/X86LegacyCpu.cs`：legacy 是 hand-coded 8086 — 不支援 80186 → 報錯 `--variant=i80186 --backend=legacy` 組合（或者 80186-specific instructions 觸發 NotImplementedException）

**設計選擇**：legacy 80186 不實作（沒必要 — legacy 只是 reference）。CLI 給 clear error: "legacy backend supports i8086 only; use --backend=json or json-block for i80186."

**估**: 2 小時。

### 25.5.2 — Spec path resolution

**Files**: `src/AprCpu.Core/JsonSpec/SpecLoader.cs`

當看到 `extends: "Intel8086"`，需要找到對應 spec file。設計兩種 strategy：
- **A**: Convention — `spec/<family>/<lowercase-id>/cpu.json`（i80186 → `spec/x86-16/i80186/cpu.json`）
- **B**: 顯式 — schema 加 `extends_path` 或 spec registry

**選 A**（convention 簡單，已 match 既有結構）。SpecLoader 用相對路徑解析。

**估**: 1 小時。

### 25.5.3 — Sprint 25.5 完成 gate

```
✓ apr-x86 --variant=i8086 跑 hello-cga 正常 (T2 18 PNG identical)
✓ apr-x86 --variant=i80186 跑 hello-cga 正常 (應 SHA256 跟 i8086 完全一致 — demo 沒用 186 新指令)
→ commit
```

---

## Sprint 25.6 — Tests, demos, Tom Harte SST filter

### 25.6.1 — 既有 demo cross-validation

跑 `tools/verify_x86_matrix.ps1` 擴充版：對 6 個 demo × 3 backend × 2 variant (i8086/i80186) = 36 個 PNG。預期：
- i8086 跟 i80186 在所有 demo 都產生 identical SHA256（因為 demo 不用 186 新指令）。
- 18 + 18 = 36 PNG 全部一致。

**Files**: `tools/verify_x86_matrix.ps1` 加 `--variant` loop。

**估**: 30 分鐘。

### 25.6.2 — 新 80186-specific demo

寫 1-2 個用 186 新指令的 demo，作為 Phase 25 的視覺輸出：

| Demo | 新指令 | 內容 |
|---|---|---|
| `25-pusha-popa.com` | PUSHA / POPA | save → modify → restore，print 修改前後 |
| `25-enter-leave.com` | ENTER / LEAVE | 寫個遞迴 factorial 用 ENTER 設 stack frame |

**Files**: `test-roms/x86/25-*.com`（hand-assembled bytes 或自製 mini-assembler）。

**Acceptance**:
- 這兩個 demo 在 i8086 backend 跑會觸發 NotImplementedException（PUSHA = 0x60 在 i8086 spec 不存在）— 預期行為。
- 在 i80186 backend 跑得正確、產出 PNG 截圖。

**估**: 2-3 小時（含手寫 .com bytes + 校驗邏輯）。

### 25.6.3 — Tom Harte SST filter for i80186

**Files**: 新增 `tools/sst_i80186_filter.py`

過濾 8088 SST vectors，去掉以下類別：
- opcode 0x54 (PUSH SP) 全部 vector
- opcode 0xD2 / 0xD3 with `cl > 0x1F` (shift count overflow)
- opcode 0x60-0x6F、0xC0/C1、0xC8/C9（這些 byte 8086 算 illegal/undefined，i80186 重定義）

預估 filter 後仍有 ~99% vectors（~1.30M）跑得起來。

**Acceptance**:
- `dotnet test --filter "TomHarte80186"` 全綠（測試 framework 沿用既有 8088 SST runner，差別在 `--variant=i80186` + filtered vector set）

**QA Tier**: 4（架構 demo 級別 — 整個繼承機制 + 新 CPU + 過濾 SST）。

**估**: 4-5 小時。

### 25.6.4 — Sprint 25.6 完成 gate

```
✓ verify_x86_matrix --variant=both 36 PNG 全 identical
✓ 25-pusha-popa.com / 25-enter-leave.com 在 i80186 跑通並產 PNG
✓ 兩個新 demo 在 i8086 backend 觸發 NotImplementedException (預期行為，作為 negative test)
✓ Tom Harte SST i80186 filtered (~1.30M vectors) 全綠
✓ T1 838 tests 全綠
→ commit
```

---

## Sprint 25.7 — Documentation + final commit/push

### 25.7.1 — 寫 perf note

**Files**: `MD/performance/<時戳>-i80186-baseline.md`

跑 i80186 backend 的 bench-loop（如果跑得起來 — bench-loop 不用 186 新指令所以應該等同 i8086），紀錄 MIPS。預期：
- legacy: N/A (legacy 不支援 i80186)
- json: 與 i8086 相當（per-instr trampoline 主導）
- json-block: 與 i8086 相當（~218 MIPS — back-edge optimization 沿用）

寫一份 ~50 行的 perf note 紀錄。

**估**: 30 分鐘。

### 25.7.2 — 更新 roadmap + README

**Files**: `MD/design/03-roadmap.md`、`README.md`

加 Phase 25 完成項：
- "Intel 80186 — first CPU using spec inheritance"
- 預期 framework 維護成本 2.5MB → 0.6MB diff （示意 inheritance ROI）

**估**: 30 分鐘。

### 25.7.3 — 最終 commit + push

```
commit 1: feat(N0d.25.1): spec inheritance — extends + diff infrastructure
commit 2: chore(N0d.25.2): retrofit i8086 spec with stable instruction IDs
commit 3: feat(N0d.25.3): i80186 spec — extends i8086, 12 new ops + 2 silicon-quirk overrides
commit 4: feat(N0d.25.4): X86_16Emitters add 80186 emitters + override emitters
commit 5: feat(N0d.25.5): apr-x86 --variant=i8086|i80186 wiring
commit 6: test(N0d.25.6): 80186 demo ROMs + Tom Harte SST filter (~1.30M vectors)
commit 7: docs(N0d.25): Phase 25 completion + perf baseline
```

每個 commit 對應一個 sprint，互相獨立可 review。

---

## 2. Test compatibility — 跨 8086/80186 重用矩陣

| 測試類型 | 8086 結果 | 80186 結果 | 重用策略 |
|---|---|---|---|
| **24.3/24.5 demo ROMs** (6 個) | ✓ pass | ✓ pass (pixel-identical) | 直接重用，T2 matrix 加 variant 維度 |
| **bench-loop.com** | ✓ 218 MIPS json-block | 預期相當 | 直接重用 |
| **Tom Harte 8088 SST** (1.31M vectors) | ✓ pass | ~99% pass，~1% 要 filter | 寫 filter (sprint 25.6.3) |
| **T1 unit tests** (838) | ✓ pass | ✓ pass (內部 logic 共用) | 直接重用 |
| **新 80186-specific demos** | NotImplementedException (PUSHA 沒實作) | ✓ pass | Phase 25.6.2 新寫 |

### Tom Harte SST filter 預期排除類別

| 類別 | opcode | 預估 vector 比例 |
|---|---|---|
| PUSH SP (silicon quirk) | 0x54 | ~0.1% (1 opcode × ~5000 vectors) |
| Shift by CL with CL > 31 | 0xD2/0xD3 subset | ~0.3% |
| 80186 redefined opcodes | 0x60-0x6F、0xC0/C1、0xC8/C9 | ~0.1-0.5%（看 8088 SST 是否包這些 byte） |

**總排除**: ~0.5-1%（~10K vectors out of 1.31M）。

---

## 3. Risk register

| 風險 | 觸發機率 | 影響 | 緩解 |
|---|---|---|---|
| RFC 7386 partial merge 對 `steps` 處理錯誤（應該整段 replace 但被當 dict merge） | 中 | spec override 失效 | unit test 明確覆蓋這個 case |
| ID conflict in 8086 retrofit (兩個 instruction map 到同 ID) | 中 | sprint 25.2 卡住 | tools/spec_id_audit.py 早期偵測 + 人工 review |
| ENTER 的 nested level loop 在 block-JIT 跟 24.6.8e back-edge pattern 衝突（內部 loop 已經自帶 back-edge） | 中 | 跑 ENTER 可能 hang | 25.4 emitter 寫小 unit test 先驗證 LLVM IR shape，再整合 |
| Tom Harte SST filter 沒過濾乾淨 → 假陽性 fail | 中 | 25.6.3 卡住 | filter 跑通後手動抽幾個 fail case 確認原因 |
| Legacy backend 對 80186 顯式不支援 user 不接受 | 低 | 政策爭議 | 直接報錯 + 文件清楚標示，預期 user OK |
| Schema breaking change 影響 ARM/LR35902/2A03 既有 spec | 低 | 全 CPU regression | sprint 25.1 完成時 T1 838 全綠是硬 gate |

---

## 4. 預估時程總計

| Sprint | 估時 | 累計 |
|---|---|---|
| 25.1 Inheritance infra | 6-9 hours | 9 |
| 25.2 ID retrofit | 4-5 hours | 14 |
| 25.3 i80186 spec | 6-8 hours | 22 |
| 25.4 Emitters | 8-12 hours | 34 |
| 25.5 CLI wiring | 3 hours | 37 |
| 25.6 Tests + demos | 6-8 hours | 45 |
| 25.7 Docs + push | 1 hour | 46 |

**總計：~40-50 小時**（5-7 個工作天，按 24.6.8d/e 的 ~5h pattern × 約 9 個 session）。

如果走 /loop autonomous：**user 可以 set goal 後讓 Claude 跑 1-2 週，每天回報 sprint 進度**。中間關鍵 fork point（ID convention / override semantics）由 Claude 經 Pattern B 諮詢 Gemini，user 拍板。

---

## 5. Phase 25 完成的 deliverables

```
spec/
├── schema/cpu-spec.schema.json                    [25.1.2 — 加 extends/diff/id]
└── x86-16/
    ├── i8086/groups/*.json                        [25.2 — 全 instruction 加 ID]
    └── i80186/                                    [25.3 — 全新 directory, ~250 行]
        ├── cpu.json
        └── groups/{additions,overrides}.json

src/
├── AprCpu.Core/JsonSpec/
│   ├── SpecLoader.cs                              [25.1.3 — merge 邏輯]
│   ├── JsonMergePatch.cs                          [25.1.3 NEW — RFC 7386 helper]
│   └── SpecModel.cs                               [25.1.4 — provenance metadata]
├── AprCpu.Core/IR/
│   └── X86_16Emitters.cs                          [25.4 — +700 行 emitter]
├── AprX86.Cli/
│   ├── Program.cs                                 [25.5.1 — --variant flag]
│   └── Cpu/X86JsonCpu.cs                          [25.5.1 — variant constructor]
└── AprCpu.Tests/                                  [25.1.3 / 25.2 — 新 unit tests]

test-roms/x86/
├── 25-pusha-popa.com                              [25.6.2 NEW]
└── 25-enter-leave.com                             [25.6.2 NEW]

tools/
├── spec_id_audit.py                               [25.2.2 NEW]
├── sst_i80186_filter.py                           [25.6.3 NEW]
└── verify_x86_matrix.ps1                          [25.6.1 — 加 --variant 維度]

MD/
├── design/
│   ├── 25-i80186-implementation-plan.md           [本文件]
│   └── 25.2-instruction-id-conventions.md         [25.2.1 NEW]
├── performance/
│   └── <時戳>-i80186-baseline.md                  [25.7.1 NEW]
└── design/03-roadmap.md                           [25.7.2 — 更新]
```

**總改動規模預估**:
- New files: ~10
- Modified files: ~8
- Lines of code/spec: ~1500 (emitter ~700 + spec ~250 + schema ~50 + loader ~300 + tests ~200)
- Lines of doc: ~600 (本文件 + 子文件 + perf note)

---

## 6. 完成準則 (definition of done)

```
✓ apr-x86 --variant=i80186 --backend=json-block 跑通 PUSHA/POPA/BOUND/IMUL imm/INS/OUTS/ENTER/LEAVE/shift-imm
✓ 既有 i8086 backend 行為完全不變（SHA256 18 PNG / Tom Harte SST / T1 全綠）
✓ Spec inheritance demo: i80186 spec 檔總行數 ≤ 300（vs 從零寫的 ~3000）
✓ Spec dump tool 顯示 "PUSH_SP overridden by i80186" provenance
✓ Phase 25 perf note 紀錄 i80186 MIPS 數字
✓ 所有 7 個 commit 已 push origin/main
✓ /loop / cron 機制已 cancel
```

---

> 本 plan 對齊 doc 23 (CPU spec inheritance design v2)。實作期間若發現
> doc 23 的 design 需要修正（例如 partial merge 對 step list 的處理變得
> 複雜），先回 doc 23 patch，再回本 plan 對應 sprint 調整。
