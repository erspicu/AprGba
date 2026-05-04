# Phase 7 A.4 + A.6 — Block-JIT integration: **10× MIPS on GBA loop100**

> **The big jump**: Phase 7 stacked dispatcher / IR / inline / lazy
> flag / pass pipeline quick wins from the 5.7 baseline, pushing GBA from
> ~3.7 to ~8.5 MIPS (2.3×). Block-JIT in one move adds another 10×:
> **GBA arm 8.55 → 85.39 MIPS, thumb 8.55 → 85.61 MIPS**. Compared to
> Phase 5.7 baseline this is **about 23×**, and real-time hits 20×.
>
> Change scope: A.4 (`BlockCache`) + A.6 (`CpuExecutor.EnableBlockJit` /
> `StepBlock` + `GbaSystemRunner` cycle scaling + apr-gba `--block-jit` flag).
> A.3 (ORC LLJIT) shipped earlier and is the prerequisite for this lazy
> module-add.

---

## 1. Results (4-run avg, loop100 1200 frames)

| ROM                     | Backend     | runs | min  | **avg** | max  | per-instr (8.5) | **Δ vs per-instr** | vs Phase 5.7 baseline |
|-------------------------|-------------|------|-----:|--------:|-----:|----------------:|-------------------:|----------------------:|
| GBA arm-loop100.gba     | json-llvm + block-JIT | 4 | 84.59 | **85.39** | 86.52 | 8.55           | **+899% (10.0×)**  | ~23×                  |
| GBA thumb-loop100.gba   | json-llvm + block-JIT | 4 | 85.14 | **85.61** | 85.91 | 8.55           | **+901% (10.0×)**  | ~23×                  |

Real-time multiplier:
- ARM block-JIT: 20.2–20.6× (per-instr was 2.0×)
- THUMB block-JIT: 20.3–20.5×

Setup time: ~315ms — same as per-instr (block-JIT is lazy; spec compile
itself doesn't take more time, each block compiles only on cache miss).

How to run:
```bash
apr-gba --rom=arm-loop100.gba    --frames=1200 --block-jit
apr-gba --rom=thumb-loop100.gba  --frames=1200 --block-jit
```

---

## 2. Why 10×

Per-instruction dispatch does these per instruction:
1. ReadPc + WritePc(pre-set R15)
2. ClearPcWritten (write 1 byte)
3. NotifyExecutingPc (interface call → bus internal logic)
4. Fetch from bus (interface call + region switch + array read)
5. NotifyInstructionFetch (interface call)
6. Decode (DecoderTable lookup)
7. ResolveFunctionPointer (Dictionary lookup, identity-keyed)
8. fn(state, instructionWord) (cdecl native call, 2 args)
9. ReadPc + ReadSelector (re-snapshot)
10. PcWritten check + branch detection
11. (no branch) WritePc(pc + size)

**11 host-side steps per instruction.**

Block-JIT dispatch does these per BLOCK (avg ~10 instructions):
1. ReadPc + CurrentMode (1×)
2. NotifyExecutingPc (1×, dropped from N× to 1×)
3. cache.TryGet (Dictionary lookup, 1×)
4. (cache miss only) Detect + Build IR + AddModule + GetFunctionPointer (~ms scale, amortized over ~100 hits)
5. Clear PcWritten (1×)
6. fn(state) (cdecl native call, 1 arg, 1×)
7. update counters (1×)

**7 host-side steps per block, amortized over N≈10 instructions = 0.7 steps/instr.**

Plus the optimisations LLVM can do on block IR (GVN / DSE / mem2reg
sharing constant folding + flag dependency tracking across instructions) —
block IR ~600 lines after RunPasses becomes a few dozen lines of hot path,
much tighter than per-instruction independent fns.

10× isn't surprising — in the literature, dynarec block-JIT typically gets
10–30× over per-instr interpreter on RISC ISAs. Our per-instr was already
JIT (not pure interpreter) so headroom is a bit smaller.

---

## 3. Changes

### 3.1 `src/AprCpu.Core/Runtime/BlockCache.cs` (A.4)

New file (~140 lines):
- `CachedBlock` struct: `(IntPtr Fn, int InstructionCount)`
- `BlockCache`: Dictionary&lt;uint, LinkedListNode&lt;Entry&gt;&gt; +
  LinkedList LRU standard pattern, capacity-bound (default 4096).
- `TryGet(pc, out CachedBlock)` / `Add(pc, CachedBlock)` /
  `Invalidate(pc)` / `Clear()` / `Count` / `Capacity`.
- O(1) lookup + O(1) MRU promotion.

### 3.2 `src/AprCpu.Core/Runtime/HostRuntime.cs` (A.4 supporting)

- New field `_externBindings: Dictionary<string, IntPtr>` — every
  `BindExtern` records (name → trampoline addr).
- New helper `BindExternInModule(module, name, addr)` factors out the
  inttoptr-bake logic from the original `BindExtern`, reused in two places.
- `AddModule(module)` first replays all known externs into the new module's
  global slot — block module transparently inherits trampolines from the
  initial module.

### 3.3 `src/AprCpu.Core/Runtime/CpuExecutor.cs` (A.6)

- New fields:
  - `_compileResult: SpecCompiler.CompileResult?` — null = per-instr mode
  - `_blockCachesBySetName: Dictionary<string, BlockCache>?` — one cache per instruction set (ARM / Thumb separate)
  - `_blockDetectorsBySetName: Dictionary<string, BlockDetector>?`
  - `LastStepInstructionCount: int` — Step reports how many instr just ran (per-instr=1, block=N)
  - `BlocksCompiled / BlocksExecuted: long` — stats
- New method `EnableBlockJit(compileResult)` — opt-in, builds cache + detector / set
- `Step()` checks at the top: if block-JIT enabled → `StepBlock()`, else takes the original per-instr path
- New private `StepBlock()`:
  ```
  pc = ReadPc; bus.NotifyExecutingPc(pc)
  if !cache.TryGet(pc, out entry) {
      entry = CompileBlockAtPc(pc, mode)  // detect → build → AddModule → lookup
      cache.Add(pc, entry)
  }
  state[pcWrittenOffset] = 0
  fn(statePtr)
  LastStepInstructionCount = entry.InstructionCount
  InstructionsExecuted += entry.InstructionCount
  BlocksExecuted++
  ```
- New private `CompileBlockAtPc(pc, mode)`:
  ```
  block = detector.Detect(bus, pc, max=64)
  module = LLVMModuleRef.CreateWithName($"AprCpu_BlockJit_{set.Name}_pc{pc:X8}")
  bfb = new BlockFunctionBuilder(module, layout, registry, resolverRegistry)
  bfb.Build(set, block)
  rt.AddModule(module)
  fnPtr = rt.GetFunctionPointer(BlockFunctionBuilder.BlockFunctionName(set.Name, pc))
  return new CachedBlock(fnPtr, block.Instructions.Count)
  ```

### 3.4 `src/AprCpu.Core/Runtime/Gba/GbaSystemRunner.cs`

`RunCycles` changes `Scheduler.Tick(cyclesPerInstr)` to
`Scheduler.Tick(cyclesPerInstr * Cpu.LastStepInstructionCount)` — under
block-JIT mode, one Step runs N instructions, so the scheduler must tick
N× cycles to be correct (otherwise VBlank / IRQ timing runs 10× slow).

### 3.5 `src/AprGba.Cli/Program.cs`

- New CLI flag `--block-jit`
- After `BootCpu(bus, enableBlockJit)` ctor, calls `exec.EnableBlockJit(compileResult)`
- ROM startup prints "block-jit: ON" indicator

---

## 4. Verification

```
$ dotnet test AprGba.slnx
Passed! - Failed: 0, Passed: 360, Skipped: 0, Total: 360 (duration 20s)

$ dotnet src/AprGb.Cli/.../apr-gb.dll --cpu=json-llvm \
    --rom=test-roms/blargg-cpu/cpu_instrs.gb --frames=12000
01:ok  02:ok  03:ok  04:ok  05:ok  06:ok  07:ok  08:ok  09:ok  10:ok  11:ok
Passed all tests
```

- 360/360 unit tests (incl. 8 BlockCache + 3 BlockFunctionBuilder including
  AddModule round-trip integration test)
- Blargg cpu_instrs 11/11 (per-instr path on GB JIT, GB path unchanged so still green)
- loop100 GBA arm + thumb (block-JIT path) — 10× speedup

---

## 5. Known limitations

### 5.1 Menu / interactive ROMs cannot run headless

Menu ROMs like armwrestler enter a menu loop after boot, waiting on user
input. When run headless, the ROM jumps to garbage memory (no one presses
buttons to enter the actual test), and block-JIT compiles huge 64-instr
blocks on garbage memory (PC walks linearly, cache miss every 256 bytes).
Correctness is OK but compile cost explodes, looking like a hang.

**Per-instr also runs garbage** (starts from the same PC, also walks garbage
memory), it's just slow enough to look like "still running". Block-JIT,
being fast, amplifies the problem.

**This isn't a block-JIT bug**, it's the headless / interactive ROM mismatch.
For test ROM selection see memory `reference_test_roms.md`.

Future safeguard (A.6.1): when PC is out of known-executable region, fall
back to per-instr Step to avoid bulk-compiling garbage memory.

### 5.2 GB CLI doesn't have --block-jit flag yet

`apr-gb` (LR35902 / Game Boy) CLI has no `--block-jit` flag. The underlying
`CpuExecutor` is generic — adding the flag is ~10 lines, but we'd need to
verify all LR35902 spec emitters work in the block IR path (uncertain
whether some LR35902 emitter assumes single-instruction module environment).
Phase 7 A.6.2 follow-up.

### 5.3 SMC not detected

Writes to compiled regions don't invalidate the cache. GBA homebrew rarely
uses SMC, but Pokemon-style ROMs and some weird demoscene ROMs hit it.
Phase 7 A.5.

### 5.4 No block linking yet

Block exit → outer loop reads PC → cache lookup. Each block-to-block
transition still rounds back through the host dispatcher. Phase 7 A.7 block
linking can save this step (patch the native call site to jump directly to
the next block's native code), expected another 1.5–2×.

---

## 6. Phase 7 cumulative (13 steps)

| Stage | GBA arm | GBA thumb | GB legacy | GB json-llvm |
|---|---:|---:|---:|---:|
| Phase 5.8 starting baseline | 3.82 / 0.9× | 3.75 / 0.9× | 32.76 / 67× | 2.66 / 5.5× |
| 7.B.a OptLevel O3 | 3.71 | 3.73 | 32.50 | 2.70 |
| 7.F.x id-keyed fn cache | 3.90 | 4.69 | 32.49 | 4.83 |
| 7.F.y pre-built decoded | 5.94 / 1.4× | 6.27 / 1.5× | 32.65 | 6.30 / 12.9× |
| 7.B.e cache state offsets | 7.19 / 1.9× | 6.26 | 34.29 | 6.41 |
| 7.B.f permanent pin | 7.13 | 7.97 / 1.9× | 33.67 | 6.31 |
| 7.B.g AggressiveInlining bus | 8.05 / 2.0× | 8.10 | (n/a) | (n/a) |
| 7.E.a fetch fast path | 8.25 | 8.16 | (n/a) | (n/a) |
| 7.E.b mem trampoline fast | 8.30 | 8.21 | (n/a) | (n/a) |
| 7.C.a width-correct flag | 8.26 | 8.24 | 32.75 | 6.48 |
| 7.B.h Tick/IRQ inline | 8.33 / 2.0× | 8.39 / 2.0× | (n/a) | (n/a) |
| 7.H.a LLVM pass pipeline | 8.45 / 2.0× | 8.46 / 2.0× | 32.24 | 6.50 |
| 7.C.b alloca-shadow retry | 8.49 / 2.0× | 8.51 / 2.0× | 31.66 | 6.52 |
| 7.A.3 ORC LLJIT swap | 8.55 / 2.0× | 8.55 / 2.0× | 32.49 | 6.64 |
| **7.A.4+A.6 block-JIT** | **85.39 / 20.5×** | **85.61 / 20.5×** | (n/a) | (n/a) |

GBA from Phase 5.7 baseline 3.6 MIPS → block-JIT 85+ MIPS = **23× total**.

---

## 7. Suggested next steps

**Quick wins (perf expected +10–50%)**:
- A.6.1 garbage region fallback — also fixes menu ROM headless hang +
  guards against adversarial wild ROMs
- A.6.2 add `--block-jit` to GB CLI — verify GB JIT path also reaps ~10×
- A.7 block linking — patch native call site directly, save dispatcher round-trip

**Mid-effort (perf +50–200%)**:
- A.5 SMC detection + invalidation
- A.8 state→register caching at block boundaries

**Architectural**:
- D tier compilation (cold O0 + hot O3 background recompile)
- True lazy flag (defer computation, not just batch writes)

---

## 8. Related documents

- `src/AprCpu.Core/Runtime/BlockCache.cs` — A.4 cache
- `src/AprCpu.Core/Runtime/CpuExecutor.cs` — A.6 dispatch
- `src/AprCpu.Core/Runtime/Gba/GbaSystemRunner.cs` — cycle scaling
- `src/AprGba.Cli/Program.cs` — `--block-jit` flag
- `MD/design/03-roadmap.md` Phase 7 A.4 + A.6 — marked done
- `MD/performance/202605021800-orc-lljit-upgrade.md` — A.3 (prerequisite infra)
- Previous perf note: `202605030241-cb-alloca-shadow-retry.md` (per-instr 8.49 MIPS baseline)
