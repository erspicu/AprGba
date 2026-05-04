# Advanced timing accuracy + framework-generic structure — design concepts and methods

> **Status**: design synthesis (2026-05-04)
> **Scope**: Explains the design concepts, methods, and tradeoffs the
> AprCpu framework uses in block-JIT mode to balance "preserve cycle-accurate
> timing" against "preserve framework genericity".
>
> Cross-reference: each individual mechanism has its own design doc
> (`13-defer-microop.md`, `14-irq-sync-fastslow.md`,
> `12-gb-block-jit-roadmap.md`); this doc is the synthesis that lays out
> the underlying concepts + generalization methods clearly.
>
> **Target audience**: future readers who want to (a) port a third CPU,
> (b) maintain timing behaviour, (c) understand "why was it designed this
> way" — including future me.

---

## 1. Why this doc has value

Block-JIT compiles N instructions into a single native function and runs
them in one go; **eliminating dispatch overhead** is its core gain. But
this gain has a cost: from the perspective of external HW (PPU / Timer /
IRQ controller / DMA), the JIT'd block's interior is **opaque** — HW
doesn't know which instruction inside the block you're executing. Any
emulator timing behaviour requiring "per-instruction granularity" breaks
under block-JIT mode.

The industry has solutions for this (QEMU TCG, Dynarmic, mGBA, Dolphin),
but they're all **arch-specific**: one set for ARM, another for PowerPC,
yet another for SH-4. We're different: a **JSON-driven framework** —
the same BlockFunctionBuilder runs ARM, Thumb, LR35902, and adding 6502
/ Z80 / 8080 in the future follows the same path.

What this doc records is the concepts and methods for "**how to design
timing-accurate mechanisms at framework level rather than rewriting them
per CPU**". The value of writing it down:

1. Future people porting a third CPU don't have to reinvent sync / defer
   / SMC / region inline concepts
2. When chasing a timing bug during maintenance, you know which axis to
   look at (which of Pattern A/B/C is misbehaving)
3. Each tradeoff is explicitly recorded, not hidden in magic constants

---

## 2. Core tension: why accurate timing in block-JIT is hard

A real CPU runs **synchronously with HW** (PPU, Timer, DMA, IRQ
controller):

```
cycle:  0    1    2    3    4    5    6    7    8    9   10
CPU :  [LD A,n][        ADD HL,BC        ][JR NZ,e][......]
HW  :   ↑    ↑    ↑    ↑    ↑    ↑    ↑    ↑    ↑    ↑    ↑
        Timer tick, PPU advance, IRQ delivery check, DIV++
```

A **per-instruction interpreter** aligns naturally: execute 1 instr →
tick HW → check IRQ → next instr. HW gets observed at every cycle
boundary, but **every instr pays dispatch cost** (fetch + decode +
indirect call + cycle book-keeping).

**Block-JIT** runs N instructions in one function call, amortizing
dispatch — but HW observation points vanish:

```
JIT call:   [block of 17 instructions runs natively]
HW  :       ?   ?   ?   ?   ?   ?   ?   ?   ?   ?   ?   ?
                ← HW can't see the cycle boundaries inside the block
```

**Block-JIT's perf gain is proportional to average block length** (block=1
→ per-instr speed; block=20 → ~10× speedup). So **the goal is to make
blocks as long as possible while still yielding politely at points that
need timing observation**. This is the core tension.

Concrete events that easily break timing:

| Event | Why it breaks timing | Example |
|---|---|---|
| **IRQ pending change** | HW sets IF.Timer; the next instr boundary should jump to IRQ vector | Should service immediately after Timer overflow |
| **MMIO read changes over time** | DIV / LY / STAT differ each cycle; reading at the wrong cycle gets the wrong value | Pokemon BUSY-wait DIV |
| **MMIO write triggers HW reset** | Writing LCDC.0=0 immediately turns off screen; writing 0xFF04 resets DIV; writing IF/IE changes IRQ | LCDC switching mid-flight triggers STAT IRQ |
| **Self-modifying code (SMC)** | Write WRAM then JR there; cached IR is stale | Blargg test framework copies new sub-test into WRAM |
| **Delayed-effect instruction** | EI doesn't set IME immediately; it takes effect "after the next instruction completes" | LR35902 EI boundary IRQ handling |
| **Conditional cycle cost** | JR cc not-taken 8 cycles; taken 12 cycles | HALT wake-up time on IRQ varies |
| **Pipeline-PC quirks** | ARM7 PC reads see pc+8 (pipeline ahead) | BIOS LLE LDR pc, [r0, ...] |

Each is "if not handled in the block → downstream HW behaviour wrong".

---

## 3. Three architectural patterns — the conceptual backbone

We don't "fix each timing problem in isolation" — we use **three reusable
architectural patterns**. Whenever a new timing problem comes in, ask
first "which pattern does this fall under?". **This section is the
backbone of this doc.**

### 3.1 Pattern A — Predictive downcounting (estimate ahead, deduct as we go)

**Used in**: cycle accounting (Phase 1a), cycles_left budget.

**Concept**:
1. Before entering the block, the host computes "cycles remaining until
   the next scheduler event" → stored in `state.cycles_left`
2. Block IR deducts `cycles_left -= cycle_cost` after each instr
3. When ≤ 0, the block exits early (writes next-PC + PcWritten=1)
4. Host computes actual ticks as `cycles_consumed = N - state.cycles_left`

**Why "predictive"**: cycle cost is given by the spec, known at compile
time; no runtime lookup table. Each instr's sub + icmp gets folded by
LLVM and branch-predicted.

**Genericity**:
- Fully spec-driven (cycle.form parsed in BlockFunctionBuilder)
- Any CPU uses the same mechanism; works as long as spec writes correct
  cycle.form
- ARM and LR35902 share the same path

### 3.2 Pattern B — Catch-up callback (tick to "now" the moment an event happens)

**Used in**: MMIO-triggered PPU/Timer/DMA sync (Phase 1b).

**Concept**:
1. Bus interface exposes `OnMmioWrite(addr, value, cyclesSinceLastTick)`
   callback
2. JIT'd block writes MMIO via the bus extern; bus first calls
   `Scheduler.Tick(cyclesSinceLastTick)` to push PPU/Timer to "now",
   then performs the actual write; subsequent MMIO reads in the same
   block see correct values

**Why retroactive**: HW should tick every cycle; we batch-tick; but at
"moments where HW state is observed" (MMIO read/write), we must catch
up first, otherwise we read stale.

**Genericity**:
- Bus interface is a platform interface (`IMemoryBus.OnMmioWrite`)
- Scheduler tick logic is platform-specific (PPU / Timer model varies)
- Block-JIT side only "calls the bus"; catch-up details are the bus's
  problem

### 3.3 Pattern C — Sync exit (HW state changed → exit block immediately)

**Used in**: IRQ delivery (P0.7), SMC invalidation trigger (P1 #5b).

**Concept**:
1. Some event may change HW state (write to MMIO changes IRQ readiness;
   write to cached code region changes IR validity)
2. JIT'd code checks a sync flag (extern return value or coverage
   counter) at the moment the event happens
3. flag set → block immediately exits (writes next-PC + PcWritten=1 +
   ret void)
4. Host sees PcWritten=1, enters dispatch loop, can deliver IRQ /
   re-compile

**Genericity**:
- `sync` micro-op (`14-irq-sync-fastslow.md`) — any spec step can be
  followed by `{ "op": "sync" }`; emitter auto-generates mid-ret IR
- Bus extern's `Write*WithSync` variant unifies the interface (return
  i8 sync flag)
- SMC inline notify is a specialization of the same pattern (fast path
  inline check + slow path notify call)

### 3.4 Decision tree across the three patterns

When facing a new timing problem, ask in this order:

1. "Can this timing be solved by cycle accounting alone?" → **Pattern A**
   (predictive)
   - e.g. each instr's cycle cost, conditional branch taken cost
2. "When the event happens, must HW be pushed to now first?" →
   **Pattern B** (catch-up)
   - e.g. MMIO read sees the correct LY, STAT, DIV
3. "After the event, must control return to host?" → **Pattern C** (sync
   exit)
   - e.g. IRQ delivery, SMC invalidation, CPU mode change

Timing problems that don't fall on any of the three axes — design might
need re-examination.

---

## 4. Nine framework-generalization methods

Underlying concepts in place — how do we **avoid rewriting from scratch
for every new CPU**? Nine methods:

### 4.1 Generic micro-ops replace arch-specific primitives

**Approach**: abstract timing behaviour into spec micro-ops, don't
hardcode in C# emitters.

**Examples**:
- `defer { delay: N, body: [...] }` — any "takes effect after N
  instructions" delay
  - LR35902 EI / Z80 STI / x86 STI / SH-2 branch delay slot all use this
- `sync` — any "needs host observation" yield point
  - LR35902 MMIO writes / IRQ-relevant writes / future cache fences from
    other CPUs

**Benefit**: spec is written once, all spec-driven CPUs work
automatically. Porting a new CPU doesn't require touching C# emitters
— just write the spec JSON correctly.

**Counter-example**: the early P0.5b hardcoded `BlockDetector.HasEiDelayStep`
check for an LR35902-specific step name — later replaced by the generic
`defer` in P0.6.

### 4.2 EmitContext as a routing layer

**Approach**: emitters don't directly call
`Layout.GepGpr(builder, statePtr, idx)`; they call `ctx.GepGpr(idx)`.
`ctx` internally decides whether to return an alloca shadow or a
state-struct GEP.

**Why**: in block-JIT mode, GPRs need to live in alloca shadows so
mem2reg picks them up; per-instr mode doesn't need this. **Emitters
shouldn't care** what mode they're running under.

**Benefit**:
- Same spec-emitted code works in both per-instr and block-JIT
- Adding a new mode (AOT, dynamic recompile) only requires adding a new
  routing path to ctx
- Refactor scope is "add methods to EmitContext" + "rename call sites",
  not "add if/else in every emitter"

### 4.3 AST pre-pass for control-flow transformations

**Approach**: insert a transformation pass between spec → IR.

**Examples**:
- `DeferLowering` — walk instructions before block enters IR, handle
  defer's "delay N → inject phantom step"
- Future possibilities: dead-flag elim, constant folding within block,
  micro-op fusion

**Why**: spec syntax is intuitive (defer wraps the body), but the IR
stage needs it "flattened" into phantom steps. With a transformation
pass in between, spec and IR don't pollute each other.

**Generalization**: each transformation pass is a stateless function
`List<Instr> → List<Instr>`; multiple passes can be stacked.

### 4.4 Spec-driven + callback escape hatch

**Approach**: solve via spec field where possible; for things spec can't
express, inject host logic via callback.

**Examples**:
- Variable-width detector — 95% logic spec-driven (mask/match/format);
  length table injected via `lengthOracle: Func<byte, int>` callback
- Bus extern — function signature in spec / IR; implementation in host
  C# `[UnmanagedCallersOnly]` static methods
- Prefix sub-decoder — spec marks `prefix_to_set: "CB"`; sub-decoder
  loaded by `SpecLoader` from another spec file

**Principle**: try spec first; callback only if spec can't express it;
callback interface should be minimal (one function, no stateful object).

### 4.5 Cold-path inlining + LLVM `expect` hint

**Approach**: inline the fast path into IR; emit conditional branch for
the slow path and mark the cold BB cold.

**Examples**:
- WRAM/HRAM inline write (`EmitWriteByteWithSyncAndRamFastPath`) —
  region check inline; MMIO/cart-RAM goes through extern + sync path
- SMC inline notify (`EmitSmcCoverageNotify`) — 1-byte counter check
  inline; only call extern when non-zero
- Sync micro-op — extern returns 0 → direct br; returns 1 → goes through
  mid-ret

**Why it matters**: 99%+ of paths are fast path (WRAM writes aren't SMC,
MMIO writes don't change IRQ); inlining drops the 9-12ns extern call to
1-2ns load+branch.

### 4.6 IR-level region check + base-pointer pinning

**Approach**: partially internalize the "memory bus dispatch" pure-host
concept into IR. By (a) pinning the host byte array, (b) baking the
address as an IR constant, (c) inlining region checks in IR.

**Example**: P1 #7 WRAM/HRAM inline write
- JsonCpu.Reset GCHandle.Pinned `bus.Wram`, gets pointer via
  `AddrOfPinnedObject()`
- HostRuntime.BindExtern binds the pointer to LLVM global
  `lr35902_wram_base`
- IR uses `(addr - 0xC000)` GEP to load/store directly

**Why**: bus extern call is 9-12ns; inline GEP-store is 1ns. Huge for
RAM-heavy workloads.

**Generalization**: `MemoryEmitters.GetOrDeclareRamBasePointer` is a
generic helper; per-CPU spec decides which regions are suitable for
inlining (pure memory regions with no side effects).

### 4.7 Strategy 2 PC handling — pipeline-PC becomes compile-time constant

**Approach**: in block-JIT mode, PC reads resolve to `bi.Pc + offset`
baked constants; only true branches write back to GPR[15] / state.PC.

**Why**: originally per-instr executor pre-sets `R15 = pc + pc_offset_bytes`
before each instr to model ARM7 pipeline; doing this for each instr
inside block-JIT (a) creates lots of redundant writes (b) muddies the
"did PC change?" signal (pre-set looks identical to a real branch).

**Generalization**:
- ARM (PC = pc+8), Thumb (PC = pc+4), LR35902 (PC = pc+length) use the
  same mechanism
- `EmitContext.PipelinePcConstant` is the unified interface
- Variable-width set uses `bi.Pc + bi.LengthBytes`; fixed-width set uses
  `bi.Pc + spec.PcOffsetBytes`
- True branches mark via `WriteReg.MarkPcWritten`

### 4.8 Generation-counter for ORC duplicate-definition

**Approach**: each time the block at the same PC is re-compiled, the fn
name gets a `_g{N}` suffix.

**Why**: ORC LLJIT doesn't release old modules; adding the same-named
fn a second time throws "Duplicate definition".

**Generalization**: BlockFunctionBuilder.Build accepts an
`int generation = 0` parameter; JsonCpu maintains a monotonic
`_blockGeneration` counter.

### 4.9 Lockstep diff harness as framework infrastructure

**Approach**: run per-instr and bjit backends on the same ROM and same
state; diff register file + WRAM every N instructions. Stop the moment
divergence is found.

**Example**: `apr-gb --diff-bjit=N` (added by commit `3617240`)

**Why**: bjit behaviour is subtle; T1 unit tests can't cover every
(PC × state × instr) combination; T2 screenshot tests are slow and only
inspect final state. Lockstep diff is a precise check on "bjit should
match per-instr at every step".

**Generalization**: the same harness works for both ARM and LR35902;
diff-checked fields are spec-driven (RegisterFile + status registers);
new CPUs don't require harness logic changes.

---

## 5. Inventory of timing mechanisms + corresponding pattern/method

Map the three patterns + nine methods above to actual mechanisms:

| Mechanism (commit) | Timing problem | Pattern | Methods used |
|---|---|---|---|
| Predictive cycle downcounting (`77396ca`) | Block can't instruction-grain tick HW | A | spec-driven cycle.form |
| MMIO catch-up callback (`860d7fe`+`05c285a`) | Mid-block MMIO read after write reads stale value | B | bus interface |
| Generic `sync` micro-op (`999f9eb`) | Mid-block IRQ-relevant change → should deliver immediately | C | 4.1 generic micro-op |
| Bus sync extern variant (`0c001fc`) | Block-JIT distinguishes IRQ-relevant write | C | 4.5 cold-path inlining |
| `defer` micro-op (`51c2921`) | LR35902 EI / Z80 STI delayed-effect | (compile-time) | 4.1 + 4.3 AST pre-pass |
| Conditional branch taken-cycle (`f27450f`+`7dd1e04`) | JR cc taken vs not-taken differ in cycle count | A reinforcement | per-instr pre-exit BB |
| HALT/STOP block boundary (`c47d849`) | HALT wake-up time on IRQ varies | (block boundary) | 4.4 spec step boundary |
| SMC V1 (per-byte coverage) (`24a58d1`) | Self-modifying code stales cached IR | (cache invalidation) | per-byte counter + bus path notify |
| SMC V2 inline notify + precise coverage (`377379c`) | P1 #7 inline write bypasses bus path → SMC miss | C | 4.5 inline check + 4.4 callback notify |
| Strategy 2 PC handling (`5b4092f`) | PC pre-set inside block-JIT creates noise | (compile-time const) | 4.7 |
| Variable-width detector (`fdce42c`) | Variable-width ISA can't fixed-stride decode | (per-set callback) | 4.4 lengthOracle |
| 0xCB prefix as atomic (`381595b`) | CB-prefix is ISA-level atomic, not switch | (sub-decoder) | 4.4 spec prefix_to_set |
| Immediate baking (`da8cf91`) | block-JIT doesn't need runtime read_imm bus call | (compile-time const) | instruction_word packing |
| WRAM/HRAM inline write (`787a8e5`) | 99% RAM writes pay extern cost they shouldn't | (region inline) | 4.6 + 4.5 |
| Cross-jump follow (`b9dd0dd`) | Block average 1.0-1.1 instr → dispatch can't amortize | (compile-time block extension) | detector follow + IsFollowedBranch flag |
| Block-local register shadowing V1 (`db9375c`) | state-struct access blocks mem2reg | (alloca + drain) | 4.2 EmitContext routing |
| Cross-jump-into-RAM unblock (`377379c`) | RAM regions can also be block targets, but need SMC support | (cross-region block) | env-gated; bundled with SMC V2 |

---

## 6. Costs and tradeoffs — what we paid

Every timing mechanism has costs. **Accurate timing isn't free** — this
section records the tradeoffs.

### 6.1 Direct costs (runtime)

| Cost | Caused by | Impact |
|---|---|---|
| **Larger IR** | sync step adds BB after each IRQ-relevant store; shadow alloca adds entry-load + exit-drain per block; SMC inline notify adds BB | LLVM compile slows down (per-block IR size +30-100%); higher x86 codegen register pressure |
| **Branch overhead** | sync exit / SMC notify / shadow drain all add conditional branches; fast path costs 1-2 extra cycles | Hot loop -1~5% MIPS |
| **Memory write piggyback** | each WRAM/HRAM write adds SMC coverage check; each MMIO write adds sync flag | Memory-heavy workloads heavily affected |
| **Recompilation** | re-compile after SMC invalidation; ORC LLJIT keeps old definitions without releasing → memory leak (slow accumulation) | Long-running + heavy-SMC ROM memory grows |
| **Block fragmentation** | HALT/STOP/EI/IRQ-relevant store split block boundaries; when block average is 1.0-1.1 instr, dispatch overhead can't amortize | BIOS LLE bjit actually slower than per-instr |

### 6.2 Indirect costs (design / engineering)

| Cost | Impact |
|---|---|
| **Every timing primitive needs a spec-level interface** | Can't hardcode in C# emitter; must design a spec micro-op; new CPU port must review which forms apply |
| **Lockstep diff harness becomes mandatory** | Any timing change requires `--diff-bjit=N` validation; test loop slows but avoids silent corruption |
| **Cycle drift unavoidable** | bjit and per-instr will drift by a cycle or so; accepted as long as functionality tests aren't affected |
| **Higher debug difficulty** | When bjit output is wrong, must first determine timing vs logic; diff harness finds first divergence point; root cause is often N instructions earlier |
| **Code coverage** | Every sync exit / shadow drain / SMC notify is a new code path; T1 unit tests can't fully cover, must rely on T2 (screenshot / Blargg) |

### 6.3 Concrete tradeoff record — what we chose

| Decision | Our choice | Alternative not chosen | Why |
|---|---|---|---|
| Cycle accuracy level | **block boundary + sync exit** = semi-cycle-accurate | per-cycle (every cycle aligns) | per-cycle yields zero batch benefit; semi-cycle-accurate is sufficient for commercial GBA / GB ROMs |
| SMC detection method | **lazy** — bus.WriteByte path does 1-byte counter check | eager — page-protection + segfault handler | cross-OS consistency + no dependence on OS-level page faults |
| MMIO catch-up point | **callback at bus.WriteByte moment** | batch tick after block ends | mid-block MMIO write (e.g. LCDC) followed by read (e.g. STAT) in the same block must see correct value |
| IRQ delivery granularity | **MMIO write triggers sync flag, exits block immediately** | wait until block finishes, then check | IRQ N cycles late breaks PPU sync |
| Defer mechanism | **AST pre-pass injects phantom instruction** | runtime counter | counter path adds register pressure; compile-time injection has zero runtime cost |
| Shadow alloc scope | **unconditionally alloc 7 GPR + F + SP** | live-range analysis precise alloc | V1 simple impl; perf -4% acceptable; V2 deferred to flip into positive |
| Cross-jump-into-RAM | **env-gated default OFF** | always on / always off | mechanism done but interaction with cycle drift unresolved; preserve V1 default correctness |
| Illegal opcode handling | **synthesize 1-byte NOP** | treat as HALT / throw entire emulator | NOP is consistent with mainstream emulators; HALT would hang test framework |
| Block average length goal | **5-20 instr** (after cross-jump follow) | 64 instr (max cap) | 64 too large slows IRQ delivery; 5-20 is the sweet spot |

### 6.4 Timing handling we **didn't do** — accepted losses

| Not handled | Consequence | Why not |
|---|---|---|
| Real per-cycle bus contention | GBA 32-bit ROM read should be 1 cycle slower; spec cycle.form doesn't distinguish | spec doesn't have this layer; commercial ROM impact minimal |
| HALT bug (LR35902 IME=0 + IRQ pending) | Real HW PC doesn't advance 1 instr; we advance | rare and Blargg doesn't test it |
| ARM7 SWI cycle stretch | Entering SVC mode adds a few cycles | jsmolka doesn't test it; GBA BIOS uses SVC, impact uniform |
| OAM bug (LR35902 inc/dec HL during OAM DMA) | Visual corruption | only visible in sprite-heavy commercial ROM |
| Full STAT IRQ blocking | LCDC.0=0 → STAT IRQ fires immediately behaviour | conservative MMIO sync flag covers most cases |

**Principle**: HW behaviour → spec → block-JIT, accuracy decreases at
each step. Spec level (per-instr) keeps up with hardware 99%; block-JIT
keeps up with spec 99%. Combined ~98% correctness. Sufficient for
commercial GBA / GB software; cycle-accurate emulation tier still a
distance away.

---

## 7. Cross-CPU validation — the framework genericity result

To prove this design really is framework-level (not ad-hoc patches):

### 7.1 One spec emit pipeline runs both ISAs

ARM (32-bit fixed-width with cond field + pipeline-PC) and LR35902
(8-bit variable-width with prefix + flat-PC) **share the same**
BlockFunctionBuilder / EmitContext / micro-op registry. The differences
are only in spec JSON descriptions and a single lengthOracle callback.

### 7.2 Pattern reuse across CPUs

| Pattern / Method | Used by ARM? | Used by LR35902? | Third-CPU candidate |
|---|---|---|---|
| Predictive cycle downcounting | ✅ | ✅ | ✅ any CPU |
| MMIO catch-up callback | ✅ (GBA bus) | ✅ (GB bus) | ✅ any CPU with MMIO |
| Sync micro-op | ⚠️ usable (ARM has fewer IRQ-relevant writes than LR35902) | ✅ | ✅ 6502/65816 |
| Defer micro-op | (ARM doesn't delay IRQ enable but has SVC delay) | ✅ EI | ✅ Z80/STM/RISC-V fence.i |
| Strategy 2 PC | ✅ (pc+8) | ✅ (pc+length) | ✅ any pipeline-PC ISA |
| Variable-width detector | (ARM/Thumb fixed-width) | ✅ | ✅ 6502/8080/Z80 |
| Prefix sub-decoder | (not in ARM) | ✅ CB | ✅ Z80 DD/FD/ED, x86 REX/VEX |
| Immediate baking | ✅ (ARM imm field) | ✅ (read_imm8/16) | ✅ any imm encoding |
| Block-local shadow | ⚠️ (ARM not enabled because of GepGprDynamic, but mechanism available) | ✅ | ✅ any narrow-int CPU |
| SMC infrastructure | ⚠️ (GBA cart RAM rarely self-modifies) | ✅ | ✅ any cached + writable code platform |
| WRAM/HRAM inline write | (GBA not done yet but same mechanism) | ✅ | ✅ any fixed memory map platform |
| Cross-jump follow | (ARM/Thumb capable, not done) | ✅ | ✅ any unconditional + computable target ISA |

### 7.3 One lockstep diff harness validates both ISAs

`--diff-bjit=N` works directly for ARM jsmolka and LR35902 Blargg; diff
logic is spec-driven, no ARM/LR35902-specific comparison code.

### 7.4 One SpecCompiler / DecoderTable handles variable + fixed width

DecoderTable mechanism (mask/match) works for both ARM 32-bit
instruction word and LR35902 8-bit opcode; instruction_word is uint —
full 32 bits for ARM, 1-3 byte LE-packed (with imm baking) for LR35902.

---

## 8. Design value — what this set of mechanisms buys

Summarize everything above in 4 points:

1. **Timing is a first-class concern of the framework, not an add-on**
   - Not "build emulator first, add timing later"; instead, decide at
     design time how timing primitives enter spec / IR
   - Result: each new CPU port doesn't need to reinvent sync / defer /
     SMC / region inline concepts

2. **Cost of abstraction is spec design effort, not runtime cost**
   - micro-ops vanish at IR stage (compile-time lowered); runtime sees
     no abstraction trace
   - Examples: defer micro-op flattens at AST pre-pass; sync micro-op
     emits as conditional br; shadow alloca gets promoted to SSA value
     by mem2reg

3. **Tradeoffs are explicitly recorded, not hidden in code**
   - Each tradeoff has a corresponding spec field / env var / commit
     message
   - Example: cross-jump-into-RAM is the `APR_CROSS_JUMP_RAM=1` switch;
     a future reader sees the env var name and immediately knows there's
     a dimension to consider
   - Not "why is block_size capped at 64" buried in a magic constant

4. **Validation mechanisms are part of the framework**
   - lockstep diff harness, T2 8-combo screenshot matrix, Blargg suite
     are all framework infrastructure
   - Any timing change must pass these three gates; correctness
     regression probability is significantly reduced

---

## 9. Reminders for future me

1. **Before adding a new timing mechanism**: which of Pattern A/B/C does
   it fall under? If none, the design might be wrong

2. **Before adding a new micro-op to spec**: will N CPUs use it? If only
   one CPU uses it, don't abstract yet — wait until a second CPU also
   needs it before promoting to framework

3. **Any "extra extern call for timing" added in block-JIT should be
   measured** — extern call ~10ns; an entire block-JIT gain might be
   wiped out by a single extern call

4. **When you hit cycle drift, don't rush to fix**: first use diff
   harness to confirm whether it's real drift or a compile-time path
   divergence; many drifts are LLVM optimization tweaks (not affecting
   functionality)

5. **SMC, cross-jump, and block-local cache interact in complex ways**
   — changing two simultaneously is hard to debug; one-at-a-time changes
   + full verification

6. **Accurate timing vs framework genericity will fight**: making a
   specific CPU's quirk extra-precise will tempt you to hardcode; resist,
   spec-ize it, accept some perf loss

---

## 10. References

- `MD/design/12-gb-block-jit-roadmap.md` — overall GB block-JIT roadmap
- `MD/design/13-defer-microop.md` — defer micro-op design
- `MD/design/14-irq-sync-fastslow.md` — sync micro-op + bus extern split
- `MD/performance/202605040000-gb-block-jit-p0-complete.md` — P0 completion
- `MD/performance/202605041800-gb-block-jit-p1-complete.md` — P1 completion
- `tools/knowledgebase/message/20260503_*.txt` — Gemini consultation
  records (QEMU TCG / FEX-Emu / Dynarmic / mGBA / Dolphin design
  references)
