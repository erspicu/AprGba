using System.Buffers.Binary;
using AprCpu.Core.IR;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Data-driven banked-register swap for any ARM-style spec that
/// declares <c>processor_modes.banked_registers</c>. On every mode
/// transition, copies the OUT-going mode's visible R8-R14 into its
/// banked storage, then copies the IN-coming mode's banked storage
/// into the visible slots.
///
/// <para><b>Known limitation</b> (Phase 3 scope):</para>
/// User and System modes have no banked entries in arm7tdmi/cpu.json,
/// so transitions FROM them don't save the current visible R8-R14
/// anywhere. After User → SVC → User the User's R13/R14 will be
/// clobbered. Fix when needed: add a "User" entry with R8-R14 to the
/// spec, OR maintain a sidecar slot keyed off the User mode encoding.
/// Phase 4 (real ROM execution) will force this fix.
/// </summary>
public sealed unsafe class Arm7tdmiBankSwapHandler : IBankSwapHandler
{
    private readonly HostRuntime _rt;
    private readonly Dictionary<uint, IReadOnlyList<BankEntry>> _bankByModeEnc = new();
    private bool _offsetsResolved;

    public Arm7tdmiBankSwapHandler(HostRuntime rt)
    {
        _rt = rt;
        var modes = rt.Layout.ProcessorModes
            ?? throw new InvalidOperationException("Bank swap requires processor_modes in spec.");

        // Build mode encoding → entries (regIdx, bankIdxInGroup) map.
        // Byte offsets are resolved lazily on first SwapBank call (after
        // HostRuntime.Compile, when target data is available).
        foreach (var mode in modes.Modes)
        {
            if (mode.Encoding is null) continue;
            uint enc = Convert.ToUInt32(mode.Encoding, 2);

            if (!modes.BankedRegisters.TryGetValue(mode.Id, out var bankedNames) || bankedNames.Count == 0)
            {
                _bankByModeEnc[enc] = Array.Empty<BankEntry>();
                continue;
            }

            var entries = new List<BankEntry>(bankedNames.Count);
            for (int i = 0; i < bankedNames.Count; i++)
            {
                var regName = bankedNames[i];
                if (!regName.StartsWith("R", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Banked register name '{regName}' must be Rn-form.");
                int regIdx = int.Parse(regName.Substring(1));
                entries.Add(new BankEntry(mode.Id, regIdx, i, 0, 0));
            }
            _bankByModeEnc[enc] = entries;
        }
    }

    public void SwapBank(byte* state, uint oldMode, uint newMode)
    {
        if (oldMode == newMode) return;
        ResolveOffsetsIfNeeded();

        if (_bankByModeEnc.TryGetValue(oldMode, out var oldEntries))
            foreach (var e in oldEntries)
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(state + e.VisibleOffset, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(state + e.BankOffset, 4), v);
            }

        if (_bankByModeEnc.TryGetValue(newMode, out var newEntries))
            foreach (var e in newEntries)
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(state + e.BankOffset, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(state + e.VisibleOffset, 4), v);
            }
    }

    private void ResolveOffsetsIfNeeded()
    {
        if (_offsetsResolved) return;
        var resolved = new Dictionary<uint, IReadOnlyList<BankEntry>>(_bankByModeEnc.Count);
        foreach (var (enc, entries) in _bankByModeEnc)
        {
            if (entries.Count == 0) { resolved[enc] = entries; continue; }
            var newList = new List<BankEntry>(entries.Count);
            foreach (var e in entries)
            {
                var visOff  = _rt.GprOffset(e.RegIndex);
                var bankOff = _rt.BankedGprOffset(e.ModeId, e.IndexInGroup);
                newList.Add(new BankEntry(e.ModeId, e.RegIndex, e.IndexInGroup, visOff, bankOff));
            }
            resolved[enc] = newList;
        }
        _bankByModeEnc.Clear();
        foreach (var kv in resolved) _bankByModeEnc[kv.Key] = kv.Value;
        _offsetsResolved = true;
    }

    private readonly record struct BankEntry(string ModeId, int RegIndex, int IndexInGroup, ulong VisibleOffset, ulong BankOffset);
}
