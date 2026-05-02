; thumb-loop100.asm — jsmolka thumb.asm wrapped in a 100-iteration loop.
; Mirrors arm/arm-loop100.asm exactly; see that file for design notes.

format binary as 'gba'

include '../lib/constants.inc'
include '../lib/macros.inc'

ITER_ADDR equ 0x03007FE0

macro m_exit test {
        mov     r7, test
        bl      tmain_end
}

header:
        include '../lib/header.asm'

main:
        m_test_init

        adr     r0, tmain + 1
        bx      r0

code16
align 2
tmain:
        ; Reset test register
        mov     r7, 0

        ; Tests start at 1
        include 'logical.asm'
        ; Tests start at 50
        include 'shifts.asm'
        ; Tests start at 100
        include 'arithmetic.asm'
        ; Tests start at 150
        include 'branches.asm'
        ; Tests start at 200
        include 'memory.asm'

tmain_end:
        adr     r0, eval
        bx      r0

code32
align 4
eval:
        m_vsync
        cmp     r7, 0
        bne     eval_failed

        ; Iteration counter ++ in IWRAM (zeroed by BIOS / HLE init).
        m_word  r4, ITER_ADDR
        ldr     r5, [r4]
        add     r5, 1
        str     r5, [r4]

        ; Line 1: "All tests passed"
        m_text_pos 56, 76
        m_text_char 'A'
        m_text_char 'l'
        m_text_char 'l'
        m_text_char ' '
        m_text_char 't'
        m_text_char 'e'
        m_text_char 's'
        m_text_char 't'
        m_text_char 's'
        m_text_char ' '
        m_text_char 'p'
        m_text_char 'a'
        m_text_char 's'
        m_text_char 's'
        m_text_char 'e'
        m_text_char 'd'

        ; Line 2: "x NNN" — pure-ARM divmod (no SWI dependency).
        mov     r6, r5
        mov     r10, 0
.h_loop:
        cmp     r6, 100
        blt     .h_done
        sub     r6, 100
        add     r10, 1
        b       .h_loop
.h_done:
        mov     r9, 0
.t_loop:
        cmp     r6, 10
        blt     .t_done
        sub     r6, 10
        add     r9, 1
        b       .t_loop
.t_done:
        mov     r11, MEM_IWRAM
        str     r10, [r11]        ; hundreds
        str     r9,  [r11, 4]     ; tens
        str     r6,  [r11, 8]     ; ones

        m_text_pos 88, 88
        m_text_char 'x'
        m_text_char ' '
        ldr     r2, [r11]
        add     r2, 48
        bl      text_char
        ldr     r2, [r11, 4]
        add     r2, 48
        bl      text_char
        ldr     r2, [r11, 8]
        add     r2, 48
        bl      text_char

        cmp     r5, 100
        blt     main

halt_done:
        b       halt_done

eval_failed:
        m_test_eval r7

idle:
        b       idle

include '../lib/text.asm'
