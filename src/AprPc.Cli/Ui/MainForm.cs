// MainForm — single-window WinForms shell hosting the CGA framebuffer
// canvas, the menu bar, and a status strip.
//
// Phase 28.0: the menus exist and are all wired but only show "TODO"
// MessageBoxes. The canvas is a fixed 640x400 (80x25 chars × 8x16 px,
// approximated; real CGA is 8x14 → 640x350 but 8x16 reads cleaner on
// modern displays and matches IBM PC AT-class BIOS POST). Real
// framebuffer blt arrives in Phase 28.2.

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AprX86.Cli.Video;

namespace AprPc.Cli.Ui;

public sealed class MainForm : Form
{
    // Canvas geometry: drive it from X86CgaRenderer so the WinForms
    // surface and the headless PNG renderer always agree.
    public  const int CanvasW  = X86CgaRenderer.ImgW;   // 640
    public  const int CanvasH  = X86CgaRenderer.ImgH;   // 350

    private readonly PcOptions _options;
    private readonly PcSystemRunner _runner;
    private readonly PictureBox _canvas;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusState;
    private readonly ToolStripStatusLabel _statusMips;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly Bitmap _frameBitmap;

    public MainForm(PcOptions options, PcSystemRunner runner)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runner  = runner  ?? throw new ArgumentNullException(nameof(runner));

        Text             = options.WindowTitle;
        FormBorderStyle  = FormBorderStyle.FixedSingle;
        MaximizeBox      = false;
        StartPosition    = FormStartPosition.CenterScreen;
        KeyPreview       = true;

        // === Menu strip ===
        var menu = new MenuStrip();

        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add("Open Floppy A...", null, (_, _) => Todo("File: Open Floppy A"));
        fileMenu.DropDownItems.Add("Open HDD...",      null, (_, _) => Todo("File: Open HDD"));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var emulMenu = new ToolStripMenuItem("&Emulation");
        var emRst = new ToolStripMenuItem("&Reset")              { ShortcutKeys = Keys.Control | Keys.R };
        emRst.Click += (_, _) => Todo("Emulation: Reset");
        var emPau = new ToolStripMenuItem("&Pause / Resume")     { ShortcutKeys = Keys.F5 };
        emPau.Click += (_, _) => TogglePause();
        var emSt1 = new ToolStripMenuItem("Step One &Instruction") { ShortcutKeys = Keys.F10 };
        emSt1.Click += (_, _) => Todo("Emulation: Step Instruction");
        var emStF = new ToolStripMenuItem("Step One &Frame")     { ShortcutKeys = Keys.F11 };
        emStF.Click += (_, _) => Todo("Emulation: Step Frame");
        emulMenu.DropDownItems.AddRange(new ToolStripItem[] { emRst, emPau, emSt1, emStF });

        var diskMenu = new ToolStripMenuItem("&Disk");
        diskMenu.DropDownItems.Add("Eject Floppy A", null, (_, _) => Todo("Disk: Eject A"));
        diskMenu.DropDownItems.Add(new ToolStripMenuItem("Floppy Write-Protect") { CheckOnClick = true });
        diskMenu.DropDownItems.Add(new ToolStripMenuItem("HDD Read-Only")        { CheckOnClick = true });

        var viewMenu = new ToolStripMenuItem("&View");
        var scaleMenu = new ToolStripMenuItem("Window Scale");
        for (int s = 1; s <= 3; s++)
        {
            int scale = s;
            var item = new ToolStripMenuItem($"{scale}×") { Checked = scale == options.WindowScale };
            item.Click += (_, _) => Todo($"View: rescale to {scale}× (re-launch with --window-scale={scale} for now)");
            scaleMenu.DropDownItems.Add(item);
        }
        viewMenu.DropDownItems.Add(scaleMenu);
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Show CPU MIPS") { Checked = true, CheckOnClick = true });
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Show Disk LED") { Checked = true, CheckOnClick = true });
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        var ssItem = new ToolStripMenuItem("Take &Screenshot...") { ShortcutKeys = Keys.PrintScreen };
        ssItem.Click += (_, _) => Todo("View: Screenshot");
        viewMenu.DropDownItems.Add(ssItem);

        var helpMenu = new ToolStripMenuItem("&Help");
        helpMenu.DropDownItems.Add("Keyboard Shortcuts", null, (_, _) => Todo("Help: Shortcuts"));
        helpMenu.DropDownItems.Add("&About...", null, (_, _) => MessageBox.Show(this,
            $"AprPc — Phase 28.0 scaffolding\n\n" +
            $"CPU: {options.Cpu}\n" +
            $"Memory: {options.Memory}\n" +
            $"Backend: {options.Backend}\n" +
            $"BIOS: {(options.BiosPath ?? "(HLE)")}\n" +
            $"Floppy A: {(options.FloppyAPath ?? "(none)")}\n" +
            $"HDD: {(options.HddPath ?? "(none)")}",
            "About AprPc", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.AddRange(new ToolStripItem[] { fileMenu, emulMenu, diskMenu, viewMenu, helpMenu });
        MainMenuStrip = menu;
        Controls.Add(menu);

        // === Canvas (CGA framebuffer placeholder) ===
        _frameBitmap = new Bitmap(CanvasW, CanvasH, PixelFormat.Format24bppRgb);
        ClearBitmap(_frameBitmap, Color.Black);

        int displayW = CanvasW * options.WindowScale;
        int displayH = CanvasH * options.WindowScale;
        _canvas = new PictureBox
        {
            Size         = new Size(displayW, displayH),
            Location     = new Point(0, menu.Height),
            SizeMode     = PictureBoxSizeMode.StretchImage,
            Image        = _frameBitmap,
            BorderStyle  = BorderStyle.None,
        };
        Controls.Add(_canvas);

        // === Status strip ===
        _statusState = new ToolStripStatusLabel("Idle")          { Spring = true,  TextAlign = ContentAlignment.MiddleLeft };
        _statusMips  = new ToolStripStatusLabel("0 inst/s")      { Spring = false, TextAlign = ContentAlignment.MiddleRight };
        _statusStrip = new StatusStrip();
        _statusStrip.Items.AddRange(new ToolStripItem[] { _statusState, _statusMips });
        Controls.Add(_statusStrip);

        // Auto-size form to fit menu + canvas + status.
        ClientSize = new Size(displayW, menu.Height + displayH + _statusStrip.Height);

        // === Refresh timer @ 60 Hz to pull status snapshot + (later) framebuffer ===
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 / 60 };
        _refreshTimer.Tick += (_, _) => RefreshFromRunner();
        _refreshTimer.Start();

        // === Keyboard plumbing — Phase 28.3 will replace this with real scancode mapping ===
        KeyDown += (_, e) => _runner.PostKeyEvent((byte)e.KeyValue, KeyEventKind.Down);
        KeyUp   += (_, e) => _runner.PostKeyEvent((byte)e.KeyValue, KeyEventKind.Up);

        // Apply fullscreen if requested.
        if (options.Fullscreen)
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState     = FormWindowState.Maximized;
        }
    }

    private long _lastInstrCount;
    private DateTime _lastSampleTime = DateTime.UtcNow;

    private void RefreshFromRunner()
    {
        // === Stats line ===
        var now = DateTime.UtcNow;
        var dt  = (now - _lastSampleTime).TotalSeconds;
        if (dt >= 0.5)
        {
            long cur = _runner.InstructionsExecuted;
            long delta = cur - _lastInstrCount;
            double rate = delta / Math.Max(dt, 1e-6);
            _statusMips.Text = $"{rate:F0} inst/s";
            _lastInstrCount  = cur;
            _lastSampleTime  = now;
        }
        _statusState.Text = _runner.State.ToString();

        // === Framebuffer blt (Phase 28.2) ===
        // Pull the B800 framebuffer through X86CgaRenderer.RenderToRgbBytes
        // and blit into our PictureBox bitmap. Skipped before the
        // runner has constructed the bus (Start() not called yet).
        if (_runner.Bus is { } bus)
        {
            try
            {
                var rgb = X86CgaRenderer.RenderToRgbBytes(bus.Memory.Ram);
                BltRgbIntoBitmap(rgb, _frameBitmap);
                _canvas.Invalidate();
            }
            catch (FileNotFoundException)
            {
                // CGA font directory missing — Apr86 dump not present.
                // Don't spam the user every 16ms; status bar shows it.
                _statusState.Text = "Idle (no CGA font)";
            }
        }
    }

    private static void BltRgbIntoBitmap(byte[] rgbBgrSwapped, Bitmap bm)
    {
        // X86CgaRenderer emits 24bpp **RGB** packed; System.Drawing's
        // Format24bppRgb is actually **BGR** in memory layout. We swap
        // R and B during the blit.
        var data = bm.LockBits(
            new Rectangle(0, 0, bm.Width, bm.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            int stride = data.Stride;
            int rowLen = bm.Width * 3;
            unsafe
            {
                byte* dst = (byte*)data.Scan0;
                for (int y = 0; y < bm.Height; y++)
                {
                    int srcRow = y * rowLen;
                    int dstRow = y * stride;
                    for (int x = 0; x < bm.Width; x++)
                    {
                        // src is R G B
                        byte r = rgbBgrSwapped[srcRow + x * 3 + 0];
                        byte g = rgbBgrSwapped[srcRow + x * 3 + 1];
                        byte b = rgbBgrSwapped[srcRow + x * 3 + 2];
                        // dst is B G R
                        dst[dstRow + x * 3 + 0] = b;
                        dst[dstRow + x * 3 + 1] = g;
                        dst[dstRow + x * 3 + 2] = r;
                    }
                }
            }
        }
        finally
        {
            bm.UnlockBits(data);
        }
    }

    private void TogglePause()
    {
        if (_runner.State == RunnerState.Running) _runner.Pause();
        else if (_runner.State == RunnerState.Paused) _runner.Resume();
    }

    private void Todo(string item)
        => MessageBox.Show(this, $"TODO (Phase 28.x): {item}", "AprPc",
            MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static void ClearBitmap(Bitmap bm, Color color)
    {
        using var g = Graphics.FromImage(bm);
        g.Clear(color);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        _runner.Stop();
        base.OnFormClosing(e);
    }
}
