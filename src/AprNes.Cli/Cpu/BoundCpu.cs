// LegacyCpu adapter that binds Ricoh2A03Cpu's protected virtual bus
// hooks to a concrete NesMemoryBus instance. This is the "wired oracle"
// — drop a NesMemoryBus into the constructor, and Mem_r / Mem_w / ZP_r
// / ZP_w / OAM-DMA-stall all just work.
//
// Why a subclass instead of an interface field on Ricoh2A03Cpu: the
// fcbbb23 source uses static methods all over MEM.cs, so the natural
// port made bus access a virtual hook on the CPU class. Subclassing
// keeps the oracle file (Ricoh2A03Cpu) untouched and lets the wiring
// live separately.

using System;
using AprNes.Cli.Memory;

namespace AprNes.Cli.Cpu;

public sealed class BoundCpu : Ricoh2A03Cpu, INesCpuBackend
{
    private readonly NesMemoryBus _bus;

    public BoundCpu(NesMemoryBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    /// <summary>Direct bus access for inspection / poke during tests.</summary>
    public NesMemoryBus Bus => _bus;

    public string BackendName => "legacy";

    // --- Bus hook overrides ---

    protected override byte Mem_r(ushort address) => _bus.ReadByte(address);
    protected override void Mem_w(ushort address, byte value) => _bus.WriteByte(address, value);
    protected override byte ZP_r(byte address) => _bus.ReadZeroPage(address);
    protected override void ZP_w(byte address, byte value) => _bus.WriteZeroPage(address, value);

    // --- Public init / control surface ---

    /// <summary>
    /// nestest-style entry: skip the normal reset-vector read and start
    /// at a caller-specified PC with caller-specified SP. nestest jumps
    /// straight to $C000 with SP=$FD, P=$24 (I=1, U=1) and runs in
    /// "automated mode" writing pass/fail codes to $02 / $03.
    /// </summary>
    public void InitForNestest()
    {
        SetRegisters(a: 0, x: 0, y: 0, sp: 0xFD, pc: 0xC000, flagN: 0, flagV: 0, flagD: 0, flagI: 1, flagZ: 0, flagC: 0);
        // nestest.log starts at CYC:7, matching the typical 6502 reset
        // sequence's 7-cycle prologue. Set Interrupt_cycle so the first
        // StepOne() call's cycle count includes that prologue.
        SetInterruptCycle(7);
    }

    /// <summary>
    /// Step one instruction and propagate the cycle cost to the bus
    /// (PPU/APU catch-up tick). Returns the cycles consumed.
    /// </summary>
    public int Step()
    {
        // Service pending PPU VBlank NMI before fetching the next opcode —
        // matches the source's main loop ordering (poll NMI → step CPU →
        // tick PPU). Without this the CPU spins forever in any vblank-wait
        // loop that begins with `BIT $2002 / BPL ...`.
        if (_bus.ConsumePpuNmi())
        {
            NmiInterrupt();
        }

        StepOne();
        var cycles = LastStepCycles;
        // OAM DMA on $4014 write adds 513 stall cycles (514 if odd-cycle
        // alignment); bus tracks this separately so we can fold it into
        // the CPU cycle count for downstream tick.
        var dmaStall = _bus.ConsumeStallCycles();
        cycles += dmaStall;
        _bus.Tick(cycles);
        return cycles;
    }
}
