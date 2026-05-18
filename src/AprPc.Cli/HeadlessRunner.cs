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
        Console.WriteLine($"  floppy B = {opts.FloppyBPath ?? "(none)"}");
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

        if (opts.FloppyBPath is { } floppyBPath)
        {
            var disk = DiskImage.LoadFloppy(floppyBPath);
            runner.MountDisk(0x01, disk);
            Console.WriteLine($"  floppy B: {floppyBPath} (mounted at FDC drive 0x01, " +
                $"{disk.Cylinders}x{disk.Heads}x{disk.Sectors} CHS)");
        }

        if (opts.HddPath is { } hddPath)
        {
            var disk = DiskImage.LoadHardDisk(hddPath);
            runner.MountDisk(0x80, disk);
            Console.WriteLine($"  hdd C:    {hddPath} ({disk.Cylinders}x{disk.Heads}x{disk.Sectors} CHS, {disk.TotalSectors * 512L / (1024 * 1024)} MB)");
        }
        if (opts.Hdd2Path is { } hdd2Path)
        {
            var disk = DiskImage.LoadHardDisk(hdd2Path);
            runner.MountDisk(0x81, disk);
            Console.WriteLine($"  hdd D:    {hdd2Path} ({disk.Cylinders}x{disk.Heads}x{disk.Sectors} CHS, {disk.TotalSectors * 512L / (1024 * 1024)} MB)");
        }

        // Phase 32.3 — parse --mount specs. Skeleton: validate the spec
        // but don't yet expose as a guest drive. Real synthesizer +
        // INT 13h backing is sprint 32.3a-c (vvfat read-only V1).
        foreach (var spec in opts.HostMounts)
        {
            var m = HostDirMount.TryParse(spec);
            if (m is null)
            {
                Console.Error.WriteLine($"  --mount={spec}: malformed (expect DRV:host[:ro|rw][:SIZE_MB]); skipped");
                continue;
            }
            if (!Directory.Exists(m.HostPath))
            {
                Console.Error.WriteLine($"  --mount={spec}: host path '{m.HostPath}' not found; skipped");
                continue;
            }
            Console.WriteLine($"  mount {m.DriveLetter}: {m.HostPath} ({(m.ReadWrite ? "rw" : "ro")}, {m.SizeMB} MB) — Phase 32.3 SKELETON (not yet exposed to guest)");
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

        // Phase 32.1d — headless stdin command listener for floppy swap.
        // Read lines from Console.In on a background thread; recognise:
        //     INSERT A <N|NEXT|PREV|path>     -> swap slot A
        //     INSERT B <N|NEXT|PREV|path>     -> swap slot B
        // N is 1-based index into the --floppy-a / -b list. Path can be
        // any file (lets you swap to something not in the list). The
        // thread is daemon-style (background=true) so headless exit
        // doesn't wait on stdin EOF.
        var stdinCts = new System.Threading.CancellationTokenSource();
        var stdinThread = new Thread(() => RunStdinCommandLoop(runner, stdinCts.Token))
        {
            Name = "apr-pc stdin commands",
            IsBackground = true,
        };
        stdinThread.Start();

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
            // Phase 32.1d — stdin "QUIT" command, or any other path
            // that called Stop(), promotes RunnerState to Stopping /
            // Stopped. Exit the limit loop now so we still snapshot
            // state + screenshot cleanly.
            if (runner.State is RunnerState.Stopping or RunnerState.Stopped) break;
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

                // Phase 30 forensics — boot sector at 0x07C00 vs copy at
                // 0x27A00. Print 8 lines of 32 bytes (covering all 256 bytes
                // = first half of boot sector) so we can see the orig→copy
                // mismatch pattern row by row.
                Console.WriteLine($"    orig boot 0x07C00..7CFF + copy 0x27A00..27AFF (per 32-byte row):");
                for (int row = 0; row < 8; row++)
                {
                    int rowOff = row * 32;
                    var origRow = string.Join(" ", Enumerable.Range(0, 32).Select(i => b.ReadByte(0x07C00 + rowOff + i).ToString("X2")));
                    var copyRow = string.Join(" ", Enumerable.Range(0, 32).Select(i => b.ReadByte(0x27A00 + rowOff + i).ToString("X2")));
                    Console.WriteLine($"      orig[+{rowOff:X3}]: {origRow}");
                    Console.WriteLine($"      copy[+{rowOff:X3}]: {copyRow}");
                }
                // Diff count over full 512 bytes
                int diffs = 0;
                int firstDiffOff = -1;
                int lastDiffOff = -1;
                for (int off = 0; off < 512; off++)
                {
                    if (b.ReadByte(0x07C00 + off) != b.ReadByte(0x27A00 + off))
                    {
                        diffs++;
                        if (firstDiffOff < 0) firstDiffOff = off;
                        lastDiffOff = off;
                    }
                }
                Console.WriteLine($"    orig vs copy diff: {diffs} bytes differ (first at 0x{firstDiffOff:X3}, last at 0x{lastDiffOff:X3})");
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

    /// <summary>
    /// Phase 32.1d — stdin command loop for headless mode. Parses one
    /// command per line; unknown lines are ignored. Recognised:
    ///
    ///     INSERT A N        # swap slot A to image N (1-based) from --floppy-a list
    ///     INSERT A NEXT     # cycle to next image in slot A's list
    ///     INSERT A PREV     # cycle to previous image in slot A's list
    ///     INSERT A path     # swap slot A to that specific file (need not be in list)
    ///     INSERT B ...      # same for slot B (--floppy-b)
    ///     QUIT              # stop emulator (graceful)
    ///
    /// Case-insensitive on command + slot; path is case-sensitive on
    /// host filesystems that distinguish.
    /// </summary>
    private static void RunStdinCommandLoop(PcSystemRunner runner, System.Threading.CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = Console.In.ReadLine();
                if (line is null) return;
                line = line.Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
                string cmd = parts[0].ToUpperInvariant();
                if (cmd == "QUIT" || cmd == "EXIT")
                {
                    Console.WriteLine("  [stdin] QUIT received, stopping emulator");
                    runner.Stop();
                    return;
                }
                if (cmd == "INSERT" && parts.Length >= 3)
                {
                    string slotStr = parts[1].ToUpperInvariant();
                    byte slot = slotStr switch { "A" => 0, "B" => 1, _ => 255 };
                    if (slot == 255)
                    {
                        Console.WriteLine($"  [stdin] INSERT: unknown slot '{parts[1]}', expected A or B");
                        continue;
                    }
                    string arg = parts[2].Trim();
                    string? newPath;
                    if (string.Equals(arg, "NEXT", StringComparison.OrdinalIgnoreCase))
                    {
                        newPath = runner.SwapFloppy(slot, nextInList: true);
                    }
                    else if (string.Equals(arg, "PREV", StringComparison.OrdinalIgnoreCase))
                    {
                        newPath = runner.SwapFloppy(slot, nextInList: false);
                    }
                    else if (int.TryParse(arg, out int idx1))
                    {
                        // 1-based index in user-facing CLI.
                        int size = runner.GetFloppyListSize(slot);
                        if (idx1 < 1 || idx1 > size)
                        {
                            Console.WriteLine($"  [stdin] INSERT: index {idx1} out of range (1..{size})");
                            continue;
                        }
                        // Walk to target index via nextInList — simpler
                        // than exposing an absolute SwapFloppy(idx).
                        int cur = runner.GetFloppyIndex(slot);
                        int steps = ((idx1 - 1) - cur + size) % size;
                        string? p = null;
                        for (int i = 0; i < steps; i++) p = runner.SwapFloppy(slot, nextInList: true);
                        newPath = p ?? runner.GetFloppyPath(slot);
                    }
                    else
                    {
                        // Treat arg as explicit file path.
                        newPath = runner.SwapFloppy(slot, nextInList: false, explicitPath: arg);
                    }
                    if (newPath is null)
                        Console.WriteLine($"  [stdin] INSERT {slotStr} {arg}: swap failed");
                    else
                        Console.WriteLine($"  [stdin] INSERT {slotStr} -> {newPath} (DSKCHG asserted)");
                    continue;
                }
                Console.WriteLine($"  [stdin] unknown command: '{line}' (expected INSERT A|B <N|NEXT|PREV|path> or QUIT)");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [stdin] reader thread exited: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
