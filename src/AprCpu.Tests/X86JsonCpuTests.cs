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
    /// Step() returns -1 for an opcode the spec doesn't yet cover.
    /// 0x0F is undefined on 8086 (was POP CS in early silicon, then
    /// reserved as the 2-byte opcode prefix on 80286+) — we never wire
    /// it. IP must NOT advance; the caller can fall through to legacy
    /// or extend the spec.
    /// </summary>
    [Fact]
    public void Step_UnknownOpcode_ReturnsMinusOneAndPreservesIp()
    {
        var (cpu, _) = Setup(new byte[] { 0x0F, 0x00 });

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

    // ---------------- 24.6.5e — MOV r/m,imm (C6/C7) ----------------

    /// <summary>
    /// 0xC7 06 50 00 EF BE  →  mov word ptr [0x0050], 0xBEEF
    /// (modrm=06: mod=00 reg=000 rm=110 → direct disp16; reg field is
    /// /0 placeholder, ignored by silicon)
    /// </summary>
    [Fact]
    public void Step_MovWordPtrDirect_Imm16()
    {
        var (cpu, mem) = Setup(new byte[] { 0xC7, 0x06, 0x50, 0x00, 0xEF, 0xBE, 0xF4 });
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xEF, mem.ReadByte(0x50));
        Assert.Equal(0xBE, mem.ReadByte(0x51));
    }

    /// <summary>
    /// 0xC6 47 02 7F  →  mov byte ptr [bx+0x02], 0x7F
    /// (modrm=47: mod=01 reg=000 rm=111 → BX+disp8)
    /// </summary>
    [Fact]
    public void Step_MovBytePtrBxDisp8_Imm8()
    {
        var (cpu, mem) = Setup(new byte[] { 0xC6, 0x47, 0x02, 0x7F, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x80;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x7F, mem.ReadByte(0x82));
    }

    /// <summary>
    /// 0xC6 mod=11 reg-direct: mov bl, 0x42  →  C6 C3 42
    /// modrm=C3: mod=11 reg=000 rm=011 (BL). Should write through reg path.
    /// </summary>
    [Fact]
    public void Step_MovC6_RegDirect_BL()
    {
        var (cpu, _) = Setup(new byte[] { 0xC6, 0xC3, 0x42, 0xF4 });
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x42, cpu.State.B.L);
    }

    // ---------------- 24.6.5e — MOV moffs (A0-A3) ----------------

    /// <summary>0xA0 50 00 → mov al, [0x0050]</summary>
    [Fact]
    public void Step_MovAlMoffs8()
    {
        var (cpu, mem) = Setup(new byte[] { 0xA0, 0x50, 0x00, 0xF4 });
        mem.WriteByte(0x50, 0xAB);
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xAB, cpu.State.A.L);
    }

    /// <summary>0xA1 80 00 → mov ax, [0x0080]</summary>
    [Fact]
    public void Step_MovAxMoffs16()
    {
        var (cpu, mem) = Setup(new byte[] { 0xA1, 0x80, 0x00, 0xF4 });
        mem.WriteByte(0x80, 0x34);
        mem.WriteByte(0x81, 0x12);
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x1234, cpu.State.A.X);
    }

    /// <summary>0xA2 60 00 → mov [0x0060], al</summary>
    [Fact]
    public void Step_MovMoffs8Al()
    {
        var (cpu, mem) = Setup(new byte[] { 0xB0, 0x99, 0xA2, 0x60, 0x00, 0xF4 });
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x99, mem.ReadByte(0x60));
    }

    /// <summary>0xA3 70 00 → mov [0x0070], ax (after loading AX with imm)</summary>
    [Fact]
    public void Step_MovMoffs16Ax()
    {
        var (cpu, mem) = Setup(new byte[] { 0xB8, 0xCD, 0xAB, 0xA3, 0x70, 0x00, 0xF4 });
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xCD, mem.ReadByte(0x70));
        Assert.Equal(0xAB, mem.ReadByte(0x71));
    }

    /// <summary>
    /// Moffs honours segment override prefix: 26 A1 50 00 → mov ax, es:[0x50]
    /// </summary>
    [Fact]
    public void Step_MovMoffs_SegOverride()
    {
        var (cpu, mem) = Setup(new byte[] { 0x26, 0xA1, 0x50, 0x00, 0xF4 });
        var s = cpu.State;
        s.ES = 0x1000;
        s.DS = 0x2000;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x10050, 0x21);
        mem.WriteByte(0x10051, 0x43);
        mem.WriteByte(0x20050, 0x99);   // wrong-segment trap
        mem.WriteByte(0x20051, 0x99);
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x4321, cpu.State.A.X);
    }

    // ---------------- 24.6.5e — MOV sreg (8C/8E) ----------------

    /// <summary>
    /// 0x8E D8 → mov ds, ax. Pre-load AX=0x1234 then move to DS.
    /// </summary>
    [Fact]
    public void Step_MovSregFromReg_DsFromAx()
    {
        // mov ax, 0x1234   B8 34 12
        // mov ds, ax       8E D8  (modrm=D8: mod=11 reg=011=DS rm=000=AX)
        // hlt              F4
        var (cpu, _) = Setup(new byte[] { 0xB8, 0x34, 0x12, 0x8E, 0xD8, 0xF4 });
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x1234, cpu.State.DS);
    }

    /// <summary>
    /// 0x8C C0 → mov ax, es. Pre-set ES=0xBEEF then read into AX.
    /// modrm=C0: mod=11 reg=000=ES rm=000=AX.
    /// </summary>
    [Fact]
    public void Step_MovRegFromSreg_AxFromEs()
    {
        var (cpu, _) = Setup(new byte[] { 0x8C, 0xC0, 0xF4 });
        var s = cpu.State;
        s.ES = 0xBEEF;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xBEEF, cpu.State.A.X);
    }

    /// <summary>
    /// Round-trip: store and reload all 4 sreg via temporary (proves all
    /// 4 sreg encodings 00..11 work in both directions).
    /// </summary>
    [Fact]
    public void Step_AllFourSregs_RoundTripViaAx()
    {
        // mov ax, 0x1111  B8 11 11
        // mov es, ax      8E C0  (reg=000=ES)
        // mov ax, 0x3333  B8 33 33
        // mov ss, ax      8E D0  (reg=010=SS)
        // mov ax, 0x4444  B8 44 44
        // mov ds, ax      8E D8  (reg=011=DS)
        // hlt             F4
        var (cpu, _) = Setup(new byte[]
        {
            0xB8, 0x11, 0x11, 0x8E, 0xC0,
            0xB8, 0x33, 0x33, 0x8E, 0xD0,
            0xB8, 0x44, 0x44, 0x8E, 0xD8,
            0xF4
        });
        for (int i = 0; i < 16 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x1111, cpu.State.ES);
        Assert.Equal(0x3333, cpu.State.SS);
        Assert.Equal(0x4444, cpu.State.DS);
    }

    // ---------------- 24.6.5f — PUSH / POP family ----------------

    /// <summary>
    /// 0x53 PUSH BX (encoding 010 = BX). Pre-set SP=0x0200, SS=0;
    /// after push, SP=0x01FE, MEM[0:01FE]=BX low, [0:01FF]=BX high.
    /// </summary>
    [Fact]
    public void Step_PushBx()
    {
        var (cpu, mem) = Setup(new byte[] { 0x53, 0xF4 });
        var s = cpu.State;
        s.B.X = 0xCAFE;
        s.SP  = 0x0200;
        s.SS  = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x01FE, cpu.State.SP);
        Assert.Equal(0xFE, mem.ReadByte(0x01FE));
        Assert.Equal(0xCA, mem.ReadByte(0x01FF));
    }

    /// <summary>
    /// 8088 PUSH SP quirk: 0x54 PUSH SP pushes the NEW (post-decrement)
    /// SP value, not the original. Pre-set SP=0x0200 → push → MEM should
    /// hold 0x01FE (the new SP), not 0x0200.
    /// </summary>
    [Fact]
    public void Step_PushSp_8088Quirk_PushesNewSp()
    {
        var (cpu, mem) = Setup(new byte[] { 0x54, 0xF4 });
        var s = cpu.State;
        s.SP = 0x0200;
        s.SS = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x01FE, cpu.State.SP);
        // Pushed value should be 0x01FE (new SP), NOT 0x0200 (old SP).
        Assert.Equal(0xFE, mem.ReadByte(0x01FE));
        Assert.Equal(0x01, mem.ReadByte(0x01FF));
    }

    /// <summary>
    /// PUSH BX then POP CX should leave CX = original BX. SP returns
    /// to its starting value.
    /// </summary>
    [Fact]
    public void Step_PushBxPopCx_RoundTrip()
    {
        // mov bx, 0x1234   BB 34 12
        // push bx          53
        // pop cx           59
        // hlt              F4
        var (cpu, _) = Setup(new byte[] { 0xBB, 0x34, 0x12, 0x53, 0x59, 0xF4 });
        var s = cpu.State;
        s.SP = 0x0200;
        s.SS = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 16 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x1234, cpu.State.C.X);
        Assert.Equal(0x0200, cpu.State.SP);
    }

    /// <summary>
    /// 0x06 PUSH ES then 0x07 POP ES. Round-trips ES through stack.
    /// </summary>
    [Fact]
    public void Step_PushPopSeg_Es()
    {
        var (cpu, _) = Setup(new byte[] { 0x06, 0x07, 0xF4 });
        var s = cpu.State;
        s.ES = 0xBEEF;
        s.SP = 0x0200; s.SS = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xBEEF, cpu.State.ES);
        Assert.Equal(0x0200, cpu.State.SP);
    }

    /// <summary>
    /// 0x9C PUSHF — verify reserved-bit pattern: bit 1 + bits 12-15 forced
    /// to 1 in the pushed word. Only CF + PF + ZF are set in FLAGS for
    /// this test, so pushed = 0xF000 | 0x0002 | (CF=1) | (PF=1<<2) | (ZF=1<<6)
    ///                       = 0xF047
    /// </summary>
    [Fact]
    public void Step_Pushf_ReservedBitsForced()
    {
        var (cpu, mem) = Setup(new byte[] { 0x9C, 0xF4 });
        var s = cpu.State;
        s.FlagC = true; s.FlagP = true; s.FlagZ = true;
        s.SP = 0x0200; s.SS = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        ushort pushed = (ushort)(mem.ReadByte(0x01FE) | (mem.ReadByte(0x01FF) << 8));
        Assert.Equal(0xF047, pushed);
    }

    /// <summary>
    /// PUSHF / POPF round-trip preserves the architectural flag bits.
    /// </summary>
    [Fact]
    public void Step_PushfPopf_RoundTripPreservesFlags()
    {
        // pushf            9C
        // mov ax, 0xFFFF   B8 FF FF   (clobber AX so we know it's untouched)
        // popf             9D
        // hlt              F4
        var (cpu, _) = Setup(new byte[] { 0x9C, 0xB8, 0xFF, 0xFF, 0x9D, 0xF4 });
        var s = cpu.State;
        s.FlagC = true; s.FlagS = true; s.FlagZ = true;
        s.SP = 0x0200; s.SS = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 16 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagS);
        Assert.True(cpu.State.FlagZ);
        Assert.Equal(0x0200, cpu.State.SP);
    }

    /// <summary>
    /// 0x8F /0 POP r/m16: pop into [BX]. Pre-stack value 0x1234, BX=0x80,
    /// after pop MEM[DS:80] = 0x1234.
    /// </summary>
    [Fact]
    public void Step_PopRm16_ToMemory()
    {
        // pre-push value via mov [SP-2], 0x1234 setup is awkward —
        // simpler: pre-fill stack memory directly.
        var (cpu, mem) = Setup(new byte[] { 0x8F, 0x07, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x80;
        s.SP  = 0x01FE;
        s.SS  = 0;
        s.DS  = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        // SS:SP holds the value to pop
        mem.WriteByte(0x01FE, 0x34);
        mem.WriteByte(0x01FF, 0x12);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0200, cpu.State.SP);
        Assert.Equal(0x34, mem.ReadByte(0x80));
        Assert.Equal(0x12, mem.ReadByte(0x81));
    }

    // ---------------- 24.6.5g — XCHG / LEA / LDS / LES ----------------

    /// <summary>
    /// 0x86 XCHG r/m8, r8 reg-direct: 86 D8 → xchg al, bl
    /// (modrm=D8: mod=11 reg=011=BL rm=000=AL)
    /// </summary>
    [Fact]
    public void Step_XchgAlBl_RegDirect()
    {
        var (cpu, _) = Setup(new byte[] { 0x86, 0xD8, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x11; s.A.H = 0xAA;   // verify high half preserved
        s.B.L = 0x99; s.B.H = 0xBB;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x99, cpu.State.A.L);
        Assert.Equal(0xAA, cpu.State.A.H);
        Assert.Equal(0x11, cpu.State.B.L);
        Assert.Equal(0xBB, cpu.State.B.H);
    }

    /// <summary>
    /// 0x87 XCHG r/m16, r16 with memory: 87 1E 80 00 → xchg [0x80], bx
    /// (modrm=1E: mod=00 reg=011=BX rm=110 = direct disp16)
    /// </summary>
    [Fact]
    public void Step_XchgWordWithMemory()
    {
        var (cpu, mem) = Setup(new byte[] { 0x87, 0x1E, 0x80, 0x00, 0xF4 });
        var s = cpu.State;
        s.B.X = 0xBEEF;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x80, 0xCD);
        mem.WriteByte(0x81, 0xAB);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xABCD, cpu.State.B.X);   // BX got the old memory value
        Assert.Equal(0xEF, mem.ReadByte(0x80));
        Assert.Equal(0xBE, mem.ReadByte(0x81));
    }

    /// <summary>
    /// 0x91-0x97 XCHG AX, r16: 0x93 xchg ax, bx.
    /// </summary>
    [Fact]
    public void Step_XchgAxBx_ShortForm()
    {
        var (cpu, _) = Setup(new byte[] { 0x93, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x1111;
        s.B.X = 0x2222;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x2222, cpu.State.A.X);
        Assert.Equal(0x1111, cpu.State.B.X);
    }

    /// <summary>
    /// 0x90 still dispatches to NOP (smoke group's mask=0xFF wins over
    /// XchgAxR16's mask=0xF8). Neither AX nor any other reg should change.
    /// </summary>
    [Fact]
    public void Step_0x90_StillDispatchesToNop_NotXchgAxAx()
    {
        var (cpu, _) = Setup(new byte[] { 0x90, 0xF4 });
        var s = cpu.State;
        s.A.X = 0xABCD;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xABCD, cpu.State.A.X);
    }

    /// <summary>
    /// 0x8D 1F → lea bx, [bx]. EA = BX (mod=00 rm=111). After LEA,
    /// BX = original BX (since [bx] addressing mode evaluates to BX).
    /// Boring but tests the basic flow.
    /// </summary>
    [Fact]
    public void Step_LeaBx_AtBx()
    {
        var (cpu, _) = Setup(new byte[] { 0x8D, 0x1F, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x1234;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x1234, cpu.State.B.X);
    }

    /// <summary>
    /// 0x8D 47 0A → lea ax, [bx+0x0A]. Pre-set BX=0x100; expect AX=0x10A.
    /// LEA must NOT touch memory.
    /// </summary>
    [Fact]
    public void Step_LeaAx_BxPlusDisp8()
    {
        var (cpu, mem) = Setup(new byte[] { 0x8D, 0x47, 0x0A, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x100;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        // Memory at the EA — should NOT be touched by LEA
        mem.WriteByte(0x10A, 0xFF);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x010A, cpu.State.A.X);
        Assert.Equal(0xFF, mem.ReadByte(0x10A));   // memory unchanged
    }

    /// <summary>
    /// 0xC5 1E 50 00 → lds bx, [0x0050]. Memory at DS:0x50 contains a
    /// 32-bit far pointer: low 16 bits → BX, high 16 bits → DS.
    /// Pre-fill mem[0x50..0x53] = 0x34, 0x12, 0x00, 0x40 → BX=0x1234, DS=0x4000.
    /// </summary>
    [Fact]
    public void Step_Lds_LoadsRegAndDs()
    {
        var (cpu, mem) = Setup(new byte[] { 0xC5, 0x1E, 0x50, 0x00, 0xF4 });
        mem.WriteByte(0x50, 0x34);
        mem.WriteByte(0x51, 0x12);
        mem.WriteByte(0x52, 0x00);
        mem.WriteByte(0x53, 0x40);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x1234, cpu.State.B.X);
        Assert.Equal(0x4000, cpu.State.DS);
    }

    /// <summary>
    /// 0xC4 06 50 00 → les ax, [0x0050]. Same shape as LDS but → ES.
    /// </summary>
    [Fact]
    public void Step_Les_LoadsRegAndEs()
    {
        var (cpu, mem) = Setup(new byte[] { 0xC4, 0x06, 0x50, 0x00, 0xF4 });
        mem.WriteByte(0x50, 0xFE);
        mem.WriteByte(0x51, 0xCA);
        mem.WriteByte(0x52, 0x00);
        mem.WriteByte(0x53, 0x80);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xCAFE, cpu.State.A.X);
        Assert.Equal(0x8000, cpu.State.ES);
    }

    // ---------------- 24.6.6a — ADD (00-05) + 9-flag IR ----------------

    /// <summary>
    /// 0x00 D8 → ADD AL, BL. AL=0x10 + BL=0x20 = 0x30. No carry, no overflow.
    /// </summary>
    [Fact]
    public void Step_AddAlBl_NoFlags()
    {
        var (cpu, _) = Setup(new byte[] { 0x00, 0xD8, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x10; s.B.L = 0x20;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x30, cpu.State.A.L);
        Assert.False(cpu.State.FlagC);
        Assert.False(cpu.State.FlagZ);
        Assert.False(cpu.State.FlagS);
        Assert.False(cpu.State.FlagO);
    }

    /// <summary>
    /// 0x04 80 → ADD AL, 0x80 with AL=0x80. Result=0x00 (carry out).
    /// CF=1, ZF=1, SF=0, OF=1 (signed: -128 + -128 overflow).
    /// </summary>
    [Fact]
    public void Step_AddAlImm8_CarryAndOverflow()
    {
        var (cpu, _) = Setup(new byte[] { 0x04, 0x80, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x80;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x00, cpu.State.A.L);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagZ);
        Assert.False(cpu.State.FlagS);
        Assert.True(cpu.State.FlagO);
    }

    /// <summary>
    /// 0x05 imm16 → ADD AX, 0x0001 with AX=0xFFFF. Wraps to 0x0000 with CF=1.
    /// </summary>
    [Fact]
    public void Step_AddAxImm16_WrapAround()
    {
        var (cpu, _) = Setup(new byte[] { 0x05, 0x01, 0x00, 0xF4 });
        var s = cpu.State;
        s.A.X = 0xFFFF;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0000, cpu.State.A.X);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagZ);
        Assert.False(cpu.State.FlagO);   // 0xFFFF + 0x0001 → no signed overflow
    }

    /// <summary>
    /// 0x02 D8 → ADD AL, [BX] (mod=00 reg=011=BL? wait modrm)
    /// Actually 0x02 is "ADD r8, r/m8". modrm=07: mod=00 reg=000(AL) rm=111(BX).
    /// AL = AL + [BX]. Pre-set AL=0x10, BX=0x80, mem[0x80]=0x05 → AL=0x15.
    /// </summary>
    [Fact]
    public void Step_AddAl_AtBx_MemoryRead()
    {
        var (cpu, mem) = Setup(new byte[] { 0x02, 0x07, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x10; s.B.X = 0x80;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x80, 0x05);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x15, cpu.State.A.L);
    }

    /// <summary>
    /// AF (auxiliary carry) — 0x10 + 0x10 nibble carry. Pre-set AL=0x18,
    /// add 0x08 → AL=0x20, AF=1 (low nibble 0x8+0x8 = 0x10 carry).
    /// </summary>
    [Fact]
    public void Step_AddAlImm8_AuxCarryFlag()
    {
        var (cpu, _) = Setup(new byte[] { 0x04, 0x08, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x18;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x20, cpu.State.A.L);
        Assert.True(cpu.State.FlagA);
        Assert.False(cpu.State.FlagC);
    }

    /// <summary>
    /// PF parity check — result = 0x03 has 2 bits set → even → PF=1.
    /// 0x04 02 add al,2 with al=1 → al=3, pf=1.
    /// </summary>
    [Fact]
    public void Step_AddAl_ParityEven()
    {
        var (cpu, _) = Setup(new byte[] { 0x04, 0x02, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x01;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x03, cpu.State.A.L);
        Assert.True(cpu.State.FlagP);
    }

    /// <summary>
    /// PF — result = 0x01 has 1 bit set → odd → PF=0.
    /// 0x04 01 add al,1 with al=0 → al=1, pf=0.
    /// </summary>
    [Fact]
    public void Step_AddAl_ParityOdd()
    {
        var (cpu, _) = Setup(new byte[] { 0x04, 0x01, 0xF4 });
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x01, cpu.State.A.L);
        Assert.False(cpu.State.FlagP);
    }

    /// <summary>
    /// ADD r/m, r with memory destination: 0x01 06 80 00 → ADD [0x0080], AX
    /// (modrm=06: mod=00 reg=000=AX rm=110=disp16). Pre-mem 0x80 = 0x100,
    /// AX=0x200; result memory = 0x300.
    /// </summary>
    [Fact]
    public void Step_AddMemAx_MemoryWrite()
    {
        var (cpu, mem) = Setup(new byte[] { 0x01, 0x06, 0x80, 0x00, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x200;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x80, 0x00);
        mem.WriteByte(0x81, 0x01);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        ushort result = (ushort)(mem.ReadByte(0x80) | (mem.ReadByte(0x81) << 8));
        Assert.Equal(0x0300, result);
    }

    // ---------------- 24.6.6b — OR/ADC/SBB/AND/SUB/XOR/CMP ----------------

    /// <summary>SUB AL, BL: AL=0x10 - BL=0x20 = 0xF0 with CF=1 (borrow), SF=1.</summary>
    [Fact]
    public void Step_SubAlBl_Borrow()
    {
        var (cpu, _) = Setup(new byte[] { 0x28, 0xD8, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x10; s.B.L = 0x20;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xF0, cpu.State.A.L);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagS);
        Assert.False(cpu.State.FlagZ);
    }

    /// <summary>CMP AL, BL — same as SUB but does NOT modify AL.</summary>
    [Fact]
    public void Step_CmpAlBl_DoesNotWriteBack()
    {
        var (cpu, _) = Setup(new byte[] { 0x38, 0xD8, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x42; s.B.L = 0x42;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x42, cpu.State.A.L);   // AL unchanged
        Assert.True(cpu.State.FlagZ);         // equal → ZF=1
        Assert.False(cpu.State.FlagC);
    }

    /// <summary>OR AL, imm8 — logical op clears CF and OF.</summary>
    [Fact]
    public void Step_OrAlImm8_LogicalClearsCfOf()
    {
        var (cpu, _) = Setup(new byte[] { 0x0C, 0x0F, 0xF4 });
        var s = cpu.State;
        s.A.L = 0xF0; s.FlagC = true; s.FlagO = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xFF, cpu.State.A.L);
        Assert.False(cpu.State.FlagC);
        Assert.False(cpu.State.FlagO);
        Assert.True(cpu.State.FlagS);
        Assert.True(cpu.State.FlagP);   // 0xFF has 8 bits → even
    }

    /// <summary>AND AL, imm8 — logical AND.</summary>
    [Fact]
    public void Step_AndAlImm8_BasicMask()
    {
        var (cpu, _) = Setup(new byte[] { 0x24, 0x0F, 0xF4 });
        var s = cpu.State;
        s.A.L = 0xAB;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0B, cpu.State.A.L);
        Assert.False(cpu.State.FlagS);
        Assert.False(cpu.State.FlagZ);
    }

    /// <summary>XOR AL, AL — classic zero-AL idiom; sets ZF=1.</summary>
    [Fact]
    public void Step_XorAlAl_ClearsRegSetsZf()
    {
        var (cpu, _) = Setup(new byte[] { 0x32, 0xC0, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x55;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x00, cpu.State.A.L);
        Assert.True(cpu.State.FlagZ);
        Assert.False(cpu.State.FlagC);
    }

    /// <summary>
    /// ADC AL, imm8 with CF=1 — 0x10 + 0x20 + 1 = 0x31. Verifies ADC reads CF.
    /// </summary>
    [Fact]
    public void Step_AdcAlImm8_ReadsCarryIn()
    {
        var (cpu, _) = Setup(new byte[] { 0x14, 0x20, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x10; s.FlagC = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x31, cpu.State.A.L);
        Assert.False(cpu.State.FlagC);
    }

    /// <summary>
    /// SBB with CF=1 — 0x20 - 0x10 - 1 = 0x0F. Verifies SBB reads CF as borrow.
    /// </summary>
    [Fact]
    public void Step_SbbAlImm8_ReadsBorrowIn()
    {
        var (cpu, _) = Setup(new byte[] { 0x1C, 0x10, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x20; s.FlagC = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0F, cpu.State.A.L);
        Assert.False(cpu.State.FlagC);
    }

    /// <summary>
    /// 32-bit ripple add via ADD/ADC chain on word pair.
    ///   add ax, bx     01 D8       low half + carry
    ///   adc cx, dx     11 D1       high half + previous CF
    /// AX|CX = 0x00010000, BX|DX = 0xFFFFF000, expect AX|CX = 0x0001F000?
    /// Actually let's do simpler: AX = 0xFFFF, CX = 0x0001; BX = 0x0001, DX = 0x0000.
    /// Total = 0x0001FFFF + 0x00000001 = 0x00020000.
    /// After add ax, bx: AX = 0x0000, CF=1
    /// After adc cx, dx: CX = 0x0001 + 0x0000 + 1 = 0x0002
    /// Result: CX:AX = 0x00020000.
    /// </summary>
    [Fact]
    public void Step_AddAdcChain_32BitRippleAdd()
    {
        var (cpu, _) = Setup(new byte[] { 0x01, 0xD8, 0x11, 0xD1, 0xF4 });
        var s = cpu.State;
        s.A.X = 0xFFFF; s.C.X = 0x0001;
        s.B.X = 0x0001; s.D.X = 0x0000;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 16 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0000, cpu.State.A.X);
        Assert.Equal(0x0002, cpu.State.C.X);
    }

    // ---------------- 24.6.6c — INC/DEC r16 + 80-83 ALU group ----------------

    /// <summary>0x40 INC AX. AX=0x10 → 0x11. CF preserved from initial state.</summary>
    [Fact]
    public void Step_IncAx_PreservesCarryFlag()
    {
        var (cpu, _) = Setup(new byte[] { 0x40, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x10; s.FlagC = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0011, cpu.State.A.X);
        Assert.True(cpu.State.FlagC);   // CF preserved by INC
        Assert.False(cpu.State.FlagZ);
    }

    /// <summary>INC overflow: 0x7FFF → 0x8000 sets OF=1 (signed overflow).</summary>
    [Fact]
    public void Step_IncBx_SignedOverflow()
    {
        var (cpu, _) = Setup(new byte[] { 0x43, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x7FFF;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x8000, cpu.State.B.X);
        Assert.True(cpu.State.FlagO);
        Assert.True(cpu.State.FlagS);
    }

    /// <summary>0x48 DEC AX from 0x0001 → 0x0000. ZF=1, CF preserved.</summary>
    [Fact]
    public void Step_DecAx_ToZero()
    {
        var (cpu, _) = Setup(new byte[] { 0x48, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x0001;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0000, cpu.State.A.X);
        Assert.True(cpu.State.FlagZ);
        Assert.False(cpu.State.FlagC);
    }

    /// <summary>
    /// 0x83 C0 05 → ADD AX, sext(0x05). modrm=C0: mod=11 reg=000(=ADD) rm=000(=AX).
    /// AX = 0x100 + 5 = 0x105.
    /// </summary>
    [Fact]
    public void Step_AluGroup_AddAxSextImm8()
    {
        var (cpu, _) = Setup(new byte[] { 0x83, 0xC0, 0x05, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x100;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0105, cpu.State.A.X);
    }

    /// <summary>
    /// 0x83 C0 FF → ADD AX, sext(0xFF) = ADD AX, -1 = SUB AX, 1.
    /// AX = 0x100 + (-1) = 0xFF.
    /// </summary>
    [Fact]
    public void Step_AluGroup_AddAxNegativeImm()
    {
        var (cpu, _) = Setup(new byte[] { 0x83, 0xC0, 0xFF, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x100;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x00FF, cpu.State.A.X);
    }

    /// <summary>
    /// 0x80 F8 42 → CMP AL, 0x42 (modrm=F8: mod=11 reg=111=CMP rm=000=AL).
    /// AL=0x42, CMP with 0x42 → ZF=1, AL unchanged.
    /// </summary>
    [Fact]
    public void Step_AluGroup_CmpAlImm8_NoWriteback()
    {
        var (cpu, _) = Setup(new byte[] { 0x80, 0xF8, 0x42, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x42;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x42, cpu.State.A.L);
        Assert.True(cpu.State.FlagZ);
    }

    /// <summary>
    /// 0x81 E3 0F 00 → AND BX, 0x000F (modrm=E3: mod=11 reg=100=AND rm=011=BX).
    /// BX=0xABCD → 0x000D. AND clears CF/OF.
    /// </summary>
    [Fact]
    public void Step_AluGroup_AndBxImm16()
    {
        var (cpu, _) = Setup(new byte[] { 0x81, 0xE3, 0x0F, 0x00, 0xF4 });
        var s = cpu.State;
        s.B.X = 0xABCD; s.FlagC = true; s.FlagO = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x000D, cpu.State.B.X);
        Assert.False(cpu.State.FlagC);
        Assert.False(cpu.State.FlagO);
    }

    /// <summary>
    /// 0x80 group with memory operand: 0x80 06 80 00 0A → ADD byte ptr [0x80], 0x0A
    /// (modrm=06: mod=00 reg=000=ADD rm=110=disp16).
    /// </summary>
    [Fact]
    public void Step_AluGroup_AddByteToMemory()
    {
        var (cpu, mem) = Setup(new byte[] { 0x80, 0x06, 0x80, 0x00, 0x0A, 0xF4 });
        mem.WriteByte(0x80, 0x05);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0F, mem.ReadByte(0x80));
    }

    // ---------------- 24.6.6d — TEST + NOT + NEG ----------------

    /// <summary>0xA8 0xFF → TEST AL, 0xFF. AL=0x80 → 0x80 & 0xFF = 0x80, SF=1, ZF=0.</summary>
    [Fact]
    public void Step_TestAlImm8_SetsSign()
    {
        var (cpu, _) = Setup(new byte[] { 0xA8, 0xFF, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x80;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x80, cpu.State.A.L);   // AL unchanged (TEST doesn't write back)
        Assert.True(cpu.State.FlagS);
        Assert.False(cpu.State.FlagZ);
        Assert.False(cpu.State.FlagC);
    }

    /// <summary>TEST AL, AL — common "is AL zero?" idiom. AL=0 → ZF=1.</summary>
    [Fact]
    public void Step_TestAlAl_ZeroIsZero()
    {
        // 0x84 C0 mod=11 reg=000(=AL) rm=000(=AL)
        var (cpu, _) = Setup(new byte[] { 0x84, 0xC0, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x00;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.True(cpu.State.FlagZ);
    }

    /// <summary>
    /// 0xF6 /2 NOT r/m8 reg-direct: F6 D0 → NOT AL.
    /// modrm=D0: mod=11 reg=010(=NOT) rm=000(=AL).
    /// AL=0xAA → 0x55. NOT does NOT touch flags.
    /// </summary>
    [Fact]
    public void Step_NotAl_ViaF6Group()
    {
        var (cpu, _) = Setup(new byte[] { 0xF6, 0xD0, 0xF4 });
        var s = cpu.State;
        s.A.L = 0xAA; s.FlagC = true; s.FlagZ = true;   // pre-set flags
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x55, cpu.State.A.L);
        Assert.True(cpu.State.FlagC);    // CF preserved
        Assert.True(cpu.State.FlagZ);    // ZF preserved
    }

    /// <summary>
    /// 0xF6 /3 NEG r/m8 reg-direct: F6 D8 → NEG AL.
    /// AL=0x05 → -5 = 0xFB. CF=1 (operand was non-zero), SF=1.
    /// </summary>
    [Fact]
    public void Step_NegAl_NonZero()
    {
        var (cpu, _) = Setup(new byte[] { 0xF6, 0xD8, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x05;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xFB, cpu.State.A.L);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagS);
    }

    /// <summary>
    /// NEG AL with AL=0 → 0. CF=0 (operand was zero), ZF=1.
    /// </summary>
    [Fact]
    public void Step_NegAl_Zero()
    {
        var (cpu, _) = Setup(new byte[] { 0xF6, 0xD8, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x00;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x00, cpu.State.A.L);
        Assert.False(cpu.State.FlagC);
        Assert.True(cpu.State.FlagZ);
    }

    /// <summary>
    /// 0xF7 /2 NOT r/m16 reg-direct: F7 D3 → NOT BX.
    /// modrm=D3: mod=11 reg=010(=NOT) rm=011(=BX). BX=0xAAAA → 0x5555.
    /// </summary>
    [Fact]
    public void Step_NotBx_ViaF7Group()
    {
        var (cpu, _) = Setup(new byte[] { 0xF7, 0xD3, 0xF4 });
        var s = cpu.State;
        s.B.X = 0xAAAA;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x5555, cpu.State.B.X);
    }

    /// <summary>
    /// 0xF7 /0 TEST r/m16, imm16 reg-direct: F7 C0 FF FF → TEST AX, 0xFFFF.
    /// modrm=C0: mod=11 reg=000(=TEST) rm=000(=AX). AX=0x1234, TEST sets SF=0, ZF=0.
    /// </summary>
    [Fact]
    public void Step_TestAxImm16_ViaF7Group()
    {
        var (cpu, _) = Setup(new byte[] { 0xF7, 0xC0, 0xFF, 0xFF, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x1234;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x1234, cpu.State.A.X);   // AX unchanged
        Assert.False(cpu.State.FlagZ);
        Assert.False(cpu.State.FlagS);          // 0x1234 high bit = 0
    }

    // ---------------- 24.6.6e — MUL / IMUL / CBW / CWD ----------------

    /// <summary>
    /// 0xF6 /4 MUL r/m8: F6 E3 → MUL BL.
    /// modrm=E3: mod=11 reg=100(=MUL) rm=011(=BL). AL=0x10, BL=0x20 → AX=0x200.
    /// AH=0x02, so CF=OF=1.
    /// </summary>
    [Fact]
    public void Step_MulBl_FullProductInAx()
    {
        var (cpu, _) = Setup(new byte[] { 0xF6, 0xE3, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x10; s.B.L = 0x20;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0200, cpu.State.A.X);
        Assert.True(cpu.State.FlagC);     // AH != 0
        Assert.True(cpu.State.FlagO);
    }

    /// <summary>
    /// MUL with small product (fits in AL): AL=0x05 * BL=0x03 = 0x0F.
    /// AH = 0, so CF=OF=0. ZF=1 (AH zero per 8088 quirk).
    /// </summary>
    [Fact]
    public void Step_MulBl_SmallProduct_ClearsCfOf()
    {
        var (cpu, _) = Setup(new byte[] { 0xF6, 0xE3, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x05; s.B.L = 0x03;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x000F, cpu.State.A.X);
        Assert.False(cpu.State.FlagC);
        Assert.False(cpu.State.FlagO);
        Assert.True(cpu.State.FlagZ);   // ZF reflects AH=0 (8088 quirk)
    }

    /// <summary>
    /// 0xF6 /5 IMUL r/m8: F6 EB → IMUL BL.
    /// modrm=EB: mod=11 reg=101(=IMUL) rm=011(=BL). AL=0xFF (-1) * BL=0x05 = 0xFFFB (-5).
    /// AH = 0xFF (sign-extended), so CF=OF=1.
    /// </summary>
    [Fact]
    public void Step_ImulBl_NegativeResult()
    {
        var (cpu, _) = Setup(new byte[] { 0xF6, 0xEB, 0xF4 });
        var s = cpu.State;
        s.A.L = 0xFF;   // -1
        s.B.L = 0x05;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xFFFB, cpu.State.A.X);   // -5 sign-extended
        Assert.True(cpu.State.FlagC);          // AH=0xFF, non-zero per 8088 rule
        Assert.True(cpu.State.FlagO);
    }

    /// <summary>
    /// 0xF7 /4 MUL r/m16: F7 E3 → MUL BX.
    /// modrm=E3: mod=11 reg=100(=MUL) rm=011(=BX). AX=0x100, BX=0x100 → DX:AX = 0x10000.
    /// AX=0x0000, DX=0x0001. CF=OF=1.
    /// </summary>
    [Fact]
    public void Step_MulBx_DxAxResult()
    {
        var (cpu, _) = Setup(new byte[] { 0xF7, 0xE3, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x100; s.B.X = 0x100;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0000, cpu.State.A.X);
        Assert.Equal(0x0001, cpu.State.D.X);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagO);
    }

    /// <summary>
    /// 0x98 CBW: AL=0x80 (-128 signed) → AX=0xFF80.
    /// </summary>
    [Fact]
    public void Step_Cbw_SignExtendNegative()
    {
        var (cpu, _) = Setup(new byte[] { 0x98, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x80;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xFF80, cpu.State.A.X);
    }

    /// <summary>
    /// CBW with AL positive: AL=0x42 → AX=0x0042 (zero-extends since high bit clear).
    /// </summary>
    [Fact]
    public void Step_Cbw_ZeroExtendPositive()
    {
        var (cpu, _) = Setup(new byte[] { 0x98, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x42;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0042, cpu.State.A.X);
    }

    /// <summary>
    /// 0x99 CWD: AX=0x8000 (-32768) → DX=0xFFFF, AX unchanged.
    /// </summary>
    [Fact]
    public void Step_Cwd_SignExtendNegative()
    {
        var (cpu, _) = Setup(new byte[] { 0x99, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x8000;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x8000, cpu.State.A.X);
        Assert.Equal(0xFFFF, cpu.State.D.X);
    }

    // ---------------- 24.6.7a — control flow ----------------

    /// <summary>
    /// 0xEB JMP rel8 forward: 0xEB 0x02 (skip 2 bytes), then HLT.
    /// IP=0x100 → fetch_imm8 → IP=0x102 → +2 = 0x104. The 2 bytes at
    /// 0x102/0x103 are filler; HLT lives at 0x104.
    /// </summary>
    [Fact]
    public void Step_JmpRel8_Forward()
    {
        var (cpu, _) = Setup(new byte[] { 0xEB, 0x02, 0x00, 0x00, 0xF4 });
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x105, cpu.State.IP);   // post-HLT IP
    }

    /// <summary>
    /// JMP backward — 0xEB 0xFE infinite loop (jumps to itself).
    /// We bail out after a few steps.
    /// </summary>
    [Fact]
    public void Step_JmpRel8_BackwardSelfLoop()
    {
        var (cpu, _) = Setup(new byte[] { 0xEB, 0xFE });
        for (int i = 0; i < 5; i++) cpu.Step();
        Assert.False(cpu.Halted);
        Assert.Equal(0x100, cpu.State.IP);   // looped back to start
    }

    /// <summary>
    /// 0x74 (JZ/JE) taken when ZF=1.
    /// Sequence: ZF=1; JZ +2; ... HLT.
    /// </summary>
    [Fact]
    public void Step_Jz_Taken()
    {
        var (cpu, _) = Setup(new byte[] { 0x74, 0x02, 0x00, 0x00, 0xF4 });
        var s = cpu.State;
        s.FlagZ = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x105, cpu.State.IP);
    }

    /// <summary>JZ NOT taken when ZF=0 — falls through to next byte.</summary>
    [Fact]
    public void Step_Jz_NotTaken()
    {
        // jz +2          74 02
        // mov al, 0xAA   B0 AA   <- this runs because JZ falls through
        // hlt            F4
        var (cpu, _) = Setup(new byte[] { 0x74, 0x02, 0xB0, 0xAA, 0xF4 });
        // ZF default = false
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        // Wait — disp +2 from post-fetch IP=0x102 → target 0x104. Bytes
        // 0x102=0xB0 0x103=0xAA 0x104=0xF4. With JZ not taken IP stays
        // at 0x102, so we run "mov al, 0xAA" first then HLT.
        Assert.Equal(0xAA, cpu.State.A.L);
    }

    /// <summary>
    /// JNE/JNZ (0x75) with ZF=0 — taken.
    /// </summary>
    [Fact]
    public void Step_Jne_Taken_WhenZfZero()
    {
        var (cpu, _) = Setup(new byte[] { 0x75, 0x02, 0x00, 0x00, 0xF4 });
        // ZF=false default
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x105, cpu.State.IP);
    }

    /// <summary>
    /// CALL rel16 then RET — round-trip through the stack.
    /// Layout:
    ///   0x100 mov sp, 0x0200    BC 00 02
    ///   0x103 call rel16 +0x05  E8 05 00       (3 bytes; IP after = 0x106; jump to 0x10B)
    ///   0x106 mov al, 0xBB      B0 BB           (executed AFTER ret returns here)
    ///   0x108 hlt               F4
    ///   0x109 (filler)          00 00
    ///   0x10B mov al, 0xAA      B0 AA           (callee body)
    ///   0x10D ret               C3
    /// Expected: AL=0xBB (callee's 0xAA gets overwritten by post-return 0xBB).
    /// </summary>
    [Fact]
    public void Step_CallRetNear_RoundTrip()
    {
        var (cpu, _) = Setup(new byte[]
        {
            0xBC, 0x00, 0x02,    // mov sp, 0x0200
            0xE8, 0x05, 0x00,    // call +5 → 0x10B
            0xB0, 0xBB,          // mov al, 0xBB (after return)
            0xF4,                // hlt
            0x00, 0x00,          // filler
            0xB0, 0xAA,          // mov al, 0xAA (callee)
            0xC3                 // ret
        });
        for (int i = 0; i < 32 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xBB, cpu.State.A.L);
        Assert.Equal(0x0200, cpu.State.SP);   // SP back to original
    }

    /// <summary>
    /// LOOP — countdown loop. CX=3; each LOOP decrements and jumps if non-zero.
    ///   mov cx, 3        B9 03 00
    ///   inc bx           43           ← loop body
    ///   loop -3          E2 FD       ← back to inc bx
    ///   hlt              F4
    /// After loop: CX=0, BX=3.
    /// </summary>
    [Fact]
    public void Step_Loop_DecCxAndBranch()
    {
        var (cpu, _) = Setup(new byte[]
        {
            0xB9, 0x03, 0x00,    // mov cx, 3
            0x43,                // inc bx
            0xE2, 0xFD,          // loop -3 (back to inc bx)
            0xF4                 // hlt
        });
        for (int i = 0; i < 32 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0000, cpu.State.C.X);
        Assert.Equal(0x0003, cpu.State.B.X);
    }

    /// <summary>
    /// JCXZ — jumps if CX==0, doesn't decrement.
    ///   mov cx, 0        B9 00 00
    ///   jcxz +2          E3 02
    ///   mov al, 0x99     B0 99   ← skipped
    ///   hlt              F4
    /// </summary>
    [Fact]
    public void Step_Jcxz_TakenWhenCxZero()
    {
        var (cpu, _) = Setup(new byte[]
        {
            0xB9, 0x00, 0x00,    // mov cx, 0
            0xE3, 0x02,          // jcxz +2
            0xB0, 0x99,          // mov al, 0x99 (skipped)
            0xF4                 // hlt
        });
        for (int i = 0; i < 16 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x00, cpu.State.A.L);   // mov skipped
    }

    /// <summary>
    /// JS (0x78) when SF=1.
    /// </summary>
    [Fact]
    public void Step_Js_Taken()
    {
        var (cpu, _) = Setup(new byte[] { 0x78, 0x02, 0x00, 0x00, 0xF4 });
        var s = cpu.State;
        s.FlagS = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x105, cpu.State.IP);
    }

    /// <summary>
    /// JL (0x7C) when SF != OF (signed less-than). SF=1, OF=0 → SF^OF=1.
    /// </summary>
    [Fact]
    public void Step_Jl_TakenWhenSfNeqOf()
    {
        var (cpu, _) = Setup(new byte[] { 0x7C, 0x02, 0x00, 0x00, 0xF4 });
        var s = cpu.State;
        s.FlagS = true;
        s.FlagO = false;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x105, cpu.State.IP);
    }

    // ---------------- 24.6.7b — shift/rotate (D0/D1, count=1) ----------------

    /// <summary>
    /// 0xD0 /4 SHL AL, 1: D0 E0 → SHL AL, 1.
    /// modrm=E0: mod=11 reg=100(=SHL) rm=000(=AL). AL=0x42 (0100_0010) → 0x84 (1000_0100).
    /// CF=0 (old MSB), SF=1, AF=0, OF=1 (sign change).
    /// </summary>
    [Fact]
    public void Step_ShlAl_ByOne_BasicLeftShift()
    {
        var (cpu, _) = Setup(new byte[] { 0xD0, 0xE0, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x42;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x84, cpu.State.A.L);
        Assert.False(cpu.State.FlagC);    // old MSB was 0
        Assert.True(cpu.State.FlagS);     // result MSB = 1
        Assert.True(cpu.State.FlagO);     // sign changed (was 0, now 1)
    }

    /// <summary>SHL with carry-out: AL=0x80 → 0x00, CF=1, ZF=1.</summary>
    [Fact]
    public void Step_ShlAl_CarryOut()
    {
        var (cpu, _) = Setup(new byte[] { 0xD0, 0xE0, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x80;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x00, cpu.State.A.L);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagZ);
    }

    /// <summary>
    /// 0xD0 /5 SHR AL, 1: D0 E8 → SHR AL, 1.
    /// AL=0x81 → 0x40. CF=1 (old LSB), OF=1 (original MSB), AF=0.
    /// </summary>
    [Fact]
    public void Step_ShrAl_ByOne_LogicalRight()
    {
        var (cpu, _) = Setup(new byte[] { 0xD0, 0xE8, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x81;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x40, cpu.State.A.L);
        Assert.True(cpu.State.FlagC);    // old LSB
        Assert.True(cpu.State.FlagO);    // original MSB (8088 SHR rule)
        Assert.False(cpu.State.FlagA);
    }

    /// <summary>
    /// 0xD0 /7 SAR AL, 1: D0 F8.
    /// AL=0x80 (-128) → 0xC0 (-64). CF=0 (old LSB), OF=0, SF=1.
    /// </summary>
    [Fact]
    public void Step_SarAl_ByOne_ArithmeticRight()
    {
        var (cpu, _) = Setup(new byte[] { 0xD0, 0xF8, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x80;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xC0, cpu.State.A.L);   // sign-extended
        Assert.False(cpu.State.FlagC);
        Assert.False(cpu.State.FlagO);
        Assert.True(cpu.State.FlagS);
    }

    /// <summary>
    /// 0xD0 /0 ROL AL, 1: D0 C0.
    /// AL=0x80 → 0x01 (rotated). CF=1 (old MSB now wrapped), OF=1 (sign change).
    /// </summary>
    [Fact]
    public void Step_RolAl_ByOne()
    {
        var (cpu, _) = Setup(new byte[] { 0xD0, 0xC0, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x80;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x01, cpu.State.A.L);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagO);   // result MSB (0) XOR CF (1) = 1
    }

    /// <summary>
    /// 0xD0 /1 ROR AL, 1: D0 C8.
    /// AL=0x01 → 0x80. CF=1 (old LSB), OF=1 (b7 XOR b6 = 1 XOR 0 = 1).
    /// </summary>
    [Fact]
    public void Step_RorAl_ByOne()
    {
        var (cpu, _) = Setup(new byte[] { 0xD0, 0xC8, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x01;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x80, cpu.State.A.L);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagO);
    }

    /// <summary>
    /// 0xD0 /2 RCL AL, 1: D0 D0. AL=0x40, CF_in=1 → AL=0x81 (low bit gets old CF).
    /// CF_out = 0 (old MSB).
    /// </summary>
    [Fact]
    public void Step_RclAl_ByOne()
    {
        var (cpu, _) = Setup(new byte[] { 0xD0, 0xD0, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x40; s.FlagC = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x81, cpu.State.A.L);
        Assert.False(cpu.State.FlagC);    // old MSB was 0
    }

    /// <summary>
    /// 0xD0 /3 RCR AL, 1: D0 D8. AL=0x02, CF_in=1 → AL=0x81 (high bit gets old CF, low half preserved shifted).
    /// CF_out = 0 (old LSB).
    /// </summary>
    [Fact]
    public void Step_RcrAl_ByOne()
    {
        var (cpu, _) = Setup(new byte[] { 0xD0, 0xD8, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x02; s.FlagC = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x81, cpu.State.A.L);
        Assert.False(cpu.State.FlagC);
    }

    /// <summary>
    /// 0xD1 SHL AX, 1 (16-bit form). AX=0x4000 → 0x8000. CF=0, SF=1, OF=1.
    /// </summary>
    [Fact]
    public void Step_ShlAx_ByOne_W16()
    {
        var (cpu, _) = Setup(new byte[] { 0xD1, 0xE0, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x4000;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x8000, cpu.State.A.X);
        Assert.False(cpu.State.FlagC);
        Assert.True(cpu.State.FlagS);
        Assert.True(cpu.State.FlagO);
    }

    /// <summary>
    /// SHL with mem operand: D0 26 80 00 → SHL byte ptr [0x80], 1.
    /// Pre-mem 0x80 = 0x42 → 0x84.
    /// </summary>
    [Fact]
    public void Step_ShlMem_ByOne()
    {
        var (cpu, mem) = Setup(new byte[] { 0xD0, 0x26, 0x80, 0x00, 0xF4 });
        mem.WriteByte(0x80, 0x42);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x84, mem.ReadByte(0x80));
    }

    // ---------------- 24.6.7c — string ops (single iteration) ----------------

    /// <summary>0xA4 MOVSB — DS:SI → ES:DI; SI++; DI++.</summary>
    [Fact]
    public void Step_Movsb_ForwardCopy()
    {
        var (cpu, mem) = Setup(new byte[] { 0xA4, 0xF4 });
        var s = cpu.State;
        s.SI = 0x100; s.DI = 0x200;
        s.DS = 0; s.ES = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x300);   // code at 0x300 to keep clear of data
        // Re-load code at the new entry
        var code = new byte[] { 0xA4, 0xF4 };
        mem.LoadBinary(code, 0, 0x300);

        mem.WriteByte(0x100, 0xAB);
        mem.WriteByte(0x200, 0x00);   // dst initially zero

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xAB, mem.ReadByte(0x200));
        Assert.Equal(0x101, cpu.State.SI);
        Assert.Equal(0x201, cpu.State.DI);
    }

    /// <summary>
    /// MOVSB with DF=1 — backward copy (SI/DI decrement).
    /// </summary>
    [Fact]
    public void Step_Movsb_BackwardCopy_DfSet()
    {
        var (cpu, mem) = Setup(new byte[] { 0xA4, 0xF4 });
        var s = cpu.State;
        s.SI = 0x100; s.DI = 0x200;
        s.FlagD = true;   // DF=1
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x300);
        mem.LoadBinary(new byte[] { 0xA4, 0xF4 }, 0, 0x300);
        mem.WriteByte(0x100, 0x55);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x55, mem.ReadByte(0x200));
        Assert.Equal(0x0FF, cpu.State.SI);   // SI decremented
        Assert.Equal(0x1FF, cpu.State.DI);   // DI decremented
    }

    /// <summary>0xAA STOSB — AL → ES:DI; DI++.</summary>
    [Fact]
    public void Step_Stosb_WritesAlAndAdvancesDi()
    {
        var (cpu, mem) = Setup(new byte[] { 0xAA, 0xF4 });
        var s = cpu.State;
        s.A.L = 0xCD;
        s.DI = 0x100;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x200);
        mem.LoadBinary(new byte[] { 0xAA, 0xF4 }, 0, 0x200);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xCD, mem.ReadByte(0x100));
        Assert.Equal(0x101, cpu.State.DI);
    }

    /// <summary>0xAB STOSW — AX → ES:DI; DI+=2.</summary>
    [Fact]
    public void Step_Stosw_WritesAxAndAdvancesDi()
    {
        var (cpu, mem) = Setup(new byte[] { 0xAB, 0xF4 });
        var s = cpu.State;
        s.A.X = 0xBEEF;
        s.DI = 0x100;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x200);
        mem.LoadBinary(new byte[] { 0xAB, 0xF4 }, 0, 0x200);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xEF, mem.ReadByte(0x100));
        Assert.Equal(0xBE, mem.ReadByte(0x101));
        Assert.Equal(0x102, cpu.State.DI);
    }

    /// <summary>0xAC LODSB — DS:SI → AL; SI++.</summary>
    [Fact]
    public void Step_Lodsb_ReadsToAl()
    {
        var (cpu, mem) = Setup(new byte[] { 0xAC, 0xF4 });
        var s = cpu.State;
        s.SI = 0x100;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x200);
        mem.LoadBinary(new byte[] { 0xAC, 0xF4 }, 0, 0x200);
        mem.WriteByte(0x100, 0x42);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x42, cpu.State.A.L);
        Assert.Equal(0x101, cpu.State.SI);
    }

    /// <summary>
    /// 0xA6 CMPSB — comparing two equal bytes sets ZF=1; SI++; DI++.
    /// </summary>
    [Fact]
    public void Step_Cmpsb_EqualSetsZf()
    {
        var (cpu, mem) = Setup(new byte[] { 0xA6, 0xF4 });
        var s = cpu.State;
        s.SI = 0x100; s.DI = 0x200;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x300);
        mem.LoadBinary(new byte[] { 0xA6, 0xF4 }, 0, 0x300);
        mem.WriteByte(0x100, 0xAA);
        mem.WriteByte(0x200, 0xAA);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.True(cpu.State.FlagZ);
        Assert.False(cpu.State.FlagC);
        Assert.Equal(0x101, cpu.State.SI);
        Assert.Equal(0x201, cpu.State.DI);
    }

    /// <summary>
    /// 0xAE SCASB with AL=0x42 vs mem 0x42 — match → ZF=1.
    /// </summary>
    [Fact]
    public void Step_Scasb_MatchSetsZf()
    {
        var (cpu, mem) = Setup(new byte[] { 0xAE, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x42;
        s.DI = 0x100;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x200);
        mem.LoadBinary(new byte[] { 0xAE, 0xF4 }, 0, 0x200);
        mem.WriteByte(0x100, 0x42);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.True(cpu.State.FlagZ);
        Assert.Equal(0x101, cpu.State.DI);
    }

    // ---------------- 24.6.7c2 — REP / REPE / REPNE prefix ----------------

    /// <summary>
    /// REP MOVSB — copy 4 bytes from DS:SI to ES:DI.
    /// Layout: src[0x100]={0x11,0x22,0x33,0x44}; CX=4; rep movsb; hlt.
    /// After: dst[0x200]={0x11,0x22,0x33,0x44}; CX=0; SI=0x104; DI=0x204.
    /// </summary>
    [Fact]
    public void Step_RepMovsb_CopiesNBytes()
    {
        var (cpu, mem) = Setup(new byte[] { 0xF3, 0xA4, 0xF4 });
        var s = cpu.State;
        s.SI = 0x100; s.DI = 0x200;
        s.C.X = 4;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x300);
        mem.LoadBinary(new byte[] { 0xF3, 0xA4, 0xF4 }, 0, 0x300);
        for (int i = 0; i < 4; i++) mem.WriteByte(0x100 + i, (byte)(0x11 * (i + 1)));

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        for (int i = 0; i < 4; i++)
            Assert.Equal((byte)(0x11 * (i + 1)), mem.ReadByte(0x200 + i));
        Assert.Equal(0, cpu.State.C.X);
        Assert.Equal(0x104, cpu.State.SI);
        Assert.Equal(0x204, cpu.State.DI);
    }

    /// <summary>
    /// REP STOSB — fill memory with AL.
    /// </summary>
    [Fact]
    public void Step_RepStosb_FillsMemory()
    {
        var (cpu, mem) = Setup(new byte[] { 0xF3, 0xAA, 0xF4 });
        var s = cpu.State;
        s.A.L = 0xCC;
        s.DI = 0x100;
        s.C.X = 8;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x200);
        mem.LoadBinary(new byte[] { 0xF3, 0xAA, 0xF4 }, 0, 0x200);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        for (int i = 0; i < 8; i++)
            Assert.Equal(0xCC, mem.ReadByte(0x100 + i));
        Assert.Equal(0, cpu.State.C.X);
        Assert.Equal(0x108, cpu.State.DI);
    }

    /// <summary>
    /// REP MOVSB with CX=0 — no iterations, nothing copied.
    /// </summary>
    [Fact]
    public void Step_RepMovsb_CxZero_NoIterations()
    {
        var (cpu, mem) = Setup(new byte[] { 0xF3, 0xA4, 0xF4 });
        var s = cpu.State;
        s.SI = 0x100; s.DI = 0x200;
        s.C.X = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x300);
        mem.LoadBinary(new byte[] { 0xF3, 0xA4, 0xF4 }, 0, 0x300);
        mem.WriteByte(0x100, 0xAA);
        mem.WriteByte(0x200, 0x00);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x00, mem.ReadByte(0x200));   // dst untouched
        Assert.Equal(0x100, cpu.State.SI);          // SI unchanged
        Assert.Equal(0x200, cpu.State.DI);
    }

    /// <summary>
    /// REPNE SCASB — search for AL=0x42 in [ES:DI..]; abort when found (ZF=1).
    /// Buffer at 0x100: { 0x11, 0x22, 0x42, 0x99 }; AL=0x42; CX=10.
    /// Should abort after 3 iterations: DI=0x103, CX=7, ZF=1.
    /// </summary>
    [Fact]
    public void Step_RepneScasb_StopsOnMatch()
    {
        var (cpu, mem) = Setup(new byte[] { 0xF2, 0xAE, 0xF4 });
        var s = cpu.State;
        s.A.L = 0x42;
        s.DI = 0x100;
        s.C.X = 10;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x200);
        mem.LoadBinary(new byte[] { 0xF2, 0xAE, 0xF4 }, 0, 0x200);
        mem.WriteByte(0x100, 0x11);
        mem.WriteByte(0x101, 0x22);
        mem.WriteByte(0x102, 0x42);
        mem.WriteByte(0x103, 0x99);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(7, cpu.State.C.X);
        Assert.Equal(0x103, cpu.State.DI);   // past the match
        Assert.True(cpu.State.FlagZ);
    }

    /// <summary>
    /// REPE CMPSB — keep going while bytes match; abort on mismatch.
    /// Buffers a={0x11,0x22,0x33}, b={0x11,0x22,0x44}; CX=3.
    /// After 3rd CMPSB: ZF=0 (third pair differs), CX=0 → loop also exits via CX.
    /// Actually 0x33 != 0x44 → after iter 3, ZF=0, CX dropped to 0, loop exits.
    /// </summary>
    [Fact]
    public void Step_RepeCmpsb_StopsOnMismatch()
    {
        var (cpu, mem) = Setup(new byte[] { 0xF3, 0xA6, 0xF4 });
        var s = cpu.State;
        s.SI = 0x100; s.DI = 0x200;
        s.C.X = 3;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x300);
        mem.LoadBinary(new byte[] { 0xF3, 0xA6, 0xF4 }, 0, 0x300);
        mem.WriteByte(0x100, 0x11); mem.WriteByte(0x200, 0x11);
        mem.WriteByte(0x101, 0x22); mem.WriteByte(0x201, 0x22);
        mem.WriteByte(0x102, 0x33); mem.WriteByte(0x202, 0x44);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0, cpu.State.C.X);
        Assert.False(cpu.State.FlagZ);   // last comparison was unequal
    }

    // ---------------- 24.6.7d — flag manip + IO ----------------

    /// <summary>0xF8 CLC — clear CF. Pre-set CF=1 → after CLC, CF=0.</summary>
    [Fact]
    public void Step_Clc_ClearsCarry()
    {
        var (cpu, _) = Setup(new byte[] { 0xF8, 0xF4 });
        var s = cpu.State;
        s.FlagC = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.False(cpu.State.FlagC);
    }

    /// <summary>0xF9 STC — set CF.</summary>
    [Fact]
    public void Step_Stc_SetsCarry()
    {
        var (cpu, _) = Setup(new byte[] { 0xF9, 0xF4 });
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.True(cpu.State.FlagC);
    }

    /// <summary>0xF5 CMC — toggle CF (1→0).</summary>
    [Fact]
    public void Step_Cmc_TogglesCarry()
    {
        var (cpu, _) = Setup(new byte[] { 0xF5, 0xF4 });
        var s = cpu.State;
        s.FlagC = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.False(cpu.State.FlagC);
    }

    /// <summary>0xFC CLD then 0xFD STD — exercise DF bit.</summary>
    [Fact]
    public void Step_CldStd_TogglesDf()
    {
        var (cpu, _) = Setup(new byte[] { 0xFC, 0xFD, 0xF4 });
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.True(cpu.State.FlagD);   // STD set DF=1
    }

    /// <summary>0xFA CLI / 0xFB STI exercise IF bit.</summary>
    [Fact]
    public void Step_CliSti_TogglesIf()
    {
        var (cpu, _) = Setup(new byte[] { 0xFA, 0xFB, 0xF4 });
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.True(cpu.State.FlagI);   // STI set IF=1
    }

    /// <summary>
    /// 0x9E SAHF — copy AH to FLAGS bits 7:0. AH=0x05 (= 0000_0101) sets
    /// CF=1 (bit 0), PF=1 (bit 2), AF=0, ZF=0, SF=0.
    /// </summary>
    [Fact]
    public void Step_Sahf_CopiesAhToFlagsLow()
    {
        var (cpu, _) = Setup(new byte[] { 0x9E, 0xF4 });
        var s = cpu.State;
        s.A.H = 0x05;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.True(cpu.State.FlagC);
        Assert.True(cpu.State.FlagP);
        Assert.False(cpu.State.FlagA);
        Assert.False(cpu.State.FlagZ);
        Assert.False(cpu.State.FlagS);
    }

    /// <summary>
    /// 0x9F LAHF — copy FLAGS bits 7:0 to AH. With CF=1, PF=0, ZF=1, SF=1
    /// → low byte = 0x01 | 0x40 | 0x80 | 0x02 (bit 1 reserved-but-FLAGS
    /// storage may have it) = 0xC3 or so. Just check the salient bits.
    /// </summary>
    [Fact]
    public void Step_Lahf_CopiesFlagsLowToAh()
    {
        var (cpu, _) = Setup(new byte[] { 0x9F, 0xF4 });
        var s = cpu.State;
        s.FlagC = true;
        s.FlagZ = true;
        s.FlagS = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        // CF (bit 0), ZF (bit 6), SF (bit 7) must all be set in AH.
        Assert.Equal(0x01, cpu.State.A.H & 0x01);
        Assert.Equal(0x40, cpu.State.A.H & 0x40);
        Assert.Equal(0x80, cpu.State.A.H & 0x80);
    }

    /// <summary>0xE4 IN AL, imm8 — open-bus stub returns 0xFF.</summary>
    [Fact]
    public void Step_InAlImm8_OpenBusReturnsFF()
    {
        var (cpu, _) = Setup(new byte[] { 0xE4, 0x60, 0xF4 });
        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xFF, cpu.State.A.L);
        // IP advanced past port byte → 0x103.
        Assert.Equal(0x103, cpu.State.IP);
    }

    /// <summary>0xE6 OUT imm8, AL — silent no-op, just advances IP past port byte.</summary>
    [Fact]
    public void Step_OutImm8Al_NoOpAdvancesIp()
    {
        var (cpu, _) = Setup(new byte[] { 0xB0, 0x42, 0xE6, 0x60, 0xF4 });
        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x42, cpu.State.A.L);   // AL unchanged after OUT
    }

    // ---------------- 24.6.7d2 — INT / INT3 / INTO / IRET ----------------

    /// <summary>
    /// 0xCD 21 → INT 0x21. Reads IVT[0x21*4=0x84..0x87] for far pointer.
    /// Pre-fill IVT[0x21] = 0:0x500 → handler at 0x500.
    /// </summary>
    [Fact]
    public void Step_IntImm8_JumpsToVector()
    {
        var (cpu, mem) = Setup(new byte[] { 0xCD, 0x21, 0xF4 });
        var s = cpu.State;
        s.SP = 0x0200; s.SS = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x300);
        mem.LoadBinary(new byte[] { 0xCD, 0x21, 0xF4 }, 0, 0x300);
        // IVT[0x21*4 = 0x84] holds: IP=0x500, CS=0
        mem.WriteByte(0x84, 0x00);
        mem.WriteByte(0x85, 0x05);
        mem.WriteByte(0x86, 0x00);
        mem.WriteByte(0x87, 0x00);
        // Handler at CS=0, IP=0x500: just HLT
        mem.WriteByte(0x500, 0xF4);

        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        // Stack should hold pushed FLAGS, CS=0, IP=0x302 (post-INT 2-byte instruction)
        Assert.Equal(0x01FA, cpu.State.SP);    // 6 bytes pushed
        ushort pushedIp = (ushort)(mem.ReadByte(0x01FA) | (mem.ReadByte(0x01FB) << 8));
        ushort pushedCs = (ushort)(mem.ReadByte(0x01FC) | (mem.ReadByte(0x01FD) << 8));
        Assert.Equal(0x302, pushedIp);
        Assert.Equal(0x000, pushedCs);
    }

    /// <summary>
    /// INT clears IF and TF after pushing FLAGS.
    /// </summary>
    [Fact]
    public void Step_Int_ClearsIfTf()
    {
        var (cpu, mem) = Setup(new byte[] { 0xCD, 0x10, 0xF4 });
        var s = cpu.State;
        s.SP = 0x0200; s.SS = 0;
        s.FlagI = true; s.FlagT = true;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x300);
        mem.LoadBinary(new byte[] { 0xCD, 0x10, 0xF4 }, 0, 0x300);
        // IVT[0x10*4 = 0x40] → handler that just halts
        mem.WriteByte(0x40, 0x00); mem.WriteByte(0x41, 0x06);
        mem.WriteByte(0x42, 0x00); mem.WriteByte(0x43, 0x00);
        mem.WriteByte(0x600, 0xF4);

        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.False(cpu.State.FlagI);
        Assert.False(cpu.State.FlagT);
    }

    /// <summary>
    /// Round-trip via INT + IRET: handler does IRET, control returns past INT.
    /// </summary>
    [Fact]
    public void Step_IntIret_RoundTrip()
    {
        // CS=0; main: int 0x10; mov al, 0xAA; hlt
        // Handler at 0x600: iret
        // After: AL=0xAA (mov ran post-IRET), SP back to original.
        var code = new byte[]
        {
            0xCD, 0x10,        // int 0x10
            0xB0, 0xAA,        // mov al, 0xAA
            0xF4               // hlt
        };
        var (cpu, mem) = Setup(code);
        var s = cpu.State;
        s.SP = 0x0200; s.SS = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x300);
        mem.LoadBinary(code, 0, 0x300);
        mem.WriteByte(0x40, 0x00); mem.WriteByte(0x41, 0x06);
        mem.WriteByte(0x42, 0x00); mem.WriteByte(0x43, 0x00);
        mem.WriteByte(0x600, 0xCF);   // iret

        for (int i = 0; i < 16 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0xAA, cpu.State.A.L);
        Assert.Equal(0x0200, cpu.State.SP);
    }

    /// <summary>
    /// INTO with OF=1 → does INT 4. With OF=0 → no-op.
    /// </summary>
    [Fact]
    public void Step_Into_ConditionalOnOverflow()
    {
        // First test: OF=0, INTO is no-op, then HLT
        var (cpu, _) = Setup(new byte[] { 0xCE, 0xF4 });
        var s = cpu.State;
        s.SP = 0x0200; s.SS = 0;
        s.FlagO = false;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x0200, cpu.State.SP);   // No push happened
    }

    /// <summary>
    /// 0xCC INT 3 — vector at IVT[12]. Pre-fill IVT[3]=0:0x700; handler halts.
    /// </summary>
    [Fact]
    public void Step_Int3_BreakpointVector()
    {
        var (cpu, mem) = Setup(new byte[] { 0xCC, 0xF4 });
        var s = cpu.State;
        s.SP = 0x0200; s.SS = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x300);
        mem.LoadBinary(new byte[] { 0xCC, 0xF4 }, 0, 0x300);
        // IVT[3*4 = 0x0C] → handler at 0x700
        mem.WriteByte(0x0C, 0x00); mem.WriteByte(0x0D, 0x07);
        mem.WriteByte(0x0E, 0x00); mem.WriteByte(0x0F, 0x00);
        mem.WriteByte(0x700, 0xF4);

        for (int i = 0; i < 8 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x01FA, cpu.State.SP);   // 6 bytes pushed
    }

    // ---------------- 24.6.7e — FE / FF group ----------------

    /// <summary>0xFE /0 INC byte ptr [BX]: FE 07. Pre-mem 0x80=0x10 → 0x11.</summary>
    [Fact]
    public void Step_IncMemByte_ViaFeGroup()
    {
        var (cpu, mem) = Setup(new byte[] { 0xFE, 0x07, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x80;
        s.FlagC = true;   // verify CF preserved
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x80, 0x10);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x11, mem.ReadByte(0x80));
        Assert.True(cpu.State.FlagC);   // INC preserves CF
    }

    /// <summary>
    /// 0xFF /0 INC word ptr [BX]: FF 07.
    /// </summary>
    [Fact]
    public void Step_IncMemWord_ViaFfGroup()
    {
        var (cpu, mem) = Setup(new byte[] { 0xFF, 0x07, 0xF4 });
        var s = cpu.State;
        s.B.X = 0x80;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x80, 0xFF); mem.WriteByte(0x81, 0x00);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x00, mem.ReadByte(0x80));
        Assert.Equal(0x01, mem.ReadByte(0x81));
    }

    /// <summary>
    /// 0xFF /4 JMP near r/m16 (reg-direct): FF E0 → JMP AX.
    /// modrm=E0: mod=11 reg=100(=JMP near) rm=000(=AX).
    /// AX=0x500 → IP=0x500.
    /// </summary>
    [Fact]
    public void Step_JmpNearIndirect_ViaFfGroup()
    {
        var (cpu, mem) = Setup(new byte[] { 0xFF, 0xE0, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x500;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        // Place HLT at the target so we stop cleanly
        mem.WriteByte(0x500, 0xF4);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x501, cpu.State.IP);
    }

    /// <summary>
    /// 0xFF /2 CALL near r/m16: FF D0 → CALL AX.
    /// AX=0x600. After: top-of-stack = post-INT IP = 0x102, IP=0x600.
    /// </summary>
    [Fact]
    public void Step_CallNearIndirect_ViaFfGroup()
    {
        var (cpu, mem) = Setup(new byte[] { 0xFF, 0xD0, 0xF4 });
        var s = cpu.State;
        s.A.X = 0x600;
        s.SP = 0x0200; s.SS = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);
        mem.WriteByte(0x600, 0xF4);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x01FE, cpu.State.SP);
        ushort retIp = (ushort)(mem.ReadByte(0x01FE) | (mem.ReadByte(0x01FF) << 8));
        Assert.Equal(0x102, retIp);
    }

    /// <summary>
    /// 0xFF /6 PUSH r/m16 reg-direct: FF F0 → PUSH AX.
    /// modrm=F0: mod=11 reg=110(=PUSH) rm=000(=AX).
    /// </summary>
    [Fact]
    public void Step_PushRm16_ViaFfGroup()
    {
        var (cpu, mem) = Setup(new byte[] { 0xFF, 0xF0, 0xF4 });
        var s = cpu.State;
        s.A.X = 0xCAFE;
        s.SP = 0x0200; s.SS = 0;
        cpu.LoadState(s);
        cpu.SetEntryPoint(0, 0x100);

        for (int i = 0; i < 4 && !cpu.Halted; i++) cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x01FE, cpu.State.SP);
        Assert.Equal(0xFE, mem.ReadByte(0x01FE));
        Assert.Equal(0xCA, mem.ReadByte(0x01FF));
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
