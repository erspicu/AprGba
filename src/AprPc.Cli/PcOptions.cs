// PcOptions — parsed CLI options for apr-pc.
//
// Phase 28.0: minimum-viable shape. Some fields are declared but unused
// until later sub-phases (e.g. FloppyAPath consumed in 28.5).

namespace AprPc.Cli;

public sealed class PcOptions
{
    // Disk inputs (at least one expected once 28.5+ is implemented;
    // empty in scaffolding mode → UI opens with no disk).
    public string? FloppyAPath  { get; set; }
    public string? HddPath      { get; set; }

    // Phase 28.5 — test ROM (tiny boot sector binary, < 1 KB) loaded
    // directly to 0000:7C00 + entry point set. Coexists with
    // --floppy-a (which now mounts a real .img to drive 0x00) so test
    // fixtures can exercise INT 13h against a real floppy.
    public string? TestRomPath  { get; set; }

    // System config.
    public string  Cpu          { get; set; } = "i8086";
    public string? BiosPath     { get; set; }
    public string  Memory       { get; set; } = "640k";
    public string  Backend      { get; set; } = "json-block";

    // UI config.
    public int     WindowScale  { get; set; } = 2;
    public string  WindowTitle  { get; set; } = "AprPc";
    public bool    Fullscreen   { get; set; }

    // Headless / CI mode.
    public bool    Headless     { get; set; }
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
    public bool    Verbose      { get; set; }

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
            else if (arg == "--trace-int")   o.TraceInt = true;
            else if (arg == "--trace-io")    o.TraceIo = true;
            else if (arg == "--trace-irq")   o.TraceIrq = true;
            else if (arg == "--trace-cpu")   o.TraceCpu = true;
            else if (arg.StartsWith("--trace-cpu-max=")) o.TraceCpuMax = int.Parse(arg["--trace-cpu-max=".Length..]);
            else if (arg == "--verbose")     o.Verbose = true;
            // Value flags --key=value.
            else if (arg.StartsWith("--floppy-a="))   o.FloppyAPath = arg["--floppy-a=".Length..];
            else if (arg.StartsWith("--hdd="))        o.HddPath = arg["--hdd=".Length..];
            else if (arg.StartsWith("--test-rom="))   o.TestRomPath = arg["--test-rom=".Length..];
            else if (arg.StartsWith("--cpu="))        o.Cpu = arg["--cpu=".Length..];
            else if (arg.StartsWith("--bios="))       o.BiosPath = arg["--bios=".Length..];
            else if (arg.StartsWith("--memory="))     o.Memory = arg["--memory=".Length..];
            else if (arg.StartsWith("--backend="))    o.Backend = arg["--backend=".Length..];
            else if (arg.StartsWith("--window-scale=")) o.WindowScale = int.Parse(arg["--window-scale=".Length..]);
            else if (arg.StartsWith("--window-title=")) o.WindowTitle = arg["--window-title=".Length..];
            else if (arg.StartsWith("--screenshot="))  o.ScreenshotPath = arg["--screenshot=".Length..];
            else if (arg.StartsWith("--max-cycles="))  o.MaxCycles = long.Parse(arg["--max-cycles=".Length..]);
            else if (arg.StartsWith("--frames="))      o.Frames = long.Parse(arg["--frames=".Length..]);
            else if (arg.StartsWith("--keys="))        o.KeysScript = arg["--keys=".Length..];
            else if (arg.StartsWith("--headless-timeout=")) o.HeadlessTimeout = int.Parse(arg["--headless-timeout=".Length..]);
            else throw new ArgumentException($"unknown argument: {arg}");
        }

        // Validate CPU model — accepted values mirror AprX86.Cli's variant set.
        if (o.Cpu is not ("i8086" or "i8088" or "i80186" or "i80188" or "i80286"))
            throw new ArgumentException($"--cpu={o.Cpu} not supported; expected i8086/i8088/i80186/i80188/i80286");

        if (o.Backend is not ("legacy" or "json" or "json-block"))
            throw new ArgumentException($"--backend={o.Backend} not supported; expected legacy/json/json-block");

        if (o.WindowScale is < 1 or > 8)
            throw new ArgumentException($"--window-scale={o.WindowScale} out of range (1-8)");

        // Cross-flag sanity.
        if (o.Headless && o.Fullscreen)
            throw new ArgumentException("--headless and --fullscreen are mutually exclusive");

        return o;
    }

    public static string UsageText() => """
        apr-pc — AprGba's Intel PC emulator (Phase 28 scaffolding)

        usage:
          apr-pc [options]

        # Disk inputs
          --floppy-a=PATH           A: floppy image (.img, 1.44MB / 720KB / 360KB)
          --hdd=PATH                C: hard disk image (.img, FAT12/16 partition)
          --test-rom=PATH           tiny boot-sector binary loaded directly
                                    to 0000:7C00 (coexists with --floppy-a)

        # System config (all have defaults)
          --cpu=i8086|i8088|i80186|i80188|i80286   [default: i8086]
          --bios=PATH               real BIOS image (LLE); omit for HLE mode
          --memory=640k|1m          conventional RAM size            [default: 640k]
          --backend=json|json-block|legacy         [default: json-block]

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
