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
        Assert.True(wram.SmcNotify);            // smc_notify default-on for ram
        Assert.False(wram.ForcesEndOfBlock);    // v1-deprecated; default-off for ram

        // N4 v2 schema fields:
        Assert.Equal("wram", wram.Handler);     // explicit handler routing key
        Assert.NotNull(wram.AllowedWidths);
        Assert.Contains(8, wram.AllowedWidths!);

        var cartPrg = spec.MemoryRegions.Single(r => r.Name == "cart_prg");
        Assert.Equal(0x4020u, cartPrg.AddrStart);
        Assert.Equal(0x10000u, cartPrg.AddrEndExclusive);
        Assert.Equal(MemoryRegionKind.Io, cartPrg.Kind);
        Assert.Equal("mapper", cartPrg.Handler);   // v2 — explicit handler

        // N4 spec_version + unmapped_behavior load from machine root
        Assert.Equal("2.0", spec.SpecVersion);
        Assert.Equal(UnmappedBehavior.Zero, spec.UnmappedBehavior);
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

    /// <summary>
    /// N3.3 — proof of the spec format break: cc=01 ALU group declares a
    /// <c>cycles.table</c> mapping the bbb-selector bit pattern to a precise
    /// cycle count. For each opcode in the cc=01 group, walking the decoder
    /// + resolving via <see cref="CycleTable.Resolve"/> must agree with the
    /// hardcoded LegacyCpu oracle (<see cref="AprNes.Cli.Cpu.NesJsonCpu"/>'s
    /// <c>s_cycleTable</c>).
    ///
    /// Other groups (cc=00, cc=10, branches, stack/jump, unofficial) still
    /// rely on the hardcoded fallback in NesJsonCpu — converting them is
    /// follow-up work.
    /// </summary>
    /// <summary>
    /// Helper for the cycle-table tests: derive a single opcode's cycle
    /// count from the spec — first checking <c>cycles.table</c> (multi-mode
    /// instructions), then falling back to the <c>cycles.form</c> single
    /// value (unique-opcode instructions). Returns null if neither is
    /// declared.
    /// </summary>
    private static int? DeriveSpecCycles(
        AprCpu.Core.Decoder.DecoderTable decoder,
        uint opcode,
        int cyclesPerSpecUnit = 1)
    {
        var d = decoder.Decode(opcode);
        if (d?.Instruction.Cycles is not { } cyc) return null;

        if (cyc.Table is { } table &&
            table.Resolve(d.Format, opcode) is int tableValue)
            return tableValue;

        if (!string.IsNullOrEmpty(cyc.Form))
        {
            int n = 0;
            foreach (var ch in cyc.Form)
            {
                if (ch >= '0' && ch <= '9') { n = n * 10 + (ch - '0'); continue; }
                if (n > 0) break;
            }
            if (n > 0) return n * cyclesPerSpecUnit;
        }
        return null;
    }

    /// <summary>
    /// Hardcoded LegacyCpu oracle — kept as a private constant so each
    /// test that needs it doesn't re-declare. Mirrors NesJsonCpu.s_cycleTable.
    /// </summary>
    private static readonly byte[] s_oracleCycles =
    {
        7,6,2,8,3,3,5,5,3,2,2,2,4,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        6,6,2,8,3,3,5,5,4,2,2,2,4,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        6,6,2,8,3,3,5,5,3,2,2,2,3,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        6,6,2,8,3,3,5,5,4,2,2,2,5,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        2,6,2,6,3,3,3,3,2,2,2,2,4,4,4,4,
        2,6,2,6,4,4,4,4,2,5,2,5,5,5,5,5,
        2,6,2,6,3,3,3,3,2,2,2,2,4,4,4,4,
        2,5,2,5,4,4,4,4,2,4,2,4,4,4,4,4,
        2,6,2,8,3,3,5,5,2,2,2,2,4,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
        2,6,2,8,3,3,5,5,2,2,2,2,4,4,6,6,
        2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7
    };

    /// <summary>
    /// N3.3 (full coverage) — for every opcode that the spec can derive a
    /// cycle count for (via cycle_table OR cycles.form), the derived value
    /// must agree with the LegacyCpu oracle. Opcodes the spec doesn't yet
    /// cover are reported as gaps but don't fail the test (incremental
    /// conversion path).
    ///
    /// As more groups gain cycle_table / correct cycles.form, the
    /// "covered" count grows; full conversion is when covered == 256.
    /// </summary>
    [Fact]
    public void Mos6502CycleTable_FullSpec_MatchesOracleWhereCovered()
    {
        var compiled = AprCpu.Core.Compilation.SpecCompiler.Compile(
            Path.Combine(TestPaths.SpecRoot, "2a03", "cpu.json"));
        Assert.True(compiled.DecoderTables.TryGetValue("Main", out var dec));

        int covered = 0;
        var mismatches = new List<string>();
        for (int op = 0; op < 256; op++)
        {
            var derived = DeriveSpecCycles(dec!, (uint)op);
            if (derived is null) continue;
            covered++;
            if (derived.Value != s_oracleCycles[op])
                mismatches.Add(
                    $"opcode 0x{op:X2}: spec={derived.Value} oracle={s_oracleCycles[op]}");
        }

        // Every covered opcode must agree with the oracle. Mismatches mean
        // the spec is wrong, not just incomplete.
        Assert.True(mismatches.Count == 0,
            $"spec/oracle cycle mismatches:\n  {string.Join("\n  ", mismatches)}");

        // N3.3 (full conversion goal): all 256 opcodes spec-derivable.
        Assert.Equal(256, covered);
    }

    [Fact]
    public void Mos6502CycleTable_DerivedFromSpec_MatchesOracleForCc01()
    {
        // Hardcoded LegacyCpu oracle — exact mirror of NesJsonCpu.s_cycleTable.
        byte[] oracle =
        {
            7,6,2,8,3,3,5,5,3,2,2,2,4,4,6,6,
            2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
            6,6,2,8,3,3,5,5,4,2,2,2,4,4,6,6,
            2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
            6,6,2,8,3,3,5,5,3,2,2,2,3,4,6,6,
            2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
            6,6,2,8,3,3,5,5,4,2,2,2,5,4,6,6,
            2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
            2,6,2,6,3,3,3,3,2,2,2,2,4,4,4,4,
            2,6,2,6,4,4,4,4,2,5,2,5,5,5,5,5,
            2,6,2,6,3,3,3,3,2,2,2,2,4,4,4,4,
            2,5,2,5,4,4,4,4,2,4,2,4,4,4,4,4,
            2,6,2,8,3,3,5,5,2,2,2,2,4,4,6,6,
            2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7,
            2,6,2,8,3,3,5,5,2,2,2,2,4,4,6,6,
            2,5,2,8,4,4,6,6,2,4,2,7,4,4,7,7
        };

        var compiled = AprCpu.Core.Compilation.SpecCompiler.Compile(
            Path.Combine(TestPaths.SpecRoot, "2a03", "cpu.json"));
        Assert.True(compiled.DecoderTables.TryGetValue("Main", out var dec));

        int verified = 0;
        for (int op = 0; op < 256; op++)
        {
            // cc=01 ALU class: low two bits == 01 (excluding 0x89 = STA #imm
            // which the unofficial.json overrides with a NOP entry).
            if ((op & 0x03) != 0x01) continue;
            var d = dec!.Decode((uint)op);
            Assert.NotNull(d);
            // Skip if the decoded format isn't the cc=01 ALU group (e.g.
            // unofficial.json with mask=0xFF wins for 0x89).
            if (d!.Format.Name != "AluCc01") continue;
            var resolved = d.Instruction.Cycles?.Table?.Resolve(d.Format, (uint)op);
            Assert.True(resolved.HasValue,
                $"opcode 0x{op:X2}: cc=01 spec must declare cycles.table");
            Assert.Equal(oracle[op], resolved!.Value);
            verified++;
        }
        // Sanity: at least 56 opcodes covered (8 mnemonics × 8 modes minus
        // STA #imm which is shadowed by unofficial.json = 63; allow some
        // slack if the spec changes).
        Assert.True(verified >= 56, $"verified only {verified} cc=01 opcodes, expected ≥56");
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
