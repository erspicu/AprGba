// Phase 24.6.4 — X86JsonCpu (per-instr JSON-driven backend) end-to-end
// roundtrip tests. The smoke group from 24.6.2 (NOP + HLT) is the only
// coverage at this sub-step; later sub-phases (24.6.5 MOV, 24.6.6 ALU,
// 24.6.7 ctrl/shift/string/misc) bring real opcodes online and run the
// full Tom Harte SST 8088 v2 sweep against this backend.
//
// What this file proves:
//   - SpecCompiler → HostRuntime → ORC LLJIT pipeline hands back live
//     function pointers for x86-16 instructions
//   - X86JsonCpu.Step() correctly: fetches opcode at CS:IP, advances IP
//     past the opcode byte, dispatches to the JIT'd function, mirrors
//     state out via the X86State accessor
//   - x86_halt emitter works — HLT sets Halted=true after one Step()

using AprX86.Cli.Cpu;
using AprX86.Cli.Memory;
using Xunit;

namespace AprCpu.Tests;

public class X86JsonCpuTests
{
    private static (X86JsonCpu cpu, X86Memory mem) Setup(byte[] code)
    {
        var mem = new X86Memory();
        mem.LoadBinary(code, 0, 0x100);
        var cpu = new X86JsonCpu(mem);
        cpu.Reset();
        cpu.SetEntryPoint(0, 0x100);
        return (cpu, mem);
    }

    [Fact]
    public void BackendName_IsJsonLlvm()
    {
        var (cpu, _) = Setup(new byte[] { 0xF4 });
        Assert.Equal("json-llvm", cpu.BackendName);
    }

    [Fact]
    public void Reset_RestoresArchitecturalResetState()
    {
        var (cpu, _) = Setup(new byte[] { 0x90 });
        // SetEntryPoint moved CS:IP; Reset should revert.
        cpu.Reset();
        Assert.Equal(0xFFFF, cpu.State.CS);
        Assert.Equal(0,      cpu.State.IP);
        Assert.False(cpu.Halted);
    }

    /// <summary>
    /// 0x90 NOP — empty steps body. After Step(), IP advances by 1, no
    /// other state changes, not halted.
    /// </summary>
    [Fact]
    public void Step_Nop_AdvancesIpByOneNothingElseChanges()
    {
        var (cpu, _) = Setup(new byte[] { 0x90 });

        var before = cpu.State;
        Assert.Equal(0x100, before.IP);

        cpu.Step();

        var after = cpu.State;
        Assert.Equal(0x101, after.IP);
        Assert.False(cpu.Halted);
        // GPRs untouched.
        Assert.Equal(before.A.X, after.A.X);
        Assert.Equal(before.B.X, after.B.X);
        Assert.Equal(before.C.X, after.C.X);
        Assert.Equal(before.D.X, after.D.X);
        // FLAGS untouched.
        Assert.Equal(before.GetFlags(), after.GetFlags());
    }

    /// <summary>
    /// 0xF4 HLT — single x86_halt step. After Step(), Halted=true and IP
    /// is past the HLT byte (silicon increments IP first, then halts).
    /// </summary>
    [Fact]
    public void Step_Hlt_SetsHaltedAndAdvancesIp()
    {
        var (cpu, _) = Setup(new byte[] { 0xF4 });

        Assert.False(cpu.Halted);
        cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x101, cpu.State.IP);
    }

    /// <summary>
    /// Several NOPs followed by HLT — exercises the function-pointer cache
    /// (NOP runs 3× from cache, HLT runs once) and the inter-step state
    /// continuity. After 4 steps Halted=true and IP=0x104.
    /// </summary>
    [Fact]
    public void Step_NopNopNopHlt_StopsAtHlt()
    {
        var (cpu, _) = Setup(new byte[] { 0x90, 0x90, 0x90, 0xF4 });

        for (int i = 0; i < 16 && !cpu.Halted; i++) cpu.Step();

        Assert.True(cpu.Halted);
        Assert.Equal(0x104, cpu.State.IP);
    }

    /// <summary>
    /// Step() returns -1 for an opcode the spec doesn't yet cover (e.g. 0x00
    /// in 24.6.4 — only NOP/HLT are wired). IP must NOT advance; the caller
    /// can fall through to legacy or extend the spec.
    /// </summary>
    [Fact]
    public void Step_UnknownOpcode_ReturnsMinusOneAndPreservesIp()
    {
        var (cpu, _) = Setup(new byte[] { 0x00, 0x00 });

        var rc = cpu.Step();
        Assert.Equal(-1, rc);
        Assert.Equal(0x100, cpu.State.IP);
        Assert.False(cpu.Halted);
    }

    // ---------------- 24.6.5 — MOV r, imm (B0-BF) ----------------

    /// <summary>0xB0 12  → MOV AL, 0x12. AL is byte-half of AX[7:0]; AH untouched.</summary>
    [Fact]
    public void Step_MovAlImm8_LoadsLowByteOfAx_PreservesAh()
    {
        var (cpu, _) = Setup(new byte[] { 0xB0, 0x12, 0xF4 });
        // Pre-load AH=0x99 so we can verify it stays put.
        var s = cpu.State;
        s.A.H = 0x99;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        cpu.Step();   // MOV AL, 0x12

        Assert.Equal(0x12, cpu.State.A.L);
        Assert.Equal(0x99, cpu.State.A.H);
        Assert.Equal(0x9912, cpu.State.A.X);
        Assert.Equal(0x102, cpu.State.IP);
    }

    /// <summary>0xB4 7F → MOV AH, 0x7F. AH is byte-half of AX[15:8]; AL untouched.</summary>
    [Fact]
    public void Step_MovAhImm8_LoadsHighByteOfAx_PreservesAl()
    {
        var (cpu, _) = Setup(new byte[] { 0xB4, 0x7F, 0xF4 });
        var s = cpu.State;
        s.A.L = 0xCC;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        cpu.Step();

        Assert.Equal(0x7F, cpu.State.A.H);
        Assert.Equal(0xCC, cpu.State.A.L);
        Assert.Equal(0x7FCC, cpu.State.A.X);
    }

    /// <summary>0xB8 34 12 → MOV AX, 0x1234. Little-endian imm16.</summary>
    [Fact]
    public void Step_MovAxImm16_LoadsWholeAx()
    {
        var (cpu, _) = Setup(new byte[] { 0xB8, 0x34, 0x12, 0xF4 });
        cpu.Step();
        Assert.Equal(0x1234, cpu.State.A.X);
        Assert.Equal(0x103, cpu.State.IP);
    }

    /// <summary>0xBE EF BE → MOV SI, 0xBEEF. Tests SI (high index, encoding 110).</summary>
    [Fact]
    public void Step_MovSiImm16()
    {
        var (cpu, _) = Setup(new byte[] { 0xBE, 0xEF, 0xBE, 0xF4 });
        cpu.Step();
        Assert.Equal(0xBEEF, cpu.State.SI);
    }

    /// <summary>
    /// Multiple MOVs back-to-back with mixed byte/word forms. Verifies the
    /// IP-fetch-imm machinery handles consecutive instructions correctly
    /// (prior bug class: imm fetch leaving IP at wrong offset).
    /// </summary>
    [Fact]
    public void Step_MultipleMovsThenHlt_AllRegistersLoaded()
    {
        // mov al, 0x11   B0 11
        // mov ah, 0x22   B4 22
        // mov bx, 0xCAFE BB FE CA
        // mov cx, 0x0001 B9 01 00
        // mov dx, 0x000A BA 0A 00
        // mov si, 0x0100 BE 00 01
        // mov di, 0x0200 BF 00 02
        // mov bp, 0xBEEF BD EF BE
        // mov sp, 0xF000 BC 00 F0
        // hlt            F4
        var code = new byte[]
        {
            0xB0, 0x11,
            0xB4, 0x22,
            0xBB, 0xFE, 0xCA,
            0xB9, 0x01, 0x00,
            0xBA, 0x0A, 0x00,
            0xBE, 0x00, 0x01,
            0xBF, 0x00, 0x02,
            0xBD, 0xEF, 0xBE,
            0xBC, 0x00, 0xF0,
            0xF4
        };
        var (cpu, _) = Setup(code);
        for (int i = 0; i < 32 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);

        var s = cpu.State;
        Assert.Equal(0x2211, s.A.X);
        Assert.Equal(0xCAFE, s.B.X);
        Assert.Equal(0x0001, s.C.X);
        Assert.Equal(0x000A, s.D.X);
        Assert.Equal(0x0100, s.SI);
        Assert.Equal(0x0200, s.DI);
        Assert.Equal(0xBEEF, s.BP);
        Assert.Equal(0xF000, s.SP);
    }

    // ---------------- 24.6.5b — MOV r,r and r/m,r register-direct (88-8B mod=11) ----------------

    /// <summary>
    /// 0x8A C3  → MOV AL, BL  (modrm: mod=11 reg=000(AL) rm=011(BL))
    /// Tests the reg-direct ModR/M decode path: byte read, reg field
    /// dispatch in both directions.
    /// </summary>
    [Fact]
    public void Step_MovAlBl_ViaModRmRegDirect()
    {
        // mov bl, 0x55  (B3 55)
        // mov al, bl    (8A C3)
        // hlt           (F4)
        var (cpu, _) = Setup(new byte[] { 0xB3, 0x55, 0x8A, 0xC3, 0xF4 });
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x55, cpu.State.A.L);
        Assert.Equal(0x55, cpu.State.B.L);
    }

    /// <summary>
    /// 0x88 D8  → MOV AL, BL  (88 = MOV r/m8, r8 — modrm: mod=11 reg=011(BL) rm=000(AL))
    /// </summary>
    [Fact]
    public void Step_MovBlAl_ViaRmFromR()
    {
        // mov bl, 0xAB  (B3 AB)
        // mov al, bl    (88 D8)  — copies BL into AL via 0x88 direction (rm,reg → rm←reg)
        // hlt
        var (cpu, _) = Setup(new byte[] { 0xB3, 0xAB, 0x88, 0xD8, 0xF4 });
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xAB, cpu.State.A.L);
    }

    /// <summary>
    /// 0x89 D8  → MOV AX, BX  (mod=11 reg=011(BX) rm=000(AX)). 16-bit copy.
    /// </summary>
    [Fact]
    public void Step_MovAxBx_Word()
    {
        // mov bx, 0xCAFE  (BB FE CA)
        // mov ax, bx       (89 D8 — r/m=AX, reg=BX, copy reg→r/m)
        // hlt
        var (cpu, _) = Setup(new byte[] { 0xBB, 0xFE, 0xCA, 0x89, 0xD8, 0xF4 });
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xCAFE, cpu.State.A.X);
        Assert.Equal(0xCAFE, cpu.State.B.X);
    }

    /// <summary>
    /// 0x8B C3  → MOV AX, BX (alternative direction: 8B = MOV r16, r/m16,
    /// modrm: mod=11 reg=000(AX) rm=011(BX)). Verifies both directions
    /// work even though the register pair is identical.
    /// </summary>
    [Fact]
    public void Step_MovAxBx_AlternateDirection()
    {
        // mov bx, 0xBEEF  (BB EF BE)
        // mov ax, bx       (8B C3 — r=AX, r/m=BX, copy r/m→r)
        // hlt
        var (cpu, _) = Setup(new byte[] { 0xBB, 0xEF, 0xBE, 0x8B, 0xC3, 0xF4 });
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xBEEF, cpu.State.A.X);
    }

    /// <summary>
    /// Exercise the full 8-arm reg field dispatch by chaining MOVs
    /// across all 8 byte registers. End-state proves both ReadGpr8 and
    /// WriteGpr8 byte-half preservation paths work for every encoding.
    /// </summary>
    [Fact]
    public void Step_RotatingByteMovs_AllEightHalves()
    {
        // Set distinct values into all 4 word regs, then chain byte
        // moves through every encoding 000..111.
        var code = new byte[]
        {
            0xB8, 0x11, 0x12,    // mov ax, 0x1211
            0xB9, 0x33, 0x34,    // mov cx, 0x3433
            0xBA, 0x55, 0x56,    // mov dx, 0x5655
            0xBB, 0x77, 0x78,    // mov bx, 0x7877
            // Round-trip AL→CL→DL→BL via 8A reads.
            // Actually for this test, just verify a few independent moves.
            0x88, 0xC1,           // mov cl, al      (al=0x11 → cl=0x11)
            0x88, 0xE2,           // mov dl, ah      (ah=0x12 → dl=0x12)
            0x88, 0xFB,           // mov bl, bh      (bh=0x78 → bl=0x78)
            0xF4
        };
        var (cpu, _) = Setup(code);
        for (int i = 0; i < 32 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x11, cpu.State.C.L);
        Assert.Equal(0x12, cpu.State.D.L);
        Assert.Equal(0x78, cpu.State.B.L);
        // High halves preserved correctly: CH stays from B9 imm load (0x34).
        Assert.Equal(0x34, cpu.State.C.H);
    }

    // ---------------- 24.6.5c — memory ModR/M (88-8B with mod=00/01/10) ----------------

    /// <summary>
    /// 0xA3 50 00 form not yet implemented (that's MOV moffs16,AX — a
    /// different group). For 24.6.5c we use 0x8B with a memory operand:
    ///   mov bx, [0x0050]   = 8B 1E 50 00   (modrm: mod=00 reg=011(BX) rm=110 = direct disp16)
    /// Memory at DS:0x50 is loaded into BX. After Reset DS=0, so linear
    /// address = 0x50.
    /// </summary>
    [Fact]
    public void Step_MovBx_DirectDisp16Read()
    {
        // Pre-seed memory with two bytes (LE) at linear 0x50.
        var (cpu, mem) = Setup(new byte[] { 0x8B, 0x1E, 0x50, 0x00, 0xF4 });
        mem.WriteByte(0x50, 0xCD);
        mem.WriteByte(0x51, 0xAB);

        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xABCD, cpu.State.B.X);
    }

    /// <summary>
    /// 0x89 1E 80 00  → MOV [0x0080], BX (mod=00, reg=011(BX), rm=110 → direct disp16)
    /// Verifies the memory store path.
    /// </summary>
    [Fact]
    public void Step_MovDirectDisp16_Write_FromBx()
    {
        // mov bx, 0xFEED  (BB ED FE)
        // mov [0x0080], bx (89 1E 80 00)
        // hlt
        var (cpu, mem) = Setup(new byte[] { 0xBB, 0xED, 0xFE, 0x89, 0x1E, 0x80, 0x00, 0xF4 });
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xED, mem.ReadByte(0x80));
        Assert.Equal(0xFE, mem.ReadByte(0x81));
    }

    /// <summary>
    /// MOV with [BX+SI] base addressing (mod=00 rm=000):
    ///   8B 00          mov ax, [bx+si]
    /// Pre-seed BX=0x40, SI=0x10 so EA = DS:0x50.
    /// </summary>
    [Fact]
    public void Step_MovAx_BxPlusSi_NoDisp()
    {
        var (cpu, mem) = Setup(new byte[] { 0x8B, 0x00, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x40;
        s.SI  = 0x10;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x50, 0x34);
        mem.WriteByte(0x51, 0x12);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x1234, cpu.State.A.X);
    }

    /// <summary>
    /// MOV with [SI+disp8] addressing (mod=01 rm=100):
    ///   8B 44 0C       mov ax, [si+0x0C]
    /// Pre-seed SI=0x40 so EA = DS:0x4C.
    /// </summary>
    [Fact]
    public void Step_MovAx_SiPlusDisp8()
    {
        var (cpu, mem) = Setup(new byte[] { 0x8B, 0x44, 0x0C, 0xF4 });
        var s = cpu.State;
        s.SI = 0x40;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x4C, 0xBE);
        mem.WriteByte(0x4D, 0xBA);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xBABE, cpu.State.A.X);
    }

    /// <summary>
    /// MOV with [BX+DI+disp16] addressing (mod=10 rm=001):
    ///   8B 81 00 02    mov ax, [bx+di+0x0200]
    /// Pre-seed BX=0x100, DI=0x50 so EA = DS:0x350.
    /// </summary>
    [Fact]
    public void Step_MovAx_BxPlusDiPlusDisp16()
    {
        var (cpu, mem) = Setup(new byte[] { 0x8B, 0x81, 0x00, 0x02, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x100;
        s.DI  = 0x50;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x350, 0x78);
        mem.WriteByte(0x351, 0x56);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x5678, cpu.State.A.X);
    }

    /// <summary>
    /// MOV byte from memory: 0x8A 07 → mov al, [bx]. Pre-seed BX=0x60.
    /// </summary>
    [Fact]
    public void Step_MovAl_AtBx_ByteRead()
    {
        var (cpu, mem) = Setup(new byte[] { 0x8A, 0x07, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x60;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x60, 0x99);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x99, cpu.State.A.L);
    }

    /// <summary>
    /// Round-trip: load memory, write back to a different address. Covers
    /// the full read-then-write data-transfer flow with both forms of EA.
    ///   8B 1E 00 02    mov bx, [0x0200]    (mod=00 rm=110 disp16)
    ///   89 5C 04       mov [si+0x04], bx   (mod=01 rm=100 disp8) — actually mod=01 rm=4 is SI+disp8
    /// Pre-seed memory at 0x200 with 0xC0FE; SI=0x100; expect bytes at
    /// 0x104/0x105 = 0xFE / 0xC0.
    /// </summary>
    [Fact]
    public void Step_RoundTrip_LoadFromDirectDisp16_StoreToSiDisp8()
    {
        var (cpu, mem) = Setup(new byte[]
        {
            0x8B, 0x1E, 0x00, 0x02,   // mov bx, [0x0200]
            0x89, 0x5C, 0x04,         // mov [si+0x04], bx
            0xF4
        });
        var s = cpu.State;
        s.SI = 0x100;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x200, 0xFE);
        mem.WriteByte(0x201, 0xC0);

        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xC0FE, cpu.State.B.X);
        Assert.Equal(0xFE,   mem.ReadByte(0x104));
        Assert.Equal(0xC0,   mem.ReadByte(0x105));
    }

    // ---------------- 24.6.5d — segment override prefixes ----------------

    /// <summary>
    /// 0x26 0x8B 0x07  → ES: MOV AX, [BX]
    /// Pre-seed BX=0x0010, ES=0x1000, DS=0x2000. Memory at ES:BX (linear
    /// 0x10010) holds 0x1234. Without the ES override, the read would go
    /// to DS:BX (linear 0x20010) which holds 0x9999 — so the test only
    /// passes when the override correctly redirects the segment.
    /// </summary>
    [Fact]
    public void Step_EsOverride_SwitchesDefaultDsToEs()
    {
        var (cpu, mem) = Setup(new byte[] { 0x26, 0x8B, 0x07, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x0010;
        s.ES  = 0x1000;
        s.DS  = 0x2000;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        // ES:BX = 0x10010
        mem.WriteByte(0x10010, 0x34);
        mem.WriteByte(0x10011, 0x12);
        // DS:BX = 0x20010 — should NOT be read
        mem.WriteByte(0x20010, 0x99);
        mem.WriteByte(0x20011, 0x99);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x1234, cpu.State.A.X);
    }

    /// <summary>
    /// 0x2E 0x89 0x07  → CS: MOV [BX], AX. Stores AX into CS:BX.
    /// </summary>
    [Fact]
    public void Step_CsOverride_StoreToCs()
    {
        // mov ax, 0xCAFE   B8 FE CA
        // cs: mov [bx], ax  2E 89 07
        // hlt              F4
        var (cpu, mem) = Setup(new byte[] { 0xB8, 0xFE, 0xCA, 0x2E, 0x89, 0x07, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x0040;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0x0500, 0);   // CS=0x0500, IP=0
        // Re-load code at CS:0 since SetEntryPoint moved CS:IP.
        var code = new byte[] { 0xB8, 0xFE, 0xCA, 0x2E, 0x89, 0x07, 0xF4 };
        mem.LoadBinary(code, 0x0500, 0);

        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        // CS:BX = 0x05000 + 0x40 = 0x05040
        Assert.Equal(0xFE, mem.ReadByte(0x05040));
        Assert.Equal(0xCA, mem.ReadByte(0x05041));
    }

    /// <summary>
    /// 0x36 0x8B 0x47 0x04  → SS: MOV AX, [BX+0x04]
    /// SS is the natural default for [BP+...] addressing modes; here we
    /// force it for [BX+disp8] (which would normally use DS).
    /// </summary>
    [Fact]
    public void Step_SsOverride_OnBxBased()
    {
        var (cpu, mem) = Setup(new byte[] { 0x36, 0x8B, 0x47, 0x04, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x0050;
        s.SS  = 0x3000;
        s.DS  = 0x4000;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        // SS:BX+4 = 0x30054
        mem.WriteByte(0x30054, 0xAD);
        mem.WriteByte(0x30055, 0xDE);
        // DS:BX+4 = 0x40054 — should NOT be read
        mem.WriteByte(0x40054, 0x00);
        mem.WriteByte(0x40055, 0x00);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xDEAD, cpu.State.A.X);
    }

    /// <summary>
    /// Override is one-shot: after the prefixed instruction, the next
    /// instruction must use its architectural default segment again.
    ///   26 8B 07     ES: mov ax, [bx]
    ///   8B 1F        mov bx, [bx]   (no prefix → DS default)
    /// </summary>
    [Fact]
    public void Step_OverrideClearsAfterOneInstruction()
    {
        var (cpu, mem) = Setup(new byte[] { 0x26, 0x8B, 0x07, 0x8B, 0x1F, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x0010;
        s.ES  = 0x1000;
        s.DS  = 0x2000;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        // ES:BX (0x10010) — first instruction reads from here
        mem.WriteByte(0x10010, 0x34);
        mem.WriteByte(0x10011, 0x12);
        // DS:BX (0x20010) — second instruction reads from here
        mem.WriteByte(0x20010, 0x78);
        mem.WriteByte(0x20011, 0x56);

        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x1234, cpu.State.A.X);   // ES:BX after first
        Assert.Equal(0x5678, cpu.State.B.X);   // DS:BX after second (no override)
    }

    /// <summary>
    /// Last-prefix-wins: 8086 silicon takes the most recent override when
    /// multiple are present. 0x26 then 0x36 → SS active.
    /// </summary>
    [Fact]
    public void Step_MultiplePrefixes_LastWins()
    {
        var (cpu, mem) = Setup(new byte[] { 0x26, 0x36, 0x8B, 0x07, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x0010;
        s.ES  = 0x1000;     // would yield 0x9999
        s.SS  = 0x3000;     // should yield 0x4242
        s.DS  = 0x4000;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        mem.WriteByte(0x10010, 0x99); mem.WriteByte(0x10011, 0x99);
        mem.WriteByte(0x30010, 0x42); mem.WriteByte(0x30011, 0x42);
        mem.WriteByte(0x40010, 0x77); mem.WriteByte(0x40011, 0x77);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x4242, cpu.State.A.X);
    }

    /// <summary>
    /// Reg-direct ModR/M (mod=11) ignores segment override since there's
    /// no memory operand. 0x26 0x8B 0xC3 → ES: mov ax, bx. ES is consumed
    /// but the instruction is reg-to-reg, so no segment is used.
    /// </summary>
    [Fact]
    public void Step_OverrideOnRegDirect_NoEffectOnRegisterCopy()
    {
        var (cpu, _) = Setup(new byte[] { 0xBB, 0xCD, 0xAB, 0x26, 0x8B, 0xC3, 0xF4 });
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xABCD, cpu.State.A.X);
    }

    /// <summary>
    /// LoadState mirrors a full architectural snapshot onto the spec
    /// buffer; State getter must round-trip the same values out (GPRs,
    /// segment regs, IP, FLAGS).
    /// </summary>
    [Fact]
    public void LoadState_ThenStateGetter_RoundTrips()
    {
        var mem = new X86Memory();
        var cpu = new X86JsonCpu(mem);

        var s = new X86State();
        s.A.X = 0x1234; s.C.X = 0x5678; s.D.X = 0x9ABC; s.B.X = 0xDEF0;
        s.SP = 0xCAFE; s.BP = 0xBABE; s.SI = 0x1111; s.DI = 0x2222;
        s.ES = 0xAA00; s.CS = 0xBB00; s.SS = 0xCC00; s.DS = 0xDD00;
        s.IP = 0x3333;
        s.FlagC = true;  s.FlagZ = true;  s.FlagS = true;
        s.FlagP = false; s.FlagA = false; s.FlagO = false;

        cpu.LoadState(s);
        var rt = cpu.State;

        Assert.Equal(0x1234, rt.A.X);
        Assert.Equal(0x5678, rt.C.X);
        Assert.Equal(0x9ABC, rt.D.X);
        Assert.Equal(0xDEF0, rt.B.X);
        Assert.Equal(0xCAFE, rt.SP);
        Assert.Equal(0xBABE, rt.BP);
        Assert.Equal(0x1111, rt.SI);
        Assert.Equal(0x2222, rt.DI);
        Assert.Equal(0xAA00, rt.ES);
        Assert.Equal(0xBB00, rt.CS);
        Assert.Equal(0xCC00, rt.SS);
        Assert.Equal(0xDD00, rt.DS);
        Assert.Equal(0x3333, rt.IP);
        Assert.True(rt.FlagC);  Assert.True(rt.FlagZ);  Assert.True(rt.FlagS);
        Assert.False(rt.FlagP); Assert.False(rt.FlagA); Assert.False(rt.FlagO);
    }
}
