namespace AprCpu.Core.Runtime;

/// <summary>
/// Phase 27b — Intel 80286 protected-mode descriptor + selector helpers.
///
/// Sprint 27.6 ships pure data-structure helpers; emitter wiring + actual
/// segmentation enforcement land in Sprint 27.10+. These helpers are
/// reachable from C# (e.g. Reset paths, debug-dump tools) and from
/// future LLVM IR via call-out shims.
/// </summary>
public static class X86_16ProtectedMode
{
    /// <summary>
    /// 80286 segment descriptor (8 bytes). Layout per Intel 80286 PRM:
    /// <code>
    ///   byte 0..1  limit (16 bits)
    ///   byte 2..4  base low 24 bits
    ///   byte 5     access rights:
    ///                bits 0..3 type
    ///                bit  4    S (system: 0=system descriptor, 1=code/data)
    ///                bits 5..6 DPL (descriptor privilege level)
    ///                bit  7    P (present)
    ///   byte 6..7  reserved (must be 0 on 80286; 80386+ reuses these)
    /// </code>
    /// </summary>
    public readonly record struct Descriptor(
        ushort Limit,
        uint   BaseLow24,
        byte   AccessRights)
    {
        public int  Type => AccessRights & 0x0F;
        public bool S    => (AccessRights & 0x10) != 0;   // system vs code/data
        public int  Dpl  => (AccessRights >> 5) & 0x03;
        public bool P    => (AccessRights & 0x80) != 0;

        // Code/data sub-fields (only meaningful when S=1):
        public bool Executable     => S && (Type & 0x08) != 0;
        public bool DataWritable   => S && !Executable && (Type & 0x02) != 0;
        public bool DataExpandDown => S && !Executable && (Type & 0x04) != 0;
        public bool CodeReadable   => S &&  Executable && (Type & 0x02) != 0;
        public bool CodeConforming => S &&  Executable && (Type & 0x04) != 0;
        public bool Accessed       => S && (Type & 0x01) != 0;

        // System descriptor sub-types (when S=0):
        public const int SysAvailableTss   = 1;
        public const int SysLdt            = 2;
        public const int SysBusyTss        = 3;
        public const int SysCallGate       = 4;
        public const int SysTaskGate       = 5;
        public const int SysInterruptGate  = 6;
        public const int SysTrapGate       = 7;
    }

    /// <summary>
    /// Parse 8 bytes (in array order) into a Descriptor.
    /// </summary>
    public static Descriptor ParseDescriptor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            throw new ArgumentException($"Descriptor needs 8 bytes, got {bytes.Length}.", nameof(bytes));
        ushort limit = (ushort)(bytes[0] | (bytes[1] << 8));
        uint baseLow24 = (uint)(bytes[2] | (bytes[3] << 8) | (bytes[4] << 16));
        byte access = bytes[5];
        // bytes 6-7 reserved on 80286 (intentionally unused).
        return new Descriptor(limit, baseLow24, access);
    }

    /// <summary>
    /// Serialize a Descriptor to 8 bytes (in array order). Bytes 6-7 set
    /// to 0 (80286 reserved); 80386+ would extend with high-base + flags.
    /// </summary>
    public static void BuildDescriptor(Descriptor d, Span<byte> dst)
    {
        if (dst.Length < 8)
            throw new ArgumentException($"Need 8 bytes of buffer, got {dst.Length}.", nameof(dst));
        dst[0] = (byte)(d.Limit & 0xFF);
        dst[1] = (byte)((d.Limit >> 8) & 0xFF);
        dst[2] = (byte)(d.BaseLow24 & 0xFF);
        dst[3] = (byte)((d.BaseLow24 >> 8) & 0xFF);
        dst[4] = (byte)((d.BaseLow24 >> 16) & 0xFF);
        dst[5] = d.AccessRights;
        dst[6] = 0;
        dst[7] = 0;
    }

    /// <summary>
    /// 16-bit selector format. Used to identify a descriptor in GDT or LDT.
    /// <code>
    ///   bits 0..1   RPL (requested privilege level)
    ///   bit  2      TI  (table indicator: 0=GDT, 1=LDT)
    ///   bits 3..15  Index (13-bit, max 8192 descriptors per table)
    /// </code>
    /// Selector 0x0000 (NULL) is invalid for code/data segment loads but
    /// allowed for some special cases (e.g. cleared LDTR means "no LDT").
    /// </summary>
    public readonly record struct Selector(ushort Raw)
    {
        public int  Rpl   => Raw & 0x03;
        public bool IsLdt => (Raw & 0x04) != 0;
        public int  Index => (Raw >> 3) & 0x1FFF;
        public bool IsNull => Raw == 0;

        public static Selector FromParts(int index, bool ti, int rpl)
        {
            if ((uint)index >= 8192) throw new ArgumentOutOfRangeException(nameof(index));
            if ((uint)rpl   >= 4)    throw new ArgumentOutOfRangeException(nameof(rpl));
            ushort raw = (ushort)(((index & 0x1FFF) << 3) | ((ti ? 1 : 0) << 2) | (rpl & 0x3));
            return new Selector(raw);
        }

        public override string ToString()
            => $"sel(idx={Index},ti={(IsLdt ? "LDT" : "GDT")},rpl={Rpl})";
    }

    /// <summary>
    /// Compute the linear (24-bit) address of descriptor at given selector,
    /// based on the active GDTR / LDTR base. Does NOT enforce limit checks
    /// or NULL-selector rules — caller's responsibility (Sprint 27.10).
    /// </summary>
    public static uint DescriptorAddress(Selector sel, uint gdtrBase, uint ldtrBaseFromGdt)
        => (sel.IsLdt ? ldtrBaseFromGdt : gdtrBase) + (uint)(sel.Index * 8);

    /// <summary>
    /// Sprint 27.7 — read 8 bytes from <paramref name="bus"/> at the
    /// descriptor address derived from <paramref name="sel"/> + the
    /// active table base, and parse into a <see cref="Descriptor"/>.
    /// Caller still does limit / privilege / present checks (Sprint 27.10/.11).
    /// Address mask = 24 bits (80286 physical address space).
    /// </summary>
    public static Descriptor ReadDescriptor(
        IMemoryBus bus, Selector sel, uint gdtrBase, uint ldtrBaseFromGdt)
    {
        ArgumentNullException.ThrowIfNull(bus);
        uint addr = DescriptorAddress(sel, gdtrBase, ldtrBaseFromGdt) & 0x00FFFFFFu;
        Span<byte> buf = stackalloc byte[8];
        for (int i = 0; i < 8; i++) buf[i] = bus.ReadByte(addr + (uint)i);
        return ParseDescriptor(buf);
    }

    /// <summary>
    /// Sprint 27.7 — write 8 bytes representing <paramref name="d"/> to
    /// <paramref name="bus"/> at the address derived from <paramref name="sel"/>.
    /// Used for example by the "accessed" bit lazy update + future TSS
    /// busy-bit toggling.
    /// </summary>
    public static void WriteDescriptor(
        IMemoryBus bus, Selector sel, uint gdtrBase, uint ldtrBaseFromGdt, Descriptor d)
    {
        ArgumentNullException.ThrowIfNull(bus);
        uint addr = DescriptorAddress(sel, gdtrBase, ldtrBaseFromGdt) & 0x00FFFFFFu;
        Span<byte> buf = stackalloc byte[8];
        BuildDescriptor(d, buf);
        for (int i = 0; i < 8; i++) bus.WriteByte(addr + (uint)i, buf[i]);
    }

    /// <summary>
    /// Sprint 27.8 — does the CPU's MSW.PE bit (bit 0) report protected
    /// mode? Reads MSW from the state buffer at <paramref name="mswOffset"/>.
    /// A future Sprint 27.10 segmentation rewrite consults this on every
    /// segment-register load to decide between "real mode shift-and-add"
    /// and "protected mode descriptor lookup".
    /// </summary>
    public static bool IsProtectedMode(byte[] state, int mswOffset)
    {
        ArgumentNullException.ThrowIfNull(state);
        if ((uint)mswOffset + 1 >= (uint)state.Length)
            throw new ArgumentOutOfRangeException(nameof(mswOffset),
                $"MSW offset {mswOffset} out of state buffer (size {state.Length}).");
        ushort msw = (ushort)(state[mswOffset] | (state[mswOffset + 1] << 8));
        return (msw & 0x0001) != 0;
    }

    /// <summary>
    /// Sprint 27.8 — packed MSW value. Useful for tests that want to
    /// build / inspect MSW outside the emulator's state buffer.
    /// </summary>
    public readonly record struct Msw(ushort Raw)
    {
        public bool Pe => (Raw & 0x0001) != 0;
        public bool Mp => (Raw & 0x0002) != 0;
        public bool Em => (Raw & 0x0004) != 0;
        public bool Ts => (Raw & 0x0008) != 0;

        public static Msw RealMode { get; } = new(0xFFF0);   // 80286 reset
        public static Msw ProtectedMode { get; } = new(0xFFF1);  // PE bit set, others reset

        public Msw WithPe(bool v) => new((ushort)((Raw & ~0x1) | (v ? 1 : 0)));
        public Msw WithTs(bool v) => new((ushort)((Raw & ~0x8) | (v ? 8 : 0)));
    }
}
