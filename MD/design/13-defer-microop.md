# `defer` micro-op — 通用 delayed-effect 機制

> **狀態**：設計文件（2026-05-03）。實作追蹤為 Phase 7 GB block-JIT P0.6
> （看 [`MD/design/12-gb-block-jit-roadmap.md`](/MD/design/12-gb-block-jit-roadmap.md)）。
>
> **起源**：Gemini consult
> ([`tools/knowledgebase/message/20260503_220938.txt`](/tools/knowledgebase/message/20260503_220938.txt))
> 關於業界處理 delayed-effect CPU quirk（LR35902 EI、Z80 STI、x86 STI、
> SH-2 branch delay、RISC-V fence.i、MIPS load-use 等）的 generic 做法 —
> 不在 `BlockDetector` 手刻 per-CPU logic。
>
> **目標**：用 JSON-spec-driven 機制取代 `BlockDetector` 目前 hardcode
> 的 `HasEiDelayStep` patch，讓任何未來 CPU 可以 reuse 它自己的 delayed
> quirk。Common case 下 block-JIT runtime cost 為零。

---

## 1. 問題陳述

幾個 CPU 有 instruction 的 effect 在下個 instruction 完成 *之後* 發生
（instruction-grained delay）：

| CPU | Instruction | Delay | Effect |
|---|---|---|---|
| LR35902 / Z80 | `EI` | 1 instr | IME=1 |
| x86 | `STI` | 1 instr | IF=1 |
| SH-2 | branch (delay slot) | 1 instr | PC = target after delay-slot instr |
| RISC-V | `fence.i` | until next fetch | I-cache invalidation |
| 6502 | NMI sample | 1 instr 邊界 | NMI vector |
| 65816 | `XCE` mode swap | next instr | width register 改變 |

Per-instruction backend 透過 host-side counter（`_eiDelay`）處理這些
因為 outer loop 每個 instruction 檢查一次 counter。Block-JIT 一次 call
跑 N instruction、打破 instruction-grained 粒度。

我們 codebase 目前的 band-aid（P0.5b commit `771d170`）：
`BlockDetector.HasEiDelayStep` hardcode 檢查 LR35902-specific
`lr35902_ime_delayed` step name、強迫 block 在 EI+1 結束。這個做法：
- LR35902-specific（不通用到 Z80 / x86）
- 沒完全修 EI test（Block 2 instr 2..N 的 IME state 還是錯 —
  看 P0.5b commit 限制）
- 犧牲 perf（EI 處 block 切開 = EI 區內無 amortization）

---

## 2. Generic 解：`defer` micro-op

### 2.1 JSON spec syntax

把 delayed body 包在 `defer` step：

```json
{
  "mnemonic": "EI",
  "encoding": "11111011",
  "steps": [
    {
      "op": "defer",
      "delay_type": "instruction_count",
      "delay_value": 1,
      "body": [
        { "op": "set_flag", "reg": "F", "flag": "IME", "value": 1 }
      ]
    }
  ]
}
```

欄位：
- `op: "defer"` — control-flow wrapper、不 lower 到直接 IR
- `delay_type` — `"instruction_count"`（V1）；未來：`"branch_taken"`、
  `"cycle_count"`、`"until_condition"`
- `delay_value` — 整數 N（instruction_count 時、在 N 個 instruction 之後 fire）
- `body` — micro-op step array、delay 到期時跑

同個 instruction 多個 defer step 可以（各自獨立追蹤）。連續多個 instruction
都有 defer 也 fine（多個 pending action 平行追蹤）。

### 2.2 Block-JIT lowering — Phantom Instruction Injection

Block builder 拿到 `block.Instructions` list。**Emit LLVM IR 之前**，
跑一個 AST pre-pass：

```
pending = []   // list of (remaining_delay, body_steps)
for each instruction in block.Instructions:
    // Decrement 全部 pending delay。
    for p in pending:
        p.remaining_delay -= 1
    // 找 NOW (= 0) fire 的 delay、把 body 注到這 instr step 前面。
    fired = pending.filter(p.remaining_delay == 0)
    pending = pending.filter(p.remaining_delay > 0)
    instruction.steps = [s for f in fired for s in f.body] + instruction.steps
    // 從這 instruction 自己的 step 剝掉 defer wrapper（不重 emit）。
    instruction.steps = instruction.steps.map(unwrap_defer_to_register)
    // 這 instruction step 裡的每個 defer、把 body push 到 pending。
    for s in instruction.steps where s.op == "defer":
        pending.append((s.delay_value, s.body))
```

Pre-pass 之後、把 mutate 過的 instruction list 交給正規 IR emitter。
Emitter 看到 plain micro-op 無 `defer` wrapper — 完全 generic、不知道
delay semantics。

### 2.3 跨 block fallback

如果 `defer` 在 block 的 LAST instruction（或 delay 延伸超過 block end），
compile-time pending list 還有 entry。兩個 fallback option：

**(A) Block epilogue serialization**：emit IR 把 pending action info 寫到
`cpu_state.pending_bitmap` slot。Block exit 正常結束。

**(B) Block preamble check**：每個 block 的 entry IR 檢查
`pending_bitmap != 0`；非零、fast-path-jump 到一個小 handler、在正常
block flow 之前 fire 過期 action。

這對 handle 罕見 case（block 結尾的 defer）、代價是每個 block start 一個
load + branch。Block linking 確保 hot-path 大多 non-pending → fast-path
很少觸發。

### 2.4 Per-instr backend

`JsonCpu.StepOne` per-instruction path 沒有 compile-time 機會。Runtime
實作 defer：

- 新 emitter `DeferEmitter`（per-instr mode）：寫 (action_id, delay_value,
  body_id) 到 `pending_actions[]` state slot
- `JsonCpu.RunCycles` outer loop 每個 StepOne 之後：decrement
  `pending_actions[]` counter、fire 任何 hit 0 的（呼叫 body 的 host
  extern、或 callback 進 JIT'd body fn）

V1 比較乾淨的 alternative：per-instr 既有的 `_eiDelay` /
`lr35902_arm_ime_delayed` host extern flow 先留著（不變）。Generic defer
機制只給 block-JIT。意思是 EI spec 同時帶兩條 path（per-instr extern +
block-JIT defer body）、醜。V2 統一。

---

## 3. State 改動

`CpuStateLayout` 加：

- `PendingActionsBitmapOffset` — i32（或 i64 如果預期超過 32 個 action）
- `PendingActionsCounters[N]` — i8[N] for per-action countdown（N 小、
  例如 8）

或 V1 比較簡單：
- `PendingDeferredFlags` — i32 bitmap；bit set = action pending；
  per CPU spec 定義 bit

LR35902 EI 具體：bit 0 = IME-pending。跨 block fallback set bit 0；
preamble check fire `set_flag IME=1` 然後清 bit 0。

---

## 4. 實作步驟（P0.6）

### 4.1 Step 1 — Spec schema + parser（~0.5 day）

- `SpecModel.cs`：`MicroOpStep` 維持 generic；defer parsing 在 AST
  pre-pass 透過 JsonElement lazily 發生
- `SpecLoader.cs`：確保 `body` array load 為 nested step list
- 加 validation：`op:"defer"` 需要 `delay_type`、`delay_value`、
  `body`；允許的 delay_type value：`"instruction_count"`（V1）

### 4.2 Step 2 — BlockFunctionBuilder 的 AST pre-pass（~1 day）

- 新 helper `DeferLowering.PreprocessBlock(IReadOnlyList<DecodedBlockInstruction>)`
  回 mutate 過、phantom 注入過、defer strip 過的 list
- 追蹤 pending list、per instruction decrement、注入過期 body
- Block end 還沒過期的 defer：emit「serialize」wrapper step 寫到
  `pending_bitmap` slot
- BlockFunctionBuilder.Build 在 main loop 之前呼叫 pre-pass

### 4.3 Step 3 — `pending_bitmap` state slot + preamble check（~0.5 day）

- `CpuStateLayout`：加 `PendingDeferredFlagsFieldIndex`、像其他
  emulator-suffix field
- BlockFunctionBuilder block preamble：emit
  `if (pending_bitmap != 0) { handle_pending_then_jump_to_first_instr; }`
- Handler 執行 pending body（by action ID）並清 bit

### 4.4 Step 4 — Per-instr fallback（~0.5 day）

V1：既有的 `lr35902_arm_ime_delayed` extern 不動。不碰 per-instr。

V2（延後）：對 per-instr backend 實作 DeferEmitter、寫到 state 的
pending counter table；JsonCpu.RunCycles outer loop per instr tick counter。

### 4.5 Step 5 — 遷移 LR35902 EI spec（~0.5 day）

- `spec/cpu/lr35902/groups/block3-di-ei.json`：EI step 從
  `[{ "op": "lr35902_ime_delayed" }]` 改成
  `[{ "op": "defer", "delay_type": "instruction_count", "delay_value": 1, "body": [...] }]`
- V1 留 `lr35902_ime_delayed` 為 alternative — per-instr 用舊、
  block-JIT 用新。Schema validator 接受兩個。

### 4.6 Step 6 — 從 BlockDetector 移除 hardcode 的 `HasEiDelayStep`（~0.5 day）

- 移除 P0.5b 加的 band-aid
- 驗 block detector 不再在 EI 結束
- T1 + T2 + Blargg 02-interrupts/EI test 應該還是 pass

### 4.7 Step 7 — 驗證（~0.5 day）

- T1 unit test（需要新 test cover defer pre-pass + 跨 block serialization
  + preamble fast-path）
- T2 GBA matrix（regression — ARM 不用 defer、應該 no-op）
- T3 GB Blargg 01-special + 02-interrupts（現在應該 pass）+
  bench（比較 perf 前後）

**Total**：~3-4 day 工作、V1 scope。

---

## 5. 未來擴充（不是 P0.6）

### 5.1 其他 delay_type 值

- `"branch_taken"`：下個 branch 採時 fire body（SH-2 delay slot）
- `"cycle_count"`：N cycle 之後 fire（cycle-grained、不是 instr-grained）
- `"until_condition"`：某 runtime condition 成立時 fire

### 5.2 HALT / STOP 透過 defer

HALT 語意是「停執行直到 IRQ」— 不是 delayed effect。可以 model 為
`defer { delay_type: "until_irq", body: [resume] }` 但那是更大的 refactor。
目前 HasHaltOrStopStep 維持 hardcode。

### 5.3 Conditional defer

某些 delayed effect 只在 delay 期間某 flag set 時 fire。加 `condition`
field：
```json
{ "op": "defer", "delay_value": 1, "condition": "F.Z == 1", "body": [...] }
```

### 5.4 Per-CPU action ID registry

多個 CPU 用 defer + 自己的 action ID 時，framework 需要在 global
pending_bitmap 分配 bit。Per-CPU ID registry 在 spec。

---

## 6. 實作前的決策點

1. **Scope V1 或 V2**：V1 = block-JIT only、per-instr 留舊 extern。
   V2 = 兩個 backend 都用 spec-driven defer。**建議 V1** — 先把 generic
   機制裝進去、驗證設計、之後再 cleanup。

2. **pending_bitmap 或 per-action counter table**：V1 = 單一 bitmap
   （每 bit = pending action ID）。簡單。限制：每 CPU 最多 32 個同時
   action。

3. **AST pre-pass 位置**：在 `BlockFunctionBuilder.Build`（最整合）或
   在 detector 跟 builder 之間跑的獨立 `DeferLowering` pass（更 reusable）。
   **建議獨立 pass**、為 testability + 未來彈性。
