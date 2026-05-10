# Phase 27 — Intel 80286 completion plan

> **Status**: Phase 27a ✅ **COMPLETE** (2026-05-11). Real-mode 80286
> system instruction set landed: 14 of 14 instructions covered,
> 7 new state registers, 4 verified round-trip demos. Closure note:
> `MD/performance/202605110100-i80286-realmode-complete.md`.
>
> **Phase 27b (protected mode) ✅ COMPLETE** (2026-05-11). Descriptor-
> based segmentation + 4-check fault model end-to-end on i80286
> backend. 5-ROM fault matrix demoable. Closure note:
> `MD/performance/202605110200-i80286-pmode-fault-model-complete.md`.
> Sprint 27.12 (TSS task switching) deferred to a future phase
> (multi-day work, well-defined and additive on top of the
> `EmitSegCacheUpdate` + `EmitRaiseException` helpers landed here).
> **Predecessor**: Phase 26 v1 (`6b1e2d6`..`7e6cf16`) — minimum-viable real-mode
> 80286 shipped with chain depth=3, 0F prefix infra, CLTS+SMSW.
> **Goal (achieved)**: finish the 80286 implementation. Two tracks:
>   - **Phase 27a — real-mode completion** (5 sprints) ✅
>   - **Phase 27b — protected mode** (22 sprints, micro-sprint cadence) ✅
>     (Sprint 27.12 TSS task switching intentionally deferred to a
>     separate phase — additive on top of the helpers landed here.)
>
> Closure note for Phase 26 listed the deferred work; this doc planned
> how to land it. Both tracks are now closed; this doc is preserved as
> historical record + sprint-status reference.

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

**Outcome (2026-05-11)**: Phase 27b closed in a single extended /loop
session via micro-sprint cadence (27.6 → 27.14, see status table below).
Sprint 27.12 (TSS task switching) intentionally deferred — additive on
top of `EmitSegCacheUpdate` + `EmitRaiseException` helpers, well-defined
for a future phase. Closure note:
`MD/performance/202605110200-i80286-pmode-fault-model-complete.md`.

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

22 micro-sprints, all landed 2026-05-11. Grouped by track for readability;
chronological order within each track.

### Track 1 — Descriptor-fetch infrastructure (Sprints 27.6 → 27.10d)

| Sprint | Status | Commit | 完成日 |
|---|---|---|---|
| 27.6  Descriptor + Selector records, parse/build helpers | ✅ | `11fdc98` | 2026-05-11 |
| 27.7  ReadDescriptor / WriteDescriptor over IMemoryBus | ✅ | `9f819b5` | 2026-05-11 |
| 27.8  Msw struct + IsProtectedMode (PE-bit reader) | ✅ | `0dd6404` | 2026-05-11 |
| 27.9  Privilege-level helpers (CPL/RPL/DPL) | ✅ | `553909c` | 2026-05-11 |
| 27.10a Helper integration test (end-to-end mock) | ✅ | `cefed78` | 2026-05-11 |
| 27.10b Hidden cache slots: <seg>_BASE/_LIMIT/_ACCESS × 4 | ✅ | `97e19ea` | 2026-05-11 |
| 27.10c SegmentedLinear by-name overload (cache-aware infra) | ✅ | `412fc7a` | 2026-05-11 |
| 27.10d wave 1 — FetchImm uses CS_BASE | ✅ | `2531bd8` | 2026-05-11 |
| 27.10d wave 2 — Stack ops + by-name Read/Write overloads | ✅ | `126e2cc` | 2026-05-11 |
| 27.10d wave 3 — PushReg/PushModRm/PushSpPreDec by-name | ✅ | `66dc4df` | 2026-05-11 |
| 27.10d wave 4 — ea_base alias (EA-compute foundation) | ✅ | `3605c42` | 2026-05-11 |
| 27.10d wave 5 — ea_base cache lookup (override path) | ✅ | `7d529f3` | 2026-05-11 |
| 27.10d wave 6 — segIdx tracking, unified cache lookup | ✅ | `c765352` | 2026-05-11 |
| 27.10d wave 7 — MOV sreg updates cache (real-mode shape) | ✅ | `3851757` | 2026-05-11 |
| 27.10d wave 8 — Descriptor-fetch path in MOV sreg (PE=1) | ✅ | `fb45845` | 2026-05-11 |

### Track 1 — End-to-end activation (Sprints 27.13a/b)

| Sprint | Status | Commit | 完成日 |
|---|---|---|---|
| 27.13a Protected-mode entry demo + ea_base consumer gap surfaced | ✅ | `c5f51a0` | 2026-05-11 |
| 27.13b Migrate ModR/M consumers to ea_base — gap closed | ✅ | `ed4b2d4` | 2026-05-11 |

After 27.13b: `MSW.PE = 1` produces visible behavioral change in
running programs — `27-pmode-entry.com` reads `BX=0xF1B8` from
descriptor base 0x100 instead of `BX=0x0080` from real-mode
`(sel << 4)` fallback.

### Track 2 — Exception model (Sprints 27.11a → 27.11f)

| Sprint | Status | Commit | 完成日 |
|---|---|---|---|
| 27.11a Exception state slots (EXC_PENDING / VECTOR / ERROR) | ✅ | `7249bb8` | 2026-05-11 |
| 27.11b Expose EXC_* in X86State + verbose CLI dump | ✅ | `0f2e6f2` | 2026-05-11 |
| 27.11c P-bit check + #NP fault (first end-to-end fault path) | ✅ | `bb790bd` | 2026-05-11 |
| 27.11d NULL-selector → #GP for SS load (PE=1) | ✅ | `7c57f5a` | 2026-05-11 |
| 27.11e DPL/RPL/CPL privilege check → #GP | ✅ | `1284d4f` | 2026-05-11 |
| 27.11f Segment-type check (SS=writable-data, DS/ES≠system) | ✅ | `92176e3` | 2026-05-11 |

After 27.11f: 4-baseline-check fault model live (P / NULL-SS / DPL /
type), all sharing the `EmitRaiseException` helper for consistent
EXC_* slot population.

### Closure

| Sprint | Status | Commit | 完成日 |
|---|---|---|---|
| 27.14 Phase 27b closure docs + 5-ROM fault matrix demo | ✅ | `4066b66` | 2026-05-11 |

### Deferred to a future phase

| Sprint | Why deferred |
|---|---|
| 27.12 TSS task switching | Multi-day. Requires TSS descriptor type + busy-bit toggle + state save/restore IR. Additive on top of `EmitSegCacheUpdate` + `EmitRaiseException`; demos do not need it. |
| LDT (TI=1) descriptor lookup | Falls through to GDT today. No demo loads LDT-based selectors. |
| CS load via far jump/call/iret in PE=1 | Different IR shape (privilege-transition + conforming/non-conforming code descriptors + possibly call gates). MOV sreg covers ES/SS/DS only. |
| Visible-sreg rewind on fault | Visible field updates before fault check fires; bounded by EXC_PENDING. Detailed in closure note "Architectural drift acknowledged". |
| Code-segment readable subcheck for DS/ES | Loading code into DS via MOV is unusual; no demo exercises it. Would be a ~30-line extension to 27.11f. |
