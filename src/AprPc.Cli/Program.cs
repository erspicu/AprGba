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
    Console.WriteLine($"  floppy A = {opts.FloppyAPath ?? "(none)"}");
    Console.WriteLine($"  hdd      = {opts.HddPath ?? "(none)"}");
    Console.WriteLine($"  scale    = {opts.WindowScale}×{(opts.Fullscreen ? " (fullscreen)" : "")}");
    Console.WriteLine($"  mode     = {(opts.Headless ? "headless" : "UI")}");
}

using var runner = new PcSystemRunner(opts);

if (opts.Headless)
{
    return HeadlessRunner.Run(opts, runner);
}

// UI mode — standard WinForms message pump on the main thread.
ApplicationConfiguration.Initialize();
runner.Start();
Application.Run(new MainForm(opts, runner));
return 0;
