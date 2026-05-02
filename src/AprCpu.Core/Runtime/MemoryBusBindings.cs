using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AprCpu.Core.IR;
using AprCpu.Core.Runtime.Gba;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Glue between the JIT'd code's <c>memory_read/write_*</c> externs and
/// a C# <see cref="IMemoryBus"/> implementation.
///
/// Trampoline functions are <c>[UnmanagedCallersOnly]</c> static methods.
/// They route to a single static <see cref="_current"/> bus — not
/// thread-safe today, intentional for Phase 3 (one CPU, one main thread).
/// Multi-CPU later will need TLS or a context-pointer argument in the IR
/// signature.
///
/// Phase 7 E.b: GBA-specific fast paths in the trampolines. When the
/// installed bus is <see cref="GbaMemoryBus"/>, common regions (ROM /
/// IWRAM / EWRAM) get a direct array-index path that bypasses the
/// interface call + Locate + region switch. IO + Palette + VRAM + OAM
/// fall through to the regular bus call because writes there have
/// side effects (PPU dirty tracking, IF/IE bit semantics, ...).
/// </summary>
public static unsafe class MemoryBusBindings
{
    private static IMemoryBus? _current;
    // Phase 7 E.b: typed cache for fast-path branch.
    private static GbaMemoryBus? _currentGba;

    /// <summary>
    /// Bind the six memory-bus externs in <paramref name="rt"/>'s module
    /// to <paramref name="bus"/>. Must be called BEFORE
    /// <see cref="HostRuntime.Compile"/> (the trampoline addresses are
    /// baked into the IR as constant initializers).
    ///
    /// The returned scope restores the prior <c>_current</c> binding when
    /// disposed — note this only changes which bus the trampolines route
    /// to; it does NOT re-bake the IR.
    /// </summary>
    public static IDisposable Install(HostRuntime rt, IMemoryBus bus)
    {
        var prior = _current;
        var priorGba = _currentGba;
        _current = bus;
        _currentGba = bus as GbaMemoryBus;

        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Read8,   (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte>)&Read8);
        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Read16,  (IntPtr)(delegate* unmanaged[Cdecl]<uint, ushort>)&Read16);
        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Read32,  (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint>)&Read32);
        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Write8,  (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte, void>)&Write8);
        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Write16, (IntPtr)(delegate* unmanaged[Cdecl]<uint, ushort, void>)&Write16);
        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Write32, (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, void>)&Write32);

        return new RestoreOnDispose(prior, priorGba);
    }

    // ---------------- Phase 7 E.b GBA fast-path helpers ----------------
    //
    // Inline region check + direct array index for GBA's ROM / IWRAM /
    // EWRAM regions. The first three exclusive comparisons in each path
    // are cheap (~2-3 ns) and short-circuit the entire bus call (~15-30 ns
    // even after B.g inlining). For all other regions (BIOS, IO, Palette,
    // VRAM, OAM, unmapped) we fall through to the regular bus call so
    // side-effects (DISPSTAT writes, IF clear-on-write, OAM dirty bit,
    // PPU palette tracking) happen correctly.
    //
    // Reads only — for writes, IO/Palette/VRAM/OAM all have side effects
    // (DMA triggers, palette dirty-track) so we keep the slow path. ROM
    // is read-only. IWRAM/EWRAM writes COULD fast-path but we keep them
    // slow for now to avoid SMC consistency questions.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte? TryGbaFastReadByte(uint addr)
    {
        var bus = _currentGba;
        if (bus is null) return null;
        // Cart ROM 0x08000000-0x0DFFFFFF (mirrors)
        if (addr >= GbaMemoryMap.RomBase)
        {
            int off = (int)((addr - GbaMemoryMap.RomBase) & (GbaMemoryMap.RomMaxSize - 1));
            return off < bus.Rom.Length ? bus.Rom[off] : (byte)0;
        }
        // IWRAM 0x03000000-0x03007FFF (mirrored)
        if ((addr & 0xFF000000) == GbaMemoryMap.IwramBase)
        {
            int off = (int)((addr - GbaMemoryMap.IwramBase) % GbaMemoryMap.IwramSize);
            return bus.Iwram[off];
        }
        // EWRAM 0x02000000-0x0203FFFF (mirrored)
        if ((addr & 0xFF000000) == GbaMemoryMap.EwramBase)
        {
            int off = (int)((addr - GbaMemoryMap.EwramBase) % GbaMemoryMap.EwramSize);
            return bus.Ewram[off];
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort? TryGbaFastReadHalfword(uint addr)
    {
        var bus = _currentGba;
        if (bus is null) return null;
        if (addr >= GbaMemoryMap.RomBase)
        {
            int off = (int)((addr - GbaMemoryMap.RomBase) & (GbaMemoryMap.RomMaxSize - 1));
            return off + 1 < bus.Rom.Length
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bus.Rom.AsSpan(off, 2))
                : (ushort)0;
        }
        if ((addr & 0xFF000000) == GbaMemoryMap.IwramBase)
        {
            int off = (int)((addr - GbaMemoryMap.IwramBase) % GbaMemoryMap.IwramSize);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bus.Iwram.AsSpan(off, 2));
        }
        if ((addr & 0xFF000000) == GbaMemoryMap.EwramBase)
        {
            int off = (int)((addr - GbaMemoryMap.EwramBase) % GbaMemoryMap.EwramSize);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bus.Ewram.AsSpan(off, 2));
        }
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint? TryGbaFastReadWord(uint addr)
    {
        var bus = _currentGba;
        if (bus is null) return null;
        if (addr >= GbaMemoryMap.RomBase)
        {
            int off = (int)((addr - GbaMemoryMap.RomBase) & (GbaMemoryMap.RomMaxSize - 1));
            return off + 3 < bus.Rom.Length
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bus.Rom.AsSpan(off, 4))
                : 0u;
        }
        if ((addr & 0xFF000000) == GbaMemoryMap.IwramBase)
        {
            int off = (int)((addr - GbaMemoryMap.IwramBase) % GbaMemoryMap.IwramSize);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bus.Iwram.AsSpan(off, 4));
        }
        if ((addr & 0xFF000000) == GbaMemoryMap.EwramBase)
        {
            int off = (int)((addr - GbaMemoryMap.EwramBase) % GbaMemoryMap.EwramSize);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bus.Ewram.AsSpan(off, 4));
        }
        return null;
    }

    // ---------------- trampolines ----------------

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte   Read8 (uint addr) =>
        TryGbaFastReadByte(addr) ?? _current!.ReadByte(addr);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Read16(uint addr) =>
        TryGbaFastReadHalfword(addr) ?? _current!.ReadHalfword(addr);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint   Read32(uint addr) =>
        TryGbaFastReadWord(addr) ?? _current!.ReadWord(addr);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void   Write8 (uint addr, byte v)     => _current!.WriteByte(addr, v);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void   Write16(uint addr, ushort v)   => _current!.WriteHalfword(addr, v);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void   Write32(uint addr, uint v)     => _current!.WriteWord(addr, v);

    private sealed class RestoreOnDispose : IDisposable
    {
        private readonly IMemoryBus? _prior;
        private readonly GbaMemoryBus? _priorGba;
        public RestoreOnDispose(IMemoryBus? prior, GbaMemoryBus? priorGba)
        {
            _prior = prior;
            _priorGba = priorGba;
        }
        public void Dispose()
        {
            _current = _prior;
            _currentGba = _priorGba;
        }
    }
}
