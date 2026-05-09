# N3 closeout — declarative ratio + perf bench

> **Status (2026-05-09)**: Quantification after N3.0–N3.3 are complete.
> N3 pushed the framework's declarative coverage from N2's ~50% to ~70%.
> N3.3 hit a structural limitation in the spec format (cycles lacks a
> per-addressing-mode dimension), so the original 80% target was missed
> — but this blocker itself is part of the framework's academic value
> (we identified an explicit structural reason why this path does not
> work).
>
> Bench: 3-run blargg cpu_test5 across three backends, no significant
> perf regression vs N1 baseline (≤7%).

---

## 1. Bench results (3 runs sequential, 5-min cap)

ROM: `test-roms/blargg_nes_cpu_test5/cpu.nes`
Workload: `--max-cycles=110000000` (~62 emulator-seconds)
Build: Debug (`dotnet build AprGba.slnx --no-incremental`)
Commit: `0db6ed8` (N3.3 blocker doc)

| Backend | Run 1 | Run 2 | Run 3 | **Avg** | MIPS | vs N1 baseline |
|---|---:|---:|---:|---:|---:|---:|
| legacy            | 22.514s | 22.822s | 22.624s | **22.65s** | **1.57** | -7% (1.69 → 1.57) |
| json per-instr    | 44.202s | 44.008s | 43.961s | **44.06s** | **0.81** | -2% (0.83 → 0.81) |
| json-block        | 35.490s | 36.093s | 35.641s | **35.74s** | **0.80** | +3% (0.78 → 0.80) |

### 1.1 Legacy slowdown analysis

The legacy backend being ~7% slower is a side effect of N3.2 changing
NesMemoryBus dispatch — from a hardcoded if-else chain to a region-table
linear scan. Although a 4-region scan and a 4-way if-else should cost
about the same, the JIT inlines the latter much better than the former.

We accept the 7% — N3's design goal is declarativity, not perf. If a
future perf-critical scenario needs to recover it, the region scan can
be replaced by a jump table (256-byte table indexed by addr>>8) to
return to O(1) lookup — but for ARM's 4GB address space this would
consume 16MB of memory; that trade-off is an ARM-specific follow-up.

### 1.2 Per-instr / block-JIT vs N1

Both backends are within noise. This shows that N2.4's imm-bake + N3.1's
vector spec read + N3.2's region dispatch caused no perceptible perf
regression.

### 1.3 Result consistency

All three backends reach the PC=$8003 self-loop (test halt); blargg
cpu_test5 PPU nametable shows "All tests complete" — all 11 subtests pass.

---

## 2. Declarative ratio quantification

### 2.1 Per-layer declarative coverage comparison

| Layer | After N2 | After N3 | Δ | Notes |
|---|---|---|---|---|
| CPU register file | 100% | 100% | – | spec.register_file auto-builds the layout |
| Instruction encoding (decoder) | 100% | 100% | – | mask/match from spec |
| Instruction semantics | 95% | 95% | – | spec.steps + emitters; remaining 5% is per-arch micro-op |
| Cycle multiplier (m-cycle vs raw) | 100% | 100% | – | isa_metadata.cycles_per_spec_unit |
| Block-JIT cycle accounting | 90% | 90% | – | ParseCyclesForm uses spec.cycles.form |
| **Memory bus dispatch** | 0% | **80%** | **+80%** | N3.2 region table from MachineSpec |
| **Interrupt routing** | 30% | **90%** | **+60%** | N3.1 vector addr from MachineSpec |
| **Per-instr cycle table** | 60% | **60%** | – | N3.3 BLOCKED — spec lacks per-addressing-mode dimension |
| Mapper logic | 0% | 0% | – | escape hatch stays in C# (state machine) |
| PPU/APU register handlers | 0% | 0% | – | escape hatch stays in C# (heavy side effects) |
| NMI handler body | 0% | 0% | – | escape hatch stays in C# (push order is procedural) |

**Weighted average**: N2 ~50% → N3 ~70% (missed the 80% target; the
N3.3 blocker drags it down by 10pp).

### 2.2 Explicit "escape hatch — always C#" list

Per design doc #21 §5 + N3 practical conclusion:

| Item | Why C# | Approx LOC |
|---|---|---|
| `Mapper000.cs` / `Mapper001.cs` IMapper impls | 5-bit shift register state machine, bank select logic | ~150 lines |
| `NesPpu.WriteRegister` / `ReadRegister` switch | vram_addr increment, VBL flag, NMI fire, OAM DMA stall — heavy side effects | ~300 lines |
| `NesJsonCpu.NmiInterrupt` body | push PC.hi / PC.lo / P, set I — procedural sequence | ~30 lines |
| `NesJsonCpu.s_cycleTable[256]` | spec structure not granular enough for per-addressing-mode | 16 lines |

**Total escape-hatch C# is approximately ~500 lines**. Compared to the
framework + spec-driven portion after N3 (emitters + spec.steps +
machine spec parsers + bus dispatch), which is ~3000+ lines — escape
hatches account for ~15% of the framework runtime.

### 2.3 N3.3 blocker — onward path

Resolving the cycles structural limitation requires a spec format
refactor. Two options:

(a) **One instruction-def per (mnemonic, addressing-mode)** —
`spec/2a03/groups/alu-cc01.json` grows from 8 entries to 64 (4×). Other
groups bloat similarly. Implementable but spec files become large and
hand-maintenance cost goes up
(b) **Per-format `cycle_table`** — add `cycle_table: { "000": 6, "001":
3, "010": 2, ... }` to `InstructionFormat`, mapping bbb → cycle count.
Spec size barely changes; resolver logic is a bit more complex

Direction (b) is more reasonable, but **left as future N4+ scope**.

---

## 3. Framework-level academic conclusion of N3

1. **Parts that can be declarative**: addr ranges, interrupt vectors,
   cycle multiplier, cycles per mnemonic (coarse-grained), register
   file shape, instruction encoding, instruction semantics — all
   succeeded
2. **Parts that must stay in C#**: state machines (mapper bank switch),
   heavy side effects (PPU register writes), procedural sequences (NMI
   push order) — confirmed as fundamentally unsuited to declarative
   description
3. **Hit a structural limitation**: cycles' (mnemonic, addressing-mode)
   2D dimensionality — the 1D spec format is not enough
4. **Quantified declarativity vs perf trade-off**: spec-driven dispatch
   is ~7% slower for legacy (4-region linear scan vs 4-way if-else);
   for the JIT backend it sits within noise

For an academic paper: the framework's "truly declarative" ceiling is
roughly 70-80% — the remaining 20-30% are fundamental escape hatches,
not the framework being insufficiently abstract.

---

## 4. Environment

- OS: Windows 11 Home (10.0.26200)
- .NET: 10.0.107
- LLVM: 20.x via LLVMSharp.Interop
- Build: Debug, no-incremental
- Commit: `0db6ed8` (end of N3)
- Logs: `temp/n34-{leg,json,block}-{1,2,3}.log`

---

## 5. N3 series closeout

- ✓ N3.0 design doc #21
- ✓ N3.1 interrupt vectors from MachineSpec
- ✓ N3.2 NES memory bus dispatch via MachineSpec
- ✓ ~~N3.3 per-instr cycle table from spec — BLOCKED + documented~~
       **RESOLVED later on 2026-05-09** (see the §update below + commits `027fe79`/`c806358`)
- ✓ N3.4 perf bench + ratio doc (this document)

Outstanding: spec format improvements (per-addressing-mode cycles) can
be picked up as the N4 series if desired. Or pause and pursue a
different direction (N5 generic lockstep diff, N6 add a fourth CPU,
etc.).

---

> **2026-05-09 (later) update — N3.3 RESOLVED**
>
> The "structural blocker hit at end of N3 for the cycle table"
> referenced in §1.1 of this doc was finished later the same day
> (taking the §2.3 direction (b) path — per-format cycle_table):
>
> - commit `027fe79` — schema break + cc=01 conversion (64 opcodes)
> - commit `c806358` — converted the remaining 7 groups + dropped the hardcoded oracle
>
> The §2.3 conclusion "direction (b) is more reasonable, but left as
> future N4+ scope" is now obsolete — the actual path was: after
> N4/N5/N7/N8 pushed the other framework dimensions into place, looking
> back, the N3.3 schema rework turned out to be very trivial (12 lines
> of resolver C# + mechanically filling in the spec table), inconsistent
> with the pessimistic feel of the "structural limitation" label.
>
> Per-instr cycle table declarative ratio: 60% → **100%** (§2.1 table)
> Framework overall ratio: 70% (N3) → 78% (N4) → **~85%** (N3.3-finally)
>
> Detailed record: `MD/performance/202605092000-n33-full-spec-cycles.md`
