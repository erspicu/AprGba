; ----------------------------------------------------------------------
; Phase 28.5 — INT 13h read sector + boot-magic check
;
; Reads sector 0 (boot sector) of drive A: into 0:7E00 via INT 13h
; AH=02, then checks the last two bytes for the 55 AA boot signature.
; Prints "OK" if magic matches (proves INT 13h is sourcing real bytes
; from the .img file), "BAD" otherwise.
;
; Run with:
;   --test-rom=test-roms/x86/28.5-int13-readback.bin
;   --floppy-a=BIOS/freedos-1.3-floppy.img
;
; The runner mounts the FreeDOS floppy as drive 0x00 and loads this
; tiny test ROM separately to 0000:7C00.
; ----------------------------------------------------------------------
        bits    16
        org     0x7C00

start:
        xor     ax, ax
        mov     ds, ax
        mov     es, ax

        ; INT 13h AH=02 read sectors
        ;   AL = sector count = 1
        ;   CH = cylinder lo = 0
        ;   CL = sector (1-indexed) = 1
        ;   DH = head = 0
        ;   DL = drive = 0 (A:)
        ;   ES:BX = buffer = 0:7E00
        mov     ax, 0x0201
        mov     cx, 0x0001
        mov     dx, 0x0000
        mov     bx, 0x7E00
        int     0x13

        jc      .bad            ; CF=1 → INT 13h failed

        ; Check magic at 0:7E00 + 510 / 511
        mov     ax, [0x7FFE]    ; little-endian word = 0xAA55
        cmp     ax, 0xAA55
        jne     .bad

        ; "OK"
        mov     bx, 0
        mov     ah, 0x0E
        mov     al, 'O'
        int     0x10
        mov     al, 'K'
        int     0x10
        hlt

.bad:
        mov     bx, 0
        mov     ah, 0x0E
        mov     al, 'B'
        int     0x10
        mov     al, 'A'
        int     0x10
        mov     al, 'D'
        int     0x10
        hlt
