# Phase 27 — Intel 80286 completion plan

> **Status**: Phase 27a ✅ **COMPLETE** (2026-05-11). Real-mode 80286
> system instruction set landed: 14 of 14 instructions covered,
> 7 new state registers, 4 verified round-trip demos. Closure note:
> `MD/performance/202605110100-i80286-realmode-complete.md`.
> Phase 27b (protected mode) is multi-week future work, deferred.
> **Predecessor**: Phase 26 v1 (`6b1e2d6`..`7e6cf16`) — minimum-viable real-mode
> 80286 shipped with chain depth=3, 0F prefix infra, CLTS+SMSW.
> **Goal**: finish the 80286 implementation. Two tracks:
>   - **Phase 27a — real-mode completion** (~5 sprints, ~1-2 weeks)
>   - **Phase 27b — protected mode** (~5-8 sprints, ~3-4 weeks)
>
> Closure note for Phase 26 listed the deferred work; this doc plans
> how to land it.

---

## Phase 27a — real-mode completion

### What's missing today

Phase 26 v1 shipped:
- CLTS (no-op stub, no MSW state)
- SMSW (returns constant 0xFFF0)

Phase 27a fills out the rest of the 80286 system-instruction set,
real-mode semantics:

| Instruction | Encoding | Real-mode behavior |
|---|---|---|
| LGDT m48     | 0F 01 /2 | Load 6-byte (16-bit limit + 24-bit base) into GDTR |
| LIDT m48     | 0F 01 /3 | Same for IDTR |
| SGDT m48     | 0F 01 /0 | Store GDTR to 6-byte memory |
| SIDT m48     | 0F 01 /1 | Same for IDTR |
| LMSW r/m16   | 0F 01 /6 | Write low 16 bits to MSW. PE bit (bit 0) ignored in real mode for our purposes (entering protected mode is Phase 27b). |
| SMSW r/m16   | 0F 01 /4 | Read MSW (real value, not constant) |
| LLDT r/m16   | 0F 00 /2 | Load LDT register |
| SLDT r/m16   | 0F 00 /0 | Store LDT register |
| LTR r/m16    | 0F 00 /3 | Load Task register |
| STR r/m16    | 0F 00 /1 | Store Task register |
| LAR r16, r/m16 | 0F 02 | Load access rights — real-mode no-op (returns 0, clears ZF) |
| LSL r16, r/m16 | 0F 03 | Load segment limit — real-mode no-op |
| VERR r/m16   | 0F 00 /4 | Verify segment readable — real-mode no-op (clears ZF) |
| VERW r/m16   | 0F 00 /5 | Verify segment writable — real-mode no-op (clears ZF) |

### State additions

Need new status registers:
- `MSW` (16-bit) — Machine Status Word. Reset = 0xFFF0.
- `GDTR_BASE` (32-bit, low 24 bits used) — GDTR base address
- `GDTR_LIMIT` (16-bit) — GDTR limit
- `IDTR_BASE` (32-bit) — IDTR base
- `IDTR_LIMIT` (16-bit) — IDTR limit
- `LDTR` (16-bit) — LDT register selector
- `TR` (16-bit) — Task register selector

Schema extension needed: `register_file_diff` to add new status registers
in child specs (parallel to instruction_set_diff). Currently child specs
inherit register_file wholesale or override entirely; no additive diff.

### Sprint breakdown

| Sprint | Deliverable | Estimate |
|---|---|---|
| 27.1 | MSW state register + real LMSW + SMSW reads real MSW | 3-4h |
| 27.2 | LGDT/LIDT/SGDT/SIDT + GDTR/IDTR state slots | 4-5h |
| 27.3 | 0F 00 group (SLDT/STR/LLDT/LTR/VERR/VERW) + LDTR/TR slots | 3-4h |
| 27.4 | LAR/LSL real-mode stubs | 2h |
| 27.5 | Phase 27a closure docs + perf note | 1h |

Total ~1-2 work days when continuous; with /loop interruptions ~1 week.

---

## Phase 27b — protected mode

Multi-week. Ordered by what's blocking what:

| Sprint | Deliverable | Estimate |
|---|---|---|
| 27.6 | Descriptor table format parsing (8-byte segment descriptors) | 1d |
| 27.7 | Selector format + descriptor lookup helpers | 1d |
| 27.8 | LGDT/LIDT real (load + descriptor table fetch on segment load) | 2d |
| 27.9 | Privilege levels (CPL/RPL/DPL) + ring transitions | 2-3d |
| 27.10 | Segmentation rewrite (selector → base/limit lookup) | 3-4d |
| 27.11 | New exception model (#GP, #SS, #NP, #TS, #UD with error codes) | 2-3d |
| 27.12 | TSS task switching | 3-4d |
| 27.13 | Protected-mode entry (LMSW PE bit handling) | 1-2d |
| 27.14 | Phase 27b closure + visual demo (`protmode-msr-i80286.png`) | 1d |

Total ~3-4 weeks. This is where the real complexity lives.

**Phase 27b is intentionally deferred from the current /loop session.**
Realistic plan: Phase 27a in this session (or two), then a fresh
multi-week effort for 27b.

---

## Phase 27a Sprint Status

| Sprint | Status | Commit | 完成日 |
|---|---|---|---|
| 27.1 MSW + real LMSW | ✅ | `95e5138` | 2026-05-11 |
| 27.2 LGDT/LIDT + GDTR/IDTR | ✅ | `5c2a70a` | 2026-05-11 |
| 27.3 0F 00 group | ✅ | `13dd781` | 2026-05-11 |
| 27.4 LAR/LSL stubs | ✅ | `78cf9fd` | 2026-05-11 |
| 27.5 27a closure docs | ✅ | (this commit) | 2026-05-11 |

## Phase 27b Sprint Status

| Sprint | Status |
|---|---|
| 27.6  Descriptor + selector helpers | ✅ | `11fdc98` | 2026-05-11 |
| 27.7  Descriptor lookup (mem -> Descriptor) | ✅ | `9f819b5` | 2026-05-11 |
| 27.8  PE bit helper + Msw struct | ✅ partial (PE detection only; segment-load fetch in 27.10) | `0dd6404` | 2026-05-11 |
| 27.9  Privilege levels (CPL/RPL/DPL) | ✅ | `553909c` | 2026-05-11 |
| 27.10a Helper integration test | ✅ | `cefed78` | 2026-05-11 |
| 27.10b Hidden segment cache slots (ES/CS/SS/DS Base/Limit) | ✅ | `97e19ea` | 2026-05-11 |
| 27.10c SegmentedLinear uses cached Base | ✅ infra (not activated) | `412fc7a` | 2026-05-11 |
| 27.10d wave 1 — FetchImm uses CS_BASE | ✅ | `2531bd8` | 2026-05-11 |
| 27.10d wave 2 — ModR/M / Read/Write by-name | ⏳ pending | — | — |
| 27.10d wave 3 — Stack ops (PUSHA/POPA/ENTER) by-name | ⏳ pending | — | — |
| 27.10d wave 4 — SDT helpers by-name | ⏳ pending | — | — |
| 27.10d wave 5 — Protected-mode segment-load IR (the actual descriptor lookup) | ⏳ pending | — | — |
| 27.11 New exception model | ⏳ pending | — | — |
| 27.12 TSS task switching | ⏳ pending | — | — |
| 27.13 Protected-mode entry (LMSW PE) | ⏳ pending | — | — |
| 27.14 27b closure + visual demo | ⏳ pending | — | — |
