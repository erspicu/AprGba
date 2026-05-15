; 29.11-fpu-integration.asm — Phase 29.11 end-to-end FPU integration test.
;
; This is the Phase 29 capstone test: a single ROM that exercises a
; realistic chain of FPU operations covering every category implemented
; in Phase 29.1-29.9:
;
;   - FNINIT (control)                                        29.3b
;   - FLD m64fp / FSTP m64fp (data movement, double precision) 29.3e
;   - FMUL ST(0), m32fp (arithmetic with mem form)             29.4
;   - FADD ST(0), ST(1) (arithmetic with register form)        29.4
;   - FSQRT (misc unary)                                       29.8
;   - FXCH (data movement, register swap)                      29.3d
;   - FCOM + FNSTSW AX (compares + status readback)            29.5
;   - FLDPI + FPATAN (constants + transcendentals)             29.3d + 29.7
;
; Computation: hypotenuse of a 3-4-5 triangle.
;   a = 3.0, b = 4.0  → √(a² + b²) = √25 = 5.0
;
; Step-by-step:
;   FLD m64fp [a]            ; ST(0) = 3.0  (loaded as f64)
;   FMUL ST(0), m32fp [x_a32]  ; ST(0) = 9.0
;   FLD m64fp [b]            ; ST(0) = 4.0, ST(1) = 9.0
;   FMUL ST(0), m32fp [x_b32]  ; ST(0) = 16.0, ST(1) = 9.0
;   FADD ST(0), ST(1)        ; ST(0) = 25.0, ST(1) = 9.0      ← reg-form arith
;   FXCH ST(1)               ; ST(0) = 9.0, ST(1) = 25.0
;   FXCH ST(1)               ; ST(0) = 25.0, ST(1) = 9.0       (back)
;   FSQRT                    ; ST(0) = 5.0
;   FSTP m64fp [hypot]       ; pop ST(0) to memory; ST(0) = 9.0 (leftover)
;
;   FCOM m32fp [val_5]       ; compare ST(0)=9.0 vs 5.0 → C0=0 C3=0 (greater)
;   FNSTSW AX                ; AX = FPU_SW (low 8 bits should be 0)
;
;   FLDPI                    ; push π — ST(0)=π, ST(1)=9.0
;   FLDPI                    ; push π again; ST(0)=π, ST(1)=π, ST(2)=9.0
;   FPATAN                   ; atan2(ST(1)=π, ST(0)=π) → ST(1), pop → ST(0)=π/4
;   FSTP m32fp [atan_result] ; pop π/4 ≈ 0.7854 as f32 → 0x3F490FDB
;
; Verification (high WORDs to GPRs):
;   AX = (FNSTSW AX result, low half of FPU_SW)        expect 0x0000 (ST(0)=9.0 > 5.0)
;   BX = WORD [hypot + 6]    (high 16 bits of f64 5.0)  expect 0x4014
;       (since 5.0 as f64 = 0x4014000000000000)
;   CX = WORD [atan_result + 2]   (high WORD of atan2(π,π) f32) expect 0x3F49
;   DX = WORD [hypot]        (low 16 bits of f64 5.0)   expect 0x0000

bits 16
org 0x0100

start:
    ; ===== Compute hypotenuse √(3² + 4²) = 5.0 =====
    db 0xDB, 0xE3                       ; FNINIT
    db 0xDD, 0x06                       ; FLD m64fp [x_a64]   modrm = 00_000_110, /0
    dw x_a64
    db 0xD8, 0x0E                       ; FMUL m32fp [x_a32]  modrm = 00_001_110, /1
    dw x_a32
    db 0xDD, 0x06                       ; FLD m64fp [x_b64]
    dw x_b64
    db 0xD8, 0x0E                       ; FMUL m32fp [x_b32]
    dw x_b32
    db 0xD8, 0xC1                       ; FADD ST(0), ST(1)  (D8 /0 mod=11 rm=1 = C0|1=C1)
    db 0xD9, 0xC9                       ; FXCH ST(1)         (D9 C8+1 = C9)
    db 0xD9, 0xC9                       ; FXCH ST(1) (swap back)
    db 0xD9, 0xFA                       ; FSQRT
    db 0xDD, 0x1E                       ; FSTP m64fp [hypot]  modrm = 00_011_110, /3
    dw hypot

    ; ===== Compare ST(0)=9.0 (leftover) vs 5.0 =====
    db 0xD8, 0x16                       ; FCOM m32fp [val_5]
    dw val_5
    db 0xDF, 0xE0                       ; FNSTSW AX
    mov [sw_result], ax

    ; ===== Compute atan2(π, π) = π/4 =====
    db 0xDB, 0xE3                       ; FNINIT (reset stack)
    db 0xD9, 0xEB                       ; FLDPI
    db 0xD9, 0xEB                       ; FLDPI again
    db 0xD9, 0xF3                       ; FPATAN
    db 0xD9, 0x1E                       ; FSTP m32fp [atan_result]
    dw atan_result

    ; ===== Verification — load results into GPRs =====
    mov ax, [sw_result]                 ; expect 0x0000 (C0=0 C3=0, ST(0)=9 > 5)
    mov bx, [hypot + 6]                 ; expect 0x4014 (high word of f64 5.0)
    mov cx, [atan_result + 2]           ; expect 0x3F49 (high word of f32 π/4)
    mov dx, [hypot]                     ; expect 0x0000 (low word of f64 5.0)
    hlt

; ---- Data ----
x_a64:        dq 3.0                     ; 3.0 as f64 = 0x4008000000000000
x_a32:        dd 3.0                     ; 3.0 as f32 = 0x40400000
x_b64:        dq 4.0                     ; 4.0 as f64 = 0x4010000000000000
x_b32:        dd 4.0                     ; 4.0 as f32 = 0x40800000
val_5:      dd 5.0                     ; 5.0 as f32 = 0x40A00000
hypot:      dq 0xDEADBEEFDEADBEEF      ; will be overwritten by FSTP m64fp
sw_result:  dw 0xCAFE
atan_result: dd 0xDEADBEEF
