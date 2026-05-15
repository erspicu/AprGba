; 30-rep-movsw-test.asm — Phase 30 debug: standalone REP MOVSW correctness.
;
; Sets up known data at offset 0x300 (DS-relative), copies 16 bytes via
; REP MOVSW to offset 0x400, reads back via MOV → verify in AX/BX.
;
; Expected after copy: bytes at offset 0x400-0x40F == 0xCAFEBABE...

bits 16
org 0x0100

start:
    ; Set ES = DS so we don't have segment issues
    push ds
    pop  es

    ; Setup source data at offset 0x300 (data segment relative)
    mov  word [src+0],  0xCAFE
    mov  word [src+2],  0xBABE
    mov  word [src+4],  0xDEAD
    mov  word [src+6],  0xBEEF
    mov  word [src+8],  0x1234
    mov  word [src+10], 0x5678
    mov  word [src+12], 0x9ABC
    mov  word [src+14], 0xDEF0

    ; REP MOVSW from src to dst (16 bytes = 8 words)
    cld
    mov  si, src
    mov  di, dst
    mov  cx, 8
    rep  movsw

    ; Read back into GPRs for verification
    mov  ax, [dst+0]    ; should be 0xCAFE
    mov  bx, [dst+2]    ; should be 0xBABE
    mov  cx, [dst+4]    ; should be 0xDEAD
    mov  dx, [dst+6]    ; should be 0xBEEF
    mov  si, [dst+8]    ; should be 0x1234
    mov  di, [dst+10]   ; should be 0x5678
    mov  bp, [dst+12]   ; should be 0x9ABC
    hlt

src: times 16 db 0
dst: times 16 db 0
