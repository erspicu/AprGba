; ----------------------------------------------------------------------
; Phase 28.4 — INT 1Ah tick readback
;
; Reads the BIOS tick counter, prints "OK" if INT 1Ah returns
; non-zero (proving the wall-clock PIT is ticking), else prints
; "0!". Halts.
;
; In practice the runner starts the PIT timer at Start(), keystrokes
; are pre-injected before Resume, the test ROM begins at 0000:7C00,
; and even just the program startup overhead is enough that 1 timer
; tick has fired by the time we read.
;
; If you want to see multiple ticks, run with --max-cycles=10000000
; and a spin loop variant; this minimum-viable test only proves the
; INT 1Ah HLE wiring + PcPit BDA write are alive.
;
; ----------------------------------------------------------------------
        bits    16
        org     0x7C00

start:
        xor     ax, ax
        mov     ds, ax
        mov     bx, 0           ; INT 10h page = 0

        ; Read INT 1Ah AH=00.
        mov     ah, 0x00
        int     0x1A            ; CX:DX = tick, AL = midnight flag

        ; Store DX (tick low) into AX for inspection.
        mov     ax, dx

        ; If DX == 0 print '0', else print 'O'.
        ; (DX is the low word — even if high word non-zero, low can be 0
        ; on a very-fast first read; but with 55 ms cadence vs ~ms boot,
        ; low word is almost always non-zero on second/later runs.)
        cmp     dx, 0
        je      .zero
        mov     al, 'O'
        jmp     .print
.zero:
        mov     al, '0'
.print:
        push    ax              ; preserve AL
        mov     ah, 0x0E
        int     0x10

        ; Second char: 'K' if zero flag was clear, '!' if set.
        ; We already overwrote flags via INT 10h — recompute.
        pop     ax              ; restore AL = 'O' or '0'
        cmp     al, 'O'
        je      .printK
        mov     al, '!'
        jmp     .print2
.printK:
        mov     al, 'K'
.print2:
        mov     ah, 0x0E
        int     0x10

        hlt
