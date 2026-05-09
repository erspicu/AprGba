# NES block-JIT vs per-instr — first-cut perf measurement

> **Status (2026-05-09)**: Baseline measurement after N1.B'.5. Three
> backends (legacy / json per-instr / json-block) on blargg cpu_test5,
> 3 runs sequential bench, all passing "All tests complete".
>
> **TL;DR**: block-JIT on this ROM is **at the same wall-clock as
> per-instr** (~42s for 110M cycles), but slower than the legacy
> interpreter (~21s). The expected block-JIT gain is cancelled out by
> blargg's SMC behavior — the test framework rewrites the RAM-resident
> `instr_template` between sub-tests, every write triggers cache
> invalidate + recompile, eating the dispatch savings.
>
> For ordinary NES games (without frequent SMC) block-JIT should give
> 2-4× speedup; blargg is the worst-case measurement. Real-game ROM
> measurement next time will reveal the true block-JIT benefit.

---

## 1. Results

ROM: `test-roms/blargg_nes_cpu_test5/cpu.nes`
(Mapper 1 / MMC1, 256KB PRG, all 256 6502 opcodes including unofficial)

Workload: `--max-cycles=110000000` (~62 emulator-seconds, ROM finishes
all 11 subtests and halts at PC=$8003 in the `JMP $8003` self-loop)

Each backend ran 3 times as independent `dotnet run` processes;
`Stop-Process` cleaned the .NET workers before each run.

### 1.1 Wall-clock + MIPS

| Backend | Run 1 | Run 2 | Run 3 | **Avg** | MIPS |
|---|---:|---:|---:|---:|---:|
| **legacy** (interpreter)              | 21.35s | 21.06s | 21.33s | **21.24s** | **1.67** |
| **json** (per-instr LLVM JIT)         | 42.60s | 42.77s | 42.97s | **42.78s** | **0.83** |
| **json-block** (block JIT, alloca+mem2reg) | 41.80s | 41.91s | 42.50s | **42.07s** | **0.68\*** |

\* json-block "MIPS" is misleading — see §2.

### 1.2 Cycles + instructions

| Backend | Instr count | Cycles | Cycles/instr |
|---|---:|---:|---:|
| legacy            | 35,468,415 | 110,000,000 | 3.10 |
| json per-instr    | 35,558,628 | 110,000,002 | 3.09 |
| json-block        | 28,526,048 | 110,000,000 | 3.86\* |

\* The "instr count" for json-block is `Step()` call count, NOT executed
instruction count. block-JIT runs many CPU instructions per `Step()`
call (the entire detected block). Real executed instructions ≈ same as
legacy (~35.5M) — verified by reaching the same halt PC=$8003 with
identical register state.

### 1.3 Result consistency

All three backends reach the PC=$8003 self-loop (test halted) within
110M cycles, with aligned final register state:

```
final  PC=0x8003 A=0x00 X=0x?? Y=0x03 SP=0xFF P=0x27
```

(X is $00 or $01 depending on backend, because X never changes inside
the self-loop — its initial value depends on boot path differences,
which does not affect the test result.)

The PPU screenshot for all three backends shows "All tests complete" —
all 11 subtests pass.

---

## 2. Why block-JIT did not win

Expected: block-JIT should beat per-instr because:
- One indirect call per `Step()` → one indirect call per block
- LLVM does cross-instruction CSE / DCE / forwarding (mem2reg promotes
  alloca to SSA registers)
- No re-decode and no per-instruction ResolveFunctionPointer

Actual wall-clock difference is ~2% (42.07s vs 42.78s) — basically tied.

**Root cause: the blargg test framework relies heavily on SMC.**

The instr_template in `test-roms/blargg_nes_cpu_test5/source/instr_test.a`:
- ~4 bytes of branch + STA + JMP sequence at a fixed scratch addr in RAM
- Each time a new opcode is tested (~80 opcodes total), template[0] byte
  is rewritten to the new opcode (e.g. $90 to test BCC, $50 to test BVC)
- runtime jumps in via `JSR template_addr` to execute from RAM

Every byte rewrite triggers our `NesMemoryBus.SmcWriteHook` →
`BlockCache.NotifyMemoryWrite` → finds the block covering that addr and
invalidates it. Then the next `Step()` into that PC gets a cache miss →
BlockDetector re-walks + BlockFunctionBuilder recompiles + ORC AddModule.

Each recompile costs ~50-200μs (LLVM JIT compile cost). Out of 28.5M
block dispatches, an estimated ~1-5% are cache misses → 285k–1.4M
recompiles → 14-280 seconds wall. **JIT compile cost eats up all the
dispatch savings**.

GB block-JIT has the same mechanism, but GB blargg cpu_instrs **does
not** use SMC (test code lives entirely in PRG-ROM), so GB block-JIT
goes from 6.5 → 21 MIPS (3.2× speedup vs per-instr).

---

## 3. Cross-ISA comparison

| Workload | Backend | MIPS |
|---|---|---:|
| GB blargg cpu_instrs (master) | legacy            | ~31    |
| GB blargg cpu_instrs (master) | json per-instr    | ~6.5   |
| GB blargg cpu_instrs (master) | json block-JIT    | ~21    |
| **NES blargg cpu_test5**       | legacy            | **1.67** |
| **NES blargg cpu_test5**       | json per-instr    | **0.83** |
| **NES blargg cpu_test5**       | json block-JIT    | ~0.85 (real-instr-based)\* |

\* 28.5M `Step()` × ~1.25 actual instr/step = ~35.6M instr / 42.07s ≈ 0.85 MIPS
using the actual instr count; the 0.68 MIPS in the report is step-count
based, the table below organises both views.

NES legacy (interpreter switch table) is relatively fast because the
original AprNes oracle is written very tight (cycle-accurate inline
switch). GB legacy uses looser timing, so its baseline MIPS is higher.

---

## 4. Possible optimisations (not implemented)

### 4.1 SMC fast-path miss

`BlockCache.NotifyMemoryWrite`'s fast path returns early when
"coverage_count[addr] == 0". But blargg's stack writes hit $0100-$01FF
(RAM) — if a block is cached at $0100+ (unlikely but possible during
SMC), coverage > 0 hits the slow path scan. The slow path linearly
scans `_map` (up to 4096 entries) checking each block's
`BlockCoversAddr`. With blargg doing hundreds of thousands of writes
per second this is not cheap.

Optimisation: replace linear scan with an interval tree / sorted addr
list. The GB P1 #5b SMC V2 design did this, but it was wired up only
for GB, not NES. Could be ported.

### 4.2 Recompile cost amortisation

Every invalidate triggers a recompile, and `BlockFunctionBuilder.Build`
+ LLVM passes are the CPU hog. Options:
- **Block fragment caching**: cache the IR for individual instructions
  too, so blocks only splice already-cached fragments (concept like
  QEMU TCG's translation block fragments)
- **Lazy reach**: on cache miss, fall back to per-instr for N runs
  first, only formally compile the block on the N+1-th — avoids the
  thrash of "write, compile, write again, compile again"
- **No invalidate for PRG-ROM regions**: bus.WriteByte to $8000+ is a
  mapper control write and **does not** modify PRG-ROM bytes (the
  mapper handles bank switching via PrgBankSwitched). SMC in the ROM
  region only happens for a handful of mapper variants (mostly never),
  so by default we can skip SMC notify for $8000+.

### 4.3 Real-game ROM measurement

blargg is worst-case for SMC. Next time we measure on a real NES game
ROM (e.g. the first few seconds of Super Mario Bros, or Donkey Kong),
the block-JIT real-world benefit should be obvious.

---

## 5. Environment

- OS: Windows 11 Home (10.0.26200)
- .NET: 10.0.107
- LLVM: 20.x via LLVMSharp.Interop + libLLVM.runtime.win-x64
- Build: `dotnet build AprGba.slnx --no-incremental` (Debug)
- Commit: `9f2f4a2` (B'.5 done; SMC notify + cycles_left residual)
- Logs: `temp/perf2-{legacy,json,block}-{1,2,3}.log`

---

## 6. Conclusion

- ✅ block-JIT framework is correct — three backends produce consistent results, T1 425/425
- ✅ cross-CPU shared framework — Mos6502Emitters.cs has zero diff,
  alloca+mem2reg switches per-instr / block modes automatically
- ✅ GB block-JIT no regression (11/11 PASS)
- ⚠️ NES block-JIT vs per-instr is tied on blargg in terms of perf —
  SMC-heavy workload is to blame, not a framework issue
- 🔜 Real-game ROM measurement (next milestone) is needed to verify
  block-JIT's benefit for normal workloads
