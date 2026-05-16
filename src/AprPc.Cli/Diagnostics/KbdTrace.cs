// KbdTrace -- file-based logging for the GUI keyboard plumbing.
//
// Captures three layers so we can pinpoint where keystrokes get lost:
//   1. UI thread:        MainForm.KeyPress / KeyDown -> what scancode chosen
//   2. UI thread:        PcPortBus.InjectScancode    -> FIFO enqueue + IRQ 1
//   3. Emulator thread:  PcPortBus port 0x60 read    -> CPU INT 9 ISR pickup
//
// Every line is timestamped (ms-resolution wall clock) + thread name so the
// causal chain is readable. Init clears the previous file so each launch is
// self-contained.

using System.IO;
using System.Text;

namespace AprPc.Cli.Diagnostics;

public static class KbdTrace
{
    private static readonly object _lock = new();
    private static string? _path;

    /// <summary>
    /// Open + truncate the trace file. Call once at program start. If the
    /// directory doesn't exist it is created.
    /// </summary>
    public static void Init(string path)
    {
        lock (_lock)
        {
            _path = path;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path,
                $"# kbd-trace opened {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} UTC\n");
        }
    }

    /// <summary>Append one line. No-op if Init() never ran.</summary>
    public static void Log(string msg)
    {
        if (_path is null) return;
        var line = new StringBuilder();
        line.Append(DateTime.UtcNow.ToString("HH:mm:ss.fff"));
        line.Append(' ');
        var tn = System.Threading.Thread.CurrentThread.Name;
        if (string.IsNullOrEmpty(tn))
            tn = $"tid-{System.Threading.Thread.CurrentThread.ManagedThreadId}";
        line.Append('[').Append(tn).Append("] ");
        line.AppendLine(msg);
        lock (_lock)
        {
            try { File.AppendAllText(_path!, line.ToString()); }
            catch { /* best-effort tracing */ }
        }
    }
}
