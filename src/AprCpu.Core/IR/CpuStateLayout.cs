using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// LLVM struct layout describing the per-CPU register file passed to every
/// emitted instruction function. Matches the C# unsafe struct that the host
/// pins for the JIT.
///
/// Field layout (i32 unless noted):
/// <code>
///   0..15  R[16]                  general-purpose registers (current bank)
///   16     CPSR
///   17     SPSR_fiq
///   18     SPSR_irq
///   19     SPSR_svc
///   20     SPSR_abt
///   21     SPSR_und
///   22..28 R_fiq[7]               banked R8..R14 (FIQ)
///   29..30 R_irq[2]               banked R13..R14 (IRQ)
///   31..32 R_svc[2]
///   33..34 R_abt[2]
///   35..36 R_und[2]
///   37     CycleCounter (i64, but represented as 2x i32 cells for alignment simplicity → encoded as i64)
///   38     PendingExceptions      bitmask
/// </code>
///
/// CPSR bit layout (mirrors ARMv4T):
///   N=31, Z=30, C=29, V=28, Q=27, I=7, F=6, T=5, M[4:0]=mode
/// </summary>
public sealed unsafe class CpuStateLayout
{
    public LLVMTypeRef StructType { get; }
    public LLVMTypeRef PointerType { get; }
    public LLVMContextRef Context { get; }

    // Field index constants
    public const int FieldR_Base       = 0;   // R[0..15]
    public const int FieldCpsr         = 16;
    public const int FieldSpsr_Fiq     = 17;
    public const int FieldSpsr_Irq     = 18;
    public const int FieldSpsr_Svc     = 19;
    public const int FieldSpsr_Abt     = 20;
    public const int FieldSpsr_Und     = 21;
    public const int FieldR_FiqBase    = 22;  // 7 entries: R8..R14
    public const int FieldR_IrqBase    = 29;  // 2 entries: R13..R14
    public const int FieldR_SvcBase    = 31;
    public const int FieldR_AbtBase    = 33;
    public const int FieldR_UndBase    = 35;
    public const int FieldCycleCounter = 37;  // i64
    public const int FieldPendingExc   = 38;

    // CPSR bit positions
    public const int CpsrBit_N = 31;
    public const int CpsrBit_Z = 30;
    public const int CpsrBit_C = 29;
    public const int CpsrBit_V = 28;
    public const int CpsrBit_Q = 27;
    public const int CpsrBit_I = 7;
    public const int CpsrBit_F = 6;
    public const int CpsrBit_T = 5;

    public CpuStateLayout(LLVMContextRef context)
    {
        Context = context;

        var i32 = LLVMTypeRef.Int32;
        var i64 = LLVMTypeRef.Int64;

        var elements = new List<LLVMTypeRef>(40);
        // R[0..15]
        for (int i = 0; i < 16; i++) elements.Add(i32);
        // CPSR
        elements.Add(i32);
        // SPSR_*
        elements.Add(i32); elements.Add(i32); elements.Add(i32); elements.Add(i32); elements.Add(i32);
        // R_fiq[7]
        for (int i = 0; i < 7; i++) elements.Add(i32);
        // R_irq[2], R_svc[2], R_abt[2], R_und[2]
        for (int i = 0; i < 8; i++) elements.Add(i32);
        // CycleCounter (i64)
        elements.Add(i64);
        // PendingExceptions
        elements.Add(i32);

        StructType = LLVMTypeRef.CreateStruct(elements.ToArray(), Packed: false);
        PointerType = LLVMTypeRef.CreatePointer(StructType, 0);
    }

    /// <summary>GEP into a fixed register slot (compile-time known index 0..15).</summary>
    public LLVMValueRef GepGpr(LLVMBuilderRef builder, LLVMValueRef statePtr, int regIndex)
    {
        if (regIndex is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(regIndex));
        return BuildGep(builder, statePtr, FieldR_Base + regIndex, $"r{regIndex}_ptr");
    }

    /// <summary>GEP into the CPSR slot.</summary>
    public LLVMValueRef GepCpsr(LLVMBuilderRef builder, LLVMValueRef statePtr)
        => BuildGep(builder, statePtr, FieldCpsr, "cpsr_ptr");

    /// <summary>GEP into the cycle counter slot (i64).</summary>
    public LLVMValueRef GepCycleCounter(LLVMBuilderRef builder, LLVMValueRef statePtr)
        => BuildGep(builder, statePtr, FieldCycleCounter, "cycle_ptr");

    /// <summary>
    /// GEP into a register slot by a runtime-computed index.
    /// Lowers to: GEP statePtr, 0, regIdx (since R[0..15] are the first 16
    /// fields of the struct, this works for indices 0..15; values outside
    /// that range will read past R[15] which is the caller's responsibility
    /// to guard against — practically the index field is masked to 4 bits
    /// before reaching here).
    /// </summary>
    public LLVMValueRef GepGprDynamic(LLVMBuilderRef builder, LLVMValueRef statePtr, LLVMValueRef regIdx)
    {
        // GEP indices: { i32 0 (deref struct ptr), i32 0 (R[0] field), i32 regIdx (offset within R[]) }
        // BUT our struct lays R[0..15] as 16 separate i32 fields, not as an array,
        // so dynamic indexing requires an inbounds GEP through the implicit
        // homogeneous prefix. Easiest path: bitcast statePtr to (i32*)
        // and do a 1-D GEP by regIdx.
        var i32Ptr = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int32, 0);
        var asI32Ptr = builder.BuildBitCast(statePtr, i32Ptr, "state_as_i32p");
        return builder.BuildGEP2(LLVMTypeRef.Int32, asI32Ptr, new[] { regIdx }, "rdyn_ptr");
    }

    private LLVMValueRef BuildGep(LLVMBuilderRef builder, LLVMValueRef statePtr, int fieldIndex, string name)
    {
        var i32 = LLVMTypeRef.Int32;
        var indices = new[]
        {
            LLVMValueRef.CreateConstInt(i32, 0,                         SignExtend: false),
            LLVMValueRef.CreateConstInt(i32, (ulong)(uint)fieldIndex,   SignExtend: false),
        };
        return builder.BuildGEP2(StructType, statePtr, indices, name);
    }
}
