// PcMemoryBus — spec-driven initialization layer over AprX86.Cli.Memory.X86Memory.
//
// Phase 28.1: declares the IBM PC physical address space, initializes
// the BIOS Data Area, places a minimal BIOS ROM stub at FFFF:0000 that
// just halts (Phase 28.2+ will replace this with INT-vector trampolines
// the HLE BIOS attaches to), and pre-clears the CGA framebuffer.
//
// We don't enforce region permissions yet (read-only ROM, MMIO write
// side-effects, etc.) — the underlying X86Memory is still a flat 1 MB
// byte[] and the CPU writes everything in one address space. The
// spec/machines/ibm-pc-xt.json table is loaded for two reasons:
//   (1) early-fail if the spec doesn't describe a sane 1 MB shape, and
//   (2) future sub-phases (28.5 floppy / 28.7 PIC) will dispatch on
//       region.handler ID — better to start carrying the table around
//       from day one.

using AprCpu.Core.JsonSpec;
using AprX86.Cli.Memory;

namespace AprPc.Cli.Memory;

public sealed class PcMemoryBus
{
    public const int IvtBase            = 0x00000;
    public const int IvtSize            = 0x00400;
    public const int BdaBase            = 0x00400;
    public const int BdaSize            = 0x00100;
    public const int CgaFramebufferBase = 0xB8000;
    public const int CgaFramebufferSize = 0x08000;
    public const int BiosRomBase        = 0xF0000;
    public const int BiosRomSize        = 0x10000;

    private readonly X86Memory _mem;
    private readonly MachineSpec _spec;

    public X86Memory Memory => _mem;
    public MachineSpec Spec => _spec;

    public PcMemoryBus(MachineSpec spec)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _mem  = new X86Memory();
        ValidateSpec(spec);
    }

    /// <summary>
    /// Locate <c>spec/machines/ibm-pc-xt.json</c> by walking up from the
    /// running binary. Mirrors the lookup pattern in NesMemoryBus /
    /// GbaMemoryBus / X86JsonCpu.
    /// </summary>
    public static string LocateMachineSpec()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var probe = Path.Combine(d.FullName, "spec", "machines", "ibm-pc-xt.json");
            if (File.Exists(probe)) return probe;
            d = d.Parent;
        }
        var cwd = Path.Combine(Environment.CurrentDirectory, "spec", "machines", "ibm-pc-xt.json");
        if (File.Exists(cwd)) return cwd;
        throw new FileNotFoundException(
            "PcMemoryBus: cannot locate spec/machines/ibm-pc-xt.json. Run from repo root.");
    }

    /// <summary>
    /// Zero RAM, populate the BIOS Data Area with the AT/XT-class
    /// defaults we care about, and install the minimal BIOS ROM stub.
    /// Call this on every PC reset.
    /// </summary>
    public void Reset()
    {
        _mem.Clear();
        InitializeBiosDataArea();
        InitializeBiosRomStub();
    }

    /// <summary>
    /// Populate the BIOS Data Area (segment 0x0040, physical 0x00400+).
    /// Only the slots Phase 28's INT handlers need so far. More land
    /// in 28.3 (kbd buffer head/tail) / 28.4 (timer ticks).
    /// </summary>
    private void InitializeBiosDataArea()
    {
        // 0x410 (equipment word):
        //   bit 0:    diskette drive present              = 1
        //   bits 1:   FPU present                         = 0
        //   bits 2-3: system board RAM (00 = no longer used)
        //   bits 4-5: initial video mode  (10 = 80×25 color)
        //   bits 6-7: number of floppy drives - 1         = 00 (1 drive)
        //   bit 8:    DMA chip present                    = 0 (PCjr-style, ignored on modern)
        //   bits 9-11: number of serial ports             = 000
        //   bit 12:    game adapter installed             = 0
        //   bit 13:    internal modem (PCjr)              = 0
        //   bits 14-15: number of parallel ports          = 00
        // Effective bytes: low=0x21 high=0x00 = 0x0021 (1 floppy, 80×25 color).
        // The classic XT-class value is 0x0061: 1 floppy + 80×25 color + 1 parallel.
        WriteWord16(0x00410, 0x0061);

        // 0x413 (conventional memory size in KB) = 640
        WriteWord16(0x00413, 0x0280);
    }

    /// <summary>
    /// Phase 28.6 (upgraded post-28.8c) — full LLE bootstrap. The
    /// BIOS ROM area at F000:E05B contains a real 37-byte 8086
    /// routine that does the standard PC boot:
    /// <list type="number">
    /// <item>Zero segment registers (DS = ES = SS = 0).</item>
    /// <item>Set up an initial stack at SS:SP = 0:7C00.</item>
    /// <item>INT 13h AH=02 read sector 0 of drive 0 into 0000:7C00.</item>
    /// <item>Far JMP 0000:7C00 with DL = 00h.</item>
    /// <item>HLT on read failure.</item>
    /// </list>
    /// The reset vector at FFFF:0000 is a 5-byte far JMP to
    /// F000:E05B (classic IBM PC cold-start entry point convention).
    ///
    /// This is **real LLE bootstrap**: the CPU executes actual 8086
    /// instructions for every step of the boot flow. INT 13h
    /// underneath is still HLE (HleBios), but the bootstrap
    /// *orchestration* uses no host-side intercept.
    ///
    /// Until Phase 28.8c, this code couldn't run because 0xEA
    /// (JMP ptr16:16) was missing from the i8086 spec (see the
    /// "deferred" note in control-flow.json). Phase 28.8a/c
    /// closed that gap (plus 0xCB RETF and 0x9A CALL ptr16:16),
    /// so the LLE path is now usable as the default. The HLE INT
    /// 19h handler is retained as a fallback for code that calls
    /// INT 19h explicitly (warm reboot from DOS, etc.).
    ///
    /// Bytes assembled once from
    /// <c>test-roms/x86/src/28.6-bios-bootstrap.asm</c>.
    /// </summary>
    private void InitializeBiosRomStub()
    {
        // FFFF:0000 (phys 0xFFFF0): far JMP F000:E05B
        //   EA ip_lo ip_hi cs_lo cs_hi  (5 bytes)
        WriteByte(0xFFFF0, 0xEA);
        WriteByte(0xFFFF1, 0x5B);   // IP low
        WriteByte(0xFFFF2, 0xE0);   // IP high
        WriteByte(0xFFFF3, 0x00);   // CS low
        WriteByte(0xFFFF4, 0xF0);   // CS high

        // F000:E05B (phys 0xFE05B): real 8086 bootstrap routine.
        var bootstrap = new byte[]
        {
            0x31, 0xC0,                         // xor ax, ax
            0x8E, 0xD8,                         // mov ds, ax
            0x8E, 0xC0,                         // mov es, ax
            0x8E, 0xD0,                         // mov ss, ax
            0xBC, 0x00, 0x7C,                   // mov sp, 0x7C00
            0xB8, 0x01, 0x02,                   // mov ax, 0x0201 (AH=02 AL=01)
            0xB9, 0x01, 0x00,                   // mov cx, 0x0001 (cyl=0 sec=1)
            0xBA, 0x00, 0x00,                   // mov dx, 0x0000 (drive=0 head=0)
            0xBB, 0x00, 0x7C,                   // mov bx, 0x7C00
            0xCD, 0x13,                         // int 0x13 (read sector via HLE)
            0x72, 0x07,                         // jc +7 (to HLT fallback)
            0xB2, 0x00,                         // mov dl, 0 (boot drive)
            0xEA, 0x00, 0x7C, 0x00, 0x00,       // jmp far 0000:7C00
            0xF4,                               // hlt (read-fail path)
            0xEB, 0xFD,                         // jmp $-1 (catch HLT wake)
        };
        for (int i = 0; i < bootstrap.Length; i++)
            WriteByte(0xFE05B + i, bootstrap[i]);
    }

    public byte ReadByte(int physAddr) => _mem.ReadByte(physAddr);
    public void WriteByte(int physAddr, byte value) => _mem.WriteByte(physAddr, value);
    public ushort ReadWord16(int physAddr) => _mem.ReadWord(physAddr);
    public void WriteWord16(int physAddr, ushort value) => _mem.WriteWord(physAddr, value);

    /// <summary>
    /// Load a flat binary blob at <c>(segment, offset)</c>. Used by
    /// the .com / boot-sector test loader.
    /// </summary>
    public void LoadBinary(byte[] bytes, ushort segment, ushort offset)
        => _mem.LoadBinary(bytes, segment, offset);

    private static void ValidateSpec(MachineSpec spec)
    {
        if (spec.MemoryRegions.Count == 0)
            throw new InvalidDataException("ibm-pc-xt.json declares no memory_regions");
        // The bus is 1 MB; reject specs that try to declare regions
        // outside that window. Catches typos like 0x010000000 (32-bit).
        foreach (var r in spec.MemoryRegions)
        {
            if (r.AddrEndExclusive > 0x100000)
                throw new InvalidDataException(
                    $"region {r.Name} ends at 0x{r.AddrEndExclusive:X} > 1MB (0x100000)");
        }
    }
}
