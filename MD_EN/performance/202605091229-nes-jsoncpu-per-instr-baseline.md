# NES JsonCpu per-instr — performance baseline (pre-block-JIT)

> **Purpose**: After N1.A is done, take LegacyCpu (interpreter oracle) and
> NesJsonCpu (per-instr LLVM JIT) and run them on the same ROM to measure
> baseline MIPS. Record this number before block JIT (N2) starts so that
> later block-JIT progress has a perf reference point — same measurement
> path as the GB block-JIT (per-instr → block).
>
> **Result**: legacy 1.69 MIPS, json per-instr 0.83 MIPS (~50% slower).
> per-instr dispatch overhead is the dominant cost — every instruction
> pays one indirect call + decoder lookup + cycle accounting, and even
> though the IR body is LLVM-optimised native it cannot make up for it.
> Block JIT is the perf answer.

---

## 1. Results (3 runs each, sequential / no overlap)

ROM: `test-roms/blargg_nes_cpu_test5/cpu.nes`
(Mapper 1 / MMC1, 256KB PRG, all 256 6502 opcodes including 105 unofficial)

Workload: `--max-cycles=110000000` (~62 emulator-seconds, covers the full
self-loop end of `Running tests... → 11-special → All tests complete`)

| Run | **legacy** (interpreter) | **json** (LLVM JIT per-instr) |
|-----|--------------------------:|------------------------------:|
| 1   | 20.928s — 1.69 MIPS       | 42.619s — 0.83 MIPS           |
| 2   | 21.191s — 1.67 MIPS       | 42.529s — 0.84 MIPS           |
| 3   | 20.936s — 1.69 MIPS       | 42.447s — 0.84 MIPS           |
| **avg** | **21.018s — 1.69 MIPS** | **42.532s — 0.83 MIPS** |
| variance | ±1.3%                  | ±0.4%                          |

Instruction count:
- legacy: 35,468,415
- json:   35,558,628 (+0.25%; minor cycle-accounting differences)

Both backends reach the `PC=0x8003` self-loop (test halt state) and the
blargg `All tests complete` result is identical. Semantics aligned.

## 2. Measurement procedure

Each run is an independent `dotnet run` process (**not** a for-loop
inside a single bash call — measuring that way gets polluted by .NET
worker build cache + GC state). Before measuring, `Stop-Process
VBCSCompiler` to make sure the .NET build server is not hogging
resources in the background.

```
dotnet build AprGba.slnx --no-incremental    # ensure Release / no incremental staleness
PowerShell> Stop-Process -Name VBCSCompiler -Force -ErrorAction SilentlyContinue
# then one dotnet run at a time, independent processes
dotnet run --project src/AprNes.Cli --no-build -- \
    --rom=test-roms/blargg_nes_cpu_test5/cpu.nes --run \
    --max-cycles=110000000 --backend=legacy
```

Logs at `temp/perf-{legacy,json}-{1,2,3}.log`.

## 3. Why json per-instr is so much slower

per-instr StepOne hot path:

```
ushort pc = ReadI16(_pcOff);              // load from pinned state buffer
byte opcode = _bus.ReadByte(pc);          // virtual call → bus dispatch
WriteI16(_pcOff, (ushort)(pc + 1));        // store
var decoded = _mainDecoder.Decode(opcode); // dictionary / array lookup
var fnPtr = _fnPtrByDef[decoded.Instruction]; // identity-keyed cache hit
var fn = (delegate* unmanaged[Cdecl]<byte*, uint, void>)fnPtr;
fn(_statePtr, opcode);                     // ★ indirect function pointer call
long cycles = CyclesFor(decoded.Instruction);
cycles += ConditionalBranchExtraCycles(opcode, ...);
_bus.Tick(cycles);                         // virtual call → PPU catch-up
```

Every NES instruction pays at minimum:
- 1 indirect function pointer call (call → ret, flushes inline cache)
- 2 virtual calls (`bus.ReadByte`, `bus.Tick`)
- 1 dictionary lookup + 1 PC store + 1 cycle add
- The LLVM-optimised instruction body itself runs extremely fast (memory
  ops go through host extern; ALU ops are direct IR) — but only accounts
  for <30% of total time

LegacyCpu's corresponding hot path:

```
opcode = Mem_r(r_PC++);                   // direct array index after sealed override
cpu_cycles = cycle_table[opcode];
switch (opcode) {                          // ★ jump table — predicted, inlined
    case 0xA9: r_A = Mem_r(r_PC++); ...   // entire op = ~5 ALU + flag ops
    ...
}
```

A big switch is friendly to the modern CPU branch predictor (each hot
opcode has its own branch slot), and case bodies inline directly without
any indirect call.

## 4. Comparison with GB

| Workload                      | Backend     | MIPS  |
|-------------------------------|-------------|-------|
| GB blargg cpu_instrs 09-op r  | legacy      | ~3.5  |
| GB blargg cpu_instrs 09-op r  | json per-instr | ~16  |
| GB blargg cpu_instrs 09-op r  | json block-JIT | ~70  |
| **NES blargg cpu_test5**      | legacy      | 1.69  |
| **NES blargg cpu_test5**      | json per-instr | 0.83  |
| **NES blargg cpu_test5**      | json block-JIT | TBD  |

One reason GB json per-instr beats legacy (5×): GB legacy uses a loose
ms-cycle timing model + loose PPU sync, whereas NES legacy is already
cycle-accurate + giant switch + tight inline ALU. NES legacy's starting
bar is higher, so json per-instr struggles to catch up. Block JIT's
inlining/CSE/DCE is needed to cross over.

## 5. Block JIT estimated gain (from GB)

GB block-JIT pushed per-instr from 16 → 70 MIPS (4.4×). For NES:

- Expected block JIT vs per-instr: ~3-5× speedup (depends on dispatch overhead ratio)
- Expected block JIT vs legacy: 1.5-3× speedup
- Expected NES json block-JIT MIPS: **2.5–5 MIPS** (from 0.83 → 2.5+)

If we only manage to tie with legacy (1.7 MIPS), block-JIT still counts
as a success — at minimum it proves the spec-driven path is no slower
than a hand-tuned switch interpreter. To beat legacy we need:
1. inline RAM/ZeroPage access bypassing the bus extern call (the GB
   block-JIT P1 #7 pattern — pin WRAM/HRAM, bake base addr into IR)
2. cross-instruction CSE: A's read-modify-write chain shares the same PC
3. conditional branch fold: the cond from `BNE` can forward into the
   next BIT/CMP result (visible inside the block)

## 6. Setup overhead

The first `new NesJsonCpu()` call loads the spec → SpecCompiler.Compile →
HostRuntime.Build → BindExtern × N → `_rt.Compile()`, taking ~2 seconds
(JIT compile of 117 functions). For long workloads this is negligible;
on short tests like nestest (8990 instr) JsonCpu reads 0.05 MIPS purely
because the setup cost cannot be amortised.

Block JIT adds lazy block compile, so setup is even lighter (only cold
blocks compiled), but every block first-hit pays one IR-emit + ORC link
cost. GB block-JIT measured this at roughly 50–200μs / block.

---

## 7. Environment

- OS: Windows 11 Home (10.0.26200)
- .NET: 10.0.107
- LLVM: 20.x via LLVMSharp.Interop + libLLVM.runtime.win-x64
- Build: `dotnet build AprGba.slnx --no-incremental` (Debug; Release comparison later)
- Commit: `cb3d0ad` (post-cleanup; N1 final state)

Release runs are typically ~1.5–2× faster, but the baseline stays at
Debug so it lines up with the GB comparison numbers (also Debug) — same
toolchain across platforms. Release comparison goes in a follow-up doc.
