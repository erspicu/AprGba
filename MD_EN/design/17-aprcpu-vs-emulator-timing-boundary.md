# AprCpu Framework vs Emulator Shell — Timing Responsibility Split

> **Status**: design + introduction (2026-05-05)
> **Scope**: Draws an explicit line between "which timing concerns the
> `AprCpu` framework owns" and "which timing concerns the emulator shell
> (AprGba / AprGb / future hosts) owns", describing the contract interfaces
> and responsibility boundary between the two sides.
>
> **Target audience**: (a) people wanting to push AprGba/AprGb to a complete
> emulator; (b) people wanting to use the `AprCpu` framework to build a new
> emulator (e.g. 6502 / Z80 / 8080); (c) future-me wanting to understand
> "where the framework boundary actually is".
>
> **Cross-references**:
> - [`MD_EN/design/15-timing-and-framework-design.md`](/MD_EN/design/15-timing-and-framework-design.md) — "**how** we get timing into the framework" (concepts and methods)
> - This doc — "**who is responsible for what**" (responsibility split and contract interfaces)
> - [`MD_EN/design/16-emulator-completeness.md`](/MD_EN/design/16-emulator-completeness.md) — "what timing-related subsystems the emulator shell still lacks" (completeness audit)

---

## 1. Why draw this line?

`AprCpu` is a **CPU emulation framework**, not a **platform emulator**. The
framework's job is to "correctly and efficiently execute the CPU instruction
stream described by the spec"; the platform emulator's job is to "wrap the
CPU with PPU, APU, Timer, DMA, Joypad, cartridge, etc., to produce a real
machine".

The timing responsibilities of these two are **complementary but
non-overlapping**:

- The framework owns "**how many cycles did this instr take**", "**when does
  the block hand control back**", "**when should an IRQ be checked**"
- The emulator owns "**how many cycles until the next PPU event**", "**after
  writing 0xFF40 LCDC, what STAT does the next instr see**", "**how should
  IF.bit-2 be set on Timer overflow**"

Why drawing this line clearly is valuable:

1. **A new CPU port doesn't redo emulator scaffolding.** Swap ARM7TDMI for
   6502, framework side untouched; the new emulator just implements the
   6502's PPU/Timer/IO talking to the same set of interfaces.
2. **A new emulator port doesn't redo the CPU framework.** AprGba shipped,
   AprGb shipped; a new platform (e.g. NES / SMS / Z80 PC) swaps PPU/APU
   while completely reusing the CPU + framework.
3. **Bug isolation.** When timing is wrong we can ask first "is the CPU's
   cycle count wrong (framework bug) or is the HW tick wrong (emulator
   bug)?" — rather than excavating both sides simultaneously.
4. **Avoid framework rot.** If the framework is stuffed with PPU/APU
   specifics it gets coupled to a platform; a third CPU port has to tear
   that out again.

---

## 2. Responsibility split overview

| Timing behavior | Owner | Interface |
|---|---|---|
| Per-instr cycle cost | framework (parsed from spec `cycles.form`) | spec JSON |
| Block-JIT in-block cycle deduct + budget exhaustion | framework (predictive downcounting in IR) | `state.cycles_left` |
| Per-instr cycle tick (per-instr backend) | framework (inside CpuExecutor) | `bus.Tick(N)` callback |
| **HW model advancement** (PPU dot / Timer counter / APU sample) | **emulator** | `Scheduler.Tick(N)` |
| **Cycles until next HW event** | **emulator** (Scheduler computes) | `Scheduler.CyclesUntilNextEvent` |
| IRQ source set on HW event (e.g. VBlank, Timer overflow) | emulator | `bus.RaiseInterrupt(source)` |
| IRQ pending bitmask maintenance | emulator (in bus IF / IE) | bus internals |
| **IRQ delivery sequence** (CPU mode swap, PC=vector, SPSR save) | **framework** | `CpuExecutor.DeliverIrqIfPending()` |
| When to check IRQ (instruction boundary) | framework (per-instr / block-exit) | framework automatic |
| Delayed-effect instruction (EI, STI) | framework (`defer` micro-op) | spec micro-op |
| Mid-block control yield (IRQ-relevant write) | framework (`sync` micro-op) | spec micro-op + bus extern return value |
| Push HW state to "now" on MMIO write | **emulator** (bus implements catch-up callback) | `bus.OnMmioWrite(addr, ...)` |
| Self-modifying code detection | framework (BlockCache coverage counter) | `bus.WriteByte` routes through framework shim |
| Pipeline-PC reads | framework (Strategy 2 baking) | spec `pc_offset_bytes` |
| Save state / battery RAM (.sav) | emulator | host file I/O |
| Joypad / keyboard input | emulator | host input event |
| Audio output streaming | emulator | host audio backend |
| **Frame timing** (60Hz wall-clock sync) | **emulator** (not yet done) | host vsync / sleep |

**Mnemonic**:
- **Framework owns CPU internals** — instruction cycle accounting, IRQ
  delivery flow, block-JIT machinery
- **Emulator owns CPU externals** — HW state advancement, HW event
  triggering, host UI/IO

---

## 3. Interface contracts (contract API)

The two sides interact through the following contracts. **The shapes of
these interfaces are framework-level design; new emulator ports should
implement them.**

### 3.1 `IMemoryBus` interface — emulator → framework memory + IRQ entry

```csharp
public interface IMemoryBus
{
    byte   ReadByte(uint addr);
    void   WriteByte(uint addr, byte value);
    ushort ReadHalf(uint addr);
    void   WriteHalf(uint addr, ushort value);
    uint   ReadWord(uint addr);
    void   WriteWord(uint addr, uint value);

    // Framework knows nothing of the HW model; only calls Tick to push cycles to the emulator
    void Tick(int tCycles);

    // Emulator looks at its own IF/IE to decide IRQ pending; framework does not parse IF
    bool   IrqPending { get; }

    // Tells framework whether boot is BIOS LLE or HLE
    bool   BiosEnabled { get; }

    // Pinned RAM region byte arrays — for framework's IR-level inline path
    byte[] Wram { get; }
    byte[] Hram { get; }
}
```

**Who implements**: emulator (e.g. [`GbaMemoryBus.cs`](/src/AprCpu.Core/Runtime/Gba/GbaMemoryBus.cs),
[`GbMemoryBus.cs`](/src/AprGb.Cli/Memory/GbMemoryBus.cs)).

**Who uses**: framework's `CpuExecutor` and the block-JIT bus extern.

**Contract highlights**:
- `ReadByte/WriteByte` are pure memory access; the emulator handles region
  routing internally (ROM / RAM / IO / VRAM / OAM, etc.)
- `Tick(N)` means "please advance HW by N cycles"; in per-instr mode the
  framework calls it once per instruction; in block-JIT mode it is not
  called directly (uses the catch-up callback path)
- `IrqPending` is a boolean — how the emulator computes it internally
  (`(IF & IE) != 0 && IME`) is the emulator's business; framework does not
  touch

### 3.2 `cycles_left` budget — framework ↔ emulator cycle accounting

```
emulator side (per Step):
    bus.cycles_left = scheduler.CyclesUntilNextEvent;   // emulator computes
    cpu.Step();                                          // framework runs
    consumed = bus.cycles_left_before - bus.cycles_left; // framework deducted
    scheduler.Tick(consumed);                            // emulator advances HW
```

**Who computes the budget**: emulator's Scheduler — it knows how many
cycles until PPU's next HBlank/VBlank, how many cycles until Timer
overflow; the minimum is the budget.

**Who deducts the budget**: framework's block-JIT IR — each instruction
subtracts its cycle cost from the budget; once ≤0, the block exits early.

**Why split this way**:
- Framework does not know "what is the next HW event" — that's the
  emulator's PPU / Timer territory
- Emulator does not want to know "how block-JIT deducts" — that's a
  framework-internal optimization

### 3.3 Bus extern sync flag — block-JIT mid-block yield

```csharp
// In emulator (e.g. JsonCpu.MemWrite8Sync):
[UnmanagedCallersOnly]
private static byte MemWrite8Sync(uint addr, byte value)
{
    bus.WriteByte((ushort)addr, value);
    bus.NotifyMemoryWrite(addr);     // SMC notify
    return IsIrqRelevantAddress(addr) ? (byte)1 : (byte)0;
    //     ^^^^^^^^^^^^^^^^^^^^^^^^^^
    //     emulator decides which addrs may flip IRQ state on write
}
```

**Who decides which addrs are IRQ-relevant**: emulator (because IF/IE live
in the emulator's bus state).

**Who handles the sync flag**: framework — the `sync` micro-op emits IR
that receives the i8 return value; if non-zero, mid-block ret void; on the
next dispatch the emulator re-checks IRQ.

**Why split this way**: the framework has no concept of "IRQ-relevant
address" (every platform is different — GBA has 0x04000200 IF / 0x04000202
IE, GB has 0xFF0F IF / 0xFFFF IE); framework only handles "what to do with
sync flag = 1, i.e. how to yield".

### 3.4 MMIO catch-up callback — HW sync outside the block-JIT

```csharp
// emulator's bus.WriteByte before processing an MMIO write:
public void WriteByte(uint addr, byte value)
{
    if (IsMmio(addr)) {
        // mid-block: framework has consumed some cycles but PPU hasn't seen them yet
        var consumed = budgetAtStep - cpu.CyclesLeft;
        if (consumed > 0) {
            scheduler.Tick(consumed);   // push PPU forward to "now"
            budgetAtStep = cpu.CyclesLeft;
        }
    }
    // actual write
    DoActualWrite(addr, value);
}
```

**Who computes "cycles since the start of this step"**: emulator (diffs
against the `cpu.CyclesLeft` snapshot).

**Who decides whether to catch up**: emulator (only MMIO writes need it;
RAM writes do not).

**Framework's role**: expose `cpu.CyclesLeft` for the emulator to read.

### 3.5 SMC notify — should a RAM write invalidate cached blocks

```csharp
// after the emulator's bus.WriteByte:
public void WriteByte(uint addr, byte value)
{
    DoActualWrite(addr, value);
    cache?.NotifyMemoryWrite(addr);      // framework's BlockCache scans itself
}
```

**Who maintains the coverage counter**: framework (in
`BlockCache._coverageCount`).

**Who triggers notify**: emulator (in bus.WriteByte and the IR-level
inline RAM write path).

**Subtlety at the boundary**: block-JIT's IR-level inline RAM write
(P1 #7) goes through host C# bus path, but the framework also has IR-level
coverage check + slow-path notify (`EmitSmcCoverageNotify`, env-gated).
**Both paths — emulator-side NotifyMemoryWrite call and framework-side
IR-level inline notify — must exist** for coverage to be complete.

### 3.6 IRQ delivery sequence — pure framework responsibility

```csharp
// emulator's SystemRunner main loop:
while (running) {
    cpu.Step();
    if (bus.IrqPending && (cpsr & I_BIT) == 0)
        cpu.DeliverIrq();   // framework handles mode swap, PC, SPSR internally
}
```

**Who decides IRQ pending**: emulator (via `bus.IrqPending` checking
`IF & IE`).

**Who decides whether to deliver**: emulator + framework jointly —
emulator looks at pending; framework knows "are we at an instruction
boundary?" and "is the I-bit enabled?".

**Who executes delivery**: framework (`CpuExecutor.DeliverIrqIfPending`:
save SPSR_irq, CPSR mode → IRQ + I-bit, bank swap, R14_irq = PC + 4,
PC = 0x18).

**What the emulator must NOT do**: manually write R14 / PC / CPSR to fake
an IRQ entry. **That is ARM7TDMI spec behavior; it is the framework's
responsibility.**

### 3.7 Pinned RAM base pointer — used by block-JIT IR-level inline

```csharp
// during emulator's Reset(bus):
_wramHandle = GCHandle.Alloc(bus.Wram, GCHandleType.Pinned);
_rt.BindExtern("lr35902_wram_base", _wramHandle.AddrOfPinnedObject());
//                                  ^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
//                                  pinned address — IR baked as constant
```

**Who pins**: emulator (using GCHandle.Alloc Pinned).

**Who binds**: emulator (via framework's `HostRuntime.BindExtern`).

**Who uses**: framework's IR emitter
(`Lr35902Emitters.EmitWriteByteWithSyncAndRamFastPath`).

**Boundary point**: framework does not manage byte-array lifetime or GC
pinning — that's host C#'s business; the emulator must guarantee the pin
remains alive while framework JIT'd code runs.

---

## 4. Walk-through — who does what during one frame

Using GBA as an example, describing how framework and emulator interact
during "running one emulated frame":

```
emulator                              framework (AprCpu)
========                              ==================

1. SystemRunner.Step() begins
   |
2. budget = scheduler.CyclesUntilNextEvent
   bus.cycles_left = budget                    <-- framework reads from state
                                                   into block, starts deducting
3. cpu.Step()  ----------------------------> 4. block-JIT cache lookup PC
                                                |
                                             5. cache hit -> jump to fn
                                                cache miss -> compile new block
                                                |
                                             6. block runs N instrs
                                                each instr deducts cycle cost
                                                cycles_left ≤ 0 -> exit
                                                or PcWritten=1 -> exit
                                                |
                                             7. block fn returns
                                                framework reads from state:
                                                cycles_left, PC, PcWritten
                                                |
                                             8. (per-instr) bus.Tick(stepCycles)
                                                                        <-- framework calls emulator
9. (block-JIT)
   consumed = budget - cycles_left
   scheduler.Tick(consumed)
   |
10. scheduler advances PPU/Timer itself
    HBlank entry -> bus.RaiseInterrupt(HBlank)
    Timer overflow -> bus.RaiseInterrupt(Timer)
    |
11. dma.TickIfTriggered()
    (HBlank/VBlank-triggered DMA fires)
    |
12. cpu.DeliverIrqIfPending()  --------> 13. framework checks bus.IrqPending && I-bit
                                                IRQ entry sequence:
                                                save SPSR_irq, mode swap,
                                                bank swap, R14, PC=0x18
                                                |
14. SystemRunner.Step() ends
    return to main loop, continue next Step
    or this frame is done, render PPU framebuffer
```

**Where each side participates**:
- steps 1, 2, 9, 10, 11, 14: emulator-only
- steps 4, 5, 6, 7, 13: framework-only
- steps 3, 8, 12: cross-boundary call

**Catch-up path (mid-block MMIO read/write)** is inserted inside step 6:
```
6. block runs instr
   encounters write to MMIO addr
     -> bus.WriteByte (extern call out of JIT'd code)
        emulator side: tick scheduler to bring PPU up to "now" (step 9 done early)
        emulator side: perform the actual write
        emulator side: return sync flag = 1 if IRQ-relevant
     -> framework receives sync=1
        write next-PC + PcWritten=1 + ret void
        block exits early
```

---

## 5. Failure modes when the responsibility boundary is wrong

The following are what happens when cross-boundary contracts are violated,
and which side is at fault:

| Symptom | Cause | Whose to fix |
|---|---|---|
| DIV register stays 0 forever | emulator scheduler doesn't Tick / doesn't advance Timer | emulator |
| Block-JIT is 2× slower than per-instr | emulator scheduler.CyclesUntilNextEvent always returns 1 (budget never amortizes) | emulator |
| After writing LCDC.0=0, the next instr still sees LCD on | emulator bus.WriteByte missed the catch-up | emulator |
| IRQ never delivers | emulator bus.IrqPending always false (IF/IE not set) | emulator |
| After IRQ delivers, PC jumps to the wrong vector | framework's IRQ entry sequence is wrong | framework |
| EI immediately interrupted by IRQ (should be 1 instr later) | framework `defer` micro-op didn't handle EI | framework (spec) |
| Self-modifying ROM produces garbage | framework BlockCache didn't invalidate | framework — or emulator didn't call NotifyMemoryWrite |
| Pipeline-PC reads see wrong values | framework Strategy 2 PC isn't handled correctly | framework |
| Commercial games have no audio at all | emulator hasn't implemented APU | emulator |
| Pokemon RBY hits an invalid opcode | emulator MBC3 not implemented (sees bank 0 instead of the real bank) | emulator |

**Rule**: HW model issues → emulator; CPU model + instruction cycle issues
→ framework.

---

## 6. How to use this doc when adding a new platform / new CPU

### Case A — Add a new emulator (e.g. NES, CPU 6502 spec already exists)

What you need to do:

1. Write `NesMemoryBus` implementing `IMemoryBus` (NES memory map: CPU RAM /
   PPU regs / APU regs / cart)
2. Write `NesScheduler` (PPU timing: 3 PPU dots = 1 CPU cycle, scanline
   counting, NMI on VBlank entry)
3. Write `NesPpu` (NES 256×240 framebuffer, background nametable, sprite
   OAM evaluation, scrolling registers $2005/$2006)
4. Write `NesApu` (5 channels: square × 2 + triangle + noise + DMC)
5. Write `NesSystemRunner` (loop: `cpu.Step → scheduler.Tick → DeliverNmi`)

**No framework changes needed.** Once the NES 6502 spec JSON is written,
it just works.

### Case B — Add a new CPU (e.g. RISC-V, no spec yet)

What you need to do:

1. Write `spec/riscv/cpu.json` (register file, condition codes, exception
   model)
2. Write encoding-format groups under `spec/riscv/groups/` (R-type / I-type
   / S-type / B-type / U-type / J-type)
3. Check whether the spec needs new micro-ops; if it's truly new behavior
   (e.g. RISC-V's fence.i), try spec-level first; only consider adding a
   framework primitive if the spec can't express it
4. Write unit tests covering spec decoding and basic ALU
5. Hook to a new platform emulator (e.g. running RISC-V on the NES platform
   would just swap the spec)

**Framework changes minimized.** **Zero emulator changes** (if reusing an
existing platform).

### Case C — Add new timing behavior (framework-level)

For example: "I want to add a `cycle_count`-style defer". The flow:

1. Confirm this is genuinely not platform-specific (would two or more
   CPUs / platforms use it?)
2. Add a framework primitive (spec field, micro-op name, IR emit logic)
3. Document in [`MD_EN/design/15-...md`](/MD_EN/design/15-timing-and-framework-design.md)
   why it was added and which Pattern A/B/C it belongs to
4. Cover with unit tests
5. Have at least one existing spec adopt it to avoid dead code

---

## 7. Maintenance guidelines for this doc

- **New framework primitives go in the doc tables** (§2 + §3) — every
  spec micro-op / extern callback added requires a corresponding row
- **Emulator-side design patterns do not go in this doc** — those go in
  platform-specific docs (e.g. GBA PPU details go in
  [`MD_EN/design/16-emulator-completeness.md`](/MD_EN/design/16-emulator-completeness.md))
- **Walk-through (§4) updates when out of date** — update on adding new
  sync points / catch-up points
- **§5 failure mode table is for debugging** — append when hitting a new
  cross-boundary bug

---

## 8. Reference

- [`MD_EN/design/12-gb-block-jit-roadmap.md`](/MD_EN/design/12-gb-block-jit-roadmap.md) — block-JIT progress and P0/P1 mechanisms
- [`MD_EN/design/13-defer-microop.md`](/MD_EN/design/13-defer-microop.md) — defer micro-op design (framework primitive example)
- [`MD_EN/design/14-irq-sync-fastslow.md`](/MD_EN/design/14-irq-sync-fastslow.md) — sync micro-op + bus extern split (cross-boundary example)
- [`MD_EN/design/15-timing-and-framework-design.md`](/MD_EN/design/15-timing-and-framework-design.md) — three architectural patterns and 9 generalization methods
- [`MD_EN/design/16-emulator-completeness.md`](/MD_EN/design/16-emulator-completeness.md) — emulator shell completeness audit
