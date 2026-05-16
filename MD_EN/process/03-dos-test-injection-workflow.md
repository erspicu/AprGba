# DOS test-binary injection workflow (EN mirror)

> Brief mirror of `MD/process/03-dos-test-injection-workflow.md` per
> CLAUDE.md bilingual-docs rule. See the zh-TW version for full
> walkthrough and gotchas.

## Three-layer test injection (Phase 30.14)

1. **Port 0xE9 debug-out hook** (Bochs/QEMU pattern) — `PcPortBus`
   intercepts `OUT 0xE9, AL`, buffers per-line, writes to
   `temp/port-e9.log`, mirrors to host stdout as `[E9] <line>`. Avoids
   screen-scraping for test pass/fail.
2. **`--floppy-b=PATH` second floppy mount** — keep A: FreeDOS boot
   image pristine, put your test programs on a disposable B: image.
   Equipment-word fix declares 2 drives so FreeDOS treats B: as real
   (no phantom-swap "Insert diskette for B:" prompts).
3. **`tools/make_fat12_floppy.py`** — pure-Python FAT12 1.44 MB builder.
   `mtools` is not available on Windows via `choco` / `winget` / `scoop`;
   the script generates a valid 1.44 MB image with 8.3 short names from
   a host directory. Contiguous cluster allocation only (intentional —
   sidesteps the FAT12 12-bit split-nibble nightmare).

## Workflow

```pwsh
# 1. Build .COM with NASM
"C:/Program Files/NASM/nasm.exe" -f bin \
    test-roms/x86/src/mytest.com.asm \
    -o test-roms/x86/fat12-b/MYTEST.COM

# 2. Build B: floppy image
python tools/make_fat12_floppy.py \
    --src=test-roms/x86/fat12-b \
    --out=test-roms/x86/test-floppy-b.img \
    --label="APRPCTEST"

# 3. Boot with both floppies
dotnet "src/AprPc.Cli/bin/Debug/net10.0-windows/apr-pc.dll" \
    --bios=BIOS/firmware/pcxtbios.bin \
    --video-bios=BIOS/firmware/videorom.bin \
    --floppy-a=BIOS/freedos-1.3-floppy.img \
    --floppy-b=test-roms/x86/test-floppy-b.img \
    --backend=json --video=cga \
    --auto-test=freedos-b-hello

# 4. Check results
Get-Content temp/port-e9.log
```

## Surfaced bug

Port 0x61 SW2 selector is **bit 3, not bit 2**. pcxtbios
`TURBO_ENABLED` writes `OUT 0x61, 0xA5` very early in POST which leaves
bit 2 sticky-high (it's the turbo flag, not the select line). The
PC/XT 8255 PIA Port B convention uses bit 3 for SW2 select; pcxtbios's
`OUT 0xAD` before the second `IN AL, 62h` toggles bit 3 (and bit 5)
deterministically. Earlier `--video=cga` paths worked by accident — VBIOS
load timing exposed the real bug. Fix in `PcPortBus.Read62`.

## Reference

- `MD/process/03-dos-test-injection-workflow.md` — full zh-TW walkthrough
- `MD/design/30-fdc-dma-plan.md` Phase 30.14a/b/c sprint rows
- `tools/make_fat12_floppy.py` — FAT12 builder source
- `test-roms/x86/src/30.14-hello.com.asm` — canonical .COM example
- `src/AprPc.Cli/Diagnostics/AutoTester.cs` — `freedos-b-hello` sequence
- `result/pc/auto-test-20260516-173855.png` — end-to-end pass screenshot
