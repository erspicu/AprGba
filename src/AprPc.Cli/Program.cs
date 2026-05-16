// AprPc.Cli — entry point.
//
// Phase 28.0 dispatch:
//   --help / -h / /?     → print usage and exit 0
//   --headless           → HeadlessRunner.Run (no UI window)
//   default              → AprPc.Cli.Ui.MainForm via standard Application.Run
//
// argv parsing lives in PcOptions.Parse. Cross-cutting validation
// (mutually-exclusive flags, recognized CPU/backend values) also there.

using AprPc.Cli;
using AprPc.Cli.Diagnostics;
using AprPc.Cli.Hardware;
using AprPc.Cli.Ui;
using System.Windows.Forms;

PcOptions opts;
try
{
    opts = PcOptions.Parse(args);
}
catch (HelpRequestedException)
{
    Console.WriteLine(PcOptions.UsageText());
    return 0;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"apr-pc: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(PcOptions.UsageText());
    return 2;
}

if (opts.Verbose)
{
    Console.WriteLine($"apr-pc starting:");
    Console.WriteLine($"  cpu      = {opts.Cpu}");
    Console.WriteLine($"  backend  = {opts.Backend}");
    Console.WriteLine($"  memory   = {opts.Memory}");
    Console.WriteLine($"  bios     = {opts.BiosPath ?? "(HLE)"}");
    Console.WriteLine($"  bios-mode= {opts.BiosMode}");
    Console.WriteLine($"  floppy A = {opts.FloppyAPath ?? "(none)"}");
    Console.WriteLine($"  hdd      = {opts.HddPath ?? "(none)"}");
    Console.WriteLine($"  video    = {opts.Video,-3} ({(opts.Video == "mda" ? "MDA mono 80x25, framebuffer 0xB0000, CRTC 0x3B4/0x3B5" : "CGA color 80x25, framebuffer 0xB8000, CRTC 0x3D4/0x3D5")})");
    Console.WriteLine($"  pit      = {opts.PitRateHz} Hz");
    Console.WriteLine($"  scale    = {opts.WindowScale}×{(opts.Fullscreen ? " (fullscreen)" : "")}");
    Console.WriteLine($"  mode     = {(opts.Headless ? "headless" : "UI")}");
}

// Always-on keyboard trace -- low volume (only fires on KeyPress + port 0x60
// read + IRQ 1), so it's safe to leave enabled. Cleared each launch.
KbdTrace.Init("temp/kbd-trace.log");

using var runner = new PcSystemRunner(opts);

if (opts.Headless)
{
    return HeadlessRunner.Run(opts, runner);
}

// UI mode — standard WinForms message pump on the main thread.
ApplicationConfiguration.Initialize();
runner.Start();

// Mount disk images BEFORE Resume() so real-BIOS INT 19h (which boots
// the moment Resume() starts the CPU thread) can find the floppy.
// Mirrors HeadlessRunner's mount sequence; without this real-BIOS POST
// hits FDC NRDY immediately on boot attempt.
if (opts.FloppyAPath is { } floppyPath)
{
    var size = new FileInfo(floppyPath).Length;
    if (size >= 64 * 1024)
    {
        var disk = DiskImage.LoadFloppy(floppyPath);
        runner.MountDisk(0x00, disk);
    }
    else
    {
        var bytes = File.ReadAllBytes(floppyPath);
        runner.LoadTestRom(bytes, segment: 0x0000, offset: 0x7C00);
    }
}
if (opts.HddPath is { } hddPath)
{
    var disk = DiskImage.LoadHardDisk(hddPath);
    runner.MountDisk(0x80, disk);
}

runner.Resume();
Application.Run(new MainForm(opts, runner));
return 0;
