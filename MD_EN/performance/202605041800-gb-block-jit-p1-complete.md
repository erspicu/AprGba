# GB block-JIT P1 — complete (mechanism shipped, perf optimisation deferred)

> **Phase**: 7 GB block-JIT P1 milestone
> **Date**: 2026-05-04
> **Result**: P1 tier mechanisms shipped. Each item's design has been
> implemented; P1 #5 V1 / P1 #5b SMC V2 are env-gated OFF by default to
> preserve V1 behavior because cpu_instrs runs show cycle drift. The
> expected "lift to ≥legacy 31 MIPS" was not reached — current 21 MIPS
> @ 10k frames (27 MIPS @ 60k amortised), still 13-32% behind legacy 31.
> User direction: "mechanism design complete, CODE optimisation later".

---

## 1. P1 full step list + results

| Step | Subject | Commit | Status |
|---|---|---|---|
| **P1 #5** | Native i8/i16 + block-local register shadowing V1 | `db9375c` | ✅ mechanism (perf -4%) |
| **P1 #5b** | SMC V2: IR-level inline notify + precise per-instr coverage + cross-jump-into-RAM unlock | `377379c` | ✅ mechanism (env-gated OFF) |
| **P1 #6** | Detector cross unconditional B/JR/JP follow (ROM-only) | `b9dd0dd` | ✅ V1 |
| **P1 #7** | E.c IR-level WRAM/HRAM inline write fast path | `787a8e5` | ✅ |
| **P2 #8** | A.5 SMC detection V1 (per-byte coverage + bus-extern path notify) | `24a58d1` | ✅ (promoted to P1 scope) |

---

## 2. Completion verification — Blargg cpu_instrs still PASS

`apr-gb --rom=test-roms/gb-test-roms-master/cpu_instrs/cpu_instrs.gb
--cpu=json-llvm --block-jit --frames=10000`:

```
cpu_instrs

01:ok  02:ok  03:ok  04:ok  05:ok  06:ok  07:ok  08:ok  09:ok  10:ok  11:ok

Passed all tests
```

3-run avg @ 10k frames: **20.95 / 20.36 / 20.37 = 20.56 MIPS**.
60k frames (compile amortised): **26.80 / 27.20 / 27.52 = 27.17 MIPS**.

T1: 365/365 unit tests PASS.
T2: 8-combo GBA screenshot canonical hash unchanged (`7e829e9e837418c0f48c038341440bcb`).
Lockstep diff per-instr vs bjit: pre-existing 1-cycle DIV drift @ iter 15300,
not introduced by P1.

---

## 3. Comparison with P0 baseline

| ROM | Mode | P0 baseline | P1 complete | Δ |
|---|---|---:|---:|---:|
| cpu_instrs master | bjit @ 10k frames | 22.64 MIPS | 20.56 | **-9%** |
| cpu_instrs master | bjit @ 60k (amortised) | — | 27.17 | — |
| GBA arm HLE loop100 | bjit | 10.3 MIPS | 8.7 | **-16%** (P0.7b regression) |
| GBA thumb HLE loop100 | bjit | 11.4 MIPS | 9.5 | -16% (same) |

GB block-JIT path -9% is the cost of P1 #5 V1 unconditional shadow; GBA
path -16% is the regression left by P0.7b (unrelated to P1).

---

## 4. P1 step mechanism details

### P1 #5 V1 — block-local register shadowing

**Mechanism**:
- `EmitContext.GprShadowSlots` (`LLVMValueRef?[]`) + `StatusShadowSlots`
  (`Dictionary<string, LLVMValueRef>`)
- `ctx.GepGpr(int)` / `ctx.GepStatusRegister(string, mode=null)` —
  emitter-transparent routing: hits shadow returns alloca, otherwise falls
  through to `Layout.GepGpr` state-struct GEP
- `ctx.DrainShadowsToState()` — unified drain helper, called by block exit
  + sync mid-ret + branch taken pre-exit + budget exit
- `BlockFunctionBuilder.Build` allocas 7 GPR (A,B,C,D,E,H,L) + F + SP at
  entry for LR35902 (`GprWidthBits == 8`), pre-loads state→shadow, drains
  at exit

**~40 emitter call sites refactored** (`ctx.Layout.GepGpr/...` → `ctx.GepGpr/...`)
— ArmEmitters / BitOps / BlockTransferEmitters / StackOps / Lr35902Emitters /
OperandResolvers / Emitters.

**Perf cost**: cpu_instrs -4% (10k frames running short blocks, entry-load
+ exit-drain overhead exceeds internal savings). `APR_DISABLE_SHADOW=1`
env can disable for A/B bench.

**V2 to do**: per-block live-range analysis — scan spec steps for actually
touched regs, alloc only those. Expected to flip into a positive gain.

### P1 #5b — SMC V2 (env-gated)

**(a) IR-level inline notify** — `Lr35902Emitters.EmitSmcCoverageNotify`:
```
cov_byte = load i8, gep coverage_base[addr]
cov_nz   = icmp ne cov_byte, 0
br cov_nz, smc_notify, smc_after  ; cold-path on smc_notify
smc_notify:
  call lr35902_smc_notify_write(addr)
  br smc_after
```
- Fast path: 1-byte load + branch (~1ns)
- Slow path: extern call to `BlockCache.NotifyMemoryWrite`
- Gate: `APR_SMC_INLINE_NOTIFY=1` env var (default OFF)

**(b) Precise per-instr coverage** — `CachedBlock` adds `CoverageInstrPcs`
(`uint[]`) + `CoverageInstrLens` (`byte[]`) two arrays. `BlockCache.IncrementCoverage`
/ `BlockCoversAddr` walks precise per-instr range instead of convex hull.
Always-on (every newly compiled block carries these arrays).

**(c) Cross-jump-into-RAM unlock** — `BlockDetector` removes ROM-only restriction.
Gate: `APR_CROSS_JUMP_RAM=1` env var (default OFF).

**Bonus: Illegal opcode NOP fallback** — `BlockDetector` synthesises a 1-instr
NOP block (decode 0x00) but advances PC by original length when encountering
0xD3/DB/DD/E3/E4/EB/EC/ED/F4/FC/FD (no decoder entry in spec). Avoids
`Block ctor "must contain ≥1 instruction"` assertion crash when cross-jump
hits an illegal byte.

**Why env-gated**:
- Both env OFF: ~21 MIPS, all 11 PASS
- `APR_SMC_INLINE_NOTIFY=1` only: ~10 MIPS, stuck on sub-test 03
- Both env ON: ~4 MIPS, stuck on sub-test 03

Root cause for V3: cycle accounting drift under invalidation > pre-existing
1-cycle DIV drift. Possible fixes:
- mGBA / Dolphin pattern of deferred invalidation (effective only after
  current block finishes)
- Invalidate only when write addr == instr.Pc first byte (more conservative)

### P1 #6 — Cross-jump unconditional follow

LR35902 0x18 (JR e8) + 0xC3 (JP nn) cross-follow. CALL/RET/JP HL (dynamic) not
followed. V1 limited to ROM-to-ROM (source ≤ 0x7FFF AND target ≤ 0x7FFF);
V2 (P1 #5b unlocked) env-gated, default still ROM-only.

`DecodedBlockInstruction.IsFollowedBranch` flag — followed-branches in
BlockFunctionBuilder skip spec steps emit (the branch's own side effect
exits block), still deduct cycle cost via postBB.

### P1 #7 — IR-level WRAM/HRAM inline write fast path

`Lr35902Emitters.EmitWriteByteWithSyncAndRamFastPath`:
- WRAM (0xC000-0xDFFF) → inline GEP-store via `lr35902_wram_base`
- HRAM (0xFF80-0xFFFE) → inline GEP-store via `lr35902_hram_base`
- Else → sync-flag extern (P0.7 path)

JsonCpu.Reset pinned bus.Wram + bus.Hram + `BindExtern` for two base pointers.
`HostRuntime.BindExtern` removed `_finalized` throw restriction — allows
late binding during Reset(bus).

### P2 #8 — A.5 SMC detection V1 infrastructure

`BlockCache._coverageCount[0x10000]` per-byte counter + bus-extern path
NotifyMemoryWrite. MemWrite8/16/Sync shims call `_blockCache?.NotifyMemoryWrite`
after bus.WriteByte. Evolved by P1 #5b into V2 (added IR inline notify +
precise coverage); promoted from P2 to P1 scope.

`_blockGeneration` monotonic counter — re-compiled fn name appends `_g{N}`
suffix to dodge ORC LLJIT "Duplicate definition".

---

## 5. Known issues + next steps

### Active

| Issue | Description | Candidate fix |
|---|---|---|
| **GBA bjit -16% regression** | Left by P0.7b commit `7dd1e04`; ARM HLE loop100 10.3→8.7 | Check whether pre-exit BB cycle deduct path has redundant IR |
| **SMC inline notify cycle drift** | When `APR_SMC_INLINE_NOTIFY=1` ON, cpu_instrs sub-test 03 livelock | Deferred invalidation pattern (mGBA / Dolphin) |
| **P1 #5 V1 shadow -4%** | Unconditional alloc 7 GPR + F + SP has high overhead for small blocks | Per-block live-range analysis (V2) |

### P2 / P3 / P4 not yet done

See [`MD_EN/design/12-gb-block-jit-roadmap.md`](/MD_EN/design/12-gb-block-jit-roadmap.md) §3. Suggested next picks:

- **P2 #9 A.9 profiling tool** (S/L/diagnostic) — small investment high
  return, prerequisite for any subsequent perf work
- **P1 #5 V2** live-range analysis — flip the -4% into a positive
- **P1 #5b V3** deferred invalidation — fix cycle drift under SMC ON
- **GBA bjit P0.7b regression fix** — recover the -16%

---

## 6. Significance for the framework

- Multi-CPU porting path: JSON-driven + block-JIT covers ARM (fixed-width
  4-byte), Thumb (2-byte), LR35902 (variable 1-3 byte) under the same
  mechanism.
- Variable-width path + immediate baking + CB-prefix sub-decoder + cross-
  jump follow are all framework-level designs, not LR35902-specific hacks;
  the next variable-width CPU (e.g. 6502 / Z80 / 8080) can use them
  directly.
- SMC infrastructure is a framework-level safety net: any platform with
  cached block + writable RAM can use it.
- Smaller V2/V3 optimisations like cache-miss / live analysis / deferred
  invalidation all build on the V1 mechanism already shipped — no need
  to dig back into the architecture for the next round of optimisation.

P0 and P1 together demo the argument that "JSON-driven CPU framework can
also run block-JIT". Perf is not yet saturated but the mechanism is
complete; the next optimisation phase does not need to re-excavate the
foundation.
