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
using AprPc.Cli.Diagnostics;
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
        // F11 NOT bound to menu so it can be used as a debug-dump hotkey
        // for catching CPU state when the guest hangs.
        var emStF = new ToolStripMenuItem("Step One &Frame");
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
        // No menu shortcut for screenshot -- F12 is used by the keyboard
        // debug "dump screen framebuffer to log" hotkey in KeyDown.
        var ssItem = new ToolStripMenuItem("Take &Screenshot...");
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

        // Phase 30.11 — optional scripted GUI integration test. When
        // --auto-test=<seq> is set, spin up an AutoTester that the
        // refresh timer ticks. AutoTester polls framebuffer every 5s,
        // matches prompts, injects scancodes, and finally closes the
        // form after dumping the screen to kbd-trace.log.
        if (options.AutoTest is { } autoTestSeq)
        {
            _autoTester = new AutoTester(_runner, () => BeginInvoke(new Action(Close)), autoTestSeq);
        }

        // === Keyboard plumbing ===
        // Two routes depending on which BIOS path is active:
        //
        //   HLE BIOS  (--bios-mode=hle, no --bios=PATH):
        //     KeyPress / KeyDown → PcKeyboard.Enqueue writes (ASCII, scan)
        //     straight into the BDA ring buffer at 0040:001E. HLE INT 16h
        //     reads from there.
        //
        //   Real BIOS (--bios=PATH):
        //     KeyPress / KeyDown → PcPortBus.InjectScancode pushes the
        //     scancode into the 8042 FIFO and asserts IRQ 1. The real
        //     BIOS INT 9 ISR then reads port 0x60, translates scancode
        //     to ASCII via its own table, and populates the BDA buffer
        //     itself. Real INT 16h reads from BDA same as HLE, but the
        //     translation + BDA write is done by the BIOS, not by us.
        bool realBiosKbd = options.BiosPath is not null;

        // Two WinForms events fire per character key: KeyDown (virtual
        // key code) and KeyPress (ASCII char). For real-BIOS mode we
        // only want ONE scancode per physical press -- BIOS INT 9 does
        // the scancode->ASCII translation, so a single make code is
        // enough. KeyDown is the right hook (fires once per key, covers
        // letters + digits + arrows + Enter + Esc + Backspace etc.) and
        // KeyPress is suppressed in real-BIOS mode.
        //
        // Without this suppression, KeyDown + KeyPress each call
        // InjectScancode -> FIFO holds 2 scancodes -> PIC is edge-
        // triggered so AssertIrq twice only fires once -> BIOS only
        // reads 1 scancode per IRQ -> the second scancode is stranded
        // in FIFO until the NEXT key press triggers a fresh IRQ -> user
        // sees a 1-key lag (press 'a' shows nothing, press 'b' shows
        // 'a', etc.). Diagnosed 2026-05-16.
        //
        // For HLE mode the dual-event behaviour is harmless because
        // PcKeyboard.Enqueue writes BDA directly (no FIFO/IRQ gating)
        // and HLE INT 16h reads BDA -- duplicates would print "aa" but
        // that's preferable to risking missed scancodes for now. Keep
        // both events firing in HLE.
        KeyPress += (_, e) =>
        {
            if (realBiosKbd) return;   // suppressed -- KeyDown owns real-BIOS
            byte ascii = (byte)e.KeyChar;
            byte scan  = AsciiToScancode(e.KeyChar);
            KbdTrace.Log(
                $"KeyPress char='{(e.KeyChar < 0x20 ? "\\x" + ((int)e.KeyChar).ToString("X2") : e.KeyChar.ToString())}' " +
                $"ascii=0x{ascii:X2} scan=0x{scan:X2} route=HLE->BDA");
            bool ok = _runner.Keyboard?.Enqueue(ascii, scan) ?? false;
            if (!ok) KbdTrace.Log("Enqueue returned false (buffer full or no kbd)");
        };
        KeyDown += (_, e) =>
        {
            // Phase 32.1 — Ctrl+L (slot A) / Ctrl+Shift+L (slot B)
            // hot-swap the floppy to the next image in the user's
            // --floppy-a / --floppy-b list. Handled BEFORE the
            // scancode mapping below so the L key isn't also injected
            // into the guest. Status bar reflects the new mount.
            if (e.Control && e.KeyCode == Keys.L)
            {
                byte slot = e.Shift ? (byte)1 : (byte)0;
                var p = _runner.SwapFloppy(slot, nextInList: true);
                if (p is not null)
                {
                    int idx  = _runner.GetFloppyIndex(slot);
                    int size = _runner.GetFloppyListSize(slot);
                    string slotName = slot == 0 ? "A:" : "B:";
                    _statusState.Text = $"Swapped {slotName} -> {System.IO.Path.GetFileName(p)} ({idx + 1}/{size})";
                    KbdTrace.Log($"FloppySwap {slotName} -> {p} ({idx + 1}/{size})");
                }
                else
                {
                    _statusState.Text = $"Swap failed (slot={(slot == 0 ? "A" : "B")}: not mounted or single-image)";
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // Map WinForms virtual key codes to PC XT scancode set 1.
            // For real-BIOS this is the ONLY path; for HLE it
            // supplements KeyPress for keys that have no ASCII (arrows,
            // F-keys).
            byte ascii = 0, scan = 0;
            switch (e.KeyCode)
            {
                // Special / control keys (covered for both modes)
                case Keys.Escape:    ascii = 0x1B; scan = 0x01; break;
                case Keys.Back:      ascii = 0x08; scan = 0x0E; break;
                case Keys.Enter:     ascii = 0x0D; scan = 0x1C; break;
                case Keys.Tab:       ascii = 0x09; scan = 0x0F; break;
                case Keys.Space:     ascii = 0x20; scan = 0x39; break;
                case Keys.Up:        ascii = 0x00; scan = 0x48; break;
                case Keys.Down:      ascii = 0x00; scan = 0x50; break;
                case Keys.Left:      ascii = 0x00; scan = 0x4B; break;
                case Keys.Right:     ascii = 0x00; scan = 0x4D; break;
                case Keys.F1:        ascii = 0x00; scan = 0x3B; break;
                case Keys.F10:       ascii = 0x00; scan = 0x44; break;
                // Modifier keys -- inject make code on KeyDown, BIOS
                // INT 9 ISR updates BDA[0x17] shift-state flags. Break
                // code on KeyUp (see OnKeyUp override) clears them.
                // Without this, BIOS thinks shift is never held and
                // Shift+4 prints '4' instead of '$' etc.
                case Keys.ShiftKey:
                case Keys.LShiftKey: scan = 0x2A; break;
                case Keys.RShiftKey: scan = 0x36; break;
                case Keys.ControlKey:
                case Keys.LControlKey: scan = 0x1D; break;
                case Keys.RControlKey: scan = 0x1D; break;  // XT had no R-Ctrl
                case Keys.Menu:
                case Keys.LMenu:     scan = 0x38; break;     // Left Alt
                case Keys.RMenu:     scan = 0x38; break;     // XT had no R-Alt
                case Keys.CapsLock:  scan = 0x3A; break;
                case Keys.F11:
                    // Debug hotkey: dump current CPU state to kbd-trace
                    // instead of injecting a scancode. Useful for "press
                    // F11 when the guest hangs and tell me where CS:IP
                    // is" diagnostics.
                    {
                        var cs = _runner.Cpu?.State.CS ?? 0;
                        var ip = _runner.Cpu?.State.IP ?? 0;
                        var fl = _runner.Cpu?.State.GetFlags() ?? 0;
                        var st = _runner.Cpu?.State;
                        KbdTrace.Log($"F11 DUMP: CS={cs:X4}:IP={ip:X4} FLAGS=0x{fl:X4} " +
                            $"AX={st?.A.X:X4} BX={st?.B.X:X4} CX={st?.C.X:X4} DX={st?.D.X:X4} " +
                            $"SI={st?.SI:X4} DI={st?.DI:X4} BP={st?.BP:X4} SP={st?.SP:X4} " +
                            $"DS={st?.DS:X4} ES={st?.ES:X4} SS={st?.SS:X4} " +
                            $"halted={_runner.Cpu?.Halted}");
                    }
                    return;
                case Keys.F12:
                    // Debug hotkey: dump current text framebuffer (MDA
                    // 0xB0000 or CGA 0xB8000, auto-detected) to
                    // kbd-trace as ASCII text + attribute hex. Lets us
                    // see whether "invisible" output is actually present
                    // in framebuffer memory (attribute=0 black-on-black
                    // bug confirmation) or genuinely missing.
                    DumpScreenToLog();
                    return;
                // Digits 0..9 (top row, not numpad)
                case Keys.D0:        ascii = (byte)'0'; scan = 0x0B; break;
                case Keys.D1:        ascii = (byte)'1'; scan = 0x02; break;
                case Keys.D2:        ascii = (byte)'2'; scan = 0x03; break;
                case Keys.D3:        ascii = (byte)'3'; scan = 0x04; break;
                case Keys.D4:        ascii = (byte)'4'; scan = 0x05; break;
                case Keys.D5:        ascii = (byte)'5'; scan = 0x06; break;
                case Keys.D6:        ascii = (byte)'6'; scan = 0x07; break;
                case Keys.D7:        ascii = (byte)'7'; scan = 0x08; break;
                case Keys.D8:        ascii = (byte)'8'; scan = 0x09; break;
                case Keys.D9:        ascii = (byte)'9'; scan = 0x0A; break;
                // Letters A..Z (Keys.A is the virtual key, not the char)
                case Keys.A: ascii = (byte)'a'; scan = 0x1E; break;
                case Keys.B: ascii = (byte)'b'; scan = 0x30; break;
                case Keys.C: ascii = (byte)'c'; scan = 0x2E; break;
                case Keys.D: ascii = (byte)'d'; scan = 0x20; break;
                case Keys.E: ascii = (byte)'e'; scan = 0x12; break;
                case Keys.F: ascii = (byte)'f'; scan = 0x21; break;
                case Keys.G: ascii = (byte)'g'; scan = 0x22; break;
                case Keys.H: ascii = (byte)'h'; scan = 0x23; break;
                case Keys.I: ascii = (byte)'i'; scan = 0x17; break;
                case Keys.J: ascii = (byte)'j'; scan = 0x24; break;
                case Keys.K: ascii = (byte)'k'; scan = 0x25; break;
                case Keys.L: ascii = (byte)'l'; scan = 0x26; break;
                case Keys.M: ascii = (byte)'m'; scan = 0x32; break;
                case Keys.N: ascii = (byte)'n'; scan = 0x31; break;
                case Keys.O: ascii = (byte)'o'; scan = 0x18; break;
                case Keys.P: ascii = (byte)'p'; scan = 0x19; break;
                case Keys.Q: ascii = (byte)'q'; scan = 0x10; break;
                case Keys.R: ascii = (byte)'r'; scan = 0x13; break;
                case Keys.S: ascii = (byte)'s'; scan = 0x1F; break;
                case Keys.T: ascii = (byte)'t'; scan = 0x14; break;
                case Keys.U: ascii = (byte)'u'; scan = 0x16; break;
                case Keys.V: ascii = (byte)'v'; scan = 0x2F; break;
                case Keys.W: ascii = (byte)'w'; scan = 0x11; break;
                case Keys.X: ascii = (byte)'x'; scan = 0x2D; break;
                case Keys.Y: ascii = (byte)'y'; scan = 0x15; break;
                case Keys.Z: ascii = (byte)'z'; scan = 0x2C; break;
                // Punctuation — PC XT scancode set 1. ASCII column here
                // is the *unshifted* char; BIOS INT 9 translates to the
                // shifted char (':' '+' etc.) when Shift make code is in
                // BDA[0x17] (sent by KeyDown for Shift above).
                case Keys.OemSemicolon:       ascii = (byte)';';  scan = 0x27; break;  // ; :
                case Keys.Oemplus:            ascii = (byte)'=';  scan = 0x0D; break;  // = +
                case Keys.OemMinus:           ascii = (byte)'-';  scan = 0x0C; break;  // - _
                case Keys.OemPeriod:          ascii = (byte)'.';  scan = 0x34; break;  // . >
                case Keys.Oemcomma:           ascii = (byte)',';  scan = 0x33; break;  // , <
                case Keys.OemQuestion:        ascii = (byte)'/';  scan = 0x35; break;  // / ?
                case Keys.OemPipe:            ascii = (byte)'\\'; scan = 0x2B; break;  // \ |
                case Keys.OemBackslash:       ascii = (byte)'\\'; scan = 0x2B; break;  // (102-key)
                case Keys.OemOpenBrackets:    ascii = (byte)'[';  scan = 0x1A; break;  // [ {
                case Keys.OemCloseBrackets:   ascii = (byte)']';  scan = 0x1B; break;  // ] }
                case Keys.OemQuotes:          ascii = (byte)'\''; scan = 0x28; break;  // ' "
                case Keys.Oemtilde:           ascii = (byte)'`';  scan = 0x29; break;  // ` ~
                default: return;
            }
            KbdTrace.Log(
                $"KeyDown code={e.KeyCode} ascii=0x{ascii:X2} scan=0x{scan:X2} " +
                $"route={(realBiosKbd ? "realBios->port60" : "HLE->BDA")}");
            if (realBiosKbd)
            {
                _runner.Ports?.InjectScancode(scan);
            }
            else
            {
                _runner.Keyboard?.Enqueue(ascii, scan);
            }
        };

        // KeyUp -- inject the BREAK code (= make code | 0x80) so the
        // BIOS INT 9 ISR can track "key released" and clear shift /
        // ctrl / alt bits in BDA[0x17]. Without this, modifier state
        // sticks forever after first press. Real-BIOS mode only;
        // HLE BDA-direct path doesn't have a release concept.
        KeyUp += (_, e) =>
        {
            if (!realBiosKbd) return;
            byte scan = MapKeyCodeToScancode(e.KeyCode);
            if (scan == 0) return;
            byte breakCode = (byte)(scan | 0x80);
            KbdTrace.Log($"KeyUp code={e.KeyCode} scan=0x{scan:X2} break=0x{breakCode:X2}");
            _runner.Ports?.InjectScancode(breakCode);
        };

        // Apply fullscreen if requested.
        if (options.Fullscreen)
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState     = FormWindowState.Maximized;
        }
    }

    private long _lastInstrCount;
    private DateTime _lastSampleTime = DateTime.UtcNow;
    private bool _autoExitFired;
    private DateTime _lastCpuDumpTime = DateTime.UtcNow;
    private long _lastCpuDumpInstr;
    private ushort _lastCpuDumpCs, _lastCpuDumpIp;
    private AutoTester? _autoTester;

    /// <summary>
    /// Snapshot current framebuffer to <c>opts.ScreenshotPath</c>, mirroring
    /// what HeadlessRunner does on max-cycles / timeout. Used by the auto-exit
    /// path so unattended GUI runs can be inspected after the fact. Caller
    /// must have already checked <c>_runner.Bus != null</c> at least once
    /// (otherwise we skip silently).
    /// </summary>
    private void TryDumpScreenshot()
    {
        if (_options.ScreenshotPath is not { } path) return;
        if (_runner.Bus is not { } bus) return;
        try
        {
            X86CgaRenderer.Render(bus.Memory.Ram, path);
        }
        catch (Exception ex)
        {
            // Best-effort — log to stderr so the operator sees it, but
            // don't crash the auto-exit path itself.
            Console.Error.WriteLine($"apr-pc: screenshot dump failed: {ex.Message}");
        }
    }

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

        // === AutoTester tick (Phase 30.11) ===
        // Internally throttled to once per 5s + cooldown after action;
        // safe to call every 16ms.
        _autoTester?.Tick();

        // === Periodic CPU dump every 3s to kbd-trace ===
        // Lets post-hoc analysis tell whether CPU is stuck at the same
        // CS:IP / not advancing instructions (= true hang) vs running
        // code at varying IPs (= just slow). Logs delta inst/s too.
        if ((now - _lastCpuDumpTime).TotalSeconds >= 3.0 && _runner.Cpu is { } cpu)
        {
            var st = cpu.State;
            long curInstr = _runner.InstructionsExecuted;
            long deltaInstr = curInstr - _lastCpuDumpInstr;
            bool ipSame = st.CS == _lastCpuDumpCs && st.IP == _lastCpuDumpIp;
            KbdTrace.Log(
                $"CPU_TICK CS={st.CS:X4}:IP={st.IP:X4} flags=0x{st.GetFlags():X4} " +
                $"AX={st.A.X:X4} BX={st.B.X:X4} CX={st.C.X:X4} DX={st.D.X:X4} " +
                $"halted={cpu.Halted} delta_instr={deltaInstr} same_csip={ipSame}");
            _lastCpuDumpTime = now;
            _lastCpuDumpInstr = curInstr;
            _lastCpuDumpCs = st.CS;
            _lastCpuDumpIp = st.IP;
        }

        // === Auto-exit on --max-cycles (GUI parity with HeadlessRunner) ===
        // Lets unattended GUI runs reach a deterministic snapshot point:
        // when the requested cycle budget is hit we render the framebuffer
        // to --screenshot=PATH (if provided) and close the form. Useful
        // for "launch + take screenshot + analyse" automation that doesn't
        // need an interactive operator.
        if (!_autoExitFired
            && _options.MaxCycles.HasValue
            && _runner.InstructionsExecuted >= _options.MaxCycles.Value)
        {
            _autoExitFired = true;
            TryDumpScreenshot();
            // BeginInvoke so the timer tick returns cleanly before Close
            // tears down the form.
            BeginInvoke(new Action(Close));
            return;
        }

        // === Framebuffer blt (Phase 28.2) ===
        // Pull the B800 framebuffer through X86CgaRenderer.RenderToRgbBytes
        // and blit into our PictureBox bitmap. Skipped before the
        // runner has constructed the bus (Start() not called yet).
        if (_runner.Bus is { } bus)
        {
            try
            {
                // Auto-detect MDA (0xB0000) vs CGA (0xB8000): real PC/XT BIOS
                // POST writes to MDA; HLE BIOS path writes to CGA. Without
                // this the GUI renders black for real-BIOS mode even when
                // boot text is sitting in memory. Mirrors the headless
                // X86CgaRenderer.Render() / HeadlessRunner screenshot path.
                int fbBase = X86CgaRenderer.PickFramebufferBase(bus.Memory.Ram);
                var rgb = X86CgaRenderer.RenderToRgbBytes(bus.Memory.Ram, fbBase);
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

    /// <summary>
    /// Standalone scancode lookup -- used by KeyUp to compute the break
    /// code for the matching key. Mirrors the switch inside KeyDown but
    /// trimmed (omits ascii). Returns 0 for unmapped keys (no break
    /// code sent -- safe default).
    /// </summary>
    private static byte MapKeyCodeToScancode(Keys k) => k switch
    {
        Keys.Escape    => 0x01,
        Keys.Back      => 0x0E,
        Keys.Enter     => 0x1C,
        Keys.Tab       => 0x0F,
        Keys.Space     => 0x39,
        Keys.Up        => 0x48,
        Keys.Down      => 0x50,
        Keys.Left      => 0x4B,
        Keys.Right     => 0x4D,
        Keys.F1        => 0x3B,
        Keys.F10       => 0x44,
        Keys.ShiftKey or Keys.LShiftKey   => 0x2A,
        Keys.RShiftKey                    => 0x36,
        Keys.ControlKey or Keys.LControlKey or Keys.RControlKey => 0x1D,
        Keys.Menu or Keys.LMenu or Keys.RMenu                   => 0x38,
        Keys.CapsLock  => 0x3A,
        Keys.D0 => 0x0B, Keys.D1 => 0x02, Keys.D2 => 0x03, Keys.D3 => 0x04,
        Keys.D4 => 0x05, Keys.D5 => 0x06, Keys.D6 => 0x07, Keys.D7 => 0x08,
        Keys.D8 => 0x09, Keys.D9 => 0x0A,
        Keys.A => 0x1E, Keys.B => 0x30, Keys.C => 0x2E, Keys.D => 0x20,
        Keys.E => 0x12, Keys.F => 0x21, Keys.G => 0x22, Keys.H => 0x23,
        Keys.I => 0x17, Keys.J => 0x24, Keys.K => 0x25, Keys.L => 0x26,
        Keys.M => 0x32, Keys.N => 0x31, Keys.O => 0x18, Keys.P => 0x19,
        Keys.Q => 0x10, Keys.R => 0x13, Keys.S => 0x1F, Keys.T => 0x14,
        Keys.U => 0x16, Keys.V => 0x2F, Keys.W => 0x11, Keys.X => 0x2D,
        Keys.Y => 0x15, Keys.Z => 0x2C,
        Keys.OemSemicolon     => 0x27,
        Keys.Oemplus          => 0x0D,
        Keys.OemMinus         => 0x0C,
        Keys.OemPeriod        => 0x34,
        Keys.Oemcomma         => 0x33,
        Keys.OemQuestion      => 0x35,
        Keys.OemPipe          => 0x2B,
        Keys.OemBackslash     => 0x2B,
        Keys.OemOpenBrackets  => 0x1A,
        Keys.OemCloseBrackets => 0x1B,
        Keys.OemQuotes        => 0x28,
        Keys.Oemtilde         => 0x29,
        _ => 0,
    };

    /// <summary>
    /// Dump the 80x25 text framebuffer (MDA 0xB0000 or CGA 0xB8000) as
    /// ASCII to kbd-trace.log. Per-row layout: row=NN "..." (cleaned,
    /// nulls/zeros shown as '.') then attribute hex dump. Helps confirm
    /// whether the "invisible chars" symptom is char-bytes present with
    /// attribute byte == 0 (black on black) vs char-bytes truly missing.
    /// </summary>
    private void DumpScreenToLog()
    {
        if (_runner.Bus is not { } bus) return;
        var mem = bus.Memory.Ram;
        int fbBase = X86CgaRenderer.PickFramebufferBase(mem);
        // Expanded BDA video state — pcxtbios spec handbook §2/§5
        byte equip = mem[0x0410];                                            // equipment flag low byte
        byte mode  = mem[0x0449];                                            // current video mode (BDA[0x49])
        ushort cols = (ushort)(mem[0x044A] | (mem[0x044B] << 8));            // CRT columns (BDA[0x4A])
        ushort regen = (ushort)(mem[0x044C] | (mem[0x044D] << 8));           // regen size (BDA[0x4C])
        ushort fbSeg = (ushort)(mem[0x0463] | (mem[0x0464] << 8));           // CRT base segment (BDA[0x63])
        KbdTrace.Log($"=== F12 SCREEN DUMP fbBase=0x{fbBase:X5} " +
            $"BDA[0x10]=0x{equip:X2}(equip,video={(equip >> 4) & 0x3:X}) " +
            $"BDA[0x49]={mode}(mode) BDA[0x4A]={cols}(cols) BDA[0x4C]=0x{regen:X4}(regen) " +
            $"BDA[0x63]={fbSeg:X4}(fbSeg) ===");
        for (int row = 0; row < 25; row++)
        {
            int rowOff = fbBase + row * 80 * 2;
            var chars = new char[80];
            int nonZeroAttr = 0;
            for (int col = 0; col < 80; col++)
            {
                byte ch = mem[rowOff + col * 2];
                byte at = mem[rowOff + col * 2 + 1];
                chars[col] = (ch >= 0x20 && ch < 0x7F) ? (char)ch :
                             (ch == 0x00 ? '_' : '.');
                if (at != 0) nonZeroAttr++;
            }
            string line = new string(chars).TrimEnd();
            if (line.Length > 0 || nonZeroAttr > 0)
                KbdTrace.Log($"  row{row:D2} attrs={nonZeroAttr}/80 \"{line}\"");
        }
        KbdTrace.Log($"=== END SCREEN DUMP ===");
    }

    /// <summary>
    /// PC/XT set-1 make-code lookup for a typed ASCII char. Covers
    /// letters / digits / common punctuation / whitespace / Esc — the
    /// keys you'd type to drive FreeDOS / DOS application menus. Returns
    /// 0 for unmapped chars (caller should drop the keystroke).
    /// </summary>
    private static byte AsciiToScancode(char c)
    {
        // Letters: both upper and lower map to the same make code; the
        // real BIOS INT 9 ISR applies caps/shift based on the BDA
        // shift-flags byte, which we don't drive (host typed-case wins
        // via the BDA buffer for the HLE path, but for the real-BIOS
        // path the BIOS will see whatever case its shift state suggests).
        if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A');
        return c switch
        {
            (char)0x1B   => 0x01,   // Esc
            '1' or '!'   => 0x02,
            '2' or '@'   => 0x03,
            '3' or '#'   => 0x04,
            '4' or '$'   => 0x05,
            '5' or '%'   => 0x06,
            '6' or '^'   => 0x07,
            '7' or '&'   => 0x08,
            '8' or '*'   => 0x09,
            '9' or '('   => 0x0A,
            '0' or ')'   => 0x0B,
            '-' or '_'   => 0x0C,
            '=' or '+'   => 0x0D,
            '\b'         => 0x0E,   // Backspace
            '\t'         => 0x0F,   // Tab
            'Q'          => 0x10,
            'W'          => 0x11,
            'E'          => 0x12,
            'R'          => 0x13,
            'T'          => 0x14,
            'Y'          => 0x15,
            'U'          => 0x16,
            'I'          => 0x17,
            'O'          => 0x18,
            'P'          => 0x19,
            '[' or '{'   => 0x1A,
            ']' or '}'   => 0x1B,
            '\r' or '\n' => 0x1C,   // Enter
            'A'          => 0x1E,
            'S'          => 0x1F,
            'D'          => 0x20,
            'F'          => 0x21,
            'G'          => 0x22,
            'H'          => 0x23,
            'J'          => 0x24,
            'K'          => 0x25,
            'L'          => 0x26,
            ';' or ':'   => 0x27,
            '\'' or '"'  => 0x28,
            '`' or '~'   => 0x29,
            '\\' or '|'  => 0x2B,
            'Z'          => 0x2C,
            'X'          => 0x2D,
            'C'          => 0x2E,
            'V'          => 0x2F,
            'B'          => 0x30,
            'N'          => 0x31,
            'M'          => 0x32,
            ',' or '<'   => 0x33,
            '.' or '>'   => 0x34,
            '/' or '?'   => 0x35,
            ' '          => 0x39,   // Space
            _            => 0x00,
        };
    }
}
