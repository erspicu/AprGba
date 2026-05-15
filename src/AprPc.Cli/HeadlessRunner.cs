// HeadlessRunner — `--headless` execution path. No UI window.
//
// Prints the resolved config, optionally mounts disk images via
// --floppy-a / --hdd, optionally loads a tiny test ROM at 0000:7C00
// via --test-rom, runs the CPU until --max-cycles is hit or it HLTs,
// dumps register state, and optionally writes a CGA framebuffer PNG.

using AprPc.Cli.Hardware;
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

        // Phase 28.5 — distinguish disk image (≥ 64 KB) from tiny
        // test ROM. Test ROMs (28.1-28.4 demos) are < 1 KB and get
        // loaded straight to 0:7C00 for direct execution; floppy
        // images are mounted as drive 0x00 and accessed via INT 13h.
        // A user can pass --test-rom + --floppy-a together so the
        // small fixture can exercise INT 13h against a real image.
        if (opts.FloppyAPath is { } floppyPath)
        {
            var size = new FileInfo(floppyPath).Length;
            if (size >= 64 * 1024)
            {
                var disk = DiskImage.LoadFloppy(floppyPath);
                runner.MountDisk(0x00, disk);
                Console.WriteLine($"  floppy A: {floppyPath} ({size / 1024} KB, " +
                    $"{disk.Cylinders}x{disk.Heads}x{disk.Sectors} CHS)");
            }
            else
            {
                var bytes = File.ReadAllBytes(floppyPath);
                runner.LoadTestRom(bytes, segment: 0x0000, offset: 0x7C00);
                Console.WriteLine($"  loaded:   {bytes.Length} bytes -> 0000:7C00 (legacy --floppy-a as test ROM)");
            }
        }

        if (opts.HddPath is { } hddPath)
        {
            var disk = DiskImage.LoadHardDisk(hddPath);
            runner.MountDisk(0x80, disk);
            Console.WriteLine($"  hdd:      {hddPath} ({disk.TotalSectors * 512L / 1024} KB)");
        }

        if (opts.TestRomPath is { } testRomPath)
        {
            var bytes = File.ReadAllBytes(testRomPath);
            runner.LoadTestRom(bytes, segment: 0x0000, offset: 0x7C00);
            Console.WriteLine($"  test ROM: {bytes.Length} bytes -> 0000:7C00");
        }

        // Phase 28.3 — pre-load the keyboard buffer with scripted keys.
        // Useful for CI: --keys="hi\r" prints "hi" through an INT 16h
        // read loop. Honours \r / \n / \t / \\ / \e (ESC) escapes.
        if (opts.KeysScript is { } script && runner.Keyboard is { } kbd)
        {
            int injected = 0;
            foreach (var (ascii, scancode) in ExpandKeyScript(script))
            {
                if (!kbd.Enqueue(ascii, scancode)) break;
                injected++;
            }
            Console.WriteLine($"  keys:     {injected} keystrokes preloaded into buffer");
        }

        runner.Resume();

        // Limit driver — max-cycles in CPU instructions. Default 1M
        // gives BDA-read fixture plenty of room without infinite-looping
        // if the ROM never HLTs.
        long limit = opts.MaxCycles ?? 1_000_000;
        long startCount = runner.InstructionsExecuted;
        // Phase 28.8b/d — FreeDOS boot is slow on our HLE path. Default
        // scales by max-cycles (200ms per 1M cycles, minimum 30s).
        // --headless-timeout=N overrides explicitly for very large
        // runs (FreeDOS interactive boot can take several minutes).
        int deadlineSeconds = opts.HeadlessTimeout
            ?? Math.Max(30, (int)Math.Min(int.MaxValue, limit / 200_000));
        var deadline = DateTime.UtcNow.AddSeconds(deadlineSeconds);

        while (runner.InstructionsExecuted - startCount < limit)
        {
            if (runner.Cpu is { Halted: true }) break;
            if (DateTime.UtcNow >= deadline)
            {
                // Phase 28.IO — still snapshot a screenshot before
                // bailing out, so timeout-bound regression captures
                // are usable (FreeDOS interactive boot routinely
                // overshoots the wall-clock budget but the framebuffer
                // is steady well before that point).
                Console.Error.WriteLine($"apr-pc: headless timeout ({deadlineSeconds}s)");
                if (opts.ScreenshotPath is { } ssTimeoutPath && runner.Bus is { } busTimeout)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ssTimeoutPath)) ?? ".");
                        X86CgaRenderer.Render(busTimeout.Memory.Ram, ssTimeoutPath);
                        Console.WriteLine($"  screenshot: {ssTimeoutPath} ({X86CgaRenderer.ImgW}×{X86CgaRenderer.ImgH} PNG, captured at timeout)");
                    }
                    catch (FileNotFoundException ex)
                    {
                        Console.Error.WriteLine($"  screenshot: skipped — {ex.Message}");
                    }
                }
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
            // Phase 28.8c — dump bytes around final CS:IP for forensics.
            if (runner.Bus is { } b)
            {
                int linear = ((state.CS << 4) + state.IP) & 0xFFFFF;
                int from = Math.Max(0, linear - 32);
                Console.Write($"    bytes @ phys 0x{from:X5}: ");
                for (int i = from; i < from + 64 && i < 0x100000; i++)
                    Console.Write($"{b.ReadByte(i):X2}{(i == linear ? "*" : " ")}");
                Console.WriteLine();

                // Phase 30 forensics — compare original boot sector at
                // 0x07C00 vs relocated copy at 0x27A00 (1FE0:7C00).
                Console.WriteLine($"    orig boot first 32 bytes (0x07C00):");
                Console.WriteLine($"      {string.Join(" ", Enumerable.Range(0, 32).Select(i => b.ReadByte(0x07C00 + i).ToString("X2")))}");
                Console.WriteLine($"    copy boot first 32 bytes (0x27A00):");
                Console.WriteLine($"      {string.Join(" ", Enumerable.Range(0, 32).Select(i => b.ReadByte(0x27A00 + i).ToString("X2")))}");
                // Diff count over full 512 bytes
                int diffs = 0;
                int firstDiffOff = -1;
                for (int off = 0; off < 512; off++)
                {
                    if (b.ReadByte(0x07C00 + off) != b.ReadByte(0x27A00 + off))
                    {
                        diffs++;
                        if (firstDiffOff < 0) firstDiffOff = off;
                    }
                }
                Console.WriteLine($"    orig vs copy diff: {diffs} bytes differ (first at offset 0x{firstDiffOff:X3})");
            }
        }
        if (runner.Pit is { } pit)
        {
            Console.WriteLine($"  PIT tick:   {pit.Ticks} ({pit.Ticks * 55} ms wall-clock equivalent)");
        }

        if (opts.ScreenshotPath is { } ssPath && runner.Bus is { } bus2)
        {
            // Phase 28.2 — real CGA framebuffer → PNG via the existing
            // AprX86.Cli.Video.X86CgaRenderer. Phase 29-supp — renderer
            // auto-detects MDA vs CGA based on which framebuffer has
            // printable content (real BIOS POST writes MDA 0xB0000).
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ssPath)) ?? ".");
                int fbBase = X86CgaRenderer.PickFramebufferBase(bus2.Memory.Ram);
                X86CgaRenderer.Render(bus2.Memory.Ram, ssPath);
                Console.WriteLine($"  screenshot: {ssPath} ({X86CgaRenderer.ImgW}×{X86CgaRenderer.ImgH} PNG, fb=0x{fbBase:X5} {(fbBase == 0xB0000 ? "MDA" : "CGA")})");

                // Phase 29-supp — also dump the first 3 rows of the
                // picked framebuffer as ASCII (printable chars + dots
                // for non-printable) so trace logs show what the BIOS
                // actually drew. Useful when running headless and you
                // can't view the PNG.
                Console.WriteLine($"  text preview (first 3 rows):");
                for (int row = 0; row < 3; row++)
                {
                    var sb = new System.Text.StringBuilder("    | ");
                    for (int col = 0; col < 80; col++)
                    {
                        byte ch = bus2.Memory.Ram[fbBase + (row * 80 + col) * 2];
                        sb.Append(ch >= 0x20 && ch < 0x7F ? (char)ch : '.');
                    }
                    sb.Append(" |");
                    Console.WriteLine(sb.ToString());
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine($"  screenshot: skipped — {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Expand a --keys=... script into (ascii, scancode) pairs.
    /// Supported escapes: \r (Enter), \n (LF), \t (Tab), \b (BS),
    /// \e (Esc), \\ (literal backslash). Other backslash sequences
    /// are passed through verbatim.
    /// </summary>
    private static IEnumerable<(byte ascii, byte scancode)> ExpandKeyScript(string script)
    {
        for (int i = 0; i < script.Length; i++)
        {
            char c = script[i];
            byte ascii;
            byte scan = 0;
            if (c == '\\' && i + 1 < script.Length)
            {
                char esc = script[++i];
                (ascii, scan) = esc switch
                {
                    'r' => ((byte)0x0D, (byte)0x1C),  // Enter
                    'n' => ((byte)0x0A, (byte)0x1C),
                    't' => ((byte)0x09, (byte)0x0F),
                    'b' => ((byte)0x08, (byte)0x0E),  // BackSpace
                    'e' => ((byte)0x1B, (byte)0x01),  // Esc
                    '\\' => ((byte)'\\', (byte)0x2B),
                    _   => ((byte)esc,  (byte)0),
                };
            }
            else
            {
                ascii = (byte)c;
                // Approximate scancode mapping for typeable ASCII —
                // good enough for CI testing of INT 16h.
                scan = ScancodeForAscii(c);
            }
            yield return (ascii, scan);
        }
    }

    private static byte ScancodeForAscii(char c)
    {
        // IBM PC scancode set 1 lookup for the most common ASCII keys.
        // 0 means "unknown" — INT 16h consumers usually look only at AL.
        return c switch
        {
            ' '              => 0x39,
            'a' or 'A'       => 0x1E,
            'b' or 'B'       => 0x30,
            'c' or 'C'       => 0x2E,
            'd' or 'D'       => 0x20,
            'e' or 'E'       => 0x12,
            'f' or 'F'       => 0x21,
            'g' or 'G'       => 0x22,
            'h' or 'H'       => 0x23,
            'i' or 'I'       => 0x17,
            'j' or 'J'       => 0x24,
            'k' or 'K'       => 0x25,
            'l' or 'L'       => 0x26,
            'm' or 'M'       => 0x32,
            'n' or 'N'       => 0x31,
            'o' or 'O'       => 0x18,
            'p' or 'P'       => 0x19,
            'q' or 'Q'       => 0x10,
            'r' or 'R'       => 0x13,
            's' or 'S'       => 0x1F,
            't' or 'T'       => 0x14,
            'u' or 'U'       => 0x16,
            'v' or 'V'       => 0x2F,
            'w' or 'W'       => 0x11,
            'x' or 'X'       => 0x2D,
            'y' or 'Y'       => 0x15,
            'z' or 'Z'       => 0x2C,
            '0'              => 0x0B,
            '1'              => 0x02,
            '2'              => 0x03,
            '3'              => 0x04,
            '4'              => 0x05,
            '5'              => 0x06,
            '6'              => 0x07,
            '7'              => 0x08,
            '8'              => 0x09,
            '9'              => 0x0A,
            _                => 0,
        };
    }
}
