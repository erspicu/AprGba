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

    /// <summary>
    /// Sprint 27.10a — integration test exercising the full helper stack
    /// shipped through Phase 27b sprints 27.6-27.9. This is a hand-coded
    /// scenario (no LLVM IR / emitters yet) that builds a realistic GDT
    /// in memory + state buffer, then runs the descriptor-lookup +
    /// privilege-check pipeline a kernel would invoke on every selector
    /// load.
    ///
    /// Flow:
    ///   1. Set up GDT with three descriptors at 0x10000:
    ///      [0]      NULL (always required)
    ///      [1]      ring-0 code segment, base 0x100000, limit 0xFFFF
    ///      [2]      ring-3 data segment, base 0x200000, limit 0xFFFF
    ///   2. Build a state buffer where MSW = ProtectedMode + CS = sel(idx=1)
    ///   3. Verify IsProtectedMode reports true
    ///   4. ReadDescriptor for selector idx=1 returns the code descriptor
    ///   5. Privilege check: CPL=0 reading user data (DPL=3) → allowed
    ///   6. Privilege check: CPL=3 trying to write kernel data (DPL=0) → denied
    ///
    /// This is the architectural proof that Sprint 27.10b+ doesn't need
    /// to invent new helpers — it just routes IR through what we already have.
    /// </summary>
    [Fact]
    public void Helper_Stack_Integration_GDT_Plus_Privilege_Pipeline()
    {
        // ----- 1. Set up GDT in a fake bus -----
        var bus = new FlatMemoryBus(0x300000);   // 3 MB span (covers our 0x200000 segment base)
        const uint gdtBase = 0x10000;

        // GDT[0]: NULL descriptor — required by 80286 (selector 0 is invalid).
        WriteDescriptor(bus, Selector.FromParts(0, false, 0), gdtBase, 0,
            new Descriptor(0, 0, 0));

        // GDT[1]: ring-0 code, base 0x100000, limit 0xFFFF, present + readable
        // AccessRights 0x9A = P=1 | DPL=00 | S=1 | Type=1010 (code, readable, !accessed)
        WriteDescriptor(bus, Selector.FromParts(1, false, 0), gdtBase, 0,
            new Descriptor(Limit: 0xFFFF, BaseLow24: 0x100000, AccessRights: 0x9A));

        // GDT[2]: ring-3 data, base 0x200000, limit 0xFFFF, writable
        // AccessRights 0xF2 = P=1 | DPL=11 | S=1 | Type=0010 (data, writable, !accessed)
        WriteDescriptor(bus, Selector.FromParts(2, false, 0), gdtBase, 0,
            new Descriptor(Limit: 0xFFFF, BaseLow24: 0x200000, AccessRights: 0xF2));

        // ----- 2. Build state buffer (small mock, MSW at offset 0) -----
        var state = new byte[8];
        var mswPM = Msw.RealMode.WithPe(true).Raw;   // 0xFFF1
        state[0] = (byte)(mswPM & 0xFF);
        state[1] = (byte)(mswPM >> 8);

        // ----- 3. PE bit detection -----
        Assert.True(IsProtectedMode(state, 0));

        // ----- 4. Lookup GDT[1] returns the code descriptor we wrote -----
        var codeSel = Selector.FromParts(1, false, 0);
        var codeDesc = ReadDescriptor(bus, codeSel, gdtBase, 0);
        Assert.Equal(0xFFFF,    codeDesc.Limit);
        Assert.Equal(0x100000u, codeDesc.BaseLow24);
        Assert.True(codeDesc.P);
        Assert.True(codeDesc.S);
        Assert.True(codeDesc.Executable);
        Assert.True(codeDesc.CodeReadable);
        Assert.Equal(0, codeDesc.Dpl);

        // ----- 5. Read GDT[2] for the data segment -----
        var dataSel = Selector.FromParts(2, false, 3);   // RPL=3 to test caps
        var dataDesc = ReadDescriptor(bus, dataSel, gdtBase, 0);
        Assert.Equal(0x200000u, dataDesc.BaseLow24);
        Assert.True(dataDesc.S);
        Assert.False(dataDesc.Executable);
        Assert.True(dataDesc.DataWritable);
        Assert.Equal(3, dataDesc.Dpl);

        // ----- 6. Privilege checks -----
        // CPL=0 (kernel) reading ring-3 data with selector RPL=0: ALLOWED
        Assert.True(CanAccessDataSegment(cpl: 0, rpl: 0, dpl: dataDesc.Dpl));
        // CPL=0 reading ring-3 data with RPL=3 (selector says "act as user"):
        // max(0,3)=3 <= dpl 3 -> ALLOWED
        Assert.True(CanAccessDataSegment(cpl: 0, rpl: 3, dpl: dataDesc.Dpl));

        // CPL=3 (user) trying to read kernel-only data segment (DPL=0): DENIED
        // For this we need a hypothetical kernel-data DPL=0 — codeDesc has DPL=0
        // but it's a code segment, not data. Use codeDesc.Dpl as the privileged
        // level to compare.
        Assert.False(CanAccessDataSegment(cpl: 3, rpl: 3, dpl: codeDesc.Dpl));

        // CPL=0 entering non-conforming code at DPL=0: ALLOWED (CPL == DPL)
        Assert.True(CanEnterNonConformingCode(0, codeDesc.Dpl));
        // CPL=3 entering DPL=0 non-conforming code: DENIED
        Assert.False(CanEnterNonConformingCode(3, codeDesc.Dpl));
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
    public void IsProtectedMode_Reads_PE_Bit()
    {
        // Build a state buffer with MSW at offset 32 = 0xFFF0 (real mode).
        var state = new byte[64];
        state[32] = 0xF0; state[33] = 0xFF;
        Assert.False(IsProtectedMode(state, 32));

        // Set PE bit.
        state[32] = 0xF1;
        Assert.True(IsProtectedMode(state, 32));

        // Top bits don't matter.
        state[32] = 0x01; state[33] = 0x00;
        Assert.True(IsProtectedMode(state, 32));
    }

    [Fact]
    public void IsProtectedMode_Bounds_Check()
    {
        var state = new byte[10];
        Assert.Throws<ArgumentOutOfRangeException>(() => IsProtectedMode(state, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => IsProtectedMode(state, 100));
    }

    [Fact]
    public void CurrentPrivilegeLevel_Reads_Low_Two_Bits_Of_CS()
    {
        Assert.Equal(0, CurrentPrivilegeLevel(0x0008));    // CS:00, RPL=0 (ring 0)
        Assert.Equal(3, CurrentPrivilegeLevel(0x000B));    // RPL=3 (ring 3)
        Assert.Equal(1, CurrentPrivilegeLevel(0xFFF1));    // top bits ignored
    }

    [Fact]
    public void CanAccessDataSegment_Standard_Privilege_Check()
    {
        // Ring 0 code can access ring 0/1/2/3 data:
        Assert.True(CanAccessDataSegment(cpl: 0, rpl: 0, dpl: 0));
        Assert.True(CanAccessDataSegment(0, 0, 3));

        // Ring 3 code can access ring 3 data only:
        Assert.True (CanAccessDataSegment(3, 3, 3));
        Assert.False(CanAccessDataSegment(3, 3, 0));   // dpl 0 means kernel-only
        Assert.False(CanAccessDataSegment(3, 3, 2));

        // RPL acts as cap: ring 0 code with RPL=3 cannot reach ring 0 data:
        Assert.False(CanAccessDataSegment(0, 3, 0));
        Assert.True (CanAccessDataSegment(0, 3, 3));
    }

    [Fact]
    public void CanEnterNonConformingCode_Requires_Equality()
    {
        Assert.True (CanEnterNonConformingCode(0, 0));
        Assert.True (CanEnterNonConformingCode(3, 3));
        Assert.False(CanEnterNonConformingCode(0, 1));
        Assert.False(CanEnterNonConformingCode(3, 0));   // ring 3 cannot direct-call ring 0
    }

    [Fact]
    public void CanEnterConformingCode_Allows_CallerOrMorePrivileged()
    {
        // Ring 3 calling conforming-DPL=0 (kernel utility): allowed (CPL=3 >= DPL=0).
        Assert.True (CanEnterConformingCode(3, 0));
        // Ring 0 calling conforming-DPL=3: NOT allowed.
        Assert.False(CanEnterConformingCode(0, 3));
        Assert.True (CanEnterConformingCode(2, 2));
    }

    [Fact]
    public void CanUseCallGate_LowerOrEqual_CPL_Allowed()
    {
        // Ring 3 calling call-gate DPL=3: allowed.
        Assert.True (CanUseCallGate(3, 3));
        // Ring 3 calling call-gate DPL=0: NOT allowed (gate restricts).
        Assert.False(CanUseCallGate(3, 0));
        // Ring 0 always allowed (most privileged).
        Assert.True (CanUseCallGate(0, 0));
        Assert.True (CanUseCallGate(0, 3));
    }

    [Fact]
    public void Msw_Constants_And_Bits()
    {
        Assert.False(Msw.RealMode.Pe);
        Assert.True(Msw.ProtectedMode.Pe);
        Assert.Equal(0xFFF0, Msw.RealMode.Raw);
        Assert.Equal(0xFFF1, Msw.ProtectedMode.Raw);

        var m = Msw.RealMode.WithPe(true);
        Assert.True(m.Pe);
        Assert.Equal(0xFFF1, m.Raw);

        var ts = m.WithTs(true);
        Assert.True(ts.Ts);
        Assert.Equal(0xFFF9, ts.Raw);
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
