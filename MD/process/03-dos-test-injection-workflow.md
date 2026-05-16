# DOS test-binary injection workflow (Phase 30.14)

How to write a tiny test program in NASM, get it onto the running FreeDOS
guest, run it, and observe its results without screen-scraping.

> Why this exists: Phase 30 brought up a real-pcxtbios + FreeDOS environment
> that's *too* realistic to test by hand. We want to ship 5-line test
> programs that exercise specific instructions / interrupts and get a
> binary pass/fail in under a minute, like Bochs / QEMU users do.

## TL;DR

```pwsh
# 1. Write a tiny .COM in NASM (test-roms/x86/src/mytest.com.asm)
"C:/Program Files/NASM/nasm.exe" -f bin `
    test-roms/x86/src/mytest.com.asm `
    -o test-roms/x86/fat12-b/MYTEST.COM

# 2. Build a B: floppy image containing it
python tools/make_fat12_floppy.py `
    --src=test-roms/x86/fat12-b `
    --out=test-roms/x86/test-floppy-b.img `
    --label="APRPCTEST"

# 3. Boot with both --floppy-a (FreeDOS) and --floppy-b (the test image)
dotnet "src/AprPc.Cli/bin/Debug/net10.0-windows/apr-pc.dll" `
    --bios=BIOS/firmware/pcxtbios.bin `
    --video-bios=BIOS/firmware/videorom.bin `
    --floppy-a=BIOS/freedos-1.3-floppy.img `
    --floppy-b=test-roms/x86/test-floppy-b.img `
    --backend=json --video=cga `
    --auto-test=freedos-b-hello `
    --window-scale=2

# 4. Read the results
Get-Content temp/port-e9.log
```

## Architecture in one diagram

```
+---------------------+        +-----------------+        +---------------+
| test-roms/x86/src/  |        | test-roms/x86/  |        | apr-pc        |
|   mytest.com.asm    |  NASM  |  fat12-b/       | Python |               |
|                     +-------->  MYTEST.COM     +-------->  --floppy-b   |
+---------------------+        +-----------------+ FAT12  +-------+-------+
                                       ^                          |
                                       |                          v
                              add more files here          +---------------+
                                                           | FreeDOS guest |
                                                           |   B:\>MYTEST  |
                                                           +-------+-------+
                                                                   |
                                                                   v
                                                   +-------+-------+--------+
                                                   |               |        |
                                                   v               v        v
                                          OUT 0xE9, AL       DOS stdout    screen
                                                   |               |        |
                                                   v               v        v
                                          temp/port-e9.log       (visible in GUI / kbd-trace.log dump)
                                                   |
                                                   v
                                          host stdout `[E9] ...`
                                          (real-time, no polling)
```

## The three building blocks

### 1. Port 0xE9 debug-out hook (Phase 30.14a)

Real PC hardware leaves port 0xE9 unassigned. Both Bochs and QEMU
re-purpose it as a side-channel: every `OUT 0xE9, AL` in the guest is
captured by the host and written somewhere visible.

In AprPc:
- `PcPortBus.Write8` case `0xE9` calls `WritePortE9(byte)`.
- Output is buffered per-line and flushed to **`temp/port-e9.log`**.
- By default also mirrored to host stdout as `[E9] <line>` so you see
  results live. Toggle with `PcPortBus.PortE9MirrorStdout = false`.
- Truncated each launch (`PcPortBus.ResetPortE9Log()` from `Program.cs`).

**Guest-side use** — anywhere in your .COM:

```nasm
mov  si, msg
.loop:
        lodsb
        or   al, al
        jz   .done
        out  0xE9, al
        jmp  .loop
.done:
msg:    db  '[MYTEST] step 1 ok', 10, 0      ; LF terminates a log line
```

Newlines (`0x0A`) flush; `0x0D` is dropped (no double-spaced logs); any
non-printable byte is escaped as `\xNN` in the log. A sentinel like
`[TEST_PASS]\n` or `[FAIL_<reason>]\n` makes results greppable.

### 2. `--floppy-b=PATH` second floppy mount (Phase 30.14b)

The boot floppy A: stays *pristine* — your tests live on a separate
disposable B:. Two pieces had to land for FreeDOS to treat B: as a
real drive instead of doing the phantom-B "Insert diskette for B:" prompt:

1. `PcSystemRunner` mounts the image at FDC drive 0x01.
2. `PcPortBus.Read62` reports `floppyCount = 2` via the SW2 nibble bits 2-3.
   pcxtbios POST builds BDA[0x10] equipment word from that nibble; bits
   6-7 = 01 → "2 floppy drives" → FreeDOS won't fake a swap.

> Subtle bug found en route: the SW2 select line in our Port 0x61 read
> is **bit 3**, not bit 2. pcxtbios's `TURBO_ENABLED` build writes
> `OUT 0x61, 0xA5` very early in POST which leaves bit 2 sticky-high
> forever; only bit 3 toggles deterministically between memory and
> video+floppy reads (per PC/XT 8255 PIA Port B convention). Earlier
> CGA-only paths worked by accident. See commit `caaeb85`.

### 3. Pure-Python FAT12 builder — `tools/make_fat12_floppy.py`

Creates a 1.44 MB FAT12 floppy image from a directory of host files.
Why not `mtools`? It's not available via Windows `choco` / `winget` /
`scoop` and SourceForge GnuWin32 binaries are offline. A 200-line Python
script with no dependencies turned out cleaner than installing MSYS2 just
to get `mcopy`.

Constraints (these are intentional; lift them only if you find a use
case that needs them):
- **8.3 short names only** — no LFN dirent generation. Use `HELLO.COM`,
  not `Hello world.exe`.
- **Contiguous cluster allocation** — Gemini's hint for skipping the
  notorious FAT12 12-bit packed-pointer read-modify-write nightmare.
  Per-file the script just chains N..N+k-1 then writes 0xFFF. Fine
  unless someone wants delete-and-fragment scenarios.
- **Standard 1.44 MB geometry** — 0xF0 media byte, 2880 sectors, 18
  sec/track, 2 heads. Anything else and FreeDOS' INT 13h driver
  silently refuses to mount.

## A working AutoTester sequence end-to-end

`AutoTester` (`src/AprPc.Cli/Diagnostics/AutoTester.cs`) polls the text
framebuffer every 5 s, pattern-matches expected screens, injects
scancodes, and finally dumps + screenshots + closes the window.

The shipped `freedos-b-hello` sequence is the canonical end-to-end
demo:

| Step | Wait for                 | Inject                                                | Why                                          |
|---|----|----|---|
| 0 | `language`               | `Enter`                                                | Take default English             |
| 1 | `[Y,N]`                  | `N` then `Enter`                                       | Abort installer                  |
| 2 | `A:\>`                   | `B` then `Shift+;` (= `:`) then `Enter`                | Switch to drive B:               |
| 3 | `B:\>`                   | `H E L L O` then `Enter`                               | Run HELLO.COM                    |
| 4 | (terminal — no pattern)  | dump screen, save PNG, close window                    | Capture result for CI            |

Add new sequences in `BuildSequence()`. Each step is a `(pattern,
scancode list)`; the last step has `IsTerminal = true` and triggers
the dump + close on its first tick.

## Writing your own test

1. **Pick a name** that fits 8.3: `MYTEST.COM`, `INT21H.EXE`, etc.
2. **Write the asm** at `test-roms/x86/src/<name>.com.asm`:
   ```nasm
   bits 16
   org  0x100             ; .COM is loaded at PSP+0x100
   start:
           ; ... your code ...
           mov  si, msg
   .e9:
           lodsb
           or   al, al
           jz   .done
           out  0xE9, al
           jmp  .e9
   .done:
           mov  ah, 0x4C    ; DOS terminate
           mov  al, 0       ; exit code
           int  0x21
   msg:    db  '[MYTEST] PASS', 10, '[TEST_PASS]', 10, 0
   ```
3. **Build** with NASM at `"C:/Program Files/NASM/nasm.exe"` (see
   `[reference_nasm]` memory). Output `.COM` into
   `test-roms/x86/fat12-b/`.
4. **Rebuild the floppy** with `tools/make_fat12_floppy.py`.
5. **Run** with `--floppy-b=test-roms/x86/test-floppy-b.img` plus
   either:
   - `--auto-test=freedos-b-hello` (if your name is `HELLO`) — easiest
   - A new AutoTester sequence (a few lines in `BuildSequence`) if your
     test wants its own scripted invocation
   - Manual GUI: type `B:` Enter then your name Enter
6. **Check** `temp/port-e9.log` for your sentinel.

## Gotchas

- **Per-test rebuild**: the floppy image is committed at
  `test-roms/x86/test-floppy-b.img`. Rebuild after every `.COM`
  change or you'll keep running the stale binary. The Python builder
  is fast (< 100 ms) — wire it into your iteration loop.
- **`Shift+key` punctuation**: only works in GUI mode (handled by
  WinForms KeyDown). AutoTester uses scancode triples `(shift make,
  key make, key break, shift break)` — see the `B:` step in
  `freedos-b-hello` for the shape.
- **HLE BIOS mode**: this whole workflow is `--bios=...` real-BIOS
  only. HLE BIOS doesn't drive the FDC, so `--floppy-b` would never
  surface to DOS.
- **Test exit**: `.COM` should `INT 21h AH=4C` to return to the
  prompt cleanly. The `freedos-b-hello` AutoTester sequence assumes
  you're back at `B:\>` within 5 seconds.

## Future extensions (not done yet)

- `--auto-test-screenshot=PATH` — let test scripts pick the output PNG
  name instead of `auto-test-<timestamp>.png`.
- DOSBox-style `AUTOEXEC.BAT` fixture loop: modify A: to call
  `B:\AUTORUN.BAT` if present; then host just rebuilds B: per test and
  re-launches, no AutoTester pattern matching needed.
- `--floppy-b=DIR` shorthand that runs `make_fat12_floppy.py`
  automatically (skip the explicit rebuild step).
- vvfat-style on-the-fly FAT synthesis (no .img file at all). Heavy;
  defer until someone asks for it.
