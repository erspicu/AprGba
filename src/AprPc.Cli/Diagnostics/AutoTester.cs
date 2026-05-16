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
        KbdTrace.Log($"=== AutoTester FINAL SCREEN (fbBase=0x{fbBase:X5}) ===");
        foreach (var line in screen.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length > 0)
                KbdTrace.Log($"  | {trimmed}");
        }
        KbdTrace.Log($"=== END AutoTester FINAL SCREEN ===");
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
        _ => null,
    };
}
