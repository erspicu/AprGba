; ----------------------------------------------------------------------
; Phase 28.2 — INT 10h AH=0Eh teletype output via HLE BIOS
;
; Print "Hi AprPc!" using INT 10h teletype, then halt.
; The HLE BIOS converts the AL byte into CGA framebuffer writes at
; 0xB8000 and advances the cursor; the headless --screenshot path
; pulls B800 → PNG via X86CgaRenderer.
;
; Loaded at 0000:7C00 by HeadlessRunner --floppy-a=.
; Final CPU state: CS:IP just past HLT.
; ----------------------------------------------------------------------
        bits    16
        org     0x7C00

start:
        xor     ax, ax
        mov     ds, ax
        mov     bx, 0           ; BH = page 0, BL = (ignored by 0Eh)

        ; "Hi AprPc!" — 9 chars
        mov     ah, 0x0E
        mov     al, 'H'
        int     0x10
        mov     al, 'i'
        int     0x10
        mov     al, ' '
        int     0x10
        mov     al, 'A'
        int     0x10
        mov     al, 'p'
        int     0x10
        mov     al, 'r'
        int     0x10
        mov     al, 'P'
        int     0x10
        mov     al, 'c'
        int     0x10
        mov     al, '!'
        int     0x10

        hlt
