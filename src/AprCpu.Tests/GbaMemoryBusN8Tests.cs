// N8 — verify GbaMemoryBus's 256-entry page table aligns with the
// declarative spec at spec/machines/gba.json. The runtime still uses
// hardcoded GbaMemoryMap constants (those constants ARE the spec values
// for now — already cross-checked by the existing
// GbaMemoryMap_AlignsWith_MachineSpec test); N8 ensures the page-table
// build correctly maps each spec region's [base, size, mirror) onto
// the right page indices.

using AprCpu.Core.Runtime.Gba;
using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

public class GbaMemoryBusN8Tests
{
    [Fact]
    public void PageTable_BuildsCleanFor_AllExpectedPages()
    {
        var bus = new GbaMemoryBus();

        // Page 0x00 — BIOS, sub-page bounds-check (not pure mirror).
        var p00 = bus.GetPageEntryForTest(0x00);
        Assert.Equal("Bios", p00.kindName);
        Assert.Equal(GbaMemoryMap.BiosBase, p00.baseAddr);
        Assert.Equal(GbaMemoryMap.BiosSize, p00.size);
        Assert.False(p00.powerOf2Mirror);   // sub-page, no mirror

        // Page 0x01 — unmapped.
        Assert.Equal("Unmapped", bus.GetPageEntryForTest(0x01).kindName);

        // Page 0x02 — EWRAM, full-page mirror.
        var p02 = bus.GetPageEntryForTest(0x02);
        Assert.Equal("Ewram", p02.kindName);
        Assert.Equal(GbaMemoryMap.EwramBase, p02.baseAddr);
        Assert.Equal(GbaMemoryMap.EwramSize, p02.size);
        Assert.True(p02.powerOf2Mirror);

        // Page 0x03 — IWRAM, full-page mirror.
        var p03 = bus.GetPageEntryForTest(0x03);
        Assert.Equal("Iwram", p03.kindName);
        Assert.True(p03.powerOf2Mirror);

        // Page 0x04 — IO, sub-page bounds-check.
        var p04 = bus.GetPageEntryForTest(0x04);
        Assert.Equal("Io", p04.kindName);
        Assert.Equal(GbaMemoryMap.IoSize, p04.size);
        Assert.False(p04.powerOf2Mirror);

        // Page 0x05 — Palette, full-page mirror.
        Assert.Equal("Palette", bus.GetPageEntryForTest(0x05).kindName);
        Assert.True(bus.GetPageEntryForTest(0x05).powerOf2Mirror);

        // Page 0x06 — VRAM, 96 KB non-power-of-2 → modulo path, NOT
        // marked as PowerOf2.
        var p06 = bus.GetPageEntryForTest(0x06);
        Assert.Equal("Vram", p06.kindName);
        Assert.Equal(GbaMemoryMap.VramSize, p06.size);
        Assert.False(p06.powerOf2Mirror);   // VRAM is the only Modulo region

        // Page 0x07 — OAM, full-page mirror.
        Assert.Equal("Oam", bus.GetPageEntryForTest(0x07).kindName);

        // Pages 0x08-0x0D — ROM aliases (6 wait-state regions).
        for (uint p = 0x08; p <= 0x0D; p++)
        {
            var entry = bus.GetPageEntryForTest((int)p);
            Assert.Equal("Rom", entry.kindName);
            Assert.Equal(GbaMemoryMap.RomBase, entry.baseAddr);   // shared base, not page base
            Assert.Equal(GbaMemoryMap.RomMaxSize, entry.size);
            Assert.True(entry.powerOf2Mirror);
        }

        // Pages 0x0E onward — currently unmapped (real GBA has SRAM here).
        for (int p = 0x0E; p < 0x20; p++)
            Assert.Equal("Unmapped", bus.GetPageEntryForTest(p).kindName);
    }

    [Fact]
    public void PageTable_AlignsWith_MachineSpec_GbaJson()
    {
        // Cross-validate: every region declared in spec/machines/gba.json
        // must end up reachable through the page table at the address
        // (region.AddrStart >> 24).
        var spec = MachineSpecLoader.LoadFromFile(
            Path.Combine(TestPaths.SpecRoot, "machines", "gba.json"));
        var bus = new GbaMemoryBus();

        foreach (var region in spec.MemoryRegions)
        {
            int pageIdx = (int)(region.AddrStart >> 24);
            var entry = bus.GetPageEntryForTest(pageIdx);

            // Map spec region.Name → expected page-entry kind name.
            string expectedKind = region.Name switch
            {
                "bios"     => "Bios",
                "ewram"    => "Ewram",
                "iwram"    => "Iwram",
                "io"       => "Io",
                "palette"  => "Palette",
                "vram"     => "Vram",
                "oam"      => "Oam",
                "cart_rom" => "Rom",
                _          => null!,
            };
            Assert.NotNull(expectedKind);
            Assert.Equal(expectedKind, entry.kindName);
        }
    }

    [Fact]
    public void Locate_RomMirrors_ProduceIdenticalReads_AcrossWaitStateAliases()
    {
        // GBA's ROM space is 32 MB linear addressed via 6 wait-state pages
        // (0x08-0x0D, two pairs of three timing zones). Pages 0x08/0x0A/0x0C
        // are the "low" half of each pair (offset 0..16MB-1); pages 0x09/0x0B/
        // 0x0D are the "high" half (offset 16MB..32MB-1). For a small test
        // ROM, only the low-half pages alias offset 0.
        var bus = new GbaMemoryBus();
        var rom = new byte[16];
        for (int i = 0; i < rom.Length; i++) rom[i] = (byte)(0x40 + i);
        bus.LoadRom(rom);

        // Read offset 0 through each low-half wait-state alias. Cast to
        // byte to sidestep xUnit's object.Equals strict-type check.
        Assert.Equal((byte)0x40, bus.ReadByte(0x08000000));
        Assert.Equal((byte)0x40, bus.ReadByte(0x0A000000));
        Assert.Equal((byte)0x40, bus.ReadByte(0x0C000000));

        // High-half pages map to offset 0x01000000 (16 MB into ROM space).
        // For our 16-byte test ROM that's past the end → returns 0 via
        // `off < Rom.Length` check. Real games (MB-class ROMs) get the
        // expected mid-ROM bytes here.
        Assert.Equal((byte)0, bus.ReadByte(0x09000000));
        Assert.Equal((byte)0, bus.ReadByte(0x0B000000));
        Assert.Equal((byte)0, bus.ReadByte(0x0D000000));
    }

    [Fact]
    public void Locate_BiosOutsideValidRange_FallsToUnmapped()
    {
        // Page 0x00, addr beyond BiosSize (16 KB) — Locate must return
        // (Unmapped, 0). Through the public read API that surfaces as
        // open-bus / 0.
        var bus = new GbaMemoryBus();
        bus.DisableBiosOpenBus = true;     // sidestep BIOS open-bus sticky
        // Inside BIOS — returns the array byte (0 by default).
        Assert.Equal((byte)0, bus.ReadByte(0x00003FFF));
        // Just past BIOS — bounds-check kicks in, returns 0 via Unmapped.
        Assert.Equal((byte)0, bus.ReadByte(0x00004000));
        Assert.Equal((byte)0, bus.ReadByte(0x00FFFFFF));
    }

    [Fact]
    public void Locate_IoOutsideValidRange_FallsToUnmapped()
    {
        // Page 0x04, addr beyond IoSize (1 KB) — Locate returns Unmapped.
        var bus = new GbaMemoryBus();
        Assert.Equal((byte)0, bus.ReadByte(0x04001000));   // beyond 1 KB IO
        Assert.Equal((byte)0, bus.ReadByte(0x04FFFFFF));
    }
}
