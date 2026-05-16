# Advanced PC-emulator testing tools (EN mirror)

> Brief mirror of `MD/process/04-advanced-pc-emulator-testing.md` per
> CLAUDE.md bilingual-docs rule. Forward-looking shopping list of test
> tools beyond the basic NASM `.COM` + Port 0xE9 workflow.

## 1. x87 (8087) FPU verification

- **🟢 `x87-test-suite` (Intel test vectors)** — JSON vectors of
  `[initial stack + ctrl + status] → instruction → expected ST0..7`.
  Drop-in xUnit harness (same shape as our existing
  `X86TomHarteTests.cs`). Best ROI for transcendental / NaN / subnormal
  bugs.
- **🟡 IBM PC/XT 8087 Diagnostic Disk** — bootable `.IMG` from
  Vetusware / Internet Archive; mount as `--floppy-a`. Likely
  needs 8087 IRQ-via-NMI wiring (not implemented).

## 2. I/O bus + peripheral chips

- **🟢 Landmark System Speed Test (v2-v6)** — bypasses DOS, probes
  PIT divisor + VRAM bandwidth from ring 0. Will hang / report
  0 MHz if PIT 8253 mode-set / latch is broken.
- **🟢 CheckIt 3.0 / 4.0 for DOS** — System Evaluation actively
  triggers each IRQ, runs DMA transfers, reads RTC. Best
  inter-chip wiring test we have access to. **Blocked** by §5
  (slave PIC).

## 3. 80186-specific testing

- **🟢 MAME `src/devices/cpu/i86/i186.cpp` PCB unit tests** — mine
  the test vectors, port to xUnit. AprPc currently runs i80186 as
  i8086 + 26 instructions; PCB at 0xFF00 not yet emulated.

## 4. The minimum-cost in-house mock pattern

For everything else: write per-device xUnit tests against the
device class. Cheap insurance against future refactors. Won't
catch inter-chip wiring bugs — that's what §2 is for.

```csharp
[Fact]
public void Pit8253_LatchCommand_PreservesCounter()
{
    var pit = new PcPit();
    pit.WriteIO(0x43, 0x36);
    pit.WriteIO(0x40, 0xFF);
    pit.WriteIO(0x40, 0xFF);
    pit.Tick(50);
    pit.WriteIO(0x43, 0x00);
    byte lo = pit.ReadIO(0x40);
    byte hi = pit.ReadIO(0x40);
    Assert.InRange((ushort)((hi << 8) | lo),
        (ushort)(0xFFFF - 60), (ushort)(0xFFFF - 40));
}
```

## 5. Prerequisite: slave PIC cascade at 0xA0

`src/AprPc.Cli/Hardware/Pic8259.cs` is master-only today (XT class).
CheckIt's interrupt test exercises IRQ 8-15 via slave; AT-class ROMs
don't POST without it. Scope ~2-3 days. Deferred until a sprint
explicitly targets AT-class compatibility.

## Recommended adoption order

1. **🟢 x87 test vectors** — drop-in xUnit, biggest precision-bug ROI
2. **🟢 PIT / PIC / DMA xUnit mocks** — backfill while fresh
3. **🟡 Landmark on B: floppy** — smoke test of test-injection pipe
4. **🟡 Slave PIC cascade** — gating prereq for §5/§6
5. **🟡 CheckIt full pass** — "your PIC is 90% correct" headline
6. **🔴 80186 PCB via MAME vectors** — only if an 80186 board ROM
   appears in scope

## References

- `MD/process/04-advanced-pc-emulator-testing.md` — full zh-TW
- `MD/process/03-dos-test-injection-workflow.md` — base test pipe
- `src/AprPc.Cli/Hardware/Pic8259.cs` — master-only PIC
- `src/AprCpu.Tests/X86TomHarteTests.cs` — template for §1.1
