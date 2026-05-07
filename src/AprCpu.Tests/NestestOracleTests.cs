using AprNes.Cli;
using AprNes.Cli.Cpu;
using AprNes.Cli.Memory;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// N0b verification: nestest.nes runs through the LegacyCpu (Ricoh2A03Cpu)
/// oracle wired to NesMemoryBus + Mapper000 (NROM), and reaches the
/// expected official-opcode-pass marker PC=0xC66E with status codes
/// $02=0x00 / $03=0x00.
///
/// nestest is the de-facto NES CPU correctness test ROM. Started at
/// PC=0xC000 with SP=0xFD / P=0x24, it executes ~9000 instructions
/// covering every official 6502 opcode. The "automated mode" jump-in
/// at $C000 (rather than the reset vector) writes failure codes to
/// $02 and $03 if any test fails. PC reaches $C66E when all
/// official-opcode tests pass.
///
/// This validates the entire wired oracle path: LegacyCpu instruction
/// execution + bus dispatch + Mapper 0 routing + cycle accounting +
/// stack semantics + flag handling. It is the headline N0b deliverable.
/// </summary>
public class NestestOracleTests
{
    private static string NestestRomPath =>
        Path.Combine(TestPaths.RepoRoot, "test-roms", "nes-test", "nestest.nes");

    [Fact]
    public void Nestest_OfficialOpcodes_ReachesC66E_AndPassesAllTests()
    {
        Assert.True(File.Exists(NestestRomPath),
            $"nestest.nes not found at {NestestRomPath}");

        // Set up oracle: LegacyCpu + NesMemoryBus + Mapper 0 (NROM).
        var rom = NesRomLoader.Load(NestestRomPath);
        Assert.Equal(0, rom.MapperId);

        var mapper = new Mapper000();
        mapper.Reset(rom.PrgRom, rom.ChrRom);

        var bus = new NesMemoryBus();
        bus.Reset(mapper);

        var cpu = new BoundCpu(bus);
        cpu.InitForNestest();

        // Run until PC reaches the pass marker, or cycle budget exhausted.
        // ~9000 instructions × ~5 cycles/instr = ~45k cycles; 5M is huge margin.
        const long maxCycles = 5_000_000L;
        long cycles = 0;
        long instructions = 0;
        bool reached = false;
        while (cycles < maxCycles)
        {
            cycles += cpu.Step();
            instructions++;
            if (cpu.PC == 0xC66E) { reached = true; break; }
        }

        Assert.True(reached,
            $"PC did not reach 0xC66E within {maxCycles:N0} cycles "
            + $"(stopped at PC=0x{cpu.PC:X4} after {instructions:N0} instr, "
            + $"{cycles:N0} cycles consumed). nestest result codes: "
            + $"$02=0x{bus.ReadByte(0x0002):X2} $03=0x{bus.ReadByte(0x0003):X2}.");

        // Verify nestest's pass markers — official-opcode test sets both
        // to 0x00 on success.
        byte resultLo = bus.ReadByte(0x0002);
        byte resultHi = bus.ReadByte(0x0003);
        Assert.True(resultLo == 0x00 && resultHi == 0x00,
            $"nestest reports failure: $02=0x{resultLo:X2} $03=0x{resultHi:X2}");

        // Sanity-check final register state (this is implementation-
        // determined but stable per the LegacyCpu oracle path).
        Assert.Equal(0xC66E, cpu.PC);
    }
}
