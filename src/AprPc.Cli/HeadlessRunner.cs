// HeadlessRunner — `--headless` execution path. No UI window.
//
// Phase 28.0: prints the resolved config + runs the (placeholder)
// emulator thread for `--max-cycles` ticks or `--frames` frames, then
// optionally writes a screenshot PNG and exits. This keeps CI / smoke
// tests independent of WinForms entirely.
//
// 28.2+ will plug in the real CGA framebuffer → PNG path via the
// existing AprX86.Cli.Video.X86CgaRenderer.

namespace AprPc.Cli;

internal static class HeadlessRunner
{
    public static int Run(PcOptions opts, PcSystemRunner runner)
    {
        Console.WriteLine("apr-pc (headless mode, Phase 28.0 scaffolding)");
        Console.WriteLine($"  cpu      = {opts.Cpu}");
        Console.WriteLine($"  backend  = {opts.Backend}");
        Console.WriteLine($"  floppy A = {opts.FloppyAPath ?? "(none)"}");
        Console.WriteLine($"  hdd      = {opts.HddPath ?? "(none)"}");

        runner.Start();

        // Limit driver — max-cycles in instruction count; frames not yet
        // meaningful in 28.0 (no PIT). Defaults to 100 ticks so this
        // command exits cleanly even without a limit.
        long limit = opts.MaxCycles ?? 100;
        long startCount = runner.InstructionsExecuted;
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (runner.InstructionsExecuted - startCount < limit)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Console.Error.WriteLine("apr-pc: headless timeout (30s)");
                runner.Stop();
                return 4;
            }
            Thread.Sleep(20);
        }

        runner.Stop();

        Console.WriteLine($"  executed: {runner.InstructionsExecuted - startCount} placeholder ticks");

        if (opts.ScreenshotPath is { } ssPath)
        {
            // Phase 28.0 has no real framebuffer rendering yet — just
            // touch the file so CI plumbing can verify the path was
            // honoured. 28.2 replaces this with the CGA → PNG path.
            File.WriteAllBytes(ssPath, Array.Empty<byte>());
            Console.WriteLine($"  screenshot: {ssPath} (empty stub; 28.2 wires real CGA → PNG)");
        }

        return 0;
    }
}
