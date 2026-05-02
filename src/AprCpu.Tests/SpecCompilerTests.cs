using AprCpu.Core.Compilation;
using Xunit;

namespace AprCpu.Tests;

public class SpecCompilerTests
{
    private static string CpuJson => Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json");

    [Fact]
    public void Compile_ProducesFunctionsForArmAndThumbInstructions()
    {
        var result = SpecCompiler.Compile(CpuJson);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.DecoderTables.Count);

        // Phase 2.5.2a: full set of 16 ARM Data Processing Immediate ALU ops
        var allArmAlu = new[]
        {
            "AND", "EOR", "SUB", "RSB", "ADD", "ADC", "SBC", "RSC",
            "TST", "TEQ", "CMP", "CMN", "ORR", "MOV", "BIC", "MVN"
        };
        foreach (var mnemonic in allArmAlu)
        {
            Assert.Contains($"ARM.DataProcessing_Immediate.{mnemonic}", result.Functions.Keys);
        }

        Assert.Contains("ARM.Branch_BL.B",                result.Functions.Keys);
        Assert.Contains("ARM.Branch_BL.BL",               result.Functions.Keys);
        Assert.Contains("ARM.Branch_Exchange.BX",         result.Functions.Keys);
        Assert.Contains("Thumb.Thumb_F1_MoveShiftedRegister.LSL", result.Functions.Keys);
        Assert.Contains("Thumb.Thumb_F3_ImmediateOps.ADD",        result.Functions.Keys);
        Assert.Contains("Thumb.Thumb_F18_B.B",                    result.Functions.Keys);
    }

    [Fact]
    public void Compile_EmittedIRContainsExpectedShapeForArmAdd()
    {
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();

        Assert.Contains("define void @Execute_ARM_DataProcessing_Immediate_ADD",  ir);
        Assert.Contains("lshr i32",  ir);  // field extraction
        Assert.Contains("add i32",   ir);  // arithmetic
        Assert.Contains("store i32", ir);  // write_reg
    }

    [Fact]
    public void Compile_EmittedIRForThumbLslContainsShl()
    {
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();

        Assert.Contains("define void @Execute_Thumb_Thumb_F1_MoveShiftedRegister_LSL", ir);
        Assert.Contains("shl i32", ir);
    }

    [Fact]
    public void Compile_EmittedIRForSwiContainsExceptionEntrySequence()
    {
        // 2.5.7b: SWI must save CPSR -> SPSR_Supervisor, save next-PC -> banked
        // R14_Supervisor, switch CPSR.M to Supervisor (10011 = 0x13), set CPSR.I,
        // call host_swap_register_bank, and store the SWI vector address (0x8) to PC.
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();

        // Phase 3.1.b: extern is now an indirect call through a global ptr
        // (MCJIT can't reliably bind function decls in LLVM 20).
        Assert.Contains("@host_swap_register_bank = external global ptr", ir);
        // The SWI emit body must load the swap-fn slot, call through it, and store the vector address.
        var swiFnIdx = ir.IndexOf("@Execute_ARM_SWI_SWI", StringComparison.Ordinal);
        Assert.True(swiFnIdx >= 0, "SWI function should be emitted");
        var nextDefine = ir.IndexOf("\ndefine ", swiFnIdx + 1, StringComparison.Ordinal);
        var swiBody = nextDefine > 0 ? ir.Substring(swiFnIdx, nextDefine - swiFnIdx) : ir.Substring(swiFnIdx);

        Assert.Contains("load ptr, ptr @host_swap_register_bank", swiBody);
        Assert.Contains("call void %swap_fn(", swiBody);
        Assert.Contains("store i32 8,", swiBody);          // PC := 0x8 (SoftwareInterrupt vector)
        Assert.Contains("cpsr_with_new_mode", swiBody);    // mode bits replaced
        Assert.Contains("cpsr_disable_i", swiBody);        // I bit set per spec disable=["I"]
    }

    [Fact]
    public void Compile_EmittedIRForSubsContainsRestoreCpsrSwitch()
    {
        // 2.5.7c: restore_cpsr_from_spsr lowers to a switch over CPSR.M[4:0]
        // with one case per banked-SPSR mode (FIQ/IRQ/Supervisor/Abort/Undefined),
        // a PHI to merge the chosen SPSR (or oldCpsr in default), and a swap call.
        var result = SpecCompiler.Compile(CpuJson);
        var ir = result.Module.PrintToString();

        // restore is wired into ALU bodies via the if-S-and-Rd=PC path —
        // SUBS Register-shifted is one such function.
        var fnIdx = ir.IndexOf("@Execute_ARM_DataProcessing_Immediate_SUB", StringComparison.Ordinal);
        Assert.True(fnIdx >= 0, "SUB function should be emitted");
        var nextDefine = ir.IndexOf("\ndefine ", fnIdx + 1, StringComparison.Ordinal);
        var body = nextDefine > 0 ? ir.Substring(fnIdx, nextDefine - fnIdx) : ir.Substring(fnIdx);

        Assert.Contains("restore_cpsr_merge", body);
        Assert.Contains("restore_cpsr_default", body);
        Assert.Contains("restore_cpsr_from_spsr_supervisor", body);
        Assert.Contains("restore_cpsr_from_spsr_fiq", body);
        Assert.Contains("phi i32", body);
        Assert.Contains("call void %swap_fn(", body);
    }

    [Fact]
    public void CompileToFile_WritesValidLlFile()
    {
        var temp = Path.Combine(TestPaths.RepoRoot, "temp", "spec_compiler_test.ll");
        if (File.Exists(temp)) File.Delete(temp);

        var result = SpecCompiler.CompileToFile(CpuJson, temp);

        Assert.True(File.Exists(temp));
        var content = File.ReadAllText(temp);
        Assert.Contains("define void @Execute_ARM_DataProcessing_Immediate_ADD", content);
        Assert.Empty(result.Diagnostics);
    }

    // ---------------- LR35902 (Phase 4.5C) ----------------

    private static string Lr35902CpuJson => Path.Combine(TestPaths.SpecRoot, "lr35902", "cpu.json");

    /// <summary>
    /// Baseline: the spec loads, the module builds, and the trivial "no-step"
    /// instructions (NOP and HALT-like) emit even before LR35902 emitters
    /// land. Functions whose steps reference unimplemented micro-ops are
    /// captured as diagnostics, not exceptions.
    /// </summary>
    [Fact]
    public void Dump_Lr35902_Ir_ToTemp()
    {
        var temp = Path.Combine(TestPaths.RepoRoot, "temp", "lr35902_full.ll");
        if (File.Exists(temp)) File.Delete(temp);
        SpecCompiler.CompileToFile(Lr35902CpuJson, temp);
        Assert.True(File.Exists(temp));
    }

    [Fact]
    public void Compile_Lr35902_LoadsAndEmitsAtLeastNop()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Equal(2, result.DecoderTables.Count);
        Assert.True(result.DecoderTables.ContainsKey("Main"));
        Assert.True(result.DecoderTables.ContainsKey("CB"));

        // NOP has empty steps[], so it compiles to bare entry+ret without
        // any emitter being needed.
        Assert.Contains("Main.Nop.NOP", result.Functions.Keys);

        // The IR text should declare the function.
        var ir = result.Module.PrintToString();
        Assert.Contains("define void @Execute_Main_Nop_NOP", ir);
    }

    /// <summary>
    /// First emitter wave: the F-flag-only ops (SCF, CCF, CPL) compile, plus
    /// HALT/STOP which currently lower to no-op placeholders. Together with
    /// NOP these constitute the smallest set that exercises the full
    /// SpecCompiler path for an 8-bit CPU.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_FlagOnlyOpsCompile()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Contains("Main.Scf.SCF", result.Functions.Keys);
        Assert.Contains("Main.Ccf.CCF", result.Functions.Keys);
        Assert.Contains("Main.Cpl.CPL", result.Functions.Keys);
        Assert.Contains("Main.Halt.HALT", result.Functions.Keys);
        Assert.Contains("Main.Stop.STOP", result.Functions.Keys);

        var ir = result.Module.PrintToString();
        Assert.Contains("define void @Execute_Main_Scf_SCF", ir);
        Assert.Contains("define void @Execute_Main_Ccf_CCF", ir);
        Assert.Contains("define void @Execute_Main_Cpl_CPL", ir);

        // CPL must read A, invert it, and write it back. After Step 5.7.A
        // migration this is a generic chain: read_reg_named(A) → mvn (i8
        // XOR with all-ones) → write_reg_named(A) → 2× set_flag(N=1, H=1).
        // We assert structurally rather than by label to keep the test
        // robust against future label-name changes inside the emitters.
        var cplFnIdx = ir.IndexOf("@Execute_Main_Cpl_CPL", StringComparison.Ordinal);
        Assert.True(cplFnIdx >= 0);
        var nextDefine = ir.IndexOf("\ndefine ", cplFnIdx + 1, StringComparison.Ordinal);
        var cplBody = nextDefine > 0 ? ir.Substring(cplFnIdx, nextDefine - cplFnIdx) : ir.Substring(cplFnIdx);
        Assert.Contains("xor i8", cplBody);     // A = ~A (i8 XOR with -1 / all-ones)
        Assert.Contains("load i8", cplBody);    // A and F both get loaded as i8
        Assert.Contains("store i8", cplBody);   // A and F both get stored as i8
    }

    /// <summary>
    /// Block-0 col-7 A-rotates (RLCA/RRCA/RLA/RRA) compile and the IR
    /// contains the expected shl/lshr i8 sequence.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_ARotatesCompile()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Contains("Main.Rlca.RLCA", result.Functions.Keys);
        Assert.Contains("Main.Rrca.RRCA", result.Functions.Keys);
        Assert.Contains("Main.Rla.RLA",   result.Functions.Keys);
        Assert.Contains("Main.Rra.RRA",   result.Functions.Keys);

        var ir = result.Module.PrintToString();
        // Each rotate body should have an i8 shift.
        var rlcaIdx = ir.IndexOf("@Execute_Main_Rlca_RLCA", StringComparison.Ordinal);
        Assert.True(rlcaIdx >= 0);
        var nextDef = ir.IndexOf("\ndefine ", rlcaIdx + 1, StringComparison.Ordinal);
        var rlcaBody = nextDef > 0 ? ir.Substring(rlcaIdx, nextDef - rlcaIdx) : ir.Substring(rlcaIdx);
        Assert.Contains("shl i8",  rlcaBody);
        Assert.Contains("lshr i8", rlcaBody);
    }

    /// <summary>
    /// Wave 2: block 1 (LD r,r') compiles for all three formats — the
    /// reg-to-reg general path, plus the (HL)-source and (HL)-dest splits
    /// that depend on read_reg_pair_named + lr35902_read_r8/write_r8.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_Block1_LdRegReg_AllFormatsCompile()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Contains("Main.LdReg_Reg.LD",    result.Functions.Keys);
        Assert.Contains("Main.LdHlInd_Reg.LD",  result.Functions.Keys);
        Assert.Contains("Main.LdReg_HlInd.LD",  result.Functions.Keys);

        var ir = result.Module.PrintToString();
        // The general LD r,r' must contain the runtime select chain that
        // picks the source register based on sss.
        var fnIdx = ir.IndexOf("@Execute_Main_LdReg_Reg_LD", StringComparison.Ordinal);
        Assert.True(fnIdx >= 0);
        var nextDef = ir.IndexOf("\ndefine ", fnIdx + 1, StringComparison.Ordinal);
        var body = nextDef > 0 ? ir.Substring(fnIdx, nextDef - fnIdx) : ir.Substring(fnIdx);

        // The select-chain produces "r8_sel0..r8_sel6" labels.
        Assert.Contains("r8_sel0", body);
        Assert.Contains("r8_sel6", body);
        // And conditional-store merges of form "r8_w_merge_*".
        Assert.Contains("r8_w_merge_", body);
    }

    /// <summary>
    /// Wave 2: block 0 mem-indirect ops (LD (BC)/(DE)/(HL+)/(HL-) ↔ A)
    /// compile via read_reg_pair_named + read/write_reg_named + the
    /// inc/dec_pair helpers. Memory access is still placeholder.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_Block0_MemIndirect_Compiles()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        // All 8 mem-indirect formats compile.
        Assert.Contains("Main.LdInd_BC_A.LD",    result.Functions.Keys);
        Assert.Contains("Main.LdInd_DE_A.LD",    result.Functions.Keys);
        Assert.Contains("Main.LdInd_HLi_A.LDI",  result.Functions.Keys);
        Assert.Contains("Main.LdInd_HLd_A.LDD",  result.Functions.Keys);
        Assert.Contains("Main.LdInd_A_BC.LD",    result.Functions.Keys);
        Assert.Contains("Main.LdInd_A_DE.LD",    result.Functions.Keys);
        Assert.Contains("Main.LdInd_A_HLi.LDI",  result.Functions.Keys);
        Assert.Contains("Main.LdInd_A_HLd.LDD",  result.Functions.Keys);

        // (HL+) variant must contain the HL increment sequence.
        var ir = result.Module.PrintToString();
        var hliIdx = ir.IndexOf("@Execute_Main_LdInd_HLi_A_LDI", StringComparison.Ordinal);
        Assert.True(hliIdx >= 0);
        var nextDef = ir.IndexOf("\ndefine ", hliIdx + 1, StringComparison.Ordinal);
        var body = nextDef > 0 ? ir.Substring(hliIdx, nextDef - hliIdx) : ir.Substring(hliIdx);

        Assert.Contains("HL_hi", body);     // pair compose
        Assert.Contains("HL_lo", body);
        Assert.Contains("hl_inc",   body);  // INC step
    }

    /// <summary>
    /// Wave 2: 16-bit pair read + write_reg_named to SP — verifies the
    /// SP/PC 16-bit status-register path through write_reg_named, and
    /// that read_reg_pair_named composes HL correctly.
    /// (JP HL needs lr35902_jp control-flow op — wave 3.)
    /// </summary>
    [Fact]
    public void Compile_Lr35902_LdSpHl_Compiles()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Contains("Main.Ld_Sp_Hl.LD", result.Functions.Keys);

        var ir = result.Module.PrintToString();
        var fnIdx = ir.IndexOf("@Execute_Main_Ld_Sp_Hl_LD", StringComparison.Ordinal);
        Assert.True(fnIdx >= 0);
        var nextDef = ir.IndexOf("\ndefine ", fnIdx + 1, StringComparison.Ordinal);
        var body = nextDef > 0 ? ir.Substring(fnIdx, nextDef - fnIdx) : ir.Substring(fnIdx);

        // HL composed from H+L halves.
        Assert.Contains("HL_hi", body);
        Assert.Contains("HL_lo", body);
        // i16 store into SP.
        Assert.Contains("store i16", body);
    }

    /// <summary>
    /// Wave 3: 8-bit ALU on A — block 2 (ALU A,r) and block 3 (ALU A,n).
    /// Verifies all 8 ops × 3 source variants compile.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_Alu8OpsCompile()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        // Block 2 reg-source — 8 ops × 1 instruction-def each = 8 entries.
        // Each instruction-def in spec disambiguates by ooo selector, so
        // function key includes the selector value.
        Assert.Contains("Main.AluA_Reg.ADD", result.Functions.Keys);
        Assert.Contains("Main.AluA_Reg.ADC", result.Functions.Keys);
        Assert.Contains("Main.AluA_Reg.SUB", result.Functions.Keys);
        Assert.Contains("Main.AluA_Reg.SBC", result.Functions.Keys);
        Assert.Contains("Main.AluA_Reg.AND", result.Functions.Keys);
        Assert.Contains("Main.AluA_Reg.XOR", result.Functions.Keys);
        Assert.Contains("Main.AluA_Reg.OR",  result.Functions.Keys);
        Assert.Contains("Main.AluA_Reg.CP",  result.Functions.Keys);

        // Block 2 (HL)-source.
        Assert.Contains("Main.AluA_HlInd.ADD", result.Functions.Keys);
        Assert.Contains("Main.AluA_HlInd.CP",  result.Functions.Keys);

        // Block 3 imm8-source.
        Assert.Contains("Main.AluA_Imm8.ADD", result.Functions.Keys);
        Assert.Contains("Main.AluA_Imm8.CP",  result.Functions.Keys);

        var ir = result.Module.PrintToString();
        // ADD body should contain the F flag-store sequence (from StoreFlags).
        var addIdx = ir.IndexOf("@Execute_Main_AluA_Reg_ADD", StringComparison.Ordinal);
        Assert.True(addIdx >= 0);
        var nextDef = ir.IndexOf("\ndefine ", addIdx + 1, StringComparison.Ordinal);
        var body = nextDef > 0 ? ir.Substring(addIdx, nextDef - addIdx) : ir.Substring(addIdx);
        Assert.Contains("z_cmp", body);
        Assert.Contains("h_cmp", body);
        Assert.Contains("c_cmp", body);
        Assert.Contains("f_new", body);
    }

    /// <summary>
    /// Wave 3: 8-bit INC/DEC selected by ddd field.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_IncDec8Compile()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Contains("Main.Inc_R8.INC", result.Functions.Keys);
        Assert.Contains("Main.Dec_R8.DEC", result.Functions.Keys);
    }

    /// <summary>
    /// Wave 3: 16-bit ops on dd-field pairs — LD rr,nn / INC rr / DEC rr /
    /// ADD HL,rr.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_RrOpsCompile()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Contains("Main.LdRr_Imm16.LD",  result.Functions.Keys);
        Assert.Contains("Main.Inc_Rr.INC",     result.Functions.Keys);
        Assert.Contains("Main.Dec_Rr.DEC",     result.Functions.Keys);
        Assert.Contains("Main.AddHl_Rr.ADD",   result.Functions.Keys);

        // ADD HL,rr body must contain the 12-bit half-carry compute.
        var ir = result.Module.PrintToString();
        var addIdx = ir.IndexOf("@Execute_Main_AddHl_Rr_ADD", StringComparison.Ordinal);
        Assert.True(addIdx >= 0);
        var nextDef = ir.IndexOf("\ndefine ", addIdx + 1, StringComparison.Ordinal);
        var body = nextDef > 0 ? ir.Substring(addIdx, nextDef - addIdx) : ir.Substring(addIdx);

        Assert.Contains("hl_low12",  body);
        Assert.Contains("rr_low12",  body);
        Assert.Contains("low12_sum", body);
    }

    /// <summary>
    /// Wave 5: CB-prefix instructions compile — RLC/RRC/RL/RR/SLA/SRA/
    /// SWAP/SRL + BIT/RES/SET. With these, the entire CB instruction
    /// set produces IR.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_CbInstructionsCompile()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        // Shift family — 8 entries selected by ooo.
        Assert.Contains("CB.Cb_Shift.RLC",  result.Functions.Keys);
        Assert.Contains("CB.Cb_Shift.RRC",  result.Functions.Keys);
        Assert.Contains("CB.Cb_Shift.RL",   result.Functions.Keys);
        Assert.Contains("CB.Cb_Shift.RR",   result.Functions.Keys);
        Assert.Contains("CB.Cb_Shift.SLA",  result.Functions.Keys);
        Assert.Contains("CB.Cb_Shift.SRA",  result.Functions.Keys);
        Assert.Contains("CB.Cb_Shift.SWAP", result.Functions.Keys);
        Assert.Contains("CB.Cb_Shift.SRL",  result.Functions.Keys);

        // BIT/RES/SET — single instruction each (with bbb/sss runtime).
        Assert.Contains("CB.Cb_Bit.BIT", result.Functions.Keys);
        Assert.Contains("CB.Cb_Res.RES", result.Functions.Keys);
        Assert.Contains("CB.Cb_Set.SET", result.Functions.Keys);

        var ir = result.Module.PrintToString();
        // SWAP body should contain the high/low nibble swap.
        var swapIdx = ir.IndexOf("@Execute_CB_Cb_Shift_SWAP", StringComparison.Ordinal);
        Assert.True(swapIdx >= 0);
        var nextDef = ir.IndexOf("\ndefine ", swapIdx + 1, StringComparison.Ordinal);
        var swapBody = nextDef > 0 ? ir.Substring(swapIdx, nextDef - swapIdx) : ir.Substring(swapIdx);
        Assert.Contains("swap_hi", swapBody);
        Assert.Contains("swap_lo", swapBody);
    }

    /// <summary>
    /// Wave 5: stack arithmetic — ADD SP,e8 and LD HL,SP+e8 share the
    /// special H/C-from-low-byte rules. Plus LDH IO ops.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_StackArithAndLdhCompile()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        Assert.Contains("Main.Add_Sp_E8.ADD",   result.Functions.Keys);
        Assert.Contains("Main.Ld_Hl_Sp_E8.LD",  result.Functions.Keys);
        Assert.Contains("Main.Ldh_N_A.LDH",     result.Functions.Keys);
        Assert.Contains("Main.Ldh_A_N.LDH",     result.Functions.Keys);
        Assert.Contains("Main.Ld_C_A.LD",       result.Functions.Keys);
        Assert.Contains("Main.Ld_A_C.LD",       result.Functions.Keys);
    }

    /// <summary>
    /// Wave 4: control flow ops compile — JP / JR / CALL / RET / RST,
    /// with conditional and unconditional variants. PUSH / POP also
    /// land here even though their memory writes are still placeholder.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_ControlFlowCompiles()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        // JP family
        Assert.Contains("Main.Jp_Nn.JP",        result.Functions.Keys);
        Assert.Contains("Main.Jp_Hl.JP",        result.Functions.Keys);
        Assert.Contains("Main.Jp_Cc_Nn.JP_00",  result.Functions.Keys);
        Assert.Contains("Main.Jp_Cc_Nn.JP_11",  result.Functions.Keys);

        // JR family
        Assert.Contains("Main.Jr_E8.JR",        result.Functions.Keys);
        Assert.Contains("Main.Jr_Cc_E8.JR_00",  result.Functions.Keys);
        Assert.Contains("Main.Jr_Cc_E8.JR_11",  result.Functions.Keys);

        // CALL family
        Assert.Contains("Main.Call_Nn.CALL",       result.Functions.Keys);
        Assert.Contains("Main.Call_Cc_Nn.CALL_00", result.Functions.Keys);

        // RET family — incl. RETI
        Assert.Contains("Main.Ret.RET",      result.Functions.Keys);
        Assert.Contains("Main.Reti.RETI",    result.Functions.Keys);
        Assert.Contains("Main.Ret_Cc.RET_00",result.Functions.Keys);

        // RST + PUSH/POP — both split into selector variants per opcode
        // when migrated to the generic emitters: PUSH/POP qq=00..11 →
        // BC/DE/HL/AF; RST ttt=000..111 → 0x00/08/10/.../38. Spec
        // compiler suffixes the function name with the selector value
        // (with leading zeros sized to the field width).
        Assert.Contains("Main.Rst.RST_000",     result.Functions.Keys);
        Assert.Contains("Main.Rst.RST_111",     result.Functions.Keys);
        Assert.Contains("Main.Push_Rr.PUSH_00", result.Functions.Keys);
        Assert.Contains("Main.Push_Rr.PUSH_11", result.Functions.Keys);    // PUSH AF
        Assert.Contains("Main.Pop_Rr.POP_00",   result.Functions.Keys);
        Assert.Contains("Main.Pop_Rr.POP_11",   result.Functions.Keys);    // POP AF (low_clear_mask)

        // JP body should write i16 to PC.
        var ir = result.Module.PrintToString();
        var jpIdx = ir.IndexOf("@Execute_Main_Jp_Nn_JP", StringComparison.Ordinal);
        Assert.True(jpIdx >= 0);
        var nextDef = ir.IndexOf("\ndefine ", jpIdx + 1, StringComparison.Ordinal);
        var body = nextDef > 0 ? ir.Substring(jpIdx, nextDef - jpIdx) : ir.Substring(jpIdx);
        Assert.Contains("store i16", body);

        // JR body composes target = PC + sext(off8) via generic ops
        // (read_imm8 → sext → read_pc → add → branch). The generic
        // read_pc op names its load "pc_cur" (matches "out" name); the
        // final branch op stores i16 back to PC.
        var jrIdx = ir.IndexOf("@Execute_Main_Jr_E8_JR", StringComparison.Ordinal);
        Assert.True(jrIdx >= 0);
        var jrNext = ir.IndexOf("\ndefine ", jrIdx + 1, StringComparison.Ordinal);
        var jrBody = jrNext > 0 ? ir.Substring(jrIdx, jrNext - jrIdx) : ir.Substring(jrIdx);
        Assert.Contains("pc_cur", jrBody);
        Assert.Contains("store i16", jrBody);
    }

    /// <summary>
    /// Track JsonCpu compilation coverage: how many of LR35902's instructions
    /// produce a function vs. how many surface as "no emitter" diagnostics.
    /// As more emitters land, the diagnostic count drops; this test merely
    /// records the current threshold so a regression (an emitter accidentally
    /// removed) shows up.
    /// </summary>
    [Fact]
    public void Compile_Lr35902_CoverageBaseline()
    {
        var result = SpecCompiler.Compile(Lr35902CpuJson);

        // Every function listed here is one that successfully emitted IR.
        // Use this number as a baseline; assert it doesn't regress below
        // the current set of emitters (NOP + 5 flag/halt-style + every
        // CB-prefix instruction whose steps reference no extant emitter
        // would also count if added).
        var compiled = result.Functions.Count;
        var failed = result.Diagnostics.Count(d => d.Contains("emission failed"));

        // Print to test output so this test doubles as a coverage report.
        var byMnemonic = result.Functions.Keys
            .Select(k => k.Split('.').Last())
            .GroupBy(m => m)
            .OrderBy(g => g.Key)
            .ToList();
        var compiledMnemonics = string.Join(", ", byMnemonic.Select(g => $"{g.Key}×{g.Count()}"));
        var sampleFailures = string.Join(" || ", result.Diagnostics.Take(3));

        Assert.True(compiled >= 102,
            $"Coverage regression: expected ≥102 compiled instructions (got {compiled}). " +
            $"Compiled mnemonics: {compiledMnemonics}. " +
            $"Sample failures: {sampleFailures}");
        Assert.Empty(result.Diagnostics);
        Assert.True(compiled + failed >= 100,
            $"Spec should account for ≥100 instructions total (compiled+failed got {compiled}+{failed} = {compiled + failed}).");
    }
}
