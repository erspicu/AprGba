// PcOptions — parsed CLI options for apr-pc.
//
// Phase 28.0: minimum-viable shape. Some fields are declared but unused
// until later sub-phases (e.g. FloppyAPath consumed in 28.5).

namespace AprPc.Cli;

public sealed class PcOptions
{
    // Disk inputs (at least one expected once 28.5+ is implemented;
    // empty in scaffolding mode → UI opens with no disk).
    //
    // Phase 32.1 — FloppyAPath / FloppyBPath are convenience aliases
    // for FloppyAPaths[0] / FloppyBPaths[0]; the full list supports
    // hot-swap via Ctrl+L (GUI) or "INSERT A 2" (headless stdin) for
    // multi-disk software (CheckIt 2-disk, FreeDOS install 4-disk,
    // Windows 3.1 6-disk). Parsed from --floppy-a=a.img,b.img,c.img.
    public IReadOnlyList<string> FloppyAPaths { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> FloppyBPaths { get; set; } = Array.Empty<string>();

    public string? FloppyAPath
    {
        get => FloppyAPaths.Count > 0 ? FloppyAPaths[0] : null;
        set => FloppyAPaths = value is null ? Array.Empty<string>() : new[] { value };
    }

    /// <summary>
    /// Phase 30.14b — second floppy image (mounted as B:). Lets you keep
    /// the FreeDOS boot floppy A: pristine and put custom test programs
    /// on a separate disk. FDC drive index 0x01.
    /// </summary>
    public string? FloppyBPath
    {
        get => FloppyBPaths.Count > 0 ? FloppyBPaths[0] : null;
        set => FloppyBPaths = value is null ? Array.Empty<string>() : new[] { value };
    }

    public string? HddPath      { get; set; }
    /// <summary>
    /// Phase 32.2 — secondary hard disk image, mounted at drive 0x81
    /// (PC convention: 0x80 = primary, 0x81 = secondary). Optional.
    /// </summary>
    public string? Hdd2Path     { get; set; }
    /// <summary>
    /// Phase 32.3 — host-directory mounts. List of --mount=DRV:host:opts
    /// specs. V1 skeleton: parses + holds the spec but does NOT yet
    /// expose the path as a guest drive (synthesizer is sprint 32.3a).
    /// See MD/issue/pc/hdd-mount-swap-plan.md §32.3.
    /// </summary>
    public List<string> HostMounts { get; } = new();

    // Phase 28.5 — test ROM (tiny boot sector binary, < 1 KB) loaded
    // directly to 0000:7C00 + entry point set. Coexists with
    // --floppy-a (which now mounts a real .img to drive 0x00) so test
    // fixtures can exercise INT 13h against a real floppy.
    public string? TestRomPath  { get; set; }

    // System config.
    public string  Cpu          { get; set; } = "i8086";
    public string? BiosPath     { get; set; }
    /// <summary>
    /// Phase 30.12 — option ROM image (Video BIOS / network BIOS / etc.)
    /// loaded into the C0000-DE000 expansion-ROM region. The IBM PC POST
    /// scans this region in 2 KB increments looking for the 0x55 0xAA
    /// signature; on hit it FAR CALLs offset 3 of the ROM so its init
    /// stub can hook INT 10h / install vectors / probe its hardware.
    ///
    /// Initial use case: <c>BIOS/firmware/videorom.bin</c> (Tseng Labs
    /// ET4000 32 KB VGA BIOS, 1992 V8.02X). Loading the ROM is
    /// independent of whether we actually emulate the VGA hardware it
    /// talks to via I/O ports 0x3C0-0x3DF — the smoke test exists to
    /// expose what the ROM probes for, before deciding whether to commit
    /// to full VGA register / framebuffer emulation.
    /// </summary>
    public string? VideoBiosPath { get; set; }
    public string  Memory       { get; set; } = "640k";
    public string  Backend      { get; set; } = "json-block";
    /// <summary>
    /// "lle" (default): the BIOS reset vector contains a real 8086
    /// bootstrap routine at F000:E05B that does INT 13h read + far
    /// JMP to 0:7C00. CPU executes real instructions for every step.
    /// "hle": the reset vector is the 2-byte INT 19h opcode; the HLE
    /// INT 19h handler synthesizes the boot-sector load + CS:IP
    /// redirect via SimulateIret-skip. Functionally equivalent;
    /// useful for comparing the two paths or when debugging the LLE
    /// bootstrap itself.
    /// </summary>
    public string  BiosMode     { get; set; } = "lle";

    // UI config.
    public int     WindowScale  { get; set; } = 2;
    public string  WindowTitle  { get; set; } = "AprPc";
    public bool    Fullscreen   { get; set; }

    // Headless / CI mode.
    public bool    Headless     { get; set; }
    /// <summary>
    /// Phase 30.15b — `--lockstep-bjit` runs two CPU envs side-by-side
    /// (per-instr A vs block-JIT B with block size = 1) and reports the
    /// first architectural-state divergence. Tool for hunting JIT bugs
    /// — not a normal runtime mode. See MD/design/30.15-blockjit-pc-
    /// investigation.md.
    /// </summary>
    public bool    LockstepBjit { get; set; }

    /// <summary>
    /// Phase 30.15d sprint 5.4 — `--verify-blocks` runs the per-block
    /// VerifiedBlockJitRunner instead of CPU-state-only lockstep.
    /// For each JIT-compiled block, the framework re-runs the same
    /// instruction count through an interp CPU starting from the
    /// captured pre-block snapshot, then 3-axis diffs:
    ///   - CPU state at block exit
    ///   - Per-block memory write trace
    ///   - Per-block port-write + IRQ-assert log
    /// Reports FIRST divergence with block linearPc + instr count.
    /// </summary>
    public bool    VerifyBlocks { get; set; }
    public string? ScreenshotPath { get; set; }
    public long?   MaxCycles    { get; set; }
    public long?   Frames       { get; set; }
    public string? KeysScript   { get; set; }
    public int?    HeadlessTimeout { get; set; }

    // Debug.
    public bool    TraceInt     { get; set; }
    public bool    TraceIo      { get; set; }
    public bool    TraceIrq     { get; set; }
    public bool    TraceCpu     { get; set; }
    public int?    TraceCpuMax  { get; set; }
    // Phase 30 debug — only trace CPU steps when CS == one of these values
    // (comma-separated hex list, e.g. --trace-cpu-cs=0000,1FE0). Without
    // this filter, --trace-cpu floods with BIOS POST instructions.
    public ushort[]? TraceCpuCs  { get; set; }
    // Phase 30 debug — log every memory write landing in the given range.
    // Format: --watch-mem=LO:HI (hex). Example: --watch-mem=27A00:27B00
    // logs writes to the relocated boot sector area.
    public uint WatchMemLo { get; set; }
    public uint WatchMemHi { get; set; }
    public uint ReadWatchLo { get; set; }
    public uint ReadWatchHi { get; set; }
    public bool    Verbose      { get; set; }

    /// <summary>
    /// PIT channel 0 tick rate in Hz. Real IBM PC default is 18.2 Hz
    /// (= 1.193182 MHz / 65536, 55ms per tick). For interactive demo
    /// use with real BIOS, the 55ms tick floor dominates because BIOS
    /// INT 13h / 16h wait loops HLT until the next tick. Bumping to
    /// 100-200 Hz (5-10ms per tick) makes the system feel responsive
    /// at the cost of time-of-day drift -- BDA tick counter advances
    /// faster than wall clock so INT 1Ah AH=00 will report wrong time.
    /// Acceptable for interactive development; pin to 18 for time-
    /// sensitive workloads.
    /// </summary>
    public int     PitRateHz    { get; set; } = 18;

    /// <summary>
    /// Video adapter to report via the 8255 PPI DIP-switch bits at port
    /// 0x62. pcxtbios.bin reads these to populate the BIOS equipment
    /// flag and decides whether to talk to MDA CRTC (port 0x3B4/0x3B5,
    /// VRAM 0xB0000) or CGA CRTC (port 0x3D4/0x3D5, VRAM 0xB8000).
    ///
    /// "mda" -> 80x25 monochrome, attribute 0x07 visible by default
    /// "cga" -> 80x25 color (mode 3), 16-color palette
    ///
    /// Default mda because MDA's stable text mode + light-gray-on-black
    /// is easier to read in the WinForms framebuffer renderer.
    /// </summary>
    public string  Video        { get; set; } = "mda";

    /// <summary>
    /// Optional scripted GUI integration test. When set, MainForm spins
    /// up an AutoTester that polls the framebuffer every 3 seconds,
    /// matches pre-defined prompts (language menu, installer Y/N, A:\>),
    /// injects the right scancodes, and finally dumps the screen to
    /// kbd-trace.log and closes the form. Used for end-to-end real-BIOS
    /// + FreeDOS bring-up testing without an operator.
    ///
    /// Built-in sequences (see Diagnostics/AutoTester.cs):
    ///   freedos-mda-dir — full FreeDOS boot to A:\> then run dir
    /// </summary>
    public string? AutoTest     { get; set; }

    /// <summary>
    /// Parse argv; throws <see cref="ArgumentException"/> for unknown
    /// flags / malformed values. The Program.cs caller catches and
    /// prints a usage block.
    /// </summary>
    public static PcOptions Parse(string[] argv)
    {
        var o = new PcOptions();
        foreach (var arg in argv)
        {
            if (arg == "--help" || arg == "-h" || arg == "/?")
                throw new HelpRequestedException();

            // No-value flags first.
            if      (arg == "--fullscreen")  o.Fullscreen = true;
            else if (arg == "--headless")    o.Headless = true;
            else if (arg == "--lockstep-bjit") o.LockstepBjit = true;
            else if (arg == "--verify-blocks") o.VerifyBlocks = true;
            else if (arg == "--trace-int")   o.TraceInt = true;
            else if (arg == "--trace-io")    o.TraceIo = true;
            else if (arg == "--trace-irq")   o.TraceIrq = true;
            else if (arg == "--trace-cpu")   o.TraceCpu = true;
            else if (arg.StartsWith("--trace-cpu-max=")) o.TraceCpuMax = int.Parse(arg["--trace-cpu-max=".Length..]);
            else if (arg.StartsWith("--trace-cpu-cs="))
            {
                var list = arg["--trace-cpu-cs=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
                o.TraceCpuCs = list.Select(s => Convert.ToUInt16(s, 16)).ToArray();
            }
            else if (arg.StartsWith("--watch-mem="))
            {
                var parts = arg["--watch-mem=".Length..].Split(':');
                o.WatchMemLo = Convert.ToUInt32(parts[0], 16);
                o.WatchMemHi = Convert.ToUInt32(parts[1], 16);
            }
            else if (arg.StartsWith("--watch-read="))
            {
                var parts = arg["--watch-read=".Length..].Split(':');
                o.ReadWatchLo = Convert.ToUInt32(parts[0], 16);
                o.ReadWatchHi = Convert.ToUInt32(parts[1], 16);
            }
            else if (arg == "--verbose")     o.Verbose = true;
            // Value flags --key=value.
            else if (arg.StartsWith("--floppy-a="))   o.FloppyAPaths = ParseFloppyList(arg["--floppy-a=".Length..]);
            else if (arg.StartsWith("--floppy-b="))   o.FloppyBPaths = ParseFloppyList(arg["--floppy-b=".Length..]);
            else if (arg.StartsWith("--hdd="))        o.HddPath = arg["--hdd=".Length..];
            else if (arg.StartsWith("--hdd2="))       o.Hdd2Path = arg["--hdd2=".Length..];
            else if (arg.StartsWith("--mount="))      o.HostMounts.Add(arg["--mount=".Length..]);
            else if (arg.StartsWith("--test-rom="))   o.TestRomPath = arg["--test-rom=".Length..];
            else if (arg.StartsWith("--cpu="))        o.Cpu = arg["--cpu=".Length..];
            else if (arg.StartsWith("--bios="))       o.BiosPath = arg["--bios=".Length..];
            else if (arg.StartsWith("--video-bios=")) o.VideoBiosPath = arg["--video-bios=".Length..];
            else if (arg.StartsWith("--memory="))     o.Memory = arg["--memory=".Length..];
            else if (arg.StartsWith("--backend="))    o.Backend = arg["--backend=".Length..];
            else if (arg.StartsWith("--bios-mode="))  o.BiosMode = arg["--bios-mode=".Length..];
            else if (arg.StartsWith("--window-scale=")) o.WindowScale = int.Parse(arg["--window-scale=".Length..]);
            else if (arg.StartsWith("--window-title=")) o.WindowTitle = arg["--window-title=".Length..];
            else if (arg.StartsWith("--screenshot="))  o.ScreenshotPath = arg["--screenshot=".Length..];
            else if (arg.StartsWith("--max-cycles="))  o.MaxCycles = long.Parse(arg["--max-cycles=".Length..]);
            else if (arg.StartsWith("--frames="))      o.Frames = long.Parse(arg["--frames=".Length..]);
            else if (arg.StartsWith("--keys="))        o.KeysScript = arg["--keys=".Length..];
            else if (arg.StartsWith("--headless-timeout=")) o.HeadlessTimeout = int.Parse(arg["--headless-timeout=".Length..]);
            else if (arg.StartsWith("--pit-rate-hz="))      o.PitRateHz = int.Parse(arg["--pit-rate-hz=".Length..]);
            else if (arg.StartsWith("--video="))            o.Video = arg["--video=".Length..].ToLowerInvariant();
            else if (arg.StartsWith("--auto-test="))        o.AutoTest = arg["--auto-test=".Length..];
            else throw new ArgumentException($"unknown argument: {arg}");
        }

        // Validate CPU model — accepted values mirror AprX86.Cli's variant set.
        if (o.Cpu is not ("i8086" or "i8088" or "i80186" or "i80188" or "i80286"))
            throw new ArgumentException($"--cpu={o.Cpu} not supported; expected i8086/i8088/i80186/i80188/i80286");

        if (o.Backend is not ("legacy" or "json" or "json-block"))
            throw new ArgumentException($"--backend={o.Backend} not supported; expected legacy/json/json-block");

        if (o.BiosMode is not ("lle" or "hle"))
            throw new ArgumentException($"--bios-mode={o.BiosMode} not supported; expected hle/lle");

        if (o.Video is not ("mda" or "cga"))
            throw new ArgumentException($"--video={o.Video} not supported; expected mda/cga");

        if (o.WindowScale is < 1 or > 8)
            throw new ArgumentException($"--window-scale={o.WindowScale} out of range (1-8)");

        // Cross-flag sanity.
        if (o.Headless && o.Fullscreen)
            throw new ArgumentException("--headless and --fullscreen are mutually exclusive");

        return o;
    }

    /// <summary>
    /// Split a comma-separated --floppy-a / --floppy-b value into a list
    /// of image paths. Trims whitespace, skips empty segments, preserves
    /// path order (the first entry is the initial mount; later entries
    /// are swap targets reachable via Ctrl+L or "INSERT A N" in headless).
    /// </summary>
    internal static IReadOnlyList<string> ParseFloppyList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? Array.Empty<string>() : parts;
    }

    public static string UsageText() => """
        apr-pc — AprGba's Intel PC emulator (Phase 28 scaffolding)

        usage:
          apr-pc [options]

        # Disk inputs
          --floppy-a=PATH[,P2,P3...] A: floppy image (.img, 1.44MB / 720KB / 360KB).
                                    Multiple comma-separated paths enable hot-swap
                                    for multi-disk software (CheckIt 2-disk,
                                    FreeDOS install 4-disk, etc.). Switch via
                                    Ctrl+L (GUI) or "INSERT A N\n" on stdin (headless).
          --floppy-b=PATH[,P2,P3...] B: floppy image (.img). Common pattern:
                                    keep --floppy-a=freedos-boot.img read-only,
                                    put your test .COM / .EXE on B: built with
                                    mtools / DiscUtils so each test run picks
                                    up the latest binaries without touching
                                    the boot disk.
          --hdd=PATH                C: hard disk image (.img). Geometry auto-detected
                                    from file size: 10/20/32/40-504 MB use canonical
                                    CHS tables; arbitrary sizes use S=63 H=16 fallback.
                                    Use FDISK + FORMAT + SYS to populate, then boot
                                    from C: with no --floppy-a.
          --hdd2=PATH               D: secondary hard disk (FDC drive 0x81).
          --mount=DRV:host[:opts]   Phase 32.3 — host-directory mount as a virtual
                                    FAT16 disk. opts: ro|rw (default ro), SIZE_MB
                                    (default 32). E.g. --mount=E:.\dos-stuff:rw:128
                                    SKELETON ONLY in this build — parse + accept
                                    spec but synthesizer is sprint 32.3a (see plan
                                    MD/issue/pc/hdd-mount-swap-plan.md).
          --test-rom=PATH           tiny boot-sector binary loaded directly
                                    to 0000:7C00 (coexists with --floppy-a)

        # System config (all have defaults)
          --cpu=i8086|i8088|i80186|i80188|i80286   [default: i8086]
          --bios=PATH               real BIOS image (LLE); omit for HLE mode
          --video-bios=PATH         option ROM image (e.g. videorom.bin, VGA
                                    Tseng ET4000 BIOS). Loaded at 0xC0000 so
                                    pcxtbios POST detects it and FAR-CALLs
                                    its init at offset 3. Phase 30.12 smoke
                                    test — actual VGA register emulation is
                                    not yet implemented, so the init code
                                    will likely hang on the first I/O probe.
          --memory=640k|1m          conventional RAM size            [default: 640k]
          --backend=json|json-block|legacy         [default: json-block]
          --bios-mode=lle|hle                      [default: lle]
                                    lle = real 8086 bootstrap routine at F000:E05B
                                    hle = HLE INT 19h handler
                                    (--bios=PATH loads a real BIOS image and
                                     supersedes both modes)

        # UI
          --window-scale=N          1-8 framebuffer pixel-doubling   [default: 2]
          --window-title="..."      main window caption              [default: "AprPc"]
          --fullscreen              start fullscreen

        # Headless / CI mode
          --headless                no UI window
          --screenshot=PATH         output PNG (works with --headless)
          --max-cycles=N            halt after N cycles
          --frames=N                halt after N frames
          --keys="text\r..."        keystroke script (CI-friendly)

        # Debug
          --trace-int               log every INT instruction (vector + AH)
          --trace-io                log every IN/OUT port + value
          --trace-irq               log every PIC IRQ delivery
          --verbose                 print full system config at start

        examples:
          apr-pc --floppy-a=BIOS/freedos-1.3-floppy.img
          apr-pc --hdd=disks/c.img --cpu=i80286
          apr-pc --floppy-a=BIOS/freedos-1.3-floppy.img --window-scale=3 --trace-int
          apr-pc --floppy-a=BIOS/test.img --headless --screenshot=temp/out.png --max-cycles=10000000
        """;
}

public sealed class HelpRequestedException : Exception { }
