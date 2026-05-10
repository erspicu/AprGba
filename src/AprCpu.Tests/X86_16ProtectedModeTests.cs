using AprCpu.Core.Runtime;
using Xunit;
using static AprCpu.Core.Runtime.X86_16ProtectedMode;

namespace AprCpu.Tests;

/// <summary>
/// Sprint 27.6 — descriptor + selector helpers.
/// </summary>
public class X86_16ProtectedModeTests
{
    [Fact]
    public void Descriptor_RoundTrips_Through_Bytes()
    {
        var d = new Descriptor(Limit: 0xFFFF, BaseLow24: 0x123456, AccessRights: 0x9A);
        Span<byte> buf = stackalloc byte[8];
        BuildDescriptor(d, buf);
        var parsed = ParseDescriptor(buf);
        Assert.Equal(d, parsed);
    }

    [Fact]
    public void Descriptor_AccessRights_Decode()
    {
        // 0x9A = 1001_1010 = P=1, DPL=00, S=1, Type=1010
        // Type 1010 with S=1: bit 3 set = executable (code segment)
        //                     bit 1 set = readable
        //                     bit 0 clear = not accessed
        var d = new Descriptor(Limit: 0, BaseLow24: 0, AccessRights: 0x9A);
        Assert.True(d.P);
        Assert.Equal(0, d.Dpl);
        Assert.True(d.S);
        Assert.True(d.Executable);
        Assert.True(d.CodeReadable);
        Assert.False(d.Accessed);
        Assert.False(d.CodeConforming);
    }

    [Fact]
    public void Descriptor_Data_Segment()
    {
        // 0x92 = 1001_0010 = P=1, DPL=00, S=1, Type=0010
        // Type 0010 with S=1: bit 3 clear = data segment
        //                     bit 1 set = writable
        //                     bit 0 clear = not accessed
        var d = new Descriptor(0, 0, 0x92);
        Assert.True(d.P);
        Assert.True(d.S);
        Assert.False(d.Executable);
        Assert.True(d.DataWritable);
        Assert.False(d.DataExpandDown);
    }

    [Fact]
    public void Descriptor_System_TSS()
    {
        // 0x89 = 1000_1001 = P=1, DPL=00, S=0, Type=1001
        // Wait: bit 4 = S, so 1000_1001 has S=0 (system descriptor)
        //      Type 1001 = 9 — but valid 286 system types are 1-7.
        //      Let me use 0x82 = 1000_0010: S=0, Type=0010 (LDT)
        var d = new Descriptor(0, 0, 0x82);
        Assert.True(d.P);
        Assert.False(d.S);
        Assert.Equal(Descriptor.SysLdt, d.Type);
    }

    [Fact]
    public void Selector_Roundtrips()
    {
        // Index 0x100, GDT, RPL=2 → raw = (0x100 << 3) | (0 << 2) | 2 = 0x802
        var sel = Selector.FromParts(index: 0x100, ti: false, rpl: 2);
        Assert.Equal(0x802, sel.Raw);
        Assert.Equal(0x100, sel.Index);
        Assert.False(sel.IsLdt);
        Assert.Equal(2, sel.Rpl);
        Assert.False(sel.IsNull);
    }

    [Fact]
    public void Selector_Null()
    {
        var sel = new Selector(0);
        Assert.True(sel.IsNull);
        Assert.Equal(0, sel.Index);
    }

    [Fact]
    public void Selector_LDT_Bit()
    {
        var sel = Selector.FromParts(index: 5, ti: true, rpl: 3);
        Assert.True(sel.IsLdt);
        Assert.Equal(5, sel.Index);
        Assert.Equal(3, sel.Rpl);
        // raw = (5 << 3) | (1 << 2) | 3 = 0x28 | 0x04 | 0x03 = 0x2F
        Assert.Equal(0x2F, sel.Raw);
    }

    [Fact]
    public void DescriptorAddress_GDT()
    {
        // GDT at 0x10000, LDT at 0x20000. Selector for GDT[3], rpl=0.
        var sel = Selector.FromParts(3, false, 0);
        uint addr = DescriptorAddress(sel, gdtrBase: 0x10000, ldtrBaseFromGdt: 0x20000);
        // GDT[3] = 0x10000 + 3*8 = 0x10018
        Assert.Equal(0x10018u, addr);
    }

    [Fact]
    public void DescriptorAddress_LDT()
    {
        var sel = Selector.FromParts(3, true, 0);
        uint addr = DescriptorAddress(sel, gdtrBase: 0x10000, ldtrBaseFromGdt: 0x20000);
        Assert.Equal(0x20018u, addr);
    }

    [Fact]
    public void ParseDescriptor_Throws_On_Short_Buffer()
    {
        Span<byte> tooShort = stackalloc byte[7];
        // Span can't cross lambda boundary — wrap the call in a delegate without Span.
        Assert.Throws<ArgumentException>(() => ParseShort());
        return;
        void ParseShort() {
            byte[] arr = new byte[7];
            ParseDescriptor(arr);
        }
    }

    [Fact]
    public void Index_Out_Of_Range_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Selector.FromParts(8192, false, 0));
    }

    [Fact]
    public void Rpl_Out_Of_Range_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Selector.FromParts(0, false, 4));
    }

    [Fact]
    public void ReadDescriptor_Roundtrips_Through_Bus()
    {
        // Sprint 27.7 — read from emulated memory at GDT[5].
        var bus = new FlatMemoryBus(0x10000);
        uint gdtBase = 0x1000;
        var sel = Selector.FromParts(5, false, 0);
        uint addr = DescriptorAddress(sel, gdtBase, 0);
        Assert.Equal(gdtBase + 5 * 8, addr);

        // Write a known descriptor at the address.
        var written = new Descriptor(Limit: 0xABCD, BaseLow24: 0x123456, AccessRights: 0x9A);
        Span<byte> buf = stackalloc byte[8];
        BuildDescriptor(written, buf);
        for (int i = 0; i < 8; i++) bus.WriteByte(addr + (uint)i, buf[i]);

        var read = ReadDescriptor(bus, sel, gdtBase, 0);
        Assert.Equal(written, read);
    }

    [Fact]
    public void WriteDescriptor_Persists_To_Bus()
    {
        var bus = new FlatMemoryBus(0x10000);
        uint gdtBase = 0x2000;
        var sel = Selector.FromParts(2, false, 0);

        var d = new Descriptor(Limit: 0x1234, BaseLow24: 0xDEADBE, AccessRights: 0x92);
        WriteDescriptor(bus, sel, gdtBase, 0, d);

        // Independent read from bus, parse, compare.
        Span<byte> buf = stackalloc byte[8];
        for (int i = 0; i < 8; i++) buf[i] = bus.ReadByte(gdtBase + 2 * 8 + (uint)i);
        var roundTrip = ParseDescriptor(buf);
        Assert.Equal(d, roundTrip);
    }

    [Fact]
    public void ReadDescriptor_Uses_LDT_Base_When_Selector_Has_TI_Bit()
    {
        var bus = new FlatMemoryBus(0x10000);
        uint gdtBase = 0x1000;
        uint ldtBase = 0x4000;

        var ldtSel = Selector.FromParts(3, ti: true, rpl: 0);
        var written = new Descriptor(0x7777, 0x000000, 0x92);
        WriteDescriptor(bus, ldtSel, gdtBase, ldtBase, written);

        // Verify it landed at LDT base + 3*8, not GDT base + 3*8.
        Span<byte> ldtBuf = stackalloc byte[8];
        for (int i = 0; i < 8; i++) ldtBuf[i] = bus.ReadByte(ldtBase + 3 * 8 + (uint)i);
        var ldtRead = ParseDescriptor(ldtBuf);
        Assert.Equal(written, ldtRead);

        // GDT slot 3 should be untouched (zeroed).
        Span<byte> gdtBuf = stackalloc byte[8];
        for (int i = 0; i < 8; i++) gdtBuf[i] = bus.ReadByte(gdtBase + 3 * 8 + (uint)i);
        var gdtRead = ParseDescriptor(gdtBuf);
        Assert.Equal(new Descriptor(0, 0, 0), gdtRead);
    }
}
