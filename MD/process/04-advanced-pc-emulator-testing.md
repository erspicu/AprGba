# Advanced PC-emulator testing tools (reference notes)

When the basic test path (NASM `.COM` + Port 0xE9 + AutoTester, see
[`03-dos-test-injection-workflow.md`](03-dos-test-injection-workflow.md))
isn't enough — i.e. we're trying to verify **non-deterministic** or
**hardware-state-machine** behaviour like the x87 FPU, PIT/PIC/DMA
chips, or 80186-specific PCB registers — these are the field-tested
tools and strategies. Kept as a reference so we don't re-derive them
under pressure when the time comes.

> **Status**: not yet adopted in AprPc. This document is a forward-
> looking shopping list. Sections are tagged 🟢 ready-to-pull-in,
> 🟡 partially blocked (needs other plumbing first), or 🔴 long-term.

## 1. x87 (8087) FPU verification

The 8087 is awkward to test because it has its own 80-bit stack
register file (ST0..ST7), independent rounding modes (RC bits in
control word), exception flags in the status word, and instructions
(`FPTAN`, `FYL2X`, `FSIN`) whose precision is hard to eyeball at
extreme values.

### 1.1 🟢 `x87-test-suite` (Intel test vectors, community-maintained)

- **Where to find**: GitHub search for `x87-test-suite`; MAME's
  `src/devices/cpu/i86/` test fixtures sometimes embed similar vectors.
- **Shape of the data**: massive JSON / binary files where each row is
  `[initial stack, control word, status word] → [instruction]
  → [expected ST0..ST7, status word, exception flags]`.
- **What it catches**: corner cases in `FPTAN`, `FYL2X`, `F2XM1` and
  the transcendental family; round-to-nearest-even ties; subnormal
  / NaN / ±∞ propagation.
- **AprPc integration cost**: low — same harness shape as the Tom
  Harte 8088 SST loader we already use for 8086 integer tests
  (`X86TomHarteTests.cs`). New xUnit class `X87TomHarteTests.cs`
  parses the JSON and drives the FPU through `X86JsonCpu`. No
  Windows / DOS dependency — purely unit-test layer.
- **Recommended trigger**: when we add a new x87 instruction or
  touch `X86Fpu` rounding logic. Currently x87 functional baseline
  closed in [`MD/performance/202605160100-x87-fpu-functional-complete.md`](../performance/202605160100-x87-fpu-functional-complete.md),
  so this is the *next* layer up.

### 1.2 🟡 IBM PC/XT 8087 Diagnostic Disk

- **Where to find**: Vetusware / Internet Archive — search
  `"IBM PC/XT 8087 Diagnostic"`. Usually a `.IMG` floppy image.
- **How it works**: bootable DOS-era diagnostic that runs an
  end-to-end stress test of every 8087 instruction, prints results
  to screen, halts on failure with a specific error number.
- **AprPc integration**: mount as `--floppy-a=...`, no special
  flags. Output goes through whatever video adapter is active.
- **What it catches**: integration bugs (FPU + IRQ wiring on
  XT — 8087 raises IRQ 13 via NMI, which we don't currently
  emulate); NaN / infinity handling under interrupt; control-word
  exception masking.
- **Blocker today**: the diagnostic likely expects 8087 IRQ wiring
  through NMI (XT-specific); our current FPU integrates without IRQ
  generation. Reading the diagnostic source / disassembly first to
  see what it actually probes is worth the hour before downloading.

## 2. I/O bus + peripheral chip testing

Verifying `Read8(port)` / `Write8(port, val)` for the support chips
(8253 PIT, 8259 PIC, 8237 DMA, MC146818 RTC, 8042 keyboard) by hand
gets old fast. These DOS-era diagnostics do it for us.

### 2.1 🟢 Landmark System Speed Test (v2.0 – v6.0)

- **Where to find**: My Abandonware, various DOS shareware archives.
- **What it does**: bypasses DOS, probes hardware directly from a
  ring-0 driver. The famous "MHz score" is computed by programming
  PIT channel 2 with a known divisor, busy-looping a known
  instruction count, then reading the latched counter.
- **Strongest signal value for us**:
  - **PIT 8253**: if `OUT 0x43` mode-set / latch + `IN 0x40`
    counter-read isn't bit-exact, the MHz score comes out 0 or
    the test hangs.
  - **VRAM bandwidth**: hammers `0xB8000` / `0xA0000` with `rep
    stosw` and times. Will surface FDC/DMA / CGA-status-port
    coupling bugs that single-instruction tests don't.
- **AprPc integration**: mount on B: (`--floppy-b`) once we have
  a quick way to push a `.COM` + a small AUTOEXEC.BAT wrapper to
  the existing FreeDOS A: floppy that does `B:\LANDMARK.COM`.
- **First-pass success criterion**: not the *correctness* of the
  reported MHz — that's tied to host wall-clock anyway — but
  **completing the run without crash / hang / "?MISMATCH"**.

### 2.2 🟢 CheckIt 3.0 / 4.0 for DOS

- **Where to find**: same archives as Landmark. (Original publisher
  was TouchStone; long since abandoned.)
- **Why it's the heaviest hitter for our use case**:
  - **System Evaluation → Interrupt Test**: actively triggers
    each IRQ line and verifies the PIC routes it to the right
    INT vector at the right priority. Tests IMR (`OUT 0x21`)
    masking, in-service register (`ISR`), end-of-interrupt
    (`OCW2 0x20`) handshakes. **This is the one tool that will
    say "your PIC has bug X" instead of "something didn't work."**
  - **DMA test**: programs ch1 / ch2 / ch3 transfers and verifies
    page-register + count-register + transfer-mode behaviour.
    Catches the kind of mis-wired flip-flop we hit in 30.3-30.5.
  - **CMOS / RTC test**: actually reads back day / hour / minute
    and verifies they advance. The `ver` time-display hang we
    chased in Phase 30.7 (BCD-vs-binary CMOS read) would have
    been a single CheckIt run.
- **AprPc integration**: same as Landmark.
- **🟡 Blocker for full pass**: needs **slave PIC at 0xA0** (we're
  master-only today — `src/AprPc.Cli/Hardware/Pic8259.cs:27` is
  XT-class single PIC). CheckIt's interrupt cascade test will
  refuse to proceed if IRQ8-15 don't route. See §4 below.

## 3. 80186-specific testing — MAME PCB model

The 80186 / 80188 integrate the PIT + PIC + DMA *inside the CPU* as
the **Peripheral Control Block (PCB)**, mapped at I/O `0xFF00`
by default (relocatable via the RELREG register at `0xFFFE`). This
is *not* the IBM PC layout and won't be exercised by anything DOS-era
on standard XT hardware. The 80186 was used in arcade boards.

### 3.1 🟢 MAME `i186.cpp` PCB unit tests

- **Where**: <https://github.com/mamedev/mame/tree/master/src/devices/cpu/i86>,
  specifically `i186.cpp` + the test harness that exercises it.
- **Why**: MAME drives 80186-based arcade boards (90s fighters,
  shmups) in production. Their PCB model is the closest thing to
  a reference implementation, and the unit tests document the
  expected I/O sequences (timer reload, IRQ priority,
  DMA chain mode) one PCB register at a time.
- **Integration path**: don't re-port their C++ — just **mine the
  test vectors**. Each test is `OUT <pcb_reg>, <val>; expect <side
  effect>`. Translate to xUnit:
  ```csharp
  [Fact]
  public void Pcb_Timer0_ReloadOnZero()
  {
      var pcb = new I186Pcb();
      pcb.WriteIO(0xFF50, 0x0010);   // T0_COUNT = 16
      pcb.WriteIO(0xFF56, 0xC001);   // T0_MODE: ENA + retrigger
      for (int i = 0; i < 17; i++) pcb.Tick();
      Assert.True(pcb.Timer0Wrapped);
  }
  ```
- **Status here**: AprPc currently runs i80186 / i80188 as i8086 +
  26 new instructions. PCB isn't implemented yet — i80186 binaries
  that talk to PCB will read 0xFF. Adopt this when we want to run
  an actual 80186 board ROM.

## 4. The "minimum-cost in-house mock" pattern

For the cases where pulling in a DOS-era diagnostic is overkill,
write an xUnit test that drives the device class directly:

```csharp
// PIT 8253 latch-command shape (Phase 28.IO regression net)
[Fact]
public void Pit8253_LatchCommand_PreservesCounter()
{
    var pit = new PcPit();

    // Counter 0 in mode 3, programmed to N
    pit.WriteIO(0x43, 0x36);          // ctrl = counter0, lo/hi, mode3, binary
    pit.WriteIO(0x40, 0xFF);          // count lo
    pit.WriteIO(0x40, 0xFF);          // count hi

    pit.Tick(50);                     // 50 host cycles elapse

    // Latch the live count into a snapshot register
    pit.WriteIO(0x43, 0x00);          // 0x00 = counter0 latch (no R/W bits)

    byte lo = pit.ReadIO(0x40);
    byte hi = pit.ReadIO(0x40);
    ushort latched = (ushort)((hi << 8) | lo);

    Assert.InRange(latched, (ushort)(0xFFFF - 60), (ushort)(0xFFFF - 40));
}
```

Pros: no DOS dependency, runs in <100 ms, regression-locked into CI.
Cons: only tests the device in isolation — won't catch the kind of
**inter-chip** wiring bug (PIT → IRQ 0 → PIC → CPU → INT 8h handler)
that CheckIt finds.

**Rule of thumb**: write the xUnit mock test for every public method
on the device class as the device matures; reserve CheckIt /
Landmark for the inter-chip / full-stack confidence pass at
phase boundaries.

## 5. Prerequisite to running CheckIt: cascade slave PIC at 0xA0

`src/AprPc.Cli/Hardware/Pic8259.cs` is currently **master-only**
(IRQ 0-7). XT machines have only one PIC, so this matches the
pcxtbios.bin world. But:

- CheckIt's interrupt test exercises IRQ 8-15 (slave PIC routed to
  master IRQ 2). Will fail / hang without slave plumbing.
- 80186 PCB IRQ routing is internal and bypasses PIC entirely — so
  that path *doesn't* need the slave.
- AT-class BIOSes won't even POST without a slave at 0xA0/0xA1
  responding.

When we want to run CheckIt or any AT-class ROM, the cascade work
is the gating prerequisite. Rough scope:
- `Pic8259.cs` gains an `IsSlave` flag and a `Cascade` reference.
- Two instances wired: master at 0x20/0x21, slave at 0xA0/0xA1.
- IRR / ISR / IMR / OCW2 / OCW3 / ICW1-4 handshake.
- IRQ 8-15 from slave bumped via master IRQ 2.
- Spurious IRQ 7 / IRQ 15 handling.

Estimated effort: 2-3 days. Not blocking any current Phase 30
deliverable, so deferred until a Phase 30.x sprint specifically
targets AT-class compatibility or runs CheckIt as bring-up test.

## 6. Recommended adoption order

1. **🟢 x87 test vectors** (§1.1) — drop-in xUnit; biggest precision-
   bug-catching value per hour invested. Do this next time we touch
   FPU code.
2. **🟢 PIT 8253 / PIC / DMA xUnit mocks** (§4) — backfill while
   the implementation is fresh. Cheap insurance against future
   refactors.
3. **🟡 Landmark Speed Test on B: floppy** (§2.1) — fun smoke test,
   confirms the test-injection pipeline reaches a real piece of
   DOS-era software. No new emulator work needed.
4. **🟡 Slave PIC cascade** (§5) — required for CheckIt and AT
   ROMs. Plan as Phase 30.x.
5. **🟡 CheckIt full pass** (§2.2) — depends on (4). The "your PIC
   is 90% correct" headline test.
6. **🔴 80186 PCB via MAME vectors** (§3) — only if/when we want
   to run an 80186 arcade board ROM. No business case yet.

## References

- [`MD/process/03-dos-test-injection-workflow.md`](03-dos-test-injection-workflow.md) —
  the underlying test-injection plumbing this doc builds on
- [`MD/performance/202605160100-x87-fpu-functional-complete.md`](../performance/202605160100-x87-fpu-functional-complete.md) —
  current FPU baseline; §1.1 picks up from here
- `src/AprPc.Cli/Hardware/Pic8259.cs` — master-only PIC, slave
  cascade described in §5
- `src/AprCpu.Tests/X86TomHarteTests.cs` — existing template for
  large external-test-vector xUnit harnesses (the x87 suite
  would follow the same shape)
