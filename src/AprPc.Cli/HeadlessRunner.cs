// HeadlessRunner — `--headless` execution path. No UI window.
//
// Prints the resolved config, optionally loads a flat ROM into RAM
// at 0000:7C00, runs the CPU until --max-cycles is hit or it HLTs,
// dumps register state, and optionally writes a CGA framebuffer PNG.

using AprX86.Cli.Video;

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

        if (opts.ScreenshotPath is { } ssPath && runner.Bus is { } bus)
        {
            // Phase 28.2 — real CGA framebuffer → PNG via the existing
            // AprX86.Cli.Video.X86CgaRenderer.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ssPath)) ?? ".");
                X86CgaRenderer.Render(bus.Memory.Ram, ssPath);
                Console.WriteLine($"  screenshot: {ssPath} ({X86CgaRenderer.ImgW}×{X86CgaRenderer.ImgH} PNG)");
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine($"  screenshot: skipped — {ex.Message}");
            }
        }

        return 0;
    }
}
