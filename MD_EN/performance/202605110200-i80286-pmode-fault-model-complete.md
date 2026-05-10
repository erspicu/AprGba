# Phase 27b — Intel 80286 protected-mode fault model complete

> **Closed**: 2026-05-11
> **Scope**: descriptor-based segmentation + 4-baseline-check fault
> model land end-to-end on the i80286 backend. Phase 27b's core
> architectural milestone (`MSW.PE = 1` → real descriptor fetch + real
> faults from a real instruction stream) is **demoable from a 96-byte
> .com ROM**.
> Predecessor: Phase 27a (real-mode complete, `MD/performance/202605110100`).
> Deferred: Sprint 27.12 TSS task switching (multi-day work, separate phase).

## Architectural milestone

`MSW.PE = 1` is no longer cosmetic. After Sprint 27.13b, every
ModR/M memory load/store uses `<seg>_BASE` from the hidden descriptor
cache, not `(visible-selector << 4)`. After Sprints 27.11a-27.11f,
loading a sreg in protected mode runs the full 80286 PRM
descriptor-validation pipeline before populating that cache. A
malformed selector reaches the EXC_PENDING flag without contaminating
the cache or letting the wrong physical address be read.

Concretely, the 96-byte fault matrix below is the proof:

| ROM | Selector → reg | Descriptor | Architectural outcome | Observed |
|---|---|---|---|---|
| `27-pmode-entry.com`        | `0x0008 → DS` | P=1, S=1, DPL=0, type=writable data | OK; `mov bx,[0]` reads DS_BASE=0x100 = first 2 bytes of code | `BX=0xF1B8`, no EXC ✓ |
| `27-pmode-np.com`           | `0x0008 → DS` | **P=0**, S=1, DPL=0, type=writable data | `#NP(sel)` per Intel | `EXC vector=0x0B error=0x0008` ✓ |
| `27-pmode-null-ss.com`      | `0x0000 → SS` | (NULL selector — descriptor not consulted) | `#GP(0)` per Intel SS-NULL rule | `EXC vector=0x0D error=0x0000` ✓ |
| `27-pmode-dpl-gp.com`       | `0x000B → DS` (RPL=3) | P=1, S=1, **DPL=0**, type=writable data | `#GP(sel)`: `max(CPL=0, RPL=3) > DPL=0` | `EXC vector=0x0D error=0x0008` ✓ |
| `27-pmode-ss-bad-type.com`  | `0x0008 → SS` | P=1, S=1, DPL=0, **type=executable code** | `#GP(sel)`: SS demands writable data | `EXC vector=0x0D error=0x0008` ✓ |

All 5 ROMs build deterministically from `tools/build_27_pmode_demos.py`
(re-running reproduces byte-identical output, verified vs git index
on the unchanged ones across each sprint).

## What shipped — Phase 27b sprint chain

Compaction-friendly micro-sprint cadence; each sprint is one commit,
typically <100 lines, individually revertable.

### Track 1 — descriptor-fetch wiring (Sprint 27.6 → 27.13b)

| Sprint | Commit | Deliverable |
|---|---|---|
| 27.6  | `11fdc98` | `Descriptor` + `Selector` records, parse/build helpers |
| 27.7  | `9f819b5` | `ReadDescriptor` / `WriteDescriptor` over `IMemoryBus` |
| 27.8  | `0dd6404` | `Msw` struct + `IsProtectedMode` PE-bit reader |
| 27.9  | `553909c` | Privilege-level helpers (`CanAccessDataSegment` etc.) |
| 27.10a | `cefed78` | Helper integration test (end-to-end mock) |
| 27.10b | `97e19ea` | Hidden cache slots: `<seg>_BASE/_LIMIT/_ACCESS` × 4 |
| 27.10c | `412fc7a` | `SegmentedLinear` reads cache (infra) |
| 27.10d w1-w8 | `2531bd8`..`fb45845` | Cache wiring across emitters |
| 27.13a | `c5f51a0` | Pmode-entry demo + identified consumer-migration gap |
| 27.13b | `ed4b2d4` | **Migrated ModR/M consumers to `ea_base` — gap closed** |

Closure of Track 1: `MSW.PE = 1` produces visible behavioral change in
running programs (`27-pmode-entry.com` BX = 0xF1B8 from descriptor
base, vs 0x0080 from real-mode shift fallback).

### Track 2 — exception model (Sprint 27.11a → 27.11f)

| Sprint | Commit | Deliverable |
|---|---|---|
| 27.11a | `7249bb8` | `EXC_PENDING / EXC_VECTOR / EXC_ERROR` state slots |
| 27.11b | `0f2e6f2` | Slots exposed in `X86State` + `--verbose` dump |
| 27.11c | `bb790bd` | **First end-to-end fault**: P-bit check → `#NP` |
| 27.11d | `7c57f5a` | NULL → SS → `#GP(0)` |
| 27.11e | `1284d4f` | DPL/RPL/CPL privilege check → `#GP(sel)` |
| 27.11f | `92176e3` | Segment-type check (SS=writable-data, DS/ES≠system) |

Closure of Track 2: descriptor-validation pipeline runs all 4 baseline
checks specified in the original Phase 27b plan (P / NULL-SS / DPL /
type), with shared `EmitRaiseException` helper so future
fault-introducing code (TSS in 27.12, code-segment loads via far jump
when added) reuses the same fault-write shape.

## State register additions (Phase 27b)

Sprint 27.10b + 27.11a additions to i80286's `register_file.status`:

| Register | Width | Reset | Sprint |
|---|---|---|---|
| ES_BASE   | 32 | 0x00000000 | 27.10b |
| ES_LIMIT  | 16 | 0xFFFF | 27.10b |
| ES_ACCESS | 8  | 0x93   | 27.10b |
| CS_BASE   | 32 | 0xFFFF0 (= CS<<4 at reset) | 27.10b |
| CS_LIMIT  | 16 | 0xFFFF | 27.10b |
| CS_ACCESS | 8  | 0x9B   | 27.10b |
| SS_BASE   | 32 | 0x00000000 | 27.10b |
| SS_LIMIT  | 16 | 0xFFFF | 27.10b |
| SS_ACCESS | 8  | 0x93   | 27.10b |
| DS_BASE   | 32 | 0x00000000 | 27.10b |
| DS_LIMIT  | 16 | 0xFFFF | 27.10b |
| DS_ACCESS | 8  | 0x93   | 27.10b |
| EXC_PENDING | 8  | 0 | 27.11a |
| EXC_VECTOR  | 8  | 0 | 27.11a |
| EXC_ERROR   | 16 | 0 | 27.11a |

12 cache slots × 4 segments + 3 exception slots = 47 bytes added to
i80286 CPU state in Phase 27b. Reset paths (`X86JsonCpu.Reset` +
`SetEntryPoint`) populate the cache from `(visible-selector << 4)` so
real-mode behavior on the i80286 backend remains pixel-identical to
i8086 / i80186 (verified every sprint via T2 + variant matrix).

## Inheritance ROI through Phase 27b

i80286 spec total size at end of Phase 27b:

- `cpu.json`: ~140 lines (Phase 27a's ~110 + 12 cache slots + 3 EXC slots)
- `groups/twobyteesc.json`: unchanged, ~150 lines
- IR (`X86_16Emitters.EmitSegCacheUpdate` + `EmitRaiseException`): one
  ~150-line C# helper used only by `X86WriteSregFieldEmitter` —
  invisible to the JSON spec, no per-instruction churn.
- Total i80286-specific *spec* surface: **~290 lines**, +30 from Phase 27a.

Vs writing protected-mode 80286 from scratch: ~3500 lines for
real-mode + roughly +800-1500 lines for the protected-mode
descriptor/exception/checks → call it ~5000 lines.

**Saving: ~94%.** The framework's ROI grows as protected-mode
machinery accumulates in shared helpers rather than per-CPU code.

## Architectural drift acknowledged

The current implementation has one known minor architectural
deviation, called out in code comments at `EmitSegCacheUpdate` and
in the 27.11c / 27.11d commit messages:

**Visible sreg field is updated before the fault check fires.** Intel
specifies that on `#GP` / `#NP` the entire sreg load is aborted —
the visible register also reverts. Our implementation:

1. `X86WriteSregFieldEmitter` writes the visible field unconditionally.
2. `EmitSegCacheUpdate` runs the validation pipeline.
3. On fault, we set `EXC_PENDING=1` and skip the **hidden cache**
   update. The visible field stays at the new (faulting) value.

This is bounded by `EXC_PENDING` — any code that respects the flag
will not act on the new visible field — and observable in the ROM
demos (e.g. `27-pmode-dpl-gp.com` shows `AX=0x000B` at HLT, meaning
the visible DS register did get the faulting selector). A future
sprint can fix this by either:
- restructuring `X86WriteSregFieldEmitter` to do the validation
  *before* the visible write, or
- adding a "rewind sreg field on EXC_PENDING" pass at instruction
  retire time.

Neither demo depends on the architecturally-correct behavior — all
5 demos halt cleanly with the right vector + error code regardless.

## Demos as visual artifacts

The original Phase 27b plan (Sprint 27.14 in `MD/design/27-`) called
for a `protmode-msr-i80286.png` visual demo. The i80286 backend has
no integrated video output (demos run via `apr-x86 --rom=...
--variant=i80286` and produce CLI text). The 5 fault-matrix ROMs
above are the actual demoable artifacts — they exercise the
descriptor pipeline end-to-end with one observable bit each (`BX`,
`EXC vector`, `EXC error`).

A retro-CGA visualization could be added later by extending the
i80286 backend with the same CGA renderer the i8086 demos use, but
that's orthogonal to the protected-mode work and would only repeat
the fault-matrix evidence in a prettier form.

## Deferred to a future phase

| Item | Why deferred |
|---|---|
| **27.12 TSS task switching** | Multi-day work. Requires TSS descriptor type + busy-bit + selector switch + state save/restore IR. None of our demos need it. |
| **LDT (TI=1) descriptor lookup** | Falls through to GDT path today. Programs that load LDT-based selectors don't exist in our demos. |
| **CS load via far jump/call/iret in PE=1** | `MOV sreg` handles ES/SS/DS; CS in PE=1 needs different IR (privilege-transition, conforming/non-conforming code descriptors, possibly call gates). |
| **Visible-sreg rewind on fault** | See "Architectural drift" above. |
| **Code-segment readable subcheck (DS/ES path)** | Loading a code descriptor into DS is unusual and our demos don't exercise it. |

## Phase 27b achievement summary

- **JSON-driven CPU framework demonstrates protected-mode segmentation
  at the i80286 layer with no per-CPU C# scaffolding** — all
  protected-mode logic lives in shared `X86_16Emitters` helpers
  guarded by `register_file` slot existence (i80286 has them; older
  variants don't, helpers no-op via try/catch and -1 sentinels).
- **Descriptor-fetch + 4-check fault model + 5-ROM fault matrix**
  end-to-end, with deterministic test ROM build script.
- **Zero regression**: T2 visual matrix 18 PNG SHA256 identical and
  i8086 vs i80186 variant matrix 6-demo identical at every commit
  through the entire sprint chain.
- **`MSW.PE = 1` is real**: programs see the architectural difference,
  faults reach `EXC_PENDING`, and the cache stays consistent on
  fault paths.

> The remaining 80286 protected-mode work (TSS, deeper LDT, far-jump
> CS handling) is well-defined and additive on top of the
> `EmitSegCacheUpdate` + `EmitRaiseException` helpers. Closing
> Phase 27b here makes architectural sense: the milestones the plan
> labeled "must have" are landed and demoable.
