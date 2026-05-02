using System.Buffers.Binary;

namespace AprCpu.Core.Runtime.Gba;

/// <summary>
/// Phase 5: GBA DMA controller — 4 channels (DMA0..DMA3). Each channel
/// has SAD/DAD/CNT_L/CNT_H registers in IO at GbaMemoryMap.DMA{i}_Base.
///
/// Owned by GbaMemoryBus. The bus delegates writes to DMAxCNT_H here so
/// we can detect the 0→1 transition of the enable bit and fire an
/// immediate-mode transfer on the spot.
///
/// Scope (Phase 5 minimum):
/// <list type="bullet">
///   <item>Immediate-mode transfers (start_timing == 00) — fired the
///         moment CNT_H is written with enable=1</item>
///   <item>VBlank / HBlank / Special start timing — registered for the
///         eventual cycle scheduler (TriggerOn{VBlank,HBlank}); currently
///         stubbed since cycle scheduler isn't wired yet</item>
///   <item>16-bit and 32-bit transfers</item>
///   <item>Source / dest address: increment, decrement, fixed (no
///         reload semantics for dest yet)</item>
///   <item>IRQ-on-end (bit 14) — calls bus.RaiseInterrupt(Dma{n})</item>
///   <item>Word count: 0 in CNT_L is treated as 0x4000 for DMA0..2 and
///         0x10000 for DMA3 per GBATEK</item>
/// </list>
///
/// Not yet supported (would matter for commercial games but not for
/// jsmolka/test ROM screenshots):
/// <list type="bullet">
///   <item>Repeat (bit 9) — would re-arm channel after each VBlank/HBlank</item>
///   <item>DMA3 Game Pak DRQ (bit 11)</item>
///   <item>Audio FIFO DMA (DMA1/2 with timing=Special)</item>
///   <item>Cycle-accurate timing (transfer is currently atomic)</item>
/// </list>
/// </summary>
public sealed class GbaDmaController
{
    private readonly GbaMemoryBus _bus;

    public GbaDmaController(GbaMemoryBus bus) { _bus = bus; }

    /// <summary>
    /// Called by the bus when CNT_H of a channel is written. Detects the
    /// enable transition and either runs an immediate transfer or arms
    /// the channel for a later VBlank/HBlank/Special trigger.
    /// </summary>
    public void OnCntHWrite(int channel, ushort previousCntH, ushort newCntH)
    {
        var prevEnabled = (previousCntH & GbaMemoryMap.DMA_ENABLE) != 0;
        var nowEnabled  = (newCntH      & GbaMemoryMap.DMA_ENABLE) != 0;
        if (prevEnabled || !nowEnabled) return;     // only act on 0→1 transition

        var timing = newCntH & GbaMemoryMap.DMA_TIMING_MASK;
        if (timing == GbaMemoryMap.DMA_TIMING_NOW)
        {
            ExecuteTransfer(channel);
        }
        // VBlank/HBlank/Special: latch and wait for the scheduler to call
        // TriggerOnVBlank / TriggerOnHBlank. Just leave CNT_H armed.
    }

    /// <summary>Called by the cycle scheduler when VBlank starts.</summary>
    public void TriggerOnVBlank()
    {
        for (int ch = 0; ch < 4; ch++)
        {
            var cntH = ReadCntH(ch);
            if ((cntH & GbaMemoryMap.DMA_ENABLE) == 0) continue;
            if ((cntH & GbaMemoryMap.DMA_TIMING_MASK) != GbaMemoryMap.DMA_TIMING_VBLANK) continue;
            ExecuteTransfer(ch);
        }
    }

    /// <summary>Called by the cycle scheduler when HBlank starts.</summary>
    public void TriggerOnHBlank()
    {
        for (int ch = 0; ch < 4; ch++)
        {
            var cntH = ReadCntH(ch);
            if ((cntH & GbaMemoryMap.DMA_ENABLE) == 0) continue;
            if ((cntH & GbaMemoryMap.DMA_TIMING_MASK) != GbaMemoryMap.DMA_TIMING_HBLANK) continue;
            ExecuteTransfer(ch);
        }
    }

    /// <summary>
    /// Run one channel's transfer atomically. Reads SAD/DAD/CNT_L/CNT_H,
    /// performs the copy, updates SAD/DAD per the address-control bits,
    /// clears the enable bit (unless repeat is set — but repeat is not
    /// fully modelled yet), and raises the end-of-transfer IRQ if bit 14
    /// is set.
    /// </summary>
    public void ExecuteTransfer(int channel)
    {
        var baseOff = ChannelBase(channel);

        var sad = ReadIoUInt32(baseOff + GbaMemoryMap.DMA_SAD_Off);
        var dad = ReadIoUInt32(baseOff + GbaMemoryMap.DMA_DAD_Off);
        var cntL = ReadIoUInt16(baseOff + GbaMemoryMap.DMA_CNT_L_Off);
        var cntH = ReadIoUInt16(baseOff + GbaMemoryMap.DMA_CNT_H_Off);

        // Word count: 0 → max for the channel.
        int wordCount = cntL;
        if (wordCount == 0) wordCount = (channel == 3) ? 0x10000 : 0x4000;

        bool word32 = (cntH & GbaMemoryMap.DMA_WIDTH_32) != 0;
        int unitBytes = word32 ? 4 : 2;

        int srcDelta = AddrDelta(cntH & GbaMemoryMap.DMA_SRC_MASK,  isSrc: true,  unitBytes: unitBytes);
        int dstDelta = AddrDelta(cntH & GbaMemoryMap.DMA_DEST_MASK, isSrc: false, unitBytes: unitBytes);

        // Mask SAD/DAD to 27/28-bit per GBATEK (DMA0 src 27, DMA1-3 src 28,
        // dest 27 for DMA0/1/2, 28 for DMA3). Simplified: just mask 28 bits
        // for everything; over-broad but safe for ROM/RAM accesses.
        sad &= 0x0FFFFFFF;
        dad &= 0x0FFFFFFF;

        for (int i = 0; i < wordCount; i++)
        {
            if (word32)
            {
                var v = _bus.ReadWord(sad);
                _bus.WriteWord(dad, v);
            }
            else
            {
                var v = _bus.ReadHalfword(sad);
                _bus.WriteHalfword(dad, v);
            }
            sad = (uint)((int)sad + srcDelta);
            dad = (uint)((int)dad + dstDelta);
        }

        // Write back updated SAD/DAD.
        WriteIoUInt32(baseOff + GbaMemoryMap.DMA_SAD_Off, sad);
        WriteIoUInt32(baseOff + GbaMemoryMap.DMA_DAD_Off, dad);

        // Disable the channel (bit 15 clear) unless repeat is set. Note:
        // "repeat" really means "stay armed until next VBlank/HBlank";
        // we just clear it for now since we don't fully model repeat.
        var newCntH = (ushort)(cntH & ~GbaMemoryMap.DMA_ENABLE);
        WriteIoUInt16(baseOff + GbaMemoryMap.DMA_CNT_H_Off, newCntH);

        // End-of-transfer IRQ.
        if ((cntH & GbaMemoryMap.DMA_IRQ_ON_END) != 0)
        {
            var irqKind = channel switch
            {
                0 => GbaInterrupt.Dma0,
                1 => GbaInterrupt.Dma1,
                2 => GbaInterrupt.Dma2,
                _ => GbaInterrupt.Dma3,
            };
            _bus.RaiseInterrupt(irqKind);
        }
    }

    // ---------------- helpers ----------------

    private static int AddrDelta(int modeMask, bool isSrc, int unitBytes)
    {
        // Decode the mode field; values are pre-shifted constants in
        // GbaMemoryMap so we can just compare directly.
        if (isSrc)
        {
            if (modeMask == GbaMemoryMap.DMA_SRC_INC)   return  unitBytes;
            if (modeMask == GbaMemoryMap.DMA_SRC_DEC)   return -unitBytes;
            if (modeMask == GbaMemoryMap.DMA_SRC_FIXED) return  0;
            // 0x180 is "prohibited" per GBATEK — treat as fixed.
            return 0;
        }
        else
        {
            if (modeMask == GbaMemoryMap.DMA_DEST_INC)    return  unitBytes;
            if (modeMask == GbaMemoryMap.DMA_DEST_DEC)    return -unitBytes;
            if (modeMask == GbaMemoryMap.DMA_DEST_FIXED)  return  0;
            // DMA_DEST_RELOAD: increment during transfer; reload to original
            // DAD on next trigger (we don't fully model reload yet — just
            // increment for this transfer).
            return unitBytes;
        }
    }

    private static uint ChannelBase(int channel) => channel switch
    {
        0 => GbaMemoryMap.DMA0_Base,
        1 => GbaMemoryMap.DMA1_Base,
        2 => GbaMemoryMap.DMA2_Base,
        3 => GbaMemoryMap.DMA3_Base,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    private ushort ReadCntH(int channel)
        => ReadIoUInt16(ChannelBase(channel) + GbaMemoryMap.DMA_CNT_H_Off);

    private uint ReadIoUInt32(uint off)
        => BinaryPrimitives.ReadUInt32LittleEndian(_bus.Io.AsSpan((int)off, 4));
    private void WriteIoUInt32(uint off, uint v)
        => BinaryPrimitives.WriteUInt32LittleEndian(_bus.Io.AsSpan((int)off, 4), v);
    private ushort ReadIoUInt16(uint off)
        => BinaryPrimitives.ReadUInt16LittleEndian(_bus.Io.AsSpan((int)off, 2));
    private void WriteIoUInt16(uint off, ushort v)
        => BinaryPrimitives.WriteUInt16LittleEndian(_bus.Io.AsSpan((int)off, 2), v);
}
