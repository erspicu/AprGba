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
        Console.WriteLine("apr-pc (headless mode)");
        Console.WriteLine($"  cpu      = {opts.Cpu}");
        Console.WriteLine($"  backend  = {opts.Backend}");
        Console.WriteLine($"  floppy A = {opts.FloppyAPath ?? "(none)"}");
        Console.WriteLine($"  hdd      = {opts.HddPath ?? "(none)"}");

        // Start() initializes the CPU + bus but leaves the emulator
        // thread Paused. We wire up any test ROM, then Resume.
        runner.Start();

        if (opts.FloppyAPath is { } floppyPath)
        {
            var bytes = File.ReadAllBytes(floppyPath);
            runner.LoadTestRom(bytes, segment: 0x0000, offset: 0x7C00);
            Console.WriteLine($"  loaded:   {bytes.Length} bytes -> 0000:7C00");
        }

        runner.Resume();

        // Limit driver — max-cycles in CPU instructions. Default 1M
        // gives BDA-read fixture plenty of room without infinite-looping
        // if the ROM never HLTs.
        long limit = opts.MaxCycles ?? 1_000_000;
        long startCount = runner.InstructionsExecuted;
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (runner.InstructionsExecuted - startCount < limit)
        {
            if (runner.Cpu is { Halted: true }) break;
            if (DateTime.UtcNow >= deadline)
            {
                Console.Error.WriteLine("apr-pc: headless timeout (30s)");
                runner.Stop();
                return 4;
            }
            Thread.Sleep(5);
        }

        // Snapshot register state before stopping (post-stop the CPU
        // pointer may still be valid but it's neater to read while
        // running).
        var state = runner.Cpu?.State;
        runner.Stop();

        long executed = runner.InstructionsExecuted - startCount;
        Console.WriteLine($"  executed: {executed} CPU instructions");
        Console.WriteLine($"  halted:   {runner.Cpu?.Halted ?? false}");
        if (state is not null)
        {
            Console.WriteLine($"    CS:IP={state.CS:X4}:{state.IP:X4} AX={state.A.X:X4} BX={state.B.X:X4} CX={state.C.X:X4} DX={state.D.X:X4}");
            Console.WriteLine($"    DS={state.DS:X4} ES={state.ES:X4} SS={state.SS:X4} SP={state.SP:X4} BP={state.BP:X4}");
        }

        if (opts.ScreenshotPath is { } ssPath)
        {
            // Phase 28.1 still has no real CGA renderer wired through
            // PcMemoryBus → PNG (28.2). Write an empty stub so CI
            // plumbing can verify the path was honoured.
            File.WriteAllBytes(ssPath, Array.Empty<byte>());
            Console.WriteLine($"  screenshot: {ssPath} (empty stub; 28.2 wires real CGA → PNG)");
        }

        return 0;
    }
}
