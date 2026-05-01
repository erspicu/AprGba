using System.Buffers.Binary;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Trivial little-endian byte-array memory bus. Used by host-runtime
/// tests; not for real GBA memory map (that needs region dispatch +
/// MMIO callbacks, which lands in Phase 5).
///
/// Out-of-range accesses throw — production buses should silently
/// return open-bus values, but a test harness wants the loud failure.
/// </summary>
public sealed class FlatMemoryBus : IMemoryBus
{
    private readonly byte[] _ram;
    public int SizeBytes => _ram.Length;
    public Span<byte> Bytes => _ram;

    public FlatMemoryBus(int sizeBytes) => _ram = new byte[sizeBytes];

    public byte ReadByte(uint addr) => _ram[(int)addr];

    public ushort ReadHalfword(uint addr)
        => BinaryPrimitives.ReadUInt16LittleEndian(_ram.AsSpan((int)addr, 2));

    public uint ReadWord(uint addr)
        => BinaryPrimitives.ReadUInt32LittleEndian(_ram.AsSpan((int)addr, 4));

    public void WriteByte(uint addr, byte value) => _ram[(int)addr] = value;

    public void WriteHalfword(uint addr, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(_ram.AsSpan((int)addr, 2), value);

    public void WriteWord(uint addr, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(_ram.AsSpan((int)addr, 4), value);
}
