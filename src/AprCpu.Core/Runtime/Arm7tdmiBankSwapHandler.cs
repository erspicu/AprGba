using System.Buffers.Binary;
using AprCpu.Core.IR;

namespace AprCpu.Core.Runtime;

/// <summary>
/// ARM7TDMI banked-register swap, modelling the architectural rules:
///
/// <list type="bullet">
/// <item>R0-R7: never banked, no work needed</item>
/// <item>R8-R12: FIQ has its own bank; all non-FIQ modes share a single
///   set of R8-R12 (the "user/system view")</item>
/// <item>R13-R14: each privileged mode (FIQ, IRQ, Supervisor, Abort,
///   Undefined) has its own bank; User and System share one</item>
/// </list>
///
/// State storage:
/// <list type="bullet">
/// <item><c>_nonFiqR8R12</c> — single shared storage for R8-R12 used by
///   all non-FIQ modes. Saved on FIQ entry, loaded on FIQ exit.</item>
/// <item><c>_userSystemR13R14</c> — shared by User and System modes.</item>
/// <item>FIQ R8-R14 + IRQ/SVC/Abort/Undef R13-R14 use the spec's
///   <c>processor_modes.banked_registers</c> entries (data-driven).</item>
/// </list>
///
/// Algorithm on mode transition <c>old → new</c>:
/// <list type="number">
/// <item><b>Save R13-R14:</b> from visible to old mode's R13-R14 storage
///   (spec bank for IRQ/SVC/Abort/Undef/FIQ; user-system sidecar for
///   User/System).</item>
/// <item><b>Save R8-R12:</b> only when crossing the FIQ boundary
///   (old==FIQ vs new!=FIQ, and vice-versa). For FIQ→non-FIQ, save
///   visible to FIQ bank R8-R12. For non-FIQ→FIQ, save visible to
///   <c>_nonFiqR8R12</c>.</item>
/// <item><b>Load R8-R12:</b> mirror of save — only on FIQ-boundary crosses.</item>
/// <item><b>Load R13-R14:</b> from new mode's storage to visible.</item>
/// </list>
/// </summary>
public sealed unsafe class Arm7tdmiBankSwapHandler : IBankSwapHandler
{
    private const uint FiqEnc = 0b10001;

    private readonly HostRuntime _rt;

    // Per-mode R13-R14 storage. Modes whose spec has [R13,R14] (or more)
    // banked entries write directly to those bank slots. Modes without
    // spec entries (User, System) share the _userSystemR13R14 sidecar.
    private readonly Dictionary<uint, ModeR13R14Slot> _r13r14ByModeEnc = new();
    private readonly uint[] _userSystemR13R14 = new uint[2];

    // R8-R12: shared by all non-FIQ modes; FIQ has its own bank in spec.
    private readonly uint[] _nonFiqR8R12 = new uint[5];
    private readonly int[]  _fiqR8R12BankIndex = new int[5];   // bank-list indices for R8..R12 within FIQ entries

    private bool _offsetsResolved;
    private ulong[] _gprOffsets = Array.Empty<ulong>();

    public Arm7tdmiBankSwapHandler(HostRuntime rt)
    {
        _rt = rt;
        var modes = rt.Layout.ProcessorModes
            ?? throw new InvalidOperationException("Bank swap requires processor_modes in spec.");

        // Build per-mode R13-R14 mapping. Spec lists banked register names
        // in order; we look up which positions are R13 and R14.
        foreach (var mode in modes.Modes)
        {
            if (mode.Encoding is null) continue;
            uint enc = Convert.ToUInt32(mode.Encoding, 2);

            int? r13Idx = null, r14Idx = null;
            int? r8Idx = null, r9Idx = null, r10Idx = null, r11Idx = null, r12Idx = null;

            if (modes.BankedRegisters.TryGetValue(mode.Id, out var bankedNames))
            {
                for (int i = 0; i < bankedNames.Count; i++)
                {
                    var nm = bankedNames[i];
                    if (!nm.StartsWith("R", StringComparison.OrdinalIgnoreCase)) continue;
                    var rn = int.Parse(nm.Substring(1));
                    switch (rn)
                    {
                        case 8:  r8Idx = i; break;
                        case 9:  r9Idx = i; break;
                        case 10: r10Idx = i; break;
                        case 11: r11Idx = i; break;
                        case 12: r12Idx = i; break;
                        case 13: r13Idx = i; break;
                        case 14: r14Idx = i; break;
                    }
                }
            }

            _r13r14ByModeEnc[enc] = new ModeR13R14Slot(mode.Id, r13Idx, r14Idx);

            // Cache FIQ R8-R12 bank indices.
            if (enc == FiqEnc)
            {
                _fiqR8R12BankIndex[0] = r8Idx  ?? -1;
                _fiqR8R12BankIndex[1] = r9Idx  ?? -1;
                _fiqR8R12BankIndex[2] = r10Idx ?? -1;
                _fiqR8R12BankIndex[3] = r11Idx ?? -1;
                _fiqR8R12BankIndex[4] = r12Idx ?? -1;
            }
        }
    }

    public void SwapBank(byte* state, uint oldMode, uint newMode)
    {
        if (oldMode == newMode) return;
        ResolveOffsetsIfNeeded();

        bool oldFiq = oldMode == FiqEnc;
        bool newFiq = newMode == FiqEnc;

        // ---- Save R13-R14 from visible to OLD mode's storage ----
        SaveR13R14(state, oldMode);

        // ---- R8-R12 only move when crossing the FIQ boundary ----
        if (oldFiq && !newFiq)
        {
            // Save current visible R8-R12 to FIQ bank (spec entries).
            for (int i = 0; i < 5; i++)
            {
                int bankIdx = _fiqR8R12BankIndex[i];
                if (bankIdx < 0) continue;
                var v = ReadGpr(state, 8 + i);
                WriteBanked(state, "FIQ", bankIdx, v);
            }
            // Load shared non-FIQ R8-R12 to visible.
            for (int i = 0; i < 5; i++)
                WriteGpr(state, 8 + i, _nonFiqR8R12[i]);
        }
        else if (!oldFiq && newFiq)
        {
            // Save current visible R8-R12 to shared non-FIQ storage.
            for (int i = 0; i < 5; i++)
                _nonFiqR8R12[i] = ReadGpr(state, 8 + i);
            // Load FIQ bank R8-R12 to visible.
            for (int i = 0; i < 5; i++)
            {
                int bankIdx = _fiqR8R12BankIndex[i];
                if (bankIdx < 0) continue;
                WriteGpr(state, 8 + i, ReadBanked(state, "FIQ", bankIdx));
            }
        }
        // else: both non-FIQ or both FIQ → R8-R12 untouched.

        // ---- Load R13-R14 from NEW mode's storage to visible ----
        LoadR13R14(state, newMode);
    }

    private void SaveR13R14(byte* state, uint modeEnc)
    {
        if (!_r13r14ByModeEnc.TryGetValue(modeEnc, out var slot)) return;
        var v13 = ReadGpr(state, 13);
        var v14 = ReadGpr(state, 14);
        if (slot.HasSpecBank)
        {
            WriteBanked(state, slot.ModeId, slot.R13BankIdx!.Value, v13);
            WriteBanked(state, slot.ModeId, slot.R14BankIdx!.Value, v14);
        }
        else
        {
            // User & System share one sidecar.
            _userSystemR13R14[0] = v13;
            _userSystemR13R14[1] = v14;
        }
    }

    private void LoadR13R14(byte* state, uint modeEnc)
    {
        if (!_r13r14ByModeEnc.TryGetValue(modeEnc, out var slot)) return;
        if (slot.HasSpecBank)
        {
            WriteGpr(state, 13, ReadBanked(state, slot.ModeId, slot.R13BankIdx!.Value));
            WriteGpr(state, 14, ReadBanked(state, slot.ModeId, slot.R14BankIdx!.Value));
        }
        else
        {
            WriteGpr(state, 13, _userSystemR13R14[0]);
            WriteGpr(state, 14, _userSystemR13R14[1]);
        }
    }

    /// <summary>
    /// Read register from the User-mode view, regardless of current mode.
    /// Used by S-bit block transfer (LDM/STM with the <c>^</c> suffix).
    /// </summary>
    public uint ReadUserModeReg(byte* state, int regIndex)
    {
        ResolveOffsetsIfNeeded();
        if (regIndex < 8) return ReadGpr(state, regIndex);             // R0-R7 always visible
        if (regIndex >= 8 && regIndex <= 12)
        {
            // In FIQ mode, visible R8-R12 are FIQ's; user view is in sidecar.
            // In non-FIQ, visible == user view.
            var cpsr = ReadCpsr(state);
            return (cpsr & 0x1F) == FiqEnc ? _nonFiqR8R12[regIndex - 8] : ReadGpr(state, regIndex);
        }
        if (regIndex == 13 || regIndex == 14)
        {
            // In User/System, visible is the user/system R13-R14.
            // In other modes, sidecar holds it.
            var modeEnc = ReadCpsr(state) & 0x1F;
            if (_r13r14ByModeEnc.TryGetValue(modeEnc, out var slot) && !slot.HasSpecBank)
                return ReadGpr(state, regIndex);
            return _userSystemR13R14[regIndex - 13];
        }
        return ReadGpr(state, regIndex);    // R15
    }

    /// <summary>Write register through the User-mode view.</summary>
    public void WriteUserModeReg(byte* state, int regIndex, uint value)
    {
        ResolveOffsetsIfNeeded();
        if (regIndex < 8) { WriteGpr(state, regIndex, value); return; }
        if (regIndex >= 8 && regIndex <= 12)
        {
            var cpsr = ReadCpsr(state);
            if ((cpsr & 0x1F) == FiqEnc) _nonFiqR8R12[regIndex - 8] = value;
            else                          WriteGpr(state, regIndex, value);
            return;
        }
        if (regIndex == 13 || regIndex == 14)
        {
            var modeEnc = ReadCpsr(state) & 0x1F;
            if (_r13r14ByModeEnc.TryGetValue(modeEnc, out var slot) && !slot.HasSpecBank)
            {
                WriteGpr(state, regIndex, value);
                return;
            }
            _userSystemR13R14[regIndex - 13] = value;
            return;
        }
        WriteGpr(state, regIndex, value);
    }

    // ---------------- helpers ----------------

    private void ResolveOffsetsIfNeeded()
    {
        if (_offsetsResolved) return;
        var gprCount = _rt.Layout.GprCount;
        _gprOffsets = new ulong[gprCount];
        for (int i = 0; i < gprCount; i++)
            _gprOffsets[i] = _rt.GprOffset(i);
        _offsetsResolved = true;
    }

    private uint ReadGpr(byte* state, int regIndex)
        => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(state + (long)_gprOffsets[regIndex], 4));

    private void WriteGpr(byte* state, int regIndex, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(state + (long)_gprOffsets[regIndex], 4), value);

    private uint ReadCpsr(byte* state)
        => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(state + (long)_rt.StatusOffset("CPSR"), 4));

    private uint ReadBanked(byte* state, string modeId, int idxInGroup)
    {
        var off = _rt.BankedGprOffset(modeId, idxInGroup);
        return BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(state + (long)off, 4));
    }

    private void WriteBanked(byte* state, string modeId, int idxInGroup, uint value)
    {
        var off = _rt.BankedGprOffset(modeId, idxInGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(state + (long)off, 4), value);
    }

    private readonly record struct ModeR13R14Slot(string ModeId, int? R13BankIdx, int? R14BankIdx)
    {
        public bool HasSpecBank => R13BankIdx.HasValue && R14BankIdx.HasValue;
    }
}
