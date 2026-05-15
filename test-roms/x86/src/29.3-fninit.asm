; 29.3-fninit.asm — Phase 29.3b minimum FPU semantic test.
;
; Runs FNINIT (DB E3) once, then halts. Verification is via apr-x86's
; --dump-fpu-state output, NOT via x87 state read-back (FNSTSW AX is a
; later sprint — DF E0, Phase 29.5).
;
; Expected post-run FPU state (per Intel 8087/80287 init defaults):
;   FPU_CW   = 0x037F   (all 6 exceptions masked, 64-bit precision,
;                        round-to-nearest, projective infinity)
;   FPU_SW   = 0x0000   (no condition codes, TOP_SW=0, not busy)
;   FPU_TAGS = 0xFFFF   (all 8 ST slots tagged 11 = Empty)
;   FPU_TOP  = 0x00     (top-of-stack index)
;
; Build:  nasm -f bin -o test-roms/x86/29.3-fninit.com 29.3-fninit.asm
;
; Run:    apr-x86 --rom=test-roms/x86/29.3-fninit.com --enable-i8087 \
;             --backend=json --dump-fpu-state --max-cycles=100

bits 16
org 0x0100

    db 0xDB, 0xE3       ; FNINIT — reset 8087/80287 to power-on state
    hlt                 ; F4 — let apr-x86 see Halted=true and exit
