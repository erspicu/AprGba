; ----------------------------------------------------------------------
; Phase 28.7 — PIT IRQ 0 delivery via user-installed INT 8 hook
;
; Test program:
;   1. Install a custom INT 8 handler at 0:0500 — this handler
;      increments a counter at 0:0600 (word) and IRETs.
;   2. Patch IVT[8] to point at 0:0500.
;   3. STI to enable interrupts; wait via a long spin until the
;      counter reaches a threshold.
;   4. Print "OK <counter-hex>" via INT 10h teletype and HLT.
;
; If IRQ delivery works, the PIT (55ms cadence) will assert IRQ 0,
; the PIC will dequeue it, the emulator thread will push FLAGS+CS+IP
; and jump to IVT[8] = 0:0500, our custom handler increments the
; counter, and we eventually see counter >= threshold.
;
; If IRQ delivery is broken, the counter stays at 0 → "TIMEOUT".
;
; ----------------------------------------------------------------------
        bits    16
        org     0x7C00

start:
        cli                     ; disable interrupts while we set up
        xor     ax, ax
        mov     ds, ax
        mov     es, ax
        mov     ss, ax
        mov     sp, 0x7C00       ; stack below the boot sector

        ; Install handler bytes at 0:0500.
        mov     di, 0x0500
        mov     si, handler
        mov     cx, handler_end - handler
        cld
        rep movsb

        ; Counter at 0:0600 starts at 0.
        mov     word [0x0600], 0

        ; Set IVT[8] = 0000:0500.
        mov     word [0x0020], 0x0500   ; IP
        mov     word [0x0022], 0x0000   ; CS

        sti                     ; enable interrupts

        ; Spin until counter reaches 3 (or timeout via outer loop).
        mov     cx, 0xFFFF
.spin_outer:
        mov     bx, 0xFFFF
.spin_inner:
        mov     ax, [0x0600]
        cmp     ax, 3
        jge     .done
        dec     bx
        jnz     .spin_inner
        dec     cx
        jnz     .spin_outer

        ; Timed out without seeing 3 IRQs — print "TIMEOUT".
        cli
        mov     si, msg_timeout
        call    print_str
        hlt

.done:
        cli
        mov     si, msg_ok
        call    print_str

        ; Print counter as one ASCII hex digit (0-F).
        mov     ah, 0x0E
        mov     bx, 0
        mov     al, [0x0600]
        cmp     al, 10
        jb      .lt10
        add     al, 'A' - 10
        jmp     .printal
.lt10:
        add     al, '0'
.printal:
        int     0x10

        hlt

; ---- print_str: print null-terminated DS:SI via INT 10h AH=0Eh ------
print_str:
        mov     ah, 0x0E
        mov     bx, 0
.ploop:
        lodsb
        or      al, al
        jz      .pdone
        int     0x10
        jmp     .ploop
.pdone:
        ret

msg_ok:      db "IRQ OK ", 0
msg_timeout: db "TIMEOUT", 0

; ---- handler payload (copied to 0:0500 by start) -------------------
handler:
        push    ax
        mov     ax, [0x0600]
        inc     ax
        mov     [0x0600], ax
        pop     ax
        iret
handler_end:

; -------- pad to 510 + boot magic -----------------------------------
        times 510 - ($ - $$) db 0
        dw 0xAA55
