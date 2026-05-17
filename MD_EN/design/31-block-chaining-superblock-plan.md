# Phase 31 — Block chaining + superblock JIT evolution plan

> **Status**: 📋 PLANNED (2026-05-17). Phase 30.18 verifier landed;
> all 4 CPUs run multi-million-block NoDiff. The natural next perf
> direction is to push the compilation unit from single basic block to
> **chained blocks** and **superblock**.
>
> **Motivation**: current block-JIT caps at 64 instr but actual blocks
> end at first branch, typically 5-30 instr. Every block exit costs a
> managed→native trampoline + `BlockCache` lookup + dispatcher
> overhead. In hot loops this dominates.
>
> **Reference**: see Chinese version [`MD/design/31-block-chaining-superblock-plan.md`](../../MD/design/31-block-chaining-superblock-plan.md)
> for the full plan with sprint breakdown, risks, and industry refs.
> This file is a brief English summary mirror.

## Evolution ladder

| Tier | Name | Expected throughput gain | Effort | Spec-driven impact |
|---|---|---|---|---|
| **31.1** | Block chaining (patch exit jump → next block entry) | hot loop **2-3×** | ~4 day | None (pure dispatcher change) |
| **31.2** | Conditional-branch follow (full superblock) | tight branch **1.5-2×** | ~6 day | Small (extend BlockDetector + side-exit IR pattern) |
| **31.3** | Hot-loop detection + re-compile | unrolled loop **2-5×** | ~10-15 day | Medium (profiling state + new emitter pass) |
| **31.4** | Trace JIT | hot path unknown (high variance) | ~3+ weeks | Large (profile-driven, no longer purely spec-driven) |

**Recommendation**: 31.1 + 31.2 first. Re-evaluate 31.3 after. 31.4 not
recommended (no JS-style polymorphism in CPU emulation; QEMU / Dynarmic /
Dolphin all skip it).

## Why not method-based / trace JIT

- **Method JIT**: CPU emulation has no explicit function boundary
  (`CALL`/`RET` can target anywhere, `POP CS` rewrites return target,
  interrupts enter from any PC).
- **Trace JIT**: solves dynamic-language polymorphism we don't have;
  CPU code shape is regular enough that superblock specialization
  captures most of the gain.

## Phasing recommendation

```
Phase 31 (next quarter):
├─ 31.1 Block chaining        ← do first, highest ROI, smallest spec impact
└─ 31.2 Cond branch follow    ← after 31.1

Phase 32+ (future):
├─ 31.3 Hot-loop re-compile   ← only if 31.1 + 31.2 leave perf gap
└─ Code cache flush mechanism ← bundled with 31.3
```

## Cross-references

- [`MD/design/12-gb-block-jit-roadmap.md`](../../MD/design/12-gb-block-jit-roadmap.md) — single-block JIT plan (prerequisite)
- [`MD/design/14-irq-sync-fastslow.md`](../../MD/design/14-irq-sync-fastslow.md) — IRQ sync mechanism (superblock must still respect)
- [`MD/design/30.15d-verified-blockjit-framework-design.md`](../../MD/design/30.15d-verified-blockjit-framework-design.md) — verifier framework (chain + superblock must be compatible)
- [`MD/issue/pc/deferred-26-30x-summary.md`](../../MD/issue/pc/deferred-26-30x-summary.md) — Phase 26-30.x deferred items
