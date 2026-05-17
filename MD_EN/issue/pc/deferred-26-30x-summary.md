# Phase 26-30.x — Deferred items summary

> **Compiled**: 2026-05-17
> **Scope**: Intel PC emulator (AprPc) + i80286 / 8087 / FDC/DMA / verifier
> framework. All deferred / TODO / out-of-scope items from Phase 26-30.x
> design docs, gathered in one place for prioritisation.
>
> **Source**: this file is produced by auditing the 8 design docs
> `MD/design/26-i80286-realmode-plan.md` through
> `MD/design/30.18-gb-fuzzer-bug-339-investigation.md` plus closure note
> `MD/performance/202605171533-verifier-and-fuzzer-closure.md`
> (audit result: 0 silent landings, 0 broken commit hashes).

## Priority overview

| Tier | Item | Knock-on effects |
|---|---|---|
| **P0 high-leverage** | 30.10 block-JIT + FPU correctness | Also resolves 28.8x + 30.9 interactive perf; FreeDOS interactive ~500K → 30M+ inst/s |
| **P0 high-leverage** | 5.7 x86 fuzzer | Completes the "4-CPU full fuzzer" claim; most likely to surface more emitter bugs |
| **P1 framework insurance** | 5.8 CI gate | Regression net for BlockFunctionBuilder / `*Emitters.cs` changes |
| **P1 framework** | Slave PIC at 0xA0 | Unblocks CheckIt / AT-class ROM; 2-3 day work |
| **P2 ISA completion** | 27.12 TSS task switching | Multi-day; no demo currently requires |
| **P2 ISA completion** | 29 DC/DA/DE/DF FPU family | DOS floating-point programs |
| **P3 optional** | 28.10/28.11 mouse + PC speaker | Optional in original plan |
| **P3 optional** | 30 WRITE/FORMAT FDC | Read-only floppy already covers boot use case |

(Detailed table see 中文版 [`MD/issue/pc/deferred-26-30x-summary.md`](../../../MD/issue/pc/deferred-26-30x-summary.md).)

## Cross-references

- `MD/design/26-i80286-realmode-plan.md`
- `MD/design/27-i80286-completion-plan.md`
- `MD/design/28-intel-pc-emulator-plan.md`
- `MD/design/29-x87-fpu-plan.md`
- `MD/design/30-fdc-dma-plan.md`
- `MD/design/30.15-blockjit-pc-investigation.md`
- `MD/design/30.15d-verified-blockjit-framework-design.md`
- `MD/design/30.18-gb-fuzzer-bug-339-investigation.md`
- `MD/performance/202605171533-verifier-and-fuzzer-closure.md`
