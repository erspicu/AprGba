# SeaBIOS — local reference snapshot (NOT YET USED)

**Upstream**: https://github.com/coreboot/seabios (read-only mirror of https://git.seabios.org/seabios.git)
**License**: LGPLv3 (`COPYING.LESSER`) + GPLv3 (`COPYING`)
**Snapshot date**: 2026-05-16, Phase 30.8 deferred work.

## What is this and why is it here

[SeaBIOS](https://www.seabios.org/) is the gold-standard open-source PC BIOS used
by **QEMU**, **Bochs**, **KVM**, **coreboot**, and others. It correctly
implements `INT 10h` video services, supports MDA/CGA/EGA/VGA, ACPI, PCI bus,
etc. — a much fuller implementation than `pcxtbios.bin` which is a minimal XT BIOS.

It is **kept here as a reference / future option** but **NOT actively used** by
the emulator. The current real-BIOS path uses `BIOS/firmware/pcxtbios.bin`
(see `ref/pcxtbios/`).

## Why we keep it as a reference

While debugging MDA video issues we discovered that pcxtbios has subtle bugs in
its `INT 10h AH=0Eh` teletype scroll path that cause black-on-black rendering
in some text-mode scrolling scenarios (Phase 30.8 deferred). Mature emulators
solve this problem one of three ways:

1. **Use authentic IBM BIOS ROMs** — proprietary, can't redistribute.
2. **Use SeaBIOS** — open source, mature, no quirks. Done by QEMU + Bochs.
3. **HLE intercept** `INT 10h` — done by DOSBox / DOSBox-X.

If we ever want option 2, SeaBIOS source is right here. Reading the SeaBIOS
`src/output.c` + `src/disk.c` + `vgasrc/` is also a great cross-reference
when in doubt about pcxtbios behaviour — SeaBIOS comments are extensive.

## Why we're NOT using it now (Phase 30.x deferred)

- SeaBIOS is **256 KB**, expects modern PC features (ACPI tables, PCI bus,
  proper RTC, possibly E820 memory map). Our emulator currently emulates a
  minimal IBM XT (PIC + PIT + 8042 + FDC + DMA only). Wiring SeaBIOS would
  need substantial additional peripheral emulation.
- pcxtbios.bin (8 KB) matches our XT-class emulation well and boots FreeDOS
  to interactive A:\> end-to-end (see Phase 30.7a closure note).
- The text-mode scroll bug is cosmetic only; CGA mode works fine; MDA mode
  has the workaround documented in `MD/ref/pcxtbios-device-spec.md`.

## How to build (if we ever do)

```
cd ref/seabios
make help         # show build targets
make              # default produces out/bios.bin
```

Build prereqs: gcc (or i686-elf-gcc cross), GNU make, python, iasl (Intel
ACPI compiler) for ACPI table compilation. On Windows MSYS2 / WSL works.

Default config builds the **QEMU** target — not directly usable on bare XT.
For an XT-class build, see `src/Kconfig` for options to disable PCI / ACPI /
USB / SMP. May still need substantial trimming.

## Companion docs

- `ref/pcxtbios/` — the BIOS we currently use, source + binary + README
- `MD/ref/pcxtbios-device-spec.md` — device-by-device handbook
- `MD/design/30-fdc-dma-plan.md` — Phase 30 implementation plan + deferred work

## Updating the snapshot

```
cd ref
rm -rf seabios
git clone --depth 1 https://github.com/coreboot/seabios.git
cd seabios && rm -rf .git
```

Pin the commit / date in this README's "Snapshot date" line above.
