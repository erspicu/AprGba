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
    public void Loads_Gba_MachineSpec_AndAlignsWith_GbaMemoryMap()
    {
        var spec = MachineSpecLoader.LoadFromFile(MachineSpecPath("gba"));

        Assert.Equal("gba", spec.Name);
        Assert.Equal("ARMv4T", spec.CpuRef);
        Assert.Equal(8, spec.MemoryRegions.Count);

        // Spec values must match the hardcoded GbaMemoryMap constants.
        // If GbaMemoryMap ever changes, this test fails — forces sync.
        var bios = spec.MemoryRegions.Single(r => r.Name == "bios");
        Assert.Equal(AprCpu.Core.Runtime.Gba.GbaMemoryMap.BiosBase, bios.AddrStart);
        Assert.Equal(AprCpu.Core.Runtime.Gba.GbaMemoryMap.BiosBase
                   + AprCpu.Core.Runtime.Gba.GbaMemoryMap.BiosSize, bios.AddrEndExclusive);
        Assert.Equal(MemoryRegionKind.Rom, bios.Kind);

        var ewram = spec.MemoryRegions.Single(r => r.Name == "ewram");
        Assert.Equal(AprCpu.Core.Runtime.Gba.GbaMemoryMap.EwramBase, ewram.AddrStart);
        Assert.Equal(AprCpu.Core.Runtime.Gba.GbaMemoryMap.EwramBase
                   + AprCpu.Core.Runtime.Gba.GbaMemoryMap.EwramSize, ewram.AddrEndExclusive);
        Assert.True(ewram.FastmemEligible);
        Assert.True(ewram.SmcNotify);

        var iwram = spec.MemoryRegions.Single(r => r.Name == "iwram");
        Assert.Equal(AprCpu.Core.Runtime.Gba.GbaMemoryMap.IwramBase, iwram.AddrStart);
        Assert.True(iwram.FastmemEligible);

        var rom = spec.MemoryRegions.Single(r => r.Name == "cart_rom");
        Assert.Equal(AprCpu.Core.Runtime.Gba.GbaMemoryMap.RomBase, rom.AddrStart);
        Assert.Equal(MemoryRegionKind.Rom, rom.Kind);
        Assert.False(rom.Writable);
    }

    [Fact]
    public void Loads_GbDmg_MachineSpec_WithExpectedRegions()
    {
        var spec = MachineSpecLoader.LoadFromFile(MachineSpecPath("gb-dmg"));

        Assert.Equal("gb-dmg", spec.Name);
        Assert.Equal("LR35902", spec.CpuRef);
        Assert.Equal(11, spec.MemoryRegions.Count);

        // ROM bank 0 + N together cover 0x0000-0x8000.
        var bank0 = spec.MemoryRegions.Single(r => r.Name == "cart_rom_bank0");
        Assert.Equal(0x0000u, bank0.AddrStart);
        Assert.Equal(0x4000u, bank0.AddrEndExclusive);
        Assert.Equal(MemoryRegionKind.Rom, bank0.Kind);
        Assert.True(bank0.FastmemEligible);

        // WRAM 0xC000-0xE000 — 8KB, fastmem + smc.
        var wram = spec.MemoryRegions.Single(r => r.Name == "wram");
        Assert.Equal(0xC000u, wram.AddrStart);
        Assert.Equal(0xE000u, wram.AddrEndExclusive);
        Assert.True(wram.FastmemEligible);
        Assert.True(wram.SmcNotify);

        // HRAM 0xFF80-0xFFFF — 127B, fastmem + smc.
        var hram = spec.MemoryRegions.Single(r => r.Name == "hram");
        Assert.Equal(0xFF80u, hram.AddrStart);
        Assert.Equal(0xFFFFu, hram.AddrEndExclusive);
        Assert.True(hram.FastmemEligible);

        // GB has 5 interrupt vectors (vblank/lcd/timer/serial/joypad).
        Assert.Equal(0x0040u, spec.InterruptVectors["vblank"]);
        Assert.Equal(0x0060u, spec.InterruptVectors["joypad"]);
    }

    [Fact]
    public void IsaMetadata_LoadsForAllThreeCpus_WithExpectedValues()
    {
        var arm = SpecLoader.LoadCpuSpec(Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json"));
        Assert.NotNull(arm.Cpu.IsaMetadata);
        Assert.Equal("little", arm.Cpu.IsaMetadata!.Endianness);
        Assert.Equal(4, arm.Cpu.IsaMetadata.CyclesPerSpecUnit);
        Assert.Equal("lazy", arm.Cpu.IsaMetadata.PcUpdatePolicy);

        var gb = SpecLoader.LoadCpuSpec(Path.Combine(TestPaths.SpecRoot, "lr35902", "cpu.json"));
        Assert.NotNull(gb.Cpu.IsaMetadata);
        Assert.Equal(4, gb.Cpu.IsaMetadata!.CyclesPerSpecUnit);

        var nes = SpecLoader.LoadCpuSpec(Path.Combine(TestPaths.SpecRoot, "2a03", "cpu.json"));
        Assert.NotNull(nes.Cpu.IsaMetadata);
        Assert.Equal(1, nes.Cpu.IsaMetadata!.CyclesPerSpecUnit);
        Assert.Equal("lazy", nes.Cpu.IsaMetadata.PcUpdatePolicy);
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
