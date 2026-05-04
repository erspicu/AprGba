# GB block-JIT P0 — complete (Blargg cpu_instrs ALL 11 PASS)

> **Phase**: 7 GB block-JIT P0 completion milestone
> **Date**: 2026-05-04
> **Result**: Entire P0 tier shipped. Blargg `cpu_instrs.gb` (master ROM)
> in block-JIT mode passes all 11 subtests (including BIOS LLE boot).

---

## 1. P0 full step list + results

| Step | Subject | Commit | Result |
|---|---|---|---|
| **P0.1** | Variable-width `BlockDetector` + LR35902 length oracle | `fdce42c` | ✅ |
| **P0.2** | 0xCB prefix as 2-byte atomic instruction | `381595b` | ✅ |
| **P0.3** | Immediate baking via instruction_word packing | `da8cf91` | ✅ |
| **P0.4** | GB CLI `--block-jit` + Strategy 2 PC fixes | `5b4092f` | ✅ |
| **P0.5** | HALT/STOP block boundary | `c47d849` | ✅ |
| **P0.5b** | EI delay band-aid (deprecated by P0.6) | `771d170` | ✅ |
| **P0.5c** | `Lr35902Alu8Emitter.FetchImmediate` baking + `--diff-bjit` lockstep harness | `3617240` | ✅ |
| **P0.6** | Generic `defer` micro-op + AST pre-pass | `51c2921` | ✅ |
| **P0.6** step 3 | Cross-block defer body emit at block exit + per-instance JsonCpu state + Io[] diff check | `1268f12` | ✅ |
| **P0.7** step 1+2 | Bus sync extern (Write*WithSync) + Lr35902StoreByte sync exit | `0c001fc` | ✅ |
| **P0.7** step 3 | Generic `sync` micro-op + EI defer integration + cycle-deduct fix | `999f9eb` | ✅ |
| **P0.7b** | Conditional branch taken-cycle accounting | `f27450f` `7dd1e04` | ✅ |

---

## 2. Completion milestone — Blargg cpu_instrs PASSED

`apr-gb --rom=test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb
--cpu=json-llvm --block-jit --frames=10000`:

```
cpu_instrs

01:ok  02:ok  03:ok  04:ok  05:ok  06:ok  07:ok  08:ok  09:ok  10:ok  11:ok

Passed all tests
```

11 subtests:
- 01-special, 02-interrupts, 03-op_sp_hl, 04-op_r_imm, 05-op_rp,
  06-ld_r_r, 07-jr_jp_call_ret_rst, 08-misc_instrs, 09-op_r_r,
  10-bit_ops, 11-op_a_hl

**Including BIOS LLE boot also PASS** (`--bios=BIOS/gb_bios.bin`).

---

## 3. Perf numbers (3-run avg, MIPS)

### GB Blargg cpu_instrs.gb (master ROM, 10000 frames)

| Backend | MIPS | vs baseline |
|---|---|---|
| `--cpu=legacy` (no block-jit) | 55.49 | reference high |
| `--cpu=legacy --block-jit` (no-op for legacy) | 55.42 | (legacy ignores --block-jit) |
| `--cpu=json-llvm` (per-instr) | 9.04 | baseline |
| `--cpu=json-llvm --block-jit` | **22.64** | **+150%** vs per-instr |

**Block-JIT is ~2.5x the speed of per-instr**. Still 2.4x behind legacy
interpreter's 55 MIPS — expected P1 (block-local state caching, detector
cross unconditional B) closes that gap to within 50%.

### GBA loop100 (1200 frames, no BIOS)

| combo | per-instr | block-JIT | bjit/pi ratio |
|---|---|---|---|
| arm pi   | 8.13 | — | — |
| arm bjit | — | 8.70 | +7% |
| thumb pi | 8.23 | — | — |
| thumb bjit | — | 10.13 | +23% |

**GBA bjit dropped 11-16% from baseline (pre-P0.7)** — the taken-branch
cycle deduct added in P0.7b affected the GBA pre-exit BB. User accepts
the trade-off "correctness first, fix perf gradually". Perf followup
left to P1 phase.

---

## 4. Comparison: P0 start → P0 complete

| Stage | GB block-JIT MIPS | Blargg cpu_instrs |
|---|---|---|
| P0 start (no block-JIT) | N/A | per-instr 9 MIPS, all pass |
| P0.4 (block-JIT runs) | 10.5 | partial subtests pass |
| P0.5c (CP A,n bug fixed) | 10.6 | **01-special PASS** |
| P0.7 step 3 (sync micro-op) | ~20 | 01 + 02 PASS |
| **P0.7b complete** | **22.64** | **all 11 subtests PASS** |

From "block-JIT runs but half of Blargg fails" to "**full cpu_instrs PASS
+ +150% perf vs per-instr**".

---

## 5. Architectural wins

Generic framework mechanisms accumulated during P0 (not GB / LR35902 specific):

| Mechanism | Use | Generality |
|---|---|---|
| Variable-width `BlockDetector` + length oracle | Any 1/2/3-byte ISA | LR35902 + future 6502/Z80/x86 |
| Prefix sub-decoder dispatch (0xCB-style) | atomic prefix+sub-opcode compilation | LR35902 + Z80 (DD/FD/ED) + x86 (REX/VEX) |
| Strategy 2 PC reads (`PipelinePcConstant`) | block-internal PC reads as constant | ARM / Thumb / LR35902 / any spec |
| `defer` micro-op + AST pre-pass | 1-instr-delayed effects (EI/STI/...) | any CPU's delayed-effect instruction |
| `sync` micro-op | force block exit + re-check IRQ | any IRQ-mutator instruction |
| Bus sync flag (Write*WithSync extern) | MMIO-write-after IRQ-state-changed → block exit | any CPU's MMIO map |
| Conditional branch cycle deduct | taken vs not-taken cycle difference | any ISA with conditional branches |
| Lockstep diff harness (`--diff-bjit`) | per-instr vs block-JIT comparison debug tool | any spec |

**P0 not only pushed GB block-JIT to fully passing Blargg, but also built
a generic framework allowing future new CPUs adding block-JIT to not
touch core C# code (BlockDetector / BlockFunctionBuilder), only the spec.**

---

## 6. Design docs + Gemini consultation records

| Doc | Content |
|---|---|
| `MD/design/12-gb-block-jit-roadmap.md` | full P0/P1/P2/P3/P4 priority order |
| `MD/design/13-defer-microop.md` | `defer` mechanism design |
| `MD/design/14-irq-sync-fastslow.md` | Hybrid IRQ sync mechanism design |
| `tools/knowledgebase/message/20260503_202431.txt` | Gemini consultation: variable-width + prefix + narrow-int |
| `tools/knowledgebase/message/20260503_220938.txt` | Gemini consultation: generic delayed-effect mechanism |
| `tools/knowledgebase/message/20260503_224732.txt` | Gemini consultation: IRQ delivery granularity |

---

## 7. Unresolved / followup work

### 7.1 GBA bjit perf regression (~11-16%)

P0.7b's pre-exit BB affects ARM block-JIT's hot-path layout. Followups:
- Restructure IR for better LLVM optimisation
- Or move cycle accounting further into host loop (trade-off needs evaluation)
- Leave for P1 wrap-up to handle together

### 7.2 Lockstep diff still has minor drift

`--diff-bjit=N` after large step counts still detects PPU/scanline detail
timing drift. Does not affect any Blargg cpu_instrs subtest — pass rate
all-green. Mooneye and other stricter cycle-precise test suites may hit
it. Follow up as needed.

### 7.3 Per-instr backend `_eiDelay` path retained

V1 strategy: per-instr still uses old `lr35902_arm_ime_delayed` extern;
only block-JIT uses the new `defer` mechanism. V2 unifies both paths
(per-instr also uses the generic defer pending-action runtime mechanism)
— small effort but no urgent need.

---

## 8. Entering P1

After P0 completion, next stage in the roadmap:

- **P1 #5** Native i8/i16 + block-local state caching — all GPRs loaded
  into LLVM SSA at block entry, stored back to state buffer at exit
- **P1 #6** Detector cross unconditional B/JR/JP — block average length
  from 1.0-1.1 raised to 5-10, expected bjit speed to climb significantly
- **P1 #7** E.c IR-level memory region inline check — JIT inline region
  check skipping extern call

Expected after P1 completion: GB block-JIT MIPS from 22.64 raised to 40-60
(approaching legacy 55). GBA bjit should also benefit.
