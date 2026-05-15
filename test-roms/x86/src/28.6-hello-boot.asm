; ----------------------------------------------------------------------
; Phase 28.6 — self-written boot sector demo
;
; Lands at 0000:7C00 after the LLE BIOS bootstrap loads it from
; sector 0 of drive 0 (A:). Prints "AprPc bootstrap OK" via INT 10h
; teletype, then halts.
;
; This is the **first PC-style boot demo** for AprPc: CPU reset →
; BIOS ROM far JMP → real-8086 bootstrap @ F000:E05B → INT 13h read
; → JMP to this boot sector → INT 10h teletype.
;
; ----------------------------------------------------------------------
        bits    16
        org     0x7C00

start:
        ; DS / ES are already 0 (set by LLE bootstrap), but keep
        ; explicit init in case a future bootstrap deviates.
        xor     ax, ax
        mov     ds, ax
        mov     es, ax

        ; Set up cursor at top-left.
        mov     ah, 0x02
        mov     bh, 0       ; page 0
        mov     dh, 0       ; row 0
        mov     dl, 0       ; col 0
        int     0x10

        ; Print message string via INT 10h teletype.
        mov     si, msg
.loop:
        lodsb               ; AL = DS:[SI], SI++
        or      al, al
        jz      .done
        mov     ah, 0x0E
        mov     bx, 0x0007  ; BH = page 0, BL = attr 7 (light grey)
        int     0x10
        jmp     .loop

.done:
        hlt
        jmp     .done       ; loop on wake

msg:    db      "AprPc bootstrap OK", 13, 10, 0

; -------- pad to 510 + boot magic 55 AA -----------------------------
        times 510 - ($ - $$) db 0
        dw 0xAA55
