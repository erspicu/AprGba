; ----------------------------------------------------------------------
; Phase 28.3 — INT 16h read-and-echo
;
; Loop: read keystroke via INT 16h AH=00, echo via INT 10h AH=0E,
; stop on ESC (AL=0x1B). With --keys="hi!\e", expected screen:
;   "hi!" in the top-left, then halt.
;
; Loaded at 0000:7C00 by HeadlessRunner --floppy-a=.
; ----------------------------------------------------------------------
        bits    16
        org     0x7C00

start:
        xor     ax, ax
        mov     ds, ax
        mov     bx, 0          ; page 0

.loop:
        mov     ah, 0x00
        int     0x16            ; AL = ascii, AH = scancode
        cmp     al, 0x1B        ; ESC?
        je      .done

        mov     ah, 0x0E
        int     0x10            ; teletype echo
        jmp     .loop

.done:
        hlt
