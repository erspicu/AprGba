using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

public class MachineSpecTests
{
    private static string MachineSpecPath(string name)
        => Path.Combine(TestPaths.SpecRoot, "machines", $"{name}.json");

    [Fact]
    public void Loads_NesNtsc_MachineSpec_WithExpectedRegions()
    {
        var spec = MachineSpecLoader.LoadFromFile(MachineSpecPath("nes-ntsc"));

        Assert.Equal("nes-ntsc",  spec.Name);
        Assert.Equal("Ricoh2A03", spec.CpuRef);
        Assert.Equal(4, spec.MemoryRegions.Count);

        var wram = spec.MemoryRegions.Single(r => r.Name == "wram");
        Assert.Equal(0x0000u, wram.AddrStart);
        Assert.Equal(0x2000u, wram.AddrEndExclusive);
        Assert.Equal(MemoryRegionKind.Ram, wram.Kind);
        Assert.Equal(0x07FFu, wram.MirrorMask);
        Assert.True(wram.FastmemEligible);
        Assert.True(wram.SmcNotify);            // smc_notify default-on for ram
        Assert.False(wram.ForcesEndOfBlock);    // forces_end_of_block default-off for ram

        var cartPrg = spec.MemoryRegions.Single(r => r.Name == "cart_prg");
        Assert.Equal(0x4020u, cartPrg.AddrStart);
        Assert.Equal(0x10000u, cartPrg.AddrEndExclusive);
        Assert.Equal(MemoryRegionKind.Io, cartPrg.Kind);
        Assert.True(cartPrg.ForcesEndOfBlock);  // explicitly set
        Assert.Contains("mapper", cartPrg.SideEffects);
    }

    [Fact]
    public void NesNtsc_HasReset_NMI_IRQ_Vectors()
    {
        var spec = MachineSpecLoader.LoadFromFile(MachineSpecPath("nes-ntsc"));

        Assert.Equal(0xFFFAu, spec.InterruptVectors["nmi"]);
        Assert.Equal(0xFFFCu, spec.InterruptVectors["reset"]);
        Assert.Equal(0xFFFEu, spec.InterruptVectors["irq"]);
    }

    [Fact]
    public void RegionTypeDefaults_AreSensible()
    {
        // io region: forces_end_of_block default-on, smc_notify default-off,
        //            writable default-on
        // ram region: forces_end_of_block default-off, smc_notify default-on,
        //             writable default-on
        // rom region: forces_end_of_block default-off, smc_notify default-off,
        //             writable default-off
        var spec = MachineSpecLoader.LoadFromFile(MachineSpecPath("nes-ntsc"));

        var ppuIo = spec.MemoryRegions.Single(r => r.Name == "ppu_io");
        Assert.True(ppuIo.ForcesEndOfBlock);  // io default
        Assert.False(ppuIo.SmcNotify);
        Assert.True(ppuIo.Writable);
    }
}
