// AutoTester — GUI integration-test automation.
//
// Polls the text-mode framebuffer every N seconds, matches the screen
// content against a sequence of (pattern, action) rules, and injects
// keystrokes when patterns hit. After the last rule fires, dumps the
// screen + recent activity to kbd-trace.log and closes the form.
//
// Use case: end-to-end real-BIOS + FreeDOS bring-up without an operator
// sitting at the keyboard. CLI: --auto-test=freedos-mda-dir runs the
// canonical "boot → language menu Enter → installer N → A:\> dir"
// sequence and captures the result, then exits.
//
// Scancode injection goes through PcPortBus.InjectScancode (real-BIOS
// keyboard path). Each key is make-code only; BIOS auto-repeat would
// add break codes if we held; for our scripted use one make per press
// is enough and BIOS INT 9 processes it normally.
//
// Adding new sequences: edit BuildSequence() below. Each Step is
// either (match-pattern, scancode-list) or (special: dump+close).

using System.Text;
using AprPc.Cli.Hardware;
using AprPc.Cli.Memory;
using AprX86.Cli.Video;

namespace AprPc.Cli.Diagnostics;

public sealed class AutoTester : IDisposable
{
    private readonly PcSystemRunner _runner;
    private readonly Action _closeForm;
    private readonly List<Step> _steps;
    private int _stateIdx;
    private DateTime _lastActionTime = DateTime.UtcNow;
    private DateTime _lastTickTime = DateTime.MinValue;
    private const int TickIntervalSec = 5;
    private const int CooldownAfterActionSec = 5;

    public AutoTester(PcSystemRunner runner, Action closeForm, string sequence)
    {
        _runner = runner;
        _closeForm = closeForm;
        _steps = BuildSequence(sequence)
            ?? throw new ArgumentException(
                $"AutoTester: unknown --auto-test sequence '{sequence}'. " +
                "Valid: freedos-mda-dir");
        KbdTrace.Log($"AutoTester started, sequence='{sequence}' " +
            $"({_steps.Count} steps, polling every {TickIntervalSec}s)");
    }

    /// <summary>
    /// Called by MainForm's refresh timer. Throttled internally so we
    /// only actually scan every TickIntervalSec seconds.
    /// </summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastTickTime).TotalSeconds < TickIntervalSec) return;
        if ((now - _lastActionTime).TotalSeconds < CooldownAfterActionSec) return;
        _lastTickTime = now;

        if (_stateIdx >= _steps.Count) return;
        if (_runner.Bus is not { } bus) return;

        var mem = bus.Memory.Ram;
        int fbBase = X86CgaRenderer.PickFramebufferBase(mem);
        string screen = ReadScreenAsString(mem, fbBase);

        var step = _steps[_stateIdx];
        if (step.IsTerminal)
        {
            KbdTrace.Log($"AutoTester step {_stateIdx} TERMINAL — dumping screen + closing");
            DumpScreen(mem, fbBase, screen);
            SaveScreenshot(mem);
            _closeForm();
            _stateIdx++;
            return;
        }

        if (!screen.Contains(step.Pattern, StringComparison.OrdinalIgnoreCase))
        {
            KbdTrace.Log($"AutoTester step {_stateIdx} waiting for pattern '{step.Pattern}' " +
                $"(screen first 80 chars: '{screen[..Math.Min(80, screen.Length)]}')");
            return;
        }

        KbdTrace.Log($"AutoTester step {_stateIdx} MATCHED '{step.Pattern}' — injecting {step.Scancodes.Count} scancodes");
        foreach (var scan in step.Scancodes)
        {
            _runner.Ports?.InjectScancode(scan);
            // tiny pause between scancodes so BIOS INT 9 ISR can run
            // for each one before the next arrives.
            Thread.Sleep(50);
        }
        _stateIdx++;
        _lastActionTime = now;
    }

    public void Dispose() { }

    private static string ReadScreenAsString(byte[] mem, int fbBase)
    {
        // 80x25 text. Read char bytes only (every other byte).
        var sb = new StringBuilder(80 * 25 + 25);
        for (int row = 0; row < 25; row++)
        {
            for (int col = 0; col < 80; col++)
            {
                byte ch = mem[fbBase + (row * 80 + col) * 2];
                sb.Append(ch >= 0x20 && ch < 0x7F ? (char)ch : ' ');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void DumpScreen(byte[] mem, int fbBase, string screen)
    {
        // Dump char bytes regardless of attr (= what's actually IN the
        // framebuffer memory, ignoring whether it would render visibly).
        KbdTrace.Log($"=== AutoTester CHAR-ONLY DUMP (ignoring attr) ===");
        for (int row = 0; row < 25; row++)
        {
            var sb = new StringBuilder(82);
            sb.Append($"r{row:D2}|");
            for (int col = 0; col < 80; col++)
            {
                byte ch = mem[fbBase + (row * 80 + col) * 2];
                sb.Append(ch >= 0x20 && ch < 0x7F ? (char)ch : '·');
            }
            sb.Append('|');
            KbdTrace.Log("  " + sb.ToString());
        }
        KbdTrace.Log($"=== END CHAR-ONLY DUMP ===");

        KbdTrace.Log($"=== AutoTester FINAL SCREEN (fbBase=0x{fbBase:X5}) ===");
        foreach (var line in screen.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length > 0)
                KbdTrace.Log($"  | {trimmed}");
        }
        KbdTrace.Log($"=== END AutoTester FINAL SCREEN ===");

        // Attribute byte dump per row — flags rows whose char dump is
        // non-empty but attrs are mostly 0 (= written-then-invisible cells).
        KbdTrace.Log($"=== AutoTester ATTR HISTOGRAM (per row) ===");
        for (int row = 0; row < 25; row++)
        {
            var hist = new Dictionary<byte, int>();
            int nonZeroChars = 0;
            for (int col = 0; col < 80; col++)
            {
                int off = fbBase + (row * 80 + col) * 2;
                byte ch = mem[off];
                byte at = mem[off + 1];
                if (ch != 0x00 && ch != 0x20) nonZeroChars++;
                hist[at] = hist.GetValueOrDefault(at) + 1;
            }
            var sorted = hist.OrderByDescending(kv => kv.Value).Take(3);
            var attrSummary = string.Join(",", sorted.Select(kv => $"0x{kv.Key:X2}={kv.Value}"));
            KbdTrace.Log($"  row {row:D2} chars={nonZeroChars,3} attrs={attrSummary}");
        }
        KbdTrace.Log($"=== END AutoTester ATTR HISTOGRAM ===");
    }

    /// <summary>
    /// Save the current framebuffer to a PNG so we have a visual record
    /// of what the user would have seen. Output goes to
    /// result/pc/auto-test-&lt;UTC timestamp&gt;.png so multiple runs don't
    /// overwrite each other. Failures are logged but non-fatal — the
    /// text dump in the trace is still the primary record.
    /// </summary>
    private static void SaveScreenshot(byte[] mem)
    {
        try
        {
            var dir = "result/pc";
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir,
                $"auto-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png");
            X86CgaRenderer.Render(mem, path);
            KbdTrace.Log($"AutoTester screenshot saved: {path}");
        }
        catch (Exception ex)
        {
            KbdTrace.Log($"AutoTester screenshot FAILED: {ex.Message}");
        }
    }

    private record struct Step(string Pattern, List<byte> Scancodes, bool IsTerminal = false);

    /// <summary>
    /// Built-in scripted sequences. Add new ones here keyed by --auto-test
    /// value. Each step waits for its pattern on screen, then injects its
    /// scancodes. Terminal step closes the form.
    /// </summary>
    private static List<Step>? BuildSequence(string name) => name switch
    {
        "freedos-mda-dir" => new List<Step>
        {
            // 1. FreeDOS language menu: just press Enter to take the default (English).
            new("language", new List<byte> { 0x1C }),     // Enter
            // 2. FreeDOS installer: "Do you want to proceed [Y,N]?" -> N then Enter.
            new("[Y,N]", new List<byte> { 0x31, 0x1C }),  // 'N' + Enter
            // 3. A:\> prompt: type "dir" + Enter.
            new("A:\\>", new List<byte>
            {
                0x20,  // 'D'
                0x17,  // 'I'
                0x13,  // 'R'
                0x1C,  // Enter
            }),
            // 4. Wait for dir to finish + scan a few seconds, then dump + close.
            new(string.Empty, new List<byte>(), IsTerminal: true),
        },

        // Phase 30.14c — exercise the --floppy-b mount + port-0xE9 hook.
        // After boot, switch to B: and run HELLO.COM which writes a banner
        // both to DOS stdout (visible on screen) and to port 0xE9 (visible
        // in temp/port-e9.log and host stdout).
        "freedos-b-hello" => new List<Step>
        {
            new("language", new List<byte> { 0x1C }),     // Enter
            new("[Y,N]",    new List<byte> { 0x31, 0x1C }),
            // A:\> -> type "B:" (shift+; for ':') + Enter
            new("A:\\>", new List<byte>
            {
                0x30,                          // 'B'
                0x2A, 0x27, 0xA7, 0xAA,        // shift-make, ; make, ; break, shift-break (= ':')
                0x1C,                          // Enter
            }),
            // B:\> -> type "HELLO" + Enter (.COM is implicit)
            new("B:\\>", new List<byte>
            {
                0x23,  // 'H'
                0x12,  // 'E'
                0x26,  // 'L'
                0x26,  // 'L'
                0x18,  // 'O'
                0x1C,  // Enter
            }),
            // Wait for the DOS-stdout banner from HELLO.COM to appear, then dump.
            new("Hello from B:", new List<byte>(), IsTerminal: true),
        },

        // Phase 32.2g — exercise the HDD install path. Press Y on the
        // installer prompt instead of N (freedos-mda-dir aborts; this
        // one proceeds). Terminal condition is any FDISK-related text
        // that proves we got past the FreeCom banner: "FDISK", "Drive C:",
        // "FLAG_SECTOR" (the known failure mode pre-32.2g fix), or
        // "successfully" (full success). Whichever appears first
        // triggers the screenshot.
        "freedos-install-y" => new List<Step>
        {
            new("language", new List<byte> { 0x1C }),               // Enter (English)
            new("[Y,N]",    new List<byte> { 0x15, 0x1C }),         // 'Y' + Enter (install)
            // Wait for FDISK output without injecting keys. Picks up
            // EITHER the failure pattern "FLAG_SECTOR" or the success
            // patterns "FDISK" / "Partition" -- "AG" appears in both
            // FLAG_SECTOR (failure) and "Verifying" / "Range" (success
            // dialogs FDISK prints), making it a permissive wait.
            new("AG",       new List<byte>()),                       // wait, no inject
            // After the wait advances, dump + close form.
            new(string.Empty, new List<byte>(), IsTerminal: true),
        },

        // Phase 32.2h — boot FreeDOS install, abort installer (N),
        // switch to B:, run HDDINT13.COM which calls INT 13h AH=08
        // DL=0x80 and dumps the returned CH/CL/DH/DL/BL values via
        // port 0xE9. This verifies whether our HLE INT 13h AH=08
        // delivers the geometry FDISK expects.
        "freedos-int13-probe" => new List<Step>
        {
            new("language", new List<byte> { 0x1C }),               // Enter (English)
            new("[Y,N]",    new List<byte> { 0x31, 0x1C }),         // 'N' + Enter (abort installer)
            new("A:\\>", new List<byte>
            {
                0x30,                          // 'B'
                0x2A, 0x27, 0xA7, 0xAA,        // shift-make, ; make, ; break, shift-break (= ':')
                0x1C,                          // Enter
            }),
            new("B:\\>", new List<byte>
            {
                0x23,                                        // 'H'
                0x20,                                        // 'D'
                0x20,                                        // 'D'
                0x17,                                        // 'I'
                0x31,                                        // 'N'
                0x14,                                        // 'T'
                0x02,                                        // '1'
                0x04,                                        // '3'
                0x1C,                                        // Enter
            }),
            // HDDINT13.COM prints "[TEST DONE]" then "[TEST_PASS]" via port 0xE9.
            // The output also appears on screen via DOS stdout (since we use INT 21h AH=4C exit).
            // Wait for "TEST" pattern in screen text.
            new("TEST", new List<byte>()),
            new(string.Empty, new List<byte>(), IsTerminal: true),
        },

        // Phase 32.2h — boot FreeDOS install, abort installer (N),
        // get to A:\> prompt, run FDISK /info on drive 1 (= HDD 0x80).
        // Used to test pre-formatted HDD image hypothesis.
        "freedos-fdisk-info" => new List<Step>
        {
            new("language", new List<byte> { 0x1C }),               // Enter (English)
            new("[Y,N]",    new List<byte> { 0x31, 0x1C }),         // 'N' + Enter (abort installer)
            new("A:\\>",    new List<byte>
            {
                // type "FDISK /INFO 1" + Enter
                0x21,                                       // 'F'
                0x20,                                       // 'D'
                0x17,                                       // 'I'
                0x1F,                                       // 'S'
                0x25,                                       // 'K'
                0x39,                                       // SPACE
                0x35,                                       // '/'
                0x17,                                       // 'I'
                0x31,                                       // 'N'
                0x21,                                       // 'F'
                0x18,                                       // 'O'
                0x39,                                       // SPACE
                0x02,                                       // '1'
                0x1C,                                       // Enter
            }),
            // Wait for any FDISK *output* — pattern "ange" matches
            // both failure ("out of range") and success ("Range"/various
            // FDISK header lines). Picking specifically "ange" avoids
            // matching the user-typed "fdisk /info" line which has no 'g'.
            new("ange", new List<byte>()),                           // wait
            new(string.Empty, new List<byte>(), IsTerminal: true),
        },
        _ => null,
    };
}
