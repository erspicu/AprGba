; 29.3-fpu-roundtrip.asm — Phase 29.3c roundtrip proof.
;
; Sequence:
;   FNINIT                  ; reset FPU
;   FLDZ                    ; push +0.0 to ST(0) (TOP=7)
;   FSTP DWORD [scratch]    ; pop ST(0) as f32 to memory (TOP=0,
;                           ;  [scratch] should now be 0x00000000)
;   MOV AX, [scratch]       ; read back low 16 bits
;   MOV BX, [scratch+2]     ; read back high 16 bits
;   HLT
;
; Verification: apr-x86 final state must show AX=0000 BX=0000
; (scratch pre-filled with 0xDEADBEEF, so if FSTP failed to write
; we'd see AX=BEEF BX=DEAD instead).
;
; Plus --dump-fpu-state should show TOP=00 TAGS=FFFF (FLDZ tagged
; slot 7 as Zero, FSTP cleared it back to Empty).

bits 16
org 0x0100

start:
    db 0xDB, 0xE3           ; FNINIT
    db 0xD9, 0xEE           ; FLDZ
    db 0xD9, 0x1E           ; FSTP DWORD [disp16] — modrm = 00_011_110
    dw scratch              ; disp16 = absolute offset within DS
    mov ax, [scratch]
    mov bx, [scratch+2]
    hlt

scratch:
    dd 0xDEADBEEF
