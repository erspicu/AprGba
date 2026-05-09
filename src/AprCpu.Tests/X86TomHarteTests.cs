// Phase 24.2.4 — Tom Harte SingleStepTests 8088 (https://github.com/
// SingleStepTests/8088) integration. Runs the gold-standard 8088 test
// suite (10000 random tests/opcode covering all flag/addressing/value
// edge cases) against X86LegacyCpu.
//
// The SST data lives under OldProject/8088/v2/<opcode>.json.gz and is
// 3.3 GB total. These tests are CONDITIONAL — they only execute when
// the data is present locally; CI / external clones get a clean SKIP.
// CLI users can run the same data via:
//   apr-x86 --tomharte=OldProject/8088/v2/00.json.gz
//
// We sample a representative subset (~10 opcodes covering ALU + MOV +
// segment-override) for the unit-test path; the full 80+ opcode batch
// is documented in MD/design/24-8086-port-plan.md and runs via the CLI.

using AprX86.Cli.Tests;
using Xunit;

namespace AprCpu.Tests;

public class X86TomHarteTests
{
    private static string SstRoot =>
        Path.Combine(TestPaths.RepoRoot, "OldProject", "8088");

    private static string? LocateOpcodeFile(string opcodeHex)
    {
        // Most opcodes live under v2/; group sub-opcodes (FE.0 etc.) and
        // a few stragglers only exist under v1/. Probe both.
        foreach (var ver in new[] { "v2", "v1" })
        {
            var p = Path.Combine(SstRoot, ver, $"{opcodeHex}.json.gz");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private void RunOpcodeOrSkip(string opcodeHex, int? limit = null)
    {
        var path = LocateOpcodeFile(opcodeHex);
        if (path == null)
            return;     // data not present locally — silently skip

        var runner = new TomHarteRunner();
        var result = runner.RunFile(path, limit);
        if (result.Failed > 0)
        {
            var first = result.Failures.First();
            Assert.Fail(
                $"Tom Harte SST opcode 0x{opcodeHex}: {result.Failed}/{result.Total} failed. " +
                $"First: [{first.Index}] {first.Name} → {string.Join("; ", first.Diffs.Take(3))}");
        }
        Assert.Equal(result.Total, result.Passed);
    }

    // ===== ALU groups (8 mnemonics × 6 forms = 48 opcodes; sample 6) =====

    [Fact] public void Tom_00_Add_Rm8_R8()      => RunOpcodeOrSkip("00");
    [Fact] public void Tom_01_Add_Rm16_R16()    => RunOpcodeOrSkip("01");
    [Fact] public void Tom_28_Sub_Rm8_R8()      => RunOpcodeOrSkip("28");
    [Fact] public void Tom_38_Cmp_Rm8_R8()      => RunOpcodeOrSkip("38");
    [Fact] public void Tom_30_Xor_Rm8_R8()      => RunOpcodeOrSkip("30");
    [Fact] public void Tom_20_And_Rm8_R8()      => RunOpcodeOrSkip("20");

    // ===== MOV variants =====

    [Fact] public void Tom_88_Mov_Rm8_R8()      => RunOpcodeOrSkip("88");
    [Fact] public void Tom_89_Mov_Rm16_R16()    => RunOpcodeOrSkip("89");
    [Fact] public void Tom_8A_Mov_R8_Rm8()      => RunOpcodeOrSkip("8A");
    [Fact] public void Tom_8B_Mov_R16_Rm16()    => RunOpcodeOrSkip("8B");
    [Fact] public void Tom_8C_Mov_Rm16_Sreg()   => RunOpcodeOrSkip("8C");
    [Fact] public void Tom_8E_Mov_Sreg_Rm16()   => RunOpcodeOrSkip("8E");
    [Fact] public void Tom_A0_Mov_AL_Disp16()   => RunOpcodeOrSkip("A0");
    [Fact] public void Tom_A3_Mov_Disp16_AX()   => RunOpcodeOrSkip("A3");
    [Fact] public void Tom_B0_Mov_AL_Imm8()     => RunOpcodeOrSkip("B0");
    [Fact] public void Tom_B8_Mov_AX_Imm16()    => RunOpcodeOrSkip("B8");
    [Fact] public void Tom_C6_Mov_Rm8_Imm8()    => RunOpcodeOrSkip("C6");
    [Fact] public void Tom_C7_Mov_Rm16_Imm16()  => RunOpcodeOrSkip("C7");

    // ===== Group 0x80-0x83 (ALU r/m, imm — ModR/M reg selects op) =====

    [Fact] public void Tom_80_AluRm8_Imm8()     => RunOpcodeOrSkip("80");
    [Fact] public void Tom_81_AluRm16_Imm16()   => RunOpcodeOrSkip("81");
    [Fact] public void Tom_82_AluRm8_Imm8_Sx()  => RunOpcodeOrSkip("82");
    [Fact] public void Tom_83_AluRm16_Imm8_Sx() => RunOpcodeOrSkip("83");

    // ===== NOP (sanity check) =====

    [Fact] public void Tom_90_Nop()             => RunOpcodeOrSkip("90");

    // ===== 24.4.1 — PUSH/POP/INC/DEC/XCHG =====

    [Fact] public void Tom_06_Push_ES()         => RunOpcodeOrSkip("06");
    [Fact] public void Tom_07_Pop_ES()          => RunOpcodeOrSkip("07");
    [Fact] public void Tom_0E_Push_CS()         => RunOpcodeOrSkip("0E");
    [Fact] public void Tom_16_Push_SS()         => RunOpcodeOrSkip("16");
    [Fact] public void Tom_17_Pop_SS()          => RunOpcodeOrSkip("17");
    [Fact] public void Tom_1E_Push_DS()         => RunOpcodeOrSkip("1E");
    [Fact] public void Tom_1F_Pop_DS()          => RunOpcodeOrSkip("1F");
    [Fact] public void Tom_40_Inc_AX()          => RunOpcodeOrSkip("40");
    [Fact] public void Tom_47_Inc_DI()          => RunOpcodeOrSkip("47");
    [Fact] public void Tom_48_Dec_AX()          => RunOpcodeOrSkip("48");
    [Fact] public void Tom_4F_Dec_DI()          => RunOpcodeOrSkip("4F");
    [Fact] public void Tom_50_Push_AX()         => RunOpcodeOrSkip("50");
    [Fact] public void Tom_54_Push_SP()         => RunOpcodeOrSkip("54");   // 8086 quirk
    [Fact] public void Tom_57_Push_DI()         => RunOpcodeOrSkip("57");
    [Fact] public void Tom_58_Pop_AX()          => RunOpcodeOrSkip("58");
    [Fact] public void Tom_5F_Pop_DI()          => RunOpcodeOrSkip("5F");
    [Fact] public void Tom_86_Xchg_Rm8_R8()     => RunOpcodeOrSkip("86");
    [Fact] public void Tom_87_Xchg_Rm16_R16()   => RunOpcodeOrSkip("87");
    [Fact] public void Tom_8F_Pop_Rm16()        => RunOpcodeOrSkip("8F");
    [Fact] public void Tom_91_Xchg_AX_CX()      => RunOpcodeOrSkip("91");
    [Fact] public void Tom_97_Xchg_AX_DI()      => RunOpcodeOrSkip("97");
    [Fact] public void Tom_9C_Pushf()           => RunOpcodeOrSkip("9C");
    [Fact] public void Tom_9D_Popf()            => RunOpcodeOrSkip("9D");
    [Fact] public void Tom_FE_0_Inc_Rm8()       => RunOpcodeOrSkip("FE.0");
    [Fact] public void Tom_FE_1_Dec_Rm8()       => RunOpcodeOrSkip("FE.1");
    [Fact] public void Tom_FF_0_Inc_Rm16()      => RunOpcodeOrSkip("FF.0");
    [Fact] public void Tom_FF_1_Dec_Rm16()      => RunOpcodeOrSkip("FF.1");
    [Fact] public void Tom_FF_6_Push_Rm16()     => RunOpcodeOrSkip("FF.6");   // 8086 SP quirk via mem path too
}
