# GB block-JIT roadmap — variable-width + narrow-int LR35902

> **Status (2026-05-04 update)**: P0 + P1 main body has shipped. GB block-JIT
> mode hits **~21 MIPS @ 10k frames / ~27 MIPS @ 60k frames (compile
> amortised)** on cpu_instrs (master), a 3-4× speedup over the original 6.5
> MIPS baseline. The P1 #5 V1 + P1 #5b SMC V2 mechanisms are designed and
> implemented (env-gated, default OFF for correctness); optimization is
> deferred to future V2/V3.
>
> Design basis:
> 1. Gemini consultation (QEMU TCG / FEX-Emu / Dynarmic / mGBA / x86 backend
>    internals), recorded in `tools/knowledgebase/message/20260503_202431.txt`
> 2. Existing codebase structure scan (`BlockDetector.cs:57-65` ctor throw,
>    `BlockFunctionBuilder.cs` already per-instr PC tracked, `Lr35902Emitters`
>    `read_imm8/imm16` runtime PC walk)
> 3. Starting perf: GB legacy ~31 MIPS, GB JsonCpu (per-instr) ~6.5 MIPS,
>    GB block-JIT did not exist
>
> **Goal**: bring GB JIT from 6.5 → 15-25 MIPS (close to or exceeding legacy
> 31). Expected leverage source: eliminating per-instr dispatch overhead
> (`ResolveFunctionPointer` + indirect call + `CyclesFor` lookup + per-instr
> `_bus.Tick`) and amortising it across the block.
>
> **Goal achievement (2026-05-04)**: cpu_instrs master 21 MIPS @ 10k hits the
> low end of the 15-25 MIPS target; 60k frames at 27 MIPS lands mid-range.
> Still 13-32% behind legacy 31 MIPS, but the framework-driven path vs
> hand-written dispatcher tradeoff is reasonable.

---

## 1. Why GB JsonCpu is so much slower than legacy

Comparing `JsonCpu.StepOne()` (line 304-353) against `LegacyCpu.Step()`:

| Step | JsonCpu | LegacyCpu |
|---|---|---|
| Read PC | `ReadI16(_pcOff)` ptr deref | `pc` field direct read |
| Fetch byte | `_bus.ReadByte(pc)` interface call | inline byte array index |
| Decode | `decoder.Decode(opcode)` table lookup | switch case direct jump |
| Resolve fn | `ResolveFunctionPointer(setName, decoded)` | (no) |
| Compute fall-through | `ComputeFallThroughPc(opcode, pc)` switch | (no) |
| Cycle | `CyclesFor(decoded.Instruction)` lookup | inline |
| Tick bus | `_bus.Tick(stepCycles)` | inline counter |
| Call emitter | `(delegate*)fn(state, word)` indirect call | inline C# arith |

JsonCpu per-instruction overhead is ~50-80ns; LegacyCpu ~5-10ns; the
JIT-compiled emitter itself does ~1-3ns of useful work. **Dispatch overhead
completely drowns the JIT advantage.**

Block-JIT solution: move `ResolveFunctionPointer` + `(delegate*)fn` +
`CyclesFor` + `_bus.Tick(per-instr)` to once-per-block, plus cross-instruction
LLVM optimization (CSE / DSE / register caching).

---

## 2. Core challenge of variable-width ISA + design basis

LR35902 length **is fully determined by the first byte** (256-entry static
table):

| First byte range | length | Examples |
|---|---|---|
| Most | 1 byte | LD A,B / ADD A,C / NOP / RET / RST |
| imm8 / e8 | 2 bytes | LD A,n / ADD A,n / JR e8 |
| imm16 | 3 bytes | LD HL,nn / JP nn / CALL nn / LD (nn),A |
| 0xCB prefix | 2 bytes | BIT/SET/RES/RLC/RRC/RL/RR/SLA/SRA/SWAP/SRL × 8 reg |

**Three concrete recommendations from Gemini** (synthesised from QEMU TCG +
FEX-Emu + Dynarmic + mGBA patterns):

1. **Variable-width detection**: sequential decode crawl, with length pulled
   from a 256-entry static table computed at spec compile time (not inferred
   at runtime). Each instruction has PC + length recorded uniformly so
   Strategy 2 PC baking continues to work naturally.
2. **i8/i16 native LLVM IR**: don't manually promote to i32. LLVM
   `LegalizeTypes` + the x86 backend already handle this (auto `movzx`
   breaks false dependencies). Partial-register stalls have been mitigated
   on Sandy Bridge / Zen and later. **Manual masking actually disrupts
   instcombine's H-flag overflow detection patterns.**
3. **0xCB prefix is a decoder state modifier, not an ISA switch**:
   hierarchical trie decoder, treat the entire `CB xx` as an atomic 2-byte
   instruction. On IRQ / page fault, PC always points at the 0xCB byte
   rather than sub-byte. Z80 DD/FD/ED and x86 REX/VEX use the same trie
   pattern.

My own additional insight: **immediate baking is a natural extension of
Strategy 2** — pack imm8/imm16 directly into the high bytes of
`DecodedBlockInstruction.InstructionWord`, and the spec's
`read_imm8`/`read_imm16` becomes "shift + mask to read certain bits of the
instruction word" (exactly the same pattern as ARM imm extraction). No new
`PreFetchedImm` mechanism needed.

---

## 3. Full optimization list — sorted by (cost × risk × value)

Below is the combined priority for everything related to GB block-JIT plus
unfinished items in the Phase 7 H group. **P0 = must do + short + unblocks
follow-ups**; **P4 = speculative + may not do**.

### Tier P0 — Foundation (✅ **completed 2026-05-04**)

Completion record: `MD/performance/202605040000-gb-block-jit-p0-complete.md`.
All 11 Blargg cpu_instrs sub-tests PASS (including BIOS LLE); GB block-JIT
MIPS went from 0 → 22.64 (+150% over per-instr 9).

| # | Item | Status | Commit |
|---|---|---|---|
| **1** | Variable-width `BlockDetector` | ✅ | `3024100` |
| **2** | 0xCB prefix as 2-byte atomic | ✅ | `0cb93a8` |
| **3** | Immediate baking via instruction_word packing | ✅ | `7a8305a` |
| **4** | GB CLI `--block-jit` + Strategy 2 PC fixes | ✅ | `adddade` |

**P0 completion milestone**: T1 360+ tests / T2 8-combo screenshot matrix
(GBA path must not regress) / GB Blargg cpu_instrs passes in block-JIT mode /
T3 bench GB 09-loop100 from 6.5 → ≥10 MIPS (conservative target ~50%
improvement).

#### P0 follow-ups — already shipped

| # | Item | Status | Commit | Notes |
|---|---|---|---|---|
| **P0.5** | HALT/STOP block boundary | ✅ | `a10a718` | detector watches step `op:"halt"`/`"stop"` and auto-splits the block |
| **P0.5b** | EI delay band-aid (block ends at EI+1) | ✅ partial | `6a86005` | hardcoded LR35902-specific; superseded by P0.6 |
| **P0.5c** | `Lr35902Alu8Emitter.FetchImmediate` Strategy 2 baking | ✅ | `d760b08` | + `--diff-bjit=N` lockstep harness; Blargg 01-special PASSED |
| **P0.6** | Generic `defer` micro-op + AST pre-pass | ✅ | `ca248e8` | Phantom-instruction-injection pattern; replaces P0.5b hardcode; details in `MD/design/13-defer-microop.md` |
| **P0.7** | **Hybrid IRQ delivery — fast/slow split + `sync` micro-op** | ✅ | `2a1de15` + `674316f` | Per-instr-grained IRQ correctness in block-JIT; MMIO write callback returns sync flag, JIT exits block on sync; details in `MD/design/14-irq-sync-fastslow.md` |
| **P0.7b** | Conditional branch taken-cycle accounting fix | ✅ | `34f9f4b` + `d7314a8` | pre-exit BB for taken-branch cycle deduct (revised for smaller GBA perf hit). **Known regression**: GBA bjit -16% from this commit; pending (C) fix |

### Tier P1 — Big-win extensions (✅ **main body completed 2026-05-04**)

| # | Item | Status | Commit | Notes |
|---|---|---|---|---|
| **5** | **Native i8/i16 + block-local state caching** | ✅ V1 mechanism | `0e1e280` | EmitContext.GprShadowSlots/StatusShadowSlots + ctx.GepGpr/GepStatusRegister + ctx.DrainShadowsToState; 7 GPR + F + SP shadow allocas; mem2reg promotes to SSA. V1 unconditionally allocates; small blocks in cpu_instrs actually go -4% because entry-load + exit-drain costs exceed internal savings. **V2 pending**: per-block live-range analysis to alloc only the registers actually used. `APR_DISABLE_SHADOW=1` env disables it for A/B benching. |
| **5b** | **SMC V2: IR-level inline notify + precise per-instr coverage + cross-jump-into-RAM** | ✅ mechanism (env-gated) | `6c04422` | Three pieces: (a) `EmitSmcCoverageNotify` adds a 1-byte cov check + cold-path notify call after each WRAM/HRAM inline write (gated by `APR_SMC_INLINE_NOTIFY=1`); (b) CachedBlock adds `CoverageInstrPcs/Lens` for precise per-instr range (always-on); (c) BlockDetector unblocks cross-jump-into-RAM (gated by `APR_CROSS_JUMP_RAM=1`). Both env vars OFF by default to preserve V1 behaviour (cpu_instrs 11/11 PASS); when ON, cpu_instrs sub-test 03 livelocks due to cycle accounting drift under invalidation. Bonus: added illegal-opcode (0xDD/...) NOP fallback to avoid crash when cross-jump hits an illegal byte. |
| **6** | **Detector cross unconditional B/JR/JP** | ✅ ROM-only | `dd99c98` | LR35902 0x18 (JR e8) + 0xC3 (JP nn) cross-follow; CALL/RET/JP HL (dynamic) not followed. V1 limited to ROM-to-ROM (source ≤ 0x7FFF AND target ≤ 0x7FFF); V2 (P1 #5b unblock) exists but is env-gated. |
| **7** | **E.c IR-level memory region inline check** | ✅ | `15f913f` | WRAM (0xC000-0xDFFF) / HRAM (0xFF80-0xFFFE) inline GEP-store skips the bus extern; still routes through sync-flag extern for MMIO/cart-RAM. Two extern globals Lr35902WramBase/HramBase are bound at JsonCpu.Reset to pinned pointers. |

**P1 completion milestone (achieved)**:
- T1 365/365 + T2 8-combo GBA canonical hash unchanged (ARM path P1 #5
  shadow gated to LR35902)
- Blargg cpu_instrs master 11/11 PASS @ ~21 MIPS (10k frames) / ~27 MIPS
  (60k frames)
- GB 09-loop100 has no material regression vs the P0 baseline of 22.6 MIPS
  (V1 shadow -4% but the SMC infrastructure addition stays flat)
- Still 13-32% behind legacy 31 MIPS; the gap between framework-driven path
  and hand-written dispatcher is reasonable

**P1 known followups (deferred to V2/V3)**:
- P1 #5 V2: per-block live analysis, shadow only allocates registers
  actually used → expected to flip into +X%
- P1 #5b V3: resolve cycle drift when SMC is enabled; mGBA/Dolphin's
  deferred invalidation pattern (effective only after current block
  finishes) is a reference solution
- GBA bjit P0.7b regression (-16%) still unfixed

### Tier P2 — Routine maintenance + medium value (on demand)

| # | Item | Status | Cost | Risk | Value | Notes |
|---|---|---|---|---|---|---|
| 8 | **A.5 SMC detection + invalidation** | ✅ V1+V2 | M | L | (correctness) | V1 (`8ce66ac`) per-byte coverage + bus-extern path notify; V2 (`6c04422`) IR-level inline notify + precise per-instr coverage + cross-jump-into-RAM unblock (env-gated). See P1 table #5b. **Overlaps with P1 #5/#5b** — promoted from P2 to P1 scope. |
| 9 | **A.9 Performance profiling tool** | ⏳ pending | S | L | (diagnostic) | `--bench-blocks` prints per-block compile counts / execution counts; only meaningful after P1 #5 V1 is done. **Recommended next pick** — low-investment high-return reconnaissance tool, prerequisite for any subsequent perf work (PGO). |
| 10 | **A.8 State→register caching aggressive** | ✅ overlaps with P1 #5 | M | M | M | P1 #5 V1 has shipped the same mechanism (block entry load shadow, exit drain); this was A.8's original target. Mark done. |
| 11 | **H.b Spec-time IR pre-processing** (dead-flag elim) | ⏳ pending | L | M | M | At SpecCompiler stage, do def-use analysis and eliminate cross-instruction dead flag writes. Continuous LR35902 ALU sequences (ADD-ADC-ADC) often overwrite intermediate H/N flags — eliminable. |
| 12 | **H.c Hot-opcode inlining to dispatcher** | ⏳ pending | L | M | M | Inline top-N hot opcodes (MOV/ADD/LDR) directly into the switch case to skip the indirect call; needs PGO statistics (→ #9 prerequisite). |
| 13 | **H.d LR35902 dispatcher GBA-parity** | ⏳ pending | M | L | L-M | Once block-JIT lands, the per-instr path becomes secondary; the value of these sub-optimizations drops. |

### Tier P3 — High risk / low return (deferred decision)

| # | Item | Status | Cost | Risk | Value | Notes |
|---|---|---|---|---|---|---|
| 14 | **A.7 Block linking** (patch native call) | ⏳ pending | L | H | M | Cross-OS native code patching is complex; ORC stub mechanism may have caveats. Touch only if dispatch overhead remains the bottleneck after block-JIT lands. |
| 15 | **C.b lazy flag retry / LR35902 H-flag lazy** | ⏸ deferred | M | H | L | ARM C.b failed twice (shadow drain edge cases); gain <1%; P1 #5 V1 already uses the same shadow alloca pattern for F register block-local, covering similar cases. Don't retry unless hot path profile proves ReadFlag is the bottleneck. |
| 16 | **H.e Cycle accounting real batch** | ⏳ pending | S | M | L | Per-instr +=4 changed to per-block += N*4; saves a few ns/instr but risks IRQ delivery timing. Phase 1a predictive downcounting has shipped, so further batching gains are smaller. |
| 17 | **H.f Per-process opcode profiling persistence** | ⏳ pending | M | L | L | disk cache opcode statistics; startup-friendly, runtime unchanged. |
| 18 | **G.a Native AOT** | ⏳ pending | M | M | L | Build-time change; helps cold-start, runtime flat; whether LLVMSharp interop is AOT-friendly is TBD. |
| 19 | **G.b UnmanagedCallersOnly IL emit** | ⏳ pending | L | H | L | Micro-optimization; requires .NET IL internals expertise. |

### Tier P4 — Speculative / may not do (unless external demand)

| # | Item | Cost | Risk | Value | Notes |
|---|---|---|---|---|---|
| 20 | **H.g LLVM IR custom calling convention** | XL | H | M (theoretical) | Requires LLVM tablegen-level changes; high risk, may hit LLVM bugs |
| 21 | **H.i AOT bitcode cache** | L | M | (startup only) | spec→IR results serialized to disk; helps startup time, runtime flat |

---

## 4. P0 detailed implementation plan (in order)

### 4.1 Step 1 — Variable-width `BlockDetector`

**Files**: `src/AprCpu.Core/Runtime/BlockDetector.cs` + `Block.cs`

**Changes**:
1. `Block.InstrSizeBytes` (current: single value for whole block) — kept for
   backward compat, but semantics change to "valid only for fixed-width set;
   variable-width set is 0"
2. `DecodedBlockInstruction` record adds `byte LengthBytes` field
3. `BlockDetector` ctor no longer throws on variable-width; instead:
   - if `WidthBits.Fixed.HasValue` → original fixed-stride path
   - else → sequential-crawl path, looking up length from spec
     `instruction_length_table` (new field)
4. Variable-width path's `ReadInstructionWord`:
   - `len=1` → `bus.ReadByte(pc)` → uint LSB
   - `len=2` → `bus.ReadByte(pc) | (bus.ReadByte(pc+1) << 8)`
   - `len=3` → three bytes packed
5. Add spec schema field `instruction_length_table` (256-entry array of
   length values); `SpecLoader` reads into `InstructionSetSpec`

**Spec changes**: `spec/lr35902/main.json` adds 256-entry length table (or
auto-derive from group files + build the table at SpecCompiler stage)

**Verification**:
- New unit test: variable-width detector on LR35902 spec detects block
  `LD A,5; ADD A,3; LD B,A; HALT` (1+2+2+1+1 = 7 bytes) → 4 instr with
  correct PCs + length
- ARM/Thumb path (fixed-width) regression must not break
- T1 all green

### 4.2 Step 2 — 0xCB prefix as 2-byte atomic

**Files**: `spec/lr35902/groups/block3-cb-prefix.json` + `BlockDetector` +
`SpecLoader`

**Changes**:
1. Spec adds `prefix_to_set: "CB"` field (replaces `switches_instruction_set: true`)
2. When `BlockDetector` detects prefix opcode:
   - Don't split block
   - Fetch second byte
   - Use `CB` decoder to resolve sub-opcode
   - Whole `CB xx` written as 1 `DecodedBlockInstruction`, length=2,
     instruction word = `(0xCB << 8) | sub_opcode` or
     `sub_opcode << 8 | 0xCB` (depending on endian / emitter alignment)
3. `SpecLoader` reads `prefix_to_set` + exposes sub-set reference in
   InstructionSetSpec

**Verification**:
- Unit test: detect block `BIT 7,A; SET 0,B; HALT` → 3 instr (first two
  length=2)
- T1 all green

### 4.3 Step 3 — Immediate baking via instruction_word packing

**Files**: `src/AprCpu.Core/IR/Lr35902Emitters.cs` (`Lr35902ReadImm8Emitter`,
`Lr35902ReadImm16Emitter`)

**Changes**:
1. `read_imm8` emitter changes to:
   - if `ctx.CurrentInstructionBaseAddress is uint` (block-JIT mode) → take
     high byte from `ctx.Instruction` (constant integer) (`(ins >> 8) & 0xFF`)
     and assign to `out` var; **emit no bus call**
   - else (per-instr mode) → original bus.ReadByte path
2. `read_imm16` similar but takes 16 bits
3. PC advance not needed in block-JIT mode either (PC is a baked constant
   in block IR)
4. **Unexpected benefit**: `read_imm8/16` degenerate into a BitPattern field
   extract pattern, structurally unified with ARM's instruction word imm
   extract; possibly future work could remove these two emitters entirely
   in favour of a generic `extract_field`

**Verification**:
- Unit test: block IR for `LD A,#42` should have no `bus.ReadByte` call,
  only const 42 store into R[A]
- T1 all green

### 4.4 Step 4 — GB CLI `--block-jit` flag

**Files**: `src/AprGb.Cli/Program.cs` + `src/AprGb.Cli/Cpu/JsonCpu.cs`

**Changes**:
1. `Program.cs` adds `--block-jit` flag parsing (mirror `AprGba.Cli`)
2. JsonCpu wraps CpuExecutor (or directly uses CpuExecutor.EnableBlockJit)
3. GB-specific bits (halt / IME / IRQ) stay in the outer loop; block fn
   only runs the instruction stream

**Verification**:
- `apr-gb --rom=... --bios=BIOS/gb_bios.bin --cpu=json-llvm --block-jit
  --frames=300 --screenshot=temp/gb-bjit.png` runs successfully
- DMG Nintendo® logo screenshot consistent across per-instr / legacy
- Blargg cpu_instrs 11/11 all green in block-JIT mode
- T2 8-combo screenshot matrix (GBA) regression must not break
- T3 bench: 09-loop100 GB block-JIT MIPS, expected 6.5 → ≥10

---

## 5. Completion criteria

P0 complete (✅ 2026-05-04) = `MD/performance/202605040000-gb-block-jit-p0-complete.md`
records:
- T1 all green (including new variable-width detector unit tests)
- T2 8-combo screenshot matrix (GBA path canonical hashes unchanged)
- GB Blargg cpu_instrs 11/11 passes in legacy / json-llvm-per-instr /
  json-llvm-bjit modes (serial output catches "Passed all tests")
- T3 bench: GB 09-loop100 across 3 modes + 3 runs, json-llvm-bjit MIPS
  meaningfully improves over per-instr (achieved)

P1 complete (✅ 2026-05-04) = all P0 ✓ plus:
- P1 #5 V1 shadow mechanism shipped, T1+T2+Blargg no regression (V1 perf
  -4% is a known cost, V2 pending)
- P1 #5b SMC V2 three pieces shipped (env-gated default OFF)
- P1 #6 ROM-only cross-jump shipped; P1 #7 IR-level WRAM/HRAM inline shipped
- cpu_instrs master 11/11 PASS @ ~21 MIPS @ 10k frames

P2/P3/P4 — profile-driven trigger. Suggested next pick:
- **P2 #9 A.9 profiling tool** (S/L/diagnostic) — prerequisite for any
  subsequent perf work. Low investment, high return.
- **P1 #5 V2** — flip V1 shadow's -4% into positive (per-block live-range
  analysis)
- **P1 #5b V3** — fix cycle drift with SMC inline notify enabled
  (mGBA/Dolphin deferred-invalidation pattern as reference)
- **GBA bjit P0.7b regression -16% fix** (left by commit `d7314a8`)

---

## 6. Relationship to existing roadmap

This doc supersedes vague hints in `03-roadmap.md` H group regarding GB /
variable-width (`H.d LR35902 dispatcher equivalent to GBA path optimization`
is retained but priority dropped to P2 because block-JIT landing reduces its
value). `03-roadmap.md`'s Phase 7 main progress remains as ARM block-JIT
progress + Group A-F-H overall framework; this doc focuses on GB-specific +
variable-width sub-topics.

After P0 + P1 completion, `03-roadmap.md` Phase 7 progress snapshot needs a
"GB block-JIT P0+P1 ship record + corresponding perf numbers" addition.
**TODO**: treat this as a housekeeping action.

---

## 7. Risk register (some retired; kept as historical record)

| Risk | Outcome |
|---|---|
| Variable-width detector design too abstract too early, bottlenecked on spec schema design | ✅ **avoided** — `Lr35902InstructionLengths.GetLength` static 256-entry table, not in spec schema; detector uses `lengthOracle` callback injection. |
| CB-prefix double-layer decode encoding mismatch with emitter expectations in instruction_word | ✅ **avoided** — `prefix_to_set: "CB"` spec field + sub-decoder mask/match directly on 8-bit sub-opcode, no shift-by-8 gymnastics. |
| GB block-JIT once landed actually hits partial-register stall for native i8/i16 | ✅ **didn't happen** — Gemini predicted correctly; modern x86 backend (Sandy Bridge / Zen and later) has mitigated; i8 LR35902 GPR + i16 SP using native width directly in IR shows no stall signs. |
| Detector cross unconditional B hits ROM bank switch / SMC | ✅ **mitigated** — P1 #6 V1 limited to ROM-to-ROM; V2 (P1 #5b) adds SMC inline notify unblock. |
| GB block-JIT once landed slower than per-instr | ✅ **avoided** — at P0 completion 22.6 MIPS vs per-instr 6.5 MIPS = 3.5× speedup; subsequent P1 advances to 27 MIPS. |
| **New risk (surfaced after P1): cycle accounting drift triggered by SMC invalidation** | ⏳ **active** — when `APR_SMC_INLINE_NOTIFY=1` is ON, cpu_instrs sub-test 03 livelocks; pending V3 deferred-invalidation pattern fix. |
| **New risk: GBA bjit perf -16% regression** (`d7314a8`) | ⏳ **active** — left by P0.7b conditional branch taken-cycle accounting fix; pending (C) fix |
