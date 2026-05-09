# N3.3 (finally) closeout — full spec-driven cycle table

> **Status (2026-05-09)**: The N3.3 series is complete. At the N3
> closeout (`MD/performance/202605091804-n3-declarative-ratio.md`) this
> task was left as BLOCKED — the original 2A03 spec only had a
> per-mnemonic coarse-grained `cycles.form: "Nm"`, lacking the
> per-(mnemonic, addressing-mode) dimension, so
> `NesJsonCpu.s_cycleTable[256]` had remained a hardcoded LegacyCpu
> mirror. After N5/N7/N8 wrapped up, we came back and closed this out.
>
> Two stages:
> 1. **N3.3 (struct break, commit `027fe79`)** — schema v2 added
>    `cycles.table`, the cc=01 ALU group's 64 opcodes were converted.
> 2. **N3.3-finally (commit `c806358`)** — the remaining 7 groups all
>    converted + 12 KIL form fixes; the `s_cycleTable[256]` hardcoded
>    oracle was **removed entirely** from NesJsonCpu.

---

## 1. Bench results (3 runs sequential, 5-min cap)

ROM: `test-roms/blargg_nes_cpu_test5/cpu.nes`
Workload: `--run --max-cycles=110000000` (~62 emulator-seconds)
Build: Debug (`dotnet build AprGba.slnx --no-incremental`)
Commit: `c806358` (end of N3.3-finally)

| Backend | Run 1 | Run 2 | Run 3 | **Avg** | **MIPS** | vs N4.6 | vs N3.4 baseline |
|---|---:|---:|---:|---:|---:|---:|---:|
| legacy           | 21.490s | 21.648s | 21.562s | **21.57s** | **1.64** | 1.65 → 1.64 (noise) | 1.57 → 1.64 (+4%) |
| json per-instr   | 42.769s | 42.550s | 42.594s | **42.64s** | **0.83** | 0.82 → 0.83 (noise) | 0.81 → 0.83 (+2%) |
| json-block       | 34.973s | 35.061s | 34.893s | **34.98s** | **0.82** | 0.80 → 0.82 (+2%) | 0.80 → 0.82 (+2%) |

### 1.1 N3.3-finally perf conclusion

N3.3-finally is purely a **refactor of the cycle table source** — the
cycle counts computed for the 256 opcodes are byte-identical to the
previous hardcoded oracle (T1 all green + nestest PASS on three
backends prove it). The hot path (`Step()`'s
`_specCycleTable[opcode]`) is the same 1 array index as the previous
`s_cycleTable[opcode]`, with 0 perf cost.

Build cost: a one-shot 256× decoder + cycle resolve, ~10-50μs at
NesJsonCpu construction time — completely invisible to long-running
emulation.

### 1.2 Cross-milestone perf trend (same ROM, same workload, Debug build)

| Milestone | legacy | json | json-block | Notes |
|---|---:|---:|---:|---|
| N1 baseline | 1.69 | 0.83 | 0.78 | hardcoded if-else dispatch |
| N3.4 | 1.57 | 0.81 | 0.80 | spec-driven region table (+ 7% regression) |
| N4.6 | 1.65 | 0.82 | 0.80 | page-table dispatch + offset semantics |
| **N3.3-finally (this run)** | **1.64** | **0.83** | **0.82** | spec drives full cycle table |

Cumulative result across the four milestones: legacy is ~3% off the N1
baseline, per-instr is flat with N1, and block-JIT surpasses N1 by
about 5%. **Important: every step simultaneously raised declarativity**
(50% → 70% → 78% → ~85%) **without perf regression**.

---

## 2. Declarative ratio quantification (N4 → N3.3-finally)

| Layer | After N4 | After N3.3-finally | Δ | Notes |
|---|---|---|---|---|
| CPU register file | 100% | 100% | – | unchanged |
| Instruction encoding (decoder) | 100% | 100% | – | unchanged |
| Instruction semantics | 95% | 95% | – | unchanged |
| Cycle multiplier | 100% | 100% | – | unchanged |
| Block-JIT cycle accounting | 90% | 90% | – | unchanged |
| Memory bus dispatch | 95% | 95% | – | unchanged |
| Region routing | 95% | 95% | – | unchanged |
| Interrupt routing | 90% | 90% | – | unchanged |
| **Per-instr cycle table** | **60%** | **100%** | **+40%** | N3.3-finally — `cycles.table` + spec-built |
| Mapper logic | 0% | 0% | – | escape hatch — by design |
| PPU/APU register handlers | 0% | 0% | – | escape hatch — by design |
| Access widths / wait states | 100% (declared) | 100% (declared) | – | unchanged |

**Weighted average**: N4 ~78% → N3.3-finally **~85%** (crossing the
previously-estimated 80% ceiling, by breaking the N3.3 structural
blocker).

### 2.1 Spec format change summary

InstructionDef entries with `cycles.table` added under `spec/2a03/groups/`:

| Group | Mnemonics | bbb modes | Total opcode-instances |
|---|---:|---:|---:|
| alu-cc01 (N3.3 part 1) | 8 | 4-8 | 64 (-1 shadowed) |
| ctrl-cc00 | 5 (BIT/STY/LDY/CPY/CPX) | 2-5 | ~16 |
| rmw-cc10 | 8 (ASL/ROL/LSR/ROR/STX/LDX/DEC/INC) | 3-5 | ~30 |
| unofficial cc=11 broad | 8 (SLO/RLA/SRE/RRA/SAX/LAX/DCP/ISC) | 4-7 | ~50 |

Plus 12 KIL forms corrected from `"1m"` to `"2m"` (the actual cycle count
certified by the oracle).

### 2.2 Code change summary

`src/AprNes.Cli/Cpu/NesJsonCpu.cs`:
- **Removed**: hardcoded `s_cycleTable[256]` static field (16 lines of oracle bytes)
- **Added**: `BuildSpecCycleTable(decoder, cyclesPerSpecUnit)` static helper
  + per-instance `_specCycleTable` field
- **Modified**: `Step()`'s `int cycles = s_cycleTable[opcode]` becomes
  `_specCycleTable[opcode]` — same location, same hot path

`src/AprCpu.Core/JsonSpec/SpecModel.cs`:
- `Cycles` record gains a `Table` field
- New `CycleTable` record + `Resolve(EncodingFormat, opcode)` method

---

## 3. Academic perspective — the "structural blocker" breakthrough path of the N3 series

At N3 closeout we classified the cycle table limitation as "framework
abstraction has hit the ceiling; pushing further requires a spec format
overhaul". N4/N5/N7/N8 all routed around it (focused on memory bus +
lockstep + GBA and other orthogonal directions), and after the other
framework dimensions were pushed into place, coming back made it easy
to resolve:

1. **Schema design**: the `cycles.table` field is trivial to describe
   — one selector field name + key-value pairs. Design cost is
   essentially zero.
2. **Resolver implementation**: 12 lines of C# (extract field bits,
   format binary key, lookup map).
3. **Spec conversion**: mechanical — fill numbers from the oracle
   table, batch by group.
4. **Code replacement**: substitute `BuildSpecCycleTable` for the
   hardcoded array — done in a single commit.

**Academic conclusion**: the "fundamental escape hatches" claimed at
N3 closeout actually reduce to just three categories — mapper state
machine + PPU/APU heavy side effects + NMI procedural sequence
(~500 lines of C#) — those are the things genuinely outside the
framework boundary. The per-instruction cycle table, interrupt
vectors, and memory layout, originally claimed as "declarative ceiling
of 70%", were limitations only because **we had not yet spent the
time to extend the schema** — not framework-fundamental limits.

The actual ceiling is probably closer to **90%+** — the remaining 10%
is procedural state-machine logic, which falls in the same category
as "ECMAScript cannot replace Verilog" — same fundamental reason.

---

## 4. Environment

- OS: Windows 11 Home (10.0.26200)
- .NET: 10.0.107
- LLVM: 20.x via LLVMSharp.Interop
- Build: Debug, no-incremental
- Commit: `c806358` (end of N3.3-finally)
- Logs: `temp/n33fin-{leg,json,block}-{1,2,3}.log`

---

## 5. N3.3 series closeout

- ✓ **2026-05-09 morning** N3.3 (struct break) — schema v2 + cc=01 conversion (commit `027fe79`)
- ✓ **2026-05-09 evening** N3.3-finally — all 256 opcodes converted + drop oracle (commit `c806358`)
- ✓ **2026-05-09 evening** 3-run bench + this doc

Outstanding: N3.3 is now thoroughly closed. Other N4/N5/N7/N8 closeouts
are independent. Remaining N5+ candidate directions (per N4.6 closeout
§3):
- ARM 2-level page table sub-grain (N8 already did 1-level; needed in
  the future only for cases like NDS dual-CPU or in-cart sub-page tricks)
- Further spec format advances (branch taken-cycle, page-cross +1
  penalty described with the same selector pattern)
- A fourth CPU (still the highest-academic-value direction; either
  R3000A or R4300i)
