; 30-rol-cl-test.asm — Phase 30.6c test: ROL r16, CL with count > 1
;
; ROL AX, CL with AX=0x1FE0 and CL=4 should produce 0xFE01.
; Before this fix, our emitter delegated to count=1 stub → produced 0x3FC0.

bits 16
org 0x0100

start:
    mov  ax, 0x1FE0
    mov  cl, 4
    rol  ax, cl       ; should be 0xFE01
    mov  bx, ax       ; save result in BX for verification

    mov  ax, 0xC123
    mov  cl, 8
    rol  ax, cl       ; should be 0x23C1
    mov  cx, ax

    mov  ax, 0x0001
    mov  cl, 15
    rol  ax, cl       ; should be 0x8000
    mov  dx, ax

    mov  ax, 0xFFFF
    mov  cl, 4
    rol  ax, cl       ; should be 0xFFFF (all bits set, rotation preserves)
    mov  si, ax

    hlt
