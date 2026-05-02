; 09-loop100.s — Blargg cpu_instrs sub-test "09-op r,r" wrapped in a
; 100-iteration loop. Same display style as the original ("09-op r,r"
; banner then "Passed") plus an extra "x N" line after each iteration.
;
; Why this file is so verbose: Blargg's framework is hardcoded for one
; sub-test per cart (single global `main:` / `instrs:` / `test_instr:`
; / `checksums:` / `post_exit:`). We need our own build-target setup
; (clone of build_rom.s) so we can override `post_exit` to loop instead
; of halting. The test data + sub-test-specific helpers are pulled in
; verbatim from `source/09-op r,r.s` via 09_data.inc.

; We bypass shell.inc entirely (which would auto-include build_rom.s)
; and inline the build-target setup ourselves so we can override
; post_exit. Runtime sets RUNTIME_INCLUDED itself when first included.

;;;; ===== Build target (clone of source/common/build_rom.s) =====

.memoryMap
     defaultSlot 0
     slot 0 $0000 size $4000
     slot 1 $C000 size $4000
.endMe

.romBankSize   $4000
.romBanks      2

.cartridgeType 1                  ; MBC1
.computeChecksum
.computeComplementCheck

;;;; GB ROM header
.org $100
     nop
     jp   reset

; Nintendo logo
.byte $CE,$ED,$66,$66,$CC,$0D,$00,$0B
.byte $03,$73,$00,$83,$00,$0C,$00,$0D
.byte $00,$08,$11,$1F,$88,$89,$00,$0E
.byte $DC,$CC,$6E,$E6,$DD,$DD,$D9,$99
.byte $BB,$BB,$67,$63,$6E,$0E,$EC,$CC
.byte $DD,$DC,$99,$9F,$BB,$B9,$33,$3E

; DMG/CGB compat byte
.org $143
.byte $80

.org $200

;;;; Pull in the standard runtime + console
.incdir "../source/common"
.include "runtime.s"
.include "console.s"

;;;; Hooks the runtime expects to be defined per build target

init_runtime:
     call console_init
     print_str "09-OP R,R LOOP100",newline,newline
     ret

std_print:
     push af
     sta  SB
     wreg SC,$81
     delay 2304
     pop  af
     jp   console_print

;;;; Custom post_exit — increment counter, display, loop until 100.
;
; A = 0 means all sub-tests passed this iteration (set by tests_passed).
; A != 0 means a failure occurred — halt without looping so the user
; sees the diagnostic.
;
; Iteration counter lives in HRAM ($FFE0) so it survives the
; copy_to_wram_then_run reset (which only repopulates $C000-$CFFF).

.define iter_counter $FFE0

post_exit:
     cp   0
     jp   nz,halt_failed

     ; Bump counter (HRAM byte, zero on cold boot)
     ld   a,(iter_counter)
     inc  a
     ld   (iter_counter),a

     ; Display "x N" line under the existing "Passed" line.
     ; (print framework auto-newlines after "Passed" via tests_passed,
     ; but we add explicit positioning to keep iterations on one line)
     print_str "x "
     ld   a,(iter_counter)
     call print_dec
     call print_newline
     call console_show

     ; Loop until 100, then halt.
     ld   a,(iter_counter)
     cp   100
     jr   nc,halt_done

     ; Re-enter the test framework's main directly. Skipping `jp reset`
     ; avoids re-running init_runtime / re-printing the banner each
     ; iteration; main does its own per-iteration framework re-init
     ; (cpu_fast / init_crc_fast / checksums_init / set_test 0).
     ; Reset stack to known state in case test was mid-stack at exit.
     ld   sp,std_stack
     jp   main

halt_failed:
halt_done:
     wreg NR52,0
forever:
     jr   forever

play_byte:
     ret

.ends                ; close the "runtime" section opened by runtime.s

;;;; ===== Sub-test 09 framework + test data =====

.include "instr_test.s"

;;;; The actual 09-op r,r data (instrs table, test_instr, test, instr_done,
;;;; values table, checksums) — pulled verbatim from the original sub-test.

.include "09_data.inc"
