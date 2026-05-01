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
/// <para><b>User/System sidecar banks:</b></para>
/// User and System modes have no banked entries in arm7tdmi/cpu.json
/// (architecturally they share storage with the visible slots). But on
/// real hardware their R13/R14 still need to survive a round-trip
/// through Supervisor/IRQ/etc. — otherwise SP is destroyed by every
/// SWI. We allocate per-mode-encoding sidecar slots in C# memory for
/// the modes that the spec leaves un-banked, and use them like any
/// other bank. This keeps the spec architecturally minimal while
/// making real ROM execution work.
/// </summary>
public sealed unsafe class Arm7tdmiBankSwapHandler : IBankSwapHandler
{
    // Registers that User/System mode's sidecar bank should preserve
    // across mode transitions. R13/R14 only — R8-R12 don't bank in any
    // non-FIQ mode, so they don't need User/System preservation either.
    private static readonly int[] UserSystemSidecarRegs = { 13, 14 };

    private readonly HostRuntime _rt;
    private readonly Dictionary<uint, IReadOnlyList<BankEntry>> _bankByModeEnc = new();
    private readonly Dictionary<uint, uint[]> _sidecar = new();    // mode_enc → sidecar storage for User/System
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
                _sidecar[enc] = new uint[UserSystemSidecarRegs.Length];
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

        // Save OUT-going mode's visible R8-R14 → its bank or sidecar.
        if (_bankByModeEnc.TryGetValue(oldMode, out var oldEntries) && oldEntries.Count > 0)
        {
            foreach (var e in oldEntries)
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(state + e.VisibleOffset, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(state + e.BankOffset, 4), v);
            }
        }
        else if (_sidecar.TryGetValue(oldMode, out var oldSide))
        {
            for (int i = 0; i < UserSystemSidecarRegs.Length; i++)
                oldSide[i] = ReadGpr(state, UserSystemSidecarRegs[i]);
        }

        // Load IN-coming mode's bank or sidecar → visible R8-R14.
        if (_bankByModeEnc.TryGetValue(newMode, out var newEntries) && newEntries.Count > 0)
        {
            foreach (var e in newEntries)
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(state + e.BankOffset, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(state + e.VisibleOffset, 4), v);
            }
        }
        else if (_sidecar.TryGetValue(newMode, out var newSide))
        {
            for (int i = 0; i < UserSystemSidecarRegs.Length; i++)
                WriteGpr(state, UserSystemSidecarRegs[i], newSide[i]);
        }
    }

    private uint ReadGpr(byte* state, int regIndex)
        => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(state + (long)_rt.GprOffset(regIndex), 4));

    private void WriteGpr(byte* state, int regIndex, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(state + (long)_rt.GprOffset(regIndex), 4), value);

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
