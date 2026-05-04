# `defer` micro-op — generic delayed-effect mechanism

> **Status**: design doc (2026-05-03). Implementation tracked as Phase 7
> GB block-JIT P0.6 (see `MD/design/12-gb-block-jit-roadmap.md`).
>
> **Origin**: Gemini consultation (`tools/knowledgebase/message/20260503_220938.txt`)
> on industry patterns for handling delayed-effect CPU quirks (LR35902 EI,
> Z80 STI, x86 STI, SH-2 branch delay, RISC-V fence.i, MIPS load-use, etc.)
> in a generic way — without hand-coding per-CPU logic in `BlockDetector`.
>
> **Goal**: replace the current hardcoded `HasEiDelayStep` patch in
> `BlockDetector` with a JSON-spec-driven mechanism that any future CPU
> can reuse for its own delayed quirks. Zero block-JIT runtime cost in
> the common case.

---

## 1. Problem statement

Several CPUs have instructions whose effect takes place AFTER the next
instruction completes (instruction-grained delay):

| CPU | Instruction | Delay | Effect |
|---|---|---|---|
| LR35902 / Z80 | `EI` | 1 instr | IME=1 |
| x86 | `STI` | 1 instr | IF=1 |
| SH-2 | branch (delay slot) | 1 instr | PC = target after delay-slot instr |
| RISC-V | `fence.i` | until next fetch | I-cache invalidation |
| 6502 | NMI sample | 1 instr boundary | NMI vector |
| 65816 | `XCE` mode swap | next instr | width register changes |

Per-instruction backend handles these via host-side counters (`_eiDelay`)
because the outer loop checks counters once per instruction. Block-JIT
runs N instructions per call, breaking instruction-grained granularity.

Current band-aid in our codebase (P0.5b commit `771d170`):
hardcoded `BlockDetector.HasEiDelayStep` checks for LR35902-specific
`lr35902_ime_delayed` step name and forces block to end at EI+1. This is:
- LR35902-specific (doesn't generalise to Z80 / x86)
- Doesn't fully fix EI test (Block 2 still has wrong IME state for instr
  2..N — see P0.5b commit limitations)
- Sacrifices perf (block split at EI = no amortization across the EI region)

---

## 2. Generic solution: `defer` micro-op

### 2.1 JSON spec syntax

Wrap the delayed body in a `defer` step:

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

Fields:
- `op: "defer"` — control-flow wrapper, not lowered to direct IR
- `delay_type` — `"instruction_count"` (V1); future: `"branch_taken"`,
  `"cycle_count"`, `"until_condition"`
- `delay_value` — integer N (for instruction_count, fires after N more
  instructions)
- `body` — array of micro-op steps, run when delay expires

Multiple defer steps in the same instruction are allowed (each tracks
independently). Multiple instructions in a row with defer is also fine
(multiple pending actions tracked in parallel).

### 2.2 Block-JIT lowering — Phantom Instruction Injection

The block builder gets a list of `block.Instructions`. **Before** emitting
LLVM IR, run an AST pre-pass:

```
pending = []   // list of (remaining_delay, body_steps)
for each instruction in block.Instructions:
    // Decrement all pending delays.
    for p in pending:
        p.remaining_delay -= 1
    // Find delays that fire NOW (= 0) and inject body at front of this instr's steps.
    fired = pending.filter(p.remaining_delay == 0)
    pending = pending.filter(p.remaining_delay > 0)
    instruction.steps = [s for f in fired for s in f.body] + instruction.steps
    // Strip defer wrappers from this instruction's own steps (don't re-emit).
    instruction.steps = instruction.steps.map(unwrap_defer_to_register)
    // For each defer in this instruction's steps, push body onto pending.
    for s in instruction.steps where s.op == "defer":
        pending.append((s.delay_value, s.body))
```

After pre-pass, hand the mutated instruction list to the regular IR
emitter. The emitter sees plain micro-ops with no `defer` wrapper — fully
generic, no awareness of delay semantics.

### 2.3 Cross-block fallback

If `defer` is in the LAST instruction of a block (or the delay extends
beyond block end), the compile-time pending list still has entries. Two
fallback options:

**(A) Block epilogue serialization**: emit IR that writes pending action
info into `cpu_state.pending_bitmap` slot. Block exit finishes normally.

**(B) Block preamble check**: every block's entry IR checks
`pending_bitmap != 0`; if non-zero, fast-path-jumps to a small handler
that fires expired actions before normal block flow.

This pair handles the rare cases (defer at end of block) at the cost of
one load + branch per block start. Block linking ensures hot-path stays
mostly non-pending → fast-path triggers rarely.

### 2.4 Per-instr backend

`JsonCpu.StepOne` per-instruction path doesn't have the compile-time
opportunity. Implement defer at runtime:

- New emitter `DeferEmitter` (per-instr mode): writes (action_id,
  delay_value, body_id) into `pending_actions[]` state slot
- `JsonCpu.RunCycles` outer loop after each StepOne: decrement counters
  in `pending_actions[]`, fire any that hit 0 (call body's host extern,
  or call back into JIT'd body fn)

Cleaner alternative for V1: keep per-instr's existing `_eiDelay` /
`lr35902_arm_ime_delayed` host extern flow for now (unchanged). Apply
generic defer mechanism only to block-JIT. This means EI spec carries
BOTH paths (per-instr extern + block-JIT defer body) which is ugly. V2
unifies.

---

## 3. State changes

`CpuStateLayout` adds:

- `PendingActionsBitmapOffset` — i32 (or i64 if more than 32 actions
  expected)
- `PendingActionsCounters[N]` — i8[N] for per-action countdown (N small,
  e.g. 8)

Or simpler V1:
- `PendingDeferredFlags` — i32 bitmap; bit set = action pending; bits
  defined per CPU spec

For LR35902 EI specifically: bit 0 = IME-pending. Cross-block fallback
sets bit 0; preamble check fires `set_flag IME=1` then clears bit 0.

---

## 4. Implementation steps (P0.6)

### 4.1 Step 1 — Spec schema + parser (~0.5 day)

- `SpecModel.cs`: keep `MicroOpStep` generic; defer parsing happens
  lazily in the AST pre-pass via JsonElement
- `SpecLoader.cs`: ensure `body` array is loaded as nested step list
- Add validation: `op:"defer"` requires `delay_type`, `delay_value`,
  `body`; allowed delay_type values: `"instruction_count"` (V1)

### 4.2 Step 2 — AST pre-pass in BlockFunctionBuilder (~1 day)

- New helper `DeferLowering.PreprocessBlock(IReadOnlyList<DecodedBlockInstruction>)`
  returns mutated list with phantoms injected + defers stripped
- Track pending list, decrement per instruction, inject expired bodies
- For un-expired defers at block end: emit "serialize" wrapper steps
  that write to `pending_bitmap` slot
- BlockFunctionBuilder.Build calls the pre-pass before its main loop

### 4.3 Step 3 — `pending_bitmap` state slot + preamble check (~0.5 day)

- `CpuStateLayout`: add `PendingDeferredFlagsFieldIndex` like other
  emulator-suffix fields
- BlockFunctionBuilder block preamble: emit
  `if (pending_bitmap != 0) { handle_pending_then_jump_to_first_instr; }`
- Handler executes pending bodies (by action ID) and clears bits

### 4.4 Step 4 — Per-instr fallback (~0.5 day)

V1: keep existing `lr35902_arm_ime_delayed` extern unchanged. Don't
touch per-instr.

V2 (deferred): implement DeferEmitter for per-instr backend that writes
to state's pending counter table; JsonCpu.RunCycles outer loop ticks
counters per instr.

### 4.5 Step 5 — Migrate LR35902 EI spec (~0.5 day)

- `spec/lr35902/groups/block3-di-ei.json`: change EI's step from
  `[{ "op": "lr35902_ime_delayed" }]` to
  `[{ "op": "defer", "delay_type": "instruction_count", "delay_value": 1, "body": [...] }]`
- For V1, keep `lr35902_ime_delayed` as alternative — per-instr uses old,
  block-JIT uses new. Schema validator accepts both.

### 4.6 Step 6 — Remove hardcoded `HasEiDelayStep` from BlockDetector (~0.5 day)

- Remove the band-aid added in P0.5b
- Verify block detector no longer terminates at EI
- T1 + T2 + Blargg 02-interrupts/EI test should still pass

### 4.7 Step 7 — Verify (~0.5 day)

- T1 unit tests (need new tests covering defer pre-pass + cross-block
  serialization + preamble fast-path)
- T2 GBA matrix (regression — ARM doesn't use defer, should be no-op)
- T3 GB Blargg 01-special + 02-interrupts (now should pass) +
  bench (compare perf before/after)

**Total**: ~3-4 days work, V1 scope.

---

## 5. Future extensions (not P0.6)

### 5.1 Other delay_type values

- `"branch_taken"`: fire body when next branch is taken (SH-2 delay slot)
- `"cycle_count"`: fire after N cycles (cycle-grained, not instr-grained)
- `"until_condition"`: fire when a runtime condition holds

### 5.2 HALT / STOP via defer

HALT semantically is "stop execution until IRQ" — not a delayed effect.
Could be modeled as `defer { delay_type: "until_irq", body: [resume] }`
but that's a bigger refactor. Keep hardcoded HasHaltOrStopStep for now.

### 5.3 Conditional defer

Some delayed effects only fire if some flag is set during the delay
window. Add `condition` field:
```json
{ "op": "defer", "delay_value": 1, "condition": "F.Z == 1", "body": [...] }
```

### 5.4 Per-CPU action ID registry

If multiple CPUs use defer with their own action IDs, the framework
needs to allocate bits in the global pending_bitmap. Per-CPU ID registry
in spec.

---

## 6. Decision points before implementation

1. **Scope V1 or V2**: V1 = block-JIT only, per-instr keeps old extern.
   V2 = both backends use spec-driven defer. **V1 recommended** — gets
   the generic mechanism in fast, validates the design, cleanup later.

2. **pending_bitmap or per-action counter table**: V1 = single bitmap
   (each bit = pending action ID). Simpler. Limits: max 32 simultaneous
   actions per CPU.

3. **AST pre-pass location**: in `BlockFunctionBuilder.Build` (most
   integrated) or in a separate `DeferLowering` pass run between
   detector and builder (more reusable). **Separate pass recommended**
   for testability + future flexibility.
