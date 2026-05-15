; 29.3d-fpu-suite.asm — Phase 29.3d coverage test.
;
; Three sub-tests in one ROM, separated by checkpoints. Each writes a
; canonical pattern into a memory slot so apr-x86's final-state dump
; can verify all three at once.
;
; Test 1 — FLDPI + FSTP m32 (push constant, store as f32).
;   FNINIT; FLDPI; FSTP DWORD [out_pi]
;   Expected: out_pi bytes = 0xDB 0x0F 0x49 0x40  (IEEE 754 f32 of π)
;   Note: 0x40490FDB not 0x40490FDA — LLVM's f64→f32 narrowing uses
;   round-half-to-even on the 24th mantissa bit. π's f64 representation
;   has the 25th bit and beyond non-zero, so the 24th rounds UP to DB.
;
; Test 2 — FLD m32 + FSTP m32 (roundtrip from memory).
;   FLD DWORD [in_pi]; FSTP DWORD [out_pi2]
;   Expected: out_pi2 == in_pi (byte-identical f32 roundtrip)
;   in_pi pre-filled with 0xDA 0x0F 0x49 0x40 = 3.1415927f
;
; Test 3 — FXCH ST(1) (swap stack slots).
;   FLDZ; FLD1; FXCH ST(1); FSTP DWORD [out_first]; FSTP DWORD [out_second]
;   After FLDZ+FLD1: ST(0)=1.0, ST(1)=0.0
;   After FXCH ST(1): ST(0)=0.0, ST(1)=1.0
;   After first FSTP: ST(0) was 0.0  → out_first  = 0x00000000
;   After second FSTP: ST(0) was 1.0 → out_second = 0x3F800000 (IEEE 754 f32 of 1.0)
;
; Final verification (loaded into GPRs for apr-x86 to print):
;   AX = WORD [out_pi]        (expected: 0x0FDB)
;   BX = WORD [out_pi+2]      (expected: 0x4049)
;   CX = WORD [out_second+2]  (expected: 0x3F80)
;   DX = WORD [out_first]     (expected: 0x0000)
;   SI = WORD [out_pi2]       (expected: 0x0FDA — FLD/FSTP roundtrip)
;   DI = WORD [out_pi2+2]     (expected: 0x4049)
;
; All 6 GPRs being correct = all three opcodes (FLDPI, FLD m32, FSTP m32,
; FXCH ST(i), FLD1, FLDZ) wired correctly.

bits 16
org 0x0100

start:
    ; ===== Test 1: FLDPI -> FSTP m32 =====
    db 0xDB, 0xE3                       ; FNINIT
    db 0xD9, 0xEB                       ; FLDPI
    db 0xD9, 0x1E                       ; FSTP DWORD [disp16]
    dw out_pi

    ; ===== Test 2: FLD m32 -> FSTP m32 (memory roundtrip) =====
    db 0xD9, 0x06                       ; FLD DWORD [disp16] (modrm = 00_000_110, /0)
    dw in_pi
    db 0xD9, 0x1E                       ; FSTP DWORD [disp16]
    dw out_pi2

    ; ===== Test 3: FLDZ + FLD1 + FXCH ST(1) + two FSTP =====
    db 0xD9, 0xEE                       ; FLDZ              ST(0)=0.0
    db 0xD9, 0xE8                       ; FLD1              ST(0)=1.0  ST(1)=0.0
    db 0xD9, 0xC9                       ; FXCH ST(1)        ST(0)=0.0  ST(1)=1.0
    db 0xD9, 0x1E                       ; FSTP DWORD [disp16]
    dw out_first                        ;                    ST(0) was 0.0
    db 0xD9, 0x1E                       ; FSTP DWORD [disp16]
    dw out_second                       ;                    ST(0) was 1.0

    ; ===== Verification — load each scratch into GPRs =====
    mov ax, [out_pi]
    mov bx, [out_pi + 2]
    mov cx, [out_second + 2]
    mov dx, [out_first]
    mov si, [out_pi2]
    mov di, [out_pi2 + 2]
    hlt

; ---- Data ----
in_pi:      dd 0x40490FDA              ; 3.1415927f (memory source for Test 2)
out_pi:     dd 0xDEADBEEF              ; will be overwritten by Test 1
out_pi2:    dd 0xCAFEF00D              ; will be overwritten by Test 2
out_first:  dd 0xAA55AA55              ; will be overwritten by Test 3 first FSTP
out_second: dd 0x55AA55AA              ; will be overwritten by Test 3 second FSTP
