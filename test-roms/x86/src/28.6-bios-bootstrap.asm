; ----------------------------------------------------------------------
; Phase 28.6 — LLE BIOS bootstrap stub
;
; This is the **real 8086 code** placed at F000:E05B by PcMemoryBus
; that runs when the CPU resets and follows the far jmp at FFFF:0000.
;
; Bootstrap responsibilities (real-PC compatible):
;   1. Zero segment registers (DS = ES = SS = 0).
;   2. Set up an initial stack at SS:SP = 0:7C00 (just below the boot
;      sector landing zone; classic IBM PC convention).
;   3. INT 13h AH=02 read sector 0 of A: into 0000:7C00.
;   4. On success, JMP 0000:7C00 with DL = 00h (boot drive).
;   5. On failure, HLT.
;
; This is "LLE" in the sense that it's actual 8086 instructions running
; from the BIOS ROM area, not a host-side HLE trap. Underneath it still
; uses our HLE INT 13h (HleBios) — the boot sector load itself is
; emulated; we just removed the INT 19h trap layer above it.
;
; Assembled into PcMemoryBus.BootstrapStub byte[] via a one-shot
; nasm build. Re-running NASM should produce byte-identical output.
;
; ----------------------------------------------------------------------
        bits    16
        org     0xE05B

bootstrap_start:
        xor     ax, ax
        mov     ds, ax
        mov     es, ax
        mov     ss, ax
        mov     sp, 0x7C00              ; stack below the boot sector

        mov     ax, 0x0201              ; AH=02 (read), AL=01 (1 sector)
        mov     cx, 0x0001              ; CH=cyl 0, CL=sector 1
        mov     dx, 0x0000              ; DH=head 0, DL=drive 0 (A:)
        mov     bx, 0x7C00              ; ES:BX = 0000:7C00
        int     0x13
        jc      .fail

        mov     dl, 0                   ; DL = boot drive 0 (A:)
        jmp     0x0000:0x7C00           ; far jmp to boot sector

.fail:
        hlt
        jmp     .fail                   ; loop in case HLT returns
